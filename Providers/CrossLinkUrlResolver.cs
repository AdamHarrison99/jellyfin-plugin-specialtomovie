using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SpecialToMovie.Providers;

/// <summary>
/// Builds the URL for a cross-link button. The URL providers are constructed by Jellyfin's part
/// discovery, so this is the only piece of the feature that is registered in the DI container.
/// </summary>
/// <remarks>
/// A full URL is emitted whenever one can be derived from the in-flight request, because that is
/// the only form a phone, tablet or TV app can follow. The web client does not need it and would be
/// harmed by it — a full URL opens a new tab and reloads the whole app — so the client script
/// reduces these links back to their hash before the user ever clicks one. That division is why
/// there is no setting here for the user to get wrong.
/// </remarks>
public class CrossLinkUrlResolver
{
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossLinkUrlResolver"/> class.
    /// </summary>
    /// <param name="appHost">The server application host.</param>
    /// <param name="httpContextAccessor">Accessor for the in-flight request the URL is derived from.</param>
    public CrossLinkUrlResolver(IServerApplicationHost appHost, IHttpContextAccessor httpContextAccessor)
    {
        _appHost = appHost;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Builds the URL for a cross-link button.
    /// </summary>
    /// <param name="targetId">The item the button navigates to.</param>
    /// <param name="marker">Which side of the pair the target is.</param>
    /// <returns>A full URL when one can be derived from the request, otherwise the hash route.</returns>
    public string Resolve(Guid targetId, char marker)
    {
        var route = CrossLinkUrlBuilder.Details(targetId, _appHost.SystemId, marker);

        // No ambient request means this DTO is not being built for a client (a scheduled task, a
        // session message); fall back rather than guessing a hostname.
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null)
        {
            return route;
        }

        var basis = _appHost.GetSmartApiUrl(request);

        // GetSmartApiUrl can derive the host from the request itself, so validate before emitting:
        // the web client writes this straight into an href without escaping it.
        if (!Uri.TryCreate(basis, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return route;
        }

        // The returned base already includes any configured base-URL path, so it is never added
        // a second time here. The route already starts with '#'.
        return $"{basis.TrimEnd('/')}/web/{route}";
    }
}
