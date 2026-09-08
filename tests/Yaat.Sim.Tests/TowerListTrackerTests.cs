using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="TowerListTracker.Update"/> as a change detector: what it reports when an aircraft enters a tower list
/// airport's range, stays in it, leaves it or disappears, and the entry order <see cref="TowerListTracker.GetEntries"/>
/// renders. A hand-built one-list ARTCC over a fix set scoped to this class, so the geometry under test is the
/// tracker's and nothing else.
/// </summary>
public class TowerListTrackerTests
{
    /// <summary>The tracker reads position only, so there is no altitude here to get wrong.</summary>
    private static AircraftState MakeAircraft(string callsign, double lat, double lon)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            Transponder = new AircraftTransponder { Code = 1200, Mode = "ModeC" },
        };
    }

    private static (TowerListTracker Tracker, double AirportLat, double AirportLon) BuildTracker()
    {
        double aptLat = 37.7213,
            aptLon = -122.2208; // OAK approx

        var config = new ArtccConfigRoot
        {
            Id = "ZOA",
            LastUpdatedAt = "",
            Facility = new FacilityConfig
            {
                Id = "ZOA",
                Type = "Artcc",
                Name = "Oakland ARTCC",
                ChildFacilities =
                [
                    new FacilityConfig
                    {
                        Id = "NCT",
                        Type = "AtctTracon",
                        Name = "NorCal TRACON",
                        StarsConfiguration = new StarsConfig
                        {
                            Lists =
                            [
                                // P-list: no coordination channel, sorted by DropZoneEntryTime
                                new StarsListConfig
                                {
                                    Id = "P1",
                                    Title = "OAK P-LIST",
                                    CoordinationChannel = null,
                                    SortField = "DropZoneEntryTime",
                                },
                            ],
                            Areas =
                            [
                                new StarsAreaConfig
                                {
                                    Id = "area-1",
                                    TowerListConfigurations = [new TowerListConfig { AirportId = "OAK", Range = 30 }],
                                },
                            ],
                        },
                    },
                ],
            },
        };

        var tracker = new TowerListTracker();

        // Scoped, not SetInstance: this project runs its test classes in parallel, and the process-wide default is the
        // real navigation database every other class reads.
        using (
            NavigationDatabase.ScopedOverride(
                NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["OAK"] = (aptLat, aptLon) })
            )
        )
        {
            tracker.Initialize(config);
        }

        return (tracker, aptLat, aptLon);
    }

    [Fact]
    public void Update_NoAircraft_ReturnsFalse()
    {
        var (tracker, _, _) = BuildTracker();
        Assert.False(tracker.Update([], 0));
    }

    [Fact]
    public void Update_AircraftEntersRange_ReturnsTrue()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        // Aircraft 10nm from airport (within 30nm range)
        var ac = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        Assert.True(tracker.Update([ac], 10.0));
    }

    [Fact]
    public void Update_SameAircraftStillInRange_ReturnsFalse()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        var ac = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        tracker.Update([ac], 10.0); // enters

        // Same aircraft, same range — no change
        Assert.False(tracker.Update([ac], 11.0));
    }

    [Fact]
    public void Update_AircraftLeavesRange_ReturnsTrue()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        var ac = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        tracker.Update([ac], 10.0); // enters

        // Move far away (>30nm)
        ac.Position = new LatLon(aptLat + 1.0, ac.Position.Lon);
        Assert.True(tracker.Update([ac], 11.0));
    }

    [Fact]
    public void Update_AircraftDeleted_ReturnsTrue()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        var ac = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        tracker.Update([ac], 10.0);

        // Aircraft disappears from snapshot
        Assert.True(tracker.Update([], 11.0));
    }

    [Fact]
    public void Update_SecondAircraftEnters_ReturnsTrue()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        var ac1 = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        tracker.Update([ac1], 10.0);
        tracker.Update([ac1], 11.0); // stable

        var ac2 = MakeAircraft("UAL200", aptLat + 0.10, aptLon);
        Assert.True(tracker.Update([ac1, ac2], 12.0));
    }

    [Fact]
    public void GetEntries_SortedByEntryTime()
    {
        var (tracker, aptLat, aptLon) = BuildTracker();

        var ac1 = MakeAircraft("AAL100", aptLat + 0.15, aptLon);
        tracker.Update([ac1], 10.0);

        var ac2 = MakeAircraft("UAL200", aptLat + 0.10, aptLon);
        tracker.Update([ac1, ac2], 20.0);

        var entries = tracker.GetEntries("P1");
        Assert.Equal(2, entries.Count);
        Assert.Equal("AAL100", entries[0].Callsign);
        Assert.Equal(10.0, entries[0].EnteredAtSeconds);
        Assert.Equal("UAL200", entries[1].Callsign);
        Assert.Equal(20.0, entries[1].EnteredAtSeconds);
    }
}
