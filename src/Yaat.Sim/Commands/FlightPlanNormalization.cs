using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// A filed aircraft-data field (ERAM field 03, FAA 7233-4 block 3) split into its parts: element a's number of aircraft
/// (<c>2</c> in <c>2H/F16</c>) and indicator (<c>H</c>, <c>J</c> or <c>S</c>), the type designator, and the equipment
/// suffix. Each part is null when the entry does not give it.
/// </summary>
public sealed record FiledAircraftType(int? Count, char? Indicator, string Type, string? Suffix);

/// <summary>
/// Input normalization shared by every flight-plan create / amend path: the typed <c>FP</c> / <c>DA</c> verbs (the
/// action router's flight-plan arm), the STARS keyboard entries CRC sends as those verbs, and the structured CRC
/// flight-plan editor. Keeps equipment-suffix splitting and FAA→ICAO airport canonicalization consistent across them.
/// </summary>
public static class FlightPlanNormalization
{
    /// <summary>
    /// Splits a filed aircraft-data string like <c>"2H/F16/L"</c> into element a (the count <c>2</c> and indicator
    /// <c>H</c>), the type and the equipment suffix. A leading segment is element a only when
    /// <see cref="AircraftState.IsTypePrefix"/> accepts it (a one- or two-digit count, an <c>H</c>/<c>J</c>/<c>S</c>
    /// indicator, or both), so the type is never read as <c>"H"</c> or <c>"2H"</c>; any other leading segment is the
    /// type (<c>"123/F16"</c> is type <c>123</c>, suffix <c>F16</c>). A bare type (<c>"SR22"</c>, <c>"H/A306"</c>) and
    /// an empty tail after the last slash (<c>"C172/"</c>) both return a null suffix: the equipment suffix is a separate
    /// field that a type alone never changes, and only <c>SimulationEngine.AmendFlightPlan</c> defaults it to <c>"A"</c>
    /// when the amendment files a new plan. Null input returns null ("no aircraft type supplied"); empty input returns
    /// an empty type.
    /// </summary>
    [return: NotNullIfNotNull(nameof(raw))]
    public static FiledAircraftType? SplitTypeAndSuffix(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        int? count = null;
        char? indicator = null;
        string typeAndSuffix = raw;
        int prefixSlash = raw.IndexOf('/');
        if ((prefixSlash >= 0) && AircraftState.IsTypePrefix(raw[..prefixSlash]))
        {
            (count, indicator) = ParseElementA(raw[..prefixSlash]);
            typeAndSuffix = raw[(prefixSlash + 1)..];
        }

        int slash = typeAndSuffix.IndexOf('/');
        if (slash < 0)
        {
            return new FiledAircraftType(count, indicator, typeAndSuffix, null);
        }

        string suffix = typeAndSuffix[(slash + 1)..];
        return new FiledAircraftType(count, indicator, typeAndSuffix[..slash], suffix.Length == 0 ? null : suffix);
    }

    /// <summary>
    /// Element a, already accepted by <see cref="AircraftState.IsTypePrefix"/>: leading digits, then an optional indicator
    /// letter, returned upper-cased.
    /// </summary>
    private static (int? Count, char? Indicator) ParseElementA(string prefix)
    {
        char last = prefix[^1];
        char? indicator = char.IsAsciiDigit(last) ? null : char.ToUpperInvariant(last);
        string digits = indicator is null ? prefix : prefix[..^1];
        int? count = digits.Length == 0 ? null : int.Parse(digits, CultureInfo.InvariantCulture);
        return (count, indicator);
    }

    /// <summary>
    /// Resolves aircraft type and FAA equipment suffix for the structured CRC flight-plan path, where CRC sends two
    /// equipment-related fields: a combined <c>Equipment</c> string and the canonical <c>FaaEquipmentSuffix</c>. The
    /// combined string may be in ICAO display form (<c>"C182/L-DOV/C"</c> = type/wakeTurb-icaoEquip/surveillance)
    /// when CRC's editor re-built it, in plain FAA form (<c>"C172/G"</c>) for legacy callers, bare type-only
    /// (<c>"C182"</c>), or the wake-prefixed form a scenario files (<c>"H/A306/L"</c>) when CRC echoed the equipment it
    /// was sent. Aircraft type comes from <see cref="SplitTypeAndSuffix"/>, which drops a leading wake or formation
    /// prefix; the suffix prefers the canonical field when present, falling back to the split tail, and is null (not
    /// edited) when neither carries one.
    /// </summary>
    public static (string? Type, string? Suffix) ResolveTypeAndSuffix(string? equipment, string? faaEquipmentSuffix)
    {
        FiledAircraftType? fromEquipment = SplitTypeAndSuffix(equipment);
        string? preferredSuffix = !string.IsNullOrEmpty(faaEquipmentSuffix) ? faaEquipmentSuffix : fromEquipment?.Suffix;
        return (fromEquipment?.Type, preferredSuffix);
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

        return NavigationDatabase.Instance.TryResolveAirport(input, out string? resolved) ? resolved : input.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Amend-path variant of <see cref="CanonicalizeAirport"/> that preserves the "clear this field" sentinel. In the
    /// flight-plan amendment pipeline a <c>null</c> field means "leave unchanged" while an empty string means "clear
    /// it" — so a genuinely absent field (<c>null</c>) stays null, an empty/whitespace field returns <c>""</c> so the
    /// clear survives all the way to <c>SimulationEngine.AmendFlightPlan</c>, and a real identifier is canonicalized
    /// FAA→ICAO as usual. The plain <see cref="CanonicalizeAirport"/> collapses empty to null, which is correct for the
    /// create/route-split path but silently drops a clear on amend.
    /// </summary>
    public static string? CanonicalizeAirportPreservingClear(string? input) => input is null ? null : (CanonicalizeAirport(input) ?? "");

    /// <summary>
    /// Splits a flight-plan route string into departure / destination / middle waypoints, matching the typed
    /// create-FP convention: a single token is destination-only; two-or-more tokens split as first=departure,
    /// last=destination, and the tokens between are the en-route waypoints. Airport identifiers are canonicalized
    /// (FAA→ICAO); the middle is returned verbatim. Pieces that are not present come back null.
    /// </summary>
    public static (string? Departure, string? Destination, string? Middle) SplitRoute(string? route)
    {
        string[] routeParts = (route ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
        (string? departure, string? destination, string? middleRoute) = SplitRoute(command.Route);
        FiledAircraftType aircraftType = SplitTypeAndSuffix(command.AircraftType);
        PlannedAltitude filedAltitude = command.Altitude;
        return new FlightPlanAmendment(
            ClearBeaconCode: false,
            AircraftType: aircraftType.Type,
            EquipmentSuffix: aircraftType.Suffix,
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
        FiledAircraftType? aircraftType = SplitTypeAndSuffix(command.AircraftType);
        PlannedAltitude filedAltitude = FlightPlanAltitude.FromRulesAndFeet(command.FlightRules, command.CruiseAltitude);
        return new FlightPlanAmendment(
            ClearBeaconCode: false,
            AircraftType: aircraftType?.Type,
            EquipmentSuffix: aircraftType?.Suffix,
            Altitude: filedAltitude,
            FlightRules: filedAltitude.IsVfr ? "VFR" : "IFR",
            Scratchpad1: command.Scratchpad1,
            Scratchpad2: command.Scratchpad2,
            BeaconCode: command.BeaconCode
        );
    }
}
