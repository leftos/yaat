using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim;

/// <summary>Severity tier for an aircraft's one-line status, used to colour the Info column.</summary>
public enum AircraftStatusSeverity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// The few status inputs that are not derivable from <see cref="AircraftState"/> alone and must be
/// supplied by the caller: whether a queued delay is active (server broadcast status string), whether
/// the scenario auto-clears landings, and the handoff peer's display label (sector code, which needs
/// ARTCC config to resolve). Pass <see cref="None"/> from contexts that don't track these (e.g. tick
/// recording of ground ops) — they only affect airborne / handoff edge cases.
/// </summary>
public readonly record struct AircraftStatusContext(bool IsDelayed, bool IsAutoClearedToLand, string? HandoffPeerLabel)
{
    public static AircraftStatusContext None => new(false, false, null);
}

/// <summary>
/// Produces the one-line human-readable status shown in the Aircraft List "Info" column from
/// <see cref="AircraftState"/>. This is a pure projection of sim state to text: the server computes
/// it once per broadcast (shipped in the aircraft DTO so the client just displays it) and tooling
/// (TickRecorder) calls it directly. There is exactly one implementation so all surfaces agree.
/// </summary>
public static class AircraftStatusDescriber
{
    /// <summary>The status text and its severity for <paramref name="ac"/>.</summary>
    public static (string Text, AircraftStatusSeverity Severity) Describe(AircraftState ac, AircraftStatusContext ctx) =>
        Describe(AircraftStatusView.FromState(ac, ctx));

    /// <summary>
    /// Status from an explicit input view. Exposed so the projection is unit-testable directly,
    /// without constructing a full <see cref="AircraftState"/> with phases.
    /// </summary>
    public static (string Text, AircraftStatusSeverity Severity) Describe(AircraftStatusView i)
    {
        var alert = CheckAlerts(i);
        if (alert is not null)
        {
            return alert.Value;
        }

        if (!string.IsNullOrEmpty(i.CurrentPhase))
        {
            var (text, severity) = ComputePhaseStatus(i);
            return (CapitalizeFirst(AppendHeadingIfAssigned(i, text, ShouldKeepHeadingSuffix(i.CurrentPhase))), severity);
        }

        var noPhase = ComputeNoPhaseStatus(i);
        return (CapitalizeFirst(AppendHeadingIfAssigned(i, noPhase.Text, keep: true)), noPhase.Severity);
    }

    /// <summary>
    /// Explicit status inputs, mirroring the AircraftState-derived fields the Info column reads.
    /// Public so the projection can be unit-tested directly; <see cref="FromState"/> builds it from
    /// an <see cref="AircraftState"/> and tests construct it with an object initializer.
    /// </summary>
    public sealed record AircraftStatusView
    {
        public string CurrentPhase { get; init; } = "";
        public bool IsOnGround { get; init; }
        public double GroundSpeed { get; init; }
        public double VerticalSpeed { get; init; }
        public double Altitude { get; init; }
        public string AssignedRunway { get; init; } = "";
        public string ClearedRunway { get; init; } = "";
        public string DepartureRunway { get; init; } = "";
        public string? CrossingRunwayId { get; init; }
        public string? ExitingRunwayId { get; init; }
        public string CurrentTaxiway { get; init; } = "";
        public string TaxiRoute { get; init; } = "";
        public string ParkingSpot { get; init; } = "";
        public string PatternDirection { get; init; } = "";
        public string? PatternEntryKind { get; init; }
        public string LandingClearance { get; init; } = "";
        public bool IsAutoClearedToLand { get; init; }
        public string? HandoffPeer { get; init; }
        public string? HandoffPeerSectorCode { get; init; }
        public string? ActiveSidId { get; init; }
        public string? ActiveStarId { get; init; }
        public string? ActiveApproachId { get; init; }
        public string? NavigatingTo { get; init; }
        public string? FollowingCallsign { get; init; }
        public double? AssignedAltitude { get; init; }
        public MagneticHeading? AssignedHeading { get; init; }

        /// <summary>Climb target altitude (feet MSL) while in the InitialClimb phase; drives the "↑ {alt}" departure suffix.</summary>
        public double? TargetAltitude { get; init; }

        /// <summary>Departure clearance retained by the InitialClimb phase; drives the departure lateral descriptor.</summary>
        public DepartureInstruction? Departure { get; init; }
        public int NavigationRouteCount { get; init; }
        public string NavigationRouteDisplay { get; init; } = "";
        public bool IsDelayed { get; init; }

