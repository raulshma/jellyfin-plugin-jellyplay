using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.JellyPlay.Helpers;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// Pins the dashboard string table's invariants: every declared key resolves
/// to a non-empty en value (the pages embed the same strings as fallbacks —
/// an empty resx value would silently win over the fallback), unknown keys
/// degrade to the key itself, and an unparsable language tag falls back to
/// the UI culture instead of throwing.
/// </summary>
public class DashboardStringsTests
{
    private static readonly string[] LoadBearingKeys =
    [
        "PageTitle", "PageDescription", "SectionDefaults", "DefaultsDescription",
        "DefaultsModeSuggested", "DefaultsModeForced", "SectionBackup", "YamlPageTitle",
        "MsgSaved", "DefaultsSaved", "DefaultsPushed", "BackupRestored",
        "SectionCustomRows", "CustomRowsDescription", "CustomRowsEmpty", "CustomRowsAdd",
        "CustomRowsSave", "CustomRowsSaved", "CustomRowsTitle", "CustomRowsLimit",
        "CustomRowsRemove", "CustomRowsInvalidTitle", "CustomRowsInvalidLimit",
        "CustomRowsListIdLetterboxd", "CustomRowsListIdImdb", "CustomRowsListIdMdblist", "CustomRowsListIdTmdb",
        "SectionAnimeOverrides", "AnimeOverridesDescription", "AnimeOverridesEmpty", "AnimeOverridesAdd",
        "AnimeOverridesSave", "AnimeOverridesSaved", "AnimeOverridesSeriesId", "AnimeOverridesAniListId",
        "AnimeOverridesMalId", "AnimeOverridesLabel", "AnimeOverridesRemove",
        "AnimeOverridesInvalidSeries", "AnimeOverridesInvalidProvider"
    ];

    [Fact]
    public void TableIsCompleteAndNonEmpty()
    {
        var all = DashboardStrings.All(CultureInfo.InvariantCulture);
        Assert.True(all.Count >= 30, $"dashboard string table looks truncated: {all.Count} entries");

        foreach (var pair in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"string '{pair.Key}' is empty");
        }
    }

    [Fact]
    public void LoadBearingKeysResolve()
    {
        foreach (var key in LoadBearingKeys)
        {
            Assert.False(string.IsNullOrWhiteSpace(DashboardStrings.Get(key, CultureInfo.InvariantCulture)), $"missing key '{key}'");
        }
    }

    [Fact]
    public void UnknownKeyFallsBackToItself()
    {
        Assert.Equal("Not_A_Real_Key", DashboardStrings.Get("Not_A_Real_Key", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UnparsableLanguageTagFallsBackToUiCulture()
    {
        Assert.Equal(CultureInfo.CurrentUICulture, DashboardStrings.ResolveCulture("not a language tag!!"));
        Assert.Equal(CultureInfo.CurrentUICulture, DashboardStrings.ResolveCulture(null));
        Assert.Equal(CultureInfo.CurrentUICulture, DashboardStrings.ResolveCulture(""));
    }

    [Fact]
    public void KnownLanguageTagParses()
    {
        Assert.Equal("de-AT", DashboardStrings.ResolveCulture("de-AT").Name);
    }

    [Fact]
    public void AllNeverThrowsAndReturnsADictionary()
    {
        IReadOnlyDictionary<string, string> all = DashboardStrings.All(CultureInfo.GetCultureInfo("de"));
        Assert.NotEmpty(all);
    }

    /// <summary>
    /// The regression that forced the fresh-ResourceManager-per-call shape:
    /// one culture-miss fallback ('de') used to poison the shared instance's
    /// invariant cache, and the NEXT en request returned an empty table.
    /// </summary>
    [Fact]
    public void InvariantTableSurvivesAPrecedingCultureFallback()
    {
        Assert.NotEmpty(DashboardStrings.All(CultureInfo.GetCultureInfo("de")));
        Assert.True(DashboardStrings.All(CultureInfo.InvariantCulture).Count >= 30);
        Assert.True(DashboardStrings.All(CultureInfo.GetCultureInfo("fr-FR")).Count >= 30);
    }
}
