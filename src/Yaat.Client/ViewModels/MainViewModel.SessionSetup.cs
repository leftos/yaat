using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The session-setup popup and the session flyout's "Positions you work" row. The popup has two sections, each shown only
/// when the server asks and closed on its own once answered: the active runways (rows, notes and <c>ARWY</c> answers owned
/// by <c>MainViewModel.ActiveRunways.cs</c>) and the positions the solo student works, asked from the load result or the
/// reply to switching solo training on, never in a live session. The popup shows while either section is open; Cancel
/// closes both and sends nothing, and leaving the scenario clears it. The flyout row sends a change at once and puts a
/// refused one back.
/// </summary>
public partial class MainViewModel
{
    private Func<string, Task<CommandResultDto>>? _studentLowestWorkedLevelSender;

    /// <summary>Bumped on every open and close of the positions section, so an answer still sending can tell it is gone.</summary>
    private int _workedPositionsPromptGeneration;

    /// <summary>The lowest worked level as the server last gave or accepted it: what a refused flyout change returns to.</summary>
    private string? _appliedStudentLowestWorkedLevel;

    /// <summary>Sends the lowest worked level to the hub; replaceable so tests can stand in for it.</summary>
    internal Func<string, Task<CommandResultDto>> StudentLowestWorkedLevelSender
    {
        get => _studentLowestWorkedLevelSender ?? _connection.SetStudentLowestWorkedLevelAsync;
        set => _studentLowestWorkedLevelSender = value;
    }

