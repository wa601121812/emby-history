using EmbyHistory.Core;

var tests = new (string Name, Action Body)[]
{
    ("normalizer supports numeric prefixes, FC2 and disc variants", () =>
    {
        Equal("ABC-123", CodeNormalizer.Extract("[中字]abc-123-CD2.1080p.mkv"));
        Equal("T28-123", CodeNormalizer.Extract("T28-123 中文字幕"));
        Equal("FC2-PPV-1234567", CodeNormalizer.Extract("FC2-PPV-1234567-CD1.mp4"));
        Equal("ABC-001", CodeNormalizer.Extract("ABC-001-CD2"));
        Equal("", CodeNormalizer.Extract("x264-1080p h265-2160p HEVC-1080 BluRay"));
        Equal("", CodeNormalizer.Extract("x264-1080"));
    }),
    ("unknown remove is a no-op and collections default initialized", () => WithStore(s =>
    {
        s.MarkRemoved("missing"); Equal(0, s.GetRecords().Count);
        var m = new MediaSnapshot(); Equal("", m.Name); Equal(0, m.Genres.Count);
        Equal(0, m.Actors.Count); Equal(0, m.Tags.Count); Equal(0, m.ProviderIds.Count);
    })),
    ("duplicates remove only after last copy and retain active metadata", () => WithStore(s =>
    {
        var a = Media("a"); a.Overview = "first copy"; s.Observe(a);
        var b = Media("b"); b.Overview = "second copy"; s.Observe(b);
        Equal(1, s.GetRecords().Count); Equal(2, Only(s).ActiveItemIds.Count);
        s.MarkRemoved("b"); Equal(null, Only(s).RemovedUtc);
        Equal("first copy", Only(s).Snapshot.Overview);
        s.MarkRemoved("a"); True(Only(s).RemovedUtc.HasValue);
        Equal("first copy", Only(s).Snapshot.Overview); Equal(0, Only(s).ActiveItemIds.Count);
    })),
    ("return to library preserves first seen", () => WithStore(s =>
    {
        s.Observe(Media("a")); var first = Only(s).FirstSeenUtc; var key = Only(s).Key;
        s.MarkRemoved("a"); s.Observe(Media("new-id"));
        Equal(first, Only(s).FirstSeenUtc); Equal(key, Only(s).Key);
        Equal(null, Only(s).RemovedUtc); True(Only(s).ActiveItemIds.SetEquals(new[] { "new-id" }));
    })),
    ("libraries have isolated identities and removal", () => WithStore(s =>
    {
        s.Observe(Media("a", library: "one")); s.Observe(Media("b", library: "two"));
        Equal(2, s.GetRecords().Count); True(s.GetRecords()[0].Key != s.GetRecords()[1].Key);
        s.Reconcile("one", Array.Empty<MediaSnapshot>(), true);
        True(s.GetRecords().Single(r => r.LibraryId == "one").RemovedUtc.HasValue);
        Equal(null, s.GetRecords().Single(r => r.LibraryId == "two").RemovedUtc);
    })),
    ("partial and failed enumerations do not remove anything", () => WithStore(s =>
    {
        s.Observe(Media("a")); s.Observe(Media("b", "XYZ-002"));
        s.Reconcile("library", new[] { Media("a") }, false);
        True(s.GetRecords().All(r => r.RemovedUtc is null));
        Throws<InvalidOperationException>(() => s.Reconcile("library", BrokenScan(), true));
        True(s.GetRecords().All(r => r.RemovedUtc is null));
        s.Reconcile("library", Array.Empty<MediaSnapshot>(), false);
        True(s.GetRecords().All(r => r.RemovedUtc is null));
    })),
    ("complete scans persist across restart only on flush", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Observe(Media("b", "XYZ-002"));
        Equal(0, new HistoryStore(dir).GetRecords().Count);
        s.Flush(); s.Reconcile("library", new[] { Media("a") }, true); s.Flush();
        var loaded = new HistoryStore(dir); Equal(2, loaded.GetRecords().Count);
        True(loaded.GetRecords().Single(r => r.Code == "XYZ-002").RemovedUtc.HasValue);
        Equal(null, loaded.GetRecords().Single(r => r.Code == "ABC-001").RemovedUtc);
        loaded.Reconcile("library", Array.Empty<MediaSnapshot>(), true); loaded.Flush();
        True(new HistoryStore(dir).GetRecords().All(r => r.RemovedUtc.HasValue));
    })),
    ("bad primary recovers valid previous backup and repairs primary", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        s.Observe(Media("b", "XYZ-002")); s.Flush();
        File.WriteAllText(Path.Combine(dir, "state.json"), "{broken");
        var recovered = new HistoryStore(dir); Equal(1, recovered.GetRecords().Count);
        True(!string.IsNullOrWhiteSpace(recovered.RecoveryWarning));
        Equal(1, new HistoryStore(dir).GetRecords().Count);
    })),
    ("first flush also has a valid recoverable backup", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        File.WriteAllText(Path.Combine(dir, "state.json"), "null");
        Equal(1, new HistoryStore(dir).GetRecords().Count);
    })),
    ("both corrupt or semantically invalid ledgers throw", () => WithDirectory(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "backup.json"), "not json");
        Throws<InvalidDataException>(() => new HistoryStore(dir));
    })),
    ("all returned and input metadata is deeply detached", () => WithStore(s =>
    {
        var input = Media("a"); input.Genres.Add("genre"); input.Tags.Add("tag");
        input.Actors.Add(new ActorSnapshot { Name = "actor", Role = "role" }); input.ProviderIds["imdb"] = "tt1";
        s.Observe(input); input.Name = "mutated input"; input.Actors[0].Name = "bad input";
        var copy = Only(s); copy.Snapshot.Actors[0].Name = "bad result";
        copy.Snapshot.Genres.Clear(); copy.Snapshot.Tags.Clear(); copy.Snapshot.ProviderIds.Clear();
        copy.ActiveItemIds.Clear(); copy.Code = "changed"; copy.FirstSeenUtc = DateTime.MinValue;
        var actual = Only(s); Equal("ABC-001", actual.Code); Equal("actor", actual.Snapshot.Actors[0].Name);
        Equal(1, actual.Snapshot.Genres.Count); Equal(1, actual.Snapshot.Tags.Count);
        Equal(1, actual.Snapshot.ProviderIds.Count); Equal(1, actual.ActiveItemIds.Count);
        True(actual.FirstSeenUtc != DateTime.MinValue); Equal("ABC-001 title", actual.Snapshot.Name);
    })),
    ("identity changes detach old association and preserve history", () => WithStore(s =>
    {
        s.Observe(Media("a")); var first = Only(s).FirstSeenUtc;
        s.Observe(Media("a", "XYZ-002")); Equal(2, s.GetRecords().Count);
        var old = s.GetRecords().Single(r => r.Code == "ABC-001");
        Equal(first, old.FirstSeenUtc); True(old.RemovedUtc.HasValue); Equal(0, old.ActiveItemIds.Count);
        s.MarkRemoved("a"); True(s.GetRecords().All(r => r.RemovedUtc.HasValue));
    })),
    ("metadata and first seen survive repeat observe and restart", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); var m = Media("a"); m.OriginalTitle = "original"; m.Path = "/Raw/ABC-001-CD1.mkv";
        s.Observe(m); var first = Only(s).FirstSeenUtc;
        m.Overview = "updated"; s.Observe(m); Equal(first, Only(s).FirstSeenUtc); s.Flush();
        var r = Only(new HistoryStore(dir)); Equal(first, r.FirstSeenUtc); Equal("updated", r.Snapshot.Overview);
        Equal("/Raw/ABC-001-CD1.mkv", r.Snapshot.Path); Equal("ABC-001 title", r.Snapshot.Name);
    })),
    ("code source precedence and safe provider or full-path fallbacks", () => WithStore(s =>
    {
        var explicitCode = Media("a", "T28-123"); explicitCode.Name = "ABC-001"; s.Observe(explicitCode);
        var original = Media("b", ""); original.Name = "unknown"; original.OriginalTitle = "ABC-123"; s.Observe(original);
        var filename = Media("c", ""); filename.Name = "unknown"; filename.Path = "/films/FC2-PPV-1234567-CD2.mkv"; s.Observe(filename);
        True(s.GetRecords().Any(r => r.Code == "T28-123")); True(s.GetRecords().Any(r => r.Code == "ABC-123"));
        True(s.GetRecords().Any(r => r.Code == "FC2-PPV-1234567"));
        var p1 = Media("p1", ""); p1.Name = "x264-1080p"; p1.ProviderIds["imdb"] = "tt123";
        var p2 = Media("p2", ""); p2.Name = "other edition"; p2.ProviderIds["imdb"] = "tt123";
        s.Observe(p1); s.Observe(p2); Equal(4, s.GetRecords().Count);
        var f1 = Media("f1", ""); f1.Name = "movie"; f1.Path = "/one/movie.mkv";
        var f2 = Media("f2", ""); f2.Name = "movie"; f2.Path = "/two/movie.mkv";
        s.Observe(f1); s.Observe(f2); Equal(6, s.GetRecords().Count);
    })),
    ("restart retains duplicate metadata and item index", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); var a = Media("a"); a.Overview = "A";
        var b = Media("b"); b.Overview = "B"; s.Observe(a); s.Observe(b); s.Flush();
        var loaded = new HistoryStore(dir); loaded.MarkRemoved("b"); Equal("A", Only(loaded).Snapshot.Overview);
        loaded.MarkRemoved("a"); True(Only(loaded).RemovedUtc.HasValue);
    })),
    ("reconcile validation is transactional", () => WithStore(s =>
    {
        s.Observe(Media("a"));
        Throws<ArgumentException>(() => s.Reconcile("library", new[] { Media("b"), Media("c", library: "other") }, true));
        Equal(1, Only(s).ActiveItemIds.Count); True(Only(s).ActiveItemIds.Contains("a"));
    })),
    ("known-code keys are stable SHA256 and source precedence is exact", () => WithStore(s =>
    {
        var m = Media("a", ""); m.Name = "T28-123 title"; m.OriginalTitle = "ABC-123";
        m.Path = "/ABC-001/FC2-PPV-1234567.mkv"; s.Observe(m);
        Equal("FC2-PPV-1234567", Only(s).Code);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("library\nFC2-PPV-1234567"))).ToLowerInvariant();
        Equal(expected, Only(s).Key);
        var noPath = Media("b", ""); noPath.Name = "T28-123 title"; noPath.OriginalTitle = "ABC-123";
        s.Observe(noPath); True(s.GetRecords().Any(r => r.Code == "T28-123"));
    })),
    ("flush errors propagate and do not mutate in-memory state", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        s.Observe(Media("b", "XYZ-002"));
        File.Delete(Path.Combine(dir, "state.json")); Directory.CreateDirectory(Path.Combine(dir, "state.json"));
        Throws<IOException>(s.Flush); Equal(2, s.GetRecords().Count);
    })),
    ("missing primary with valid backup recovers rather than empties", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        File.Delete(Path.Combine(dir, "state.json"));
        var loaded = new HistoryStore(dir); Equal(1, loaded.GetRecords().Count);
        True(!string.IsNullOrWhiteSpace(loaded.RecoveryWarning));
    })),
    ("moving item across libraries coexists until its own library reconciles", () => WithStore(s =>
    {
        s.Observe(Media("a", library: "source")); s.Observe(Media("a", library: "destination"));
        Equal(2, s.GetRecords().Count);
        True(s.GetRecords().All(r => r.RemovedUtc is null));
        Equal(1, s.GetRecords().Single(r => r.LibraryId == "destination").ActiveItemIds.Count);
        s.Reconcile("source", Array.Empty<MediaSnapshot>(), true);
        True(s.GetRecords().Single(r => r.LibraryId == "source").RemovedUtc.HasValue);
        Equal(null, s.GetRecords().Single(r => r.LibraryId == "destination").RemovedUtc);
    })),
    ("same item id in two libraries coexists and explicit removal is scoped", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        s.Observe(Media("a", library: "one")); s.Observe(Media("a", library: "two"));
        Equal(2, s.GetRecords().Count); True(s.GetRecords().All(r => r.RemovedUtc is null));
        s.MarkRemoved("a", "one");
        True(s.GetRecords().Single(r => r.LibraryId == "one").RemovedUtc.HasValue);
        Equal(null, s.GetRecords().Single(r => r.LibraryId == "two").RemovedUtc);
        s.Flush();
        var loaded = new HistoryStore(dir); Equal(2, loaded.GetRecords().Count);
        True(loaded.GetRecords().Single(r => r.LibraryId == "two").ActiveItemIds.Contains("a"));
        loaded.MarkRemoved("a"); True(loaded.GetRecords().All(r => r.RemovedUtc.HasValue));
    })),
    ("missing poster keeps the last valid reference across updates and re-entry", () => WithDirectory(dir =>
    {
        var poster = Path.Combine(dir, "poster.jpg"); File.WriteAllBytes(poster, new byte[] { 1 });
        var s = new HistoryStore(dir);
        var seeded = Media("a"); seeded.PosterPath = poster; s.Observe(seeded);
        var blank = Media("a"); blank.Overview = "refreshed"; blank.PosterPath = ""; s.Observe(blank);
        Equal(poster, Only(s).Snapshot.PosterPath); Equal("refreshed", Only(s).Snapshot.Overview);
        var stale = Media("a"); stale.PosterPath = Path.Combine(dir, "gone.jpg"); s.Observe(stale);
        Equal(poster, Only(s).Snapshot.PosterPath);
        s.MarkRemoved("a");
        var reentry = Media("new-id"); reentry.PosterPath = ""; s.Observe(reentry);
        Equal(poster, Only(s).Snapshot.PosterPath); Equal(null, Only(s).RemovedUtc);
        s.Flush(); Equal(poster, Only(new HistoryStore(dir)).Snapshot.PosterPath);
    })),
    ("unscraped refresh and new-id reentry preserve detached rich metadata", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        var rich = RichMedia("a"); s.Observe(rich); var first = Only(s).FirstSeenUtc;
        var blank = RawMedia("a"); s.Observe(blank);
        AssertRich(Only(s).Snapshot); Equal("ABC-123-A", blank.Name); Equal(0, blank.Actors.Count);
        Equal("Rich title", rich.Name); Equal("actor", rich.Actors[0].Name);
        s.MarkRemoved("a"); s.Flush();
        s = new HistoryStore(dir);
        var returned = RawMedia("new-id"); returned.Name = "ABC-123-folder";
        returned.Path = "/missing/ABC-123-folder/part.mkv";
        s.Reconcile("library", new[] { returned }, true);
        AssertRich(Only(s).Snapshot); Equal(first, Only(s).FirstSeenUtc);
        Equal("new-id", Only(s).Snapshot.ItemId); Equal(returned.Path, Only(s).Snapshot.Path);
        s.MarkRemoved("new-id"); s.Flush(); AssertRich(Only(new HistoryStore(dir)).Snapshot);
    })),
    ("removing the richest latest copy retains known metadata without changing older copy", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        var raw = RawMedia("a"); s.Observe(raw);
        var rich = RichMedia("b"); s.Observe(rich); s.Flush();
        s = new HistoryStore(dir); s.MarkRemoved("b");
        AssertRich(Only(s).Snapshot); Equal("a", Only(s).Snapshot.ItemId);
        s.Flush();
        using (var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "state.json"))))
        {
            var original = state.RootElement.GetProperty("Entries")[0].GetProperty("Items").GetProperty("a").GetProperty("Snapshot");
            Equal(0, original.GetProperty("Actors").GetArrayLength());
        }
        var returned = Only(s).Snapshot; returned.Actors[0].Name = "mutated";
        returned.Genres.Clear(); returned.Tags.Clear(); returned.ProviderIds.Clear();
        AssertRich(Only(s).Snapshot); Equal(0, raw.Actors.Count); Equal("actor", rich.Actors[0].Name);
        s.MarkRemoved("a"); s.Flush(); AssertRich(Only(new HistoryStore(dir)).Snapshot);
    })),
    ("identity enrichment preserves metadata when migrating or merging records", () =>
    {
        foreach (var merge in new[] { false, true }) WithDirectory(dir =>
        {
            var s = new HistoryStore(dir); var rich = RichMedia("a");
            rich.Code = ""; rich.Path = "/missing/movie.mkv"; rich.ProviderIds.Clear();
            s.Observe(rich); Equal("", Only(s).Code); var first = Only(s).FirstSeenUtc;
            if (merge) s.Observe(RawMedia("b"));
            var completed = RawMedia("a"); completed.Path = rich.Path; completed.Name = "Completed title";
            s.Observe(completed); Equal(1, s.GetRecords().Count);
            var record = Only(s); Equal(first, record.FirstSeenUtc);
            Equal("ABC-123", record.Code); Equal("Completed title", record.Snapshot.Name);
            Equal("overview", record.Snapshot.Overview);
            Equal("actor", record.Snapshot.Actors.Single().Name); Equal(2020, record.Snapshot.Year);
            s.Flush(); s = new HistoryStore(dir); s.MarkRemoved("a");
            Equal("overview", Only(s).Snapshot.Overview); Equal("actor", Only(s).Snapshot.Actors.Single().Name);
        });
    }),
    ("fresh metadata wins while code changes and libraries never inherit it", () => WithStore(s =>
    {
        var rich = RichMedia("a"); s.Observe(rich);
        var fresh = RichMedia("a"); fresh.Name = "New scraped title"; fresh.OriginalTitle = "new original";
        fresh.Overview = "new overview"; fresh.Year = 2025; fresh.Genres = new() { "new genre" };
        fresh.Tags = new() { "new tag" }; fresh.Actors = new() { new() { Name = "new actor", Role = "new role" } };
        fresh.ProviderIds = new() { ["tmdb"] = "42" }; s.Observe(fresh);
        var updated = Only(s).Snapshot; Equal(fresh.Name, updated.Name); Equal(fresh.OriginalTitle, updated.OriginalTitle);
        Equal(fresh.Overview, updated.Overview); Equal(fresh.Year, updated.Year);
        Equal("new genre", updated.Genres.Single()); Equal("new tag", updated.Tags.Single());
        Equal("new actor", updated.Actors.Single().Name); Equal("new role", updated.Actors.Single().Role);
        Equal("42", updated.ProviderIds["tmdb"]); Equal(1, updated.ProviderIds.Count);
        var blank = RawMedia("a"); blank.Name = " "; s.Observe(blank);
        Equal("New scraped title", Only(s).Snapshot.Name);
        var changed = RawMedia("a"); changed.Code = "XYZ-002"; changed.Name = "XYZ-002";
        changed.Path = rich.Path; s.Observe(changed);
        var otherWork = s.GetRecords().Single(r => r.Code == "XYZ-002").Snapshot;
        Equal("XYZ-002", otherWork.Name); Equal("", otherWork.Overview); Equal(0, otherWork.Actors.Count);
        var otherLibrary = RawMedia("a"); otherLibrary.LibraryId = "other"; s.Observe(otherLibrary);
        var isolated = s.GetRecords().Single(r => r.LibraryId == "other").Snapshot;
        Equal("ABC-123-A", isolated.Name); Equal("", isolated.Overview); Equal(0, isolated.ProviderIds.Count);
        var oldRaw = RawMedia("raw"); oldRaw.Code = "RAW-003"; oldRaw.Name = "old"; oldRaw.Path = "/missing/old.mkv";
        s.Observe(oldRaw);
        var newRaw = RawMedia("raw"); newRaw.Code = "RAW-003"; newRaw.Name = "new"; newRaw.Path = "/missing/other.mkv";
        s.Observe(newRaw); Equal("new", s.GetRecords().Single(r => r.Code == "RAW-003").Snapshot.Name);
    })),
    ("metadata completion migrates the codeless record without a ghost", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        var raw = new MediaSnapshot { ItemId = "a", LibraryId = "library", Name = "x264-1080p", Path = "/films/movie.mkv" };
        raw.ProviderIds["imdb"] = "tt123"; s.Observe(raw);
        Equal("", Only(s).Code); var oldKey = Only(s).Key; var first = Only(s).FirstSeenUtc;
        var completed = new MediaSnapshot { ItemId = "a", LibraryId = "library", Code = "ABC-001", Name = "ABC-001 title", Path = "/films/movie.mkv", Overview = "scraped" };
        s.Observe(completed);
        Equal(1, s.GetRecords().Count); var record = Only(s);
        Equal("ABC-001", record.Code); True(record.Key != oldKey); Equal(first, record.FirstSeenUtc);
        Equal(null, record.RemovedUtc); True(record.ActiveItemIds.SetEquals(new[] { "a" }));
        Equal("scraped", record.Snapshot.Overview); Equal("/films/movie.mkv", record.Snapshot.Path);
        s.Flush(); var loaded = Only(new HistoryStore(dir));
        Equal("ABC-001", loaded.Code); Equal(first, loaded.FirstSeenUtc); Equal(null, loaded.RemovedUtc);
    })),
    ("provider completion migrates the path-identified record", () => WithStore(s =>
    {
        var raw = new MediaSnapshot { ItemId = "a", LibraryId = "library", Name = "unknown film", Path = "/films/raw.mkv" };
        s.Observe(raw); var oldKey = Only(s).Key; var first = Only(s).FirstSeenUtc;
        var tagged = new MediaSnapshot { ItemId = "a", LibraryId = "library", Name = "unknown film", Path = "/films/raw.mkv" };
        tagged.ProviderIds["tmdb"] = "42"; s.Observe(tagged);
        Equal(1, s.GetRecords().Count); var record = Only(s);
        True(record.Key != oldKey); Equal("", record.Code); Equal(first, record.FirstSeenUtc);
        Equal(null, record.RemovedUtc); True(record.ActiveItemIds.SetEquals(new[] { "a" }));
    })),
    ("completion merges into an existing key with earliest first seen and all copies", () => WithStore(s =>
    {
        var raw = new MediaSnapshot { ItemId = "a", LibraryId = "library", Name = "raw", Path = "/films/a.mkv" };
        s.Observe(raw); var first = Only(s).FirstSeenUtc;
        var other = Media("b"); other.Path = "/films/b.mkv"; s.Observe(other);
        var latest = s.GetRecords().Single(r => r.Code == "ABC-001").LastSeenUtc;
        var completed = Media("a"); completed.Path = "/films/a.mkv"; completed.Overview = "scraped"; s.Observe(completed);
        Equal(1, s.GetRecords().Count); var record = Only(s);
        Equal(first, record.FirstSeenUtc); True(record.LastSeenUtc >= latest);
        True(record.ActiveItemIds.SetEquals(new[] { "a", "b" })); Equal(null, record.RemovedUtc);
        Equal("scraped", record.Snapshot.Overview);
    })),
    ("a genuine code change with an unchanged path keeps the old history", () => WithStore(s =>
    {
        var a = Media("a"); a.Path = "/films/x.mkv"; s.Observe(a); var first = Only(s).FirstSeenUtc;
        var b = Media("a", "XYZ-002"); b.Path = "/films/x.mkv"; s.Observe(b);
        Equal(2, s.GetRecords().Count);
        var old = s.GetRecords().Single(r => r.Code == "ABC-001");
        Equal(first, old.FirstSeenUtc); True(old.RemovedUtc.HasValue); Equal(0, old.ActiveItemIds.Count);
    })),
    ("completion keeps the other active copies of the codeless record", () => WithStore(s =>
    {
        var one = new MediaSnapshot { ItemId = "a", LibraryId = "library", Name = "raw", Path = "/films/box.mkv" };
        var two = new MediaSnapshot { ItemId = "b", LibraryId = "library", Name = "raw", Path = "/films/box.mkv" };
        s.Observe(one); s.Observe(two);
        Equal(1, s.GetRecords().Count); Equal(2, Only(s).ActiveItemIds.Count); var first = Only(s).FirstSeenUtc;
        var completed = new MediaSnapshot { ItemId = "a", LibraryId = "library", Code = "ABC-001", Name = "ABC-001 title", Path = "/films/box.mkv" };
        s.Observe(completed);
        Equal(1, s.GetRecords().Count); var record = Only(s);
        Equal(first, record.FirstSeenUtc); Equal(2, record.ActiveItemIds.Count); Equal(null, record.RemovedUtc);
        s.MarkRemoved("b"); Equal(null, Only(s).RemovedUtc); Equal(1, Only(s).ActiveItemIds.Count);
        s.MarkRemoved("a"); True(Only(s).RemovedUtc.HasValue);
    })),
    ("persisted cross-library duplicate is valid while same-library duplicate is rejected", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        s.Observe(Media("a")); s.Observe(Media("b", "XYZ-002")); s.Observe(Media("a", library: "other"));
        s.Flush();
        Equal(3, new HistoryStore(dir).GetRecords().Count);
        var path = Path.Combine(dir, "state.json");
        var tampered = File.ReadAllText(path).Replace("\"b\"", "\"a\"", StringComparison.Ordinal);
        File.WriteAllText(path, tampered); File.WriteAllText(Path.Combine(dir, "backup.json"), tampered);
        Throws<InvalidDataException>(() => new HistoryStore(dir));
    })),
    ("unchanged flush does not rewrite while the first empty flush persists", () => WithDirectory(dir =>
    {
        var path = Path.Combine(dir, "state.json");
        new HistoryStore(dir).Flush(); True(File.Exists(path));
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(path, stamp);
        s.Flush(); Equal(stamp, File.GetLastWriteTimeUtc(path));
        s.Observe(Media("b", "XYZ-002")); s.Flush(); True(File.GetLastWriteTimeUtc(path) > stamp);
        Equal(2, new HistoryStore(dir).GetRecords().Count);
    })),
    ("backup recovery does not rewrite the ledger on an unchanged flush", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir); s.Observe(Media("a")); s.Flush();
        var path = Path.Combine(dir, "state.json"); File.WriteAllText(path, "{broken");
        var recovered = new HistoryStore(dir);
        True(!string.IsNullOrWhiteSpace(recovered.RecoveryWarning));
        var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(path, stamp);
        recovered.Flush(); Equal(stamp, File.GetLastWriteTimeUtc(path)); Equal(1, recovered.GetRecords().Count);
    })),
    ("concurrent observes and reads are serialized", () => WithStore(s =>
    {
        Parallel.For(0, 100, i => { s.Observe(Media("id-" + i)); _ = s.GetRecords(); });
        Equal(100, Only(s).ActiveItemIds.Count);
        Parallel.For(0, 100, i => s.MarkRemoved("id-" + i)); True(Only(s).RemovedUtc.HasValue);
    })),
    ("temporarily thin metadata does not split a known code record", () => WithStore(s =>
    {
        var rich = Media("a"); rich.Path = "/m/x.mkv"; rich.Name = "ABC-001 title";
        s.Observe(rich);
        var thin = new MediaSnapshot { ItemId = "a", LibraryId = "library", Path = "/m/x.mkv", Name = "x" };
        s.Observe(thin);
        Equal(1, s.GetRecords().Count);
        var record = Only(s);
        Equal("ABC-001", record.Code);
        True(!record.RemovedUtc.HasValue && record.ActiveItemIds.SetEquals(new[] { "a" }));
    })),
    ("identity scheme upgrade re-keys instead of reporting a removal", () => WithDirectory(dir =>
    {
        var s = new HistoryStore(dir);
        var m = Media("a", "N1975"); m.Path = "/lib/092015-975-carib-1080p.mp4";
        s.Observe(m); s.Flush();
        var firstSeen = s.GetRecords().Single().FirstSeenUtc;
        // Simulate a ledger written by an older identification scheme.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "state.json")))!;
        json["Scheme"] = 1;
        File.WriteAllText(Path.Combine(dir, "state.json"), json.ToJsonString());
        File.Delete(Path.Combine(dir, "backup.json"));
        var upgraded = new HistoryStore(dir);
        var renamed = Media("a", "092015-975"); renamed.Path = "/lib/092015-975-carib-1080p.mp4";
        upgraded.Observe(renamed);
        Equal(1, upgraded.GetRecords().Count);
        var record = upgraded.GetRecords().Single();
        Equal("092015-975", record.Code);
        True(!record.RemovedUtc.HasValue && record.ActiveItemIds.SetEquals(new[] { "a" }));
        Equal(firstSeen, record.FirstSeenUtc);
    })),
    ("a different code at the same path still keeps the old history", () => WithStore(s =>
    {
        var first = Media("a"); first.Path = "/m/x.mkv"; s.Observe(first);
        var second = Media("a", "XYZ-999"); second.Path = "/m/x.mkv"; s.Observe(second);
        Equal(2, s.GetRecords().Count);
        True(s.GetRecords().Any(r => r.Code == "ABC-001" && r.RemovedUtc.HasValue));
        True(s.GetRecords().Any(r => r.Code == "XYZ-999" && !r.RemovedUtc.HasValue));
    })),
    ("provider mode keys on the scraper id and never invents a code", () =>
    {
        var movie = new MediaSnapshot { ItemId = "m1", LibraryId = "5", Name = "4K-115 freeform title",
            Path = "/m/Sampletitle.Part1.4K-115.mp4", ProviderIds = new() { ["Tmdb"] = "10775", ["Imdb"] = "tt0338564" } };
        var result = SnapshotTools.Identify(movie, IdentityMode.Provider);
        Equal("", result.Code);
        Equal("provider:IMDB:TT0338564", result.Identity);
        // Code mode would have read "4K-115" out of the same file as if it were a release code.
        Equal("4K-115", SnapshotTools.Identify(movie, IdentityMode.Code).Code);
    }),
    ("local path mode keeps unrelated free-form files apart", () =>
    {
        var first = new MediaSnapshot { ItemId = "a", LibraryId = "1002", Name = "sampletitle ep2", Path = "/m/OLF_sampletitle_EP2 - UPLOAD18PLUS-115.mp4" };
        var second = new MediaSnapshot { ItemId = "b", LibraryId = "1002", Name = "sampletitle ep3", Path = "/m/OLF_sampletitle_EP3 - UPLOAD18PLUS-115.mp4" };
        var one = SnapshotTools.Identify(first, IdentityMode.LocalPath);
        var two = SnapshotTools.Identify(second, IdentityMode.LocalPath);
        Equal("", one.Code); Equal("", two.Code);
        True(one.Identity != two.Identity);
        True(SnapshotTools.Key(first.LibraryId, one.Identity) != SnapshotTools.Key(second.LibraryId, two.Identity));
    }),
    ("legacy code identity for the JAV library is byte-identical", () =>
    {
        var jav = new MediaSnapshot { ItemId = "j1", LibraryId = "1001", Name = "TESTA-035 title", Path = "/m/TESTA-035-1080p.mp4" };
        var result = SnapshotTools.Identify(jav);
        Equal("TESTA-035", result.Code);
        Equal("TESTA-035", result.Identity);
        Equal(SnapshotTools.Key("1001", "TESTA-035"), SnapshotTools.Key(jav.LibraryId, result.Identity));
    }),
    ("access plan mirrors source viewers and never touches administrators", () =>
    {
        var users = new[]
        {
            Viewer("1", "can-see", folders: new[] { "src" }),
            Viewer("2", "cannot-see", folders: new[] { "other" }),
            Viewer("3", "already-has-history", folders: new[] { "src", "hist" }),
            new UserAccess { UserId = "4", Name = "admin", IsAdministrator = true, EnableAllFolders = true },
            new UserAccess { UserId = "5", Name = "disabled", IsDisabled = true, EnabledFolders = new[] { "src" } }
        };
        var plan = AccessPlanner.Plan(users, "src", "900", "hist");
        Equal(null, plan.Blocked);
        Equal(1, plan.Changes.Count);
        Equal("1", plan.Changes[0].UserId); Equal(AccessAction.Add, plan.Changes[0].Action);
        True(plan.Skipped.Any(x => x.StartsWith("admin"))); True(plan.Skipped.Any(x => x.StartsWith("disabled")));
    }),
    ("whole-library exclusion beats EnableAllFolders", () =>
    {
        var all = new UserAccess { UserId = "9", Name = "all", EnableAllFolders = true,
            EnabledFolders = new[] { "hist" }, ExcludedSubFolders = new[] { "src_900" } };
        True(!AccessPlanner.CanSeeSource(all, "src", "900"));
        var plan = AccessPlanner.Plan(new[] { all }, "src", "900", "hist");
        Equal(1, plan.Changes.Count); Equal(AccessAction.Remove, plan.Changes[0].Action);
        True(AccessPlanner.CanSeeSource(all, "other", "901"));
    }),
    ("applying a change preserves every other folder and the all-folders flag", () =>
    {
        var user = new UserAccess { UserId = "1", EnableAllFolders = false, EnabledFolders = new[] { "a", "hist", "b" } };
        var removed = AccessPlanner.Apply(user, new AccessChange { UserId = "1", Action = AccessAction.Remove }, "hist");
        Equal("a,b", string.Join(",", removed));
        Equal(false, user.EnableAllFolders);
        var added = AccessPlanner.Apply(new UserAccess { UserId = "1", EnabledFolders = new[] { "a" } },
            new AccessChange { UserId = "1", Action = AccessAction.Add }, "hist");
        Equal("a,hist", string.Join(",", added));
    }),
    ("an oversized access change is refused unless it is the first population", () =>
    {
        var users = Enumerable.Range(0, 12)
            .Select(i => Viewer(i.ToString(), "u" + i, folders: new[] { "src" })).ToArray();
        var plan = AccessPlanner.Plan(users, "src", "900", "hist", maxChanges: 10);
        True(plan.Blocked != null); True(!plan.Safe);
        Equal(12, plan.Changes.Count);
        var wide = AccessPlanner.Plan(users, "src", "900", "hist", maxChanges: 20);
        Equal(null, wide.Blocked);
        // Nobody holds the history library yet, so this really is the initial population.
        True(AccessPlanner.NeedsBootstrap(users, "hist"));
        var boot = AccessPlanner.Plan(users, "src", "900", "hist", maxChanges: 10,
            bootstrap: AccessPlanner.NeedsBootstrap(users, "hist"));
        Equal(null, boot.Blocked); Equal(12, boot.Changes.Count);
        // Once somebody holds it, the cap applies again to every later pass.
        var after = users.Select(u => new UserAccess { UserId = u.UserId, Name = u.Name,
            EnabledFolders = u.UserId == "0" ? new[] { "src", "hist" } : u.EnabledFolders }).ToArray();
        True(!AccessPlanner.NeedsBootstrap(after, "hist"));
        True(AccessPlanner.Plan(after, "src", "900", "hist", 10, bootstrap: AccessPlanner.NeedsBootstrap(after, "hist")).Blocked != null);
    }),
    ("episode identity follows series plus season and episode numbers", () =>
    {
        var first = new MediaSnapshot { ItemId = "e1", LibraryId = "14", Name = "第 1 集",
            Path = "/tv/Show (2020)/Season 01/Show - S01E01.mkv", SeriesId = "900", SeriesName = "Show",
            SeriesYear = 2020, SeasonNumber = 1, EpisodeNumber = 1,
            SeriesProviderIds = new() { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" } };
        var renamed = new MediaSnapshot { ItemId = "e1", LibraryId = "14", Name = "完全不同的标题",
            Path = "/tv/Show (2020)/Season 01/Show - S01E01 - renamed.mkv", SeriesId = "900", SeriesName = "Show",
            SeriesYear = 2020, SeasonNumber = 1, EpisodeNumber = 1,
            SeriesProviderIds = new() { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" } };
        var otherEpisode = new MediaSnapshot { ItemId = "e2", LibraryId = "14", Name = "第 2 集",
            Path = "/tv/Show (2020)/Season 01/Show - S01E02.mkv", SeriesId = "900", SeriesName = "Show",
            SeriesYear = 2020, SeasonNumber = 1, EpisodeNumber = 2,
            SeriesProviderIds = new() { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" } };
        var one = SnapshotTools.Identify(first, IdentityMode.SeriesEpisode);
        var two = SnapshotTools.Identify(renamed, IdentityMode.SeriesEpisode);
        var three = SnapshotTools.Identify(otherEpisode, IdentityMode.SeriesEpisode);
        Equal(one.Identity, two.Identity);
        True(one.Identity != three.Identity);
        // Series id beats the episode's own title, whichever provider wins the ordering.
        True(one.Identity.StartsWith("series-provider:IMDB:TT0903747", StringComparison.Ordinal));
        True(one.Identity.EndsWith("\ns:1\ne:1", StringComparison.Ordinal));
    }),
    ("episode identity falls back to series name+year, then to the file path", () =>
    {
        var named = new MediaSnapshot { ItemId = "e1", LibraryId = "1005", Name = "第 5 集",
            Path = "/anime/Some Anime (2023)/Season 2/Some Anime - S02E05.mkv",
            SeriesName = "Some Anime", SeriesYear = 2023, SeasonNumber = 2, EpisodeNumber = 5 };
        var a = SnapshotTools.Identify(named, IdentityMode.SeriesEpisode);
        True(a.Identity.StartsWith("series-title:", StringComparison.Ordinal));
        True(a.Identity.EndsWith("\ns:2\ne:5", StringComparison.Ordinal));

        var noNumbers = new MediaSnapshot { ItemId = "e2", LibraryId = "14", Path = "/tv/X/S01/e.mkv", SeriesName = "X" };
        var b = SnapshotTools.Identify(noNumbers, IdentityMode.SeriesEpisode);
        True(b.Identity.Contains("\npath:", StringComparison.Ordinal));

        var noSeries = new MediaSnapshot { ItemId = "e3", LibraryId = "14", Path = "/tv/orphan.mkv" };
        var c = SnapshotTools.Identify(noSeries, IdentityMode.SeriesEpisode);
        Equal("path:/tv/orphan.mkv", c.Identity);
        True(SnapshotTools.Key("14", c.Identity) != SnapshotTools.Key("14", b.Identity));
    }),
    ("episode archive layout is stable, filesystem-safe and zero padded", () =>
    {
        var snapshot = new MediaSnapshot { SeriesName = "Some: Show?/Test", SeriesYear = 2021, SeasonNumber = 3, EpisodeNumber = 7 };
        Equal("Some Show Test (2021)", TvArchivePlan.SeriesFolder(snapshot));
        Equal("Season 03", TvArchivePlan.SeasonFolder(3));
        Equal("Some Show Test - S03E07", TvArchivePlan.EpisodeBase(snapshot));

        // Names that survive a hostile filesystem: trailing dots, control chars, over-long titles.
        Equal("Name", TvArchivePlan.SafeName("Name... "));
        Equal("Unknown", TvArchivePlan.SafeName("   "));
        Equal("A B", TvArchivePlan.SafeName("A\u0001B"));
        True(TvArchivePlan.SafeName(new string('x', 200)).Length <= 90);
        // No path separator survives, so a crafted title cannot escape the archive root.
        True(!TvArchivePlan.SafeName("a/b\\c").Contains('/'));
        True(!TvArchivePlan.SafeName("a/b\\c").Contains('\\'));
    }),
    ("series, season and episode NFO carry the identity Emby needs", () =>
    {
        var snapshot = new MediaSnapshot { SeriesName = "Some Show", SeriesYear = 2021, SeasonNumber = 1, EpisodeNumber = 2,
            Name = "第 2 集", Overview = "plot\u0001with bad char" };
        var series = TvArchivePlan.SeriesNfo(snapshot, "abc123", DateTime.UtcNow.AddDays(-5), DateTime.UtcNow, 2, 12);
        True(series.Contains("<tvshow>", StringComparison.Ordinal));
        True(series.Contains("type=\"embyhistory\"", StringComparison.Ordinal));
        True(series.Contains("abc123", StringComparison.Ordinal));

        var season = TvArchivePlan.SeasonNfo(2, DateTime.UtcNow, DateTime.UtcNow, 8);
        True(season.Contains("<seasonnumber>2</seasonnumber>", StringComparison.Ordinal));

        var record = new HistoryRecord { Key = "deadbeef", LibraryId = "14", Snapshot = snapshot };
        var episode = TvArchivePlan.EpisodeNfo(record);
        True(episode.Contains("<episodedetails>", StringComparison.Ordinal));
        True(episode.Contains("<season>1</season>", StringComparison.Ordinal));
        True(episode.Contains("<episode>2</episode>", StringComparison.Ordinal));
        True(episode.Contains("[历史] 第 2 集", StringComparison.Ordinal));
        True(episode.Contains("deadbeef", StringComparison.Ordinal));
        // A scraped plot with U+0001 must not make the whole file unwritable.
        True(!episode.Contains('\u0001'));
        True(episode.Contains("plot with bad char", StringComparison.Ordinal));
    }),
    ("series key is shared by episodes but differs between series and libraries", () =>
    {
        MediaSnapshot Episode(string name, int season, int episode, string tvdb = "81189") => new()
        {
            LibraryId = "14", SeriesName = name, SeriesYear = 2021,
            SeasonNumber = season, EpisodeNumber = episode,
            SeriesProviderIds = new() { ["Tvdb"] = tvdb }
        };
        var a = SnapshotTools.SeriesKeyOf(Episode("Show", 1, 1), "14");
        var b = SnapshotTools.SeriesKeyOf(Episode("Show", 2, 9), "14");
        var other = SnapshotTools.SeriesKeyOf(Episode("Show II", 1, 1, tvdb: "99999"), "14");
        var otherLibrary = SnapshotTools.SeriesKeyOf(Episode("Show", 1, 1), "1005");
        // The external id is authoritative: a renamed series is still the same series.
        var renamed = SnapshotTools.SeriesKeyOf(Episode("Show (new title)", 1, 1), "14");
        Equal(a, b);
        True(a != other);
        True(a != otherLibrary);
        Equal(a, renamed);
        Equal(64, a.Length);
        // Without any series context there is no series key, and the caller must not invent one.
        Equal("", SnapshotTools.SeriesKeyOf(new MediaSnapshot { LibraryId = "14" }, "14"));
    }),
    ("a history library shared by general and adult sources is rejected", () =>
    {
        var rules = new[]
        {
            new RuleView { LibraryId = "5", ContentClass = "General", HistoryLibraryId = "hist" },
            new RuleView { LibraryId = "1001", ContentClass = "Adult", HistoryLibraryId = "hist" },
            new RuleView { LibraryId = "1004", ContentClass = "Adult", HistoryLibraryId = "adult-hist" }
        };
        var conflicts = AccessPlanner.CrossClassTargets(rules);
        True(conflicts.Contains("5")); True(conflicts.Contains("1001"));
        True(!conflicts.Contains("1004"));
        // Many-to-one inside one class is exactly what the settings page is for.
        var shared = new[]
        {
            new RuleView { LibraryId = "1004", ContentClass = "Adult", HistoryLibraryId = "adult-hist" },
            new RuleView { LibraryId = "1003", ContentClass = "Adult", HistoryLibraryId = "adult-hist" },
            new RuleView { LibraryId = "1002", ContentClass = "adult", HistoryLibraryId = "adult-hist" }
        };
        Equal(0, AccessPlanner.CrossClassTargets(shared).Count);
        // Blank targets must not be treated as "shared with everything".
        var unbound = new[]
        {
            new RuleView { LibraryId = "5", ContentClass = "General", HistoryLibraryId = "" },
            new RuleView { LibraryId = "1001", ContentClass = "Adult", HistoryLibraryId = "" }
        };
        Equal(0, AccessPlanner.CrossClassTargets(unbound).Count);
    }),
    ("missing guid or self-mapped history blocks the whole plan", () =>
    {
        var users = new[] { Viewer("1", "u", folders: new[] { "src" }) };
        True(AccessPlanner.Plan(users, "", "900", "hist").Blocked != null);
        True(AccessPlanner.Plan(users, "src", "900", "src").Blocked != null);
    }),
    ("changing a library's identity mode rekeys instead of faking a removal", () => WithStore(s =>
    {
        var first = new MediaSnapshot { ItemId = "same", LibraryId = "5", Name = "Some Film", Path = "/m/Some Film (2020).mkv" };
        s.Observe(first, IdentityMode.Code);
        var original = Only(s);
        var seenAt = original.FirstSeenUtc;
        var rescraped = new MediaSnapshot { ItemId = "same", LibraryId = "5", Name = "Some Film", Path = "/m/Some Film (2020).mkv",
            ProviderIds = new() { ["Tmdb"] = "42" } };
        s.Observe(rescraped, IdentityMode.Provider);
        Equal(1, s.GetRecords().Count);
        Equal(null, Only(s).RemovedUtc);
        Equal(seenAt, Only(s).FirstSeenUtc);
        Equal(IdentityMode.Provider, Only(s).Mode);
    }))
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static MediaSnapshot Media(string id, string code = "ABC-001", string library = "library") =>
    new() { ItemId = id, LibraryId = library, Code = code, Name = code + " title" };
static UserAccess Viewer(string id, string name, string[] folders, bool all = false) =>
    new() { UserId = id, Name = name, EnabledFolders = folders, EnableAllFolders = all };
// A freshly rescanned item before Emby metadata providers run: filename as Name, nothing else.
static MediaSnapshot RawMedia(string id, string library = "library") => new()
{
    ItemId = id, LibraryId = library, Code = "ABC-123", Name = "ABC-123-A",
    Path = "/missing/ABC-123-A/ABC-123-A.mp4"
};
static MediaSnapshot RichMedia(string id, string library = "library") => new()
{
    ItemId = id, LibraryId = library, Code = "ABC-123", Name = "Rich title",
    OriginalTitle = "rich original", Overview = "overview", Path = "/missing/ABC-123/ABC-123-A.mkv",
    Year = 2020, Genres = new() { "drama" }, Tags = new() { "tag" },
    Actors = new() { new ActorSnapshot { Name = "actor", Role = "role" } }, ProviderIds = new() { ["tmdb"] = "7" }
};
static void AssertRich(MediaSnapshot s)
{
    Equal("Rich title", s.Name); Equal("rich original", s.OriginalTitle);
    Equal("overview", s.Overview); Equal(2020, s.Year); Equal("drama", s.Genres.Single());
    Equal("tag", s.Tags.Single()); Equal("actor", s.Actors.Single().Name);
    Equal("role", s.Actors.Single().Role); Equal("7", s.ProviderIds["tmdb"]); Equal("ABC-123", s.Code);
}
static HistoryRecord Only(HistoryStore store) => store.GetRecords().Single();
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected <{expected}>, actual <{actual}>");
}
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected exception " + typeof(T).Name);
}
static void WithStore(Action<HistoryStore> action) => WithDirectory(dir => action(new HistoryStore(dir)));
static void WithDirectory(Action<string> action)
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestResults"));
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    try { action(dir); } finally { Directory.Delete(dir, true); }
}
static IEnumerable<MediaSnapshot> BrokenScan()
{
    yield return Media("a"); throw new InvalidOperationException("simulated enumeration failure");
}
