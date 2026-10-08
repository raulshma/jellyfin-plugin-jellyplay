using System.Text;

namespace Jellyfin.Plugin.JellyPlay.Realtime;

/// <summary>
/// A single server-sent event queued for one subscriber. The complete wire
/// frame is built ONCE at construction and carried on the event, so one
/// publish fanned out to N subscribers (and recorded in the replay ring)
/// frames the bytes once and every writer copies the same string.
/// </summary>
public sealed class SseEvent
{
    public SseEvent(string eventName, string data, ulong id)
    {
        EventName = eventName;
        Data = data;
        Id = id;
        Frame = FormatFrame();
    }

    public string EventName { get; }

    public string Data { get; }

    public ulong Id { get; }

    public string RetrySeconds { get; } = "3";

    /// <summary>The exact bytes the writer puts on the wire (id/event/retry/data lines, blank-line terminator).</summary>
    public string Frame { get; }

    private string FormatFrame()
    {
        var builder = new StringBuilder(256);
        builder.Append("id: ").Append(Id).Append('\n');
        builder.Append("event: ").Append(EventName).Append('\n');
        builder.Append("retry: ").Append(RetrySeconds).Append('\n');
        // SSE carries one data: line per payload line — a raw
        // newline inside the payload would terminate the frame
        // early and corrupt the stream.
        foreach (var line in Data.Replace("\r\n", "\n").Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');
        return builder.ToString();
    }
}
