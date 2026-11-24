# 逐用户播放快照服务：集成与验收边界

## 已交付

仅新增 `Plugin/HistoryPlaybackState.cs`、`Plugin/HistoryPlaybackService.cs` 和本目录；没有改 EntryPoint、Core schema 或 ArchiveWriter。独立 sidecar 为 `<HistoryDataPath>/playback-state.v1.json`，上一版为 `.bak`。按 **源 LibraryId + 原账本 RecordKey + 用户内部 ID/GUID + 源 ItemId** 保存；无用户名、令牌或密码。保存真实时长、位置、Played、PlayCount、LastPlayed、PlaystateModified、实际源播放键和证据来源。

- 读源状态从不写源；首次存量及实时事件均核验该用户对实际源条目的可见性、源库归属、未禁用状态。
- Movie / Episode 独立快照；Folder / Series / Season 显式前置 hook 可捕获已知后代，无需假定逐集事件齐全。
- 空白/不存在数据不覆盖已有有效进度；不会把所有者进度抄给其他人。显式全零重置也保守不抹旧的非零快照，这是防止删除后默认值覆盖的有意边界。
- 历史写入必须同时满足：源归档路径边界、`.disc`、Movie/Episode 类型、精确 `embyhistory` key、自有 marker、已提交 manifest 中 disc/NFO 哈希、NFO 相同 uniqueid，无符号链接。
- 原生时长通过 `historyItem.RunTimeTicks` + SDK `ILibraryManager.UpdateItem(..., ItemUpdateType.MetadataEdit)` 持久化（与 repository 路径同类 SDK 操作），随后逐用户 `SaveUserData(..., Import, ...)`，再回读。无需画进度条，不修改封面。
- 无变化不重写；不改收藏、评分、音轨/字幕选择、HideFromResume。较新的历史卡用户编辑优先。
- sidecar 使用 flush-to-disk + 同目录原子替换；备份恢复时保留 `.corrupt-*`，两份都坏则拒绝重建空库。Linux 文件权限 0600，拒绝包括悬空链接在内的符号链接。
- 批量采集一批 flush，批量恢复按 source/key 预建索引，避免对每条重新复制整个 sidecar/遍历全部候选。
- 每个操作返回 `HistoryPlaybackResult(Captured, Restored, Skipped, Failed)`，异常通过回调报告，不向账本工作流抛出。调用方需保留 Failed/未索引的重试，不把“Skipped”一律解释成成功。

## 接入接口

```csharp
var playback = new HistoryPlaybackService(library, users, userDataManager,
    plugin.HistoryDataPath,
    (stage, ex) => log.ErrorException("Playback: " + stage, ex));

// 生命周期：提供仅启用且通过安全校验的源记录；事件线程调用，须返回线程安全的脱离快照。
playback.Start(() => SelectedRecordsSnapshot());

// 启动基线／已有在线媒体采集（应在库身份 Observe 后调用）。
var initial = playback.CaptureRecords(SelectedRecordsSnapshot());

// 新增或更新源：由已解析的账本拿准确原 key，不能重新猜番号/标题。
playback.Capture(sourceItem, sourceLibraryId, canonicalLedgerKey);

// 若集成层确有删除前调用点，在条目仍存活时调用，容器也支持。
playback.CaptureRemoving(sourceItemOrContainer);

// writer 发布 + 目标索引完成之后；每次 metadata refresh 后均重新补时长/用户数据。
var restored = playback.RestoreRecords(SelectedRecordsSnapshot(), cancellationToken);
// 已掌握确切历史索引条目时亦可：
playback.Restore(record, indexedHistoryItem, cancellationToken);

// 退出必须解除事件订阅。
playback.Dispose();
```

`Start` 幂等订阅 **IUserDataManager.UserDataSaved** 与 **ILibraryManager.ItemRemoved**。后者只做尽力补采，不是删除前保证。`Start` 不自动首次扫描，需显式 `CaptureRecords`。构造函数最后的 `canRead` 参数是测试/专门授权适配接缝，生产默认实际 `BaseItem.IsVisible(user)`，不能传恒 true。

### 重要 SDK 事实（本地 sdk-current，非网上推测）

