using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyPlay.Api;
using Jellyfin.Plugin.JellyPlay.Configuration;
using Jellyfin.Plugin.JellyPlay.Realtime;
using Jellyfin.Plugin.JellyPlay.Services.Admin;
using Jellyfin.Plugin.JellyPlay.Services.Cache;
using Jellyfin.Plugin.JellyPlay.Services.Seerr;
using Jellyfin.Plugin.JellyPlay.Storage;
using Jellyfin.Plugin.JellyPlay.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>Hardening: SSE keepalive frames keep idle proxies from reaping streams.</summary>
public sealed class SseKeepaliveTests
{
    private static (DefaultHttpContext Context, MemoryStream Body) StreamContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string BodyText(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    [Fact]
    public async Task IdleWriter_EmitsKeepaliveFrame_AfterInterval()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var id = hub.Subscribe("alice", "events");
        var (context, body) = StreamContext();
        using var cts = new CancellationTokenSource(600);

        await SseStreamWriter.WriteAsync(context, hub, id, cts.Token, TimeSpan.FromMilliseconds(50));

        var text = BodyText(body);
        Assert.Contains(": keepalive", text);
        Assert.DoesNotContain("data:", text); // no events were published
        Assert.False(hub.IsSubscribed(id)); // writer unsubscribed on exit
    }

    [Fact]
    public async Task Keepalive_WaitsForQuiet_EventsStreamFirst()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var id = hub.Subscribe("alice", "events");
        hub.PublishAll("events", "new-media", "{\"x\":1}");
        var (context, body) = StreamContext();
        using var cts = new CancellationTokenSource(1000);

        await SseStreamWriter.WriteAsync(context, hub, id, cts.Token, TimeSpan.FromMilliseconds(50));

        var text = BodyText(body);
        Assert.Contains("event: new-media", text);
        Assert.Contains("data: {\"x\":1}", text);
        Assert.Contains("retry: ", text);
        var keepaliveAt = text.IndexOf(": keepalive", StringComparison.Ordinal);
        var dataAt = text.IndexOf("data:", StringComparison.Ordinal);
        Assert.True(keepaliveAt > dataAt, "keepalive must come after the event, not before");
        // A 50ms quiet interval in a 1000ms budget yields ~19 keepalives; demanding
        // only 2 leaves ~17 frames of slack for CI runner scheduling overshoot.
        Assert.True(text.Split(": keepalive", StringSplitOptions.None).Length - 1 >= 2);
    }

    [Fact]
    public async Task Unsubscribe_TerminatesWriter_Promptly()
    {
        var hub = new SseHub(NullLogger<SseHub>.Instance);
        var id = hub.Subscribe("alice", "events");
        var (context, _) = StreamContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writer = SseStreamWriter.WriteAsync(context, hub, id, cts.Token, TimeSpan.FromMilliseconds(100));
        hub.Unsubscribe(id);
        await writer.WaitAsync(TimeSpan.FromSeconds(2)); // would hang if the loop spun on a closed channel

        Assert.Equal(0, hub.SubscriberCount);
    }
}

