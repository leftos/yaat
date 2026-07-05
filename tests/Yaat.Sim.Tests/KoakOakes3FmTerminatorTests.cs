using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
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
        var star = NavigationDatabase.Instance.GetStar("KOAK", "OAKES3");
        if (star is null || star.CommonLegs.Count == 0 || !star.RunwayTransitions.TryGetValue("RW12", out var rw12))
        {
            return; // OAKES3 / RW12 not in the bundled CIFP cycle — skip offline.
        }

        // Arrival to runway 12 ends its runway transition at HIRMO with an FM "fly 260.8°, expect
        // vectors" leg.
        var orderedLegs = new List<Yaat.Sim.Data.Vnas.CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);
        orderedLegs.AddRange(rw12.Legs);
        var targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);
        Assert.NotEmpty(targets);

        var last = targets[^1];
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

        FlightPhysics.Update(ac, 1.0, null, null);

        // Reached HIRMO → route empties and the aircraft turns to the published outbound course,
        // rather than holding its 90° arrival heading.
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        var expected = new MagneticHeading(260.8).ToTrue(ac.Declination);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected FM outbound ~{expected.Degrees:F0}° true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F0}°"
        );
    }
}
