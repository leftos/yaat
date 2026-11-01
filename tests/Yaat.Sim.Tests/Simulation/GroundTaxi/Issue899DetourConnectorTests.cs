using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-899: a taxi detour connector never runs along a runway centreline (R1), and a runway is never read back as a
/// connector (R7). Two named taxiways that meet neither directly, nor across a runway as an offset crossing or a crossing
/// the airport's sidecar declares, and that no bridge off the runway joins, are refused: the pilot says they do not
/// connect and stays put. KCOS A2/C2 face each other across 17R/35L, C2 a high-speed exit, and YAAT ships no sidecar
/// declaring them a crossing: a bare <c>TAXI A2 C2</c> is refused, and the crossing tests declare the pair in themselves.
/// </summary>
[Collection("Acceptance")]
public partial class Issue899DetourConnectorTests(ITestOutputHelper output)
{
    private const string SfoC3S2Refusal = "Unable, C3 and S2 do not connect, request taxi instructions";

    /// <summary>
    /// Rest position of the E75L after <c>PUSH Y A1</c> off SFO gate B12 (<see cref="Pathfinding.SfoYankeeConnectorChoiceTests"/>).
    /// </summary>
    private static readonly LatLon SfoYankeeRestPosition = new(37.61216797, -122.38345984);

    /// <summary>Rest heading of that same push: along Y, toward A1 and AY3.</summary>
    private const double SfoYankeeRestHeadingDeg = 208.0;

