using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyPlay;

/// <summary>
/// The JellyPlay plugin. The constructor must never throw — Jellyfin removes
/// plugins whose construction fails — so anything fallible is lazy.
/// </summary>
public class JellyPlayPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Lazy<JellyPlayDatabase> _database;

    public JellyPlayPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "plugins", "JellyPlay");
        _database = new Lazy<JellyPlayDatabase>(() => new JellyPlayDatabase(applicationPaths.DataPath));
    }

    public JellyPlayDatabase Database => _database.Value;

    public override string Name => "JellyPlay";

    /// <summary>Shown on the dashboard; also re-persisted into meta.json on every load
    /// (PluginManager syncs manifest fields from the instance), so it must match the
    /// description shipped in the package meta.json.</summary>
    public override string Description => "Companion plugin for the JellyPlay client: settings/profile sync, admin defaults, events and messages, Seerr SSO bridge and proxy, newsletter backend, ratings aggregation, custom/seasonal home rows, anime markers, recommendations, book bookmarks and transcode insights.";

    public override Guid Id => Guid.Parse(JellyPlayContract.PluginId);

    /// <summary>Plugin data directory (database, file caches, backups).</summary>
    public string DataDirectory { get; private set; } = string.Empty;

    public static JellyPlayPlugin? Instance { get; private set; }

    /// <summary>
    /// Both config write paths funnel through here — the dashboard form via the
    /// host's POST /Plugins/{id}/Configuration and the YAML editor via
    /// POST jellyplay/config/yaml — so the dashboard's "auto-generate if empty"
    /// promise for the Seerr webhook secret is enforced in one place.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config
            && string.IsNullOrWhiteSpace(config.Seerr.WebhookSecret))
        {
            config.Seerr.WebhookSecret = NormalizeWebhookSecret(null);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>Pure webhook-secret normalizer: keeps a real value, generates otherwise (unit-tested).</summary>
    internal static string NormalizeWebhookSecret(string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? Guid.NewGuid().ToString("N") : candidate;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = "JellyPlay",
                EmbeddedResourcePath = prefix + ".Pages.index.html"
            },
            new PluginPageInfo
            {
                Name = "JellyPlay.js",
                EmbeddedResourcePath = prefix + ".Pages.index.js"
            },
            new PluginPageInfo
            {
                Name = "JellyPlayCommon.js",
                EmbeddedResourcePath = prefix + ".Pages.jellyplay-common.js"
            },
            new PluginPageInfo
            {
                Name = "JellyPlayYaml",
                EmbeddedResourcePath = prefix + ".Pages.yaml.html"
            },
            new PluginPageInfo
            {
                Name = "JellyPlayYaml.js",
                EmbeddedResourcePath = prefix + ".Pages.yaml.js"
            },
            new PluginPageInfo
            {
                Name = "JellyPlaySync",
                EmbeddedResourcePath = prefix + ".Pages.sync.html"
            },
            new PluginPageInfo
            {
                Name = "JellyPlaySync.js",
                EmbeddedResourcePath = prefix + ".Pages.sync.js"
            }
        ];
    }

    public override void OnUninstalling()
    {
        base.OnUninstalling();
        if (_database.IsValueCreated)
        {
            _database.Value.Dispose();
        }
    }
}
