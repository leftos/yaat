using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The option clearances (<c>TG</c>/<c>SG</c>/<c>LA</c>/<c>COPT</c>) take the same pattern modifier
/// <c>CTO</c> does: <c>COPT MLT 28L</c> clears the option on the runway the aircraft is already flying
/// and puts it into 28L's left pattern on the climb-out. The modifier moves
/// <see cref="PhaseList.PatternRunway"/> only — the clearance itself is flown on the assigned runway —
/// and the auto-cycle turns the mismatch into the runway-transition circuit (AIM 4-3-2: the aircraft
/// climbs out on the runway it used, and joins the other runway's pattern beyond both departure ends).
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA, OAK). N342T (DA42) flies a 28R right
/// circuit: final from t≈865 (recorded <c>COPT</c> at t=883), touch-and-go at t≈910, a fresh upwind at
/// t≈935, then a second, uncleared approach that goes around at t≈1110. Every E2E test restores a snapshot and
/// drives the engine with <see cref="SimulationEngine.TickOneSecond"/>, so the recording's own commands
/// (including the operator's MLT at t=1125) never fire.
/// </summary>
public class OptionClearancePatternModifierTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const string AirportId = "OAK";

    /// <summary>Final approach 28R, after the recorded COPT — the aircraft is committed to the option.</summary>
    private const int FinalBeforeTouchAndGoTime = 890;

    /// <summary>Final approach on the second circuit — the approach the recording goes around from at t≈1110.</summary>
    private const int FinalBeforeGoAroundTime = 1095;

    private const int MaxTicks = 300;

    // ---------------------------------------------------------------------------------------------
    // Parser + describer
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The canonical text is what the action router records and re-parses on every run kind, so
    /// <see cref="CommandDescriber.DescribeCommand"/> followed by <see cref="CommandParser.Parse"/> has
    /// to be the identity for every field — a dropped pattern runway replays as a different clearance.
    /// </summary>
    [Theory]
    [InlineData("TG")]
    [InlineData("TG 28R")]
    [InlineData("TG MLT")]
    [InlineData("TG MRT")]
    [InlineData("TG 28R MLT 28L")]
    [InlineData("TG MLT 28L 15")]
    [InlineData("COPT")]
    [InlineData("COPT MLT 28L")]
    [InlineData("SG MRT 28R 015")]
    [InlineData("SG MRT 28R 15")]
    [InlineData("LA MLT")]
    [InlineData("LA MRT")]
    [InlineData("MLT")]
    [InlineData("MRT")]
    [InlineData("MLT 28R")]
    [InlineData("MLT 28R 015")]
    [InlineData("MLT 28R 15")]
    [InlineData("MRT 15")]
    [InlineData("CTO MLT")]
    [InlineData("CTO MLT 28R")]
    [InlineData("CTO MLT 28R 15")]
    [InlineData("CTO MRT 15")]
    public void OptionClearanceCanonical_RoundTripsThroughTheParser(string canonical)
    {
        TestVnasData.EnsureInitialized();

        var parsed = CommandParser.Parse(canonical);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(parsed.Value!));
    }

    [Fact]
    public void ParseTouchAndGo_LandingRunwayThenModifier_KeepsEveryField()
    {
        TestVnasData.EnsureInitialized();

        var parsed = CommandParser.Parse("TG 28R MLT 28L 15");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var tg = Assert.IsType<TouchAndGoCommand>(parsed.Value);
        Assert.Equal("28R", tg.RunwayId);
        Assert.Equal(PatternDirection.Left, tg.TrafficPattern);
        Assert.Equal("28L", tg.PatternRunwayId);
        Assert.Equal(1500, tg.PatternAltitude);
    }

    [Fact]
    public void ParseClearedForOption_ModifierWithRunway_KeepsEveryField()
    {
        TestVnasData.EnsureInitialized();

        var parsed = CommandParser.Parse("COPT MRT 28R 20");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        var opt = Assert.IsType<ClearedForOptionCommand>(parsed.Value);
        Assert.Equal(PatternDirection.Right, opt.TrafficPattern);
        Assert.Equal("28R", opt.PatternRunwayId);
        Assert.Equal(2000, opt.PatternAltitude);
    }

    /// <summary>A token the modifier cannot read is rejected, not silently dropped — it names a runway
    /// and an altitude the aircraft will actually fly.</summary>
    [Fact]
    public void ParseTouchAndGo_TrailingJunkAfterTheModifier_Fails()
    {
        TestVnasData.EnsureInitialized();

        var parsed = CommandParser.Parse("TG MLT 28L BOGUS");

        Assert.False(parsed.IsSuccess);
        Assert.Contains("BOGUS", parsed.Reason ?? "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // E2E — the clearance is flown on the assigned runway, the next circuit belongs to the new one
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CoptMlt28L_OnFinal28R_FliesTheOptionOn28RThenTransitionsTo28L()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);

            // Re-issuing the option replaces the clearance the recording already gave; the modifier is
            // the new part.
            var result = engine.SendCommand(Callsign, "COPT MLT 28L");
            output.WriteLine($"COPT MLT 28L: success={result.Success} — {result.Message}");
            Assert.True(result.Success, $"COPT MLT 28L was refused: {result.Message}");
            Assert.Contains("Runway 28R", result.Message ?? "", StringComparison.Ordinal);
            Assert.Contains("left traffic", result.Message ?? "", StringComparison.Ordinal);
            Assert.Contains("28L", result.Message ?? "", StringComparison.Ordinal);
            // 28R and 28L are close parallels: the transition continues the upwind past both departure
            // ends, so there is no field crossing to announce.
            Assert.DoesNotContain("crossing midfield", result.Message ?? "", StringComparison.Ordinal);

            Assert.Equal("28L", ac.Phases?.PatternRunway?.Designator);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Left, ac.Pattern.TrafficDirection);
            Assert.Equal(ClearanceType.ClearedForOption, ac.Phases?.LandingClearance);

            FlyTheOptionAndTheTransition(engine);
        }
    }

    /// <summary>
    /// A go-around voids the clearance the modifier rode on (AIM 5-5-5.a.6: the pilot requests the next
    /// action rather than carrying the old instruction into the climb-out), so the armed pattern runway
    /// is dropped: the aircraft keeps flying 28R's pattern and the RPO is told to re-issue. Only a
    /// clearance actually flown — the touch-and-go of the test above — transitions.
    ///
    /// The go-around is commanded rather than left to the simulation: on this approach N342T goes
    /// around because it reaches minimums <em>uncleared</em>, and issuing the option clearance is
    /// exactly what removes that reason.
    /// </summary>
    [Fact]
    public void CoptMlt28L_GoingAroundOff28R_CancelsTheArmedTransitionAndWarnsTheRpo()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeGoAroundTime);
            if (ac is null)
            {
                return;
            }

            Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);

            var warnings = new List<string>();
            engine.WarningEmitted += (callsign, warning) =>
            {
                if (string.Equals(callsign, Callsign, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(warning);
                }
            };

            var result = engine.SendCommand(Callsign, "COPT MLT 28L");
            Assert.True(result.Success, $"COPT MLT 28L was refused: {result.Message}");
            Assert.Equal("28L", ac.Phases?.PatternRunway?.Designator);

            var goAround = engine.SendCommand(Callsign, "GA");
            Assert.True(goAround.Success, $"GA was refused: {goAround.Message}");
            Assert.IsType<GoAroundPhase>(ac.Phases?.CurrentPhase);

            bool sawGoAround = false;
            AircraftState? onNextCircuit = null;
            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                var live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);

                sawGoAround |= live.Phases?.CurrentPhase is GoAroundPhase;
                if (sawGoAround && (live.Phases?.CurrentPhase is UpwindPhase))
                {
                    output.WriteLine($"post-go-around circuit at +{t}s");
                    onNextCircuit = live;
                    break;
                }
            }

            Assert.True(sawGoAround, "the go-around never became the current phase");
            Assert.NotNull(onNextCircuit);
            output.WriteLine($"warnings=[{string.Join(" | ", warnings)}]");

            // The pattern stays on the runway the aircraft is flying, both fields together, so the next
            // auto-cycle does not transition either.
            Assert.Equal("28R", onNextCircuit.Phases?.AssignedRunway?.Designator);
            Assert.Equal("28R", onNextCircuit.Phases?.PatternRunway?.Designator);
            Assert.DoesNotContain(onNextCircuit.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);

            var upwind = Assert.IsType<UpwindPhase>(onNextCircuit.Phases?.CurrentPhase);
            var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
            Assert.NotNull(upwind.Waypoints);
            Assert.True(
                GeoMath.DistanceNm(upwind.Waypoints.ThresholdLat, upwind.Waypoints.ThresholdLon, rwy28R.ThresholdLatitude, rwy28R.ThresholdLongitude)
                    < 0.05,
                "the post-go-around circuit must be built on 28R, the runway the aircraft is flying"
            );

            Assert.Contains(
                warnings,
                w =>
                    w.Contains("went around", StringComparison.OrdinalIgnoreCase)
                    && w.Contains("28L", StringComparison.Ordinal)
                    && w.Contains("re-issue", StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    /// <summary>
    /// A pattern runway that is not a close parallel of the one being flown joins through a midfield
    /// crossing (SFO 28R → 1L), and the clearance's own result text is the only place the RPO learns that
    /// the aircraft will fly across the field — AIM 4-3-5's unexpected maneuver.
    /// </summary>
    [Fact]
    public void CoptMlt_CrossingRunway_AnnouncesTheMidfieldCrossing()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        if (runway28R is null)
        {
            return;
        }

        var ac = OnFinalFor(runway28R);

        var result = PatternCommandHandler.TrySetupClearedForOption(
            ac,
            new OptionPatternModifier(PatternDirection.Left, "01L", null),
            TestDispatch.Context(Random.Shared)
        );

        output.WriteLine($"COPT MLT 01L: success={result.Success} — {result.Message}");
        Assert.True(result.Success, $"COPT MLT 01L was refused: {result.Message}");
        Assert.Equal("01L", ac.Phases?.PatternRunway?.Designator);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Contains("crossing midfield", result.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The OAK variant of the crossing case, reachable now that a bare two-digit token is a runway
    /// rather than a pattern altitude: <c>COPT MLT 33</c> on the 28R final arms OAK's crossing runway,
    /// and the readback says so. This is the field the E2E fixtures fly, so the grammar and the
    /// announcement are pinned on the same airport the recordings use.
    /// </summary>
    [Fact]
    public void CoptMlt_OakCrossingRunway33_ArmsItAndAnnouncesTheMidfieldCrossing()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R");
        if (runway28R is null)
        {
            return;
        }

        var parsed = CommandParser.Parse("COPT MLT 33");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var opt = Assert.IsType<ClearedForOptionCommand>(parsed.Value);
        Assert.Equal("33", opt.PatternRunwayId);
        Assert.Null(opt.PatternAltitude);

        var ac = OnFinalFor(runway28R);
        var result = PatternCommandHandler.TrySetupClearedForOption(
            ac,
            new OptionPatternModifier(opt.TrafficPattern, opt.PatternRunwayId, opt.PatternAltitude),
            TestDispatch.Context(Random.Shared)
        );

        output.WriteLine($"COPT MLT 33: success={result.Success} — {result.Message}");
        Assert.True(result.Success, $"COPT MLT 33 was refused: {result.Message}");
        Assert.Equal("33", ac.Phases?.PatternRunway?.Designator);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Contains("crossing midfield", result.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>The parallel case is the counterpart: SFO 28R → 28L continues the upwind, no crossing.</summary>
    [Fact]
    public void CoptMlt_CloseParallel_DoesNotAnnounceAMidfieldCrossing()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        if (runway28R is null)
        {
            return;
        }

        var ac = OnFinalFor(runway28R);

        var result = PatternCommandHandler.TrySetupClearedForOption(
            ac,
            new OptionPatternModifier(PatternDirection.Left, "28L", null),
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success, $"COPT MLT 28L was refused: {result.Message}");
        Assert.Equal("28L", ac.Phases?.PatternRunway?.Designator);
        Assert.DoesNotContain("crossing midfield", result.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The modifier's runway may be the one already assigned — <c>TG 28R MRT 28R</c> is today's
    /// <c>TG MRT</c> with the runways spelled out. No transition, no crossing: the next circuit is the
    /// plain one on 28R.
    /// </summary>
    [Fact]
    public void Tg28RMrt28R_SameRunway_KeepsFlyingThePlain28RCircuit()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            var ac = RestoreAt(engine, archive, FinalBeforeTouchAndGoTime);
            if (ac is null)
            {
                return;
            }

            var result = engine.SendCommand(Callsign, "TG 28R MRT 28R");
            output.WriteLine($"TG 28R MRT 28R: success={result.Success} — {result.Message}");
            Assert.True(result.Success, $"TG 28R MRT 28R was refused: {result.Message}");
            Assert.Equal("28R", ac.Phases?.PatternRunway?.Designator);
            Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Right, ac.Pattern.TrafficDirection);

            bool sawUpwind = false;
            for (int t = 1; t <= MaxTicks; t++)
            {
                engine.TickOneSecond();
                var live = engine.FindAircraft(Callsign);
                Assert.NotNull(live);
                Assert.Equal("28R", live.Phases?.AssignedRunway?.Designator);
                Assert.DoesNotContain(live.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);

                if (live.Phases?.CurrentPhase is UpwindPhase upwind && sawUpwind is false)
                {
                    sawUpwind = true;
                    output.WriteLine($"upwind at +{t}s");
                    var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
                    Assert.NotNull(upwind.Waypoints);
                    Assert.True(
                        GeoMath.DistanceNm(
                            upwind.Waypoints.ThresholdLat,
                            upwind.Waypoints.ThresholdLon,
                            rwy28R.ThresholdLatitude,
                            rwy28R.ThresholdLongitude
                        ) < 0.05,
                        "the next circuit must be built on 28R's own threshold"
                    );
                    break;
                }
            }

            Assert.True(sawUpwind, $"{Callsign} never reached the next circuit's upwind within {MaxTicks} ticks");
        }
    }

    /// <summary>
    /// Pre-issued against a still-queued pattern entry (no PhaseList to stamp), the modifier rides in
    /// <see cref="PendingLandingClearance"/> — through a snapshot round trip — and reaches the circuit
    /// the entry finally builds.
    /// </summary>
    [Fact]
    public void CoptMrt28L_BehindQueuedErd_SurvivesSnapshotAndLandsOnTheCircuit()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC020");

        Assert.True(engine.SendCommand("TSC020", "DCT VPCOL; ERD 28R").Success);

        var copt = engine.SendCommand("TSC020", "COPT MRT 28L");
        output.WriteLine($"COPT MRT 28L: success={copt.Success} — {copt.Message}");
        Assert.True(copt.Success, $"COPT MRT 28L behind a queued ERD should be accepted: {copt.Message}");
        Assert.Contains("28L", copt.Message ?? "", StringComparison.Ordinal);

        var ac = engine.FindAircraft("TSC020");
        Assert.NotNull(ac);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);
        Assert.Equal(ClearanceType.ClearedForOption, restored.Pattern.PendingLandingClearance?.Clearance);
        Assert.Equal("28R", restored.Pattern.PendingLandingClearance?.RunwayId);
        Assert.Equal("28L", restored.Pattern.PendingLandingClearance?.PatternRunwayId);
        Assert.Null(restored.Pattern.PendingLandingClearance?.PatternAltitudeFt);

        engine.World.RemoveAircraft("TSC020");
        engine.World.AddAircraft(restored);

        Assert.True(engine.SendCommand("TSC020", "ERD 28R").Success);

        ac = engine.FindAircraft("TSC020");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedForOption, ac.Phases.LandingClearance);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", ac.Phases.PatternRunway?.Designator);
        Assert.Null(ac.Pattern.PendingLandingClearance);
    }

    /// <summary>
    /// A pre-issued modifier is answered before the circuit exists, so its result text cannot say
    /// whether the transition will cross the field. When the entry finally builds and the armed runway
    /// turns out not to be a close parallel, the RPO gets the notice on the warning channel instead —
    /// the AIM 4-3-5 unexpected maneuver still has to be announced somewhere.
    /// </summary>
    [Fact]
    public void PreIssuedCoptMlt_CrossingRunway_WarnsThatTheTransitionCrossesMidfield()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        if (runway28R is null)
        {
            return;
        }

        // A few miles out on the pattern side of 28R, with the option pre-issued for 1L: the entry is
        // still to come, so the modifier rides in PendingLandingClearance.
        var abeam = GeoMath.ProjectPoint(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, runway28R.TrueHeading - 90.0, 3.0);
        var ac = new AircraftState
        {
            Callsign = "N721PS",
            AircraftType = "C182",
            Position = new LatLon(abeam.Lat, abeam.Lon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.AirportElevationFt + 2000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KSFO",
                FlightRules = "VFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway28R };
        ac.Pattern.PendingLandingClearance = new PendingLandingClearance(ClearanceType.ClearedForOption, "28R", "01L", null);
        ac.Pattern.TrafficDirection = PatternDirection.Left;

        var entry = PatternCommandHandler.TryEnterPattern(
            ac,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        output.WriteLine($"entry: success={entry.Success} — {entry.Message}");
        output.WriteLine($"warnings=[{string.Join(" | ", ac.PendingWarnings)}]");
        Assert.True(entry.Success, $"the pattern entry was refused: {entry.Message}");
        Assert.Equal(ClearanceType.ClearedForOption, ac.Phases?.LandingClearance);
        Assert.Equal("01L", ac.Phases?.PatternRunway?.Designator);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("will cross midfield", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.PendingWarnings, w => w.Contains("1L", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("CrosswindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Hybrid restore: load the scenario, then drop straight into the snapshot at
    /// <paramref name="elapsedSeconds"/>. Returns null when the fixture is unavailable.
    /// </summary>
    private AircraftState? RestoreAt(SimulationEngine engine, RecordingArchive archive, int elapsedSeconds)
    {
        engine.Replay(archive.ToBaseSessionRecording(), 0);

        var snapshot = archive.ReadSnapshotAt(elapsedSeconds);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={elapsedSeconds} — skipping");
            return null;
        }

        engine.RestoreFromSnapshot(snapshot.State);

        var ac = engine.FindAircraft(Callsign);
        if (ac is null)
        {
            output.WriteLine($"{Callsign} is not in the t={elapsedSeconds} snapshot — skipping");
        }

        return ac;
    }

    /// <summary>
    /// Fly the clearance out: the touch-and-go happens on 28R, and the circuit that follows is 28L's
    /// transition — an upwind whose crosswind turn lies beyond both parallels' departure ends, no
    /// midfield crossing, and a downwind established south of 28L.
    /// </summary>
    private void FlyTheOptionAndTheTransition(SimulationEngine engine)
    {
        var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
        var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;

        bool sawTerminatorOn28R = false;
        bool transitionInstalled = false;
        bool establishedOnDownwind = false;
        AircraftState? onDownwind = null;
        Type? lastPhaseType = null;

        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;
            if (phase?.GetType() != lastPhaseType)
            {
                lastPhaseType = phase?.GetType();
                output.WriteLine(
                    $"+{t}s: {phase?.GetType().Name ?? "none"} on {ac.Phases?.AssignedRunway?.Designator} (pattern {ac.Phases?.PatternRunway?.Designator})"
                );
            }
            if (phase is TouchAndGoPhase)
            {
                sawTerminatorOn28R = true;
                // The clearance is flown on the runway it was issued for — the pattern runway only
                // takes over on the climb-out.
                Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
            }

            if (!transitionInstalled && string.Equals(ac.Phases?.AssignedRunway?.Designator, "28L", StringComparison.OrdinalIgnoreCase))
            {
                transitionInstalled = true;
                output.WriteLine($"transitioned to 28L at +{t}s during {phase?.GetType().Name}");
                Assert.True(sawTerminatorOn28R, "the transition fired before the option was flown");
                AssertTransitionInstalled(ac, rwy28L, rwy28R);
            }

            if (transitionInstalled && (phase is DownwindPhase))
            {
                onDownwind = ac;
                // Established: south of 28L (left traffic), tracking its reciprocal.
                establishedOnDownwind = (ac.TrueHeading.AbsAngleTo(rwy28L.TrueHeading.ToReciprocal()) < 20) && (CrossTrack(rwy28L, ac) < -0.2);
                if (establishedOnDownwind)
                {
                    break;
                }
            }
        }

        Assert.True(transitionInstalled, $"{Callsign} never picked up the 28L transition within {MaxTicks} ticks");
        Assert.NotNull(onDownwind);
        output.WriteLine(
            $"downwind: cross-track {CrossTrack(rwy28L, onDownwind):F3} nm (negative = south of 28L), "
                + $"hdg {onDownwind.TrueHeading.Degrees:F0}° (reciprocal {rwy28L.TrueHeading.ToReciprocal().Degrees:F0}°)"
        );
        Assert.True(
            establishedOnDownwind,
            $"never established on 28L's left downwind — last sample was {CrossTrack(rwy28L, onDownwind):F3} nm from 28L "
                + $"on heading {onDownwind.TrueHeading.Degrees:F0}°"
        );
    }

    /// <summary>
    /// The transition circuit: 28L's pattern, no midfield crossing, and an upwind whose crosswind turn
    /// point lies beyond both parallels' departure ends (AIM 4-3-2).
    /// </summary>
    private void AssertTransitionInstalled(AircraftState aircraft, RunwayInfo patternRunway, RunwayInfo otherRunway)
    {
        var chain = aircraft.Phases?.Phases ?? [];
        output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");

        Assert.DoesNotContain(chain, p => p is MidfieldCrossingPhase { Status: PhaseStatus.Pending or PhaseStatus.Active });

        var upwind = chain.OfType<UpwindPhase>().LastOrDefault();
        Assert.NotNull(upwind);
        var waypoints = upwind.Waypoints;
        Assert.NotNull(waypoints);

        double turnAlongTrack = AlongTrack(patternRunway, waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon);
        double patternDer = AlongTrack(patternRunway, patternRunway.EndLatitude, patternRunway.EndLongitude);
        double otherDer = AlongTrack(patternRunway, otherRunway.EndLatitude, otherRunway.EndLongitude);
        output.WriteLine(
            $"crosswind turn along-track={turnAlongTrack:F3} nm, {patternRunway.Designator} DER={patternDer:F3}, {otherRunway.Designator} DER={otherDer:F3}"
        );

        Assert.True(
            turnAlongTrack >= patternDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along {patternRunway.Designator}, short of its own departure end at {patternDer:F3} nm"
        );
        Assert.True(
            turnAlongTrack >= otherDer - 0.001,
            $"crosswind turn point is {turnAlongTrack:F3} nm along {patternRunway.Designator}, short of {otherRunway.Designator}'s departure end at {otherDer:F3} nm"
        );
    }

    /// <summary>
    /// A VFR aircraft on final for <paramref name="runway"/> with an approach pending — the state an
    /// option clearance is issued into, minus the recording.
    /// </summary>
    private static AircraftState OnFinalFor(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "N654TS",
            AircraftType = "DA62",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.AirportElevationFt + 800,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = runway.AirportId,
                Destination = runway.AirportId,
                FlightRules = "VFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());
        return ac;
    }

    private static void SpawnAirborneOverOak(SimulationEngine engine, string callsign)
    {
        // A few miles east of OAK 28R on the right downwind side, slow VFR piston.
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "DA62",
            Position = new LatLon(37.66, -122.16),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 2000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 150,
            },
        };
        engine.World.AddAircraft(ac);
    }

    private static double AlongTrack(RunwayInfo runway, double lat, double lon) =>
        GeoMath.AlongTrackDistanceNm(new LatLon(lat, lon), new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);

    private static double CrossTrack(RunwayInfo runway, AircraftState aircraft) =>
        GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
}