        public static AircraftStatusView FromState(AircraftState ac, AircraftStatusContext ctx)
        {
            var navNames = BuildNavigationRoute(ac.Targets);
            return new AircraftStatusView
            {
                CurrentPhase = ac.Phases?.CurrentPhase?.Name ?? "",
                IsOnGround = ac.IsOnGround,
                GroundSpeed = ac.GroundSpeed,
                VerticalSpeed = ac.VerticalSpeed,
                Altitude = ac.Altitude,
                AssignedRunway = ac.Phases?.AssignedRunway?.Designator ?? "",
                ClearedRunway = ac.Phases?.ClearedRunwayId ?? "",
                DepartureRunway = ac.Procedure.DepartureRunway ?? "",
                CrossingRunwayId = ExtractCrossingRunwayId(ac.Phases),
                ExitingRunwayId = ExtractExitingRunwayId(ac.Phases),
                CurrentTaxiway = ac.Ground.CurrentTaxiway ?? "",
                TaxiRoute = ac.Ground.AssignedTaxiRoute?.FormatTaxiwaySequence() ?? "",
                ParkingSpot = ac.Ground.ParkingSpot ?? "",
                PatternDirection = ac.Phases?.TrafficDirection?.ToString() ?? "",
                PatternEntryKind = ExtractPatternEntryKind(ac.Phases),
                LandingClearance = ac.Phases?.LandingClearance?.ToString() ?? "",
                IsAutoClearedToLand = ctx.IsAutoClearedToLand,
                HandoffPeer = ac.Track.HandoffPeer?.Callsign,
                HandoffPeerSectorCode = ctx.HandoffPeerLabel,
                ActiveSidId = ac.Procedure.ActiveSidId,
                ActiveStarId = ac.Procedure.ActiveStarId,
                ActiveApproachId = ac.Phases?.ActiveApproach?.ApproachId,
                NavigatingTo = navNames.Count > 0 ? navNames[0] : "",
                FollowingCallsign = ac.Approach.FollowingCallsign,
                AssignedAltitude = ac.Targets.AssignedAltitude,
                AssignedHeading = ac.Targets.AssignedMagneticHeading,
                TargetAltitude = ac.Targets.TargetAltitude,
                Departure = (ac.Phases?.CurrentPhase as InitialClimbPhase)?.Departure,
                NavigationRouteCount = navNames.Count,
                NavigationRouteDisplay = string.Join(" ", navNames),
                IsDelayed = ctx.IsDelayed,
            };
        }
    }

    private static (string Text, AircraftStatusSeverity Severity)? CheckAlerts(AircraftStatusView i)
    {
        if (i.CurrentPhase is "FinalApproach" && string.IsNullOrEmpty(i.LandingClearance) && !i.IsAutoClearedToLand)
        {
            return ("No landing clnc", AircraftStatusSeverity.Critical);
        }

        if (i.CurrentPhase is "Landing" or "Landing-H" && string.IsNullOrEmpty(i.LandingClearance) && !i.IsAutoClearedToLand)
        {
            return ("Landing — no clnc!", AircraftStatusSeverity.Critical);
        }

        if (!string.IsNullOrEmpty(i.HandoffPeer))
        {
            var target = i.HandoffPeerSectorCode ?? i.HandoffPeer;
            return ($"HO → {target}", AircraftStatusSeverity.Warning);
        }

        if (
            !i.IsOnGround
            && string.IsNullOrEmpty(i.CurrentPhase)
            && string.IsNullOrEmpty(i.ActiveSidId)
            && string.IsNullOrEmpty(i.ActiveStarId)
            && !i.AssignedAltitude.HasValue
            && !i.AssignedHeading.HasValue
            && i.NavigationRouteCount == 0
            && !i.IsDelayed
        )
        {
            return ("No altitude asgn", AircraftStatusSeverity.Warning);
        }

        return null;
    }

