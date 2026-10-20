using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class FrdResolverToFrdTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static readonly IReadOnlyList<(string Name, double Lat, double Lon)> TestFixes =
    [
        ("OAK", 37.7213, -122.2208),
        ("SFO", 37.6213, -122.3790),
        ("SAC", 38.5125, -121.4944),
        ("SUNOL", 37.5922, -121.8822),
    ];

    [Fact]
    public void ToFrd_AtFixPosition_ReturnsBareFixName()
    {
        string? result = FrdResolver.ToFrd(37.7213, -122.2208, TestFixes, StubNavDb());

        Assert.Equal("OAK", result);
    }

    [Fact]
    public void ToFrd_NearFixPosition_ReturnsBareFixName()
    {
        // Within 0.1nm of OAK
        string? result = FrdResolver.ToFrd(37.7214, -122.2207, TestFixes, StubNavDb());

        Assert.Equal("OAK", result);
    }

    [Fact]
    public void ToFrd_AwayFromFix_ReturnsFrdString()
    {
        // ~10nm north of OAK
        string? result = FrdResolver.ToFrd(37.8880, -122.2208, TestFixes, StubNavDb());

        Assert.NotNull(result);
        Assert.StartsWith("OAK", result);
        Assert.Matches(@"^OAK\d{6}$", result);
    }

    [Fact]
    public void ToFrd_NoFixesInRange_ReturnsNull()
    {
        string? result = FrdResolver.ToFrd(0.0, 0.0, TestFixes, StubNavDb(), maxNm: 1.0);

        Assert.Null(result);
    }

    [Fact]
    public void ToFrd_EmptyFixList_ReturnsNull()
    {
        string? result = FrdResolver.ToFrd(37.7213, -122.2208, [], StubNavDb());

        Assert.Null(result);
    }

    [Fact]
    public void ToFrd_RoundTrip_ResolvesCloseToOriginal()
    {
        // A position 10nm east of OAK
        double lat = 37.7213;
        double lon = -122.0500;

        string? frdString = FrdResolver.ToFrd(lat, lon, TestFixes, StubNavDb());
        Assert.NotNull(frdString);

        // Resolve it back
        NavigationDatabase stubFixes = TestNavDbFactory.WithFixes(("OAK", 37.7213, -122.2208));
        LatLon? resolved = FrdResolver.Resolve(frdString, stubFixes);
        Assert.NotNull(resolved);

        // Should be within 2nm of original (rounding of radial and distance)
        double distNm = GeoMath.DistanceNm(lat, lon, resolved.Value.Lat, resolved.Value.Lon);
        Assert.True(distNm < 2.0, $"Round-trip distance was {distNm:F2} nm, expected < 2.0");
    }

    [Fact]
    public void ToFrd_EmitsMagneticRadial_NotTrue()
    {
        // A point ~10 nm due-east of OAK (37.7213, -122.2208). FRD azimuths are conventionally
        // MAGNETIC (7110.65 4-4-3.a.1.2), not true. OAK sits in ~13-15 deg EAST declination, so the
        // emitted magnetic radial must be that many degrees LESS than the raw true bearing.
        double oakLat = 37.7213,
            oakLon = -122.2208;
        double lat = 37.7213,
            lon = -122.0500;

        double trueBrg = GeoMath.BearingTo(oakLat, oakLon, lat, lon);
        int trueRadial = (int)Math.Round(trueBrg);
        double magBrg = MagneticDeclination.TrueToMagnetic(trueBrg, oakLat, oakLon);
        int expectedRadial = (int)Math.Round(magBrg);

        string? frd = FrdResolver.ToFrd(lat, lon, TestFixes, StubNavDb());
        Assert.NotNull(frd);
        (string Fix, int? Radial, int? Distance)? parsed = FrdResolver.ParseFrd(frd);
        Assert.NotNull(parsed);
        Assert.Equal("OAK", parsed.Value.Fix);
        Assert.Equal(expectedRadial, parsed.Value.Radial);

        // Declination actually applied, correct sign and realistic magnitude for OAK.
        Assert.True(expectedRadial < trueRadial, $"magnetic {expectedRadial} should be east-of-true less than true {trueRadial}");
        Assert.InRange(trueRadial - expectedRadial, 10, 18);
    }

    [Fact]
    public void Resolve_InterpretsRadialAsMagnetic()
    {
        // The mirror of ToFrd: a typed FRD radial is magnetic, so Resolve must convert magnetic->true
        // before projecting. OAK090010 should land 10 nm along the 090 MAGNETIC radial.
        double oakLat = 37.7213,
            oakLon = -122.2208;
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("OAK", oakLat, oakLon));

        LatLon? resolved = FrdResolver.Resolve("OAK090010", navDb);
        Assert.NotNull(resolved);

        double trueBrg = GeoMath.BearingTo(oakLat, oakLon, resolved.Value.Lat, resolved.Value.Lon);
        double magBrg = MagneticDeclination.TrueToMagnetic(trueBrg, oakLat, oakLon);
        Assert.InRange(magBrg, 89.0, 91.0); // resolved point is on the 090 magnetic radial
        Assert.InRange(GeoMath.DistanceNm(oakLat, oakLon, resolved.Value.Lat, resolved.Value.Lon), 9.5, 10.5);
    }

    [Fact]
    public void ToFrd_PicksNearestFix()
    {
        // Position much closer to SUNOL than OAK
        string? result = FrdResolver.ToFrd(37.59, -121.88, TestFixes, StubNavDb());

        Assert.NotNull(result);
        Assert.StartsWith("SUNOL", result);
    }

    [Fact]
    public void ToFrd_RadialFormattedThreeDigits()
    {
        // Position north of OAK — radial should be zero-padded
        string? result = FrdResolver.ToFrd(37.9, -122.2208, TestFixes, StubNavDb());

        Assert.NotNull(result);
        // The string after the fix name should be 6 digits: radial(3) + distance(3)
        string suffix = result["OAK".Length..];
        Assert.Equal(6, suffix.Length);
        Assert.True(int.TryParse(suffix, out _));
    }

    [Theory]
    [InlineData("OAK169001", true)]
    [InlineData("ABI037030", true)]
    [InlineData("DUMBA090010", true)]
    [InlineData("OAK", false)]
    [InlineData("SUNOL", false)]
    [InlineData("OAK169", false)] // radial-only shape is not treated as an FRD identifier
    [InlineData("AA409", false)]
    [InlineData("OAK000010", false)] // radial 000 is not a valid FRD azimuth
    [InlineData("XYZ999123", false)] // radial 999 is not a valid FRD azimuth
    [InlineData("A169001", false)] // single-letter anchor
    [InlineData("TOOLONG169001", false)] // anchor longer than 5 chars
    [InlineData("OA1169001", false)] // digit inside the anchor
    [InlineData("", false)]
    public void IsFrdIdentifier_ClassifiesNames(string name, bool expected) => Assert.Equal(expected, FrdResolver.IsFrdIdentifier(name));

    // vNAS NavData publishes thousands of adapted fixes whose identifiers are themselves FRD
    // strings (OAK169001, ABI037030, …). Anchoring on one emits an FRD-of-an-FRD such as
    // "DCTF OAK169001231001", which no controller or pilot can read back.
    private static readonly IReadOnlyList<(string Name, double Lat, double Lon)> FrdNamedFixes =
    [
        ("OAK", 37.7213, -122.2208),
        ("OAK169001", 37.7053, -122.2166), // ~1 nm from OAK, on its 169 radial
    ];

    [Fact]
    public void ToFrd_NearFrdNamedFix_AnchorsOnRealFixInstead()
    {
        // ~3 nm south of the OAK169001 adapted fix — that fix is the nearest, but it must not anchor.
        string? result = FrdResolver.ToFrd(37.6553, -122.2166, FrdNamedFixes, TestNavDbFactory.WithFixes([.. FrdNamedFixes]));

        Assert.NotNull(result);
        Assert.Matches(@"^OAK\d{6}$", result);
    }

    [Fact]
    public void ToFrd_OnTopOfFrdNamedFix_ReturnsBareIdentifier()
    {
        // Within 0.1 nm: the identifier itself is a real, resolvable fix, so naming it is correct.
        string? result = FrdResolver.ToFrd(37.7054, -122.2165, FrdNamedFixes, TestNavDbFactory.WithFixes([.. FrdNamedFixes]));

        Assert.Equal("OAK169001", result);
    }

    [Fact]
    public void ToFrd_RealNavData_NeverAnchorsOnFrdNamedFix()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        // Guards the premise: the real vNAS dataset really does publish these as fixes.
        Assert.NotNull(navDb.GetFixPosition("OAK169001"));

        IReadOnlyList<(string Name, double Lat, double Lon)> allFixes = navDb.GetFixTuples();
        var frdNamed = allFixes.Where(f => FrdResolver.IsFrdIdentifier(f.Name)).Take(200).ToList();
        Assert.NotEmpty(frdNamed);

        // Probe 2 nm north of each adapted fix: far enough that the distance doesn't round to zero
        // (which would return a bare identifier rather than a constructed FRD), close enough that the
        // adapted fix or one of its neighbours is still the nearest entry of any kind.
        int probesWhereFrdFixIsNearest = 0;
        foreach ((string Name, double Lat, double Lon) probeAnchor in frdNamed)
        {
            double lat = probeAnchor.Lat + (2.0 / 60.0);
            double lon = probeAnchor.Lon;

            (string Name, double Lat, double Lon) nearest = allFixes.MinBy(f => GeoMath.DistanceNm(lat, lon, f.Lat, f.Lon));
            if (!FrdResolver.IsFrdIdentifier(nearest.Name))
            {
                continue;
            }

            probesWhereFrdFixIsNearest++;

            string? result = FrdResolver.ToFrd(lat, lon, allFixes, navDb);
            Assert.NotNull(result);
            string? anchor = FrdResolver.ParseFrd(result)?.Fix;
            Assert.NotNull(anchor);
            Assert.False(FrdResolver.IsFrdIdentifier(anchor), $"ToFrd anchored on FRD-named fix '{anchor}' (result '{result}')");
        }

        Assert.True(probesWhereFrdFixIsNearest > 0, "no probe had an FRD-named fix as its nearest entry — the test proves nothing");
    }

    // CIFP theta/rho legs define these fixes as FRDs read against the anchor VOR's station declination of record, so
    // resolving the FRD with that declination lands on the published fix; the live model at the VOR misses it by a mile or more.
    [Theory]
    [InlineData("OAK135015", "SIWBI")]
    [InlineData("RBL357019", "HOMAN")]
    public void Resolve_VorAnchoredFrd_LandsOnTheCifpFixItDefines(string frd, string publishedFix)
    {
        NavigationDatabase navDb = RealNavDb();
        string anchor = Assert.NotNull(FrdResolver.ParseFrd(frd)).Fix;
        (double Lat, double Lon) anchorPosition = Assert.NotNull(navDb.GetFixPosition(anchor));
        double stationDeclination = Assert.NotNull(navDb.GetStationDeclination(anchor));
        double modelledDeclination = MagneticDeclination.GetDeclination(anchorPosition.Lat, anchorPosition.Lon);
        Assert.True(
            Math.Abs(stationDeclination - modelledDeclination) > 3,
            $"{anchor} station declination {stationDeclination:F2} is within 3 deg of the modelled {modelledDeclination:F2}: test is vacuous"
        );

        (double Lat, double Lon) fixPosition = Assert.NotNull(navDb.GetFixPosition(publishedFix));
        LatLon resolved = Assert.NotNull(FrdResolver.Resolve(frd, navDb));

        double missNm = GeoMath.DistanceNm(resolved.Lat, resolved.Lon, fixPosition.Lat, fixPosition.Lon);
        Assert.True(missNm < 0.3, $"{frd} resolved {missNm:F2} nm from {publishedFix}, expected < 0.3");
    }

    [Fact]
    public void Resolve_NonNavaidAnchor_KeepsTheModelledDeclinationAtTheFix()
    {
        NavigationDatabase navDb = RealNavDb();
        Assert.Null(navDb.GetStationDeclination("SUNOL"));
        (double Lat, double Lon) sunol = Assert.NotNull(navDb.GetFixPosition("SUNOL"));

        LatLon resolved = Assert.NotNull(FrdResolver.Resolve("SUNOL090010", navDb));

        double trueRadial = MagneticDeclination.MagneticToTrue(90, sunol.Lat, sunol.Lon);
        LatLon expected = GeoMath.ProjectPoint(new LatLon(sunol.Lat, sunol.Lon), new TrueHeading(trueRadial), 10);
        double offsetNm = GeoMath.DistanceNm(expected, resolved);
        Assert.True(offsetNm < 0.02, $"SUNOL090010 resolved {offsetNm:F3} nm from the modelled-declination point");
    }

    [Fact]
    public void ToFrd_RealNavData_RoundTripsThroughResolveAtAnOakAnchoredPoint()
    {
        NavigationDatabase navDb = RealNavDb();
        (double Lat, double Lon) oak = Assert.NotNull(navDb.GetFixPosition("OAK"));
        double stationDeclination = Assert.NotNull(navDb.GetStationDeclination("OAK"));

        // 12 nm out on OAK's 090 radial, which lies on true bearing 090 plus the station's declination of record.
        LatLon point = GeoMath.ProjectPoint(new LatLon(oak.Lat, oak.Lon), new TrueHeading(90 + stationDeclination), 12);
        string? frd = FrdResolver.ToFrd(point.Lat, point.Lon, [("OAK", oak.Lat, oak.Lon)], navDb);
        Assert.NotNull(frd);
        Assert.Equal("OAK090012", frd);

        LatLon resolved = Assert.NotNull(FrdResolver.Resolve(frd, navDb));
        double offsetNm = GeoMath.DistanceNm(point, resolved);
        Assert.True(offsetNm < 0.05, $"{frd} resolved {offsetNm:F3} nm from the point it was built from");
    }

    // The DCT target comes from FrdResolver and the AT condition's point from the AT command's station-variation radial;
    // when the two read the same FRD differently, the aircraft flies to one point and misses the condition at the other.
    [Fact]
    public void DctThenAtTheSameVorFrd_ConditionFiresAtTheDirectToPoint()
    {
        NavigationDatabase navDb = RealNavDb();
        (double Lat, double Lon) oak = Assert.NotNull(navDb.GetFixPosition("OAK"));
        double stationDeclination = Assert.NotNull(navDb.GetStationDeclination("OAK"));
        var radial = new TrueHeading(90 + stationDeclination);
        LatLon frdPoint = GeoMath.ProjectPoint(new LatLon(oak.Lat, oak.Lon), radial, 25);

        SimulationEngine engine = BuildEngine();
        // 10 nm short of the point on the radial, flying outbound along it.
        var ac = new AircraftState
        {
            Callsign = "FRD01",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(frdPoint, new TrueHeading(radial.Degrees + 180), 10),
            TrueHeading = radial,
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);

        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == ac.Callsign)
            {
                warnings.Add(warning);
            }
        };

        CommandResult result = engine.SendCommand(ac.Callsign, "DCT OAK090025; AT OAK090025 FH 360");
        Assert.True(result.Success, result.Message);

        for (int t = 0; t < 300; t++)
        {
            engine.TickOneSecond();
        }

        _output.WriteLine($"warnings: [{string.Join(" | ", warnings)}], assigned {ac.Targets.AssignedMagneticHeading?.Degrees}");
        Assert.DoesNotContain(warnings, w => w.Contains("Missed condition", StringComparison.Ordinal));
        Assert.Equal(new MagneticHeading(360), ac.Targets.AssignedMagneticHeading);
    }

    /// <summary>The stub fixes as a nav database: no station declinations, so radials read against the modelled declination.</summary>
    private static NavigationDatabase StubNavDb() => TestNavDbFactory.WithFixes([.. TestFixes]);

    private static NavigationDatabase RealNavDb()
    {
        TestVnasData.EnsureInitialized();
        return NavigationDatabase.Instance;
    }

    private SimulationEngine BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        return engine;
    }
}
