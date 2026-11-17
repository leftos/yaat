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
[Collection(MapBlinkClockCollection.Name)]
public class ScopeEramTemplateTests
{
    [Fact]
    public void EramTemplate_Line2_IsEramAltitudeThenTheStarsRightSide()
    {
        ScopeTemplateLine line2 = ScopeTemplates.Eram.Lines[1];

        Assert.Equal(
            [
                ScopeFieldSlot.EramAltitude,
                ScopeFieldSlot.AltitudeMismatch,
                ScopeFieldSlot.GroundSpeed,
                ScopeFieldSlot.CwtType,
                ScopeFieldSlot.Atpa,
                ScopeFieldSlot.Ident,
            ],
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

    [Fact]
    public void EramTemplate_Line3_IsTheStatusThenTheHsfIndicator_Line4_IsTheNotes()
    {
        ScopeTemplateLine line3 = ScopeTemplates.Eram.Lines[2];
        ScopeTemplateLine line4 = ScopeTemplates.Eram.Lines[3];

        Assert.Equal([ScopeFieldSlot.StatusLine3, ScopeFieldSlot.HsfIndicator], line3.Slots.Select(s => s.Slot));
        Assert.True(line3.Slots[0].Reserved);
        Assert.Equal("", line3.Slots[1].Separator);
        Assert.False(line3.AlwaysShown);
        Assert.Equal(
            [
                ScopeFieldSlot.Line4FreeText,
                ScopeFieldSlot.Line4Heading,
                ScopeFieldSlot.Line4Pad,
                ScopeFieldSlot.Line4Speed,
                ScopeFieldSlot.Line4Notes,
            ],
            line4.Slots.Select(s => s.Slot)
        );
        Assert.False(line4.AlwaysShown);
        Assert.Equal(
            [ScopeFieldSlot.ModeC, ScopeFieldSlot.NoLandingClearance, ScopeFieldSlot.ConflictAlert, ScopeFieldSlot.InstructorNote],
            ScopeTemplates.Eram.Lines.Skip(4).SelectMany(l => l.Slots).Select(s => s.Slot)
        );
        Assert.DoesNotContain(ScopeTemplates.Eram.Lines.SelectMany(l => l.Slots), s => s.Slot == ScopeFieldSlot.Squawk);
    }

    [Fact]
    public void EramPresentation_EmitsNoSquawkAndNoStarsStatusText()
    {
        AircraftModel ac = ScopeTestAircraft.CreateSquawkMismatch();
        ac.OwnerSectorCode = "2S";
        ac.Scratchpad1 = "ABC";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId));

        Assert.DoesNotContain(p.Fields, f => f.Slot == ScopeFieldSlot.Squawk);
        Assert.Equal("", Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.StatusLine3).StableText);
    }

    private static readonly Action<AircraftModel> Handoff = ac => ac.HandoffPeerSectorCode = "C40";
    private static readonly Action<AircraftModel> Coast = ac => ac.EramIsCoastTrack = true;
    private static readonly Action<AircraftModel> SpcBlinking = ac =>
    {
        ac.BeaconCode = 7700;
        ac.EramIsCorrelated = true;
        ac.EramSpcBlinking = true;
    };

    private static Action<AircraftModel> RecentlyAccepted(bool forced) =>
        ac =>
        {
            ac.EramRecentHandoffFromFacilityId = "ZOA";
            ac.EramRecentHandoffFromSectorId = "44";
            ac.EramRecentHandoffWasForced = forced;
        };

    public static TheoryData<string, Action<AircraftModel>, bool, bool> BlinkRows =>
        new()
        {
            // Case, its arrangement, the off phase blanks it, in conflict.
            { "H", Handoff, true, false },
            { "CST", Coast, true, false },
            { "SPC correlated blinking", SpcBlinking, true, false },
            { "H", Handoff, false, true },
            { "CST", Coast, false, true },
            { "SPC correlated blinking", SpcBlinking, false, true },
            { "O", RecentlyAccepted(false), false, false },
            { "K", RecentlyAccepted(true), false, false },
            {
                "mismatch",
                ac =>
                {
                    ac.EramIsCorrelated = true;
                    ac.BeaconCode = 1234;
                    ac.AssignedBeaconCode = 4321;
                },
                false,
                false
            },
            {
                "NONE",
                ac =>
                {
                    ac.EramIsCorrelated = true;
                    ac.TransponderMode = "Standby";
                    ac.AssignedBeaconCode = 4321;
                },
                false,
                false
            },
            { "FRZN", ac => ac.EramIsFrozen = true, false, false },
            {
                "SPC uncorrelated",
                ac =>
                {
                    ac.BeaconCode = 7700;
                    ac.EramSpcBlinking = true;
                },
                false,
                false
            },
            {
                "SPC correlated steady",
                ac =>
                {
                    ac.BeaconCode = 7700;
                    ac.EramIsCorrelated = true;
                },
                false,
                false
            },
        };

    [Theory]
    [MemberData(nameof(BlinkRows))]
    public void EramStatus_OffPhase_BlanksOnlyABlinkingTextOutOfConflict(string status, Action<AircraftModel> arrange, bool blanks, bool inConflict)
    {
        Assert.NotEmpty(status);
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        arrange(ac);
        if (inConflict)
        {
            ac.ConflictPeerCallsign = "SWA1234";
        }
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId, flashOn: false) with
        {
            Display = ScopeTestAircraft.AllDisplay with { Viewer = Viewer },
        };

        ScopeField field = Assert.Single(TrackPresenter.Present(input).Fields, f => f.Slot == ScopeFieldSlot.StatusLine3);

        Assert.NotEqual("", field.StableText);
        Assert.Equal(blanks ? new string(' ', field.StableText.Length) : field.StableText, field.Text);
    }

    [Fact]
    public void EramStatus_OnPhase_ShowsABlinkingText()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        Handoff(ac);
        ScopeField field = Assert.Single(
            TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId, flashOn: true)).Fields,
            f => f.Slot == ScopeFieldSlot.StatusLine3
        );

        Assert.Equal("HUNK", field.Text);
        Assert.Equal("HUNK", field.StableText);
    }

    [Fact]
    public void EramLayout_NoStatusAndNoIndicator_HasNoLine3Rect()
    {
        ScopeBlockLayout layout = LayOut(ScopeTestAircraft.CreateModel());

        Assert.DoesNotContain(layout.Fields, f => f.Slot is ScopeFieldSlot.StatusLine3 or ScopeFieldSlot.HsfIndicator);
    }

    [Fact]
    public void EramLayout_IndicatorAlone_LaysLine3WithThePaddedStatusBeforeIt()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramFreeText = "WX";
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));

        ScopeBlockLayout layout = LayOut(ac, metrics);

        FieldRect status = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.StatusLine3);
        FieldRect indicator = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.HsfIndicator);
        FieldRect altitude = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.EramAltitude);
        Assert.Equal(status.Rect.Top, indicator.Rect.Top);
        Assert.True(status.Rect.Top > altitude.Rect.Top);
        Assert.Equal(status.Rect.Right, indicator.Rect.Left, 3);
        Assert.Equal(metrics.Measure("    "), indicator.Rect.Left - status.Rect.Left, 3);
        Assert.Equal(ScopeFieldStyle.HsfIndicator, indicator.Style);
    }

    [Fact]
    public void EramLayout_EmptyLine4_HasNoRect()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AircraftType = "";
        ac.FiledAircraftType = "";
        ac.Destination = "";

        Assert.DoesNotContain(LayOut(ac).Fields, f => f.Slot.IsLine4());
    }

    private static readonly SKFont Line4Font = new() { Size = 12f };

    private static ScopeTextMetrics Line4Metrics() => ScopeTextMetrics.FromStyle(new TextStyle(Line4Font, new SKPaint()));

    private static List<FieldRect> Line4Rects(AircraftModel ac) => [.. LayOut(ac, Line4Metrics()).Fields.Where(f => f.Slot.IsLine4())];

    private static AircraftModel WithLine4(string? heading, string? speed, string? freeText)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramAssignedHeading = heading;
        ac.EramAssignedSpeed = speed;
        ac.EramFreeText = freeText;
        return ac;
    }

    [Fact]
    public void EramLayout_HeadingAndSpeed_AreTwoAdjacentTokens_DrawingTheLine4Text()
    {
        AircraftModel ac = WithLine4("H270", "S250", null);
        ScopeTextMetrics metrics = Line4Metrics();

        List<FieldRect> line4 = Line4Rects(ac);

        Assert.Equal([ScopeFieldSlot.Line4Heading, ScopeFieldSlot.Line4Speed], line4.Select(f => f.Slot));
        Assert.Equal(["H270", "S250"], line4.Select(f => f.Text));
        Assert.Equal(0f, line4[0].Rect.Left);
        Assert.Equal(metrics.Measure("H270 "), line4[1].Rect.Left, 3);
        Assert.True(line4[0].Rect.Right <= line4[1].Rect.Left);
        Assert.Equal(EramLine4.Text(ac, true, true, false), $"{line4[0].Text} {line4[1].Text}");
    }

    [Fact]
    public void EramLayout_ShortHeadingBeforeASpeed_SpeedFollowsThePaddedHeadingRun()
    {
        AircraftModel ac = WithLine4("20L", "S250", null);

        List<FieldRect> line4 = Line4Rects(ac);

        Assert.Equal(["20L ", "S250"], line4.Select(f => f.Text));
        Assert.Equal(Line4Metrics().Measure("20L " + " "), line4[1].Rect.Left, 3);
        Assert.Equal(EramLine4.Text(ac, true, true, false), $"{line4[0].Text} {line4[1].Text}");
    }

    [Fact]
    public void EramLayout_SpeedAlone_SpeedFollowsTheInertPadRun()
    {
        AircraftModel ac = WithLine4(null, "S250", null);

        List<FieldRect> line4 = Line4Rects(ac);

        FieldRect pad = Assert.Single(line4, f => f.Slot == ScopeFieldSlot.Line4Pad);
        FieldRect speed = Assert.Single(line4, f => f.Slot == ScopeFieldSlot.Line4Speed);
        Assert.Equal("    ", pad.Text);
        Assert.Equal(ScopeFieldAction.None, pad.Action);
        Assert.Equal(ScopeFieldAction.OpenEramSpeedMenu, speed.Action);
        Assert.Equal(0f, pad.Rect.Left);
        Assert.Equal(Line4Metrics().Measure("    " + " "), speed.Rect.Left, 3);
        Assert.Equal(EramLine4.Text(ac, true, true, false), $"{pad.Text} {speed.Text}");
    }

    [Fact]
    public void EramLayout_HeadingAlone_IsOneHeadingToken()
    {
        FieldRect heading = Assert.Single(Line4Rects(WithLine4("H270", null, null)));

        Assert.Equal(ScopeFieldSlot.Line4Heading, heading.Slot);
        Assert.Equal("H270", heading.Text);
        Assert.Equal(ScopeFieldAction.OpenEramHeadingMenu, heading.Action);
    }

    [Fact]
    public void EramLayout_FreeText_IsOneFreeTextToken_WhateverElseIsSet()
    {
        FieldRect text = Assert.Single(Line4Rects(WithLine4("H270", "S250", "LOWALT")));

        Assert.Equal(ScopeFieldSlot.Line4FreeText, text.Slot);
        Assert.Equal("LOWALT", text.Text);
        Assert.Equal(ScopeFieldAction.OpenEramFreeTextMenu, text.Action);
    }

    [Fact]
    public void EramLayout_NoHsf_IsOneFallbackRect()
    {
        FieldRect fallback = Assert.Single(Line4Rects(WithLine4(null, null, null)));

        Assert.Equal(ScopeFieldSlot.Line4Notes, fallback.Slot);
        Assert.Equal(ScopeFieldAction.OpenEramHsfMenu, fallback.Action);
    }

    [Fact]
    public void EramLayout_SuppressedForTheViewer_IsOneFallbackRect()
    {
        AircraftModel ac = WithLine4("H270", "S250", null);
        ac.EramFourthLineSuppressedSectors = [new Yaat.Sim.EramSectorKey("ZOA", "44")];
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId) with
        {
            Display = ScopeTestAircraft.AllDisplay with { Viewer = Viewer },
        };

        FieldRect fallback = Assert.Single(DatablockTemplate.Layout(TrackPresenter.Present(input), Line4Metrics()).Fields, f => f.Slot.IsLine4());

        Assert.Equal(ScopeFieldSlot.Line4Notes, fallback.Slot);
        Assert.Equal(ScopeFieldAction.OpenEramHsfMenu, fallback.Action);
    }

    [Fact]
    public void EramLayout_Line4_SitsBelowTheStatusLine()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramIsFrozen = true;
        ac.EramAssignedHeading = "270";

        ScopeBlockLayout layout = LayOut(ac);

        FieldRect status = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.StatusLine3);
        FieldRect line4 = Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.Line4Heading);
        Assert.Equal("270", line4.Text);
        Assert.True(line4.Rect.Top > status.Rect.Top);
        Assert.Equal(0f, line4.Rect.Left);
    }

    private static readonly ScopeViewer Viewer = new(new Yaat.Sim.ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static ScopeBlockLayout LayOut(AircraftModel ac)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        return LayOut(ac, ScopeTextMetrics.FromStyle(new TextStyle(font, paint)));
    }

    private static ScopeBlockLayout LayOut(AircraftModel ac, ScopeTextMetrics metrics) =>
        DatablockTemplate.Layout(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId)), metrics);
}
