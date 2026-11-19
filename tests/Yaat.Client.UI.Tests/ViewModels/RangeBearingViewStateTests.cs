using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.ViewModels;

// Covers the shared state the Ground and Scope views all drive: one store behind them, so slot
// numbers are globally unique, but each measurement is tagged with the view it was taken in and only
// renders there, labelled in that view's units.
public class RangeBearingViewStateTests
{
    private static readonly LatLon Oakland = new(37.7213, -122.2208);

    // One degree of latitude is 60 NM, so 0.01 deg north is 0.6 NM (3,646 ft).
    private static readonly LatLon PointNorth = new(37.7313, -122.2208);

    private static RblTrack? NoAircraft(string callsign) => null;

    private static RangeBearingViewState NewState(out RangeBearingLineStore store)
    {
        store = new RangeBearingLineStore();
        return new RangeBearingViewState(store);
    }

    [Fact]
    public void PickTwiceCompletesAMeasurement()
    {
        RangeBearingViewState state = NewState(out _);

        state.Pick(RblEndpoint.AtPoint(Oakland, "A"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null));
        Assert.NotNull(state.Anchor);
        Assert.Empty(state.Lines);

        state.Pick(RblEndpoint.AtPoint(PointNorth, "B"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null));
        Assert.Null(state.Anchor);
        Assert.Single(state.Lines);
        Assert.True(state.HasLines);
    }

