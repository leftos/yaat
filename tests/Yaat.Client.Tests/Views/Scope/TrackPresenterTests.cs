using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>Pins the pure <see cref="TrackPresenter"/>: field texts, tokens and the flash/reservation contract.</summary>
[Collection(MapBlinkClockCollection.Name)]
public class TrackPresenterTests
{
    private static ScopeField Field(TrackPresentation p, ScopeFieldSlot slot) => Assert.Single(p.Fields, f => f.Slot == slot);

    [Fact]
    public void VfrMark_PresentForVfr_EmptyForIfr()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        Assert.Equal("", Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId)), ScopeFieldSlot.VfrMark).Text);

        ac.FlightRules = "VFR";
        TrackPresentation vfr = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal("*", Field(vfr, ScopeFieldSlot.VfrMark).Text);
        Assert.Equal("UAL238", Field(vfr, ScopeFieldSlot.Callsign).Text);
    }

    [Fact]
    public void Line2Slots_CarryAltitudeGroundSpeedAndCwtType()
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), ScopeTemplates.StarsId));

        Assert.Equal("230", Field(p, ScopeFieldSlot.Altitude).Text);
        Assert.Equal("25", Field(p, ScopeFieldSlot.GroundSpeed).Text);
        Assert.Equal("D/B738", Field(p, ScopeFieldSlot.CwtType).Text);
    }

    [Fact]
    public void CwtType_FallsBackToTypeAlone_WhenNoCwt()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.CwtCode = "";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal("B738", Field(p, ScopeFieldSlot.CwtType).Text);
    }

    [Fact]
    public void Atpa_ShowsInTrailDistanceFromTheLeadModel_TintedByConeState()
    {
        AircraftModel ac = ScopeTestAircraft.CreateAtpaTrailer();

        TrackPresentation p = TrackPresenter.Present(
            ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, atpaLead: ScopeTestAircraft.CreateAtpaLead())
        );

        ScopeField atpa = Field(p, ScopeFieldSlot.Atpa);
        Assert.Equal("3.2", atpa.Text);
        Assert.Equal(ScopeColorToken.AtpaWarning, atpa.Token);
    }

    [Theory]
    [InlineData(true, true, true, "3.2")]
    [InlineData(true, false, false, "3.2")]
    [InlineData(false, true, true, "")]
    [InlineData(false, false, false, "")]
    public void Atpa_Distance_FollowsDistOnly(bool dist, bool alertCones, bool monitorCones, string expected)
    {
        ScopeAlertOptions alerts = ScopeTestAircraft.AllAlerts with
        {
            ShowAtpa = dist,
            ShowAtpaAlertCones = alertCones,
            ShowAtpaMonitorCones = monitorCones,
        };
        TrackPresenterInput input = ScopeTestAircraft.Input(
            ScopeTestAircraft.CreateAtpaTrailer(),
            ScopeTemplates.StarsId,
            atpaLead: ScopeTestAircraft.CreateAtpaLead()
        ) with
        {
            Alerts = alerts,
        };

        Assert.Equal(expected, Field(TrackPresenter.Present(input), ScopeFieldSlot.Atpa).StableText);
    }

    [Fact]
    public void Atpa_Empty_WhenLeadModelAbsent()
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateAtpaTrailer(), ScopeTemplates.StarsId));

        Assert.Equal("", Field(p, ScopeFieldSlot.Atpa).StableText);
    }

    [Fact]
    public void FlashPhase_TogglesOnlyTheFlashingSlotsText_NotItsReservation()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.IsIdenting = true;

        TrackPresentation on = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, flashOn: true));
        TrackPresentation off = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId, flashOn: false));

        Assert.Equal("ID", Field(on, ScopeFieldSlot.Ident).Text);
        Assert.Equal("", Field(off, ScopeFieldSlot.Ident).Text);
        Assert.Equal("ID", Field(off, ScopeFieldSlot.Ident).StableText);
        Assert.Equal(on.Fields.Where(f => f.Slot != ScopeFieldSlot.Ident), off.Fields.Where(f => f.Slot != ScopeFieldSlot.Ident));
    }

    [Fact]
    public void Presenter_ReadsNothingButItsInputs()
    {
        // Every flashing field is active and the blink clock flips phase on every read, so a hidden read of
        // the blink would make the two calls differ while the passed phase stays fixed.
        AircraftModel ac = ScopeTestAircraft.CreateEverythingFlashing();
        TrackPresenterInput input = ScopeTestAircraft.Input(
            ac,
            ScopeTemplates.StarsId,
            flashOn: false,
            conflictPeer: ScopeTestAircraft.CreateConflictPeer()
        );

        long reads = 0;
        TrackPresentation first;
        TrackPresentation second;
        using (MapBlinkPin.To(() => reads++ * MapBlinkPin.OffPhaseMs))
        {
            first = TrackPresenter.Present(input);
            second = TrackPresenter.Present(input);
        }

        Assert.Equal(first.Fields, second.Fields);
        Assert.Equal(first.BlockColor, second.BlockColor);
    }

    [Fact]
    public void Presentation_IsFullLevel_WithEachSlotsAction_AndSelectionLeavesTheBlockColor()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();

        TrackPresentation idle = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId));
        TrackPresentation selected = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId) with { IsSelected = true });

        Assert.Equal(ScopeBlockLevel.Full, idle.Level);
        Assert.Equal(ScopeBlockColor.Default, idle.BlockColor);
        Assert.Equal(ScopeBlockColor.Default, selected.BlockColor);
        Assert.All(idle.Fields, f => Assert.Equal(TrackPresenter.ActionFor(ScopeTemplates.EuroScopeId, f.Slot, starsFieldMenus: true), f.Action));
        Assert.Contains(idle.Fields, f => f.Action != ScopeFieldAction.None);
    }

    // The block-colour precedence:
    // middle-click highlight, then the RPO assignment tint, then the student STARS colour (sync on), then Default.
    private const string Initials = "AB";

    private static ScopeBlockColor BlockColorOf(
        string templateId,
        Action<AircraftModel> setUp,
        ScopeDisplayOptions display,
        bool isHighlighted,
        bool isSelected
    )
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        setUp(ac);
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, templateId) with
        {
            Display = display,
            IsHighlighted = isHighlighted,
            IsSelected = isSelected,
        };
        return TrackPresenter.Present(input).BlockColor;
    }

    private static ScopeDisplayOptions Display(bool sync, string? initials, bool assignmentTint, bool unassignedTint) =>
        ScopeTestAircraft.AllDisplay with
        {
            SyncStudentColors = sync,
            UserInitials = initials,
            AssignmentTint = assignmentTint,
            UnassignedTint = unassignedTint,
        };

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_HighlightOverTint(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.Highlighted,
            BlockColorOf(templateId, ac => ac.AssignedTo = Initials, Display(true, Initials, true, false), isHighlighted: true, isSelected: false)
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Tint_HighlightedAssignedToMe_KeepsTheTintBesideTheHighlight(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AssignedTo = Initials;
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, templateId) with
        {
            Display = Display(true, Initials, true, false),
            IsHighlighted = true,
        };

        TrackPresentation p = TrackPresenter.Present(input);

        Assert.Equal(ScopeBlockColor.Highlighted, p.BlockColor);
        Assert.Equal(ScopeTintRole.AssignedToMe, p.Tint);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_TintOverStudent(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.AssignedToMe,
            BlockColorOf(
                templateId,
                ac =>
                {
                    ac.AssignedTo = Initials;
                    ac.StudentDatablockColor = StarsDatablockColor.Unowned;
                },
                Display(true, Initials, true, false),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_OthersTint(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.AssignedToOther,
            BlockColorOf(templateId, ac => ac.AssignedTo = "XX", Display(true, Initials, false, true), isHighlighted: false, isSelected: false)
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_CaseInsensitiveMe(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.AssignedToMe,
            BlockColorOf(templateId, ac => ac.AssignedTo = "ab", Display(true, Initials, true, false), isHighlighted: false, isSelected: false)
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_TintOffFallsToStudent(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.StudentPointout,
            BlockColorOf(
                templateId,
                ac =>
                {
                    ac.AssignedTo = Initials;
                    ac.StudentDatablockColor = StarsDatablockColor.Pointout;
                },
                Display(true, Initials, false, false),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_NullInitials_AppliesNoTint(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.StudentOwned,
            BlockColorOf(
                templateId,
                ac => ac.StudentDatablockColor = StarsDatablockColor.Owned,
                Display(true, null, true, true),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_SyncOff_IsDefault(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.Default,
            BlockColorOf(
                templateId,
                ac => ac.StudentDatablockColor = StarsDatablockColor.Unowned,
                Display(false, null, false, false),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [InlineData(ScopeTemplates.StarsId, StarsDatablockColor.Owned, ScopeBlockColor.StudentOwned)]
    [InlineData(ScopeTemplates.StarsId, StarsDatablockColor.Unowned, ScopeBlockColor.StudentUnowned)]
    [InlineData(ScopeTemplates.StarsId, StarsDatablockColor.Pointout, ScopeBlockColor.StudentPointout)]
    [InlineData(ScopeTemplates.StarsId, StarsDatablockColor.Highlighted, ScopeBlockColor.StudentHighlighted)]
    [InlineData(ScopeTemplates.EuroScopeId, StarsDatablockColor.Owned, ScopeBlockColor.StudentOwned)]
    [InlineData(ScopeTemplates.EuroScopeId, StarsDatablockColor.Unowned, ScopeBlockColor.StudentUnowned)]
    [InlineData(ScopeTemplates.EuroScopeId, StarsDatablockColor.Pointout, ScopeBlockColor.StudentPointout)]
    [InlineData(ScopeTemplates.EuroScopeId, StarsDatablockColor.Highlighted, ScopeBlockColor.StudentHighlighted)]
    public void BlockColor_EachStudentColor(string templateId, StarsDatablockColor student, ScopeBlockColor expected) =>
        Assert.Equal(
            expected,
            BlockColorOf(
                templateId,
                ac => ac.StudentDatablockColor = student,
                Display(true, null, false, false),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_StudentNone_IsDefault(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.Default,
            BlockColorOf(
                templateId,
                ac => ac.StudentDatablockColor = null,
                Display(true, null, false, false),
                isHighlighted: false,
                isSelected: false
            )
        );

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void BlockColor_SelectionIgnored(string templateId) =>
        Assert.Equal(
            ScopeBlockColor.Default,
            BlockColorOf(templateId, _ => { }, Display(true, null, false, false), isHighlighted: false, isSelected: true)
        );

    [Fact]
    public void CwtType_FiledTypeMismatch_CarriesTheTypeMismatchToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";
        ac.AircraftType = "B738";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal(ScopeColorToken.TypeMismatch, Field(p, ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void CwtType_SuffixOnlyDifference_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B738/L";
        ac.AircraftType = "B738";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId));

        Assert.Equal(ScopeColorToken.Default, Field(p, ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void CwtType_HintOff_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";
        TrackPresenterInput input = ScopeTestAircraft.Input(ac, ScopeTemplates.StarsId) with
        {
            Display = ScopeTestAircraft.AllDisplay with { ShowTypeMismatchHints = false },
        };

        Assert.Equal(ScopeColorToken.Default, Field(TrackPresenter.Present(input), ScopeFieldSlot.CwtType).Token);
    }

    [Fact]
    public void EuroScopeTypeCwt_FiledTypeMismatch_KeepsTheDefaultToken()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.FiledAircraftType = "B744";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EuroScopeId));

        Assert.Equal(ScopeColorToken.Default, Field(p, ScopeFieldSlot.TypeCwt).Token);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Squawk_Mismatch_DimsTheTailOnlyOffPhase_WithTheSameText(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateSquawkMismatch();

        ScopeField on = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: true)), ScopeFieldSlot.Squawk);
        ScopeField off = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: false)), ScopeFieldSlot.Squawk);

        Assert.Equal("1200 0301", on.Text);
        Assert.Equal(on.Text, off.Text);
        Assert.Equal(on.StableText, off.StableText);
        Assert.Equal(ScopeFieldStyle.None, on.Style);
        Assert.Equal(ScopeFieldStyle.DimTail, off.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Squawk_NoMismatch_HasNoStyle(string templateId)
    {
        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), templateId, flashOn: false));

        Assert.Equal(ScopeFieldStyle.None, Field(p, ScopeFieldSlot.Squawk).Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Squawk_Mismatch_IsHidden_WhileCommandedSquawkVfr(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateSquawkMismatch();
        ac.CommandedSquawkVfr = true;

        foreach (bool flashOn in new[] { true, false })
        {
            ScopeField squawk = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn: flashOn)), ScopeFieldSlot.Squawk);

            Assert.Equal("", squawk.Text);
            Assert.Equal(ScopeFieldStyle.None, squawk.Style);
        }
    }

    [Theory]
    [InlineData(ScopeTemplates.StarsId, true)]
    [InlineData(ScopeTemplates.StarsId, false)]
    [InlineData(ScopeTemplates.EuroScopeId, true)]
    [InlineData(ScopeTemplates.EuroScopeId, false)]
    public void ModeC_Standby_IsStruckThroughInBothPhases(string templateId, bool flashOn)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "Standby";

        ScopeField modeC = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId, flashOn)), ScopeFieldSlot.ModeC);

        Assert.Equal("ModeC", modeC.Text);
        Assert.Equal(ScopeFieldStyle.Strikethrough, modeC.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void ModeC_TransponderOn_HasNoStyle(string templateId)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = "C";

        ScopeField modeC = Field(TrackPresenter.Present(ScopeTestAircraft.Input(ac, templateId)), ScopeFieldSlot.ModeC);

        Assert.Equal(ScopeFieldStyle.None, modeC.Style);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaBelow_CarriesTheMvaBelowToken(string templateId) =>
        Assert.Equal(ScopeColorToken.MvaBelow, AltitudeTokenFor(templateId, MvaRelation.Below));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaAt_CarriesTheMvaAtToken(string templateId) =>
        Assert.Equal(ScopeColorToken.MvaAt, AltitudeTokenFor(templateId, MvaRelation.At));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaAbove_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, MvaRelation.Above));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_MvaNoData_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, MvaRelation.NoData));

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Altitude_NoMvaRelation_KeepsTheDefaultToken(string templateId) =>
        Assert.Equal(ScopeColorToken.Default, AltitudeTokenFor(templateId, null));

    [Fact]
    public void EuroScopeAssignedAltitude_MvaBelow_KeepsTheDefaultToken()
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), ScopeTemplates.EuroScopeId) with
        {
            Mva = MvaRelation.Below,
        };

        Assert.Equal(ScopeColorToken.Default, Field(TrackPresenter.Present(input), ScopeFieldSlot.AssignedAltitude).Token);
    }

    private static ScopeColorToken AltitudeTokenFor(string templateId, MvaRelation? mva)
    {
        TrackPresenterInput input = ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), templateId) with { Mva = mva };
        return Field(TrackPresenter.Present(input), ScopeFieldSlot.Altitude).Token;
    }

    public static TheoryData<string> TemplateIds => [ScopeTemplates.StarsId, ScopeTemplates.EuroScopeId];

    [Fact]
    public void UnknownTemplateId_Throws() =>
        Assert.Throws<ArgumentException>(() => TrackPresenter.Present(ScopeTestAircraft.Input(ScopeTestAircraft.CreateModel(), "nope")));

    /// <summary>A STARS viewer that owns no test track, so the others' level applies.</summary>
    private static readonly ScopeViewer OtherStarsViewer = new(new ActingPosition("OAK_APP", "NCT", "2S"), "NCT", null);

    /// <summary>DAL385, an A321 on the ground filed to KATL and squawking 4521, as CRC's ground data block image shows it.</summary>
    private static AircraftModel GroundTarget() =>
        new()
        {
            Callsign = "DAL385",
            AircraftType = "A321",
            FiledAircraftType = "A321",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0),
            Altitude = 0,
            GroundSpeed = 0,
            CwtCode = "E",
            IsOnGround = true,
            Destination = "KATL",
            BeaconCode = 4521,
            TransponderMode = "C",
        };

    /// <summary>One frame of <paramref name="ac"/> alone, viewed from <see cref="OtherStarsViewer"/>.</summary>
    private static ScopeTrack TrackIn(string templateId, AircraftModel ac, bool showTopDown, string othersLevel, string? hovered)
    {
        using SKFont font = PlatformHelper.MonospaceFontBold(12f);
        using var paint = new SKPaint();
        var input = new ScopeFrameInput(
            [ac],
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
            ScopeTestAircraft.AllDisplay with
            {
                Viewer = OtherStarsViewer,
            },
            new Dictionary<string, SKPoint>(),
            new HashSet<string>(),
            MvaFacilityId: null,
            othersLevel,
            VectorMinutes: 0,
            VectorOwnership: ScopeVectorOwnership.Own,
            HistoryCount: 0,
            LocalRings: new Dictionary<string, ScopeTrackRings>(),
            IntentPaths: new ScopeIntentPathCache(),
            hovered,
            ShowTopDown: showTopDown
        );
        return Assert.Single(ScopeFrame.Build(input).Tracks);
    }

    private static ScopeTrack GroundTrackIn(string templateId, AircraftModel ac) => TrackIn(templateId, ac, true, ScopeProfile.OthersFull, null);

    /// <summary>The block's drawn rows, top to bottom, each its texts left to right joined by a space.</summary>
    private static List<string> Rows(ScopeTrack track) =>
        [
            .. track
                .Block.Fields.Where(f => f.Text.Length > 0)
                .GroupBy(f => f.Rect.Top)
                .OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(f => f.Rect.Left).Select(f => f.Text))),
        ];

    public static TheoryData<string> GroundTemplateIds => [ScopeTemplates.StarsId, ScopeTemplates.EramId];

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void GroundTarget_WithAFlightPlan_ShowsItsIdThenDestinationAndType(string templateId)
    {
        ScopeTrack track = GroundTrackIn(templateId, GroundTarget());

        Assert.Equal(ScopeBlockLevel.Ground, track.Level);
        Assert.Equal(["DAL385", "KATL A321"], Rows(track));
    }

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void GroundTarget_SquawkingWithNoFlightPlan_ShowsItsBeaconCodeThenType(string templateId)
    {
        AircraftModel ac = GroundTarget();
        ac.Destination = "";

        Assert.Equal(["DAL385", "4521 A321"], Rows(GroundTrackIn(templateId, ac)));
    }

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void GroundTarget_WithNeitherFlightPlanNorBeaconCode_ShowsItsTypeAlone(string templateId)
    {
        AircraftModel ac = GroundTarget();
        ac.Destination = "";
        ac.BeaconCode = 0;

        Assert.Equal(["DAL385", "A321"], Rows(GroundTrackIn(templateId, ac)));
    }

    /// <summary>The pilot's connection type, the filed type that differs from it, and the type the block shows.</summary>
    public static TheoryData<string, string, string, string> GroundTypes =>
        new()
        {
            { ScopeTemplates.StarsId, "A321", "A320", "A321" },
            { ScopeTemplates.EramId, "A321", "A320", "A321" },
            { ScopeTemplates.StarsId, "H/A332/L", "A333", "A332" },
            { ScopeTemplates.EramId, "H/A332/L", "A333", "A332" },
            { ScopeTemplates.StarsId, "B738/L", "B739", "B738" },
            { ScopeTemplates.EramId, "B738/L", "B739", "B738" },
        };

    [Theory]
    [MemberData(nameof(GroundTypes))]
    public void GroundTarget_ShowsThePilotsBareConnectionType_NotTheFiledType(string templateId, string type, string filed, string shown)
    {
        AircraftModel ac = GroundTarget();
        ac.AircraftType = type;
        ac.FiledAircraftType = filed;

        Assert.Equal(["DAL385", $"KATL {shown}"], Rows(GroundTrackIn(templateId, ac)));
    }

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void GroundAircraft_WithTdmOff_IsNoGroundBlock(string templateId)
    {
        ScopeTrack track = TrackIn(templateId, GroundTarget(), false, ScopeProfile.OthersFull, null);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.NotEqual(["DAL385", "KATL A321"], Rows(track));
    }

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void AirborneBelowTheFloor_WithTdmOn_KeepsTheOrdinaryBlock(string templateId)
    {
        AircraftModel ac = GroundTarget();
        ac.IsOnGround = false;
        ac.BelowDisplayFloor = true;
        ac.Altitude = 800;

        ScopeTrack track = GroundTrackIn(templateId, ac);

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.NotEqual(["DAL385", "KATL A321"], Rows(track));
    }

    [Fact]
    public void GroundTarget_OnEuroScope_KeepsItsOrdinaryTag()
    {
        ScopeTrack track = GroundTrackIn(ScopeTemplates.EuroScopeId, GroundTarget());

        Assert.Equal(ScopeBlockLevel.Full, track.Level);
        Assert.Equal("-- DAL385", Rows(track)[0]);
        Assert.True(Rows(track).Count > 2);
    }

    [Theory]
    [MemberData(nameof(GroundTemplateIds))]
    public void GroundTarget_KeepsItsTwoLines_AtFull_AtTheOthersPartialLevel_AndOnDwell(string templateId)
    {
        ScopeTrack full = GroundTrackIn(templateId, GroundTarget());
        ScopeTrack partial = TrackIn(templateId, GroundTarget(), true, ScopeProfile.OthersPartial, null);
        ScopeTrack dwelled = TrackIn(templateId, GroundTarget(), true, ScopeProfile.OthersFull, "DAL385");

        Assert.All([full, partial, dwelled], t => Assert.Equal(ScopeBlockLevel.Ground, t.Level));
        Assert.All([full, partial, dwelled], t => Assert.Equal(["DAL385", "KATL A321"], Rows(t)));
        Assert.False(full.IsDwelled);
        Assert.True(dwelled.IsDwelled);
    }
}

