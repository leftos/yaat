using Yaat.Sim;
using Yaat.Sim.Data.Artcc;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Finds the ARTCC whose vNAS folder the Airport Editor asks for an airport's tower-cab image.</summary>
public static class AirportEditorArtccResolver
{
    /// <summary>
    /// The ARTCC whose lateral boundary holds the map's centre (of several, the one with the smallest box), else the
    /// preferred ARTCC, else none.
    /// </summary>
    /// <param name="centre">The open map's centre.</param>
    /// <param name="preferredArtccId">The user's ARTCC preference, or null or blank for none.</param>
    /// <returns>The ARTCC id, or null when neither gives one.</returns>
    public static string? Resolve(LatLon centre, string? preferredArtccId)
    {
        // An oceanic center's boundary can hold a coastal airport too (ZAK holds KOAK); the domestic center inside it has
        // the smaller box, and it is the one whose vNAS folder holds the airport's image.
        ArtccBoundary? containing = ArtccBoundaryDatabase.Default.FindContaining(centre).MinBy(b => (b.MaxLat - b.MinLat) * (b.MaxLon - b.MinLon));
        if (containing is not null)
        {
            return containing.Id;
        }

        return string.IsNullOrWhiteSpace(preferredArtccId) ? null : preferredArtccId.Trim().ToUpperInvariant();
    }
}
