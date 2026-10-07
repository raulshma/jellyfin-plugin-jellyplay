using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>Tri-state admin default modes (GLOSSARY "Admin defaults").</summary>
public enum AdminDefaultMode
{
    /// <summary>Always overwrites whatever the user has.</summary>
    Forced,

    /// <summary>Fills keys the user has not set; never overwrites.</summary>
    Suggested
}

/// <summary>One tri-state default that applies to a user: <see cref="Mode"/> drives the write/overwrite decision.</summary>
public sealed record ResolvedDefault(string Ns, string Key, AdminDefaultMode Mode, JsonElement Value);

/// <summary>
/// The ONE parser and merger for the tri-state admin-defaults envelope
/// (<c>{"ns/key": {"mode": "forced"|"suggested", "value": ...}}</c>), shared by
/// the read side (SettingsService.ResolveProfile) and the write side
/// (AdminDefaultsService.PushDefaults). Pure — no storage, no host — so the
/// precedence (forced overwrites always, suggested fills gaps only, per-user
/// scope beats global, a user's own value beats suggested) is table-testable.
/// Also the one home of the <c>ns/key</c> composite spelling (<see cref="Split"/>/
/// <see cref="Join"/>): the namespace is the FIRST path segment, the key is the
/// remainder (it may itself contain '/').
/// </summary>
public static class DefaultsEnvelope
{
    /// <summary>
    /// Splits a <c>ns/key</c> composite at its FIRST '/': the namespace is the
    /// first path segment, the key is the remainder. An empty namespace (no '/'
    /// or a leading one) marks the composite unusable — callers treat it as a
    /// skip.
    /// </summary>
    internal static (string Ns, string Key) Split(string composite)
    {
        var separator = composite.IndexOf('/', StringComparison.Ordinal);
        return separator <= 0
            ? (string.Empty, composite)
            : (composite[..separator], composite[(separator + 1)..]);
    }

    /// <summary>The <c>ns/key</c> composite spelling — the inverse of <see cref="Split"/> for a non-empty namespace.</summary>
    internal static string Join(string ns, string key) => ns + "/" + key;

    /// <summary>
    /// Parses one <c>{mode, value}</c> envelope: the mode must be exactly
    /// "forced" or "suggested" and a value property must be present (it is
    /// carried as parsed JSON, never re-serialized). Anything else — a
    /// non-object entry, a missing or unknown mode, a missing value — is
    /// rejected.
    /// </summary>
    public static bool TryParse(JsonElement envelope, out AdminDefaultMode mode, out JsonElement value)
    {
        mode = default;
        value = default;
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("mode", out var modeElement)
            || !envelope.TryGetProperty("value", out value))
        {
            return false;
        }

        switch (modeElement.GetString())
        {
            case "forced":
                mode = AdminDefaultMode.Forced;
                return true;
            case "suggested":
                mode = AdminDefaultMode.Suggested;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// READ path (SettingsService.ResolveProfile): resolves the defaults that
    /// apply to one user — the global scope first, then the per-user scope; a
    /// per-user declaration carrying a mode replaces the global entry for the
    /// same key wholesale (user scope beats global, whatever the two modes
    /// say), and entries without a usable <c>ns/key</c> shape are skipped.
    /// Keys present in <paramref name="existingKeys"/> are the user's own
    /// values: forced ones still apply (forced overwrites always), suggested
    /// ones are dropped (suggested fills gaps only). The returned values are
    /// the stored envelope values verbatim. A malformed per-user entry is
    /// simply ignored (it cannot shadow the global default).
    /// </summary>
    public static List<ResolvedDefault> MergeForRead(JsonElement? globalDefaults, JsonElement? perUserDefaults, IReadOnlySet<string> existingKeys)
        => Merge(globalDefaults, perUserDefaults, existingKeys, shadowModelessPerUserDefaults: false);

    /// <summary>
    /// WRITE path (AdminDefaultsService.PushDefaults): the same precedence as
    /// <see cref="MergeForRead"/>, with the conservative shadow rule on — a
    /// per-user object entry WITHOUT a usable mode still shadows the global
    /// default for its key (and is itself not pushed): a broken per-user
    /// override must block the push, not let the global forced default fall
    /// through.
    /// </summary>
    public static List<ResolvedDefault> MergeForPush(JsonElement? globalDefaults, JsonElement? perUserDefaults, IReadOnlySet<string> existingKeys)
        => Merge(globalDefaults, perUserDefaults, existingKeys, shadowModelessPerUserDefaults: true);

    /// <summary>The one precedence core both entry points share; the shadow switch is private detail, never a call-site decision.</summary>
    private static List<ResolvedDefault> Merge(JsonElement? globalDefaults, JsonElement? perUserDefaults, IReadOnlySet<string> existingKeys, bool shadowModelessPerUserDefaults)
    {
        var resolved = new List<ResolvedDefault>();
        foreach (var (compositeKey, envelope) in MergeScopes(globalDefaults, perUserDefaults, shadowModelessPerUserDefaults))
        {
            var (ns, key) = Split(compositeKey);
            if (ns.Length == 0 || !TryParse(envelope, out var mode, out var value))
            {
                continue;
            }

            if (mode == AdminDefaultMode.Forced || !existingKeys.Contains(compositeKey))
            {
                resolved.Add(new ResolvedDefault(ns, key, mode, value));
            }
        }

        return resolved;
    }

    /// <summary>Per-key winning envelope after the scope merge (see <see cref="Merge"/> for the shadow rule).</summary>
    private static Dictionary<string, JsonElement> MergeScopes(JsonElement? globalDefaults, JsonElement? perUserDefaults, bool shadowModelessPerUserDefaults)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, envelope) in EnumerateEnvelopes(globalDefaults))
        {
            merged[key] = envelope;
        }

        foreach (var (key, envelope) in EnumerateEnvelopes(perUserDefaults, shadowModelessPerUserDefaults))
        {
            merged[key] = envelope;
        }

        return merged;
    }

    /// <summary>Object entries participate in the merge (and can shadow the other scope). By default only entries carrying a mode; <paramref name="includeModelessEntries"/> is the write path's conservative shadow-everything rule. Everything else is ignored.</summary>
    private static IEnumerable<(string Key, JsonElement Envelope)> EnumerateEnvelopes(JsonElement? payload, bool includeModelessEntries = false)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && (includeModelessEntries || property.Value.TryGetProperty("mode", out _)))
            {
                yield return (property.Name, property.Value);
            }
        }
    }
}
