using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A takeoff clearance given while the aircraft holds short of a taxiway is an implicit taxi onto that taxiway for an
/// intersection departure. N45912 (C172) at OAK is cleared <c>TAXI D C HS E RWY 28R</c> and holds short of E on C; a
/// <c>CTO</c> there sends it onto E to 28R's hold line at E, where it lines up and departs 28R at E. The pilot reads the
/// departure point back: "cleared for takeoff runway 28R at E".
///
/// Recording: S1-OAK-P (A) | S1 Rating Practical Exam (shared with <see cref="OakHsDirectionalHintTests"/>).
/// </summary>
[Collection("NavDbMutator")]
public class OakCtoAtTaxiwayHoldShortTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-hs-directional-hint-recording.zip";
    private const string Callsign = "N45912";
    private const int JustBeforeTaxi = 1363;

    /// <summary>The arrival that exits 28R onto E while N45912 taxis out, and stops on E.</summary>
    private const string ExitingTrafficOnE = "N314WN";

    /// <summary>Hang guard for the taxi to the hold short of E, in simulated seconds.</summary>
    private const int TaxiToHoldLimitSeconds = 900;

    /// <summary>Hang guard for the taxi onto E, the line-up and the start of the roll, in simulated seconds.</summary>
    private const int CtoToTakeoffLimitSeconds = 900;

    /// <summary>How far (ft) from 28R's bar on E the aircraft may be when its takeoff roll starts: the line-up's length.</summary>
    private const double TakeoffStartNearBarFt = 700;

    /// <summary>
    /// How far (ft) past the turn off C onto E the traffic standing on E is placed: on the first segment on E, which runs about 53 ft
    /// from the end of the C-E fillet to 28R's bar, so short of the bar.
    /// </summary>
    private const double PastTheTurnFt = 20.0;

    /// <summary>How far (ft) behind N45912 on C the traffic standing there is placed.</summary>
    private const double BehindOnCFt = 150.0;

    /// <summary>The taxiing traffic's speed (kt): well above at rest.</summary>
    private const double TaxiingTrafficKts = 15.0;

    /// <summary>The airborne traffic's altitude (ft MSL) and speed (kt) over E.</summary>
    private const double AirborneTrafficAltitudeFt = 500.0;

    private const double AirborneTrafficKts = 90.0;

    /// <summary>How long (simulated s) an aircraft that said unable is watched to stay held short of E.</summary>
    private const int UnableHoldCheckSeconds = 30;

    /// <summary>Below this indicated airspeed (kt) the aircraft counts as stopped.</summary>
    private const double StoppedIasKts = 0.5;

    /// <summary>The oldest roll clock (s) a takeoff roll first sampled can show and still have started from a standstill.</summary>
    private const double StandingStartRollClockSeconds = 1.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("a CTO at a taxiway hold short becoming an intersection departure");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    private void LogRoute(string label, TaxiRoute? route)
    {
        if (route is null)
        {
            output.WriteLine($"[{label}] route=<null>");
            return;
        }

        output.WriteLine(
            $"[{label}] taxiways=[{string.Join(", ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))}]"
        );
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} cleared={hs.IsCleared}");
        }
    }

    private static bool IsHoldingShortOfE(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } target }
        && string.Equals(target, "E", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>TAXI D C HS E RWY 28R</c>: the route ends at the hold short of E; a CTO there departs 28R at E.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_Cto_DepartsFrom28RAtE() =>
        AssertCtoAtHoldShortOfEDepartsFrom28RAtE("TAXI D C HS E RWY 28R", "CTO", "cleared for takeoff runway 28R at E", "two eight right at echo");

    /// <summary>
    /// <c>RWY 28R TAXI D C B HS E</c>: the route holds short of E on C and goes on along B to 28R's full length; a CTO given
    /// at the hold short of E departs 28R at E, not from B.
    /// </summary>
    [Fact]
    public void RunwayFirst_DCBHsE_HeldShortOfE_Cto_DepartsFrom28RAtE() =>
        AssertCtoAtHoldShortOfEDepartsFrom28RAtE("RWY 28R TAXI D C B HS E", "CTO", "cleared for takeoff runway 28R at E", "two eight right at echo");

    /// <summary><c>CTO IMM</c> at the hold short of E: "cleared for immediate takeoff runway 28R at E".</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_CtoImmediate_ReadsBackImmediateAtE() =>
        AssertCtoAtHoldShortOfEDepartsFrom28RAtE(
            "TAXI D C HS E RWY 28R",
            "CTO IMM",
            "cleared for immediate takeoff runway 28R at E",
            "cleared for immediate takeoff runway two eight right at echo"
        );

    private void AssertCtoAtHoldShortOfEDepartsFrom28RAtE(string clearance, string ctoCommand, string terminalLead, string spoken)
    {
        SimulationEngine? engine = EngineHoldingShortOfE(clearance);
        if (engine is null)
        {
            return;
        }

        CommandResult cto = SendCto(engine, ctoCommand);
        Assert.StartsWith(terminalLead, cto.PilotReadback?.Speech.Terminal ?? "");
        Assert.Contains(spoken, cto.PilotReadback?.Speech.Tts ?? "");
        LatLon barPosition = AssertRouteEndsAt28RBarOnE(engine);

        AircraftState aircraft = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, CtoToTakeoffLimitSeconds, "in TakeoffPhase");
        double fromBarFt = GeoMath.DistanceNm(aircraft.Position, barPosition) * GeoMath.FeetPerNm;
        output.WriteLine($"[takeoff] pos=({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) {fromBarFt:F0} ft from the 28R bar on E");
        Assert.True(fromBarFt < TakeoffStartNearBarFt, $"{Callsign} began its takeoff roll {fromBarFt:F0} ft from 28R's bar on E");
        Assert.Equal("28R", aircraft.Procedure.DepartureRunway);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// A Heavy makes no rolling takeoff (7110.65 3-9-5): the intersection departure from E lines up and comes to a stop on 28R before
    /// its takeoff roll starts.
    /// </summary>
    [Fact]
    public void Heavy_DCHsE_HeldShortOfE_Cto_StopsAfterLineUpBeforeTakeoff()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        AircraftState? heavy = engine.FindAircraft(Callsign);
        Assert.NotNull(heavy);
        heavy.AircraftType = "B744";
        Assert.False(LineUpPhase.IsAircraftEligibleForRollingTakeoff(heavy.AircraftType), "B744 must be a Heavy");

        CommandResult cto = SendCto(engine, "CTO");
        Assert.StartsWith("cleared for takeoff runway 28R at E", cto.PilotReadback?.Speech.Terminal ?? "");
        AssertRouteEndsAt28RBarOnE(engine);

        bool linedUp = false;
        bool stoppedLinedUp = false;
        for (int t = 0; t <= CtoToTakeoffLimitSeconds; t++)
        {
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            Phase? phase = aircraft.Phases?.CurrentPhase;
            if (phase is TakeoffPhase takeoff)
            {
                // Samples are a second apart, so the stop and the start of the roll can fall inside one: a roll first seen at most a
                // second into its clock began from a standstill, while a rolling takeoff starts its clock at its line-up speed.
                stoppedLinedUp |= linedUp && (takeoff.RollElapsedSeconds <= StandingStartRollClockSeconds);
                output.WriteLine($"{Callsign} in TakeoffPhase after {t}s (lined up: {linedUp}, stopped lined up: {stoppedLinedUp})");
                Assert.True(stoppedLinedUp, $"{Callsign} (B744) began its takeoff roll without stopping after lining up");
                return;
            }

            linedUp |= phase is LineUpPhase or LinedUpAndWaitingPhase;
            stoppedLinedUp |= linedUp && (aircraft.IndicatedAirspeed < StoppedIasKts);
            engine.TickOneSecond();
        }

        Assert.Fail($"hung: {Callsign} never in TakeoffPhase within {CtoToTakeoffLimitSeconds}s");
    }

    /// <summary>
    /// The implicit taxi onto E enters 28R within seconds, so a CTO there checks 28R for occupancy as one at 28R's own hold short
    /// does (7110.65 3-9-6): an aircraft holding in position on 28R draws the advisory.
    /// </summary>
    [Fact]
    public void DCHsE_HeldShortOfE_Cto_AircraftLinedUpOn28R_Warns()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        RunwayInfo? runway = TestVnasData.NavigationDb!.GetRunway("KOAK", "28R");
        Assert.NotNull(runway);
        var occupant = new AircraftState
        {
            Callsign = "N100LU",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        occupant.Phases.Add(new LinedUpAndWaitingPhase());
        occupant.Phases.Start(CommandDispatcher.BuildMinimalContext(occupant, ActiveRunways.Empty));
        engine.World.AddAircraft(occupant);

        SendCto(engine, "CTO");
        AircraftState? departure = engine.FindAircraft(Callsign);
        Assert.NotNull(departure);
        output.WriteLine($"[CTO] warnings=[{string.Join(" | ", departure.PendingWarnings)}]");
        Assert.Contains(departure.PendingWarnings, w => w.Contains("N100LU", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// With N314WN still standing on E at 28R's bar, a CTO at the hold short of E is refused at once: the pilot says unable, stays
    /// held short of E, and no takeoff clearance or taxi onto E is installed. The controller re-issues the CTO once E is clear.
    /// </summary>
    [Fact]
    public void DCHsE_HeldShortOfE_N314WNOnE_Cto_SaysUnableAndKeepsTheHold() => AssertUnableWithTrafficOnE("CTO");

    /// <summary>With N314WN still standing on E at 28R's bar, a LUAW at the hold short of E is refused as a CTO there is.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_N314WNOnE_Luaw_SaysUnableAndKeepsTheHold() => AssertUnableWithTrafficOnE("LUAW");

    private void AssertUnableWithTrafficOnE(string command)
    {
        SimulationEngine? engine = EngineHeldShortOfEWithTrafficOnE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(engine.FindAircraft(ExitingTrafficOnE));
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        int eSegmentsBefore = CountESegments(routeBefore);

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"[{command}] success={result.Success} message={result.Message}");
        output.WriteLine($"[{command}] unable terminal={result.PilotUnable?.Terminal} spoken={result.PilotUnable?.Tts}");
        Assert.False(result.Success, $"{command} with N314WN on E was accepted: {result.Message}");
        Assert.NotNull(result.PilotUnable);
        Assert.Equal("unable, traffic on E", result.PilotUnable.Terminal);
        Assert.Contains("unable, traffic on echo", result.PilotUnable.Tts);

        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState? after = engine.FindAircraft(Callsign);
        Assert.NotNull(after);
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.True(IsHoldingShortOfE(after), $"{Callsign} is no longer holding short of E: {after.Phases?.CurrentPhase?.Name}");
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.Same(routeBefore, after.Ground.AssignedTaxiRoute);
        Assert.Equal(eSegmentsBefore, CountESegments(after.Ground.AssignedTaxiRoute));
    }

    /// <summary>
    /// A LUAW at the hold short of E is an implicit taxi onto E and a line-up at the intersection (7110.65 3-9-4.m): the pilot reads
    /// back "line up and wait runway 28R at E", taxis E to 28R's bar, lines up on 28R there and holds in position.
    /// </summary>
    [Fact]
    public void DCHsE_HeldShortOfE_Luaw_LinesUpOn28RAtE()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        LineUpAtE(engine);
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("E", aircraft.Phases?.LineUpIntersectionTaxiway);
    }

    /// <summary>
    /// Lined up at E after a LUAW there, a CTO needs no taxi step and its readback still names the intersection: "cleared for takeoff
    /// runway 28R at E". The intersection is forgotten once the takeoff roll starts.
    /// </summary>
    [Fact]
    public void DCHsE_LuawAtE_LinedUp_ThenCto_ReadsBackAtE()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        LineUpAtE(engine);
        CommandResult cto = SendCto(engine, "CTO");
        Assert.StartsWith("cleared for takeoff runway 28R at E", AppliedCtoReadback(engine, cto).Terminal);
        Assert.Contains("cleared for takeoff runway two eight right at echo", AppliedCtoReadback(engine, cto).Tts);

        AircraftState aircraft = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, CtoToTakeoffLimitSeconds, "in TakeoffPhase");
        Assert.Null(aircraft.Phases?.LineUpIntersectionTaxiway);
    }

    /// <summary>
    /// A CTO given after a LUAW at E while the aircraft still taxis E toward 28R's bar replaces the stored line-up, reads back "at E"
    /// and departs 28R from E.
    /// </summary>
    [Fact]
    public void DCHsE_LuawAtE_WhileTaxiingOnE_ThenCto_ReadsBackAtE()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        SendCto(engine, "LUAW");
        LatLon barPosition = AssertRouteEndsAt28RBarOnE(engine);
        TickUntil(engine, IsTaxiingOnE, CtoToTakeoffLimitSeconds, "taxiing on E");

        CommandResult cto = SendCto(engine, "CTO");
        Assert.StartsWith("cleared for takeoff runway 28R at E", AppliedCtoReadback(engine, cto).Terminal);
        Assert.Contains("cleared for takeoff runway two eight right at echo", AppliedCtoReadback(engine, cto).Tts);

        AircraftState aircraft = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, CtoToTakeoffLimitSeconds, "in TakeoffPhase");
        double fromBarFt = GeoMath.DistanceNm(aircraft.Position, barPosition) * GeoMath.FeetPerNm;
        Assert.True(fromBarFt < TakeoffStartNearBarFt, $"{Callsign} began its takeoff roll {fromBarFt:F0} ft from 28R's bar on E");
        Assert.Equal("28R", aircraft.Procedure.DepartureRunway);
    }

    /// <summary>
    /// Lined up at E after a LUAW there, a CTOC leaves the aircraft lined up at E, so a CTO afterwards still departs from E and reads
    /// it back (7110.65 3-9-10.b).
    /// </summary>
    [Fact]
    public void DCHsE_LuawAtE_LinedUp_ThenCtocThenCto_StillReadsBackAtE()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        LineUpAtE(engine);
        SendCto(engine, "CTO");
        CommandResult cancel = engine.SendCommand(Callsign, "CTOC");
        Assert.True(cancel.Success, $"CTOC refused: {cancel.Message}");
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.Equal("E", aircraft.Phases?.LineUpIntersectionTaxiway);

        CommandResult cto = SendCto(engine, "CTO");
        Assert.StartsWith("cleared for takeoff runway 28R at E", AppliedCtoReadback(engine, cto).Terminal);
    }

    /// <summary>
    /// A LUAW at E followed, while the aircraft still taxis on E, by a new TAXI to 28R's full length forgets the intersection: a CTO
    /// afterwards reads back no "at E".
    /// </summary>
    [Fact]
    public void DCHsE_LuawAtE_WhileTaxiingOnE_ThenTaxiToFullLength_ThenCto_ReadsBackNoIntersection()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        SendCto(engine, "LUAW");
        TickUntil(engine, IsTaxiingOnE, CtoToTakeoffLimitSeconds, "taxiing on E");
        CommandResult retaxi = engine.SendCommand(Callsign, "TAXI E C B RWY 28R");
        output.WriteLine($"[re-taxi] success={retaxi.Success} message={retaxi.Message}");
        Assert.True(retaxi.Success, $"TAXI E C B RWY 28R refused: {retaxi.Message}");
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        LogRoute("after re-taxi", aircraft.Ground.AssignedTaxiRoute);
        Assert.Null(aircraft.Phases?.LineUpIntersectionTaxiway);

        CommandResult cto = SendCto(engine, "CTO");
        PilotSpeechText readback = AppliedCtoReadback(engine, cto);
        Assert.StartsWith("cleared for takeoff runway 28R", readback.Terminal);
        Assert.DoesNotContain(" at E", readback.Terminal);
    }

    /// <summary>A helicopter lined up at E after a LUAW there forgets the intersection once its takeoff starts.</summary>
    [Fact]
    public void Helicopter_DCHsE_LuawAtE_ThenCto_ForgetsTheIntersectionAtTakeoff()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        AircraftState? helicopter = engine.FindAircraft(Callsign);
        Assert.NotNull(helicopter);
        helicopter.AircraftType = "S76";
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(helicopter.AircraftType));

        LineUpAtE(engine);
        SendCto(engine, "CTO");
        AircraftState aircraft = TickUntil(
            engine,
            a => a.Phases?.CurrentPhase is HelicopterTakeoffPhase,
            CtoToTakeoffLimitSeconds,
            "in HelicopterTakeoffPhase"
        );
        Assert.Null(aircraft.Phases?.LineUpIntersectionTaxiway);
    }

    /// <summary>"LUAW WD" at the hold short of E: "line up and wait runway 28R at E, without delay".</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_LuawWithoutDelay_ReadsBackAtEWithoutDelay()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        CommandResult luaw = SendCto(engine, "LUAW WD");
        Assert.Equal("line up and wait runway 28R at E, without delay", luaw.PilotReadback?.Speech.Terminal);
        Assert.Equal("line up and wait runway two eight right at echo, without delay", luaw.PilotReadback?.Speech.Tts);
    }

    /// <summary>The RPO's terminal line of the LUAW at E readback keeps the runway and intersection clause.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_Luaw_RpoTerminalKeepsTheRunwayClause()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        CommandResult luaw = SendCto(engine, "LUAW");
        Assert.StartsWith("line up and wait runway 28R at E", luaw.PilotReadback?.Speech.TerminalForRpo ?? "");
    }

    /// <summary>Traffic at rest on E just past the turn off C stands on the way to 28R: the CTO is refused.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_TrafficStandingOnEPastTheTurn_Cto_SaysUnable()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        (LatLon turn, LatLon along) = FirstESegment(engine);
        double bearing = GeoMath.BearingTo(turn, along);
        Assert.True(
            GeoMath.DistanceNm(turn, along) * GeoMath.FeetPerNm > PastTheTurnFt,
            "the first segment on E is too short to stand traffic on it short of 28R's bar"
        );
        AddTraffic(engine, GeoMath.ProjectPoint(turn, new TrueHeading(bearing), PastTheTurnFt / GeoMath.FeetPerNm), bearing, TrafficState.AtRest);

        CommandResult result = engine.SendCommand(Callsign, "CTO");
        output.WriteLine($"[CTO] success={result.Success} message={result.Message}");
        Assert.False(result.Success, $"CTO with traffic standing on E was accepted: {result.Message}");
        Assert.Equal("unable, traffic on E", result.PilotUnable?.Terminal);
    }

    /// <summary>Traffic taxiing across E is moving through, not standing on it: the CTO is accepted.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_TrafficTaxiingAcrossE_Cto_Accepted() =>
        AssertCtoAcceptedWithTraffic(engine => MidpointOfLastESegment(engine), acrossE: true, TrafficState.Taxiing);

    /// <summary>Airborne traffic over E is not on the ground: the CTO is accepted.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_AirborneTrafficOverE_Cto_Accepted() =>
        AssertCtoAcceptedWithTraffic(engine => MidpointOfLastESegment(engine), acrossE: false, TrafficState.Airborne);

    /// <summary>Traffic at rest on C behind the held aircraft is not on its way to 28R: the CTO is accepted.</summary>
    [Fact]
    public void DCHsE_HeldShortOfE_TrafficStandingOnCBehind_Cto_Accepted() =>
        AssertCtoAcceptedWithTraffic(
            engine =>
            {
                AircraftState? held = engine.FindAircraft(Callsign);
                Assert.NotNull(held);
                var behind = new TrueHeading(held.TrueHeading.Degrees + 180.0);
                return (GeoMath.ProjectPoint(held.Position, behind, BehindOnCFt / GeoMath.FeetPerNm), held.TrueHeading.Degrees);
            },
            acrossE: false,
            TrafficState.AtRest
        );

    private void AssertCtoAcceptedWithTraffic(Func<SimulationEngine, (LatLon Position, double AlongDeg)> place, bool acrossE, TrafficState state)
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        (LatLon position, double alongDeg) = place(engine);
        AddTraffic(engine, position, acrossE ? alongDeg + 90.0 : alongDeg, state);
        CommandResult cto = SendCto(engine, "CTO");
        Assert.Null(cto.PilotUnable);
    }

    private enum TrafficState
    {
        AtRest,
        Taxiing,
        Airborne,
    }

    private static void AddTraffic(SimulationEngine engine, LatLon position, double headingDeg, TrafficState state) =>
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = "N200TR",
                AircraftType = "B738",
                Position = position,
                TrueHeading = new TrueHeading(headingDeg),
                Altitude = state == TrafficState.Airborne ? AirborneTrafficAltitudeFt : 6,
                IndicatedAirspeed = state switch
                {
                    TrafficState.AtRest => 0,
                    TrafficState.Taxiing => TaxiingTrafficKts,
                    _ => AirborneTrafficKts,
                },
                IsOnGround = state != TrafficState.Airborne,
                FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            }
        );

    /// <summary>The implicit taxi's route onto E from where N45912 holds, resolved without installing it.</summary>
    private static TaxiRoute ImplicitRouteOntoE(SimulationEngine engine)
    {
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Ground.Layout);
        TaxiRoute? route = GroundCommandHandler.ResolveTaxiRoute(
            aircraft,
            new TaxiCommand(["E"], [], Expedite: false, DestinationRunway: "28R"),
            aircraft.Ground.Layout,
            TestDispatch.EmptyTaxi
        );
        Assert.NotNull(route);
        return route;
    }

    /// <summary>
    /// The implicit route's first segment on E that starts ahead of N45912 (the turn off C onto E, past the fillet it is held on),
    /// and the segment's far end.
    /// </summary>
    private static (LatLon Turn, LatLon Along) FirstESegment(SimulationEngine engine)
    {
        TaxiRoute route = ImplicitRouteOntoE(engine);
        AircraftState held = engine.FindAircraft(Callsign)!;
        AirportGroundLayout layout = held.Ground.Layout!;
        TaxiRouteSegment first = route.Segments.First(s =>
            string.Equals(s.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase)
            && (
                Math.Abs(
                    GeoMath.SignedBearingDifference(held.TrueHeading.Degrees, GeoMath.BearingTo(held.Position, layout.Nodes[s.FromNodeId].Position))
                ) < 90.0
            )
        );
        return (layout.Nodes[first.FromNodeId].Position, layout.Nodes[first.ToNodeId].Position);
    }

    /// <summary>The midpoint of the implicit route's last segment on E, and the segment's bearing.</summary>
    private static (LatLon Position, double AlongDeg) MidpointOfLastESegment(SimulationEngine engine)
    {
        TaxiRoute route = ImplicitRouteOntoE(engine);
        AirportGroundLayout layout = engine.FindAircraft(Callsign)!.Ground.Layout!;
        TaxiRouteSegment last = route.Segments.Last(s => string.Equals(s.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase));
        LatLon from = layout.Nodes[last.FromNodeId].Position;
        LatLon to = layout.Nodes[last.ToNodeId].Position;
        double bearing = GeoMath.BearingTo(from, to);
        double halfNm = GeoMath.DistanceNm(from, to) / 2.0;
        return (GeoMath.ProjectPoint(from, new TrueHeading(bearing), halfNm), bearing);
    }

    /// <summary>An aircraft lined up at E after a LUAW there keeps the intersection through a snapshot round trip.</summary>
    [Fact]
    public void DCHsE_LuawAtE_LinedUp_SnapshotRoundTrip_KeepsTheIntersection()
    {
        SimulationEngine? engine = EngineHoldingShortOfE("TAXI D C HS E RWY 28R");
        if (engine is null)
        {
            return;
        }

        LineUpAtE(engine);
        SimulationEngine? restored = BuildEngine();
        Assert.NotNull(restored);
        restored.RestoreFromSnapshot(RoundTrip(engine.CaptureSnapshot()));

        AircraftState? aircraft = restored.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("E", aircraft.Phases?.LineUpIntersectionTaxiway);
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    /// <summary>
    /// Sends LUAW at the hold short of E, checks the "at E" readback and the route onto E, and ticks the aircraft until it is lined up and
    /// waiting on 28R within the line-up's length of 28R's bar on E.
    /// </summary>
    private void LineUpAtE(SimulationEngine engine)
    {
        CommandResult luaw = SendCto(engine, "LUAW");
        Assert.StartsWith("line up and wait runway 28R at E", luaw.PilotReadback?.Speech.Terminal ?? "");
        Assert.Contains("line up and wait runway two eight right at echo", luaw.PilotReadback?.Speech.Tts ?? "");
        LatLon barPosition = AssertRouteEndsAt28RBarOnE(engine);

        AircraftState aircraft = TickUntil(
            engine,
            a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase,
            CtoToTakeoffLimitSeconds,
            "lined up and waiting"
        );
        double fromBarFt = GeoMath.DistanceNm(aircraft.Position, barPosition) * GeoMath.FeetPerNm;
        output.WriteLine($"[lined up] {fromBarFt:F0} ft from the 28R bar on E");
        Assert.True(fromBarFt < TakeoffStartNearBarFt, $"{Callsign} lined up {fromBarFt:F0} ft from 28R's bar on E");
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal("E", aircraft.Phases?.LineUpIntersectionTaxiway);
    }

    /// <summary>
    /// The readback the pilot gives for a bare CTO that <paramref name="cto"/> answered: the handler's own when it built one, else the
    /// clause read from the aircraft as the dispatch left it (<see cref="PilotResponder.BuildReadbackAsApplied"/>).
    /// </summary>
    private static PilotSpeechText AppliedCtoReadback(SimulationEngine engine, CommandResult cto)
    {
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var compound = new CompoundCommand([new ParsedBlock(null, [new ClearedForTakeoffCommand(new DefaultDeparture())])]);
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(compound, cto, aircraft, PilotSpeechContext.Canonical, ActiveRunways.Empty);
        Assert.NotNull(readback);
        return readback;
    }

    private static bool IsTaxiingOnE(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is TaxiingPhase
        && aircraft.Ground.AssignedTaxiRoute is { } route
        && (route.CurrentSegmentIndex >= 0)
        && (route.CurrentSegmentIndex < route.Segments.Count)
        && string.Equals(route.Segments[route.CurrentSegmentIndex].TaxiwayName, "E", StringComparison.OrdinalIgnoreCase);

    private static int CountESegments(TaxiRoute? route) =>
        route?.Segments.Count(s => string.Equals(s.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase)) ?? 0;

    /// <summary>Replays to the taxi, clears N45912 and ticks it to the hold short of E, then removes the traffic standing on E.</summary>
    private SimulationEngine? EngineHoldingShortOfE(string clearance)
    {
        SimulationEngine? engine = EngineHeldShortOfEWithTrafficOnE(clearance);
        if (engine is null)
        {
            return null;
        }

        // The traffic N45912 holds short of: N314WN exits 28R onto E and stops there, holding after its exit, at 28R's bar on E.
        // With no controller to taxi it in, it would block E for good; it is removed so E is clear for the departure.
        CommandResult clearE = engine.SendCommand(ExitingTrafficOnE, "DEL");
        Assert.True(clearE.Success, $"DEL {ExitingTrafficOnE} refused: {clearE.Message}");
        return engine;
    }

    /// <summary>Replays to the taxi, clears N45912 and ticks it to the hold short of E; N314WN still stands on E at 28R's bar.</summary>
    private SimulationEngine? EngineHeldShortOfEWithTrafficOnE(string clearance)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return null;
        }

        engine.Replay(recording, JustBeforeTaxi);
        CommandResult taxi = engine.SendCommand(Callsign, clearance);
        Assert.True(taxi.Success, $"{clearance} refused: {taxi.Message}");

        AircraftState aircraft = TickUntil(engine, IsHoldingShortOfE, TaxiToHoldLimitSeconds, "holding short of E");
        LogRoute("before CTO", aircraft.Ground.AssignedTaxiRoute);
        return engine;
    }

    private CommandResult SendCto(SimulationEngine engine, string ctoCommand)
    {
        CommandResult cto = engine.SendCommand(Callsign, ctoCommand);
        output.WriteLine($"[{ctoCommand}] success={cto.Success} message={cto.Message}");
        output.WriteLine($"[{ctoCommand}] readback terminal={cto.PilotReadback?.Speech.Terminal} spoken={cto.PilotReadback?.Speech.Tts}");
        Assert.True(cto.Success, $"{ctoCommand} refused: {cto.Message}");
        return cto;
    }

    /// <summary>The route the CTO installed drives E and ends at 28R's bar on E; returns the bar's position.</summary>
    private LatLon AssertRouteEndsAt28RBarOnE(SimulationEngine engine)
    {
        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        LogRoute("after CTO", route);
        Assert.NotNull(route);
        AirportGroundLayout? layout = aircraft.Ground.Layout;
        Assert.NotNull(layout);

        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase));
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.DestinationRunway);
        Assert.Equal("E", RunwayEntryPoint.Resolve(layout, bar.NodeId, "28R", null));
        return layout.Nodes[bar.NodeId].Position;
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
        return null!;
    }

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280, elevationFt: 6, thresholdLat: 37.727, thresholdLon: -122.218);

    /// <summary>
    /// A C172 held short of an explicit hold short on E, on a route that goes on to 28R's hold short, with no ground layout: the
    /// implicit <c>TAXI E RWY 28R</c> cannot be resolved.
    /// </summary>
    private static (AircraftState Aircraft, HoldingShortPhase Holding) HeldShortOfEWithoutLayout(RunwayInfo? assignedRunway)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Route = "" },
            Phases = new PhaseList { AssignedRunway = assignedRunway },
        };
        var holdE = new HoldShortPoint
        {
            NodeId = 10,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "E",
        };
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                holdE,
                new HoldShortPoint
                {
                    NodeId = 11,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R",
                },
            ],
        };
        var holding = new HoldingShortPhase(holdE, HoldShortArrival.HeldShort);
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return (ac, holding);
    }

    private CommandResult DispatchCto(AircraftState ac)
    {
        CommandResult result = CommandDispatcher.Dispatch(
            new ClearedForTakeoffCommand(new DefaultDeparture()),
            ac,
            TestDispatch.Context(Random.Shared)
        );
        output.WriteLine($"[CTO] success={result.Success} message={result.Message}");
        output.WriteLine($"[CTO] readback terminal={result.PilotReadback?.Speech.Terminal} spoken={result.PilotReadback?.Speech.Tts}");
        return result;
    }

    /// <summary>Ticks the aircraft 10 s with no layout and asserts it is still in <paramref name="holding"/>.</summary>
    private static void AssertStillHeldAfterTenSeconds(AircraftState ac, HoldingShortPhase holding, RunwayInfo runway)
    {
        for (int i = 0; i < 10; i++)
        {
            var ctx = new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategorization.Categorize(ac.AircraftType),
                DeltaSeconds = 1.0,
                Runway = runway,
                FieldElevation = runway.ElevationFt,
                GroundLayout = null,
                Logger = NullLogger.Instance,
            };
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.Same(holding, ac.Phases!.CurrentPhase);
    }

    /// <summary>
    /// A CTO at a taxiway hold short whose implicit taxi to the assigned runway cannot be resolved (here: no ground layout,
    /// so <c>TAXI E RWY 28R</c> fails) keeps the aircraft held, stores the takeoff clearance on its route for the runway
    /// it already goes on to, and reads back where it holds: "cleared for takeoff runway 28R, holding short of E".
    /// </summary>
    [Fact]
    public void ImplicitTaxiUnresolvable_Cto_KeepsTheHold_ReadsBackHoldingShort()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy28R = Oak28R();
        (AircraftState ac, HoldingShortPhase holding) = HeldShortOfEWithoutLayout(rwy28R);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R));

        CommandResult result = DispatchCto(ac);

        Assert.True(result.Success, $"CTO refused: {result.Message}");
        Assert.Equal("cleared for takeoff runway 28R, holding short of E", result.PilotReadback?.Speech.Terminal);
        Assert.Equal("cleared for takeoff runway two eight right, holding short of echo", result.PilotReadback?.Speech.Tts);
        Assert.NotNull(ac.Phases!.DepartureClearance);
        Assert.Equal(ClearanceType.ClearedForTakeoff, ac.Phases.DepartureClearance!.Type);
        AssertStillHeldAfterTenSeconds(ac, holding, rwy28R);
    }

    /// <summary>
    /// A LUAW at a taxiway hold short whose implicit taxi to the assigned runway cannot be resolved keeps the aircraft held as a CTO
    /// there does, stores the line-up on its route, and reads back where it holds: "line up and wait runway 28R, holding short of E".
    /// </summary>
    [Fact]
    public void ImplicitTaxiUnresolvable_Luaw_KeepsTheHold_ReadsBackHoldingShort()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy28R = Oak28R();
        (AircraftState ac, HoldingShortPhase holding) = HeldShortOfEWithoutLayout(rwy28R);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R));

        CommandResult result = CommandDispatcher.Dispatch(new LineUpAndWaitCommand(), ac, TestDispatch.Context(Random.Shared));
        output.WriteLine($"[LUAW] success={result.Success} message={result.Message}");
        output.WriteLine($"[LUAW] readback terminal={result.PilotReadback?.Speech.Terminal} spoken={result.PilotReadback?.Speech.Tts}");

        Assert.True(result.Success, $"LUAW refused: {result.Message}");
        Assert.Equal("line up and wait runway 28R, holding short of E", result.PilotReadback?.Speech.Terminal);
        Assert.Equal("line up and wait runway two eight right, holding short of echo", result.PilotReadback?.Speech.Tts);
        Assert.Equal(ClearanceType.LineUpAndWait, ac.Phases!.DepartureClearance?.Type);
        Assert.Null(ac.Phases.LineUpIntersectionTaxiway);
        AssertStillHeldAfterTenSeconds(ac, holding, rwy28R);
    }

    /// <summary>
    /// A "without delay" LUAW at a taxiway hold short whose implicit taxi cannot be resolved leaves the aircraft standing, so its
    /// readback drops "without delay": "line up and wait runway 28R, holding short of E".
    /// </summary>
    [Fact]
    public void ImplicitTaxiUnresolvable_LuawWithoutDelay_DropsWithoutDelay_ReadsBackHoldingShort()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy28R = Oak28R();
        AircraftState ac = HeldShortOfEWithoutLayout(rwy28R).Aircraft;
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R));

        CommandResult result = CommandDispatcher.Dispatch(new LineUpAndWaitCommand { WithoutDelay = true }, ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"LUAW WD refused: {result.Message}");
        Assert.Equal("line up and wait runway 28R, holding short of E", result.PilotReadback?.Speech.Terminal);
        Assert.Equal("line up and wait runway two eight right, holding short of echo", result.PilotReadback?.Speech.Tts);
    }

    /// <summary>
    /// A CTO at a taxiway hold short with no runway assigned makes no implicit taxi: the clearance is stored for the runway the
    /// route goes on to and the aircraft stays held short of E.
    /// </summary>
    [Fact]
    public void NoAssignedRunway_Cto_KeepsTheHold()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy28R = Oak28R();
        (AircraftState ac, HoldingShortPhase holding) = HeldShortOfEWithoutLayout(assignedRunway: null);
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(rwy28R));

        CommandResult result = DispatchCto(ac);

        Assert.True(result.Success, $"CTO refused: {result.Message}");
        Assert.Equal("cleared for takeoff runway 28R, holding short of E", result.PilotReadback?.Speech.Terminal);
        AssertStillHeldAfterTenSeconds(ac, holding, rwy28R);
    }

    /// <summary>
    /// Held short of E with no ground layout, <c>CTO 33</c> names a runway whose implicit taxi cannot be judged: the pilot queries
    /// the runway, and nothing changes: no clearance is stored, the assigned runway stays 28R and the aircraft stays held.
    /// </summary>
    [Fact]
    public void HeldShortOfE_NoLayout_Cto33_ConfirmsRunway()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(NavigationDatabase.Instance.GetRunway("OAK", "33"));
        RunwayInfo rwy28R = Oak28R();
        (AircraftState ac, HoldingShortPhase holding) = HeldShortOfEWithoutLayout(rwy28R);
        string? departureRunwayBefore = ac.Procedure.DepartureRunway;

        CommandResult result = CommandDispatcher.Dispatch(
            new ClearedForTakeoffCommand(new DefaultDeparture()) { Runway = "33" },
            ac,
            TestDispatch.Context(Random.Shared)
        );
        output.WriteLine($"[CTO 33] success={result.Success} message={result.Message} spoken={result.PilotUnable?.Tts}");

        Assert.False(result.Success, $"CTO 33 without a layout was accepted: {result.Message}");
        Assert.Equal("confirm runway 33? we're holding short of E for 28R", result.PilotUnable?.Terminal);
        Assert.Null(ac.Phases!.DepartureClearance);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
        Assert.Equal(departureRunwayBefore, ac.Procedure.DepartureRunway);
        AssertStillHeldAfterTenSeconds(ac, holding, rwy28R);
    }

    // ---- CTO {runway}: a takeoff clearance naming a runway (7110.65 3-9-10.d/.e) ----

    /// <summary>Hang guard for a CTO naming another runway to reach its takeoff roll, in simulated seconds.</summary>
    private const int NamedRunwayTakeoffLimitSeconds = 300;

    /// <summary>How far (ft) an aircraft that stays held may drift from where it stood when the clearance was refused.</summary>
    private const double StillStandingFt = 1.0;

    /// <summary>
    /// Held short of K on D, 33's hold line is reached along K without crossing another runway: <c>CTO 33</c> is an intersection
    /// departure from 33 at K. The runway becomes the assigned and departure runway, and the pilot reads it back in full.
    /// </summary>
    [Fact]
    public void HeldShortOfK_Cto33_DepartsRunway33AtK()
    {
        SimulationEngine engine = EngineHoldingShortOf("K");

        CommandResult cto = SendCto(engine, "CTO 33");

        Assert.StartsWith("cleared for takeoff runway 33 at K", cto.PilotReadback?.Speech.Terminal ?? "");
        Assert.Contains("cleared for takeoff runway three three at kilo", cto.PilotReadback?.Speech.Tts ?? "");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        Assert.Equal("33", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal("33", aircraft.Procedure.DepartureRunway);
        AircraftState rolling = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, NamedRunwayTakeoffLimitSeconds, "in TakeoffPhase");
        Assert.Equal("33", rolling.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// Held short of K on D with a takeoff clearance for 28R stored on its route, <c>CTO 33</c> departs 33 at K, a runway
    /// change: the controller is warned that the takeoff clearance issued for 28R is void.
    /// </summary>
    [Fact]
    public void HeldShortOfK_TakeoffClearanceStoredFor28R_Cto33_WarnsOfTheRunwayChange()
    {
        SimulationEngine engine = EngineHoldingShortOf("K");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        Assert.Equal("28R", aircraft.Procedure.DepartureRunway);
        aircraft.Phases!.DepartureClearance = new DepartureClearanceInfo
        {
            Type = ClearanceType.ClearedForTakeoff,
            Departure = new DefaultDeparture(),
        };
        aircraft.PendingWarnings.Clear();

        SendCto(engine, "CTO 33");

        AircraftState after = engine.FindAircraft(Callsign)!;
        output.WriteLine($"[CTO 33] warnings=[{string.Join(" | ", after.PendingWarnings)}]");
        Assert.Equal("33", after.Procedure.DepartureRunway);
        Assert.Contains(
            after.PendingWarnings,
            w =>
                w.Contains("names runway 33 but the departure runway assigned is 28R", StringComparison.Ordinal)
                && w.Contains("the takeoff clearance issued for 28R is void", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Held short of J on D, 28L's hold line on J lies beyond 28R: the clearance does not include that crossing, so the pilot says
    /// unable, naming the runway to cross, and stays held short of J with the assigned runway and route unchanged.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cto28L_NeedsToCross28R_SaysUnable_KeepsTheHold()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        CommandResult result = AssertRefusedAndStillHeld(engine, "J", "CTO 28L");

        Assert.Equal("unable, need to cross runway 28R, holding short of J", result.PilotUnable!.Terminal);
        Assert.Contains("unable, need to cross runway two eight right, holding short of juliett", result.PilotUnable.Tts);
    }

    /// <summary>
    /// Held short of E on C, E reaches no runway but 28R/10L: <c>CTO 33</c> draws the pilot's query instead of a takeoff, and
    /// nothing changes.
    /// </summary>
    [Fact]
    public void HeldShortOfE_Cto33_NeverReaches_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        Assert.Null(ImplicitRoute(engine, "E", "33"));

        CommandResult result = AssertRefusedAndStillHeld(engine, "E", "CTO 33");

        Assert.Equal("confirm runway 33? we're holding short of E for 28R", result.PilotUnable!.Terminal);
        Assert.Contains("confirm runway three three? we're holding short of echo for two eight right", result.PilotUnable.Tts);
    }

    /// <summary>Held short of E on C, <c>CTO 28L</c> draws the same query: E does not reach 28L.</summary>
    [Fact]
    public void HeldShortOfE_Cto28L_NeverReaches_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        Assert.Null(ImplicitRoute(engine, "E", "28L"));

        CommandResult result = AssertRefusedAndStillHeld(engine, "E", "CTO 28L");

        Assert.Contains("confirm runway two eight left? we're holding short of echo for two eight right", result.PilotUnable!.Tts);
    }

    /// <summary>
    /// Held short of J on D, the implicit taxi to 33 resolves but never reaches 33's hold line (it stops where the route needs C): the
    /// pilot queries the runway and the aircraft does not leave the hold.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cto33_RouteNeverReaches_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        TaxiRoute? route = ImplicitRoute(engine, "J", "33");
        LogRoute("J to 33", route);
        Assert.NotNull(route);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.DestinationRunway);

        CommandResult result = AssertRefusedAndStillHeld(engine, "J", "CTO 33");

        Assert.Contains("confirm runway three three? we're holding short of juliett for two eight right", result.PilotUnable!.Tts);
    }

    /// <summary>
    /// Lined up on 28R at E, <c>CTO 28L</c> names a runway the aircraft is not on: the pilot queries it and no clearance is
    /// stored, so the aircraft stays lined up and waiting.
    /// </summary>
    [Fact]
    public void LinedUpOn28R_Cto28L_ConfirmsRunway_StoresNothing()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        LineUpAtE(engine);

        CommandResult result = engine.SendCommand(Callsign, "CTO 28L");
        output.WriteLine($"[CTO 28L] success={result.Success} message={result.Message} spoken={result.PilotUnable?.Tts}");

        Assert.False(result.Success, $"CTO 28L lined up on 28R was accepted: {result.Message}");
        Assert.Equal("confirm runway 28L? we're lined up on runway 28R", result.PilotUnable?.Terminal);
        Assert.Contains("confirm runway two eight left? we're lined up on runway two eight right", result.PilotUnable?.Tts ?? "");
        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.IsType<LinedUpAndWaitingPhase>(after.Phases?.CurrentPhase);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Lined up on 28R at E, <c>CTO 28R</c> names the runway it is on: the clearance a bare CTO there is.</summary>
    [Fact]
    public void LinedUpOn28R_Cto28R_IsABareCto()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        LineUpAtE(engine);

        CommandResult cto = SendCto(engine, "CTO 28R");

        Assert.Contains("cleared for takeoff runway two eight right at echo", AppliedCtoReadback(engine, cto).Tts);
        TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, CtoToTakeoffLimitSeconds, "in TakeoffPhase");
    }

    /// <summary>
    /// Held short of 28R at the end of <c>TAXI D C E RWY 28R</c>, <c>CTO 28L</c> draws the pilot's query naming the runway it
    /// holds short of, and the aircraft stays held.
    /// </summary>
    [Fact]
    public void HoldingShortOf28R_Cto28L_ConfirmsRunway_KeepsTheHold()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D C E RWY 28R");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        CommandResult result = engine.SendCommand(Callsign, "CTO 28L");
        output.WriteLine($"[CTO 28L] success={result.Success} message={result.Message} spoken={result.PilotUnable?.Tts}");

        Assert.False(result.Success, $"CTO 28L held short of 28R was accepted: {result.Message}");
        Assert.Contains("confirm runway two eight left? we're holding short of runway two eight right", result.PilotUnable?.Tts ?? "");
        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Null(after.Phases?.DepartureClearance);
    }

    /// <summary>Held short of 28R, <c>CTO 28R</c> is the clearance a bare CTO there is.</summary>
    [Fact]
    public void HoldingShortOf28R_Cto28R_IsABareCto()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D C E RWY 28R");

        SendCto(engine, "CTO 28R");

        Assert.Equal("28R", engine.FindAircraft(Callsign)!.Phases?.AssignedRunway?.Designator);
        TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, CtoToTakeoffLimitSeconds, "in TakeoffPhase");
    }

    /// <summary>
    /// Sends <paramref name="command"/> and asserts the pilot refused it through the unable channel, then that over
    /// <see cref="UnableHoldCheckSeconds"/> the aircraft stays held short of <paramref name="taxiway"/> where it stood, with its assigned
    /// runway, route and (absent) departure clearance unchanged.
    /// </summary>
    private CommandResult AssertRefusedAndStillHeld(SimulationEngine engine, string taxiway, string command)
    {
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        LatLon positionBefore = aircraft.Position;
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"[{command}] success={result.Success} message={result.Message}");
        output.WriteLine($"[{command}] unable terminal={result.PilotUnable?.Terminal} spoken={result.PilotUnable?.Tts}");
        Assert.False(result.Success, $"{command} held short of {taxiway} was accepted: {result.Message}");
        Assert.NotNull(result.PilotUnable);
        Assert.Equal(result.PilotUnable.Terminal, result.Message);

        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.True(IsHoldingShortOf(after, taxiway), $"{Callsign} is no longer holding short of {taxiway}");
        Assert.Same(routeBefore, after.Ground.AssignedTaxiRoute);
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", after.Procedure.DepartureRunway);
        double movedFt = GeoMath.DistanceNm(positionBefore, after.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt < StillStandingFt, $"{Callsign} moved {movedFt:F1} ft from the hold short of {taxiway}");
        return result;
    }

    private static bool IsHoldingShortOf(AircraftState aircraft, string taxiway) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } target }
        && string.Equals(target, taxiway, StringComparison.OrdinalIgnoreCase);

    /// <summary>Replays to the taxi, clears N45912 <c>TAXI D C HS {taxiway} RWY 28R</c>, ticks it to that hold short and deletes N314WN.</summary>
    private SimulationEngine EngineHoldingShortOf(string taxiway)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(recording);
        Assert.NotNull(engine);
        engine.Replay(recording, JustBeforeTaxi);
        string clearance = $"TAXI D C HS {taxiway} RWY 28R";
        CommandResult taxi = engine.SendCommand(Callsign, clearance);
        Assert.True(taxi.Success, $"{clearance} refused: {taxi.Message}");
        TickUntil(engine, a => IsHoldingShortOf(a, taxiway), TaxiToHoldLimitSeconds, $"holding short of {taxiway}");
        Assert.True(engine.SendCommand(ExitingTrafficOnE, "DEL").Success);
        return engine;
    }

    /// <summary>Replays to the taxi, clears N45912 with <paramref name="clearance"/>, deletes N314WN and ticks it to its runway hold short.</summary>
    private SimulationEngine EngineHoldingShortOfRunway(string clearance) => EngineHoldingShortOfRunway(clearance, HoldShortReason.DestinationRunway);

    /// <summary>
    /// Replays to the taxi, deletes N314WN, clears N45912 with <paramref name="clearance"/> and ticks it to its first runway hold short
    /// held for <paramref name="reason"/>.
    /// </summary>
    private SimulationEngine EngineHoldingShortOfRunway(string clearance, HoldShortReason reason)
    {
        SimulationEngine engine = EngineTaxiing(clearance);
        TickUntil(
            engine,
            a => a.Phases?.CurrentPhase is HoldingShortPhase holding && (holding.HoldShort.Reason == reason),
            TaxiToHoldLimitSeconds,
            $"holding short ({reason})"
        );
        return engine;
    }

    /// <summary>Replays to the taxi, deletes N314WN and clears N45912 with <paramref name="clearance"/>.</summary>
    private SimulationEngine EngineTaxiing(string clearance)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        Assert.NotNull(recording);
        Assert.NotNull(engine);
        engine.Replay(recording, JustBeforeTaxi);
        Assert.True(engine.SendCommand(ExitingTrafficOnE, "DEL").Success);
        CommandResult taxi = engine.SendCommand(Callsign, clearance);
        Assert.True(taxi.Success, $"{clearance} refused: {taxi.Message}");
        return engine;
    }

    /// <summary>Sends <paramref name="command"/>, asserts the pilot refused it with <paramref name="spoken"/>, and returns the result.</summary>
    private CommandResult AssertRefusedSaying(SimulationEngine engine, string command, string spoken)
    {
        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"[{command}] success={result.Success} message={result.Message} spoken={result.PilotUnable?.Tts}");
        Assert.False(result.Success, $"{command} was accepted: {result.Message}");
        Assert.Contains(spoken, result.PilotUnable?.Tts ?? "");
        return result;
    }

    private void TickUnableHoldCheck(SimulationEngine engine)
    {
        for (int t = 0; t < UnableHoldCheckSeconds; t++)
        {
            engine.TickOneSecond();
        }
    }

    // ---- At the hold short of 28R on J, crossed on the way to 28L (TAXI D J RWY 28L) ----

    /// <summary>Held short of 28R on the way to 28L, <c>CTO 28R</c> departs 28R from J, which becomes the assigned runway.</summary>
    [Fact]
    public void CrossingHoldOf28R_Cto28R_Departs28R()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);

        SendCto(engine, "CTO 28R");

        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", aircraft.Procedure.DepartureRunway);
        AircraftState rolling = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, NamedRunwayTakeoffLimitSeconds, "in TakeoffPhase");
        Assert.Equal("28R", rolling.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Held short of 28R on the way to 28L, <c>CTO 28L</c> needs the crossing of 28R: the pilot says unable, nothing changes.</summary>
    [Fact]
    public void CrossingHoldOf28R_Cto28L_SaysUnableNeedToCross28R()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        CommandResult result = AssertRefusedSaying(engine, "CTO 28L", ", unable, need to cross runway two eight right");

        Assert.Equal("unable, need to cross runway 28R", result.PilotUnable?.Terminal);
        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Equal("28L", after.Phases?.AssignedRunway?.Designator);
        Assert.Null(after.Phases?.DepartureClearance);
    }

    /// <summary>Held short of 28R on the way to 28L, <c>CTO 33</c> names neither: the pilot queries it, nothing changes.</summary>
    [Fact]
    public void CrossingHoldOf28R_Cto33_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        AssertRefusedSaying(engine, "CTO 33", ", confirm runway three three? we're holding short of runway two eight right for two eight left");

        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Equal("28L", after.Phases?.AssignedRunway?.Designator);
    }

    // ---- At the hold short of 28R on J re-armed by the controller (TAXI D J HS 28R RWY 28L) ----

    /// <summary>
    /// Held short of 28R on J, a crossing on the way to 28L that the controller re-armed with <c>HS 28R</c>: <c>CTO 28L</c> needs
    /// that crossing, so the pilot says unable and nothing changes.
    /// </summary>
    [Fact]
    public void ExplicitHsCrossingHoldOf28R_Cto28L_SaysUnableNeedToCross28R()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J HS 28R RWY 28L", HoldShortReason.ExplicitHoldShort);
        HoldingShortPhase holding = AssertHeldShortOfExplicitRunwayHold(engine);

        CommandResult result = AssertRefusedSaying(engine, "CTO 28L", ", unable, need to cross runway two eight right");

        Assert.Equal("unable, need to cross runway 28R", result.PilotUnable?.Terminal);
        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Equal("28L", after.Phases?.AssignedRunway?.Designator);
        Assert.Null(after.Phases?.DepartureClearance);
    }

    /// <summary>
    /// Held short of 28R on J, a crossing on the way to 28L that the controller re-armed with <c>HS 28R</c>: <c>CTO 28R</c> departs
    /// 28R from J, which becomes the assigned runway.
    /// </summary>
    [Fact]
    public void ExplicitHsCrossingHoldOf28R_Cto28R_Departs28R()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J HS 28R RWY 28L", HoldShortReason.ExplicitHoldShort);
        AssertHeldShortOfExplicitRunwayHold(engine);

        SendCto(engine, "CTO 28R");

        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", aircraft.Procedure.DepartureRunway);
        AircraftState rolling = TickUntil(engine, a => a.Phases?.CurrentPhase is TakeoffPhase, NamedRunwayTakeoffLimitSeconds, "in TakeoffPhase");
        Assert.Equal("28R", rolling.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>The hold N45912 stands at: an explicit hold short of the 28R/10L runway (re-armed by <c>HS 28R</c>), not a taxiway.</summary>
    private static HoldingShortPhase AssertHeldShortOfExplicitRunwayHold(SimulationEngine engine)
    {
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(HoldShortReason.ExplicitHoldShort, holding.HoldShort.Reason);
        Assert.Null(GroundCommandHandler.HeldShortTaxiway(aircraft));
        Assert.Contains("28R", holding.HoldShort.TargetName ?? "");
        return holding;
    }

    /// <summary>Lined up on 28R at E, <c>CTO 10L</c> names the other end: a different runway, so the pilot queries it.</summary>
    [Fact]
    public void LinedUpOn28R_Cto10L_ConfirmsRunway_StoresNothing()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        LineUpAtE(engine);

        AssertRefusedSaying(engine, "CTO 10L", "confirm runway one zero left? we're lined up on runway two eight right");

        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.IsType<LinedUpAndWaitingPhase>(after.Phases?.CurrentPhase);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// Held short of J with no runway assigned, <c>CTO 28L</c> needs the crossing of 28R/10L on J: with no active end listed the
    /// pilot names the end parallel to the runway named (28R, within 45° of 28L), not the end nearer the aircraft, and nothing
    /// is assigned.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_NoAssignedRunway_Cto28L_NamesTheParallelEndToCross()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Phases!.AssignedRunway = null;
        Assert.NotEqual("28R", RunwayCrossingEnd.Spoken(aircraft, "28R/10L", aircraft.Ground.Layout, ActiveRunways.Empty));

        AssertRefusedSaying(engine, "CTO 28L", "unable, need to cross runway two eight right, holding short of juliett");

        Assert.Null(engine.FindAircraft(Callsign)!.Phases?.AssignedRunway);
    }

    /// <summary>
    /// Held short of K on D, the implicit taxi to 33 reaches its bar along K with no runway to cross (the reach path), and traffic
    /// stands at rest on K between the aircraft and that bar: <c>CTO 33</c> draws the pilot's unable, and the runway the reach path
    /// assigned for the taxi is restored to 28R.
    /// </summary>
    [Fact]
    public void HeldShortOfK_TrafficOnK_Cto33_SaysUnable_RestoresTheRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("K");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        TaxiRoute? route = ImplicitRoute(engine, "K", "33");
        LogRoute("K to 33", route);
        Assert.NotNull(route);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.RunwayCrossing);
        Assert.Contains(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.DestinationRunway);
        TaxiRouteSegment onK = route.Segments.Last(s => string.Equals(s.TaxiwayName, "K", StringComparison.OrdinalIgnoreCase));
        LatLon from = aircraft.Ground.Layout!.Nodes[onK.FromNodeId].Position;
        LatLon to = aircraft.Ground.Layout.Nodes[onK.ToNodeId].Position;
        double alongK = GeoMath.BearingTo(from, to);
        AddTraffic(engine, GeoMath.ProjectPoint(from, new TrueHeading(alongK), GeoMath.DistanceNm(from, to) / 2.0), alongK, TrafficState.AtRest);

        CommandResult result = AssertRefusedSaying(engine, "CTO 33", ", unable, traffic on kilo");

        Assert.Equal("unable, traffic on K", result.PilotUnable?.Terminal);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", after.Procedure.DepartureRunway);
        Assert.True(IsHoldingShortOf(after, "K"));
    }

    /// <summary>Taxiing to 28R, <c>CTO 28L</c> draws the pilot's query naming the runway it taxis to, and nothing is stored.</summary>
    [Fact]
    public void TaxiingTo28R_Cto28L_ConfirmsRunway()
    {
        SimulationEngine engine = EngineTaxiing("TAXI D C HS E RWY 28R");
        TickUntil(engine, a => a.Phases?.CurrentPhase is TaxiingPhase, TaxiToHoldLimitSeconds, "taxiing");

        AssertRefusedSaying(engine, "CTO 28L", "confirm runway two eight left? we're taxiing to runway two eight right");

        Assert.Null(engine.FindAircraft(Callsign)!.Phases?.DepartureClearance);
    }

    private static TaxiRoute? ImplicitRoute(SimulationEngine engine, string taxiway, string runway)
    {
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        return GroundCommandHandler.ResolveTaxiRoute(
            aircraft,
            new TaxiCommand([taxiway], [], Expedite: false, DestinationRunway: runway),
            aircraft.Ground.Layout!,
            TestDispatch.EmptyTaxi with
            {
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            }
        );
    }

    // ---- LUAW {runway}: a line-up clearance naming a runway (7110.65 3-9-4) ----

    /// <summary>
    /// Held short of J on D, <c>LUAW 28L</c> needs the crossing of 28R the clearance does not include: the pilot says unable, naming
    /// the runway to cross, and nothing changes.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Luaw28L_NeedsToCross28R_SaysUnable_KeepsTheHold()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        CommandResult result = AssertRefusedAndStillHeld(engine, "J", "LUAW 28L");

        Assert.Equal("unable, need to cross runway 28R, holding short of J", result.PilotUnable!.Terminal);
        Assert.Contains("unable, need to cross runway two eight right, holding short of juliett", result.PilotUnable.Tts);
    }

    /// <summary>
    /// Held short of J with no runway assigned, <c>LUAW 28L</c> is judged at the taxiway hold as a named runway is: the pilot says
    /// unable for the crossing on J, and nothing is assigned.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_NoAssignedRunway_Luaw28L_SaysUnable()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Phases!.AssignedRunway = null;

        AssertRefusedSaying(engine, "LUAW 28L", "unable, need to cross runway two eight right, holding short of juliett");

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Null(after.Phases?.AssignedRunway);
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.True(IsHoldingShortOf(after, "J"));
    }

    /// <summary>
    /// Held short of E on C, <c>LUAW 28R</c> names the assigned runway: the intersection line-up a bare LUAW there is, read back
    /// "line up and wait runway 28R at E", lined up on 28R at E.
    /// </summary>
    [Fact]
    public void HeldShortOfE_Luaw28R_LinesUpOn28RAtE()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");

        CommandResult luaw = SendCto(engine, "LUAW 28R");

        Assert.StartsWith("line up and wait runway 28R at E", luaw.PilotReadback?.Speech.Terminal ?? "");
        Assert.Contains("line up and wait runway two eight right at echo", luaw.PilotReadback?.Speech.Tts ?? "");
        AircraftState aircraft = TickUntil(engine, a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase, CtoToTakeoffLimitSeconds, "lined up");
        Assert.Equal("E", aircraft.Phases?.LineUpIntersectionTaxiway);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// Held short of K on D with a takeoff clearance for 28R stored on its route, <c>LUAW 33</c> reaches 33's hold line along K: the
    /// line-up is on 33 at K, which becomes the assigned and departure runway, and the controller is warned of the runway change.
    /// </summary>
    [Fact]
    public void HeldShortOfK_Luaw33_LinesUpOn33AtK_WarnsOfTheRunwayChange()
    {
        SimulationEngine engine = EngineHoldingShortOf("K");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Phases!.DepartureClearance = new DepartureClearanceInfo
        {
            Type = ClearanceType.ClearedForTakeoff,
            Departure = new DefaultDeparture(),
        };
        aircraft.PendingWarnings.Clear();

        CommandResult luaw = SendCto(engine, "LUAW 33");

        Assert.StartsWith("line up and wait runway 33 at K", luaw.PilotReadback?.Speech.Terminal ?? "");
        AircraftState after = engine.FindAircraft(Callsign)!;
        output.WriteLine($"[LUAW 33] warnings=[{string.Join(" | ", after.PendingWarnings)}]");
        Assert.Equal("33", after.Phases?.AssignedRunway?.Designator);
        Assert.Equal("33", after.Procedure.DepartureRunway);
        Assert.Contains(after.PendingWarnings, w => w.Contains("names runway 33 but the departure runway assigned is 28R", StringComparison.Ordinal));
        AircraftState linedUp = TickUntil(engine, a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase, CtoToTakeoffLimitSeconds, "lined up");
        Assert.Equal("33", linedUp.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Held short of E on C, <c>LUAW 33</c> names a runway E never reaches: the pilot queries it, and nothing changes.</summary>
    [Fact]
    public void HeldShortOfE_Luaw33_NeverReaches_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");

        CommandResult result = AssertRefusedAndStillHeld(engine, "E", "LUAW 33");

        Assert.Equal("confirm runway 33? we're holding short of E for 28R", result.PilotUnable!.Terminal);
    }

    /// <summary>Lined up on 28R at E, <c>LUAW 28R</c> is refused as a bare LUAW there is: the aircraft is already lined up.</summary>
    [Fact]
    public void LinedUpOn28R_Luaw28R_IsRefusedAsABareLuaw()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        LineUpAtE(engine);

        CommandResult named = engine.SendCommand(Callsign, "LUAW 28R");
        CommandResult bare = engine.SendCommand(Callsign, "LUAW");
        output.WriteLine($"[LUAW 28R] success={named.Success} message={named.Message}");

        Assert.False(named.Success, $"LUAW 28R lined up on 28R was accepted: {named.Message}");
        Assert.Equal("Line up and wait requires aircraft to be taxiing or holding short", named.Message);
        Assert.Equal(bare.Message, named.Message);
        Assert.IsType<LinedUpAndWaitingPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);
    }

    /// <summary>Lined up on 28R at E, <c>LUAW 28L</c> names a runway the aircraft is not on: the pilot queries it.</summary>
    [Fact]
    public void LinedUpOn28R_Luaw28L_ConfirmsRunway()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");
        LineUpAtE(engine);

        CommandResult result = AssertRefusedSaying(engine, "LUAW 28L", "confirm runway two eight left? we're lined up on runway two eight right");

        Assert.Equal("confirm runway 28L? we're lined up on runway 28R", result.PilotUnable?.Terminal);
        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.IsType<LinedUpAndWaitingPhase>(after.Phases?.CurrentPhase);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Held short of 28R, <c>LUAW 28L</c> draws the pilot's query naming the runway it holds short of, and it stays held.</summary>
    [Fact]
    public void HoldingShortOf28R_Luaw28L_ConfirmsRunway_KeepsTheHold()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D C E RWY 28R");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        AssertRefusedSaying(engine, "LUAW 28L", "confirm runway two eight left? we're holding short of runway two eight right");

        TickUnableHoldCheck(engine);
        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Null(after.Phases?.DepartureClearance);
    }

    /// <summary>Held short of 28R, <c>LUAW 28R</c> is the line-up a bare LUAW there is.</summary>
    [Fact]
    public void HoldingShortOf28R_Luaw28R_LinesUp()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D C E RWY 28R");

        SendCto(engine, "LUAW 28R");

        AircraftState linedUp = TickUntil(engine, a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase, CtoToTakeoffLimitSeconds, "lined up");
        Assert.Equal("28R", linedUp.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Taxiing to 28R, <c>LUAW 28L</c> draws the pilot's query naming the runway it taxis to, and nothing is stored.</summary>
    [Fact]
    public void TaxiingTo28R_Luaw28L_ConfirmsRunway()
    {
        SimulationEngine engine = EngineTaxiing("TAXI D C HS E RWY 28R");
        TickUntil(engine, a => a.Phases?.CurrentPhase is TaxiingPhase, TaxiToHoldLimitSeconds, "taxiing");

        AssertRefusedSaying(engine, "LUAW 28L", "confirm runway two eight left? we're taxiing to runway two eight right");

        Assert.Null(engine.FindAircraft(Callsign)!.Phases?.DepartureClearance);
    }

    /// <summary>Taxiing to 28R's hold short on E, <c>LUAW 28R</c> is stored on the route as a bare LUAW is.</summary>
    [Fact]
    public void TaxiingTo28R_Luaw28R_StoresTheLineUp()
    {
        SimulationEngine engine = EngineTaxiing("TAXI D C E RWY 28R");
        TickUntil(engine, a => a.Phases?.CurrentPhase is TaxiingPhase, TaxiToHoldLimitSeconds, "taxiing");

        SendCto(engine, "LUAW 28R");

        Assert.Equal(ClearanceType.LineUpAndWait, engine.FindAircraft(Callsign)!.Phases?.DepartureClearance?.Type);
    }

    // ---- CROSS {runway} with a takeoff or line-up clearance in the same transmission, at a taxiway hold short ----

    /// <summary>
    /// Held short of J on D, <c>CROSS 28R; CTO 28L</c>: the CROSS installs nothing and the CTO, fired from the queue on the next
    /// tick, carries the crossing into its implicit taxi along J, which crosses 28R cleared and departs 28L from J.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_Cross28RThenCto28L_Departs28LViaJ()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        SendCto(engine, "CROSS 28R; CTO 28L");

        AircraftState sent = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, sent.Phases?.CurrentPhase);
        Assert.Null(sent.Phases?.DepartureClearance);
        engine.TickOneSecond();
        AssertImplicitRouteTo28LCrosses28RCleared(engine);
        AircraftState airborne = TickUntil(engine, a => !a.IsOnGround, CtoToTakeoffLimitSeconds, "airborne");
        Assert.Equal("28L", airborne.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Held short of J on D, <c>CROSS 28R, CTO 28L</c> in one block departs 28L via J as the sequenced form does.</summary>
    [Fact]
    public void HeldShortOfJ_Cross28RWithCto28L_Departs28LViaJ()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        SendCto(engine, "CROSS 28R, CTO 28L");

        AssertImplicitRouteTo28LCrosses28RCleared(engine);
        AircraftState airborne = TickUntil(engine, a => !a.IsOnGround, CtoToTakeoffLimitSeconds, "airborne");
        Assert.Equal("28L", airborne.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>Held short of J on D, <c>CROSS 28R; LUAW 28L</c> crosses 28R on J and lines up on 28L at J.</summary>
    [Fact]
    public void HeldShortOfJ_Cross28RThenLuaw28L_LinesUpOn28LAtJ()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        SendCto(engine, "CROSS 28R; LUAW 28L");

        engine.TickOneSecond();
        AssertImplicitRouteTo28LCrosses28RCleared(engine);
        AircraftState linedUp = TickUntil(engine, a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase, CtoToTakeoffLimitSeconds, "lined up");
        Assert.Equal("28L", linedUp.Phases?.AssignedRunway?.Designator);
        Assert.Equal("J", linedUp.Phases?.LineUpIntersectionTaxiway);
    }

    /// <summary>Held short of J on D, <c>CROSS 28L; CTO 28L</c> names the takeoff runway as a crossing: refused whole, nothing applied.</summary>
    [Fact]
    public void HeldShortOfJ_Cross28LThenCto28L_RefusedWhole()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        CommandResult result = AssertTransmissionRefusedAndStillHeld(engine, "J", "CROSS 28L; CTO 28L");

        Assert.Equal("CROSS 28L: 28L is the runway of the takeoff clearance", result.Message);
    }

    /// <summary>Held short of J on D, <c>CROSS 33; CTO 28L</c> names a runway the route to 28L never crosses: refused whole.</summary>
    [Fact]
    public void HeldShortOfJ_Cross33ThenCto28L_RefusedWhole()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");

        CommandResult result = AssertTransmissionRefusedAndStillHeld(engine, "J", "CROSS 33; CTO 28L");

        Assert.Equal("CROSS 33 not applied — the route crosses no runway 33", result.Message);
    }

    /// <summary>
    /// Held short of J on D with no runway assigned (the assignment its taxi made removed), <c>CROSS 33; CTO</c>: the clearance has no
    /// runway, so no implicit taxi carries the crossing, and the transmission is refused whole, nothing applied.
    /// </summary>
    [Fact]
    public void HeldShortOfJ_NoRunway_Cross33ThenCto_RefusedWhole()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Phases!.AssignedRunway = null;
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        int queuedBefore = aircraft.Queue.Blocks.Count;

        CommandResult result = engine.SendCommand(Callsign, "CROSS 33; CTO");
        output.WriteLine($"[CROSS 33; CTO] success={result.Success} message={result.Message}");
        Assert.False(result.Success, $"CROSS 33; CTO with no runway was accepted: {result.Message}");
        Assert.Equal("CROSS 33: no departure route to carry the crossing", result.Message);
        TickUnableHoldCheck(engine);

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.Equal(queuedBefore, after.Queue.Blocks.Count);
        Assert.Null(after.Phases?.DepartureClearance);
    }

    /// <summary>
    /// Held short of E on C, <c>CROSS 28R; CTO 28L</c>: E never reaches 28L, so the implicit taxi to it does not resolve and nothing
    /// carries the crossing; the transmission is refused whole, nothing applied.
    /// </summary>
    [Fact]
    public void HeldShortOfE_Cross28RThenCto28L_NoImplicitTaxi_RefusedWhole()
    {
        SimulationEngine engine = EngineHoldingShortOf("E");

        CommandResult result = AssertTransmissionRefusedAndStillHeld(engine, "E", "CROSS 28R; CTO 28L");

        Assert.Equal("CROSS 28R: no departure route to carry the crossing", result.Message);
    }

    /// <summary>
    /// Held short of J on D and held for release, <c>CROSS 28R; CTO 28L</c>: the CTO waits in the queue for the release, carrying
    /// the crossing through a snapshot of its block, and once released departs 28L via J across 28R.
    /// </summary>
    [Fact]
    public void HeldForRelease_HeldShortOfJ_Cross28RThenCto28L_WaitsThenDepartsAcross28R()
    {
        SimulationEngine engine = EngineHoldingShortOf("J");
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        aircraft.Ground.HeldForRelease = true;

        SendCto(engine, "CROSS 28R; CTO 28L");
        TickUnableHoldCheck(engine);

        AircraftState waiting = engine.FindAircraft(Callsign)!;
        Assert.True(IsHoldingShortOf(waiting, "J"), $"{Callsign} left the hold short of J while held for release");
        CommandBlock queued = Assert.Single(waiting.Queue.Blocks, b => !b.IsApplied);
        Assert.True(queued.WaitingForRelease);
        Assert.Equal("28R", Assert.Single(queued.CrossingsClearedInTransmission ?? []));
        var restored = CommandBlock.FromSnapshot(JsonSerializer.Deserialize<CommandBlockDto>(JsonSerializer.Serialize(queued.ToSnapshot()))!);
        Assert.Equal("28R", Assert.Single(restored.CrossingsClearedInTransmission ?? []));

        Assert.True(engine.SendCommand(Callsign, $"REL {Callsign}").Success);
        TickUntil(
            engine,
            a => a.Ground.AssignedTaxiRoute?.HoldShortPoints.Exists(Is28LDestination) == true,
            CtoToTakeoffLimitSeconds,
            "taxiing for 28L"
        );
        AssertImplicitRouteTo28LCrosses28RCleared(engine);
        AircraftState airborne = TickUntil(engine, a => !a.IsOnGround, CtoToTakeoffLimitSeconds, "airborne");
        Assert.Equal("28L", airborne.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// At the crossing hold of 28R on the way to 28L (<c>TAXI D J RWY 28L</c>), <c>CROSS 28R; CTO 28L</c> crosses 28R and departs
    /// 28L: the CTO fires after the crossing, as part A's crossing hold already did.
    /// </summary>
    [Fact]
    public void CrossingHoldOf28R_Cross28RThenCto28L_Departs28L()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);

        SendCto(engine, "CROSS 28R; CTO 28L");

        AircraftState airborne = TickUntil(engine, a => !a.IsOnGround, CtoToTakeoffLimitSeconds, "airborne");
        Assert.Equal("28L", airborne.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// At the crossing hold of 28R on the way to 28L (<c>TAXI D J RWY 28L</c>), <c>CROSS 28R, CTO 28L</c> in one block, the 7110.65
    /// 3-9-10.e transmission, crosses 28R and departs 28L as the sequenced form does.
    /// </summary>
    [Fact]
    public void CrossingHoldOf28R_Cross28RWithCto28L_Departs28L()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);

        CommandResult cto = SendCto(engine, "CROSS 28R, CTO 28L");

        Assert.Contains("28L", cto.PilotReadback?.Speech.Terminal ?? "");
        Assert.DoesNotContain("cleared for takeoff runway 28R", cto.PilotReadback?.Speech.Terminal ?? "");
        AircraftState airborne = TickUntil(engine, a => !a.IsOnGround, CtoToTakeoffLimitSeconds, "airborne");
        Assert.Equal("28L", airborne.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// At the crossing hold of 28R on the way to 28L (<c>TAXI D J RWY 28L</c>), <c>CROSS 28R, LUAW 28L</c> in one block crosses 28R
    /// and lines up on 28L as the sequenced form does.
    /// </summary>
    [Fact]
    public void CrossingHoldOf28R_Cross28RWithLuaw28L_LinesUpOn28L()
    {
        SimulationEngine engine = EngineHoldingShortOfRunway("TAXI D J RWY 28L", HoldShortReason.RunwayCrossing);

        SendCto(engine, "CROSS 28R, LUAW 28L");

        AircraftState linedUp = TickUntil(engine, a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase, CtoToTakeoffLimitSeconds, "lined up");
        Assert.Equal("28L", linedUp.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// From the ramp, <c>TAXI D C HS J RWY 28R; CROSS 28R; CTO 28L</c> in two identical runs, one of which restores its queued blocks
    /// from their snapshot during the taxi: the restored CROSS, fired at the hold short of J, carries into the CTO as the live one
    /// does, so both runs install the same routes up to the CTO's route to 28L (no J-across-28R route of the CROSS's own before it).
    /// </summary>
    [Fact]
    public void FromTheRamp_TaxiHsJThenCross28RThenCto28L_QueueRestoredDuringTheTaxi_InstallsTheLiveRoute()
    {
        const string Transmission = "TAXI D C HS J RWY 28R; CROSS 28R; CTO 28L";
        SimulationEngine live = EngineTaxiing(Transmission);
        SimulationEngine restored = EngineTaxiing(Transmission);
        TickUnableHoldCheck(live);
        TickUnableHoldCheck(restored);
        RestoreQueuedBlocksFromTheirSnapshot(restored.FindAircraft(Callsign)!);

        List<string> liveRoutes = RoutesUntilTaxiingFor28L(live, "live");
        List<string> restoredRoutes = RoutesUntilTaxiingFor28L(restored, "restored");

        Assert.Equal(liveRoutes, restoredRoutes);
    }

    /// <summary>
    /// Ticks <paramref name="engine"/> until N45912's route holds short of 28L as its destination, and returns each distinct route it
    /// held on the way (<see cref="RouteSignature"/>), the last being the route to 28L.
    /// </summary>
    private List<string> RoutesUntilTaxiingFor28L(SimulationEngine engine, string run)
    {
        List<string> routes = [];
        for (int t = 0; t <= TaxiToHoldLimitSeconds + CtoToTakeoffLimitSeconds; t++)
        {
            AircraftState aircraft = engine.FindAircraft(Callsign)!;
            TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
            string signature = RouteSignature(route);
            if ((routes.Count == 0) || (routes[^1] != signature))
            {
                output.WriteLine($"[{run} {t}s] phase={aircraft.Phases?.CurrentPhase?.Name} queue=[{DescribeQueue(aircraft)}]");
                LogRoute($"{run} route at {t}s", route);
                routes.Add(signature);
            }

            if (route?.HoldShortPoints.Exists(Is28LDestination) == true)
            {
                return routes;
            }

            engine.TickOneSecond();
        }

        AircraftState last = engine.FindAircraft(Callsign)!;
        Assert.Fail(
            $"hung: the {run} run never installed the route to 28L: phase={last.Phases?.CurrentPhase?.Name} ias={last.IndicatedAirspeed:F1} "
                + $"pos=({last.Position.Lat:F6}, {last.Position.Lon:F6}) segment={last.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} "
                + $"queue=[{DescribeQueue(last)}]"
        );
        return routes;
    }

    /// <summary>
    /// Replaces each of <paramref name="aircraft"/>'s queued blocks, none yet applied, by its snapshot round-tripped through JSON, as a
    /// snapshot restore leaves it: the engine rebuilds each one from its serialized facts on the next tick.
    /// </summary>
    private static void RestoreQueuedBlocksFromTheirSnapshot(AircraftState aircraft)
    {
        List<CommandBlock> blocks = aircraft.Queue.Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.False(b.IsApplied, $"{b.Description} already applied before the restore"));
        for (int i = 0; i < blocks.Count; i++)
        {
            string json = JsonSerializer.Serialize(blocks[i].ToSnapshot(), RecordingJsonOptions.Default);
            blocks[i] = CommandBlock.FromSnapshot(JsonSerializer.Deserialize<CommandBlockDto>(json, RecordingJsonOptions.Default)!);
        }
    }

    private static string DescribeQueue(AircraftState aircraft) =>
        string.Join(" | ", aircraft.Queue.Blocks.Select(b => $"{b.Description} applied={b.IsApplied}"));

    /// <summary>The route's taxiways and hold shorts (target, reason, cleared), or "(none)".</summary>
    private static string RouteSignature(TaxiRoute? route) =>
        route is null
            ? "(none)"
            : string.Join(" ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))
                + " | "
                + string.Join(" ", route.HoldShortPoints.Select(hs => $"{hs.TargetName}:{hs.Reason}:{hs.IsCleared}"));

    private static bool Is28LDestination(HoldShortPoint hs) =>
        (hs.Reason == HoldShortReason.DestinationRunway) && (hs.TargetName is { } target) && RunwayIdentifier.Parse(target).Contains("28L");

    /// <summary>The installed route runs along J to 28L's bar, its crossing of 28R cleared and no other crossing cleared.</summary>
    private void AssertImplicitRouteTo28LCrosses28RCleared(SimulationEngine engine)
    {
        TaxiRoute? route = engine.FindAircraft(Callsign)!.Ground.AssignedTaxiRoute;
        LogRoute("implicit taxi to 28L", route);
        Assert.NotNull(route);
        Assert.Contains(route.HoldShortPoints, Is28LDestination);
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "J", StringComparison.OrdinalIgnoreCase));
        List<HoldShortPoint> crossings = route.HoldShortPoints.FindAll(hs => hs.Reason == HoldShortReason.RunwayCrossing);
        HoldShortPoint crossing28R = Assert.Single(crossings);
        Assert.Contains("28R", crossing28R.TargetName ?? "");
        Assert.True(crossing28R.IsCleared, "the crossing of 28R on the way to 28L is not cleared");
    }

    /// <summary>
    /// Sends <paramref name="command"/> and asserts it was refused to the controller with no pilot transmission, then that the
    /// aircraft stays held short of <paramref name="taxiway"/> with its queue, route, runway and (absent) clearance unchanged.
    /// </summary>
    private CommandResult AssertTransmissionRefusedAndStillHeld(SimulationEngine engine, string taxiway, string command)
    {
        AircraftState aircraft = engine.FindAircraft(Callsign)!;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        int queuedBefore = aircraft.Queue.Blocks.Count;

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"[{command}] success={result.Success} message={result.Message}");
        Assert.False(result.Success, $"{command} held short of {taxiway} was accepted: {result.Message}");
        Assert.Null(result.PilotUnable);
        TickUnableHoldCheck(engine);

        AircraftState after = engine.FindAircraft(Callsign)!;
        Assert.Same(holding, after.Phases?.CurrentPhase);
        Assert.True(IsHoldingShortOf(after, taxiway), $"{Callsign} is no longer holding short of {taxiway}");
        Assert.Same(routeBefore, after.Ground.AssignedTaxiRoute);
        Assert.Equal(queuedBefore, after.Queue.Blocks.Count);
        Assert.Null(after.Phases?.DepartureClearance);
        Assert.Equal("28R", after.Phases?.AssignedRunway?.Designator);
        return result;
    }
}
