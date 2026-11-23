using System.ComponentModel;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Aircraft tab's parking picker: which airport's map it opens (the scenario loader's rule for a Parking start: the
/// aircraft's airport, else the scenario's primary airport, else the flight plan's departure), whether it is offered (a
/// Parking start and an airport known), and the picked stand written through the parking field's own commit.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    // The General tab's primary airport field, relayed by the editor; until the first relay the document's value is read.
    private string? _primaryAirportId;
    private bool _primaryAirportRelayed;

    /// <summary>Gets the preferences the parking picker keeps its position and size in.</summary>
    public UserPreferences Preferences => _services.Preferences;

    /// <summary>
    /// Gets or sets the scenario's primary airport as the General tab's field shows it, relayed by the editor so the
    /// picker's airport follows that field; until it is first set, the document's primary airport is read.
    /// </summary>
    public string? PrimaryAirportId
    {
        get => _primaryAirportRelayed ? _primaryAirportId : _session.Document.Scenario.PrimaryAirportId;
        set
        {
            _primaryAirportRelayed = true;
            SetProperty(ref _primaryAirportId, value);
        }
    }

    /// <summary>
    /// Gets the airport the parking picker opens: the aircraft's airport field, else the scenario's primary airport, else
    /// the flight plan's departure; null when none is set.
    /// </summary>
    public string? ParkingAirportId => NonBlank(Airport) ?? NonBlank(PrimaryAirportId) ?? (HasFlightPlan ? NonBlank(Departure) : null);

    /// <summary>Gets a value indicating whether the parking picker is offered: an aircraft with a Parking start and an airport.</summary>
    public bool CanPickParking =>
        (Selected is not null)
        && string.Equals(StartKind, StartingConditionKinds.Parking, StringComparison.Ordinal)
        && (ParkingAirportId is not null);

    /// <summary>Writes a stand picked on the map as the parking, through the parking field's commit: one "Edit parking" step.</summary>
    /// <param name="stand">The stand's name.</param>
    public void ApplyPickedParking(string stand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stand);
        Parking = stand;
        CommitParking();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (
            e.PropertyName
            is nameof(Airport)
                or nameof(PrimaryAirportId)
                or nameof(StartKind)
                or nameof(Departure)
                or nameof(HasFlightPlan)
                or nameof(Selected)
        )
        {
            OnPropertyChanged(nameof(ParkingAirportId));
            OnPropertyChanged(nameof(CanPickParking));
        }
    }

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
