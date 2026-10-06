(function () {
    'use strict';

    const editor = document.getElementById('yamlEditor');
    const status = document.getElementById('yamlStatus');
    if (!editor) {
        return;
    }

    let strings = {};

    function fmt(key, fallback) {
        let text = strings[key] || fallback;
        for (let i = 1; i < arguments.length; i++) {
            text = String(text).replace('{' + (i - 1) + '}', String(arguments[i]));
        }
        return text;
    }

    function api(method, path, body) {
        return window.ApiClient.ajax({
            type: method,
            url: window.ApiClient.getUrl(path),
            dataType: 'json',
            contentType: 'application/json',
            data: body ? JSON.stringify(body) : undefined
        });
    }

    function applyStrings() {
        return api('GET', '/JellyPlay/dashboard-strings?lang=' + encodeURIComponent(navigator.language || '')).then(function (table) {
            strings = table || {};
            document.querySelectorAll('[data-i18n]').forEach(function (element) {
                const value = strings[element.getAttribute('data-i18n')];
                if (typeof value === 'string' && value.length > 0) { element.textContent = value; }
            });
        }).catch(function () {
            // The static en markup stands on its own; localization is cosmetic.
        });
    }

    function reload() {
        api('GET', '/JellyPlay/config/yaml').then(function (res) {
            editor.value = (res && res.value) || '';
            status.textContent = '';
        }).catch(function () {
            status.textContent = 'Failed to load config.';
        });
    }

    document.getElementById('yamlReload').addEventListener('click', reload);

    document.getElementById('yamlSave').addEventListener('click', function () {
        api('POST', '/JellyPlay/config/yaml', { value: editor.value }).then(function (res) {
            if (res && res.error) {
                status.textContent = fmt('MsgLoadFailed', 'Failed to load: {0}', res.message);
            } else {
                status.textContent = fmt('YamlStatusOk', 'Saved.');
            }
        }).catch(function (e) {
            status.textContent = fmt('MsgLoadFailed', 'Failed to load: {0}', e);
        });
    });

    applyStrings().then(reload);
})();
