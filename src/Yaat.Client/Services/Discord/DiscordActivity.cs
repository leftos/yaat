using System.Buffers;
using System.Text.Json;

namespace Yaat.Client.Services.Discord;

/// <summary>
/// What Discord shows other people under the user's YAAT entry: a headline line
/// (<paramref name="Details"/>), an optional second line (<paramref name="State"/>) and the moment
/// the session started, which Discord renders as a running elapsed timer.
/// </summary>
/// <param name="Details">First line. The scenario's name.</param>
/// <param name="State">Second line, omitted from the wire when null.</param>
/// <param name="StartUnixSeconds">Unix seconds the activity started, back-dated for a late joiner.</param>
public sealed record DiscordActivity(string Details, string? State, long StartUnixSeconds);

/// <summary>
/// Builds the two JSON payloads this client ever sends. Written with <see cref="Utf8JsonWriter"/>
/// rather than a serializer so no reflection-based (or generated) contract is needed for two
/// fixed-shape messages.
/// </summary>
internal static class DiscordRpcJson
{
    /// <summary>
    /// Discord's limit on an activity's text fields. A longer one is rejected outright, and the
    /// rejection only comes back in the SET_ACTIVITY response, so an over-long scenario name would
    /// otherwise fail invisibly: clamp instead.
    /// </summary>
    internal const int MaxFieldLength = 128;

    /// <summary>
    /// Trims <paramref name="value"/> to <see cref="MaxFieldLength"/>, ending it with an ellipsis and
    /// never cutting a surrogate pair in half (which would put a lone surrogate on the wire).
    /// </summary>
    internal static string Clamp(string value)
    {
        if (value.Length <= MaxFieldLength)
        {
            return value;
        }

        int cut = MaxFieldLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }

    /// <summary>The opcode-0 payload: RPC version plus the Discord application id.</summary>
    public static byte[] Handshake(string clientId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("client_id", clientId);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The opcode-1 SET_ACTIVITY command. Discord keys the activity on the process id, so the
    /// running process's own id goes on the wire.
    /// </summary>
    public static byte[] SetActivity(DiscordActivity activity, int processId, string nonce)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cmd", "SET_ACTIVITY");
            writer.WriteStartObject("args");
            writer.WriteNumber("pid", processId);
            writer.WriteStartObject("activity");
            writer.WriteString("details", Clamp(activity.Details));
            if (!string.IsNullOrEmpty(activity.State))
            {
                writer.WriteString("state", Clamp(activity.State));
            }

            writer.WriteStartObject("timestamps");
            writer.WriteNumber("start", activity.StartUnixSeconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteString("nonce", nonce);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
