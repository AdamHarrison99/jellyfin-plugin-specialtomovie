using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SpecialToMovie.Providers;

// Builds the URL for a cross-link button, and the only part of the feature in the DI container.
// See agentic/ARCHITECTURE.md, "Cross-link buttons".
public class CrossLinkUrlResolver
{
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CrossLinkUrlResolver(IServerApplicationHost appHost, IHttpContextAccessor httpContextAccessor)
    {
        _appHost = appHost;
        _httpContextAccessor = httpContextAccessor;
    }

    public string Resolve(Guid targetId, char marker)
    {
        var route = CrossLinkUrlBuilder.Details(targetId, _appHost.SystemId, marker);

        // No ambient request: a scheduled task or a session message, not a client.
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null)
        {
            return route;
        }

        var basis = _appHost.GetSmartApiUrl(request);

        // ! GetSmartApiUrl can take the host from the request, and this lands in an href.
        if (!Uri.TryCreate(basis, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return route;
        }

        // ! The base carries any configured base-URL path already, and the route starts with '#'.
        return $"{basis.TrimEnd('/')}/web/{route}";
    }
}
