using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyHistory;

/// <summary>One awaited refresh per affected history target, never a server-wide library scan.</summary>
internal sealed class HistoryIndexService
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _log;
    private readonly HashSet<string> _pendingSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
    private DateTime _retryAt;

    internal HistoryIndexService(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger log)
    { _library = library; _providers = providers; _fileSystem = fileSystem; _log = log; }

    internal void Queue(IEnumerable<string> sources, IEnumerable<string>? changedPaths = null)
    {
        foreach (var source in sources) _pendingSources.Add(source);
        if (changedPaths != null) foreach (var path in changedPaths) _pendingPaths.Add(Path.GetFullPath(path));
        if (_pendingSources.Count > 0) HistoryStatus.Update(s => s.IndexPhase = "Queued");
    }

    internal async Task RefreshAsync(Plugin plugin, ISet<string> selected, CancellationToken cancellationToken)
    {
        _pendingSources.RemoveWhere(id => !selected.Contains(id));
        if (_pendingSources.Count == 0 || DateTime.UtcNow < _retryAt) return;
        HistoryStatus.Update(s => s.Stage = "Index");
        var mappings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var roots = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var folders = _library.GetVirtualFolders();
        var unresolved = 0;
        foreach (var source in _pendingSources)
        {
            var root = Path.GetFullPath(Path.Combine(plugin.HistoryDataPath, "archive", source));
            var rule = (plugin.Configuration.Sources ?? Array.Empty<SourceRule>())
                .FirstOrDefault(r => r.Enabled && r.LibraryId == source);
            var candidates = folders.Where(f => !selected.Contains(f.ItemId) &&
                (string.IsNullOrEmpty(rule?.HistoryLibraryId) || f.ItemId == rule.HistoryLibraryId) &&
                (f.Locations ?? Array.Empty<string>()).Any(location => ContainsPath(location, root))).ToList();
            if (candidates.Count == 0) { unresolved++; continue; }
            foreach (var target in candidates)
            {
                if (!mappings.TryGetValue(target.ItemId, out var sources)) mappings[target.ItemId] = sources = new(StringComparer.Ordinal);
                sources.Add(source);
                if (!roots.TryGetValue(target.ItemId, out var paths)) roots[target.ItemId] = paths = new(StringComparer.Ordinal);
                paths.Add(root);
            }
        }
        HistoryStatus.Update(s =>
        {
            s.IndexTargetsTotal = mappings.Count;
            s.IndexTargetsCompleted = 0;
            s.PendingIndexTargets = mappings.Count + unresolved;
            s.IndexProgress = 0;
        });
        var completedSources = new HashSet<string>(StringComparer.Ordinal);
        var failedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!long.TryParse(target.Key, out var id) || _library.GetItemById(id) is not Folder folder)
                    throw new InvalidOperationException("History target is not an indexed folder");
                var refreshPaths = new HashSet<string>(_pendingPaths.Where(p => roots[target.Key].Any(root => ContainsPath(root, p))), StringComparer.Ordinal);
                // RefreshItem's real SDK contract is changedPath.StartsWith(item.Path), not the
                // inverse. A source-root-only RefreshPaths silently skips every series/episode.
                // For startup/retry, include on-disk leaves AND indexed leaves (deleted files).
                foreach (var root in roots[target.Key])
                {
                    ArchiveWriter.EnsureSafeDirectory(root);
                    refreshPaths.Add(root);
                    foreach (var path in SafeLeaves(root)) refreshPaths.Add(path);
                }
                foreach (var item in _library.GetItemList(new InternalItemsQuery { ParentIds = new[] { id }, Recursive = true }))
                    if (!string.IsNullOrEmpty(item.Path) && roots[target.Key].Any(root => ContainsPath(root, item.Path))) refreshPaths.Add(item.Path);
                var options = new MetadataRefreshOptions(_fileSystem)
                {
                    Recursive = true,
                    RefreshPaths = refreshPaths.ToArray(),
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ForceSave = true,
                    EnableRemoteContentProbe = false,
                    EnableSubtitleDownloading = false,
                    EnableThumbnailImageExtraction = false,
                    ReplaceAllMetadata = true,
                    ReplaceAllImages = false
                };
                HistoryStatus.Update(s => { s.Stage = "Index"; s.IndexPhase = "Running"; });
                // SDK 4.10.2: Task RefreshFullItem(BaseItem, MetadataRefreshOptions, CancellationToken).
                // QueueRefresh returns void; never label an enqueued request as completed.
                await _providers.RefreshFullItem(folder, options, cancellationToken).ConfigureAwait(false);
                completedSources.UnionWith(target.Value);
                HistoryStatus.Update(s =>
                {
                    s.IndexTargetsCompleted++;
                    s.PendingIndexTargets--;
                    s.IndexProgress = s.IndexTargetsTotal == 0 ? 0 : 100d * s.IndexTargetsCompleted / s.IndexTargetsTotal;
                    s.LastIndexUtc = DateTime.UtcNow;
                });
                _log.Info("EmbyHistory indexed history target={0} affected source count={1}", target.Key, target.Value.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failedSources.UnionWith(target.Value);
                HistoryStatus.Error("Index", ex);
                _log.ErrorException("EmbyHistory targeted history refresh failed target=" + target.Key, ex);
            }
        }
        completedSources.ExceptWith(failedSources);
        _pendingSources.ExceptWith(completedSources);
        _pendingPaths.RemoveWhere(p => completedSources.Any(source => ContainsPath(Path.Combine(plugin.HistoryDataPath, "archive", source), p)));
        if (unresolved > 0)
        {
            HistoryStatus.Error("Index", new InvalidOperationException("No configured archive location in history target"));
            _log.Warn("EmbyHistory {0} changed source archive(s) have no valid registered history target; refresh retained for retry", unresolved);
        }
        _retryAt = _pendingSources.Count > 0 ? DateTime.UtcNow.AddMinutes(1) : DateTime.MinValue;
        HistoryStatus.Update(s =>
        {
            if (_pendingSources.Count == 0) { s.IndexPhase = "Completed"; s.IndexError = ""; s.PendingIndexTargets = 0; }
            else if (s.IndexPhase != "Error") s.IndexPhase = "Queued";
        });
    }

    private static IEnumerable<string> SafeLeaves(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            ArchiveWriter.RejectLink(path);
            if (Directory.Exists(path)) { foreach (var child in SafeLeaves(path)) yield return child; }
            else if (path.EndsWith(".disc", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    private static bool ContainsPath(string parent, string child)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            child = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
            return child.Equals(parent, comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}
