using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E pushback-to-spot tests using the S1-SFO-P scenario and SFO GeoJSON layout.
/// Loads a real scenario, sends PUSH @spot commands, and verifies aircraft push
/// backwards to the named parking spot via SimulationEngine ticking.
/// </summary>
public class SfoPushbackTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private const string ScenarioPath = "TestData/sfo-gc-scenario.json";

    private static string? LoadScenarioJson()
    {
        return File.Exists(ScenarioPath) ? File.ReadAllText(ScenarioPath) : null;
    }

    private static SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// SWA1360 spawns at B12 (delay=10s, no presets). Send PUSH @B13 and verify
    /// the aircraft pushes backwards to B13 and transitions to AtParkingPhase.
    /// B12 → B13 is ~150ft, should complete in under 60 seconds at pushback speed.
    /// </summary>
    [Fact]
    public void Sfo_PushToSpot_SWA1360_B12_ToB13_CompletesToAtParking()
    {
        var scenarioJson = LoadScenarioJson();
        var engine = BuildEngine();
        if (scenarioJson is null || engine is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();

        var warnings = engine.LoadScenario(scenarioJson, rngSeed: 42);
        foreach (var w in warnings)
        {
            _output.WriteLine($"[WARN] {w}");
        }

        // Tick to t=12s so SWA1360 (delay=10s) has spawned
        for (int t = 0; t < 12; t++)
        {
            engine.TickOneSecond();
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);
        _output.WriteLine(
            $"SWA1360 spawned: phase={ac.Phases?.CurrentPhase?.Name ?? "null"} pos=({ac.Latitude:F6},{ac.Longitude:F6}) hdg={ac.TrueHeading.Degrees:F0}"
        );

        // Get B13 parking position for distance checks
        var layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        var b13 = layout.FindSpotByName("B13");
        Assert.NotNull(b13);
        _output.WriteLine($"B13 target: ({b13.Latitude:F6},{b13.Longitude:F6}) hdg={b13.TrueHeading?.Degrees}");

        double startDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
        _output.WriteLine($"Start distance to B13: {startDist:F0}ft");

        // Send PUSH @B13
        var result = engine.SendCommand("SWA1360", "PUSH @B13");
        Assert.True(result.Success, $"PUSH @B13 failed: {result.Message}");
        _output.WriteLine($"Command result: {result.Message}");
        _output.WriteLine($"Phase after command: {ac.Phases?.CurrentPhase?.Name ?? "null"}");
        Assert.IsType<PushbackToSpotPhase>(ac.Phases?.CurrentPhase);

        // Tick and trace until completion or timeout
        _output.WriteLine("");
        _output.WriteLine($"{"t(s)", 5}  {"gs(kts)", 8}  {"dist(ft)", 9}  {"pushHdg", 8}  {"noseHdg", 8}  {"phase", -24}");

        bool reachedParking = false;
        int maxTicks = 120;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            engine.TickOneSecond();

            double distFt = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
            string phase = ac.Phases?.CurrentPhase?.Name ?? "complete";
            double pushHdg = ac.PushbackTrueHeading?.Degrees ?? -1;
            _output.WriteLine(
                $"{tick + 13, 5}  {ac.GroundSpeed, 8:F2}  {distFt, 9:F1}  {pushHdg, 8:F0}  {ac.TrueHeading.Degrees, 8:F0}  {phase, -24}"
            );

            if (ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                reachedParking = true;
                _output.WriteLine($"\nReached AtParkingPhase at t={tick + 13}s");
                break;
            }

            if (ac.Phases?.CurrentPhase is null)
            {
                _output.WriteLine($"\nPhase became null at t={tick + 13}s (unexpected)");
                break;
            }
        }

        Assert.True(reachedParking, $"Pushback should complete to AtParkingPhase within {maxTicks}s, got: {ac.Phases?.CurrentPhase?.Name ?? "null"}");

        // Aircraft should end up near B13
        double finalDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
        _output.WriteLine($"Final distance to B13: {finalDist:F0}ft");
        Assert.True(finalDist < 200, $"Aircraft should be near B13 after pushback: dist={finalDist:F0}ft");
    }

    /// <summary>
    /// PUSH @B13 180 — push to B13 with explicit heading 180. Verify aircraft
    /// ends up near B13 facing heading ~180.
    /// </summary>
    [Fact]
    public void Sfo_PushToSpot_SWA1360_B12_ToB13_Heading180()
    {
        var scenarioJson = LoadScenarioJson();
        var engine = BuildEngine();
        if (scenarioJson is null || engine is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();

        engine.LoadScenario(scenarioJson, rngSeed: 42);

        // Tick to t=12s so SWA1360 spawns
        for (int t = 0; t < 12; t++)
        {
            engine.TickOneSecond();
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        var layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        var b13 = layout.FindSpotByName("B13");
        Assert.NotNull(b13);

        // Send PUSH @B13 180
        var result = engine.SendCommand("SWA1360", "PUSH @B13 180");
        Assert.True(result.Success, $"PUSH @B13 180 failed: {result.Message}");
        _output.WriteLine($"Command result: {result.Message}");

        // Tick until completion
        _output.WriteLine($"{"t(s)", 5}  {"gs(kts)", 8}  {"dist(ft)", 9}  {"noseHdg", 8}  {"phase", -24}");

        bool reachedParking = false;
        for (int tick = 0; tick < 120; tick++)
        {
            engine.TickOneSecond();

            double distFt = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
            string phase = ac.Phases?.CurrentPhase?.Name ?? "complete";
            _output.WriteLine($"{tick + 13, 5}  {ac.GroundSpeed, 8:F2}  {distFt, 9:F1}  {ac.TrueHeading.Degrees, 8:F0}  {phase, -24}");

            if (ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                reachedParking = true;
                break;
            }

            if (ac.Phases?.CurrentPhase is null)
            {
                break;
            }
        }

        Assert.True(reachedParking, $"Pushback should complete to AtParkingPhase, got: {ac.Phases?.CurrentPhase?.Name ?? "null"}");

        // Verify final heading is near 180
        double hdgDiff = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - 180.0));
        _output.WriteLine($"Final heading: {ac.TrueHeading.Degrees:F0} (diff from 180: {hdgDiff:F1})");
        Assert.True(hdgDiff < 5.0, $"Aircraft should face ~180 after pushback, got {ac.TrueHeading.Degrees:F0} (diff={hdgDiff:F1})");

        // Verify near B13
        double finalDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
        _output.WriteLine($"Final distance to B13: {finalDist:F0}ft");
        Assert.True(finalDist < 200, $"Aircraft should be near B13: dist={finalDist:F0}ft");
    }

    // NormalizeAngle is private in FlightPhysics — inline equivalent.
    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0)
        {
            angle -= 360.0;
        }

        if (angle < -180.0)
        {
            angle += 360.0;
        }

        return angle;
    }

    /// <summary>
    /// Diagnostic trace: detailed per-second output of PUSH @B13 from B12.
    /// Shows route segments, position, speed, heading at each tick.
    /// </summary>
    [Fact]
    public void Diag_Sfo_PushToSpot_B12_ToB13_Trace()
    {
        var scenarioJson = LoadScenarioJson();
        var engine = BuildEngine();
        if (scenarioJson is null || engine is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();

        engine.LoadScenario(scenarioJson, rngSeed: 42);

        for (int t = 0; t < 12; t++)
        {
            engine.TickOneSecond();
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        var layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        var b12 = layout.FindSpotByName("B12");
        var b13 = layout.FindSpotByName("B13");
        Assert.NotNull(b12);
        Assert.NotNull(b13);

        _output.WriteLine($"B12: ({b12.Latitude:F6},{b12.Longitude:F6}) hdg={b12.TrueHeading?.Degrees}");
        _output.WriteLine($"B13: ({b13.Latitude:F6},{b13.Longitude:F6}) hdg={b13.TrueHeading?.Degrees}");
        _output.WriteLine(
            $"Distance B12→B13: {GeoMath.DistanceNm(b12.Latitude, b12.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm:F0}ft"
        );
        _output.WriteLine($"Bearing B12→B13: {GeoMath.BearingTo(b12.Latitude, b12.Longitude, b13.Latitude, b13.Longitude):F0}°");
        _output.WriteLine("");

        _output.WriteLine(
            $"Aircraft before command: pos=({ac.Latitude:F6},{ac.Longitude:F6}) hdg={ac.TrueHeading.Degrees:F0} gs={ac.GroundSpeed:F1} phase={ac.Phases?.CurrentPhase?.Name ?? "null"}"
        );

        var result = engine.SendCommand("SWA1360", "PUSH @B13");
        Assert.True(result.Success, $"PUSH @B13 failed: {result.Message}");
        _output.WriteLine($"Command: PUSH @B13 → {result.Message}");

        // Log route info
        var route = ac.AssignedTaxiRoute;
        if (route is not null)
        {
            _output.WriteLine($"Route: {route.Segments.Count} segments");
            for (int i = 0; i < route.Segments.Count; i++)
            {
                var seg = route.Segments[i];
                var fromNode = layout.Nodes.GetValueOrDefault(seg.FromNodeId);
                var toNode = layout.Nodes.GetValueOrDefault(seg.ToNodeId);
                double segDist =
                    fromNode is not null && toNode is not null
                        ? GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude) * GeoMath.FeetPerNm
                        : 0;
                _output.WriteLine(
                    $"  [{i}] {seg.TaxiwayName}: ({fromNode?.Latitude:F6},{fromNode?.Longitude:F6}) -> ({toNode?.Latitude:F6},{toNode?.Longitude:F6}) dist={segDist:F0}ft"
                );
            }
        }

        _output.WriteLine("");
        _output.WriteLine(
            $"{"t(s)", 5}  {"lat", 12}  {"lon", 13}  {"gs(kts)", 8}  {"ias", 5}  {"pushHdg", 8}  {"noseHdg", 8}  {"distB13(ft)", 12}  {"segIdx", 6}  {"phase", -24}"
        );

        for (int tick = 0; tick < 120; tick++)
        {
            engine.TickOneSecond();

            double distFt = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, b13.Latitude, b13.Longitude) * GeoMath.FeetPerNm;
            string phase = ac.Phases?.CurrentPhase?.Name ?? "complete";
            double pushHdg = ac.PushbackTrueHeading?.Degrees ?? -1;
            int segIdx = ac.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1;

            _output.WriteLine(
                $"{tick + 13, 5}  {ac.Latitude, 12:F6}  {ac.Longitude, 13:F6}  {ac.GroundSpeed, 8:F2}  {ac.IndicatedAirspeed, 5:F1}  {pushHdg, 8:F0}  {ac.TrueHeading.Degrees, 8:F0}  {distFt, 12:F1}  {segIdx, 6}  {phase, -24}"
            );

            if (ac.Phases?.CurrentPhase is AtParkingPhase or null)
            {
                break;
            }
        }
    }
}
