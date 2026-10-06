using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt that captures a layout name from the user. The list of
/// existing layout names is passed in so the dialog can show an "overwrite"
/// warning before the user clicks Save.
/// </summary>
public partial class SaveLayoutDialog : Window
{
    private readonly HashSet<string> _existingNames;

    /// <summary>Set to the entered name on Save; null on Cancel.</summary>
    public string? LayoutName { get; private set; }

    public SaveLayoutDialog()
        : this([], null) { }

    public SaveLayoutDialog(IEnumerable<string> existingNames, string? initialName)
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _existingNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        TextBox? nameBox = this.FindControl<TextBox>("NameTextBox");
        Button? okBtn = this.FindControl<Button>("OkButton");
        Button? cancelBtn = this.FindControl<Button>("CancelButton");
        TextBlock? status = this.FindControl<TextBlock>("StatusText");

        if (nameBox is not null)
        {
            nameBox.Text = initialName ?? "";
            nameBox.SelectAll();
            nameBox.TextChanged += (_, _) => UpdateStatus(nameBox.Text, status);
            Opened += (_, _) => nameBox.Focus();
        }

        okBtn?.Click += OnOkClick;

        cancelBtn?.Click += OnCancelClick;
    }

    private void UpdateStatus(string? text, TextBlock? status)
    {
        if (status is null)
        {
            return;
        }
        string trimmed = (text ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            status.IsVisible = false;
            return;
        }
        if (_existingNames.Contains(trimmed))
        {
            status.Text = $"A layout named \"{trimmed}\" already exists — saving will overwrite it.";
            status.IsVisible = true;
        }
        else
        {
            status.IsVisible = false;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        TextBox? nameBox = this.FindControl<TextBox>("NameTextBox");
        string entered = (nameBox?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(entered))
        {
            return;
        }
        LayoutName = entered;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        LayoutName = null;
        Close();
    }
}
