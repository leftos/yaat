using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: FinalApproachPhase used a fixed 5.0 NM trigger to slow to FAS,
/// which wasted minutes of approach time for slow piston aircraft already near
/// approach speed. A C172 cruising final at 100 kt with an 81 kt FAS only needs
/// ~10 s × 2.0 kt/s = ~0.27 NM of decel travel, but the trigger fired ~18× earlier.
///
/// The fix replaces the fixed trigger with a kinematic computation: trigger =
/// FasReachGate + bleedDistance, capped at 5.0 NM. With FasReachGate = 2.0 NM,
/// the C172 in this test holds 100 kt until ~2.24 NM and reaches FAS by ~2.0 NM.
/// </summary>
public class SlowPistonFasTriggerTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("FinalApproachPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-slow-piston-fas",
                ScenarioName = "Slow Piston FAS Trigger",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
    }

    /// <summary>
    /// C172 spawned at 6 NM on OAK 28R final at 100 kt (only 19 kt above FAS=81).
    /// Today the aircraft decelerates at the fixed 5 NM trigger, hits FAS within
    /// ~0.3 NM, and cruises the last 4.7 NM at FAS — wasting time.
    /// After the fix it holds 100 kt past 4 NM (decel hasn't started yet) and
    /// reaches FAS by 1 NM (still well stabilized for a 3° approach to OAK).
    /// </summary>
    [Fact]
    public void C172_HoldsCruiseSpeedPast4nm_WhenSmallSpeedDelta()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway = navDb.GetRunway("OAK", "28R");
        Assert.NotNull(runway);

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        const double startDistNm = 6.0;
        const double startIas = 100;

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, startDistNm);
        // 3° glideslope: tan(3°) ≈ 0.0524, i.e. ~318 ft per NM.
        double altAboveField = startDistNm * 318;

        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = "C172",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + altAboveField,
            IndicatedAirspeed = startIas,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                CruiseAltitude = 3000,
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);
        engine.World.AddAircraft(aircraft);

        var category = AircraftCategorization.Categorize("C172");
        double fas = AircraftPerformance.ApproachSpeed("C172", category);
        output.WriteLine($"C172 FAS = {fas:F0} kt, start IAS = {startIas:F0} kt, speed delta = {startIas - fas:F0} kt");
        double decelRate = AircraftPerformance.DecelRate("C172", category);
        output.WriteLine($"Decel rate (Piston) = {decelRate:F1} kt/s");

        bool capturedAt4 = false;
        bool capturedAt1 = false;
        double iasAt4nm = double.NaN;
        double iasAt1nm = double.NaN;
        double dist4nm = double.NaN;
        double dist1nm = double.NaN;

        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("TSTAC");
            if (ac is null)
            {
                break;
            }

            double distNm = GeoMath.DistanceNm(ac.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));

            if ((t % 10 == 0) || ((distNm <= 5.0) && (distNm >= 0.5)))
            {
                output.WriteLine(
                    $"t={t, 3}s dist={distNm:F2}nm ias={ac.IndicatedAirspeed:F1} "
                        + $"alt={ac.Altitude:F0} tgtSpd={ac.Targets.TargetSpeed?.ToString("F0") ?? "null"}"
                );
            }

            if (!capturedAt4 && (distNm <= 4.0))
            {
                iasAt4nm = ac.IndicatedAirspeed;
                dist4nm = distNm;
                capturedAt4 = true;
            }

            if (!capturedAt1 && (distNm <= 1.0))
            {
                iasAt1nm = ac.IndicatedAirspeed;
                dist1nm = distNm;
                capturedAt1 = true;
                break;
            }
        }

        Assert.True(capturedAt4, "Aircraft never reached 4 NM");
        Assert.True(capturedAt1, "Aircraft never reached 1 NM");

        // Today's behavior: fixed 5 NM trigger fires regardless of speed delta. By 4 NM the C172
        // has already bled the 19 kt to FAS (only ~0.27 NM needed). After the fix the kinematic
        // trigger is ~2.24 NM, so at 4 NM IAS is still at cruise.
        Assert.True(
            iasAt4nm > fas + 10,
            $"At {dist4nm:F2} NM: IAS={iasAt4nm:F1} kt, FAS={fas:F0} kt. "
                + "Aircraft should not have started decelerating to FAS yet — kinematic decel from "
                + $"{startIas:F0} kt only requires ~0.3 NM of travel."
        );

        // Aircraft must still hit FAS by short final regardless of trigger logic.
        Assert.True(
            iasAt1nm <= fas + 5,
            $"At {dist1nm:F2} NM: IAS={iasAt1nm:F1} kt, FAS={fas:F0} kt. " + "Aircraft should be at or very near FAS on short final."
        );
    }
}
