(function () {
    'use strict';

    var common = window.JellyPlayCommon;
    var fmt = common.fmt;
    var apiGet = common.apiGet;
    var apiPost = common.apiPost;
    var applyStrings = common.applyStrings;

    const editor = document.getElementById('yamlEditor');
    const status = document.getElementById('yamlStatus');
    if (!editor) {
        return;
    }

    function reload() {
        apiGet('jellyplay/config/yaml').then(function (res) {
            editor.value = (res && res.value) || '';
            status.textContent = '';
        }).catch(function () {
            status.textContent = 'Failed to load config.';
        });
    }

    document.getElementById('yamlReload').addEventListener('click', reload);

    document.getElementById('yamlSave').addEventListener('click', function () {
        apiPost('jellyplay/config/yaml', { value: editor.value }).then(function (res) {
            if (res && res.error) {
                status.textContent = fmt('MsgLoadFailed', 'Failed to load: {0}', res.message);
            } else {
                status.textContent = fmt('YamlStatusOk', 'Saved.');
            }
        }).catch(function (e) {
            status.textContent = fmt('MsgLoadFailed', 'Failed to load: {0}', e && e.message ? e.message : e);
        });
    });

    applyStrings().then(reload);
})();
