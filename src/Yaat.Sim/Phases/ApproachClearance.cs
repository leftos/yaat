using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Phases;

/// <summary>
/// Active approach clearance stored on PhaseList. Set when the controller
/// clears the aircraft for an approach (JFAC, CAPP, JAPP, PTAC).
/// </summary>
public sealed class ApproachClearance
{
    public required string ApproachId { get; init; }
    public required string AirportCode { get; init; }
    public required string RunwayId { get; init; }
    public required double FinalApproachCourse { get; init; }

    /// <summary>True when the aircraft is on a straight-in approach (no hold-in-lieu).</summary>
    public bool StraightIn { get; init; }

    /// <summary>True when the controller forced the clearance (skip intercept validation).</summary>
    public bool Force { get; init; }

    /// <summary>Resolved CIFP procedure data, if available.</summary>
    public CifpApproachProcedure? Procedure { get; init; }

    /// <summary>Pre-built missed approach fix sequence from CIFP data. Empty if no MAP data.</summary>
    public IReadOnlyList<ApproachFix> MissedApproachFixes { get; init; } = [];

    /// <summary>Hold parameters for the final MAP fix (from HA/HF/HM leg), or null if no hold.</summary>
    public MissedApproachHold? MapHold { get; init; }
}

/// <summary>Holding pattern parameters extracted from a missed approach HA/HF/HM leg.</summary>
public sealed record MissedApproachHold(
    string FixName,
    double FixLat,
    double FixLon,
    int InboundCourse,
    double LegLength,
    bool IsMinuteBased,
    TurnDirection Direction
);
