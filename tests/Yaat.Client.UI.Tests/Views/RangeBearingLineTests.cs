using Xunit;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// Covers the shared range/bearing (distance measuring) tool: CRC STARS *T label formatting, the
// fifteen-slot store, latched endpoints following an aircraft, and the Ground View's feet/NM switch.
public class RangeBearingLineFormatterTests
{
    [Fact]
    public void RadarLabel_IsBearingSlashDistanceSlashSlot()
    {
        var label = RangeBearingLineFormatter.Format(4.2, 87.0, null, 1, RblUnits.NauticalMiles);

        Assert.Equal("087/4.20-1", label);
    }

    [Fact]
    public void BearingIsZeroPaddedToThreeDigits()
    {
        Assert.StartsWith("005/", RangeBearingLineFormatter.Format(1.0, 5.0, null, 1, RblUnits.NauticalMiles), StringComparison.Ordinal);
    }

    [Fact]
    public void BearingZeroRendersAs360()
    {
        // CRC's NavCalc.NormalizeHeading maps 0 to 360; bearings are read as 360, never 000.
        Assert.StartsWith("360/", RangeBearingLineFormatter.Format(1.0, 0.0, null, 1, RblUnits.NauticalMiles), StringComparison.Ordinal);
    }

    [Fact]
    public void BearingWrapsAboveThreeSixty()
    {
        Assert.StartsWith("010/", RangeBearingLineFormatter.Format(1.0, 370.0, null, 1, RblUnits.NauticalMiles), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeBearingNormalizes()
    {
        Assert.StartsWith("350/", RangeBearingLineFormatter.Format(1.0, -10.0, null, 1, RblUnits.NauticalMiles), StringComparison.Ordinal);
    }

    [Fact]
    public void MinutesToGoIsAppendedBeforeSlot()
    {
        var label = RangeBearingLineFormatter.Format(4.2, 87.0, 2, 1, RblUnits.NauticalMiles);

        Assert.Equal("087/4.20/2-1", label);
    }

    [Fact]
    public void DistanceOver999ClampsToHashes()
    {
        var label = RangeBearingLineFormatter.Format(1200.0, 90.0, null, 3, RblUnits.NauticalMiles);

        Assert.Equal("090/###.##-3", label);
    }

    [Fact]
    public void MinutesOver99ClampToHashes()
    {
        var label = RangeBearingLineFormatter.Format(10.0, 90.0, 140, 2, RblUnits.NauticalMiles);

        Assert.Equal("090/10.00/##-2", label);
    }

    [Fact]
    public void PendingLineHasNoSlotSuffix()
    {
        var label = RangeBearingLineFormatter.Format(4.2, 87.0, null, null, RblUnits.NauticalMiles);

        Assert.Equal("087/4.20", label);
    }

    [Fact]
    public void GroundUnitsUseFeetUnderOneMile()
    {
        // 0.2 NM = 1215 ft. Taxiway-scale distances are unreadable in hundredths of a mile.
        var label = RangeBearingLineFormatter.Format(0.2, 87.0, null, 1, RblUnits.FeetThenNauticalMiles);

        Assert.Equal("087/1,215 ft-1", label);
    }

    [Fact]
    public void GroundUnitsSwitchToMilesAtOneMile()
    {
        var label = RangeBearingLineFormatter.Format(1.35, 87.0, null, 1, RblUnits.FeetThenNauticalMiles);

        Assert.Equal("087/1.35 NM-1", label);
    }

    [Fact]
    public void MinutesToGo_OnlyWhenExactlyOneEndIsAMovingAircraft()
    {
        var moving = new RblTrack(new LatLon(37.7, -122.2), 120.0);
        var other = new RblTrack(new LatLon(37.8, -122.3), 200.0);

        // One aircraft, one fixed point: 6 NM at 120 kt = 3 minutes.
        Assert.Equal(3, RangeBearingLineFormatter.MinutesToGo(moving, null, 6.0));
        Assert.Equal(3, RangeBearingLineFormatter.MinutesToGo(null, moving, 6.0));

        // Aircraft to aircraft, and point to point: CRC shows no time field for either.
        Assert.Null(RangeBearingLineFormatter.MinutesToGo(moving, other, 6.0));
        Assert.Null(RangeBearingLineFormatter.MinutesToGo(null, null, 6.0));
    }

    [Fact]
    public void MinutesToGo_IsNullForAStoppedAircraft()
    {
        Assert.Null(RangeBearingLineFormatter.MinutesToGo(new RblTrack(new LatLon(37.7, -122.2), 0.0), null, 6.0));
        Assert.Null(RangeBearingLineFormatter.MinutesToGo(new RblTrack(new LatLon(37.7, -122.2), null), null, 6.0));
    }
}

public class RangeBearingLineStoreTests
{
    private static readonly LatLon Somewhere = new(37.7213, -122.2208);
    private static readonly LatLon Elsewhere = new(37.8, -122.3);

    private static RblEndpoint Point(LatLon at) => RblEndpoint.AtPoint(at, "PT");

    [Fact]
    public void SlotsAreAssignedInAscendingOrder()
    {
        var store = new RangeBearingLineStore();

        Assert.Equal(1, store.Add(Point(Somewhere), Point(Elsewhere)));
        Assert.Equal(2, store.Add(Point(Somewhere), Point(Elsewhere)));
        Assert.Equal(3, store.Add(Point(Somewhere), Point(Elsewhere)));
    }

    [Fact]
    public void RemovedSlotIsReusedByTheNextLine()
    {
        var store = new RangeBearingLineStore();
        store.Add(Point(Somewhere), Point(Elsewhere));
        store.Add(Point(Somewhere), Point(Elsewhere));
        store.Add(Point(Somewhere), Point(Elsewhere));

        Assert.True(store.Remove(2));

        Assert.Equal(2, store.Add(Point(Somewhere), Point(Elsewhere)));
    }

    [Fact]
    public void RemovingAnEmptySlotReportsFailure()
    {
        var store = new RangeBearingLineStore();

        Assert.False(store.Remove(1));
        Assert.False(store.Remove(0));
        Assert.False(store.Remove(99));
    }

    [Fact]
    public void SixteenthLineIsRefused()
    {
        var store = new RangeBearingLineStore();
        for (var i = 0; i < RangeBearingLineStore.MaxLines; i++)
        {
            Assert.NotNull(store.Add(Point(Somewhere), Point(Elsewhere)));
        }

        Assert.True(store.IsFull);
        Assert.Null(store.Add(Point(Somewhere), Point(Elsewhere)));
        Assert.False(store.Arm());
        Assert.False(store.SetAnchor(Point(Somewhere)));
    }

    [Fact]
    public void AnchorThenCompletePlacesOneLine()
    {
        var store = new RangeBearingLineStore();

        Assert.True(store.Arm());
        Assert.True(store.SetAnchor(RblEndpoint.OnAircraft("OAL123")));
        Assert.NotNull(store.PendingAnchor);

        Assert.Equal(1, store.Complete(Point(Elsewhere)));
        Assert.Null(store.PendingAnchor);
        Assert.False(store.IsArmed);
        Assert.Single(store.Lines);
    }

    [Fact]
    public void CompleteWithoutAnAnchorPlacesNothing()
    {
        var store = new RangeBearingLineStore();

        Assert.Null(store.Complete(Point(Elsewhere)));
        Assert.Empty(store.Lines);
    }

    [Fact]
    public void DisarmDiscardsThePendingAnchorButKeepsPlacedLines()
    {
        var store = new RangeBearingLineStore();
        store.Add(Point(Somewhere), Point(Elsewhere));
        store.SetAnchor(RblEndpoint.OnAircraft("OAL123"));

        store.Disarm();

        Assert.Null(store.PendingAnchor);
        Assert.False(store.IsArmed);
        Assert.Single(store.Lines);
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        var store = new RangeBearingLineStore();
        store.Add(Point(Somewhere), Point(Elsewhere));
        store.SetAnchor(Point(Somewhere));

        store.Clear();

        Assert.Empty(store.Lines);
        Assert.Null(store.PendingAnchor);
        Assert.False(store.HasLines);
    }

    [Fact]
    public void PruneDropsLinesLatchedToAVanishedAircraft()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.OnAircraft("OAL123"), Point(Elsewhere));
        store.Add(RblEndpoint.OnAircraft("SWA45"), RblEndpoint.OnAircraft("UAL9"));
        store.Add(Point(Somewhere), Point(Elsewhere));

        store.PruneMissing(cs => cs != "OAL123" && cs != "UAL9");

        // Only the fixed point-to-point line survives; both latched lines referenced a gone aircraft.
        var remaining = store.Lines;
        Assert.Single(remaining);
        Assert.Equal(3, remaining[0].Slot);
    }

