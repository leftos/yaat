using System.Globalization;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Where an arriving pilot says it is going. Nothing in a scenario assigns an arrival a parking spot, so the pilot picks
/// one itself — deterministically per callsign (an FNV-1a draw, replay-safe, no RNG state) — from the layout's parking
/// nodes that fit the operator: the operator's own ramp when the layout names one (FDX*, JSX*, DHL*), a cargo apron or
/// a gate for an airline, a non-gate spot (FBO ramps, tie-down rows) for everyone else — an operator the airport's
/// sidecar lists in <c>gaOperators</c> among them, whatever the fleet data says — general aviation avoids every
/// gate name, so a layout whose airline hardstands match the gate shape keeps them out of the GA pool, and a GA arrival
/// whose pool is empty picks nothing rather than a gate. A gate name is an optional leading upper-case letter, one to
/// three digits, and an optional trailing upper-case letter ("29", "F8", "A13R", "G101"); an airline's pool is the
/// digit-led gates when they are at least half of the layout's gates (OAK's 1-32, whose lettered names are hardstands)
/// and every gate otherwise. Names the airport's sidecar lists in <c>nonGateParking</c> (the runway and terminal markers
/// SMF and FLL draw as parking, and placeholders such as SMF's GA, FAA and TEXT) are never in any pool. Spots already
/// parked on, being taxied to, or named in another pilot's open taxi-in request are skipped.
/// </summary>
public static class ArrivalParkingPicker
{
    /// <summary>Operators whose callsign prefix differs from the ramp name a layout uses for them.</summary>
    private static readonly Dictionary<string, string> RampAliases = new(StringComparer.Ordinal)
    {
        ["DHK"] = "DHL",
        ["BCS"] = "DHL",
        ["DAE"] = "DHL",
        ["DHX"] = "DHL",
    };

    private static readonly HashSet<string> CargoOperators = new(StringComparer.Ordinal)
    {
        "FDX",
        "UPS",
        "DHK",
        "BCS",
        "DAE",
        "DHX",
        "GTI",
        "ABX",
        "ATN",
        "CLX",
        "BOX",
        "GEC",
        "CKS",
        "PAC",
        "NCA",
        "KFS",
    };

    private const string CargoApronPrefix = "CARGO";

    /// <summary>
    /// The pick for <paramref name="aircraft"/> over the layout's parking names. <paramref name="sidecars"/> supplies the
    /// airport's <c>gaOperators</c>, so an operator the airport lists is pooled as general aviation, and its
    /// <c>nonGateParking</c>, whose names are left out before any pool is drawn.
    /// </summary>
    /// <returns>The picked name; null with no layout, when the layout has no parking names, and when the operator's pool is empty.</returns>
    public static string? Pick(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        IReadOnlyList<AircraftState> others,
        int salt,
        AirportSidecarCatalog sidecars
    )
    {
        if (layout is null)
        {
            return null;
        }

        IReadOnlySet<string> nonGate = sidecars.GetNonGateParking(layout.AirportId);
        var names = ParkingNames(layout).Where(n => !nonGate.Contains(n)).ToList();
        return Pick(aircraft.Callsign, names, TakenSpots(others, aircraft.Callsign), salt, sidecars.GetGaOperators(layout.AirportId));
    }

    /// <summary>Whether <paramref name="layout"/> names any parking node at all, whatever an arrival's pool would hold.</summary>
    public static bool HasParkingNames(AirportGroundLayout? layout) => (layout is not null) && ParkingNames(layout).Any();

    /// <summary>The layout's parking names, trimmed, each once, in ordinal order.</summary>
    private static IEnumerable<string> ParkingNames(AirportGroundLayout layout) =>
        layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => n.Name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// The pure choice: <paramref name="names"/> in ordinal order, minus <paramref name="taken"/>, narrowed to the operator's
    /// pool; null when there are no names or the pool is empty.
    /// </summary>
    public static string? Pick(string callsign, IReadOnlyList<string> names, IReadOnlySet<string> taken, int salt, IReadOnlySet<string> gaOperators)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var free = names.Where(n => !taken.Contains(n)).ToList();
        if (free.Count == 0)
        {
            free = [.. names];
        }

