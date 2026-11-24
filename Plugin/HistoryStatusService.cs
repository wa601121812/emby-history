using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyHistory;

/// <summary>Admin-only, detached operational counters. No media paths, titles, tokens or raw exceptions.</summary>
public sealed class HistoryStatusSnapshot
{
    public bool Enabled { get; set; }
    public bool Busy { get; set; }
    public string Stage { get; set; } = "Starting";
    public string LedgerPhase { get; set; } = "Pending";
    public string ArchivePhase { get; set; } = "Pending";
    public string IndexPhase { get; set; } = "Idle";
    public string ReconcileReason { get; set; } = "";
    public int PendingEvents { get; set; }
    public int PendingReconciles { get; set; }
    public int LedgerEventsProcessed { get; set; }
    public int LedgerRecords { get; set; }
    public int RemovedRecords { get; set; }
    public int ReconcileLibrariesCompleted { get; set; }
    public int ReconcileLibrariesTotal { get; set; }
    public int ScannedItems { get; set; }
    public int? ScanTotal { get; set; }
    public int ArchiveRecords { get; set; }
    public int ArchiveFailures { get; set; }
    public int ArchiveChangedLibraries { get; set; }
    public int IndexTargetsCompleted { get; set; }
    public int IndexTargetsTotal { get; set; }
    public int PendingIndexTargets { get; set; }
    public double IndexProgress { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastEventUtc { get; set; }
    public DateTime? LastLedgerUtc { get; set; }
    public DateTime? LastReconcileUtc { get; set; }
    public DateTime? LastArchiveUtc { get; set; }
    public DateTime? LastIndexUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string LedgerError { get; set; } = "";
    public string ArchiveError { get; set; } = "";
    public string IndexError { get; set; } = "";
    public string PlaybackPhase { get; set; } = "Pending";
    public string PlaybackError { get; set; } = "";
    public int PlaybackCaptured { get; set; }
    public int PlaybackRestored { get; set; }
    public int PlaybackPending { get; set; }
    public string GroupPhase { get; set; } = "Pending";
    public string GroupError { get; set; } = "";
    public int HistoryGroups { get; set; }
    internal HistoryStatusSnapshot Copy() => (HistoryStatusSnapshot)MemberwiseClone();
}

internal static class HistoryStatus
{
    private static readonly object Gate = new();
    private static readonly HistoryStatusSnapshot State = new();
    internal static void Update(Action<HistoryStatusSnapshot> update)
    {
        lock (Gate) { update(State); State.UpdatedUtc = DateTime.UtcNow; }
    }
    internal static HistoryStatusSnapshot Read() { lock (Gate) return State.Copy(); }
    internal static void Error(string area, Exception ex)
    {
        // Exception messages can contain source paths, metadata, credentials or network URLs.
        var code = area + ": " + ex.GetType().Name + "（详见服务器日志）";
        Update(s =>
        {
            s.LastErrorUtc = DateTime.UtcNow;
            if (area == "Ledger") { s.LedgerError = code; s.LedgerPhase = "Error"; }
            else if (area == "Archive") { s.ArchiveError = code; s.ArchivePhase = "Error"; }
            else { s.IndexError = code; s.IndexPhase = "Error"; }
        });
    }
}

[Route("/EmbyHistory/Status", "GET", Summary = "Emby History processing status (administrator only)")]
[Authenticated(Roles = "Admin")]
public sealed class GetHistoryStatus : IReturn<HistoryStatusSnapshot> { }

[Authenticated(Roles = "Admin")]
public sealed class HistoryStatusService : IService
{
    public object Get(GetHistoryStatus request) => HistoryStatus.Read();
}
