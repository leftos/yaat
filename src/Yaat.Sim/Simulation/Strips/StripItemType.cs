namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// What a strip item is: a printed flight strip, one of the four separator styles, half of a split strip, or a blank.
/// The values are the vStrips wire numbering, which <see cref="StripItemRecord.Type"/> stores as an <c>int</c> (the
/// snapshot carries the number, so the schema does not move when this enum gains a member) and the server's
/// <c>DtoConverter</c> casts to its own wire enum.
/// </summary>
public enum StripItemType
{
    DepartureStrip = 0,
    ArrivalStrip = 1,
    HandwrittenSeparator = 2,
    WhiteSeparator = 3,
    RedSeparator = 4,
    GreenSeparator = 5,
    HalfStripLeft = 6,
    HalfStripRight = 7,
    BlankStrip = 8,
}
