namespace Jellyfin.Plugin.SpecialToMovie.Providers;

/// <summary>
/// Pure string helpers for the cross-link URLs. Kept separate from
/// <see cref="CrossLinkUrlResolver"/> so the route shape has exactly one definition.
/// </summary>
internal static class CrossLinkUrlBuilder
{
    /// <summary>
    /// Marks a link whose target is the paired movie.
    /// </summary>
    public const char MovieMarker = 'm';

    /// <summary>
    /// Marks a link whose target is the paired special.
    /// </summary>
    public const char SpecialMarker = 's';

    /// <summary>
    /// Builds the in-app details route for an item.
    /// </summary>
    /// <remarks>
    /// Only a GUID, the server's system ID, and a fixed marker character ever reach this string.
    /// The web client interpolates the URL into an href attribute without escaping it, so no
    /// user-supplied text may be added here. The button captions travel through the provider's
    /// Name instead, which the web client does escape.
    /// </remarks>
    /// <param name="itemId">The item to link to.</param>
    /// <param name="systemId">The server's system ID.</param>
    /// <param name="marker">Which side of the pair the target is; see the marker constants.</param>
    /// <returns>A hash route of the form <c>#/details?id=...&amp;serverId=...&amp;stm=m</c>.</returns>
    public static string Details(Guid itemId, string systemId, char marker)
        => $"#/details?id={itemId:N}&serverId={systemId}&stm={marker}";
}
