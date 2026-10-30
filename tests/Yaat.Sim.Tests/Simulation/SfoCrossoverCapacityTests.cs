using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// SFO's crossover taxiways between 28R and 28L (sidecar <c>exitCapacity</c> crossovers entry): each crossover is one
/// shared exit-capacity segment, hold line to hold line, counted in both directions, so a 28R arrival counts the 28L
/// arrivals standing on it and a 28L arrival likewise. Every crossover holds at most two aircraft at or below CWT G and
/// one otherwise, and under that ceiling only as many as fit between its bars nose to tail, each arrival needing its own
/// length plus the ground conflict detector's stop gap behind the nearest occupant's tail
/// (<see cref="GroundConflictDetector.StopGapFt"/>); an aircraft on it travelling toward the arrival's bar fills it
/// outright. The counting is checked where it decides: the occupied set an arrival's exit choice reads
/// (<see cref="SimulationEngine.ComputeOccupancyForExitChoice"/>) holds the arrival's own bar of a full crossover.
/// Occupants are staged with automatic pull-up off, so an exited arrival stays just past its own bar and leaves the other
/// runway's bar free; a same-direction occupant is then taxied along the crossover to the far bar.
/// </summary>
public class SfoCrossoverCapacityTests(ITestOutputHelper output)
{
    private static readonly string[] CrossoverTaxiways = ["Q", "K", "D", "T", "E", "L", "C", "R", "N", "P"];

    public static readonly TheoryData<string> Crossovers = [.. CrossoverTaxiways];

    public static readonly TheoryData<string, string> CrossoverRunways = BuildCrossoverRunways();

    private static TheoryData<string, string> BuildCrossoverRunways()
    {
        var data = new TheoryData<string, string>();
        foreach (string taxiway in CrossoverTaxiways)
        {
            data.Add(taxiway, "28R");
            data.Add(taxiway, "28L");
        }

        return data;
    }

