using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetLiveCheckFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The live check of a stored push target (<see cref="PushTargetLiveCheck.Check"/>) on the real KOAK and KSFO layouts
/// and their shipped sidecars: <see cref="PushMoveEntry.ToMove"/> rebuilds each stored move and the moves re-flown with
/// the design group's envelope end where the stored plan says they did; a neighbour the aircraft already touches blocks a
/// target, read with the tug lead for a pull-first one; a plan the actual aircraft cannot complete is unflyable; and the
/// stored moves flown with the actual aircraft's turn radius and swept with its outline are blocked by exactly the parked
/// neighbours the planner's own sweep would not pass; and a push the check blocks is held short of that neighbour by the
/// simulation itself.
/// </summary>
public class PushTargetLiveCheckTests(ITestOutputHelper output)
{
    /// <summary>
    /// Every move the OAK sample stands store, rebuilt with <see cref="PushMoveEntry.ToMove"/> and flown again from the
    /// stand with the group's envelope, stores back as the same entry: every move field equal, the end within
    /// <see cref="PushTargetLiveCheckFixtures.RefliedEndToleranceFt"/>, one <see cref="TugMovePlanner.StepFt"/> more for a
    /// line move.
    /// </summary>
    [Fact]
    public void ToMove_RoundTripsEveryStoredMove()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        double worstFt = 0.0;
        double worstOverToleranceFt = double.NegativeInfinity;
        int moveCount = 0;
        foreach (PushTargetEntry entry in PushTargetPlannerFixtures.OakEntries.Value)
        {
            PushbackPose start = StartOf(Stand(layout, entry.StandName));
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
                    double gapFt = EndGapFt(stored, reflown.Moves[i].End.Position);
                    double toleranceFt = stored.Shape is TugMoveShape.ViaLine or TugMoveShape.ToPoint
                        ? TugMovePlanner.StepFt + RefliedEndToleranceFt
                        : RefliedEndToleranceFt;
                    worstFt = Math.Max(worstFt, gapFt);
                    worstOverToleranceFt = Math.Max(worstOverToleranceFt, gapFt - toleranceFt);
                    moveCount++;
                }
            }
        }

        output.WriteLine($"{moveCount} stored moves re-flown; the largest end gap is {worstFt:F4} ft");
        Assert.True(moveCount > 0, "the OAK sample stands store no moves");
        Assert.True(worstOverToleranceFt <= 0.0, $"a re-flown move ended {worstOverToleranceFt:F4} ft past its shape's tolerance");
    }

    /// <summary>Gate 26's group-IV <c>PUSH TE</c> and <c>PUSH TC</c>, re-flown whole with the envelope, end on the stored end.</summary>
    [Theory]
    [InlineData("TE")]
    [InlineData("TC")]
    public void RefliedPlan_EndsWhereTheStoredPlanSays(string taxilane)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", "IV", taxilane);

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
        PushbackPose start = StartOf(Stand(layout, "26"));
        List<PushTargetEntry> gate26 = [.. PushTargetPlannerFixtures.OakEntries.Value.Where(e => e.StandName == "26")];
        Assert.NotEmpty(gate26);
        foreach (PushTargetEntry entry in gate26)
        {
            PushTargetLiveCheckRequest emptyApron = Request(Parked(Subject, Narrowbody, start, "26"), EnvelopeFootprint(entry.DesignGroup), []);
            Assert.All(entry.Targets, t => Assert.Equal(PushTargetLiveVerdict.Clear, PushTargetLiveCheck.Check(t, emptyApron)));
        }
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
        PushbackPose start = StartOf(Stand(layout, "26"));
        LatLon aft = Offset(start, 180.0, BesideTheLineAftFt);
        var beside = new PushbackPose(Offset(new PushbackPose(aft, start.NoseTrueDeg), 90.0, BesideTheLineFt), start.NoseTrueDeg);
        NeighbourCandidate subject = Parked(Subject, GroupTwoType, start, "26");
        List<NeighbourCandidate> others = [Parked(Neighbour, Narrowbody, beside, null)];
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", "IV", "TC");
        Assert.All(target.Moves, m => Assert.Equal(TugMoveShape.Straight, m.Shape));

        PushTargetLiveVerdict small = PushTargetLiveCheck.Check(target, Request(subject, AircraftFootprint.FromType(GroupTwoType), others));
        PushTargetLiveVerdict envelope = PushTargetLiveCheck.Check(target, Request(subject, EnvelopeFootprint("IV"), others));

        Assert.Equal(PushTargetLiveVerdict.Clear, small);
        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), envelope);
    }

    /// <summary>
    /// A B738 already touching the aircraft on gate 26 is not a neighbour to plan around (<see cref="TugParkedNeighbours.Build"/>
    /// drops it), and the live push would be refused naming it
    /// (<see cref="TugParkedNeighbours.FindStartOverlap(NeighbourCandidate, AircraftFootprint, TugPlan, IEnumerable{NeighbourCandidate})"/>);
    /// every target is blocked by it.
    /// </summary>
    [Fact]
    public void NeighbourAlreadyTouching_BlocksEveryTarget()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose start = StartOf(Stand(layout, "26"));
        NeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");
        NeighbourCandidate touching = Parked(Neighbour, Narrowbody, new PushbackPose(Offset(start, 90.0, TouchingOffsetFt), start.NoseTrueDeg), null);
        string group = GroupOf(Narrowbody);
        IReadOnlyList<PrecomputedPushTarget> targets = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", group);
        Assert.NotEmpty(targets);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        TugPlan plan = Assert.IsType<TugPlan>(
            TugMovePlanner.Plan(
                layout,
                PushTargetPlanner.RequestFor(
                    start,
                    AircraftFootprint.FromType(Narrowbody),
                    classification,
                    GoalOf(layout, start, targets[0]),
                    startsAtStand: true
                ),
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
        TugSimulation onTe = Refly(
            Target(PushTargetPlannerFixtures.OakEntries.Value, "26", GroupOf(Narrowbody), "TE"),
            StartOf(Stand(layout, "26")),
            footprint
        );
        Assert.True(onTe.Completed, "gate 26's PUSH TE did not fly to completion for a B738");
        PushbackPose start = onTe.End;
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

        NeighbourCandidate subject = Parked(Subject, Narrowbody, start, null);
        var inLineAhead = new PushbackPose(Offset(start, 0.0, footprint.LengthFt + LeadOnlyGapFt), start.NoseTrueDeg);
        NeighbourCandidate leadOnly = Parked(Neighbour, Narrowbody, inLineAhead, null);
        Assert.Null(TugParkedNeighbours.FindStartOverlap(subject, footprint, towedNoseFirst: false, [leadOnly]));
        Assert.NotNull(TugParkedNeighbours.FindStartOverlap(subject, footprint, towedNoseFirst: true, [leadOnly]));
        PushTargetLiveCheckRequest request = Request(subject, footprint, [leadOnly]);

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), PushTargetLiveCheck.Check(pullFirst, request));
        Assert.Equal(PushTargetLiveVerdict.Clear, PushTargetLiveCheck.Check(pushFirst, request));
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
        PushbackPose start = StartOf(Stand(layout, "26"));
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", GroupOf(Narrowbody), "TC");
        Assert.All(target.Moves, m => Assert.Equal(TugMoveShape.Straight, m.Shape));
        NeighbourCandidate near = Parked("SWA1", Narrowbody, new PushbackPose(Offset(start, 180.0, NearBlockerAftFt), start.NoseTrueDeg), null);
        NeighbourCandidate far = Parked("SWA2", Narrowbody, new PushbackPose(Offset(start, 180.0, FarBlockerAftFt), start.NoseTrueDeg), null);
        NeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, Request(subject, AircraftFootprint.FromType(Narrowbody), [far, near]));

        Assert.Equal(PushTargetLiveVerdict.Blocked(near.Callsign), verdict);
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
        PushbackPose start = StartOf(Stand(layout, "26"));
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", "II", "TE");
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
    /// straight 545 ft push, flown by a B738 parked <see cref="PushTargetLiveCheckFixtures.MisalignedDeg"/> off the stand's
    /// heading, completes but ends about 2 × 545 × sin(5°) ≈ 95 ft from the stored end, past half the B738's 129.5 ft
    /// length: not this aircraft's push, so unflyable. Constructed: no natural case exists, as the members of every group fly
    /// every OAK sample plan from its stand within the limit
    /// (<see cref="PushTargetLiveCheckReflyTests.ActualRadiusRefly_EndsWithinHalfALength_ForEveryGroup"/>), and a
    /// mismatched type on a fixed-stop plan from the stand drifts under 1 ft (SFO D2's group-V and VI <c>PUSH $5A</c> for
    /// every member type measured, at most 0.80 ft).
    /// </summary>
    [Fact]
    public void EndDriftPastHalfALength_IsUnflyable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose stand = StartOf(Stand(layout, "26"));
        var misaligned = new PushbackPose(stand.Position, new TrueHeading(stand.NoseTrueDeg + MisalignedDeg).Degrees);
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", GroupOf(Narrowbody), "TC");
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
    /// <see cref="PushTargetLiveCheckFixtures.UnflyableTurnDeg"/> off the stand's heading, whose floating line capture runs past its budget.
    /// </summary>
    [Fact]
    public void StoredPlanTheAircraftCannotComplete_IsUnflyable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose stand = StartOf(Stand(layout, "26"));
        var turned = new PushbackPose(stand.Position, new TrueHeading(stand.NoseTrueDeg + UnflyableTurnDeg).Degrees);
        PrecomputedPushTarget target = Target(PushTargetPlannerFixtures.OakEntries.Value, "26", "I", "TE");
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
}
