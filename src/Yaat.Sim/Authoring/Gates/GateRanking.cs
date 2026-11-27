using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Gates;

/// <summary>
/// Ranks an airport layout's parking stands for one scenario aircraft by size, airline tag and occupancy, for Assign Gate.
/// Pure and deterministic: nothing ticks, and the one random choice (<see cref="Pick"/>) draws from a caller's
/// <see cref="Random"/>.
///
/// <para>The candidates are the layout's named parking nodes, one per name, less the sidecar's <c>nonGateParking</c>.
/// Size is the runtime arrival pick's rule: the type's wingspan against the stand's cap (<see cref="StandFit"/>, the
/// area's <c>maxAdg</c> replacing the spacing estimate). When the type has no FAA design group or span, its wake class
/// sets a floor (Super at least ADG VI, Heavy at least ADG IV). A known span past ADG VI's ceiling fits only an area
/// marked ADG VI.</para>
///
/// <para>Airline: a stand whose area is tagged with the callsign's ICAO operator is preferred; a stand in an area tagged
/// only for other airlines is left out, and so is every tagged area for a callsign that is not airline-style
/// (<see cref="ArrivalParkingPicker.IsAirlineStyleCallsign"/>). Occupancy: another aircraft starting at the stand
/// (same airport, name matched ignoring case and spaces) holds it from its spawn until its spawn plus its
/// <c>yaatTaxiSeconds</c> plus <see cref="StandClearBufferSeconds"/>, or to the scenario's end with no taxi time; this
/// aircraft holds a stand by the same rule, and the stand is taken when the two windows overlap.</para>
/// </summary>
public static class GateRanking
{
    /// <summary>Seconds an occupant holds its stand past its taxi time, for push and engine start.</summary>
    public const int StandClearBufferSeconds = 300;

    private static readonly ILogger Log = SimLog.CreateLogger("GateRanking");

