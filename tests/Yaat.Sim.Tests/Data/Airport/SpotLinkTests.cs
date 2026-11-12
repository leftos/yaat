using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// Every taxi spot is a reachable node of the taxi graph: a spot no taxiway vertex snapped onto is linked by the airport
/// sidecar's <c>spotEdges</c> chains, not when the layout is parsed, so a <c>TAXI $n</c> to it has a route once its sidecar
/// carries them.
/// </summary>
public class SpotLinkTests
{
    /// <summary>The MIA stand the spot route starts from (the south D-gate of <c>Issue192MiaGateEntryTests</c>).</summary>
    private const string MiaParking = "D32";

    /// <summary>
    /// Spots left without an edge on a committed layout, as <c>airport $spot</c> with the airport as its TestData file name
    /// spells it. An airport's entries leave this list when its sidecar's <c>spotEdges</c> chains link them.
    /// </summary>
    private static readonly string[] KnownUnlinkedSpots =
    [
        "ABQ $G1",
        "ABQ $G2",
        "ABQ $G3",
        "ABQ $M2",
        "ABQ $M3",
        "BNA $1",
        "BNA $4",
        "BOS $1",
        "BOS $3",
        "BOS $4",
        "BOS $5",
        "BOS $6",
        "BOS $7",
        "BOS $B",
        "BOS $B1",
        "BOS $B10",
        "BOS $B11",
        "BOS $B13",
        "BOS $B14",
        "BOS $B15",
        "BOS $B2",
        "BOS $B3",
        "BOS $B4",
        "BOS $B5",
        "BOS $B6",
        "BOS $B8",
        "BOS $B9",
        "BOS $C10",
        "BOS $C12",
        "BOS $C13",
        "BOS $C14",
        "BOS $C2",
        "BOS $C3",
        "BOS $C4",
        "BOS $C5",
        "BOS $C6",
        "BOS $C7",
        "BOS $C8",
        "BOS $C8A",
        "BOS $C9",
        "BOS $E",
        "BOS $E1",
        "BOS $E2",
        "BOS $E3",
        "BOS $E4",
        "BOS $EA",
        "BOS $EB",
        "CLT $32",
        "CVG $15",
        "CVG $20",
        "CVG $51",
        "CVG $52",
        "CVG $54",
        "CVG $55",
        "CVG $56",
        "CVG $57",
        "CVG $58",
        "CVG $59",
        "CVG $71",
        "CVG $72",
        "CVG $73",
        "DCA $1",
        "DCA $10",
        "DCA $11",
        "DCA $2",
        "DCA $25",
        "DCA $26",
        "DCA $27",
        "DCA $3",
        "DCA $4",
        "DCA $5",
        "DCA $6",
        "DCA $7",
        "DCA $8",
        "DCA $9",
        "DFW $1",
        "DFW $10",
        "DFW $103",
        "DFW $104",
        "DFW $106",
        "DFW $107",
        "DFW $108",
        "DFW $109",
        "DFW $11",
        "DFW $110",
        "DFW $111",
        "DFW $112",
        "DFW $113",
        "DFW $114",
        "DFW $115",
        "DFW $116",
        "DFW $117",
        "DFW $118",
        "DFW $12",
        "DFW $122",
        "DFW $123",
        "DFW $124",
        "DFW $13",
        "DFW $131",
        "DFW $132",
        "DFW $133",
        "DFW $134",
        "DFW $135",
        "DFW $136",
        "DFW $137",
        "DFW $138",
        "DFW $14",
        "DFW $142",
        "DFW $143",
        "DFW $144",
        "DFW $145",
        "DFW $15",
        "DFW $16",
        "DFW $17",
        "DFW $1W",
        "DFW $2",
        "DFW $22",
        "DFW $23",
        "DFW $24",
        "DFW $25",
        "DFW $26",
        "DFW $27",
        "DFW $28",
        "DFW $29",
        "DFW $3",
        "DFW $30",
        "DFW $31",
        "DFW $32",
        "DFW $33",
        "DFW $34",
        "DFW $35",
        "DFW $36",
        "DFW $37",
        "DFW $38",
        "DFW $39",
        "DFW $42",
        "DFW $43",
        "DFW $44",
        "DFW $45",
        "DFW $46",
        "DFW $47",
        "DFW $48",
        "DFW $49",
        "DFW $5",
        "DFW $50",
        "DFW $51",
        "DFW $52",
        "DFW $53",
        "DFW $54",
        "DFW $55",
        "DFW $6",
        "DFW $7",
        "DFW $8",
        "DFW $9",
        "DTW $J1",
        "DTW $U2",
        "IAD $70",
        "IAD $71",
        "IAD $72",
        "IAD $73",
        "IAD $78",
        "IAD $79",
        "IAD $80",
        "IAD $81",
        "IAD $82",
        "IAD $83",
        "IAD $86",
        "IAD $87",
        "iah $11",
        "iah $12",
        "iah $14",
        "iah $15",
        "iah $15L1",
        "iah $15L2",
        "iah $15L3",
        "iah $15L4",
        "iah $15R1",
        "iah $15R2",
        "iah $15R3",
        "iah $15R4",
        "iah $15R5",
        "iah $17",
        "iah $18",
        "iah $19",
        "iah $4",
        "iah $8",
        "issue172-sfo $20",
        "issue172-sfo $30",
        "ITO $H1",
        "KOA $H1",
        "KOA $H2",
        "LAS $1",
        "LAS $10",
        "LAS $14",
        "LAS $15",
        "LAS $16",
        "LAS $17",
        "LAS $19",
        "LAS $1B",
        "LAS $2",
        "LAS $21",
        "LAS $3",
        "LAS $4",
        "LAS $40",
        "LAS $5",
        "LAS $50",
        "LAS $7A",
        "LAS $8",
        "LAS $8A",
        "LAS $8B",
        "PHL $LAPPN",
        "PHL $LAPPS",
        "PHL $SPOT1",
        "PHL $SPOT10",
        "PHL $SPOT11",
        "PHL $SPOT12",
        "PHL $SPOT14",
        "PHL $SPOT15",
        "PHL $SPOT3",
        "PHL $SPOT4",
        "PHL $SPOT5",
        "PHL $SPOT6",
        "PHL $SPOT7",
        "PHL $SPOT8",
        "PHL $SPOT9",
        "PHX $I2",
        "PHX $I3",
        "PHX $J2",
        "PHX $K1",
        "PHX $K2",
        "PHX $L1",
        "PHX $L2",
        "PHX $M1",
        "PHX $M2",
        "PHX $N1",
        "PHX $N2",
        "PHX $P1",
        "PHX $P2",
        "PHX $Y2",
        "PHX $Y3",
        "PHX $Y4",
        "PRC $HNGR",
        "RDU $1",
        "RDU $12",
        "RDU $2",
        "RDU $6",
        "RDU $67",
        "RDU $7",
        "SAN $3",
        "SAN $4",
        "SAN $5",
        "SAN $7",
        "SAN $8",
        "sfo $20",
        "sfo $30",
        "sfo-b1short $20",
        "sfo-b1short $30",
        "SLC $10",
        "SLC $12",
        "SLC $14",
        "SLC $16",
        "SLC $2ES",
        "SLC $3E",
        "SLC $97",
        "TUS $M",
    ];

