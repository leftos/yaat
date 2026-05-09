using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Training;

public enum SoloTrainingEventCategory
{
    Separation,
    RunwayWake,
    AdvisoryVisual,
    Approach,
    Recovery,
}

public enum SoloTrainingEventSeverity
{
    Coach,
    Warning,
    Safety,
}

public sealed record SoloTrainingEvent(
    string Id,
    SoloTrainingEventCategory Category,
    SoloTrainingEventSeverity Severity,
    string Title,
    string Description,
    string RuleReference,
    double StartedAtSeconds,
    double LastObservedAtSeconds,
    double ExposureSeconds,
    bool IsActive,
    List<string> Callsigns,
    string? RunwayId,
    double? RequiredHorizontalNm,
    double? ActualHorizontalNm,
    double? RequiredVerticalFt,
    double? ActualVerticalFt
);

public sealed record SoloTrainingScoreBucket(string Name, int PointsAvailable, int PointsLost);

public sealed record SoloTrainingReportData(
    bool SoloTrainingMode,
    double ScenarioElapsedSeconds,
    int Score,
    string Grade,
    List<SoloTrainingScoreBucket> ScoreBuckets,
    List<SoloTrainingEvent> ActiveEvents,
    List<SoloTrainingEvent> Timeline,
    List<string> CoachingNotes,
    ApproachReportData ApproachReport
);

public sealed class SoloTrainingEvaluator
{
    private const double TerminalRadarHorizontalNm = 3.0;
    private const double IfrVerticalFt = 1000.0;
    private const double ClassBHeavyOrTurbojetHorizontalNm = 1.5;
    private const double ClassBTargetResolutionNm = 0.25;
    private const double ClassBVerticalFt = 500.0;
    private const double WarningMargin = 1.10;
    private const double WarningLookaheadSeconds = 30.0;
    private const double CoachLookaheadSeconds = 60.0;

    private readonly Dictionary<string, TrackedEvent> _events = new(StringComparer.OrdinalIgnoreCase);

    public List<SoloTrainingEvent> Evaluate(List<AircraftState> aircraft, double scenarioElapsedSeconds, AirspaceDatabase airspace)
    {
        var notices = new List<SoloTrainingEvent>();
        var observedThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eligible = aircraft.Where(IsEligibleAirborneTarget).ToList();

        for (int i = 0; i < eligible.Count; i++)
        {
            for (int j = i + 1; j < eligible.Count; j++)
            {
                var a = eligible[i];
                var b = eligible[j];
                if (ResolveRequirement(a, b, airspace) is not { } requirement)
                {
                    continue;
                }

                if (IsCoveredByVisualFollow(a, b))
                {
                    continue;
                }

                var sample = SamplePair(a, b, requirement, scenarioElapsedSeconds);
                if (sample is null)
                {
                    continue;
                }

                observedThisTick.Add(sample.Id);
                var notice = Upsert(sample, scenarioElapsedSeconds);
                if (notice is not null)
                {
                    notices.Add(notice);
                }
            }
        }

        foreach (var tracked in _events.Values)
        {
            if (tracked.IsActive && !observedThisTick.Contains(tracked.Id))
            {
                tracked.IsActive = false;
                tracked.LastObservedAtSeconds = scenarioElapsedSeconds;
            }
        }

        return notices;
    }

    public SoloTrainingReportData BuildReport(bool soloTrainingMode, double scenarioElapsedSeconds, ApproachReportData approachReport)
    {
        var timeline = _events.Values.Select(t => t.ToEvent()).OrderByDescending(e => e.StartedAtSeconds).ToList();
        var active = timeline.Where(e => e.IsActive).OrderByDescending(e => e.Severity).ThenByDescending(e => e.ExposureSeconds).ToList();

        var separationLoss = ComputeEventLoss(timeline, SoloTrainingEventCategory.Separation);
        var runwayWakeLoss = ComputeEventLoss(timeline, SoloTrainingEventCategory.RunwayWake);
        var advisoryLoss = ComputeEventLoss(timeline, SoloTrainingEventCategory.AdvisoryVisual);
        var approachLoss = ComputeApproachLoss(approachReport);
        var recoveryLoss = ComputeRecoveryLoss(timeline);

        var buckets = new List<SoloTrainingScoreBucket>
        {
            new("Separation", 45, Math.Min(45, separationLoss)),
            new("Runway / Wake", 30, Math.Min(30, runwayWakeLoss)),
            new("Advisory / Visual", 15, Math.Min(15, advisoryLoss)),
            new("Approaches", 10, Math.Min(10, approachLoss)),
            new("Recovery", 10, Math.Min(10, recoveryLoss)),
        };

        int pointsLost = buckets.Sum(b => b.PointsLost);
        int score = Math.Clamp(100 - pointsLost, 0, 100);

        return new SoloTrainingReportData(
            soloTrainingMode,
            scenarioElapsedSeconds,
            score,
            GradeFor(score),
            buckets,
            active,
            timeline,
            BuildCoachingNotes(timeline, approachReport),
            approachReport
        );
    }

