(function () {
    'use strict';

    var PLUGIN_ID = 'd3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c';

    var form = document.getElementById('jellyplayConfigForm');
    if (!form) {
        return;
    }

    // Scalar paths of PluginConfiguration surfaced on the form.
    var fields = {
        NewMediaEnabled: 'Events.NewMediaEnabled',
        NewMediaGroupingSeconds: 'Events.NewMediaGroupingSeconds',
        NewMediaAudience: 'Events.NewMediaAudience',
        SeerrServerUrl: 'Seerr.ServerUrl',
        SeerrApiKey: 'Seerr.ApiKey',
        WebhookSecret: 'Seerr.WebhookSecret',
        MdbListApiKey: 'Ratings.MdbListApiKey',
        TmdbApiKey: 'Ratings.TmdbApiKey',
        SmtpHost: 'Newsletter.SmtpHost',
        SmtpPort: 'Newsletter.SmtpPort',
        SmtpUsername: 'Newsletter.SmtpUsername',
        SmtpPassword: 'Newsletter.SmtpPassword',
        FromAddress: 'Newsletter.FromAddress',
        TestRecipient: 'Newsletter.TestRecipient'
    };

    var strings = {};

    // ── api plumbing ──

    function authHeaders(extra) {
        var token = window.ApiClient.accessToken();
        var auth = 'MediaBrowser Client="JellyPlay Dashboard", Device="Dashboard", DeviceId="jellyplay-dashboard", Version="1.0", Token="' + token + '"';
        var headers = { 'Authorization': auth, 'X-Emby-Authorization': auth };
        for (var key in (extra || {})) { headers[key] = extra[key]; }
        return headers;
    }

    function apiGet(path) {
        return fetch(window.ApiClient.getUrl(path), { headers: authHeaders() }).then(function (response) {
            if (!response.ok) { throw new Error(String(response.status)); }
            return response.json();
        });
    }

    function apiPost(path, body) {
        return fetch(window.ApiClient.getUrl(path), {
            method: 'POST',
            headers: authHeaders({ 'Content-Type': 'application/json' }),
            body: body === undefined ? undefined : JSON.stringify(body)
        }).then(function (response) {
            if (!response.ok) { throw new Error(String(response.status)); }
            return response.status === 204 ? null : response.json();
        });
    }

    function fmt(key, fallback) {
        var text = strings[key] || fallback;
        for (var i = 1; i < arguments.length; i++) {
            text = String(text).replace('{' + (i - 1) + '}', String(arguments[i]));
        }
        return text;
    }

    // fmt's argument convention substitutes the fallback into {0}; use this
    // variant when the message itself carries {n} placeholders:
    // alertFmt(key, fallbackWithPlaceholders, value0, value1, ...).
    function alertFmt() {
        var args = Array.prototype.slice.call(arguments);
        var text = strings[args[0]] || args[1];
        for (var i = 2; i < args.length; i++) {
            text = String(text).replace('{' + (i - 2) + '}', String(args[i]));
        }
        window.Dashboard.alert(text);
    }

    function alertText(key, fallback) {
        window.Dashboard.alert(fmt.apply(null, arguments));
    }

    // Every config write round-trips the WHOLE config object, so concurrent
    // saves would silently drop each other's section (last write wins).
    // Serialize them: each queued job starts only after the previous write
    // (and its post-save reload) has settled.
    var configWriteChain = Promise.resolve();
    function enqueueConfigWrite(job) {
        var run = function () { return job(); };
        configWriteChain = configWriteChain.then(run, run);
        return configWriteChain;
    }

    // ── i18n ──

    function applyStrings() {
        return apiGet('jellyplay/dashboard-strings?lang=' + encodeURIComponent(navigator.language || '')).then(function (table) {
            strings = table || {};
            Array.prototype.forEach.call(document.querySelectorAll('[data-i18n]'), function (element) {
                var value = strings[element.getAttribute('data-i18n')];
                if (typeof value === 'string' && value.length > 0) { element.textContent = value; }
            });
        }).catch(function () {
            // The static en markup stands on its own; localization is cosmetic.
        });
    }

    // ── scalar config form ──

    function getPath(obj, path) {
        return path.split('.').reduce(function (acc, key) { return acc ? acc[key] : undefined; }, obj);
    }

    function setPath(obj, path, value) {
        var parts = path.split('.');
        var cursor = obj;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!cursor[parts[i]]) { cursor[parts[i]] = {}; }
            cursor = cursor[parts[i]];
        }
        cursor[parts[parts.length - 1]] = value;
    }

    function load() {
        window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            Object.keys(fields).forEach(function (id) {
                var element = document.getElementById(id);
                if (!element) { return; }
                var value = getPath(config, fields[id]);
                if (element.type === 'checkbox') {
                    element.checked = value === true;
                } else if (value !== undefined && value !== null) {
                    element.value = value;
                }
            });
        });
    }

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        enqueueConfigWrite(function () {
            return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                Object.keys(fields).forEach(function (id) {
                    var element = document.getElementById(id);
                    if (!element) { return; }
                    var value = element.type === 'checkbox' ? element.checked : element.value;
                    setPath(config, fields[id], element.type === 'number' ? Number(value) : value);
                });
                return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                    alertText('MsgSaved', 'Settings saved.');
                    // The server may have filled in a generated webhook secret —
                    // re-read the config so the form shows the stored value.
                    load();
                });
            });
        });
        return false;
    });

    // ── editor row scaffolding ──
    // Shared by the defaults/rows/anime editors. Fields are labeled and flex
    // based; emby-input/emby-select CSS forces width:100%, so every field is
    // wrapped in a .jellyplay-field that owns the sizing and the control
    // fills it. Rows wrap on narrow viewports instead of overflowing.

    function ensureRowStyles() {
        if (document.getElementById('jellyplayRowStyles')) { return; }
        var style = document.createElement('style');
        style.id = 'jellyplayRowStyles';
        style.textContent = [
            '.jellyplay-rows { margin-bottom: .5em; }',
            '.jellyplay-row { display:flex; flex-wrap:wrap; gap:10px 14px; align-items:flex-end; margin:0 0 12px; padding:12px 14px; background:rgba(255,255,255,.05); border-radius:.35em; }',
            '.jellyplay-field { display:flex; flex-direction:column; gap:4px; flex:1 1 190px; min-width:150px; }',
            '.jellyplay-field.narrow { flex:0 1 140px; min-width:110px; }',
            '.jellyplay-field.fixed { flex:0 0 110px; min-width:0; }',
            '.jellyplay-field-label { font-size:.82em; font-weight:500; color:rgba(255,255,255,.65); padding-left:2px; }',
            '.jellyplay-field .emby-input, .jellyplay-field .emby-select { width:100%; min-width:0; flex:1 1 auto; }',
            '.jellyplay-row .jellyplay-remove { flex:0 0 auto; margin:0 0 2px; }',
            '@media (max-width:40em) { .jellyplay-field, .jellyplay-field.narrow { flex-basis:100%; } .jellyplay-field.fixed { flex:1 1 40%; } }'
        ].join('\n');
        document.head.appendChild(style);
    }

    function fieldLabel(text) {
        var label = document.createElement('label');
        label.className = 'jellyplay-field-label';
        label.textContent = text;
        return label;
    }

    function fieldControl(tag, className, placeholder) {
        var control = document.createElement(tag);
        control.className = className;
        if (placeholder) { control.placeholder = placeholder; }
        return control;
    }

    // Adds <div class="jellyplay-field [narrow|fixed]"><label/><control/></div>.
    // Controls stay in DOM order, which collect*() rely on.
    function appendField(row, labelText, options) {
        var wrap = document.createElement('div');
        wrap.className = 'jellyplay-field' + (options.variant ? ' ' + options.variant : '');
        wrap.appendChild(fieldLabel(labelText));
        var control = fieldControl(options.tag, options.className, options.placeholder);
        if (options.type) { control.type = options.type; }
        if (options.min !== undefined) { control.min = options.min; }
        if (options.value !== undefined && options.value !== null) { control.value = options.value; }
        wrap.appendChild(control);
        row.appendChild(wrap);
        return control;
    }

    function removeButton(row, onChange, textKey) {
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'raised emby-button jellyplay-remove';
        button.textContent = strings[textKey] || strings.DefaultsRemove || 'Remove';
        button.addEventListener('click', function () { row.remove(); onChange(); });
        row.appendChild(button);
        return button;
    }

    // ── tri-state client defaults ──

    var storedDefaults = {};

    function defaultsList() { return document.getElementById('jellyplayDefaultsList'); }
    function defaultsEmptyLabel() { return document.getElementById('jellyplayDefaultsEmpty'); }

    function formatValue(value) {
        return typeof value === 'string' ? value : JSON.stringify(value);
    }

    function parseValue(raw) {
        var text = raw.trim();
        if (text.length === 0) { return null; }
        try {
            return JSON.parse(text);
        } catch (ignored) {
            // A plain word ("dark") is a legitimate string value; anything
            // that LOOKS like structured JSON but fails to parse is a typo —
            // keep it as a string but tell the admin.
            if (/^[\[{]/.test(text)) {
                alertFmt('DefaultsInvalidJson', '"{0}" is not valid JSON — saved as a plain string.', text);
            }
            return text;
        }
    }

    function addDefaultRow(key, entry) {
        ensureRowStyles();
        var row = document.createElement('div');
        row.className = 'jellyplay-row';

        appendField(row, strings.DefaultsColumnKey || 'Key', {
            tag: 'input', className: 'emby-input', placeholder: 'namespace/key', value: key || ''
        });

        var modeSelect = appendField(row, strings.DefaultsColumnMode || 'Mode', {
            tag: 'select', className: 'emby-select', variant: 'narrow'
        });
        [['suggested', 'DefaultsModeSuggested'], ['forced', 'DefaultsModeForced']].forEach(function (pair) {
            var option = document.createElement('option');
            option.value = pair[0];
            option.textContent = strings[pair[1]] || pair[0];
            modeSelect.appendChild(option);
        });
        modeSelect.value = (entry && entry.mode === 'forced') ? 'forced' : 'suggested';

        appendField(row, strings.DefaultsColumnValue || 'Value (JSON)', {
            tag: 'input', className: 'emby-input', placeholder: 'value (JSON)',
            value: entry && entry.value !== undefined ? formatValue(entry.value) : undefined
        });

        removeButton(row, refreshDefaultsEmptyLabel);
        defaultsList().appendChild(row);
        refreshDefaultsEmptyLabel();
    }

    function refreshDefaultsEmptyLabel() {
        var empty = defaultsList().children.length === 0;
        defaultsEmptyLabel().style.display = empty ? '' : 'none';
    }

    function collectDefaults() {
        var map = {};
        Array.prototype.forEach.call(defaultsList().children, function (row) {
            var inputs = row.querySelectorAll('input, select');
            var key = inputs[0].value.trim();
            if (key.length === 0) { return; }
            var value = parseValue(inputs[2].value);
            if (value === null) { return; }
            map[key] = { mode: inputs[1].value, value: value };
        });
        return map;
    }

    function loadDefaults() {
        return apiGet('jellyplay/defaults').then(function (map) {
            storedDefaults = map || {};
            defaultsList().innerHTML = '';
            Object.keys(storedDefaults).forEach(function (key) {
                addDefaultRow(key, storedDefaults[key]);
            });
            refreshDefaultsEmptyLabel();
        });
    }

    document.getElementById('jellyplayDefaultsAdd').addEventListener('click', function () {
        addDefaultRow('', { mode: 'suggested' });
    });

    document.getElementById('jellyplayDefaultsSave').addEventListener('click', function () {
        apiPost('jellyplay/defaults', collectDefaults()).then(function () {
            alertText('DefaultsSaved', 'Defaults saved.');
            return loadDefaults();
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
        });
    });

    document.getElementById('jellyplayDefaultsPush').addEventListener('click', function () {
        apiPost('jellyplay/admin/pushDefaults').then(function (outcome) {
            window.Dashboard.alert((strings.DefaultsPushed || 'Pushed {0} key(s) to {1} user(s).')
                .replace('{0}', outcome && outcome.keysPushed !== undefined ? outcome.keysPushed : '?')
                .replace('{1}', outcome && outcome.users !== undefined ? outcome.users : '?'));
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
        });
    });

    // ── custom rows ──

    // One entry per supported row source (must match RowsServices' switch).
    // needsId drives the list-id field's visibility; hintKey/hint is its
    // placeholder (resx first, static en fallback).
    var ROW_SOURCES = [
        { value: 'letterboxd', needsId: true, hintKey: 'CustomRowsListIdLetterboxd', hint: 'Letterboxd list slug (e.g. staffpicks)' },
        { value: 'imdb', needsId: true, hintKey: 'CustomRowsListIdImdb', hint: 'IMDb list id (ls…) or chart name' },
        { value: 'mdblist', needsId: true, hintKey: 'CustomRowsListIdMdblist', hint: 'MDBList list id' },
        { value: 'tmdb', needsId: true, hintKey: 'CustomRowsListIdTmdb', hint: 'TMDB list id (numeric)' }
    ];

    function rowsList() { return document.getElementById('jellyplayRowsList'); }
    function rowsEmptyLabel() { return document.getElementById('jellyplayRowsEmpty'); }

    function sourceInfo(value) {
        var match = null;
        ROW_SOURCES.forEach(function (candidate) {
            if (candidate.value === value) { match = candidate; }
        });
        return match;
    }

    function applyListIdVisibility(row) {
        var info = sourceInfo(row.querySelector('select').value) || ROW_SOURCES[0];
        var listIdInput = row.querySelector('input[data-role="row-listid"]');
        var field = listIdInput.parentNode;
        field.style.display = info.needsId ? '' : 'none';
        listIdInput.placeholder = strings[info.hintKey] || info.hint;
    }

    function addRowRow(definition) {
        ensureRowStyles();
        var row = document.createElement('div');
        row.className = 'jellyplay-row';

        appendField(row, strings.CustomRowsTitle || 'Title', {
            tag: 'input', className: 'emby-input', placeholder: strings.CustomRowsTitle || 'Title',
            value: (definition && definition.Title) || ''
        });

        var sourceSelect = appendField(row, strings.CustomRowsColumnSource || 'Source', {
            tag: 'select', className: 'emby-select', variant: 'narrow'
        });
        ROW_SOURCES.forEach(function (source) {
            var option = document.createElement('option');
            option.value = source.value;
            option.textContent = source.value;
            sourceSelect.appendChild(option);
        });
        var storedSource = (definition && definition.Source) || 'letterboxd';
        sourceSelect.value = sourceInfo(storedSource) ? storedSource : 'letterboxd';

        appendField(row, strings.CustomRowsLimit || 'Limit', {
            tag: 'input', className: 'emby-input', type: 'number', min: '1', variant: 'fixed',
            placeholder: strings.CustomRowsLimit || 'Limit',
            value: definition && definition.Limit !== undefined ? definition.Limit : 20
        });

        var listIdInput = appendField(row, strings.CustomRowsColumnListId || 'List ID', {
            tag: 'input', className: 'emby-input',
            placeholder: strings.CustomRowsListIdLetterboxd || 'List id',
            value: (definition && definition.ListId) || ''
        });
        listIdInput.setAttribute('data-role', 'row-listid');

        removeButton(row, refreshRowsEmptyLabel, 'CustomRowsRemove');

        sourceSelect.addEventListener('change', function () { applyListIdVisibility(row); });
        rowsList().appendChild(row);
        applyListIdVisibility(row);
        refreshRowsEmptyLabel();
    }

    function refreshRowsEmptyLabel() {
        var empty = rowsList().children.length === 0;
        rowsEmptyLabel().style.display = empty ? '' : 'none';
    }

    // Collects and validates the editor rows. Fully blank rows are dropped
    // silently; a row with content but no title, or a limit below 1, aborts
    // the whole save (nothing is written half-way).
    function collectRows() {
        var rows = [];
        var children = rowsList().children;
        for (var i = 0; i < children.length; i++) {
            var inputs = children[i].querySelectorAll('input, select');
            var title = inputs[0].value.trim();
            var source = inputs[1].value;
            var limit = Number(inputs[2].value);
            var listId = inputs[3].value.trim();

            if (title.length === 0 && listId.length === 0 && inputs[2].value === '') {
                continue;
            }

            if (title.length === 0) {
                alertFmt('CustomRowsInvalidTitle', 'Every row needs a non-empty title.');
                return null;
            }

            if (!(limit >= 1)) {
                alertFmt('CustomRowsInvalidLimit', 'Row "{0}" needs a limit of at least 1.', title);
                return null;
            }

            rows.push({ Title: title, Source: source, ListId: listId, Limit: Math.floor(limit) });
        }

        return rows;
    }

    function loadRows() {
        return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            rowsList().innerHTML = '';
            var rows = (config && config.Rows && config.Rows.CustomRows) || [];
            rows.forEach(function (row) {
                addRowRow(row);
            });
            refreshRowsEmptyLabel();
        });
    }

    document.getElementById('jellyplayRowsAdd').addEventListener('click', function () {
        addRowRow(null);
    });

    document.getElementById('jellyplayRowsSave').addEventListener('click', function () {
        var rows = collectRows();
        if (rows === null) { return; }
        enqueueConfigWrite(function () {
            // Round-trip the WHOLE config object: unrelated sections survive.
            return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                if (!config.Rows) { config.Rows = {}; }
                config.Rows.CustomRows = rows;
                return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                    alertText('CustomRowsSaved', 'Custom rows saved.');
                    loadRows();
                });
            }).catch(function (error) {
                alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
            });
        });
    });

    // ── anime series overrides ──

    function animeOverridesList() { return document.getElementById('jellyplayAnimeOverridesList'); }
    function animeOverridesEmptyLabel() { return document.getElementById('jellyplayAnimeOverridesEmpty'); }

    function addAnimeOverrideRow(entry) {
        ensureRowStyles();
        var row = document.createElement('div');
        row.className = 'jellyplay-row';

        appendField(row, strings.AnimeOverridesSeriesId || 'Series id', {
            tag: 'input', className: 'emby-input', placeholder: strings.AnimeOverridesSeriesId || 'Series id (Jellyfin item id)',
            value: (entry && entry.SeriesId) || ''
        });

        var aniListInput = appendField(row, strings.AnimeOverridesAniListId || 'AniList id', {
            tag: 'input', className: 'emby-input', variant: 'narrow',
            placeholder: strings.AnimeOverridesAniListId || 'AniList id',
            value: (entry && entry.AniListId) || ''
        });
        aniListInput.setAttribute('data-role', 'override-anilist');

        var malInput = appendField(row, strings.AnimeOverridesMalId || 'MAL id', {
            tag: 'input', className: 'emby-input', variant: 'narrow',
            placeholder: strings.AnimeOverridesMalId || 'MAL id',
            value: (entry && entry.MalId) || ''
        });
        malInput.setAttribute('data-role', 'override-mal');

        appendField(row, strings.AnimeOverridesLabel || 'Label', {
            tag: 'input', className: 'emby-input', placeholder: strings.AnimeOverridesLabel || 'Label (optional)',
            value: (entry && entry.Label) || ''
        });

        removeButton(row, refreshAnimeOverridesEmptyLabel, 'AnimeOverridesRemove');
        animeOverridesList().appendChild(row);
        refreshAnimeOverridesEmptyLabel();
    }

    function refreshAnimeOverridesEmptyLabel() {
        var empty = animeOverridesList().children.length === 0;
        animeOverridesEmptyLabel().style.display = empty ? '' : 'none';
    }

    // Collects and validates the editor rows. Fully blank rows are dropped
    // silently; a row with content but no series id, or no provider id at
    // all, aborts the whole save (nothing is written half-way).
    function collectAnimeOverrides() {
        var overrides = [];
        var children = animeOverridesList().children;
        for (var i = 0; i < children.length; i++) {
            var inputs = children[i].querySelectorAll('input');
            var seriesId = inputs[0].value.trim();
            var aniListId = inputs[1].value.trim();
            var malId = inputs[2].value.trim();
            var label = inputs[3].value.trim();

            if (seriesId.length === 0 && aniListId.length === 0 && malId.length === 0 && label.length === 0) {
                continue;
            }

            if (seriesId.length === 0) {
                alertFmt('AnimeOverridesInvalidSeries', 'Every override needs a non-empty series id.');
                return null;
            }

            if (aniListId.length === 0 && malId.length === 0) {
                alertFmt('AnimeOverridesInvalidProvider', 'Override "{0}" needs an AniList id or a MAL id.', seriesId);
                return null;
            }

            var entry = { SeriesId: seriesId };
            if (aniListId.length > 0) { entry.AniListId = aniListId; }
            if (malId.length > 0) { entry.MalId = malId; }
            if (label.length > 0) { entry.Label = label; }
            overrides.push(entry);
        }

        return overrides;
    }

    function loadAnimeOverrides() {
        return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            animeOverridesList().innerHTML = '';
            var overrides = (config && config.Anime && config.Anime.SeriesOverrides) || [];
            overrides.forEach(function (entry) {
                addAnimeOverrideRow(entry);
            });
            refreshAnimeOverridesEmptyLabel();
        });
    }

    document.getElementById('jellyplayAnimeOverridesAdd').addEventListener('click', function () {
        addAnimeOverrideRow(null);
    });

    document.getElementById('jellyplayAnimeOverridesSave').addEventListener('click', function () {
        var overrides = collectAnimeOverrides();
        if (overrides === null) { return; }
        enqueueConfigWrite(function () {
            // Round-trip the WHOLE config object: unrelated sections survive; only
            // Anime.SeriesOverrides is touched.
            return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                if (!config.Anime) { config.Anime = {}; }
                config.Anime.SeriesOverrides = overrides;
                return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                    alertText('AnimeOverridesSaved', 'Anime overrides saved.');
                    loadAnimeOverrides();
                });
            }).catch(function (error) {
                alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
            });
        });
    });

    // ── backup / restore ──

    document.getElementById('jellyplayBackupDownload').addEventListener('click', function () {
        fetch(window.ApiClient.getUrl('jellyplay/admin/configBackup'), { headers: authHeaders() }).then(function (response) {
            if (!response.ok) { throw new Error(String(response.status)); }
            return response.blob();
        }).then(function (blob) {
            var link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = 'jellyplay-backup-' + new Date().toISOString().slice(0, 10) + '.json';
            link.click();
            URL.revokeObjectURL(link.href);
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
        });
    });

    document.getElementById('jellyplayBackupRestore').addEventListener('click', function () {
        document.getElementById('jellyplayBackupFile').click();
    });

    document.getElementById('jellyplayBackupFile').addEventListener('change', function (event) {
        var file = event.target.files && event.target.files[0];
        if (!file) { return; }
        file.text().then(function (text) {
            return apiPost('jellyplay/admin/configBackup', JSON.parse(text));
        }).then(function (outcome) {
            if (outcome && outcome.success === false) {
                window.Dashboard.alert((strings.BackupFailed || 'Restore failed: {0}').replace('{0}', outcome.error || ''));
                return;
            }
            alertText('BackupRestored', 'Backup restored.');
        }).catch(function (error) {
            window.Dashboard.alert((strings.BackupFailed || 'Restore failed: {0}').replace('{0}', error.message || error));
        }).finally(function () {
            event.target.value = '';
        });
    });

    applyStrings().then(function () {
        load();
        loadDefaults();
        loadRows();
        loadAnimeOverrides();
    });
})();
