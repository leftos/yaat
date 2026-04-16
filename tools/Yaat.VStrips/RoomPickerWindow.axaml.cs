using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;

namespace Yaat.VStrips;

/// <summary>
/// Minimal room-picker dialog for the standalone vStrips app. Lists active
/// training rooms from yaat-server and lets the student join one.
/// </summary>
public partial class RoomPickerWindow : Window
{
    private readonly StandaloneViewModel _vm;

    /// <summary>The room ID the user selected, or null if they cancelled.</summary>
    public string? SelectedRoomId { get; private set; }

    /// <summary>Parameterless ctor required by Avalonia's XAML runtime loader. Not used at runtime.</summary>
    public RoomPickerWindow()
    {
        _vm = null!;
        InitializeComponent();
    }

    public RoomPickerWindow(StandaloneViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("RoomsGrid")!;
        grid.ItemsSource = vm.AvailableRooms;

        var refreshButton = this.FindControl<Button>("RefreshButton")!;
        refreshButton.Click += async (_, _) => await vm.RefreshRoomsAsync();

        var joinButton = this.FindControl<Button>("JoinButton")!;
        joinButton.Click += OnJoinClick;

        var cancelButton = this.FindControl<Button>("CancelButton")!;
        cancelButton.Click += (_, _) => Close();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        await _vm.RefreshRoomsAsync();
    }

    private void OnJoinClick(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("RoomsGrid")!;
        if (grid.SelectedItem is TrainingRoomInfoDto room)
        {
            SelectedRoomId = room.RoomId;
            Close();
        }
    }
}
