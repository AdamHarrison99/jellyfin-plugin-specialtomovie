namespace Jellyfin.Plugin.SpecialToMovie.Models;

public class LinkedPair
{
    public Guid Id { get; set; }

    public Guid EpisodeItemId { get; set; }

    public Guid? MovieItemId { get; set; }

    public Guid SourceLibraryId { get; set; }

    public Guid DestinationLibraryId { get; set; }

    public string EpisodePath { get; set; } = string.Empty;

    public string? HardLinkPath { get; set; }

    public bool IsExistingMovie { get; set; }

    public string MovieTitle { get; set; } = string.Empty;

    public int? MovieYear { get; set; }

    public string? TmdbMovieId { get; set; }

    public string? TvdbMovieId { get; set; }

    public string? ImdbId { get; set; }

    public PairStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
