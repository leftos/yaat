using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The SSA's time is the session clock with a scenario, so the time read off it is the time ERAM LC measures from, and
/// the wall clock with none.
/// </summary>
public class SsaViewModelTests
{
    private static readonly DateTime Start = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTimeOffset Wall = new(2026, 11, 16, 18, 47, 32, TimeSpan.Zero);

    [AvaloniaFact]
    public void Refresh_WithAScenario_ShowsTheSessionTime()
    {
        RunWithWallClock(main =>
        {
            main.ApplyRoomState(RoomNinetySecondsIn());
            using var scope = new ScopeViewModel(main, null, profileStore: null);
            var ssa = new SsaViewModel(scope);

            ssa.Refresh();

            Assert.Equal("1201/30", ssa.Line1);
        });
    }

    [AvaloniaFact]
    public void Refresh_WithoutAScenario_ShowsTheWallClock()
    {
        RunWithWallClock(main =>
        {
            using var scope = new ScopeViewModel(main, null, profileStore: null);
            var ssa = new SsaViewModel(scope);

            ssa.Refresh();

            Assert.Equal("1847/32", ssa.Line1);
        });
    }

    private static void RunWithWallClock(Action<MainViewModel> body)
    {
        TimeProvider saved = MainViewModel.WallClock;
        MainViewModel.WallClock = new FakeTimeProvider(Wall);
        try
        {
            body(new MainViewModel(new FakeFilePickerService()));
        }
        finally
        {
            MainViewModel.WallClock = saved;
        }
    }

    private static RoomStateDto RoomNinetySecondsIn() =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: "OAK Ground",
            ScenarioId: "scenario-1",
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: null,
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            ActiveRunways: [],
            ToweredAirports: [],
            SessionStartUtc: Start,
            ElapsedSeconds: 90
        );
}
