using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// The airport spatial grid buckets positions in 1°×1° (degree) cells, but one degree of
/// <em>longitude</em> is only 60·cos(lat) nm, not 60 nm. A bucket search radius of
/// <c>ceil(maxRangeNm / 60)</c> therefore under-covers the longitude axis away from the
/// equator, so <see cref="NavigationDatabase.FindAirportsWithin"/> (and its siblings
/// <c>FindNearestAirportElevation</c> / <c>FindNearestSizeableAirport</c>, which walk the
/// same grid at a 100 nm cap) silently drop airports that are within range but east/west of
/// the query. Real-navdata regression for that coverage contract.
/// </summary>
public class SpatialGridAuditTests
{
    public SpatialGridAuditTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void FindAirportsWithin_ReturnsEveryAirportInRange_RealNavdata()
    {
        NavigationDatabase db = NavigationDatabase.Instance;
        const double testRange = 100.0;
        const double wideRange = 600.0; // Over-covers the grid, so its filtered result is the ground truth.

        // Probe points near the west edge of a 1° longitude bucket, spanning CONUS and high latitudes.
        (double Lat, double Lon, string Label)[] probes =
        [
            (48.01, -98.99, "CONUS ~48N (North Dakota, ZMP)"),
            (61.01, -149.99, "Alaska ~61N (Anchorage, ZAN)"),
            (64.01, -147.99, "Alaska ~64N (Fairbanks, ZAN)"),
        ];

        var failures = new List<string>();
        foreach ((double lat, double lon, string label) in probes)
        {
            var p = new LatLon(lat, lon);
            var gotIds = new HashSet<string>(db.FindAirportsWithin(p, testRange).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            List<(string Id, LatLon Position)> truth =
            [
                .. db.FindAirportsWithin(p, wideRange).Where(a => GeoMath.DistanceNm(p, a.Position) <= testRange),
            ];

            foreach ((string id, LatLon pos) in truth.Where(t => !gotIds.Contains(t.Id)))
            {
                failures.Add($"[{label}] missed {id} at {GeoMath.DistanceNm(p, pos):F1} nm, bearing {GeoMath.BearingTo(p, pos):F0}");
            }
        }

        Assert.True(failures.Count == 0, "FindAirportsWithin dropped in-range airports:\n" + string.Join("\n", failures));
    }
}
