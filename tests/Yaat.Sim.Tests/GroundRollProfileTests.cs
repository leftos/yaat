using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The closed forms of <see cref="GroundRollProfile"/>: the spool ramp's speed, distance and their
/// inverse, and the constant-rate profile the measured-acceleration predictors use.
/// </summary>
public sealed class GroundRollProfileTests
{
    /// <summary>One knot flown for one second covers 1.6878 ft.</summary>
    private const double FeetPerKnotSecond = 1.6878;

    private readonly ITestOutputHelper _output;

    public GroundRollProfileTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Jet_profile_ramps_from_idle_to_the_steady_rate()
    {
        var profile = GroundRollProfile.For("B738", AircraftCategory.Jet);

        Assert.Equal(1.0, profile.IdleRateKtPerSec, 1e-9);
        Assert.Equal(5.0, profile.SteadyRateKtPerSec, 1e-9);
        Assert.Equal(5.0, profile.SpoolSeconds, 1e-9);

        // Brake release accelerates at the idle rate; takeoff thrust at the steady rate; halfway
        // through the spool, halfway between them.
        Assert.Equal(1.0, profile.AccelAt(0), 1e-9);
        Assert.Equal(3.0, profile.AccelAt(2.5), 1e-9);
        Assert.Equal(5.0, profile.AccelAt(5), 1e-9);
        Assert.Equal(5.0, profile.AccelAt(60), 1e-9);
    }

    [Fact]
    public void Jet_speed_and_time_are_inverses_across_the_ramp_and_beyond()
    {
        var profile = GroundRollProfile.For("B738", AircraftCategory.Jet);

        // v(5) = 1*5 + 4*25/(2*5) = 15 kt at the end of the spool; Vr 145 at 5 + (145-15)/5 = 31.0 s.
        Assert.Equal(15.0, profile.SpeedAt(5), 1e-9);
        Assert.Equal(31.0, profile.TimeAtSpeed(145), 1e-9);

        foreach (double v in new[] { 0.0, 3.0, 15.0, 80.0, 145.0 })
        {
            double t = profile.TimeAtSpeed(v);
            _output.WriteLine($"v={v:F1}kt -> t={t:F4}s -> v={profile.SpeedAt(t):F6}kt");
            Assert.Equal(v, profile.SpeedAt(t), 1e-9);
        }
    }

    [Fact]
    public void Jet_roll_distance_matches_the_closed_form()
    {
        var profile = GroundRollProfile.For("B738", AircraftCategory.Jet);

        // d(5) = 1*25/2 + 4*125/(6*5) = 29.17 kt.s, then 15 kt for 26 s plus 5 kt/s over 26 s.
        double ktSeconds = profile.DistanceKtSecondsAt(31);
        _output.WriteLine($"B738 roll to Vr: {ktSeconds:F2} kt.s = {ktSeconds * FeetPerKnotSecond:F0} ft");
        Assert.Equal(2109.0, ktSeconds, 1.0);

        // Inverting the distance from a standing start returns the time it was measured at.
        Assert.Equal(31.0, profile.TimeToCoverKtSeconds(0, ktSeconds), 1e-4);

        // Past the spool the ramp is the steady rate, so the covering time has a closed form:
        // v(8) = 30 kt, 30t + 2.5t² = 100 kt.s → t = (-12 + sqrt(304)) / 2 = 2.7178 s.
        Assert.Equal(30.0, profile.SpeedAt(8), 1e-9);
        Assert.Equal(2.7178, profile.TimeToCoverKtSeconds(8, 100), 1e-4);
    }

    [Fact]
    public void Constant_profile_never_ramps()
    {
        var profile = GroundRollProfile.Constant(5);

        Assert.Equal(5.0, profile.AccelAt(0), 1e-9);
        Assert.Equal(5.0, profile.AccelAt(30), 1e-9);
        Assert.Equal(5.0, profile.SpeedAt(1), 1e-9);
        Assert.Equal(50.0, profile.SpeedAt(10), 1e-9);
        Assert.Equal(145.0, profile.SpeedAt(29), 1e-9);
        Assert.Equal(29.0, profile.TimeAtSpeed(145), 1e-9);

        // v*t/2 over the whole run: 145 kt reached at 29 s covers 2102.5 kt.s.
        Assert.Equal(2102.5, profile.DistanceKtSecondsAt(29), 1e-9);

        // Inverse of the distance: ½·5·5² = 62.5 kt.s from a standing start takes 5 s.
        Assert.Equal(5.0, profile.TimeToCoverKtSeconds(0, 62.5), 1e-4);
    }

    /// <summary>
    /// The predictors' entry point onto the ramp: the exact clock of the phase flying the roll when there
    /// is one, the speed proxy otherwise. Without it a touch-and-go re-spooling at 60 kt would be projected
    /// at the steady rate its speed implies rather than the idle rate it actually has.
    /// </summary>
    [Fact]
    public void RollClockSeconds_prefers_the_phase_clock_over_the_speed_proxy()
    {
        var profile = GroundRollProfile.For("B738", AircraftCategory.Jet);
        var aircraft = new AircraftState
        {
            Callsign = "ROLL01",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 0,
            IndicatedAirspeed = 40,
            IsOnGround = true,
        };

        // No phase owns the roll (a lined-up leader, a live-traffic shadow): the ramp position 40 kt
        // implies. 40 kt is past the 15 kt spool exit, so t = 5 + (40 - 15)/5 = 10 s.
        Assert.Equal(10.0, GroundRollProfile.RollClockSeconds(aircraft, profile), 1e-9);
        Assert.Equal(profile.TimeAtSpeed(40), GroundRollProfile.RollClockSeconds(aircraft, profile), 1e-9);

        // A roll phase reporting 2.0 s wins outright — the aircraft is 2 s into its spool whatever speed
        // it carries, so the predictors see the 2.6 kt/s it has rather than the 5.0 kt/s 40 kt suggests.
        var takeoff = TakeoffPhase.FromSnapshot(
            new TakeoffPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 2,
                Airborne = false,
                FieldElevation = 0,
                RunwayHeadingDeg = 280,
                ThresholdLat = 0,
                ThresholdLon = 0,
                RollElapsedSeconds = 2.0,
            }
        );
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(takeoff);

        Assert.Same(takeoff, aircraft.Phases.CurrentPhase);
        Assert.Equal(2.0, GroundRollProfile.RollClockSeconds(aircraft, profile), 1e-9);
        Assert.Equal(2.6, profile.AccelAt(GroundRollProfile.RollClockSeconds(aircraft, profile)), 1e-9);
    }

    [Fact]
    public void C172_roll_to_rotation_is_in_the_poh_range()
    {
        var profile = GroundRollProfile.For("C172", AircraftCategory.Piston);

        double toVrSeconds = profile.TimeAtSpeed(60);
        double rollFt = profile.DistanceKtSecondsAt(toVrSeconds) * FeetPerKnotSecond;
        _output.WriteLine(
            $"C172 (idle={profile.IdleRateKtPerSec:F2}, steady={profile.SteadyRateKtPerSec:F2}, spool={profile.SpoolSeconds:F1}s): "
                + $"Vr 60 kt at t={toVrSeconds:F2}s, roll={rollFt:F0}ft"
        );

        // The POH ground roll for a sea-level standard-day C172 is 960 ft.
        Assert.InRange(rollFt, 800, 1300);
    }
}
