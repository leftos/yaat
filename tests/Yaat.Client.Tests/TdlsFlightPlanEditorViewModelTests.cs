using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers <see cref="TdlsFlightPlanEditorViewModel"/> mandatory-field gating and
/// SID/transition default propagation. Construction time, dropdown selection,
/// and clearance round-trips are the surface the Avalonia view binds against —
/// keeping them green ensures the editor's Send button behaves the way the
/// upstream vTDLS docs describe ("CLEARANCE TYPE: PDC" vs "MANDATORY FIELD NOT SET").
/// </summary>
public class TdlsFlightPlanEditorViewModelTests
{
    private static TdlsConfigDto BuildConfig(bool mandatorySid = true, bool mandatoryExpect = true, bool mandatoryDepFreq = true) =>
        new(
            FacilityId: "OAK",
            FacilityName: "Oakland ATCT",
            MandatorySid: mandatorySid,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: mandatoryDepFreq,
            MandatoryExpect: mandatoryExpect,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "OAKLAND4",
                    "OAKLAND4",
                    [
                        new TdlsSidTransitionDto(
                            "ALTAM",
                            "ALTAMONT",
                            FirstRoutePoint: "ALTAM",
                            DefaultExpect: "10 MIN",
                            DefaultClimbout: null,
                            DefaultClimbvia: null,
                            DefaultInitialAlt: "5000",
                            DefaultDepFreq: "120.9",
                            DefaultContactInfo: null,
                            DefaultLocalInfo: null
                        ),
                    ]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            // Multiple entries per field so the seed/hand-edited values used
            // by Seed_NonNullClearanceSkipsTransitionDefaults find a matching
            // dropdown item. Upstream rule: "Only values predefined by the
            // Facility Engineer may be selected" — values not in the config
            // are dropped on the way in, which is enforced now that we bind
            // SelectedItem (not a free string).
            InitialAlts: [new TdlsClearanceValueDto("5000", "5000"), new TdlsClearanceValueDto("7000", "7000")],
            DepFreqs: [new TdlsClearanceValueDto("120.9", "120.9"), new TdlsClearanceValueDto("121.4", "121.4")],
            Expects: [new TdlsClearanceValueDto("10MIN", "10 MIN"), new TdlsClearanceValueDto("20MIN", "20 MIN")],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "OAKLAND4",
            DefaultTransitionId: "ALTAM"
        );

    /// <summary>
    /// Mirrors the real KIAD facility shape from GitHub issue #306: a single mandatory
    /// departure frequency whose value list stores three decimal places while the
    /// transition default the Facility Engineer typed carries two. KRNO in the committed
    /// ZOA snapshot has the same shape ("119.2" against a "119.200" list entry).
    /// <paramref name="defaultDepFreq"/> is the transition's default; pass null to model a
    /// facility that defines no default at all for a mandatory field.
    /// </summary>
    private static TdlsConfigDto BuildIadLikeConfig(string? defaultDepFreq) =>
        new(
            FacilityId: "IAD",
            FacilityName: "Washington Dulles ATCT",
            MandatorySid: true,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: true,
            MandatoryDepFreq: true,
            MandatoryExpect: true,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "RNLDI4",
                    "RNLDI4",
                    [
                        new TdlsSidTransitionDto(
                            "OTTTO",
                            "OTTTO",
                            FirstRoutePoint: "OTTTO",
                            DefaultExpect: "10 MIN AFT DP",
                            DefaultClimbout: null,
                            DefaultClimbvia: null,
                            DefaultInitialAlt: "3000FT",
                            DefaultDepFreq: defaultDepFreq,
                            DefaultContactInfo: "CTC ATC AT W SIDE OF RAMP",
                            DefaultLocalInfo: null
                        ),
                    ]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [new TdlsClearanceValueDto("3000FT", "3000FT")],
            DepFreqs:
            [
                new TdlsClearanceValueDto("125050", "125.050"),
                new TdlsClearanceValueDto("126650", "126.650"),
                new TdlsClearanceValueDto("none", "- - - -"),
            ],
            Expects: [new TdlsClearanceValueDto("10MIN", "10 MIN AFT DP")],
            ContactInfos: [new TdlsClearanceValueDto("west", "CTC ATC AT W SIDE OF RAMP")],
            LocalInfos: [],
            DefaultSidId: "RNLDI4",
            DefaultTransitionId: "OTTTO"
        );

