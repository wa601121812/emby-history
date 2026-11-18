# ArchiveWriter 针对性回归

## 运行

从项目根目录执行（SDK 位于本 profile scratch）：

```sh
dotnet run --project ArchiveTests/ArchiveTests.csproj -c Release
dotnet build Plugin/EmbyHistory.csproj -c Release -p:EmbySdkDir=../sdk-current
```

测试无外部 NuGet 包，临时目录只创建于 `$TMPDIR`，结束自动清理。海报 renderer 为可计数、可故障注入的确定性 **mock**，其输出不是 JPEG，不代表 Skia、NAS 或 Emby 搜索端到端验收。

## 覆盖与实际结果

- 24 项通过：TV NFO 的文件名/根元素/历史标题/对象级 provider IDs、本地身份、季号与特别篇 0；XML 非法字符。
- 0.5.3 `series.nfo` / `episodedata` 原位迁移，不改账本、key、目录、`.disc` 路径及原 mtime；也覆盖不先迁移直接回库。
- 第二次 Sync（包括反序 records 和重新构造 writer）所有文件 **内容与 mtime** 保持；不重复 renderer。
- 单集元数据更新只变对应 NFO，不动占位和海报；渲染失败可重试但不产生相同文件写入。
- 单集、整季、整剧回库撤卡；完整自有树回收；未知 NFO/disc/子目录保留，目录有用户内容时保留 ownership marker。
- 无主目录、无主季目录、修改后的生成文件 fail closed；祖先/season/marker/悬空 NFO 符号链接不越界读写/删除。
- 改剧名保留原目录与占位 basename；源 poster 路径不同但相同大小/mtime 时仍失效缓存。
- 1005 集批量：本机实际首轮 972ms，第二轮 152ms；2020 个文件内容与 mtime 均不变、0 次文件写入、0 次渲染。时间仅是当前本机 mock 基准，不外推 NAS。
- 插件 Release build：0 warnings / 0 errors。

日志见 `archive-regression.log`、`plugin-build.log`。

## 父调用方接口（保持原 Sync 签名）

`int Sync(IEnumerable<HistoryRecord>, ISet<string>, Action<string, Exception>?)` 仍返回失败记录数。

新增两个同步完成后的只读、脱离内部可变集合的快照：

- `LastChangedLibraryIds : IReadOnlyCollection<string>`：本轮有实际文件变化的**源库 ID**（不是历史目标库 ID）。父调用方需通过映射去重目标库，再做主动增量索引。
- `LastChangedPaths : IReadOnlyCollection<string>`：本轮实际写入/删除的绝对路径，含撤卡路径、回收目录、部分成功后失败的变化。不包含仅创建/更新 ownership marker 或 manifest 的管理写入；未变化内容不会触发报告。

上次保存的快照不会被下一次 Sync 清空。无变化时两者均为空。该 writer 假定父 worker 串行调用 Sync；不提供多线程 Sync 并发保证。

## 所有权与迁移边界

- 新写入使用 `.embyhistory-files.json` 明确记录生成文件名及 SHA256。写/删前核对目录所有权、祖先路径非符号链接及文件 hash；用户修改/替换的文件不再自动覆盖/删除。
- 0.5.3 无 manifest：只认带 EmbyHistory tag、lockdata、匹配本地 uniqueid 的剧/集 NFO（旧季 NFO 另核对 seasonnumber 和生成 plot 前缀）；仅接管与其明确配对的零字节 `.disc` 及旧生成 stamp/poster 固定名。未知文件从不按扩展名批量删除。
- manifest 按目录批量原子保存，避免大季每集重新序列化整份 manifest。进程被强杀时可能留下未提交文件；此时 fail closed，不把未知文件猜作自有。这不是跨文件事务或对抗并发恶意 rename 的 openat/no-follow 沙箱。
- 不清账本，不升级身份 Scheme，不接触正式服务器，不重启服务。

## 已有旧测试的适配

`Tests/Program.cs` 的旧 `<episodedata>` 断言已随 TV NFO 契约修正，当前父级复跑核心测试为 **53/53**。本目录保留的 `legacy-core-tests.log` 是修正之前的历史证据，不代表当前结果。

原 AdapterTests 也存在“仅 marker 即可删除 history.disc”、“缺 poster 必须重新写 NFO/stamp”等旧断言，与本次显式所有权和幂等需求不一致；本次未修改或声称其通过。
