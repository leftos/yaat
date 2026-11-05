using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The descent planner, off a via, applies every in-force bound on the route, not only the first. Every in-force floor still
/// ahead (an at-or-above, a window's bottom, an at's value), met or not, is a lower limit: no ceiling takes the target below
/// it, so a floor the cleared altitude already meets still stops a later, lower ceiling. The target starts at the cleared
/// altitude; a floor above it raises and binds it, never above a nearer ceiling or at; a ceiling or an at below it lowers
/// it, never below an earlier floor. Ceilings before the binding, and bounds the cleared altitude already meets, only set
/// the required rate, the steepest one winning. An in-force approach floor below the target, not met by the cleared
/// altitude, lowers it. With no cleared altitude a ceiling, an at or an approach floor is the
/// altitude to descend to, and a controller's floor sets none. Under a descend via the step descent is unchanged. Real KOAK
/// navdata: the OAKES3 STAR's route fixes, stamped as controller crossings where a row needs them, and the H12-Z approach's
/// fixes past a deferred approach clearance's connecting fix.
/// </summary>
public class MultiBoundDescentScanTests
{
    private const string AircraftType = "B738";
    private const double SetBackNm = 10;
    private const string FirstFix = "OAKES";

    /// <summary>The set-back from LOHGN that puts the second ceiling (at WHYEE) past the standard rate and LOHGN's within it.</summary>
    private const double SteepestSetBackNm = 4.5;

    /// <summary>The set-back from OAKES that lets the aircraft level at a floor ahead before OAKES sequences.</summary>
    private const double LevelFirstSetBackNm = 40;

    private const int DescendViaTicks = 1500;

    /// <summary>
    /// What a descend via flies on the OAKES3 harness over <see cref="DescendViaTicks"/> seconds: each target change with
    /// the planned rate then, and the altitude crossed at each sequenced fix, recorded on the base tree (b72c91769), before
    /// the off-via descent scan.
    /// </summary>
    private static readonly string[] DescendViaBaseEvents =
    [
        "0 crossed OAKES at 15986",
        "0 target 7000 rate -814",
        "252 crossed GRUDG at 12658",
        "368 crossed BANND at 11279",
        "523 crossed TOOOL at 9499",
        "757 target null rate null",
        "758 crossed FFIST at 7000",
    ];

    private readonly ITestOutputHelper _output;

