using System.Text.Json.Serialization;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// A named route kept in the <see cref="RouteLibrary"/>: geometry only, with no stamp profile. It is tagged with the ARTCC it
/// was made in so the library can list the open scenario's ARTCC by default.
/// </summary>
/// <param name="Id">The route's id, a GUID string; it names the route's file, so a route name never reaches a path.</param>
/// <param name="Name">The name the author gave the route.</param>
/// <param name="Color">The colour the route is drawn in, such as <c>#FF8800</c>; null for the default.</param>
/// <param name="ArtccId">The ARTCC the route was made in, such as <c>ZOA</c>.</param>
/// <param name="Points">The route's points in flying order.</param>
public sealed record SavedRoute(
    string Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Color,
    string ArtccId,
    IReadOnlyList<RoutePoint> Points
)
{
    /// <summary>A fresh route id.</summary>
    /// <returns>A new GUID string.</returns>
    public static string NewId() => Guid.NewGuid().ToString("D");
}
