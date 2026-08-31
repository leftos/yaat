using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Mid-session live-traffic filter dialog: hosts <see cref="LiveTrafficFilterEditor"/> seeded from the room's
/// current filter and returns the new canonical string (null = cancelled). The caller applies it via the
/// session-settings path so every RPO sees the change.
/// </summary>
public partial class LiveTrafficFilterWindow : Window
{
    private readonly LiveTrafficFilterEditor _editor;
    private readonly Button _applyButton;

    public LiveTrafficFilterWindow()
        : this(new UserPreferences(), "") { }

    public LiveTrafficFilterWindow(UserPreferences preferences, string currentFilter)
    {
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "LiveTrafficFilter", 460, 360).Restore();

        _editor = this.FindControl<LiveTrafficFilterEditor>("Editor")!;
        _applyButton = this.FindControl<Button>("ApplyButton")!;
        _editor.SetFilterText(currentFilter);
        _editor.Changed += (_, _) => _applyButton.IsEnabled = _editor.TryGetFilterText(out _, out _);

        _applyButton.Click += (_, _) =>
        {
            if (_editor.TryGetFilterText(out var text, out _))
            {
                Close(text);
            }
        };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
    }
}
