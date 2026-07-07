using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ShownRouteBuilder"/>. Uses hand-rolled CIFP procedures via
/// <see cref="NavigationDatabase.ForTesting"/> so the assertions are deterministic and
/// don't depend on the bundled FAA CIFP being present in the test data directory.
/// </summary>
public class ShownRouteBuilderTests
{
    private const string Airport = "KOAK";
    private const string RunwayDesignator = "30";

    [Fact]
    public void BuildPrimary_StarEndingInVm_ReturnsTailFromAnchorFixOnPublishedHeading()
    {
        // STAR: ENROU → WNDSR (anchor fix) → VM "fly heading 280° for vectors"
        var stars = new[]
        {
            new CifpStarProcedure(
                Airport,
                "WNDSR2",
                CommonLegs:
                [
                    new CifpLeg("ENROU", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                    new CifpLeg("WNDSR", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
                    new CifpLeg("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 30, OutboundCourse: 280.0, null, null),
                ],
                EnrouteTransitions: new Dictionary<string, CifpTransition>(),
                RunwayTransitions: new Dictionary<string, CifpTransition>()
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENROU"] = (38.0, -122.5),
            ["WNDSR"] = (37.85, -122.30),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, null, stars);

        var ac = new AircraftModel
        {
            Callsign = "UAL123",
            Destination = Airport,
            ActiveStarId = "WNDSR2",
            DestinationRunway = RunwayDesignator,
            NavRouteFixes = [new NavRouteFixDto("ENROU", 38.0, -122.5, null), new NavRouteFixDto("WNDSR", 37.85, -122.30, null)],
        };

        var (waypoints, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.Collection(waypoints, wp => Assert.Equal("ENROU", wp.ResolvedName), wp => Assert.Equal("WNDSR", wp.ResolvedName));
        Assert.NotNull(tail);
        Assert.Equal(37.85, tail!.FromLat, 6);
        Assert.Equal(-122.30, tail.FromLon, 6);
        Assert.Equal(280.0, tail.HeadingMag, 3);
        Assert.Equal(ShownRouteBuilder.TailLengthNm, tail.LengthNm, 3);
    }

    [Fact]
    public void BuildPrimary_StarRunwayTransitionEndingInFm_ReturnsTailFromOwnFix()
    {
        // FM is the standard US-CIFP "fly from this fix on this course until vectored" leg —
        // e.g. KOAK WNDSR2 RW30: HOPTA → ALLXX → CRSEN → FM(CRSEN, 112°). The anchor for FM
        // is the leg's OWN FixIdentifier (CRSEN), not the previous leg.
        var rwyTransition = new CifpTransition(
            "RW30",
            [
                new CifpLeg("HOPTA", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                new CifpLeg("ALLXX", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
                new CifpLeg("CRSEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
                new CifpLeg("CRSEN", CifpPathTerminator.FM, null, null, null, CifpFixRole.None, 40, OutboundCourse: 112.0, null, null),
            ]
        );

        var stars = new[]
        {
            new CifpStarProcedure(
                Airport,
                "WNDSR2",
                CommonLegs: [new CifpLeg("HOPTA", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null)],
                EnrouteTransitions: new Dictionary<string, CifpTransition>(),
                RunwayTransitions: new Dictionary<string, CifpTransition>(StringComparer.OrdinalIgnoreCase) { ["RW30"] = rwyTransition }
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOPTA"] = (37.78, -122.15),
            ["ALLXX"] = (37.73, -122.06),
            ["CRSEN"] = (37.69, -122.01),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, null, stars);

        var ac = new AircraftModel
        {
            Callsign = "SWA1234",
            Destination = Airport,
            ActiveStarId = "WNDSR2",
            DestinationRunway = "30",
            NavigationRoute = ["HOPTA", "ALLXX", "CRSEN"],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.NotNull(tail);
        Assert.Equal(37.69, tail!.FromLat, 6);
        Assert.Equal(-122.01, tail.FromLon, 6);
        Assert.Equal(112.0, tail.HeadingMag, 3);
    }

    [Fact]
    public void BuildPrimary_SingleDigitRunway_ResolvesPaddedRunwayTransitionVector()
    {
        // Aircraft assigned runway 1R; the STAR publishes the transition under the zero-padded CIFP
        // key "RW01R". The client must pad "1R" → "RW01R" and produce that transition's vector tail
        // (issue #273).
        var rwyTransition = new CifpTransition(
            "RW01R",
            [
                new CifpLeg("CRSEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
                new CifpLeg("CRSEN", CifpPathTerminator.FM, null, null, null, CifpFixRole.None, 20, OutboundCourse: 112.0, null, null),
            ]
        );

        var stars = new[]
        {
            new CifpStarProcedure(
                Airport,
                "WNDSR2",
                CommonLegs: [new CifpLeg("HOPTA", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null)],
                EnrouteTransitions: new Dictionary<string, CifpTransition>(),
                RunwayTransitions: new Dictionary<string, CifpTransition>(StringComparer.OrdinalIgnoreCase) { ["RW01R"] = rwyTransition }
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOPTA"] = (37.78, -122.15),
            ["CRSEN"] = (37.69, -122.01),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, null, stars);

        var ac = new AircraftModel
        {
            Callsign = "SWA1234",
            Destination = Airport,
            ActiveStarId = "WNDSR2",
            DestinationRunway = "1R",
            NavRouteFixes = [new NavRouteFixDto("HOPTA", 37.78, -122.15, null)],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.NotNull(tail);
        Assert.Equal(112.0, tail!.HeadingMag, 3);
    }

    [Fact]
    public void BuildPrimary_SingleDigitRunway_DoesNotSuffixMatchDifferentRunwayTransition()
    {
        // Aircraft assigned runway 1R, but the STAR only publishes an RW31R transition. The old
        // EndsWith fallback ("RW31R".EndsWith("1R")) wrongly bound the 31R vector; zero-padding
        // "1R" → "RW01R" finds no transition, so no vector tail is produced (issue #273).
        var rwyTransition = new CifpTransition(
            "RW31R",
            [
                new CifpLeg("CRSEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
                new CifpLeg("CRSEN", CifpPathTerminator.FM, null, null, null, CifpFixRole.None, 20, OutboundCourse: 112.0, null, null),
            ]
        );

        var stars = new[]
        {
            new CifpStarProcedure(
                Airport,
                "WNDSR2",
                CommonLegs: [new CifpLeg("HOPTA", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null)],
                EnrouteTransitions: new Dictionary<string, CifpTransition>(),
                RunwayTransitions: new Dictionary<string, CifpTransition>(StringComparer.OrdinalIgnoreCase) { ["RW31R"] = rwyTransition }
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOPTA"] = (37.78, -122.15),
            ["CRSEN"] = (37.69, -122.01),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, null, stars);

        var ac = new AircraftModel
        {
            Callsign = "SWA1234",
            Destination = Airport,
            ActiveStarId = "WNDSR2",
            DestinationRunway = "1R",
            NavRouteFixes = [new NavRouteFixDto("HOPTA", 37.78, -122.15, null)],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.Null(tail);
    }

    [Fact]
    public void BuildPrimary_StarEndingAtFix_NoVectorLeg_ReturnsNullTail()
    {
        var stars = new[]
        {
            new CifpStarProcedure(
                Airport,
                "WNDSR2",
                CommonLegs:
                [
                    new CifpLeg("ENROU", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                    new CifpLeg("WNDSR", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
                ],
                EnrouteTransitions: new Dictionary<string, CifpTransition>(),
                RunwayTransitions: new Dictionary<string, CifpTransition>()
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENROU"] = (38.0, -122.5),
            ["WNDSR"] = (37.85, -122.30),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, null, stars);

        var ac = new AircraftModel
        {
            Callsign = "UAL123",
            Destination = Airport,
            ActiveStarId = "WNDSR2",
            DestinationRunway = RunwayDesignator,
            NavigationRoute = ["ENROU", "WNDSR"],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);
        Assert.Null(tail);
    }

    [Fact]
    public void BuildPrimary_SidWithRunwayTransitionEndingInVa_ReturnsTail()
    {
        var rwyTransition = new CifpTransition(
            "RW28L",
            [
                new CifpLeg("DEPRW", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                new CifpLeg("CLIMB", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
                new CifpLeg("", CifpPathTerminator.VA, null, null, null, CifpFixRole.None, 30, OutboundCourse: 095.0, null, null),
            ]
        );

        var sids = new[]
        {
            new CifpSidProcedure(
                Airport,
                "VECTOR1",
                CommonLegs: [],
                RunwayTransitions: new Dictionary<string, CifpTransition>(StringComparer.OrdinalIgnoreCase) { ["RW28L"] = rwyTransition },
                EnrouteTransitions: new Dictionary<string, CifpTransition>()
            ),
        };

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEPRW"] = (37.72, -122.22),
            ["CLIMB"] = (37.74, -122.18),
        };

        var navDb = NavigationDatabase.ForTesting(fixes, null, null, null, sids);

        var ac = new AircraftModel
        {
            Callsign = "UAL456",
            Departure = Airport,
            ActiveSidId = "VECTOR1",
            DepartureRunway = "28L",
            NavigationRoute = ["DEPRW", "CLIMB"],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.NotNull(tail);
        Assert.Equal(37.74, tail!.FromLat, 6);
        Assert.Equal(-122.18, tail.FromLon, 6);
        Assert.Equal(95.0, tail.HeadingMag, 3);
    }

    [Fact]
    public void BuildPrimary_PureVectorAircraft_NoRoute_NoProcedure_ReturnsTailFromAircraft()
    {
        var navDb = NavigationDatabase.ForTesting();

        var ac = new AircraftModel
        {
            Callsign = "UAL789",
            Position = new LatLon(37.6, -122.4),
            AssignedHeading = new MagneticHeading(250.0),
        };

        var (waypoints, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.Empty(waypoints);
        Assert.NotNull(tail);
        Assert.Equal(37.6, tail!.FromLat, 6);
        Assert.Equal(-122.4, tail.FromLon, 6);
        Assert.Equal(250.0, tail.HeadingMag, 3);
    }

    [Fact]
    public void BuildPrimary_RouteHasFixes_NoProcedureVector_NoAssignedHeading_NoTail()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENROU"] = (38.0, -122.5),
            ["WNDSR"] = (37.85, -122.30),
        };

        var navDb = NavigationDatabase.ForTesting(fixes);

        var ac = new AircraftModel
        {
            Callsign = "UAL101",
            NavRouteFixes = [new NavRouteFixDto("ENROU", 38.0, -122.5, null), new NavRouteFixDto("WNDSR", 37.85, -122.30, null)],
        };

        var (_, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);
        Assert.Null(tail);
    }

    [Fact]
    public void BuildPrimary_DrawsServerPositionsVerbatim_WithArcVerticesAndRestrictionLabels()
    {
        // The overlay must draw the server-provided route positions directly — including synthetic
        // arc-densification vertices (empty name) that no navdb lookup could resolve — and carry each
        // fix's pre-formatted crossing-restriction lines. No navDb resolution of names.
        var navDb = NavigationDatabase.ForTesting();

        var ac = new AircraftModel
        {
            Callsign = "UAL909",
            NavRouteFixes =
            [
                new NavRouteFixDto("SKUNK", 37.90, -122.40, ["≥6000", "250"]),
                new NavRouteFixDto("", 37.88, -122.36, null), // synthetic arc vertex
                new NavRouteFixDto("", 37.86, -122.33, null), // synthetic arc vertex
                new NavRouteFixDto("DOCKR", 37.84, -122.30, ["≤17000", "≥11000"]),
            ],
        };

        var (waypoints, tail) = ShownRouteBuilder.BuildPrimary(ac, navDb);

        Assert.Null(tail);
        Assert.Equal(4, waypoints.Count);

        // Positions passed through verbatim.
        Assert.Equal("SKUNK", waypoints[0].ResolvedName);
        Assert.Equal(37.90, waypoints[0].Lat, 6);
        Assert.Equal(-122.40, waypoints[0].Lon, 6);
        Assert.Equal(["≥6000", "250"], waypoints[0].RestrictionLines);

        // Arc vertices preserved as bare points (empty name, no restriction).
        Assert.Equal("", waypoints[1].ResolvedName);
        Assert.Null(waypoints[1].RestrictionLines);
        Assert.Equal("", waypoints[2].ResolvedName);

        // Window restriction preserved as two lines.
        Assert.Equal("DOCKR", waypoints[3].ResolvedName);
        Assert.Equal(["≤17000", "≥11000"], waypoints[3].RestrictionLines);
    }

    [Fact]
    public void BuildExpectedApproach_NoTransition_PrependsFacReciprocalAnchor_AppendsRunwayThreshold()
    {
        // Runway 30 oriented true heading 300° (so reciprocal = 120°).
        var runway = MakeRunway(threshold30Lat: 37.72, threshold30Lon: -122.22, trueHdg30: 300.0);

        var procedure = new CifpApproachProcedure(
            Airport,
            "I30",
            TypeCode: 'I',
            ApproachTypeName: "ILS",
            Runway: RunwayDesignator,
            CommonLegs:
            [
                new CifpLeg("IFFAF", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                new CifpLeg("FAF30", CifpPathTerminator.CF, null, null, null, CifpFixRole.FAF, 20, OutboundCourse: 300.0, null, null),
                new CifpLeg("RW30", CifpPathTerminator.CF, null, null, null, CifpFixRole.MAP, 30, OutboundCourse: 300.0, null, null),
            ],
            Transitions: new Dictionary<string, CifpTransition>(),
            MissedApproachLegs: [],
            HasHoldInLieu: false,
            HoldInLieuLeg: null
        );

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["IFFAF"] = (37.80, -122.32),
            ["FAF30"] = (37.78, -122.28),
        };

        var navDb = NavigationDatabase.ForTesting(
            fixes,
            [runway],
            new Dictionary<string, IReadOnlyList<CifpApproachProcedure>>(StringComparer.OrdinalIgnoreCase) { [Airport] = [procedure] }
        );

        var ac = new AircraftModel
        {
            Callsign = "UAL202",
            Destination = Airport,
            ExpectedApproach = "I30",
        };

        var result = ShownRouteBuilder.BuildExpectedApproach(ac, navDb);
        Assert.NotNull(result);
        var (waypoints, tail) = result!.Value;
        Assert.Null(tail);

        // Expected sequence: synthetic FAC-extension anchor (empty name) → IFFAF → FAF30 → RW30 threshold.
        Assert.Equal(4, waypoints.Count);
        Assert.Equal("", waypoints[0].ResolvedName);
        Assert.Equal("IFFAF", waypoints[1].ResolvedName);
        Assert.Equal("FAF30", waypoints[2].ResolvedName);
        Assert.Equal($"RW{RunwayDesignator}", waypoints[3].ResolvedName);

        // The anchor must be approximately 20 nm back from IFFAF along the FAC reciprocal
        // (true 120°). Verify by checking the great-circle distance and the bearing.
        var (anchorLat, anchorLon) = (waypoints[0].Lat, waypoints[0].Lon);
        double distNm = GeoMath.DistanceNm(anchorLat, anchorLon, waypoints[1].Lat, waypoints[1].Lon);
        Assert.InRange(distNm, ShownRouteBuilder.FacExtensionNm - 0.5, ShownRouteBuilder.FacExtensionNm + 0.5);
    }

    [Fact]
    public void BuildExpectedApproach_NamedTransition_BuildsTransitionThenCommonThenRunway()
    {
        var runway = MakeRunway(threshold30Lat: 37.72, threshold30Lon: -122.22, trueHdg30: 300.0);
        var transition = new CifpTransition(
            "SHARK",
            [
                new CifpLeg("SHARK", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("IFFAF", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
            ]
        );
        var procedure = new CifpApproachProcedure(
            Airport,
            "I30",
            TypeCode: 'I',
            ApproachTypeName: "ILS",
            Runway: RunwayDesignator,
            CommonLegs:
            [
                new CifpLeg("IFFAF", CifpPathTerminator.IF, null, null, null, CifpFixRole.IF, 10, null, null, null),
                new CifpLeg("FAF30", CifpPathTerminator.CF, null, null, null, CifpFixRole.FAF, 20, OutboundCourse: 300.0, null, null),
                new CifpLeg("RW30", CifpPathTerminator.CF, null, null, null, CifpFixRole.MAP, 30, OutboundCourse: 300.0, null, null),
            ],
            Transitions: new Dictionary<string, CifpTransition>(StringComparer.OrdinalIgnoreCase) { ["SHARK"] = transition },
            MissedApproachLegs: [],
            HasHoldInLieu: false,
            HoldInLieuLeg: null
        );

        var fixes = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            ["SHARK"] = (37.95, -122.35),
            ["IFFAF"] = (37.80, -122.32),
            ["FAF30"] = (37.78, -122.28),
        };

        var navDb = NavigationDatabase.ForTesting(
            fixes,
            [runway],
            new Dictionary<string, IReadOnlyList<CifpApproachProcedure>>(StringComparer.OrdinalIgnoreCase) { [Airport] = [procedure] }
        );

        var ac = new AircraftModel
        {
            Callsign = "UAL303",
            Destination = Airport,
            ExpectedApproach = "I30.SHARK",
        };

        var result = ShownRouteBuilder.BuildExpectedApproach(ac, navDb);
        Assert.NotNull(result);

        // Transition legs (SHARK → IFFAF) then common (FAF30 → RW30 threshold).
        // IFFAF in common is trimmed because it's the transition endpoint.
        var waypoints = result!.Value.Waypoints;
        Assert.Collection(
            waypoints,
            wp => Assert.Equal("SHARK", wp.ResolvedName),
            wp => Assert.Equal("IFFAF", wp.ResolvedName),
            wp => Assert.Equal("FAF30", wp.ResolvedName),
            wp => Assert.Equal($"RW{RunwayDesignator}", wp.ResolvedName)
        );
    }

    [Fact]
    public void BuildExpectedApproach_ApproachAlreadyActive_ReturnsNull()
    {
        var navDb = NavigationDatabase.ForTesting();

        var ac = new AircraftModel
        {
            Callsign = "UAL404",
            Destination = Airport,
            ExpectedApproach = "I30",
            ActiveApproachId = "I30",
        };

        Assert.Null(ShownRouteBuilder.BuildExpectedApproach(ac, navDb));
    }

    [Fact]
    public void BuildExpectedApproach_NoExpectedApproach_ReturnsNull()
    {
        var navDb = NavigationDatabase.ForTesting();
        var ac = new AircraftModel { Callsign = "UAL505", Destination = Airport };
        Assert.Null(ShownRouteBuilder.BuildExpectedApproach(ac, navDb));
    }

    [Fact]
    public void ParseApproachHint_NoDot_ReturnsApproachOnly()
    {
        var (approach, transition) = ShownRouteBuilder.ParseApproachHint("ILS 30");
        Assert.Equal("ILS 30", approach);
        Assert.Null(transition);
    }

    [Fact]
    public void ParseApproachHint_WithDot_SplitsApproachAndTransition()
    {
        var (approach, transition) = ShownRouteBuilder.ParseApproachHint("I30.SHARK");
        Assert.Equal("I30", approach);
        Assert.Equal("SHARK", transition);
    }

    private static RunwayInfo MakeRunway(double threshold30Lat, double threshold30Lon, double trueHdg30)
    {
        // End1 = 30 (threshold at given lat/lon), End2 = 12 (other end, computed by reciprocal).
        // Length 8000 ft is plausible for KOAK 30.
        return new RunwayInfo
        {
            AirportId = Airport,
            Id = new RunwayIdentifier("30", "12"),
            Designator = "30",
            Lat1 = threshold30Lat,
            Lon1 = threshold30Lon,
            Elevation1Ft = 9,
            TrueHeading1 = new TrueHeading(trueHdg30),
            Lat2 = threshold30Lat + 0.02,
            Lon2 = threshold30Lon - 0.02,
            Elevation2Ft = 9,
            TrueHeading2 = new TrueHeading(trueHdg30 + 180),
            LengthFt = 8000,
            WidthFt = 150,
        };
    }
}
