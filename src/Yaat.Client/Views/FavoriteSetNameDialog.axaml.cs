using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt that captures a favorite-set name. Unlike window profiles, set names must be
/// unique (no overwrite-on-collision), so Save is disabled while the entered name collides
/// with an existing set.
/// </summary>
public partial class FavoriteSetNameDialog : Window
{
    private readonly HashSet<string> _existingNames;

    /// <summary>Set to the entered name on Save; null on Cancel.</summary>
    public string? SetName { get; private set; }

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public FavoriteSetNameDialog()
        : this([], null) { }

    public FavoriteSetNameDialog(IEnumerable<string> existingNames, string? initialName)
    {
        InitializeComponent();
        _existingNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var nameBox = this.FindControl<TextBox>("NameTextBox");
        var okBtn = this.FindControl<Button>("OkButton");
        var cancelBtn = this.FindControl<Button>("CancelButton");

        if (nameBox is not null)
        {
            nameBox.Text = initialName ?? "";
            nameBox.SelectAll();
            nameBox.TextChanged += (_, _) => UpdateStatus(nameBox.Text);
            Opened += (_, _) => nameBox.Focus();
        }

        if (okBtn is not null)
        {
            okBtn.Click += OnOkClick;
        }

        if (cancelBtn is not null)
        {
            cancelBtn.Click += OnCancelClick;
        }
    }

    private void UpdateStatus(string? text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        var okBtn = this.FindControl<Button>("OkButton");
        var trimmed = (text ?? "").Trim();
        var collides = !string.IsNullOrEmpty(trimmed) && _existingNames.Contains(trimmed);

        if (status is not null)
        {
            status.Text = $"A set named \"{trimmed}\" already exists.";
            status.IsVisible = collides;
        }
        if (okBtn is not null)
        {
            okBtn.IsEnabled = !collides;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var nameBox = this.FindControl<TextBox>("NameTextBox");
        var entered = (nameBox?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(entered) || _existingNames.Contains(entered))
        {
            return;
        }
        SetName = entered;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SetName = null;
        Close();
    }
}
