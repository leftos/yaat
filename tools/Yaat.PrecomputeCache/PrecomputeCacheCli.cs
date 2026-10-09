using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;

namespace Yaat.PrecomputeCache;

/// <summary>
/// The maintainer tool for the committed precompute cache: computes airports' entries from their current vNAS maps,
/// checks the committed entries against the build-time source hashes, and refreshes the envelopes and the airport list.
/// The work itself is <see cref="PrecomputeEntryBuild"/>'s and <see cref="PrecomputeBuilder"/>'s; this class reads the
/// arguments, fetches, and picks the exit code.
/// </summary>
public static class PrecomputeCacheCli
{
    /// <summary>The run did what it was asked; a check found no unreadable entry.</summary>
    public const int Ok = 0;

    /// <summary>A fetch, a compute or a refresh failed, or a checked entry is unreadable.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong.</summary>
    public const int UsageError = 2;

    /// <summary>Runs the tool.</summary>
    /// <param name="args">The command line after <c>--</c>.</param>
    /// <param name="services">The network, navdata and output the run uses.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, PrecomputeCacheServices services)
    {
        services = services with { Out = TextWriter.Synchronized(services.Out), Error = TextWriter.Synchronized(services.Error) };
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            await services.Error.WriteLineAsync(ex.Message);
            await services.Error.WriteLineAsync(CliOptions.Usage);
            return UsageError;
        }

