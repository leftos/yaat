using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

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
    double? ActualVerticalFt,
    string? RequiredText,
    string? ActualText
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
    private readonly SameRunwaySeparationTracker _sameRunwayTracker = new();

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

        foreach (var sample in _sameRunwayTracker.Evaluate(aircraft, scenarioElapsedSeconds))
        {
            observedThisTick.Add(sample.Id);
            var notice = Upsert(sample, scenarioElapsedSeconds);
            if (notice is not null)
            {
                notices.Add(notice);
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
        _sameRunwayTracker.Reset();
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

    private static TrainingEventSample? SamplePair(AircraftState a, AircraftState b, SeparationRequirement requirement, double scenarioElapsedSeconds)
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
            $"{a.Callsign} and {b.Callsign}: {requirement.CoachingText} Current spacing is "
            + $"{current.HorizontalNm:F1} NM and {current.VerticalFt:F0} ft.";

        return new TrainingEventSample(
            id,
            requirement.Category,
            severity.Value,
            title,
            description,
            requirement.RuleReference,
            scenarioElapsedSeconds,
            [a.Callsign, b.Callsign],
            null,
            requirement.RequiredHorizontalNm,
            current.HorizontalNm,
            requirement.RequiredVerticalFt,
            current.VerticalFt,
            null,
            null
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

    private SoloTrainingEvent? Upsert(TrainingEventSample sample, double scenarioElapsedSeconds)
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

    private sealed record TrainingEventSample(
        string Id,
        SoloTrainingEventCategory Category,
        SoloTrainingEventSeverity Severity,
        string Title,
        string Description,
        string RuleReference,
        double ObservedAtSeconds,
        List<string> Callsigns,
        string? RunwayId,
        double? RequiredHorizontalNm,
        double? ActualHorizontalNm,
        double? RequiredVerticalFt,
        double? ActualVerticalFt,
        string? RequiredText,
        string? ActualText
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
        public string? RequiredText { get; set; }
        public string? ActualText { get; set; }

        public static TrackedEvent FromSample(TrainingEventSample sample) =>
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
                RunwayId = sample.RunwayId,
                RequiredHorizontalNm = sample.RequiredHorizontalNm,
                ActualHorizontalNm = sample.ActualHorizontalNm,
                RequiredVerticalFt = sample.RequiredVerticalFt,
                ActualVerticalFt = sample.ActualVerticalFt,
                RequiredText = sample.RequiredText,
                ActualText = sample.ActualText,
            };

        public void Update(TrainingEventSample sample, double scenarioElapsedSeconds)
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
            RequiredText = sample.RequiredText;
            ActualText = sample.ActualText;
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
                ActualVerticalFt,
                RequiredText,
                ActualText
            );
    }

    private sealed class SameRunwaySeparationTracker
    {
        private readonly Dictionary<string, AircraftRunwayState> _previousStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RunwayOperation> _lastOperationByRunway = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ActiveSameRunwayViolation> _activeViolations = new(StringComparer.OrdinalIgnoreCase);

        public List<TrainingEventSample> Evaluate(List<AircraftState> aircraft, double scenarioElapsedSeconds)
        {
            var samples = new List<TrainingEventSample>();
            var currentStates = BuildCurrentStates(aircraft);

            foreach (var violation in _activeViolations.Values.ToList())
            {
                if (TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is { } sample)
                {
                    samples.Add(sample);
                }
                else
                {
                    _activeViolations.Remove(violation.Id);
                }
            }

            foreach (var state in currentStates.Values.OrderBy(s => s.Callsign, StringComparer.OrdinalIgnoreCase))
            {
                bool firstObservation = !_previousStates.TryGetValue(state.Callsign, out var previous);
                var operation = DetectOperation(state, previous, firstObservation, scenarioElapsedSeconds);
                if (operation is null)
                {
                    continue;
                }

                if (!firstObservation && _lastOperationByRunway.TryGetValue(operation.RunwayKey, out var preceding))
                {
                    if (TryCreateViolation(preceding, operation, currentStates, scenarioElapsedSeconds) is { } violation)
                    {
                        _activeViolations[violation.Id] = violation;
                        if (TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is { } sample)
                        {
                            samples.Add(sample);
                        }
                    }
                }

                _lastOperationByRunway[operation.RunwayKey] = operation;
            }

            _previousStates.Clear();
            foreach (var state in currentStates.Values)
            {
                _previousStates[state.Callsign] = state;
            }

            return samples;
        }

        public void Reset()
        {
            _previousStates.Clear();
            _lastOperationByRunway.Clear();
            _activeViolations.Clear();
        }

        private static Dictionary<string, AircraftRunwayState> BuildCurrentStates(List<AircraftState> aircraft)
        {
            var states = new Dictionary<string, AircraftRunwayState>(StringComparer.OrdinalIgnoreCase);
            foreach (var ac in aircraft)
            {
                if (TryBuildState(ac) is { } state)
                {
                    states[state.Callsign] = state;
                }
            }

            return states;
        }

        private static AircraftRunwayState? TryBuildState(AircraftState aircraft)
        {
            var runway = aircraft.Phases?.AssignedRunway;
            if (runway is null)
            {
                return null;
            }

            double alongThresholdFt =
                GeoMath.AlongTrackDistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading)
                * GeoMath.FeetPerNm;
            var phase = aircraft.Phases?.CurrentPhase;
            string runwayKey = BuildRunwayKey(runway);
            return new AircraftRunwayState(
                aircraft.Callsign,
                aircraft.AircraftType,
                aircraft.IsOnGround,
                aircraft.Position,
                phase,
                runway,
                runwayKey,
                alongThresholdFt,
                ResolveSrsCategory(aircraft)
            );
        }

        private static RunwayOperation? DetectOperation(
            AircraftRunwayState state,
            AircraftRunwayState? previous,
            bool firstObservation,
            double scenarioElapsedSeconds
        )
        {
            if (firstObservation)
            {
                return TrySeedOperation(state, scenarioElapsedSeconds);
            }

            if (
                (previous is not null)
                && !previous.IsTakeoffRoll
                && state.IsTakeoffRoll
                && string.Equals(previous.RunwayKey, state.RunwayKey, StringComparison.OrdinalIgnoreCase)
            )
            {
                return RunwayOperation.FromState(OperationKind.Departure, state, scenarioElapsedSeconds);
            }

            if (
                (previous is not null)
                && previous.IsArrivalApproach
                && state.IsArrivalOrLanding
                && string.Equals(previous.RunwayKey, state.RunwayKey, StringComparison.OrdinalIgnoreCase)
                && (previous.AlongThresholdFt < 0.0)
                && (state.AlongThresholdFt >= 0.0)
            )
            {
                return RunwayOperation.FromState(OperationKind.Landing, state, scenarioElapsedSeconds);
            }

            return null;
        }

        private static RunwayOperation? TrySeedOperation(AircraftRunwayState state, double scenarioElapsedSeconds)
        {
            if (state.IsDepartureAfterRollStart)
            {
                return RunwayOperation.FromState(OperationKind.Departure, state, scenarioElapsedSeconds);
            }

            if (state.IsLandingAfterThreshold)
            {
                return RunwayOperation.FromState(OperationKind.Landing, state, scenarioElapsedSeconds);
            }

            return null;
        }

        private static ActiveSameRunwayViolation? TryCreateViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            if (!states.TryGetValue(preceding.Callsign, out var precedingState) || !states.TryGetValue(succeeding.Callsign, out var succeedingState))
            {
                return null;
            }

            return (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) => TryCreateDepartureBehindDeparture(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds
                ),
                (OperationKind.Departure, OperationKind.Landing) => TryCreateDepartureBehindLanding(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds
                ),
                (OperationKind.Landing, OperationKind.Departure) => TryCreateArrivalBehindDeparture(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds
                ),
                (OperationKind.Landing, OperationKind.Landing) => TryCreateArrivalBehindLanding(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds
                ),
                _ => null,
            };
        }

        private static ActiveSameRunwayViolation? TryCreateDepartureBehindDeparture(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            double requiredFt = RequiredDepartureBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory);
            if (DepartureBehindDepartureSatisfied(precedingState, succeedingState, requiredFt))
            {
                return null;
            }

            return BuildViolation(
                preceding,
                succeeding,
                "Departure behind departure same-runway separation",
                "7110.65 §3-9-6(a)",
                $"Preceding departure crossed DER/runway end or airborne with {requiredFt:N0} ft spacing.",
                scenarioElapsedSeconds
            );
        }

        private static ActiveSameRunwayViolation? TryCreateDepartureBehindLanding(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            if (IsLandingClearOfRunway(precedingState))
            {
                return null;
            }

            return BuildViolation(
                preceding,
                succeeding,
                "Departure behind landing same-runway separation",
                "7110.65 §3-9-6(b)",
                "Preceding landing aircraft clear of the runway.",
                scenarioElapsedSeconds
            );
        }

        private static ActiveSameRunwayViolation? TryCreateArrivalBehindDeparture(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            double requiredFt = RequiredArrivalBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory);
            if (ArrivalBehindDepartureSatisfied(precedingState, requiredFt))
            {
                return null;
            }

            return BuildViolation(
                preceding,
                succeeding,
                "Arrival behind departure same-runway separation",
                "7110.65 §3-10-3(a)(2)",
                $"Preceding departure crossed DER/runway end or airborne at least {requiredFt:N0} ft from landing threshold.",
                scenarioElapsedSeconds
            );
        }

        private static ActiveSameRunwayViolation? TryCreateArrivalBehindLanding(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            double? exceptionFt = RequiredLandingBehindLandingExceptionFt(preceding.SrsCategory, succeeding.SrsCategory);
            if (ArrivalBehindLandingSatisfied(precedingState, exceptionFt))
            {
                return null;
            }

            string required = exceptionFt.HasValue
                ? $"Preceding landing aircraft clear of runway or landed at least {exceptionFt.Value:N0} ft from landing threshold."
                : "Preceding landing aircraft clear of the runway.";
            return BuildViolation(
                preceding,
                succeeding,
                "Arrival behind landing same-runway separation",
                "7110.65 §3-10-3(a)(1)",
                required,
                scenarioElapsedSeconds
            );
        }

        private static TrainingEventSample? TrySampleViolation(
            ActiveSameRunwayViolation violation,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            if (!states.TryGetValue(violation.Preceding.Callsign, out var precedingState))
            {
                return null;
            }

            if (!states.TryGetValue(violation.Succeeding.Callsign, out var succeedingState))
            {
                return null;
            }

            string actualText;
            bool satisfied;
            switch (violation.Rule)
            {
                case SameRunwayRule.DepartureBehindDeparture:
                    satisfied = DepartureBehindDepartureSatisfied(precedingState, succeedingState, violation.RequiredDistanceFt!.Value);
                    actualText = FormatDepartureSpacingActual(precedingState, succeedingState);
                    break;

                case SameRunwayRule.DepartureBehindLanding:
                    satisfied = IsLandingClearOfRunway(precedingState);
                    actualText = FormatRunwayClearActual(precedingState);
                    break;

                case SameRunwayRule.ArrivalBehindDeparture:
                    satisfied = ArrivalBehindDepartureSatisfied(precedingState, violation.RequiredDistanceFt!.Value);
                    actualText = FormatThresholdDistanceActual(precedingState);
                    break;

                case SameRunwayRule.ArrivalBehindLanding:
                    satisfied = ArrivalBehindLandingSatisfied(precedingState, violation.RequiredDistanceFt);
                    actualText = FormatRunwayClearOrThresholdActual(precedingState);
                    break;

                default:
                    satisfied = true;
                    actualText = "";
                    break;
            }

            if (satisfied)
            {
                return null;
            }

            string description =
                $"{violation.Succeeding.Callsign} used runway {violation.RunwayId} behind "
                + $"{violation.Preceding.Callsign} before same-runway separation existed.";

            return new TrainingEventSample(
                violation.Id,
                SoloTrainingEventCategory.RunwayWake,
                SoloTrainingEventSeverity.Safety,
                violation.Title,
                description,
                violation.RuleReference,
                scenarioElapsedSeconds,
                [violation.Preceding.Callsign, violation.Succeeding.Callsign],
                violation.RunwayId,
                null,
                null,
                null,
                null,
                violation.RequiredText,
                actualText
            );
        }

        private static ActiveSameRunwayViolation BuildViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            string title,
            string ruleReference,
            string requiredText,
            double scenarioElapsedSeconds
        )
        {
            var rule = (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) => SameRunwayRule.DepartureBehindDeparture,
                (OperationKind.Departure, OperationKind.Landing) => SameRunwayRule.DepartureBehindLanding,
                (OperationKind.Landing, OperationKind.Departure) => SameRunwayRule.ArrivalBehindDeparture,
                (OperationKind.Landing, OperationKind.Landing) => SameRunwayRule.ArrivalBehindLanding,
                _ => SameRunwayRule.DepartureBehindDeparture,
            };
            double? requiredDistanceFt = rule switch
            {
                SameRunwayRule.DepartureBehindDeparture => RequiredDepartureBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory),
                SameRunwayRule.ArrivalBehindDeparture => RequiredArrivalBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory),
                SameRunwayRule.ArrivalBehindLanding => RequiredLandingBehindLandingExceptionFt(preceding.SrsCategory, succeeding.SrsCategory),
                _ => null,
            };
            string id = MakeSameRunwayEventId(preceding, succeeding, ruleReference, scenarioElapsedSeconds);
            return new ActiveSameRunwayViolation(id, rule, preceding, succeeding, title, ruleReference, requiredText, requiredDistanceFt);
        }

        private static bool DepartureBehindDepartureSatisfied(
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double requiredFt
        )
        {
            if (HasCrossedRunwayEnd(precedingState))
            {
                return true;
            }

            double spacingFt = precedingState.AlongThresholdFt - succeedingState.AlongThresholdFt;
            return !precedingState.IsOnGround && (spacingFt >= requiredFt);
        }

        private static bool ArrivalBehindDepartureSatisfied(AircraftRunwayState precedingState, double requiredFt) =>
            HasCrossedRunwayEnd(precedingState) || (!precedingState.IsOnGround && (precedingState.AlongThresholdFt >= requiredFt));

        private static bool ArrivalBehindLandingSatisfied(AircraftRunwayState precedingState, double? requiredExceptionFt)
        {
            if (IsLandingClearOfRunway(precedingState))
            {
                return true;
            }

            return requiredExceptionFt.HasValue && precedingState.IsOnGround && (precedingState.AlongThresholdFt >= requiredExceptionFt.Value);
        }

        private static bool HasCrossedRunwayEnd(AircraftRunwayState state) => state.AlongThresholdFt >= state.Runway.LengthFt;

        private static bool IsLandingClearOfRunway(AircraftRunwayState state)
        {
            if (!state.IsOnGround)
            {
                return true;
            }

            return state.Phase is not (LandingPhase or RunwayExitPhase or StopAndGoPhase or TouchAndGoPhase);
        }

        private static double RequiredDepartureBehindDepartureFt(SrsCategory preceding, SrsCategory succeeding)
        {
            if (preceding == SrsCategory.III || succeeding == SrsCategory.III)
            {
                return 6000.0;
            }

            if (preceding == SrsCategory.II || succeeding == SrsCategory.II)
            {
                return 4500.0;
            }

            return 3000.0;
        }

        private static double RequiredArrivalBehindDepartureFt(SrsCategory preceding, SrsCategory succeeding)
        {
            if (preceding == SrsCategory.III || succeeding == SrsCategory.III)
            {
                return 6000.0;
            }

            return succeeding == SrsCategory.II ? 4500.0 : 3000.0;
        }

        private static double? RequiredLandingBehindLandingExceptionFt(SrsCategory preceding, SrsCategory succeeding)
        {
            if (preceding == SrsCategory.III || succeeding == SrsCategory.III)
            {
                return null;
            }

            return succeeding == SrsCategory.II ? 4500.0 : 3000.0;
        }

        private static SrsCategory ResolveSrsCategory(AircraftState aircraft)
        {
            var record = FaaAircraftDatabase.Get(aircraft.AircraftType);
            if (record?.Srs is { Length: > 0 } srs)
            {
                if (srs.Equals("I", StringComparison.OrdinalIgnoreCase))
                {
                    return SrsCategory.I;
                }

                if (srs.Equals("II", StringComparison.OrdinalIgnoreCase))
                {
                    return SrsCategory.II;
                }

                if (srs.Equals("III", StringComparison.OrdinalIgnoreCase))
                {
                    return SrsCategory.III;
                }
            }

            if (record is not null)
            {
                bool small = (record.MtowLb ?? double.MaxValue) <= 12500.0;
                bool prop =
                    (record.PhysicalClassEngine?.Contains("Piston", StringComparison.OrdinalIgnoreCase) == true)
                    || (record.PhysicalClassEngine?.Contains("Prop", StringComparison.OrdinalIgnoreCase) == true);
                bool helicopter =
                    (record.Class?.Contains("Helicopter", StringComparison.OrdinalIgnoreCase) == true)
                    || (record.PhysicalClassEngine?.Contains("Turboshaft", StringComparison.OrdinalIgnoreCase) == true);

                if (helicopter || (small && prop && record.NumEngines == 1))
                {
                    return SrsCategory.I;
                }

                if (small && prop && record.NumEngines == 2)
                {
                    return SrsCategory.II;
                }
            }

            return AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter ? SrsCategory.I : SrsCategory.III;
        }

        private static string FormatDepartureSpacingActual(AircraftRunwayState precedingState, AircraftRunwayState succeedingState)
        {
            if (HasCrossedRunwayEnd(precedingState))
            {
                return "preceding departure crossed DER/runway end";
            }

            double spacingFt = Math.Max(0.0, precedingState.AlongThresholdFt - succeedingState.AlongThresholdFt);
            return precedingState.IsOnGround
                ? $"preceding departure still on runway, {spacingFt:N0} ft ahead"
                : $"airborne, {spacingFt:N0} ft spacing";
        }

        private static string FormatThresholdDistanceActual(AircraftRunwayState state)
        {
            if (HasCrossedRunwayEnd(state))
            {
                return "preceding departure crossed DER/runway end";
            }

            return state.IsOnGround
                ? $"preceding departure still on runway, {state.AlongThresholdFt:N0} ft from threshold"
                : $"{state.AlongThresholdFt:N0} ft from threshold";
        }

        private static string FormatRunwayClearActual(AircraftRunwayState state) =>
            IsLandingClearOfRunway(state)
                ? "preceding landing clear of runway"
                : $"preceding landing still on runway ({state.Phase?.Name ?? "unknown"})";

        private static string FormatRunwayClearOrThresholdActual(AircraftRunwayState state) =>
            IsLandingClearOfRunway(state) ? "preceding landing clear of runway" : $"preceding landing {state.AlongThresholdFt:N0} ft from threshold";

        private static string BuildRunwayKey(RunwayInfo runway) => $"{runway.AirportId}/{runway.Designator}".ToUpperInvariant();

        private static string MakeSameRunwayEventId(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            string ruleReference,
            double scenarioElapsedSeconds
        )
        {
            string normalizedRule = ruleReference.Replace(' ', '_').Replace('/', '_').Replace('§', 'S').Replace('(', '_').Replace(')', '_');
            string runway = preceding.RunwayKey.Replace('/', '_');
            return $"SRS_{runway}_{preceding.Callsign}_{succeeding.Callsign}_{normalizedRule}_{scenarioElapsedSeconds:F0}".ToUpperInvariant();
        }

        private sealed record AircraftRunwayState(
            string Callsign,
            string AircraftType,
            bool IsOnGround,
            LatLon Position,
            Phase? Phase,
            RunwayInfo Runway,
            string RunwayKey,
            double AlongThresholdFt,
            SrsCategory SrsCategory
        )
        {
            public bool IsTakeoffRoll => IsOnGround && Phase is TakeoffPhase;
            public bool IsDepartureAfterRollStart => Phase is TakeoffPhase or InitialClimbPhase;
            public bool IsArrivalApproach => Phase is FinalApproachPhase or LandingPhase;
            public bool IsArrivalOrLanding => Phase is FinalApproachPhase or LandingPhase or RunwayExitPhase;
            public bool IsLandingAfterThreshold => (AlongThresholdFt >= 0.0) && (Phase is LandingPhase or RunwayExitPhase or HoldingAfterExitPhase);
        }

        private sealed record RunwayOperation(
            OperationKind Kind,
            string Callsign,
            string AircraftType,
            RunwayInfo Runway,
            string RunwayKey,
            SrsCategory SrsCategory,
            double TriggeredAtSeconds
        )
        {
            public string RunwayId => Runway.Designator;

            public static RunwayOperation FromState(OperationKind kind, AircraftRunwayState state, double scenarioElapsedSeconds) =>
                new(kind, state.Callsign, state.AircraftType, state.Runway, state.RunwayKey, state.SrsCategory, scenarioElapsedSeconds);
        }

        private sealed record ActiveSameRunwayViolation(
            string Id,
            SameRunwayRule Rule,
            RunwayOperation Preceding,
            RunwayOperation Succeeding,
            string Title,
            string RuleReference,
            string RequiredText,
            double? RequiredDistanceFt
        )
        {
            public string RunwayId => Preceding.RunwayId;
        }

        private enum OperationKind
        {
            Departure,
            Landing,
        }

        private enum SameRunwayRule
        {
            DepartureBehindDeparture,
            DepartureBehindLanding,
            ArrivalBehindDeparture,
            ArrivalBehindLanding,
        }

        private enum SrsCategory
        {
            I,
            II,
            III,
        }
    }
}