    private static AirportGroundLayout SfoLayout()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return layout;
    }

    private static ExitCapacitySegment SegmentFor(AirportGroundLayout layout, string taxiway, string runway) =>
        Assert.Single(ExitCapacityResolver.GetSegments(layout), s => (s.Rule.Taxiway == taxiway) && s.AppliesToRunway(runway));

    private static string OtherRunway(string runway) => (runway == "28R") ? "28L" : "28R";

    /// <summary>Every crossover resolves to a segment that governs arrivals on both 28R and 28L, at 2 / 1 / G.</summary>
    [Theory]
    [MemberData(nameof(CrossoverRunways))]
    public void EveryCrossover_ResolvesForBothRunways(string taxiway, string runway)
    {
        ExitCapacitySegment segment = SegmentFor(SfoLayout(), taxiway, runway);
        output.WriteLine($"{runway}/{taxiway}: HS #{segment.ExitBarNodeId} ↔ HS #{segment.ParallelBarNodeId}");

        Assert.Equal(2, segment.Rule.MaxAircraft);
        Assert.Equal(1, segment.Rule.MaxAircraftAboveCwt);
        Assert.Equal("G", segment.Rule.CwtThreshold);
    }

    /// <summary>A 28R arrival and a 28L arrival looking up the same crossover find the same shared segment.</summary>
    [Theory]
    [MemberData(nameof(Crossovers))]
    public void Crossover_SameSegmentBothDirections(string taxiway)
    {
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment from28R = SegmentFor(layout, taxiway, "28R");
        ExitCapacitySegment from28L = SegmentFor(layout, taxiway, "28L");

        Assert.Same(from28R, from28L);
    }

    /// <summary>
    /// A crossovers entry naming a taxiway the layout lacks loads, and on resolution logs one Error naming the airport,
    /// the runway pair and the taxiway; the entry's other taxiways still resolve.
    /// </summary>
    [Fact]
    public void Crossovers_UnknownTaxiway_LogsErrorAndKeepsTheRest()
    {
        AirportGroundLayout layout = SfoLayout();
        AirportSidecarLoadResult loaded = ExitCapacitySidecarTests.LoadSidecar(
            """
            {
              "airportId": "SFO",
              "exitCapacity": [
                { "runways": ["28R", "28L"], "crossovers": ["K", "ZZ9", "D"], "maxAircraft": 2, "maxAircraftAboveCwt": 1, "cwtThreshold": "G" }
              ]
            }
            """
        );
        Assert.Empty(loaded.Warnings);
        IReadOnlyList<ExitCapacityRule> rules = Assert.Single(loaded.Airports).ExitCapacity;

        var capture = ErrorLogCapture.Install();
        IReadOnlyList<ExitCapacitySegment> segments = ExitCapacityResolver.Resolve(layout, rules);

        Assert.Equal(["K", "D"], segments.Select(s => s.Rule.Taxiway));
        Assert.All(segments, s => Assert.True(s.AppliesToRunway("28R") && s.AppliesToRunway("28L")));
        string error = Assert.Single(capture.Errors);
        output.WriteLine(error);
        Assert.Contains(layout.AirportId, error, StringComparison.Ordinal);
        Assert.Contains("28R", error, StringComparison.Ordinal);
        Assert.Contains("28L", error, StringComparison.Ordinal);
        Assert.Contains("taxiway ZZ9", error, StringComparison.Ordinal);
        Assert.Contains("Fix the entry", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A B737 off 28L holds just past 28L's bar on K; for an A321 on final to 28R (both above CWT G, so K holds one)
    /// K is full, and its exit choice reads 28R's bar on K as occupied.
    /// </summary>
    [Fact]
    public void Crossover_28LArrivalOnK_MakesKFullForHeavy28RArrival() => AssertCrossDirectionFull("K", "28L", "28R");

    /// <summary>The reverse direction: a B737 off 28R on K makes K full for an A321 on final to 28L, at 28L's bar.</summary>
    [Fact]
    public void Crossover_28RArrivalOnK_MakesKFullForHeavy28LArrival() => AssertCrossDirectionFull("K", "28R", "28L");

    /// <summary>The same on D: a B737 off 28L on D makes D full for an A321 on final to 28R.</summary>
    [Fact]
    public void Crossover_28LArrivalOnD_MakesDFullForHeavy28RArrival() => AssertCrossDirectionFull("D", "28L", "28R");

    /// <summary>
    /// An E75L off 28L holding just past 28L's bar on a long (Q) and a short (K) crossover faces every 28R arrival, so
    /// the crossover is full for each of them: a B737 (above CWT G as well), an E75L on K (under the limit of two, but
    /// nose to nose with the occupant), and an E75L on Q with a second E75L stopped 250 ft from 28R's bar, which the
    /// facing occupant, the stopped occupant and the count (2 of 2) each refuse. 250 − 53 = 197 ft is left behind its tail
    /// against 131 ft needed, so length does not. On K the length rule refuses as well (191 ft leaves at most 85 ft behind
    /// an E75L), and the B737 rows are refused by the count (CWT F, limit 1), so the direction proof is
    /// <see cref="Crossover_OppositeDirectionOccupant_FillsEvenWhenSmall"/>, a lone E75L arrival facing a lone E75L on Q.
    /// </summary>
    [Theory]
    [InlineData("Q", null, "B737")]
    [InlineData("K", null, "B737")]
    [InlineData("K", null, "E75L")]
    [InlineData("Q", "E75L", "E75L")]
    public void Crossover_OccupantFrom28L_Fills28RExit(string taxiway, string? midCrossoverType, string arrivalType)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, taxiway, "28R");
        LandOccupant(engine, layout, segment, "28L", "E75L");
        if (midCrossoverType is not null)
        {
            AircraftState midCrossover = SpawnMidCrossover(engine, layout, segment, midCrossoverType);
            midCrossover.Position = PointAlongFromBar(segment, "28R", 250);
            WriteLengthCheck(segment, midCrossover, arrivalType);
        }

        AssertExitBarRead(engine, layout, segment, ("28R", arrivalType), full: true);
    }

    /// <summary>
    /// An E75L off 28L holds just past 28L's bar on Q, a leg long enough for two E75Ls nose to tail, so length alone
    /// would admit a second one; but it faces an E75L landing 28R, so Q is full for it.
    /// </summary>
    [Fact]
    public void Crossover_OppositeDirectionOccupant_FillsEvenWhenSmall()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "Q", "28R");
        LandOccupant(engine, layout, segment, "28L", "E75L");

        AssertExitBarRead(engine, layout, segment, ("28R", "E75L"), full: true);
    }

    /// <summary>
    /// An E75L off 28R has pulled up along Q and holds short of 28L; a second E75L landing 28R travels the same way, is
    /// within the limit of two, and Q is long enough for both, so Q is not full.
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_TwoRegionalsFitOnQ() => AssertSameDirectionPair("Q", ("E75L", "E75L"), full: false);

    /// <summary>
    /// An E75L off 28R has pulled up along K and holds short of 28L; a second E75L landing 28R travels the same way and
    /// is within the limit of two, but K's pavement between its bars is shorter than two E75Ls nose to tail, so K is full.
    /// Measured on the committed layout: K's walked path between the bars is 191 ft and an E75L is 106 ft
    /// (<see cref="AircraftLength.ResolveFt"/>), so 191 − 106 = 85 ft is left, short of the 131 ft the arrival needs (its
    /// 106 ft plus the detector's 25 ft stop gap, <see cref="GroundConflictDetector.StopGapFt"/>). Q's is 373 ft, room for
    /// two (<see cref="Crossover_SameDirection_TwoRegionalsFitOnQ"/>); a C172 is 27 ft, so two fit on K
    /// (<see cref="Crossover_SameDirection_SmallPairFitsOnK"/>).
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_LengthRefusesSecondRegionalOnK() => AssertSameDirectionPair("K", ("E75L", "E75L"), full: true);

    /// <summary>
    /// An E75L holding short of 28L on K stands just past 28R's bar, its tail 5 ft across 28R's hold line. A C172
    /// landing 28R would fit in the 85 ft K has left beside one E75L pulled up to 28L's bar, but not behind this one: the
    /// free pavement is measured from the exit bar to the occupant's tail where it actually stands, so K is full.
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_OccupantAtEntryBar_LeavesNoRoomOnK()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "K", "28R");
        AircraftState occupant = SpawnHoldingShortOf28L(engine, layout, segment, "E75L", (AircraftLength.ResolveFt("E75L") / 2) + 5);
        WriteLengthCheck(segment, occupant, "C172");

        AssertExitBarRead(engine, layout, segment, ("28R", "C172"), full: true);
    }

    /// <summary>
    /// An E75L stopped mid-Q with no phase that gives it a direction blocks the strip both ways: Q is full for an E75L
    /// landing 28R, though both are CWT G (limit two) and Q is long enough for both. It stands 250 ft along Q's 373 ft
    /// from 28R's bar, leaving 250 − 53 = 197 ft behind its tail against the 106 + 25 = 131 ft an E75L arrival needs, so
    /// the length rule does not decide; its track points at 28L's bar, the way the arrival travels, so only its being
    /// stopped can refuse the arrival.
    /// </summary>
    [Fact]
    public void Crossover_E75LStoppedMidQ_FillsQForE75LArrival()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "Q", "28R");
        AircraftState stopped = SpawnMidCrossover(engine, layout, segment, "E75L");
        stopped.Position = PointAlongFromBar(segment, "28R", 250);
        stopped.TrueTrack = segment.BearingToward(segment.OtherBar(segment.ExitBarFor("28R")));
        stopped.TrueHeading = stopped.TrueTrack;
        WriteLengthCheck(segment, stopped, "E75L");

        AssertExitBarRead(engine, layout, segment, ("28R", "E75L"), full: true);
    }

    /// <summary>
    /// An E75L taxiing at 10 kt on Q, counted only by its position, takes its direction from its track: heading for
    /// 28R's bar it faces an E75L landing 28R and fills Q; heading for 28L's bar it travels the same way, and Q has room.
    /// It stands 250 ft along Q's 373 ft from 28R's bar, leaving 250 − 53 = 197 ft behind its tail, so the length rule
    /// (an E75L arrival needs 106 + 25 = 131 ft) does not decide either row: only the direction rule does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Crossover_E75LMovingMidQ_FillsQOnlyWhenHeadingForTheExitBar(bool towardExitBar)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "Q", "28R");
        AircraftState mover = SpawnMidCrossover(engine, layout, segment, "E75L");
        mover.Position = PointAlongFromBar(segment, "28R", 250);
        int exitBar = segment.ExitBarFor("28R");
        mover.TrueTrack = segment.BearingToward(towardExitBar ? exitBar : segment.OtherBar(exitBar));
        mover.TrueHeading = mover.TrueTrack;
        mover.IndicatedAirspeed = 10;
        output.WriteLine($"SKW3 {segment.DistanceAlongFromBarFt(exitBar, mover.Position):F0} ft along Q from 28R's bar, track {mover.TrueTrack}");

        AssertExitBarRead(engine, layout, segment, ("28R", "E75L"), full: towardExitBar);
    }

    /// <summary>
    /// A C172 off 28R has pulled up along K and holds short of 28L; a second C172 landing 28R fits behind it, so K is
    /// not full. A C172 is 27 ft, so the detector's trail ring is max(100, 27 + 25 = 52) = 100 ft centre to centre and
    /// leaves a stop gap of 100 − 27 = 73 ft (<see cref="GroundConflictDetector.StopGapFt"/>): the arrival needs
    /// 27 + 73 + 10 = 110 ft behind the occupant's tail (its length, the stop gap and the tail-clear margin,
    /// <see cref="RunwayTailClearance.MarginFt"/>), against about 164 − s ft free on K's 191 ft, s being the occupant's
    /// nose distance short of 28L's bar.
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_SmallPairFitsOnK() => AssertSameDirectionPair("K", ("C172", "C172"), full: false);

    /// <summary>
    /// A CRJ2 holds short of 28L on K with its nose 5 ft short of 28L's bar; a second CRJ2 landing 28R travels the same
    /// way and is within the limit of two. K is 191 ft between the bars and a CRJ2 88 ft, so 98 ft is free behind the
    /// occupant's tail, room for the arrival's 88 ft; but the ground conflict detector would stop the arrival at its trail
    /// ring, 88 + 25 = 113 ft centre to centre, with its tail still across 28R's hold line. The free pavement must hold the
    /// arrival plus the detector's stop gap (<see cref="GroundConflictDetector.StopGapFt"/>, 25 ft) plus the tail-clear
    /// margin (<see cref="RunwayTailClearance.MarginFt"/>, 10 ft), 123 ft, so K is full.
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_Crj2PairRefusedOnK()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "K", "28R");
        double centreFt = segment.StripLengthFt - (AircraftLength.ResolveFt("CRJ2") / 2) - 5;
        AircraftState occupant = SpawnHoldingShortOf28L(engine, layout, segment, "CRJ2", centreFt);
        WriteLengthCheck(segment, occupant, "CRJ2");

        AssertExitBarRead(engine, layout, segment, ("28R", "CRJ2"), full: true);
    }

    /// <summary>
    /// An E75L stands on Q, about 136 ft short of 28L's bar, with half <see cref="RunwayTailClearance.MarginFt"/> more pavement behind its tail
    /// than an E75L landing 28R and the detector's stop gap take (<see cref="GroundConflictDetector.StopGapFt"/>): the
    /// arrival would fit, but not with its tail the margin past 28R's hold line, where its exit stops it
    /// (<see cref="RunwayTailClearance.PastBarFt"/>), so Q is full.
    /// </summary>
    [Fact]
    public void Crossover_SameDirection_RoomShortOfTheTailClearMargin_IsFull()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "Q", "28R");
        double lengthFt = AircraftLength.ResolveFt("E75L");
        double withoutMarginFt = lengthFt + GroundConflictDetector.StopGapFt(lengthFt, lengthFt);
        double centreFt = (lengthFt / 2) + withoutMarginFt + (RunwayTailClearance.MarginFt / 2);
        AircraftState occupant = SpawnHoldingShortOf28L(engine, layout, segment, "E75L", centreFt);
        WriteLengthCheck(segment, occupant, "E75L");

        AssertExitBarRead(engine, layout, segment, ("28R", "E75L"), full: true);
    }

    /// <summary>
    /// A B737 stopped on Q, and nothing else on Q, with no phase that places it on a bar, is counted by its position on
    /// the crossover pavement: Q is full for an E75L on final to either runway. The stopped check refuses the arrival
    /// first, and the count would refuse it too (a B737 is CWT F, above Q's threshold of G, so Q holds one); the stopped
    /// rule is pinned by <see cref="Crossover_E75LStoppedMidQ_FillsQForE75LArrival"/>. The B737 stands 250 ft from the
    /// arrival's bar, leaving 250 − 55 = 195 ft behind its tail against the 106 + 25 = 131 ft the arrival needs, so the
    /// length rule does not decide either row.
    /// </summary>
    [Theory]
    [InlineData("28R")]
    [InlineData("28L")]
    public void Crossover_B737StoppedMidQ_MakesQFullForE75LArrival(string arrivalRunway)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "Q", arrivalRunway);
        AircraftState stopped = SpawnMidCrossover(engine, layout, segment, "B737");
        stopped.Position = PointAlongFromBar(segment, arrivalRunway, 250);
        double alongFt = segment.DistanceAlongFromBarFt(segment.ExitBarFor(arrivalRunway), stopped.Position);
        output.WriteLine($"SKW3 {alongFt:F0} ft along Q from {arrivalRunway}'s bar");

        AssertExitBarRead(engine, layout, segment, (arrivalRunway, "E75L"), full: true);
    }

    /// <summary>
    /// End to end on T, 28R's natural exit: an E75L off 28L holds just past 28L's bar on T and a second E75L stands
    /// mid-T, so T is full and an E75L landing 28R rolls out past T to a later exit.
    /// </summary>
    [Fact]
    public void Crossover_T_FullFromBothSides_E75L28RArrivalTakesLaterExit()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "T", "28R");
        LandOccupant(engine, layout, segment, "28L", "E75L");
        SpawnMidCrossover(engine, layout, segment, "E75L");

        int exitBar = RollOut(engine, SfoExitCapacityTests.AddArrival(engine, layout, "UAL2", "E75L", "28R"));
        Assert.NotEqual(segment.ExitBarFor("28R"), exitBar);
    }

    /// <summary>
    /// An E75L rolling out on 28R and one on 28L hand off to their runway exits in the same tick, each choosing its exit
    /// on its own toward the other runway, with D the first such exit ahead of both. The one first in the world's order
    /// claims D and joins its occupants as it commits, so the other, ticking later in that same tick, finds D full (an
    /// aircraft on it travelling toward its bar) and takes a later exit: the two never meet nose to nose on D. The
    /// hand-offs run through the engine tick: each arrival starts in its landing roll at the hand-off.
    /// </summary>
    [Theory]
    [InlineData("28R")]
    [InlineData("28L")]
    public void Crossover_PairedArrivalsSameTick_OnlyOneClaimsTheCrossover(string firstRunway)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, "D", "28R");
        string secondRunway = OtherRunway(firstRunway);
        AircraftState first = AddArrivalAtHandoff(engine, layout, segment, ("UAL1", firstRunway));
        AircraftState second = AddArrivalAtHandoff(engine, layout, segment, ("UAL2", secondRunway));

        engine.TickOneSecond();

        int firstBar = segment.ExitBarFor(firstRunway);
        int secondBar = segment.ExitBarFor(secondRunway);
        Assert.IsType<RunwayExitPhase>(second.Phases?.CurrentPhase);
        int? firstTarget = (first.Phases?.CurrentPhase as RunwayExitPhase)?.TargetHoldShortNodeId;
        int? secondTarget = (second.Phases?.CurrentPhase as RunwayExitPhase)?.TargetHoldShortNodeId;
        output.WriteLine($"UAL1 off {firstRunway} targets HS #{firstTarget}; UAL2 off {secondRunway} targets HS #{secondTarget}");
        output.WriteLine($"D's bars: #{firstBar} ({firstRunway}), #{secondBar} ({secondRunway})");

        Assert.Equal(firstBar, firstTarget);
        Assert.NotEqual(secondBar, secondTarget);

        (int? firstLeftBy, int? secondLeftBy) = RollOutPair(engine, first, second, (firstBar, secondBar));
        Assert.Equal(firstBar, firstLeftBy);
        Assert.NotNull(secondLeftBy);
        Assert.NotEqual(secondBar, secondLeftBy);
    }

    /// <summary>
    /// Ticks until both arrivals leave the runway, returning the exit bar each last targeted as it left; fails at any tick
    /// in which both target their bars of the segment (<paramref name="segmentBars"/>), and when either has not left the
    /// runway within 400 s.
    /// </summary>
    private (int? First, int? Second) RollOutPair(
        SimulationEngine engine,
        AircraftState first,
        AircraftState second,
        (int First, int Second) segmentBars
    )
    {
        int? firstTarget = TargetOf(first);
        int? secondTarget = TargetOf(second);
        int? firstLeftBy = null;
        int? secondLeftBy = null;
        for (int t = 1; (t <= 400) && ((firstLeftBy is null) || (secondLeftBy is null)); t++)
        {
            engine.TickOneSecond();
            firstTarget = TargetOf(first) ?? firstTarget;
            secondTarget = TargetOf(second) ?? secondTarget;
            bool bothOnSegment = (TargetOf(first) == segmentBars.First) && (TargetOf(second) == segmentBars.Second);
            Assert.False(bothOnSegment, $"at t={t} s both arrivals target the segment, #{segmentBars.First} and #{segmentBars.Second}");
            firstLeftBy ??= LeftRunway(first) ? firstTarget : null;
            secondLeftBy ??= LeftRunway(second) ? secondTarget : null;
        }

        output.WriteLine($"{first.Callsign} left via HS #{firstLeftBy}; {second.Callsign} left via HS #{secondLeftBy}");
        Assert.True((firstLeftBy is not null) && (secondLeftBy is not null), "both arrivals must leave the runway via an exit bar within 400 s");
        return (firstLeftBy, secondLeftBy);
    }

    private static int? TargetOf(AircraftState arrival) => (arrival.Phases?.CurrentPhase as RunwayExitPhase)?.TargetHoldShortNodeId;

    private static bool LeftRunway(AircraftState arrival) =>
        arrival.Phases?.CurrentPhase is HoldingAfterExitPhase or TaxiingPhase or HoldingShortPhase;

    private void AssertCrossDirectionFull(string taxiway, string occupantRunway, string arrivalRunway)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, taxiway, arrivalRunway);
        LandOccupant(engine, layout, segment, occupantRunway, "B737");

        AssertExitBarRead(engine, layout, segment, (arrivalRunway, "A321"), full: true);
    }

    /// <summary>
    /// An occupant off 28R taxies along <paramref name="taxiway"/> to hold short of 28L, and an arrival on final to 28R
    /// checks whether the crossover is <paramref name="full"/> for it.
    /// </summary>
    private void AssertSameDirectionPair(string taxiway, (string Occupant, string Arrival) types, bool full)
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment segment = SegmentFor(layout, taxiway, "28R");
        output.WriteLine(
            $"{taxiway}: {segment.StripLengthFt:F0} ft between the bars; {types.Occupant} {AircraftLength.ResolveFt(types.Occupant):F0} ft, "
                + $"{types.Arrival} {AircraftLength.ResolveFt(types.Arrival):F0} ft"
        );
        LandOccupantPulledUp(engine, layout, segment, "28R", types.Occupant);
        AircraftState occupant = engine.FindAircraft("UAL1") ?? throw new InvalidOperationException("UAL1 vanished");
        WriteLengthCheck(segment, occupant, types.Arrival);

        AssertExitBarRead(engine, layout, segment, ("28R", types.Arrival), full);
    }

    /// <summary>
    /// Writes where a same-direction <paramref name="occupant"/> stands on the segment, travelling toward 28L's bar: its
    /// nose distance <c>s</c> short of that bar, the pavement free behind its tail for a 28R arrival, and the length an
    /// arrival of <paramref name="arrivalType"/> needs there (its own length, the detector's stop gap and the tail-clear
    /// margin (<see cref="RunwayTailClearance.MarginFt"/>)).
    /// </summary>
    private void WriteLengthCheck(ExitCapacitySegment segment, AircraftState occupant, string arrivalType)
    {
        double occupantFt = AircraftLength.ResolveFt(occupant.AircraftType);
        double arrivalFt = AircraftLength.ResolveFt(arrivalType);
        double centreFt = segment.DistanceAlongFromBarFt(segment.ExitBarFor("28R"), occupant.Position);
        double noseFt = segment.StripLengthFt - centreFt - (occupantFt / 2);
        double neededFt = arrivalFt + GroundConflictDetector.StopGapFt(occupantFt, arrivalFt) + RunwayTailClearance.MarginFt;
        output.WriteLine(
            $"{occupant.Callsign} centre {centreFt:F0} ft along from 28R's bar, nose s = {noseFt:F0} ft short of 28L's bar; "
                + $"{centreFt - (occupantFt / 2):F0} ft free behind its tail, {neededFt:F0} ft needed (arrival, stop gap and tail-clear margin)"
        );
    }

    /// <summary>
    /// Spawns UAL1, a <paramref name="type"/>, stationary in <see cref="HoldingShortPhase"/> for 28L's bar of the
    /// segment, with its centre <paramref name="centreFt"/> along the walked path from 28R's bar and facing 28L's bar.
    /// </summary>
    private static AircraftState SpawnHoldingShortOf28L(
        SimulationEngine engine,
        AirportGroundLayout layout,
        ExitCapacitySegment segment,
        string type,
        double centreFt
    )
    {
        int farBar = segment.ExitBarFor("28L");
        var holdShort = new HoldShortPoint
        {
            NodeId = farBar,
            Reason = HoldShortReason.DestinationRunway,
            TargetName = "28L",
        };
        (GroundNode Node, TrueHeading Heading) placement = (layout.Nodes[farBar], segment.BearingToward(farBar));
        AircraftState spawned = SfoGroundHarness.SpawnAt(new SfoGround(engine, layout), "UAL1", type, placement, new HoldingShortPhase(holdShort));
        spawned.Position = PointAlongFromBar(segment, "28R", centreFt);
        return spawned;
    }

    /// <summary>The point <paramref name="distanceFt"/> along the segment's walked path from its bar on <paramref name="runway"/>.</summary>
    private static LatLon PointAlongFromBar(ExitCapacitySegment segment, string runway, double distanceFt)
    {
        int bar = segment.ExitBarFor(runway);
        List<GroundNode> path = (segment.PathNodes[0].Id == bar) ? [.. segment.PathNodes] : [.. segment.PathNodes.Reverse()];
        double remainingFt = distanceFt;
        for (int i = 1; i < path.Count; i++)
        {
            LatLon from = path[i - 1].Position;
            double legFt = GeoMath.DistanceNm(from, path[i].Position) * GeoMath.FeetPerNm;
            if (remainingFt <= legFt)
            {
                return GeoMath.ProjectPoint(from, new TrueHeading(GeoMath.BearingTo(from, path[i].Position)), remainingFt / GeoMath.FeetPerNm);
            }

            remainingFt -= legFt;
        }

        throw new ArgumentOutOfRangeException(
            nameof(distanceFt),
            $"{distanceFt:F0} ft is past the end of {segment.Rule.Taxiway}'s {segment.StripLengthFt:F0} ft walked path"
        );
    }

    /// <summary>
    /// Puts an arrival of the given type on final to the given runway and checks whether the occupied set its exit choice
    /// reads holds its own bar of <paramref name="segment"/>: it must exactly when the segment is <paramref name="full"/>.
    /// The bar must not already be in the shared occupied set, so a refusal can only come from the capacity check.
    /// </summary>
    private void AssertExitBarRead(
        SimulationEngine engine,
        AirportGroundLayout layout,
        ExitCapacitySegment segment,
        (string Runway, string Type) arrival,
        bool full
    )
    {
        int bar = segment.ExitBarFor(arrival.Runway);
        Assert.DoesNotContain(bar, engine.ComputeOccupiedHoldShortNodes());

        AircraftState aircraft = SfoExitCapacityTests.AddArrival(engine, layout, "UAL2", arrival.Type, arrival.Runway);
        IReadOnlySet<int> occupied = engine.ComputeOccupancyForExitChoice(aircraft, layout);
        output.WriteLine($"{arrival.Type} on final {arrival.Runway}: {segment.Rule.Taxiway} bar #{bar} read as occupied: {occupied.Contains(bar)}");

        Assert.Equal(full, occupied.Contains(bar));
    }

    /// <summary>An SFO engine with automatic pull-up to the parallel runway off, so an exited arrival stays just past its bar.</summary>
    private SimulationEngine BuildEngine()
    {
        SimulationEngine engine = SfoExitCapacityTests.CreateEngine(output);
        Assert.NotNull(engine.Scenario);
        engine.Scenario.AutoPullUpToParallel = false;
        return engine;
    }

    /// <summary>
    /// Lands <paramref name="type"/> on <paramref name="runway"/>, exits it toward the other runway onto the segment's
    /// taxiway and ticks until it holds after exiting at its own bar of the segment.
    /// </summary>
    private void LandOccupant(SimulationEngine engine, AirportGroundLayout layout, ExitCapacitySegment segment, string runway, string type)
    {
        int bar = segment.ExitBarFor(runway);
        TickUntilOccupantHolds(
            engine,
            ExitOccupant(engine, layout, segment, runway, type),
            phase => (phase is HoldingAfterExitPhase holding) && (holding.HoldShortNodeId == bar),
            $"holding after exiting {runway} on {segment.Rule.Taxiway} at #{bar}"
        );
    }

    /// <summary>
    /// Lands <paramref name="type"/> on <paramref name="runway"/>, exits it toward the other runway onto the segment's
    /// taxiway, then taxies it along the taxiway (<c>TAXI {taxiway} HS {other runway}</c>) and ticks until it holds short
    /// of the other runway at the segment's far bar.
    /// </summary>
    private void LandOccupantPulledUp(SimulationEngine engine, AirportGroundLayout layout, ExitCapacitySegment segment, string runway, string type)
    {
        LandOccupant(engine, layout, segment, runway, type);
        string otherRunway = OtherRunway(runway);
        string command = $"TAXI {segment.Rule.Taxiway} HS {otherRunway}";
        CommandResult taxi = engine.SendCommand("UAL1", command);
        Assert.True(taxi.Success, $"{command} failed: {taxi.Message}");

        int farBar = segment.ExitBarFor(otherRunway);
        TickUntilOccupantHolds(
            engine,
            engine.FindAircraft("UAL1") ?? throw new InvalidOperationException("UAL1 vanished after its TAXI"),
            phase => (phase is HoldingShortPhase holding) && (holding.HoldShort.NodeId == farBar),
            $"holding short of {otherRunway} on {segment.Rule.Taxiway} at #{farBar}"
        );
    }

    /// <summary>
    /// Puts UAL1, a <paramref name="type"/>, on final to <paramref name="runway"/>, told to exit toward the other runway
    /// (28R left, 28L right) on the segment's taxiway.
    /// </summary>
    private static AircraftState ExitOccupant(
        SimulationEngine engine,
        AirportGroundLayout layout,
        ExitCapacitySegment segment,
        string runway,
        string type
    )
    {
        AircraftState occupant = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", type, runway);
        string command = $"{((runway == "28R") ? "EL" : "ER")} {segment.Rule.Taxiway}";
        CommandResult exit = engine.SendCommand("UAL1", command);
        Assert.True(exit.Success, $"{command} failed: {exit.Message}");
        return occupant;
    }

    /// <summary>
    /// Puts an E75L on the centreline of the arrival's runway 800 ft short of abeam the segment's bar there, rolling at
    /// 20 kt, its landing at the hand-off to its runway exit, and told only to exit toward the other
    /// runway (28R left, 28L right), so the exit taxiway is its own choice.
    /// </summary>
    private static AircraftState AddArrivalAtHandoff(
        SimulationEngine engine,
        AirportGroundLayout layout,
        ExitCapacitySegment segment,
        (string Callsign, string Runway) arrival
    )
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", arrival.Runway);
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        GroundNode bar = layout.Nodes[segment.ExitBarFor(arrival.Runway)];
        (LatLon _, double barAlongNm, bool _) = GeoMath.FootOfPerpendicular(
            bar.Position,
            threshold,
            GeoMath.ProjectPoint(threshold, runway.TrueHeading, 3.0)
        );

        var landing = new LandingPhase();
        var aircraft = new AircraftState
        {
            Callsign = arrival.Callsign,
            AircraftType = "E75L",
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading, barAlongNm - (800 / GeoMath.FeetPerNm)),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 20,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList
            {
                AssignedRunway = runway,
                RequestedExit = new ExitPreference { Side = (arrival.Runway == "28R") ? ExitSide.Left : ExitSide.Right },
            },
        };
        aircraft.Targets.TargetSpeed = 20;
        aircraft.Phases.Add(landing);
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        landing.CurrentState = LandingPhase.State.Handoff;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private void TickUntilOccupantHolds(SimulationEngine engine, AircraftState occupant, Func<Phase?, bool> holds, string description)
    {
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if (holds(occupant.Phases?.CurrentPhase))
            {
                output.WriteLine($"UAL1 ({occupant.AircraftType}) {description} after {t} s");
                return;
            }
        }

        Assert.Fail($"UAL1 ({occupant.AircraftType}) never ended up {description}; phase {occupant.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>Spawns a stationary <paramref name="type"/> at the middle node of the segment's walked path, facing along it.</summary>
    private AircraftState SpawnMidCrossover(SimulationEngine engine, AirportGroundLayout layout, ExitCapacitySegment segment, string type)
    {
        int middle = segment.PathNodes.Count / 2;
        Assert.InRange(middle, 1, segment.PathNodes.Count - 2);
        GroundNode node = segment.PathNodes[middle];
        var heading = new TrueHeading(GeoMath.BearingTo(node.Position, segment.PathNodes[middle + 1].Position));
        AircraftState spawned = SfoGroundHarness.SpawnAt(new SfoGround(engine, layout), "SKW3", type, (node, heading), new HoldingInPositionPhase());
        output.WriteLine($"SKW3 ({type}) stationary mid-{segment.Rule.Taxiway} at #{node.Id}");
        return spawned;
    }

    /// <summary>
    /// Ticks until <paramref name="arrival"/> leaves the runway, returning the exit bar its rollout last targeted; fails
    /// when it has not left the runway, or never targeted a bar, within 400 s.
    /// </summary>
    private int RollOut(SimulationEngine engine, AircraftState arrival)
    {
        int? lastTarget = null;
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            Phase? phase = arrival.Phases?.CurrentPhase;
            if (phase is RunwayExitPhase { TargetHoldShortNodeId: { } target })
            {
                lastTarget = target;
            }

            if ((phase is HoldingAfterExitPhase or TaxiingPhase or HoldingShortPhase) && (lastTarget is { } exitBar))
            {
                output.WriteLine($"{arrival.Callsign}: left the runway via HS #{exitBar} into {phase.Name} after {t} s");
                return exitBar;
            }
        }

        Assert.Fail($"{arrival.Callsign}: did not leave the runway via an exit bar in 400 s; phase {arrival.Phases?.CurrentPhase?.Name}");
        return 0;
    }
}
