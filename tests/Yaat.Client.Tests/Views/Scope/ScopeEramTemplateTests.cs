using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The ERAM template: line 1 as STARS, line 2 the ERAM altitude then the STARS right side, and the ERAM altitude field as
/// <see cref="TrackPresenter.EramAltitudeText"/> formats it from the aircraft, in the block colour.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeEramTemplateTests
{
    [Fact]
    public void EramTemplate_Line2_IsEramAltitudeThenTheStarsRightSide()
    {
        ScopeTemplateLine line2 = ScopeTemplates.Eram.Lines[1];

        Assert.Equal(
            [ScopeFieldSlot.EramAltitude, ScopeFieldSlot.GroundSpeed, ScopeFieldSlot.CwtType, ScopeFieldSlot.Atpa, ScopeFieldSlot.Ident],
            line2.Slots.Select(s => s.Slot)
        );
        Assert.Equal(ScopeGapMode.TextSeparator, ScopeTemplates.Eram.GapMode);
        Assert.Same(ScopeTemplates.Eram, ScopeTemplates.Get(ScopeTemplates.EramId));
        Assert.Equal(ScopeTemplates.Stars.Lines[0].Slots.Select(s => s.Slot), ScopeTemplates.Eram.Lines[0].Slots.Select(s => s.Slot));
    }

    [Fact]
    public void EramTemplate_PlacesNoStarsAltitude() =>
        Assert.DoesNotContain(ScopeTemplates.Eram.Lines.SelectMany(l => l.Slots), s => s.Slot == ScopeFieldSlot.Altitude);

    [Fact]
    public void EramPresentation_AltitudeFieldIsTheFormattedLine_InTheBlockColour()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 23000;
        ac.EramInterimAltitude = 150;
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId) with { Mva = MvaRelation.Below };

        TrackPresentation p = TrackPresenter.Present(input);

        ScopeField altitude = Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.EramAltitude);
        Assert.Equal("150T230", altitude.Text);
        Assert.Equal(altitude.Text, altitude.StableText);
        Assert.Equal(ScopeColorToken.Default, altitude.Token);
        Assert.DoesNotContain(p.Fields, f => f.Slot == ScopeFieldSlot.Altitude);
        Assert.Equal("25", Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.GroundSpeed).Text);
    }

    [Fact]
    public void EramPresentation_ModeCLostAfterReporting_ShowsXxx()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 23000;
        ac.TransponderMode = "Standby";
        ac.TransponderHasReportedModeC = true;

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId));

        Assert.Equal("230XXXX", Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.EramAltitude).Text);
    }

    [Fact]
    public void EramLayout_Line2_StartsWithTheAltitudeAndKeepsTheArrowStyle()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 23000;
        ac.Altitude = 25300;
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));

        ScopeBlockLayout layout = DatablockTemplate.Layout(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId)), metrics);

        FieldRect altitude = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.EramAltitude);
        FieldRect speed = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.GroundSpeed);
        Assert.Equal(ScopeFieldStyle.ArrowDown, altitude.Style);
        Assert.Equal(0f, altitude.Rect.Left);
        Assert.Equal(altitude.Rect.Top, speed.Rect.Top);
        Assert.True(speed.Rect.Left > altitude.Rect.Right);
        Assert.True(layout.Bounds.Contains(altitude.Rect.MidX, altitude.Rect.MidY));
    }
}
