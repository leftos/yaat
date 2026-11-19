using System.Text.Json;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins the Scope's block templates to frozen text: the STARS template's Full block reads the lines in
/// <c>Golden/stars-full-block.json</c>, and the EuroScope template draws the fields and texts in
/// <c>Golden/euroscope-block.json</c>. The presenter reads the flash phase it is passed, one row per phase.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class ScopeTemplateParityTests
{
    /// <param name="Marker">The student-level marker line 1 carries.</param>
    /// <param name="MarkLevel">Whether the student-level marker preference is on.</param>
    private sealed record Case(AircraftModel Ac, AircraftModel? Lead, AircraftModel? Peer, string Marker, bool MarkLevel)
    {
        /// <summary>
        /// An ATPA trailer: the Scope's STARS block carries the ATPA distance at line 2's right end, with no extra row.
        /// </summary>
        public bool AtpaOnLine2 { get; init; }
    }

    private static readonly Dictionary<string, Func<Case>> Cases = new()
    {
        ["Vfr"] = () => Plain(ac => ac.FlightRules = "VFR"),
        ["NoCwtIdenting"] = () =>
            Plain(ac =>
            {
                ac.CwtCode = "";
                ac.IsIdenting = true;
            }),
        ["AtpaLead"] = () => AtpaPair(identing: false),
        ["AtpaLeadWithIdent"] = () => AtpaPair(identing: true),
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
                ScopeTestAircraft.CreateConflictPeer(),
                Marker: "",
                MarkLevel: true
            ),
        ["Ldb"] = () => Student(StarsDatablockLevel.Limited, markLevel: true),
        ["Pdb"] = () => Student(StarsDatablockLevel.Partial, markLevel: true),
        ["LdbMarkerOff"] = () => Student(StarsDatablockLevel.Limited, markLevel: false),
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

    private static AircraftModel With(Action<AircraftModel> setup)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        setup(ac);
        return ac;
    }

    private static Case Plain(Action<AircraftModel> setup) => new(With(setup), null, null, Marker: "", MarkLevel: true);

    /// <summary>The ATPA trailer UAL238 behind its lead, identing or not.</summary>
    private static Case AtpaPair(bool identing)
    {
        AircraftModel trailer = ScopeTestAircraft.CreateAtpaTrailer();
        trailer.IsIdenting = identing;
        return new Case(trailer, ScopeTestAircraft.CreateAtpaLead(), null, Marker: "", MarkLevel: true) { AtpaOnLine2 = true };
    }

    /// <summary>A track the student's scope shows at <paramref name="level"/>, with the marker preference on or off.</summary>
    private static Case Student(StarsDatablockLevel level, bool markLevel) =>
        new(With(ac => ac.StudentDatablockLevel = level), null, null, markLevel ? DatablockText.StudentLevelMarker(level) : "", markLevel);

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
            ScopeTestAircraft.Input(c.Ac, templateId, flashOn, atpaLead: c.Lead, conflictPeer: c.Peer) with
            {
                Display = ScopeTestAircraft.AllDisplay with { MarkStudentLevel = c.MarkLevel },
            }
        );
        return DatablockTemplate.Layout(presentation, ScopeTextMetrics.FromStyle(style));
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

    /// <summary>
    /// One frozen STARS full block: the lines the block draws, in draw order. Text only, since measured bounds
    /// follow the machine's default typeface.
    /// </summary>
    /// <param name="Case">The <see cref="Cases"/> key.</param>
    /// <param name="FlashOn">The flash phase the block was laid out in.</param>
    /// <param name="Lines">The drawn lines, in draw order.</param>
    private sealed record GoldenBlock(string Case, bool FlashOn, string[] Lines);

    private static readonly JsonSerializerOptions GoldenJson = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<GoldenBlock[]> GoldenBlocks = new(() =>
        JsonSerializer.Deserialize<GoldenBlock[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "Scope", "Golden", "stars-full-block.json")),
            GoldenJson
        ) ?? throw new InvalidOperationException("stars-full-block.json deserialized to null")
    );

    private static GoldenBlock Golden(string caseName, bool flashOn) =>
        Assert.Single(GoldenBlocks.Value, g => (g.Case == caseName) && (g.FlashOn == flashOn));

    [Fact]
    public void FrozenStarsText_CoversExactlyTheParityCases()
    {
        HashSet<(string, bool)> expected = [.. Cases.Keys.SelectMany(name => new[] { (name, true), (name, false) })];

        HashSet<(string, bool)> frozen = [.. GoldenBlocks.Value.Select(g => (g.Case, g.FlashOn))];

        Assert.Equal(expected, frozen);
    }

    [Theory]
    [MemberData(nameof(CasesByPhase))]
    public void StarsTemplate_FullBlock_MatchesFrozenStarsText(string caseName, bool flashOn)
    {
        GoldenBlock golden = Golden(caseName, flashOn);

        ScopeBlockLayout scope = ScopeLayout(ScopeTemplates.StarsId, Cases[caseName](), flashOn, Style(12));

        Assert.Equal(golden.Lines, Rows(scope).Select(JoinRow));
    }

    [Theory]
    [InlineData("AtpaLead", true)]
    [InlineData("AtpaLead", false)]
    [InlineData("AtpaLeadWithIdent", true)]
    [InlineData("AtpaLeadWithIdent", false)]
    public void StarsTemplate_AtpaPair_PutsTheDistanceAtLine2sRightEnd_WithNoExtraRow(string caseName, bool flashOn)
    {
        ScopeBlockLayout scope = ScopeLayout(ScopeTemplates.StarsId, Cases[caseName](), flashOn, Style(12));
        List<FieldRect[]> rows = Rows(scope);

        Assert.Equal(2, rows.Count);
        FieldRect atpa = Assert.Single(scope.Fields, f => f.Slot == ScopeFieldSlot.Atpa);
        Assert.Equal("3.2", atpa.Text);
        Assert.Contains(atpa, rows[1]);
        FieldRect[] drawn = [.. rows[1].Where(f => f.Text.Length > 0)];
        Assert.Equal(ScopeFieldSlot.CwtType, drawn[Array.IndexOf(drawn, atpa) - 1].Slot);
        Assert.All(drawn.SkipWhile(f => f != atpa).Skip(1), f => Assert.Equal(ScopeFieldSlot.Ident, f.Slot));
    }

    [Fact]
    public void StarsTemplate_Line2_IsAltitudeGroundSpeedCwtTypeAtpaThenIdent()
    {
        Assert.Equal(
            [ScopeFieldSlot.Altitude, ScopeFieldSlot.GroundSpeed, ScopeFieldSlot.CwtType, ScopeFieldSlot.Atpa, ScopeFieldSlot.Ident],
            ScopeTemplates.Stars.Lines[1].Slots.Select(s => s.Slot)
        );
        Assert.Single(ScopeTemplates.Stars.Lines, l => l.Slots.Any(s => s.Slot == ScopeFieldSlot.Atpa));
    }

    /// <summary>One drawn EuroScope tag field: its slot name and text.</summary>
    private sealed record GoldenField(string Slot, string Text);

    /// <summary>
    /// One frozen EuroScope tag: the drawn fields' slots and texts, row by row, top to bottom. Text only, since measured
    /// rects follow the machine's default typeface; the text is the same at every font size.
    /// </summary>
    /// <param name="Case">The <see cref="Cases"/> key.</param>
    /// <param name="FlashOn">The flash phase the tag was laid out in.</param>
    /// <param name="Rows">The drawn fields, row by row.</param>
    private sealed record GoldenTag(string Case, bool FlashOn, GoldenField[][] Rows);

    private static readonly Lazy<GoldenTag[]> GoldenTags = new(() =>
        JsonSerializer.Deserialize<GoldenTag[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "Scope", "Golden", "euroscope-block.json")),
            GoldenJson
        ) ?? throw new InvalidOperationException("euroscope-block.json deserialized to null")
    );

    /// <summary>The scope tag's drawn fields as golden rows: blank (reserved) fields are not drawn.</summary>
    private static List<GoldenField[]> DrawnRows(ScopeBlockLayout layout) =>
        [
            .. layout
                .Fields.Where(f => f.Text.Length > 0)
                .GroupBy(f => f.Rect.Top)
                .Select(g => g.Select(f => new GoldenField(f.Slot.ToString(), f.Text)).ToArray()),
        ];

    [Fact]
    public void FrozenEuroScopeText_CoversExactlyTheParityCases()
    {
        HashSet<(string, bool)> expected = [.. Cases.Keys.SelectMany(name => new[] { (name, true), (name, false) })];

        HashSet<(string, bool)> frozen = [.. GoldenTags.Value.Select(g => (g.Case, g.FlashOn))];

        Assert.Equal(expected, frozen);
    }

    [Theory]
    [MemberData(nameof(CasesBySizeAndPhase))]
    public void EuroScopeTemplate_MatchesFrozenEuroScopeText(string caseName, float fontSize, bool flashOn)
    {
        GoldenTag golden = Assert.Single(GoldenTags.Value, g => (g.Case == caseName) && (g.FlashOn == flashOn));

        ScopeBlockLayout scope = ScopeLayout(ScopeTemplates.EuroScopeId, Cases[caseName](), flashOn, Style(fontSize));

        Assert.DoesNotContain(scope.Fields, f => f.Slot == ScopeFieldSlot.StudentLevelMark);
        Assert.Equal(golden.Rows, DrawnRows(scope));
    }
}
