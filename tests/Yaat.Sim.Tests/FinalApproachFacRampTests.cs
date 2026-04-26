using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies the FAC → runway-heading visual-alignment ramp at the bottom of the
/// approach. Reproduces the SFO 10L scenario where the published RNAV FAC is
/// 120.86° but the runway TrueHeading is 117.91° (~3° offset due to CIFP /
/// magnetic-variation rounding). Without the ramp, LandingPhase snaps the
/// aircraft from FAC to runway heading at the threshold, producing a ~22° bank
/// that trips the stabilization gate and triggers a spurious go-around.
///
/// Drives the simulation through <see cref="SimulationWorld.Tick"/> + a preTick
/// callback that runs <see cref="PhaseRunner.Tick"/> — the same path
/// <c>SimulationEngine.TickPhysics</c> uses in production. Tests that hand-roll
/// <c>phase.OnTick()</c> directly bypass phase advance and don't catch
/// inter-phase regressions like the one this fixture is built for.
/// </summary>
public class FinalApproachFacRampTests
{
    private readonly ITestOutputHelper _output;

    public FinalApproachFacRampTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FacOffsetByMagVar_AlignsToRunwayBeforeFlare()
    {
        // KSFO 10L geometry from the bug bundle (AS3-NCTB-6 (B) | SFO10):
        // - threshold ≈ (37.628738, -122.393391), elevation 13 ft
        // - runway TrueHeading = 117.91°
        // - published RNAV FAC = 120.86°, ~3° offset
        const double thresholdLat = 37.628738722;
        const double thresholdLon = -122.39339186;
        const double thresholdElev = 13.1;
        const double runwayHeadingDeg = 117.91;
        const double facDeg = 120.86;

        var rwy = new RunwayInfo
        {
            AirportId = "KSFO",
            Id = new RunwayIdentifier("10L"),
            Designator = "10L",
            Lat1 = thresholdLat,
            Lon1 = thresholdLon,
            TrueHeading1 = new TrueHeading(runwayHeadingDeg),
            Elevation1Ft = thresholdElev,
            Lat2 = 37.61353611,
            Lon2 = -122.357141,
            TrueHeading2 = new TrueHeading((runwayHeadingDeg + 180) % 360),
            Elevation2Ft = thresholdElev,
            LengthFt = 11193,
            WidthFt = 200,
        };

        // Place aircraft 3 nm from threshold, on the FAC, at 1000 ft AGL,
        // heading along the FAC (i.e., already established on FAC).
        var fac = new TrueHeading(facDeg);
        var (startLat, startLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, fac.ToReciprocal(), 3.0);

        var ac = new AircraftState
        {
            Callsign = "EVA18",
            AircraftType = "B77W",
            Position = new LatLon(startLat, startLon),
            TrueHeading = fac,
            Altitude = 1013, // 1000 ft AGL
            IndicatedAirspeed = 149,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KSFO" },
        };

        var clearance = new ApproachClearance
        {
            ApproachId = "R10L",
            AirportCode = "SFO",
            RunwayId = "10L",
            FinalApproachCourse = fac,
            // Anchor null → falls back to threshold; mirrors the bundle's clearance.
            FinalApproachAnchorLat = null,
            FinalApproachAnchorLon = null,
        };

        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = clearance,
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());