    public void Reset()
    {
        _events.Clear();
    }

    internal static SeparationRequirement? ResolveRequirement(AircraftState a, AircraftState b, AirspaceDatabase airspace)
    {
        bool aVfr = a.FlightPlan.IsVfr;
        bool bVfr = b.FlightPlan.IsVfr;

        if (!aVfr && !bVfr)
        {
            return new SeparationRequirement(
                "IFR radar separation",
                SoloTrainingEventCategory.Separation,
                "7110.65 §5-5-4, §5-5-5, §4-5-1",
                TerminalRadarHorizontalNm,
                IfrVerticalFt,
                "Maintain at least 3 NM radar separation or 1,000 ft vertical separation between IFR aircraft."
            );
        }

        bool classBApplies = IsInBravo(a, airspace) || IsInBravo(b, airspace);
        if (!classBApplies)
        {
            return null;
        }

        bool heavyOrTurbojetPair = IsLargeOrTurbojet(a) || IsLargeOrTurbojet(b);
        if (heavyOrTurbojetPair)
        {
            return new SeparationRequirement(
                "Class B large/turbojet separation",
                SoloTrainingEventCategory.Separation,
                "7110.65 §7-9-4",
                ClassBHeavyOrTurbojetHorizontalNm,
                ClassBVerticalFt,
                "VFR aircraft in Class B require 1.5 NM, 500 ft vertical, or visual separation from turbojets and aircraft over 19,000 lb."
            );
        }

        return new SeparationRequirement(
            "Class B target-resolution separation",
            SoloTrainingEventCategory.Separation,
            "7110.65 §7-9-4",
            ClassBTargetResolutionNm,
            ClassBVerticalFt,
            "VFR aircraft in Class B require target resolution, 500 ft vertical, or visual separation from aircraft 19,000 lb or less."
        );
    }

    private static bool IsEligibleAirborneTarget(AircraftState aircraft) =>
        !aircraft.IsOnGround && aircraft.Transponder.Mode.Equals("C", StringComparison.OrdinalIgnoreCase) && !aircraft.Ghost.IsUnsupported;

    private static bool IsInBravo(AircraftState aircraft, AirspaceDatabase airspace) =>
        airspace.FindContaining(aircraft.Position, aircraft.Altitude).Any(v => v.Class == AirspaceClass.Bravo);

    private static bool IsLargeOrTurbojet(AircraftState aircraft)
    {
        var record = FaaAircraftDatabase.Get(aircraft.AircraftType);
        if (record is not null)
        {
            if (record.MtowLb > 19000.0)
            {
                return true;
            }

            return record.PhysicalClassEngine?.Contains("Jet", StringComparison.OrdinalIgnoreCase) == true;
        }

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return category == AircraftCategory.Jet;
    }

    private static bool IsCoveredByVisualFollow(AircraftState a, AircraftState b)
    {
        bool aFollowingB =
            a.Approach.HasReportedTrafficInSight
            && a.Approach.FollowingCallsign is not null
            && a.Approach.FollowingCallsign.Equals(b.Callsign, StringComparison.OrdinalIgnoreCase);
        bool bFollowingA =
            b.Approach.HasReportedTrafficInSight
            && b.Approach.FollowingCallsign is not null
            && b.Approach.FollowingCallsign.Equals(a.Callsign, StringComparison.OrdinalIgnoreCase);
        return aFollowingB || bFollowingA;
    }

