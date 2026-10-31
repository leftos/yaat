using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.SpeechSandbox;

/// <summary>The <c>audio</c> struct column of a UWB-ATCC row: an embedded WAV file.</summary>
public sealed class UwbAtccAudio
{
    [JsonPropertyName("bytes")]
    public byte[]? Bytes { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>One row of the UWB-ATCC parquet split, as Parquet.Net deserializes it.</summary>
public sealed class UwbAtccRow
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("audio")]
    public UwbAtccAudio? Audio { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("segment_start_time")]
    public float? SegmentStartTime { get; set; }

    [JsonPropertyName("segment_end_time")]
    public float? SegmentEndTime { get; set; }

    [JsonPropertyName("duration")]
    public float? Duration { get; set; }
}

/// <summary>A pinned file to download: where it lives, and the size and SHA-256 (lowercase hex) it must have.</summary>
public sealed record CorpusDownload(string Url, long SizeBytes, string Sha256);

/// <summary>
/// The UWB-ATCC test split (<c>Jzuluaga/uwb_atcc</c> on Hugging Face, pinned to one revision):
/// downloaded once into the YAAT cache, hash-checked on every use, and read into 16 kHz segments.
/// </summary>
public static class UwbAtccCorpus
{
    public const string Revision = "1e8270beb8c1643c8735dddb7229fffb0e399f74";

    public static readonly CorpusDownload TestSplit = new(
        "https://huggingface.co/datasets/Jzuluaga/uwb_atcc/resolve/" + Revision + "/data/test-00000-of-00001-3a021115ca23c2a5.parquet",
        131_266_472,
        "6a637e5b035c4e22bdf880554c900c3a775eac46c10dc9713e827dd6cbf38783"
    );

    private const int MaxMoveAttempts = 8;

    private const int RetryStepMs = 25;

    private static readonly ILogger Log = AppLog.CreateLogger("UwbAtccCorpus");

    /// <summary>Where the pinned test split is cached.</summary>
    public static string DefaultCachePath => YaatPaths.Combine("cache", "atc-corpora", "uwb-atcc", Revision, "test.parquet");

    /// <summary>
    /// Downloads <paramref name="download"/> into <paramref name="cachePath"/> unless a file of the
    /// right size is already there, then checks its SHA-256. A mismatching file is deleted and the
    /// call throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<string> EnsureDownloadedAsync(HttpClient http, CorpusDownload download, string cachePath, CancellationToken ct)
    {
        if (!File.Exists(cachePath) || (new FileInfo(cachePath).Length != download.SizeBytes))
        {
            await DownloadAsync(http, download, cachePath, ct).ConfigureAwait(false);
        }
        string actual = await HashFileAsync(cachePath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(cachePath);
            string problem = $"{cachePath}: SHA-256 is {actual}, expected {download.Sha256}";
            throw new InvalidDataException($"{problem}; the file was deleted, run again to download it afresh.");
        }
        return cachePath;
    }

    /// <summary>
    /// Reads every row of <paramref name="parquetPath"/>, picks the ids <see cref="CorpusSubset.Select"/>
    /// chooses and decodes each picked row's WAV to 16 kHz, in id order. A duplicate id, or a row
    /// without text or audio bytes, throws <see cref="InvalidDataException"/> naming the id.
    /// </summary>
    public static async Task<IReadOnlyList<CorpusSegment>> LoadAsync(string parquetPath, int? subset, int seed)
    {
        IList<UwbAtccRow> rows = (await ParquetSerializer.DeserializeAsync<UwbAtccRow>(parquetPath).ConfigureAwait(false)).Data;
        Dictionary<string, ValidRow> byId = IndexRows(rows);
        IReadOnlyList<string> ids = CorpusSubset.Select([.. byId.Keys], subset, seed);
        return [.. ids.Select(id => ToSegment(byId[id]))];
    }

    /// <summary>A row checked to carry text and audio bytes.</summary>
    private sealed record ValidRow(string Id, byte[] Wav, string Text);

    private static Dictionary<string, ValidRow> IndexRows(IList<UwbAtccRow> rows)
    {
        var byId = new Dictionary<string, ValidRow>(StringComparer.Ordinal);
        foreach (UwbAtccRow row in rows)
        {
            string text = row.Text ?? throw new InvalidDataException($"UWB-ATCC row {row.Id} has no text.");
            byte[] wav = row.Audio?.Bytes ?? throw new InvalidDataException($"UWB-ATCC row {row.Id} has no audio bytes.");
            if (!byId.TryAdd(row.Id, new ValidRow(row.Id, wav, text)))
            {
                throw new InvalidDataException($"UWB-ATCC id {row.Id} appears in more than one row.");
            }
        }
        return byId;
    }

    private static CorpusSegment ToSegment(ValidRow row)
    {
        float[] samples = CorpusRoles.To16k(WavHeader.ReadPcm16WithRate(row.Wav, row.Id), row.Id);
        return new CorpusSegment(row.Id, CorpusRoles.FromId(row.Id), samples, row.Text);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task DownloadAsync(HttpClient http, CorpusDownload download, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string partial = path + ".partial";
        string megabytes = (download.SizeBytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
        Console.WriteLine($"downloading {download.Url} ({megabytes} MB)");
        try
        {
            using HttpResponseMessage response = await http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream file = File.Create(partial);
            await stream.CopyToAsync(file, 128 * 1024, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Console.Error.WriteLine($"downloading {download.Url} failed ({ex.Message}); deleting {partial}");
            File.Delete(partial);
            throw;
        }
        long got = new FileInfo(partial).Length;
        if (got != download.SizeBytes)
        {
            File.Delete(partial);
            throw new InvalidDataException($"{download.Url}: downloaded {got} bytes, expected {download.SizeBytes}.");
        }
        await MoveWithRetryAsync(partial, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="partial"/>. An antivirus scan of a file just
    /// written can deny the move for a few milliseconds, so it is retried with a growing wait (as
    /// <see cref="AtomicFile.WriteAllText"/> does) before the last failure is thrown.
    /// </summary>
    private static async Task MoveWithRetryAsync(string partial, string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partial, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && (attempt < MaxMoveAttempts))
            {
                Log.LogDebug(ex, "Moving {Partial} to {Path} failed on attempt {Attempt}; retrying", partial, path, attempt);
                await Task.Delay(RetryStepMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
