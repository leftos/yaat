using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Core.Services;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A scenario restart clears the room's world and repopulates it from t=0, so most of the abandoned
/// run's aircraft are gone — back in the delayed-spawn queue, or, for generator traffic drawn against
/// the re-drawn RNG seed, never returning. None of that reaches the client on its own: the server's
/// teardown runs with broadcasts suppressed, so no <c>AircraftDeleted</c> is sent, and
/// <c>OnAircraftUpdated</c> only ever adds or updates. Left alone the scope keeps every vanished
/// aircraft, frozen where the abandoned run left it.
///
/// <c>OnScenarioRestarted</c> is the repair: the server broadcasts its post-restart manifest to the
/// whole room and the client replaces its list wholesale, the same shape the rewind path uses. These
/// tests drive <c>ApplyScenarioRestart</c>, the synchronous half, because the handler itself only
/// marshals onto the UI thread.
/// </summary>
public class MainViewModelScenarioRestartTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static AircraftDto MakeAircraft(string callsign, string status) =>
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
            Status: status
        );

    [AvaloniaFact]
    public void OnScenarioRestarted_DropsAircraftMissingFromTheManifest()
    {
        var vm = NewVm();
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap(
                "scenario-1",
                "OAK Ground",
                "OAK",
                null,
                null,
                [MakeAircraft("SWA101", "Active"), MakeAircraft("UAL202", "Active"), MakeAircraft("FDX303", "Active")]
            )
        );
        Assert.Equal(3, vm.Aircraft.Count);

        // The restart keeps SWA101 live, puts UAL202 back in the delayed queue, and drops FDX303
        // entirely (generator traffic drawn against the old seed).
        vm.ApplyScenarioRestart([MakeAircraft("SWA101", "Active"), MakeAircraft("UAL202", "Delayed (120s)")]);

        Assert.Equal(["SWA101", "UAL202"], vm.Aircraft.Select(a => a.Callsign).OrderBy(c => c, StringComparer.Ordinal));
        Assert.DoesNotContain(vm.Aircraft, a => a.Callsign == "FDX303");
    }

    [AvaloniaFact]
    public void OnScenarioRestarted_RestoresStaleAircraftToTheirManifestState()
    {
        var vm = NewVm();
        vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-1", "OAK Ground", "OAK", null, null, [MakeAircraft("UAL202", "Active")]));
        Assert.False(vm.Aircraft[0].IsDelayed);

        // Same callsign, but the restart re-queued it — a merge would have left it showing Active at
        // its abandoned-run position.
        vm.ApplyScenarioRestart([MakeAircraft("UAL202", "Delayed (120s)")]);

        var restored = Assert.Single(vm.Aircraft);
        Assert.Equal("UAL202", restored.Callsign);
        Assert.True(restored.IsDelayed);
    }

    /// <summary>
    /// The pending-spawn indicator counts down as delayed aircraft appear. A restart re-queues them,
    /// so both counters have to be recomputed from the manifest or the badge reads for the run that
    /// was abandoned.
    /// </summary>
    [AvaloniaFact]
    public void OnScenarioRestarted_RecomputesDelayedSpawnCounters()
    {
        var vm = NewVm();
        vm.ApplyScenarioBootstrap(new ScenarioBootstrap("scenario-1", "OAK Ground", "OAK", null, null, [MakeAircraft("SWA101", "Active")]));
        Assert.Equal(0, vm.InitialDelayedSpawnCount);
        Assert.Equal(0, vm.PendingDelayedSpawnCount);

        vm.ApplyScenarioRestart([MakeAircraft("SWA101", "Delayed (60s)"), MakeAircraft("UAL202", "Delayed (120s)")]);

        Assert.Equal(2, vm.InitialDelayedSpawnCount);
        Assert.Equal(2, vm.PendingDelayedSpawnCount);
    }
}
