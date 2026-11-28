using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What a MERGED VALUE editor opens with.</summary>
/// <param name="Editor">The row's field editor.</param>
/// <param name="Value">The value it is pre-filled from.</param>
/// <param name="Context">The flight plan a preset command resolves against; null for none.</param>
public sealed record ScenarioMergeEditorSeed(ScenarioMergeFieldEditor Editor, JsonNode? Value, ScenarioMergeCommandContext? Context);

/// <summary>
/// One MERGED VALUE editor under a merge row: the field's own control pre-filled from the side the row takes, its error
/// line set when the box is left (<see cref="CommitText"/>), Keep this storing the typed JSON as the row's edit, and Cancel
/// closing it with the row's choice unchanged. A picker whose options could not load is a free-text box.
/// </summary>
public sealed partial class ScenarioMergedValueViewModel : ObservableObject
{
    private readonly Action<JsonNode?> _keep;
    private readonly Action _cancel;
    private readonly ScenarioMergeCommandContext? _context;
    private readonly string _initialText;

    // A comma list that opened on JSON null keeps it while its text is the blank it opened with.
    private readonly bool _keepsNull;
    private ScenarioMergeParsed? _parsed;

    /// <summary>The editor's text: a check box's "true" or "false", a picker's option value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(IsChecked))]
    private string _text = "";

    /// <summary>The editor's error line, set when the box is left; null for none.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCommand))]
    private string? _error;

    internal ScenarioMergedValueViewModel(ScenarioMergeEditorSeed seed, ScenarioMergePickers pickers, Action<JsonNode?> keep, Action cancel)
    {
        Editor = seed.Editor.ForValue(seed.Value);
        _context = seed.Context;
        _keep = keep;
        _cancel = cancel;
        Text = Editor.Format(seed.Value);
        _initialText = Text;
        _keepsNull = (Kind == ScenarioMergeEditorKind.CommaList) && (seed.Value is null);
        Options = Editor.OptionsFor(Text, pickers);
    }

    /// <summary>Gets the flight plan a preset command resolves against; null for none.</summary>
    public ScenarioMergeCommandContext? Context => _context;

    /// <summary>Gets the field's editor.</summary>
    public ScenarioMergeFieldEditor Editor { get; }

    /// <summary>Gets the control the editor shows.</summary>
    public ScenarioMergeEditorKind Kind => Editor.Kind;

    /// <summary>Gets a picker's options; null for free text and for every kind but a choice.</summary>
    public IReadOnlyList<ScenarioPickOption>? Options { get; }

    /// <summary>Gets a value indicating whether a choice is typed because its options could not load.</summary>
    public bool IsFreeText => (Kind == ScenarioMergeEditorKind.Choice) && (Options is null);

    /// <summary>Gets a value indicating whether the box is monospace: the JSON box.</summary>
    public bool IsMonospace => Kind == ScenarioMergeEditorKind.Json;

    /// <summary>Gets or sets the picker's selected option; setting one sets the text to its value.</summary>
    public ScenarioPickOption? Selection
    {
        get => (Options is null) ? null : ScenarioFieldText.Selected(Options, Text);
        set
        {
            if (value is not null)
            {
                Text = value.Value;
            }
        }
    }

    /// <summary>Gets a value indicating whether the check box has a third state, none (JSON null).</summary>
    public bool IsThreeState => Kind == ScenarioMergeEditorKind.OptionalBool;

    /// <summary>Gets or sets whether a check box is checked; null for a three-state box's none.</summary>
    public bool? IsChecked
    {
        get =>
            Text switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
        set =>
            Text = value switch
            {
                true => "true",
                false => "false",
                null => "",
            };
    }

    /// <summary>Reads the text as the field's value, as the box is left: sets the error line, or clears it.</summary>
    [RelayCommand]
    public void CommitText()
    {
        _parsed =
            (_keepsNull && string.Equals(Text, _initialText, StringComparison.Ordinal)) ? ScenarioMergeParsed.Ok(null) : Editor.Parse(Text, _context);
        Error = _parsed.Error;
    }

    partial void OnTextChanged(string value)
    {
        _parsed = null;
        Error = null;
    }

    private bool CanKeep() => _parsed is not { BlocksKeep: true };

    [RelayCommand(CanExecute = nameof(CanKeep))]
    private void Keep()
    {
        CommitText();
        if (_parsed is { BlocksKeep: false } parsed)
        {
            _keep(parsed.Value);
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel();
}
