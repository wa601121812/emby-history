using System.Globalization;
using System.Text.Json;

namespace EmbyHistory;

/// <summary>No credentials or user names; each source-copy/user pair is retained independently.</summary>
public sealed record HistoryPlaybackSnapshot
{
    public string LibraryId { get; init; } = "";
    public string RecordKey { get; init; } = "";
    public long UserId { get; init; }
    public Guid UserGuid { get; init; }
    public long SourceItemId { get; init; }
    public string SourceUserDataKey { get; init; } = "";
    public long? DurationTicks { get; init; }
    public long PositionTicks { get; init; }
    public bool Played { get; init; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public DateTimeOffset? PlaystateModified { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public string Evidence { get; init; } = "";
    public bool HasPlaybackEvidence => PositionTicks > 0 || Played || PlayCount > 0 || LastPlayed.HasValue;
}

/// <summary>Independent schema. Atomic primary/backup replacement, corrupt copies never silently reset.</summary>
public sealed class HistoryPlaybackStore
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<HistoryPlaybackSnapshot> Snapshots { get; set; } = new();
    }
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<(string Library, string Key, long User, long Source), HistoryPlaybackSnapshot> _rows = new();
    private bool _dirty;
    private bool _loaded;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string FilePath => _path;
    public HistoryPlaybackStore(string dataPath) => _path = Path.Combine(Path.GetFullPath(dataPath), "playback-state.v1.json");

    public static bool IsCanonicalId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && id.ToString(CultureInfo.InvariantCulture) == value;
    private static void Validate(HistoryPlaybackSnapshot row)
    {
        if (!IsCanonicalId(row.LibraryId) || string.IsNullOrEmpty(row.RecordKey) || row.RecordKey.Length > 512 ||
            row.UserId <= 0 || row.UserGuid == Guid.Empty || row.SourceItemId <= 0 || row.PositionTicks < 0 || row.PlayCount < 0 || row.DurationTicks is <= 0)
            throw new InvalidDataException("Invalid playback sidecar row");
    }
    private static (string, string, long, long) Key(HistoryPlaybackSnapshot row) => (row.LibraryId, row.RecordKey, row.UserId, row.SourceItemId);
    public IReadOnlyList<HistoryPlaybackSnapshot> Read()
    {
        lock (_gate) { Load(); return _rows.Values.ToArray(); }
    }
    public bool Put(HistoryPlaybackSnapshot row)
    {
        lock (_gate)
        {
            Load(); Validate(row);
            if (_rows.TryGetValue(Key(row), out var previous))
            {
                // Default GetUserData results (including after removal) are not evidence of a reset.
                if (!row.HasPlaybackEvidence && previous.HasPlaybackEvidence) return false;
                if (row.DurationTicks == null) row = row with { DurationTicks = previous.DurationTicks };
                if (row.PlaystateModified < previous.PlaystateModified) return false;
                if (row == previous || row with { CapturedUtc = previous.CapturedUtc, Evidence = previous.Evidence } == previous) return false;
            }
            _rows[Key(row)] = row; _dirty = true; return true;
        }
    }
    public void Flush()
    {
        lock (_gate)
        {
            Load(); if (!_dirty) return;
            SafePath(Path.GetDirectoryName(_path)!); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafePath(_path); SafePath(_path + ".bak");
            var document = new Document { Snapshots = _rows.Values.OrderBy(r => r.LibraryId, StringComparer.Ordinal).ThenBy(r => r.RecordKey, StringComparer.Ordinal).ThenBy(r => r.UserId).ThenBy(r => r.SourceItemId).ToList() };
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, document, Json); stream.Flush(true); }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
                else { File.Move(temp, _path); if (!File.Exists(_path + ".bak")) File.Copy(_path, _path + ".bak", false); }
                _dirty = false;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private void Load()
    {
        if (_loaded) return;
        var failures = new List<Exception>();
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            SafePath(path); if (!File.Exists(path)) continue;
            try
            {
                var doc = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Empty playback sidecar");
                if (doc.Version != 1 || doc.Snapshots == null) throw new InvalidDataException("Unsupported playback sidecar version");
                var rows = new Dictionary<(string, string, long, long), HistoryPlaybackSnapshot>();
                foreach (var row in doc.Snapshots) { Validate(row); if (!rows.TryAdd(Key(row), row)) throw new InvalidDataException("Duplicate playback sidecar row"); }
                if (path != _path && File.Exists(_path)) File.Move(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                _rows = rows; _loaded = true; _dirty = path != _path; return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Playback sidecar unreadable; refusing to reset", failures);
        _loaded = true;
    }
    internal static void SafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            // LinkTarget also catches dangling links, for which File.Exists is deliberately false.
            if (new FileInfo(current).LinkTarget != null || new DirectoryInfo(current).LinkTarget != null ||
                ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Playback sidecar/archive symlink refused");
        }
    }
}
