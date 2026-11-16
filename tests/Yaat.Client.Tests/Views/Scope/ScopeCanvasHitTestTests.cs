using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The scope frame lays each block out once and both drawing and hit-testing read that one layout: every field the
/// frame carries hit-tests back to itself. A flashing field keeps its reserved rect; the STARS template hit-tests it in
/// both blink phases, the EuroScope template only in the on phase, as today's EuroScope tag does.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeCanvasHitTestTests
{
    public static TheoryData<string, float> TemplatesBySize
    {
        get
        {
            var data = new TheoryData<string, float>();
            foreach (string templateId in new[] { ScopeTemplates.StarsId, ScopeTemplates.EramId, ScopeTemplates.EuroScopeId, ScopeTemplates.MixedId })
            {
                foreach (float size in new[] { 9f, 12f, 14f, 18f })
                {
                    data.Add(templateId, size);
                }
            }
            return data;
        }
    }

    private static MapViewport Viewport()
    {
        var vp = new MapViewport
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };
        return vp;
    }

    /// <summary>A track carrying every flashing field, owned with scratchpads, runway and assignments.</summary>
    private static AircraftModel Busy()
    {
        AircraftModel ac = ScopeTestAircraft.CreateEverythingFlashing();
        ac.AssignedTo = "AB";
        ac.Scratchpad1 = "ABC";
        ac.Scratchpad2 = "XY";
        ac.AssignedRunway = "28R";
        ac.Destination = "KSFO";
        ac.AssignedAltitude = 11000;
        ac.AssignedSpeed = 250;
        ac.TransponderMode = "Standby";
        return ac;
    }

    /// <summary>A track with a flashing handoff and nothing else flashing.</summary>
    private static AircraftModel HandingOff()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.HandoffPeerSectorCode = "3E";
        return ac;
    }

    private static ScopeFrame Build(string templateId, float size, params AircraftModel[] aircraft) => Build(templateId, size, null, aircraft);

    private static ScopeFrame Build(string templateId, float size, string? hoveredCallsign, params AircraftModel[] aircraft)
    {
        using var font = new SKFont { Size = size };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(),
            templateId,
            metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            hoveredCallsign
        );
        return ScopeFrame.Build(input);
    }

    private static FieldRect FieldOf(ScopeFrame frame, ScopeFieldSlot slot) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == slot);

    private static (float X, float Y) Centre(FieldRect field) => (field.Rect.MidX, field.Rect.MidY);

    [Theory]
    [MemberData(nameof(TemplatesBySize))]
    public void HitTest_EveryDrawnField_ReturnsItsDrawnRect(string templateId, float fontSize)
    {
        AircraftModel busy = Busy();
        AircraftModel other = ScopeTestAircraft.CreateModel();
        other.Callsign = "DAL55";
        other.Position = new LatLon(37.05, -121.95);
        foreach (AircraftModel ac in new[] { busy, other })
        {
            ac.EramPlanAltitudeFeet = 24000;
            ac.CruiseAltitude = 35000;
        }

        ScopeFrame frame;
        using (RadarBlinkPin.Phase(true))
        {
            frame = Build(templateId, fontSize, other.Callsign, busy, other);
        }

        Assert.Equal(2, frame.Tracks.Count);
        // The mark draws on ERAM blocks (Mixed draws the unowned DAL55 as ERAM); the dwell line on the hovered STARS or ERAM block.
        if (templateId is ScopeTemplates.EramId or ScopeTemplates.MixedId)
        {
            Assert.Contains(frame.Tracks, t => t.Block.Fields.Any(f => f.Slot == ScopeFieldSlot.AltitudeMismatch));
        }
        if (templateId != ScopeTemplates.EuroScopeId)
        {
            Assert.Contains(frame.Tracks, t => t.Block.Fields.Any(f => f.Slot == ScopeFieldSlot.OtherAltitude));
        }
        foreach (ScopeTrack track in frame.Tracks)
        {
            Assert.NotEmpty(track.Block.Fields);
            foreach (FieldRect field in track.Block.Fields)
            {
                (float x, float y) = Centre(field);
                ScopeFieldHit? hit = frame.FieldAt(x, y);
                Assert.NotNull(hit);
                Assert.Same(track.Aircraft, hit!.Aircraft);
                Assert.Equal(field, hit.Field);
                Assert.Same(track.Aircraft, frame.BlockAt(x, y));
            }
        }
    }

    [Fact]
    public void HitTest_Stars_ReservedFlashingField_HitsInBothPhases()
    {
        AircraftModel ac = HandingOff();
        ScopeFrame on;
        ScopeFrame off;
        using (RadarBlinkPin.Phase(true))
        {
            on = Build(ScopeTemplates.StarsId, 12f, ac);
        }
        using (RadarBlinkPin.Phase(false))
        {
            off = Build(ScopeTemplates.StarsId, 12f, ac);
        }

        FieldRect onField = FieldOf(on, ScopeFieldSlot.StatusLine3);
        FieldRect offField = FieldOf(off, ScopeFieldSlot.StatusLine3);
        Assert.NotEqual(onField.Text, offField.Text);
        Assert.Equal(onField.Rect, offField.Rect);

        (float x, float y) = Centre(offField);
        Assert.Equal(ScopeFieldSlot.StatusLine3, on.FieldAt(x, y)?.Field.Slot);
        Assert.Equal(offField, off.FieldAt(x, y)?.Field);
    }

    [Fact]
    public void HitTest_EuroScope_FlashingField_HitsOnlyOnPhase()
    {
        AircraftModel ac = HandingOff();
        ScopeFrame on;
        ScopeFrame off;
        using (RadarBlinkPin.Phase(true))
        {
            on = Build(ScopeTemplates.EuroScopeId, 12f, ac);
        }
        using (RadarBlinkPin.Phase(false))
        {
            off = Build(ScopeTemplates.EuroScopeId, 12f, ac);
        }

        FieldRect onField = FieldOf(on, ScopeFieldSlot.Handoff);
        FieldRect offField = FieldOf(off, ScopeFieldSlot.Handoff);
        Assert.NotEqual("", onField.Text);
        Assert.Equal("", offField.Text);
        Assert.Equal(onField.Rect, offField.Rect);

        (float x, float y) = Centre(onField);
        Assert.Equal(onField, on.FieldAt(x, y)?.Field);
        Assert.Null(off.FieldAt(x, y));
    }

    [Theory]
    [InlineData("stars")]
    [InlineData("eram")]
    [InlineData("euroscope")]
    public void Snapshot_TemplateIsTheInputsTemplate(string templateId)
    {
        ScopeFrame frame = Build(templateId, 12f, ScopeTestAircraft.CreateModel());

        Assert.Equal(templateId, frame.TemplateId);
    }

    /// <summary>An ERAM block with the arrow drawn in line 2 hit-tests every field, the altitude among them, to its drawn rect.</summary>
    [Fact]
    public void HitTest_Eram_ClimbingAltitudeField_ReturnsItsDrawnRect()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.EramPlanAltitudeFeet = 30000;
        ScopeFrame frame = Build(ScopeTemplates.EramId, 12f, ac);

        FieldRect altitude = FieldOf(frame, ScopeFieldSlot.EramAltitude);
        Assert.Equal("300" + TrackPresenter.EramArrowCell + "230", altitude.Text);
        Assert.Equal(ScopeFieldStyle.ArrowUp, altitude.Style);
        (float x, float y) = Centre(altitude);
        Assert.Equal(altitude, frame.FieldAt(x, y)?.Field);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HitTest_Eram_HsfIndicatorAndBlinkingStatus_HitInBothPhases(bool flashOn)
    {
        AircraftModel ac = HandingOff();
        ac.EramAssignedHeading = "270";
        ScopeFrame frame;
        using (RadarBlinkPin.Phase(flashOn))
        {
            frame = Build(ScopeTemplates.EramId, 12f, ac);
        }

        FieldRect indicator = FieldOf(frame, ScopeFieldSlot.HsfIndicator);
        FieldRect status = FieldOf(frame, ScopeFieldSlot.StatusLine3);
        (float ix, float iy) = Centre(indicator);
        (float sx, float sy) = Centre(status);

        Assert.Equal(ScopeFieldSlot.HsfIndicator, frame.FieldAt(ix, iy)?.Field.Slot);
        Assert.Equal(ScopeFieldSlot.StatusLine3, frame.FieldAt(sx, sy)?.Field.Slot);
    }

    private static ScopeFrame BuildViewedFrom(ScopeViewer viewer, AircraftModel aircraft)
    {
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [aircraft],
            null,
            Viewport(),
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
            OthersLevel: ScopeProfile.OthersFull,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>()
        );
        return ScopeFrame.Build(input);
    }

    [Fact]
    public void HitTest_Eram_ColumnZeroMarks_ReturnTheirRects()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C45";
        ac.EramOnFrequencySectorIds = ["44"];
        ScopeFrame frame;
        using (RadarBlinkPin.Phase(true))
        {
            frame = BuildViewedFrom(new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44"), ac);
        }

        foreach (ScopeFieldSlot slot in new[] { ScopeFieldSlot.Vci, ScopeFieldSlot.NotYourControl })
        {
            FieldRect mark = FieldOf(frame, slot);
            (float x, float y) = Centre(mark);
            Assert.Equal(mark, frame.FieldAt(x, y)?.Field);
            Assert.Same(ac, frame.BlockAt(x, y));
        }
    }
}
