using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Client.Models;

public partial class AircraftModel : ObservableObject
{
    [ObservableProperty]
    private string _callsign = "";

    /// <summary>
    /// Actual aircraft type — what's physically flying. Read by Tower Cab (out-the-window
    /// datablock), physics/performance lookups, and the operator-facing Aircraft List data
    /// grid (including its right-click menu header). Fixed at spawn and never changed by FP
    /// amendments. Flight-plan-bound surfaces (ASDE-X, FP Editor, flight strips) read
    /// <see cref="FiledAircraftType"/> directly; radar surfaces (STARS-style datablock,
    /// EuroScope tag, radar right-click menu) read <see cref="DisplayAircraftType"/>, which
    /// falls back to this physical value when the filed type is blank.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaseAircraftType))]
    [NotifyPropertyChangedFor(nameof(AircraftTypeName))]
    [NotifyPropertyChangedFor(nameof(DisplayAircraftType))]
    private string _aircraftType = "";

    /// <summary>
    /// Bare ICAO designator with wake-turbulence prefix and equipment suffix stripped
    /// (e.g. "H/B763/L" → "B763", "B738/L" → "B738"). Mirrors
    /// <see cref="Yaat.Sim.AircraftState.BaseAircraftType"/> for the client-side view.
    /// </summary>
    public string BaseAircraftType => AircraftState.StripTypePrefix(AircraftType);

    /// <summary>
    /// Human-readable display name for the bare ICAO designator (e.g. "Cessna Skyhawk 172/Cutlass"
    /// for C172, "Boeing 737-800" for B738). Looked up via <see cref="FaaAircraftDatabase.Get"/>
    /// (which handles wake-prefix stripping, equipment-suffix stripping, and sibling-map
    /// fallback) and then through the curated <see cref="AircraftDisplayNames"/> table seeded
    /// from FAA ACD. Empty when no entry is found.
    /// </summary>
    public string AircraftTypeName
    {
        get
        {
            var record = FaaAircraftDatabase.Get(AircraftType);
            if (record is not null && AircraftDisplayNames.TryGet(record.IcaoCode, out var name))
            {
                return name;
            }
            // No sibling/database hit — fall back to a direct lookup on the bare ICAO.
            return AircraftDisplayNames.TryGet(BaseAircraftType, out var direct) ? direct : "";
        }
    }

    /// <summary>
    /// Filed aircraft type — what the flight plan currently records. Read by ASDE-X, the
    /// Flight Plan Editor, and flight strips. Mutated by FP amendments and may be empty when
    /// an instructor blanks the field. Tower Cab and the Aircraft List data grid are driven
    /// by <see cref="AircraftType"/> instead so blanking the FP type does not blank either
    /// the tower's "out the window" view or the operator's situational awareness grid. The
    /// radar surfaces consume <see cref="DisplayAircraftType"/>, which prefers this filed
    /// value but falls back to <see cref="AircraftType"/> when it is blank — YAAT is an RPO
    /// tool and the radar must never hide a physically-known aircraft type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(DisplayAircraftType))]
    private string _filedAircraftType = "";

