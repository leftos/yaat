using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Per-airport exit capacity (sidecar <c>exitCapacity</c>): SFO taxiway T between the 28R and 28L hold bars is about
/// 356 ft long, room for two aircraft at or below CWT G and one otherwise. In bundle "S1-SFO-2 | Ground Control 28/01"
/// (fixture <c>sfo-gc-28-01</c>) every 28R arrival exited left on T while the previous one still held short of 28L
/// there, because an aircraft that pulled up to the parallel bar no longer claimed the 28R exit bar. A full segment
/// now reads as an occupied exit for automatic exit choice, so the arrival takes a later exit (AIM 4-3-21.a).
/// </summary>
public class SfoExitCapacityTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-28-01-recording.yaat-bug-report-bundle.zip";
    private const int ReplayRestoreAt = 1040;
    private const int ReplayEndAt = 1110;

    /// <summary>Where an arrival's rollout ended up: the exit bar it last targeted and the phase it left the runway into.</summary>
    private sealed record ExitOutcome(int? ExitBarNodeId, string? FinalPhase);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("ExitCapacity", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-sfo-exit-capacity",
                ScenarioName = "SFO exit capacity",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
                AutoPullUpToParallel = true,
            },
        };
    }

    private static AirportGroundLayout SfoLayout()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return layout;
    }

    private static ExitCapacitySegment Segment28RT(AirportGroundLayout layout) =>
        Assert.Single(ExitCapacityResolver.GetSegments(layout), s => (s.Rule.Runway == "28R") && (s.Rule.Taxiway == "T"));

    /// <summary>An arrival of <paramref name="type"/> on a 1 nm final to SFO 28R, cleared to land.</summary>
    private static AircraftState AddArrival(SimulationEngine engine, AirportGroundLayout layout, string callsign, string type)
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);

        LatLon start = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading.ToReciprocal(), 1.0);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = start,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Targets.TargetSpeed = 140;
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);

        CommandResult cleared = engine.SendCommand(callsign, "CLAND");
        Assert.True(cleared.Success, $"CLAND {callsign} failed: {cleared.Message}");
        return aircraft;
    }

    /// <summary>Lands <paramref name="type"/> on 28R with EXIT T and ticks until it holds short of 28L at the segment's parallel-runway bar.</summary>
    private void LandOccupantOnT(SimulationEngine engine, AirportGroundLayout layout, ExitCapacitySegment segment, string type)
    {
        AircraftState occupant = AddArrival(engine, layout, "OCC1", type);
        CommandResult exit = engine.SendCommand("OCC1", "EXIT T");
        Assert.True(exit.Success, $"EXIT T failed: {exit.Message}");

        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if ((occupant.Phases?.CurrentPhase is HoldingShortPhase hs) && (hs.HoldShort.NodeId == segment.ParallelBarNodeId))
            {
                output.WriteLine($"OCC1 ({type}) holding short of 28L at #{segment.ParallelBarNodeId} after {t} s");
                return;
            }
        }

        Assert.Fail($"OCC1 ({type}) never held short of 28L at #{segment.ParallelBarNodeId}; phase {occupant.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>Ticks until <paramref name="arrival"/> leaves the runway, returning the exit bar its rollout committed to.</summary>
    private ExitOutcome RollOut(SimulationEngine engine, AircraftState arrival)
    {
        int? lastTarget = null;
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            Phase? phase = arrival.Phases?.CurrentPhase;
            if (phase is RunwayExitPhase { TargetHoldShortNodeId: { } target })
            {
                lastTarget = target;
            }

            if (phase is HoldingAfterExitPhase or TaxiingPhase or HoldingShortPhase)
            {
                output.WriteLine($"{arrival.Callsign}: left the runway via HS #{lastTarget} into {phase.Name} after {t} s");
                return new ExitOutcome(lastTarget, phase.Name);
            }
        }

        return new ExitOutcome(lastTarget, arrival.Phases?.CurrentPhase?.Name);
    }

    private ExitOutcome? RunPair(string occupantType, string arrivalType)
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = Segment28RT(layout);
        output.WriteLine($"28R/T segment: HS #{segment.ExitBarNodeId} → HS #{segment.ParallelBarNodeId} ({segment.ParallelRunwayId})");

        LandOccupantOnT(engine, layout, segment, occupantType);
        AircraftState arrival = AddArrival(engine, layout, "ARR2", arrivalType);
        return RollOut(engine, arrival);
    }

    /// <summary>A B737 holds short of 28L on T; an A321 landing 28R must not exit onto T (both above CWT G: capacity one).</summary>
    [Fact]
    public void A321_WithB737HoldingShortOnT_TakesLaterExit()
    {
        ExitOutcome? outcome = RunPair("B737", "A321");
        if (outcome is null)
        {
            return;
        }

        ExitCapacitySegment segment = Segment28RT(SfoLayout());
        Assert.NotNull(outcome.ExitBarNodeId);
        Assert.NotEqual(segment.ExitBarNodeId, outcome.ExitBarNodeId);
    }

    /// <summary>An E75L holds short of 28L on T; a second E75L may still exit onto T (both at CWT G: capacity two).</summary>
    [Fact]
    public void E75L_WithE75LHoldingShortOnT_MayExitOnT()
    {
        ExitOutcome? outcome = RunPair("E75L", "E75L");
        if (outcome is null)
        {
            return;
        }

        ExitCapacitySegment segment = Segment28RT(SfoLayout());
        Assert.Equal(segment.ExitBarNodeId, outcome.ExitBarNodeId);
    }

    /// <summary>An E75L holds short of 28L on T; a B737 landing 28R must not exit onto T — the mixed pair is above CWT G.</summary>
    [Fact]
    public void B737_WithE75LHoldingShortOnT_TakesLaterExit()
    {
        ExitOutcome? outcome = RunPair("E75L", "B737");
        if (outcome is null)
        {
            return;
        }

        ExitCapacitySegment segment = Segment28RT(SfoLayout());
        Assert.NotNull(outcome.ExitBarNodeId);
        Assert.NotEqual(segment.ExitBarNodeId, outcome.ExitBarNodeId);
    }

    /// <summary>
    /// Replay of the fixture from t=1040 to t=1110: SWA2644 (B737) holds short of 28L on T from t=975 until its CROSS at
    /// t=1111; JBU115 (A321) rolls out on 28R from t=1045. JBU115 must not exit onto T while SWA2644 is on it.
    /// </summary>
    [Fact]
    public void Replay_Jbu115_DoesNotExitOntoTWhileSwa2644HoldsThere()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(archive.ToBaseSessionRecording(), 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(ReplayRestoreAt);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);

            ExitCapacitySegment segment = Segment28RT(SfoLayout());
            int secondsSwaHeldWhileJbuRolled = 0;
            List<string> violations = [];
            for (int t = ReplayRestoreAt + 1; t <= ReplayEndAt; t++)
            {
                engine.ReplayOneSecond();
                AircraftState? jbu = engine.FindAircraft("JBU115");
                AircraftState? swa = engine.FindAircraft("SWA2644");
                bool swaOnT = swa?.Phases?.CurrentPhase is HoldingShortPhase hs && (hs.HoldShort.NodeId == segment.ParallelBarNodeId);
                Phase? jbuPhase = jbu?.Phases?.CurrentPhase;
                bool jbuOnT =
                    (jbuPhase is RunwayExitPhase rep && (rep.TargetHoldShortNodeId == segment.ExitBarNodeId))
                    || (jbuPhase is HoldingAfterExitPhase haep && (haep.HoldShortNodeId == segment.ExitBarNodeId))
                    || (
                        (jbuPhase is TaxiingPhase) && (jbu?.Ground.AssignedTaxiRoute?.Segments.FirstOrDefault()?.FromNodeId == segment.ExitBarNodeId)
                    );
                if (swaOnT && (jbuPhase is LandingPhase or RunwayExitPhase))
                {
                    secondsSwaHeldWhileJbuRolled++;
                }

                if (swaOnT && jbuOnT)
                {
                    violations.Add($"t={t}: JBU115 in {jbuPhase?.Name} onto T while SWA2644 holds short of 28L there");
                }

                if (t % 5 == 0)
                {
                    output.WriteLine(
                        $"t={t} JBU115 {jbuPhase?.Name} target={(jbuPhase as RunwayExitPhase)?.TargetHoldShortNodeId} "
                            + $"gs={jbu?.GroundSpeed:F0}; SWA2644 {swa?.Phases?.CurrentPhase?.Name} onT={swaOnT}"
                    );
                }
            }

            Assert.True(secondsSwaHeldWhileJbuRolled > 0, "SWA2644 never held short of 28L on T during JBU115's rollout; the replay proves nothing");
            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>
    /// An exitCapacity entry naming a taxiway the layout lacks logs one Error naming the airport, runway and taxiway, and
    /// is dropped; a good entry beside it still resolves.
    /// </summary>
    [Fact]
    public void Resolve_UnknownTaxiway_LogsErrorAndDropsOnlyThatEntry()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = SfoLayout();
        var good = new ExitCapacityRule("28R", "T", MaxAircraft: 2, MaxAircraftAboveCwt: 1, CwtThreshold: "G", Notes: null);
        var bad = new ExitCapacityRule("28R", "ZZ9", MaxAircraft: 2, MaxAircraftAboveCwt: 1, CwtThreshold: "G", Notes: null);

        var capture = ErrorLogCapture.Install();
        IReadOnlyList<ExitCapacitySegment> segments = ExitCapacityResolver.Resolve(layout, [good, bad]);

        ExitCapacitySegment resolved = Assert.Single(segments);
        Assert.Same(good, resolved.Rule);
        string error = Assert.Single(capture.Errors);
        output.WriteLine(error);
        Assert.Contains(layout.AirportId, error, StringComparison.Ordinal);
        Assert.Contains("runway 28R", error, StringComparison.Ordinal);
        Assert.Contains("taxiway ZZ9", error, StringComparison.Ordinal);
        Assert.Contains("Fix the entry", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ExitCapacityResolver"/> matches a rule's runway against a hold-short's <see cref="GroundNode.RunwayId"/>
    /// with <see cref="RunwayIdentifier.Contains"/>, which compares whole designators, zero-padding normalised: a 1R rule
    /// serves the 1R/19L bars in either written form, and never an 11R/29L bar whose name merely contains "1R".
    /// </summary>
    [Theory]
    [InlineData("11R", "29L", "1R", false)]
    [InlineData("1R", "19L", "1R", true)]
    [InlineData("01R", "19L", "1R", true)]
    [InlineData("10L", "28R", "28R", true)]
    [InlineData("10L", "28R", "8R", false)]
    public void HoldShortRunwayId_MatchesRuleRunwayByExactDesignator(string end1, string end2, string ruleRunway, bool expected) =>
        Assert.Equal(expected, new RunwayIdentifier(end1, end2).Contains(ruleRunway));

    /// <summary>
    /// An occupant whose type has no known CWT counts as above the rule's threshold, so the segment holds only
    /// <see cref="ExitCapacityRule.MaxAircraftAboveCwt"/> even for an arrival at the threshold.
    /// </summary>
    [Fact]
    public void Capacity_OccupantWithUnknownCwt_CountsAsAboveThreshold()
    {
        TestVnasData.EnsureInitialized();
        ExitCapacitySegment segment = Segment28RT(SfoLayout());
        string? unknownCwt = WakeTurbulenceData.GetCwt("ZZZZ");

        Assert.Null(unknownCwt);
        Assert.False(segment.IsAtOrBelowThreshold(unknownCwt));
        Assert.True(segment.Rule.MaxAircraftAboveCwt < segment.Rule.MaxAircraft);
        Assert.Equal(segment.Rule.MaxAircraft, segment.CapacityFor(segment.Rule.CwtThreshold, occupantsAtOrBelowThreshold: true));
        Assert.Equal(
            segment.Rule.MaxAircraftAboveCwt,
            segment.CapacityFor(segment.Rule.CwtThreshold, occupantsAtOrBelowThreshold: segment.IsAtOrBelowThreshold(unknownCwt))
        );
    }
}
