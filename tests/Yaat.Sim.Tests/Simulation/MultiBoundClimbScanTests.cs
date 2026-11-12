using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The climb planner applies every in-force bound on the route, not only the first: the target is the cleared or top
/// altitude, lowered by every in-force ceiling still ahead (an at, an at-or-below, a window's top), and the first ceiling
/// below it binds until its fix sequences. A floor before the binding ceiling, and a bound the cleared altitude already
/// meets, only set the required rate, the steepest one winning, and never end the scan; with no cleared or top altitude, or
/// for a controller's floor above the cleared altitude, the floor becomes the target. A ceiling never raises the target
/// above the cleared or top altitude. Real KOAK navdata: the CNDEL5 SUSEY departure past LEJAY, its route fixes stamped as
/// controller crossings where a row needs them.
/// </summary>
public class MultiBoundClimbScanTests
{
    private const string AircraftType = "B738";
    private const double SetBackNm = 10;

    private readonly ITestOutputHelper _output;

    public MultiBoundClimbScanTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SidFallback_CeilingBelowProxyTop_LevelsAtCeiling()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out CifpAltitudeRestriction published);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrBelow, published.Type);
        Assert.Equal(10000, published.Altitude1Ft);
        int top = PublishedClimbViaTop(ac);
        Assert.True(top >= 10000, $"setup: the published climb via top {top} must be at or above PORTE's 10,000");
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void SidFallback_BetweenFromBelow_ClimbsToUpper()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        int top = PublishedClimbViaTop(ac);
        Assert.True(top >= 9000, $"setup: the published climb via top {top} must be at or above the window's 9,000");
        Fix(ac, "PORTE").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 9000, 7000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(9000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void SidFallback_At_SetsExact()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        int top = PublishedClimbViaTop(ac);
        Assert.True(top >= 8000, $"setup: the published climb via top {top} must be at or above 8,000");
        Fix(ac, "PORTE").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(8000, ac.Targets.TargetAltitude);
    }

    /// <summary>A met floor at the first fix no longer hides the ceiling at the second.</summary>
    [Fact]
    public void NonVia_MetFloorThenCeiling_CapsAtLaterCeiling()
    {
        AircraftState ac = NonViaDeparture(altitude: 11000, assigned: 14000);
        string fix1 = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 12000);
        string fix2 = Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 13000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(13000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix1, 1800);
        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix2, 1800);
        _output.WriteLine($"past {fix2} at {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.Equal(14000, ac.Targets.TargetAltitude);
    }

    /// <summary>A ceiling with no revert on its fix releases the target to the cleared altitude once the fix sequences.</summary>
    [Fact]
    public void NonVia_CeilingNoRevert_ReleasesToClearedPastFix()
    {
        AircraftState ac = NonViaDeparture(altitude: 9000, assigned: 14000);
        string fix = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 10000);
        Assert.Null(ac.Targets.NavigationRoute[0].RevertAltitude);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(10000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix, 1800);
        _output.WriteLine($"past {fix} at {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.Equal(14000, ac.Targets.TargetAltitude);
    }

    /// <summary>Every in-force ceiling ahead lowers the target, not only the first.</summary>
    [Fact]
    public void NonVia_TwoCeilings_TakesTheLower()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: 14000);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 12000);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 10000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
    }

    /// <summary>A ceiling above the cleared altitude neither binds nor ends the scan.</summary>
    [Fact]
    public void NonVia_CeilingAboveC_DoesNotHideLaterCeiling()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: 9000);
        Stamp(ac, 0, CifpAltitudeRestrictionType.Between, 12000, 11000);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 8000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(8000, ac.Targets.TargetAltitude);
    }

    /// <summary>With no cleared altitude a window's lower bound is the floor to climb to.</summary>
    [Fact]
    public void NonVia_WindowNoAssigned_TargetsLowerBound()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: null);
        Stamp(ac, 0, CifpAltitudeRestrictionType.Between, 12000, 10000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// Established past a deferred approach's connecting fix, level at an approach floor with a higher assigned altitude
    /// and nothing lower ahead: no bound calls for a climb, so the planner leaves the target alone.
    /// </summary>
    [Fact]
    public void ApproachFloorAtAltitude_AssignedAbove_NotSentBackUp()
    {
        AircraftState ac = ApproachFloorState(altitude: 2000, assigned: 5000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.NotEqual(5000, ac.Targets.TargetAltitude);

        double highest = ac.Altitude;
        for (int t = 0; t < 60; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            highest = Math.Max(highest, ac.Altitude);
        }

        _output.WriteLine($"highest over 60 s {highest:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.True(highest <= 2000 + FlightPhysics.AltitudeSnapFt, $"climbed to {highest:F0} ft off the approach floor");
    }

    /// <summary>With no assigned altitude, an in-force approach floor above the aircraft is the altitude to climb to.</summary>
    [Fact]
    public void ApproachFloorNoAssigned_TargetsFloor()
    {
        AircraftState ac = ApproachFloorState(altitude: 1500, assigned: null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(2000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// A CNDEL5 departure flying its SID under a controller altitude at 8,000, then "CFIX PORTE 060": when PORTE becomes the
    /// next fix the crossing below the aircraft takes the descending path and targets 6,000.
    /// </summary>
    [Fact]
    public void SidDeparture_CfixBelowAltitude_DescendsAtTheFix()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        ac.Altitude = 8000;
        ac.Targets.AssignedAltitude = 8000;
        ac.Targets.TargetAltitude = 8000;
        Assert.True(ac.Procedure.SidViaMode);
        string next = ac.Targets.NavigationRoute[0].Name;
        Assert.NotEqual("PORTE", next);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        Dispatch(ac, "CFIX PORTE 060", ctx);
        ac.Targets.TargetAltitude = 8000;
        PrintRoute(ac);
        ClimbViaTopAltitudeTests.FlyPastFix(ac, next, 600);
        _output.WriteLine($"past {next} at {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}, assigned {ac.Targets.AssignedAltitude}");

        Assert.Equal("PORTE", ac.Targets.NavigationRoute[0].Name);
        Assert.Equal(6000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void NonVia_CeilingNoAssigned_SetsNoTarget()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: null);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 10000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// A controller's at-or-above floor above the cleared altitude is complied with: the aircraft climbs to cross the fix at
    /// the floor. An at-or-above is not an altitude to maintain, so past its fix, with no revert stored, the aircraft resumes
    /// its assigned altitude (AIM 4-4-10.e).
    /// </summary>
    [Fact]
    public void NonVia_AtcFloorAboveAssigned_ClimbsToFloorThenResumesAssigned()
    {
        AircraftState ac = NonViaDeparture(altitude: 3000, assigned: 5000);
        string fix = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 7000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(7000, ac.Targets.TargetAltitude);

        double crossed = ClimbViaTopAltitudeTests.FlyPastFix(ac, fix, 1800);
        _output.WriteLine($"crossed {fix} at {crossed:F0} ft, target {ac.Targets.TargetAltitude}");
        // About 7,000 at the fix: the level-off taper leaves the last few tens of feet to the tick that sequences it.
        Assert.InRange(crossed, 6900, 7000 + FlightPhysics.AltitudeSnapFt);
        Assert.Equal(5000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPhysics(ac, 180);
        _output.WriteLine($"180 s past {fix}: {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}");
        // The level-off clears the target, which then holds the present altitude.
        Assert.True(ac.Targets.TargetAltitude is null or 5000, $"target {ac.Targets.TargetAltitude} past {fix}");
        Assert.Equal(5000, ac.Altitude, FlightPhysics.AltitudeSnapFt);
    }

    [Fact]
    public void Via_ExceptCrossAboveTop_StopsAtTop()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        ac.Procedure.ClimbViaTargetFt = 10000;
        Fix(ac, "PORTE").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 12000)
        {
            Source = CifpAltitudeRestrictionSource.Atc,
        };
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void NonVia_CmBelowCrossingAt_CapsAtCm()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        Dispatch(ac, "CFIX PORTE 120", ctx);
        Dispatch(ac, "CM 090", ctx);
        PrintRoute(ac);

        Assert.Equal(9000, ac.Targets.AssignedAltitude);
        for (int t = 0; t < 180; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            Assert.True(ac.Targets.TargetAltitude is null or <= 9000, $"At {t} s the target is {ac.Targets.TargetAltitude}, above the CM 090");
        }
    }

    [Fact]
    public void Climb_TwoFloorsBeforeCeiling_SteepestRequiredWins()
    {
        // A SID departure released with no controller altitude, its published floors unmet, so each needs the plain rate
        // (no standard-rate minimum, unlike a floor the cleared altitude meets): the far, higher floor at PORTE needs more
        // than the near, low one at CNDEL, and only the steepest-wins rule picks it.
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        SetBack(ac, SetBackNm);
        Assert.True(PublishedClimbViaTop(ac) >= 10000, "setup: the published climb via top must be at or above the 10,000 ceiling");
        Fix(ac, "CNDEL").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 4000);
        Fix(ac, "PORTE").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 9000);
        Fix(ac, "SUSEY").AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 10000);
        PrintRoute(ac);
        double rate1 = UnmetFloorRate(ac, 0, 4000);
        double rate2 = UnmetFloorRate(ac, 1, 9000);
        _output.WriteLine($"floor rates: {rate1:F0} fpm at CNDEL, {rate2:F0} fpm at PORTE");
        Assert.True(rate2 > rate1 + 100, $"setup: PORTE's floor ({rate2:F0} fpm) must need more than CNDEL's ({rate1:F0} fpm)");

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.PlannedVerticalRate);
        Assert.Equal(rate2, ac.Targets.PlannedVerticalRate.Value, 0);
    }

    [Fact]
    public void Climb_FloorBeyondBindingCeiling_PlannedAfterIt()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: 14000);
        string fix1 = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 10000);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 13000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(10000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix1, 1800);
        double expectedRate = MetFloorRate(ac, 0, 13000);
        _output.WriteLine($"past {fix1} at {ac.Altitude:F0} ft; floor rate {expectedRate:F0} fpm");
        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(14000, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.PlannedVerticalRate);
        Assert.Equal(expectedRate, ac.Targets.PlannedVerticalRate.Value, 0);
    }

    [Fact]
    public void FixSequencing_UsesScanClamp()
    {
        AircraftState sequenced = FixSequencingState();
        AircraftState scanned = FixSequencingState();
        PrintRoute(sequenced);

        FlightPhysics.ApplyFixConstraints(sequenced, sequenced.Targets.NavigationRoute[0]);
        FlightPhysics.Update(scanned, 1.0);
        _output.WriteLine($"fix sequencing target {sequenced.Targets.TargetAltitude}, scan target {scanned.Targets.TargetAltitude}");

        Assert.Equal(scanned.Targets.TargetAltitude, sequenced.Targets.TargetAltitude);
        Assert.Equal(8000, sequenced.Targets.TargetAltitude);
    }

    /// <summary>Cleared to 9,000 at 5,000, an at 12,000 crossing at the next fix and an at-or-below 8,000 at the one after.</summary>
    private static AircraftState FixSequencingState()
    {
        AircraftState ac = NonViaDeparture(altitude: 5000, assigned: 9000);
        Stamp(ac, 0, CifpAltitudeRestrictionType.At, 12000);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 8000);
        return ac;
    }

    /// <summary>
    /// The CNDEL5 SUSEY departure with bare route fixes and no via, cleared to <paramref name="assigned"/>, set back
    /// <see cref="SetBackNm"/> from LEJAY on its course to CNDEL so that CNDEL (2 nm past LEJAY, a fly-by turn) does not
    /// sequence on the first tick (<paramref name="setBackNm"/> overrides the distance).
    /// </summary>
    private static AircraftState NonViaDeparture(double altitude, int? assigned, double setBackNm = SetBackNm)
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        Assert.False(ac.Procedure.SidViaMode);
        SetBack(ac, setBackNm);
        ac.Altitude = altitude;
        ac.Targets.AssignedAltitude = assigned;
        ac.Targets.TargetAltitude = assigned;
        return ac;
    }

    /// <summary>Moves the aircraft <paramref name="setBackNm"/> back from LEJAY on its course to the route's first fix.</summary>
    private static void SetBack(AircraftState ac, double setBackNm)
    {
        LatLon first = ac.Targets.NavigationRoute[0].Position;
        ac.Position = GeoMath.ProjectPoint(ac.Position, new TrueHeading(GeoMath.BearingTo(first, ac.Position)), setBackNm);
    }

    /// <summary>
    /// A B738 established past the connecting fix (HIRMO) of a deferred "CAPP H12-Z" at KOAK, 10 nm from the approach's
    /// first altitude fix, the only fix left on the route, which carries an approach floor at or above 2,000 in force.
    /// </summary>
    private static AircraftState ApproachFloorState(double altitude, int? assigned)
    {
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        (double Lat, double Lon) emzoh = Assert.IsType<(double, double)>(navDb.GetFixPosition("EMZOH"));
        (double Lat, double Lon) hirmo = Assert.IsType<(double, double)>(navDb.GetFixPosition("HIRMO"));
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = AircraftType,
            TrueHeading = new TrueHeading(320),
            Altitude = altitude,
            IndicatedAirspeed = 210,
            Position = new LatLon(37.5, -121.8),
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", Route = "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK" },
            Procedure = new AircraftProcedure { DestinationRunway = "12" },
        };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzoh.Lat, emzoh.Lon) });
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmo.Lat, hirmo.Lon) });
        ac.Approach.Expected = "H12-Z";
        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Approach.PendingClearance);

        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        NavigationTarget fix = route.First(t => t.AltitudeRestriction?.Source == CifpAltitudeRestrictionSource.Approach);
        route.RemoveAll(t => !ReferenceEquals(t, fix));
        fix.AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 2000)
        {
            Source = CifpAltitudeRestrictionSource.Approach,
        };
        Assert.True(AltitudeRestrictionInForce.IsInForce(ac, fix.AltitudeRestriction), "setup: past HIRMO the approach floor must be in force");

        ac.Position = GeoMath.ProjectPoint(fix.Position, new TrueHeading(130), 10);
        var inbound = new TrueHeading(GeoMath.BearingTo(ac.Position, fix.Position));
        ac.TrueHeading = inbound;
        ac.TrueTrack = inbound;
        ac.Targets.AssignedAltitude = assigned;
        ac.Targets.TargetAltitude = null;
        return ac;
    }

    /// <summary>Stamps the route's fix at <paramref name="index"/> with a controller crossing; returns its name.</summary>
    private static string Stamp(AircraftState ac, int index, CifpAltitudeRestrictionType type, int altitudeFt, int? lowerFt = null)
    {
        NavigationTarget fix = ac.Targets.NavigationRoute[index];
        fix.AltitudeRestriction = new CifpAltitudeRestriction(type, altitudeFt, lowerFt) { Source = CifpAltitudeRestrictionSource.Atc };
        return fix.Name;
    }

    private static NavigationTarget Fix(AircraftState ac, string name) => ac.Targets.NavigationRoute.First(f => f.Name == name);

    private static int PublishedClimbViaTop(AircraftState ac)
    {
        int? top = NavigationCommandHandler.PublishedClimbViaTopFt(ac);
        Assert.NotNull(top);
        return top.Value;
    }

    /// <summary>
    /// The rate the planner needs for a floor the cleared altitude meets at route index <paramref name="index"/>: the rate
    /// that reaches the floor where the fix sequences, between the standard climb rate and twice it.
    /// </summary>
    private static double MetFloorRate(AircraftState ac, int index, int floorFt)
    {
        (double timeMinutes, double standardRate) = TimeToSequencing(ac, index);
        return timeMinutes > 0.1 ? Math.Clamp((floorFt - ac.Altitude) / timeMinutes, standardRate, standardRate * 2.0) : standardRate * 2.0;
    }

    /// <summary>
    /// The rate the planner needs for a floor no cleared altitude meets at route index <paramref name="index"/>: the rate that
    /// reaches it where the fix sequences, at most twice the standard climb rate and with no minimum.
    /// </summary>
    private static double UnmetFloorRate(AircraftState ac, int index, int floorFt)
    {
        (double timeMinutes, double standardRate) = TimeToSequencing(ac, index);
        return Math.Min((floorFt - ac.Altitude) / Math.Max(timeMinutes, 1.0 / 60), standardRate * 2.0);
    }

    /// <summary>The minutes to where the route's fix at <paramref name="index"/> sequences, and the standard climb rate.</summary>
    private static (double TimeMinutes, double StandardRate) TimeToSequencing(AircraftState ac, int index)
    {
        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        double cumulativeNm = GeoMath.DistanceNm(ac.Position, route[0].Position);
        for (int i = 1; i <= index; i++)
        {
            cumulativeNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
        }

        double distanceNm = FlightPhysics.SequencingDistanceNm(ac, route, index, cumulativeNm);
        double standardRate = AircraftPerformance.ClimbRate(AircraftType, AircraftCategorization.Categorize(AircraftType), ac.Altitude);
        return (distanceNm / (ac.GroundSpeed / 60.0), standardRate);
    }

    private void PrintRoute(AircraftState ac)
    {
        LatLon from = ac.Position;
        double cumulativeNm = 0;
        _output.WriteLine($"{ac.Callsign} at {ac.Altitude:F0} ft, assigned {ac.Targets.AssignedAltitude}, climb via {ac.Procedure.ClimbViaTargetFt}");
        foreach (NavigationTarget fix in ac.Targets.NavigationRoute)
        {
            cumulativeNm += GeoMath.DistanceNm(from, fix.Position);
            from = fix.Position;
            string restriction = fix.AltitudeRestriction is { } r ? $"{r.Type} {r.Altitude1Ft}/{r.Altitude2Ft} ({r.Source})" : "-";
            _output.WriteLine($"  {fix.Name, -6} {cumulativeNm, 6:F1} nm  {restriction}");
        }
    }

    private void Dispatch(AircraftState ac, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), ac, ctx);
        _output.WriteLine($"{text} -> {result.Message}");
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
