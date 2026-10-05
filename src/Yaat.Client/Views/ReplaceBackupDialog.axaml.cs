using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The question the Import / Export hub asks before an import that replaces an item: back up the current values first,
/// replace without a backup, or cancel. Shown through <see cref="DialogPresenter.ShowModalAsync{T}"/>, it closes with the
/// <see cref="ReplaceBackupChoice"/>; closing it any other way is a cancel.
/// </summary>
public partial class ReplaceBackupDialog : Window
{
    // Parameterless ctor required for the Avalonia designer / XamlLoader. Not used at runtime.
    public ReplaceBackupDialog()
        : this("") { }

    /// <summary>Builds the dialog.</summary>
    /// <param name="message">The question, naming the items the import replaces (<see cref="ImportExportViewModel.BackupMessage"/>).</param>
    public ReplaceBackupDialog(string message)
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("BackUpButton")!.Click += (_, _) => DialogPresenter.Close(this, ReplaceBackupChoice.BackUp);
        this.FindControl<Button>("ReplaceWithoutBackupButton")!.Click += (_, _) =>
            DialogPresenter.Close(this, ReplaceBackupChoice.ReplaceWithoutBackup);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => DialogPresenter.Close(this, ReplaceBackupChoice.Cancel);
    }

    /// <summary>Asks <paramref name="message"/> over <paramref name="owner"/>; a dialog closed without a button is a cancel.</summary>
    public static async Task<ReplaceBackupChoice> AskAsync(Window owner, string message) =>
        await DialogPresenter.ShowModalAsync<ReplaceBackupChoice?>(new ReplaceBackupDialog(message), owner) ?? ReplaceBackupChoice.Cancel;
}
