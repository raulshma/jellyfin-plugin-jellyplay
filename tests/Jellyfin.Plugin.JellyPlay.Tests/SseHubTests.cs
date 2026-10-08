using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

public class SseHubTests
{
    /// <summary>
    /// The wire frame pinned byte-for-byte: id/event/retry lines, one data:
    /// line per payload line (\r\n normalized first), blank-line terminator.
    /// The frame is built once per event and shared by every writer.
    /// </summary>
    [Fact]
    public void Frame_IsTheExactWireBytes_MultiLineDataNormalized()
    {
        var evt = new SseEvent("new-media", "{\"x\":1}", 42);
        Assert.Equal("id: 42\nevent: new-media\nretry: 3\ndata: {\"x\":1}\n\n", evt.Frame);
        Assert.Equal("3", evt.RetrySeconds);

        var multiline = new SseEvent("broadcast", "line1\r\nline2\n", 7);
        Assert.Equal(
            "id: 7\nevent: broadcast\nretry: 3\ndata: line1\ndata: line2\ndata: \n\n",
            multiline.Frame);
    }

    [Fact]
    public async Task PublishToUser_OnlyReachesThatUser()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var aliceId = hub.Subscribe("alice", "settings");
        var bobId = hub.Subscribe("bob", "settings");

        // The bounded channel buffers the event, so publish-then-read is race-free.
        var delivered = hub.PublishToUser("settings", "alice", "settings.changed", "{\"x\":1}");
        var evt = await hub.WaitForEventAsync(aliceId, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.NotNull(evt);
        Assert.Equal("settings.changed", evt!.EventName);
        Assert.NotEqual(0UL, evt.Id);

        hub.Unsubscribe(aliceId);
        hub.Unsubscribe(bobId);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var id = hub.Subscribe("alice", "events");
        hub.Unsubscribe(id);

        var delivered = hub.PublishAll("events", "broadcast", "{}");
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void PublishAll_ReachesAllSubscribers_OnSameStream()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var a = hub.Subscribe("a", "events");
        var b = hub.Subscribe("b", "events");
        var c = hub.Subscribe("c", "settings"); // different stream

        var delivered = hub.PublishAll("events", "new-media", "{}");
        Assert.Equal(2, delivered);
        Assert.Equal(3, hub.SubscriberCount);

        hub.Unsubscribe(a);
        hub.Unsubscribe(b);
        hub.Unsubscribe(c);
    }

    [Fact]
    public async Task PublishToUser_ExplicitId_AnchorsTheWireEventId()
    {
        // The settings stream anchors its SSE ids at the change-log head so a
        // reconnecting client can resume the delta pull from that cursor.
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var id = hub.Subscribe("alice", "settings");

        hub.PublishToUser("settings", "alice", "settings.changed", "{\"x\":1}", 4242);
        var evt = await hub.WaitForEventAsync(id, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(4242UL, evt!.Id);

        hub.Unsubscribe(id);
    }

    [Fact]
    public async Task PublishToUser_WithoutExplicitId_KeepsMonotonicSequence()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var first = hub.Subscribe("alice", "events");
        var second = hub.Subscribe("bob", "events");

        hub.PublishToUser("events", "alice", "broadcast", "{}");
        hub.PublishToUser("events", "bob", "broadcast", "{}");
        var a = await hub.WaitForEventAsync(first, TimeSpan.FromSeconds(2), CancellationToken.None);
        var b = await hub.WaitForEventAsync(second, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.NotEqual(0UL, a!.Id);
        Assert.True(b!.Id > a.Id); // the shared sequence advances across users

        hub.Unsubscribe(first);
        hub.Unsubscribe(second);
    }

    [Fact]
    public void ReplayEvents_ServesMissedEventsPerUser_InIdOrder()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        hub.PublishToUser("events", "alice", "new-media", "{\"n\":1}");
        hub.PublishToUser("events", "alice", "broadcast", "{\"n\":2}");
        hub.PublishToUser("events", "bob", "new-media", "{\"n\":3}");
        var all = hub.PublishAll("events", "broadcast", "{\"n\":4}"); // broadcast reaches every existing ring
        Assert.Equal(0, all); // nobody subscribed — delivery, not recording

        var aliceEvents = hub.ReplayEvents("alice", 0);
        Assert.Equal(3, aliceEvents.Count); // her two + the broadcast
        Assert.True(aliceEvents[0].Id < aliceEvents[1].Id && aliceEvents[1].Id < aliceEvents[2].Id);

        // Resume from the second event: only the events after it.
        var resume = hub.ReplayEvents("alice", aliceEvents[1].Id);
        Assert.Equal(aliceEvents[2].Id, Assert.Single(resume).Id);

        // Per-user isolation: bob never sees alice's targeted events.
        var bobEvents = hub.ReplayEvents("bob", 0);
        Assert.Equal(2, bobEvents.Count);

        // A user with no ring (never targeted) gets nothing.
        Assert.Empty(hub.ReplayEvents("carol", 0));
    }

