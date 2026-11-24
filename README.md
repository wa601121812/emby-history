# Emby History

**Keep deleted movies and TV episodes searchable in Emby — as amber "removed" history cards.**
**让已删除的电影和剧集继续在 Emby 中可搜索 —— 以琥珀色「已移出」历史卡片保留。**

A native C# plugin (Emby 4.10.2, .NET 8). It records what was in your library, and when the last copy of an item disappears it writes an official Emby **Media Stub** plus NFO/poster into a separate history library. Items stay findable in native Emby search, marked `[历史]`, with the original title, overview, artwork — and, uniquely, **the viewer's own playback progress**.

> Verified against **Emby 4.10.2.0** only. Custom plugin, not an official Emby product; it does not patch Emby's search or database.

---

## What it does

- **Durable ledger** — JSON ledger with atomic writes and a validated backup, keyed by `(source library, identity)` with multi-copy tracking.
- **Movies, series, seasons and episodes** — an item becomes history only when its **last** active copy leaves the source. Re-adding the same identity withdraws the card and keeps the ledger entry and first-seen time.
- **Whole-folder deletes** — deleting a directory (including nested ones) is captured immediately from saved per-item identity; no waiting for the periodic reconcile, and no per-file delete events required.
- **Native search** — the archive is a normal Emby library of `.disc` stubs, NFO and generated posters. No browser extension, no `library.db` edits, no Harmony.
- **Folder grouping** — a source directory that held two or more works becomes **one native collection** in the history library (`[历史] Folder name`). Members remain individual, searchable history cards; single-film folders and root-level movies are left alone.
- **Playback progress** — before deletion, position, watched state, play count and original runtime are saved **per user**; after the card is indexed they are written onto the history item, so Emby draws its own progress bar. Progress is never drawn into the shared poster and never copied between accounts.
- **Targeted indexing** — after writing or withdrawing files the plugin refreshes exactly the affected paths and waits for that refresh, instead of waiting for the file watcher or scanning the whole library.
- **Visible status** — ledger / archive / index / playback / grouping phases with counters and errors at `GET /EmbyHistory/Status` (administrators only). Pending index or unmounted target reports `Waiting`, never a false `Completed`.
- **File ownership** — generated files are tracked in a per-directory manifest with SHA-256; withdrawal deletes only verified generated files, and modified, unknown or symlinked files are left untouched.

## How it works

```text
archive/<source library id>/<record key>/history.disc        movie stub
archive/<source library id>/<record key>/movie.nfo
archive/<source library id>/<record key>/poster.jpg

archive/<source library id>/<series title (year)>/tvshow.nfo
archive/<source library id>/<series title (year)>/Season 01/season.nfo
archive/<source library id>/<series title (year)>/Season 01/<series> - S01E01.disc
archive/<source library id>/<series title (year)>/Season 01/<series> - S01E01.nfo
```

The history library must be **mixed content** (empty `CollectionType`) to hold movies and episodes together, or `tvshows` for episodes only. Attach the matching `archive/<source id>` path yourself — the plugin writes files, it does not attach library paths. Status reports an unattached mapping instead of claiming the card is indexed.

## Install

1. Stop Emby; back up `plugins/`, `plugins/configurations`, `<data>/emby-history` and the Emby database.
2. Copy **only** `EmbyHistory.dll` into `plugins/`. Do not copy Emby or SkiaSharp assemblies.
3. Start Emby, open **Dashboard → Plugins → Emby History** and confirm the version.
4. Create the target history library and attach the `archive/<source id>` paths you intend to use.
5. Tick source libraries in the plugin settings, choose an identity mode per library and a target history library. Several sources may share one target.
6. Grant the history library to the users who should see it. The plugin does not grant library permissions; adult owner-only visibility must be configured in Emby itself.

## Identity modes

| Mode | Use for | Identity |
|---|---|---|
| `Code` | JAV-style code libraries | Normalised code (FC2, HEYZO, date codes, filename precedence) |
| `Provider` | Movies/series scraped with IMDb/TMDb | Provider ID, falling back to title + year |
| `LocalPath` | Adult libraries without stable IDs | First-seen path, conservative |
| `SeriesEpisode` | TV / anime | Series identity + season + episode |

Identity mode is independent of the Adult/General classification; changing a mode re-keys records instead of inventing removals.

## Folder grouping behaviour

