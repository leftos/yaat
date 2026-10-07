using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Reads and writes the per-airport precompute files: one Brotli-compressed UTF-8 JSON document per airport,
/// <c>{airportId}.json.br</c>, under <c>root</c>.
/// </summary>
public sealed class PrecomputeStore(string root)
{
    private static readonly ILogger Log = SimLog.CreateLogger("PrecomputeStore");

    /// <summary>The replacing move of a write is retried this many times, waiting an increasing multiple of <see cref="MoveRetryStepMs"/>.</summary>
    private const int MaxMoveAttempts = 8;

    private const int MoveRetryStepMs = 25;

    /// <summary>The file one airport's entry is stored in, the id upper-cased.</summary>
    public string PathFor(string airportId) => Path.Combine(root, $"{airportId.ToUpperInvariant()}.json.br");

    /// <summary>
    /// Reads the entry for <paramref name="airportId"/>, false when no file exists. A file that cannot be opened, or
    /// decompressed, or parsed, or that carries no key/layout/push targets, throws <see cref="InvalidDataException"/>
    /// naming its path.
    /// </summary>
    public bool TryRead(string airportId, [NotNullWhen(true)] out PrecomputeEntry? entry)
    {
        string path = PathFor(airportId);
        if (!File.Exists(path))
        {
            entry = null;
            return false;
        }

        // Opened outside the try so a sharing violation or an access denial surfaces as itself, not as corruption.
        using FileStream file = File.OpenRead(path);
        PrecomputeEntry? read;
        try
        {
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            read = JsonSerializer.Deserialize<PrecomputeEntry>(brotli, GroundLayoutSerializer.Options);
        }
        // BrotliStream throws InvalidOperationException ("Decoder ran into invalid data") on non-Brotli input; the other
        // three cover a truncated or malformed stream (InvalidDataException, IOException) and a non-JSON document
        // (JsonException).
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or JsonException)
        {
            throw new InvalidDataException($"Corrupt precompute cache file: {path}", ex);
        }

        // Checked outside the try so this text reaches the caller rather than being re-wrapped as corruption.
        if ((read is null) || (read.Key is null) || (read.Layout is null) || (read.PushTargets is null))
        {
            throw new InvalidDataException($"Precompute cache file has no usable entry: {path}");
        }

        // The repair walks all three collections; a document that drops one would otherwise fail with a bare
        // NullReferenceException naming no file.
        if ((read.Layout.Nodes is null) || (read.Layout.Edges is null) || (read.Layout.Arcs is null))
        {
            throw new InvalidDataException($"Precompute cache file has a layout with no nodes, edges or arcs: {path}");
        }

        GroundLayoutSerializer.Repair(read.Layout);
        entry = read;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="entry"/>, replacing any existing file through a uniquely named temporary sibling and a
    /// rename. The bytes are identical for the same entry: the documents' declaration order is fixed, the push targets
    /// are sorted by stand then design group, and nothing is timestamped.
    /// </summary>
    public void Write(PrecomputeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(entry.AirportId, entry.Layout.AirportId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Entry airport id '{entry.AirportId}' does not match its layout's '{entry.Layout.AirportId}'.",
                nameof(entry)
            );
        }

        PrecomputeEntry ordered = entry with
        {
            PushTargets =
            [
                .. entry
                    .PushTargets.OrderBy(target => target.StandName, StringComparer.Ordinal)
                    .ThenBy(target => target.DesignGroup, StringComparer.Ordinal),
            ],
        };

        Directory.CreateDirectory(root);
        string path = PathFor(entry.AirportId);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream file = File.Create(tempPath))
            {
                using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
                JsonSerializer.Serialize(brotli, ordered, GroundLayoutSerializer.Options);
            }

            ReplaceWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="tempPath"/>. The replacing move is denied while a scan or
    /// another process holds the target open without delete sharing, so it is retried with a growing wait (about 0.7 s
    /// in all) before the last failure is thrown. Mirrors the retry in <c>HttpFileCache.WriteAtomicallyAsync</c>, which
    /// is private, asynchronous and writes a string, so it cannot be reused here.
    /// </summary>
    private static void ReplaceWithRetry(string tempPath, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && (attempt < MaxMoveAttempts))
            {
                Log.LogDebug(ex, "Replacing {Path} failed on attempt {Attempt}; retrying", path, attempt);
                Thread.Sleep(MoveRetryStepMs * attempt);
            }
        }
    }
}
