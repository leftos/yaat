using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt behind <b>Scenario → File Bug Report...</b>. Collects the title, what happened, what was
/// expected and the callsigns involved; "Open GitHub Issue" stays disabled until the title and the
/// description are both filled in, because an issue with neither is worthless.
/// </summary>
public partial class FileBugReportDialog : Window
{
    private const string AttachesRecordingNote =
        "YAAT will save a bug report bundle (the session recording and logs), open a new GitHub issue in your browser, and show the bundle so you can drag it into the issue.";

    private const string LogOnlyNote =
        "You're not in a room, so only the client log will be attached. YAAT will open a new GitHub issue in your browser and show the log zip so you can drag it into the issue.";

    /// <summary>Set to the entered form on submit; null on Cancel.</summary>
    public BugReportForm? Result { get; private set; }

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public FileBugReportDialog()
        : this(false) { }

    public FileBugReportDialog(bool attachesRecording)
    {
        InitializeComponent();

        TextBox? titleBox = this.FindControl<TextBox>("TitleTextBox");
        TextBox? happenedBox = this.FindControl<TextBox>("WhatHappenedTextBox");
        TextBlock? note = this.FindControl<TextBlock>("NoteText");
        Button? okButton = this.FindControl<Button>("OkButton");
        Button? cancelButton = this.FindControl<Button>("CancelButton");

        note?.Text = attachesRecording ? AttachesRecordingNote : LogOnlyNote;

        if (titleBox is not null)
        {
            titleBox.TextChanged += (_, _) => UpdateOkEnabled();
            Opened += (_, _) => titleBox.Focus();
        }

        happenedBox?.TextChanged += (_, _) => UpdateOkEnabled();
        okButton?.Click += OnOkClick;
        cancelButton?.Click += OnCancelClick;

        UpdateOkEnabled();
    }

    private void UpdateOkEnabled()
    {
        TextBox? titleBox = this.FindControl<TextBox>("TitleTextBox");
        TextBox? happenedBox = this.FindControl<TextBox>("WhatHappenedTextBox");
        Button? okButton = this.FindControl<Button>("OkButton");

        bool ready = !string.IsNullOrWhiteSpace(titleBox?.Text) && !string.IsNullOrWhiteSpace(happenedBox?.Text);
        okButton?.IsEnabled = ready;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        string title = (this.FindControl<TextBox>("TitleTextBox")?.Text ?? "").Trim();
        string whatHappened = (this.FindControl<TextBox>("WhatHappenedTextBox")?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(whatHappened))
        {
            return;
        }

        Result = new BugReportForm(
            title,
            whatHappened,
            (this.FindControl<TextBox>("ExpectedTextBox")?.Text ?? "").Trim(),
            (this.FindControl<TextBox>("CallsignsTextBox")?.Text ?? "").Trim()
        );
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
