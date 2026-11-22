using System.Collections.Immutable;
using System.Text.Json;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Why an airport map opened from the vNAS cache is the cache's copy rather than what vNAS serves now.</summary>
public enum CachedCopyReason
{
    /// <summary>Not a stale copy: vNAS answered (or the map came from a file).</summary>
    None,

    /// <summary>vNAS could not be reached.</summary>
    Offline,

    /// <summary>vNAS answered that it has no map for the airport (HTTP 404) any more.</summary>
    NoLongerListed,
}

/// <summary>
/// How an Airport Editor open went: the map it opened, or why it opened nothing, one record per case.
/// </summary>
public abstract record AirportMapOpenOutcome
{
    private AirportMapOpenOutcome() { }

    /// <summary>Gets the status line the editor shows for this outcome.</summary>
    public abstract string Message { get; }

    /// <summary>The map opened.</summary>
    /// <param name="Document">The map read.</param>
    /// <param name="SourceLabel">Where it came from: <c>{FAA} (vNAS cache)</c>, or the file's path.</param>
    /// <param name="CachedCopy">Why the map is the cache's copy, or <see cref="CachedCopyReason.None"/>.</param>
    /// <param name="FaaCode">The airport's FAA code for a map from the vNAS cache; null for a file.</param>
    public sealed record Opened(AirportMapDocument Document, string SourceLabel, CachedCopyReason CachedCopy, string? FaaCode) : AirportMapOpenOutcome
    {
        /// <inheritdoc />
        public override string Message =>
            CachedCopy switch
            {
                CachedCopyReason.Offline => "vNAS not reachable; showing the cached copy.",
                CachedCopyReason.NoLongerListed => $"vNAS no longer lists {FaaCode}; showing the cached copy.",
                _ => $"Opened {SourceLabel}: {Document.Features.Count} features.",
            };
    }

    /// <summary>An open that opened nothing; the editor keeps the map it had.</summary>
    public abstract record Failed : AirportMapOpenOutcome
    {
        private protected Failed() { }
    }

    /// <summary>The text asked for is not an airport id, so nothing was fetched.</summary>
    /// <param name="Id">The text as given.</param>
    public sealed record InvalidAirportId(string Id) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"'{Id}' is not an airport id.";
    }

    /// <summary>vNAS has no map for the airport and the cache holds no copy.</summary>
    /// <param name="FaaCode">The airport's FAA code.</param>
    public sealed record NotFound(string FaaCode) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"No vNAS map for {FaaCode}.";
    }

    /// <summary>vNAS could not be reached and the cache holds no copy of the airport's map.</summary>
    /// <param name="FaaCode">The airport's FAA code.</param>
    public sealed record Unreachable(string FaaCode) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"Couldn't reach vNAS for {FaaCode}, and no copy is cached.";
    }

    /// <summary>The file does not exist.</summary>
    /// <param name="Path">The path asked for.</param>
    public sealed record FileMissing(string Path) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"File not found: {Path}";
    }

    /// <summary>The folder does not exist.</summary>
    /// <param name="Folder">The path asked for.</param>
    public sealed record FolderMissing(string Folder) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"Folder not found: {Folder}";
    }

    /// <summary>The file, folder or cache could not be read, or the file is not an airport map.</summary>
    /// <param name="Path">The file's, folder's or cache file's path.</param>
    /// <param name="ReaderMessage">The reader's (or the file system's) own message.</param>
    public sealed record Unreadable(string Path, string ReaderMessage) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"Couldn't read {Path}: {ReaderMessage}";
    }

    /// <summary>The folder holds no <c>.geojson</c> file.</summary>
    /// <param name="Folder">The folder's path.</param>
    public sealed record FolderNoMap(string Folder) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"No .geojson file in {Folder}.";
    }

    /// <summary>The folder holds more than one <c>.geojson</c> file, so which map to open is the user's choice.</summary>
    /// <param name="Folder">The folder's path.</param>
    /// <param name="FileNames">The <c>.geojson</c> files' names, in ordinal order.</param>
    public sealed record FolderSeveral(string Folder, ImmutableArray<string> FileNames) : Failed
    {
        /// <inheritdoc />
        public override string Message => $"{Folder} holds several .geojson files: {string.Join(", ", FileNames)}. Open one of them.";
    }
}

/// <summary>
/// Opens an airport map for the Airport Editor from the vNAS cache, a file, or a folder holding one combined
/// <c>.geojson</c>, off the UI thread. It only reads: the one writer of the cache is the downloader's own refresh.
/// </summary>
/// <param name="downloader">The vNAS airport-map downloader whose cache the editor opens from.</param>
public sealed class AirportMapSourceLoader(AirportLayoutDownloader downloader)
{
    private readonly AirportLayoutDownloader _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

