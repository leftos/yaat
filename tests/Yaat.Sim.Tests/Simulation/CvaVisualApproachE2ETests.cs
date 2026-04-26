using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Recording-free E2E tests for the classic "RFIS → CVA → CLAND → land" flow
/// at KOAK runway 30. Two scenarios cover the two non-trivial CVA branches in
/// <see cref="Yaat.Sim.Commands.ApproachCommandHandler.TryClearedVisualApproach"/>:
/// pattern entry from a downwind position (>90° off final) and angled join
/// from a base leg (≤90° off final). The straight-in branch is covered by
/// <see cref="OakFullLifecycleTests"/>.
/// </summary>
public class CvaVisualApproachE2ETests(ITestOutputHelper output)
{
    private const double KoakArpLat = 37.7240;
    private const double KoakArpLon = -122.2199;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-cva-e2e",
                ScenarioName = "CVA Visual Approach E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        return engine;
    }

    /// <summary>
    /// Classic visual-approach-from-the-downwind: B738 5 nm north of KOAK
    /// heading 090 at 5000 ft, RFIS, CVA 30, CLAND, land. Angle off final
    /// course (303.7°) is ~146°, so CVA routes through the >90° pattern-entry
    /// branch: PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase
    /// → LandingPhase.
    /// </summary>
    [Fact]
    public void Cva_FromNorthDownwind_RfisThenCvaThenCland_Lands()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway30 = navDb.GetRunway("OAK", "30");
        Assert.NotNull(runway30);

        // 5 nm at bearing 350° (slightly NW of due north) puts the field on the right-front
        // quadrant rather than exactly abeam. The visual acquisition cone is ±90° of nose
        // with strict > 90 → fail, and floating-point bearing math from due-north spawn
        // lands us right on the boundary. Nudging 10° west keeps "5 nm north of KOAK on
        // a vectored downwind for 30" semantically intact while putting KOAK clearly
        // inside the forward cone.
        var (spawnLat, spawnLon) = GeoMath.ProjectPointRaw(KoakArpLat, KoakArpLon, 350.0, 5.0);
        var aircraft = new AircraftState
        {
            Callsign = "UAL738",
            AircraftType = "B738",
            Position = new LatLon(spawnLat, spawnLon),
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = 5000,
            IndicatedAirspeed = 230,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = 5000,
            },
        };
        engine.World.AddAircraft(aircraft);
        output.WriteLine($"Spawned UAL738 at ({spawnLat:F6},{spawnLon:F6}), hdg=090, alt=5000, ias=230");

        var rfis = engine.SendCommand("UAL738", "RFIS");
        output.WriteLine($"RFIS result: {rfis.Success} — {rfis.Message}");
        Assert.True(rfis.Success, $"RFIS failed: {rfis.Message}");
        Assert.True(aircraft.Approach.HasReportedFieldInSight, $"RFIS soft-failed (field not visible). Diagnostic: {rfis.Message}");

        var cva = engine.SendCommand("UAL738", "CVA 30");
        output.WriteLine($"CVA 30 result: {cva.Success} — {cva.Message}");
        Assert.True(cva.Success, $"CVA 30 failed: {cva.Message}");

        Assert.NotNull(aircraft.Phases);
        Assert.Equal("30", aircraft.Phases.AssignedRunway?.Designator);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        output.WriteLine($"Phases after CVA: [{string.Join(", ", phaseTypes.Select(t => t.Name))}]");
        Assert.Contains(typeof(PatternEntryPhase), phaseTypes);
        Assert.Contains(typeof(DownwindPhase), phaseTypes);
        Assert.Contains(typeof(BasePhase), phaseTypes);
        Assert.Contains(typeof(FinalApproachPhase), phaseTypes);
        Assert.Contains(typeof(LandingPhase), phaseTypes);

        var cland = engine.SendCommand("UAL738", "CLAND");
        output.WriteLine($"CLAND result: {cland.Success} — {cland.Message}");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);

        TickUntilLandedOrGoAround(engine, aircraft, maxSeconds: 600);
    }

    /// <summary>
    /// Vectored to a right-base leg 15 nm NE of KOAK at 4000 ft. Heading is
    /// derived as `final - 80°` so the aircraft is 80° off the final course
    /// — solidly inside the CVA angled-join branch (>30° and ≤90°), which
    /// runs ApproachNavigationPhase (intercept point at 5 nm out for jets)
    /// → FinalApproachPhase → LandingPhase. A pure perpendicular (90°) lands
    /// on the boundary and risks falling into pattern entry, so we offset
    /// by 10° toward final.
    /// </summary>
    [Fact]
    public void Cva_FromNortheastBase_RfisThenCvaThenCland_Lands()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway30 = navDb.GetRunway("OAK", "30");
        Assert.NotNull(runway30);

        double baseHeading = (runway30.TrueHeading.Degrees - 80 + 360) % 360;
        var (spawnLat, spawnLon) = GeoMath.ProjectPointRaw(KoakArpLat, KoakArpLon, 45.0, 15.0);
        var aircraft = new AircraftState
        {
            Callsign = "DAL738",
            AircraftType = "B738",
            Position = new LatLon(spawnLat, spawnLon),
            TrueHeading = new TrueHeading(baseHeading),
            TrueTrack = new TrueHeading(baseHeading),
            Altitude = 4000,
            IndicatedAirspeed = 230,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = 4000,
            },
        };
        engine.World.AddAircraft(aircraft);
        output.WriteLine($"Spawned DAL738 at ({spawnLat:F6},{spawnLon:F6}), hdg={baseHeading:F1} (final-80°), alt=4000, ias=230");

        var rfis = engine.SendCommand("DAL738", "RFIS");
        output.WriteLine($"RFIS result: {rfis.Success} — {rfis.Message}");
        Assert.True(rfis.Success, $"RFIS failed: {rfis.Message}");
        Assert.True(aircraft.Approach.HasReportedFieldInSight, "RFIS soft-failed: field should be visible from base leg.");

        var cva = engine.SendCommand("DAL738", "CVA 30");
        output.WriteLine($"CVA 30 result: {cva.Success} — {cva.Message}");
        Assert.True(cva.Success, $"CVA 30 failed: {cva.Message}");

        Assert.NotNull(aircraft.Phases);
        Assert.Equal("30", aircraft.Phases.AssignedRunway?.Designator);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        output.WriteLine($"Phases after CVA: [{string.Join(", ", phaseTypes.Select(t => t.Name))}]");
        Assert.Contains(typeof(ApproachNavigationPhase), phaseTypes);
        Assert.Contains(typeof(FinalApproachPhase), phaseTypes);
        Assert.Contains(typeof(LandingPhase), phaseTypes);
        Assert.DoesNotContain(typeof(DownwindPhase), phaseTypes);
        Assert.DoesNotContain(typeof(BasePhase), phaseTypes);
        Assert.DoesNotContain(typeof(PatternEntryPhase), phaseTypes);

        var cland = engine.SendCommand("DAL738", "CLAND");
        output.WriteLine($"CLAND result: {cland.Success} — {cland.Message}");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);

        // 80° off-final from 15 nm out at 4000 ft → ~4 min of ApproachNavigation
        // before intercept, then full rollout from 140 kt touchdown speed.
        TickUntilLandedOrGoAround(engine, aircraft, maxSeconds: 500);
    }

    /// <summary>
    /// Almost-straight-in visual approach: B738 15 nm east of KOAK at 3400 ft
    /// heading 280°. With runway 30's actual true heading ~313° (300° magnetic
    /// + ~13°E variation), heading 280° is ~33° off final — just past the 30°
    /// straight-in threshold, so CVA routes through the angled-join branch.
    /// What matters operationally is the same: no pattern phases (no
    /// downwind/base/entry circuit), aircraft lines up with final and lands.
    /// </summary>
    [Fact]
    public void Cva_FromEastStraightIn_RfisThenCvaThenCland_Lands()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var runway30 = navDb.GetRunway("OAK", "30");
        Assert.NotNull(runway30);

        var (spawnLat, spawnLon) = GeoMath.ProjectPointRaw(KoakArpLat, KoakArpLon, 90.0, 15.0);
        var aircraft = new AircraftState
        {
            Callsign = "AAL738",
            AircraftType = "B738",
            Position = new LatLon(spawnLat, spawnLon),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 3400,
            IndicatedAirspeed = 230,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = 3400,
            },
        };
        engine.World.AddAircraft(aircraft);
        output.WriteLine($"Spawned AAL738 at ({spawnLat:F6},{spawnLon:F6}), hdg=280, alt=3400, ias=230");

        var rfis = engine.SendCommand("AAL738", "RFIS");
        output.WriteLine($"RFIS result: {rfis.Success} — {rfis.Message}");
        Assert.True(rfis.Success, $"RFIS failed: {rfis.Message}");
        Assert.True(aircraft.Approach.HasReportedFieldInSight, $"RFIS soft-failed: field should be visible. Diagnostic: {rfis.Message}");

        var cva = engine.SendCommand("AAL738", "CVA 30");
        output.WriteLine($"CVA 30 result: {cva.Success} — {cva.Message}");
        Assert.True(cva.Success, $"CVA 30 failed: {cva.Message}");

        Assert.NotNull(aircraft.Phases);
        Assert.Equal("30", aircraft.Phases.AssignedRunway?.Designator);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        output.WriteLine($"Phases after CVA: [{string.Join(", ", phaseTypes.Select(t => t.Name))}]");
        Assert.Contains(typeof(FinalApproachPhase), phaseTypes);
        Assert.Contains(typeof(LandingPhase), phaseTypes);
        // No pattern circuit — straight-in or angled-join is acceptable for this geometry.
        Assert.DoesNotContain(typeof(DownwindPhase), phaseTypes);
        Assert.DoesNotContain(typeof(BasePhase), phaseTypes);
        Assert.DoesNotContain(typeof(PatternEntryPhase), phaseTypes);

        var cland = engine.SendCommand("AAL738", "CLAND");
        output.WriteLine($"CLAND result: {cland.Success} — {cland.Message}");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);

        TickUntilLandedOrGoAround(engine, aircraft, maxSeconds: 400);
    }

    private void TickUntilLandedOrGoAround(SimulationEngine engine, AircraftState aircraft, int maxSeconds)
    {
        bool landed = false;
        bool goAround = false;
        string? goAroundMessage = null;

        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();

            if (aircraft.IsOnGround && aircraft.GroundSpeed < 40)
            {
                output.WriteLine($"t+{t}s: landed, gs={aircraft.GroundSpeed:F1}kts, pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6})");
                landed = true;
                break;
            }

            foreach (var w in aircraft.PendingWarnings)
            {
                if (w.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"t+{t}s: WARNING: {w}");
                    goAround = true;
                    goAroundMessage = w;
                }
            }

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"  t+{t}s: alt={aircraft.Altitude:F0}ft, gs={aircraft.GroundSpeed:F1}kt, hdg={aircraft.TrueHeading.Degrees:F0}, "
                        + $"phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}"
                );
            }
        }

        Assert.False(goAround, $"Aircraft went around: {goAroundMessage}");
        Assert.True(
            landed,
            $"Aircraft did not land within {maxSeconds}s. "
                + $"Final state: alt={aircraft.Altitude:F0}ft, gs={aircraft.GroundSpeed:F1}kt, "
                + $"phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}, "
                + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6})"
        );
    }
}
