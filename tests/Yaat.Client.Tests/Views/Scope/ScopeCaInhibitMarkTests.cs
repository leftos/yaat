using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope's CA inhibit cue (7110.65 §5-14-6.c.4): <see cref="AircraftModel.FromDto"/> and
/// <see cref="AircraftModel.UpdateFromDto"/> carry <c>AircraftDto.IsCaInhibited</c>, and a STARS full block of an inhibited
/// track draws a filled triangle in the cell right after its callsign, as CRC's block does (docs/crc/stars.md, "Conflict
/// alerts inhibited"), before a VFR track's "*"; an uninhibited track and an ERAM block draw none.
/// </summary>
public class ScopeCaInhibitMarkTests
{
    [Fact]
    public void FromDto_CopiesIsCaInhibited()
    {
        Assert.True(AircraftModel.FromDto(Dto(caInhibited: true)).IsCaInhibited);
        Assert.False(AircraftModel.FromDto(Dto(caInhibited: false)).IsCaInhibited);
    }

    [Fact]
    public void UpdateFromDto_CopiesIsCaInhibited_BothWays()
    {
        var model = AircraftModel.FromDto(Dto(caInhibited: false));

        model.UpdateFromDto(Dto(caInhibited: true));
        Assert.True(model.IsCaInhibited);

        model.UpdateFromDto(Dto(caInhibited: false));
        Assert.False(model.IsCaInhibited);
    }

    [Fact]
    public void StarsFullBlock_OfAnInhibitedTrack_CarriesTheMarkAfterTheCallsign()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsCaInhibited = true;

        ScopeField callsign = CallsignField(ac, ScopeTemplates.StarsId);

        Assert.Equal("UAL238" + TrackPresenter.EramArrowCell, callsign.Text);
        Assert.Equal(callsign.Text, callsign.StableText);
        Assert.True(callsign.Style.HasFlag(ScopeFieldStyle.CaInhibitMark));
    }

    [Fact]
    public void StarsFullBlock_OfAnUninhibitedTrack_HasNoMark()
    {
        ScopeField callsign = CallsignField(ScopeTestAircraft.CreateModel(), ScopeTemplates.StarsId);

        Assert.Equal("UAL238", callsign.Text);
        Assert.False(callsign.Style.HasFlag(ScopeFieldStyle.CaInhibitMark));
    }

    [Fact]
    public void StarsFullBlock_OfAnInhibitedVfrTrack_LaysOutTheTriangleBeforeTheStar()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FlightRules = "VFR";
        ac.IsCaInhibited = true;
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();

        ScopeBlockLayout layout = DatablockTemplate.Layout(
            TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId)),
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint))
        );
        FieldRect callsign = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.Callsign);
        FieldRect vfr = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.VfrMark);

        Assert.Equal("UAL238" + TrackPresenter.EramArrowCell, callsign.Text);
        Assert.Equal(ScopeFieldStyle.CaInhibitMark, callsign.Style & ScopeFieldStyle.CaInhibitMark);
        Assert.Equal("*", vfr.Text);
        Assert.Equal(callsign.Rect.Top, vfr.Rect.Top);
        Assert.True(callsign.Rect.Right <= vfr.Rect.Left, $"triangle cell ends at {callsign.Rect.Right}, '*' starts at {vfr.Rect.Left}");
    }

    [Fact]
    public void EramBlock_OfAnInhibitedTrack_HasNoMark()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsCaInhibited = true;

        TrackPresentation presentation = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId));

        Assert.DoesNotContain(presentation.Fields, f => f.Style.HasFlag(ScopeFieldStyle.CaInhibitMark));
    }

    [Fact]
    public void CaInhibitPath_IsAFilledTriangleTheCapHeightTall_InTheCellAfterTheCallsign()
    {
        using var font = new SKFont { Size = 12f };
        string text = "UAL238" + TrackPresenter.EramArrowCell;
        var field = new FieldRect(
            ScopeFieldSlot.Callsign,
            new SKRect(10, 0, 80, 20),
            text,
            ScopeColorToken.Default,
            ScopeFieldStyle.CaInhibitMark,
            ScopeFieldAction.None
        );
        float cellLeft = 10 + font.MeasureText("UAL238");
        float cellRight = cellLeft + font.MeasureText(TrackPresenter.EramArrowCell);
        float capHeight = font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : font.Size * 0.7f;

        using SKPath path = ScopeRenderer.CaInhibitPath(field, font, text.Length - 1);
        SKRect bounds = path.Bounds;

        Assert.Equal(3, path.PointCount);
        Assert.InRange(bounds.Left, cellLeft, cellRight);
        Assert.InRange(bounds.Right, cellLeft, cellRight);
        Assert.Equal(20f, bounds.Bottom, 0.01f);
        Assert.Equal(capHeight, bounds.Height, 0.01f);
    }

    private static ScopeField CallsignField(AircraftModel ac, string templateId) =>
        Assert.Single(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId)).Fields, f => f.Slot == ScopeFieldSlot.Callsign);

    private static AircraftDto Dto(bool caInhibited) =>
        new()
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            Latitude = 37.72,
            Longitude = -122.22,
            Heading = 100,
            Altitude = 5000,
            GroundSpeed = 250,
            BeaconCode = 4521,
            TransponderMode = "C",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "KOAK",
            Destination = "KLAX",
            Route = "",
            FlightRules = "IFR",
            Status = "",
            IsCaInhibited = caInhibited,
        };
}
