using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Most US STARs end with an ARINC-424 FM leg ("from the fix, fly the published course, expect
/// vectors"). The OAKES THREE arrival into KOAK ends its common legs at HIRMO with FM course
/// 260.8°. Previously the aircraft reached HIRMO and held whatever heading it arrived on; it must
/// instead fly the published 260.8° outbound and await vectors.
/// </summary>
[Collection("NavDbMutator")]
public class KoakOakes3FmTerminatorTests
{
    public KoakOakes3FmTerminatorTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Oakes3_LastFix_CarriesPublishedOutboundCourse_AndFliesItAtRouteEnd()
    {
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar("KOAK", "OAKES3");
        if (star is null || star.CommonLegs.Count == 0 || !star.RunwayTransitions.TryGetValue("RW12", out CifpTransition? rw12))
        {
            return; // OAKES3 / RW12 not in the bundled CIFP cycle — skip offline.
        }

        // Arrival to runway 12 ends its runway transition at HIRMO with an FM "fly 260.8°, expect
        // vectors" leg.
        var orderedLegs = new List<Yaat.Sim.Data.Vnas.CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);
        orderedLegs.AddRange(rw12.Legs);
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);
        Assert.NotEmpty(targets);

        NavigationTarget last = targets[^1];
        Assert.Equal("HIRMO", last.Name);
        Assert.NotNull(last.TerminalCourseMagnetic);
        Assert.Equal(260.8, last.TerminalCourseMagnetic!.Value, 1);

        // Fly an aircraft to HIRMO with the FM target as its sole remaining route fix.
        var ac = new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = last.Position,
            TrueHeading = new TrueHeading(90), // arriving on a heading unrelated to the FM course
            Altitude = 11000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSLC",
                Destination = "KOAK",
                Route = "OAKES3",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        ac.Targets.NavigationRoute.Add(last);

        FlightPhysics.Update(ac, 1.0);

        // Reached HIRMO → route empties and the aircraft turns to the published outbound course,
        // rather than holding its 90° arrival heading.
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        TrueHeading expected = new MagneticHeading(260.8).ToTrue(ac.Declination);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected FM outbound ~{expected.Degrees:F0}° true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }

    [Fact]
    public void Altta9Rw11l_VmLastLeg_CarriesCourseOnAltta_AndFliesItAtRouteEnd()
    {
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar("KFAT", "ALTTA9");
        if (star is null || star.CommonLegs.Count == 0 || !star.RunwayTransitions.TryGetValue("RW11L", out CifpTransition? rw11l))
        {
            return; // ALTTA9 / RW11L not in the bundled CIFP cycle — skip offline.
        }

        // The RW11L transition ends in a VM leg whose fix field names the airport (KFAT) and whose
        // published course is 310°. The fix field is the leg's reference, not a waypoint to fly to.
        var orderedLegs = new List<CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);
        orderedLegs.AddRange(rw11l.Legs);
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);
        Assert.NotEmpty(targets);
        Assert.DoesNotContain(targets, t => t.Name == "KFAT");

        NavigationTarget last = targets[^1];
        Assert.Equal("ALTTA", last.Name);
        Assert.NotNull(last.TerminalCourseMagnetic);
        Assert.Equal(310.0, last.TerminalCourseMagnetic!.Value, 1);

        // Fly an aircraft to ALTTA with that target as its sole remaining route fix.
        var ac = new AircraftState
        {
            Callsign = "SWA23",
            AircraftType = "B738",
            Position = last.Position,
            TrueHeading = new TrueHeading(90),
            Altitude = 11000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KCMA",
                Destination = "KFAT",
                Route = "ALTTA9",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        ac.Targets.NavigationRoute.Add(last);

        FlightPhysics.Update(ac, 1.0);

        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        TrueHeading expected = new MagneticHeading(310.0).ToTrue(ac.Declination);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected VM course ~{expected.Degrees:F0}° true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }
}
