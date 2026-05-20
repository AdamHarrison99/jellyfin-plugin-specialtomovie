namespace Jellyfin.Plugin.SpecialToMovie.Models;

public record MovieMatch
{
    public string Title { get; init; } = string.Empty;

    public int? Year { get; init; }

    public string? TmdbMovieId { get; init; }

    public string? TvdbMovieId { get; init; }

    public string? TvdbMovieSlug { get; init; }

    public string? ImdbId { get; init; }
}