    private static PairSample? SamplePair(AircraftState a, AircraftState b, SeparationRequirement requirement, double scenarioElapsedSeconds)
    {
        var current = ComputeSeparation(a, b, lookaheadSeconds: 0.0);
        var projected30 = ComputeSeparation(a, b, WarningLookaheadSeconds);
        var projected60 = ComputeSeparation(a, b, CoachLookaheadSeconds);

        bool currentViolation = Violates(current.HorizontalNm, current.VerticalFt, requirement);
        bool warningViolation = Violates(projected30.HorizontalNm, projected30.VerticalFt, requirement);
        bool coachViolation = Violates(projected60.HorizontalNm, projected60.VerticalFt, requirement);
        bool warningMargin = WithinWarningMargin(current.HorizontalNm, current.VerticalFt, requirement);

        SoloTrainingEventSeverity? severity =
            currentViolation ? SoloTrainingEventSeverity.Safety
            : warningViolation || warningMargin ? SoloTrainingEventSeverity.Warning
            : coachViolation ? SoloTrainingEventSeverity.Coach
            : null;

        if (severity is null)
        {
            return null;
        }

        string id = MakePairEventId(requirement.Name, a.Callsign, b.Callsign);
        string title = severity == SoloTrainingEventSeverity.Safety ? $"{requirement.Name} loss" : $"{requirement.Name} risk";
        string description =
            $"{a.Callsign} and {b.Callsign}: {requirement.CoachingText} Current spacing is {current.HorizontalNm:F1} NM and {current.VerticalFt:F0} ft.";

        return new PairSample(
            id,
            requirement.Category,
            severity.Value,
            title,
            description,
            requirement.RuleReference,
            scenarioElapsedSeconds,
            [a.Callsign, b.Callsign],
            requirement.RequiredHorizontalNm,
            current.HorizontalNm,
            requirement.RequiredVerticalFt,
            current.VerticalFt
        );
    }

    private static (double HorizontalNm, double VerticalFt) ComputeSeparation(AircraftState a, AircraftState b, double lookaheadSeconds)
    {
        var aPosition = ProjectPosition(a, lookaheadSeconds);
        var bPosition = ProjectPosition(b, lookaheadSeconds);
        double aAltitude = a.Altitude + (a.VerticalSpeed * lookaheadSeconds / 60.0);
        double bAltitude = b.Altitude + (b.VerticalSpeed * lookaheadSeconds / 60.0);
        return (GeoMath.DistanceNm(aPosition, bPosition), Math.Abs(aAltitude - bAltitude));
    }

