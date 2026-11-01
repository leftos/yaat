using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// A jet whose planned base speed is driven below the drag-angle descent threshold (≈87 kt) — e.g. after an
/// <c>SPDN</c> force-speed, or a scenario-scripted sub-minimum speed, both of which bypass the min-operating-IAS
/// refusal — must still fly the base leg. <see cref="BasePhase.PlanDescent"/> clamps the descent rate with
/// <c>Math.Clamp(computedRate, PatternDescentRate, MaxDescentRateFpm(speed))</c>; at a low speed the drag-limited
/// <see cref="BasePhase.MaxDescentRateFpm"/> falls BELOW the category's normal <c>PatternDescentRate</c>, so the
/// lower clamp bound exceeds the upper one and <see cref="Math.Clamp(double,double,double)"/> throws
/// <see cref="ArgumentException"/>, crashing the tick when the aircraft turns base.
/// </summary>
[Collection("NavDbMutator")]
public class BasePhaseDescentClampTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public BasePhaseDescentClampTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(Rwy()));
    }

    public void Dispose() => _navDbScope.Dispose();

    // Field elevation 9 ft, heading 280 — mirrors OAK 28.
    private static RunwayInfo Rwy() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 9);

    private static PhaseContext Ctx(AircraftState ac)
    {
        RunwayInfo rwy = ac.Phases!.AssignedRunway!;
        return new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    [Fact]
    public void JetTurningBaseWithForcedLowSpeed_DoesNotThrowOnDescentClamp()
    {
        RunwayInfo rwy = Rwy();
        const double sizeNm = 1.5;

        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "B738",
            0,
            PatternDirection.Left,
            sizeNm,
            null,
            null,
            authoredRunway: null
        );

        // On the downwind, abeam, about to turn base.
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        var ac = new AircraftState
        {
            Callsign = "SWA1",
            AircraftType = "B738",
            Position = abeam,
            TrueHeading = wp.DownwindHeading,
            TrueTrack = wp.DownwindHeading,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 70,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left },
        };

        // The SPDN / scenario-scripted outcome: an explicit controller speed below the jet's min operating IAS.
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetSpeed = 70;

        var basePhase = new BasePhase { Waypoints = wp, FinalDistanceNm = 1.0 };
        ac.Phases.Add(basePhase);

        // OnStart runs PlanDescent — this is the tick that throws today.
        Exception? thrown = Record.Exception(() => ac.Phases.Start(Ctx(ac)));

        Assert.True(
            thrown is null,
            $"BasePhase.PlanDescent threw on the descent-rate clamp for a jet at 70 kt: {thrown?.GetType().Name}: {thrown?.Message}"
        );
    }
}
