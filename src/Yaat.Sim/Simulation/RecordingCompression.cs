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

        // Gzip magic: 0x1F 0x8B
        if (bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            return DecompressGzip(bytes);
        }

        // Plain JSON: starts with '{', '[', or UTF-8 BOM, or whitespace before '{'
        if (LooksLikePlainJson(bytes))
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // Assume Brotli for everything else
        return DecompressBrotli(bytes);
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

    private static bool LooksLikePlainJson(byte[] bytes)
    {
        // Skip UTF-8 BOM if present
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            start = 3;
        }

        // Skip leading whitespace
        while (start < bytes.Length && (bytes[start] == ' ' || bytes[start] == '\t' || bytes[start] == '\r' || bytes[start] == '\n'))
        {
            start++;
        }

        if (start >= bytes.Length)
        {
            return false;
        }

        return bytes[start] == '{' || bytes[start] == '[';
    }
}
