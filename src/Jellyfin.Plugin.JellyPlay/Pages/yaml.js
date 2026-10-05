(function () {
    'use strict';

    const editor = document.getElementById('yamlEditor');
    const status = document.getElementById('yamlStatus');
    if (!editor) {
        return;
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
                status.textContent = 'Error: ' + res.message;
            } else {
                status.textContent = 'Saved.';
            }
        }).catch(function (e) {
            status.textContent = 'Save failed: ' + e;
        });
    });

    reload();
})();
