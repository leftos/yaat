using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's ATC positions: a wrapper over <see cref="Scenario.Atc"/> at <see cref="Index"/>, so
/// every commit edits that item in place and its unmodelled fields survive. The position is picked from the ARTCC's and
/// its neighbours' catalog, which also gives the entry's facility and ARTCC; a position the catalog does not list is
/// still offered. Auto-connect is written as it changes; the auto-track airports are one text field.
/// </summary>
public sealed partial class ScenarioAtcRowViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioAtcRowViewModel>();

    private readonly ScenarioEditSession _session;
    private readonly ScenarioAtc _item;
    private readonly ScenarioEditApply _apply;
    private readonly Func<string, ArtccPositionRow?> _findPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionPick), nameof(PositionSelection))]
    private string _positionId;

    [ObservableProperty]
    private bool _autoConnect;

    /// <summary>The position's live-validation finding (a position no config lists); null for none.</summary>
    [ObservableProperty]
    private ScenarioFieldIssue? _positionIssue;

    [ObservableProperty]
    private string _autoTrackAirportsText;

    private readonly ObservableCollection<ScenarioPickOption> _positionOptions = [];

    /// <summary>Initializes a new instance of the <see cref="ScenarioAtcRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.Atc"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    /// <param name="findPosition">The catalog row of a position id, or null when the catalog does not list it.</param>
    public ScenarioAtcRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply, Func<string, ArtccPositionRow?> findPosition)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _findPosition = findPosition ?? throw new ArgumentNullException(nameof(findPosition));
        Index = index;
        ScenarioAtc atc = session.Document.Scenario.Atc[index];
        _item = atc;
        _positionId = atc.PositionId;
        _autoConnect = atc.AutoConnect;
        _autoTrackAirportsText = ScenarioIdListText.Join(atc.AutoTrackAirportIds);
    }

    /// <summary>Gets the item's index into <see cref="Scenario.Atc"/>.</summary>
    public int Index { get; }

    /// <summary>Gets whether the document still holds this row's item at its index; a stale row writes nothing.</summary>
    public bool IsCurrent => ScenarioListRow.IsCurrent(_session.Document.Scenario.Atc, Index, _item);

    /// <summary>Offers the catalog's positions, plus the row's own position when the catalog does not list it.</summary>
    /// <param name="catalog">The ARTCC's and its neighbours' positions.</param>
    public void UpdateOptions(IReadOnlyList<ScenarioPickOption> catalog) =>
        ScenarioFieldText.SetOptions(
            _positionOptions,
            ScenarioGeneralViewModel.WithCurrent(catalog, PositionId, $"{PositionId} (not in the ARTCC config)"),
            () => OnPropertyChanged(nameof(PositionSelection))
        );

    /// <summary>Gets the position picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> PositionOptions => _positionOptions;

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="PositionOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="PositionPick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? PositionSelection
    {
        get => ScenarioFieldText.Selected(PositionOptions, PositionId);
        set => PositionPick = value?.Value;
    }

    /// <summary>
    /// Gets or sets the position picker's selection. Null (a picker clearing as its options are swapped) and the value
    /// already held write nothing; any other value is the user's pick and is committed.
    /// </summary>
    public string? PositionPick
    {
        get => PositionId;
        set => ScenarioFieldText.Pick(value, PositionId, v => PositionId = v, CommitPosition);
    }

    /// <summary>
    /// Writes the position id, trimmed; a position the catalog lists also writes its facility and ARTCC, one undo step.
    /// </summary>
    [RelayCommand]
    private void CommitPosition()
    {
        if (IsStale())
        {
            return;
        }

        string positionId = PositionId.Trim();
        PositionId = positionId;
        ArtccPositionRow? row = _findPosition(positionId);
        int index = Index;
        _apply(
            "Change ATC position",
            s =>
            {
                ScenarioAtc atc = s.Atc[index];
                atc.PositionId = positionId;
                if (row is not null)
                {
                    atc.FacilityId = row.FacilityId;
                    atc.ArtccId = row.ArtccId;
                }
            }
        );
    }

    /// <summary>Writes the auto-track airports: split on commas and whitespace, in order, each id as typed.</summary>
    [RelayCommand]
    private void CommitAutoTrackAirports()
    {
        if (IsStale())
        {
            return;
        }

        List<string> ids = ScenarioIdListText.Split(AutoTrackAirportsText);
        AutoTrackAirportsText = ScenarioIdListText.Join(ids);
        int index = Index;
        _apply("Change auto-track airports", s => s.Atc[index].AutoTrackAirportIds = [.. ids]);
    }

    private bool IsStale() => ScenarioListRow.IsStale(_session.Document.Scenario.Atc, Index, _item, Log);

    partial void OnAutoConnectChanged(bool value)
    {
        if (IsStale())
        {
            return;
        }

        int index = Index;
        _apply("Change auto-connect", s => s.Atc[index].AutoConnect = value);
    }
}