        try
        {
            return options.Mode switch
            {
                CliMode.Compute => await ComputeAsync(options, services),
                CliMode.Check => await CheckAsync(options, services),
                CliMode.RefreshEnvelopes => await RefreshEnvelopesAsync(options, services),
                CliMode.RefreshAirports => await RefreshAirportsCommand.RunAsync(RootOf(options, services), services),
                _ => throw new UnreachableException($"Unknown mode {options.Mode}"),
            };
        }
        catch (ToolFailureException ex)
        {
            await services.Error.WriteLineAsync(ex.InnerException is { } inner ? $"{ex.Message}: {inner}" : ex.Message);
            return Failed;
        }
        catch (Exception ex)
        {
            await services.Error.WriteLineAsync($"The run failed: {ex}");
            return Failed;
        }
    }

    /// <summary>The cache folder: <c>--root</c> against the working directory, else the repo's.</summary>
    /// <param name="options">The options.</param>
    /// <param name="services">The services.</param>
    /// <returns>The full path.</returns>
    public static string RootOf(CliOptions options, PrecomputeCacheServices services) =>
        options.Root is { } root
            ? Path.GetFullPath(root, services.WorkingDirectory)
            : Path.Combine(RepoRootOf(services), "src", "Yaat.Sim", "Data", "PrecomputeCache");

    /// <summary>The nearest folder at or above the working directory holding <c>yaat.slnx</c>.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The repo root.</returns>
    /// <exception cref="ToolFailureException">No such folder.</exception>
    public static string RepoRootOf(PrecomputeCacheServices services)
    {
        for (DirectoryInfo? dir = new(services.WorkingDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new ToolFailureException($"No yaat.slnx at or above {services.WorkingDirectory}; run the tool from the yaat repo, or pass --root");
    }

    private static async Task<int> ComputeAsync(CliOptions options, PrecomputeCacheServices services)
    {
        string root = RootOf(options, services);
        IReadOnlyList<string> airports = AirportsOf(options, root);
        var limits = new PrecomputeRunLimits(options.Parallel, options.AirportsInFlight);
        using var downloader = new AirportLayoutDownloader(services.Http, services.GeoJsonCacheDir);
        var run = new ComputeRun(
            new PrecomputeStore(root),
            downloader,
            limits,
            new Lazy<Task<PrecomputeBuildContext>>(() => BuildContextAsync(limits.StandLoop, services)),
            options.Force
        );
        var failed = new ConcurrentBag<string>();
        await limits.ForEachAirportAsync(
            airports,
            async (airport, cancellationToken) =>
            {
                if (!await ComputeOneAsync(airport, run, services, cancellationToken))
                {
                    failed.Add(airport);
                }
            }
        );

        if (failed.IsEmpty)
        {
            return Ok;
        }

        await services.Error.WriteLineAsync(
            $"{failed.Count} of {airports.Count} airports failed: {string.Join(' ', failed.Order(StringComparer.Ordinal))}"
        );
        return Failed;
    }

    /// <summary>
    /// Fetches, keys and, unless current, computes one airport; false (reported) when it fails. A navdata failure is the
    /// whole run's, so it throws <see cref="ToolFailureException"/> instead, and a run cancelled by another airport's
    /// failure throws its cancellation.
    /// </summary>
    private static async Task<bool> ComputeOneAsync(
        string airport,
        ComputeRun run,
        PrecomputeCacheServices services,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (await FetchGeoJsonAsync(run.Downloader, airport, services, cancellationToken) is not { } geoJson)
            {
                return false;
            }

            PrecomputeBuildContext context = await run.Context.Value;
            bool isCurrent = await run.Limits.RunAsync(() =>
                (!run.Force)
                && IsCurrent(
                    run.Store,
                    airport,
                    PrecomputeEntryBuild.CurrentKey(airport, geoJson, services.ArtccsDir, context.NavDataSerial),
                    services
                )
            );
            if (isCurrent)
            {
                await services.Out.WriteLineAsync($"{airport}: entry is current; skipped (--force recomputes it)");
                return true;
            }

            var stopwatch = Stopwatch.StartNew();
            PrecomputeEntry entry = await run.Limits.RunAsync(() => PrecomputeEntryBuild.Build(run.Store, airport, geoJson, context, null));
            string path = run.Store.PathFor(airport);
            await services.Out.WriteLineAsync(
                $"{airport}: wrote {path} ({entry.PushTargets.Count} stand/group entries, {new FileInfo(path).Length:N0} bytes) "
                    + $"in {stopwatch.Elapsed.TotalSeconds:F1} s"
            );
            return true;
        }
        catch (Exception ex)
            when ((ex is not ToolFailureException) && !((ex is OperationCanceledException) && cancellationToken.IsCancellationRequested))
        {
            await services.Error.WriteLineAsync($"{airport}: the compute failed; nothing written for it: {ex}");
            return false;
        }
    }

    private static async Task<int> CheckAsync(CliOptions options, PrecomputeCacheServices services)
    {
        string root = RootOf(options, services);
        IReadOnlyList<string> airports = AirportsOf(options, root);
        PrecomputeOnlineFacts? online = options.Online ? await OnlineFactsAsync(airports, services) : null;
        IReadOnlyList<PrecomputeCheckResult> results = PrecomputeBuilder.Check(new PrecomputeStore(root), airports, services.ArtccsDir, online);
        foreach (PrecomputeCheckResult result in results)
        {
            await services.Out.WriteLineAsync(CheckLine(result));
        }

        int unreadable = results.Count(r => r.Status == PrecomputeCheckStatus.Unreadable);
        int stale = results.Count(r => r.Status is not (PrecomputeCheckStatus.Current or PrecomputeCheckStatus.Unreadable));
        string mode = online is null ? "offline" : "online";
        if ((unreadable == 0) && (stale == 0))
        {
            await services.Out.WriteLineAsync($"{results.Count} airports checked ({mode}): every entry is current");
            return Ok;
        }

        if (stale > 0)
        {
            await services.Out.WriteLineAsync(
                $"{stale} of {results.Count} airports checked ({mode}) have a missing or stale entry (a warning, not a failure); "
                    + "run `dotnet run -c Release --project tools/Yaat.PrecomputeCache` to recompute them"
            );
        }

        if (unreadable == 0)
        {
            return Ok;
        }

        await services.Error.WriteLineAsync(
            $"{unreadable} of {results.Count} airports checked ({mode}) have an unreadable entry; "
                + "run `dotnet run -c Release --project tools/Yaat.PrecomputeCache` to recompute them"
        );
        return Failed;
    }

    /// <summary>
    /// A check result as the tool prints it: a current entry plainly, an unreadable one as a GitHub <c>::error::</c> line,
    /// a missing or stale one as a <c>::warning::</c> line.
    /// </summary>
    private static string CheckLine(PrecomputeCheckResult result) =>
        result.Status switch
        {
            PrecomputeCheckStatus.Current => result.Describe(),
            PrecomputeCheckStatus.Unreadable => $"::error::{result.Describe()}",
            _ => $"::warning::{result.Describe()}",
        };

    private static async Task<int> RefreshEnvelopesAsync(CliOptions options, PrecomputeCacheServices services)
    {
        string envelopesPath = Path.Combine(RootOf(options, services), "design-group-envelopes.json");
        string pinnedPath = Path.Combine(RepoRootOf(services), "tests", "Yaat.Sim.Tests", "TestData", "FaaAcd.json");
        IReadOnlyDictionary<string, FaaAircraftRecord> records = await services.LoadFaaRecordsAsync();
        IReadOnlyList<string> warnings;
        try
        {
            PrecomputeBuilder.RefreshEnvelopes(records, envelopesPath, pinnedPath, out warnings);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolFailureException(ex.Message);
        }

        foreach (string warning in warnings)
        {
            await services.Error.WriteLineAsync($"warning: {warning}");
        }

        await services.Out.WriteLineAsync($"Wrote {envelopesPath} from {records.Count} FAA records, and the pinned test copy {pinnedPath}.");
        await services.Out.WriteLineAsync(
            "The envelopes are a push-target source: if the file changed, every entry's push half is stale until the tool recomputes it. "
                + "Other tests read the pinned copy too; run the test suite."
        );
        return Ok;
    }

    private static IReadOnlyList<string> AirportsOf(CliOptions options, string root)
    {
        if (options.Airports.Count > 0)
        {
            return options.Airports;
        }

        try
        {
            return PrecomputeAirportList.Read(Path.Combine(root, PrecomputeAirportList.FileName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            throw new ToolFailureException(ex.Message);
        }
    }

    /// <summary>The airport's current vNAS map, or null (reported) when vNAS has none or could not be reached.</summary>
    private static async Task<string?> FetchGeoJsonAsync(
        AirportLayoutDownloader downloader,
        string airport,
        PrecomputeCacheServices services,
        CancellationToken cancellationToken
    )
    {
        HttpCacheResult fetched = await downloader.FetchGeoJsonAsync(airport, cancellationToken);
        if (fetched.NotFound)
        {
            await services.Error.WriteLineAsync($"{airport}: vNAS has no ground map (HTTP 404); nothing written");
            return null;
        }

        if (ServedMap(fetched) is not { } geoJson)
        {
            await services.Error.WriteLineAsync(
                $"{airport}: the vNAS ground map could not be fetched; nothing written "
                    + "(an entry is computed from the map vNAS serves now, never a cached copy)"
            );
            return null;
        }

        return geoJson;
    }

    /// <summary>The map text vNAS served now; null when it answered 404 or could not be reached.</summary>
    /// <param name="fetched">The fetch.</param>
    /// <returns>The GeoJSON, or null.</returns>
    public static string? ServedMap(HttpCacheResult fetched) =>
        fetched is { NotFound: false, RefreshFailed: false, Content: { } content } ? content : null;

    /// <summary>Whether vNAS could not be reached for a map: neither served nor a 404.</summary>
    /// <param name="fetched">The fetch.</param>
    /// <returns>True when unreachable.</returns>
    public static bool MapUnreachable(HttpCacheResult fetched) => !fetched.NotFound && (ServedMap(fetched) is null);

    /// <summary>
    /// The live NavData serial, then the navdata loaded for the layout parse; the run stops when the loaded serial is not
    /// the live one, which means the download failed and only a cached copy was found.
    /// </summary>
    private static async Task<PrecomputeBuildContext> BuildContextAsync(ParallelOptions standLoop, PrecomputeCacheServices services)
    {
        long live = await LiveNavDataSerialAsync(services);
        try
        {
            long loaded = await services.InitializeNavigationAsync();
            if (loaded != live)
            {
                throw new ToolFailureException(
                    $"vNAS publishes NavData serial {live}, but only serial {loaded} could be loaded (a cached copy; the download failed); "
                        + "nothing is computed, since an entry is keyed on the serial vNAS publishes now"
                );
            }

            var sidecars = new AirportSidecarCatalog(AirportSidecarLoader.LoadAll(services.ArtccsDir).Airports);
            return new PrecomputeBuildContext(services.ArtccsDir, sidecars, DesignGroupEnvelopes.LoadShipped(), live, standLoop);
        }
        catch (Exception ex) when (ex is not ToolFailureException)
        {
            throw new ToolFailureException(
                "The navdata, the airport sidecars or the design-group envelopes could not be loaded; nothing is computed",
                ex
            );
        }
    }

    /// <summary>The NavData serial vNAS publishes now, read from <see cref="VnasConfig.Url"/>; never a cached one.</summary>
    private static async Task<long> LiveNavDataSerialAsync(PrecomputeCacheServices services)
    {
        string json;
        try
        {
            json = await services.Http.GetStringAsync(VnasConfig.Url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            throw new ToolFailureException(
                $"The live vNAS NavData serial could not be read from {VnasConfig.Url} ({ex.Message}); "
                    + "nothing is computed, since an entry is keyed on the serial vNAS publishes now, never a cached one"
            );
        }

        return LiveSerialOf(json);
    }

    private static long LiveSerialOf(string json)
    {
        VnasConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VnasConfig>(json);
        }
        catch (JsonException ex)
        {
            throw new ToolFailureException($"{VnasConfig.Url} is not readable JSON ({ex.Message}), so the live NavData serial is unknown");
        }

        return config is { NavDataSerial: > 0 } read
            ? read.NavDataSerial
            : throw new ToolFailureException($"{VnasConfig.Url} names no navDataSerial, so the live NavData serial is unknown");
    }

    /// <summary>Whether the airport's stored entry matches <paramref name="current"/> on every key field; an unreadable one does not.</summary>
    private static bool IsCurrent(PrecomputeStore store, string airport, PrecomputeKey current, PrecomputeCacheServices services)
    {
        try
        {
            return store.TryRead(airport, out PrecomputeEntry? stored) && stored.Key.PushTargetsMatch(current);
        }
        catch (InvalidDataException ex)
        {
            services.Error.WriteLine($"{airport}: {ex.Message}; recomputing it");
            return false;
        }
    }

    private static async Task<PrecomputeOnlineFacts> OnlineFactsAsync(IReadOnlyList<string> airports, PrecomputeCacheServices services)
    {
        using var downloader = new AirportLayoutDownloader(services.Http, services.GeoJsonCacheDir);
        var md5ByAirport = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string airport in airports)
        {
            HttpCacheResult fetched = await downloader.FetchGeoJsonAsync(airport, CancellationToken.None);
            if (MapUnreachable(fetched))
            {
                throw new ToolFailureException($"{airport}: the vNAS ground map could not be fetched, so --online cannot compare its GeoJSON MD5");
            }

            md5ByAirport[airport] = ServedMap(fetched) is { } geoJson ? GeoJsonMd5.Of(geoJson) : null;
        }

        return new PrecomputeOnlineFacts(await LiveNavDataSerialAsync(services), md5ByAirport);
    }

    /// <summary>What every airport of one compute run shares.</summary>
    private sealed record ComputeRun(
        PrecomputeStore Store,
        AirportLayoutDownloader Downloader,
        PrecomputeRunLimits Limits,
        Lazy<Task<PrecomputeBuildContext>> Context,
        bool Force
    );
}
