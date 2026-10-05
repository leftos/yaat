namespace Yaat.Sim.Pilot;

/// <summary>What a "ready to taxi" call names as the place the pilot calls from.</summary>
public enum ReadyToTaxiLocationKind
{
    /// <summary>No known place: "at the ramp".</summary>
    Ramp,

    /// <summary>A parking stand: "at gate F8", "at parking GA13", "at KILO RAMP".</summary>
    Stand,

    /// <summary>A ramp spot: "at spot 9".</summary>
    Spot,

    /// <summary>A movement-area taxiway the aircraft sits on: "on taxiway K".</summary>
    Taxiway,

    /// <summary>The stand a completed push started from: "pushed back from gate F8".</summary>
    PushedBackFrom,

    /// <summary>A taxiway hold short: "holding short of C at T41W".</summary>
    HoldingShort,
}

/// <summary>
/// Where a "ready to taxi" call is made from (<see cref="PilotResponder.BuildReadyToTaxi(AircraftState, string, string?, ReadyToTaxiLocation)"/>):
/// its kind, the name it carries (the stand, spot, taxiway or hold-short target), and for a hold short the taxiway the
/// aircraft holds on (empty for every other kind).
/// </summary>
public readonly record struct ReadyToTaxiLocation(ReadyToTaxiLocationKind Kind, string Name, string Taxiway)
{
    public static ReadyToTaxiLocation Ramp => new(ReadyToTaxiLocationKind.Ramp, "", "");

    public static ReadyToTaxiLocation Stand(string stand) => new(ReadyToTaxiLocationKind.Stand, stand, "");

    public static ReadyToTaxiLocation Spot(string spot) => new(ReadyToTaxiLocationKind.Spot, spot, "");

    public static ReadyToTaxiLocation OnTaxiway(string taxiway) => new(ReadyToTaxiLocationKind.Taxiway, taxiway, "");

    public static ReadyToTaxiLocation PushedBackFrom(string stand) => new(ReadyToTaxiLocationKind.PushedBackFrom, stand, "");

    public static ReadyToTaxiLocation HoldingShort(string target, string taxiway) => new(ReadyToTaxiLocationKind.HoldingShort, target, taxiway);

    /// <summary>
    /// The place the stand call names: the stand the aircraft occupies, else the movement-area taxiway it spawned on,
    /// else the ramp.
    /// </summary>
    public static ReadyToTaxiLocation ForStandCall(AircraftState aircraft)
    {
        if (aircraft.Ground.ParkingSpot is { Length: > 0 } stand)
        {
            return Stand(stand);
        }

        return aircraft.Ground.SpawnTaxiway is { Length: > 0 } taxiway ? OnTaxiway(taxiway) : Ramp;
    }
}
