using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Makes sure our sherpa-onnx CPU native for each requested RID is downloaded, SHA-256 verified and extracted
/// under %LOCALAPPDATA%\yaat\cache\ (or the cache below YAAT_APPDATA_DIR when that is set), and returns the extract
/// directory per RID. A lock in each cached-zip folder (one per manifest commit) serializes that work: one build prepares
/// the native, and every other build on the machine waits for it and then keeps the finished extraction. Each zip is tried from the
/// manifest's baseUrl, then from each of natives.mirrors, until one verifies. When none does, a required build (a
/// publish) fails, and any other build warns YAATSHERPA001 and returns the RID as a fallback to NuGet's stock native.
/// Compiled by RoslynCodeTaskFactory against netstandard, so it uses only netstandard APIs.
/// </summary>
public class EnsureSherpaNatives : Microsoft.Build.Utilities.Task
{
    private const string ManifestCopyName = "sherpa-native-MANIFEST.txt";
    private const string FallbackWarningCode = "YAATSHERPA001";
    private const int LockWaitSeconds = 600;

    [Required]
    public string ManifestPath { get; set; }

    [Required]
    public string[] Rids { get; set; }

    /// <summary>True when no source verifying a RID's native fails the build instead of falling back to NuGet's.</summary>
    [Required]
    public bool Required { get; set; }

    /// <summary>One item per overlaid RID: ItemSpec is the RID, with ExtractDir and ShortCommit metadata.</summary>
    [Output]
    public ITaskItem[] Overlays { get; set; }

    /// <summary>One item per RID that keeps NuGet's stock native: ItemSpec is the RID, with Reason metadata.</summary>
    [Output]
    public ITaskItem[] Fallbacks { get; set; }

    public override bool Execute()
    {
        try
        {
            SherpaManifest manifest = ReadManifest(ManifestPath);
            string shortCommit = manifest.Natives.Commit.Length > 10 ? manifest.Natives.Commit.Substring(0, 10) : manifest.Natives.Commit;
            List<ITaskItem> overlays = new List<ITaskItem>();
            List<ITaskItem> fallbacks = new List<ITaskItem>();
            foreach (string rid in Rids.Distinct(StringComparer.Ordinal))
            {
                SherpaNativeFile entry = manifest.Natives.Files.FirstOrDefault(f => (f.Rid == rid) && (f.Variant == "cpu"));
                if (entry == null)
                {
                    continue;
                }

                try
                {
                    string extractDir = EnsureRid(SourceUrls(manifest, entry), rid, entry);
                    TaskItem item = new TaskItem(rid);
                    item.SetMetadata("ExtractDir", extractDir);
                    item.SetMetadata("ShortCommit", shortCommit);
                    overlays.Add(item);
                }
                catch (SherpaSourcesException ex)
                {
                    ReportNoSource(rid, ex, fallbacks);
                }
            }

            Overlays = overlays.ToArray();
            Fallbacks = fallbacks.ToArray();
        }
        catch (SherpaNativeException ex)
        {
            Log.LogError(ex.Message);
        }

        return !Log.HasLoggedErrors;
    }

    private static string[] SourceUrls(SherpaManifest manifest, SherpaNativeFile entry)
    {
        string fileName = entry.Key.Substring(entry.Key.LastIndexOf('/') + 1);
        List<string> urls = new List<string> { manifest.BaseUrl + entry.Key };
        if (manifest.Natives.Mirrors != null)
        {
            urls.AddRange(manifest.Natives.Mirrors.Select(mirror => mirror + fileName));
        }

        return urls.ToArray();
    }

    private void ReportNoSource(string rid, SherpaSourcesException ex, List<ITaskItem> fallbacks)
    {
        string label = rid + "-cpu";
        if (Required)
        {
            Log.LogError(
                "Sherpa native "
                    + label
                    + ": no verified native from any source; a publish never ships NuGet's stock native."
                    + string.Concat(ex.Failures.Select(failure => Environment.NewLine + "  " + failure))
            );
            return;
        }

        string warning =
            "Sherpa native "
            + label
            + ": no verified native from any source ("
            + string.Join("; ", ex.Failures)
            + "); this build ships NuGet's stock sherpa-onnx native, which has no accurate (n-best) mode. A publish refuses to fall back.";
        Log.LogWarning(
            subcategory: null,
            warningCode: FallbackWarningCode,
            helpKeyword: null,
            file: null,
            lineNumber: 0,
            columnNumber: 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: warning
        );
        TaskItem item = new TaskItem(rid);
        // Literal, so the semicolons between failures survive MSBuild's item-list splitting in WriteLinesToFile.
        ((ITaskItem2)item).SetMetadataValueLiteral("Reason", warning);
        fallbacks.Add(item);
    }

