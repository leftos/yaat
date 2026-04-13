using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public enum ModelStatus
{
    NotDownloaded,
    Downloading,
    Ready,
    Failed,
}

// Legacy alias kept so existing call sites that hardcoded "WhisperModelStatus" still compile.
public enum WhisperModelStatus
{
    NotDownloaded = ModelStatus.NotDownloaded,
    Downloading = ModelStatus.Downloading,
    Ready = ModelStatus.Ready,
    Failed = ModelStatus.Failed,
}

/// <summary>Catalog entry for a recommended LLM GGUF download.</summary>
/// <param name="Id">Short stable identifier used in <see cref="UserPreferences.LlmModelPath"/> path computation.</param>
/// <param name="DisplayName">Human-readable name shown in the Settings dropdown.</param>
/// <param name="FileName">The exact filename this resolves to on disk (and at the HF URL).</param>
/// <param name="ApproxSizeMb">Approximate file size in MB, used for user-facing download estimates.</param>
/// <param name="DownloadUrl">Direct HTTPS URL to the GGUF file. Must resolve to a fixed, stable location.</param>
public sealed record LlmCatalogEntry(string Id, string DisplayName, string FileName, int ApproxSizeMb, string DownloadUrl);

/// <summary>
/// Manages local Whisper and LLM model files under <c>%LOCALAPPDATA%/yaat/models/</c>.
/// Both models download via streaming HTTPS with <c>.partial</c> → rename atomicity and live
/// progress reporting. The LLM catalog offers a curated set of recommended GGUFs (Qwen2.5 Q4_K_M
/// variants) that have been verified to work with LocalLlmCommandMapper's prompt and validator; users who want
/// to bring their own GGUF can still point <see cref="UserPreferences.LlmModelPath"/> at a custom
/// file via the Settings "Browse..." button.
/// </summary>
public sealed class ModelManager
{
    private static readonly ILogger Log = AppLog.CreateLogger<ModelManager>();

    private const string WhisperUrlTemplate = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{0}.bin";

    // Below this we assume a partial or failed download and treat the model as NotDownloaded.
    private const long MinWhisperFileBytes = 1_000_000;
    private const long MinLlmFileBytes = 10_000_000;

