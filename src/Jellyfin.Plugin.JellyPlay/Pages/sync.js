(function () {
    'use strict';

    // Page logic only — the api/i18n surface is jellyplay-common.js
    // (window.JellyPlayCommon): auth, fetch wrappers, error extraction, fmt.
    var common = window.JellyPlayCommon;
    var strings = common.strings;
    var fmt = common.fmt;
    var alertFmt = common.alertFmt;
    var apiGet = common.apiGet;
    var apiPost = common.apiPost;
    var apiDelete = common.apiDelete;
    var authHeaders = common.authHeaders;
    var applyStrings = common.applyStrings;

    // ── shared styles (the row/table scaffolding this page needs) ──

    function ensureStyles() {
        if (document.getElementById('jellyplaySyncStyles')) { return; }
        var style = document.createElement('style');
        style.id = 'jellyplaySyncStyles';
        style.textContent = [
            '.jellyplay-quota { display:flex; align-items:center; gap:10px; margin-bottom:8px; }',
            '.jellyplay-quota .jellyplay-field-label { flex:0 0 60px; font-size:.82em; font-weight:500; color:rgba(255,255,255,.65); }',
            '.jellyplay-quota progress { flex:1 1 auto; width:auto; min-width:120px; max-width:420px; height:12px; }',
            '.jellyplay-quota .jellyplay-test-status { flex:0 1 auto; }',
            '.jellyplay-table-wrap { margin-bottom:1em; }',
            '.jellyplay-op { display:block; }',
            '.jellyplay-op-toggle { background:none; border:none; color:inherit; cursor:pointer; padding:0; font:inherit; text-align:left; width:100%; }',
            '.jellyplay-op .jellyplay-test-status { margin-left:8px; }',
            '.jellyplay-op-keys { margin:8px 0 0; padding:6px 10px; background:rgba(255,255,255,.04); border-radius:.3em; font-size:.85em; }',
            '.jellyplay-op-keys div { padding:1px 0; }',
            '.jellyplay-badge { display:inline-block; padding:0 6px; border-radius:.3em; font-size:.78em; margin-left:8px; background:rgba(255,255,255,.12); }',
            '.jellyplay-badge.forced { background:#c33; }',
            '.jellyplay-badge.suggested { background:#2a7ad0; }',
            '.jellyplay-badge.revoked { background:#c33; }'
        ].join('\n');
        document.head.appendChild(style);
    }

    // ── formatting helpers ──

    function formatTime(ts) {
        if (!ts) { return ''; }
        return new Date(ts).toLocaleString();
    }

    function formatBytes(bytes) {
        if (bytes === undefined || bytes === null) { return ''; }
        if (bytes < 1024) { return bytes + ' B'; }
        if (bytes < 1024 * 1024) { return (bytes / 1024).toFixed(1) + ' KB'; }
        return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
    }

    function userName(userId) {
        var found = null;
        Object.keys(userNames).forEach(function (id) {
            if (id === userId) { found = userNames[id]; }
        });
        return found || userId;
    }

    var userNames = {};

    function opLabel(op) {
        var labels = { push: 'push', pull: 'pull', reset: 'reset', wipe: 'wipe' };
        return labels[op] || op;
    }

    // ── user pickers ──

    function fillUserSelect(select, includeAllOption) {
        select.innerHTML = '';
        if (includeAllOption) {
            var all = document.createElement('option');
            all.value = '';
            all.textContent = strings.PushDryRunScopeAll || 'All users';
            select.appendChild(all);
        }
        Object.keys(userNames).forEach(function (id) {
            var option = document.createElement('option');
            option.value = id;
            option.textContent = userNames[id];
            select.appendChild(option);
        });
    }

    function loadUsers() {
        return apiGet('jellyplay/admin/users').then(function (response) {
            userNames = {};
            ((response && response.users) || []).forEach(function (user) {
                userNames[user.userId] = user.userName || user.userId;
            });
            fillUserSelect(document.getElementById('jellyplaySyncUser'), false);
            fillUserSelect(document.getElementById('jellyplayPreviewUser'), false);
            fillUserSelect(document.getElementById('jellyplayDryRunUser'), true);
        });
    }

    // ── live monitor ──

    var live = { connected: false, controller: null, rows: 0 };

    function setLiveStatus(text, failing) {
        var status = document.getElementById('jellyplayLiveStatus');
        status.textContent = text;
        status.classList.toggle('fail', failing === true);
    }

    function appendLiveRow(entry) {
        var body = document.getElementById('jellyplayLiveBody');
        var row = document.createElement('tr');
        [formatTime(entry.ts), userName(entry.userId), opLabel(entry.op), entry.deviceId || '', entry.keysApplied, entry.keysRejected].forEach(function (value) {
            var cell = document.createElement('td');
            cell.textContent = value;
            row.appendChild(cell);
        });
        body.insertBefore(row, body.firstChild);
        live.rows++;
        while (body.children.length > 200) {
            body.removeChild(body.lastChild);
            live.rows--;
        }
        document.getElementById('jellyplayLiveEmpty').style.display = live.rows === 0 ? '' : 'none';
    }

    // One parsed SSE frame: { eventName, data } (ids are not needed — the
    // monitor is a live view, there is no resume).
    function parseFrame(frame) {
        var parsed = { eventName: 'message', data: '' };
        frame.split('\n').forEach(function (line) {
            if (line.indexOf(':') === 0) { return; } // keepalive comment
            var separator = line.indexOf(':');
            if (separator < 0) { return; }
            var field = line.slice(0, separator);
            var value = line.charAt(separator + 1) === ' ' ? line.slice(separator + 2) : line.slice(separator + 1);
            if (field === 'event') { parsed.eventName = value; }
            else if (field === 'data') { parsed.data = parsed.data ? parsed.data + '\n' + value : value; }
        });
        return parsed;
    }

    function pumpStream(reader) {
        var decoder = new TextDecoder();
        var buffer = '';
        function read() {
            return reader.read().then(function (chunk) {
                if (chunk.done || !live.connected) {
                    if (live.connected) { scheduleReconnect(); }
                    return;
                }
                buffer += decoder.decode(chunk.value, { stream: true });
                var boundary;
                while ((boundary = buffer.indexOf('\n\n')) >= 0) {
                    var frame = buffer.slice(0, boundary);
                    buffer = buffer.slice(boundary + 2);
                    if (frame.trim().length === 0) { continue; }
                    handleFrame(parseFrame(frame));
                }
                return read();
            }).catch(function () {
                if (live.connected) { scheduleReconnect(); }
            });
        }
        return read();
    }

    function handleFrame(frame) {
        if (frame.eventName !== 'sync.op' || !frame.data) { return; }
        try {
            appendLiveRow(JSON.parse(frame.data));
        } catch (ignored) {
            // A malformed payload must never take the monitor down.
        }
    }

    function scheduleReconnect() {
        setLiveStatus(fmt('LiveMonitorReconnecting', 'Reconnecting…'), true);
        setTimeout(connectLive, 5000);
    }

    function connectLive() {
        if (live.connected) { return; }
        live.connected = true;
        document.getElementById('jellyplayLiveConnect').disabled = true;
        document.getElementById('jellyplayLiveDisconnect').disabled = false;
        setLiveStatus(fmt('LiveMonitorConnected', 'Connected.'));

        live.controller = new AbortController();
        fetch(window.ApiClient.getUrl('jellyplay/admin/stream'), {
            headers: authHeaders(),
            signal: live.controller.signal
        }).then(function (response) {
            if (!response.ok || !response.body) {
                throw new Error(String(response.status));
            }
            return pumpStream(response.body.getReader());
        }).catch(function (error) {
            if (live.connected) {
                setLiveStatus(fmt('LiveMonitorFailed', 'Stream failed: {0}', error.message || error), true);
                scheduleReconnect();
            }
        });
    }

    function disconnectLive() {
        live.connected = false;
        if (live.controller) {
            live.controller.abort();
            live.controller = null;
        }
        document.getElementById('jellyplayLiveConnect').disabled = false;
        document.getElementById('jellyplayLiveDisconnect').disabled = true;
        setLiveStatus(fmt('LiveMonitorIdle', 'Idle.'));
    }

    // ── per-user drill-down ──

    var drilldownUser = null;

    function setBar(barId, textId, value, quota, renderValue) {
        var percent = quota > 0 ? Math.min(100, Math.round((value / quota) * 100)) : 0;
        document.getElementById(barId).value = percent;
        document.getElementById(textId).textContent = fmt('QuotaUsageText', '{0} of {1} ({2}%)', renderValue(value), renderValue(quota), percent);
    }

    function renderQuotas(status) {
        setBar('jellyplayQuotaKeysBar', 'jellyplayQuotaKeysText', status.keys, status.quotaKeys, function (v) { return v; });
        setBar('jellyplayQuotaBytesBar', 'jellyplayQuotaBytesText', status.bytes, status.quotaBytes, formatBytes);

        var body = document.getElementById('jellyplayNamespaceBody');
        body.innerHTML = '';
        (status.namespaces || []).forEach(function (ns) {
            var row = document.createElement('tr');
            [ns.ns, ns.keys, formatBytes(ns.bytes)].forEach(function (value) {
                var cell = document.createElement('td');
                cell.textContent = value;
                row.appendChild(cell);
            });
            body.appendChild(row);
        });
        document.getElementById('jellyplayNamespaceEmpty').style.display = (status.namespaces || []).length === 0 ? '' : 'none';
    }

    function renderDevices(devices) {
        var body = document.getElementById('jellyplayDeviceBody');
        body.innerHTML = '';
        (devices || []).forEach(function (device) {
            var row = document.createElement('tr');
            [device.name, device.platform, device.model || '', device.appVersion, formatTime(device.lastSeen)].forEach(function (value) {
                var cell = document.createElement('td');
                cell.textContent = value;
                row.appendChild(cell);
            });

            var actionCell = document.createElement('td');
            if (device.revoked) {
                var badge = document.createElement('span');
                badge.className = 'jellyplay-badge revoked';
                badge.textContent = strings.DeviceRevoked || 'Revoked';
                actionCell.appendChild(badge);
            } else {
                var revoke = document.createElement('button');
                revoke.type = 'button';
                revoke.className = 'raised emby-button jellyplay-remove';
                revoke.textContent = strings.DeviceRevoke || 'Revoke';
                revoke.addEventListener('click', function () {
                    apiDelete('jellyplay/admin/sync/user/' + encodeURIComponent(drilldownUser) + '/devices/' + encodeURIComponent(device.deviceId)).then(function () {
                        return loadDrilldown();
                    }).catch(function (error) {
                        alertFmt('DeviceRevokeFailed', 'Revoke failed: {0}', error.message || error);
                    });
                });
                actionCell.appendChild(revoke);
            }
            row.appendChild(actionCell);
            body.appendChild(row);
        });
        document.getElementById('jellyplayDeviceEmpty').style.display = (devices || []).length === 0 ? '' : 'none';
    }

    function buildHistoryEntry(entry) {
        var wrap = document.createElement('div');
        wrap.className = 'jellyplay-row jellyplay-op';

        var toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'jellyplay-op-toggle';
        var heading = fmt('HistoryEntrySummary', '#{0} · {1} · {2} · device {3} · {4} applied, {5} rejected · {6}',
            entry.seq, opLabel(entry.op), formatTime(entry.ts), entry.deviceId || '—', entry.keysApplied, entry.keysRejected, (entry.keys || []).length);
        toggle.textContent = heading;
        wrap.appendChild(toggle);

        var details = document.createElement('div');
        details.className = 'jellyplay-op-keys';
        details.style.display = 'none';
        if ((entry.rejects || []).length > 0) {
            entry.rejects.forEach(function (reject) {
                var line = document.createElement('div');
                line.textContent = fmt('HistoryRejectLine', 'rejected {0}/{1}: {2}', reject.ns, reject.key, reject.reason);
                line.style.color = '#ff6c6c';
                details.appendChild(line);
            });
        }
        (entry.keys || []).forEach(function (key) {
            var line = document.createElement('div');
            line.textContent = key.ns + '/' + key.key + ' — ' + formatTime(key.updatedAt);
            details.appendChild(line);
        });
        if ((entry.keys || []).length === 0 && (entry.rejects || []).length === 0) {
            var line = document.createElement('div');
            line.textContent = strings.HistoryNoKeys || 'No per-key changes recorded.';
            details.appendChild(line);
        }

        toggle.addEventListener('click', function () {
            details.style.display = details.style.display === 'none' ? '' : 'none';
        });

        wrap.appendChild(details);
        return wrap;
    }

    function renderHistory(history) {
        var list = document.getElementById('jellyplayHistoryList');
        list.innerHTML = '';
        (history || []).forEach(function (entry) {
            list.appendChild(buildHistoryEntry(entry));
        });
        document.getElementById('jellyplayHistoryEmpty').style.display = (history || []).length === 0 ? '' : 'none';
    }

    function loadDrilldown() {
        var userId = document.getElementById('jellyplaySyncUser').value;
        if (!userId) { return Promise.resolve(); }
        drilldownUser = userId;
        return apiGet('jellyplay/admin/sync/user/' + encodeURIComponent(userId)).then(function (response) {
            document.getElementById('jellyplayDrilldown').style.display = '';
            renderQuotas(response.status);
            renderDevices(response.devices);
            return apiGet('jellyplay/admin/sync/export?userId=' + encodeURIComponent(userId) + '&format=json&limit=50').then(function (exported) {
                renderHistory(exported.history);
            });
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error.message || error);
        });
    }

    function downloadExport(extension) {
        if (!drilldownUser) { return; }
        fetch(window.ApiClient.getUrl('jellyplay/admin/sync/export?userId=' + encodeURIComponent(drilldownUser) + '&format=' + extension), { headers: authHeaders() }).then(function (response) {
            if (!response.ok) { throw new Error(String(response.status)); }
            return response.blob();
        }).then(function (blob) {
            var link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = 'jellyplay-sync-' + drilldownUser + '-' + new Date().toISOString().slice(0, 10) + '.' + extension;
            link.click();
            URL.revokeObjectURL(link.href);
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error.message || error);
        });
    }

    // ── resolved preview simulator ──

    function renderPreview(resolved) {
        document.getElementById('jellyplayPreviewEmpty').style.display = 'none';
        var list = document.getElementById('jellyplayPreviewList');
        list.innerHTML = '';
        var modes = resolved.modes || {};
        (resolved.settings || []).slice().sort(function (a, b) {
            return (a.ns + '/' + a.key).localeCompare(b.ns + '/' + b.key);
        }).forEach(function (entry) {
            var row = document.createElement('div');
            row.className = 'jellyplay-row';
            var mode = modes[entry.ns + '/' + entry.key] || 'unset';
            var label = document.createElement('div');
            label.style.flex = '1 1 300px';
            var name = document.createElement('div');
            name.textContent = entry.ns + '/' + entry.key;
            var badge = document.createElement('span');
            badge.className = 'jellyplay-badge ' + mode;
            badge.textContent = mode;
            name.appendChild(badge);
            var value = document.createElement('div');
            value.className = 'jellyplay-test-status';
            value.textContent = JSON.stringify(entry.value);
            label.appendChild(name);
            label.appendChild(value);
            row.appendChild(label);
            list.appendChild(row);
        });
        if ((resolved.settings || []).length === 0) {
            var empty = document.createElement('p');
            empty.className = 'sectionDescription';
            empty.textContent = strings.PreviewNoKeys || 'Nothing to resolve — the user has no settings and no defaults apply.';
            list.appendChild(empty);
        }
    }

    function runPreview() {
        var userId = document.getElementById('jellyplayPreviewUser').value;
        if (!userId) { return; }
        var profile = document.getElementById('jellyplayPreviewProfile').value;
        apiGet('jellyplay/admin/settings/preview?userId=' + encodeURIComponent(userId) + '&profile=' + encodeURIComponent(profile))
            .then(renderPreview)
            .catch(function (error) {
                alertFmt('MsgLoadFailed', 'Failed to load: {0}', error.message || error);
            });
    }

    // ── push dry run ──

    function runDryRun() {
        var userId = document.getElementById('jellyplayDryRunUser').value;
        var path = 'jellyplay/admin/pushDefaults' + (userId ? '/' + encodeURIComponent(userId) : '') + '?dryRun=true';
        apiPost(path).then(function (outcome) {
            var result = document.getElementById('jellyplayDryRunResult');
            result.style.display = '';
            var dry = outcome && outcome.dryRun;
            if (!dry) {
                result.textContent = strings.PushDryRunUnsupported || 'The server did not return a dry-run report.';
                result.classList.add('fail');
                return;
            }

            result.classList.remove('fail');
            var lines = [fmt('PushDryRunSummary', 'Would apply {0} key(s); {1} would be rejected (stale) across {2} user(s). Nothing was written.',
                dry.wouldApply, dry.wouldReject, outcome.users)];
            (dry.problems || []).forEach(function (problem) {
                lines.push(fmt('PushDryRunProblem', 'Catalog problem: {0}', problem));
            });
            (dry.wouldRejects || []).forEach(function (reject) {
                lines.push(fmt('PushDryRunRejectLine', '{0}: {1}/{2} — {3}', userName(reject.userId), reject.ns, reject.key, reject.reason));
            });
            result.textContent = lines.join('\n');
        }).catch(function (error) {
            alertFmt('MsgLoadFailed', 'Failed to load: {0}', error.message || error);
        });
    }

    // ── wiring ──

    ensureStyles();
    applyStrings().then(function () {
        loadUsers();
    });

    document.getElementById('jellyplayLiveConnect').addEventListener('click', connectLive);
    document.getElementById('jellyplayLiveDisconnect').addEventListener('click', disconnectLive);
    document.getElementById('jellyplaySyncLoad').addEventListener('click', loadDrilldown);
    document.getElementById('jellyplayHistoryCsv').addEventListener('click', function () { downloadExport('csv'); });
    document.getElementById('jellyplayHistoryJson').addEventListener('click', function () { downloadExport('json'); });
    document.getElementById('jellyplayPreviewRun').addEventListener('click', runPreview);
    document.getElementById('jellyplayDryRunRun').addEventListener('click', runDryRun);
})();
