using System.Text.Json.Serialization;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data;

/// <summary>
/// One taxiway an ARTCC wants the AUTO taxi pathfinder to avoid at a given airport, as a section of
/// the unified per-airport sidecar (<see cref="AirportSidecarFile"/>). The auto-router skips the named
/// taxiway unless the destination is only reachable through it; explicit <c>TAXI</c> commands that name
/// the taxiway are unaffected.
/// </summary>
public sealed class AvoidTaxiwayEntry
{
    /// <summary>Taxiway name to avoid, e.g. <c>"S"</c>. Matched case-insensitively against edge taxiway names.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Optional human-readable rationale (SOP reference, condition). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One taxiway whose movement-area status an ARTCC pins at a given airport, as an entry of the unified per-airport
/// sidecar's <c>movementAreaTaxiways</c> or <c>nonMovementTaxilanes</c> list (<see cref="AirportSidecarFile"/>). The
/// layout carries no movement-area boundary, so the status is otherwise inferred
/// (<see cref="Yaat.Sim.Data.Airport.MovementAreaClassification"/>); an entry overrides that inference.
/// </summary>
public sealed class PavementClassEntry
{
    /// <summary>Taxiway name, e.g. <c>"B1"</c>. Matched case-insensitively against edge taxiway names.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Optional human-readable rationale (airport diagram, SOP reference). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One implicitly-allowed named connector taxiway. A connector taxiway (e.g. <c>"LF"</c>) is normally
/// a letter-only taxiway that the controller must name explicitly. This entry authorizes it implicitly,
/// but only contextually — when the controller's cleared sequence places the two <see cref="Between"/>
/// taxiways adjacent (unordered). So <c>{ connector: "LF", between: ["L","F"] }</c> authorizes <c>LF</c>
/// for <c>TAXI L F</c> (and <c>TAXI F L</c>) but not for <c>TAXI L A F</c>.
/// </summary>
public sealed class ImplicitConnectorEntry
{
    /// <summary>The connector taxiway name to authorize, e.g. <c>"LF"</c>.</summary>
    [JsonPropertyName("connector")]
    public string Connector { get; set; } = "";

    /// <summary>The two taxiways this connector bridges (exactly 2, unordered).</summary>
    [JsonPropertyName("between")]
    public List<string> Between { get; set; } = [];

    /// <summary>Optional human-readable rationale. Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Two taxiways that face each other across a runway, declared a crossing of it from the airport diagram, as a section of
/// the unified per-airport sidecar (<see cref="AirportSidecarFile"/>). A clearance naming them adjacent (either order) crosses
/// the runway from one onto the other even where one meets the runway obliquely, as a high-speed exit does (KCOS A2 and C2
/// across 17R/35L). The declaration waives only the oblique-join rule; the two must still join the runway from opposite sides
/// within the offset-crossing bound.
/// </summary>
public sealed class CrossingPairEntry
{
    /// <summary>The two taxiways (exactly 2, unordered), e.g. <c>["A2", "C2"]</c>.</summary>
    [JsonPropertyName("taxiways")]
    public List<string> Taxiways { get; set; } = [];

    /// <summary>The runway they cross, both ends as the layout names it, e.g. <c>"17R/35L"</c>.</summary>
    [JsonPropertyName("runway")]
    public string Runway { get; set; } = "";

