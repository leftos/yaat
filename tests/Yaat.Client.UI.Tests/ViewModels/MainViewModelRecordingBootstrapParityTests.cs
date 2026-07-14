using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
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

        var viaBootstrap = NewVm();
        viaBootstrap.ApplyScenarioBootstrap(new ScenarioBootstrap(ScenarioId, ScenarioName, AirportId, null, null, aircraft));

        var viaRecording = NewVm();
        viaRecording.ApplyRecordingResult(
            new RewindResultDto(
                Success: true,
                Error: null,
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
}
