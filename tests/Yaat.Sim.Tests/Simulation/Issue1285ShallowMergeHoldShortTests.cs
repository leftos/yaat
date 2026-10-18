using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub #1285 / YAAT-800: at OAK, D merges into C at ~5.7°. A C172 on <c>TAXI D C B 28R</c> told <c>HS C</c> stopped
/// with its centre ~13 ft from C's centreline (bundle "S1-OAK-P (A) _ S1 Rating Practical Exam", N45912). At a shallow
/// merge C's west leg runs alongside D, so the nose has to stand off C's own centreline (AIM 2-3-5.b.3), but the G
/// intersection 70 ft back caps the walk-back long before the 132 ft wingtip floor. The aircraft then holds short of that
/// whole intersection instead, its nose at the intersection's near edge, says so in the readback and the hold report, and
/// the route tells the instructor the floor is still not met. An aircraft already past that edge keeps the capped stop.
/// </summary>
public sealed class Issue1285ShallowMergeHoldShortTests
{
    private const string Callsign = "N45912";
    private const string MergeAdvisoryPrefix = "holding short of TWY C before TWY G (D merges into C) — wingtip clearance from C not assured (";
    private const string WingtipWarningPrefixC = "holding short of TWY C — wingtip clearance from ";

    /// <summary>How far back along D, from the D/C merge node, the C172 starts: behind the whole G intersection.</summary>
    private const double StartBackFt = 300.0;

    /// <summary>How far past the G intersection's near edge (ft, centre) the rolling aircraft is when <c>HS C</c> comes.</summary>
    private const double PastNearEdgeFt = 20.0;

    /// <summary>
    /// How close to the merge (ft, centre) the rolling C172 is when the hold-short comes in the braking-branch tests: short of
    /// the merge stop (centre ~215 ft back), but within the stop it can fly at the firm rate from the speed it has gathered.
    /// </summary>
    private const double InsideBrakingStopFt = 240.0;

    private readonly ITestOutputHelper _output;

    public Issue1285ShallowMergeHoldShortTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiDCB28RHsC_ShallowMergeCappedByG_HoldsWithTheNoseAtGsNearEdgeAndSaysSo()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState ac, GroundNode merge) = world;
        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == Callsign)
            {
                warnings.Add(warning);
            }
        };
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        CommandResult hs = engine.SendCommand(Callsign, "HS C");
        Assert.True(hs.Success, hs.Message);
        PilotSpeechText readback = Readback(ac, "HS C");

        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        Assert.Equal(merge.Id, bar.NodeId);
        LogWarnings(route);

        double halfLengthFt = FaaAircraftDatabase.Get("C172")!.LengthFt!.Value / 2.0;
        var stop = new LatLon(bar.Latitude!.Value, bar.Longitude!.Value);
        LatLon nose = AlongRouteFrom(route, stop, halfLengthFt);
        GroundNode nearEdge = NearEdgeOfG(merge);
        double noseOffEdgeFt = GeoMath.DistanceNm(nose, nearEdge.Position) * GeoMath.FeetPerNm;
        double noseToCFt = SfoGroundHarness.DistanceToTaxiwayFt(layout, "C", nose);
        _output.WriteLine($"planned nose {noseOffEdgeFt:F1} ft from G's near edge (node {nearEdge.Id}), {noseToCFt:F1} ft from C's centreline");
        Assert.True(noseOffEdgeFt <= 5.0, $"the planned nose is {noseOffEdgeFt:F1} ft from the G intersection's near edge");

        Assert.Equal("hold short of C, will hold on D before G", readback.Terminal);
        Assert.StartsWith("hold short of charlie, will hold on delta before golf", readback.Tts, StringComparison.Ordinal);

        Assert.DoesNotContain(route.Warnings, w => w.StartsWith(WingtipWarningPrefixC, StringComparison.Ordinal));
        string advisory = Assert.Single(route.Warnings, w => w.StartsWith(MergeAdvisoryPrefix, StringComparison.Ordinal));
        Assert.True(Math.Abs(ReportedFt(advisory) - noseToCFt) <= 1.0, $"the advisory reports {advisory}; the nose is {noseToCFt:F1} ft from C");
        Assert.Equal($"{MergeAdvisoryPrefix}61 ft)", advisory);

        int settled = SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null);
        Assert.True(settled > 0, $"{Callsign} never came to rest holding short of C");
        double offStopFt = GeoMath.DistanceNm(ac.Position, stop) * GeoMath.FeetPerNm;
        Assert.True(offStopFt <= 10.0, $"{Callsign} held {offStopFt:F0} ft from its planned HS C stop");
        Assert.Contains($"{Callsign} holding short of C on D before G", warnings);
    }

    [Fact]
    public void TaxiDCB28RHsC_MergeHold_HoldReportIsSpokenOnDBeforeG()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, _) = world;
        engine.Scenario!.RpoShowPilotSpeech = true;
        var speech = new List<string>();
        engine.PilotSpeechEmitted += (callsign, text) =>
        {
            if (callsign == Callsign)
            {
                speech.Add(text);
            }
        };
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        Assert.True(engine.SendCommand(Callsign, "HS C").Success);

        int settled = SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null);
        Assert.True(settled > 0, $"{Callsign} never came to rest holding short of C");
        Assert.Contains(speech, s => s.EndsWith(", holding short of charlie on delta before golf.", StringComparison.Ordinal));
    }

    [Fact]
    public void TaxiDCB28RThenHsC_InsideTheBrakingStopOfGsNearEdge_DeclinesTheMergeHoldAndKeepsItThroughARecompute()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, GroundNode merge) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        RollUntilWithin(engine, ac, merge, InsideBrakingStopFt);
        GroundNode nearEdge = NearEdgeOfG(merge);
        double noseToEdgeFt = (DistanceFt(ac.Position, merge.Position) - DistanceFt(nearEdge.Position, merge.Position)) - HalfLengthFt;
        _output.WriteLine($"HS C issued at {ac.GroundSpeed:F1} kt with the centre {noseToEdgeFt:F0} ft short of where the merge stop would put it");
        Assert.True(noseToEdgeFt > 0.0, "the aircraft is already past the merge stop; this test is for the braking branch");

        CommandResult hs = engine.SendCommand(Callsign, "HS C");
        Assert.Equal("Hold short of C", hs.Message);
        Assert.Equal("hold short of C", Readback(ac, "HS C").Terminal);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        Assert.True(bar.MergeHoldDeclined);
        Assert.Null(bar.MergeHold);
        Assert.False(bar.Unable);
        Assert.Single(route.Warnings, w => w.StartsWith($"{WingtipWarningPrefixC}C not assured (", StringComparison.Ordinal));
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("holding short of TWY C before TWY ", StringComparison.Ordinal));
        (double lat, double lon) = (bar.Latitude!.Value, bar.Longitude!.Value);

        Assert.True(engine.SendCommand(Callsign, "HS B").Success);
        Assert.True(bar.MergeHoldDeclined);
        Assert.Null(bar.MergeHold);
        Assert.Equal(lat, bar.Latitude!.Value);
        Assert.Equal(lon, bar.Longitude!.Value);
    }

    [Fact]
    public void HsCAgain_AfterAnEarlierDecline_JudgesTheMergeHoldAfresh()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, _) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        Assert.True(engine.SendCommand(Callsign, "HS C").Success);
        HoldShortPoint bar = Assert.Single(ac.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.TargetName == "C");
        Assert.NotNull(bar.MergeHold);

        // Stands in for an HS C declined while the aircraft rolled: the aircraft is at rest well short of the G intersection now.
        bar.MergeHoldDeclined = true;
        Assert.True(engine.SendCommand(Callsign, "HS C").Success);

        Assert.False(bar.MergeHoldDeclined);
        Assert.Equal(new MergeHold("D", "G"), bar.MergeHold);
        Assert.Equal("hold short of C, will hold on D before G", Readback(ac, "HS C").Terminal);
    }

    [Fact]
    public void TaxiDCB28RHsC_SentRollingInsideTheBrakingStopOfGsNearEdge_KeepsTheCappedStopAndPlainClause()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, GroundNode merge) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        RollUntilWithin(engine, ac, merge, InsideBrakingStopFt);
        _output.WriteLine($"TAXI … HS C issued at {ac.GroundSpeed:F1} kt, {DistanceFt(ac.Position, merge.Position):F0} ft from the merge");

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI D C B 28R HS C");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        Assert.True(bar.MergeHoldDeclined);
        Assert.Null(bar.MergeHold);
        Assert.Single(route.Warnings, w => w.StartsWith($"{WingtipWarningPrefixC}C not assured (", StringComparison.Ordinal));
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("holding short of TWY C before TWY ", StringComparison.Ordinal));
        string readback = Readback(ac, "TAXI D C B 28R HS C").Terminal;
        Assert.EndsWith("hold short of C", readback, StringComparison.Ordinal);
    }

    [Fact]
    public void TaxiDCB28RWithHsC_FromRest_ReadsBackTheMergeClauseInTheTaxiReadback()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, _) = world;
        const string Taxi = "TAXI D C B 28R HS C";
        string plain = Readback(ac, Taxi).Terminal;
        Assert.Contains("hold short of C", plain, StringComparison.Ordinal);

        Assert.True(engine.SendCommand(Callsign, Taxi).Success);

        PilotSpeechText readback = Readback(ac, Taxi);
        Assert.Equal(plain.Replace("hold short of C", "hold short of C, will hold on D before G", StringComparison.Ordinal), readback.Terminal);
        Assert.Contains("hold short of charlie, will hold on delta before golf", readback.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void ResHsC_FromAHold_ReadsBackTheMergeClause()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, _) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        Assert.True(engine.SendCommand(Callsign, "HOLD").Success);

        CommandResult res = engine.SendCommand(Callsign, "RES HS C");
        Assert.True(res.Success, res.Message);

        Assert.Equal(new MergeHold("D", "G"), Assert.Single(ac.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.TargetName == "C").MergeHold);
        PilotSpeechText readback = Readback(ac, "RES HS C");
        Assert.Equal("resume taxi, hold short of C, will hold on D before G", readback.Terminal);
        Assert.StartsWith("resume taxi, hold short of charlie, will hold on delta before golf", readback.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossWithHsC_OnARouteWithTheMergeHold_ReadsBackTheMergeClause()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, _, AircraftState ac, _) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        Assert.True(engine.SendCommand(Callsign, "HS C").Success);

        PilotSpeechText readback = Readback(ac, "CROSS 28R HS C");
        Assert.Equal("cross runway 28R, hold short of C, will hold on D before G", readback.Terminal);
        Assert.Contains("hold short of charlie, will hold on delta before golf", readback.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeHoldReports_ReadyToTaxiAndRouteIncomplete_NameTheHoldOnDBeforeG()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        AircraftState ac = world.Aircraft;
        var merge = new MergeHold("D", "G");

        PilotSpeechText incomplete = PilotResponder.BuildHoldingShortBeforeTaxiway(ac, "C", merge, requestFurtherTaxi: true);
        Assert.Equal("holding short of C on D before G, request further taxi.", incomplete.Terminal);
        Assert.EndsWith(", holding short of charlie on delta before golf, request further taxi.", incomplete.Tts, StringComparison.Ordinal);

        ReadyToTaxiLocation location = ReadyToTaxiLocation.HoldingShort("C", "D") with { MergeHold = merge };
        PilotSpeechText ready = PilotResponder.BuildReadyToTaxi(ac, "ground", "A", location);
        Assert.StartsWith("ground, holding short of C on D before G, with information Alpha", ready.Terminal, StringComparison.Ordinal);
        Assert.Contains(" holding short of charlie on delta before golf, with information Alpha", ready.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void Unable_DropsTheMergeHold()
    {
        var bar = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "C",
            MergeHold = new MergeHold("D", "G"),
            Unable = true,
        };

        Assert.Null(bar.MergeHold);
    }

    [Fact]
    public void TaxiRoute_RoundTrip_KeepsAPlacedMergeHoldAndADeclinedOne()
    {
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode merge = FindShallowMerge(layout);
        GroundEdge d = merge.Edges.OfType<GroundEdge>().Single(e => e.TaxiwayName == "D");
        GroundNode back = d.OtherNode(merge);
        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "D", Edge = d.Directed(back, merge) }],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = merge.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "C",
                    MergeHold = new MergeHold("D", "G"),
                },
                new HoldShortPoint
                {
                    NodeId = back.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                    MergeHoldDeclined = true,
                },
            ],
        };

        string json = JsonSerializer.Serialize(route.ToSnapshot());
        TaxiRouteDto dto = JsonSerializer.Deserialize<TaxiRouteDto>(json) ?? throw new InvalidOperationException("the snapshot did not deserialize");
        TaxiRoute restored = TaxiRoute.FromSnapshot(dto, layout) ?? throw new InvalidOperationException("the route did not restore");
        string plainBar = JsonSerializer.Serialize(
            new HoldShortPointDto
            {
                NodeId = back.Id,
                RunwayId = "B",
                IsSatisfied = false,
            }
        );
        Assert.DoesNotContain("MergeHold", plainBar, StringComparison.Ordinal);
        HoldShortPoint placed = restored.HoldShortPoints.Single(h => h.TargetName == "C");
        HoldShortPoint declined = restored.HoldShortPoints.Single(h => h.TargetName == "B");
        Assert.Equal(new MergeHold("D", "G"), placed.MergeHold);
        Assert.False(placed.MergeHoldDeclined);
        Assert.Null(declined.MergeHold);
        Assert.True(declined.MergeHoldDeclined);
    }

    [Fact]
    public void TaxiDCB28RThenHsC_AlreadyPastGsNearEdge_KeepsTheCappedStopWarningAndPlainReadback()
    {
        if (SpawnOnDShortOfTheMerge() is not { } world)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout, AircraftState ac, GroundNode merge) = world;
        Assert.True(engine.SendCommand(Callsign, "TAXI D C B 28R").Success);
        GroundNode nearEdge = NearEdgeOfG(merge);
        double passFt = (GeoMath.DistanceNm(nearEdge.Position, merge.Position) * GeoMath.FeetPerNm) - PastNearEdgeFt;
        int rolled = SfoGroundHarness.TickUntil(
            engine,
            () => (GeoMath.DistanceNm(ac.Position, merge.Position) * GeoMath.FeetPerNm) < passFt,
            120,
            null
        );
        Assert.True(rolled > 0, $"{Callsign} never rolled past the G intersection's near edge");
        _output.WriteLine(
            $"HS C issued at {ac.GroundSpeed:F1} kt, {GeoMath.DistanceNm(ac.Position, merge.Position) * GeoMath.FeetPerNm:F0} ft from the merge"
        );

        CommandResult hs = engine.SendCommand(Callsign, "HS C");
        Assert.True(hs.Success, hs.Message);
        Assert.Equal("Hold short of C", hs.Message);
        PilotSpeechText readback = Readback(ac, "HS C");
        Assert.Equal("hold short of C", readback.Terminal);

        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        LogWarnings(route);
        double halfLengthFt = FaaAircraftDatabase.Get("C172")!.LengthFt!.Value / 2.0;
        var stop = new LatLon(bar.Latitude!.Value, bar.Longitude!.Value);
        double noseToCFt = SfoGroundHarness.DistanceToTaxiwayFt(layout, "C", AlongRouteFrom(route, stop, halfLengthFt));
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("holding short of TWY C before TWY ", StringComparison.Ordinal));
        string warning = Assert.Single(route.Warnings, w => w.StartsWith(WingtipWarningPrefixC, StringComparison.Ordinal));
        Assert.StartsWith($"{WingtipWarningPrefixC}C not assured (", warning, StringComparison.Ordinal);
        Assert.True(Math.Abs(ReportedFt(warning) - noseToCFt) <= 1.0, $"the warning reports {warning}; the nose is {noseToCFt:F1} ft from C");

        int settled = SfoGroundHarness.TickUntil(engine, () => IsHoldingShortAtRest(ac), 180, null);
        Assert.True(settled > 0, $"{Callsign} never came to rest holding short of C");
        double offStopFt = GeoMath.DistanceNm(ac.Position, stop) * GeoMath.FeetPerNm;
        Assert.True(offStopFt <= 10.0, $"{Callsign} held {offStopFt:F0} ft from its planned HS C stop");
    }

    private static double HalfLengthFt => FaaAircraftDatabase.Get("C172")!.LengthFt!.Value / 2.0;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Ticks until the taxiing aircraft is within <paramref name="ft"/> of the merge node.</summary>
    private static void RollUntilWithin(SimulationEngine engine, AircraftState ac, GroundNode merge, double ft)
    {
        int rolled = SfoGroundHarness.TickUntil(engine, () => DistanceFt(ac.Position, merge.Position) < ft, 120, null);
        Assert.True(rolled > 0, $"{Callsign} never rolled within {ft:F0} ft of the merge");
    }

    private void LogWarnings(TaxiRoute route)
    {
        foreach (string w in route.Warnings)
        {
            _output.WriteLine($"warning: {w}");
        }
    }

    private static PilotSpeechText Readback(AircraftState ac, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, text);
        return PilotResponder.BuildReadback(parsed.Value!, ac) ?? throw new InvalidOperationException($"no readback for {text}");
    }

    /// <summary>The figure in a warning ending "(N ft)".</summary>
    private static int ReportedFt(string warning) =>
        int.Parse(warning[(warning.LastIndexOf('(') + 1)..^" ft)".Length], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The point <paramref name="aheadFt"/> further along the route's node-to-node chords from <paramref name="start"/>,
    /// which lies on one of them.
    /// </summary>
    private static LatLon AlongRouteFrom(TaxiRoute route, LatLon start, double aheadFt)
    {
        int onIndex = Enumerable
            .Range(0, route.Segments.Count)
            .MinBy(i => GeoMath.DistanceToSegmentFt(start, route.Segments[i].Edge.FromNode.Position, route.Segments[i].Edge.ToNode.Position));
        LatLon from = start;
        double remainingFt = aheadFt;
        for (int i = onIndex; i < route.Segments.Count; i++)
        {
            LatLon to = route.Segments[i].Edge.ToNode.Position;
            double legFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
            if (remainingFt <= legFt)
            {
                return GeoMath.ProjectPoint(from, new TrueHeading(GeoMath.BearingTo(from, to)), remainingFt / GeoMath.FeetPerNm);
            }

            remainingFt -= legFt;
            from = to;
        }

        return from;
    }

    private static bool IsHoldingShortAtRest(AircraftState ac) =>
        (ac.GroundSpeed < SfoGroundHarness.StationarySpeedKts) && (ac.Phases?.CurrentPhase is HoldingShortPhase);

    /// <summary>
    /// The node where D merges into C: one straight D edge and two straight C edges, with D and one C edge within 10° of
    /// each other (OAK's D/C fan there is 5.7°). Resolved by shape, never by node id.
    /// </summary>
    private static GroundNode FindShallowMerge(AirportGroundLayout layout) =>
        layout.Nodes.Values.Single(n =>
        {
            var straight = n.Edges.OfType<GroundEdge>().ToList();
            List<GroundEdge> d = [.. straight.Where(e => e.TaxiwayName == "D")];
            List<GroundEdge> c = [.. straight.Where(e => e.TaxiwayName == "C")];
            return (d.Count == 1)
                && (c.Count == 2)
                && c.Any(ce =>
                    GeoMath.AbsBearingDifference(
                        GeoMath.BearingTo(n.Position, d[0].OtherNode(n).Position),
                        GeoMath.BearingTo(n.Position, ce.OtherNode(n).Position)
                    ) < 10.0
                );
        });

    /// <summary>
    /// The G intersection's near edge on D: walking D back from the merge, the last node of the first run of nodes that
    /// carry a G edge (G itself or a fillet arc onto it).
    /// </summary>
    private static GroundNode NearEdgeOfG(GroundNode merge)
    {
        GroundNode? nearEdge = null;
        foreach (GroundNode node in WalkBackAlongD(merge))
        {
            bool touchesG = node.Edges.Any(e => e.MatchesTaxiway("G"));
            if (touchesG)
            {
                nearEdge = node;
            }
            else if (nearEdge is not null)
            {
                break;
            }
        }

        return nearEdge ?? throw new InvalidOperationException("no G intersection on D behind the merge");
    }

    /// <summary>
    /// The nodes along D's straight edges back from the merge for <see cref="StartBackFt"/>, taking the straightest
    /// continuation, nearest the merge first.
    /// </summary>
    private static List<GroundNode> WalkBackAlongD(GroundNode merge)
    {
        GroundNode prev = merge;
        GroundNode cur = merge.Edges.OfType<GroundEdge>().Single(e => e.TaxiwayName == "D").OtherNode(merge);
        var walked = new List<GroundNode> { cur };
        double walkedFt = GeoMath.DistanceNm(prev.Position, cur.Position) * GeoMath.FeetPerNm;
        while (walkedFt < StartBackFt)
        {
            double inBearing = GeoMath.BearingTo(prev.Position, cur.Position);
            GroundNode back = prev;
            GroundNode here = cur;
            GroundEdge? next = here
                .Edges.OfType<GroundEdge>()
                .Where(e => (e.TaxiwayName == "D") && (e.OtherNode(here) != back))
                .MinBy(e => GeoMath.AbsBearingDifference(inBearing, GeoMath.BearingTo(here.Position, e.OtherNode(here).Position)));
            if (next is null)
            {
                break;
            }

            prev = here;
            cur = next.OtherNode(here);
            walked.Add(cur);
            walkedFt += GeoMath.DistanceNm(prev.Position, cur.Position) * GeoMath.FeetPerNm;
        }

        return walked;
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Aircraft, GroundNode Merge)? SpawnOnDShortOfTheMerge()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).EnableCategory("HoldShortAnnotator", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = true,
            },
        };
        GroundNode merge = FindShallowMerge(layout);
        List<GroundNode> walked = WalkBackAlongD(merge);
        GroundNode start = walked[^1];
        GroundNode toward = walked.Count > 1 ? walked[^2] : merge;
        AircraftState ac = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            Callsign,
            "C172",
            (start, new TrueHeading(GeoMath.BearingTo(start.Position, toward.Position))),
            new HoldingInPositionPhase()
        );
        return (engine, layout, ac, merge);
    }
}
