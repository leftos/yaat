using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's strip placements: a wrapper over <see cref="Scenario.FlightStripConfigurations"/> at
/// <see cref="Index"/>, so every commit edits that item in place and its unmodelled fields survive. The bay is picked
/// from the folder ARTCC's own facilities' bays, its value "{FacilityId}/{BayId}"; a pair the config does not list is
/// still offered. A rack that is not a whole number zero or more shows <see cref="RackError"/> and writes nothing.
/// </summary>
public sealed partial class StripPlacementRowViewModel : ObservableObject
{
    /// <summary>The rack error a rack that is not a number shows.</summary>
    public const string RackErrorText = "Enter a rack number.";

    private static readonly ILogger Log = AppLog.CreateLogger<StripPlacementRowViewModel>();

    private readonly ScenarioEditSession _session;
    private readonly FlightStripConfiguration _item;
    private readonly ScenarioEditApply _apply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BayPick), nameof(BaySelection))]
    private string _bayValue;

    [ObservableProperty]
    private string _rackText;

    [ObservableProperty]
    private string? _rackError;

    [ObservableProperty]
    private string _aircraftText;

    private readonly ObservableCollection<ScenarioPickOption> _bayOptions = [];

    /// <summary>Initializes a new instance of the <see cref="StripPlacementRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.FlightStripConfigurations"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public StripPlacementRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Index = index;
        FlightStripConfiguration strip = session.Document.Scenario.FlightStripConfigurations[index];
        _item = strip;
        _bayValue = BayValueOf(strip.FacilityId, strip.BayId);
        _rackText = strip.Rack.ToString(CultureInfo.InvariantCulture);
        _aircraftText = ScenarioIdListText.Join(strip.AircraftIds);
    }

    /// <summary>Gets the item's index into <see cref="Scenario.FlightStripConfigurations"/>.</summary>
    public int Index { get; }

    /// <summary>Gets whether the document still holds this row's item at its index; a stale row writes nothing.</summary>
    public bool IsCurrent => ScenarioListRow.IsCurrent(_session.Document.Scenario.FlightStripConfigurations, Index, _item);

    /// <summary>The picker value of a facility and bay: "{FacilityId}/{BayId}", or empty when the placement names neither.</summary>
    /// <param name="facilityId">The facility id; null for none.</param>
    /// <param name="bayId">The bay id; null for none.</param>
    /// <returns>The value.</returns>
    public static string BayValueOf(string? facilityId, string? bayId) =>
        (string.IsNullOrEmpty(facilityId) && string.IsNullOrEmpty(bayId)) ? "" : $"{facilityId}/{bayId}";

    /// <summary>Offers the config's bays, plus the row's own pair when the config does not list it.</summary>
    /// <param name="bays">The folder ARTCC's bays.</param>
    public void UpdateOptions(IReadOnlyList<ScenarioPickOption> bays) =>
        ScenarioFieldText.SetOptions(
            _bayOptions,
            ScenarioGeneralViewModel.WithCurrent(bays, BayValue, $"{BayValue} (not in the ARTCC config)"),
            () => OnPropertyChanged(nameof(BaySelection))
        );

    /// <summary>Gets the bay picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> BayOptions => _bayOptions;

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="BayOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="BayPick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? BaySelection
    {
        get => ScenarioFieldText.Selected(BayOptions, BayValue);
        set => BayPick = value?.Value;
    }

    /// <summary>
    /// Gets or sets the bay picker's selection. Null (a picker clearing as its options are swapped) and the value already
    /// held write nothing; any other value is the user's pick and is committed.
    /// </summary>
    public string? BayPick
    {
        get => BayValue;
        set => ScenarioFieldText.Pick(value, BayValue, v => BayValue = v, CommitBay);
    }

    /// <summary>Writes the picked bay's facility and bay ids; the empty value writes none of either.</summary>
    [RelayCommand]
    private void CommitBay()
    {
        if (IsStale())
        {
            return;
        }

        string value = BayValue.Trim();
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        string? facilityId = Optional((slash < 0) ? value : value[..slash]);
        string? bayId = (slash < 0) ? null : Optional(value[(slash + 1)..]);
        BayValue = BayValueOf(facilityId, bayId);
        int index = Index;
        _apply(
            "Change strip bay",
            s =>
            {
                FlightStripConfiguration strip = s.FlightStripConfigurations[index];
                strip.FacilityId = facilityId;
                strip.BayId = bayId;
            }
        );
    }

    /// <summary>Writes the rack when it is a whole number zero or more; otherwise sets <see cref="RackError"/>.</summary>
    [RelayCommand]
    private void CommitRack()
    {
        if (IsStale())
        {
            return;
        }

        if (!int.TryParse(RackText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rack) || (rack < 0))
        {
            RackError = RackErrorText;
            return;
        }

        RackError = null;
        RackText = rack.ToString(CultureInfo.InvariantCulture);
        int index = Index;
        _apply("Change strip rack", s => s.FlightStripConfigurations[index].Rack = rack);
    }

    /// <summary>Writes the aircraft ids: split on commas and whitespace, in order, each id as typed.</summary>
    [RelayCommand]
    private void CommitAircraft()
    {
        if (IsStale())
        {
            return;
        }

        List<string> ids = ScenarioIdListText.Split(AircraftText);
        AircraftText = ScenarioIdListText.Join(ids);
        int index = Index;
        _apply("Change strip aircraft", s => s.FlightStripConfigurations[index].AircraftIds = [.. ids]);
    }

    private bool IsStale() => ScenarioListRow.IsStale(_session.Document.Scenario.FlightStripConfigurations, Index, _item, Log);

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
