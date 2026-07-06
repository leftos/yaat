using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// KPO83 turn-back regression (KOAK HUSSH2 departure). A fixed-path RNAV SID filed as
/// "SID VOR TRANSITION" — "HUSSH2 OAK SYRAH ..." — must apply the published SYRAH enroute
/// transition (NIITE -> REBAS -> TAMMM -> SYRAH) and drop the redundant on-field OAK VOR.
///
/// The bug: the enroute-transition match only inspected the first post-SID token (OAK), which
/// is not a HUSSH2 transition name, so no transition was applied and OAK was appended literally
/// between NIITE and SYRAH — a ~140 degree reversal back over the departure airport.
/// </summary>
public class Hussh2DepartureTransitionTests
{
    public Hussh2DepartureTransitionTests()
    {
        // Pin the real CIFP-backed singleton before any test method runs to avoid racing with
        // other test classes that initialize on demand.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeOakDeparture(string sidRoute, string runwayDesignator, double runwayHeading)
    {
        var ac = new AircraftState
        {
            Callsign = "KPO83",
            AircraftType = "GLEX",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(runwayHeading),
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "MDLR",
                Route = sidRoute,
                Altitude = PlannedAltitude.Ifr(41000),
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: runwayDesignator, airportId: "OAK", heading: runwayHeading, elevationFt: 6),
        };
        return ac;
    }

    [Fact]
    public void HUSSH2_ColocatedOakBeforeSyrahTransition_AppliesTransitionAndDropsOak()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeOakDeparture("HUSSH2 OAK SYRAH Q128 JSICA", "30", 300.0);

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);

        Assert.NotNull(result);
        var names = result.Targets.Select(t => t.Name).ToList();

        // The on-field OAK VOR must never appear — routing through it reverses the aircraft back
        // over the departure airport.
        Assert.DoesNotContain("OAK", names);

        // The published HUSSH2 SYRAH enroute transition (NIITE -> REBAS -> TAMMM -> SYRAH) must be flown.
        Assert.Contains("REBAS", names);
        Assert.Contains("TAMMM", names);
        Assert.Contains("SYRAH", names);
        Assert.True(
            names.IndexOf("NIITE") >= 0 && names.IndexOf("NIITE") < names.IndexOf("SYRAH"),
            $"Expected NIITE before SYRAH in [{string.Join(", ", names)}]"
        );

        // No fix after NIITE may lie within 1 nm of KOAK — i.e. no reversal over the field.
        var airportPos = TestVnasData.NavigationDb.GetFixPosition("KOAK");
        Assert.NotNull(airportPos);
        int niiteIdx = names.IndexOf("NIITE");
        for (int i = niiteIdx + 1; i < result.Targets.Count; i++)
        {
            double dist = GeoMath.DistanceNm(new LatLon(airportPos.Value.Lat, airportPos.Value.Lon), result.Targets[i].Position);
            Assert.True(dist > 1.0, $"Fix {result.Targets[i].Name} at index {i} is {dist:F2} nm from KOAK — a turn-back over the field");
        }
    }

    [Fact]
    public void HUSSH2_ColocatedOakBeforeNonTransitionFix_DropsOak()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // MLF is a real VOR but not a HUSSH2 enroute transition, so no transition excludes OAK.
        // The general rule still applies: the co-located OAK (first fix after the SID body) is dropped.
        var ac = MakeOakDeparture("HUSSH2 OAK MLF", "30", 300.0);

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);

        Assert.NotNull(result);
        var names = result.Targets.Select(t => t.Name).ToList();

        Assert.DoesNotContain("OAK", names);
        Assert.Contains("MLF", names);
        // NIITE (last SID body fix) is followed directly by MLF, not a turn-back through OAK.
        Assert.True(names.IndexOf("NIITE") < names.IndexOf("MLF"), $"Expected NIITE before MLF in [{string.Join(", ", names)}]");
    }

    [Fact]
    public void OAK6_RadarVectorsSid_ColocatedOakDroppedFromRoute()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // OAK6 is a radar-vectors SID (empty core body); the co-located OAK is leading. It anchors any
        // following route element during expansion but must never appear as a flown waypoint.
        var ac = MakeOakDeparture("OAK6 OAK SUNOL", "28R", 280.0);

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);

        Assert.NotNull(result);
        var names = result.Targets.Select(t => t.Name).ToList();
        Assert.DoesNotContain("OAK", names);
    }
}
