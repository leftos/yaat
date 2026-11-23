using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One preset command of the selected aircraft in the Aircraft tab's detail panel: its command text (kept as typed, only
/// trimmed at its ends as every text field's commit does) and its time offset (typed and shown as a ScenarioTime). Each
/// commit, Remove, Move up, Move down and Duplicate is one undo step of the panel. The row is found again in the
/// document by <see cref="Id"/>; a field edit, or its undo or redo, that leaves the presets' ids and order unchanged
/// refreshes the row in place through <see cref="Update"/>.
/// </summary>
public sealed partial class ScenarioPresetRowViewModel : ObservableObject
{
    private readonly ScenarioAircraftViewModel _owner;
    private int _index;
    private int _count;

    [ObservableProperty]
    private string _command = "";

    [ObservableProperty]
    private string? _commandError;

    [ObservableProperty]
    private string _offset = "";

    [ObservableProperty]
    private string? _offsetError;

    /// <summary>Initializes a new instance of the <see cref="ScenarioPresetRowViewModel"/> class from a preset.</summary>
    /// <param name="owner">The detail panel the row's commands write through.</param>
    /// <param name="preset">The preset, as the document holds it now.</param>
    /// <param name="index">The preset's index into the aircraft's presets.</param>
    /// <param name="count">How many presets the aircraft has.</param>
    internal ScenarioPresetRowViewModel(ScenarioAircraftViewModel owner, PresetCommand preset, int index, int count)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(preset);
        Id = preset.Id;
        Update(preset, index, count);
    }

    /// <summary>Gets the preset's id, the key the row and later checks find it by.</summary>
    public string Id { get; }

    /// <summary>Gets the preset's index into the aircraft's presets when the row was last built or updated.</summary>
    public int Index
    {
        get => _index;
        private set => SetProperty(ref _index, value);
    }

    /// <summary>Refreshes the row from its preset as the document holds it now, each error cleared.</summary>
    /// <param name="preset">The preset, the one with the row's <see cref="Id"/>.</param>
    /// <param name="index">The preset's index into the aircraft's presets.</param>
    /// <param name="count">How many presets the aircraft has.</param>
    public void Update(PresetCommand preset, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Index = index;
        _count = count;
        LoadCommand(preset.Command);
        LoadOffset(preset.TimeOffset);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    internal void LoadCommand(string command) => (Command, CommandError) = (command, null);

    internal void LoadOffset(int seconds) => (Offset, OffsetError) = (ScenarioFieldText.FormatTime(seconds), null);

    /// <summary>Writes the command text, trimmed at its ends.</summary>
    [RelayCommand]
    private void CommitCommand() => _owner.CommitPresetCommand(this);

    /// <summary>Writes the time offset; blank is 0.</summary>
    [RelayCommand]
    private void CommitOffset() => _owner.CommitPresetOffset(this);

    /// <summary>Removes the preset.</summary>
    [RelayCommand]
    private void Remove() => _owner.RemovePreset(this);

    /// <summary>Inserts a copy of the preset, with a new id, directly after it.</summary>
    [RelayCommand]
    private void Duplicate() => _owner.DuplicatePreset(this);

    /// <summary>Swaps the preset with the one before it.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.MovePreset(this, -1);

    /// <summary>Swaps the preset with the one after it.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.MovePreset(this, 1);

    private bool CanMoveUp() => Index > 0;

    private bool CanMoveDown() => Index < (_count - 1);
}