    /// <summary>Optional human-readable rationale (airport diagram reference). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One validated <see cref="CrossingPairEntry"/>, produced by <see cref="AirportSidecarLoader"/>: taxiway names trimmed and
/// upper-cased, the runway trimmed and upper-cased.
/// </summary>
public sealed record CrossingPair(string FirstTaxiway, string SecondTaxiway, string Runway, string? Notes)
{
    /// <summary>
    /// True when this pair is <paramref name="fromTaxiway"/> and <paramref name="toTaxiway"/> (either order) across the runway
    /// whose centreline is <paramref name="runwayCenterline"/> (<c>RWY17R/35L</c>): every end this pair's runway names is one
    /// of the centreline's ends.
    /// </summary>
    public bool Matches(string fromTaxiway, string toTaxiway, string runwayCenterline)
    {
        bool samePair =
            (
                FirstTaxiway.Equals(fromTaxiway, StringComparison.OrdinalIgnoreCase)
                && SecondTaxiway.Equals(toTaxiway, StringComparison.OrdinalIgnoreCase)
            )
            || (
                FirstTaxiway.Equals(toTaxiway, StringComparison.OrdinalIgnoreCase)
                && SecondTaxiway.Equals(fromTaxiway, StringComparison.OrdinalIgnoreCase)
            );
        return samePair && Runway.Split('/').All(end => IGroundEdge.RunwayNameContainsDesignator(runwayCenterline, end));
    }
}

/// <summary>
/// One Arrival/Departure Window (ADW) a facility directive publishes for a converging arrival/departure
/// runway pair, as a section of the unified per-airport sidecar (<see cref="AirportSidecarFile"/>). An
/// ADW is one of the facility aids 7110.65 §3-9-9.b permits in lieu of applying the §3-9-8
/// intersecting-runway provisions; it protects the arrival's missed approach from the converging
/// departure and does <em>not</em> change IFR separation standards.
/// <see cref="OuterNm"/> and <see cref="InnerNm"/> are distances along the arrival runway's final
/// approach course measured from its landing threshold — positive outbound onto final, negative past
/// the threshold and down the runway. Drawn as reference marks in Ground View; nothing in the
/// simulation acts on them.
/// </summary>
public sealed class AdwEntry
{
    /// <summary>Landing runway end the window is measured along, e.g. <c>"26R"</c>.</summary>
    [JsonPropertyName("arrivalRunway")]
    public string ArrivalRunway { get; set; } = "";

    /// <summary>The converging runway end whose takeoff roll this window gates, e.g. <c>"30"</c>.</summary>
    [JsonPropertyName("departureRunway")]
    public string DepartureRunway { get; set; } = "";

    /// <summary>Outer range in nm from the landing threshold, outbound along the final approach course.</summary>
    [JsonPropertyName("outerNm")]
    public double OuterNm { get; set; }

    /// <summary>Inner range in nm from the landing threshold; negative is past the threshold, on the runway.</summary>
    [JsonPropertyName("innerNm")]
    public double InnerNm { get; set; }

    /// <summary>Facility directive this window is published in. Informational, but do not author an entry without one.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's validated ADW definition, produced by <see cref="AirportSidecarLoader"/> from an
/// <see cref="AdwEntry"/>. Runway designators are zero-pad-normalized so they match
/// <see cref="Yaat.Sim.Data.Airport.RunwayIdentifier"/> lookups.
/// </summary>
public sealed record AdwWindow(string ArrivalRunway, string DepartureRunway, double OuterNm, double InnerNm, string? Notes);

/// <summary>
/// One default exit (turn-off) direction override for a single runway <em>end</em>, as a section of
/// the unified per-airport sidecar (<see cref="AirportSidecarFile"/>). The GeoJSON <c>turnoff</c>
/// property carries one value per physical runway and the parser flips it for the reciprocal end,
/// assuming both landing directions vacate to the same physical side; this entry overrides that per
/// end for layouts where the assumption doesn't hold. It wins over both the GeoJSON-authored side and
/// the layout heuristics; an explicit <c>EL</c>/<c>ER</c> command on an aircraft still wins over it.
/// An entry with an <see cref="ArrivalClass"/> applies only to arrivals of that class and wins over a
/// class-less entry for the same end; an arrival's parking remark wins over both (<see cref="ArrivalExitSide"/>).
/// </summary>
public sealed class ExitDirectionEntry
{
    /// <summary>Landing runway end the override applies to, e.g. <c>"26R"</c>.</summary>
    [JsonPropertyName("runway")]
    public string Runway { get; set; } = "";

    /// <summary>Side to vacate toward, relative to the landing aircraft's nose: <c>"left"</c> or <c>"right"</c>.</summary>
    [JsonPropertyName("side")]
    public string Side { get; set; } = "";

    /// <summary>
    /// The arrivals the entry applies to: <c>"ga"</c> (a registration callsign, or an operator in the sidecar's
    /// <c>gaOperators</c>) or <c>"cargo"</c> (an operator in the sidecar's <c>cargoOperators</c>). Absent, it applies to
    /// every arrival.
    /// </summary>
    [JsonPropertyName("arrivalClass")]
    public string? ArrivalClass { get; set; }

