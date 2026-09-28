namespace Yaat.Sim.Commands;

/// <summary>What a field 21 (Hold Data) entry does to the hold.</summary>
public enum EramHoldDataKind
{
    /// <summary>A fix, fix radial distance or lat/long: a new hold there.</summary>
    Location,

    /// <summary><c>P</c>: a new hold at the aircraft's present position.</summary>
    PresentPosition,

    /// <summary><c>dddd</c>: only the EFC time changes.</summary>
    EfcOnly,

    /// <summary><c>/*</c>: only the EFC time is deleted.</summary>
    DeleteEfc,

    /// <summary><c>C</c>: the hold is cancelled.</summary>
    Cancel,
}

/// <summary>What a field 21 entry does to the EFC time.</summary>
public enum EramEfcEdit
{
    None,
    Set,
    Delete,
}

/// <summary>A parsed field 21: the kind, the location as typed (upper-cased, EFC suffix removed) and the EFC edit.</summary>
public readonly record struct EramHoldData(EramHoldDataKind Kind, string? Location, EramEfcEdit Efc, int EfcHhmm);

/// <summary>
/// A parsed field 310. <see cref="Delete"/> is the <c>*</c> form; otherwise <see cref="Direction"/> and <see cref="Turns"/>
/// are CRC <c>CompassDirection</c> / <c>TurnDirection</c> ordinals, <see cref="Radial"/> the inbound radial when one was
/// entered, and a null <see cref="LegLength"/> is <c>STD</c>.
/// </summary>
public readonly record struct EramHoldingInstructions(bool Delete, int? Direction, int? Radial, int? Turns, int? LegLength, bool LegLengthInNm);

/// <summary>A parsed HM / QH hold entry: field 21 and field 310, either of which may be absent.</summary>
public readonly record struct EramHoldEntry(EramHoldData? Data, EramHoldingInstructions? Instructions);
