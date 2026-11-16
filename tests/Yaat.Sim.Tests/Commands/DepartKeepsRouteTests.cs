using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// "DEPART (fix) HEADING" keeps the route to a fix already on it: the aircraft flies the cleared route and turns at the
/// fix (7110.65 §5-6-2.a.1; AIM §4-4-10.a), instead of turning direct to the fix from wherever the command caught it. The
/// kept fix is rebuilt without the published FM course of a STAR's terminating leg, so the depart heading wins at the fix,
/// and the fixes after it are dropped. A fix that is not on the route still goes direct, as before.
/// </summary>
public sealed class DepartKeepsRouteTests
{
    /// <summary>
    /// The published outbound course a STAR's terminating leg carries — LUYTA is the BRIXX4 STAR's last fix. Unlike the
    /// 140° depart heading, so a rebuild that forgets to drop it turns the aircraft the wrong way at the fix.
    /// </summary>
    private const double StagedTerminalCourse = 100.0;

    public DepartKeepsRouteTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Depart_FixOnRoute_KeepsTheRouteAndRebuildsTheFix()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX", "LUYTA");
        NavigationTarget original = aircraft.Targets.NavigationRoute[^1];
        original.TerminalCourseMagnetic = StagedTerminalCourse;

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "CORKK", "BRIXX", "LUYTA" }, RouteNames(aircraft));
        NavigationTarget kept = aircraft.Targets.NavigationRoute[^1];
        Assert.NotSame(original, kept);
        Assert.Null(kept.TerminalCourseMagnetic);
        Assert.Equal(StagedTerminalCourse, original.TerminalCourseMagnetic);
        Assert.Contains("Depart LUYTA heading 140", result.Message);
        Assert.DoesNotContain("direct", result.Message);
    }

    [Fact]
    public void Depart_FixOnRoute_AtTheFixTheDepartHeadingWinsOverTheTerminalCourse()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX", "LUYTA");
        aircraft.Targets.NavigationRoute[^1].TerminalCourseMagnetic = StagedTerminalCourse;

        Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "CORKK", "BRIXX", "LUYTA" }, RouteNames(aircraft));
        Assert.Null(aircraft.Targets.NavigationRoute[^1].TerminalCourseMagnetic);

        FlyUntil(aircraft, ac => ac.Targets.NavigationRoute.Count == 0, "the aircraft flies the kept route through LUYTA and the fix sequences");

        Assert.Equal(140, aircraft.Targets.AssignedMagneticHeading?.Degrees);

        TrueHeading departTrue = new MagneticHeading(140).ToTrue(aircraft.Declination);
        Assert.True(aircraft.Targets.TargetTrueHeading.HasValue, "the depart block sets the heading when the fix sequences");
        Assert.True(
            GeoMath.AbsBearingDifference(aircraft.Targets.TargetTrueHeading.Value.Degrees, departTrue.Degrees) <= 0.25,
            $"the target heading is the 140° magnetic depart heading, not the STAR's {StagedTerminalCourse:000}° course"
        );

        FlyUntil(
            aircraft,
            ac => GeoMath.AbsBearingDifference(ac.TrueHeading.Degrees, departTrue.Degrees) <= 2,
            "the aircraft turns to the 140° depart heading"
        );
    }

    [Fact]
    public void Depart_FixOnRoute_DropsTheFixesAfterIt()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX", "LUYTA", "SUNOL", "PMD");

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "CORKK", "BRIXX", "LUYTA" }, RouteNames(aircraft));
        Assert.Contains("Depart LUYTA heading 140", result.Message);
    }

    [Fact]
    public void Depart_FixIsTheActiveLeg_KeepsItAndReadsBackAsDepart()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "LUYTA");

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "LUYTA" }, RouteNames(aircraft));
        Assert.Contains("Depart LUYTA heading 140", result.Message);
        Assert.DoesNotContain("direct", result.Message);
    }

    [Fact]
    public void Depart_FixNotOnRoute_GoesDirect()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX");

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "LUYTA" }, RouteNames(aircraft));
        Assert.Contains("Proceed direct LUYTA, depart heading 140", result.Message);
    }

    [Fact]
    public void Depart_EmptyRoute_GoesDirect()
    {
        AircraftState aircraft = AircraftShortOfCorkk();

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "LUYTA" }, RouteNames(aircraft));
        Assert.Contains("Proceed direct LUYTA, depart heading 140", result.Message);
    }

    [Fact]
    public void Depart_FixTwiceInTheRoute_KeepsTheFirstOccurrence()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "LUYTA", "BRIXX", "LUYTA");

        CommandResult result = Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "CORKK", "LUYTA" }, RouteNames(aircraft));
        Assert.Contains("Depart LUYTA heading 140", result.Message);
        Assert.DoesNotContain("direct", result.Message);
    }

    [Fact]
    public void Depart_LowerCaseCommand_KeepsTheRoute()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX", "LUYTA");
        aircraft.Targets.NavigationRoute[^1].TerminalCourseMagnetic = StagedTerminalCourse;

        CommandResult result = Send(aircraft, "depart luyta 140");

        Assert.Equal(new[] { "CORKK", "BRIXX", "LUYTA" }, RouteNames(aircraft));
        NavigationTarget kept = aircraft.Targets.NavigationRoute[^1];
        Assert.Null(kept.TerminalCourseMagnetic);
        Assert.Contains("Depart LUYTA heading 140", result.Message);
        Assert.DoesNotContain("direct", result.Message);
    }

    [Fact]
    public void Depart_FixOnRoute_KeepsTheMatchedFixesPosition()
    {
        // Two fixes can share an ident; the route's own copy is the one the aircraft was flying to, so the rebuilt fix
        // takes its position from that target, not from the database the command's fix was resolved against.
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "LUYTA");
        LatLon brixxPosition = FixPosition("BRIXX");
        aircraft.Targets.NavigationRoute[^1] = new NavigationTarget { Name = "LUYTA", Position = brixxPosition };

        Send(aircraft, "DEPART LUYTA 140");

        NavigationTarget kept = aircraft.Targets.NavigationRoute[^1];
        Assert.Equal("LUYTA", kept.Name);
        Assert.Equal(brixxPosition.Lat, kept.Position.Lat, 9);
        Assert.Equal(brixxPosition.Lon, kept.Position.Lon, 9);
    }

    [Fact]
    public void Depart_FixOnRoute_KeepsEveryFixsCrossingRestriction()
    {
        AircraftState aircraft = AircraftShortOfCorkk();
        SetRoute(aircraft, "CORKK", "BRIXX", "LUYTA");
        NavigationTarget brixx = aircraft.Targets.NavigationRoute[1];
        brixx.AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 6000);
        brixx.SpeedRestriction = new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow);
        aircraft.Targets.NavigationRoute[^1].AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 5000);
        aircraft.Targets.NavigationRoute[^1].SpeedRestriction = new CifpSpeedRestriction(180, CifpSpeedRestrictionType.AtOrBelow);

        Send(aircraft, "DEPART LUYTA 140");

        Assert.Equal(new[] { "CORKK", "BRIXX", "LUYTA" }, RouteNames(aircraft));
        NavigationTarget keptBrixx = aircraft.Targets.NavigationRoute[1];
        Assert.Same(brixx, keptBrixx);
        Assert.Equal(6000, keptBrixx.AltitudeRestriction?.Altitude1Ft);
        Assert.Equal(210, keptBrixx.SpeedRestriction?.SpeedKts);
        NavigationTarget keptLuyta = aircraft.Targets.NavigationRoute[^1];
        Assert.Equal(5000, keptLuyta.AltitudeRestriction?.Altitude1Ft);
        Assert.Equal(180, keptLuyta.SpeedRestriction?.SpeedKts);
    }

    /// <summary>
    /// An aircraft two miles short of CORKK on the BRIXX4 arrival, flying its course: the route of the reported case
    /// (CHBLI transition fixes), far enough from every fix for sequencing to mean something.
    /// </summary>
    private static AircraftState AircraftShortOfCorkk()
    {
        LatLon corkk = FixPosition("CORKK");
        LatLon brixx = FixPosition("BRIXX");
        var course = new TrueHeading(GeoMath.BearingTo(corkk, brixx));
        LatLon position = GeoMath.ProjectPoint(corkk, new TrueHeading((course.Degrees + 180) % 360), 2);

        var aircraft = new AircraftState
        {
            Callsign = "SWA2089",
            AircraftType = "B738",
            Position = position,
            TrueHeading = course,
            TrueTrack = course,
            Altitude = 12000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KSFO",
                Destination = "KSJC",
            },
        };
        aircraft.Targets.TargetTrueHeading = course;
        aircraft.Targets.TargetAltitude = 12000;
        return aircraft;
    }

    private static void SetRoute(AircraftState aircraft, params string[] fixes)
    {
        aircraft.Targets.NavigationRoute.Clear();
        foreach (string fix in fixes)
        {
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix, Position = FixPosition(fix) });
        }
    }

    private static string[] RouteNames(AircraftState aircraft) => [.. aircraft.Targets.NavigationRoute.Select(target => target.Name)];

    private static LatLon FixPosition(string fixName)
    {
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fixName));
        return new LatLon(lat, lon);
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, $"'{text}': {result.Message}");
        return result;
    }

    /// <summary>Flies <paramref name="aircraft"/> a second at a time until <paramref name="done"/> holds, failing otherwise.</summary>
    private static void FlyUntil(AircraftState aircraft, Func<AircraftState, bool> done, string premise)
    {
        for (int second = 0; (second < 3_600) && !done(aircraft); second++)
        {
            for (int tick = 0; tick < 4; tick++)
            {
                FlightPhysics.Update(aircraft, 0.25);
            }
        }

        Assert.True(done(aircraft), premise);
    }
}
