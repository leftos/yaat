using Microsoft.Extensions.Logging;
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
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The hold after a LAHSO stop over the real SFO layout: land 28R holding short of 1L, stop at the point, and release it.
///
/// <para>
/// AIM 4-3-11.c.2 and 7110.65 §3-7-2.a.1: the aircraft stopped short of the intersecting runway crosses it only on a crossing
/// clearance. A <c>CROSS</c> of that runway (bare, or naming it) releases the hold; a <c>CROSS</c> of another runway is refused;
/// an exit instruction is stored for after the crossing.
/// </para>
/// </summary>
public class LahsoHoldReleaseTests(ITestOutputHelper output)
{
    /// <summary>The layouts under <c>TestData</c>, cached across every run in this class.</summary>
    private static readonly TestAirportGroundData GroundData = new();

    private const string AircraftType = "E75L";
    private const double ApproachSpeedKts = 135;

    /// <summary>How far (ft) past the held aircraft an exit must lie to be one beyond the hold-short point.</summary>
    private const double ExitBeyondHoldFt = 1000;

    /// <summary>The 28R exit the held aircraft is told to take once across 1L.</summary>
    private const string ExitPastHold = "D";

    private sealed record HeldRun(
        SimulationEngine Engine,
        AircraftState Aircraft,
        AirportGroundLayout Layout,
        RunwayInfo Runway,
        string CrossingRunwayId
    );

    /// <summary>
    /// Spawns the aircraft on 1 nm final for SFO 28R, clears it to land and hold short of 1L, and ticks the production loop
    /// until it is stopped in the runway hold. Null when the layout or nav data is absent (silent skip).
    /// </summary>
    private HeldRun? StageHeld()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("LandingPhase", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(GroundData, PushPlanStore.CreateShipped());

        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = AircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            TrueTrack = runway28R.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(
                GeoMath.DistanceNm(new LatLon(acLat, acLon), LandingThreshold.Resolve(runway28R, layout)),
                runway28R.ElevationFt,
                AircraftCategorization.Categorize(AircraftType)
            ),
            IndicatedAirspeed = ApproachSpeedKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };

