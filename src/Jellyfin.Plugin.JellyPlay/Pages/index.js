(function () {
    'use strict';

    const form = document.getElementById('jellyplayConfigForm');
    if (!form) {
        return;
    }

    // Scalar paths of PluginConfiguration surfaced on the form.
    const fields = {
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

    function getPath(obj, path) {
        return path.split('.').reduce(function (acc, key) { return acc ? acc[key] : undefined; }, obj);
    }

    function setPath(obj, path, value) {
        const parts = path.split('.');
        let cursor = obj;
        for (let i = 0; i < parts.length - 1; i++) {
            if (!cursor[parts[i]]) { cursor[parts[i]] = {}; }
            cursor = cursor[parts[i]];
        }
        cursor[parts[parts.length - 1]] = value;
    }

    function load() {
        window.ApiClient.getPluginConfiguration('d3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c').then(function (config) {
            Object.keys(fields).forEach(function (id) {
                const element = document.getElementById(id);
                if (!element) { return; }
                const value = getPath(config, fields[id]);
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
        window.ApiClient.getPluginConfiguration('d3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c').then(function (config) {
            Object.keys(fields).forEach(function (id) {
                const element = document.getElementById(id);
                if (!element) { return; }
                const value = element.type === 'checkbox' ? element.checked : element.value;
                setPath(config, fields[id], element.type === 'number' ? Number(value) : value);
            });
            window.ApiClient.updatePluginConfiguration('d3f1a6c8-5b2e-4d7f-9a0c-6e8b1f4d2a7c', config).then(function () {
                window.Dashboard.alert('Settings saved.');
            });
        });
        return false;
    });

    load();
})();
