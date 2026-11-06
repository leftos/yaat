using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim;

/// <summary>
/// Per-tick scenario inputs <see cref="FlightPhysics.Update(AircraftState, double, Func{string, AircraftState?}?, WeatherProfile?, double, PhysicsTickOptions)"/>
/// needs beyond the aircraft itself: the pilot-transmission routing flags and the session's magnetic-model day
/// (<see cref="Simulation.SimScenarioState.MagneticModelDateUtc"/>) for the declination refresh.
/// </summary>
public readonly record struct PhysicsTickOptions(bool SoloTrainingMode, bool RpoShowPilotSpeech, DateTime MagneticModelDateUtc)
{
    /// <summary>Instructor topology, no RPO speech, process-day magnetic model — the standalone/test default.</summary>
    public static PhysicsTickOptions Default => new(false, false, MagneticDeclination.EvaluationDateUtc);
}

public static class FlightPhysics
{
    private static readonly ILogger Log = SimLog.CreateLogger("FlightPhysics");

    private const double HeadingSnapDeg = 0.5;

    /// <summary>Altitude capture tolerance in feet — inside this band the aircraft is "at" its goal.</summary>
    internal const double AltitudeSnapFt = 10.0;

    /// <summary>AIM 4-4-10.4 level-off band: within this many feet of the assigned altitude the rate tapers.</summary>
    private const double LevelOffBandFt = 1000.0;

    /// <summary>Minimum vertical rate (fpm) inside the level-off band, per the AIM's 500–1,500 fpm guidance.</summary>
    private const double LevelOffMinFpm = 500.0;

    /// <summary>Vertical rate (fpm) entering the level-off band — the top of the AIM's 500–1,500 fpm guidance.</summary>
    private const double LevelOffMaxFpm = 1500.0;

    /// <summary>Within this many feet of its top altitude, a climb via counts as reached and ends.</summary>
    private const double ClimbViaTopReachedToleranceFt = 20.0;

    private const double SpeedSnapKts = 2.0;
    private const double NavArrivalNm = 0.5;
    private const double FrdArrivalNm = 1.5;
    private const double GroundArrivalNm = 0.05;
    private const double RadialInterceptDeg = 3.0;
    private const double FrdMissThresholdNm = 5.0;
    private const double FrdMissDepartureNm = 0.5;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmPerDegLat = 60.0;

    public static void Update(AircraftState aircraft, double deltaSeconds) =>
        Update(aircraft, deltaSeconds, aircraftLookup: null, weather: null, simTimeSeconds: 0, PhysicsTickOptions.Default);

    public static void Update(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup) =>
        Update(aircraft, deltaSeconds, aircraftLookup, weather: null, simTimeSeconds: 0, PhysicsTickOptions.Default);

    public static void Update(
        AircraftState aircraft,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather,
        double simTimeSeconds
    ) => Update(aircraft, deltaSeconds, aircraftLookup, weather, simTimeSeconds, PhysicsTickOptions.Default);

    public static void Update(
        AircraftState aircraft,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather,
        double simTimeSeconds,
        PhysicsTickOptions options
    )
    {
        bool soloTrainingMode = options.SoloTrainingMode;
        bool rpoShowPilotSpeech = options.RpoShowPilotSpeech;
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);

        RefreshDeclinationCache(aircraft, options.MagneticModelDateUtc);

        // Backward compat: airborne aircraft without IAS initialized derive it from GS.
        if (!aircraft.IsOnGround && aircraft.IndicatedAirspeed <= 0 && aircraft.GroundSpeed > 0)
        {
            aircraft.IndicatedAirspeed = aircraft.GroundSpeed;
        }

