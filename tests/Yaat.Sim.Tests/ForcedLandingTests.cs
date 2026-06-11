using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

/// <summary>
/// CLANDF (forced landing) instructor/RPO override. Verifies that with
/// <see cref="PhaseList.ForceLanding"/> set, FinalApproachPhase and LandingPhase suppress
/// every automatic go-around and drive the aircraft to a touchdown even from an unflyable
/// energy state, while the same setup without the flag still goes around (negative control).
///
/// Drives the production path: SimulationWorld.Tick + a preTick callback running
/// PhaseRunner.Tick, exactly as SimulationEngine.TickPhysics does.
/// </summary>
[Collection("NavDbMutator")]
public class ForcedLandingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public ForcedLandingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeRunway() =>
        TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100, thresholdLat: 37.0, thresholdLon: -122.0);

    /// <summary>Aircraft on the extended centerline at <paramref name="distNm"/>, AGL above threshold.</summary>
    private static AircraftState MakeAircraftOnFinal(RunwayInfo rwy, double distNm, double agl, double ias)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var startPos = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), distNm);
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = startPos,
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + agl,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST" },
        };
    }

    // Clearance granted for most arms so the test isolates the forced-descent / go-around-
    // suppression behavior from the no-clearance triggers. The no-clearance suppression test
    // uses PreTickNoAutoClear to exercise triggers B/C directly.
    private static void PreTick(AircraftState aircraft, double dt) => TickPhases(aircraft, dt, autoClearedToLand: true);

    private static void PreTickNoAutoClear(AircraftState aircraft, double dt) => TickPhases(aircraft, dt, autoClearedToLand: false);

    private static void TickPhases(AircraftState aircraft, double dt, bool autoClearedToLand)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return;
        }

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = dt,
            Runway = aircraft.Phases.AssignedRunway,
            FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
            Logger = NullLogger.Instance,
            AutoClearedToLand = autoClearedToLand,
        };
        PhaseRunner.Tick(aircraft, ctx);
    }

    private readonly record struct RunResult(bool Landed, bool WentAround);

    /// <summary>Tick at production cadence (4 Hz) until the aircraft lands, goes around, or the budget expires.</summary>
    private RunResult Run(SimulationWorld world, AircraftState ac, Action<AircraftState, double> preTick, int maxSeconds = 120)
    {
        const double dt = 0.25;
        int maxTicks = maxSeconds * 4;
        for (int i = 0; i < maxTicks; i++)
        {
            world.Tick(dt, preTick);
            world.DrainAllWarnings();

            if (ac.Phases?.CurrentPhase is GoAroundPhase)
            {
                _output.WriteLine($"went around at tick {i} (t={i * dt:F1}s), alt={ac.Altitude:F0}");
                return new RunResult(Landed: false, WentAround: true);
            }
            if (ac.IsOnGround && ac.CompletionReason == CompletionReason.Landed)
            {
                _output.WriteLine($"landed at tick {i} (t={i * dt:F1}s)");
                return new RunResult(Landed: true, WentAround: false);
            }
        }
        _output.WriteLine($"budget expired: onGround={ac.IsOnGround}, alt={ac.Altitude:F0}, phase={ac.Phases?.CurrentPhase?.GetType().Name}");
        return new RunResult(Landed: ac.IsOnGround, WentAround: false);
    }

    [Fact]
    public void ForcedLanding_TooHighOnFinal_LandsInsteadOfGoingAround()
    {
        var rwy = MakeRunway();
        // 3000 ft high at 2 nm — impossible to make on a normal glidepath, so without the
        // override the too-high-at-MAP trigger fires a go-around.
        var ac = MakeAircraftOnFinal(rwy, distNm: 2.0, agl: 3000, ias: 220);
        ac.Phases = new PhaseList { AssignedRunway = rwy, ForceLanding = true };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ac.IndicatedAirspeed;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var result = Run(world, ac, PreTick);
        Assert.False(result.WentAround, "forced landing must suppress the automatic go-around");
        Assert.True(result.Landed, "forced landing must drive the aircraft to a touchdown");
    }

    [Fact]
    public void WithoutForceLanding_TooHighOnFinal_GoesAround()
    {
        var rwy = MakeRunway();
        var ac = MakeAircraftOnFinal(rwy, distNm: 2.0, agl: 3000, ias: 220);
        ac.Phases = new PhaseList { AssignedRunway = rwy, ForceLanding = false };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ac.IndicatedAirspeed;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var result = Run(world, ac, PreTick);
        Assert.True(result.WentAround, "an aircraft this high should auto-go-around without the override");
    }

    [Fact]
    public void ForcedLanding_UnstableInLandingPhase_SuppressesGoAround()
    {
        var rwy = MakeRunway();
        // Already in the landing phase, well above 1.3·Vref (overspeed gate failure).
        var ac = MakeAircraftOnFinal(rwy, distNm: 1.0, agl: 200, ias: 200);
        ac.Phases = new PhaseList { AssignedRunway = rwy, ForceLanding = true };
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ac.IndicatedAirspeed;
        ac.Targets.DesiredVerticalRate = -700;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var result = Run(world, ac, PreTick, maxSeconds: 60);
        Assert.False(result.WentAround, "forced landing must suppress the unstable-approach go-around");
        Assert.True(result.Landed, "forced landing must reach a touchdown");
    }

    [Fact]
    public void ForcedLanding_NoLandingClearance_SuppressesGoAroundAndLands()
    {
        var rwy = MakeRunway();
        // No landing clearance at all (AutoClearedToLand off, no CLAND). Normally the
        // no-clearance go-around fires by 200 ft AGL; ForceLanding must suppress it and land.
        var ac = MakeAircraftOnFinal(rwy, distNm: 2.0, agl: 1500, ias: 160);
        ac.Phases = new PhaseList { AssignedRunway = rwy, ForceLanding = true };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = ac.IndicatedAirspeed;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        var result = Run(world, ac, PreTickNoAutoClear);
        Assert.False(result.WentAround, "forced landing must suppress the no-clearance go-around");
        Assert.True(result.Landed, "forced landing must reach a touchdown without a clearance");
    }
}
