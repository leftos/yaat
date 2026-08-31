using Avalonia.Controls;
using Yaat.Sim.LiveTraffic;

namespace Yaat.Client.Views;

/// <summary>
/// Structured editor over <see cref="LiveTrafficFilter"/>'s canonical string: flight rules, flight-plan airports
/// (with the no-plan toggle) and the radius that replaces the room's lateral scope. Hosted by the Start Live
/// Session dialog's Filters tab and the mid-session <see cref="LiveTrafficFilterWindow"/>.
/// </summary>
public partial class LiveTrafficFilterEditor : UserControl
{
    private static readonly string[] RulesItems = ["VFR and IFR", "VFR only", "IFR only"];
    private static readonly string[] MatchItems = ["Departure or destination", "Departure", "Destination"];

    private readonly ComboBox _rulesBox;
    private readonly ComboBox _matchBox;
    private readonly TextBox _airportsBox;
    private readonly CheckBox _noPlanBox;
    private readonly NumericUpDown _radiusBox;
    private readonly TextBox _centerBox;
    private readonly TextBlock _errorText;

    /// <summary>Raised on any edit, after the error text has been refreshed — hosts re-check their OK/Start button.</summary>
    public event EventHandler? Changed;

    public LiveTrafficFilterEditor()
    {
        InitializeComponent();
        _rulesBox = this.FindControl<ComboBox>("RulesBox")!;
        _matchBox = this.FindControl<ComboBox>("MatchBox")!;
        _airportsBox = this.FindControl<TextBox>("AirportsBox")!;
        _noPlanBox = this.FindControl<CheckBox>("NoPlanBox")!;
        _radiusBox = this.FindControl<NumericUpDown>("RadiusBox")!;
        _centerBox = this.FindControl<TextBox>("CenterBox")!;
        _errorText = this.FindControl<TextBlock>("ErrorText")!;

        _rulesBox.ItemsSource = RulesItems;
        _rulesBox.SelectedIndex = 0;
        _matchBox.ItemsSource = MatchItems;
        _matchBox.SelectedIndex = 0;

        _rulesBox.SelectionChanged += (_, _) => OnEdited();
        _matchBox.SelectionChanged += (_, _) => OnEdited();
        _airportsBox.TextChanged += (_, _) => OnEdited();
        _noPlanBox.IsCheckedChanged += (_, _) => OnEdited();
        _radiusBox.ValueChanged += (_, _) => OnEdited();
        _centerBox.TextChanged += (_, _) => OnEdited();
    }

    /// <summary>Loads a canonical filter string into the fields; an unparseable one loads as no filtering.</summary>
    public void SetFilterText(string? text)
    {
        if (!LiveTrafficFilter.TryParse(text, out var filter, out _))
        {
            filter = LiveTrafficFilter.None;
        }

        _rulesBox.SelectedIndex = (int)filter.Rules;
        _matchBox.SelectedIndex = (int)filter.AirportMatch;
        _airportsBox.Text = string.Join(", ", filter.AirportCodes);
        _noPlanBox.IsChecked = filter.IncludeUnplanned;
        _centerBox.Text = filter.RadiusCenter ?? "";
        _radiusBox.Value = (decimal)(filter.RadiusNm ?? 0);
        RefreshError();
    }

    /// <summary>The canonical filter string for the current fields, or false with the error shown inline.</summary>
    public bool TryGetFilterText(out string text, out string? error)
    {
        text = "";
        var parts = new List<string>();
        if (_rulesBox.SelectedIndex > 0)
        {
            parts.Add(_rulesBox.SelectedIndex == 1 ? "RULES=VFR" : "RULES=IFR");
        }

        var airports = (_airportsBox.Text ?? "").Trim();
        if (airports.Length > 0)
        {
            parts.Add($"APT={airports}");
            if (_matchBox.SelectedIndex == 1)
            {
                parts.Add("MATCH=DEP");
            }
            else if (_matchBox.SelectedIndex == 2)
            {
                parts.Add("MATCH=DEST");
            }

            if (_noPlanBox.IsChecked == true)
            {
                parts.Add("NOPLAN=1");
            }
        }

        var center = (_centerBox.Text ?? "").Trim();
        double radius = (double)(_radiusBox.Value ?? 0);
        if ((center.Length > 0) != (radius > 0))
        {
            error = center.Length > 0 ? "Set a radius for the centre (or clear it)" : "Name the centre for the radius (or set it to 0)";
            return false;
        }

        if (center.Length > 0)
        {
            parts.Add($"CENTER={center}");
            parts.Add($"RADIUS={radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (!LiveTrafficFilter.TryParse(string.Join(';', parts), out var filter, out error))
        {
            return false;
        }

        text = filter.Serialize();
        return true;
    }

    private void OnEdited()
    {
        RefreshError();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshError()
    {
        TryGetFilterText(out _, out var error);
        _errorText.Text = error ?? "";
        _errorText.IsVisible = error is not null;
    }
}
