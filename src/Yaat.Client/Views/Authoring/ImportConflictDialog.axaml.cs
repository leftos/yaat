using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Asks what to do with an imported file whose scenario id is already taken: New id, Keep id or Skip, with "Apply to the
/// remaining N files" (ticked) when files of the import remain. Keep id's wording follows what holds the id. Closing the
/// dialog without a button answers Skip.
/// </summary>
public partial class ImportConflictDialog : Window
{
    private readonly CheckBox _applyToRemaining;

    // Parameterless ctor for the Avalonia designer / XamlLoader.
    public ImportConflictDialog()
        : this(new ImportConflictQuestion("scenario.json", "01H06NVK7VN8BS7MCDXHKJZ7MQ", "Scenario", ScenarioIdConflict.WorkingCopy, 0)) { }

    /// <summary>Creates the dialog for one clash.</summary>
    /// <param name="question">The file, its id and name, what holds the id and how many files remain.</param>
    public ImportConflictDialog(ImportConflictQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        this.FindControl<TextBlock>("FileText")!.Text = question.FileName;
        this.FindControl<TextBlock>("MessageText")!.Text =
            $"The scenario “{question.ScenarioName}” has the id {question.ScenarioId}, which {HolderText(question.Conflict)}.";
        this.FindControl<TextBlock>("NewIdText")!.Text = "New id imports it as a separate local copy with a fresh id.";
        this.FindControl<TextBlock>("KeepIdText")!.Text = KeepIdWording(question.Conflict);
        this.FindControl<TextBlock>("SkipText")!.Text = "Skip leaves this file out.";
        _applyToRemaining = this.FindControl<CheckBox>("ApplyToRemainingCheckBox")!;
        _applyToRemaining.IsVisible = question.RemainingFiles > 0;
        _applyToRemaining.Content =
            question.RemainingFiles == 1 ? "Apply to the remaining file" : $"Apply to the remaining {question.RemainingFiles} files";
        this.FindControl<Button>("NewIdButton")!.Click += (_, _) => Answer(ImportIdChoice.NewId);
        this.FindControl<Button>("KeepIdButton")!.Click += (_, _) => Answer(ImportIdChoice.KeepId);
        this.FindControl<Button>("SkipButton")!.Click += OnSkipClick;
    }

    /// <summary>Shows the dialog over <paramref name="owner"/> and finishes with the answer; Skip when closed without one.</summary>
    /// <param name="owner">The window the dialog is modal over.</param>
    /// <returns>The choice, and whether it applies to the remaining files.</returns>
    public async Task<ImportConflictAnswer> ShowAsync(Window owner) =>
        await DialogPresenter.ShowModalAsync<ImportConflictAnswer>(this, owner)
        ?? new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false);

    /// <summary>What Keep id does, by what holds the id.</summary>
    /// <param name="conflict">What holds the id.</param>
    /// <returns>The Keep id line.</returns>
    public static string KeepIdWording(ScenarioIdConflict conflict) =>
        conflict == ScenarioIdConflict.Mirror
            ? "Keep id: this file becomes the working copy of this server scenario."
            : "Keep id replaces your working copy with this file.";

    private static string HolderText(ScenarioIdConflict conflict) =>
        conflict switch
        {
            ScenarioIdConflict.Mirror => "a server scenario already uses",
            ScenarioIdConflict.Both => "your working copy and a server scenario already use",
            _ => "your working copy already uses",
        };

    private void OnSkipClick(object? sender, RoutedEventArgs e) => Answer(ImportIdChoice.Skip);

    private void Answer(ImportIdChoice choice) =>
        DialogPresenter.Close(this, new ImportConflictAnswer(choice, _applyToRemaining.IsVisible && (_applyToRemaining.IsChecked == true)));
}