    private static (string Text, AircraftStatusSeverity Severity) ComputePhaseStatus(AircraftStatusView i)
    {
        var dir = string.IsNullOrEmpty(i.PatternDirection) ? "" : i.PatternDirection.ToLowerInvariant();
        var text = i.CurrentPhase switch
        {
            "At Parking" => string.IsNullOrEmpty(i.ParkingSpot) ? "at parking" : $"at parking {i.ParkingSpot}",
            "Pushback" or "Pushback to Spot" => "pushing back",
            "Holding After Pushback" or "Holding In Position" => "holding position",
            "Holding After Exit" => FormatHoldingAfterExitStatus(i),
            "Taxiing" => FormatTaxiStatus(i),
            "AirTaxi" => string.IsNullOrEmpty(i.AssignedRunway) ? "air taxi" : $"air taxi to {i.AssignedRunway}",
            "Crossing Runway" => FormatCrossingRunwayStatus(i),
            "LiningUp" => $"lining up {i.AssignedRunway}",
            "LinedUpAndWaiting" => $"LUAW {i.AssignedRunway}",
            "Takeoff" or "Takeoff-H" => $"takeoff {i.AssignedRunway}",
            "InitialClimb" => FormatInitialClimbStatus(i),
            "InterceptCourse" => string.IsNullOrEmpty(i.ActiveApproachId) ? "intercepting course" : $"intercepting {i.ActiveApproachId}",
            "ApproachNav" => FormatApproachNavStatus(i),
            "HoldingPattern" or "HoldingAtFix" => string.IsNullOrEmpty(i.NavigatingTo) ? "holding" : $"holding at {i.NavigatingTo}",
            "ProceedToFix" => string.IsNullOrEmpty(i.NavigatingTo) ? "proceeding to fix" : $"proceeding to {i.NavigatingTo}",
            "FinalApproach" => FormatFinalApproachStatus(i),
            "Pattern Entry" => FormatPatternEntryStatus(i),
            "Upwind" or "Crosswind" or "Downwind" or "Base" => JoinNonEmpty(dir, i.CurrentPhase.ToLowerInvariant(), i.AssignedRunway),
            "MidfieldCrossing" => $"midfield crossing {i.AssignedRunway}",
            "Landing" or "Landing-H" => $"landing {(string.IsNullOrEmpty(i.ClearedRunway) ? i.AssignedRunway : i.ClearedRunway)}",
            "Runway Exit" => FormatRunwayExitStatus(i),
            "TouchAndGo" => $"touch-and-go {i.ClearedRunway}",
            "StopAndGo" => $"stop-and-go {i.ClearedRunway}",
            "LowApproach" => $"low approach {i.ClearedRunway}",
            "GoAround" => $"go-around {(string.IsNullOrEmpty(i.ClearedRunway) ? i.AssignedRunway : i.ClearedRunway)}",
            "HPP-L" or "HPP-R" or "HPP" => "hold present position",
            "S-Turns" => "s-turns",
            "VFR Follow" => string.IsNullOrEmpty(i.FollowingCallsign) ? "VFR follow" : $"following {i.FollowingCallsign}",
            _ => FormatFallbackPhase(i),
        };

        if (!string.IsNullOrEmpty(i.FollowingCallsign) && i.CurrentPhase != "VFR Follow")
        {
            text = $"following {i.FollowingCallsign} → {text}";
        }

        return (text, AircraftStatusSeverity.Normal);
    }

    private static string FormatPatternEntryStatus(AircraftStatusView i)
    {
        var dir = string.IsNullOrEmpty(i.PatternDirection) ? "" : i.PatternDirection.ToLowerInvariant();
        var rwy = i.AssignedRunway;

        return i.PatternEntryKind switch
        {
            "Direct" => JoinNonEmpty("direct", dir, "downwind", rwy),
            "FortyFive" => JoinNonEmpty("45 to", dir, "downwind", rwy),
            "Crosswind" => JoinNonEmpty("crosswind to", dir, "downwind", rwy),
            "Midfield" => JoinNonEmpty("midfield to", dir, "downwind", rwy),
            "Upwind" => JoinNonEmpty("upwind entry", rwy),
            "Base" => JoinNonEmpty(dir, "base entry", rwy),
            "Final" => JoinNonEmpty("straight-in", rwy),
            _ => JoinNonEmpty(dir, "pattern entry", rwy),
        };
    }

