using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt that captures a profile name from the user. The list of
/// existing profile names is passed in so the dialog can show an "overwrite"
/// warning before the user clicks Save.
/// </summary>
public partial class SaveWindowProfileDialog : Window
{
    private readonly HashSet<string> _existingNames;

    /// <summary>Set to the entered name on Save; null on Cancel.</summary>
    public string? ProfileName { get; private set; }

    public SaveWindowProfileDialog()
        : this([], null) { }

    public SaveWindowProfileDialog(IEnumerable<string> existingNames, string? initialName)
    {
        InitializeComponent();
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
            status.Text = $"A profile named \"{trimmed}\" already exists — saving will overwrite it.";
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
        ProfileName = entered;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ProfileName = null;
        Close();
    }
}
