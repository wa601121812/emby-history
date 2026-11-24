using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyHistory;
using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;

if (args.Contains("--inspect-sdk")) { SdkInspection.Run(); return 0; }
var passed = 0; var failed = 0;
void Test(string name, Action<Fixture> action)
{
    try { using var f = new Fixture(); action(f); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
}
static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }

Console.WriteLine("MODE linked production playback service/store + actual sdk-current entities/DispatchProxy; no server, no credentials, no production writes. Native GUI/DTO percentage NOT tested.");
Test("SDK has no ItemRemoving; settable UserDataKey is not the native key", f => {
    Check(typeof(ILibraryManager).GetEvent("ItemRemoving") == null);
    var m = new Movie { Id = Guid.NewGuid(), UserDataKey = "fabricated-key" };
    Check(((BaseItem)m).GetUserDataKey(null!) != "fabricated-key");
});
Test("initial inventory captures each authorized user's own progress and duration", f => {
    f.SourceData(1, 120); f.SourceData(2, 0, true, 4); f.SourceData(3, 200);
    var result = f.Service.CaptureRecords(new[] { f.Record });
    Check(result.Captured == 2 && result.Failed == 0, JsonSerializer.Serialize(result));
    var rows = f.Service.Store.Read(); Check(rows.Count == 2 && rows.All(r => r.DurationTicks == Fixture.Duration));
    Check(rows.Single(r => r.UserId == 1).PositionTicks == Fixture.Ticks(120));
    Check(rows.Single(r => r.UserId == 2).Played && rows.Single(r => r.UserId == 2).PlayCount == 4);
    Check(!File.ReadAllText(f.Service.Store.FilePath).Contains("SECRET"));
});
Test("capture rejects wrong library, unauthorized, disabled, source id normalization", f => {
    f.Service.Capture(f.Source, "15", f.Record.Key); Check(f.Service.Store.Read().Count == 0);
    var result = f.Service.Capture(f.Source, "014", f.Record.Key); Check(result.Failed == 1);
    f.Disabled.Add(1); f.Disabled.Add(2); f.Service.Capture(f.Source, "14", f.Record.Key);
    Check(f.Service.Store.Read().Count == 0);
});
Test("events subscribe once, capture just event user, unsubscribe on dispose", f => {
    f.Service.Start(() => new[] { f.Record }); f.Service.Start(() => new[] { f.Record });
    Check(f.SavedAdds == 1 && f.RemovedAdds == 1);
    f.Emit(1, 110); f.Emit(3, 210);
    Check(f.Service.Store.Read().Count == 1 && f.Service.Store.Read().Single().UserId == 1);
    f.Service.Dispose(); Check(f.Saved == null && f.Removed == null);
});
Test("null/default observations never erase evidence, duration survives missing runtime", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key);
    f.Source.RunTimeTicks = null; f.SourceData(1, 0, key: "source-101");
    f.Service.Capture(f.Source, "14", f.Record.Key);
    var row = f.Service.Store.Read().Single(r => r.UserId == 1); Check(row.PositionTicks == Fixture.Ticks(100) && row.DurationTicks == Fixture.Duration);
    f.SourceData(1, 150); f.Service.Capture(f.Source, "14", f.Record.Key);
    row = f.Service.Store.Read().Single(r => r.UserId == 1); Check(row.PositionTicks == Fixture.Ticks(150) && row.DurationTicks == Fixture.Duration);
});
Test("event snapshot detached from mutable SDK objects and persists across restart", f => {
    f.Service.Start(() => new[] { f.Record }); var value = f.Emit(1, 130); value.PlaybackPositionTicks = 0;
    var stored = new HistoryPlaybackStore(f.Root).Read().Single(); Check(stored.PositionTicks == Fixture.Ticks(130));
});
Test("explicit pre-delete hook captures final position before source disappears", f => {
    f.SourceData(1, 160); f.Service.Start(() => new[] { f.Record });
    f.Service.CaptureRemoving(f.Source); f.Items.Remove(101);
    Check(new HistoryPlaybackStore(f.Root).Read().Single(r => r.UserId == 1).PositionTicks == Fixture.Ticks(160));
});
Test("container pre-delete hook captures descendants without per-child events", f => {
    f.SourceData(1, 140); f.Service.Start(() => new[] { f.Record });
    f.Service.CaptureRemoving(new Folder { InternalId = 99, Path = Path.GetDirectoryName(f.Source.Path)! });
    Check(f.Service.Store.Read().Single(r => r.UserId == 1).PositionTicks == Fixture.Ticks(140));
});
Test("owned history restores two users independently, runtime persists and source never writes", f => {
    f.SourceData(1, 120); f.SourceData(2, 0, true, 3); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    var result = f.Service.Restore(f.Record, f.History);
    Check(result.Failed == 0 && result.Restored == 2, JsonSerializer.Serialize(result) + string.Join(";", f.Errors.Select(e => e.Message)));
    Check(f.History.RunTimeTicks == Fixture.Duration && f.RuntimeWrites == 1);
    Check(f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(120)); Check(f.ArchiveData(2).Played && f.ArchiveData(2).PlayCount == 3);
    Check(f.SaveTargets.All(id => id == 901) && !f.SaveTargets.Contains(101));
});
Test("restore preserves favorite, rating, track preferences; source data is immutable", f => {
    var source = f.SourceData(1, 125); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    var archive = f.ArchiveData(1); archive.IsFavorite = true; archive.Rating = 7; archive.AudioStreamIndex = 2; archive.SubtitleStreamIndex = 3; archive.HideFromResume = true;
    f.Service.Restore(f.Record, f.History); var after = f.ArchiveData(1);
    Check(after.IsFavorite && after.Rating == 7 && after.AudioStreamIndex == 2 && after.SubtitleStreamIndex == 3 && after.HideFromResume);
    Check(archive.PlaybackPositionTicks == 0 && source.PlaybackPositionTicks == Fixture.Ticks(125));
});
Test("restore is idempotent and reapplies duration after metadata refresh", f => {
    f.SourceData(1, 115); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.Service.Restore(f.Record, f.History); f.Service.Restore(f.Record, f.History);
    Check(f.SaveTargets.Count == 1 && f.RuntimeWrites == 1);
    f.History.RunTimeTicks = null; f.Service.Restore(f.Record, f.History);
    Check(f.SaveTargets.Count == 1 && f.RuntimeWrites == 2 && f.History.RunTimeTicks == Fixture.Duration);
});
Test("RestoreRecords queries only mapped target and correct key, waits on ambiguity", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    var result = f.Service.RestoreRecords(new[] { f.Record }); Check(result.Restored == 1);
    Check(f.QueryParents.Single().SequenceEqual(new long[] { 50 }));
    f.Candidates.Add(f.History); f.SaveTargets.Clear(); result = f.Service.RestoreRecords(new[] { f.Record }); Check(result.Skipped == 1 && f.SaveTargets.Count == 0);
});
Test("missing/unmapped target is retryable without broad source query", f => {
    f.RemoveSource(); f.TargetMapped = false; var result = f.Service.RestoreRecords(new[] { f.Record }); Check(result.Skipped == 1 && f.QueryParents.Count == 0);
});
Test("source or unrelated archive candidate can never be restored", f => {
    f.SourceData(1, 120); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    Check(f.Service.Restore(f.Record, f.Source).Skipped == 1);
    f.History.ProviderIds["embyhistory"] = "wrong"; Check(f.Service.Restore(f.Record, f.History).Skipped == 1);
    Check(f.SaveTargets.Count == 0 && f.RuntimeWrites == 0);
});
Test("ownership manifest rejects externally modified NFO and sibling-prefix paths", f => {
    f.SourceData(1, 120); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    File.AppendAllText(Path.Combine(Path.GetDirectoryName(f.History.Path)!, "movie.nfo"), "changed");
    Check(f.Service.Restore(f.Record, f.History).Skipped == 1 && f.SaveTargets.Count == 0);
    f.History.Path = f.History.Path.Replace("/archive/14/", "/archive/140/"); Check(f.Service.Restore(f.Record, f.History).Skipped == 1);
});
Test("shared native playback keys refuse writes rather than mutate source state", f => {
    f.SourceData(1, 100, key: "shared-provider"); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.ArchiveData(1).Key = "shared-provider";
    var result = f.Service.Restore(f.Record, f.History); Check(result.Failed == 1 && f.SaveTargets.Count == 0);
    Check(f.History.RunTimeTicks == Fixture.Duration);
});
Test("already shared identical native state needs only history runtime, zero userdata writes", f => {
    var source = f.SourceData(1, 100, key: "shared-provider"); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.Values[(1, 901)] = source;
    var result = f.Service.Restore(f.Record, f.History); Check(result.Failed == 0 && f.SaveTargets.Count == 0 && f.History.RunTimeTicks == Fixture.Duration);
});
Test("revoked history visibility and replaced account GUID never copy another account", f => {
    f.SourceData(1, 100); f.SourceData(2, 130); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.Invisible.Add((1, 901)); f.Users[1].Id = Guid.NewGuid();
    Check(f.Service.Restore(f.Record, f.History).Restored == 0 && f.SaveTargets.Count == 0);
});
Test("blank source/default evidence cannot wipe nonzero historical progress", f => {
    f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource(); f.ArchiveData(1).PlaybackPositionTicks = Fixture.Ticks(180);
    f.Service.Restore(f.Record, f.History); Check(f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(180) && f.SaveTargets.Count == 0);
});
Test("newer edits on historical card are not overwritten by old snapshot", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.ArchiveData(1).PlaybackPositionTicks = Fixture.Ticks(220); f.ArchiveData(1).PlaystateLastModified = DateTimeOffset.UtcNow.AddMinutes(1);
    f.Service.Restore(f.Record, f.History); Check(f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(220) && f.SaveTargets.Count == 0);
});
Test("duplicate versions retain rows and choose latest dated playback, not maximum position", f => {
    f.SourceData(1, 200, last: DateTimeOffset.UtcNow.AddDays(-2)); f.Service.Capture(f.Source, "14", f.Record.Key);
    f.Source.InternalId = 102; f.Items[102] = f.Source; f.SourceData(1, 50, last: DateTimeOffset.UtcNow.AddDays(-1));
    f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.Service.Restore(f.Record, f.History); Check(f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(50));
    Check(f.Service.Store.Read().Count(r => r.UserId == 1) == 2);
});
Test("old deleted rows recover only retained numeric-id evidence + explicit authorization", f => {
    f.SourceData(1, 80); f.SourceData(2, 100); f.RemoveSource();
    var result = f.Service.RecoverRemovedRecords(new[] { f.Record }, (u, r) => u.InternalId == 1);
    Check(result.Captured == 1 && f.Service.Store.Read().Single().UserId == 1);
    Check(f.Service.Store.Read().Single().DurationTicks == null); // never infer from position
    f.Service.Restore(f.Record, f.History); Check(f.History.RunTimeTicks == null && f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(80));
});
Test("deleted userdata absent/blank is not invented, without authorization no read", f => {
    f.RemoveSource(); f.Service.RecoverRemovedRecords(new[] { f.Record }, (u, r) => false); Check(f.NumericReads == 0);
    f.Service.RecoverRemovedRecords(new[] { f.Record }, (u, r) => true); Check(f.Service.Store.Read().Count == 0);
});
Test("atomic store backup recovers corrupted primary and preserves corrupt evidence", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); var path = f.Service.Store.FilePath;
    File.WriteAllText(path, "broken"); var recovered = new HistoryPlaybackStore(f.Root); Check(recovered.Read().Count == 2); recovered.Flush();
    Check(Directory.GetFiles(f.Root, "*.corrupt-*").Length == 1 && new HistoryPlaybackStore(f.Root).Read().Count == 2);
});
Test("both corrupt sidecars fail closed, service exceptions do not escape", f => {
    File.WriteAllText(Path.Combine(f.Root, "playback-state.v1.json"), "broken"); File.WriteAllText(Path.Combine(f.Root, "playback-state.v1.json.bak"), "broken");
    var result = f.Service.Capture(f.Source, "14", f.Record.Key); Check(result.Failed > 0 && File.ReadAllText(Path.Combine(f.Root, "playback-state.v1.json")) == "broken");
});
Test("sidecar file is private and no-change capture does not replace it", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); var path = f.Service.Store.FilePath; var stamp = File.GetLastWriteTimeUtc(path);
    f.Service.Capture(f.Source, "14", f.Record.Key); Check(File.GetLastWriteTimeUtc(path) == stamp);
    if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
});
Test("archive symlinks are refused", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    var old = f.History.Path; var link = Path.Combine(f.Root, "archive", "14", "linked"); Directory.CreateSymbolicLink(link, Path.GetDirectoryName(old)!);
    f.History.Path = Path.Combine(link, Path.GetFileName(old)); Check(f.Service.Restore(f.Record, f.History).Skipped == 1 && f.SaveTargets.Count == 0);
});
Test("live/reimported record never receives archive restore", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key);
    Check(f.Service.Restore(f.Record, f.History).Skipped == 1 && f.SaveTargets.Count == 0);
});
Test("SDK save failure isolated per-user; runtime save readback enforced", f => {
    f.SourceData(1, 100); f.SourceData(2, 110); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    f.FailSaveUser = 1; var result = f.Service.Restore(f.Record, f.History); Check(result.Failed == 1 && result.Restored == 1 && f.ArchiveData(2).PlaybackPositionTicks == Fixture.Ticks(110));
});
Test("concurrent captures preserve separate rows in valid atomic store", f => {
    Parallel.For(0, 30, i => {
        f.Service.Store.Put(new HistoryPlaybackSnapshot { LibraryId = "14", RecordKey = "parallel-" + i, UserId = 1, UserGuid = f.Users[0].Id, SourceItemId = 1000 + i, PositionTicks = i + 1, DurationTicks = Fixture.Duration, CapturedUtc = DateTimeOffset.UtcNow }); f.Service.Store.Flush();
    });
    Check(new HistoryPlaybackStore(f.Root).Read().Count == 30);
});
Test("actual SDK provider keys are shared despite distinct history item GUIDs", f => {
    var a = new Movie { Id = Guid.NewGuid() }; var b = new Movie { Id = Guid.NewGuid() };
    a.ProviderIds["Tmdb"] = "123"; b.ProviderIds["Tmdb"] = "123";
    Check(((BaseItem)a).GetUserDataKey(null!) == ((BaseItem)b).GetUserDataKey(null!));
});
Test("episode history obeys exact episode key + series owner and restores per-user state", f => {
    var source = new Episode { InternalId = 101, Id = Guid.NewGuid(), Path = f.Source.Path, RunTimeTicks = Fixture.Duration, ParentIndexNumber = 1, IndexNumber = 2, SeriesId = 200 };
    f.Items[101] = source; f.SourceData(1, 90);
    f.Record.Mode = IdentityMode.SeriesEpisode; f.Record.SeriesKey = "series-a"; f.Record.Snapshot.SeriesId = "200"; f.Record.Snapshot.SeasonNumber = 1;
    var episode = new Episode { InternalId = 901, Id = Guid.NewGuid(), Path = f.History.Path, ParentIndexNumber = 1, IndexNumber = 2 };
    episode.ProviderIds["embyhistory"] = f.Record.Key; f.Items[901] = episode;
    var dir = Path.GetDirectoryName(episode.Path)!; var nfo = Path.ChangeExtension(episode.Path, ".nfo");
    File.WriteAllText(nfo, "<episodedetails><uniqueid type=\"embyhistory\">record-a</uniqueid></episodedetails>");
    File.WriteAllText(Path.Combine(dir, ".embyhistory-owned"), "series-a");
    File.WriteAllText(Path.Combine(dir, ".embyhistory-files.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [Path.GetFileName(episode.Path)] = Fixture.Hash(episode.Path), [Path.GetFileName(nfo)] = Fixture.Hash(nfo) }));
    Check(f.Service.Capture(source, "14", f.Record.Key).Captured == 2); f.RemoveSource();
    Check(f.Service.Restore(f.Record, episode).Restored == 1 && episode.RunTimeTicks == Fixture.Duration && f.ArchiveData(1).PlaybackPositionTicks == Fixture.Ticks(90));
});
Test("season pre-delete captures only matching known descendants", f => {
    f.Record.Snapshot.SeriesId = "200"; f.Record.Snapshot.SeasonNumber = 2; f.Service.Start(() => new[] { f.Record });
    f.SourceData(1, 80); f.Service.CaptureRemoving(new Season { InternalId = 300, SeriesId = 200, IndexNumber = 1 }); Check(f.Service.Store.Read().Count == 0);
    f.Service.CaptureRemoving(new Season { InternalId = 301, SeriesId = 200, IndexNumber = 2 }); Check(f.Service.Store.Read().Single(r => r.UserId == 1).PositionTicks == Fixture.Ticks(80));
});
Test("dangling sidecar symlinks fail closed without touching target", f => {
    var target = Path.Combine(f.Root, "must-not-create"); File.CreateSymbolicLink(Path.Combine(f.Root, "playback-state.v1.json"), target);
    Check(f.Service.Capture(f.Source, "14", f.Record.Key).Failed > 0 && !File.Exists(target));
});
Test("cancelled restore does not write userdata or escape into presence worker", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource();
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Check(f.Service.RestoreRecords(new[] { f.Record }, cancelled.Token).Failed > 0 && f.SaveTargets.Count == 0);
});
Test("runtime repository write failure can retry rather than trusting mutated cache", f => {
    f.SourceData(1, 100); f.Service.Capture(f.Source, "14", f.Record.Key); f.RemoveSource(); f.FailRuntime = true;
    Check(f.Service.Restore(f.Record, f.History).Failed == 1 && f.History.RunTimeTicks == null && f.SaveTargets.Count == 0);
    f.FailRuntime = false; Check(f.Service.Restore(f.Record, f.History).Restored == 1 && f.History.RunTimeTicks == Fixture.Duration);
});
Console.WriteLine($"RESULT passed={passed} failed={failed}"); return failed == 0 ? 0 : 1;

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Handler = handler; return value; }
}
internal sealed class Fixture : IDisposable
{
    public static long Ticks(long seconds) => checked(seconds * TimeSpan.TicksPerSecond);
    public static readonly long Duration = Ticks(300);
    public readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR must be set"), "playback-tests-" + Guid.NewGuid().ToString("N"));
    public readonly List<User> Users = Enumerable.Range(1, 4).Select(i => new User { InternalId = i, Id = Guid.NewGuid(), Name = "SECRET-" + i }).ToList();
    public readonly Dictionary<long, BaseItem> Items = new();
    public readonly Dictionary<(long, long), UserItemData> Values = new();
    public readonly HashSet<long> Disabled = new() { 4 };
    public readonly HashSet<(long, long)> Invisible = new();
    public readonly List<Exception> Errors = new();
    public readonly List<long> SaveTargets = new();
    public readonly List<long[]> QueryParents = new();
    public readonly List<BaseItem> Candidates = new();
    public EventHandler<UserDataSaveEventArgs>? Saved;
    public EventHandler<ItemChangeEventArgs>? Removed;
    public int SavedAdds, RemovedAdds, RuntimeWrites, NumericReads;
    public long FailSaveUser;
    public bool TargetMapped = true, FailRuntime;
    public readonly Movie Source;
    public readonly Movie History;
    public readonly HistoryRecord Record;
    public HistoryPlaybackService Service { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Source = new Movie { InternalId = 101, Id = Guid.NewGuid(), Path = Path.Combine(Root, "source", "movie.mkv"), RunTimeTicks = Duration };
        var dir = Path.Combine(Root, "archive", "14", "record-a"); Directory.CreateDirectory(dir);
        History = new Movie { InternalId = 901, Id = Guid.NewGuid(), Path = Path.Combine(dir, "history.disc") };
        History.ProviderIds["embyhistory"] = "record-a";
        File.WriteAllText(Path.Combine(dir, ".embyhistory-owned"), "record-a");
        File.WriteAllBytes(History.Path, Array.Empty<byte>());
        var nfo = Path.Combine(dir, "movie.nfo"); File.WriteAllText(nfo, "<movie><uniqueid type=\"embyhistory\">record-a</uniqueid></movie>");
        File.WriteAllText(Path.Combine(dir, ".embyhistory-files.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["history.disc"] = Hash(History.Path), ["movie.nfo"] = Hash(nfo) }));
        Record = new HistoryRecord { LibraryId = "14", Key = "record-a", Snapshot = new MediaSnapshot { ItemId = "101", LibraryId = "14", Path = Source.Path }, ActiveItemIds = new() { "101" } };
        Items[101] = Source; Items[901] = History; Candidates.Add(History);
        var users = Proxy.Create<IUserManager>((m, a) => m.Name switch {
            "GetUserList" => Users.ToArray(), "GetUserById" => Users.FirstOrDefault(u => u.InternalId == (long)a![0]!),
            "GetUserPolicy" => new UserPolicy { IsDisabled = Disabled.Contains(((User)a![0]!).InternalId) }, _ => Default(m.ReturnType) });
        var library = Proxy.Create<ILibraryManager>((m, a) => {
            switch (m.Name)
            {
                case "GetCollectionFolders": return Collection(m.ReturnType, new Folder { InternalId = 14 });
                case "GetItemById": return Items.GetValueOrDefault((long)a![0]!);
                case "UpdateItem":
                    var item = (BaseItem)a![0]!; if (item.InternalId != 901) throw new Exception("SOURCE WRITE!"); if (FailRuntime) throw new IOException("injected runtime write failure"); RuntimeWrites++; Items[item.InternalId] = item; return null;
                case "GetVirtualFolders":
                    return Collection(m.ReturnType, new VirtualFolderInfo { ItemId = "50", Locations = TargetMapped ? new[] { Path.Combine(Root, "archive", "14") } : Array.Empty<string>() });
                case "GetItemList": var query = (InternalItemsQuery)a![0]!; QueryParents.Add(query.ParentIds); return Candidates.ToArray();
                case "add_ItemRemoved": RemovedAdds++; Removed += (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                case "remove_ItemRemoved": Removed -= (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                default: return Default(m.ReturnType);
            }
        });
        var data = Proxy.Create<IUserDataManager>((m, a) => {
            switch (m.Name)
            {
                case "GetUserData":
                    var user = (User)a![0]!; var id = a[1] is BaseItem b ? b.InternalId : (long)a[1]!;
                    if (a[1] is long) NumericReads++;
                    if (!Values.TryGetValue((user.InternalId, id), out var value)) Values[(user.InternalId, id)] = value = new UserItemData { Key = id == 901 ? "archive-901" : "source-" + id };
                    return value;
                case "SaveUserData":
                    var saveUser = (User)a![0]!; var target = (BaseItem)a[1]!;
                    if (target.InternalId != 901) throw new Exception("SOURCE WRITE!");
                    if (FailSaveUser == saveUser.InternalId) throw new IOException("injected save failure");
                    Values[(saveUser.InternalId, target.InternalId)] = (UserItemData)a[2]!; SaveTargets.Add(target.InternalId);
                    Saved?.Invoke(null, new UserDataSaveEventArgs { User = saveUser, Item = target, UserData = (UserItemData)a[2]! }); return null;
                case "add_UserDataSaved": SavedAdds++; Saved += (EventHandler<UserDataSaveEventArgs>)a![0]!; return null;
                case "remove_UserDataSaved": Saved -= (EventHandler<UserDataSaveEventArgs>)a![0]!; return null;
                default: return Default(m.ReturnType);
            }
        });
        Service = new HistoryPlaybackService(library, users, data, Root, (stage, ex) => Errors.Add(ex), (u, item) => u.InternalId <= 2 && !Invisible.Contains((u.InternalId, item.InternalId)));
    }
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public void RemoveSource() { Record.ActiveItemIds.Clear(); Record.RemovedUtc = DateTime.UtcNow; Items.Remove(101); Items.Remove(102); }
    public UserItemData SourceData(long user, long seconds, bool played = false, int count = 0, string? key = null, DateTimeOffset? last = null)
    {
        var value = new UserItemData { Key = key ?? "source-" + Source.InternalId, PlaybackPositionTicks = Ticks(seconds), Played = played, PlayCount = count, LastPlayedDate = last };
        Values[(user, Source.InternalId)] = value; return value;
    }
    public UserItemData ArchiveData(long user)
    {
        if (!Values.TryGetValue((user, 901), out var value)) Values[(user, 901)] = value = new UserItemData { Key = "archive-901" }; return value;
    }
    public UserItemData Emit(long user, long seconds)
    {
        var value = SourceData(user, seconds); Saved?.Invoke(null, new UserDataSaveEventArgs { User = Users.Single(u => u.InternalId == user), Item = Source, UserData = value, SaveReason = UserDataSaveReason.PlaybackProgress }); return value;
    }
    private static object? Default(Type t) => t == typeof(void) || !t.IsValueType ? null : Activator.CreateInstance(t);
    private static object Collection(Type type, object instance)
    {
        var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        if (!element.IsInstanceOfType(instance)) { var typed = Activator.CreateInstance(element)!; element.GetProperty("InternalId")!.SetValue(typed, ((Folder)instance).InternalId); instance = typed; }
        var array = Array.CreateInstance(element, 1); array.SetValue(instance, 0);
        if (type.IsArray) return array;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!; list.Add(instance); return list;
    }
    public void Dispose() { Service.Dispose(); Directory.Delete(Root, true); }
}