    private static string JoinNonEmpty(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string FormatCrossingRunwayStatus(AircraftStatusView i)
    {
        var rwy = string.IsNullOrEmpty(i.CrossingRunwayId) ? i.AssignedRunway : i.CrossingRunwayId;
        return string.IsNullOrEmpty(rwy) ? "crossing runway" : $"crossing runway {rwy}";
    }

    private static string FormatHoldingAfterExitStatus(AircraftStatusView i)
    {
        var rwy = string.IsNullOrEmpty(i.ExitingRunwayId) ? i.AssignedRunway : i.ExitingRunwayId;
        if (string.IsNullOrEmpty(rwy))
        {
            return "clear of runway";
        }
        if (!string.IsNullOrEmpty(i.CurrentTaxiway))
        {
            return $"clear of runway {rwy} via {i.CurrentTaxiway}";
        }
        return $"clear of runway {rwy}";
    }

    private static string FormatRunwayExitStatus(AircraftStatusView i)
    {
        var rwy = string.IsNullOrEmpty(i.ExitingRunwayId) ? i.AssignedRunway : i.ExitingRunwayId;
        if (string.IsNullOrEmpty(rwy))
        {
            return "exiting runway";
        }
        if (!string.IsNullOrEmpty(i.CurrentTaxiway))
        {
            return $"exiting runway {rwy} via {i.CurrentTaxiway}";
        }
        return $"exiting runway {rwy}";
    }

    private static string FormatTaxiStatus(AircraftStatusView i)
    {
        var baseText = string.IsNullOrEmpty(i.AssignedRunway) ? "taxiing" : $"taxi to RWY {i.AssignedRunway}";
        if (!string.IsNullOrEmpty(i.TaxiRoute))
        {
            baseText = $"{baseText} via {i.TaxiRoute}";
        }
        return baseText;
    }

    private static string FormatInitialClimbStatus(AircraftStatusView i)
    {
        var text = $"departing {i.DepartureRunway}";
        var lateral = FormatDepartureLateral(i);
        if (!string.IsNullOrEmpty(lateral))
        {
            text = $"{text}, {lateral}";
        }
        if (i.TargetAltitude.HasValue)
        {
            text = $"{text}, ↑ {FormatAltitudeCompact(i.TargetAltitude.Value)}";
        }
        return text;
    }

    /// <summary>
    /// The lateral half of the departure status: where the aircraft is going after takeoff. SID takes
    /// precedence; a set <see cref="AircraftStatusView.PatternDirection"/> means closed traffic (this
    /// survives snapshot restore, unlike the instruction type); otherwise the retained
    /// <see cref="DepartureInstruction"/> is matched. A <see cref="DefaultDeparture"/> with a loaded
    /// route fix is IFR ("→ {fix}"); without one it is VFR runway heading.
    /// </summary>
    private static string FormatDepartureLateral(AircraftStatusView i)
    {
        if (!string.IsNullOrEmpty(i.ActiveSidId))
        {
            return i.ActiveSidId;
        }
        if (!string.IsNullOrEmpty(i.PatternDirection))
        {
            return $"{i.PatternDirection.ToLowerInvariant()} traffic";
        }

        return i.Departure switch
        {
            ClosedTrafficDeparture ct => $"{ct.Direction.ToString().ToLowerInvariant()} traffic",
            OnCourseDeparture => "on course",
            DirectFixDeparture d => $"→ {d.FixName}",
            FlyHeadingDeparture fh => FormatHeadingLateral(fh.Direction, fh.MagneticHeading.Degrees),
            RelativeTurnDeparture rt => $"{rt.Direction.ToString().ToLowerInvariant()} turn {rt.Degrees}°",
            RunwayHeadingDeparture => "runway heading",
            DefaultDeparture => string.IsNullOrEmpty(i.NavigatingTo) ? "runway heading" : $"→ {i.NavigatingTo}",
            null => !string.IsNullOrEmpty(i.NavigatingTo) ? $"→ {i.NavigatingTo}"
            : i.AssignedHeading.HasValue ? $"hdg {i.AssignedHeading.Value.Degrees:F0}"
            : "",
            _ => "",
        };
    }

    private static string FormatHeadingLateral(TurnDirection? dir, double deg) =>
        dir is null ? $"hdg {deg:F0}" : $"{dir.ToString()!.ToLowerInvariant()} turn hdg {deg:F0}";

    private static string FormatApproachNavStatus(AircraftStatusView i)
    {
        var text = i.ActiveApproachId ?? "";
        if (i.NavigationRouteCount > 0)
        {
            text = $"{text} → {i.NavigationRouteDisplay}";
        }
        return text;
    }

    private static string FormatFinalApproachStatus(AircraftStatusView i)
    {
        if (!string.IsNullOrEmpty(i.ActiveApproachId))
        {
            return $"{i.ActiveApproachId} final";
        }
        var rwy = string.IsNullOrEmpty(i.ClearedRunway) ? i.AssignedRunway : i.ClearedRunway;
        return $"final {rwy}";
    }

    private static string FormatFallbackPhase(AircraftStatusView i)
    {
        if (i.CurrentPhase.StartsWith("Holding Short", StringComparison.Ordinal))
        {
            var target = i.CurrentPhase.Length > 14 ? i.CurrentPhase[14..] : "";
            if (string.IsNullOrEmpty(target))
            {
                return "holding short";
            }
            bool isRunway = char.IsDigit(target[0]);
            var twy = i.CurrentTaxiway;
            if (isRunway)
            {
                return string.IsNullOrEmpty(twy) ? $"holding short {target}" : $"holding short {target} @ {twy}";
            }
            return string.IsNullOrEmpty(twy) ? $"holding short of {target}" : $"holding short of {target} on {twy}";
        }

        if (i.CurrentPhase.StartsWith("Following ", StringComparison.Ordinal))
        {
            return "following " + i.CurrentPhase[10..];
        }

        if (i.CurrentPhase.StartsWith("Turn", StringComparison.Ordinal))
        {
            return "turning";
        }

        return i.CurrentPhase;
    }

    private static bool ShouldKeepHeadingSuffix(string phase)
    {
        return phase switch
        {
            "ProceedToFix" or "InterceptCourse" or "HoldingPattern" or "HoldingAtFix" => true,
            _ when phase.StartsWith("Turn", StringComparison.Ordinal) => true,
            _ => false,
        };
    }

    private static (string Text, AircraftStatusSeverity Severity) ComputeNoPhaseStatus(AircraftStatusView i)
    {
        if (i.IsOnGround && i.GroundSpeed < 5)
        {
            return ("on ground", AircraftStatusSeverity.Normal);
        }

        if (!i.IsOnGround && i.VerticalSpeed > 300)
        {
            return (FormatClimbDescentStatus(i, "climbing", "↑"), AircraftStatusSeverity.Normal);
        }

        if (!i.IsOnGround && i.VerticalSpeed < -300)
        {
            return (FormatClimbDescentStatus(i, "descending", "↓"), AircraftStatusSeverity.Normal);
        }

        if (!i.IsOnGround)
        {
            if (i.NavigationRouteCount > 0)
            {
                return ($"→ {i.NavigationRouteDisplay}", AircraftStatusSeverity.Normal);
            }
            if (!string.IsNullOrEmpty(i.NavigatingTo))
            {
                return ($"→ {i.NavigatingTo}", AircraftStatusSeverity.Normal);
            }
            return ($"{FormatAltitudeCompact(i.Altitude)}, on course", AircraftStatusSeverity.Normal);
        }

        return ("taxiing", AircraftStatusSeverity.Normal);
    }

    private static string AppendHeadingIfAssigned(AircraftStatusView i, string text, bool keep)
    {
        if (keep && i.AssignedHeading.HasValue)
        {
            return $"{text}, hdg {i.AssignedHeading.Value.Degrees:F0}";
        }
        return text;
    }

    private static string FormatClimbDescentStatus(AircraftStatusView i, string verb, string arrow)
    {
        string text = i.AssignedAltitude.HasValue ? $"{arrow} {FormatAltitudeCompact(i.AssignedAltitude.Value)}" : verb;
        if (i.NavigationRouteCount > 0)
        {
            text = $"{text} → {i.NavigationRouteDisplay}";
        }
        return text;
    }

    /// <summary>Compact altitude rendering: flight levels above FL180, otherwise feet with thousands separators.</summary>
    public static string FormatAltitudeCompact(double altitude)
    {
        if (altitude >= 18000)
        {
            return $"FL{altitude / 100:F0}";
        }
        return altitude.ToString("N0");
    }

    /// <summary>Uppercases the first character if it is a lowercase letter, so the Info column reads like a sentence.</summary>
    internal static string CapitalizeFirst(string s)
    {
        if (string.IsNullOrEmpty(s) || !char.IsLower(s[0]))
        {
            return s;
        }
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static List<string> BuildNavigationRoute(ControlTargets targets)
    {
        if (targets.NavigationRoute.Count == 0)
        {
            return [];
        }

        var names = new List<string>(targets.NavigationRoute.Count);
        foreach (var nav in targets.NavigationRoute)
        {
            names.Add(nav.Name);
        }

        return names;
    }

    private static string? ExtractCrossingRunwayId(PhaseList? phases)
    {
        if (phases?.CurrentPhase is CrossingRunwayPhase crossing)
        {
            return crossing.RunwayId;
        }
        return null;
    }

    private static string? ExtractPatternEntryKind(PhaseList? phases)
    {
        if (phases?.CurrentPhase is PatternEntryPhase entry)
        {
            return entry.Kind.ToString();
        }
        return null;
    }

    private static string? ExtractExitingRunwayId(PhaseList? phases)
    {
        if (phases is null)
        {
            return null;
        }
        var current = phases.CurrentPhase;
        if (current is RunwayExitPhase exit)
        {
            return exit.RunwayId;
        }
        if (current is HoldingAfterExitPhase holding)
        {
            return holding.RunwayId;
        }
        return null;
    }
}
