using System.Formats.Tar;
using System.Globalization;
using System.Net.Http;
using SharpCompress.Compressors.BZip2;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Downloads and extracts the <c>--tts-bench</c> model archives into a cache folder (one folder per model id,
/// marked complete by a <c>.complete</c> file holding the archive's byte count), and measures each asset's download
/// size from its <c>Content-Length</c>. The download and BZip2 + tar extraction follow <c>PiperVoiceInstaller</c>.
/// </summary>
internal sealed class TtsBenchDownloader(HttpClient http, string cacheRoot)
{
    private const string CompleteMarker = ".complete";

    /// <summary>The asset's size in bytes from a HEAD request's <c>Content-Length</c>.</summary>
    public async Task<long> ContentLengthAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength ?? throw new InvalidDataException($"No Content-Length for {url}.");
    }

    /// <summary>The archive byte count a cached model's <c>.complete</c> marker holds, or null when the model is not cached with one.</summary>
    public long? CachedModelBytes(string id)
    {
        string marker = Path.Combine(cacheRoot, id, CompleteMarker);
        return File.Exists(marker) && long.TryParse(File.ReadAllText(marker).Trim(), CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
    }

    /// <summary>The size of a downloaded vocoder (stored only after its size matched the download's), or null when it is not cached.</summary>
    public long? CachedVocoderBytes(string fileName)
    {
        var file = new FileInfo(VocoderPath(fileName));
        return file.Exists ? file.Length : null;
    }

    /// <summary>Downloads and extracts <paramref name="asset"/> once; returns the model folder (the archive's single top folder).</summary>
    public async Task<string> EnsureModelAsync(string id, string asset, long expectedBytes, CancellationToken ct)
    {
        string dir = Path.Combine(cacheRoot, id);
        string marker = Path.Combine(dir, CompleteMarker);
        if (!File.Exists(marker))
        {
            string archive = Path.Combine(cacheRoot, asset);
            await EnsureFileAsync(TtsBenchModels.ModelBaseUrl + asset, archive, expectedBytes, ct).ConfigureAwait(false);
            await ExtractFreshAsync(archive, dir, ct).ConfigureAwait(false);
            File.Delete(archive);
        }
        if (CachedModelBytes(id) != expectedBytes)
        {
            await File.WriteAllTextAsync(marker, expectedBytes.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        }
        string[] tops = Directory.GetDirectories(dir);
        return tops.Length == 1 ? tops[0] : throw new InvalidDataException($"Expected one top folder in {dir}, found {tops.Length}.");
    }

    /// <summary>Downloads a vocoder file once into <c>vocoders/</c>; returns its path.</summary>
    public async Task<string> EnsureVocoderAsync(string fileName, long expectedBytes, CancellationToken ct)
    {
        string path = VocoderPath(fileName);
        await EnsureFileAsync(TtsBenchModels.VocoderBaseUrl + fileName, path, expectedBytes, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Total size of the files under <paramref name="dir"/>.</summary>
    public static long DiskBytes(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private string VocoderPath(string fileName) => Path.Combine(cacheRoot, "vocoders", fileName);

    /// <summary>
    /// Extracts <paramref name="archive"/> into an emptied <paramref name="dir"/>. An archive of the right size that
    /// fails to extract is corrupt, so it and the half-extracted folder are deleted before the error goes on.
    /// </summary>
    private static async Task ExtractFreshAsync(string archive, string dir, CancellationToken ct)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
        Console.WriteLine($"  extracting {Path.GetFileName(archive)}");
        try
        {
            await Task.Run(() => ExtractArchive(archive, dir), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"  extracting {archive} failed ({ex.Message}); deleting the archive and {dir}");
            File.Delete(archive);
            Directory.Delete(dir, recursive: true);
            throw;
        }
    }

    private async Task EnsureFileAsync(string url, string path, long expectedBytes, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length == expectedBytes)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".partial";
        Console.WriteLine($"  downloading {url} ({expectedBytes / (1024.0 * 1024.0):F1} MB)");
        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream file = File.Create(partial);
            await stream.CopyToAsync(file, 128 * 1024, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Console.Error.WriteLine($"  downloading {url} failed ({ex.Message}); deleting {partial}");
            File.Delete(partial);
            throw;
        }
        long got = new FileInfo(partial).Length;
        if (got != expectedBytes)
        {
            File.Delete(partial);
            throw new InvalidDataException($"{url}: downloaded {got} bytes, expected {expectedBytes}.");
        }
        File.Move(partial, path, overwrite: true);
    }

    // decompressConcatenated: true because some release archives are multi-stream (pbzip2) BZip2 files.
    private static void ExtractArchive(string archivePath, string destRoot)
    {
        using FileStream file = File.OpenRead(archivePath);
        using var bzip = BZip2Stream.Create(file, SharpCompressionMode.Decompress, decompressConcatenated: true, leaveOpen: false);
        TarFile.ExtractToDirectory(bzip, destRoot, overwriteFiles: true);
    }
}
