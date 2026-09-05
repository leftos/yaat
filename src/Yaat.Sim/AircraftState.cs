using System.Text.Json.Serialization;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

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

    /// <summary>Maximum stored length of <see cref="Note"/>. Keeps the datablock line bounded.</summary>
    public const int MaxNoteLength = 40;

    /// <summary>
    /// Instructor freetext note shown as an extra datablock line on the radar and
    /// ground views. Server-synced and snapshot-serialized so it follows the aircraft
    /// across views, reconnects, and recordings. Instructor-only — never projected to
    /// CRC student scopes. Preserves case/spaces; capped at <see cref="MaxNoteLength"/> chars.
    /// </summary>
    public string Note { get; set; } = "";

    /// <summary>Caps note text to <see cref="MaxNoteLength"/> characters (trailing whitespace trimmed).</summary>
    public static string TruncateNote(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > MaxNoteLength ? trimmed[..MaxNoteLength] : trimmed;
    }

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
    /// Most recently observed wind components in knots (North, East), in the direction the
    /// wind blows TOWARD. Updated by FlightPhysics each tick from the WeatherProfile at the
    /// aircraft's altitude, on the ground and airborne alike. Zero when no weather is loaded.
    /// </summary>
    public (double N, double E) WindComponents { get; internal set; }

    /// <summary>
    /// Headwind component along the current heading, in knots (negative = tailwind).
    /// Derived from the cached <see cref="WindComponents"/>, so it round-trips through
    /// snapshots and replays with no extra state.
    /// </summary>
    public double HeadwindKts
    {
        get
        {
            double hdgRad = TrueHeading.Degrees * (Math.PI / 180.0);
            return -((WindComponents.N * Math.Cos(hdgRad)) + (WindComponents.E * Math.Sin(hdgRad)));
        }
    }

    /// <summary>
    /// Total wind speed in knots at the aircraft's position/altitude. Derived from the cached
    /// <see cref="WindComponents"/>, so it round-trips through snapshots and replays. Zero when
    /// no weather is loaded.
    /// </summary>
    public double WindSpeedKts => Math.Sqrt((WindComponents.N * WindComponents.N) + (WindComponents.E * WindComponents.E));

    /// <summary>
    /// Ground speed in knots. On the ground: equals IndicatedAirspeed, which carries wheel
    /// speed there (see that property's frame note). Airborne: derived from IAS → TAS
    /// (altitude correction) plus cached wind vector.
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
    /// Airborne: indicated airspeed in knots — what the pilot flies and ATC commands.
    /// ON THE GROUND this field carries groundspeed (wheel speed): taxi speeds, rollout
    /// coast/exit speeds, and braking rates are all ground-frame quantities, and the ASI
    /// is only meaningful at roll speeds. <see cref="GroundFrame"/> owns the conversions
    /// at the liftoff/touchdown transitions.
    /// </summary>
    public double IndicatedAirspeed { get; set; }

    public double VerticalSpeed { get; set; }

    public AircraftFlightPlan FlightPlan { get; set; } = new();

    public AircraftGroundOps Ground { get; set; } = new();

    public AircraftTransponder Transponder { get; set; } = new();

    public bool IsOnGround { get; set; }

    /// <summary>
    /// Latched by the tick loop the first time the aircraft is observed airborne and never
    /// cleared. Distinguishes a pre-departure aircraft (on ground, never flown — its flight plan
    /// is still "proposed") from a landed arrival (on ground, but has flown — its plan stays
    /// active). Snapshot-persisted.
    /// </summary>
    public bool HasBeenAirborne { get; set; }

    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
    public PhaseList? Phases { get; set; }
    public List<DeferredDispatch> DeferredDispatches { get; } = [];
    public List<string> PendingWarnings { get; } = [];
    public List<string> PendingNotifications { get; } = [];

    /// <summary>
    /// Strip commands (AN / STRIP / SCAN / HSC / …) produced by preset, deferred, or triggered
    /// dispatch. The Sim has no strip state — <c>FlightStripState</c> lives on the host
    /// (yaat-server's <c>TrainingRoom</c>), so <see cref="Commands.CommandDispatcher.ApplyCommand"/>
    /// queues strip commands here instead of failing, and the host drains them each tick
    /// (yaat-server's <c>TickProcessor.ProcessDeferredStripDispatches</c>) into
    /// <c>StripCommandHandler</c>. Transient — not snapshot-serialized.
    /// </summary>
    public List<Commands.ParsedCommand> PendingStripDispatches { get; } = [];

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
    /// The AI-side counterpart of <see cref="HasMadeInitialContact"/>, keyed per answering position: the vNAS position
    /// ids of every AI-staffed position this pilot has already made an initial call to. Per position because AIM
    /// 4-2-3.a.1.1 makes each new facility or controller a fresh initial contact — an aircraft that called AI Oakland
    /// Ground still checks in with AI San Francisco Local on final at SFO. Kept apart from the student flag so a
    /// departure handled by AI Ground and AI Local still checks in with a human student radar position later (the rule
    /// scripted clearances follow). Snapshot-serialized in sorted order so snapshots stay byte-stable.
    /// </summary>
    public SortedSet<string> AiInitialContactPositionIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Set when the controller has used this aircraft's callsign in a successful live command.
    /// In solo training this satisfies the Class C "two-way radio communications established"
    /// entry gate after the pilot's initial contact (AIM §3-2-4).
    /// </summary>
    public bool HasControllerAcknowledgedInitialContact { get; set; }

    /// <summary>
    /// Set whenever the controller issues CT or FCA — i.e. the aircraft has been told to
    /// leave the controller's frequency. Set in both solo training and RPO modes; track
    /// ownership transfers (HOO/auto-track) do NOT set this, because radar identity and
    /// comms are independent (FAA 7110.65 §7-6-11). Snapshot-serialized so replays / rewinds
    /// restore post-transfer state without re-firing dependent behavior (solo evaluator's
    /// advisory scoring; InitialClimbPhase's radar-vectors SID release).
    /// </summary>
    public bool HasLeftStudentFrequency { get; set; }

    /// <summary>
    /// Scenario-elapsed seconds at which this aircraft entered the world. Stamped by
    /// <see cref="SimulationEngine"/> at every production spawn path (immediate scenario
    /// load, delayed-queue release, arrival generator, force-spawn, replay restore).
    /// Snapshot-serialized so per-aircraft debrief time-on-frequency survives recording
    /// round-trip. Tests that bypass <c>SimulationEngine</c> and call
    /// <c>SimulationWorld.AddAircraft</c> directly leave it at 0 — debrief metadata is
    /// optional in those paths.
    /// </summary>
    public double SpawnedAtSeconds { get; set; }

    /// <summary>
    /// True when this aircraft was produced by an arrival generator (the simulated-TRACON
    /// arrival stream), set at the single generator spawn path
    /// (<see cref="SimulationEngine.SpawnGeneratedArrival"/>) before the aircraft enters the
    /// world. Scopes the in-trail spacing manager to generator arrivals only. Snapshot-
    /// serialized so it survives recording round-trip / recorded-spawn replay; non-required so
    /// pre-feature recordings default to <see langword="false"/> (no auto-spacing on replay).
    /// </summary>
    public bool IsGeneratorArrival { get; set; }

    /// <summary>
    /// True when this aircraft was produced by an overflight generator — a VFR transit that never lands, so
    /// the destination-matching auto-delete that cleans up arrivals can never reach it. Paired with
    /// <see cref="OverflightExitDistanceNm"/>, which tells the server when the transit has left.
    /// </summary>
    public bool IsGeneratedOverflight { get; set; }

    /// <summary>
    /// Distance from the primary airport, in nautical miles, past which an outbound generated overflight is
    /// deleted. Null for every aircraft that is not a generated overflight.
    /// </summary>
    public double? OverflightExitDistanceNm { get; set; }

    /// <summary>
    /// Scenario-elapsed seconds at which the aircraft's session ended from the student
    /// controller's perspective (landed, handed off, or dropped). Null while the aircraft
    /// is still active. Set by <see cref="Phases.Tower.LandingPhase"/> on touchdown,
    /// <see cref="Commands.ContactCommandHandler"/> on <c>CT</c>/<c>FCA</c>, and the
    /// engine on explicit deletion. Snapshot-serialized.
    /// </summary>
    public double? CompletedAtSeconds { get; set; }

    /// <summary>
    /// Why <see cref="CompletedAtSeconds"/> was stamped. Mirrors the timestamp;
    /// <see cref="Training.CompletionReason.Active"/> while the aircraft is still in
    /// service. Snapshot-serialized.
    /// </summary>
    public CompletionReason CompletionReason { get; set; } = CompletionReason.Active;

    /// <summary>
    /// Free-form completion detail — runway id for <see cref="Training.CompletionReason.Landed"/>,
    /// position callsign for <see cref="Training.CompletionReason.HandedOff"/>. Null while
    /// active. Snapshot-serialized.
    /// </summary>
    public string? CompletionDetail { get; set; }

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

    public AircraftDataBlock DataBlock { get; set; } = new();

    /// <summary>AP/1B military training route clearance state. See <see cref="AircraftMilitaryRoute"/>.</summary>
    public AircraftMilitaryRoute MilitaryRoute { get; set; } = new();

    /// <summary>
    /// Live-traffic state while this aircraft is a shadow of a real aircraft (driven by external
    /// samples via <see cref="LiveTrafficKinematics"/>, not <see cref="FlightPhysics"/>). Null for
    /// every simulated aircraft; assuming a shadow sets it to null.
    /// </summary>
    public AircraftLiveTraffic? LiveTraffic { get; set; }

    /// <summary>True while the aircraft is driven by live-traffic samples rather than the simulation.</summary>
    [JsonIgnore]
    public bool IsShadow => LiveTraffic is not null;

    /// <summary>Sim-seconds between <see cref="PositionHistory"/> samples (the <c>PositionHistory</c> spine step).</summary>
    public const int PositionHistorySampleSeconds = 5;

    /// <summary>Ring-buffer depth of <see cref="PositionHistory"/> — the history-trail dots every display projects.</summary>
    public const int PositionHistoryCapacity = 10;

    /// <summary>Position history for the radar and surface history trails, one sample every <see cref="PositionHistorySampleSeconds"/>.</summary>
    public List<(double Lat, double Lon)> PositionHistory { get; } = new(PositionHistoryCapacity);

    public static AircraftState FromSnapshot(AircraftSnapshotDto dto, AirportGroundLayout? groundLayout)
    {
        var ac = new AircraftState
        {
            Callsign = dto.Callsign,
            AircraftType = dto.AircraftType,
            ScenarioId = dto.ScenarioId,
            Cid = dto.Cid,
            AirportId = dto.AirportId,
            Note = dto.Note,
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
            HasBeenAirborne = dto.HasBeenAirborne,
            HasMadeInitialContact = dto.HasMadeInitialContact,
            AiInitialContactPositionIds = new SortedSet<string>(dto.AiInitialContactPositionIds ?? [], StringComparer.Ordinal),
            HasControllerAcknowledgedInitialContact = dto.HasControllerAcknowledgedInitialContact,
            HasLeftStudentFrequency = dto.HasLeftStudentFrequency,
            SpawnedAtSeconds = dto.SpawnedAtSeconds,
            IsGeneratorArrival = dto.IsGeneratorArrival,
            IsGeneratedOverflight = dto.IsGeneratedOverflight,
            OverflightExitDistanceNm = dto.OverflightExitDistanceNm,
            CompletedAtSeconds = dto.CompletedAtSeconds,
            CompletionReason = (CompletionReason)dto.CompletionReasonValue,
            CompletionDetail = dto.CompletionDetail,
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
            DataBlock = dto.DataBlock is not null ? AircraftDataBlock.FromSnapshot(dto.DataBlock) : new(),
            MilitaryRoute = dto.MilitaryRoute is not null ? AircraftMilitaryRoute.FromSnapshot(dto.MilitaryRoute) : new(),
            LiveTraffic = dto.LiveTraffic is not null ? AircraftLiveTraffic.FromSnapshot(dto.LiveTraffic) : null,
            Queue = CommandQueue.FromSnapshot(dto.Queue),
            Phases = dto.Phases is not null ? PhaseList.FromSnapshot(dto.Phases, groundLayout) : null,
            ActiveApproachScore = dto.ActiveApproachScore is not null ? ApproachScore.FromSnapshot(dto.ActiveApproachScore) : null,
        };

        ac.WindComponents = (dto.WindN, dto.WindE);
        ControlTargets.RestoreFrom(dto.Targets, ac.Targets);

        // Re-link every restored HoldingShortPhase to the taxi route's own HoldShortPoint. Each side round-trips
        // independently, so without this the phase holds a detached copy: it loses TailOverRunwayNodeId (which CLRWY
        // gates on, so the command would be refused for the rest of the session) and its writes — clearing the
        // hold-short — would no longer reach the route the rest of the sim reads.
        if (ac.Phases is { } phases && ac.Ground.AssignedTaxiRoute is { } taxiRoute)
        {
            foreach (var phase in phases.Phases)
            {
                if (
                    phase is Phases.Ground.HoldingShortPhase holdingShort
                    && taxiRoute.GetHoldShortAt(holdingShort.HoldShort.NodeId) is { } routeHoldShort
                )
                {
                    holdingShort.RebindHoldShort(routeHoldShort);
                }
            }
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
            AirportId = AirportId,
            Note = Note,
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
            HasBeenAirborne = HasBeenAirborne,
            HasMadeInitialContact = HasMadeInitialContact,
            AiInitialContactPositionIds = AiInitialContactPositionIds.Count == 0 ? null : AiInitialContactPositionIds.ToList(),
            HasControllerAcknowledgedInitialContact = HasControllerAcknowledgedInitialContact,
            HasLeftStudentFrequency = HasLeftStudentFrequency,
            SpawnedAtSeconds = SpawnedAtSeconds,
            IsGeneratorArrival = IsGeneratorArrival,
            IsGeneratedOverflight = IsGeneratedOverflight,
            OverflightExitDistanceNm = OverflightExitDistanceNm,
            CompletedAtSeconds = CompletedAtSeconds,
            CompletionReasonValue = (int)CompletionReason,
            CompletionDetail = CompletionDetail,
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
            DataBlock = DataBlock.ToSnapshot(),
            MilitaryRoute = MilitaryRoute.ToSnapshot(),
            LiveTraffic = LiveTraffic?.ToSnapshot(),
            PositionHistory = PositionHistory.Count > 0 ? PositionHistory.Select(p => new PositionDto { Lat = p.Lat, Lon = p.Lon }).ToList() : null,
            ActiveApproachScore = ActiveApproachScore?.ToSnapshot(),
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
