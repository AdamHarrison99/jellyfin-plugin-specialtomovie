using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

// Adds the client script tag to the web app's index.html as it is served.
// ! Additive and fails open; every early return here passes the response through untouched.
public class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/SpecialToMovie/ClientScript";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // ! Registered ahead of the pipeline to run outermost, which is what makes
            // the Accept-Encoding strip below yield a readable body.
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

        // A suffix match, not equality: a base-URL install still carries its prefix here.
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    // ! The result comes off the request line and lands in an HTML attribute, so anything
    // that is not a plain path is discarded. See agentic/ARCHITECTURE.md.
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

        // The rewritten body invalidates the static file's validators, and it has no ranges.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");

        // ! With the validators gone, a heuristically cached copy predating the install
        // would keep the tag out of the page. See agentic/ARCHITECTURE.md.
        context.Response.Headers.CacheControl = "no-cache, must-revalidate";

        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }
}
