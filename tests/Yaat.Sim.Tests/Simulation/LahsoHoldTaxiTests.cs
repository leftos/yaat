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
/// A <c>TAXI</c> to an aircraft holding on the runway after a LAHSO stop, over the real SFO layout: land 28R holding short of 1L,
/// stop at the point, then taxi (YAAT-1944).
///
/// <para>
/// AIM 4-3-11.c.2 and 7110.65 §3-7-2.a.1: the aircraft stopped short of the intersecting runway crosses it only on a crossing
/// clearance. A <c>TAXI</c> whose route crosses the held runway is stored, as a <c>TAXI</c> given during the rollout is, and the
/// aircraft keeps holding; <c>CROSS</c> of that runway releases the hold and the stored route runs. A <c>TAXI</c> carrying
/// <c>CROSS</c> of the held runway proceeds at once; one whose route never enters it runs at once and ends the hold.
/// </para>
/// </summary>
public class LahsoHoldTaxiTests(ITestOutputHelper output)
{
    /// <summary>The layouts under <c>TestData</c>, cached across every run in this class.</summary>
    private static readonly TestAirportGroundData GroundData = new();

    private const string AircraftType = "E75L";
    private const double ApproachSpeedKts = 135;

    /// <summary>The taxiway the stored route leaves 28R on, past the 1L crossing.</summary>
    private const string TaxiwayPastHold = "E";

    /// <summary>How far (ft) the held aircraft may move while it keeps holding.</summary>
    private const double HeldDriftFt = 5;

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

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .InitializeSimLog();
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

        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-hold-taxi",
            ScenarioName = "SFO 28R LAHSO hold taxi",
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

    /// <summary>The held runway as the controller reads it back.</summary>
    private static string HeldRunway(HeldRun run) => RunwayIdentifier.ToDisplayDesignator(run.CrossingRunwayId);

