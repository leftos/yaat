using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>How the aircraft stands in relation to a military route clearance.</summary>
public enum MilitaryRouteStatus
{
    /// <summary>Not cleared into any route.</summary>
    None,

    /// <summary>Cleared in, but not yet at the entry point.</summary>
    ClearedIn,

    /// <summary>Established on the route; published altitudes apply.</summary>
    Established,

    /// <summary>Vectored off by the controller. The clearance is kept as a record, not flown.</summary>
    VectoredOff,

    /// <summary>Exited under an exit clearance, or ran past the exit point.</summary>
    Exited,
}

/// <summary>What is driving the aircraft's altitude while it is on a military route.</summary>
public enum MilitaryRouteAltitudeSource
{
    /// <summary>The published block for the current segment (FAA JO 7110.65 §9-2-6.a "MAINTAIN IR (designator) ALTITUDES").</summary>
    RouteAltitudes,

    /// <summary>A specific altitude the controller assigned instead of the published block.</summary>
    AssignedAltitude,

    /// <summary>An "at or below" restriction, applied under the route's published floor.</summary>
    AtOrBelow,
}

/// <summary>
/// Per-aircraft military training route clearance state.
///
/// Deliberately not folded into <see cref="AircraftProcedure"/>: that satellite holds SID/STAR
/// state and <see cref="FlightPhysics.ClearProcedureState"/> tears it down wholesale on any heading
/// command. A military route clearance has to survive a vector as a *record* — FAA JO 7110.65
/// §9-2-6.h treats an amendment as an amended clearance, and the instructor should still see which
/// route the aircraft was cleared into after vectoring it off.
/// </summary>
public class AircraftMilitaryRoute
{
    /// <summary>Route designator (<c>IR149</c>), or null when the aircraft is not on a route.</summary>
    public string? Designator { get; set; }

    public MilitaryRouteType Kind { get; set; } = MilitaryRouteType.Ir;

    public MilitaryRouteStatus Status { get; set; } = MilitaryRouteStatus.None;

    /// <summary>Cleared entry point label, primary or alternate.</summary>
    public string? EntryPointId { get; set; }

    /// <summary>Cleared exit point label, primary or alternate.</summary>
    public string? ExitPointId { get; set; }

    /// <summary>Index into the route's point list of the segment being flown; -1 before entry.</summary>
    public int CurrentSegmentIndex { get; set; } = -1;

    public MilitaryRouteAltitudeSource AltitudeSource { get; set; } = MilitaryRouteAltitudeSource.RouteAltitudes;

    /// <summary>The controller-assigned altitude when <see cref="AltitudeSource"/> is not the published block.</summary>
    public int? AssignedOverrideFt { get; set; }

    /// <summary>
    /// True when the route is designated for MARSA operations. Sourced from the route, never typed:
    /// MARSA is established by letter of agreement (§9-2-6.c), not on frequency.
    /// </summary>
    public bool Marsa { get; set; }

    /// <summary>
    /// The MSL floor and ceiling last pushed into <see cref="ControlTargets"/>, so the phase can tell
    /// whether a controller altitude command has since superseded the block.
    /// </summary>
    public double? AppliedFloorFt { get; set; }

    public double? AppliedCeilingFt { get; set; }

    /// <summary>Beacon code to restore on exit, stashed when a VR or SR entry forced 4000.</summary>
    public uint? PreRouteSquawk { get; set; }

    public bool IsActive => Designator is not null && Status is MilitaryRouteStatus.ClearedIn or MilitaryRouteStatus.Established;

    /// <summary>
    /// One-line altitude summary for the strip and Aircraft List, pre-rendered here rather than sent
    /// as two numbers so the change tracker's fingerprint does not churn every tick as an AGL bound
    /// re-resolves.
    /// <para>
    /// It reports the <em>resolved MSL</em> pair the simulation actually enforced, not the published
    /// notation. That matters because an AGL floor is resolved against the nearest airport's
    /// elevation — YAAT has no terrain model — so on a route crossing high ground the enforced floor
    /// can sit well away from true height above ground. Showing the enforced number lets the
    /// instructor see what the aircraft is really being held to.
    /// </para>
    /// </summary>
    public string AltitudeText
    {
        get
        {
            if (Designator is null)
            {
                return string.Empty;
            }

            if (AltitudeSource == MilitaryRouteAltitudeSource.AssignedAltitude && AssignedOverrideFt is { } assigned)
            {
                return $"MAINTAIN {assigned:N0}";
            }

            if (AltitudeSource == MilitaryRouteAltitudeSource.AtOrBelow && AssignedOverrideFt is { } restriction)
            {
                return $"AT OR BELOW {restriction:N0}";
            }

            return (AppliedFloorFt, AppliedCeilingFt) switch
            {
                ({ } floor, { } ceiling) when Math.Abs(floor - ceiling) < 1 => $"{floor:N0}",
                ({ } floor, { } ceiling) => $"{floor:N0}–{ceiling:N0}",
                (null, { } ceiling) => $"AT OR BELOW {ceiling:N0}",
                ({ } floor, null) => $"AT OR ABOVE {floor:N0}",
                _ => $"{Designator} ALT",
            };
        }
    }

    /// <summary>
    /// True when the 14 CFR 91.117(a) 250-knot waiver applies. AP/1B chapter 1 §I grants it within the
    /// lateral and vertical confines of an IR or VR route. It deliberately does not extend to SR
    /// routes, which are defined as 250 KIAS or less (AP/1B chapter 4 §V.C).
    /// </summary>
    public bool SpeedLimitWaived =>
        Status == MilitaryRouteStatus.Established && Kind is MilitaryRouteType.Ir or MilitaryRouteType.Vr or MilitaryRouteType.Ar;

    public void Clear()
    {
        Designator = null;
        Status = MilitaryRouteStatus.None;
        EntryPointId = null;
        ExitPointId = null;
        CurrentSegmentIndex = -1;
        AltitudeSource = MilitaryRouteAltitudeSource.RouteAltitudes;
        AssignedOverrideFt = null;
        Marsa = false;
        AppliedFloorFt = null;
        AppliedCeilingFt = null;
        PreRouteSquawk = null;
    }

    public AircraftMilitaryRouteDto ToSnapshot() =>
        new()
        {
            Designator = Designator,
            Kind = (int)Kind,
            Status = (int)Status,
            EntryPointId = EntryPointId,
            ExitPointId = ExitPointId,
            CurrentSegmentIndex = CurrentSegmentIndex,
            AltitudeSource = (int)AltitudeSource,
            AssignedOverrideFt = AssignedOverrideFt,
            Marsa = Marsa,
            AppliedFloorFt = AppliedFloorFt,
            AppliedCeilingFt = AppliedCeilingFt,
            PreRouteSquawk = PreRouteSquawk,
        };

    public static AircraftMilitaryRoute FromSnapshot(AircraftMilitaryRouteDto dto) =>
        new()
        {
            Designator = dto.Designator,
            Kind = (MilitaryRouteType)dto.Kind,
            Status = (MilitaryRouteStatus)dto.Status,
            EntryPointId = dto.EntryPointId,
            ExitPointId = dto.ExitPointId,
            CurrentSegmentIndex = dto.CurrentSegmentIndex,
            AltitudeSource = (MilitaryRouteAltitudeSource)dto.AltitudeSource,
            AssignedOverrideFt = dto.AssignedOverrideFt,
            Marsa = dto.Marsa,
            AppliedFloorFt = dto.AppliedFloorFt,
            AppliedCeilingFt = dto.AppliedCeilingFt,
            PreRouteSquawk = dto.PreRouteSquawk,
        };
}
