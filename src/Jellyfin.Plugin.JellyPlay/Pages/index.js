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

    // Pulls a readable message out of an error body — the defaults endpoint
    // answers 400 with { error, message, problems: [...] }. The problems
    // ride on the thrown Error (`error.problems`) so callers can anchor them
    // back to the offending editor rows.
    function extractErrorDetail(text, status) {
        try {
            var parsed = JSON.parse(text);
            if (parsed && Array.isArray(parsed.problems) && parsed.problems.length > 0) {
                var error = new Error(parsed.problems.join(' '));
                error.problems = parsed.problems;
                return error;
            }
            if (parsed && typeof parsed.message === 'string' && parsed.message) {
                return new Error(parsed.message);
            }
        } catch (ignored) { }
        return new Error(String(status));
    }

    function apiGet(path) {
        return fetch(window.ApiClient.getUrl(path), { headers: authHeaders() }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (text) {
                    throw extractErrorDetail(text, response.status);
                });
            }
            return response.json();
        });
    }

    function apiPost(path, body) {
        return fetch(window.ApiClient.getUrl(path), {
            method: 'POST',
            headers: authHeaders({ 'Content-Type': 'application/json' }),
            body: body === undefined ? undefined : JSON.stringify(body)
        }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (text) {
                    throw extractErrorDetail(text, response.status);
                });
            }
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
        // Re-inject rather than skip: the SPA keeps this document (and the
        // old style element) alive across page visits, so a plugin upgrade
        // serving newer CSS must replace the stale block, not bow out.
        var existing = document.getElementById('jellyplayRowStyles');
        if (existing) { existing.remove(); }
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
            '.jellyplay-field.invalid .emby-input, .jellyplay-field.invalid .emby-select, .jellyplay-field.invalid input { outline:1px solid #ff6c6c; }',
            '.jellyplay-field-error { font-size:.8em; color:#ff6c6c; padding-left:2px; }',
            '.jellyplay-field-help { flex-basis:100%; font-size:.82em; color:rgba(255,255,255,.65); padding-left:2px; }',
            '.jellyplay-field-help.ok { color:#7ddb87; }',
            '.jellyplay-switch { display:inline-flex; align-items:center; cursor:pointer; margin:6px 0; }',
            '.jellyplay-switch-input { position:absolute; opacity:0; width:0; height:0; margin:0; }',
            '.jellyplay-switch-track { width:34px; height:14px; border-radius:7px; background:rgba(255,255,255,.3); position:relative; transition:background .15s; flex:0 0 auto; }',
            '.jellyplay-switch-knob { position:absolute; top:-3px; left:0; width:20px; height:20px; border-radius:50%; background:#f2f2f2; box-shadow:0 1px 3px rgba(0,0,0,.4); transition:transform .15s, background .15s; }',
            '.jellyplay-switch-input:checked + .jellyplay-switch-track { background:rgba(0,164,220,.5); }',
            '.jellyplay-switch-input:checked + .jellyplay-switch-track .jellyplay-switch-knob { transform:translateX(14px); background:#00a4dc; }',
            '.jellyplay-switch-input:focus-visible + .jellyplay-switch-track { outline:1px solid #00a4dc; outline-offset:2px; }',
            '.jellyplay-field-boolean { flex-direction:row; align-items:center; gap:8px; }',
            '.jellyplay-field-boolean .jellyplay-field-label { padding:0; }',
            '.jellyplay-row .jellyplay-remove { flex:0 0 auto; margin:0 0 2px; }',
            '.jellyplay-row .jellyplay-test { flex:0 0 auto; margin:0 0 2px; }',
            '.jellyplay-test-status { flex-basis:100%; font-size:.85em; color:rgba(255,255,255,.75); }',
            '.jellyplay-test-status.fail { color:#ff6c6c; }',
            '.jellyplay-combobox { position:relative; }',
            '.jellyplay-combobox-menu { position:absolute; top:100%; left:0; right:0; z-index:100; max-height:280px; overflow-y:auto; background:#1c1c1e; border:1px solid rgba(255,255,255,.18); border-radius:.35em; box-shadow:0 8px 24px rgba(0,0,0,.55); margin-top:2px; }',
            '.jellyplay-cbo-group { font-size:.72em; font-weight:600; letter-spacing:.05em; text-transform:uppercase; color:rgba(255,255,255,.5); padding:8px 10px 2px; }',
            '.jellyplay-cbo-option { padding:6px 10px; cursor:pointer; }',
            '.jellyplay-cbo-option.active, .jellyplay-cbo-option:hover { background:rgba(255,255,255,.12); }',
            '.jellyplay-cbo-option .cbo-label { display:block; font-size:.92em; }',
            '.jellyplay-cbo-option .cbo-key { display:block; font-size:.78em; color:rgba(255,255,255,.55); font-family:monospace; }',
            '.jellyplay-cbo-option .cbo-desc { display:block; font-size:.78em; color:rgba(255,255,255,.6); }',
            '.jellyplay-cbo-foot { padding:6px 10px; font-size:.8em; color:rgba(255,255,255,.5); border-top:1px solid rgba(255,255,255,.1); }',
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

    // Non-alerting JSON probe for inline validation.
    function tryParseJson(raw) {
        var text = raw.trim();
        if (text.length === 0) { return { ok: false, empty: true, value: null }; }
        try {
            return { ok: true, empty: false, value: JSON.parse(text) };
        } catch (ignored) {
            return { ok: true, empty: false, value: text };
        }
    }

    // Builds the value control matching a known key's type; unknown keys get
    // the free-form JSON text field.
    function buildValueControl(kind, info, initial) {
        var control;
        if (kind === 'boolean') {
            // A styled toggle instead of a bare checkbox. The hidden input
            // stays the [data-kind] control, so the read/validate/wire paths
            // are unchanged; the wrapper is what gets appended.
            var shell = document.createElement('label');
            shell.className = 'jellyplay-switch';
            control = document.createElement('input');
            control.type = 'checkbox';
            control.className = 'jellyplay-switch-input';
            control.checked = initial === true;
            var track = document.createElement('span');
            track.className = 'jellyplay-switch-track';
            track.appendChild(document.createElement('span')).className = 'jellyplay-switch-knob';
            shell.appendChild(control);
            shell.appendChild(track);
            control.setAttribute('data-kind', kind);
            return shell;
        } else if (kind === 'number') {
            control = document.createElement('input');
            control.type = 'number';
            control.className = 'emby-input';
            if (info && info.min !== undefined) { control.min = info.min; }
            if (info && info.max !== undefined) { control.max = info.max; }
            if (typeof initial === 'number' && !isNaN(initial)) { control.value = initial; }
        } else if (kind === 'enum') {
            control = document.createElement('select');
            control.className = 'emby-select';
            var enumOptions = (info && info.options) || [];
            var enumLabels = (info && info.optionLabels) || [];
            enumOptions.forEach(function (optionValue, index) {
                var option = document.createElement('option');
                option.value = optionValue;
                // The wire value is the raw enum constant; the display label
                // is the humanized mirror the catalog carries alongside it.
                option.textContent = enumLabels[index] || optionValue;
                control.appendChild(option);
            });
            if (initial !== undefined && initial !== null && (info.options || []).indexOf(String(initial)) !== -1) {
                control.value = String(initial);
            }
        } else {
            control = document.createElement('input');
            control.type = 'text';
            control.className = 'emby-input';
            control.placeholder = 'value (JSON)';
            if (initial !== undefined && initial !== null) { control.value = formatValue(initial); }
        }
        control.setAttribute('data-kind', kind);
        return control;
    }

    function readDefaultValue(wrap) {
        var control = wrap.querySelector('[data-kind]');
        var kind = control.getAttribute('data-kind');
        if (kind === 'boolean') { return control.checked; }
        if (kind === 'number') { return control.value === '' ? null : Number(control.value); }
        if (kind === 'enum' || kind === 'string') { return control.value === '' ? null : control.value; }
        var parsed = tryParseJson(control.value);
        return parsed.empty ? null : parsed.value;
    }

    // The defaults key picker: a single search input over the catalog with a
    // suggestion dropdown. Free-typed text matching no catalog entry simply
    // IS the custom key — the old "Custom…" escape hatch, implicit.
    function rowKeyInput(row) { return row.querySelector('[data-role="defaults-key-input"]'); }
    function rowKeyMenu(row) { return row.querySelector('[data-role="defaults-key-menu"]'); }

    // The row's effective key: whatever the picker holds — a catalog pick or
    // a hand-typed custom key.
    function rowKeyValue(row) {
        var input = rowKeyInput(row);
        return input ? input.value.trim() : '';
    }

    // fmt's argument convention substitutes the fallback into {0}, so a
    // message carrying its OWN {n} placeholders cannot go through it. This
    // variant is alertFmt's convention (args from index 2) but returns the
    // string instead of alerting.
    function sub(key, fallback) {
        var text = strings[key] || fallback;
        for (var i = 2; i < arguments.length; i++) {
            text = String(text).replace('{' + (i - 2) + '}', String(arguments[i]));
        }
        return text;
    }

    // The per-key help line: what the setting does, the client's own default,
    // and the audited numeric range — the constraints BEFORE typing. Hidden
    // for custom (unknown) keys, where there is nothing to say.
    function refreshDefaultsHelp(row, info) {
        var help = row.querySelector('[data-role="defaults-help"]');
        if (!help) { return; }
        help.textContent = '';
        if (!info) { help.style.display = 'none'; return; }
        var head = info.label || '';
        if (info.description) { head += (head ? ' — ' : '') + info.description; }
        var parts = head ? [head] : [];
        if (info.defaultValue !== undefined && info.defaultValue !== null) {
            parts.push(sub('DefaultsClientDefault', 'Client default: {0}', formatValue(info.defaultValue)));
        }
        if (info.kind === 'number') {
            var hasMin = info.min !== undefined && info.min !== null;
            var hasMax = info.max !== undefined && info.max !== null;
            if (hasMin && hasMax) {
                parts.push(sub('DefaultsRange', 'Range: {0}–{1}', info.min, info.max));
            } else if (hasMin) {
                parts.push(sub('DefaultsMin', 'Min: {0}', info.min));
            } else if (hasMax) {
                parts.push(sub('DefaultsMax', 'Max: {0}', info.max));
            }
        }
        help.textContent = parts.join(' · ');
        help.style.display = parts.length > 0 ? '' : 'none';
    }

    // The custom-value status line: makes the JSON-vs-plain-string fallback
    // explicit instead of silent. Stays quiet when the field error already
    // flags the value.
    function refreshJsonStatus(row) {
        var wrap = row.querySelector('[data-role="defaults-value"]');
        var status = row.querySelector('[data-role="defaults-json-status"]');
        if (!wrap || !status) { return; }
        status.classList.remove('ok');
        if (wrap.getAttribute('data-kind') !== 'json') { status.style.display = 'none'; return; }
        var control = wrap.querySelector('[data-kind]');
        var raw = control ? control.value.trim() : '';
        if (raw.length === 0) { status.style.display = 'none'; return; }
        if (/^[\[{]/.test(raw)) {
            if (isParsableJson(raw)) {
                status.textContent = strings.DefaultsJsonOk || 'Valid JSON — stored as JSON.';
                status.classList.add('ok');
                status.style.display = '';
            } else {
                status.style.display = 'none';
            }
            return;
        }
        status.textContent = strings.DefaultsJsonText || 'Not JSON — will be stored as a plain string.';
        status.style.display = '';
    }

    // Swaps the value control when the typed key changes kind, carrying the
    // old value over when it survives the conversion, otherwise prefilling
    // the catalog's recommended default.
    function syncValueKind(row) {
        var wrap = row.querySelector('[data-role="defaults-value"]');
        if (!wrap) { return; }
        var info = knownKeyInfo(rowKeyValue(row));
        var kind = info ? info.kind : 'json';
        // Help and status track the KEY, so they refresh even when the kind
        // (and therefore the control) does not change.
        refreshDefaultsHelp(row, info);
        refreshJsonStatus(row);
        if (wrap.getAttribute('data-kind') === kind) { return; }
        var previous = readDefaultValue(wrap);
        var usable = previous !== null && previous !== undefined && previous !== '';
        var initial = usable ? previous : (info ? info.defaultValue : undefined);
        var label = wrap.querySelector('.jellyplay-field-label');
        clearFieldErrors(row);
        // .jellyplay-switch goes too — it wraps the boolean input, and
        // removing only the input would leave an empty track husk behind.
        wrap.querySelectorAll('input, select, .jellyplay-switch, .jellyplay-field-error').forEach(function (node) { node.remove(); });
        var control = buildValueControl(kind, info, initial);
        if (kind === 'boolean') {
            wrap.classList.add('jellyplay-field-boolean');
        } else {
            wrap.classList.remove('jellyplay-field-boolean');
        }
        wrap.appendChild(control);
        wrap.setAttribute('data-kind', kind);
        control.addEventListener('input', function () { validateDefaultRow(row); });
        control.addEventListener('change', function () { validateDefaultRow(row); });
        if (label) { wrap.insertBefore(label, wrap.firstChild); }
    }

    // Inline row validation: marks bad fields, returns nothing (state only).
    function validateDefaultRow(row, seen) {
        clearFieldErrors(row);
        var key = rowKeyValue(row);
        var valueWrap = row.querySelector('[data-role="defaults-value"]');
        var value = readDefaultValue(valueWrap);
        var jsonKind = valueWrap.getAttribute('data-kind') === 'json';
        var raw = jsonKind ? valueWrap.querySelector('[data-kind]').value : '';
        var blank = key.length === 0 && (jsonKind ? raw.trim().length === 0 : value === null);

        if (blank) { return true; }

        var ok = true;
        var keyControl = rowKeyInput(row);
        if (key.length === 0) {
            setFieldError(keyControl, strings.DefaultsInvalidKey || 'Enter or pick a key.');
            ok = false;
        } else if (!knownKeyInfo(key)
            && (key.indexOf('/') <= 0 || key.indexOf('/') === key.length - 1 || /\s/.test(key))) {
            // Catalog keys are correct by construction; only hand-typed keys
            // need the namespace/key shape check.
            setFieldError(keyControl, strings.DefaultsInvalidKey || 'Key must look like namespace/key.');
            ok = false;
        } else if (seen && seen.indexOf(key) !== -1) {
            setFieldError(keyControl, strings.DefaultsDuplicateKey || 'Duplicate key.');
            ok = false;
        }

        var invalid = false;
        if (jsonKind) {
            var parsed = tryParseJson(raw);
            invalid = parsed.empty || /^[\[{]/.test(raw.trim()) && !isParsableJson(raw.trim());
        } else {
            invalid = value === null || (valueWrap.getAttribute('data-kind') === 'number' && isNaN(value));
        }
        if (invalid) {
            setFieldError(valueWrap.querySelector('[data-kind]'), strings.DefaultsInvalidValue || 'Enter a value.');
            ok = false;
        } else if (valueWrap.getAttribute('data-kind') === 'number' && value !== null && !isNaN(value)) {
            // Catalog range check — mirrors the server's ValidateValue against
            // the same descriptor, so an out-of-range number dies here first.
            var rangeInfo = knownKeyInfo(key);
            var rangeControl = valueWrap.querySelector('[data-kind]');
            var hasMin = rangeInfo && rangeInfo.min !== undefined && rangeInfo.min !== null;
            var hasMax = rangeInfo && rangeInfo.max !== undefined && rangeInfo.max !== null;
            if (hasMin && hasMax && (value < rangeInfo.min || value > rangeInfo.max)) {
                setFieldError(rangeControl, sub('DefaultsInvalidRangeBetween', 'Must be between {0} and {1}.', rangeInfo.min, rangeInfo.max));
                ok = false;
            } else if (hasMin && value < rangeInfo.min) {
                setFieldError(rangeControl, sub('DefaultsInvalidRangeMin', 'Must be at least {0}.', rangeInfo.min));
                ok = false;
            } else if (hasMax && value > rangeInfo.max) {
                setFieldError(rangeControl, sub('DefaultsInvalidRangeMax', 'Must be at most {0}.', rangeInfo.max));
                ok = false;
            }
        }
        refreshJsonStatus(row);
        return ok;
    }

    function isParsableJson(text) {
        try {
            JSON.parse(text);
            return true;
        } catch (ignored) {
            return false;
        }
    }

    // Validates every defaults row (marks errors inline); false = don't save.
    function validateDefaults() {
        var seen = [];
        var ok = true;
        var rows = defaultsList().children;
        for (var i = 0; i < rows.length; i++) {
            var rowOk = validateDefaultRow(rows[i], seen);
            var key = rowKeyValue(rows[i]);
            if (key.length > 0) {
                seen.push(key);
                if (!rowOk) { ok = false; }
            }
        }
        return ok;
    }

    // Upper bound on suggestion hits rendered at once — the catalog holds
    // ~145 keys and an unfiltered query would render them all.
    var KEY_MATCH_LIMIT = 50;

    // Renders the suggestion menu: catalog entries matching the query
    // (label, key, or description — empty query browses the whole catalog),
    // grouped by the client's domain, capped, with a footer for the overflow
    // or the no-match/custom hint.
    function renderKeyMenu(row) {
        var input = rowKeyInput(row);
        var menu = rowKeyMenu(row);
        if (!input || !menu) { return; }
        var query = input.value.trim().toLowerCase();
        // Ranked matches: a query hit on the key or label outranks one only
        // riding the description ("downmix" must surface downmix_boost_db,
        // not a whose description merely says "downmixed").
        var matches = [];
        var overflow = 0;
        for (var rank = 0; rank < 2 && matches.length <= KEY_MATCH_LIMIT; rank++) {
            for (var i = 0; i < KNOWN_KEYS.length; i++) {
                var entry = KNOWN_KEYS[i];
                var inKey = entry.key.toLowerCase().indexOf(query) !== -1
                    || (entry.label || '').toLowerCase().indexOf(query) !== -1;
                var inDesc = (entry.description || '').toLowerCase().indexOf(query) !== -1;
                if (!query || (rank === 0 ? inKey : (!inKey && inDesc))) {
                    if (matches.length < KEY_MATCH_LIMIT) {
                        matches.push(entry);
                    } else {
                        overflow++;
                    }
                }
            }
        }

        menu.textContent = '';
        var lastGroup = null;
        matches.forEach(function (entry) {
            if (entry.group && entry.group !== lastGroup) {
                var group = document.createElement('div');
                group.className = 'jellyplay-cbo-group';
                group.textContent = entry.group;
                menu.appendChild(group);
                lastGroup = entry.group;
            }
            var option = document.createElement('div');
            option.className = 'jellyplay-cbo-option';
            option.setAttribute('data-key', entry.key);
            var label = document.createElement('span');
            label.className = 'cbo-label';
            label.textContent = entry.label || entry.key;
            option.appendChild(label);
            var slug = document.createElement('span');
            slug.className = 'cbo-key';
            slug.textContent = entry.key;
            option.appendChild(slug);
            if (entry.description) {
                var desc = document.createElement('span');
                desc.className = 'cbo-desc';
                desc.textContent = entry.description;
                option.appendChild(desc);
            }
            // preventDefault on mousedown keeps the input focused (no blur
            // race) while the click still selects.
            option.addEventListener('mousedown', function (event) {
                event.preventDefault();
                selectKey(row, entry.key);
            });
            menu.appendChild(option);
        });
        if (overflow > 0 || matches.length === 0) {
            var foot = document.createElement('div');
            foot.className = 'jellyplay-cbo-foot';
            foot.textContent = overflow > 0
                ? sub('DefaultsMoreResults', '{0} more…', overflow)
                : (strings.DefaultsNoMatches || 'No matching catalog key — keep it as a custom key.');
            menu.appendChild(foot);
        }
        menu.hidden = false;
        setActiveOption(menu, matches.length > 0 ? 0 : -1);
    }

    function closeKeyMenu(row) {
        var menu = rowKeyMenu(row);
        if (menu) { menu.hidden = true; }
    }

    function activeOptionIndex(menu) {
        return Number(menu.getAttribute('data-active') || '-1');
    }

    function setActiveOption(menu, index) {
        var options = menu.querySelectorAll('.jellyplay-cbo-option');
        if (options.length === 0) {
            menu.setAttribute('data-active', '-1');
            return;
        }
        var next = ((index % options.length) + options.length) % options.length;
        options.forEach(function (option, i) {
            option.classList.toggle('active', i === next);
        });
        if (options[next].scrollIntoView) { options[next].scrollIntoView({ block: 'nearest' }); }
        menu.setAttribute('data-active', String(next));
    }

    function selectKey(row, key) {
        var input = rowKeyInput(row);
        input.value = key;
        closeKeyMenu(row);
        syncValueKind(row);
        validateDefaultRow(row);
        input.focus();
    }

    // Wires one key picker: filter-as-you-type, keyboard navigation
    // (↑/↓/Enter/Esc), click selection. Enter never submits the surrounding
    // dashboard form — the input is a picker, not a submit button.
    function wireCombobox(row, input, menu) {
        input.addEventListener('input', function () {
            renderKeyMenu(row);
            validateDefaultRow(row);
        });
        input.addEventListener('focus', function () { renderKeyMenu(row); });
        input.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                if (menu.hidden) { renderKeyMenu(row); return; }
                var delta = event.key === 'ArrowDown' ? 1 : -1;
                setActiveOption(menu, activeOptionIndex(menu) + delta);
                return;
            }
            if (event.key === 'Enter') {
                event.preventDefault();
                if (!menu.hidden) {
                    var options = menu.querySelectorAll('.jellyplay-cbo-option');
                    var index = activeOptionIndex(menu);
                    if (index >= 0 && options[index]) {
                        selectKey(row, options[index].getAttribute('data-key'));
                    } else {
                        closeKeyMenu(row);
                    }
                }
                return;
            }
            if (event.key === 'Escape') { closeKeyMenu(row); }
        });
        input.addEventListener('blur', function () { closeKeyMenu(row); });
        input.addEventListener('change', function () {
            syncValueKind(row);
            validateDefaultRow(row);
        });
    }

    function addDefaultRow(key, entry) {
        ensureRowStyles();
        var row = document.createElement('div');
        row.className = 'jellyplay-row';

        var keyWrap = document.createElement('div');
        keyWrap.className = 'jellyplay-field jellyplay-combobox';
        keyWrap.appendChild(fieldLabel(strings.DefaultsColumnKey || 'Key'));
        var keyInput = document.createElement('input');
        keyInput.type = 'text';
        keyInput.className = 'emby-input';
        keyInput.setAttribute('data-role', 'defaults-key-input');
        keyInput.setAttribute('autocomplete', 'off');
        keyInput.placeholder = strings.DefaultsSearchPlaceholder || "Type to search the client's settings…";
        if (key) { keyInput.value = key; }
        keyWrap.appendChild(keyInput);
        var keyMenu = document.createElement('div');
        keyMenu.className = 'jellyplay-combobox-menu';
        keyMenu.setAttribute('data-role', 'defaults-key-menu');
        keyMenu.hidden = true;
        keyWrap.appendChild(keyMenu);
        row.appendChild(keyWrap);
        wireCombobox(row, keyInput, keyMenu);

        var modeSelect = appendField(row, strings.DefaultsColumnMode || 'Mode', {
            tag: 'select', className: 'emby-select', variant: 'narrow'
        });
        modeSelect.setAttribute('data-role', 'defaults-mode');
        modeSelect.title = strings.DefaultsModeHint
            || 'Suggested fills only what the user has not set; Forced overrides the user.';
        [['suggested', 'DefaultsModeSuggested'], ['forced', 'DefaultsModeForced']].forEach(function (pair) {
            var option = document.createElement('option');
            option.value = pair[0];
            option.textContent = strings[pair[1]] || pair[0];
            modeSelect.appendChild(option);
        });
        modeSelect.value = (entry && entry.mode === 'forced') ? 'forced' : 'suggested';

        var info = knownKeyInfo(rowKeyValue(row));
        var kind = info ? info.kind : 'json';
        var valueWrap = document.createElement('div');
        valueWrap.className = 'jellyplay-field';
        if (kind === 'boolean') { valueWrap.classList.add('jellyplay-field-boolean'); }
        valueWrap.setAttribute('data-role', 'defaults-value');
        valueWrap.setAttribute('data-kind', kind);
        valueWrap.appendChild(fieldLabel(strings.DefaultsColumnValue || 'Value'));
        var valueControl = buildValueControl(kind, info, entry ? entry.value : undefined);
        valueWrap.appendChild(valueControl);
        row.appendChild(valueWrap);

        valueControl.addEventListener('input', function () { validateDefaultRow(row); });
        valueControl.addEventListener('change', function () { validateDefaultRow(row); });

        var jsonStatus = document.createElement('div');
        jsonStatus.className = 'jellyplay-field-help';
        jsonStatus.setAttribute('data-role', 'defaults-json-status');
        jsonStatus.style.display = 'none';
        valueWrap.appendChild(jsonStatus);

        var help = document.createElement('div');
        help.className = 'jellyplay-field-help';
        help.setAttribute('data-role', 'defaults-help');
        row.appendChild(help);

        removeButton(row, refreshDefaultsEmptyLabel);
        defaultsList().appendChild(row);
        syncValueKind(row);
        refreshDefaultsEmptyLabel();
    }

    function refreshDefaultsEmptyLabel() {
        var empty = defaultsList().children.length === 0;
        defaultsEmptyLabel().style.display = empty ? '' : 'none';
    }

    function collectDefaults() {
        var map = {};
        Array.prototype.forEach.call(defaultsList().children, function (row) {
            var key = rowKeyValue(row);
            if (key.length === 0) { return; }
            var value = readDefaultValue(row.querySelector('[data-role="defaults-value"]'));
            if (value === null) { return; }
            var modeSelect = row.querySelector('[data-role="defaults-mode"]');
            map[key] = { mode: modeSelect ? modeSelect.value : 'suggested', value: value };
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

    // Anchors a 400's structured problems back onto their rows: each problem
    // is "<ns>/<key>: <message>", so the prefix finds the row and the message
    // lands on its value control as a normal inline field error. Unmatched
    // problems stay in the toast.
    function anchorDefaultProblems(error) {
        var problems = error && error.problems;
        if (!Array.isArray(problems)) { return; }
        problems.forEach(function (problem) {
            var separator = String(problem).indexOf(': ');
            if (separator <= 0) { return; }
            var key = String(problem).slice(0, separator);
            var message = String(problem).slice(separator + 2);
            var match = null;
            Array.prototype.forEach.call(defaultsList().children, function (row) {
                if (rowKeyValue(row) === key) { match = row; }
            });
            if (!match) { return; }
            var control = match.querySelector('[data-role="defaults-value"] [data-kind]');
            if (control) { setFieldError(control, message); }
        });
    }

    document.getElementById('jellyplayDefaultsSave').addEventListener('click', function () {
        if (!validateDefaults()) {
            alertFmt('EditorFixHighlighted', 'Fix the highlighted fields.');
            return;
        }
        apiPost('jellyplay/defaults', collectDefaults()).then(function () {
            alertText('DefaultsSaved', 'Defaults saved.');
            return loadDefaults();
        }).catch(function (error) {
            anchorDefaultProblems(error);
            alertFmt('DefaultsRejected', 'Save rejected: {0}', error && error.message || error);
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
    // label is the friendly dropdown name; urlPattern extracts the list id
    // from a pasted URL; hintKey is the localized "what to paste here" text.
    var ROW_SOURCES = [
        {
            value: 'letterboxd', label: 'Letterboxd', needsId: true,
            urlPatterns: [/letterboxd\.com\/[^\/\s]+\/list\/([^\/\s?#]+)/i],
            hintKey: 'CustomRowsHintLetterboxd', hint: 'Paste a letterboxd list URL (letterboxd.com/user/list/slug) or the slug.'
        },
        {
            value: 'imdb', label: 'IMDb', needsId: true,
            urlPatterns: [/imdb\.com\/list\/(ls[0-9]+)/i, /imdb\.com\/chart\/([a-z0-9]+)/i],
            hintKey: 'CustomRowsHintImdb', hint: 'Paste an IMDb list URL (imdb.com/list/lsXXXXXXXX) or a chart name.'
        },
        {
            value: 'mdblist', label: 'MDBList', needsId: true,
            urlPatterns: [/mdblist\.com\/lists\/([^\/\s?#]+)/i],
            hintKey: 'CustomRowsHintMdblist', hint: 'Paste an MDBList list URL or the list id.'
        },
        {
            value: 'tmdb', label: 'TMDB', needsId: true,
            urlPatterns: [/themoviedb\.org\/list\/([0-9]+)/i],
            hintKey: 'CustomRowsHintTmdb', hint: 'Paste a TMDB list URL (themoviedb.org/list/XXXXXXXX) or the numeric list id.'
        }
    ];

    // The client defaults key catalog, loaded from the plugin (GET
    // jellyplay/settings/catalog) — the plugin owns the key list; this page
    // only renders it. The catalog is GENERATED from the client's own
    // preference declarations, so every key/type/enum option (and its human
    // label/description) is the real thing the client syncs. Keys the catalog
    // does not know yet (a newer client against an older plugin) stay legal:
    // free-typed text simply IS the custom key.
    var KNOWN_KEYS = [];
    var KNOWN_KEY_INDEX = {};

    function loadCatalog() {
        return apiGet('jellyplay/settings/catalog').then(function (res) {
            KNOWN_KEYS = ((res && res.settings) || []).map(function (descriptor) {
                var kind = descriptor.valueType;
                if (kind !== 'boolean' && kind !== 'number' && kind !== 'enum' && kind !== 'string') {
                    kind = 'json';
                }
                return {
                    key: descriptor.ns + '/' + descriptor.key,
                    kind: kind,
                    label: descriptor.label,
                    description: descriptor.description,
                    group: descriptor.group,
                    options: descriptor.options,
                    optionLabels: descriptor.optionLabels,
                    min: descriptor.min,
                    max: descriptor.max,
                    defaultValue: descriptor.defaultValue
                };
            });
            KNOWN_KEY_INDEX = {};
            KNOWN_KEYS.forEach(function (entry) {
                KNOWN_KEY_INDEX[entry.key] = entry;
            });
        }).catch(function () {
            // Catalog unavailable: editors degrade to free-text keys/values.
            KNOWN_KEYS = [];
            KNOWN_KEY_INDEX = {};
        });
    }

    function knownKeyInfo(key) {
        return Object.prototype.hasOwnProperty.call(KNOWN_KEY_INDEX, key) ? KNOWN_KEY_INDEX[key] : null;
    }

    function fmtMsg(text, value) {
        return String(text).replace('{0}', String(value));
    }

    function setFieldError(control, message) {
        var field = control.parentNode;
        field.classList.add('invalid');
        var note = field.querySelector('.jellyplay-field-error');
        if (!note) {
            note = document.createElement('div');
            note.className = 'jellyplay-field-error';
            field.appendChild(note);
        }
        note.textContent = message;
    }

    function clearFieldErrors(row) {
        row.querySelectorAll('.jellyplay-field.invalid').forEach(function (field) {
            field.classList.remove('invalid');
        });
        row.querySelectorAll('.jellyplay-field-error').forEach(function (note) {
            note.remove();
        });
    }

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

    // Extracts a list id from a pasted URL (or passes a bare id through).
    function parseListId(source, raw) {
        var text = raw.trim();
        var info = sourceInfo(source);
        if (!info) { return text; }
        for (var i = 0; i < info.urlPatterns.length; i++) {
            var match = text.match(info.urlPatterns[i]);
            if (match) { return match[1]; }
        }
        return text;
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
            option.textContent = source.label || source.value;
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
            placeholder: strings.CustomRowsHintLetterboxd || 'List URL or id',
            value: (definition && definition.ListId) || ''
        });
        listIdInput.setAttribute('data-role', 'row-listid');
        listIdInput.addEventListener('change', function () {
            listIdInput.value = parseListId(sourceSelect.value, listIdInput.value);
        });

        removeButton(row, refreshRowsEmptyLabel, 'CustomRowsRemove');

        var testButton = document.createElement('button');
        testButton.type = 'button';
        testButton.className = 'raised emby-button jellyplay-test';
        testButton.textContent = strings.CustomRowsTest || 'Test';
        testButton.addEventListener('click', function () { testCustomRow(row); });
        row.appendChild(testButton);

        var testStatus = document.createElement('div');
        testStatus.className = 'jellyplay-test-status';
        testStatus.style.display = 'none';
        row.appendChild(testStatus);

        sourceSelect.addEventListener('change', function () { applyListIdVisibility(row); });
        rowsList().appendChild(row);
        applyListIdVisibility(row);
        refreshRowsEmptyLabel();
    }

    // Writes the test outcome onto the CURRENT DOM row for the title — the
    // save inside this flow rebuilds all rows, so captured nodes go stale.
    function setTestStatus(title, ok, text) {
        var rows = rowsList().children;
        for (var i = 0; i < rows.length; i++) {
            var rowTitle = rows[i].querySelectorAll('input, select')[0].value.trim();
            if (rowTitle === title) {
                var status = rows[i].querySelector('.jellyplay-test-status');
                if (status) {
                    status.classList.toggle('fail', !ok);
                    status.style.display = '';
                    status.textContent = text;
                }
                return;
            }
        }
    }

    // Saves the rows editor, then resolves the row's title through the server
    // and reports the library match count inline. Saving first is required —
    // the server resolves rows from the stored configuration.
    function testCustomRow(row) {
        var inputs = row.querySelectorAll('input, select');
        var title = inputs[0].value.trim();
        if (title.length === 0) {
            setFieldError(inputs[0], strings.CustomRowsInvalidTitle || 'Every row needs a non-empty title.');
            return;
        }

        document.getElementById('jellyplayRowsSave').click();
        enqueueConfigWrite(function () {
            return apiGet('jellyplay/rows/items?title=' + encodeURIComponent(title)).then(function (result) {
                var count = result && result.items ? result.items.length : 0;
                setTestStatus(title, true, fmtMsg(strings.CustomRowsTestOk || 'OK — {0} item(s) resolved from your library.', count));
            }).catch(function () {
                setTestStatus(title, false, strings.CustomRowsTestFail || 'Could not resolve this list — check the id and try again.');
            });
        });
    }

    function refreshRowsEmptyLabel() {
        var empty = rowsList().children.length === 0;
        rowsEmptyLabel().style.display = empty ? '' : 'none';
    }

    // Collects and validates the editor rows. Fully blank rows are dropped
    // silently; anything else invalid is marked inline and the save aborts
    // (nothing is written half-way).
    function collectRows() {
        var rows = [];
        var seenTitles = {};
        var valid = true;
        var children = rowsList().children;
        for (var i = 0; i < children.length; i++) {
            var row = children[i];
            clearFieldErrors(row);
            var inputs = row.querySelectorAll('input, select');
            var title = inputs[0].value.trim();
            var source = inputs[1].value;
            var limit = Number(inputs[2].value);
            var listId = inputs[3].value.trim();

            if (title.length === 0 && listId.length === 0 && inputs[2].value === '') {
                continue;
            }

            if (title.length === 0) {
                setFieldError(inputs[0], strings.CustomRowsInvalidTitle || 'Every row needs a non-empty title.');
                valid = false;
                continue;
            }

            if (seenTitles[title.toLowerCase()]) {
                setFieldError(inputs[0], strings.CustomRowsDuplicateTitle || 'Duplicate title.');
                valid = false;
                continue;
            }
            seenTitles[title.toLowerCase()] = true;

            if (!(limit >= 1)) {
                setFieldError(inputs[2], fmtMsg(strings.CustomRowsInvalidLimit || 'Row "{0}" needs a limit of at least 1.', title));
                valid = false;
                continue;
            }

            rows.push({ Title: title, Source: source, ListId: listId, Limit: Math.floor(limit) });
        }

        return valid ? rows : null;
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
        if (rows === null) {
            alertFmt('EditorFixHighlighted', 'Fix the highlighted fields.');
            return;
        }
        enqueueConfigWrite(function () {
            // Round-trip the WHOLE config object: unrelated sections survive.
            return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                if (!config.Rows) { config.Rows = {}; }
                config.Rows.CustomRows = rows;
                return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                    alertText('CustomRowsSaved', 'Custom rows saved.');
                    // Reload before the queued job continues: chained work
                    // (the row Test button) must query the REBUILT rows.
                    return loadRows();
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
    // silently; anything else invalid is marked inline and the save aborts
    // (nothing is written half-way).
    function collectAnimeOverrides() {
        var overrides = [];
        var valid = true;
        var children = animeOverridesList().children;
        for (var i = 0; i < children.length; i++) {
            var row = children[i];
            clearFieldErrors(row);
            var inputs = row.querySelectorAll('input');
            var seriesId = inputs[0].value.trim();
            var aniListId = inputs[1].value.trim();
            var malId = inputs[2].value.trim();
            var label = inputs[3].value.trim();

            if (seriesId.length === 0 && aniListId.length === 0 && malId.length === 0 && label.length === 0) {
                continue;
            }

            if (seriesId.length === 0) {
                setFieldError(inputs[0], strings.AnimeOverridesInvalidSeries || 'Every override needs a non-empty series id.');
                valid = false;
                continue;
            }

            if (aniListId.length === 0 && malId.length === 0) {
                setFieldError(inputs[1], fmtMsg(strings.AnimeOverridesInvalidProvider || 'Override "{0}" needs an AniList id or a MAL id.', seriesId));
                valid = false;
                continue;
            }

            var entry = { SeriesId: seriesId };
            if (aniListId.length > 0) { entry.AniListId = aniListId; }
            if (malId.length > 0) { entry.MalId = malId; }
            if (label.length > 0) { entry.Label = label; }
            overrides.push(entry);
        }

        return valid ? overrides : null;
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
        if (overrides === null) {
            alertFmt('EditorFixHighlighted', 'Fix the highlighted fields.');
            return;
        }
        enqueueConfigWrite(function () {
            // Round-trip the WHOLE config object: unrelated sections survive; only
            // Anime.SeriesOverrides is touched.
            return window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                if (!config.Anime) { config.Anime = {}; }
                config.Anime.SeriesOverrides = overrides;
                return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                    alertText('AnimeOverridesSaved', 'Anime overrides saved.');
                    return loadAnimeOverrides();
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
        // The defaults editor renders typed controls from the plugin's
        // catalog, so it must be loaded before the row lists.
        loadCatalog().then(function () {
            load();
            loadDefaults();
            loadRows();
            loadAnimeOverrides();
        });
    });
})();
