namespace Jellyfin.Plugin.SpecialToMovie.Providers;

// The one definition of the cross-link route shape.
// See agentic/ARCHITECTURE.md, "Cross-link buttons".
internal static class CrossLinkUrlBuilder
{
    public const char MovieMarker = 'm';

    public const char SpecialMarker = 's';

    // ! Only a GUID, the system ID and a marker char may reach this string.
    // The web client writes an external URL into an href without escaping it.
    public static string Details(Guid itemId, string systemId, char marker)
        => $"#/details?id={itemId:N}&serverId={systemId}&stm={marker}";
}