/// <summary>Hardening: stepwise schema migrations via PRAGMA user_version, plus integrity quarantine.</summary>
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-migration-" + Guid.NewGuid().ToString("N"));

    public DatabaseMigrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DbPath => Path.Combine(_tempDir, "plugins", "JellyPlay", "jellyplay_plugin.db");

    private static bool IndexExists(string dbPath, string indexName)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from sqlite_master where type = 'index' and name = '{indexName}'";
        return Convert.ToInt64(command.ExecuteScalar()!) > 0;
    }

    private static int RawUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection($"Filename={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "pragma user_version";
        return Convert.ToInt32(command.ExecuteScalar()!);
    }

    private static SettingWrite Write(string ns, string key, long updatedAt)
        => new(ns, key, 1, updatedAt, "d1", Encoding.UTF8.GetBytes("\"v\""));

    [Fact]
    public void FreshDatabase_IsCreatedAtCurrentVersion_WithMigratedIndexes()
    {
        using var db = new JellyPlayDatabase(_tempDir);

        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, db.UserVersion);
        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, RawUserVersion(DbPath));
        Assert.True(IndexExists(DbPath, "idx_change_log_updated"));
        Assert.True(IndexExists(DbPath, "idx_sync_history_user_device")); // schema v8
        Assert.True(db.CheckIntegrity().IntegrityOk);
    }

    [Fact]
    public void V1Database_MigratesToCurrent_PreservesData_AndIsIdempotent()
    {
        // Build at the current version, write data, then roll the file back to a v1 state:
        // v1 schema without the migrated index, user_version stamped 1.
        string storedValue;
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            first.UpsertSettings("user1", JellyPlayDatabase.BaseProfile, new[] { Write("ui", "theme", 100) }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
            storedValue = Encoding.UTF8.GetString(Assert.Single(first.GetSettings("user1", "")).Value);
        }

        using (var raw = new SqliteConnection($"Filename={DbPath}"))
        {
            raw.Open();
            using var downgrade = raw.CreateCommand();
            downgrade.CommandText =
                "drop index if exists idx_change_log_updated; " +
                "alter table devices drop column PushKind; alter table devices drop column PushEndpoint; " +
                "alter table devices drop column CreatedAt; " +
                "alter table sync_history drop column FromSeq; alter table sync_history drop column ToSeq; " +
                "pragma user_version = 1;";
            downgrade.ExecuteNonQuery();
        }

        using (var second = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, second.UserVersion);
            Assert.True(IndexExists(DbPath, "idx_change_log_updated"));
            Assert.Equal(storedValue, Encoding.UTF8.GetString(Assert.Single(second.GetSettings("user1", "")).Value));
        }

        // Re-open: nothing left to migrate, version and index stable.
        using (var third = new JellyPlayDatabase(_tempDir))
        {
            Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, third.UserVersion);
            Assert.True(IndexExists(DbPath, "idx_change_log_updated"));
            Assert.True(third.CheckIntegrity().IntegrityOk);
        }
    }

    [Fact]
    public void CorruptDatabase_IsQuarantined_AndRecreatedFresh()
    {
        using (var first = new JellyPlayDatabase(_tempDir))
        {
            first.UpsertSettings("user1", JellyPlayDatabase.BaseProfile, new[] { Write("ui", "a", 1), Write("ui", "b", 2) }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
            first.CheckIntegrity(); // runs wal_checkpoint(TRUNCATE): data lands in the main file
        }

        // Corrupt data pages (never the header or sqlite_master on page 1) so the
        // schema DDL still loads but integrity_check fails.
        using (var stream = new FileStream(DbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            var garbage = new byte[256];
            Array.Fill(garbage, (byte)0xFF);
            foreach (var offset in new[] { 4096, 6144, 8192, 10240 })
            {
                if (offset < stream.Length)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    stream.Write(garbage);
                }
            }
        }

        using var repaired = new JellyPlayDatabase(_tempDir);
        var result = repaired.CheckIntegrity();

        Assert.False(result.IntegrityOk);
        Assert.True(result.Repaired);
        Assert.Equal(JellyPlayDatabase.CurrentSchemaVersion, repaired.UserVersion); // fresh database, fully migrated
        Assert.True(IndexExists(DbPath, "idx_change_log_updated"));

        // Quarantined copy exists alongside, and the fresh store works.
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "*.corrupt-*"));
        repaired.UpsertSettings("user2", JellyPlayDatabase.BaseProfile, new[] { Write("ui", "x", 1) }, new JellyPlayDatabase.Quotas(1024, 4096, 10));
        Assert.Single(repaired.GetSettings("user2", ""));
    }
}