    [Fact]
    public void EveryCommittedLayout_EverySpotHasAnEdge()
    {
        TestVnasData.EnsureInitialized();
        var unlinked = new List<string>();
        foreach (string path in Directory.EnumerateFiles("TestData", "*.geojson").Order(StringComparer.Ordinal))
        {
            string airport = Path.GetFileNameWithoutExtension(path);
            AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
            Assert.NotNull(layout);

            unlinked.AddRange(
                layout
                    .Nodes.Values.Where(node => (node.Type == GroundNodeType.Spot) && (node.Edges.Count == 0))
                    .Select(node => $"{airport} ${node.Name}")
            );
        }

        HashSet<string> expected = KnownUnlinkedSpots.ToHashSet(StringComparer.Ordinal);
        List<string> unexpected = [.. unlinked.Where(line => !expected.Contains(line)).Order(StringComparer.Ordinal)];
        List<string> gone = [.. KnownUnlinkedSpots.Where(line => !unlinked.Contains(line, StringComparer.Ordinal)).Order(StringComparer.Ordinal)];
        string nl = Environment.NewLine;
        Assert.True(
            (unexpected.Count == 0) && (gone.Count == 0),
            $"Unlinked spots:{nl}Unexpected:{nl}{string.Join(nl, unexpected)}{nl}No longer seen:{nl}{string.Join(nl, gone)}"
        );
    }

    /// <summary>
    /// A taxi from a MIA stand reaches spot 9 over the <c>spotEdges</c> chains <c>ZMA/Airports/mia.json</c> carries.
    /// </summary>
    [Fact]
    public void Mia_RouteFromAStandToSpot9_Exists()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("MIA");
        Assert.NotNull(layout);
        GroundNode? parking = layout.FindParkingByName(MiaParking);
        GroundNode? spot = layout.FindSpotNodeByName("9");
        Assert.NotNull(parking);
        Assert.NotNull(spot);

        TaxiRoute? route = TaxiPathfinder.FindRoute(layout, parking.Id, spot.Id, AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Large);

        Assert.NotNull(route);
    }

    /// <summary>
    /// The shipped <c>ZMA/Airports/mia.json</c> loads its 22 <c>spotEdges</c> chains without a warning, and the MIA layout
    /// build resolves every one of them into its 45 edges (spots 1-27 and 52). The edges are counted on the layout built
    /// without fillets, since the fillet pass re-mints the edges it cuts at a junction under its own origin.
    /// </summary>
    [Fact]
    public void Mia_ShippedSpotEdges_LoadAndBuildEveryChain()
    {
        TestVnasData.EnsureInitialized();
        AirportSidecarLoadResult loaded = AirportSidecarLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs"));
        Assert.DoesNotContain(loaded.Warnings, warning => warning.Replace('\\', '/').Contains("ZMA/Airports/mia.json", StringComparison.Ordinal));
        Assert.Equal(22, NavigationDatabase.Instance.AirportSidecars.GetSpotEdges("MIA").Count);

        var capture = WarningLogCapture.Install();
        AirportGroundLayout? layout = TestAirportGroundData.LoadUncached("MIA");

        Assert.NotNull(layout);
        Assert.DoesNotContain(capture.Warnings, warning => warning.Contains("sidecar spotEdges entry", StringComparison.Ordinal));
        AirportGroundLayout? unfilleted = new TestAirportGroundData(FilletMode.None).GetLayout("MIA");
        Assert.NotNull(unfilleted);
        Assert.Equal(45, unfilleted.Edges.Count(edge => edge.Origin == SpotEdgeBuilder.Origin));
    }
}
