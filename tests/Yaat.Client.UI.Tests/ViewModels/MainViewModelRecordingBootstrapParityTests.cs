using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Guards the scenario-identity contract between the two client entry points that
/// activate a scenario from server data:
/// <list type="bullet">
/// <item><see cref="MainViewModel.ApplyScenarioBootstrap"/> — the shared router used by the
/// loader RPC, the other-client broadcast, and the JoinRoom snapshot.</item>
/// <item><see cref="MainViewModel.ApplyRecordingResult"/> — the recording-load / rewind path,
/// which deliberately does <em>not</em> go through the router.</item>
/// </list>
/// Because the recording path re-implements setup inline, a new scenario-derived field wired
/// into the router could silently fail to apply after a recording load. This test pins the
/// shared scenario-identity surface so that divergence fails CI.
///
/// Deliberately NOT asserted (intentional divergence — rewind reloads the same scenario, so it
/// owns its own layout lifecycle): the <c>_preferences.SetScenarioName</c> side effect, the
/// delayed-spawn counters, and the Radar/Ground/VStrips/vTDLS sub-VM bootstrap fan-outs. If you
/// add a new <em>scalar identity</em> field to the router, extend the assertions below.
/// </summary>
public class MainViewModelRecordingBootstrapParityTests
{
    private const string ScenarioId = "scenario-7";
    private const string ScenarioName = "OAK Ground 7";
    private const string AirportId = "OAK";

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static AircraftDto MakeAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 0,
            GroundSpeed: 0,
            BeaconCode: 1200,
            TransponderMode: "Standby",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: "LAX",
            Route: "",
            FlightRules: "IFR",
            Status: "Active"
        );

    [AvaloniaFact]
    public void ApplyRecordingResult_MatchesScenarioBootstrap_OnSharedScenarioIdentityFields()
    {
        List<AircraftDto> aircraft = [MakeAircraft("SWA101"), MakeAircraft("UAL202")];

        MainViewModel viaBootstrap = NewVm();
        viaBootstrap.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = ScenarioId,
                ScenarioName = ScenarioName,
                PrimaryAirportId = AirportId,
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = aircraft,
                ElapsedSeconds = 0,
            }
        );

        MainViewModel viaRecording = NewVm();
        viaRecording.ApplyRecordingResult(
            new RewindResultDto(
                Success: true,
                Error: null,
                ActiveRunways: [],
                ToweredAirports: [],
                Aircraft: aircraft,
                ScenarioId: ScenarioId,
                ScenarioName: ScenarioName,
                PrimaryAirportId: AirportId
            )
        );

        // Scalar identity must propagate identically on both paths.
        Assert.Equal(ScenarioId, viaRecording.ActiveScenarioId);
        Assert.Equal(viaBootstrap.ActiveScenarioId, viaRecording.ActiveScenarioId);

        Assert.Equal(ScenarioName, viaRecording.ActiveScenarioName);
        Assert.Equal(viaBootstrap.ActiveScenarioName, viaRecording.ActiveScenarioName);

        // PrimaryAirportId is normalized the same way on both paths (NormalizeFavoriteAirportId).
        Assert.Equal(viaBootstrap.ActiveScenarioPrimaryAirportId, viaRecording.ActiveScenarioPrimaryAirportId);

        // Aircraft collection is rebuilt from the same DTO list on both paths.
        Assert.Equal(viaBootstrap.Aircraft.Count, viaRecording.Aircraft.Count);
        Assert.Equal(viaBootstrap.Aircraft.Select(a => a.Callsign), viaRecording.Aircraft.Select(a => a.Callsign));
    }

    [AvaloniaFact]
    public void ApplyRecordingResult_KeysTheGroundViewsSavedViewOnTheRecordingsScenario()
    {
        MainViewModel vm = NewVm();
        vm.ApplyRecordingResult(
            new RewindResultDto(
                Success: true,
                Error: null,
                ActiveRunways: [],
                ToweredAirports: [],
                Aircraft: [],
                ScenarioId: ScenarioId,
                ScenarioName: ScenarioName,
                PrimaryAirportId: AirportId
            )
        );

        // The Ground View restores and saves its center/zoom per scenario, so a recording load has to
        // tell it which scenario it is on, exactly as the bootstrap router does.
        Assert.Equal(ScenarioId, vm.Ground.ActiveScenarioId);
    }

    // --- After a recording load the "No landing clnc" flag follows the tape's session, not the preference (YAAT-252) ---

    private static SessionSettingsDto TapeSettings(bool autoClearedToLand) =>
        new(
            AutoDeleteOverride: null,
            EffectiveAutoDeleteMode: null,
            DepartureAutoDeleteDistanceNm: null,
            AutoAcceptDelaySeconds: 5,
            AutoClearedToLand: autoClearedToLand,
            AutoCrossRunway: false,
            AutoPullUpToParallel: false,
            AutoGoAroundOnOccupiedRunway: false,
            AutoRejectTakeoffOnOccupiedRunway: false,
            AutoArrivalSpacingOnOccupiedRunway: false,
            ValidateDctFixes: true,
            SoloTrainingMode: false,
            SoloParkingInitialCallupRatePercent: 100,
            SoloArrivalGeneratorRatePercent: 100,
            SoloGoAroundProbabilityPercent: 0,
            HasSoloParkingInitialCallupSource: false,
            HasSoloArrivalGeneratorSource: false,
            RpoShowPilotSpeech: false
        );

    private static RewindResultDto LoadedTape(bool autoClearedToLand) =>
        new(
            Success: true,
            Error: null,
            ActiveRunways: [],
            ToweredAirports: [],
            Aircraft: [MakeAircraft("SWA101")],
            ScenarioId: ScenarioId,
            ScenarioName: ScenarioName,
            PrimaryAirportId: AirportId,
            StudentPositionType: "TWR",
            IsPlayback: true,
            SessionSettings: TapeSettings(autoClearedToLand)
        );

    private static MainViewModel VmPreferringAutoClearedToLand()
    {
        MainViewModel vm = NewVm();
        UserPreferences prefs = vm.Preferences;
        prefs.SetSimulationShortcuts(
            autoClearedToLandGnd: true,
            autoClearedToLandTwr: true,
            autoClearedToLandApp: true,
            autoClearedToLandCtr: true,
            autoCrossRunway: prefs.AutoCrossRunway,
            autoPullUpToParallel: prefs.AutoPullUpToParallel,
            autoGoAroundOnOccupiedRunway: prefs.AutoGoAroundOnOccupiedRunway,
            autoRejectTakeoffOnOccupiedRunway: prefs.AutoRejectTakeoffOnOccupiedRunway,
            autoArrivalSpacingOnOccupiedRunwayGnd: prefs.AutoArrivalSpacingOnOccupiedRunwayGnd,
            autoArrivalSpacingOnOccupiedRunwayTwr: prefs.AutoArrivalSpacingOnOccupiedRunwayTwr
        );
        return vm;
    }

    /// <summary>The loader: the tape's settings broadcast landed first, then the load's result.</summary>
    [AvaloniaFact]
    public void ApplyRecordingResult_AfterTheSettingsBroadcast_FlagFollowsTheTapeNotThePreference()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = VmPreferringAutoClearedToLand();

        vm.ApplySessionSettings(TapeSettings(autoClearedToLand: false));
        vm.ApplyRecordingResult(LoadedTape(autoClearedToLand: false));

        Assert.False(Assert.Single(vm.Aircraft).IsAutoClearedToLand, "the tape's session has auto cleared-to-land off");
        Assert.False(vm.SessionAutoClearedToLand);
        Assert.True(vm.Preferences.GetAutoClearedToLand("TWR"), "a recording load must not write the preference");
    }

    /// <summary>Another room member: the RecordingLoaded result may land before the settings broadcast does.</summary>
    [AvaloniaFact]
    public void ApplyRecordingResult_BeforeTheSettingsBroadcast_FlagFollowsTheTapeNotThePreference()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = VmPreferringAutoClearedToLand();

        vm.ApplyRecordingResult(LoadedTape(autoClearedToLand: false));

        Assert.False(Assert.Single(vm.Aircraft).IsAutoClearedToLand, "the tape's session has auto cleared-to-land off");
        Assert.False(vm.SessionAutoClearedToLand);
        Assert.True(vm.Preferences.GetAutoClearedToLand("TWR"), "a recording load must not write the preference");
    }
}
