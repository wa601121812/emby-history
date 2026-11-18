# Emby History

**Keep deleted movies and TV episodes searchable in Emby — as amber "removed" history cards.**
**让已删除的电影和剧集继续在 Emby 中可搜索 —— 以琥珀色「已移出」历史卡片保留。**

Emby History is a native C# plugin (Emby 4.10.2, .NET 8). It records what was in your library, and when the last copy of an item disappears it writes an official Emby **Media Stub** plus NFO/poster into a separate history library. Your users keep finding the item in native Emby search, marked `[历史]`, with the original title, overview and artwork — while the media file itself is gone.

> Scope of verification: built and tested against **Emby 4.10.2.0** only. This is a custom plugin, not an official Emby product; it does not patch Emby's search or database.

---

## English

### What it does

- **Durable independent ledger** — JSON ledger with atomic writes, a validated backup, and multi-copy identity per `(source library, item identity)`; not a disposable mirror of the current library.
- **Movies, series, seasons and episodes** — an item becomes history only when its **last** active copy leaves the source. Re-adding the same identity withdraws the historical card and keeps the ledger entry (original first-seen time preserved).
- **Native Emby search** — the archive is a normal Emby library containing `.disc` stubs, NFO and generated posters. No browser extension, no direct `library.db` edits, no Harmony patching.
- **Correct TV metadata** — `tvshow.nfo` / `season.nfo` / `episodedetails`, original episode title, overview and per-object provider IDs, with `[历史]` prefix on series, season and episode titles.
- **Fast, targeted indexing** — after writing or withdrawing files the plugin asks Emby to refresh the affected paths *and waits for that refresh*, instead of waiting for the file watcher (default 90 s) or triggering a full library scan.
- **Cascading deletes** — a whole-series or whole-season delete is handled immediately from saved series/season context, and the deletion evidence is retained until Emby has actually finished removing the child items (no silence, no false "complete").
- **Visible status** — the settings page shows ledger / archive / index stage, counters, last success time and errors. `GET /EmbyHistory/Status` exposes the same data to administrators only. It never claims Emby's global scan percentage.
- **Safe file ownership** — generated files are tracked in a per-directory manifest of names and SHA-256 hashes. Withdrawal deletes only verified generated files; modified, unknown or symlinked files are left untouched (fail closed).
- **Amber history cards** — original artwork kept in colour and aspect ratio, warm amber border, top-right angled `已移出` badge, `[历史]` title prefix. Chinese badge text is embedded as a PNG, so no server fonts are required.

### How it works

```text
archive/<source library id>/<record key>/history.disc        movie stub (0 bytes)
archive/<source library id>/<record key>/movie.nfo
archive/<source library id>/<record key>/poster.jpg

archive/<source library id>/<series title (year)>/tvshow.nfo
archive/<source library id>/<series title (year)>/Season 01/season.nfo
archive/<source library id>/<series title (year)>/Season 01/<series> - S01E01.disc
archive/<source library id>/<series title (year)>/Season 01/<series> - S01E01.nfo
```

The history library must be a **mixed-content** library (empty `CollectionType`) to hold movies and episodes together, or a `tvshows` library for episodes only. Point the plugin's target at that library and attach the matching `archive/<source id>` path to it — the plugin writes files, it does **not** attach library paths for you. The status page reports an unattached mapping instead of claiming the card is indexed.

### Requirements

| Item | Value |
|---|---|
| Emby Server | 4.10.2.x (tested); other versions untested |
| Runtime | .NET 8 (as shipped by Emby 4.10) |
| Server OS | Linux container or native; paths must be readable/writable by the Emby process |
| Write access | `<Emby data>/emby-history` (ledger + archive) and `/config/plugins` |

### Install

1. Stop Emby and back up `plugins/`, `plugins/configurations`, `<data>/emby-history` and the Emby database.
2. Copy **only** `EmbyHistory.dll` into `plugins/`. Do not copy Emby or SkiaSharp assemblies.
3. Start Emby, open **Dashboard → Plugins → Emby History**, and check the version.
4. Create the target history library (mixed content recommended) and attach `archive/<source id>` paths you intend to use.
5. In the plugin settings, tick the source libraries, pick identity mode per library, and choose the target history library. Several sources may share one target.
6. Grant the history library to the users who should see it. The plugin does not grant library permissions for adult content; owner-only visibility must be configured in Emby itself.

### Identity modes

| Mode | Use for | Identity |
|---|---|---|
| `Code` | JAV-style code libraries | Normalised code (FC2, HEYZO, date codes, filename precedence) |
| `Provider` | Movies/series scraped with IMDb/TMDb | Provider ID, falling back to title + year |
| `LocalPath` | Adult libraries without stable IDs | First-seen path, conservative |
| `SeriesEpisode` | TV / anime | Series identity + season + episode |

Identity mode is independent of the Adult/General classification. Changing a mode re-keys records instead of inventing removals.

### Build and test

```sh
# Put MediaBrowser.Controller/Common/Model + SkiaSharp DLLs from your Emby install into sdk/
dotnet build Plugin/EmbyHistory.csproj -c Release -p:EmbySdkDir=../sdk
dotnet run --project Tests/EmbyHistory.Tests.csproj -c Release           # core: 53
dotnet run --project ArchiveTests/ArchiveTests.csproj -c Release        # filesystem: 24 (mock renderer)
dotnet run --project IndexContractTests/IndexContractTests.csproj -c Release  # SDK contract: 6
python3 RetryTests/run-tests.py                                          # control flow: 12
```

