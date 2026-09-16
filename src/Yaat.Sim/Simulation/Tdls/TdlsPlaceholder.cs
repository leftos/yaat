namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// The FE's "no value" entry, recognised in one place.
///
/// <para>A vTDLS facility's value lists are authored by a Facility Engineer, and the way one says "this field can be
/// left blank" is to list an entry made of dashes — "- - - -" at most facilities, "----" or "-" at others. It is a real
/// selectable entry in the dropdown and a real string on the wire, but it names nothing: a transition whose name is
/// dashes matches no route token, and a clearance field holding dashes is an unfilled field, not an instruction.</para>
///
/// <para>Both sides need the same answer. The client editor drops the entry out of its route matching and its
/// mandatory-field accounting, and the Sim drops it out of the clearance payload and the PDC text, so a PDC can never
/// go out reading "MAINT - - - -". One predicate keeps the two from drifting apart.</para>
/// </summary>
public static class TdlsPlaceholder
{
    /// <summary>
    /// True when <paramref name="value"/> carries no value at all: null, blank, or made only of dashes and spaces.
    /// </summary>
    /// <param name="value">A clearance field value, or an FE-authored SID or transition name.</param>
    /// <returns>True when the value is the FE's "no value" placeholder (or nothing at all); false when it names something.</returns>
    public static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value) || (value.Replace(" ", "").Replace("-", "").Length == 0);
}
