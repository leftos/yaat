using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// What clicking each datablock field does (<see cref="TrackPresenter.ActionFor"/>), per template and slot; a Mixed track
/// takes its owner kind's action, a ground target's fields stay inert, and the laid-out <see cref="FieldRect"/> carries the
/// action the presenter gave its field, on a block line and in ERAM column 0.
/// </summary>
[Collection(RadarBlinkClockCollection.Name)]
public class ScopeFieldActionTests
{
    [Theory]
    // STARS
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Altitude, ScopeFieldAction.OpenAltitudeMenu)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.GroundSpeed, ScopeFieldAction.OpenSpeedMenu)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Squawk, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.StatusLine3, ScopeFieldAction.OpenScratchpadMenu)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.InstructorNote, ScopeFieldAction.OpenNoteBox)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.CwtType, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Atpa, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.ModeC, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.ConflictAlert, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Handoff, ScopeFieldAction.None)]
    // ERAM
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.EramAltitude, ScopeFieldAction.OpenEramAltitudeMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.GroundSpeed, ScopeFieldAction.OpenSpeedMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.StatusLine3, ScopeFieldAction.OpenHandoffMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4Notes, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.HsfIndicator, ScopeFieldAction.ToggleHsf)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Vci, ScopeFieldAction.ToggleVci)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.NotYourControl, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.AltitudeMismatch, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.InstructorNote, ScopeFieldAction.None)]
    // EuroScope
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Owner, ScopeFieldAction.TakeControl)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Altitude, ScopeFieldAction.OpenAltitudeMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedAltitude, ScopeFieldAction.OpenAltitudeMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedSpeed, ScopeFieldAction.OpenSpeedMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedHeading, ScopeFieldAction.OpenHeadingMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedRunway, ScopeFieldAction.OpenRunwayMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Scratchpad1, ScopeFieldAction.OpenScratchpadMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Scratchpad2, ScopeFieldAction.OpenScratchpadMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Handoff, ScopeFieldAction.OpenHandoffMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Squawk, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Destination, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.TypeCwt, ScopeFieldAction.None)]
    public void ActionFor_TemplateAndSlot(string templateId, ScopeFieldSlot slot, ScopeFieldAction expected) =>
        Assert.Equal(expected, TrackPresenter.ActionFor(templateId, slot));

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.EuroScopeId)]
    public void Present_EveryFieldCarriesItsSlotsAction(string templateId)
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateEverythingFlashing(), templateId));

        Assert.Contains(p.Fields, f => f.Action != ScopeFieldAction.None);
        Assert.All(p.Fields, f => Assert.Equal(TrackPresenter.ActionFor(templateId, f.Slot), f.Action));
    }

    /// <summary>The note field opens the note box on a STARS block and is inert on an ERAM one, so it tells the kinds apart.</summary>
    [Theory]
    [InlineData("2S", ScopeFieldAction.OpenNoteBox)]
    [InlineData("C45", ScopeFieldAction.None)]
    public void Mixed_TrackTakesItsOwnerKindsAction(string ownerSectorCode, ScopeFieldAction expectedNote)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerSectorCode = ownerSectorCode;
        ac.Note = "Watch wake";
        ScopeFrame frame;
        using (RadarBlinkPin.Phase(true))
        {
            frame = BuildMixed(ac);
        }

        IReadOnlyList<FieldRect> fields = Assert.Single(frame.Tracks).Block.Fields;
        Assert.Equal(expectedNote, Assert.Single(fields, f => f.Slot == ScopeFieldSlot.InstructorNote).Action);
        string kind = ScopeOwnerKind.TemplateIdFor(ac);
        Assert.All(fields, f => Assert.Equal(TrackPresenter.ActionFor(kind, f.Slot), f.Action));
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    public void GroundTarget_FieldsAreInert(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.BeaconCode = 4321;
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, templateId) with { Level = ScopeBlockLevel.Ground };

        TrackPresentation p = TrackPresenter.Present(input);

        Assert.Contains(p.Fields, f => (f.Slot == ScopeFieldSlot.Squawk) && (f.Text.Length > 0));
        Assert.All(p.Fields, f => Assert.Equal(ScopeFieldAction.None, f.Action));
    }

    [Fact]
    public void Layout_LinePath_FieldRectCarriesThePresentedAction()
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateEverythingFlashing(), ScopeTemplates.StarsId));

        ScopeBlockLayout layout = DatablockTemplate.Layout(p, Metrics());

        Assert.Equal(ScopeFieldAction.OpenAltitudeMenu, Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.Altitude).Action);
        Assert.All(layout.Fields, r => Assert.Equal(Assert.Single(p.Fields, f => f.Slot == r.Slot).Action, r.Action));
    }

    /// <summary>
    /// The presented VCI field is given an action other than its own here, to show the column-0 path carries whatever action the
    /// presenter gave it.
    /// </summary>
    [Fact]
    public void Layout_Column0Path_FieldRectCarriesThePresentedAction()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C45";
        ac.EramOnFrequencySectorIds = ["44"];
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.EramId) with
        {
            Display = ScopeTestAircraft.AllDisplay with { Viewer = viewer },
        };
        TrackPresentation p = TrackPresenter.Present(input);
        List<ScopeField> fields = [.. p.Fields.Select(f => f.Slot == ScopeFieldSlot.Vci ? f with { Action = ScopeFieldAction.OpenNoteBox } : f)];

        ScopeBlockLayout layout = DatablockTemplate.Layout(p with { Fields = fields }, Metrics());

        Assert.Equal(ScopeFieldAction.OpenNoteBox, Assert.Single(layout.Fields, f => f.Slot == ScopeFieldSlot.Vci).Action);
    }

    private static ScopeTextMetrics Metrics() => ScopeTextMetrics.FromStyle(new TextStyle(new SKFont { Size = 12 }, new SKPaint()));

    private static ScopeFrame BuildMixed(AircraftModel ac)
    {
        var vp = new MapViewport
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };
        var input = new ScopeFrameInput(
            [ac],
            null,
            vp,
            ScopeTemplates.MixedId,
            Metrics(),
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
        return ScopeFrame.Build(input);
    }
}