`RetryTests` links the real `EntryPoint.cs` and executes its private scan/worker paths against a real ledger with SDK proxies; it needs the extra Emby assemblies described in `RetryTests/README.md`. None of the suites redistributes Emby binaries.

### Known limitations

- **Access control is not implemented by this plugin.** Adult/General classification only prevents mixing audiences in one target; per-library visibility is Emby's `EnabledFolders`. `OwnerOnly` does not create a whitelist.
- The optional `FollowSource` mode is a 10-minute policy sync: revocation windows, bulk-change caps and administrator skipping are unresolved. Do not rely on it for secrecy.
- Emby's native per-library search will not cross into the history library; use global search or open the history library.
- Offline history cards have no playable media. Some clients still draw a play button.
- Series folder naming is `title (year)` for compatibility; two different series with the same title and year are refused rather than overwritten.
- Multi-episode files (one file, `S01E01-E02`) are not modelled as ranges.
- No formal support for Emby major upgrades; retest in an isolated instance first.

### License

MIT — see [LICENSE](LICENSE). Emby and its assemblies are not included and remain under their own terms.

---

## 中文

### 功能

- **独立持久账本**：JSON 账本，原子写入 + 校验备份，`(源库, 条目身份)` 维度记录多副本；不是当前媒体库的可丢弃镜像。
- **电影、剧、季、单集**：只有**最后一个**在库副本离开源库时才生成历史卡；同身份重新入库会撤下历史卡并保留账本与原首次记录时间。
- **使用 Emby 原生搜索**：历史库是普通 Emby 媒体库，内容是 `.disc` 占位、NFO 和生成的海报。不改 `library.db`、不注入浏览器扩展、不用 Harmony 拦截搜索。
- **剧集元数据正确**：`tvshow.nfo` / `season.nfo` / `episodedetails`，保留原集名、剧情与各对象自己的 provider ID，剧/季/集标题带 `[历史]`。
- **主动增量索引**：写完或撤下归档后，只请求刷新受影响的路径并等待刷新完成；不再等目录监控（默认 90 秒）或触发全库扫描。
- **整剧/整季删除**：使用保存的剧/季上下文立即对账；Emby 级联删除尚未提交完时保留证据并短周期重试，不误报完成。
- **可见状态**：设置页显示账本/归档/索引阶段、数量、最近成功时间与错误；管理员接口 `GET /EmbyHistory/Status` 返回同样信息，不冒充 Emby 全局扫描百分比。
- **自有文件保护**：按目录记录生成文件名与 SHA-256；撤卡只删已验证的自有文件，被修改、未知或符号链接文件一律保留（fail closed）。
- **琥珀历史卡**：原图保留彩色与比例，暖琥珀外框，右上斜切「已移出」标签，标题前置 `[历史]`；中文角标是内嵌 PNG，不依赖服务器字体。

### 安装

1. 停止 Emby，备份 `plugins/`、`plugins/configurations`、`<数据目录>/emby-history` 和 Emby 数据库。
2. 只把 `EmbyHistory.dll` 放进 `plugins/`，不要复制 Emby 或 SkiaSharp 程序集。
3. 启动 Emby，打开 **控制台 → 插件 → Emby History** 确认版本。
4. 建立目标历史库（建议混合内容），并把要用的 `archive/<源库ID>` 路径挂进去；插件只写文件，**不会自动挂载库路径**。
5. 在设置页勾选源库、选择识别方式和目标历史库（多对一允许），保存后无需重启。
6. 在 Emby 里给相应账号授予历史库权限。成人内容的「仅我可见」必须由 Emby 库权限控制，插件不代为授权。

### 识别方式

| 模式 | 适用 | 身份 |
|---|---|---|
| `Code` | 番号类成人库 | 规范化番号（FC2、HEYZO、日期码、文件名优先） |
| `Provider` | 有 IMDb/TMDb 的影视 | 提供方 ID，缺失时退回标题+年份 |
| `LocalPath` | 无稳定 ID 的成人库 | 首次出现路径，保守 |
| `SeriesEpisode` | 电视剧 / 动漫 | 剧身份 + 季 + 集 |

识别方式与成人/普通分类相互独立；改模式会静默重键，不会制造「已移出」。

### 构建与测试

```sh
# 把 Emby 安装里的 MediaBrowser.Controller/Common/Model 与 SkiaSharp DLL 放到 sdk/
dotnet build Plugin/EmbyHistory.csproj -c Release -p:EmbySdkDir=../sdk
dotnet run --project Tests/EmbyHistory.Tests.csproj -c Release
dotnet run --project ArchiveTests/ArchiveTests.csproj -c Release
dotnet run --project IndexContractTests/IndexContractTests.csproj -c Release
python3 RetryTests/run-tests.py
```

### 已知限制

- **插件本身不实现访问控制**。成人/普通分类只用于阻止不同受众共用同一历史库；可见性仍由 Emby 库权限决定，`OwnerOnly` 不是自动白名单。
- `FollowSource` 是十分钟轮询的权限同步，存在撤权延迟、批量变更上限和跳过管理员等未解决问题，不能当作保密手段。
- Emby 单库搜索不会跨到历史库，请用全局搜索或进入历史库搜索。
- 离线历史卡没有可播放文件，部分客户端仍会显示播放按钮。
- 剧目录使用「剧名 (年份)」，同名同年不同剧会因所有权校验而拒绝，不会覆盖。
- 多集连体文件（`S01E01-E02`）未建模为范围。
- 不承诺跨 Emby 大版本兼容，升级前先在隔离实例重测。

### 许可

MIT，见 [LICENSE](LICENSE)。仓库不包含 Emby 程序集，Emby 本身适用其自身许可。
