using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// An aircraft in several STARS conflict pairs shows its most urgent one: the first, in broadcast order, that is unsuppressed
/// and unacknowledged, else unsuppressed and acknowledged, else the first suppressed one. The broadcast and the late-add seed
/// pick the same pair.
/// </summary>
public class MainViewModelConflictPairSelectionTests
{
    private static AircraftDto MakeAircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.62,
            Longitude = -122.22,
            Heading = 90,
            Altitude = 3_000,
            GroundSpeed = 180,
            BeaconCode = 1200,
            TransponderMode = "ModeC",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "SFO",
            Destination = "OAK",
            Route = "",
            FlightRules = "IFR",
            Status = "Active",
        };

    private static MainViewModel VmWith(params string[] callsigns)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(
            new ScenarioBootstrap
            {
                ScenarioId = "scenario-ca",
                ScenarioName = "CA Test",
                PrimaryAirportId = "OAK",
                PositionDisplayConfig = null,
                FlightStripsConfig = null,
                Aircraft = [.. callsigns.Select(MakeAircraft)],
                ElapsedSeconds = 0,
                SessionStartUtc = null,
            }
        );
        return vm;
    }

    private static AircraftModel Find(MainViewModel vm, string callsign) => vm.Aircraft.Single(a => a.Callsign == callsign);

    private static readonly ConflictAlertDto LiveAb = new("CA_ALPHA1_BRAVO1", "ALPHA1", "BRAVO1");

    private static readonly ConflictAlertDto SuppressedAc = new("CA_ALPHA1_CHARLIE1", "ALPHA1", "CHARLIE1", Suppressed: true);

    [AvaloniaFact]
    public void ALivePair_WinsOverASuppressedOneListedAfterIt()
    {
        MainViewModel vm = VmWith("ALPHA1", "BRAVO1", "CHARLIE1");

        vm.ApplyConflictAlerts([LiveAb, SuppressedAc]);

        Assert.Equal("BRAVO1", Find(vm, "ALPHA1").ConflictPeerCallsign);
        Assert.False(Find(vm, "ALPHA1").ConflictSuppressed);
    }

    [AvaloniaFact]
    public void ALivePair_WinsOverASuppressedOneListedBeforeIt()
    {
        MainViewModel vm = VmWith("ALPHA1", "BRAVO1", "CHARLIE1");

        vm.ApplyConflictAlerts([SuppressedAc, LiveAb]);

        Assert.Equal("BRAVO1", Find(vm, "ALPHA1").ConflictPeerCallsign);
        Assert.False(Find(vm, "ALPHA1").ConflictSuppressed);
    }

    [AvaloniaFact]
    public void AnUnacknowledgedPair_WinsOverAnAcknowledgedOne()
    {
        MainViewModel vm = VmWith("ALPHA1", "BRAVO1", "CHARLIE1");

        vm.ApplyConflictAlerts([
            new ConflictAlertDto("CA_ALPHA1_BRAVO1", "ALPHA1", "BRAVO1", Acknowledged: true),
            new ConflictAlertDto("CA_ALPHA1_CHARLIE1", "ALPHA1", "CHARLIE1"),
        ]);

        Assert.Equal("CHARLIE1", Find(vm, "ALPHA1").ConflictPeerCallsign);
        Assert.False(Find(vm, "ALPHA1").ConflictAcknowledged);
    }

    [AvaloniaFact]
    public void AnAcknowledgedPair_WinsOverASuppressedOneListedBeforeIt()
    {
        MainViewModel vm = VmWith("ALPHA1", "BRAVO1", "CHARLIE1");

        vm.ApplyConflictAlerts([SuppressedAc, new ConflictAlertDto("CA_ALPHA1_BRAVO1", "ALPHA1", "BRAVO1", Acknowledged: true)]);

        AircraftModel alpha = Find(vm, "ALPHA1");
        Assert.Equal("BRAVO1", alpha.ConflictPeerCallsign);
        Assert.False(alpha.ConflictSuppressed);
        Assert.True(alpha.ConflictAcknowledged);
    }

    [AvaloniaFact]
    public void WithEveryPairSuppressed_TheFirstSuppressedPairShows()
    {
        MainViewModel vm = VmWith("ALPHA1", "BRAVO1", "CHARLIE1");

        vm.ApplyConflictAlerts([
            new ConflictAlertDto("CA_ALPHA1_BRAVO1", "ALPHA1", "BRAVO1", Suppressed: true),
            new ConflictAlertDto("CA_ALPHA1_CHARLIE1", "ALPHA1", "CHARLIE1", Suppressed: true),
        ]);

        Assert.Equal("BRAVO1", Find(vm, "ALPHA1").ConflictPeerCallsign);
        Assert.True(Find(vm, "ALPHA1").ConflictSuppressed);
    }

    [AvaloniaFact]
    public void ALateAddedAircraft_IsSeededWithTheSamePairTheBroadcastPicks()
    {
        MainViewModel vm = VmWith("BRAVO1", "CHARLIE1");
        vm.ApplyConflictAlerts([SuppressedAc, LiveAb]);

        vm.OnAircraftUpdated(MakeAircraft("ALPHA1"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("BRAVO1", Find(vm, "ALPHA1").ConflictPeerCallsign);
        Assert.False(Find(vm, "ALPHA1").ConflictSuppressed);
    }
}
