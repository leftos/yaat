using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Makes sure our sherpa-onnx CPU native for each requested RID is downloaded, SHA-256 verified and extracted
/// under %LOCALAPPDATA%\yaat\cache\, and returns the extract directory per RID. Compiled by RoslynCodeTaskFactory
/// against netstandard, so it uses only netstandard APIs.
/// </summary>
public class EnsureSherpaNatives : Task
{
    private const string ManifestCopyName = "sherpa-native-MANIFEST.txt";

    [Required]
    public string ManifestPath { get; set; }

    [Required]
    public string[] Rids { get; set; }

    /// <summary>One item per overlaid RID: ItemSpec is the RID, with ExtractDir and ShortCommit metadata.</summary>
    [Output]
    public ITaskItem[] Overlays { get; set; }

    public override bool Execute()
    {
        try
        {
            SherpaManifest manifest = ReadManifest(ManifestPath);
            string shortCommit = manifest.Natives.Commit.Length > 10 ? manifest.Natives.Commit.Substring(0, 10) : manifest.Natives.Commit;
            List<ITaskItem> overlays = new List<ITaskItem>();
            foreach (string rid in Rids.Distinct(StringComparer.Ordinal))
            {
                SherpaNativeFile entry = manifest.Natives.Files.FirstOrDefault(f => (f.Rid == rid) && (f.Variant == "cpu"));
                if (entry == null)
                {
                    continue;
                }

                string extractDir = EnsureRid(manifest.BaseUrl, rid, entry);
                TaskItem item = new TaskItem(rid);
                item.SetMetadata("ExtractDir", extractDir);
                item.SetMetadata("ShortCommit", shortCommit);
                overlays.Add(item);
            }

            Overlays = overlays.ToArray();
        }
        catch (SherpaNativeException ex)
        {
            Log.LogError(ex.Message);
        }

        return !Log.HasLoggedErrors;
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

    private string EnsureRid(string baseUrl, string rid, SherpaNativeFile entry)
    {
        string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat", "cache");
        string zipPath = Path.Combine(cacheRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
        string url = baseUrl + entry.Key;
        string label = rid + "-cpu";
        string[] required = RequiredFiles(rid);

        EnsureZip(label, zipPath, url, entry.Sha256);

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

    private void EnsureZip(string label, string zipPath, string url, string expected)
    {
        if (File.Exists(zipPath))
        {
            string actual = Sha256Of(zipPath);
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
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

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
        string partPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".part";
        Download(label, url, partPath);

        string partSha = Sha256Of(partPath);
        if (!string.Equals(partSha, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new SherpaNativeException(
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
        }

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

            throw new SherpaNativeException("Sherpa native " + label + ": download of " + url + " failed: " + ex.Message);
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
