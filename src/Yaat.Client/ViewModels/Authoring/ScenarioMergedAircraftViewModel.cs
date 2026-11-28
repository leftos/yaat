using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What the MERGED AIRCRAFT editor works through.</summary>
/// <param name="Browser">What the Scenario Editor's aircraft view works through: preferences, clock, ids and the UI-thread post.</param>
/// <param name="FetchLayout">Fetches an airport's ground layout for the live checks; null when it has none.</param>
/// <param name="NavDataReady">The startup navdata load the live checks run again after.</param>
public sealed record ScenarioMergedAircraftServices(
    ScenarioBrowserServices Browser,
    Func<string, CancellationToken, Task<AirportGroundLayout?>> FetchLayout,
    Task NavDataReady
);

/// <summary>Where the MERGED AIRCRAFT editor opens.</summary>
/// <param name="ItemKey">The aircraft line's key.</param>
/// <param name="ArtccId">The ARTCC the scenario belongs to.</param>
/// <param name="ScenarioId">The scenario's id.</param>
/// <param name="MergedText">The merge so far.</param>
/// <param name="Index">The aircraft's index in the merge so far.</param>
internal sealed record ScenarioMergedAircraftSeed(string ItemKey, string ArtccId, string ScenarioId, string MergedText, int Index);

/// <summary>
/// The merge window's MERGED AIRCRAFT editor: one aircraft of the merge so far in the Scenario Editor's aircraft view, over a
/// session opened on that text (no working copy, so nothing is ever saved), with the editor's live checks for its marks.
/// Keep this hands the editor back to the window, which takes the aircraft when it differs from its pre-filled text (found by
/// its id, else at its index while the aircraft count is unchanged); Cancel hands it back to be closed.
/// </summary>
public sealed partial class ScenarioMergedAircraftViewModel : ObservableObject, IDisposable
{
    private const string AircraftKey = "aircraft";

    private readonly Action<ScenarioMergedAircraftViewModel> _keep;

    private readonly Action<ScenarioMergedAircraftViewModel> _cancel;

    private readonly JsonObject _prefilled;

    private readonly string? _id;

    private readonly int _count;

    internal ScenarioMergedAircraftViewModel(
        ScenarioMergedAircraftSeed seed,
        ScenarioMergedAircraftServices services,
        Action<ScenarioMergedAircraftViewModel> keep,
        Action<ScenarioMergedAircraftViewModel> cancel
    )
    {
        ItemKey = seed.ItemKey;
        Index = seed.Index;
        _keep = keep;
        _cancel = cancel;
        Session = ScenarioEditSession.OverText(seed.ArtccId, seed.ScenarioId, seed.MergedText);
        JsonArray aircraft = AircraftList();
        _count = aircraft.Count;
        _prefilled = aircraft[Index]!.DeepClone().AsObject();
        _id = ScenarioMergeItemKeys.IdOf(_prefilled);
        Callsign = ((_prefilled["aircraftId"] is JsonValue value) && value.TryGetValue(out string? callsign)) ? callsign : ItemKey;
        Aircraft = new ScenarioAircraftViewModel(Session, services.Browser);
        Aircraft.Selected = Aircraft.Rows[Index];
        Validation = new ScenarioValidationViewModel(
            Session,
            services.Browser.Post,
            () => NavigationDatabase.InstanceOrNull is not null,
            services.FetchLayout,
            services.NavDataReady
        );
        Aircraft.Edited += OnEdited;
        Validation.Recompute();
    }

    /// <summary>Gets the aircraft line's key.</summary>
    public string ItemKey { get; }

    /// <summary>Gets the aircraft's callsign as it was pre-filled, else its key.</summary>
    public string Callsign { get; }

    /// <summary>Gets the aircraft's index in the merge so far, the session's document.</summary>
    public int Index { get; }

    /// <summary>Gets the session over the merge so far; it has no working copy behind it.</summary>
    public ScenarioEditSession Session { get; }

    /// <summary>Gets the Scenario Editor's aircraft view over the merge so far, with the aircraft selected.</summary>
    public ScenarioAircraftViewModel Aircraft { get; }

    /// <summary>Gets the live checks over the merge so far, for the aircraft's field marks.</summary>
    public ScenarioValidationViewModel Validation { get; }

    /// <summary>Stops the live checks; the editor is closed.</summary>
    public void Dispose()
    {
        Aircraft.Edited -= OnEdited;
        Validation.Dispose();
    }

    /// <summary>The aircraft as edited: found by its id, else at its index while the aircraft count is unchanged.</summary>
    /// <param name="changed">The aircraft when it differs from its pre-filled text; null when it reads as it was pre-filled.</param>
    /// <returns>False when the aircraft was removed in the editor.</returns>
    internal bool TryTakeEdited(out JsonObject? changed)
    {
        changed = null;
        List<JsonObject?> aircraft = [.. AircraftList().Select(node => node as JsonObject)];
        int index = (_id is { } id) ? aircraft.FindIndex(node => ScenarioMergeItemKeys.IdOf(node) == id) : ((aircraft.Count == _count) ? Index : -1);
        if ((index < 0) || (aircraft[index] is not { } found))
        {
            return false;
        }

        changed = JsonNode.DeepEquals(found, _prefilled) ? null : found.DeepClone().AsObject();
        return true;
    }

    [RelayCommand]
    private void KeepThis() => _keep(this);

    [RelayCommand]
    private void Cancel() => _cancel(this);

    private void OnEdited(object? sender, EventArgs e) => Validation.Recompute();

    private JsonArray AircraftList() => JsonNode.Parse(Session.Document.Write())![AircraftKey] as JsonArray ?? [];
}
