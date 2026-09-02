using Yaat.Sim.Simulation;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// Which of the configured AI positions are actually active this tick, and which aircraft and track owners belong to
/// humans the AI must never act for. A human staffing a configured position suspends the AI on it; the host decides
/// how humans are detected (the solo student in the pure engine; the student, connected CRC positions and per-connection
/// aircraft assignments in a live room).
/// </summary>
public interface IAiStaffing
{
    /// <summary>The positions the AI plays right now, sorted by (role rank, position id).</summary>
    IReadOnlyList<AiPositionConfig> ActivePositions { get; }

    /// <summary>Re-derives <see cref="ActivePositions"/> from the current human presence. Called once per AI tick.</summary>
    void Refresh();

    /// <summary>True when a human — not the AI — holds this track owner's position.</summary>
    bool IsHumanHeld(TrackOwner owner);

    /// <summary>
    /// True when a human holds this configured position itself — compared by position, never by TCP, because tower-cab
    /// positions share one (a human tower must not read as a human ground).
    /// </summary>
    bool IsHumanHeld(AiPositionConfig position);

    /// <summary>True when the aircraft is assigned to a specific human connection; such aircraft are outside every AI jurisdiction.</summary>
    bool IsAssignedToHuman(string callsign);
}

/// <summary>
/// Staffing for the pure engine (tests, the soak runner's non-room path): every configured position is active except
/// one that IS the solo student's position, and the solo student is the only human.
/// </summary>
public sealed class HeadlessAiStaffing(IReadOnlyList<AiPositionConfig> configured, SimScenarioState scenario) : IAiStaffing
{
    private IReadOnlyList<AiPositionConfig> _active = Filter(configured, scenario);

    public IReadOnlyList<AiPositionConfig> ActivePositions => _active;

    public void Refresh()
    {
        _active = Filter(configured, scenario);
    }

    public bool IsHumanHeld(TrackOwner owner) =>
        scenario.SoloTrainingMode && scenario.StudentPosition is { } student && owner.MatchesPosition(student);

    public bool IsHumanHeld(AiPositionConfig position) => IsStudentPosition(position, scenario);

    public bool IsAssignedToHuman(string callsign) => false;

    private static List<AiPositionConfig> Filter(IReadOnlyList<AiPositionConfig> configured, SimScenarioState scenario) =>
        configured
            .Where(p => !IsStudentPosition(p, scenario))
            .OrderBy(p => ControlRoles.Rank(p.Role))
            .ThenBy(p => p.PositionId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Compare by callsign, not by TCP: tower-cab positions share a TCP (OAK_GND / OAK_TWR / OAK_DEL are all 3O).</summary>
    private static bool IsStudentPosition(AiPositionConfig position, SimScenarioState scenario) =>
        scenario.SoloTrainingMode && string.Equals(position.Callsign, scenario.StudentPosition?.Callsign, StringComparison.OrdinalIgnoreCase);
}