1. **4.10.2 的 ILibraryManager 没有 ItemRemoving 事件。** 因而不能写不存在的订阅、不能把 ItemRemoved 改名后声称已删除前采集。持续 `UserDataSaved` + 首次基线是当前 SDK 下的主要保障；`CaptureRemoving` 只有父集成真实拥有前置调用点才有前置语义。完整事件枚举及 IL 证据见 `sdk-contract.txt`，可用 `--inspect-sdk` 重跑。
2. `IUserDataManager.GetUserData(User, BaseItem)`、`GetUserData(User, long)` 和 `SaveUserData(User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken)` 均是实际 SDK 签名。事件 `UserDataSaveEventArgs` 带 `User/Item/UserData/SaveReason/CollectionFolders`；`UserItemData.LastPlayedDate` 为 `DateTimeOffset?`。
3. **赋值 BaseItem.UserDataKey 不能隔离播放状态。** SDK 实际 `GetUserDataKey(IDataContext)` 会调用类型实现：BaseItem 回退条目 GUID，Video/Movie 可用 TMDB/IMDb/TVDB，Episode 可用剧键＋季集号。测试直接调用实际 SDK，证明两个 GUID 不同但同 TMDB 的 Movie 生成同一个播放键。
   - 历史实际读取的数据已等于快照：不做 userdata 写入，只补历史时长。
   - 若目标键与任何保存的源键相同且内容不同／缺键：**拒绝 SaveUserData 并报告 Failed**，不能为让界面出现条而改变源键状态。
   - 未擅自移除历史 provider IDs、改 metadata 语义或修改源状态。若隔离实例发现共享键数据会在删除后清空，需要父层另行验证安全隔离键方案；本服务对此不宣称通吃。
4. 当前 `MediaSnapshot` **没有 RunTimeTicks**，writer NFO 也没有 runtime。本 sidecar 从源 BaseItem 记录真实时长；每次历史刷新后再次 Restore。只恢复 position 不保证原生 `UserDataDto.PlayedPercentage`。
5. Movie 的 `GetRunTimeTicksForPlayState()` 调用服务器静态 LiveTvManager；脱离服务器的 SDK entity 无此依赖会空引用。纯回归没有伪造该 native DTO 路径，实际百分比/封面条必须真实 Emby 验收。

## 安装前已删除记录：只凭实际证据补回

```csharp
playback.RecoverRemovedRecords(records,
    (user, record) => IndependentlyVerifiedPreRemovalAuthorization(user, record));
```

默认不启用。前置要求：记录确实 Removed 且没有 active copy、源 numeric ItemId 精确有效且当前不存在、该用户删除前权限有独立证据。随后真的调用 `GetUserData(user, oldNumericItemId)`，只有返回有 Key 且 position/Played/PlayCount/LastPlayed 至少一个有效才写快照。没有数据就跳过；不把“数据库可能有 userdatalog”当恢复成功。旧记录没有保存时长则维持未知，不从播放位置推测时长，因此无法承诺补出原生百分比。

## 多副本与个体语义

每个源 copy 单独留存，同一用户恢复时按最近 LastPlayed → PlaystateModified → CapturedUtc → 源 ItemId 稳定选择，不用 `max(position)`，不合并其他用户。Emby 一张历史卡只能有一个 RunTimeTicks；本实现采用该记录最近采集的正时长，不对不同版本位置进行猜测或比例换算。若多个版本总时长不同，这是单卡表达能力边界。

## 可复现本地验证

```sh
DOTNET=dotnet
"$DOTNET" build Plugin/EmbyHistory.csproj -c Release -p:EmbySdkDir=../sdk-current
"$DOTNET" run --project PlaybackTests -c Release -p:EmbySdkDir=../sdk-current
"$DOTNET" run --project PlaybackTests -c Release -- --inspect-sdk
```

`regression.log` 为真实执行记录，覆盖 36 项：权限/跨用户隔离、事件生命周期、持久化与备份故障、预删 hook/容器、Movie/Episode 正确历史解析、manifest/符号链接防护、原生播放键冲突、只补时长/刷新重补、缺证旧历史不猜、同用户多版本、较新历史编辑保护、异常和取消、时长落库失败重试、并发 sidecar 等。

**验收声明：纯回归通过不是 Emby E2E 或 GUI 通过。** 父层仍需在同镜像隔离实例用真实长视频、两个有不同状态的用户与无权限用户，分别验证：删除前后源及历史实际 key、RunTimeTicks、UserDataDto.PlayedPercentage、Played/position/count/date、原生封面条；电影与单集/季/剧删除、刷新后重补、重启、回库及旧记录证据恢复。未操作生产 API、替换生产 DLL、重启或正式删除。
