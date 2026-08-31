using System.Globalization;

namespace Yaat.Sim.LiveTraffic;

/// <summary>Flight-rules side of a <see cref="LiveTrafficFilter"/>.</summary>
public enum LiveTrafficRulesFilter
{
    Both,
    VfrOnly,
    IfrOnly,
}

/// <summary>Which flight-plan airport a <see cref="LiveTrafficFilter"/> airport list must match.</summary>
public enum LiveTrafficAirportMatch
{
    Either,
    Departure,
    Destination,
}

/// <summary>
/// Which real aircraft a live session shadows: flight rules (VFR / IFR / both), a flight-plan airport list
/// (departure / destination / either, with a toggle for aircraft that have no plan), and a radius around an
/// airport, fix, or FRD that replaces the room's lateral scope. Carried on the scenario as the canonical
/// string <see cref="Serialize"/> produces (empty = no filtering), recorded as a setting change under
/// <c>LiveTrafficFilter</c>, and parsed back by the server's shadow sync and the client's filter UI.
/// </summary>
public sealed record LiveTrafficFilter
{
    public static readonly LiveTrafficFilter None = new();

    public const double MaxRadiusNm = 400;

    public LiveTrafficRulesFilter Rules { get; init; }

    /// <summary>Airport codes the flight plan must name (FAA or ICAO form); empty = no airport filtering.</summary>
    public IReadOnlyList<string> AirportCodes { get; init; } = [];

    public LiveTrafficAirportMatch AirportMatch { get; init; }

    /// <summary>Airport filter only: also include aircraft without a flight plan (they can never match a code).</summary>
    public bool IncludeUnplanned { get; init; }

    /// <summary>Airport, fix, or FRD (e.g. <c>OAK090010</c>) at the centre of the radius; null = no radius.</summary>
    public string? RadiusCenter { get; init; }

    /// <summary>Radius around <see cref="RadiusCenter"/> in nautical miles.</summary>
    public double? RadiusNm { get; init; }

    public bool HasAirportFilter => AirportCodes.Count > 0;

    public bool HasRadius => !string.IsNullOrEmpty(RadiusCenter) && RadiusNm is > 0;

    public bool IsNone => (Rules == LiveTrafficRulesFilter.Both) && !HasAirportFilter && !HasRadius;

