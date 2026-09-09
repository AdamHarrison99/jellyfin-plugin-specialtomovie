using System.Net.Mime;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpecialToMovie.Api;

// ! The plugin's only anonymous route: the browser fetches it before anyone signs in.
// It serves a fixed embedded asset and reflects nothing from the request.
[ApiController]
[Route("SpecialToMovie")]
[AllowAnonymous]
public class ClientScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.SpecialToMovie.Web.specialtomovie.js";

    private static readonly string EntityTag =
        $"\"{typeof(ClientScriptController).Assembly.GetName().Version?.ToString() ?? "0"}\"";

    // An embedded asset cannot change while the process runs; read it once.
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

        // Version-stamped: a plugin upgrade invalidates any cached copy.
        Response.Headers.ETag = EntityTag;
        Response.Headers.CacheControl = "no-cache";

        // ! MVC does not act on an ETag by itself; without this the header is decorative.
        if (Request.Headers.IfNoneMatch.Contains(EntityTag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(script, MediaTypeNames.Text.JavaScript, Encoding.UTF8);
    }
}
