using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pilot-initiated go-around when the runway is occupied on short final (AIM 5-2-5.9: never land on
/// an occupied runway even with a clearance; AIM 5-5-5.a.2: say why). The gate is time-to-threshold
/// with the 7110.65 §3-10-3 landmark exceptions, so a landed aircraft far enough down the runway does
/// not trigger it.
/// </summary>
public class OccupiedRunwayGoAroundTests
{
    private const double ElevationFt = 100;

    private static readonly RunwayInfo Runway = MakeRunway();

    public OccupiedRunwayGoAroundTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo MakeRunway()
    {
        var threshold = new LatLon(37.0, -122.0);
        var end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), 10_000 / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280,
            elevationFt: ElevationFt
        );
    }

    private static LatLon Threshold => new(Runway.ThresholdLatitude, Runway.ThresholdLongitude);

    private static LatLon OnRunway(double alongFt) => GeoMath.ProjectPoint(Threshold, Runway.TrueHeading, alongFt / GeoMath.FeetPerNm);

    private static AircraftState Arrival(string type, double distanceNm, double aglFt)
    {
        var ac = new AircraftState
        {
            Callsign = "ARR1",
            AircraftType = type,
            Position = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distanceNm),
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = ElevationFt + aglFt,
            IndicatedAirspeed = type == "C172" ? 70 : 130,
            VerticalSpeed = -600,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST", Destination = "KTEST" },
            Phases = new PhaseList { AssignedRunway = Runway },
        };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        return ac;
    }

    private static AircraftState Occupant(string type, LatLon position, double groundSpeedKts, Phase? phase, bool onGround)
    {
        var ac = new AircraftState
        {
            Callsign = "OCC1",
            AircraftType = type,
            Position = position,
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = ElevationFt,
            IndicatedAirspeed = groundSpeedKts,
            IsOnGround = onGround,
        };
        if (phase is not null)
        {
            ac.Phases = new PhaseList { AssignedRunway = Runway };
            ac.Phases.Add(phase);
            ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        }

        return ac;
    }

    private static PhaseContext Ctx(AircraftState arrival, IReadOnlyList<AircraftState> all, bool setting) =>
        new()
        {
            Aircraft = arrival,
            Targets = arrival.Targets,
            Category = AircraftCategorization.Categorize(arrival.AircraftType),
            DeltaSeconds = 1.0,
            Runway = Runway,
            FieldElevation = Runway.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            AutoGoAroundOnOccupiedRunway = setting,
            ListAircraft = () => all,
        };

    private static bool GoesAround(AircraftState arrival, AircraftState occupant, bool setting)
    {
        var ctx = Ctx(arrival, [arrival, occupant], setting);
        arrival.Phases!.Start(ctx);
        arrival.Phases.CurrentPhase!.OnTick(ctx);
        return arrival.Phases.CurrentPhase is GoAroundPhase;
    }

    [Fact]
    public void LinedUpOccupant_TriggersGoAroundWithSpokenReason()
    {
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
        Assert.Contains(arrival.PendingWarnings, w => w.Contains("going around, traffic on the runway", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingOff_NoGoAround()
    {
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: false));
    }

    [Fact]
    public void ForcedLanding_NoGoAround()
    {
        var arrival = Arrival("B738", 0.6, 150);
        arrival.Phases!.ForceLanding = true;
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void ArrivalStillOutsideThirtySeconds_NoGoAround()
    {
        var arrival = Arrival("B738", 2.0, 600);
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void BelowThresholdCrossingHeight_NoGoAround()
    {
        // Under 50 ft AGL the aircraft is landing; there is no balked landing from the flare for traffic.
        var arrival = Arrival("B738", 0.1, 30);
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void HelicopterArrival_NoGoAround()
    {
        var arrival = Arrival("EC35", 0.4, 120);
        var occupant = Occupant("B738", OnRunway(300), 0, new LinedUpAndWaitingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIAheadBeyondLandmark_NoGoAround()
    {
        // §3-10-3.a.1: a Category I arrival may cross the threshold with a landed Category I still
        // rolling 3,000 ft down the runway.
        var arrival = Arrival("C172", 0.4, 120);
        var occupant = Occupant("C172", OnRunway(3500), 40, new LandingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void LandedCategoryIAheadInsideLandmark_ProjectedToThresholdCrossing()
    {
        // §3-10-3 is judged when the arrival crosses the threshold: a rollout still moving at 40 kt
        // 2,000 ft down the runway will be past 3,000 ft by then; one that has stopped there will not.
        var rolling = Arrival("C172", 0.4, 120);
        var rollingOccupant = Occupant("C172", OnRunway(2000), 40, new LandingPhase(), onGround: true);
        var stopped = Arrival("C172", 0.4, 120);
        var stoppedOccupant = Occupant("C172", OnRunway(2000), 0, new LandingPhase(), onGround: true);

        Assert.False(GoesAround(rolling, rollingOccupant, setting: true));
        Assert.True(GoesAround(stopped, stoppedOccupant, setting: true));
    }

    [Fact]
    public void StopAndGoStoppedBeyondLandmark_NoGoAround()
    {
        // §3-10-3.a.1 has no motion requirement: a landed Category I stopped 3,500 ft down is legal to land behind.
        var arrival = Arrival("C172", 0.4, 120);
        var occupant = Occupant("C172", OnRunway(3500), 0, new StopAndGoPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void ExitingOffTheCenterlineInsidePavement_UsesTheLandingLandmark()
    {
        // A light twin still inside the pavement rectangle while turning off at 5,000 ft: a.1 (4,500 ft) is met.
        var arrival = Arrival("C172", 0.4, 120);
        var occupant = Occupant("BE58", OnRunway(5000), 15, new RunwayExitPhase(), onGround: true);
        occupant.TrueHeading = new TrueHeading(250);
        occupant.TrueTrack = new TrueHeading(250);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void CategoryIIIArrivalBehindLandedAircraft_GoesAround()
    {
        // No landmark exception when either aircraft is Category III: the runway must be clear.
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(5000), 60, new LandingPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void StoppedOccupantBeyondLandmark_GoesAround()
    {
        var arrival = Arrival("C172", 0.4, 120);
        var occupant = Occupant("C172", OnRunway(4000), 0, new HoldingInPositionPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void AirborneDeparture_LandmarkIs6000FtForCategoryIII()
    {
        // §3-10-3.a.2: an airborne departure need not have crossed the runway end when it is 6,000 ft
        // (either aircraft Category III) from the landing threshold — projected to the arrival's crossing.
        var near = Arrival("B738", 0.6, 150);
        var nearOccupant = Occupant("B738", OnRunway(1000), 150, new TakeoffPhase(), onGround: false);
        nearOccupant.Altitude = ElevationFt + 100;
        var far = Arrival("B738", 0.6, 150);
        var farOccupant = Occupant("B738", OnRunway(4000), 150, new TakeoffPhase(), onGround: false);
        farOccupant.Altitude = ElevationFt + 300;

        Assert.True(GoesAround(near, nearOccupant, setting: true));
        Assert.False(GoesAround(far, farOccupant, setting: true));
    }

    [Fact]
    public void RollingDeparture_ProjectedPastTheLandmark_NoGoAround()
    {
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(5000), 100, new TakeoffPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void DepartureLinedUpAndStopped_GoesAround()
    {
        // Neither landed nor departed: no exception at any distance.
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(4000), 0, new TakeoffPhase(), onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void CrossingOccupant_GoesAround()
    {
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(6000), 15, new TaxiingPhase(), onGround: true);
        occupant.TrueHeading = new TrueHeading(10);
        occupant.TrueTrack = new TrueHeading(10);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void OccupantOffThePavement_NoGoAround()
    {
        var beside = GeoMath.ProjectPoint(OnRunway(2000), Runway.TrueHeading + 90, 400 / GeoMath.FeetPerNm);
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", beside, 15, new TaxiingPhase(), onGround: true);

        Assert.False(GoesAround(arrival, occupant, setting: true));
    }

    [Fact]
    public void HelicopterHoveringDownOntoTheRunwayBeyondTheLandmark_GoesAround()
    {
        // A preceding rotorcraft has no §3-10-3 exception: it has not landed (a.1) and never rolled (a.2), and the
        // landmark categories are fixed-wing classes — 4,000 ft down the runway it still blocks.
        var arrival = Arrival("C172", 0.4, 120);
        var hovering = Occupant("EC35", OnRunway(4000), 5, phase: null, onGround: false);
        hovering.Altitude = ElevationFt + 40;
        hovering.VerticalSpeed = -200;

        Assert.True(GoesAround(arrival, hovering, setting: true));
    }

    [Fact]
    public void PhaselessOccupantParkedOnThePavement_GoesAround()
    {
        var arrival = Arrival("B738", 0.6, 150);
        var occupant = Occupant("B738", OnRunway(1500), 0, phase: null, onGround: true);

        Assert.True(GoesAround(arrival, occupant, setting: true));
    }
}
