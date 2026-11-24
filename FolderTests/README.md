# 普通 Folder 删除与原目录分组控制流回归

## 运行

```sh
dotnet run --project RetryTests/RetryTests.csproj -c Release
dotnet build Plugin/EmbyHistory.csproj -c Release -p:EmbySdkDir=../sdk-current
```

RetryTests 链接生产 `EntryPoint`、`HistoryWorkPlanner`、`HistoryFolderPlan` 和真实 Core；SDK 使用 `sdk-current`。`FolderCases.cs` 由同一项目编译，保留原先 12 条 partial/cascade 控制流回归。

这是 SDK DispatchProxy 控制流测试，不是真实 Emby 删除 API、渲染、索引或客户端搜索验收。Archive、Index、Access 边界使用替身；测试只写自建 TMPDIR 路径。

## 删除边界

- 普通 Folder 走电影规则，不再错误地把所有 container 都路由到 SeriesEpisode。
- 回调以真实 `GetVirtualFolders().Locations` 保存 `(LibraryId, canonical SourceRoot, relative directory)`；只接受源根下的真实路径边界。源根本身、相似前缀目录、越过根的 symlink 都不作为普通目录删除证据。
- worker 先验证源挂载，再匹配快照路径，在 `GetItemById(id) == null` 时逐条 MarkRemoved。不用 File.Exists 判定删除。
- 空库保护仍开启。级联半提交、部分扫描、挂载不可达保留证明，30 秒重试；完整扫描也不能覆盖还在线的匹配子 ItemId。
- 195 条测试一次 Folder 事件、不发任何子影片事件，同一 worker 完成 195 条移出落盘、1 次 archive 边界调用和目录分组计划落盘。

## 给 BoxSet 集成的接口

```csharp
IReadOnlyList<HistoryFolderGroup> groups = HistoryFolderPlan.Build(
    records,
    new Dictionary<string, string[]> { [sourceLibraryId] = realSourceLocations });
// group.LibraryId, FolderPath, DisplayName, Key, RecordKeys
```

- `RecordKeys` **只含 RemovedUtc != null 且无 ActiveItemIds 的电影记录**。
- 是否有多片目录，以完整账本中同目录至少 2 个不同作品决定。因此其中一部回库后，另一个历史成员仍保留为同一个组，Key 不变。
- 单片单目录、源根直接放置的影片不自动建组；单片子目录可向上收拢到最近多片祖先。
- 嵌套目录优先最深的多片组，每个记录只分配一次，不重复收入所有祖先 BoxSet。
- `Key` 为 SHA256("folder-v1\\n" + LibraryId + "\\n" + canonical SourceRoot + "\\n" + canonical relative directory)，不同来源、不同真实 Locations、同名目录不混合。
- `HistoryFolderContext` 提供 `LibraryId`、`SourceRoot`、`RelativePath`、`FolderPath`、`Name`、`Key`。
- `HistoryFolderPlan.Save(dataPath, groups)` / `Load(dataPath)` 使用 `folder-context.json`，Version=1，Groups 保存 Context 和 RecordKeys；原子写入，内容不变不改 mtime，拒绝链接，格式损坏抛异常但不修改账本。worker 的事件/对账批次已调用 Save。
- 不修改 ledger Key、FirstSeen 或任何已有 archive/disc 路径。原生 BoxSet 创建、索引刷新和归组交由独立分组服务完成。

测试输出见 `control-flow-results.txt` 和 `plugin-build-results.txt`。
