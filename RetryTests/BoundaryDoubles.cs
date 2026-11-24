using EmbyHistory.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyHistory;

// Only unrelated integration boundaries are doubled. EntryPoint, ledger, snapshot extraction,
// source-path validation, event application, planner, and status are linked production source.
public sealed class Plugin
{
    public static Plugin? Instance { get; set; }
    public Configuration Configuration { get; set; } = new();
    public string HistoryDataPath { get; set; } = "";
}

internal sealed class ArchiveWriter
{
    internal static readonly List<HashSet<string>> SyncCalls = new();
    internal static int Failures;
    public ArchiveWriter(string root) { EnsureSafeDirectory(root); }
    public IReadOnlyList<string> LastChangedLibraryIds => Array.Empty<string>();
    public IReadOnlyList<string> LastChangedPaths => Array.Empty<string>();
    public void EnsureLibrary(string id) { }
    public int Sync(IReadOnlyList<HistoryRecord> records, ISet<string> sources, Action<string, Exception> error)
    {
        SyncCalls.Add(new HashSet<string>(sources, StringComparer.Ordinal));
        return Failures;
    }
    public static void EnsureSafeDirectory(string path) => Directory.CreateDirectory(path);
    public static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget != null) throw new IOException("Test boundary rejects links");
    }
}

internal sealed class HistoryIndexService
{
    public HistoryIndexService(ILibraryManager library, IProviderManager provider, IFileSystem fs, ILogger log) { }
    public void Queue(IEnumerable<string> ids, IEnumerable<string>? paths = null) { }
    public Task RefreshAsync(Plugin plugin, ISet<string> selected, CancellationToken token) => Task.CompletedTask;
}

internal sealed class HistoryPlaybackService : IDisposable
{
    public sealed class DummyStore { public void Flush() { } }
    public DummyStore Store { get; } = new();
    public HistoryPlaybackService(ILibraryManager l,IUserManager u,IUserDataManager d,string p,Action<string,Exception> e) { }
    public void Start(Func<IEnumerable<HistoryRecord>> r) { }
    public (int Captured,int Failed) CaptureRecords(IEnumerable<HistoryRecord> r)=>(0,0);
    public (int Restored,int Failed,int Pending) RestoreRecords(IEnumerable<HistoryRecord> r,CancellationToken t)=>(0,0,0);
    public void Capture(MediaBrowser.Controller.Entities.BaseItem i,string l,string k) { }
    public void Dispose() { }
}
internal sealed class HistoryGroupService
{
    public HistoryGroupService(ILibraryManager l,IUserManager u,MediaBrowser.Controller.Collections.ICollectionManager c,IProviderManager p,ILogger log) { }
    public int GroupCount=>0; public string LastError=>"";
    public Task SyncAsync(Plugin p,IReadOnlyList<HistoryRecord> r,ISet<string>s,CancellationToken t)=>Task.CompletedTask;
}

internal sealed class HistoryAccessSync
{
    public HistoryAccessSync(ILibraryManager library, IUserManager users, ILogger log) { }
    public void Run(SourceRule[] rules) { }
}