    public static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "yaat",
        "models"
    );

    public static readonly string WhisperDir = Path.Combine(ModelsDir, "whisper");
    public static readonly string LlmDir = Path.Combine(ModelsDir, "llm");

    public static IReadOnlyList<string> AvailableWhisperSizes { get; } = ["tiny.en", "base.en", "small.en", "medium.en"];

    // Qwen 2.5 Q4_K_M variants. Qwen 1.5B is exhaustively verified by LocalLlmPipelineIntegrationTests;
    // the 0.5B and 3B entries are size/accuracy tradeoffs with the same prompt format, so they're
    // expected to work with the existing system prompt without retuning. If a specific entry
    // drifts in future tuning, it can be removed from this list without breaking user prefs
    // because LlmModelPath is stored as a raw file path, not a catalog ID.
    public static IReadOnlyList<LlmCatalogEntry> AvailableLlmModels { get; } =
    [
        new(
            "qwen2.5-0.5b-q4km",
            "Qwen2.5-0.5B-Instruct Q4_K_M (fastest, ~400 MB)",
            "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            400,
            "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf"
        ),
        new(
            "qwen2.5-1.5b-q4km",
            "Qwen2.5-1.5B-Instruct Q4_K_M (recommended, ~1 GB)",
            "qwen2.5-1.5b-instruct-q4_k_m.gguf",
            1100,
            "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf"
        ),
        new(
            "qwen2.5-3b-q4km",
            "Qwen2.5-3B-Instruct Q4_K_M (most accurate, ~2 GB)",
            "qwen2.5-3b-instruct-q4_k_m.gguf",
            2000,
            "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf"
        ),
    ];

    // ---------- Whisper ----------

    public string GetWhisperPath(string modelSize)
    {
        return Path.Combine(WhisperDir, $"ggml-{modelSize}.bin");
    }

    public long GetWhisperFileSize(string modelSize)
    {
        return GetFileSize(GetWhisperPath(modelSize));
    }

    public WhisperModelStatus GetWhisperStatus(string modelSize)
    {
        var size = GetWhisperFileSize(modelSize);
        return (WhisperModelStatus)ClassifySize(size, MinWhisperFileBytes);
    }

    public async Task<bool> DownloadWhisperModelAsync(string modelSize, IProgress<double> progress, CancellationToken ct)
    {
        var url = string.Format(CultureInfo.InvariantCulture, WhisperUrlTemplate, modelSize);
        var destPath = GetWhisperPath(modelSize);
        return await DownloadToFileAsync(url, destPath, $"Whisper model {modelSize}", progress, ct).ConfigureAwait(false);
    }

    public bool DeleteWhisperModel(string modelSize)
    {
        return DeleteIfExists(GetWhisperPath(modelSize), $"Whisper model {modelSize}");
    }

    // ---------- LLM ----------

    public string GetLlmPath(string catalogId)
    {
        var entry = FindLlmEntry(catalogId);
        return entry is null ? string.Empty : Path.Combine(LlmDir, entry.FileName);
    }

    public long GetLlmFileSize(string catalogId)
    {
        var path = GetLlmPath(catalogId);
        return string.IsNullOrEmpty(path) ? 0 : GetFileSize(path);
    }

    public ModelStatus GetLlmStatus(string catalogId)
    {
        var size = GetLlmFileSize(catalogId);
        return ClassifySize(size, MinLlmFileBytes);
    }

    public async Task<bool> DownloadLlmModelAsync(string catalogId, IProgress<double> progress, CancellationToken ct)
    {
        var entry = FindLlmEntry(catalogId);
        if (entry is null)
        {
            Log.LogError("No catalog entry for LLM model id {CatalogId}", catalogId);
            return false;
        }

        var destPath = Path.Combine(LlmDir, entry.FileName);
        return await DownloadToFileAsync(entry.DownloadUrl, destPath, $"LLM model {entry.Id}", progress, ct).ConfigureAwait(false);
    }

    public bool DeleteLlmModel(string catalogId)
    {
        var path = GetLlmPath(catalogId);
        return !string.IsNullOrEmpty(path) && DeleteIfExists(path, $"LLM model {catalogId}");
    }

    /// <summary>
    /// Looks up a catalog entry by its downloaded file path. Used by the Settings UI to figure out
    /// whether <see cref="UserPreferences.LlmModelPath"/> refers to a catalog model (so the dropdown
    /// selection and Delete button work) or a custom file the user Browsed to.
    /// </summary>
    public LlmCatalogEntry? FindLlmEntryByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fileName = Path.GetFileName(path);
        foreach (var entry in AvailableLlmModels)
        {
            if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that a path points to a plausible GGUF file. Used for custom-path Browse flow.
    /// Does not attempt to parse the file — just checks existence, extension, and a loose size floor.
    /// </summary>
    public bool ValidateLlmModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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

    // ---------- Shared download helper ----------

    /// <summary>
    /// Streams a file from <paramref name="url"/> to <paramref name="destPath"/>. Writes to
    /// <c>{destPath}.partial</c> then renames atomically on completion so interrupted downloads
    /// don't look "Ready" on next startup. Reports progress as 0.0..1.0 when Content-Length is
    /// known; NaN otherwise.
    /// </summary>
    private static async Task<bool> DownloadToFileAsync(
        string url,
        string destPath,
        string logLabel,
        IProgress<double> progress,
        CancellationToken ct
    )
    {
        var partialPath = destPath + ".partial";
        var dir = Path.GetDirectoryName(destPath);

        try
        {
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(60);

            Log.LogInformation("Downloading {Label} from {Url}", logLabel, url);

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

            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }

            File.Move(partialPath, destPath);
            progress.Report(1.0);
            Log.LogInformation("{Label} downloaded to {Path} ({Bytes} bytes)", logLabel, destPath, downloaded);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("{Label} download cancelled", logLabel);
            TryDelete(partialPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to download {Label} from {Url}", logLabel, url);
            TryDelete(partialPath);
            return false;
        }
    }

    private static long GetFileSize(string path)
    {
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

    private static ModelStatus ClassifySize(long size, long minimum)
    {
        if (size == 0)
        {
            return ModelStatus.NotDownloaded;
        }

        return size < minimum ? ModelStatus.Failed : ModelStatus.Ready;
    }

    private static bool DeleteIfExists(string path, string logLabel)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            Log.LogInformation("Deleted {Label} at {Path}", logLabel, path);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to delete {Label} at {Path}", logLabel, path);
            return false;
        }
    }

    private static LlmCatalogEntry? FindLlmEntry(string catalogId)
    {
        foreach (var entry in AvailableLlmModels)
        {
            if (string.Equals(entry.Id, catalogId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
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
