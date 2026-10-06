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
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    };

    public static ContentResult Camel(object? payload) => new()
    {
        Content = JsonConvert.SerializeObject(payload ?? new object(), Settings),
        ContentType = "application/json; charset=utf-8",
        StatusCode = 200,
    };

    /// <summary>Bare 200 for actions with no response body.</summary>
    public static ContentResult Camel() => new()
    {
        StatusCode = 200,
    };
}