    [Fact]
    public void ArmingReportsInstructions()
    {
        RangeBearingViewState state = NewState(out _);
        var reported = new List<string>();
        state.StatusReported += reported.Add;

        state.Arm();

        Assert.True(state.IsMeasuring);
        Assert.Contains("first point", reported[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedMeasurementIsReportedWithItsReading()
    {
        RangeBearingViewState state = NewState(out _);
        var reported = new List<string>();
        state.StatusReported += reported.Add;

        state.Pick(RblEndpoint.AtPoint(Oakland, "A"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null));
        state.Pick(RblEndpoint.AtPoint(PointNorth, "B"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null));

        // Due north (360 true) reads 347 magnetic at Oakland's ~13 deg east declination — both map views
        // are magnetic-north-up, so the label must be magnetic.
        Assert.Equal("Measurement 347/0.60-1", reported[^1]);
    }

    [Fact]
    public void GroundReportsTheSameMeasurementInFeet()
    {
        RangeBearingViewState state = NewState(out _);
        var reported = new List<string>();
        state.StatusReported += reported.Add;

        state.Pick(RblEndpoint.AtPoint(Oakland, "A"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.FeetThenNauticalMiles, null));
        state.Pick(RblEndpoint.AtPoint(PointNorth, "B"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.FeetThenNauticalMiles, null));

        Assert.Equal("Measurement 347/3,648 ft-1", reported[^1]);
    }

    [Fact]
    public void MeasurementOnlyRendersInTheViewItWasTakenIn()
    {
        // Every view-model shares one state object so slot numbers stay globally unique, but a ground
        // measurement never shows on a Scope and vice versa.
        RangeBearingViewState state = NewState(out RangeBearingLineStore? store);
        object scopeWindow = new();

        state.Place(
            RblEndpoint.AtPoint(Oakland, "A"),
            RblEndpoint.AtPoint(PointNorth, "B"),
            new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
        );

        Assert.Single(state.Lines);
        Assert.Single(store.Lines);
        Assert.Equal(1, state.Lines[0].Slot);

        List<ResolvedRbl> ground = RangeBearingLineResolver.Resolve(state.Lines, NoAircraft, RblUnits.NauticalMiles, RblView.Ground, null);
        List<ResolvedRbl> scope = RangeBearingLineResolver.Resolve(state.Lines, NoAircraft, RblUnits.NauticalMiles, RblView.Scope, scopeWindow);
        Assert.Equal("347/0.60-1", Assert.Single(ground).Label);
        Assert.Empty(scope);
    }

    [Fact]
    public void CancelDropsTheAnchorAndReports()
    {
        RangeBearingViewState state = NewState(out _);
        var reported = new List<string>();
        state.Pick(RblEndpoint.AtPoint(Oakland, "A"), new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null));
        state.StatusReported += reported.Add;

        state.Cancel();

        Assert.Null(state.Anchor);
        Assert.False(state.IsMeasuring);
        Assert.Equal("Measure cancelled", Assert.Single(reported));
    }

    [Fact]
    public void CancelWithNothingPendingIsSilent()
    {
        RangeBearingViewState state = NewState(out _);
        var reported = new List<string>();
        state.StatusReported += reported.Add;

        state.Cancel();

        Assert.Empty(reported);
    }

    [Fact]
    public void SixteenthMeasurementReportsTheCeiling()
    {
        RangeBearingViewState state = NewState(out _);
        for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
        {
            state.Place(
                RblEndpoint.AtPoint(Oakland, "A"),
                RblEndpoint.AtPoint(PointNorth, "B"),
                new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
            );
        }

        var reported = new List<string>();
        state.StatusReported += reported.Add;
        state.Place(
            RblEndpoint.AtPoint(Oakland, "A"),
            RblEndpoint.AtPoint(PointNorth, "B"),
            new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
        );

        Assert.Equal(RangeBearingLineStore.MaxLines, state.Lines.Count);
        Assert.Contains("all 15", Assert.Single(reported), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAndClearReportOutcomes()
    {
        RangeBearingViewState state = NewState(out _);
        state.Place(
            RblEndpoint.AtPoint(Oakland, "A"),
            RblEndpoint.AtPoint(PointNorth, "B"),
            new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
        );

        var reported = new List<string>();
        state.StatusReported += reported.Add;

        state.Remove(7);
        state.Remove(1);
        state.Clear();

        Assert.Equal(["No measurement 7", "Measurement 1 removed", "No measurements to clear"], reported);
        Assert.False(state.HasLines);
    }

    [Fact]
    public void PruneDropsMeasurementsLatchedToADespawnedAircraft()
    {
        RangeBearingViewState state = NewState(out _);
        state.Place(
            RblEndpoint.OnAircraft("OAL123"),
            RblEndpoint.AtPoint(PointNorth, "B"),
            new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
        );
        Assert.Single(state.Lines);

        state.PruneMissing(_ => null);

        Assert.Empty(state.Lines);
        Assert.False(state.HasLines);
    }

    [Fact]
    public void PruneKeepsMeasurementsWhoseAircraftIsStillFlying()
    {
        RangeBearingViewState state = NewState(out _);
        state.Place(
            RblEndpoint.OnAircraft("OAL123"),
            RblEndpoint.AtPoint(PointNorth, "B"),
            new MeasureTarget(RblView.Ground, NoAircraft, RblUnits.NauticalMiles, null)
        );

        state.PruneMissing(cs => new AircraftModel { Callsign = cs });

        Assert.Single(state.Lines);
    }

    [Fact]
    public void TrackLookupReadsPositionAndGroundSpeed()
    {
        var aircraft = new AircraftModel
        {
            Callsign = "OAL123",
            Position = Oakland,
            GroundSpeed = 140,
        };

        RblTrackLookup lookup = RangeBearingViewState.TrackLookup(cs => cs == "OAL123" ? aircraft : null);

        RblTrack? track = lookup("OAL123");
        Assert.NotNull(track);
        Assert.Equal(Oakland.Lat, track.Value.Position.Lat, 6);
        Assert.Equal(140, track.Value.GroundSpeedKts);
        Assert.Null(lookup("NOPE"));
    }

    [Fact]
    public void ScopeAnchorWithoutItsSurfaceThrows()
    {
        NewState(out RangeBearingLineStore store);

        Assert.Throws<ArgumentException>(() => store.SetAnchor(RblEndpoint.AtPoint(Oakland, "A"), RblView.Scope, null));
    }

    [Fact]
    public void ScopePlacementWithoutItsSurfaceThrows()
    {
        NewState(out RangeBearingLineStore store);

        void Place() => store.Add(RblEndpoint.AtPoint(Oakland, "A"), RblEndpoint.AtPoint(PointNorth, "B"), RblView.Scope, null);

        Assert.Throws<ArgumentException>(Place);
    }
}
