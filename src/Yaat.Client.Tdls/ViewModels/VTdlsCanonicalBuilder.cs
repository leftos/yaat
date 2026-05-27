using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Translates vTDLS UI gestures into canonical command strings that the server's
/// <c>CommandParser</c> understands. One method per verb so call-sites stay
/// declarative and the actual canonical form lives in exactly one place.
///
/// Mirrors <see cref="VStripsCanonicalBuilder"/> — the strip equivalent. Output
/// strings round-trip through <c>SendCommandAsync(callsign, command, initials)</c>.
/// </summary>
public static class VTdlsCanonicalBuilder
{
    /// <summary>TDLSQ — queue a Pending PDC for the aircraft's filed departure facility.</summary>
    public static string BuildQueue() => "TDLSQ";

    /// <summary>
    /// TDLSS &lt;fields&gt; — send the queued PDC. <paramref name="clearance"/>
    /// is serialized to nine '|'-separated fields in the canonical order
    /// (Expect|Sid|Transition|Climbout|Climbvia|InitialAlt|ContactInfo|DepFreq|LocalInfo).
    /// Empty between separators encodes null on the server side.
    /// </summary>
    public static string BuildSend(ClearanceDto clearance)
    {
        string?[] fields =
        [
            clearance.Expect,
            clearance.Sid,
            clearance.Transition,
            clearance.Climbout,
            clearance.Climbvia,
            clearance.InitialAlt,
            clearance.ContactInfo,
            clearance.DepFreq,
            clearance.LocalInfo,
        ];
        return "TDLSS " + string.Join('|', fields.Select(static v => v ?? ""));
    }

    /// <summary>TDLSW — manually force the Sent PDC to WILCO.</summary>
    public static string BuildWilco() => "TDLSW";

    /// <summary>TDLSDUMP — remove the PDC from TDLS. Terminal for the session.</summary>
    public static string BuildDump() => "TDLSDUMP";
}
