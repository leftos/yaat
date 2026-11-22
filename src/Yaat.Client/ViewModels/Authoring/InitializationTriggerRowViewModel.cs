using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's initialization triggers: a wrapper over <see cref="Scenario.InitializationTriggers"/>
/// at <see cref="Index"/>, so every commit edits that item in place and its unmodelled fields survive. The time is read
/// through <see cref="ScenarioTime"/>: a time that does not parse shows <see cref="TimeError"/>, keeps the text and writes
/// nothing, so the document keeps its last good time.
/// </summary>
public sealed partial class InitializationTriggerRowViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<InitializationTriggerRowViewModel>();

    private readonly ScenarioEditSession _session;
    private readonly InitializationTrigger _item;
    private readonly ScenarioEditApply _apply;

    [ObservableProperty]
    private string _command;

    [ObservableProperty]
    private string _timeText;

    [ObservableProperty]
    private string? _timeError;

    /// <summary>Initializes a new instance of the <see cref="InitializationTriggerRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.InitializationTriggers"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public InitializationTriggerRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Index = index;
        InitializationTrigger trigger = session.Document.Scenario.InitializationTriggers[index];
        _item = trigger;
        _command = trigger.Command;
        _timeText = ScenarioFieldText.FormatTime(trigger.TimeOffset);
    }

    /// <summary>Gets the item's index into <see cref="Scenario.InitializationTriggers"/>.</summary>
    public int Index { get; }

    /// <summary>Gets whether the document still holds this row's item at its index; a stale row writes nothing.</summary>
    public bool IsCurrent => ScenarioListRow.IsCurrent(_session.Document.Scenario.InitializationTriggers, Index, _item);

    private bool IsStale() => ScenarioListRow.IsStale(_session.Document.Scenario.InitializationTriggers, Index, _item, Log);

    /// <summary>Writes the command, trimmed; blank is written as it is.</summary>
    [RelayCommand]
    private void CommitCommand()
    {
        if (IsStale())
        {
            return;
        }

        string command = Command.Trim();
        Command = command;
        int index = Index;
        _apply("Change trigger command", s => s.InitializationTriggers[index].Command = command);
    }

    /// <summary>Writes the time when it parses and shows it formatted; otherwise sets <see cref="TimeError"/>.</summary>
    [RelayCommand]
    private void CommitTime()
    {
        if (IsStale())
        {
            return;
        }

        if (ScenarioTime.Parse(TimeText) is not { } seconds)
        {
            TimeError = ScenarioFieldText.TimeErrorText;
            return;
        }

        TimeError = null;
        TimeText = ScenarioTime.Format(seconds);
        int index = Index;
        _apply("Change trigger time", s => s.InitializationTriggers[index].TimeOffset = seconds);
    }
}
