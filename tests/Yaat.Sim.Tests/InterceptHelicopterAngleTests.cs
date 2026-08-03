using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// 7110.65 §5-9-2 TBL 5-9-1 allows a final-approach-course interception angle of up to 30° at
/// 2 mi or more from the approach gate — <b>45° for helicopters</b>. <see cref="InterceptCoursePhase"/>
/// hard-codes a 30° bust-through gate (<c>BustThroughAlignmentDeg</c>) for every category, so a
/// helicopter legally vectored to intercept at a 40° cut is wrongly refused ("Unable, passing through
/// localizer") and the approach is abandoned.
/// </summary>
public class InterceptHelicopterAngleTests
{
    private const double RunwayHeading = 280.0;
    private const double ThresholdLat = 37.72;
    private const double ThresholdLon = -122.22;

    private static AircraftState MakeAircraft(string type, double heading, double lat, double lon) =>
        new()
        {
            Callsign = "N911",
            AircraftType = type,
            TrueHeading = new TrueHeading(heading),
            Altitude = 2000,
            Position = new LatLon(lat, lon),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    private static PhaseContext MakeContext(AircraftState aircraft, AircraftCategory category) =>
        new()
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = category,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };

    private static (AircraftState aircraft, InterceptCoursePhase phase) Setup(string type)
    {
        var aircraft = MakeAircraft(type, heading: 240, lat: 37.74, lon: -122.23);
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(RunwayHeading),
            },
        };
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(RunwayHeading),
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = "I28R",
            ForcedIntercept = false,
        };
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new HelicopterLandingPhase());
        return (aircraft, phase);
    }

    /// <summary>
    /// Helicopter, heading 240 on course 280 = 40° cut. 40° &gt; 30° but 40° ≤ 45°, so per
    /// TBL 5-9-1 it is a legal helicopter intercept and must CAPTURE, not bust through.
    /// </summary>
    [Fact]
    public void Helicopter_40DegIntercept_CapturesNotBustThrough()
    {
        var (aircraft, phase) = Setup("EC45");
        var ctx = MakeContext(aircraft, AircraftCategory.Helicopter);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);
        bool complete = phase.OnTick(ctx);

        Assert.True(complete);
        Assert.Empty(aircraft.PendingNotifications);
        Assert.NotNull(aircraft.Phases!.ActiveApproach);
    }

    /// <summary>
    /// Regression guard: the same 40° geometry for a JET must still bust through — the 45°
    /// allowance is helicopter-only, the 30° gate is unchanged for everyone else.
    /// </summary>
    [Fact]
    public void Jet_40DegIntercept_StillBustsThrough()
    {
        var (aircraft, phase) = Setup("B738");
        var ctx = MakeContext(aircraft, AircraftCategory.Jet);
        phase.Status = PhaseStatus.Active;
        phase.OnStart(ctx);

        phase.OnTick(ctx);
        aircraft.Position = new LatLon(aircraft.Position.Lat - 0.02, aircraft.Position.Lon - 0.03);
        bool complete = phase.OnTick(ctx);

        Assert.True(complete);
        Assert.Single(aircraft.PendingNotifications);
        Assert.Contains("localizer", aircraft.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(aircraft.Phases!.ActiveApproach);
    }
}
