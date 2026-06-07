using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

public static class ApproachCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("ApproachCommandHandler");

    /// <summary>
    /// Downwind altitude (feet AGL) for an IFR visual approach pattern entry.
    /// 2000 ft AGL keeps the IFR aircraft 1000+ ft above standard 1000-ft VFR
    /// pattern traffic and 500 ft above 1500-ft VFR-jet pattern traffic.
    /// </summary>
    private const double IfrVisualDownwindAltAglFt = 2000.0;

    public static CommandResult TryClearedApproach(ClearedApproachCommand cmd, AircraftState aircraft)
    {
        Log.LogDebug(
            "[CAPP] {Callsign} cmd=({Apch} apt={Apt} dct={Dct} at={At}) nav=[{Route}] hdg={Hdg:F1} assignedHdg={Assigned}",
            aircraft.Callsign,
            cmd.ApproachId,
            cmd.AirportCode,
            cmd.DctFix,
            cmd.AtFix,
            string.Join(",", aircraft.Targets.NavigationRoute.Select(n => n.Name)),
            aircraft.TrueHeading.Degrees,
            aircraft.Targets.AssignedMagneticHeading?.Degrees.ToString("F0") ?? "null"
        );

        var resolved = ResolveApproach(cmd.ApproachId, cmd.AirportCode, aircraft);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        // Cancel existing speed restrictions per 7110.65 §5-7-1
        aircraft.Targets.TargetSpeed = null;

        var facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Build approach fix sequence, selecting best transition if available
        var transition = SelectBestTransition(procedure, aircraft);
        var approachFixes = transition is not null ? BuildApproachFixesWithTransition(transition, procedure) : BuildApproachFixes(procedure);

        // Check conditions for deferred vs immediate approach activation
        bool hasDctFix = cmd.DctFix is not null;
        bool isOnAssignedHeading = aircraft.Targets.AssignedMagneticHeading is not null;

        // A transition that ends with HF/HM/HA carries a hold-in-lieu of procedure turn at
        // the IAF. The deferred path stores fixes in NavigationRoute as a plain sequence,
        // which cannot express a holding pattern, so we force immediate activation and let
        // the immediate path insert a HoldingPatternPhase (mirroring JAPP).
        bool transitionHasHilpt =
            transition is not null
            && transition.Legs.Any(l => l.PathTerminator is CifpPathTerminator.HF or CifpPathTerminator.HM or CifpPathTerminator.HA);

        // Procedure turn (PI leg) — engage per AIM 5-4-9.1 unless an exclusion applies.
        // Three positive triggers:
        //   (1) selected transition contains the PI leg — aircraft is naturally entering
        //       the published course-reversal route (e.g. arriving at CCR after DCT or via
        //       a STAR that drops the aircraft at the PT anchor).
        //   (2) controller's DCT fix matches the PT anchor — explicit "go to the PT fix
        //       and shoot the approach" intent in compound CAPP-DCT form.
        //   (3) instantaneous intercept angle > 90° AND the aircraft is on a transition path
        //       (not following the full published common-leg sequence). When transition is
        //       null the aircraft flies the common legs from the start — the published
        //       feeder course delivers alignment regardless of current heading, so the
        //       instantaneous-heading check would be a false positive (e.g. an aircraft
        //       crossing the IAF heading away from the FAF will turn back through HUKVI
        //       on the published 191° leg long before reaching the FAF).
        // Negative exclusion: NoPT transition. AIM 5-4-9.1 exempts aircraft on a published
        // NoPT feeder (the chart guarantees the geometry); skip PT and let approach
        // navigation deliver the aircraft inbound naturally.
        bool transitionContainsPi = transition is not null && transition.Legs.Any(l => l.PathTerminator == CifpPathTerminator.PI);
        bool transitionIsNoPt = transition is not null && transition.IsNoPt;
        bool dctMatchesPtAnchor =
            cmd.DctFix is not null
            && procedure.ProcedureTurnLeg is not null
            && procedure.ProcedureTurnLeg.FixIdentifier.Equals(cmd.DctFix, StringComparison.OrdinalIgnoreCase);
        double interceptAngleDeg = aircraft.TrueHeading.AbsAngleTo(finalCourse);
        bool interceptTooSteep = interceptAngleDeg > 90.0 && transition is not null;
        bool needsProcedureTurn =
            procedure.ProcedureTurnLeg is not null && !transitionIsNoPt && (transitionContainsPi || dctMatchesPtAnchor || interceptTooSteep);

        // Deferred approach: when the STAR delivers to a transition connecting fix,
        // store the clearance as pending and append approach fixes to the nav route.
        // The aircraft continues flying its STAR; approach phases activate when the
        // route empties (aircraft reaches the last approach fix via normal navigation).
        // "AT <fix> CAPP" defers the same way when the AT fix is already in the nav route
        // — the AT is redundant since the STAR already delivers there.
        // DCT always activates immediately (it implies leaving the STAR route).
        if (transition is not null && !hasDctFix && !isOnAssignedHeading && !transitionHasHilpt && !needsProcedureTurn)
        {
            var trimmedFixes = TrimToNavRouteConnection(approachFixes, aircraft);
            string connectingFix = trimmedFixes.Count > 0 ? trimmedFixes[0].Name : "";

            // AT fix must match the connecting fix (or no AT fix at all) for deferred path
            bool atFixMatchesConnection = cmd.AtFix is null || connectingFix.Equals(cmd.AtFix, StringComparison.OrdinalIgnoreCase);

            if (trimmedFixes.Count > 0 && atFixMatchesConnection && NavRouteContainsFix(aircraft, connectingFix))
            {
                var clearance = BuildClearance(procedure, airport, facResult, approachRunway, cmd.Force);
                aircraft.Approach.PendingClearance = new PendingApproachInfo { Clearance = clearance, AssignedRunway = approachRunway };
                aircraft.Procedure.DestinationRunway = approachRunway.Designator;

                // Append approach fixes after the connecting fix in the NavigationRoute
                AppendApproachFixesToNavRoute(aircraft, trimmedFixes);

                string deferredPrefix = cmd.Force ? "Force: cleared" : "Cleared";
                return new CommandResult(
                    true,
                    $"{deferredPrefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
                );
            }
        }

        // --- Immediate approach activation ---

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var immClearance = BuildClearance(procedure, airport, facResult, approachRunway, cmd.Force);

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = immClearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;

        bool hasAtOrDctFix = cmd.AtFix is not null || hasDctFix;
        bool hasNavRoute = aircraft.Targets.NavigationRoute.Count > 0;

        // Implied PTAC: no AT/DCT fix, aircraft is on vectors (no nav route, on assigned
        // heading) → intercept on present heading. Aircraft with a navigation route are
        // not vectored — they're navigating direct to a fix, so CAPP must build the
        // published procedure (and engage course reversal if needed).
        if (!hasAtOrDctFix && isOnAssignedHeading && !hasNavRoute)
        {
            aircraft.Targets.NavigationRoute.Clear();

            if (cmd.CrossFixAltitude is { } interceptCxAlt && interceptCxAlt > 0)
            {
                aircraft.Targets.TargetAltitude = interceptCxAlt;
                aircraft.Targets.AssignedAltitude = interceptCxAlt;
            }

            aircraft.Phases.Add(
                new InterceptCoursePhase
                {
                    FinalApproachCourse = finalCourse,
                    ThresholdLat = approachRunway.ThresholdLatitude,
                    ThresholdLon = approachRunway.ThresholdLongitude,
                    ApproachId = immClearance.ApproachId,
                    ForcedIntercept = cmd.Force,
                }
            );
            aircraft.Phases.Add(new FinalApproachPhase());
            var isHeliIntercept = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
            aircraft.Phases.Add(isHeliIntercept ? new HelicopterLandingPhase() : new LandingPhase());

            StartPhases(aircraft);
            string impliedPrefix = cmd.Force ? "Force: cleared" : "Cleared";
            return new CommandResult(
                true,
                $"{impliedPrefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
            );
        }

        // Handle rich CAPP forms: AT fix, DCT fix — prepend to approach fixes
        if (cmd.AtFix is not null && cmd.AtFixLat is not null && cmd.AtFixLon is not null)
        {
            approachFixes.Insert(0, new ApproachFix(cmd.AtFix, cmd.AtFixLat.Value, cmd.AtFixLon.Value));
        }
        else if (cmd.DctFix is not null && cmd.DctFixLat is not null && cmd.DctFixLon is not null)
        {
            approachFixes.Insert(0, new ApproachFix(cmd.DctFix, cmd.DctFixLat.Value, cmd.DctFixLon.Value));
        }

        // Apply crossing fix altitude if specified
        if (cmd.CrossFixAltitude is { } cxAlt && cxAlt > 0)
        {
            aircraft.Targets.TargetAltitude = cxAlt;
            aircraft.Targets.AssignedAltitude = cxAlt;
        }

        // Procedure turn: insert PT phase BEFORE approach navigation, and trim approach fixes
        // through the PT anchor (post-PT, the aircraft is established inbound at/near the
        // anchor — typically the FAF — so only post-anchor fixes remain to navigate).
        ProcedureTurnPhase? cappProcedureTurn = null;
        if (needsProcedureTurn && procedure.ProcedureTurnLeg is { } cappPiLeg)
        {
            cappProcedureTurn = BuildProcedureTurnPhase(cappPiLeg, aircraft, finalCourse);
            if (cappProcedureTurn is not null)
            {
                approachFixes = TrimFixesPastProcedureTurnAnchor(approachFixes, cappProcedureTurn.FixName);
                aircraft.Phases.Add(cappProcedureTurn);
            }
        }

        // Build phase sequence
        if (approachFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = approachFixes });
        }

        // Hold-in-lieu of procedure turn: when the chosen transition includes a hold leg
        // (HF/HM/HA), insert a one-circuit HoldingPatternPhase at the IAF after the
        // navigation phase. The aircraft flies the entry fixes (e.g. MOD → ZELAT), then
        // the hold phase takes over (one circuit course-reversal), then FinalApproachPhase
        // captures the FAC inbound. Mirrors the JAPP HILPT block at TryJoinApproach above.
        // Skipped when a PI procedure turn is already engaged (PT and HILPT are mutually
        // exclusive on real procedures).
        if (cappProcedureTurn is null && transitionHasHilpt && procedure.HoldInLieuLeg is { } cappHoldLeg)
        {
            var holdPhase = BuildHoldInLieuPhase(cappHoldLeg, approachFixes, finalCourse);
            if (holdPhase is not null)
            {
                aircraft.Phases.Add(holdPhase);
            }
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string cappPrefix = cmd.Force ? "Force: cleared" : "Cleared";
        return new CommandResult(
            true,
            $"{cappPrefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    public static CommandResult TryJoinApproach(string approachId, string? airportCode, bool force, bool straightIn, AircraftState aircraft)
    {
        var resolved = ResolveApproach(approachId, airportCode, aircraft);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        // Cancel existing speed restrictions per 7110.65 §5-7-1
        aircraft.Targets.TargetSpeed = null;

        var facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Build approach fix sequence, selecting best transition if available
        var transition = SelectBestTransition(procedure, aircraft);
        var approachFixes = transition is not null ? BuildApproachFixesWithTransition(transition, procedure) : BuildApproachFixes(procedure);

        // For JAPP: find nearest IAF/IF ahead of aircraft
        var trimmedFixes = TrimToNearestEntry(approachFixes, aircraft);

        // Hold-in-lieu: if procedure has one and NOT straight-in, insert hold
        bool needsHold = procedure.HasHoldInLieu && !straightIn;

        // Procedure turn (PI). For non-straight-in joins, engage per AIM 5-4-9.1 when the
        // published procedure has a PT and the aircraft is entering via the PI-bearing
        // transition or the geometry forces it — unless on a NoPT feeder. For straight-in
        // (CAPPSI/JAPPSI), reject when the intercept is too steep AND a course reversal is
        // published — controller asked to skip a maneuver the geometry actually requires.
        bool jappTransitionContainsPi = transition is not null && transition.Legs.Any(l => l.PathTerminator == CifpPathTerminator.PI);
        bool jappTransitionIsNoPt = transition is not null && transition.IsNoPt;
        double japInterceptAngle = aircraft.TrueHeading.AbsAngleTo(finalCourse);
        bool japInterceptTooSteep = japInterceptAngle > 90.0;
        if (straightIn && japInterceptTooSteep && (procedure.ProcedureTurnLeg is not null || procedure.HasHoldInLieu))
        {
            return new CommandResult(
                false,
                $"Unable straight-in {procedure.ApproachId}: intercept angle {japInterceptAngle:F0}° exceeds 90° and a course reversal is published. Request vectors to final or clear for full approach."
            );
        }
        bool jappNeedsProcedureTurn =
            !straightIn && procedure.ProcedureTurnLeg is not null && !jappTransitionIsNoPt && (jappTransitionContainsPi || japInterceptTooSteep);

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = finalCourse,
            FinalApproachAnchorLat = facResult.AnchorLat,
            FinalApproachAnchorLon = facResult.AnchorLon,
            StraightIn = straightIn,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure),
            MapHold = ExtractMissedApproachHold(procedure),
            MapAltitudeFt = ExtractMapAltitude(procedure),
            MapDistanceNm = ExtractMapDistance(procedure, approachRunway),
            Force = force,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        // Insert procedure turn (PI) if needed — trim approach fixes through the PT anchor.
        ProcedureTurnPhase? jappProcedureTurn = null;
        if (jappNeedsProcedureTurn && procedure.ProcedureTurnLeg is { } japPiLeg)
        {
            jappProcedureTurn = BuildProcedureTurnPhase(japPiLeg, aircraft, finalCourse);
            if (jappProcedureTurn is not null)
            {
                trimmedFixes = TrimFixesPastProcedureTurnAnchor(trimmedFixes, jappProcedureTurn.FixName);
                aircraft.Phases.Add(jappProcedureTurn);
            }
        }

        // Insert hold-in-lieu if needed (skipped when a PT is already engaged)
        if (jappProcedureTurn is null && needsHold && procedure.HoldInLieuLeg is { } holdLeg)
        {
            var holdPhase = BuildHoldInLieuPhase(holdLeg, trimmedFixes, finalCourse);
            if (holdPhase is not null)
            {
                aircraft.Phases.Add(holdPhase);
            }
        }

        if (trimmedFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = trimmedFixes });
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string baseVerb = straightIn ? "Cleared straight-in" : "Join";
        string prefix = force ? $"Force: {baseVerb.ToLowerInvariant()}" : baseVerb;
        return new CommandResult(
            true,
            $"{prefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    public static CommandResult TryPtac(PositionTurnAltitudeClearanceCommand cmd, AircraftState aircraft)
    {
        var resolved = ResolveApproach(cmd.ApproachId, null, aircraft);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        var (procedure, approachRunway, airport) = resolved;

        var facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Resolve heading: explicit or present
        int heading = (int)Math.Round(cmd.MagneticHeading?.Degrees ?? aircraft.MagneticHeading.Degrees);
        // Resolve altitude: explicit or present
        int altitude = cmd.Altitude ?? (int)(aircraft.Targets.TargetAltitude ?? aircraft.Altitude);

        // Cancel existing speed restrictions per 7110.65 §5-7-1
        aircraft.Targets.TargetSpeed = null;

        // Set heading and altitude immediately
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = new MagneticHeading(heading).ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(heading);
        aircraft.Targets.PreferredTurnDirection = null;
        aircraft.Targets.TargetAltitude = altitude;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = finalCourse,
            FinalApproachAnchorLat = facResult.AnchorLat,
            FinalApproachAnchorLon = facResult.AnchorLon,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure),
            MapHold = ExtractMissedApproachHold(procedure),
            MapAltitudeFt = ExtractMapAltitude(procedure),
            MapDistanceNm = ExtractMapDistance(procedure, approachRunway),
            Force = cmd.Forced,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = finalCourse,
                ThresholdLat = approachRunway.ThresholdLatitude,
                ThresholdLon = approachRunway.ThresholdLongitude,
                ApproachId = clearance.ApproachId,
                ForcedIntercept = cmd.Forced,
            }
        );
        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string ptacPrefix = cmd.Forced ? "Force: turn" : "Turn";
        return new CommandResult(
            true,
            $"{ptacPrefix} heading {heading:000}, maintain {altitude}, cleared {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    public static CommandResult TryClearedVisualApproach(ClearedVisualApproachCommand cmd, AircraftState aircraft)
    {
        // CVA is for IFR arrivals being vectored to a visual approach (7110.65 §7-4-3).
        // VFR pattern entry uses a different command set (TPAT etc.) and a different
        // pattern geometry (low altitude, deconflicted). Reject VFR aircraft up front
        // so they don't end up on an IFR-shaped pattern.
        if (aircraft.FlightPlan.IsVfr)
        {
            return new CommandResult(false, "CVA requires an IFR flight plan");
        }

        // RTIS gate: when following traffic, pilot must have reported traffic in sight.
        // RPO can force this with RTISF command.
        if ((cmd.FollowCallsign is not null) && !aircraft.Approach.HasReportedTrafficInSight)
        {
            return new CommandResult(false, "Traffic not in sight — issue RTIS first");
        }

        var navDb = NavigationDatabase.Instance;
        string airport;
        if (cmd.AirportCode is not null)
        {
            if (!navDb.TryResolveAirport(cmd.AirportCode, out var canonical))
            {
                return new CommandResult(false, $"Unknown airport {cmd.AirportCode.Trim().ToUpperInvariant()}");
            }
            airport = canonical;
        }
        else
        {
            airport = CommandDispatcher.ResolveAirport(aircraft);
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for visual approach");
        }

        var runway = navDb.GetRunway(airport, cmd.RunwayId);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(cmd.RunwayId)} at {airport}");
        }

        var approachRunway = runway.Designator.Equals(cmd.RunwayId, StringComparison.OrdinalIgnoreCase) ? runway : runway.ForApproach(cmd.RunwayId);

        // Cancel speed restrictions per 7110.65 §5-7-1
        aircraft.Targets.TargetSpeed = null;

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            ApproachId = $"VIS{cmd.RunwayId}",
            AirportCode = airport,
            RunwayId = cmd.RunwayId,
            FinalApproachCourse = approachRunway.TrueHeading,
            Procedure = null,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        // Determine execution path based on aircraft position
        TrueHeading finalCourse = approachRunway.TrueHeading;
        double angleOff = aircraft.TrueHeading.AbsAngleTo(finalCourse);

        if (cmd.FollowCallsign is not null)
        {
            aircraft.Approach.FollowingCallsign = cmd.FollowCallsign;
        }

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool isHeli = category == AircraftCategory.Helicopter;

        if (angleOff <= 30.0)
        {
            // Straight-in: direct to final approach + landing
            aircraft.Phases.Add(new FinalApproachPhase());
            aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());
        }
        else if (angleOff <= 90.0)
        {
            // Angled join: navigate to intercept point, then final
            double interceptDistNm = category is AircraftCategory.Jet ? 5.0 : 3.0;
            var interceptPoint = ComputeInterceptPoint(approachRunway, interceptDistNm);

            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = [new ApproachFix("INTCP", interceptPoint.Lat, interceptPoint.Lon)] });
            aircraft.Phases.Add(new FinalApproachPhase());
            aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());
        }
        else
        {
            // Pattern entry: downwind → base → final → landing.
            //
            // IFR visual approach geometry: downwind at ≥2000 ft AGL (separates from
            // standard 1000 ft VFR pattern by 1000+ ft and from 1500 ft VFR-jet pattern
            // by 500 ft). Pattern shape is unconstrained — at altitude there's no
            // conflict with low approaches / departures / arrivals on parallel runways,
            // so we skip the runway-deconfliction step that VFR pattern entry uses.
            // Authored size/altitude overrides also bypassed: those are tuned for VFR.
            var direction = cmd.TrafficDirection ?? DeterminePatternDirection(aircraft, approachRunway);

            double ifrPatternAltMsl = approachRunway.ElevationFt + IfrVisualDownwindAltAglFt;
            var waypoints = PatternGeometry.Compute(
                approachRunway,
                category,
                direction,
                sizeOverrideNm: null,
                ifrPatternAltMsl,
                airportRunways: null
            );

            var circuitPhases = PatternBuilder.BuildCircuit(
                approachRunway,
                category,
                direction,
                PatternEntryLeg.Downwind,
                false,
                null,
                patternSizeNm: null,
                ifrPatternAltMsl,
                airportRunways: null
            );

            // Check if the aircraft is already established on the downwind leg.
            // If so, start DownwindPhase directly — the along-track checks work
            // correctly when the aircraft is near the track. If NOT on the downwind,
            // insert a PatternEntryPhase to navigate there first.
            double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon),
                waypoints.DownwindHeading
            );
            double headingDiff = aircraft.TrueHeading.AbsAngleTo(waypoints.DownwindHeading);
            bool isOnDownwind = (Math.Abs(crossTrack) < 0.5) && (headingDiff < 45);

            if (!aircraft.IsOnGround && !isOnDownwind)
            {
                double distToEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon));

                if (distToEntry > 1.0)
                {
                    TrueHeading reverseDownwind = waypoints.DownwindHeading.ToReciprocal();
                    var leadIn = GeoMath.ProjectPoint(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon, reverseDownwind, 1.5);

                    aircraft.Phases.Add(
                        new PatternEntryPhase
                        {
                            EntryLat = waypoints.DownwindAbeamLat,
                            EntryLon = waypoints.DownwindAbeamLon,
                            PatternAltitude = waypoints.PatternAltitude,
                            Kind = PatternEntryPhase.ClassifyDownwindEntry(
                                aircraft.Position,
                                aircraft.TrueTrack,
                                new LatLon(approachRunway.ThresholdLatitude, approachRunway.ThresholdLongitude),
                                approachRunway.TrueHeading,
                                waypoints.DownwindHeading,
                                waypoints.Direction
                            ),
                            LeadInLat = leadIn.Lat,
                            LeadInLon = leadIn.Lon,
                        }
                    );
                }
            }

            foreach (var phase in circuitPhases)
            {
                aircraft.Phases.Add(phase);
            }
        }

        StartPhases(aircraft);

        var msg = $"Cleared visual approach runway {RunwayIdentifier.ToDisplayDesignator(cmd.RunwayId)}";
        if (cmd.FollowCallsign is not null)
        {
            msg += $", follow {cmd.FollowCallsign}";
        }
        return new CommandResult(true, msg);
    }

    private static (double Lat, double Lon) ComputeInterceptPoint(RunwayInfo runway, double distanceNm)
    {
        // Project back from threshold along the reciprocal of the runway heading
        TrueHeading reciprocal = runway.TrueHeading.ToReciprocal();
        return GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, distanceNm);
    }

    private static PatternDirection DeterminePatternDirection(AircraftState aircraft, RunwayInfo runway)
    {
        // Determine which side of the runway the aircraft is on. SignedCrossTrack
        // is positive when the aircraft is right of the runway heading direction.
        // Match the pattern to the aircraft's current side: aircraft on the right
        // → right traffic (all right turns, downwind on the right side of the
        // extended centerline); aircraft on the left → left traffic. Picking the
        // opposite side would route the aircraft through the runway centerline
        // (and any active departure corridor) on its way to the downwind.
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
            aircraft.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading
        );

        return crossTrack >= 0 ? PatternDirection.Right : PatternDirection.Left;
    }

    // --- Shared helpers ---

    internal static ResolvedApproach ResolveApproach(string? approachId, string? airportCode, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        string airport;
        if (airportCode is not null)
        {
            if (!navDb.TryResolveAirport(airportCode, out var canonical))
            {
                return ResolvedApproach.Fail($"Unknown airport {airportCode.Trim().ToUpperInvariant()}");
            }
            airport = canonical;
        }
        else
        {
            airport = CommandDispatcher.ResolveAirport(aircraft);
        }

        if (string.IsNullOrEmpty(airport))
        {
            return ResolvedApproach.Fail("Cannot determine airport for approach");
        }

        if (approachId is null)
        {
            return AutoResolveApproach(navDb, airport, aircraft);
        }

        var candidates = navDb.ResolveApproachCandidates(airport, approachId);
        if (candidates.Count == 0)
        {
            return ResolvedApproach.Fail($"Unknown approach: {approachId} at {airport}");
        }

        if (candidates.Count == 1)
        {
            return BuildResolved(navDb, airport, candidates[0]);
        }

        return DisambiguateCandidates(navDb, airport, candidates, aircraft);
    }

    /// <summary>
    /// Auto-resolve approach when bare CAPP is issued (no approach ID specified).
    /// Uses a 3-tier strategy:
    /// <list type="number">
    /// <item>Try hint sources (ExpectedApproach, DestinationRunway) in order.
    ///        For each, verify candidates exist at the airport. If the aircraft is on a nav route,
    ///        also verify route connectivity — skip disconnected hints.</item>
    /// <item>Auto-discover: enumerate all approaches at the airport, pick one that connects
    ///        to the aircraft's navigation route.</item>
    /// <item>Fail with a clear error if nothing works.</item>
    /// </list>
    /// </summary>
    private static ResolvedApproach AutoResolveApproach(NavigationDatabase navDb, string airport, AircraftState aircraft)
    {
        var knownFixes = BuildKnownFixes(aircraft);
        bool onNavRoute = aircraft.Targets.NavigationRoute.Count > 0;

        // Tier 1: try hint sources in priority order
        string?[] sources = [aircraft.Approach.Expected, aircraft.Procedure.DestinationRunway];

        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            var candidates = navDb.ResolveApproachCandidates(airport, source);
            if (candidates.Count == 0)
            {
                continue;
            }

            if (onNavRoute)
            {
                // Verify route connectivity — don't blindly use a disconnected approach
                var connected = FindConnectedCandidate(navDb, airport, candidates, knownFixes);
                if (connected is not null)
                {
                    return BuildResolved(navDb, airport, connected);
                }
                // No connectivity — try next source
                continue;
            }

            // Being vectored — accept without connectivity check
            if (candidates.Count == 1)
            {
                return BuildResolved(navDb, airport, candidates[0]);
            }

            return DisambiguateCandidates(navDb, airport, candidates, aircraft);
        }

        // Tier 2: auto-discover any approach at the airport that connects to the route
        if (onNavRoute)
        {
            var allApproaches = navDb.GetApproaches(airport);
            foreach (var proc in allApproaches)
            {
                if (HasRouteConnectivity(proc, knownFixes))
                {
                    return BuildResolved(navDb, airport, proc.ApproachId);
                }
            }
        }

        return ResolvedApproach.Fail("No approach connects to the aircraft's route — specify explicitly");
    }

    private static string? FindConnectedCandidate(
        NavigationDatabase navDb,
        string airport,
        IReadOnlyList<string> candidates,
        HashSet<string> knownFixes
    )
    {
        foreach (string candidateId in candidates)
        {
            var proc = navDb.GetApproach(airport, candidateId);
            if (proc is not null && HasRouteConnectivity(proc, knownFixes))
            {
                return candidateId;
            }
        }

        return null;
    }

    /// <summary>
    /// Disambiguate multiple approach candidates using route connectivity.
    /// Prefers ExpectedApproach if it connects, then first connected candidate, then first overall.
    /// </summary>
    private static ResolvedApproach DisambiguateCandidates(
        NavigationDatabase navDb,
        string airport,
        IReadOnlyList<string> candidates,
        AircraftState aircraft
    )
    {
        var knownFixes = BuildKnownFixes(aircraft);

        // Priority 1: ExpectedApproach, if it matches one of the candidates and connects
        if (aircraft.Approach.Expected is not null)
        {
            var expMatch = candidates.FirstOrDefault(c => c.Equals(aircraft.Approach.Expected, StringComparison.OrdinalIgnoreCase));
            if (expMatch is not null)
            {
                var expProc = navDb.GetApproach(airport, expMatch);
                if (expProc is not null && HasRouteConnectivity(expProc, knownFixes))
                {
                    return BuildResolved(navDb, airport, expMatch);
                }
            }
        }

        // Priority 2: first candidate whose fixes overlap with the aircraft's route
        foreach (string candidateId in candidates)
        {
            var proc = navDb.GetApproach(airport, candidateId);
            if (proc is not null && HasRouteConnectivity(proc, knownFixes))
            {
                return BuildResolved(navDb, airport, candidateId);
            }
        }

        // No connectivity match — fall back to first candidate (preserves prior behavior)
        return BuildResolved(navDb, airport, candidates[0]);
    }

    private static ResolvedApproach BuildResolved(NavigationDatabase navDb, string airport, string approachId)
    {
        var procedure = navDb.GetApproach(airport, approachId);
        if (procedure?.Runway is null)
        {
            return ResolvedApproach.Fail($"No runway for approach {approachId}");
        }

        var runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return ResolvedApproach.Fail($"Unknown runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")} at {airport}");
        }

        var approachRunway = runway.Designator.Equals(procedure.Runway, StringComparison.OrdinalIgnoreCase)
            ? runway
            : runway.ForApproach(procedure.Runway);

        return new ResolvedApproach(procedure, approachRunway, airport);
    }

    /// <summary>
    /// Builds the set of fix names from the aircraft's flight plan route and active nav route.
    /// Used for approach connectivity disambiguation.
    /// </summary>
    private static HashSet<string> BuildKnownFixes(AircraftState aircraft)
    {
        var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(aircraft.FlightPlan.Route))
        {
            foreach (var token in aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var dotIdx = token.IndexOf('.');
                var fixName = dotIdx >= 0 ? token[..dotIdx] : token;
                if (!string.IsNullOrEmpty(fixName))
                {
                    fixes.Add(fixName);
                }
            }
        }

        foreach (var navTarget in aircraft.Targets.NavigationRoute)
        {
            fixes.Add(navTarget.Name);
        }

        return fixes;
    }

    /// <summary>
    /// Checks whether an approach procedure has any fix (in transitions or common legs) that
    /// overlaps with the aircraft's known route fixes. Used for disambiguation when multiple
    /// approach variants match the same shorthand (e.g. I17RX vs I17RZ).
    /// </summary>
    private static bool HasRouteConnectivity(CifpApproachProcedure procedure, HashSet<string> knownFixes)
    {
        foreach (var transition in procedure.Transitions.Values)
        {
            foreach (var leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && knownFixes.Contains(leg.FixIdentifier))
                {
                    return true;
                }
            }
        }

        foreach (var leg in procedure.CommonLegs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier) && knownFixes.Contains(leg.FixIdentifier))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> GetApproachFixNames(CifpApproachProcedure procedure)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect transition fixes (all transitions — EAPP programs all of them for DCT validation)
        foreach (var transition in procedure.Transitions.Values)
        {
            foreach (var leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier))
                {
                    names.Add(leg.FixIdentifier);
                }
            }
        }

        // Collect common segment fixes (stop before MAP)
        foreach (var leg in procedure.CommonLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }
            if (leg.FixRole == CifpFixRole.MAP)
            {
                break;
            }
            names.Add(leg.FixIdentifier);
        }

        return [.. names];
    }

    /// <summary>
    /// Extracts the altitude at the missed approach point (DA for precision, MDA for non-precision)
    /// from the MAP-role leg in CommonLegs. Returns null if no MAP with altitude found.
    /// </summary>
    public static int? ExtractMapAltitude(CifpApproachProcedure procedure)
    {
        foreach (var leg in procedure.CommonLegs)
        {
            if (leg.FixRole == CifpFixRole.MAP && leg.Altitude is not null)
            {
                return leg.Altitude.Altitude1Ft;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the distance (nm) from the MAP fix to the runway threshold.
    /// Returns null if no MAP fix found or its position can't be resolved.
    /// </summary>
    public static double? ExtractMapDistance(CifpApproachProcedure procedure, RunwayInfo runway)
    {
        var navDb = NavigationDatabase.Instance;
        foreach (var leg in procedure.CommonLegs)
        {
            if (leg.FixRole != CifpFixRole.MAP || string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            return GeoMath.DistanceNm(pos.Value.Lat, pos.Value.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude);
        }

        return null;
    }

    internal static List<ApproachFix> BuildMissedApproachFixes(CifpApproachProcedure procedure)
    {
        var navDb = NavigationDatabase.Instance;
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (var leg in procedure.MissedApproachLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            if (
                leg.PathTerminator == CifpPathTerminator.RF
                && leg.ArcCenterLat is not null
                && leg.ArcCenterLon is not null
                && leg.ArcRadiusNm is not null
                && previousFixPos is not null
            )
            {
                ExpandApproachArcFixes(
                    result,
                    leg.ArcCenterLat.Value,
                    leg.ArcCenterLon.Value,
                    leg.ArcRadiusNm.Value,
                    previousFixPos.Value,
                    pos.Value,
                    leg.TurnDirection == 'R'
                );
            }

            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is not null
                && previousFixPos is not null
            )
            {
                var navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection != 'L'
                    );
                }
            }

            result.Add(
                new ApproachFix(leg.FixIdentifier, pos.Value.Lat, pos.Value.Lon, leg.Altitude, leg.Speed?.SpeedKts, leg.FixRole, leg.IsFlyOver)
            );
            previousFixPos = (pos.Value.Lat, pos.Value.Lon);
        }

        return result;
    }

    /// <summary>
    /// Build MAP navigation phases from pre-built missed approach fixes on the active approach clearance.
    /// Returns empty if no instrument approach, no procedure, or no MAP fixes.
    /// </summary>
    internal static List<Phase> BuildMissedApproachPhases(AircraftState aircraft)
    {
        var clearance = aircraft.Phases?.ActiveApproach;
        if (clearance?.Procedure is null || clearance.MissedApproachFixes.Count == 0)
        {
            return [];
        }

        // Visual approaches have no MAP
        if (clearance.ApproachId.StartsWith("VIS", StringComparison.Ordinal))
        {
            return [];
        }

        var phases = new List<Phase> { new ApproachNavigationPhase { Fixes = clearance.MissedApproachFixes } };

        if (clearance.MapHold is { } hold)
        {
            phases.Add(
                new HoldingPatternPhase
                {
                    FixName = hold.FixName,
                    FixLat = hold.FixLat,
                    FixLon = hold.FixLon,
                    InboundCourse = hold.InboundCourse,
                    LegLength = hold.LegLength,
                    IsMinuteBased = hold.IsMinuteBased,
                    Direction = hold.Direction,
                    MaxCircuits = null,
                }
            );
        }

        return phases;
    }

    /// <summary>
    /// Extract the target altitude for GoAroundPhase from the first MAP fix altitude restriction.
    /// Returns null if no altitude restriction found (caller falls back to default).
    /// </summary>
    internal static int? GetMissedApproachAltitude(IReadOnlyList<ApproachFix> mapFixes)
    {
        foreach (var fix in mapFixes)
        {
            if (fix.Altitude is { } alt)
            {
                return alt.Altitude1Ft;
            }
        }

        return null;
    }

    /// <summary>
    /// Extract holding pattern data from the MAP legs (HA/HF/HM terminator on the last fix).
    /// Returns null if no hold leg exists or the fix position can't be resolved.
    /// </summary>
    internal static MissedApproachHold? ExtractMissedApproachHold(CifpApproachProcedure procedure)
    {
        var navDb = NavigationDatabase.Instance;
        for (int i = procedure.MissedApproachLegs.Count - 1; i >= 0; i--)
        {
            var leg = procedure.MissedApproachLegs[i];
            if (leg.PathTerminator is not (CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM))
            {
                continue;
            }

            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            int inboundCourse = leg.OutboundCourse.HasValue ? (int)((leg.OutboundCourse.Value + 180) % 360) : 0;
            double legLength = leg.LegDistanceNm ?? 1.0;
            bool isMinuteBased = leg.LegDistanceNm is null;
            var direction = leg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right;

            return new MissedApproachHold(leg.FixIdentifier, pos.Value.Lat, pos.Value.Lon, inboundCourse, legLength, isMinuteBased, direction);
        }

        return null;
    }

    internal static CifpTransition? SelectBestTransition(CifpApproachProcedure procedure, AircraftState aircraft)
    {
        if (procedure.Transitions.Count == 0)
        {
            return null;
        }

        // Build set of known fixes from aircraft's flight plan route + active nav route
        var knownFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(aircraft.FlightPlan.Route))
        {
            foreach (var token in aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var dotIdx = token.IndexOf('.');
                var fixName = dotIdx >= 0 ? token[..dotIdx] : token;
                if (!string.IsNullOrEmpty(fixName))
                {
                    knownFixes.Add(fixName);
                }
            }
        }

        foreach (var navTarget in aircraft.Targets.NavigationRoute)
        {
            knownFixes.Add(navTarget.Name);
        }

        // Build set of all fix names in the approach (transitions + common legs).
        var approachFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leg in procedure.CommonLegs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier))
            {
                approachFixes.Add(leg.FixIdentifier);
            }
        }

        foreach (var transition in procedure.Transitions.Values)
        {
            foreach (var leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier))
                {
                    approachFixes.Add(leg.FixIdentifier);
                }
            }
        }

        // If the aircraft's active NavigationRoute contains an approach fix, check where
        // it lives: CommonLegs fix → no transition needed (aircraft heading into common segment);
        // transition-only fix → return that transition (aircraft needs its legs to reach CommonLegs).
        foreach (var navTarget in aircraft.Targets.NavigationRoute)
        {
            if (!approachFixes.Contains(navTarget.Name))
            {
                continue;
            }

            // If the matched fix anchors a course-reversal leg (PI/HM/HF/HA) in any transition,
            // prefer that transition even if the fix also lives in CommonLegs. The controller
            // said "DCT <fix>" implying entry at <fix> with the published course reversal.
            foreach (var transition in procedure.Transitions.Values)
            {
                foreach (var leg in transition.Legs)
                {
                    if (string.IsNullOrEmpty(leg.FixIdentifier))
                    {
                        continue;
                    }
                    if (!leg.FixIdentifier.Equals(navTarget.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (leg.PathTerminator is CifpPathTerminator.PI or CifpPathTerminator.HM or CifpPathTerminator.HF or CifpPathTerminator.HA)
                    {
                        return transition;
                    }
                }
            }

            // CommonLegs first: boundary fixes (e.g. BERKS in both CCR transition and CommonLegs)
            // must NOT trigger transition selection — aircraft is already heading to the common segment.
            bool isInCommonLegs = procedure.CommonLegs.Any(leg =>
                !string.IsNullOrEmpty(leg.FixIdentifier) && leg.FixIdentifier.Equals(navTarget.Name, StringComparison.OrdinalIgnoreCase)
            );

            if (isInCommonLegs)
            {
                return null;
            }

            // Fix is only in a transition (e.g. HIRMO) — return that transition so the full
            // fix sequence (transition legs + common legs) is built.
            foreach (var transition in procedure.Transitions.Values)
            {
                foreach (var leg in transition.Legs)
                {
                    if (!string.IsNullOrEmpty(leg.FixIdentifier) && leg.FixIdentifier.Equals(navTarget.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return transition;
                    }
                }
            }
        }

        // Try route-based match: find a transition whose fix appears in known fixes.
        if (knownFixes.Count > 0)
        {
            foreach (var transition in procedure.Transitions.Values)
            {
                foreach (var leg in transition.Legs)
                {
                    if (!string.IsNullOrEmpty(leg.FixIdentifier) && knownFixes.Contains(leg.FixIdentifier))
                    {
                        return transition;
                    }
                }
            }
        }

        // Fallback: pick the nearest IAF/IF ahead of the aircraft (within ±90° of heading),
        // considering BOTH each transition's first fix AND every common-leg IAF/IF. If the
        // winner lives in CommonLegs, return null so the caller starts the approach at that
        // fix directly via BuildApproachFixes — picking a transition just because it's the
        // nearest *transition* IAF would route an aircraft already on top of a common-leg IAF
        // backwards to the transition entry. Mirrors TrimToNearestEntry's logic at the
        // transition-selection layer.
        var navDb = NavigationDatabase.Instance;
        CifpTransition? bestTransition = null;
        bool bestIsCommonLeg = false;
        double bestDist = double.MaxValue;

        foreach (var transition in procedure.Transitions.Values)
        {
            string? firstFix = null;
            foreach (var leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier))
                {
                    firstFix = leg.FixIdentifier;
                    break;
                }
            }

            if (firstFix is null)
            {
                continue;
            }

            var pos = navDb.GetFixPosition(firstFix);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            double angleDiff = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTransition = transition;
                bestIsCommonLeg = false;
            }
        }

        foreach (var leg in procedure.CommonLegs)
        {
            if (leg.FixRole is not (CifpFixRole.IAF or CifpFixRole.IF))
            {
                continue;
            }
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            double angleDiff = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTransition = null;
                bestIsCommonLeg = true;
            }
        }

        return bestIsCommonLeg ? null : bestTransition;
    }

    /// <summary>
    /// Build a one-circuit HoldingPatternPhase from the procedure's HILPT leg, anchored
    /// at whichever fix in <paramref name="approachFixes"/> matches the leg's identifier.
    /// Returns null if the matching fix isn't present (e.g. user prepended a DCT/AT that
    /// trimmed past it). Used by both CAPP (when transition contains HF/HM/HA) and JAPP
    /// (when procedure.HasHoldInLieu and not straight-in).
    /// </summary>
    private static HoldingPatternPhase? BuildHoldInLieuPhase(CifpLeg holdLeg, IReadOnlyList<ApproachFix> approachFixes, TrueHeading finalCourse)
    {
        var holdFix = approachFixes.FirstOrDefault(f => f.Name.Equals(holdLeg.FixIdentifier, StringComparison.OrdinalIgnoreCase));
        if (holdFix is null)
        {
            return null;
        }

        int inboundCourse = holdLeg.OutboundCourse.HasValue ? (int)((holdLeg.OutboundCourse.Value + 180) % 360) : (int)finalCourse.Degrees;

        return new HoldingPatternPhase
        {
            FixName = holdFix.Name,
            FixLat = holdFix.Latitude,
            FixLon = holdFix.Longitude,
            InboundCourse = inboundCourse,
            LegLength = holdLeg.LegDistanceNm ?? 1.0,
            IsMinuteBased = holdLeg.LegDistanceNm is null,
            Direction = holdLeg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right,
            MaxCircuits = 1,
        };
    }

    /// <summary>
    /// Build a <see cref="ProcedureTurnPhase"/> from the procedure's PI leg. The PT anchor
    /// fix must already be loadable from the navdata (CCR for KCCR S19R). The CIFP outbound
    /// course is published as magnetic; convert to true using the aircraft's local declination.
    /// </summary>
    private static ProcedureTurnPhase? BuildProcedureTurnPhase(CifpLeg piLeg, AircraftState aircraft, TrueHeading finalCourse)
    {
        var navDb = NavigationDatabase.Instance;
        var pos = navDb.GetFixPosition(piLeg.FixIdentifier);
        if (pos is null)
        {
            return null;
        }

        double publishedPtMagDeg = piLeg.OutboundCourse ?? finalCourse.ToMagnetic(aircraft.Declination).Degrees;
        var ptOutboundTrue = new MagneticHeading(publishedPtMagDeg).ToTrue(aircraft.Declination);

        int minAlt = piLeg.Altitude is { } restriction ? restriction.Altitude1Ft : 0;

        return new ProcedureTurnPhase
        {
            FixName = piLeg.FixIdentifier,
            FixLat = pos.Value.Lat,
            FixLon = pos.Value.Lon,
            InboundCourseDeg = finalCourse.Degrees,
            PtOutboundCourseDeg = ptOutboundTrue.Degrees,
            MaxOutboundDistanceNm = piLeg.LegDistanceNm ?? 10.0,
            OneEightyTurnDirection = piLeg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right,
            MinAltitudeFt = minAlt,
        };
    }

    /// <summary>
    /// When a <see cref="ProcedureTurnPhase"/> is being inserted ahead of the approach navigation,
    /// trim approach fixes through the LAST occurrence of the PT anchor fix. Anything before/at
    /// the anchor is consumed by the PT (it ends established inbound at/near the anchor); only
    /// post-PT fixes remain for <see cref="ApproachNavigationPhase"/>.
    /// </summary>
    private static List<ApproachFix> TrimFixesPastProcedureTurnAnchor(List<ApproachFix> fixes, string anchorName)
    {
        int lastIdx = -1;
        for (int i = fixes.Count - 1; i >= 0; i--)
        {
            if (fixes[i].Name.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
            {
                lastIdx = i;
                break;
            }
        }

        if (lastIdx < 0)
        {
            return fixes;
        }

        return fixes.GetRange(lastIdx + 1, fixes.Count - lastIdx - 1);
    }

    private static List<ApproachFix> BuildApproachFixesWithTransition(CifpTransition transition, CifpApproachProcedure procedure)
    {
        var transitionFixes = BuildFixesFromLegs(transition.Legs, stopAtMahp: false);
        var commonFixes = BuildFixesFromLegs(procedure.CommonLegs, stopAtMahp: true);

        // Trim common-leg fixes that the transition has already passed. Standard case
        // (e.g. FRA transition ending at DLRAY, common starts at DLRAY): drop the index-0
        // duplicate. HILPT case (e.g. MOD transition ending at ZELAT(HF), common is
        // [DLRAY, ZELAT(FAF), RW28R]): the transition's HF leg establishes the aircraft
        // inbound at the FAF, so drop everything in common up to and including ZELAT —
        // DLRAY is the IAF for a different feeder (FRA/PXN) and would route the aircraft
        // ~13 nm past the IAF on the wrong side of the FAC.
        if (transitionFixes.Count > 0 && commonFixes.Count > 0)
        {
            string transitionEndFix = transitionFixes[^1].Name;
            int idxInCommon = commonFixes.FindIndex(f => f.Name.Equals(transitionEndFix, StringComparison.OrdinalIgnoreCase));
            if (idxInCommon >= 0)
            {
                commonFixes.RemoveRange(0, idxInCommon + 1);
            }
        }

        transitionFixes.AddRange(commonFixes);
        return transitionFixes;
    }

    private static List<ApproachFix> BuildApproachFixes(CifpApproachProcedure procedure)
    {
        return BuildFixesFromLegs(procedure.CommonLegs, stopAtMahp: true);
    }

    private static List<ApproachFix> BuildFixesFromLegs(IReadOnlyList<CifpLeg> legs, bool stopAtMahp)
    {
        var navDb = NavigationDatabase.Instance;
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (var leg in legs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            // Skip past MAP (missed approach) — those are in MissedApproachLegs already
            if (stopAtMahp && leg.FixRole == CifpFixRole.MAP)
            {
                break;
            }

            // Skip procedure turn legs (handled by hold-in-lieu)
            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            // RF leg: expand arc from previous fix to terminator fix
            if (
                leg.PathTerminator == CifpPathTerminator.RF
                && leg.ArcCenterLat is not null
                && leg.ArcCenterLon is not null
                && leg.ArcRadiusNm is not null
                && previousFixPos is not null
            )
            {
                ExpandApproachArcFixes(
                    result,
                    leg.ArcCenterLat.Value,
                    leg.ArcCenterLon.Value,
                    leg.ArcRadiusNm.Value,
                    previousFixPos.Value,
                    pos.Value,
                    leg.TurnDirection == 'R'
                );
            }

            // AF leg: expand DME arc from previous fix to terminator fix
            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is not null
                && previousFixPos is not null
            )
            {
                var navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection != 'L'
                    );
                }
            }

            result.Add(
                new ApproachFix(
                    leg.FixIdentifier,
                    pos.Value.Lat,
                    pos.Value.Lon,
                    leg.Altitude,
                    leg.Speed?.SpeedKts,
                    leg.FixRole,
                    leg.IsFlyOver || leg.FixRole is CifpFixRole.FAF or CifpFixRole.MAP
                )
            );
            previousFixPos = (pos.Value.Lat, pos.Value.Lon);
        }

        return result;
    }

    private static void ExpandApproachArcFixes(
        List<ApproachFix> result,
        double centerLat,
        double centerLon,
        double radiusNm,
        (double Lat, double Lon) previousFix,
        (double Lat, double Lon) terminatorFix,
        bool turnRight
    )
    {
        double startBearing = GeoMath.BearingTo(centerLat, centerLon, previousFix.Lat, previousFix.Lon);
        double endBearing = GeoMath.BearingTo(centerLat, centerLon, terminatorFix.Lat, terminatorFix.Lon);

        var arcPoints = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight);

        // Insert intermediate points (skip the last one — that's the terminator fix)
        for (int i = 0; i < arcPoints.Count - 1; i++)
        {
            result.Add(new ApproachFix($"ARC{i + 1:D2}", arcPoints[i].Lat, arcPoints[i].Lon));
        }
    }

    private static List<ApproachFix> TrimToNavRouteConnection(List<ApproachFix> fixes, AircraftState aircraft)
    {
        if (fixes.Count == 0)
        {
            return fixes;
        }

        var navNames = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < fixes.Count; i++)
        {
            if (navNames.Contains(fixes[i].Name))
            {
                return fixes.GetRange(i, fixes.Count - i);
            }
        }

        return fixes;
    }

    private static ApproachClearance BuildClearance(
        CifpApproachProcedure procedure,
        string airport,
        FinalApproachCourseResult fac,
        RunwayInfo approachRunway,
        bool force
    )
    {
        return new ApproachClearance
        {
            ApproachId = procedure.ApproachId,
            AirportCode = airport,
            RunwayId = procedure.Runway!,
            FinalApproachCourse = fac.Course,
            FinalApproachAnchorLat = fac.AnchorLat,
            FinalApproachAnchorLon = fac.AnchorLon,
            Procedure = procedure,
            MissedApproachFixes = BuildMissedApproachFixes(procedure),
            MapHold = ExtractMissedApproachHold(procedure),
            MapAltitudeFt = ExtractMapAltitude(procedure),
            MapDistanceNm = ExtractMapDistance(procedure, approachRunway),
            Force = force,
        };
    }

    private static bool NavRouteContainsFix(AircraftState aircraft, string fixName)
    {
        foreach (var target in aircraft.Targets.NavigationRoute)
        {
            if (target.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends approach fixes to the NavigationRoute after the connecting fix.
    /// The first fix in approachFixes is the connecting fix (already in the route),
    /// so we skip it and insert the remaining fixes after it.
    /// </summary>
    private static void AppendApproachFixesToNavRoute(AircraftState aircraft, List<ApproachFix> approachFixes)
    {
        if (approachFixes.Count == 0)
        {
            return;
        }

        string connectingFix = approachFixes[0].Name;
        var route = aircraft.Targets.NavigationRoute;

        // Find the connecting fix in the route
        int insertAfter = -1;
        for (int i = 0; i < route.Count; i++)
        {
            if (route[i].Name.Equals(connectingFix, StringComparison.OrdinalIgnoreCase))
            {
                insertAfter = i;
                break;
            }
        }

        if (insertAfter < 0)
        {
            return;
        }

        // Superseding deferred CAPP: drop the prior approach tail after the STAR connecting fix.
        if (insertAfter + 1 < route.Count)
        {
            route.RemoveRange(insertAfter + 1, route.Count - insertAfter - 1);
        }

        // Convert approach fixes after the connecting fix to NavigationTargets and insert
        var newTargets = new List<NavigationTarget>();
        for (int i = 1; i < approachFixes.Count; i++)
        {
            var fix = approachFixes[i];
            newTargets.Add(
                new NavigationTarget
                {
                    Name = fix.Name,
                    Position = new LatLon(fix.Latitude, fix.Longitude),
                    AltitudeRestriction = fix.Altitude,
                    SpeedRestriction = fix.SpeedKts is { } kts ? new CifpSpeedRestriction(kts, CifpSpeedRestrictionType.AtOrBelow) : null,
                    IsFlyOver = fix.IsFlyOver,
                }
            );
        }

        route.InsertRange(insertAfter + 1, newTargets);
    }

    /// <summary>
    /// Activates a pending approach clearance. Called from FlightPhysics when the
    /// NavigationRoute empties and a PendingApproachClearance exists.
    /// </summary>
    public static void ActivatePendingApproach(AircraftState aircraft, PendingApproachInfo pending)
    {
        aircraft.Approach.PendingClearance = null;

        // Clear STAR state so stale descent logic doesn't conflict with approach phases
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaFloor = null;

        aircraft.Phases = new PhaseList { AssignedRunway = pending.AssignedRunway, ActiveApproach = pending.Clearance };
        aircraft.Phases.Add(new FinalApproachPhase());
        var isHeli = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(ctx);
    }

    private static List<ApproachFix> TrimToNearestEntry(List<ApproachFix> fixes, AircraftState aircraft)
    {
        if (fixes.Count == 0)
        {
            return fixes;
        }

        // Find nearest IAF or IF ahead of aircraft
        int bestIndex = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < fixes.Count; i++)
        {
            var fix = fixes[i];
            if (fix.Role is not (CifpFixRole.IAF or CifpFixRole.IF))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(fix.Latitude, fix.Longitude));
            double angleDiff = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));

            // Only consider fixes within ±90° of current heading
            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(fix.Latitude, fix.Longitude));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            return fixes.GetRange(bestIndex, fixes.Count - bestIndex);
        }

        // Fallback: use all fixes
        return fixes;
    }

    /// <summary>Clears a deferred approach clearance so it cannot activate after an immediate approach.</summary>
    internal static void ClearPendingApproach(AircraftState aircraft)
    {
        aircraft.Approach.PendingClearance = null;
    }

    /// <summary>
    /// Clears arrival procedure state when the destination airport or routing context is superseded
    /// (e.g. APT to a new airport). Does not clear departure-only fields such as <see cref="AircraftProcedure.DepartureRunway"/>,
    /// and does not tear down ground-departure phases (taxi, lineup, takeoff) when the aircraft is not in an
    /// arrival-approach context.
    /// </summary>
    internal static void ClearArrivalProcedureState(AircraftState aircraft)
    {
        bool hadArrivalApproach =
            aircraft.Approach.PendingClearance is not null
            || aircraft.Phases?.ActiveApproach is not null
            || IsArrivalApproachPhase(aircraft.Phases?.CurrentPhase);

        if (hadArrivalApproach && aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        ClearPendingApproach(aircraft);
        aircraft.Approach.Expected = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Procedure.DestinationRunway = null;
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaFloor = null;
        aircraft.Approach.HasReportedFieldInSight = false;
        aircraft.Approach.HasReportedTrafficInSight = false;
        Phases.AirborneFollowHelper.ClearFollowState(aircraft);
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
    }

    private static bool IsArrivalApproachPhase(Phase? phase) =>
        phase is ApproachNavigationPhase or InterceptCoursePhase or FinalApproachPhase or HoldingPatternPhase or ProcedureTurnPhase;

    private static void ClearExistingPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        // Clear STAR state so stale descent logic doesn't conflict with approach phases
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaFloor = null;

        ClearPendingApproach(aircraft);

        // Clear visual approach state
        aircraft.Approach.HasReportedFieldInSight = false;
        aircraft.Approach.HasReportedTrafficInSight = false;
        Phases.AirborneFollowHelper.ClearFollowState(aircraft);
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
    }

    private static void StartPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is not null)
        {
            var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Start(startCtx);
        }
    }

    internal readonly record struct ResolvedApproach
    {
        public bool Success { get; }
        public string? Error { get; }
        public CifpApproachProcedure? Procedure { get; }
        public RunwayInfo? Runway { get; }
        public string? Airport { get; }

        public ResolvedApproach(CifpApproachProcedure procedure, RunwayInfo runway, string airport)
        {
            Success = true;
            Procedure = procedure;
            Runway = runway;
            Airport = airport;
        }

        private ResolvedApproach(string error)
        {
            Success = false;
            Error = error;
        }

        public static ResolvedApproach Fail(string error) => new(error);

        public void Deconstruct(out CifpApproachProcedure procedure, out RunwayInfo runway, out string airport)
        {
            procedure = Procedure!;
            runway = Runway!;
            airport = Airport!;
        }
    }
}
