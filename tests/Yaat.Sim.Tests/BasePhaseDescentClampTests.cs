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
/// refusal — must still fly the base leg. <see cref="BasePhase.PlanDescent"/> clamps the descent rate between the
/// category's normal <c>PatternDescentRate</c> and the drag-limited <see cref="BasePhase.MaxDescentRateFpm"/>; at a low
/// speed that ceiling falls BELOW the normal rate, so a bare <see cref="Math.Clamp(double,double,double)"/> throws
/// <see cref="ArgumentException"/>, crashing the tick when the aircraft turns base. The ceiling must bind instead.
/// </summary>
public class BasePhaseDescentClampTests
{
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
    public void JetTurningBaseWithForcedLowSpeed_DescendsAtTheDragLimitedCeiling()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? found = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(found);
        RunwayInfo rwy = found;
        const double sizeNm = 1.5;
        const double plannedSpeedKt = 70;

        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Jet, "B738", 0, PatternDirection.Left)
            {
                SizeOverrideNm = sizeNm,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
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
            IndicatedAirspeed = plannedSpeedKt,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left },
        };

        // The SPDN / scenario-scripted outcome: an explicit controller speed below the jet's min operating IAS.
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetSpeed = plannedSpeedKt;

        var basePhase = new BasePhase { Waypoints = wp, FinalDistanceNm = 1.0 };
        ac.Phases.Add(basePhase);

        // OnStart runs PlanDescent, whose descent-rate clamp threw for a jet this slow.
        Exception? thrown = Record.Exception(() => ac.Phases.Start(Ctx(ac)));

        Assert.True(
            thrown is null,
            $"BasePhase.PlanDescent threw on the descent-rate clamp for a jet at 70 kt: {thrown?.GetType().Name}: {thrown?.Message}"
        );

        // The drag-limited ceiling binds, not the jet's normal 1000 fpm pattern rate.
        double expectedRate = -BasePhase.MaxDescentRateFpm(plannedSpeedKt, AircraftCategory.Jet);
        Assert.NotNull(ac.Targets.DesiredVerticalRate);
        double rate = ac.Targets.DesiredVerticalRate.Value;
        Assert.Equal(expectedRate, rate, 0.5);
        Assert.True(rate > -1000, $"base descent {rate:F1} fpm should be shallower than the jet's normal 1000 fpm pattern rate");
    }
}
