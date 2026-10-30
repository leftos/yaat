using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// What the <see cref="PushTargetLiveCheckTests"/> classes share: the parked aircraft, the measured figures and the
/// helpers that re-fly a stored target and build a live-check request.
/// </summary>
internal static class PushTargetLiveCheckFixtures
{
    /// <summary>
    /// How far a re-flown move may end from the end its stored entry records, feet. A straight move re-flies exactly, so
    /// its only gap is the stored end's rounding step (coordinates to 1e-7°, about 0.023 ft at OAK's latitude); this is that
    /// step plus a margin.
    /// <para>
    /// A line move (<see cref="TugMoveShape.ViaLine"/>, <see cref="TugMoveShape.ToPoint"/>) steers from rounded inputs (the
    /// line point, its direction to 0.01°, the stop point), so it drifts up to about 0.12 ft and can latch its stop one
    /// <see cref="TugMovePlanner.StepFt"/> early or late (seen up to 1.006 ft, S15 group I <c>PUSH S</c>);
    /// <see cref="PushTargetLiveCheckTests.ToMove_RoundTripsEveryStoredMove"/> allows such a move one step more. The product's
    /// own limit on a re-flown plan's drift is half the aircraft's length (<see cref="PushTargetLiveCheck.EndDriftLimitFt"/>).
    /// </para>
    /// </summary>
    internal const double RefliedEndToleranceFt = 0.026;

    internal const string Narrowbody = "B738";
    internal const string Regional = "E75L";

    /// <summary>A design group II type in the pinned FAA copy, for a small aircraft on a large group's plan.</summary>
    internal const string GroupTwoType = "CRJ2";

    internal const string Subject = "SWA1905";
    internal const string Neighbour = "SWA5456";

    /// <summary>How far behind gate 27 along its push line the push-line neighbour is parked, feet (as in issue #475's test).</summary>
    internal const double PushLineNeighbourAftFt = 230.0;

    /// <summary>
    /// Where the neighbour beside gate 26's <c>PUSH TC</c> line stands: this far off the line, feet, so a B738 there passes
    /// a CRJ2's wingtips at about 47 ft and overlaps the group-IV envelope's.
    /// </summary>
    internal const double BesideTheLineFt = 140.0;

    /// <summary>And this far behind the stand along the line, feet: inside the 545 ft push, clear of the stand.</summary>
    internal const double BesideTheLineAftFt = 300.0;

    /// <summary>How far off gate 26's axis a neighbour already touching the aircraft at its stand is parked, feet.</summary>
    internal const double TouchingOffsetFt = 60.0;

    /// <summary>How far ahead of or behind the nose the marked points of the pull-first and push-first plans lie, feet.</summary>
    internal const double MarkedPointFt = 150.0;

    /// <summary>
    /// The gap between the aircraft's nose and the tail of a like neighbour parked in line ahead of it, feet: clear of the
    /// nose, inside the <see cref="GroundOutline.TugLeadFt"/> tug lead.
    /// </summary>
    internal const double LeadOnlyGapFt = 10.0;

    /// <summary>
    /// The gap between the tug lead's tip and the tail of the aircraft held ahead of it where SFO D2's <c>PUSH $5A</c>
    /// ends, feet: inside the <see cref="GroundOutlineSweep.WingtipBufferFt"/> floor.
    /// </summary>
    internal const double HeldGapFt = 15.0;

    /// <summary>How far behind gate 26 along its push line the nearer of two in-line blockers is parked, feet.</summary>
    internal const double NearBlockerAftFt = 200.0;

    /// <summary>And the farther one, feet: one B738 length and 50 ft behind the nearer, inside the 400 ft range.</summary>
    internal const double FarBlockerAftFt = 380.0;

    /// <summary>The smallest design group II member in the pinned FAA copy (<see cref="MemberTypes"/>).</summary>
    internal const string SmallGroupTwoType = "BE95";

    /// <summary>A design group VI type.</summary>
    internal const string SuperheavyType = "A388";

    /// <summary>How far off gate 26's heading the misaligned B738 is parked, degrees.</summary>
    internal const double MisalignedDeg = 10.0;

    /// <summary>How far off gate 26's heading the A388 that cannot complete group I's <c>PUSH TE</c> is parked, degrees.</summary>
    internal const double UnflyableTurnDeg = 225.0;

    /// <summary>The B738 parked on gate 25, off the side of gate 26's push.</summary>
    internal const string Gate25Neighbour = "SWA25";

