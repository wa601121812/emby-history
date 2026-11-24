using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using EmbyHistory.Core;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace EmbyHistory;

/// <summary>Managed native collections; never moves existing archive cards or grants library access.</summary>
internal sealed class HistoryGroupService
{
    private const string Provider = "embyhistorygroup";
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ICollectionManager _collections;
    private readonly IProviderManager _providers;
    private readonly ILogger _log;
    private sealed class OwnedGroup
    {
        public long Id { get; set; }
        public string Source { get; set; } = "";
        public long[] Members { get; set; } = Array.Empty<long>();
        public string[] Viewers { get; set; } = Array.Empty<string>();
    }
    private readonly Dictionary<string, OwnedGroup> _owned = new(StringComparer.Ordinal);
    private bool _loaded;
    private string _lastSignature = "";
    internal int GroupCount { get; private set; }
    internal string LastError { get; private set; } = "";

    internal HistoryGroupService(ILibraryManager library, IUserManager users, ICollectionManager collections,
        IProviderManager providers, ILogger log)
    { _library = library; _users = users; _collections = collections; _providers = providers; _log = log; }

    private sealed record Group(string Key, string Name, string LibraryId, string TargetId, string[] RecordKeys);

    internal async Task SyncAsync(Plugin plugin, IReadOnlyList<HistoryRecord> records, ISet<string> selected, CancellationToken token)
    {
        var file = Path.Combine(plugin.HistoryDataPath, "groups.json");
        ArchiveWriter.RejectLink(file);
        if (!_loaded)
        {
            if (File.Exists(file)) foreach (var x in JsonSerializer.Deserialize<Dictionary<string,OwnedGroup>>(File.ReadAllText(file)) ?? new()) _owned[x.Key] = x.Value;
            _loaded = true;
        }
        var folders = _library.GetVirtualFolders();
        var groups = new List<Group>();
        foreach (var rule in (plugin.Configuration.Sources ?? Array.Empty<SourceRule>()).Where(r => r.Enabled && selected.Contains(r.LibraryId) && r.Mode != IdentityMode.SeriesEpisode))
        {
            var source = folders.FirstOrDefault(f => f.ItemId == rule.LibraryId);
            if (source == null) continue;
            var roots = new Dictionary<string,string[]> { [rule.LibraryId]=source.Locations ?? Array.Empty<string>() };
            foreach (var plan in HistoryFolderPlan.Build(records.Where(r=>r.LibraryId==rule.LibraryId), roots))
                groups.Add(new Group(plan.Key, "[历史] " + plan.DisplayName, rule.LibraryId, rule.HistoryLibraryId, plan.RecordKeys.ToArray()));
        }
        var signature = string.Join("|", groups.OrderBy(g => g.Key).Select(g => g.Key+":"+string.Join(",",g.RecordKeys)));
        // Include IDs: reindexing can replace Emby IDs without changing ledger keys.
        var archive = Path.Combine(plugin.HistoryDataPath, "archive");
        var targetIds = groups.Select(g=>g.TargetId).Distinct().Select(id=>long.TryParse(id,out var n)?n:0).Where(id=>id>0).ToArray();
        var indexed = targetIds.Length == 0 ? Array.Empty<BaseItem>() : _library.GetItemList(new InternalItemsQuery { ParentIds=targetIds, Recursive = true, IncludeItemTypes = new[]{"Movie"} })
            .Where(i => !string.IsNullOrEmpty(i.Path) && Within(i.Path, archive)).ToArray();
        signature += "|" + string.Join(",", indexed.Select(i => i.InternalId).Order());
        if (signature == _lastSignature) return;
        LastError = "";
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var target = folders.FirstOrDefault(f => f.ItemId == group.TargetId);
            if (target == null || !long.TryParse(group.TargetId, out var parentId)) continue;
            var root = Path.Combine(archive, group.LibraryId);
            if (!(target.Locations ?? Array.Empty<string>()).Any(p => Within(root, p))) continue;
            var members = indexed.Where(i => group.RecordKeys.Any(k => Within(i.Path, Path.Combine(root, k)))).ToArray();
            if (members.Length != group.RecordKeys.Length) { LastError = "分组成员尚未完成索引"; continue; }
            desired.Add(group.Key);
            BoxSet? box = null;
            // Recompute the audience on every pass: revocation must not leave a stale share behind.
            var viewers = _users.GetUserList(new UserQuery()).Where(u => CanViewTarget(u, target.Guid)).ToArray();
            if (viewers.Length == 0) { LastError = "目标历史库没有可确认的授权用户"; continue; }
            EnsureGroupedView(viewers.Select(v => v.InternalId), group.TargetId);
            if (_owned.TryGetValue(group.Key, out var existingId))
            {
                box = _library.GetItemById(existingId.Id) as BoxSet;
                if (box != null && (!box.ProviderIds.TryGetValue(Provider, out var ownerKey) || ownerKey != group.Key)) throw new InvalidOperationException("Managed group ownership mismatch");
            }
            // A changed audience (revocation or new grant) rebuilds our own collection.
            if (box != null) RebuildForAudience(box, group.Key, viewers, file);
            if (box != null && !_owned.ContainsKey(group.Key)) box = null;
            if (box == null)
            {
                box = await _collections.CreateCollection(new CollectionCreationOptions { Name=group.Name, ParentId=parentId, IsLocked=true,
                    ProviderIds=new ProviderIdDictionary { [Provider]=group.Key }, ItemIdList=members.Select(i=>i.InternalId).ToArray(), UserIds=viewers.Select(v=>v.InternalId).ToArray() });
                // CollectionManager does not persist arbitrary creation ProviderIds on this SDK.
                // Publish our ownership token through the normal metadata repository before claiming it.
                box.ProviderIds[Provider]=group.Key;
                box.UpdateToRepository(ItemUpdateType.MetadataEdit);
                _owned[group.Key] = new OwnedGroup { Id=box.InternalId, Source=group.LibraryId, Members=members.Select(i=>i.InternalId).ToArray(), Viewers=viewers.Select(v=>v.Id.ToString()).ToArray() };
                Save(file);
            }
            else
            {
                var current = box.GetChildren(new InternalItemsQuery { Recursive = false }).Select(i=>i.InternalId).ToHashSet();
                var next = members.Select(i=>i.InternalId).ToHashSet();
                var managed = _owned[group.Key].Members.ToHashSet();
                var add = next.Except(current).ToArray(); var remove = current.Intersect(managed).Except(next).ToArray();
                if (remove.Length>0) _collections.RemoveFromCollection(box, remove);
                if (add.Length>0) await _collections.AddToCollection(box.InternalId, add);
                _owned[group.Key].Members = next.ToArray(); Save(file);
            }
            if (box.Name != group.Name || box.Overview != $"【已移出文件夹】\n{members.Length} 部历史影片。点击进入查看；原文件已移出，历史卡不可播放。")
            {
                box.Name=group.Name; box.Overview=$"【已移出文件夹】\n{members.Length} 部历史影片。点击进入查看；原文件已移出，历史卡不可播放。";
                box.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }
            if (!box.HasImage(ImageType.Primary))
            {
                var poster = Path.Combine(root, group.RecordKeys[0], "poster.jpg");
                ArchiveWriter.RejectLink(poster);
                if (File.Exists(poster)) await _providers.SaveImage(box, _library.GetLibraryOptions(box), poster, ImageType.Primary, null, Array.Empty<long>(), null, false, token);
            }
        }
        foreach (var key in _owned.Keys.Where(k => !desired.Contains(k) && selected.Contains(_owned[k].Source)).ToArray())
        {
            // Missing members during an incomplete index pass are not evidence to remove a collection.
            if (groups.Any(g=>g.Key==key)) continue;
            var state = _owned[key]; var box = _library.GetItemById(state.Id) as BoxSet;
            if (box != null)
            {
                if (!box.ProviderIds.TryGetValue(Provider, out var ownerKey) || ownerKey != key) throw new InvalidOperationException("Managed group ownership mismatch");
                var childIds = box.GetChildren(new InternalItemsQuery { Recursive=false }).Select(i=>i.InternalId).ToArray();
                var removable=childIds.Intersect(state.Members).ToArray();
                if (removable.Length>0) _collections.RemoveFromCollection(box,removable);
                // Preserve an externally extended collection rather than destroy manual membership.
                if (childIds.Except(removable).Any()) { LastError="已保留手工添加成员的历史分组"; continue; }
                _library.DeleteItem(box, new DeleteOptions { DeleteFileLocation=false, DeleteFromExternalProvider=false });
            }
            _owned.Remove(key); Save(file);
        }
        GroupCount = _owned.Count;
        if (LastError.Length==0) _lastSignature=signature;
    }

    private void RebuildForAudience(BoxSet box, string key, User[] viewers, string file)
    {
        // This SDK exposes no settable Shares on BoxSet. When the permitted audience changes
        // (especially a revocation), rebuild our own collection so the old share cannot persist.
        var desired = viewers.Select(v => v.Id.ToString()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var state = _owned[key];
        if (state.Viewers.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase)) return;
        var managed = state.Members.ToHashSet();
        var children = box.GetChildren(new InternalItemsQuery { Recursive = false }).Select(i => i.InternalId).ToArray();
        var ours = children.Where(managed.Contains).ToArray();
        if (ours.Length > 0) _collections.RemoveFromCollection(box, ours);
        if (children.Except(ours).Any()) { LastError = "分组含手工成员，未重建以免丢失"; return; }
        _library.DeleteItem(box, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
        _owned.Remove(key); Save(file);
    }

    private void EnsureGroupedView(IEnumerable<long> viewers, string targetId)
    {
        // Verified from Emby 4.10's itemstab/videostab: this key is library-specific.
        // Do NOT use items-Movie-Folder: it changes every Movie list for the user.
        var key=targetId+"-1-videos-groupItemsIntoCollections";
        foreach (var userId in viewers)
        {
            if (string.IsNullOrEmpty(_users.GetUserSetting(userId,key)))
                _users.UpdatePartialUserSettings(userId,new Dictionary<string,string> { [key]="true" });
        }
    }

    private bool CanViewTarget(User user, string guid)
    {
        var policy=_users.GetUserPolicy(user);
        if (policy.IsDisabled) return false;
        if ((policy.ExcludedSubFolders??Array.Empty<string>()).Any(x=>x.StartsWith(guid+"_",StringComparison.OrdinalIgnoreCase))) return false;
        return policy.EnableAllFolders || (policy.EnabledFolders??Array.Empty<string>()).Contains(guid,StringComparer.OrdinalIgnoreCase);
    }

    private void Save(string file)
    {
        ArchiveWriter.RejectLink(file); var tmp=file+".tmp"; ArchiveWriter.RejectLink(tmp);
        File.WriteAllText(tmp,JsonSerializer.Serialize(_owned)); File.Move(tmp,file,true);
    }
    internal static string? PackagePath(string path, string[] roots)
    {
        foreach (var source in roots.OrderByDescending(x=>x.Length))
        {
            if (!Within(path,source)) continue;
            var relative=Path.GetRelativePath(source,path); var parts=relative.Split(Path.DirectorySeparatorChar);
            return parts.Length>=2 ? Path.Combine(Path.GetFullPath(source),parts[0]) : null;
        }
        return null;
    }
    private static bool Within(string path,string root)
    {
        path=Path.GetFullPath(path);root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(root,StringComparison.Ordinal) || path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal);
    }
}
