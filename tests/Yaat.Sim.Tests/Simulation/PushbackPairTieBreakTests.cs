using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Two tugs pushing tail to tail in the KOAK ramp, each with no lateral room to pass the other: resolved independently,
/// both took the pushback yield's hard stop and neither moved again (the ThirtyDepartures "no net movement for 600s while
/// yielding" deadlock). The tie-break holds exactly one of them, chosen lexicographically: a committed push beats one
/// still on its stand, a push off a stand beats a tow from a non-stand origin, the one that has moved further goes, then
/// the lower callsign goes. Each case below sets the keys after the deciding one to point the other way. The winner is
/// not pushed through the holder: it goes on only while the rest of its push leg clears it, and brakes to the stop ring
/// otherwise.
/// </summary>
[Collection("GroundConflictDebugSink")]
public class PushbackPairTieBreakTests
{
    private const double FtPerNm = 6076.12;

    /// <summary>How far apart the two tails start, feet: inside the pushback buffer, on one line so neither can pass.</summary>
    private const double SeparationFt = 150.0;

    private readonly ITestOutputHelper _output;

    public PushbackPairTieBreakTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _output = output;
    }

    [Fact]
    public void CommittedPush_BeatsOneStillOnItsStand()
    {
        // The winner is a continuation of a stand push-off (committed however little it has moved), has moved less and
        // carries the higher callsign; the loser is a push-off still within half a fuselage of its stand.
        AssertOnlyLoserHeld(
            new PushStart("ZZZ1", StartsAtStand: false, ContinuesStandPushOff: true, MovedFt: 10.0),
            new PushStart("AAA1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 40.0)
        );
    }

    [Fact]
    public void PushOffAStand_BeatsATowFromANonStandOrigin()
    {
        // Neither is committed: the winner is a push-off still on its stand, the loser a repositioning tow that has
        // moved further and carries the lower callsign.
        AssertOnlyLoserHeld(
            new PushStart("ZZZ1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 20.0),
            new PushStart("AAA1", StartsAtStand: false, ContinuesStandPushOff: false, MovedFt: 50.0)
        );
    }

    [Fact]
    public void FurtherMoved_BeatsLessFar()
    {
        // Both are push-offs still on their stands; the winner has moved further and carries the higher callsign.
        AssertOnlyLoserHeld(
            new PushStart("ZZZ1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 50.0),
            new PushStart("AAA1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 20.0)
        );
    }

    [Fact]
    public void EqualOtherwise_LowerCallsignGoes()
    {
        AssertOnlyLoserHeld(
            new PushStart("AAA1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 0.0),
            new PushStart("ZZZ1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 0.0)
        );
    }

    /// <summary>
    /// The winner's leg runs straight into the holder: it is not pushed through it but brakes to the stop ring against
    /// it, while the loser still takes the hard stop for it.
    /// </summary>
    [Fact]
    public void WinnerWhoseLegRunsIntoTheLoser_StopsAtTheRing()
    {
        var winner = new PushStart("AAA1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 0.0);
        var loser = new PushStart("ZZZ1", StartsAtStand: true, ContinuesStandPushOff: false, MovedFt: 0.0);
        (AirportGroundLayout layout, LatLon origin, LatLon south) = Ramp();
        AircraftState goes = Pusher(layout, winner, origin, pushDeg: 180, Along(origin, 180, 300.0));
        AircraftState holds = Pusher(layout, loser, south, pushDeg: 0, Along(south, 0, 300.0));
        string trace = RunPass([goes, holds], layout);

        Assert.True(
            holds.Ground.SpeedLimit == 0.0,
            $"{holds.Callsign} was not held: SpeedLimit={holds.Ground.SpeedLimit}{Environment.NewLine}{trace}"
        );
        Assert.Equal(goes.Callsign, holds.Ground.AutoYieldTarget);
        double? goesLimit = goes.Ground.SpeedLimit;
        string pushedOn =
            $"{goes.Callsign} pushed on into {holds.Callsign} {SeparationFt:F0}ft astern: SpeedLimit={goesLimit}{Environment.NewLine}{trace}";
        Assert.True(goesLimit is { } limit && (limit < goes.IndicatedAirspeed), pushedOn);
        Assert.Contains($"{goes.Callsign} push path passes", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ranks alone pick the pusher whose leg runs into the other (the two are equal but for the callsign), while the
    /// other's leg clears it: the clearing side goes, finishes its push past the holder, and the holder then finishes too.
    /// Ranked alone, the winner braked to the stop ring, the loser held for it, and neither moved again.
    /// </summary>
    [Fact]
    public void OnlyOneSideClears_ThatSideGoes_AndBothFinish()
    {
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("a replay-free pushback pair on the KOAK layout");
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-pushback-pair-one-side-clears",
                ScenarioName = "Pushback pair, one side clears",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        LatLon origin = layout.FindParkingByName("S7")!.Position;
        // 190 ft apart, inside the pushback buffer: the clearing leg keeps about 189 ft from the other against the 142 ft
        // a B738 pair needs, with room to spare as it moves.
        LatLon south = Along(origin, 180, 190.0);
        AircraftState runsIn = Pusher(layout, new PushStart("AAA1", true, false, 0.0), origin, pushDeg: 180, Along(origin, 180, 300.0));
        AircraftState clears = Pusher(layout, new PushStart("ZZZ1", true, false, 0.0), south, pushDeg: 0, Along(Along(south, 0, 30.0), 90, 300.0));
        foreach (AircraftState pusher in new[] { runsIn, clears })
        {
            pusher.AirportId = "OAK";
            engine.World.AddAircraft(pusher);
        }

        int second = 0;
        for (; (second < 600) && ((runsIn.Phases?.CurrentPhase is PushbackPhase) || (clears.Phases?.CurrentPhase is PushbackPhase)); second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(
            (runsIn.Phases?.CurrentPhase is not PushbackPhase) && (clears.Phases?.CurrentPhase is not PushbackPhase),
            $"after {second}s: {Describe(runsIn, origin)} | {Describe(clears, south)}{Environment.NewLine}{RunPass([runsIn, clears], layout)}"
        );
    }

    private static string Describe(AircraftState pusher, LatLon start) =>
        $"{pusher.Callsign} {pusher.Phases?.CurrentPhase?.Name ?? "-"} limit={pusher.Ground.SpeedLimit} "
        + $"yield={pusher.Ground.AutoYieldTarget ?? "-"} moved={GeoMath.DistanceNm(start, pusher.Position) * FtPerNm:F0}ft";

    /// <summary>
    /// Builds the pair on the KOAK layout — <paramref name="winner"/> at stand S7's node pushing south, its leg arcing
    /// away east so the rest of it clears the loser; the loser <see cref="SeparationFt"/> south of it pushing north,
    /// its leg running straight through the winner — runs one detector pass in each list order, and asserts that only
    /// the loser is held, and held for the winner.
    /// </summary>
    private void AssertOnlyLoserHeld(PushStart winner, PushStart loser)
    {
        (AirportGroundLayout layout, LatLon origin, LatLon south) = Ramp();
        LatLon winnerLegEnd = Along(Along(origin, 180, 50.0), 90, 300.0);
        LatLon loserLegEnd = Along(south, 0, 300.0);

        foreach (bool winnerFirst in new[] { true, false })
        {
            AircraftState goes = Pusher(layout, winner, origin, pushDeg: 180, winnerLegEnd);
            AircraftState holds = Pusher(layout, loser, south, pushDeg: 0, loserLegEnd);
            string trace = RunPass(winnerFirst ? [goes, holds] : [holds, goes], layout);

            Assert.True(
                holds.Ground.SpeedLimit == 0.0,
                $"{holds.Callsign} was not held (winnerFirst={winnerFirst}): SpeedLimit={holds.Ground.SpeedLimit}{Environment.NewLine}{trace}"
            );
            Assert.Equal(goes.Callsign, holds.Ground.AutoYieldTarget);
            Assert.True(
                (goes.Ground.SpeedLimit is null) || (goes.Ground.SpeedLimit > 0),
                $"{goes.Callsign} was held too (winnerFirst={winnerFirst}): SpeedLimit={goes.Ground.SpeedLimit}{Environment.NewLine}{trace}"
            );
            Assert.Null(goes.Ground.AutoYieldTarget);
        }
    }

    /// <summary>The KOAK layout, stand S7's node, and the point <see cref="SeparationFt"/> south of it.</summary>
    private static (AirportGroundLayout Layout, LatLon Origin, LatLon South) Ramp()
    {
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        GroundNode stand = layout.FindParkingByName("S7") ?? throw new InvalidOperationException("KOAK has no stand S7");
        return (layout, stand.Position, Along(stand.Position, 180, SeparationFt));
    }

    private static LatLon Along(LatLon from, double bearingDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / FtPerNm);

    /// <summary>One detector pass over <paramref name="pair"/>; returns its diagnostic trace, also written to the test output.</summary>
    private string RunPass(List<AircraftState> pair, AirportGroundLayout layout)
    {
        var diagnostics = new List<string>();
        GroundConflictDetector.ApplySpeedLimits(pair, layout, 0, diagnostics.Add);
        string trace = string.Join(Environment.NewLine, diagnostics);
        _output.WriteLine(trace);
        return trace;
    }

    /// <summary>
    /// A B738 under a tug push to <paramref name="legEnd"/>, its tail tracking <paramref name="pushDeg"/>, now at
    /// <paramref name="at"/>, its move begun <see cref="PushStart.MovedFt"/> back along the push line — what the move's
    /// recorded start, and so <see cref="PushbackPhase.HasRampPriority"/>, measures from.
    /// </summary>
    private static AircraftState Pusher(AirportGroundLayout layout, PushStart start, LatLon at, double pushDeg, LatLon legEnd)
    {
        var push = new TrueHeading(pushDeg);
        LatLon began = Along(at, push.ToReciprocal().Degrees, start.MovedFt);
        var aircraft = new AircraftState
        {
            Callsign = start.Callsign,
            AircraftType = "B738",
            Position = began,
            TrueHeading = push.ToReciprocal(),
            IsOnGround = true,
            IndicatedAirspeed = 3,
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(
            new PushbackPhase
            {
                Move = TugMove.ToPoint(PushbackLegKind.Push, legEnd),
                PlannedEnd = legEnd,
                StartsAtStand = start.StartsAtStand,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = start.ContinuesStandPushOff,
            }
        );
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Position = at;
        aircraft.Ground.PushbackTrueHeading = push;
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    /// <summary>One pusher's tie-break keys: its callsign, the kind of move it is in, and how far that move has taken it.</summary>
    private sealed record PushStart(string Callsign, bool StartsAtStand, bool ContinuesStandPushOff, double MovedFt);
}
