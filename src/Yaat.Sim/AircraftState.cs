using System.Text.Json.Serialization;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
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
    public static string StripTypePrefix(string aircraftType)
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

    /// <summary>
    /// Operational airport context (e.g. "OAK"). Set from the scenario aircraft's
    /// <c>airportId</c>, the scenario primary airport, or the ADD command's
    /// primary airport. Used by airport-relative commands (pattern entry, ERD)
    /// when <see cref="Phases.AssignedRunway"/> isn't yet set and the aircraft
    /// has no filed flight plan to provide a destination — typical for VFR
    /// cold-call aircraft.
    /// </summary>
    public string AirportId { get; set; } = "";

    /// <summary>Geographic position in degrees.</summary>
    public LatLon Position { get; set; }

    public TrueHeading TrueHeading { get; set; }

    /// <summary>Ground track direction in degrees true. Equals TrueHeading when there is no wind.</summary>
    public TrueHeading TrueTrack { get; set; }

    /// <summary>Cached magnetic declination at this aircraft's position. Updated each tick by FlightPhysics.</summary>
    public double Declination { get; set; }

    /// <summary>
    /// Position at which <see cref="Declination"/> was last recomputed. Used by
    /// <c>FlightPhysics.Update</c> to skip the expensive WMM evaluation when the aircraft
    /// has moved less than ~1 nm. <c>null</c> means "not cached yet". Runtime-only —
    /// intentionally not serialized (cache warms up on first tick after a DTO round-trip).
    /// </summary>
    [JsonIgnore]
    public LatLon? DeclinationCachePosition { get; set; }

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

    public AircraftFlightPlan FlightPlan { get; set; } = new();

    public AircraftGroundOps Ground { get; set; } = new();

    public AircraftTransponder Transponder { get; set; } = new();

    public bool IsOnGround { get; set; }
    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
    public PhaseList? Phases { get; set; }
    public List<DeferredDispatch> DeferredDispatches { get; } = [];
    public List<string> PendingWarnings { get; } = [];
    public List<string> PendingNotifications { get; } = [];

    /// <summary>
    /// Pilot transmissions emitted by the sim in RPO mode when the
    /// <c>RpoShowPilotSpeech</c> scenario setting is on. Drained per tick into the
    /// terminal as <c>PilotSpeech</c>-kind entries (green), rendered with the spelled-out
    /// spoken form built by <see cref="Pilot.PilotResponder"/>. Solo mode queues delayed
    /// SAY/audio entries in <see cref="PendingPilotTransmissions"/>; RPO with the setting
    /// off keeps using <see cref="PendingWarnings"/>. Transient — not snapshot-serialized.
    /// </summary>
    public List<string> PendingPilotSpeech { get; } = [];

    /// <summary>
    /// Terse pilot readbacks for visual-acquisition events (RTIS / RFIS — the "Have
    /// N9225L in sight" / "Negative contact, looking" line). Drained per tick into the
    /// terminal as a <c>SayReadback</c>-kind entry (the kind starts with "Say" so the
    /// client routes it to the SAY channel just like the SPOS / SALT verb output).
    /// Used when the spelled-out <see cref="PendingPilotSpeech"/> path is not active —
    /// RPO mode without <c>RpoShowPilotSpeech</c> lands here. Solo mode uses the delayed
    /// <see cref="PendingPilotTransmissions"/> queue. Transient — not snapshot-serialized.
    /// </summary>
    public List<string> PendingPilotReadbacks { get; } = [];

    /// <summary>
    /// Typed solo-training pilot transmissions awaiting server broadcast to
    /// the client audio layer. Each entry stores compact terminal text plus
    /// bracket-free spoken text for TTS. Transient — not snapshot-serialized.
    /// </summary>
    public List<PilotTransmission> PendingPilotTransmissions { get; } = [];

    public List<ApproachScore> PendingApproachScores { get; } = [];
    public ApproachScore? ActiveApproachScore { get; set; }

    /// <summary>
    /// Set the first time any pilot transmission fires for this aircraft (spawn check-in,
    /// readback, leg announcement, proactive call). Cross-phase one-shot — gates "fresh-spawn"
    /// check-ins (FinalApproachPhase, future airborne-spawn) so they don't re-fire after the
    /// aircraft has already been talking. Snapshot-serialized so replays produce identical
    /// pilot output.
    /// </summary>
    public bool HasMadeInitialContact { get; set; }

    /// <summary>
    /// Set when the controller has used this aircraft's callsign in a successful live command.
    /// In solo training this satisfies the Class C "two-way radio communications established"
    /// entry gate after the pilot's initial contact (AIM §3-2-4).
    /// </summary>
    public bool HasControllerAcknowledgedInitialContact { get; set; }

    /// <summary>
    /// Set after solo-training CT/FCA releases the aircraft from the student's frequency.
    /// Snapshot-serialized so session-report advisory scoring does not re-score aircraft
    /// after replay/rewind restores post-transfer state.
    /// </summary>
    public bool HasLeftStudentFrequency { get; set; }

    /// <summary>
    /// Set when the controller has issued the explicit VFR Class Bravo clearance
    /// (FAA 7110.65 §7-9-2). Snapshot-serialized so replays keep the entry gate state.
    /// </summary>
    public bool IsClearedIntoBravo { get; set; }

    /// <summary>
    /// Set after <c>LinedUpAndWaitingPhase</c>'s 10-second "ready" reminder fires once. Never
    /// cleared — a single LUAW is one logical event, so a touch-and-go's second LUAW does
    /// not re-fire the reminder. Snapshot-serialized.
    /// </summary>
    public bool HasAnnouncedLinedUpReady { get; set; }

    /// <summary>
    /// True while the "approaching final without a landing clearance" warning has fired on
    /// the current final approach and the aircraft still lacks a landing clearance. Drives
    /// the flashing red <c>NoLndgClnc</c> datablock line on the client. Written each tick by
    /// <see cref="Phases.Tower.FinalApproachPhase"/>; cleared when a landing clearance is
    /// granted or when the phase yields to <c>GoAroundPhase</c>. Snapshot-serialized.
    /// </summary>
    public bool NoLandingClearanceWarningActive { get; set; }

    /// <summary>
    /// Latest solo-training pilot-originated request still waiting on controller action.
    /// Snapshot-serialized so replay/export preserves follow-up timing.
    /// </summary>
    public PilotPendingRequest? PendingPilotRequest { get; set; }

    public NavTickDiag? LastNavDiag { get; set; }

    public AircraftTrack Track { get; set; } = new();

    public AircraftStarsState Stars { get; set; } = new();

    public AircraftApproachState Approach { get; set; } = new();

    public AircraftProcedure Procedure { get; set; } = new();

    /// <summary>Current bank angle in degrees. Positive = right bank, negative = left bank. Zero when wings level.</summary>
    public double BankAngle { get; set; }

    public AircraftPattern Pattern { get; set; } = new();

    /// <summary>
    /// Pilot-side "watch for a condition" state — populated when RTIS soft-fails
    /// (pilot keeps looking for traffic) or, in the future, for other
    /// report-when-satisfied conditions. Re-evaluated each tick by
    /// <see cref="PilotObservationUpdater"/>. Ephemeral runtime state — not
    /// persisted in snapshots.
    /// </summary>
    public List<PilotObservation> PendingObservations { get; } = [];

    public AircraftVoice Voice { get; set; } = new();

    public AircraftHoldAnnotation HoldAnnotation { get; set; } = new();

    public AircraftEramState Eram { get; set; } = new();

    public AircraftClearance Clearance { get; set; } = new();

    public AircraftGhostTrack Ghost { get; set; } = new();

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
            AirportId = dto.AirportId,
            Position = dto.Position,
            TrueHeading = new TrueHeading(dto.TrueHeadingDeg),
            TrueTrack = new TrueHeading(dto.TrueTrackDeg),
            Declination = dto.Declination,
            Altitude = dto.Altitude,
            IndicatedAirspeed = dto.IndicatedAirspeed,
            VerticalSpeed = dto.VerticalSpeed,
            BankAngle = dto.BankAngle,
            FlightPlan = AircraftFlightPlan.FromSnapshot(dto.FlightPlan),
            Ground = AircraftGroundOps.FromSnapshot(dto.Ground, groundLayout),
            Transponder = AircraftTransponder.FromSnapshot(dto.Transponder),
            IsOnGround = dto.IsOnGround,
            HasMadeInitialContact = dto.HasMadeInitialContact,
            HasControllerAcknowledgedInitialContact = dto.HasControllerAcknowledgedInitialContact,
            HasLeftStudentFrequency = dto.HasLeftStudentFrequency,
            IsClearedIntoBravo = dto.IsClearedIntoBravo,
            HasAnnouncedLinedUpReady = dto.HasAnnouncedLinedUpReady,
            NoLandingClearanceWarningActive = dto.NoLandingClearanceWarningActive,
            PendingPilotRequest = dto.PendingPilotRequest is not null ? PilotPendingRequest.FromSnapshot(dto.PendingPilotRequest) : null,
            Track = AircraftTrack.FromSnapshot(dto.Track),
            Stars = AircraftStarsState.FromSnapshot(dto.Stars),
            Approach = AircraftApproachState.FromSnapshot(dto.Approach),
            Procedure = AircraftProcedure.FromSnapshot(dto.Procedure),
            Pattern = AircraftPattern.FromSnapshot(dto.Pattern),
            Voice = AircraftVoice.FromSnapshot(dto.Voice),
            HoldAnnotation = AircraftHoldAnnotation.FromSnapshot(dto.HoldAnnotation),
            Eram = AircraftEramState.FromSnapshot(dto.Eram),
            Clearance = AircraftClearance.FromSnapshot(dto.Clearance),
            Ghost = AircraftGhostTrack.FromSnapshot(dto.Ghost),
            Queue = CommandQueue.FromSnapshot(dto.Queue),
            Phases = dto.Phases is not null ? PhaseList.FromSnapshot(dto.Phases, groundLayout) : null,
        };

        ac.WindComponents = (dto.WindN, dto.WindE);
        ControlTargets.RestoreFrom(dto.Targets, ac.Targets);

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
            AirportId = AirportId,
            Position = Position,
            TrueHeadingDeg = TrueHeading.Degrees,
            TrueTrackDeg = TrueTrack.Degrees,
            Declination = Declination,
            Altitude = Altitude,
            IndicatedAirspeed = IndicatedAirspeed,
            VerticalSpeed = VerticalSpeed,
            BankAngle = BankAngle,
            WindN = WindComponents.N,
            WindE = WindComponents.E,
            FlightPlan = FlightPlan.ToSnapshot(),
            Ground = Ground.ToSnapshot(),
            Transponder = Transponder.ToSnapshot(),
            IsOnGround = IsOnGround,
            HasMadeInitialContact = HasMadeInitialContact,
            HasControllerAcknowledgedInitialContact = HasControllerAcknowledgedInitialContact,
            HasLeftStudentFrequency = HasLeftStudentFrequency,
            IsClearedIntoBravo = IsClearedIntoBravo,
            HasAnnouncedLinedUpReady = HasAnnouncedLinedUpReady,
            NoLandingClearanceWarningActive = NoLandingClearanceWarningActive,
            PendingPilotRequest = PendingPilotRequest?.ToSnapshot(),
            Track = Track.ToSnapshot(),
            Stars = Stars.ToSnapshot(),
            Approach = Approach.ToSnapshot(),
            Procedure = Procedure.ToSnapshot(),
            Pattern = Pattern.ToSnapshot(),
            Voice = Voice.ToSnapshot(),
            HoldAnnotation = HoldAnnotation.ToSnapshot(),
            Eram = Eram.ToSnapshot(),
            Clearance = Clearance.ToSnapshot(),
            Ghost = Ghost.ToSnapshot(),
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
        };

    public HashSet<string> GetProgrammedFixes()
    {
        IReadOnlyList<string>? activeApproachFixNames = null;
        if (Phases?.ActiveApproach?.Procedure is { } activeProc)
        {
            activeApproachFixNames = ApproachCommandHandler.GetApproachFixNames(activeProc);
        }

        return ProgrammedFixResolver.Resolve(
            FlightPlan.Route,
            Approach.Expected,
            FlightPlan.Destination,
            FlightPlan.Departure,
            activeApproachFixNames,
            Procedure.ActiveStarId,
            Procedure.DestinationRunway
        );
    }
}
