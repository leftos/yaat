using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins the <see cref="DatablockTemplate"/> engine's geometry: the one <see cref="FieldRect"/> list that both
/// drawing and hit-testing read must be bounded, non-overlapping, flash-stable and translation-invariant.
/// </summary>
public class DatablockTemplateTests
{
    public static TheoryData<string> TemplateIds => [ScopeTemplates.StarsId, ScopeTemplates.EuroScopeId];

    private static ScopeTextMetrics Metrics(float size = 12) => ScopeTextMetrics.FromStyle(new TextStyle(new SKFont { Size = size }, new SKPaint()));

    private static ScopeBlockLayout LayOut(string templateId, bool flashOn)
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(
            ScopeTestAircraft.CreateEverythingFlashing(),
            templateId,
            flashOn,
            conflictPeer: ScopeTestAircraft.CreateConflictPeer()
        );
        return DatablockTemplate.Layout(TrackPresenter.Present(input), Metrics());
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void EveryFieldRect_LiesInsideTheBlockBounds(string templateId)
    {
        ScopeBlockLayout layout = LayOut(templateId, flashOn: true);

        Assert.NotEmpty(layout.Fields);
        Assert.All(
            layout.Fields,
            f =>
            {
                Assert.True(f.Rect.Left >= layout.Bounds.Left, $"{f.Slot} left");
                Assert.True(f.Rect.Top >= layout.Bounds.Top, $"{f.Slot} top");
                Assert.True(f.Rect.Right <= layout.Bounds.Right, $"{f.Slot} right");
                Assert.True(f.Rect.Bottom <= layout.Bounds.Bottom, $"{f.Slot} bottom");
            }
        );
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void RectsWithinALine_DoNotOverlap(string templateId)
    {
        ScopeBlockLayout layout = LayOut(templateId, flashOn: true);

        foreach (IGrouping<float, FieldRect> line in layout.Fields.GroupBy(f => f.Rect.Top))
        {
            FieldRect[] ordered = [.. line.OrderBy(f => f.Rect.Left)];
            for (int i = 1; i < ordered.Length; i++)
            {
                Assert.True(ordered[i - 1].Rect.Right <= ordered[i].Rect.Left, $"{ordered[i - 1].Slot} overlaps {ordered[i].Slot}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void ReservedEmptySlot_KeepsItsWidth(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsIdenting = true;
        ScopeTextMetrics metrics = Metrics();

        ScopeBlockLayout off = DatablockTemplate.Layout(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: false)), metrics);

        FieldRect ident = Assert.Single(off.Fields, f => f.Slot == ScopeFieldSlot.Ident);
        Assert.Equal("", ident.Text);
        Assert.Equal(metrics.Measure("ID"), ident.Rect.Width, precision: 3);
    }

    [Theory]
    [InlineData(ScopeGapMode.TextSeparator, true)]
    [InlineData(ScopeGapMode.TextSeparator, false)]
    [InlineData(ScopeGapMode.PixelGap, true)]
    [InlineData(ScopeGapMode.PixelGap, false)]
    public void FieldAfterAReservedField_StartsAtTheAdvanceOfTheMeasuredTextBeforeIt(ScopeGapMode gapMode, bool flashOn)
    {
        // No shipped template puts a reserved slot before another on one line, so the line is built here.
        var template = new ScopeTemplate(
            "test",
            gapMode,
            FieldGap: 6f,
            Padding: 3f,
            [
                new ScopeTemplateLine(
                    [
                        new ScopeSlotPlacement(ScopeFieldSlot.Ident, " ", Reserved: true),
                        new ScopeSlotPlacement(ScopeFieldSlot.Altitude, " ", Reserved: false),
                        new ScopeSlotPlacement(ScopeFieldSlot.GroundSpeed, " ", Reserved: false),
                    ],
                    AlwaysShown: true,
                    TrailingGapInBounds: true
                ),
            ]
        );
        var presentation = new TrackPresentation(
            "test",
            ScopeBlockLevel.Full,
            ScopeColorToken.Default,
            [
                new ScopeField(ScopeFieldSlot.Ident, flashOn ? "ID" : "", "ID", ScopeColorToken.Default, ScopeFieldAction.None),
                new ScopeField(ScopeFieldSlot.Altitude, "230", "230", ScopeColorToken.Default, ScopeFieldAction.None),
                new ScopeField(ScopeFieldSlot.GroundSpeed, "25", "25", ScopeColorToken.Default, ScopeFieldAction.None),
            ]
        );
        ScopeTextMetrics metrics = Metrics();

        ScopeBlockLayout layout = DatablockTemplate.Layout(presentation, template, metrics);

        float[] expectedLefts =
            gapMode == ScopeGapMode.TextSeparator
                ? [0f, metrics.Measure("ID "), metrics.Measure("ID 230 ")]
                : [0f, metrics.Measure("ID") + 6f, metrics.Measure("ID") + 6f + metrics.Measure("230") + 6f];
        Assert.Equal(3, layout.Fields.Count);
        for (int i = 0; i < expectedLefts.Length; i++)
        {
            Assert.Equal(expectedLefts[i], layout.Fields[i].Rect.Left, precision: 3);
        }
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Rects_AreIdenticalAcrossFlashPhases(string templateId)
    {
        ScopeBlockLayout on = LayOut(templateId, flashOn: true);
        ScopeBlockLayout off = LayOut(templateId, flashOn: false);

        Assert.Equal(on.Bounds, off.Bounds);
        Assert.Equal(on.Fields.Select(f => (f.Slot, f.Rect)), off.Fields.Select(f => (f.Slot, f.Rect)));
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Rects_TranslateWithTheOrigin(string templateId)
    {
        ScopeBlockLayout relative = LayOut(templateId, flashOn: true);
        ScopeBlockLayout placed = relative.At(137, -52);

        Assert.Equal(relative.Bounds.Left + 137, placed.Bounds.Left, precision: 3);
        Assert.Equal(relative.Bounds.Top - 52, placed.Bounds.Top, precision: 3);
        Assert.Equal(relative.Fields.Count, placed.Fields.Count);
        for (int i = 0; i < relative.Fields.Count; i++)
        {
            Assert.Equal(relative.Fields[i].Slot, placed.Fields[i].Slot);
            Assert.Equal(relative.Fields[i].Rect.Left + 137, placed.Fields[i].Rect.Left, precision: 3);
            Assert.Equal(relative.Fields[i].Rect.Top - 52, placed.Fields[i].Rect.Top, precision: 3);
            Assert.Equal(relative.Fields[i].Rect.Width, placed.Fields[i].Rect.Width, precision: 3);
            Assert.Equal(relative.Fields[i].Rect.Height, placed.Fields[i].Rect.Height, precision: 3);
        }
    }
}
