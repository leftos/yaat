using System.IO.Compression;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Compresses and decompresses recording data using Brotli.
/// Recordings are stored as <c>.yaat-recording.br</c> (Brotli-compressed JSON).
/// Legacy <c>.yaat-recording.json</c> (plain JSON) and <c>.yaat-recording.json.gz</c>
/// (gzip-compressed JSON) are detected and decompressed transparently.
/// </summary>
public static class RecordingCompression
{
    /// <summary>Compress JSON bytes using Brotli.</summary>
    public static byte[] Compress(byte[] jsonUtf8Bytes)
    {
        using var output = new MemoryStream();
        using (var br = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            br.Write(jsonUtf8Bytes);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompress recording bytes to a UTF-8 JSON string.
    /// Detects format by magic bytes: gzip (0x1F 0x8B), Brotli (heuristic),
    /// or plain UTF-8 JSON (starts with '{' or whitespace).
    /// </summary>
    public static string Decompress(byte[] bytes)
    {
        if (bytes.Length < 2)
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // Gzip has an unambiguous magic number (0x1F 0x8B).
        if (bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            return DecompressGzip(bytes);
        }

        // Brotli has no magic number, and its first byte can be '{' or '[' (0x7B / 0x5B) — the same
        // bytes plain JSON starts with — so a first-byte "looks like JSON" test misfires on such
        // streams. Decode as Brotli and fall back to plain UTF-8 JSON only when Brotli genuinely
        // can't read it (real JSON is not a valid Brotli stream).
        if (TryDecompressBrotli(bytes, out var brotliText))
        {
            return brotliText;
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Decompress from a stream. Reads all bytes then delegates to <see cref="Decompress(byte[])"/>.
    /// </summary>
    public static string Decompress(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Decompress(ms.ToArray());
    }

    private static string DecompressGzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    }

    private static string DecompressBrotli(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(br);
        return reader.ReadToEnd();
    }

    private static bool TryDecompressBrotli(byte[] bytes, out string text)
    {
        try
        {
            text = DecompressBrotli(bytes);
            return true;
        }
        // BrotliStream throws InvalidOperationException ("Decoder ran into invalid data") on
        // non-Brotli input; InvalidDataException / IOException cover truncated or malformed streams.
        // Any of these means "not Brotli" — fall back to plain JSON.
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the byte array starts with the ZIP local file header
    /// magic bytes (<c>PK\x03\x04</c>).
    /// </summary>
    public static bool IsZipArchive(byte[] bytes)
    {
        return bytes.Length >= 4
            && bytes[0] == 0x50 // P
            && bytes[1] == 0x4B // K
            && bytes[2] == 0x03
            && bytes[3] == 0x04;
    }
}
