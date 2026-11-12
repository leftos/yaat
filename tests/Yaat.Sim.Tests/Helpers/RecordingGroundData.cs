using System.Collections.Concurrent;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// The ground data a recording test builds its engine on. Every call site states which of two things the test pins, and why.
///
/// <see cref="Evergreen"/>: behaviour that must keep holding on today's airport (a regression test for a sim, pathfinder or
/// parser fix). It serves the committed <c>TestData/&lt;id&gt;.geojson</c> layouts through <see cref="TestAirportGroundData"/>,
/// and the test is maintained as those layouts change.
///
/// <see cref="VersionPinned"/>: behaviour that only makes sense on the airport as recorded (a restored mid-taxi route,
/// recorded node ids). It serves the layouts the archive bundles (<c>layouts/{id}.json.br</c>) as they were recorded, so a
/// restored snapshot's taxi route resolves by node id on the graph it was planned on, even for an archive without a
/// <c>LayoutFormatVersion</c>, where the server pins today's layout instead. An airport the archive does not bundle comes
/// from the committed layouts.
///
/// Each archive is decoded once per test process; the layouts are immutable, so parallel test classes share them. Only a
/// direct recording archive is read; a bug-report bundle wrapping a nested recording is not supported.
/// </summary>
internal sealed class RecordingGroundData : IAirportGroundData
{
    private static readonly ConcurrentDictionary<string, Lazy<BundledAirports>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly BundledAirports _bundled;
    private readonly TestAirportGroundData _live = new();

    private RecordingGroundData(BundledAirports bundled) => _bundled = bundled;

    /// <summary>Ground data for a test that pins behaviour which must keep holding on today's airport layouts.</summary>
    /// <param name="why">One line naming what the test pins.</param>
    /// <exception cref="ArgumentException"><paramref name="why"/> is empty or whitespace.</exception>
    public static RecordingGroundData Evergreen(string why)
    {
        RequireReason(why);
        return new RecordingGroundData(BundledAirports.None);
    }

    /// <summary>
    /// Ground data for a test that pins behaviour on the airport as recorded: the layouts the archive at
    /// <paramref name="archivePath"/> bundles, falling back per airport to the committed test layouts.
    /// </summary>
    /// <param name="archivePath">The recording archive whose bundled layouts to serve.</param>
    /// <param name="why">One line naming what the test pins.</param>
    /// <exception cref="ArgumentException"><paramref name="why"/> is empty or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="InvalidOperationException">The archive bundles no airport layout.</exception>
    public static RecordingGroundData VersionPinned(string archivePath, string why)
    {
        RequireReason(why);
        string fullPath = Path.GetFullPath(archivePath);
        Lazy<BundledAirports> entry = Cache.GetOrAdd(fullPath, static path => new Lazy<BundledAirports>(() => BundledAirports.Read(path)));
        try
        {
            return new RecordingGroundData(entry.Value);
        }
        catch
        {
            // Lazy caches the factory's exception; drop the entry so a later call reads the archive again.
            Cache.TryRemove(new KeyValuePair<string, Lazy<BundledAirports>>(fullPath, entry));
            throw;
        }
    }

    /// <summary>Whether the archive bundles <paramref name="airportId"/>'s layout (either spelling of the ID).</summary>
    public bool Bundles(string airportId) => _bundled.Layouts.ContainsKey(AirportLayoutDownloader.ToFaaCode(airportId));

    public AirportGroundLayout? GetLayout(string airportId) =>
        _bundled.Layouts.TryGetValue(AirportLayoutDownloader.ToFaaCode(airportId), out AirportGroundLayout? layout)
            ? layout
            : _live.GetLayout(airportId);

    /// <summary>
    /// The source GeoJSON for <paramref name="airportId"/>: the archive's own for a bundled airport (null when the archive
    /// carries its layout but not its GeoJSON, never today's file beside a recorded graph), else the committed file.
    /// </summary>
    public string? GetSourceGeoJson(string airportId)
    {
        string faaCode = AirportLayoutDownloader.ToFaaCode(airportId);
        if (_bundled.Layouts.ContainsKey(faaCode))
        {
            return _bundled.GeoJsons.GetValueOrDefault(faaCode);
        }

        return _live.GetSourceGeoJson(airportId);
    }

    /// <summary>The bundled or committed layout and its GeoJSON, always loaded.</summary>
    public GroundDataRead Read(string airportId) => new(true, GetLayout(airportId), GetSourceGeoJson(airportId));

    private static void RequireReason(string why)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException("State in one line what the test pins on this layout.", nameof(why));
        }
    }

    private sealed record BundledAirports(IReadOnlyDictionary<string, AirportGroundLayout> Layouts, IReadOnlyDictionary<string, string> GeoJsons)
    {
        public static readonly BundledAirports None = new(new Dictionary<string, AirportGroundLayout>(), new Dictionary<string, string>());

        public static BundledAirports Read(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException(
                    "Recording archive not found; RecordingGroundData.VersionPinned needs an existing archive.",
                    archivePath
                );
            }

            using var archive = RecordingArchive.Open(archivePath);
            var layouts = new Dictionary<string, AirportGroundLayout>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, AirportGroundLayout layout) in archive.ReadAllLayouts())
            {
                layouts[AirportLayoutDownloader.ToFaaCode(id)] = layout;
            }

            if (layouts.Count == 0)
            {
                throw new InvalidOperationException($"{archivePath} bundles no airport layout; use RecordingGroundData.Evergreen");
            }

            var geoJsons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, string geoJson) in archive.ReadAllAirportGeoJsons())
            {
                geoJsons[AirportLayoutDownloader.ToFaaCode(id)] = geoJson;
            }

            return new BundledAirports(layouts, geoJsons);
        }
    }
}
