using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// Tower-cab reports are routed by the position the pilot is tuned to (YAAT-1934), not by the hardcoded tower/approach
/// position lists: a solo student outside those lists still hears a report from a pilot on their side, and a report from
/// a pilot tuned away from the student is shown only as a warning.
/// </summary>
public class TowerReportRoutingTests
{
    private const string SoloStudentOnGround = "GND";
    private const string SoloStudentOnApproach = "APP";

    private readonly RunwayInfo _runway = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TowerReportRoutingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RejectedTakeoff_StoppedOnRunway_NoTunedPosition_IsSpoken()
    {
        if (StudentTower() is not { } student)
        {
            return;
        }

        AircraftState ac = RollingDeparture(indicatedAirspeedKts: 60, tunedPosition: null);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnGround);

        Assert.True(RunToStop(ac, ctx), "the rejected takeoff never came to a stop on the runway");
        AssertSingleSpoken(ac, "stopped on the runway");
    }

    [Fact]
    public void RejectedTakeoff_StoppedOnRunway_TunedToTheStudent_IsSpoken()
    {
        if (StudentTower() is not { } student)
        {
            return;
        }

        AircraftState ac = RollingDeparture(indicatedAirspeedKts: 60, tunedPosition: student);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnGround);

        Assert.True(RunToStop(ac, ctx), "the rejected takeoff never came to a stop on the runway");
        AssertSingleSpoken(ac, "stopped on the runway");
    }

    [Fact]
    public void RejectedTakeoff_StoppedOnRunway_TunedToAnotherPosition_IsAWarning()
    {
        if ((StudentTower() is not { } student) || (AnotherPosition() is not { } other))
        {
            return;
        }

        AircraftState ac = RollingDeparture(indicatedAirspeedKts: 60, tunedPosition: other);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnGround);

        Assert.True(RunToStop(ac, ctx), "the rejected takeoff never came to a stop on the runway");
        AssertWarningOnly(ac, "stopped on the runway");
    }

    [Fact]
    public void FinalApproach_ShortFinalReminder_NoTunedPosition_IsSpoken()
    {
        if (StudentTower() is not { } student)
        {
            return;
        }

        AircraftState ac = FinalArrival(altitudeFt: 301, indicatedAirspeedKts: 140, tunedPosition: null);
        FinalApproachPhase phase = AddUnclearedFinal(ac);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnApproach);
        ac.Phases!.Start(ctx);
        Assert.Null(ac.TunedPosition);

        phase.OnTick(ctx);

        AssertSingleSpoken(ac, "short final");
    }

    [Fact]
    public void FinalApproach_ShortFinalReminder_TunedToTheStudent_IsSpoken()
    {
        if (StudentTower() is not { } student)
        {
            return;
        }

        AircraftState ac = FinalArrival(altitudeFt: 301, indicatedAirspeedKts: 140, tunedPosition: student);
        FinalApproachPhase phase = AddUnclearedFinal(ac);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnApproach);
        ac.Phases!.Start(ctx);
        Assert.Same(student, ac.TunedPosition);

        phase.OnTick(ctx);

        AssertSingleSpoken(ac, "short final");
    }

    [Fact]
    public void FinalApproach_ShortFinalReminder_TunedToAnotherPosition_IsAWarning()
    {
        if ((StudentTower() is not { } student) || (AnotherPosition() is not { } other))
        {
            return;
        }

        AircraftState ac = FinalArrival(altitudeFt: 301, indicatedAirspeedKts: 140, tunedPosition: other);
        FinalApproachPhase phase = AddUnclearedFinal(ac);
        PhaseContext ctx = Ctx(ac, student, SoloStudentOnApproach);
        ac.Phases!.Start(ctx);
        Assert.Same(other, ac.TunedPosition);

        phase.OnTick(ctx);

        AssertWarningOnly(ac, "short final");
    }

    private PhaseContext Ctx(AircraftState aircraft, TrackOwner student, string studentPositionType) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1.0,
            Runway = _runway,
            FieldElevation = _runway.ElevationFt,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPosition = student,
            StudentPositionType = studentPositionType,
        };

    /// <summary>The ZOA tower position the student works; null when the test data is absent.</summary>
    private TrackOwner? StudentTower() => Resolve("OAK_TWR");

    /// <summary>A real ZOA position other than the student's, for the tuned-away cases.</summary>
    private TrackOwner? AnotherPosition() => Resolve("OAK_GND");

    private TrackOwner? Resolve(string callsign)
    {
        if (_zoa is null)
        {
            return null;
        }

        PositionConfig? position = _zoa.FindPositionByCallsign(callsign, facilityHint: null);
        return position is null ? null : _zoa.ResolvePosition(position.Id);
    }

    private AircraftState RollingDeparture(double indicatedAirspeedKts, TrackOwner? tunedPosition)
    {
        var ac = new AircraftState
        {
            Callsign = "DEP1",
            AircraftType = "B738",
            Position = new LatLon(_runway.ThresholdLatitude, _runway.ThresholdLongitude),
            TrueHeading = _runway.TrueHeading,
            TrueTrack = _runway.TrueHeading,
            Altitude = _runway.ElevationFt,
            IndicatedAirspeed = indicatedAirspeedKts,
            IsOnGround = true,
            TunedPosition = tunedPosition,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Altitude = PlannedAltitude.Ifr(5000) },
            Phases = new PhaseList { AssignedRunway = _runway },
        };
        ac.Phases.Add(new RejectedTakeoffPhase(rollElapsedSeconds: 0));
        return ac;
    }

    private static bool RunToStop(AircraftState ac, PhaseContext ctx)
    {
        ac.Phases!.Start(ctx);
        RejectedTakeoffPhase phase = Assert.IsType<RejectedTakeoffPhase>(ac.Phases.CurrentPhase);
        for (int tick = 0; tick < 240; tick++)
        {
            if (phase.OnTick(ctx))
            {
                return true;
            }
        }

        return false;
    }

    private static AircraftState FinalArrival(double altitudeFt, double indicatedAirspeedKts, TrackOwner? tunedPosition) =>
        new()
        {
            Callsign = "ARR1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            Altitude = altitudeFt,
            IndicatedAirspeed = indicatedAirspeedKts,
            IsOnGround = false,
            TunedPosition = tunedPosition,
            // Announced upstream: OnStart does not check in, and checking in with the student re-tunes the pilot.
            HasMadeInitialContact = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList(),
        };

    private FinalApproachPhase AddUnclearedFinal(AircraftState ac)
    {
        var phase = new FinalApproachPhase();
        ac.Phases = new PhaseList
        {
            AssignedRunway = _runway,
            ActiveApproach = new ApproachClearance
            {
                TransitionName = null,
                ApproachId = "I28",
                AirportCode = "KTEST",
                RunwayId = _runway.Designator,
                FinalApproachCourse = _runway.TrueHeading,
                MapAltitudeFt = null,
                MapDistanceNm = 0.5,
            },
        };
        ac.Phases.Add(phase);
        return phase;
    }

    private static void AssertSingleSpoken(AircraftState ac, string expected)
    {
        PilotTransmission spoken = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains(expected, spoken.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PilotTransmissionKind.Proactive, spoken.Kind);
        Assert.Empty(ac.PendingWarnings);
    }

    private static void AssertWarningOnly(AircraftState ac, string expected)
    {
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Contains(ac.PendingWarnings, w => w.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }
}
