# EmbyHistory.Core

独立 `net8.0` 类库，仅依赖 .NET BCL；不引用 Emby SDK，不需要 NuGet 包。

## 调用约定

```csharp
using EmbyHistory.Core;
var store = new HistoryStore(privateDirectory);
store.Observe(snapshot);                  // 仅内存
store.MarkRemoved(itemId);                // 仅内存；未知 ID 无操作
store.Reconcile(libraryId, items, false);  // 部分扫描：只添加/刷新，不判定缺失
store.Reconcile(libraryId, items, true);   // 完整扫描：缺失项移出，显式空集合也有效
store.Flush();                           // 唯一常规落盘点；错误向调用者传播
```

- 构造器读取 `state.json`，损坏或丢失时尝试 `backup.json`；有效备份会原子恢复主文件并设置 `RecoveryWarning`。两者均无效时抛出 `InvalidDataException`，不重建空历史。两者从未存在时才初始化为空。
- `Flush()` 在同目录写唯一临时文件、`Flush(true)` 后原子重命名；备份保留前一次有效提交。首次提交也生成有效备份。临时文件不会被当作账本加载。
- `Reconcile()` 先完整枚举、复制并验证输入再修改内存。枚举异常或跨库快照会中止整个调用；`complete=false` 永远不因缺失而移出。适配器应另外门控意外完整空扫描，核心允许显式完整空。
- `Observe()` 只接受非空 `ItemId`/`LibraryId`。输入和 `GetRecords()` 输出都深复制。所有读写由单个实例锁保护；同目录不支持多个独立写入实例/进程。
- 同源库同身份共用历史记录。每副本保留元数据，删除一份后选择剩余活跃副本中最近观测的快照；最后一份移出保留最后快照。重入库不改变首次入库时间。
- 同一 item ID 的番号或源库变化会先解除旧关联，旧历史仍保留。`FirstSeenUtc` 永远不因刷新/回库改变；`LastSeenUtc` 表示最后一次观测，不是删除时间。

## 身份

`Key = lowercase hex SHA256(UTF8(LibraryId + "\n" + normalizedIdentity))`。

已确认的番号直接作为规范身份，例如 `ABC-123`、`T28-123`、`FC2-PPV-1234567`。提取优先级为 `Code` → `Name` → `OriginalTitle` → 文件名（不扫描父目录）。番号匹配不包含 CD1/CD2、字幕或编码标签，原始名称、路径均不改写。`x264-1080p` 等编码噪声不作为番号。

无番号时依次使用 provider ID（imdb/tmdb/tvdb/anidb 优先，其余确定性排序）、原标题+年份、完整路径、名称+年份、item ID。不按 `.mkv` 等后缀或单独 basename 合并。无番号时 `HistoryRecord.Code` 为空，fallback 身份只参与 Key，不伪装成番号。

## 测试

```sh
dotnet run --project Tests/EmbyHistory.Tests.csproj
```

控制台断言测试不使用测试框架。测试文件在 `Tests/TestResults/` 下隔离创建并清理。返回码为 0 才表示全部通过。
