using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;

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
    ApproachReportData ApproachReport,
    IReadOnlyList<AircraftDebriefData> AircraftDebriefs
);

public sealed record SoloTrainingServiceContext(InitialContactEligibilityContext InitialContactEligibility, WakeDirectiveCatalog WakeDirectives)
{
    public static SoloTrainingServiceContext Empty { get; } = new(InitialContactEligibilityContext.Empty, WakeDirectiveCatalog.Empty);
}

public sealed class SoloTrainingEvaluator
{
    private const double TerminalRadarHorizontalNm = 3.0;
    private const double IfrVerticalFt = 1000.0;
    private const double ClassBHeavyOrTurbojetHorizontalNm = 1.5;
    private const double ClassBTargetResolutionNm = 0.25;
    private const double ClassBVerticalFt = 500.0;
    private const double ClassCTargetResolutionNm = 0.25;
    private const double ClassCVerticalFt = 500.0;
    private const double WarningMargin = 1.10;
    private const double WarningLookaheadSeconds = 30.0;
    private const double CoachLookaheadSeconds = 60.0;

    private readonly Dictionary<string, TrackedEvent> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _advisoryProofs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fieldAdvisoryProofs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WakeAdvisoryProof> _wakeAdvisoryProofs = [];
    private readonly List<SafetyAlertProof> _safetyAlertProofs = [];
    private readonly SameRunwaySeparationTracker _sameRunwayTracker = new();

    // Debrief cache. BuildReport runs every 2-5 seconds in production while the inputs
    // (active aircraft + completed records + timeline findings) usually haven't changed.
    // A cheap structural hash of the inputs skips the O(n×m) rebuild and per-row record
    // allocation when nothing relevant changed.
    private long _lastDebriefHash;
    private IReadOnlyList<AircraftDebriefData>? _lastDebriefs;

    public void RecordControllerCommand(
        AircraftState aircraft,
        CompoundCommand command,
        double scenarioElapsedSeconds,
        IReadOnlyList<AircraftState> knownAircraft
    )
    {
        foreach (var parsedCommand in EnumerateImmediatelyAppliedCommands(command))
        {
            if (parsedCommand is ContactCommand or FrequencyChangeApprovedCommand)
            {
                aircraft.HasLeftStudentFrequency = true;
            }
            else if (parsedCommand is ReportFieldAdvisoryCommand)
            {
                _fieldAdvisoryProofs.Add(NormalizeCallsign(aircraft.Callsign));
            }
            else if (parsedCommand is ReportTrafficAdvisoryCommand rtis)
            {
                var target = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(aircraft, rtis.Details, knownAircraft, out _);
                if (target is not null)
                {
                    string key = MakeAdvisoryProofKey(aircraft.Callsign, target.Callsign);
                    _advisoryProofs.Add(key);
                }
            }
            else if (parsedCommand is SafetyAlertCommand safetyAlert)
            {
                var target = TrafficAdvisoryMatcher.ResolveSafetyAlertTarget(aircraft, safetyAlert.Details, knownAircraft, out _);
                if (target is not null)
                {
                    _safetyAlertProofs.Add(new SafetyAlertProof(aircraft.Callsign, target.Callsign, scenarioElapsedSeconds));
                }
            }
            else if (parsedCommand is WakeAdvisoryCommand)
            {
                var currentWakeContexts = _sameRunwayTracker
                    .SampleActiveWakeContexts([.. knownAircraft], scenarioElapsedSeconds)
                    .Where(context => context.SucceedingCallsign.Equals(aircraft.Callsign, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (currentWakeContexts.Count == 1)
                {
                    _wakeAdvisoryProofs.Add(new WakeAdvisoryProof(aircraft.Callsign, scenarioElapsedSeconds, currentWakeContexts[0].SourceEventId));
                }
            }
            else if (
                parsedCommand is ClearedForTakeoffCommand { CautionWakeTurbulence: true } or ClearedToLandCommand { CautionWakeTurbulence: true }
            )
            {
                _wakeAdvisoryProofs.Add(new WakeAdvisoryProof(aircraft.Callsign, scenarioElapsedSeconds, sourceEventId: null));
            }
        }
    }

    public List<SoloTrainingEvent> Evaluate(List<AircraftState> aircraft, double scenarioElapsedSeconds, AirspaceDatabase airspace)
    {
        return Evaluate(aircraft, scenarioElapsedSeconds, airspace, SoloTrainingServiceContext.Empty);
    }

    public List<SoloTrainingEvent> Evaluate(
        List<AircraftState> aircraft,
        double scenarioElapsedSeconds,
        AirspaceDatabase airspace,
        TrackOwner? studentPosition
    )
    {
        return Evaluate(
            aircraft,
            scenarioElapsedSeconds,
            airspace,
            new SoloTrainingServiceContext(
                new InitialContactEligibilityContext(studentPosition, null, null, null, InitialContactTransferCatalog.Empty),
                WakeDirectiveCatalog.Empty
            )
        );
    }

    public List<SoloTrainingEvent> Evaluate(
        List<AircraftState> aircraft,
        double scenarioElapsedSeconds,
        AirspaceDatabase airspace,
        SoloTrainingServiceContext serviceContext
    )
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
                if (IsCoveredByVisualFollow(a, b))
                {
                    continue;
                }

                var separationSample = SamplePair(a, b, airspace, scenarioElapsedSeconds);
                if (separationSample is not null)
                {
                    var sample = separationSample.Event;
                    var requirement = separationSample.Requirement;
                    observedThisTick.Add(sample.Id);
                    var notice = Upsert(sample, scenarioElapsedSeconds);
                    if (notice is not null)
                    {
                        notices.Add(notice);
                    }

                    foreach (var advisorySample in SampleAdvisoryPair(a, b, requirement, sample, scenarioElapsedSeconds, serviceContext))
                    {
                        observedThisTick.Add(advisorySample.Id);
                        var advisoryNotice = Upsert(advisorySample, scenarioElapsedSeconds);
                        if (advisoryNotice is not null)
                        {
                            notices.Add(advisoryNotice);
                        }
                    }
                }
                else
                {
                    foreach (var advisorySample in SampleNoMinimaAdvisoryPair(a, b, airspace, scenarioElapsedSeconds, serviceContext))
                    {
                        observedThisTick.Add(advisorySample.Id);
                        var advisoryNotice = Upsert(advisorySample, scenarioElapsedSeconds);
                        if (advisoryNotice is not null)
                        {
                            notices.Add(advisoryNotice);
                        }
                    }
                }
            }
        }

        foreach (var aircraftState in eligible)
        {
            var visualSample = SampleVisualApproach(aircraftState, scenarioElapsedSeconds, serviceContext);
            if (visualSample is null)
            {
                continue;
            }

            observedThisTick.Add(visualSample.Id);
            var visualNotice = Upsert(visualSample, scenarioElapsedSeconds);
            if (visualNotice is not null)
            {
                notices.Add(visualNotice);
            }
        }

        foreach (var overuseSample in SampleSafetyAlertOveruse(scenarioElapsedSeconds))
        {
            observedThisTick.Add(overuseSample.Id);
            var notice = Upsert(overuseSample, scenarioElapsedSeconds);
            if (notice is not null)
            {
                notices.Add(notice);
            }
        }

        var runwayEvaluation = _sameRunwayTracker.Evaluate(aircraft, scenarioElapsedSeconds, serviceContext);
        foreach (
            var wakeAdvisorySample in SampleWakeAdvisoryProofs(
                runwayEvaluation.RunwayEvents,
                runwayEvaluation.WakeContexts,
                aircraft,
                scenarioElapsedSeconds,
                serviceContext
            )
        )
        {
            observedThisTick.Add(wakeAdvisorySample.Id);
            var notice = Upsert(wakeAdvisorySample, scenarioElapsedSeconds);
            if (notice is not null)
            {
                notices.Add(notice);
            }
        }

        foreach (var sample in runwayEvaluation.RunwayEvents.Select(e => e.Sample))
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

    public SoloTrainingReportData BuildReport(
        bool soloTrainingMode,
        double scenarioElapsedSeconds,
        ApproachReportData approachReport,
        AircraftDebriefContext debriefContext
    )
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

        var debriefs = BuildAircraftDebriefs(timeline, debriefContext);

