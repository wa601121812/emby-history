using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyHistory.Core;

namespace EmbyHistory;

/// <summary>A source-relative boundary, not filesystem-deletion evidence.</summary>
public sealed record HistoryFolderContext(string LibraryId, string SourceRoot, string RelativePath)
{
    public string FolderPath => Path.GetFullPath(Path.Combine(SourceRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    public string Name => Path.GetFileName(FolderPath);
    // Root participates because different Locations of the same library may contain the same relative name.
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "folder-v1\n" + LibraryId + "\n" + SourceRoot + "\n" + RelativePath))).ToLowerInvariant();
}

public sealed record HistoryFolderGroup(HistoryFolderContext Context, IReadOnlyList<string> RecordKeys)
{
    public string LibraryId => Context.LibraryId;
    public string FolderPath => Context.FolderPath;
    public string DisplayName => Context.Name;
    public string Key => Context.Key;
}

/// <summary>
/// Additive presentation plan. Never changes ledger keys, FirstSeen, archive directories or disc paths.
/// Persisted as HistoryDataPath/folder-context.json, Version=1, Groups=[{Context,RecordKeys}].
/// Eligibility counts current and removed movies; RecordKeys contains ONLY removed movies.
/// A member returning to source therefore leaves the remaining single-member group intact.
/// Choose the deepest non-root directories with at least two known works; singleton leaf directories
/// roll up to their nearest shared ancestor. Assign each work once so nested groups cannot duplicate it.
/// </summary>
public static class HistoryFolderPlan
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static HistoryFolderContext? Resolve(string libraryId, IEnumerable<string> locations, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || !Path.IsPathFullyQualified(folderPath)) return null;
        try
        {
            var folder = PhysicalPath(folderPath);
            return locations.Where(Path.IsPathFullyQualified).Select(PhysicalPath)
                .Where(root => IsStrictlyWithin(folder, root))
                .OrderByDescending(root => root.Length)
                .Select(root => new HistoryFolderContext(libraryId, root, Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/')))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static bool Matches(HistoryRecord record, HistoryFolderContext context) =>
        record.LibraryId == context.LibraryId && record.Mode != IdentityMode.SeriesEpisode &&
        Contains(context, record.Snapshot.Path);

    public static bool Contains(HistoryFolderContext context, string mediaPath)
    {
        if (!Path.IsPathFullyQualified(mediaPath) || !Valid(context)) return false;
        try { return IsStrictlyWithin(PhysicalPath(mediaPath), context.FolderPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static bool Valid(HistoryFolderContext context)
    {
        if (string.IsNullOrWhiteSpace(context.LibraryId) || !Path.IsPathFullyQualified(context.SourceRoot) ||
            string.IsNullOrWhiteSpace(context.RelativePath) || Path.IsPathRooted(context.RelativePath) ||
            context.RelativePath.Split('/').Any(p => p is "" or "." or "..")) return false;
        try { return IsStrictlyWithin(context.FolderPath, context.SourceRoot); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    public static IReadOnlyList<HistoryFolderGroup> Build(IEnumerable<HistoryRecord> records,
        IDictionary<string, string[]> sourceLocations)
    {
        var allRecords = records.ToArray();
        var removedKeys = new HashSet<string>(allRecords.Where(r => r.ActiveItemIds.Count == 0 && r.RemovedUtc.HasValue).Select(r => r.Key), StringComparer.Ordinal);
        var contexts = new Dictionary<string, (HistoryFolderContext Context, HashSet<string> Keys)>(StringComparer.Ordinal);
        foreach (var record in allRecords.Where(r => r.Mode != IdentityMode.SeriesEpisode))
        {
            if (!sourceLocations.TryGetValue(record.LibraryId, out var locations)) continue;
            var parent = Path.IsPathFullyQualified(record.Snapshot.Path) ? Path.GetDirectoryName(record.Snapshot.Path) : null;
            if (parent == null) continue;
            var context = Resolve(record.LibraryId, locations, parent);
            while (context != null)
            {
                if (!contexts.TryGetValue(context.Key, out var item))
                    contexts[context.Key] = item = (context, new HashSet<string>(StringComparer.Ordinal));
                item.Keys.Add(record.Key);
                parent = Path.GetDirectoryName(context.FolderPath);
                context = parent == null ? null : Resolve(record.LibraryId, locations, parent);
            }
        }
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<HistoryFolderGroup>();
        foreach (var candidate in contexts.Values.OrderByDescending(v => v.Context.RelativePath.Count(c => c == '/'))
                     .ThenBy(v => v.Context.Key, StringComparer.Ordinal))
        {
            var members = candidate.Keys.Where(k => !assigned.Contains(k)).ToArray();
            if (members.Length < 2) continue;
            assigned.UnionWith(members);
            var removed = members.Where(removedKeys.Contains).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            if (removed.Length > 0) groups.Add(new HistoryFolderGroup(candidate.Context, removed));
        }
        return groups.OrderBy(g => g.LibraryId, StringComparer.Ordinal).ThenBy(g => g.Context.SourceRoot, StringComparer.Ordinal)
            .ThenBy(g => g.Context.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public static void Save(string dataPath, IReadOnlyList<HistoryFolderGroup> groups)
    {
        ArchiveWriter.EnsureSafeDirectory(dataPath);
        var path = Path.Combine(dataPath, "folder-context.json");
        ArchiveWriter.RejectLink(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new FolderState(1, groups), JsonOptions);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<HistoryFolderGroup> Load(string dataPath)
    {
        var path = Path.Combine(dataPath, "folder-context.json");
        ArchiveWriter.EnsureSafeDirectory(dataPath); ArchiveWriter.RejectLink(path);
        if (!File.Exists(path)) return Array.Empty<HistoryFolderGroup>();
        var state = JsonSerializer.Deserialize<FolderState>(File.ReadAllBytes(path), JsonOptions);
        if (state?.Version != 1 || state.Groups == null || state.Groups.Any(g => g == null || g.Context == null ||
            !Valid(g.Context) || g.RecordKeys == null || g.RecordKeys.Count < 1 ||
            g.RecordKeys.Any(string.IsNullOrWhiteSpace) || g.RecordKeys.Distinct(StringComparer.Ordinal).Count() != g.RecordKeys.Count) ||
            state.Groups.Select(g => g.Context.Key).Distinct(StringComparer.Ordinal).Count() != state.Groups.Count)
            throw new InvalidDataException("Invalid folder-context presentation plan; ledger is unchanged");
        return state.Groups;
    }

    private sealed record FolderState(int Version, IReadOnlyList<HistoryFolderGroup> Groups);

    private static bool IsStrictlyWithin(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison) &&
        !path.Equals(root, PathComparison);

    // Resolve existing ancestors too: a deleted child under a symlink still has the same source boundary.
    private static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget != null) current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolved source path link");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
