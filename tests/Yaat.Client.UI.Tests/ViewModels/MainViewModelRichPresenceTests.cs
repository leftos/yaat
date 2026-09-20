using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Discord;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// What the desktop client publishes to Discord while a scenario is running. The publisher is faked
/// outright, so nothing here goes near a real IPC pipe.
/// </summary>
public class MainViewModelRichPresenceTests
{
    /// <summary>Records what the scenario lifecycle asked Discord to show.</summary>
    private sealed class RecordingPublisher : IRichPresencePublisher
    {
        public List<DiscordActivity> Published { get; } = [];

        public int Clears { get; private set; }

        public void Publish(DiscordActivity activity) => Published.Add(activity);

        public void Clear() => Clears++;
    }

    // The creator's ARTCC is the room's, and joining adopts it as the ARTCC in effect (SetActiveArtcc),
    // so a room with a blank one is the only way the presence can end up with no ARTCC to show.
    private static RoomStateDto RoomState(string? scenarioId, string? airportId, double elapsedSeconds, string creatorArtccId) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: creatorArtccId,
            Members: [],
            ScenarioName: scenarioId is null ? null : "OAK Ground 7",
            ScenarioId: scenarioId,
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: airportId,
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ElapsedSeconds: elapsedSeconds
        );

    /// <summary>A room whose scenario carries no display name yet — the name follows in a member-changed push.</summary>
    private static RoomStateDto RoomStateWithoutAScenarioName() =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: null,
            ScenarioId: "scenario-7",
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: "OAK",
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ElapsedSeconds: 0
        );

    /// <summary>
    /// Runs <paramref name="body"/> with the preferences the presence reads, restoring both
    /// afterwards: the test process shares one preferences file.
    /// </summary>
    private static void WithPreferences(string artccId, bool presenceEnabled, Action<MainViewModel, RecordingPublisher> body)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string previousArtcc = vm.Preferences.ArtccId;
        bool previousEnabled = vm.Preferences.DiscordRichPresenceEnabled;
        try
        {
            vm.Preferences.SetArtccId(artccId);
            vm.Preferences.SetDiscordRichPresenceEnabled(presenceEnabled);

            var publisher = new RecordingPublisher();
            vm.RichPresence = publisher;
            body(vm, publisher);
        }
        finally
        {
            vm.Preferences.SetArtccId(previousArtcc);
            vm.Preferences.SetDiscordRichPresenceEnabled(previousEnabled);
        }
    }

    [AvaloniaFact]
    public void JoinWithScenario_PublishesNameArtccAirport_BackDatedByElapsed()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                vm.ApplyRoomState(RoomState("scenario-7", "OAK", elapsedSeconds: 600, creatorArtccId: "ZOA"));

                DiscordActivity activity = Assert.Single(publisher.Published);
                Assert.Equal("OAK Ground 7", activity.Details);
                Assert.Equal("ZOA · OAK", activity.State);

                // Ten minutes into the room, so the Discord timer starts ten minutes ago.
                long expected = before - 600;
                Assert.InRange(activity.StartUnixSeconds, expected - 5, expected + 5);
            }
        );
    }

    /// <summary>
    /// Loading a recording swaps the active scenario without going through the bootstrap router, so it
    /// publishes on its own — otherwise Discord keeps showing the scenario the recording replaced.
    /// </summary>
    [AvaloniaFact]
    public void LoadingARecording_PublishesItsScenario_BackDatedByElapsed()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                vm.ApplyRecordingResult(
                    new RewindResultDto(
                        Success: true,
                        Error: null,
                        Aircraft: [],
                        ScenarioId: "scenario-7",
                        ScenarioName: "OAK Ground 7",
                        PrimaryAirportId: "OAK",
                        ElapsedSeconds: 900
                    )
                );

                DiscordActivity activity = Assert.Single(publisher.Published);
                Assert.Equal("OAK Ground 7", activity.Details);
                Assert.Equal("ZOA · OAK", activity.State);

                // The tape is fifteen minutes in, so the Discord timer starts fifteen minutes ago.
                long expected = before - 900;
                Assert.InRange(activity.StartUnixSeconds, expected - 5, expected + 5);
            }
        );
    }

    /// <summary>
    /// A room can hand over a scenario id before its display name; the name lands in a later
    /// member-changed push, and Discord must move off the raw id when it does.
    /// </summary>
    [AvaloniaFact]
    public void ScenarioNameArrivingLate_RepublishesWithTheName()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomStateWithoutAScenarioName());
                Assert.Equal("scenario-7", Assert.Single(publisher.Published).Details);

                vm.OnRoomMemberChanged(new RoomMemberChangedDto("ROOM-A", [], "OAK Ground 7"));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(2, publisher.Published.Count);
                Assert.Equal("OAK Ground 7", publisher.Published[1].Details);

                // The same name again is not a change, so it does not spend a Discord update.
                vm.OnRoomMemberChanged(new RoomMemberChangedDto("ROOM-A", [], "OAK Ground 7"));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(2, publisher.Published.Count);
            }
        );
    }

    [AvaloniaFact]
    public void JoinWithoutScenario_PublishesNothing()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomState(scenarioId: null, airportId: null, elapsedSeconds: 0, creatorArtccId: "ZOA"));

                Assert.Empty(publisher.Published);
            }
        );
    }

    [AvaloniaFact]
    public void LeavingTheScenario_Clears()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomState("scenario-7", "OAK", elapsedSeconds: 0, creatorArtccId: "ZOA"));
                Assert.Single(publisher.Published);

                vm.ClearScenarioState();

                Assert.Equal(1, publisher.Clears);
            }
        );
    }

    [AvaloniaFact]
    public void PreferenceOff_PublishesNothing()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: false,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomState("scenario-7", "OAK", elapsedSeconds: 0, creatorArtccId: "ZOA"));

                Assert.Empty(publisher.Published);
                // Turning the setting off withdraws whatever was showing rather than leaving it stale.
                Assert.Equal(1, publisher.Clears);
            }
        );
    }

    /// <summary>
    /// The second line carries whichever of the ARTCC and the airport is known, and is omitted
    /// entirely when neither is.
    /// </summary>
    [AvaloniaFact]
    public void StateOmitsTheMissingHalf()
    {
        WithPreferences(
            "ZOA",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomState("scenario-7", airportId: null, elapsedSeconds: 0, creatorArtccId: "ZOA"));

                Assert.Equal("ZOA", Assert.Single(publisher.Published).State);
            }
        );

        WithPreferences(
            "",
            presenceEnabled: true,
            (vm, publisher) =>
            {
                vm.ApplyRoomState(RoomState("scenario-7", airportId: null, elapsedSeconds: 0, creatorArtccId: ""));

                Assert.Null(Assert.Single(publisher.Published).State);
            }
        );
    }
}
