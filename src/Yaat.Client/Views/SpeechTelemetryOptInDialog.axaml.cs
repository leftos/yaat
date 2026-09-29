using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaat.Client.Views;

/// <summary>
/// One-time offer to send push-to-talk recordings to the YAAT developers for speech-recognition
/// improvement. Closing the window, Esc or "No thanks" all count as not accepted.
/// </summary>
public partial class SpeechTelemetryOptInDialog : Window
{
    /// <summary>True when the user chose "Share recordings".</summary>
    public bool Accepted { get; private set; }

    public SpeechTelemetryOptInDialog()
    {
        InitializeComponent();

        this.FindControl<Button>("ShareButton")?.Click += OnShareClick;
        this.FindControl<Button>("DeclineButton")?.Click += OnDeclineClick;
    }

    private void OnShareClick(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnDeclineClick(object? sender, RoutedEventArgs e) => Close();
}
