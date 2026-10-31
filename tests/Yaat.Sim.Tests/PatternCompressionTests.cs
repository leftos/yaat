using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A traffic pattern compressed automatically so it stays clear of another active runway, against the real OAK layout
/// (28L and runway 30). The figures come from the aviation rulings on YAAT-1566.
/// </summary>
public class PatternCompressionTests
{
    public PatternCompressionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo Runway(string airport, string designator)
    {
        RunwayInfo? runway = TestVnasData.NavigationDb!.GetRunway(airport, designator);
        Assert.NotNull(runway);
        return runway;
    }

    private static IReadOnlyList<RunwayInfo> OakRunways => TestVnasData.NavigationDb!.GetRunways("KOAK");

    private static GroundRunway Authored28L()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        GroundRunway? authored = layout.FindRunway("28L");
        Assert.NotNull(authored);
        return authored;
    }

    private static PatternWaypoints Oak28LMlt(string type, ActiveRunways activeRunways, PatternCommanded commanded, double? commandSizeNm = null)
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        GroundRunway authored = Authored28L();
        AircraftCategory category = AircraftCategorization.Categorize(type);
        (double? sizeOv, double? altOv) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, category, commandSizeNm, null);
        return PatternGeometry.Compute(rwy, category, type, 0, PatternDirection.Left, sizeOv, altOv, OakRunways, authored, activeRunways, commanded);
    }

    /// <summary>The planned crosswind, downwind and base from the rulings' turn point, departure end + 0.5 nm.</summary>
    private static List<LatLon> PlannedTrack(PatternWaypoints wp)
    {
        (double Lat, double Lon) turn = GeoMath.ProjectPoint(wp.DepartureEndLat, wp.DepartureEndLon, wp.UpwindHeading, 0.5);
        (double Lat, double Lon) start = GeoMath.ProjectPoint(turn.Lat, turn.Lon, wp.CrosswindHeading, wp.PatternSizeNm);
        var track = new List<LatLon>();
        Sample(track, new LatLon(turn.Lat, turn.Lon), new LatLon(start.Lat, start.Lon));
        Sample(track, new LatLon(start.Lat, start.Lon), new LatLon(wp.BaseTurnLat, wp.BaseTurnLon));
        return track;
    }

    private static void Sample(List<LatLon> track, LatLon from, LatLon to)
    {
        const int Steps = 400;
        for (int i = 0; i <= Steps; i++)
        {
            double f = i / (double)Steps;
            track.Add(new LatLon(from.Lat + ((to.Lat - from.Lat) * f), from.Lon + ((to.Lon - from.Lon) * f)));
        }
    }

    internal static void AssertClearOfRunway30(IEnumerable<LatLon> track)
    {
        RunwayInfo r30 = Runway("KOAK", "30");
        var origin = new LatLon(r30.ThresholdLatitude, r30.ThresholdLongitude);
        double lengthNm = GeoMath.DistanceNm(r30.ThresholdLatitude, r30.ThresholdLongitude, r30.EndLatitude, r30.EndLongitude);
        double halfWidthNm = r30.WidthFt / 2.0 / GeoMath.FeetPerNm;

        int side = 0;
        foreach (LatLon point in track)
        {
            double along = GeoMath.AlongTrackDistanceNm(point, origin, r30.TrueHeading);
            if ((along < -1.0) || (along > lengthNm + 1.0))
            {
                continue;
            }

            double cross = GeoMath.SignedCrossTrackDistanceNm(point, origin, r30.TrueHeading);
            if ((along >= 0) && (along <= lengthNm))
            {
                Assert.True(Math.Abs(cross) > halfWidthNm, $"on runway 30's pavement at {point}: {cross:F3} nm off its centerline");
            }

            int pointSide = Math.Sign(cross);
            side = side == 0 ? pointSide : side;
            Assert.True(pointSide == side, $"crossed runway 30's extended centerline at {point}");
        }

        Assert.NotEqual(0, side);
    }

    [Theory]
    [InlineData("DHC6", 85, 80, 0.43, 0.23)]
    [InlineData("C208", 91, 85, 0.48, 0.26)]
    [InlineData("PC12", 98, 91, 0.56, 0.30)]
    [InlineData("DH8A", 112, 102, 0.72, 0.40)]
    [InlineData("B738", 161, 152, 1.54, 0.81)]
    public void CompressedFloor_MatchesTheRulingsTable(string type, double downwindKt, double baseKt, double arrivalNm, double crosswindNm)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);

        CompressedLegs legs = PatternCompression.Legs(type, category, 0);

        Assert.Equal(downwindKt, legs.DownwindKts, 0);
        Assert.Equal(baseKt, legs.BaseKts, 0);
        double actualArrivalNm =
            PatternCompression.TurnRadiusNm(legs.DownwindKts, category) + PatternCompression.TurnRadiusNm(legs.BaseKts, category);
        double actualCrosswindNm = PatternCompression.TurnRadiusNm(legs.DownwindKts, category);
        Assert.InRange(actualArrivalNm, arrivalNm - 0.01, arrivalNm + 0.01);
        Assert.InRange(actualCrosswindNm, crosswindNm - 0.01, crosswindNm + 0.01);
        Assert.Equal(Math.Max(actualArrivalNm, 2 * actualCrosswindNm), legs.FloorNm, 6);
    }

    [Fact]
    public void Oak28LMlt_PC12_CompressesClearOfRunway30()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None);

        Assert.Equal(98, wp.CompressedDownwindSpeedKts!.Value, 0);
        Assert.Equal(91, wp.CompressedBaseSpeedKts!.Value, 0);
        Assert.NotNull(wp.CompressedTurnRateDegPerSec);
        Assert.Null(wp.RunwayCrossing);
        Assert.InRange(wp.PatternSizeNm, 0.60, 0.65);
        AssertClearOfRunway30(PlannedTrack(wp));
    }

    [Fact]
    public void Oak28LMlt_C208_CompressesClearOfRunway30()
    {
        PatternWaypoints wp = Oak28LMlt("C208", ActiveRunways.Empty, PatternCommanded.None);

        Assert.Null(wp.RunwayCrossing);
        Assert.True(wp.PatternSizeNm <= 0.75, $"size {wp.PatternSizeNm:F2}");
        AssertClearOfRunway30(PlannedTrack(wp));
    }

    [Fact]
    public void Oak28LMlt_B738_KeepsItsNormalPatternAndWarnsOnce()
    {
        PatternWaypoints wp = Oak28LMlt("B738", ActiveRunways.Empty, PatternCommanded.None);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm("B738", AircraftCategory.Jet, 0), wp.PatternSizeNm, 2);
        Assert.Equal("30", wp.RunwayCrossing!.CrossedRunway);
        Assert.Equal(1.6, wp.RunwayCrossing.TightestFlyableNm, 1);

        AircraftState ac = OnRunway28L("B738");
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            Runway("KOAK", "28L"),
            false,
            ActiveRunways.Empty
        );
        PhaseContext ctx = Context(ac, false);
        ac.Phases!.Start(ctx);

        PhaseRunner.Tick(ac, ctx);
        PatternBuilder.UpdateWaypoints(ac.Phases, Oak28LMlt("B738", ActiveRunways.Empty, PatternCommanded.None));
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(["N123 B738: pattern runway 28L crosses runway 30, tightest flyable 1.6 NM"], ac.PendingWarnings);
    }

    [Fact]
    public void Oak28LMlt_DH8A_KeepsItsNormalPattern()
    {
        PatternWaypoints wp = Oak28LMlt("DH8A", ActiveRunways.Empty, PatternCommanded.None);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.NotNull(wp.RunwayCrossing);
        Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm("DH8A", AircraftCategory.Turboprop, 0), wp.PatternSizeNm, 2);
    }

    [Fact]
    public void NoCrossingRunway_PatternIsUnchanged()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");

        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Turboprop,
            "PC12",
            0,
            PatternDirection.Right,
            null,
            null,
            [rwy, Runway("KOAK", "30")],
            null,
            ActiveRunways.Empty,
            PatternCommanded.None
        );

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void InactiveRunway_DoesNotCompress()
    {
        ActiveRunways only28L = ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]);

        PatternWaypoints wp = Oak28LMlt("PC12", only28L, PatternCommanded.None);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void NoActiveList_CountsEveryRunway()
    {
        ActiveRunways with30 = ActiveRunways.Empty.With(
            "OAK",
            [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("12", ActiveRunwayUse.Both)]
        );

        PatternWaypoints noList = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None);
        PatternWaypoints named = Oak28LMlt("PC12", with30, PatternCommanded.None);

        Assert.NotNull(noList.CompressedDownwindSpeedKts);
        Assert.Equal(noList.PatternSizeNm, named.PatternSizeNm, 6);
    }

    [Fact]
    public void IntersectingRunway_IsNotDeconflicted()
    {
        RunwayInfo rwy = Runway("KSFO", "28L");
        RunwayInfo crossing = Runway("KSFO", "01R");
        Assert.True(PatternGeometry.RunwaysCross(rwy, crossing));

        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Turboprop,
            "PC12",
            0,
            PatternDirection.Left,
            null,
            null,
            [rwy, crossing],
            null,
            ActiveRunways.Empty,
            PatternCommanded.None
        );

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void ControllerSpeedAboveTheCompressedSpeed_CancelsCompression()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(false, 140));

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.NotNull(wp.RunwayCrossing);
    }

    [Fact]
    public void Psize_PC12AtItsFullFloor_IsAcceptedAtTheCompressedSpeeds()
    {
        double floorNm = PatternCompression.Legs("PC12", AircraftCategory.Turboprop, 0).FloorNm;
        Assert.Equal(0.60, floorNm, 2);
        AircraftState ac = OnRunway28L("PC12");

        CommandResult result = PatternCommandHandler.TrySetPatternSize(ac, floorNm, false, false, null);
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(true, null), floorNm);

        Assert.DoesNotContain("turn-radius minimum", result.Message!);
        Assert.Equal(floorNm, wp.PatternSizeNm, 6);
        Assert.Equal(98, wp.CompressedDownwindSpeedKts!.Value, 0);
        Assert.Equal(91, wp.CompressedBaseSpeedKts!.Value, 0);
    }

    [Fact]
    public void Psize_PC12At05_SaysUnableAtTheCompressedFloor()
    {
        AircraftState ac = OnRunway28L("PC12");

        CommandResult result = PatternCommandHandler.TrySetPatternSize(ac, 0.5, false, false, null);

        double floorNm = PatternCompression.Legs("PC12", AircraftCategory.Turboprop, 0).FloorNm;
        Assert.Contains($"{floorNm:F1} NM turn-radius minimum", result.Message!);
        Assert.Contains(ac.PendingWarnings, w => w.Contains($"flying {floorNm:F1} NM", StringComparison.Ordinal));
    }

    [Fact]
    public void Psize_CrossesTheRunway_IsFlownAsTold()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(true, null), 1.5);

        Assert.Equal(1.5, wp.PatternSizeNm, 6);
        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void SoloPilot_SaysTheCrossing()
    {
        AircraftState ac = OnRunway28L("B738");
        PilotSpeechText text = PilotResponder.BuildPatternCrossesRunway(ac, new PatternRunwayCrossing("28L", "30", 1.6));

        Assert.EndsWith(", be advised our downwind will take us across runway three zero.", text.Tts);
    }

    private static AircraftState OnRunway28L(string type)
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = type,
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 500,
            IndicatedAirspeed = 130,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
    }

    private static PhaseContext Context(AircraftState ac, bool solo) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = ac.Phases!.AssignedRunway,
            FieldElevation = ac.Phases.AssignedRunway!.ElevationFt,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
            SoloTrainingMode = solo,
        };
}
