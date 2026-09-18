using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end regression for issue #438: an aircraft holding short of KSFO 01R at A1 (or 01L at M1) given LUAW
/// pivoted a full 360° to the right at the tight-turn speed floor before rolling forward and lining up. The
/// graph route started on the taxiway node the aircraft had just rolled over — a couple of feet behind the
/// nose — so the virtual approach segment pointed backward and the navigator's entry alignment swept the nose
/// through ~178°, after which pure pursuit chased the node round the rest of the circle.
///
/// <para>
/// The legitimate lineup turn from these poses is ~93° to the left, so the running net turn must stay well
/// inside a half circle. <c>GroundNavigator.ThrowOnOrbit</c> is armed for the test suite
/// (<c>tests/Yaat.Sim.Tests/ModuleInit.cs</c>), so on the unfixed code the tick loop may throw instead.
/// </para>
/// </summary>
[Collection("NavDbMutator")]
public class Issue438SfoLuawPivotTests(ITestOutputHelper output)
{
    /// <summary>Net turn (deg) the lineup may accumulate before it is a pivot rather than a lineup.</summary>
    private const double MaxNetTurnDeg = 120.0;

    [Fact]
    public void Ual859HoldingShort01RAtA1_LinesUpWithoutPivotingInACircle()
    {
        RunLineup("UAL859", "01R", new LatLon(37.60687155024075, -122.381946548138), 120.85);
    }

    [Fact]
    public void Dal819HoldingShort01LAtM1_LinesUpWithoutPivotingInACircle()
    {
        RunLineup("DAL819", "01L", new LatLon(37.608439953128396, -122.38383474182635), 120.79);
    }

    private void RunLineup(string callsign, string runwayDesignator, LatLon position, double headingDeg)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        RunwayInfo? runway = TestVnasData.NavigationDb.GetRunway("KSFO", runwayDesignator);
        if (runway is null)
        {
            output.WriteLine($"SKIP: KSFO {runwayDesignator} not in navdata");
            return;
        }

        AirportGroundLayout? sfoLayout = new TestAirportGroundData().GetLayout("SFO");
        if (sfoLayout is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return;
        }

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = sfoLayout,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        phase.OnStart(ctx);

        output.WriteLine($"[OnStart] state={phase.CurrentState} targetHdg={ctx.Targets.TargetTrueHeading?.Degrees.ToString("F1") ?? "null"}");
        Assert.Equal(LineUpPhase.State.GraphTaxi, phase.CurrentState);
        Assert.True(
            ctx.Targets.TargetTrueHeading is null,
            $"graph-taxi lineup must leave the heading to the navigator, got a seeded target of {ctx.Targets.TargetTrueHeading?.Degrees:F1}°"
        );

        const int budgetTicks = 60 * 4; // 60 s @ 0.25 s/tick
        bool completed = false;
        int completionTick = -1;
        double netTurnDeg = 0.0;
        double maxNetTurnDeg = 0.0;

        for (int i = 0; i < budgetTicks; i++)
        {
            double hdgBefore = aircraft.TrueHeading.Degrees;
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);

            netTurnDeg += GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, hdgBefore);
            maxNetTurnDeg = Math.Max(maxNetTurnDeg, Math.Abs(netTurnDeg));

            if (done)
            {
                completed = true;
                completionTick = i + 1;
                break;
            }
        }

        double crossFt =
            Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Position.Lat,
                    aircraft.Position.Lon,
                    runway.ThresholdLatitude,
                    runway.ThresholdLongitude,
                    runway.TrueHeading
                )
            ) * GeoMath.FeetPerNm;
        double hdgDiff = Math.Abs(runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));

        output.WriteLine(
            $"[end] completed={completed} tick={completionTick} cross={crossFt:F2}ft hdgDiff={hdgDiff:F2}° "
                + $"ias={aircraft.IndicatedAirspeed:F2}kt maxNetTurn={maxNetTurnDeg:F1}° state={phase.CurrentState}"
        );

        Assert.True(
            maxNetTurnDeg < MaxNetTurnDeg,
            $"{callsign} swung {maxNetTurnDeg:F1}° of net turn lining up on {runwayDesignator} — the lineup turn is ~93°, "
                + "anything past a half circle is the issue #438 pivot"
        );
        Assert.True(
            completed,
            $"LineUpPhase did not complete within {budgetTicks * 0.25:F0} s "
                + $"(cross={crossFt:F2}ft hdgDiff={hdgDiff:F2}° ias={aircraft.IndicatedAirspeed:F2}kt state={phase.CurrentState})"
        );
        Assert.True(crossFt < 5.0, $"final cross-track {crossFt:F2}ft exceeds 5 ft tolerance");
        Assert.True(hdgDiff < 2.0, $"final heading-diff {hdgDiff:F2}° exceeds 2° tolerance");
        Assert.True(aircraft.IndicatedAirspeed < 2.0, $"LUAW mode should brake to ~0, got {aircraft.IndicatedAirspeed:F2}kt");
    }
}
