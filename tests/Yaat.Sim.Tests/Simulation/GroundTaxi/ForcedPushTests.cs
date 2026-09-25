using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>PUSHF</c> / <c>PUSHMF</c>: the forced <c>PUSH</c> / <c>PUSHM</c> (docs/ground/pushback.md). Forcing skips the
/// taxiway parts of the flown-path check, the parked-neighbour sweep, the alley clearance and the overswing filter at plan
/// time, and the detector's stop for parked aircraft at fly time; it keeps the runway and holding-position rules, the
/// outright refusals, the start-overlap refusal, the pass-through hints and the yield to moving traffic. A plain push the
/// forced form would get past says so in its refusal.
/// </summary>
public partial class ForcedPushTests(ITestOutputHelper output)
{
    private const string Pusher = "UAL1";
    private const string Parked = "PRK1";
    private const string Narrowbody = "B738";

    /// <summary>A generous budget for any tow here to finish, seconds.</summary>
    private const int TowBudgetSeconds = 600;

    // ─── 1. Parsing ───

    [Theory]
    [InlineData("PUSHF $7A/PULL", "PUSHF $7A/PULL")]
    [InlineData("PUSHF A FACE N", "PUSHF A FACE N")]
    [InlineData("PUSHF", "PUSHF")]
    [InlineData("PUSHF ~37.615230/-122.386040/090", "PUSHF ~37.615230/-122.386040/090")]
    public void Pushf_ParsesToAForcedPushbackCommand_AndDescribesBackToItself(string text, string canonical)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);
        PushbackCommand push = Assert.IsType<PushbackCommand>(parsed.Value);
        Assert.True(push.Forced, $"'{text}' parsed unforced");
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(push));
        Assert.Equal(CanonicalCommandType.ForcedPushback, CommandDescriber.ToCanonicalType(push));
    }

    [Fact]
    public void Pushf_KeepsEveryPartOfThePushForm()
    {
        PushbackCommand pull = Assert.IsType<PushbackCommand>(CommandParser.Parse("PUSHF $7A/PULL").Value);
        Assert.Equal("7A", pull.Destination?.Spot);
        Assert.Equal(PushbackLegKind.Pull, pull.Destination?.ForcedKind);

        PushbackCommand onto = Assert.IsType<PushbackCommand>(CommandParser.Parse("PUSHF A FACE N").Value);
        Assert.Equal("A", onto.Taxiway);
        Assert.NotNull(onto.MagneticHeading);

        PushbackCommand marked = Assert.IsType<PushbackCommand>(CommandParser.Parse("PUSHF ~37.615230/-122.386040/090").Value);
        Assert.NotNull(marked.Destination?.FreePose);

        Assert.Equal(new PushbackCommand(null, null, null, null, true), CommandParser.Parse("PUSHF").Value);
    }

    [Fact]
    public void Pushmf_ParsesToAForcedTugMove_AndDescribesBackToItself()
    {
        PushbackMultiCommand move = Assert.IsType<PushbackMultiCommand>(CommandParser.Parse("PUSHMF @F8 $7A $7B FACE E").Value);
        Assert.True(move.Forced);
        Assert.Equal(["@F8", "$7A", "$7B"], move.Targets);
        Assert.NotNull(move.FinalFacing);
        Assert.Equal("PUSHMF @F8 $7A $7B FACE E", CommandDescriber.DescribeCommand(move));
        Assert.Equal(CanonicalCommandType.ForcedPushbackMulti, CommandDescriber.ToCanonicalType(move));
    }

    [Theory]
    [InlineData("PUSH $7A/PULL")]
    [InlineData("PUSH A FACE N")]
    [InlineData("PUSH")]
    public void Push_ParsesUnforced(string text) => Assert.False(Assert.IsType<PushbackCommand>(CommandParser.Parse(text).Value).Forced);

    [Fact]
    public void Pushm_ParsesUnforced() => Assert.False(Assert.IsType<PushbackMultiCommand>(CommandParser.Parse("PUSHM $7A $7B").Value).Forced);

    // ─── 2. A parked neighbour inside the sweep floor ───

    /// <summary>
    /// SFO F5, a B738 with a B738 parked on F6: its push-off alone comes within 22.8 ft of the neighbour, so <c>PUSH $7A</c>
    /// is refused, and suggests <c>PUSHF $7A</c>. <c>PUSHF $7A</c> is accepted with the same pilot readback, notes how
    /// close it passes and that it will not stop for parked aircraft, and flies to the spot without the detector ever
    /// braking it for the neighbour.
    /// </summary>
    [Fact]
    public void PushPastAParkedNeighbour_PlainRefusesAndSuggestsPushf_PushfFliesPastIt()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState parked = SfoGroundHarness.SpawnParked(ground, Parked, Narrowbody, "F6");
        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "F5");

        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH $7A");
        output.WriteLine($"PUSH $7A: {plain.Success} \"{plain.Message}\"");
        Assert.False(plain.Success);
        Assert.Contains($"would swing into {Parked}", plain.Message);
        Assert.EndsWith(". To force it: PUSHF $7A", plain.Message);

        string path = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "pushf", "f5-7a-forced.json");
        double closestFt = double.MaxValue;
        int second;
        using (TickRecorder.Attach(ground.Engine, path, Pusher, Parked))
        {
            CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $7A");
            output.WriteLine($"PUSHF $7A: {forced.Success} \"{forced.Message}\"; moves {Describe(QueuedMoves(pusher))}");
            Assert.True(forced.Success, forced.Message);
            Match passes = PassesNote().Match(forced.Message ?? "");
            Assert.True(passes.Success, forced.Message);

            // Measured 2026-09-25: the neighbour ranking step keeps the plan 2.4 ft from PRK1 (the most any candidate
            // keeps is 7.2 ft, within the 5 ft margin); unranked it came 0.85 ft from it, noted "passes 0 ft".
            Assert.InRange(int.Parse(passes.Groups["ft"].Value, CultureInfo.InvariantCulture), 2, 7);
            Assert.Contains("(forced: will not stop for parked aircraft)", forced.Message);
            Assert.Equal(PlainReadback("F5", "PUSH $7A"), forced.PilotReadback);
            Assert.DoesNotContain("forced", forced.PilotReadback?.Tts ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("forced", forced.PilotReadback?.Terminal ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(pusher.Ground.ForcedTowIgnoresParked);

            second = SfoGroundHarness.TickUntil(
                ground.Engine,
                () => pusher.Phases?.CurrentPhase is not PushbackPhase,
                TowBudgetSeconds,
                _ =>
                {
                    Assert.NotEqual(Parked, pusher.Ground.AutoYieldTarget);
                    Assert.False(pusher.Ground.SpeedLimit is <= 0.0, $"the forced tow was held at a zero limit ({pusher.Ground.AutoYieldTarget})");
                    bool noseFirst = pusher.Phases?.CurrentPhase is PushbackPhase { Move.Kind: PushbackLegKind.Pull };
                    closestFt = Math.Min(closestFt, GroundOutline.ClearanceBetween(pusher, noseFirst, parked));
                }
            );
        }

        output.WriteLine($"arrived at t={second}s, closest {closestFt:F1} ft from {Parked}");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.False(pusher.Ground.ForcedTowIgnoresParked);

        // Forcing ranks the plans that keep the most room to the neighbour first: never through it. Flown 2.7 ft (measured
        // 2026-09-25, sampled once a second).
        Assert.True(closestFt > 0.0, $"the forced tow's outline came {closestFt:F1} ft from {Parked}");
    }

    /// <summary>
    /// The forced neighbour ranking step: candidates that keep the floor win outright; else a positive pass beats an
    /// overlap and only those within the 5 ft margin of the most room any keeps go on to the usual keys; when every one
    /// overlaps, all of them do.
    /// </summary>
    [Fact]
    public void NeighbourRankingStep_ShortlistsByFloorThenRoom()
    {
        TugNeighbourClearance keeps = new(true, 20.0);
        TugNeighbourClearance wide = new(false, 12.0);
        TugNeighbourClearance nearWide = new(false, 7.5);
        TugNeighbourClearance narrow = new(false, 6.9);
        TugNeighbourClearance touching = new(false, 0.0);

        Assert.Equal([keeps], Shortlist(wide, keeps, touching));
        Assert.Equal([wide, nearWide], Shortlist(wide, nearWide, narrow, touching));
        Assert.Equal([narrow], Shortlist(touching, narrow));
        Assert.Equal([touching, touching], Shortlist(touching, touching));
        Assert.Empty(Shortlist());

        // Measured from the best, not candidate to candidate: 12 → 7.5 → 6.9 never chains 6.9 in.
        Assert.DoesNotContain(narrow, Shortlist(narrow, nearWide, wide));
    }

    private static List<TugNeighbourClearance> Shortlist(params TugNeighbourClearance[] candidates) =>
        TugNeighbourClearance.Shortlist(candidates, c => c);

    // ─── 3. The detector at fly time ───

    /// <summary>
    /// OAK gate 25 <c>PUSH TE</c> with a B738 parked crossways on the push line 230 ft back: the plain push stalls short
    /// of it (<c>TugMoveParkedNeighbourTests.PushIntoAircraftParkedAcrossThePushLine_StopsShortOfItAndShowsIt</c>);
    /// <c>PUSHF TE</c> is never braked for it and completes.
    /// </summary>
    [Fact]
    public void ForcedPushIntoAircraftParkedAcrossThePushLine_Completes()
    {
        if (BuildEngine("OAK") is not { } built)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = built;
        AircraftState pusher = SpawnOnStand(engine, layout, Pusher, "25");
        double pushDeg = new TrueHeading(pusher.TrueHeading.Degrees + 180.0).Degrees;
        LatLon blockPosition = GeoMath.ProjectPoint(pusher.Position, new TrueHeading(pushDeg), 230.0 / GeoMath.FeetPerNm);
        Spawn(engine, layout, Parked, (blockPosition, pushDeg + 90.0), new AtParkingPhase());

        CommandResult push = engine.SendCommand(Pusher, "PUSHF TE");
        output.WriteLine($"PUSHF TE: \"{push.Message}\"");
        Assert.True(push.Success, push.Message);

        // A bare push onto TE has the one shape, and it runs through the aircraft parked across the line.
        Assert.Contains($"(forced: overlaps {Parked})", push.Message);
        Assert.DoesNotContain("forced: passes", push.Message);

        int second = SfoGroundHarness.TickUntil(
            engine,
            () => pusher.Phases?.CurrentPhase is not PushbackPhase,
            TowBudgetSeconds,
            _ => Assert.NotEqual(Parked, pusher.Ground.AutoYieldTarget)
        );
        output.WriteLine($"completed at t={second}s");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
    }

    // ─── 5. A taxiway the plain push may not enter ───

    /// <summary>
    /// SFO B25 <c>PUSH $1</c> would put a B738 on taxiway Y, so it is refused with the suggestion; <c>PUSHF $1</c> is
    /// accepted and notes the taxiway it enters.
    /// </summary>
    [Fact]
    public void PushOntoATaxiwayItWasNotSentTo_PlainRefusesAndSuggests_PushfNotesTheTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH $1");
        output.WriteLine($"PUSH $1: \"{plain.Message}\"");
        Assert.False(plain.Success);
        Assert.Equal("Unable, the move to spot 1 would put the aircraft on taxiway Y. To force it: PUSHF $1", plain.Message);

        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $1");
        output.WriteLine($"PUSHF $1: \"{forced.Message}\"");
        Assert.True(forced.Success, forced.Message);
        Assert.Contains("(forced: tow enters taxiway Y, coordinate with ground)", forced.Message);
        Assert.DoesNotContain("will not stop for parked aircraft", forced.Message);
    }

    // ─── 6 and 10. What forcing does not get past ───

    /// <summary>
    /// A target on a runway holding position is refused alike by <c>PUSH</c> and <c>PUSHF</c>, and the plain refusal
    /// suggests nothing, since forcing cannot get past it.
    /// </summary>
    [Fact]
    public void TargetOnARunwayHoldingPosition_RefusedAlike_NoSuggestion()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        GroundNode hold = ground
            .Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
            .MinBy(n => DistanceFt(n.Position, pusher.Position))!;
        output.WriteLine($"hold-short node {hold.Id} is {DistanceFt(hold.Position, pusher.Position):F0} ft from B25");

        CommandResult plain = ground.Engine.SendCommand(Pusher, $"PUSH #{hold.Id}");
        CommandResult forced = ground.Engine.SendCommand(Pusher, $"PUSHF #{hold.Id}");
        output.WriteLine($"PUSH: \"{plain.Message}\"; PUSHF: \"{forced.Message}\"");
        Assert.False(plain.Success);
        Assert.False(forced.Success);
        Assert.Contains("runway holding position", plain.Message);
        Assert.Equal(plain.Message, forced.Message);
        Assert.DoesNotContain("To force it", plain.Message);
    }

    /// <summary>A target past the 2,000 ft guard is refused by <c>PUSHF</c> too.</summary>
    [Fact]
    public void TargetPastTheSanityGuard_PushfRefuses()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        GroundNode far = ground
            .Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Spot)
            .First(n => DistanceFt(n.Position, pusher.Position) is > 2200.0 and < 4000.0);

        CommandResult forced = ground.Engine.SendCommand(Pusher, $"PUSHF #{far.Id}");
        output.WriteLine($"PUSHF #{far.Id}: \"{forced.Message}\"");
        Assert.False(forced.Success);
        Assert.Contains("sanity guard", forced.Message);
    }

    /// <summary>Outlines already overlapping at the start are refused by <c>PUSHF</c> too, naming both aircraft.</summary>
    [Fact]
    public void OutlinesOverlappingAtTheStart_PushfRefuses()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "F5");
        LatLon beside = GeoMath.ProjectPoint(pusher.Position, new TrueHeading(pusher.TrueHeading.Degrees + 90.0), 20.0 / GeoMath.FeetPerNm);
        Spawn(ground.Engine, ground.Layout, Parked, (beside, pusher.TrueHeading.Degrees), new AtParkingPhase());

        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF");
        output.WriteLine($"PUSHF: \"{forced.Message}\"");
        Assert.False(forced.Success);
        Assert.Contains("their outlines overlap", forced.Message);
    }

    // ─── 7. PUSHMF keeps the pass-through hints ───

    /// <summary>SFO F8 <c>PUSHMF $7A $7B</c> flies the same moves as <c>PUSHM $7A $7B</c>: forcing never skips a hint.</summary>
    [Fact]
    public void PushmfThroughTwoSpots_FliesThePushmPlan()
    {
        IReadOnlyList<TugMove>? plain = InstalledMoves("F8", "PUSHM $7A $7B");
        IReadOnlyList<TugMove>? forced = InstalledMoves("F8", "PUSHMF $7A $7B");
        if ((plain is null) || (forced is null))
        {
            return;
        }

        Assert.Equal(Describe(plain), Describe(forced));
    }

    // ─── 8. Phase-state refusals ───

    /// <summary>An aircraft holding on a taxiway is not at a stand; <c>PUSHF</c> is refused, with no suggestion.</summary>
    [Fact]
    public void PushfToAnAircraftNotAtAStand_RefusedWithNoSuggestion()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnAtJunction(ground, Pusher, Narrowbody, "A", "F1");
        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH");
        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF");
        output.WriteLine($"PUSH: \"{plain.Message}\"; PUSHF: \"{forced.Message}\"");
        Assert.False(plain.Success);
        Assert.False(forced.Success);
        Assert.DoesNotContain("To force it", plain.Message);
        Assert.DoesNotContain("To force it", forced.Message);
    }

    // ─── 9. The detector flag's lifecycle ───

    [Fact]
    public void ForcedTowFlag_SetOnInstall_ClearedOnCompletion()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "F8");
        Assert.True(ground.Engine.SendCommand(Pusher, "PUSHF $7A").Success);
        Assert.True(pusher.Ground.ForcedTowIgnoresParked);

        SfoGroundHarness.TickUntil(ground.Engine, () => pusher.Phases?.CurrentPhase is not PushbackPhase, TowBudgetSeconds, null);
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.False(pusher.Ground.ForcedTowIgnoresParked);
    }

    [Theory]
    [InlineData("TAXI A")]
    [InlineData("PUSHM $6A $6B")]
    public void ForcedTowFlag_ClearedByACommandThatClearsTheTow(string command)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "D15");
        Assert.True(ground.Engine.SendCommand(Pusher, "PUSHF $6A").Success);
        for (int i = 0; i < 20; i++)
        {
            ground.Engine.TickOneSecond();
        }

        // Sent each second of the tow until it is taken: a TAXI only routes once the tow has brought the aircraft near
        // the taxiway (SfoPushRouteE2ETests.TaxiMidMove_DropsEveryQueuedLeg waits for the same cue).
        CommandResult? result = null;
        for (int second = 0; (second < TowBudgetSeconds) && (pusher.Phases?.CurrentPhase is PushbackPhase); second++)
        {
            Assert.True(pusher.Ground.ForcedTowIgnoresParked);
            result = ground.Engine.SendCommand(Pusher, command);
            if (result.Success)
            {
                break;
            }

            ground.Engine.TickOneSecond();
        }

        output.WriteLine($"{command} mid-tow: \"{result?.Message}\"");
        Assert.True(result?.Success, result?.Message);
        Assert.False(pusher.Ground.ForcedTowIgnoresParked);
    }

    [Fact]
    public void ForcedTowFlag_RoundTripsASnapshot()
    {
        var ground = new AircraftGroundOps { ForcedTowIgnoresParked = true };
        AircraftGroundOpsDto dto = ground.ToSnapshot();
        AircraftGroundOpsDto json = JsonSerializer.Deserialize<AircraftGroundOpsDto>(JsonSerializer.Serialize(dto))!;
        Assert.True(AircraftGroundOps.FromSnapshot(json, layout: null).ForcedTowIgnoresParked);
        Assert.False(AircraftGroundOps.FromSnapshot(new AircraftGroundOps().ToSnapshot(), layout: null).ForcedTowIgnoresParked);
    }

    // ─── The geometric fallback ───

    /// <summary>
    /// SFO D11 <c>PUSH $6A</c>: no template lines a B738 up on 6A from D11, so the plain push is refused ("cannot line up");
    /// <c>PUSHF $6A</c> takes the geometric fallback — a tow to a point on 6A's approach line, then the move onto the
    /// line — and ends on the spot. The tick playback is recorded under <c>.tmp/pushf/</c> for rendering.
    /// </summary>
    [Fact]
    public void NoTemplateFits_PushfTakesTheGeometricFallbackOntoTheSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "D11");
        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH $6A");
        output.WriteLine($"PUSH $6A: \"{plain.Message}\"");
        Assert.False(plain.Success);
        Assert.Equal("Unable, cannot line up on spot 6A from here. To force it: PUSHF $6A", plain.Message);

        string path = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "pushf", "d11-pushf-6a.json");
        List<TugMove> moves;
        double towedFt = 0.0;
        using (TickRecorder.Attach(ground.Engine, path, Pusher))
        {
            CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $6A");
            moves = QueuedMoves(pusher);
            output.WriteLine($"PUSHF $6A: \"{forced.Message}\"; moves {Describe(moves)}");
            Assert.True(forced.Success, forced.Message);
            LatLon last = pusher.Position;
            SfoGroundHarness.TickUntil(
                ground.Engine,
                () => pusher.Phases?.CurrentPhase is not PushbackPhase,
                TowBudgetSeconds,
                _ =>
                {
                    towedFt += DistanceFt(last, pusher.Position);
                    last = pusher.Position;
                }
            );
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName("6A")!;
        double restFt = DistanceFt(pusher.Position, spot.Position);
        output.WriteLine($"towed {towedFt:F0} ft, rests {restFt:F1} ft from spot 6A's mark, heading {pusher.TrueHeading.Degrees:F1}");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.InRange(restFt, 0.0, TugMovePlanner.FuselageLengthFt(Narrowbody));

        // The fallback ranks by tow length first: the 828 ft pull-side tow (Push Straight 65, Push ToPoint 572, Pull
        // ViaLine 191, planned lengths measured 2026-09-25), not the longer tow whose lead-in departs 6A's line least.
        Assert.Equal("Push Straight, Push ToPoint, Pull ViaLine", Describe(moves));
        Assert.InRange(moves[0].StraightDistanceFt, 64.0, 66.0);
        Assert.InRange(towedFt, 790.0, 870.0);
    }

    // ─── The suggestion names only a forced form that would be installed ───

    /// <summary>
    /// SFO B25 with a B738 placed 20 ft abeam on the right, same heading, so the two outlines overlap where UAL1 stands:
    /// <c>PUSH $1</c> is refused by the planner for taxiway Y, and <c>PUSHF $1</c> would plan but is then refused for the
    /// start overlap, so the plain refusal suggests nothing.
    /// </summary>
    [Fact]
    public void PlainRefusalWhoseForcedFormOverlapsAtTheStart_SuggestsNothing()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        LatLon beside = GeoMath.ProjectPoint(pusher.Position, new TrueHeading(pusher.TrueHeading.Degrees + 90.0), 20.0 / GeoMath.FeetPerNm);
        Spawn(ground.Engine, ground.Layout, Parked, (beside, pusher.TrueHeading.Degrees), new AtParkingPhase());

        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH $1");
        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $1");
        output.WriteLine($"PUSH $1: \"{plain.Message}\"; PUSHF $1: \"{forced.Message}\"");
        Assert.False(plain.Success);
        Assert.Equal("Unable, the move to spot 1 would put the aircraft on taxiway Y", plain.Message);
        Assert.False(forced.Success);
        Assert.Contains("their outlines overlap", forced.Message);
    }

    /// <summary>
    /// SFO F5 with a B738 parked on F6: <c>PUSHM $7A $7B</c> would swing into it, so it is refused and suggests
    /// <c>PUSHMF $7A $7B</c>.
    /// </summary>
    [Fact]
    public void RefusedPushm_SuggestsPushmf()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Parked, Narrowbody, "F6");
        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "F5");
        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSHM $7A $7B");
        output.WriteLine($"PUSHM $7A $7B: \"{plain.Message}\"");
        Assert.False(plain.Success);
        Assert.Equal($"Unable, the move to spot 7A would swing into {Parked}. To force it: PUSHMF $7A $7B", plain.Message);
    }

    // ─── The notes on a forced plan ───

    /// <summary>SFO B20 <c>PUSHF $1</c> crosses taxiways M1 and Y on its way to the spot: each is noted, once.</summary>
    [Fact]
    public void ForcedTowEnteringTwoTaxiways_NotesEach()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B20");
        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $1");
        output.WriteLine($"PUSHF $1: \"{forced.Message}\"");
        Assert.True(forced.Success, forced.Message);
        foreach (string taxiway in new[] { "M1", "Y" })
        {
            string note = $"(forced: tow enters taxiway {taxiway}, coordinate with ground)";
            Assert.Single(Regex.Matches(forced.Message ?? "", Regex.Escape(note)));
        }
    }

    /// <summary>
    /// The forced notes for the alley clearance, the overshoot past a taxiway pushed onto and the overswing filter, on the
    /// real cases that break them (measured 2026-09-25): F20 <c>PUSHF $8</c> reaches its left wing into A with a 202°
    /// swing where the lane turns 166°; C8 <c>PUSHF Y FACE S</c> runs 64 ft past Y; B25 <c>PUSHF $1</c> noses into Y.
    /// </summary>
    [Theory]
    [InlineData("F20", "PUSHF $8", "(forced: left wing fouls taxiway A, coordinate with ground)")]
    [InlineData("F20", "PUSHF $8", "(forced: nose swings 202°, lane needs 166°)")]
    [InlineData("C8", "PUSHF Y FACE S", "(forced: tow runs past taxiway Y, coordinate with ground)")]
    [InlineData("B25", "PUSHF $1", "(forced: nose fouls taxiway Y, coordinate with ground)")]
    public void ForcedPlan_NotesTheRuleItBreaks(string stand, string command, string note)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, stand);
        CommandResult forced = ground.Engine.SendCommand(Pusher, command);
        output.WriteLine($"{stand} {command}: \"{forced.Message}\"");
        Assert.True(forced.Success, forced.Message);
        Assert.Contains(note, forced.Message);
    }

    /// <summary>
    /// A parked neighbour is noted once however many stretches of a forced tow pass it, at the closest any comes, an
    /// overlap outranking every pass; every other override is noted once.
    /// </summary>
    [Fact]
    public void ForcedOverrides_ANeighbourIsNotedOnceAtTheClosest()
    {
        List<TugForcedOverride> overrides = [];
        TugPlanBuilder.AddOverride(overrides, new TugForcedPassesNeighbour(Parked, 6.0));
        TugPlanBuilder.AddOverride(overrides, new TugForcedEntersTaxiway("A"));
        TugPlanBuilder.AddOverride(overrides, new TugForcedPassesNeighbour(Parked, 2.5));
        TugPlanBuilder.AddOverride(overrides, new TugForcedPassesNeighbour(Parked, 4.0));
        TugPlanBuilder.AddOverride(overrides, new TugForcedEntersTaxiway("A"));
        TugPlanBuilder.AddOverride(overrides, new TugForcedPassesNeighbour("PRK2", 3.0));
        Assert.Equal<TugForcedOverride>(
            [new TugForcedPassesNeighbour(Parked, 2.5), new TugForcedEntersTaxiway("A"), new TugForcedPassesNeighbour("PRK2", 3.0)],
            overrides
        );

        TugPlanBuilder.AddOverride(overrides, new TugForcedPassesNeighbour(Parked, 0.0));
        Assert.Equal(new TugForcedPassesNeighbour(Parked, 0.0), overrides[0]);
        Assert.Equal(3, overrides.Count);
    }

    // ─── Forcing keeps the runway and holding-position rules on the flown path ───

    /// <summary>
    /// SFO B25 <c>PUSH $35</c>: the tow to spot 35 would put a B738 on runway 1L/19R. The flown-path check refuses
    /// <c>PUSHF $35</c> alike, and the plain refusal suggests nothing.
    /// </summary>
    [Fact]
    public void ForcedPushWhosePathCrossesARunway_RefusedAlike_NoSuggestion()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        CommandResult plain = ground.Engine.SendCommand(Pusher, "PUSH $35");
        CommandResult forced = ground.Engine.SendCommand(Pusher, "PUSHF $35");
        output.WriteLine($"PUSH $35: \"{plain.Message}\"; PUSHF $35: \"{forced.Message}\"");
        Assert.False(forced.Success);
        Assert.Equal("Unable, the move to spot 35 would put the aircraft on runway 1L - 19R", forced.Message);
        Assert.Equal(forced.Message, plain.Message);
    }

    /// <summary>
    /// SFO B25 toward the taxiway nodes beside the four runway holding positions nearest it: every one whose flown path
    /// reaches a holding position (the target itself is not one) is refused alike by <c>PUSH</c> and <c>PUSHF</c>, with
    /// no suggestion; at least one does (node 1433 beside hold 884, measured 2026-09-25).
    /// </summary>
    [Fact]
    public void ForcedPushWhosePathReachesAHoldingPosition_RefusedAlike_NoSuggestion()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "B25");
        IEnumerable<GroundNode> besideHolds = ground
            .Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
            .OrderBy(n => DistanceFt(n.Position, pusher.Position))
            .Take(4)
            .SelectMany(hold => hold.Edges.SelectMany(e => e.Nodes))
            .Where(n => n.Type != GroundNodeType.RunwayHoldShort)
            .Distinct();
        int reached = 0;
        foreach (GroundNode node in besideHolds)
        {
            CommandResult plain = ground.Engine.SendCommand(Pusher, $"PUSH #{node.Id}");
            if (!(plain.Message ?? "").EndsWith("reaches a runway holding position", StringComparison.Ordinal))
            {
                continue;
            }

            CommandResult forced = ground.Engine.SendCommand(Pusher, $"PUSHF #{node.Id}");
            output.WriteLine($"node {node.Id}: PUSH \"{plain.Message}\"; PUSHF \"{forced.Message}\"");
            Assert.False(forced.Success);
            Assert.Equal(plain.Message, forced.Message);
            reached++;
        }

        Assert.True(reached > 0, "no taxiway node beside B25's nearest holding positions is reached through one");
    }

    // ─── The flag across a mid-push amendment ───

    /// <summary>SFO C8 <c>PUSHF Y FACE S</c> amended by a plain <c>PUSH FACE N</c>: the amended tow stops for parked aircraft.</summary>
    [Fact]
    public void ForcedTowFlag_ClearedByAPlainAmendment()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "C8");
        Assert.True(ground.Engine.SendCommand(Pusher, "PUSHF Y FACE S").Success);
        Assert.True(pusher.Ground.ForcedTowIgnoresParked);

        CommandResult amended = ground.Engine.SendCommand(Pusher, "PUSH FACE N");
        output.WriteLine($"PUSH FACE N: \"{amended.Message}\"");
        Assert.True(amended.Success, amended.Message);
        Assert.False(pusher.Ground.ForcedTowIgnoresParked);
    }

    /// <summary>
    /// SFO C8 <c>PUSH Y FACE N</c> amended by <c>PUSHF FACE S</c>, which the plain amendment would be refused for (64 ft
    /// past Y): the amended tow is forced, and notes it.
    /// </summary>
    [Fact]
    public void ForcedTowFlag_SetByAForcedAmendment()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, "C8");
        Assert.True(ground.Engine.SendCommand(Pusher, "PUSH Y FACE N").Success);
        Assert.False(pusher.Ground.ForcedTowIgnoresParked);

        CommandResult amended = ground.Engine.SendCommand(Pusher, "PUSHF FACE S");
        output.WriteLine($"PUSHF FACE S: \"{amended.Message}\"");
        Assert.True(amended.Success, amended.Message);
        Assert.Contains("(forced: tow runs past taxiway Y, coordinate with ground)", amended.Message);
        Assert.True(pusher.Ground.ForcedTowIgnoresParked);
    }

    // ─── Helpers ───

    /// <summary>The pilot's readback of <paramref name="command"/> off <paramref name="gate"/> with no other aircraft about.</summary>
    private PilotSpeechText? PlainReadback(string gate, string command)
    {
        SfoGround ground = SfoGroundHarness.Build(output, autoCross: false)!.Value;
        SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, gate);
        CommandResult result = ground.Engine.SendCommand(Pusher, command);
        Assert.True(result.Success, result.Message);
        return result.PilotReadback;
    }

    /// <summary>The tug moves <paramref name="command"/> installs off <paramref name="gate"/>, or null without SFO's layout.</summary>
    private IReadOnlyList<TugMove>? InstalledMoves(string gate, string command)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState pusher = SfoGroundHarness.SpawnParked(ground, Pusher, Narrowbody, gate);
        CommandResult result = ground.Engine.SendCommand(Pusher, command);
        output.WriteLine($"{command}: \"{result.Message}\"; moves {Describe(QueuedMoves(pusher))}");
        Assert.True(result.Success, result.Message);
        return QueuedMoves(pusher);
    }

    private static List<TugMove> QueuedMoves(AircraftState aircraft) => [.. aircraft.Phases!.Phases.OfType<PushbackPhase>().Select(p => p.Move)];

    private static string Describe(IReadOnlyList<TugMove> moves) => string.Join(", ", moves.Select(m => $"{m.Kind} {m.Shape}"));

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private (SimulationEngine Engine, AirportGroundLayout Layout)? BuildEngine(string airport)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(airport) is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-forced-push-{airport}",
                ScenarioName = "Forced push",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunway = false,
            },
        };
        return (engine, layout);
    }

    private static AircraftState Spawn(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        (LatLon Position, double NoseTrueDeg) pose,
        Phase phase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = Narrowbody,
            Position = pose.Position,
            TrueHeading = new TrueHeading(pose.NoseTrueDeg),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = layout.AirportId,
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static AircraftState SpawnOnStand(SimulationEngine engine, AirportGroundLayout layout, string callsign, string standName)
    {
        GroundNode stand =
            layout.FindParkingByName(standName) ?? throw new InvalidOperationException($"{layout.AirportId} has no stand '{standName}'");
        return Spawn(engine, layout, callsign, (stand.Position, Assert.NotNull(stand.TrueHeading).Degrees), new AtParkingPhase());
    }

    [GeneratedRegex(@"\(forced: passes (?<ft>\d+) ft from PRK1\)")]
    private static partial Regex PassesNote();
}