    /// <summary>
    /// Opens an airport's vNAS map through the downloader, which refreshes the cache copy from vNAS when it can; the
    /// cache's copy opens when vNAS cannot be reached or no longer lists the airport.
    /// </summary>
    /// <param name="airportId">The airport's FAA or ICAO id: 3 or 4 ASCII letters or digits, spaces around it ignored.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The outcome.</returns>
    public Task<AirportMapOpenOutcome> OpenFromCacheAsync(string airportId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(airportId);
        string trimmed = airportId.Trim();
        if ((trimmed.Length is < 3 or > 4) || !trimmed.All(char.IsAsciiLetterOrDigit))
        {
            return Task.FromResult<AirportMapOpenOutcome>(new AirportMapOpenOutcome.InvalidAirportId(airportId));
        }

        string faaCode = AirportLayoutDownloader.ToFaaCode(trimmed);
        return Task.Run(() => FetchAsync(faaCode, cancellationToken), cancellationToken);
    }

    /// <summary>Opens a combined airport-map file (<c>.geojson</c> or <c>.json</c>), read without being written.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outcome.</returns>
    public static Task<AirportMapOpenOutcome> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => ReadFileAsync(path, path, cancellationToken), cancellationToken);
    }

    /// <summary>Opens the one combined <c>.geojson</c> a folder holds; a folder with none or several opens nothing.</summary>
    /// <param name="folder">The folder's path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outcome.</returns>
    public static Task<AirportMapOpenOutcome> OpenFolderAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Task.Run(
            async () =>
            {
                string[] maps;
                try
                {
                    maps = Directory.GetFiles(folder, "*.geojson", SearchOption.TopDirectoryOnly);
                }
                catch (DirectoryNotFoundException)
                {
                    return new AirportMapOpenOutcome.FolderMissing(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new AirportMapOpenOutcome.Unreadable(folder, ex.Message);
                }

                return maps.Length switch
                {
                    0 => new AirportMapOpenOutcome.FolderNoMap(folder),
                    1 => await ReadFileAsync(maps[0], maps[0], cancellationToken).ConfigureAwait(false),
                    _ => new AirportMapOpenOutcome.FolderSeveral(
                        folder,
                        [.. maps.Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
                    ),
                };
            },
            cancellationToken
        );
    }

    private async Task<AirportMapOpenOutcome> FetchAsync(string faaCode, CancellationToken cancellationToken)
    {
        string cachePath = _downloader.GetCachePath(faaCode);
        string label = $"{faaCode} (vNAS cache)";
        HttpCacheResult result;
        try
        {
            result = await _downloader.FetchGeoJsonAsync(faaCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AirportMapOpenOutcome.Unreadable(cachePath, ex.Message);
        }

        if (result.NotFound)
        {
            // A fresh 404 serves the disk copy; one inside the downloader's 404 memory serves nothing, so read it here.
            if (result.Content is not null)
            {
                return Parse(result.Content, cachePath, new OpenedAs(label, CachedCopyReason.NoLongerListed, faaCode));
            }

            return File.Exists(cachePath)
                ? await ReadFileAsync(cachePath, new OpenedAs(label, CachedCopyReason.NoLongerListed, faaCode), cancellationToken)
                    .ConfigureAwait(false)
                : new AirportMapOpenOutcome.NotFound(faaCode);
        }

        if (result.Content is null)
        {
            return new AirportMapOpenOutcome.Unreachable(faaCode);
        }

        CachedCopyReason reason = result.RefreshFailed ? CachedCopyReason.Offline : CachedCopyReason.None;
        return Parse(result.Content, cachePath, new OpenedAs(label, reason, faaCode));
    }

    private static Task<AirportMapOpenOutcome> ReadFileAsync(string path, string label, CancellationToken cancellationToken) =>
        ReadFileAsync(path, new OpenedAs(label, CachedCopyReason.None, FaaCode: null), cancellationToken);

    private static async Task<AirportMapOpenOutcome> ReadFileAsync(string path, OpenedAs openedAs, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AirportMapOpenOutcome.FileMissing(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AirportMapOpenOutcome.Unreadable(path, ex.Message);
        }

        return Parse(text, path, openedAs);
    }

    private static AirportMapOpenOutcome Parse(string text, string path, OpenedAs openedAs)
    {
        // A key repeated in a JSON object surfaces as an ArgumentException when the object is first read: the root, a
        // feature and a geometry inside the reader, a feature's properties only when ReadEveryFeaturesProperties reads them.
        try
        {
            var document = AirportMapDocument.Read(text);
            ReadEveryFeaturesProperties(document);
            return new AirportMapOpenOutcome.Opened(document, openedAs.Label, openedAs.Reason, openedAs.FaaCode);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            string message = ex is JsonException ? ex.Message : "the map repeats a key: " + ex.Message;
            return new AirportMapOpenOutcome.Unreadable(path, message);
        }
    }

    // The reader keeps a feature's properties unparsed; reading Count builds each one's dictionary, which throws on a
    // repeated key, so such a map is refused at open, not when the editor shows the feature.
    private static void ReadEveryFeaturesProperties(AirportMapDocument document)
    {
        foreach (MapFeature feature in document.Features)
        {
            if (feature.CloneProperties() is { } properties)
            {
                _ = properties.Count;
            }
        }
    }

    // How a map that reads is labelled once open.
    private sealed record OpenedAs(string Label, CachedCopyReason Reason, string? FaaCode);
}
