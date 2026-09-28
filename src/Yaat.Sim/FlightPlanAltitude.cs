using Yaat.Sim.Data;

namespace Yaat.Sim;

/// <summary>
/// Parses and formats the FAA altitude text used in flight plans, matching CRC's documented
/// FPE grammar: <c>NNN</c> (IFR cruise in hundreds of feet), <c>VFR</c>, <c>OTP</c>
/// (VFR-on-top), and <c>VFR/NNN</c> / <c>OTP/NNN</c> (rules with explicit altitude).
/// Altitudes are always hundreds of feet, per CRC's "Unlike legacy clients, altitudes are
/// expressed in hundreds of feet" note. The block (<c>NNNBNNN</c>) form is producible via the
/// ERAM <c>QZ</c> keyboard command (see <c>CrcClientState.Eram.DispatchQz</c>), not by typing in
/// the FPE, so it is <em>rendered</em> by <see cref="Format"/> but not accepted by <see cref="Parse"/>.
/// The above form (ERAM <c>AM ALT ABV/170</c>, <see cref="PlannedAltitude.IsAbove"/>) and the fix-qualified
/// <c>NNN/Fix/NNN</c> form (ERAM <c>AM ALT 170/SJC/110</c>) are both rendered and accepted, the above form as the
/// <c>A170</c> <see cref="Format"/> writes, so both survive a flight-plan editor amend, which resends the altitude text.
/// </summary>
public static class FlightPlanAltitude
{
    /// <summary>
    /// Parses the altitude text into the flight <c>Rules</c> ("IFR"/"VFR") and the filed
    /// <see cref="PlannedAltitude"/> notation (feet). VFR-on-top is an IFR flight (AIM 4-4-8), so
    /// OTP maps to IFR rules with a VFR-on-top notation; only plain VFR maps to VFR rules.
    /// Empty input is treated as VFR with no altitude (matches YAAT's FPE convention). Returns
    /// null when the text matches none of the single, VFR, OTP, above (<c>A170</c>) and fix-qualified forms.
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
            return ("IFR", PlannedAltitude.Otp(null));
        }
        if (text.StartsWith("VFR/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out int vfrAlt))
        {
            return ("VFR", PlannedAltitude.Vfr(vfrAlt * 100));
        }
        if (text.StartsWith("OTP/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out int otpAlt))
        {
            return ("IFR", PlannedAltitude.Otp(otpAlt * 100));
        }
        if (int.TryParse(text, out int alt))
        {
            return ("IFR", PlannedAltitude.Ifr(alt * 100));
        }
        PlannedAltitude? other = ParseAbove(text) ?? ParseFixQualified(text);
        return (other is { } altitude) ? ("IFR", altitude) : null;
    }

    /// <summary>
    /// ERAM field 08's fix-qualified form <c>(d)dd/Fix/(d)dd</c> (AM.yaml field 17 for 08), the one grammar both this parse
    /// and ERAM's <c>AM ALT</c> use: each altitude 1–999 hundreds of feet (<see cref="EramFixResolver.ParseAltitudeHundreds"/>),
    /// and the Fix, which runs from the first slash to the last since a lat/long holds a slash of its own, a form
    /// <see cref="EramFixResolver.IsAltitudeFixForm"/> accepts.
    /// </summary>
    /// <param name="upper">The altitude text, upper case.</param>
    /// <returns>The altitude, or null for any other form.</returns>
    public static PlannedAltitude? ParseFixQualified(string upper)
    {
        int firstSlash = upper.IndexOf('/', StringComparison.Ordinal);
        int lastSlash = upper.LastIndexOf('/');
        if ((firstSlash < 0) || (lastSlash == firstSlash))
        {
            return null;
        }

        string fix = upper[(firstSlash + 1)..lastSlash];
        int? feet = EramFixResolver.ParseAltitudeHundreds(upper[..firstSlash]);
        int? afterFixFeet = EramFixResolver.ParseAltitudeHundreds(upper[(lastSlash + 1)..]);
        bool wellFormed = (feet is not null) && (afterFixFeet is not null) && EramFixResolver.IsAltitudeFixForm(fix);
        return wellFormed ? PlannedAltitude.UntilFix(feet!.Value, fix, afterFixFeet!.Value) : null;
    }

    // The above form Format writes (A170): an A and an altitude of 1-999 hundreds of feet.
    private static PlannedAltitude? ParseAbove(string upper) =>
        (upper.StartsWith('A') && (EramFixResolver.ParseAltitudeHundreds(upper[1..]) is { } feet)) ? PlannedAltitude.Above(feet) : null;

    /// <summary>
    /// Builds a <see cref="PlannedAltitude"/> from a flight-rules label ("IFR"/"VFR"/"OTP") and an
    /// optional single altitude in feet. Used by command handlers that carry rules + a plain altitude
    /// rather than parsed text. Zero/negative feet map to "no altitude".
    /// </summary>
    public static PlannedAltitude FromRulesAndFeet(string flightRules, int? feet)
    {
        int? alt = feet is int f and > 0 ? f : (int?)null;
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

        string altStr = altitude.CruiseFeet is { } feet and > 0 ? (feet / 100).ToString("D3") : "";

        if (altitude is { AltitudeFix: { } fix, AfterFixFeet: { } afterFixFeet })
        {
            return $"{altStr}/{fix}/{afterFixFeet / 100:D3}";
        }

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
