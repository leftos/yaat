using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Input normalization shared by every flight-plan create / amend path: the typed <c>FP</c> / <c>DA</c> verbs (the
/// action router's flight-plan arm), the STARS keyboard entries CRC sends as those verbs, and the structured CRC
/// flight-plan editor. Keeps equipment-suffix splitting and FAA→ICAO airport canonicalization consistent across them.
/// </summary>
public static class FlightPlanNormalization
{
    /// <summary>
    /// Splits an FAA equipment string like <c>"C172/G"</c> into its base type and suffix. When the controller types
    /// only the type (e.g. <c>"SR22"</c>) the suffix defaults to <c>"A"</c> per FAA convention (no transponder /
    /// Mode-A only). Returns <c>(null, null)</c> on null/empty input so callers can distinguish "no aircraft type
    /// supplied" from "aircraft type with no suffix typed".
    /// </summary>
    public static (string? Type, string? Suffix) SplitTypeAndSuffix(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (raw, null);
        }

        var slash = raw.IndexOf('/');
        if (slash < 0)
        {
            return (raw, "A");
        }

        return (raw[..slash], raw[(slash + 1)..]);
    }

    /// <summary>
    /// Resolves aircraft type and FAA equipment suffix for the structured CRC flight-plan path, where CRC sends two
    /// equipment-related fields: a combined <c>Equipment</c> string and the canonical <c>FaaEquipmentSuffix</c>. The
    /// combined string may be in ICAO display form (<c>"C182/L-DOV/C"</c> = type/wakeTurb-icaoEquip/surveillance)
    /// when CRC's editor re-built it, in plain FAA form (<c>"C172/G"</c>) for legacy callers, or bare type-only
    /// (<c>"C182"</c>) when CRC echoed its cached equipment. Aircraft type comes from the portion before the first
    /// <c>/</c>; the suffix prefers the canonical field when present, falling back to the slash-split tail.
    /// </summary>
    public static (string? Type, string? Suffix) ResolveTypeAndSuffix(string? equipment, string? faaEquipmentSuffix)
    {
        var (typeFromEquipment, suffixFromEquipment) = SplitTypeAndSuffix(equipment);
        var preferredSuffix = !string.IsNullOrEmpty(faaEquipmentSuffix) ? faaEquipmentSuffix : suffixFromEquipment;
        return (typeFromEquipment, preferredSuffix);
    }

    /// <summary>
    /// Canonicalizes a user-typed airport identifier (FAA-3 or ICAO-4) to its ICAO form via
    /// <see cref="NavigationDatabase.TryResolveAirport"/> when the identifier resolves (e.g. <c>"OAK"</c> →
    /// <c>"KOAK"</c>). Unknown identifiers — including legitimate non-US airports the US-centric nav database does not
    /// carry (e.g. <c>"WSSS"</c>) — pass through trimmed and uppercased so international flight plans round-trip
    /// instead of being rejected. Returns null for null/empty input ("no airport specified"). Airports are not
    /// validated for existence here; that falls out when procedures and ground layouts are loaded for them.
    /// </summary>
    public static string? CanonicalizeAirport(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return NavigationDatabase.Instance.TryResolveAirport(input, out var resolved) ? resolved : input.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Amend-path variant of <see cref="CanonicalizeAirport"/> that preserves the "clear this field" sentinel. In the
    /// flight-plan amendment pipeline a <c>null</c> field means "leave unchanged" while an empty string means "clear
    /// it" — so a genuinely absent field (<c>null</c>) stays null, an empty/whitespace field returns <c>""</c> so the
    /// clear survives all the way to <c>SimulationEngine.AmendFlightPlan</c>, and a real identifier is canonicalized
    /// FAA→ICAO as usual. The plain <see cref="CanonicalizeAirport"/> collapses empty to null, which is correct for the
    /// create/route-split path but silently drops a clear on amend.
    /// </summary>
    public static string? CanonicalizeAirportPreservingClear(string? input)
    {
        return input is null ? null : (CanonicalizeAirport(input) ?? "");
    }

    /// <summary>
    /// Splits a flight-plan route string into departure / destination / middle waypoints, matching the typed
    /// create-FP convention: a single token is destination-only; two-or-more tokens split as first=departure,
    /// last=destination, and the tokens between are the en-route waypoints. Airport identifiers are canonicalized
    /// (FAA→ICAO); the middle is returned verbatim. Pieces that are not present come back null.
    /// </summary>
    public static (string? Departure, string? Destination, string? Middle) SplitRoute(string? route)
    {
        var routeParts = (route ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? departureRaw = routeParts.Length >= 2 ? routeParts[0] : null;
        string? destinationRaw =
            routeParts.Length >= 2 ? routeParts[^1]
            : routeParts.Length == 1 ? routeParts[0]
            : null;
        string? middle = routeParts.Length > 2 ? string.Join(" ", routeParts[1..^1]) : null;
        return (CanonicalizeAirport(departureRaw), CanonicalizeAirport(destinationRaw), middle);
    }

    /// <summary>
    /// The amendment a typed <c>FP</c> / <c>VP</c> files: the type/suffix split, the route split into departure /
    /// destination / en-route, and the filed altitude with its rules. VFR-on-top notation is an IFR flight (AIM 4-4-8),
    /// so only plain VFR maps to VFR rules — OTP stays IFR.
    /// </summary>
    public static FlightPlanAmendment FromCreateCommand(CreateFlightPlanCommand command)
    {
        var (departure, destination, middleRoute) = SplitRoute(command.Route);
        var (acType, equipSuffix) = SplitTypeAndSuffix(command.AircraftType);
        var filedAltitude = FlightPlanAltitude.FromRulesAndFeet(command.FlightRules, command.CruiseAltitude);
        return new FlightPlanAmendment(
            AircraftType: acType,
            EquipmentSuffix: equipSuffix,
            Departure: departure,
            Destination: destination,
            Altitude: filedAltitude,
            FlightRules: filedAltitude.IsVfr ? "VFR" : "IFR",
            Route: middleRoute ?? ""
        );
    }

    /// <summary>The amendment a typed <c>DA</c> files: type/suffix, the filed altitude with its rules, scratchpads and beacon.</summary>
    public static FlightPlanAmendment FromCreateAbbreviatedCommand(CreateAbbreviatedFlightPlanCommand command)
    {
        var (acType, equipSuffix) = SplitTypeAndSuffix(command.AircraftType);
        var filedAltitude = FlightPlanAltitude.FromRulesAndFeet(command.FlightRules, command.CruiseAltitude);
        return new FlightPlanAmendment(
            AircraftType: acType,
            EquipmentSuffix: equipSuffix,
            Altitude: filedAltitude,
            FlightRules: filedAltitude.IsVfr ? "VFR" : "IFR",
            Scratchpad1: command.Scratchpad1,
            Scratchpad2: command.Scratchpad2,
            BeaconCode: command.BeaconCode
        );
    }
}
