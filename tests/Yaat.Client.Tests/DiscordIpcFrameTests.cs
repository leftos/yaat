using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Yaat.Client.Services.Discord;

namespace Yaat.Client.Tests;

/// <summary>
/// Discord's RPC-over-IPC framing is a fixed binary contract — a little-endian opcode, a
/// little-endian length, then the UTF-8 JSON — so the header layout is pinned byte for byte here
/// rather than only round-tripped through our own writer.
/// </summary>
public class DiscordIpcFrameTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsOpcodeAndPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("""{"cmd":"SET_ACTIVITY"}""");
        using var stream = new MemoryStream();

        await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.FrameOpcode, payload, TestContext.Current.CancellationToken);
        stream.Position = 0;
        (int opcode, byte[] read) = await DiscordIpcFrame.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(DiscordIpcFrame.FrameOpcode, opcode);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Write_EmitsALittleEndianOpcodeAndLengthHeader()
    {
        using var stream = new MemoryStream();

        await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.HandshakeOpcode, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        byte[] bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 0, 3, 0, 0, 0, 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task Read_RejectsAPayloadLargerThanTheCap()
    {
        using var stream = new MemoryStream(Header(DiscordIpcFrame.FrameOpcode, DiscordIpcFrame.MaxPayloadBytes + 1));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await DiscordIpcFrame.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_RejectsANegativeLength()
    {
        using var stream = new MemoryStream(Header(DiscordIpcFrame.FrameOpcode, -1));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await DiscordIpcFrame.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_ThrowsWhenThePeerClosedMidFrame()
    {
        using var stream = new MemoryStream(Header(DiscordIpcFrame.FrameOpcode, 16));

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await DiscordIpcFrame.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    private static byte[] Header(int opcode, int length)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), length);
        return header;
    }
}