        IReadOnlyList<string> pool = Candidates(callsign, free, gaOperators);
        return (pool.Count == 0) ? null : Choose(callsign, pool, salt);
    }

    /// <summary>
    /// The deterministic draw from <paramref name="pool"/> as given: no operator narrowing and no fall-back, the same name
    /// for the same callsign, pool and salt.
    /// </summary>
    /// <param name="callsign">The aircraft the draw is for.</param>
    /// <param name="pool">The names to draw from, in a stable order; never empty.</param>
    /// <param name="salt">Varies the draw between asks by one aircraft.</param>
    /// <returns>One of <paramref name="pool"/>.</returns>
    public static string Choose(string callsign, IReadOnlyList<string> pool, int salt)
    {
        double u = FinalApproachSpeedVariety.UnitInterval(callsign, "taxi-in" + salt.ToString(CultureInfo.InvariantCulture));
        return pool[(int)(u * pool.Count)];
    }

    /// <summary>
    /// The free stand <paramref name="callsign"/> takes in parking <paramref name="area"/>: the area's stands that fit the type
    /// (<see cref="AreaPool"/>), less the stands other aircraft hold (<see cref="TakenSpots"/>), drawn by callsign with salt 0
    /// (<see cref="PickFree"/>), so the same traffic gives the same stand.
    /// </summary>
    /// <param name="callsign">The aircraft the pick is for.</param>
    /// <param name="aircraftType">Its type, whose wingspan decides which stands fit.</param>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="area">The parking area as the remark spells it.</param>
    /// <param name="traffic">Every aircraft in the world; the aircraft itself is skipped.</param>
    /// <returns>The picked stand; null when the area matches no stand or every stand of its pool is taken.</returns>
    internal static string? PickFreeStandInArea(
        string callsign,
        string aircraftType,
        AirportGroundLayout layout,
        string area,
        IReadOnlyList<AircraftState> traffic
    ) => (AreaPool(aircraftType, layout, area) is { } pool) ? PickFree(callsign, pool.Names, traffic) : null;

    /// <summary>
    /// The stands of parking <paramref name="area"/> (the airport's sidecar <c>parkingAreas</c>) that fit
    /// <paramref name="aircraftType"/> (<see cref="StandFit"/>), or every stand of the area when none fits.
    /// </summary>
    /// <param name="aircraftType">The type whose wingspan decides which stands fit.</param>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="area">The parking area as the remark spells it.</param>
    /// <returns>The pool; null when the area matches no stand of the layout.</returns>
    internal static ParkingAreaPool? AreaPool(string aircraftType, AirportGroundLayout layout, string area)
    {
        AirportSidecarCatalog sidecars = NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty;
        if ((ArrivalExitSide.AreaStands(area, layout, sidecars.GetParkingAreas(layout.AirportId)) is not { } match) || (match.Stands.Count == 0))
        {
            return null;
        }

        List<string> fitting = FittingStandNames(aircraftType, layout, match);
        List<string> all = StandNames(match.Stands);
        return new ParkingAreaPool((fitting.Count > 0) ? fitting : all, all.Count, fitting.Count > 0);
    }

    /// <summary>
    /// The stand <paramref name="callsign"/> draws from <paramref name="pool"/> less the stands other aircraft in
    /// <paramref name="traffic"/> hold (<see cref="TakenSpots"/>), with salt 0.
    /// </summary>
    /// <param name="callsign">The aircraft the pick is for.</param>
    /// <param name="pool">The stands to draw from, in ordinal order.</param>
    /// <param name="traffic">Every aircraft in the world; the aircraft itself is skipped.</param>
    /// <returns>The picked stand; null when every stand of the pool is taken.</returns>
    internal static string? PickFree(string callsign, IReadOnlyList<string> pool, IReadOnlyList<AircraftState> traffic)
    {
        HashSet<string> taken = TakenSpots(traffic, callsign);
        List<string> free = [.. pool.Where(n => !taken.Contains(n))];
        return (free.Count == 0) ? null : Choose(callsign, free, salt: 0);
    }

    /// <summary>
    /// The names of an area's stands that take the type's wingspan (<see cref="StandFit.MaxWingspanFt"/>), ordinal. A sidecar
    /// <c>maxAdg</c> gives every stand of the area the same cap, so it is asked once, which also keeps its above-the-ceiling
    /// warning to one per area.
    /// </summary>
    private static List<string> FittingStandNames(
        string aircraftType,
        AirportGroundLayout layout,
        (ParkingAreaAlias Alias, List<(GroundNode Node, string Name)> Stands) area
    )
    {
        double spanFt = AircraftFootprint.ResolveWingspanFt(aircraftType);
        AirplaneDesignGroup ceiling = AirplaneDesignGroups.AirportCeiling(layout);
        AirplaneDesignGroup? overrideAdg = area.Alias.MaxAdg;
        double? areaCapFt = overrideAdg is null ? null : StandFit.MaxWingspanFt(layout, area.Stands[0].Node, ceiling, overrideAdg);
        return StandNames(area.Stands.Where(s => spanFt <= (areaCapFt ?? StandFit.MaxWingspanFt(layout, s.Node, ceiling, null))));
    }

    /// <summary>The stands' trimmed names, each once, in ordinal order.</summary>
    private static List<string> StandNames(IEnumerable<(GroundNode Node, string Name)> stands) =>
        [.. stands.Select(s => s.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    /// <summary>Spots other aircraft occupy or are heading for: parked on, the destination of a taxi route, or asked for in an open taxi-in request.</summary>
    public static HashSet<string> TakenSpots(IReadOnlyList<AircraftState> others, string selfCallsign)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftState other in others)
        {
            if (string.Equals(other.Callsign, selfCallsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (
                (other.Phases?.CurrentPhase is AtParkingPhase or PushbackPhase or HoldingAfterPushbackPhase)
                && other.Ground.ParkingSpot is { Length: > 0 } spot
            )
            {
                taken.Add(spot);
            }

            if (other.Ground.AssignedTaxiRoute?.DestinationParking is { Length: > 0 } destination)
            {
                taken.Add(destination);
            }

            if (other.PendingPilotRequest is { IsOpen: true, ParkingName: { Length: > 0 } requested })
            {
                taken.Add(requested);
            }
        }

        return taken;
    }

    /// <summary>
    /// The names an operator would taxi to, in ordinal order. An airline's is never empty when <paramref name="names"/> is
    /// not; general aviation's is empty when no name is a non-gate spot, since a GA arrival is never sent to an airline gate.
    /// An operator in <paramref name="gaOperators"/> pools as general aviation, whatever the fleet data says of it.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string callsign, IReadOnlyList<string> names, IReadOnlySet<string> gaOperators)
    {
        string? operatorCode = OperatorCode(callsign, gaOperators);
        if (operatorCode is null)
        {
            return [.. names.Where(n => !IsGateName(n) && !IsOperatorRamp(n) && !n.StartsWith(CargoApronPrefix, StringComparison.OrdinalIgnoreCase))];
        }

        string ramp = RampAliases.GetValueOrDefault(operatorCode, operatorCode);
        var own = names.Where(n => IsRampOf(n, ramp)).ToList();
        if (own.Count > 0)
        {
            return own;
        }

        if (CargoOperators.Contains(operatorCode))
        {
            var apron = names.Where(n => n.StartsWith(CargoApronPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (apron.Count > 0)
            {
                return apron;
            }
        }

        var gates = names.Where(IsGateName).ToList();
        if (gates.Count > 0)
        {
            // The digit-led names are the pool only when they dominate the layout's gates: OAK's 1-32, where the lettered
            // names are hardstands. A minority of digit-led names sits beside the lettered gates, so every gate name is the
            // pool; markers that only look like gates are the sidecar's nonGateParking, left out before this.
            var digitLed = gates.Where(g => char.IsAsciiDigit(g[0])).ToList();
            return (digitLed.Count * 2 >= gates.Count) ? digitLed : gates;
        }

        return names;
    }

    /// <summary>
    /// An airline-style callsign: three upper-case letters, the ICAO operator, then a digit (<c>ATN3303</c>). A registration
    /// (<c>N377L</c>) is not one.
    /// </summary>
    public static bool IsAirlineStyleCallsign(string callsign) =>
        (callsign.Length >= 4) && callsign[..3].All(char.IsAsciiLetterUpper) && char.IsAsciiDigit(callsign[3]);

    /// <summary>
    /// The three-letter ICAO operator of an airline-style callsign (letters then digits) the fleet data knows and the
    /// airport's sidecar does not list in <c>gaOperators</c>; null for a registration and for a listed GA operator.
    /// </summary>
    private static string? OperatorCode(string callsign, IReadOnlySet<string> gaOperators)
    {
        if (!IsAirlineStyleCallsign(callsign))
        {
            return null;
        }

        string code = callsign[..3];
        if (gaOperators.Contains(code))
        {
            return null;
        }

        return AirlineFleets.TryGetAirline(code, out _) || RampAliases.ContainsKey(code) ? code : null;
    }

    /// <summary>A gate name: an optional upper-case letter, one to three digits, then an optional upper-case letter
    /// ("29", "8B", "F8", "A13R", "G101").</summary>
    public static bool IsGateName(string name)
    {
        int i = 0;
        if ((i < name.Length) && char.IsAsciiLetterUpper(name[i]))
        {
            i++;
        }

        int digits = 0;
        while ((i < name.Length) && char.IsAsciiDigit(name[i]))
        {
            digits++;
            i++;
        }

        if ((digits < 1) || (digits > 3))
        {
            return false;
        }

        if ((i < name.Length) && char.IsAsciiLetterUpper(name[i]))
        {
            i++;
        }

        return i == name.Length;
    }

    private static bool IsRampOf(string name, string ramp) =>
        (name.Length > ramp.Length) && name.StartsWith(ramp, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[ramp.Length]);

    /// <summary>An operator's named ramp (FDX1, DHL2, JSX3): a known operator code followed by a digit.</summary>
    private static bool IsOperatorRamp(string name) =>
        (name.Length >= 4)
        && name[..3].All(char.IsAsciiLetterUpper)
        && char.IsDigit(name[3])
        && (AirlineFleets.TryGetAirline(name[..3], out _) || RampAliases.ContainsValue(name[..3].ToUpperInvariant()));
}

/// <summary>A parking area's stand pool for one aircraft type (<see cref="ArrivalParkingPicker.AreaPool"/>).</summary>
/// <param name="Names">The stands that fit the type, or every stand of the area when none fits; ordinal order, never empty.</param>
/// <param name="AreaStandCount">How many stands the area has in all.</param>
/// <param name="Fits">Whether <paramref name="Names"/> are the stands that fit the type, not the whole-area fall-back.</param>
internal sealed record ParkingAreaPool(IReadOnlyList<string> Names, int AreaStandCount, bool Fits);
