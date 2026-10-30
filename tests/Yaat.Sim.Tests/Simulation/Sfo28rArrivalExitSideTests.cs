using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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
/// SFO 28R arrivals exit toward where they park (YAAT-598). A parking remark (<c>/V/ PARKING SIGNATURE, 28R</c>) gives the side of
/// 28R the parking lies on; without one, the KSFO sidecar sends GA (a registration callsign, or a listed GA operator) and cargo
/// operators right; every other arrival keeps the runway's default, left. A controller's <c>EL</c>/<c>ER</c> still wins. The
/// final-approach forecast, the landing rollout and the runway exit resolve the same side, and a snapshot restore re-derives it.
/// Real SFO layout, navdata, the shipped ZOA sidecar and the ZOA scenario remark strings.
/// </summary>
public class Sfo28rArrivalExitSideTests(ITestOutputHelper output)
{
    private const string Signature = "/V/ PARKING SIGNATURE, 28R";
    private const string Plot9 = "/V/ PARKING PLOT 9, 28R";
    private const string GateF14 = "/V/ GATE F14, 28R";

    /// <summary>
    /// The right-side 90° exit the heavy cargo arrival takes once it has slowed to a normal roll speed: D, to the bar at node 832
    /// (the layout's 28R exits: Q and T are high-speed and left-only, every right-side exit is 90° but E).
    /// </summary>
    private const string HeavyCargoRightExit = "D";

    private static readonly string[] LeftHighSpeedExits = ["Q", "T"];

    private static ShortFinalArrival.Spawned? Spawn(string callsign, string type, string remarks, double distNm)
    {
        if (ShortFinalArrival.SpawnClearedToLand("SFO", "28R", type, callsign, distNm) is not { } spawned)
        {
            return null;
        }

        spawned.Aircraft.FlightPlan.ReplaceRemarks(remarks);
        return spawned;
    }

    private static IReadOnlyList<ExitAheadDto> Forecast(AircraftState aircraft, AirportGroundLayout layout) =>
        Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(FinalApproachExitForecast.ListExitsAhead(aircraft, layout, null, 0));

