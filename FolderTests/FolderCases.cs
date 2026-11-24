using System.Reflection;
using EmbyHistory;
using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

internal static class FolderCases
{
    private static Movie Movie(Fixture f, int id, string relative)
    {
        var movie = new Movie { InternalId = id, Name = "Movie " + id, Path = Path.Combine(f.Source, relative),
            GenreItems = Array.Empty<MediaBrowser.Model.Dto.LinkedItemInfo>(), TagItems = Array.Empty<MediaBrowser.Model.Dto.LinkedItemInfo>() };
        typeof(BaseItem).GetField("_taggedItemsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(movie, true);
        f.Live[id] = movie;
        f.Store.Observe(new MediaSnapshot { ItemId = id.ToString(), LibraryId = "14", Name = movie.Name, Path = movie.Path }, IdentityMode.Provider);
        f.Store.Flush();
        return movie;
    }
    private static void RemoveFolder(Fixture f, string path)
    {
        var args = new ItemChangeEventArgs(null!) { Item = new Folder { InternalId = 900, Path = path } };
        f.Invoke("Removed", null, args);
    }
    public static async Task<int> Run()
    {
        var passed = 0; var failed = 0;
        async Task Test(string name, Func<Fixture, Task> run)
        {
            try { using var f = new Fixture(IdentityMode.Provider); await run(f);
                Program.Check(f.Errors.All(e => f.ExpectUnavailableSource && e is IOException), "unexpected worker error");
                passed++; Console.WriteLine("PASS FOLDER " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL FOLDER " + name + ": " + ex); }
        }
        await Test("ordinary folder event immediately removes known movie children without child events", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); var b = Movie(f, 42, "collection/nested/b.mkv");
            f.Live.Clear(); RemoveFolder(f, Path.Combine(f.Source, "collection")); await f.Process();
            Program.Check(f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Record(b.InternalId, true).RemovedUtc.HasValue, "Folder event ignored until five-minute scan");
            Program.Check(f.Queries.Count > 0 && f.Retry.Count == 0 && f.Proofs.Count == 0, "exact last-folder removal should complete despite empty-library protection");
            Program.Check(ArchiveWriter.SyncCalls.Count == 1, "immediate ledger change was not archived");
        });
        await Test("complete half-committed folder cascade keeps proof and 30-second retry", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); var b = Movie(f, 42, "collection/b.mkv"); f.Live.Remove(a.InternalId);
            f.Pages = _ => (new BaseItem[] { b }, 1); RemoveFolder(f, Path.Combine(f.Source, "collection"));
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Program.Check(f.Record(a.InternalId, true).RemovedUtc.HasValue && !f.Record(b.InternalId, true).RemovedUtc.HasValue, "exact proof must distinguish committed and live children");
            Program.Check(f.Proofs.Count == 1, "folder proof retired too early"); f.ShortRetry(before, after);
            f.Live.Clear(); f.Pages = _ => (Array.Empty<BaseItem>(), 0); f.Retry["14"] = DateTime.MinValue; await f.Process();
            Program.Check(f.Record(b.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 0 && f.Retry.Count == 0, "retry must finish cascade without another event");
        });
        await Test("missing filesystem alone is not deletion proof", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); // Never create this file; Emby still has the id.
            RemoveFolder(f, Path.Combine(f.Source, "collection"));
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Program.Check(!f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 1, "filesystem absence removed a live movie"); f.ShortRetry(before, after);
        });
        await Test("inaccessible source retains folder proof", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); f.Live.Clear(); RemoveFolder(f, Path.Combine(f.Source, "collection"));
            Directory.Delete(f.Source); f.ExpectUnavailableSource = true;
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Program.Check(!f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 1 && f.Queries.Count == 0, "unavailable mount must retain presence and proof"); f.ShortRetry(before, after);
        });
        await Test("prefix-adjacent folder is excluded from exact proof", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); var neighbor = Movie(f, 42, "collection-other/b.mkv"); f.Live.Clear();
            f.Pages = _ => (Array.Empty<BaseItem>(), 0); RemoveFolder(f, Path.Combine(f.Source, "collection")); await f.Process();
            Program.Check(f.Record(a.InternalId, true).RemovedUtc.HasValue && !f.Record(neighbor.InternalId, true).RemovedUtc.HasValue, "path prefix crossed directory boundary");
        });
        await Test("unconfigured outside folder cannot scope a deletion", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); f.Live.Clear(); RemoveFolder(f, Path.Combine(f.Root, "outside")); await f.Process();
            Program.Check(!f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Queries.Count == 0 && f.Proofs.Count == 0, "outside path became source evidence");
        });
        await Test("complete listing cannot override live id evidence for folder children", async f => {
            var child = Movie(f, 41, "collection/a.mkv"); var other = Movie(f, 42, "elsewhere/b.mkv");
            f.Pages = _ => (new BaseItem[] { other }, 1); RemoveFolder(f, Path.Combine(f.Source, "collection"));
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Program.Check(!f.Record(child.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 1, "complete listing overrode live child id"); f.ShortRetry(before, after);
        });
        await Test("folder event partial scan persists removals and retries", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); var b = Movie(f, 42, "collection/b.mkv"); f.Live.Remove(a.InternalId);
            f.Pages = _ => (new BaseItem[] { b }, 2); RemoveFolder(f, Path.Combine(f.Source, "collection"));
            var before = DateTime.UtcNow; await f.Process(); var after = DateTime.UtcNow;
            Program.Check(f.Record(a.InternalId, true).RemovedUtc.HasValue && !f.Record(b.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 1, "partial folder scan lost evidence"); f.ShortRetry(before, after);
        });
        await Test("195 historical movies have one stable original-directory plan without moving archive", async f => {
            var source = Path.Combine(f.Source, "195 films");
            var snapshots = Enumerable.Range(1, 195).Select(i => new MediaSnapshot { ItemId = (10000 + i).ToString(), LibraryId = "14", Name = "Film " + i,
                Path = Path.Combine(source, i.ToString(), "movie.mkv") }).ToArray();
            foreach (var snapshot in snapshots) { f.Store.Observe(snapshot, IdentityMode.Provider); f.Store.MarkRemoved(snapshot.ItemId, "14"); }
            f.Store.Flush();
            var before = File.ReadAllBytes(Path.Combine(f.Data, "state.json"));
            var archive = Path.Combine(f.Data, "archive", "14", f.Store.GetRecords()[0].Key); Directory.CreateDirectory(archive);
            var disc = Path.Combine(archive, "history.disc"); File.WriteAllText(disc, "preserve existing disc");
            var roots = new Dictionary<string, string[]> { ["14"] = new[] { f.Source } };
            var plans = HistoryFolderPlan.Build(f.Store.GetRecords(), roots);
            Program.Check(plans.Count == 1 && plans[0].RecordKeys.Count == 195 && plans[0].FolderPath == source && plans[0].DisplayName == "195 films", "195 movies not gathered by original multi-film folder");
            HistoryFolderPlan.Save(f.Data, plans);
            var loaded = HistoryFolderPlan.Load(f.Data); Program.Check(loaded.Count == 1 && loaded[0].Key == plans[0].Key && loaded[0].RecordKeys.SequenceEqual(plans[0].RecordKeys), "saved context lost on restart");
            var statePath = Path.Combine(f.Data, "folder-context.json"); var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(statePath, old);
            HistoryFolderPlan.Save(f.Data, plans); Program.Check(File.GetLastWriteTimeUtc(statePath) == old, "unchanged plan rewritten");
            Program.Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Data, "state.json"))) && File.ReadAllText(disc) == "preserve existing disc", "group plan changed ledger or moved disc");
            f.Store.Observe(snapshots[0], IdentityMode.Provider);
            var returned = HistoryFolderPlan.Build(f.Store.GetRecords(), roots);
            Program.Check(returned.Count == 1 && returned[0].Key == plans[0].Key && returned[0].RecordKeys.Count == 194, "returning movie should leave group stable and exclude active work");
            await Task.CompletedTask;
        });
        await Test("single returned movie leaves single-member historical group", f => {
            var a = Movie(f, 41, "collection/a.mkv"); var b = Movie(f, 42, "collection/b.mkv");
            f.Store.MarkRemoved(b.InternalId.ToString(), "14");
            var groups = HistoryFolderPlan.Build(f.Store.GetRecords(), new Dictionary<string, string[]> { ["14"] = new[] { f.Source } });
            Program.Check(groups.Count == 1 && groups[0].RecordKeys.Count == 1 && groups[0].RecordKeys[0] == f.Record(b.InternalId).Key, "eligible full-folder membership should retain single removed member");
            return Task.CompletedTask;
        });
        await Test("single film directories and root-level movies are not grouped", f => {
            Movie(f, 41, "single/a.mkv"); Movie(f, 42, "other/b.mkv"); Movie(f, 43, "root.mkv");
            foreach (var record in f.Store.GetRecords()) f.Store.MarkRemoved(record.Snapshot.ItemId, "14");
            Program.Check(HistoryFolderPlan.Build(f.Store.GetRecords(), new Dictionary<string, string[]> { ["14"] = new[] { f.Source } }).Count == 0, "singleton directories became automatic groups");
            return Task.CompletedTask;
        });
        await Test("same folder names never merge sources or source roots", f => {
            HistoryRecord R(string library, string root, int id) => new() { LibraryId = library, Key = "key" + id, Mode = IdentityMode.Provider, RemovedUtc = DateTime.UtcNow,
                Snapshot = new MediaSnapshot { LibraryId = library, ItemId = id.ToString(), Path = Path.Combine(root, "same", id + ".mkv") } };
            var root2 = Path.Combine(f.Root, "source2"); Directory.CreateDirectory(root2);
            var records = new[] { R("14", f.Source, 1), R("14", f.Source, 2), R("15", f.Source, 3), R("15", f.Source, 4), R("14", root2, 5), R("14", root2, 6) };
            var plans = HistoryFolderPlan.Build(records, new Dictionary<string, string[]> { ["14"] = new[] { f.Source, root2 }, ["15"] = new[] { f.Source } });
            Program.Check(plans.Count == 3 && plans.Select(p => p.Key).Distinct().Count() == 3 && plans.All(p => p.RecordKeys.Count == 2), "same-name folders crossed library/root boundaries");
            Program.Check(HistoryFolderPlan.Build(records.Reverse(), new Dictionary<string, string[]> { ["14"] = new[] { root2, f.Source }, ["15"] = new[] { f.Source } }).Select(p => p.Key).SequenceEqual(plans.Select(p => p.Key)), "plan keys depend on enumeration order");
            return Task.CompletedTask;
        });
        await Test("nested groups do not duplicate every card into their common ancestor", f => {
            foreach (var p in new[] { "category/first/a.mkv", "category/first/b.mkv", "category/second/c.mkv", "category/second/d.mkv" })
                Movie(f, 41 + f.Live.Count, p);
            foreach (var record in f.Store.GetRecords()) f.Store.MarkRemoved(record.Snapshot.ItemId, "14");
            var plans = HistoryFolderPlan.Build(f.Store.GetRecords(), new Dictionary<string, string[]> { ["14"] = new[] { f.Source } });
            Program.Check(plans.Count == 2 && plans.All(p => p.RecordKeys.Count == 2) && plans.SelectMany(p => p.RecordKeys).Distinct().Count() == 4, "nested plan duplicated films into ancestor BoxSets"); return Task.CompletedTask;
        });
        await Test("195-child Folder event persists history and original-folder plan in same worker", async f => {
            for (var i = 1; i <= 195; i++)
                f.Store.Observe(new MediaSnapshot { ItemId = (2000 + i).ToString(), LibraryId = "14", Name = "Film " + i, Path = Path.Combine(f.Source, "batch", i + ".mkv") }, IdentityMode.Provider);
            f.Store.Flush(); RemoveFolder(f, Path.Combine(f.Source, "batch")); await f.Process();
            var records = new HistoryStore(f.Data).GetRecords(); var plans = HistoryFolderPlan.Load(f.Data);
            Program.Check(records.Count == 195 && records.All(r => r.RemovedUtc.HasValue && r.ActiveItemIds.Count == 0), "195-child container did not persist each history identity");
            Program.Check(plans.Count == 1 && plans[0].RecordKeys.Count == 195 && ArchiveWriter.SyncCalls.Count == 1, "folder plan and archive publication were not in immediate batch");
        });
        await Test("source root event is not evidence for deleting the whole library", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); f.Live.Clear(); RemoveFolder(f, f.Source); await f.Process();
            Program.Check(!f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Queries.Count == 0 && f.Proofs.Count == 0, "source root became ordinary folder proof");
        });
        await Test("symlink escaping real source root cannot become folder evidence", async f => {
            var a = Movie(f, 41, "collection/a.mkv"); var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(f.Source, "escape"), outside); f.Live.Clear(); RemoveFolder(f, Path.Combine(f.Source, "escape", "collection")); await f.Process();
            Program.Check(!f.Record(a.InternalId, true).RemovedUtc.HasValue && f.Proofs.Count == 0 && f.Queries.Count == 0, "source symlink escaped canonical boundary");
        });
        await Test("folder-context symlink is rejected without external writes", f => {
            var target = Path.Combine(f.Root, "outside.json"); File.WriteAllText(target, "do not overwrite"); File.CreateSymbolicLink(Path.Combine(f.Data, "folder-context.json"), target);
            var rejected = false; try { HistoryFolderPlan.Save(f.Data, Array.Empty<HistoryFolderGroup>()); } catch (IOException) { rejected = true; }
            Program.Check(rejected && File.ReadAllText(target) == "do not overwrite", "context save followed symlink"); return Task.CompletedTask;
        });
        Console.WriteLine($"FOLDER RESULT {passed}/{passed + failed} passed; {failed} failed");
        return failed;
    }
}
