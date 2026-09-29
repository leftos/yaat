using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// ERAM-side per-track display state mirrored to CRC. Includes leader/dwell overrides, the ERAM-tier interim/procedure
/// altitude pile, pending pointouts, and the ERAM sectors that have minimized the point-out data block or cycled the data
/// block to an FDB.
/// </summary>
public class AircraftEramState
{
    public bool IsDwellLocked { get; set; }

    /// <summary>
    /// Sector IDs that have marked this aircraft on-frequency — the ERAM VCI indicator, toggled per
    /// sector via the <c>//&lt;FLID&gt;</c> implied command or by clicking the FDB column-0 symbol
    /// (docs/crc/eram.md §FDB Column 0). Emitted verbatim as <c>EramTrackDto.OnFrequencySectorIds</c>;
    /// CRC lights the glyph for a viewing controller iff the list contains that controller's own
    /// ERAM sector ID.
    /// </summary>
    public List<string> OnFrequencySectorIds { get; set; } = [];

    /// <summary>Leader direction override (1=SW .. 9=NE per CRC enum; 5=Default). Null = sector default.</summary>
    public int? LeaderDirection { get; set; }

    /// <summary>Leader length override (0-3 per CRC's render switch; 5=use display default). Null = controller's display default.</summary>
    public int? LeaderLength { get; set; }

    /// <summary>Interim altitude issued via ERAM QQ, in hundreds of feet (the unit CRC renders directly).</summary>
    public int? InterimAltitude { get; set; }

    /// <summary>Local interim altitude (QQ L&lt;alt&gt;), in hundreds of feet.</summary>
    public int? LocalInterimAltitude { get; set; }

    /// <summary>Procedure altitude from QQ P&lt;alt&gt;, in hundreds of feet.</summary>
    public int? ProcedureAltitude { get; set; }

    /// <summary>Controller-entered altitude (QQ R&lt;alt&gt; / auto-track cleared), in hundreds of feet.</summary>
    public int? ControllerEnteredAltitude { get; set; }

    /// <summary>
    /// ERAM FDB line-4 (HSF) assigned heading, set via the <c>QS &lt;heading&gt;</c> command (docs/crc/eram.md
    /// §QS Command). A manual controller annotation — NOT the aircraft's actual assigned heading. Null = unset.
    /// </summary>
    public string? AssignedHeading { get; set; }

    /// <summary>ERAM FDB line-4 (HSF) assigned speed, set via <c>QS /&lt;speed&gt;</c>. A manual annotation. Null = unset.</summary>
    public string? AssignedSpeed { get; set; }

    /// <summary>ERAM FDB line-4 (HSF) free text, set via the <c>QS `&lt;text&gt;</c> backtick form. Distinct from the
    /// STARS scratchpad. Null = unset.</summary>
    public string? FreeText { get; set; }

    /// <summary>
    /// Distance Reference Indicator (DRI / separation halo) toggled via <c>QP J</c> (standard, 5 NM) or
    /// <c>QP T</c> (reduced separation, 3 NM) (docs/crc/eram.md §Distance Reference Indicators). Stored as
    /// the CRC <c>HaloType</c> ordinal: 1 = Standard, 2 = ReducedSeparation; null = no halo. A manual display
    /// annotation carried on the aircraft, so all sectors viewing the FDB see the same halo (adequate for
    /// YAAT's single-ERAM-sector training; real ERAM DRIs are per-controller).
    /// </summary>
    public int? DriHaloType { get; set; }

    /// <summary>
    /// Label of the Continuous Range Readout (CRR) group this aircraft belongs to, assigned via the
    /// <c>LF</c> command (docs/crc/eram.md §Continuous Range Readout View). Drives the FDB's
    /// <c>CrrGroup</c> field so CRC renders the aircraft's Range Data Block (nm to the group location) and
    /// lists it under the group in the CRR view. Null = not in a group. Each aircraft belongs to at most one
    /// group (CRC models it as a single FDB field), carried on the aircraft so every sector viewing the FDB
    /// sees the same membership (adequate for YAAT's single-ERAM-sector training, mirroring <see cref="DriHaloType"/>).
    /// </summary>
    public string? CrrGroupLabel { get; set; }

