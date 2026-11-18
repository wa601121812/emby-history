using System.Collections.Concurrent;
using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyHistory;

public sealed class EntryPoint : IServerEntryPoint
{
    private const int MaxPending = 20000;
    private const int PageSize = 256;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<Change> _pending = new();
    private readonly HashSet<string> _invalidIdsLogged = new(StringComparer.Ordinal);
    private Timer? _timer;
    private HistoryStore? _store;
    private ArchiveWriter? _writer;
    private int _busy;
    private volatile bool _disposed;
    private DateTime _nextScan = DateTime.MinValue;
    private DateTime _storeRetryAt = DateTime.MinValue;
    private DateTime _syncRetryAt = DateTime.MinValue;
    private int _storeFailures;
    private int _started;
    private int _busySkips;
    private bool _needsSync = true;
    private string _signature = "";
    private Dictionary<string, IdentityMode> _modes = new(StringComparer.Ordinal);
    private DateTime _nextAccessSync = DateTime.MinValue;
    private readonly HistoryIndexService _index;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _workerGate = new();
    private Task _workerTask = Task.CompletedTask;
    private int _eventWake;
    private bool _startupIndex = true;
    private readonly Dictionary<string, DateTime> _reconcile = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyLibraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Change> _containerProofs = new(StringComparer.Ordinal);

    private sealed record Change(string ItemId, string? LibraryId, MediaSnapshot? Snapshot,
        bool IsEpisode = false, bool Container = false, string SeriesId = "", int? SeasonNumber = null);

    public EntryPoint(ILibraryManager libraryManager, IUserManager userManager, ILogManager logManager,
        IProviderManager providerManager, IFileSystem fileSystem)
    {
        _library = libraryManager; _users = userManager; _log = logManager.GetLogger("EmbyHistory");
        _index = new HistoryIndexService(libraryManager, providerManager, fileSystem, _log);
    }

