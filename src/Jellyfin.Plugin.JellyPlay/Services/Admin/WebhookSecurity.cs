using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyPlay.Services.Admin;

/// <summary>
/// Hardening helpers for the anonymous Seerr webhook route: constant-time
/// secret comparison and rate-limit client identity.
/// </summary>
public static class WebhookSecurity
{
    /// <summary>
    /// Constant-time secret comparison over UTF8 bytes. A length mismatch never
    /// short-circuits the comparison work (the shorter buffer is compared
    /// against itself) so request timing does not reveal that the lengths
    /// differed; the result is still false.
    /// </summary>
    public static bool SecretMatches(string? configured, string? presented)
    {
        var configuredBytes = Encoding.UTF8.GetBytes(configured ?? string.Empty);
        var presentedBytes = Encoding.UTF8.GetBytes(presented ?? string.Empty);

        if (configuredBytes.Length == presentedBytes.Length)
        {
            return CryptographicOperations.FixedTimeEquals(configuredBytes, presentedBytes);
        }

        // Length mismatch: burn an equivalent full comparison, then reject.
        CryptographicOperations.FixedTimeEquals(presentedBytes, presentedBytes);
        return false;
    }

    /// <summary>
    /// The identity a webhook rate limit is keyed on: the remote address, or —
    /// only when <c>Seerr:TrustProxyHeaders</c> is configured — the first hop
    /// of X-Forwarded-For. Default false: without the flag a spoofable
    /// forwarded header would let clients rotate their limit identity.
    /// </summary>
    public static string ClientIpKey(HttpContext context, bool trustProxyHeaders)
    {
        if (trustProxyHeaders)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            var firstHop = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (firstHop.Length > 0 && firstHop[0].Length > 0)
            {
                return "xff:" + StripPort(firstHop[0]);
            }
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>X-Forwarded-For hops may carry a port ("1.2.3.4:5678"); keep the address for stable keying.</summary>
    private static string StripPort(string hop)
    {
        if (hop.StartsWith('[') && hop.EndsWith(']'))
        {
            return hop[1..^1]; // bracketed IPv6
        }

        var colon = hop.LastIndexOf(':');
        if (colon > 0 && hop.IndexOf(':') == colon && char.IsDigit(hop[^1]))
        {
            return hop[..colon]; // IPv4:port — a bare IPv6 has multiple colons
        }

        return hop;
    }
}