    private static bool Lists(IReadOnlyList<ExitAheadDto> list, string taxiway, ExitSide side) =>
        list.Any(row => (row.Side == side) && string.Equals(row.Taxiway, taxiway, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first taxiway the forecast lists on both sides, so the planned row's side is the side the crew expects.</summary>
    private static string CrossingTaxiway(IReadOnlyList<ExitAheadDto> list) =>
        list.Select(row => row.Taxiway).First(taxiway => Lists(list, taxiway, ExitSide.Left) && Lists(list, taxiway, ExitSide.Right));

    /// <summary>The planned row of the forecast with an <c>EXIT</c> on <paramref name="taxiway"/> carrying <paramref name="side"/>.</summary>
    private static ExitAheadDto PlannedRow(AircraftState aircraft, AirportGroundLayout layout, string taxiway, ExitSide? side)
    {
        aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = taxiway, Side = side };
        return Assert.Single(Forecast(aircraft, layout), row => row.Planned);
    }

    private static PhaseContext GroundContext(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout layout) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

    /// <summary>The arrival rolling on 28R 3,000 ft past the landing threshold at 60 kt, with the controller's side if any.</summary>
    private static (AircraftState Aircraft, PhaseContext Ctx)? StageOnTheRunway(
        string callsign,
        string type,
        string remarks,
        ExitSide? controllerSide
    )
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        if ((layout is null) || (runway is null))
        {
            return null;
        }

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = GeoMath.ProjectPoint(LandingThreshold.Resolve(runway, layout), runway.TrueHeading, 3000 / GeoMath.FeetPerNm),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 60,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "SFO" },
            Phases = new PhaseList
            {
                AssignedRunway = runway,
                RequestedExit = controllerSide is { } side ? new ExitPreference { Side = side } : null,
            },
        };
        aircraft.FlightPlan.ReplaceRemarks(remarks);
        aircraft.Ground.Layout = layout;
        return (aircraft, GroundContext(aircraft, runway, layout));
    }

    private sealed record LandedExit(string? Taxiway, ExitSide Side, ExitSide? LandingInferredSide);

    /// <summary>Flies the spawned arrival to its runway exit and reports the taxiway, the side of 28R it holds on, and the landing's side.</summary>
    private LandedExit FlyToTheExit(ShortFinalArrival.Spawned spawned)
    {
        (SimulationEngine engine, AircraftState aircraft, RunwayInfo runway) = spawned;
        LatLon threshold = LandingThreshold.Resolve(runway, aircraft.Ground.Layout);
        ExitSide? landingSide = null;
        bool sawLanding = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if ((aircraft.Phases?.CurrentPhase is LandingPhase landing) && !sawLanding)
            {
                sawLanding = true;
                landingSide = landing.InferredSide;
            }

            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                double crossTrackFt = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading) * GeoMath.FeetPerNm;
                output.WriteLine($"{aircraft.Callsign}: clear at {aircraft.Ground.CurrentTaxiway} after {t} s, {crossTrackFt:F0} ft right of 28R");
                return new LandedExit(aircraft.Ground.CurrentTaxiway, crossTrackFt > 0 ? ExitSide.Right : ExitSide.Left, landingSide);
            }
        }

        Assert.Fail($"{aircraft.Callsign} never cleared 28R within 300 s; phase {aircraft.Phases?.CurrentPhase?.Name}");
        return null!;
    }

    /// <summary>On final, an <c>EXIT</c> on a taxiway that crosses 28R is planned on the side the arrival resolves.</summary>
    [Theory]
    [InlineData("EJA791", "CL30", Signature, null, ExitSide.Right)]
    [InlineData("N377L", "PC12", "", null, ExitSide.Right)]
    [InlineData("ATN3303", "B763", Plot9, null, ExitSide.Left)]
    [InlineData("ATN3303", "B763", "", null, ExitSide.Right)]
    [InlineData("UAL2164", "B738", GateF14, null, ExitSide.Left)]
    [InlineData("EJA791", "CL30", Signature, ExitSide.Left, ExitSide.Left)]
    [InlineData("EJA791", "CL30", "", null, ExitSide.Right)]
    public void Forecast_PlansTheExitOnTheResolvedSide(string callsign, string type, string remarks, ExitSide? controllerSide, ExitSide expected)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (Spawn(callsign, type, remarks, 3.0) is not { } spawned)
        {
            return;
        }

        AirportGroundLayout layout = spawned.Aircraft.Ground.Layout!;
        string crossing = CrossingTaxiway(Forecast(spawned.Aircraft, layout));

        ExitAheadDto planned = PlannedRow(spawned.Aircraft, layout, crossing, controllerSide);

        Assert.Equal((crossing, expected), (planned.Taxiway, planned.Side));
    }

    /// <summary>Landed with no exit instruction (or only the controller's side), the arrival clears 28R on the resolved side.</summary>
    [Theory]
    [InlineData("EJA791", "CL30", Signature, null, ExitSide.Right)]
    [InlineData("N377L", "PC12", "", null, ExitSide.Right)]
    [InlineData("ATN3303", "B763", Plot9, null, ExitSide.Left)]
    [InlineData("ATN3303", "B763", "", null, ExitSide.Right)]
    [InlineData("UAL2164", "B738", GateF14, null, ExitSide.Left)]
    [InlineData("EJA791", "CL30", Signature, ExitSide.Left, ExitSide.Left)]
    [InlineData("EJA791", "CL30", "", null, ExitSide.Right)]
    public void Rollout_ClearsTheRunwayOnTheResolvedSide(string callsign, string type, string remarks, ExitSide? controllerSide, ExitSide expected)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (Spawn(callsign, type, remarks, 1.0) is not { } spawned)
        {
            return;
        }

        if (controllerSide is { } side)
        {
            string command = side == ExitSide.Left ? "EL" : "ER";
            CommandResult result = spawned.Engine.SendCommand(callsign, command);
            Assert.True(result.Success, $"{command} failed: {result.Message}");
        }

        LandedExit exit = FlyToTheExit(spawned);

        Assert.Equal(controllerSide is null ? expected : null, exit.LandingInferredSide);
        Assert.Equal(expected, exit.Side);
    }

    /// <summary>The runway exit, started on its own on 28R, resolves the same side the landing does.</summary>
    [Theory]
    [InlineData("EJA791", "CL30", Signature, null, ExitSide.Right)]
    [InlineData("N377L", "PC12", "", null, ExitSide.Right)]
    [InlineData("ATN3303", "B763", Plot9, null, ExitSide.Left)]
    [InlineData("ATN3303", "B763", "", null, ExitSide.Right)]
    [InlineData("UAL2164", "B738", GateF14, null, ExitSide.Left)]
    [InlineData("EJA791", "CL30", Signature, ExitSide.Left, ExitSide.Left)]
    [InlineData("EJA791", "CL30", "", null, ExitSide.Right)]
    public void RunwayExit_ResolvesTheSameSideAsTheLanding(string callsign, string type, string remarks, ExitSide? controllerSide, ExitSide expected)
    {
        if (StageOnTheRunway(callsign, type, remarks, controllerSide) is not { } staged)
        {
            return;
        }

        var landing = new LandingPhase();
        landing.OnStart(staged.Ctx);
        var exit = new RunwayExitPhase();
        exit.OnStart(staged.Ctx);

        Assert.Equal(controllerSide is null ? expected : null, landing.InferredSide);
        RunwayExitPhaseDto dto = Assert.IsType<RunwayExitPhaseDto>(exit.ToSnapshot());
        Assert.Equal((int)expected, dto.LastResolvedPreference);
    }

    /// <summary>
    /// A snapshot of the arrival on final restores to the same planned side, re-derived from the restored remarks; a snapshot of
    /// its rollout restores the landing's side.
    /// </summary>
    [Fact]
    public void SnapshotRoundTrip_KeepsTheResolvedSide()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (Spawn("EJA791", "CL30", Signature, 3.0) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        string crossing = CrossingTaxiway(Forecast(aircraft, layout));
        Assert.Equal(ExitSide.Right, PlannedRow(aircraft, layout, crossing, null).Side);

        AircraftState onFinal = RoundTrip(aircraft, layout);
        Assert.Equal(ExitSide.Right, Assert.Single(Forecast(onFinal, layout), row => row.Planned).Side);

        for (int t = 0; (t < 180) && (aircraft.Phases?.CurrentPhase is not LandingPhase { CurrentState: LandingPhase.State.Rollout }); t++)
        {
            spawned.Engine.TickOneSecond();
        }

        Assert.Equal(ExitSide.Right, Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase).InferredSide);
        AircraftState rollingOut = RoundTrip(aircraft, layout);
        Assert.Equal(ExitSide.Right, Assert.IsType<LandingPhase>(rollingOut.Phases?.CurrentPhase).InferredSide);
    }

    private static AircraftState RoundTrip(AircraftState aircraft, AirportGroundLayout layout)
    {
        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        return AircraftState.FromSnapshot(dto, layout);
    }

    /// <summary>
    /// The right-exiting heavy cargo arrival has no high-speed exit on its side: it slows to a normal roll speed and takes a 90°
    /// right exit rather than the left high-speed exits Q and T.
    /// </summary>
    [Fact]
    public void HeavyCargo_TakesARight90DegreeExit_NotALeftHighSpeedExit()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (Spawn("ATN3303", "B763", "", 1.0) is not { } spawned)
        {
            return;
        }

        LandedExit exit = FlyToTheExit(spawned);

        Assert.DoesNotContain(exit.Taxiway, LeftHighSpeedExits);
        Assert.Equal((HeavyCargoRightExit, ExitSide.Right), (exit.Taxiway, exit.Side));
    }

    private static AircraftState Arrival(string callsign, string remarks)
    {
        var aircraft = new AircraftState { Callsign = callsign, AircraftType = "B738" };
        aircraft.FlightPlan.ReplaceRemarks(remarks);
        return aircraft;
    }

    private static (RunwayInfo Runway, AirportGroundLayout Layout)? Sfo(string designator)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", designator);
        return (layout is null) || (runway is null) ? null : (runway, layout);
    }

    /// <summary>
    /// A remark whose stand the layout lacks falls back to its area, then to the class rule, then to the runway's default; an
    /// area the sidecar's <c>parkingAreas</c> does not match (<c>PLOT</c> with no number, <c>PLOT9A</c>, <c>CUTTER</c>) falls
    /// through the same way.
    /// </summary>
    [Theory]
    [InlineData("EJA791", "/v/ Parking Signature (SIG99), 28R", ExitSide.Right)]
    [InlineData("ATN3303", "/v/ Parking Plot 9 (9-5A), 28R", ExitSide.Left)]
    [InlineData("ATN3303", "/V/ PARKING PLOT, 28R", ExitSide.Right)]
    [InlineData("ATN3303", "/V/ PARKING PLOT9A, 28R", ExitSide.Right)]
    [InlineData("N377L", "/V/ GATE Z99, 28R", ExitSide.Right)]
    [InlineData("UAL2164", "/V/ GATE Z99, 28R", ExitSide.Left)]
    [InlineData("UAL2164", "/V/ PARKING AT CUTTER, 28R", ExitSide.Left)]
    [InlineData("EJA791", "", ExitSide.Right)]
    public void UnresolvedParking_FallsBackToTheAreaThenTheClassThenTheDefault(string callsign, string remarks, ExitSide expected)
    {
        if (Sfo("28R") is not { } sfo)
        {
            return;
        }

        Assert.Equal(expected, ArrivalExitSide.Resolve(Arrival(callsign, remarks), sfo.Runway, sfo.Layout, sfo.Runway.TrueHeading));
    }

    /// <summary>A class-keyed <c>exitDirections</c> entry beats a class-less one for the same runway end.</summary>
    [Fact]
    public void ClassEntry_BeatsTheClassLessEntryForTheSameEnd()
    {
        if (Sfo("28R") is not { } sfo)
        {
            return;
        }

        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KSFO")
            {
                ExitDirections =
                [
                    new ExitDirectionOverride("28R", null, ExitSide.Left, null),
                    new ExitDirectionOverride("28R", ArrivalClass.GeneralAviation, ExitSide.Right, null),
                ],
            },
        ]);

        Assert.Equal(ExitSide.Right, ArrivalExitSide.Resolve(Arrival("N377L", ""), sfo.Runway, sfo.Layout, sfo.Runway.TrueHeading, catalog));
        Assert.Equal(ExitSide.Left, ArrivalExitSide.Resolve(Arrival("UAL2164", ""), sfo.Runway, sfo.Layout, sfo.Runway.TrueHeading, catalog));
    }

    /// <summary>
    /// Parking whose centroid lies within 250 ft of the runway's extended centreline is neither left nor right of it: the remark
    /// gives no side and the class rule decides, even against the side the few feet of offset would give. SFO has no single
    /// stand that close to a centreline (the nearest is 723 ft), so the area is one stand right of 28R and one left of it whose
    /// centroid is.
    /// </summary>
    [Fact]
    public void ParkingInsideTheDeadBand_FallsThroughToTheClassRule()
    {
        if (Sfo("28R") is not { } sfo)
        {
            return;
        }

        LatLon threshold = new(sfo.Runway.ThresholdLatitude, sfo.Runway.ThresholdLongitude);
        List<(string Name, double CrossTrackFt)> stands =
        [
            .. sfo
                .Layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
                .Select(n => (n.Name!, GeoMath.SignedCrossTrackDistanceNm(n.Position, threshold, sfo.Runway.TrueHeading) * GeoMath.FeetPerNm)),
        ];
        ((string Name, double CrossTrackFt) Right, (string Name, double CrossTrackFt) Left) pair = (
            from right in stands
            where right.CrossTrackFt > ArrivalExitSide.ParkingDeadBandFt
            from left in stands
            where left.CrossTrackFt < -ArrivalExitSide.ParkingDeadBandFt
            orderby Math.Abs(right.CrossTrackFt + left.CrossTrackFt - 200)
            select (right, left)
        ).First();
        double centroidFt = (pair.Right.CrossTrackFt + pair.Left.CrossTrackFt) / 2;
        output.WriteLine(
            $"{pair.Right.Name} {pair.Right.CrossTrackFt:F0} ft, {pair.Left.Name} {pair.Left.CrossTrackFt:F0} ft: centroid {centroidFt:F0} ft"
        );
        Assert.InRange(centroidFt, 1, ArrivalExitSide.ParkingDeadBandFt - 1);

        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KSFO")
            {
                ExitDirections = [new ExitDirectionOverride("28R", ArrivalClass.GeneralAviation, ExitSide.Left, null)],
                ParkingAreas =
                [
                    new ParkingAreaAlias("TESTPAIR", [pair.Right.Name, pair.Left.Name], null),
                    new ParkingAreaAlias("TESTRIGHT", [pair.Right.Name], null),
                ],
            },
        ]);

        AircraftState inBand = Arrival("N377L", "/V/ PARKING TESTPAIR, 28R");
        AircraftState clear = Arrival("N377L", "/V/ PARKING TESTRIGHT, 28R");
        Assert.Equal(ExitSide.Left, ArrivalExitSide.ForArrival(inBand, sfo.Runway, sfo.Layout, catalog));
        Assert.Equal(ExitSide.Right, ArrivalExitSide.ForArrival(clear, sfo.Runway, sfo.Layout, catalog));
    }

    /// <summary>
    /// A runway exit restored from a snapshot keeps the side it expected to exit on, so a bare <c>EXIT &lt;twy&gt;</c> issued
    /// after the restore turns off on that side, as it would have live.
    /// </summary>
    [Fact]
    public void RunwayExit_RestoredMidExit_KeepsTheExpectedSideForABareExit()
    {
        if (StageOnTheRunway("EJA791", "CL30", "", null) is not { } live || (StageOnTheRunway("EJA791", "CL30", "", null) is not { } restored))
        {
            return;
        }

        var livePhase = new RunwayExitPhase();
        livePhase.OnStart(live.Ctx);
        RunwayExitPhaseDto dto = Assert.IsType<RunwayExitPhaseDto>(livePhase.ToSnapshot());
        Assert.Equal((int)ExitSide.Right, dto.InferredSideValue);

        string json = JsonSerializer.Serialize<PhaseDto>(dto, RecordingJsonOptions.Default);
        RunwayExitPhaseDto restoredDto = Assert.IsType<RunwayExitPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restoredPhase = RunwayExitPhase.FromSnapshot(restoredDto, restored.Ctx.GroundLayout);

        ExitSide liveSide = BareExitSide(livePhase, live.Aircraft, live.Ctx);
        ExitSide restoredSide = BareExitSide(restoredPhase, restored.Aircraft, restored.Ctx);

        Assert.Equal((ExitSide.Right, ExitSide.Right), (liveSide, restoredSide));
    }

    /// <summary>The side of 28R the exit a bare <c>EXIT K</c> commits <paramref name="phase"/> to lies on, after one tick.</summary>
    private static ExitSide BareExitSide(RunwayExitPhase phase, AircraftState aircraft, PhaseContext ctx)
    {
        aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = "K" };
        phase.OnTick(ctx);
        RunwayExitPhaseDto dto = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        GroundNode holdShort = ctx.GroundLayout!.Nodes[Assert.NotNull(dto.ExitNodeId)];
        RunwayInfo runway = ctx.Runway!;
        double crossTrackNm = GeoMath.SignedCrossTrackDistanceNm(
            holdShort.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading
        );
        return crossTrackNm > 0 ? ExitSide.Right : ExitSide.Left;
    }
}
