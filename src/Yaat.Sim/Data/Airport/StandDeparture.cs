using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>How an aircraft leaves a stand: pushed back by a tug, or taxiing out under its own power.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StandDeparture>))]
public enum StandDeparture
{
    /// <summary>The aircraft parks nose-in and is pushed back off the stand.</summary>
    PushBack,

    /// <summary>The aircraft parks facing its way out and taxis off the stand: it has no push targets.</summary>
    TaxiOut,
}

/// <summary>
/// The one place the sim answers whether a stand is pushed back or taxied out of. The layout build stores the geometric
/// answer on each parking node (<see cref="GroundNode.StandDeparture"/>, from <see cref="Classify"/>); an airport sidecar
/// may override it per stand name (<c>standDeparture</c>), and <see cref="StandDepartureOf"/> applies that override. Read
/// a stand's departure through <see cref="StandDepartureOf"/>, never from the node alone.
/// </summary>
public static class StandDepartures
{
    /// <summary>
    /// How far either side of the stand's heading the way out to its taxiway may lie and the stand still be taxied out of,
    /// degrees; exactly this far is a push-back stand.
    /// </summary>
    public const double TaxiOutHalfAngleDeg = 90.0;

    /// <summary>
    /// How close to the stand its connector's perpendicular foot may sit before the bearing to the foot is undefined,
    /// feet: a stand lying on its connector edge's own line has no outward bearing, so it reads as a push-back stand.
    /// </summary>
    private const double IndeterminateFootFt = 1.0;

    private static readonly ILogger Log = SimLog.CreateLogger("StandDepartures");

    /// <summary>
    /// The geometric answer for a stand: <see cref="StandDeparture.TaxiOut"/> when the foot of the perpendicular from the
    /// stand onto the taxiway edge its parking connector joins lies less than <see cref="TaxiOutHalfAngleDeg"/> off the
    /// stand's heading, so the aircraft parked there faces its way out; else <see cref="StandDeparture.PushBack"/>. Only the
    /// parking connector counts, never a gate-group connector. A stand with no heading or no connector, one whose
    /// perpendicular foot is the stand itself (it lies on the connector edge's line, so the outward bearing is undefined),
    /// or one whose foot lies exactly <see cref="TaxiOutHalfAngleDeg"/> off (a broadside stand), is
    /// <see cref="StandDeparture.PushBack"/>.
    /// </summary>
    /// <param name="stand">The parking node.</param>
    /// <param name="connectorFoot">
    /// The foot of the perpendicular from the stand onto the taxiway edge its parking connector joins, or null when the
    /// stand has no connector.
    /// </param>
    /// <returns>The stand's geometric departure.</returns>
    public static StandDeparture Classify(GroundNode stand, LatLon? connectorFoot)
    {
        if ((stand.TrueHeading is not { } heading) || (connectorFoot is not { } foot))
        {
            return StandDeparture.PushBack;
        }

        if ((GeoMath.DistanceNm(stand.Position, foot) * GeoMath.FeetPerNm) < IndeterminateFootFt)
        {
            return StandDeparture.PushBack;
        }

        var outward = new TrueHeading(GeoMath.BearingTo(stand.Position, foot));
        return heading.AbsAngleTo(outward) < TaxiOutHalfAngleDeg ? StandDeparture.TaxiOut : StandDeparture.PushBack;
    }

    /// <summary>
    /// The stand's departure: the airport sidecar's override for the stand's name when <paramref name="sidecars"/> carries
    /// one, else the geometric answer the layout build stored. A parking node with no stored answer comes only from a
    /// layout serialised before <see cref="GroundNode.StandDeparture"/> existed, and reads as
    /// <see cref="StandDeparture.PushBack"/>.
    /// </summary>
    /// <param name="layout">The stand's airport layout.</param>
    /// <param name="stand">The parking node.</param>
    /// <param name="sidecars">The airport sidecars the override is read from.</param>
    /// <returns>The stand's departure.</returns>
    /// <exception cref="ArgumentException"><paramref name="stand"/> is not a parking node.</exception>
    public static StandDeparture StandDepartureOf(AirportGroundLayout layout, GroundNode stand, AirportSidecarCatalog sidecars)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(stand);
        ArgumentNullException.ThrowIfNull(sidecars);
        if (stand.Type != GroundNodeType.Parking)
        {
            throw new ArgumentException(
                $"Node {stand.Id} ({stand.Name ?? "unnamed"}) at {layout.AirportId} is a {stand.Type} node; only a parking node has a stand departure",
                nameof(stand)
            );
        }

        if ((stand.Name is { } name) && sidecars.GetStandDepartureOverrides(layout.AirportId).TryGetValue(name, out StandDeparture overridden))
        {
            return overridden;
        }

        return stand.StandDeparture ?? StandDeparture.PushBack;
    }

    /// <summary>
    /// Warns about each of the airport's stand-departure overrides that names no parking node on the layout, and each that
    /// names several (an override applies to every stand of its name).
    /// </summary>
    /// <param name="layout">The airport's layout.</param>
    /// <param name="sidecars">The airport sidecars the overrides are read from.</param>
    public static void WarnAboutOverrides(AirportGroundLayout layout, AirportSidecarCatalog sidecars)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sidecars);
        foreach (string name in sidecars.GetStandDepartureOverrides(layout.AirportId).Keys.Order(StringComparer.Ordinal))
        {
            int count = layout.Nodes.Values.Count(n =>
                (n.Type == GroundNodeType.Parking) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            if (count == 0)
            {
                Log.LogWarning("Stand-departure override for {Stand} at {Airport} names no stand on the layout", name, layout.AirportId);
            }
            else if (count > 1)
            {
                Log.LogWarning(
                    "Stand-departure override for {Stand} at {Airport} applies to all {Count} stands of that name",
                    name,
                    layout.AirportId,
                    count
                );
            }
        }
    }
}
