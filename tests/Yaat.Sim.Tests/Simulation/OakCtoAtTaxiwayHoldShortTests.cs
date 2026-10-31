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
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(compound, cto, aircraft, PilotSpeechContext.Canonical);
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
        var holding = new HoldingShortPhase(holdE);
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
}
