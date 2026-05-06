namespace Yaat.Sim;

/// <summary>
/// Parses and formats the FAA altitude text used in flight plans, matching CRC's documented
/// FPE grammar: <c>NNN</c> (IFR cruise in hundreds of feet), <c>VFR</c>, <c>OTP</c>
/// (VFR-on-top), and <c>VFR/NNN</c> / <c>OTP/NNN</c> (rules with explicit altitude).
/// Altitudes are always hundreds of feet, per CRC's "Unlike legacy clients, altitudes are
/// expressed in hundreds of feet" note. Block (B-prefix) and above (A-prefix) forms exist
/// in the wire-level <c>ParsedAltitude</c> but are not user-typeable in the FPE; we don't
/// accept them here.
/// </summary>
public static class FlightPlanAltitude
{
    /// <summary>
    /// Parses the altitude text into <c>(FlightRules, CruiseAltitude)</c> in feet. Empty
    /// input is treated as VFR with no altitude (matches YAAT's FPE convention). Returns
    /// null when the text doesn't match any of the four documented forms.
    /// </summary>
    public static (string FlightRules, int CruiseAltitude)? Parse(string text)
    {
        text = text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return ("VFR", 0);
        }

        if (text == "VFR")
        {
            return ("VFR", 0);
        }
        if (text == "OTP")
        {
            return ("OTP", 0);
        }
        if (text.StartsWith("VFR/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var vfrAlt))
        {
            return ("VFR", vfrAlt * 100);
        }
        if (text.StartsWith("OTP/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var otpAlt))
        {
            return ("OTP", otpAlt * 100);
        }
        if (int.TryParse(text, out var alt))
        {
            return ("IFR", alt * 100);
        }
        return null;
    }

    /// <summary>
    /// Renders <c>FlightRules</c> + <c>CruiseAltitude</c> back to FPE text. Inverse of
    /// <see cref="Parse"/> for the four documented forms.
    /// </summary>
    public static string Format(string flightRules, int cruiseAltitude)
    {
        var isVfr = flightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        var isOtp = flightRules.Equals("OTP", StringComparison.OrdinalIgnoreCase);
        var altStr = cruiseAltitude > 0 ? (cruiseAltitude / 100).ToString("D3") : "";

        if (isOtp)
        {
            return string.IsNullOrEmpty(altStr) ? "OTP" : $"OTP/{altStr}";
        }
        if (isVfr)
        {
            return string.IsNullOrEmpty(altStr) ? "VFR" : $"VFR/{altStr}";
        }
        return altStr;
    }
}