    /// <summary>Active ERAM pointouts.</summary>
    public List<EramPointoutState> Pointouts { get; set; } = [];

    /// <summary>
    /// ERAM sectors that have minimized this aircraft's point-out data block back to an LDB with <c>QP &lt;FLID&gt;</c>
    /// (docs/crc/eram.md §Point Outs). A point out otherwise forces the block to an FDB so its yellow P / white A
    /// indicator is visible; a sector listed here overrides that force. A fresh point out removes its initiating and
    /// receiving sectors, so the indicator reappears. Read by the broadcast path, so every touch locks the list.
    /// </summary>
    public List<EramSectorKey> PointoutMinimizedSectors { get; set; } = [];

    /// <summary>
    /// ERAM sectors that have cycled this aircraft's data block to an FDB with the bare-<c>&lt;FLID&gt;</c> implied command
    /// (docs/crc/eram.md §Changing Data Block Types); the same command again removes the sector. Read by the broadcast
    /// path, so every touch locks the list.
    /// </summary>
    public List<EramSectorKey> FdbOpenSectors { get; set; } = [];

    /// <summary>Whether the sector is in <see cref="PointoutMinimizedSectors"/>.</summary>
    public bool IsPointoutMinimizedFor(string facility, string sector) => ContainsSector(PointoutMinimizedSectors, facility, sector);

    /// <summary>Whether the sector is in <see cref="FdbOpenSectors"/>.</summary>
    public bool IsFdbOpenFor(string facility, string sector) => ContainsSector(FdbOpenSectors, facility, sector);

    private static bool ContainsSector(List<EramSectorKey> sectors, string facility, string sector)
    {
        lock (sectors)
        {
            return sectors.Any(s => s.Is(facility, sector));
        }
    }

    /// <summary>
    /// QH-frozen track (CRC ERAM QH display function, <c>docs/crc/eram.md</c> §Freezing a Track): the data
    /// block is parked at <see cref="FrozenLat"/>/<see cref="FrozenLon"/> and unpaired from the target. A
    /// frozen track shows FRZN, holds its snapshot altitude, and is exempt from coast and every auto-removal
    /// path until re-started (TRACK), which revalidates it per 7110.65 §5-2-15 ("track start from … frozen status").
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>Frozen location (from the QH command). Null unless <see cref="IsFrozen"/>.</summary>
    public double? FrozenLat { get; set; }
    public double? FrozenLon { get; set; }

    /// <summary>Frozen (snapshot) altitude in hundreds of feet, captured at freeze time.</summary>
    public int? FrozenAltitude { get; set; }

    /// <summary>
    /// QT Coast Track (docs/eram/commands/QT.yaml, action <c>CT</c>; 7110.65 §5-13-8a flat track): the track is unpaired
    /// from the target and moves on its own from <see cref="CoastLat"/>/<see cref="CoastLon"/> at <see cref="CoastSpeed"/>,
    /// along <see cref="CoastRoute"/> and then on its last leg's course, or on <see cref="CoastTrueCourse"/> when the route
    /// is empty. Its position is <see cref="CoastPositionAt"/>, a function of the stored anchor and the sim time, so a
    /// replay or a restore shows the same track. It is exempt from STCA (§5-13-7) and from every auto-removal path, and
    /// ends only on a track start (QT) or a drop (QX). Coast and freeze exclude each other.
    /// </summary>
    public bool IsCoastTrack { get; set; }

    /// <summary>The coast anchor: where the coasted track was at <see cref="CoastStartSeconds"/>. Null unless coasting.</summary>
    public double? CoastLat { get; set; }
    public double? CoastLon { get; set; }

    /// <summary>Sim-elapsed seconds at which the coast was entered, the time of the anchor.</summary>
    public double? CoastStartSeconds { get; set; }

    /// <summary>The coasted track's displayed altitude in hundreds of feet.</summary>
    public int? CoastAltitude { get; set; }

    /// <summary>The coasted track's speed in knots (true airspeed, no wind).</summary>
    public int? CoastSpeed { get; set; }

    /// <summary>The course in degrees true the track holds once <see cref="CoastRoute"/> is empty or flown out.</summary>
    public double? CoastTrueCourse { get; set; }

    /// <summary>The fixes the coasted track flies through from the anchor, in order. Empty when a heading was entered.</summary>
    public List<LatLon> CoastRoute { get; set; } = [];

