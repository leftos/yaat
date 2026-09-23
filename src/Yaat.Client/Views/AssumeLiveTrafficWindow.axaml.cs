using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Services;
using Yaat.Sim.LiveTraffic;

namespace Yaat.Client.Views;

/// <summary>
/// Bulk live-traffic assume dialog: every airborne shadow, or those within a radius of an airport / fix / FRD, narrowed
/// by flight rules. Returns the request to send (null = cancelled); the server picks the shadows and skips the ones on
/// the ground or stale.
/// </summary>
public partial class AssumeLiveTrafficWindow : Window
{
    private readonly RadioButton _radiusRadio;
    private readonly TextBox _centerBox;
    private readonly NumericUpDown _radiusBox;
    private readonly ComboBox _rulesBox;
    private readonly Button _assumeButton;
    private readonly string _initials;

    public AssumeLiveTrafficWindow()
        : this(new UserPreferences(), "") { }

    public AssumeLiveTrafficWindow(UserPreferences preferences, string defaultCenter)
    {
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "AssumeLiveTraffic", 420, 300).Restore();

        _initials = preferences.UserInitials;
        _radiusRadio = this.FindControl<RadioButton>("RadiusRadio")!;
        _centerBox = this.FindControl<TextBox>("CenterBox")!;
        _radiusBox = this.FindControl<NumericUpDown>("RadiusBox")!;
        _rulesBox = this.FindControl<ComboBox>("RulesBox")!;
        _assumeButton = this.FindControl<Button>("AssumeButton")!;

        _centerBox.Text = defaultCenter;
        _radiusRadio.IsCheckedChanged += (_, _) => RefreshEnabled();
        _centerBox.TextChanged += (_, _) => RefreshEnabled();
        _radiusBox.ValueChanged += (_, _) => RefreshEnabled();
        RefreshEnabled();

        _assumeButton.Click += (_, _) => Close(BuildRequest());
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };
    }

    private bool IsRadiusMode => _radiusRadio.IsChecked == true;

    private void RefreshEnabled()
    {
        _centerBox.IsEnabled = IsRadiusMode;
        _radiusBox.IsEnabled = IsRadiusMode;
        _assumeButton.IsEnabled = !IsRadiusMode || (!string.IsNullOrWhiteSpace(_centerBox.Text) && (_radiusBox.Value is > 0));
    }

    private AssumeLiveTrafficRequestDto BuildRequest() =>
        new()
        {
            Mode = IsRadiusMode ? AssumeLiveTrafficMode.WithinRadius : AssumeLiveTrafficMode.All,
            Center = IsRadiusMode ? _centerBox.Text?.Trim() : null,
            RadiusNm = IsRadiusMode ? (double?)_radiusBox.Value : null,
            Rules = _rulesBox.SelectedIndex switch
            {
                1 => LiveTrafficRulesFilter.VfrOnly,
                2 => LiveTrafficRulesFilter.IfrOnly,
                _ => LiveTrafficRulesFilter.Both,
            },
            Callsigns = [],
            Initials = _initials,
        };
}