        double windPhaseSeconds = WindVariation.PhaseSecondsFor(aircraft.Callsign);
        UpdateNavigation(aircraft, weather, simTimeSeconds, windPhaseSeconds);
        aircraft.Targets.PlannedVerticalRate = null;
        UpdateDescentPlanning(aircraft, cat);
        UpdateClimbPlanning(aircraft, cat);
        UpdateSpeedPlanning(aircraft, cat);
        UpdateHeading(aircraft, cat, deltaSeconds);
        UpdateAltitude(aircraft, cat, deltaSeconds);
        UpdateSpeed(aircraft, cat, deltaSeconds);
        AutoCancelSpeedAtFinal(aircraft);
        UpdatePosition(aircraft, deltaSeconds, weather, simTimeSeconds, windPhaseSeconds);
        UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup);
        UpdateGroundStationaryTimer(aircraft, deltaSeconds);
        UpdateGiveWayResume(aircraft, aircraftLookup, deltaSeconds);
        PilotObservationUpdater.Update(aircraft, aircraftLookup, weather, simTimeSeconds, options);
    }

    /// <summary>
    /// Recomputes the cached magnetic declination when the aircraft has moved materially. WMM is a
    /// degree-12 spherical-harmonic evaluation (~0.06 ms/call) — at 4 Hz per aircraft this dominates
    /// per-tick physics. Declination changes ~0.01°/km IRL, so reusing a cached value for sub-nm
    /// motion is invisible for heading/wind use. Shared with the live-traffic kinematics, which
    /// bypass <see cref="Update"/> but still need magnetic readouts.
    /// </summary>
    public static void RefreshDeclinationCache(AircraftState aircraft, DateTime magneticModelDateUtc)
    {
        const double DeclinationCacheThresholdDeg = 0.02;
        if (
            aircraft.DeclinationCachePosition is not { } cached
            || (Math.Abs(aircraft.Position.Lat - cached.Lat) > DeclinationCacheThresholdDeg)
            || (Math.Abs(aircraft.Position.Lon - cached.Lon) > DeclinationCacheThresholdDeg)
        )
        {
            // Skip declination update if position is non-finite or out of range. Geo.Coordinate's
            // ctor would throw, crashing the tick. Logging the bad state lets the upstream cause
            // be investigated without taking down the sim. Keep the previously cached value.
            if (
                !double.IsFinite(aircraft.Position.Lat)
                || !double.IsFinite(aircraft.Position.Lon)
                || Math.Abs(aircraft.Position.Lat) > 90
                || Math.Abs(aircraft.Position.Lon) > 180
            )
            {
                Log.LogError(
                    "Aircraft position out of range, skipping declination update: callsign={CS} pos=({Lat},{Lon}) phase={Phase}",
                    aircraft.Callsign,
                    aircraft.Position.Lat,
                    aircraft.Position.Lon,
                    aircraft.Phases?.CurrentPhase?.Name ?? "(none)"
                );
            }
            else
            {
                aircraft.Declination = MagneticDeclination.GetDeclination(aircraft.Position, magneticModelDateUtc);
                aircraft.DeclinationCachePosition = aircraft.Position;
            }
        }
    }

    private static void UpdateNavigation(AircraftState aircraft, WeatherProfile? weather, double simTimeSeconds, double windPhaseSeconds)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            if (aircraft.Approach.PendingClearance is { } pendingEarly)
            {
                ApproachCommandHandler.ActivatePendingApproach(aircraft, pendingEarly);
            }

            return;
        }

        NavigationTarget nav = route[0];
        double distNm = GeoMath.DistanceNm(aircraft.Position, nav.Position);

        (double threshold, double anticipationNm) = SequencingThreshold(aircraft, aircraft.Position, nav, (route.Count >= 2) ? route[1] : null);
        bool inAnticipationZone = (anticipationNm > NavArrivalNm) && (distNm < threshold);

        // Sequence waypoint: for fly-by with anticipation, wait until the aircraft
        // has passed abeam the waypoint (along-track goes negative along the next leg bearing).
        // For fly-over or last waypoint, sequence at NavArrivalNm.
        bool shouldSequence;
        if (inAnticipationZone && route.Count >= 2)
        {
            double nextLegBearing = GeoMath.BearingTo(nav.Position, route[1].Position);
            double alongTrack = GeoMath.AlongTrackDistanceNmRaw(aircraft.Position, nav.Position, nextLegBearing);
            shouldSequence = alongTrack >= 0;
        }
        else
        {
            shouldSequence = distNm < NavArrivalNm;
        }

        if (shouldSequence)
        {
            NavigationTarget sequenced = nav;

            // Read while the fix is on the route: an enroute transition counts as flown only while one of its own fixes is
            // ahead, so past a transition's last fix the published speed a controller replaced there could not be found.
            CifpSpeedRestriction? viaPublishedSpeed =
                (aircraft.Procedure.ProcedureSpeedsInForce && IsControllerSetSpeed(sequenced))
                    ? Commands.NavigationCommandHandler.ViaProcedureSpeedAt(aircraft, sequenced.Name)
                    : null;
            route.RemoveAt(0);

            // Fire any AT fix triggers in the command queue for this fix
            NotifyFixSequenced(aircraft, sequenced.Name);

            // Restore altitude/speed from revert fields when sequencing past a constrained fix
            if (sequenced.RevertAltitude is not null)
            {
                aircraft.Targets.TargetAltitude = sequenced.RevertAltitude;
                aircraft.Targets.AssignedAltitude = sequenced.RevertAssignedAltitude;
                aircraft.Targets.DesiredVerticalRate = null;
            }
            else
            {
                ReleaseClimbCeilingPastFix(aircraft, sequenced, route);
                ReleaseDescentFloorPastFix(aircraft, sequenced, route);
            }

            // Read while the fix still carries it: a resume written with the crossing ends its speed at the fix, so the
            // crossing must not leave that speed as a ceiling — not even where the reverts are cleared instead of applied.
            bool resumesAtFix = sequenced.RevertResume is not null;
            RestoreSpeedPastFix(aircraft, sequenced, route);

            // Under a via clearance a crossing speed the controller set ends at its fix (7110.65 5-7-2.e), and a speed the
            // procedure publishes there is still the speed crossed (5-7-1.e NOTE, 5-7-1.g NOTE 3). With no via clearance
            // the crossing speed holds until ATC ends it.
            bool controllerCrossingEnds = aircraft.Procedure.ProcedureSpeedsInForce && IsControllerSetSpeed(sequenced);
            CifpSpeedRestriction? crossedRestriction = sequenced.SpeedRestriction;
            if (controllerCrossingEnds)
            {
                crossedRestriction = viaPublishedSpeed;
                MoveAssignedSpeedPastControllerCrossing(aircraft, sequenced, route);
            }

            if (controllerCrossingEnds || !IsControllerSetSpeed(sequenced))
            {
                RecordCrossedPublishedSpeed(aircraft, crossedRestriction);
            }

            // AIM 5-4-1 NOTE 2 / 7110.65 §5-7-1.g NOTE 3: once at a "cross at" speed the
            // pilot maintains it until ATC reassigns or the approach speed takes over.
            // Publish the crossed restriction as a ceiling so the auto altitude-band schedule
            // cannot accelerate the aircraft back to default cruise while flying the remaining
            // (often unrestricted) fixes. An explicit ATC speed wins and is left untouched, and so does a Mach the fix
            // restores (RestoreMachPastFix), which decides the speed past it as an explicit one does, and an RNS, RPS or
            // DSR given with the crossing (RevertResume), which ends the crossing speed at the fix.
            if (
                (crossedRestriction is { } crossedSpeed)
                && !aircraft.Targets.HasExplicitSpeedCommand
                && (sequenced.RevertMach is null)
                && !resumesAtFix
            )
            {
                if (crossedSpeed.Type == CifpSpeedRestrictionType.AtOrAbove)
                {
                    // A "cross at or above" speed is a minimum (ARINC 424 speed-descriptor coding;
                    // the phraseology analogue is 7110.65 §5-7-2.a.2, "maintain (speed) knots or
                    // greater"). Publish it as a floor so the aircraft holds at or above it past
                    // the fix — never a ceiling that would cap it to the minimum.
                    aircraft.Targets.SpeedFloor = aircraft.Targets.SpeedFloor is { } existingFloor
                        ? Math.Max(existingFloor, crossedSpeed.SpeedKts)
                        : crossedSpeed.SpeedKts;
                }
                else
                {
                    double ceilingKts = ClampFixSpeedToApproachFloor(aircraft, crossedSpeed.SpeedKts);
                    aircraft.Targets.SpeedCeiling = aircraft.Targets.SpeedCeiling is { } existingCeiling
                        ? Math.Min(existingCeiling, ceilingKts)
                        : ceilingKts;

                    if (controllerCrossingEnds)
                    {
                        aircraft.Procedure.LastProcedureSpeedKts = ceilingKts;
                    }
                }
            }

            if (route.Count == 0)
            {
                if (aircraft.Approach.PendingClearance is { } pending)
                {
                    ApproachCommandHandler.ActivatePendingApproach(aircraft, pending);
                    return;
                }

                // AIM 5-4-1 NOTE 2: after the procedure's terminating fix the pilot
                // maintains the last published speed until ATC intervenes. Publish
                // it as a ceiling so the auto speed schedule cannot accelerate the
                // aircraft above it. An explicit ATC speed (HasExplicitSpeedCommand)
                // wins — leave the procedural memory aside in that case.
                if (!aircraft.Targets.HasExplicitSpeedCommand && aircraft.Procedure.LastProcedureSpeedKts is { } lastProcSpeed)
                {
                    // Lowering only, like the crossed-restriction stamp above: a ceiling another source already set
                    // lower is a constraint in its own right and the procedural memory must not raise it.
                    aircraft.Targets.SpeedCeiling = aircraft.Targets.SpeedCeiling is { } existingProcCeiling
                        ? Math.Min(existingProcCeiling, lastProcSpeed)
                        : lastProcSpeed;
                }

                // FM terminator (most US STARs): fly the published outbound course and await
                // vectors, instead of holding the heading the aircraft happened to arrive on.
                aircraft.Targets.TargetTrueHeading = sequenced.TerminalCourseMagnetic is { } fmCourse
                    ? new MagneticHeading(fmCourse).ToTrue(aircraft.Declination)
                    : null;
                aircraft.Targets.PreferredTurnDirection = null;
                ClearProcedureState(aircraft);
                return;
            }

            nav = route[0];
            ApplySequencedFixConstraints(aircraft, nav);
        }

        // Compute steering heading
        double bearing;
        if (inAnticipationZone && !shouldSequence && route.Count >= 2)
        {
            // Arc-blended steering: compute tangent heading along inscribed turn circle
            double currentLegBearing = GeoMath.BearingTo(aircraft.Position, nav.Position);
            double nextLegBearing = GeoMath.BearingTo(nav.Position, route[1].Position);
            double turnRate =
                aircraft.Targets.TurnRateOverride
                ?? AircraftPerformance.TurnRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
            bearing = ComputeArcBlendedHeading(aircraft.Position, aircraft.GroundSpeed, turnRate, nav.Position, currentLegBearing, nextLegBearing);
        }
        else
        {
            bearing = GeoMath.BearingTo(aircraft.Position, nav.Position);
        }

        // Apply wind correction angle so the aircraft flies a straight ground track, not a pursuit curve.
        double wca = 0;
        if (weather is not null && !aircraft.IsOnGround && aircraft.IndicatedAirspeed > 0)
        {
            double tas = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
            var at = new WindSamplePoint(aircraft.Position, aircraft.Declination);
            WindAtAltitude wind = WindInterpolator.GetWindAt(weather, at, aircraft.Altitude, simTimeSeconds, windPhaseSeconds);
            wca = WindInterpolator.ComputeWindCorrectionAngle(bearing, tas, wind.DirectionDeg, wind.SpeedKts);
        }

        aircraft.Targets.TargetTrueHeading = new TrueHeading(bearing + wca);
        // PreferredTurnDirection is NOT cleared here. It is cleared when the heading
        // is reached (UpdateHeading snap) or when the route is exhausted. Clearing it
        // every tick would stomp on direction preferences set by departure phases
        // (e.g., TRDCT/TLDCT) before the initial turn completes.
    }

    /// <summary>
    /// Restores what a fix's crossing speed replaced, once the aircraft sequences past the fix or a route change drops it
    /// (<see cref="Commands.NavigationCommandHandler.ApplyTakenSpeedReverts"/>): SMAX's re-arm past the last crossing the
    /// controller set (<see cref="Commands.NavigationCommandHandler.PlaceSpeedMaxRevert"/>), else the Mach hold, else the
    /// RNS, RPS or DSR given with the crossing (<see cref="Commands.FlightCommandHandler.ApplySpeedResume"/>), else the
    /// plain speed and its assignment. In a hold or inside the final gate nothing is restored and every revert goes.
    /// </summary>
    internal static void RestoreSpeedPastFix(AircraftState aircraft, NavigationTarget sequenced, List<NavigationTarget> route)
    {
        // A hold flies the holding phase's speed and an arrival inside 5 nm final its approach speed (7110.65 §5-7-1.b.4):
        // no revert is applied there, and every revert on the route goes rather than waiting for a fix past the gate.
        if (Commands.FlightCommandHandler.SpeedRevertForbidden(aircraft))
        {
            // The fix just passed is already off the route, so its own reverts go here and the rest with the route.
            Commands.NavigationCommandHandler.ClearSpeedReverts(sequenced);
            Commands.NavigationCommandHandler.DropSpeedReverts(aircraft);
            return;
        }

        if (sequenced.RevertSpeedMax)
        {
            // A later crossing speed the controller set is flown first: SMAX comes back past the last one.
            if (!Commands.NavigationCommandHandler.PlaceSpeedMaxRevert(route))
            {
                Commands.FlightCommandHandler.ArmSpeedMax(aircraft, aircraft.Targets.SpeedCommandIsControllerIssued);
            }

            return;
        }

        if (sequenced.RevertMach is { } revertMach)
        {
            // The Mach hold the fix's speed replaced comes back past the fix, as SMAX's re-arm does.
            RestoreMachPastFix(aircraft, sequenced, revertMach);
            return;
        }

        if (sequenced.RevertResume is { } resume)
        {
            // An RNS, RPS or DSR given with the crossing applies once the crossing is flown.
            Commands.FlightCommandHandler.ApplySpeedResume(aircraft, resume);
            return;
        }

        if ((sequenced.RevertSpeed is not null) || (sequenced.RevertSpeedFloor is not null) || (sequenced.RevertSpeedCeiling is not null))
        {
            aircraft.Targets.TargetSpeed = sequenced.RevertSpeed;
            aircraft.Targets.AssignedSpeed = sequenced.RevertAssignedSpeed;
        }

        // A bound comes back only when the capture held one; with none, a bound in force at the fix (a published "at or
        // above" crossed since the CFIX) stays.
        if (sequenced.RevertSpeedFloor is { } floor)
        {
            aircraft.Targets.SpeedFloor = floor;
        }

        if (sequenced.RevertSpeedCeiling is { } ceiling)
        {
            aircraft.Targets.SpeedCeiling = ceiling;
        }
    }

    /// <summary>
    /// Restores the Mach hold a fix's speed replaced once the aircraft sequences past it, as SMAX's re-arm restores SMAX.
    /// At or below <see cref="MachHandOverAltitudeFt"/>, or on a descent below it whose Mach already reaches
    /// <see cref="MachHandOverThresholdKts"/>, <see cref="HandOverMachHold"/> would hand the Mach over on the very
    /// next tick, so the pilot resumes it there as an IAS instead: an assignment when the Mach was an explicit speed
    /// command, a target speed only when nobody assigned it. Otherwise the Mach comes back as a Mach. Either way it carries
    /// the explicit-speed flag it held when the fix's speed replaced it.
    /// </summary>
    private static void RestoreMachPastFix(AircraftState aircraft, NavigationTarget sequenced, double mach)
    {
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.HasExplicitSpeedCommand = sequenced.RevertMachExplicit ?? true;
        if (RestoredMachHandsOver(aircraft, mach))
        {
            double ias = RestoredMachIas(aircraft.AircraftType, mach, aircraft.Altitude);
            Log.LogDebug(
                "{Callsign}: Mach {Mach:F2} restored past {Fix} as {Speed:F0} kt at {Altitude:F0} ft",
                aircraft.Callsign,
                mach,
                sequenced.Name,
                ias,
                aircraft.Altitude
            );
            // A Mach nobody assigned (live traffic) resumes as a target speed only, as the handover gives it.
            aircraft.Targets.TargetMach = null;
            aircraft.Targets.AssignedSpeed = aircraft.Targets.HasExplicitSpeedCommand ? ias : null;
            aircraft.Targets.TargetSpeed = ias;
            return;
        }

        aircraft.Targets.TargetMach = mach;
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
    }

    /// <summary>
    /// True when the Mach restored past a fix is handed over as an IAS on the restore tick: at or below
    /// <see cref="MachHandOverAltitudeFt"/>, or on a descent below it whose Mach's IAS equivalent already reaches
    /// <see cref="MachHandOverThresholdKts"/>. SMAX keeps its own Mach hold and a helicopter its envelope, as
    /// <see cref="HandOverMachHold"/> does.
    /// </summary>
    private static bool RestoredMachHandsOver(AircraftState aircraft, double mach)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if ((cat == AircraftCategory.Helicopter) || aircraft.IsOnGround || aircraft.Targets.SpeedMaxHeld)
        {
            return false;
        }

        return AtOrBelowHandOverAltitude(aircraft)
            || (
                DescendingBelowHandOverAltitude(aircraft)
                && (WindInterpolator.MachToIas(mach, aircraft.Altitude) >= MachHandOverThresholdKts(aircraft.AircraftType, cat))
            );
    }

    /// <summary>
    /// The IAS a Mach restored past a fix resumes as where the handover would fire: none higher than the profile's FL100
    /// descent speed or Vmo less <see cref="SpeedMaxMarginKts"/>, and none higher than the Mach's own IAS equivalent.
    /// </summary>
    private static double RestoredMachIas(string aircraftType, double mach, double altitudeFt)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraftType);
        double capped = Math.Min(SpeedMaxKts(aircraftType) ?? double.MaxValue, WindInterpolator.MachToIas(mach, altitudeFt));
        return Math.Round(Math.Min(capped, DescentSpeedKts(aircraftType, cat)));
    }

    /// <summary>
    /// Descent planning, except on a SID (<see cref="AircraftProcedure.SidViaMode"/>), where only climb planning handles
    /// constraints. Under a descend via the step descent targets the next depicted altitude
    /// (<see cref="UpdateDescendViaPlanning"/>); off it the target comes from the descent scan over the route
    /// (<see cref="PlanDescentScan"/>): the cleared altitude above every in-force floor still ahead, at the rate the bounds
    /// before the binding floor need. The rate is published in <see cref="ControlTargets.PlannedVerticalRate"/>, which
    /// <see cref="Update"/> clears before every run of this method: it therefore lives only as long as a constrained fix is
    /// in the route, and a vector that clears the route returns the aircraft to its profile descent rate on the next tick.
    /// </summary>
    private static void UpdateDescentPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if ((aircraft.IsOnGround) || (aircraft.GroundSpeed <= 0) || (aircraft.Procedure.SidViaMode))
        {
            return;
        }

        // Activate only when some route fix carries an altitude restriction in force
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (!HasInForceAltitudeRestriction(aircraft, route))
        {
            return;
        }

        if (aircraft.Procedure.StarViaMode)
        {
            UpdateDescendViaPlanning(aircraft, cat, route);
            return;
        }

        DescentScanPlan plan = PlanDescentScan(aircraft, cat, route, 0);
        if ((plan.TargetFt is { } target) && (target < aircraft.Altitude))
        {
            aircraft.Targets.TargetAltitude = target;
            aircraft.Targets.PlannedVerticalRate = -plan.RateFpm;
        }
    }

    /// <summary>
    /// Step-descent planning under a descend via. Finds the next altitude constraint in the route and computes the descent
    /// rate required to meet it at the sequencing point, adjusting the rate (steeper or shallower) rather than using a fixed
    /// rate with a distance-based trigger: one constraint at a time.
    /// </summary>
    private static void UpdateDescendViaPlanning(AircraftState aircraft, AircraftCategory cat, List<NavigationTarget> route)
    {
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            }

            if ((route[i].AltitudeRestriction is not { } restriction) || !AltitudeRestrictionInForce.IsInForce(aircraft, restriction))
            {
                continue;
            }

            if (PlanDescendViaFix(aircraft, cat, restriction, SequencingDistanceNm(aircraft, route, i, cumulativeDistNm)))
            {
                break; // Step descent: only target the next constraint
            }
        }
    }

    /// <summary>
    /// Targets the depicted altitude <paramref name="restriction"/> calls for under a descend via, never below the via's
    /// floor (<see cref="AircraftProcedure.StarViaFloor"/>), at the rate that reaches it where the fix sequences,
    /// <paramref name="sequencingDistNm"/> ahead. A via clearance flies to the depicted altitudes, so the cleared altitude
    /// never meets a bound (<see cref="ClearedAltitudeMeetingBound"/>) and each restriction resolves as a descent. False when
    /// the restriction calls for no descent, and the step descent looks at the next one.
    /// </summary>
    private static bool PlanDescendViaFix(AircraftState aircraft, AircraftCategory cat, CifpAltitudeRestriction restriction, double sequencingDistNm)
    {
        // Only a descent is planned: a depicted altitude at or above the aircraft calls for none
        if ((ResolveAltitudeRestriction(aircraft, restriction, isDescending: true) is not { } targetAlt) || (targetAlt >= aircraft.Altitude))
        {
            return false;
        }

        if (aircraft.Procedure.StarViaFloor is { } floor)
        {
            targetAlt = Math.Max(targetAlt, floor);
        }

        double altDelta = aircraft.Altitude - targetAlt;
        if (altDelta <= 0)
        {
            return false;
        }

        aircraft.Targets.TargetAltitude = targetAlt;

        // Compute the descent rate required to reach targetAlt where the fix sequences
        double standardRate = AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude);
        double timeMinutes = sequencingDistNm / (aircraft.GroundSpeed / 60.0);

        // Cap at 2× standard rate; no minimum — use a gentle rate if there's plenty of distance. Almost at the
        // sequencing point the time is floored at one physics tick: on track the required rate holds steady and arrives on
        // the last tick, never steepening into the level-off.
        aircraft.Targets.PlannedVerticalRate = -Math.Min(altDelta / Math.Max(timeMinutes, 1.0 / 60), standardRate * 2.0);
        return true;
    }

    /// <summary>
    /// Climb planning off a climb via or descend via. The target comes from the climb scan over the route
    /// (<see cref="PlanClimbScan"/>): the cleared or top altitude under every in-force ceiling still ahead, at the rate the
    /// bounds before the binding ceiling need. The rate is published in <see cref="ControlTargets.PlannedVerticalRate"/>,
    /// which <see cref="Update"/> clears before every run of this method: it therefore lives only as long as a constrained
    /// fix is in the route, and a vector that clears the route returns the aircraft to its profile climb rate on the next
    /// tick. A climb via owns its own target (<see cref="ApplyClimbViaAltitude"/>).
    /// </summary>
    private static void UpdateClimbPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if (aircraft.IsOnGround || aircraft.GroundSpeed <= 0)
        {
            return;
        }

        // When StarViaMode is active, only descent planning handles constraints
        if (aircraft.Procedure.StarViaMode)
        {
            return;
        }

        // A climb via flies to its top altitude at the profile rate, held only under a ceiling still ahead.
        if (ApplyClimbViaAltitude(aircraft))
        {
            return;
        }

        // Activate only when some route fix carries an altitude restriction in force
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (!HasInForceAltitudeRestriction(aircraft, route))
        {
            return;
        }

        ClimbScanPlan plan = PlanClimbScan(aircraft, cat, route, 0);
        if ((plan.TargetFt is { } target) && (target > aircraft.Altitude))
        {
            aircraft.Targets.TargetAltitude = target;
            aircraft.Targets.PlannedVerticalRate = plan.RateFpm;
        }
    }

    /// <summary>Whether some fix on <paramref name="route"/> carries an altitude restriction in force for <paramref name="aircraft"/>.</summary>
    private static bool HasInForceAltitudeRestriction(AircraftState aircraft, List<NavigationTarget> route) =>
        route.Exists(t => (t.AltitudeRestriction is { } r) && AltitudeRestrictionInForce.IsInForce(aircraft, r));

    /// <summary>
    /// What the climb scan plans: the altitude to climb to, null when no bound acted, and the rate the bounds before the
    /// binding ceiling need (null: the profile rate).
    /// </summary>
    private readonly record struct ClimbScanPlan(double? TargetFt, double? RateFpm);

    /// <summary>What one in-force restriction does to the climb scan.</summary>
    private enum ClimbScanStep
    {
        /// <summary>The scan goes on to the next fix.</summary>
        Continue,

        /// <summary>The restriction needs a descent: the scan ends there.</summary>
        NeedsDescent,
    }

    /// <summary>The scanned route and where the scan started on it, for the stand-in top a SID-fallback departure reads lazily.</summary>
    private readonly record struct ClimbScanRoute(List<NavigationTarget> Route, int Start);

    /// <summary>A climb scan's running state (<see cref="PlanClimbScan"/>).</summary>
    private struct ClimbScanState
    {
        /// <summary>The running target: the cleared or top altitude, lowered by each ceiling ahead, or raised by a floor.</summary>
        public double? TargetFt;

        /// <summary>The steepest rate a bound before the binding ceiling needs.</summary>
        public double? RateFpm;

        /// <summary>Whether any bound acted, so the scan has a target to plan.</summary>
        public bool Acted;

        /// <summary>Whether a ceiling below the running target has bound it: floors beyond wait until its fix sequences.</summary>
        public bool Bound;

        /// <summary>Whether the cleared or top altitude is still to be read (a SID-fallback departure's stand-in top).</summary>
        public bool TopPending;
    }

    /// <summary>
    /// The climb the in-force restrictions on <paramref name="route"/> from <paramref name="start"/> plan, off a climb via
    /// (which <see cref="ApplyClimbViaAltitude"/> owns) and off a descend via. The target is the cleared or top altitude
    /// (<see cref="ClimbClearedOrTopAltitude"/>) under every in-force ceiling still ahead (an at, a glideslope intercept, an
    /// at-or-below, a window's top), up to a nearer restriction that needs a descent, which ends the scan. A ceiling at or
    /// above the running target neither binds nor ends the scan; the first one below it binds, and floors beyond it are not
    /// planned until its fix sequences. Before that, a floor (an at-or-above, a window's bottom, an at) and a bound the
    /// cleared altitude already meets only set the required rate, the steepest one needed winning; a controller's
    /// at-or-above floor above the cleared altitude is complied with and becomes the target. With no cleared or top altitude,
    /// an at or a floor of any source is the altitude to fly, and an at-or-below sets none. The target is null when no bound
    /// acted, so a route whose bounds are all already met leaves the target alone. Shared by the climb planner
    /// (<see cref="UpdateClimbPlanning"/>), fix sequencing (<see cref="ApplyClimbScanAtFix"/>) and the release past a
    /// ceiling's fix (<see cref="ReleaseClimbCeilingPastFix"/>), so the clamp has one judge.
    /// </summary>
    private static ClimbScanPlan PlanClimbScan(AircraftState aircraft, AircraftCategory cat, List<NavigationTarget> route, int start)
    {
        var scanned = new ClimbScanRoute(route, start);
        var state = new ClimbScanState { TargetFt = aircraft.Targets.AssignedAltitude, TopPending = aircraft.Targets.AssignedAltitude is null };
        double standardRate = AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, aircraft.Altitude);
        double cumulativeDistNm = 0;
        for (int i = start; i < route.Count; i++)
        {
            LatLon from = (i == start) ? aircraft.Position : route[i - 1].Position;
            cumulativeDistNm += GeoMath.DistanceNm(from, route[i].Position);
            if ((route[i].AltitudeRestriction is not { } restriction) || !AltitudeRestrictionInForce.IsInForce(aircraft, restriction))
            {
                continue;
            }

            var leg = new ClimbScanLeg(SequencingDistanceNm(aircraft, route, i, cumulativeDistNm), standardRate);
            if (ApplyBoundToClimbScan(aircraft, restriction, leg, scanned, ref state) == ClimbScanStep.NeedsDescent)
            {
                break;
            }
        }

        return new ClimbScanPlan(state.Acted ? state.TargetFt : null, state.RateFpm);
    }

    /// <summary>A scanned fix's distance to where it sequences and the aircraft's standard climb rate.</summary>
    private readonly record struct ClimbScanLeg(double SequencingDistNm, double StandardRate);

    /// <summary>
    /// Applies one in-force restriction to the climb scan's <paramref name="state"/> (<see cref="PlanClimbScan"/>). Each
    /// bound's needed rate is the one that reaches it where its fix sequences: for a bound the cleared altitude meets,
    /// <see cref="BoundCrossingRate"/>; for any other, <see cref="NeededClimbRate"/>.
    /// </summary>
    private static ClimbScanStep ApplyBoundToClimbScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        ClimbScanLeg leg,
        ClimbScanRoute scanned,
        ref ClimbScanState state
    )
    {
        if (ClearedAltitudeMeetingBound(aircraft, restriction) is not null)
        {
            ApplyMetBoundToClimbScan(aircraft, restriction, leg, ref state);
            return ClimbScanStep.Continue;
        }

        // Only climbs are planned here; descent is handled by UpdateDescentPlanning. A nearer restriction that needs a
        // descent ends the scan: a climb for one beyond it would undo that descent before its fix.
        bool isDescending = (!aircraft.Procedure.SidViaMode) && (aircraft.Altitude > restriction.Altitude1Ft);
        double? resolved = ResolveAltitudeRestriction(aircraft, restriction, isDescending);
        if ((resolved is { } resolvedFt) && (resolvedFt < aircraft.Altitude - AltitudeSnapFt))
        {
            return ClimbScanStep.NeedsDescent;
        }

        ResolvePendingClimbTop(aircraft, scanned, ref state);
        if (IsClimbCeiling(restriction))
        {
            ApplyCeilingToClimbScan(aircraft, restriction, leg, ref state);
        }
        else
        {
            ApplyFloorToClimbScan(aircraft, restriction, resolved, leg, ref state);
        }

        return ClimbScanStep.Continue;
    }

    /// <summary>Reads the cleared or top altitude the first time the scan needs it (a SID-fallback departure's stand-in top).</summary>
    private static void ResolvePendingClimbTop(AircraftState aircraft, ClimbScanRoute scanned, ref ClimbScanState state)
    {
        if (state.TopPending)
        {
            state.TargetFt = ClimbClearedOrTopAltitude(aircraft, scanned.Route, scanned.Start);
            state.TopPending = false;
        }
    }

    /// <summary>A bound the cleared altitude meets before the binding ceiling: it sets the rate that crosses it in time.</summary>
    private static void ApplyMetBoundToClimbScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        ClimbScanLeg leg,
        ref ClimbScanState state
    )
    {
        if (state.Bound)
        {
            return;
        }

        state.Acted = true;
        double feetToBound = FeetShortOfBound(aircraft.Altitude, restriction, descending: false);
        if (feetToBound > 0)
        {
            state.RateFpm = SteeperRate(state.RateFpm, BoundCrossingRate(feetToBound, leg.SequencingDistNm, aircraft.GroundSpeed, leg.StandardRate));
        }
    }

    /// <summary>
    /// A ceiling ahead lowers the running target to its value; one below the target binds it. With no cleared or top
    /// altitude an at, or a window whose bottom is above the aircraft, is itself the altitude to fly, and an at-or-below
    /// sets none but still binds. Before the binding, an at's value and a window's bottom set the rate as floors.
    /// </summary>
    private static void ApplyCeilingToClimbScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        ClimbScanLeg leg,
        ref ClimbScanState state
    )
    {
        bool wasBound = state.Bound;
        double? floorFt = (restriction.Type == CifpAltitudeRestrictionType.Between) ? restriction.Altitude2Ft : restriction.Altitude1Ft;
        if (state.TargetFt is { } running)
        {
            state.Acted = true;
            if (restriction.Altitude1Ft < running)
            {
                state.TargetFt = restriction.Altitude1Ft;
                state.Bound = true;
            }
        }
        else
        {
            ApplyNoTopCeiling(aircraft, restriction, floorFt, ref state);
        }

        bool hasFloor = restriction.Type is not CifpAltitudeRestrictionType.AtOrBelow;
        if ((!wasBound) && hasFloor && (floorFt is { } f))
        {
            state.RateFpm = SteeperRate(state.RateFpm, FloorRate(aircraft, f, state.TargetFt, leg));
        }
    }

    /// <summary>
    /// A ceiling with no cleared or top altitude: an at or a glideslope intercept targets its value, a window its bottom
    /// when that is above the aircraft; either binds, as does an at-or-below, which sets no target.
    /// </summary>
    private static void ApplyNoTopCeiling(AircraftState aircraft, CifpAltitudeRestriction restriction, double? floorFt, ref ClimbScanState state)
    {
        if (state.Bound)
        {
            return;
        }

        state.Bound = true;
        if (restriction.Type is CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept)
        {
            state.TargetFt = restriction.Altitude1Ft;
            state.Acted = true;
        }
        else if ((restriction.Type == CifpAltitudeRestrictionType.Between) && (floorFt is { } lower) && (lower > aircraft.Altitude))
        {
            state.TargetFt = lower;
            state.Acted = true;
        }
    }

    /// <summary>
    /// An at-or-above floor <paramref name="resolved"/> above the aircraft, before the binding ceiling (any other restriction
    /// that is not a ceiling does nothing). With no cleared or top altitude it is the altitude to fly, whoever wrote it; a
    /// controller's floor above the cleared altitude is complied with and becomes the target until its fix sequences; past
    /// it the aircraft flies its assigned altitude (<see cref="ReleaseDescentFloorPastFix"/>). Any other floor only sets the
    /// required rate.
    /// </summary>
    private static void ApplyFloorToClimbScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        double? resolved,
        ClimbScanLeg leg,
        ref ClimbScanState state
    )
    {
        if (
            (state.Bound)
            || (restriction.Type != CifpAltitudeRestrictionType.AtOrAbove)
            || (resolved is not { } floor)
            || (floor <= aircraft.Altitude)
        )
        {
            return;
        }

        state.Acted = true;
        if ((state.TargetFt is not { } current) || ((restriction.Source == CifpAltitudeRestrictionSource.Atc) && (floor > current)))
        {
            state.TargetFt = floor;
        }

        state.RateFpm = SteeperRate(state.RateFpm, FloorRate(aircraft, floor, state.TargetFt, leg));
    }

    /// <summary>The rate a floor at <paramref name="floorFt"/> needs, toward no higher than <paramref name="target"/>.</summary>
    private static double? FloorRate(AircraftState aircraft, double floorFt, double? target, ClimbScanLeg leg)
    {
        if (target is not { } t)
        {
            return null;
        }

        return NeededClimbRate(Math.Min(floorFt, t) - aircraft.Altitude, leg.SequencingDistNm, aircraft.GroundSpeed, leg.StandardRate);
    }

    /// <summary>
    /// The climb rate that covers <paramref name="altDelta"/> where the fix sequences, capped at twice
    /// <paramref name="standardRate"/>; no minimum, so a gentle rate if there is plenty of distance. Almost at the sequencing
    /// point the time is floored at one physics tick: on track the required rate holds steady and arrives on the last tick,
    /// never steepening into the level-off. Null when nothing is left to climb.
    /// </summary>
    private static double? NeededClimbRate(double altDelta, double sequencingDistNm, double groundSpeedKts, double standardRate)
    {
        if (altDelta <= 0)
        {
            return null;
        }

        double timeMinutes = sequencingDistNm / (groundSpeedKts / 60.0);
        return Math.Min(altDelta / Math.Max(timeMinutes, 1.0 / 60), standardRate * 2.0);
    }

    /// <summary>The steeper of two needed climb rates; null when neither is needed.</summary>
    private static double? SteeperRate(double? a, double? b) =>
        (a, b) switch
        {
            ({ } x, { } y) => Math.Max(x, y),
            (null, _) => b,
            _ => a,
        };

    /// <summary>Whether <paramref name="restriction"/> caps a climb: an at, a glideslope intercept, an at-or-below or a window.</summary>
    private static bool IsClimbCeiling(CifpAltitudeRestriction restriction) =>
        restriction.Type
            is CifpAltitudeRestrictionType.At
                or CifpAltitudeRestrictionType.GlideSlopeIntercept
                or CifpAltitudeRestrictionType.AtOrBelow
                or CifpAltitudeRestrictionType.Between;

    /// <summary>
    /// The altitude a climb scan flies to with nothing capping it, off a climb via: the cleared altitude
    /// (<see cref="ControlTargets.AssignedAltitude"/>), else, for a SID departure released with no controller altitude (its
    /// published altitudes in force through <see cref="AltitudeRestrictionInForce.ProcedureAltitudesInForce"/>'s fallback),
    /// the SID's stand-in top (<see cref="NavigationCommandHandler.HighestPublishedSidAltitudeFt"/>), or the highest in-force
    /// published bound on the scanned fixes when the SID cannot be resolved. Null otherwise.
    /// </summary>
    private static double? ClimbClearedOrTopAltitude(AircraftState aircraft, List<NavigationTarget> route, int start)
    {
        if (aircraft.Targets.AssignedAltitude is { } assigned)
        {
            return assigned;
        }

        if ((!aircraft.Procedure.SidViaMode) || (!AltitudeRestrictionInForce.ProcedureAltitudesInForce(aircraft)))
        {
            return null;
        }

        return NavigationCommandHandler.HighestPublishedSidAltitudeFt(aircraft) ?? HighestInForceProcedureBoundFt(aircraft, route, start);
    }

    /// <summary>
    /// The highest in-force published bound on <paramref name="route"/> from <paramref name="start"/>: a window's top,
    /// any other's value.
    /// </summary>
    private static double? HighestInForceProcedureBoundFt(AircraftState aircraft, List<NavigationTarget> route, int start)
    {
        double? highest = null;
        for (int i = start; i < route.Count; i++)
        {
            if (
                (route[i].AltitudeRestriction is { Source: CifpAltitudeRestrictionSource.Procedure } restriction)
                && AltitudeRestrictionInForce.IsInForce(aircraft, restriction)
            )
            {
                highest = Math.Max(highest ?? 0, Math.Max(restriction.Altitude1Ft, restriction.Altitude2Ft ?? 0));
            }
        }

        return highest;
    }

    /// <summary>
    /// Past the fix of a ceiling that capped the climb, the climb resumes at once to the cleared or top altitude, under the
    /// ceilings on the remaining route (<see cref="PlanClimbScan"/>), when that is above the present altitude
    /// (AIM 5-2-9.e.5). Called only where the fix carries no revert: a crossing's revert decides the altitude past its fix.
    /// </summary>
    private static void ReleaseClimbCeilingPastFix(AircraftState aircraft, NavigationTarget sequenced, List<NavigationTarget> route)
    {
        if (!ReleasesClimbPastFix(aircraft, sequenced))
        {
            return;
        }

        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double? target = PlanClimbScan(aircraft, cat, route, 0).TargetFt ?? ClimbClearedOrTopAltitude(aircraft, route, 0);
        if ((target is { } climbTo) && (climbTo > aircraft.Altitude))
        {
            Log.LogDebug("{Callsign}: past {Fix}, climb resumes to {Target}", aircraft.Callsign, sequenced.Name, climbTo);
            aircraft.Targets.TargetAltitude = climbTo;
        }
    }

    /// <summary>
    /// Whether <paramref name="sequenced"/> held an in-force climb ceiling a controller or the procedure wrote, off a climb
    /// via (which re-targets itself) and a descend via.
    /// </summary>
    private static bool ReleasesClimbPastFix(AircraftState aircraft, NavigationTarget sequenced) =>
        (!aircraft.Procedure.StarViaMode)
        && (aircraft.Procedure.ClimbViaTargetFt is null)
        && (sequenced.AltitudeRestriction is { } passed)
        && (passed.Source is CifpAltitudeRestrictionSource.Atc or CifpAltitudeRestrictionSource.Procedure)
        && IsClimbCeiling(passed)
        && AltitudeRestrictionInForce.IsInForce(aircraft, passed);

    /// <summary>
    /// What the descent scan plans: the altitude to descend to, null when no bound acted, and the rate magnitude the bounds
    /// before the binding floor need (null: the profile rate).
    /// </summary>
    private readonly record struct DescentScanPlan(double? TargetFt, double? RateFpm);

    /// <summary>
    /// A scanned fix's distance to where it sequences, the aircraft's standard descent rate, and whether the cleared
    /// altitude meets the fix's bound (<see cref="ClearedAltitudeMeetingBound"/>).
    /// </summary>
    private readonly record struct DescentScanLeg(double SequencingDistNm, double StandardRate, bool Met);

    /// <summary>A descent scan's running state (<see cref="PlanDescentScan"/>).</summary>
    private struct DescentScanState
    {
        /// <summary>
        /// The running target: the cleared altitude, raised by each floor ahead, lowered by a ceiling or an at below it, or
        /// lowered by an approach floor below it.
        /// </summary>
        public double? TargetFt;

        /// <summary>The highest floor seen so far on the scanned route: no ceiling takes the target below it.</summary>
        public double? FloorFt;

        /// <summary>The steepest rate magnitude a bound before the binding needs.</summary>
        public double? RateFpm;

        /// <summary>Whether any bound acted, so the scan has a target to plan.</summary>
        public bool Acted;

        /// <summary>
        /// Whether the target is bound: by a floor that raised it, by an earlier floor that stopped a ceiling, or, with no
        /// cleared altitude, by the first bound that set it. Ceilings beyond wait until its fix sequences.
        /// </summary>
        public bool Bound;

        /// <summary>The lowest ceiling seen so far on the scanned route: no later floor lifts the target above it.</summary>
        public double? CeilingFt;
    }

    /// <summary>
    /// The descent the in-force restrictions on <paramref name="route"/> from <paramref name="start"/> plan, off a descend
    /// via (<see cref="UpdateDescendViaPlanning"/>) and a SID. Every in-force floor still ahead (an at-or-above, a window's
    /// bottom, an at's or a glideslope intercept's value), met or not, is a lower limit: the aircraft cannot descend below it
    /// and climb back (AIM 4-4-10.e). The target starts at the cleared altitude (<see cref="ControlTargets.AssignedAltitude"/>);
    /// a floor above it raises and binds it, never above an earlier ceiling or at (the nearer restriction is met first,
    /// AIM 4-4-10.a). A ceiling (an at-or-below, a window's top, an at's value) lowers the target to itself, never
    /// below an earlier floor still ahead, which then binds; ceilings beyond the binding wait until its fix sequences, and
    /// those before it set the required rate, the steepest one needed winning. An in-force approach floor below the target
    /// lowers it to that floor unless the cleared altitude meets it (AIM 5-4-6.h.1). With no cleared altitude the first bound
    /// that calls for a descent binds: an at, a glideslope intercept, an at-or-below, a window's top or an approach floor is
    /// the altitude to descend to, and a controller's or a procedure's at-or-above sets none. The target is null when no
    /// bound acted. Shared by the descent
    /// planner (<see cref="UpdateDescentPlanning"/>), fix sequencing (<see cref="ApplyDescendingFixBound"/>) and the release
    /// past a floor's fix (<see cref="ReleaseDescentFloorPastFix"/>), so the clamp has one judge.
    /// </summary>
    private static DescentScanPlan PlanDescentScan(AircraftState aircraft, AircraftCategory cat, List<NavigationTarget> route, int start)
    {
        var state = new DescentScanState { TargetFt = aircraft.Targets.AssignedAltitude };
        double standardRate = AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude);
        double cumulativeDistNm = 0;
        for (int i = start; i < route.Count; i++)
        {
            LatLon from = (i == start) ? aircraft.Position : route[i - 1].Position;
            cumulativeDistNm += GeoMath.DistanceNm(from, route[i].Position);
            if ((route[i].AltitudeRestriction is not { } restriction) || !AltitudeRestrictionInForce.IsInForce(aircraft, restriction))
            {
                continue;
            }

            var leg = new DescentScanLeg(SequencingDistanceNm(aircraft, route, i, cumulativeDistNm), standardRate, Met: false);
            ApplyBoundToDescentScan(aircraft, restriction, leg, ref state);
        }

        return new DescentScanPlan(state.Acted ? state.TargetFt : null, state.RateFpm);
    }

    /// <summary>
    /// Applies one in-force restriction to the descent scan's <paramref name="state"/> (<see cref="PlanDescentScan"/>). Each
    /// bound's needed rate is the one that reaches it where its fix sequences: for a bound the cleared altitude meets,
    /// <see cref="BoundCrossingRate"/>; for any other, <see cref="NeededDescentRate"/>.
    /// </summary>
    private static void ApplyBoundToDescentScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        DescentScanLeg leg,
        ref DescentScanState state
    )
    {
        double? cleared = ClearedAltitudeMeetingBound(aircraft, restriction);
        RecordDescentLimits(restriction, ref state);

        // At or below its cleared altitude already, the aircraft has nothing to descend for a bound that altitude meets
        if ((cleared is { } c) && (aircraft.Altitude - c <= AltitudeSnapFt))
        {
            return;
        }

        if (state.TargetFt is null)
        {
            ApplyNoClearedBoundToDescentScan(aircraft, restriction, leg, ref state);
            return;
        }

        DescentScanLeg judged = leg with { Met = cleared is not null };
        ApplyCeilingToDescentScan(aircraft, restriction, judged, ref state);
        ApplyFloorToDescentScan(DescentFloorFt(restriction), ref state);
        ApplyApproachFloorToDescentScan(aircraft, restriction, judged, ref state);
    }

    /// <summary>
    /// Records <paramref name="restriction"/>'s floor and ceiling in the scan's limits, met or not: the highest floor seen so
    /// far and the lowest ceiling.
    /// </summary>
    private static void RecordDescentLimits(CifpAltitudeRestriction restriction, ref DescentScanState state)
    {
        if (DescentFloorFt(restriction) is { } floor)
        {
            state.FloorFt = Math.Max(state.FloorFt ?? floor, floor);
        }

        if (DescentCeilingFt(restriction) is { } ceiling)
        {
            state.CeilingFt = Math.Min(state.CeilingFt ?? ceiling, ceiling);
        }
    }

    /// <summary>The floor <paramref name="restriction"/> puts under a descent: an at-or-above's, an at's, a window's bottom.</summary>
    private static double? DescentFloorFt(CifpAltitudeRestriction restriction) =>
        restriction.Type switch
        {
            CifpAltitudeRestrictionType.AtOrAbove or CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept =>
                restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.Between => restriction.Altitude2Ft,
            _ => null,
        };

    /// <summary>The ceiling <paramref name="restriction"/> puts over a descent: an at-or-below's, an at's, a window's top.</summary>
    private static double? DescentCeilingFt(CifpAltitudeRestriction restriction) =>
        restriction.Type is CifpAltitudeRestrictionType.AtOrAbove ? null : restriction.Altitude1Ft;

    /// <summary>
    /// A bound with no cleared altitude and no target yet: the first that calls for a descent sets the target and binds. An
    /// at, a glideslope intercept, an at-or-below or a window's top is the altitude to descend to, never below an earlier
    /// floor still ahead, and so is an approach floor (AIM 5-4-6.h.1); a controller's or a procedure's at-or-above sets none.
    /// </summary>
    private static void ApplyNoClearedBoundToDescentScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        DescentScanLeg leg,
        ref DescentScanState state
    )
    {
        double? targetFt =
            (DescentCeilingFt(restriction) is { } ceiling) ? Math.Max(ceiling, state.FloorFt ?? ceiling)
            : (restriction.Source == CifpAltitudeRestrictionSource.Approach) ? state.FloorFt
            : null;
        if ((targetFt is not { } target) || (target >= aircraft.Altitude))
        {
            return;
        }

        state.TargetFt = target;
        state.Acted = true;
        state.Bound = true;
        state.RateFpm = SteeperRate(state.RateFpm, NeededDescentRate(aircraft, target, leg));
    }

    /// <summary>
    /// A ceiling ahead (an at-or-below, a window's top, an at's value) before the binding lowers the running target to
    /// itself, never below an earlier floor still ahead: that floor then binds the target. It sets the rate that reaches its
    /// limit where its fix sequences, for a bound the cleared altitude meets the one that crosses it in time.
    /// </summary>
    private static void ApplyCeilingToDescentScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        DescentScanLeg leg,
        ref DescentScanState state
    )
    {
        if ((state.Bound) || (DescentCeilingFt(restriction) is not { } ceiling) || (state.TargetFt is not { } running))
        {
            return;
        }

        double limit = Math.Max(ceiling, state.FloorFt ?? ceiling);
        state.Acted = true;
        state.RateFpm = SteeperRate(state.RateFpm, CeilingRate(aircraft, restriction, limit, leg));
        state.TargetFt = Math.Min(running, limit);
        if (limit > ceiling)
        {
            state.Bound = true;
        }
    }

    /// <summary>
    /// The rate magnitude a ceiling needs: for one the cleared altitude meets, the rate that crosses it in time
    /// (<see cref="BoundCrossingRate"/>); for any other, the one that reaches <paramref name="limitFt"/>
    /// (<see cref="NeededDescentRate"/>).
    /// </summary>
    private static double? CeilingRate(AircraftState aircraft, CifpAltitudeRestriction restriction, double limitFt, DescentScanLeg leg)
    {
        if (!leg.Met)
        {
            return NeededDescentRate(aircraft, limitFt, leg);
        }

        double feetToBound = FeetShortOfBound(aircraft.Altitude, restriction, descending: true);
        return (feetToBound > 0) ? BoundCrossingRate(feetToBound, leg.SequencingDistNm, aircraft.GroundSpeed, leg.StandardRate) : null;
    }

    /// <summary>
    /// A floor ahead (<paramref name="floorFt"/>: an at-or-above, a window's bottom, an at's value) above the running target
    /// raises and binds it, never above the lowest ceiling or at seen so far: the nearer restriction is met first, and a
    /// floor beyond it waits until that fix sequences. A floor at or below the target acts without binding.
    /// </summary>
    private static void ApplyFloorToDescentScan(double? floorFt, ref DescentScanState state)
    {
        if ((floorFt is not { } floor) || (state.TargetFt is not { } running))
        {
            return;
        }

        state.Acted = true;
        double raised = Math.Min(floor, state.CeilingFt ?? floor);
        if (raised > running)
        {
            state.TargetFt = raised;
            state.Bound = true;
        }
    }

    /// <summary>
    /// An in-force approach floor below the running target, before the binding and not met by the cleared altitude, lowers
    /// it to the highest floor seen (AIM 5-4-6.h.1: on a cleared approach, the aircraft may descend to the minimum altitude
    /// listed for the segment), at the rate that reaches it where its fix sequences. A window the cleared altitude already
    /// meets keeps the cleared altitude.
    /// </summary>
    private static void ApplyApproachFloorToDescentScan(
        AircraftState aircraft,
        CifpAltitudeRestriction restriction,
        DescentScanLeg leg,
        ref DescentScanState state
    )
    {
        if (
            (restriction.Source != CifpAltitudeRestrictionSource.Approach)
            || (leg.Met)
            || (DescentFloorFt(restriction) is null)
            || (state.Bound)
            || (state.FloorFt is not { } floor)
            || (state.TargetFt is not { } running)
            || (floor >= running)
        )
        {
            return;
        }

        state.TargetFt = floor;
        state.RateFpm = SteeperRate(state.RateFpm, NeededDescentRate(aircraft, floor, leg));
    }

    /// <summary>
    /// The descent rate magnitude that reaches <paramref name="toFt"/> where the scanned fix sequences: the same formula as a
    /// climb's (<see cref="NeededClimbRate"/>), capped at twice the standard rate with no minimum, since how a descent is
    /// flown before the fix is the pilot's choice (AIM 4-4-10.e). Null when nothing is left to descend.
    /// </summary>
    private static double? NeededDescentRate(AircraftState aircraft, double toFt, DescentScanLeg leg) =>
        NeededClimbRate(aircraft.Altitude - toFt, leg.SequencingDistNm, aircraft.GroundSpeed, leg.StandardRate);

    /// <summary>
    /// Past the fix of a floor, the aircraft resumes its cleared altitude at once, above the floors on the remaining route
    /// (<see cref="PlanDescentScan"/>), when that is below the present altitude: an at-or-above is not an altitude to
    /// maintain (AIM 4-4-10.e), so this fires whether the aircraft descended to the floor or climbed to it. Called only where
    /// the fix carries no revert: a crossing's revert decides the altitude past its fix.
    /// </summary>
    private static void ReleaseDescentFloorPastFix(AircraftState aircraft, NavigationTarget sequenced, List<NavigationTarget> route)
    {
        if (!ReleasesDescentPastFix(aircraft, sequenced))
        {
            return;
        }

        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double? target = PlanDescentScan(aircraft, cat, route, 0).TargetFt ?? aircraft.Targets.AssignedAltitude;
        if ((target is { } descendTo) && (descendTo < aircraft.Altitude))
        {
            Log.LogDebug("{Callsign}: past {Fix}, descent resumes to {Target}", aircraft.Callsign, sequenced.Name, descendTo);
            aircraft.Targets.TargetAltitude = descendTo;
        }
    }

    /// <summary>
    /// Whether <paramref name="sequenced"/> held an in-force descent floor (an at, a glideslope intercept, an at-or-above, a
    /// window) a controller or the procedure wrote, off a descend via, a climb via and a SID.
    /// </summary>
    private static bool ReleasesDescentPastFix(AircraftState aircraft, NavigationTarget sequenced) =>
        (!AltitudeRestrictionInForce.ViaClearanceInForce(aircraft))
        && (!aircraft.Procedure.SidViaMode)
        && (sequenced.AltitudeRestriction is { } passed)
        && (passed.Source is CifpAltitudeRestrictionSource.Atc or CifpAltitudeRestrictionSource.Procedure)
        && (passed.Type != CifpAltitudeRestrictionType.AtOrBelow)
        && AltitudeRestrictionInForce.IsInForce(aircraft, passed);

    /// <summary>
    /// Speed look-ahead planning for procedure fixes. Scans the navigation route for the
    /// first fix with a speed restriction, computes time-to-fix vs acceleration/deceleration
    /// time, and sets TargetSpeed proactively so the aircraft meets the constraint at the fix
    /// rather than reacting after sequencing past it.
    /// </summary>
    private static void UpdateSpeedPlanning(AircraftState aircraft, AircraftCategory cat)
    {
        if (IsSpeedPlanningOff(aircraft))
        {
            return;
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            PlanPendingCrossingSpeed(aircraft, cat);
            return;
        }

        bool speedLimitWaived = IsSpeedLimitWaived(aircraft);
        double cumulativeDistNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);

        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                cumulativeDistNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            }

            if ((route[i].SpeedRestriction is not { } restriction) || IsPublishedSpeedOverridden(aircraft, route[i]))
            {
                continue;
            }

            // The constraint holds where the fix sequences, its sequencing threshold before the fix itself.
            double sequencingDistNm = SequencingDistanceNm(aircraft, route, i, cumulativeDistNm);
            if (PlanConstraintSpeed(aircraft, cat, restriction, sequencingDistNm, speedLimitWaived))
            {
                break; // Step planning: only target the first speed-constrained fix
            }
        }
    }

    /// <summary>
    /// Plans the crossing speed a controller set on the fix of the coded leg a SID departure flies while its route is still
    /// pending (<see cref="Phases.Tower.DepartureProcedurePhase.ActiveLegCrossingSpeed"/>), with the live planner's judge
    /// (<see cref="PlanConstraintSpeed"/>), so the aircraft crosses that fix at it.
    /// </summary>
    private static void PlanPendingCrossingSpeed(AircraftState aircraft, AircraftCategory cat)
    {
        if (
            (aircraft.Phases?.CurrentPhase is not Phases.Tower.DepartureProcedurePhase departure)
            || (departure.ActiveLegCrossingSpeed(aircraft) is not { } crossing)
        )
        {
            return;
        }

        PlanConstraintSpeed(aircraft, cat, crossing.Restriction, crossing.SequencingDistNm, IsSpeedLimitWaived(aircraft));
    }

    /// <summary>Whether 14 CFR 91.117's 250 KIAS limit is waived: for the type, or on a military route that waives it.</summary>
    private static bool IsSpeedLimitWaived(AircraftState aircraft) =>
        (AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType)) || (aircraft.MilitaryRoute.SpeedLimitWaived);

    /// <summary>
    /// Plans a fix's speed <paramref name="restriction"/>, <paramref name="sequencingDistNm"/> before the point the fix
    /// sequences (<see cref="PlannedConstraintSpeed"/>, then under a Mach hold <see cref="PlanCrossingUnderMach"/>, else
    /// <see cref="PlanFixSpeed"/>). False when the planner skips the restriction (<see cref="IsPlannedSpeedFix"/>).
    /// </summary>
    private static bool PlanConstraintSpeed(
        AircraftState aircraft,
        AircraftCategory cat,
        CifpSpeedRestriction restriction,
        double sequencingDistNm,
        bool speedLimitWaived
    )
    {
        (double constraintSpeed, bool floorGivesWay) = PlannedConstraintSpeed(aircraft, restriction, speedLimitWaived);
        if (!IsPlannedSpeedFix(aircraft, restriction, constraintSpeed))
        {
            return false;
        }

        if (aircraft.Targets.TargetMach is not null)
        {
            PlanCrossingUnderMach(aircraft, cat, constraintSpeed, floorGivesWay, sequencingDistNm);
        }
        else
        {
            PlanFixSpeed(aircraft, cat, constraintSpeed, floorGivesWay, sequencingDistNm);
        }

        return true;
    }

    /// <summary>
    /// Whether the speed planner is off: on the ground or not moving, or with the published speeds deleted. Under an
    /// assigned speed or a Mach hold it still runs, for controller crossings only.
    /// </summary>
    private static bool IsSpeedPlanningOff(AircraftState aircraft) =>
        (aircraft.IsOnGround) || (aircraft.GroundSpeed <= 0) || (aircraft.Procedure.SpeedRestrictionsDeleted);

    /// <summary>
    /// Whether <paramref name="fix"/>'s speed is a published one an ATC speed or a Mach hold overrides, so the planner
    /// neither plans it nor stops at it. An assigned speed overrides a published one (AIM 5-4-1.a.1; 7110.65 5-7-1.f,
    /// 5-7-4.c), so a published fix is neither planned nor hides a controller crossing after it; a controller crossing is
    /// an ATC assignment itself, flown at the assigned speed until its slowdown point. The same rule
    /// ApplyFixSpeedConstraint keeps at sequencing. Under a Mach hold a published fix is not planned either.
    /// </summary>
    private static bool IsPublishedSpeedOverridden(AircraftState aircraft, NavigationTarget fix) =>
        (aircraft.Targets.HasExplicitSpeedCommand || (aircraft.Targets.TargetMach is not null)) && !IsControllerSetSpeed(fix);

    /// <summary>
    /// Whether the planner plans <paramref name="restriction"/> (planned as <paramref name="constraintSpeed"/>), or
    /// skips past it to the next speed fix.
    /// </summary>
    private static bool IsPlannedSpeedFix(AircraftState aircraft, CifpSpeedRestriction restriction, double constraintSpeed)
    {
        // A minimum ("at or above") restriction must never trigger a deceleration: the
        // aircraft may legally cross faster. Only act on it to accelerate up to the floor
        // when currently slower; otherwise skip it.
        if ((restriction.Type == CifpSpeedRestrictionType.AtOrAbove) && (aircraft.IndicatedAirspeed > constraintSpeed))
        {
            return false;
        }

        // A maximum ("at or below") restriction caps a following aircraft's speed; it is never a cue to accelerate,
        // which would close on the lead the follower is spacing behind. Skip it, as a minimum is skipped above.
        return !(
            (restriction.Type == CifpSpeedRestrictionType.AtOrBelow)
            && (aircraft.Approach.FollowingCallsign is not null)
            && (aircraft.IndicatedAirspeed < constraintSpeed)
        );
    }

    /// <summary>
    /// Plans the fix speed <paramref name="constraintSpeed"/>, <paramref name="sequencingDistNm"/> before the point the
    /// fix sequences, with no Mach hold in force: an acceleration is targeted at once, a deceleration at its slowdown point.
    /// </summary>
    private static void PlanFixSpeed(
        AircraftState aircraft,
        AircraftCategory cat,
        double constraintSpeed,
        bool floorGivesWay,
        double sequencingDistNm
    )
    {
        double speedDelta = Math.Abs(aircraft.IndicatedAirspeed - constraintSpeed);
        if (speedDelta < SpeedSnapKts)
        {
            // Pin TargetSpeed to the constraint so the auto speed schedule
            // doesn't override it while the constrained fix is still ahead.
            SetPlannedSpeed(aircraft, constraintSpeed, floorGivesWay);
            return;
        }

        bool needsDecel = aircraft.IndicatedAirspeed > constraintSpeed;
        double rate = needsDecel
            ? AircraftPerformance.DecelRate(aircraft.AircraftType, cat)
            : AircraftPerformance.AccelRate(aircraft.AircraftType, cat);
        double changeTimeSeconds = speedDelta / rate;
        double timeToFixSeconds = sequencingDistNm / (aircraft.GroundSpeed / 3600.0);

        // Acceleration: set TargetSpeed immediately — there's no benefit to
        // delaying, and starting early ensures the aircraft meets the constraint.
        // Deceleration: wait until time-to-fix is within 10% of change time so the
        // aircraft doesn't slow down prematurely.
        if (!needsDecel || (timeToFixSeconds <= changeTimeSeconds * 1.1))
        {
            SetPlannedSpeed(aircraft, constraintSpeed, floorGivesWay);
        }
    }

    /// <summary>
    /// Plans a controller's crossing speed (<paramref name="constraintSpeed"/>) under the Mach hold in force,
    /// <paramref name="sequencingDistNm"/> before the point the fix sequences: the Mach is held until the window opens,
    /// at the slowdown point for a crossing below the speed it flies (the larger of the IAS and the Mach's IAS
    /// equivalent), and for a crossing at or above it at the earlier of the acceleration point and the point where the
    /// Mach's IAS equivalent reaches the crossing speed. There the Mach hold and SMAX end and the crossing speed is
    /// flown as an IAS (AIM 4-4-12.c). At or below FL240 before the window opens (7110.65 5-7-1.g;
    /// <see cref="MachHandOverAltitudeFt"/>) they end too, handed over to the Mach's own IAS
    /// (<see cref="MachHandOverKts"/>), which the window then slows from as for any IAS. Before either nothing is
    /// written, and the Mach keeps steering the target speed.
    /// </summary>
    private static void PlanCrossingUnderMach(
        AircraftState aircraft,
        AircraftCategory cat,
        double constraintSpeed,
        bool floorGivesWay,
        double sequencingDistNm
    )
    {
        if (aircraft.Targets.TargetMach is not { } mach)
        {
            return;
        }

        double machIas = WindInterpolator.MachToIas(mach, aircraft.Altitude);
        double startSpeed = Math.Max(aircraft.IndicatedAirspeed, machIas);
        double timeToFixSeconds = sequencingDistNm / (aircraft.GroundSpeed / 3600.0);
        bool windowOpen;
        if (constraintSpeed < startSpeed)
        {
            double decelSeconds = (startSpeed - constraintSpeed) / AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
            windowOpen = timeToFixSeconds <= decelSeconds * 1.1;
        }
        else
        {
            double accelSeconds = (constraintSpeed - machIas) / AircraftPerformance.AccelRate(aircraft.AircraftType, cat);
            windowOpen = (machIas >= constraintSpeed - SpeedSnapKts) || (timeToFixSeconds <= accelSeconds * 1.1);
        }

        if (!windowOpen && (aircraft.Altitude > MachHandOverAltitudeFt))
        {
            return;
        }

        // Below FL240 before the window opens the Mach is handed over to its own IAS, which the window then slows from.
        double target = windowOpen ? constraintSpeed : MachHandOverKts(aircraft.AircraftType, mach, aircraft.Altitude);
        Log.LogDebug(
            "{Callsign}: Mach {Mach:F2} ends for the {Speed:F0} kt crossing at {Altitude:F0} ft, targeting {Target:F0} kt",
            aircraft.Callsign,
            mach,
            constraintSpeed,
            aircraft.Altitude,
            target
        );
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.SpeedMaxHeld = false;
        if (windowOpen)
        {
            SetPlannedSpeed(aircraft, constraintSpeed, floorGivesWay);
        }
        else
        {
            aircraft.Targets.TargetSpeed = target;
        }
    }

    /// <summary>
    /// The speed the planner flies to cross a fix's <paramref name="restriction"/>: its published speed under 14 CFR 91.117,
    /// clamped to the speed floor and ceiling in force. With no explicit ATC speed a floor is the procedure's, a minimum
    /// crossed earlier: it holds until the pilot slows for a lower "at" or "at or below" speed ahead, which it then gives
    /// way to (7110.65 5-7-1.g NOTE 3; AIM 5-4-1 NOTE 2), and <c>FloorGivesWay</c> says so. Under an explicit ATC speed,
    /// where only a controller crossing is planned, the floor never gives way: the speed is clamped to it, as
    /// <see cref="ApplyFixSpeedConstraint"/> clamps it at sequencing.
    /// </summary>
    private static (double Speed, bool FloorGivesWay) PlannedConstraintSpeed(
        AircraftState aircraft,
        CifpSpeedRestriction restriction,
        bool speedLimitWaived
    )
    {
        double constraintSpeed = ClampFixSpeedToApproachFloor(aircraft, restriction.SpeedKts);

        // 14 CFR 91.117: cap at 250 KIAS below 10,000 ft MSL
        if (aircraft.Altitude < 10_000 && !speedLimitWaived)
        {
            constraintSpeed = Math.Min(constraintSpeed, 250);
        }

        bool floorGivesWay = false;
        if (aircraft.Targets.SpeedFloor is { } floor)
        {
            floorGivesWay =
                (!aircraft.Targets.HasExplicitSpeedCommand) && (restriction.Type != CifpSpeedRestrictionType.AtOrAbove) && (floor > constraintSpeed);
            if (!floorGivesWay)
            {
                constraintSpeed = Math.Max(constraintSpeed, floor);
            }
        }

        if (aircraft.Targets.SpeedCeiling is { } ceiling)
        {
            constraintSpeed = Math.Min(constraintSpeed, ceiling);
        }

        return (constraintSpeed, floorGivesWay);
    }

    /// <summary>
    /// Targets the planned <paramref name="speed"/>; when a procedure's floor gives way to it
    /// (<see cref="PlannedConstraintSpeed"/>), the floor ends here, as the deceleration for the lower speed starts.
    /// </summary>
    private static void SetPlannedSpeed(AircraftState aircraft, double speed, bool floorGivesWay)
    {
        if (floorGivesWay)
        {
            aircraft.Targets.SpeedFloor = null;
        }

        aircraft.Targets.TargetSpeed = speed;
    }

    /// <summary>
    /// The distance before <paramref name="fix"/>, flown from <paramref name="legStart"/>, at which the sequencer starts
    /// the turn, or sequences the fix: for a fly-by fix with <paramref name="nextFix"/> after it the larger of its turn
    /// anticipation and <see cref="NavArrivalNm"/>, for a fly-over or last fix <see cref="NavArrivalNm"/> alone. Also
    /// returns the anticipation distance (0 for the latter).
    /// </summary>
    internal static (double ThresholdNm, double AnticipationNm) SequencingThreshold(
        AircraftState aircraft,
        LatLon legStart,
        NavigationTarget fix,
        NavigationTarget? nextFix
    )
    {
        if ((nextFix is null) || fix.IsFlyOver)
        {
            return (NavArrivalNm, 0);
        }

        double currentLegBearing = GeoMath.BearingTo(legStart, fix.Position);
        double nextLegBearing = GeoMath.BearingTo(fix.Position, nextFix.Position);
        double turnRate =
            aircraft.Targets.TurnRateOverride
            ?? AircraftPerformance.TurnRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
        double anticipationNm = ComputeAnticipationDistanceNm(aircraft.GroundSpeed, turnRate, currentLegBearing, nextLegBearing);
        return (Math.Max(anticipationNm, NavArrivalNm), anticipationNm);
    }

    /// <summary>
    /// Computes the turn anticipation distance for a fly-by waypoint.
    /// Based on turn radius (from ground speed and turn rate) and the course change angle.
    /// Returns 0 for negligible turns (&lt;1°), capped at 5nm.
    /// </summary>
    internal static double ComputeAnticipationDistanceNm(
        double groundSpeedKts,
        double turnRateDegPerSec,
        double currentLegBearing,
        double nextLegBearing
    )
    {
        double courseChange = Math.Abs(NormalizeAngle(nextLegBearing - currentLegBearing));
        if (courseChange < 1.0)
        {
            return 0;
        }

        // Turn radius: R = GS / (turnRate × 2π) in nm (GS in nm/hr, turnRate in deg/s → rad/s)
        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        double radiusNm = gsNmPerSec / turnRateRadPerSec;

        double halfAngleRad = courseChange * Math.PI / 360.0;
        double anticipation = radiusNm * Math.Tan(halfAngleRad);

        return Math.Min(anticipation, 5.0);
    }

    /// <summary>
    /// Computes an arc-blended heading for smooth fly-by turns.
    /// Finds the inscribed turn circle tangent to both legs and returns the tangent heading.
    /// </summary>
    internal static double ComputeArcBlendedHeading(
        LatLon aircraftPos,
        double groundSpeedKts,
        double turnRateDegPerSec,
        LatLon waypointPos,
        double currentLegBearing,
        double nextLegBearing
    )
    {
        double courseChange = NormalizeAngle(nextLegBearing - currentLegBearing);
        if (Math.Abs(courseChange) < 1.0)
        {
            return GeoMath.BearingTo(aircraftPos, waypointPos);
        }

        // Turn radius
        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        double radiusNm = gsNmPerSec / turnRateRadPerSec;

        // Turn center: offset perpendicular to the bisector of the two legs
        bool turnRight = courseChange > 0;
        double bisector = NormalizeBearing(currentLegBearing + courseChange / 2.0);
        double perpBearing = turnRight ? bisector + 90.0 : bisector - 90.0;
        perpBearing = NormalizeBearing(perpBearing);

        // Offset distance from waypoint to turn center
        double halfAngleRad = Math.Abs(courseChange) * Math.PI / 360.0;
        double cosHalf = Math.Cos(halfAngleRad);
        double offsetNm = cosHalf > 0.01 ? radiusNm / cosHalf : radiusNm;

        LatLon center = GeoMath.ProjectPointRaw(waypointPos, perpBearing, offsetNm);

        // Aircraft's bearing from turn center
        double radialFromCenter = GeoMath.BearingTo(center, aircraftPos);

        // Tangent heading = perpendicular to radial, in turn direction
        double tangent = turnRight ? radialFromCenter + 90.0 : radialFromCenter - 90.0;
        return NormalizeBearing(tangent);
    }

    /// <summary>
    /// Clamps a fix/restriction-derived target speed to a flyable minimum — the aircraft's
    /// final-approach speed (FAS). No published speed restriction may legitimately require an
    /// aircraft to fly below the speed it would hold on short final, so a corrupt CIFP value
    /// (e.g. a continuation-record padding artifact parsed as 2 kt) cannot near-stop the
    /// aircraft in flight. Logs a warning so the underlying data problem stays visible.
    /// </summary>
    private static double ClampFixSpeedToApproachFloor(AircraftState aircraft, double restrictionKts)
    {
        double fas = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
        if (restrictionKts >= fas)
        {
            return restrictionKts;
        }

        Log.LogWarning(
            "[Speed] {Callsign}: fix speed restriction {Restr:F0}kt is below final-approach speed {Fas:F0}kt — clamping (check CIFP data)",
            aircraft.Callsign,
            restrictionKts,
            fas
        );
        return fas;
    }

    /// <summary>
    /// Applies altitude and speed constraints from a navigation target when via mode is active.
    /// SID via mode enforces climb restrictions; STAR via mode enforces descent restrictions.
    /// </summary>
    internal static void ApplyFixConstraints(AircraftState aircraft, NavigationTarget target) =>
        ApplyFixConstraints(aircraft, target, slowdownLeftToPlanner: false);

    /// <summary>
    /// As <see cref="ApplyFixConstraints(AircraftState, NavigationTarget)"/>, for the first constrained fix when a climb via
    /// or descend via is issued: the slowdown for a lower speed there is left to the planner's deceleration timing rather
    /// than started at once (7110.65 5-7-1.g NOTE 3; AIM 5-4-1.a.2(a) NOTE 2). A higher speed or an "at or
    /// above" minimum still applies at once, and the speed is still remembered as the last published one.
    /// </summary>
    internal static void ApplyViaClearanceFixConstraints(AircraftState aircraft, NavigationTarget target) =>
        ApplyFixConstraints(aircraft, target, slowdownLeftToPlanner: true);

    /// <summary>
    /// As <see cref="ApplyFixConstraints(AircraftState, NavigationTarget)"/>, for the fix the aircraft has just sequenced
    /// onto: a speed below the present one is left to the speed planner's deceleration window, in every mode, a
    /// controller-set speed under an assigned speed too, so the aircraft holds its speed until the slowdown point
    /// (7110.65 5-7-1.g NOTE 3; AIM 5-4-1.a.2(a) NOTE 2), and under a Mach hold a controller-set speed too, which the
    /// planner plans there (<see cref="PlanCrossingUnderMach"/>). Under an ATC-assigned Mach a published speed is not
    /// applied at all (an assigned speed outranks it, AIM 5-4-1.a.1); under a Mach nobody assigned (live traffic) it is
    /// applied at once, but the Mach rewrites the target speed each tick while it is held, so of what it sets only the
    /// <see cref="ControlTargets.SpeedFloor"/> and <see cref="AircraftProcedure.LastProcedureSpeedKts"/> outlast the
    /// Mach. An altitude, an acceleration and an "at or above" floor are left immediate by the flag itself.
    /// </summary>
    internal static void ApplySequencedFixConstraints(AircraftState aircraft, NavigationTarget target) =>
        ApplyFixConstraints(aircraft, target, slowdownLeftToPlanner: (aircraft.Targets.TargetMach is null) || IsControllerSetSpeed(target));

    private static void ApplyFixConstraints(AircraftState aircraft, NavigationTarget target, bool slowdownLeftToPlanner)
    {
        bool sidVia = aircraft.Procedure.SidViaMode;
        bool starVia = aircraft.Procedure.StarViaMode;
        bool hasConstraint = target.AltitudeRestriction is not null || target.SpeedRestriction is not null;

        if (!sidVia && !starVia && !hasConstraint)
        {
            return;
        }

        // Under a climb via the target is its top altitude under the ceilings ahead: an at-or-above restriction never is.
        bool climbVia = ApplyClimbViaAltitude(aircraft);
        if ((!climbVia) && (target.AltitudeRestriction is { } alt) && AltitudeRestrictionInForce.IsInForce(aircraft, alt))
        {
            ApplyFixAltitudeConstraint(aircraft, target, alt);
        }

        ApplyFixSpeedConstraint(aircraft, target, slowdownLeftToPlanner);
    }

    /// <summary>
    /// Targets the speed on the first route fix that has one when reaching it means speeding up: a speed above the
    /// present one, or an "at or above" minimum, which becomes the floor. A resume on a STAR then starts toward it at once
    /// rather than waiting for the planner, which only times slowdowns. A lower speed is left to the planner's
    /// deceleration timing, so the aircraft keeps its present speed until it has to slow (7110.65 5-7-1.g NOTE 3;
    /// AIM 4-4-12.f.3 NOTE). Nothing while the published speeds are deleted or an ATC speed is assigned.
    /// </summary>
    internal static void ApplyNextPublishedSpeed(AircraftState aircraft)
    {
        if (
            (aircraft.Targets.NavigationRoute.Find(f => f.SpeedRestriction is not null) is { SpeedRestriction: { } spd } next)
            && ((spd.Type == CifpSpeedRestrictionType.AtOrAbove) || (spd.SpeedKts > aircraft.IndicatedAirspeed))
        )
        {
            ApplyFixSpeedConstraint(aircraft, next, slowdownLeftToPlanner: false);
        }
    }

    /// <summary>
    /// Whether a controller set <paramref name="fix"/>'s speed restriction (a CFIX carrying a speed, a constrained
    /// direct-to's speed constraint or a "DVIA SPD n FIX"), which stamps the fix's speed with its transmission or leaves a
    /// speed revert on it, rather than the procedure publishing it. An altitude-only crossing leaves the speed the
    /// procedure's.
    /// </summary>
    internal static bool IsControllerSetSpeed(NavigationTarget fix) =>
        (fix.SpeedTransmissionId is not null)
        || Commands.NavigationCommandHandler.HasPlainRevert(fix)
        || (fix.RevertMach is not null)
        || fix.RevertSpeedMax
        || (fix.RevertResume is not null);

    /// <summary>
    /// Whether <paramref name="fix"/>'s speed restriction binds under the given speed state: it has one, the published
    /// speeds are not deleted, and no ATC speed is in force unless the controller set the fix's speed itself. The rule
    /// <see cref="ApplyFixSpeedConstraint"/> flies and the nav-route overlay shows for published speeds
    /// (<see cref="NavRouteSpeedLatency"/>). The overlay never draws a controller-set speed latent, while under DSR this
    /// rule keeps the physics from flying a controller crossing speed (YAAT-1920).
    /// </summary>
    /// <param name="fix">The route fix whose speed is judged.</param>
    /// <param name="explicitSpeedInForce">Whether an ATC speed is in force (<see cref="ControlTargets.HasExplicitSpeedCommand"/>).</param>
    /// <param name="speedRestrictionsDeleted">Whether DSR is in force (<see cref="AircraftProcedure.SpeedRestrictionsDeleted"/>).</param>
    /// <returns>True when the fix's speed applies.</returns>
    internal static bool PublishedSpeedApplies(NavigationTarget fix, bool explicitSpeedInForce, bool speedRestrictionsDeleted) =>
        (fix.SpeedRestriction is not null) && !speedRestrictionsDeleted && !(explicitSpeedInForce && !IsControllerSetSpeed(fix));

    /// <summary>
    /// Applies <paramref name="target"/>'s speed restriction: an "at or above" minimum as the floor, any other as the
    /// target speed and the last published speed. With <paramref name="slowdownLeftToPlanner"/>, a target below the
    /// present speed is left to <see cref="UpdateSpeedPlanning"/>, though still remembered as the last published speed.
    /// </summary>
    private static void ApplyFixSpeedConstraint(AircraftState aircraft, NavigationTarget target, bool slowdownLeftToPlanner)
    {
        // An ATC-assigned speed outranks a published one until ATC ends it (7110.65 5-7-1.c, 5-7-4; AIM 4-4-12.f), the
        // same rule UpdateSpeedPlanning and the speed stamp (SpeedTransmissionId) keep. A crossing speed a controller set is itself an ATC
        // assignment, issued after the speed in force, and still applies.
        if (
            PublishedSpeedApplies(target, aircraft.Targets.HasExplicitSpeedCommand, aircraft.Procedure.SpeedRestrictionsDeleted)
            && (target.SpeedRestriction is { } spd)
        )
        {
            if (spd.Type == CifpSpeedRestrictionType.AtOrAbove)
            {
                // Minimum crossing speed: establish a floor (the 91.117 cap is enforced against
                // the floor in UpdateSpeed). Do not pull TargetSpeed down toward the minimum the
                // way an at-or-below restriction does, and don't record it as a terminating
                // ceiling memory.
                aircraft.Targets.SpeedFloor = aircraft.Targets.SpeedFloor is { } existingFloor ? Math.Max(existingFloor, spd.SpeedKts) : spd.SpeedKts;
            }
            else
            {
                double targetSpeed = ClampFixSpeedToApproachFloor(aircraft, spd.SpeedKts);

                // 14 CFR 91.117: cap speed restrictions at 250 KIAS below 10,000 ft MSL.
                if (!aircraft.IsOnGround && aircraft.Altitude < 10_000)
                {
                    targetSpeed = Math.Min(targetSpeed, 250);
                }

                // Clamp via-mode speed to active floor/ceiling
                if (aircraft.Targets.SpeedFloor is { } floor)
                {
                    targetSpeed = Math.Max(targetSpeed, floor);
                }

                if (aircraft.Targets.SpeedCeiling is { } ceiling)
                {
                    targetSpeed = Math.Min(targetSpeed, ceiling);
                }

                if ((!slowdownLeftToPlanner) || (targetSpeed > aircraft.IndicatedAirspeed))
                {
                    aircraft.Targets.TargetSpeed = targetSpeed;
                }

                // AIM 5-4-1 NOTE 2: remember the last published speed so that if the
                // procedure terminates without further ATC instruction the aircraft
                // does not accelerate beyond it. Under a via clearance a crossing speed
                // the controller set ends at its fix (7110.65 5-7-2.e), so it is not one.
                if (!(aircraft.Procedure.ProcedureSpeedsInForce && IsControllerSetSpeed(target)))
                {
                    aircraft.Procedure.LastProcedureSpeedKts = targetSpeed;
                }
            }
        }
    }

    /// <summary>
    /// Targets the altitude <paramref name="target"/>'s in-force restriction <paramref name="alt"/> calls for. A climb takes
    /// its target from the climb scan (<see cref="PlanClimbScan"/>) over the route from <paramref name="target"/> (or over
    /// that fix alone when it is not on the route), so a fix never raises the target above the cleared or top altitude nor
    /// above a later ceiling; it is left alone when that is not above the present altitude. A descent off a via, met bound
    /// or not, takes its target from the descent scan (<see cref="ApplyDescendingFixBound"/>), so a floor ahead still holds it.
    /// </summary>
    private static void ApplyFixAltitudeConstraint(AircraftState aircraft, NavigationTarget target, CifpAltitudeRestriction alt)
    {
        if (ClimbsToFixBound(aircraft, alt))
        {
            ApplyClimbScanAtFix(aircraft, target);
            return;
        }

        if ((ClearedAltitudeMeetingBound(aircraft, alt) is { } cleared) && (!DescendsToClearedOffVia(aircraft, cleared)))
        {
            // The bound does not move the aircraft: it keeps flying to its cleared altitude
            if (Math.Abs(cleared - aircraft.Altitude) > AltitudeSnapFt)
            {
                aircraft.Targets.TargetAltitude = cleared;
            }

            return;
        }

        ApplyDescendingFixBound(aircraft, target, alt);
    }

    /// <summary>
    /// Whether the aircraft descends to its <paramref name="cleared"/> altitude off a descend via and a SID, so a bound that
    /// altitude meets is judged by the descent scan (<see cref="PlanDescentScan"/>) with the floors ahead.
    /// </summary>
    private static bool DescendsToClearedOffVia(AircraftState aircraft, double cleared) =>
        (!aircraft.Procedure.StarViaMode) && (!aircraft.Procedure.SidViaMode) && (cleared < aircraft.Altitude - AltitudeSnapFt);

    /// <summary>
    /// Targets the altitude a fix that does not mean a climb (<see cref="ClimbsToFixBound"/>) calls for. Off a descend via
    /// and a SID it is the descent scan's (<see cref="PlanDescentScan"/>) over the route from <paramref name="target"/> (or
    /// over that fix alone when it is not on the route), so no ceiling takes the target below a floor ahead; it is left alone
    /// when that is not below the present altitude. Under a via it is what <paramref name="alt"/> resolves to
    /// (<see cref="ApplyViaDescendingFixBound"/>).
    /// </summary>
    private static void ApplyDescendingFixBound(AircraftState aircraft, NavigationTarget target, CifpAltitudeRestriction alt)
    {
        if ((aircraft.Procedure.StarViaMode) || (aircraft.Procedure.SidViaMode))
        {
            ApplyViaDescendingFixBound(aircraft, alt);
            return;
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int start = route.IndexOf(target);
        List<NavigationTarget> scanned = (start >= 0) ? route : [target];
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if ((PlanDescentScan(aircraft, cat, scanned, Math.Max(start, 0)).TargetFt is { } descendTo) && (descendTo < aircraft.Altitude))
        {
            aircraft.Targets.TargetAltitude = descendTo;
        }
    }

    /// <summary>
    /// Targets what <paramref name="alt"/> resolves to for a fix that does not mean a climb under a descend via or a SID:
    /// under a descend via the depicted altitude, never below the via's floor.
    /// </summary>
    private static void ApplyViaDescendingFixBound(AircraftState aircraft, CifpAltitudeRestriction alt)
    {
        bool starVia = aircraft.Procedure.StarViaMode;

        // A descend via flies down to the depicted altitudes; on a SID the restriction resolves as a climb's
        double? resolvedAlt = ResolveAltitudeRestriction(aircraft, alt, isDescending: starVia);
        if (resolvedAlt is { } targetAlt)
        {
            // Apply the STAR floor limit (only in via mode)
            if ((starVia) && (aircraft.Procedure.StarViaFloor is { } floor))
            {
                targetAlt = Math.Max(targetAlt, floor);
            }

            aircraft.Targets.TargetAltitude = targetAlt;
        }
    }

    /// <summary>
    /// Whether <paramref name="alt"/> at the fix ahead means a climb: off a descend via, never for a bound that resolves
    /// below the aircraft (an at or an at-or-below under it, which takes the descending path, SID via included), toward a
    /// cleared altitude above the present one that already meets the bound, else (with the bound not met) under a SID via
    /// or from at or below the bound's value.
    /// </summary>
    private static bool ClimbsToFixBound(AircraftState aircraft, CifpAltitudeRestriction alt)
    {
        if (aircraft.Procedure.StarViaMode)
        {
            return false;
        }

        if ((ResolveAltitudeRestriction(aircraft, alt, isDescending: false) is { } resolved) && (resolved < aircraft.Altitude - AltitudeSnapFt))
        {
            return false;
        }

        if (ClearedAltitudeMeetingBound(aircraft, alt) is { } cleared)
        {
            return cleared > aircraft.Altitude + AltitudeSnapFt;
        }

        return (aircraft.Procedure.SidViaMode) || (aircraft.Altitude <= alt.Altitude1Ft);
    }

    /// <summary>
    /// Targets the climb scan's altitude (<see cref="PlanClimbScan"/>) over the route from <paramref name="target"/>, or over
    /// that fix alone when it is not on the route, when that is above the present altitude.
    /// </summary>
    private static void ApplyClimbScanAtFix(AircraftState aircraft, NavigationTarget target)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int start = route.IndexOf(target);
        List<NavigationTarget> scanned = (start >= 0) ? route : [target];
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if ((PlanClimbScan(aircraft, cat, scanned, Math.Max(start, 0)).TargetFt is { } climbTo) && (climbTo > aircraft.Altitude))
        {
            aircraft.Targets.TargetAltitude = climbTo;
        }
    }

    /// <summary>
    /// The cleared altitude (<see cref="NavigationCommandHandler.ClearedAltitude"/>) when it already meets
    /// <paramref name="restriction"/>'s bound outside a descend or climb via
    /// (<see cref="NavigationCommandHandler.CrossingBoundMetByClearance"/>); otherwise null, and the restriction resolves as
    /// <see cref="ResolveAltitudeRestriction"/> says. A via clearance flies to the depicted altitudes and clears the
    /// assigned altitude, so it never meets a bound this way; nor does an in-force published approach floor below the cleared
    /// altitude, which the aircraft descends to once established on the approach (AIM 5-4-7.f; 7110.65 4-8-1.a.4).
    /// </summary>
    private static double? ClearedAltitudeMeetingBound(AircraftState aircraft, CifpAltitudeRestriction restriction) =>
        NavigationCommandHandler.CrossingBoundMetByClearance(aircraft, restriction) ? NavigationCommandHandler.ClearedAltitude(aircraft) : null;

    /// <summary>What a planner does with a restriction whose bound the cleared altitude may already meet.</summary>
    private enum ClearedAltitudePlan
    {
        /// <summary>The bound is not met: the planner resolves the restriction itself.</summary>
        NotMet,

        /// <summary>The bound is met and the aircraft is at its cleared altitude, or it lies the planner's other way.</summary>
        AlreadyThere,

        /// <summary>The bound is met and the aircraft now flies to its cleared altitude; planning stops here.</summary>
        Targeted,
    }

    /// <summary>
    /// Flies to the cleared altitude when it already meets <paramref name="restriction"/>'s bound
    /// (<see cref="ClearedAltitudeMeetingBound"/>). While the aircraft is still on the wrong side of the bound (climbing
    /// below an at-or-above, descending above an at-or-below, outside a window) it must still be past the bound by the fix
    /// (AIM 4-4-10.e), so the rate is the one that reaches the bound in <paramref name="sequencingDistNm"/>, the distance to
    /// where the fix sequences (<see cref="SequencingDistanceNm"/>), never slower
    /// than the standard rate (<see cref="BoundCrossingRate"/>). Once past the bound it flies at the profile rate.
    /// </summary>
    private static ClearedAltitudePlan PlanToClearedAltitude(
        AircraftState aircraft,
        AircraftCategory cat,
        CifpAltitudeRestriction restriction,
        double sequencingDistNm,
        bool descending
    )
    {
        if (ClearedAltitudeMeetingBound(aircraft, restriction) is not { } cleared)
        {
            return ClearedAltitudePlan.NotMet;
        }

        double feetToCleared = descending ? aircraft.Altitude - cleared : cleared - aircraft.Altitude;
        if (feetToCleared <= AltitudeSnapFt)
        {
            return ClearedAltitudePlan.AlreadyThere;
        }

        aircraft.Targets.TargetAltitude = cleared;
        double feetToBound = FeetShortOfBound(aircraft.Altitude, restriction, descending);
        if (feetToBound > 0)
        {
            double standardRate = descending
                ? AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude)
                : AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, aircraft.Altitude);
            double rate = BoundCrossingRate(feetToBound, sequencingDistNm, aircraft.GroundSpeed, standardRate);
            aircraft.Targets.PlannedVerticalRate = descending ? -rate : rate;
        }
        else
        {
            aircraft.Targets.PlannedVerticalRate = null;
        }

        return ClearedAltitudePlan.Targeted;
    }

    /// <summary>
    /// How far <paramref name="altitude"/> still is from the side of <paramref name="restriction"/>'s bound it is flying to:
    /// a descent's ceiling (at-or-below, a window's top), a climb's floor (at-or-above, a window's bottom). Zero or less once
    /// on the right side, or when the restriction has no bound that way.
    /// </summary>
    private static double FeetShortOfBound(double altitude, CifpAltitudeRestriction restriction, bool descending)
    {
        double? bound = (descending, restriction.Type) switch
        {
            (true, CifpAltitudeRestrictionType.AtOrBelow or CifpAltitudeRestrictionType.Between) => restriction.Altitude1Ft,
            (false, CifpAltitudeRestrictionType.AtOrAbove) => restriction.Altitude1Ft,
            (false, CifpAltitudeRestrictionType.Between) => restriction.Altitude2Ft,
            _ => null,
        };
        if (bound is not { } b)
        {
            return 0;
        }

        return descending ? altitude - b : b - altitude;
    }

    /// <summary>
    /// The along-route distance from the aircraft to where it sequences <paramref name="route"/>[<paramref name="i"/>]:
    /// <paramref name="cumulativeDistNm"/> (the distance to the fix itself) less the fix's
    /// <see cref="SequencingThreshold"/>, never below zero. A crossing restriction holds where the fix sequences, so the
    /// planners aim their required rate here (AIM 1-2-1.b.1, 4-4-10.e). For a fly-by turn under 90° the threshold is the
    /// turn anticipation, so this measures to the turn start, which is at or before sequencing.
    /// </summary>
    internal static double SequencingDistanceNm(AircraftState aircraft, List<NavigationTarget> route, int i, double cumulativeDistNm)
    {
        LatLon legStart = (i == 0) ? aircraft.Position : route[i - 1].Position;
        NavigationTarget? nextFix = (i + 1 < route.Count) ? route[i + 1] : null;
        return Math.Max(0, cumulativeDistNm - SequencingThreshold(aircraft, legStart, route[i], nextFix).ThresholdNm);
    }

    /// <summary>
    /// The vertical rate that covers <paramref name="feetToBound"/> in <paramref name="distanceNm"/>, between the standard
    /// rate and twice it; twice it when the sequencing point is less than six seconds away.
    /// </summary>
    private static double BoundCrossingRate(double feetToBound, double distanceNm, double groundSpeedKts, double standardRate)
    {
        double timeMinutes = distanceNm / (groundSpeedKts / 60.0);
        double maxRate = standardRate * 2.0;
        return timeMinutes > 0.1 ? Math.Clamp(feetToBound / timeMinutes, standardRate, maxRate) : maxRate;
    }

    /// <summary>
    /// Resolves an altitude restriction to a concrete target altitude based on the aircraft's
    /// current altitude and the restriction type (At, AtOrAbove, AtOrBelow, Between).
    /// Returns null if the aircraft already satisfies the restriction.
    /// When <paramref name="isDescending"/> is true (a descend via, or a non-via aircraft above the bound), AtOrAbove
    /// constraints resolve to the depicted altitude even when the aircraft is above it: the pilot descends TO the constraint
    /// altitude. When false, AtOrBelow constraints resolve to the depicted altitude even when below it; that value is a cap,
    /// and the climb scan (<see cref="PlanClimbScan"/>) never climbs past the cleared or top altitude for it.
    /// </summary>
    private static double? ResolveAltitudeRestriction(AircraftState aircraft, CifpAltitudeRestriction alt, bool isDescending)
    {
        return alt.Type switch
        {
            CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept => alt.Altitude1Ft,
            CifpAltitudeRestrictionType.AtOrAbove => (isDescending || aircraft.Altitude < alt.Altitude1Ft) ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.AtOrBelow => (!isDescending || aircraft.Altitude > alt.Altitude1Ft) ? alt.Altitude1Ft : null,
            CifpAltitudeRestrictionType.Between => ResolveBetweenRestriction(aircraft, alt, isDescending),
            _ => null,
        };
    }

    private static double? ResolveBetweenRestriction(AircraftState aircraft, CifpAltitudeRestriction alt, bool isDescending)
    {
        if (alt.Altitude2Ft is not { } lower)
        {
            return null;
        }

        // Descent (STAR via): always target the lower bound — the upper bound is permissiveness
        if (isDescending)
        {
            return aircraft.Altitude > lower ? lower : null;
        }

        // Climb (SID via): always target the upper bound — pilots want to get high fast
        return aircraft.Altitude < alt.Altitude1Ft ? alt.Altitude1Ft : null;
    }

    /// <summary>
    /// The altitude a climb via flies to now (7110.65 4-5-7.h.10 NOTEs; AIM 5-2-9 Examples 6-7): its top altitude
    /// (<see cref="AircraftProcedure.ClimbViaTargetFt"/>), lowered by the lowest ceiling on a route fix still ahead — an
    /// at, an at-or-below, or the upper bound of a between restriction. An at-or-above restriction is never the target:
    /// the climb to the top altitude meets it. An aircraft already above a ceiling ahead holds its altitude, since a via
    /// clearance never commands a descent. Null when no climb via is in force.
    /// </summary>
    internal static double? ClimbViaAltitude(AircraftState aircraft)
    {
        if (aircraft.Procedure.ClimbViaTargetFt is not { } top)
        {
            return null;
        }

        double target = top;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (
                (fix.AltitudeRestriction is { } restriction)
                && (
                    restriction.Type is CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrBelow or CifpAltitudeRestrictionType.Between
                )
                && AltitudeRestrictionInForce.IsInForce(aircraft, restriction)
            )
            {
                target = Math.Min(target, restriction.Altitude1Ft);
            }
        }

        return Math.Max(target, Math.Min(top, aircraft.Altitude));
    }

    /// <summary>
    /// Under a climb via, targets <see cref="ClimbViaAltitude"/> and returns true; while
    /// <see cref="Phases.Tower.DepartureProcedurePhase"/> flies its coded legs it caps each leg itself, so the target is
    /// left to it. Reaching the top clears the climb via, keeping the top as the assigned altitude. False when no climb via
    /// is in force.
    /// </summary>
    private static bool ApplyClimbViaAltitude(AircraftState aircraft)
    {
        if (ClimbViaAltitude(aircraft) is not { } climbVia)
        {
            return false;
        }

        if (aircraft.Phases?.CurrentPhase is not Phases.Tower.DepartureProcedurePhase)
        {
            aircraft.Targets.TargetAltitude = climbVia;
        }

        // Reaching the top with nothing left to fly ends the climb via, keeping the top target so the aircraft levels there
        // (7110.65 4-5-7.h.10; AIM 5-2-9.h.8). While the SID's coded legs fly the aircraft they hold their own route off the
        // live one and keep the interim CTO altitude behind the via, so the top is theirs to hold until they hand over.
        bool legsOwnAltitude = aircraft.Phases?.CurrentPhase is not Phases.Tower.DepartureProcedurePhase;
        if (
            legsOwnAltitude
            && (aircraft.Targets.NavigationRoute.Count == 0)
            && (Math.Abs(aircraft.Altitude - climbVia) <= ClimbViaTopReachedToleranceFt)
        )
        {
            aircraft.Procedure.ClimbViaTargetFt = null;
            aircraft.Procedure.ClimbViaTransmissionId = null;
            aircraft.Procedure.SidViaMode = false;
            aircraft.Procedure.SidViaCeiling = null;
            aircraft.Procedure.ActiveSidId = null;
            aircraft.Procedure.DepartureRunway = null;
            // The top is where the aircraft stays: the assigned altitude keeps it there for anything that rebuilds the
            // target from it after the via is gone.
            aircraft.Targets.AssignedAltitude = climbVia;
        }

        return true;
    }

    /// <summary>
    /// Whether a climb via is still in force below its top altitude: the route ending does not end it, since the top may lie
    /// above a ceiling on the route's last fix.
    /// </summary>
    private static bool ClimbViaInForceBelowTop(AircraftState aircraft) =>
        (aircraft.Procedure.ClimbViaTargetFt is { } top) && (aircraft.Altitude < top - ClimbViaTopReachedToleranceFt);

    /// <summary>
    /// Remembers the "at" or "at or below" speed published on a procedure fix the aircraft just crossed, whatever speed
    /// it was flying, so a later "resume normal speed" or "resume published speed" holds it as the ceiling until the next
    /// published speed restriction (AIM 5-4-1 NOTE 2). The caller passes only a published speed, never one a controller
    /// set (<see cref="IsControllerSetSpeed"/>). An "at or above" speed is a minimum, never a ceiling, and leaves the
    /// memory as it was.
    /// </summary>
    private static void RecordCrossedPublishedSpeed(AircraftState aircraft, CifpSpeedRestriction? published)
    {
        if ((published is not { } crossed) || (crossed.Type == CifpSpeedRestrictionType.AtOrAbove))
        {
            return;
        }

        aircraft.Procedure.LastCrossedPublishedSpeedKts = ClampFixSpeedToApproachFloor(aircraft, crossed.SpeedKts);
    }

    /// <summary>
    /// The assigned speed past a controller crossing under a via clearance: the next controller crossing's speed, else
    /// none. An explicit ATC speed and SMAX's re-arm on <paramref name="crossed"/> keep their own. With neither, a speed the
    /// fix reverted to is an earlier crossing's, which has ended too, or one ATC has since cancelled.
    /// </summary>
    private static void MoveAssignedSpeedPastControllerCrossing(AircraftState aircraft, NavigationTarget crossed, List<NavigationTarget> route)
    {
        if (!aircraft.Targets.HasExplicitSpeedCommand && !crossed.RevertSpeedMax)
        {
            aircraft.Targets.AssignedSpeed = route.Find(IsControllerSetSpeed)?.SpeedRestriction?.SpeedKts;
        }
    }

    private static void ClearProcedureState(AircraftState aircraft)
    {
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;
        // A climb via still below its top stays in force when the route ends at the SID's last constrained fix: that fix's
        // ceiling applied only up to the fix, and the climb goes on to the top altitude (AIM 5-2-9.h.8; P/CG TOP ALTITUDE;
        // 7110.65 4-5-7.h). The SID stays named with it, so a vector without an altitude still takes the 5-6-2 handling
        // (CommandDispatcher.CheckVectoringWarning) and a check-in still names the departure. Reaching the top ends it
        // (ApplyClimbViaAltitude); a new altitude assignment ends it through CancelViaClearances, and a vector through
        // FlightCommandHandler.ClearActiveProcedure.
        if (!ClimbViaInForceBelowTop(aircraft))
        {
            aircraft.Procedure.ActiveSidId = null;
            aircraft.Procedure.DepartureRunway = null;
            aircraft.Procedure.SidViaMode = false;
            aircraft.Procedure.SidViaCeiling = null;
            aircraft.Procedure.ClimbViaTargetFt = null;
            aircraft.Procedure.ClimbViaTransmissionId = null;
        }
        // Keep the destination-runway datablock when the aircraft is still committed to a runway
        // (e.g. a pattern-entry route draining onto final): nulling it here blanked the runway during
        // the final approach after an EF/CLAND-to-runway entry (#292). Only clear it once the aircraft
        // has no assigned runway at all.
        if (aircraft.Phases?.AssignedRunway is null)
        {
            aircraft.Procedure.DestinationRunway = null;
        }
        aircraft.Procedure.LastProcedureSpeedKts = null;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
    }

    /// <summary>Constant for bank angle formula: (π/180) × 1.6878 / 32.174 ≈ 0.0009156.</summary>
    private const double BankAngleCoeff = Math.PI / 180.0 * 1.6878 / 32.174;

    /// <summary>
    /// Ground-speed threshold below which a ground aircraft is considered
    /// stationary and cannot rotate. Nosewheel steering requires forward
    /// motion; differential-brake or tiller turns need some thrust overcoming
    /// rolling friction. Helicopters on the ground must be rolling as well —
    /// hover turns only happen in the air. A tight threshold (0.1 kt) avoids
    /// forbidding rotation in the last sliver of a taxi deceleration while
    /// still catching the fully-stopped case that causes stale
    /// <see cref="ControlTargets.TargetTrueHeading"/> values to drift a parked
    /// aircraft's heading.
    /// </summary>
    public const double StationaryGroundSpeedKts = 0.1;

    private static void UpdateHeading(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        TrueHeading? target = aircraft.Targets.TargetTrueHeading;
        if (target is null)
        {
            aircraft.BankAngle = 0;
            return;
        }

        // Aircraft on the ground at zero ground speed cannot physically
        // pivot in place. Reject rotation even if an upstream phase left a
        // stale target heading — the physics contract is the last line of
        // defence against "aircraft pirouettes while parked" artifacts.
        // Airborne helicopters at zero ground speed (hover) are unaffected
        // because IsOnGround is false.
        if (aircraft.IsOnGround && aircraft.GroundSpeed < StationaryGroundSpeedKts)
        {
            aircraft.BankAngle = 0;
            return;
        }

        double current = aircraft.TrueHeading.Degrees;
        double goal = target.Value.Degrees;

        double diff = NormalizeAngle(goal - current);
        if (Math.Abs(diff) < HeadingSnapDeg)
        {
            aircraft.TrueHeading = new TrueHeading(goal);

            // Heading reached — clear turn direction bias but keep the
            // assigned heading so it persists in the UI and autopilot
            // until the controller issues a new instruction.
            aircraft.Targets.PreferredTurnDirection = null;
            aircraft.BankAngle = 0;
            return;
        }

        double turnRate = aircraft.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(aircraft.AircraftType, cat);
        double maxTurn = turnRate * deltaSeconds;

        double direction = ResolveDirection(diff, aircraft.Targets.PreferredTurnDirection);

        double turnAmount = Math.Min(Math.Abs(diff), maxTurn) * direction;

        aircraft.TrueHeading = new TrueHeading(current + turnAmount);

        // Compute bank angle: atan(TAS_kts × turnRate_deg/s × coeff)
        double tasKts = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
        double bankMag = Math.Atan(tasKts * turnRate * BankAngleCoeff) * (180.0 / Math.PI);
        aircraft.BankAngle = bankMag * direction;
    }

    private static void UpdateAltitude(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        if (aircraft.IsOnGround)
        {
            aircraft.VerticalSpeed = 0;
            return;
        }

        double? target = ResolveAltitudeGoal(aircraft);
        if (target is null)
        {
            aircraft.VerticalSpeed = 0;
            return;
        }

        double current = aircraft.Altitude;
        double clearedGoal = target.Value;
        (double rate, bool profileRate) = BaseVerticalRate(aircraft, cat, climbing: clearedGoal > current);

        // 14 CFR 91.117(a) backstop: an aircraft too fast to slow to 250 by 10,000 ft levels there until it has. The
        // hold is derived every tick and changes no stored target, so the cleared descent resumes on its own.
        bool heldForSpeed = HoldsAtTenThousandForSpeed(aircraft, cat, clearedGoal, rate);
        LogSpeedHoldRelease(aircraft, clearedGoal, heldForSpeed);
        double goal = heldForSpeed ? Math.Max(clearedGoal, TenThousandFt) : clearedGoal;
        double diff = goal - current;

        if (Math.Abs(diff) < AltitudeSnapFt)
        {
            if (heldForSpeed)
            {
                LevelAtTenThousandForSpeed(aircraft, goal);
                return;
            }

            aircraft.Altitude = goal;
            aircraft.VerticalSpeed = 0;
            aircraft.Targets.TargetAltitude = null;
            aircraft.Targets.DesiredVerticalRate = null;
            aircraft.Procedure.IsExpediting = false;
            return;
        }

        bool climbing = diff > 0;

        // AIM 4-4-10.4: fly the optimum rate to 1,000 ft above/below the assigned altitude,
        // "and then attempt to descend or climb at a rate of between 500 and 1,500 fpm until
        // the assigned altitude is reached." Taper proportionally to the remaining altitude
        // (1,500 fpm entering the band, floored at 500) so the Mode C readout winds down
        // instead of cutting from full rate to level in one tick. Scoped to free flight
        // leveling at an assigned altitude: never raises an already gentle rate, and never
        // reinterprets a phase's vertical path — approach/landing phases fly segments in the
        // last 1,000 ft AGL that must not be flattened (a tapered short final floats the
        // touchdown hundreds of feet long).
        if (profileRate && (aircraft.Phases?.CurrentPhase is null) && Math.Abs(diff) < LevelOffBandFt)
        {
            rate = Math.Min(rate, Math.Max(LevelOffMinFpm, Math.Abs(diff) * (LevelOffMaxFpm / LevelOffBandFt)));
        }

        double feetPerSec = rate / 60.0;
        double maxChange = feetPerSec * deltaSeconds;
        double change = Math.Min(Math.Abs(diff), maxChange);

        if (climbing)
        {
            aircraft.Altitude += change;
            aircraft.VerticalSpeed = rate;
        }
        else
        {
            aircraft.Altitude -= change;
            aircraft.VerticalSpeed = -rate;
        }
    }

    /// <summary>
    /// The vertical rate (fpm, unsigned) <see cref="UpdateAltitude"/> flies before the level-off taper, and whether it is
    /// the type's profile rate. A phase (or the instructor) owns DesiredVerticalRate; the step climb/descent planners own
    /// PlannedVerticalRate, which is recomputed every tick and only exists while a constrained fix is still in the route.
    /// A phase's rate wins when both are set.
    /// </summary>
    private static (double Rate, bool ProfileRate) BaseVerticalRate(AircraftState aircraft, AircraftCategory cat, bool climbing)
    {
        double? commanded = aircraft.Targets.DesiredVerticalRate ?? aircraft.Targets.PlannedVerticalRate;
        if (commanded is { } desired)
        {
            // 7110.65 §4-5-7 NOTE 4: "'Expedite' is not to be used in lieu of appropriate
            // restrictions." A phase-commanded rate (a glidepath) and a planner-commanded rate (a
            // computed crossing-restriction rate) ARE the appropriate restriction — both are flown
            // verbatim and expedite never scales either.
            return (Math.Abs(desired), false);
        }

        double rate = climbing
            ? AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, aircraft.Altitude)
            : AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude);

        if (aircraft.Procedure.IsExpediting)
        {
            rate = CategoryPerformance.ExpediteVerticalRate(cat, rate, climbing);
        }

        return (rate, true);
    }

    /// <summary>
    /// True when the aircraft levels at 10,000 ft this tick to slow to 250 KIAS before descending through it (14 CFR
    /// 91.117(a)): it is at or above 10,000 ft, cleared below it, the descent band applies
    /// (<see cref="DescentSpeedLimitBandApplies"/>), it is faster than its release speed (<see cref="SpeedHoldReleaseKts"/>),
    /// and even full deceleration at <paramref name="rateFpm"/> cannot reach the cap by 10,000 ft. An aircraft already level
    /// at 10,000 ft stays held until it has slowed. <paramref name="rateFpm"/> is the rate flown toward the cleared goal
    /// before any taper (<see cref="BaseVerticalRate"/>), the same rate the cap's lead judges by, so the hold's own level-off
    /// does not feed back into either.
    /// </summary>
    private static bool HoldsAtTenThousandForSpeed(AircraftState aircraft, AircraftCategory cat, double clearedGoal, double rateFpm)
    {
        if (
            (aircraft.Altitude < TenThousandFt)
            || (clearedGoal >= TenThousandFt)
            || (aircraft.IndicatedAirspeed <= SpeedHoldReleaseKts(aircraft))
            || !DescentSpeedLimitBandApplies(aircraft)
        )
        {
            return false;
        }

        double aboveFt = aircraft.Altitude - TenThousandFt;
        if (aboveFt <= AltitudeSnapFt)
        {
            return true;
        }

        double fullDecelSeconds =
            (aircraft.IndicatedAirspeed - DescentBandCapKts(aircraft)) / AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
        return (rateFpm > 0) && (aboveFt / (rateFpm / 60.0) < fullDecelSeconds);
    }

    /// <summary>Levels the aircraft at 10,000 ft for <see cref="HoldsAtTenThousandForSpeed"/>, keeping every cleared target.</summary>
    private static void LevelAtTenThousandForSpeed(AircraftState aircraft, double goal)
    {
        if (aircraft.VerticalSpeed != 0)
        {
            Log.LogDebug(
                "{Callsign}: levelling at {Altitude:F0} ft until slowed to {Release} KIAS (IAS {Ias:F1}, was {Rate:F0} fpm)",
                aircraft.Callsign,
                goal,
                SpeedHoldReleaseKts(aircraft),
                aircraft.IndicatedAirspeed,
                aircraft.VerticalSpeed
            );
        }

        aircraft.Altitude = goal;
        aircraft.VerticalSpeed = 0;
    }

    /// <summary>Logs the tick an aircraft level at 10,000 ft and cleared below it continues its descent.</summary>
    private static void LogSpeedHoldRelease(AircraftState aircraft, double clearedGoal, bool heldForSpeed)
    {
        if (
            !heldForSpeed
            && (clearedGoal < TenThousandFt)
            && (aircraft.VerticalSpeed == 0)
            && (Math.Abs(aircraft.Altitude - TenThousandFt) < AltitudeSnapFt)
        )
        {
            Log.LogDebug(
                "{Callsign}: descending below {Altitude:F0} ft to {Goal:F0} ft at IAS {Ias:F1}",
                aircraft.Callsign,
                aircraft.Altitude,
                clearedGoal,
                aircraft.IndicatedAirspeed
            );
        }
    }

    /// <summary>
    /// The altitude <see cref="UpdateAltitude"/> is currently flying toward, folding the VFR
    /// floor/ceiling band into <see cref="ControlTargets.TargetAltitude"/>. Null means the
    /// aircraft is level and has nowhere to go — which is why command handlers gate on this
    /// rather than on <c>TargetAltitude</c> alone.
    /// </summary>
    internal static double? ResolveAltitudeGoal(AircraftState aircraft)
    {
        double? goal = aircraft.Targets.TargetAltitude;
        if (aircraft.Targets.AltitudeFloor is { } floor)
        {
            goal = goal is null ? (aircraft.Altitude < floor - AltitudeSnapFt ? floor : null) : Math.Max(goal.Value, floor);
        }

        if (aircraft.Targets.AltitudeCeiling is { } ceiling)
        {
            goal = goal is null ? (aircraft.Altitude > ceiling + AltitudeSnapFt ? ceiling : null) : Math.Min(goal.Value, ceiling);
        }

        return goal;
    }

    /// <summary>14 CFR 91.117(a): maximum indicated airspeed below 10,000 ft MSL.</summary>
    private const double SpeedLimitBelow10kKts = 250;

    /// <summary>14 CFR 91.117(c): maximum indicated airspeed in the airspace underlying Class B.</summary>
    private const double SpeedLimitUnderClassBKts = 200;

    /// <summary>
    /// The regulatory speed cap at the aircraft's present position, or <see cref="double.MaxValue"/>
    /// when none applies.
    ///
    /// The two waivers are legally different and must not be collapsed into one flag. The DoD's
    /// authorization (AP/1B chapter 1 §I) relieves 14 CFR 91.117(a) only, and is unlimited inside a
    /// military training route's confines — but 91.117(c)'s 200 kt Class B shelf cap has no military
    /// training route exception, so it survives. 91.117(d) is the opposite shape: it reaches every
    /// paragraph of the section, including (c), but only up to the aircraft's minimum safe speed.
    ///
    /// At or above 10,000 ft the 250 kt cap leads a descent below it, so the aircraft crosses 10,000 ft at the cap rather
    /// than slowing after it (<see cref="DescentSpeedLimitLead"/>).
    /// </summary>
    public static double RegulatorySpeedLimit(AircraftState aircraft)
    {
        if (aircraft.IsOnGround)
        {
            return double.MaxValue;
        }

        if (aircraft.Altitude >= TenThousandFt)
        {
            return ((DescentSpeedLimitLead(aircraft) is { } lead) && lead.Applies) ? lead.CapKts : double.MaxValue;
        }

        bool underShelf = Data.Airspace.AirspaceDatabase.Default.IsUnderClassBShelf(aircraft.Position, aircraft.Altitude);
        double cap = underShelf ? SpeedLimitUnderClassBKts : SpeedLimitBelow10kKts;

        if (aircraft.MilitaryRoute.SpeedLimitWaived && !underShelf)
        {
            return double.MaxValue;
        }

        // 91.117(d): "If the minimum safe airspeed for any particular operation is greater than the
        // maximum speed prescribed in this section, the aircraft may be operated at that minimum
        // speed." That is a floor under the cap, not a removal of it.
        return AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType)
            ? Math.Max(cap, AircraftPerformance.MinimumSafeSpeedKts(aircraft.AircraftType))
            : cap;
    }

    /// <summary>The altitude (ft MSL) 14 CFR 91.117(a)'s 250 kt limit begins below.</summary>
    private const double TenThousandFt = 10_000;

    /// <summary>How much earlier than full deceleration needs the descent band starts slowing the aircraft.</summary>
    private const double SpeedLimitLeadMargin = 1.1;

    /// <summary>
    /// The sim's 2 kt airborne speed snap (<see cref="SpeedSnapKts"/>) over a speed limit: IAS within it counts as at the
    /// limit. A rounding margin of the integration, not a pilot tolerance.
    /// </summary>
    public const double SpeedSnapMarginKts = SpeedSnapKts;

    /// <summary>
    /// The IAS at or below which an aircraft held at 10,000 ft for speed continues its descent: the band's cap
    /// (<see cref="DescentBandCapKts"/>) plus the speed snap (<see cref="SpeedSnapMarginKts"/>), 252 KIAS for a type 91.117(d)
    /// does not waive.
    /// </summary>
    private static double SpeedHoldReleaseKts(AircraftState aircraft) => DescentBandCapKts(aircraft) + SpeedSnapMarginKts;

    /// <summary>
    /// The descent band's state for an aircraft at or above 10,000 ft that is descending below it: the seconds left to
    /// 10,000 ft at <paramref name="RateFpm"/>, the seconds the band leads by (1.1 times the time the type's deceleration
    /// needs to bring the speed it is flying toward down to <paramref name="CapKts"/>), and the cap it holds.
    /// </summary>
    private readonly record struct DescentLead(double SecondsToTenThousand, double LeadSeconds, double RateFpm, double CapKts)
    {
        /// <summary>True once the aircraft is within the lead of 10,000 ft, so the cap holds it.</summary>
        public bool Applies => SecondsToTenThousand <= LeadSeconds;
    }

    /// <summary>
    /// The 250 kt lead above 10,000 ft (14 CFR 91.117(a)): null when the band does not apply
    /// (<see cref="DescentSpeedLimitBandApplies"/>). The speed it slows from is the one the aircraft would fly with no cap
    /// (<see cref="UncappedSpeedKts"/>), so once the band starts it stays on down to 10,000 ft instead of letting the
    /// aircraft speed back up as the IAS falls.
    /// </summary>
    private static DescentLead? DescentSpeedLimitLead(AircraftState aircraft)
    {
        if (!DescentSpeedLimitBandApplies(aircraft))
        {
            return null;
        }

        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double cap = DescentBandCapKts(aircraft);
        double fromKts = UncappedSpeedKts(aircraft, cat);
        // The hold's rate (UpdateAltitude): toward the cleared altitude before the level-off taper, so one judges both.
        double rateFpm = BaseVerticalRate(aircraft, cat, climbing: false).Rate;
        double aboveFt = aircraft.Altitude - TenThousandFt;
        double seconds = (aboveFt <= 0) ? 0 : ((rateFpm > 0) ? aboveFt / (rateFpm / 60.0) : double.PositiveInfinity);
        double leadSeconds = SpeedLimitLeadMargin * Math.Max(0, fromKts - cap) / AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
        return new DescentLead(seconds, leadSeconds, rateFpm, cap);
    }

    /// <summary>
    /// The speed the aircraft would fly toward with no 91.117 cap: the highest of its IAS, its target (which the cap holds
    /// short rather than retires), a speed floor (which would lift it back once slowed) and the auto schedule's speed
    /// (<see cref="AutoScheduleSpeed"/>, which would set a new target once one at the cap is reached).
    /// </summary>
    private static double UncappedSpeedKts(AircraftState aircraft, AircraftCategory cat)
    {
        double target = Math.Max(aircraft.Targets.TargetSpeed ?? 0, aircraft.Targets.SpeedFloor ?? 0);
        double scheduled = AutoScheduleSpeed(aircraft, cat, EnvelopeSpeedLimit(aircraft)) ?? 0;
        return Math.Max(aircraft.IndicatedAirspeed, Math.Max(target, scheduled));
    }

    /// <summary>
    /// The cap the descent band holds: 250 KIAS, or the type's minimum safe speed where 91.117(d) waives it to that.
    /// </summary>
    private static double DescentBandCapKts(AircraftState aircraft) =>
        AircraftPerformance.IsSpeedLimitWaived(aircraft.AircraftType)
            ? Math.Max(SpeedLimitBelow10kKts, AircraftPerformance.MinimumSafeSpeedKts(aircraft.AircraftType))
            : SpeedLimitBelow10kKts;

    /// <summary>
    /// True when 91.117(a)'s cap leads this aircraft's descent: airborne at or above 10,000 ft, cleared below both 10,000 ft
    /// and its altitude (<see cref="LowestClearedAltitude"/>), not exempt, and with no crossing speed above 250 still to
    /// fly at or above 10,000 ft (<see cref="FastCrossingAtOrAboveTenThousandAhead"/>). The one test both the cap
    /// (<see cref="RegulatorySpeedLimit"/>) and the 10,000 ft hold (<see cref="HoldsAtTenThousandForSpeed"/>) read.
    /// </summary>
    private static bool DescentSpeedLimitBandApplies(AircraftState aircraft)
    {
        if (aircraft.IsOnGround || (aircraft.Altitude < TenThousandFt) || ExemptFromDescentBand(aircraft))
        {
            return false;
        }

        if ((LowestClearedAltitude(aircraft) is not { } lowest) || (lowest >= TenThousandFt) || (lowest >= aircraft.Altitude))
        {
            return false;
        }

        return !FastCrossingAtOrAboveTenThousandAhead(aircraft);
    }

    /// <summary>
    /// The exemptions the 250 kt limit already has: SPDN (<see cref="ControlTargets.SpeedLimitsLifted"/>), SMAX
    /// (<see cref="ControlTargets.SpeedMaxHeld"/>) and the military training route waiver.
    /// </summary>
    private static bool ExemptFromDescentBand(AircraftState aircraft) =>
        aircraft.Targets.SpeedLimitsLifted || aircraft.Targets.SpeedMaxHeld || aircraft.MilitaryRoute.SpeedLimitWaived;

    /// <summary>
    /// The lowest altitude the aircraft is cleared to now: the lower of <see cref="ControlTargets.AssignedAltitude"/> and
    /// <see cref="ControlTargets.TargetAltitude"/>, and on a descend via also its bottom, the "except maintain" altitude
    /// (<see cref="AircraftProcedure.StarViaFloor"/>) or else the lowest altitude bound on the route still ahead. Null when
    /// none is set.
    /// </summary>
    internal static double? LowestClearedAltitude(AircraftState aircraft)
    {
        double? lowest = LowerOf(aircraft.Targets.AssignedAltitude, aircraft.Targets.TargetAltitude);
        if (!aircraft.Procedure.StarViaMode)
        {
            return lowest;
        }

        double? viaBottom = aircraft.Procedure.StarViaFloor ?? LowestRouteAltitudeBound(aircraft);
        return LowerOf(lowest, viaBottom);
    }

    private static double? LowerOf(double? a, double? b) => ((a is { } x) && (b is { } y)) ? Math.Min(x, y) : (a ?? b);

    /// <summary>The lowest altitude bound among the route's restrictions in force (<see cref="AltitudeRestrictionInForce.IsInForce"/>).</summary>
    private static double? LowestRouteAltitudeBound(AircraftState aircraft)
    {
        double? lowest = null;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if ((fix.AltitudeRestriction is { } restriction) && AltitudeRestrictionInForce.IsInForce(aircraft, restriction))
            {
                lowest = LowerOf(lowest, LowerAltitudeBoundFt(restriction));
            }
        }

        return lowest;
    }

    /// <summary>
    /// The lower altitude of a crossing restriction: the altitude of an at, at-or-above or at-or-below restriction, and
    /// the lower bound of a between. Null for none, or any other kind.
    /// </summary>
    private static double? LowerAltitudeBoundFt(CifpAltitudeRestriction? restriction) =>
        restriction?.Type switch
        {
            CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrAbove or CifpAltitudeRestrictionType.AtOrBelow =>
                restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.Between => Math.Min(restriction.Altitude1Ft, restriction.Altitude2Ft ?? restriction.Altitude1Ft),
            _ => null,
        };

    /// <summary>
    /// True when the next speed-restricted fix ahead carries a speed above 250 KIAS that must be crossed at or above
    /// 10,000 ft: that crossing wins until the fix is passed. Published speeds deleted by DSR
    /// (<see cref="AircraftProcedure.SpeedRestrictionsDeleted"/>) are skipped, as the planner skips them; a controller's
    /// crossing speed (<see cref="IsControllerSetSpeed"/>) always counts. The fix's altitude must hold the aircraft up
    /// there (<see cref="CrossingFloorFt"/>) and be in force (<see cref="AltitudeRestrictionInForce.IsInForce"/>): a
    /// controller's crossing, or a published one under a via clearance. A published altitude off a via clearance, an "at or below",
    /// or no altitude at all leaves the aircraft free to be below 10,000 ft before the fix, so it does not hold the band off.
    /// </summary>
    private static bool FastCrossingAtOrAboveTenThousandAhead(AircraftState aircraft)
    {
        if ((NextSpeedRestrictedFix(aircraft) is not { } next) || (next.SpeedRestriction!.SpeedKts <= SpeedLimitBelow10kKts))
        {
            return false;
        }

        bool altitudeInForce = (next.AltitudeRestriction is { } r) && AltitudeRestrictionInForce.IsInForce(aircraft, r);
        return altitudeInForce && (CrossingFloorFt(next.AltitudeRestriction) is { } floorFt) && (floorFt >= TenThousandFt);
    }

    /// <summary>The first route fix ahead with a speed restriction the aircraft flies, skipping published ones DSR deleted.</summary>
    private static NavigationTarget? NextSpeedRestrictedFix(AircraftState aircraft)
    {
        bool publishedDeleted = aircraft.Procedure.SpeedRestrictionsDeleted;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if ((fix.SpeedRestriction is not null) && (!publishedDeleted || IsControllerSetSpeed(fix)))
            {
                return fix;
            }
        }

        return null;
    }

    /// <summary>
    /// The lowest altitude a crossing restriction lets the aircraft be at the fix: an at or at-or-above restriction's
    /// altitude, and the lower bound of a between. Null for an at-or-below (no floor), none, or any other kind.
    /// </summary>
    private static double? CrossingFloorFt(CifpAltitudeRestriction? restriction) =>
        restriction?.Type switch
        {
            CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrAbove => restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.Between => Math.Min(restriction.Altitude1Ft, restriction.Altitude2Ft ?? restriction.Altitude1Ft),
            _ => null,
        };

    /// <summary>Logs the tick the descent band starts holding the aircraft to the cap above 10,000 ft.</summary>
    private static void LogDescentBandStart(AircraftState aircraft, double deltaSeconds)
    {
        if (
            !Log.IsEnabled(LogLevel.Debug)
            || (aircraft.Altitude < TenThousandFt)
            || (DescentSpeedLimitLead(aircraft) is not { } lead)
            || !lead.Applies
        )
        {
            return;
        }

        // The lead does not change while the cap holds the target short, and the time to 10,000 ft falls by a tick's worth.
        if (lead.SecondsToTenThousand + deltaSeconds > lead.LeadSeconds)
        {
            Log.LogDebug(
                "{Callsign}: slowing to {Cap:F0} KIAS for 10,000 ft from {Altitude:F0} ft (IAS {Ias:F1}, {Rate:F0} fpm)",
                aircraft.Callsign,
                lead.CapKts,
                aircraft.Altitude,
                aircraft.IndicatedAirspeed,
                lead.RateFpm
            );
        }
    }

    /// <summary>
    /// The fastest the type may fly where it is: the lower of Vmo and Mmo's IAS equivalent at its altitude
    /// (<see cref="AircraftPerformance.MaxOperatingIasKts"/>), recomputed on every call so a climb past the IAS/Mach
    /// crossover lowers it as the altitude rises. Airborne only, like <see cref="RegulatorySpeedLimit"/>; unlimited on the
    /// ground and for a type whose limit is unknown.
    /// </summary>
    private static double EnvelopeSpeedLimit(AircraftState aircraft)
    {
        if (aircraft.IsOnGround)
        {
            return double.MaxValue;
        }

        return AircraftPerformance.MaxOperatingIasKts(aircraft.AircraftType, aircraft.Altitude) ?? double.MaxValue;
    }

    /// <summary>
    /// The speed limit <see cref="UpdateSpeed"/> holds an aircraft to: the lower of 14 CFR 91.117
    /// (<see cref="RegulatorySpeedLimit"/>) and the type's own maximum (<see cref="EnvelopeSpeedLimit"/>). Both bend an
    /// assignment rather than end it: an aircraft held at either keeps its target and takes it up again where the limit
    /// allows (<see cref="ArriveAtGoal"/>). Unlimited while SPDN has lifted the limits
    /// (<see cref="ControlTargets.SpeedLimitsLifted"/>); the envelope alone while SMAX is in force
    /// (<see cref="ControlTargets.SpeedMaxHeld"/>).
    /// </summary>
    private static double EffectiveSpeedLimit(AircraftState aircraft) =>
        aircraft.Targets.SpeedLimitsLifted ? double.MaxValue : SpeedLimitWithin(aircraft, EnvelopeSpeedLimit(aircraft));

    /// <summary>
    /// The speed limit under <paramref name="envelopeLimit"/>: 14 CFR 91.117 as well, except while SMAX is in force
    /// (<see cref="ControlTargets.SpeedMaxHeld"/>), which flies the type's maximum past it.
    /// </summary>
    private static double SpeedLimitWithin(AircraftState aircraft, double envelopeLimit) =>
        aircraft.Targets.SpeedMaxHeld ? envelopeLimit : Math.Min(RegulatorySpeedLimit(aircraft), envelopeLimit);

    /// <summary>
    /// The knots SMAX holds below Vmo: a pilot flies an assigned speed within 10 knots of it (AIM 4-4-12.c; 7110.65 5-7-1.g
    /// NOTE 1), so the maximum forward speed is flown that margin under the redline rather than on it.
    /// </summary>
    private const double SpeedMaxMarginKts = 10;

    /// <summary>
    /// The Mach SMAX holds below Mmo: a pilot flies an assigned Mach within 0.02 of it (AIM 4-4-12.c; 7110.65 5-7-1.g
    /// NOTE 1), so the maximum forward speed is flown that margin under the redline rather than on it.
    /// </summary>
    private const double SpeedMaxMarginMach = 0.02;

    /// <summary>The fraction of a helicopter's Vne SMAX holds it to, the helicopter's margin under its redline.</summary>
    private const double HelicopterSpeedMaxFractionOfVne = 0.9;

    /// <summary>
    /// While SMAX is in force (<see cref="ControlTargets.SpeedMaxHeld"/>) and airborne, holds the type's maximum forward
    /// speed a margin under its redline: Mmo less <see cref="SpeedMaxMarginMach"/> as a Mach hold
    /// (<see cref="ControlTargets.TargetMach"/>, no <see cref="ControlTargets.AssignedSpeed"/>) where that Mach's IAS
    /// equivalent is below Vmo less <see cref="SpeedMaxMarginKts"/>, that IAS as the assigned and target speed elsewhere,
    /// and <see cref="HelicopterSpeedMaxFractionOfVne"/> of Vne for a helicopter. The fields are written only when the mode
    /// changes, at the IAS/Mach crossover, not on every tick.
    /// </summary>
    internal static void HoldMaximumForwardSpeed(AircraftState aircraft)
    {
        if (!aircraft.Targets.SpeedMaxHeld || aircraft.IsOnGround)
        {
            return;
        }

        double? maxKts = SpeedMaxKts(aircraft.AircraftType);
        double? maxMach = AircraftPerformance.MaxOperatingMach(aircraft.AircraftType) is { } mmo ? Math.Round(mmo - SpeedMaxMarginMach, 2) : null;
        if ((maxMach is { } smaxMach) && ((maxKts is not { } ktsBound) || (WindInterpolator.MachToIas(smaxMach, aircraft.Altitude) < ktsBound)))
        {
            if (aircraft.Targets.TargetMach != smaxMach)
            {
                Log.LogDebug("{Callsign}: SMAX holds Mach {Mach:F2} at {Altitude:F0} ft", aircraft.Callsign, smaxMach, aircraft.Altitude);
                aircraft.Targets.TargetMach = smaxMach;
                aircraft.Targets.AssignedSpeed = null;
            }

            return;
        }

        if (maxKts is not { } assigned)
        {
            return;
        }

        if ((aircraft.Targets.TargetMach is not null) || (aircraft.Targets.AssignedSpeed != assigned))
        {
            Log.LogDebug("{Callsign}: SMAX holds {Speed:F0} kt at {Altitude:F0} ft", aircraft.Callsign, assigned, aircraft.Altitude);
            aircraft.Targets.TargetMach = null;
            aircraft.Targets.AssignedSpeed = assigned;
            aircraft.Targets.TargetSpeed = assigned;
        }
    }

    /// <summary>Feet below the cleared altitude at which a Mach hold counts as a descent.</summary>
    private const double MachHandOverDescendingMarginFt = 100;

    /// <summary>
    /// The altitude at and below which a Mach hold is handed over to an IAS whatever its Mach's IAS equivalent is: a descent
    /// cleared to end at or above it keeps the Mach.
    /// </summary>
    private const double MachHandOverAltitudeFt = 24_000;

    /// <summary>
    /// Hands a Mach hold over to an IAS (<see cref="IsHandOverMachHold"/>): level at or below FL240
    /// (<see cref="MachHandOverAltitudeFt"/>) at once, and on a descent below it once the descent reaches FL240 or the
    /// Mach's IAS equivalent reaches <see cref="MachHandOverThresholdKts"/>, whichever comes first. The IAS it hands over to
    /// is the Mach's own at that tick, no higher than Vmo less <see cref="SpeedMaxMarginKts"/> (<see cref="MachHandOverKts"/>),
    /// so a high Mach keeps its IAS and a slow one is never sped up. SMAX keeps its own Mach hold
    /// (<see cref="HoldMaximumForwardSpeed"/>), a helicopter is left to the envelope, and a Mach no speed command set (live
    /// traffic) gets a target speed only, never an assigned one. A controller's crossing speed still ahead on the route stays
    /// the assigned speed until its fix: the handover then sets the target speed only.
    /// </summary>
    private static void HandOverMachHold(AircraftState aircraft, AircraftCategory cat)
    {
        if (!IsHandOverMachHold(aircraft, cat) || (aircraft.Targets.TargetMach is not { } mach))
        {
            return;
        }

        double machIas = WindInterpolator.MachToIas(mach, aircraft.Altitude);
        if ((machIas < MachHandOverThresholdKts(aircraft.AircraftType, cat)) && (aircraft.Altitude > MachHandOverAltitudeFt))
        {
            return;
        }

        double handOverKts = MachHandOverKts(aircraft.AircraftType, mach, aircraft.Altitude);
        Log.LogDebug(
            "{Callsign}: Mach {Mach:F2} handed over to {Speed:F0} kt at {Altitude:F0} ft",
            aircraft.Callsign,
            mach,
            handOverKts,
            aircraft.Altitude
        );
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.TargetSpeed = handOverKts;
        bool controllerCrossingAhead = aircraft.Targets.NavigationRoute.Exists(fix =>
            (fix.SpeedRestriction is not null) && IsControllerSetSpeed(fix)
        );
        if (aircraft.Targets.HasExplicitSpeedCommand && !controllerCrossingAhead)
        {
            aircraft.Targets.AssignedSpeed = handOverKts;
        }
    }

    /// <summary>
    /// True when <paramref name="aircraft"/> holds a Mach that ATC or live traffic set, on an altitude a handover can end:
    /// airborne, not a helicopter, and either descending toward a cleared altitude below
    /// <see cref="MachHandOverAltitudeFt"/> or at or below it without climbing out of it, a level hold there included.
    /// </summary>
    private static bool IsHandOverMachHold(AircraftState aircraft, AircraftCategory cat) =>
        (aircraft.Targets.TargetMach is not null)
        && !aircraft.Targets.SpeedMaxHeld
        && !aircraft.IsOnGround
        && (cat != AircraftCategory.Helicopter)
        && (AtOrBelowHandOverAltitude(aircraft) || DescendingBelowHandOverAltitude(aircraft));

    /// <summary>
    /// True when <paramref name="aircraft"/> is at or below <see cref="MachHandOverAltitudeFt"/> without climbing out of
    /// it: the Mach is handed over there even level, so the thick air cannot carry it past where the descent ends.
    /// </summary>
    private static bool AtOrBelowHandOverAltitude(AircraftState aircraft) =>
        (aircraft.Altitude <= MachHandOverAltitudeFt)
        && ((aircraft.Targets.TargetAltitude is not { } cleared) || (cleared <= aircraft.Altitude + MachHandOverDescendingMarginFt));

    /// <summary>
    /// True when <paramref name="aircraft"/> descends toward a cleared altitude below
    /// <see cref="MachHandOverAltitudeFt"/>, more than <see cref="MachHandOverDescendingMarginFt"/> below it.
    /// </summary>
    private static bool DescendingBelowHandOverAltitude(AircraftState aircraft) =>
        (aircraft.Targets.TargetAltitude is { } cleared)
        && (cleared < MachHandOverAltitudeFt)
        && (cleared < aircraft.Altitude - MachHandOverDescendingMarginFt);

    /// <summary>
    /// The IAS equivalent a Mach's descent triggers the handover at: Vmo less <see cref="SpeedMaxMarginKts"/> or the
    /// profile's FL100 descent speed, whichever is lower.
    /// </summary>
    private static double MachHandOverThresholdKts(string aircraftType, AircraftCategory cat) =>
        Math.Min(SpeedMaxKts(aircraftType) ?? double.MaxValue, DescentSpeedKts(aircraftType, cat));

    /// <summary>The speed the type's profile is flown at in the descent (its FL100 descent speed), as <c>DescentSpeed</c> reads it.</summary>
    private static double DescentSpeedKts(string aircraftType, AircraftCategory cat) =>
        AircraftProfileDatabase.Get(aircraftType) is { } profile
            ? AircraftPerformance.ResolveSpeed(profile.DescentSpeedFl100, 10_000)
            : CategoryPerformance.DefaultSpeed(cat, 10_000);

    /// <summary>
    /// The IAS a Mach hold is handed over to at <paramref name="altitudeFt"/>: the Mach's own IAS equivalent, no higher than
    /// Vmo less <see cref="SpeedMaxMarginKts"/>.
    /// </summary>
    private static double MachHandOverKts(string aircraftType, double mach, double altitudeFt) =>
        Math.Round(Math.Min(SpeedMaxKts(aircraftType) ?? double.MaxValue, WindInterpolator.MachToIas(mach, altitudeFt)));

    /// <summary>
    /// The IAS SMAX holds <paramref name="aircraftType"/> to below its IAS/Mach crossover: Vmo less
    /// <see cref="SpeedMaxMarginKts"/>, or <see cref="HelicopterSpeedMaxFractionOfVne"/> of Vne for a helicopter, rounded to
    /// the knot. Null for a type with no known Vmo.
    /// </summary>
    private static double? SpeedMaxKts(string aircraftType)
    {
        if (AircraftPerformance.MaxOperatingSpeedKts(aircraftType) is not { } vmo)
        {
            return null;
        }

        bool helicopter = AircraftCategorization.Categorize(aircraftType) == AircraftCategory.Helicopter;
        return Math.Round(helicopter ? vmo * HelicopterSpeedMaxFractionOfVne : vmo - SpeedMaxMarginKts);
    }

    private static void UpdateSpeed(AircraftState aircraft, AircraftCategory cat, double deltaSeconds)
    {
        HoldMaximumForwardSpeed(aircraft);
        HandOverMachHold(aircraft, cat);
        bool limitsLifted = aircraft.Targets.SpeedLimitsLifted;
        double envelopeLimit = limitsLifted ? double.MaxValue : EnvelopeSpeedLimit(aircraft);
        double speedLimit = limitsLifted ? double.MaxValue : SpeedLimitWithin(aircraft, envelopeLimit);
        LogDescentBandStart(aircraft, deltaSeconds);

        // Mach hold: recompute equivalent IAS each tick so the aircraft maintains constant Mach. The limit keeps a Mach
        // hold carried down a descent from running the IAS past Vmo (or 91.117) as the air thickens, unless SPDN has
        // lifted the limits. A descending Mach hold is handed over to an IAS first (HandOverMachHold), so this block
        // holds the Mach only until that handover.
        if (aircraft.Targets.TargetMach is { } targetMach && !aircraft.IsOnGround)
        {
            double machIas = Math.Min(WindInterpolator.MachToIas(targetMach, aircraft.Altitude), speedLimit);
            aircraft.Targets.TargetSpeed = machIas;
        }

        // Bound enforcement: if IAS violates a floor, a ceiling, 14 CFR 91.117 or the type's Vmo/Mmo, create a target to correct it.
        // While SPDN has lifted the limits, only the floor and the ceiling bind.
        if (aircraft.Targets.TargetSpeed is null)
        {
            aircraft.Targets.TargetSpeed = BoundsCorrectionTarget(aircraft, speedLimit);
        }

        ApplyAutoSpeedSchedule(aircraft, cat, envelopeLimit);

        double? target = aircraft.Targets.TargetSpeed;
        if (target is null)
        {
            return;
        }

        // IAS is the primary airspeed; TargetSpeed is an IAS command.
        double current = aircraft.IndicatedAirspeed;
        double goal = target.Value;

        // Clamp goal to ground conflict limit (ground ops only).
        if (aircraft.IsOnGround && aircraft.Ground.SpeedLimit is { } limit)
        {
            goal = Math.Min(goal, limit);
        }

        // 14 CFR 91.117 (250 KIAS below 10,000 ft MSL, 200 KIAS under a Class B shelf) and the type's Vmo/Mmo. A cap
        // that bites here holds the aircraft short of the speed it was told to fly, which is not the same as reaching it.
        bool heldShortBySpeedLimit = goal > speedLimit;
        goal = Math.Min(goal, speedLimit);

        // Honor SpeedCeiling continuously, including when a non-procedural source
        // (e.g. auto speed schedule, controller-assigned TargetSpeed before a ceiling
        // was published) has set TargetSpeed above the cap.
        if (aircraft.Targets.SpeedCeiling is { } activeCeiling)
        {
            goal = Math.Min(goal, activeCeiling);
        }

        double diff = goal - current;

        bool accelerating = diff > 0;
        double rate = SpeedChangeRate(aircraft, cat, accelerating);
        double maxChange = rate * deltaSeconds;

        // Snap window. Airborne it is a fixed rounding nicety (2 kt is a fraction of a second of jet
        // acceleration). On the ground it is one sub-tick of change: at the taxi rates 2 kt is two whole
        // seconds of acceleration, so snapping that far would move the aircraft further in one sub-tick
        // than the integrator ever would. Closing the last sliver early is fine; jumping is not.
        double snapWindow = aircraft.IsOnGround ? maxChange : SpeedSnapKts;

        if (Math.Abs(diff) < snapWindow)
        {
            ArriveAtGoal(aircraft, goal, heldShortBySpeedLimit);
            return;
        }

        double change = Math.Min(Math.Abs(diff), maxChange);

        aircraft.IndicatedAirspeed += accelerating ? change : -change;
    }

    /// <summary>
    /// Auto speed schedule: when no explicit speed target exists and the aircraft is climbing or descending without an
    /// explicit SPD command, set the speed for its altitude band, which gives natural speed transitions through a climb
    /// or descent. Skipped during approach or pattern phases, which manage their own speed. The schedule's speed is
    /// held to <paramref name="envelopeLimit"/>.
    /// </summary>
    private static void ApplyAutoSpeedSchedule(AircraftState aircraft, AircraftCategory cat, double envelopeLimit)
    {
        if ((aircraft.Targets.TargetSpeed is null) && (AutoScheduleSpeed(aircraft, cat, envelopeLimit) is { } scheduled))
        {
            if (Math.Abs(aircraft.IndicatedAirspeed - scheduled) > SpeedSnapKts)
            {
                aircraft.Targets.TargetSpeed = scheduled;
            }
        }
    }

    /// <summary>
    /// The speed the auto schedule (<see cref="ApplyAutoSpeedSchedule"/>) flies the aircraft at here, or null when the
    /// schedule does not run: on the ground, under an explicit SPD command, in an approach or a phase that manages speed,
    /// or level at its target altitude.
    /// </summary>
    private static double? AutoScheduleSpeed(AircraftState aircraft, AircraftCategory cat, double envelopeLimit)
    {
        if (
            aircraft.IsOnGround
            || aircraft.Targets.HasExplicitSpeedCommand
            || aircraft.Phases?.ActiveApproach is not null
            || aircraft.Phases?.CurrentPhase?.ManagesSpeed == true
            || aircraft.Targets.TargetAltitude is null
            || Math.Abs(aircraft.Altitude - aircraft.Targets.TargetAltitude.Value) <= AltitudeSnapFt
        )
        {
            return null;
        }

        double defaultSpeed = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, aircraft.Altitude, aircraft.Targets.TargetAltitude);

        // Honor an active speed ceiling so the auto schedule cannot accelerate
        // past a procedural last-published-speed memory or other capped speed.
        if (aircraft.Targets.SpeedCeiling is { } scheduleCeiling)
        {
            defaultSpeed = Math.Min(defaultSpeed, scheduleCeiling);
        }

        // Nor past what the type can fly here, so the schedule never sets a target the cap would then hold short of.
        return Math.Min(defaultSpeed, envelopeLimit);
    }

    /// <summary>
    /// The speed target that corrects a bound the aircraft is violating, or null when it is within all of them.
    /// Ordered: a <see cref="ControlTargets.SpeedFloor"/> it is under, a <see cref="ControlTargets.SpeedCeiling"/> it is
    /// over, then the speed limit itself (<see cref="EffectiveSpeedLimit"/>: the lower of 14 CFR 91.117 and the type's
    /// Vmo/Mmo).
    ///
    /// The limit arm is what makes the cap bite on a standing speed rather than only on an assignment: a pilot
    /// complies with 91.117 without being told to (7110.65 §5-7-2 NOTE 1; AIM 4-4-12.i), so an aircraft that comes under
    /// a Class B shelf faster than 200 kt with nothing left to fly to slows to it, and one past its Vmo/Mmo slows to that.
    /// A floor above the cap does not hold the aircraft up there — 91.117 and the type's own maximum outrank an ATC
    /// "or greater", which is the same clamp <paramref name="speedLimit"/> already puts on the floor. When an exact
    /// assignment stands behind the cap, the limit arm targets the assignment rather than the cap, which is what lets the
    /// aircraft take it back where the cap no longer applies (<see cref="HoldsAssignedSpeedBehindTheLimit"/>). On the ground the
    /// cap does not apply at all; the ground-conflict <see cref="AircraftGroundOps.SpeedLimit"/> governs there.
    /// </summary>
    private static double? BoundsCorrectionTarget(AircraftState aircraft, double speedLimit)
    {
        double effectiveFloor = Math.Min(aircraft.Targets.SpeedFloor ?? 0, speedLimit);
        double effectiveCeiling = Math.Min(aircraft.Targets.SpeedCeiling ?? double.MaxValue, speedLimit);

        if ((aircraft.Targets.SpeedFloor is not null) && (aircraft.IndicatedAirspeed < effectiveFloor))
        {
            return effectiveFloor;
        }

        if ((aircraft.Targets.SpeedCeiling is not null) && (aircraft.IndicatedAirspeed > effectiveCeiling))
        {
            return effectiveCeiling;
        }

        if (!aircraft.IsOnGround && (aircraft.IndicatedAirspeed > speedLimit + SpeedSnapKts))
        {
            return HoldsAssignedSpeedBehindTheLimit(aircraft) ? aircraft.Targets.AssignedSpeed : speedLimit;
        }

        return null;
    }

    /// <summary>
    /// True when an exact ATC assignment stands behind the speed limit that is holding <paramref name="aircraft"/> short,
    /// so the limit branch of <see cref="BoundsCorrectionTarget"/> targets the assignment instead of the cap: the pilot
    /// flies the cap, the assignment stays on the books, and the aircraft takes it back as the cap allows (7110.65
    /// §5-7-2.a NOTE 1, §5-7-4; AIM 4-4-12.f.1).
    ///
    /// The bound forms <c>SPD n+</c>/<c>SPD n-</c> hold a floor or a ceiling rather than a speed to fly, so neither is
    /// resumed as an assignment; SMAX holds its own target behind the same cap; and a Mach hold's target is its Mach,
    /// resolved to an IAS each tick. <see cref="ControlTargets.HasExplicitSpeedCommand"/> is the test rather than
    /// <see cref="ControlTargets.SpeedCommandIsControllerIssued"/> because a scenario preset is an ATC assignment to the
    /// pilot too.
    /// </summary>
    private static bool HoldsAssignedSpeedBehindTheLimit(AircraftState aircraft) =>
        aircraft.Targets.HasExplicitSpeedCommand
        && (aircraft.Targets.AssignedSpeed is { } assigned)
        && (aircraft.Targets.TargetMach is null)
        && !aircraft.Targets.SpeedMaxHeld
        && (aircraft.Targets.SpeedFloor != assigned)
        // Never decides alone: a standing ceiling takes the ceiling arm first, and a phase clearing it ends the assignment (`EndBoundWithTheAssignment`).
        && (aircraft.Targets.SpeedCeiling != assigned);

    /// <summary>
    /// IAS snaps onto the integration goal, and the target that asked for it is retired — unless the speed limit (14 CFR
    /// 91.117 or the type's Vmo/Mmo) is what stopped the aircraft short of that target, in which case the assignment
    /// stands and the aircraft takes it up again where the cap no longer applies. The regulatory cap bends a speed assignment rather than cancelling it: the pilot
    /// complies with 91.117 "without notification" (7110.65 §5-7-2.a NOTE 1), even "resume normal speed" "does not relieve
    /// the pilot of those speed restrictions which are applicable to 14 CFR section 91.117" (AIM 4-4-12.f.1), and only
    /// ATC terminates a speed adjustment (§5-7-4). Retiring the target at the cap would strand the aircraft there
    /// wherever a phase with <c>ManagesSpeed</c> suppresses the auto schedule that would otherwise pick the speed back up.
    ///
    /// Only the speed limit (<see cref="EffectiveSpeedLimit"/>) defers the retirement, and it defers it whichever clamp the
    /// aircraft actually settles on: a <see cref="ControlTargets.SpeedCeiling"/> below the cap holds it lower still, and
    /// the assignment stands behind both until ATC ends it. A target at or under the speed limit is retired on arrival as it always was, whether
    /// the aircraft reached the target itself or a <see cref="ControlTargets.SpeedCeiling"/> or the ground-conflict
    /// <see cref="AircraftGroundOps.SpeedLimit"/> stopped it there — those two are the last word on what this aircraft
    /// flies where it is, and neither is a rule the aircraft stops obeying by flying somewhere else.
    ///
    /// The type's own Vmo/Mmo cap (<see cref="EnvelopeSpeedLimit"/>) defers the retirement the same way: an assignment
    /// accepted low down and capped at Mmo's IAS equivalent through a climb stands, and is flown again on the way down.
    /// </summary>
    private static void ArriveAtGoal(AircraftState aircraft, double goal, bool heldShortBySpeedLimit)
    {
        aircraft.IndicatedAirspeed = goal;
        if (!heldShortBySpeedLimit)
        {
            aircraft.Targets.TargetSpeed = null;
        }
    }

    /// <summary>
    /// True when the aircraft is flying the clamped speed its speed limit allows it here (<see cref="EffectiveSpeedLimit"/>:
    /// 14 CFR 91.117 or the type's Vmo/Mmo, whichever is lower) and carrying an assignment above that — the state
    /// <see cref="ArriveAtGoal"/> leaves it in. The assignment has gone as far as it can go where the
    /// aircraft is, which is what a chained <c>SPD</c> needs to count as finished (<see cref="UpdateBlockCompletion"/>):
    /// the target no longer nulls at the cap, so completion cannot be read off a null target alone.
    ///
    /// The settled test is against the goal the clamps actually produce, the lower of the cap and an active
    /// <see cref="ControlTargets.SpeedCeiling"/>, and not against the assignment: an aircraft given 210 under a 200 kt
    /// shelf while flying 250 is still carrying out the reduction, and a chain behind it waits for the aircraft to get
    /// there just as it would for an uncapped one.
    ///
    /// An assignment held at the type's Vmo/Mmo counts as finished the same way as one held at 91.117. While SPDN has
    /// lifted the limits (<see cref="ControlTargets.SpeedLimitsLifted"/>) nothing holds the aircraft, so this is false.
    /// </summary>
    public static bool IsSpeedAssignmentHeldAtSpeedLimit(AircraftState aircraft)
    {
        if (aircraft.Targets.TargetSpeed is not { } target)
        {
            return false;
        }

        double speedLimit = EffectiveSpeedLimit(aircraft);
        if (target <= speedLimit)
        {
            return false;
        }

        double clampedGoal = Math.Min(speedLimit, aircraft.Targets.SpeedCeiling ?? double.MaxValue);
        return Math.Abs(aircraft.IndicatedAirspeed - clampedGoal) <= SpeedSnapKts;
    }

    /// <summary>
    /// Rate (kts/sec) at which <see cref="UpdateSpeed"/> drives IAS toward the target. Split by frame: on
    /// the ground the aircraft is on wheels at taxi power, so it uses the category's taxi accel/brake rates
    /// (<see cref="CategoryPerformance.TaxiAccelRate"/> / <see cref="CategoryPerformance.TaxiDecelRate"/>);
    /// airborne it uses the type's flight-envelope rates. Physics is the only integrator of ground speed —
    /// ground phases and <see cref="Phases.Ground.GroundNavigator"/> publish
    /// <see cref="ControlTargets.TargetSpeed"/> and let this rate close the gap.
    /// A phase that needs a specific braking rate (a firm rollout stop, an expedited runway exit) publishes
    /// <see cref="ControlTargets.DesiredDecelRate"/>, and one that needs a specific acceleration (a tug move,
    /// which picks up speed at the towbar rate rather than the aircraft's own) publishes
    /// <see cref="ControlTargets.DesiredAccelRate"/>; null falls back to the category default on either branch.
    /// </summary>
    private static double SpeedChangeRate(AircraftState aircraft, AircraftCategory cat, bool accelerating)
    {
        if (aircraft.IsOnGround)
        {
            return accelerating
                ? (aircraft.Targets.DesiredAccelRate ?? CategoryPerformance.TaxiAccelRate(cat))
                : (aircraft.Targets.DesiredDecelRate ?? CategoryPerformance.TaxiDecelRate(cat));
        }

        return accelerating
            ? (aircraft.Targets.DesiredAccelRate ?? AircraftPerformance.AccelRate(aircraft.AircraftType, cat))
            : (aircraft.Targets.DesiredDecelRate ?? AircraftPerformance.DecelRate(aircraft.AircraftType, cat));
    }

    private static void UpdatePosition(
        AircraftState aircraft,
        double deltaSeconds,
        WeatherProfile? weather,
        double simTimeSeconds,
        double windPhaseSeconds
    )
    {
        LatLon pos = aircraft.Position;
        double latRad = pos.Lat * DegToRad;

        // Cache wind on the ground too: motion there is wheel-driven (track = heading, no
        // vector sum), but the takeoff roll's rotation gate and the touchdown frame flip
        // read AircraftState.HeadwindKts from this cache. On the ground the wind is the
        // METAR surface layer (the layers without a position); airborne it is sampled at
        // the aircraft's position, from the GFS grid's column there when the weather carries one.
        aircraft.WindComponents = aircraft.IsOnGround
            ? WindInterpolator.GetWindComponents(weather, aircraft.Altitude, simTimeSeconds, windPhaseSeconds)
            : WindInterpolator.GetWindComponents(
                weather,
                new WindSamplePoint(pos, aircraft.Declination),
                aircraft.Altitude,
                simTimeSeconds,
                windPhaseSeconds
            );

        if (aircraft.IsOnGround)
        {
            // Enforce ground conflict speed limit before computing displacement.
            double iasBeforeLimit = aircraft.IndicatedAirspeed;
            if (aircraft.Ground.SpeedLimit is { } limit && aircraft.IndicatedAirspeed > limit)
            {
                aircraft.IndicatedAirspeed = limit;
            }

            double speedNmPerSec = aircraft.IndicatedAirspeed / 3600.0;
            double moveDir = aircraft.Ground.PushbackTrueHeading?.Degrees ?? aircraft.TrueHeading.Degrees;
            double headingRad = moveDir * DegToRad;

            double dLat = speedNmPerSec * deltaSeconds * Math.Cos(headingRad) / NmPerDegLat;
            double dLon = speedNmPerSec * deltaSeconds * Math.Sin(headingRad) / (NmPerDegLat * Math.Cos(latRad));
            aircraft.Position = new LatLon(pos.Lat + dLat, pos.Lon + dLon);

            // On the ground: Track follows Heading directly (GS is derived from IAS).
            aircraft.TrueTrack = aircraft.TrueHeading;

            if ((aircraft.Ground.SpeedLimit is { } activeLimit) && Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[Ground.Limit] t={Time:F2} {Callsign}: phase={Phase}, ias {Before:F2}->{After:F2}kt, limit={Limit:F2}kt, moved {MovedFt:F2}ft in {Delta:F2}s",
                    simTimeSeconds,
                    aircraft.Callsign,
                    aircraft.Phases?.CurrentPhase?.Name ?? "(none)",
                    iasBeforeLimit,
                    aircraft.IndicatedAirspeed,
                    activeLimit,
                    speedNmPerSec * deltaSeconds * GeoMath.FeetPerNm,
                    deltaSeconds
                );
            }
        }
        else
        {
            // Airborne: derive ground speed vector from TAS + wind.
            double tasKts = WindInterpolator.IasToTas(aircraft.IndicatedAirspeed, aircraft.Altitude);
            double headingRad = aircraft.TrueHeading.Degrees * DegToRad;

            // Ground speed vector (knots, N/E components).
            double gsNKts = tasKts * Math.Cos(headingRad) + aircraft.WindComponents.N;
            double gsEKts = tasKts * Math.Sin(headingRad) + aircraft.WindComponents.E;

            double trackDeg = Math.Atan2(gsEKts, gsNKts) * (180.0 / Math.PI);
            if (trackDeg < 0)
            {
                trackDeg += 360.0;
            }

            aircraft.TrueTrack = new TrueHeading(trackDeg);

            // Displace using the full ground speed vector.
            double dLat = (gsNKts / 3600.0) * deltaSeconds / NmPerDegLat;
            double dLon = (gsEKts / 3600.0) * deltaSeconds / (NmPerDegLat * Math.Cos(latRad));
            aircraft.Position = new LatLon(pos.Lat + dLat, pos.Lon + dLon);
        }
    }

    private static void UpdateCommandQueue(AircraftState aircraft, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        CommandQueue queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        DiscardMissedCycleTerminatorBlocks(aircraft);

        // During active phases, the phase system owns control targets. The full
        // command queue logic (block advancement, untriggered block application) is
        // skipped, but triggered blocks still need to be watched. This lets commands
        // such as "SPD 210 UNTIL 10" fire their ATFN/RNS block during approach
        // phases, while fix and ground triggers can still fire via their event
        // callbacks. Exception: while the aircraft idles in a terminal command-waiting
        // phase (Phase.IsIdleAwaitingCommands), untriggered blocks advance too — the
        // phase never completes on its own, so the queue is the only source of progress.
        if (aircraft.Phases?.CurrentPhase is { } activePhase)
        {
            int triggerStart = queue.CurrentBlock is { IsApplied: true } ? queue.CurrentBlockIndex + 1 : queue.CurrentBlockIndex;
            if (activePhase.IsIdleAwaitingCommands)
            {
                AdvanceQueueWhileIdle(aircraft, queue, triggerStart, deltaSeconds, aircraftLookup);
            }
            else
            {
                ApplyReadyConditionalBlocks(aircraft, queue, triggerStart, deltaSeconds, aircraftLookup);
            }
            return;
        }

        CommandBlock? block = queue.CurrentBlock;
        if (block is null)
        {
            return;
        }

        // Check completion of commands in the current applied block
        if (block.IsApplied)
        {
            UpdateBlockCompletion(aircraft, block, deltaSeconds);

            if (block.ReadyToAdvance)
            {
                queue.CurrentBlockIndex++;
                block = queue.CurrentBlock;
                if (block is null)
                {
                    return;
                }
            }
            else
            {
                // Lookahead: check following conditional blocks while the current
                // block is still running. This allows "DCT A B C; AT B CM 014",
                // "CM 100; LV 050 FH 270", and "SPD 210; ATFN 10 RNS" to fire
                // on their trigger without waiting for the current lateral,
                // altitude, or speed target to complete.
                ApplyReadyConditionalBlocks(aircraft, queue, queue.CurrentBlockIndex + 1, deltaSeconds, aircraftLookup);
                return;
            }
        }

        // For unapplied blocks, check if trigger is met
        if (!block.IsApplied)
        {
            if (block.Trigger is not null && !block.TriggerMet)
            {
                block.TriggerMet = IsTriggerMet(aircraft, block, deltaSeconds, aircraftLookup);
                if (!block.TriggerMet)
                {
                    TrackFrdMiss(aircraft, block);
                    return;
                }

                if (block.Trigger.Type is BlockTriggerType.OnHandoff)
                {
                    aircraft.Track.HandoffAccepted = false;
                }
            }

            // Apply the block's commands (a triggered WAIT holds the payload until its countdown
            // elapses; a Failed apply has already discarded the chain remainder — stop for this tick; a Deferred one
            // waits for its departure's release, unapplied, and is retried next tick).
            if (ApplyOrCountdownWait(aircraft, block, deltaSeconds) == BlockApplyOutcome.WaitCounting)
            {
                // The current block is a WAIT still counting down. Latch the triggers of the following
                // `;`-sequenced blocks now (without applying them) so a ReachFix trigger survives the
                // aircraft flying past the fix during the countdown — otherwise the trailing block orphans
                // when the queue finally advances to it. Mirrors the holdApplies latching in
                // ApplyReadyConditionalBlocks, which only covers the lookahead (current-block-applied) path.
                LatchFollowingTriggers(aircraft, queue, queue.CurrentBlockIndex + 1, deltaSeconds, aircraftLookup);
            }
        }
    }

    /// <summary>
    /// Latches (but does not apply) the triggers of the contiguous run of conditional blocks starting at
    /// <paramref name="startIndex"/>. Used while a current-block WAIT counts down so a trailing block's
    /// <see cref="BlockTriggerType.ReachFix"/> trigger stays met even after the aircraft flies past the fix.
    /// Stops at the first unapplied untriggered block so ordinary `;` sequencing is preserved.
    /// </summary>
    private static void LatchFollowingTriggers(
        AircraftState aircraft,
        CommandQueue queue,
        int startIndex,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup
    )
    {
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is null)
            {
                break;
            }

            if (!block.TriggerMet)
            {
                block.TriggerMet = IsTriggerMet(aircraft, block, deltaSeconds, aircraftLookup);
                if (!block.TriggerMet)
                {
                    TrackFrdMiss(aircraft, block);
                    continue;
                }

                if (block.Trigger.Type is BlockTriggerType.OnHandoff)
                {
                    aircraft.Track.HandoffAccepted = false;
                }
            }
        }
    }

    /// <summary>
    /// Applies ready conditional blocks in the contiguous triggered run starting at
    /// <paramref name="startIndex"/>. Stops at the first unapplied untriggered block
    /// so ordinary semicolon sequencing is preserved.
    /// </summary>
    private static void ApplyReadyConditionalBlocks(
        AircraftState aircraft,
        CommandQueue queue,
        int startIndex,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup
    )
    {
        bool holdApplies = false;
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is null)
            {
                break;
            }

            // Latch the trigger: once met it stays met, so a post-trigger WAIT (and any later block held
            // behind it) survives the aircraft flying past the fix (a ReachFix check would otherwise flip
            // back to false and orphan the block).
            if (!block.TriggerMet)
            {
                block.TriggerMet = IsTriggerMet(aircraft, block, deltaSeconds, aircraftLookup);
                if (!block.TriggerMet)
                {
                    TrackFrdMiss(aircraft, block);
                    continue;
                }

                if (block.Trigger.Type is BlockTriggerType.OnHandoff)
                {
                    aircraft.Track.HandoffAccepted = false;
                }
            }

            // Once a preceding triggered WAIT is still counting down, hold the following blocks too:
            // `;` sequences them after the wait's payload. Their trigger stays latched (above) so they
            // apply once the wait clears, rather than firing at the fix in parallel with the wait.
            if (holdApplies)
            {
                continue;
            }

            BlockApplyOutcome outcome = ApplyOrCountdownWait(aircraft, block, deltaSeconds);
            if (outcome == BlockApplyOutcome.Failed)
            {
                // Chain remainder already discarded; the queue list mutated under this loop —
                // stop scanning for the tick (latched triggers re-fire next sub-tick).
                return;
            }
            if (outcome == BlockApplyOutcome.Deferred)
            {
                // Waiting for its departure's release, trigger latched: skipped like an unmet trigger, so the
                // triggered blocks behind it still fire; the next sub-tick retries it.
                continue;
            }
            if (outcome == BlockApplyOutcome.WaitCounting)
            {
                holdApplies = true;
            }
        }
    }

    /// <summary>
    /// Queue advancement while the aircraft sits in a terminal command-waiting phase
    /// (<see cref="Phase.IsIdleAwaitingCommands"/>). Triggered blocks are handled exactly like
    /// <see cref="ApplyReadyConditionalBlocks"/>; additionally, untriggered blocks are applied in
    /// strict `;` order — an idle phase never completes on its own, so without this the blocks
    /// queued behind PUSH / TAXI-to-hold-short strand forever (issue #407). An untriggered block
    /// never leapfrogs an earlier unfired block, is held while a non-transparent command before its
    /// first phase-clearing command is Rejected by the idle phase (it stays queued and visible; the
    /// pre-check stops at the first phase-clearing command), counts a WAIT down before firing
    /// its payload, and stops advancing the moment an applied block leaves the idle phase.
    /// </summary>
    private static void AdvanceQueueWhileIdle(
        AircraftState aircraft,
        CommandQueue queue,
        int startIndex,
        double deltaSeconds,
        Func<string, AircraftState?>? aircraftLookup
    )
    {
        bool holdApplies = false;
        bool frontierBroken = false;
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is not null)
            {
                // Mirror ApplyReadyConditionalBlocks: latch the trigger once met; an unmet trigger
                // is skipped (later triggered siblings may still fire out of order) but pins the
                // frontier so no untriggered block behind it can jump the sequence.
                if (!block.TriggerMet)
                {
                    block.TriggerMet = IsTriggerMet(aircraft, block, deltaSeconds, aircraftLookup);
                    if (!block.TriggerMet)
                    {
                        TrackFrdMiss(aircraft, block);
                        frontierBroken = true;
                        continue;
                    }

                    if (block.Trigger.Type is BlockTriggerType.OnHandoff)
                    {
                        aircraft.Track.HandoffAccepted = false;
                    }
                }

                if (holdApplies)
                {
                    continue;
                }

                BlockApplyOutcome outcome = ApplyOrCountdownWait(aircraft, block, deltaSeconds);
                if (outcome == BlockApplyOutcome.Failed)
                {
                    // Chain remainder already discarded; stop scanning the mutated queue this tick.
                    return;
                }
                if (outcome == BlockApplyOutcome.Deferred)
                {
                    // Waiting for its departure's release: like an unmet trigger, later triggered blocks may still
                    // fire, but no untriggered block behind it jumps the sequence.
                    frontierBroken = true;
                    continue;
                }
                if (outcome == BlockApplyOutcome.WaitCounting)
                {
                    holdApplies = true;
                }

                continue;
            }

            if (frontierBroken || holdApplies)
            {
                break;
            }

            if (!TryApplyIdleUntriggeredBlock(aircraft, block, deltaSeconds))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Applies one untriggered block while the aircraft idles. Returns <c>false</c> when the idle
    /// scan must stop: the aircraft left its idle phase (an earlier applied block installed a new
    /// one), the idle phase rejects a command in the block ahead of its first phase-clearing command
    /// (it stays queued and visible), or an untriggered WAIT is still counting down. Consecutive
    /// returns of <c>true</c> deliberately do NOT wait for the previous block's tracked commands to complete — completion tracking does
    /// not run while a phase is active, so gating on it would re-strand pre-arm chains like
    /// <c>PUSH; CM 5000; TAXI ...</c> whose altitude target cannot complete on the ground.
    /// </summary>
    private static bool TryApplyIdleUntriggeredBlock(AircraftState aircraft, CommandBlock block, double deltaSeconds)
    {
        // An applied block may have installed a new phase (TAXI → TaxiingPhase) or cleared the
        // chain; only keep advancing while the aircraft is still idling.
        if (aircraft.Phases?.CurrentPhase is not { IsIdleAwaitingCommands: true } idlePhase)
        {
            return false;
        }

        if (block.ParsedCommands is not { Count: > 0 } parsed)
        {
            return false;
        }

        if (!IdlePhaseAcceptsBlock(idlePhase, parsed))
        {
            return false;
        }

        // An untriggered WAIT holds its payload until the countdown elapses (a triggered WAIT
        // is handled by ApplyOrCountdownWait on the triggered path).
        if (block.IsWaitBlock && !CheckWaitComplete(block, aircraft, deltaSeconds))
        {
            return false;
        }

        // A failed apply has already discarded the chain remainder, and a block waiting for its departure's release holds
        // its `;` successors behind it; either way stop the idle scan this tick.
        return ApplyBlock(aircraft, block) == BlockApplyOutcome.Applied;
    }

    /// <summary>
    /// Acceptance pre-check of an idle block against the idle phase. Phase-transparent commands
    /// (squawk family, say/report, ...) always pass — every ground hold phase nominally "Rejects"
    /// them, yet they apply fine through BuildApplyAction. A WAIT is queue mechanics, not a
    /// phase-interactive instruction — no phase accepts CanonicalCommandType.Wait. The check stops
    /// at the first command that clears the idle phase: the commands after it are judged at apply
    /// time by the phase it installs (BuildApplyAction re-reads the current phase per command), so
    /// <c>TAXI ..., HS B</c> fires while holding after pushback even though that phase rejects HS.
    /// </summary>
    private static bool IdlePhaseAcceptsBlock(Phase idlePhase, IReadOnlyList<ParsedCommand> parsed)
    {
        foreach (ParsedCommand cmd in parsed)
        {
            if (cmd is UnsupportedCommand or WaitCommand)
            {
                continue;
            }

            CanonicalCommandType canonical = CommandDescriber.ToCanonicalType(cmd);
            if (CommandDescriber.IsPhaseTransparent(canonical))
            {
                continue;
            }

            CommandAcceptance acceptance = idlePhase.CanAcceptCommand(canonical);
            if (acceptance.IsRejected)
            {
                return false;
            }

            if (acceptance.ClearsThePhase)
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Per-tick idle timer for ground aircraft. Accumulates the time an aircraft has been
    /// stopped (so a held aircraft can tell how long its yield target has been stationary)
    /// and resets the moment it moves or goes airborne.
    /// </summary>
    internal static void UpdateGroundStationaryTimer(AircraftState aircraft, double deltaSeconds)
    {
        if (aircraft.IsOnGround && aircraft.GroundSpeed <= GiveWayConstants.StationarySpeedThresholdKts)
        {
            aircraft.Ground.StationarySeconds += deltaSeconds;
        }
        else
        {
            aircraft.Ground.StationarySeconds = 0;
        }
    }

    /// <summary>
    /// Releases a direct GIVEWAY hold once the target is no longer in the way. The release is decided by where the two taxi
    /// routes meet (<see cref="GroundConflictDetector.GiveWayRouteVerdictOf(AircraftState, AircraftState)"/>, over the whole of
    /// both routes): at a node where they meet across each other, or, for a target running head-on along ours, decided at the
    /// from-node of the edge we are on when the traffic comes down it toward us, else at the first node of ours its route shares
    /// (see <see cref="GroundConflictDetector.GiveWayRouteVerdictOf"/>), the hold lasts until the target's tail has cleared it
    /// (<see cref="UpdateSharedNodeGiveWay"/>).
    /// With no such node — the target drives no route, or the routes neither cross nor run head-on — the release is the
    /// bearing/heading geometry in <see cref="IsGiveWayMet"/>, with its safety nets (<see cref="JudgeGiveWay"/>).
    /// </summary>
    internal static void UpdateGiveWayResume(AircraftState aircraft, Func<string, AircraftState?>? aircraftLookup, double deltaSeconds)
    {
        if ((aircraft.Ground.Hold is not { Kind: HoldKind.GiveWay, YieldTarget: { } yieldTarget }) || !aircraft.IsOnGround)
        {
            aircraft.Ground.HoldElapsedSeconds = 0;
            return;
        }

        // Still on the landing runway: the give-way is judged (and ages) only once the aircraft taxis.
        if (aircraft.HasGiveWayPendingOnLandingRunway)
        {
            return;
        }

        aircraft.Ground.HoldElapsedSeconds += deltaSeconds;

        if (aircraftLookup is null)
        {
            return;
        }

        AircraftState? target = aircraftLookup(yieldTarget);
        if ((target is null) || !target.IsOnGround)
        {
            Log.LogDebug("GIVEWAY released for {Callsign}: {Target} is gone or airborne", aircraft.Callsign, yieldTarget);
            ClearGiveWayHold(aircraft);
            return;
        }

        JudgeGiveWay(aircraft, yieldTarget, target, aircraftLookup);
    }

    /// <summary>
    /// The judgement of <paramref name="aircraft"/>'s GIVEWAY to <paramref name="target"/>, both on the ground. Routes that meet
    /// across each other at a node the target has still to clear, or whose target runs head-on along ours (decided at the
    /// from-node of the edge we are on when the traffic comes down it toward us, else at the first node of ours its route shares
    /// (see <see cref="GroundConflictDetector.GiveWayRouteVerdictOf"/>)), are decided there
    /// (<see cref="GroundConflictDetector.GiveWayRouteVerdictOf(AircraftState, AircraftState)"/>, <see cref="UpdateSharedNodeGiveWay"/>),
    /// and nothing else releases the hold before it. Otherwise, in order:
    /// a hold past its safety timeout (<see cref="UpdateGiveWaySafetyTimeout"/>); the pass geometry of <see cref="IsGiveWayMet"/>,
    /// kept while head-on routes still share an upcoming node; and the stalemate bypass of a stationary target
    /// (<see cref="UpdateStationaryTargetBypass"/>). The safety timeout and the bypass are for direct holds only: a deferred GIVEWAY
    /// and a queued GiveWay block (<see cref="IsQueuedGiveWayMet"/>) take the same shared-node judgement, wait while the target runs
    /// head-on along the route, and otherwise keep the pure geometry.
    /// </summary>
    private static void JudgeGiveWay(AircraftState aircraft, string yieldTarget, AircraftState target, Func<string, AircraftState?> aircraftLookup)
    {
        if (GroundConflictDetector.GiveWayRouteVerdictOf(aircraft, target) is { } verdict)
        {
            UpdateSharedNodeGiveWay(aircraft, target, verdict.Junction, verdict.Clear);
            return;
        }

        if (aircraft.Ground.HoldElapsedSeconds >= GiveWayConstants.SafetyTimeoutSeconds)
        {
            UpdateGiveWaySafetyTimeout(aircraft, yieldTarget, target);
            return;
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = yieldTarget };
        if (IsGiveWayMet(aircraft, trigger, aircraftLookup))
        {
            // Geometry says the target is past, but head-on traffic whose route still meets ours at an upcoming node is still in
            // our way at that intersection — keep holding.
            if (GroundConflictDetector.ShareUpcomingNode(aircraft, target))
            {
                if (LatchGiveWayKept(aircraft, KeptPastButRoutesMeetAhead, nodeId: null))
                {
                    Log.LogDebug(KeptPastButRoutesMeetAhead, aircraft.Callsign, yieldTarget);
                }

                return;
            }

            Log.LogDebug("GIVEWAY released for {Callsign}: no shared node with {Target}, and it is past", aircraft.Callsign, yieldTarget);
            ClearGiveWayHold(aircraft);
            return;
        }

        UpdateStationaryTargetBypass(aircraft, yieldTarget, target);
    }

    /// <summary>
    /// A hold past <see cref="GiveWayConstants.SafetyTimeoutSeconds"/> that no shared node decides (e.g. a typo'd-but-real
    /// distant target that will never satisfy the geometry) auto-releases — but only when releasing won't drive straight into
    /// the target. A target still close and dead ahead keeps the hold (the conflict detector continues to manage proximity); a
    /// distant or laterally-clear target releases as intended.
    /// </summary>
    private static void UpdateGiveWaySafetyTimeout(AircraftState aircraft, string yieldTarget, AircraftState target)
    {
        bool clearToProceed =
            GroundConflictDetector.HasWingspanLateralClearance(aircraft, target)
            || (GeoMath.DistanceNm(aircraft.Position, target.Position) > GiveWayConstants.SafetyTimeoutClearDistanceNm);
        if (clearToProceed)
        {
            Log.LogDebug(
                "GIVEWAY safety timeout fired for {Callsign} after {Seconds:F0}s waiting on {Target}",
                aircraft.Callsign,
                aircraft.Ground.HoldElapsedSeconds,
                yieldTarget
            );
            ClearGiveWayHold(aircraft);
            return;
        }

        if (LatchGiveWayKept(aircraft, KeptPastSafetyTimeout, nodeId: null))
        {
            Log.LogDebug(KeptPastSafetyTimeout, aircraft.Callsign, yieldTarget);
        }
    }

    /// <summary>
    /// Geometry says the target is still in the way. Break a stalemate: when the target has been stationary long enough and
    /// there is lateral room to bypass it, proceed. In a mutual yield (target is giving way to us) a deterministic callsign
    /// tie-break ensures only the lower-callsign aircraft releases first, so both never proceed on the same tick.
    /// </summary>
    private static void UpdateStationaryTargetBypass(AircraftState aircraft, string yieldTarget, AircraftState target)
    {
        if (target.Ground.StationarySeconds < GiveWayConstants.TargetStationaryThresholdSeconds)
        {
            if (LatchGiveWayKept(aircraft, KeptStillInTheWay, nodeId: null))
            {
                Log.LogDebug(KeptStillInTheWay, aircraft.Callsign, yieldTarget);
            }

            return;
        }

        if (IsMutualGiveWay(aircraft, target) && (string.CompareOrdinal(aircraft.Callsign, target.Callsign) >= 0))
        {
            if (LatchGiveWayKept(aircraft, KeptLowerCallsignFirst, nodeId: null))
            {
                Log.LogDebug(KeptLowerCallsignFirst, aircraft.Callsign, yieldTarget);
            }

            return;
        }

        if (GroundConflictDetector.HasWingspanLateralClearance(aircraft, target))
        {
            Log.LogDebug(
                "GIVEWAY target-stationary fallback fired for {Callsign} bypassing {Target} (stationary {Seconds:F0}s)",
                aircraft.Callsign,
                yieldTarget,
                target.Ground.StationarySeconds
            );
            ClearGiveWayHold(aircraft);
            return;
        }

        if (LatchGiveWayKept(aircraft, KeptNoRoomToPass, nodeId: null))
        {
            Log.LogDebug(KeptNoRoomToPass, aircraft.Callsign, yieldTarget);
        }
    }

    /// <summary>
    /// The GIVEWAY of <paramref name="aircraft"/> to <paramref name="target"/> while their routes meet across each other, or head-on,
    /// at <paramref name="junction"/>'s node (<see cref="GroundConflictDetector.GiveWayRouteVerdictOf(AircraftState, AircraftState)"/>):
    /// it is met once the target's tail has cleared the node (7110.65 3-7-2.a). A merge, where both routes leave the node the same
    /// way, is no exception: the in-trail spacing of the shared taxiway takes over only once the tail has cleared it. Before that
    /// only a mutual give-way releases the hold, which the callsign sorting first ordinally leaves first while the other's own
    /// routes still hold it for this one (<see cref="GroundConflictDetector.GiveWayRouteVerdictOf(AircraftState, AircraftState)"/>
    /// from its side says not clear, across or head-on), so the pair never deadlocks. Once the other is in the junction, its
    /// routes no longer holding it, it goes first and this one keeps holding.
    /// </summary>
    private static void UpdateSharedNodeGiveWay(
        AircraftState aircraft,
        AircraftState target,
        GroundConflictDetector.GiveWayJunction junction,
        bool clear
    )
    {
        if (clear)
        {
            Log.LogDebug(
                "GIVEWAY released for {Callsign}: {Target}'s tail is {TailPast:F0} ft past shared node {Node} (needs {Required:F0} ft)",
                aircraft.Callsign,
                target.Callsign,
                junction.TailPastFt,
                junction.NodeId,
                junction.RequiredPastFt
            );
            ClearGiveWayHold(aircraft);
            return;
        }

        if (
            IsMutualGiveWay(aircraft, target)
            && (string.CompareOrdinal(aircraft.Callsign, target.Callsign) < 0)
            && (GroundConflictDetector.GiveWayRouteVerdictOf(target, aircraft) is { Clear: false })
        )
        {
            Log.LogDebug(
                "GIVEWAY released for {Callsign}: {Target} gives way to it too at shared node {Node}, and the lower callsign goes first",
                aircraft.Callsign,
                target.Callsign,
                junction.NodeId
            );
            ClearGiveWayHold(aircraft);
            return;
        }

        if (LatchGiveWayKept(aircraft, KeptShortOfSharedNode, junction.NodeId))
        {
            Log.LogDebug(KeptShortOfSharedNode, aircraft.Callsign, target.Callsign, junction.TailPastFt, junction.NodeId, junction.RequiredPastFt);
        }
    }

    private const string KeptPastButRoutesMeetAhead = "GIVEWAY kept for {Callsign}: {Target} is past, but their routes still meet ahead";

    private const string KeptPastSafetyTimeout = "GIVEWAY kept for {Callsign} past its safety timeout: {Target} is close and dead ahead";

    private const string KeptStillInTheWay = "GIVEWAY kept for {Callsign}: no shared node with {Target}, and it is still in the way";

    private const string KeptLowerCallsignFirst = "GIVEWAY kept for {Callsign}: {Target} gives way to it too, and the lower callsign goes first";

    private const string KeptNoRoomToPass = "GIVEWAY kept for {Callsign}: {Target} is stationary, but there is no room to pass it";

    private const string KeptShortOfSharedNode =
        "GIVEWAY kept for {Callsign}: {Target}'s tail is {TailPast:F0} ft past shared node {Node}, short of the {Required:F0} ft it needs";

    /// <summary>
    /// The "kept" line last logged for each GIVEWAY hold: its message and the shared node it named. The judgement runs every
    /// tick, so a kept line is logged only when its reason changes (<see cref="LatchGiveWayKept"/>); keyed by the hold itself,
    /// so a new GIVEWAY starts unlatched.
    /// </summary>
    private static readonly ConditionalWeakTable<HoldDirective, KeptGiveWayLog> LoggedGiveWayKept = [];

    /// <summary>A "kept" line logged for a GIVEWAY hold: its message template and the shared node it named, if any.</summary>
    private sealed record KeptGiveWayLog(string Message, int? NodeId);

    /// <summary>
    /// The judgement last logged for each deferred GIVEWAY or queued GiveWay trigger, keyed by that object: both are judged every
    /// tick, so a judgement is logged only when it changes (<see cref="LatchGiveWayJudgement(DeferredDispatch, bool?, int?)"/>). The
    /// latch is for the log only, never read by a judgement; a restored deferral or trigger starts unlatched.
    /// </summary>
    private static readonly ConditionalWeakTable<object, GiveWayJudgementLog> LoggedGiveWayJudgements = [];

    /// <summary>A judgement logged for a deferred GIVEWAY or a queued GiveWay trigger: its verdict and the shared node it named, if any.</summary>
    private sealed record GiveWayJudgementLog(bool? Verdict, int? NodeId);

    /// <summary>Whether this judgement of <paramref name="deferral"/> differs from the last one logged for it, latching it when it does.</summary>
    internal static bool LatchGiveWayJudgement(DeferredDispatch deferral, bool? verdict, int? nodeId) =>
        LatchGiveWayJudgementOf(deferral, verdict, nodeId);

    /// <summary>Whether this judgement of <paramref name="trigger"/> differs from the last one logged for it, latching it when it does.</summary>
    internal static bool LatchGiveWayJudgement(BlockTrigger trigger, bool? verdict, int? nodeId) => LatchGiveWayJudgementOf(trigger, verdict, nodeId);

    private static bool LatchGiveWayJudgementOf(object owner, bool? verdict, int? nodeId)
    {
        var judgement = new GiveWayJudgementLog(verdict, nodeId);
        if (LoggedGiveWayJudgements.TryGetValue(owner, out GiveWayJudgementLog? last) && (last == judgement))
        {
            return false;
        }

        LoggedGiveWayJudgements.AddOrUpdate(owner, judgement);
        return true;
    }

    /// <summary>
    /// Whether the "kept" line <paramref name="message"/> at <paramref name="nodeId"/> differs from the last one logged for
    /// <paramref name="aircraft"/>'s hold, latching it when it does.
    /// </summary>
    private static bool LatchGiveWayKept(AircraftState aircraft, string message, int? nodeId)
    {
        if (aircraft.Ground.Hold is not { } hold)
        {
            return false;
        }

        if (LoggedGiveWayKept.TryGetValue(hold, out KeptGiveWayLog? last) && (last.Message == message) && (last.NodeId == nodeId))
        {
            return false;
        }

        LoggedGiveWayKept.AddOrUpdate(hold, new KeptGiveWayLog(message, nodeId));
        return true;
    }

    /// <summary>Whether <paramref name="target"/> is itself giving way to <paramref name="aircraft"/>.</summary>
    private static bool IsMutualGiveWay(AircraftState aircraft, AircraftState target) =>
        (target.Ground.Hold is { Kind: HoldKind.GiveWay, YieldTarget: { } targetYield })
        && string.Equals(targetYield, aircraft.Callsign, StringComparison.OrdinalIgnoreCase);

    private static void ClearGiveWayHold(AircraftState aircraft)
    {
        aircraft.Ground.Hold = null;
        aircraft.Ground.HoldElapsedSeconds = 0;
    }

    private static void UpdateBlockCompletion(AircraftState aircraft, CommandBlock block, double deltaSeconds)
    {
        foreach (TrackedCommand cmd in block.Commands)
        {
            if (cmd.IsComplete)
            {
                continue;
            }

            cmd.IsComplete = cmd.Type switch
            {
                TrackedCommandType.Heading => IsHeadingReached(aircraft),
                // On the ground the altitude loop never runs (UpdateAltitude early-returns), so the
                // target can't be reached and nulled; treat a grounded altitude assignment as
                // accepted-and-pending — the chain advances while TargetAltitude stays armed for departure.
                TrackedCommandType.Altitude => (aircraft.Targets.TargetAltitude is null) || aircraft.IsOnGround,
                // A speed the 91.117 clamp holds the aircraft short of keeps its target standing (ArriveAtGoal), so the
                // assignment being flown out no longer shows as a null target; the aircraft is at the fastest speed it
                // may fly here and the instruction is as done as it can be, which is what the chain advances on.
                TrackedCommandType.Speed => (aircraft.Targets.TargetSpeed is null) || IsSpeedAssignmentHeldAtSpeedLimit(aircraft),
                TrackedCommandType.Navigation => aircraft.Targets.NavigationRoute.Count == 0,
                TrackedCommandType.Immediate => true,
                TrackedCommandType.Wait => CheckWaitComplete(block, aircraft, deltaSeconds),
                _ => true,
            };
        }
    }

    private static bool IsHeadingReached(AircraftState aircraft)
    {
        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            return false;
        }

        if (aircraft.Targets.TargetTrueHeading is not { } target)
        {
            return true;
        }

        double diff = NormalizeAngle(aircraft.TrueHeading.Degrees - target.Degrees);
        return Math.Abs(diff) < HeadingSnapDeg;
    }

    private static bool CheckWaitComplete(CommandBlock block, AircraftState aircraft, double deltaSeconds)
    {
        if (block.WaitRemainingSeconds > 0)
        {
            block.WaitRemainingSeconds -= deltaSeconds;
            return block.WaitRemainingSeconds <= 0;
        }

        if (block.WaitRemainingDistanceNm > 0)
        {
            double distanceTraveled = (aircraft.GroundSpeed / 3600.0) * deltaSeconds;
            block.WaitRemainingDistanceNm -= distanceTraveled;
            return block.WaitRemainingDistanceNm <= 0;
        }

        return true;
    }

    private static bool IsTriggerMet(AircraftState aircraft, CommandBlock block, double deltaSeconds, Func<string, AircraftState?>? aircraftLookup)
    {
        BlockTrigger trigger = block.Trigger!;
        return trigger.Type switch
        {
            BlockTriggerType.ReachAltitude => trigger.Altitude.HasValue
                && Math.Abs(aircraft.Altitude - trigger.Altitude.Value) < ReachAltitudeToleranceFt(aircraft, deltaSeconds),
            BlockTriggerType.ReachFix => trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.FixLat.Value, trigger.FixLon.Value)) < NavArrivalNm,
            BlockTriggerType.InterceptRadial => trigger.FixLat.HasValue
                && trigger.FixLon.HasValue
                && trigger.TrueRadial.HasValue
                && IsRadialIntercepted(aircraft, trigger),
            BlockTriggerType.ReachFrdPoint => trigger.TargetLat.HasValue
                && trigger.TargetLon.HasValue
                && GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.TargetLat.Value, trigger.TargetLon.Value)) < FrdArrivalNm,
            BlockTriggerType.GiveWay => IsQueuedGiveWayMet(aircraft, trigger, aircraftLookup),
            BlockTriggerType.DistanceFinal => IsDistanceFinalMet(aircraft, trigger),
            BlockTriggerType.OnHandoff => aircraft.Track.HandoffAccepted,
            BlockTriggerType.AtGroundEntity => IsGroundEntityReached(aircraft, trigger),
            BlockTriggerType.EnteringHoldingAfterExit => aircraft.Phases?.CurrentPhase is Yaat.Sim.Phases.Ground.HoldingAfterExitPhase,
            BlockTriggerType.AfterRunwayCrossing => IsAfterRunwayCrossingMet(aircraft, block),
            BlockTriggerType.AfterCycleTerminator => IsAfterCycleTerminatorMet(aircraft, block),
            _ => true,
        };
    }

    /// <summary>
    /// Capture band for a <see cref="BlockTriggerType.ReachAltitude"/> trigger. The altitude is
    /// only sampled once per tick, so a fixed band narrower than one tick of travel can be
    /// stepped clean over — an expedited dive covers well over 20 ft per sub-tick, and the
    /// trigger would then never fire. Widening the band to one tick of travel guarantees at
    /// least one sample lands inside it.
    /// </summary>
    private static double ReachAltitudeToleranceFt(AircraftState aircraft, double deltaSeconds) =>
        Math.Max(AltitudeSnapFt, Math.Abs(aircraft.VerticalSpeed) / 60.0 * deltaSeconds);

    /// <summary>
    /// Whether <paramref name="follow"/> is done crossing <paramref name="runway"/>: its crossing clearance spent (off the pavement)
    /// and the follower's tail and wingtips past its bars (no longer among <see cref="Yaat.Sim.Phases.Ground.FollowingPhase.ExitingRunways"/>).
    /// </summary>
    private static bool IsDoneCrossing(Yaat.Sim.Phases.Ground.FollowingPhase follow, RunwayIdentifier runway) =>
        !follow.CrossingClearedRunways.Any(r => r.Overlaps(runway)) && !follow.ExitingRunways.Any(r => r.Overlaps(runway));

    /// <summary>
    /// Latches <see cref="CommandBlock.TriggerMet"/> on every other unapplied block of <paramref name="aircraft"/>'s queue waiting on
    /// the same follow crossing as <paramref name="met"/>, so the blocks chained behind it are not stranded once it changes the phase.
    /// </summary>
    private static void LatchSiblingFollowCrossingTriggers(AircraftState aircraft, CommandBlock met, IReadOnlyList<RunwayIdentifier> runways)
    {
        foreach (CommandBlock block in aircraft.Queue.Blocks)
        {
            if (
                !ReferenceEquals(block, met)
                && !block.IsApplied
                && (block.Trigger is { Type: BlockTriggerType.AfterRunwayCrossing, FollowCrossingRunways: { } other })
                && other.SequenceEqual(runways)
            )
            {
                block.TriggerMet = true;
            }
        }
    }

    /// <summary>
    /// Latches when the aircraft enters <see cref="Yaat.Sim.Phases.Ground.CrossingRunwayPhase"/>,
    /// fires once it has exited that phase — regardless of what phase comes
    /// next. The latch is the load-bearing piece: it distinguishes the
    /// pre-crossing armed state (where the aircraft is still in
    /// <see cref="Yaat.Sim.Phases.Ground.HoldingShortPhase"/> or
    /// <see cref="Yaat.Sim.Phases.Ground.TaxiingPhase"/> heading toward the
    /// crossing) from the post-crossing fired state (where the aircraft may
    /// land directly in HoldingShortPhase of the next parallel runway with no
    /// taxiing gap, as at OAK 28R→28L on taxiway B).
    /// </summary>
    private static bool IsAfterRunwayCrossingMet(AircraftState aircraft, CommandBlock block)
    {
        Phase? phase = aircraft.Phases?.CurrentPhase;

        // A FOLLOWG follower crosses inside its follow, never in a CrossingRunwayPhase: the trigger fires once the follow is done with
        // every runway the CROSS named. The blocks chained with this one share its trigger and fire behind it by the `;` rules, even
        // once the block that fired has changed the phase.
        if (block.Trigger!.FollowCrossingRunways is { } followRunways)
        {
            if ((phase is not Yaat.Sim.Phases.Ground.FollowingPhase follow) || !followRunways.All(r => IsDoneCrossing(follow, r)))
            {
                return false;
            }

            LatchSiblingFollowCrossingTriggers(aircraft, block, followRunways);
            return true;
        }

        if (phase is Yaat.Sim.Phases.Ground.CrossingRunwayPhase)
        {
            block.TriggerCrossingObserved = true;
            return false;
        }

        return block.TriggerCrossingObserved;
    }

    /// <summary>
    /// Latches when the aircraft enters a cycle-terminator phase — touch-and-go, stop-and-go,
    /// low approach or go-around — and fires once it has left that phase and is airborne again.
    /// The latch is what makes the trigger mean "after the next one": an aircraft issued
    /// <c>OTG</c> while already climbing out has no terminator behind it, so the block simply
    /// waits for the next circuit rather than firing immediately. The airborne check keeps a
    /// stop-and-go from firing during its stop on the runway, where the phase has ended for the
    /// roll but the aircraft has not gone anywhere yet.
    /// </summary>
    private static bool IsAfterCycleTerminatorMet(AircraftState aircraft, CommandBlock block)
    {
        Phase? phase = aircraft.Phases?.CurrentPhase;
        if (
            phase
            is Yaat.Sim.Phases.Tower.TouchAndGoPhase
                or Yaat.Sim.Phases.Tower.StopAndGoPhase
                or Yaat.Sim.Phases.Tower.LowApproachPhase
                or Yaat.Sim.Phases.Tower.GoAroundPhase
        )
        {
            block.TriggerTerminatorObserved = true;
            return false;
        }

        if (HasLandedFullStop(aircraft, phase))
        {
            MarkCycleTerminatorMissed(block);
            return false;
        }

        return block.TriggerTerminatorObserved && !aircraft.IsOnGround;
    }

    /// <summary>
    /// The aircraft has ended the cycle on the runway instead of climbing out of it: rolling out from a
    /// full-stop landing, or already off the runway. Detected the same way the terminator latch is —
    /// from the phase the aircraft is in — so a block armed on the approach and a block that latched
    /// during the terminator are both caught.
    /// </summary>
    private static bool HasLandedFullStop(AircraftState aircraft, Yaat.Sim.Phases.Phase? phase) =>
        (phase is Yaat.Sim.Phases.Ground.RunwayExitPhase or Yaat.Sim.Phases.Ground.HoldingAfterExitPhase)
        || ((phase is Yaat.Sim.Phases.Tower.LandingPhase or Yaat.Sim.Phases.Tower.HelicopterLandingPhase) && aircraft.IsOnGround);

    /// <summary>
    /// The <c>OTG</c> condition can no longer be met — the aircraft landed full stop, so there is no
    /// climb-out for the block to fire on. Only the flag is set here; the removal and the warning belong
    /// to <see cref="DiscardMissedCycleTerminatorBlocks"/>, which runs where the block list may be
    /// mutated. An aircraft cleared for the option becomes a departure only once it touches down and
    /// continues (7110.65 §3-8-2); a full stop ends the cycle instead, and the pilot simply could not
    /// comply with the queued instruction — P/CG UNABLE.
    /// </summary>
    private static void MarkCycleTerminatorMissed(CommandBlock block) => block.TriggerMissed = true;

    /// <summary>
    /// Drops the blocks whose <see cref="BlockTriggerType.AfterCycleTerminator"/> condition was marked
    /// missed by <see cref="MarkCycleTerminatorMissed"/>, and tells the RPO. Done here rather than at
    /// detection time because the detection runs inside the queue scans, which must not mutate the block
    /// list under themselves; one pass at the top of the queue update owns the removal.
    ///
    /// <para>The rest of the missed block's own transmission goes with it
    /// (<see cref="CommandQueue.DiscardChainRemainder"/>, the same abort a fire-time failure runs): the
    /// follow-on instructions were premised on the one that can no longer happen. The warning quotes the
    /// source text verbatim — never through a runway de-padder, which would turn the <c>015</c> of
    /// <c>OTG MLT 28R 015</c> into a <c>15</c> that reads as a runway.</para>
    /// </summary>
    private static void DiscardMissedCycleTerminatorBlocks(AircraftState aircraft)
    {
        CommandQueue queue = aircraft.Queue;
        for (int i = queue.Blocks.Count - 1; i >= 0; i--)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied || !block.TriggerMissed || (block.Trigger?.Type is not BlockTriggerType.AfterCycleTerminator))
            {
                continue;
            }

            List<string> discarded = queue.DiscardChainRemainder(block);
            int index = queue.Blocks.IndexOf(block);
            if (index >= 0)
            {
                queue.Blocks.RemoveAt(index);
                if (queue.CurrentBlockIndex > index)
                {
                    queue.CurrentBlockIndex--;
                }
            }

            string src = !string.IsNullOrEmpty(block.SourceCommandText)
                ? block.SourceCommandText
                : (!string.IsNullOrEmpty(block.Description) ? block.Description : block.NaturalDescription);
            string warning = $"{aircraft.Callsign} {src}: unable — landed full stop";
            if (discarded.Count > 0)
            {
                warning += $" — rest of transmission discarded ({discarded.Count}): {string.Join("; ", discarded)}";
            }
            aircraft.PendingWarnings.Add(warning);
        }

        if (queue.CurrentBlockIndex > queue.Blocks.Count)
        {
            queue.CurrentBlockIndex = queue.Blocks.Count;
        }
    }

    private static bool IsGroundEntityReached(AircraftState aircraft, BlockTrigger trigger)
    {
        // Taxiway-only triggers fire from TaxiingPhase via NotifyGroundEntityReached;
        // there's no per-tick "am I on taxiway X" check here. The route cursor is the
        // source of truth and only the phase callback knows when it transitions.
        if (trigger.GroundKind == GroundEntityKind.Taxiway)
        {
            return false;
        }

        if (!trigger.FixLat.HasValue || !trigger.FixLon.HasValue)
        {
            return false;
        }

        return GeoMath.DistanceNm(aircraft.Position, new LatLon(trigger.FixLat.Value, trigger.FixLon.Value)) < GroundArrivalNm;
    }

    internal static bool IsGiveWayMet(AircraftState aircraft, BlockTrigger trigger, Func<string, AircraftState?>? aircraftLookup)
    {
        if (trigger.TargetCallsign is null || aircraftLookup is null)
        {
            return true;
        }

        AircraftState? target = aircraftLookup(trigger.TargetCallsign);
        if (target is null || !target.IsOnGround)
        {
            // Target is gone or airborne — no conflict
            return true;
        }

        // The held aircraft proceeds only when the target is no longer in the way:
        // either behind us (opposite-direction case) or ahead and moving away
        // (same-direction case). Distance alone is not a release condition — for
        // ground BEHIND, the target is virtually always >0.1 nm away when the
        // command is issued (that's the point of issuing it).
        double headingDiff = Math.Abs(aircraft.TrueHeading.Degrees - target.TrueHeading.Degrees);
        if (headingDiff > 180)
        {
            headingDiff = 360 - headingDiff;
        }

        double bearingToTarget = GeoMath.BearingTo(aircraft.Position, target.Position);
        double diffToTarget = Math.Abs(aircraft.TrueHeading.Degrees - bearingToTarget);
        if (diffToTarget > 180)
        {
            diffToTarget = 360 - diffToTarget;
        }

        // Opposite direction: conflict resolved when no longer head-on
        if (headingDiff > 120)
        {
            return diffToTarget > 90;
        }

        // Same direction: conflict resolved when target is ahead of us
        if (headingDiff < 60)
        {
            return diffToTarget < 90;
        }

        // Neither same nor opposite — no conflict
        return true;
    }

    /// <summary>
    /// The trigger of a GiveWay block queued behind other blocks. The route the aircraft is driving decides first, as in
    /// <see cref="JudgeGiveWay"/>: what the routes say is final, either way
    /// (<see cref="GroundConflictDetector.GiveWayRouteVerdictOf(AircraftState, AircraftState, TaxiRoute)"/>: a crossing, or
    /// decided at the from-node of the edge we are on when the traffic comes down it toward us, else at the first node of ours its route shares
    /// (see <see cref="GroundConflictDetector.GiveWayRouteVerdictOf"/>)). With no node to decide, the geometry of
    /// <see cref="IsGiveWayMet"/> stands, as it does with no route to judge.
    /// </summary>
    private static bool IsQueuedGiveWayMet(AircraftState aircraft, BlockTrigger trigger, Func<string, AircraftState?>? aircraftLookup)
    {
        if (
            (trigger.TargetCallsign is not { } targetCallsign)
            || (aircraftLookup?.Invoke(targetCallsign) is not { IsOnGround: true } target)
            || (Phases.Ground.FollowingPhase.DrivenRouteOf(aircraft) is not { } route)
        )
        {
            return IsGiveWayMet(aircraft, trigger, aircraftLookup);
        }

        GroundConflictDetector.GiveWayRouteVerdict? verdict = GroundConflictDetector.GiveWayRouteVerdictOf(aircraft, target, route);
        bool met = verdict?.Clear ?? IsGiveWayMet(aircraft, trigger, aircraftLookup);
        if (LatchGiveWayJudgement(trigger, met, verdict?.Junction.NodeId))
        {
            Log.LogDebug(
                "Queued GIVEWAY for {Callsign} behind {Target}: met={Met}, deciding node {Node}, tail past {TailPast:F0} ft",
                aircraft.Callsign,
                targetCallsign,
                met,
                verdict?.Junction.NodeId,
                verdict?.Junction.TailPastFt
            );
        }

        return met;
    }

    private static bool IsDistanceFinalMet(AircraftState aircraft, BlockTrigger trigger)
    {
        if (trigger.DistanceFinalNm is not { } distNm)
        {
            return false;
        }

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (runway is null)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
        return dist <= distNm;
    }

    /// <summary>
    /// Releases ATC speed restrictions at 5 nm final (7110.65 §5-7-1.b.4): the controller can no longer adjust the
    /// speed, so the explicit assignment is dropped, and with it the controller crossing speeds still on the route, and
    /// the pilot owns the approach speed — but the aircraft must never accelerate with the runway in sight, so the
    /// last-assigned speed is retained as a ceiling (<see cref="ReleaseSpeedAssignmentAtGate"/>). A phase that owns
    /// speed (an active instrument approach, final) clears the ceiling instead of holding it. An aircraft inbound to land
    /// with neither an explicit speed nor a route, and a departure or a go-around, are untouched, and a forced assignment
    /// (SPEEDF / SPEEDN) is exempt so the controller's override persists. Called from Update() after UpdateSpeed(); the
    /// stale ceiling is cleared on go-around (GoAroundPhase.OnStart) so the missed-approach climb is not capped.
    /// </summary>
    private static void AutoCancelSpeedAtFinal(AircraftState aircraft)
    {
        ControlTargets targets = aircraft.Targets;

        // Nothing to release without an explicit speed and with no route a crossing speed could sit on, and a forced
        // assignment is exempt from the gate altogether.
        if (targets.SpeedOverridesFinalGate || (!targets.HasExplicitSpeedCommand && (targets.NavigationRoute.Count == 0)))
        {
            return;
        }

        if (Commands.FlightCommandHandler.FinalGateRunway(aircraft) is not { } runway)
        {
            return;
        }

        // A controller crossing speed on a fix still ahead inside the gate ends here: the pilot flies the approach speed
        // from the gate, and nothing rides on a crossing the controller may no longer adjust.
        bool crossingEnded = CancelControllerCrossingSpeedsInsideFinalGate(aircraft, runway);
        if (!crossingEnded && !targets.HasExplicitSpeedCommand)
        {
            return;
        }

        if (crossingEnded)
        {
            targets.AssignedSpeed = null;
        }

        ReleaseSpeedAssignmentAtGate(aircraft);
    }

    /// <summary>
    /// Drops the ATC speed assignment at the gate so the pilot owns the approach speed, and leaves the aircraft unable to
    /// accelerate: the target goes, and either the phase that owns speed keeps the ceiling cleared or the last-assigned
    /// speed stays as a ceiling at the lower of the existing ceiling, the speed the controller last commanded and the
    /// present IAS.
    /// </summary>
    private static void ReleaseSpeedAssignmentAtGate(AircraftState aircraft)
    {
        // The speed the controller last commanded, read before the assignment is released.
        double reference = aircraft.Targets.TargetSpeed ?? aircraft.Targets.SpeedCeiling ?? aircraft.IndicatedAirspeed;

        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.HasExplicitSpeedCommand = false;
        aircraft.Targets.SpeedCommandIsControllerIssued = false;
        aircraft.Targets.SpeedMaxHeld = false;
        aircraft.Targets.SpeedFloor = null;

        // A phase that owns speed (pattern legs, an active instrument approach, final) keeps the aircraft from
        // accelerating on its own — clear the ceiling outright. Otherwise the auto speed schedule would re-accelerate a
        // hand-vectored inbound toward the descent default, so retain the last-assigned speed as a ceiling: the aircraft
        // holds it or eases down to its approach speed but can never speed back up.
        if ((aircraft.Phases?.ActiveApproach is not null) || (aircraft.Phases?.CurrentPhase?.ManagesSpeed == true))
        {
            aircraft.Targets.SpeedCeiling = null;
            return;
        }

        double ceiling = aircraft.Targets.SpeedCeiling is { } existing ? Math.Min(existing, aircraft.IndicatedAirspeed) : aircraft.IndicatedAirspeed;
        aircraft.Targets.SpeedCeiling = Math.Min(reference, ceiling);
    }

    /// <summary>
    /// Ends the controller speeds on the fixes still ahead inside the 5 nm gate: a fix whose crossing speed a controller
    /// set loses the restriction and its stamp, and every controller-marked fix inside the gate loses its speed reverts,
    /// so nothing crosses at a speed the controller may no longer assign and nothing is restored past the gate. A speed
    /// the procedure publishes stays. Returns whether a crossing speed was ended.
    /// </summary>
    private static bool CancelControllerCrossingSpeedsInsideFinalGate(AircraftState aircraft, RunwayInfo runway)
    {
        LatLon threshold = LandingThreshold.Resolve(runway, aircraft.Ground.Layout);
        bool crossingEnded = false;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (!IsControllerSetSpeed(fix) || (GeoMath.DistanceNm(fix.Position, threshold) > Commands.FlightCommandHandler.FinalGateNm))
            {
                continue;
            }

            if (fix.SpeedTransmissionId is not null)
            {
                fix.SpeedRestriction = null;
                fix.SpeedTransmissionId = null;
                crossingEnded = true;
            }

            Commands.NavigationCommandHandler.ClearSpeedReverts(fix);
        }

        return crossingEnded;
    }

    private static bool IsRadialIntercepted(AircraftState aircraft, BlockTrigger trigger)
    {
        double bearing = GeoMath.BearingTo(new LatLon(trigger.FixLat!.Value, trigger.FixLon!.Value), aircraft.Position);
        double diff = Math.Abs(NormalizeAngle(bearing - trigger.TrueRadial!.Value));
        return diff < RadialInterceptDeg;
    }

    private static void TrackFrdMiss(AircraftState aircraft, CommandBlock block)
    {
        if (
            block.TriggerMissed
            || block.Trigger?.Type is not BlockTriggerType.ReachFrdPoint
            || !block.Trigger.TargetLat.HasValue
            || !block.Trigger.TargetLon.HasValue
        )
        {
            return;
        }

        double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(block.Trigger.TargetLat.Value, block.Trigger.TargetLon.Value));

        if (dist < block.TriggerClosestApproach)
        {
            block.TriggerClosestApproach = dist;
        }
        else if (block.TriggerClosestApproach < FrdMissThresholdNm && dist > block.TriggerClosestApproach + FrdMissDepartureNm)
        {
            block.TriggerMissed = true;
            string fixName = block.Trigger.FixName ?? "?";
            string radial = block.Trigger.IssuedRadial?.ToString("D3") ?? "???";
            string distNm = block.Trigger.DistanceNm?.ToString("D3") ?? "???";
            aircraft.PendingWarnings.Add($"Missed condition at {fixName} R{radial} D{distNm} " + $"(closest: {block.TriggerClosestApproach:F1} NM)");
        }
    }

    /// <summary>
    /// Fires a queued block's <see cref="CommandBlock.ApplyAction"/>. Returns <see cref="BlockApplyOutcome.Applied"/> on success.
    /// On failure (<see cref="BlockApplyOutcome.Failed"/>) the remainder of the block's own chain is discarded
    /// (<see cref="CommandQueue.DiscardChainRemainder"/>) — the follow-on instructions were premised
    /// on the failed one — and one warning names both the failure and the discarded blocks. Every
    /// fire-time apply path funnels through here so the abort covers all of them; callers must stop
    /// scanning the (now mutated) queue for the rest of the tick on a failure.
    /// Returns <see cref="BlockApplyOutcome.Deferred"/>, leaving the block unapplied and its chain intact, while the
    /// block waits for its departure's release (<see cref="WaitsForRelease"/>); the next scan retries it.
    /// </summary>
    private static BlockApplyOutcome ApplyBlock(AircraftState aircraft, CommandBlock block)
    {
        if (WaitsForRelease(aircraft, block))
        {
            return BlockApplyOutcome.Deferred;
        }

        block.IsApplied = true;
        CommandResult? result = block.ApplyAction?.Invoke(aircraft, true);

        if (result is not null && !result.Success)
        {
            Log.LogWarning("Triggered block failed during apply: {Message}", result.Message);

            // CommandDispatcher.DryRunValidate only validates the first
            // immediately-applied block — deferred blocks reach their handler
            // here at trigger fire time. Surface failures so the RPO sees them
            // in the terminal log rather than having them swallowed. The source text
            // is quoted verbatim: the runway de-padder strips the leading zero from
            // every 0-led token, and in an argument slot where a runway and an
            // altitude compete a de-padded "015" reads back as runway 15.
            string src = !string.IsNullOrEmpty(block.SourceCommandText)
                ? block.SourceCommandText
                : (!string.IsNullOrEmpty(block.Description) ? block.Description : block.NaturalDescription);
            string reason = !string.IsNullOrEmpty(result.Message) ? result.Message : "command failed";
            List<string> discarded = aircraft.Queue.DiscardChainRemainder(block);
            string warning = $"{aircraft.Callsign} {src}: {reason}";
            if (discarded.Count > 0)
            {
                warning += $" — rest of transmission discarded: {string.Join("; ", discarded)}";
            }
            aircraft.PendingWarnings.Add(warning);
            return BlockApplyOutcome.Failed;
        }

        if (result is { Success: true, Message: not null })
        {
            block.NaturalDescription = result.Message;
        }

        foreach (TrackedCommand cmd in block.Commands)
        {
            if (cmd.Type == TrackedCommandType.Immediate)
            {
                cmd.IsComplete = true;
            }
        }

        if (block.Trigger is not null)
        {
            string desc = RunwayIdentifier.ToDisplayDesignator(block.NaturalDescription.Length > 0 ? block.NaturalDescription : block.Description);
            aircraft.PendingNotifications.Add($"[Executing] {desc}");
        }

        return BlockApplyOutcome.Applied;
    }

    /// <summary>
    /// A queued block that enters the runway (<see cref="HeldReleaseService.IsRunwayEntryCommand"/>) for a departure held
    /// for release waits in the queue, unapplied, until the release (REL or HFROFF) clears the hold, rather than meeting the
    /// dispatcher's hold-for-release refusal and being dropped with its chain. A takeoff clearance the controller chained or
    /// scheduled is delayed by the hold, never lost. A directly typed CTO is answered by the dispatcher's gate instead, since
    /// it never reaches the queue. The first time a block waits, the RPO is told once.
    /// </summary>
    private static bool WaitsForRelease(AircraftState aircraft, CommandBlock block)
    {
        if ((block.ParsedCommands is not { } parsed) || !parsed.Exists(HeldReleaseService.IsRunwayEntryCommand))
        {
            return false;
        }

        if (aircraft.Ground.HeldForRelease)
        {
            if (!block.WaitingForRelease)
            {
                block.WaitingForRelease = true;
                string desc = RunwayIdentifier.ToDisplayDesignator(block.Description);
                aircraft.PendingNotifications.Add($"{aircraft.Callsign} {desc} waits for the release");
                Log.LogDebug("{Callsign}: queued {Block} waits for the release", aircraft.Callsign, block.Description);
            }

            return true;
        }

        if (block.WaitingForRelease)
        {
            block.WaitingForRelease = false;
            Log.LogDebug("{Callsign}: released, queued {Block} resumes", aircraft.Callsign, block.Description);
        }

        return false;
    }

    /// <summary>
    /// Outcome of one <see cref="ApplyBlock"/> or <see cref="ApplyOrCountdownWait"/> attempt. <see cref="Failed"/> means
    /// the block fired and its handler failed — the chain remainder is already discarded and the caller must stop
    /// scanning the mutated queue for the rest of the tick. <see cref="Deferred"/> means the block did not fire because it
    /// waits for its departure's release (<see cref="WaitsForRelease"/>): the queue is untouched, the caller treats it as
    /// an unmet trigger, and a later scan retries it.
    /// </summary>
    private enum BlockApplyOutcome
    {
        WaitCounting,
        Applied,
        Failed,
        Deferred,
    }

    /// <summary>
    /// Applies a triggered block, but first counts down a post-trigger WAIT so the payload fires the
    /// wait's duration <em>after</em> the trigger fires — not on the trigger itself (issue #286).
    /// Returns <see cref="BlockApplyOutcome.WaitCounting"/> while the wait is still counting down. Only
    /// engages for a block that is both triggered and a wait block; a standalone (untriggered) WAIT
    /// keeps applying immediately and counting down as the current block.
    /// </summary>
    private static BlockApplyOutcome ApplyOrCountdownWait(AircraftState aircraft, CommandBlock block, double deltaSeconds)
    {
        if (block.Trigger is not null && block.IsWaitBlock && !CheckWaitComplete(block, aircraft, deltaSeconds))
        {
            return BlockApplyOutcome.WaitCounting;
        }

        return ApplyBlock(aircraft, block);
    }

    /// <summary>
    /// Called when a fix is sequenced from the navigation route or an approach phase.
    /// Scans the command queue for any pending block with a ReachFix trigger matching
    /// the given fix name and fires it immediately. This ensures AT fix triggers work
    /// even when phases are active (where UpdateCommandQueue is normally skipped) and
    /// when turn anticipation sequences the fix at a distance greater than NavArrivalNm.
    /// </summary>
    public static void NotifyFixSequenced(AircraftState aircraft, string fixName)
    {
        CommandQueue queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        bool pendingWaitAhead = false;
        for (int i = 0; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is not { Type: BlockTriggerType.ReachFix } trigger)
            {
                continue;
            }

            if (!string.Equals(trigger.FixName, fixName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Log.LogDebug("[AtFix] {Callsign}: fix {Fix} sequenced from route, firing block {Idx}", aircraft.Callsign, fixName, i);

            block.TriggerMet = true;

            // A triggered WAIT latches its trigger here but is applied only once its countdown elapses,
            // driven by the per-tick queue update — so the payload fires n seconds after the fix, not on
            // it (issue #286). Blocks sequenced after it (`;`) latch too but must not fire ahead of the
            // wait's payload, so hold them for the per-tick update as well.
            if (block.IsWaitBlock)
            {
                pendingWaitAhead = true;
            }
            else if (!pendingWaitAhead && (ApplyBlock(aircraft, block) == BlockApplyOutcome.Failed))
            {
                // Chain remainder already discarded; stop scanning the mutated queue. A block deferred for its
                // departure's release keeps its latched trigger and the scan goes on, so a later block on the same
                // event still latches and fires.
                return;
            }
        }
    }

    /// <summary>
    /// Called by <see cref="Phases.Ground.TaxiingPhase"/> on segment transitions and node
    /// arrivals. Scans the command queue for any pending AT ground-entity block whose
    /// node id matches <paramref name="arrivedNodeId"/> or whose taxiway name matches
    /// <paramref name="newTaxiwayName"/> and fires it immediately. Mirrors
    /// <see cref="NotifyFixSequenced"/> — required because <see cref="UpdateCommandQueue"/>
    /// is skipped while a phase is active, and ground aircraft are essentially always in
    /// a phase. Idempotent: already-applied blocks are skipped, so double-calls are safe.
    /// </summary>
    public static void NotifyGroundEntityReached(AircraftState aircraft, int? arrivedNodeId, string? newTaxiwayName)
    {
        if (arrivedNodeId is null && string.IsNullOrEmpty(newTaxiwayName))
        {
            return;
        }

        CommandQueue queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        bool pendingWaitAhead = false;
        for (int i = 0; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            if (block.Trigger is not { Type: BlockTriggerType.AtGroundEntity } trigger)
            {
                continue;
            }

            bool nodeMatch = arrivedNodeId.HasValue && trigger.GroundNodeId == arrivedNodeId.Value;
            bool taxiwayMatch =
                !string.IsNullOrEmpty(newTaxiwayName)
                && !string.IsNullOrEmpty(trigger.GroundTaxiwayName)
                && string.Equals(trigger.GroundTaxiwayName, newTaxiwayName, StringComparison.OrdinalIgnoreCase);

            if (!nodeMatch && !taxiwayMatch)
            {
                continue;
            }

            Log.LogDebug(
                "[AtGround] {Callsign}: ground entity reached (kind={Kind}, node={Node}, taxiway={Taxi}), firing block {Idx}",
                aircraft.Callsign,
                trigger.GroundKind,
                arrivedNodeId,
                newTaxiwayName,
                i
            );

            block.TriggerMet = true;

            // See NotifyFixSequenced: a triggered WAIT latches here and applies its payload only after
            // its countdown; blocks sequenced after it (`;`) are held for the per-tick update too.
            if (block.IsWaitBlock)
            {
                pendingWaitAhead = true;
            }
            else if (!pendingWaitAhead && (ApplyBlock(aircraft, block) == BlockApplyOutcome.Failed))
            {
                // Chain remainder already discarded; stop scanning the mutated queue. A block deferred for its
                // departure's release keeps its latched trigger and the scan goes on, so a later block on the same
                // event still latches and fires.
                return;
            }
        }
    }

    /// <summary>
    /// Called by <see cref="Phases.PhaseRunner"/> after a phase advances to a new
    /// current phase. If the new phase is a "wait" phase (has at least one
    /// unsatisfied clearance requirement, e.g. HoldingShortPhase waiting on
    /// RunwayCrossing) and the head of the command queue is an untriggered,
    /// unapplied block whose commands the new phase accepts, fire it now. This
    /// lets sequential compounds like <c>TAXI ... ; CTO MRT</c> auto-fire the
    /// second block when the aircraft reaches the hold-short —
    /// <see cref="UpdateCommandQueue"/> early-returns while a phase is active,
    /// and <see cref="ApplyReadyConditionalBlocks"/> stops at the first
    /// untriggered block, so without this hook the queued block would sit
    /// untouched until the next user dispatch.
    ///
    /// We gate on unsatisfied requirements so transient phases (InitialClimb,
    /// pattern legs) don't prematurely fire a queued block intended for after
    /// the phase chain settles into its natural endpoint. Example: in
    /// <c>CTO MR270 014; DCT OAK30NUM</c>, InitialClimbPhase accepts DCT (as
    /// ClearsPhase), but the user means "DCT after the MR270 pattern" — not
    /// "DCT now." InitialClimbPhase has no clearance requirement, so it skips.
    /// HoldingShortPhase has a RunwayCrossing requirement, so TAXI;CTO does fire.
    ///
    /// Idempotent: applied blocks are skipped, so double-calls are safe.
    /// </summary>
    public static void NotifyPhaseAdvanced(AircraftState aircraft)
    {
        CommandQueue queue = aircraft.Queue;
        if (queue.IsComplete)
        {
            return;
        }

        if (aircraft.Phases?.CurrentPhase is not { } currentPhase)
        {
            return;
        }

        // Fire when the aircraft reaches a phase that waits for input (an unsatisfied clearance
        // requirement). Additionally, fire a queued runway-crossing clearance as soon as the
        // aircraft is taxiing again (see the CROSS guard in the loop below): a CROSS block
        // pre-clears an upcoming crossing and is idempotent, so applying it while moving lets
        // "RES; CROSS 28L" clear 28L before arrival instead of installing a transient
        // HoldingShortPhase — and its spurious "holding short" warning — at the runway crossed.
        bool isWaitPhase = false;
        foreach (ClearanceRequirement req in currentPhase.Requirements)
        {
            if (!req.IsSatisfied)
            {
                isWaitPhase = true;
                break;
            }
        }
        bool movingGroundPhase = currentPhase is Phases.Ground.TaxiingPhase or Phases.Ground.CrossingRunwayPhase;
        if (!isWaitPhase && !movingGroundPhase)
        {
            return;
        }

        // Locate the head unapplied block (matches ApplyReadyConditionalBlocks's
        // start-index logic: skip the current applied block, then look forward).
        int startIndex = queue.CurrentBlock is { IsApplied: true } ? queue.CurrentBlockIndex + 1 : queue.CurrentBlockIndex;
        for (int i = startIndex; i < queue.Blocks.Count; i++)
        {
            CommandBlock block = queue.Blocks[i];
            if (block.IsApplied)
            {
                continue;
            }

            // Triggered blocks are handled by their own trigger pathway.
            if (block.Trigger is not null)
            {
                return;
            }

            // Untriggered head block: ask the new current phase whether it accepts
            // every command in the block. Any Rejected leaves the block queued.
            if (block.ParsedCommands is not { Count: > 0 } parsed)
            {
                return;
            }

            // On a moving (non-wait) ground phase, only eager-apply runway-crossing clearances.
            // Other queued blocks (e.g. HOLD) keep waiting for the next hold-short phase so the
            // aircraft doesn't stop prematurely.
            if (!isWaitPhase && !parsed.Exists(c => c is CrossRunwayCommand))
            {
                return;
            }

            foreach (ParsedCommand cmd in parsed)
            {
                CanonicalCommandType canonical = CommandDescriber.ToCanonicalType(cmd);
                CommandAcceptance acceptance = currentPhase.CanAcceptCommand(canonical);
                if (acceptance.IsRejected)
                {
                    return;
                }
            }

            Log.LogDebug(
                "[PhaseAdvanced] {Callsign}: phase {Phase} accepts queued block {Idx} ({Desc}); firing",
                aircraft.Callsign,
                currentPhase.GetType().Name,
                i,
                block.Description
            );

            // A failed apply discards the chain remainder inside ApplyBlock, and a block deferred for its departure's
            // release stays queued for the idle scan to retry; this path fires at most one block and returns either
            // way, so no further scan needs guarding here.
            _ = ApplyBlock(aircraft, block);
            return;
        }
    }

    // --- Math helpers ---

    private static double ResolveDirection(double diff, TurnDirection? preferred)
    {
        if (preferred == TurnDirection.Left)
        {
            return -1.0;
        }

        if (preferred == TurnDirection.Right)
        {
            return 1.0;
        }

        return diff > 0 ? 1.0 : -1.0;
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0)
        {
            angle -= 360.0;
        }

        if (angle < -180.0)
        {
            angle += 360.0;
        }

        return angle;
    }

    /// <summary>Normalize a raw angle to [0,360). For headings, prefer constructing TrueHeading/MagneticHeading directly.</summary>
    private static double NormalizeBearing(double bearing)
    {
        bearing = ((bearing % 360.0) + 360.0) % 360.0;
        return bearing;
    }

    /// <summary>Display-format a raw bearing angle as 001..360. For headings, use TrueHeading/MagneticHeading.ToDisplayInt().</summary>
    internal static int BearingToDisplayInt(double bearing)
    {
        double normalized = ((bearing % 360.0) + 360.0) % 360.0;
        return normalized < 0.5 ? 360 : (int)Math.Round(normalized);
    }
}