    /// <summary>Facility rationale (SOP reference, request provenance). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's validated exit-direction override, produced by <see cref="AirportSidecarLoader"/>
/// from an <see cref="ExitDirectionEntry"/>. The runway designator is zero-pad-normalized so it
/// matches <see cref="Yaat.Sim.Data.Airport.RunwayIdentifier"/> lookups. A null <c>ArrivalClass</c>
/// applies to every arrival.
/// </summary>
public sealed record ExitDirectionOverride(string Runway, ArrivalClass? ArrivalClass, ExitSide Side, string? Notes);

/// <summary>
/// One parking area a flight-plan remark may name instead of a stand (<c>/V/ PARKING SIGNATURE, 28R</c>), as an entry of the
/// unified per-airport sidecar's <c>parkingAreas</c> list (<see cref="AirportSidecarFile"/>): the area's name and the stands
/// it covers. <c>{n}</c> in the area stands for a number the remark gives and is carried into the stand patterns
/// (<c>"Plot {n}"</c> covering <c>"{n}-*"</c>).
/// </summary>
public sealed class ParkingAreaEntry
{
    /// <summary>The area as remarks name it, e.g. <c>"Signature"</c> or <c>"Plot {n}"</c>. Case and spaces are ignored.</summary>
    [JsonPropertyName("area")]
    public string? Area { get; set; }

    /// <summary>The layout stand names the area covers: exact names, or a prefix ending in <c>*</c> (<c>"SIG*"</c>).</summary>
    [JsonPropertyName("stands")]
    public List<string>? Stands { get; set; }

    /// <summary>Facility rationale. Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's validated parking area, produced by <see cref="AirportSidecarLoader"/> from a <see cref="ParkingAreaEntry"/>:
/// the area upper-cased with its whitespace removed, the stand patterns upper-cased. Read by <see cref="ArrivalExitSide"/>.
/// </summary>
public sealed record ParkingAreaAlias(string Area, IReadOnlyList<string> Stands, string? Notes)
{
    /// <summary>The placeholder an area carries for the number a remark gives (<c>"PLOT{N}"</c>), as normalized from <c>{n}</c>.</summary>
    public const string NumberToken = "{N}";
}

/// <summary>
/// One stand-departure area rule, as an entry of the unified per-airport sidecar's <c>standDepartureAreas</c> list
/// (<see cref="AirportSidecarFile"/>): every stand on one side of a runway end's extended centerline departs the named
/// way. Left of one end is right of the reciprocal, so a rule may name either end of the runway.
/// </summary>
public sealed class StandDepartureAreaEntry
{
    /// <summary>Runway end whose nose the side is taken from, e.g. <c>"28R"</c>.</summary>
    [JsonPropertyName("runway")]
    public string? Runway { get; set; }

    /// <summary>Side of the runway end's extended centerline, relative to its nose on landing: <c>"left"</c> or <c>"right"</c>.</summary>
    [JsonPropertyName("side")]
    public string? Side { get; set; }

    /// <summary>The departure of every stand on that side: <c>"PushBack"</c>, <c>"TaxiOut"</c> or <c>"Either"</c>.</summary>
    [JsonPropertyName("departure")]
    public string? Departure { get; set; }

    /// <summary>Facility rationale (the area's name, SOP reference). Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's validated stand-departure area rule, produced by <see cref="AirportSidecarLoader"/> from a
/// <see cref="StandDepartureAreaEntry"/>. The runway designator is zero-pad-normalized so it matches
/// <see cref="Yaat.Sim.Data.Airport.RunwayIdentifier"/> lookups. Read by <see cref="StandDepartures.StandDepartureOf"/>.
/// </summary>
public sealed record StandDepartureArea(string Runway, ExitSide Side, StandDeparture Departure, string? Notes);

/// <summary>
/// On-disk shape of one entry of the unified per-airport sidecar's <c>standLeadIns</c> section: the parking stand whose
/// connector to the taxiway network the layout build draws to <see cref="Taxiway"/> instead of to the nearest taxiway edge,
/// for a stand the vNAS map draws no lead-in line for.
/// </summary>
public sealed class StandLeadInEntry
{
    /// <summary>The parking stand's name, e.g. <c>"D12"</c>, matched case-insensitively.</summary>
    [JsonPropertyName("stand")]
    public string? Stand { get; set; }

    /// <summary>The taxiway the stand's lead-in joins, e.g. <c>"A"</c>.</summary>
    [JsonPropertyName("taxiway")]
    public string? Taxiway { get; set; }

