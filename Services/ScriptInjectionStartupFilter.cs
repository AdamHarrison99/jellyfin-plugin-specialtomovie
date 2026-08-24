using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

/// <summary>
/// Injects the plugin's client script tag into the web app's index.html as it is served.
/// </summary>
/// <remarks>
/// Jellyfin offers plugins no hook for adding a script to the web client, and writing into the web
/// folder on disk needs a writable install and is undone by every jellyfin-web update. Rewriting the
/// response instead keeps the change self-contained.
///
/// The filter is deliberately additive and fails open: any unexpected condition results in the
/// original response being served untouched. It is enabled by default, so that property is what
/// makes it safe — every early return below is a case where the response is passed through.
/// </remarks>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/SpecialToMovie/ClientScript";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered ahead of the rest of the pipeline so this runs outermost; stripping
            // Accept-Encoding below then reliably yields a response body we can read.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // EndsWith rather than equality so this stays correct when the server is hosted under a
        // base-URL prefix.
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the base-URL prefix the web app is being served under, or an empty string.
    /// </summary>
    /// <remarks>
    /// This middleware is registered outermost, ahead of the <c>Map</c> the server wraps its whole
    /// pipeline in, so a base-URL install still carries its prefix on
    /// <see cref="HttpRequest.Path"/> and <see cref="HttpRequest.PathBase"/> is empty — which is why
    /// <see cref="IsIndexRequest"/> matches on a suffix. The script tag has to carry the same
    /// prefix: a root-relative <c>src</c> would send the browser to a path the server does not
    /// serve, and the enhancement would silently never load.
    /// <para>
    /// The value comes off the request line and is written into an HTML attribute, so anything that
    /// is not a plain path is discarded rather than escaped. Without that check a request whose
    /// path merely ends in <c>/web/index.html</c> could reflect markup into the served page.
    /// </para>
    /// </remarks>
    /// <param name="path">The matched request path.</param>
    /// <returns>A prefix starting with '/', or an empty string.</returns>
    private static string GetBasePrefix(string path)
    {
        var webIndex = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        if (webIndex <= 0)
        {
            return string.Empty;
        }

        var prefix = path[..webIndex];
        if (prefix.Contains("..", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        foreach (var c in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '/' && c != '-' && c != '_' && c != '.' && c != '~')
            {
                return string.Empty;
            }
        }

        return prefix;
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!IsIndexRequest(context.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Only a GET has a body worth rewriting. Buffering a HEAD would produce a Content-Length
        // that does not match what the host intends to send.
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.ShowCrossLinks || !config.InjectClientScript)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Normalise the request so the static file handler returns a complete, uncompressed 200:
        // a compressed or partial response cannot be rewritten correctly.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            // Not ours to swallow. The buffered bytes never reached the client, so restoring the
            // real stream and rethrowing lets the host render its own error response.
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == 200 &&
                     (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            // 304, redirects, anything not HTML: pass through byte for byte.
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            var alreadyInjected = html.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase);
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (!alreadyInjected && bodyClose >= 0)
            {
                var prefix = GetBasePrefix(context.Request.Path.Value ?? string.Empty);
                var tag = $"<script src=\"{prefix}{ScriptPath}\" defer></script>";
                html = html[..bodyClose] + tag + "\n" + html[bodyClose..];

                if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Injected the SpecialToMovie client script into the web app");
                }
            }
        }
        catch (Exception ex)
        {
            // Never break the web app: serve whatever we have.
            _logger.LogWarning(ex, "Script injection failed, serving the original page");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // The body no longer matches the static file, so its validators must not be reused, and
        // range requests are not supported on the rewritten document.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");

        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }
}
