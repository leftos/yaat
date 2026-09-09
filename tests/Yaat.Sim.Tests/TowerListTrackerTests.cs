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

    /// <summary>
    /// Two facilities under one ARTCC that both name a list <c>P1</c> — the shape ZOA's FAT and NCT have — each with
    /// its own tower list airport. The tracker has to hold the two apart; keyed by the bare list id they overwrite
    /// each other.
    /// </summary>
    private static (TowerListTracker Tracker, double OakLat, double OakLon, double FatLat, double FatLon) BuildTwoFacilityTracker()
    {
        double oakLat = 37.7213,
            oakLon = -122.2208;
        double fatLat = 36.7762,
            fatLon = -119.7181;

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
                                    Id = "nct-area-1",
                                    TowerListConfigurations = [new TowerListConfig { AirportId = "OAK", Range = 30 }],
                                },
                            ],
                        },
                    },
                    new FacilityConfig
                    {
                        Id = "FAT",
                        Type = "AtctTracon",
                        Name = "Fresno TRACON",
                        StarsConfiguration = new StarsConfig
                        {
                            Lists =
                            [
                                new StarsListConfig
                                {
                                    Id = "P1",
                                    Title = "FAT P-LIST",
                                    CoordinationChannel = null,
                                    SortField = "DropZoneEntryTime",
                                },
                            ],
                            Areas =
                            [
                                new StarsAreaConfig
                                {
                                    Id = "fat-area-1",
                                    TowerListConfigurations = [new TowerListConfig { AirportId = "FAT", Range = 30 }],
                                },
                            ],
                        },
                    },
                ],
            },
        };

        var tracker = new TowerListTracker();

        using (
            NavigationDatabase.ScopedOverride(
                NavigationDatabase.ForTesting(
                    fixes: new Dictionary<string, (double Lat, double Lon)> { ["OAK"] = (oakLat, oakLon), ["FAT"] = (fatLat, fatLon) }
                )
            )
        )
        {
            tracker.Initialize(config);
        }

        return (tracker, oakLat, oakLon, fatLat, fatLon);
    }

    /// <summary>
    /// ZOA's FAT and NCT both declare a list called <c>P1</c> over different airports. Each facility's list holds its
    /// own traffic, and once both are populated a static second is no change at all — keyed by list id alone the two
    /// airports' proximity passes evict each other and every tick reports a change.
    /// </summary>
    [Fact]
    public void TwoFacilitiesSharingAListId_KeepSeparateEntries_AndSettle()
    {
        var (tracker, oakLat, oakLon, fatLat, fatLon) = BuildTwoFacilityTracker();

        var overOak = MakeAircraft("AAL100", oakLat, oakLon);
        var overFat = MakeAircraft("DAL200", fatLat, fatLon);

        Assert.True(tracker.Update([overOak, overFat], 10.0));

        // Nothing moved: both lists already hold their aircraft, so the next seconds report no change.
        Assert.False(tracker.Update([overOak, overFat], 11.0));
        Assert.False(tracker.Update([overOak, overFat], 12.0));

        Assert.Equal([("AAL100", 10.0)], tracker.GetEntries(new TowerListKey("NCT", "P1")));
        Assert.Equal([("DAL200", 10.0)], tracker.GetEntries(new TowerListKey("FAT", "P1")));
    }

    /// <summary>
    /// Two aircraft that come into a list's range in the same second share a dwell second, so nothing but the
    /// callsign can order them — and the order has to be the same on every run, since it is what the snapshot and
    /// the P-list carry. The two trackers here see the same second in opposite snapshot orders and read back the
    /// same list, ordinal by callsign.
    /// </summary>
    [Fact]
    public void TwoAircraftEnteringInTheSameSecond_ReadBackInOneFixedOrder()
    {
        var (first, oakLat, oakLon, _, _) = BuildTwoFacilityTracker();
        var (second, _, _, _, _) = BuildTwoFacilityTracker();

        var a = MakeAircraft("AAL100", oakLat, oakLon);
        var b = MakeAircraft("UAL200", oakLat + 0.01, oakLon);
        var c = MakeAircraft("SWA300", oakLat, oakLon + 0.01);

        first.Update([a, b, c], 10.0);
        second.Update([c, b, a], 10.0);

        var expected = new List<(string, double)> { ("AAL100", 10.0), ("SWA300", 10.0), ("UAL200", 10.0) };
        Assert.Equal(expected, first.GetEntries(new TowerListKey("NCT", "P1")));
        Assert.Equal(expected, second.GetEntries(new TowerListKey("NCT", "P1")));
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

        var entries = tracker.GetEntries(new TowerListKey("NCT", "P1"));
        Assert.Equal(2, entries.Count);
        Assert.Equal("AAL100", entries[0].Callsign);
        Assert.Equal(10.0, entries[0].EnteredAtSeconds);
        Assert.Equal("UAL200", entries[1].Callsign);
        Assert.Equal(20.0, entries[1].EnteredAtSeconds);
    }
}