/// <summary>Hardening: the file cache is size-capped with oldest-first eviction.</summary>
public sealed class FileCacheSweepTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-cache-" + Guid.NewGuid().ToString("N"));
    private readonly FileCacheStore _store;

    public FileCacheSweepTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new FileCacheStore(NullLogger<FileCacheStore>.Instance, _tempDir, () => 256);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Entry(string name, int bytes, DateTimeOffset written)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, new string('a', bytes));
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    [Fact]
    public void Sweep_EvictsOldestFirst_UntilUnderCap()
    {
        var now = DateTimeOffset.UtcNow;
        var oldest = Entry("a.json", 100, now.AddMinutes(-40));
        var older = Entry("b.json", 100, now.AddMinutes(-30));
        var old = Entry("c.json", 100, now.AddMinutes(-20));
        var kept = Entry("d.json", 100, now.AddMinutes(-10));

        var result = _store.Sweep(maxTotalBytes: 150, maxEntryAge: TimeSpan.FromHours(1));

        Assert.Equal(3, result.DeletedFiles);
        Assert.Equal(300, result.BytesReclaimed);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Sweep_PurgesExpiredEntries_EvenUnderCap()
    {
        var expired = Entry("stale.json", 50, DateTimeOffset.UtcNow.AddDays(-40));
        var fresh = Entry("fresh.json", 50, DateTimeOffset.UtcNow.AddMinutes(-1));

        // The store's constructor sweeps in the background (same 30-day age,
        // 256 MB cap) and may delete the expired file before — or during —
        // the explicit sweep below. Settle that race first (bounded wait for
        // the delete), then assert the eventual filesystem outcome instead
        // of the explicit sweep's return count.
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(expired) && DateTime.UtcNow < settleDeadline)
        {
            Thread.Sleep(25);
        }

        _store.Sweep(maxTotalBytes: 1024 * 1024, maxEntryAge: TimeSpan.FromDays(30));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Sweep_UnderCapWithNothingExpired_DeletesNothing()
    {
        Entry("a.json", 10, DateTimeOffset.UtcNow.AddMinutes(-1));
        Entry("b.json", 10, DateTimeOffset.UtcNow.AddMinutes(-2));

        var result = _store.Sweep(maxTotalBytes: 1024 * 1024, maxEntryAge: TimeSpan.FromDays(30));

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Equal(2, Directory.GetFiles(_tempDir, "*.json").Length);
    }

    [Fact]
    public async Task Set_ReadsBack_AndRespectsValue()
    {
        await _store.SetAsync("tmdb:123", new[] { "one", "two" });
        var read = await _store.GetAsync<string[]>("tmdb:123", TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { "one", "two" }, read);
        Assert.NotNull(await _store.GetAsync<string[]>("tmdb:123", TimeSpan.FromHours(24)));
        Assert.Null(await _store.GetAsync<string[]>("tmdb:123", TimeSpan.Zero)); // expired = miss
    }
}

/// <summary>Hardening: webhook secret comparison and rate-limit client identity.</summary>
public sealed class WebhookSecurityTests
{
    [Theory]
    [InlineData("s3cret", "s3cret", true)]
    [InlineData("s3cret", "other", false)]
    [InlineData("s3cret", "s3cer", false)]   // length mismatch must still be false
    [InlineData("s3cret", "s3crett", false)] // presented longer
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void SecretMatches_MatchesExactly(string? configured, string? presented, bool expected)
    {
        Assert.Equal(expected, WebhookSecurity.SecretMatches(configured, presented));
    }

    [Fact]
    public void SecretMatches_HandlesNulls()
    {
        Assert.True(WebhookSecurity.SecretMatches(null, null));
        Assert.False(WebhookSecurity.SecretMatches("real-secret", null));
        Assert.False(WebhookSecurity.SecretMatches(null, "guess"));
    }

    private static HttpContext ContextWith(string? remoteIp, string? forwardedFor)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    [Fact]
    public void ClientIpKey_UsesRemoteAddress_ByDefault_EvenWhenForwardedHeaderPresent()
    {
        var context = ContextWith("203.0.113.7", "198.51.100.9, 10.0.0.1");
        Assert.Equal("ip:203.0.113.7", WebhookSecurity.ClientIpKey(context, trustProxyHeaders: false));
    }

    [Fact]
    public void ClientIpKey_TrustsForwardedFirstHop_OnlyWhenFlagSet()
    {
        var context = ContextWith("203.0.113.7", "198.51.100.9:5678, 10.0.0.1");
        Assert.Equal("xff:198.51.100.9", WebhookSecurity.ClientIpKey(context, trustProxyHeaders: true));

        var noPort = ContextWith("203.0.113.7", "198.51.100.9, 10.0.0.1");
        Assert.Equal("xff:198.51.100.9", WebhookSecurity.ClientIpKey(noPort, trustProxyHeaders: true));
    }

    [Fact]
    public void ClientIpKey_FallsBackToUnknown_WhenNoRemoteAddress()
    {
        Assert.Equal("ip:unknown", WebhookSecurity.ClientIpKey(ContextWith(null, null), trustProxyHeaders: false));
    }

    [Fact]
    public void WebhookRateLimiter_AllowsThirtyPerMinutePerClient()
    {
        var limiter = RateLimiter.Webhook();
        for (var hit = 0; hit < 30; hit++)
        {
            Assert.True(limiter.Allow("ip:203.0.113.7", 1_000 + hit));
        }

        Assert.False(limiter.Allow("ip:203.0.113.7", 1_031));
        Assert.True(limiter.Allow("ip:198.51.100.9", 1_032)); // other clients unaffected
        Assert.True(limiter.Allow("ip:203.0.113.7", 61_001)); // next window recovers
    }
}

