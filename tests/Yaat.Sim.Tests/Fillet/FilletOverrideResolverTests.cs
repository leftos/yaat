using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>How per-corner fillet overrides match the corners of OAK's pre-fillet layout: by apex, taxiway names and arm bearings.</summary>
public class FilletOverrideResolverTests
{
    private const double FiftyFeetDeg = 50.0 / GeoMath.FeetPerNm / 60.0;

    [Fact]
    public void Override_KeyedOnARealCorner_MatchesItsJunction()
    {
        (AirportGroundLayout pre, FilletCornerReport corner) = Assert.NotNull(OakCorner());

        FilletOverrideReport row = Assert.Single(FilletReportLayouts.Apply(pre, FilletReportLayouts.OverrideFor(corner)).Overrides);

        Assert.Equal(FilletOverrideMatch.Matched, row.Match);
        Assert.Equal(corner.JunctionNodeId, row.JunctionNodeId);
    }

    [Fact]
    public void Override_WhoseApexMoved50Feet_IsOrphaned()
    {
        (AirportGroundLayout pre, FilletCornerReport corner) = Assert.NotNull(OakCorner());

        CornerFilletOverride moved = FilletReportLayouts.OverrideFor(corner) with { ApexLat = corner.ApexLat + FiftyFeetDeg };
        FilletOverrideReport row = Assert.Single(FilletReportLayouts.Apply(pre, moved).Overrides);

        Assert.Equal(FilletOverrideMatch.Orphaned, row.Match);
        Assert.Null(row.JunctionNodeId);
        Assert.Equal($"no corner within 10 ft with taxiways {moved.TaxiwayA}/{moved.TaxiwayB} and matching arm bearings", row.Detail);
    }

    [Fact]
    public void Override_NamingTheCornersTaxiwaysInTheOtherOrder_MatchesItsJunction()
    {
        (AirportGroundLayout pre, FilletCornerReport corner) = Assert.NotNull(OakCorner());

        CornerFilletOverride key = FilletReportLayouts.OverrideFor(corner);
        CornerFilletOverride swapped = key with
        {
            TaxiwayA = key.TaxiwayB,
            TaxiwayB = key.TaxiwayA,
            BearingA = key.BearingB,
            BearingB = key.BearingA,
        };
        FilletOverrideReport row = Assert.Single(FilletReportLayouts.Apply(pre, swapped).Overrides);

        Assert.Equal(FilletOverrideMatch.Matched, row.Match);
        Assert.Equal(corner.JunctionNodeId, row.JunctionNodeId);
    }

    [Fact]
    public void TwoOverrides_NamingOneCorner_AreBothAmbiguous()
    {
        (AirportGroundLayout pre, FilletCornerReport corner) = Assert.NotNull(OakCorner());

        CornerFilletOverride first = FilletReportLayouts.OverrideFor(corner);
        CornerFilletOverride second = first with { ApexLat = first.ApexLat + 1e-6 };
        IReadOnlyList<FilletOverrideReport> rows = FilletReportLayouts.Apply(pre, first, second).Overrides;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(FilletOverrideMatch.Ambiguous, r.Match));
        Assert.All(rows, r => Assert.Equal("2 overrides name this corner", r.Detail));
        Assert.Same(first, rows[0].Override);
        Assert.Same(second, rows[1].Override);
    }

    [Fact]
    public void Override_BetweenTwoJunctionsWithinTenFeetOfBoth_IsAmbiguous()
    {
        // OAK junctions 291 and 292 lie 18.7 ft apart and both join B1 and RAMP at matching bearings.
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet("oak");
        if (pre is null)
        {
            return;
        }

        GroundNode near = pre.Nodes[291];
        GroundNode far = pre.Nodes[292];
        IReadOnlyList<FilletCornerReport> corners = FilletReportLayouts.Apply(pre).Corners;
        List<FilletCornerReport> farPairs = [.. corners.Where(c => (c.JunctionNodeId == 292) && (c.TaxiwayA == "B1") && (c.TaxiwayB == "RAMP"))];
        FilletCornerReport corner = corners.First(c =>
            (c.JunctionNodeId == 291) && (c.TaxiwayA == "B1") && (c.TaxiwayB == "RAMP") && farPairs.Any(f => SameBearings(c, f))
        );
        CornerFilletOverride between = FilletReportLayouts.OverrideFor(corner) with
        {
            ApexLat = (near.Position.Lat + far.Position.Lat) / 2.0,
            ApexLon = (near.Position.Lon + far.Position.Lon) / 2.0,
        };
        FilletOverrideReport row = Assert.Single(FilletReportLayouts.Apply(pre, between).Overrides);

        Assert.Equal(FilletOverrideMatch.Ambiguous, row.Match);
        Assert.Null(row.JunctionNodeId);
        Assert.EndsWith(" corners match", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>True when both corners' arms point within 15 degrees of each other, arm for arm.</summary>
    private static bool SameBearings(FilletCornerReport first, FilletCornerReport second) =>
        (BearingGapDeg(Assert.NotNull(first.BearingA), Assert.NotNull(second.BearingA)) <= 15.0)
        && (BearingGapDeg(Assert.NotNull(first.BearingB), Assert.NotNull(second.BearingB)) <= 15.0);

    private static double BearingGapDeg(double first, double second) => Math.Abs(((((first - second) % 360.0) + 540.0) % 360.0) - 180.0);

    /// <summary>OAK's pre-fillet layout and the first corner its Standard pass builds between two differently named taxiways.</summary>
    /// <returns>The layout and the corner, or null when OAK's layout is not committed.</returns>
    private static (AirportGroundLayout Pre, FilletCornerReport Corner)? OakCorner()
    {
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet("oak");
        if (pre is null)
        {
            return null;
        }

        FilletCornerReport corner = FilletReportLayouts
            .Apply(pre)
            .Corners.First(c =>
                (c.Outcome == FilletCornerOutcome.Built) && !string.Equals(c.TaxiwayA, c.TaxiwayB, StringComparison.OrdinalIgnoreCase)
            );
        return (pre, corner);
    }
}
