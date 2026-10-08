using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Jellyfin.Plugin.JellyPlay.Api;

/// <summary>
/// The ONE serialization gate for the plugin's API surface: camelCase JSON,
/// per docs/CONTRACT.md. The host's MVC JSON options are PascalCase (Jellyfin
/// convention) and plugin-registered MvcOptions configurators never reach the
/// host's effective options pipeline, so responses are serialized here
/// explicitly instead of via <c>Ok(...)</c>'s host serializer. The legacy
/// newsletter stubs and any non-ObjectResult action are untouched.
/// </summary>
public static class JellyPlayResponses
{
    // CamelCase property names WITHOUT camelizing dictionary keys: the
    // dashboard-strings table is keyed by resx identifiers ("PageTitle") that
    // the config pages look up verbatim via data-i18n.
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy
            {
                ProcessDictionaryKeys = false,
                OverrideSpecifiedNames = true
            }
        },
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new SystemTextJsonElementConverter(), new RawJsonConverter() }
    };

    public static ContentResult Camel(object? payload) => Gate(200, JsonConvert.SerializeObject(payload ?? new object(), Settings));

    /// <summary>camelCase JSON with a non-200 status (e.g. the newsletter 400 contract, the reprovision 502).</summary>
    public static ContentResult Camel(object? payload, int statusCode) => Gate(statusCode, JsonConvert.SerializeObject(payload ?? new object(), Settings));

    /// <summary>Bare 200 for actions with no response body.</summary>
    public static ContentResult Camel() => Gate(200, content: null);

    /// <summary>202 with a camelCase body (the broadcast route's response).</summary>
    public static ContentResult Accepted(object? payload) => Camel(payload, StatusCodes.Status202Accepted);

    /// <summary>
    /// The ONE error-body shape ({error: "code"}) through the same camelCase
    /// gate — raw StatusCode(...) calls bypass the gate and leak PascalCase
    /// serialization, contradicting docs/CONTRACT.md. All error responses
    /// (400/401/404/429/503…) go through here. The error code crosses the wire
    /// verbatim (byte stability): a null stays null.
    /// </summary>
    public static ContentResult Error(int statusCode, string? error) => Camel(new { error }, statusCode);

    /// <summary>
    /// The gate for responses written outside the MVC result pipeline (the
    /// Seerr proxy pass-through writes the response itself): the same gate
    /// serializer and error shape, straight onto the response.
    /// </summary>
    public static async Task WriteErrorAsync(HttpResponse response, int statusCode, string error, CancellationToken cancellationToken = default)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(JsonConvert.SerializeObject(new { error }, Settings), cancellationToken);
    }

    /// <summary>The one place a gate result is built — a plain ContentResult, which the response filter never touches (it rewrites ObjectResults only).</summary>
    private static ContentResult Gate(int statusCode, string? content) => new()
    {
        Content = content,
        ContentType = content is null ? null : "application/json; charset=utf-8",
        StatusCode = statusCode,
    };
}

/// <summary>
/// Writes System.Text.Json elements through to the Newtonsoft writer
/// verbatim. Without this, DTOs carrying JsonElement values (settings sync
/// snapshots, admin defaults) would serialize as the struct's own properties
/// ({"valueKind":1}) instead of the stored JSON.
/// </summary>
public sealed class SystemTextJsonElementConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => objectType == typeof(System.Text.Json.JsonElement)
            || objectType == typeof(System.Text.Json.JsonElement?);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => writer.WriteRawValue(((System.Text.Json.JsonElement)value!).GetRawText());

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("System.Text.Json elements are write-only here; input binding uses the host serializer.");
}

/// <summary>
/// A pre-encoded JSON payload carried as its exact text — the input type of
/// the gate's passthrough vocabulary. Settings values arrive from storage as
/// serialized JSON bytes; wrapping them here lets the gate emit them verbatim
/// (one WriteRawValue) instead of parse-clone-reserialize through
/// <see cref="SystemTextJsonElementConverter"/>, which stays for genuinely
/// parsed values. The text is valid JSON by every construction path: values
/// are stored serialized (the write paths serialize before store) and the
/// binding converter below reads whole tokens only.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(RawJsonSystemTextJsonConverter))]
public readonly record struct RawJson(string Json)
{
    /// <summary>The stored-null shape: an empty settings blob reads as a JSON null.</summary>
    public static RawJson Null { get; } = new("null");
}

/// <summary>
/// The gate's half of the passthrough: writes the payload text VERBATIM — the
/// same WriteRawValue the JsonElement converter uses, skipping its parse.
/// </summary>
public sealed class RawJsonConverter : JsonConverter<RawJson>
{
    public override void WriteJson(JsonWriter writer, RawJson value, JsonSerializer serializer)
        => writer.WriteRawValue(value.Json);

    public override RawJson ReadJson(JsonReader reader, Type objectType, RawJson existingValue, bool hasExistingValue, JsonSerializer serializer)
        => throw new NotSupportedException("RawJson is write-only here; input binding uses the host serializer.");
}

/// <summary>
/// The host-serializer half: binds ANY JSON token (import bundles carry
/// arbitrary value shapes) to its raw text, and serializes back verbatim — so
/// <c>RawJson</c> round-trips byte-exactly through both serializer worlds.
/// </summary>
public sealed class RawJsonSystemTextJsonConverter : System.Text.Json.Serialization.JsonConverter<RawJson>
{
    public override RawJson Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => new(System.Text.Json.JsonElement.ParseValue(ref reader).GetRawText());

    public override void Write(System.Text.Json.Utf8JsonWriter writer, RawJson value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteRawValue(value.Json);
}
