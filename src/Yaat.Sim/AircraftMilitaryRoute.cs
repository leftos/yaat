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

    /// <summary>
    /// A controller-assigned altitude block instead of the published one (FAA JO 7110.65 §9-2-13
    /// "MAINTAIN BLOCK (altitude) THROUGH (altitude)"). Refueling is flown in a block, not at a
    /// level, so an assigned refueling altitude has two bounds rather than one.
    /// </summary>
    AssignedBlock,
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

    /// <summary>
    /// Which published direction of an aerial refueling track is being flown ("North", "East", …),
    /// or empty. A track's directions are separate geometries sharing one designator, so the
    /// direction is part of the clearance rather than a property of the route.
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Cleared entry point label, primary or alternate.</summary>
    public string? EntryPointId { get; set; }

    /// <summary>Cleared exit point label, primary or alternate.</summary>
    public string? ExitPointId { get; set; }

    /// <summary>Index into the route's point list of the segment being flown; -1 before entry.</summary>
    public int CurrentSegmentIndex { get; set; } = -1;

    public MilitaryRouteAltitudeSource AltitudeSource { get; set; } = MilitaryRouteAltitudeSource.RouteAltitudes;

    /// <summary>The controller-assigned altitude when <see cref="AltitudeSource"/> is not the published block.</summary>
    public int? AssignedOverrideFt { get; set; }

    /// <summary>The assigned block's bounds when <see cref="AltitudeSource"/> is <see cref="MilitaryRouteAltitudeSource.AssignedBlock"/>.</summary>
    public int? AssignedFloorFt { get; set; }

    public int? AssignedCeilingFt { get; set; }

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
                return Hundreds(assigned);
            }

            if (AltitudeSource == MilitaryRouteAltitudeSource.AtOrBelow && AssignedOverrideFt is { } restriction)
            {
                return $"B{Hundreds(restriction)}";
            }

            return (AppliedFloorFt, AppliedCeilingFt) switch
            {
                ({ } floor, { } ceiling) when Math.Abs(floor - ceiling) < 1 => Hundreds(floor),
                ({ } floor, { } ceiling) => $"{Hundreds(floor)}B{Hundreds(ceiling)}",
                (null, { } ceiling) => $"B{Hundreds(ceiling)}",
                ({ } floor, null) => $"A{Hundreds(floor)}",
                _ => $"{Designator} ALT",
            };
        }
    }

    /// <summary>
    /// An altitude in the hundreds-of-feet form controllers write on a strip: 5,000 ft is "050",
    /// FL240 is "240". FAA JO 7110.65 §4-5-2 and the §13-1-1 strip conventions pair two of them
    /// with a "B" to mean a block, which is why the strip reads "050B060" rather than "5,000-6,000"
    /// — and it keeps a non-ASCII dash out of strip text.
    /// </summary>
    private static string Hundreds(double feet) => $"{(int)Math.Round(feet / 100.0):000}";

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
        Direction = string.Empty;
        EntryPointId = null;
        ExitPointId = null;
        CurrentSegmentIndex = -1;
        AltitudeSource = MilitaryRouteAltitudeSource.RouteAltitudes;
        AssignedOverrideFt = null;
        AssignedFloorFt = null;
        AssignedCeilingFt = null;
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
            Direction = Direction,
            EntryPointId = EntryPointId,
            ExitPointId = ExitPointId,
            CurrentSegmentIndex = CurrentSegmentIndex,
            AltitudeSource = (int)AltitudeSource,
            AssignedOverrideFt = AssignedOverrideFt,
            AssignedFloorFt = AssignedFloorFt,
            AssignedCeilingFt = AssignedCeilingFt,
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
            Direction = dto.Direction ?? string.Empty,
            EntryPointId = dto.EntryPointId,
            ExitPointId = dto.ExitPointId,
            CurrentSegmentIndex = dto.CurrentSegmentIndex,
            AltitudeSource = (MilitaryRouteAltitudeSource)dto.AltitudeSource,
            AssignedOverrideFt = dto.AssignedOverrideFt,
            AssignedFloorFt = dto.AssignedFloorFt,
            AssignedCeilingFt = dto.AssignedCeilingFt,
            Marsa = dto.Marsa,
            AppliedFloorFt = dto.AppliedFloorFt,
            AppliedCeilingFt = dto.AppliedCeilingFt,
            PreRouteSquawk = dto.PreRouteSquawk,
        };
}