    /// <summary>
    /// Whether a span fits a stand: a known span at or past ADG VI's ceiling fits only an area whose <c>maxAdg</c> is VI;
    /// any other span fits a stand whose cap is at least the span.
    /// </summary>
    /// <param name="spanFt">The span the aircraft needs, feet.</param>
    /// <param name="spanKnown">Whether the span is the type's FAA record span, not a fallback or a wake floor.</param>
    /// <param name="capFt">The stand's span cap, feet.</param>
    /// <param name="areaMaxAdg">The <c>maxAdg</c> of the stand's area, or null.</param>
    /// <returns>True when the aircraft fits.</returns>
    internal static bool FitsSpan(double spanFt, bool spanKnown, double capFt, AirplaneDesignGroup? areaMaxAdg) =>
        (spanKnown && (spanFt >= AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroup.VI)))
            ? (areaMaxAdg == AirplaneDesignGroup.VI)
            : (spanFt <= capFt);

    /// <summary>
    /// Every stand in scope with its reason: the fitting ones first, preferred before fitting, then the rejected ones by
    /// reason; each group in ordinal stand-name order.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="sidecars">The sidecars supplying the airport's <c>parkingAreas</c> and <c>nonGateParking</c>.</param>
    /// <param name="scenario">The scenario whose other aircraft may hold stands.</param>
    /// <param name="aircraft">The aircraft a stand is wanted for; skipped among the scenario's aircraft.</param>
    /// <param name="scope">Every stand, or one parking area's.</param>
    /// <returns>The ranked stands; empty when the scope names no area of the airport's sidecar.</returns>
    public static IReadOnlyList<GateCandidate> Rank(
        AirportGroundLayout layout,
        AirportSidecarCatalog sidecars,
        Scenario scenario,
        ScenarioAircraft aircraft,
        GateScope scope
    )
    {
        if (ScopedStands(layout, sidecars, scope) is not { } stands)
        {
            return [];
        }

        var judge = new StandJudge(layout, aircraft, Occupants(layout.AirportId, scenario, aircraft));
        List<GateCandidate> ranked =
        [
            .. stands
                .Select(s => judge.Judge(s.Node, s.Name, s.Area, s.AreaName))
                .OrderBy(c => c.Reason)
                .ThenBy(c => c.Stand, StringComparer.Ordinal),
        ];
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "{Airport} {Callsign} ({Type}): {Count} stand(s) ranked, {Fitting} fitting",
                layout.AirportId,
                aircraft.AircraftId,
                aircraft.AircraftType,
                ranked.Count,
                ranked.Count(c => c.IsFit)
            );
        }

        return ranked;
    }

    /// <summary>
    /// A random stand of the best fitting tier: a <see cref="GateReason.Preferred"/> one when any is, else a
    /// <see cref="GateReason.Fits"/> one, drawn in ordinal stand-name order.
    /// </summary>
    /// <param name="candidates">The ranking (<see cref="Rank"/>).</param>
    /// <param name="random">The draw; a seeded one gives the same pick for the same ranking.</param>
    /// <returns>The pick; null when no stand fits.</returns>
    public static GateCandidate? Pick(IReadOnlyList<GateCandidate> candidates, Random random)
    {
        GateReason tier = candidates.Any(c => c.Reason == GateReason.Preferred) ? GateReason.Preferred : GateReason.Fits;
        List<GateCandidate> pool = [.. candidates.Where(c => c.Reason == tier).OrderBy(c => c.Stand, StringComparer.Ordinal)];
        return (pool.Count == 0) ? null : pool[random.Next(pool.Count)];
    }

    /// <summary>
    /// The stands in scope, one per trimmed name (case-insensitive, first in layout order), less <c>nonGateParking</c>, each
    /// with the area it lies in: the scoped area, else the first sidecar area covering it. Null when the scope's area is
    /// not one of the airport's.
    /// </summary>
    private static List<(GroundNode Node, string Name, ParkingAreaAlias? Area, string? AreaName)>? ScopedStands(
        AirportGroundLayout layout,
        AirportSidecarCatalog sidecars,
        GateScope scope
    )
    {
        IReadOnlyList<ParkingAreaAlias> areas = sidecars.GetParkingAreas(layout.AirportId);
        IReadOnlySet<string> nonGate = sidecars.GetNonGateParking(layout.AirportId);
        List<(GroundNode Node, string Name, ParkingAreaAlias? Area, string? AreaName)> stands;
        if (scope.AreaName is { } areaName)
        {
            if (ArrivalExitSide.AreaStands(areaName.ToUpperInvariant(), layout, areas) is not { } match)
            {
                return null;
            }

            string concrete = Compact(areaName);
            stands = [.. match.Stands.Select(s => (s.Node, s.Name, (ParkingAreaAlias?)match.Alias, (string?)concrete))];
        }
        else
        {
            stands = [.. AllStands(layout, areas)];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. stands
                .Select(s => (s.Node, Name: s.Name.Trim(), s.Area, s.AreaName))
                .Where(s => (s.Name.Length > 0) && !nonGate.Contains(s.Name) && seen.Add(s.Name)),
        ];
    }

    /// <summary>Every named parking node in layout order, with the first sidecar area that covers it.</summary>
    private static IEnumerable<(GroundNode Node, string Name, ParkingAreaAlias? Area, string? AreaName)> AllStands(
        AirportGroundLayout layout,
        IReadOnlyList<ParkingAreaAlias> areas
    )
    {
        var areaOf = new Dictionary<GroundNode, (ParkingAreaAlias Alias, string Name)>(ReferenceEqualityComparer.Instance);
        foreach ((ParkingAreaAlias alias, List<(GroundNode Node, string Name, string Area)> covered) in ArrivalExitSide.AllAreaStands(layout, areas))
        {
            foreach ((GroundNode node, _, string areaName) in covered)
            {
                areaOf.TryAdd(node, (alias, areaName));
            }
        }

        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node is { Type: GroundNodeType.Parking, Name: { } name }) && !string.IsNullOrWhiteSpace(name))
            {
                bool inArea = areaOf.TryGetValue(node, out (ParkingAreaAlias Alias, string Name) area);
                yield return (node, name, inArea ? area.Alias : null, inArea ? area.Name : null);
            }
        }
    }

    /// <summary>
    /// The scenario's other aircraft that start at parking at the layout's airport (their own airport, else the scenario's
    /// primary one), by compacted, upper-cased stand name.
    /// </summary>
    private static Dictionary<string, List<GateOccupant>> Occupants(string airportId, Scenario scenario, ScenarioAircraft self)
    {
        string airport = NavigationDatabase.NormalizeAirport(airportId);
        var byStand = new Dictionary<string, List<GateOccupant>>(StringComparer.Ordinal);
        foreach (ScenarioAircraft other in scenario.Aircraft)
        {
            if (IsSelf(other, self) || (ParkedStand(other) is not { } stand))
            {
                continue;
            }

            string? otherAirport = other.AirportId ?? scenario.PrimaryAirportId;
            if (
                string.IsNullOrWhiteSpace(otherAirport)
                || !string.Equals(NavigationDatabase.NormalizeAirport(otherAirport), airport, StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (!byStand.TryGetValue(stand, out List<GateOccupant>? list))
            {
                byStand[stand] = list = [];
            }

            list.Add(new GateOccupant(other.AircraftId, WindowOf(other)));
        }

        return byStand;
    }

    private static bool IsSelf(ScenarioAircraft other, ScenarioAircraft self) =>
        ReferenceEquals(other, self) || ((self.Id.Length > 0) && string.Equals(other.Id, self.Id, StringComparison.Ordinal));

    /// <summary>The compacted, upper-cased stand a Parking start names, or null for any other start.</summary>
    private static string? ParkedStand(ScenarioAircraft aircraft) =>
        (string.Equals(aircraft.StartingConditions.Type, StartingConditionKinds.Parking, StringComparison.Ordinal))
        && (aircraft.StartingConditions.Parking is { } parking)
        && (Compact(parking) is { Length: > 0 } stand)
            ? stand
            : null;

    /// <summary>The name without whitespace, upper-cased: the form stand names are matched in.</summary>
    internal static string Compact(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>
    /// The time an aircraft holds its stand: from its spawn until its spawn plus its <c>yaatTaxiSeconds</c> plus
    /// <see cref="StandClearBufferSeconds"/>, or to the scenario's end when it has no whole, non-negative taxi time. The one
    /// rule for the ranked aircraft and every occupant alike.
    /// </summary>
    internal static GateWindow WindowOf(ScenarioAircraft aircraft)
    {
        int? taxiSeconds =
            (aircraft.ExtensionData?.TryGetValue(WorkingCopyExporter.AircraftTaxiSecondsKey, out JsonElement value) == true)
            && (value.ValueKind == JsonValueKind.Number)
            && value.TryGetInt32(out int seconds)
            && (seconds >= 0)
                ? seconds
                : null;
        return new GateWindow(aircraft.SpawnDelay, aircraft.SpawnDelay + taxiSeconds + StandClearBufferSeconds);
    }

    /// <summary>One aircraft's judgement of each stand: its span need, its airline and its window, worked out once.</summary>
    private sealed class StandJudge
    {
        private readonly AirportGroundLayout _layout;
        private readonly AirplaneDesignGroup _ceiling;
        private readonly double _spanFt;
        private readonly bool _spanKnown;
        private readonly string? _airline;
        private readonly GateWindow _window;
        private readonly Dictionary<string, List<GateOccupant>> _occupants;
        private readonly Dictionary<ParkingAreaAlias, double> _areaCapFt = new(ReferenceEqualityComparer.Instance);

        internal StandJudge(AirportGroundLayout layout, ScenarioAircraft aircraft, Dictionary<string, List<GateOccupant>> occupants)
        {
            _layout = layout;
            _ceiling = AirplaneDesignGroups.AirportCeiling(layout);
            (_spanFt, _spanKnown) = RequiredSpan(aircraft.AircraftType);
            _airline = ArrivalParkingPicker.IsAirlineStyleCallsign(aircraft.AircraftId) ? aircraft.AircraftId[..3] : null;
            _window = WindowOf(aircraft);
            _occupants = occupants;
            if (_spanKnown && (_spanFt >= AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroup.VI)))
            {
                Log.LogWarning(
                    "{Callsign} ({Type}): its {Span:F0} ft span is past ADG VI's ceiling; it fits only an area whose maxAdg is VI",
                    aircraft.AircraftId,
                    aircraft.AircraftType,
                    _spanFt
                );
            }
        }

        internal GateCandidate Judge(GroundNode node, string name, ParkingAreaAlias? area, string? areaName)
        {
            double capFt = CapFt(node, area);
            GateOccupant? occupant = _occupants.GetValueOrDefault(Compact(name))?.FirstOrDefault(o => o.Window.Overlaps(_window));
            GateReason reason = Reason(capFt, area, occupant);
            return new GateCandidate(name, node, areaName, reason, capFt, reason == GateReason.Occupied ? occupant : null);
        }

        private GateReason Reason(double capFt, ParkingAreaAlias? area, GateOccupant? occupant)
        {
            if (!FitsSpan(_spanFt, _spanKnown, capFt, area?.MaxAdg))
            {
                return GateReason.TooSmall;
            }

            if ((area is { Airlines.Count: > 0 } tagged) && ((_airline is null) || !tagged.Airlines.Contains(_airline, StringComparer.Ordinal)))
            {
                return GateReason.OtherAirline;
            }

            if (occupant is not null)
            {
                return GateReason.Occupied;
            }

            return (area is { Airlines.Count: > 0 }) ? GateReason.Preferred : GateReason.Fits;
        }

        /// <summary>The stand's span cap (<see cref="StandFit.MaxWingspanFt"/>), asked once per area with a <c>maxAdg</c>.</summary>
        private double CapFt(GroundNode node, ParkingAreaAlias? area)
        {
            if (area?.MaxAdg is not { } maxAdg)
            {
                return StandFit.MaxWingspanFt(_layout, node, _ceiling, null);
            }

            if (!_areaCapFt.TryGetValue(area, out double capFt))
            {
                _areaCapFt[area] = capFt = StandFit.MaxWingspanFt(_layout, node, _ceiling, maxAdg);
            }

            return capFt;
        }
    }

    /// <summary>
    /// The span a type needs, feet: its wingspan (<see cref="AircraftFootprint.ResolveWingspanFt"/>), raised to the wake
    /// floor group's ceiling when the type has no FAA design group or span (<see cref="AirplaneDesignGroups.GroupForType"/>
    /// falls back) and its wake class is Super (ADG VI) or Heavy (ADG IV); and whether the span is the FAA record's own.
    /// </summary>
    private static (double SpanFt, bool Known) RequiredSpan(string aircraftType)
    {
        double spanFt = AircraftFootprint.ResolveWingspanFt(aircraftType);
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraftType);
        bool known = record?.WingspanFt is > 0.0;
        if ((record is not null) && (AirplaneDesignGroups.OfRecord(record, out _) is not null))
        {
            return (spanFt, known);
        }

        AirplaneDesignGroup? floor = WakeTurbulenceData.WakeClassForType(aircraftType, AircraftCategorization.Categorize(aircraftType)) switch
        {
            WakeTurbulenceData.WakeClass.Super => AirplaneDesignGroup.VI,
            WakeTurbulenceData.WakeClass.Heavy => AirplaneDesignGroup.IV,
            _ => null,
        };
        if ((floor is not { } group) || (spanFt >= AirplaneDesignGroups.MaxWingspanFt(group)))
        {
            return (spanFt, known);
        }

        double floorFt = AirplaneDesignGroups.MaxWingspanFt(group);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Type}: no FAA design group or span; wake floor ADG {Group} applies, {Span:F0} ft", aircraftType, group, floorFt);
        }

        return (floorFt, false);
    }
}