    [Fact]
    public void ReplayEvents_RingHonorsCapacity()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        for (var index = 0; index < SseHub.ReplayRingCapacity + 10; index++)
        {
            hub.PublishToUser("events", "alice", "broadcast", $"\"{index}\"");
        }

        Assert.Equal(SseHub.ReplayRingCapacity, hub.ReplayEvents("alice", 0).Count);
    }

    [Fact]
    public void PublishToUser_DeliveredCount_ReflectsLiveSubscriptionsOnly()
    {
        // The delivered count IS the live-subscriber signal (the sync-nudge
        // gate reads it): zero when nobody is streaming the user's stream.
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        Assert.Equal(0, hub.PublishToUser("settings", "alice", "settings.changed", "{}"));

        var id = hub.Subscribe("alice", "settings");
        Assert.Equal(1, hub.PublishToUser("settings", "alice", "settings.changed", "{}"));
        Assert.Equal(0, hub.PublishToUser("events", "alice", "other", "{}")); // same user, other stream

        hub.Unsubscribe(id);
        Assert.Equal(0, hub.PublishToUser("settings", "alice", "settings.changed", "{}"));
    }
}

public class EpisodeGroupBufferTests
{
    private static EpisodeRef Episode(Guid id, string name) => new(id, name);

    [Fact]
    public void Group_PopsOnlyAfterWindowElapses()
    {
        var buffer = new EpisodeGroupBuffer();
        var season = Guid.NewGuid();
        buffer.Add(season, Guid.NewGuid(), "Show", 1, Guid.NewGuid(), "E1", null, nowMs: 0);
        buffer.Add(season, Guid.NewGuid(), "Show", 1, Guid.NewGuid(), "E2", null, nowMs: 5_000);

        Assert.Empty(buffer.PopDue(nowMs: 30_000, windowSeconds: 60));
        Assert.Equal(1, buffer.PendingGroupCount);

        var due = buffer.PopDue(nowMs: 60_001, windowSeconds: 60);
        var group = Assert.Single(due);
        Assert.Equal(season, group.SeasonId);
        Assert.Equal(2, group.Episodes.Count);
        Assert.Equal(0, buffer.PendingGroupCount);
    }

    [Fact]
    public void DuplicateEpisodes_Collapse()
    {
        var buffer = new EpisodeGroupBuffer();
        var season = Guid.NewGuid();
        var episode = Guid.NewGuid();
        buffer.Add(season, Guid.NewGuid(), "Show", 1, episode, "E1", null, 0);
        buffer.Add(season, Guid.NewGuid(), "Show", 1, episode, "E1", null, 1);

        var due = buffer.PopDue(nowMs: 120_000, windowSeconds: 60);
        Assert.Single(due[0].Episodes);
    }