    private static LatLon ProjectPosition(AircraftState aircraft, double lookaheadSeconds)
    {
        if (lookaheadSeconds <= 0.0 || aircraft.GroundSpeed <= 0.0)
        {
            return aircraft.Position;
        }

        double distanceNm = aircraft.GroundSpeed * lookaheadSeconds / 3600.0;
        return GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueTrack, distanceNm);
    }

    private static bool Violates(double horizontalNm, double verticalFt, SeparationRequirement requirement) =>
        (horizontalNm < requirement.RequiredHorizontalNm) && (verticalFt < requirement.RequiredVerticalFt);

    private static bool WithinWarningMargin(double horizontalNm, double verticalFt, SeparationRequirement requirement) =>
        (horizontalNm < requirement.RequiredHorizontalNm * WarningMargin) && (verticalFt < requirement.RequiredVerticalFt * WarningMargin);

    private SoloTrainingEvent? Upsert(PairSample sample, double scenarioElapsedSeconds)
    {
        if (!_events.TryGetValue(sample.Id, out var tracked))
        {
            tracked = TrackedEvent.FromSample(sample);
            _events.Add(sample.Id, tracked);
            return tracked.ToEvent();
        }

        var oldSeverity = tracked.Severity;
        tracked.Update(sample, scenarioElapsedSeconds);

        return sample.Severity > oldSeverity ? tracked.ToEvent() : null;
    }

    private static string MakePairEventId(string ruleName, string callsignA, string callsignB)
    {
        int cmp = string.Compare(callsignA, callsignB, StringComparison.OrdinalIgnoreCase);
        string first = cmp <= 0 ? callsignA : callsignB;
        string second = cmp <= 0 ? callsignB : callsignA;
        string normalizedRule = ruleName.Replace(' ', '_').Replace('/', '_');
        return $"{normalizedRule}_{first}_{second}".ToUpperInvariant();
    }

    private static int ComputeEventLoss(List<SoloTrainingEvent> events, SoloTrainingEventCategory category)
    {
        int loss = 0;
        foreach (var e in events.Where(e => e.Category == category))
        {
            int baseLoss = e.Severity switch
            {
                SoloTrainingEventSeverity.Safety => 12,
                SoloTrainingEventSeverity.Warning => 6,
                _ => 2,
            };
            int exposureLoss = e.Severity switch
            {
                SoloTrainingEventSeverity.Safety => (int)Math.Min(10, Math.Floor(e.ExposureSeconds / 5.0)),
                SoloTrainingEventSeverity.Warning => (int)Math.Min(5, Math.Floor(e.ExposureSeconds / 10.0)),
                _ => 0,
            };
            loss += baseLoss + exposureLoss;
        }

        return loss;
    }

    private static int ComputeApproachLoss(ApproachReportData approachReport)
    {
        int loss = 0;
        foreach (var approach in approachReport.Approaches)
        {
            loss += approach.Grade switch
            {
                "F" => 8,
                "D" => 5,
                "C" => 2,
                _ => 0,
            };
        }

        return loss;
    }

    private static int ComputeRecoveryLoss(List<SoloTrainingEvent> events) =>
        events.Count(e => e.Severity == SoloTrainingEventSeverity.Safety && e.IsActive) * 2;

    private static string GradeFor(int score) =>
        score switch
        {
            >= 90 => "A",
            >= 80 => "B",
            >= 70 => "C",
            >= 60 => "D",
            _ => "F",
        };

    private static List<string> BuildCoachingNotes(List<SoloTrainingEvent> events, ApproachReportData approachReport)
    {
        var notes = new List<string>();
        foreach (var e in events.OrderByDescending(e => e.Severity).ThenByDescending(e => e.ExposureSeconds).Take(4))
        {
            notes.Add(e.Description);
        }

        var weakApproach = approachReport.Approaches.FirstOrDefault(a => a.Grade is "D" or "F");
        if (weakApproach is not null)
        {
            notes.Add($"{weakApproach.Score.Callsign}: review {weakApproach.Score.ApproachId} setup; the approach graded {weakApproach.Grade}.");
        }

        if (notes.Count == 0)
        {
            notes.Add("No active solo-training scoring issues recorded yet.");
        }

        return notes;
    }

    internal sealed record SeparationRequirement(
        string Name,
        SoloTrainingEventCategory Category,
        string RuleReference,
        double RequiredHorizontalNm,
        double RequiredVerticalFt,
        string CoachingText
    );

    private sealed record PairSample(
        string Id,
        SoloTrainingEventCategory Category,
        SoloTrainingEventSeverity Severity,
        string Title,
        string Description,
        string RuleReference,
        double ObservedAtSeconds,
        List<string> Callsigns,
        double RequiredHorizontalNm,
        double ActualHorizontalNm,
        double RequiredVerticalFt,
        double ActualVerticalFt
    );

    private sealed class TrackedEvent
    {
        public required string Id { get; init; }
        public required SoloTrainingEventCategory Category { get; init; }
        public SoloTrainingEventSeverity Severity { get; private set; }
        public required string Title { get; set; }
        public required string Description { get; set; }
        public required string RuleReference { get; init; }
        public double StartedAtSeconds { get; init; }
        public double LastObservedAtSeconds { get; set; }
        public bool IsActive { get; set; } = true;
        public required List<string> Callsigns { get; init; }
        public string? RunwayId { get; init; }
        public double? RequiredHorizontalNm { get; set; }
        public double? ActualHorizontalNm { get; set; }
        public double? RequiredVerticalFt { get; set; }
        public double? ActualVerticalFt { get; set; }

        public static TrackedEvent FromSample(PairSample sample) =>
            new()
            {
                Id = sample.Id,
                Category = sample.Category,
                Severity = sample.Severity,
                Title = sample.Title,
                Description = sample.Description,
                RuleReference = sample.RuleReference,
                StartedAtSeconds = sample.ObservedAtSeconds,
                LastObservedAtSeconds = sample.ObservedAtSeconds,
                Callsigns = sample.Callsigns,
                RequiredHorizontalNm = sample.RequiredHorizontalNm,
                ActualHorizontalNm = sample.ActualHorizontalNm,
                RequiredVerticalFt = sample.RequiredVerticalFt,
                ActualVerticalFt = sample.ActualVerticalFt,
            };

        public void Update(PairSample sample, double scenarioElapsedSeconds)
        {
            Severity = sample.Severity > Severity ? sample.Severity : Severity;
            Title = sample.Title;
            Description = sample.Description;
            LastObservedAtSeconds = scenarioElapsedSeconds;
            IsActive = true;
            RequiredHorizontalNm = sample.RequiredHorizontalNm;
            ActualHorizontalNm = sample.ActualHorizontalNm;
            RequiredVerticalFt = sample.RequiredVerticalFt;
            ActualVerticalFt = sample.ActualVerticalFt;
        }

        public SoloTrainingEvent ToEvent() =>
            new(
                Id,
                Category,
                Severity,
                Title,
                Description,
                RuleReference,
                StartedAtSeconds,
                LastObservedAtSeconds,
                Math.Max(0.0, LastObservedAtSeconds - StartedAtSeconds),
                IsActive,
                Callsigns,
                RunwayId,
                RequiredHorizontalNm,
                ActualHorizontalNm,
                RequiredVerticalFt,
                ActualVerticalFt
            );
    }
}
