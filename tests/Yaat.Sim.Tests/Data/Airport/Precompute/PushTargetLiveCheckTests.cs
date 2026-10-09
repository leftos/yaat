using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The live check of a stored push target (<see cref="PushTargetLiveCheck.Check"/>) on the real KOAK and KSFO layouts
/// and their shipped sidecars: <see cref="PushMoveEntry.ToMove"/> rebuilds each stored move and the moves re-flown with
/// the design group's envelope end where the stored plan says they did; a neighbour the aircraft already touches blocks a
/// target, read with the tug lead for a pull-first one; a plan the actual aircraft cannot complete is unflyable; and the
/// stored moves flown with the actual aircraft's turn radius and swept with its outline are blocked by exactly the parked
/// neighbours the planner's own sweep would not pass.
/// </summary>
public class PushTargetLiveCheckTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far a re-flown move may end from the end its stored entry records, feet. Measured, not chosen: the largest gap
    /// over every move of every target the OAK sample stands (gate 26 and the two named stands nearest it) store for every
    /// design group, as <see cref="ToMove_RoundTripsEveryStoredMove"/> prints it: 0.0253 ft over 46 moves, rounded up to
    /// the next thousandth. The stored figures are rounded (coordinates to 1e-7°, feet and degrees to 0.01), so a re-flown
    /// move steers by inputs a hair off the planner's.
    /// </summary>
    private const double RefliedEndToleranceFt = 0.026;

    private const string Narrowbody = "B738";
    private const string Regional = "E75L";

    /// <summary>A design group II type in the pinned FAA copy, for a small aircraft on a large group's plan.</summary>
    private const string GroupTwoType = "CRJ2";

    private const string Subject = "SWA1905";
    private const string Neighbour = "SWA5456";

    /// <summary>How far behind gate 27 along its push line the push-line neighbour is parked, feet (as in issue #475's test).</summary>
    private const double PushLineNeighbourAftFt = 230.0;

    /// <summary>
    /// Where the neighbour beside gate 26's <c>PUSH TC</c> line stands: this far off the line, feet, so a B738 there passes
    /// a CRJ2's wingtips at about 47 ft and overlaps the group-IV envelope's.
    /// </summary>
    private const double BesideTheLineFt = 140.0;

    /// <summary>And this far behind the stand along the line, feet: inside the 545 ft push, clear of the stand.</summary>
    private const double BesideTheLineAftFt = 300.0;

    /// <summary>How far off gate 26's axis a neighbour already touching the aircraft at its stand is parked, feet.</summary>
    private const double TouchingOffsetFt = 60.0;

    /// <summary>How far ahead of or behind the nose the marked points of the pull-first and push-first plans lie, feet.</summary>
    private const double MarkedPointFt = 150.0;

    /// <summary>
    /// The gap between the aircraft's nose and the tail of a like neighbour parked in line ahead of it, feet: clear of the
    /// nose, inside the <see cref="GroundOutline.TugLeadFt"/> tug lead.
    /// </summary>
    private const double LeadOnlyGapFt = 10.0;

    /// <summary>
    /// The gap between the tug lead's tip and the tail of the aircraft held ahead of it where SFO D2's <c>PUSH $5A</c>
    /// ends, feet: inside the <see cref="GroundOutlineSweep.WingtipBufferFt"/> floor.
    /// </summary>
    private const double HeldGapFt = 15.0;

    /// <summary>How far behind gate 26 along its push line the nearer of two in-line blockers is parked, feet.</summary>
    private const double NearBlockerAftFt = 200.0;

    /// <summary>And the farther one, feet: one B738 length and 50 ft behind the nearer, inside the 400 ft range.</summary>
    private const double FarBlockerAftFt = 380.0;

    /// <summary>The smallest design group II member in the pinned FAA copy (<see cref="MemberTypes"/>).</summary>
    private const string SmallGroupTwoType = "BE95";

    /// <summary>A design group VI type.</summary>
    private const string SuperheavyType = "A388";

    /// <summary>How far off gate 26's heading the misaligned B738 is parked, degrees.</summary>
    private const double MisalignedDeg = 10.0;

    /// <summary>How far off gate 26's heading the A388 that cannot complete group I's <c>PUSH TE</c> is parked, degrees.</summary>
    private const double UnflyableTurnDeg = 225.0;

    private static readonly Lazy<DesignGroupEnvelopes> Envelopes = new(DesignGroupEnvelopes.LoadShipped);

    private static readonly Lazy<Dictionary<string, FaaAircraftRecord>> PinnedRecords = new(PushTargetPlannerTests.PinnedRecords);

    /// <summary>The targets of gate 26 and the two named stands nearest it, for every design group.</summary>
    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> OakSampleEntries = new(() =>
    {
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        return PushTargetPlanner.ComputeStands(layout, Envelopes.Value, PushTargetPlannerTests.Sidecars.Value, NearestStandNames(layout, "26", 3), 1);
    });

    /// <summary>The targets of OAK gates 25 and 27, the stands the row tests push from.</summary>
    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> OakRowEntries = new(() =>
        PushTargetPlanner.ComputeStands(
            PushTargetPlannerTests.Oak(),
            Envelopes.Value,
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "25", "27" },
            1
        )
    );

    /// <summary>The targets of SFO gate D2.</summary>
    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> SfoD2Entries = new(() =>
        PushTargetPlanner.ComputeStands(
            PushTargetPlannerTests.Sfo(),
            Envelopes.Value,
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "D2" },
            1
        )
    );

    /// <summary>
    /// Every move the OAK sample stands store, rebuilt with <see cref="PushMoveEntry.ToMove"/> and flown again from the
    /// stand with the group's envelope, stores back as the same entry: every move field equal, the end within
    /// <see cref="RefliedEndToleranceFt"/>.
    /// </summary>
    [Fact]
    public void ToMove_RoundTripsEveryStoredMove()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        double worstFt = 0.0;
        int moveCount = 0;
        foreach (PushTargetEntry entry in OakSampleEntries.Value)
        {
            TugPose start = StartOf(Stand(layout, entry.StandName));
            AircraftFootprint footprint = EnvelopeFootprint(entry.DesignGroup);
            foreach (PrecomputedPushTarget target in entry.Targets)
            {
                TugSimulation reflown = Refly(target, start, footprint);
                Assert.True(
                    reflown.Completed,
                    $"{target.Command} from {entry.StandName} (group {entry.DesignGroup}) did not fly to completion again"
                );
                for (int i = 0; i < target.Moves.Count; i++)
                {
                    PushMoveEntry stored = target.Moves[i];
                    var again = PushMoveEntry.From(reflown.Moves[i]);
                    Assert.Equal(
                        stored,
                        again with
                        {
                            PlannedEndLatitude = stored.PlannedEndLatitude,
                            PlannedEndLongitude = stored.PlannedEndLongitude,
                        }
                    );
                    worstFt = Math.Max(worstFt, EndGapFt(stored, reflown.Moves[i].End.Position));
                    moveCount++;
                }
            }
        }

        output.WriteLine($"{moveCount} stored moves re-flown; the largest end gap is {worstFt:F4} ft");
        Assert.True(moveCount > 0, "the OAK sample stands store no moves");
        Assert.True(worstFt <= RefliedEndToleranceFt, $"a re-flown move ended {worstFt:F4} ft from its stored end");
    }

    /// <summary>Gate 26's group-IV <c>PUSH TE</c> and <c>PUSH TC</c>, re-flown whole with the envelope, end on the stored end.</summary>
    [Theory]
    [InlineData("TE")]
    [InlineData("TC")]
    public void RefliedPlan_EndsWhereTheStoredPlanSays(string taxilane)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", "IV", taxilane);

        TugSimulation reflown = Refly(target, StartOf(Stand(layout, "26")), EnvelopeFootprint("IV"));

        Assert.True(reflown.Completed, $"{target.Command} did not fly to completion again");
        double gapFt = EndGapFt(target.Moves[^1], reflown.End.Position);
        output.WriteLine($"{target.Command}: re-flown end {gapFt:F4} ft from the stored end");
        Assert.True(gapFt <= RefliedEndToleranceFt, $"{target.Command} re-flown ended {gapFt:F4} ft from its stored end");
    }

    /// <summary>With nobody parked about, no target of gate 26 is blocked or unflyable for any design group's envelope.</summary>
    [Fact]
    public void EmptyApron_BlocksNothing()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        List<PushTargetEntry> gate26 = [.. OakSampleEntries.Value.Where(e => e.StandName == "26")];
        Assert.NotEmpty(gate26);
        foreach (PushTargetEntry entry in gate26)
        {
            PushTargetLiveCheckRequest emptyApron = Request(Parked(Subject, Narrowbody, start, "26"), EnvelopeFootprint(entry.DesignGroup), []);
            Assert.All(entry.Targets, t => Assert.Equal(PushTargetLiveVerdict.Clear, PushTargetLiveCheck.Check(t, emptyApron)));
        }
    }

    /// <summary>
    /// With B738s on gates 25 and 27, gate 26's group-III targets (the B738's group: a group-IV envelope at a 737 gate
    /// already overlaps its neighbours) get the live planner's verdict, planned like for like with the envelope: a target
    /// the planner refuses naming a neighbour is blocked by that neighbour, a spot target it plans as stored passed its
    /// sweep and is clear, and a target a forced tow plans as stored is blocked by one of the neighbours the forced tow notes
    /// passing inside the floor, or clear when it notes none.
    /// </summary>
    [Fact]
    public void GateNeighbours_LiveCheckAgreesWithTheLivePlanner()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        string group = GroupOf(Narrowbody);
        AircraftFootprint envelope = EnvelopeFootprint(group);
        TugNeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");
        List<TugNeighbourCandidate> others =
        [
            Parked("SWA25", Narrowbody, StartOf(Stand(layout, "25")), "25"),
            Parked("SWA27", Narrowbody, StartOf(Stand(layout, "27")), "27"),
        ];
        IReadOnlyList<TugParkedNeighbour> neighbours = TugParkedNeighbours.Build(subject, others);
        Assert.Equal(2, neighbours.Count);
        PushTargetLiveCheckRequest request = Request(subject, envelope, others);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        int crossChecked = 0;
        foreach (PrecomputedPushTarget target in Target(OakSampleEntries.Value, "26", group))
        {
            PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, request);
            TugRequest live = PushTargetPlanner.RequestFor(start, envelope, classification, GoalOf(layout, start, target)) with
            {
                ParkedNeighbours = neighbours,
            };
            TugPlan? plain = TugMovePlanner.Plan(layout, live, out string refusal);
            TugPlan? forced = TugMovePlanner.Plan(layout, live with { Forced = true }, out _);
            output.WriteLine(
                $"{target.Command}: {verdict}; planner {(plain is null ? $"refused ({refusal})" : "accepted")}"
                    + $"{(((plain is not null) && SameMoves(plain, target)) ? " as stored" : "")}; forced "
                    + $"{((forced is null) ? "refused" : (SameMoves(forced, target) ? "as stored" : "replanned"))}"
            );
            if ((plain is null) && (neighbours.FirstOrDefault(n => refusal.Contains(n.Callsign, StringComparison.Ordinal)) is { } named))
            {
                Assert.Equal(PushTargetLiveVerdict.Blocked(named.Callsign), verdict);
                crossChecked++;
            }
            else if ((plain is not null) && (target.Kind == PushTargetKind.Spot) && SameMoves(plain, target))
            {
                Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
                crossChecked++;
            }
            else if ((forced is not null) && SameMoves(forced, target))
            {
                HashSet<string> passedInside = [.. forced.ForcedOverrides.OfType<TugForcedPassesNeighbour>().Select(o => o.Callsign)];
                if (passedInside.Count == 0)
                {
                    Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
                }
                else
                {
                    Assert.Equal(PushTargetLiveOutcome.Blocked, verdict.Outcome);
                    Assert.NotNull(verdict.BlockerCallsign);
                    Assert.Contains(verdict.BlockerCallsign, passedInside);
                }

                crossChecked++;
            }
        }

        Assert.True(crossChecked > 0, "no group-III target of gate 26 could be cross-checked against the live planner");
    }

    /// <summary>Issue #475: a B738 on gate 27 pushed onto TE passes the B738 on staggered gate 29 at the row's wingtip gap.</summary>
    [Fact]
    public void Oak_Gate27_PushTe_NeighbourOnStaggeredGate29_NotBlocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "27"));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "27", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "27"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, StartOf(Stand(layout, "29")), "29")]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
    }

    /// <summary>Issue #475: a B738 parked on gate 27's push line, 230 ft back, blocks <c>PUSH TE</c>.</summary>
    [Fact]
    public void Oak_Gate27_PushTe_NeighbourOnThePushLine_Blocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "27"));
        var onPushLine = new TugPose(Offset(start, 180.0, PushLineNeighbourAftFt), start.NoseTrueDeg);

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "27", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "27"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, onPushLine, null)]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), verdict);
    }

    /// <summary>Issue #222: a B738 on gate 25 pushed onto TE passes the B738 parked on gate 26.</summary>
    [Fact]
    public void Oak_Gate25_PushTe_B738OnGate26_NotBlocked()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "25"));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(OakRowEntries.Value, "25", GroupOf(Narrowbody), "TE"),
            Request(
                Parked(Subject, Narrowbody, start, "25"),
                AircraftFootprint.FromType(Narrowbody),
                [Parked(Neighbour, Narrowbody, StartOf(Stand(layout, "26")), "26")]
            )
        );

        Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
    }

    /// <summary>
    /// The SFO GC 28/01 field case: an E75L on D2 with an E75L on D1. Group III stores no <c>PUSH $5A</c> from D2: its
    /// envelope plan is 611.0 ft, over the group's 600.0 ft cap (<see cref="PushTargetPlanner.PathCapFt"/>), so a group-III
    /// menu never offers it, although the E75L's own live plan with SKW3398 at D1 is 591.0 ft. D2's group-III
    /// <c>PUSH T5A</c> is offered: the live planner plans it for the E75L with SKW3398 at D1 as a neighbour, so the E75L's
    /// own plan keeps D1's floor, and the stored group-III moves flown and swept for the E75L keep it too, so both are
    /// clear.
    /// </summary>
    [Fact]
    public void Sfo_D2_E75LAtD1_Spot5ANotOffered()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Sfo();
        TugPose start = StartOf(Stand(layout, "D2"));
        TugNeighbourCandidate subject = Parked("SKW3396", Regional, start, "D2");
        List<TugNeighbourCandidate> others = [Parked("SKW3398", Regional, StartOf(Stand(layout, "D1")), "D1")];
        IReadOnlyList<TugParkedNeighbour> neighbours = TugParkedNeighbours.Build(subject, others);
        Assert.Single(neighbours);
        string group = GroupOf(Regional);
        AircraftFootprint envelope = EnvelopeFootprint(group);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);

        Assert.DoesNotContain(Target(SfoD2Entries.Value, "D2", group), t => t.Command == "PUSH $5A");
        GroundNode spot5A = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("5A"));
        TugPlan envelopePlan = Assert.IsType<TugPlan>(
            TugMovePlanner.Plan(layout, PushTargetPlanner.RequestFor(start, envelope, classification, TugGoal.Spot(spot5A)), out _)
        );
        double envelopePathFt = envelopePlan.Moves.Sum(m => m.PathLengthFt);
        output.WriteLine($"PUSH $5A for group {group}: {envelopePathFt:F1} ft, cap {PushTargetPlanner.PathCapFt(envelope):F1} ft");
        Assert.True(envelopePathFt > PushTargetPlanner.PathCapFt(envelope), $"group {group}'s PUSH $5A is {envelopePathFt:F1} ft, inside the cap");

        PrecomputedPushTarget t5A = Target(SfoD2Entries.Value, "D2", group, "T5A");
        var actual = AircraftFootprint.FromType(Regional);
        PushTargetLiveCheckRequest request = Request(subject, actual, others);
        TugRequest live = PushTargetPlanner.RequestFor(start, actual, classification, GoalOf(layout, start, t5A)) with
        {
            ParkedNeighbours = neighbours,
        };
        TugPlan livePlan = Assert.IsType<TugPlan>(TugMovePlanner.Plan(layout, live, out _));

        PushTargetLiveVerdict stored = PushTargetLiveCheck.Check(t5A, request);
        PushTargetLiveVerdict livePath = PushTargetLiveCheck.Check(t5A with { Moves = [.. livePlan.Moves.Select(PushMoveEntry.From)] }, request);

        output.WriteLine($"{t5A.Command}: stored plan {stored}; live plan {livePath}");
        Assert.Equal(PushTargetLiveVerdict.Clear, livePath);
        Assert.Equal(PushTargetLiveVerdict.Clear, stored);
    }

    /// <summary>
    /// A group-II CRJ2 on gate 26's group-IV <c>PUSH TC</c> plan, a B738 parked parallel beside the push line: the CRJ2's
    /// own outline passes it, the group-IV envelope's would not, so the check sweeps the actual aircraft's outline. The plan
    /// is one straight push, so the turn radius plays no part and the outline alone decides.
    /// </summary>
    [Fact]
    public void SmallAircraftOnLargeGroupPlan_SweepsWithItsOwnOutline()
    {
        TestVnasData.EnsureInitialized();
        Assert.Equal("II", GroupOf(GroupTwoType));
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        LatLon aft = Offset(start, 180.0, BesideTheLineAftFt);
        var beside = new TugPose(Offset(new TugPose(aft, start.NoseTrueDeg), 90.0, BesideTheLineFt), start.NoseTrueDeg);
        TugNeighbourCandidate subject = Parked(Subject, GroupTwoType, start, "26");
        List<TugNeighbourCandidate> others = [Parked(Neighbour, Narrowbody, beside, null)];
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", "IV", "TC");
        Assert.All(target.Moves, m => Assert.Equal(TugMoveShape.Straight, m.Shape));

        PushTargetLiveVerdict small = PushTargetLiveCheck.Check(target, Request(subject, AircraftFootprint.FromType(GroupTwoType), others));
        PushTargetLiveVerdict envelope = PushTargetLiveCheck.Check(target, Request(subject, EnvelopeFootprint("IV"), others));

        Assert.Equal(PushTargetLiveVerdict.Clear, small);
        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), envelope);
    }

    /// <summary>
    /// A B738 already touching the aircraft on gate 26 is not a neighbour to plan around (<see cref="TugParkedNeighbours.Build"/>
    /// drops it), and the live push would be refused naming it
    /// (<see cref="TugParkedNeighbours.FindStartOverlap(TugNeighbourCandidate, AircraftFootprint, TugPlan, IEnumerable{TugNeighbourCandidate})"/>);
    /// every target is blocked by it.
    /// </summary>
    [Fact]
    public void NeighbourAlreadyTouching_BlocksEveryTarget()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        TugNeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");
        TugNeighbourCandidate touching = Parked(Neighbour, Narrowbody, new TugPose(Offset(start, 90.0, TouchingOffsetFt), start.NoseTrueDeg), null);
        string group = GroupOf(Narrowbody);
        IReadOnlyList<PrecomputedPushTarget> targets = Target(OakSampleEntries.Value, "26", group);
        Assert.NotEmpty(targets);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        TugPlan plan = Assert.IsType<TugPlan>(
            TugMovePlanner.Plan(
                layout,
                PushTargetPlanner.RequestFor(start, AircraftFootprint.FromType(Narrowbody), classification, GoalOf(layout, start, targets[0])),
                out _
            )
        );

        Assert.Empty(TugParkedNeighbours.Build(subject, [touching]));
        Assert.Equal(
            Neighbour,
            Assert
                .IsType<TugStartOverlap>(TugParkedNeighbours.FindStartOverlap(subject, AircraftFootprint.FromType(Narrowbody), plan, [touching]))
                .NeighbourCallsign
        );
        PushTargetLiveCheckRequest request = Request(subject, AircraftFootprint.FromType(Narrowbody), [touching]);
        Assert.All(targets, t => Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), PushTargetLiveCheck.Check(t, request)));
    }

    /// <summary>
    /// The start overlap is read per target, with the tug and towbar ahead of the nose only for a target that opens with a
    /// pull. Stand targets always open with the stand push-off today (<c>TugMovePlanner</c>'s <c>_standPushOff</c>, which
    /// <c>TryBuildCandidates</c> puts first in every off-stand candidate), so no shipped entry opens with a pull: this guards
    /// the shape the stored format allows rather than a case the shipped cache holds. A B738 pushed onto TE from OAK gate
    /// 26 has two live plans from where it ends, stored as entries are: a forced pull to a marked point ahead of the nose
    /// and a forced push to one behind it. A B738 parked in line ahead, its tail clear of the nose but inside the tug lead,
    /// blocks the pull-first target only.
    /// </summary>
    [Fact]
    public void PullFirstTarget_NeighbourTouchingOnlyTheTugLead_BlocksPullTargetsOnly()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        var footprint = AircraftFootprint.FromType(Narrowbody);
        TugSimulation onTe = Refly(Target(OakSampleEntries.Value, "26", GroupOf(Narrowbody), "TE"), StartOf(Stand(layout, "26")), footprint);
        Assert.True(onTe.Completed, "gate 26's PUSH TE did not fly to completion for a B738");
        TugPose start = onTe.End;
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        PrecomputedPushTarget pullFirst = LiveTarget(
            layout,
            start,
            footprint,
            classification,
            Offset(start, 0.0, MarkedPointFt),
            PushbackLegKind.Pull
        );
        PrecomputedPushTarget pushFirst = LiveTarget(
            layout,
            start,
            footprint,
            classification,
            Offset(start, 180.0, MarkedPointFt),
            PushbackLegKind.Push
        );
        Assert.Equal(PushbackLegKind.Pull, pullFirst.Moves[0].Kind);
        Assert.Equal(PushbackLegKind.Push, pushFirst.Moves[0].Kind);

        TugNeighbourCandidate subject = Parked(Subject, Narrowbody, start, null);
        var inLineAhead = new TugPose(Offset(start, 0.0, footprint.LengthFt + LeadOnlyGapFt), start.NoseTrueDeg);
        TugNeighbourCandidate leadOnly = Parked(Neighbour, Narrowbody, inLineAhead, null);
        Assert.Null(TugParkedNeighbours.FindStartOverlap(subject, footprint, towedNoseFirst: false, [leadOnly]));
        Assert.NotNull(TugParkedNeighbours.FindStartOverlap(subject, footprint, towedNoseFirst: true, [leadOnly]));
        PushTargetLiveCheckRequest request = Request(subject, footprint, [leadOnly]);

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), PushTargetLiveCheck.Check(pullFirst, request));
        Assert.Equal(PushTargetLiveVerdict.Clear, PushTargetLiveCheck.Check(pushFirst, request));
    }

    /// <summary>
    /// SFO D2's group-V <c>PUSH $5A</c>, a 554 ft stand-to-spot move: an aircraft held on the taxilane just ahead of the
    /// tug where the move ends, more than <see cref="TugParkedNeighbours.RangeFt"/> from the stand (so the live push
    /// planner's neighbour list leaves it out), is within reach of the path and blocks the target.
    /// </summary>
    [Fact]
    public void HeldAircraftOnTheTaxilaneNearThePathEnd_BlocksTheTarget()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Sfo();
        TugPose start = StartOf(Stand(layout, "D2"));
        PrecomputedPushTarget spot5A = Target(SfoD2Entries.Value, "D2", "V", "5A");
        Assert.True(spot5A.PathLengthFt >= 500.0, $"{spot5A.Command} is {spot5A.PathLengthFt} ft");
        AircraftFootprint envelope = EnvelopeFootprint("V");
        TugSimulation flown = Refly(spot5A, start, envelope);
        Assert.True(flown.Completed);
        Assert.Equal(PushbackLegKind.Pull, flown.Moves[^1].Move.Kind);
        double aheadFt = (envelope.LengthFt / 2.0) + GroundOutline.TugLeadFt + HeldGapFt + (AircraftFootprint.FromType(Narrowbody).LengthFt / 2.0);
        var heldPose = new TugPose(Offset(flown.End, 0.0, aheadFt), flown.End.NoseTrueDeg);
        TugNeighbourCandidate subject = Parked(Subject, Narrowbody, start, "D2");
        TugNeighbourCandidate held = Parked(Neighbour, Narrowbody, heldPose, null) with { IsImmobile = true, PhaseName = null };
        double fromStandFt = GeoMath.DistanceNm(start.Position, heldPose.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"{spot5A.Command}: {spot5A.PathLengthFt} ft; held aircraft {fromStandFt:F1} ft from the stand");
        Assert.True(fromStandFt > TugParkedNeighbours.RangeFt, $"the held aircraft is {fromStandFt:F1} ft from the stand");
        Assert.Empty(TugParkedNeighbours.Build(subject, [held]));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(spot5A, Request(subject, envelope, [held]));

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), verdict);
    }

    /// <summary>
    /// Two B738s parked in line on gate 26's <c>PUSH TC</c> push line, listed far one first: the one the tug reaches
    /// first along the path, the near one, is named.
    /// </summary>
    [Fact]
    public void TwoBlockers_NamesTheOneReachedFirst()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", GroupOf(Narrowbody), "TC");
        Assert.All(target.Moves, m => Assert.Equal(TugMoveShape.Straight, m.Shape));
        TugNeighbourCandidate near = Parked("SWA1", Narrowbody, new TugPose(Offset(start, 180.0, NearBlockerAftFt), start.NoseTrueDeg), null);
        TugNeighbourCandidate far = Parked("SWA2", Narrowbody, new TugPose(Offset(start, 180.0, FarBlockerAftFt), start.NoseTrueDeg), null);
        TugNeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, Request(subject, AircraftFootprint.FromType(Narrowbody), [far, near]));

        Assert.Equal(PushTargetLiveVerdict.Blocked(near.Callsign), verdict);
    }

    /// <summary>
    /// Every target the OAK sample stands store for a design group, re-flown with the smallest and the largest member of
    /// that group (<see cref="MemberTypes"/>), completes, and every one whose last move is not a floating-stop line ends
    /// less than half the type's length from the stored end (<see cref="PushTargetLiveCheck.EndDriftFt"/>). Measured by
    /// this test, which prints each group's largest end gap over every target of gates 25, 26 and 27 for both types. A
    /// target whose last move is a floating-stop line is held to completion alone: completing the line puts the aircraft
    /// on it, and its end slides along the line with the turn radius (gate 26's group-II <c>PUSH TE</c> ends 68.9 ft
    /// further along TE for a BE95, more than five times half its length), which is not part of the plan.
    /// </summary>
    [Fact]
    public void ActualRadiusRefly_EndsWithinHalfALength_ForEveryGroup()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        int reflown = 0;
        foreach (IGrouping<string, PushTargetEntry> group in OakSampleEntries.Value.GroupBy(e => e.DesignGroup))
        {
            (string smallest, string largest) = Assert.NotNull(MemberTypes(group.Key));
            double endGapMaxFt = 0.0;
            foreach (PushTargetEntry entry in group)
            {
                TugPose start = StartOf(Stand(layout, entry.StandName));
                foreach ((PrecomputedPushTarget target, string type) in entry.Targets.SelectMany(t => new[] { (t, smallest), (t, largest) }))
                {
                    AircraftFootprint footprint = PinnedFootprint(type);
                    TugSimulation flown = Refly(target, start, footprint);
                    Assert.True(flown.Completed, $"{target.Command} from {entry.StandName} (group {group.Key}) did not complete for a {type}");
                    reflown++;
                    if (PushTargetLiveCheck.EndsOnAFloatingLine(target))
                    {
                        continue;
                    }

                    double driftFt = PushTargetLiveCheck.EndDriftFt(target, flown);
                    Assert.True(
                        driftFt < PushTargetLiveCheck.EndDriftLimitFt(footprint),
                        $"{target.Command} from {entry.StandName} (group {group.Key}) drifted {driftFt:F2} ft for a {type}, "
                            + $"limit {PushTargetLiveCheck.EndDriftLimitFt(footprint):F1} ft"
                    );
                    endGapMaxFt = Math.Max(endGapMaxFt, driftFt);
                }
            }

            output.WriteLine($"group {group.Key} ({smallest}, {largest}): end gap at most {endGapMaxFt:F2} ft");
        }

        Assert.True(reflown > 0, "the OAK sample stands store no targets");
    }

    /// <summary>
    /// Gate 26's group-II <c>PUSH TE</c> — the stand push-off, then a line move onto TE with a floating stop — re-flown for
    /// a BE95, the group's smallest member, ends 68.9 ft from the stored end, against half a BE95's length of 12.9 ft, but
    /// on TE: the end slides along the line with the turn radius, and <c>PUSH TE</c> names no stop, so it is flyable. The
    /// test prints the measured slide.
    /// </summary>
    [Fact]
    public void FloatingLineSlide_IsFlyable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose start = StartOf(Stand(layout, "26"));
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", "II", "TE");
        Assert.True(PushTargetLiveCheck.EndsOnAFloatingLine(target));
        AircraftFootprint footprint = PinnedFootprint(SmallGroupTwoType);
        TugSimulation flown = Refly(target, start, footprint);
        double slideFt = EndGapFt(target.Moves[^1], flown.End.Position);
        output.WriteLine($"{target.Command} for a {SmallGroupTwoType}: end {slideFt:F2} ft from the stored end");
        Assert.True(slideFt > PushTargetLiveCheck.EndDriftLimitFt(footprint), $"the end slid only {slideFt:F2} ft");

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, Request(Parked(Subject, SmallGroupTwoType, start, "26"), footprint, []));

        Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
    }

    /// <summary>
    /// A fixed-stop plan flown by an aircraft that is not where the plan starts: gate 26's group-III <c>PUSH TC</c>, one
    /// straight 545 ft push, flown by a B738 parked <see cref="MisalignedDeg"/> off the stand's heading, completes but ends
    /// about 2 × 545 × sin(5°) ≈ 95 ft from the stored end, past half the B738's 129.5 ft length: not this aircraft's push,
    /// so unflyable. Constructed: no natural case exists, as the members of every group fly every OAK sample plan from its
    /// stand within the limit (<see cref="ActualRadiusRefly_EndsWithinHalfALength_ForEveryGroup"/>), and a mismatched type
    /// on a fixed-stop plan from the stand drifts under 1 ft (SFO D2's group-V and VI <c>PUSH $5A</c> for every member
    /// type measured, at most 0.80 ft).
    /// </summary>
    [Fact]
    public void EndDriftPastHalfALength_IsUnflyable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose stand = StartOf(Stand(layout, "26"));
        var misaligned = new TugPose(stand.Position, new TrueHeading(stand.NoseTrueDeg + MisalignedDeg).Degrees);
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", GroupOf(Narrowbody), "TC");
        Assert.False(PushTargetLiveCheck.EndsOnAFloatingLine(target));
        var footprint = AircraftFootprint.FromType(Narrowbody);
        TugSimulation flown = Refly(target, misaligned, footprint);
        Assert.True(flown.Completed, $"{target.Command} did not complete from the misaligned pose");
        double driftFt = PushTargetLiveCheck.EndDriftFt(target, flown);
        output.WriteLine($"{target.Command} from {MisalignedDeg}° off the stand: end {driftFt:F1} ft from the stored end");
        Assert.True(driftFt > PushTargetLiveCheck.EndDriftLimitFt(footprint), $"the end drifted only {driftFt:F1} ft");

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, Request(Parked(Subject, Narrowbody, misaligned, "26"), footprint, []));

        Assert.Equal(PushTargetLiveVerdict.Unflyable, verdict);
    }

    /// <summary>
    /// A stored plan the aircraft cannot fly to completion inside its travel budget is unflyable. Found by measurement: of
    /// every OAK sample target and SFO D2 and D15 target re-flown from its stand with the smallest and largest member of
    /// every design group, all completed; of gate 26's targets re-flown with a B738, an A388 and a C172 parked at seven
    /// headings 45° apart off the stand's, only one did not — group I's <c>PUSH TE</c> for an A388 parked
    /// <see cref="UnflyableTurnDeg"/> off the stand's heading, whose floating line capture runs past its budget.
    /// </summary>
    [Fact]
    public void StoredPlanTheAircraftCannotComplete_IsUnflyable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        TugPose stand = StartOf(Stand(layout, "26"));
        var turned = new TugPose(stand.Position, new TrueHeading(stand.NoseTrueDeg + UnflyableTurnDeg).Degrees);
        PrecomputedPushTarget target = Target(OakSampleEntries.Value, "26", "I", "TE");
        var footprint = AircraftFootprint.FromType(SuperheavyType);
        Assert.False(Refly(target, turned, footprint).Completed);

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, Request(Parked(Subject, SuperheavyType, turned, "26"), footprint, []));

        Assert.Equal(PushTargetLiveVerdict.Unflyable, verdict);
    }

    /// <summary>
    /// The live check is in neither hashed source set, so changing it never stales a stored entry: it reads the stored plans,
    /// it does not make them. (<see cref="PushMoveEntry.ToMove"/>, which it calls, lives in the hashed
    /// <c>PushMoveEntry.cs</c>.)
    /// </summary>
    [Fact]
    public void LiveCheck_IsInNeitherHashedSet()
    {
        const string liveCheck = "Data/Airport/Precompute/PushTargetLiveCheck.cs";
        Assert.True(File.Exists(Path.Combine(TickRecorder.FindRepoRoot(), "src", "Yaat.Sim", liveCheck)), $"{liveCheck} is missing");

        Assert.DoesNotContain(liveCheck, PrecomputeSourceHashes.PushTargetFiles);
        Assert.DoesNotContain(liveCheck, PrecomputeSourceHashes.LayoutFiles);
    }

    private static TugSimulation Refly(PrecomputedPushTarget target, TugPose start, AircraftFootprint footprint) =>
        TugKinematics.Simulate(start, [.. target.Moves.Select(m => m.ToMove())], footprint, TugMovePlanner.StepFt);

    private static double EndGapFt(PushMoveEntry stored, LatLon end) =>
        GeoMath.DistanceNm(new LatLon(stored.PlannedEndLatitude, stored.PlannedEndLongitude), end) * GeoMath.FeetPerNm;

    private static bool SameMoves(TugPlan plan, PrecomputedPushTarget target) => plan.Moves.Select(PushMoveEntry.From).SequenceEqual(target.Moves);

    private static PushTargetLiveCheckRequest Request(
        TugNeighbourCandidate subject,
        AircraftFootprint actual,
        IReadOnlyList<TugNeighbourCandidate> others
    ) =>
        new()
        {
            Subject = subject,
            Actual = actual,
            Others = others,
        };

    /// <summary>
    /// A live plan from <paramref name="start"/> (not off a stand) to a marked point facing the nose, its moves forced to
    /// <paramref name="kind"/>, stored as a precomputed target would be.
    /// </summary>
    private static PrecomputedPushTarget LiveTarget(
        AirportGroundLayout layout,
        TugPose start,
        AircraftFootprint footprint,
        MovementAreaClassification classification,
        LatLon point,
        PushbackLegKind kind
    )
    {
        TugGoal goal = TugGoal.FreePose(VirtualNode.Create(point.Lat, point.Lon), null, start.NoseTrueDeg, "the marked point") with
        {
            ForcedKind = kind,
        };
        TugRequest request = PushTargetPlanner.RequestFor(start, footprint, classification, goal) with { StartsAtStand = false };
        TugPlan plan =
            TugMovePlanner.Plan(layout, request, out string refusal)
            ?? throw new InvalidOperationException($"The forced {kind} to the marked point was refused: {refusal}");
        return new PrecomputedPushTarget
        {
            Kind = PushTargetKind.Spot,
            Name = "the marked point",
            Note = "",
            Command = $"PUSHM the marked point /{kind}",
            Facings = [],
            PathLengthFt = Math.Round(plan.Moves.Sum(m => m.PathLengthFt), 1),
            Moves = [.. plan.Moves.Select(PushMoveEntry.From)],
        };
    }

    /// <summary>An aircraft parked at rest, as the simulation and the client feed it to <see cref="TugParkedNeighbours"/>.</summary>
    private static TugNeighbourCandidate Parked(string callsign, string type, TugPose pose, string? stand) =>
        new()
        {
            Callsign = callsign,
            Position = pose.Position,
            TrueHeadingDeg = pose.NoseTrueDeg,
            AircraftType = type,
            StandName = stand,
            IsImmobile = false,
            PhaseName = "At Parking",
            GroundSpeedKts = 0.0,
            TargetSpeedKts = null,
        };

    /// <summary>The goal the planner planned a target for, as <see cref="PushTargetPlanner"/> builds it.</summary>
    private static TugGoal GoalOf(AirportGroundLayout layout, TugPose start, PrecomputedPushTarget target) =>
        target.Kind == PushTargetKind.Spot
            ? TugGoal.Spot(Assert.IsType<GroundNode>(layout.FindSpotNodeByName(target.Name)))
            : TugGoal.StraightBackTo(Assert.IsType<GroundNode>(layout.FindExitByTaxiway(start.Position, target.Name)), target.Name);

    /// <summary>A point <paramref name="feet"/> from <paramref name="pose"/>, <paramref name="offNoseDeg"/> clockwise off its nose.</summary>
    private static LatLon Offset(TugPose pose, double offNoseDeg, double feet) =>
        GeoMath.ProjectPoint(pose.Position, new TrueHeading(pose.NoseTrueDeg + offNoseDeg), feet / GeoMath.FeetPerNm);

    private static string GroupOf(string type) => Assert.NotNull(AirplaneDesignGroups.OfRecord(PinnedRecords.Value[type], out _)).ToString();

    /// <summary>
    /// The smallest and largest fixed-wing member of a design group in the pinned FAA copy, by wingspan then ICAO code,
    /// among the records that carry a length, a wingspan and a wheelbase; null when the group has none.
    /// </summary>
    private static (string Smallest, string Largest)? MemberTypes(string group)
    {
        List<FaaAircraftRecord> members =
        [
            .. PinnedRecords
                .Value.Values.Where(r =>
                    (r.LengthFt is > 0.0)
                    && (r.WingspanFt is > 0.0)
                    && (r.WheelbaseFt is > 0.0)
                    && (AircraftCategorization.Categorize(r.IcaoCode) != AircraftCategory.Helicopter)
                    && (AirplaneDesignGroups.OfRecord(r, out _)?.ToString() == group)
                )
                .OrderBy(r => r.WingspanFt)
                .ThenBy(r => r.IcaoCode, StringComparer.Ordinal),
        ];
        return members.Count == 0 ? null : (members[0].IcaoCode, members[^1].IcaoCode);
    }

    /// <summary>A type's footprint from the pinned FAA copy, so the figures do not move with the live database.</summary>
    private static AircraftFootprint PinnedFootprint(string type)
    {
        FaaAircraftRecord record = PinnedRecords.Value[type];
        return new AircraftFootprint
        {
            TypeCode = type,
            LengthFt = Assert.NotNull(record.LengthFt),
            WingspanFt = Assert.NotNull(record.WingspanFt),
            WheelbaseFt = record.WheelbaseFt,
            Category = AircraftCategorization.Categorize(type),
        };
    }

    private static IReadOnlyList<PrecomputedPushTarget> Target(IReadOnlyList<PushTargetEntry> entries, string stand, string group) =>
        Assert.Single(entries, e => (e.StandName == stand) && (e.DesignGroup == group)).Targets;

    private static PrecomputedPushTarget Target(IReadOnlyList<PushTargetEntry> entries, string stand, string group, string name) =>
        Assert.Single(Target(entries, stand, group), t => t.Name == name);

    private static AircraftFootprint EnvelopeFootprint(string group) =>
        DesignGroupEnvelopes.FootprintOf(Assert.Single(Envelopes.Value.Envelopes, e => e.Group == group));

    /// <summary>The stand a name plans from, as <see cref="PushTargetPlanner"/> picks it: the parking node with the lowest id.</summary>
    private static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    private static TugPose StartOf(GroundNode stand) => new(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees);

    /// <summary>The <paramref name="count"/> named parking nodes nearest the named stand, nearest first then by node id.</summary>
    private static IReadOnlySet<string> NearestStandNames(AirportGroundLayout layout, string reference, int count)
    {
        GroundNode gate = Stand(layout, reference);
        return layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(gate.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(count)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
