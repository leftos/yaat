using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The layout build marks every hold-short node of a runway in the layout as that runway's hold line or a short fallback
/// bar inside its holding distance (<see cref="GroundNode.IsShortBar"/>), by the same cross-track measurement the runtime
/// used before the flag existed, so every reader keeps today's answer.
/// </summary>
public class HoldShortBarFlagTests
{
    public HoldShortBarFlagTests() => TestVnasData.EnsureInitialized();

    /// <summary>
    /// The airport's layout with every bar where the parse's rules seat it: OAK parsed without its tower-cab map, since a bar
    /// the map snaps is a hold line by paint rather than by cross-track (<see cref="ASnappedBar_IsAHoldLine_AndEveryOtherBarMatchesTheCrossTrack"/>);
    /// every other airport from the shared harness, which has no map for it.
    /// </summary>
    private static AirportGroundLayout? UnsnappedLayout(string airportId) =>
        (airportId == "OAK") ? RunwayPaintSnapTests.ParseOak(map: null, FilletMode.Standard) : new TestAirportGroundData().GetLayout(airportId);

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    [InlineData("ATL")]
    [InlineData("MIA")]
    [InlineData("COS")]
    public void EveryRunwayBar_IsMarked_AndNoOtherNodeIs(string airportId)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);

        foreach (GroundNode node in layout.Nodes.Values)
        {
            bool isBarOfALayoutRunway = (node.Type == GroundNodeType.RunwayHoldShort) && (FindRunway(layout, node.RunwayId) is not null);
            if (isBarOfALayoutRunway)
            {
                Assert.True(node.IsShortBar is not null, $"{airportId} bar #{node.Id} ({node.RunwayId}) is unmarked");
            }
            else
            {
                Assert.True(node.IsShortBar is null, $"{airportId} node #{node.Id} ({node.Type}) carries a short-bar flag");
            }
        }
    }

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    [InlineData("ATL")]
    [InlineData("MIA")]
    [InlineData("COS")]
    public void TheFlag_MatchesTheCrossTrackMeasurement(string airportId)
    {
        AirportGroundLayout? layout = UnsnappedLayout(airportId);
        Assert.NotNull(layout);

        int marked = 0;
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (node.IsShortBar is not { } isShort)
            {
                continue;
            }

            GroundRunway? runway = FindRunway(layout, node.RunwayId);
            Assert.NotNull(runway);
            RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
            Assert.Equal(!isShort, AirportGroundLayout.IsAtHoldingDistanceByCrossTrack(rect, node));
            marked++;
        }

        Assert.True(marked > 0, $"{airportId} has no marked hold-short node");
    }

    [Fact]
    public void Atl_KeepsShortBars()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("ATL");
        Assert.NotNull(layout);

        GroundNode[] shortBars = [.. layout.Nodes.Values.Where(node => node.IsShortBar == true)];
        Assert.NotEmpty(shortBars);
    }

    [Fact]
    public void TheFlag_SurvivesSerialization()
    {
        AirportGroundLayout? shared = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(shared);

        AirportGroundLayout copy = Copy(shared);
        Assert.Equal(shared.Nodes.Count, copy.Nodes.Count);
        foreach (GroundNode node in shared.Nodes.Values)
        {
            Assert.True(copy.Nodes.TryGetValue(node.Id, out GroundNode? read), $"node #{node.Id} is missing from the copy");
            Assert.Equal(node.IsShortBar, read.IsShortBar);
        }
    }

    [Fact]
    public void TheFlag_DecidesIsAtRunwayHoldingDistance()
    {
        AirportGroundLayout? shared = UnsnappedLayout("OAK");
        Assert.NotNull(shared);

        AirportGroundLayout copy = Copy(shared);
        GroundNode? bar = copy
            .Nodes.Values.Where(node =>
                (node.Type == GroundNodeType.RunwayHoldShort) && (node.IsShortBar == false) && (node.RunwayId is { } id) && id.Contains("28R")
            )
            .OrderBy(node => node.Id)
            .FirstOrDefault();
        Assert.NotNull(bar);

        Assert.True(copy.IsAtRunwayHoldingDistance(bar, "28R"));
        bar.IsShortBar = true;
        Assert.False(copy.IsAtRunwayHoldingDistance(bar, "28R"));

        GroundRunway? other = copy.FindRunway("30");
        Assert.NotNull(other);
        Assert.Equal(
            AirportGroundLayout.IsAtHoldingDistanceByCrossTrack(RunwayCrossingDetector.BuildRunwayRectangle(other), bar),
            copy.IsAtRunwayHoldingDistance(bar, "30")
        );

        bar.IsShortBar = null;
        Assert.True(copy.IsAtRunwayHoldingDistance(bar, "28R"));
    }

    [Fact]
    public void AnUnmarkedLayout_MeasuresAsBefore()
    {
        AirportGroundLayout? shared = new TestAirportGroundData().GetLayout("ATL");
        Assert.NotNull(shared);

        AirportGroundLayout copy = Copy(shared);
        foreach (GroundNode node in copy.Nodes.Values)
        {
            node.IsShortBar = null;
        }

        int measured = 0;
        foreach (GroundNode node in copy.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.RunwayHoldShort) || (node.RunwayId is not { } id) || (FindRunway(copy, id) is null))
            {
                continue;
            }

            bool originalFlag =
                shared.Nodes[node.Id].IsShortBar ?? throw new Xunit.Sdk.XunitException($"ATL bar #{node.Id} is unmarked in the shared layout");

            Assert.Equal(!originalFlag, copy.IsAtRunwayHoldingDistance(node, id.End1));
            measured++;
        }

        Assert.True(measured > 0, "ATL has no hold-short node of a layout runway");
    }

    [Fact]
    public void ASnappedBar_IsAHoldLine_AndEveryOtherBarMatchesTheCrossTrack()
    {
        string layoutPath = Path.Combine("TestData", "oak.geojson");
        string mapPath = Path.Combine("TestData", "TowerCabMaps", "oak.geojson");
        var map = TowerCabVideoMap.Read(File.ReadAllText(mapPath));
        AirportGroundLayout layout = GeoJsonParser.Parse(
            "oak",
            File.ReadAllText(layoutPath),
            "OAK",
            new LayoutInputs(TestStandLeadIns.For("oak"), map),
            FilletMode.Standard
        );

        int snapped = 0;
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node.IsShortBar is not { } isShort) || (FindRunway(layout, node.RunwayId) is not { } runway))
            {
                continue;
            }

            if (node.PaintSegmentIndex is not null)
            {
                Assert.False(isShort, $"snapped OAK bar #{node.Id} ({node.RunwayId}) is marked a short bar");
                snapped++;
                continue;
            }

            Assert.Equal(!isShort, AirportGroundLayout.IsAtHoldingDistanceByCrossTrack(RunwayCrossingDetector.BuildRunwayRectangle(runway), node));
        }

        Assert.True(snapped > 0, "no OAK bar was seated on paint");
    }

    /// <summary>
    /// The layout runway whose full identifier is <paramref name="id"/>, as the build and the readers' guard look it up, or
    /// null when the layout has no such runway.
    /// </summary>
    private static GroundRunway? FindRunway(AirportGroundLayout layout, RunwayIdentifier? id) =>
        (id is { } wanted) ? layout.Runways.FirstOrDefault(candidate => candidate.Id.Equals(wanted)) : null;

    /// <summary>A private, deserialized copy of <paramref name="layout"/>: the shared test layouts are never mutated.</summary>
    private static AirportGroundLayout Copy(AirportGroundLayout layout) =>
        GroundLayoutSerializer.Deserialize(new MemoryStream(GroundLayoutSerializer.Serialize(layout)), layout.AirportId);
}
