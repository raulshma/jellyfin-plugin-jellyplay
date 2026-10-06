using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;

namespace Jellyfin.Plugin.JellyPlay.Helpers;

/// <summary>
/// The dashboard's string table — the single home of the config pages' user
/// facing text (Resources/DashboardStrings.resx, en first). The pages carry
/// the same en strings as static fallbacks and re-resolve their
/// <c>data-i18n</c> elements through GET jellyplay/dashboard-strings, so a
/// failed fetch degrades to the embedded en text rather than to missing UI.
/// </summary>
public static class DashboardStrings
{
    private const string BaseName = "Jellyfin.Plugin.JellyPlay.Resources.DashboardStrings";

    /// <summary>Resolves one key for the culture, falling back to the key itself.</summary>
    public static string Get(string key, CultureInfo? culture = null)
        => Manager.GetString(key, culture ?? CultureInfo.CurrentUICulture) ?? key;

    private static readonly ResourceManager Manager = new(BaseName, typeof(DashboardStrings).Assembly);

    /// <summary>
    /// The whole table for a culture (parent cultures folded in). A FRESH
    /// ResourceManager per call: the shared instance's per-culture set cache
    /// poisons the invariant set after one culture-miss fallback (a 'de'
    /// request makes the next en request return an empty table — observed, not
    /// hypothetical). Never throws — an unreadable set degrades to just the
    /// invariant fallback.
    /// </summary>
    public static IReadOnlyDictionary<string, string> All(CultureInfo? culture = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var set = new ResourceManager(BaseName, typeof(DashboardStrings).Assembly)
                .GetResourceSet(culture ?? CultureInfo.CurrentUICulture, createIfNotExists: true, tryParents: true);
            if (set is null)
            {
                return result;
            }

            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    result[key] = value;
                }
            }
        }
        catch
        {
            // A dashboard cosmetic — never fail the request over it.
        }

        return result;
    }

    /// <summary>Parses a dashboard-requested language tag; anything unknown falls back to the current UI culture.</summary>
    public static CultureInfo ResolveCulture(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return CultureInfo.CurrentUICulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(lang);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentUICulture;
        }
    }
}
