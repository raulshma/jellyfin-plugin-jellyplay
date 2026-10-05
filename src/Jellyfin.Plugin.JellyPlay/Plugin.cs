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

    public override Guid Id => Guid.Parse(JellyPlayContract.PluginId);

    /// <summary>Plugin data directory (database, file caches, backups).</summary>
    public string DataDirectory { get; private set; } = string.Empty;

    public static JellyPlayPlugin? Instance { get; private set; }

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
                Name = "JellyPlayYaml",
                EmbeddedResourcePath = prefix + ".Pages.yaml.html"
            },
            new PluginPageInfo
            {
                Name = "JellyPlayYaml.js",
                EmbeddedResourcePath = prefix + ".Pages.yaml.js"
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