    [Fact]
    public void Constructor_ResolvesTransitionDefault_WhenListUsesDifferentNumericForm()
    {
        // Issue #306: every KIAD transition defaults DepFreq to "125.05" while the
        // facility's depFreqs list holds "125.050". Exact-ordinal matching left the
        // dropdown blank, and with MandatoryDepFreq set the clearance could never
        // be completed. The default must resolve to the list's equivalent entry.
        var editor = new TdlsFlightPlanEditorViewModel(
            "UAL1742",
            BuildIadLikeConfig("125.05"),
            seed: null,
            flightPlan: null,
            isReadOnly: false,
            opConfigId: null
        );

        Assert.Equal("125.050", editor.SelectedDepFreq?.Value);
        Assert.True(editor.IsSendEnabled);
        Assert.Equal("", editor.MissingMandatoryFieldNames);
    }

    [Fact]
    public void Constructor_LeavesFieldBlank_WhenDefaultMatchesNoListEntry()
    {
        // Normalization must not turn into guessing: a default that resolves to
        // nothing leaves the field unset (upstream's "MANDATORY FIELD NOT SET"),
        // rather than auto-picking the first list entry.
        var editor = new TdlsFlightPlanEditorViewModel(
            "UAL1742",
            BuildIadLikeConfig("118.375"),
            seed: null,
            flightPlan: null,
            isReadOnly: false,
            opConfigId: null
        );

        Assert.Null(editor.SelectedDepFreq);
        Assert.False(editor.IsSendEnabled);
        Assert.Contains("Departure frequency", editor.MissingMandatoryFieldNames);
    }

    [Fact]
    public void FillingLastMandatoryField_RaisesSendCommandCanExecuteChanged()
    {
        // Issue #306, second half: the Send button binds both Command and IsEnabled.
        // Avalonia's Button caches the command's CanExecute verdict and only refreshes
        // it from CanExecuteChanged, so without that notification the button latched
        // disabled and picking a frequency never brought it back.
        var editor = new TdlsFlightPlanEditorViewModel(
            "UAL1742",
            BuildIadLikeConfig(defaultDepFreq: null),
            seed: null,
            flightPlan: null,
            isReadOnly: false,
            opConfigId: null
        );
        Assert.False(editor.IsSendEnabled);

        var notifications = 0;
        editor.SendCommand.CanExecuteChanged += (_, _) => notifications++;

        editor.SelectedDepFreq = editor.DepFreqs[0];

        Assert.True(editor.IsSendEnabled);
        Assert.True(notifications > 0, "SendCommand.CanExecuteChanged never fired — a bound Button stays greyed out.");
    }

    [Fact]
    public void Constructor_AppliesTransitionDefaults_WhenNoSeed()
    {
        var editor = new TdlsFlightPlanEditorViewModel("N42416", BuildConfig(), seed: null, flightPlan: null, isReadOnly: false, opConfigId: null);

        // The constructor seeds SelectedSid + SelectedTransition from the
        // facility defaults. Transition defaults apply during construction so
        // the editor opens with the FE-defined values pre-populated.
        Assert.Equal("OAKLAND4", editor.SelectedSid?.Id);
        Assert.Equal("ALTAM", editor.SelectedTransition?.Id);
        Assert.Equal("10 MIN", editor.Expect);
        Assert.Equal("5000", editor.InitialAlt);
        Assert.Equal("120.9", editor.DepFreq);
        Assert.True(editor.IsSendEnabled);
    }

    [Fact]
    public void Constructor_PrePopulatedMandatoryField_DoesNotReportMissing()
    {
        // Regression for the Avalonia ComboBox SelectedValue/SelectedValueBinding
        // bug: the dropdown displayed "120.9" but the VM thought DepFreq was empty,
        // surfacing as "MANDATORY FIELD NOT SET — Departure frequency" with the
        // Send button greyed out. After the SelectedItem refactor, an item picked
        // by ApplyTransitionDefaults registers as set immediately.
        var cfg = BuildConfig(mandatorySid: true, mandatoryExpect: true, mandatoryDepFreq: true);
        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed: null, flightPlan: null, isReadOnly: false, opConfigId: null);

