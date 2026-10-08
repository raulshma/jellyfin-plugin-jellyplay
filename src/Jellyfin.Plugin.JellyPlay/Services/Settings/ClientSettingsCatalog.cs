using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// One known client setting. The catalog is GENERATED, never hand-maintained:
/// the JellyPlay client's settings-catalog generator derives it from the same
/// PreferenceSpec declarations the client persists through (run
/// <c>./gradlew :shared:core:datastore:generateSettingsCatalog</c> in the
/// client repo and commit the artifact), so key names, types, enum
/// vocabularies and defaults can never drift from what the client actually
/// reads and writes. The dashboard renders its editors from it (GET
/// jellyplay/settings/catalog) and the admin-defaults write path validates
/// values against it. Unknown keys remain legal — the settings namespace is
/// client-defined and forward-compatible (a newer client against an older
/// plugin) — the catalog only documents what is known today.
/// </summary>
public sealed record ClientSettingDescriptor
{
    /// <summary>Settings namespace (first path segment of "ns/key").</summary>
    public required string Ns { get; init; }

    /// <summary>Setting key (second path segment of "ns/key").</summary>
    public required string Key { get; init; }

    /// <summary>Human-facing name for the dashboard.</summary>
    public required string Label { get; init; }

    /// <summary>One-line explanation shown as field help.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>boolean | number | enum | string | json.</summary>
    public required string ValueType { get; init; }

    /// <summary>Recommended default (type must match <see cref="ValueType"/>).</summary>
    public JsonElement? DefaultValue { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>Allowed values when <see cref="ValueType"/> is "enum".</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>The composite "ns/key" identifier used everywhere on the wire.</summary>
    public string Id => Ns + "/" + Key;
}

/// <summary>Raised when an admin-defaults payload fails catalog validation.</summary>
public sealed class SettingsCatalogValidationException : Exception
{
    public SettingsCatalogValidationException(IReadOnlyList<string> problems)
        : base(string.Join(' ', problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

public static class ClientSettingsCatalog
{
    /// <summary>
    /// Wire schema of the embedded artifact. Bump only on breaking shape
    /// changes; the generator stamps the same constant
    /// (SettingsCatalogGenerator.CATALOG_SCHEMA client-side).
    /// </summary>
    public const int CatalogSchema = 1;

    /// <summary>
    /// Load-time backstop mirroring the generator's denylist: secrets and
    /// device identity must never be advertised even if the generator's own
    /// guard regressed — a catalog carrying them fails the load rather than
    /// ship. The generator (derived from the client's SyncExcludedKeys sets)
    /// remains the policy source; this list covers only the critical few.
    /// </summary>
    private static readonly string[] SecretKeyDenylist = ["pin_hash", "active_server_id", "active_user_id", "device_id"];

    /// <summary>
    /// The known client settings, loaded once from the embedded
    /// Resources/jellyplay-settings-catalog.json artifact. Secrets and device
    /// identity keys are structurally absent from the generator's allowlist —
    /// a catalog that somehow carries them fails the load rather than ship.
    /// </summary>
    public static readonly IReadOnlyList<ClientSettingDescriptor> KnownSettings = LoadEmbeddedCatalog();

    /// <summary>
    /// KnownSettings keyed by (Ns, Key), built once — validation hits this per
    /// property, the linear scan it replaced did not scale. A duplicate id
    /// keeps the FIRST entry (the old linear FirstOrDefault's tolerance), so
    /// the index can never throw at load.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Ns, string Key), ClientSettingDescriptor> IndexById =
        KnownSettings
            .GroupBy(descriptor => (descriptor.Ns, descriptor.Key))
            .ToDictionary(group => group.Key, group => group.First());

    public static ClientSettingDescriptor? Find(string ns, string key)
        => IndexById.TryGetValue((ns, key), out var descriptor) ? descriptor : null;

    /// <summary>
    /// Validates one admin-defaults value against the catalog. Unknown keys
    /// pass (client-defined namespace); known keys must type-match and honor
    /// min/max/options. Returns a problem description, or null when valid.
    /// </summary>
    public static string? ValidateValue(ClientSettingDescriptor descriptor, JsonElement value)
    {
        switch (descriptor.ValueType)
        {
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return $"{descriptor.Id}: expected true or false.";
                }

                break;
            case "number":
                if (value.ValueKind is not (JsonValueKind.Number))
                {
                    return $"{descriptor.Id}: expected a number.";
                }

                var number = value.GetDouble();
                if (descriptor.Min is { } min && number < min)
                {
                    return $"{descriptor.Id}: must be ≥ {min}.";
                }

                if (descriptor.Max is { } max && number > max)
                {
                    return $"{descriptor.Id}: must be ≤ {max}.";
                }

                break;
            case "enum":
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (text is null || descriptor.Options is null || !descriptor.Options.Contains(text))
                {
                    return $"{descriptor.Id}: must be one of {string.Join(", ", descriptor.Options ?? [])}.";
                }

                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return $"{descriptor.Id}: expected a string.";
                }

                break;
        }

        // "json" and anything unrecognized accept any value.
        return null;
    }

    private static IReadOnlyList<ClientSettingDescriptor> LoadEmbeddedCatalog()
    {
        var assembly = typeof(ClientSettingsCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("jellyplay-settings-catalog.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "JellyPlay settings catalog artifact is missing — the build must embed Resources/jellyplay-settings-catalog.json.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open the embedded settings catalog artifact.");

        var artifact = JsonSerializer.Deserialize<CatalogArtifact>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The embedded settings catalog artifact is empty.");

        if (artifact.CatalogSchema != CatalogSchema)
        {
            throw new InvalidOperationException(
                $"Embedded settings catalog schema {artifact.CatalogSchema} does not match the plugin's {CatalogSchema} — regenerate the artifact.");
        }

        var settings = artifact.Settings ?? [];
        var duplicates = settings
            .GroupBy(entry => entry.Ns + "/" + entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Embedded settings catalog has duplicate ids: " + string.Join(", ", duplicates.Select(group => group.Key)));
        }

        var leaked = settings
            .Where(entry => SecretKeyDenylist.Contains(entry.Key, StringComparer.Ordinal))
            .Select(entry => entry.Ns + "/" + entry.Key)
            .ToList();
        if (leaked.Count > 0)
        {
            throw new InvalidOperationException(
                "Embedded settings catalog carries denylisted keys: " + string.Join(", ", leaked));
        }

        return settings
            .Select(entry => new ClientSettingDescriptor
            {
                Ns = entry.Ns,
                Key = entry.Key,
                Label = entry.Label,
                Description = entry.Description ?? string.Empty,
                ValueType = entry.ValueType,
                DefaultValue = entry.DefaultValue,
                Min = entry.Min,
                Max = entry.Max,
                Options = entry.Options?.AsReadOnly(),
            })
            .ToList();
    }

    /// <summary>Deserialization shape of the generated artifact (camelCase JSON).</summary>
    private sealed record CatalogArtifact(int? CatalogSchema, List<CatalogEntry>? Settings);

    private sealed record CatalogEntry(
        string Ns,
        string Key,
        string Label,
        string? Description,
        string ValueType,
        JsonElement? DefaultValue,
        double? Min,
        double? Max,
        List<string>? Options);
}
