using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E coverage for issue #167 — a heading-only PUSH during a pushback re-plans the push on the new facing, while
/// the stand push-off (the straight half-fuselage push before any turn) is still running. Once the push-off has
/// finished the amendment is rejected. Facings are magnetic in the command and true on the aircraft.
///
/// <para>The targeted push is <c>PUSH Y FACE N</c> off gate B12: out onto taxiway Y, lined up northbound. A push to a
/// stand parks on the stand's own heading, so it takes no facing and is never amended.</para>
/// </summary>
public class Issue167AdjustPushbackFaceTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private const string ScenarioPath = "TestData/sfo-gc-scenario.json";

    /// <summary>A targeted push off B12 that a facing change may amend: onto taxiway Y, lined up northbound.</summary>
    private const string PushYankeeFacingNorth = "PUSH Y FACE N";

    private static string? LoadScenarioJson()
    {
        return File.Exists(ScenarioPath) ? File.ReadAllText(ScenarioPath) : null;
    }

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");

    private static SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
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

    private SimulationEngine? SpawnSwa1360()
    {
        var scenarioJson = LoadScenarioJson();
        var engine = BuildEngine();
        if (scenarioJson is null || engine is null)
        {
            return null;
        }

        TestVnasData.EnsureInitialized();
        engine.LoadScenario(scenarioJson, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);

        // Tick to t=12s so SWA1360 (delay=10s) has spawned at B12
        for (int t = 0; t < 12; t++)
        {
            engine.TickOneSecond();
        }
        return engine;
    }

    /// <summary>
    /// <c>PUSH Y FACE N</c> off B12 amended to <c>PUSH FACE S</c> during the push-off: the push lines up on taxiway Y
    /// facing Y's southbound direction — the edge direction nearest true-of-magnetic-south — instead of its
    /// northbound one, on the centreline, within the line capture's 1° and 1 ft.
    /// </summary>
    [Fact]
    public void HeadingOnlyPush_DuringActivePushback_UpdatesFaceDirection()
    {
        var engine = SpawnSwa1360();
        if ((engine is null) || (LoadSfo() is not { } layout))
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);
        var exit = layout.FindExitByTaxiway(ac.Position, "Y") ?? throw new InvalidOperationException("no taxiway Y exit near B12");
        double northbound = Assert.NotNull(layout.GetEdgeBearingForTaxiway(exit, "Y", MagneticDeclination.MagneticToTrue(360.0, ac.Position)));
        double southbound = Assert.NotNull(layout.GetEdgeBearingForTaxiway(exit, "Y", MagneticDeclination.MagneticToTrue(180.0, ac.Position)));
        _output.WriteLine($"taxiway Y at node {exit.Id}: northbound {northbound:F1}°, southbound {southbound:F1}° true");

        var result = engine.SendCommand("SWA1360", PushYankeeFacingNorth);
        Assert.True(result.Success, $"Initial PUSH failed: {result.Message}");
        var pushOff = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.True(pushOff.StartsAtStand, "the first move off B12 is the stand push-off");

        // A few seconds in, the push-off is still running: nothing has turned yet.
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);

        var amend = engine.SendCommand("SWA1360", "PUSH FACE S");
        _output.WriteLine($"Amend result: success={amend.Success} msg={amend.Message}");
        Assert.True(amend.Success, $"PUSH FACE S amendment failed: {amend.Message}");
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);

        Assert.True(TickUntilHolding(engine, ac), $"the push never finished; phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");

        double hdg = ac.TrueHeading.Degrees;
        double offSouthDeg = Math.Abs(NormalizeAngle(hdg - southbound));
        double offNorthDeg = Math.Abs(NormalizeAngle(hdg - northbound));
        double crossFt = GeoMath.SignedCrossTrackDistanceNm(ac.Position, exit.Position, new TrueHeading(southbound)) * GeoMath.FeetPerNm;
        _output.WriteLine(
            $"final nose {hdg:F2}° ({offSouthDeg:F2}° off southbound, {offNorthDeg:F2}° off northbound), {crossFt:F2} ft off Y's centreline"
        );
        Assert.True(offSouthDeg <= 1.0, $"the push ended {offSouthDeg:F2}° off Y's southbound direction ({southbound:F1}°)");
        Assert.True(Math.Abs(crossFt) <= 1.0, $"the push ended {crossFt:F2} ft off Y's centreline");
        Assert.True(offSouthDeg < offNorthDeg, "the final nose is closer to the original facing (N) than the amended one (S)");
    }

    [Fact]
    public void NonHeadingPush_DuringActivePushback_Rejected()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        Assert.True(engine.SendCommand("SWA1360", PushYankeeFacingNorth).Success);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        // Try a non-heading-only PUSH — should fail without disturbing the active phase
        var bad = engine.SendCommand("SWA1360", "PUSH @B12");
        _output.WriteLine($"Bad PUSH result: success={bad.Success} msg={bad.Message}");
        Assert.False(bad.Success, "Non-heading-only PUSH during active pushback should fail");
        Assert.Contains("face/tail amendment", bad.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A push to a stand (<c>PUSH @B13</c> off B12) parks on the stand's own heading: a heading-only PUSH during its
    /// push-off is refused, and the push-off keeps running.
    /// </summary>
    [Fact]
    public void HeadingOnlyPush_DuringAStandPushback_Refused()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);
        var push = engine.SendCommand("SWA1360", "PUSH @B13");
        Assert.True(push.Success, $"PUSH @B13 failed: {push.Message}");
        var pushOff = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.True(pushOff.StartsAtStand, "the first move off B12 is the stand push-off");
        engine.TickOneSecond();
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);

        var amend = engine.SendCommand("SWA1360", "PUSH FACE S");
        _output.WriteLine($"Amend result: success={amend.Success} msg={amend.Message}");

        Assert.False(amend.Success, "a facing change on a push to a stand was accepted");
        Assert.Equal("Unable, a pushback to a stand keeps the stand's heading", amend.Message);
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Once the stand push-off has finished — the push has moved on to its turn — a heading-only PUSH is refused and
    /// the move under way is left alone.
    /// </summary>
    [Fact]
    public void HeadingOnlyPush_AfterThePushOffCompleted_Rejected()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        Assert.True(engine.SendCommand("SWA1360", PushYankeeFacingNorth).Success);
        var pushOff = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.True(pushOff.StartsAtStand, "the first move off B12 is the stand push-off");

        bool pastPushOff = false;
        for (int tick = 0; (tick < 120) && !pastPushOff; tick++)
        {
            engine.TickOneSecond();
            pastPushOff = !ReferenceEquals(ac.Phases?.CurrentPhase, pushOff);
        }

        Assert.True(pastPushOff, "test setup: the push-off never finished");
        var running = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.False(running.StartsAtStand);

        var late = engine.SendCommand("SWA1360", "PUSH FACE E");
        _output.WriteLine($"Late amend result: success={late.Success} msg={late.Message} phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");

        Assert.False(late.Success, "a facing change after the push-off was accepted");
        Assert.Equal("Unable, pushback turn in progress", late.Message);
        Assert.Same(running, ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A plain <c>PUSH FACE E</c> amended to <c>PUSH FACE W</c> during the push-off ends facing west — the true
    /// heading of magnetic west, within a degree.
    /// </summary>
    [Fact]
    public void PushFaceEastAmendedToWest_DuringThePushOff_EndsOnTrueWest()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        var push = engine.SendCommand("SWA1360", "PUSH FACE E");
        Assert.True(push.Success, $"PUSH FACE E failed: {push.Message}");
        var pushOff = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        engine.TickOneSecond();
        engine.TickOneSecond();
        engine.TickOneSecond();
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);

        var amend = engine.SendCommand("SWA1360", "PUSH FACE W");
        _output.WriteLine($"Amend result: success={amend.Success} msg={amend.Message}");
        Assert.True(amend.Success, $"PUSH FACE W amendment failed: {amend.Message}");
        Assert.Same(pushOff, ac.Phases?.CurrentPhase);

        Assert.True(TickUntilHolding(engine, ac), $"the push never finished; phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");
        double west = MagneticDeclination.MagneticToTrue(270.0, ac.Position);
        double offDeg = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - west));
        _output.WriteLine($"final nose {ac.TrueHeading.Degrees:F2}°, true west {west:F2}°, {offDeg:F2}° off");
        Assert.True(offDeg <= 1.0, $"the push ended {offDeg:F2}° off true west ({west:F1}°)");
    }

    /// <summary>
    /// <c>PUSH FACE E</c> names a magnetic facing: the aircraft ends with its nose on the true heading of magnetic
    /// east (about 13° more at SFO), within a degree.
    /// </summary>
    [Fact]
    public void PushFaceEast_EndsOnTheTrueHeadingOfMagneticEast()
    {
        var engine = SpawnSwa1360();
        if (engine is null)
        {
            return;
        }

        var ac = engine.FindAircraft("SWA1360");
        Assert.NotNull(ac);

        var push = engine.SendCommand("SWA1360", "PUSH FACE E");
        Assert.True(push.Success, $"PUSH FACE E failed: {push.Message}");
        Assert.True(TickUntilHolding(engine, ac), $"the push never finished; phase={ac.Phases?.CurrentPhase?.Name ?? "null"}");

        double east = MagneticDeclination.MagneticToTrue(90.0, ac.Position);
        double offDeg = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - east));
        _output.WriteLine($"final nose {ac.TrueHeading.Degrees:F2}°, true of magnetic east {east:F2}°, {offDeg:F2}° off");
        Assert.True(offDeg <= 1.0, $"the push ended {offDeg:F2}° off the true heading of magnetic east ({east:F1}°)");
    }

    private static bool TickUntilHolding(SimulationEngine engine, AircraftState ac)
    {
        for (int tick = 0; tick < 240; tick++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase)
            {
                return true;
            }
        }

        return false;
    }

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
}