    public void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _library.ItemAdded += AddedOrUpdated;
        _library.ItemUpdated += AddedOrUpdated;
        _library.ItemRemoved += Removed;
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var plugin = Plugin.Instance;
        _log.Info("EmbyHistory loaded; enabled={0}, configured libraries={1}",
            plugin?.Configuration.Enabled == true, plugin?.Configuration.SourceLibraryIds?.Length ?? 0);
    }

    private void AddedOrUpdated(object? sender, ItemChangeEventArgs e)
    {
        var plugin = Plugin.Instance;
        if (_disposed || plugin?.Configuration.Enabled != true || IsOwnItem(e.Item, plugin)) return;
        var isEpisode = e.Item is Episode;
        // Movies feed movie rules, episodes feed series rules; nothing else is tracked.
        if (e.Item is not Movie && !isEpisode) return;
        try
        {
            var rules = SafeRules(plugin);
            var selected = new HashSet<string>(rules.Keys, StringComparer.Ordinal);
            if (selected.Count == 0) return;
            foreach (var folder in _library.GetCollectionFolders(e.Item))
            {
                var id = folder.InternalId.ToString();
                if (!selected.Contains(id)) continue;
                if (!TracksItemType(rules[id], isEpisode)) continue;
                var snapshot = Snapshot(e.Item, id);
                // Capture the sidecar while the source still exists; a failure must not drop the event.
                try { CacheArtwork(snapshot, plugin.HistoryDataPath); }
                catch (Exception ex) { snapshot.PosterPath = ""; _log.Warn("EmbyHistory poster cache skipped item={0}: {1}", snapshot.ItemId, ex.Message); }
                TryEnqueue(new Change(snapshot.ItemId, id, snapshot));
            }
        }
        catch (Exception ex) { _log.ErrorException("EmbyHistory could not capture item event", ex); }
    }

    private void Removed(object? sender, ItemChangeEventArgs e)
    {
        var plugin = Plugin.Instance;
        if (_disposed || plugin?.Configuration.Enabled != true || IsOwnItem(e.Item, plugin)) return;
        var isEpisode = e.Item is Episode;
        var container = e.Item is Series or Season;
        if (e.Item is not Movie && !isEpisode && !container) return;
        try
        {
            var selected = Selected(plugin);
            if (selected.Count == 0) return;
            // Copy ids from the event before Emby detaches the parent tree. No path-disappearance
            // inference: ledger context is the fallback when GetCollectionFolders no longer works.
            var owners = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var folder in _library.GetCollectionFolders(e.Item))
                    if (selected.Contains(folder.InternalId.ToString())) owners.Add(folder.InternalId.ToString());
            }
            catch (Exception ex) { _log.Debug("EmbyHistory removal owner lookup deferred: " + ex.GetType().Name); }
            var seriesId = e.Item is Series ? e.Item.InternalId : e.Item.SeriesId;
            if (e.Item is Season season && seriesId <= 0)
            {
                try { seriesId = season.FindSeriesId(); }
                catch (Exception ex) { _log.Debug("EmbyHistory season parent lookup deferred: " + ex.GetType().Name); }
                if (seriesId <= 0 && season.ParentId > 0) seriesId = season.ParentId;
            }
            var seasonNumber = e.Item is Season ? e.Item.IndexNumber : null;
            // An unknown season number is not evidence for deleting every season of a series.
            var context = seriesId > 0 && (e.Item is not Season || seasonNumber.HasValue) ? seriesId.ToString() : "";
            if (owners.Count == 0)
                TryEnqueue(new Change(e.Item.InternalId.ToString(), null, null, isEpisode, container, context, seasonNumber));
            else
                foreach (var owner in owners)
                    TryEnqueue(new Change(e.Item.InternalId.ToString(), owner, null, isEpisode, container, context, seasonNumber));
        }
        catch (Exception ex)
        {
            HistoryStatus.Error("Ledger", ex);
            _log.ErrorException("EmbyHistory could not capture removal event", ex);
        }
    }

    private void TryEnqueue(Change change)
    {
        if (_pending.Count >= MaxPending)
        {
            _pending.TryDequeue(out _);
            _log.Warn("EmbyHistory event queue overflow; dropping the oldest event");
            HistoryStatus.Error("Ledger", new InvalidOperationException("Event queue overflow; scheduled reconcile will repair presence"));
        }
        _pending.Enqueue(change);
        HistoryStatus.Update(s => { s.PendingEvents = _pending.Count; s.LastEventUtc = DateTime.UtcNow; });
        // Wake once per batch; do not keep postponing the worker during a continuous event stream.
        if (Interlocked.Exchange(ref _eventWake, 1) == 0)
        {
            try { _timer?.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10)); }
            catch (ObjectDisposedException) { }
        }
    }

    private bool IsOwnItem(BaseItem item, Plugin plugin)
    {
        if (string.IsNullOrEmpty(item.Path)) return false;
        try { return IsWithin(item.Path, plugin.HistoryDataPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private HashSet<string> Selected(Plugin plugin)
    {
        return new HashSet<string>(SafeRules(plugin).Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Source id to identity mode. A legacy SourceLibraryIds entry keeps the historical code-first
    /// behaviour, so an existing JAV deployment is unaffected until it is migrated explicitly.
    /// </summary>
    private Dictionary<string, IdentityMode> SourceRules(Plugin plugin) => SourceRulesFrom(plugin.Configuration);

    private Dictionary<string, IdentityMode> SourceRulesFrom(Configuration configuration)
    {
        var result = new Dictionary<string, IdentityMode>(StringComparer.Ordinal);
        foreach (var rule in configuration.Sources ?? Array.Empty<SourceRule>())
        {
            var value = (rule?.LibraryId ?? "").Trim();
            if (!IsCanonicalId(value))
            {
                WarnInvalid(value);
                continue;
            }
            if (rule!.Enabled == false) continue;
            if (result.ContainsKey(value)) { WarnInvalid(value + " (duplicate)"); continue; }
            result[value] = rule.Mode;
        }
        if (result.Count > 0) return result;
        foreach (var raw in configuration.SourceLibraryIds ?? Array.Empty<string>())
        {
            var value = (raw ?? "").Trim();
            if (!IsCanonicalId(value)) { WarnInvalid(value); continue; }
            result[value] = IdentityMode.Code;
        }
        return result;
    }

    /// <summary>
    /// Refuses sources whose history library is shared across content classes. The settings page
    /// blocks this too, but a direct configuration write must not be able to mix adult and general
    /// history in one library.
    /// </summary>
    private Dictionary<string, IdentityMode> SafeRules(Plugin plugin)
    {
        var rules = plugin.Configuration.Sources ?? Array.Empty<SourceRule>();
        var conflicts = AccessPlanner.CrossClassTargets(rules.Select(r => new RuleView
        {
            LibraryId = (r?.LibraryId ?? "").Trim(),
            ContentClass = r?.ContentClass ?? "Unclassified",
            HistoryLibraryId = (r?.HistoryLibraryId ?? "").Trim()
        }));
        if (conflicts.Count == 0) return SourceRules(plugin);
        foreach (var id in conflicts)
            _log.Error("EmbyHistory source {0} is disabled: its history library also receives the other content class. " +
                "Separate general and adult history libraries before re-enabling it.", id);
        var safe = new Configuration
        {
            Enabled = plugin.Configuration.Enabled,
            SourceLibraryIds = plugin.Configuration.SourceLibraryIds,
            AllowEmptyLibraryReconcile = plugin.Configuration.AllowEmptyLibraryReconcile,
            Sources = rules.Where(r => r != null && !conflicts.Contains((r.LibraryId ?? "").Trim())).ToArray()
        };
        return SourceRulesFrom(safe);
    }

    private static bool IsCanonicalId(string value) =>
        long.TryParse(value, out var id) && id > 0 && value == id.ToString();

    private void WarnInvalid(string value)
    {
        if (value.Length > 0 && _invalidIdsLogged.Add(value))
            _log.Warn("EmbyHistory ignored source library entry '{0}': expected a canonical positive decimal Emby id", value);
    }

    private void Tick(object? state)
    {
        lock (_workerGate)
        {
            if (_disposed) return;
            if (!_workerTask.IsCompleted)
            {
                var skips = Interlocked.Increment(ref _busySkips);
                if (skips % 18 == 1) _log.Warn("EmbyHistory worker still busy; {0} scheduled cycles skipped", skips);
                return;
            }
            Interlocked.Exchange(ref _eventWake, 0);
            Volatile.Write(ref _busy, 1);
            // Keep the complete asynchronous operation, including indexing, tracked until completion.
            // The timer callback never blocks on Emby's refresh task and is never async void.
            _workerTask = Task.Run(ProcessAsync);
        }
    }

    private async Task ProcessAsync()
    {
        Interlocked.Exchange(ref _busySkips, 0);
        HistoryStatus.Update(s => { s.Busy = true; s.Stage = "Ledger"; });
        try
        {
            var plugin = Plugin.Instance;
            if (plugin == null) { Drain(); return; }
            HistoryStatus.Update(s => s.Enabled = plugin.Configuration.Enabled);
            if (!plugin.Configuration.Enabled)
            {
                Drain(); _signature = "";
                HistoryStatus.Update(s => { s.Stage = "Disabled"; s.LedgerPhase = "Disabled"; });
                return;
            }
            var modes = SafeRules(plugin);
            _modes = modes;
            var selected = new HashSet<string>(modes.Keys, StringComparer.Ordinal);
            if (selected.Count == 0) { Drain(); HistoryStatus.Update(s => s.Stage = "NoSources"); return; }
            _reconcile.Keys.Where(id => !selected.Contains(id)).ToList().ForEach(id => _reconcile.Remove(id));
            _dirtyLibraries.RemoveWhere(id => !selected.Contains(id));
            foreach (var source in _containerProofs.Values.Where(c => c.LibraryId != null && !selected.Contains(c.LibraryId)).Select(c => c.LibraryId!).Distinct().ToArray())
                ClearContainerProofs(source);
            var signature = string.Join(",", modes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + ":" + (int)kv.Value)) + "|" +
                string.Join(",", (plugin.Configuration.Sources ?? Array.Empty<SourceRule>())
                    .Where(r => selected.Contains(r.LibraryId)).Select(r => r.LibraryId + ":" + r.HistoryLibraryId));
            if (signature != _signature)
            {
                _nextScan = DateTime.MinValue; _signature = signature; _needsSync = true;
                _dirtyLibraries.UnionWith(selected); _startupIndex = true;
            }
            if (DateTime.UtcNow >= _nextAccessSync)
            {
                _nextAccessSync = DateTime.UtcNow.AddMinutes(10);
                try { new HistoryAccessSync(_library, _users, _log).Run(plugin.Configuration.Sources ?? Array.Empty<SourceRule>()); }
                catch (Exception ex) { _log.ErrorException("EmbyHistory access sync failed", ex); }
            }
            if (!EnsureStore(plugin)) { TrimQueue(); return; }
            if (!EnsureWriter(plugin)) { TrimQueue(); return; }
            foreach (var id in selected)
            {
                try { _writer!.EnsureLibrary(id); }
                catch (Exception ex) { HistoryStatus.Error("Archive", ex); _log.ErrorException("EmbyHistory archive directory unavailable for library " + id, ex); }
            }
            // Bound each batch: a busy Emby producer cannot starve persistence and index publication.
            var batch = Math.Min(_pending.Count, MaxPending);
            if (batch > 0) HistoryStatus.Update(s => { s.LedgerPhase = "Events"; s.LedgerEventsProcessed = 0; });
            for (var i = 0; i < batch && _pending.TryDequeue(out var change); i++)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                if (change.Snapshot != null && selected.Contains(change.Snapshot.LibraryId))
                {
                    Observe(plugin, change.Snapshot, ModeOf(change.Snapshot.LibraryId));
                    _dirtyLibraries.Add(change.Snapshot.LibraryId);
                }
                else if (change.Snapshot == null) ApplyRemoval(plugin, change, selected);
                HistoryStatus.Update(s => { s.LedgerEventsProcessed++; s.PendingEvents = _pending.Count; });
            }
            var periodic = DateTime.UtcNow >= _nextScan;
            var scans = periodic ? selected.ToArray() : _reconcile.Where(kv => DateTime.UtcNow >= kv.Value).Select(kv => kv.Key).ToArray();
            HistoryStatus.Update(s =>
            {
                s.ReconcileReason = periodic ? "PeriodicSafetyReconcile" : scans.Length > 0 ? "ContainerRemoval" : "EventsOnly";
                s.ReconcileLibrariesTotal = scans.Length; s.ReconcileLibrariesCompleted = 0;
                s.PendingReconciles = _reconcile.Count;
            });
            if (scans.Length > 0)
            {
                var started = DateTime.UtcNow;
                foreach (var id in scans)
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    HistoryStatus.Update(s => { s.LedgerPhase = "Reconcile"; s.ScannedItems = 0; s.ScanTotal = null; });
                    try
                    {
                        var complete = Scan(plugin, id);
                        // Partial scans can safely Observe or apply exact deletion proofs, but are
                        // not reconciliation completion. Always persist their changes and retain a
                        // short retry, including incomplete periodic scans without a prior request.
                        _dirtyLibraries.Add(id);
                        if (complete) _reconcile.Remove(id);
                        else _reconcile[id] = DateTime.UtcNow.AddSeconds(30);
                    }
                    catch (Exception ex)
                    {
                        _reconcile[id] = DateTime.UtcNow.AddSeconds(30);
                        HistoryStatus.Error("Ledger", ex);
                        _log.ErrorException("EmbyHistory reconcile failed; retained previous presence for library " + id, ex);
                    }
                    HistoryStatus.Update(s => { s.ReconcileLibrariesCompleted++; s.PendingReconciles = _reconcile.Count; });
                }
                if (periodic)
                {
                    var took = DateTime.UtcNow - started;
                    _nextScan = DateTime.UtcNow + (took > TimeSpan.FromMinutes(1) ? TimeSpan.FromTicks(took.Ticks * 5) : TimeSpan.FromMinutes(5));
                }
                HistoryStatus.Update(s => s.LastReconcileUtc = DateTime.UtcNow);
            }
            var records = _store!.GetRecords();
            if (batch > 0 || scans.Length > 0 || _needsSync)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                _store.Flush();
            }
            HistoryStatus.Update(s =>
            {
                s.LedgerRecords = records.Count; s.RemovedRecords = records.Count(r => r.RemovedUtc.HasValue);
                if (batch > 0 || scans.Length > 0) s.LastLedgerUtc = DateTime.UtcNow;
                if (_reconcile.Count > 0 && s.LedgerPhase != "Error") s.LedgerPhase = "Held";
                else if (s.LedgerPhase != "Error" && s.LedgerPhase != "Held") { s.LedgerPhase = "Completed"; s.LedgerError = ""; }
            });
            if (_dirtyLibraries.Count > 0) _needsSync = true;
            if (_needsSync && (batch > 0 || DateTime.UtcNow >= _syncRetryAt))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                HistoryStatus.Update(s => { s.Stage = "Archive"; s.ArchivePhase = "Running"; s.ArchiveFailures = 0; });
                var syncSources = new HashSet<string>(_dirtyLibraries, StringComparer.Ordinal);
                if (syncSources.Count == 0) syncSources.UnionWith(selected);
                HistoryStatus.Update(s => s.ArchiveRecords = records.Count(r => syncSources.Contains(r.LibraryId)));
                var failed = _writer!.Sync(records, syncSources, (key, ex) =>
                {
                    HistoryStatus.Error("Archive", ex);
                    _log.Warn("EmbyHistory archive sync failed key={0}: {1}", key, ex.Message);
                });
                var changedSources = _writer.LastChangedLibraryIds.ToArray();
                _index.Queue(changedSources, _writer.LastChangedPaths);
                // Restart/configuration recovery also republishes existing cards (including upgraded NFOs).
                if (_startupIndex)
                {
                    // An empty newly selected source has nothing to publish yet. Do not turn a
                    // missing target attachment for an unused source into a false index failure.
                    _index.Queue(syncSources.Where(id => records.Any(r => r.LibraryId == id && r.ActiveItemIds.Count == 0 && r.RemovedUtc.HasValue)));
                    _startupIndex = false;
                }
                _needsSync = failed > 0;
                HistoryStatus.Update(s =>
                {
                    s.ArchiveFailures = failed; s.ArchiveChangedLibraries = changedSources.Length;
                    s.LastArchiveUtc = DateTime.UtcNow; s.ArchivePhase = failed > 0 ? "Error" : "Completed";
                    if (failed == 0) s.ArchiveError = "";
                });
                if (failed > 0)
                {
                    _syncRetryAt = DateTime.UtcNow.AddMinutes(5);
                    _log.Warn("EmbyHistory archive sync incomplete; {0} record(s) retry in 5 minutes", failed);
                }
                else
                {
                    _dirtyLibraries.ExceptWith(syncSources); _syncRetryAt = DateTime.MinValue;
                    PrunePosterCache(plugin);
                }
            }
            await _index.RefreshAsync(plugin, selected, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        { HistoryStatus.Update(s => s.Stage = "Stopped"); }
        catch (Exception ex)
        {
            var stage = HistoryStatus.Read().Stage;
            HistoryStatus.Error(stage is "Archive" or "Index" ? stage : "Ledger", ex);
            _log.ErrorException("EmbyHistory worker failed; history is not cleared", ex);
        }
        finally
        {
            HistoryStatus.Update(s =>
            {
                s.Busy = false; s.PendingEvents = _pending.Count;
                if (s.Stage is not "Disabled" and not "NoSources" and not "Stopped") s.Stage = "Idle";
            });
            Volatile.Write(ref _busy, 0);
            if (!_disposed && !_pending.IsEmpty)
            {
                Interlocked.Exchange(ref _eventWake, 0);
                try { _timer?.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10)); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void ApplyRemoval(Plugin plugin, Change change, ISet<string> selected)
    {
        if (!change.Container && change.LibraryId != null)
        {
            if (selected.Contains(change.LibraryId) && TracksItemType(ModeOf(change.LibraryId), change.IsEpisode))
            {
                _store!.MarkRemoved(change.ItemId, change.LibraryId);
                _dirtyLibraries.Add(change.LibraryId);
            }
            return;
        }
        var records = _store!.GetRecords();
        var owners = HistoryWorkPlanner.AffectedLibraries(records, selected, change.ItemId, change.SeriesId,
            change.SeasonNumber, change.Container, change.LibraryId == null ? Array.Empty<string>() : new[] { change.LibraryId });
        foreach (var libraryId in owners.Where(id => TracksItemType(ModeOf(id), change.IsEpisode || change.Container)))
        {
            if (!change.Container)
            {
                _store.MarkRemoved(change.ItemId, libraryId);
                _dirtyLibraries.Add(libraryId);
                continue;
            }
            _reconcile[libraryId] = DateTime.MinValue;
            _containerProofs[libraryId + "\n" + change.ItemId] = change with { LibraryId = libraryId };
        }
    }

    private void ApplyContainerProofs(string libraryId)
    {
        // Called only after ValidateSourcePaths succeeds. Keep exact event evidence across a
        // temporarily unavailable mount or a cascading Emby delete that has not committed yet.
        var records = _store!.GetRecords();
        foreach (var change in _containerProofs.Values.Where(c => c.LibraryId == libraryId))
            foreach (var record in records.Where(r => r.LibraryId == libraryId &&
                         HistoryWorkPlanner.MatchesContainer(r, change.SeriesId, change.SeasonNumber)))
                foreach (var itemId in record.ActiveItemIds)
                {
                    // The id query is independent of filesystem absence. Exact per-item evidence
                    // may be applied for the final series while empty-library inference stays off.
                    if (!long.TryParse(itemId, out var id) || _library.GetItemById(id) != null) continue;
                    _store.MarkRemoved(itemId, libraryId);
                    _dirtyLibraries.Add(libraryId);
                }
    }

    private bool RetireCompletedContainerProofs(string libraryId)
    {
        var records = _store!.GetRecords();
        var pending = false;
        foreach (var entry in _containerProofs.Where(kv => kv.Value.LibraryId == libraryId).ToArray())
        {
            var proof = entry.Value;
            if (records.Any(r => r.LibraryId == libraryId && r.ActiveItemIds.Count > 0 &&
                HistoryWorkPlanner.MatchesContainer(r, proof.SeriesId, proof.SeasonNumber))) pending = true;
            else _containerProofs.Remove(entry.Key);
        }
        return pending;
    }

    private void ClearContainerProofs(string libraryId)
    {
        foreach (var key in _containerProofs.Where(kv => kv.Value.LibraryId == libraryId).Select(kv => kv.Key).ToArray())
            _containerProofs.Remove(key);
    }

    private bool EnsureStore(Plugin plugin)
    {
        if (_store != null) return true;
        if (DateTime.UtcNow < _storeRetryAt) return false;
        try
        {
            ArchiveWriter.EnsureSafeDirectory(plugin.HistoryDataPath);
            ArchiveWriter.RejectLink(Path.Combine(plugin.HistoryDataPath, "state.json"));
            ArchiveWriter.RejectLink(Path.Combine(plugin.HistoryDataPath, "backup.json"));
            _store = new HistoryStore(plugin.HistoryDataPath);
            if (!string.IsNullOrEmpty(_store.RecoveryWarning)) _log.Warn("EmbyHistory " + _store.RecoveryWarning);
            if (_store.GetRecords().Count == 0 && HasArchivedCards(plugin))
                _log.Warn("EmbyHistory ledger is empty but archived cards exist; history may have been lost. Cards are kept, not deleted.");
            _storeFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            HistoryStatus.Error("Ledger", ex);
            _storeFailures++;
            QuarantineCorruptLedger(plugin);
            var delay = TimeSpan.FromSeconds(Math.Min(3600, 10 * Math.Pow(2, Math.Min(8, _storeFailures - 1))));
            _storeRetryAt = DateTime.UtcNow + delay;
            _log.ErrorException("EmbyHistory cannot open its ledger; retrying in " + (int)delay.TotalSeconds + " seconds", ex);
            return false;
        }
    }

    private bool HasArchivedCards(Plugin plugin)
    {
        try
        {
            var archive = Path.Combine(plugin.HistoryDataPath, "archive");
            return Directory.Exists(archive) && Directory.EnumerateFileSystemEntries(archive, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void QuarantineCorruptLedger(Plugin plugin)
    {
        foreach (var name in new[] { "state.json", "backup.json" })
        {
            try
            {
                var source = Path.Combine(plugin.HistoryDataPath, name);
                if (!File.Exists(source)) continue;
                var copy = source + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                if (!File.Exists(copy)) File.Copy(source, copy);
            }
            catch (Exception ex) { _log.Warn("EmbyHistory could not preserve a corrupt ledger copy: " + ex.Message); }
        }
    }

    private bool EnsureWriter(Plugin plugin)
    {
        if (_writer != null) return true;
        try { _writer = new ArchiveWriter(plugin.HistoryDataPath); return true; }
        catch (Exception ex) { HistoryStatus.Error("Archive", ex); _log.ErrorException("EmbyHistory archive root unavailable; cards are not rendered", ex); return false; }
    }

    private void Drain() { while (_pending.TryDequeue(out _)) { } }

    private IdentityMode ModeOf(string? libraryId) =>
        libraryId != null && _modes.TryGetValue(libraryId, out var mode) ? mode : IdentityMode.Code;

    /// <summary>A rule only claims the item type it can identify: movies, or episodes for series rules.</summary>
    private static bool TracksItemType(IdentityMode mode, bool isEpisode) =>
        isEpisode == (mode == IdentityMode.SeriesEpisode);

    /// <summary>Posters are only needed while a copy can still be rendered; keep the cache bounded.</summary>
    private void PrunePosterCache(Plugin plugin)
    {
        try
        {
            var directory = Path.Combine(plugin.HistoryDataPath, "posters");
            if (!Directory.Exists(directory)) return;
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            var records = _store!.GetRecords();
            var referencedPaths = HistoryWorkPlanner.ReferencedPosterPaths(records);
            foreach (var record in records)
            {
                referenced.Add(record.Snapshot.ItemId);
                foreach (var id in record.ActiveItemIds) referenced.Add(id);
            }
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(directory, "*.image"))
            {
                var itemId = Path.GetFileNameWithoutExtension(file);
                if (referenced.Contains(itemId) || referencedPaths.Contains(Path.GetFullPath(file))) continue;
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void TrimQueue()
    {
        while (_pending.Count > MaxPending && _pending.TryDequeue(out _)) { }
    }

    private void ValidateSourcePaths(Plugin plugin, string libraryId)
    {
        var folder = _library.GetVirtualFolders().FirstOrDefault(f => f.ItemId == libraryId);
        if (folder == null) throw new InvalidOperationException("Configured source library is missing");
        var locations = folder.Locations ?? Array.Empty<string>();
        var archive = Canonical(plugin.HistoryDataPath);
        if (locations.Length == 0 || locations.Any(p => !Directory.Exists(p)))
            throw new IOException("A source library path is not accessible; no removals inferred");
        if (locations.Any(p => IsWithin(Canonical(p), archive) || IsWithin(archive, Canonical(p))))
            throw new InvalidOperationException("History output and source library paths may not overlap");
    }

    /// <summary>True only when reconciliation is complete; partial observations still need short retry.</summary>
    private bool Scan(Plugin plugin, string libraryId)
    {
        ValidateSourcePaths(plugin, libraryId);
        if (_containerProofs.Values.Any(c => c.LibraryId == libraryId)) ApplyContainerProofs(libraryId);
        var snapshots = new List<MediaSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        var repeated = false;
        // A series rule must enumerate episodes; it cannot use movies as its unit of history.
        var itemTypes = ModeOf(libraryId) == IdentityMode.SeriesEpisode ? new[] { "Episode" } : new[] { "Movie" };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            snapshots.Clear(); seen.Clear(); total = null; repeated = false;
            var offset = 0;
            while (true)
            {
                var page = _library.GetItemsResult(new InternalItemsQuery
                {
                    ParentIds = new[] { long.Parse(libraryId) }, Recursive = true, IncludeItemTypes = itemTypes,
                    StartIndex = offset, Limit = PageSize, EnableTotalRecordCount = offset == 0,
                    OrderBy = new[] { ("SortName", SortOrder.Ascending) }
                });
                if (offset == 0) { total = page.TotalRecordCount; HistoryStatus.Update(s => s.ScanTotal = total); }
                foreach (var item in page.Items)
                {
                    if (IsOwnItem(item, plugin)) throw new InvalidOperationException("An archive item appeared in the source scan");
                    if (!seen.Add(item.InternalId.ToString())) { repeated = true; break; }
                    var snapshot = Snapshot(item, libraryId);
                    try { CacheArtwork(snapshot, plugin.HistoryDataPath); }
                    catch (Exception ex) { snapshot.PosterPath = ""; _log.Warn("EmbyHistory poster cache skipped item={0}: {1}", snapshot.ItemId, ex.Message); }
                    snapshots.Add(snapshot);
                }
                HistoryStatus.Update(s => s.ScannedItems = snapshots.Count);
                if (repeated) break;
                offset += page.Items.Length;
                if (page.Items.Length < PageSize) break;
            }
            if (!repeated) break;
            _log.Warn("EmbyHistory paging repeated an item in library {0}; retrying once", libraryId);
        }
        if (repeated)
        {
            _log.Warn("EmbyHistory paging unstable for library {0}; presence retained this cycle", libraryId);
            _store!.Reconcile(libraryId, snapshots, false, ModeOf(libraryId));
            HistoryStatus.Update(s => { s.LedgerPhase = "Held"; s.LedgerError = "分页不稳定：仅记录已见条目，30 秒后重试"; });
            return false;
        }
        if (snapshots.Count == 0 && !plugin.Configuration.AllowEmptyLibraryReconcile)
        {
            _log.Warn("EmbyHistory empty scan held for library {0}; not inferring mass removal", libraryId);
            var retainedActive = _store!.GetRecords().Any(r => r.LibraryId == libraryId && r.ActiveItemIds.Count > 0);
            if (!retainedActive)
            {
                // All known removals were already proven by exact events, not inferred from emptiness.
                ClearContainerProofs(libraryId);
                _reconcile.Remove(libraryId);
            }
            HistoryStatus.Update(s =>
            {
                s.LedgerPhase = retainedActive ? "Held" : "Completed";
                s.LedgerError = retainedActive ? "空库保护：未推断整库删除；确切删除事件已单独处理" : "";
            });
            return !retainedActive;
        }
        // If the library shrank during paging, an item may have shifted across a page boundary.
        // Observing is safe; inferring removals is not.
        var complete = total == null || seen.Count >= total;
        if (!complete) _log.Warn("EmbyHistory library {0} changed during scan ({1}/{2}); no removals inferred", libraryId, seen.Count, total);
        _store!.Reconcile(libraryId, snapshots, complete, ModeOf(libraryId));
        // A complete PAGE enumeration is not proof that Emby's cascading delete has finished.
        // Keep exact container evidence until its known child records have no active copies left.
        var cascadePending = complete && RetireCompletedContainerProofs(libraryId);
        if (!complete) HistoryStatus.Update(s => { s.LedgerPhase = "Held"; s.LedgerError = "扫描期间条目数变化：仅记录已见条目，30 秒后重试"; });
        else if (cascadePending) HistoryStatus.Update(s => { s.LedgerPhase = "Held"; s.LedgerError = "整剧或整季删除尚未提交完毕：保留删除证据，30 秒后重试"; });
        _log.Info("EmbyHistory scan library={0} count={1} complete={2} cascadePending={3}", libraryId, snapshots.Count, complete, cascadePending);
        return complete && !cascadePending;
    }

    private static string Canonical(string path)
    {
        try
        {
            var resolved = new DirectoryInfo(path).ResolveLinkTarget(true);
            return Path.GetFullPath(resolved?.FullName ?? path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Path.GetFullPath(path); }
    }

    private static bool IsWithin(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(parent, comparison) || path.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private MediaSnapshot Snapshot(BaseItem item, string libraryId)
    {
        var result = new MediaSnapshot
        {
            ItemId = item.InternalId.ToString(), LibraryId = libraryId, Name = item.Name ?? "",
            OriginalTitle = item.OriginalTitle ?? "", Overview = item.Overview ?? "", Year = item.ProductionYear,
            Path = item.Path ?? "", PosterPath = item.GetImageInfo(ImageType.Primary, 0)?.Path ?? "",
            Genres = item.Genres?.ToList() ?? new(), Tags = item.Tags?.ToList() ?? new(),
            ProviderIds = item.ProviderIds == null ? new() : new(item.ProviderIds)
        };
        if (item is Episode episode)
        {
            // Episodes are identified by their series, so the series context must be captured now:
            // after the series is gone there is nothing left to look up.
            result.SeriesId = episode.SeriesId > 0 ? episode.SeriesId.ToString() : "";
            result.SeriesName = episode.SeriesName ?? "";
            result.SeasonNumber = episode.ParentIndexNumber;
            result.EpisodeNumber = episode.IndexNumber;
            var series = episode.SeriesId > 0 ? _library.GetItemById(episode.SeriesId) : null;
            if (series != null)
            {
                result.SeriesYear = series.ProductionYear;
                if (string.IsNullOrWhiteSpace(result.SeriesName)) result.SeriesName = series.Name ?? "";
                if (series.ProviderIds != null) result.SeriesProviderIds = new(series.ProviderIds);
                result.SeriesPosterPath = series.GetImageInfo(ImageType.Primary, 0)?.Path ?? "";
            }
        }
        foreach (var person in _library.GetItemPeople(item))
            if (person.Type == PersonType.Actor) result.Actors.Add(new ActorSnapshot { Name = person.Name ?? "", Role = person.Role ?? "" });
        return result;
    }

    private void Observe(Plugin plugin, MediaSnapshot snapshot, IdentityMode mode)
    {
        try { CacheArtwork(snapshot, plugin.HistoryDataPath); }
        catch (Exception ex) { _log.Warn("EmbyHistory poster cache skipped item={0}: {1}", snapshot.ItemId, ex.Message); }
        _store!.Observe(snapshot, mode);
    }

    internal static void CachePoster(MediaSnapshot snapshot, string dataPath) => PosterCache.Capture(snapshot, dataPath);

    /// <summary>Episode posters plus the series artwork the series card needs.</summary>
    internal static void CacheArtwork(MediaSnapshot snapshot, string dataPath)
    {
        PosterCache.Capture(snapshot, dataPath);
        PosterCache.CaptureSeries(snapshot, dataPath);
    }

    public void Dispose()
    {
        Task worker;
        lock (_workerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            worker = _workerTask;
        }
        _library.ItemAdded -= AddedOrUpdated; _library.ItemUpdated -= AddedOrUpdated; _library.ItemRemoved -= Removed;
        _shutdown.Cancel();
        // A bounded shutdown wait, never while holding a lock needed by the worker or refresh callbacks.
        try
        {
            if (!worker.Wait(TimeSpan.FromSeconds(5)))
            {
                _log.Warn("EmbyHistory shutdown continued while a tracked worker was still running");
                return;
            }
            if (_store != null && _needsSync) _store.Flush();
            _shutdown.Dispose();
            HistoryStatus.Update(s => { s.Busy = false; s.Stage = "Stopped"; });
        }
        catch (Exception ex) { _log.ErrorException("EmbyHistory final flush/shutdown failed", ex); }
    }
}