        // The chain ends at the landing, as a cleared-to-land chain does: PhaseRunner appends the runway hold after a LAHSO stop.
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-hold",
            ScenarioName = "SFO 28R LAHSO hold release",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult lahso = engine.SendCommand("TST001", "LAHSO 1L");
        Assert.True(lahso.Success, $"LAHSO 1L failed: {lahso.Message}");
        LahsoTarget? target = aircraft.Phases.LahsoHoldShort;
        Assert.NotNull(target);
        string crossingRunwayId = target.CrossingRunwayId;

        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();
            if ((aircraft.Phases.CurrentPhase is RunwayHoldingPhase) && (aircraft.GroundSpeed < 0.5))
            {
                output.WriteLine($"held at t+{t}s: {aircraft.Phases.CurrentPhase.Name}");
                return new HeldRun(engine, aircraft, layout, runway28R, crossingRunwayId);
            }
        }

        Assert.Fail($"never stopped in the runway hold; final phase {aircraft.Phases.CurrentPhase?.Name ?? "none"}");
        return null;
    }

    /// <summary>Ticks until the runway hold ends (at most <paramref name="seconds"/>) and returns the phase that follows it.</summary>
    private static Phase? TickPastHold(HeldRun run, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            if (run.Aircraft.Phases!.CurrentPhase is not RunwayHoldingPhase)
            {
                break;
            }

            run.Engine.TickOneSecond();
        }

        return run.Aircraft.Phases!.CurrentPhase;
    }

    private static bool NamesRunway(string? targetName, string runway) =>
        (targetName is not null) && targetName.Split('/').Any(p => string.Equals(p.TrimStart('0'), runway, StringComparison.OrdinalIgnoreCase));

    /// <summary>The held runway as the controller reads it back.</summary>
    private static string HeldRunway(HeldRun run) => RunwayIdentifier.ToDisplayDesignator(run.CrossingRunwayId);

    /// <summary>The "without delay" tail an expedited clearance carries (7110.65 §3-7-2.j).</summary>
    private const string WithoutDelay = ", without delay";

    /// <summary>How many times <paramref name="value"/> appears in <paramref name="text"/>.</summary>
    private static int Occurrences(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Sends <paramref name="command"/> while held, asserts it is refused with the pilot's own "unable" and that the aircraft
    /// is still held after a tick, and returns the refusal.
    /// </summary>
    private CommandResult AssertRefusedWhileHeld(HeldRun run, string command, bool pilotUnable = true)
    {
        CommandResult result = run.Engine.SendCommand("TST001", command);
        output.WriteLine($"{command}: success={result.Success} message={result.Message}");
        Assert.False(result.Success, $"{command} was accepted");
        if (pilotUnable)
        {
            Assert.NotNull(result.PilotUnable);
        }
        else
        {
            Assert.Null(result.PilotUnable);
        }

        run.Engine.TickOneSecond();
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        return result;
    }

    /// <summary>
    /// The pilot's readback of a CROSS that releases the post-LAHSO hold: "cross runway {held}", the runway the aircraft crosses,
    /// with "without delay" when the clearance was expedited (7110.65 §3-7-2.a.3, §3-7-2.j).
    /// </summary>
    private static void AssertCrossReadback(CommandResult result, string heldRunway, bool expedited)
    {
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.Equal(CanonicalCommandType.CrossRunway, readback.Command);
        string suffix = expedited ? WithoutDelay : "";
        Assert.Equal($"cross runway {heldRunway}{suffix}", readback.Speech.Terminal);
        Assert.Equal($"cross runway {PhraseologyVerbalizer.SpellRunway(heldRunway)}{suffix}", readback.Speech.Tts);
    }

    [Theory]
    [InlineData("CROSS 1L")]
    [InlineData("CROSS 19R")]
    public void Cross_TheLahsoRunway_ReleasesTheHold(string command)
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult cross = run.Engine.SendCommand("TST001", command);
        output.WriteLine($"{command} (held runway {run.CrossingRunwayId}): success={cross.Success} message={cross.Message}");
        Assert.True(cross.Success, $"{command} refused: {cross.Message}");
        Assert.Null(run.Aircraft.Phases!.LahsoHoldShort);
        AssertCrossReadback(cross, HeldRunway(run), expedited: false);

        Phase? next = TickPastHold(run, 5);
        Assert.IsType<RunwayExitPhase>(next);
    }

    [Fact]
    public void BareCross_ReleasesTheHold()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult cross = run.Engine.SendCommand("TST001", "CROSS");
        output.WriteLine($"CROSS: success={cross.Success} message={cross.Message}");
        Assert.True(cross.Success, $"bare CROSS refused: {cross.Message}");
        Assert.Null(run.Aircraft.Phases!.LahsoHoldShort);
        AssertCrossReadback(cross, HeldRunway(run), expedited: false);

        Phase? next = TickPastHold(run, 5);
        Assert.IsType<RunwayExitPhase>(next);
    }

    [Fact]
    public void Cross_AnotherRunway_IsRefused()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult cross = AssertRefusedWhileHeld(run, "CROSS 28L");
        Assert.Contains($"Unable, holding short of runway {HeldRunway(run)}", cross.Message);
    }

    /// <summary>
    /// A CROSS naming the held runway and another one crosses two runways, and the aircraft holds only one of them: releasing the
    /// hold would drop the other runway from the instruction, so the whole command is refused.
    /// </summary>
    [Fact]
    public void Cross_TheHeldRunwayAndAnother_IsRefused()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult cross = AssertRefusedWhileHeld(run, "CROSS 1L 28L");
        Assert.Contains($"Unable, holding short of runway {HeldRunway(run)}", cross.Message);
    }

    [Theory]
    [InlineData("CROSS HS B")]
    [InlineData("CROSS 1L HS B")]
    public void Cross_CarryingAHoldShort_IsRefused_AndKeepsHolding(string command)
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        // The clearance is legal; YAAT just cannot carry it from the runway hold, so only the controller is told and the pilot
        // never says "unable" (7110.65 §3-7-2: a crossing that names a hold short is the controller's to restate).
        CommandResult cross = AssertRefusedWhileHeld(run, command, pilotUnable: false);
        Assert.Contains("say crossing clearance without hold-short instructions", cross.Message);
    }

    /// <summary>
    /// <c>CROSS … EXP</c> while held releases the hold and carries its "without delay" onto the runway exit that follows, the
    /// field <c>RunwayExitPhase</c> reads for a max-effort exit. The hold has no route to carry the crossing, so the release reads
    /// the suffix back itself, whatever the command's shape: exactly once, never twice.
    /// </summary>
    [Theory]
    [InlineData("CROSS EXP")]
    [InlineData("CROSS 1L EXP")]
    [InlineData("CROSS 1L 19R EXP")]
    public void Cross_Expedite_ReleasesTheHoldAndExpeditesTheExit(string command)
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult cross = run.Engine.SendCommand("TST001", command);
        output.WriteLine($"{command}: success={cross.Success} message={cross.Message}");
        Assert.True(cross.Success, $"{command} refused: {cross.Message}");
        Assert.Null(run.Aircraft.Phases!.LahsoHoldShort);
        Assert.True(run.Aircraft.Ground.IsExpeditingExit, $"{command} did not expedite the exit");
        string message = cross.Message ?? string.Empty;
        Assert.EndsWith(WithoutDelay, message);
        Assert.Equal(1, Occurrences(message, WithoutDelay));
        AssertCrossReadback(cross, HeldRunway(run), expedited: true);

        Phase? next = TickPastHold(run, 5);
        Assert.IsType<RunwayExitPhase>(next);
    }

    /// <summary>
    /// <c>EL C</c> while held is accepted by the hold phase itself (not by clearing it), is stored for after the crossing, and lets
    /// the aircraft keep holding. The pilot reads its exit back with the hold it leaves the aircraft in (7110.65 §3-10-9.d).
    /// </summary>
    [Fact]
    public void ExitLeft_WhileHeld_IsAllowedAndStored()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        RunwayHoldingPhase hold = Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Equal(CommandAcceptance.Allowed, hold.CanAcceptCommand(CanonicalCommandType.ExitLeft));

        CommandResult exit = run.Engine.SendCommand("TST001", "EL C");
        output.WriteLine($"EL C: success={exit.Success} message={exit.Message}");
        Assert.True(exit.Success, $"EL C refused: {exit.Message}");
        Assert.Contains($"stored; holding short of runway {HeldRunway(run)} until cleared to cross", exit.Message);

        HandlerReadback readback = Assert.IsType<HandlerReadback>(exit.PilotReadback);
        Assert.Equal(CanonicalCommandType.ExitLeft, readback.Command);
        Assert.EndsWith($"holding short of runway {HeldRunway(run)}", readback.Speech.Terminal);
        Assert.EndsWith($"holding short of runway {PhraseologyVerbalizer.SpellRunway(HeldRunway(run))}", readback.Speech.Tts);

        for (int t = 0; t < 5; t++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Equal(ExitSide.Left, run.Aircraft.Phases.RequestedExit?.Side);
        Assert.Equal("C", run.Aircraft.Phases.RequestedExit?.Taxiway);
    }

    [Fact]
    public void Exit_WhileHeld_IsStoredAndKeepsHolding()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        string exit = PickExitBeyondHold(run);

        CommandResult exitResult = run.Engine.SendCommand("TST001", $"EXIT {exit}");
        output.WriteLine($"EXIT {exit}: success={exitResult.Success} message={exitResult.Message}");
        Assert.True(exitResult.Success, $"EXIT {exit} refused: {exitResult.Message}");
        Assert.Contains(
            $"stored; holding short of runway {RunwayIdentifier.ToDisplayDesignator(run.CrossingRunwayId)} until cleared to cross",
            exitResult.Message
        );

        for (int t = 0; t < 5; t++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Equal(exit, run.Aircraft.Phases.RequestedExit?.Taxiway);

        CommandResult cross = run.Engine.SendCommand("TST001", "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L refused: {cross.Message}");

        string? exitTaxiway = null;
        for (int t = 0; t < 300; t++)
        {
            run.Engine.TickOneSecond();
            Phase? current = run.Aircraft.Phases.CurrentPhase;
            if (current is RunwayExitPhase or HoldingAfterExitPhase)
            {
                exitTaxiway = run.Aircraft.Ground.CurrentTaxiway ?? exitTaxiway;
            }

            if (current is HoldingAfterExitPhase)
            {
                break;
            }
        }

        output.WriteLine($"after CROSS: phase={run.Aircraft.Phases.CurrentPhase?.Name ?? "none"} taxiway={exitTaxiway ?? "(none)"}");
        Assert.IsType<HoldingAfterExitPhase>(run.Aircraft.Phases.CurrentPhase);
        Assert.Equal(exit, exitTaxiway);
    }

    /// <summary>
    /// The 28R exit past the LAHSO hold-short point the test names, checked against the layout's exits (a runway hold-short
    /// node of a taxiway leaving the runway) to lie well past the held aircraft. Prints every candidate.
    /// </summary>
    private string PickExitBeyondHold(HeldRun run)
    {
        LatLon threshold = LandingThreshold.Resolve(run.Runway, run.Layout);
        double heldFt = GeoMath.AlongTrackDistanceNm(run.Aircraft.Position, threshold, run.Runway.TrueHeading) * GeoMath.FeetPerNm;
        output.WriteLine($"held {heldFt:F0} ft from the 28R threshold");

        var candidates = new List<(string Taxiway, double AlongFt)>();
        foreach (GroundNode node in run.Layout.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.RunwayHoldShort) || !NamesRunway(node.RunwayId?.ToString(), "28R"))
            {
                continue;
            }

            string? taxiway = node.Edges.Select(e => e.TaxiwayName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            if (taxiway is null)
            {
                continue;
            }

            double alongFt = GeoMath.AlongTrackDistanceNm(node.Position, threshold, run.Runway.TrueHeading) * GeoMath.FeetPerNm;
            output.WriteLine($"  candidate {taxiway} node #{node.Id} at {alongFt:F0} ft");
            candidates.Add((taxiway, alongFt));
        }

        // D: the first right-angle exit past the 1L crossing (T, nearer, is a high-speed exit leaving from inside the crossing).
        Assert.Contains(candidates, c => (c.Taxiway == ExitPastHold) && (c.AlongFt > heldFt + ExitBeyondHoldFt));
        return ExitPastHold;
    }
}
