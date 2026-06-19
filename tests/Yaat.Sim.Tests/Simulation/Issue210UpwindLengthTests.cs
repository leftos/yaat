using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E diagnostic for GitHub issue #210: "Adjust upwind length for a smaller specified pattern".
///
/// At KOAK 28L (runway 1.02 nm long, authored patternSize 0.5 nm / TPA 600 ft AGL, right traffic)
/// closed-traffic aircraft fly a very long upwind that reaches toward the runway-30 corridor. Per the
/// ZOA WM ruling (AIM 4-3-2): pattern aircraft fly over the departure end of the runway (DER) and turn
/// crosswind there, once within 300 ft of pattern altitude. The upwind should therefore end at the DER
/// using the AUTHORED low TPA, not climb far past the DER to a category-default TPA.
///
/// This file first MEASURES the binding constraint (authored TPA applied? geometric vs altitude gate),
/// then asserts the corrected behavior.
/// </summary>
public class Issue210UpwindLengthTests(ITestOutputHelper output)
{
    private static PhaseContext Ctx(
        AircraftState ac,
        AircraftCategory cat,
        RunwayInfo rwy,
        double dt = 1.0,
        Data.Airport.AirportGroundLayout? groundLayout = null
    ) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = cat,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            GroundLayout = groundLayout,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
        };

    private static AircraftState MakeDepartingPiston(RunwayInfo rwy)
    {
        // Just-airborne off the threshold on runway heading, accelerating/climbing.
        var ac = new AircraftState
        {
            Callsign = "N12345",
            AircraftType = "C172",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 50,
            IndicatedAirspeed = 65,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        return ac;
    }

    /// <summary>
    /// Ticks a piston through a closed-traffic upwind built with the given size/altitude overrides and
    /// returns the along-track distance (nm) from the threshold at which the aircraft turns crosswind
    /// (UpwindPhase completes), plus the resolved pattern altitude and crosswind-turn-point along-track.
    /// </summary>
    private (double upwindEndNm, double patternAltMsl, double crosswindPointNm, double altAtTurnAgl) MeasureUpwind(
        RunwayInfo rwy,
        IReadOnlyList<RunwayInfo> allRunways,
        double? sizeOverrideNm,
        double? altitudeOverrideFt,
        string label
    )
    {
        var cat = AircraftCategory.Piston;
        var waypoints = PatternGeometry.Compute(rwy, cat, PatternDirection.Right, sizeOverrideNm, altitudeOverrideFt, allRunways);

        double crosswindPointNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon),
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading
        );

        var ac = MakeDepartingPiston(rwy);
        var circuit = PatternBuilder.BuildCircuit(
            rwy,
            cat,
            PatternDirection.Right,
            PatternEntryLeg.Upwind,
            true,
            null,
            sizeOverrideNm,
            altitudeOverrideFt,
            allRunways
        );
        foreach (var p in circuit)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac, cat, rwy));

        double upwindEndNm = double.NaN;
        double altAtTurnAgl = double.NaN;
        for (int t = 1; t <= 600; t++)
        {
            var ctx = Ctx(ac, cat, rwy);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            double alongTrack = GeoMath.AlongTrackDistanceNm(ac.Position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading);

            if (t % 10 == 0)
            {
                output.WriteLine($"  [{label}] t={t} alongTrk={alongTrack:F2}nm alt={ac.Altitude:F0}ft phase={ac.Phases?.CurrentPhase?.Name}");
            }

            if (ac.Phases?.CurrentPhase is not UpwindPhase && double.IsNaN(upwindEndNm))
            {
                upwindEndNm = alongTrack;
                altAtTurnAgl = ac.Altitude - rwy.ElevationFt;
                break;
            }
        }

        output.WriteLine(
            $"[{label}] patternAlt={waypoints.PatternAltitude:F0}MSL crosswindPoint={crosswindPointNm:F2}nm "
                + $"=> UPWIND ENDS at {upwindEndNm:F2}nm (alt {altAtTurnAgl:F0}AGL); runwayLen={rwy.LengthFt / 6076.12:F2}nm"
        );
        return (upwindEndNm, waypoints.PatternAltitude, crosswindPointNm, altAtTurnAgl);
    }

    [Fact]
    public void Diagnostic_Oak28L_UpwindLength_AuthoredVsDefault()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var allRunways = navDb.GetRunways("KOAK");

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var authored = layout.FindRunway("28L");
        output.WriteLine($"authored 28L: PatternAltitudeAglFt={authored?.PatternAltitudeAglFt} PatternSizeNm={authored?.PatternSizeNm}");

        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(rwy28L, authored, commandSizeNm: null, commandAltitudeMslFt: null);
        output.WriteLine($"resolved override: sizeOv={sizeOv} altOv={altOv}");

        output.WriteLine("--- WITH authored override (size 0.5, alt 609 MSL) ---");
        var authoredRun = MeasureUpwind(rwy28L, allRunways, sizeOv, altOv, "authored");

        output.WriteLine("--- WITHOUT override (category default TPA/size) ---");
        var defaultRun = MeasureUpwind(rwy28L, allRunways, null, null, "default");

        double runwayLenNm = RunwayLengthNm(rwy28L);
        // Authored low TPA → at-the-DER upwind. Category-default high TPA → the 300-below-TPA gate isn't
        // met by the DER, so the aircraft is climb-bound past it: a longer upwind toward RWY 30.
        Assert.Equal(609, authoredRun.patternAltMsl, 0);
        Assert.Equal(1009, defaultRun.patternAltMsl, 0);
        Assert.True(authoredRun.upwindEndNm <= runwayLenNm + 0.10, $"authored upwind {authoredRun.upwindEndNm:F2} should end at the DER");
        Assert.True(defaultRun.upwindEndNm > authoredRun.upwindEndNm, "default-TPA upwind should be climb-bound past the DER");
    }

    private static double RunwayLengthNm(RunwayInfo rwy) => rwy.LengthFt / 6076.12;

    [Fact]
    public void Compute_CrosswindTurn_AtDepartureEnd_IndependentOfPatternSize()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var allRunways = navDb.GetRunways("KOAK");
        double runwayLenNm = RunwayLengthNm(rwy28L);

        double CrosswindTurnAlongTrack(double? sizeNm)
        {
            var wp = PatternGeometry.Compute(rwy28L, AircraftCategory.Piston, PatternDirection.Right, sizeNm, 609, allRunways);
            return GeoMath.AlongTrackDistanceNm(
                new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon),
                new LatLon(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude),
                rwy28L.TrueHeading
            );
        }

        double small = CrosswindTurnAlongTrack(0.5);
        double normal = CrosswindTurnAlongTrack(1.0);

        // Per AIM 4-3-2 the crosswind turn is at the departure end of the runway, governed by runway
        // geometry — NOT by pattern size. The crosswind-turn point must sit at the DER (≈ runway length
        // from the threshold) regardless of how small the pattern is.
        Assert.Equal(runwayLenNm, small, 1);
        Assert.Equal(runwayLenNm, normal, 1);
        Assert.Equal(small, normal, 2);
    }

    [Fact]
    public void Oak28L_AuthoredPattern_TurnsCrosswindAtDepartureEnd()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var allRunways = navDb.GetRunways("KOAK");
        double runwayLenNm = RunwayLengthNm(rwy28L);

        var (upwindEndNm, _, _, _) = MeasureUpwind(rwy28L, allRunways, sizeOverrideNm: 0.5, altitudeOverrideFt: 609, "authored");

        // Aircraft must fly over the DER before turning crosswind (AIM 4-3-2), and must not be carried
        // far past it toward the runway-30 corridor.
        Assert.True(upwindEndNm >= runwayLenNm - 0.07, $"Upwind ended at {upwindEndNm:F2}nm — turned crosswind before the DER ({runwayLenNm:F2}nm)");
        Assert.True(
            upwindEndNm <= runwayLenNm + 0.20,
            $"Upwind ended at {upwindEndNm:F2}nm — carried well past the DER ({runwayLenNm:F2}nm) toward RWY 30"
        );
    }

    /// <summary>
    /// Exercises the real runtime path (DepartureClearanceHandler.ApplyClosedTraffic) to confirm
    /// whether the authored pattern altitude reaches the UpwindPhase — with and without the
    /// per-aircraft Ground.Layout loaded.
    /// </summary>
    [Fact]
    public void Diagnostic_ApplyClosedTraffic_AuthoredAltitudeReachesUpwind()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        double? PatternAltVia(bool withLayout)
        {
            var ac = new AircraftState
            {
                Callsign = "N12345",
                AircraftType = "C172",
                Position = new LatLon(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude),
                TrueHeading = rwy28L.TrueHeading,
                Altitude = rwy28L.ElevationFt,
                IsOnGround = true,
                FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            };
            ac.Phases = new PhaseList { AssignedRunway = rwy28L };
            if (withLayout)
            {
                ac.Ground.Layout = layout;
            }

            DepartureClearanceHandler.ApplyClosedTraffic(
                new ClosedTrafficDeparture(PatternDirection.Right, null, null),
                ac,
                ac.Phases,
                rwy28L,
                removeInitialClimb: false
            );

            var upwind = ac.Phases.Phases.OfType<UpwindPhase>().FirstOrDefault();
            return upwind?.Waypoints?.PatternAltitude;
        }

        double? withLayoutAlt = PatternAltVia(withLayout: true);
        double? withoutLayoutAlt = PatternAltVia(withLayout: false);

        output.WriteLine($"ApplyClosedTraffic patternAlt: withGroundLayout={withLayoutAlt} withoutGroundLayout={withoutLayoutAlt}");
        // Field elev 9ft: authored 600 AGL => 609 MSL; piston default 1000 AGL => 1009 MSL. The first
        // circuit is built while the aircraft is on the ground, where Ground.Layout is loaded, so the
        // authored TPA is applied; the auto-cycle covers the airborne case via ctx.GroundLayout.
        Assert.Equal(609, withLayoutAlt!.Value, 0);
    }

    /// <summary>
    /// The pattern auto-cycle (PhaseRunner) must resolve the authored pattern altitude even when the
    /// per-aircraft Ground.Layout is unset, by falling back to the context's resolved ground layout —
    /// otherwise every circuit after a touch-and-go reverts to the category-default TPA and a long upwind.
    /// </summary>
    [Fact]
    public void AutoCycle_WithoutAircraftGroundLayout_AppliesAuthoredAltitudeFromContext()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var allRunways = navDb.GetRunways("KOAK");
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var cat = AircraftCategory.Piston;
        // First circuit built with the authored override so the entry leg is flyable; alt 609.
        var wp = PatternGeometry.Compute(rwy28L, cat, PatternDirection.Right, 0.5, 609, allRunways);
        var ac = new AircraftState
        {
            Callsign = "N12345",
            AircraftType = "C172",
            Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon),
            TrueHeading = wp.DownwindHeading,
            TrueTrack = wp.DownwindHeading,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy28L, TrafficDirection = PatternDirection.Right };
        // Key: the aircraft has NO cached ground layout — resolution must come from ctx.GroundLayout.
        ac.Ground.Layout = null;

        var circuit = PatternBuilder.BuildCircuit(rwy28L, cat, PatternDirection.Right, PatternEntryLeg.Downwind, true, null, 0.5, 609, allRunways);
        foreach (var p in circuit)
        {
            ac.Phases.Add(p);
        }
        ac.Phases.Start(Ctx(ac, cat, rwy28L, groundLayout: layout));
        ac.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;

        UpwindPhase? secondUpwind = null;
        bool sawTouchAndGo = false;
        for (int i = 0; i < 4000 && secondUpwind is null; i++)
        {
            var ctx = Ctx(ac, cat, rwy28L, groundLayout: layout);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            var current = ac.Phases.CurrentPhase;
            if (current is TouchAndGoPhase)
            {
                sawTouchAndGo = true;
            }
            else if (sawTouchAndGo && current is UpwindPhase up)
            {
                secondUpwind = up;
            }
        }

        Assert.NotNull(secondUpwind);
        output.WriteLine($"second-circuit UpwindPhase patternAlt={secondUpwind!.Waypoints?.PatternAltitude}");
        // Authored 600 AGL over field elev 9 = 609 MSL. Must NOT be the piston default 1009.
        Assert.Equal(609, secondUpwind.Waypoints!.PatternAltitude, 0);
    }

    /// <summary>
    /// A manual pattern-entry command (TryEnterPattern) must apply the authored pattern altitude from
    /// the resolved ground layout passed by the dispatcher, even when the per-aircraft Ground.Layout is
    /// unset (airborne-spawned pattern aircraft). Without it the first circuit reverts to the default TPA.
    /// </summary>
    [Fact]
    public void EnterPattern_WithoutAircraftGroundLayout_AppliesAuthoredAltitudeFromPassedLayout()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy28L = navDb.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy28L);
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N12345",
            AircraftType = "C172",
            Position = new LatLon(37.87, -122.22), // ~10 nm north of OAK (right-pattern side for 28L)
            TrueHeading = new TrueHeading(180),
            Altitude = 1500,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy28L };
        ac.Ground.Layout = null; // airborne aircraft with no cached layout

        var result = PatternCommandHandler.TryEnterPattern(
            ac,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28L",
            finalDistanceNm: null,
            groundLayout: layout
        );
        Assert.True(result.Success, result.Message);

        var downwind = ac.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(downwind);
        Assert.Equal(609, downwind!.Waypoints!.PatternAltitude, 0);
    }
}