    public MultiBoundDescentScanTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A met ceiling at the first fix no longer hides the floor at the second.</summary>
    [Fact]
    public void Descent_MetCeilingThenFloor_HoldsAtFloor()
    {
        AircraftState ac = Arrival(14000, 6000, SetBackNm, FirstFix, AircraftType);
        string fix1 = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 12000, null);
        string fix2 = Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(9000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix1, 1800);
        double crossed = AltitudeWhereFixSequences(ac, fix2, 1800);
        _output.WriteLine($"crossed {fix2} at {crossed:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.True(crossed >= 9000 - FlightPhysics.AltitudeSnapFt, $"crossed {fix2} at {crossed:F0} ft, below its 9,000 floor");
        Assert.Equal(6000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// Row 1's route behind a leading bare fix, the aircraft level at the 9,000 floor before that fix sequences: the met
    /// ceiling that becomes the next fix does not send it down to its cleared altitude, and it crosses the floor's fix at it.
    /// </summary>
    [Fact]
    public void Descent_LevelBeforeMetCeiling_HoldsFloorPastIt()
    {
        AircraftState ac = Arrival(14000, 6000, LevelFirstSetBackNm, FirstFix, AircraftType);
        string fix0 = ac.Targets.NavigationRoute[0].Name;
        string fix1 = Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 12000, null);
        string fix2 = Stamp(ac, 2, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        double atFix0 = ClimbViaTopAltitudeTests.FlyPastFix(ac, fix0, 1800);
        _output.WriteLine($"past {fix0} at {atFix0:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.Equal(9000, atFix0, FlightPhysics.AltitudeSnapFt);
        Assert.True(ac.Targets.TargetAltitude is null or 9000, $"target {ac.Targets.TargetAltitude} past {fix0}");

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix1, 1800);
        _output.WriteLine($"past {fix1} at {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.True(ac.Targets.TargetAltitude is null or 9000, $"target {ac.Targets.TargetAltitude} past {fix1}");

        double crossed = AltitudeWhereFixSequences(ac, fix2, 1800);
        _output.WriteLine($"crossed {fix2} at {crossed:F0} ft");
        Assert.True(crossed >= 9000 - FlightPhysics.AltitudeSnapFt, $"crossed {fix2} at {crossed:F0} ft, below its 9,000 floor");
    }

    /// <summary>A floor the cleared altitude already meets keeps the cleared altitude.</summary>
    [Fact]
    public void Descent_FloorBelowCleared_KeepsCleared()
    {
        AircraftState ac = Arrival(14000, 8000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 5000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(8000, ac.Targets.TargetAltitude);
    }

    /// <summary>Every in-force floor ahead raises the target, not only the first.</summary>
    [Fact]
    public void Descent_TwoFloors_TakesTheHigher()
    {
        AircraftState ac = Arrival(14000, 5000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 7000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(9000, ac.Targets.TargetAltitude);
    }

    /// <summary>A floor at the cleared altitude neither binds nor hides a later, higher floor.</summary>
    [Fact]
    public void Descent_MetFloorThenHigherFloor_TakesTheHigher()
    {
        AircraftState ac = Arrival(14000, 8000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 8000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(9000, ac.Targets.TargetAltitude);
    }

    /// <summary>A floor the cleared altitude meets still holds a later, lower ceiling up until its fix sequences.</summary>
    [Fact]
    public void Descent_MetFloorThenLowerCeiling_HoldsTheFloor()
    {
        AircraftState ac = Arrival(14000, 6000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 5000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 4000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(5000, ac.Targets.TargetAltitude);
    }

    /// <summary>A controller's window below the cleared altitude is complied with: the target is its top.</summary>
    [Fact]
    public void Descent_WindowBelowCleared_TargetsItsTop()
    {
        AircraftState ac = Arrival(14000, 8000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.Between, 7000, 6000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(7000, ac.Targets.TargetAltitude);
    }

    /// <summary>An at above the cleared altitude pins the target: a later, lower ceiling waits for its fix.</summary>
    [Fact]
    public void Descent_AtPinsAgainstLaterCeiling()
    {
        AircraftState ac = Arrival(14000, 6000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.At, 9000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 7000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(9000, ac.Targets.TargetAltitude);
    }

    /// <summary>The nearer restriction is met first: a later floor never lifts the target above an earlier window's top.</summary>
    [Fact]
    public void Descent_EarlierCeilingBeatsLaterFloor()
    {
        AircraftState ac = Arrival(14000, 6000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.Between, 7000, 6000);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(7000, ac.Targets.TargetAltitude);
    }

    /// <summary>Two met ceilings before the binding floor: the steeper needed rate is the planned one.</summary>
    [Fact]
    public void Descent_TwoCeilingsBeforeFloor_SteepestRequiredWins()
    {
        // A BE20 (a low standard descent rate) set back before LOHGN on OAKES3's RW12 transition, LOHGN being 4 nm before
        // WHYEE: the far, lower ceiling at WHYEE needs more than the standard rate, the near one at LOHGN no more than it.
        AircraftState ac = Arrival(14000, 6000, SteepestSetBackNm, "LOHGN", "BE20");
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 13000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrBelow, 11000, null);
        Stamp(ac, 2, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);
        double rate1 = MetCeilingRate(ac, 0, 13000);
        double rate2 = MetCeilingRate(ac, 1, 11000);
        _output.WriteLine($"ceiling rates: {rate1:F0} fpm at fix 1, {rate2:F0} fpm at fix 2");
        Assert.True(rate2 > rate1 + 100, $"setup: fix 2's ceiling ({rate2:F0} fpm) must need more than fix 1's ({rate1:F0} fpm)");

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(9000, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.PlannedVerticalRate);
        Assert.Equal(-rate2, ac.Targets.PlannedVerticalRate.Value, 0);
    }

    /// <summary>A floor with no revert on its fix releases the target to the cleared altitude once the fix sequences.</summary>
    [Fact]
    public void Descent_FloorNoRevert_ReleasesToClearedPastFix()
    {
        AircraftState ac = Arrival(14000, 6000, SetBackNm, FirstFix, AircraftType);
        string fix = Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        Assert.Null(ac.Targets.NavigationRoute[0].RevertAltitude);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(9000, ac.Targets.TargetAltitude);

        ClimbViaTopAltitudeTests.FlyPastFix(ac, fix, 1800);
        _output.WriteLine($"past {fix} at {ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude}");
        Assert.Equal(6000, ac.Targets.TargetAltitude);
    }

    /// <summary>With no cleared altitude an in-force ceiling is the altitude to descend to.</summary>
    [Fact]
    public void Descent_CeilingNoAssigned_TargetsCeiling()
    {
        AircraftState ac = Arrival(14000, null, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrBelow, 10000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(10000, ac.Targets.TargetAltitude);
    }

    /// <summary>With no cleared altitude a controller's floor is not an altitude to descend to: the planner sets no target.</summary>
    [Fact]
    public void Descent_FloorNoAssigned_SetsNoTarget()
    {
        AircraftState ac = Arrival(14000, null, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// Fix sequencing on an at below the cleared altitude with a higher floor ahead takes the scan's target: the at's value,
    /// the nearer restriction, never the later floor.
    /// </summary>
    [Fact]
    public void FixSequencing_Descending_UsesScanClamp()
    {
        AircraftState sequenced = FixSequencingState();
        AircraftState scanned = FixSequencingState();
        PrintRoute(sequenced);

        FlightPhysics.ApplyFixConstraints(sequenced, sequenced.Targets.NavigationRoute[0]);
        FlightPhysics.Update(scanned, 1.0);
        _output.WriteLine($"fix sequencing target {sequenced.Targets.TargetAltitude}, scan target {scanned.Targets.TargetAltitude}");

        Assert.Equal(scanned.Targets.TargetAltitude, sequenced.Targets.TargetAltitude);
        Assert.Equal(7000, sequenced.Targets.TargetAltitude);
    }

    /// <summary>
    /// Established past a deferred approach's connecting fix, level at an approach floor with a lower assigned altitude: the
    /// floor holds the target at the present altitude, so the planner leaves the target alone.
    /// </summary>
    [Fact]
    public void Descent_NoBoundActs_LeavesTargetAlone()
    {
        AircraftState ac = ApproachRoute(2000, 1500, Floor(2000));
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// Past a deferred approach's connecting fix, an approach floor below the cleared altitude is descended to, at the rate
    /// that reaches it where its fix sequences.
    /// </summary>
    [Fact]
    public void Descent_ApproachFloorBelowCleared_DescendsToIt()
    {
        AircraftState ac = ApproachRoute(10000, 8000, Floor(4000));
        PrintRoute(ac);
        double rate = NeededRate(ac, 0, 4000);
        _output.WriteLine($"needed rate to 4,000: {rate:F0} fpm");

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(4000, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.PlannedVerticalRate);
        Assert.Equal(-rate, ac.Targets.PlannedVerticalRate.Value, 0);
    }

    /// <summary>An approach window the cleared altitude already meets keeps the cleared altitude: its bottom is not descended to.</summary>
    [Fact]
    public void Descent_ApproachWindowAroundCleared_KeepsCleared()
    {
        AircraftState ac = ApproachRoute(8000, 4000, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 5000, 3000));
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(4000, ac.Targets.TargetAltitude);
    }

    /// <summary>An aircraft inside a controller's window above its cleared altitude holds the window's bottom.</summary>
    [Fact]
    public void Descent_InsideWindowAboveCleared_HoldsWindowBottom()
    {
        AircraftState ac = Arrival(6000, 4000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.Between, 7000, 5000);
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(5000, ac.Targets.TargetAltitude);
    }

    /// <summary>An approach floor at the first fix holds a lower at at the next until the first sequences.</summary>
    [Fact]
    public void Descent_ApproachFloorThenLowerAt_HoldsTheFloor()
    {
        AircraftState ac = ApproachRoute(8000, 6000, Floor(4000), new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 2500));
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(4000, ac.Targets.TargetAltitude);
    }

    /// <summary>An approach floor at the first fix holds a lower glideslope intercept at the next.</summary>
    [Fact]
    public void Descent_ApproachFloorThenGlideslopeIntercept_HoldsTheFloor()
    {
        AircraftState ac = ApproachRoute(5000, 4000, Floor(3000), new CifpAltitudeRestriction(CifpAltitudeRestrictionType.GlideSlopeIntercept, 2000));
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(3000, ac.Targets.TargetAltitude);
    }

    /// <summary>With no cleared altitude an in-force approach floor is the altitude to descend to.</summary>
    [Fact]
    public void Descent_ApproachFloorNoAssigned_TargetsFloor()
    {
        AircraftState ac = ApproachRoute(6000, null, Floor(4000));
        PrintRoute(ac);

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(4000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// Under a descend via the step descent on OAKES3 flies what it flew before the off-via scan: the same targets at the
    /// same rates, and the same altitude at each fix.
    /// </summary>
    [Fact]
    public void DescendVia_StepDescent_Unchanged()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState ac = harness.Aircraft;
        CommandResult result = harness.Engine.SendCommand(ac.Callsign, "DVIA");
        _output.WriteLine($"DVIA -> {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.True(ac.Procedure.StarViaMode);

        List<string> events = [];
        double? lastTarget = null;
        for (int t = 0; t < DescendViaTicks; t++)
        {
            string? before = ac.Targets.NavigationRoute.FirstOrDefault()?.Name;
            harness.Engine.TickOneSecond();
            if ((before is not null) && (ac.Targets.NavigationRoute.FirstOrDefault()?.Name != before))
            {
                events.Add($"{t} crossed {before} at {ac.Altitude:F0}");
            }

            if ((t == 0) || (ac.Targets.TargetAltitude != lastTarget))
            {
                string rate = ac.Targets.PlannedVerticalRate is { } r ? r.ToString("F0") : "null";
                events.Add($"{t} target {ac.Targets.TargetAltitude?.ToString() ?? "null"} rate {rate}");
            }

            lastTarget = ac.Targets.TargetAltitude;
        }

        _output.WriteLine($"events: [{string.Join(", ", events.Select(e => $"\"{e}\""))}]");
        Assert.Equal(DescendViaBaseEvents, events);
    }

    /// <summary>Cleared to 8,000 at 14,000, an at 7,000 crossing at the next fix and an at-or-above 9,000 at the one after.</summary>
    private static AircraftState FixSequencingState()
    {
        AircraftState ac = Arrival(14000, 8000, SetBackNm, FirstFix, AircraftType);
        Stamp(ac, 0, CifpAltitudeRestrictionType.At, 7000, null);
        Stamp(ac, 1, CifpAltitudeRestrictionType.AtOrAbove, 9000, null);
        return ac;
    }

    /// <summary>
    /// An <paramref name="aircraftType"/> on the OAKES3 STAR's fixes (the common route, then the RW12 transition) from
    /// <paramref name="startFix"/>, bare, with no via, cleared to <paramref name="assigned"/> and with no target altitude,
    /// set back <paramref name="setBackNm"/> from that fix on the course from the next.
    /// </summary>
    private static AircraftState Arrival(double altitude, int? assigned, double setBackNm, string startFix, string aircraftType)
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        List<NavigationTarget> resolved = DepartureClearanceHandler.ResolveLegsToTargets([
            .. star.CommonLegs,
            .. star.RunwayTransitions["RW12"].Legs,
        ]);
        List<NavigationTarget> fixes = [.. resolved.DistinctBy(f => f.Name)];
        int start = fixes.FindIndex(f => f.Name == startFix);
        Assert.True(start >= 0, $"setup: OAKES3 RW12 must pass {startFix}");
        fixes = fixes[start..];
        Assert.True(fixes.Count >= 3, $"setup: OAKES3 RW12 must have at least three fixes from {startFix}");

        LatLon first = fixes[0].Position;
        LatLon position = GeoMath.ProjectPoint(first, new TrueHeading(GeoMath.BearingTo(fixes[1].Position, first)), setBackNm);
        var inbound = new TrueHeading(GeoMath.BearingTo(position, first));
        var ac = new AircraftState
        {
            Callsign = "SWA1867",
            AircraftType = aircraftType,
            Position = position,
            TrueHeading = inbound,
            TrueTrack = inbound,
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KLAS",
                Destination = "KOAK",
                Route = "OAKES3",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };

        foreach (NavigationTarget fix in fixes)
        {
            ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = fix.Position });
        }

        Assert.False(ac.Procedure.StarViaMode);
        Assert.False(ac.Procedure.SidViaMode);
        ac.Targets.AssignedAltitude = assigned;
        ac.Targets.TargetAltitude = null;
        return ac;
    }

    /// <summary>An at-or-above restriction at <paramref name="altitudeFt"/>.</summary>
    private static CifpAltitudeRestriction Floor(int altitudeFt) => new(CifpAltitudeRestrictionType.AtOrAbove, altitudeFt);

    /// <summary>
    /// Flies physics alone until <paramref name="fixName"/> leaves the route; returns the altitude at the start of the tick it
    /// sequenced on, before anything planned past the fix moves the aircraft.
    /// </summary>
    private static double AltitudeWhereFixSequences(AircraftState aircraft, string fixName, int maxSeconds)
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            double before = aircraft.Altitude;
            FlightPhysics.Update(aircraft, 1.0);
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name == fixName))
            {
                return before;
            }
        }

        Assert.Fail($"the aircraft did not pass {fixName} within {maxSeconds} s");
        return aircraft.Altitude;
    }

    /// <summary>
    /// A B738 established past the connecting fix (HIRMO) of a deferred "CAPP H12-Z" at KOAK, as in
    /// <c>AltitudeRestrictionSourceTests</c>' deferred-approach rows, its route the approach's fixes from its first altitude
    /// fix, one per restriction in <paramref name="restrictions"/>, each stamped with it as a published approach altitude
    /// (in force), and set back 10 nm from the first on the course from the second (or from the southeast for one fix).
    /// </summary>
    private static AircraftState ApproachRoute(double altitude, int? assigned, params CifpAltitudeRestriction[] restrictions)
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
        int first = route.FindIndex(t => t.AltitudeRestriction?.Source == CifpAltitudeRestrictionSource.Approach);
        Assert.True(first >= 0, "setup: H12-Z must put an approach altitude on the route");
        Assert.True(route.Count - first >= restrictions.Length, $"setup: H12-Z must have {restrictions.Length} fixes from {route[first].Name}");
        List<NavigationTarget> fixes = route.GetRange(first, restrictions.Length);
        route.Clear();
        for (int i = 0; i < fixes.Count; i++)
        {
            fixes[i].AltitudeRestriction = restrictions[i] with { Source = CifpAltitudeRestrictionSource.Approach };
            route.Add(fixes[i]);
            Assert.True(
                AltitudeRestrictionInForce.IsInForce(ac, fixes[i].AltitudeRestriction!),
                "setup: past HIRMO the approach altitudes must be in force"
            );
        }

        double backBearing = (fixes.Count > 1) ? GeoMath.BearingTo(fixes[1].Position, fixes[0].Position) : 130;
        ac.Position = GeoMath.ProjectPoint(fixes[0].Position, new TrueHeading(backBearing), 10);
        var inbound = new TrueHeading(GeoMath.BearingTo(ac.Position, fixes[0].Position));
        ac.TrueHeading = inbound;
        ac.TrueTrack = inbound;
        ac.Targets.AssignedAltitude = assigned;
        ac.Targets.TargetAltitude = null;
        return ac;
    }

    /// <summary>Stamps the route's fix at <paramref name="index"/> with a controller crossing; returns its name.</summary>
    private static string Stamp(AircraftState ac, int index, CifpAltitudeRestrictionType type, int altitudeFt, int? lowerFt)
    {
        NavigationTarget fix = ac.Targets.NavigationRoute[index];
        fix.AltitudeRestriction = new CifpAltitudeRestriction(type, altitudeFt, lowerFt) { Source = CifpAltitudeRestrictionSource.Atc };
        return fix.Name;
    }

    /// <summary>
    /// The rate magnitude the planner needs for a ceiling the cleared altitude meets at route index <paramref name="index"/>:
    /// the rate that reaches the ceiling where the fix sequences, between the standard descent rate and twice it.
    /// </summary>
    private static double MetCeilingRate(AircraftState ac, int index, int ceilingFt)
    {
        (double timeMinutes, double standardRate) = TimeToSequencing(ac, index);
        return timeMinutes > 0.1 ? Math.Clamp((ac.Altitude - ceilingFt) / timeMinutes, standardRate, standardRate * 2.0) : standardRate * 2.0;
    }

    /// <summary>
    /// The rate magnitude the planner needs to reach <paramref name="toFt"/> where the route's fix at <paramref name="index"/>
    /// sequences: at most twice the standard descent rate, with no minimum.
    /// </summary>
    private static double NeededRate(AircraftState ac, int index, int toFt)
    {
        (double timeMinutes, double standardRate) = TimeToSequencing(ac, index);
        return Math.Min((ac.Altitude - toFt) / Math.Max(timeMinutes, 1.0 / 60), standardRate * 2.0);
    }

    /// <summary>The minutes to where the route's fix at <paramref name="index"/> sequences, and the standard descent rate.</summary>
    private static (double TimeMinutes, double StandardRate) TimeToSequencing(AircraftState ac, int index)
    {
        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        double cumulativeNm = GeoMath.DistanceNm(ac.Position, route[0].Position);
        for (int i = 1; i <= index; i++)
        {
            cumulativeNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
        }

        double distanceNm = FlightPhysics.SequencingDistanceNm(ac, route, index, cumulativeNm);
        double standardRate = AircraftPerformance.DescentRate(ac.AircraftType, AircraftCategorization.Categorize(ac.AircraftType), ac.Altitude);
        return (distanceNm / (ac.GroundSpeed / 60.0), standardRate);
    }

    private void PrintRoute(AircraftState ac)
    {
        LatLon from = ac.Position;
        double cumulativeNm = 0;
        _output.WriteLine(
            $"{ac.Callsign} {ac.AircraftType} at {ac.Altitude:F0} ft, GS {ac.GroundSpeed:F0} kt, assigned {ac.Targets.AssignedAltitude}"
        );
        foreach (NavigationTarget fix in ac.Targets.NavigationRoute)
        {
            cumulativeNm += GeoMath.DistanceNm(from, fix.Position);
            from = fix.Position;
            string restriction = fix.AltitudeRestriction is { } r ? $"{r.Type} {r.Altitude1Ft}/{r.Altitude2Ft} ({r.Source})" : "-";
            _output.WriteLine($"  {fix.Name, -6} {cumulativeNm, 6:F1} nm  {restriction}");
        }
    }
}
