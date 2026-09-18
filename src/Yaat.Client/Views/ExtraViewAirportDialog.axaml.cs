using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt that picks the base airport for a new Radar or Ground View window (issue #434). A second
/// view has no scenario-inferred target, so the user chooses one: an airport of the configured ARTCC from
/// the list, or any airport the navigation database knows, typed in. The text box is authoritative —
/// selecting from the list fills it — and Open stays disabled while the entry is empty or unknown.
/// </summary>
public partial class ExtraViewAirportDialog : Window
{
    private readonly Func<string, bool> _isKnownAirport;

    /// <summary>The chosen airport ID, upper-cased, on Open; null on Cancel.</summary>
    public string? AirportId { get; private set; }

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public ExtraViewAirportDialog()
        : this("New Window", [], null, _ => true) { }

    public ExtraViewAirportDialog(string title, IReadOnlyList<string> artccAirports, string? initialAirportId, Func<string, bool> isKnownAirport)
    {
        InitializeComponent();
        _isKnownAirport = isKnownAirport;
        Title = title;

        ListBox? list = this.FindControl<ListBox>("AirportList");
        TextBox? airportBox = this.FindControl<TextBox>("AirportTextBox");
        Button? okBtn = this.FindControl<Button>("OkButton");
        Button? cancelBtn = this.FindControl<Button>("CancelButton");

        if (list is not null)
        {
            list.ItemsSource = artccAirports;
            list.SelectionChanged += (_, _) =>
            {
                if ((list.SelectedItem is string selected) && (airportBox is not null))
                {
                    airportBox.Text = selected;
                }
            };
            if (initialAirportId is not null)
            {
                list.SelectedItem = artccAirports.FirstOrDefault(a => string.Equals(a, initialAirportId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (airportBox is not null)
        {
            airportBox.Text = initialAirportId ?? "";
            airportBox.SelectAll();
            airportBox.TextChanged += (_, _) => UpdateStatus(airportBox.Text);
            Opened += (_, _) => airportBox.Focus();
            UpdateStatus(airportBox.Text);
        }

        okBtn?.Click += OnOkClick;

        cancelBtn?.Click += OnCancelClick;
    }

    private void UpdateStatus(string? text)
    {
        TextBlock? status = this.FindControl<TextBlock>("StatusText");
        Button? okBtn = this.FindControl<Button>("OkButton");
        string trimmed = (text ?? "").Trim();
        bool known = !string.IsNullOrEmpty(trimmed) && _isKnownAirport(trimmed);

        status?.IsVisible = !string.IsNullOrEmpty(trimmed) && !known;

        okBtn?.IsEnabled = known;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        TextBox? airportBox = this.FindControl<TextBox>("AirportTextBox");
        string entered = (airportBox?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(entered) || !_isKnownAirport(entered))
        {
            return;
        }

        AirportId = entered.ToUpperInvariant();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        AirportId = null;
        Close();
    }
}
