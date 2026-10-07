using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The one tri-state admin-defaults merger (GLOSSARY "Admin defaults"): forced
/// overwrites always, suggested fills gaps only, the per-user scope beats the
/// global scope, and a user's own value beats suggested. Table-style over
/// stored payloads exactly as the dashboard persists them.
/// </summary>
public sealed class DefaultsEnvelopeTests
{
    private static JsonElement? Payload(string? json)
        => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static List<ResolvedDefault> Merge(string? global, string? perUser, params string[] existingKeys)
        => DefaultsEnvelope.MergeForRead(Payload(global), Payload(perUser), new HashSet<string>(existingKeys, StringComparer.Ordinal));

    /// <summary>The admin PUSH path: the conservative write-path shadow rule is on (see <see cref="DefaultsEnvelope.MergeForPush"/>).</summary>
    private static List<ResolvedDefault> MergeForPush(string? global, string? perUser)
        => DefaultsEnvelope.MergeForPush(Payload(global), Payload(perUser), new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void Forced_EmptyExisting_Applies()
    {
        var resolved = Merge(
            """{"ui/theme":{"mode":"forced","value":"dark"}}""",
            null);

        var entry = Assert.Single(resolved);
        Assert.Equal(("ui", "theme", AdminDefaultMode.Forced), (entry.Ns, entry.Key, entry.Mode));
        Assert.Equal("\"dark\"", entry.Value.GetRawText());
    }

    [Fact]
    public void Suggested_FillsGapOnly()
    {
        var gapFill = Merge(
            """{"player/skip":{"mode":"suggested","value":15}}""",
            null);
        var entry = Assert.Single(gapFill);
        Assert.Equal(("player", "skip", AdminDefaultMode.Suggested, 15), (entry.Ns, entry.Key, entry.Mode, entry.Value.GetInt32()));

        var userOwnsIt = Merge(
            """{"player/skip":{"mode":"suggested","value":15}}""",
            null,
            "player/skip"); // the user already set this key
        Assert.Empty(userOwnsIt); // suggested never overwrites
    }

    [Fact]
    public void Forced_OverwritesExistingUserValue()
    {
        var resolved = Merge(
            """{"ui/locked":{"mode":"forced","value":true}}""",
            null,
            "ui/locked"); // user value loses to forced

        var entry = Assert.Single(resolved);
        Assert.Equal((AdminDefaultMode.Forced, true), (entry.Mode, entry.Value.GetBoolean()));
    }

    [Fact]
    public void PerUserScope_BeatsGlobal_ModeAndValue()
    {
        var resolved = Merge(
            """{"ui/volume":{"mode":"suggested","value":50}}""",
            """{"ui/volume":{"mode":"forced","value":80}}""");

        var entry = Assert.Single(resolved);
        Assert.Equal((AdminDefaultMode.Forced, 80), (entry.Mode, entry.Value.GetInt32()));
    }

    [Fact]
    public void PerUserScope_SuggestedDowngrade_ReplacesGlobalForced()
    {
        var resolved = Merge(
            """{"ui/volume":{"mode":"forced","value":90}}""",
            """{"ui/volume":{"mode":"suggested","value":40}}""");

        // The user scope wins wholesale: the entry is suggested now — and a
        // suggested default still only fills a gap, so an existing user value
        // would keep it out entirely.
        var entry = Assert.Single(resolved);
        Assert.Equal((AdminDefaultMode.Suggested, 40), (entry.Mode, entry.Value.GetInt32()));
        Assert.Empty(Merge(
            """{"ui/volume":{"mode":"forced","value":90}}""",
            """{"ui/volume":{"mode":"suggested","value":40}}""",
            "ui/volume"));
    }

    [Theory]
    [InlineData("""{"ui/theme":{"value":"dark"}}""")] // missing mode
    [InlineData("""{"ui/theme":{"mode":"bogus","value":"dark"}}""")] // unknown mode
    [InlineData("""{"ui/theme":{"mode":"forced"}}""")] // missing value
    [InlineData("""{"ui/theme":5}""")] // not an envelope object
    [InlineData("""{"ui-theme":{"mode":"forced","value":"dark"}}""")] // no ns/key shape
    public void InvalidEnvelopes_Rejected(string payload)
    {
        Assert.Empty(Merge(payload, null));
    }

    [Fact]
    public void UnusablePerUserEntry_DoesNotShadowTheGlobalDefault()
    {
        // Only per-user entries carrying a mode participate in the scope merge;
        // a malformed one leaves the global default standing.
        var resolved = Merge(
            """{"ui/theme":{"mode":"forced","value":"dark"}}""",
            """{"ui/theme":5}""");

        var entry = Assert.Single(resolved);
        Assert.Equal("\"dark\"", entry.Value.GetRawText());
    }

    [Fact]
    public void WritePath_MalformedPerUserEntry_ShadowsTheGlobalDefault()
    {
        // The conservative write-path rule (the pre-refactor admin push): ANY
        // per-user OBJECT entry — even one without a usable mode — shadows the
        // global default for its key, so a broken per-user override blocks the
        // push instead of letting the global FORCED default fall through.
        const string global = """{"ui/theme":{"mode":"forced","value":"dark"}}""";

        Assert.Empty(MergeForPush(global, """{"ui/theme":{"value":"dark"}}""")); // object without a mode
        Assert.Empty(MergeForPush(global, """{"ui/theme":{"mode":"FORCED","value":"dark"}}""")); // unknown mode
        Assert.Empty(MergeForPush(global, """{"ui/theme":{"mode":"forced"}}""")); // missing value

        // A NON-object entry never shadowed (pre-refactor and now): the
        // global default stands on the write path too.
        var pushed = MergeForPush(global, """{"ui/theme":5}""");
        Assert.Equal("\"dark\"", Assert.Single(pushed).Value.GetRawText());

        // The read path is unchanged: a mode-less object entry is ignored and
        // the global default still resolves for the client.
        Assert.Equal("\"dark\"", Assert.Single(Merge(global, """{"ui/theme":{"value":"dark"}}""")).Value.GetRawText());
    }

    [Fact]
    public void Values_CarriedVerbatim_NeverReserialized()
    {
        const string value = """{"nested":{"a":[1,2],"b":"x"}}""";
        var resolved = Merge("{\"ns/key\":{\"mode\":\"forced\",\"value\":" + value + "}}", null);

        Assert.Equal(value, Assert.Single(resolved).Value.GetRawText());
    }

    [Fact]
    public void Merge_MixedScopesAndModes_Together()
    {
        var resolved = Merge(
            """{"ui/locked":{"mode":"forced","value":true},"player/skip":{"mode":"suggested","value":15},"player/volume":{"mode":"forced","value":60}}""",
            """{"ui/locked":{"mode":"suggested","value":false},"player/skip":{"mode":"suggested","value":20}}""",
            "player/volume"); // the user already owns this key

        // ui/locked: the per-user scope downgrades forced→suggested and gap-fills.
        var locked = resolved.Single(entry => entry.Key == "locked");
        Assert.Equal(("ui", AdminDefaultMode.Suggested, false), (locked.Ns, locked.Mode, locked.Value.GetBoolean()));

        // player/skip: the per-user scope's value wins over the global one.
        var skip = resolved.Single(entry => entry.Key == "skip");
        Assert.Equal(("player", AdminDefaultMode.Suggested, 20), (skip.Ns, skip.Mode, skip.Value.GetInt32()));

        // The user owns player/volume, yet the global FORCED default still
        // overwrites it — that is what forced means.
        var volume = resolved.Single(entry => entry.Key == "volume");
        Assert.Equal(("player", AdminDefaultMode.Forced, 60), (volume.Ns, volume.Mode, volume.Value.GetInt32()));
    }

    [Fact]
    public void TryParse_ValidAndInvalid()
    {
        Assert.True(DefaultsEnvelope.TryParse(
            JsonDocument.Parse("""{"mode":"forced","value":1}""").RootElement, out var forced, out var forcedValue));
        Assert.Equal((AdminDefaultMode.Forced, 1), (forced, forcedValue.GetInt32()));

        Assert.True(DefaultsEnvelope.TryParse(
            JsonDocument.Parse("""{"mode":"suggested","value":"x"}""").RootElement, out var suggested, out var suggestedValue));
        Assert.Equal((AdminDefaultMode.Suggested, "x"), (suggested, suggestedValue.GetString()));

        Assert.False(DefaultsEnvelope.TryParse(
            JsonDocument.Parse("""{"mode":"FORCED","value":1}""").RootElement, out _, out _)); // modes are exact
        Assert.False(DefaultsEnvelope.TryParse(JsonDocument.Parse("5").RootElement, out _, out _));
    }
}
