using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public TerminalWindow()
        : this(new UserPreferences()) { }

    public TerminalWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Terminal", 700, 400);
        _geometryHelper.Restore();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            var cmdView = this.FindControl<CommandInputView>("CommandInputView");
            if (cmdView is not null && SettingsViewModel.ParseKeybind(vm.Preferences.AircraftSelectKey, out var key, out var mods))
            {
                cmdView.SetAircraftSelectKeybind(key, mods);
            }
        }
    }

    /// <summary>
    /// Brings this popped-out terminal window forward and focuses its command input. Called by
    /// MainWindow's focus router when the terminal is popped out (the embedded input in MainWindow
    /// is hidden in that state).
    /// </summary>
    public void FocusCommandInput()
    {
        Activate();
        this.FindControl<CommandInputView>("CommandInputView")?.FocusCommandInput();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