    /// <summary>The signed cross-track distance (nm) of <paramref name="position"/> from the held runway's centerline.</summary>
    private static double SideOfHeldRunway(HeldRun run, LatLon position)
    {
        RunwayInfo? held = NavigationDatabase.Instance.GetRunway("SFO", HeldRunway(run));
        Assert.NotNull(held);
        return GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(held.ThresholdLatitude, held.ThresholdLongitude), held.TrueHeading);
    }

    private CommandResult Send(HeldRun run, string command)
    {
        CommandResult result = run.Engine.SendCommand("TST001", command);
        string readback = result.PilotReadback is HandlerReadback handler ? handler.Speech.Terminal : "(none)";
        output.WriteLine($"{command}: success={result.Success} message={result.Message} readback={readback}");
        return result;
    }

    /// <summary>
    /// A TAXI that is stored keeps the hold: success, still <see cref="RunwayHoldingPhase"/>, the route on
    /// <see cref="PhaseList.TaxiRoute"/>, the pilot reading back the hold it stays in, and the aircraft at rest for 30 s.
    /// </summary>
    private void AssertStoredAndHolding(HeldRun run, CommandResult result, string command)
    {
        Assert.True(result.Success, $"{command} refused: {result.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.NotNull(run.Aircraft.Phases.TaxiRoute);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.EndsWith($"holding short of runway {HeldRunway(run)}", readback.Speech.Terminal);
        Assert.EndsWith($"holding short of runway {PhraseologyVerbalizer.SpellRunway(HeldRunway(run))}", readback.Speech.Tts);

        LatLon rest = run.Aircraft.Position;
        for (int t = 0; t < 30; t++)
        {
            run.Engine.TickOneSecond();
        }

        double driftFt = GeoMath.DistanceNm(rest, run.Aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after 30 s: phase={run.Aircraft.Phases.CurrentPhase?.Name ?? "none"} drift={driftFt:F1} ft");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases.CurrentPhase);
        Assert.True(driftFt <= HeldDriftFt, $"moved {driftFt:F1} ft while holding");
        Assert.NotNull(run.Aircraft.Phases.TaxiRoute);
    }

    /// <summary>
    /// Ticks until the aircraft taxis its route (a <see cref="TaxiingPhase"/> or a <see cref="HoldingShortPhase"/> on it), at
    /// most 400 s, and asserts it crossed the held runway and is on <see cref="TaxiwayPastHold"/>.
    /// </summary>
    private void AssertCrossesAndTaxisOnE(HeldRun run)
    {
        double startSide = SideOfHeldRunway(run, run.Aircraft.Position);
        for (int t = 0; t < 400; t++)
        {
            run.Engine.TickOneSecond();
            if (run.Aircraft.Phases!.CurrentPhase is TaxiingPhase or HoldingShortPhase)
            {
                break;
            }
        }

        Phase? current = run.Aircraft.Phases!.CurrentPhase;
        double endSide = SideOfHeldRunway(run, run.Aircraft.Position);
        output.WriteLine(
            $"after release: phase={current?.Name ?? "none"} taxiway={run.Aircraft.Ground.CurrentTaxiway ?? "(none)"} side {startSide:F4} -> {endSide:F4}"
        );
        Assert.True(current is TaxiingPhase or HoldingShortPhase, $"not taxiing the route: {current?.Name ?? "none"}");
        Assert.True((startSide * endSide) < 0, "the aircraft did not cross the held runway");
        Assert.Equal(TaxiwayPastHold, run.Aircraft.Ground.CurrentTaxiway);
    }

    [Fact]
    public void TaxiE_FromTheLahsoHold_IsStoredAndKeepsHolding()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold}");
        AssertStoredAndHolding(run, taxi, $"TAXI {TaxiwayPastHold}");
    }

    [Fact]
    public void TaxiE_ThenCross1L_RunsTheStoredRouteAcross1L()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold}");
        AssertStoredAndHolding(run, taxi, $"TAXI {TaxiwayPastHold}");

        CommandResult cross = Send(run, "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L refused: {cross.Message}");
        AssertCrossesAndTaxisOnE(run);
    }

    [Fact]
    public void TaxiECross1L_FromTheLahsoHold_ProceedsAtOnce()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold} CROSS 1L");
        Assert.True(taxi.Success, $"TAXI {TaxiwayPastHold} CROSS 1L refused: {taxi.Message}");
        Assert.Null(run.Aircraft.Phases!.LahsoHoldShort);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        Assert.Contains($"cross runway {HeldRunway(run)}", readback.Speech.Terminal);

        run.Engine.TickOneSecond();
        Assert.IsNotType<RunwayHoldingPhase>(run.Aircraft.Phases.CurrentPhase);
        AssertCrossesAndTaxisOnE(run);
    }

    [Fact]
    public void TaxiAuto_FromTheLahsoHold_IsStored()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, "TAXIAUTO @B2");
        AssertStoredAndHolding(run, taxi, "TAXIAUTO @B2");
    }

    [Fact]
    public void Taxi_RouteNotEntering1L_RunsAtOnceAndEndsTheHold()
    {
        HeldRun? run = StageHeld();
        if (run is null)
        {
            return;
        }

        string behind = ExitBehindHold(run);
        double startSide = SideOfHeldRunway(run, run.Aircraft.Position);
        CommandResult taxi = Send(run, $"TAXI {behind}");
        Assert.True(taxi.Success, $"TAXI {behind} refused: {taxi.Message}");
        Assert.IsType<TaxiingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Phases.TaxiRoute);

        for (int t = 0; t < 120; t++)
        {
            run.Engine.TickOneSecond();
            Assert.True((startSide * SideOfHeldRunway(run, run.Aircraft.Position)) > 0, $"crossed the held runway at t+{t}s");
        }
    }

    /// <summary>The nearest 28R exit behind the held aircraft (toward the landing threshold). Prints every candidate.</summary>
    private string ExitBehindHold(HeldRun run)
    {
        LatLon threshold = LandingThreshold.Resolve(run.Runway, run.Layout);
        double heldFt = GeoMath.AlongTrackDistanceNm(run.Aircraft.Position, threshold, run.Runway.TrueHeading) * GeoMath.FeetPerNm;
        output.WriteLine($"held {heldFt:F0} ft from the 28R threshold");

        var candidates = new List<(string Taxiway, double AlongFt)>();
        foreach (GroundNode node in run.Layout.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.RunwayHoldShort) || (node.RunwayId is not { } id) || !id.Contains("28R"))
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

        (string Taxiway, double AlongFt) behind = candidates.Where(c => c.AlongFt < heldFt).MaxBy(c => c.AlongFt);
        Assert.NotNull(behind.Taxiway);
        return behind.Taxiway;
    }
}
