using System;
using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.Models;

public enum SmartStatusSeverity
{
    Normal,
    Warning,
    Critical,
}

public partial class AircraftModel : ObservableObject
{
    [ObservableProperty]
    private string _callsign = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlightPlanDisplay))]
    private string _aircraftType = "";

    [ObservableProperty]
    private double _latitude;

    [ObservableProperty]
    private double _longitude;

    [ObservableProperty]
    private double _heading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MachDisplay))]
    private double _altitude;

    [ObservableProperty]
    private double _groundSpeed;

    [ObservableProperty]
    private uint _beaconCode;

    [ObservableProperty]
    private string _transponderMode = "C";

    [ObservableProperty]
    private double _verticalSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private double? _assignedHeading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private string _navigatingTo = "";

    public string AssignedHeadingDisplay =>
        !string.IsNullOrEmpty(NavigatingTo) ? NavigatingTo
        : AssignedHeading.HasValue ? AssignedHeading.Value.ToString("F0")
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    [NotifyPropertyChangedFor(nameof(ClearanceShorthand))]
    [NotifyPropertyChangedFor(nameof(HasClearance))]
    private string _landingClearance = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    [NotifyPropertyChangedFor(nameof(ClearanceShorthand))]
    private string _clearedRunway = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPattern))]
    private string _patternDirection = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNavigationRoute))]
    [NotifyPropertyChangedFor(nameof(ShowNavRoute))]
    private string _navigationRoute = "";

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
                return $"{humanized} Rwy {ClearedRunway}";
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

            return !string.IsNullOrEmpty(ClearedRunway) ? $"{shorthand} {ClearedRunway}" : shorthand;
        }
    }

    public bool HasPattern => !string.IsNullOrEmpty(PatternDirection);

    public string PatternDisplay => string.IsNullOrEmpty(PatternDirection) ? "" : $"{PatternDirection} traffic";

    public bool HasNavigationRoute => !string.IsNullOrEmpty(NavigationRoute);

    public bool HasFlightPlan => !string.IsNullOrEmpty(Route) || !string.IsNullOrEmpty(Departure) || !string.IsNullOrEmpty(Destination);

    public string FlightPlanDisplay
    {
        get
        {
            var parts = new List<string>(4);

            if (!string.IsNullOrEmpty(FlightRules) || !string.IsNullOrEmpty(AircraftType))
            {
                parts.Add($"{FlightRules} {AircraftType}".Trim());
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
        if (string.IsNullOrEmpty(NavigationRoute) || string.IsNullOrEmpty(Route))
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

        var navFixes = NavigationRoute.Split(" > ", StringSplitOptions.RemoveEmptyEntries);
        foreach (var fix in navFixes)
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

    public string CruiseAltitudeDisplay => FormatAltitudeField(FlightRules, CruiseAltitude);

    internal static string FormatAltitudeField(string flightRules, int cruiseAltitude)
    {
        var isVfr = flightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        var isOtp = flightRules.Equals("OTP", StringComparison.OrdinalIgnoreCase);
        var altStr = cruiseAltitude > 0 ? (cruiseAltitude / 100).ToString("D3") : "";

        if (isOtp)
        {
            return string.IsNullOrEmpty(altStr) ? "OTP" : $"OTP/{altStr}";
        }
        if (isVfr)
        {
            return string.IsNullOrEmpty(altStr) ? "VFR" : $"VFR/{altStr}";
        }
        return altStr;
    }

    internal static (string FlightRules, int CruiseAltitude)? ParseAltitudeField(string text)
    {
        text = text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text == "VFR")
        {
            return ("VFR", 0);
        }
        if (text == "OTP")
        {
            return ("OTP", 0);
        }
        if (text.StartsWith("VFR/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var vfrAlt))
        {
            return ("VFR", vfrAlt * 100);
        }
        if (text.StartsWith("OTP/", StringComparison.Ordinal) && int.TryParse(text.AsSpan(4), out var otpAlt))
        {
            return ("OTP", otpAlt * 100);
        }
        if (int.TryParse(text, out var alt))
        {
            return ("IFR", alt * 100);
        }
        return null;
    }

    [ObservableProperty]
    private string _taxiRoute = "";

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

    [ObservableProperty]
    private string _cwtCode = "";

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
    private SmartStatusSeverity _smartStatusSeverity = SmartStatusSeverity.Normal;

    public static AircraftModel FromDto(AircraftDto dto, Func<AircraftModel, double?>? computeDistance = null)
    {
        var model = new AircraftModel
        {
            Callsign = dto.Callsign,
            AircraftType = dto.AircraftType,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            Heading = dto.Heading,
            Altitude = dto.Altitude,
            GroundSpeed = dto.GroundSpeed,
            BeaconCode = dto.BeaconCode,
            TransponderMode = dto.TransponderMode,
            VerticalSpeed = dto.VerticalSpeed,
            AssignedHeading = dto.AssignedHeading,
            NavigatingTo = dto.NavigatingTo,
            AssignedAltitude = dto.AssignedAltitude,
            AssignedSpeed = dto.AssignedSpeed,
            Departure = dto.Departure,
            Destination = dto.Destination,
            Route = dto.Route,
            Remarks = dto.Remarks,
            FlightRules = dto.FlightRules,
            Status = dto.Status,
            PendingCommands = dto.PendingCommands,
            CurrentPhase = dto.CurrentPhase,
            AssignedRunway = dto.AssignedRunway,
            IsOnGround = dto.IsOnGround,
            PhaseSequence = dto.PhaseSequence,
            ActivePhaseIndex = dto.ActivePhaseIndex,
            LandingClearance = dto.LandingClearance,
            ClearedRunway = dto.ClearedRunway,
            PatternDirection = dto.PatternDirection,
            NavigationRoute = dto.NavigationRoute,
            EquipmentSuffix = dto.EquipmentSuffix,
            CruiseAltitude = dto.CruiseAltitude,
            CruiseSpeed = dto.CruiseSpeed,
            TaxiRoute = dto.TaxiRoute,
            ParkingSpot = dto.ParkingSpot,
            CurrentTaxiway = dto.CurrentTaxiway,
            Owner = dto.Owner,
            OwnerSectorCode = dto.OwnerSectorCode,
            HandoffPeer = dto.HandoffPeer,
            HandoffPeerSectorCode = dto.HandoffPeerSectorCode,
            PointoutStatus = dto.PointoutStatus,
            Scratchpad1 = dto.Scratchpad1,
            Scratchpad2 = dto.Scratchpad2,
            TemporaryAltitude = dto.TemporaryAltitude,
            IsAnnotated = dto.IsAnnotated,
            ActiveApproachId = dto.ActiveApproachId,
            ExpectedApproach = dto.ExpectedApproach,
            CwtCode = dto.CwtCode,
            ActiveSidId = dto.ActiveSidId,
            ActiveStarId = dto.ActiveStarId,
            DepartureRunway = dto.DepartureRunway,
            DestinationRunway = dto.DestinationRunway,
            IndicatedAirspeed = dto.IndicatedAirspeed,
            Mach = dto.Mach,
            AssignedMach = dto.AssignedMach,
            Wind = dto.WindSpeed > 0 ? $"{dto.WindDirection:D3}{dto.WindSpeed:D2}KT" : "",
            PositionHistory = dto.PositionHistory,
        };
        model.DistanceFromFix = computeDistance?.Invoke(model);
        model.ComputeSmartStatus();
        return model;
    }

    public void UpdateFromDto(AircraftDto dto, Func<AircraftModel, double?>? computeDistance = null)
    {
        Latitude = dto.Latitude;
        Longitude = dto.Longitude;
        Heading = dto.Heading;
        Altitude = dto.Altitude;
        GroundSpeed = dto.GroundSpeed;
        BeaconCode = dto.BeaconCode;
        TransponderMode = dto.TransponderMode;
        VerticalSpeed = dto.VerticalSpeed;
        AssignedHeading = dto.AssignedHeading;
        NavigatingTo = dto.NavigatingTo;
        AssignedAltitude = dto.AssignedAltitude;
        AssignedSpeed = dto.AssignedSpeed;
        Departure = dto.Departure;
        Destination = dto.Destination;
        Route = dto.Route;
        Remarks = dto.Remarks;
        FlightRules = dto.FlightRules;
        Status = dto.Status;
        PendingCommands = dto.PendingCommands;
        CurrentPhase = dto.CurrentPhase;
        AssignedRunway = dto.AssignedRunway;
        IsOnGround = dto.IsOnGround;
        PhaseSequence = dto.PhaseSequence;
        ActivePhaseIndex = dto.ActivePhaseIndex;
        LandingClearance = dto.LandingClearance;
        ClearedRunway = dto.ClearedRunway;
        PatternDirection = dto.PatternDirection;
        NavigationRoute = dto.NavigationRoute;
        EquipmentSuffix = dto.EquipmentSuffix;
        CruiseAltitude = dto.CruiseAltitude;
        CruiseSpeed = dto.CruiseSpeed;
        TaxiRoute = dto.TaxiRoute;
        ParkingSpot = dto.ParkingSpot;
        CurrentTaxiway = dto.CurrentTaxiway;
        Owner = dto.Owner;
        OwnerSectorCode = dto.OwnerSectorCode;
        HandoffPeer = dto.HandoffPeer;
        HandoffPeerSectorCode = dto.HandoffPeerSectorCode;
        PointoutStatus = dto.PointoutStatus;
        Scratchpad1 = dto.Scratchpad1;
        Scratchpad2 = dto.Scratchpad2;
        TemporaryAltitude = dto.TemporaryAltitude;
        IsAnnotated = dto.IsAnnotated;
        ActiveApproachId = dto.ActiveApproachId;
        ExpectedApproach = dto.ExpectedApproach;
        CwtCode = dto.CwtCode;
        ActiveSidId = dto.ActiveSidId;
        ActiveStarId = dto.ActiveStarId;
        DepartureRunway = dto.DepartureRunway;
        DestinationRunway = dto.DestinationRunway;
        IndicatedAirspeed = dto.IndicatedAirspeed;
        Mach = dto.Mach;
        AssignedMach = dto.AssignedMach;
        Wind = dto.WindSpeed > 0 ? $"{dto.WindDirection:D3}{dto.WindSpeed:D2}KT" : "";
        PositionHistory = dto.PositionHistory;
        DistanceFromFix = computeDistance?.Invoke(this);
        ComputeSmartStatus();
    }

    public void ComputeSmartStatus()
    {
        var alert = CheckAlerts();
        if (alert is not null)
        {
            SmartStatus = alert.Value.Text;
            SmartStatusSeverity = alert.Value.Severity;
            return;
        }

        if (!string.IsNullOrEmpty(CurrentPhase))
        {
            var (text, severity) = ComputePhaseStatus();
            SmartStatus = text;
            SmartStatusSeverity = severity;
            return;
        }

        var noPhase = ComputeNoPhaseStatus();
        SmartStatus = noPhase.Text;
        SmartStatusSeverity = noPhase.Severity;
    }

    private (string Text, SmartStatusSeverity Severity)? CheckAlerts()
    {
        if (CurrentPhase is "FinalApproach" && string.IsNullOrEmpty(LandingClearance))
        {
            return ("No landing clnc", SmartStatusSeverity.Critical);
        }

        if (CurrentPhase is "Landing" or "Landing-H" && string.IsNullOrEmpty(LandingClearance))
        {
            return ("Landing — no clnc!", SmartStatusSeverity.Critical);
        }

        if (!string.IsNullOrEmpty(HandoffPeer))
        {
            var target = HandoffPeerSectorCode ?? HandoffPeer;
            return ($"HO → {target}", SmartStatusSeverity.Warning);
        }

        if (
            !IsOnGround
            && string.IsNullOrEmpty(CurrentPhase)
            && string.IsNullOrEmpty(ActiveSidId)
            && string.IsNullOrEmpty(ActiveStarId)
            && !AssignedAltitude.HasValue
            && string.IsNullOrEmpty(NavigationRoute)
            && !IsDelayed
        )
        {
            return ("No altitude asgn", SmartStatusSeverity.Warning);
        }

        return null;
    }

    private (string Text, SmartStatusSeverity Severity) ComputePhaseStatus()
    {
        var text = CurrentPhase switch
        {
            "At Parking" => string.IsNullOrEmpty(ParkingSpot) ? "At parking" : $"At parking {ParkingSpot}",
            "Pushback" or "Pushback to Spot" => "Pushing back",
            "Holding After Pushback" or "Holding In Position" => "Holding position",
            "Holding After Exit" => "Clear of runway",
            "Taxiing" => FormatTaxiStatus(),
            "AirTaxi" => "Air taxi",
            "Crossing Runway" => "Crossing runway",
            "LiningUp" => $"Lining up {AssignedRunway}",
            "LinedUpAndWaiting" => $"LUAW {AssignedRunway}",
            "Takeoff" or "Takeoff-H" => $"Takeoff {AssignedRunway}",
            "InitialClimb" => FormatInitialClimbStatus(),
            "InterceptCourse" => string.IsNullOrEmpty(ActiveApproachId) ? "Intercepting course" : $"Intercepting {ActiveApproachId}",
            "ApproachNav" => FormatApproachNavStatus(),
            "HoldingPattern" or "HoldingAtFix" => string.IsNullOrEmpty(NavigatingTo) ? "Holding" : $"Holding at {NavigatingTo}",
            "ProceedToFix" => string.IsNullOrEmpty(NavigatingTo) ? "Proceeding to fix" : $"Proceeding to {NavigatingTo}",
            "FinalApproach" => FormatFinalApproachStatus(),
            "Pattern Entry" => $"{PatternDirection} pattern entry",
            "Upwind" or "Crosswind" or "Downwind" or "Base" => $"{PatternDirection} {CurrentPhase.ToLowerInvariant()} {AssignedRunway}",
            "MidfieldCrossing" => $"Midfield crossing {AssignedRunway}",
            "Landing" or "Landing-H" => $"Landing {(string.IsNullOrEmpty(ClearedRunway) ? AssignedRunway : ClearedRunway)}",
            "Runway Exit" => "Exiting runway",
            "TouchAndGo" => $"Touch-and-go {ClearedRunway}",
            "StopAndGo" => $"Stop-and-go {ClearedRunway}",
            "LowApproach" => $"Low approach {ClearedRunway}",
            "GoAround" => $"Go-around {(string.IsNullOrEmpty(ClearedRunway) ? AssignedRunway : ClearedRunway)}",
            "HPP-L" or "HPP-R" or "HPP" => "Hold present position",
            "S-Turns" => "S-turns",
            _ => FormatFallbackPhase(),
        };

        return (text, SmartStatusSeverity.Normal);
    }

    private string FormatTaxiStatus()
    {
        var baseText = string.IsNullOrEmpty(AssignedRunway) ? "Taxiing" : $"Taxi to RWY {AssignedRunway}";
        if (!string.IsNullOrEmpty(TaxiRoute))
        {
            var taxiways = TaxiRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var preview = string.Join(" ", taxiways.Length > 3 ? taxiways.AsSpan(0, 3).ToArray() : taxiways);
            baseText = $"{baseText} via {preview}";
        }
        return baseText;
    }

    private string FormatInitialClimbStatus()
    {
        var text = $"Departing {DepartureRunway}";
        if (!string.IsNullOrEmpty(ActiveSidId))
        {
            return $"{text}, {ActiveSidId}";
        }
        if (AssignedHeading.HasValue)
        {
            return $"{text}, hdg {AssignedHeadingDisplay}";
        }
        return text;
    }

    private string FormatApproachNavStatus()
    {
        var text = ActiveApproachId ?? "";
        if (!string.IsNullOrEmpty(NavigationRoute))
        {
            text = $"{text} → {NavigationRoute.Replace(" > ", " ")}";
        }
        return text;
    }

    private string FormatFinalApproachStatus()
    {
        if (!string.IsNullOrEmpty(ActiveApproachId))
        {
            return $"{ActiveApproachId} final";
        }
        var rwy = string.IsNullOrEmpty(ClearedRunway) ? AssignedRunway : ClearedRunway;
        return $"Final {rwy}";
    }

    private string FormatFallbackPhase()
    {
        if (CurrentPhase.StartsWith("Holding Short", StringComparison.Ordinal))
        {
            var target = CurrentPhase.Length > 14 ? CurrentPhase[14..] : "";
            return string.IsNullOrEmpty(target) ? "Hold short" : $"Hold short {target}";
        }

        if (CurrentPhase.StartsWith("Following ", StringComparison.Ordinal))
        {
            return CurrentPhase;
        }

        if (CurrentPhase.StartsWith("Turn", StringComparison.Ordinal))
        {
            return "Turning";
        }

        return CurrentPhase;
    }

    private (string Text, SmartStatusSeverity Severity) ComputeNoPhaseStatus()
    {
        if (IsOnGround && GroundSpeed < 5)
        {
            return ("On ground", SmartStatusSeverity.Normal);
        }

        if (!IsOnGround && VerticalSpeed > 300)
        {
            return (FormatClimbDescentStatus("Climbing", "\u2191"), SmartStatusSeverity.Normal);
        }

        if (!IsOnGround && VerticalSpeed < -300)
        {
            return (FormatClimbDescentStatus("Descending", "\u2193"), SmartStatusSeverity.Normal);
        }

        if (!IsOnGround)
        {
            if (!string.IsNullOrEmpty(NavigationRoute))
            {
                return ($"\u2192 {NavigationRoute.Replace(" > ", " ")}", SmartStatusSeverity.Normal);
            }
            if (!string.IsNullOrEmpty(NavigatingTo))
            {
                return ($"\u2192 {NavigatingTo}", SmartStatusSeverity.Normal);
            }
            return ($"{FormatAltitudeCompact(Altitude)}, on course", SmartStatusSeverity.Normal);
        }

        return ("Taxiing", SmartStatusSeverity.Normal);
    }

    private string FormatClimbDescentStatus(string verb, string arrow)
    {
        string text;
        if (AssignedAltitude.HasValue)
        {
            text = $"{arrow} {FormatAltitudeCompact(AssignedAltitude.Value)}";
        }
        else
        {
            text = verb;
        }

        if (!string.IsNullOrEmpty(NavigationRoute))
        {
            text = $"{text} \u2192 {NavigationRoute.Replace(" > ", " ")}";
        }
        return text;
    }

    public static string FormatAltitudeCompact(double altitude)
    {
        if (altitude >= 18000)
        {
            return $"FL{altitude / 100:F0}";
        }
        return altitude.ToString("N0");
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