        return new SoloTrainingReportData(
            soloTrainingMode,
            scenarioElapsedSeconds,
            score,
            GradeFor(score),
            buckets,
            active,
            timeline,
            BuildCoachingNotes(timeline, approachReport),
            approachReport,
            debriefs
        );
    }

    private IReadOnlyList<AircraftDebriefData> BuildAircraftDebriefs(IReadOnlyList<SoloTrainingEvent> timeline, AircraftDebriefContext debriefContext)
    {
        if (debriefContext.ActiveAircraft.Count == 0 && debriefContext.CompletedAircraft.Count == 0)
        {
            _lastDebriefs = null;
            _lastDebriefHash = 0;
            return [];
        }

        long hash = ComputeDebriefInputsHash(timeline, debriefContext);
        if (_lastDebriefs is not null && _lastDebriefHash == hash)
        {
            return _lastDebriefs;
        }

        // Group findings by participating callsign. SoloTrainingEvent.Callsigns lists every
        // aircraft involved in the finding, so the same finding shows up in both aircraft's
        // debrief blocks (which is what we want — separation losses are shared blame).
        var findingsByCallsign = new Dictionary<string, List<SoloTrainingEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in timeline)
        {
            foreach (var cs in ev.Callsigns)
            {
                if (string.IsNullOrWhiteSpace(cs))
                {
                    continue;
                }
                if (!findingsByCallsign.TryGetValue(cs, out var list))
                {
                    list = [];
                    findingsByCallsign[cs] = list;
                }
                list.Add(ev);
            }
        }

        var results = new List<AircraftDebriefData>(debriefContext.ActiveAircraft.Count + debriefContext.CompletedAircraft.Count);

        // Active aircraft come from the live world.
        foreach (var ac in debriefContext.ActiveAircraft)
        {
            results.Add(BuildDebriefForActive(ac, debriefContext.PrimaryAirportId, findingsByCallsign));
        }

        // Completed-and-removed aircraft come from SimulationWorld's registry. Skip any
        // callsign that's also active (the live entry wins — possible during a same-callsign
        // respawn after a removal). Within the completed list, dedupe by callsign keeping
        // the highest CompletedAtSeconds: if a controller deletes an aircraft, respawns the
        // same callsign, and lands it again, the registry holds two records; the user only
        // wants to see the most recent run on the Aircraft tab.
        var liveCallsigns = new HashSet<string>(debriefContext.ActiveAircraft.Select(a => a.Callsign), StringComparer.OrdinalIgnoreCase);
        var latestByCallsign = new Dictionary<string, CompletedAircraftRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in debriefContext.CompletedAircraft)
        {
            if (liveCallsigns.Contains(record.Callsign))
            {
                continue;
            }
            if (!latestByCallsign.TryGetValue(record.Callsign, out var existing) || record.CompletedAtSeconds > existing.CompletedAtSeconds)
            {
                latestByCallsign[record.Callsign] = record;
            }
        }
        foreach (var record in latestByCallsign.Values)
        {
            results.Add(BuildDebriefForCompleted(record, debriefContext.PrimaryAirportId, findingsByCallsign));
        }

        // Sort by spawn time so the timeline reads chronologically.
        results.Sort((a, b) => a.SpawnedAtSeconds.CompareTo(b.SpawnedAtSeconds));
        _lastDebriefs = results;
        _lastDebriefHash = hash;
        return results;
    }

    // Structural hash over the inputs that affect debrief output. Cheap to compute; if
    // it matches the previous call, the cached result list can be reused. Must cover
    // every field BuildDebriefForActive / BuildDebriefForCompleted consume — including
    // AircraftType and FlightPlan.Departure/Destination, which drive OperationKind
    // classification and the displayed route.
    private static long ComputeDebriefInputsHash(IReadOnlyList<SoloTrainingEvent> timeline, AircraftDebriefContext debriefContext)
    {
        // FNV-1a 64-bit basis / prime. Order-sensitive within each input list; we don't
        // need order-independence since the inputs are produced in a stable order.
        unchecked
        {
            const ulong FnvOffset = 14695981039346656037UL;
            const ulong FnvPrime = 1099511628211UL;
            ulong h = FnvOffset;

            h = Combine(h, (ulong)debriefContext.ActiveAircraft.Count);
            foreach (var ac in debriefContext.ActiveAircraft)
            {
                h = CombineString(h, ac.Callsign);
                h = CombineString(h, ac.Cid);
                h = CombineString(h, ac.AircraftType);
                h = CombineString(h, ac.FlightPlan.Departure);
                h = CombineString(h, ac.FlightPlan.Destination);
                h = Combine(h, (ulong)ac.CompletionReason);
                h = CombineString(h, ac.CompletionDetail);
                h = Combine(h, BitConverter.DoubleToUInt64Bits(ac.SpawnedAtSeconds));
                h = Combine(h, BitConverter.DoubleToUInt64Bits(ac.CompletedAtSeconds ?? double.NaN));
            }

            h = Combine(h, (ulong)debriefContext.CompletedAircraft.Count);
            foreach (var rec in debriefContext.CompletedAircraft)
            {
                h = CombineString(h, rec.Callsign);
                h = Combine(h, BitConverter.DoubleToUInt64Bits(rec.CompletedAtSeconds));
                h = Combine(h, (ulong)rec.Reason);
            }

            h = CombineString(h, debriefContext.PrimaryAirportId);

            h = Combine(h, (ulong)timeline.Count);
            foreach (var ev in timeline)
            {
                h = CombineString(h, ev.Id);
                h = Combine(h, (ulong)ev.Severity);
                h = Combine(h, ev.IsActive ? 1UL : 0UL);
            }

            return (long)h;

            static ulong Combine(ulong acc, ulong v) => (acc ^ v) * FnvPrime;
            static ulong CombineString(ulong acc, string? s)
            {
                if (s is null)
                {
                    return Combine(acc, 0);
                }
                ulong h2 = acc;
                foreach (var ch in s)
                {
                    h2 = (h2 ^ ch) * FnvPrime;
                }
                return h2;
            }
        }
    }

    private static AircraftDebriefData BuildDebriefForActive(
        AircraftState ac,
        string? primaryAirportId,
        Dictionary<string, List<SoloTrainingEvent>> findingsByCallsign
    )
    {
        string? departure = string.IsNullOrEmpty(ac.FlightPlan.Departure) ? null : ac.FlightPlan.Departure;
        string? destination = string.IsNullOrEmpty(ac.FlightPlan.Destination) ? null : ac.FlightPlan.Destination;
        var operation = ClassifyOperation(departure, destination, primaryAirportId);
        var findings = findingsByCallsign.GetValueOrDefault(ac.Callsign) ?? [];
        return BuildDebriefRow(
            ac.Callsign,
            ac.AircraftType,
            departure,
            destination,
            operation,
            ac.SpawnedAtSeconds,
            ac.CompletedAtSeconds,
            ac.CompletionReason,
            ac.CompletionDetail,
            findings
        );
    }

    private static AircraftDebriefData BuildDebriefForCompleted(
        CompletedAircraftRecord record,
        string? primaryAirportId,
        Dictionary<string, List<SoloTrainingEvent>> findingsByCallsign
    )
    {
        var operation = ClassifyOperation(record.FiledDeparture, record.FiledDestination, primaryAirportId);
        var findings = findingsByCallsign.GetValueOrDefault(record.Callsign) ?? [];
        return BuildDebriefRow(
            record.Callsign,
            record.AircraftType,
            record.FiledDeparture,
            record.FiledDestination,
            operation,
            record.SpawnedAtSeconds,
            record.CompletedAtSeconds,
            record.Reason,
            record.Detail,
            findings
        );
    }

    private static AircraftDebriefData BuildDebriefRow(
        string callsign,
        string aircraftType,
        string? departure,
        string? destination,
        OperationKind operation,
        double spawnedAt,
        double? completedAt,
        CompletionReason completionReason,
        string? completionDetail,
        IReadOnlyList<SoloTrainingEvent> findings
    )
    {
        int separation = 0;
        int runwayWake = 0;
        int advisory = 0;
        int approachCount = 0;
        int coach = 0;
        int warning = 0;
        int safety = 0;
        SoloTrainingEvent? topFinding = null;
        var findingIds = new List<string>(findings.Count);

        foreach (var f in findings)
        {
            findingIds.Add(f.Id);
            switch (f.Category)
            {
                case SoloTrainingEventCategory.Separation:
                    separation++;
                    break;
                case SoloTrainingEventCategory.RunwayWake:
                    runwayWake++;
                    break;
                case SoloTrainingEventCategory.AdvisoryVisual:
                    advisory++;
                    break;
                case SoloTrainingEventCategory.Approach:
                    approachCount++;
                    break;
            }
            switch (f.Severity)
            {
                case SoloTrainingEventSeverity.Coach:
                    coach++;
                    break;
                case SoloTrainingEventSeverity.Warning:
                    warning++;
                    break;
                case SoloTrainingEventSeverity.Safety:
                    safety++;
                    break;
            }

            // Top finding = highest severity, breaking ties by longest active exposure (most
            // recent activity if both inactive). Mirrors the ordering used for the ActiveEvents
            // pane so the coaching note matches what the controller sees at the top.
            if (topFinding is null || CompareForTop(f, topFinding) > 0)
            {
                topFinding = f;
            }
        }

        var note = AircraftDebriefCoachingTemplates.Build(operation, completionReason, completionDetail, topFinding, findings.Count);

        return new AircraftDebriefData(
            callsign,
            aircraftType,
            departure,
            destination,
            operation,
            spawnedAt,
            completedAt,
            completionReason,
            completionDetail,
            separation,
            runwayWake,
            advisory,
            approachCount,
            coach,
            warning,
            safety,
            note,
            findingIds
        );
    }

    private static int CompareForTop(SoloTrainingEvent candidate, SoloTrainingEvent incumbent)
    {
        int sev = candidate.Severity.CompareTo(incumbent.Severity);
        if (sev != 0)
        {
            return sev;
        }
        return candidate.ExposureSeconds.CompareTo(incumbent.ExposureSeconds);
    }

    private static OperationKind ClassifyOperation(string? departure, string? destination, string? primaryAirportId)
    {
        if (string.IsNullOrWhiteSpace(primaryAirportId))
        {
            return string.IsNullOrWhiteSpace(departure) && string.IsNullOrWhiteSpace(destination) ? OperationKind.Unknown : OperationKind.Transit;
        }

        bool matchesDeparture = AirportIdMatches(departure, primaryAirportId);
        bool matchesDestination = AirportIdMatches(destination, primaryAirportId);

        // Pattern work (dep == dest == primary) reads as Departure since the aircraft
        // originated there. The completion reason (Landed vs HandedOff) makes the actual
        // outcome clear in the UI.
        if (matchesDeparture)
        {
            return OperationKind.Departure;
        }
        if (matchesDestination)
        {
            return OperationKind.Arrival;
        }
        if (string.IsNullOrWhiteSpace(departure) && string.IsNullOrWhiteSpace(destination))
        {
            return OperationKind.Unknown;
        }
        return OperationKind.Transit;
    }

    // FAA scenarios use 3-letter codes ("OAK"); some flight plans carry the ICAO prefix
    // ("KOAK"). Match either form against either form.
    private static bool AirportIdMatches(string? candidate, string primary)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }
        var c = candidate.Trim();
        var p = primary.Trim();
        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Tolerate the optional leading 'K' / 'P' prefix on US/Pacific airports.
        return Strip(c).Equals(Strip(p), StringComparison.OrdinalIgnoreCase);

        static string Strip(string s) => s.Length == 4 && (s[0] == 'K' || s[0] == 'P' || s[0] == 'k' || s[0] == 'p') ? s[1..] : s;
    }

    public void Reset()
    {
        _events.Clear();
        _advisoryProofs.Clear();
        _fieldAdvisoryProofs.Clear();
        _wakeAdvisoryProofs.Clear();
        _safetyAlertProofs.Clear();
        _sameRunwayTracker.Reset();
        _lastDebriefs = null;
        _lastDebriefHash = 0;
    }

    internal static SeparationRequirement? ResolveRequirement(AircraftState a, AircraftState b, AirspaceDatabase airspace)
    {
        return ResolveRequirement(a, b, airspace, lookaheadSeconds: 0.0);
    }

    internal static SeparationRequirement? ResolveRequirement(AircraftState a, AircraftState b, AirspaceDatabase airspace, double lookaheadSeconds)
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

        var applicableClasses = FindApplicableAirspaceClasses(a, b, airspace, lookaheadSeconds);
        if (applicableClasses.Contains(AirspaceClass.Bravo))
        {
            bool heavyOrTurbojetPair = IsLargeOrTurbojet(a) || IsLargeOrTurbojet(b);
            if (!heavyOrTurbojetPair)
            {
                return new SeparationRequirement(
                    "Class B target-resolution separation",
                    SoloTrainingEventCategory.Separation,
                    "7110.65 §7-9-4",
                    ClassBTargetResolutionNm,
                    ClassBVerticalFt,
                    "VFR aircraft in Class B require target resolution, 500 ft vertical, or visual separation from aircraft 19,000 lb or less."
                );
            }

            return new SeparationRequirement(
                "Class B large/turbojet separation",
                SoloTrainingEventCategory.Separation,
                "7110.65 §7-9-4",
                ClassBHeavyOrTurbojetHorizontalNm,
                ClassBVerticalFt,
                "VFR aircraft in Class B require 1.5 NM, 500 ft vertical, or visual separation from turbojets and aircraft over 19,000 lb."
            );
        }

        if ((applicableClasses.Contains(AirspaceClass.Charlie) || IsClassCOuterAreaPair(a, b, airspace, lookaheadSeconds)) && (aVfr != bVfr))
        {
            return new SeparationRequirement(
                applicableClasses.Contains(AirspaceClass.Charlie)
                    ? "Class C IFR/VFR target-resolution separation"
                    : "Class C outer-area IFR/VFR target-resolution separation",
                SoloTrainingEventCategory.Separation,
                "7110.65 §7-8-2; 7110.65 §7-8-3; AIM §3-2-4",
                ClassCTargetResolutionNm,
                ClassCVerticalFt,
                "VFR aircraft in Class C require target resolution, 500 ft vertical, or visual separation from IFR aircraft."
            );
        }

        return null;
    }

    private static bool IsClassCOuterAreaPair(AircraftState a, AircraftState b, AirspaceDatabase airspace, double lookaheadSeconds) =>
        (IsInClassCOuterArea(a, airspace, lookaheadSeconds)) || (IsInClassCOuterArea(b, airspace, lookaheadSeconds));

    private static bool IsInClassCOuterArea(AircraftState aircraft, AirspaceDatabase airspace, double lookaheadSeconds)
    {
        var position = ProjectPosition(aircraft, lookaheadSeconds);
        double altitude = ProjectAltitude(aircraft, lookaheadSeconds);
        var navDb = NavigationDatabase.Instance;

        foreach (
            var group in airspace
                .Volumes.Where(v => v.Class == AirspaceClass.Charlie)
                .GroupBy(v => !string.IsNullOrWhiteSpace(v.IcaoId) ? v.IcaoId : v.Ident)
        )
        {
            var airportId = group.Key;
            var airport = navDb.GetFixPosition(airportId) ?? navDb.GetFixPosition("K" + airportId);
            if (airport is null)
            {
                continue;
            }

            var airportPosition = new LatLon(airport.Value.Lat, airport.Value.Lon);
            double ceiling = group.Max(v => v.UpperFtMsl);
            if (GeoMath.DistanceNm(position, airportPosition) <= 20.0 && altitude <= ceiling)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEligibleAirborneTarget(AircraftState aircraft) =>
        !aircraft.IsOnGround && aircraft.Transponder.Mode.Equals("C", StringComparison.OrdinalIgnoreCase) && !aircraft.Ghost.IsUnsupported;

    private static HashSet<AirspaceClass> FindApplicableAirspaceClasses(
        AircraftState a,
        AircraftState b,
        AirspaceDatabase airspace,
        double lookaheadSeconds
    )
    {
        var classes = new HashSet<AirspaceClass>();
        foreach (var volume in FindContainingProjectedAirspace(a, airspace, lookaheadSeconds))
        {
            classes.Add(volume.Class);
        }

        foreach (var volume in FindContainingProjectedAirspace(b, airspace, lookaheadSeconds))
        {
            classes.Add(volume.Class);
        }

        return classes;
    }

    private static IEnumerable<AirspaceVolume> FindContainingProjectedAirspace(
        AircraftState aircraft,
        AirspaceDatabase airspace,
        double lookaheadSeconds
    )
    {
        var position = ProjectPosition(aircraft, lookaheadSeconds);
        double altitude = ProjectAltitude(aircraft, lookaheadSeconds);
        return airspace.FindContaining(position, altitude);
    }

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

    private static string? ResolveTrafficAdvisoryTarget(AircraftState aircraft, ParsedCommand command) =>
        command switch
        {
            ReportTrafficInSightCommand rtis => ResolveTrafficAdvisoryTarget(aircraft, rtis.TargetCallsign),
            ReportTrafficInSightForcedCommand rtisf => ResolveTrafficAdvisoryTarget(aircraft, rtisf.TargetCallsign),
            _ => null,
        };

    private static string? ResolveTrafficAdvisoryTarget(AircraftState aircraft, string? commandTarget) =>
        !string.IsNullOrWhiteSpace(commandTarget) ? commandTarget.Trim().ToUpperInvariant()
        : !string.IsNullOrWhiteSpace(aircraft.Approach.LastReportedTrafficCallsign)
            ? aircraft.Approach.LastReportedTrafficCallsign.Trim().ToUpperInvariant()
        : null;

    private static IEnumerable<ParsedCommand> EnumerateImmediatelyAppliedCommands(CompoundCommand command)
    {
        if (IsUnconditionedTransparentCompound(command))
        {
            return command.Blocks.SelectMany(block => block.Commands);
        }

        var firstBlock = command.Blocks.FirstOrDefault();
        return firstBlock?.Condition is null ? firstBlock?.Commands ?? [] : [];
    }

    private static bool IsUnconditionedTransparentCompound(CompoundCommand command)
    {
        foreach (var block in command.Blocks)
        {
            if (block.Condition is not null)
            {
                return false;
            }

            foreach (var commandInBlock in block.Commands)
            {
                if (commandInBlock is UnsupportedCommand)
                {
                    return false;
                }

                if (!CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(commandInBlock)))
                {
                    return false;
                }
            }
        }

        return command.Blocks.Count > 0;
    }

    private static SeparationTrainingSample? SamplePair(AircraftState a, AircraftState b, AirspaceDatabase airspace, double scenarioElapsedSeconds)
    {
        var current = ComputeSeparation(a, b, lookaheadSeconds: 0.0);
        var projected30 = ComputeSeparation(a, b, WarningLookaheadSeconds);
        var projected60 = ComputeSeparation(a, b, CoachLookaheadSeconds);
        var currentRequirement = ResolveRequirement(a, b, airspace, lookaheadSeconds: 0.0);
        var projected30Requirement = ResolveRequirement(a, b, airspace, WarningLookaheadSeconds);
        var projected60Requirement = ResolveRequirement(a, b, airspace, CoachLookaheadSeconds);

        bool currentViolation = (currentRequirement is not null) && Violates(current.HorizontalNm, current.VerticalFt, currentRequirement);
        bool warningViolation =
            (projected30Requirement is not null) && Violates(projected30.HorizontalNm, projected30.VerticalFt, projected30Requirement);
        bool coachViolation =
            (projected60Requirement is not null) && Violates(projected60.HorizontalNm, projected60.VerticalFt, projected60Requirement);
        bool warningMargin = (currentRequirement is not null) && WithinWarningMargin(current.HorizontalNm, current.VerticalFt, currentRequirement);

        SoloTrainingEventSeverity? severity =
            currentViolation ? SoloTrainingEventSeverity.Safety
            : warningViolation || warningMargin ? SoloTrainingEventSeverity.Warning
            : coachViolation ? SoloTrainingEventSeverity.Coach
            : null;
        var trigger =
            currentViolation || warningMargin ? new SampledSeparation(0.0, current.HorizontalNm, current.VerticalFt, currentRequirement)
            : warningViolation
                ? new SampledSeparation(WarningLookaheadSeconds, projected30.HorizontalNm, projected30.VerticalFt, projected30Requirement)
            : coachViolation ? new SampledSeparation(CoachLookaheadSeconds, projected60.HorizontalNm, projected60.VerticalFt, projected60Requirement)
            : null;

        if (severity is null || trigger?.Requirement is null)
        {
            return null;
        }

        var requirement = trigger.Requirement;
        string id = MakePairEventId(requirement.Name, a.Callsign, b.Callsign);
        string title = severity == SoloTrainingEventSeverity.Safety ? $"{requirement.Name} loss" : $"{requirement.Name} risk";
        string spacingLabel = trigger.LookaheadSeconds <= 0.0 ? "Current spacing" : $"{trigger.LookaheadSeconds:F0}-second projected spacing";
        string description =
            $"{a.Callsign} and {b.Callsign}: {requirement.CoachingText} {spacingLabel} is "
            + $"{trigger.HorizontalNm:F1} NM and {trigger.VerticalFt:F0} ft.";

        return new SeparationTrainingSample(
            new TrainingEventSample(
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
                trigger.HorizontalNm,
                requirement.RequiredVerticalFt,
                trigger.VerticalFt,
                null,
                null
            ),
            requirement
        );
    }

    private List<TrainingEventSample> SampleAdvisoryPair(
        AircraftState a,
        AircraftState b,
        SeparationRequirement requirement,
        TrainingEventSample separationSample,
        double scenarioElapsedSeconds,
        SoloTrainingServiceContext serviceContext
    )
    {
        var samples = new List<TrainingEventSample>(capacity: 2);
        if (separationSample.Severity == SoloTrainingEventSeverity.Safety)
        {
            if (IsStudentServiceRecipient(a, serviceContext) && !HasSafetyAlertProof(a.Callsign, b.Callsign))
            {
                samples.Add(CreateSafetyAlertSample(a, b, requirement, separationSample, scenarioElapsedSeconds));
            }

            if (IsStudentServiceRecipient(b, serviceContext) && !HasSafetyAlertProof(b.Callsign, a.Callsign))
            {
                samples.Add(CreateSafetyAlertSample(b, a, requirement, separationSample, scenarioElapsedSeconds));
            }
        }
        else
        {
            if (IsStudentServiceRecipient(a, serviceContext) && !HasTrafficAdvisoryProof(a.Callsign, b.Callsign))
            {
                samples.Add(CreateAdvisorySample(a, b, requirement, separationSample, scenarioElapsedSeconds));
            }

            if (IsStudentServiceRecipient(b, serviceContext) && !HasTrafficAdvisoryProof(b.Callsign, a.Callsign))
            {
                samples.Add(CreateAdvisorySample(b, a, requirement, separationSample, scenarioElapsedSeconds));
            }
        }

        return samples;
    }

    private List<TrainingEventSample> SampleNoMinimaAdvisoryPair(
        AircraftState a,
        AircraftState b,
        AirspaceDatabase airspace,
        double scenarioElapsedSeconds,
        SoloTrainingServiceContext serviceContext
    )
    {
        var current = ComputeSeparation(a, b, lookaheadSeconds: 0.0);
        if ((current.HorizontalNm > TerminalRadarHorizontalNm) || (current.VerticalFt > IfrVerticalFt))
        {
            return [];
        }

        bool classCContext =
            (FindApplicableAirspaceClasses(a, b, airspace, lookaheadSeconds: 0.0).Contains(AirspaceClass.Charlie))
            || (IsClassCOuterAreaPair(a, b, airspace, lookaheadSeconds: 0.0));
        var requirement = new SeparationRequirement(
            classCContext ? "Class C traffic advisory service" : "Traffic advisory service",
            SoloTrainingEventCategory.AdvisoryVisual,
            classCContext ? "7110.65 §7-8-2, §2-1-21" : "7110.65 §2-1-21",
            TerminalRadarHorizontalNm,
            IfrVerticalFt,
            classCContext
                ? "Provide Class C traffic advisories to participating aircraft when traffic is proximate."
                : "Provide traffic advisories to participating aircraft when traffic is proximate and workload permits."
        );
        var sample = new TrainingEventSample(
            MakePairEventId(requirement.Name, a.Callsign, b.Callsign),
            SoloTrainingEventCategory.AdvisoryVisual,
            SoloTrainingEventSeverity.Warning,
            "Traffic advisory service needed",
            $"{a.Callsign} and {b.Callsign}: {requirement.CoachingText} Current spacing is {current.HorizontalNm:F1} NM "
                + $"and {current.VerticalFt:F0} ft.",
            requirement.RuleReference,
            scenarioElapsedSeconds,
            [a.Callsign, b.Callsign],
            null,
            null,
            current.HorizontalNm,
            null,
            current.VerticalFt,
            null,
            null
        );

        return SampleAdvisoryPair(a, b, requirement, sample, scenarioElapsedSeconds, serviceContext);
    }

    private TrainingEventSample? SampleVisualApproach(
        AircraftState aircraft,
        double scenarioElapsedSeconds,
        SoloTrainingServiceContext serviceContext
    )
    {
        if ((!IsStudentServiceRecipient(aircraft, serviceContext)) || (!IsVisualApproachWithoutFollow(aircraft)) || (HasFieldInSightProof(aircraft)))
        {
            return null;
        }

        string id = $"VISUAL_APPROACH_FIELD_PROOF_{NormalizeCallsign(aircraft.Callsign)}";
        return new TrainingEventSample(
            id,
            SoloTrainingEventCategory.AdvisoryVisual,
            SoloTrainingEventSeverity.Warning,
            "Visual approach field proof missing",
            $"{aircraft.Callsign}: accepted CVA without field-in-sight proof.",
            "7110.65 §7-4-3; AIM §5-4-23",
            scenarioElapsedSeconds,
            [aircraft.Callsign],
            aircraft.Phases?.AssignedRunway?.Designator,
            null,
            null,
            null,
            null,
            "Confirm the airport is in sight before issuing a visual approach clearance.",
            "No accepted structured RFIS proof and the aircraft has not reported the field in sight."
        );
    }

    private static bool IsVisualApproachWithoutFollow(AircraftState aircraft) =>
        (aircraft.Phases?.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.OrdinalIgnoreCase) == true)
        && (string.IsNullOrWhiteSpace(aircraft.Approach.FollowingCallsign));

    private bool HasFieldInSightProof(AircraftState aircraft) =>
        (aircraft.Approach.HasReportedFieldInSight) || (_fieldAdvisoryProofs.Contains(NormalizeCallsign(aircraft.Callsign)));

    private static bool IsStudentServiceRecipient(AircraftState aircraft, SoloTrainingServiceContext serviceContext)
    {
        if ((!aircraft.HasMadeInitialContact) || (aircraft.HasLeftStudentFrequency))
        {
            return false;
        }

        return PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, serviceContext.InitialContactEligibility);
    }

    private bool HasTrafficAdvisoryProof(string recipientCallsign, string targetCallsign) =>
        _advisoryProofs.Contains(MakeAdvisoryProofKey(recipientCallsign, targetCallsign));

    private bool HasSafetyAlertProof(string recipientCallsign, string targetCallsign)
    {
        foreach (var proof in _safetyAlertProofs)
        {
            if (
                proof.RecipientCallsign.Equals(recipientCallsign, StringComparison.OrdinalIgnoreCase)
                && proof.TargetCallsign.Equals(targetCallsign, StringComparison.OrdinalIgnoreCase)
            )
            {
                proof.UsedForSafetyState = true;
                return true;
            }
        }

        return false;
    }

    private List<TrainingEventSample> SampleWakeAdvisoryProofs(
        IReadOnlyList<RunwayEventSample> runwaySamples,
        IReadOnlyList<WakeDirectiveContext> wakeContexts,
        List<AircraftState> aircraft,
        double scenarioElapsedSeconds,
        SoloTrainingServiceContext serviceContext
    )
    {
        var candidates = new List<WakeAdvisoryCandidate>();
        var contextIdsWithRunwaySamples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runwaySample in runwaySamples)
        {
            var sample = runwaySample.Sample;
            if (runwaySample.WakeContext is not null)
            {
                contextIdsWithRunwaySamples.Add(runwaySample.WakeContext.SourceEventId);
            }

            var wakeContext = ApplyServiceContext(runwaySample.WakeContext, serviceContext);
            if (wakeContext is not null && serviceContext.WakeDirectives.FindMatches(wakeContext).Any(HasSuppressWakeAdvisoryEffect))
            {
                continue;
            }

            if (!IsWakeAdvisoryContext(sample) || sample.Callsigns.Count < 2)
            {
                continue;
            }

            var recipient = aircraft.FirstOrDefault(a => a.Callsign.Equals(sample.Callsigns[1], StringComparison.OrdinalIgnoreCase));
            var target = aircraft.FirstOrDefault(a => a.Callsign.Equals(sample.Callsigns[0], StringComparison.OrdinalIgnoreCase));
            if (recipient is null || target is null || !IsStudentServiceRecipient(recipient, serviceContext))
            {
                continue;
            }

            candidates.Add(
                new WakeAdvisoryCandidate(
                    sample.Id,
                    recipient,
                    target,
                    sample.RuleReference,
                    sample.RunwayId,
                    sample.ActualHorizontalNm,
                    sample.ActualVerticalFt
                )
            );
        }

        foreach (var context in wakeContexts)
        {
            if (contextIdsWithRunwaySamples.Contains(context.SourceEventId))
            {
                continue;
            }

            var resolvedContext = ApplyServiceContext(context, serviceContext);
            var matches = serviceContext.WakeDirectives.FindMatches(resolvedContext!);
            if (!matches.Any(HasRequireWakeAdvisoryEffect) || matches.Any(HasSuppressWakeAdvisoryEffect))
            {
                continue;
            }

            var recipient = aircraft.FirstOrDefault(a => a.Callsign.Equals(context.SucceedingCallsign, StringComparison.OrdinalIgnoreCase));
            var target = aircraft.FirstOrDefault(a => a.Callsign.Equals(context.PrecedingCallsign, StringComparison.OrdinalIgnoreCase));
            if (recipient is null || target is null || !IsStudentServiceRecipient(recipient, serviceContext))
            {
                continue;
            }

            string ruleReference =
                matches.FirstOrDefault(rule => !string.IsNullOrWhiteSpace(rule.RuleReference))?.RuleReference ?? context.SourceRuleReference;
            string runwayId =
                resolvedContext!.Relation == WakeDirectiveRelation.SameRunway
                    ? resolvedContext.PrecedingRunwayId
                    : $"{resolvedContext.PrecedingRunwayId}/{resolvedContext.SucceedingRunwayId}";
            candidates.Add(new WakeAdvisoryCandidate(resolvedContext.SourceEventId, recipient, target, ruleReference, runwayId, null, null));
        }

        var countByRecipient = candidates
            .GroupBy(candidate => NormalizeCallsign(candidate.Recipient.Callsign), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var results = new List<TrainingEventSample>();
        foreach (var candidate in candidates)
        {
            string recipientKey = NormalizeCallsign(candidate.Recipient.Callsign);
            if (countByRecipient[recipientKey] == 1 && HasWakeAdvisoryProof(candidate))
            {
                continue;
            }

            results.Add(CreateWakeAdvisorySample(candidate, scenarioElapsedSeconds));
        }

        return results;
    }

    private static bool HasRequireWakeAdvisoryEffect(WakeDirectiveRule rule) => rule.Effects.Contains(WakeDirectiveEffect.RequireWakeAdvisory);

    private static bool HasSuppressWakeAdvisoryEffect(WakeDirectiveRule rule) => rule.Effects.Contains(WakeDirectiveEffect.SuppressWakeAdvisory);

    private static WakeDirectiveContext? ApplyServiceContext(WakeDirectiveContext? context, SoloTrainingServiceContext serviceContext) =>
        context is null
            ? null
            : context with
            {
                ArtccId = serviceContext.InitialContactEligibility.ArtccId ?? context.ArtccId,
                AirportId = context.AirportId ?? serviceContext.InitialContactEligibility.PrimaryAirportId,
            };

    private static bool IsWakeAdvisoryContext(TrainingEventSample sample) =>
        sample.Category == SoloTrainingEventCategory.RunwayWake
        && (
            sample.Title.Contains("wake", StringComparison.OrdinalIgnoreCase)
            || sample.RuleReference.Contains("§5-5-4", StringComparison.OrdinalIgnoreCase)
        );

    private bool HasWakeAdvisoryProof(WakeAdvisoryCandidate candidate)
    {
        foreach (var proof in _wakeAdvisoryProofs)
        {
            if (
                proof.RecipientCallsign.Equals(candidate.Recipient.Callsign, StringComparison.OrdinalIgnoreCase)
                && proof.CanApplyTo(candidate.SourceEventId)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static TrainingEventSample CreateWakeAdvisorySample(WakeAdvisoryCandidate candidate, double scenarioElapsedSeconds)
    {
        string id = $"WAKE_ADVISORY_{NormalizeCallsign(candidate.Recipient.Callsign)}_{candidate.SourceEventId}";
        return new TrainingEventSample(
            id,
            SoloTrainingEventCategory.AdvisoryVisual,
            SoloTrainingEventSeverity.Warning,
            "Wake turbulence advisory missing",
            $"{candidate.Recipient.Callsign}: issue caution wake turbulence for {candidate.Target.Callsign} before the wake-sensitive operation.",
            $"7110.65 §2-1-20; {candidate.SourceRuleReference}",
            scenarioElapsedSeconds,
            [candidate.Recipient.Callsign, candidate.Target.Callsign],
            candidate.RunwayId,
            null,
            candidate.ActualHorizontalNm,
            null,
            candidate.ActualVerticalFt,
            "Issue caution wake turbulence when wake from the preceding aircraft may affect the succeeding aircraft.",
            $"No accepted CWT proof for {candidate.Recipient.Callsign}."
        );
    }

    private static TrainingEventSample CreateAdvisorySample(
        AircraftState recipient,
        AircraftState target,
        SeparationRequirement requirement,
        TrainingEventSample separationSample,
        double scenarioElapsedSeconds
    )
    {
        string id = MakeAdvisoryEventId(requirement.Name, recipient.Callsign, target.Callsign);
        string requiredText =
            requirement.Name.StartsWith("Class B", StringComparison.OrdinalIgnoreCase)
                ? "Issue a mandatory Class B traffic advisory before proximity diminishes below applicable separation minima."
            : requirement.Name.StartsWith("Class C", StringComparison.OrdinalIgnoreCase)
                ? "Issue a Class C traffic advisory before proximity diminishes below applicable IFR/VFR separation minima."
            : "Issue a traffic advisory before proximity diminishes below applicable separation minima.";
        string actualText = $"No accepted structured RTIS proof for {recipient.Callsign} about {target.Callsign}.";
        string description =
            $"{recipient.Callsign}: issue traffic advisory for {target.Callsign}; {requirement.CoachingText} Current spacing is "
            + $"{separationSample.ActualHorizontalNm:F1} NM and {separationSample.ActualVerticalFt:F0} ft.";

        return new TrainingEventSample(
            id,
            SoloTrainingEventCategory.AdvisoryVisual,
            separationSample.Severity,
            separationSample.Severity == SoloTrainingEventSeverity.Safety ? "Traffic advisory missing" : "Traffic advisory needed",
            description,
            AdvisoryRuleReference(requirement),
            scenarioElapsedSeconds,
            [recipient.Callsign, target.Callsign],
            null,
            null,
            null,
            null,
            null,
            requiredText,
            actualText
        );
    }

    private static TrainingEventSample CreateSafetyAlertSample(
        AircraftState recipient,
        AircraftState target,
        SeparationRequirement requirement,
        TrainingEventSample separationSample,
        double scenarioElapsedSeconds
    )
    {
        string id = MakeSafetyAlertEventId(requirement.Name, recipient.Callsign, target.Callsign);
        string description =
            $"{recipient.Callsign}: issue safety alert for {target.Callsign}; current spacing is "
            + $"{separationSample.ActualHorizontalNm:F1} NM and {separationSample.ActualVerticalFt:F0} ft.";

        return new TrainingEventSample(
            id,
            SoloTrainingEventCategory.AdvisoryVisual,
            SoloTrainingEventSeverity.Safety,
            "Safety alert missing",
            description,
            "7110.65 §2-1-6",
            scenarioElapsedSeconds,
            [recipient.Callsign, target.Callsign],
            null,
            null,
            null,
            null,
            null,
            "Issue a traffic safety alert when another aircraft is in unsafe proximity.",
            $"No accepted SAFAL proof for {recipient.Callsign} about {target.Callsign}."
        );
    }

    private static string AdvisoryRuleReference(SeparationRequirement requirement) =>
        requirement.Name.StartsWith("Class B", StringComparison.OrdinalIgnoreCase) ? "7110.65 §7-9-5, §2-1-21"
        : requirement.Name.StartsWith("Class C", StringComparison.OrdinalIgnoreCase) ? "7110.65 §7-8-2, §2-1-21"
        : "7110.65 §2-1-21";

    private IEnumerable<TrainingEventSample> SampleSafetyAlertOveruse(double scenarioElapsedSeconds)
    {
        foreach (var proof in _safetyAlertProofs)
        {
            if (proof.UsedForSafetyState || proof.OveruseRecorded)
            {
                continue;
            }

            proof.OveruseRecorded = true;
            string id = $"SAFETY_ALERT_OVERUSE_{proof.RecipientCallsign}_{proof.TargetCallsign}_{proof.ScenarioElapsedSeconds:F0}".ToUpperInvariant();
            yield return new TrainingEventSample(
                id,
                SoloTrainingEventCategory.AdvisoryVisual,
                SoloTrainingEventSeverity.Warning,
                "Safety alert overused",
                $"{proof.RecipientCallsign}: SAFAL was issued for {proof.TargetCallsign} without a current safety-severity unsafe-proximity state.",
                "7110.65 §2-1-6",
                scenarioElapsedSeconds,
                [proof.RecipientCallsign, proof.TargetCallsign],
                null,
                null,
                null,
                null,
                null,
                "Reserve traffic safety alerts for unsafe aircraft proximity.",
                "Accepted SAFAL did not match a current safety-severity pair state."
            );
        }
    }

    private static (double HorizontalNm, double VerticalFt) ComputeSeparation(AircraftState a, AircraftState b, double lookaheadSeconds)
    {
        var aPosition = ProjectPosition(a, lookaheadSeconds);
        var bPosition = ProjectPosition(b, lookaheadSeconds);
        double aAltitude = ProjectAltitude(a, lookaheadSeconds);
        double bAltitude = ProjectAltitude(b, lookaheadSeconds);
        return (GeoMath.DistanceNm(aPosition, bPosition), Math.Abs(aAltitude - bAltitude));
    }

    private static double ProjectAltitude(AircraftState aircraft, double lookaheadSeconds) =>
        aircraft.Altitude + (aircraft.VerticalSpeed * lookaheadSeconds / 60.0);

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

    private static string MakeAdvisoryProofKey(string recipientCallsign, string targetCallsign) =>
        $"{recipientCallsign.Trim().ToUpperInvariant()}>{targetCallsign.Trim().ToUpperInvariant()}";

    private static string NormalizeCallsign(string callsign) => callsign.Trim().ToUpperInvariant();

    private static string MakeAdvisoryEventId(string ruleName, string recipientCallsign, string targetCallsign)
    {
        string normalizedRule = ruleName.Replace(' ', '_').Replace('/', '_');
        return $"TRAFFIC_ADVISORY_{normalizedRule}_{recipientCallsign}_{targetCallsign}".ToUpperInvariant();
    }

    private static string MakeSafetyAlertEventId(string ruleName, string recipientCallsign, string targetCallsign)
    {
        string normalizedRule = ruleName.Replace(' ', '_').Replace('/', '_');
        return $"SAFETY_ALERT_{normalizedRule}_{recipientCallsign}_{targetCallsign}".ToUpperInvariant();
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

    private sealed record SampledSeparation(double LookaheadSeconds, double HorizontalNm, double VerticalFt, SeparationRequirement? Requirement);

    private sealed record SeparationTrainingSample(TrainingEventSample Event, SeparationRequirement Requirement);

    private sealed record WakeAdvisoryCandidate(
        string SourceEventId,
        AircraftState Recipient,
        AircraftState Target,
        string SourceRuleReference,
        string? RunwayId,
        double? ActualHorizontalNm,
        double? ActualVerticalFt
    );

    private sealed record RunwayEvaluationResult(IReadOnlyList<RunwayEventSample> RunwayEvents, IReadOnlyList<WakeDirectiveContext> WakeContexts);

    private sealed record RunwayEventSample(TrainingEventSample Sample, WakeDirectiveContext? WakeContext);

    private sealed class WakeAdvisoryProof(string recipientCallsign, double scenarioElapsedSeconds, string? sourceEventId)
    {
        public string RecipientCallsign { get; } = recipientCallsign.Trim().ToUpperInvariant();
        public double ScenarioElapsedSeconds { get; } = scenarioElapsedSeconds;
        public string? SourceEventId { get; private set; } = sourceEventId;

        public bool CanApplyTo(string sourceEventId)
        {
            if (SourceEventId is null)
            {
                SourceEventId = sourceEventId;
                return true;
            }

            return SourceEventId.Equals(sourceEventId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class SafetyAlertProof(string recipientCallsign, string targetCallsign, double scenarioElapsedSeconds)
    {
        public string RecipientCallsign { get; } = recipientCallsign.Trim().ToUpperInvariant();
        public string TargetCallsign { get; } = targetCallsign.Trim().ToUpperInvariant();
        public double ScenarioElapsedSeconds { get; } = scenarioElapsedSeconds;
        public bool UsedForSafetyState { get; set; }
        public bool OveruseRecorded { get; set; }
    }

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
        private readonly List<RunwayOperation> _recentOperations = [];
        private readonly Dictionary<string, ActiveSameRunwayViolation> _activeViolations = new(StringComparer.OrdinalIgnoreCase);
        private const double ProjectedConvergingRunwayLimitNm = 1.0;

        public RunwayEvaluationResult Evaluate(List<AircraftState> aircraft, double scenarioElapsedSeconds, SoloTrainingServiceContext serviceContext)
        {
            var samples = new List<RunwayEventSample>();
            var wakeContexts = new Dictionary<string, WakeDirectiveContext>(StringComparer.OrdinalIgnoreCase);
            var currentStates = BuildCurrentStates(aircraft);

            foreach (var violation in _activeViolations.Values.ToList())
            {
                if (violation.WakeContext is not null)
                {
                    wakeContexts[violation.WakeContext.SourceEventId] = violation.WakeContext;
                }

                if (TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is { } sample)
                {
                    if (!IsWakeIntervalSuppressed(violation.WakeContext, serviceContext))
                    {
                        samples.Add(new RunwayEventSample(sample, violation.WakeContext));
                    }
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

                foreach (var violation in CreateRunwaySeparationViolations(operation, currentStates, scenarioElapsedSeconds, firstObservation))
                {
                    _activeViolations[violation.Id] = violation;
                    if (TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is { } sample)
                    {
                        samples.Add(new RunwayEventSample(sample, null));
                    }
                }

                foreach (var violation in CreateWakeViolations(operation, currentStates, scenarioElapsedSeconds))
                {
                    _activeViolations[violation.Id] = violation;
                    if (violation.WakeContext is not null)
                    {
                        wakeContexts[violation.WakeContext.SourceEventId] = violation.WakeContext;
                    }

                    if (TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is { } sample)
                    {
                        if (!IsWakeIntervalSuppressed(violation.WakeContext, serviceContext))
                        {
                            samples.Add(new RunwayEventSample(sample, violation.WakeContext));
                        }
                    }
                }

                _lastOperationByRunway[operation.RunwayKey] = operation;
                _recentOperations.Add(operation);
            }

            _previousStates.Clear();
            foreach (var state in currentStates.Values)
            {
                _previousStates[state.Callsign] = state;
            }

            foreach (var context in SamplePotentialWakeContexts(currentStates, scenarioElapsedSeconds))
            {
                wakeContexts[context.SourceEventId] = context;
            }

            return new RunwayEvaluationResult(samples, wakeContexts.Values.ToList());
        }

        public List<WakeDirectiveContext> SampleActiveWakeContexts(List<AircraftState> aircraft, double scenarioElapsedSeconds)
        {
            var contexts = new Dictionary<string, WakeDirectiveContext>(StringComparer.OrdinalIgnoreCase);
            var currentStates = BuildCurrentStates(aircraft);
            foreach (var violation in _activeViolations.Values)
            {
                if (violation.WakeContext is not null && TrySampleViolation(violation, currentStates, scenarioElapsedSeconds) is not null)
                {
                    contexts[violation.WakeContext.SourceEventId] = violation.WakeContext;
                }
            }

            foreach (var context in SamplePotentialWakeContexts(currentStates, scenarioElapsedSeconds))
            {
                contexts[context.SourceEventId] = context;
            }

            return contexts.Values.ToList();
        }

        public void Reset()
        {
            _previousStates.Clear();
            _lastOperationByRunway.Clear();
            _recentOperations.Clear();
            _activeViolations.Clear();
        }

        private static bool IsWakeIntervalSuppressed(WakeDirectiveContext? context, SoloTrainingServiceContext serviceContext) =>
            ApplyServiceContext(context, serviceContext) is { } resolved
            && serviceContext.WakeDirectives.FindMatches(resolved).Any(rule => rule.Effects.Contains(WakeDirectiveEffect.SuppressWakeInterval));

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
                ResolveSrsCategory(aircraft),
                ResolveCwtCategory(aircraft)
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

        private List<ActiveSameRunwayViolation> CreateRunwaySeparationViolations(
            RunwayOperation operation,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds,
            bool firstObservation
        )
        {
            var violations = new List<ActiveSameRunwayViolation>();
            if (firstObservation)
            {
                return violations;
            }

            if (_lastOperationByRunway.TryGetValue(operation.RunwayKey, out var sameRunwayPreceding))
            {
                var relation = RunwayRelation.SameActive();
                if (TryCreateViolation(sameRunwayPreceding, operation, states, scenarioElapsedSeconds, relation) is { } violation)
                {
                    violations.Add(violation);
                }
            }

            foreach (var preceding in _recentOperations.AsEnumerable().Reverse())
            {
                if (string.Equals(preceding.Callsign, operation.Callsign, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(preceding.RunwayKey, operation.RunwayKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryResolveRunwayRelation(preceding.Runway, operation.Runway) is not { } relation)
                {
                    continue;
                }

                if (relation.Kind == RunwayRelationKind.SameActive)
                {
                    continue;
                }

                if (TryCreateViolation(preceding, operation, states, scenarioElapsedSeconds, relation) is { } violation)
                {
                    violations.Add(violation);
                }
            }

            return violations;
        }

        private static ActiveSameRunwayViolation? TryCreateViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds,
            RunwayRelation relation
        )
        {
            if (!states.TryGetValue(preceding.Callsign, out var precedingState) || !states.TryGetValue(succeeding.Callsign, out var succeedingState))
            {
                return null;
            }

            if (relation.Kind == RunwayRelationKind.OppositeDirectionSamePavement)
            {
                return TryCreateOppositeDirectionViolation(preceding, succeeding, precedingState, scenarioElapsedSeconds, relation);
            }

            if (relation.Kind == RunwayRelationKind.Intersecting)
            {
                return TryCreateIntersectingRunwayViolation(preceding, succeeding, precedingState, scenarioElapsedSeconds, relation);
            }

            if (relation.Kind == RunwayRelationKind.ProjectedConverging)
            {
                return TryCreateConvergingRunwayViolation(preceding, succeeding, precedingState, scenarioElapsedSeconds, relation);
            }

            return (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) => TryCreateDepartureBehindDeparture(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Departure, OperationKind.Landing) => TryCreateDepartureBehindLanding(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Departure) => TryCreateArrivalBehindDeparture(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Landing) => TryCreateArrivalBehindLanding(
                    preceding,
                    succeeding,
                    precedingState,
                    succeedingState,
                    scenarioElapsedSeconds,
                    relation
                ),
                _ => null,
            };
        }

        private List<ActiveSameRunwayViolation> CreateWakeViolations(
            RunwayOperation operation,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            return operation.Kind switch
            {
                OperationKind.Departure => CreateDepartureWakeViolations(operation, states, scenarioElapsedSeconds),
                OperationKind.Landing =>
                [
                    .. CreateApproachWakeViolations(operation, states, scenarioElapsedSeconds),
                    .. CreateArrivalCrossingWakeViolations(operation, states, scenarioElapsedSeconds),
                ],
                _ => [],
            };
        }

        private List<ActiveSameRunwayViolation> CreateDepartureWakeViolations(
            RunwayOperation succeeding,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            var violations = new List<ActiveSameRunwayViolation>();
            if (!states.TryGetValue(succeeding.Callsign, out var succeedingState))
            {
                return violations;
            }

            foreach (var preceding in _recentOperations.Where(o => o.Kind is OperationKind.Departure or OperationKind.Landing))
            {
                if (string.Equals(preceding.Callsign, succeeding.Callsign, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!states.TryGetValue(preceding.Callsign, out var precedingState))
                {
                    continue;
                }

                var relation = TryResolveRunwayRelation(preceding.Runway, succeeding.Runway) ?? RunwayRelation.SameActive();
                if (TryResolveDepartureWakeRequirement(preceding, succeeding, precedingState, succeedingState, relation) is not { } requirement)
                {
                    continue;
                }

                double elapsedSeconds = Math.Max(0.0, scenarioElapsedSeconds - preceding.TriggeredAtSeconds);
                double? cwtDistanceNm =
                    relation.Kind == RunwayRelationKind.SameActive
                        ? RequiredDirectlyBehindWakeNm(preceding.CwtCategory, succeeding.CwtCategory)
                        : null;
                bool timeSatisfied = elapsedSeconds >= requirement.RequiredSeconds;
                bool distanceSatisfied = cwtDistanceNm.HasValue && (DistanceBetween(precedingState, succeedingState) >= cwtDistanceNm.Value);
                if (timeSatisfied || distanceSatisfied)
                {
                    continue;
                }

                string requiredText = FormatDepartureWakeRequired(requirement, cwtDistanceNm);
                violations.Add(
                    BuildWakeViolation(
                        preceding,
                        succeeding,
                        SameRunwayRule.DepartureWakeInterval,
                        "Departure wake interval",
                        requirement.RuleReference,
                        requiredText,
                        requirement.RequiredSeconds,
                        cwtDistanceNm,
                        scenarioElapsedSeconds,
                        relation
                    )
                );
            }

            return violations;
        }

        private List<ActiveSameRunwayViolation> CreateApproachWakeViolations(
            RunwayOperation preceding,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            var violations = new List<ActiveSameRunwayViolation>();
            if (!states.TryGetValue(preceding.Callsign, out var precedingState))
            {
                return violations;
            }

            foreach (var succeedingState in states.Values)
            {
                if (string.Equals(preceding.Callsign, succeedingState.Callsign, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!succeedingState.IsArrivalApproach || !RunwaysShareApproachWake(preceding.Runway, succeedingState.Runway))
                {
                    continue;
                }

                if (succeedingState.AlongThresholdFt >= precedingState.AlongThresholdFt)
                {
                    continue;
                }

                double? requiredNm = RequiredApproachWakeNm(preceding.CwtCategory, succeedingState.CwtCategory);
                if (!requiredNm.HasValue)
                {
                    continue;
                }

                double actualNm = DistanceBetween(precedingState, succeedingState);
                if (actualNm >= requiredNm.Value)
                {
                    continue;
                }

                var succeeding = RunwayOperation.FromState(OperationKind.Landing, succeedingState, scenarioElapsedSeconds);
                string relationText = IsSameRunway(preceding.Runway, succeedingState.Runway) ? "same runway" : "parallel runways less than 2,500 ft";
                violations.Add(
                    BuildWakeViolation(
                        preceding,
                        succeeding,
                        SameRunwayRule.ApproachWakeSpacing,
                        "Approach wake spacing",
                        "7110.65 §5-5-4(h)",
                        $"{relationText}: {requiredNm.Value:N0} NM CWT spacing by Table 5-5-2.",
                        null,
                        requiredNm,
                        scenarioElapsedSeconds,
                        RunwayRelation.SameActive()
                    )
                );
            }

            return violations;
        }

        private List<ActiveSameRunwayViolation> CreateArrivalCrossingWakeViolations(
            RunwayOperation succeeding,
            Dictionary<string, AircraftRunwayState> states,
            double scenarioElapsedSeconds
        )
        {
            var violations = new List<ActiveSameRunwayViolation>();
            if (!states.ContainsKey(succeeding.Callsign))
            {
                return violations;
            }

            foreach (var preceding in _recentOperations.Where(o => o.Kind == OperationKind.Departure))
            {
                if (!states.ContainsKey(preceding.Callsign))
                {
                    continue;
                }

                if (TryResolveRunwayRelation(preceding.Runway, succeeding.Runway) is not { } relation || !IsProjectedOrPhysicalIntersection(relation))
                {
                    continue;
                }

                if (
                    TryResolveProjectedFlightPathWakeRequirement(preceding, succeeding, relation, arrivalBehindDeparture: true) is not { } requirement
                )
                {
                    continue;
                }

                double elapsedSeconds = Math.Max(0.0, scenarioElapsedSeconds - preceding.TriggeredAtSeconds);
                if (elapsedSeconds >= requirement.RequiredSeconds)
                {
                    continue;
                }

                violations.Add(
                    BuildWakeViolation(
                        preceding,
                        succeeding,
                        SameRunwayRule.DepartureWakeInterval,
                        "Arrival wake interval",
                        requirement.RuleReference,
                        FormatDepartureWakeRequired(requirement, cwtDistanceNm: null),
                        requirement.RequiredSeconds,
                        requiredDistanceNm: null,
                        scenarioElapsedSeconds,
                        relation
                    )
                );
            }

            return violations;
        }

        private List<WakeDirectiveContext> SamplePotentialWakeContexts(Dictionary<string, AircraftRunwayState> states, double scenarioElapsedSeconds)
        {
            var contexts = new Dictionary<string, WakeDirectiveContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var succeeding in _recentOperations.Where(o => o.Kind == OperationKind.Departure))
            {
                if (!states.TryGetValue(succeeding.Callsign, out var succeedingState))
                {
                    continue;
                }

                foreach (var preceding in _recentOperations.Where(o => o.Kind is OperationKind.Departure or OperationKind.Landing))
                {
                    if (
                        string.Equals(preceding.Callsign, succeeding.Callsign, StringComparison.OrdinalIgnoreCase)
                        || (preceding.TriggeredAtSeconds > succeeding.TriggeredAtSeconds)
                        || !states.TryGetValue(preceding.Callsign, out var precedingState)
                    )
                    {
                        continue;
                    }

                    var relation = TryResolveRunwayRelation(preceding.Runway, succeeding.Runway) ?? RunwayRelation.SameActive();
                    if (TryResolveDepartureWakeRequirement(preceding, succeeding, precedingState, succeedingState, relation) is not { } requirement)
                    {
                        continue;
                    }

                    string id = MakeRunwayEventId(preceding, succeeding, requirement.RuleReference, relation, succeeding.TriggeredAtSeconds);
                    contexts[id] = BuildWakeDirectiveContext(id, preceding, succeeding, requirement.RuleReference, relation);
                }
            }

            foreach (var preceding in _recentOperations.Where(o => o.Kind == OperationKind.Landing))
            {
                if (!states.TryGetValue(preceding.Callsign, out var precedingState))
                {
                    continue;
                }

                foreach (var succeedingState in states.Values)
                {
                    if (
                        string.Equals(preceding.Callsign, succeedingState.Callsign, StringComparison.OrdinalIgnoreCase)
                        || !succeedingState.IsArrivalApproach
                        || !RunwaysShareApproachWake(preceding.Runway, succeedingState.Runway)
                    )
                    {
                        continue;
                    }

                    if (RequiredApproachWakeNm(preceding.CwtCategory, succeedingState.CwtCategory) is null)
                    {
                        continue;
                    }

                    var succeeding = RunwayOperation.FromState(OperationKind.Landing, succeedingState, scenarioElapsedSeconds);
                    string ruleReference = "7110.65 §5-5-4(h)";
                    string id = MakeRunwayEventId(preceding, succeeding, ruleReference, RunwayRelation.SameActive(), scenarioElapsedSeconds);
                    contexts[id] = BuildWakeDirectiveContext(id, preceding, succeeding, ruleReference, RunwayRelation.SameActive());
                }
            }

            foreach (var succeeding in _recentOperations.Where(o => o.Kind == OperationKind.Landing))
            {
                if (!states.ContainsKey(succeeding.Callsign))
                {
                    continue;
                }

                foreach (var preceding in _recentOperations.Where(o => o.Kind == OperationKind.Departure))
                {
                    if (
                        string.Equals(preceding.Callsign, succeeding.Callsign, StringComparison.OrdinalIgnoreCase)
                        || (preceding.TriggeredAtSeconds > succeeding.TriggeredAtSeconds)
                    )
                    {
                        continue;
                    }

                    if (
                        TryResolveRunwayRelation(preceding.Runway, succeeding.Runway) is not { } relation
                        || !IsProjectedOrPhysicalIntersection(relation)
                        || TryResolveProjectedFlightPathWakeRequirement(preceding, succeeding, relation, arrivalBehindDeparture: true)
                            is not { } requirement
                    )
                    {
                        continue;
                    }

                    string id = MakeRunwayEventId(preceding, succeeding, requirement.RuleReference, relation, succeeding.TriggeredAtSeconds);
                    contexts[id] = BuildWakeDirectiveContext(id, preceding, succeeding, requirement.RuleReference, relation);
                }
            }

            return contexts.Values.ToList();
        }

        private static ActiveSameRunwayViolation? TryCreateDepartureBehindDeparture(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
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
                scenarioElapsedSeconds,
                relation
            );
        }

        private static ActiveSameRunwayViolation? TryCreateDepartureBehindLanding(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
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
                scenarioElapsedSeconds,
                relation
            );
        }

        private static ActiveSameRunwayViolation? TryCreateArrivalBehindDeparture(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
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
                scenarioElapsedSeconds,
                relation
            );
        }

        private static ActiveSameRunwayViolation? TryCreateArrivalBehindLanding(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
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
                scenarioElapsedSeconds,
                relation
            );
        }

        private static ActiveSameRunwayViolation? TryCreateOppositeDirectionViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
        )
        {
            return (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) when HasCrossedRunwayEnd(precedingState) => null,
                (OperationKind.Departure, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind departure opposite-direction separation",
                    "7110.65 §3-9-6(a)",
                    "Preceding opposite-direction departure crossed DER/runway end.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Departure, OperationKind.Landing) when IsLandingClearOfRunway(precedingState) => null,
                (OperationKind.Departure, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind landing opposite-direction separation",
                    "7110.65 §3-9-6(b)",
                    "Preceding opposite-direction landing aircraft clear of the runway.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Departure) when HasCrossedRunwayEnd(precedingState) => null,
                (OperationKind.Landing, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind departure opposite-direction separation",
                    "7110.65 §3-10-3(a)(2)",
                    "Preceding opposite-direction departure crossed DER/runway end.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Landing) when IsLandingClearOfRunway(precedingState) => null,
                (OperationKind.Landing, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind landing opposite-direction separation",
                    "7110.65 §3-10-3(a)(1)",
                    "Preceding opposite-direction landing aircraft clear of the runway.",
                    scenarioElapsedSeconds,
                    relation
                ),
                _ => null,
            };
        }

        private static ActiveSameRunwayViolation? TryCreateIntersectingRunwayViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
        )
        {
            double intersectionFt = relation.PrecedingIntersectionFt ?? double.MaxValue;
            bool passedIntersection = precedingState.AlongThresholdFt >= intersectionFt;
            return (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) when passedIntersection => null,
                (OperationKind.Departure, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind departure intersecting-runway separation",
                    "7110.65 §3-9-8(a)(1)",
                    "Preceding departure passed the intersection.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Departure, OperationKind.Landing) when (passedIntersection) || (IsLandingClearOfRunway(precedingState)) => null,
                (OperationKind.Departure, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind landing intersecting-runway separation",
                    "7110.65 §3-9-8(a)(2)",
                    "Preceding landing clear of runway or passed the intersection.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Departure) when passedIntersection => null,
                (OperationKind.Landing, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind departure intersecting-runway separation",
                    "7110.65 §3-10-4(a)(1)",
                    "Preceding departure passed the intersection/flight path.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Landing) when (passedIntersection) || (IsLandingClearOfRunway(precedingState)) => null,
                (OperationKind.Landing, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind landing intersecting-runway separation",
                    "7110.65 §3-10-4(a)(2)",
                    "Preceding landing clear of runway or passed the intersection/flight path.",
                    scenarioElapsedSeconds,
                    relation
                ),
                _ => null,
            };
        }

        private static ActiveSameRunwayViolation? TryCreateConvergingRunwayViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            double scenarioElapsedSeconds,
            RunwayRelation relation
        )
        {
            double intersectionFt = relation.PrecedingIntersectionFt ?? double.MaxValue;
            bool passedIntersection = precedingState.AlongThresholdFt >= intersectionFt;
            return (succeeding.Kind, preceding.Kind) switch
            {
                (OperationKind.Departure, OperationKind.Departure) when passedIntersection => null,
                (OperationKind.Departure, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind departure converging-runway separation",
                    "7110.65 §3-9-9(a)(1)",
                    "Preceding departure crossed the projected intersection.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Departure, OperationKind.Landing) when (passedIntersection || IsLandingClearOfRunway(precedingState)) => null,
                (OperationKind.Departure, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Departure behind landing converging-runway separation",
                    "7110.65 §3-9-9(a)(2)",
                    "Preceding landing clear of runway or passed the projected intersection.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Departure) when passedIntersection => null,
                (OperationKind.Landing, OperationKind.Departure) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind departure converging-runway separation",
                    "7110.65 §3-10-4(a)(1)",
                    "Preceding departure passed the projected intersection/flight path.",
                    scenarioElapsedSeconds,
                    relation
                ),
                (OperationKind.Landing, OperationKind.Landing) when (passedIntersection || IsLandingClearOfRunway(precedingState)) => null,
                (OperationKind.Landing, OperationKind.Landing) => BuildViolation(
                    preceding,
                    succeeding,
                    "Arrival behind landing converging-runway separation",
                    "7110.65 §3-10-4(a)(2)",
                    "Preceding landing clear of runway or passed the projected intersection/flight path.",
                    scenarioElapsedSeconds,
                    relation
                ),
                _ => null,
            };
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
                    if (IsProjectedOrPhysicalIntersection(violation.Relation))
                    {
                        satisfied = HasPassedIntersection(precedingState, violation);
                        actualText = FormatIntersectionActual(precedingState, violation);
                    }
                    else if (violation.Relation.Kind == RunwayRelationKind.OppositeDirectionSamePavement)
                    {
                        satisfied = HasCrossedRunwayEnd(precedingState);
                        actualText = FormatThresholdDistanceActual(precedingState);
                    }
                    else
                    {
                        satisfied = DepartureBehindDepartureSatisfied(precedingState, succeedingState, violation.RequiredDistanceFt!.Value);
                        actualText = FormatDepartureSpacingActual(precedingState, succeedingState);
                    }
                    break;

                case SameRunwayRule.DepartureBehindLanding:
                    if (IsProjectedOrPhysicalIntersection(violation.Relation))
                    {
                        satisfied = (HasPassedIntersection(precedingState, violation)) || (IsLandingClearOfRunway(precedingState));
                        actualText = FormatIntersectionOrClearActual(precedingState, violation);
                    }
                    else
                    {
                        satisfied = IsLandingClearOfRunway(precedingState);
                        actualText = FormatRunwayClearActual(precedingState);
                    }
                    break;

                case SameRunwayRule.ArrivalBehindDeparture:
                    if (IsProjectedOrPhysicalIntersection(violation.Relation))
                    {
                        satisfied = HasPassedIntersection(precedingState, violation);
                        actualText = FormatIntersectionActual(precedingState, violation);
                    }
                    else if (violation.Relation.Kind == RunwayRelationKind.OppositeDirectionSamePavement)
                    {
                        satisfied = HasCrossedRunwayEnd(precedingState);
                        actualText = FormatThresholdDistanceActual(precedingState);
                    }
                    else
                    {
                        satisfied = ArrivalBehindDepartureSatisfied(precedingState, violation.RequiredDistanceFt!.Value);
                        actualText = FormatThresholdDistanceActual(precedingState);
                    }
                    break;

                case SameRunwayRule.ArrivalBehindLanding:
                    if (IsProjectedOrPhysicalIntersection(violation.Relation))
                    {
                        satisfied = (HasPassedIntersection(precedingState, violation)) || (IsLandingClearOfRunway(precedingState));
                        actualText = FormatIntersectionOrClearActual(precedingState, violation);
                    }
                    else if (violation.Relation.Kind == RunwayRelationKind.OppositeDirectionSamePavement)
                    {
                        satisfied = IsLandingClearOfRunway(precedingState);
                        actualText = FormatRunwayClearActual(precedingState);
                    }
                    else
                    {
                        satisfied = ArrivalBehindLandingSatisfied(precedingState, violation.RequiredDistanceFt);
                        actualText = FormatRunwayClearOrThresholdActual(precedingState);
                    }
                    break;

                case SameRunwayRule.DepartureWakeInterval:
                    satisfied = DepartureWakeIntervalSatisfied(violation, precedingState, succeedingState, scenarioElapsedSeconds);
                    actualText = FormatDepartureWakeActual(violation, precedingState, succeedingState, scenarioElapsedSeconds);
                    break;

                case SameRunwayRule.ApproachWakeSpacing:
                    satisfied = ApproachWakeSpacingSatisfied(violation, precedingState, succeedingState);
                    actualText = FormatApproachWakeActual(precedingState, succeedingState);
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

            string relationText = violation.Relation.Kind switch
            {
                RunwayRelationKind.OppositeDirectionSamePavement => "opposite-direction runway",
                RunwayRelationKind.Intersecting => "runway-intersection",
                RunwayRelationKind.ProjectedConverging => "converging-runway",
                _ => "same-runway",
            };
            string runwayText =
                violation.Relation.Kind == RunwayRelationKind.SameActive
                    ? violation.RunwayId
                    : $"{violation.Succeeding.RunwayId} against {violation.Preceding.RunwayId}";
            string description =
                $"{violation.Succeeding.Callsign} used runway {runwayText} behind "
                + $"{violation.Preceding.Callsign} before {relationText} separation existed.";

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
            double scenarioElapsedSeconds,
            RunwayRelation relation
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
            double? requiredDistanceFt =
                relation.Kind == RunwayRelationKind.SameActive
                    ? rule switch
                    {
                        SameRunwayRule.DepartureBehindDeparture => RequiredDepartureBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory),
                        SameRunwayRule.ArrivalBehindDeparture => RequiredArrivalBehindDepartureFt(preceding.SrsCategory, succeeding.SrsCategory),
                        SameRunwayRule.ArrivalBehindLanding => RequiredLandingBehindLandingExceptionFt(preceding.SrsCategory, succeeding.SrsCategory),
                        _ => null,
                    }
                    : null;
            string id = MakeRunwayEventId(preceding, succeeding, ruleReference, relation, scenarioElapsedSeconds);
            return new ActiveSameRunwayViolation(
                id,
                rule,
                preceding,
                succeeding,
                title,
                ruleReference,
                requiredText,
                requiredDistanceFt,
                null,
                null,
                relation,
                null
            );
        }

        private static ActiveSameRunwayViolation BuildWakeViolation(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            SameRunwayRule rule,
            string title,
            string ruleReference,
            string requiredText,
            double? requiredTimeSeconds,
            double? requiredDistanceNm,
            double scenarioElapsedSeconds,
            RunwayRelation relation
        )
        {
            string id = MakeRunwayEventId(preceding, succeeding, ruleReference, relation, scenarioElapsedSeconds);
            var context = BuildWakeDirectiveContext(id, preceding, succeeding, ruleReference, relation);
            return new ActiveSameRunwayViolation(
                id,
                rule,
                preceding,
                succeeding,
                title,
                ruleReference,
                requiredText,
                null,
                requiredTimeSeconds,
                requiredDistanceNm,
                relation,
                context
            );
        }

        private static WakeDirectiveContext BuildWakeDirectiveContext(
            string sourceEventId,
            RunwayOperation preceding,
            RunwayOperation succeeding,
            string ruleReference,
            RunwayRelation relation
        )
        {
            string? airportId = !string.IsNullOrWhiteSpace(preceding.Runway.AirportId)
                ? NavigationDatabase.NormalizeAirport(preceding.Runway.AirportId)
                : null;
            return new WakeDirectiveContext(
                sourceEventId,
                null,
                airportId,
                preceding.RunwayId,
                succeeding.RunwayId,
                preceding.Callsign,
                succeeding.Callsign,
                ResolveWakeDirectiveOperation(preceding.Kind, succeeding.Kind, ruleReference),
                ResolveWakeDirectiveRelation(preceding.Runway, succeeding.Runway, relation),
                CwtToChar(preceding.CwtCategory),
                CwtToChar(succeeding.CwtCategory),
                ruleReference
            );
        }

        private static WakeDirectiveOperation ResolveWakeDirectiveOperation(
            OperationKind preceding,
            OperationKind succeeding,
            string ruleReference
        ) =>
            ruleReference.Contains("§5-5-4", StringComparison.OrdinalIgnoreCase)
                ? WakeDirectiveOperation.ApproachBehindArrival
                : (succeeding, preceding) switch
                {
                    (OperationKind.Departure, OperationKind.Departure) => WakeDirectiveOperation.DepartureBehindDeparture,
                    (OperationKind.Departure, OperationKind.Landing) => WakeDirectiveOperation.DepartureBehindLanding,
                    (OperationKind.Landing, OperationKind.Departure) => WakeDirectiveOperation.ArrivalBehindDeparture,
                    (OperationKind.Landing, OperationKind.Landing) => WakeDirectiveOperation.ArrivalBehindLanding,
                    _ => WakeDirectiveOperation.Any,
                };

        private static WakeDirectiveRelation ResolveWakeDirectiveRelation(RunwayInfo preceding, RunwayInfo succeeding, RunwayRelation relation) =>
            relation.Kind switch
            {
                RunwayRelationKind.Intersecting => WakeDirectiveRelation.Intersecting,
                RunwayRelationKind.ProjectedConverging => WakeDirectiveRelation.ProjectedConverging,
                RunwayRelationKind.OppositeDirectionSamePavement => WakeDirectiveRelation.OppositeDirection,
                _ => IsSameRunway(preceding, succeeding) ? WakeDirectiveRelation.SameRunway : WakeDirectiveRelation.CloseParallel,
            };

        private static char CwtToChar(CwtCategory category) => category.ToString()[0];

        private static DepartureWakeRequirement? TryResolveDepartureWakeRequirement(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            RunwayRelation relation
        )
        {
            if (IsProjectedOrPhysicalIntersection(relation))
            {
                return TryResolveProjectedFlightPathWakeRequirement(preceding, succeeding, relation, arrivalBehindDeparture: false);
            }

            if (relation.Kind == RunwayRelationKind.OppositeDirectionSamePavement || preceding.Kind != OperationKind.Departure)
            {
                return null;
            }

            bool sameRunway = IsSameRunway(preceding.Runway, succeeding.Runway);
            double? parallelSpacingFt = sameRunway ? null : ParallelRunwaySpacingFt(preceding.Runway, succeeding.Runway);
            bool parallelUnder700 = parallelSpacingFt is < 700.0;
            bool parallelUnder2500 = parallelSpacingFt is < 2500.0;
            bool sameOrParallelUnder2500 = sameRunway || parallelUnder2500;
            bool sameOrParallelUnder700 = sameRunway || parallelUnder700;
            bool intersectionDeparture = IsIntersectionDeparture(succeedingState);

            if (intersectionDeparture)
            {
                bool categoryIBehindSuperOrHeavy =
                    (succeeding.CwtCategory == CwtCategory.I)
                    && (preceding.CwtCategory is CwtCategory.E or CwtCategory.F or CwtCategory.G or CwtCategory.H);
                if (sameOrParallelUnder700 && categoryIBehindSuperOrHeavy)
                {
                    return new DepartureWakeRequirement(180.0, "7110.65 §3-9-7(a)", WakeRelationText(sameRunway, parallelSpacingFt));
                }

                if (!sameOrParallelUnder2500)
                {
                    return null;
                }

                if ((preceding.CwtCategory == CwtCategory.A) && IsCategoryRange(succeeding.CwtCategory, CwtCategory.B, CwtCategory.I))
                {
                    return new DepartureWakeRequirement(240.0, "7110.65 §3-9-7(a)", WakeRelationText(sameRunway, parallelSpacingFt));
                }

                bool categoryBThroughIBehindBOrD =
                    (preceding.CwtCategory is CwtCategory.B or CwtCategory.D)
                    && IsCategoryRange(succeeding.CwtCategory, CwtCategory.B, CwtCategory.I);
                if (categoryBThroughIBehindBOrD)
                {
                    return new DepartureWakeRequirement(180.0, "7110.65 §3-9-7(a)", WakeRelationText(sameRunway, parallelSpacingFt));
                }

                if ((preceding.CwtCategory == CwtCategory.C) && IsCategoryRange(succeeding.CwtCategory, CwtCategory.E, CwtCategory.I))
                {
                    return new DepartureWakeRequirement(180.0, "7110.65 §3-9-7(a)", WakeRelationText(sameRunway, parallelSpacingFt));
                }

                return null;
            }

            if (sameOrParallelUnder700 && (preceding.CwtCategory == CwtCategory.E) && (succeeding.CwtCategory == CwtCategory.I))
            {
                return new DepartureWakeRequirement(120.0, "7110.65 §3-9-6(g)", WakeRelationText(sameRunway, parallelSpacingFt));
            }

            if (!sameOrParallelUnder2500)
            {
                return null;
            }

            if ((preceding.CwtCategory == CwtCategory.A) && IsCategoryRange(succeeding.CwtCategory, CwtCategory.B, CwtCategory.I))
            {
                return new DepartureWakeRequirement(180.0, "7110.65 §3-9-6(f)", WakeRelationText(sameRunway, parallelSpacingFt));
            }

            bool sameRunwayCategoryBThroughIBehindBOrD =
                (preceding.CwtCategory is CwtCategory.B or CwtCategory.D) && IsCategoryRange(succeeding.CwtCategory, CwtCategory.B, CwtCategory.I);
            if (sameRunwayCategoryBThroughIBehindBOrD)
            {
                return new DepartureWakeRequirement(120.0, "7110.65 §3-9-6(f)", WakeRelationText(sameRunway, parallelSpacingFt));
            }

            if ((preceding.CwtCategory == CwtCategory.C) && IsCategoryRange(succeeding.CwtCategory, CwtCategory.E, CwtCategory.I))
            {
                return new DepartureWakeRequirement(120.0, "7110.65 §3-9-6(f)", WakeRelationText(sameRunway, parallelSpacingFt));
            }

            return null;
        }

        private static DepartureWakeRequirement? TryResolveProjectedFlightPathWakeRequirement(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            RunwayRelation relation,
            bool arrivalBehindDeparture
        )
        {
            if (!IsProjectedOrPhysicalIntersection(relation))
            {
                return null;
            }

            if (arrivalBehindDeparture && (preceding.Kind != OperationKind.Departure || succeeding.Kind != OperationKind.Landing))
            {
                return null;
            }

            if (!arrivalBehindDeparture && (succeeding.Kind != OperationKind.Departure))
            {
                return null;
            }

            double? requiredSeconds = RequiredProjectedFlightPathWakeSeconds(preceding.CwtCategory, succeeding.CwtCategory);
            if (!requiredSeconds.HasValue)
            {
                return null;
            }

            string ruleReference =
                arrivalBehindDeparture ? "7110.65 §3-10-4(a)(3)"
                : relation.Kind == RunwayRelationKind.ProjectedConverging ? "7110.65 §3-9-9(a)(3)"
                : "7110.65 §3-9-8(a)(4)";
            string relationText =
                relation.Kind == RunwayRelationKind.ProjectedConverging ? "projected converging flight paths" : "intersecting flight paths";
            return new DepartureWakeRequirement(requiredSeconds.Value, ruleReference, relationText);
        }

        private static double? RequiredProjectedFlightPathWakeSeconds(CwtCategory preceding, CwtCategory succeeding)
        {
            if ((preceding == CwtCategory.A) && IsCategoryRange(succeeding, CwtCategory.B, CwtCategory.I))
            {
                return 180.0;
            }

            if ((preceding is CwtCategory.B or CwtCategory.D) && IsCategoryRange(succeeding, CwtCategory.B, CwtCategory.I))
            {
                return 120.0;
            }

            if ((preceding == CwtCategory.C) && IsCategoryRange(succeeding, CwtCategory.E, CwtCategory.I))
            {
                return 120.0;
            }

            if ((preceding == CwtCategory.E) && (succeeding == CwtCategory.I))
            {
                return 120.0;
            }

            return null;
        }

        private static bool DepartureWakeIntervalSatisfied(
            ActiveSameRunwayViolation violation,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            bool timeSatisfied =
                violation.RequiredTimeSeconds.HasValue
                && ((scenarioElapsedSeconds - violation.Preceding.TriggeredAtSeconds) >= violation.RequiredTimeSeconds.Value);
            bool distanceSatisfied =
                violation.RequiredDistanceNm.HasValue && (DistanceBetween(precedingState, succeedingState) >= violation.RequiredDistanceNm.Value);
            return timeSatisfied || distanceSatisfied;
        }

        private static bool ApproachWakeSpacingSatisfied(
            ActiveSameRunwayViolation violation,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState
        ) => violation.RequiredDistanceNm.HasValue && (DistanceBetween(precedingState, succeedingState) >= violation.RequiredDistanceNm.Value);

        private static string FormatDepartureWakeActual(
            ActiveSameRunwayViolation violation,
            AircraftRunwayState precedingState,
            AircraftRunwayState succeedingState,
            double scenarioElapsedSeconds
        )
        {
            double elapsedSeconds = Math.Max(0.0, scenarioElapsedSeconds - violation.Preceding.TriggeredAtSeconds);
            double actualNm = DistanceBetween(precedingState, succeedingState);
            return $"{elapsedSeconds:N0} seconds elapsed, {actualNm:N1} NM CWT spacing";
        }

        private static string FormatApproachWakeActual(AircraftRunwayState precedingState, AircraftRunwayState succeedingState) =>
            $"{DistanceBetween(precedingState, succeedingState):N1} NM spacing";

        private static bool IsIntersectionDeparture(AircraftRunwayState state) =>
            (state.AlongThresholdFt > 500.0) || (state.Phase is TouchAndGoPhase or StopAndGoPhase);

        private static bool IsCategoryRange(CwtCategory actual, CwtCategory min, CwtCategory max) => (actual >= min) && (actual <= max);

        private static double DistanceBetween(AircraftRunwayState a, AircraftRunwayState b) => GeoMath.DistanceNm(a.Position, b.Position);

        private static string FormatMinutes(double seconds) => $"{seconds / 60.0:N0} minutes";

        private static string FormatDepartureWakeRequired(DepartureWakeRequirement requirement, double? cwtDistanceNm)
        {
            string minimum = cwtDistanceNm.HasValue
                ? $"{FormatMinutes(requirement.RequiredSeconds)} or {cwtDistanceNm.Value:N1} NM CWT spacing."
                : $"{FormatMinutes(requirement.RequiredSeconds)}.";
            return requirement.RelationText is { Length: > 0 } ? $"{requirement.RelationText}: {minimum}" : minimum;
        }

        private static string WakeRelationText(bool sameRunway, double? parallelSpacingFt)
        {
            if (sameRunway)
            {
                return "same runway";
            }

            if (!parallelSpacingFt.HasValue)
            {
                return "";
            }

            return parallelSpacingFt.Value < 700.0 ? "parallel runways less than 700 ft apart" : "parallel runways less than 2,500 ft apart";
        }

        private static bool RunwaysShareApproachWake(RunwayInfo a, RunwayInfo b)
        {
            if (IsSameRunway(a, b))
            {
                return true;
            }

            return ParallelRunwaySpacingFt(a, b) is < 2500.0;
        }

        private static RunwayRelation? TryResolveRunwayRelation(RunwayInfo preceding, RunwayInfo succeeding)
        {
            if (!string.Equals(preceding.AirportId, succeeding.AirportId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (IsSameRunway(preceding, succeeding))
            {
                return RunwayRelation.SameActive();
            }

            if (IsSamePhysicalRunway(preceding, succeeding))
            {
                return new RunwayRelation(RunwayRelationKind.OppositeDirectionSamePavement, null, null);
            }

            if (RunwayIntersectionCalculator.FindIntersection(preceding, succeeding) is { } intersection)
            {
                return new RunwayRelation(
                    RunwayRelationKind.Intersecting,
                    intersection.FirstDistFromThresholdNm * GeoMath.FeetPerNm,
                    intersection.SecondDistFromThresholdNm * GeoMath.FeetPerNm
                );
            }

            if (
                RunwayIntersectionCalculator.FindProjectedFlightPathIntersection(preceding, succeeding, ProjectedConvergingRunwayLimitNm) is
                { } projectedIntersection
            )
            {
                return new RunwayRelation(
                    RunwayRelationKind.ProjectedConverging,
                    projectedIntersection.FirstDistFromThresholdNm * GeoMath.FeetPerNm,
                    projectedIntersection.SecondDistFromThresholdNm * GeoMath.FeetPerNm
                );
            }

            return null;
        }

        private static bool IsSameRunway(RunwayInfo a, RunwayInfo b) =>
            string.Equals(BuildRunwayKey(a), BuildRunwayKey(b), StringComparison.OrdinalIgnoreCase);

        private static bool IsSamePhysicalRunway(RunwayInfo a, RunwayInfo b) =>
            string.Equals(a.AirportId, b.AirportId, StringComparison.OrdinalIgnoreCase) && (a.Id.Overlaps(b.Id));

        private static double? ParallelRunwaySpacingFt(RunwayInfo a, RunwayInfo b)
        {
            if (!string.Equals(a.AirportId, b.AirportId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (a.TrueHeading.AbsAngleTo(b.TrueHeading) > 15.0)
            {
                return null;
            }

            double crossTrackNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    new LatLon(b.ThresholdLatitude, b.ThresholdLongitude),
                    new LatLon(a.ThresholdLatitude, a.ThresholdLongitude),
                    a.TrueHeading
                )
            );
            return crossTrackNm * GeoMath.FeetPerNm;
        }

        private static double? RequiredDirectlyBehindWakeNm(CwtCategory preceding, CwtCategory succeeding)
        {
            return preceding switch
            {
                CwtCategory.A => succeeding switch
                {
                    CwtCategory.B => 5.0,
                    CwtCategory.C or CwtCategory.D => 6.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G => 7.0,
                    CwtCategory.H or CwtCategory.I => 8.0,
                    _ => null,
                },
                CwtCategory.B => succeeding switch
                {
                    CwtCategory.B => 3.0,
                    CwtCategory.C or CwtCategory.D => 4.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G or CwtCategory.H or CwtCategory.I => 5.0,
                    _ => null,
                },
                CwtCategory.C => succeeding switch
                {
                    CwtCategory.E or CwtCategory.F or CwtCategory.G => 3.5,
                    CwtCategory.H or CwtCategory.I => 5.0,
                    _ => null,
                },
                CwtCategory.D => succeeding switch
                {
                    CwtCategory.B => 3.0,
                    CwtCategory.C or CwtCategory.D => 4.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G or CwtCategory.H or CwtCategory.I => 5.0,
                    _ => null,
                },
                CwtCategory.E => succeeding == CwtCategory.I ? 4.0 : null,
                _ => null,
            };
        }

        private static double? RequiredApproachWakeNm(CwtCategory preceding, CwtCategory succeeding)
        {
            return preceding switch
            {
                CwtCategory.A => succeeding switch
                {
                    CwtCategory.B => 5.0,
                    CwtCategory.C or CwtCategory.D => 6.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G => 7.0,
                    CwtCategory.H or CwtCategory.I => 8.0,
                    _ => null,
                },
                CwtCategory.B => succeeding switch
                {
                    CwtCategory.B => 3.0,
                    CwtCategory.C or CwtCategory.D => 4.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G or CwtCategory.H => 5.0,
                    CwtCategory.I => 6.0,
                    _ => null,
                },
                CwtCategory.C => succeeding switch
                {
                    CwtCategory.E or CwtCategory.F or CwtCategory.G => 3.5,
                    CwtCategory.H => 5.0,
                    CwtCategory.I => 6.0,
                    _ => null,
                },
                CwtCategory.D => succeeding switch
                {
                    CwtCategory.B => 3.0,
                    CwtCategory.C or CwtCategory.D => 4.0,
                    CwtCategory.E or CwtCategory.F or CwtCategory.G => 5.0,
                    CwtCategory.H or CwtCategory.I => 6.0,
                    _ => null,
                },
                CwtCategory.E or CwtCategory.F => succeeding == CwtCategory.I ? 4.0 : null,
                _ => null,
            };
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

        private static bool HasPassedIntersection(AircraftRunwayState state, ActiveSameRunwayViolation violation) =>
            violation.Relation.PrecedingIntersectionFt.HasValue && (state.AlongThresholdFt >= violation.Relation.PrecedingIntersectionFt.Value);

        private static bool IsProjectedOrPhysicalIntersection(RunwayRelation relation) =>
            relation.Kind is RunwayRelationKind.Intersecting or RunwayRelationKind.ProjectedConverging;

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

        private static CwtCategory ResolveCwtCategory(AircraftState aircraft)
        {
            string? cwt = WakeTurbulenceData.GetCwt(aircraft.AircraftType) ?? FaaAircraftDatabase.Get(aircraft.AircraftType)?.Cwt;
            if (Enum.TryParse(cwt, ignoreCase: true, out CwtCategory category))
            {
                return category;
            }

            return AircraftCategorization.Categorize(aircraft.AircraftType) switch
            {
                AircraftCategory.Piston or AircraftCategory.Helicopter => CwtCategory.I,
                AircraftCategory.Turboprop => CwtCategory.G,
                _ => CwtCategory.F,
            };
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

        private static string FormatIntersectionActual(AircraftRunwayState state, ActiveSameRunwayViolation violation)
        {
            if (!violation.Relation.PrecedingIntersectionFt.HasValue)
            {
                return FormatThresholdDistanceActual(state);
            }

            double remainingFt = Math.Max(0.0, violation.Relation.PrecedingIntersectionFt.Value - state.AlongThresholdFt);
            string intersectionText = violation.Relation.Kind == RunwayRelationKind.ProjectedConverging ? "projected intersection" : "intersection";
            return HasPassedIntersection(state, violation)
                ? $"preceding aircraft passed the {intersectionText}"
                : $"preceding aircraft {remainingFt:N0} ft short of the {intersectionText}";
        }

        private static string FormatIntersectionOrClearActual(AircraftRunwayState state, ActiveSameRunwayViolation violation)
        {
            if (IsLandingClearOfRunway(state))
            {
                return "preceding landing clear of runway";
            }

            return FormatIntersectionActual(state, violation);
        }

        private static string BuildRunwayKey(RunwayInfo runway) => $"{runway.AirportId}/{runway.Designator}".ToUpperInvariant();

        private static string MakeRunwayEventId(
            RunwayOperation preceding,
            RunwayOperation succeeding,
            string ruleReference,
            RunwayRelation relation,
            double scenarioElapsedSeconds
        )
        {
            string normalizedRule = ruleReference.Replace(' ', '_').Replace('/', '_').Replace('§', 'S').Replace('(', '_').Replace(')', '_');
            string runway = preceding.RunwayKey.Replace('/', '_');
            string succeedingRunway = succeeding.RunwayKey.Replace('/', '_');
            string key = string.Join(
                '_',
                "SRS",
                relation.Kind,
                runway,
                succeedingRunway,
                preceding.Callsign,
                succeeding.Callsign,
                normalizedRule,
                $"{scenarioElapsedSeconds:F0}"
            );
            return key.ToUpperInvariant();
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
            SrsCategory SrsCategory,
            CwtCategory CwtCategory
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
            CwtCategory CwtCategory,
            double TriggeredAtSeconds
        )
        {
            public string RunwayId => Runway.Designator;

            public static RunwayOperation FromState(OperationKind kind, AircraftRunwayState state, double scenarioElapsedSeconds) =>
                new(
                    kind,
                    state.Callsign,
                    state.AircraftType,
                    state.Runway,
                    state.RunwayKey,
                    state.SrsCategory,
                    state.CwtCategory,
                    scenarioElapsedSeconds
                );
        }

        private sealed record ActiveSameRunwayViolation(
            string Id,
            SameRunwayRule Rule,
            RunwayOperation Preceding,
            RunwayOperation Succeeding,
            string Title,
            string RuleReference,
            string RequiredText,
            double? RequiredDistanceFt,
            double? RequiredTimeSeconds,
            double? RequiredDistanceNm,
            RunwayRelation Relation,
            WakeDirectiveContext? WakeContext
        )
        {
            public string RunwayId =>
                Relation.Kind == RunwayRelationKind.SameActive ? Preceding.RunwayId : $"{Preceding.RunwayId}/{Succeeding.RunwayId}";
        }

        private sealed record RunwayRelation(RunwayRelationKind Kind, double? PrecedingIntersectionFt, double? SucceedingIntersectionFt)
        {
            public static RunwayRelation SameActive() => new(RunwayRelationKind.SameActive, null, null);
        }

        private sealed record DepartureWakeRequirement(double RequiredSeconds, string RuleReference, string RelationText);

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
            DepartureWakeInterval,
            ApproachWakeSpacing,
        }

        private enum RunwayRelationKind
        {
            SameActive,
            OppositeDirectionSamePavement,
            Intersecting,
            ProjectedConverging,
        }

        private enum SrsCategory
        {
            I,
            II,
            III,
        }

        private enum CwtCategory
        {
            A = 1,
            B = 2,
            C = 3,
            D = 4,
            E = 5,
            F = 6,
            G = 7,
            H = 8,
            I = 9,
        }
    }
}
