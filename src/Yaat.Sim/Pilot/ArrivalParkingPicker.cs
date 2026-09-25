using System.Globalization;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Where an arriving pilot says it is going. Nothing in a scenario assigns an arrival a parking spot, so the pilot picks
/// one itself — deterministically per callsign (an FNV-1a draw, replay-safe, no RNG state) — from the layout's parking
/// nodes that fit the operator: the operator's own ramp when the layout names one (FDX*, JSX*, DHL*), a cargo apron or
/// a gate for an airline, a non-gate spot (FBO ramps, tie-down rows) for everyone else — general aviation avoids every
/// gate name, so a layout whose airline hardstands match the gate shape keeps them out of the GA pool. A gate name is an
/// optional leading upper-case letter, one to three digits, and an optional trailing upper-case letter ("29", "F8",
/// "A13R", "G101"); an airline's pool is the digit-led gates when they are at least half of the layout's gates (OAK's
/// 1-32, whose lettered names are hardstands) and every gate otherwise (FLL, SMF and MIA, where the digits are runway
/// and terminal markers beside the real gates). Spots already parked on, being taxied to, or named in another pilot's
/// open taxi-in request are skipped.
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

    public static string? Pick(AircraftState aircraft, AirportGroundLayout? layout, IReadOnlyList<AircraftState> others, int salt)
    {
        if (layout is null)
        {
            return null;
        }

        var names = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => n.Name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return Pick(aircraft.Callsign, names, TakenSpots(others, aircraft.Callsign), salt);
    }

    /// <summary>The pure choice: <paramref name="names"/> in ordinal order, minus <paramref name="taken"/>, narrowed to the operator's pool.</summary>
    public static string? Pick(string callsign, IReadOnlyList<string> names, IReadOnlySet<string> taken, int salt)
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

        IReadOnlyList<string> pool = Candidates(callsign, free);
        double u = FinalApproachSpeedVariety.UnitInterval(callsign, "taxi-in" + salt.ToString(CultureInfo.InvariantCulture));
        return pool[(int)(u * pool.Count)];
    }

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

    /// <summary>The names an operator would taxi to, in ordinal order; never empty when <paramref name="names"/> is not.</summary>
    public static IReadOnlyList<string> Candidates(string callsign, IReadOnlyList<string> names)
    {
        string? operatorCode = OperatorCode(callsign);
        if (operatorCode is null)
        {
            var general = names
                .Where(n => !IsGateName(n) && !IsOperatorRamp(n) && !n.StartsWith(CargoApronPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return general.Count > 0 ? general : names;
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
            // names are hardstands. A minority of digit-led names (FLL, SMF, MIA) means the digits are runway and
            // terminal markers beside the real gates, so every gate name is the pool.
            var digitLed = gates.Where(g => char.IsAsciiDigit(g[0])).ToList();
            return (digitLed.Count * 2 >= gates.Count) ? digitLed : gates;
        }

        return names;
    }

    /// <summary>The three-letter ICAO operator of an airline-style callsign (letters then digits) the fleet data knows; null for a registration.</summary>
    private static string? OperatorCode(string callsign)
    {
        if ((callsign.Length < 4) || !callsign[..3].All(char.IsAsciiLetterUpper) || !char.IsAsciiDigit(callsign[3]))
        {
            return null;
        }

        string code = callsign[..3];
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