/// <summary>Shared aircraft setups and presenter inputs for the YAAT Scope presenter, engine and parity tests.</summary>
internal static class ScopeTestAircraft
{
    public static AircraftModel CreateModel()
    {
        return new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0),
            Altitude = 23000,
            GroundSpeed = 250,
            CwtCode = "D",
        };
    }

    /// <summary>ATPA lead 3.2 nm due east of <see cref="CreateModel"/>.</summary>
    public static AircraftModel CreateAtpaLead()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (4.00704 / 60.0)),
            Altitude = 23000,
            GroundSpeed = 250,
        };
    }

    /// <summary>Conflict peer 2.0 nm east of <see cref="CreateModel"/> and 800 ft below it.</summary>
    public static AircraftModel CreateConflictPeer()
    {
        return new AircraftModel
        {
            Callsign = "SWA1234",
            AircraftType = "B737",
            FiledAircraftType = "B737",
            FlightRules = "IFR",
            Position = new LatLon(37.0, -122.0 + (2.5044 / 60.0)),
            Altitude = 22200,
            GroundSpeed = 250,
            Owner = "CD",
        };
    }

    public static AircraftModel CreateAtpaTrailer()
    {
        AircraftModel ac = CreateModel();
        ac.AtpaLeadCallsign = "SWA1234";
        ac.AtpaAllowedSeparationNm = 3.0;
        ac.AtpaConeState = AtpaConeState.Warning;
        return ac;
    }

    /// <summary>An aircraft carrying every flashing field at once: handoff, ident, no-landing-clearance and conflict.</summary>
    public static AircraftModel CreateEverythingFlashing()
    {
        AircraftModel ac = CreateModel();
        ac.OwnerSectorCode = "2S";
        ac.HandoffPeerSectorCode = "3E";
        ac.IsIdenting = true;
        ac.NoLandingClearanceWarningActive = true;
        ac.ConflictPeerCallsign = "SWA1234";
        ac.Note = "Watch wake";
        return ac;
    }

    public static TrackPresenterInput Input(
        AircraftModel ac,
        string templateId,
        bool flashOn = true,
        AircraftModel? atpaLead = null,
        AircraftModel? conflictPeer = null
    ) => new(ac, templateId, atpaLead, conflictPeer, flashOn, IsSelected: false, AllAlerts, AllDisplay, Mva: null, IsHighlighted: false);

    /// <summary>Every alert overlay on, matching the flags the parity rows pass to today's layouts.</summary>
    public static readonly ScopeAlertOptions AllAlerts = new(
        ShowAtpa: true,
        ShowAtpaAlertCones: true,
        ShowAtpaMonitorCones: true,
        ShowConflictAlerts: true,
        ShowNoLandingClearance: true,
        ShowEramCaRing: true
    );

    /// <summary>
    /// Every display hint on and student colour sync on, as the preferences default; no initials, both tints off; the STARS field
    /// menus on.
    /// </summary>
    public static readonly ScopeDisplayOptions AllDisplay = new(
        MarkStudentLevel: true,
        ShowTypeMismatchHints: true,
        ShowMvaAltitudeTint: true,
        SyncStudentColors: true,
        UserInitials: null,
        AssignmentTint: false,
        UnassignedTint: false,
        Viewer: ScopeViewer.None,
        EramLine4Type: true,
        EramLine4Destination: true,
        StarsFieldMenus: true
    );

    /// <summary>A beacon-code mismatch: Mode C reporting 1200 while 0301 is assigned.</summary>
    public static AircraftModel CreateSquawkMismatch()
    {
        AircraftModel ac = CreateModel();
        ac.TransponderMode = "C";
        ac.BeaconCode = 1200;
        ac.AssignedBeaconCode = 301;
        return ac;
    }
}

/// <summary>
/// Classes that pin the process-wide <see cref="MapBlink.Clock"/>. Parallelization is off so no other test class
/// reads the blink while a pin is in place.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MapBlinkClockCollection
{
    public const string Name = "MapBlinkClock";
}

/// <summary>Pins <see cref="MapBlink.Clock"/> for a <c>using</c> scope and restores the previous clock on dispose.</summary>
internal sealed class MapBlinkPin : IDisposable
{
    public const long OnPhaseMs = 0;
    public const long OffPhaseMs = 500;

    private readonly Func<long> _previous;

    private MapBlinkPin(Func<long> clock)
    {
        _previous = MapBlink.Clock;
        MapBlink.Clock = clock;
    }

    public static MapBlinkPin To(Func<long> clock) => new(clock);

    public static MapBlinkPin Phase(bool on) => To(on ? static () => OnPhaseMs : static () => OffPhaseMs);

    public void Dispose() => MapBlink.Clock = _previous;
}
