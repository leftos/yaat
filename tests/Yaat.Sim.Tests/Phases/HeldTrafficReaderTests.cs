using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Situation;
using Yaat.Sim.Training;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// Readers that judge another aircraft as traffic (a lead for a follower, a runway user, a generator arrival) read an aircraft
/// held behind a Class B boundary hold by the phase it holds, with no lift: each comes out the same as for an unheld twin
/// built the same way. The readers that show the hold (situation, overlay, status line) keep seeing the hold. Real KOAK 28R
/// right traffic; the hold is the SFO Class B orbit, put in front with <see cref="PhaseList.InsertHoldInFront"/>.
/// </summary>
public sealed class HeldTrafficReaderTests : IDisposable
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string Sfo = "SFO";

    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public HeldTrafficReaderTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    // ─── Follow and traffic readers ───

    [Theory]
    [InlineData(PatternEntryLeg.Downwind)]
    [InlineData(PatternEntryLeg.Base)]
    [InlineData(PatternEntryLeg.Final)]
    public void PatternLegIndex_OfAHeldLead_IsTheUnheldLeadsLeg(PatternEntryLeg leg)
    {
        (AircraftState unheld, AircraftState held) = Twins(() => OnCircuit(Leader, leg, null));

        int? expected = AirborneFollowHelper.PatternLegIndex(unheld);

        Assert.NotNull(expected);
        Assert.Equal(expected, AirborneFollowHelper.PatternLegIndex(held));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GoAroundReaders_OfAHeldGoAround_MatchTheUnheldGoAround(bool reenterPattern)
    {
        (AircraftState unheld, AircraftState held) = Twins(() => GoingAround(Leader, reenterPattern));

        Assert.True(AirborneFollowHelper.IsGoingAround(unheld));
        Assert.True(AirborneFollowHelper.IsGoingAround(held));
        Assert.Equal(AirborneFollowHelper.PatternLegIndex(unheld), AirborneFollowHelper.PatternLegIndex(held));
        if (reenterPattern)
        {
            Assert.NotNull(AirborneFollowHelper.PatternLegIndex(held));
        }
        else
        {
            Assert.Null(AirborneFollowHelper.PatternLegIndex(held));
        }
    }

    [Fact]
    public void IsLeadPatternFlowAhead_HeldExtendedDownwindLead_IsAheadAsUnheld()
    {
        AircraftState follower = OnCircuit(Follower, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        (AircraftState unheld, AircraftState held) = Twins(() => ExtendedDownwindLead());

        Assert.True(AirborneFollowHelper.IsLeadPatternFlowAhead(follower, unheld));
        Assert.True(AirborneFollowHelper.IsLeadPatternFlowAhead(follower, held));
    }

    [Fact]
    public void BaseFollowSpacing_HeldBaseLeadAhead_DecidesAsUnheld()
    {
        AircraftState follower = OnCircuit(Follower, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        follower.Approach.FollowingCallsign = Leader;
        PatternWaypoints waypoints = WaypointsOf(follower);
        (AircraftState unheld, AircraftState held) = Twins(() => OnCircuit(Leader, PatternEntryLeg.Base, wp => IntoBase(wp, 0.8)));

        BaseFollowSpacingDecision expected = BaseFollowSpacing.Evaluate(ContextWithLead(follower, unheld), waypoints, widening: false);
        BaseFollowSpacingDecision actual = BaseFollowSpacing.Evaluate(ContextWithLead(follower, held), waypoints, widening: false);
        _output.WriteLine($"unheld {expected.Action}, held {actual.Action}");

        Assert.NotEqual(BaseFollowSpacingAction.Keep, expected.Action);
        Assert.Equal(expected, actual);
        RunwayInfo rwy = Oak("28R");
        Assert.Equal(AirborneFollowHelper.LeadRemainingPathNm(unheld, rwy), AirborneFollowHelper.LeadRemainingPathNm(held, rwy), 6);
        Assert.Equal(
            BaseFollowSpacing.ProjectedBaseGapNm(ContextWithLead(follower, unheld), unheld, rwy),
            BaseFollowSpacing.ProjectedBaseGapNm(ContextWithLead(follower, held), held, rwy),
            6
        );
    }

    [Fact]
    public void EntrantJoin_HeldEntryJoiningTheDownwind_ReadsTheDownwindJoin()
    {
        AircraftState follower = OnCircuit(Follower, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 0.5));
        PatternWaypoints wp = WaypointsOf(follower);
        LatLon join = GeoMath.ProjectPoint(follower.Position, wp.DownwindHeading.ToReciprocal(), 2.0);
        (AircraftState unheld, AircraftState held) = Twins(() => EntryJoiningDownwind(join));

        Assert.True(AirborneFollowHelper.IsFollowerPastEntrantJoin(follower, unheld));
        Assert.True(AirborneFollowHelper.IsFollowerPastEntrantJoin(follower, held), "a held entry joins the downwind queued after it");
    }

    [Fact]
    public void StraightInEntry_HeldEntryJoiningTheFinal_IsStraightInAsUnheld()
    {
        LatLon join = OffFinal(Oak("28R"), 3.0, 0);
        (AircraftState unheld, AircraftState held) = Twins(() => EntryAhead(PatternEntryLeg.Final, PatternEntryKind.FortyFive, join));

        Assert.True(AirborneFollowHelper.IsStraightInEntry(unheld));
        Assert.True(AirborneFollowHelper.IsStraightInEntry(held), "a held entry joins the final queued after it, whatever its kind names");
    }

    [Theory]
    [InlineData(true, "is holding")]
    [InlineData(false, "is going around")]
    public void FollowRefusal_LeadHeldOutOfSequence_ReadsAsUnheld(bool missedApproachHold, string expected)
    {
        CommandResult unheld = FollowFromDownwind(missedApproachHold, hold: false);
        CommandResult held = FollowFromDownwind(missedApproachHold, hold: true);

        Assert.False(unheld.Success);
        Assert.Contains(expected, unheld.Message, StringComparison.Ordinal);
        Assert.Equal(unheld.Success, held.Success);
        Assert.Equal(unheld.Message, held.Message);
    }

    [Theory]
    [InlineData(PatternEntryLeg.Downwind)]
    [InlineData(PatternEntryLeg.Final)]
    public void Follow_FromFreeFlight_HeldLeadOnTheCircuit_SequencesAsUnheld(PatternEntryLeg leg)
    {
        (CommandResult unheld, string unheldPhase) = FollowFromFreeFlight(leg, hold: false);
        (CommandResult held, string heldPhase) = FollowFromFreeFlight(leg, hold: true);

        Assert.True(unheld.Success, unheld.Message);
        Assert.Equal(unheld.Success, held.Success);
        Assert.Equal(unheld.Message, held.Message);
        Assert.Equal(unheldPhase, heldPhase);
    }

    // ─── Runway and traffic readers ───

    [Fact]
    public void AsdexRole_HeldFinal_IsArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, AsdexRunwayUsers.Role(Paired(new FinalApproachPhase(), Oak("28R")), arrivesOnRunway: false));

    [Fact]
    public void Evaluator_HeldFinal_IsArrivalApproach()
    {
        AircraftState ac = Paired(new FinalApproachPhase(), Oak("28R"));
        ac.Position = OffFinal(Oak("28R"), 3.0, 0);

        SoloTrainingEvaluator.SameRunwaySeparationTracker.AircraftRunwayState? state =
            SoloTrainingEvaluator.SameRunwaySeparationTracker.TryBuildState(ac);

        Assert.NotNull(state);
        Assert.True(state.IsArrivalApproach);
    }

    [Fact]
    public void RunwayCrossingGate_HeldGoAroundOrbitingOffTheDepartureEnd_DoesNotBlock()
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = Paired(new GoAroundPhase(), rwy);
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 1.3);
        ac.Altitude = rwy.AirportElevationFt + 800;

        Assert.False(RunwayCrossingGate.IsOverTheRunway(ac, rwy), "an orbit away from the pavement does not shut the crossing");
    }

    [Fact]
    public void RunwayCrossingGate_HeldGoAroundOverThePavement_Blocks()
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = Paired(new GoAroundPhase(), rwy);
        ac.Position = OffFinal(rwy, -0.5, 0);
        ac.Altitude = rwy.AirportElevationFt + 800;

        Assert.True(RunwayCrossingGate.IsOverTheRunway(ac, rwy));
    }

    [Fact]
    public void SameRunwayProtection_HeldFinal_IsNotEligible()
    {
        (SimulationEngine engine, AircraftState held, RunwayInfo rwy) = ProtectionStream(hold: true);
        (SimulationEngine twinEngine, AircraftState unheld, _) = ProtectionStream(hold: false);

        Assert.False(engine.IsProtectionEligible(held, rwy, engine.Scenario!, preClearance: false), "an orbiting aircraft gets no speed adjustment");
        Assert.True(twinEngine.IsProtectionEligible(unheld, rwy, twinEngine.Scenario!, preClearance: false));
    }

    [Fact]
    public void SameRunwayProtection_HeldFinal_StaysInTheArrivalStream()
    {
        (SimulationEngine engine, AircraftState held, _) = ProtectionStream(hold: true);

        Assert.Contains(engine.BuildRunwayArrivalStreams(), stream => stream.Exists(e => ReferenceEquals(e.Aircraft, held)));
    }

    [Fact]
    public void ScenarioExport_HeldFinal_ExportsAsAirborneVfrAtItsPosition()
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = Paired(new FinalApproachPhase(), rwy);
        ac.Position = OffFinal(rwy, 3.0, 0);
        ac.TrueHeading = rwy.TrueHeading;
        ac.TrueTrack = rwy.TrueHeading;
        ac.Altitude = rwy.AirportElevationFt + 950;
        ac.FlightPlan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KOAK",
            FlightRules = "VFR",
        };
        var context = new ScenarioExportContext("ZOA", "KOAK", null, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1));

        ScenarioExportResult result = ScenarioExporter.Export([ac], context, null, _ => null);

        StartingConditions start = Assert.Single(result.Scenario.Aircraft).StartingConditions;
        Assert.Equal("Coordinates", start.Type);
        Assert.Equal(Math.Round(ac.Position.Lat, 7), start.Coordinates!.Lat);
        Assert.Equal(Math.Round(ac.Position.Lon, 7), start.Coordinates.Lon);
        Assert.Contains(new ScenarioExportFlag(ac.Callsign, ScenarioExporter.ReasonAirborneVfr), result.Flags);
    }

    // ─── Readers that keep showing the hold ───

    [Fact]
    public void Situation_WithAPair_IsHolding()
    {
        AircraftState ac = HeldDownwindStarted(out _);

        Assert.Equal(AircraftSituation.Holding, SituationClassifier.Classify(ac, 0, AircraftSituation.Unknown));
    }

    [Fact]
    public void Overlay_WithAPair_DrawsTheOrbit()
    {
        AircraftState ac = HeldDownwindStarted(out _);

        Assert.Contains(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.OrbitCircle);
    }

    [Fact]
    public void StatusLine_WithAPair_NamesTheHold()
    {
        AircraftState paired = HeldDownwindStarted(out AircraftState freeFlight);

        string pairedText = AircraftStatusDescriber.Describe(paired, AircraftStatusContext.None).Text;
        _output.WriteLine(pairedText);

        Assert.Equal(AircraftStatusDescriber.Describe(freeFlight, AircraftStatusContext.None).Text, pairedText);
    }

    // ─── Builders ───

    /// <summary>Two aircraft built by <paramref name="build"/>; the second has the SFO hold put in front of its running phase.</summary>
    private static (AircraftState Unheld, AircraftState Held) Twins(Func<AircraftState> build)
    {
        AircraftState unheld = build();
        AircraftState held = build();
        Phase running = held.Phases!.CurrentPhase!;
        held.Phases.InsertHoldInFront(SfoBravoHold());
        Assert.Same(running, held.Phases.HeldPhase);
        return (unheld, held);
    }

    /// <summary>A VFR aircraft on the KOAK 28R right circuit from <paramref name="leg"/>, placed by <paramref name="place"/>.</summary>
    private static AircraftState OnCircuit(string callsign, PatternEntryLeg leg, Func<PatternWaypoints, (LatLon, TrueHeading)>? place)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, 1.0, 0), rwy.TrueHeading, 1000);
        PutOnCircuit(ac, rwy, leg);
        if (place is not null)
        {
            (LatLon position, TrueHeading heading) = place(WaypointsOf(ac));
            ac.Position = position;
            ac.TrueHeading = heading;
            ac.TrueTrack = heading;
        }

        return ac;
    }

    private static AircraftState GoingAround(string callsign, bool reenterPattern)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, -0.5, 0), rwy.TrueHeading, 600);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new GoAroundPhase { ReenterPattern = reenterPattern, Status = PhaseStatus.Active });
        return ac;
    }

    private static AircraftState MissedApproachHold(string callsign)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, -2.0, 0), rwy.TrueHeading, 2000);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(
            new HoldingPatternPhase
            {
                FixName = "OAK",
                FixLat = rwy.ThresholdLatitude,
                FixLon = rwy.ThresholdLongitude,
                InboundCourse = (int)rwy.TrueHeading.Degrees,
                LegLength = 1.0,
                IsMinuteBased = true,
                Direction = TurnDirection.Right,
                IsMissedApproachHold = true,
                Status = PhaseStatus.Active,
            }
        );
        return ac;
    }

    private static AircraftState ExtendedDownwindLead()
    {
        AircraftState lead = OnCircuit(Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 0.5));
        Assert.IsType<DownwindPhase>(lead.Phases!.CurrentPhase).IsExtended = true;
        return lead;
    }

    /// <summary>A lead on a straight-in entry kind whose next queued phase is the circuit's downwind, joining at <paramref name="join"/>.</summary>
    private static AircraftState EntryJoiningDownwind(LatLon join)
    {
        AircraftState lead = EntryAhead(PatternEntryLeg.Downwind, PatternEntryKind.Final, join);
        lead.Position = GeoMath.ProjectPoint(join, new TrueHeading(0), 2.0);
        return lead;
    }

    /// <summary>
    /// A lead on a <paramref name="kind"/> entry, joining at <paramref name="join"/>, queued in front of the circuit from
    /// <paramref name="leg"/>.
    /// </summary>
    private static AircraftState EntryAhead(PatternEntryLeg leg, PatternEntryKind kind, LatLon join)
    {
        AircraftState lead = OnCircuit(Leader, leg, null);
        PhaseList phases = lead.Phases!;
        phases.CurrentPhase!.Status = PhaseStatus.Pending;
        phases.Phases.Insert(
            phases.CurrentIndex,
            new PatternEntryPhase
            {
                EntryLat = join.Lat,
                EntryLon = join.Lon,
                PatternAltitude = 1000,
                Kind = kind,
                Status = PhaseStatus.Active,
            }
        );
        return lead;
    }

    /// <summary>An Active <paramref name="held"/> behind the SFO hold, on <paramref name="runway"/>, for an airborne VFR aircraft.</summary>
    private static AircraftState Paired(Phase held, RunwayInfo runway)
    {
        held.Status = PhaseStatus.Active;
        AircraftState ac = MakeVfr(Leader, new LatLon(37.71, -121.80), new TrueHeading(70), 1400);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(held);
        ac.Phases.InsertHoldInFront(SfoBravoHold());
        Assert.Same(held, ac.Phases.HeldPhase);
        return ac;
    }

    /// <summary>
    /// A downwind aircraft with a started SFO hold in front of its downwind; <paramref name="freeFlight"/> is its twin in the
    /// same hold, started the same way, with nothing behind it.
    /// </summary>
    private static AircraftState HeldDownwindStarted(out AircraftState freeFlight)
    {
        AircraftState paired = OnCircuit(Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        StartHold(paired, list => list.InsertHoldInFront);
        Assert.NotNull(paired.Phases!.HeldPhase);

        freeFlight = OnCircuit(Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        freeFlight.Phases = new PhaseList();
        StartHold(freeFlight, list => list.Add);
        return paired;
    }

    private static void StartHold(AircraftState ac, Func<PhaseList, Action<AirspaceBoundaryHoldPhase>> put)
    {
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        put(ac.Phases!)(hold);
        hold.Status = PhaseStatus.Active;
        hold.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
    }

    private CommandResult FollowFromDownwind(bool missedApproachHold, bool hold)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = OnCircuit(Follower, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        engine.World.AddAircraft(follower);
        AircraftState lead = missedApproachHold ? MissedApproachHold(Leader) : GoingAround(Leader, reenterPattern: false);
        AddLead(engine, lead, hold);
        return Send(engine, hold);
    }

    private (CommandResult Result, string Phase) FollowFromFreeFlight(PatternEntryLeg leg, bool hold)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = MakeVfr(Follower, OffFinal(Oak("28R"), 4.0, 5.0), new TrueHeading(180), 2000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        AddLead(engine, OnCircuit(Leader, leg, null), hold);
        CommandResult result = Send(engine, hold);
        return (result, follower.Phases?.CurrentPhase?.Name ?? "no phase");
    }

    private static void AddLead(SimulationEngine engine, AircraftState lead, bool hold)
    {
        if (hold)
        {
            lead.Phases!.InsertHoldInFront(SfoBravoHold());
            Assert.NotNull(lead.Phases.HeldPhase);
        }

        engine.World.AddAircraft(lead);
    }

    private CommandResult Send(SimulationEngine engine, bool hold)
    {
        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        _output.WriteLine($"{(hold ? "held" : "unheld")} lead: success={result.Success} - {result.Message}");
        return result;
    }

    /// <summary>
    /// A same-runway arrival stream on the KOAK 28R final with the protection pass's gate open (tower student): a slow lead
    /// on final 4 nm out and, 8 nm out and faster, the follower on its final, held behind the SFO hold when
    /// <paramref name="hold"/>.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Follower, RunwayInfo Runway) ProtectionStream(bool hold)
    {
        RunwayInfo rwy = Oak("28R");
        SimulationEngine engine = BuildEngine();
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-held-protection",
            ScenarioName = "Held Protection",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "KOAK",
            StudentPositionType = "TWR",
            AutoArrivalSpacingOnOccupiedRunway = true,
        };
        engine.World.AddAircraft(OnFinal(Leader, new FinalApproachPhase { Status = PhaseStatus.Active }, rwy, 4.0, 90, hold: false));
        AircraftState follower = OnFinal(Follower, new FinalApproachPhase { Status = PhaseStatus.Active }, rwy, 8.0, 160, hold);
        engine.World.AddAircraft(follower);
        return (engine, follower, rwy);
    }

    private static AircraftState OnFinal(string callsign, Phase phase, RunwayInfo rwy, double alongNm, double ias, bool hold)
    {
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, alongNm, 0), rwy.TrueHeading, rwy.AirportElevationFt + (alongNm * 318), ias);
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        ac.Phases.Add(phase);
        if (hold)
        {
            ac.Phases.InsertHoldInFront(SfoBravoHold());
            Assert.Same(phase, ac.Phases.HeldPhase);
        }

        return ac;
    }

    private static SimulationEngine BuildEngine() => new(new TestAirportGroundData(), PushPlanStore.CreateShipped());

    private static PhaseContext ContextWithLead(AircraftState follower, AircraftState lead)
    {
        RunwayInfo? runway = follower.Phases?.AssignedRunway;
        return new PhaseContext
        {
            Aircraft = follower,
            Targets = follower.Targets,
            Category = AircraftCategorization.Categorize(follower.AircraftType),
            DeltaSeconds = 1,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? 0,
            ActiveRunways = ActiveRunways.Empty,
            Logger = NullLogger.Instance,
            AircraftLookup = cs => string.Equals(cs, lead.Callsign, StringComparison.OrdinalIgnoreCase) ? lead : null,
        };
    }

    private static PatternWaypoints WaypointsOf(AircraftState ac) =>
        AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase) ?? throw new InvalidOperationException("no waypoints");

    private static LatLon BaseTurn(PatternWaypoints wp) => new(wp.BaseTurnLat, wp.BaseTurnLon);

    /// <summary>On the downwind, <paramref name="shortNm"/> before the base-turn point.</summary>
    private static (LatLon, TrueHeading) DownwindShortOfBaseTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(BaseTurn(wp), wp.DownwindHeading.ToReciprocal(), shortNm), wp.DownwindHeading);

    /// <summary>On the base, <paramref name="intoNm"/> past the base-turn point.</summary>
    private static (LatLon, TrueHeading) IntoBase(PatternWaypoints wp, double intoNm) =>
        (GeoMath.ProjectPoint(BaseTurn(wp), wp.CrosswindHeading.ToReciprocal(), intoNm), wp.CrosswindHeading.ToReciprocal());

    private static AirspaceBoundaryHoldPhase SfoBravoHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };
}
