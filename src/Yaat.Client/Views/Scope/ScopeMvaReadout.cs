using System.Globalization;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>The Ctrl-hover MVA readout's text: the MVA floor and sector at a point.</summary>
internal static class ScopeMvaReadout
{
    /// <summary>
    /// The readout at <paramref name="position"/>: <c>MVA 3000 (9)</c>, the controlling sector's floor in feet MSL and its
    /// name, <paramref name="facilityId"/>'s chart winning where charts overlap; null where no sector of
    /// <paramref name="mva"/> covers the point.
    /// </summary>
    public static string? TextAt(MvaDatabase mva, LatLon position, string? facilityId) =>
        mva.FindSector(position, facilityId) is { } sector
            ? string.Create(CultureInfo.InvariantCulture, $"MVA {sector.FloorFtMsl} ({sector.Sector})")
            : null;
}
