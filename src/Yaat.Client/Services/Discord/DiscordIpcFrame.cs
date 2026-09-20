using System.Buffers.Binary;

namespace Yaat.Client.Services.Discord;

/// <summary>
/// Discord's RPC-over-IPC framing: a little-endian int32 opcode, a little-endian int32 payload
/// length, then exactly that many bytes of UTF-8 JSON. Both directions use the same shape, so one
/// pair of helpers serves the client writing a handshake and Discord answering with a frame.
/// </summary>
internal static class DiscordIpcFrame
{
    /// <summary>Opcode 0: the client's opening frame, carrying the RPC version and application id.</summary>
    public const int HandshakeOpcode = 0;

    /// <summary>Opcode 1: an ordinary JSON message in either direction (commands, responses, events).</summary>
    public const int FrameOpcode = 1;

    /// <summary>Opcode 2: either side is closing the connection.</summary>
    public const int CloseOpcode = 2;

    /// <summary>Opcode 3: a keepalive from Discord, which must be echoed back as a PONG.</summary>
    public const int PingOpcode = 3;

    /// <summary>Opcode 4: the answer to a PING, carrying the PING's payload verbatim.</summary>
    public const int PongOpcode = 4;

    /// <summary>
    /// Largest payload this client will read. Discord's own frames are a few hundred bytes; the
    /// bound exists so a corrupt or hostile length field cannot make the client allocate wildly.
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024;

    private const int HeaderBytes = 8;

    /// <summary>Writes one frame and flushes it, so the peer sees the whole message.</summary>
    public static async Task WriteAsync(Stream stream, int opcode, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), json.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads one whole frame. Throws <see cref="EndOfStreamException"/> when the peer closed the
    /// connection mid-frame (or before one started), and <see cref="InvalidDataException"/> when the
    /// declared length is negative or larger than <see cref="MaxPayloadBytes"/>.
    /// </summary>
    public static async Task<(int Opcode, byte[] Payload)> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header.AsMemory(), cancellationToken);

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if ((length < 0) || (length > MaxPayloadBytes))
        {
            throw new InvalidDataException($"Discord IPC frame declares a {length}-byte payload; expected 0..{MaxPayloadBytes}");
        }

        byte[] payload = new byte[length];
        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload.AsMemory(), cancellationToken);
        }

        return (opcode, payload);
    }
}
