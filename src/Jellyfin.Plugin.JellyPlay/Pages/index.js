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
                alertText('DefaultsInvalidJson', '"{0}" is not valid JSON — saved as a plain string.', text);
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
            alertText('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
        });
    });

    document.getElementById('jellyplayDefaultsPush').addEventListener('click', function () {
        apiPost('jellyplay/admin/pushDefaults').then(function (outcome) {
            window.Dashboard.alert((strings.DefaultsPushed || 'Pushed {0} key(s) to {1} user(s).')
                .replace('{0}', outcome && outcome.keysPushed !== undefined ? outcome.keysPushed : '?')
                .replace('{1}', outcome && outcome.users !== undefined ? outcome.users : '?'));
        }).catch(function (error) {
            alertText('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
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
            alertText('MsgLoadFailed', 'Failed to load: {0}', error && error.message || error);
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
    });
})();
