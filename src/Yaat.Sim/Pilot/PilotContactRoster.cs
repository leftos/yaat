using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Pilot;

/// <summary>Who answers when a pilot calls: the solo-training student, or an AI controller standing in for one.</summary>
public enum PilotAnsweringAgent
{
    Student,
    ControllerAi,
}

/// <summary>
/// One position a pilot may initiate contact with. <see cref="Owner"/> is null only for the solo student when the
/// scenario selected no student TCP (the pilot then calls the generic facility). <see cref="PositionId"/> is the vNAS
/// position id for an AI position (the key of the per-position initial-contact latch) and null for the student.
/// <see cref="AirportIds"/> scopes an AI position to its airports; empty means unscoped (the student, radar roles).
/// </summary>
public sealed record PilotAnsweringPosition(
    TrackOwner? Owner,
    string? PositionType,
    string? RadioName,
    string? PositionId,
    IReadOnlyList<string> AirportIds,
    PilotAnsweringAgent Agent
)
{
    /// <summary>A tower-cab position (ground or local) answers only at the airport the aircraft is physically at.</summary>
    public bool IsTowerCab => PositionType is "GND" or "TWR";

    /// <summary>
    /// Latches the aircraft's initial-contact one-shot for this addressee. Contact with the human student sets
    /// <see cref="AircraftState.HasMadeInitialContact"/>; contact with an AI position adds only its position id to
    /// <see cref="AircraftState.AiInitialContactPositionIds"/>, so a departure handled by AI Ground and AI Local still
    /// checks in with a student radar position later (the same rule scripted clearances follow), and a fresh AI
    /// facility still gets its own call (AIM 4-2-3.a.1.1).
    /// </summary>
    public void MarkInitialContact(AircraftState aircraft)
    {
        if (Agent == PilotAnsweringAgent.Student)
        {
            aircraft.HasMadeInitialContact = true;
        }
        else if (PositionId is { Length: > 0 } positionId)
        {
            aircraft.AiInitialContactPositionIds.Add(positionId);
        }
    }

    /// <summary>Whether the aircraft already made its initial call to this addressee.</summary>
    public bool HasInitialContact(AircraftState aircraft) =>
        Agent == PilotAnsweringAgent.Student
            ? aircraft.HasMadeInitialContact
            : PositionId is { Length: > 0 } positionId && aircraft.AiInitialContactPositionIds.Contains(positionId);
}

/// <summary>
/// The positions pilots may initiate contact with in this session. Empty in an instructor room with no AI
/// (nobody answers, so nobody calls — the pre-existing instructor-mode behavior); the student in solo training; the
/// AI-staffed positions whenever the controller AI is on. Built by <see cref="Build"/> from
/// <c>SimScenarioState</c> and memoized there; phases read it through <c>PhaseContext.PilotContacts</c>.
/// </summary>
public sealed class PilotContactRoster
{
    public static PilotContactRoster Empty { get; } = new([], null, null);

    private PilotContactRoster(IReadOnlyList<PilotAnsweringPosition> positions, StudentWorkedRange? studentRange, string? studentPositionType)
    {
        Positions = positions;
        Student = positions.FirstOrDefault(p => p.Agent == PilotAnsweringAgent.Student);
        StudentRange = studentRange;
        StudentPositionType = studentPositionType;
    }

    /// <summary>
    /// The scenario's student position type (<c>GND</c>, <c>TWR</c>, <c>APP</c>, <c>CTR</c>), kept whether or not solo
    /// training is on, so an RPO room with a student position still reads it; null when none is set.
    /// </summary>
    public string? StudentPositionType { get; }

    /// <summary>AI entries first in ordinal position-id order, then the student (at most one).</summary>
    public IReadOnlyList<PilotAnsweringPosition> Positions { get; }

    public PilotAnsweringPosition? Student { get; }

    /// <summary>
    /// The position types the student works; null when no range is set, and the student then answers every call type
    /// no AI position takes.
    /// </summary>
    public StudentWorkedRange? StudentRange { get; }

    /// <summary>True when anyone answers pilots — the gate that replaced the solo-only pilot-call checks.</summary>
    public bool AnyAnswering => Positions.Count > 0;

    /// <summary>The roster a hand-built context implies: the student when solo training is on, nobody otherwise.</summary>
    public static PilotContactRoster ForStudent(bool soloTrainingMode, TrackOwner? studentPosition, string? studentPositionType, string? radioName) =>
        soloTrainingMode
            ? new(
                [new PilotAnsweringPosition(studentPosition, studentPositionType, radioName, null, [], PilotAnsweringAgent.Student)],
                null,
                studentPositionType
            )
            : ForTypeOnly(studentPositionType);

    /// <summary>The roster with nobody answering that still carries the student position type; <see cref="Empty"/> when there is none.</summary>
    private static PilotContactRoster ForTypeOnly(string? studentPositionType) =>
        studentPositionType is null ? Empty : new([], null, studentPositionType);

