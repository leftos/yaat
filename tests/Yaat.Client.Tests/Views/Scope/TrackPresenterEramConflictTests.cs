using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins the ERAM conflict look on the Scope (docs/crc/eram.md, Conflict Alert Processing: "Targets that are currently in
/// conflict alert status will have flashing data blocks"): a track drawn with the ERAM template in a shown ERAM pair flashes
/// its whole block and draws no STARS <c>CA</c> line; a STARS-drawn track and a STARS pair keep today's look.
/// </summary>
[Collection(MapBlinkClockCollection.Name)]
public class TrackPresenterEramConflictTests
{
    private static ScopeField Field(TrackPresentation p, ScopeFieldSlot slot) => Assert.Single(p.Fields, f => f.Slot == slot);

    private static TrackPresentation Present(AircraftModel ac, string templateId, bool flashOn, bool inEramConflict) =>
        TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn) with { InEramConflict = inEramConflict });

    [Fact]
    public void EramTrackInAnEramPair_FlashesTheWholeBlock()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        // In a STARS pair as well: the ERAM look draws no CA line in place of the flash.
        ac.ConflictPeerCallsign = "SWA1234";

        TrackPresentation on = Present(ac, ScopeTemplates.EramId, flashOn: true, inEramConflict: true);
        TrackPresentation off = Present(ac, ScopeTemplates.EramId, flashOn: false, inEramConflict: true);

        Assert.Equal("UAL238", Field(on, ScopeFieldSlot.Callsign).Text);
        Assert.All(off.Fields, f => Assert.Equal("", f.Text));
        Assert.Equal(on.Fields.Select(f => f.StableText), off.Fields.Select(f => f.StableText));
        Assert.Equal("", Field(on, ScopeFieldSlot.ConflictAlert).StableText);
    }

    [Fact]
    public void EramTrackInAnEramPair_KeepsItsLayoutThroughTheFlash()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsIdenting = true;
        var metrics = new ScopeTextMetrics(s => s.Length * 7f, 12f, 14f);

        ScopeBlockLayout on = DatablockTemplate.Layout(Present(ac, ScopeTemplates.EramId, flashOn: true, inEramConflict: true), metrics);
        ScopeBlockLayout off = DatablockTemplate.Layout(Present(ac, ScopeTemplates.EramId, flashOn: false, inEramConflict: true), metrics);

        Assert.Equal(on.Bounds, off.Bounds);
        Assert.Equal(on.Fields.Select(f => (f.Slot, f.Rect)), off.Fields.Select(f => (f.Slot, f.Rect)));
        Assert.All(off.Fields, f => Assert.Equal("", f.Text));
    }

    [Fact]
    public void EramTrackInAnEramPair_FlashesItsLdbToo()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId, flashOn: false) with
        {
            InEramConflict = true,
            Level = ScopeBlockLevel.Partial,
        };

        Assert.All(TrackPresenter.Present(input).Fields, f => Assert.Equal("", f.Text));
    }

    [Fact]
    public void StarsTrackInAnEramPair_IsUnchanged()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();

        foreach (bool flashOn in new[] { true, false })
        {
            Assert.Equal(
                Present(ac, ScopeTemplates.StarsId, flashOn, inEramConflict: false),
                Present(ac, ScopeTemplates.StarsId, flashOn, inEramConflict: true),
                PresentationComparer.Instance
            );
        }
    }

    [Fact]
    public void StarsPairOnAnEramTrack_KeepsTodaysCaLine()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.ConflictPeerCallsign = "SWA1234";
        TrackPresenterInput input = ScopeTestAircraft.Input(
            ac,
            ScopeTemplates.EramId,
            flashOn: false,
            conflictPeer: ScopeTestAircraft.CreateConflictPeer()
        );

        TrackPresentation off = TrackPresenter.Present(input);

        Assert.Equal("UAL238", Field(off, ScopeFieldSlot.Callsign).Text);
        ScopeField ca = Field(off, ScopeFieldSlot.ConflictAlert);
        // Today's line from the shared builder (MCI here: the test model is untracked with no flight plan).
        Assert.Equal(DatablockText.BuildConflictLine(ac, input.ConflictPeer), ca.StableText);
        Assert.NotEqual("", ca.StableText);
        Assert.Equal("", ca.Text);
        Assert.Equal(ca.StableText, Field(TrackPresenter.Present(input with { FlashOn = true }), ScopeFieldSlot.ConflictAlert).Text);
    }

    /// <summary>Compares two presentations by their level, colour, leader and fields, in order.</summary>
    private sealed class PresentationComparer : IEqualityComparer<TrackPresentation>
    {
        public static readonly PresentationComparer Instance = new();

        public bool Equals(TrackPresentation? x, TrackPresentation? y) =>
            (x is not null)
            && (y is not null)
            && (x.Level == y.Level)
            && (x.BlockColor == y.BlockColor)
            && (x.ShowsLeader == y.ShowsLeader)
            && x.Fields.SequenceEqual(y.Fields);

        public int GetHashCode(TrackPresentation obj) => obj.Fields.Count;
    }
}
