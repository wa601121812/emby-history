using EmbyHistory.Core;
using MediaBrowser.Model.Plugins;

namespace EmbyHistory;

/// <summary>
/// One monitored source library. ContentClass decides who may ever see its history; Mode decides how
/// two files are recognised as the same work. They are separate on purpose: an adult library may be
/// free-form with no release codes at all.
/// </summary>
public class SourceRule
{
    public string LibraryId { get; set; } = "";
    public IdentityMode Mode { get; set; } = IdentityMode.Code;
    /// <summary>Adult | General | Unclassified. Unclassified never publishes to other accounts.</summary>
    public string ContentClass { get; set; } = "Unclassified";
    /// <summary>History library that receives this source's cards. Empty = no history library bound yet.</summary>
    public string HistoryLibraryId { get; set; } = "";
    /// <summary>OwnerOnly (default) | ExplicitUsers | FollowSource. Never auto-grants adult sources.</summary>
    public string AccessMode { get; set; } = "OwnerOnly";
    public string[] ExplicitUserIds { get; set; } = Array.Empty<string>();
    public bool Enabled { get; set; } = true;
}

public class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = false;
    /// <summary>Legacy list. Kept so an existing deployment keeps working; treated as Code mode.</summary>
    public string[] SourceLibraryIds { get; set; } = Array.Empty<string>();
    /// <summary>Preferred list. When non-empty it replaces SourceLibraryIds.</summary>
    public SourceRule[] Sources { get; set; } = Array.Empty<SourceRule>();
    // Empty scans are held for manual investigation, never inferred as mass removal.
    public bool AllowEmptyLibraryReconcile { get; set; } = false;
}
