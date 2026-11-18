using System.Collections;
using System.Reflection;
using EmbyHistory;
using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

// No Emby server, network, timer, native renderer or production API is used.
public class InterfaceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var instance = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)instance).Handler = handler;
        return instance;
    }
}

internal sealed class Fixture : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR required"), "emby-retry-" + Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public string Data => Path.Combine(Root, "history");
    public Plugin Plugin { get; }
    public EntryPoint Entry { get; }
    public HistoryStore Store { get; }
    public readonly Dictionary<long, BaseItem> Live = new();
    public readonly List<(int Offset, bool Count)> Queries = new();
    public readonly List<Exception> Errors = new();
    public bool ExpectUnavailableSource;
    public Func<InternalItemsQuery, (BaseItem[] Items, int Total)> Pages = _ => (Array.Empty<BaseItem>(), 0);
    public Dictionary<string, DateTime> Retry => Get<Dictionary<string, DateTime>>("_reconcile");
    public IDictionary Proofs => Get<IDictionary>("_containerProofs");
    public HashSet<string> Dirty => Get<HashSet<string>>("_dirtyLibraries");

    public Fixture()
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Data);
        Plugin = new Plugin { HistoryDataPath = Data, Configuration = new Configuration { Enabled = true,
            Sources = new[] { new SourceRule { LibraryId = "14", Mode = IdentityMode.SeriesEpisode } } } };
        EmbyHistory.Plugin.Instance = Plugin;
        var log = InterfaceProxy.Create<ILogger>((m, a) => { if (m.Name == "ErrorException") { Errors.Add((Exception)a![1]!); Console.WriteLine("  LOG " + a[0] + ": " + a[1]!.GetType().Name); } return Default(m.ReturnType); });
        var logs = InterfaceProxy.Create<ILogManager>((m, a) => m.Name == "GetLogger" ? log : Default(m.ReturnType));
        var library = InterfaceProxy.Create<ILibraryManager>(LibraryCall);
        Entry = new EntryPoint(library, null!, logs, null!, null!);
        Store = new HistoryStore(Data);
        Set("_store", Store);
        Set("_signature", "14:3|14:");
        Set("_modes", new Dictionary<string, IdentityMode> { ["14"] = IdentityMode.SeriesEpisode });
        Set("_nextScan", DateTime.UtcNow.AddHours(1));
        Set("_nextAccessSync", DateTime.UtcNow.AddHours(1));
        Set("_needsSync", false);
        Set("_startupIndex", false);
        ArchiveWriter.SyncCalls.Clear(); ArchiveWriter.Failures = 0;
        HistoryStatus.Update(s => { s.LedgerPhase = "Pending"; s.LedgerError = ""; s.Stage = "Starting"; s.LastErrorUtc = null; });
    }
    private object? LibraryCall(MethodInfo method, object?[]? args)
    {
        switch (method.Name)
        {
            case "GetVirtualFolders":
            {
                var collection = Collection(method.ReturnType);
                var element = collection.Element;
                var folder = Activator.CreateInstance(element)!;
                element.GetProperty("ItemId")!.SetValue(folder, "14");
                element.GetProperty("Locations")!.SetValue(folder, new[] { Source });
                collection.List.Add(folder);
                return collection.Finish();
            }
            case "GetItemsResult":
            {
                var query = (InternalItemsQuery)args![0]!;
                Program.Check(query.ParentIds.SequenceEqual(new long[] { 14 }) && query.Recursive, "real query must scope source recursively");
                Program.Check(query.IncludeItemTypes.SequenceEqual(new[] { "Episode" }), "series rule must query episodes");
                Queries.Add((query.StartIndex ?? 0, query.EnableTotalRecordCount));
                var page = Pages(query);
                var result = Activator.CreateInstance(method.ReturnType)!;
                method.ReturnType.GetProperty("Items")!.SetValue(result, page.Items);
                method.ReturnType.GetProperty("TotalRecordCount")!.SetValue(result, page.Total);
                return result;
            }
            case "GetItemById":
                return Live.GetValueOrDefault(Convert.ToInt64(args![0]));
            case "GetItemPeople":
                return Collection(method.ReturnType).Finish();
            default:
                if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
                throw new InvalidOperationException("Unconfigured SDK call: " + method);
        }
    }
    private static (Type Element, IList List, Func<object> Finish) Collection(Type type)
    {
        var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        return (element, list, () => { if (!type.IsArray) return list; var a = Array.CreateInstance(element, list.Count); list.CopyTo(a, 0); return a; });
    }
    private static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    public T Get<T>(string name) => (T)typeof(EntryPoint).GetField(name, Private)!.GetValue(Entry)!;
    public void Set(string name, object value) => typeof(EntryPoint).GetField(name, Private)!.SetValue(Entry, value);
    public object? Invoke(string name, params object?[] args)
    {
        try { return typeof(EntryPoint).GetMethod(name, Private)!.Invoke(Entry, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
    public bool Scan() => (bool)Invoke("Scan", Plugin, "14")!;
    public async Task Process() => await (Task)Invoke("ProcessAsync")!;
    public void Periodic() => Set("_nextScan", DateTime.MinValue);
    public Episode Episode(int number, long series = 500)
    {
        var episode = new Episode { InternalId = 1000 + number, Name = "Episode " + number, Path = Path.Combine(Source, series + "-" + number + ".mkv"),
            SeriesId = series, SeriesName = "Series " + series, ParentIndexNumber = 1, IndexNumber = number,
            GenreItems = Array.Empty<MediaBrowser.Model.Dto.LinkedItemInfo>(), TagItems = Array.Empty<MediaBrowser.Model.Dto.LinkedItemInfo>() };
        // Detached SDK entities have no server IDataContext; declare these empty metadata arrays loaded.
        typeof(BaseItem).GetField("_taggedItemsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(episode, true);
        return episode;
    }
    public void Seed(params Episode[] episodes)
    {
        foreach (var e in episodes)
        {
            Live[e.InternalId] = e;
            Store.Observe(new MediaSnapshot { ItemId = e.InternalId.ToString(), LibraryId = "14", Name = e.Name, Path = e.Path,
                SeriesId = e.SeriesId.ToString(), SeriesName = e.SeriesName, SeasonNumber = e.ParentIndexNumber, EpisodeNumber = e.IndexNumber }, IdentityMode.SeriesEpisode);
        }
        Store.Flush();
    }
    public void EnqueueContainer(long series = 500, int? season = null)
    {
        var type = typeof(EntryPoint).GetNestedType("Change", BindingFlags.NonPublic)!;
        var change = Activator.CreateInstance(type, new object?[] { series.ToString(), "14", null, false, true, series.ToString(), season })!;
        Invoke("TryEnqueue", change);
    }
    public HistoryRecord Record(long id, bool persisted = false) => (persisted ? new HistoryStore(Data) : Store).GetRecords().Single(r => r.Snapshot.ItemId == id.ToString());
    public void ShortRetry(DateTime before, DateTime after)
    {
        Program.Check(Retry.TryGetValue("14", out var at), "missing short retry for library 14");
        Program.Check(at >= before.AddSeconds(30) && at <= after.AddSeconds(30), "retry must be now + 30 seconds; got " + at.ToString("O"));
    }
    public void Dispose() { Entry.Dispose(); EmbyHistory.Plugin.Instance = null; Directory.Delete(Root, true); }
}

internal static class Program
{
    private static int _passed, _failed;
    public static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Test(string name, Func<Fixture, Task> action)
    {
        try { using var f = new Fixture(); await action(f); Check(f.Errors.All(e => f.ExpectUnavailableSource && e is IOException), "worker swallowed unexpected exception: " + string.Join("; ", f.Errors)); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static Task Done => Task.CompletedTask;
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--inspect-sdk"))
        {
            foreach (var f in typeof(BaseItem).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(f => f.Name.Contains("tag", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("genre", StringComparison.OrdinalIgnoreCase))) Console.WriteLine(f);
            foreach (var p in typeof(BaseItem).GetProperties().Where(p => p.Name.Contains("tag", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("genre", StringComparison.OrdinalIgnoreCase))) Console.WriteLine(p + " writable=" + p.CanWrite);
            return 0;
        }
        Console.WriteLine("MODE linked production EntryPoint.Scan/ProcessAsync + real ledger + sdk-current DispatchProxy; archive/index/access boundary doubles; NO production API");
        await Test("shrinking scan returns false, observes metadata, keeps unseen presence", f => {
            var a = f.Episode(1); var b = f.Episode(2); f.Seed(a, b); a.Overview = "observed partial";
            f.Pages = _ => (new BaseItem[] { a }, 2);
            Check(!f.Scan(), "partial scan falsely reported completion");
            Check(f.Record(a.InternalId).Snapshot.Overview == "observed partial", "partial observations dropped");
            Check(f.Record(b.InternalId).ActiveItemIds.Contains(b.InternalId.ToString()), "unseen active item was inferred removed");
            return Done;
        });
        await Test("repeated pagination retries once and returns false without mass removal", f => {
            var items = Enumerable.Range(1, 256).Select(i => f.Episode(i)).ToArray(); var missing = f.Episode(999); f.Seed(missing);
            f.Pages = q => q.StartIndex == 0 ? (items.Cast<BaseItem>().ToArray(), 257) : (new BaseItem[] { items[0] }, 257);
            Check(!f.Scan(), "repeated page falsely reported completion");
            Check(f.Queries.Select(q => q.Offset).SequenceEqual(new[] { 0, 256, 0, 256 }), "must retry paging exactly once");
            Check(f.Queries.Select(q => q.Count).SequenceEqual(new[] { true, false, true, false }), "total count only on first pages");
            Check(f.Record(missing.InternalId).RemovedUtc == null, "unstable paging removed unseen record");
            return Done;
        });
        await Test("periodic partial scan creates 30-second retry and persists partial observations", async f => {
            var a = f.Episode(1); var b = f.Episode(2); f.Seed(a, b); a.Overview = "persist partial";
            f.Pages = _ => (new BaseItem[] { a }, 2); f.Periodic();
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after);
            Check(f.Get<DateTime>("_nextScan") > after.AddMinutes(4), "periodic cadence not advanced");
            Check(f.Record(a.InternalId, true).Snapshot.Overview == "persist partial", "partial observation not flushed");
            Check(f.Record(b.InternalId, true).RemovedUtc == null, "partial scan inferred removal");
            Check(ArchiveWriter.SyncCalls.Count == 1 && ArchiveWriter.SyncCalls[0].SetEquals(new[] { "14" }), "partial scan was not published to archive boundary");
        });
        await Test("scheduled partial retry keeps container proof and exact deletion persists", async f => {
            var removed = f.Episode(1); var pending = f.Episode(2); f.Seed(removed, pending); f.Live.Remove(removed.InternalId);
            f.EnqueueContainer(); f.Pages = _ => (new BaseItem[] { pending }, 2);
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after);
            Check(f.Proofs.Count == 1, "partial scan discarded series proof");
            Check(f.Record(removed.InternalId, true).RemovedUtc != null, "exact event proof removal not flushed");
            Check(f.Record(pending.InternalId, true).RemovedUtc == null, "still-live cascade child removed");
            Check(ArchiveWriter.SyncCalls.Count == 1, "partial exact removals not sent to archive boundary");
            Check(HistoryStatus.Read().LedgerPhase == "Held", "partial state must be Held");
        });
        await Test("repeated-page periodic scan creates short retry and archives observations", async f => {
            var items = Enumerable.Range(1, 256).Select(i => f.Episode(i)).ToArray();
            f.Pages = q => q.StartIndex == 0 ? (items.Cast<BaseItem>().ToArray(), 257) : (new BaseItem[] { items[0] }, 257);
            f.Periodic(); var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after);
            Check(new HistoryStore(f.Data).GetRecords().Count == 256, "repeated-page partial observations not persisted");
            Check(ArchiveWriter.SyncCalls.Count == 1, "repeated-page partial scan skipped archive publication");
        });
        await Test("held empty library retains active records, proof and short retry", async f => {
            var a = f.Episode(1); f.Seed(a); f.EnqueueContainer();
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after); Check(f.Proofs.Count == 1, "held empty scan lost proof");
            Check(f.Record(a.InternalId, true).RemovedUtc == null && HistoryStatus.Read().LedgerPhase == "Held", "empty scan must hold active ledger");
        });
        await Test("empty library fully proven removed completes and clears retry/proof", async f => {
            var a = f.Episode(1); f.Seed(a); f.Live.Clear(); f.EnqueueContainer();
            await f.Process();
            Check(f.Record(a.InternalId, true).RemovedUtc != null, "exact empty-library deletion was not persisted");
            Check(f.Retry.Count == 0 && f.Proofs.Count == 0, "all-proven empty library not completed");
            Check(HistoryStatus.Read().LedgerPhase == "Completed", "empty proven completion state incorrect");
        });
        await Test("unavailable source mount retains deletion proof and retries without removals", async f => {
            var a = f.Episode(1); f.Seed(a); f.Live.Clear(); f.EnqueueContainer(); Directory.Delete(f.Source); f.ExpectUnavailableSource = true;
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after); Check(f.Proofs.Count == 1, "mount failure lost container proof");
            Check(f.Record(a.InternalId, true).RemovedUtc == null, "proof applied before mount validation");
            Check(f.Queries.Count == 0, "enumeration executed before source validation");
        });
        await Test("short retry executes before periodic deadline and completes cascade", async f => {
            var a = f.Episode(1); var b = f.Episode(2); f.Seed(a, b); f.EnqueueContainer();
            f.Pages = _ => (new BaseItem[] { a }, 2); f.Live.Remove(b.InternalId);
            await f.Process(); Check(f.Proofs.Count == 1 && f.Retry.Count == 1, "initial partial proof/retry missing");
            var priorQueries = f.Queries.Count; await f.Process(); Check(f.Queries.Count == priorQueries, "retry executed before due");
            f.Live.Clear(); f.Pages = _ => (Array.Empty<BaseItem>(), 0); f.Retry["14"] = DateTime.UtcNow.AddMilliseconds(-1);
            await f.Process();
            Check(f.Queries.Count == priorQueries + 1, "due short retry did not run independently of periodic deadline");
            Check(f.Record(a.InternalId, true).RemovedUtc != null && f.Record(b.InternalId, true).RemovedUtc != null, "cascade removals not persisted");
            Check(f.Retry.Count == 0 && f.Proofs.Count == 0, "completed retry not cleared");
        });
        await Test("complete ordinary scan clears scheduled retry and infers only unseen removal", async f => {
            var a = f.Episode(1); var b = f.Episode(2); f.Seed(a, b);
            f.Retry["14"] = DateTime.MinValue; f.Pages = _ => (new BaseItem[] { a }, 1); await f.Process();
            Check(f.Retry.Count == 0, "complete scan retained retry");
            Check(f.Record(a.InternalId, true).RemovedUtc == null && f.Record(b.InternalId, true).RemovedUtc != null, "complete reconcile behavior incorrect");
        });
        await Test("partial scan remains dirty when archive sync fails", async f => {
            f.Pages = _ => (new BaseItem[] { f.Episode(1) }, 2); f.Periodic(); ArchiveWriter.Failures = 1;
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            f.ShortRetry(before, after); Check(f.Dirty.SetEquals(new[] { "14" }), "partial source lost dirty flag on archive failure");
            Check(f.Get<bool>("_needsSync"), "archive failure lost sync retry state");
        });
        await Test("UNCOMMITTED cascade with complete nonempty scan must retain proof and short retry", async f => {
            var a = f.Episode(1); var b = f.Episode(2); f.Seed(a, b); f.Live.Remove(a.InternalId);
            f.EnqueueContainer(); f.Pages = _ => (new BaseItem[] { b }, 1);
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Check(f.Record(a.InternalId, true).RemovedUtc != null, "committed child removal not applied");
            Check(f.Record(b.InternalId, true).RemovedUtc == null, "uncommitted child incorrectly removed");
            var proofCount = f.Proofs.Count; var retryCount = f.Retry.Count; var queryCount = f.Queries.Count;
            // Simulate the remaining SDK deletion committing without another per-episode event.
            f.Live.Clear(); f.Pages = _ => (Array.Empty<BaseItem>(), 0);
            await f.Process();
            Console.WriteLine($"  CASCADE evidence: after full scan proofs={proofCount} retries={retryCount}; next worker extraQueries={f.Queries.Count - queryCount} remainingChildRemoved={f.Record(b.InternalId, true).RemovedUtc.HasValue}");
            Check(proofCount == 1, "series deletion proof discarded while a matching child is still live");
            f.ShortRetry(before, after);
            f.Retry["14"] = DateTime.UtcNow.AddMilliseconds(-1);
            await f.Process();
            Check(f.Queries.Count > queryCount, "due cascade retry did not query without a new episode event");
            Check(f.Record(b.InternalId, true).RemovedUtc.HasValue, "last child was not removed after cascade commit");
            Check(f.Proofs.Count == 0 && f.Retry.Count == 0, "completed cascade retained stale evidence");
        });
        Console.WriteLine($"RESULT {_passed}/{_passed + _failed} passed; {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