    /// <summary>
    /// Aircraft type as the radar surfaces (STARS-style datablock, EuroScope tag, radar
    /// right-click menu header) should display it: prefers <see cref="FiledAircraftType"/>
    /// when set, falls back to <see cref="AircraftType"/> otherwise. Keeps the type visible
    /// on the radar even when the filed FP type was never set or got blanked by an amendment.
    /// </summary>
    public string DisplayAircraftType => string.IsNullOrWhiteSpace(FiledAircraftType) ? AircraftType : FiledAircraftType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingDisplay))]
    private LatLon _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingDisplay))]
    private TrueHeading _heading;

    /// <summary>Live heading in magnetic, 3-digit zero-padded for display (matches the assigned-heading frame).</summary>
    public string HeadingDisplay => Heading.ToMagnetic(MagneticDeclination.GetDeclination(Position)).ToDisplayString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MachDisplay))]
    private double _altitude;

    [ObservableProperty]
    private double _groundSpeed;

    [ObservableProperty]
    private uint _beaconCode;

    [ObservableProperty]
    private uint _assignedBeaconCode;

    [ObservableProperty]
    private string _transponderMode = "C";

    [ObservableProperty]
    private double _verticalSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private MagneticHeading? _assignedHeading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private string _navigatingTo = "";

    public string AssignedHeadingDisplay =>
        !string.IsNullOrEmpty(NavigatingTo) ? NavigatingTo
        : AssignedHeading.HasValue ? AssignedHeading.Value.ToDisplayString()
        : "";

    [ObservableProperty]
    private double? _assignedAltitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedSpeedDisplay))]
    private double? _assignedSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(HasFlightPlan))]
    private string _departure = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(HasFlightPlan))]
    private string _destination = "";

    // ASDE-style surface fix (exit fix or destination per the airport's ASDE-X/SAID facility config),
    // resolved server-side. Shown on the Ground View datablock and the Aircraft List "Fix" column.
    [ObservableProperty]
    private string _asdexFix = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(HasFlightPlan))]
    [NotifyPropertyChangedFor(nameof(ShowNavRoute))]
    private string _route = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(CruiseAltitudeDisplay))]
    private string _flightRules = "IFR";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(IsDelayed))]
    private string _status = "";

    public string StatusDisplay => FormatStatus(Status);

    public bool IsDelayed => Status.StartsWith("Delayed", StringComparison.Ordinal);

    /// <summary>
    /// True for any aircraft whose STARS representation is a stationary ghost (no live
    /// surveillance motion). Set in two cases: (1) phantom data blocks created by CRC
    /// <c>DA</c>/<c>VP</c> typing for callsigns with no real aircraft body, and (2) ghost
    /// overlays attached to real scenario aircraft via AID+slew. The Aircraft List filter
    /// uses this together with <see cref="IsGhostOverlay"/> to hide only the phantoms.
    /// </summary>
    [ObservableProperty]
    private bool _isUnsupported;

    /// <summary>
    /// True when <see cref="IsUnsupported"/> is set because a ghost overlay was attached
    /// to an existing scenario aircraft (AID+slew). Distinguishes the overlay-on-real
    /// case from a pure phantom data block so the Aircraft List can keep the row visible.
    /// Clears when the ghost is dropped or auto-merges as the aircraft crosses the STARS
    /// floor.
    /// </summary>
    [ObservableProperty]
    private bool _isGhostOverlay;

    /// <summary>
    /// True while the displayed altitude rounds to 000 — the aircraft is below the acquisition
    /// floor (AGL &lt; 100 ft, field-elevation adjusted server-side). The radar withholds the target
    /// while set (matching CRC STARS coast/skip); the ground view keeps a ghost overlay visible
    /// across liftoff until this clears.
    /// </summary>
    [ObservableProperty]
    private bool _belowDisplayFloor;

    /// <summary>
    /// True while the aircraft is established on an approach procedure being descended below the MVA
    /// (on final/glideslope, or cleared for a full approach and flying it). Computed server-side via
    /// <c>PhaseList.IsEstablishedOnApproach()</c>; the radar uses it to inhibit the MVA altitude tint
    /// because the procedure provides its own obstacle clearance.
    /// </summary>
    [ObservableProperty]
    private bool _isEstablishedOnApproach;

    private static string FormatStatus(string status)
    {
        if (status.StartsWith("Delayed (", StringComparison.Ordinal) && status.EndsWith("s)", StringComparison.Ordinal))
        {
            var numStr = status.AsSpan(9, status.Length - 11);
            if (int.TryParse(numStr, out var seconds))
            {
                var minutes = seconds / 60;
                var secs = seconds % 60;
                return $"Delayed {minutes}:{secs:D2}";
            }
        }
        return status;
    }

    [ObservableProperty]
    private string _pendingCommands = "";

    [ObservableProperty]
    private string _currentPhase = "";

    [ObservableProperty]
    private string _assignedRunway = "";

    [ObservableProperty]
    private bool _isOnGround;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseSequenceDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPhases))]
    private string _phaseSequence = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseSequenceDisplay))]
    private int _activePhaseIndex = -1;

    /// <summary>
    /// When true, the scenario has auto-CTL enabled (e.g. GND/APP/CTR positions),
    /// so missing landing clearance should not trigger alerts.
    /// </summary>
    public bool IsAutoClearedToLand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    [NotifyPropertyChangedFor(nameof(ClearanceShorthand))]
    [NotifyPropertyChangedFor(nameof(HasClearance))]
    private string _landingClearance = "";

    /// <summary>
    /// True while the "approaching final without a landing clearance" warning is active for
    /// this aircraft on the current final approach. Drives the flashing red "NoLndgClnc"
    /// datablock line on the radar. Updated by <c>FinalApproachPhase</c> in the sim — cleared
    /// when a landing clearance is granted or the aircraft leaves FinalApproach.
    /// </summary>
    [ObservableProperty]
    private bool _noLandingClearanceWarningActive;

    /// <summary>
    /// True when a queued <c>ONHS DEL</c> block is armed on this aircraft (or the
    /// per-aircraft <c>PendingAutoDelete</c> flag has been raised). Drives a small
    /// marker in the radar / tower-cab datablock so controllers can see at a glance
    /// which landing aircraft are pre-marked for auto-removal. Cleared by <c>NODEL</c>
    /// and by the auto-delete sweep itself (the aircraft is removed shortly after).
    /// </summary>
    [ObservableProperty]
    private bool _autoDeletePending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    [NotifyPropertyChangedFor(nameof(ClearanceShorthand))]
    private string _clearedRunway = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPattern))]
    private string _patternDirection = "";

    /// <summary>
    /// When CurrentPhase is "Pattern Entry", classifies how the aircraft is
    /// joining the pattern (Direct / FortyFive / Crosswind / Upwind / Base / Final).
    /// Null when not in a pattern-entry phase.
    /// </summary>
    [ObservableProperty]
    private string? _patternEntryKind;

    /// <summary>
    /// When set, the callsign this aircraft is following (VFR follow / visual
    /// with follow-traffic). Null when not following.
    /// </summary>
    [ObservableProperty]
    private string? _followingCallsign;

    /// <summary>
    /// Callsign of the traffic this aircraft most recently reported in sight (via
    /// RTIS/RTISF). Null until a successful report. Used to gate the "follow this
    /// traffic" context-menu item to traffic the aircraft can actually see.
    /// </summary>
    [ObservableProperty]
    private string? _lastReportedTrafficCallsign;

    /// <summary>
    /// When in <c>Runway Exit</c> or <c>Holding After Exit</c>, the runway being
    /// exited. Distinct from <see cref="AssignedRunway"/> which can change once
    /// the aircraft accepts a taxi clearance. Null otherwise.
    /// </summary>
    [ObservableProperty]
    private string? _exitingRunwayId;

    /// <summary>
    /// When in <c>Crossing Runway</c>, the runway currently being crossed (e.g. "28R/10L").
    /// Distinct from <see cref="AssignedRunway"/>, which holds the aircraft's departure /
    /// destination runway — those differ when an aircraft taxis across one runway to reach
    /// a different departure runway. Null otherwise (or for snapshots pre-dating this field).
    /// </summary>
    [ObservableProperty]
    private string? _crossingRunwayId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNavigationRoute))]
    [NotifyPropertyChangedFor(nameof(ShowNavRoute))]
    [NotifyPropertyChangedFor(nameof(NavigationRouteDisplay))]
    private List<string> _navigationRoute = [];

    /// <summary>
    /// Full flown lateral route as projected by the server: each fix's geographic position (arc
    /// vertices, custom fixes, and FRD fixes included) plus any per-fix crossing-restriction label
    /// lines. Drives the radar "Show nav route" overlay so it draws the exact path the aircraft flies.
    /// Synthetic arc vertices carry an empty <see cref="NavRouteFixDto.Name"/>; <see cref="NavigationRoute"/>
    /// is the human-readable fix-name list derived from this (arc vertices filtered out).
    /// </summary>
    public List<NavRouteFixDto> NavRouteFixes { get; set; } = [];

    private static List<string> DeriveFixNames(List<NavRouteFixDto>? fixes) =>
        fixes is null ? [] : fixes.Where(f => f.Name.Length > 0).Select(f => f.Name).ToList();

    [ObservableProperty]
    private string _equipmentSuffix = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(HasFlightPlan))]
    [NotifyPropertyChangedFor(nameof(ShowNavRoute))]
    private string _activeSidId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    [NotifyPropertyChangedFor(nameof(HasFlightPlan))]
    [NotifyPropertyChangedFor(nameof(ShowNavRoute))]
    private string _activeStarId = "";

    [ObservableProperty]
    private string _departureRunway = "";

    [ObservableProperty]
    private string _destinationRunway = "";

    [ObservableProperty]
    private double _indicatedAirspeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MachDisplay))]
    private double _mach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedSpeedDisplay))]
    private double? _assignedMach;

    [ObservableProperty]
    private string _wind = "";

    public string MachDisplay => Altitude >= 24000 && Mach >= 0.01 ? $"M.{Mach * 100:F0}" : "";

    public string AssignedSpeedDisplay =>
        AssignedMach.HasValue ? $"M.{AssignedMach.Value * 100:F0}"
        : AssignedSpeed.HasValue ? AssignedSpeed.Value.ToString("F0")
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseDisplay))]
    [NotifyPropertyChangedFor(nameof(CruiseAltitudeDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCruise))]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    private int _cruiseAltitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseAltitudeDisplay))]
    private int? _blockFloorAltitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseAltitudeDisplay))]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    private bool _isVfrOnTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseAltitudeDisplay))]
    private bool _isAbove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseDisplay))]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    private int _cruiseSpeed;

    public bool HasPhases => !string.IsNullOrEmpty(PhaseSequence);

    public string PhaseSequenceDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(PhaseSequence))
            {
                return "";
            }

            var parts = PhaseSequence.Split(" > ");
            if (ActivePhaseIndex < 0 || ActivePhaseIndex >= parts.Length)
            {
                return PhaseSequence;
            }

            // The server sends only non-completed phases, so index 0
            // in the split array is the active phase.
            parts[0] = $"[{parts[0]}]";
            return string.Join(" > ", parts);
        }
    }

    public bool HasClearance => !string.IsNullOrEmpty(LandingClearance);

    public string ClearanceDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(LandingClearance))
            {
                return "";
            }

            var humanized = LandingClearance switch
            {
                "ClearedToLand" => "Cleared to land",
                "ClearedForOption" => "Cleared for the option",
                "ClearedTouchAndGo" => "Cleared touch and go",
                "ClearedStopAndGo" => "Cleared stop and go",
                "LineUpAndWait" => "Line up and wait",
                "ClearedForTakeoff" => "Cleared for takeoff",
                _ => LandingClearance,
            };

            if (!string.IsNullOrEmpty(ClearedRunway))
            {
                return $"{humanized} Rwy {RunwayIdentifier.ToDisplayDesignator(ClearedRunway)}";
            }
            return humanized;
        }
    }

    public string ClearanceShorthand
    {
        get
        {
            if (string.IsNullOrEmpty(LandingClearance))
            {
                return "";
            }

            var shorthand = LandingClearance switch
            {
                "ClearedToLand" => "CLAND",
                "ClearedForOption" => "COPT",
                "ClearedTouchAndGo" => "TG",
                "ClearedStopAndGo" => "SG",
                "ClearedLowApproach" => "LA",
                "LineUpAndWait" => "LUAW",
                "ClearedForTakeoff" => "CTO",
                _ => LandingClearance,
            };

            return !string.IsNullOrEmpty(ClearedRunway) ? $"{shorthand} {RunwayIdentifier.ToDisplayDesignator(ClearedRunway)}" : shorthand;
        }
    }

    public bool HasPattern => !string.IsNullOrEmpty(PatternDirection);

    public string PatternDisplay => string.IsNullOrEmpty(PatternDirection) ? "" : $"{PatternDirection} traffic";

    public bool HasNavigationRoute => NavigationRoute.Count > 0;

    public string NavigationRouteDisplay => string.Join(" ", NavigationRoute);

    public bool HasFlightPlan => !string.IsNullOrEmpty(Route) || !string.IsNullOrEmpty(Departure) || !string.IsNullOrEmpty(Destination);

    public string FlightPlanDisplay
    {
        get
        {
            var parts = new List<string>(4);

            if (!string.IsNullOrEmpty(FlightRules) || !string.IsNullOrEmpty(FiledAircraftType))
            {
                var rulesLabel = IsVfrOnTop ? "OTP" : FlightRules;
                parts.Add($"{rulesLabel} {FiledAircraftType}".Trim());
            }

            if (!string.IsNullOrEmpty(Departure) || !string.IsNullOrEmpty(Destination))
            {
                parts.Add($"{Departure}-{Destination}");
            }

            if (CruiseAltitude > 0)
            {
                var altStr = CruiseAltitude >= 18000 ? $"FL{CruiseAltitude / 100}" : $"{CruiseAltitude}";
                parts.Add(CruiseSpeed > 0 ? $"{altStr}/{CruiseSpeed}kt" : altStr);
            }

            if (!string.IsNullOrEmpty(ActiveSidId))
            {
                parts.Add($"SID:{ActiveSidId}");
            }
            if (!string.IsNullOrEmpty(ActiveStarId))
            {
                parts.Add($"STAR:{ActiveStarId}");
            }

            var header = string.Join("  ", parts);
            return string.IsNullOrEmpty(Route) ? header : $"{header}\n{Route}";
        }
    }

    public bool ShowNavRoute => HasNavigationRoute && !IsNavRouteOnFiledRoute();

    private bool IsNavRouteOnFiledRoute()
    {
        if (NavigationRoute.Count == 0 || string.IsNullOrEmpty(Route))
        {
            return false;
        }

        var routeFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Route.Split(['.', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && "VJTQ".Contains(token[0]) && char.IsDigit(token[1]))
            {
                continue;
            }
            routeFixes.Add(token);
        }

        foreach (var fix in NavigationRoute)
        {
            if (!routeFixes.Contains(fix))
            {
                return false;
            }
        }
        return true;
    }

    public bool HasCruise => CruiseAltitude > 0;

    public string CruiseDisplay
    {
        get
        {
            if (CruiseAltitude <= 0)
            {
                return "";
            }

            var altStr = CruiseAltitude >= 18000 ? $"FL{CruiseAltitude / 100}" : $"{CruiseAltitude}";

            if (CruiseSpeed > 0)
            {
                return $"{altStr} / {CruiseSpeed} kt";
            }
            return altStr;
        }
    }

    /// <summary>Reconstructs the filed <see cref="PlannedAltitude"/> from the flattened DTO notation fields.</summary>
    private PlannedAltitude AsPlannedAltitude
    {
        get
        {
            if (BlockFloorAltitude is { } floor)
            {
                return PlannedAltitude.Block(floor, CruiseAltitude);
            }
            var alt = CruiseAltitude > 0 ? CruiseAltitude : (int?)null;
            if (IsAbove)
            {
                return PlannedAltitude.Above(CruiseAltitude);
            }
            if (IsVfrOnTop)
            {
                return PlannedAltitude.Otp(alt);
            }
            if (FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
            {
                return PlannedAltitude.Vfr(alt);
            }
            return alt is { } feet ? PlannedAltitude.Ifr(feet) : PlannedAltitude.None;
        }
    }

    public string CruiseAltitudeDisplay => FlightPlanAltitude.Format(AsPlannedAltitude);

    internal static (string Rules, PlannedAltitude Altitude)? ParseAltitudeField(string text) => FlightPlanAltitude.Parse(text);

    [ObservableProperty]
    private string _taxiRoute = "";

    /// <summary>
    /// True when the aircraft has an assigned taxi route with segments still ahead
    /// of it. Drives whether "Resume taxi" appears in the context menu — RES only
    /// resumes a paused route, so we hide it when there's nothing to resume.
    /// </summary>
    [ObservableProperty]
    private bool _hasActiveTaxiRoute;

    /// <summary>
    /// Kind of active hold: <c>"HoldPosition"</c> for unconditional stop, <c>"GiveWay"</c>
    /// for a controller-issued GIVEWAY relationship, or null when free to move. Mirrored
    /// from <c>AircraftGroundOps.Hold</c> on the server. Drives the ground datablock
    /// status suffix and the right-click menu's hold header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldStatusDisplay))]
    [NotifyPropertyChangedFor(nameof(IsHeld))]
    private string? _holdKind;

    /// <summary>
    /// Yield target callsign when <see cref="HoldKind"/> is <c>"GiveWay"</c>. Null
    /// for HoldPosition or no hold.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldStatusDisplay))]
    private string? _holdYieldTarget;

    /// <summary>
    /// Callsign this aircraft is auto-yielding to, set by the ground conflict detector for
    /// converging / same-edge-trailing pairs. Display-only — the aircraft is slowing, not held,
    /// so it does NOT set <see cref="IsHeld"/>. Drives the "→{target} (auto)" badge.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldStatusDisplay))]
    private string? _autoYieldTarget;

    /// <summary>
    /// True when <see cref="AutoYieldTarget"/> is a same-edge in-trail follow ("Following") rather
    /// than a converging give-way ("Yielding to"). Drives the right-click menu wording.
    /// </summary>
    [ObservableProperty]
    private bool _autoYieldIsFollowing;

    /// <summary>
    /// True when the aircraft is under any active hold directive (HoldPosition or
    /// GiveWay). Driven by <see cref="HoldKind"/>. Auto-yield (slowing) is NOT a hold.
    /// </summary>
    public bool IsHeld => !string.IsNullOrEmpty(HoldKind);

    /// <summary>
    /// Compact status string for the ground datablock suffix and the right-click menu header.
    /// A controller hold takes precedence; an auto-detected yield shows "→{target} (auto)".
    /// Empty when there is neither.
    /// </summary>
    public string HoldStatusDisplay =>
        HoldKind switch
        {
            "GiveWay" when !string.IsNullOrEmpty(HoldYieldTarget) => $"→{HoldYieldTarget}",
            "HoldPosition" => "HOLD",
            _ when !string.IsNullOrEmpty(AutoYieldTarget) => $"→{AutoYieldTarget} (auto)",
            _ => string.Empty,
        };

    [ObservableProperty]
    private string _parkingSpot = "";

    [ObservableProperty]
    private string _currentTaxiway = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerDisplay))]
    private string? _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnerDisplay))]
    private string? _ownerSectorCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HandoffDisplay))]
    private string? _handoffPeer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HandoffDisplay))]
    private string? _handoffPeerSectorCode;

    [ObservableProperty]
    private string? _pointoutStatus;

    [ObservableProperty]
    private string? _pointoutToTcpCode;

    [ObservableProperty]
    private string? _scratchpad1;

    [ObservableProperty]
    private string? _scratchpad2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempAltDisplay))]
    private int? _temporaryAltitude;

    [ObservableProperty]
    private bool _isAnnotated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpectedApproachDisplay))]
    private string? _activeApproachId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpectedApproachDisplay))]
    private string? _expectedApproach;

    public string ExpectedApproachDisplay => string.IsNullOrEmpty(ActiveApproachId) ? ExpectedApproach ?? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemarks))]
    private string _remarks = "";

    public bool HasRemarks => !string.IsNullOrEmpty(Remarks);

    /// <summary>Instructor freetext note, shown as an extra datablock line on radar + ground.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = "";

    public bool HasNote => !string.IsNullOrEmpty(Note);

    [ObservableProperty]
    private string _cwtCode = "";

    // Student-scope STARS view projected by the server (StarsDatablockClassifier). Null when the
    // scenario has no student position. Read by the radar renderer to optionally color the datablock,
    // mark/collapse limited blocks, and orient the leader line to match the student's scope.
    [ObservableProperty]
    private Yaat.Sim.StarsDatablockColor? _studentDatablockColor;

    [ObservableProperty]
    private Yaat.Sim.StarsDatablockLevel? _studentDatablockLevel;

    [ObservableProperty]
    private int? _studentLeaderDirection;

    /// <summary>Instructor TPA overlay on YAAT's radar (emulates STARS *J/*P): 0=None, 1=J-Ring, 2=Cone.</summary>
    [ObservableProperty]
    private int _tpaType;

    /// <summary>TPA J-Ring radius / Cone length in nm (only meaningful when <see cref="TpaType"/> != 0).</summary>
    [ObservableProperty]
    private double _tpaSize;

    public string OwnerDisplay => OwnerSectorCode ?? Owner ?? "";

    public string HandoffDisplay => HandoffPeerSectorCode ?? HandoffPeer ?? "";

    public string TempAltDisplay =>
        TemporaryAltitude.HasValue ? (TemporaryAltitude.Value >= 180 ? $"FL{TemporaryAltitude.Value}" : $"{TemporaryAltitude.Value}") : "";

    public IReadOnlyList<double[]>? PositionHistory { get; set; }

    [ObservableProperty]
    private string? _assignedTo;

    [ObservableProperty]
    private double? _distanceFromFix;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _smartStatus = "";

    [ObservableProperty]
    private AircraftStatusSeverity _smartStatusSeverity = AircraftStatusSeverity.Normal;

    // Live CFR release-window badge shown as a prefix in the Aircraft List Info column.
    [ObservableProperty]
    private string _cfrBadge = "";

    [ObservableProperty]
    private bool _hasCfrBadge;

    [ObservableProperty]
    private bool _cfrBadgeExpired;

    /// <summary>
    /// Opt-in transient overlay populated by <see cref="ViewModels.MainViewModel"/> when a
    /// SAY-family or RPO pilot-speech terminal entry arrives. The radar / ground renderers
    /// draw this near the aircraft and drop it once <c>ExpiresAt</c> has passed. Never set
    /// from <see cref="AircraftDto"/> — purely client-side.
    /// </summary>
    [ObservableProperty]
    private AircraftSpeechBubble? _speechBubble;

    /// <summary>
    /// Airport id of the ground layout this aircraft is currently on (server-side
    /// <c>AircraftGroundOps.LayoutAirportId</c>, with the scenario primary airport as a fallback).
    /// Null when airborne / unknown. Read by the radar to decide whether a ground aircraft's
    /// speech bubble needs surfacing because no ground view is currently showing that airport.
    /// </summary>
    public string? GroundAirportId { get; set; }

    /// <summary>
    /// True when this ground departure is held for release (hold-for-release armed at its airport).
    /// Drives the radar "Release (HFR)" context-menu item and the held datablock badge.
    /// </summary>
    public bool IsHeldForRelease { get; set; }

    /// <summary>
    /// Absolute-UTC bounds of this aircraft's Call-For-Release window (CFR), or null. Evaluated
    /// against real UTC by the MainViewModel to raise instructor-facing expiry alerts (GitHub issue #230).
    /// </summary>
    public DateTime? CfrWindowStartUtc { get; set; }

    public DateTime? CfrWindowEndUtc { get; set; }

    /// <summary>
    /// Recomputes the live CFR release-window badge (Info column) from the window vs <paramref name="nowUtc"/>:
    /// <c>CFR opens M:SS</c> before it opens, <c>CFR M:SS</c> while open, <c>CFR EXP</c> (red) past close.
    /// Shown only while on the ground — it drops at wheels-up. Driven by the view-model's 1 s sweep.
    /// </summary>
    public void UpdateCfrBadge(DateTime nowUtc)
    {
        if (!IsOnGround || CfrWindowStartUtc is not { } start || CfrWindowEndUtc is not { } end)
        {
            CfrBadge = "";
            HasCfrBadge = false;
            CfrBadgeExpired = false;
            return;
        }

        var remaining = CfrCountdown.Evaluate(new ReleaseWindow(start, end), nowUtc);
        (CfrBadge, CfrBadgeExpired) = remaining.Phase switch
        {
            CfrPhase.BeforeOpen => ($"CFR opens {FormatMinSec(remaining.Seconds)}", false),
            CfrPhase.Open => ($"CFR {FormatMinSec(remaining.Seconds)}", false),
            _ => ("CFR EXP", true),
        };
        HasCfrBadge = true;
    }

    private static string FormatMinSec(int seconds) => $"{seconds / 60}:{seconds % 60:D2}";

    public static AircraftModel FromDto(AircraftDto dto, Func<AircraftModel, double?>? computeDistance = null)
    {
        var model = new AircraftModel
        {
            Callsign = dto.Callsign,
            AircraftType = dto.AircraftType,
            FiledAircraftType = dto.FiledAircraftType,
            Position = new LatLon(dto.Latitude, dto.Longitude),
            Heading = new TrueHeading(dto.Heading),
            Altitude = dto.Altitude,
            GroundSpeed = dto.GroundSpeed,
            BeaconCode = dto.BeaconCode,
            AssignedBeaconCode = dto.AssignedBeaconCode,
            TransponderMode = dto.TransponderMode,
            VerticalSpeed = dto.VerticalSpeed,
            AssignedHeading = dto.AssignedHeading.HasValue ? new MagneticHeading(dto.AssignedHeading.Value) : null,
            NavigatingTo = dto.NavigatingTo,
            AssignedAltitude = dto.AssignedAltitude,
            AssignedSpeed = dto.AssignedSpeed,
            Departure = dto.Departure,
            Destination = dto.Destination,
            AsdexFix = dto.AsdexFix,
            Route = dto.Route,
            Remarks = dto.Remarks,
            Note = dto.Note,
            FlightRules = dto.FlightRules,
            Status = dto.Status,
            PendingCommands = dto.PendingCommands,
            CurrentPhase = dto.CurrentPhase,
            AssignedRunway = dto.AssignedRunway,
            IsOnGround = dto.IsOnGround,
            GroundAirportId = dto.GroundAirportId,
            IsHeldForRelease = dto.HeldForRelease,
            CfrWindowStartUtc = dto.CfrWindowStartUtc,
            CfrWindowEndUtc = dto.CfrWindowEndUtc,
            PhaseSequence = dto.PhaseSequence,
            ActivePhaseIndex = dto.ActivePhaseIndex,
            LandingClearance = dto.LandingClearance,
            NoLandingClearanceWarningActive = dto.NoLandingClearanceWarningActive,
            AutoDeletePending = dto.AutoDeletePending,
            ClearedRunway = dto.ClearedRunway,
            PatternDirection = dto.PatternDirection,
            NavigationRoute = DeriveFixNames(dto.NavigationRoute),
            EquipmentSuffix = dto.EquipmentSuffix,
            CruiseAltitude = dto.CruiseAltitude,
            BlockFloorAltitude = dto.BlockFloorAltitude,
            IsVfrOnTop = dto.IsVfrOnTop,
            IsAbove = dto.IsAbove,
            CruiseSpeed = dto.CruiseSpeed,
            TaxiRoute = dto.TaxiRoute,
            HasActiveTaxiRoute = dto.HasActiveTaxiRoute,
            HoldKind = dto.HoldKind,
            HoldYieldTarget = dto.HoldYieldTarget,
            AutoYieldTarget = dto.AutoYieldTarget,
            AutoYieldIsFollowing = dto.AutoYieldIsFollowing,
            ParkingSpot = dto.ParkingSpot,
            CurrentTaxiway = dto.CurrentTaxiway,
            Owner = dto.Owner,
            OwnerSectorCode = dto.OwnerSectorCode,
            HandoffPeer = dto.HandoffPeer,
            HandoffPeerSectorCode = dto.HandoffPeerSectorCode,
            PointoutStatus = dto.PointoutStatus,
            PointoutToTcpCode = dto.PointoutToTcpCode,
            Scratchpad1 = dto.Scratchpad1,
            Scratchpad2 = dto.Scratchpad2,
            TemporaryAltitude = dto.TemporaryAltitude,
            IsAnnotated = dto.IsAnnotated,
            ActiveApproachId = dto.ActiveApproachId,
            ExpectedApproach = dto.ExpectedApproach,
            CwtCode = dto.CwtCode,
            StudentDatablockColor = dto.StudentDatablockColor,
            StudentDatablockLevel = dto.StudentDatablockLevel,
            StudentLeaderDirection = dto.StudentLeaderDirection,
            TpaType = dto.TpaType,
            TpaSize = dto.TpaSize,
            ActiveSidId = dto.ActiveSidId,
            ActiveStarId = dto.ActiveStarId,
            DepartureRunway = dto.DepartureRunway,
            DestinationRunway = dto.DestinationRunway,
            IndicatedAirspeed = dto.IndicatedAirspeed,
            Mach = dto.Mach,
            AssignedMach = dto.AssignedMach,
            Wind = dto.WindSpeed > 0 ? $"{dto.WindDirection:D3}{dto.WindSpeed:D2}KT" : "",
            PositionHistory = dto.PositionHistory,
            PatternEntryKind = dto.PatternEntryKind,
            FollowingCallsign = dto.FollowingCallsign,
            LastReportedTrafficCallsign = dto.LastReportedTrafficCallsign,
            ExitingRunwayId = dto.ExitingRunwayId,
            CrossingRunwayId = dto.CrossingRunwayId,
            IsUnsupported = dto.IsUnsupported,
            IsGhostOverlay = dto.IsGhostOverlay,
            BelowDisplayFloor = dto.BelowDisplayFloor,
            IsEstablishedOnApproach = dto.IsEstablishedOnApproach,
        };
        model.NavRouteFixes = dto.NavigationRoute ?? [];
        model.DistanceFromFix = computeDistance?.Invoke(model);
        model.SmartStatus = dto.SmartStatus;
        model.SmartStatusSeverity = dto.SmartStatusSeverity;
        return model;
    }

    public void UpdateFromDto(AircraftDto dto, Func<AircraftModel, double?>? computeDistance = null)
    {
        FiledAircraftType = dto.FiledAircraftType;
        Position = new LatLon(dto.Latitude, dto.Longitude);
        Heading = new TrueHeading(dto.Heading);
        Altitude = dto.Altitude;
        GroundSpeed = dto.GroundSpeed;
        BeaconCode = dto.BeaconCode;
        AssignedBeaconCode = dto.AssignedBeaconCode;
        TransponderMode = dto.TransponderMode;
        VerticalSpeed = dto.VerticalSpeed;
        AssignedHeading = dto.AssignedHeading.HasValue ? new MagneticHeading(dto.AssignedHeading.Value) : null;
        NavigatingTo = dto.NavigatingTo;
        AssignedAltitude = dto.AssignedAltitude;
        AssignedSpeed = dto.AssignedSpeed;
        Departure = dto.Departure;
        Destination = dto.Destination;
        AsdexFix = dto.AsdexFix;
        Route = dto.Route;
        Remarks = dto.Remarks;
        Note = dto.Note;
        FlightRules = dto.FlightRules;
        Status = dto.Status;
        PendingCommands = dto.PendingCommands;
        CurrentPhase = dto.CurrentPhase;
        AssignedRunway = dto.AssignedRunway;
        IsOnGround = dto.IsOnGround;
        GroundAirportId = dto.GroundAirportId;
        IsHeldForRelease = dto.HeldForRelease;
        CfrWindowStartUtc = dto.CfrWindowStartUtc;
        CfrWindowEndUtc = dto.CfrWindowEndUtc;
        PhaseSequence = dto.PhaseSequence;
        ActivePhaseIndex = dto.ActivePhaseIndex;
        LandingClearance = dto.LandingClearance;
        NoLandingClearanceWarningActive = dto.NoLandingClearanceWarningActive;
        AutoDeletePending = dto.AutoDeletePending;
        ClearedRunway = dto.ClearedRunway;
        PatternDirection = dto.PatternDirection;
        NavRouteFixes = dto.NavigationRoute ?? [];
        NavigationRoute = DeriveFixNames(dto.NavigationRoute);
        EquipmentSuffix = dto.EquipmentSuffix;
        CruiseAltitude = dto.CruiseAltitude;
        BlockFloorAltitude = dto.BlockFloorAltitude;
        IsVfrOnTop = dto.IsVfrOnTop;
        IsAbove = dto.IsAbove;
        CruiseSpeed = dto.CruiseSpeed;
        TaxiRoute = dto.TaxiRoute;
        HasActiveTaxiRoute = dto.HasActiveTaxiRoute;
        HoldKind = dto.HoldKind;
        HoldYieldTarget = dto.HoldYieldTarget;
        AutoYieldTarget = dto.AutoYieldTarget;
        AutoYieldIsFollowing = dto.AutoYieldIsFollowing;
        ParkingSpot = dto.ParkingSpot;
        CurrentTaxiway = dto.CurrentTaxiway;
        Owner = dto.Owner;
        OwnerSectorCode = dto.OwnerSectorCode;
        HandoffPeer = dto.HandoffPeer;
        HandoffPeerSectorCode = dto.HandoffPeerSectorCode;
        PointoutStatus = dto.PointoutStatus;
        PointoutToTcpCode = dto.PointoutToTcpCode;
        Scratchpad1 = dto.Scratchpad1;
        Scratchpad2 = dto.Scratchpad2;
        TemporaryAltitude = dto.TemporaryAltitude;
        IsAnnotated = dto.IsAnnotated;
        ActiveApproachId = dto.ActiveApproachId;
        ExpectedApproach = dto.ExpectedApproach;
        CwtCode = dto.CwtCode;
        StudentDatablockColor = dto.StudentDatablockColor;
        StudentDatablockLevel = dto.StudentDatablockLevel;
        StudentLeaderDirection = dto.StudentLeaderDirection;
        TpaType = dto.TpaType;
        TpaSize = dto.TpaSize;
        ActiveSidId = dto.ActiveSidId;
        ActiveStarId = dto.ActiveStarId;
        DepartureRunway = dto.DepartureRunway;
        DestinationRunway = dto.DestinationRunway;
        IndicatedAirspeed = dto.IndicatedAirspeed;
        Mach = dto.Mach;
        AssignedMach = dto.AssignedMach;
        Wind = dto.WindSpeed > 0 ? $"{dto.WindDirection:D3}{dto.WindSpeed:D2}KT" : "";
        PositionHistory = dto.PositionHistory;
        PatternEntryKind = dto.PatternEntryKind;
        FollowingCallsign = dto.FollowingCallsign;
        LastReportedTrafficCallsign = dto.LastReportedTrafficCallsign;
        ExitingRunwayId = dto.ExitingRunwayId;
        CrossingRunwayId = dto.CrossingRunwayId;
        IsUnsupported = dto.IsUnsupported;
        IsGhostOverlay = dto.IsGhostOverlay;
        BelowDisplayFloor = dto.BelowDisplayFloor;
        IsEstablishedOnApproach = dto.IsEstablishedOnApproach;
        DistanceFromFix = computeDistance?.Invoke(this);
        SmartStatus = dto.SmartStatus;
        SmartStatusSeverity = dto.SmartStatusSeverity;
    }

    internal static (int Order, int Seconds) ParseStatusSortKey(string status)
    {
        if (status.StartsWith("Delayed (", StringComparison.Ordinal) && status.EndsWith("s)", StringComparison.Ordinal))
        {
            var numStr = status.AsSpan(9, status.Length - 11);
            if (int.TryParse(numStr, out var seconds))
            {
                return (1, seconds);
            }
        }
        return (0, 0); // Active
    }
}

