using System.Text.Json;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E replay tests that load oak-taxi-recording.json and replay it through SimulationEngine.
/// Silently skip if NavData.dat or oak.geojson is not present in TestData/.
/// </summary>
public class SimulationEngineReplayTests
{
    private const string RecordingPath = "TestData/oak-taxi-recording.json";

    // NKS2904 starts at parking 11: lat ~37.7107, lon ~-122.2171
    private const double Parking11Lat = 37.71073671539057;
    private const double Parking11Lon = -122.21706518663802;

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static SimulationEngine? BuildEngine()
    {
        var fixes = TestVnasData.FixDatabase;
        if (fixes is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        return new SimulationEngine(fixes, fixes, groundData, null, null);
    }

    [Fact]
    public void Replay_OakTaxi_NKS2904_HasTaxiRoute()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        var nks = engine.FindAircraft("NKS2904");
        Assert.NotNull(nks);
        Assert.NotNull(nks.AssignedTaxiRoute);
    }

    [Fact]
    public void Replay_OakTaxi_NKS2904_TaxiRouteFollowsExpectedPath()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        var nks = engine.FindAircraft("NKS2904");
        Assert.NotNull(nks);
        Assert.NotNull(nks.AssignedTaxiRoute);

        // Recording issues "TAXI S T U W W1 HS 30" — route should include all these taxiways
        var taxiways = nks.AssignedTaxiRoute!.Segments.Select(s => s.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("S", taxiways);
        Assert.Contains("T", taxiways);
        Assert.Contains("U", taxiways);
        Assert.Contains("W", taxiways);
        Assert.Contains("W1", taxiways);
    }

    [Fact]
    public void Replay_OakTaxi_NKS2904_MovedFromParking()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, recording.TotalElapsedSeconds);

        var nks = engine.FindAircraft("NKS2904");
        Assert.NotNull(nks);

        // After 96 seconds of taxiing, aircraft should have moved from parking 11
        double distNm = Yaat.Sim.GeoMath.DistanceNm(nks.Latitude, nks.Longitude, Parking11Lat, Parking11Lon);
        Assert.True(distNm > 0.01, $"Expected NKS2904 to have moved from parking 11, but distance is only {distNm:F4} nm");
    }
}
