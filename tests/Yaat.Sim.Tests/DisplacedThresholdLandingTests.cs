using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #324. Approaches were flown to the pavement end, so on a runway with a displaced threshold the
/// aircraft touched down short of the landing threshold — on pavement that is departures-only for that
/// direction (AIM 2-3-3.b.8.2). KSJC 30L is the extreme case in the shipped test data: 2,537 ft of
/// displacement, more than the whole touchdown float, so every landing rolled onto unlandable pavement.
///
/// Drives the production path (<see cref="SimulationWorld.Tick"/> + <see cref="PhaseRunner.Tick"/> over
/// a real <c>[FinalApproachPhase, LandingPhase]</c> list) against the real KSJC airport map and nav
/// database, because the datum only exists once the ground layout is in the phase context.
/// </summary>
public class DisplacedThresholdLandingTests
{
    private readonly ITestOutputHelper _output;

    public DisplacedThresholdLandingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout SjcLayout() => GeoJsonParser.Parse("SJC", File.ReadAllText(Path.Combine("TestData", "sjc.geojson")), "SJC");

    private sealed record Touchdown(double PastPavementFt, double PastLandingThresholdFt, double DisplacementFt);

    /// <summary>
    /// Flies <paramref name="aircraftType"/> from a level 8 nm final onto <paramref name="designator"/>
    /// and reports where it touched down relative to both candidate datums.
    /// </summary>
    private Touchdown FlyApproach(string airportId, string designator, string aircraftType, double approachSpeedKt, AirportGroundLayout? layout)
    {
        var rwy = NavigationDatabase.Instance.GetRunway(airportId, designator);
        Assert.NotNull(rwy);

        var pavement = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var landing = LandingThreshold.Resolve(rwy, layout);
        var course = rwy.TrueHeading;

        const double startDistNm = 8.0;
        var start = GeoMath.ProjectPoint(pavement, course.ToReciprocal(), startDistNm);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = aircraftType,
            Position = start,
            TrueHeading = course,
            // Level well below the 8 nm glidepath: the phase holds altitude until the glideslope
            // descends onto it, which is the normal capture path (AIM 5-4-14).
            Altitude = rwy.ElevationFt + 2000,
            IndicatedAirspeed = approachSpeedKt,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = airportId },
        };
        ac.Ground.Layout = layout;

        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = $"I{designator}",
                AirportCode = airportId,
                RunwayId = designator,
                FinalApproachCourse = course,
            },
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        ac.Targets.TargetSpeed = approachSpeedKt;
        ac.Targets.AssignedAltitude = rwy.ElevationFt + 2000;

        void PreTick(AircraftState aircraft, double dt)
        {
            if (aircraft.Phases is null || aircraft.Phases.IsComplete)
            {
                return;
            }

            PhaseRunner.Tick(
                aircraft,
                new PhaseContext
                {
                    Aircraft = aircraft,
                    Targets = aircraft.Targets,
                    Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                    DeltaSeconds = dt,
                    Runway = aircraft.Phases.AssignedRunway,
                    FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
                    GroundLayout = aircraft.Ground.Layout,
                    Logger = NullLogger.Instance,
                    AutoClearedToLand = true,
                }
            );
        }

        var world = new SimulationWorld();
        world.AddAircraft(ac);

        const double dt = 0.25;
        const int maxTicks = 4 * 600;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Tick(dt, PreTick);

            foreach (var (callsign, warning) in world.DrainAllWarnings())
            {
                _output.WriteLine($"# tick {tick} {callsign}: {warning}");
                Assert.DoesNotContain("going around", warning, StringComparison.OrdinalIgnoreCase);
            }

            if (ac.IsOnGround)
            {
                double pastPavementFt = GeoMath.AlongTrackDistanceNm(ac.Position, pavement, course) * GeoMath.FeetPerNm;
                double pastLandingFt = GeoMath.AlongTrackDistanceNm(ac.Position, landing, course) * GeoMath.FeetPerNm;
                double displacementFt = LandingThreshold.DisplacementFt(rwy, layout);
                _output.WriteLine(
                    $"{airportId} {designator} {aircraftType}: touchdown {pastPavementFt:F0} ft past pavement, {pastLandingFt:F0} ft past the landing threshold (displaced {displacementFt:F0} ft)"
                );
                return new Touchdown(pastPavementFt, pastLandingFt, displacementFt);
            }
        }

        Assert.Fail($"{aircraftType} never touched down within {maxTicks * dt:F0} s");
        return null!;
    }

    /// <summary>
    /// The defect itself: with the airport map available, a landing on a displaced end must touch down
    /// inside the AIM 2-1-5.b touchdown zone measured from the *landing* threshold, not from the pavement.
    /// </summary>
    [Theory]
    [InlineData("B738", 140.0)]
    [InlineData("DH8D", 120.0)]
    [InlineData("C172", 70.0)]
    public void Landing_OnADisplacedThreshold_TouchesDownPastTheLandingThreshold(string aircraftType, double approachSpeedKt)
    {
        var touchdown = FlyApproach("KSJC", "30L", aircraftType, approachSpeedKt, SjcLayout());

        Assert.Equal(2537, touchdown.DisplacementFt);
        Assert.InRange(touchdown.PastLandingThresholdFt, 100, 3000);
    }

    /// <summary>
    /// Without a layout there is no displacement data, so the approach still flies to the pavement end.
    /// This is the fallback every existing recording replays through — it must not move.
    /// </summary>
    [Fact]
    public void Landing_WithoutALayout_StillFliesToThePavementThreshold()
    {
        var touchdown = FlyApproach("KSJC", "30L", "B738", 140.0, layout: null);

        Assert.Equal(0, touchdown.DisplacementFt);
        Assert.InRange(touchdown.PastPavementFt, 100, 3000);
    }

    /// <summary>
    /// An undisplaced end resolves to the same point either way, so supplying the layout changes nothing.
    /// Guards the bulk of the replay fixtures, which are KOAK (every end 0 ft).
    /// </summary>
    [Fact]
    public void Landing_OnAnUndisplacedThreshold_IsUnaffectedByTheLayout()
    {
        var oak = GeoJsonParser.Parse("OAK", File.ReadAllText(Path.Combine("TestData", "oak.geojson")), "OAK");

        var withLayout = FlyApproach("KOAK", "28R", "B738", 140.0, oak);
        var withoutLayout = FlyApproach("KOAK", "28R", "B738", 140.0, layout: null);

        Assert.Equal(0, withLayout.DisplacementFt);
        Assert.InRange(withLayout.PastPavementFt, withoutLayout.PastPavementFt - 1, withoutLayout.PastPavementFt + 1);
    }

    /// <summary>
    /// The arrival half of the pattern hangs off the landing threshold — the abeam point, and therefore
    /// the base turn, slide downfield with it. The departure half does not: AIM 4-3-2 anchors the
    /// crosswind turn beyond the *departure* end, and a takeoff may use the pre-threshold pavement in
    /// either direction (AIM 2-3-3.b.8.2), so the departure end and crosswind turn stay put.
    /// </summary>
    [Fact]
    public void PatternGeometry_DisplacedThreshold_MovesTheArrivalLegsOnly()
    {
        var layout = SjcLayout();
        var rwy = NavigationDatabase.Instance.GetRunway("KSJC", "30L");
        Assert.NotNull(rwy);
        var authored = layout.FindRunway("30L");
        Assert.NotNull(authored);

        var pavementPattern = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left, null, null, null, authoredRunway: null);
        var landingPattern = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left, null, null, null, authored);

        double thresholdMovedFt =
            GeoMath.DistanceNm(pavementPattern.ThresholdLat, pavementPattern.ThresholdLon, landingPattern.ThresholdLat, landingPattern.ThresholdLon)
            * GeoMath.FeetPerNm;
        double abeamMovedFt =
            GeoMath.DistanceNm(
                pavementPattern.DownwindAbeamLat,
                pavementPattern.DownwindAbeamLon,
                landingPattern.DownwindAbeamLat,
                landingPattern.DownwindAbeamLon
            ) * GeoMath.FeetPerNm;
        double baseMovedFt =
            GeoMath.DistanceNm(pavementPattern.BaseTurnLat, pavementPattern.BaseTurnLon, landingPattern.BaseTurnLat, landingPattern.BaseTurnLon)
            * GeoMath.FeetPerNm;

        Assert.InRange(thresholdMovedFt, 2537 - 5, 2537 + 5);
        Assert.InRange(abeamMovedFt, 2537 - 5, 2537 + 5);
        Assert.InRange(baseMovedFt, 2537 - 5, 2537 + 5);

        Assert.Equal(pavementPattern.DepartureEndLat, landingPattern.DepartureEndLat);
        Assert.Equal(pavementPattern.DepartureEndLon, landingPattern.DepartureEndLon);
        Assert.Equal(pavementPattern.CrosswindTurnLat, landingPattern.CrosswindTurnLat);
        Assert.Equal(pavementPattern.CrosswindTurnLon, landingPattern.CrosswindTurnLon);
    }
}
