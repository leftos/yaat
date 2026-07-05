namespace Yaat.Sim;

/// <summary>
/// Parses and formats the FAA altitude text used in flight plans, matching CRC's documented
/// FPE grammar: <c>NNN</c> (IFR cruise in hundreds of feet), <c>VFR</c>, <c>OTP</c>
/// (VFR-on-top), and <c>VFR/NNN</c> / <c>OTP/NNN</c> (rules with explicit altitude).
/// Altitudes are always hundreds of feet, per CRC's "Unlike legacy clients, altitudes are
/// expressed in hundreds of feet" note. The block (<c>NNNBNNN</c>) form is producible via the
/// ERAM <c>QZ</c> keyboard command (see <c>CrcClientState.Eram.DispatchQz</c>), not by typing in
/// the FPE, so it is <em>rendered</em> by <see cref="Format"/> but not accepted by <see cref="Parse"/>.
/// The above (<c>A</c>-prefix) form exists on the wire (<see cref="PlannedAltitude.IsAbove"/>) but
/// has no input path yet.
/// </summary>
public static class FlightPlanAltitude
{
    /// <summary>
    /// Parses the altitude text into the flight <c>Rules</c> ("IFR"/"VFR") and the filed
    /// <see cref="PlannedAltitude"/> notation (feet). OTP is VFR rules with VFR-on-top notation.
    /// Empty input is treated as VFR with no altitude (matches YAAT's FPE convention). Returns
    /// null when the text doesn't match any of the four documented single/VFR/OTP forms.
    /// </summary>
    public static (string Rules, PlannedAltitude Altitude)? Parse(string text)
    {
        text = text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text) || (text == "VFR"))
        {
            return ("VFR", PlannedAltitude.Vfr(null));
        }
        if (text == "OTP")
        {
            return ("VFR", PlannedAltitude.Otp(null));
        }
        if (text.StartsWith("VFR/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var vfrAlt))
        {
            return ("VFR", PlannedAltitude.Vfr(vfrAlt * 100));
        }
        if (text.StartsWith("OTP/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var otpAlt))
        {
            return ("VFR", PlannedAltitude.Otp(otpAlt * 100));
        }
        if (int.TryParse(text, out var alt))
        {
            return ("IFR", PlannedAltitude.Ifr(alt * 100));
        }
        return null;
    }

    /// <summary>
    /// Builds a <see cref="PlannedAltitude"/> from a flight-rules label ("IFR"/"VFR"/"OTP") and an
    /// optional single altitude in feet. Used by command handlers that carry rules + a plain altitude
    /// rather than parsed text. Zero/negative feet map to "no altitude".
    /// </summary>
    public static PlannedAltitude FromRulesAndFeet(string flightRules, int? feet)
    {
        var alt = feet is int f and > 0 ? f : (int?)null;
        if (flightRules.Equals("OTP", StringComparison.OrdinalIgnoreCase))
        {
            return PlannedAltitude.Otp(alt);
        }
        if (flightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return PlannedAltitude.Vfr(alt);
        }
        return alt is int single ? PlannedAltitude.Ifr(single) : PlannedAltitude.None;
    }

    /// <summary>
    /// Renders a <see cref="PlannedAltitude"/> back to FPE/data-block text. Inverse of
    /// <see cref="Parse"/> for the single/VFR/OTP forms, and additionally renders block
    /// (<c>NNNBNNN</c>) and above (<c>ANNN</c>).
    /// </summary>
    public static string Format(PlannedAltitude altitude)
    {
        if (altitude.IsBlock)
        {
            return $"{altitude.BlockFloorFeet!.Value / 100:D3}B{altitude.CruiseFeet!.Value / 100:D3}";
        }

        var altStr = altitude.CruiseFeet is { } feet and > 0 ? (feet / 100).ToString("D3") : "";

        if (altitude.IsAbove)
        {
            return $"A{altStr}";
        }
        if (altitude.IsVfrOnTop)
        {
            return string.IsNullOrEmpty(altStr) ? "OTP" : $"OTP/{altStr}";
        }
        if (altitude.IsVfr)
        {
            return string.IsNullOrEmpty(altStr) ? "VFR" : $"VFR/{altStr}";
        }
        return altStr;
    }
}
