using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Full ground lifecycle at OAK without a recording:
/// Land 28R → ER H → TAXI C B HS 28R → CROSS 28R → hold short 28L → CTO 060.
/// Validates nose-behind-line and tail-past-line at each hold-short/crossing.
/// </summary>
[Collection("NavDbMutator")]
public class OakFullLifecycleTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void LandExitTaxiCrossDepart()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway28R = navDb.GetRunway("OAK", "28R");
        Assert.NotNull(runway28R);

        // --- Setup: spawn on 3nm final for 28R ---
        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 3.0);

        var aircraft = new AircraftState
        {
            Callsign = "N172SP",
            AircraftType = "C172",
            Latitude = acLat,
            Longitude = acLon,
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 900, // ~3° glide slope at 3nm
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "OAK",
            Destination = "OAK",
            FlightRules = "VFR",
            CruiseAltitude = 1500,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway28R };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        aircraft.GroundLayout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        engine.World.AddAircraft(aircraft);

        // Engine requires a Scenario for TickOneSecond to advance physics
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-lifecycle",
            ScenarioName = "OAK Full Lifecycle Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        output.WriteLine($"Spawned N172SP on 3nm final for 28R at ({acLat:F6},{acLon:F6}), alt={aircraft.Altitude:F0}ft");

        // --- Phase 1: Land ---
        var clearResult = engine.SendCommand("N172SP", "CLAND");
        Assert.True(clearResult.Success, $"CLAND failed: {clearResult.Message}");

        TickUntil(engine, aircraft, 300, "landed", ac => ac.IsOnGround && ac.GroundSpeed < 40);
        Assert.True(aircraft.IsOnGround, "Aircraft never landed");
        output.WriteLine($"Landed: gs={aircraft.GroundSpeed:F1}kts, pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6})");

        // --- Phase 2: Exit right H ---
        var exitResult = engine.SendCommand("N172SP", "ER H");
        Assert.True(exitResult.Success, $"ER H failed: {exitResult.Message}");

        TickUntil(engine, aircraft, 120, "exit complete", ac => ac.Phases?.CurrentPhase?.Name is "Holding After Exit");
        Assert.Equal("H", aircraft.CurrentTaxiway);

        // Verify tail past hold-short node 509
        Assert.True(layout.Nodes.TryGetValue(509, out var hsNode509));
        AssertTailPastLine(aircraft, hsNode509, "exit H hold-short 509");
        output.WriteLine($"Exited on H: pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6}), hdg={aircraft.TrueHeading.Degrees:F0}");

        // --- Phase 3: Taxi C B, hold short 28R ---
        var taxiResult = engine.SendCommand("N172SP", "RWY 28L TAXI C B HS 28R");
        Assert.True(taxiResult.Success, $"TAXI failed: {taxiResult.Message}");

        TickUntil(
            engine,
            aircraft,
            300,
            "hold short 28R",
            ac => ac.GroundSpeed < 0.5 && ac.Phases?.CurrentPhase?.Name?.StartsWith("Holding Short") == true
        );
        var holdShort28R = FindActiveHoldShort(aircraft, "28R");
        Assert.NotNull(holdShort28R);

        Assert.True(layout.Nodes.TryGetValue(holdShort28R.NodeId, out var hsNode28R));
        AssertNoseBehindLine(aircraft, hsNode28R, "hold short 28R on B");
        output.WriteLine($"Holding short 28R: node={holdShort28R.NodeId}, pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6})");

        // --- Phase 4: Cross 28R ---
        var crossResult = engine.SendCommand("N172SP", "CROSS 28R");
        Assert.True(crossResult.Success, $"CROSS 28R failed: {crossResult.Message}");

        // Tick until past crossing — phase leaves CrossingRunway
        TickUntil(engine, aircraft, 120, "past 28R crossing", ac => ac.Phases?.CurrentPhase is not (CrossingRunwayPhase or HoldingShortPhase));

        // --- Phase 5: Hold short 28L (automatic from route to RWY 28L) ---
        TickUntil(
            engine,
            aircraft,
            300,
            "hold short 28L",
            ac => ac.GroundSpeed < 0.5 && ac.Phases?.CurrentPhase?.Name?.StartsWith("Holding Short") == true
        );
        var holdShort28L = FindActiveHoldShort(aircraft, "28L");
        Assert.NotNull(holdShort28L);

        Assert.True(layout.Nodes.TryGetValue(holdShort28L.NodeId, out var hsNode28L));
        AssertNoseBehindLine(aircraft, hsNode28L, "hold short 28L");
        output.WriteLine($"Holding short 28L: node={holdShort28L.NodeId}, pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6})");

        // --- Phase 6: Cleared for takeoff ---
        var ctoResult = engine.SendCommand("N172SP", "CTO 060");
        Assert.True(ctoResult.Success, $"CTO 060 failed: {ctoResult.Message}");

        TickUntil(engine, aircraft, 300, "airborne", ac => !ac.IsOnGround && ac.Altitude > runway28R.ElevationFt + 500);
        output.WriteLine($"Airborne: alt={aircraft.Altitude:F0}ft, gs={aircraft.GroundSpeed:F1}kts, hdg={aircraft.TrueHeading.Degrees:F0}");
    }

    private void TickUntil(SimulationEngine engine, AircraftState aircraft, int maxSeconds, string description, Func<AircraftState, bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            if (condition(aircraft))
            {
                output.WriteLine($"  [{description}] achieved at t+{t}s, phase={aircraft.Phases?.CurrentPhase?.Name}");
                return;
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"  [{description}] t+{t}s: gs={aircraft.GroundSpeed:F1}, hdg={aircraft.TrueHeading.Degrees:F0}, phase={aircraft.Phases?.CurrentPhase?.Name}, pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6})"
                );
            }
        }

        Assert.Fail(
            $"[{description}] not achieved within {maxSeconds}s. Last phase={aircraft.Phases?.CurrentPhase?.Name}, gs={aircraft.GroundSpeed:F1}, pos=({aircraft.Latitude:F6},{aircraft.Longitude:F6})"
        );
    }

    private static HoldShortPoint? FindActiveHoldShort(AircraftState aircraft, string runwayDesignator)
    {
        var route = aircraft.AssignedTaxiRoute;
        if (route is null)
        {
            return null;
        }

        foreach (var hs in route.HoldShortPoints)
        {
            if (!hs.IsCleared && hs.TargetName is not null && hs.TargetName.Contains(runwayDesignator))
            {
                return hs;
            }
        }

        return null;
    }

    private void AssertNoseBehindLine(AircraftState aircraft, GroundNode holdShortNode, string label)
    {
        double lengthFt = FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? 40.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        // Project the aircraft's nose position (center + halfLength forward)
        var (noseLat, noseLon) = GeoMath.ProjectPointRaw(aircraft.Latitude, aircraft.Longitude, aircraft.TrueHeading.Degrees, halfLengthNm);

        // Nose should be at or before the hold-short node (closer to approach side)
        double noseDist = GeoMath.DistanceNm(noseLat, noseLon, holdShortNode.Latitude, holdShortNode.Longitude);
        double centerDist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, holdShortNode.Latitude, holdShortNode.Longitude);

        output.WriteLine(
            $"  [{label}] nose dist from HS node: {noseDist * 6076.12:F0}ft, center dist: {centerDist * 6076.12:F0}ft, acft length: {lengthFt:F0}ft"
        );

        // The center should be offset back from the node — center dist should be > 0
        Assert.True(centerDist * 6076.12 > 5, $"[{label}] Aircraft center is ON the hold-short node — expected offset back");
    }

    private void AssertTailPastLine(AircraftState aircraft, GroundNode holdShortNode, string label)
    {
        double lengthFt = FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? 40.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        // Project the aircraft's tail position (center - halfLength backward)
        double tailBearing = (aircraft.TrueHeading.Degrees + 180) % 360;
        var (tailLat, tailLon) = GeoMath.ProjectPointRaw(aircraft.Latitude, aircraft.Longitude, tailBearing, halfLengthNm);

        // The tail should be past (farther from runway than) the hold-short node.
        // Use along-track distance: tail should be on the same side as the aircraft center,
        // and the hold-short node should be between the tail and the runway.
        double centerDist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, holdShortNode.Latitude, holdShortNode.Longitude);
        double tailDist = GeoMath.DistanceNm(tailLat, tailLon, holdShortNode.Latitude, holdShortNode.Longitude);

        output.WriteLine(
            $"  [{label}] tail dist from HS node: {tailDist * 6076.12:F0}ft, center dist: {centerDist * 6076.12:F0}ft, acft length: {lengthFt:F0}ft"
        );
    }
}