public sealed class StatusSortComparer : IComparer
{
    public static readonly StatusSortComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is not AircraftModel a || y is not AircraftModel b)
        {
            return 0;
        }

        var ka = AircraftModel.ParseStatusSortKey(a.Status);
        var kb = AircraftModel.ParseStatusSortKey(b.Status);

        var orderCmp = ka.Order.CompareTo(kb.Order);
        if (orderCmp != 0)
        {
            return orderCmp;
        }

        return ka.Seconds.CompareTo(kb.Seconds);
    }
}

/// <summary>
/// Compares AircraftModel instances by a named property using reflection (cached delegate).
/// </summary>
public sealed class PropertySortComparer : IComparer
{
    private static readonly Dictionary<string, Func<AircraftModel, IComparable?>> _accessorCache = new();
    private readonly Func<AircraftModel, IComparable?> _accessor;

    public PropertySortComparer(string propertyName)
    {
        if (!_accessorCache.TryGetValue(propertyName, out var accessor))
        {
            var prop = typeof(AircraftModel).GetProperty(propertyName);
            if (prop is not null)
            {
                accessor = ac => prop.GetValue(ac) as IComparable;
            }
            else
            {
                accessor = _ => null;
            }
            _accessorCache[propertyName] = accessor;
        }
        _accessor = accessor;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not AircraftModel a || y is not AircraftModel b)
        {
            return 0;
        }

        var va = _accessor(a);
        var vb = _accessor(b);

        if (va is null && vb is null)
        {
            return 0;
        }
        if (va is null)
        {
            return -1;
        }
        if (vb is null)
        {
            return 1;
        }

        return va.CompareTo(vb);
    }
}

/// <summary>
/// Wraps any IComparer to always sort Active aircraft before Delayed,
/// then delegates to the inner comparer within each group.
/// </summary>
public sealed class GroupStableSortComparer : IComparer
{
    private readonly IComparer _inner;

    public GroupStableSortComparer(IComparer inner)
    {
        _inner = inner;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not AircraftModel a || y is not AircraftModel b)
        {
            return 0;
        }

        // Active (false=0) before Delayed (true=1)
        var groupCmp = a.IsDelayed.CompareTo(b.IsDelayed);
        if (groupCmp != 0)
        {
            return groupCmp;
        }

        return _inner.Compare(x, y);
    }
}