/// <summary>Hardening: Seerr cookies are AES-GCM encrypted at rest with a plugin-held key.</summary>
public sealed class SecretBoxTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jellyplay-secretbox-" + Guid.NewGuid().ToString("N"));

    public SecretBoxTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SecretBox Box() => new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private const string CookieJson = """
        [{"Name":"connect.sid","Value":"s%3Aabc.def"}]
        """;

    [Fact]
    public void RoundTrip_ProtectAndUnprotect()
    {
        var box = Box();
        var payload = box.Protect(CookieJson);

        Assert.NotNull(payload);
        Assert.True(SecretBox.IsEncryptedPayload(payload));
        Assert.Equal(SecretBox.PayloadVersion, payload[0]);
        Assert.Equal(CookieJson, box.TryUnprotect(payload));
    }

    [Fact]
    public void Ciphertext_DoesNotContainPlaintext()
    {
        var payload = Box().Protect(CookieJson)!;
        var needle = Encoding.UTF8.GetBytes("connect.sid");
        Assert.Equal(-1, payload.AsSpan().IndexOf(needle));
    }

    [Fact]
    public void LegacyPlaintext_IsNotMarkedEncrypted_AndIsRefusedByTheBox()
    {
        var legacy = Encoding.UTF8.GetBytes(CookieJson);
        Assert.False(SecretBox.IsEncryptedPayload(legacy));
        Assert.Null(Box().TryUnprotect(legacy)); // decryption never "succeeds" on plaintext
    }

    [Fact]
    public void WrongKey_FailsToDecrypt()
    {
        var sealedPayload = Box().Protect(CookieJson);
        Assert.Null(Box().TryUnprotect(sealedPayload));
    }

    [Fact]
    public void TamperedPayload_FailsToDecrypt()
    {
        var box = Box();
        var payload = box.Protect(CookieJson);
        payload![^2] ^= 0xFF; // flip a tag byte

        Assert.Null(box.TryUnprotect(payload));
    }

    [Fact]
    public void BoxWithoutKey_IsInert()
    {
        var box = new SecretBox(null);
        Assert.False(box.IsAvailable);
        Assert.Null(box.Protect(CookieJson));
        Assert.Null(box.TryUnprotect(Box().Protect(CookieJson)));
    }

    [Fact]
    public void KeyTooShort_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new SecretBox(new byte[16]));
    }

    [Fact]
    public void LoadOrCreate_PersistsKey_AcrossLoads()
    {
        var first = SecretBox.LoadOrCreate(_tempDir);
        Assert.True(first.IsAvailable);
        Assert.True(File.Exists(Path.Combine(_tempDir, SecretBox.KeyFileName)));

        var payload = first.Protect(CookieJson);

        var second = SecretBox.LoadOrCreate(_tempDir);
        Assert.True(second.IsAvailable);
        Assert.Equal(CookieJson, second.TryUnprotect(payload));
    }

    [Fact]
    public void LoadOrCreate_WithUnusableDirectory_YieldsInertBox()
    {
        var fileAsDir = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(fileAsDir, "not a directory");

        var box = SecretBox.LoadOrCreate(Path.Combine(fileAsDir, "sub"), NullLogger.Instance);
        Assert.False(box.IsAvailable);
        Assert.Null(box.Protect(CookieJson));
    }
}

/// <summary>The Seerr validation cache: entries expire on read and are swept on write, so the map cannot grow forever.</summary>
public sealed class SeerrValidationCacheTests
{
    [Fact]
    public void Entries_ExpireOnRead_AfterTheTtl()
    {
        var cache = new ValidationCache();
        cache.Set("u", 0, valid: true);

        Assert.True(cache.TryGet("u", 1_000, out var valid));
        Assert.True(valid);
        Assert.False(cache.TryGet("u", ValidationCache.TtlMs + 1, out _)); // expired = miss
    }

