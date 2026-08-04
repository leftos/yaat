using Xunit;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// AIM 4-4-10.4: climb/descend at an optimum rate to 1,000 ft above/below the assigned
/// altitude, "and then attempt to descend or climb at a rate of between 500 and 1,500 fpm
/// until the assigned altitude is reached." Physics used to hold the full profile rate to
/// within 10 ft of the target and then snap — a 4,000 fpm expedited descent went to level in
/// a single tick, which no Mode C readout ever shows.
///
/// The model: within the last 1,000 ft the rate tapers proportionally to the remaining
/// altitude (1,500 fpm entering the band, floored at 500), never raising a rate that is
/// already gentler. It is scoped to free flight — a phase/planner-commanded
/// <c>DesiredVerticalRate</c> (a glidepath, a crossing restriction) is never touched, and
/// no taper applies while a phase is active at all: approach/landing phases fly
/// profile-rate segments in the last 1,000 ft AGL that must not be flattened
/// (<c>TouchdownPointTests</c> guards that end of it).
/// </summary>
public sealed class LevelOffTaperTests
{
    public LevelOffTaperTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft(string type, double altitude)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = 250,
        };
    }

    private static double VerticalSpeedAfterOneTick(AircraftState ac)
    {
        FlightPhysics.Update(ac, 1.0);
        return ac.VerticalSpeed;
    }

    [Fact]
    public void Descent_TapersProportionallyInsideTheLastThousandFeet()
    {
        // B738 at 5,800 descending to 5,000: profile rate ~2,660 fpm, but 800 ft to go
        // tapers to 800 × 1.5 = 1,200 fpm.
        var ac = Aircraft("B738", 5800);
        ac.Targets.TargetAltitude = 5000;

        Assert.Equal(-1200, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void Descent_TaperFloorsAtFiveHundred()
    {
        // 200 ft to go: 200 × 1.5 = 300 would undershoot the AIM band; the 500 fpm floor holds.
        var ac = Aircraft("B738", 5200);
        ac.Targets.TargetAltitude = 5000;

        Assert.Equal(-500, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void Climb_TapersInsideTheLastThousandFeet()
    {
        // B738 at 9,600 climbing to 10,000: 400 ft to go tapers to 600 fpm.
        var ac = Aircraft("B738", 9600);
        ac.Targets.TargetAltitude = 10000;

        Assert.Equal(600, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void Taper_NeverRaisesAnAlreadyGentleRate()
    {
        // An SR22's 500 fpm profile descent sits at the band floor; 900 ft to go must not
        // raise it toward 1,350.
        var ac = Aircraft("SR22", 2300);
        ac.Targets.TargetAltitude = 1400;
        ac.IndicatedAirspeed = 115;

        Assert.Equal(-500, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void Taper_DoesNotTouchAPhaseCommandedRate()
    {
        // A glidepath or crossing-restriction rate is a commanded vertical path — 300 ft from
        // the target it still flies the commanded 1,800 fpm, not a tapered 500.
        var ac = Aircraft("B738", 5300);
        ac.Targets.TargetAltitude = 5000;
        ac.Targets.DesiredVerticalRate = -1800;

        Assert.Equal(-1800, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void Taper_AppliesToAnExpeditedDescent()
    {
        // Expedite raises the en-route rate, but the last-1,000-ft band still governs the
        // capture: an expedited B738 400 ft above the target descends at 600 fpm, not 4,000.
        var ac = Aircraft("B738", 5400);
        ac.Targets.TargetAltitude = 5000;
        ac.Procedure.IsExpediting = true;

        Assert.Equal(-600, VerticalSpeedAfterOneTick(ac), 1.0);
    }

    [Fact]
    public void FullRateResumesOutsideTheBand()
    {
        // 4,000 ft to go: the taper must not reach outside the AIM band. B738 at 9,000
        // descending to 5,000 flies the full profile rate (~3,300 fpm at that altitude).
        var ac = Aircraft("B738", 9000);
        ac.Targets.TargetAltitude = 5000;

        double vs = VerticalSpeedAfterOneTick(ac);
        Assert.True(vs < -3000, $"expected the full profile rate outside the band; got {vs:F0}");
    }
}