    /// <summary>
    /// SFO <c>TAXI C3 S2</c>: C3 and S2 join 28R/10L from opposite sides about 298 ft apart, but S1 continues straight
    /// across 28R from C3's join, so the pair is no offset crossing, and every bridge from C3 to S2 runs along 10L/28R. The
    /// owner's ruling: refuse, "Unable, C3 and S2 do not connect, request taxi instructions", and stay put.
    /// </summary>
    [Fact]
    public void SfoC3S2_IsRefused_AndTheAircraftStaysPut()
    {
        if (SfoC3Start() is not { } start)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "SFO", start.Node, start.HeadingDeg);
        LatLon before = aircraft.Position;
        var taxi = new TaxiCommand(Path: ["C3", "S2"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message} — spoken: {result.PilotUnable?.Tts}");

        Assert.False(result.Success, "TAXI C3 S2 was accepted");
        Assert.Equal(SfoC3S2Refusal, result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(before, aircraft.Position);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(PilotResponder.BuildUnableTaxiwaysDoNotConnect(aircraft, "C3", "S2"), result.PilotUnable);
    }

    /// <summary>
    /// R1, R6 and R7 on SFO C3/S2: the C3→S2 bridges that run along 10L/28R are dropped, and so is the one left, through
    /// the lettered C, Z, ZS and S the clearance never named. No route runs along a runway centreline or names a runway as a
    /// connector, and the pair fails as not connecting.
    /// </summary>
    [Fact]
    public void SfoC3S2_BridgesAlong28R_AreDropped()
    {
        if (SfoC3Start() is not { } start)
        {
            return;
        }

        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPathDetailed(
            start.Layout,
            start.Node.Id,
            ["C3", "S2"],
            out PathfindingFailure? failure,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                StartHeadingTrue = start.HeadingDeg,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        foreach (string line in diag.Where(l => l.StartsWith("[detour]", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }

        output.WriteLine(
            $"route: {route?.FormatTaxiwaySequence()} warnings [{string.Join("; ", route?.Warnings ?? [])}] fail={failure?.HumanMessage}"
        );

        Assert.Contains(
            diag,
            line =>
                line.StartsWith("[detour] drops C3→S2 bridge", StringComparison.Ordinal)
                && (line.Contains("runs along RWY") || line.Contains("turns at RWY") || line.Contains("turns onto RWY"))
        );
        Assert.Contains(
            diag,
            line => line.StartsWith("[detour] drops C3→S2 bridge", StringComparison.Ordinal) && line.Contains("not in the clearance")
        );
        Assert.Null(route);
        Assert.Equal(FailureKind.TaxiwaysDoNotConnect, failure?.Kind);
        Assert.Equal(SfoC3S2Refusal, failure?.HumanMessage);
    }

    /// <summary>
    /// The silent connector stays silent: SFO's Y and A share no junction, and the bridge through the numbered connector
    /// AY3 (<see cref="Pathfinding.SfoYankeeConnectorChoiceTests"/>) still resolves with the "do not connect directly — taxi
    /// via AY3" notice, which never names a runway.
    /// </summary>
    [Fact]
    public void SfoYankeeToAlpha_NumberedStub_StillGetsTheConnectorNotice()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if ((layout is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        GroundNode? startNode = layout.FindTaxiStartNode(
            SfoYankeeRestPosition,
            new TrueHeading(SfoYankeeRestHeadingDeg),
            new TaxiStartTraffic
            {
                Callsign = "TEST",
                AircraftType = "B738",
                OtherGroundAircraft = [],
            }
        );
        Assert.NotNull(startNode);

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["Y", "A", "A1"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = "1R",
                ExplicitHoldShorts = [],
                StartHeadingTrue = SfoYankeeRestHeadingDeg,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        output.WriteLine($"route: {route?.FormatTaxiwaySequence()} warnings [{string.Join("; ", route?.Warnings ?? [])}] fail={failReason}");

        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Contains("Y and A do not connect directly — taxi via AY3", route.Warnings);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("RWY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A scenario-scripted <c>TAXI C3 S2</c> is refused the same way, though no controller issued it: the aircraft stays put
    /// and a warning names the aircraft, the taxi and why it was refused.
    /// </summary>
    [Fact]
    public void SfoC3S2_Scripted_IsRefusedAndLogged()
    {
        if (SfoC3Start() is not { } start)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "SFO", start.Node, start.HeadingDeg);
        LatLon before = aircraft.Position;
        var taxi = new TaxiCommand(Path: ["C3", "S2"], HoldShorts: [], Expedite: false, DestinationParking: null);
        var warnings = WarningLogCapture.Install();

        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            taxi,
            start.Layout,
            new GroundCommandHandler.TaxiDispatch(
                AutoCrossRunwayMode.Off,
                ActiveRunways.Empty,
                IsScenarioScripted: true,
                ResultReachesController: false,
                ListAircraft: null
            )
        );
        foreach (string line in warnings.Warnings)
        {
            output.WriteLine($"warning: {line}");
        }

        Assert.False(result.Success, "the scripted TAXI C3 S2 was accepted");
        Assert.Equal(SfoC3S2Refusal, result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(before, aircraft.Position);
        Assert.Contains(
            warnings.Warnings,
            w =>
                w.Contains(Issue1334OakDToFAcross33Tests.Callsign, StringComparison.Ordinal)
                && w.Contains("C3 S2", StringComparison.Ordinal)
                && w.Contains(SfoC3S2Refusal, StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// The hold-short fold does not go around the refusal: SFO <c>TAXI C3 S2 HS B</c> is refused as C3 and S2 not connecting,
    /// never re-resolved with B folded into the path.
    /// </summary>
    [Fact]
    public void SfoC3S2_WithAHoldShortHint_IsStillRefused()
    {
        if (SfoC3Start() is not { } start)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "SFO", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["C3", "S2"], HoldShorts: [HoldShortTarget.Parse("B")], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(result.Success, "TAXI C3 S2 HS B was accepted");
        Assert.Equal(SfoC3S2Refusal, result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The ramp-lane cut does not go around the refusal: from SFO gate B20S, <c>TAXI M4 C3 S2</c> is refused as issued, C3 and S2
    /// not connecting, before any recovery runs — the ramp-lane reposition (category <c>RampLaneReposition</c>, logging every
    /// plan it tries at Debug) is never asked, so it logs nothing — the refusal reaches the controller and the aircraft stays put.
    /// </summary>
    [Fact]
    public void SfoGateB20S_TaxiM4C3S2_RampLaneCutDoesNotBypassTheRefusal()
    {
        TestVnasData.EnsureInitialized();
        var tap = new CapturingSimLogProvider(LogLevel.Debug, capacity: 500);
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("RampLaneReposition", LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .CaptureInto(tap)
            .InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundNode gate = layout.FindParkingByName("B20S")!;
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "SFO", gate, gate.TrueHeading!.Value.Degrees);
        LatLon before = aircraft.Position;
        var taxi = new TaxiCommand(Path: ["M4", "C3", "S2"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        List<string> logged = [.. tap.Drain().Select(r => r.Message)];
        foreach (string line in logged)
        {
            output.WriteLine($"log: {line}");
        }

        Assert.False(result.Success, "TAXI M4 C3 S2 was accepted");
        Assert.Equal(SfoC3S2Refusal, result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(before, aircraft.Position);
        Assert.Contains(logged, line => line.Contains("TAXI M4 C3 S2 refused — " + SfoC3S2Refusal, StringComparison.Ordinal));
        Assert.DoesNotContain(logged, line => line.StartsWith("[Reposition]", StringComparison.Ordinal));
    }

    /// <summary>
    /// S1 (YAAT-899 R7): the search predicate and the post-hoc filter are one judgement with one set of inputs, the bridge's first
    /// move judged against the arrival edge of the head it leaves from. KCOS C2 ends on 17R/35L at #40: a head there, arrived
    /// along C2 from #248, whose bridge opens with the straight same-side edge back toward #248, is turning at the uncleared
    /// runway for both judges. The filter once judged the first move with no arrival edge and let it pass.
    /// </summary>
    [Fact]
    public void CosC2HeadAt40_StraightSameSideFirstMove_IsJudgedTheSameBySearchAndFilter()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return;
        }

        GroundNode at40 = layout.Nodes[40];
        GroundNode at252 = layout.Nodes[248];
        Assert.True(AirportGroundLayout.HasRunwayCenterlineEdge(at40), "#40 is not on 17R/35L");
        IGroundEdge c2 = Assert.Single(at40.Edges, e => (e is GroundEdge) && e.MatchesTaxiway("C2") && (e.OtherNode(at40).Id == at252.Id));

        var diag = new List<string>();
        var ctx = SearchContext.Compile(
            layout,
            at252.Id,
            ["C2", "A2"],
            null,
            null,
            null,
            null,
            null,
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small,
            null,
            diag.Add,
            null,
            null,
            startPose: null,
            aircraftLengthFt: AircraftLength.ResolveFt("C172")
        );
        var start = PartialRoute.StartAt(at252.Id);
        PartialRoute head = start with
        {
            HeadNodeId = at40.Id,
            ArrivalBearing = GeoMath.BearingTo(at252.Position, at40.Position),
            LastEdge = c2,
            LastTaxiwayName = "C2",
            Previous = start,
            Depth = 1,
        };
        var bridge = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    Edge = new DirectionalEdge
                    {
                        Edge = c2,
                        FromNode = at40,
                        ToNode = at252,
                    },
                    TaxiwayName = "C2",
                },
            ],
            HoldShortPoints = [],
        };
        BridgeRules rules = SegmentExpander.BridgeRulesFor(head, "C2", "A2", 0, ctx);

        (string? search, string? filter) = SegmentExpander.JudgeBridge(head, bridge, rules, ctx);
        output.WriteLine($"search={search ?? "legal"}; filter={filter ?? "legal"}");

        Assert.NotNull(search);
        Assert.StartsWith("turns at RWY17R/35L", search);
        Assert.Equal(search, filter);
    }

    /// <summary>
    /// S1 (YAAT-899 R7): every bridge the detour search finds, restricted pass or unrestricted classification pass, is judged the
    /// same by the search predicate and the post-hoc filter (<see cref="SegmentExpander.JudgeBridge"/>, logged per bridge as
    /// <c>[detour] judged …</c>), over the item's real cases: KCOS <c>C2 A2 A</c>, SFO <c>C3 S2</c> and <c>Z1 S2</c>, the OAK
    /// rollout clearance from the exit J (its J→A bridge through C), the repeated-lead clearance of
    /// <see cref="OakCJJunction_TaxiFKJCW_RepeatedLeadTaxiwayGetsNoFirstTransitionExemption"/> and a stand start (SFO gate G3,
    /// <c>TAXI A Q B F</c>, whose A→Q bridge opens from the stand).
    /// </summary>
    [Theory]
    [InlineData("COS C2 A2 A")]
    [InlineData("SFO C3 S2")]
    [InlineData("SFO Z1 S2")]
    [InlineData("OAK exit J: C F K J A B W")]
    [InlineData("OAK on C: C F K J C W")]
    [InlineData("SFO stand G3: A Q B F")]
    public void EveryBridgeFound_IsJudgedTheSameBySearchAndFilter(string caseName)
    {
        var diag = new List<string>();
        if (AgreementCase(caseName, diag) is not { } c)
        {
            return;
        }

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPathDetailed(
            c.Layout,
            c.StartNodeId,
            c.Path,
            out PathfindingFailure? failure,
            c.Options,
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        output.WriteLine($"route: {route?.FormatTaxiwaySequence()} fail={failure?.HumanMessage}");
        List<(string Bridge, string Search, string Filter)> judged = [];
        foreach (string line in diag)
        {
            Match m = JudgedLine().Match(line);
            if (m.Success)
            {
                judged.Add((m.Groups["bridge"].Value, m.Groups["search"].Value, m.Groups["filter"].Value));
                output.WriteLine(line);
            }
        }

        Assert.NotEmpty(judged);
        Assert.All(judged, j => Assert.True(j.Search == j.Filter, $"{j.Bridge}: search says '{j.Search}', filter says '{j.Filter}'"));
        Assert.DoesNotContain(diag, line => line.Contains("only the unrestricted search found", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\[detour\] judged (?<bridge>.+?): search=(?<search>.+); filter=(?<filter>.+)$")]
    private static partial Regex JudgedLine();

    private sealed record AgreementStart(AirportGroundLayout Layout, int StartNodeId, List<string> Path, ExplicitPathOptions Options);

    /// <summary>The start, clearance and options of one agreement case, or null when its layout is missing.</summary>
    private AgreementStart? AgreementCase(string caseName, List<string> diag)
    {
        TestVnasData.EnsureInitialized();
        switch (caseName)
        {
            case "COS C2 A2 A":
                return CosStart("C2", "A2") is { } cos ? OnTaxiway(cos.Layout, cos.Node.Id, cos.HeadingDeg, ["C2", "A2", "A"], diag) : null;
            case "SFO C3 S2":
                return SfoC3Start() is { } sfo ? OnTaxiway(sfo.Layout, sfo.Node.Id, sfo.HeadingDeg, ["C3", "S2"], diag) : null;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(caseName[..3]);
        if (layout is null)
        {
            return null;
        }

        switch (caseName)
        {
            case "SFO Z1 S2":
                return OnTaxiway(layout, 649, 28.0, ["Z1", "S2"], diag);
            case "OAK exit J: C F K J A B W":
                GroundNode cj = GroundNodeLookup.Junction(layout, "C", "J");
                GroundNode jBar = GroundNodeLookup.HoldShortBar(layout, "28R", "J", cj);
                Assert.True(
                    (jBar.Type == GroundNodeType.RunwayHoldShort) && jBar.Edges.Any(e => e.MatchesTaxiway("J")),
                    $"#{jBar.Id} is not J's 28R bar"
                );
                double offJ = GeoMath.BearingTo(jBar.Position, cj.Position);
                return OnTaxiway(layout, jBar.Id, offJ, ["C", "F", "K", "J", "A", "B", "W"], diag);
            case "OAK on C: C F K J C W":
                double alongC = GeoMath.BearingTo(layout.Nodes[352].Position, layout.Nodes[357].Position);
                return new AgreementStart(
                    layout,
                    352,
                    ["C", "F", "K", "J", "C", "W"],
                    new ExplicitPathOptions
                    {
                        StartPose = null,
                        OccupiedTaxiway = "C",
                        ImpliedLeadTaxiway = "C",
                        AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                        StartHeadingTrue = alongC,
                        DiagnosticLog = diag.Add,
                    }
                );
            case "SFO stand G3: A Q B F":
                GroundNode stand = layout.FindParkingByName("G3")!;
                return new AgreementStart(
                    layout,
                    stand.Id,
                    ["A", "Q", "B", "F"],
                    new ExplicitPathOptions
                    {
                        StartPose = null,
                        OccupiedTaxiway = null,
                        ImpliedLeadTaxiway = null,
                        AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                        DestinationRunway = "28L",
                        StartHeadingTrue = stand.TrueHeading!.Value.Degrees,
                        DiagnosticLog = diag.Add,
                    }
                );
            default:
                throw new ArgumentException($"unknown agreement case '{caseName}'", nameof(caseName));
        }
    }

    private static AgreementStart OnTaxiway(AirportGroundLayout layout, int startNodeId, double heading, List<string> path, List<string> diag) =>
        new(
            layout,
            startNodeId,
            path,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                StartHeadingTrue = heading,
                DiagnosticLog = diag.Add,
            }
        );

    /// <summary>
    /// S-A (YAAT-899 R8): the bridge search keys its closed set on the bridge's progress, so a cheaper arrival at a node that has
    /// touched <see cref="BridgeRules.FromTaxiway"/> never prunes a dearer one that has not, the only one the excluded letter may
    /// still follow. Driven on <see cref="AutoRouter"/> directly over SFO's real layout from a stand-like start off the taxiway
    /// the transition leaves (<see cref="BridgeRules.HeadOnFromTaxiway"/> false), no real clearance reaching this topology: the
    /// cheapest way to the goal touches <paramref name="fromTaxiway"/> before it drives <paramref name="excluded"/>, so it is
    /// illegal; a legal bridge exists that reaches <paramref name="excluded"/> without touching it, and the search returns it.
    /// </summary>
    [Theory]
    [InlineData(370, 894, "L", "M")]
    [InlineData(859, 860, "Z", "R")]
    public void BridgeSearch_CheaperArrivalThatTouchedFromTaxiway_DoesNotPruneTheLegalBridge(
        int startNodeId,
        int goalNodeId,
        string fromTaxiway,
        string excluded
    )
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var ctx = SearchContext.Compile(
            layout,
            startNodeId,
            [],
            null,
            null,
            null,
            goalNodeId,
            null,
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large,
            null,
            null,
            null,
            null,
            startPose: null,
            aircraftLengthFt: null
        );
        var rules = new BridgeRules(null, fromTaxiway, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { excluded });

        (TaxiRoute? cheapest, PathfindingFailure? _) = AutoRouter.Run(ctx);
        (TaxiRoute? bridge, PathfindingFailure? failure) = AutoRouter.Run(ctx with { Bridge = rules });
        output.WriteLine($"cheapest: {NodePath(cheapest)} — {Verdict(cheapest, rules, ctx)}");
        output.WriteLine($"bridge:   {NodePath(bridge)} — {Verdict(bridge, rules, ctx)} fail={failure?.HumanMessage}");

        Assert.NotNull(cheapest);
        Assert.NotNull(Refusal(cheapest, rules, ctx));
        Assert.NotNull(bridge);
        Assert.Equal(goalNodeId, bridge.Segments[^1].ToNodeId);
        Assert.Null(Refusal(bridge, rules, ctx));
    }

    /// <summary>Why <paramref name="rules"/> refuse <paramref name="route"/>, replayed move by move from a bare start, or null.</summary>
    private static string? Refusal(TaxiRoute route, BridgeRules rules, SearchContext ctx)
    {
        var progress = BridgeProgress.AtStart(null, rules);
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            if (ctx.BridgeMoveRefusal(rules, progress, seg.Edge.FromNode, seg.Edge.Edge, seg.Edge.ToNode) is { } refusal)
            {
                return refusal;
            }

            progress = progress.After(seg.Edge.FromNode, seg.Edge.Edge, rules);
        }

        return null;
    }

    private static string Verdict(TaxiRoute? route, BridgeRules rules, SearchContext ctx) =>
        route is null ? "no route" : Refusal(route, rules, ctx) ?? "legal";

    private static string NodePath(TaxiRoute? route) =>
        route is null
            ? "none"
            : string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId} {s.TaxiwayName}").Append($"#{route.Segments[^1].ToNodeId}"));

    /// <summary>
    /// S2 (YAAT-899 R7): the first-transition exemptions follow the transition's index in the clearance, not the name of the
    /// taxiway it leaves. At OAK's C/J junction (#352) an aircraft on C, cleared <c>TAXI F K J C W</c>, gets C prepended as the
    /// implied lead ([C, F, K, J, C, W]); the closing C→W transition leaves the same taxiway as the lead but is the clearance's
    /// fifth, and C and W meet only through A and B, which the clearance left out: refused, not bridged silently.
    /// </summary>
    [Fact]
    public void OakCJJunction_TaxiFKJCW_RepeatedLeadTaxiwayGetsNoFirstTransitionExemption()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode junction = layout.Nodes[352];
        GroundNode cfJunction = layout.Nodes[357];
        Assert.True(junction.Edges.Any(e => e.MatchesTaxiway("C")) && junction.Edges.Any(e => e.MatchesTaxiway("J")), "#352 is not the C/J junction");
        Assert.True(cfJunction.Edges.Any(e => e.MatchesTaxiway("F")), "#357 is not the C/F junction");
        double heading = GeoMath.BearingTo(junction.Position, cfJunction.Position);
        output.WriteLine($"OAK C/J junction #{junction.Id}, nosed {heading:F0}° along C toward F");

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "OAK", junction, heading);
        aircraft.Ground.CurrentTaxiway = "C";
        var taxi = new TaxiCommand(Path: ["F", "K", "J", "C", "W"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(result.Success, "TAXI F K J C W was accepted");
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("C", "W"), result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// S7 (YAAT-899 R7): a taxiway counts as cleared before a transition when the clearance names it before the pair's own
    /// mention, or, when the pair is nowhere adjacent, before the <em>first</em> mention of the second taxiway: a later mention
    /// of the second taxiway does not license a connector named between the two.
    /// </summary>
    [Theory]
    [InlineData("Z,A2,A,Y,A2", false)]
    [InlineData("Z,A,A2", true)]
    [InlineData("A2,A,C2,A2", true)]
    [InlineData("C2,A2,A", false)]
    public void ClearedBefore_StopsAtTheFirstMentionUnlessThePairIsAdjacent(string sequence, bool expected)
    {
        bool cleared = SegmentExpander.ClearedBefore("A", "C2", "A2", sequence.Split(','));

        Assert.Equal(expected, cleared);
    }

    /// <summary>
    /// Case 1 (YAAT-899 R6): the detour search itself keeps off runway centrelines and unnamed letter taxiways, so each entry
    /// node offers its cheapest legal bridge. SFO <c>TAXI Z1 S2</c> from #649 heading 028 resolves through the numbered stub S3
    /// with the connector notice, and no bridge is dropped after the fact.
    /// </summary>
    [Fact]
    public void SfoZ1S2_FromNode649_ResolvesThroughS3WithoutADroppedBridge()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var diag = new List<string>();
        TaxiRoute? route = ResolveDetailed(layout, 649, ["Z1", "S2"], 28.0, diag, out PathfindingFailure? failure);
        WriteDetourDiag(diag);
        output.WriteLine(
            $"route: {route?.FormatTaxiwaySequence()} warnings [{string.Join("; ", route?.Warnings ?? [])}] fail={failure?.HumanMessage}"
        );

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.Contains(route.Segments, s => s.TaxiwayName == "S3");
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Contains("Z1 and S2 do not connect directly — taxi via S3", route.Warnings);
        HoldShortPoint crossing = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.RunwayCrossing) && (h.TargetName?.Contains("10R", StringComparison.Ordinal) == true)
        );
        output.WriteLine($"holds short of {crossing.TargetName} at #{crossing.NodeId}");
        Assert.DoesNotContain(diag, line => line.StartsWith("[detour] drops Z1→S2", StringComparison.Ordinal));
    }

    /// <summary>
    /// Case 3a (YAAT-899 R6): a bridge may use the runway the aircraft stands on only to leave it, up to
    /// <see cref="SearchContext.MaxStartRunwayExitFt"/>: OAK <c>TAXI D G</c> from 15/33 and MIA <c>TAXI Z M</c> from 12/30 resolve,
    /// the search's pick passing the post-hoc filter (no dropped-bridge diagnostic).
    /// </summary>
    [Theory]
    [InlineData("MIA", 1160, 120.0, "Z", "M")]
    public void StandingOnARunway_ABridgeMayLeaveItWithinTheAllowance(string airportId, int startNodeId, double heading, string from, string to)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        if (layout is null)
        {
            return;
        }

        AssertABridgeLeavesTheStartRunwayWithinTheAllowance(layout, startNodeId, heading, from, to);
    }

    /// <summary>
    /// Case 3a at OAK: <c>TAXI D G</c> from the 15/33 centreline node where the D and F fillets onto the runway meet, nosed
    /// 164°.
    /// </summary>
    [Fact]
    public void StandingOnOak33AtTheDAndFFillets_ABridgeMayLeaveItWithinTheAllowance()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode start = GroundNodeLookup.Junction(layout, "D - RWY15/33", "F - RWY15/33");
        AssertABridgeLeavesTheStartRunwayWithinTheAllowance(layout, start.Id, 164.0, "D", "G");
    }

    private void AssertABridgeLeavesTheStartRunwayWithinTheAllowance(
        AirportGroundLayout layout,
        int startNodeId,
        double heading,
        string from,
        string to
    )
    {
        Assert.True(AirportGroundLayout.HasRunwayCenterlineEdge(layout.Nodes[startNodeId]), $"#{startNodeId} is not on a runway");
        var diag = new List<string>();
        TaxiRoute? route = ResolveDetailed(layout, startNodeId, [from, to], heading, diag, out PathfindingFailure? failure);
        WriteDetourDiag(diag);
        output.WriteLine(
            $"route: {route?.FormatTaxiwaySequence()} warnings [{string.Join("; ", route?.Warnings ?? [])}] fail={failure?.HumanMessage}"
        );

        Assert.Null(failure);
        Assert.NotNull(route);
        double centrelineFt = SegmentExpander.CenterlineLengthFt(route.Segments.Where(s => s.Edge.Edge.IsRunwayCenterline).Select(s => s.Edge.Edge));
        output.WriteLine($"{centrelineFt:F0} ft along the start runway");
        Assert.True(centrelineFt > 0.0, "the route never used the runway the aircraft stands on");
        Assert.InRange(centrelineFt, double.Epsilon, SearchContext.MaxStartRunwayExitFt);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("do not connect", StringComparison.Ordinal));
        Assert.DoesNotContain(diag, line => line.StartsWith($"[detour] drops {from}→{to}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Case 3a's limit: COS <c>TAXI C6 C7</c> from C6's runway join, nosed along the runway toward C7, would back-taxi about 460 ft
    /// of it to reach C7, so the pair is refused as not connecting.
    /// </summary>
    [Fact]
    public void CosC6C7_BackTaxiAlongTheRunway_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return;
        }

        GroundNode c6Join = Assert.Single(layout.GetNodesOnTaxiway("C6"), n => AirportGroundLayout.HasRunwayCenterlineEdge(n));
        GroundNode c7Join = Assert.Single(layout.GetNodesOnTaxiway("C7"), n => AirportGroundLayout.HasRunwayCenterlineEdge(n));
        double heading = GeoMath.BearingTo(c6Join.Position, c7Join.Position);
        double apartFt = GeoMath.DistanceNm(c6Join.Position, c7Join.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"COS C6 joins at #{c6Join.Id}, C7 at #{c7Join.Id}, {apartFt:F0} ft apart");

        var diag = new List<string>();
        TaxiRoute? route = ResolveDetailed(layout, c6Join.Id, ["C6", "C7"], heading, diag, out PathfindingFailure? failure);
        WriteDetourDiag(diag);
        output.WriteLine($"route: {route?.FormatTaxiwaySequence()} fail={failure?.HumanMessage}");

        Assert.Null(route);
        Assert.Equal(FailureKind.TaxiwaysDoNotConnect, failure?.Kind);
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("C6", "C7"), failure?.HumanMessage);
    }

    private static TaxiRoute? ResolveDetailed(
        AirportGroundLayout layout,
        int startNodeId,
        List<string> path,
        double headingTrue,
        List<string> diag,
        out PathfindingFailure? failure
    ) =>
        TaxiPathfinder.ResolveExplicitPathDetailed(
            layout,
            startNodeId,
            path,
            out failure,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                StartHeadingTrue = headingTrue,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );

    private void WriteDetourDiag(List<string> diag)
    {
        foreach (string line in diag.Where(l => l.StartsWith("[detour]", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// The start exemptions cover only the clearance's first transition: at OAK's C/B junction (#193), <c>TAXI C A W</c> leads
    /// off along C, but A and W meet only through B, which the clearance left out, so the pair is refused even though the
    /// aircraft started on B.
    /// </summary>
    [Fact]
    public void OakCBJunction_TaxiCAW_StartTaxiwayExemptsOnlyTheFirstTransition()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode junction = layout.Nodes[193];
        Assert.True(junction.Edges.Any(e => e.MatchesTaxiway("B")) && junction.Edges.Any(e => e.MatchesTaxiway("C")), "#193 is not the C/B junction");
        GroundEdge alongC = junction.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway("C"));
        double heading = GeoMath.BearingTo(junction.Position, alongC.OtherNode(junction).Position);
        output.WriteLine($"OAK C/B junction #{junction.Id}, nosed {heading:F0}° along C");

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "OAK", junction, heading);
        var taxi = new TaxiCommand(Path: ["C", "A", "W"], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(result.Success, "TAXI C A W was accepted");
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("A", "W"), result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// Case 2 (YAAT-899 R6): a pair whose junctions exist but none is reachable ahead is not "do not connect". LAX <c>TAXI E W</c>
    /// from #1822 heading 263 has both E/W junctions behind the aircraft; CMH <c>TAXI B B1</c> from #210 and <c>TAXI SR C1</c>
    /// from #425 (both heading 094) walk their first taxiway to a dead end short of the junction ahead, so they say the same
    /// without the turn-about note.
    /// </summary>
    [Theory]
    [InlineData("LAX", 1822, 263.0, "E", "W", true)]
    [InlineData("CMH", 210, 94.0, "B", "B1", false)]
    [InlineData("CMH", 425, 94.0, "SR", "C1", false)]
    public void JunctionsExistButNoneAhead_IsRefusedAsNoRouteAhead(
        string airportId,
        int startNodeId,
        double heading,
        string from,
        string to,
        bool intersectionOnlyBehind
    )
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, airportId, layout.Nodes[startNodeId], heading);
        LatLon before = aircraft.Position;
        var taxi = new TaxiCommand(Path: [from, to], HoldShorts: [], Expedite: false, DestinationParking: null);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message} — spoken: {result.PilotUnable?.Tts}");

        Assert.False(result.Success, $"TAXI {from} {to} was accepted");
        Assert.Equal(SegmentExpander.NoRouteAheadReason(from, to, intersectionOnlyBehind), result.Message);
        Assert.Equal(PilotResponder.BuildUnableNoRouteAhead(aircraft, from, to, intersectionOnlyBehind), result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(before, aircraft.Position);
    }

    /// <summary>The spoken no-route-ahead refusal spells both taxiways, says where the intersection is, and asks for a taxi.</summary>
    [Fact]
    public void SpokenNoRouteAhead_SpellsBothTaxiways()
    {
        var aircraft = new AircraftState { Callsign = Issue1334OakDToFAcross33Tests.Callsign, AircraftType = "C172" };
        string spokenCallsign = SpokenCallsign(aircraft);

        PilotSpeechText behind = PilotResponder.BuildUnableNoRouteAhead(aircraft, "E", "W", intersectionOnlyBehind: true);
        PilotSpeechText ahead = PilotResponder.BuildUnableNoRouteAhead(aircraft, "SR", "C1", intersectionOnlyBehind: false);
        output.WriteLine($"terminal: {behind.Terminal} — spoken: {behind.Tts}");
        output.WriteLine($"terminal: {ahead.Terminal} — spoken: {ahead.Tts}");

        Assert.Equal(
            "Unable, no route from E onto W ahead, request taxi instructions (intersection only directly behind, would require turn-about)",
            behind.Terminal
        );
        Assert.Equal(
            $"{spokenCallsign}, unable, no route from echo onto whiskey ahead, the intersection is behind us, we'd have to turn around, request taxi",
            behind.Tts
        );
        Assert.Equal("Unable, no route from SR onto C1 ahead, request taxi instructions", ahead.Terminal);
        Assert.Equal($"{spokenCallsign}, unable, no route from sierra romeo onto charlie one ahead, request taxi", ahead.Tts);
    }

    /// <summary>The spoken refusal spells both taxiways and asks for a taxi.</summary>
    [Fact]
    public void SpokenRefusal_SpellsBothTaxiways()
    {
        var aircraft = new AircraftState { Callsign = Issue1334OakDToFAcross33Tests.Callsign, AircraftType = "C172" };
        string spokenCallsign = SpokenCallsign(aircraft);

        PilotSpeechText speech = PilotResponder.BuildUnableTaxiwaysDoNotConnect(aircraft, "C3", "S2");
        output.WriteLine($"terminal: {speech.Terminal} — spoken: {speech.Tts}");

        Assert.Equal(SfoC3S2Refusal, speech.Terminal);
        Assert.Equal($"{spokenCallsign}, unable, charlie three and sierra two don't connect, request taxi", speech.Tts);
    }

    /// <summary>
    /// KCOS <c>TAXI A2 C2</c> and <c>TAXI C2 A2</c> with no declared pair: C2 is a high-speed exit 30° off 17R/35L, so the pair
    /// is no offset crossing, and every bridge between them runs along the runway or through lettered taxiways the clearance
    /// left out. The pilot refuses, naming the pair in the order issued, and stays put.
    /// </summary>
    [Theory]
    [InlineData("A2", "C2")]
    [InlineData("C2", "A2")]
    public void CosA2C2_NoDeclaredPair_IsRefused(string fromTaxiway, string toTaxiway)
    {
        if (CosStart(fromTaxiway, toTaxiway) is not { } start)
        {
            return;
        }

        var diag = new List<string>();
        TaxiRoute? resolved = ResolveDetailed(
            start.Layout,
            start.Node.Id,
            [fromTaxiway, toTaxiway],
            start.HeadingDeg,
            diag,
            out PathfindingFailure? failure
        );
        WriteDetourDiag(diag);
        output.WriteLine($"resolved: {resolved?.FormatTaxiwaySequence()} fail={failure?.HumanMessage}");

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        LatLon before = aircraft.Position;
        var taxi = new TaxiCommand(Path: [fromTaxiway, toTaxiway], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message} — spoken: {result.PilotUnable?.Tts}");

        Assert.Null(resolved);
        Assert.False(result.Success, $"TAXI {fromTaxiway} {toTaxiway} was accepted");
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason(fromTaxiway, toTaxiway), result.Message);
        Assert.Equal(PilotResponder.BuildUnableTaxiwaysDoNotConnect(aircraft, fromTaxiway, toTaxiway), result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(before, aircraft.Position);
    }

    /// <summary>
    /// The refusal names the pair that does not connect, not the pair after it: KCOS <c>TAXI C2 A2 A</c> with no declared pair
    /// refuses C2 and A2. A debugger once saw it refuse "A2 and A", the search having kept a C2→A2 detour that ended on a
    /// centreline node (the arc onto the runway at A2's join); the search now forbids that arc and drops the bridge.
    /// </summary>
    [Fact]
    public void CosC2A2A_NoDeclaredPair_RefusesNamingC2AndA2()
    {
        if (CosStart("C2", "A2") is not { } start)
        {
            return;
        }

        var diag = new List<string>();
        TaxiRoute? resolved = ResolveDetailed(
            start.Layout,
            start.Node.Id,
            ["C2", "A2", "A"],
            start.HeadingDeg,
            diag,
            out PathfindingFailure? failure
        );
        WriteDetourDiag(diag);
        output.WriteLine($"resolved: {resolved?.FormatTaxiwaySequence()} fail={failure?.HumanMessage}");

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["C2", "A2", "A"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.Null(resolved);
        Assert.DoesNotContain(diag, line => line.StartsWith("[detour] found detour C2→A2", StringComparison.Ordinal));
        Assert.False(result.Success, "TAXI C2 A2 A was accepted");
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("C2", "A2"), result.Message);
        Assert.DoesNotContain("A2 and A ", result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// Naming the runway is the way between them: KCOS <c>TAXI A2 17R C2 C</c> taxis A2, along 17R and off onto C2, and reads
    /// back the runway as a taxiway of the route.
    /// </summary>
    [Fact]
    public void CosA2On17RC2_NamingTheRunway_Resolves()
    {
        if (CosStart("A2", "C2") is not { } start)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["A2", "17R", "C2", "C"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Taxi via A2 on 17R C2 C", result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Contains(route.Segments, s => s.TaxiwayName == "C2");
    }

    /// <summary>
    /// The waiver is pair- and runway-specific: a sidecar declaring another pair across 17R/35L, or A2/C2 across the other
    /// runway, leaves KCOS <c>TAXI A2 C2</c> refused as not connecting.
    /// </summary>
    [Theory]
    [InlineData("A3", "C3", "17R/35L")]
    [InlineData("A2", "C2", "17L/35R")]
    public void CosA2C2_DeclaredAsAnotherPairOrAcrossAnotherRunway_IsStillRefused(string declaredFirst, string declaredSecond, string declaredRunway)
    {
        if ((CosStart("A2", "C2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, declaredFirst, declaredSecond, declaredRunway));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["A2", "C2"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"declared {declaredFirst}/{declaredSecond} across {declaredRunway}: {result.Success} — {result.Message}");

        Assert.False(result.Success, "TAXI A2 C2 was accepted");
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("A2", "C2"), result.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The declared crossing is measured on the path flown: KCOS <c>TAXI A2 C2 C</c> with the pair declared continues A2's line
    /// to its meeting point with C2 and turns once, 59.6°, within a B738's limit, where the 150° reversal off the centreline
    /// refused it.
    /// </summary>
    [Fact]
    public void CosA2C2_DeclaredPair_AdmitsAB738WithOneTurnOntoC2()
    {
        if ((CosStart("A2", "C2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        aircraft.AircraftType = "B738";
        var taxi = new TaxiCommand(Path: ["A2", "C2", "C"], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"B738: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Taxi via A2 C2 C", result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }

    /// <summary>
    /// KCOS <c>TAXI A2 C2</c> and <c>TAXI C2 A2</c> with the pair declared: C2 is a high-speed exit 30° off 17R/35L, which the
    /// 60° rule alone would refuse, but the airport diagram has A2 and C2 facing each other across the runway, and the sidecar
    /// the test declares waives that rule for them. The route is an offset crossing of 17R/35L: it holds short at the entry
    /// taxiway's bar, walks no centreline beyond the two joins, and reads back as a crossing with no connector notice.
    /// </summary>
    [Theory]
    [InlineData("A2", "C2", "C")]
    [InlineData("C2", "A2", "A")]
    public void CosA2C2_DeclaredPair_IsAnOffsetCrossing(string fromTaxiway, string toTaxiway, string onward)
    {
        if ((CosStart(fromTaxiway, toTaxiway) is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        var diag = new List<string>();
        TaxiRoute? resolved = TaxiPathfinder.ResolveExplicitPath(
            start.Layout,
            start.Node.Id,
            [fromTaxiway, toTaxiway],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("C172"),
                StartHeadingTrue = start.HeadingDeg,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
        );
        output.WriteLine($"resolved: {resolved?.FormatTaxiwaySequence()} fail={failReason}");
        output.WriteLine($"bare route nodes: {string.Join(",", RouteNodeIds(resolved))}");
        Assert.Contains(diag, line => line.StartsWith($"[runway-crossing] {fromTaxiway}→{toTaxiway} across", StringComparison.Ordinal));

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: [fromTaxiway, toTaxiway, onward], HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");

        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, h => h.TargetName?.Contains("35L") == true);
        Assert.Equal(HoldShortReason.RunwayCrossing, crossing.Reason);
        Assert.True(
            IsCosRunwayBarOnTaxiway(start.Layout, crossing.NodeId, fromTaxiway),
            $"17R/35L hold short at #{crossing.NodeId} is not {fromTaxiway}'s bar"
        );

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.DoesNotContain(route.Segments, s => s.TaxiwayName.Contains(":link", StringComparison.Ordinal));
        TaxiRouteSegment intoMeetingPoint = Assert.Single(route.Segments, s => s.ToNodeId < 0);
        Assert.Equal(fromTaxiway, intoMeetingPoint.TaxiwayName);
        double meetingPointOffFt = AirportGroundLayout.CrossTrackFromCenterlineFt(
            RunwayCrossingDetector.BuildRunwayRectangle(Assert.IsType<GroundRunway>(start.Layout.FindRunway("35L"))),
            intoMeetingPoint.Edge.ToNode
        );
        Assert.InRange(meetingPointOffFt, 147.0, 153.0);

        // The readback names the hold the controller issued, as OAK's "Taxi via E C D F HS 15/33 at D" does.
        AircraftState withHold = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        CommandResult heldResult = GroundCommandHandler.TryTaxi(withHold, taxi with { HoldShorts = [HoldShortTarget.Parse("35L")] }, start.Layout);
        output.WriteLine($"with HS 35L: {heldResult.Success} — {heldResult.Message}");
        Assert.True(heldResult.Success, heldResult.Message);
        foreach (string readback in new[] { result.Message ?? "", heldResult.Message ?? "" })
        {
            Assert.StartsWith($"Taxi via {fromTaxiway} {toTaxiway} {onward}", readback);
            Assert.DoesNotContain("do not connect", readback);
            Assert.DoesNotContain("RWY", readback);
            Assert.DoesNotContain(" on 17R", readback);
            Assert.DoesNotContain(" on 35L", readback);
        }

        Assert.StartsWith($"Taxi via {fromTaxiway} {toTaxiway} {onward} HS 17R/35L at {fromTaxiway}", heldResult.Message);
    }

    /// <summary>
    /// Driven end to end, as OAK D/F is (<see cref="Issue1334OakDToFAcross33Tests.TaxiCDF_Cross33_CarriesTheAircraftAcrossOntoF"/>):
    /// the aircraft holds at the entry taxiway's 17R/35L bar, and <c>CROSS 35L</c> carries it straight over onto the other
    /// taxiway without stopping between the two bars.
    /// </summary>
    [Theory]
    [InlineData("A2", "C2", "C")]
    [InlineData("C2", "A2", "A")]
    public void CosA2C2_Cross35L_CarriesTheAircraftOntoTheOtherTaxiway(string fromTaxiway, string toTaxiway, string onward)
    {
        if ((CosStart(fromTaxiway, toTaxiway) is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-899-cos-a2-c2",
            ScenarioName = "COS A2/C2 across 17R/35L",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, $"TAXI {fromTaxiway} {toTaxiway} {onward}");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);

        AircraftState? holding = Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        output.WriteLine($"holding short of {hold.HoldShort.TargetName} at node #{hold.HoldShort.NodeId}");
        Assert.True(
            IsCosRunwayBarOnTaxiway(start.Layout, hold.HoldShort.NodeId, fromTaxiway),
            $"holding at #{hold.HoldShort.NodeId}, not {fromTaxiway}'s bar"
        );

        int farBarSegment = route.Segments.FindIndex(s => IsCosRunwayBarOnTaxiway(start.Layout, s.FromNodeId, toTaxiway));
        Assert.True(farBarSegment >= 0, $"the route never leaves {toTaxiway}'s 17R/35L bar");

        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);

        bool stoppedBetweenBars = false;
        AircraftState? past = Issue1334OakDToFAcross33Tests.TickUntil(
            output,
            engine,
            ac =>
            {
                bool pastFarBar = ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex > farBarSegment;
                stoppedBetweenBars |= !pastFarBar && (ac.Phases?.CurrentPhase is HoldingShortPhase);
                return pastFarBar;
            },
            300
        );

        Assert.False(stoppedBetweenBars, $"the aircraft held short again between {fromTaxiway}'s bar and {toTaxiway}'s bar");
        Assert.NotNull(past);

        // The clearance names no destination, so the route ends holding short of the onward taxiway past the far bar, with the
        // tail clear of the runway it crossed.
        HoldingShortPhase endHold = Assert.IsType<HoldingShortPhase>(past.Phases?.CurrentPhase);
        Assert.Equal(onward, endHold.HoldShort.TargetName);
        Assert.Null(endHold.HoldShort.TailOverRunwayNodeId);

        output.WriteLine(
            $"past {toTaxiway}'s bar: twy={past.Ground.CurrentTaxiway} gs={past.GroundSpeed:F1} phase={past.Phases?.CurrentPhase?.GetType().Name}"
        );
        Assert.Equal(toTaxiway, past.Ground.CurrentTaxiway);
    }

    /// <summary>
    /// <c>TAXI C2 A2 A HS A</c> across COS 17R/35L: the explicit hold short of A never binds A2's far 35L bar, where the route
    /// leaves the runway and A2's fillet onto A begins. The aircraft holds at C2's entry bar, crosses on <c>CROSS 35L</c>, and
    /// next holds short of A past the far bar, its tail clear of that runway's hold line (AIM 4-3-21.b).
    /// </summary>
    [Fact]
    public void CosC2A2_ExplicitHsA_HoldsShortOfAPastThe35LBar()
    {
        const string FromTaxiway = "C2";
        const string ToTaxiway = "A2";
        const string Onward = "A";
        if ((CosStart(FromTaxiway, ToTaxiway) is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("HoldShortAnnotator", LogLevel.Debug).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-1460-cos-c2-a2-hs-a",
            ScenarioName = "COS C2/A2 across 17R/35L, HS A",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, $"TAXI {FromTaxiway} {ToTaxiway} {Onward} HS {Onward}");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"segments: {string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId}({s.TaxiwayName})"))}");
        output.WriteLine($"holds: {string.Join(" ", route.HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}:{h.IsCleared}"))}");

        HoldShortPoint holdA = Assert.Single(route.HoldShortPoints, h => h.TargetName == Onward);
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, start.Layout.Nodes[holdA.NodeId].Type);

        AircraftState? holding = Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase entryHold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        output.WriteLine($"holding short of {entryHold.HoldShort.TargetName} at node #{entryHold.HoldShort.NodeId}");
        Assert.True(
            IsCosRunwayBarOnTaxiway(start.Layout, entryHold.HoldShort.NodeId, FromTaxiway),
            $"holding at #{entryHold.HoldShort.NodeId}, not {FromTaxiway}'s bar"
        );

        int farBarSegment = route.Segments.FindIndex(s => IsCosRunwayBarOnTaxiway(start.Layout, s.FromNodeId, ToTaxiway));
        Assert.True(farBarSegment >= 0, $"the route never leaves {ToTaxiway}'s 17R/35L bar");
        GroundNode farBar = start.Layout.Nodes[route.Segments[farBarSegment].FromNodeId];

        GroundNode onA = FirstRouteNodeOnTaxiwayFrom(start.Layout, route, farBarSegment, Onward);
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);

        HoldRun run = DriveToRestPastTheCrossing(engine, entryHold.HoldShort.NodeId, farBar, onA, 420);
        AircraftState atRest = Assert.IsType<AircraftState>(run.AtRest);
        HoldingShortPhase nextHold = Assert.IsType<HoldingShortPhase>(atRest.Phases?.CurrentPhase);
        Assert.Equal(Onward, nextHold.HoldShort.TargetName);

        double halfFt = AircraftLength.ResolveFt(atRest.AircraftType) / 2.0;
        double centrePastBarFt = GeoMath.DistanceNm(atRest.Position, farBar.Position) * GeoMath.FeetPerNm;
        bool pastTheBar = GeoMath.DistanceNm(atRest.Position, start.Node.Position) > GeoMath.DistanceNm(farBar.Position, start.Node.Position);
        double tailPastBarFt = pastTheBar ? centrePastBarFt - halfFt : -(centrePastBarFt + halfFt);
        bool wingtipNamesA = route.Warnings.Any(w =>
            w.StartsWith($"holding short of TWY {Onward} — wingtip clearance from ", StringComparison.Ordinal)
        );
        output.WriteLine(
            $"held {centrePastBarFt:F1} ft from far bar #{farBar.Id} ({(pastTheBar ? "past" : "behind")} it); tail {tailPastBarFt:F1} ft past it"
        );
        output.WriteLine($"warnings: {string.Join(" | ", route.Warnings)}");
        output.WriteLine($"wingtip warning names {Onward}: {wingtipNamesA}");
        Assert.Contains(route.Warnings, w => w.StartsWith($"holding short of TWY {Onward} — wingtip clearance from ", StringComparison.Ordinal));
        Assert.True(
            tailPastBarFt >= RunwayTailClearance.MarginFt - GroundNavigator.SetBackStopMarginFt,
            $"the tail ends {tailPastBarFt:F1} ft past {ToTaxiway}'s 35L bar #{farBar.Id}, inside {RunwayTailClearance.MarginFt:F0} ft"
        );

        // YAAT-1464: the crossing brakes onto the stop for A at taxi rate and rests there, its nose never over A's centreline.
        AssertRestsAtItsStop(atRest, nextHold.HoldShort, farBar, GroundNavigator.SetBackStopMarginFt);
        Assert.True(run.NosePastFt <= 0.0, $"the nose passed {Onward}'s centreline node #{onA.Id} by {run.NosePastFt:F1} ft");
        double maxLossKts = TaxiDecelPerSubTickKts(atRest) + 0.01;
        Assert.True(
            run.Losses.WorstKts <= maxLossKts,
            $"the aircraft lost {run.Losses.WorstKts:F2} kt in one sub-tick at {run.Losses.WorstAt}, more than the taxi rate's {maxLossKts:F2} kt"
        );
    }

    /// <summary>
    /// YAAT-1464: <c>TAXI C2 A2 A A3</c>, then <c>HS A</c> and <c>CROSS 35L</c> across COS 17R/35L. The hold short of A binds
    /// the first route node past A2's far bar, at the end of A2's fillet onto A, so the crossing's tail clearance never takes that
    /// fillet whole over the stop: the aircraft brakes onto the stop and holds short of A there.
    /// </summary>
    [Fact]
    public void CosC2A2_HsAOnARouteAcrossA_CrossesAndHoldsShortOfA()
    {
        if ((CosStart("C2", "A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-1464-cos-c2-a2-a-a3-hs-a-cross",
            ScenarioName = "COS C2/A2 across 17R/35L onto A, HS A, CROSS 35L",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI C2 A2 A A3");
        Assert.True(taxi.Success, taxi.Message);
        CommandResult hs = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HS A");
        Assert.True(hs.Success, hs.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"segments: {string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId}({s.TaxiwayName})"))}");
        output.WriteLine($"holds: {string.Join(" ", route.HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}"))}");

        AircraftState? holding = Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase entryHold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        Assert.True(
            IsCosRunwayBarOnTaxiway(start.Layout, entryHold.HoldShort.NodeId, "C2"),
            $"holding at #{entryHold.HoldShort.NodeId}, not C2's bar"
        );

        int farBarSegment = route.Segments.FindIndex(s => IsCosRunwayBarOnTaxiway(start.Layout, s.FromNodeId, "A2"));
        Assert.True(farBarSegment >= 0, "the route never leaves A2's 17R/35L bar");
        GroundNode farBar = start.Layout.Nodes[route.Segments[farBarSegment].FromNodeId];
        GroundNode onA = FirstRouteNodeOnTaxiwayFrom(start.Layout, route, farBarSegment, "A");

        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        HoldRun run = DriveToRestPastTheCrossing(engine, entryHold.HoldShort.NodeId, farBar, onA, 420);
        AircraftState atRest = Assert.IsType<AircraftState>(run.AtRest);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(atRest.Phases?.CurrentPhase);
        Assert.Equal("A", hold.HoldShort.TargetName);

        // The stop for A is placed off A2's fillet arc, on its chord; see YAAT-1469. The aircraft is taken at it within the
        // set-back take distance.
        AssertRestsAtItsStop(atRest, hold.HoldShort, farBar, TaxiingPhase.SetBackStopTakeFt);

        // The same off-arc stop (see YAAT-1469) makes the onward taxi brake at the firm rate onto it.
        double maxLossKts = FirmDecelPerSubTickKts(atRest) + 0.01;
        Assert.True(
            run.Losses.WorstKts <= maxLossKts,
            $"the aircraft lost {run.Losses.WorstKts:F2} kt in one sub-tick at {run.Losses.WorstAt}, more than the firm rate's {maxLossKts:F2} kt"
        );

        // Holding short of A past the crossing, the aircraft is no longer counted as using 35L.
        Assert.True(run.CountedWhileHoldingAt is null, $"holding short of A, still counted on 35L: {run.CountedWhileHoldingAt}");
    }

    /// <summary>
    /// What <see cref="DriveToRestPastTheCrossing"/> saw on its way to rest: the aircraft at rest, how far its nose got past the
    /// guard node, the most speed it lost in one sub-tick and where, and the first sub-tick it held short past the crossing still
    /// counted as using 35L.
    /// </summary>
    private sealed record HoldRun(AircraftState? AtRest, double NosePastFt, SpeedLossWatch Losses, string? CountedWhileHoldingAt);

    /// <summary>
    /// Drives the aircraft on physics sub-ticks from a <c>CROSS</c> of COS 35L until it rests holding short of a bar other than
    /// its entry bar <paramref name="entryHoldNodeId"/>. Records how far its nose ever got past <paramref name="guard"/>, measured
    /// along the way from <paramref name="farBar"/> to it, the most speed it lost in one sub-tick, the take of a stop from a
    /// crawl aside (<see cref="SpeedLossWatch"/>), and whether, holding short past the crossing, it was still counted as using
    /// 35L (<see cref="RunwayOccupancy.Classify"/>, <see cref="RunwayOccupancy.IsTailInsideRunwayState"/>).
    /// </summary>
    private HoldRun DriveToRestPastTheCrossing(SimulationEngine engine, int entryHoldNodeId, GroundNode farBar, GroundNode guard, int maxSeconds)
    {
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("COS", "35L"));
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Issue1334OakDToFAcross33Tests.Callsign));
        var way = new TrueHeading(GeoMath.BearingTo(farBar.Position, guard.Position));
        SpeedLossWatch losses = new(aircraft);
        double nosePastFt = double.NegativeInfinity;
        string? countedWhileHoldingAt = null;
        int subTick = 0;
        string last = "none";
        AircraftState? atRest = SubTickDriver.TickSubTicksUntil(
            engine,
            Issue1334OakDToFAcross33Tests.Callsign,
            ac => (ac.Phases?.CurrentPhase is HoldingShortPhase hs) && (hs.HoldShort.NodeId != entryHoldNodeId) && (ac.GroundSpeed < 0.5),
            maxSeconds,
            ac =>
            {
                subTick++;
                double toGuardFt = GeoMath.DistanceNm(ac.Position, guard.Position) * GeoMath.FeetPerNm;
                last = $"{ac.Phases?.CurrentPhase?.Name} gs={ac.GroundSpeed:F2} {toGuardFt:F1} ft from #{guard.Id}";
                LatLon nose = SegmentExpander.ProjectAlong(ac.Position, ac.TrueHeading.Degrees, AircraftLength.ResolveFt(ac.AircraftType) / 2.0);
                nosePastFt = Math.Max(nosePastFt, GeoMath.AlongTrackDistanceNm(nose, guard.Position, way) * GeoMath.FeetPerNm);
                losses.Sample();
                bool holdingPast = (ac.Phases?.CurrentPhase is HoldingShortPhase held) && (held.HoldShort.NodeId != entryHoldNodeId);
                if (!holdingPast)
                {
                    return;
                }

                RunwayUseKind? use = RunwayOccupancy.Classify(ac, runway, ac.Ground.Layout)?.Kind;
                if ((use is not null) || RunwayOccupancy.IsTailInsideRunwayState(ac))
                {
                    countedWhileHoldingAt ??= $"sub-tick {subTick}: counted as {use?.ToString() ?? "tail inside"}";
                }
            }
        );
        output.WriteLine($"drive: nose past #{guard.Id} at most {nosePastFt:F1} ft; worst sub-tick loss {losses.WorstKts:F2} kt at {losses.WorstAt}");
        output.WriteLine($"drive: last sub-tick {subTick}: {last}");
        return new HoldRun(atRest, nosePastFt, losses, countedWhileHoldingAt);
    }

    /// <summary>
    /// The aircraft at rest is within <paramref name="withinFt"/> of <paramref name="hold"/>'s placed stop, its centre measured
    /// along the route's way from <paramref name="farBar"/> to the stop.
    /// </summary>
    private void AssertRestsAtItsStop(AircraftState atRest, HoldShortPoint hold, GroundNode farBar, double withinFt)
    {
        if ((hold.Latitude is not { } lat) || (hold.Longitude is not { } lon))
        {
            Assert.Fail($"the hold short of {hold.TargetName} has no placed stop");
            return;
        }

        // Along the route while it still reaches the stop (along a fillet arc it lies on, though its point may lie off it), else
        // along the way from the far bar to the stop.
        var stop = new LatLon(lat, lon);
        double toStopFt =
            (atRest.Ground.AssignedTaxiRoute is { } route) && (atRest.Ground.Layout is { } layout)
                ? TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, atRest.Position, hold)
                : double.PositiveInfinity;
        double pastStopFt = double.IsFinite(toStopFt)
            ? -toStopFt
            : GeoMath.AlongTrackDistanceNm(atRest.Position, stop, new TrueHeading(GeoMath.BearingTo(farBar.Position, stop))) * GeoMath.FeetPerNm;
        output.WriteLine($"at rest {pastStopFt:F2} ft past the stop for {hold.TargetName} (#{hold.NodeId})");
        Assert.True(
            Math.Abs(pastStopFt) <= withinFt,
            $"at rest {pastStopFt:F1} ft past the stop for {hold.TargetName}, not within {withinFt:F0} ft of it"
        );
    }

    /// <summary>
    /// The first route node, from segment <paramref name="fromSegment"/> on, that a straight edge of <paramref name="taxiway"/> touches.
    /// </summary>
    private static GroundNode FirstRouteNodeOnTaxiwayFrom(AirportGroundLayout layout, TaxiRoute route, int fromSegment, string taxiway)
    {
        GroundNode? node = route
            .Segments.Skip(fromSegment)
            .Select(s => layout.Nodes[s.ToNodeId])
            .FirstOrDefault(n => n.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway)));
        Assert.NotNull(node);
        return node;
    }

    /// <summary>The speed (kt) <paramref name="aircraft"/>'s taxi brake rate takes off in one physics sub-tick.</summary>
    private static double TaxiDecelPerSubTickKts(AircraftState aircraft) =>
        CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType)) / SimulationEngine.PhysicsSubTickRate;

    /// <summary>
    /// The speed (kt) <paramref name="aircraft"/>'s firm stop rate takes off in one physics sub-tick: the max-effort rate
    /// <see cref="GroundStopBraking"/> brakes at when the taxi rate no longer stops it in time.
    /// </summary>
    private static double FirmDecelPerSubTickKts(AircraftState aircraft) =>
        CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType)) / SimulationEngine.PhysicsSubTickRate;

    /// <summary>
    /// <c>TAXI C2 A2 A A3</c> across COS 17R/35L, then <c>HS A</c>: the route drives A past A2's far 35L bar, so the hold short
    /// of A is planned by walking the route's nodes, and it never binds that bar (where the route leaves the runway); it binds
    /// the first route node past it (AIM 4-3-21.b).
    /// </summary>
    [Fact]
    public void CosC2A2_HsAOnARouteAcrossA_BindsPastThe35LBar()
    {
        if ((CosStart("C2", "A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-1460-cos-c2-a2-a-a3-hs-a",
            ScenarioName = "COS C2/A2 across 17R/35L onto A, then HS A",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI C2 A2 A A3");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute taxied = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"segments: {string.Join(" ", taxied.Segments.Select(s => $"{s.FromNodeId}->{s.ToNodeId}({s.TaxiwayName})"))}");
        output.WriteLine($"holds: {string.Join(" ", taxied.HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}"))}");
        int farBarSegment = taxied.Segments.FindIndex(s => IsCosRunwayBarOnTaxiway(start.Layout, s.ToNodeId, "A2"));
        Assert.True(farBarSegment >= 0, "the route never reaches A2's 17R/35L bar");
        Assert.True(farBarSegment + 1 < taxied.Segments.Count, "the route ends at A2's 17R/35L bar");
        Assert.DoesNotContain(taxied.HoldShortPoints, h => h.TargetName == "A");

        CommandResult hs = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HS A");
        output.WriteLine($"HS A: {hs.Success} — {hs.Message}");
        Assert.True(hs.Success, hs.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"holds: {string.Join(" ", route.HoldShortPoints.Select(h => $"{h.NodeId}:{h.Reason}:{h.TargetName}"))}");

        HoldShortPoint holdA = Assert.Single(route.HoldShortPoints, h => h.TargetName == "A");
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, start.Layout.Nodes[holdA.NodeId].Type);
        int barSegment = route.Segments.FindIndex(s => IsCosRunwayBarOnTaxiway(start.Layout, s.ToNodeId, "A2"));
        Assert.True(barSegment >= 0, "the held route never reaches A2's 17R/35L bar");
        Assert.Equal(route.Segments[barSegment + 1].ToNodeId, holdA.NodeId);
    }

    private sealed record TaxiStart(AirportGroundLayout Layout, GroundNode Node, double HeadingDeg, GroundNode FromJoin, GroundNode ToJoin);

    /// <summary>The far node of SFO C3 from its 28R join, nosed toward the join.</summary>
    private TaxiStart? SfoC3Start()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "28R");
        (GroundNode start, GroundNode join) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "SFO", "C3", runway);
        GroundNode s2Join = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "SFO", "S2", runway).Join;
        return new TaxiStart(layout, start, GeoMath.BearingTo(start.Position, join.Position), join, s2Join);
    }

    /// <summary>The far node of COS <paramref name="fromTaxiway"/> from its 17R/35L join, nosed toward the join.</summary>
    private TaxiStart? CosStart(string fromTaxiway, string toTaxiway)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return null;
        }

        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "35L");
        output.WriteLine($"COS 35L centreline: {runway}");
        (GroundNode start, GroundNode fromJoin) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", fromTaxiway, runway);
        GroundNode toJoin = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", toTaxiway, runway).Join;
        return new TaxiStart(layout, start, GeoMath.BearingTo(start.Position, fromJoin.Position), fromJoin, toJoin);
    }

    /// <summary>
    /// <paramref name="navDb"/> reading a KCOS sidecar the test declares, one <c>crossingPairs</c> entry over the real layout,
    /// in place of the shipped sidecars (which declare nothing for KCOS).
    /// </summary>
    internal static NavigationDatabase CosDeclaring(NavigationDatabase navDb, string firstTaxiway, string secondTaxiway, string runway) =>
        navDb.WithAirportSidecars(
            new AirportSidecarCatalog([
                new AirportSidecar("KCOS") { CrossingPairs = [new CrossingPair(firstTaxiway, secondTaxiway, runway, "declared by the test")] },
            ])
        );

    private static IEnumerable<int> RouteNodeIds(TaxiRoute? route) =>
        route is not { Segments.Count: > 0 } ? [] : route.Segments.Select(s => s.FromNodeId).Append(route.Segments[^1].ToNodeId);

    private static bool IsCosRunwayBarOnTaxiway(AirportGroundLayout layout, int nodeId, string taxiway) =>
        layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
        && (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId?.Contains("35L") == true)
        && node.Edges.Any(e => e.MatchesTaxiway(taxiway));

    /// <summary>How the aircraft's own callsign is spoken, read off a builder that speaks nothing but it before its report.</summary>
    private static string SpokenCallsign(AircraftState aircraft)
    {
        const string Report = ", unable to exit the runway.";
        string tts = PilotResponder.BuildUnableToExitRequestBackTaxi(aircraft, null).Tts;
        Assert.EndsWith(Report, tts);
        return tts[..^Report.Length];
    }
}
