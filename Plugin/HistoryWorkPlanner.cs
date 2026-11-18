using EmbyHistory.Core;

namespace EmbyHistory;

/// <summary>Deletion evidence is Emby ids and saved metadata, never a missing filesystem path.</summary>
internal static class HistoryWorkPlanner
{
    internal static bool MatchesContainer(HistoryRecord record, string seriesId, int? seasonNumber) =>
        record.Mode == IdentityMode.SeriesEpisode && !string.IsNullOrEmpty(seriesId) &&
        record.Snapshot.SeriesId == seriesId &&
        (!seasonNumber.HasValue || record.Snapshot.SeasonNumber == seasonNumber);

    internal static HashSet<string> AffectedLibraries(IEnumerable<HistoryRecord> records,
        ISet<string> configured, string itemId, string seriesId, int? seasonNumber, bool container,
        IEnumerable<string> resolvedLibraries)
    {
        var result = new HashSet<string>(resolvedLibraries.Where(configured.Contains), StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!configured.Contains(record.LibraryId)) continue;
            if (container ? MatchesContainer(record, seriesId, seasonNumber) :
                record.ActiveItemIds.Contains(itemId) || record.Snapshot.ItemId == itemId)
                result.Add(record.LibraryId);
        }
        return result;
    }

    internal static HashSet<string> ReferencedPosterPaths(IEnumerable<HistoryRecord> records)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var record in records)
            foreach (var path in new[] { record.Snapshot.PosterPath, record.Snapshot.SeriesPosterPath })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { paths.Add(Path.GetFullPath(path)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            }
        return paths;
    }
}
