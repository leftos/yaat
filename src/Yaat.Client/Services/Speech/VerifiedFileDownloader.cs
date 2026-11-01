using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Speech;

/// <summary>One file pinned by size and SHA-256, and the URLs it is served from.</summary>
/// <param name="Name">The file's name in logs and error messages.</param>
/// <param name="Urls">Where the file is served, tried in order until one serves the pinned bytes.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Sha256">The file's SHA-256, hex.</param>
internal sealed record VerifiedFile(string Name, IReadOnlyList<string> Urls, long Size, string Sha256);

/// <summary>
/// Downloads one pinned file to a path, trying each of its URLs in turn. A URL fails over to the next on an HTTP error, a
/// header or body idle timeout, an announced or served length other than the pinned size, or a SHA-256 mismatch; only the
/// caller's token cancels the download.
/// </summary>
/// <param name="http">The client the file is downloaded with.</param>
/// <param name="timeProvider">The clock the body idle timeout runs on.</param>
internal sealed class VerifiedFileDownloader(HttpClient http, TimeProvider timeProvider)
{
    /// <summary>How long one read of a download body may wait for bytes before that URL counts as failed.</summary>
    public static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(60);

    private static readonly ILogger Log = AppLog.CreateLogger<VerifiedFileDownloader>();

    private readonly HttpClient _http = http;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>Downloads <paramref name="file"/> to <paramref name="destPath"/> from the first URL that serves its pinned bytes.</summary>
    /// <param name="file">The file to download.</param>
    /// <param name="destPath">Where the file is written; deleted again when a URL fails.</param>
    /// <param name="onBytesRead">Called with the bytes read so far from the current URL, after each chunk is written.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The URL that served the file.</returns>
    /// <exception cref="InvalidDataException">Every URL failed, the last with a size or SHA-256 mismatch.</exception>
    /// <exception cref="HttpRequestException">Every URL failed, the last with an HTTP error or a timeout.</exception>
    public async Task<string> DownloadAsync(VerifiedFile file, string destPath, Action<long> onBytesRead, CancellationToken ct)
    {
        List<string> failures = [];
        Exception? lastFailure = null;
        foreach (string url in file.Urls)
        {
            try
            {
                await DownloadVerifiedAsync(file, url, destPath, onBytesRead, ct).ConfigureAwait(false);
                return url;
            }
            catch (Exception ex)
                when ((ex is HttpRequestException or HttpIOException or InvalidDataException)
                    || ((ex is OperationCanceledException) && !ct.IsCancellationRequested)
                )
            {
                Log.LogWarning(ex, "{Name}: download from {Url} failed", file.Name, url);
                failures.Add($"{url}: {ex.Message}");
                lastFailure = ex;
                if (File.Exists(destPath))
                {
                    File.Delete(destPath);
                }
            }
        }

        string message = $"{file.Name}: every URL failed. {string.Join(" | ", failures)}";
        throw (lastFailure is InvalidDataException) ? new InvalidDataException(message, lastFailure) : new HttpRequestException(message, lastFailure);
    }

    private async Task DownloadVerifiedAsync(VerifiedFile file, string url, string destPath, Action<long> onBytesRead, CancellationToken ct)
    {
        Log.LogInformation("Downloading {Name} from {Url}", file.Name, url);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if ((length is not null) && (length != file.Size))
        {
            throw new InvalidDataException($"{file.Name}: {url} announces {length} bytes, expected {file.Size}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        long bytesRead = 0;
        await using (Stream stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
        await using (FileStream output = File.Create(destPath))
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                idle.CancelAfter(IdleReadTimeout);
                int read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0)
                {
                    break;
                }
                bytesRead += read;
                if (bytesRead > file.Size)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onBytesRead(bytesRead);
            }
        }

        if (bytesRead > file.Size)
        {
            File.Delete(destPath);
            throw new InvalidDataException($"{file.Name}: {url} sent more than the expected {file.Size} bytes");
        }
        if (bytesRead != file.Size)
        {
            File.Delete(destPath);
            throw new InvalidDataException($"{file.Name}: {url} sent {bytesRead} bytes, expected {file.Size}");
        }

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destPath);
            throw new InvalidDataException($"{file.Name}: SHA-256 of {url} is {actual}, expected {file.Sha256}");
        }
    }
}
