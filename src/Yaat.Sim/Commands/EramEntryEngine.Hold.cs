using System.Globalization;
using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

/// <summary>
/// The ERAM hold entries: <c>HM {field 21} [{field 310}]</c> (docs/eram/commands/HM.yaml) and <c>QH {field 21} [{field 310}]</c>,
/// QH's Hold variant (QH.yaml). Both write the flight's <see cref="AircraftHoldAnnotation"/>, display data only (7110.65
/// §5-13-9; no flight behaviour). A new hold (a location or <c>P</c> in field 21) carries only the instructions field 310
/// gives: a fix-only hold leaves direction, turns and leg blank (§4-6-4e). The EFC-only, <c>/*</c> and field-310-only
/// forms read-modify-write the stored hold; <c>C</c> clears it. The live handler checks the entry with
/// <see cref="ParseHoldFields(IReadOnlyList{string}, NavigationDatabase, out EramHoldEntry)"/> and <see cref="RefuseHoldEntry"/>
/// before recording it, looking each location up in its navigation data; <see cref="Apply"/> reads the recorded entry with
/// <see cref="ParseRecordedHoldFields"/>, which trusts the location, so a replay does not depend on the navigation data.
/// </summary>
public static partial class EramEntryEngine
{
    private const string CancelHold = "C";
    private const string DeleteEfcToken = "/*";
    private const string DeleteMarker = "*";
    private const string PresentPositionToken = "P";
    private const int MaxHoldLegNm = 99;
    private const int MaxHoldLegMinutes = 9;
    private const double CompassPointDegrees = 45.0;

    // CRC's TurnDirection ordinals.
    private const int LeftTurn = 0;
    private const int RightTurn = 1;

    /// <summary>The eight field 310 cardinal directions, as CRC <c>CompassDirection</c> ordinals.</summary>
    public static IReadOnlyDictionary<string, int> HoldCardinalPoints { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["NW"] = 0,
            ["N"] = 1,
            ["NE"] = 2,
            ["W"] = 3,
            ["E"] = 4,
            ["SW"] = 5,
            ["S"] = 6,
            ["SE"] = 7,
        };

    // Clockwise from north, so a radial's point is index round(radial / 45), wrapping 360 back to north.
    private static readonly string[] ClockwisePoints = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>
    /// Fields 21 and 310 of an HM or QH hold entry as the live handler checks them, in entry order: one field is 310 when
    /// it has 310's shape (<see cref="IsHoldingInstructionsToken"/>) and 21 otherwise; two are 21 then 310. A field 21
    /// location (a fix, fix radial distance or lat/long, never <c>P</c>) must resolve through
    /// <see cref="EramFixResolver.ParseLocation"/>. Cancelling a hold while giving it instructions is an invalid combination.
    /// </summary>
    /// <param name="fields">The typed fields, without the verb, the flight ID and the field 60 override.</param>
    /// <param name="navDb">The navigation data each field 21 location is looked up in.</param>
    /// <param name="entry">The parsed entry when the fields are valid; the default otherwise.</param>
    /// <returns>
    /// Null when the fields are valid; otherwise the refusal: too short, too long, <c>COFIE FORMAT</c> naming the field, an
    /// invalid EFC time.
    /// </returns>
    public static CommandResult? ParseHoldFields(IReadOnlyList<string> fields, NavigationDatabase navDb, out EramHoldEntry entry)
    {
        ArgumentNullException.ThrowIfNull(navDb);
        return ParseHoldFields(fields, location => EramFixResolver.ParseLocation(location, navDb) is not null, out entry);
    }

    /// <summary>
    /// Fields 21 and 310 of a recorded HM or QH hold entry, read as
    /// <see cref="ParseHoldFields(IReadOnlyList{string}, NavigationDatabase, out EramHoldEntry)"/>
    /// reads them but trusting every field 21 location: the live handler checked it before recording, so a replay does
    /// not depend on the navigation data.
    /// </summary>
    /// <param name="fields">The recorded fields, without the verb and the flight ID.</param>
    /// <param name="entry">The parsed entry when the fields are valid; the default otherwise.</param>
    /// <returns>Null when the fields are valid; otherwise the refusal the live check would give for a malformed shape.</returns>
    public static CommandResult? ParseRecordedHoldFields(IReadOnlyList<string> fields, out EramHoldEntry entry) =>
        ParseHoldFields(fields, _ => true, out entry);

    private static CommandResult? ParseHoldFields(IReadOnlyList<string> fields, Func<string, bool> isLocation, out EramHoldEntry entry)
    {
        entry = default;
        if (fields.Count == 0)
        {
            return Refused(EramEntryErrors.MessageTooShort);
        }
        if (fields.Count > 2)
        {
            return Refused(EramEntryErrors.MessageTooLong);
        }

        (string? dataToken, string? instructionsToken) = SplitHoldTokens(fields);
        return ParseHoldTokens(dataToken, instructionsToken, isLocation, out entry);
    }

