using System.Net.Mime;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpecialToMovie.Api;

/// <summary>
/// Serves the detail-page client script.
/// </summary>
/// <remarks>
/// This is the plugin's only anonymous route. The browser requests it as part of loading the web
/// app, before anyone has signed in, so it cannot require authentication. It returns a fixed
/// embedded asset and reflects nothing from the request.
/// </remarks>
[ApiController]
[Route("SpecialToMovie")]
[AllowAnonymous]
public class ClientScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.SpecialToMovie.Web.specialtomovie.js";

    private static readonly string EntityTag =
        $"\"{typeof(ClientScriptController).Assembly.GetName().Version?.ToString() ?? "0"}\"";

    // The asset is embedded in the assembly and cannot change while the process is running, so it
    // is read once rather than on every page load of the web app.
    private static readonly Lazy<string?> Script = new(() =>
    {
        using var stream = typeof(ClientScriptController).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Gets the client script.
    /// </summary>
    /// <returns>The script, 304 if the caller already has it, or 404 if it is not embedded.</returns>
    [HttpGet("ClientScript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClientScript()
    {
        var script = Script.Value;
        if (script == null)
        {
            return NotFound();
        }

        // Version-stamped, so upgrading the plugin invalidates any cached copy. Paired with
        // no-cache, which asks the browser to revalidate rather than to stop caching: the common
        // case then costs a conditional request instead of the whole body on every page load.
        Response.Headers.ETag = EntityTag;
        Response.Headers.CacheControl = "no-cache";

        // MVC does not act on an ETag by itself, so the 304 has to be returned here or the header
        // is decorative and every request still carries the full script.
        if (Request.Headers.IfNoneMatch.Contains(EntityTag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(script, MediaTypeNames.Text.JavaScript, Encoding.UTF8);
    }
}
