namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>A single server-sent event queued for one subscriber.</summary>
public sealed record SseEvent(string EventName, string Data, ulong Id)
{
    public string RetrySeconds { get; init; } = "3";
}
