using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public class AircraftState
{
    public required string Callsign { get; set; }
    public required string AircraftType { get; set; }
    public string BaseAircraftType => StripTypePrefix(AircraftType);

    /// <summary>
    /// Extract ICAO type designator from FAA flight plan format.
    /// "B738" → "B738", "H/B763/L" → "B763", "B738/L" → "B738".
    /// </summary>
    internal static string StripTypePrefix(string aircraftType)
    {
        var parts = aircraftType.Split('/');
        if (parts.Length >= 2 && parts[0] is "H" or "J" or "S")
        {
            return parts[1];
        }

        return parts[0];
    }

    public string? ScenarioId { get; set; }
    public string Cid { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public TrueHeading TrueHeading { get; set; }

    /// <summary>Ground track direction in degrees true. Equals TrueHeading when there is no wind.</summary>
    public TrueHeading TrueTrack { get; set; }

    /// <summary>Cached magnetic declination at this aircraft's position. Updated each tick by FlightPhysics.</summary>
    public double Declination { get; set; }

    /// <summary>Magnetic heading derived from TrueHeading and Declination.</summary>
    public MagneticHeading MagneticHeading => TrueHeading.ToMagnetic(Declination);

    /// <summary>Magnetic track derived from TrueTrack and Declination.</summary>
    public MagneticHeading MagneticTrack => TrueTrack.ToMagnetic(Declination);

    public double Altitude { get; set; }

    /// <summary>
    /// Most recently observed wind components in knots (North, East).
    /// Updated by FlightPhysics each tick from the WeatherProfile at the aircraft's altitude.
    /// Zero when no weather is loaded or aircraft is on the ground.
    /// </summary>
    public (double N, double E) WindComponents { get; internal set; }

    /// <summary>
    /// Ground speed in knots. On the ground: equals IndicatedAirspeed.
    /// Airborne: derived from IAS → TAS (altitude correction) plus cached wind vector.
    /// </summary>
    public double GroundSpeed
    {
        get
        {
            if (IsOnGround)
            {
                return IndicatedAirspeed;
            }

            double tasKts = WindInterpolator.IasToTas(IndicatedAirspeed, Altitude);
            double hdgRad = TrueHeading.Degrees * (Math.PI / 180.0);
            double gsN = tasKts * Math.Cos(hdgRad) + WindComponents.N;
            double gsE = tasKts * Math.Sin(hdgRad) + WindComponents.E;
            return Math.Sqrt(gsN * gsN + gsE * gsE);
        }
    }

    /// <summary>
    /// Indicated airspeed in knots. What the pilot flies and ATC commands.
    /// Differs from GroundSpeed when wind is present or at altitude (TAS correction).
    /// </summary>
    public double IndicatedAirspeed { get; set; }

    public double VerticalSpeed { get; set; }
    public uint AssignedBeaconCode { get; set; }
    public uint BeaconCode { get; set; }
    public string Departure { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>
    /// Cached ground layout for the airport this aircraft is currently at (or about to land at).
    /// Set at lifecycle events: spawn, CLAND, flight plan amend. Used by phases and commands.
    /// </summary>
    public AirportGroundLayout? GroundLayout { get; set; }
    public string Route { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string EquipmentSuffix { get; set; } = "A";
    public string FlightRules { get; set; } = "IFR";
    public bool IsVfr => FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
    public int CruiseAltitude { get; set; }
    public int CruiseSpeed { get; set; }
    public string TransponderMode { get; set; } = "C";
    public bool IsIdenting { get; set; }
    public double? IdentStartedAt { get; set; }
    public bool IsOnGround { get; set; }
    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
    public PhaseList? Phases { get; set; }
    public List<DeferredDispatch> DeferredDispatches { get; } = [];
    public List<string> PendingWarnings { get; } = [];
    public List<string> PendingNotifications { get; } = [];
    public List<ApproachScore> PendingApproachScores { get; } = [];
    public ApproachScore? ActiveApproachScore { get; set; }

    // Ground operations state
    public TaxiRoute? AssignedTaxiRoute { get; set; }
    public string? ParkingSpot { get; set; }
    public string? CurrentTaxiway { get; set; }
    public bool IsHeld { get; set; }
    public string? GiveWayTarget { get; set; }
    public bool AutoDeleteExempt { get; set; }

    /// <summary>
    /// Remaining seconds of BREAK conflict override. While positive, the aircraft
    /// ignores ground conflict speed limits imposed by GroundConflictDetector.
    /// Decremented each tick in ApplySpeedLimits.
    /// </summary>
    public double ConflictBreakRemainingSeconds { get; set; }

    /// <summary>
    /// Max ground speed (kts) imposed by GroundConflictDetector.
    /// Null = no limit. Reset each tick before conflict detection runs.
    /// </summary>
    public double? GroundSpeedLimit { get; set; }

    /// <summary>
    /// When set, FlightPhysics uses this heading for ground position updates
    /// instead of <see cref="TrueHeading"/>. Used by pushback (aircraft nose stays
    /// forward while tug pushes it backward along this direction).
    /// </summary>
    public TrueHeading? PushbackTrueHeading { get; set; }

    // Track operations state
    public bool HasFlightPlan { get; set; }
    public TrackOwner? Owner { get; set; }
    public TrackOwner? HandoffPeer { get; set; }
    public TrackOwner? HandoffRedirectedBy { get; set; }
    public StarsPointout? Pointout { get; set; }
    public string? Scratchpad1 { get; set; }
    public bool WasScratchpad1Cleared { get; set; }
    public string? PreviousScratchpad1 { get; set; }
    public string? Scratchpad2 { get; set; }
    public string? PreviousScratchpad2 { get; set; }

    // ASDEX scratchpads (separate from STARS scratchpads above)
    public string? AsdexScratchpad1 { get; set; }
    public string? AsdexScratchpad2 { get; set; }

    public int? TemporaryAltitude { get; set; }
    public int? PilotReportedAltitude { get; set; }
    public bool IsAnnotated { get; set; }
    public bool OnHandoff { get; set; }
    public bool HandoffAccepted { get; set; }
    public double? HandoffInitiatedAt { get; set; }
    public int? AssignedAltitude { get; set; }

    // Approach expectation
    public string? ExpectedApproach { get; set; }

    /// <summary>
    /// Approach clearance issued while the aircraft is still on a STAR en route to the
    /// approach connecting fix. Activated when the aircraft reaches the connecting fix
    /// via normal navigation. Null when no deferred approach is pending.
    /// </summary>
    public PendingApproachInfo? PendingApproachClearance { get; set; }

    // Procedure state (SID/STAR)
    public string? ActiveSidId { get; set; }
    public string? ActiveStarId { get; set; }

    /// <summary>Runway designator snapshot from when the SID was activated. Used for radar route display.</summary>
    public string? DepartureRunway { get; set; }

    /// <summary>Runway designator snapshot for arrival. Set when an approach or pattern assigns a runway. Used for radar route display.</summary>
    public string? DestinationRunway { get; set; }
    public bool SidViaMode { get; set; }
    public bool StarViaMode { get; set; }
    public int? SidViaCeiling { get; set; }
    public int? StarViaFloor { get; set; }

    /// <summary>Current bank angle in degrees. Positive = right bank, negative = left bank. Zero when wings level.</summary>
    public double BankAngle { get; set; }

    /// <summary>DSR flag: when true, suppresses via-mode speed constraints at waypoints. Cleared by new SPD, CVIA, or DVIA.</summary>
    public bool SpeedRestrictionsDeleted { get; set; }

    /// <summary>When true, climb/descent rate is multiplied by 1.5. Cleared on altitude reached or by NORM/CM/DM.</summary>
    public bool IsExpediting { get; set; }

    /// <summary>Override for pattern downwind offset distance (NM). Null uses category default.</summary>
    public double? PatternSizeOverrideNm { get; set; }

    /// <summary>Override for pattern altitude (feet MSL). Null uses category-based default. Set by CM/DM during pattern mode or explicit altitude argument on MLT/MRT/CTO/GA.</summary>
    public double? PatternAltitudeOverrideFt { get; set; }

    // Visual approach state
    public bool HasReportedFieldInSight { get; set; }
    public bool HasReportedTrafficInSight { get; set; }
    public string? FollowingCallsign { get; set; }

    // Voice type (CRC display): 0=Unknown, 1=Full, 2=ReceiveOnly, 3=TextOnly
    public int VoiceType { get; set; } = 1;

    // TDLS dump flag (CRC flight plan)
    public bool TdlsDumped { get; set; }

    // Hold annotations (CRC display)
    public string? HoldAnnotationFix { get; set; }
    public int HoldAnnotationDirection { get; set; }
    public int HoldAnnotationTurns { get; set; }
    public int? HoldAnnotationLegLength { get; set; }
    public bool HoldAnnotationLegLengthInNm { get; set; }
    public int HoldAnnotationEfc { get; set; }

    // Clearance (departure clearance from CRC)
    public string? ClearanceExpect { get; set; }
    public string? ClearanceSid { get; set; }
    public string? ClearanceTransition { get; set; }
    public string? ClearanceClimbout { get; set; }
    public string? ClearanceClimbvia { get; set; }
    public string? ClearanceInitialAlt { get; set; }
    public string? ClearanceContactInfo { get; set; }
    public string? ClearanceLocalInfo { get; set; }
    public string? ClearanceDepFreq { get; set; }

    // Unsupported (ghost) track — stationary, no surveillance data
    public bool IsUnsupported { get; set; }

    /// <summary>
    /// Override display position for ghost tracks overlaying real aircraft.
    /// When non-null, STARS shows the ghost at this position while the real
    /// aircraft continues physics at its true lat/lon. Null for pure ghosts
    /// (where the real position IS the ghost position).
    /// </summary>
    public double? UnsupportedLatitude { get; set; }
    public double? UnsupportedLongitude { get; set; }

    // Conflict alert
    public bool IsCaInhibited { get; set; }

    // Per-track STARS display state (shared across CRC scopes on same TCP)
    public bool IsModeCInhibited { get; set; }
    public bool IsMsawInhibited { get; set; }
    public bool IsDuplicateBeaconInhibited { get; set; }
    public int? TpaType { get; set; }
    public int? GlobalLeaderDirection { get; set; }
    public List<Tcp> ForcedPointoutsTo { get; set; } = [];

    // Per-TCP shared state (per-track, per-controller display overrides)
    public Dictionary<string, StarsTrackSharedState> SharedState { get; set; } = [];

    // Position history for STARS radar trails (recorded every ~5 sim-seconds)
    public List<(double Lat, double Lon)> PositionHistory { get; } = new(10);

    public static AircraftState FromSnapshot(AircraftSnapshotDto dto, AirportGroundLayout? groundLayout)
    {
        var ac = new AircraftState
        {
            Callsign = dto.Callsign,
            AircraftType = dto.AircraftType,
            ScenarioId = dto.ScenarioId,
            Cid = dto.Cid,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            TrueHeading = new TrueHeading(dto.TrueHeadingDeg),
            TrueTrack = new TrueHeading(dto.TrueTrackDeg),
            Declination = dto.Declination,
            Altitude = dto.Altitude,
            IndicatedAirspeed = dto.IndicatedAirspeed,
            VerticalSpeed = dto.VerticalSpeed,
            BankAngle = dto.BankAngle,
            HasFlightPlan = dto.HasFlightPlan,
            Departure = dto.Departure,
            Destination = dto.Destination,
            Route = dto.Route,
            Remarks = dto.Remarks,
            EquipmentSuffix = dto.EquipmentSuffix,
            FlightRules = dto.FlightRules,
            CruiseAltitude = dto.CruiseAltitude,
            CruiseSpeed = dto.CruiseSpeed,
            TransponderMode = dto.TransponderMode,
            AssignedBeaconCode = dto.AssignedBeaconCode,
            BeaconCode = dto.BeaconCode,
            IsIdenting = dto.IsIdenting,
            IdentStartedAt = dto.IdentStartedAt,
            IsOnGround = dto.IsOnGround,
            GroundLayout = groundLayout,
            ParkingSpot = dto.ParkingSpot,
            CurrentTaxiway = dto.CurrentTaxiway,
            IsHeld = dto.IsHeld,
            GiveWayTarget = dto.GiveWayTarget,
            AutoDeleteExempt = dto.AutoDeleteExempt,
            ConflictBreakRemainingSeconds = dto.ConflictBreakRemainingSeconds,
            GroundSpeedLimit = dto.GroundSpeedLimit,
            PushbackTrueHeading = dto.PushbackTrueHeadingDeg.HasValue ? new TrueHeading(dto.PushbackTrueHeadingDeg.Value) : null,
            Owner = dto.Owner is not null ? TrackOwner.FromSnapshot(dto.Owner) : null,
            HandoffPeer = dto.HandoffPeer is not null ? TrackOwner.FromSnapshot(dto.HandoffPeer) : null,
            HandoffRedirectedBy = dto.HandoffRedirectedBy is not null ? TrackOwner.FromSnapshot(dto.HandoffRedirectedBy) : null,
            Pointout = dto.Pointout is not null ? StarsPointout.FromSnapshot(dto.Pointout) : null,
            Scratchpad1 = dto.Scratchpad1,
            WasScratchpad1Cleared = dto.WasScratchpad1Cleared,
            PreviousScratchpad1 = dto.PreviousScratchpad1,
            Scratchpad2 = dto.Scratchpad2,
            PreviousScratchpad2 = dto.PreviousScratchpad2,
            AsdexScratchpad1 = dto.AsdexScratchpad1,
            AsdexScratchpad2 = dto.AsdexScratchpad2,
            TemporaryAltitude = dto.TemporaryAltitude,
            PilotReportedAltitude = dto.PilotReportedAltitude,
            IsAnnotated = dto.IsAnnotated,
            OnHandoff = dto.OnHandoff,
            HandoffAccepted = dto.HandoffAccepted,
            HandoffInitiatedAt = dto.HandoffInitiatedAt,
            AssignedAltitude = dto.AssignedAltitude,
            ExpectedApproach = dto.ExpectedApproach,
            PendingApproachClearance = dto.PendingApproachClearance is not null
                ? PendingApproachInfo.FromSnapshot(dto.PendingApproachClearance)
                : null,
            ActiveSidId = dto.ActiveSidId,
            ActiveStarId = dto.ActiveStarId,
            DepartureRunway = dto.DepartureRunway,
            DestinationRunway = dto.DestinationRunway,
            SidViaMode = dto.SidViaMode,
            StarViaMode = dto.StarViaMode,
            SidViaCeiling = dto.SidViaCeiling,
            StarViaFloor = dto.StarViaFloor,
            SpeedRestrictionsDeleted = dto.SpeedRestrictionsDeleted,
            IsExpediting = dto.IsExpediting,
            PatternSizeOverrideNm = dto.PatternSizeOverrideNm,
            PatternAltitudeOverrideFt = dto.PatternAltitudeOverrideFt,
            HasReportedFieldInSight = dto.HasReportedFieldInSight,
            HasReportedTrafficInSight = dto.HasReportedTrafficInSight,
            FollowingCallsign = dto.FollowingCallsign,
            VoiceType = dto.VoiceType,
            TdlsDumped = dto.TdlsDumped,
            HoldAnnotationFix = dto.HoldAnnotationFix,
            HoldAnnotationDirection = dto.HoldAnnotationDirection,
            HoldAnnotationTurns = dto.HoldAnnotationTurns,
            HoldAnnotationLegLength = dto.HoldAnnotationLegLength,
            HoldAnnotationLegLengthInNm = dto.HoldAnnotationLegLengthInNm,
            HoldAnnotationEfc = dto.HoldAnnotationEfc,
            ClearanceExpect = dto.ClearanceExpect,
            ClearanceSid = dto.ClearanceSid,
            ClearanceTransition = dto.ClearanceTransition,
            ClearanceClimbout = dto.ClearanceClimbout,
            ClearanceClimbvia = dto.ClearanceClimbvia,
            ClearanceInitialAlt = dto.ClearanceInitialAlt,
            ClearanceContactInfo = dto.ClearanceContactInfo,
            ClearanceLocalInfo = dto.ClearanceLocalInfo,
            ClearanceDepFreq = dto.ClearanceDepFreq,
            IsUnsupported = dto.IsUnsupported,
            UnsupportedLatitude = dto.UnsupportedLatitude,
            UnsupportedLongitude = dto.UnsupportedLongitude,
            IsCaInhibited = dto.IsCaInhibited,
            IsModeCInhibited = dto.IsModeCInhibited,
            IsMsawInhibited = dto.IsMsawInhibited,
            IsDuplicateBeaconInhibited = dto.IsDuplicateBeaconInhibited,
            TpaType = dto.TpaType,
            GlobalLeaderDirection = dto.GlobalLeaderDirection,
            AssignedTaxiRoute = dto.AssignedTaxiRoute is not null ? TaxiRoute.FromSnapshot(dto.AssignedTaxiRoute, groundLayout) : null,
            Queue = CommandQueue.FromSnapshot(dto.Queue),
            Phases = dto.Phases is not null ? PhaseList.FromSnapshot(dto.Phases, groundLayout) : null,
        };

        ac.WindComponents = (dto.WindN, dto.WindE);
        ControlTargets.RestoreFrom(dto.Targets, ac.Targets);

        if (dto.ForcedPointoutsTo is not null)
        {
            ac.ForcedPointoutsTo = dto.ForcedPointoutsTo.Select(Tcp.FromSnapshot).ToList();
        }

        if (dto.SharedState is not null)
        {
            ac.SharedState = dto.SharedState.ToDictionary(kv => kv.Key, kv => StarsTrackSharedState.FromSnapshot(kv.Value));
        }

        if (dto.PositionHistory is not null)
        {
            foreach (var p in dto.PositionHistory)
            {
                ac.PositionHistory.Add((p.Lat, p.Lon));
            }
        }

        if (dto.DeferredDispatches is not null)
        {
            foreach (var dd in dto.DeferredDispatches)
            {
                var dispatch = DeferredDispatch.FromSnapshot(dd);
                if (dispatch is not null)
                {
                    ac.DeferredDispatches.Add(dispatch);
                }
            }
        }

        return ac;
    }

    public AircraftSnapshotDto ToSnapshot() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            ScenarioId = ScenarioId,
            Cid = Cid,
            Latitude = Latitude,
            Longitude = Longitude,
            TrueHeadingDeg = TrueHeading.Degrees,
            TrueTrackDeg = TrueTrack.Degrees,
            Declination = Declination,
            Altitude = Altitude,
            IndicatedAirspeed = IndicatedAirspeed,
            VerticalSpeed = VerticalSpeed,
            BankAngle = BankAngle,
            WindN = WindComponents.N,
            WindE = WindComponents.E,
            HasFlightPlan = HasFlightPlan,
            Departure = Departure,
            Destination = Destination,
            Route = Route,
            Remarks = Remarks,
            EquipmentSuffix = EquipmentSuffix,
            FlightRules = FlightRules,
            CruiseAltitude = CruiseAltitude,
            CruiseSpeed = CruiseSpeed,
            TransponderMode = TransponderMode,
            AssignedBeaconCode = AssignedBeaconCode,
            BeaconCode = BeaconCode,
            IsIdenting = IsIdenting,
            IdentStartedAt = IdentStartedAt,
            IsOnGround = IsOnGround,
            ParkingSpot = ParkingSpot,
            CurrentTaxiway = CurrentTaxiway,
            IsHeld = IsHeld,
            GiveWayTarget = GiveWayTarget,
            AutoDeleteExempt = AutoDeleteExempt,
            ConflictBreakRemainingSeconds = ConflictBreakRemainingSeconds,
            GroundSpeedLimit = GroundSpeedLimit,
            PushbackTrueHeadingDeg = PushbackTrueHeading?.Degrees,
            Owner = Owner?.ToSnapshot(),
            HandoffPeer = HandoffPeer?.ToSnapshot(),
            HandoffRedirectedBy = HandoffRedirectedBy?.ToSnapshot(),
            Pointout = Pointout?.ToSnapshot(),
            Scratchpad1 = Scratchpad1,
            WasScratchpad1Cleared = WasScratchpad1Cleared,
            PreviousScratchpad1 = PreviousScratchpad1,
            Scratchpad2 = Scratchpad2,
            PreviousScratchpad2 = PreviousScratchpad2,
            AsdexScratchpad1 = AsdexScratchpad1,
            AsdexScratchpad2 = AsdexScratchpad2,
            TemporaryAltitude = TemporaryAltitude,
            PilotReportedAltitude = PilotReportedAltitude,
            IsAnnotated = IsAnnotated,
            OnHandoff = OnHandoff,
            HandoffAccepted = HandoffAccepted,
            HandoffInitiatedAt = HandoffInitiatedAt,
            AssignedAltitude = AssignedAltitude,
            ExpectedApproach = ExpectedApproach,
            PendingApproachClearance = PendingApproachClearance?.ToSnapshot(),
            ActiveSidId = ActiveSidId,
            ActiveStarId = ActiveStarId,
            DepartureRunway = DepartureRunway,
            DestinationRunway = DestinationRunway,
            SidViaMode = SidViaMode,
            StarViaMode = StarViaMode,
            SidViaCeiling = SidViaCeiling,
            StarViaFloor = StarViaFloor,
            SpeedRestrictionsDeleted = SpeedRestrictionsDeleted,
            IsExpediting = IsExpediting,
            PatternSizeOverrideNm = PatternSizeOverrideNm,
            PatternAltitudeOverrideFt = PatternAltitudeOverrideFt,
            HasReportedFieldInSight = HasReportedFieldInSight,
            HasReportedTrafficInSight = HasReportedTrafficInSight,
            FollowingCallsign = FollowingCallsign,
            VoiceType = VoiceType,
            TdlsDumped = TdlsDumped,
            HoldAnnotationFix = HoldAnnotationFix,
            HoldAnnotationDirection = HoldAnnotationDirection,
            HoldAnnotationTurns = HoldAnnotationTurns,
            HoldAnnotationLegLength = HoldAnnotationLegLength,
            HoldAnnotationLegLengthInNm = HoldAnnotationLegLengthInNm,
            HoldAnnotationEfc = HoldAnnotationEfc,
            ClearanceExpect = ClearanceExpect,
            ClearanceSid = ClearanceSid,
            ClearanceTransition = ClearanceTransition,
            ClearanceClimbout = ClearanceClimbout,
            ClearanceClimbvia = ClearanceClimbvia,
            ClearanceInitialAlt = ClearanceInitialAlt,
            ClearanceContactInfo = ClearanceContactInfo,
            ClearanceLocalInfo = ClearanceLocalInfo,
            ClearanceDepFreq = ClearanceDepFreq,
            IsUnsupported = IsUnsupported,
            UnsupportedLatitude = UnsupportedLatitude,
            UnsupportedLongitude = UnsupportedLongitude,
            IsCaInhibited = IsCaInhibited,
            IsModeCInhibited = IsModeCInhibited,
            IsMsawInhibited = IsMsawInhibited,
            IsDuplicateBeaconInhibited = IsDuplicateBeaconInhibited,
            TpaType = TpaType,
            GlobalLeaderDirection = GlobalLeaderDirection,
            ForcedPointoutsTo = ForcedPointoutsTo.Count > 0 ? ForcedPointoutsTo.Select(t => t.ToSnapshot()).ToList() : null,
            SharedState = SharedState.Count > 0 ? SharedState.ToDictionary(kv => kv.Key, kv => kv.Value.ToSnapshot()) : null,
            PositionHistory = PositionHistory.Count > 0 ? PositionHistory.Select(p => new PositionDto { Lat = p.Lat, Lon = p.Lon }).ToList() : null,
            ActiveApproachScore = ActiveApproachScore is { } score
                ? new ApproachScoreDto
                {
                    Callsign = score.Callsign,
                    ApproachId = score.ApproachId,
                    AirportCode = score.AirportCode,
                    RunwayId = score.RunwayId,
                    InterceptAngleDeg = score.InterceptAngleDeg,
                    InterceptDistanceNm = score.InterceptDistanceNm,
                    EstablishedDistanceNm = score.MinInterceptDistanceNm,
                    GoAround = false,
                }
                : null,
            Targets = Targets.ToSnapshot(),
            Queue = Queue.ToSnapshot(),
            Phases = Phases?.ToSnapshot(),
            DeferredDispatches = DeferredDispatches.Count > 0 ? DeferredDispatches.Select(d => d.ToSnapshot()).ToList() : null,
            AssignedTaxiRoute = AssignedTaxiRoute?.ToSnapshot(),
        };

    public HashSet<string> GetProgrammedFixes()
    {
        IReadOnlyList<string>? activeApproachFixNames = null;
        if (Phases?.ActiveApproach?.Procedure is { } activeProc)
        {
            activeApproachFixNames = ApproachCommandHandler.GetApproachFixNames(activeProc);
        }

        return ProgrammedFixResolver.Resolve(
            Route,
            ExpectedApproach,
            Destination,
            Departure,
            activeApproachFixNames,
            ActiveStarId,
            DestinationRunway
        );
    }
}
