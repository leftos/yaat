using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
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

    private static PatternWaypoints Oak28LMlt(string type, ActiveRunways activeRunways, PatternCommanded commanded, double? commandSizeNm)
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        GroundRunway authored = Authored28L();
        AircraftCategory category = AircraftCategorization.Categorize(type);
        (double? sizeOv, double? altOv) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, category, commandSizeNm, null);
        return PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, category, type, 0, PatternDirection.Left)
            {
                SizeOverrideNm = sizeOv,
                AltitudeOverrideFt = altOv,
                AirportRunways = OakRunways,
                AuthoredRunway = authored,
                ActiveRunways = activeRunways,
                Commanded = commanded,
            }
        );
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
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None, null);

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
        PatternWaypoints wp = Oak28LMlt("C208", ActiveRunways.Empty, PatternCommanded.None, null);

        Assert.Null(wp.RunwayCrossing);
        Assert.True(wp.PatternSizeNm <= 0.75, $"size {wp.PatternSizeNm:F2}");
        AssertClearOfRunway30(PlannedTrack(wp));
    }

    [Fact]
    public void Oak28LMlt_C182_NeverCompressesWiderThanItsNormalSize()
    {
        PatternWaypoints normal = Oak28LMlt("C182", ActiveRunways.Empty, PatternCommanded.None with { NeverCompresses = true }, null);

        PatternWaypoints wp = Oak28LMlt("C182", ActiveRunways.Empty, PatternCommanded.None, null);

        Assert.True(
            wp.PatternSizeNm <= normal.PatternSizeNm + 1e-9,
            $"compressed to {wp.PatternSizeNm:F4} nm, wider than its normal {normal.PatternSizeNm:F4} nm"
        );
    }

    /// <summary>
    /// The ERB gate judges a base call on the circuit being flown: the same aircraft, position and altitude can be a feasible
    /// base in the normal circuit and not in the compressed one, whose tighter turn rolls out closer in and lower on the
    /// glidepath. Swept over altitude, since where the verdicts part depends on the type's speeds.
    /// </summary>
    [Fact]
    public void BaseDescentGate_JudgesTheCompressedCircuitAtItsOwnTurnAndSpeed()
    {
        PatternWaypoints compressed = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None, null);
        PatternWaypoints normal = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None with { NeverCompresses = true }, null);
        Assert.NotNull(compressed.CompressedDownwindSpeedKts);
        Assert.Null(normal.CompressedDownwindSpeedKts);
        RunwayInfo rwy = Runway("KOAK", "28L");
        AircraftState ac = OnRunway28L("PC12");

        var parted = new List<double>();
        for (double aglFt = 200; aglFt <= 1500; aglFt += 10)
        {
            ac.Altitude = rwy.ElevationFt + aglFt;
            BaseDescentFeasibility inNormal = PatternCommandHandler.EvaluateBaseDescent(ac, rwy, AircraftCategory.Turboprop, 0.5, 1.0, normal);
            BaseDescentFeasibility inCompressed = PatternCommandHandler.EvaluateBaseDescent(
                ac,
                rwy,
                AircraftCategory.Turboprop,
                0.5,
                1.0,
                compressed
            );
            if (inNormal != inCompressed)
            {
                parted.Add(aglFt);
            }
        }

        Assert.NotEmpty(parted);
    }

    /// <summary>Clears <paramref name="ac"/> for left closed traffic on OAK 28L and ticks it twice, rebuilding its circuit between.</summary>
    private static void ClearedForClosedTrafficAndTicked(AircraftState ac)
    {
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
        PatternBuilder.UpdateWaypoints(ac.Phases, Oak28LMlt(ac.AircraftType, ActiveRunways.Empty, PatternCommanded.None, null));
        PhaseRunner.Tick(ac, ctx);
    }

    [Fact]
    public void Oak28LMlt_B738_KeepsItsNormalPatternAndWarnsOnce()
    {
        PatternWaypoints wp = Oak28LMlt("B738", ActiveRunways.Empty, PatternCommanded.None, null);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm("B738", AircraftCategory.Jet, 0), wp.PatternSizeNm, 2);
        Assert.Equal("30", wp.RunwayCrossing?.CrossedRunway);

        AircraftState ac = OnRunway28L("B738");
        ClearedForClosedTrafficAndTicked(ac);

        string warning = Assert.Single(ac.PendingWarnings);
        Assert.Equal("N123 B738: pattern runway 28L crosses runway 30, tightest flyable 1.6 NM", warning);
    }

    [Fact]
    public void Oak28LMlt_PC12_CompressedCircuitClearsTheCorridorAndDoesNotWarn()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None, null);

        Assert.NotNull(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);

        AircraftState ac = OnRunway28L("PC12");
        ClearedForClosedTrafficAndTicked(ac);

        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Oak28LMlt_DH8A_KeepsItsNormalPattern()
    {
        PatternWaypoints wp = Oak28LMlt("DH8A", ActiveRunways.Empty, PatternCommanded.None, null);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal("30", wp.RunwayCrossing?.CrossedRunway);
        Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm("DH8A", AircraftCategory.Turboprop, 0), wp.PatternSizeNm, 2);
    }

    /// <summary>
    /// A right circuit on 28L for <paramref name="type"/>, with 28R, 0.17 nm north of 28L and shorter, the only other runway;
    /// asserts the circuit encloses 28R's pavement.
    /// </summary>
    private static PatternWaypoints RightCircuitAround28R(string type)
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        RunwayInfo inside = Runway("KOAK", "28R");

        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategorization.Categorize(type), type, 0, PatternDirection.Right)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = [rwy, inside],
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        double departureEndAlongNm = GeoMath.AlongTrackDistanceNm(new LatLon(rwy.EndLatitude, rwy.EndLongitude), threshold, rwy.TrueHeading);
        foreach (
            LatLon end in new[]
            {
                new LatLon(inside.ThresholdLatitude, inside.ThresholdLongitude),
                new LatLon(inside.EndLatitude, inside.EndLongitude),
            }
        )
        {
            double alongNm = GeoMath.AlongTrackDistanceNm(end, threshold, rwy.TrueHeading);
            double rightNm = GeoMath.SignedCrossTrackDistanceNm(end, threshold, rwy.TrueHeading);
            Assert.InRange(alongNm, -0.1, departureEndAlongNm);
            Assert.InRange(rightNm, 0.05, wp.PatternSizeNm);
        }

        return wp;
    }

    /// <summary>
    /// A C172's right circuit on 28L encloses 28R and crosses its extended centerline, but 28R, 1,000 ft north, is by design: no
    /// right circuit of any width from 0.3 to 2.5 nm clears it, so the pilot flies the normal pattern without a warning.
    /// </summary>
    [Fact]
    public void Oak28L_C172CircuitAround28R_IsByDesignAndDoesNotWarn()
    {
        PatternWaypoints wp = RightCircuitAround28R("C172");

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    /// <summary>
    /// A PC12's right circuit on 28L encloses 28R too, but its crosswind and base legs lie beyond 28R's corridor, 1 nm past each
    /// end, so no leg cuts across it.
    /// </summary>
    [Fact]
    public void Oak28L_PC12CircuitEnclosing28R_ClearOfItsCorridor_StaysSilentAbout28R()
    {
        PatternWaypoints wp = RightCircuitAround28R("PC12");

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    private static PatternWaypoints Oak28R(string type, PatternDirection direction, params string[] active) =>
        OakCircuit("28R", type, direction, ActiveRunways.Empty.With("OAK", [.. active.Select(a => new ActiveRunway(a, ActiveRunwayUse.Both))]));

    private static PatternWaypoints OakCircuit(string runway, string type, PatternDirection direction, ActiveRunways activeRunways) =>
        PatternGeometry.Compute(
            new PatternGeometryRequest(Runway("KOAK", runway), AircraftCategorization.Categorize(type), type, 0, direction)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = OakRunways,
                AuthoredRunway = null,
                ActiveRunways = activeRunways,
                Commanded = PatternCommanded.None,
            }
        );

    /// <summary>
    /// 28R clears a left circuit on 33 only from about 0.92 nm wide, inside the by-design range, so it stays protected and a
    /// C172, whose compressed floor is far narrower, still warns of it.
    /// </summary>
    [Fact]
    public void Oak33Mlt_C172_Runway28RActive_StillWarnsOf28R()
    {
        PatternWaypoints wp = OakCircuit(
            "33",
            "C172",
            PatternDirection.Left,
            ActiveRunways.Empty.With("OAK", [new ActiveRunway("33", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)])
        );

        Assert.Equal("28R", wp.RunwayCrossing?.CrossedRunway);
    }

    /// <summary>With no active list every runway counts, but 28L is by design for a left circuit on 28R, so a PC12 compresses for 30.</summary>
    [Fact]
    public void Oak28RMlt_DefaultRoom_TurbopropCompressesForRunway30()
    {
        PatternWaypoints wp = OakCircuit("28R", "PC12", PatternDirection.Left, ActiveRunways.Empty);

        Assert.NotNull(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
        Assert.Equal(0.814, wp.PatternSizeNm, 3);
    }

    /// <summary>Runway 33's corridor crosses 28R's own pavement; no right circuit of any width from 0.3 to 2.5 nm clears it, so it is by design.</summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("C208")]
    public void Oak28RMrt_Runway33Active_IsByDesign_FliesNormalAndDoesNotWarn(string type)
    {
        PatternWaypoints wp = Oak28R(type, PatternDirection.Right, "28R", "33");

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
        Assert.Equal(CategoryPerformance.PatternSizeNm(AircraftCategorization.Categorize(type)), wp.PatternSizeNm, 3);
    }

    /// <summary>28L, 1,000 ft south of 28R, lies inside every left circuit on 28R that a compression could fly, so it is by design.</summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("PC12")]
    public void Oak28RMlt_Runway28LActive_IsByDesign_FliesNormalAndDoesNotWarn(string type)
    {
        PatternWaypoints wp = Oak28R(type, PatternDirection.Left, "28R", "28L");

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
        Assert.Equal(CategoryPerformance.PatternSizeNm(AircraftCategorization.Categorize(type)), wp.PatternSizeNm, 3);
    }

    /// <summary>28L, 1,000 ft south of 28R, is by design for a left circuit on 28R; runway 30 still compresses it.</summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("PC12")]
    [InlineData("SF50")]
    public void Oak28RMlt_28LAnd30Active_CompressesForRunway30Only(string type)
    {
        PatternWaypoints only30 = Oak28R(type, PatternDirection.Left, "28R", "30");

        PatternWaypoints wp = Oak28R(type, PatternDirection.Left, "28R", "28L", "30");

        Assert.NotNull(only30.CompressedDownwindSpeedKts);
        Assert.NotNull(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
        Assert.Equal(only30.PatternSizeNm, wp.PatternSizeNm, 6);
    }

    /// <summary>A B738's compressed floor is wider than any left circuit on 28R that clears runway 30, so it still warns of 30.</summary>
    [Fact]
    public void Oak28RMlt_B738_28LAnd30Active_StillWarnsOfRunway30()
    {
        PatternWaypoints wp = Oak28R("B738", PatternDirection.Left, "28R", "28L", "30");

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal("30", wp.RunwayCrossing?.CrossedRunway);
    }

    /// <summary>A PC12's left circuit on 28R compresses clear of runway 30, as before the by-design rule.</summary>
    [Fact]
    public void Oak28RMlt_Runway30Active_CompressesAsToday()
    {
        PatternWaypoints wp = Oak28R("PC12", PatternDirection.Left, "28R", "30");

        Assert.NotNull(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
        Assert.Equal(0.814, wp.PatternSizeNm, 3);
    }

    [Fact]
    public void NoCrossingRunway_PatternIsUnchanged()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");

        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Turboprop, "PC12", 0, PatternDirection.Right)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = [rwy, Runway("KOAK", "30")],
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void InactiveRunway_DoesNotCompress()
    {
        ActiveRunways only28L = ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]);

        PatternWaypoints wp = Oak28LMlt("PC12", only28L, PatternCommanded.None, null);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void NoActiveList_CompressesAsAListNamingRunway12Does()
    {
        ActiveRunways with12 = ActiveRunways.Empty.With(
            "OAK",
            [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("12", ActiveRunwayUse.Both)]
        );

        PatternWaypoints noList = Oak28LMlt("PC12", ActiveRunways.Empty, PatternCommanded.None, null);
        PatternWaypoints named = Oak28LMlt("PC12", with12, PatternCommanded.None, null);

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
            new PatternGeometryRequest(rwy, AircraftCategory.Turboprop, "PC12", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = [rwy, crossing],
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Null(wp.RunwayCrossing);
    }

    [Fact]
    public void ControllerSpeedAboveTheCompressedSpeed_CancelsCompression()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(false, 140) { NeverCompresses = false }, null);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.NotNull(wp.RunwayCrossing);
    }

    [Fact]
    public void Psize_PC12AtItsFullFloor_IsAcceptedAtTheCompressedSpeeds()
    {
        double floorNm = PatternCompression.Legs("PC12", AircraftCategory.Turboprop, 0).FloorNm;
        Assert.Equal(0.60, floorNm, 2);
        AircraftState ac = OnRunway28L("PC12");

        CommandResult result = PatternCommandHandler.TrySetPatternSize(ac, floorNm, ActiveRunways.Empty, false, false, null);
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(true, null) { NeverCompresses = false }, floorNm);

        Assert.DoesNotContain("turn-radius minimum", result.Message!);
        Assert.Equal(floorNm, wp.PatternSizeNm, 6);
        Assert.Equal(98, wp.CompressedDownwindSpeedKts!.Value, 0);
        Assert.Equal(91, wp.CompressedBaseSpeedKts!.Value, 0);
    }

    [Fact]
    public void Psize_PC12At05_SaysUnableAtTheCompressedFloor()
    {
        AircraftState ac = OnRunway28L("PC12");

        CommandResult result = PatternCommandHandler.TrySetPatternSize(ac, 0.5, ActiveRunways.Empty, false, false, null);

        double floorNm = PatternCompression.Legs("PC12", AircraftCategory.Turboprop, 0).FloorNm;
        Assert.Contains($"{floorNm:F1} NM turn-radius minimum", result.Message!);
        Assert.Contains(ac.PendingWarnings, w => w.Contains($"flying {floorNm:F1} NM", StringComparison.Ordinal));
    }

    [Fact]
    public void Psize_CrossesTheRunway_IsFlownAsTold()
    {
        PatternWaypoints wp = Oak28LMlt("PC12", ActiveRunways.Empty, new PatternCommanded(true, null) { NeverCompresses = false }, 1.5);

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

    /// <summary>In solo training with the student on tower, the pilot says the crossing over the solo pilot-speech path.</summary>
    [Fact]
    public void SoloTraining_BE58AboveItsCompressedSpeed_PilotSpeaksTheCrossing()
    {
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            Runway("KOAK", "28L"),
            false,
            ActiveRunways.Empty
        );
        PhaseContext ctx = Context(ac, true, "TWR");
        ac.Phases!.Start(ctx);

        PhaseRunner.Tick(ac, ctx);

        PilotTransmission spoken = Assert.Single(ac.PendingPilotTransmissions, t => t.SpeechText.Contains("across runway", StringComparison.Ordinal));
        Assert.EndsWith(", be advised our downwind will take us across runway three zero.", spoken.SpeechText);
        Assert.Equal(PilotTransmissionKind.Proactive, spoken.Kind);
        Assert.Equal(0, CrossingWarnings(ac));
    }

    /// <summary>A BE58 held at 150 kt, above its compressed downwind speed, flies its normal circuit, whose downwind lies on runway 30.</summary>
    private static AircraftState HeldAboveItsCompressedSpeed(AircraftState ac)
    {
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetSpeed = 150;
        return ac;
    }

    private static int CrossingWarnings(AircraftState ac) => ac.PendingWarnings.Count(w => w.Contains("crosses runway 30", StringComparison.Ordinal));

    private static TouchAndGoPhase AirborneTouchAndGo(RunwayInfo rwy) =>
        TouchAndGoPhase.FromSnapshot(
            new TouchAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 0,
                FieldElevation = rwy.ElevationFt,
                RunwayHeadingDeg = rwy.TrueHeading.Degrees,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                RolloutDuration = 0,
                RolloutElapsed = 0,
                Reaccelerating = true,
                Airborne = true,
            }
        );

    [Fact]
    public void Oak28LMlt_BE58AboveItsCompressedSpeed_WarnsOnFlownPavementContact()
    {
        PatternWaypoints wp = Oak28LMlt("BE58", ActiveRunways.Empty, new PatternCommanded(false, 150) { NeverCompresses = false }, null);

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal("30", wp.RunwayCrossing?.CrossedRunway);
    }

    [Fact]
    public void MltOnTheGroundThenCtoMlt_WarnsOnce()
    {
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        RunwayInfo rwy = Runway("KOAK", "28L");
        ac.IsOnGround = true;
        ac.Altitude = rwy.ElevationFt;
        ac.IndicatedAirspeed = 0;
        ac.Phases!.Add(new TakeoffPhase());
        PhaseContext ctx = Context(ac, false);
        ac.Phases.Start(ctx);

        Assert.True(PatternCommandHandler.TryChangePatternDirection(ac, PatternDirection.Left, null, null, ActiveRunways.Empty, null).Success);
        PhaseRunner.Tick(ac, ctx);
        // The takeoff roll drops the speed target; the controller's speed still stands at the takeoff clearance.
        HeldAboveItsCompressedSpeed(ac);
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases,
            rwy,
            false,
            ActiveRunways.Empty
        );
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(1, CrossingWarnings(ac));
    }

    [Fact]
    public void RunwayCrossing_PendingAndWarned_SurviveASnapshotRoundTrip()
    {
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            Runway("KOAK", "28L"),
            false,
            ActiveRunways.Empty
        );
        Assert.Equal("30", ac.Pattern.PendingRunwayCrossing?.CrossedRunway);

        var restored = AircraftPattern.FromSnapshot(ac.Pattern.ToSnapshot());

        Assert.Equal(ac.Pattern.PendingRunwayCrossing, restored.PendingRunwayCrossing);

        PhaseContext ctx = Context(ac, false);
        ac.Phases!.Start(ctx);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(1, CrossingWarnings(ac));
        List<string>? warned = ac.Pattern.ToSnapshot().WarnedRunwayCrossings;
        Assert.NotNull(warned);

        var restoredWarned = AircraftPattern.FromSnapshot(ac.Pattern.ToSnapshot());

        Assert.Equal(warned, restoredWarned.ToSnapshot().WarnedRunwayCrossings);
        restoredWarned.NoteRunwayCrossing(ac.Phases.Phases);
        Assert.Null(restoredWarned.PendingRunwayCrossing);
    }

    /// <summary>
    /// A crossing noted for a closed-traffic circuit is not voiced for a circuit that has been replaced: a phase list cleared
    /// before the tick that would voice it (here by a touch-and-go with no circuit queued) drops it, and the next circuit on
    /// the same runway and side owes it again.
    /// </summary>
    [Fact]
    public void PendingCrossingWarning_DroppedWhenThePhaseListClearsBeforeItIsVoiced()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            rwy,
            false,
            ActiveRunways.Empty
        );
        Assert.NotNull(ac.Pattern.PendingRunwayCrossing);
        PhaseContext cleared = Context(ac, false);
        ac.Phases!.Start(cleared);

        ac.Phases.Clear(cleared);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(AirborneTouchAndGo(rwy));
        PhaseRunner.Tick(ac, Context(ac, false));

        Assert.Equal(0, CrossingWarnings(ac));
    }

    /// <summary>
    /// A crossing warning dropped with its circuit is still owed: the left circuit on 28L the touch-and-go builds next, the
    /// aircraft rejoining the same runway and side, says it once.
    /// </summary>
    [Fact]
    public void DroppedCrossingWarning_IsVoicedWhenTheAircraftRejoinsThePatternOnTheSameSide()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            rwy,
            false,
            ActiveRunways.Empty
        );
        PhaseContext cleared = Context(ac, false);
        ac.Phases!.Start(cleared);
        ac.Phases.Clear(cleared);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(AirborneTouchAndGo(rwy));
        PhaseContext ctx = Context(ac, false);

        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(0, CrossingWarnings(ac));
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(1, CrossingWarnings(ac));
    }

    [Fact]
    public void TouchAndGoCircuits_WarnWhenFirstBuiltAndNeverOnALap()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");
        AircraftState ac = HeldAboveItsCompressedSpeed(OnRunway28L("BE58"));
        ac.Altitude = rwy.ElevationFt + 1000;
        ac.Pattern.TrafficDirection = PatternDirection.Left;

        for (int lap = 0; lap < 2; lap++)
        {
            ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
            ac.Phases.Add(AirborneTouchAndGo(rwy));
            PhaseContext ctx = Context(ac, false);
            PhaseRunner.Tick(ac, ctx);
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.Equal(1, CrossingWarnings(ac));
    }

    [Fact]
    public void CtoMrd_DoesNotCompress()
    {
        AircraftState ac = OnRunway28L("PC12");

        DepartureClearanceHandler.ApplyPatternExitDeparture(
            new PatternExitDeparture(PatternEntryLeg.Downwind, PatternDirection.Right),
            ac,
            ac.Phases!,
            Runway("KOAK", "28L"),
            null,
            false,
            ActiveRunways.Empty
        );

        Assert.All(
            ac.Phases!.Phases.Select(AirborneFollowHelper.PatternWaypointsOf).OfType<PatternWaypoints>(),
            wp => Assert.Null(wp.CompressedDownwindSpeedKts)
        );
    }

    [Fact]
    public void StandingPsize_WithNoSizeOverride_DoesNotCompress()
    {
        RunwayInfo rwy = Runway("KOAK", "28L");

        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Jet, "B738", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = OakRunways,
                AuthoredRunway = Authored28L(),
                ActiveRunways = ActiveRunways.Empty,
                Commanded = new PatternCommanded(true, null) { NeverCompresses = false },
            }
        );

        Assert.Null(wp.CompressedDownwindSpeedKts);
        Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm("B738", AircraftCategory.Jet, 0), wp.PatternSizeNm, 2);
    }

    [Fact]
    public void Psize_OnAnActiveLeg_FliesTheCompressedSpeedAndTurnRate()
    {
        AircraftState ac = OnRunway28L("PC12");
        Assert.True(PatternCommandHandler.TrySetPatternSize(ac, 1.5, ActiveRunways.Empty, false, false, null).Success);
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases!,
            Runway("KOAK", "28L"),
            false,
            ActiveRunways.Empty
        );
        ac.Phases!.Start(Context(ac, false));
        Assert.Null(AirborneFollowHelper.PatternWaypointsOf(ac.Phases.CurrentPhase)!.CompressedDownwindSpeedKts);

        double floorNm = PatternCompression.Legs("PC12", AircraftCategory.Turboprop, 0).FloorNm;
        Assert.True(PatternCommandHandler.TrySetPatternSize(ac, floorNm, ActiveRunways.Empty, false, false, null).Success);

        PatternWaypoints wp = AirborneFollowHelper.PatternWaypointsOf(ac.Phases.CurrentPhase)!;
        Assert.NotNull(wp.CompressedDownwindSpeedKts);
        Assert.Equal(wp.CompressedDownwindSpeedKts, ac.Targets.TargetSpeed);
        Assert.Equal(wp.CompressedTurnRateDegPerSec, ac.Targets.TurnRateOverride);
    }

    [Fact]
    public void ParallelTransition_FliesTheCompressionOfThePatternItJoins()
    {
        RunwayInfo flown = Runway("KOAK", "28R");
        RunwayInfo pattern = Runway("KOAK", "28L");

        List<Phase> circuit = PatternBuilder.BuildRunwayTransitionCircuit(
            flown,
            pattern,
            AircraftCategory.Turboprop,
            "PC12",
            0,
            PatternDirection.Left,
            true,
            null,
            null,
            OakRunways,
            null,
            Authored28L(),
            ActiveRunways.Empty,
            PatternCommanded.None
        );

        PatternWaypoints joined = circuit.OfType<DownwindPhase>().Single().Waypoints!;
        Assert.NotNull(joined.CompressedDownwindSpeedKts);
        foreach (PatternWaypoints wp in circuit.Select(AirborneFollowHelper.PatternWaypointsOf).OfType<PatternWaypoints>())
        {
            Assert.Equal(joined.CompressedDownwindSpeedKts, wp.CompressedDownwindSpeedKts);
            Assert.Equal(joined.CompressedTurnRateDegPerSec, wp.CompressedTurnRateDegPerSec);
            Assert.Equal(joined.PatternSizeNm, wp.PatternSizeNm, 6);
        }
    }

    [Fact]
    public void CrosswindEntry_IntoACompressedCircuit_FliesTheCompressedSpeed()
    {
        AircraftState ac = OnRunway28L("PC12");
        (double Lat, double Lon) start = GeoMath.ProjectPoint(ac.Position.Lat, ac.Position.Lon, ac.TrueHeading.ToReciprocal(), 3.0);
        ac.Position = new LatLon(start.Lat, start.Lon);

        CommandResult result = PatternCommandHandler.TryEnterPattern(
            ac,
            PatternDirection.Left,
            PatternEntryLeg.Crosswind,
            null,
            null,
            ActiveRunways.Empty,
            true,
            new TestAirportGroundData().GetLayout("OAK")
        );
        Assert.True(result.Success, result.Message);
        ac.Phases!.Start(Context(ac, false));

        PatternWaypoints circuit = ac.Phases.Phases.Select(AirborneFollowHelper.PatternWaypointsOf).OfType<PatternWaypoints>().First();
        Assert.NotNull(circuit.CompressedDownwindSpeedKts);
        Assert.Equal(circuit.CompressedDownwindSpeedKts, ac.Targets.TargetSpeed);
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

    private static PhaseContext Context(AircraftState ac, bool solo) => Context(ac, solo, null);

    private static PhaseContext Context(AircraftState ac, bool solo, string? studentPositionType) =>
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
            StudentPositionType = studentPositionType,
        };
}
