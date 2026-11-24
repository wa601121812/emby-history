namespace EmbyHistory.Core;

/// <summary>
/// How a source library decides that two items are the same work. Classification (adult/general)
/// is deliberately separate: an adult library may have no release codes at all.
/// </summary>
public enum IdentityMode
{
    /// <summary>Release code first (JAV-style). Production behaviour for library 1001; do not change.</summary>
    Code = 0,
    /// <summary>Scraper work id first (TMDB/IMDb/TVDB). Use for ordinary films.</summary>
    Provider = 1,
    /// <summary>First-seen local record keyed by path. Use where names are free-form and no ids exist.</summary>
    LocalPath = 2,
    /// <summary>Episode identity: series identity plus season and episode numbers.</summary>
    SeriesEpisode = 3
}

/// <summary>Detached metadata captured before an item disappears from Emby.</summary>
public sealed class MediaSnapshot
{
    public string ItemId { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Overview { get; set; } = "";
    public string Path { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public int? Year { get; set; }
    // Episode-only context. Series identity deliberately comes from the series, not the episode.
    public string SeriesId { get; set; } = "";
    public string SeriesName { get; set; } = "";
    public int? SeriesYear { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    /// <summary>Cached sidecar of the parent series, captured while the series still existed.</summary>
    public string SeriesPosterPath { get; set; } = "";
    public Dictionary<string, string> SeriesProviderIds { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<ActorSnapshot> Actors { get; set; } = new();
    public Dictionary<string, string> ProviderIds { get; set; } = new();
}

public sealed class ActorSnapshot
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class HistoryRecord
{
    public string Key { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>For episodes: stable key of the parent series, so its container can be shared/cleaned.</summary>
    public string SeriesKey { get; set; } = "";
    public IdentityMode Mode { get; set; } = IdentityMode.Code;
    public MediaSnapshot Snapshot { get; set; } = new();
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? RemovedUtc { get; set; }
    public HashSet<string> ActiveItemIds { get; set; } = new(StringComparer.Ordinal);
}