    /// <summary>
    /// The coasted track's position at <paramref name="nowSeconds"/>: <see cref="CoastSpeed"/> times the time since
    /// <see cref="CoastStartSeconds"/>, flown from the anchor through <see cref="CoastRoute"/> and then on the last leg's
    /// course (or <see cref="CoastTrueCourse"/> with no route). A time before the anchor's shows the anchor. Null unless
    /// coasting.
    /// </summary>
    public LatLon? CoastPositionAt(double nowSeconds) => CoastStateAt(nowSeconds)?.Position;

    /// <summary>The coasted track's course in degrees true at <paramref name="nowSeconds"/>. Null unless coasting.</summary>
    public double? CoastCourseAt(double nowSeconds) => CoastStateAt(nowSeconds)?.Course;

    private (LatLon Position, double Course)? CoastStateAt(double nowSeconds)
    {
        if (!IsCoastTrack || (CoastLat is not { } lat) || (CoastLon is not { } lon) || (CoastStartSeconds is not { } start))
        {
            return null;
        }

        double distanceNm = (CoastSpeed ?? 0) * Math.Max(0, nowSeconds - start) / 3600.0;
        return FlyCoast(new LatLon(lat, lon), distanceNm);
    }

    // Flies distanceNm from the anchor through CoastRoute, then on the last leg's course (CoastTrueCourse with no route).
    private (LatLon Position, double Course) FlyCoast(LatLon from, double distanceNm)
    {
        double course = CoastTrueCourse ?? 0;
        foreach (LatLon fix in CoastRoute)
        {
            double legNm = GeoMath.DistanceNm(from, fix);
            if (legNm <= 0)
            {
                continue;
            }

            course = GeoMath.BearingTo(from, fix);
            if (legNm >= distanceNm)
            {
                return (GeoMath.ProjectPoint(from, new TrueHeading(course), distanceNm), course);
            }

            distanceNm -= legNm;
            from = fix;
        }

        return (GeoMath.ProjectPoint(from, new TrueHeading(course), distanceNm), course);
    }

    /// <summary>Ends a QT coast: the track pairs with its target again.</summary>
    public void EndCoast()
    {
        IsCoastTrack = false;
        CoastLat = null;
        CoastLon = null;
        CoastStartSeconds = null;
        CoastAltitude = null;
        CoastSpeed = null;
        CoastTrueCourse = null;
        CoastRoute = [];
    }

    /// <summary>
    /// The sector that owned the Track immediately before a handoff was accepted (or the Track was
    /// force-taken). While the accept window is open the previous owner's FDB shows the Field-E accepted
    /// indicator <c>Oxxx</c>/<c>Kxxx</c>/<c>OUNK</c> (docs/crc/eram.md §Data Blocks; CRC
    /// <c>FdbRenderObject</c> renders <c>RecentHandoffPeer</c> as the same-facility abbreviation context and
    /// the current <see cref="AircraftTrack.Owner"/> — the acceptor — as the sector shown). Null when no
    /// accept is being confirmed. Cleared on drop; overwritten by the next accept; the 30 s window is
    /// enforced by the broadcast against <see cref="RecentHandoffAcceptedAtSeconds"/>.
    /// </summary>
    public TrackOwner? RecentHandoffPreviousOwner { get; set; }

    /// <summary><c>Kxxx</c> (accepted with <c>/OK</c>, i.e. force-taken) when true; <c>Oxxx</c> when false.</summary>
    public bool RecentHandoffWasForced { get; set; }

    /// <summary>Sim-elapsed seconds at which the handoff was accepted; the accepted indicator expires 30 s later.</summary>
    public double? RecentHandoffAcceptedAtSeconds { get; set; }

    /// <summary>
    /// The ERAM vertical-conformance latch CRC's data block reads as <c>ReachedAssignedAltitude</c>: set once the measured
    /// altitude is inside the conformance band of <see cref="ConformanceKey"/>, and cleared only when that assignment changes.
    /// Outside the band, false shows the climb/descent arrow and true shows <c>-</c> (low) or <c>+</c> (high). True with no
    /// assigned altitude. Maintained by <c>SimulationEngine.TickEramVerticalConformance</c>.
    /// </summary>
    public bool ReachedAssignedAltitude { get; set; }

