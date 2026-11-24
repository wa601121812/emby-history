using System.Text.Json;

namespace EmbyHistory.Core;

/// <summary>
/// A thread-safe, single-writer historical ledger. Mutations stay in memory until Flush.
/// Complete reconciliation accepts an explicitly empty library; the adapter must gate suspicious scans.
/// </summary>
public sealed class HistoryStore
{
    /// <summary>Bump when identification rules change; existing items are then re-keyed, not deleted.</summary>
    public const int IdentityScheme = 3;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _statePath;
    private readonly Dictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string LibraryId, string ItemId), string> _itemKeys = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private byte[]? _lastPersisted;
    private long _sequence;
    private bool _dirty;
    private bool _schemeChanged;

    public string RecoveryWarning { get; } = "";

    public HistoryStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A storage directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory);
        _statePath = Path.Combine(_directory, "state.json");
        var primary = _statePath;
        var backup = Path.Combine(_directory, "backup.json");
        if (!Path.Exists(primary) && !Path.Exists(backup)) return;
        if (TryRead(primary, out var state, out var bytes, out var primaryError))
        {
            Load(state!); _lastPersisted = bytes; _schemeChanged = state!.Scheme != IdentityScheme; return;
        }
        if (!TryRead(backup, out state, out bytes, out var backupError))
            throw new InvalidDataException("History ledger and backup are missing or invalid; refusing to create an empty history.",
                new AggregateException(primaryError!, backupError!));
        Load(state!);
        _schemeChanged = state!.Scheme != IdentityScheme;
        // Restoration is itself atomic. IO errors are deliberately visible to the caller.
        AtomicWrite(primary, bytes!);
        _lastPersisted = bytes;
        RecoveryWarning = "Recovered state.json from backup.json. The primary ledger was missing or invalid: " + primaryError!.Message;
    }

    public IReadOnlyList<HistoryRecord> GetRecords()
    {
        lock (_gate)
            return _entries.Values.OrderBy(e => e.Record.Key, StringComparer.Ordinal)
                .Select(e => SnapshotTools.Clone(e.Record)).ToList().AsReadOnly();
    }

    public void Observe(MediaSnapshot snapshot)
    {
        Observe(snapshot, IdentityMode.Code);
    }

    public void Observe(MediaSnapshot snapshot, IdentityMode mode)
    {
        lock (_gate) ObserveCore(SnapshotTools.Prepare(snapshot), DateTime.UtcNow, mode);
    }

    public void MarkRemoved(string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        lock (_gate) RemoveCore(itemId, DateTime.UtcNow);
    }

    public void MarkRemoved(string itemId, string libraryId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (string.IsNullOrWhiteSpace(libraryId)) throw new ArgumentException("LibraryId is required.", nameof(libraryId));
        lock (_gate) RemoveCore(itemId, libraryId, DateTime.UtcNow);
    }

    public void Reconcile(string libraryId, IEnumerable<MediaSnapshot> snapshots, bool complete)
        => Reconcile(libraryId, snapshots, complete, IdentityMode.Code);

    public void Reconcile(string libraryId, IEnumerable<MediaSnapshot> snapshots, bool complete, IdentityMode mode)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) throw new ArgumentException("LibraryId is required.", nameof(libraryId));
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (_gate)
        {
            // No mutation until enumeration and every input check have succeeded.
            var prepared = snapshots.Select(SnapshotTools.Prepare).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in prepared)
            {
                if (!string.Equals(snapshot.LibraryId, libraryId, StringComparison.Ordinal))
                    throw new ArgumentException("Every snapshot must belong to the reconciled library.", nameof(snapshots));
                if (!seen.Add(snapshot.ItemId)) throw new ArgumentException("Duplicate ItemId in reconciliation.", nameof(snapshots));
                // Extraction is also validated up front (including regex timeouts).
                _ = SnapshotTools.Identify(snapshot, mode);
            }
            var now = DateTime.UtcNow;
            foreach (var snapshot in prepared) ObserveCore(snapshot, now, mode);
            if (!complete) return;
            var missing = _entries.Values.Where(e => e.Record.LibraryId == libraryId)
                .SelectMany(e => e.Record.ActiveItemIds).Where(id => !seen.Contains(id)).ToList();
            foreach (var id in missing) RemoveCore(id, libraryId, now);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            // Nothing changed and a ledger already exists: leave the file (and its mtime) untouched.
            if (!_dirty && File.Exists(_statePath)) return;
            var state = new FileState
            {
                Version = 1, Sequence = _sequence, Scheme = IdentityScheme,
                Entries = _entries.Values.OrderBy(e => e.Record.Key, StringComparer.Ordinal).ToList()
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            // Only bytes previously validated or generated by this store become backups.
            // First commit seeds the backup too, so a single successful flush is recoverable.
            AtomicWrite(Path.Combine(_directory, "backup.json"), _lastPersisted ?? bytes);
            AtomicWrite(_statePath, bytes);
            _lastPersisted = bytes;
            _dirty = false;
        }
    }

    private void ObserveCore(MediaSnapshot snapshot, DateTime now, IdentityMode mode)
    {
        var (code, identity) = SnapshotTools.Identify(snapshot, mode);
        snapshot.Code = code;
        var key = SnapshotTools.Key(snapshot.LibraryId, identity);
        var itemKey = (snapshot.LibraryId, snapshot.ItemId);
        DateTime? carriedFirstSeen = null;
        if (_itemKeys.TryGetValue(itemKey, out var oldKey) && oldKey != key)
        {
            if (_entries.TryGetValue(oldKey, out var prior) && prior.Record.Mode != mode && SamePath(oldKey, snapshot))
            {
                // The library's identity rule was reconfigured. The file never left the library, so
                // re-key it silently instead of reporting a fake "removed" title.
                carriedFirstSeen = prior.Record.FirstSeenUtc;
                DetachItem(oldKey, snapshot.ItemId, now);
            }
            else if (IsWeakIdentity(identity) && IsStrongRecordForKey(oldKey, snapshot))
            {
                // The same file re-read before Emby re-scraped it: do not split a known title into a
                // second, path-identified record just because its metadata is temporarily thin.
                var strong = _entries[oldKey];
                key = oldKey; code = strong.Record.Code; identity = strong.Identity; snapshot.Code = code;
            }
            else if (_schemeChanged && SamePath(oldKey, snapshot))
            {
                // The identification rules themselves changed (plugin upgrade). The file never left
                // the library, so re-key it silently instead of reporting a fake "removed" title.
                if (_entries.TryGetValue(oldKey, out var stale)) carriedFirstSeen = stale.Record.FirstSeenUtc;
                DetachItem(oldKey, snapshot.ItemId, now);
            }
            else if (CanMigrateIdentity(oldKey, snapshot))
                MigrateEntry(oldKey, key, identity, code);
            else
                RemoveCore(snapshot.ItemId, snapshot.LibraryId, now);
        }
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new StoredEntry
            {
                Identity = identity,
                Record = new HistoryRecord
                {
                    Key = key, LibraryId = snapshot.LibraryId, Code = code,
                    SeriesKey = mode == IdentityMode.SeriesEpisode ? SnapshotTools.SeriesKeyOf(snapshot, snapshot.LibraryId) : "",
                    Snapshot = snapshot,
                    Mode = mode,
                    FirstSeenUtc = carriedFirstSeen is { } seen && seen < now ? seen : now,
                    LastSeenUtc = now
                }
            };
            _entries.Add(key, entry);
        }
        else
        {
            entry.Record.Mode = mode;
            if (mode == IdentityMode.SeriesEpisode && entry.Record.SeriesKey.Length == 0)
                entry.Record.SeriesKey = SnapshotTools.SeriesKeyOf(snapshot, snapshot.LibraryId);
            entry.Items.TryGetValue(snapshot.ItemId, out var previous);
            SnapshotTools.PreserveMetadata(snapshot, entry.Record.Snapshot,
                entry.Items.Values.OrderByDescending(i => i.Sequence).Select(i => i.Snapshot), previous?.Snapshot);
        }
        entry.Record.LastSeenUtc = now > entry.Record.LastSeenUtc ? now : entry.Record.LastSeenUtc;
        entry.Record.RemovedUtc = null;
        entry.Record.ActiveItemIds.Add(snapshot.ItemId);
        entry.Record.Snapshot = snapshot;
        entry.Items[snapshot.ItemId] = new StoredItem { Snapshot = snapshot, Sequence = ++_sequence };
        _itemKeys[itemKey] = key;
        _dirty = true;
    }

    /// <summary>
    /// A record that never had a code may later be completed (provider or code appears) for the same
    /// library item at the same non-empty path. That is the same history, not a deletion plus a new title.
    /// A real code-to-code change keeps the previous record untouched.
    /// </summary>
    private bool CanMigrateIdentity(string oldKey, MediaSnapshot snapshot)
    {
        if (!_entries.TryGetValue(oldKey, out var entry)) return false;
        if (!string.Equals(entry.Record.LibraryId, snapshot.LibraryId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(entry.Record.Code)) return false;
        var oldPath = entry.Items.TryGetValue(snapshot.ItemId, out var item)
            ? item.Snapshot.Path : entry.Record.Snapshot.Path;
        return !string.IsNullOrWhiteSpace(oldPath) && string.Equals(oldPath, snapshot.Path, StringComparison.Ordinal);
    }

    private bool SamePath(string oldKey, MediaSnapshot snapshot)
    {
        if (!_entries.TryGetValue(oldKey, out var entry)) return false;
        if (!string.Equals(entry.Record.LibraryId, snapshot.LibraryId, StringComparison.Ordinal)) return false;
        var oldPath = entry.Items.TryGetValue(snapshot.ItemId, out var item)
            ? item.Snapshot.Path : entry.Record.Snapshot.Path;
        return !string.IsNullOrWhiteSpace(oldPath) && string.Equals(oldPath, snapshot.Path, StringComparison.Ordinal);
    }

    /// <summary>Moves one item out of a record without recording a removal; the caller re-observes it.</summary>
    private void DetachItem(string oldKey, string itemId, DateTime now)
    {
        if (!_entries.TryGetValue(oldKey, out var entry)) return;
        entry.Record.ActiveItemIds.Remove(itemId);
        entry.Items.Remove(itemId);
        _itemKeys.Remove((entry.Record.LibraryId, itemId));
        if (entry.Record.ActiveItemIds.Count == 0) _entries.Remove(oldKey);
        else
        {
            entry.Record.Snapshot = KnownSnapshot(entry, entry.Items.Values.MaxBy(i => i.Sequence)!.Snapshot);
            entry.Record.LastSeenUtc = now;
        }
        _dirty = true;
    }

    private bool IsWeakIdentity(string identity) =>
        identity.StartsWith("path:", StringComparison.Ordinal) || identity.StartsWith("title:", StringComparison.Ordinal) ||
        identity.StartsWith("name:", StringComparison.Ordinal) || identity.StartsWith("item:", StringComparison.Ordinal);

    /// <summary>True when this item already belongs to a code-identified record at the same path.</summary>
    private bool IsStrongRecordForKey(string oldKey, MediaSnapshot snapshot)
    {
        if (!_entries.TryGetValue(oldKey, out var entry)) return false;
        if (string.IsNullOrEmpty(entry.Record.Code)) return false;
        if (!string.Equals(entry.Record.LibraryId, snapshot.LibraryId, StringComparison.Ordinal)) return false;
        var oldPath = entry.Items.TryGetValue(snapshot.ItemId, out var item)
            ? item.Snapshot.Path : entry.Record.Snapshot.Path;
        return !string.IsNullOrWhiteSpace(oldPath) && string.Equals(oldPath, snapshot.Path, StringComparison.Ordinal);
    }

    private void MigrateEntry(string oldKey, string newKey, string identity, string code)
    {
        var old = _entries[oldKey];
        _entries.Remove(oldKey);
        if (_entries.TryGetValue(newKey, out var target))
        {
            foreach (var pair in old.Items)
            {
                pair.Value.Snapshot.Code = code;
                target.Items[pair.Key] = pair.Value;
                _itemKeys[(old.Record.LibraryId, pair.Key)] = newKey;
            }
            target.Record.Code = code;
            target.Record.FirstSeenUtc = old.Record.FirstSeenUtc < target.Record.FirstSeenUtc
                ? old.Record.FirstSeenUtc : target.Record.FirstSeenUtc;
            target.Record.LastSeenUtc = old.Record.LastSeenUtc > target.Record.LastSeenUtc
                ? old.Record.LastSeenUtc : target.Record.LastSeenUtc;
            target.Record.ActiveItemIds.UnionWith(old.Record.ActiveItemIds);
            target.Record.RemovedUtc = target.Record.ActiveItemIds.Count == 0
                ? (target.Record.RemovedUtc ?? old.Record.RemovedUtc ?? target.Record.LastSeenUtc)
                : null;
            if (target.Record.RemovedUtc is DateTime removed && removed < target.Record.LastSeenUtc)
                target.Record.RemovedUtc = target.Record.LastSeenUtc;
            target.Record.Snapshot = KnownSnapshot(target, target.Items.Values.MaxBy(item => item.Sequence)!.Snapshot);
        }
        else
        {
            old.Identity = identity;
            old.Record.Key = newKey;
            old.Record.Code = code;
            foreach (var item in old.Items.Values) item.Snapshot.Code = code;
            old.Record.Snapshot.Code = code;
            foreach (var id in old.Items.Keys) _itemKeys[(old.Record.LibraryId, id)] = newKey;
            _entries.Add(newKey, old);
        }
        _dirty = true;
    }

    /// <summary>
    /// The record snapshot must stay the richest view of the work: a merge, a completion or removing the
    /// copy that happened to be scanned last must not throw away metadata the other copies still carry.
    /// </summary>
    private static MediaSnapshot KnownSnapshot(StoredEntry entry, MediaSnapshot latest)
    {
        var filled = SnapshotTools.Clone(latest);
        SnapshotTools.PreserveMetadata(filled, entry.Record.Snapshot,
            entry.Items.Values.OrderByDescending(i => i.Sequence).Select(i => i.Snapshot), latest);
        return filled;
    }

    private void RemoveCore(string itemId, DateTime now)
    {
        var libraries = _itemKeys.Keys.Where(k => string.Equals(k.ItemId, itemId, StringComparison.Ordinal))
            .Select(k => k.LibraryId).Distinct(StringComparer.Ordinal).ToList();
        foreach (var libraryId in libraries) RemoveCore(itemId, libraryId, now);
    }

    private void RemoveCore(string itemId, string libraryId, DateTime now)
    {
        if (!_itemKeys.Remove((libraryId, itemId), out var key)) return;
        var entry = _entries[key];
        entry.Record.ActiveItemIds.Remove(itemId);
        entry.Items.Remove(itemId);
        if (entry.Items.Count == 0)
            entry.Record.RemovedUtc = now > entry.Record.LastSeenUtc ? now : entry.Record.LastSeenUtc;
        else
            entry.Record.Snapshot = KnownSnapshot(entry, entry.Items.Values.MaxBy(item => item.Sequence)!.Snapshot);
        _dirty = true;
    }

    private void Load(FileState state)
    {
        _sequence = state.Sequence;
        foreach (var entry in state.Entries!)
        {
            _entries.Add(entry.Record.Key, entry);
            foreach (var id in entry.Record.ActiveItemIds) _itemKeys.Add((entry.Record.LibraryId, id), entry.Record.Key);
        }
    }

    private static bool TryRead(string path, out FileState? state, out byte[]? bytes, out Exception? error)
    {
        state = null; bytes = null; error = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            state = JsonSerializer.Deserialize<FileState>(bytes, JsonOptions);
            Validate(state);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = exception;
            return false;
        }
    }

    private static void Validate(FileState? state)
    {
        if (state is null || state.Version != 1 || state.Entries is null || state.Sequence < 0)
            throw new InvalidDataException("Invalid history file schema/version.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var items = new HashSet<(string LibraryId, string ItemId)>();
        var sequences = new HashSet<long>();
        foreach (var entry in state.Entries)
        {
            if (entry is null || entry.Record is null || entry.Items is null || string.IsNullOrWhiteSpace(entry.Identity))
                throw new InvalidDataException("Invalid history entry.");
            var r = entry.Record;
            if (string.IsNullOrWhiteSpace(r.LibraryId) || r.Key != SnapshotTools.Key(r.LibraryId, entry.Identity) ||
                !keys.Add(r.Key) || r.Code is null || r.ActiveItemIds is null ||
                r.FirstSeenUtc == default || r.FirstSeenUtc.Kind != DateTimeKind.Utc ||
                r.LastSeenUtc.Kind != DateTimeKind.Utc || r.LastSeenUtc < r.FirstSeenUtc ||
                (r.RemovedUtc is DateTime removed && (removed.Kind != DateTimeKind.Utc || removed < r.LastSeenUtc)) ||
                (r.ActiveItemIds.Count == 0) != r.RemovedUtc.HasValue || !r.ActiveItemIds.SetEquals(entry.Items.Keys))
                throw new InvalidDataException("Invalid history identity, timestamps or active item index.");
            ValidateSnapshot(r.Snapshot, r.LibraryId, r.Code);
            foreach (var pair in entry.Items)
            {
                var item = pair.Value;
                if (string.IsNullOrWhiteSpace(pair.Key) || !items.Add((r.LibraryId, pair.Key)) || item is null ||
                    item.Sequence <= 0 || item.Sequence > state.Sequence || !sequences.Add(item.Sequence))
                    throw new InvalidDataException("Invalid or duplicate active item.");
                ValidateSnapshot(item.Snapshot, r.LibraryId, r.Code);
                if (item.Snapshot.ItemId != pair.Key)
                    throw new InvalidDataException("Snapshot item ID differs from active index.");
            }
        }
    }

    private static void ValidateSnapshot(MediaSnapshot? s, string libraryId, string code)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.ItemId) || s.LibraryId != libraryId || s.Code != code ||
            s.Name is null || s.OriginalTitle is null || s.Overview is null || s.Path is null || s.PosterPath is null ||
            s.Genres is null || s.Tags is null || s.Actors is null || s.ProviderIds is null ||
            s.Genres.Any(x => x is null) || s.Tags.Any(x => x is null) ||
            s.Actors.Any(x => x is null || x.Name is null || x.Role is null) || s.ProviderIds.Any(x => x.Value is null))
            throw new InvalidDataException("Invalid snapshot metadata.");
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class FileState
    {
        public FileState() { }
        public int Version { get; set; }
        public long Sequence { get; set; }
        public int Scheme { get; set; }
        public List<StoredEntry>? Entries { get; set; }
    }

    private sealed class StoredEntry
    {
        public StoredEntry() { }
        public string Identity { get; set; } = "";
        public HistoryRecord Record { get; set; } = new();
        public Dictionary<string, StoredItem> Items { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredItem
    {
        public StoredItem() { }
        public MediaSnapshot Snapshot { get; set; } = new();
        public long Sequence { get; set; }
    }
}