    [Fact]
    public void WritePastTheSweepWindow_DropsExpiredEntries()
    {
        var cache = new ValidationCache();
        cache.Set("a", 0, true);
        cache.Set("b", 0, false);
        Assert.Equal(2, cache.Count);

        cache.Set("c", ValidationCache.TtlMs, true); // a full window past the last write → sweep fires

        Assert.Equal(1, cache.Count); // a and b were expired; c is fresh
        Assert.True(cache.TryGet("c", ValidationCache.TtlMs, out var valid));
        Assert.True(valid);
    }

    [Fact]
    public void Sweep_DropsOnlyExpiredEntries()
    {
        var cache = new ValidationCache();
        cache.Set("old", 0, true);
        cache.Set("fresh", ValidationCache.TtlMs - 1, true);

        cache.Set("trigger", ValidationCache.TtlMs + 1, true);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("fresh", ValidationCache.TtlMs + 1, out _));
        Assert.True(cache.TryGet("trigger", ValidationCache.TtlMs + 1, out _));
    }
}

/// <summary>The rate-limit seam's key derivation: user id vs client identity (proxy trust read from the Seerr config).</summary>
public sealed class RateLimitKeyTests
{
    private static HttpContext ContextWith(string? remoteIp, string? forwardedFor, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services ?? new ServiceCollection().BuildServiceProvider()
        };
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    private static IServiceProvider TrustedProxyProviders()
        => new ServiceCollection()
            .AddSingleton(new Func<SeerrConfig>(() => new SeerrConfig { TrustProxyHeaders = true }))
            .BuildServiceProvider();

    [Fact]
    public void UserStrategy_KeysOnThePluginUserId()
    {
        var context = ContextWith(null, null);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("Jellyfin-UserId", "11111111-1111-1111-1111-111111111111") }, "Bearer"));

        Assert.Equal(
            "settings:11111111-1111-1111-1111-111111111111",
            RateLimitFilter.BuildKey(RateLimitKeyStrategy.User, "settings", context, context.RequestServices));
    }

    [Fact]
    public void ClientIdentity_KeysOnRemoteIp_ByDefault_EvenWithForwardedHeader()
    {
        var context = ContextWith("203.0.113.7", "198.51.100.9, 10.0.0.1");

        Assert.Equal(
            "webhook:ip:203.0.113.7",
            RateLimitFilter.BuildKey(RateLimitKeyStrategy.ClientIdentity, "webhook", context, context.RequestServices));
    }

    [Fact]
    public void ClientIdentity_UsesForwardedFirstHop_OnlyWhenProxyTrusted()
    {
        var context = ContextWith("203.0.113.7", "198.51.100.9:5678, 10.0.0.1", TrustedProxyProviders());

        Assert.Equal(
            "webhook:xff:198.51.100.9",
            RateLimitFilter.BuildKey(RateLimitKeyStrategy.ClientIdentity, "webhook", context, context.RequestServices));
    }
}

/// <summary>The gate's safety net: stray ObjectResults are rewritten through the gate; ContentResults (the gate's own output shape) are untouched.</summary>
public sealed class JellyPlayResponseFilterTests
{
    private static ActionExecutedContext ExecutedContext(IActionResult result)
        => new(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new ActionDescriptor())
        {
            Result = result
        };

    [Fact]
    public void RawObjectResult_IsRewrittenThroughTheGate()
    {
        var filter = new JellyPlayResponseFilter();
        var context = ExecutedContext(new ObjectResult(new { Event = "Watch Later" })
        {
            StatusCode = StatusCodes.Status409Conflict
        });

        filter.OnActionExecuted(context);

        var rewritten = Assert.IsAssignableFrom<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status409Conflict, rewritten.StatusCode);
        Assert.Equal("{\"event\":\"Watch Later\"}", rewritten.Content);
    }

    [Fact]
    public void ContentResultBodies_AreUntouched()
    {
        // Gate-produced responses are ContentResults — the net rewrites
        // ObjectResults only, so a gate body is never double-serialized.
        var filter = new JellyPlayResponseFilter();
        var gateResult = JellyPlayResponses.Error(StatusCodes.Status409Conflict, "conflict");
        var context = ExecutedContext(gateResult);

        filter.OnActionExecuted(context);

        Assert.Same(gateResult, context.Result);
    }

    [Fact]
    public void BodylessObjectResult_IsLeftAlone()
    {
        var filter = new JellyPlayResponseFilter();
        var raw = new ObjectResult(null) { StatusCode = StatusCodes.Status200OK };
        var context = ExecutedContext(raw);

        filter.OnActionExecuted(context);

        Assert.Same(raw, context.Result);
    }
}
