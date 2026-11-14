using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
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
    public static TheoryData<bool, float> TemplatesBySize
    {
        get
        {
            var data = new TheoryData<bool, float>();
            foreach (bool euroScope in new[] { false, true })
            {
                foreach (float size in new[] { 9f, 12f, 14f, 18f })
                {
                    data.Add(euroScope, size);
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

    private static ScopeFrame Build(bool euroScope, float size, params AircraftModel[] aircraft)
    {
        using var font = new SKFont { Size = size };
        using var paint = new SKPaint();
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(font, paint));
        var input = new ScopeFrameInput(
            aircraft,
            null,
            Viewport(),
            euroScope,
            metrics,
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay,
            new Dictionary<string, SKPoint>()
        );
        return ScopeFrame.Build(input);
    }

    private static FieldRect FieldOf(ScopeFrame frame, ScopeFieldSlot slot) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == slot);

    private static (float X, float Y) Centre(FieldRect field) => (field.Rect.MidX, field.Rect.MidY);

    [Theory]
    [MemberData(nameof(TemplatesBySize))]
    public void HitTest_EveryDrawnField_ReturnsItsDrawnRect(bool euroScope, float fontSize)
    {
        AircraftModel busy = Busy();
        AircraftModel other = ScopeTestAircraft.CreateModel();
        other.Callsign = "DAL55";
        other.Position = new LatLon(37.05, -121.95);

        ScopeFrame frame;
        using (RadarBlinkPin.Phase(true))
        {
            frame = Build(euroScope, fontSize, busy, other);
        }

        Assert.Equal(2, frame.Tracks.Count);
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
            on = Build(false, 12f, ac);
        }
        using (RadarBlinkPin.Phase(false))
        {
            off = Build(false, 12f, ac);
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
            on = Build(true, 12f, ac);
        }
        using (RadarBlinkPin.Phase(false))
        {
            off = Build(true, 12f, ac);
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
    [InlineData(false, "stars")]
    [InlineData(true, "euroscope")]
    public void Snapshot_TemplateFollowsEuroScopeMode(bool euroScopeMode, string templateId)
    {
        ScopeFrame frame = Build(euroScopeMode, 12f, ScopeTestAircraft.CreateModel());

        Assert.Equal(templateId, frame.TemplateId);
        Assert.Equal(templateId, ScopeFrame.TemplateFor(euroScopeMode));
    }
}
