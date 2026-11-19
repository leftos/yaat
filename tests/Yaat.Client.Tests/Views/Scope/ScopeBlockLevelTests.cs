using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The four block levels: ERAM Limited is CRC's paired LDB and Partial its enhanced paired LDB, STARS Limited and Partial are
/// the collapsed blocks <see cref="DatablockText.BuildCollapsedLines"/> builds, EuroScope draws the Full tag at every level,
/// and Dwell shows the Full block's content.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeBlockLevelTests
{
    private static readonly ScopeFieldSlot[] AlertSlots =
    [
        ScopeFieldSlot.Squawk,
        ScopeFieldSlot.ModeC,
        ScopeFieldSlot.NoLandingClearance,
        ScopeFieldSlot.ConflictAlert,
        ScopeFieldSlot.InstructorNote,
    ];

    // One measuring style for the class, never disposed: a static the test process owns.
    private static readonly TextStyle Style12 = new(new SKFont { Size = 12f }, new SKPaint());

    private static TextStyle Style() => Style12;

    private static TrackPresentation Present(AircraftModel ac, string templateId, ScopeBlockLevel level) =>
        TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId) with { Level = level });

    private static ScopeBlockLayout Layout(TrackPresentation presentation, TextStyle style) =>
        DatablockTemplate.Layout(presentation, ScopeTextMetrics.FromStyle(style));

    /// <summary>Each row's drawn texts, top to bottom, the fields joined with their separators on <paramref name="lines"/>.</summary>
    private static List<string> RowTexts(ScopeBlockLayout layout, IReadOnlyList<ScopeTemplateLine> lines)
    {
        var separators = lines.SelectMany(l => l.Slots).ToDictionary(p => p.Slot, p => p.Separator);
        var rows = new List<string>();
        foreach (IGrouping<float, FieldRect> row in layout.Fields.GroupBy(f => f.Rect.Top))
        {
            string text = "";
            foreach (FieldRect field in row.Where(f => f.Text.Length > 0))
            {
                text += text.Length > 0 ? separators[field.Slot] + field.Text : field.Text;
            }
            rows.Add(text);
        }
        return rows;
    }

    private static AircraftModel EramAircraft(Action<AircraftModel> setup)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";
        ac.Altitude = 12000;
        ac.EramPlanAltitudeFeet = 23000;
        ac.Note = "Watch wake";
        ac.NoLandingClearanceWarningActive = true;
        setup(ac);
        return ac;
    }

    [Fact]
    public void EramLimited_NoCera_IsTheIdThenTheModeCAltitude_WithNoLeaderColumn0OrAlerts()
    {
        TrackPresentation p = Present(EramAircraft(_ => { }), ScopeTemplates.EramId, ScopeBlockLevel.Limited);
        ScopeBlockLayout layout = Layout(p, Style());

        Assert.Equal(ScopeBlockLevel.Limited, p.Level);
        Assert.Equal(["UAL238", "120"], RowTexts(layout, ScopeTemplates.Eram.LimitedLines!));
        Assert.False(p.ShowsLeader);
        Assert.DoesNotContain(layout.Fields, f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl);
        Assert.DoesNotContain(p.Fields, f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl);
        Assert.DoesNotContain(layout.Fields, f => AlertSlots.Contains(f.Slot));
        Assert.Equal(layout.Bounds, layout.TextBounds);
    }

    [Fact]
    public void EramLimited_Cera_IsCrcsPaddedCeraForm()
    {
        TrackPresentation p = Present(EramAircraft(ac => ac.EramControllerEnteredAltitude = 90), ScopeTemplates.EramId, ScopeBlockLevel.Limited);

        Assert.Equal(["UAL238", "090#"], RowTexts(Layout(p, Style()), ScopeTemplates.Eram.LimitedLines!));
    }

    [Fact]
    public void EramLimited_NoModeC_HasNoAltitudeRow()
    {
        TrackPresentation p = Present(EramAircraft(ac => ac.TransponderMode = "Standby"), ScopeTemplates.EramId, ScopeBlockLevel.Limited);

        Assert.Equal(["UAL238"], RowTexts(Layout(p, Style()), ScopeTemplates.Eram.LimitedLines!));
    }

    [Fact]
    public void EramLimited_Squawk7700_Line3IsTheSpecialCodeText()
    {
        TrackPresentation p = Present(EramAircraft(ac => ac.BeaconCode = 7700), ScopeTemplates.EramId, ScopeBlockLevel.Limited);

        Assert.Equal(["UAL238", "120", "EMRG"], RowTexts(Layout(p, Style()), ScopeTemplates.Eram.LimitedLines!));
    }

    [Theory]
    [InlineData(ScopeBlockLevel.Limited)]
    [InlineData(ScopeBlockLevel.Partial)]
    public void EramReduced_SpecialCode_BlinksTheWholeBlock_KeepingItsRects(ScopeBlockLevel level)
    {
        AircraftModel ac = EramAircraft(a => a.BeaconCode = 7700);
        TextStyle style = Style();
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId) with { Level = level };

        ScopeBlockLayout on = Layout(TrackPresenter.Present(input with { FlashOn = true }), style);
        ScopeBlockLayout off = Layout(TrackPresenter.Present(input with { FlashOn = false }), style);

        IReadOnlyList<ScopeTemplateLine> lines =
            level == ScopeBlockLevel.Limited ? ScopeTemplates.Eram.LimitedLines! : ScopeTemplates.Eram.PartialLines!;
        Assert.Equal("EMRG", RowTexts(on, lines)[^1]);
        Assert.All(off.Fields, f => Assert.Equal("", f.Text));
        Assert.Equal(on.Fields.Select(f => (f.Slot, f.Rect)), off.Fields.Select(f => (f.Slot, f.Rect)));
        Assert.Equal(on.Bounds, off.Bounds);
    }

    [Fact]
    public void EramReduced_NoSpecialCode_DoesNotBlink()
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(EramAircraft(_ => { }), ScopeTemplates.EramId, flashOn: false) with
        {
            Level = ScopeBlockLevel.Limited,
        };

        Assert.Equal(["UAL238", "120"], RowTexts(Layout(TrackPresenter.Present(input), Style()), ScopeTemplates.Eram.LimitedLines!));
    }

    [Theory]
    [InlineData(0u, null)]
    [InlineData(7700u, "EMRG")]
    public void EramPartial_IsTheIdThenFieldsBAndCThenLine3AsLimited(uint beacon, string? line3)
    {
        AircraftModel ac = EramAircraft(a => a.BeaconCode = beacon);

        TrackPresentation p = Present(ac, ScopeTemplates.EramId, ScopeBlockLevel.Partial);
        ScopeBlockLayout layout = Layout(p, Style());

        // Climbing from 120 to 230: Field B 230, Field C 120, with no character 4 between them.
        List<string> expected = line3 is null ? ["UAL238", "230120"] : ["UAL238", "230120", line3];
        Assert.Equal(expected, RowTexts(layout, ScopeTemplates.Eram.PartialLines!));
        Assert.False(p.ShowsLeader);
        Assert.DoesNotContain(p.Fields, f => f.Slot is ScopeFieldSlot.Vci or ScopeFieldSlot.NotYourControl);
        Assert.DoesNotContain(layout.Fields, f => AlertSlots.Contains(f.Slot));
    }

    [Theory]
    [InlineData(null, "230 120", "230120")]
    [InlineData(150, "150T120", "150120")]
    public void EramPartial_Line2_DropsTheFullBlocksCharacter4(int? interim, string fullText, string partialText)
    {
        AircraftModel ac = EramAircraft(a => a.EramInterimAltitude = interim);

        ScopeField full = Assert.Single(Present(ac, ScopeTemplates.EramId, ScopeBlockLevel.Full).Fields, f => f.Slot == ScopeFieldSlot.EramAltitude);
        ScopeField partial = Assert.Single(
            Present(ac, ScopeTemplates.EramId, ScopeBlockLevel.Partial).Fields,
            f => f.Slot == ScopeFieldSlot.EramAltitude
        );

        Assert.Equal(fullText, full.Text);
        Assert.Equal(partialText, partial.Text);
        Assert.Equal(partialText, partial.StableText);
        Assert.Equal(ScopeFieldStyle.None, partial.Style);
    }

    public static TheoryData<string> StarsCollapsedCases =>
        ["LimitedBeacon", "LimitedNoBeacon", "LimitedIdent", "PartialSp1", "PartialNoSp1", "PartialHandoff"];

    private static (AircraftModel Ac, ScopeBlockLevel Level) StarsCollapsed(string name)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";
        ac.Note = "Watch wake";
        switch (name)
        {
            case "LimitedBeacon":
                ac.BeaconCode = 4521;
                break;
            case "LimitedNoBeacon":
                ac.BeaconCode = 0;
                break;
            case "LimitedIdent":
                ac.BeaconCode = 301;
                ac.IsIdenting = true;
                break;
            case "PartialSp1":
                ac.Scratchpad1 = "ABC";
                break;
            case "PartialHandoff":
                ac.OwnerSectorCode = "2S";
                ac.HandoffPeerSectorCode = "3E";
                ac.IsIdenting = true;
                break;
        }
        ScopeBlockLevel level = name.StartsWith("Partial", StringComparison.Ordinal) ? ScopeBlockLevel.Partial : ScopeBlockLevel.Limited;
        ac.StudentDatablockLevel = level == ScopeBlockLevel.Partial ? StarsDatablockLevel.Partial : StarsDatablockLevel.Limited;
        return (ac, level);
    }

    [Theory]
    [MemberData(nameof(StarsCollapsedCases))]
    public void StarsReducedBlock_MatchesTheCollapsedLines(string caseName)
    {
        (AircraftModel ac, ScopeBlockLevel level) = StarsCollapsed(caseName);
        TextStyle style = Style();
        IReadOnlyList<string> today = DatablockText.BuildCollapsedLines(ac);

        TrackPresentation p = Present(ac, ScopeTemplates.StarsId, level);
        ScopeBlockLayout layout = Layout(p, style);
        IReadOnlyList<ScopeTemplateLine> lines =
            level == ScopeBlockLevel.Partial ? ScopeTemplates.Stars.PartialLines! : ScopeTemplates.Stars.LimitedLines!;
        List<string> rows = RowTexts(layout, lines);

        Assert.Equal(today, rows);
        Assert.True(p.ShowsLeader);
        Assert.DoesNotContain(layout.Fields, f => f.Slot is ScopeFieldSlot.Callsign or ScopeFieldSlot.StudentLevelMark);
        // The STARS Limited line places the beacon code in the squawk slot; no alert line shows.
        Assert.DoesNotContain(layout.Fields, f => AlertSlots.Skip(1).Contains(f.Slot));
        var byRow = layout.Fields.GroupBy(f => f.Rect.Top).ToList();
        for (int i = 0; i < today.Count; i++)
        {
            Assert.Equal(style.Measure(today[i]), byRow[i].Max(f => f.Rect.Right), precision: 3);
        }
    }

    [Theory]
    [InlineData(ScopeBlockLevel.Limited)]
    [InlineData(ScopeBlockLevel.Partial)]
    public void EuroScope_ReducedLevel_DrawsTheFullTag(ScopeBlockLevel level)
    {
        AircraftModel ac = ScopeTestAircraft.CreateEverythingFlashing();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;
        TextStyle style = Style();

        ScopeBlockLayout full = Layout(Present(ac, ScopeTemplates.EuroScopeId, ScopeBlockLevel.Full), style);
        TrackPresentation reduced = Present(ac, ScopeTemplates.EuroScopeId, level);
        ScopeBlockLayout layout = Layout(reduced, style);

        Assert.Equal(full.Fields, layout.Fields);
        Assert.Equal(full.Bounds, layout.Bounds);
        Assert.Equal(full.TextBounds, layout.TextBounds);
        Assert.Equal(level, reduced.Level);
    }

    [Fact]
    public void StudentLevelMark_ShowsOnAFullBlock_NotOnALimitedOne()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.StudentDatablockLevel = StarsDatablockLevel.Limited;

        TrackPresentation full = Present(ac, ScopeTemplates.StarsId, ScopeBlockLevel.Full);
        TrackPresentation limited = Present(ac, ScopeTemplates.StarsId, ScopeBlockLevel.Limited);
        TrackPresentation eramLimited = Present(ac, ScopeTemplates.EramId, ScopeBlockLevel.Limited);

        Assert.Equal(" (LDB)", Assert.Single(full.Fields, f => f.Slot == ScopeFieldSlot.StudentLevelMark).Text);
        Assert.DoesNotContain(limited.Fields, f => f.Slot == ScopeFieldSlot.StudentLevelMark);
        Assert.DoesNotContain(eramLimited.Fields, f => f.Slot == ScopeFieldSlot.StudentLevelMark);
    }

    public static TheoryData<string> TemplateIds => [ScopeTemplates.StarsId, ScopeTemplates.EramId, ScopeTemplates.EuroScopeId];

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Dwell_ShowsTheFullBlocksFields(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateEverythingFlashing();
        ac.StudentDatablockLevel = StarsDatablockLevel.Partial;
        TextStyle style = Style();

        TrackPresentation full = Present(ac, templateId, ScopeBlockLevel.Full);
        TrackPresentation dwell = Present(ac, templateId, ScopeBlockLevel.Dwell);

        Assert.Equal(ScopeBlockLevel.Dwell, dwell.Level);
        Assert.Equal(full.Fields, dwell.Fields);
        Assert.Equal(full.ShowsLeader, dwell.ShowsLeader);
        Assert.Equal(Layout(full, style).Fields, Layout(dwell, style).Fields);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Frame_WithNoHover_PresentsEveryTrackAtFull(string templateId)
    {
        AircraftModel other = ScopeTestAircraft.CreateModel();
        other.Callsign = "DAL55";
        using var font = new SKFont { Size = 12f };
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [ScopeTestAircraft.CreateModel(), other],
            null,
            new MapViewport
            {
                PixelWidth = 1000,
                PixelHeight = 800,
                CenterLat = 37.0,
                CenterLon = -122.0,
            },
            templateId,
            ScopeTextMetrics.FromStyle(new TextStyle(font, paint)),
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
            IntentPaths: new ScopeIntentPathCache()
        );

        var frame = ScopeFrame.Build(input);

        Assert.Equal(2, frame.Tracks.Count);
        Assert.All(frame.Tracks, t => Assert.Equal(ScopeBlockLevel.Full, t.Level));
        Assert.All(frame.Tracks, t => Assert.True(t.ShowsLeader));
    }
}
