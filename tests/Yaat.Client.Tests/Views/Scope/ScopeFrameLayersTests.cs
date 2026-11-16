using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The frame's vector and history layers: OWN draws a vector on the viewer's tracks only, and on every track with no
/// viewer; ALL on every track; history dots draw on every track whatever OWN or ALL says; 0 minutes and a count of 0 draw
/// nothing.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeFrameLayersTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static ScopeFrame Frame(ScopeViewer viewer, ScopeVectorOwnership ownership, double minutes, int history, params AircraftModel[] aircraft)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            aircraft,
            null,
            new MapViewport
            {
                PixelWidth = 1000,
                PixelHeight = 800,
                CenterLat = 37.0,
                CenterLon = -122.0,
            },
            ScopeTemplates.EramId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = viewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            ScopeProfile.OthersFull,
            VectorMinutes: minutes,
            VectorOwnership: ownership,
            HistoryCount: history,
            LocalRings: new Dictionary<string, ScopeTrackRings>()
        );
        return ScopeFrame.Build(input);
    }

    private static AircraftModel Track(string callsign, string? owner, string? sectorCode)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.TrueTrack = 90;
        ac.Owner = owner;
        ac.OwnerFacilityId = owner is null ? null : "ZOA";
        ac.OwnerSectorCode = sectorCode;
        ac.PositionHistory = [.. Enumerable.Range(0, 10).Select(i => new[] { 37.0 - (i * 0.01), -122.0 })];
        return ac;
    }

    private static AircraftModel[] ThreeTracks() =>
        [Track("OWN1", "OAK_44_CTR", "C44"), Track("OTH1", "OAK_12_CTR", "C12"), Track("UNO1", owner: null, sectorCode: null)];

    private static string[] WithVector(ScopeFrame frame) => [.. frame.Tracks.Where(t => t.VectorEnd is not null).Select(t => t.Aircraft.Callsign)];

    [Fact]
    public void NoViewer_Own_DrawsAVectorOnEveryTrack() =>
        Assert.Equal(["OWN1", "OTH1", "UNO1"], WithVector(Frame(ScopeViewer.None, ScopeVectorOwnership.Own, 1, 0, ThreeTracks())));

    [Fact]
    public void WithAViewer_Own_DrawsAVectorOnlyOnTheViewersTrack() =>
        Assert.Equal(["OWN1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.Own, 1, 0, ThreeTracks())));

    [Fact]
    public void WithAViewer_All_DrawsAVectorOnEveryTrack() =>
        Assert.Equal(["OWN1", "OTH1", "UNO1"], WithVector(Frame(EramViewer, ScopeVectorOwnership.All, 1, 0, ThreeTracks())));

    [Fact]
    public void ZeroMinutes_DrawsNoVector() => Assert.Empty(WithVector(Frame(EramViewer, ScopeVectorOwnership.All, 0, 0, ThreeTracks())));

    [Fact]
    public void History_DrawsOnEveryTrack_WhateverOwnOrAllSays()
    {
        ScopeFrame frame = Frame(EramViewer, ScopeVectorOwnership.Own, 1, 3, ThreeTracks());

        Assert.All(frame.Tracks, t => Assert.Equal(3, t.History?.Count));
    }

    [Fact]
    public void ZeroHistory_DrawsNoDots() =>
        Assert.All(Frame(EramViewer, ScopeVectorOwnership.All, 1, 0, ThreeTracks()).Tracks, t => Assert.Empty(t.History ?? []));

    [Fact]
    public void StationaryTrack_HasNoVector_ButKeepsItsHistory()
    {
        AircraftModel parked = Track("OWN1", "OAK_44_CTR", "C44");
        parked.GroundSpeed = 0.5;

        ScopeTrack track = Assert.Single(Frame(EramViewer, ScopeVectorOwnership.All, 4, 5, parked).Tracks);

        Assert.Null(track.VectorEnd);
        Assert.Equal(5, track.History?.Count);
    }
}
