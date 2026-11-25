using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

// YAAT-2822: N245BD departed KSJC 30R on "LOUPE1 BMRNG ..." and, past LOUPE, turned for SJC instead of flying the SID's
// FM 130 "expect radar vectors" leg. LOUPE1's runway transitions end in FM LOUPE 130.0 and the common route restarts at
// IF SJC (a route discontinuity), but the departure route ran LOUPE -> SJC -> BMRNG, so the FM course sat mid-route and
// FlightPhysics, which flies a terminal course only at route end, steered on to SJC.
public class Loupe1RunwayTransitionFmCourseTests
{
    public Loupe1RunwayTransitionFmCourseTests() => TestVnasData.EnsureInitialized();

    private static AircraftFlightPlan FlightPlan() =>
        new()
        {
            Departure = "KSJC",
            Destination = "PHLI",
            Route = "LOUPE1 BMRNG BRINY ALCOA R463 ALLBE R463 APACK",
            Altitude = PlannedAltitude.Ifr(35000),
            FlightRules = "IFR",
        };

    private static List<NavigationTarget> ResolveRw30RRoute()
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("KSJC", "30R")!;
        var onRunway = new AircraftState
        {
            Callsign = "N245BD",
            AircraftType = "GLF5",
            Position = new LatLon(37.3523, -121.9153),
            TrueHeading = new TrueHeading(318.9),
            Altitude = 55,
            IsOnGround = true,
            FlightPlan = FlightPlan(),
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        DepartureRouteResult? result = DepartureClearanceHandler.TryResolveSidFromCifp(onRunway);
        Assert.NotNull(result);
        return result.Targets;
    }

    [Fact]
    public void Loupe1Rw30R_PastLoupe_FliesTheFmCourse_NotDirectSjc()
    {
        List<NavigationTarget> route = ResolveRw30RRoute();
        NavigationTarget loupe = route.Single(t => t.Name == "LOUPE");

        // N245BD at t=1190 of the YAAT-2822 recording: inbound to LOUPE on 128 true, 5000 ft, 200 KIAS, 5.8 nm out.
        var ac = new AircraftState
        {
            Callsign = "N245BD",
            AircraftType = "GLF5",
            Position = new LatLon(37.4108, -121.8960),
            TrueHeading = new TrueHeading(128.2),
            Altitude = 5000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            FlightPlan = FlightPlan(),
        };
        ac.Targets.NavigationRoute.AddRange(route.SkipWhile(t => t.Name != "LOUPE"));

        double? sequencedAtNm = null;
        for (int s = 0; (s < 240) && (sequencedAtNm is null); s++)
        {
            FlightPhysics.Update(ac, 1.0);
            if ((ac.Targets.NavigationRoute.Count == 0) || (ac.Targets.NavigationRoute[0].Name != "LOUPE"))
            {
                sequencedAtNm = GeoMath.DistanceNm(ac.Position, loupe.Position);
            }
        }

        Assert.NotNull(sequencedAtNm);
        Assert.True(sequencedAtNm < 1.0, $"LOUPE sequenced {sequencedAtNm:F2} nm short of the fix");
        TrueHeading fmCourse = new MagneticHeading(130.0).ToTrue(ac.Declination);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(fmCourse) < 1.0,
            $"expected the FM 130 course, ~{fmCourse.Degrees:F1} true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F1} true; "
                + $"route [{string.Join(", ", ac.Targets.NavigationRoute.Select(t => t.Name))}]"
        );

        for (int s = 0; s < 60; s++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(
            ac.TrueHeading.AbsAngleTo(fmCourse) < 3.0,
            $"60 s past LOUPE heading {ac.TrueHeading.Degrees:F1} true, FM course {fmCourse.Degrees:F1} true"
        );
    }
}
