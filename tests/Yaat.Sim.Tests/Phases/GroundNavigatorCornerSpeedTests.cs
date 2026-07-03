using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigator.CornerSpeed"/> — the per-corner taxi speed model.
/// Pins the turn-rate-feasibility cap that gives chord-chain ramp curves (polylines of shallow bends
/// the fillet generator could not widen into one arc) a realistic aggregate speed, while leaving
/// isolated gentle kinks over long legs at full taxi speed.
/// </summary>
public class GroundNavigatorCornerSpeedTests
{
    private const double FeetPerNm = 6076.12;

    private static double Nm(double ft) => ft / FeetPerNm;

    [Fact]
    public void ChordChainBend_TightRamp_CapsBelowLateralAccelSafeSpeed()
    {
        // A 50 ft-radius / 90° apron curve subdivided into ~10° chords: each chord is
        // L = 2*R*sin(5°) ≈ 8.72 ft. The aggregate curve is comfortable at ~8.6 kt (0.13 g lateral
        // accel for R=50 ft). The per-corner model must respect that via the feasibility cap — the
        // shallow 10° angle alone would (wrongly) keep the full 30 kt taxi speed.
        double chordFt = 2.0 * 50.0 * Math.Sin(5.0 * Math.PI / 180.0);
        double speed = GroundNavigator.CornerSpeed(AircraftCategory.Jet, 10.0, Nm(chordFt), Nm(chordFt));

        Assert.True(speed < 8.6, $"chord-chain bend should cap below the R=50ft lateral-accel safe speed (~8.6 kt), was {speed:F2} kt");
        Assert.True(speed >= CategoryPerformance.SlowTurnSpeedKts, $"should stay at or above the slow-turn floor, was {speed:F2} kt");
    }

    [Fact]
    public void IsolatedGentleKink_LongLeg_StaysAtTaxiSpeed()
    {
        // A 10° kink between two long (200 ft) legs is a real, gentle turn — the feasibility cap is a
        // no-op (ω·½L/θ ≫ taxi speed), so the aircraft keeps full taxi speed. Guards against over-slowing
        // normal taxiways now that the shallow-angle gate is lowered.
        double speed = GroundNavigator.CornerSpeed(AircraftCategory.Jet, 10.0, Nm(200.0), Nm(200.0));

        Assert.Equal(CategoryPerformance.TaxiSpeed(AircraftCategory.Jet), speed, precision: 2);
    }

    [Fact]
    public void ModerateChordChain_SlowsWithLegLength()
    {
        // 20° bends over 25 ft chords: the turn-rate feasibility cap ≈ GroundTurnRate·(0.5·L)/θ =
        // 12·(0.5·25)/20 = 7.5 ft/s ≈ 4.4 kt at the realistic Jet ceiling (12 °/s) — a moderate,
        // non-floored speed that demonstrates the feasibility cap tracking leg length. (Tighter 15 ft
        // ramp chords floor at the slow-turn creep — see VeryTightShortChord_FlooredAtSlowTurnSpeed.)
        double speed = GroundNavigator.CornerSpeed(AircraftCategory.Jet, 20.0, Nm(25.0), Nm(25.0));

        Assert.InRange(speed, 3.5, 6.0);
    }

    [Fact]
    public void VeryTightShortChord_FlooredAtSlowTurnSpeed()
    {
        // Degenerately tight: 25° over 2 ft → feasibility ≈ 0.5 kt, floored at the slow-turn creep speed.
        double speed = GroundNavigator.CornerSpeed(AircraftCategory.Jet, 25.0, Nm(2.0), Nm(2.0));

        Assert.Equal(CategoryPerformance.SlowTurnSpeedKts, speed, precision: 3);
    }

    [Fact]
    public void NearCollinearChord_StaysAtTaxiSpeed()
    {
        // Below the near-collinear epsilon: arc-tessellation chords with negligible bend are not slowed.
        double speed = GroundNavigator.CornerSpeed(AircraftCategory.Jet, 0.5, Nm(10.0), Nm(10.0));

        Assert.Equal(CategoryPerformance.TaxiSpeed(AircraftCategory.Jet), speed, precision: 2);
    }
}
