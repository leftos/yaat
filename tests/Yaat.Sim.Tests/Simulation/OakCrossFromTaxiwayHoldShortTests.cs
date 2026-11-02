using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <c>CROSS {runway}</c> given while the aircraft holds short of a taxiway whose route never reaches that runway: the aircraft
/// taxis on along the held taxiway across the runway and holds past its far-side holding position, when the taxiway crosses it
/// first. N45912 (C172) at OAK is cleared <c>TAXI D C HS {taxiway} RWY 28R</c> and holds short of the taxiway; J (off D) crosses
/// 28R/10L, whose two holding positions on J are a near and a far bar, before it reaches 28L/10R; E (off C) meets only 28R/10L,
/// by a single bar, and never reaches 28L. The crossing keeps the runway assignment the aircraft held short with: 28R from that
/// clearance, none from <c>TAXI D HS J</c>.
///
/// Recording: S1-OAK-P (A) | S1 Rating Practical Exam (shared with <see cref="OakCtoAtTaxiwayHoldShortTests"/>).
/// </summary>
[Collection("NavDbMutator")]
public class OakCrossFromTaxiwayHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-hs-directional-hint-recording.zip";
    private const string Callsign = "N45912";
    private const int JustBeforeTaxi = 1363;

    /// <summary>The arrival that exits 28R onto E while N45912 taxis out; deleted so it stands nowhere on the way.</summary>
    private const string ExitingTrafficOnE = "N314WN";

    /// <summary>Hang guard for the taxi to the hold short, in simulated seconds.</summary>
    private const int TaxiToHoldLimitSeconds = 900;

    /// <summary>Hang guard for the crossing of 28R and the stop past its far-side bar, in simulated seconds.</summary>
    private const int CrossLimitSeconds = 600;

    /// <summary>How long (simulated s) a refused or stopped aircraft is watched to stay where it is.</summary>
    private const int StillCheckSeconds = 30;

    /// <summary>Below this indicated airspeed (kt) the aircraft counts as stopped.</summary>
    private const double StoppedIasKts = 0.5;

    /// <summary>How far (ft) an aircraft standing still may drift (a stopped aircraft settles a little).</summary>
    private const double StillStandingFt = 1.0;

    /// <summary>
    /// Held short of J on D, <c>CROSS 28R</c>: the pilot reads back the crossing and where it crosses, the route goes on along J
    /// across 28R, only that crossing cleared, and the aircraft stops with its tail past 28R's far-side bar on J and its nose short of
    /// 28L's bar.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cross28R_CrossesOnJ_HoldsPastTheFarSideBar()
    {
        SimulationEngine engine = EngineHoldingShortOf("J", "TAXI D C HS J RWY 28R");
        AirportGroundLayout layout = engine.FindAircraft(Callsign)!.Ground.Layout!;
        (GroundNode near, GroundNode far) = BarsOn(layout, engine.FindAircraft(Callsign)!, "28R", "J");

        CommandResult cross = engine.SendCommand(Callsign, "CROSS 28R");
        output.WriteLine($"[CROSS 28R] success={cross.Success} message={cross.Message}");
        Assert.True(cross.Success, $"CROSS 28R held short of J refused: {cross.Message}");
        output.WriteLine($"[CROSS 28R] readback terminal={cross.PilotReadback?.Speech.Terminal} spoken={cross.PilotReadback?.Speech.Tts}");
        Assert.Equal("cross runway 28R at J", cross.PilotReadback?.Speech.Terminal);
        Assert.Equal("cross runway two eight right at juliett", cross.PilotReadback?.Speech.Tts);

        TaxiRoute route = Assert.IsType<TaxiRoute>(engine.FindAircraft(Callsign)!.Ground.AssignedTaxiRoute);
        LogRoute("after CROSS 28R", route);
        Assert.Equal(far.Id, route.Segments[^1].ToNodeId);
        Assert.Equal("J", route.Segments[^1].TaxiwayName, ignoreCase: true);
        Assert.Contains(route.HoldShortPoints, hs => (hs.Reason == HoldShortReason.RunwayCrossing) && hs.IsCleared && IsRunway(hs, "28R"));
        Assert.DoesNotContain(route.HoldShortPoints, hs => (hs.Reason == HoldShortReason.RunwayCrossing) && hs.IsCleared && !IsRunway(hs, "28R"));

        double farFromNearFt = DistanceFt(near.Position, far.Position);
        AircraftState stopped = TickUntil(
            engine,
            a => (DistanceFt(near.Position, a.Position) > farFromNearFt) && (a.IndicatedAirspeed < StoppedIasKts),
            CrossLimitSeconds,
            "stopped past 28R's far-side bar on J"
        );
        output.WriteLine($"[stopped] phase={stopped.Phases?.CurrentPhase?.Name} pos=({stopped.Position.Lat:F6}, {stopped.Position.Lon:F6})");
        AssertTailClearOfFarBarAndNoseShortOf28L(layout, stopped, near, far);
        LatLon stoppedAt = stopped.Position;
        TickSeconds(engine, StillCheckSeconds);

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.True(DistanceFt(stoppedAt, after.Position) < StillStandingFt, $"{Callsign} moved on past 28R's far-side bar");
        Assert.IsNotType<CrossingRunwayPhase>(after.Phases?.CurrentPhase);
        Assert.True(after.IsOnGround);
    }

    /// <summary>
    /// Held short of J on D with 28R assigned, <c>CROSS 28R</c>: a crossing is not a new taxi clearance, so the aircraft keeps the
    /// runway it was assigned, not the one the route's last bar names (10L, the crossed runway's far side), and its departure runway.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cross28R_WithAssigned28R_Keeps28R()
    {
        SimulationEngine engine = EngineHoldingShortOf("J", "TAXI D C HS J RWY 28R");
        AircraftState held = engine.FindAircraft(Callsign)!;
        Assert.Equal("28R", held.Phases?.AssignedRunway?.Designator);
        string? departureRunwayBefore = held.Procedure.DepartureRunway;

        AircraftState crossed = Cross28RFromTheJHold(engine);

        Assert.Equal("28R", crossed.Phases?.AssignedRunway?.Designator);
        Assert.Equal(departureRunwayBefore, crossed.Procedure.DepartureRunway);
    }

    /// <summary>
    /// Held short of J on D after <c>TAXI D HS J</c>, which assigns no runway, <c>CROSS 28R</c>: the crossing assigns none either,
    /// not the runway the route's last bar names (10L), and leaves the departure runway as it was.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cross28R_NoAssignedRunway_StaysUnassigned()
    {
        SimulationEngine engine = EngineHoldingShortOf("J", "TAXI D HS J");
        AircraftState held = engine.FindAircraft(Callsign)!;
        Assert.Null(held.Phases?.AssignedRunway);
        string? departureRunwayBefore = held.Procedure.DepartureRunway;

        AircraftState crossed = Cross28RFromTheJHold(engine);

        Assert.Null(crossed.Phases?.AssignedRunway);
        Assert.Equal(departureRunwayBefore, crossed.Procedure.DepartureRunway);
    }

    /// <summary>
    /// Sends <c>CROSS 28R</c> to N45912 held short of J and asserts it applied as a taxi on along J with the crossing of 28R cleared.
    /// </summary>
    private AircraftState Cross28RFromTheJHold(SimulationEngine engine)
    {
        CommandResult cross = engine.SendCommand(Callsign, "CROSS 28R");
        output.WriteLine($"[CROSS 28R] success={cross.Success} message={cross.Message}");
        Assert.True(cross.Success, $"CROSS 28R held short of J refused: {cross.Message}");

        AircraftState crossed = engine.FindAircraft(Callsign)!;
        TaxiRoute route = Assert.IsType<TaxiRoute>(crossed.Ground.AssignedTaxiRoute);
        LogRoute("after CROSS 28R", route);
        Assert.Equal("J", route.Segments[^1].TaxiwayName, ignoreCase: true);
        Assert.Contains(route.HoldShortPoints, hs => (hs.Reason == HoldShortReason.RunwayCrossing) && hs.IsCleared && IsRunway(hs, "28R"));
        output.WriteLine($"[CROSS 28R] assigned runway={crossed.Phases?.AssignedRunway?.Designator ?? "(none)"}");
        return crossed;
    }

    /// <summary>
    /// Held short of J on D, <c>CROSS 28L</c>: J reaches 28R before 28L, a crossing the instruction does not include, so the pilot
    /// says unable, naming it, and nothing changes.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cross28L_NeedsToCross28R_SaysUnable_KeepsTheHold()
    {
        SimulationEngine engine = EngineHoldingShortOf("J", "TAXI D C HS J RWY 28R");

        CommandResult result = AssertRefusedAndStillHeld(engine, "J", "CROSS 28L");

        Assert.Equal("unable, need to cross runway 28R, holding short of J", result.PilotUnable?.Terminal);
        Assert.Contains("unable, need to cross runway two eight right, holding short of juliett", result.PilotUnable?.Tts ?? "");
        Assert.Equal(result.PilotUnable!.Terminal, result.Message);
    }

    /// <summary>
    /// Held short of E on C, <c>CROSS 28L</c>: E never reaches 28L/10R, so the controller is told the taxiway does not cross it, and
    /// nothing changes.
    /// </summary>
    [Fact]
    public void HeldShortOfE_Cross28L_TaxiwayDoesNotCrossIt_Refused()
    {
        SimulationEngine engine = EngineHoldingShortOf("E", "TAXI D C HS E RWY 28R");

        CommandResult result = AssertRefusedAndStillHeld(engine, "E", "CROSS 28L");

        Assert.Equal("CROSS 28L: taxiway E does not cross runway 28L", result.Message);
        Assert.Null(result.PilotUnable);
    }

    /// <summary>
    /// Sends <paramref name="command"/>, asserts it was refused, then that over <see cref="StillCheckSeconds"/> the aircraft stays
    /// held short of <paramref name="taxiway"/> where it stood, with its route, runway and (absent) departure clearance unchanged.
    /// </summary>
    private CommandResult AssertRefusedAndStillHeld(SimulationEngine engine, string taxiway, string command)
    {
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        LatLon positionBefore = aircraft.Position;
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"[{command}] success={result.Success} message={result.Message} spoken={result.PilotUnable?.Tts}");
        Assert.False(result.Success, $"{command} held short of {taxiway} was accepted: {result.Message}");
        TickSeconds(engine, StillCheckSeconds);

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.True(IsHoldingShortOf(after, taxiway), $"{Callsign} is no longer holding short of {taxiway}");
        Assert.Same(routeBefore, after.Ground.AssignedTaxiRoute);
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
        Assert.True(DistanceFt(positionBefore, after.Position) < StillStandingFt, $"{Callsign} moved from the hold short of {taxiway}");
        return result;
    }

    /// <summary>The runway's two holding positions on <paramref name="taxiway"/>: the one nearer the aircraft, and the far side.</summary>
    private static (GroundNode Near, GroundNode Far) BarsOn(AirportGroundLayout layout, AircraftState aircraft, string runway, string taxiway)
    {
        List<GroundNode> bars = [.. layout.GetRunwayHoldShortNodes(runway).Where(n => n.Edges.Any(e => e.MatchesTaxiway(taxiway)))];
        Assert.Equal(2, bars.Count);
        List<GroundNode> byDistance = [.. bars.OrderBy(n => GeoMath.DistanceNm(aircraft.Position, n.Position))];
        return (byDistance[0], byDistance[1]);
    }

    /// <summary>
    /// The stopped aircraft's tail (its centre less half its length along its heading) stands past 28R's far-side bar
    /// <paramref name="far"/> on J by more than <see cref="RunwayTailClearance.TailClearToleranceFt"/>, the sim's bound for a tail
    /// clear of the marking (AIM 4-3-21.b), and its nose (centre plus half its length) short of 28L's bar on J.
    /// </summary>
    private void AssertTailClearOfFarBarAndNoseShortOf28L(AirportGroundLayout layout, AircraftState stopped, GroundNode near, GroundNode far)
    {
        double halfLengthNm = AircraftLength.ResolveFt(stopped.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon tail = GeoMath.ProjectPoint(stopped.Position, stopped.TrueHeading + 180.0, halfLengthNm);
        LatLon nose = GeoMath.ProjectPoint(stopped.Position, stopped.TrueHeading, halfLengthNm);
        double tailPastFarBarFt = AlongFt(far.Position, tail, GeoMath.BearingTo(near.Position, far.Position));

        List<GroundNode> bars28L = [.. layout.GetRunwayHoldShortNodes("28L").Where(n => n.Edges.Any(e => e.MatchesTaxiway("J")))];
        Assert.NotEmpty(bars28L);
        GroundNode bar28L = bars28L.MinBy(n => GeoMath.DistanceNm(far.Position, n.Position))!;
        double noseShortOf28LBarFt = -AlongFt(bar28L.Position, nose, GeoMath.BearingTo(far.Position, bar28L.Position));

        output.WriteLine(
            $"[stopped] tail {tailPastFarBarFt:F1} ft past 28R's far bar on J; nose {noseShortOf28LBarFt:F1} ft short of 28L's bar on J"
        );
        Assert.True(
            tailPastFarBarFt > RunwayTailClearance.TailClearToleranceFt,
            $"tail {tailPastFarBarFt:F1} ft past 28R's far-side bar on J, not beyond the tail-clear tolerance of "
                + $"{RunwayTailClearance.TailClearToleranceFt:F1} ft"
        );
        Assert.True(noseShortOf28LBarFt > 0, $"nose {-noseShortOf28LBarFt:F1} ft past 28L's bar on J");
    }

    /// <summary>
    /// How far (ft) <paramref name="point"/> lies from <paramref name="origin"/> along the true bearing <paramref name="bearingDeg"/>.
    /// </summary>
    private static double AlongFt(LatLon origin, LatLon point, double bearingDeg) =>
        DistanceFt(origin, point) * Math.Cos((GeoMath.BearingTo(origin, point) - bearingDeg) * Math.PI / 180.0);

    private static bool IsRunway(HoldShortPoint hs, string runway) =>
        (hs.TargetName is { } target) && RunwayIdentifier.Parse(target).Contains(runway);

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static bool IsHoldingShortOf(AircraftState aircraft, string taxiway) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } target }
        && string.Equals(target, taxiway, StringComparison.OrdinalIgnoreCase);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("a CROSS at a taxiway hold short taxiing on across the runway");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// Replays to the taxi, clears N45912 with <paramref name="clearance"/> (a taxi that holds short of <paramref name="taxiway"/>),
    /// ticks it to that hold short and deletes N314WN.
    /// </summary>
    private SimulationEngine EngineHoldingShortOf(string taxiway, string clearance)
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(recording);
        Assert.NotNull(engine);
        engine.Replay(recording, JustBeforeTaxi);
        CommandResult taxi = engine.SendCommand(Callsign, clearance);
        Assert.True(taxi.Success, $"{clearance} refused: {taxi.Message}");
        TickUntil(engine, a => IsHoldingShortOf(a, taxiway), TaxiToHoldLimitSeconds, $"holding short of {taxiway}");
        Assert.True(engine.SendCommand(ExitingTrafficOnE, "DEL").Success);
        return engine;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            engine.TickOneSecond();
        }
    }

    private AircraftState TickUntil(SimulationEngine engine, Func<AircraftState, bool> reached, int limitSeconds, string what)
    {
        for (int t = 0; t <= limitSeconds; t++)
        {
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            if (reached(aircraft))
            {
                output.WriteLine($"{Callsign} {what} after {t}s");
                return aircraft;
            }

            engine.TickOneSecond();
        }

        AircraftState? last = engine.FindAircraft(Callsign);
        Assert.Fail(
            $"hung: {Callsign} never {what} within {limitSeconds}s: phase={last?.Phases?.CurrentPhase?.Name ?? "(none)"} "
                + $"pos=({last?.Position.Lat:F6}, {last?.Position.Lon:F6})"
        );
        return last!;
    }

    private void LogRoute(string label, TaxiRoute route)
    {
        output.WriteLine(
            $"[{label}] taxiways=[{string.Join(", ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))}]"
        );
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} cleared={hs.IsCleared}");
        }

        foreach (TaxiRouteSegment s in route.Segments)
        {
            output.WriteLine($"[{label}]   segment {s.TaxiwayName} {s.FromNodeId}->{s.ToNodeId}");
        }
    }
}
