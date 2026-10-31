using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>Flies OAK 28L closed traffic from the takeoff roll through the base turn and judges the flown track.</summary>
public class PatternCompressionFlownTests
{
    private const int HangGuardTicks = 3600;

    private sealed record Flight(List<LatLon> Track, PatternWaypoints Waypoints, double WidestDownwindNm, double SpeedAtCrosswindTurnKts);

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 0.25,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            GroundLayout = layout,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
        };

    private static Flight FlyClosedTraffic(string type)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? rwy = TestVnasData.NavigationDb!.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy);
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = type,
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        ac.Ground.Layout = layout;
        ac.Phases.Add(new TakeoffPhase());
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases,
            rwy,
            false,
            ActiveRunways.Empty
        );
        PatternWaypoints waypoints = ac.Phases.Phases.OfType<UpwindPhase>().Single().Waypoints!;
        ac.Phases.Start(Ctx(ac, rwy, layout));

        var recorder = new TickRecorder(ac);
        var track = new List<LatLon>();
        var threshold = new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon);
        double widestDownwindNm = 0;
        double speedAtCrosswindTurnKts = double.NaN;
        for (int t = 1; t <= HangGuardTicks; t++)
        {
            PhaseContext ctx = Ctx(ac, rwy, layout);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
            recorder.Record(t);

            if (!ac.IsOnGround)
            {
                track.Add(ac.Position);
            }

            if (double.IsNaN(speedAtCrosswindTurnKts) && (ac.Phases!.CurrentPhase is CrosswindPhase))
            {
                speedAtCrosswindTurnKts = ac.IndicatedAirspeed;
            }

            if (ac.Phases!.CurrentPhase is DownwindPhase)
            {
                widestDownwindNm = Math.Max(
                    widestDownwindNm,
                    Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, waypoints.FinalHeading))
                );
            }

            if (ac.Phases!.CurrentPhase is FinalApproachPhase)
            {
                return new Flight(track, waypoints, widestDownwindNm, speedAtCrosswindTurnKts);
            }
        }

        Assert.Fail($"hung: {type} never reached final, last phase {ac.Phases!.CurrentPhase?.Name}");
        throw new InvalidOperationException();
    }

    [Fact]
    public void Oak28LMlt_PC12_FlownTrackStaysClearOfRunway30()
    {
        Flight flight = FlyClosedTraffic("PC12");

        Assert.NotNull(flight.Waypoints.CompressedDownwindSpeedKts);
        PatternCompressionTests.AssertClearOfRunway30(flight.Track);
        Assert.InRange(flight.WidestDownwindNm, flight.Waypoints.PatternSizeNm - 0.1, flight.Waypoints.PatternSizeNm + 0.1);
        Assert.Equal(flight.Waypoints.CompressedDownwindSpeedKts.Value, flight.SpeedAtCrosswindTurnKts, 0);
    }

    [Fact]
    public void Oak28LMlt_B738_FlownTrackIsUnchanged()
    {
        Flight flight = FlyClosedTraffic("B738");

        Assert.Null(flight.Waypoints.CompressedDownwindSpeedKts);
        Assert.True(
            flight.WidestDownwindNm > flight.Waypoints.PatternSizeNm,
            $"a normal circuit still settles outside its width: {flight.WidestDownwindNm:F2} nm against {flight.Waypoints.PatternSizeNm:F2} nm"
        );
    }
}