    /// <summary>The positions section's radios, highest level first.</summary>
    public ObservableCollection<WorkedPositionsChoiceRow> WorkedPositionsPromptChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSessionSetup))]
    [NotifyPropertyChangedFor(nameof(ShowSessionSetupDivider))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSessionSetupCommand))]
    private bool _showWorkedPositionsPrompt;

    /// <summary>The server's refusal of the positions answer, shown under the section; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkedPositionsPromptError))]
    private string? _workedPositionsPromptError;

    public bool HasWorkedPositionsPromptError => WorkedPositionsPromptError is not null;

    /// <summary>True while either section of the popup is open.</summary>
    public bool ShowSessionSetup => ShowActiveRunwaysPrompt || ShowWorkedPositionsPrompt;

    /// <summary>True when both sections show, so a rule separates them.</summary>
    public bool ShowSessionSetupDivider => ShowActiveRunwaysPrompt && ShowWorkedPositionsPrompt;

    /// <summary>The flyout row's selection: the lowest level the solo student works, as a level code; null when unset.</summary>
    [ObservableProperty]
    private string? _sessionStudentLowestWorkedLevel;

    /// <summary>The level of the student's own position, as a level code; null when the scenario has no typed student position.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSessionWorkedPositions))]
    private string? _sessionStudentOwnLevel;

    /// <summary>The flyout row's choices, from the student's own level down to delivery.</summary>
    [ObservableProperty]
    private IReadOnlyList<WorkedPositionsChoice> _sessionWorkedPositionsChoices = [];

    /// <summary>The flyout row shows in a solo session whose student works above delivery.</summary>
    public bool ShowSessionWorkedPositions =>
        SessionSoloTrainingMode
        && (SessionStudentOwnLevel is { } own)
        && AtcPositionLevels.TryParse(own, out AtcPositionLevel level)
        && (level > AtcPositionLevel.Delivery);

    /// <summary>
    /// The loader's own result: the active-runways section as <see cref="ApplyLoadResultActiveRunways"/> decides, and the
    /// positions section opened when the result asks and the session is not live; an earlier positions section closes otherwise.
    /// </summary>
    internal void ApplyLoadResultSessionSetup(LoadScenarioResultDto result)
    {
        ApplyLoadResultActiveRunways(result);
        CloseWorkedPositionsPrompt();
        if ((result.SessionSetup?.WorkedPositions is { } positions) && (!result.IsLiveSession))
        {
            OpenWorkedPositionsPrompt(positions);
        }
    }

    /// <summary>
    /// The reply to switching solo training on: opens the positions section when it asks, outside a live session, beside
    /// an active-runways section already showing. A positions section already open is left as the student has it.
    /// </summary>
    internal void ApplySoloTrainingModeReply(SessionSetupPromptDto? prompt)
    {
        if ((prompt?.WorkedPositions is { } positions) && (!IsLiveSession) && (!ShowWorkedPositionsPrompt))
        {
            OpenWorkedPositionsPrompt(positions);
        }
    }

    /// <summary>
    /// Answers every open section: the active runways first, sent through <paramref name="sendCommand"/> exactly as that
    /// section sends them, then the chosen level through <paramref name="sendLevel"/>. A section whose answer is accepted
    /// closes; a refused one stays open with the server's message.
    /// </summary>
    internal async Task SubmitSessionSetupAsync(
        NavigationDatabase? navDb,
        Func<string, Task<CommandResultDto>> sendCommand,
        Func<string, Task<CommandResultDto>> sendLevel
    )
    {
        if (ShowActiveRunwaysPrompt)
        {
            await SubmitActiveRunwaysPromptAsync(navDb, sendCommand);
        }

        if (ShowWorkedPositionsPrompt)
        {
            await SubmitWorkedPositionsPromptAsync(sendLevel);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmSessionSetup))]
    private Task ConfirmSessionSetupAsync() =>
        SubmitSessionSetupAsync(
            NavigationDatabase.InstanceOrNull,
            command => _connection.SendCommandAsync("", command, _preferences.UserInitials),
            StudentLowestWorkedLevelSender
        );

    private bool CanConfirmSessionSetup() =>
        ((!ShowActiveRunwaysPrompt) || CanConfirmActiveRunwaysPrompt())
        && ((!ShowWorkedPositionsPrompt) || (SelectedWorkedPositionsCode() is not null));

    /// <summary>Closes both sections and sends nothing; an open active-runways section says how to set them later.</summary>
    [RelayCommand]
    private void CancelSessionSetup()
    {
        if (ShowActiveRunwaysPrompt)
        {
            CancelActiveRunwaysPrompt();
        }

        CloseWorkedPositionsPrompt();
    }

    partial void OnShowActiveRunwaysPromptChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSessionSetup));
        OnPropertyChanged(nameof(ShowSessionSetupDivider));
        ConfirmSessionSetupCommand.NotifyCanExecuteChanged();
    }

    private async Task SubmitWorkedPositionsPromptAsync(Func<string, Task<CommandResultDto>> send)
    {
        if (SelectedWorkedPositionsCode() is not { } code)
        {
            return;
        }

        int generation = _workedPositionsPromptGeneration;
        WorkedPositionsPromptError = null;
        CommandResultDto result = await SendStudentLowestWorkedLevelAsync(send, code);
        if (generation != _workedPositionsPromptGeneration)
        {
            return;
        }

        if (result.Success)
        {
            CloseWorkedPositionsPrompt();
        }
        else
        {
            WorkedPositionsPromptError = result.Message ?? $"Positions you work ({code}) was refused";
        }
    }

    private string? SelectedWorkedPositionsCode() => WorkedPositionsPromptChoices.FirstOrDefault(row => row.IsSelected)?.Code;

    private void OpenWorkedPositionsPrompt(WorkedPositionsSetupDto positions)
    {
        CloseWorkedPositionsPrompt();
        List<WorkedPositionsChoice> choices =
        [
            .. WorkedPositionsChoices.For(positions.Own).Where(choice => positions.Choices.Contains(choice.Code, StringComparer.Ordinal)),
        ];
        if (choices.Count == 0)
        {
            _log.LogWarning("Positions-you-work question skipped: no choice for own level {Own} among {Choices}", positions.Own, positions.Choices);
            return;
        }

        _workedPositionsPromptGeneration++;
        foreach (WorkedPositionsChoice choice in choices)
        {
            var row = new WorkedPositionsChoiceRow(choice, choice.Code == positions.Prefill);
            row.PropertyChanged += OnWorkedPositionsChoiceChanged;
            WorkedPositionsPromptChoices.Add(row);
        }

        ShowWorkedPositionsPrompt = true;
        ConfirmSessionSetupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Choosing one radio clears the others, so the section always holds at most one answer.</summary>
    private void OnWorkedPositionsChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName != nameof(WorkedPositionsChoiceRow.IsSelected)) || (sender is not WorkedPositionsChoiceRow { IsSelected: true } chosen))
        {
            ConfirmSessionSetupCommand.NotifyCanExecuteChanged();
            return;
        }

        foreach (WorkedPositionsChoiceRow row in WorkedPositionsPromptChoices.Where(row => !ReferenceEquals(row, chosen)))
        {
            row.IsSelected = false;
        }

        ConfirmSessionSetupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A level the flyout set while the positions section is open moves the section's radio to it; a level the section
    /// does not offer closes the section, since the question is answered.
    /// </summary>
    private void MoveWorkedPositionsPromptTo(string level)
    {
        if (!ShowWorkedPositionsPrompt)
        {
            return;
        }

        if (WorkedPositionsPromptChoices.FirstOrDefault(row => row.Code == level) is { } match)
        {
            match.IsSelected = true;
        }
        else
        {
            CloseWorkedPositionsPrompt();
        }
    }

    /// <summary>Closes both sections of the popup: another load, a recording load or leaving the scenario.</summary>
    private void CloseSessionSetup()
    {
        CloseActiveRunwaysPrompt();
        CloseWorkedPositionsPrompt();
    }

    private void CloseWorkedPositionsPrompt()
    {
        _workedPositionsPromptGeneration++;
        ShowWorkedPositionsPrompt = false;
        WorkedPositionsPromptError = null;
        foreach (WorkedPositionsChoiceRow row in WorkedPositionsPromptChoices)
        {
            row.PropertyChanged -= OnWorkedPositionsChoiceChanged;
        }

        WorkedPositionsPromptChoices.Clear();
    }

    /// <summary>Turns solo training on or off and applies the reply's positions section on the UI thread.</summary>
    private Task SendSoloTrainingModeAsync(bool enabled) => RequestSoloTrainingModeAsync(() => _connection.SetSoloTrainingModeAsync(enabled));

    /// <summary>
    /// Sends a solo-mode switch through <paramref name="request"/> and applies its reply on the UI thread, unless another
    /// scenario was loaded while the reply was on its way.
    /// </summary>
    internal async Task RequestSoloTrainingModeAsync(Func<Task<SessionSetupPromptDto?>> request)
    {
        string? scenarioId = ActiveScenarioId;
        try
        {
            SessionSetupPromptDto? prompt = await request();
            Dispatcher.UIThread.Post(() =>
            {
                if (ActiveScenarioId != scenarioId)
                {
                    _log.LogDebug("Solo training reply for scenario {Sent} dropped: scenario {Now} is loaded now", scenarioId, ActiveScenarioId);
                    return;
                }

                ApplySoloTrainingModeReply(prompt);
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to set solo training mode");
        }
    }

    private async Task<CommandResultDto> SendStudentLowestWorkedLevelAsync(Func<string, Task<CommandResultDto>> send, string level)
    {
        try
        {
            return await send(level);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Setting the lowest position worked to {Level} failed", level);
            return new CommandResultDto(false, ex.Message);
        }
    }

    /// <summary>The flyout row's choices follow the student's own level.</summary>
    partial void OnSessionStudentOwnLevelChanged(string? value) =>
        SessionWorkedPositionsChoices = value is null ? [] : WorkedPositionsChoices.For(value);

    /// <summary>A change the user makes in the flyout row reaches the hub at once; a push from the server never does.</summary>
    partial void OnSessionStudentLowestWorkedLevelChanged(string? value)
    {
        if (_isApplyingSessionSettings || (value is null) || (value == _appliedStudentLowestWorkedLevel))
        {
            return;
        }

        _ = SendFlyoutStudentLowestWorkedLevelAsync(value);
    }

    /// <summary>The server refuses the level outside solo training; put the row back and say why in the terminal.</summary>
    private async Task SendFlyoutStudentLowestWorkedLevelAsync(string level)
    {
        CommandResultDto result = await SendStudentLowestWorkedLevelAsync(StudentLowestWorkedLevelSender, level);
        if (result.Success)
        {
            _appliedStudentLowestWorkedLevel = level;
            Dispatcher.UIThread.Post(() => MoveWorkedPositionsPromptTo(level));
            return;
        }

        _log.LogWarning("Server refused the lowest position worked {Level}: {Reason}", level, result.Message);
        Dispatcher.UIThread.Post(() =>
        {
            _isApplyingSessionSettings = true;
            SessionStudentLowestWorkedLevel = _appliedStudentLowestWorkedLevel;
            _isApplyingSessionSettings = false;
            AddSystemEntry(result.Message ?? $"Positions you work ({level}) was refused");
        });
    }

    /// <summary>Applies a session-settings payload's two level fields; called inside <see cref="ApplySessionSettings"/>.</summary>
    private void ApplySessionStudentLevels(string? lowest, string? own)
    {
        SessionStudentOwnLevel = own;
        _appliedStudentLowestWorkedLevel = lowest;
        SessionStudentLowestWorkedLevel = lowest;
    }
}