    /// <summary>The assignment <see cref="ReachedAssignedAltitude"/> was latched against. Null = never evaluated.</summary>
    public EramConformanceKey? ConformanceKey { get; set; }

    public AircraftEramStateDto ToSnapshot() =>
        new()
        {
            IsDwellLocked = IsDwellLocked,
            OnFrequencySectorIds = OnFrequencySectorIds.Count > 0 ? [.. OnFrequencySectorIds] : null,
            LeaderDirection = LeaderDirection,
            LeaderLength = LeaderLength,
            InterimAltitude = InterimAltitude,
            LocalInterimAltitude = LocalInterimAltitude,
            ProcedureAltitude = ProcedureAltitude,
            ControllerEnteredAltitude = ControllerEnteredAltitude,
            AssignedHeading = AssignedHeading,
            AssignedSpeed = AssignedSpeed,
            FreeText = FreeText,
            DriHaloType = DriHaloType,
            CrrGroupLabel = CrrGroupLabel,
            IsFrozen = IsFrozen,
            FrozenLat = FrozenLat,
            FrozenLon = FrozenLon,
            FrozenAltitude = FrozenAltitude,
            IsCoastTrack = IsCoastTrack,
            CoastLat = CoastLat,
            CoastLon = CoastLon,
            CoastStartSeconds = CoastStartSeconds,
            CoastAltitude = CoastAltitude,
            CoastSpeed = CoastSpeed,
            CoastTrueCourse = CoastTrueCourse,
            CoastRoute = CoastRoute.Count > 0 ? [.. CoastRoute] : null,
            RecentHandoffPreviousOwner = RecentHandoffPreviousOwner?.ToSnapshot(),
            RecentHandoffWasForced = RecentHandoffWasForced,
            RecentHandoffAcceptedAtSeconds = RecentHandoffAcceptedAtSeconds,
            ReachedAssignedAltitude = ReachedAssignedAltitude,
            ConformanceKey = ConformanceKey?.ToSnapshot(),
            Pointouts =
                Pointouts.Count > 0
                    ?
                    [
                        .. Pointouts.Select(p => new EramPointoutStateDto
                        {
                            OriginatingFacility = p.OriginatingFacility,
                            OriginatingSector = p.OriginatingSector,
                            ReceivingFacility = p.ReceivingFacility,
                            ReceivingSector = p.ReceivingSector,
                            IsAcknowledged = p.IsAcknowledged,
                            IsRSideCleared = p.IsRSideCleared,
                            IsDSideCleared = p.IsDSideCleared,
                        }),
                    ]
                    : null,
            PointoutMinimizedSectors = SnapshotSectors(PointoutMinimizedSectors),
            FdbOpenSectors = SnapshotSectors(FdbOpenSectors),
        };

    private static List<EramSectorKeyDto>? SnapshotSectors(List<EramSectorKey> sectors)
    {
        lock (sectors)
        {
            return sectors.Count > 0 ? [.. sectors.Select(s => s.ToSnapshot())] : null;
        }
    }