    [Fact]
    public void PruneDropsAPendingAnchorOnAVanishedAircraft()
    {
        var store = new RangeBearingLineStore();
        store.SetAnchor(RblEndpoint.OnAircraft("OAL123"));

        store.PruneMissing(_ => false);

        Assert.Null(store.PendingAnchor);
    }

    [Fact]
    public void PruneKeepsLinesWhoseAircraftAreStillPresent()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.OnAircraft("OAL123"), Point(Elsewhere));

        store.PruneMissing(_ => true);

        Assert.Single(store.Lines);
    }

    [Fact]
    public void ChangedFiresOnPlacementAndRemoval()
    {
        var store = new RangeBearingLineStore();
        var count = 0;
        store.Changed += () => count++;

        store.Add(Point(Somewhere), Point(Elsewhere));
        store.Remove(1);

        Assert.Equal(2, count);
    }
}

public class RangeBearingLineResolverTests
{
    private static readonly LatLon Oakland = new(37.7213, -122.2208);

    // One degree of latitude is 60 NM, so due north by 0.1 deg is 6 NM.
    private static readonly LatLon SixNorth = new(37.8213, -122.2208);

    [Fact]
    public void LatchedEndpointFollowsTheAircraft()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.OnAircraft("OAL123"), RblEndpoint.AtPoint(SixNorth, "PT"));

        var position = Oakland;
        RblTrack? Lookup(string cs) => cs == "OAL123" ? new RblTrack(position, null) : null;

        var first = RangeBearingLineResolver.Resolve(store.Lines, Lookup, RblUnits.NauticalMiles);
        Assert.Single(first);
        Assert.Equal(Oakland.Lat, first[0].A.Lat, 6);

        // Move the aircraft; the resolved line moves with it.
        position = new LatLon(37.7513, -122.2208);
        var second = RangeBearingLineResolver.Resolve(store.Lines, Lookup, RblUnits.NauticalMiles);
        Assert.Equal(37.7513, second[0].A.Lat, 6);
        Assert.NotEqual(first[0].Label, second[0].Label);
    }

    [Fact]
    public void LineIsSkippedWhileItsAircraftIsUnresolvable()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.OnAircraft("GONE"), RblEndpoint.AtPoint(SixNorth, "PT"));

        var resolved = RangeBearingLineResolver.Resolve(store.Lines, _ => null, RblUnits.NauticalMiles);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolvedLabelCarriesDistanceAndSlot()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.AtPoint(Oakland, "A"), RblEndpoint.AtPoint(SixNorth, "B"));

        var resolved = RangeBearingLineResolver.Resolve(store.Lines, _ => null, RblUnits.NauticalMiles);

        Assert.Single(resolved);
        Assert.EndsWith("/6.00-1", resolved[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingAircraftToFixedPointGetsATimeToGo()
    {
        var store = new RangeBearingLineStore();
        store.Add(RblEndpoint.OnAircraft("OAL123"), RblEndpoint.AtPoint(SixNorth, "PT"));

        // 6 NM at 120 kt = 3 minutes.
        var resolved = RangeBearingLineResolver.Resolve(store.Lines, _ => new RblTrack(Oakland, 120.0), RblUnits.NauticalMiles);

        Assert.EndsWith("/6.00/3-1", resolved[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingLineDrawsFromAnchorToCursorWithNoSlot()
    {
        var pending = RangeBearingLineResolver.ResolvePending(RblEndpoint.AtPoint(Oakland, "A"), SixNorth, _ => null, RblUnits.NauticalMiles);

        Assert.NotNull(pending);
        Assert.EndsWith("/6.00", pending.Label, StringComparison.Ordinal);
        Assert.Equal(0, pending.Slot);
    }

    [Fact]
    public void PendingLineIsNullWithoutAnAnchor()
    {
        Assert.Null(RangeBearingLineResolver.ResolvePending(null, Oakland, _ => null, RblUnits.NauticalMiles));
    }
}
