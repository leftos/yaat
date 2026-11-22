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
[Collection(MapBlinkClockCollection.Name)]
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
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.GroundSpeed, ScopeFieldAction.OpenEramSpeedMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.StatusLine3, ScopeFieldAction.OpenHandoffMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4Notes, ScopeFieldAction.OpenEramHsfMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4Heading, ScopeFieldAction.OpenEramHeadingMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4Speed, ScopeFieldAction.OpenEramSpeedMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4FreeText, ScopeFieldAction.OpenEramFreeTextMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Line4Pad, ScopeFieldAction.None)]
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
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedHeading, ScopeFieldAction.StartHeadingVector)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.AssignedRunway, ScopeFieldAction.OpenRunwayMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Scratchpad1, ScopeFieldAction.OpenScratchpadMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Scratchpad2, ScopeFieldAction.OpenScratchpadMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Handoff, ScopeFieldAction.OpenHandoffMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Squawk, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Destination, ScopeFieldAction.StartDrawRoute)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.InstructorNote, ScopeFieldAction.OpenNoteBox)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.TypeCwt, ScopeFieldAction.None)]
    public void ActionFor_TemplateAndSlot(string templateId, ScopeFieldSlot slot, ScopeFieldAction expected) =>
        Assert.Equal(expected, TrackPresenter.ActionFor(templateId, slot, starsFieldMenus: true));

    /// <summary>
    /// With the "STARS field menus" setting off, every STARS field but the note is inert, so a click on it only selects; ERAM and
    /// EuroScope fields keep their actions.
    /// </summary>
    [Theory]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Altitude, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.GroundSpeed, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Squawk, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Ident, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.StatusLine3, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.InstructorNote, ScopeFieldAction.OpenNoteBox)]
    [InlineData(ScopeTemplates.StarsId, ScopeFieldSlot.Callsign, ScopeFieldAction.None)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.EramAltitude, ScopeFieldAction.OpenEramAltitudeMenu)]
    [InlineData(ScopeTemplates.EramId, ScopeFieldSlot.Ident, ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeTemplates.EuroScopeId, ScopeFieldSlot.Altitude, ScopeFieldAction.OpenAltitudeMenu)]
    public void ActionFor_StarsFieldMenusOff(string templateId, ScopeFieldSlot slot, ScopeFieldAction expected) =>
        Assert.Equal(expected, TrackPresenter.ActionFor(templateId, slot, starsFieldMenus: false));

    [Fact]
    public void Present_StarsFieldMenusOff_LeavesOnlyTheNoteWithAnAction()
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ScopeTestAircraft.CreateEverythingFlashing(), ScopeTemplates.StarsId) with
        {
            Display = ScopeTestAircraft.AllDisplay with { StarsFieldMenus = false },
        };

        TrackPresentation p = TrackPresenter.Present(input);

        Assert.Contains(p.Fields, f => f.Slot == ScopeFieldSlot.Altitude);
        ScopeField acting = Assert.Single(p.Fields, f => f.Action != ScopeFieldAction.None);
        Assert.Equal(ScopeFieldSlot.InstructorNote, acting.Slot);
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId)]
    [InlineData(ScopeTemplates.EramId)]
    [InlineData(ScopeTemplates.EuroScopeId)]
    public void Present_EveryFieldCarriesItsSlotsAction(string templateId)
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateEverythingFlashing(), templateId));

        Assert.Contains(p.Fields, f => f.Action != ScopeFieldAction.None);
        Assert.All(p.Fields, f => Assert.Equal(TrackPresenter.ActionFor(templateId, f.Slot, starsFieldMenus: true), f.Action));
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
        using (MapBlinkPin.Phase(true))
        {
            frame = BuildMixed(ac, starsFieldMenus: true);
        }

        IReadOnlyList<FieldRect> fields = Assert.Single(frame.Tracks).Block.Fields;
        Assert.Equal(expectedNote, Assert.Single(fields, f => f.Slot == ScopeFieldSlot.InstructorNote).Action);
        string kind = ScopeOwnerKind.TemplateIdFor(ac, ScopeTemplates.EramId);
        Assert.All(fields, f => Assert.Equal(TrackPresenter.ActionFor(kind, f.Slot, starsFieldMenus: true), f.Action));
    }

    /// <summary>
    /// With the "STARS field menus" setting off, a STARS-owned Mixed track's altitude is inert and an ERAM-owned one's still opens
    /// its ERAM menu.
    /// </summary>
    [Theory]
    [InlineData("2S", ScopeFieldSlot.Altitude, ScopeFieldAction.None)]
    [InlineData("C45", ScopeFieldSlot.EramAltitude, ScopeFieldAction.OpenEramAltitudeMenu)]
    public void Mixed_StarsFieldMenusOff_FollowsTheOwnerKind(string ownerSectorCode, ScopeFieldSlot slot, ScopeFieldAction expected)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerSectorCode = ownerSectorCode;
        ScopeFrame frame;
        using (MapBlinkPin.Phase(true))
        {
            frame = BuildMixed(ac, starsFieldMenus: false);
        }

        IReadOnlyList<FieldRect> fields = Assert.Single(frame.Tracks).Block.Fields;
        Assert.Equal(expected, Assert.Single(fields, f => f.Slot == slot).Action);
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

    /// <summary>
    /// The STARS CRC-style menus line up with the drawn altitude field, on whatever line the block puts it: line 2 of a Full
    /// block, line 1 of a Limited one (others drawn partial, the track not the viewer's).
    /// </summary>
    [Fact]
    public void FieldMiddleY_IsTheDrawnAltitudesMiddle_OnFullAndLimitedBlocks()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.OwnerSectorCode = null;
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");
        ScopeFrame full;
        ScopeFrame limited;
        using (MapBlinkPin.Phase(true))
        {
            full = BuildStars(ac, viewer, ScopeProfile.OthersFull);
            limited = BuildStars(ac, viewer, ScopeProfile.OthersPartial);
        }

        ScopeBlockLayout fullBlock = Assert.Single(full.Tracks).Block;
        ScopeBlockLayout limitedBlock = Assert.Single(limited.Tracks).Block;
        Assert.Contains(fullBlock.Fields, f => f.Slot == ScopeFieldSlot.Callsign);
        Assert.DoesNotContain(limitedBlock.Fields, f => f.Slot == ScopeFieldSlot.Callsign);
        Assert.DoesNotContain(limitedBlock.Fields, f => f.Slot == ScopeFieldSlot.GroundSpeed);
        FieldRect fullAltitude = fullBlock.Fields.First(f => f.Slot == ScopeFieldSlot.Altitude);
        FieldRect limitedAltitude = limitedBlock.Fields.First(f => f.Slot == ScopeFieldSlot.Altitude);

        Assert.Equal(fullAltitude.Rect.MidY, ScopeView.FieldMiddleY(full, ac.Callsign, ScopeFieldSlot.Altitude));
        Assert.Equal(limitedAltitude.Rect.MidY, ScopeView.FieldMiddleY(limited, ac.Callsign, ScopeFieldSlot.Altitude));
        // Line 1 against line 2: a fixed line 2 would put the limited block's menu a line low.
        Assert.True(
            (limitedAltitude.Rect.MidY - limitedBlock.Bounds.Top) < (fullAltitude.Rect.MidY - fullBlock.Bounds.Top),
            "the limited block's altitude is not above where the full block's sits"
        );
        Assert.Null(ScopeView.FieldMiddleY(limited, ac.Callsign, ScopeFieldSlot.GroundSpeed));
    }

    private static ScopeFrame BuildStars(AircraftModel ac, ScopeViewer viewer, string othersLevel)
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
            ScopeTemplates.StarsId,
            Metrics(),
            ScopeTestAircraft.AllAlerts,
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = viewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            OthersLevel: othersLevel,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache()
        );
        return ScopeFrame.Build(input);
    }

    private static ScopeFrame BuildMixed(AircraftModel ac, bool starsFieldMenus)
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
            ScopeTestAircraft.AllDisplay with
            {
                StarsFieldMenus = starsFieldMenus,
            },
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
