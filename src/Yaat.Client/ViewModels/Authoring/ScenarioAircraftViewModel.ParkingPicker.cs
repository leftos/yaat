using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;

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

    /// <summary>
    /// What Assign Gate ranks the selected aircraft with: the scenario, the aircraft, the navdata's sidecars (none while
    /// navdata loads) and the draw; null when the picker is not offered (<see cref="CanPickParking"/>).
    /// </summary>
    /// <param name="random">The draw for the Random and In area modes.</param>
    /// <returns>The ranking context, or null.</returns>
    public GateAssignContext? CreateGateAssignContext(Random random) => CreateGateAssignContext(random, NavigationDatabase.InstanceOrNull);

    /// <summary>The ranking context over a given navdata; null navdata ranks without sidecars. Test seam.</summary>
    internal GateAssignContext? CreateGateAssignContext(Random random, NavigationDatabase? navdata)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!CanPickParking || (SelectedDocumentIndex("Assign gate") is not { } index))
        {
            return null;
        }

        Scenario scenario = _session.Document.Scenario;
        return GateAssignContext.Create(scenario, scenario.Aircraft[index], random, navdata);
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

/// <summary>One Assign Gate draw: the stand drawn and the ranking it was drawn from.</summary>
/// <param name="Stand">The drawn stand's name, or null when no free stand fits.</param>
/// <param name="Ranked">Every stand in scope with its reason (<see cref="GateRanking.Rank"/>).</param>
public sealed record GateAssignment(string? Stand, IReadOnlyList<GateCandidate> Ranked);

/// <summary>
/// What Assign Gate ranks one scenario aircraft's stands with (<see cref="GateRanking.Rank"/>), and the words the picker
/// shows for each reason.
/// </summary>
/// <param name="Scenario">The scenario whose other aircraft may hold stands.</param>
/// <param name="Aircraft">The aircraft a stand is wanted for.</param>
/// <param name="Sidecars">The sidecars supplying the airport's parking areas; empty while navdata loads.</param>
/// <param name="NavdataLoading">Whether navdata is still loading, so the ranking has no areas or airline tags.</param>
/// <param name="Random">The draw for the Random and In area modes.</param>
public sealed record GateAssignContext(
    Scenario Scenario,
    ScenarioAircraft Aircraft,
    AirportSidecarCatalog Sidecars,
    bool NavdataLoading,
    Random Random
)
{
    /// <summary>The note the picker shows while navdata loads.</summary>
    public const string NavdataLoadingText = "Navdata loading; ranking without airline tags";

    /// <summary>The status line when no free stand fits.</summary>
    public const string NoFitText = "No free stand fits; parking unchanged";

    /// <summary>The context over a navdata's sidecars, or none while it loads.</summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="random">The draw.</param>
    /// <param name="navdata">The navdata, or null while it loads.</param>
    /// <returns>The context.</returns>
    public static GateAssignContext Create(Scenario scenario, ScenarioAircraft aircraft, Random random, NavigationDatabase? navdata) =>
        new(scenario, aircraft, navdata?.AirportSidecars ?? AirportSidecarCatalog.Empty, navdata is null, random);

    /// <summary>Ranks the layout's stands in scope for the aircraft.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="scope">Every stand, or one parking area's.</param>
    /// <returns>The ranking.</returns>
    public IReadOnlyList<GateCandidate> Rank(AirportGroundLayout layout, GateScope scope) =>
        GateRanking.Rank(layout, Sidecars, Scenario, Aircraft, scope);

    /// <summary>Ranks the stands in scope and draws one of the best fitting tier (<see cref="GateRanking.Pick"/>).</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="scope">Every stand, or one parking area's.</param>
    /// <returns>The draw and its ranking.</returns>
    public GateAssignment Assign(AirportGroundLayout layout, GateScope scope)
    {
        IReadOnlyList<GateCandidate> ranked = Rank(layout, scope);
        return new GateAssignment(GateRanking.Pick(ranked, Random)?.Stand, ranked);
    }

    /// <summary>The parking areas a ranking's stands lie in, concrete names for templated ones, in ordinal order.</summary>
    /// <param name="ranked">A ranking of every stand.</param>
    /// <returns>The area names, each usable as a scope.</returns>
    public static IReadOnlyList<string> AreasOf(IReadOnlyList<GateCandidate> ranked) =>
        [.. ranked.Select(c => c.Area).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The words for a stand's reason, as the picker's list and status line show them.</summary>
    /// <param name="candidate">The ranked stand.</param>
    /// <returns>The text, e.g. <c>too small (span cap 118 ft)</c>.</returns>
    public string ReasonText(GateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Reason switch
        {
            GateReason.Preferred => $"preferred for {Airline}",
            GateReason.Fits => "fits",
            GateReason.TooSmall => string.Create(CultureInfo.InvariantCulture, $"too small (span cap {candidate.MaxWingspanFt:F0} ft)"),
            GateReason.OtherAirline => "another airline's",
            GateReason.Occupied => $"taken until {Until(candidate.Occupant?.Window.EndSeconds)}",
            _ => candidate.Reason.ToString(),
        };
    }

    private string Airline =>
        GateRanking.AirlineOf(Aircraft.AircraftId)
        ?? throw new UnreachableException($"A preferred stand for {Aircraft.AircraftId}, which names no airline");

    private static string Until(int? seconds) =>
        (seconds is { } s)
            ? TimeSpan.FromSeconds(s).ToString((s >= 3600) ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
            : "the scenario's end";
}
