using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// <see cref="StudentWorkedRange"/>: the solo student works its own position type and every type below it down to a
/// chosen lowest, less any type an online scenario position covers at the airport.
/// </summary>
public class StudentWorkedRangeTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public StudentWorkedRangeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void LevelOf_DelCallsign_IsDelivery()
    {
        Assert.Equal(AtcPositionLevel.Delivery, AtcPositionLevels.LevelOf("OAK_DEL"));
        Assert.Equal(AtcPositionLevel.Delivery, AtcPositionLevels.LevelOf("oak_del"));
        Assert.Equal(AtcPositionLevel.Ground, AtcPositionLevels.LevelOf("OAK_GND"));
        Assert.Equal(AtcPositionLevel.Approach, AtcPositionLevels.LevelOf("NCT_APP"));
        Assert.Null(AtcPositionLevels.LevelOf("OAK_ATIS"));
        Assert.Null(AtcPositionLevels.LevelOf(null));
    }

    [Fact]
    public void For_LowestAboveOwn_Throws()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StudentWorkedRange.For(ground, AtcPositionLevel.Tower, [])
        );
        Assert.Contains("Tower", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Ground", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_LeavesOutTheStudentAndNonAutoConnectPositions()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner approach = Resolve("NCT_APP");
        var range = StudentWorkedRange.For(
            approach,
            AtcPositionLevel.Delivery,
            [Atc("NCT_APP", "NCT", autoConnect: true), Atc("OAK_GND", "OAK", autoConnect: false), Atc("OAK_TWR", "OAK", autoConnect: true)]
        );

        Assert.NotNull(range);
        Assert.Equal(AtcPositionLevel.Approach, range.Own);
        Assert.Equal(AtcPositionLevel.Delivery, range.Lowest);
        OnlinePosition online = Assert.Single(range.Online);
        Assert.Equal(new OnlinePosition(AtcPositionLevel.Tower, "OAK"), online);
        Assert.Null(StudentWorkedRange.For(approach, null, []));
        Assert.Null(StudentWorkedRange.For(null, AtcPositionLevel.Delivery, []));
    }

    [Fact]
    public void Works_OnlineTowerAtOak_TakesTowerOutAtOakOnly()
    {
        if (_zoa is null)
        {
            return;
        }

        StudentWorkedRange range = StudentWorkedRange.For(Resolve("NCT_APP"), AtcPositionLevel.Delivery, [Atc("OAK_TWR", "OAK", autoConnect: true)])!;

        Assert.False(range.Works(AtcPositionLevel.Tower, "OAK"));
        Assert.True(range.Works(AtcPositionLevel.Tower, "KSFO"));
        Assert.True(range.Works(AtcPositionLevel.Ground, "OAK"));
        Assert.True(range.Works(AtcPositionLevel.Approach, "OAK"));
        Assert.False(range.Works(AtcPositionLevel.Center, "OAK"));
    }

    private TrackOwner Resolve(string callsign) => _zoa!.ResolvePosition(_zoa!.FindPositionByCallsign(callsign, facilityHint: null)!.Id)!;

    private ResolvedAtcPosition Atc(string callsign, string facilityId, bool autoConnect) =>
        new()
        {
            Source = new ScenarioAtc { FacilityId = facilityId, AutoConnect = autoConnect },
            Owner = Resolve(callsign),
        };
}