    /// <summary>Field 21 then field 310, each when entered; a cancel given with instructions is an invalid combination.</summary>
    private static CommandResult? ParseHoldTokens(
        string? dataToken,
        string? instructionsToken,
        Func<string, bool> isLocation,
        out EramHoldEntry entry
    )
    {
        entry = default;
        (EramHoldData? data, CommandResult? dataRefusal) = dataToken is null ? (null, null) : ParseHoldData(dataToken, isLocation);
        if (dataRefusal is not null)
        {
            return dataRefusal;
        }
        (EramHoldingInstructions? instructions, CommandResult? instructionsRefusal) = instructionsToken is null
            ? (null, null)
            : ParseHoldingInstructions(instructionsToken);
        if (instructionsRefusal is not null)
        {
            return instructionsRefusal;
        }
        if ((data?.Kind == EramHoldDataKind.Cancel) && (instructions is not null))
        {
            return Refused(EramEntryErrors.InvalidCombination);
        }

        entry = new EramHoldEntry(data, instructions);
        return null;
    }

    /// <summary>
    /// The refusal for an entry that only changes the stored hold (an EFC, <c>/*</c> or field 310 alone) when no hold is
    /// stored: it lacks the hold location field 21 would have given, so it is too short. Null when the entry applies.
    /// </summary>
    public static CommandResult? RefuseHoldEntry(AircraftState ac, EramHoldEntry entry)
    {
        bool placesOrCancels = entry.Data?.Kind is EramHoldDataKind.Location or EramHoldDataKind.PresentPosition or EramHoldDataKind.Cancel;
        return (placesOrCancels || (ac.HoldAnnotation.Fix is not null)) ? null : Refused(EramEntryErrors.MessageTooShort);
    }

