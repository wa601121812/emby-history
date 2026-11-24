define([], function () {
    'use strict';

    var pluginId = '82d39a44-2b97-4d7d-8fa6-94b8f7f5aa01';
    var MODES = [
        { value: 'Code', label: '番号' },
        { value: 'Provider', label: '作品 ID' },
        { value: 'LocalPath', label: '本地记录' }
    ];
    var EPISODE_MODE = [{ value: 'SeriesEpisode', label: '剧集（剧+季+集）' }];

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function select(options, value) {
        var node = el('select');
        options.forEach(function (option) {
            var item = el('option', null, option.label);
            item.value = option.value;
            if (option.value === value) item.selected = true;
            node.appendChild(item);
        });
        return node;
    }

    // A history library is recognised by where its folders physically are: everything the plugin
    // writes lives under <data>/emby-history/. Names are free text the user may change at any time,
    // so a rename must not change how a library is treated.
    var HISTORY_PATH_MARKER = '/emby-history';

    function isHistoryLibrary(library) {
        var locations = library.Locations || [];
        for (var i = 0; i < locations.length; i++) {
            var path = String(locations[i] || '').replace(/\\/g, '/').toLowerCase();
            if (path.indexOf(HISTORY_PATH_MARKER) >= 0) return true;
        }
        return false;
    }

    function isVideoLibrary(library) {
        return library.CollectionType === 'movies' || library.CollectionType === 'tvshows';
    }

    // Anything that can physically host history stubs: movies/TV libraries, plus "mixed content"
    // libraries (CollectionType empty), which is what a history library normally is.
    function isTargetLibrary(library) {
        return isHistoryLibrary(library) || isVideoLibrary(library) || !library.CollectionType;
    }

    function EmbyHistoryConfig(view) {
        var page = view;
        var form = page.querySelector('#EmbyHistoryForm');
        var enabled = page.querySelector('#Enabled');
        var allowEmpty = page.querySelector('#AllowEmptyReconcile');
        var rowsHost = page.querySelector('#SourceRows');
        var loading = page.querySelector('#SourceLoading');
        var wrap = page.querySelector('#SourceTableWrap');
        var summary = page.querySelector('#MappingSummary');
        var statusHost = page.querySelector('#HistoryStatus');
        var statusTimer = null;
        var statusVisible = false;
        var statusGeneration = 0;

        function phase(value) {
            return {
                Starting: '启动中', Pending: '等待处理', Idle: '空闲', Events: '处理事件',
                Reconcile: '按库对账', Completed: '已完成', Running: '处理中', Queued: '已排队',
                Held: '安全保护/待重试', Error: '失败，等待重试', Disabled: '已停用',
                NoSources: '未选择源库', Stopped: '已停止', Ledger: '账本', Archive: '归档', Index: '索引'
            }[value] || value || '—';
        }

        function time(value) {
            if (!value) return '尚无';
            var date = new Date(value);
            return isNaN(date.getTime()) ? '—' : date.toLocaleString();
        }

        function renderStatus(state) {
            statusHost.textContent = '';
            statusHost.appendChild(el('div', null, '当前：' + phase(state.Stage) +
                (state.Busy ? ' · 工作中' : '') + ' · 状态更新 ' + time(state.UpdatedUtc)));
            statusHost.appendChild(el('div', null, '账本：' + phase(state.LedgerPhase) +
                ' · 记录 ' + (state.LedgerRecords || 0) + ' · 已移出 ' + (state.RemovedRecords || 0) +
                ' · 待处理事件 ' + (state.PendingEvents || 0) + ' · 本批事件 ' + (state.LedgerEventsProcessed || 0) +
                ' · 最近入账 ' + time(state.LastLedgerUtc)));
            var reason = { PeriodicSafetyReconcile: '定时安全对账', ContainerRemoval: '整剧/整季删除触发的定向对账', EventsOnly: '仅增量事件，无源库遍历' }[state.ReconcileReason] || '等待';
            statusHost.appendChild(el('div', null, reason +
                ' · 本轮库 ' + (state.ReconcileLibrariesCompleted || 0) + '/' + (state.ReconcileLibrariesTotal || 0) +
                ' · 本库条目 ' + (state.ScannedItems || 0) + '/' + (state.ScanTotal == null ? '未知' : state.ScanTotal) +
                ' · 待对账库 ' + (state.PendingReconciles || 0) + ' · 最近事件 ' + time(state.LastEventUtc)));
            statusHost.appendChild(el('div', null, '归档：' + phase(state.ArchivePhase) +
                ' · 本批记录 ' + (state.ArchiveRecords || 0) + ' · 变更源库 ' + (state.ArchiveChangedLibraries || 0) +
                ' · 失败 ' + (state.ArchiveFailures || 0) + ' · 最近写档 ' + time(state.LastArchiveUtc)));
            statusHost.appendChild(el('div', null, '索引：' + phase(state.IndexPhase) +
                ' · 已完成历史目标 ' + (state.IndexTargetsCompleted || 0) + '/' + (state.IndexTargetsTotal || 0) +
                ' · 待刷新目标/映射 ' + (state.PendingIndexTargets || 0) + ' · 最近刷新完成 ' + time(state.LastIndexUtc)));
            [state.LedgerError, state.ArchiveError, state.IndexError].forEach(function (message) {
                if (message) statusHost.appendChild(el('div', 'eh-error', message));
            });
            if (state.LastErrorUtc) statusHost.appendChild(el('div', null, '最近错误/保护记录 ' + time(state.LastErrorUtc)));
        }

        function pollStatus(generation) {
            if (!statusVisible || generation !== statusGeneration) return;
            ApiClient.getJSON(ApiClient.getUrl('EmbyHistory/Status')).then(function (state) {
                if (statusVisible && generation === statusGeneration) renderStatus(state || {});
            }, function () {
                if (statusVisible && generation === statusGeneration) {
                    statusHost.textContent = '读取状态失败（仅管理员可访问）；不会把读取失败当成处理完成。';
                }
            }).then(function () {
                if (statusVisible && generation === statusGeneration) {
                    statusTimer = setTimeout(function () { pollStatus(generation); }, 2000);
                }
            });
        }

        function startStatus() {
            statusVisible = true;
            if (statusTimer) clearTimeout(statusTimer);
            pollStatus(++statusGeneration);
        }

        function stopStatus() {
            statusVisible = false;
            statusGeneration++;
            if (statusTimer) clearTimeout(statusTimer);
            statusTimer = null;
        }

        var libraries = [];
        var rows = [];

        function targets() {
            // Recomputed from the live library list on every page load, so a library created later
            // shows up here without touching this code.
            return libraries.filter(isTargetLibrary).sort(function (a, b) {
                var ah = isHistoryLibrary(a) ? 0 : 1;
                var bh = isHistoryLibrary(b) ? 0 : 1;
                if (ah !== bh) return ah - bh;
                return (a.Name || '').localeCompare(b.Name || '');
            });
        }

        function defaultMode(contentClass) {
            return contentClass === 'General' ? 'Provider' : 'LocalPath';
        }

        function defaultAccess(contentClass) {
            return contentClass === 'General' ? 'FollowSource' : 'OwnerOnly';
        }

        function targetOptions(selectedId) {
            var options = targets().map(function (library) {
                return {
                    value: library.Id,
                    label: (library.Name || library.Id) +
                        (isHistoryLibrary(library) ? '' : (!library.CollectionType ? '（混合内容）' : '（非历史库）'))
                };
            });
            if (selectedId && !options.some(function (o) { return o.value === selectedId; })) {
                options.unshift({ value: selectedId, label: selectedId + '（库不存在）' });
            }
            options.unshift({ value: '', label: '— 请选择 —' });
            return options;
        }

        function makeRow(library, rule) {
            var tr = el('tr');
            tr.dataset.libraryId = library.Id;
            var isSeries = library.CollectionType === 'tvshows';

            var pickCell = el('td');
            var pick = el('input');
            pick.type = 'checkbox';
            pick.className = 'eh-pick';
            pick.checked = !!rule;
            pickCell.appendChild(pick);

            var nameCell = el('td');
            nameCell.appendChild(el('div', 'eh-lib', library.Name || library.Id));
            nameCell.appendChild(el('div', 'eh-sub', isSeries ? '电视剧/动漫 · 按单集留史' : '电影'));

            var targetCell = el('td');
            var target = select(targetOptions(rule ? rule.HistoryLibraryId : ''), rule ? rule.HistoryLibraryId : '');
            target.className = 'eh-target';
            targetCell.appendChild(target);

            var classCell = el('td');
            var cls = select([
                { value: 'Adult', label: '成人' },
                { value: 'General', label: '普通' }
            ], rule ? rule.ContentClass : 'Adult');
            cls.className = 'eh-class';
            classCell.appendChild(cls);

            var modeCell = el('td');
            var mode = select(isSeries ? EPISODE_MODE : MODES,
                isSeries ? 'SeriesEpisode' : (rule ? rule.Mode : defaultMode(rule ? rule.ContentClass : 'Adult')));
            mode.className = 'eh-mode';
            // A series library can only be identified as 剧+季+集; there is nothing to choose.
            mode.disabled = isSeries;
            modeCell.appendChild(mode);

            var accessCell = el('td');
            var access = select([
                { value: 'OwnerOnly', label: '仅我' },
                { value: 'FollowSource', label: '跟随源库' }
            ], rule ? rule.AccessMode : defaultAccess(rule ? rule.ContentClass : 'Adult'));
            access.className = 'eh-access';
            accessCell.appendChild(access);

            [pickCell, nameCell, targetCell, classCell, modeCell, accessCell].forEach(function (cell) {
                tr.appendChild(cell);
            });

            var state = { library: library, pick: pick, target: target, cls: cls, mode: mode, access: access, tr: tr };
            rows.push(state);

            pick.addEventListener('change', function () {
                if (pick.checked && target.value === '') {
                    var suggested = suggestTarget(cls.value, library.Id);
                    if (suggested) target.value = suggested;
                }
                if (pick.checked && access.value === 'FollowSource' && cls.value !== 'General') {
                    access.value = 'OwnerOnly';
                }
                refreshRow(state);
                renderSummary();
            });
            cls.addEventListener('change', function () {
                // Keep mode/access consistent with the class unless the user already diverged.
                if (cls.value === 'General') {
                    if (!isSeries && mode.value === 'LocalPath') mode.value = 'Provider';
                    if (access.value === 'OwnerOnly') access.value = 'FollowSource';
                } else if (access.value === 'FollowSource') {
                    access.value = 'OwnerOnly';
                }
                if (pick.checked) {
                    // Switching class must not leave the row pointed at a library that already
                    // serves the other class: that would mix adult and general history.
                    var clashes = rows.some(function (other) {
                        return other !== state && other.pick.checked &&
                            other.target.value === target.value && other.cls.value !== cls.value;
                    });
                    if (target.value === '' || clashes) {
                        var suggested = suggestTarget(cls.value, library.Id);
                        if (suggested) target.value = suggested;
                    }
                }
                refreshRow(state);
                renderSummary();
            });
            access.addEventListener('change', function () {
                if (access.value === 'FollowSource' && cls.value !== 'General') {
                    cls.value = 'General';
                    if (!isSeries && mode.value === 'LocalPath') mode.value = 'Provider';
                }
                refreshRow(state);
                renderSummary();
            });
            target.addEventListener('change', renderSummary);
            mode.addEventListener('change', renderSummary);

            refreshRow(state);
            return tr;
        }

        function suggestTarget(contentClass, sourceId) {
            // Prefer a target already used by the same class: that keeps "many sources -> one
            // history library" easy without ever mixing adult and general content.
            for (var i = 0; i < rows.length; i++) {
                var row = rows[i];
                if (!row.pick.checked) continue;
                if (row.library.Id === sourceId) continue;
                if (row.cls.value !== contentClass) continue;
                if (row.target.value) return row.target.value;
            }
            // No sibling row to copy from: fall back to the first history library. Class is shown in
            // the row, so a wrong guess is visible and one click to fix; names are not trusted here.
            var fallback = targets().filter(isHistoryLibrary);
            return fallback.length ? fallback[0].Id : '';
        }

        function refreshRow(state) {
            var on = state.pick.checked;
            state.tr.classList.toggle('eh-off', !on);
            state.target.disabled = !on;
            state.cls.disabled = !on;
            state.mode.disabled = !on || state.mode.options.length === 1;
            // Adult history is owner-only by design: the choice stays visible but inert.
            if (on && state.cls.value !== 'General') state.access.value = 'OwnerOnly';
            state.access.disabled = !on || state.cls.value !== 'General';
            state.target.classList.toggle('eh-missing', on && !state.target.value);
        }

        function collected() {
            var result = [];
            rows.forEach(function (row) {
                if (!row.pick.checked) return;
                result.push({
                    LibraryId: row.library.Id,
                    Name: row.library.Name,
                    Mode: row.mode.value,
                    ContentClass: row.cls.value,
                    HistoryLibraryId: row.target.value,
                    AccessMode: row.cls.value === 'General' ? row.access.value : 'OwnerOnly',
                    ExplicitUserIds: [],
                    Enabled: true
                });
            });
            return result;
        }

        function validate(items) {
            var problems = [];
            var names = {};
            libraries.forEach(function (library) { names[library.Id] = library.Name || library.Id; });
            var byTarget = {};
            items.forEach(function (item) {
                if (!item.HistoryLibraryId) {
                    problems.push('“' + (item.Name || item.LibraryId) + '”没有选择归档到哪个历史库。');
                    return;
                }
                if (item.HistoryLibraryId === item.LibraryId) {
                    problems.push('“' + (item.Name || item.LibraryId) + '”不能归档到自己。');
                }
                byTarget[item.HistoryLibraryId] = byTarget[item.HistoryLibraryId] || {};
                byTarget[item.HistoryLibraryId][item.ContentClass] = true;
            });
            Object.keys(byTarget).forEach(function (targetId) {
                var classes = Object.keys(byTarget[targetId]);
                if (classes.length > 1) {
                    problems.push('“' + (names[targetId] || targetId) + '”同时接收普通和成人内容，会让成人历史暴露给普通库用户，请拆成两个历史库。');
                }
            });
            items.forEach(function (item) {
                var target = null;
                libraries.forEach(function (library) { if (library.Id === item.HistoryLibraryId) target = library; });
                if (!target || isHistoryLibrary(target)) return;   // a history library may be mixed content
                var targetIsSeriesOnly = target.CollectionType === 'tvshows';
                var targetIsMoviesOnly = target.CollectionType === 'movies';
                if (item.Mode === 'SeriesEpisode' && targetIsMoviesOnly) {
                    problems.push('“' + (names[item.HistoryLibraryId] || item.HistoryLibraryId) + '”是电影库，装不了剧集卡片；剧集历史要放进混合内容或电视剧库。');
                }
                if (item.Mode !== 'SeriesEpisode' && targetIsSeriesOnly) {
                    problems.push('“' + (names[item.HistoryLibraryId] || item.HistoryLibraryId) + '”是电视剧库，装不了电影卡片。');
                }
            });
            items.forEach(function (item) {
                var clash = items.some(function (other) {
                    return other.LibraryId === item.HistoryLibraryId;
                });
                if (clash) {
                    problems.push('“' + (names[item.HistoryLibraryId] || item.HistoryLibraryId) + '”本身是被监控的源库，不能同时当历史库。');
                }
            });
            return problems.filter(function (value, index, all) { return all.indexOf(value) === index; });
        }

        function renderSummary() {
            var items = collected();
            summary.textContent = '';
            if (!items.length) {
                summary.appendChild(el('div', null, '当前没有勾选任何源库，保存后将停止记录。'));
                return;
            }
            var grouped = {};
            items.forEach(function (item) {
                var key = item.HistoryLibraryId || '';
                grouped[key] = grouped[key] || [];
                grouped[key].push(item);
            });
            Object.keys(grouped).forEach(function (key) {
                var group = grouped[key];
                var targetName = '';
                libraries.forEach(function (library) { if (library.Id === key) targetName = library.Name; });
                var names = group.map(function (item) { return item.Name || item.LibraryId; }).join('、');
                var adultOnly = group.every(function (item) { return item.ContentClass !== 'General'; });
                summary.appendChild(el('div', null,
                    (targetName || '未选择历史库') + ' ← ' + names +
                    (key ? (adultOnly ? '（仅你可见）' : '（跟随各源库权限）') : '')));
            });
        }

        function render(configSources) {
            rows = [];
            rowsHost.textContent = '';
            loading.style.display = 'none';
            wrap.style.display = '';

            var byId = {};
            (configSources || []).forEach(function (rule) { byId[rule.LibraryId] = rule; });

            var candidates = libraries.filter(function (library) {
                return isVideoLibrary(library) && !isHistoryLibrary(library);
            }).sort(function (a, b) { return (a.Name || '').localeCompare(b.Name || ''); });

            candidates.forEach(function (library) {
                rowsHost.appendChild(makeRow(library, byId[library.Id]));
            });

            // A configured library that no longer exists must not silently disappear.
            (configSources || []).forEach(function (rule) {
                var exists = candidates.some(function (library) { return library.Id === rule.LibraryId; });
                if (exists) return;
                var ghost = el('tr');
                ghost.dataset.libraryId = rule.LibraryId;
                var cell = el('td', 'eh-error', '该库已不存在：' + rule.LibraryId + '（保存时会移除）');
                cell.colSpan = 6;
                ghost.appendChild(cell);
                rowsHost.appendChild(ghost);
            });
            renderSummary();
        }

        function load() {
            loading.style.display = '';
            wrap.style.display = 'none';
            Dashboard.showLoadingMsg();
            Promise.all([
                ApiClient.getPluginConfiguration(pluginId),
                ApiClient.getJSON(ApiClient.getUrl('Library/VirtualFolders'))
            ]).then(function (results) {
                var config = results[0] || {};
                // VirtualFolderInfo exposes both ItemId and Id; normalise once so every later
                // comparison uses the same value.
                libraries = (results[1] || []).map(function (library) {
                    return {
                        Id: library.ItemId || library.Id,
                        Name: library.Name,
                        CollectionType: library.CollectionType,
                        // Needed to recognise history libraries by their archive path, not by name.
                        Locations: library.Locations || []
                    };
                });
                enabled.checked = config.Enabled === true;
                allowEmpty.checked = config.AllowEmptyLibraryReconcile === true;
                var sources = (config.Sources || []).slice();
                if (!sources.length && (config.SourceLibraryIds || []).length) {
                    // Legacy configuration: show it as monitored, but mapping must be completed.
                    sources = config.SourceLibraryIds.map(function (id) {
                        return { LibraryId: id, Mode: 'Code', ContentClass: 'Adult', HistoryLibraryId: '', AccessMode: 'OwnerOnly' };
                    });
                }
                render(sources);
                Dashboard.hideLoadingMsg();
            }, function () {
                Dashboard.hideLoadingMsg();
                loading.textContent = '读取媒体库或插件配置失败，请刷新页面重试。';
            });
        }

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var items = collected();
            var problems = validate(items);
            if (problems.length) {
                Dashboard.alert(problems.join('\n'));
                return false;
            }
            Dashboard.showLoadingMsg();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.Enabled = enabled.checked;
                config.AllowEmptyLibraryReconcile = allowEmpty.checked;
                config.Sources = items.map(function (item) {
                    return {
                        LibraryId: item.LibraryId,
                        Mode: item.Mode,
                        ContentClass: item.ContentClass,
                        HistoryLibraryId: item.HistoryLibraryId,
                        AccessMode: item.AccessMode,
                        ExplicitUserIds: [],
                        Enabled: true
                    };
                });
                // The new page owns the source list; clear the legacy field so an empty selection
                // really means "monitor nothing" instead of falling back to the old ids.
                config.SourceLibraryIds = [];
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            }, function () {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('保存插件配置失败。');
            });
            return false;
        });

        page.addEventListener('viewshow', load);
        page.addEventListener('viewshow', startStatus);
        page.addEventListener('viewhide', stopStatus);
        page.addEventListener('viewdestroy', stopStatus);
    }

    return EmbyHistoryConfig;
});
