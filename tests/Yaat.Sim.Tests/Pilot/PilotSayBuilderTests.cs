using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Unit tests for <see cref="PilotSayBuilder"/> plain-text formatters. SAY-class verbs
/// emit plain numeric values; downstream consumers (RPO readback, TTS) own radio
/// phraseology like digit-by-digit speech, "thousand"/"hundred" forms, and "Mach point X".
/// </summary>
public class PilotSayBuilderTests
{
    [Theory]
    [InlineData(360, "360")]
    [InlineData(1, "001")]
    [InlineData(90, "090")]
    [InlineData(270, "270")]
    public void PlainHeading_AlwaysThreeDigitsZeroPadded(int hdg, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.PlainHeading(hdg));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(500, "500")]
    [InlineData(5000, "5,000")]
    [InlineData(5300, "5,300")]
    [InlineData(8000, "8,000")]
    [InlineData(17900, "17,900")]
    [InlineData(18000, "FL180")]
    [InlineData(25000, "FL250")]
    [InlineData(35000, "FL350")]
    public void PlainAltitude_CommaThousandsBelowFL180_FlightLevelAbove(int alt, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.PlainAltitude(alt));
    }

    [Theory]
    [InlineData(0.78, "0.78")]
    [InlineData(0.65, "0.65")]
    [InlineData(0.8, "0.80")]
    public void PlainMach_TwoDecimals(double mach, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.PlainMach(mach));
    }

    [Theory]
    [InlineData("I19L", "ILS 19L")]
    [InlineData("I28R", "ILS 28R")]
    [InlineData("I19C", "ILS 19C")]
    [InlineData("R28L", "RNAV 28L")]
    [InlineData("V19", "VOR 19")]
    [InlineData("V09", "VOR 09")]
    [InlineData("IY28L", "ILS Y 28L")]
    [InlineData("IZ19R", "ILS Z 19R")]
    [InlineData("R09", "RNAV 09")]
    [InlineData("L05", "LOC 05")]
    [InlineData("N28", "NDB 28")]
    public void PlainApproach_ExpandsTypeAndKeepsRunwayPlain(string id, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.PlainApproach(id));
    }

    // ── BuildPosition ────────────────────────────────────────────────────────
    // Position reports anchor on fixes the working controller is likely to recognize:
    // departure/destination airport, filed-route fixes (expanded SIDs/STARs/airways),
    // and active DCT-queue fixes. When the chosen anchor is a fix (not an airport),
    // the readback appends a parenthetical airport reference for an unfamiliar reader.
    // If no candidate is within 50 nm, falls back to the nearest sizeable airport
    // (max runway ≥ 6,500 ft) within 100 nm.

    private static AircraftState MakeAircraftAt(double lat, double lon)
    {
        return new AircraftState
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    [Fact]
    public void BuildPosition_DepartureAirportAnchor_ShowsCodeAndFriendlyName()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var oakPos = navDb.GetFixPosition("KOAK");
        if (oakPos is null)
        {
            return;
        }

        // Aircraft 5 nm north of KOAK on departure roll.
        var ac = MakeAircraftAt(oakPos.Value.Lat + 5.0 / 60.0, oakPos.Value.Lon);
        ac.FlightPlan.Departure = "KOAK";
        ac.FlightPlan.Destination = "KSAN";

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Contains("KOAK", result);
        Assert.Contains("Oakland", result);
        Assert.Contains("Airport", result);
    }

    [Fact]
    public void BuildPosition_DestinationAirportAnchor_ShowsCodeAndFriendlyName()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var sfoPos = navDb.GetFixPosition("KSFO");
        if (sfoPos is null)
        {
            return;
        }

        // Aircraft 8 nm east of KSFO on final.
        var ac = MakeAircraftAt(sfoPos.Value.Lat, sfoPos.Value.Lon + 8.0 / 60.0);
        ac.FlightPlan.Departure = "KLAX";
        ac.FlightPlan.Destination = "KSFO";

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Contains("KSFO", result);
        Assert.Contains("San Francisco", result);
    }

    [Fact]
    public void BuildPosition_NoFlightPlan_FallsBackToSizeableAirport()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var oakPos = navDb.GetFixPosition("KOAK");
        if (oakPos is null)
        {
            return;
        }

        // VFR aircraft, no flight plan, ~3 nm south of KOAK.
        var ac = MakeAircraftAt(oakPos.Value.Lat - 3.0 / 60.0, oakPos.Value.Lon);

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.NotEqual("Unable to determine position", result);
        // Sizeable-airport fallback should land on a nearby airport — KOAK or another major
        // ZOA field. Either way the friendly "Airport" suffix must be present.
        Assert.Contains("Airport", result);
    }

    [Fact]
    public void BuildPosition_FarFromEverything_Unable()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Mid-Pacific — no fixes or airports within 100 nm.
        var ac = MakeAircraftAt(20.0, -160.0);

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Equal("Unable to determine position", result);
    }

    /// <summary>
    /// Picks a ZOA-area fix that is not an airport and not a VHF navaid — i.e. a named
    /// RNAV intersection like WAITZ or MENLO. Returns null if none of the candidates are
    /// present in the test navdata.
    /// </summary>
    private static (string Name, double Lat, double Lon)? FindNamedIntersection(NavigationDatabase navDb)
    {
        foreach (var name in new[] { "WAITZ", "MENLO", "ARCHI", "STINS", "BRIXX", "GROAN", "CEDES", "WOODY", "LIDAT", "SUNOL" })
        {
            var pos = navDb.GetFixPosition(name);
            if (pos is null)
            {
                continue;
            }
            if (navDb.GetAirportElevation(name) is not null)
            {
                continue;
            }
            if (navDb.GetNavaidName(name) is not null)
            {
                continue;
            }
            return (name, pos.Value.Lat, pos.Value.Lon);
        }
        return null;
    }

    [Fact]
    public void BuildPosition_VorAnchor_RendersAsCodeDashNameVor()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Pick the first VHF navaid we can find from a small candidate set.
        (string Name, double Lat, double Lon, string FriendlyName)? vor = null;
        foreach (var code in new[] { "OSI", "SAC", "SNS", "MOD", "PYE", "OAK" })
        {
            var pos = navDb.GetFixPosition(code);
            var navaidName = navDb.GetNavaidName(code);
            if (pos is not null && !string.IsNullOrEmpty(navaidName))
            {
                vor = (code, pos.Value.Lat, pos.Value.Lon, navaidName);
                break;
            }
        }
        if (vor is null)
        {
            return;
        }

        var ac = MakeAircraftAt(vor.Value.Lat, vor.Value.Lon);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = vor.Value.Name, Position = new LatLon(vor.Value.Lat, vor.Value.Lon) });

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Contains($"{vor.Value.Name} - ", result);
        Assert.Contains(" VOR", result);
    }

    [Fact]
    public void BuildPosition_IntersectionAnchor_RendersWithIntersectionSuffixAndAirportContext()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var fix = FindNamedIntersection(navDb);
        if (fix is null)
        {
            return;
        }

        var ac = MakeAircraftAt(fix.Value.Lat, fix.Value.Lon);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Value.Name, Position = new LatLon(fix.Value.Lat, fix.Value.Lon) });

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Contains($"{fix.Value.Name} intersection", result);
        // Intersections don't self-place — the report must append a comma-separated airport context.
        Assert.Contains(", ", result);
        Assert.Contains("Airport", result);
    }

    [Fact]
    public void BuildPosition_PrefersRouteFixOverArbitraryNearbyFix()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var oakPos = navDb.GetFixPosition("KOAK");
        if (oakPos is null)
        {
            return;
        }

        // Aircraft 1 nm from KOAK with KOAK both as departure and on the route. The old
        // implementation picked the unrestricted nearest fix from the entire navdata
        // (often an obscure RNAV waypoint a few hundred feet away). The new
        // implementation must restrict to route candidates and pick KOAK.
        var ac = MakeAircraftAt(oakPos.Value.Lat + 1.0 / 60.0, oakPos.Value.Lon);
        ac.FlightPlan.Departure = "KOAK";
        ac.FlightPlan.Destination = "KSAN";

        var result = PilotSayBuilder.BuildPosition(ac);

        Assert.Contains("KOAK", result);
    }

    [Theory]
    // Real ZOA navdata: KOAK has "OAKLAND SAN FRANCISCO BAY" (no slash, no INTL); the metro
    // qualifier "SAN FRANCISCO" must be detected and trimmed.
    [InlineData("OAKLAND SAN FRANCISCO BAY", "Oakland Airport")]
    [InlineData("SAN FRANCISCO INTL", "San Francisco Airport")]
    [InlineData("JOHN F KENNEDY INTL", "John F Kennedy Airport")]
    [InlineData("DENVER INTL", "Denver Airport")]
    [InlineData("CHICAGO O'HARE INTL", "Chicago O'hare Airport")] // TextInfo.ToTitleCase quirk: doesn't recapitalize after an apostrophe
    [InlineData("LIVERMORE MUNI", "Livermore Airport")]
    [InlineData("STOCKTON METRO", "Stockton Airport")]
    [InlineData("SACRAMENTO EXEC", "Sacramento Airport")]
    [InlineData("MONTEREY RGNL", "Monterey Airport")]
    [InlineData("NORMAN Y MINETA SAN JOSE INTL", "Norman Y Mineta Airport")]
    [InlineData("PALO ALTO", "Palo Alto Airport")]
    [InlineData("SEATTLE-TACOMA INTL", "Seattle-Tacoma Airport")]
    public void FriendlyAirportName_StripsSuffixesAndAppendsAirport(string raw, string expected)
    {
        Assert.Equal(expected, PilotSayBuilder.FriendlyAirportName(raw));
    }

    // Diagnostic: prints the raw airport name strings for a handful of well-known fields
    // so we can see what NavData provides and tune the friendly-name heuristic. Skipped
    // by default; flip Skip to "" to inspect the raw values via xUnit output.
    [Fact(Skip = "Diagnostic only — re-enable to inspect raw airport names from NavData")]
    public void Diagnostic_DumpAirportNames()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        foreach (var code in new[] { "KOAK", "KSFO", "KSJC", "KSAC", "KSCK", "KFAT", "KMRY", "KLAX", "KJFK", "KORD", "KDEN", "KSAN", "KSEA", "KBOS" })
        {
            Console.WriteLine($"{code, -6} | name='{navDb.GetAirportName(code)}'");
        }
    }

    [Fact]
    public void FindNearestSizeableAirport_FiltersByRunwayLength()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var oakPos = navDb.GetFixPosition("KOAK");
        if (oakPos is null)
        {
            return;
        }

        // KOAK has a 10,500 ft runway — qualifies at any sane threshold.
        var atOak = navDb.FindNearestSizeableAirport(new LatLon(oakPos.Value.Lat, oakPos.Value.Lon), 6500, 50);
        Assert.NotNull(atOak);

        // 50,000 ft threshold — no real airport qualifies.
        var noneQualify = navDb.FindNearestSizeableAirport(new LatLon(oakPos.Value.Lat, oakPos.Value.Lon), 50_000, 100);
        Assert.Null(noneQualify);
    }
}
