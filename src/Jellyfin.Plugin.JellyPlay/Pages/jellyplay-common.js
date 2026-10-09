(function () {
    'use strict';

    // The ONE client commons behind every JellyPlay dashboard page: auth
    // headers, the wire error-body contract ({ error, message, problems[] }),
    // the fetch wrappers, and the i18n pass. Pages add page logic only —
    // no private copies of this surface (they drifted once; the copies are
    // gone on purpose).

    var strings = {};

    function authHeaders(extra) {
        var token = window.ApiClient.accessToken();
        var auth = 'MediaBrowser Client="JellyPlay Dashboard", Device="Dashboard", DeviceId="jellyplay-dashboard", Version="1.0", Token="' + token + '"';
        var headers = { 'Authorization': auth, 'X-Emby-Authorization': auth };
        for (var key in (extra || {})) { headers[key] = extra[key]; }
        return headers;
    }

    // Pulls a readable message out of an error body. The full wire contract:
    // { error: "code" } always, plus { message } and { problems: [...] } on
    // validation failures. The problems ride on the thrown Error
    // (`error.problems`) so callers can anchor them back to offending rows.
    function extractErrorDetail(text, status) {
        try {
            var parsed = JSON.parse(text);
            if (parsed && Array.isArray(parsed.problems) && parsed.problems.length > 0) {
                var error = new Error(parsed.problems.join(' '));
                error.problems = parsed.problems;
                return error;
            }
            if (parsed && typeof parsed.error === 'string' && parsed.error) {
                return new Error(parsed.error);
            }
            if (parsed && typeof parsed.message === 'string' && parsed.message) {
                return new Error(parsed.message);
            }
        } catch (ignored) { }
        return new Error(String(status));
    }

    function handleFailure(response) {
        return response.text().then(function (text) {
            throw extractErrorDetail(text, response.status);
        });
    }

    function apiGet(path) {
        return fetch(window.ApiClient.getUrl(path), { headers: authHeaders() }).then(function (response) {
            if (!response.ok) {
                return handleFailure(response);
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
                return handleFailure(response);
            }
            return response.status === 204 ? null : response.json();
        });
    }

    // DELETE tolerates 404 (an already-gone target is a successful delete).
    function apiDelete(path) {
        return fetch(window.ApiClient.getUrl(path), { method: 'DELETE', headers: authHeaders() }).then(function (response) {
            if (!response.ok && response.status !== 404) {
                return handleFailure(response);
            }
            return null;
        });
    }

    // fmt(key, fallback, value0, value1, ...): the localized text with every
    // occurrence of {n} substituted from valueN. One convention for all pages.
    function fmt(key, fallback) {
        var text = strings[key] || fallback;
        for (var i = 2; i < arguments.length; i++) {
            text = String(text).split('{' + (i - 2) + '}').join(String(arguments[i]));
        }
        return text;
    }

    function alertFmt(key, fallback) {
        var args = Array.prototype.slice.call(arguments);
        window.Dashboard.alert(fmt.apply(null, args));
    }

    function alertText(key, fallback) {
        window.Dashboard.alert(fmt.apply(null, arguments));
    }

    // Loads the localized string table and runs the data-i18n pass. The
    // strings object is mutated IN PLACE — page-held references stay live.
    // The static en markup stands on its own; localization is cosmetic.
    function applyStrings() {
        return apiGet('jellyplay/dashboard-strings?lang=' + encodeURIComponent(navigator.language || '')).then(function (table) {
            table = table || {};
            Object.keys(strings).forEach(function (key) { delete strings[key]; });
            Object.keys(table).forEach(function (key) { strings[key] = table[key]; });
            Array.prototype.forEach.call(document.querySelectorAll('[data-i18n]'), function (element) {
                var value = strings[element.getAttribute('data-i18n')];
                if (typeof value === 'string' && value.length > 0) { element.textContent = value; }
            });
        }).catch(function () {
        });
    }

    window.JellyPlayCommon = {
        strings: strings,
        authHeaders: authHeaders,
        extractErrorDetail: extractErrorDetail,
        apiGet: apiGet,
        apiPost: apiPost,
        apiDelete: apiDelete,
        fmt: fmt,
        alertFmt: alertFmt,
        alertText: alertText,
        applyStrings: applyStrings
    };
})();
