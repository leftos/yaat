using System.Diagnostics;
using Yaat.Client.ContextMenus;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Client.ViewModels;

/// <summary>The Push back to… mapping from planned push targets to menu targets, and the records a live plan carries.</summary>
public partial class GroundViewModel
{
    /// <summary>Two held poses this close, feet, are one push origin: an in-flight plan for one serves the other.</summary>
    public const double SameHeldPoseFt = 1.0;

    /// <summary>Two held poses whose headings differ by no more than this, degrees, are one push origin.</summary>
    public const double SameHeldPoseDeg = 1.0;

    /// <summary>
    /// <paramref name="targets"/> as menu targets, each checked live (<see cref="PushTargetLiveCheck.Check"/>): an
    /// unflyable one left out, a blocked one naming its blocker.
    /// </summary>
    private static List<MenuPushTarget> ToMenuTargets(IReadOnlyList<PrecomputedPushTarget> targets, PushLiveCheckInputs inputs)
    {
        var request = new PushTargetLiveCheckRequest
        {
            Subject = inputs.Subject,
            Actual = inputs.Actual,
            Others = inputs.Others,
        };
        var rows = new List<MenuPushTarget>(targets.Count);
        foreach (PrecomputedPushTarget target in targets)
        {
            PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, request);
            if (verdict.Outcome != PushTargetLiveOutcome.Unflyable)
            {
                rows.Add(ToMenuTarget(target, verdict.BlockerCallsign, inputs.Position));
            }
        }

        return rows;
    }

    private static MenuPushTarget ToMenuTarget(PrecomputedPushTarget target, string? blockedBy, LatLon position) =>
        new(
            MenuKindOf(target.Kind),
            target.Name,
            string.IsNullOrEmpty(target.Note) ? null : target.Note,
            target.PathLengthFt,
            target.Command,
            FacingChoices(target, position),
            blockedBy
        );

    private static MenuPushTargetKind MenuKindOf(PushTargetKind kind) =>
        kind switch
        {
            PushTargetKind.Taxilane => MenuPushTargetKind.Taxilane,
            PushTargetKind.Taxiway => MenuPushTargetKind.Taxiway,
            PushTargetKind.Spot => MenuPushTargetKind.Spot,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown push target kind."),
        };

    /// <summary>
    /// A target's stored true facings as magnetic 8-point cardinals at <paramref name="position"/>, a cardinal both
    /// share given once: each <c>Face {cardinal}</c>, sending <c>{command} FACE {cardinal}</c>.
    /// </summary>
    private static List<MenuCommandChoice> FacingChoices(PrecomputedPushTarget target, LatLon position) =>
        [
            .. target
                .Facings.Select(trueDeg => SnapToCardinal(MagneticDeclination.TrueToMagnetic(trueDeg, position)))
                .Distinct(StringComparer.Ordinal)
                .Select(cardinal => new MenuCommandChoice($"Face {cardinal}", $"{target.Command} FACE {cardinal}", null, [])),
        ];

    /// <summary>The aircraft and its neighbours, as one Push back to… open saw them, for the live check.</summary>
    private sealed record PushLiveCheckInputs(
        TugNeighbourCandidate Subject,
        AircraftFootprint Actual,
        IReadOnlyList<TugNeighbourCandidate> Others,
        LatLon Position
    );

    /// <summary>
    /// Where a push plan starts: the aircraft's named stand (<see cref="AtStand"/>), or the pose it is held at after a
    /// pushback (<see cref="Held"/>). Exactly one of the two is set.
    /// </summary>
    private sealed class PushOrigin
    {
        private PushOrigin(string? stand, TugPose? heldPose)
        {
            Stand = stand;
            HeldPose = heldPose;
        }

        /// <summary>The stand's name, or null for a held origin.</summary>
        public string? Stand { get; }

        /// <summary>The held position and true heading, or null for a stand origin.</summary>
        public TugPose? HeldPose { get; }

        /// <summary>The stand's name, or the held position (<see cref="PushTargetPlanner.HeldDescription"/>), for the log.</summary>
        public string Name =>
            this switch
            {
                { Stand: { } stand } => stand,
                { HeldPose: { } heldPose } => PushTargetPlanner.HeldDescription(heldPose),
                _ => throw new UnreachableException("A push origin is either a stand or a held pose."),
            };

        /// <summary>A push off the named stand.</summary>
        public static PushOrigin AtStand(string stand) => new(stand, null);

        /// <summary>A push from where the aircraft is held after a pushback.</summary>
        public static PushOrigin Held(TugPose heldPose) => new(null, heldPose);

        /// <summary>
        /// Whether <paramref name="other"/> starts from the same origin: the same stand by name, or a held pose within
        /// <see cref="SameHeldPoseFt"/> and <see cref="SameHeldPoseDeg"/> of this one. A stand never matches a held pose.
        /// </summary>
        public bool SameAs(PushOrigin other) =>
            (this, other) switch
            {
                ({ Stand: { } stand }, { Stand: { } otherStand }) => string.Equals(stand, otherStand, StringComparison.Ordinal),
                ({ HeldPose: { } heldPose }, { HeldPose: { } otherPose }) => CloseTo(heldPose, otherPose),
                _ => false,
            };

        private static bool CloseTo(TugPose a, TugPose b) =>
            ((GeoMath.DistanceNm(a.Position, b.Position) * GeoMath.FeetPerNm) <= SameHeldPoseFt)
            && (new TrueHeading(a.NoseTrueDeg).AbsAngleTo(new TrueHeading(b.NoseTrueDeg)) <= SameHeldPoseDeg);
    }

    /// <summary>
    /// One aircraft's background live plan that has not landed yet: the layout, push origin and design group it plans
    /// for, and the list it fills.
    /// </summary>
    private sealed record InFlightPushPlan(AirportGroundLayout Layout, PushOrigin Origin, AirplaneDesignGroup Group, PushTargetList List)
    {
        /// <summary>Whether this plan is for <paramref name="origin"/> on <paramref name="layout"/> as <paramref name="group"/>.</summary>
        public bool IsFor(AirportGroundLayout layout, PushOrigin origin, AirplaneDesignGroup group) =>
            ReferenceEquals(Layout, layout) && Origin.SameAs(origin) && (Group == group);
    }

    /// <summary>Everything one background live plan of a push origin reads, taken on the UI thread.</summary>
    private sealed record LivePushPlanRequest(
        AirportGroundLayout Layout,
        AirportSidecarCatalog Sidecars,
        PushOrigin Origin,
        AirplaneDesignGroup Group,
        PushLiveCheckInputs Check
    );
}
