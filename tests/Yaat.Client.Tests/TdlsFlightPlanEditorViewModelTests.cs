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
            InitialAlts: [new TdlsClearanceValueDto("5000", "5000")],
            DepFreqs: [new TdlsClearanceValueDto("120.9", "120.9")],
            Expects: [new TdlsClearanceValueDto("10MIN", "10 MIN")],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "OAKLAND4",
            DefaultTransitionId: "ALTAM"
        );

    [Fact]
    public void Constructor_AppliesTransitionDefaults_WhenNoSeed()
    {
        var editor = new TdlsFlightPlanEditorViewModel("N42416", BuildConfig(), seed: null, flightPlan: null);

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
    public void IsSendEnabled_False_WhenMandatoryFieldMissing()
    {
        var cfg = BuildConfig(mandatoryExpect: true);
        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed: null, flightPlan: null);

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

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed, flightPlan: null);

        Assert.Equal("20 MIN", editor.Expect);
        Assert.Equal("7000", editor.InitialAlt);
        Assert.Equal("121.4", editor.DepFreq);
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

        var editor = new TdlsFlightPlanEditorViewModel("SWA1905", cfg, seed: null, flightPlan: fp);

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

        var editor = new TdlsFlightPlanEditorViewModel("SWA1905", cfg, seed: null, flightPlan: fp);

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

        var editor = new TdlsFlightPlanEditorViewModel("N42416", cfg, seed: null, flightPlan: fp);

        // Falls back to the facility's DefaultSidId (and its default transition).
        Assert.Equal("OAKLAND4", editor.SelectedSid?.Id);
        Assert.Equal("ALTAM", editor.SelectedTransition?.Id);
    }

    [Fact]
    public void ToClearanceDto_RoundTripsCurrentEditorState()
    {
        var editor = new TdlsFlightPlanEditorViewModel("N42416", BuildConfig(), seed: null, flightPlan: null);

        var clearance = editor.ToClearanceDto();

        Assert.Equal("OAKLAND4", clearance.Sid);
        Assert.Equal("ALTAM", clearance.Transition);
        Assert.Equal("10 MIN", clearance.Expect);
        Assert.Equal("5000", clearance.InitialAlt);
        Assert.Equal("120.9", clearance.DepFreq);
    }
}
