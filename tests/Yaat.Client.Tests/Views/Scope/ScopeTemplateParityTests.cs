using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Acceptance for the step-1 templates: the STARS template's Full block reads the same lines and bounds as today's
/// <see cref="RadarDatablockLayout.Compute"/>, and the EuroScope template produces the same drawn fields, texts,
/// relative rects and bounds as today's <see cref="EuroScopeTagLayout.Layout"/>. Today's layouts read the flash
/// phase from <see cref="RadarBlink.Clock"/>, which each row pins to the phase it passes the presenter.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeTemplateParityTests
{
    private const float OriginX = 137f;
    private const float OriginY = -52f;

    private sealed record Case(AircraftModel Ac, AircraftModel? Lead, AircraftModel? Peer);

    private static readonly Dictionary<string, Func<Case>> Cases = new()
    {
        ["Vfr"] = () => Plain(ac => ac.FlightRules = "VFR"),
        ["NoCwtIdenting"] = () =>
            Plain(ac =>
            {
                ac.CwtCode = "";
                ac.IsIdenting = true;
            }),
        ["AtpaLead"] = () => new Case(ScopeTestAircraft.CreateAtpaTrailer(), ScopeTestAircraft.CreateAtpaLead(), null),
        ["LongCallsign"] = () => Plain(ac => ac.Callsign = "N1234567890AB"),
        ["OwnedWithScratchpads"] = () => Plain(OwnedWithScratchpads),
        ["SquawkMismatchWithNote"] = () =>
            Plain(ac =>
            {
                ac.TransponderMode = "C";
                ac.BeaconCode = 1200;
                ac.AssignedBeaconCode = 301;
                ac.Note = "Watch wake";
            }),
        ["StandbyModeC"] = () => Plain(ac => ac.TransponderMode = "Standby"),
        ["Handoff"] = () =>
            Plain(ac =>
            {
                ac.OwnerSectorCode = "2S";
                ac.HandoffPeerSectorCode = "3E";
            }),
        ["NoLandingClearance"] = () => Plain(ac => ac.NoLandingClearanceWarningActive = true),
        ["ConflictWithPeer"] = () =>
            new Case(
                With(ac =>
                {
                    ac.Owner = "AB";
                    ac.ConflictPeerCallsign = "SWA1234";
                }),
                null,
                ScopeTestAircraft.CreateConflictPeer()
            ),
    };

    public static TheoryData<string, bool> CasesByPhase
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string name in Cases.Keys)
            {
                data.Add(name, true);
                data.Add(name, false);
            }
            return data;
        }
    }

    public static TheoryData<string, float, bool> CasesBySizeAndPhase
    {
        get
        {
            var data = new TheoryData<string, float, bool>();
            foreach (string name in Cases.Keys)
            {
                foreach (float size in new[] { 9f, 12f, 14f })
                {
                    data.Add(name, size, true);
                    data.Add(name, size, false);
                }
            }
            return data;
        }
    }

    private static readonly Dictionary<ScopeFieldSlot, TagFieldId> EuroScopeIds = new()
    {
        [ScopeFieldSlot.Owner] = TagFieldId.Owner,
        [ScopeFieldSlot.Callsign] = TagFieldId.Callsign,
        [ScopeFieldSlot.TypeCwt] = TagFieldId.TypeCwt,
        [ScopeFieldSlot.Destination] = TagFieldId.Destination,
        [ScopeFieldSlot.Ident] = TagFieldId.Ident,
        [ScopeFieldSlot.Altitude] = TagFieldId.CurrentAltitude,
        [ScopeFieldSlot.AssignedAltitude] = TagFieldId.AssignedAltitude,
        [ScopeFieldSlot.AssignedSpeed] = TagFieldId.AssignedSpeed,
        [ScopeFieldSlot.AssignedHeading] = TagFieldId.AssignedHeading,
        [ScopeFieldSlot.AssignedRunway] = TagFieldId.AssignedRunway,
        [ScopeFieldSlot.Scratchpad1] = TagFieldId.Scratchpad1,
        [ScopeFieldSlot.Scratchpad2] = TagFieldId.Scratchpad2,
        [ScopeFieldSlot.Handoff] = TagFieldId.Handoff,
        [ScopeFieldSlot.Squawk] = TagFieldId.Squawk,
        [ScopeFieldSlot.ModeC] = TagFieldId.ModeC,
        [ScopeFieldSlot.NoLandingClearance] = TagFieldId.NoLandingClearance,
        [ScopeFieldSlot.ConflictAlert] = TagFieldId.ConflictAlert,
        [ScopeFieldSlot.InstructorNote] = TagFieldId.Note,
    };

    private static AircraftModel With(Action<AircraftModel> setup)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        setup(ac);
        return ac;
    }

    private static Case Plain(Action<AircraftModel> setup) => new(With(setup), null, null);

    private static void OwnedWithScratchpads(AircraftModel ac)
    {
        ac.AssignedTo = "AB";
        ac.OwnerSectorCode = "2S";
        ac.Scratchpad1 = "ABC";
        ac.Scratchpad2 = "XY";
        ac.PointoutToTcpCode = "3E";
        ac.AssignedRunway = "28R";
        ac.Destination = "KSFO";
        ac.AssignedAltitude = 11000;
        ac.AssignedSpeed = 250;
    }

    private static TextStyle Style(float size) => new(new SKFont { Size = size }, new SKPaint());

    private static ScopeBlockLayout ScopeLayout(string templateId, Case c, bool flashOn, TextStyle style)
    {
        TrackPresentation presentation = TrackPresenter.Present(
            ScopeTestAircraft.Input(c.Ac, templateId, flashOn, atpaLead: c.Lead, conflictPeer: c.Peer)
        );
        return DatablockTemplate.Layout(presentation, ScopeTextMetrics.FromStyle(style));
    }

    private static RadarDatablockLayout Today(Case c, bool flashOn, TextStyle style)
    {
        using var pin = RadarBlinkPin.Phase(flashOn);
        return RadarDatablockLayout.Compute(
            c.Ac,
            new DatablockPlacement(0, 0, style, true),
            new DatablockOverlays(true, c.Peer, true, c.Lead),
            callsignMarker: ""
        );
    }

    /// <summary>The lines today's renderer draws for a layout, in draw order; the ident token blanks off-phase.</summary>
    private static List<string> DrawnLines(RadarDatablockLayout layout, bool flashOn)
    {
        string line2 = (layout.IdentActive && !flashOn) ? layout.Line2[..^(RadarDatablockLayout.IdentText.Length + 1)] : layout.Line2;
        var lines = new List<string> { layout.Line1, line2 };
        AddIf(lines, layout.SquawkLine, layout.SquawkLine.Length > 0);
        AddIf(lines, layout.Line3, layout.ReserveOwnerSlot);
        AddIf(lines, layout.AtpaLine, layout.AtpaLine.Length > 0);
        AddIf(lines, layout.Line4, layout.Line4.Length > 0);
        AddIf(lines, layout.Line5, layout.ReserveWarningSlot);
        AddIf(lines, layout.ConflictLine, layout.ReserveConflictSlot);
        AddIf(lines, layout.Line6, layout.Line6.Length > 0);
        return lines;
    }

    private static void AddIf(List<string> lines, string line, bool present)
    {
        if (present)
        {
            lines.Add(line);
        }
    }

    /// <summary>The scope block's lines: each row's field rects, top to bottom.</summary>
    private static List<FieldRect[]> Rows(ScopeBlockLayout layout) => [.. layout.Fields.GroupBy(f => f.Rect.Top).Select(g => g.ToArray())];

    /// <summary>Rebuilds a STARS row's drawn text by joining its fields' texts with their template separators.</summary>
    private static string JoinRow(FieldRect[] row)
    {
        var separators = ScopeTemplates.Stars.Lines.SelectMany(l => l.Slots).ToDictionary(p => p.Slot, p => p.Separator);
        string text = "";
        foreach (FieldRect field in row.Where(f => f.Text.Length > 0))
        {
            text += text.Length > 0 ? separators[field.Slot] + field.Text : field.Text;
        }
        return text;
    }

    [Theory]
    [MemberData(nameof(CasesByPhase))]
    public void StarsTemplate_FullBlock_MatchesRadarDatablockLayout(string caseName, bool flashOn)
    {
        TextStyle style = Style(12);
        Case c = Cases[caseName]();
        RadarDatablockLayout today = Today(c, flashOn, style);
        List<string> stableLines = DrawnLines(Today(c, flashOn: true, style), flashOn: true);

        ScopeBlockLayout scope = ScopeLayout(ScopeTemplates.StarsId, c, flashOn, style);
        List<FieldRect[]> rows = Rows(scope);

        Assert.Equal(DrawnLines(today, flashOn), rows.Select(JoinRow));
        Assert.Equal(today.Rect, scope.Bounds);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(style.Measure(stableLines[i]), rows[i][^1].Rect.Right, precision: 3);
        }
        if (today.TypeTokenStart >= 0)
        {
            FieldRect cwtType = Assert.Single(scope.Fields, f => f.Slot == ScopeFieldSlot.CwtType);
            Assert.Equal(style.Measure(today.Line2[..today.TypeTokenStart]), cwtType.Rect.Left, precision: 3);
            Assert.Equal(style.Measure(today.Line2.Substring(today.TypeTokenStart, today.TypeTokenLength)), cwtType.Rect.Width, precision: 3);
        }
    }

    [Theory]
    [MemberData(nameof(CasesBySizeAndPhase))]
    public void EuroScopeTemplate_MatchesEuroScopeTagLayout(string caseName, float fontSize, bool flashOn)
    {
        TextStyle style = Style(fontSize);
        Case c = Cases[caseName]();
        EuroScopeTagResult today;
        using (RadarBlinkPin.Phase(flashOn))
        {
            today = EuroScopeTagLayout.Layout(c.Ac, OriginX, OriginY, style, true, true, c.Peer);
        }

        ScopeBlockLayout scope = ScopeLayout(ScopeTemplates.EuroScopeId, c, flashOn, style);
        // Today's tag drops a flashing field off-phase, except the ident, which it keeps and its renderer blanks;
        // the template keeps every flashing field reserved with blank text.
        TagFieldRect[] todayDrawn = [.. today.Fields.Where(f => flashOn || (f.Field != TagFieldId.Ident))];
        FieldRect[] drawn = [.. scope.Fields.Where(f => f.Text.Length > 0)];

        Assert.Equal(todayDrawn.Select(f => (f.Field, f.Text)), drawn.Select(f => (EuroScopeIds[f.Slot], f.Text)));
        for (int i = 0; i < todayDrawn.Length; i++)
        {
            AssertRelative(todayDrawn[i].Rect, drawn[i].Rect);
        }
        AssertRelative(today.Bounds, scope.Bounds);
    }

    private static void AssertRelative(SKRect expectedAtOrigin, SKRect actualRelative)
    {
        Assert.Equal(expectedAtOrigin.Left - OriginX, actualRelative.Left, precision: 3);
        Assert.Equal(expectedAtOrigin.Top - OriginY, actualRelative.Top, precision: 3);
        Assert.Equal(expectedAtOrigin.Right - OriginX, actualRelative.Right, precision: 3);
        Assert.Equal(expectedAtOrigin.Bottom - OriginY, actualRelative.Bottom, precision: 3);
    }
}