        Assert.NotNull(editor.SelectedDepFreq);
        Assert.Equal("120.9", editor.SelectedDepFreq?.Value);
        Assert.True(editor.IsSendEnabled);
        Assert.DoesNotContain("Departure frequency", editor.MissingMandatoryFieldNames);
    }

    [Fact]
    public void IsSendEnabled_False_WhenMandatoryFieldMissing()
    {
        var cfg = BuildConfig(mandatoryExpect: true);
        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed: null, flightPlan: null, isReadOnly: false, opConfigId: null);

        // Wipe Expect — that's mandatory in this config; Send must lock.
        editor.Expect = null;

        Assert.False(editor.IsSendEnabled);
        Assert.Contains("Expect", editor.MissingMandatoryFieldNames);
    }

    [Fact]
    public void Seed_NonNullClearanceSkipsTransitionDefaults()
    {
        // When a seed already has values, the constructor must NOT overwrite
        // them with the transition's defaults (controller's hand-edits win).
        var cfg = BuildConfig();
        var seed = new ClearanceDto(
            Expect: "20 MIN",
            Sid: "OAKLAND4",
            Transition: "ALTAM",
            Climbout: null,
            Climbvia: null,
            InitialAlt: "7000",
            ContactInfo: null,
            LocalInfo: null,
            DepFreq: "121.4"
        );

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed, flightPlan: null, isReadOnly: false, opConfigId: null);

        Assert.Equal("20 MIN", editor.Expect);
        Assert.Equal("7000", editor.InitialAlt);
        Assert.Equal("121.4", editor.DepFreq);
    }

    [Fact]
    public void ReadOnly_SeededFromSentClearance_IsNotEditableAndCannotSend()
    {
        // Selecting a Sent PDC opens the editor read-only, seeded from the
        // issued clearance. Every field reflects the seed, the panel reports
        // itself non-editable, and Send is locked even though all mandatory
        // fields are populated.
        var cfg = BuildConfig();
        var seed = new ClearanceDto(
            Expect: "20 MIN",
            Sid: "OAKLAND4",
            Transition: "ALTAM",
            Climbout: null,
            Climbvia: null,
            InitialAlt: "7000",
            ContactInfo: null,
            LocalInfo: null,
            DepFreq: "121.4"
        );

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed, flightPlan: null, isReadOnly: true, opConfigId: null);

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.IsEditable);
        Assert.Equal("20 MIN", editor.Expect);
        Assert.Equal("7000", editor.InitialAlt);
        Assert.Equal("121.4", editor.DepFreq);
        // Mandatory fields are all set, but read-only still blocks Send/F12.
        Assert.False(editor.IsSendEnabled);
    }

    [Fact]
    public void ReadOnly_DoesNotBackfillTransitionDefaultsIntoBlankSeedFields()
    {
        // A sent PDC issued with a blank Expect must keep it blank in review —
        // read-only construction skips ApplyTransitionDefaults, so the FE
        // default ("10 MIN") is NOT pulled in.
        var cfg = BuildConfig();
        var seed = new ClearanceDto(
            Expect: null,
            Sid: "OAKLAND4",
            Transition: "ALTAM",
            Climbout: null,
            Climbvia: null,
            InitialAlt: null,
            ContactInfo: null,
            LocalInfo: null,
            DepFreq: null
        );

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed, flightPlan: null, isReadOnly: true, opConfigId: null);

        Assert.Null(editor.Expect);
        Assert.Null(editor.InitialAlt);
        Assert.Null(editor.DepFreq);
    }

    [Fact]
    public void FiledRoute_PrepopulatesSidAndTransition_FromLeadingTokens()
    {
        // "OAK6 OAK V107 LAX" — leading token matches SID name; second token
        // matches transition FirstRoutePoint. Both dropdowns should snap to
        // the filed values without any explicit seed.
        var cfg = BuildConfig();
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 501,
            Departure: "KOAK",
            Destination: "KLAX",
            Route: "OAKLAND4 ALTAM V107 LAX",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "1234567",
            CruiseAltitude: 35000
        );

        var editor = new TdlsFlightPlanEditorViewModel("SWA1905", cfg, seed: null, flightPlan: fp, isReadOnly: false, opConfigId: null);

        Assert.Equal("OAKLAND4", editor.SelectedSid?.Id);
        Assert.Equal("ALTAM", editor.SelectedTransition?.Id);
    }

    [Fact]
    public void FiledRoute_DottedSidTransitionForm_AlsoMatches()
    {
        // "OAKLAND4.ALTAM V107 LAX" — common ATC route notation for SID+transition.
        var cfg = BuildConfig();
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 501,
            Departure: "KOAK",
            Destination: "KLAX",
            Route: "OAKLAND4.ALTAM V107 LAX",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "1234567",
            CruiseAltitude: 35000
        );

        var editor = new TdlsFlightPlanEditorViewModel("SWA1905", cfg, seed: null, flightPlan: fp, isReadOnly: false, opConfigId: null);

        Assert.Equal("OAKLAND4", editor.SelectedSid?.Id);
        Assert.Equal("ALTAM", editor.SelectedTransition?.Id);
    }

    [Fact]
    public void FiledRoute_UnknownSidFallsBackToDefault()
    {
        var cfg = BuildConfig();
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: null,
            Departure: "KOAK",
            Destination: "KLAX",
            Route: "NOTAREAL4 V107 LAX",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "",
            CruiseAltitude: 0
        );

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed: null, flightPlan: fp, isReadOnly: false, opConfigId: null);

        // Falls back to the facility's DefaultSidId (and its default transition).
        Assert.Equal("OAKLAND4", editor.SelectedSid?.Id);
        Assert.Equal("ALTAM", editor.SelectedTransition?.Id);
    }

    [Fact]
    public void ToClearanceDto_RoundTripsCurrentEditorState()
    {
        var editor = new TdlsFlightPlanEditorViewModel("N42416", BuildConfig(), seed: null, flightPlan: null, isReadOnly: false, opConfigId: null);

        var clearance = editor.ToClearanceDto();

        Assert.Equal("OAKLAND4", clearance.Sid);
        Assert.Equal("ALTAM", clearance.Transition);
        Assert.Equal("10 MIN", clearance.Expect);
        Assert.Equal("5000", clearance.InitialAlt);
        Assert.Equal("120.9", clearance.DepFreq);
    }

    /// <summary>
    /// Mirrors the KSFO shape in the committed ZOA config: transitions that share a name across SIDs
    /// while <c>firstRoutePoint</c> is filled in for some and omitted for others. TRUKN2's ORRCA
    /// transition carries <c>firstRoutePoint</c>, SNTNA2's identically-named one does not, OFFSH9's
    /// points at the fix *after* the transition fix, and WESLA3 lists a name hit ahead of a
    /// first-route-point hit so match precedence is observable. <paramref name="noTransitionName"/>
    /// is the FE's "no transition" placeholder name on SNTNA2 (the real config writes "- - - -").
    /// </summary>
    private static TdlsConfigDto BuildSfoLikeConfig(string noTransitionName) =>
        new(
            FacilityId: "SFO",
            FacilityName: "San Francisco ATCT",
            MandatorySid: false,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: false,
            MandatoryExpect: false,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto("TRUKN2", "TRUKN2", [Transition("TRUKN2-PORTE", "PORTE", "PORTE"), Transition("TRUKN2-ORRCA", "ORRCA", "ORRCA")]),
                new TdlsSidDto(
                    "SNTNA2",
                    "SNTNA2",
                    [
                        Transition("SNTNA2-PORTE", "PORTE", firstRoutePoint: null),
                        Transition("SNTNA2-ORRCA", "ORRCA", firstRoutePoint: null),
                        Transition("SNTNA2-NONE", noTransitionName, firstRoutePoint: null),
                    ]
                ),
                new TdlsSidDto("OFFSH9", "OFFSH9", [Transition("OFFSH9-PORTE", "PORTE", "PORTE"), Transition("OFFSH9-ORRCA", "ORRCA", "STOKD")]),
                new TdlsSidDto("WESLA3", "WESLA3", [Transition("WESLA3-NAMEHIT", "ORRCA", "STOKD"), Transition("WESLA3-POINTHIT", "BRIXX", "ORRCA")]),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [],
            DepFreqs: [],
            Expects: [],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "TRUKN2",
            DefaultTransitionId: "TRUKN2-PORTE"
        );

    private static TdlsSidTransitionDto Transition(string id, string name, string? firstRoutePoint) =>
        new(
            id,
            name,
            firstRoutePoint,
            DefaultExpect: null,
            DefaultClimbout: null,
            DefaultClimbvia: null,
            DefaultInitialAlt: null,
            DefaultDepFreq: null,
            DefaultContactInfo: null,
            DefaultLocalInfo: null
        );

    [Fact]
    public void FiledRoute_MatchesTransitionByName_WhenFirstRoutePointIsMissing()
    {
        // The reported bug: "TRUKN2 ORRCA" auto-filled the transition and "SNTNA2 ORRCA" did not,
        // because the ZOA config sets firstRoutePoint on TRUKN2's ORRCA transition and omits it on
        // SNTNA2's even though both transitions are named ORRCA.
        var cfg = BuildSfoLikeConfig("- - - -");

        var (sidId, transitionId) = TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute(cfg, "SNTNA2 ORRCA STOKD KEEDS");

        Assert.Equal("SNTNA2", sidId);
        Assert.Equal("SNTNA2-ORRCA", transitionId);
    }

    [Fact]
    public void FiledRoute_MatchesTransitionByName_WhenFirstRoutePointNamesADifferentFix()
    {
        // OFFSH9's ORRCA transition has firstRoutePoint = "STOKD" (the FE pointed it at the fix
        // after the transition fix). The route names the transition, so the name pass must catch it
        // rather than leaving the dropdown on the SID's first transition.
        var cfg = BuildSfoLikeConfig("- - - -");

        var (sidId, transitionId) = TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute(cfg, "OFFSH9 ORRCA KEEDS");

        Assert.Equal("OFFSH9", sidId);
        Assert.Equal("OFFSH9-ORRCA", transitionId);
    }

    [Fact]
    public void FiledRoute_FirstRoutePointMatchWinsOverNameMatch()
    {
        // WESLA3 lists a transition *named* ORRCA before one whose firstRoutePoint is ORRCA. The
        // FE's explicit entry-fix mapping stays authoritative: firstRoutePoint is matched across
        // every transition before any name comparison runs.
        var cfg = BuildSfoLikeConfig("- - - -");

        var (sidId, transitionId) = TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute(cfg, "WESLA3 ORRCA MOVDD");

        Assert.Equal("WESLA3", sidId);
        Assert.Equal("WESLA3-POINTHIT", transitionId);
    }

    [Theory]
    [InlineData("- - - -")]
    [InlineData("----")]
    [InlineData("-")]
    public void FiledRoute_NeverMatchesTheNoTransitionPlaceholderByName(string noTransitionName)
    {
        // The "no transition" entry's name is dashes, not a fix. A route token made of dashes must
        // never select it — the SID still matches, the transition stays unresolved.
        var cfg = BuildSfoLikeConfig(noTransitionName);

        var (sidId, transitionId) = TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute(cfg, $"SNTNA2 {noTransitionName} OSI");

        Assert.Equal("SNTNA2", sidId);
        Assert.Null(transitionId);
    }

    [Fact]
    public void Constructor_FiledRouteWithoutFirstRoutePoint_SelectsNamedTransition()
    {
        // End of the same path through the editor the vTDLS view binds: the filed SNTNA2 ORRCA
        // route snaps both dropdowns without an explicit seed.
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 501,
            Departure: "KSFO",
            Destination: "KSEA",
            Route: "SNTNA2 ORRCA STOKD KEEDS",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "1234567",
            CruiseAltitude: 35000
        );

        var editor = new TdlsFlightPlanEditorViewModel(
            "ASA1234",
            BuildSfoLikeConfig("- - - -"),
            seed: null,
            flightPlan: fp,
            isReadOnly: false,
            opConfigId: null
        );

        Assert.Equal("SNTNA2", editor.SelectedSid?.Id);
        Assert.Equal("SNTNA2-ORRCA", editor.SelectedTransition?.Id);
    }

    [Fact]
    public void ApplyAmendedFlightPlan_ReseedsSidAndTransition_WhenTheRouteChanged()
    {
        // What an open editor gets when an amendment lands: the header follows the new route, and the SID and
        // transition the route derives follow it rather than staying on what the aircraft was filed with.
        var cfg = BuildSfoLikeConfig("- - - -");
        var fp = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 501,
            Departure: "KSFO",
            Destination: "KSEA",
            Route: "SNTNA2 ORRCA STOKD KEEDS",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "1234567",
            CruiseAltitude: 35000
        );
        var editor = new TdlsFlightPlanEditorViewModel("ASA1234", cfg, seed: null, flightPlan: fp, isReadOnly: false, opConfigId: null);
        Assert.Equal("SNTNA2", editor.SelectedSid?.Id);
        Assert.Equal("SNTNA2-ORRCA", editor.SelectedTransition?.Id);

        editor.ApplyAmendedFlightPlan(fp with { Route = "OFFSH9 ORRCA KEEDS" });

        Assert.Equal("OFFSH9 ORRCA KEEDS", editor.FlightPlan?.Route);
        Assert.Equal("OFFSH9", editor.SelectedSid?.Id);
        Assert.Equal("OFFSH9-ORRCA", editor.SelectedTransition?.Id);
    }
}
