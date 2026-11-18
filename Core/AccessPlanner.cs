namespace EmbyHistory.Core;

/// <summary>Plain snapshot of one account's library access, detached from Emby types.</summary>
public sealed class UserAccess
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public bool IsDisabled { get; set; }
    public bool EnableAllFolders { get; set; }
    public string[] EnabledFolders { get; set; } = Array.Empty<string>();
    public string[] ExcludedSubFolders { get; set; } = Array.Empty<string>();
}

public enum AccessAction { Add, Remove }

public sealed class AccessChange
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public AccessAction Action { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class AccessPlan
{
    public List<AccessChange> Changes { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    /// <summary>Set when the plan is too large or cannot be trusted; callers must apply nothing.</summary>
    public string? Blocked { get; set; }
    public bool Safe => Blocked == null && Changes.Count > 0;
}

/// <summary>One configured source library, detached from Emby and plugin types.</summary>
public sealed class RuleView
{
    public string LibraryId { get; set; } = "";
    public string ContentClass { get; set; } = "Unclassified";
    public string HistoryLibraryId { get; set; } = "";
}

/// <summary>
/// Decides which accounts should hold the history library, mirroring source-library access.
/// Deliberately conservative: never widens access beyond the source library, never touches
/// administrative accounts, and refuses a change set too large to be a normal permission edit.
/// </summary>
public static class AccessPlanner
{
    /// <summary>
    /// A history library must never receive both general and adult cards: anyone who can see it
    /// would see the other category's history. Returns the library ids that violate this, so the
    /// caller can refuse to publish for them instead of trusting the settings page.
    /// </summary>
    public static HashSet<string> CrossClassTargets(IEnumerable<RuleView> rules) =>
        rules.Where(r => !string.IsNullOrWhiteSpace(r.HistoryLibraryId))
            .GroupBy(r => r.HistoryLibraryId, StringComparer.Ordinal)
            .Where(g => g.Select(r => Normalize(r.ContentClass)).Distinct().Count() > 1)
            .SelectMany(g => g.Select(r => r.LibraryId))
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unclassified" : value.Trim().ToUpperInvariant();

    /// <param name="sourceFolderId">
    /// Internal id of the source library root. Emby stores whole-library exclusions as
    /// "&lt;library guid&gt;_&lt;root folder item id&gt;", so this is needed to honour them.
    /// </param>
    /// <param name="maxChanges">Hard ceiling; a bigger diff is treated as misconfiguration.</param>
    /// <param name="bootstrap">
    /// True only when no ordinary account holds the history library yet. The first population is
    /// necessarily large, so the size guard is lifted for that one pass; later passes stay capped.
    /// </param>
    public static AccessPlan Plan(IEnumerable<UserAccess> users, string sourceGuid, string sourceFolderId,
        string historyGuid, int maxChanges = 10, bool bootstrap = false)
    {
        var plan = new AccessPlan();
        if (string.IsNullOrWhiteSpace(sourceGuid) || string.IsNullOrWhiteSpace(historyGuid))
        {
            plan.Blocked = "Source or history library guid could not be resolved; nothing changed.";
            return plan;
        }
        if (string.Equals(sourceGuid, historyGuid, StringComparison.OrdinalIgnoreCase))
        {
            plan.Blocked = "History library must not be the source library itself.";
            return plan;
        }

        var all = users.ToList();
        foreach (var user in all)
        {
            if (string.IsNullOrWhiteSpace(user.UserId)) continue;
            var has = HasFolder(user, historyGuid);
            if (user.IsAdministrator)
            {
                // Admins reach every library already; rewriting their policy buys nothing.
                plan.Skipped.Add(user.Name + ": administrator");
                continue;
            }
            if (user.IsDisabled)
            {
                plan.Skipped.Add(user.Name + ": account disabled");
                continue;
            }
            if (CanSeeSource(user, sourceGuid, sourceFolderId))
            {
                if (!has) plan.Changes.Add(new AccessChange { UserId = user.UserId, Name = user.Name,
                    Action = AccessAction.Add, Reason = "sees source library" });
            }
            else if (has)
            {
                plan.Changes.Add(new AccessChange { UserId = user.UserId, Name = user.Name,
                    Action = AccessAction.Remove, Reason = "no longer sees source library" });
            }
        }

        if (plan.Changes.Count > maxChanges && !bootstrap)
            plan.Blocked = "Plan would change " + plan.Changes.Count + " accounts (limit " + maxChanges +
                "); treated as misconfiguration. Nothing changed.";
        return plan;
    }

    /// <summary>True when no ordinary account currently has the history library.</summary>
    public static bool NeedsBootstrap(IEnumerable<UserAccess> users, string historyGuid) =>
        !users.Any(u => !u.IsAdministrator && string.Equals(historyGuid,
            u.EnabledFolders.FirstOrDefault(x => string.Equals(x, historyGuid, StringComparison.OrdinalIgnoreCase)),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>GUID form only: Emby silently ignores a numeric item id in EnabledFolders.</summary>
    public static bool HasFolder(UserAccess user, string guid) =>
        user.EnableAllFolders || user.EnabledFolders.Any(x => string.Equals(x, guid, StringComparison.OrdinalIgnoreCase));

    public static bool CanSeeSource(UserAccess user, string sourceGuid, string sourceFolderId)
    {
        if (!HasFolder(user, sourceGuid)) return false;
        // ExcludedSubFolders can exclude the whole library even when EnableAllFolders is set.
        foreach (var raw in user.ExcludedSubFolders)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) continue;
            var separator = value.LastIndexOf('_');
            if (separator <= 0) continue;
            var guid = value[..separator];
            var folder = value[(separator + 1)..];
            if (!string.Equals(guid, sourceGuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (sourceFolderId.Length == 0 || folder == sourceFolderId) return false;
        }
        return true;
    }

    /// <summary>
    /// Applies one change to a copy of the folder list. Only ever adds or removes the history
    /// library; every other entry, and the EnableAllFolders flag, is preserved verbatim.
    /// </summary>
    public static string[] Apply(UserAccess user, AccessChange change, string historyGuid)
    {
        var kept = user.EnabledFolders
            .Where(x => x != null)
            .Where(x => !string.Equals(x, historyGuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (change.Action == AccessAction.Add) kept.Add(historyGuid);
        return kept.ToArray();
    }
}
