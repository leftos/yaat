using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AutoScratchpadResolver"/> — the STARS primary-scratchpad destination
/// fallback. Gate and classification cases run against the real ZOA config snapshot, because the
/// behavior depends on facility adaptation that varies per area (O90's OAK area shows destinations
/// for departures, its HWD area does not; its top-level O90 area has no tower list at all).
/// </summary>
[Collection("NavDbMutator")]
public class AutoScratchpadResolverTests
{
    public AutoScratchpadResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string departure, string destination)
    {
        return new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            Altitude = 6000,
            Transponder = new AircraftTransponder { Code = 1234, Mode = "ModeC" },
            FlightPlan = new AircraftFlightPlan { Departure = departure, Destination = destination },
        };
    }

    /// <summary>
    /// Resolves a STARS facility config and one of its named areas from the ZOA snapshot.
    /// Returns false when the snapshot is absent so tests skip silently.
    /// </summary>
    private static bool TryGetZoaArea(string facilityId, string areaName, out StarsConfig starsConfig, out StarsAreaConfig area)
    {
        starsConfig = null!;
        area = null!;

        var config = TestArtccConfig.LoadZoa();
        var facility = config?.FindFacility(facilityId);
        if (facility?.StarsConfiguration is null)
        {
            return false;
        }

        foreach (var candidate in facility.StarsConfiguration.Areas)
        {
            if (candidate.Name == areaName)
            {
                starsConfig = facility.StarsConfiguration;
                area = candidate;
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void PrimaryArrival_ShowsDestination()
    {
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KOAK");

        Assert.Equal("OAK", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void SatelliteArrival_ShowsDestination()
    {
        // SFO is internal to O90 but is not the OAK area's tower-list airport, so an SFO arrival
        // viewed from the OAK area is a satellite arrival.
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KSFO");
        var classification = AutoScratchpadResolver.Classify(ac, starsConfig, area);

        Assert.True(classification.IsSatelliteArrival);
        Assert.False(classification.IsPrimaryArrival);
        Assert.Equal("SFO", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void Departure_ShowsDestination_WhenAreaEnablesIt()
    {
        // O90's OAK area has showDestinationDepartures = true.
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        Assert.True(area.ShowDestinationDepartures);
        var ac = MakeAircraft("KOAK", "KLAX");

        Assert.Equal("LAX", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void Departure_Suppressed_WhenAreaDisablesIt()
    {
        // O90's HWD area has showDestinationDepartures = false — the same departure that shows a
        // destination from the OAK area must show nothing here.
        if (!TryGetZoaArea("O90", "HWD", out var starsConfig, out var area))
        {
            return;
        }

        Assert.False(area.ShowDestinationDepartures);
        var ac = MakeAircraft("KHWD", "KLAX");

        Assert.True(AutoScratchpadResolver.Classify(ac, starsConfig, area).IsDeparture);
        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void AreaWithNoTowerList_TreatsEveryArrivalAsSatellite()
    {
        // O90's top-level area has no towerListConfigurations, so it has no primary airport and
        // the primary-arrival branch is unreachable.
        if (!TryGetZoaArea("O90", "O90", out var starsConfig, out var area))
        {
            return;
        }

        Assert.Empty(area.TowerListConfigurations);
        var ac = MakeAircraft("KLAX", "KOAK");
        var classification = AutoScratchpadResolver.Classify(ac, starsConfig, area);

        Assert.False(classification.IsPrimaryArrival);
        Assert.True(classification.IsSatelliteArrival);
        Assert.Equal("OAK", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void Overflight_ShowsNothing()
    {
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KDEN", "KJFK");
        var classification = AutoScratchpadResolver.Classify(ac, starsConfig, area);

        Assert.True(classification.IsOverflight);
        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void ArrivalAndDeparture_WithinFacility_UsesArrivalBranch()
    {
        // An OAK-to-SFO hop is both a departure and an arrival. CRC checks the arrival cases
        // first, so the OAK area classifies it as a satellite arrival.
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KOAK", "KSFO");
        var classification = AutoScratchpadResolver.Classify(ac, starsConfig, area);

        Assert.True(classification.IsDeparture);
        Assert.True(classification.IsSatelliteArrival);
    }

    [Fact]
    public void ExistingScratchpad1_SuppressesFallback()
    {
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KOAK");
        ac.Stars.Scratchpad1 = "ABC";

        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void ClearedScratchpad1_SuppressesFallback()
    {
        // Clearing the slot must actually clear it — otherwise the destination would reappear
        // and the controller's clear would look like a no-op.
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KOAK");
        ac.Stars.WasScratchpad1Cleared = true;

        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void BlankDestination_ShowsNothing()
    {
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KOAK", "");

        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void IcaoFiledDestination_DisplaysFaaId()
    {
        // Filing "KOAK" must display "OAK", not a 3-char truncation of the ICAO form ("KOA").
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KOAK");
        var result = AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area);

        Assert.Equal("OAK", result);
        Assert.NotEqual("KOA", result);
    }

    [Fact]
    public void SuppressedArrivalGate_DoesNotFallThroughToDepartureGate()
    {
        // CRC's if/else-if chain stops at the first matching classification: a primary arrival whose
        // gate is off yields null, even when the track is also a departure and the departure gate is
        // on. Matching on the gate instead of the classification would leak a destination here.
        var starsConfig = new StarsConfig { InternalAirports = ["OAK", "SFO"] };
        var area = new StarsAreaConfig
        {
            TowerListConfigurations = [new TowerListConfig { AirportId = "OAK" }],
            ShowDestinationPrimaryArrivals = false,
            ShowDestinationDepartures = true,
        };
        var ac = MakeAircraft("KSFO", "KOAK");

        var classification = AutoScratchpadResolver.Classify(ac, starsConfig, area);
        Assert.True(classification.IsPrimaryArrival);
        Assert.True(classification.IsDeparture);

        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongDestination_IsNotTruncatedToScratchpadLimit(bool allow4Char)
    {
        // STARS applies the 3/4-character scratchpad limit only to controller-entered text; the
        // destination fallback renders in full regardless of Allow4CharacterScratchpad.
        var starsConfig = new StarsConfig { InternalAirports = ["CL56"], Allow4CharacterScratchpad = allow4Char };
        var area = new StarsAreaConfig
        {
            TowerListConfigurations = [new TowerListConfig { AirportId = "CL56" }],
            ShowDestinationPrimaryArrivals = true,
        };
        var ac = MakeAircraft("KLAX", "CL56");

        Assert.Equal("CL56", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void UnresolvedForeignDestination_RendersInFull_NotAMisleadingPrefix()
    {
        // A departure to a field with no published FAA id falls back to the identifier as filed.
        // Truncating it would render "CYVR" as "CYV", which reads like a 3-letter FAA id or an
        // adapted scratchpad code and would mislead the controller.
        var starsConfig = new StarsConfig { InternalAirports = ["OAK"] };
        var area = new StarsAreaConfig { ShowDestinationDepartures = true };
        var ac = MakeAircraft("KOAK", "CYVR");

        Assert.True(AutoScratchpadResolver.Classify(ac, starsConfig, area).IsDeparture);
        Assert.Equal("CYVR", AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area));
    }

    [Fact]
    public void ResolvingNeverMutatesAircraftState()
    {
        // The fallback is a display projection. Writing it back to Stars.Scratchpad1 would ship the
        // synthetic destination to CRC as though a controller had typed it, bypassing CRC's own
        // per-area gating and breaking the SP1 clear/undo toggle for the student.
        if (!TryGetZoaArea("O90", "OAK", out var starsConfig, out var area))
        {
            return;
        }

        var ac = MakeAircraft("KLAX", "KOAK");

        var result = AutoScratchpadResolver.ResolveAutoScratchpad1(ac, starsConfig, area);

        Assert.Equal("OAK", result);
        Assert.Null(ac.Stars.Scratchpad1);
        Assert.Null(ac.Stars.Scratchpad2);
        Assert.False(ac.Stars.WasScratchpad1Cleared);
    }

    [Fact]
    public void NullConfig_ShowsNothing()
    {
        var ac = MakeAircraft("KLAX", "KOAK");

        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, null, null));
        Assert.Null(AutoScratchpadResolver.ResolveAutoScratchpad1(ac, new StarsConfig(), null));
    }
}