    /// <summary>
    /// <c>[lon, lat]</c> (GeoJSON order) near the taxiway vertex the lead-in joins: the vertex of <see cref="Taxiway"/> nearest
    /// this point is chosen.
    /// </summary>
    [JsonPropertyName("point")]
    public double[]? Point { get; set; }

    /// <summary>Optional human-readable rationale. Informational only.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// One airport's validated stand lead-in, produced by <see cref="AirportSidecarLoader"/> from a
/// <see cref="StandLeadInEntry"/>: <see cref="Stand"/> trimmed, <see cref="Taxiway"/> trimmed and upper-cased. Read by
/// <see cref="GeoJsonParser"/> when it connects the stand to the taxiway network.
/// </summary>
public sealed record StandLeadIn(string Stand, string Taxiway, LatLon Point, string? Notes);

/// <summary>
/// On-disk shape of a unified per-airport ground sidecar: one airport per JSON file under
/// <c>Data/ARTCCs/{ARTCC}/Airports/{airport}.json</c>. Consolidates the per-airport ground-routing
/// overrides (avoided taxiways, preset taxi routes, implicit connectors) that were previously split
/// across sibling category folders. Unknown sections are ignored, so a file may carry any subset.
/// </summary>
internal sealed class AirportSidecarFile
{
    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("avoidTaxiways")]
    public List<AvoidTaxiwayEntry> AvoidTaxiways { get; set; } = [];

    [JsonPropertyName("taxiRoutes")]
    public List<TaxiRouteDefinition> TaxiRoutes { get; set; } = [];

    [JsonPropertyName("implicitConnectors")]
    public List<ImplicitConnectorEntry> ImplicitConnectors { get; set; } = [];

    [JsonPropertyName("crossingPairs")]
    public List<CrossingPairEntry> CrossingPairs { get; set; } = [];

    [JsonPropertyName("oneWayEdges")]
    public List<OneWayConstraintEntry> OneWayEdges { get; set; } = [];

    [JsonPropertyName("blockedTurns")]
    public List<BlockedTurnEntry> BlockedTurns { get; set; } = [];

    [JsonPropertyName("adw")]
    public List<AdwEntry> Adw { get; set; } = [];

    [JsonPropertyName("exitDirections")]
    public List<ExitDirectionEntry> ExitDirections { get; set; } = [];

    /// <summary>Three-letter ICAO operators whose arrivals are cargo for a <c>cargo</c> <c>exitDirections</c> entry.</summary>
    [JsonPropertyName("cargoOperators")]
    public List<string> CargoOperators { get; set; } = [];

    /// <summary>
    /// Three-letter ICAO operators whose arrivals are general aviation: a <c>ga</c> <c>exitDirections</c> entry, and the
    /// parking pool, where a listed operator parks among the general-aviation stands rather than an airline's.
    /// </summary>
    [JsonPropertyName("gaOperators")]
    public List<string> GaOperators { get; set; } = [];

    /// <summary>
    /// Parking areas a flight-plan remark may name instead of a stand, each with the stands it covers; an area no entry
    /// matches gives no side.
    /// </summary>
    [JsonPropertyName("parkingAreas")]
    public List<ParkingAreaEntry> ParkingAreas { get; set; } = [];

    [JsonPropertyName("exitCapacity")]
    public List<ExitCapacityEntry> ExitCapacity { get; set; } = [];

    [JsonPropertyName("movementAreaTaxiways")]
    public List<PavementClassEntry> MovementAreaTaxiways { get; set; } = [];

    [JsonPropertyName("nonMovementTaxilanes")]
    public List<PavementClassEntry> NonMovementTaxilanes { get; set; } = [];

    /// <summary>
    /// Stand name to <c>"PushBack"</c>, <c>"TaxiOut"</c> or <c>"Either"</c>, overriding the stand's area rule and its
    /// geometric departure.
    /// </summary>
    [JsonPropertyName("standDeparture")]
    public Dictionary<string, string?> StandDeparture { get; set; } = [];

    /// <summary>Area rules giving every stand on one side of a runway end's centerline a departure, first match wins.</summary>
    [JsonPropertyName("standDepartureAreas")]
    public List<StandDepartureAreaEntry> StandDepartureAreas { get; set; } = [];

    /// <summary>Stands whose connector the layout build draws to a named taxiway's vertex instead of the nearest edge.</summary>
    [JsonPropertyName("standLeadIns")]
    public List<StandLeadInEntry> StandLeadIns { get; set; } = [];