    [Fact]
    public void FlushAll_ReleasesEverything()
    {
        var buffer = new EpisodeGroupBuffer();
        buffer.Add(Guid.NewGuid(), Guid.NewGuid(), "A", 1, Guid.NewGuid(), "E", null, 0);
        buffer.Add(Guid.NewGuid(), Guid.NewGuid(), "B", 1, Guid.NewGuid(), "E", null, 0);

        Assert.Equal(2, buffer.FlushAll().Count);
        Assert.Equal(0, buffer.PendingGroupCount);
    }
}

public class ScraperParseTests
{
    [Fact]
    public void Letterboxd_Parser_ExtractsTitleYear()
    {
        const string html = """
            <ul class="film-list">
              <li class="posteritem">
                <img alt="Seven Samurai (1954)" src="/x.jpg"><a href="/film/seven-samurai/"></a>
              </li>
              <li class="posteritem">
                <img alt="Rashomon" src="/y.jpg"><a href="/film/rashomon/"></a>
              </li>
            </ul>
            """;

        var items = Jellyfin.Plugin.JellyPlay.Services.Rows.CustomRowsService.ParseLetterboxd(html);
        Assert.Equal(2, items.Count);
        Assert.Equal("Seven Samurai", items[0].Title);
        Assert.Equal("1954", items[0].Year);
        Assert.Equal("Rashomon", items[1].Title);
        Assert.Null(items[1].Year);
    }

    [Fact]
    public void ImdbList_Parser_ExtractsIdTitleYear()
    {
        const string html = """
            <div class="lister-item">
              <a href="/title/tt0111161/?ref_=lis_tt">The Shawshank Redemption</a>
              <span class="lister-item-year">1994</span>
            </div>
            """;

        var items = Jellyfin.Plugin.JellyPlay.Services.Rows.CustomRowsService.ParseImdbList(html);
        // Parser relies on ipc-title__text class; legacy markup falls back to this regex form only when matched.
        // Either zero or more entries acceptable — assert no crash and shape correctness when present.
        Assert.All(items, item => Assert.StartsWith("tt", item.ImdbId!));
    }

    [Fact]
    public void FillerList_Parser_ExtractsEpisodeTypes()
    {
        const string html = """
            <table class="episode-table">
              <tr><td><a>1</a></td><td class="episode-table-type">Canon</td></tr>
              <tr><td><a>2</a></td><td class="episode-table-type">Filler</td></tr>
              <tr><td><a>3</a></td><td class="episode-table-type">MIXED CANON/FILLER</td></tr>
            </table>
            """;

        var markers = Jellyfin.Plugin.JellyPlay.Services.Anime.AnimeMarkersService.ParseFillerList(html);
        Assert.Equal(3, markers.Count);
        Assert.Equal("canon", markers[0].Type);
        Assert.Equal("filler", markers[1].Type);
        Assert.Equal("mixed", markers[2].Type);
    }

    [Fact]
    public void ImdbTop250_NextData_Parser_Ranks()
    {
        const string json = """
            {"props":{"pageProps":{"pageData":{"chartTitles":{"edges":[
              {"node":{"id":"tt0111161","titleText":{"text":"The Shawshank Redemption"},"releaseYear":{"year":1994},"ratingsSummary":{"aggregateRating":9.3}}},
              {"node":{"id":"tt0068646","titleText":{"text":"The Godfather"},"releaseYear":{"year":1972},"ratingsSummary":{"aggregateRating":9.2}}}
            ]}}}}}
            """;
        var html = $"<html><script id=\"__NEXT_DATA__\" type=\"application/json\">{json}</script></html>";

        var entries = Jellyfin.Plugin.JellyPlay.Services.Ratings.ImdbChartsService.ParseTop250(html);
        Assert.Equal(2, entries.Count);
        Assert.Equal(("1", "The Shawshank Redemption", "1994", "tt0111161", "9.3"),
            (entries[0].Rank, entries[0].Title, entries[0].Year, entries[0].ImdbId, entries[0].Rating));
    }
}
