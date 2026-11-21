using System.Globalization;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope.FieldMenus.Crc;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// What a pick on a CRC FDB menu does for the aircraft: on ERAM the scope-data entries CRC's menu makes, built by
/// <see cref="ScopeDataEntries"/> as the detailed popups' rows build them; on PILOT the pilot command the detailed popups' PILOT
/// panels send for the same value, or none where no pilot command matches. And the values each menu opens with, read from the
/// aircraft's ERAM state as the detailed popups read it.
/// </summary>
internal static class CrcMenuCommands
{
    /// <summary>The altitudes the altitude menu opens with, in hundreds of feet.</summary>
    public static CrcAltitudeValues AltitudeValues(AircraftModel ac) =>
        new(FlightPlanHundreds(ac), ac.EramInterimAltitude, ac.EramLocalInterimAltitude, ac.EramProcedureAltitude)
        {
            FlightPlanAltitudeText = ac.CruiseAltitudeDisplay,
            IsVfr = IsVfr(ac),
        };

    /// <summary>The ground track and the HSF heading the heading menu opens with.</summary>
    public static CrcHeadingValues HeadingValues(AircraftModel ac) =>
        new(((int)Math.Round(ac.TrueTrack, MidpointRounding.AwayFromZero) % 360), ac.EramAssignedHeading);

    /// <summary>The HSF speed and the altitudes the speed menu opens with.</summary>
    public static CrcSpeedValues SpeedValues(AircraftModel ac) => new(ac.EramAssignedSpeed, ac.EramInterimAltitude, FlightPlanHundreds(ac));

    /// <summary>
    /// The ERAM entries an altitude pick runs, in order: QZ (VFR/ or OTP/ before the altitude on a VFR plan), QQ, QQ L, QQ P,
    /// and for FP, as CRC's HandleFlightPlanAssignedAltitudePickAreaPicked: QQ while an interim or procedure altitude is set,
    /// then QQ L while a local interim is set.
    /// </summary>
    public static IReadOnlyList<string> EramAltitude(CrcAltitudePick pick, AircraftModel ac)
    {
        string flid = ac.Callsign;
        int alt = pick.AltitudeHundreds;
        return pick.Area switch
        {
            CrcAltitudePickArea.Altitude => [AssignedAltitude(alt, ac)],
            CrcAltitudePickArea.T => [ScopeDataEntries.Interim(alt, flid)],
            CrcAltitudePickArea.LocalTalt => [ScopeDataEntries.Local(alt, flid)],
            CrcAltitudePickArea.Procedure => [ScopeDataEntries.Procedure(alt, flid)],
            CrcAltitudePickArea.Fp =>
            [
                .. ((ac.EramInterimAltitude is not null) || (ac.EramProcedureAltitude is not null))
                    ? [ScopeDataEntries.ClearInterimOrProcedure(flid)]
                    : Array.Empty<string>(),
                .. (ac.EramLocalInterimAltitude is not null) ? [ScopeDataEntries.ClearLocal(flid)] : Array.Empty<string>(),
            ],
            CrcAltitudePickArea.InterimT => [ScopeDataEntries.ClearLocal(flid)],
            _ => throw new ArgumentOutOfRangeException(nameof(pick), pick.Area, "Not an altitude pick area."),
        };
    }

    /// <summary>
    /// The pilot command an altitude pick sends: for a list altitude or its T column, the CM or DM the Maintain altitude panel's
    /// row for that altitude sends (<see cref="AltitudeFlyout.MaintainCommandFor"/>); none for FP, <c>nnn T</c>, LOCAL TALT and
    /// PROCEDURE.
    /// </summary>
    public static string? PilotAltitude(CrcAltitudePick pick, double currentFeet, double? assignedFeet) =>
        (pick.Area is CrcAltitudePickArea.Altitude or CrcAltitudePickArea.T)
            ? AltitudeFlyout.MaintainCommandFor(pick.AltitudeHundreds * 100, currentFeet, assignedFeet)
            : null;