    /// <summary>
    /// Taxiways the ASDE-X safety logic alerts a landing on, as the ground layout spells them. Absent means any taxiway; an
    /// empty list means no taxiway-landing alert at all.
    /// </summary>
    [JsonPropertyName("asdexLandingTaxiways")]
    public List<string>? AsdexLandingTaxiways { get; set; }
}

/// <summary>
/// One airport's parsed, validated sidecar, produced by <see cref="AirportSidecarLoader"/> from a single
/// file. Sections default to empty so the loader and tests can populate only what a file declares.
/// </summary>
public sealed record AirportSidecar(string AirportId)
{
    public IReadOnlyList<AvoidTaxiwayEntry> AvoidTaxiways { get; init; } = [];
    public IReadOnlyList<TaxiRouteDefinition> TaxiRoutes { get; init; } = [];
    public IReadOnlyList<ImplicitConnectorEntry> ImplicitConnectors { get; init; } = [];

    /// <summary>Taxiway pairs declared a crossing of a runway, in file order.</summary>
    public IReadOnlyList<CrossingPair> CrossingPairs { get; init; } = [];

    public IReadOnlyList<OneWayConstraint> OneWayEdges { get; init; } = [];
    public IReadOnlyList<BlockedTurn> BlockedTurns { get; init; } = [];
    public IReadOnlyList<AdwWindow> Adw { get; init; } = [];
    public IReadOnlyList<ExitDirectionOverride> ExitDirections { get; init; } = [];

    /// <summary>Upper-cased three-letter ICAO operators whose arrivals are <see cref="ArrivalClass.Cargo"/>.</summary>
    public IReadOnlyList<string> CargoOperators { get; init; } = [];

    /// <summary>Upper-cased three-letter ICAO operators whose arrivals are <see cref="ArrivalClass.GeneralAviation"/>.</summary>
    public IReadOnlyList<string> GaOperators { get; init; } = [];

    /// <summary>The parking areas a remark may name instead of a stand, in file order.</summary>
    public IReadOnlyList<ParkingAreaAlias> ParkingAreas { get; init; } = [];

    public IReadOnlyList<ExitCapacityRule> ExitCapacity { get; init; } = [];

    /// <summary>Upper-cased taxiway names forced to movement area, overriding the inferred classification.</summary>
    public IReadOnlyList<string> MovementAreaTaxiways { get; init; } = [];

    /// <summary>Upper-cased taxiway names forced to non-movement ramp taxilanes, overriding the inferred classification.</summary>
    public IReadOnlyList<string> NonMovementTaxilanes { get; init; } = [];

    /// <summary>
    /// Stand name (case-insensitive) to its departure — <see cref="StandDeparture.PushBack"/>,
    /// <see cref="StandDeparture.TaxiOut"/> or <see cref="StandDeparture.Either"/> — overriding the area rules and the
    /// geometric answer the layout stores (<see cref="StandDepartures.StandDepartureOf"/>); applies to every stand of that name.
    /// </summary>
    public IReadOnlyDictionary<string, StandDeparture> StandDepartureOverrides { get; init; } =
        new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Area rules in file order, each giving every stand on one side of a runway end's extended centerline a departure;
    /// the first matching rule wins, below a per-name override (<see cref="StandDepartures.StandDepartureOf"/>).
    /// </summary>
    public IReadOnlyList<StandDepartureArea> StandDepartureAreas { get; init; } = [];

    /// <summary>
    /// Stands whose connector the layout build draws to a named taxiway's vertex, in file order, one entry per stand
    /// (<see cref="GeoJsonParser"/>). The only section the layout build reads.
    /// </summary>
    public IReadOnlyList<StandLeadIn> StandLeadIns { get; init; } = [];

    /// <summary>
    /// Upper-cased taxiway names the ASDE-X safety logic alerts a landing on, as the ground layout spells them. Null when the
    /// file carries no <c>asdexLandingTaxiways</c> list, so any taxiway alerts; an empty set when it carries an empty list, so
    /// no taxiway landing does. Read by <see cref="Yaat.Sim.Asdex.AsdexConfiguredTaxiways"/>.
    /// </summary>
    public IReadOnlySet<string>? AsdexLandingTaxiways { get; init; }

    /// <summary>
    /// The sidecar file this airport was parsed from, named by a load or merge warning about it; null for a sidecar built in code.
    /// </summary>
    public string? SourceFile { get; init; }
}
