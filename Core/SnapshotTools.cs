using System.Security.Cryptography;
using System.Text;

namespace EmbyHistory.Core;

public static class SnapshotTools
{
    public static MediaSnapshot Clone(MediaSnapshot value) => new()
    {
        ItemId = value.ItemId ?? "", LibraryId = value.LibraryId ?? "", Name = value.Name ?? "",
        Code = value.Code ?? "", OriginalTitle = value.OriginalTitle ?? "", Overview = value.Overview ?? "",
        Path = value.Path ?? "", PosterPath = value.PosterPath ?? "", Year = value.Year,
        SeriesId = value.SeriesId ?? "", SeriesName = value.SeriesName ?? "", SeriesYear = value.SeriesYear,
        SeasonNumber = value.SeasonNumber, EpisodeNumber = value.EpisodeNumber,
        SeriesPosterPath = value.SeriesPosterPath ?? "",
        SeriesProviderIds = value.SeriesProviderIds?.ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal) ?? new(),
        Genres = value.Genres?.Select(x => x ?? "").ToList() ?? new(),
        Tags = value.Tags?.Select(x => x ?? "").ToList() ?? new(),
        Actors = value.Actors?.Where(x => x is not null).Select(x => new ActorSnapshot
            { Name = x.Name ?? "", Role = x.Role ?? "" }).ToList() ?? new(),
        ProviderIds = value.ProviderIds?.ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal) ?? new()
    };

    public static HistoryRecord Clone(HistoryRecord value) => new()
    {
        Key = value.Key, LibraryId = value.LibraryId, Code = value.Code, SeriesKey = value.SeriesKey,
        Mode = value.Mode,
        Snapshot = Clone(value.Snapshot), FirstSeenUtc = value.FirstSeenUtc,
        LastSeenUtc = value.LastSeenUtc, RemovedUtc = value.RemovedUtc,
        ActiveItemIds = new HashSet<string>(value.ActiveItemIds, StringComparer.Ordinal)
    };

    public static MediaSnapshot Prepare(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.ItemId)) throw new ArgumentException("ItemId is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.LibraryId)) throw new ArgumentException("LibraryId is required.", nameof(snapshot));
        return Clone(snapshot);
    }

    /// <summary>
    /// A refreshed snapshot often carries no usable poster (Emby image tags expire before the cache
    /// file is regenerated). Keep the last poster reference that still exists on disk instead of
    /// overwriting it with an empty or dangling path.
    /// </summary>
    public static void PreservePoster(MediaSnapshot incoming, MediaSnapshot recordSnapshot, IEnumerable<MediaSnapshot> activeSnapshots)
    {
        if (IsUsablePoster(incoming.PosterPath)) return;
        foreach (var candidate in activeSnapshots.Append(recordSnapshot))
        {
            if (!IsUsablePoster(candidate.PosterPath)) continue;
            incoming.PosterPath = candidate.PosterPath;
            return;
        }
    }

    public static bool IsUsablePoster(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// A rescan or re-entry often produces a snapshot before Emby has scraped it: Name is the file
    /// basename and every metadata field is empty. Keep the last known non-empty metadata for the same
    /// work so removing the item afterwards does not leave only a placeholder title. Non-empty incoming
    /// values always win; work whose code genuinely changed is a different record and inherits nothing.
    /// </summary>
    public static void PreserveMetadata(MediaSnapshot incoming, MediaSnapshot recordSnapshot,
        IEnumerable<MediaSnapshot> activeSnapshots, MediaSnapshot? previousForItem = null)
    {
        PreservePoster(incoming, recordSnapshot, activeSnapshots);
        var candidates = new List<MediaSnapshot>();
        if (previousForItem is not null) candidates.Add(previousForItem);
        candidates.Add(recordSnapshot);
        candidates.AddRange(activeSnapshots.Where(x => x is not null && x.ItemId != incoming.ItemId));
        candidates = candidates.Where(x => x is not null && !ReferenceEquals(x, incoming)).ToList();

        if (IsPlaceholderName(incoming)) PreserveName(incoming, candidates);
        if (string.IsNullOrWhiteSpace(incoming.OriginalTitle)) incoming.OriginalTitle = First(candidates, x => x.OriginalTitle);
        if (string.IsNullOrWhiteSpace(incoming.Overview)) incoming.Overview = First(candidates, x => x.Overview);
        incoming.Year ??= candidates.Select(x => x.Year).FirstOrDefault(y => y.HasValue);
        if (incoming.Genres.Count == 0) incoming.Genres = candidates.FirstOrDefault(x => x.Genres.Count > 0)?.Genres.Select(x => x ?? "").ToList() ?? new();
        if (incoming.Tags.Count == 0) incoming.Tags = candidates.FirstOrDefault(x => x.Tags.Count > 0)?.Tags.Select(x => x ?? "").ToList() ?? new();
        if (incoming.Actors.Count == 0)
            incoming.Actors = candidates.FirstOrDefault(x => x.Actors.Count > 0)?.Actors.Where(x => x is not null)
                .Select(x => new ActorSnapshot { Name = x.Name ?? "", Role = x.Role ?? "" }).ToList() ?? new();
        if (incoming.ProviderIds.Count == 0)
            incoming.ProviderIds = candidates.FirstOrDefault(x => x.ProviderIds.Count > 0)?
                .ProviderIds.ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal) ?? new();
    }

    /// <summary>True when the name carries no scraped title: empty, the file basename or its folder name.</summary>
    private static bool IsPlaceholderName(MediaSnapshot snapshot)
    {
        var name = (snapshot.Name ?? "").Trim();
        if (name.Length == 0) return true;
        return PathNames(snapshot.Path).Any(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void PreserveName(MediaSnapshot incoming, List<MediaSnapshot> candidates)
    {
        foreach (var candidate in candidates)
        {
            var name = (candidate.Name ?? "").Trim();
            if (name.Length == 0) continue;
            // Only a richer old name is worth restoring; another placeholder adds nothing.
            if (PathNames(candidate.Path).Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
            incoming.Name = name;
            return;
        }
    }

    private static string[] PathNames(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<string>();
        var file = parts[^1];
        var extension = System.IO.Path.GetExtension(file);
        var basename = extension.Length > 0 ? file[..^extension.Length] : file;
        return parts.Length > 1 ? new[] { basename, parts[^2] } : new[] { basename };
    }

    /// <summary>The first non-empty value among the candidates, or "" when every copy is still unscraped.</summary>
    private static string First(List<MediaSnapshot> candidates, Func<MediaSnapshot, string> selector) =>
        candidates.Select(selector).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    public static (string Code, string Identity) Identify(MediaSnapshot snapshot) => Identify(snapshot, IdentityMode.Code);

    /// <summary>
    /// Identity must follow the source library's own naming reality. Release codes appear only in
    /// code-based libraries; ordinary films carry scraper ids; free-form adult libraries carry neither
    /// and must fall back to a first-seen local record instead of a guessed code.
    /// </summary>
    public static (string Code, string Identity) Identify(MediaSnapshot snapshot, IdentityMode mode) => mode switch
    {
        IdentityMode.Provider => ProviderIdentity(snapshot),
        IdentityMode.LocalPath => LocalPathIdentity(snapshot),
        IdentityMode.SeriesEpisode => SeriesEpisodeIdentity(snapshot),
        _ => CodeIdentity(snapshot)
    };

    /// <summary>
    /// An episode is identified by its series plus season and episode numbers, never by the
    /// episode's own title or file name: renaming an episode file must not look like a removal.
    /// </summary>
    private static (string Code, string Identity) SeriesEpisodeIdentity(MediaSnapshot snapshot)
    {
        var seriesIdentity = SeriesIdentity(snapshot);
        // No series context at all (should not happen for a real Episode): keep it local.
        if (seriesIdentity.Length == 0) return LocalPathIdentity(snapshot);
        if (snapshot.SeasonNumber is null || snapshot.EpisodeNumber is null)
            return ("", seriesIdentity + "\npath:" + snapshot.Path.Normalize(NormalizationForm.FormKC).Trim());
        return ("", seriesIdentity + "\ns:" + snapshot.SeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "\ne:" + snapshot.EpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Identity of the parent series; empty when the snapshot carries no series context.</summary>
    public static string SeriesIdentity(MediaSnapshot snapshot)
    {
        var series = Ordered(snapshot.SeriesProviderIds);
        if (series.Count > 0) return "series-provider:" + Normalize(series[0].Key) + ":" + Normalize(series[0].Value);
        if (!string.IsNullOrWhiteSpace(snapshot.SeriesName))
        {
            var year = snapshot.SeriesYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return "series-title:" + Normalize(snapshot.SeriesName) + "\nyear:" + year;
        }
        return string.IsNullOrWhiteSpace(snapshot.SeriesId) ? "" : "series-item:" + snapshot.SeriesId.Trim();
    }

    public static string SeriesKeyOf(MediaSnapshot snapshot, string libraryId)
    {
        var identity = SeriesIdentity(snapshot);
        return identity.Length == 0 ? "" : Key(libraryId, identity);
    }

    private static (string Code, string Identity) ProviderIdentity(MediaSnapshot snapshot)
    {
        var providers = Ordered(snapshot);
        if (providers.Count > 0) return ("", "provider:" + Normalize(providers[0].Key) + ":" + Normalize(providers[0].Value));
        return FallbackIdentity(snapshot);
    }

    private static (string Code, string Identity) LocalPathIdentity(MediaSnapshot snapshot)
    {
        // Path first on purpose: a scraped title or a later provider id must not silently move
        // a free-form record to another key and manufacture a removal.
        if (!string.IsNullOrWhiteSpace(snapshot.Path)) return ("", "path:" + snapshot.Path.Normalize(NormalizationForm.FormKC).Trim());
        var providers = Ordered(snapshot);
        if (providers.Count > 0) return ("", "provider:" + Normalize(providers[0].Key) + ":" + Normalize(providers[0].Value));
        return FallbackIdentity(snapshot);
    }

    private static List<KeyValuePair<string, string>> Ordered(MediaSnapshot snapshot) => Ordered(snapshot.ProviderIds);

    private static List<KeyValuePair<string, string>> Ordered(Dictionary<string, string> ids) =>
        ids.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .OrderBy(x => ProviderPriority(x.Key)).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    private static (string Code, string Identity) FallbackIdentity(MediaSnapshot snapshot)
    {
        var year = snapshot.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (!string.IsNullOrWhiteSpace(snapshot.OriginalTitle)) return ("", "title:" + Normalize(snapshot.OriginalTitle) + "\nyear:" + year);
        if (!string.IsNullOrWhiteSpace(snapshot.Path)) return ("", "path:" + snapshot.Path.Normalize(NormalizationForm.FormKC).Trim());
        if (!string.IsNullOrWhiteSpace(snapshot.Name)) return ("", "name:" + Normalize(snapshot.Name) + "\nyear:" + year);
        return ("", "item:" + snapshot.ItemId);
    }

    private static (string Code, string Identity) CodeIdentity(MediaSnapshot snapshot)
    {
        var filename = snapshot.Path.Replace('\\', '/');
        filename = filename[(filename.LastIndexOf('/') + 1)..];
        // The file name is what is actually on disk; Emby's scraped Name can belong to a different
        // release in the same folder (mis-scrape), so it is only a fallback.
        foreach (var text in new[] { snapshot.Code, filename, snapshot.Name, snapshot.OriginalTitle })
        {
            var code = CodeNormalizer.Extract(text);
            if (code.Length > 0) return (code, code);
        }
        var providers = Ordered(snapshot);
        if (providers.Count > 0)
        {
            var provider = providers[0];
            return ("", "provider:" + Normalize(provider.Key) + ":" + Normalize(provider.Value));
        }
        return FallbackIdentity(snapshot);
    }

    public static string Key(string libraryId, string identity) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(libraryId + "\n" + identity))).ToLowerInvariant();

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
    private static int ProviderPriority(string key) => key.ToLowerInvariant() switch
    {
        "imdb" => 0, "tmdb" => 1, "tvdb" => 2, "anidb" => 3, _ => 4
    };
}