    /// <summary>The ERAM entry a heading pick runs: QS Hddd, QS nL, QS nR, QS PH or QS */.</summary>
    public static string EramHeading(CrcHeadingPick pick, string flid)
    {
        string degrees = pick.Heading.ToString(CultureInfo.InvariantCulture);
        return pick.Area switch
        {
            CrcHeadingPickArea.Heading => ScopeDataEntries.HsfHeading($"H{pick.Heading.ToString("D3", CultureInfo.InvariantCulture)}", flid),
            CrcHeadingPickArea.Lt => ScopeDataEntries.HsfHeading($"{degrees}L", flid),
            CrcHeadingPickArea.Rt => ScopeDataEntries.HsfHeading($"{degrees}R", flid),
            CrcHeadingPickArea.Ph => ScopeDataEntries.HsfHeading("PH", flid),
            CrcHeadingPickArea.Delete => ScopeDataEntries.HsfClearHeading(flid),
            _ => throw new ArgumentOutOfRangeException(nameof(pick), pick.Area, "Not a heading pick area."),
        };
    }

    /// <summary>The pilot command a heading pick sends: FH ddd, LT n, RT n or FPH; none for DELETE.</summary>
    public static string? PilotHeading(CrcHeadingPick pick)
    {
        string degrees = pick.Heading.ToString(CultureInfo.InvariantCulture);
        return pick.Area switch
        {
            CrcHeadingPickArea.Heading => $"FH {pick.Heading.ToString("000", CultureInfo.InvariantCulture)}",
            CrcHeadingPickArea.Lt => $"LT {degrees}",
            CrcHeadingPickArea.Rt => $"RT {degrees}",
            CrcHeadingPickArea.Ph => "FPH",
            _ => null,
        };
    }

    /// <summary>The ERAM entry a speed pick runs: QS /Sddd or /Mdd, QS /ddd+ or /ddd- (Mdd+ and Mdd- for Mach), or QS /*.</summary>
    public static string EramSpeed(CrcSpeedPick pick, string flid)
    {
        string value = pick.IsMach
            ? $"M{pick.Speed.ToString("D2", CultureInfo.InvariantCulture)}"
            : pick.Speed.ToString("D3", CultureInfo.InvariantCulture);
        return pick.Area switch
        {
            CrcSpeedPickArea.Kt => ScopeDataEntries.HsfSpeed(pick.IsMach ? value : $"S{value}", flid),
            CrcSpeedPickArea.Plus => ScopeDataEntries.HsfSpeed($"{value}+", flid),
            CrcSpeedPickArea.Minus => ScopeDataEntries.HsfSpeed($"{value}-", flid),
            CrcSpeedPickArea.Del => ScopeDataEntries.HsfClearSpeed(flid),
            _ => throw new ArgumentOutOfRangeException(nameof(pick), pick.Area, "Not a speed pick area."),
        };
    }

    /// <summary>
    /// The pilot command a speed pick sends: the Assign speed panel's SPD or MACH command for the value; with + or -, SPD's
    /// or-greater or or-less form (<c>SPD 250+</c>), and for Mach the plain MACH command, which has no such form; RNS for DEL.
    /// </summary>
    public static string PilotSpeed(CrcSpeedPick pick)
    {
        if (pick.Area == CrcSpeedPickArea.Del)
        {
            return "RNS";
        }

        if (pick.IsMach)
        {
            return SpeedFlyout.MachCommand(pick.Speed);
        }

        string knots = SpeedFlyout.KnotsCommand(pick.Speed);
        return pick.Area switch
        {
            CrcSpeedPickArea.Plus => $"{knots}+",
            CrcSpeedPickArea.Minus => $"{knots}-",
            _ => knots,
        };
    }

    // QZ ddd, or on a VFR plan QZ VFR/ddd (OTP/ddd for VFR-on-top), as CRC's menu sends it.
    private static string AssignedAltitude(int hundreds, AircraftModel ac)
    {
        string flid = ac.Callsign;
        if (!IsVfr(ac))
        {
            return ScopeDataEntries.FpAltitude(hundreds, flid);
        }

        string prefix = ac.IsVfrOnTop ? "OTP/" : "VFR/";
        return $"QZ {prefix}{hundreds.ToString("D3", CultureInfo.InvariantCulture)} {flid}";
    }

    private static bool IsVfr(AircraftModel ac) => ac.IsVfrOnTop || ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);

    // The flight-plan altitude in hundreds of feet, as the altitude popup's FP row reads it; null while the plan has none.
    private static int? FlightPlanHundreds(AircraftModel ac) => (ac.CruiseAltitude > 0) ? ac.CruiseAltitude / 100 : null;
}