    /// <summary>
    /// Builds the roster: every AI-staffed position (sorted by position id; an AI entry that IS the student's position is
    /// dropped while solo training is on — the human answers), then the student when solo training is on, with the
    /// vNAS radio name resolved from the ARTCC config. <paramref name="studentRange"/> limits the call types the student
    /// answers; null keeps every type.
    /// </summary>
    public static PilotContactRoster Build(
        bool soloTrainingMode,
        TrackOwner? studentPosition,
        string? studentPositionType,
        StudentWorkedRange? studentRange,
        IReadOnlyList<AiPositionConfig> aiStaffed,
        ArtccConfigRoot? artccConfig
    )
    {
        var positions = new List<PilotAnsweringPosition>();
        foreach (AiPositionConfig? ai in aiStaffed.OrderBy(p => p.PositionId, StringComparer.Ordinal))
        {
            // Compare by callsign, not TrackOwner.MatchesPosition: tower-cab positions at one airport often share a
            // TCP (OAK_GND / OAK_TWR / OAK_DEL are all 3O), and the AI ground must keep answering next to a tower student.
            if (soloTrainingMode && string.Equals(ai.Callsign, studentPosition?.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            positions.Add(
                new PilotAnsweringPosition(ai.Identity, ai.PositionType, ai.RadioName, ai.PositionId, ai.AirportIds, PilotAnsweringAgent.ControllerAi)
            );
        }

        if (soloTrainingMode)
        {
            positions.Add(
                new PilotAnsweringPosition(
                    studentPosition,
                    studentPositionType,
                    ResolveRadioName(studentPosition, artccConfig),
                    null,
                    [],
                    PilotAnsweringAgent.Student
                )
            );
        }

        return positions.Count == 0 ? ForTypeOnly(studentPositionType) : new PilotContactRoster(positions, studentRange, studentPositionType);
    }

    /// <summary>
    /// Whether the student works clearance delivery at <paramref name="atAirportId"/>: with no range, a student on that
    /// airport's <c>…_DEL</c> position; with a range, when it takes in delivery there and no AI-staffed delivery
    /// (<c>…_DEL</c>) covers the airport.
    /// </summary>
    public bool StudentWorksDeliveryAt(string? atAirportId)
    {
        if (StudentRange is null)
        {
            return Student?.Owner?.Callsign is { } callsign && IsDeliveryAt(callsign, atAirportId);
        }

        return StudentRange.Works(AtcPositionLevel.Delivery, atAirportId) && !AiTowerCabCovers(IsDeliveryPosition, atAirportId);
    }

    /// <summary>A <c>…_DEL</c> callsign whose airport prefix is <paramref name="atAirportId"/>.</summary>
    private static bool IsDeliveryAt(string callsign, string? atAirportId)
    {
        int underscore = callsign.IndexOf('_');
        return callsign.EndsWith("_DEL", StringComparison.OrdinalIgnoreCase)
            && (underscore > 0)
            && !string.IsNullOrWhiteSpace(atAirportId)
            && NavigationDatabase.AirportIdsMatch(callsign[..underscore], atAirportId);
    }

    private static bool IsDeliveryPosition(PilotAnsweringPosition position) =>
        position.Owner?.Callsign is { } callsign && callsign.EndsWith("_DEL", StringComparison.OrdinalIgnoreCase);

    private static bool IsTowerPosition(PilotAnsweringPosition position) =>
        string.Equals(position.PositionType, "TWR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the student, not an AI, works the tower at <paramref name="atAirportId"/>: with no range, when the scenario's
    /// student position type (<see cref="StudentPositionType"/>, read in an RPO room too) is not a radar one
    /// (<see cref="RunwaySpawnCall.IsRadarStudent"/>); with a range, when it takes in the tower there.
    /// </summary>
    public bool StudentWorksTowerAt(string? atAirportId)
    {
        if (StudentRange is null)
        {
            return !RunwaySpawnCall.IsRadarStudent(StudentPositionType);
        }

        return StudentRange.Works(AtcPositionLevel.Tower, atAirportId);
    }

    /// <summary>
    /// Whether the tower at <paramref name="atAirportId"/> is simulated silently: a student with a range that does not
    /// take in the tower there, and no AI tower covering the airport.
    /// </summary>
    public bool TowerIsSimulatedAt(string? atAirportId) =>
        (Student is not null)
        && (StudentRange is not null)
        && !StudentRange.Works(AtcPositionLevel.Tower, atAirportId)
        && !AiTowerCabCovers(IsTowerPosition, atAirportId);

    /// <summary>
    /// The airport an aircraft is physically on: its ground layout's airport, else the airport it was spawned at. Null
    /// when neither is known. Tower-cab positions are matched on this — never on the filed destination, which would
    /// send an OAK departure filed to SFO to "San Francisco Ground".
    /// </summary>
    public static string? SurfaceAirportOf(AircraftState aircraft)
    {
        if (!string.IsNullOrWhiteSpace(aircraft.Ground.LayoutAirportId))
        {
            return aircraft.Ground.LayoutAirportId;
        }

        return string.IsNullOrWhiteSpace(aircraft.AirportId) ? null : aircraft.AirportId;
    }

    /// <summary>
    /// The position this aircraft should call for the role it needs (<paramref name="expectedPositionType"/> = GND /
    /// TWR / APP / CTR), in order: an AI position of that type covering the aircraft; else the student; else — for a
    /// ground call — an AI local position covering the airport, since a tower working alone works ground too (it is
    /// addressed by the generic word, like a tower-only student). <paramref name="atAirportId"/> is the airport the call
    /// is physically made at (ground layout, runway); tower-cab positions are matched on it alone, radar positions on
    /// the destination/primary candidates. When <paramref name="checkEligibility"/> is set, every candidate is subject
    /// to the SOP initial-contact rules (<see cref="PilotInitialContactEligibility.CanInitiateWith"/>) as the parking /
    /// final / pattern call-ups require — an arrival still owned by approach with no handoff inbound does not call the
    /// tower, whoever staffs it. Null when nobody should be called.
    /// </summary>
    public PilotAnsweringPosition? ResolveFor(
        AircraftState aircraft,
        string expectedPositionType,
        string? atAirportId,
        InitialContactEligibilityContext eligibility,
        bool checkEligibility
    )
    {
        if (Positions.Count == 0)
        {
            return null;
        }

        if (FirstAi(aircraft, expectedPositionType, atAirportId, eligibility, checkEligibility) is { } exact)
        {
            return exact;
        }

        if (
            Student is { } student
            && StudentAnswers(expectedPositionType, atAirportId)
            && (!checkEligibility || PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, eligibility))
        )
        {
            return student;
        }

        if (string.Equals(expectedPositionType, "GND", StringComparison.OrdinalIgnoreCase))
        {
            return FirstAi(aircraft, "TWR", atAirportId, eligibility, checkEligibility);
        }

        return null;
    }

    private PilotAnsweringPosition? FirstAi(
        AircraftState aircraft,
        string positionType,
        string? atAirportId,
        InitialContactEligibilityContext eligibility,
        bool checkEligibility
    )
    {
        foreach (PilotAnsweringPosition position in Positions)
        {
            if (
                position.Agent == PilotAnsweringAgent.ControllerAi
                && string.Equals(position.PositionType, positionType, StringComparison.OrdinalIgnoreCase)
                && Covers(position, aircraft, atAirportId, eligibility.PrimaryAirportId)
                && (!checkEligibility || PilotInitialContactEligibility.CanInitiateWith(aircraft, position.Owner, position.PositionType, eligibility))
            )
            {
                return position;
            }
        }

        return null;
    }

    private bool StudentAnswers(string expectedPositionType, string? atAirportId)
    {
        if (StudentRange is null)
        {
            return true;
        }

        return AtcPositionLevels.LevelOfType(expectedPositionType) is { } level && StudentRange.Works(level, atAirportId);
    }

    /// <summary>Whether an AI-staffed position <paramref name="matches"/> accepts covers the tower-cab airport <paramref name="atAirportId"/>.</summary>
    private bool AiTowerCabCovers(Func<PilotAnsweringPosition, bool> matches, string? atAirportId) =>
        Positions.Any(position =>
            (position.Agent == PilotAnsweringAgent.ControllerAi)
            && matches(position)
            && ((position.AirportIds.Count == 0) || TowerCabCovers(position, atAirportId))
        );

    private static bool TowerCabCovers(PilotAnsweringPosition position, string? atAirportId) =>
        !string.IsNullOrWhiteSpace(atAirportId) && position.AirportIds.Any(id => NavigationDatabase.AirportIdsMatch(id, atAirportId));

    private static bool Covers(PilotAnsweringPosition position, AircraftState aircraft, string? atAirportId, string? primaryAirportId)
    {
        if (position.AirportIds.Count == 0)
        {
            return true;
        }

        if (position.IsTowerCab)
        {
            return TowerCabCovers(position, atAirportId);
        }

        foreach (string candidate in PilotInitialContactEligibility.CandidateAirportIds(aircraft, primaryAirportId))
        {
            foreach (string airportId in position.AirportIds)
            {
                if (NavigationDatabase.AirportIdsMatch(airportId, candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ResolveRadioName(TrackOwner? position, ArtccConfigRoot? artccConfig)
    {
        if (position?.Callsign is not { Length: > 0 } callsign)
        {
            return null;
        }

        string? radioName = artccConfig?.FindPositionByCallsign(callsign, facilityHint: null)?.RadioName;
        return string.IsNullOrWhiteSpace(radioName) ? null : radioName.Trim();
    }
}
