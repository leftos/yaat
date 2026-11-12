using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Cover for the SFO two-letter connector taxiways curated in the sidecar: GL (G/L), AF (A/F),
/// CZ (C/Z) and ZS (S/Z). Each is a letter-only taxiway that joins its two taxiways, so
/// a clearance naming the pair adjacent must bridge through it without an unauthorized-taxiway
/// penalty and without a blind detour.
/// </summary>
public class SfoTwoLetterConnectorTests
{
    private readonly ITestOutputHelper _output;

    public SfoTwoLetterConnectorTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin shared NavData/sidecar singletons before layout build (CLAUDE.md singleton races).
        TestVnasData.EnsureInitialized();
    }

    private const string FixturePath = "TestData/sfo.geojson";

    private AirportGroundLayout? LoadLayout()
    {
        if (!File.Exists(FixturePath) || TestVnasData.NavigationDb is null)
        {
            return null;
        }

        return GeoJsonParser.Parse("SFO", File.ReadAllText(FixturePath), null, TestLayoutInputs.For("SFO", videoMap: null));
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    private static SearchContext CompileContext(AirportGroundLayout layout, int startNodeId, List<string> path)
    {
        return SearchContext.Compile(
            layout,
            startNodeId,
            path,
            null,
            null,
            null,
            null,
            null,
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large,
            null,
            null,
            null,
            null,
            startPose: null,
            aircraftLengthFt: AircraftLength.ResolveFt("B738")
        );
    }

    [Theory]
    [InlineData("GL", "G", "L")]
    [InlineData("AF", "A", "F")]
    [InlineData("CZ", "C", "Z")]
    [InlineData("ZS", "S", "Z")]
    public void Sfo_ProductionData_AuthorizesTwoLetterConnectorBetweenItsTaxiways(string connector, string p, string q)
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        // The curated connector must ship in production data — fail loud if missing or mispaired.
        IReadOnlyList<ImplicitConnectorEntry> connectors = NavigationDatabase.Instance.AirportSidecars.GetImplicitConnectors("SFO");
        Assert.Contains(
            connectors,
            c =>
                string.Equals(c.Connector, connector, StringComparison.OrdinalIgnoreCase)
                && (c.Between.Count == 2)
                && c.Between.Contains(p, StringComparer.OrdinalIgnoreCase)
                && c.Between.Contains(q, StringComparer.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("GL", "G", "L")]
    [InlineData("AF", "A", "F")]
    [InlineData("CZ", "C", "Z")]
    [InlineData("ZS", "S", "Z")]
    public void ClearanceNamingAdjacentTaxiways_AuthorizesConnector(string connector, string p, string q)
    {
        AirportGroundLayout? layout = LoadLayout();
        if (layout is null)
        {
            _output.WriteLine("sfo.geojson or navdata not found — skipping");
            return;
        }

        GroundNode? startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway(p)));
        Assert.NotNull(startNode);

        SearchContext adjacent = CompileContext(layout, startNode!.Id, [p, q]);
        Assert.NotNull(adjacent.AuthorizedTaxiways);
        Assert.Contains(connector, adjacent.AuthorizedTaxiways!);

        // A sequence that does not place the two taxiways adjacent does not authorize the connector.
        SearchContext separated = CompileContext(layout, startNode.Id, [p, "B", q]);
        Assert.NotNull(separated.AuthorizedTaxiways);
        Assert.DoesNotContain(connector, separated.AuthorizedTaxiways!);
    }

    // GL and ZS carry no traversal row: the layout already joins G to L directly at node 322 and S to
    // Z at node 639, so the resolver keeps the shorter direct junction and never needs the connector.
    [Theory]
    [InlineData("GL", "G", "L", false)]
    [InlineData("AF", "A", "F", true)]
    [InlineData("CZ", "C", "Z", true)]
    [InlineData("ZS", "S", "Z", false)]
    public void ClearanceNamingAdjacentTaxiways_ResolvesWithoutDetour(string connector, string p, string q, bool traversesConnector)
    {
        AirportGroundLayout? layout = LoadLayout();
        if (layout is null)
        {
            _output.WriteLine("sfo.geojson or navdata not found — skipping");
            return;
        }

        GroundNode? startNode = layout.Nodes.Values.FirstOrDefault(n => n.Edges.Any(e => e.MatchesTaxiway(p)));
        Assert.NotNull(startNode);

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            [p, q],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = null,
                ExplicitHoldShorts = null,
                StartHeadingTrue = null,
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            new TaxiClass(AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large)
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route!.Segments.Select(s => s.TaxiwayName)));

        if (traversesConnector)
        {
            Assert.True(Traverses(route, connector), $"route must taxi via the real {p}↔{q} connector {connector}");
        }

        // The clearance threads the curated connector, so no blind-detour insertion was needed.
        Assert.Equal(0, route.MandatoryConnectorCount);
    }
}
