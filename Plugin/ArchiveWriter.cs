using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EmbyHistory.Core;

namespace EmbyHistory;

/// <summary>Single-writer archive publisher. Never touches source media or untracked files.</summary>
internal sealed class ArchiveWriter
{
    private const string Stamp = "render.sha256";
    private const string Marker = ".embyhistory-owned";
    private const string Manifest = ".embyhistory-files.json";
    private readonly string _root;
    private readonly HashSet<string> _changes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedLibraries = new(StringComparer.Ordinal);
    private string _currentLibrary = "";

    // Detached, read-only snapshots of the LAST Sync, including partial progress on a failed record.
    // Library IDs are SOURCE IDs; the caller maps them to history-library IDs before incremental refresh.
    public IReadOnlyCollection<string> LastChangedLibraryIds { get; private set; } = Array.Empty<string>();
    public IReadOnlyCollection<string> LastChangedPaths { get; private set; } = Array.Empty<string>();

    public ArchiveWriter(string dataPath) { _root = Path.Combine(dataPath, "archive"); EnsureSafeDirectory(_root); }

    public void EnsureLibrary(string libraryId)
    {
        ValidateLibrary(libraryId);
        EnsureSafeDirectory(Path.Combine(_root, libraryId));
    }

    // Retains the original signature/return value (failed record count).
    public int Sync(IEnumerable<HistoryRecord> records, ISet<string> configuredLibraries, Action<string, Exception>? onError = null)
    {
        _changes.Clear(); _changedLibraries.Clear();
        var failed = 0;
        try
        {
            var selected = records.Where(r => configuredLibraries.Contains(r.LibraryId)).ToList();
            foreach (var record in selected.Where(r => r.Mode != IdentityMode.SeriesEpisode))
            {
                _currentLibrary = record.LibraryId;
                try { SyncMovie(record); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failed++; onError?.Invoke(record.Key, ex); }
            }
            foreach (var group in selected.Where(r => r.Mode == IdentityMode.SeriesEpisode).GroupBy(r => (r.LibraryId, r.SeriesKey)))
            {
                _currentLibrary = group.Key.LibraryId;
                try { SyncSeries(group.Key.LibraryId, group.Key.SeriesKey, group.ToList()); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failed += group.Count(); onError?.Invoke(group.Key.SeriesKey, ex); }
            }
            return failed;
        }
        finally
        {
            LastChangedLibraryIds = Array.AsReadOnly(_changedLibraries.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            LastChangedPaths = Array.AsReadOnly(_changes.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
    }

    private void Changed(string path) { _changes.Add(path); _changedLibraries.Add(_currentLibrary); }

    private void SyncMovie(HistoryRecord record)
    {
        ValidateLibrary(record.LibraryId); ValidateKey(record.Key); EnsureLibrary(record.LibraryId);
        var directory = Path.Combine(_root, record.LibraryId, record.Key);
        if (IsOnline(record) && !Directory.Exists(directory)) return;
        if (IsOnline(record)) RequireOwned(directory, record.Key); else EnsureOwned(directory, record.Key);
        using var files = new OwnedFiles(this, directory, record.Key);
        files.AdoptLegacyNfo("movie.nfo", record.Key, "movie");
        files.AdoptLegacyDecoration("movie.nfo");
        files.AdoptLegacyStub("history.disc", "movie.nfo");
        if (IsOnline(record)) { files.Withdraw(); return; }
        var content = BuildNfo(record).ToString();
        files.Write("movie.nfo", Encoding.UTF8.GetBytes(content));
        PublishPoster(files, record, Digest("render-v5-amber", content, record.Snapshot.PosterPath));
        files.Write("history.disc", Array.Empty<byte>());
    }

    private void SyncSeries(string libraryId, string seriesKey, List<HistoryRecord> records)
    {
        ValidateLibrary(libraryId); ValidateKey(seriesKey); EnsureLibrary(libraryId);
        foreach (var r in records) ValidateKey(r.Key);
        var sample = records.OrderBy(r => r.Key, StringComparer.Ordinal).First().Snapshot;
        var libraryDirectory = Path.Combine(_root, libraryId);
        // Titles may change on re-scrape: the owner marker, not a new title, locates existing archives.
        var existing = OwnedSubdirectories(libraryDirectory, seriesKey).ToList();
        if (existing.Count > 1) throw new IOException("Multiple archive directories own the same series key");
        var directory = existing.SingleOrDefault() ?? Path.Combine(libraryDirectory, TvArchivePlan.SeriesFolder(sample));
        var removed = records.Where(IsRemoved).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        if (removed.Count == 0 && !Directory.Exists(directory)) return;
        if (removed.Count == 0) RequireOwned(directory, seriesKey); else EnsureOwned(directory, seriesKey);
        using var series = new OwnedFiles(this, directory, seriesKey);
        series.AdoptLegacyNfo("series.nfo", seriesKey, "tvshow");
        series.AdoptLegacyNfo("tvshow.nfo", seriesKey, "tvshow");
        series.AdoptLegacyDecoration(series.Owns("series.nfo") ? "series.nfo" : "tvshow.nfo");

        var seasons = removed.GroupBy(r => r.Snapshot.SeasonNumber ?? 0).ToList();
        // Process all ledger seasons, including ones with no remaining history cards.
        foreach (var season in records.GroupBy(r => r.Snapshot.SeasonNumber ?? 0).OrderBy(g => g.Key))
        {
            var seasonDirectory = Path.Combine(directory, TvArchivePlan.SeasonFolder(season.Key));
            var missing = season.Where(IsRemoved).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            if (missing.Count == 0 && !Directory.Exists(seasonDirectory)) continue;
            if (missing.Count == 0) RequireOwned(seasonDirectory, seriesKey); else EnsureOwned(seasonDirectory, seriesKey);
            using var files = new OwnedFiles(this, seasonDirectory, seriesKey);
            files.AdoptLegacyNfo("season.nfo", seriesKey + "-s" + season.Key, "season", season.Key);
            files.AdoptLegacyDecoration("season.nfo");
            var episodeBases = FindEpisodeBases(seasonDirectory, season);
            foreach (var record in season)
            {
                // Reuse the old .disc basename if a series has been renamed. Only local-id-matched NFOs qualify.
                var baseName = episodeBases.GetValueOrDefault(record.Key) ?? TvArchivePlan.EpisodeBase(record.Snapshot);
                var nfo = baseName + ".nfo";
                files.AdoptLegacyNfo(nfo, record.Key, "episodedata", null, "episodedetails");
                files.AdoptLegacyStub(baseName + ".disc", nfo);
                if (IsRemoved(record))
                {
                    files.Write(nfo, Encoding.UTF8.GetBytes(TvArchivePlan.EpisodeNfo(record)));
                    files.Write(baseName + ".disc", Array.Empty<byte>());
                }
                else
                {
                    // Both the series AND season containers were checked above, before any unlink.
                    files.Delete(baseName + ".disc"); files.Delete(nfo);
                }
            }
            if (missing.Count == 0) files.Withdraw();
            else
            {
                var xml = TvArchivePlan.SeasonNfo(season.Key, missing.Min(r => r.FirstSeenUtc), missing.Max(r => r.LastSeenUtc), missing.Count, seriesKey);
                files.Write("season.nfo", Encoding.UTF8.GetBytes(xml));
                var posterPath = missing[0].Snapshot.PosterPath;
                PublishPoster(files, Synthetic(seriesKey + "-s" + season.Key, libraryId, "第 " + season.Key + " 季", posterPath, missing),
                    Digest("tv-poster-v2", "", posterPath));
            }
        }
        if (removed.Count == 0) series.Withdraw();
        else
        {
            var xml = TvArchivePlan.SeriesNfo(sample, seriesKey, records.Min(r => r.FirstSeenUtc), records.Max(r => r.LastSeenUtc), seasons.Count, removed.Count);
            series.Write("tvshow.nfo", Encoding.UTF8.GetBytes(xml));
            // Delete only a positively identified 0.5.3 sidecar; unrelated series.nfo files stay untouched.
            series.Delete("series.nfo");
            var posterPath = string.IsNullOrWhiteSpace(sample.SeriesPosterPath) ? sample.PosterPath : sample.SeriesPosterPath;
            PublishPoster(series, Synthetic(seriesKey, libraryId, sample.SeriesName, posterPath, removed), Digest("tv-poster-v2", "", posterPath));
        }
    }

    private static Dictionary<string, string> FindEpisodeBases(string directory, IEnumerable<HistoryRecord> records)
    {
        // Enumerate/parse once per season, not once per episode (a 1005-episode show is otherwise
        // a million NFO reads per Sync). Only expected local identities can supply a stable basename.
        var expected = records.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var matches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.nfo"))
        {
            RejectLink(path);
            XElement? node;
            try
            {
                using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                node = XDocument.Load(reader).Root;
            }
            catch (System.Xml.XmlException) { continue; }
            if (node == null || (node.Name != "episodedata" && node.Name != "episodedetails")) continue;
            var key = node.Elements("uniqueid").FirstOrDefault(i => (string?)i.Attribute("type") == "embyhistory")?.Value;
            if (key == null || !expected.Contains(key) || !node.Elements("tag").Any(t => t.Value == "EmbyHistory") || node.Element("lockdata")?.Value != "true") continue;
            if (!matches.TryAdd(key, Path.GetFileNameWithoutExtension(path))) throw new IOException("Duplicate NFO history identity: " + key);
        }
        return matches;
    }

    private void PublishPoster(OwnedFiles files, HistoryRecord record, string digest)
    {
        if (files.MatchesText(Stamp, digest) && files.ExistsOwned("poster.jpg")) return;
        var poster = TryRender(record);
        if (poster != null) files.Write("poster.jpg", poster);
        // Failed rendering is retryable, but identical NFO/stub/stamp bytes are never rewritten.
        files.Write(Stamp, Encoding.ASCII.GetBytes(poster == null ? digest + "\nnoposter" : digest));
    }

    /// <summary>
    /// A container marker is NOT permission to delete all .nfo/.disc files. The manifest records
    /// exact generated names and hashes. User-edited/replaced files fail closed (and keep the marker).
    /// Legacy adoption is limited to id/tag-verified sidecars and their explicitly paired zero-byte stub.
    /// </summary>
    private sealed class OwnedFiles : IDisposable
    {
        private readonly ArchiveWriter _writer;
        private readonly string _directory;
        private readonly string _key;
        private readonly Dictionary<string, string> _hashes;
        private readonly bool _legacy;
        private bool _dirty;
        private bool _withdraw;
        public OwnedFiles(ArchiveWriter writer, string directory, string key)
        {
            RequireOwned(directory, key);
            _writer = writer; _directory = directory; _key = key;
            var path = Path.Combine(directory, Manifest); RejectLink(path);
            _legacy = !File.Exists(path);
            _hashes = _legacy ? new(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid archive manifest");
            foreach (var entry in _hashes) { ValidateName(entry.Key); ValidateKey(entry.Value); }
        }
        private string PathOf(string name) { ValidateName(name); return Path.Combine(_directory, name); }
        private static void ValidateName(string name)
        {
            if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name is "." or ".." || name == Marker || name == Manifest)
                throw new InvalidDataException("Invalid generated filename");
            if (!(name.EndsWith(".nfo", StringComparison.Ordinal) || name.EndsWith(".disc", StringComparison.Ordinal) || name == "poster.jpg" || name == Stamp))
                throw new InvalidDataException("Unexpected generated filename");
        }
        public bool Owns(string name) => _hashes.ContainsKey(name);
        public bool ExistsOwned(string name)
        {
            var path = PathOf(name); RejectLink(path);
            if (!File.Exists(path) || !Owns(name)) return false;
            Verify(name); return true;
        }
        private void Verify(string name)
        {
            RequireOwned(_directory, _key);
            var path = PathOf(name); RejectLink(path);
            if (File.Exists(path) && (!_hashes.TryGetValue(name, out var hash) || Hash(File.ReadAllBytes(path)) != hash))
                throw new IOException("Refusing untracked or externally modified archive file: " + path);
        }
        public bool MatchesText(string name, string text) => ExistsOwned(name) && File.ReadAllText(PathOf(name)) == text;
        public void AdoptLegacyNfo(string name, string key, string root, int? season = null, string? alternateRoot = null)
        {
            if (!_legacy || Owns(name)) return;
            var path = PathOf(name); RejectLink(path);
            if (IsLegacyNfo(path, key, root, season, alternateRoot)) Adopt(name);
        }
        private void Adopt(string name)
        {
            var path = PathOf(name); RejectLink(path);
            if (File.Exists(path)) { _hashes[name] = Hash(File.ReadAllBytes(path)); _dirty = true; }
        }
        public void AdoptLegacyStub(string stub, string nfo)
        {
            if (!_legacy || !Owns(nfo)) return;
            var path = PathOf(stub); RejectLink(path);
            if (File.Exists(path) && new FileInfo(path).Length == 0) Adopt(stub);
        }
        public void AdoptLegacyDecoration(string nfo)
        {
            if (!_legacy || !Owns(nfo)) return;
            var stamp = PathOf(Stamp); RejectLink(stamp);
            if (!File.Exists(stamp)) return;
            var text = File.ReadAllText(stamp);
            var digest = text.Split('\n')[0];
            if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c)) || (text != digest && text != digest + "\nnoposter")) return;
            Adopt(Stamp); Adopt("poster.jpg");
        }
        public void Write(string name, byte[] bytes)
        {
            var path = PathOf(name); Verify(name);
            var hash = Hash(bytes);
            if (File.Exists(path) && _hashes.TryGetValue(name, out var old) && old == hash) return;
            // Batch the ownership manifest once per container (Dispose), rather than serializing
            // an ever-growing season manifest for every episode. On abrupt process death an
            // uncommitted file is left untouched/fails closed, never inferred from its extension.
            AtomicWrite(path, bytes);
            _hashes[name] = hash; _dirty = true; _writer.Changed(path);
        }
        public void Delete(string name)
        {
            if (!Owns(name)) return;
            var path = PathOf(name); Verify(name);
            if (File.Exists(path)) { File.Delete(path); _writer.Changed(path); }
            _hashes.Remove(name); _dirty = true;
        }
        public void Withdraw()
        {
            foreach (var name in _hashes.Keys.ToArray()) Delete(name);
            _withdraw = true;
        }
        private void Save()
        {
            if (!_dirty) return;
            RequireOwned(_directory, _key);
            AtomicWrite(Path.Combine(_directory, Manifest), JsonSerializer.SerializeToUtf8Bytes(_hashes.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)));
            _dirty = false;
        }
        public void Dispose()
        {
            Save();
            if (!_withdraw || _hashes.Count != 0) return;
            RequireOwned(_directory, _key);
            // Unknown files, child directories and symlinks keep the container AND its marker alive.
            if (Directory.EnumerateFileSystemEntries(_directory).Any(p => Path.GetFileName(p) != Marker && Path.GetFileName(p) != Manifest)) return;
            var manifest = Path.Combine(_directory, Manifest); RejectLink(manifest);
            if (File.Exists(manifest)) File.Delete(manifest);
            File.Delete(Path.Combine(_directory, Marker));
            Directory.Delete(_directory); _writer.Changed(_directory);
        }
    }

    private static bool IsLegacyNfo(string path, string key, string root, int? season = null, string? alternateRoot = null)
    {
        RejectLink(path);
        if (!File.Exists(path)) return false;
        try
        {
            // Never resolve external entities while examining a pre-existing user file.
            using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var node = XDocument.Load(reader).Root;
            if (node == null || (node.Name != root && node.Name != alternateRoot) || !node.Elements("tag").Any(t => t.Value == "EmbyHistory") || node.Element("lockdata")?.Value != "true") return false;
            var id = node.Elements("uniqueid").FirstOrDefault(i => (string?)i.Attribute("type") == "embyhistory")?.Value;
            if (id == key) return true;
            // 0.5.3 season sidecars had no uniqueid; accept only their characteristic generated plot.
            return season.HasValue && root == "season" && id == null && node.Element("seasonnumber")?.Value == season.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && (node.Element("plot")?.Value.StartsWith("【永久历史记录：已移出源库，无可播放文件】", StringComparison.Ordinal) ?? false);
        }
        catch (System.Xml.XmlException) { return false; }
    }

    private static IEnumerable<string> OwnedSubdirectories(string directory, string key)
    {
        RequireSafeAncestors(directory);
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            // Unknown links are not traversed, even if their target contains a matching marker.
            try { RejectLink(candidate); }
            catch (IOException) { continue; }
            var marker = Path.Combine(candidate, Marker);
            RejectLink(marker);
            if (File.Exists(marker) && File.ReadAllText(marker) == key) yield return candidate;
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string Digest(string version, string content, string posterPath)
    {
        var file = new FileInfo(posterPath.Length == 0 ? ".embyhistory-no-poster" : posterPath);
        return Hash(Encoding.UTF8.GetBytes(version + "\n" + content + "\n" + posterPath + "\n" + (file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : "missing")));
    }
    private static byte[]? TryRender(HistoryRecord record)
    {
        try { return HistoryPosterRenderer.Render(record); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
    private static HistoryRecord Synthetic(string key, string libraryId, string name, string posterPath, List<HistoryRecord> records) => new()
    {
        Key = key, LibraryId = libraryId, Snapshot = new MediaSnapshot { LibraryId = libraryId, Name = name, PosterPath = posterPath },
        FirstSeenUtc = records.Min(r => r.FirstSeenUtc), LastSeenUtc = records.Max(r => r.LastSeenUtc), RemovedUtc = records.Max(r => r.RemovedUtc)
    };
    private static bool IsOnline(HistoryRecord record) => !IsRemoved(record);
    private static bool IsRemoved(HistoryRecord record) => record.ActiveItemIds.Count == 0 && record.RemovedUtc != null;

    private static XDocument BuildNfo(HistoryRecord r)
    {
        var s = r.Snapshot;
        var name = Xml(s.Name);
        if (!string.IsNullOrWhiteSpace(r.Code) && !name.Contains(r.Code, StringComparison.OrdinalIgnoreCase)) name = Xml(r.Code) + " " + name;
        var movie = new XElement("movie",
            new XElement("title", "[历史] " + name), new XElement("originaltitle", Xml(s.OriginalTitle)), new XElement("sorttitle", name),
            new XElement("plot", "【永久历史记录：已移出源库，无可播放文件】\n" +
                "首次记录（非真实首次入库）：" + r.FirstSeenUtc.ToString("u") + "\n" + "最后在库：" + r.LastSeenUtc.ToString("u") + "\n" +
                "移出记录：" + r.RemovedUtc?.ToString("u") + "\n" + "来源库：" + r.LibraryId + "\n\n" + Xml(s.Overview)),
            new XElement("lockdata", "true"),
            new XElement("uniqueid", new XAttribute("type", "embyhistory"), new XAttribute("default", "true"), r.Key),
            new XElement("tag", "EmbyHistory"), new XElement("tag", "已移出"));
        if (s.Year.HasValue) movie.Add(new XElement("year", s.Year.Value));
        foreach (var genre in s.Genres) movie.Add(new XElement("genre", Xml(genre)));
        foreach (var actor in s.Actors) movie.Add(new XElement("actor", new XElement("name", Xml(actor.Name)), new XElement("role", Xml(actor.Role))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), movie);
    }

    private static string Xml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { builder.Append(c).Append(value[++i]); continue; }
            builder.Append(System.Xml.XmlConvert.IsXmlChar(c) ? c : ' ');
        }
        return builder.ToString();
    }

    private static void ValidateLibrary(string id)
    {
        if (!long.TryParse(id, out var value) || value <= 0 || id != value.ToString()) throw new InvalidDataException("Library IDs must be positive decimal Emby IDs.");
    }
    private static void ValidateKey(string key)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid history key");
    }
    internal static void RejectLink(string path)
    {
        // File.Exists is false for dangling symlinks; inspect attributes directly instead.
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a symbolic link: " + path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static void RequireSafeAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current)) RejectLink(current);
    }
    internal static void EnsureSafeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        RequireSafeAncestors(full);
        Directory.CreateDirectory(full);
        RequireSafeAncestors(full);
    }
    private static void EnsureOwned(string directory, string key)
    {
        RequireSafeAncestors(directory);
        if (Directory.Exists(directory)) { RequireOwned(directory, key); return; }
        EnsureSafeDirectory(directory);
        AtomicWrite(Path.Combine(directory, Marker), Encoding.ASCII.GetBytes(key));
    }
    private static void RequireOwned(string directory, string key)
    {
        RequireSafeAncestors(directory);
        var marker = Path.Combine(directory, Marker); RejectLink(marker);
        if (!File.Exists(marker) || File.ReadAllText(marker) != key) throw new IOException("Unowned archive directory: " + directory);
    }
    internal static void AtomicWrite(string path, byte[] data)
    {
        RequireSafeAncestors(Path.GetDirectoryName(path)!); RejectLink(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            RequireSafeAncestors(Path.GetDirectoryName(path)!); RejectLink(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