    private static SherpaManifest ReadManifest(string path)
    {
        try
        {
            using (FileStream stream = File.OpenRead(path))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SherpaManifest));
                SherpaManifest manifest = (SherpaManifest)serializer.ReadObject(stream);
                if (
                    (manifest == null)
                    || string.IsNullOrEmpty(manifest.BaseUrl)
                    || (manifest.Natives == null)
                    || (manifest.Natives.Files == null)
                    || string.IsNullOrEmpty(manifest.Natives.Commit)
                )
                {
                    throw new SherpaNativeException("Sherpa native: " + path + " lacks baseUrl, natives.commit or natives.files.");
                }

                return manifest;
            }
        }
        catch (Exception ex) when ((ex is IOException) || (ex is UnauthorizedAccessException) || (ex is SerializationException))
        {
            throw new SherpaNativeException("Sherpa native: cannot read the manifest " + path + ": " + ex.Message);
        }
    }

    private string EnsureRid(string[] urls, string rid, SherpaNativeFile entry)
    {
        string label = rid + "-cpu";
        string[] required = RequiredFiles(rid);
        try
        {
            string zipPath = Path.Combine(CacheRoot(), entry.Key.Replace('/', Path.DirectorySeparatorChar));
            string cacheDir = Path.GetDirectoryName(zipPath);
            Directory.CreateDirectory(cacheDir);
            using (AcquireLock(label, Path.Combine(cacheDir, ".lock")))
            {
                EnsureZip(label, zipPath, urls, entry.Sha256);

                string extractDir = zipPath.Substring(0, zipPath.Length - ".zip".Length);
                if (IsComplete(extractDir, required))
                {
                    return extractDir;
                }

                if (Directory.Exists(extractDir))
                {
                    Log.LogMessage(MessageImportance.High, "Sherpa native " + label + ": " + extractDir + " is incomplete; extracting it again.");
                    Directory.Delete(extractDir, true);
                }

                Extract(label, zipPath, extractDir, required);
                return extractDir;
            }
        }
        catch (Exception ex) when ((ex is IOException) || (ex is UnauthorizedAccessException))
        {
            throw new SherpaNativeException("Sherpa native " + label + ": " + ex.Message);
        }
    }

    // The inline task is compiled alone by RoslynCodeTaskFactory and cannot reference Yaat.Sim's YaatPaths, so it
    // repeats that helper's rule: the cache below YAAT_APPDATA_DIR when that is set, else below %LOCALAPPDATA%\yaat.
    private static string CacheRoot()
    {
        string root = Environment.GetEnvironmentVariable("YAAT_APPDATA_DIR");
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat");
        }

        return Path.Combine(root, "cache");
    }

    /// <summary>
    /// Opens the lock in the cached zip's folder, waiting for the build that holds it instead of racing it. The lock
    /// file stays in place; only the handle is exclusive.
    /// </summary>
    private FileStream AcquireLock(string label, string lockPath)
    {
        bool announced = false;
        for (int failures = 0; ; failures++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                if (failures >= LockWaitSeconds)
                {
                    throw new SherpaNativeException("Sherpa native " + label + ": timed out after 10 minutes waiting for " + lockPath + ".");
                }

                if (!announced)
                {
                    Log.LogMessage(
                        MessageImportance.High,
                        "Sherpa native " + label + ": waiting for another build to finish preparing it (" + lockPath + ")."
                    );
                    announced = true;
                }

                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>Whether opening the lock failed because another handle holds it: a sharing or lock violation on Windows.</summary>
    private static bool IsLockContention(IOException ex)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int code = ex.HResult & 0xFFFF;
            return (code == 32) || (code == 33);
        }

        // netstandard has no portable way to tell EWOULDBLOCK from another I/O error, so off Windows every IOException waits.
        return true;
    }

    private void EnsureZip(string label, string zipPath, string[] urls, string expected)
    {
        if (CachedZipMatches(label, zipPath, expected))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
        List<string> failures = new List<string>();
        foreach (string url in urls)
        {
            string partPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".part";
            string failure = TryDownloadVerified(label, url, partPath, expected);
            if (failure == null)
            {
                MoveIntoCache(label, partPath, zipPath, url, expected);
                return;
            }

            failures.Add(url + ": " + failure);
        }

        throw new SherpaSourcesException(failures);
    }

    private bool CachedZipMatches(string label, string zipPath, string expected)
    {
        if (File.Exists(zipPath))
        {
            string actual = Sha256Of(zipPath);
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Log.LogMessage(
                MessageImportance.High,
                "Sherpa native "
                    + label
                    + ": cached "
                    + zipPath
                    + " has SHA-256 "
                    + actual
                    + ", expected "
                    + expected
                    + "; deleting it and downloading again."
            );
            File.Delete(zipPath);
        }

        return false;
    }

    /// <summary>Downloads url to partPath and checks its SHA-256; returns null on success, else the failure, with partPath gone.</summary>
    private string TryDownloadVerified(string label, string url, string partPath, string expected)
    {
        try
        {
            Download(label, url, partPath);
        }
        catch (SherpaSourceException ex)
        {
            Log.LogMessage(MessageImportance.High, ex.Message);
            return ex.Reason;
        }

        string partSha = Sha256Of(partPath);
        if (string.Equals(partSha, expected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        File.Delete(partPath);
        Log.LogMessage(
            MessageImportance.High,
            "Sherpa native "
                + label
                + ": SHA-256 of "
                + partPath
                + " is "
                + partSha
                + ", but "
                + ManifestPath
                + " lists "
                + expected
                + " for "
                + url
                + "."
        );
        return "SHA-256 " + partSha + ", expected " + expected;
    }

    private void MoveIntoCache(string label, string partPath, string zipPath, string url, string expected)
    {
        try
        {
            File.Move(partPath, zipPath);
        }
        catch (IOException) when (File.Exists(zipPath))
        {
            File.Delete(partPath);
            string raced = Sha256Of(zipPath);
            if (!string.Equals(raced, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SherpaNativeException(
                    "Sherpa native "
                        + label
                        + ": SHA-256 of "
                        + zipPath
                        + " is "
                        + raced
                        + ", but "
                        + ManifestPath
                        + " lists "
                        + expected
                        + " for "
                        + url
                        + "."
                );
            }
        }
    }

    private static void Download(string label, string url, string partPath)
    {
        try
        {
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (Stream body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (FileStream file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    body.CopyTo(file);
                }
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException) || (ex is IOException) || (ex is OperationCanceledException))
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }

            throw new SherpaSourceException(
                "Sherpa native " + label + ": download of " + url + " failed: " + ex.Message,
                "download failed: " + ex.Message
            );
        }
    }

    private static void Extract(string label, string zipPath, string extractDir, string[] required)
    {
        string tempDir = extractDir + "." + Guid.NewGuid().ToString("N") + ".tmp";
        ZipFile.ExtractToDirectory(zipPath, tempDir);
        string manifestText = Path.Combine(tempDir, "MANIFEST.txt");
        if (File.Exists(manifestText))
        {
            File.Copy(manifestText, Path.Combine(tempDir, ManifestCopyName));
        }

        if (!IsComplete(tempDir, required))
        {
            Directory.Delete(tempDir, true);
            throw new SherpaNativeException(
                "Sherpa native " + label + ": " + zipPath + " does not hold " + string.Join(", ", required.Take(3)) + " at its root."
            );
        }

        try
        {
            Directory.Move(tempDir, extractDir);
        }
        catch (IOException) when (Directory.Exists(extractDir))
        {
            Directory.Delete(tempDir, true);
            if (!IsComplete(extractDir, required))
            {
                throw new SherpaNativeException(
                    "Sherpa native " + label + ": " + extractDir + " appeared incomplete while extracting " + zipPath + "."
                );
            }
        }
    }

    private static string[] RequiredFiles(string rid)
    {
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            return new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll", "MANIFEST.txt", ManifestCopyName };
        }

        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            return new[] { "libsherpa-onnx-c-api.so", "libonnxruntime.so", "MANIFEST.txt", ManifestCopyName };
        }

        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            return new[] { "libsherpa-onnx-c-api.dylib", "libonnxruntime.dylib", "MANIFEST.txt", ManifestCopyName };
        }

        throw new SherpaNativeException("Sherpa native " + rid + "-cpu: no library names are known for RID " + rid + ".");
    }

    private static bool IsComplete(string dir, string[] required) => required.All(name => File.Exists(Path.Combine(dir, name)));

    private static string Sha256Of(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private sealed class SherpaNativeException : Exception
    {
        public SherpaNativeException(string message)
            : base(message) { }
    }

    /// <summary>One source URL failed; Message is the full log text, Reason the short form for the summary.</summary>
    private sealed class SherpaSourceException : Exception
    {
        public SherpaSourceException(string message, string reason)
            : base(message)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>Every source URL failed; each failure is "url: reason", in the order tried.</summary>
    private sealed class SherpaSourcesException : Exception
    {
        public SherpaSourcesException(List<string> failures)
            : base("Sherpa native: no source verified.")
        {
            Failures = failures;
        }

        public List<string> Failures { get; }
    }
}

[DataContract]
public class SherpaManifest
{
    [DataMember(Name = "baseUrl")]
    public string BaseUrl { get; set; }

    [DataMember(Name = "natives")]
    public SherpaNatives Natives { get; set; }
}

[DataContract]
public class SherpaNatives
{
    [DataMember(Name = "commit")]
    public string Commit { get; set; }

    [DataMember(Name = "mirrors")]
    public string[] Mirrors { get; set; }

    [DataMember(Name = "files")]
    public SherpaNativeFile[] Files { get; set; }
}

[DataContract]
public class SherpaNativeFile
{
    [DataMember(Name = "rid")]
    public string Rid { get; set; }

    [DataMember(Name = "variant")]
    public string Variant { get; set; }

    [DataMember(Name = "key")]
    public string Key { get; set; }

    [DataMember(Name = "sha256")]
    public string Sha256 { get; set; }
}
