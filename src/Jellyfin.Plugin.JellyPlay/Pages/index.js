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
        window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            Object.keys(fields).forEach(function (id) {
                var element = document.getElementById(id);
                if (!element) { return; }
                var value = element.type === 'checkbox' ? element.checked : element.value;
                setPath(config, fields[id], element.type === 'number' ? Number(value) : value);
            });
            window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                alertText('MsgSaved', 'Settings saved.');
                // The server may have filled in a generated webhook secret —
                // re-read the config so the form shows the stored value.
                load();
            });
        });
        return false;
    });

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
        var row = document.createElement('div');
        row.className = 'defaultsRow';
        row.style.cssText = 'display:flex;gap:8px;align-items:center;margin-bottom:6px';

        var keyInput = document.createElement('input');
        keyInput.type = 'text';
        keyInput.className = 'emby-input';
        keyInput.placeholder = 'namespace/key';
        keyInput.value = key || '';
        keyInput.style.flex = '2 1 auto';

        var modeSelect = document.createElement('select');
        modeSelect.className = 'emby-select';
        modeSelect.style.flex = '0 0 auto';
        [['suggested', 'DefaultsModeSuggested'], ['forced', 'DefaultsModeForced']].forEach(function (pair) {
            var option = document.createElement('option');
            option.value = pair[0];
            option.textContent = strings[pair[1]] || pair[0];
            modeSelect.appendChild(option);
        });
        modeSelect.value = (entry && entry.mode === 'forced') ? 'forced' : 'suggested';

        var valueInput = document.createElement('input');
        valueInput.type = 'text';
        valueInput.className = 'emby-input';
        valueInput.placeholder = 'value (JSON)';
        valueInput.value = entry && entry.value !== undefined ? formatValue(entry.value) : '';
        valueInput.style.flex = '2 1 auto';

        var removeButton = document.createElement('button');
        removeButton.type = 'button';
        removeButton.className = 'emby-button';
        removeButton.textContent = strings.DefaultsRemove || 'Remove';
        removeButton.addEventListener('click', function () { row.remove(); refreshDefaultsEmptyLabel(); });

        row.appendChild(keyInput);
        row.appendChild(modeSelect);
        row.appendChild(valueInput);
        row.appendChild(removeButton);
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
        listIdInput.style.display = info.needsId ? '' : 'none';
        listIdInput.placeholder = strings[info.hintKey] || info.hint;
    }

    function addRowRow(definition) {
        var row = document.createElement('div');
        row.className = 'customRowRow';
        row.style.cssText = 'display:flex;gap:8px;align-items:center;margin-bottom:6px';

        var titleInput = document.createElement('input');
        titleInput.type = 'text';
        titleInput.className = 'emby-input';
        titleInput.placeholder = strings.CustomRowsTitle || 'Title';
        titleInput.value = (definition && definition.Title) || '';
        titleInput.style.flex = '2 1 auto';

        var sourceSelect = document.createElement('select');
        sourceSelect.className = 'emby-select';
        sourceSelect.style.flex = '0 0 auto';
        ROW_SOURCES.forEach(function (source) {
            var option = document.createElement('option');
            option.value = source.value;
            option.textContent = source.value;
            sourceSelect.appendChild(option);
        });
        var storedSource = (definition && definition.Source) || 'letterboxd';
        sourceSelect.value = sourceInfo(storedSource) ? storedSource : 'letterboxd';

        var limitInput = document.createElement('input');
        limitInput.type = 'number';
        limitInput.min = '1';
        limitInput.className = 'emby-input';
        limitInput.placeholder = strings.CustomRowsLimit || 'Limit';
        limitInput.value = definition && definition.Limit !== undefined ? definition.Limit : 20;
        limitInput.style.flex = '0 0 90px';

        var listIdInput = document.createElement('input');
        listIdInput.type = 'text';
        listIdInput.className = 'emby-input';
        listIdInput.setAttribute('data-role', 'row-listid');
        listIdInput.value = (definition && definition.ListId) || '';
        listIdInput.style.flex = '2 1 auto';

        var removeButton = document.createElement('button');
        removeButton.type = 'button';
        removeButton.className = 'emby-button';
        removeButton.textContent = strings.CustomRowsRemove || 'Remove';
        removeButton.addEventListener('click', function () { row.remove(); refreshRowsEmptyLabel(); });

        sourceSelect.addEventListener('change', function () { applyListIdVisibility(row); });

        row.appendChild(titleInput);
        row.appendChild(sourceSelect);
        row.appendChild(limitInput);
        row.appendChild(listIdInput);
        row.appendChild(removeButton);
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
        // Round-trip the WHOLE config object: unrelated sections survive.
        window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            if (!config.Rows) { config.Rows = {}; }
            config.Rows.CustomRows = rows;
            window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                alertText('CustomRowsSaved', 'Custom rows saved.');
                loadRows();
            });
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
        });
    });

    // ── anime series overrides ──

    function animeOverridesList() { return document.getElementById('jellyplayAnimeOverridesList'); }
    function animeOverridesEmptyLabel() { return document.getElementById('jellyplayAnimeOverridesEmpty'); }

    function addAnimeOverrideRow(entry) {
        var row = document.createElement('div');
        row.className = 'animeOverrideRow';
        row.style.cssText = 'display:flex;gap:8px;align-items:center;margin-bottom:6px';

        var seriesIdInput = document.createElement('input');
        seriesIdInput.type = 'text';
        seriesIdInput.className = 'emby-input';
        seriesIdInput.placeholder = strings.AnimeOverridesSeriesId || 'Series id (Jellyfin item id)';
        seriesIdInput.value = (entry && entry.SeriesId) || '';
        seriesIdInput.style.flex = '2 1 auto';

        var aniListInput = document.createElement('input');
        aniListInput.type = 'text';
        aniListInput.className = 'emby-input';
        aniListInput.setAttribute('data-role', 'override-anilist');
        aniListInput.placeholder = strings.AnimeOverridesAniListId || 'AniList id';
        aniListInput.value = (entry && entry.AniListId) || '';
        aniListInput.style.flex = '1 1 auto';

        var malInput = document.createElement('input');
        malInput.type = 'text';
        malInput.className = 'emby-input';
        malInput.setAttribute('data-role', 'override-mal');
        malInput.placeholder = strings.AnimeOverridesMalId || 'MAL id';
        malInput.value = (entry && entry.MalId) || '';
        malInput.style.flex = '1 1 auto';

        var labelInput = document.createElement('input');
        labelInput.type = 'text';
        labelInput.className = 'emby-input';
        labelInput.placeholder = strings.AnimeOverridesLabel || 'Label (optional)';
        labelInput.value = (entry && entry.Label) || '';
        labelInput.style.flex = '2 1 auto';

        var removeButton = document.createElement('button');
        removeButton.type = 'button';
        removeButton.className = 'emby-button';
        removeButton.textContent = strings.AnimeOverridesRemove || 'Remove';
        removeButton.addEventListener('click', function () { row.remove(); refreshAnimeOverridesEmptyLabel(); });

        row.appendChild(seriesIdInput);
        row.appendChild(aniListInput);
        row.appendChild(malInput);
        row.appendChild(labelInput);
        row.appendChild(removeButton);
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
        // Round-trip the WHOLE config object: unrelated sections survive; only
        // Anime.SeriesOverrides is touched.
        window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            if (!config.Anime) { config.Anime = {}; }
            config.Anime.SeriesOverrides = overrides;
            window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                alertText('AnimeOverridesSaved', 'Anime overrides saved.');
                loadAnimeOverrides();
            });
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
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