    public static AircraftEramState FromSnapshot(AircraftEramStateDto dto) =>
        new()
        {
            IsDwellLocked = dto.IsDwellLocked,
            OnFrequencySectorIds = dto.OnFrequencySectorIds is not null ? [.. dto.OnFrequencySectorIds] : [],
            LeaderDirection = dto.LeaderDirection,
            LeaderLength = dto.LeaderLength,
            InterimAltitude = dto.InterimAltitude,
            LocalInterimAltitude = dto.LocalInterimAltitude,
            ProcedureAltitude = dto.ProcedureAltitude,
            ControllerEnteredAltitude = dto.ControllerEnteredAltitude,
            AssignedHeading = dto.AssignedHeading,
            AssignedSpeed = dto.AssignedSpeed,
            FreeText = dto.FreeText,
            DriHaloType = dto.DriHaloType,
            CrrGroupLabel = dto.CrrGroupLabel,
            IsFrozen = dto.IsFrozen,
            FrozenLat = dto.FrozenLat,
            FrozenLon = dto.FrozenLon,
            FrozenAltitude = dto.FrozenAltitude,
            IsCoastTrack = dto.IsCoastTrack,
            CoastLat = dto.CoastLat,
            CoastLon = dto.CoastLon,
            CoastStartSeconds = dto.CoastStartSeconds,
            CoastAltitude = dto.CoastAltitude,
            CoastSpeed = dto.CoastSpeed,
            CoastTrueCourse = dto.CoastTrueCourse,
            CoastRoute = dto.CoastRoute is not null ? [.. dto.CoastRoute] : [],
            RecentHandoffPreviousOwner = dto.RecentHandoffPreviousOwner is null ? null : TrackOwner.FromSnapshot(dto.RecentHandoffPreviousOwner),
            RecentHandoffWasForced = dto.RecentHandoffWasForced,
            RecentHandoffAcceptedAtSeconds = dto.RecentHandoffAcceptedAtSeconds,
            ReachedAssignedAltitude = dto.ReachedAssignedAltitude,
            ConformanceKey = dto.ConformanceKey is null ? null : EramConformanceKey.FromSnapshot(dto.ConformanceKey),
            Pointouts = dto.Pointouts is null
                ? []
                :
                [
                    .. dto.Pointouts.Select(p => new EramPointoutState
                    {
                        OriginatingFacility = p.OriginatingFacility,
                        OriginatingSector = p.OriginatingSector,
                        ReceivingFacility = p.ReceivingFacility,
                        ReceivingSector = p.ReceivingSector,
                        IsAcknowledged = p.IsAcknowledged,
                        IsRSideCleared = p.IsRSideCleared,
                        IsDSideCleared = p.IsDSideCleared,
                    }),
                ],
            PointoutMinimizedSectors = dto.PointoutMinimizedSectors is null
                ? []
                : [.. dto.PointoutMinimizedSectors.Select(EramSectorKey.FromSnapshot)],
            FdbOpenSectors = dto.FdbOpenSectors is null ? [] : [.. dto.FdbOpenSectors.Select(EramSectorKey.FromSnapshot)],
        };
}

/// <summary>
/// The flight-plan assignment the ERAM vertical-conformance latch is held against: the altitude in effect
/// (<see cref="AircraftFlightPlan.EramAltitudeFeet"/>, the block ceiling for a block), the block floor, and the ABV flag, all
/// in feet. A different key clears the latch; an equal one keeps it.
/// </summary>
/// <param name="AssignedFeet">The assigned altitude (block ceiling); null or not positive = no assigned altitude.</param>
/// <param name="BlockFloorFeet">The block floor; non-null only for a block altitude.</param>
/// <param name="IsAbove">ABV: the band has no upper bound.</param>
public readonly record struct EramConformanceKey(int? AssignedFeet, int? BlockFloorFeet, bool IsAbove)
{
    /// <summary>The conformance tolerance either side of the assigned altitude (or block), in feet.</summary>
    public const int ToleranceFeet = 200;

    /// <summary>The key of the plan's current assignment.</summary>
    public static EramConformanceKey Of(AircraftFlightPlan plan) => new(plan.EramAltitudeFeet, plan.Altitude.BlockFloorFeet, plan.Altitude.IsAbove);

    /// <summary>
    /// Whether <paramref name="altitudeFeet"/> is inside the band: the assigned altitude ± 200 ft, the block from its floor
    /// − 200 ft to its ceiling + 200 ft, or anything from the assigned altitude − 200 ft up for ABV. Always true with no
    /// assigned altitude.
    /// </summary>
    public bool Contains(double altitudeFeet)
    {
        if ((AssignedFeet is not { } assigned) || (assigned <= 0))
        {
            return true;
        }

        int floor = (BlockFloorFeet ?? assigned) - ToleranceFeet;
        if (altitudeFeet < floor)
        {
            return false;
        }
        return IsAbove || (altitudeFeet <= (assigned + ToleranceFeet));
    }

    public EramConformanceKeyDto ToSnapshot() =>
        new()
        {
            AssignedFeet = AssignedFeet,
            BlockFloorFeet = BlockFloorFeet,
            IsAbove = IsAbove,
        };

    public static EramConformanceKey FromSnapshot(EramConformanceKeyDto dto) => new(dto.AssignedFeet, dto.BlockFloorFeet, dto.IsAbove);
}
