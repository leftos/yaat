using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public enum WhisperModelStatus
{
    NotDownloaded,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Manages local Whisper and LLM model files under %LOCALAPPDATA%/yaat/models/.
/// Whisper models are downloaded from Hugging Face (ggerganov/whisper.cpp).
/// LLM GGUF files are supplied by the user via file picker — we only validate the path.
/// </summary>
public sealed class ModelManager
{
    private static readonly ILogger Log = AppLog.CreateLogger<ModelManager>();

    private const string WhisperUrlTemplate = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{0}.bin";
    private const long MinWhisperFileBytes = 1_000_000; // below this we assume a partial / failed download

    // Minimum reasonable GGUF size. Real models start at ~100 MB; we use 10 MB as a loose sanity floor.
    private const long MinLlmFileBytes = 10_000_000;

    public static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "yaat",
        "models"
    );

    public static readonly string WhisperDir = Path.Combine(ModelsDir, "whisper");

    public static IReadOnlyList<string> AvailableWhisperSizes { get; } = ["tiny.en", "base.en", "small.en", "medium.en"];

    public string GetWhisperPath(string modelSize)
    {
        return Path.Combine(WhisperDir, $"ggml-{modelSize}.bin");
    }

    public long GetWhisperFileSize(string modelSize)
    {
        var path = GetWhisperPath(modelSize);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public WhisperModelStatus GetWhisperStatus(string modelSize)
    {
        var size = GetWhisperFileSize(modelSize);
        if (size == 0)
        {
            return WhisperModelStatus.NotDownloaded;
        }

        if (size < MinWhisperFileBytes)
        {
            return WhisperModelStatus.Failed;
        }

        return WhisperModelStatus.Ready;
    }

    /// <summary>
    /// Downloads a Whisper model from Hugging Face to %LOCALAPPDATA%/yaat/models/whisper/.
    /// Streams to {path}.partial then renames on success so incomplete files don't look Ready on restart.
    /// Progress is reported as 0.0..1.0 when Content-Length is known; NaN otherwise.
    /// Returns true on successful completion, false on any error or cancellation.
    /// </summary>
    public async Task<bool> DownloadWhisperModelAsync(string modelSize, IProgress<double> progress, CancellationToken ct)
    {
        var finalPath = GetWhisperPath(modelSize);
        var partialPath = finalPath + ".partial";
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, WhisperUrlTemplate, modelSize);

        try
        {
            Directory.CreateDirectory(WhisperDir);
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            Log.LogInformation("Downloading Whisper model {ModelSize} from {Url}", modelSize, url);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;
            var buffer = new byte[81920];

            await using (var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        progress.Report((double)downloaded / totalBytes);
                    }
                    else
                    {
                        progress.Report(double.NaN);
                    }
                }
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partialPath, finalPath);
            progress.Report(1.0);
            Log.LogInformation("Whisper model {ModelSize} downloaded to {Path} ({Bytes} bytes)", modelSize, finalPath, downloaded);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Whisper model {ModelSize} download cancelled", modelSize);
            TryDelete(partialPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to download Whisper model {ModelSize} from {Url}", modelSize, url);
            TryDelete(partialPath);
            return false;
        }
    }

    public bool DeleteWhisperModel(string modelSize)
    {
        var path = GetWhisperPath(modelSize);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            Log.LogInformation("Deleted Whisper model {ModelSize} at {Path}", modelSize, path);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to delete Whisper model {ModelSize} at {Path}", modelSize, path);
            return false;
        }
    }

    /// <summary>
    /// Validates that a path points to a plausible GGUF file.
    /// Does not attempt to parse the file — just checks existence, extension, and a loose size floor.
    /// </summary>
    public bool ValidateLlmModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return new FileInfo(path).Length >= MinLlmFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Failed to delete partial file {Path}", path);
        }
    }
}
