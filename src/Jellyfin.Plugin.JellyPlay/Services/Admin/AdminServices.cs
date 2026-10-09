using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Services.Settings;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>
/// Pushes tri-state defaults into user accounts. Forced keys overwrite whatever
/// the user has; suggested keys only fill gaps. Scope "global" merges with any
/// per-user overrides already stored. A restore-point snapshot is captured
/// before each user's push (best-effort), so an admin can always roll back.
/// </summary>
public sealed class AdminDefaultsService
{
    private readonly SettingsService _settings;
    private readonly JellyPlayDatabase _db;
    private readonly SnapshotService _snapshots;
    private readonly ILogger<AdminDefaultsService> _logger;
    private readonly TimeProvider _clock;

    public AdminDefaultsService(SettingsService settings, JellyPlayDatabase db, SnapshotService snapshots, ILogger<AdminDefaultsService> logger, TimeProvider? clock = null)
    {
        _settings = settings;
        _db = db;
        _snapshots = snapshots;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Per-key would-rejects are capped at this many per dry run (counts stay authoritative).</summary>
    internal const int MaxDryRunRejects = 100;

    /// <summary>
    /// Per-user outcome counts. The additive <see cref="DryRun"/> payload is
    /// is present only on dry runs (<c>?dryRun=true</c>), which never write:
    /// <c>keysPushed</c> is 0 there and the report carries would-apply /
    /// would-reject counts plus the catalog-validation problems of the stored
    /// defaults.
    /// </summary>
    public sealed record PushOutcome(int Users, int KeysPushed, PushDryRun? DryRun = null);

    /// <summary>The dry-run report: what a real push would do, computed without touching any row.</summary>
    public sealed record PushDryRun(int WouldApply, int WouldReject, IReadOnlyList<PushDryRunReject> WouldRejects, IReadOnlyList<string> Problems);

    public sealed record PushDryRunReject(string UserId, string Ns, string Key, string Reason);

    /// <summary>Per-user outcome counts; <c>dryRun</c> simulates the push and writes nothing.</summary>
    public PushOutcome PushDefaults(string? userId, bool dryRun = false)
    {
        var targets = string.IsNullOrEmpty(userId)
            ? _db.GetDistinctSettingUserIds()
            : new List<string> { userId! };

        var globalDefaults = _settings.GetAdminDefaultsRaw(SettingsService.GlobalDefaultsScope);

        if (dryRun)
        {
            return DryRunPush(targets, globalDefaults);
        }

        var pushed = 0;
        foreach (var target in targets)
        {
            pushed += PushMerged(target, globalDefaults, _settings.GetAdminDefaultsRaw(target));
        }

        return new PushOutcome(targets.Count, pushed);
    }

    /// <summary>
    /// Simulates the push over every target without writing: the same merged
    /// write set a real push would produce (scope merge + shadow rule), each
    /// write judged by the same LWW rule the batch pipeline applies (strictly
    /// newer than the stored value applies; equal or older rejects
    /// stale-write), plus the catalog-validation problems of the stored
    /// defaults maps. Quota rejections are not simulated — the defaults are
    /// catalog-sized and the per-user footprint is checked live on the real
    /// push.
    /// </summary>
    private PushOutcome DryRunPush(IReadOnlyList<string> targets, JsonElement? globalDefaults)
    {
        var problems = new List<string>();
        if (globalDefaults is { } global)
        {
            problems.AddRange(SettingsService.ValidateAgainstCatalog(global));
        }

        var rejects = new List<PushDryRunReject>();
        var wouldApply = 0;
        var wouldReject = 0;

        foreach (var target in targets)
        {
            var perUserDefaults = _settings.GetAdminDefaultsRaw(target);
            if (perUserDefaults is { } payload)
            {
                problems.AddRange(SettingsService.ValidateAgainstCatalog(payload));
            }

            var (writes, existing) = BuildPushWrites(target, globalDefaults, perUserDefaults);
            foreach (var write in writes)
            {
                // The exact LWW rule the batch pipeline applies
                // (<see cref="Storage.Models.SettingsLww"/>): strictly newer
                // applies; equal or older rejects stale-write.
                if (existing.TryGetValue(DefaultsEnvelope.Join(write.Ns, write.Key), out var current)
                    && !SettingsLww.WouldApply(write.UpdatedAt, current.UpdatedAt))
                {
                    wouldReject++;
                    if (rejects.Count < MaxDryRunRejects)
                    {
                        rejects.Add(new PushDryRunReject(target, write.Ns, write.Key, "stale-write"));
                    }

                    continue;
                }

                wouldApply++;
            }
        }

        return new PushOutcome(
            targets.Count,
            0,
            new PushDryRun(wouldApply, wouldReject, rejects, problems));
    }

    /// <summary>
    /// Pushes the scope-merged defaults to one user. The precedence lives in
    /// <see cref="DefaultsEnvelope.MergeForPush"/> (forced overwrites always,
    /// suggested fills gaps only, per-user scope beats global, the user's own
    /// value beats suggested); this method only orchestrates the resulting
    /// writes through the settings pipeline (LWW batch, change log, SSE). The
    /// conservative write-path shadow rule is on: a malformed per-user
    /// override blocks the push for its key instead of letting the global
    /// default fall through.
    /// </summary>
    private int PushMerged(string userId, JsonElement? globalDefaults, JsonElement? perUserDefaults)
    {
        var (writes, _) = BuildPushWrites(userId, globalDefaults, perUserDefaults);

        if (writes.Count == 0)
        {
            return 0;
        }

        // Restore point before the overwrite (best-effort; Create never throws).
        _snapshots.Create(userId, "admin-push");

        var result = _settings.ApplyRawWrites(userId, JellyPlayDatabase.BaseProfile, "admin-push", writes);
        _logger.LogInformation("Pushed {Applied}/{Total} defaults to user {UserId}", result.Applied.Count, writes.Count, userId);
        return result.Applied.Count;
    }

    /// <summary>
    /// The merged write set a push would issue for one user (server-now
    /// stamped, serialized straight to its stored bytes) together with the
    /// user's current base-profile rows — shared verbatim by the real push and
    /// the dry-run simulation so the two can never disagree about WHAT would
    /// be written.
    /// </summary>
    private (List<SettingWrite> Writes, Dictionary<string, Api.SettingsEntryDto> Existing) BuildPushWrites(
        string userId,
        JsonElement? globalDefaults,
        JsonElement? perUserDefaults)
    {
        var baseSnapshot = _settings.GetAll(userId, JellyPlayDatabase.BaseProfile);
        var existing = baseSnapshot.Settings.ToDictionary(entry => DefaultsEnvelope.Join(entry.Ns, entry.Key), entry => entry);

        var writes = new List<SettingWrite>();
        foreach (var entry in DefaultsEnvelope.MergeForPush(
                     globalDefaults,
                     perUserDefaults,
                     existing.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            var hasExisting = existing.TryGetValue(DefaultsEnvelope.Join(entry.Ns, entry.Key), out var current);
            writes.Add(new SettingWrite(
                entry.Ns,
                entry.Key,
                hasExisting ? current.SchemaVersion : 1,
                _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                "admin-push",
                JsonSerializer.SerializeToUtf8Bytes(entry.Value)));
        }

        return (writes, existing);
    }
}

/// <summary>JSON backup/restore of plugin state: admin defaults, messages, and plugin XML config.</summary>
public sealed class ConfigBackupService
{
    private readonly JellyPlayDatabase _db;
    private readonly ILogger<ConfigBackupService> _logger;

    public ConfigBackupService(JellyPlayDatabase db, ILogger<ConfigBackupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public byte[] CreateBackup()
    {
        var backup = new BackupPayload
        {
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PluginVersion = typeof(JellyPlayPlugin).Assembly.GetName().Version?.ToString() ?? "0.11.3",
            AdminDefaults = _db.GetAllAdminDefaults().ToList(),
            Messages = _db.GetMessages().ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(backup);
    }

    public RestoreOutcome Restore(byte[] payload)
    {
        try
        {
            var backup = JsonSerializer.Deserialize<BackupPayload>(payload)
                ?? throw new JsonException("Backup payload is empty.");
            _db.RestoreAdminDefaults(backup.AdminDefaults ?? new List<AdminDefaultsRow>());
            _db.RestoreMessages(backup.Messages ?? new List<MessageRow>());
            return new RestoreOutcome(true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config restore failed");
            return new RestoreOutcome(false, ex.Message);
        }
    }

    public sealed class BackupPayload
    {
        public long CreatedAt { get; set; }
        public string PluginVersion { get; set; } = string.Empty;
        public List<AdminDefaultsRow> AdminDefaults { get; set; } = new();
        public List<MessageRow> Messages { get; set; } = new();
    }

    public sealed record RestoreOutcome(bool Success, string Error);
}

/// <summary>
/// The dashboard's YAML editor format: the WHOLE plugin configuration as
/// camelCase YAML. Serialization and parse-validate are admin service logic;
/// persisting the round-trip stays a plugin-lifecycle concern at the caller.
/// </summary>
public static class ConfigYaml
{
    public static string Serialize(PluginConfiguration config)
        => new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Serialize(config);

    /// <summary>Parses admin-edited YAML into a full configuration (unmatched keys ignored); throws on invalid YAML.</summary>
    public static PluginConfiguration Parse(string yaml)
        => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<PluginConfiguration>(yaml);
}
