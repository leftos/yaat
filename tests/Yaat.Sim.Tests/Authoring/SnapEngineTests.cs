using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="SnapEngine"/> on the committed OAK map: a target within the threshold snaps to the target's exact position, one
/// beyond it does not, a disabled kind is ignored, a tie resolves by kind then feature order, the dragged vertex is never its
/// own target while its feature's other vertices are, and a caller's own extra vertices count.
/// </summary>
public class SnapEngineTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    // OAK features pinned by index in the committed map. Stand "25" has no other target within 180 ft.
    private const int Stand25Index = 0;

    private const int Spot2Index = 182;

    private const int Taxiway199Index = 199;

    private const int Taxiway200Index = 200;

    private const int Taxiway201Index = 201;

    private const int TaxiwayWIndex = 188;

    // Spot "3" (173) sits 0.6 ft from taxiway 203's vertex 6, the next target 68 ft away.
    private const int Spot173Index = 173;

    private const int Taxiway203Index = 203;

    [Fact]
    public void WithinThreshold_SnapsToTheExactTarget()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        MapPosition cursor = OffsetNorth(stand, 5.0);

        SnapResult snapped = Assert.IsType<SnapResult>(SnapEngine.Find(document, cursor, SnapOptions.Default, null, null));

        Assert.Equal(SnapTargetKind.Parking, snapped.Kind);
        Assert.Equal(Stand25Index, snapped.FeatureIndex);
        Assert.Equal(0, snapped.VertexIndex);
        Assert.Equal(stand, snapped.Position);
        Assert.Equal(FeetBetween(cursor, stand), snapped.DistanceFt, 6);
    }

    [Fact]
    public void ExactlyAtThreshold_Snaps()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        MapPosition cursor = OffsetNorth(stand, 30.0);
        SnapOptions options = SnapOptions.Default with { ThresholdFt = FeetBetween(cursor, stand) };

        SnapResult snapped = Assert.IsType<SnapResult>(SnapEngine.Find(document, cursor, options, null, null));

        Assert.Equal(stand, snapped.Position);
    }

    [Fact]
    public void BeyondThreshold_ReturnsNoSnap()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        MapPosition cursor = OffsetNorth(stand, 30.0);
        SnapOptions options = SnapOptions.Default with { ThresholdFt = FeetBetween(cursor, stand) - 1.0 };

        Assert.Null(SnapEngine.Find(document, cursor, options, null, null));
    }

    [Fact]
    public void DisabledKind_IsIgnored()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        MapPosition cursor = OffsetNorth(stand, 5.0);

        Assert.Null(SnapEngine.Find(document, cursor, SnapOptions.Default with { Parking = false }, null, null));
    }

    [Fact]
    public void DisabledNearerKind_FartherEnabledKindWins()
    {
        AirportMapDocument document = Oak();
        MapPosition cursor = FarFromOak(document);
        SnapTarget nearer = new(OffsetNorth(cursor, -4.0), SnapTargetKind.Parking);
        SnapTarget farther = new(OffsetNorth(cursor, -8.0), SnapTargetKind.TaxiwayVertex);

        SnapResult snapped = Assert.IsType<SnapResult>(
            SnapEngine.Find(document, cursor, SnapOptions.Default with { Parking = false }, null, [nearer, farther])
        );

        Assert.Equal(SnapTargetKind.TaxiwayVertex, snapped.Kind);
        Assert.Equal(farther.Position, snapped.Position);
    }

    [Fact]
    public void AllKindsDisabled_ReturnsNoSnap()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        SnapOptions options = SnapOptions.Default with
        {
            TaxiwayVertices = false,
            RunwayVertices = false,
            Parking = false,
            Spot = false,
            Helipad = false,
        };

        Assert.Null(SnapEngine.Find(document, OffsetNorth(stand, 5.0), options, null, null));
    }

    [Fact]
    public void EqualDistance_TieBreaksByKind()
    {
        AirportMapDocument oak = Oak();
        MapFeature spot = oak.Features[Spot2Index];
        MapFeature taxiway = oak.Features[Taxiway199Index];
        MapPosition shared = spot.Coordinates[0];
        Assert.Equal(shared, taxiway.Coordinates[6]);

        var forward = new AirportMapDocument([spot, taxiway], oak.ForeignMembers);
        var reversed = new AirportMapDocument([taxiway, spot], oak.ForeignMembers);

        SnapResult first = Assert.IsType<SnapResult>(SnapEngine.Find(forward, shared, SnapOptions.Default, null, null));
        SnapResult second = Assert.IsType<SnapResult>(SnapEngine.Find(reversed, shared, SnapOptions.Default, null, null));

        Assert.Equal(SnapTargetKind.Spot, first.Kind);
        Assert.Equal(SnapTargetKind.Spot, second.Kind);
        Assert.Equal(shared, first.Position);
        Assert.Equal(shared, second.Position);
    }

    [Fact]
    public void EqualDistance_TieBreaksByFeatureOrder()
    {
        AirportMapDocument oak = Oak();
        MapFeature first = oak.Features[Taxiway200Index];
        MapPosition shared = first.Coordinates[23];
        Assert.Equal(shared, oak.Features[Taxiway201Index].Coordinates[12]);

        var forward = new AirportMapDocument([first, oak.Features[Taxiway201Index]], oak.ForeignMembers);
        var reversed = new AirportMapDocument([oak.Features[Taxiway201Index], first], oak.ForeignMembers);

        SnapResult forwardResult = Assert.IsType<SnapResult>(SnapEngine.Find(forward, shared, SnapOptions.Default, null, null));
        SnapResult reversedResult = Assert.IsType<SnapResult>(SnapEngine.Find(reversed, shared, SnapOptions.Default, null, null));

        Assert.Equal(0, forwardResult.FeatureIndex);
        Assert.Equal(0, reversedResult.FeatureIndex);
        Assert.Equal(shared, forwardResult.Position);
        Assert.Equal(shared, reversedResult.Position);
    }

    [Fact]
    public void DraggedVertex_IsNeverItsOwnTarget()
    {
        AirportMapDocument document = Oak();
        MapPosition dragged = document.Features[TaxiwayWIndex].Coordinates[0];

        SnapResult withoutExclusion = Assert.IsType<SnapResult>(SnapEngine.Find(document, dragged, SnapOptions.Default, null, null));
        Assert.Equal(TaxiwayWIndex, withoutExclusion.FeatureIndex);
        Assert.Equal(0, withoutExclusion.VertexIndex);
        Assert.Equal(0.0, withoutExclusion.DistanceFt, 6);

        SnapResult withExclusion = Assert.IsType<SnapResult>(
            SnapEngine.Find(document, dragged, SnapOptions.Default, new SnapExclusion(TaxiwayWIndex, 0), null)
        );
        Assert.False((withExclusion.FeatureIndex == TaxiwayWIndex) && (withExclusion.VertexIndex == 0));
    }

    [Fact]
    public void DraggedFeaturesOtherVertex_IsATarget()
    {
        AirportMapDocument document = Oak();
        MapPosition other = document.Features[TaxiwayWIndex].Coordinates[1];

        SnapResult snapped = Assert.IsType<SnapResult>(
            SnapEngine.Find(document, other, SnapOptions.Default, new SnapExclusion(TaxiwayWIndex, 0), null)
        );

        Assert.Equal(TaxiwayWIndex, snapped.FeatureIndex);
        Assert.Equal(1, snapped.VertexIndex);
        Assert.Equal(other, snapped.Position);
    }

    [Fact]
    public void ThresholdZero_NeverSnaps()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];

        Assert.Null(SnapEngine.Find(document, stand, SnapOptions.Default with { ThresholdFt = 0.0 }, null, null));
    }

    [Fact]
    public void EmptyDocument_ReturnsNoSnap()
    {
        var document = new AirportMapDocument([], []);
        MapPosition cursor = new(-122.2, 37.7);

        Assert.Null(SnapEngine.Find(document, cursor, SnapOptions.Default, null, null));
    }

    [Fact]
    public void ForeignGeometryFeature_IsNoTarget()
    {
        var document = AirportMapDocument.Read(ForeignGeometryMap);
        Assert.Null(document.Features[0].GeometryType);

        Assert.Null(SnapEngine.Find(document, new MapPosition(1.0, 2.0), SnapOptions.Default, null, null));
    }

    [Fact]
    public void NonFiniteCursor_ThrowsArgumentOutOfRange()
    {
        AirportMapDocument document = Oak();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapEngine.Find(document, new MapPosition(double.NaN, 37.7), SnapOptions.Default, null, null)
        );
    }

    [Fact]
    public void NonFiniteExtraTarget_ThrowsWhateverItsKind()
    {
        AirportMapDocument document = Oak();
        var extra = new SnapTarget(new MapPosition(double.NaN, 37.7), SnapTargetKind.Spot);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapEngine.Find(document, FarFromOak(document), SnapOptions.Default with { Spot = false }, null, [extra])
        );
    }

    [Fact]
    public void DisabledNearerKind_FartherEnabledDocumentTargetWins()
    {
        AirportMapDocument document = Oak();
        MapFeature spot = document.Features[Spot173Index];
        MapPosition cursor = spot.Coordinates[0];
        MapFeature taxiway = document.Features[Taxiway203Index];
        Assert.InRange(FeetBetween(cursor, taxiway.Coordinates[6]), 0.1, 5.0);

        SnapResult enabled = Assert.IsType<SnapResult>(SnapEngine.Find(document, cursor, SnapOptions.Default, null, null));
        SnapResult disabled = Assert.IsType<SnapResult>(SnapEngine.Find(document, cursor, SnapOptions.Default with { Spot = false }, null, null));

        Assert.Equal(SnapTargetKind.Spot, enabled.Kind);
        Assert.Equal(Spot173Index, enabled.FeatureIndex);
        Assert.Equal(SnapTargetKind.TaxiwayVertex, disabled.Kind);
        Assert.Equal(Taxiway203Index, disabled.FeatureIndex);
        Assert.Equal(6, disabled.VertexIndex);
        Assert.Equal(taxiway.Coordinates[6], disabled.Position);
    }

    [Fact]
    public void ExtraTarget_PassedByTheCaller_IsConsidered()
    {
        AirportMapDocument document = Oak();
        MapPosition cursor = FarFromOak(document);
        var extra = new SnapTarget(OffsetNorth(cursor, -3.0), SnapTargetKind.Spot);

        SnapResult snapped = Assert.IsType<SnapResult>(SnapEngine.Find(document, cursor, SnapOptions.Default, null, [extra]));

        Assert.Equal(SnapTargetKind.Spot, snapped.Kind);
        Assert.Equal(extra.Position, snapped.Position);
        Assert.Null(snapped.FeatureIndex);
        Assert.Null(snapped.VertexIndex);
    }

    [Fact]
    public void Oak_DefaultThreshold_SnapsAtFiveFeetAndNotAtFifteen()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];

        SnapResult snapped = Assert.IsType<SnapResult>(SnapEngine.Find(document, OffsetNorth(stand, 5.0), SnapOptions.Default, null, null));
        Assert.Equal(stand, snapped.Position);

        Assert.Null(SnapEngine.Find(document, OffsetNorth(stand, 15.0), SnapOptions.Default, null, null));
    }

    private const string ForeignGeometryMap = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "X" },
              "geometry": { "type": "Point", "coordinates": [1.0, 2.0, 0.0] }
            }
          ]
        }
        """;

    private static AirportMapDocument Oak() => AirportMapDocument.Read(File.ReadAllText(OakPath));

    // Half a degree north of the field: no OAK target is within a threshold of it.
    private static MapPosition FarFromOak(AirportMapDocument document) =>
        new(document.Features[Stand25Index].Coordinates[0].Lon, document.Features[Stand25Index].Coordinates[0].Lat + 0.5);

    private static MapPosition OffsetNorth(MapPosition position, double feet) => new(position.Lon, position.Lat + (feet / GeoMath.FeetPerNm / 60.0));

    private static double FeetBetween(MapPosition a, MapPosition b) => GeoMath.DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon) * GeoMath.FeetPerNm;
}
