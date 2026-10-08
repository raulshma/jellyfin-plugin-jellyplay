using System;
using Jellyfin.Plugin.JellyPlay.Api;

namespace Jellyfin.Plugin.JellyPlay.Services.Settings;

/// <summary>
/// RFC 4180 CSV rendering of an <see cref="AuditExportResponse"/> — one row
/// per per-key diff entry; operations without a usable range (pre-v7 rows,
/// no-op pushes, empty pulls) render as a single row with empty key columns
/// so every recorded operation still appears. Values are quoted only when
/// they must be (comma, quote, CR/LF); a literal quote doubles.
/// </summary>
public static class SyncAuditCsv
{
    /// <summary>The header row — column order is the contract for spreadsheet consumers.</summary>
    public const string Header = "seq,ts,deviceId,op,keysApplied,keysRejected,fromSeq,toSeq,ns,key,keyUpdatedAt";

    /// <summary>The characters that force RFC 4180 quoting — checked per field per row.</summary>
    private static readonly char[] QuoteTriggers = { ',', '"', '\r', '\n' };

    public static string Build(AuditExportResponse export)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(Header);
        foreach (var entry in export.History)
        {
            if (entry.Keys.Count == 0)
            {
                AppendRow(builder, entry.Seq, entry.Ts, entry.DeviceId, entry.Op, entry.KeysApplied, entry.KeysRejected, entry.FromSeq, entry.ToSeq, string.Empty, string.Empty, null);
                continue;
            }

            foreach (var key in entry.Keys)
            {
                AppendRow(builder, entry.Seq, entry.Ts, entry.DeviceId, entry.Op, entry.KeysApplied, entry.KeysRejected, entry.FromSeq, entry.ToSeq, key.Ns, key.Key, key.UpdatedAt);
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(
        System.Text.StringBuilder builder,
        long seq,
        long ts,
        string deviceId,
        string op,
        int keysApplied,
        int keysRejected,
        long? fromSeq,
        long? toSeq,
        string ns,
        string key,
        long? keyUpdatedAt)
    {
        builder.Append(seq).Append(',');
        builder.Append(ts).Append(',');
        builder.Append(Field(deviceId)).Append(',');
        builder.Append(Field(op)).Append(',');
        builder.Append(keysApplied).Append(',');
        builder.Append(keysRejected).Append(',');
        builder.Append(fromSeq is { } from ? from.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append(',');
        builder.Append(toSeq is { } to ? to.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append(',');
        builder.Append(Field(ns)).Append(',');
        builder.Append(Field(key)).Append(',');
        builder.Append(keyUpdatedAt is { } stamp
            ? stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);
        builder.Append("\r\n");
    }

    /// <summary>Minimal RFC 4180 quoting: quote only when required, doubling embedded quotes.</summary>
    private static string Field(string value)
    {
        if (value.IndexOfAny(QuoteTriggers) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
