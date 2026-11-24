using EmbyHistory.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Users;

namespace EmbyHistory;

/// <summary>
/// Keeps a history library's audience equal to its source library's audience.
/// Every write touches exactly one entry (the history library guid) in one account's EnabledFolders;
/// everything else in the policy is copied back unchanged. Adult sources are never followed.
/// </summary>
public sealed class HistoryAccessSync
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger _log;

    public HistoryAccessSync(ILibraryManager library, IUserManager users, ILogger log)
    { _library = library; _users = users; _log = log; }

    /// <summary>
    /// Library guid as Emby stores it in UserPolicy.EnabledFolders. Read from the virtual-folder
    /// registry (the same source the REST /Library/MediaFolders endpoint reports) rather than
    /// deriving it, because a numeric id in that field is silently ignored by Emby.
    /// </summary>
    public string GuidOf(string libraryId)
    {
        var info = VirtualFolder(libraryId);
        return info == null || string.IsNullOrWhiteSpace(info.Guid) ? "" : info.Guid;
    }

    public string RootFolderIdOf(string libraryId) => VirtualFolder(libraryId)?.ItemId ?? "";

    private MediaBrowser.Model.Entities.VirtualFolderInfo? VirtualFolder(string libraryId)
    {
        if (!long.TryParse(libraryId, out var internalId) || internalId <= 0) return null;
        try
        {
            return _library.GetVirtualFolders()
                .FirstOrDefault(x => x != null && x.ItemId == libraryId);
        }
        catch (Exception ex)
        {
            _log.Warn("EmbyHistory could not read virtual folders: {0}", ex.Message);
            return null;
        }
    }

    public IReadOnlyList<UserAccess> ReadAccess()
    {
        var result = new List<UserAccess>();
        foreach (var user in _users.GetUserList(new MediaBrowser.Model.Querying.UserQuery()))
        {
            if (user == null) continue;
            UserPolicy policy;
            try { policy = _users.GetUserPolicy(user); }
            catch (Exception ex) { _log.Warn("EmbyHistory access sync could not read policy for {0}: {1}", user.Name, ex.Message); continue; }
            result.Add(new UserAccess
            {
                UserId = user.InternalId.ToString(), Name = user.Name ?? "",
                IsAdministrator = policy.IsAdministrator, IsDisabled = policy.IsDisabled,
                EnableAllFolders = policy.EnableAllFolders,
                EnabledFolders = policy.EnabledFolders ?? Array.Empty<string>(),
                ExcludedSubFolders = policy.ExcludedSubFolders ?? Array.Empty<string>()
            });
        }
        return result;
    }

    /// <summary>Applies one plan. Returns the number of accounts actually written.</summary>
    public int Apply(AccessPlan plan, string historyGuid)
    {
        if (!plan.Safe && plan.Blocked == null) return 0;
        if (plan.Blocked != null) { _log.Warn("EmbyHistory access sync blocked: {0}", plan.Blocked); return 0; }
        var access = ReadAccess().ToDictionary(u => u.UserId, StringComparer.Ordinal);
        var written = 0;
        foreach (var change in plan.Changes)
        {
            if (!access.TryGetValue(change.UserId, out var snapshot))
            { _log.Warn("EmbyHistory access sync user {0} vanished; skipped", change.Name); continue; }
            if (!long.TryParse(change.UserId, out var internalId)) continue;
            var user = _users.GetUserById(internalId);
            if (user == null) continue;
            try
            {
                var policy = _users.GetUserPolicy(user);
                var before = policy.EnabledFolders ?? Array.Empty<string>();
                var after = AccessPlanner.Apply(snapshot, change, historyGuid);
                if (before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase)) continue;
                policy.EnabledFolders = after;
                // Guard the one invariant that would leak every library at once.
                if (policy.EnableAllFolders != snapshot.EnableAllFolders)
                    throw new InvalidOperationException("EnableAllFolders drift detected; refusing to write");
                _users.UpdateUserPolicy(internalId, policy);
                written++;
                _log.Info("EmbyHistory access sync {0} {1} ({2}); folders {3} -> {4}",
                    change.Action, change.Name, change.Reason, before.Length, after.Length);
            }
            catch (Exception ex)
            {
                _log.ErrorException("EmbyHistory access sync failed for " + change.Name, ex);
            }
        }
        return written;
    }

    /// <summary>Runs every FollowSource rule. Adult rules are ignored even if configured to follow.</summary>
    public void Run(IEnumerable<SourceRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule == null || !rule.Enabled) continue;
            if (!string.Equals(rule.ContentClass, "General", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(rule.AccessMode, "FollowSource", StringComparison.OrdinalIgnoreCase))
                    _log.Warn("EmbyHistory source {0} is {1}; FollowSource is ignored and access stays owner-only",
                        rule.LibraryId, rule.ContentClass);
                continue;
            }
            if (!string.Equals(rule.AccessMode, "FollowSource", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(rule.HistoryLibraryId))
            { _log.Info("EmbyHistory source {0} has no history library bound yet; access sync skipped", rule.LibraryId); continue; }
            var sourceGuid = GuidOf(rule.LibraryId);
            var historyGuid = GuidOf(rule.HistoryLibraryId);
            var access = ReadAccess();
            var bootstrap = AccessPlanner.NeedsBootstrap(access, historyGuid);
            var plan = AccessPlanner.Plan(access, sourceGuid, RootFolderIdOf(rule.LibraryId), historyGuid,
                maxChanges: 10, bootstrap: bootstrap);
            if (plan.Blocked != null) { _log.Warn("EmbyHistory access sync skipped for {0}: {1}", rule.LibraryId, plan.Blocked); continue; }
            if (plan.Changes.Count == 0) continue;
            _log.Info("EmbyHistory access sync source={0} changes={1} bootstrap={2}", rule.LibraryId, plan.Changes.Count, bootstrap);
            Apply(plan, historyGuid);
        }
    }
}
