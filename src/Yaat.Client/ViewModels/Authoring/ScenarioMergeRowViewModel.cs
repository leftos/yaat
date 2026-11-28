using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Whether a side holds a value the other version lacks.</summary>
public enum ScenarioMergePresence
{
    /// <summary>The side holds the value as the other version does, or lacks it as the other does.</summary>
    None,

    /// <summary>The side holds a value the base lacks; in a two-way merge, one the other side lacks.</summary>
    Added,

    /// <summary>The side lacks a value the base holds; in a two-way merge, one the other side holds.</summary>
    Removed,
}

/// <summary>One merge row as the window shows it: its label, each side's text, its kind and the side it takes.</summary>
public sealed partial class ScenarioMergeRowViewModel : ObservableObject
{
    private readonly Action<ScenarioMergeRowViewModel, MergeChoice> _choose;

    // Set while the MERGED AIRCRAFT editor is open: keep mine and keep theirs do nothing.
    private bool _locked;

    /// <summary>The side the row takes: its choice, else an auto row's own side; null for a clash with no choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinePressed), nameof(IsTheirsPressed), nameof(IsEditPressed))]
    private MergeChoiceKind? _pressedSide;

    /// <summary>The MERGED VALUE editor open under the row; null while none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private ScenarioMergedValueViewModel? _editor;

    /// <summary>Whether the row is a clash with no choice yet and no merged-aircraft replacement over its aircraft.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Whether the row shows its base strip: every clash, and auto rows with Show base on; never in a two-way merge.</summary>
    [ObservableProperty]
    private bool _showsBase;

    internal ScenarioMergeRowViewModel(MergeRow row, bool isTwoWay, int index, Action<ScenarioMergeRowViewModel, MergeChoice> choose)
    {
        Row = row;
        IsTwoWay = isTwoWay;
        Index = index;
        _choose = choose;
        IsOrderRow = row.Path.EndsWith(ScenarioMerge.OrderPathSuffix, StringComparison.Ordinal);
        Label = IsOrderRow ? row.Label + " order" : row.Label;
        BaseText = ScenarioMergeValueText.Format(row.Section, row.FieldKey, row.InBase, row.Base);
        MineText = ScenarioMergeValueText.Format(row.Section, row.FieldKey, row.InMine, row.Mine);
        TheirsText = ScenarioMergeValueText.Format(row.Section, row.FieldKey, row.InTheirs, row.Theirs);
        MinePresence = Presence(row.InMine, row.InTheirs);
        TheirsPresence = Presence(row.InTheirs, row.InMine);
        MineNote = Note(MinePresence, "mine");
        TheirsNote = Note(TheirsPresence, "theirs");
        BaseReason = Reason();
        Refresh(null, false, false);
    }

    /// <summary>Gets the engine's row.</summary>
    public MergeRow Row { get; }

    /// <summary>Gets the row's JSON Pointer, the key of its choice.</summary>
    public string Path => Row.Path;

    /// <summary>Gets the editor tab or General-tab section the row sits on.</summary>
    public string Section => Row.Section;

    /// <summary>Gets the top-level list item's key, or null outside a list.</summary>
    public string? ItemKey => Row.ItemKey;

    /// <summary>Gets which side the row takes unless a choice overrides it.</summary>
    public MergeRowKind Kind => Row.Kind;

    /// <summary>Gets the row's index in the engine's plan.</summary>
    public int Index { get; }

    /// <summary>Gets a value indicating whether the merge has no base.</summary>
    public bool IsTwoWay { get; }

    /// <summary>Gets the row's label; an order row reads "{list} order".</summary>
    public string Label { get; }

    /// <summary>Gets a value indicating whether the row is a clash.</summary>
    public bool IsClash => Row.Kind == MergeRowKind.Clash;

    /// <summary>Gets a value indicating whether the row shows the auto-merged tag; never in a two-way merge.</summary>
    public bool ShowsAutoTag => !IsTwoWay && !IsClash;

    /// <summary>Gets a value indicating whether the row covers the order of a list's shared items.</summary>
    public bool IsOrderRow { get; }

    /// <summary>Gets a value indicating whether the row is a whole list item.</summary>
    public bool IsItemRow => Row.FieldKey == "[]";

    /// <summary>Gets a value indicating whether the row offers an edited value; an order row takes mine or theirs only.</summary>
    public bool CanEdit => !IsOrderRow;

    /// <summary>Gets the base's value as the base strip shows it.</summary>
    public string BaseText { get; }

    /// <summary>Gets mine's value as its pane shows it.</summary>
    public string MineText { get; }