    /// <summary>The B738 parked on gate 27, the neighbour the live check names and the push is held short of.</summary>
    internal const string Gate27Neighbour = "SWA27";

    /// <summary>How long the held push is ticked in one-second ticks, long past the time an unheld push from gate 26 takes.</summary>
    internal const int HoldSeconds = 120;

    /// <summary>The phase a push held short of its neighbour stays in.</summary>
    internal const string PushbackPhaseName = "Pushback";

    /// <summary>The most ground speed a held push may read at the end of the hold, kt: at rest, not creeping.</summary>
    internal const double HeldSpeedKts = 0.5;

    internal static readonly Lazy<DesignGroupEnvelopes> Envelopes = new(DesignGroupEnvelopes.LoadShipped);

    internal static readonly Lazy<Dictionary<string, FaaAircraftRecord>> PinnedRecords = new(PushTargetPlannerTests.PinnedRecords);

    internal static TugSimulation Refly(PrecomputedPushTarget target, PushbackPose start, AircraftFootprint footprint) =>
        TugKinematics.Simulate(start, [.. target.Moves.Select(m => m.ToMove())], footprint, TugMovePlanner.StepFt);

    internal static double EndGapFt(PushMoveEntry stored, LatLon end) =>
        GeoMath.DistanceNm(new LatLon(stored.PlannedEndLatitude, stored.PlannedEndLongitude), end) * GeoMath.FeetPerNm;

    internal static bool SameMoves(TugPlan plan, PrecomputedPushTarget target) => plan.Moves.Select(PushMoveEntry.From).SequenceEqual(target.Moves);

    internal static PushTargetLiveCheckRequest Request(
        NeighbourCandidate subject,
        AircraftFootprint actual,
        IReadOnlyList<NeighbourCandidate> others
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
    internal static PrecomputedPushTarget LiveTarget(
        AirportGroundLayout layout,
        PushbackPose start,
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
        TugRequest request = PushTargetPlanner.RequestFor(start, footprint, classification, goal, startsAtStand: false);
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
    internal static NeighbourCandidate Parked(string callsign, string type, PushbackPose pose, string? stand) =>
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
    internal static TugGoal GoalOf(AirportGroundLayout layout, PushbackPose start, PrecomputedPushTarget target) =>
        target.Kind == PushTargetKind.Spot
            ? TugGoal.Spot(Assert.IsType<GroundNode>(layout.FindSpotNodeByName(target.Name)))
            : TugGoal.StraightBackTo(Assert.IsType<GroundNode>(layout.FindExitByTaxiway(start.Position, target.Name)), target.Name);

    /// <summary>A point <paramref name="feet"/> from <paramref name="pose"/>, <paramref name="offNoseDeg"/> clockwise off its nose.</summary>
    internal static LatLon Offset(PushbackPose pose, double offNoseDeg, double feet) =>
        GeoMath.ProjectPoint(pose.Position, new TrueHeading(pose.NoseTrueDeg + offNoseDeg), feet / GeoMath.FeetPerNm);

    internal static string GroupOf(string type) => Assert.NotNull(AirplaneDesignGroups.OfRecord(PinnedRecords.Value[type], out _)).ToString();

    /// <summary>
    /// The smallest and largest fixed-wing member of a design group in the pinned FAA copy, by wingspan then ICAO code,
    /// among the records that carry a length, a wingspan and a wheelbase; null when the group has none.
    /// </summary>
    internal static (string Smallest, string Largest)? MemberTypes(string group)
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
    internal static AircraftFootprint PinnedFootprint(string type)
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

    internal static IReadOnlyList<PrecomputedPushTarget> Target(IReadOnlyList<PushTargetEntry> entries, string stand, string group) =>
        Assert.Single(entries, e => (e.StandName == stand) && (e.DesignGroup == group)).Targets;

    internal static PrecomputedPushTarget Target(IReadOnlyList<PushTargetEntry> entries, string stand, string group, string name) =>
        Assert.Single(Target(entries, stand, group), t => t.Name == name);

    internal static AircraftFootprint EnvelopeFootprint(string group) =>
        DesignGroupEnvelopes.FootprintOf(Assert.Single(Envelopes.Value.Envelopes, e => e.Group == group));

    /// <summary>The stand a name plans from, as <see cref="PushTargetPlanner"/> picks it: the parking node with the lowest id.</summary>
    internal static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    internal static PushbackPose StartOf(GroundNode stand) => new(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees);
}
