using EmbyHistory;
using EmbyHistory.Core;
using System.Text;
using System.Xml.Linq;
using System.Security.Cryptography;

// Filesystem/metadata regression suite ONLY. The deterministic fake is not a JPEG renderer and
// passing these checks is NOT an Emby/Skia integration or native-search end-to-end result.
internal static class Program
{
    private static readonly DateTime Start = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sentinel = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> Libraries = new(StringComparer.Ordinal) { "14" };
    private static readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR required"), "emby-archive-regression-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _failed;
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action<string> test)
    {
        var data = Path.Combine(Root, (_passed + _failed).ToString());
        HistoryPosterRenderer.Calls = 0; HistoryPosterRenderer.Fail = false;
        try { test(data); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static HistoryRecord Episode(int number = 1, int season = 1, bool removed = true, string library = "14")
    {
        var s = new MediaSnapshot { ItemId = "e" + season + "-" + number, LibraryId = library,
            Name = "第 " + number + " 集", SeriesName = "回归测试剧", SeriesYear = 2025, Year = 2025,
            SeasonNumber = season, EpisodeNumber = number, Path = "/synthetic/s" + season + "e" + number + ".mkv",
            SeriesProviderIds = new() { ["Tvdb"] = "1234", ["Tmdb"] = "5678" },
            ProviderIds = new() { ["Tvdb"] = "9000" + number, ["Imdb"] = "tt9000" + number } };
        var r = new HistoryRecord { Mode = IdentityMode.SeriesEpisode, LibraryId = library, Snapshot = s,
            FirstSeenUtc = Start, LastSeenUtc = Start.AddDays(1), RemovedUtc = removed ? Start.AddDays(2) : null };
        r.Key = SnapshotTools.Key(library, SnapshotTools.Identify(s, IdentityMode.SeriesEpisode).Identity);
        r.SeriesKey = SnapshotTools.SeriesKeyOf(s, library);
        if (!removed) r.ActiveItemIds.Add(s.ItemId);
        return r;
    }
    private static string Show(string data, HistoryRecord r) => Path.Combine(data, "archive", r.LibraryId, TvArchivePlan.SeriesFolder(r.Snapshot));
    private static string Season(string data, HistoryRecord r) => Path.Combine(Show(data, r), TvArchivePlan.SeasonFolder(r.Snapshot.SeasonNumber ?? 0));
    private static string EpisodePath(string data, HistoryRecord r, string extension = ".nfo") => Path.Combine(Season(data, r), TvArchivePlan.EpisodeBase(r.Snapshot) + extension);
    private static void Online(HistoryRecord r) { r.RemovedUtc = null; r.ActiveItemIds.Add(r.Snapshot.ItemId); }
    private static int Sync(ArchiveWriter writer, params HistoryRecord[] records) => writer.Sync(records, Libraries, (id, e) => Console.WriteLine("  expected-or-diagnostic: " + e.Message));
    private static Dictionary<string, (long Mtime, string Hash)> Snapshot(string data, bool setSentinel = false)
    {
        var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
        {
            if (setSentinel) File.SetLastWriteTimeUtc(file, Sentinel);
            result[Path.GetRelativePath(data, file)] = (File.GetLastWriteTimeUtc(file).Ticks, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
        return result;
    }
    private static void Same(Dictionary<string, (long, string)> before, Dictionary<string, (long, string)> after)
    {
        Assert(before.Count == after.Count, "file count changed");
        foreach (var p in before) Assert(after.TryGetValue(p.Key, out var value) && value == p.Value, "bytes/mtime changed: " + p.Key);
    }
    private static void Owned(string directory, string key) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, ".embyhistory-owned"), key); }
    private static void Legacy(string data, HistoryRecord[] records)
    {
        var first = records[0]; var show = Show(data, first); Owned(show, first.SeriesKey);
        var series = XDocument.Parse(TvArchivePlan.SeriesNfo(first.Snapshot, first.SeriesKey, first.FirstSeenUtc, first.LastSeenUtc, 1, records.Length));
        series.Root!.Element("title")!.Value = first.Snapshot.SeriesName;
        foreach (var id in series.Root.Elements("uniqueid").Where(n => (string?)n.Attribute("type") != "embyhistory").ToArray()) id.Remove();
        File.WriteAllText(Path.Combine(show, "series.nfo"), series.ToString());
        File.WriteAllText(Path.Combine(show, "render.sha256"), new string('A', 64)); File.WriteAllText(Path.Combine(show, "poster.jpg"), "old generated series poster");
        foreach (var group in records.GroupBy(r => r.Snapshot.SeasonNumber ?? 0))
        {
            var season = Season(data, group.First()); Owned(season, first.SeriesKey);
            var nfo = XDocument.Parse(TvArchivePlan.SeasonNfo(group.Key, first.FirstSeenUtc, first.LastSeenUtc, group.Count()));
            nfo.Root!.Element("title")!.Value = "第 " + group.Key + " 季";
            File.WriteAllText(Path.Combine(season, "season.nfo"), nfo.ToString());
            File.WriteAllText(Path.Combine(season, "render.sha256"), new string('B', 64)); File.WriteAllText(Path.Combine(season, "poster.jpg"), "old generated season poster");
            foreach (var record in group)
            {
                var episode = XDocument.Parse(TvArchivePlan.EpisodeNfo(record)); episode.Root!.Name = "episodedata";
                File.WriteAllText(EpisodePath(data, record), episode.ToString()); File.WriteAllBytes(EpisodePath(data, record, ".disc"), Array.Empty<byte>());
            }
        }
    }
    public static int Main()
    {
        Directory.CreateDirectory(Root);
        Console.WriteLine("MODE filesystem-only; mocked poster renderer; no NAS or production access");
        try
        {
            Test("TV NFO contract, prefixed titles and object-scoped provider IDs", data => {
                var r = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, r) == 0);
                Assert(!File.Exists(Path.Combine(Show(data, r), "series.nfo")));
                var show = XDocument.Load(Path.Combine(Show(data, r), "tvshow.nfo")).Root!;
                var season = XDocument.Load(Path.Combine(Season(data, r), "season.nfo")).Root!;
                var episode = XDocument.Load(EpisodePath(data, r)).Root!;
                Assert(show.Name == "tvshow" && episode.Name == "episodedetails" && season.Name == "season");
                foreach (var node in new[] { show, season, episode }) Assert(node.Element("title")!.Value.StartsWith("[历史] "));
                Assert(season.Element("seasonnumber")!.Value == "1");
                Assert(show.Elements("uniqueid").Single(n => (string?)n.Attribute("type") == "tvdb").Value == "1234");
                Assert(episode.Elements("uniqueid").Single(n => (string?)n.Attribute("type") == "tvdb").Value == "90001");
                Assert(episode.Elements("uniqueid").Single(n => (string?)n.Attribute("default") == "true").Value == r.Key);
                Assert(show.Elements("uniqueid").Single(n => (string?)n.Attribute("default") == "true").Value == r.SeriesKey);
                Assert(season.Elements("uniqueid").Single().Value == r.SeriesKey + "-s1");
            });
            Test("second sync and reconstructed writer preserve ALL bytes/mtimes and render count", data => {
                var a = Episode(); var b = Episode(2); var writer = new ArchiveWriter(data);
                Assert(Sync(writer, a, b) == 0); var changes = writer.LastChangedPaths; var ids = writer.LastChangedLibraryIds;
                Assert(ids.SequenceEqual(new[] { "14" }) && changes.Count > 0);
                var before = Snapshot(data, true); var calls = HistoryPosterRenderer.Calls;
                Assert(Sync(writer, b, a) == 0); Same(before, Snapshot(data));
                Assert(writer.LastChangedPaths.Count == 0 && writer.LastChangedLibraryIds.Count == 0 && HistoryPosterRenderer.Calls == calls);
                Assert(changes.Count > 0 && ids.SequenceEqual(new[] { "14" }), "old snapshots mutated");
                var restarted = new ArchiveWriter(data); Assert(Sync(restarted, a, b) == 0); Same(before, Snapshot(data));
                Assert(restarted.LastChangedPaths.Count == 0 && HistoryPosterRenderer.Calls == calls);
            });
            Test("episode metadata edit rewrites only episode NFO, not stub or posters", data => {
                var r = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, r) == 0);
                Snapshot(data, true); var calls = HistoryPosterRenderer.Calls; r.Snapshot.Overview = "updated\u0001 metadata";
                Assert(Sync(writer, r) == 0); Assert(writer.LastChangedPaths.SequenceEqual(new[] { EpisodePath(data, r) }));
                Assert(File.GetLastWriteTimeUtc(EpisodePath(data, r, ".disc")) == Sentinel && HistoryPosterRenderer.Calls == calls);
                XDocument.Load(EpisodePath(data, r));
            });
            Test("0.5.3 migration is in place with unchanged keys, directory and .disc mtime", data => {
                var a = Episode(); var b = Episode(2); Legacy(data, new[] { a, b });
                var key = a.Key; var seriesKey = a.SeriesKey; var oldDisc = EpisodePath(data, a, ".disc"); File.SetLastWriteTimeUtc(oldDisc, Sentinel);
                var unknown = Path.Combine(Season(data, a), "personal.nfo"); File.WriteAllText(unknown, "not generated");
                var writer = new ArchiveWriter(data); Assert(Sync(writer, a, b) == 0);
                Assert(a.Key == key && a.SeriesKey == seriesKey && File.Exists(oldDisc) && File.GetLastWriteTimeUtc(oldDisc) == Sentinel);
                Assert(!File.Exists(Path.Combine(Show(data, a), "series.nfo")) && File.Exists(Path.Combine(Show(data, a), "tvshow.nfo")));
                Assert(XDocument.Load(EpisodePath(data, a)).Root!.Name == "episodedetails" && File.ReadAllText(unknown) == "not generated");
                var before = Snapshot(data, true); Assert(Sync(writer, a, b) == 0); Same(before, Snapshot(data)); Assert(writer.LastChangedPaths.Count == 0);
            });
            Test("single-episode return removes only its generated pair; remaining episode untouched", data => {
                var a = Episode(); var b = Episode(2); var writer = new ArchiveWriter(data); Assert(Sync(writer, a, b) == 0);
                var bNfo = EpisodePath(data, b); File.SetLastWriteTimeUtc(bNfo, Sentinel);
                File.WriteAllText(Path.Combine(Season(data, a), "other.disc"), "private"); File.WriteAllText(Path.Combine(Season(data, a), "other.nfo"), "private nfo");
                Online(a); Assert(Sync(writer, a, b) == 0);
                Assert(!File.Exists(EpisodePath(data, a)) && !File.Exists(EpisodePath(data, a, ".disc")));
                Assert(File.Exists(EpisodePath(data, b, ".disc")) && File.GetLastWriteTimeUtc(bNfo) == Sentinel);
                Assert(File.ReadAllText(Path.Combine(Season(data, a), "other.disc")) == "private");
            });
            Test("whole-series return preserves unknown files, child dirs and ownership markers", data => {
                var a = Episode(); var b = Episode(1, 2); var writer = new ArchiveWriter(data); Assert(Sync(writer, a, b) == 0);
                File.WriteAllText(Path.Combine(Season(data, a), "unknown.nfo"), "user nfo"); File.WriteAllText(Path.Combine(Season(data, a), "unknown.disc"), "user stub");
                Directory.CreateDirectory(Path.Combine(Show(data, a), "user-folder")); File.WriteAllText(Path.Combine(Show(data, a), "user-folder", "poster.jpg"), "user image");
                Online(a); Online(b); Assert(Sync(writer, a, b) == 0);
                Assert(!File.Exists(EpisodePath(data, a)) && !Directory.Exists(Season(data, b)) && !File.Exists(Path.Combine(Show(data, a), "tvshow.nfo")));
                Assert(File.ReadAllText(Path.Combine(Season(data, a), "unknown.nfo")) == "user nfo");
                Assert(File.ReadAllText(Path.Combine(Show(data, a), "user-folder", "poster.jpg")) == "user image");
                Assert(File.Exists(Path.Combine(Season(data, a), ".embyhistory-owned")) && File.Exists(Path.Combine(Show(data, a), ".embyhistory-owned")));
                Assert(Sync(writer, a, b) == 0 && writer.LastChangedPaths.Count == 0);
            });
            Test("whole-series removal and return fully reclaim an exclusively generated tree", data => {
                var a = Episode(); var b = Episode(2); var c = Episode(1, 2); var writer = new ArchiveWriter(data);
                Assert(Sync(writer, a, b, c) == 0); Online(a); Online(b); Online(c); Assert(Sync(writer, a, b, c) == 0);
                Assert(!Directory.Exists(Show(data, a)) && writer.LastChangedLibraryIds.SequenceEqual(new[] { "14" }));
                Assert(Sync(writer, a, b, c) == 0 && writer.LastChangedPaths.Count == 0);
            });
            Test("legacy direct return without migration deletes only verified generated files", data => {
                var a = Episode(); Legacy(data, new[] { a }); var unknown = Path.Combine(Season(data, a), "manual.nfo"); File.WriteAllText(unknown, "keep");
                Online(a); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                Assert(!File.Exists(EpisodePath(data, a)) && !File.Exists(EpisodePath(data, a, ".disc")) && File.ReadAllText(unknown) == "keep");
            });
            Test("unowned season blocks single-episode unlink BEFORE any deletion", data => {
                var a = Episode(); var b = Episode(2); var writer = new ArchiveWriter(data); Assert(Sync(writer, a, b) == 0);
                File.Delete(Path.Combine(Season(data, a), ".embyhistory-owned")); Online(a); var before = Snapshot(data, true);
                Assert(Sync(writer, a, b) == 2); Same(before, Snapshot(data)); Assert(writer.LastChangedPaths.Count == 0);
            });
            Test("unowned series is never claimed or removed", data => {
                var a = Episode(); Directory.CreateDirectory(Season(data, a)); File.WriteAllText(EpisodePath(data, a, ".disc"), "user-owned");
                Online(a); var writer = new ArchiveWriter(data); var before = Snapshot(data, true); Assert(Sync(writer, a) == 1); Same(before, Snapshot(data));
            });
            Test("symlinked season cannot redirect return-time unlink outside archive", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var outside = Path.Combine(data, "outside"); Directory.Move(Season(data, a), outside); Directory.CreateSymbolicLink(Season(data, a), outside);
                Online(a); Assert(Sync(writer, a) == 1); Assert(File.Exists(Path.Combine(outside, TvArchivePlan.EpisodeBase(a.Snapshot) + ".disc")));
            });
            Test("ancestor archive symlink swapped after construction is rejected", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var outside = Path.Combine(data, "outside"); Directory.Move(Path.Combine(data, "archive"), outside); Directory.CreateSymbolicLink(Path.Combine(data, "archive"), outside);
                Online(a); Assert(Sync(writer, a) == 1); Assert(File.Exists(Path.Combine(outside, "14", TvArchivePlan.SeriesFolder(a.Snapshot), "Season 01", TvArchivePlan.EpisodeBase(a.Snapshot) + ".disc")));
            });
            Test("dangling file symlink is rejected without replacing it", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var path = EpisodePath(data, a); File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(data, "missing-target"));
                Assert(Sync(writer, a) == 1); Assert(new FileInfo(path).LinkTarget != null && !File.Exists(Path.Combine(data, "missing-target")));
            });
            Test("symlink ownership marker never grants ownership", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var marker = Path.Combine(Season(data, a), ".embyhistory-owned"); var external = Path.Combine(data, "marker-outside"); File.WriteAllText(external, a.SeriesKey);
                File.Delete(marker); File.CreateSymbolicLink(marker, external); Online(a); Assert(Sync(writer, a) == 1); Assert(File.Exists(EpisodePath(data, a, ".disc")));
            });
            Test("modified generated file fails closed on withdrawal", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                File.WriteAllText(EpisodePath(data, a, ".disc"), "user replacement"); Online(a); Assert(Sync(writer, a) == 1);
                Assert(File.ReadAllText(EpisodePath(data, a, ".disc")) == "user replacement");
            });
            Test("marker alone does not authorize deleting an unknown generated-looking filename", data => {
                var a = Episode(); Owned(Show(data, a), a.SeriesKey); Owned(Season(data, a), a.SeriesKey);
                File.WriteAllText(EpisodePath(data, a), "user metadata"); File.WriteAllText(EpisodePath(data, a, ".disc"), ""); Online(a);
                var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                Assert(File.Exists(EpisodePath(data, a, ".disc")) && File.ReadAllText(EpisodePath(data, a)) == "user metadata");
            });
            Test("unknown series.nfo is preserved while correct tvshow.nfo is published", data => {
                var a = Episode(); Owned(Show(data, a), a.SeriesKey); File.WriteAllText(Path.Combine(Show(data, a), "series.nfo"), "user metadata");
                var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                Assert(File.Exists(Path.Combine(Show(data, a), "tvshow.nfo")) && File.ReadAllText(Path.Combine(Show(data, a), "series.nfo")) == "user metadata");
            });
            Test("series rename keeps existing directory and .disc basename/key", data => {
                var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0); var directory = Show(data, a); var disc = EpisodePath(data, a, ".disc"); var key = a.Key;
                a.Snapshot.SeriesName = "改名后的剧"; Assert(Sync(writer, a) == 0);
                Assert(Directory.Exists(directory) && File.Exists(disc) && !Directory.Exists(Show(data, a)) && a.Key == key);
                Online(a); Assert(Sync(writer, a) == 0); Assert(!Directory.Exists(directory));
            });
            Test("failed rendering retries without NFO/disc/stamp mtime churn", data => {
                HistoryPosterRenderer.Fail = true; var a = Episode(); var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var before = Snapshot(data, true); var calls = HistoryPosterRenderer.Calls; Assert(Sync(writer, a) == 0); Same(before, Snapshot(data));
                Assert(HistoryPosterRenderer.Calls > calls && writer.LastChangedPaths.Count == 0);
                HistoryPosterRenderer.Fail = false; Assert(Sync(writer, a) == 0); Assert(File.Exists(Path.Combine(Show(data, a), "poster.jpg")));
                Assert(File.GetLastWriteTimeUtc(EpisodePath(data, a, ".disc")) == Sentinel);
            });
            Test("poster source path changes invalidate poster even with equal mtime/length", data => {
                var a = Episode(); Directory.CreateDirectory(data); var first = Path.Combine(data, "one.image"); var next = Path.Combine(data, "two.image");
                File.WriteAllText(first, "first"); File.WriteAllText(next, "other"); File.SetLastWriteTimeUtc(first, Sentinel); File.SetLastWriteTimeUtc(next, Sentinel);
                a.Snapshot.SeriesPosterPath = first; var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0); var calls = HistoryPosterRenderer.Calls;
                a.Snapshot.SeriesPosterPath = next; Assert(Sync(writer, a) == 0); Assert(HistoryPosterRenderer.Calls == calls + 1);
                Assert(writer.LastChangedPaths.Contains(Path.Combine(Show(data, a), "poster.jpg")));
            });
            Test("movie paths share idempotent manifest and safe explicit-file withdrawal", data => {
                var a = Episode(); a.Mode = IdentityMode.Code; var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                var before = Snapshot(data, true); Assert(Sync(writer, a) == 0); Same(before, Snapshot(data));
                var directory = Path.Combine(data, "archive", "14", a.Key); File.WriteAllText(Path.Combine(directory, "private.nfo"), "keep");
                Online(a); Assert(Sync(writer, a) == 0); Assert(!File.Exists(Path.Combine(directory, "history.disc")) && File.ReadAllText(Path.Combine(directory, "private.nfo")) == "keep");
            });
            Test("specials season zero and invalid XML title characters remain valid", data => {
                var a = Episode(1, 0); a.Snapshot.Name = "bad\u0001title\ud800"; a.Snapshot.SeriesName = "show\u0001name";
                var writer = new ArchiveWriter(data); Assert(Sync(writer, a) == 0);
                Assert(XDocument.Load(EpisodePath(data, a)).Root!.Element("season")!.Value == "0");
            });
            Test("changed library snapshot includes only configured source library with real changes", data => {
                var a = Episode(); var other = Episode(2, library: "15"); var writer = new ArchiveWriter(data); Assert(Sync(writer, a, other) == 0);
                Assert(writer.LastChangedLibraryIds.SequenceEqual(new[] { "14" }) && !Directory.Exists(Path.Combine(data, "archive", "15")));
            });
            Test("1005-episode whole-series batch has zero repeated writes or renders", data => {
                var records = Enumerable.Range(1, 1005).Select(n => Episode(n)).ToArray(); var writer = new ArchiveWriter(data);
                var timer = System.Diagnostics.Stopwatch.StartNew(); Assert(Sync(writer, records) == 0); var firstMs = timer.ElapsedMilliseconds;
                var before = Snapshot(data, true); var calls = HistoryPosterRenderer.Calls;
                timer.Restart(); Assert(Sync(writer, records.Reverse().ToArray()) == 0); var secondMs = timer.ElapsedMilliseconds;
                Same(before, Snapshot(data)); Assert(writer.LastChangedPaths.Count == 0 && writer.LastChangedLibraryIds.Count == 0 && HistoryPosterRenderer.Calls == calls);
                Console.WriteLine($"  BATCH episodes={records.Length} files={before.Count} first_ms={firstMs} unchanged_ms={secondMs} unchanged_writes=0 unchanged_renders=0");
            });
            Console.WriteLine($"RESULT passed={_passed} failed={_failed}; renderer=MOCK; end_to_end=NOT_RUN");
            return _failed == 0 ? 0 : 1;
        }
        finally { Directory.Delete(Root, true); }
    }
}

namespace EmbyHistory
{
    internal static class HistoryPosterRenderer
    {
        internal static int Calls;
        internal static bool Fail;
        public static byte[] Render(HistoryRecord record)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("intentional mock-render failure");
            return Encoding.UTF8.GetBytes("MOCK-NOT-JPEG:" + record.Key + ":" + record.Snapshot.PosterPath);
        }
    }
}