    /// <summary>Canonical, order-stable form: <c>RULES=VFR;APT=OAK,SFO;MATCH=DEP;NOPLAN=1;CENTER=SUNOL;RADIUS=15</c>. Empty for <see cref="None"/>.</summary>
    public string Serialize()
    {
        var parts = new List<string>();
        if (Rules != LiveTrafficRulesFilter.Both)
        {
            parts.Add($"RULES={(Rules == LiveTrafficRulesFilter.VfrOnly ? "VFR" : "IFR")}");
        }

        if (HasAirportFilter)
        {
            parts.Add($"APT={string.Join(',', AirportCodes)}");
            if (AirportMatch != LiveTrafficAirportMatch.Either)
            {
                parts.Add($"MATCH={(AirportMatch == LiveTrafficAirportMatch.Departure ? "DEP" : "DEST")}");
            }

            if (IncludeUnplanned)
            {
                parts.Add("NOPLAN=1");
            }
        }

        if (HasRadius)
        {
            parts.Add($"CENTER={RadiusCenter}");
            parts.Add($"RADIUS={RadiusNm!.Value.ToString("0.#", CultureInfo.InvariantCulture)}");
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Parses a serialized filter. Fails (with the reason) on unknown keys or values, malformed codes, or a radius
    /// outside (0, <see cref="MaxRadiusNm"/>]; a CENTER without a RADIUS (or vice versa) is also an error. The result
    /// is normalized: codes uppercased and de-duplicated, canonical key order on re-serialize.
    /// </summary>
    public static bool TryParse(string? text, out LiveTrafficFilter filter, out string? error)
    {
        filter = None;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var rules = LiveTrafficRulesFilter.Both;
        var codes = new List<string>();
        var match = LiveTrafficAirportMatch.Either;
        bool includeUnplanned = false;
        string? center = null;
        double? radiusNm = null;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                error = $"Malformed filter part '{part}'";
                return false;
            }

            var key = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim().ToUpperInvariant();
            switch (key)
            {
                case "RULES":
                    if (value is not ("VFR" or "IFR"))
                    {
                        error = $"RULES must be VFR or IFR, not '{value}'";
                        return false;
                    }

                    rules = value == "VFR" ? LiveTrafficRulesFilter.VfrOnly : LiveTrafficRulesFilter.IfrOnly;
                    break;
                case "APT":
                    foreach (var code in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!IsAirportCode(code))
                        {
                            error = $"'{code}' is not an airport code";
                            return false;
                        }

                        if (!codes.Contains(code))
                        {
                            codes.Add(code);
                        }
                    }

                    break;
                case "MATCH":
                    if (value is not ("DEP" or "DEST" or "EITHER"))
                    {
                        error = $"MATCH must be DEP, DEST or EITHER, not '{value}'";
                        return false;
                    }

                    match = value switch
                    {
                        "DEP" => LiveTrafficAirportMatch.Departure,
                        "DEST" => LiveTrafficAirportMatch.Destination,
                        _ => LiveTrafficAirportMatch.Either,
                    };
                    break;
                case "NOPLAN":
                    includeUnplanned = value == "1";
                    break;
                case "CENTER":
                    if (value.Length == 0 || !value.All(char.IsAsciiLetterOrDigit))
                    {
                        error = $"CENTER must be an airport, fix or FRD, not '{value}'";
                        return false;
                    }

                    center = value;
                    break;
                case "RADIUS":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nm) || nm <= 0 || nm > MaxRadiusNm)
                    {
                        error = $"RADIUS must be between 0 and {MaxRadiusNm} nm, not '{value}'";
                        return false;
                    }

                    radiusNm = nm;
                    break;
                default:
                    error = $"Unknown filter key '{key}'";
                    return false;
            }
        }

        if ((center is null) != (radiusNm is null))
        {
            error = "CENTER and RADIUS go together";
            return false;
        }

        filter = new LiveTrafficFilter
        {
            Rules = rules,
            AirportCodes = codes,
            AirportMatch = codes.Count > 0 ? match : LiveTrafficAirportMatch.Either,
            IncludeUnplanned = codes.Count > 0 && includeUnplanned,
            RadiusCenter = center,
            RadiusNm = radiusNm,
        };
        return true;
    }

    /// <summary>Human form for terminal lines and tooltips: "VFR only; plans dep/dest OAK or SFO (+ no-plan); within 15 nm of SUNOL".</summary>
    public string Describe()
    {
        if (IsNone)
        {
            return "none";
        }

        var parts = new List<string>();
        if (Rules != LiveTrafficRulesFilter.Both)
        {
            parts.Add(Rules == LiveTrafficRulesFilter.VfrOnly ? "VFR only" : "IFR only");
        }

        if (HasAirportFilter)
        {
            var side = AirportMatch switch
            {
                LiveTrafficAirportMatch.Departure => "dep",
                LiveTrafficAirportMatch.Destination => "dest",
                _ => "dep/dest",
            };
            var noPlan = IncludeUnplanned ? " (+ no-plan)" : "";
            parts.Add($"plans {side} {string.Join(" or ", AirportCodes)}{noPlan}");
        }

        if (HasRadius)
        {
            parts.Add($"within {RadiusNm!.Value.ToString("0.#", CultureInfo.InvariantCulture)} nm of {RadiusCenter}");
        }

        return string.Join("; ", parts);
    }

    private static bool IsAirportCode(string code) => code.Length is >= 3 and <= 4 && code.All(char.IsAsciiLetterOrDigit);
}
