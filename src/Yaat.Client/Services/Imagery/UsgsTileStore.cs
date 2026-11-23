using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Services.Imagery;

/// <summary>A tile's bytes as the disk cache holds them, and whether they are young enough to use without asking again.</summary>
/// <param name="Bytes">The JPEG as it was downloaded.</param>
/// <param name="IsFresh">True when the file is younger than the store's freshness window.</param>
internal sealed record StoredTile(byte[] Bytes, bool IsFresh);

/// <summary>
/// The USGS tiles' disk cache: <c>{root}/{z}/{x}/{y}.jpg</c>, each written through a temp file beside it and a move, so a
/// tile path never holds a partial JPEG; another YAAT on the same folder can read and write it at the same time.
/// </summary>
/// <param name="root">The cache's root folder.</param>
/// <param name="time">The clock a file's age is measured against and a written file is stamped with.</param>
internal sealed class UsgsTileStore(string root, TimeProvider time)
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromDays(90);

    private static readonly TimeSpan LeftoverTempAge = TimeSpan.FromHours(1);

    private readonly ILogger _log = AppLog.CreateLogger<UsgsTileStore>();

    private readonly ConcurrentDictionary<string, byte> _sweptFolders = new(StringComparer.Ordinal);

    /// <summary>The tile's path under the root.</summary>
    public string PathFor(TileKey key) =>
        Path.Combine(
            root,
            key.Z.ToString(CultureInfo.InvariantCulture),
            key.X.ToString(CultureInfo.InvariantCulture),
            string.Create(CultureInfo.InvariantCulture, $"{key.Y}.jpg")
        );

    /// <summary>The stored tile at <paramref name="path"/>, or null when there is none or it cannot be read.</summary>
    public StoredTile? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            DateTime written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return new StoredTile(copy.ToArray(), (time.GetUtcNow().UtcDateTime - written) < FreshFor);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(ex, "Reading the cached imagery tile {Path} failed", path);
            }
            return null;
        }
    }

    /// <summary>
    /// Writes the tile: a new one (<paramref name="overwrite"/> false) counts another writer's copy as its own, a refresh
    /// replaces the old file once and keeps it when that fails. A failed write is logged, never thrown.
    /// </summary>
    public void Write(string path, byte[] bytes, bool overwrite)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string folder = Path.GetDirectoryName(path) ?? throw new ArgumentException($"The tile path {path} has no folder", nameof(path));
            Directory.CreateDirectory(folder);
            SweepOnce(folder);
            File.WriteAllBytes(temp, bytes);
            File.SetLastWriteTimeUtc(temp, time.GetUtcNow().UtcDateTime);
            MoveIntoPlace(temp, path, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(ex, "Writing the imagery tile {Path} failed; it is not cached", path);
            }
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    private static void MoveIntoPlace(string temp, string path, bool overwrite)
    {
        try
        {
            File.Move(temp, path, overwrite);
        }
        catch (IOException) when ((!overwrite) && (File.Exists(path)))
        {
            // Another writer (a second window or a second YAAT) stored the same tile first; its copy is as good.
        }
    }

    // Temp files a crashed writer left behind, deleted the first time this session writes into their folder.
    private void SweepOnce(string folder)
    {
        if (!_sweptFolders.TryAdd(folder, 0))
        {
            return;
        }

        DateTime cutoff = time.GetUtcNow().UtcDateTime - LeftoverTempAge;
        foreach (string leftover in Directory.EnumerateFiles(folder, "*.tmp"))
        {
            if (File.GetLastWriteTimeUtc(leftover) < cutoff)
            {
                DeleteQuietly(leftover);
            }
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(ex, "Deleting the temporary tile file {Path} failed; it is left behind", path);
            }
        }
    }
}