    /// <summary>
    /// Whether a token has field 310's shape rather than field 21's: <c>*</c>, or three parts with <c>LT</c>/<c>RT</c> in
    /// the middle.
    /// </summary>
    public static bool IsHoldingInstructionsToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (token == DeleteMarker)
        {
            return true;
        }
        string[] parts = token.ToUpperInvariant().Split('/');
        return (parts.Length == 3) && (parts[1] is "LT" or "RT");
    }

    /// <summary>An HM or QH hold entry, checked and recorded by the live handler (see the class remarks).</summary>
    private static CommandResult ApplyHold(AircraftState ac, List<string> args, string verb)
    {
        if (ParseRecordedHoldFields(args, out EramHoldEntry entry) is { } refusal)
        {
            return refusal;
        }

        if (RefuseHoldEntry(ac, entry) is { } noHold)
        {
            return noHold;
        }

        ac.HoldAnnotation = entry.Data?.Kind == EramHoldDataKind.Cancel ? new AircraftHoldAnnotation() : NextHold(ac, entry);
        return new CommandResult(true, $"{verb} {string.Join(' ', args).ToUpperInvariant()} {ac.Callsign}");
    }

    private static (string? Data, string? Instructions) SplitHoldTokens(IReadOnlyList<string> fields)
    {
        if (fields.Count == 2)
        {
            return (fields[0], fields[1]);
        }
        return IsHoldingInstructionsToken(fields[0]) ? (null, fields[0]) : (fields[0], null);
    }

    /// <summary>The hold after an entry that places a hold or changes the stored one (<see cref="RefuseHoldEntry"/> passed).</summary>
    private static AircraftHoldAnnotation NextHold(AircraftState ac, EramHoldEntry entry)
    {
        AircraftHoldAnnotation next = entry.Data is { Kind: EramHoldDataKind.Location or EramHoldDataKind.PresentPosition } located
            ? new AircraftHoldAnnotation { Fix = located.Kind == EramHoldDataKind.Location ? located.Location : PresentPositionHoldFix(ac.Position) }
            : AircraftHoldAnnotation.FromSnapshot(ac.HoldAnnotation.ToSnapshot());

        if (entry.Data is { } data)
        {
            ApplyEfcEdit(next, data);
        }
        if (entry.Instructions is { } instructions)
        {
            ApplyHoldingInstructions(next, instructions);
        }
        return next;
    }

    /// <summary>Sets or deletes the EFC as field 21 says; an entry that names no EFC leaves the hold's as it is.</summary>
    private static void ApplyEfcEdit(AircraftHoldAnnotation hold, EramHoldData data)
    {
        hold.Efc = data.Efc switch
        {
            EramEfcEdit.Set => data.EfcHhmm,
            EramEfcEdit.Delete => null,
            _ => hold.Efc,
        };
    }

    private static void ApplyHoldingInstructions(AircraftHoldAnnotation hold, EramHoldingInstructions instructions)
    {
        hold.Direction = instructions.Direction;
        hold.Radial = instructions.Radial;
        hold.Turns = instructions.Turns;
        hold.LegLength = instructions.LegLength;
        hold.LegLengthInNm = instructions.LegLengthInNm;
    }

    /// <summary>A present-position hold's fix: the aircraft's position at entry time as an ERAM lat/long, <c>ddmmN/dddmmW</c>.</summary>
    private static string PresentPositionHoldFix(LatLon position) =>
        $"{DegreesMinutes(Math.Abs(position.Lat), 2)}{(position.Lat >= 0 ? 'N' : 'S')}/"
        + $"{DegreesMinutes(Math.Abs(position.Lon), 3)}{(position.Lon < 0 ? 'W' : 'E')}";

    private static string DegreesMinutes(double degrees, int degreeDigits)
    {
        int totalMinutes = (int)Math.Round(degrees * 60, MidpointRounding.AwayFromZero);
        string whole = (totalMinutes / 60).ToString(CultureInfo.InvariantCulture).PadLeft(degreeDigits, '0');
        return whole + (totalMinutes % 60).ToString("D2", CultureInfo.InvariantCulture);
    }

    // ─── Field 21 hold data ──────────────────────────────────────────────

    /// <summary>
    /// Field 21 (Hold Data), HM.yaml and QH.yaml: a fix, fix radial distance or lat/long, or <c>P</c>, each with an
    /// optional <c>/dddd</c> EFC time or <c>/*</c> EFC deletion; an EFC time <c>dddd</c> alone (four digits are always a
    /// time, never a fix); <c>/*</c>; or <c>C</c> to cancel the hold. A malformed or unknown location is <c>COFIE FORMAT</c>
    /// naming the whole field; an EFC with hours above 23 or minutes above 59 is <c>INVALID TIME</c>.
    /// </summary>
    private static (EramHoldData? Data, CommandResult? Refusal) ParseHoldData(string token, Func<string, bool> isLocation)
    {
        string upper = token.ToUpperInvariant();
        if (upper == CancelHold)
        {
            return (new EramHoldData(EramHoldDataKind.Cancel, null, EramEfcEdit.None, 0), null);
        }
        if (upper == DeleteEfcToken)
        {
            return (new EramHoldData(EramHoldDataKind.DeleteEfc, null, EramEfcEdit.Delete, 0), null);
        }
        if (IsFourDigits(upper))
        {
            return ParseEfc(upper) is { } efc
                ? (new EramHoldData(EramHoldDataKind.EfcOnly, null, EramEfcEdit.Set, efc), null)
                : (null, Refused(EramEntryErrors.InvalidTime));
        }
        return ParseLocatedHoldData(upper, token, isLocation);
    }

    // A location with an EFC suffix splits at its last slash; a lat/long without one ("3730/12200") has a slash of its own,
    // so a head that is no location falls back to reading the whole field as the location.
    private static (EramHoldData? Data, CommandResult? Refusal) ParseLocatedHoldData(string upper, string token, Func<string, bool> isLocation)
    {
        int slash = upper.LastIndexOf('/');
        if (slash > 0)
        {
            string suffix = upper[(slash + 1)..];
            bool isEfcSuffix = (suffix == DeleteMarker) || IsFourDigits(suffix);
            EramHoldData? located = isEfcSuffix ? TryHoldLocation(upper[..slash], isLocation) : null;
            if (located is { } hold)
            {
                return WithEfcSuffix(hold, suffix);
            }
        }

        return TryHoldLocation(upper, isLocation) is { } whole ? (whole, null) : (null, Refused(EramEntryErrors.CofieFormat, token));
    }

    // Four digits are always a time, never a location, so the split of "3730/1230" never rests on the navigation data.
    private static EramHoldData? TryHoldLocation(string location, Func<string, bool> isLocation)
    {
        if (location == PresentPositionToken)
        {
            return new EramHoldData(EramHoldDataKind.PresentPosition, null, EramEfcEdit.None, 0);
        }
        return ((!IsFourDigits(location)) && isLocation(location))
            ? new EramHoldData(EramHoldDataKind.Location, location, EramEfcEdit.None, 0)
            : null;
    }

    private static (EramHoldData? Data, CommandResult? Refusal) WithEfcSuffix(EramHoldData hold, string suffix)
    {
        if (suffix == DeleteMarker)
        {
            return (hold with { Efc = EramEfcEdit.Delete }, null);
        }
        return ParseEfc(suffix) is { } efc
            ? (hold with { Efc = EramEfcEdit.Set, EfcHhmm = efc }, null)
            : (null, Refused(EramEntryErrors.InvalidTime));
    }

    /// <summary>A four-digit EFC time as the <c>HHMM</c> integer the hold stores (<c>0915</c> → 915); null past 2359.</summary>
    private static int? ParseEfc(string time)
    {
        int hours = int.Parse(time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int minutes = int.Parse(time.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        return (hours <= 23) && (minutes <= 59) ? (hours * 100) + minutes : null;
    }

    private static bool IsFourDigits(string text) => (text.Length == 4) && text.All(char.IsAsciiDigit);

    // ─── Field 310 holding instructions ──────────────────────────────────

    /// <summary>
    /// Field 310 (Holding Instructions), HM.yaml and QH.yaml: a cardinal direction <c>(L)L/LL/(a)(a)aaa</c> (one of the 8
    /// points, <c>LT</c> or <c>RT</c>, then <c>STD</c>, <c>(d)dNM</c> 1–99 or <c>dMIN</c> 1–9); an inbound radial
    /// <c>ddd/LL/(a)(a)aaa</c> (001–360, the leg <c>(d)dNM</c> or <c>dMIN</c>, never <c>STD</c>); or <c>*</c> to delete the
    /// instructions. A radial's direction is the nearest of the 8 points to the radial itself. Any other form is
    /// <c>COFIE FORMAT</c> naming the field.
    /// </summary>
    private static (EramHoldingInstructions? Instructions, CommandResult? Refusal) ParseHoldingInstructions(string token)
    {
        string upper = token.ToUpperInvariant();
        if (upper == DeleteMarker)
        {
            return (new EramHoldingInstructions(true, null, null, null, null, false), null);
        }

        string[] parts = upper.Split('/');
        EramHoldingInstructions? parsed = parts.Length == 3 ? TryHoldingInstructions(parts[0], parts[1], parts[2]) : null;
        return parsed is { } instructions ? (instructions, null) : (null, Refused(EramEntryErrors.CofieFormat, token));
    }

    private static EramHoldingInstructions? TryHoldingInstructions(string direction, string turns, string leg)
    {
        int? turnDirection = turns switch
        {
            "LT" => LeftTurn,
            "RT" => RightTurn,
            _ => null,
        };
        if (turnDirection is null)
        {
            return null;
        }

        if (HoldCardinalPoints.TryGetValue(direction, out int point))
        {
            return TryHoldLeg(leg, allowStandard: true) is { } cardinalLeg
                ? new EramHoldingInstructions(false, point, null, turnDirection, cardinalLeg.Length, cardinalLeg.InNm)
                : null;
        }

        int? radial = ParseHoldRadial(direction);
        return (radial is { } r) && (TryHoldLeg(leg, allowStandard: false) is { } radialLeg)
            ? new EramHoldingInstructions(false, RoundRadialToPoint(r), r, turnDirection, radialLeg.Length, radialLeg.InNm)
            : null;
    }

    /// <summary>
    /// The CRC <c>CompassDirection</c> ordinal of the compass point nearest a whole-degree radial: the radial itself, not
    /// its reciprocal.
    /// </summary>
    private static int RoundRadialToPoint(int radial)
    {
        int index = (int)Math.Floor((radial + (CompassPointDegrees / 2)) / CompassPointDegrees) % ClockwisePoints.Length;
        return HoldCardinalPoints[ClockwisePoints[index]];
    }

    private static int? ParseHoldRadial(string direction)
    {
        if ((direction.Length != 3) || !direction.All(char.IsAsciiDigit))
        {
            return null;
        }
        int radial = int.Parse(direction, NumberStyles.None, CultureInfo.InvariantCulture);
        return radial is >= 1 and <= 360 ? radial : null;
    }

    /// <summary>A field 310 leg: <c>STD</c> (no length) when allowed, <c>(d)dNM</c> 1–99, or <c>dMIN</c> 1–9; null for any other form.</summary>
    private static (int? Length, bool InNm)? TryHoldLeg(string leg, bool allowStandard)
    {
        if (leg == "STD")
        {
            return allowStandard ? (null, false) : null;
        }
        if (leg.EndsWith("NM", StringComparison.Ordinal))
        {
            int? nm = ParseHoldLegNumber(leg[..^2], maxDigits: 2, MaxHoldLegNm);
            return nm is { } length ? (length, true) : null;
        }
        if (leg.EndsWith("MIN", StringComparison.Ordinal))
        {
            int? minutes = ParseHoldLegNumber(leg[..^3], maxDigits: 1, MaxHoldLegMinutes);
            return minutes is { } length ? (length, false) : null;
        }
        return null;
    }

    private static int? ParseHoldLegNumber(string digits, int maxDigits, int max)
    {
        if ((digits.Length == 0) || (digits.Length > maxDigits) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }
        int value = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return (value >= 1) && (value <= max) ? value : null;
    }
}