- Eligible when the original directory contained **at least two known works**; the plan uses the deepest non-root directory and never merges different sources or same-named folders from different roots.
- Only **removed** works join the collection; when one member returns to the library it simply leaves the group and the rest stay.
- The collection's audience is recomputed from the history library's permissions on every pass, and rebuilt when that audience changes — a revoked account loses the group immediately.
- Grouping appears in clients that enable **"Group items into collections"** (Emby's per-library view option). The plugin sets only the history library's own view key, never other libraries'.

## Playback progress behaviour

- Captured from Emby's own user-data events plus a periodic sweep; stored in `<data>/emby-history/playback-state.v1.json` (atomic, with a backup, 0600, symlinks refused).
- Restored onto the owned `.disc` history item together with the original runtime, so `PlayedPercentage` and the cover progress bar work natively.
- Per user and per source copy: one account's position never leaks into another, and nothing is painted into the poster.
- If Emby's native playback key for a new copy collides with a saved old state, the plugin **refuses to write and reports an error** rather than overwrite the new copy or another account.
- Only viewing and deletion that happen **after** the upgrade can be preserved; older records without a saved snapshot are not reconstructed.

## Build and test

Copy these assemblies from your Emby install into `sdk-current/` (never redistributed here):
`MediaBrowser.Controller.dll`, `MediaBrowser.Common.dll`, `MediaBrowser.Model.dll`, `SkiaSharp.dll`, plus `Emby.Naming.dll`, `Emby.Media.Model.dll`, `Emby.Extensions.dll` from Emby's `/system`.

```sh
dotnet build Plugin/EmbyHistory.csproj -c Release
dotnet run --project Tests/EmbyHistory.Tests.csproj -c Release            # core, 53
dotnet run --project ArchiveTests/ArchiveTests.csproj -c Release         # filesystem, 24 (mock renderer)
dotnet run --project IndexContractTests/IndexContractTests.csproj -c Release  # real SDK contract, 6
dotnet run --project PlaybackTests -c Release                            # playback service, 36
DOTNET=$(command -v dotnet) python3 RetryTests/run-tests.py               # control flow, 12 + folder 17
```

## Known limitations

- **Access control is Emby's job.** Adult/General classification only prevents mixing audiences in one target library; visibility is `EnabledFolders`. `OwnerOnly` is not an automatic whitelist.
- `FollowSource` is a 10-minute policy sync with revocation delay, bulk-change caps and administrator skipping.
- Emby's per-library search does not cross into the history library; use global search or enter the library.
- Offline history cards have no playable media; some clients still draw a play button.
- Series folders are named `title (year)`; two different series with the same title and year are refused rather than overwritten.
- Multi-episode files (`S01E01-E02`) are not modelled as ranges.
- No formal support across Emby major versions; retest in an isolated instance first.

## License

MIT — see [LICENSE](LICENSE). Emby and its assemblies are not included and remain under their own terms.

---

## 中文

### 功能

- **独立持久账本**：原子写入 + 校验备份，按「源库 + 身份」记录多副本；同身份重新入库会撤卡并保留首次记录时间。
- **电影 / 剧 / 季 / 单集**：最后一份副本离开源库才生成历史卡。
- **整目录删除即时留史**：删除文件夹（含嵌套）时按已保存的逐条身份立即核对，不等周期对账，也不依赖逐片删除事件。
- **原生搜索**：历史库是普通 Emby 库，内容是 `.disc` 占位、NFO 与生成海报；不改数据库、不用浏览器扩展。
- **按原目录收拢**：原始目录内至少两部作品时生成一个原生合集入口 `[历史] 目录名`，成员仍是独立可搜的历史卡；单片目录和库根影片不收拢。
- **保留播放进度**：删除前按账号保存播放位置、已看、播放次数与原片时长，索引完成后写回历史条目，客户端显示原生进度条；进度不画进海报、不跨账号复制。
- **主动增量索引**：写完或撤下归档后只刷新受影响路径并等待完成，不等目录监控、不触发全库扫描。
- **可见状态**：`GET /EmbyHistory/Status` 分账本 / 归档 / 索引 / 播放 / 分组阶段；索引未完成或目录未挂载时显示 `Waiting`，不误报完成。
- **自有文件保护**：按目录 manifest 记录 SHA-256，撤卡只删已验证的自有文件。

### 安装

1. 停 Emby，备份 `plugins/`、`plugins/configurations`、`<数据目录>/emby-history` 与数据库。
2. 只把 `EmbyHistory.dll` 放进 `plugins/`，不要复制 Emby / SkiaSharp 程序集。
3. 启动后在 控制台 → 插件 确认版本。
4. 建立目标历史库并把 `archive/<源库ID>` 路径挂进去（插件不会自动挂载）。
5. 设置页勾选源库、识别方式与目标历史库，保存即生效。
6. 在 Emby 里给账号授予历史库权限；成人内容的「仅我可见」必须由 Emby 权限控制。

### 分组与进度的边界

- 分组只对「原目录内至少两部作品」生效；合集受众随历史库权限重建，撤权立即不可见；客户端需开启「将项目分组到合集中」，插件只改该历史库自己的视图键。
- 进度只覆盖升级之后发生的观看与删除；旧记录没有快照就无法补出。
- Emby 若让新旧条目共用播放记录，插件拒绝写入并报错，不会覆盖新片或他人进度。

### 许可

MIT，见 [LICENSE](LICENSE)。仓库不包含 Emby 程序集。
