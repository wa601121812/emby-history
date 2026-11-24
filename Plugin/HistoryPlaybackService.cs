using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace EmbyHistory;

public sealed record HistoryPlaybackIdentity(string LibraryId, string RecordKey);
public sealed record HistoryPlaybackResult(int Captured = 0, int Restored = 0, int Skipped = 0, int Failed = 0, int Pending = 0);

/// <summary>
/// Independent opt-in playback sidecar. All source access is read-only; writes are restricted to
/// positively identified owned history .disc items. Call RestoreRecords AFTER every history refresh.
/// Every public operation isolates failures so playback cannot prevent the presence ledger committing.
/// </summary>
public sealed class HistoryPlaybackService : IDisposable
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _data;
    private readonly HistoryPlaybackStore _store;
    private readonly string _archive;
    private readonly Action<string, Exception>? _error;
    private readonly Func<User, BaseItem, bool> _visible;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<HistoryPlaybackIdentity, byte>> _identities = new();
    private readonly object _lifecycle = new();
    private Func<IEnumerable<HistoryRecord>>? _records;
    private bool _started, _disposed;
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public HistoryPlaybackService(ILibraryManager library, IUserManager users, IUserDataManager data,
        string dataPath, Action<string, Exception>? onError = null, Func<User, BaseItem, bool>? canRead = null)
    {
        _library = library; _users = users; _data = data; _store = new HistoryPlaybackStore(dataPath);
        _archive = Path.Combine(Path.GetFullPath(dataPath), "archive"); _error = onError;
        _visible = canRead ?? ((user, item) => item.IsVisible(user));
    }
    public HistoryPlaybackStore Store => _store;
    private void Error(string stage, Exception ex) { try { _error?.Invoke(stage, ex); } catch { } }
    private static bool Supported(BaseItem item) => item is Movie or Episode;
    private bool OwnPath(BaseItem item) => !string.IsNullOrEmpty(item.Path) && Within(_archive, item.Path);
    private bool Authorized(User user, BaseItem item, string source)
    {
        if (user.InternalId <= 0 || user.Id == Guid.Empty || _users.GetUserPolicy(user).IsDisabled || OwnPath(item)) return false;
        return _library.GetCollectionFolders(item).Any(f => f.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture) == source) && _visible(user, item);
    }
    private static void Validate(string libraryId, string historyKey)
    {
        if (!HistoryPlaybackStore.IsCanonicalId(libraryId) || string.IsNullOrEmpty(historyKey) || historyKey.Length > 512)
            throw new ArgumentException("Playback identity requires an exact canonical source id and ledger key");
    }
    private void Register(BaseItem item, string source, string key)
    {
        Validate(source, key);
        _identities.GetOrAdd(item.InternalId, _ => new()).TryAdd(new(source, key), 0);
    }
    private bool Put(User user, BaseItem item, string source, string key, UserItemData? value, string evidence)
    {
        if (value == null) return false;
        var nativeKey = value.Key;
        if (string.IsNullOrEmpty(nativeKey)) nativeKey = item.GetUserDataKey(null!);
        return _store.Put(new HistoryPlaybackSnapshot
        {
            LibraryId = source, RecordKey = key, UserId = user.InternalId, UserGuid = user.Id,
            SourceItemId = item.InternalId, SourceUserDataKey = nativeKey ?? "",
            DurationTicks = item.RunTimeTicks > 0 ? item.RunTimeTicks : null,
            PositionTicks = Math.Max(0, value.PlaybackPositionTicks), Played = value.Played,
            PlayCount = Math.Max(0, value.PlayCount), LastPlayed = value.LastPlayedDate,
            PlaystateModified = value.PlaystateLastModified, CapturedUtc = DateTimeOffset.UtcNow, Evidence = evidence
        });
    }
    /// <summary>After ledger Observe resolves its canonical key; captures ONLY users who can read this actual source item.</summary>
    public HistoryPlaybackResult Capture(BaseItem item, string libraryId, string historyKey) => CaptureCore(item, libraryId, historyKey, true);
    private HistoryPlaybackResult CaptureCore(BaseItem item, string libraryId, string historyKey, bool flush)
    {
        int captured = 0, skipped = 0, failed = 0;
        try
        {
            if (_disposed || !Supported(item) || item.InternalId <= 0 || OwnPath(item)) return new(Skipped: 1);
            Register(item, libraryId, historyKey);
            foreach (var user in _users.GetUserList(new UserQuery()))
            {
                try
                {
                    if (!Authorized(user, item, libraryId)) { skipped++; continue; }
                    if (Put(user, item, libraryId, historyKey, _data.GetUserData(user, item), "source-read")) captured++; else skipped++;
                }
                catch (Exception ex) { failed++; Error("CaptureUser", ex); }
            }
            if (flush) _store.Flush();
        }
        catch (Exception ex) { failed++; Error("Capture", ex); }
        return new(captured, Skipped: skipped, Failed: failed);
    }
    /// <summary>Startup inventory/backfill of still-existing source items. No assumption that deleted userdata survives.</summary>
    public HistoryPlaybackResult CaptureRecords(IEnumerable<HistoryRecord> records)
    {
        int captured = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var record in records)
            {
                try
                {
                    foreach (var raw in record.ActiveItemIds.Append(record.Snapshot.ItemId).Distinct(StringComparer.Ordinal))
                    {
                        if (!HistoryPlaybackStore.IsCanonicalId(raw)) { skipped++; continue; }
                        var item = _library.GetItemById(long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture));
                        if (item == null) { skipped++; continue; }
                        var result = CaptureCore(item, record.LibraryId, record.Key, false);
                        captured += result.Captured; skipped += result.Skipped; failed += result.Failed;
                    }
                }
                catch (Exception ex) { failed++; Error("CaptureRecord", ex); }
            }
        }
        catch (Exception ex) { failed++; Error("CaptureRecords", ex); }
        try { _store.Flush(); } catch (Exception ex) { failed++; Error("CaptureRecordsFlush", ex); }
        return new(captured, Skipped: skipped, Failed: failed);
    }
    /// <summary>
    /// Explicit forensic recovery only: the caller must supply independently verified pre-removal
    /// per-user authorization. GetUserData(user, oldNumericId) must actually return playback evidence.
    /// Duration is NOT invented (MediaSnapshot currently has none); existing sidecar duration survives.
    /// </summary>
    public HistoryPlaybackResult RecoverRemovedRecords(IEnumerable<HistoryRecord> records, Func<User, HistoryRecord, bool> wasAuthorized)
    {
        int captured = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var record in records.Where(r => r.RemovedUtc.HasValue && r.ActiveItemIds.Count == 0))
            {
                Validate(record.LibraryId, record.Key);
                if (!HistoryPlaybackStore.IsCanonicalId(record.Snapshot.ItemId)) { skipped++; continue; }
                var sourceId = long.Parse(record.Snapshot.ItemId, System.Globalization.CultureInfo.InvariantCulture);
                if (_library.GetItemById(sourceId) != null) { skipped++; continue; }
                foreach (var user in _users.GetUserList(new UserQuery()))
                {
                    try
                    {
                        if (_users.GetUserPolicy(user).IsDisabled || !wasAuthorized(user, record)) { skipped++; continue; }
                        var value = _data.GetUserData(user, sourceId);
                        if (value == null) { skipped++; continue; }
                        var row = new HistoryPlaybackSnapshot { LibraryId = record.LibraryId, RecordKey = record.Key,
                            UserId = user.InternalId, UserGuid = user.Id, SourceItemId = sourceId,
                            SourceUserDataKey = value.Key ?? "", PositionTicks = Math.Max(0, value.PlaybackPositionTicks),
                            Played = value.Played, PlayCount = Math.Max(0, value.PlayCount), LastPlayed = value.LastPlayedDate,
                            PlaystateModified = value.PlaystateLastModified, CapturedUtc = DateTimeOffset.UtcNow, Evidence = "retained-userdata-by-id" };
                        if (!row.HasPlaybackEvidence || string.IsNullOrEmpty(row.SourceUserDataKey)) { skipped++; continue; }
                        if (_store.Put(row)) captured++; else skipped++;
                    }
                    catch (Exception ex) { failed++; Error("RecoverUser", ex); }
                }
            }
            _store.Flush();
        }
        catch (Exception ex) { failed++; Error("RecoverRemovedRecords", ex); }
        return new(captured, Skipped: skipped, Failed: failed);
    }
    /// <summary>Idempotent subscription; provider must return only enabled/safe source records, never historical targets as sources.</summary>
    public void Start(Func<IEnumerable<HistoryRecord>> records)
    {
        lock (_lifecycle)
        {
            if (_disposed || _started) return;
            _records = records;
            try { _data.UserDataSaved += Saved; _library.ItemRemoved += Removing; _started = true; }
            catch (Exception ex) { try { _data.UserDataSaved -= Saved; _library.ItemRemoved -= Removing; } catch { } Error("Start", ex); }
        }
    }
    private IEnumerable<HistoryPlaybackIdentity> Identities(BaseItem item)
    {
        var set = new HashSet<HistoryPlaybackIdentity>();
        if (_records != null)
        {
            foreach (var record in _records())
                if (record.ActiveItemIds.Contains(item.InternalId.ToString()) || record.Snapshot.ItemId == item.InternalId.ToString())
                    set.Add(new(record.LibraryId, record.Key));
        }
        else if (_identities.TryGetValue(item.InternalId, out var known)) set.UnionWith(known.Keys);
        return set;
    }
    private void FlushThrottled()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            if (now - _lastFlushUtc < TimeSpan.FromSeconds(2)) return;
            _store.Flush();
            _lastFlushUtc = now;
        }
    }

    private void Saved(object? sender, UserDataSaveEventArgs e)
    {
        if (_disposed || e.Item == null || e.User == null || !Supported(e.Item) || OwnPath(e.Item)) return;
        try
        {
            foreach (var identity in Identities(e.Item))
                if (Authorized(e.User, e.Item, identity.LibraryId)) Put(e.User, e.Item, identity.LibraryId, identity.RecordKey, e.UserData, "UserDataSaved:" + e.SaveReason);
            // Bounded durability: flush at most once every 2s instead of rewriting the whole file
            // per event (8k works → O(n²)), and Dispose always flushes what is left.
            FlushThrottled();
        }
        catch (Exception ex) { Error("UserDataSaved", ex); }
    }
    // SDK 4.10.2 has no public ItemRemoving. Explicit hook for an integration with a real
    // pre-delete seam; ItemRemoved is best effort only and never establishes pre-delete timing.
    public void CaptureRemoving(BaseItem item) => Removing(this, new ItemChangeEventArgs(_library) { Item = item });
    private void Removing(object? sender, ItemChangeEventArgs e)
    {
        if (_disposed || e.Item == null || OwnPath(e.Item)) return;
        try
        {
            if (Supported(e.Item))
            {
                foreach (var id in Identities(e.Item)) Capture(e.Item, id.LibraryId, id.RecordKey);
                return;
            }
            if (e.Item is not Folder || _records == null) return;
            // Container callbacks may be the ONLY pre-delete event for all descendants.
            var affected = _records().Where(r => r.ActiveItemIds.Count > 0 &&
                ((e.Item is Series && r.Snapshot.SeriesId == e.Item.InternalId.ToString()) ||
                 (e.Item is Season && r.Snapshot.SeriesId == e.Item.SeriesId.ToString() && r.Snapshot.SeasonNumber == e.Item.IndexNumber) ||
                 (!string.IsNullOrEmpty(e.Item.Path) && !string.IsNullOrEmpty(r.Snapshot.Path) && Within(e.Item.Path, r.Snapshot.Path)))).ToArray();
            CaptureRecords(affected);
        }
        catch (Exception ex) { Error("ItemRemoving", ex); }
    }
    public HistoryPlaybackResult RestoreRecords(IEnumerable<HistoryRecord> records, CancellationToken cancellationToken = default)
    {
        int restored = 0, skipped = 0, failed = 0, pending = 0;
        try
        {
            var playbackRows = _store.Read().GroupBy(r => (r.LibraryId, r.RecordKey)).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var group in records.Where(r => r.RemovedUtc.HasValue && r.ActiveItemIds.Count == 0).GroupBy(r => r.LibraryId))
            {
                try
                {
                    Validate(group.Key, "lookup");
                    var root = Path.Combine(_archive, group.Key);
                    var targets = _library.GetVirtualFolders().Where(v => v.ItemId != group.Key && HistoryPlaybackStore.IsCanonicalId(v.ItemId) &&
                        (v.Locations ?? Array.Empty<string>()).Any(location => Within(location, root))).Select(v => long.Parse(v.ItemId)).Distinct().ToArray();
                    // No mounted target is a pending configuration/index state, never a silent success.
                    if (targets.Length == 0) { pending += group.Count(); skipped += group.Count(); continue; }
                    var candidates = _library.GetItemList(new InternalItemsQuery { ParentIds = targets, Recursive = true, IncludeItemTypes = new[] { "Movie", "Episode" } });
                    var byKey = candidates.Where(i => Within(root, i.Path)).GroupBy(HistoryProviderKey).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                    foreach (var record in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var matching = byKey.GetValueOrDefault(record.Key, Array.Empty<BaseItem>()).Where(i => Matches(i, record)).ToArray();
                        if (matching.Length != 1) { pending++; skipped++; continue; } // pending index or ambiguous: retry next worker
                        var result = RestoreCore(record, matching[0], playbackRows.GetValueOrDefault((record.LibraryId, record.Key), Array.Empty<HistoryPlaybackSnapshot>()), cancellationToken);
                        restored += result.Restored; skipped += result.Skipped; failed += result.Failed;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed++; Error("RestoreLibrary", ex); }
            }
        }
        catch (Exception ex) { failed++; Error("RestoreRecords", ex); }
        return new(Restored: restored, Skipped: skipped, Failed: failed, Pending: pending);
    }
    /// <summary>Only a verified owned archive item is writable; never pass a guessed title/provider lookup result.</summary>
    public HistoryPlaybackResult Restore(HistoryRecord record, BaseItem historyItem, CancellationToken cancellationToken = default)
        => RestoreCore(record, historyItem, null, cancellationToken);
    private HistoryPlaybackResult RestoreCore(HistoryRecord record, BaseItem historyItem, HistoryPlaybackSnapshot[]? savedRows, CancellationToken cancellationToken)
    {
        int restored = 0, skipped = 0, failed = 0;
        try
        {
            if (_disposed || !record.RemovedUtc.HasValue || record.ActiveItemIds.Count != 0 || !Matches(historyItem, record)) return new(Skipped: 1);
            var rows = savedRows ?? _store.Read().Where(r => r.LibraryId == record.LibraryId && r.RecordKey == record.Key).ToArray();
            var duration = rows.Where(r => r.DurationTicks > 0).OrderByDescending(r => r.CapturedUtc).ThenBy(r => r.SourceItemId).FirstOrDefault()?.DurationTicks;
            if (duration > 0 && historyItem.RunTimeTicks != duration)
            {
                var previousDuration = historyItem.RunTimeTicks;
                try
                {
                    historyItem.RunTimeTicks = duration;
                    _library.UpdateItem(historyItem, null!, ItemUpdateType.MetadataEdit);
                    if (_library.GetItemById(historyItem.InternalId)?.RunTimeTicks != duration) throw new IOException("History duration read-back failed");
                }
                catch { historyItem.RunTimeTicks = previousDuration; throw; }
            }
            foreach (var group in rows.Where(r => r.HasPlaybackEvidence).GroupBy(r => r.UserId))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // No max(position) or copying the owner's status: latest dated playback per SAME user wins.
                    var row = group.OrderByDescending(r => r.LastPlayed ?? DateTimeOffset.MinValue).ThenByDescending(r => r.PlaystateModified ?? DateTimeOffset.MinValue).ThenByDescending(r => r.CapturedUtc).ThenBy(r => r.SourceItemId).First();
                    var user = _users.GetUserById(row.UserId);
                    if (user == null || user.Id != row.UserGuid || _users.GetUserPolicy(user).IsDisabled || !_visible(user, historyItem)) { skipped++; continue; }
                    var current = _data.GetUserData(user, historyItem);
                    if (current == null) { skipped++; continue; }
                    if (Equal(current, row)) { skipped++; continue; }
                    if (current.PlaystateLastModified > row.CapturedUtc) { skipped++; continue; }
                    var targetKey = current.Key;
                    if (string.IsNullOrEmpty(targetKey)) targetKey = historyItem.GetUserDataKey(null!);
                    // SDK generates provider/series keys, NOT the settable UserDataKey property.
                    // Shared keys can expose source state directly; never write them (even after deletion).
                    if (string.IsNullOrEmpty(targetKey) || rows.Any(r => string.IsNullOrEmpty(r.SourceUserDataKey) || r.SourceUserDataKey == targetKey))
                        throw new InvalidOperationException("History/source native userdata key collision or missing key; refusing source-state write");
                    var next = Clone(current); next.Key = targetKey; next.PlaybackPositionTicks = row.PositionTicks;
                    next.Played = row.Played; next.PlayCount = row.PlayCount; next.LastPlayedDate = row.LastPlayed;
                    next.PlaystateLastModified = row.PlaystateModified ?? row.CapturedUtc;
                    _data.SaveUserData(user, historyItem, next, UserDataSaveReason.Import, cancellationToken);
                    if (!Equal(_data.GetUserData(user, historyItem), row)) throw new IOException("History userdata read-back failed");
                    restored++;
                }
                catch (Exception ex) { failed++; Error("RestoreUser", ex); }
            }
        }
        catch (Exception ex) { failed++; Error("Restore", ex); }
        return new(Restored: restored, Skipped: skipped, Failed: failed);
    }
    private static bool Equal(UserItemData? data, HistoryPlaybackSnapshot row) => data != null && data.PlaybackPositionTicks == row.PositionTicks && data.Played == row.Played && data.PlayCount == row.PlayCount && data.LastPlayedDate == row.LastPlayed;
    private static UserItemData Clone(UserItemData value) => new() { Key = value.Key, PlaybackPositionTicks = value.PlaybackPositionTicks,
        Played = value.Played, PlayCount = value.PlayCount, LastPlayedDate = value.LastPlayedDate, PlaystateLastModified = value.PlaystateLastModified,
        IsFavorite = value.IsFavorite, Rating = value.Rating, RatingLastModified = value.RatingLastModified, HideFromResume = value.HideFromResume,
        AudioStreamIndex = value.AudioStreamIndex, SubtitleStreamIndex = value.SubtitleStreamIndex, DateLastSearched = value.DateLastSearched };
    private bool Matches(BaseItem item, HistoryRecord record)
    {
        try
        {
            Validate(record.LibraryId, record.Key);
            var root = Path.Combine(_archive, record.LibraryId);
            if (!Supported(item) || item.InternalId <= 0 || !Within(root, item.Path) || !item.Path.EndsWith(".disc", StringComparison.OrdinalIgnoreCase) ||
                HistoryProviderKey(item) != record.Key) return false;
            if ((record.Mode == IdentityMode.SeriesEpisode) != (item is Episode)) return false;
            HistoryPlaybackStore.SafePath(item.Path);
            var dir = Path.GetDirectoryName(item.Path)!;
            var marker = Path.Combine(dir, ".embyhistory-owned"); var manifest = Path.Combine(dir, ".embyhistory-files.json");
            HistoryPlaybackStore.SafePath(marker); HistoryPlaybackStore.SafePath(manifest);
            if (!File.Exists(marker) || !File.Exists(manifest) || File.ReadAllText(marker).Trim() != (item is Episode ? record.SeriesKey : record.Key)) return false;
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(manifest));
            var nfo = item is Movie ? Path.Combine(dir, "movie.nfo") : Path.ChangeExtension(item.Path, ".nfo");
            HistoryPlaybackStore.SafePath(nfo);
            foreach (var file in new[] { item.Path, nfo })
                if (hashes == null || !hashes.TryGetValue(Path.GetFileName(file), out var hash) ||
                    !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
            using var reader = XmlReader.Create(nfo, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            var xml = XDocument.Load(reader).Root;
            return xml?.Elements("uniqueid").Any(e => (string?)e.Attribute("type") == "embyhistory" && e.Value == record.Key) == true;
        }
        catch (Exception ex) { Error("ValidateHistoryItem", ex); return false; }
    }
    private static string HistoryProviderKey(BaseItem item) => item.ProviderIds?.FirstOrDefault(p => p.Key.Equals("embyhistory", StringComparison.OrdinalIgnoreCase)).Value ?? "";
    private static bool Within(string root, string path)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); path = Path.GetFullPath(path);
            return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch { return false; }
    }
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return; _disposed = true;
            if (_started) { try { _data.UserDataSaved -= Saved; } catch (Exception ex) { Error("DisposeSaved", ex); } try { _library.ItemRemoved -= Removing; } catch (Exception ex) { Error("DisposeRemoving", ex); } }
            _started = false; _records = null;
            try { _store.Flush(); } catch (Exception ex) { Error("DisposeFlush", ex); }
        }
    }
}
