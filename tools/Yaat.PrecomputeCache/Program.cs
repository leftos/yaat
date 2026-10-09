using Microsoft.Extensions.Logging;
using Yaat.PrecomputeCache;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.AddSimpleConsole(options => options.SingleLine = true);
    builder.SetMinimumLevel(LogLevel.Warning);
});
SimLog.Initialize(loggerFactory);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("yaat-precompute-cache/1.0");

var services = new PrecomputeCacheServices
{
    Http = http,
    GeoJsonCacheDir = YaatPaths.Combine("cache", "airports"),
    ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs"),
    WorkingDirectory = Directory.GetCurrentDirectory(),
    InitializeNavigationAsync = InitializeNavigationAsync,
    LoadFaaRecordsAsync = LoadFaaRecordsAsync,
    Out = Console.Out,
    Error = Console.Error,
};
return await PrecomputeCacheCli.RunAsync(args, services);

// The vNAS navdata, downloaded when the cached copy is behind, and its serial; a cached copy's when the download failed,
// which the tool compares with the live serial.
static (string Path, long Serial) ResolveNavData()
{
    string path =
        NavDataPathResolver.EnsureCurrent(new NavDataResolveOptions(AllowDownload: true))
        ?? throw new InvalidOperationException("No vNAS NavData.dat could be downloaded or found in the cache");
    long serial =
        NavDataPathResolver.ResolvedNavDataSerial
        ?? throw new InvalidOperationException(
            $"The serial of {path} is unknown, so no entry can be keyed on it; delete it and rerun to download it"
        );
    return (path, serial);
}

// Loads the navigation database as the server does (navdata, then the current CIFP cycle), for the layout parse.
static async Task<long> InitializeNavigationAsync()
{
    (string path, long serial) = ResolveNavData();
    using var cifp = new CifpDataService();
    await cifp.InitializeAsync(CifpDataService.CreateDefaultOptions());
    if (cifp.CifpFilePath is null)
    {
        throw new InvalidOperationException("No CIFP could be downloaded or found in the cache; an airport layout is parsed against it");
    }

    NavDataSet navData = NavDataSet.Parser.ParseFrom(await File.ReadAllBytesAsync(path));
    NavigationDatabase.Initialize(navData, cifp.CifpFilePath, supplementaryCifpFilePaths: cifp.SupplementaryCifpFilePaths);
    return serial;
}

// The current FAA ACD (downloaded once per AIRAC cycle, else the last cycle's cache), with no override applied.
static async Task<IReadOnlyDictionary<string, FaaAircraftRecord>> LoadFaaRecordsAsync()
{
    using var service = new FaaAircraftDataService();
    await service.InitializeAsync();
    return new Dictionary<string, FaaAircraftRecord>(FaaAircraftDatabase.Records, StringComparer.Ordinal);
}