        ac.Targets.TargetSpeed = 149;

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        // Mirror SimulationEngine.PreTick: build a PhaseContext per aircraft per tick
        // and run PhaseRunner.Tick. This is how production drives phases — manually
        // calling phase.OnTick() in tests bypasses phase advance and PhaseList wiring.
        void PreTick(AircraftState aircraft, double dt)
        {
            if (aircraft.Phases is null || aircraft.Phases.IsComplete)
            {
                return;
            }

            var ctx = new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = dt,
                Runway = aircraft.Phases.AssignedRunway,
                FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
                Logger = NullLogger.Instance,
                AutoClearedToLand = true,
            };
            PhaseRunner.Tick(aircraft, ctx);
        }

        // Production cadence: SimulationEngine runs PhysicsSubTickRate=4 sub-ticks per second.
        // The stabilization-gate grace window absorbs single-tick bank spikes only at this
        // cadence; using dt=1.0 in tests would falsely fire the gate on a one-tick residual.
        const double dt = 0.25;
        const int maxTicks = 4 * 240; // up to 240 simulated seconds
        // Mirrors FinalApproachPhase.MagVarRampEndAgl — alignment must complete by this AGL.
        const double VisualAlignmentRampEndAgl = 150.0;

        _output.WriteLine("tick,phase,agl,distNm,heading,targetHdg,bank,xteFt");

        double maxBankBelow200Ft = 0;
        double hdgDiffAtRampEnd = double.NaN;
        bool sawGoAround = false;
        var warningsAtGoAround = new List<string>();
        int sawLandingTick = -1;
        int sawTouchdownTick = -1;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Tick(dt, PreTick);

            // Drain warnings each tick (mirrors server post-physics flow).
            foreach (var (callsign, warning) in world.DrainAllWarnings())
            {
                _output.WriteLine($"# tick {tick} warning: {callsign}: {warning}");
                if (warning.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    sawGoAround = true;
                    warningsAtGoAround.Add(warning);
                }
            }

            double agl = ac.Altitude - thresholdElev;
            double dist = GeoMath.DistanceNm(ac.Position, new LatLon(thresholdLat, thresholdLon));
            double xteNm = GeoMath.SignedCrossTrackDistanceNm(ac.Position, new LatLon(thresholdLat, thresholdLon), rwy.TrueHeading);
            string phaseName = ac.Phases?.CurrentPhase?.Name ?? "(complete)";

            _output.WriteLine(
                $"{tick},{phaseName},{agl:F0},{dist:F2},{ac.TrueHeading.Degrees:F2},{ac.Targets.TargetTrueHeading?.Degrees:F2},{ac.BankAngle:F1},{xteNm * 6076.12:F0}"
            );

            if (agl <= 200)
            {
                double absBank = Math.Abs(ac.BankAngle);
                if (absBank > maxBankBelow200Ft)
                {
                    maxBankBelow200Ft = absBank;
                }
            }

            // First tick where AGL ≤ ramp end altitude: alignment must be complete.
            if (double.IsNaN(hdgDiffAtRampEnd) && agl <= VisualAlignmentRampEndAgl)
            {
                hdgDiffAtRampEnd = Math.Abs(ac.TrueHeading.SignedAngleTo(rwy.TrueHeading));
            }

            if (sawLandingTick < 0 && phaseName == "Landing")
            {
                sawLandingTick = tick;
            }
            if (sawTouchdownTick < 0 && ac.IsOnGround)
            {
                sawTouchdownTick = tick;
                break; // Touchdown → end of relevant test window.
            }
        }

        // No go-around must fire — this is the regression we're guarding.
        Assert.False(sawGoAround, "Spurious go-around fired during stable approach. Warnings: " + string.Join(" | ", warningsAtGoAround));

        // Aircraft must transition all the way to Landing and touch down.
        Assert.True(sawLandingTick > 0, "Never reached LandingPhase — descent geometry is broken.");
        Assert.True(sawTouchdownTick > 0, "Never touched down — flare/landing not completing.");

        // Below 200 ft, bank must stay under the stabilization-gate threshold (15°).
        Assert.True(
            maxBankBelow200Ft < 15.0,
            $"Bank exceeded 15° during the visual-alignment segment (max={maxBankBelow200Ft:F1}°) — heading transition is too abrupt."
        );

        // By the ramp end altitude, the aircraft must already be aligned with runway heading
        // (not still mid-turn). We want zero residual turning during flare/touchdown.
        Assert.False(double.IsNaN(hdgDiffAtRampEnd), $"Test never observed AGL ≤ {VisualAlignmentRampEndAgl} ft — phase advance broke.");
        Assert.True(
            hdgDiffAtRampEnd < 1.0,
            $"Aircraft was not aligned with runway heading at AGL ≤ {VisualAlignmentRampEndAgl} ft (diff={hdgDiffAtRampEnd:F2}°) — visual-alignment ramp finished too late."
        );
    }
}
