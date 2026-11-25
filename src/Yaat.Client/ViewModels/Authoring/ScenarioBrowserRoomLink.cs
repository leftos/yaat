using System.ComponentModel;
using Avalonia.Threading;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// Keeps a Scenario Editor browser's Test in Room command following the main window's room: each change to what
/// <see cref="MainViewModel.CanLoadScenario"/> reads re-reads the command, on the UI thread.
/// </summary>
public static class ScenarioBrowserRoomLink
{
    // What MainViewModel.CanLoadScenario reads, among the properties it raises changes for.
    private static readonly HashSet<string> RoomProperties =
    [
        nameof(MainViewModel.CanLoadScenario),
        nameof(MainViewModel.CanExecuteInRoom),
        nameof(MainViewModel.IsNonMentor),
        nameof(MainViewModel.IsRoomLoading),
    ];

    /// <summary>Starts following the room.</summary>
    /// <param name="room">The main window's view model.</param>
    /// <param name="browser">The Scenario Editor's browser.</param>
    /// <returns>Stops following when disposed; the main window disposes it when the editor window closes.</returns>
    public static IDisposable Follow(MainViewModel room, ScenarioBrowserViewModel browser)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(browser);
        void OnRoomChanged(object? sender, PropertyChangedEventArgs e)
        {
            if ((e.PropertyName is not { } name) || !RoomProperties.Contains(name))
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                browser.RefreshTestInRoom();
            }
            else
            {
                Dispatcher.UIThread.Post(browser.RefreshTestInRoom);
            }
        }

        room.PropertyChanged += OnRoomChanged;
        return new Link(() => room.PropertyChanged -= OnRoomChanged);
    }

    private sealed class Link(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