    /// <summary>Gets theirs' value as its pane shows it.</summary>
    public string TheirsText { get; }

    /// <summary>Gets whether mine added or removed the value.</summary>
    public ScenarioMergePresence MinePresence { get; }

    /// <summary>Gets whether theirs added or removed the value.</summary>
    public ScenarioMergePresence TheirsPresence { get; }

    /// <summary>Gets the wording on mine's pane for a list item added or removed there; null otherwise.</summary>
    public string? MineNote { get; }

    /// <summary>Gets the wording on theirs' pane for a list item added or removed there; null otherwise.</summary>
    public string? TheirsNote { get; }

    /// <summary>Gets the base strip's reason, naming what each side changed; null in a two-way merge.</summary>
    public string? BaseReason { get; }

    /// <summary>Gets a value indicating whether the row takes mine.</summary>
    public bool IsMinePressed => PressedSide == MergeChoiceKind.KeepMine;

    /// <summary>Gets a value indicating whether the row takes theirs.</summary>
    public bool IsTheirsPressed => PressedSide == MergeChoiceKind.KeepTheirs;

    /// <summary>Gets a value indicating whether the row takes an edited value: its ✎ shows pressed.</summary>
    public bool IsEditPressed => PressedSide == MergeChoiceKind.Edit;

    /// <summary>Gets a value indicating whether a MERGED VALUE editor is open under the row.</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>Gets the row's field editor; the JSON box for a field the table does not map.</summary>
    public ScenarioMergeFieldEditor FieldEditor => ScenarioMergeFieldEditors.For(Section, FieldKey);

    /// <summary>Gets the row's label path.</summary>
    public string FieldKey => Row.FieldKey;

    // A row of an aircraft the merged-aircraft editor replaced reads as resolved by that edit, whatever its own choice.
    internal void Refresh(MergeChoice? choice, bool showBase, bool replaced)
    {
        PressedSide = replaced ? MergeChoiceKind.Edit : (choice?.Kind ?? AutoSide());
        IsOpen = IsClash && (choice is null) && !replaced;
        ShowsBase = !IsTwoWay && (IsClash || showBase);
    }

    // The value an editor opens with: the row's edit, else theirs when the row takes theirs, else mine.
    internal JsonNode? PrefillValue(MergeChoice? choice) =>
        PressedSide switch
        {
            MergeChoiceKind.Edit => choice?.Value,
            MergeChoiceKind.KeepTheirs => Row.Theirs,
            _ => Row.Mine,
        };

    // Keep mine and keep theirs are offered only while the MERGED AIRCRAFT editor is closed.
    internal void Lock(bool locked)
    {
        if (_locked == locked)
        {
            return;
        }

        _locked = locked;
        KeepMineCommand.NotifyCanExecuteChanged();
        KeepTheirsCommand.NotifyCanExecuteChanged();
    }

    private bool CanChoose() => !_locked;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepMine()
    {
        if (CanChoose())
        {
            _choose(this, MergeChoice.KeepMine);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepTheirs()
    {
        if (CanChoose())
        {
            _choose(this, MergeChoice.KeepTheirs);
        }
    }

    private MergeChoiceKind? AutoSide() =>
        Kind switch
        {
            MergeRowKind.AutoMine => MergeChoiceKind.KeepMine,
            MergeRowKind.AutoTheirs => MergeChoiceKind.KeepTheirs,
            _ => null,
        };

    private ScenarioMergePresence Presence(bool inSide, bool inOther)
    {
        bool inReference = IsTwoWay ? inOther : Row.InBase;
        return (inSide, inReference) switch
        {
            (true, false) => ScenarioMergePresence.Added,
            (false, true) => ScenarioMergePresence.Removed,
            _ => ScenarioMergePresence.None,
        };
    }

    private string? Note(ScenarioMergePresence presence, string side)
    {
        if (!IsItemRow)
        {
            return null;
        }

        return (presence, IsTwoWay) switch
        {
            (ScenarioMergePresence.Added, false) => "added in " + side,
            (ScenarioMergePresence.Removed, false) => "removed in " + side,
            (ScenarioMergePresence.Added, true) => "only in " + side,
            (ScenarioMergePresence.Removed, true) => "not in " + side,
            _ => null,
        };
    }

    private string? Reason()
    {
        if (IsTwoWay)
        {
            return null;
        }

        return Kind switch
        {
            MergeRowKind.AutoMine => $"mine changed it to {MineText}",
            MergeRowKind.AutoTheirs => $"theirs changed it to {TheirsText}",
            _ => $"mine changed it to {MineText}; theirs changed it to {TheirsText}",
        };
    }
}
