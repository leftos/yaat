using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;

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

    /// <summary>
    /// CVA weather minimums (7110.65 §7-4-3.b; AIM §5-5-11.b.1): do not clear a visual
    /// approach unless the reported ceiling is at/above 1000 ft and visibility 3 SM or
    /// greater. Enforced only when weather IS reported — §7-4-3.c allows the clearance
    /// with weather not available. CVAF bypasses (instructor override).
    /// </summary>
    private const int BasicVfrCeilingFt = 1000;

    private const double BasicVfrVisibilitySm = 3.0;

    /// <summary>
    /// Below-bases margin required between the reported ceiling and the 2000 ft AGL IFR
    /// visual downwind before the wide-angle pattern-entry geometry is allowed. Without
    /// it the downwind is built inside (or scraping) the deck and the lost-reference
    /// consequence immediately breaks off the approach it was just cleared for.
    /// </summary>
    private const double PatternEntryCeilingMarginFt = 500.0;

    /// <summary>
    /// How closely the heading target must match the assigned heading for a vector to still count as
    /// in force at a <c>PTAC PH</c>; beyond it a hold, procedure or pattern owns the heading.
    /// </summary>
    private const double PtacAssignedHeadingToleranceDeg = 1.0;

    /// <summary>How far the altitude target must be from the present altitude for <c>PTAC PA</c> to round toward it.</summary>
    private const double PtacTargetDirectionMinFt = 50.0;

    /// <summary>The vertical speed that counts as climbing or descending for <c>PTAC PA</c>'s rounding when no target decides it.</summary>
    private const double PtacVerticalSpeedDirectionMinFpm = 100.0;

    /// <summary>
    /// How close the PTAC altitude must be to the present altitude for the readback to say "maintain" rather than climb or
    /// descend.
    /// </summary>
    private const double PtacLevelToleranceFt = 10.0;

    public static CommandResult TryClearedApproach(ClearedApproachCommand cmd, AircraftState aircraft)
    {
        ResolvedApproach resolved = ResolveApproach(cmd.ApproachId, cmd.AirportCode, aircraft);
        CommandResult cleared = WithApproachReadback(
            TryClearedApproachCore(cmd, aircraft, resolved),
            CommandDescriber.ToCanonicalType(cmd),
            resolved,
            approach => ClearedApproachReadback(cmd, approach)
        );
        return AttachProcedureSourceAdvisory(cleared, resolved);
    }

    /// <summary>
    /// <paramref name="result"/> carrying the pilot's readback of the approach clearance it reports, built by
    /// <paramref name="build"/> from the resolved approach's name (<see cref="ApproachReadbackName.ForProcedure"/>); unchanged
    /// when the clearance failed or names an approach type no readback names, which leaves the readback to the verbalizer.
    /// </summary>
    private static CommandResult WithApproachReadback(
        CommandResult result,
        CanonicalCommandType readbackType,
        ResolvedApproach resolved,
        Func<PilotSpeechText, PilotSpeechText?> build
    )
    {
        if (!result.Success || (resolved.Procedure is not { } procedure) || (ApproachReadbackName.ForProcedure(procedure) is not { } approach))
        {
            return result;
        }

        if (build(approach) is not { } readback)
        {
            return result;
        }

        var handlerReadback = new HandlerReadback(readbackType, readback);
        if (
            (ApproachReadbackName.SpokenParts(procedure) is { Runway: { } runway } parts)
            && (ClauseVariants(readbackType, parts.Approach, runway) is { } variants)
        )
        {
            handlerReadback = handlerReadback with { Variants = variants.Set, Captures = variants.Captures };
        }

        return result with
        {
            PilotReadback = handlerReadback,
        };
    }

    /// <summary>
    /// The variant set a varied pilot picks the final clearance clause of an approach readback from, and its captures:
    /// <c>{app}</c> the spoken approach type and variant, <c>{rwy}</c> the spoken runway. Null for a command type with no set.
    /// A circling approach (no runway) has none, so callers never ask for it.
    /// </summary>
    /// <param name="readbackType">The canonical type the readback is for; a forced twin shares its plain twin's set.</param>
    /// <param name="approach">The spoken approach type and variant ("ILS", "RNAV Zulu").</param>
    /// <param name="runway">The spoken runway ("two eight right").</param>
    /// <returns>The set and its captures, or null.</returns>
    public static (BuilderVariantSet Set, IReadOnlyDictionary<string, string> Captures)? ClauseVariants(
        CanonicalCommandType readbackType,
        string approach,
        string runway
    )
    {
        BuilderVariantSet? set = readbackType switch
        {
            CanonicalCommandType.ClearedApproach or CanonicalCommandType.ClearedApproachForce => PhraseologyRules.ApproachClearance,
            CanonicalCommandType.ClearedApproachStraightIn => PhraseologyRules.StraightInApproachClearance,
            CanonicalCommandType.JoinApproach or CanonicalCommandType.JoinApproachForce => PhraseologyRules.ApproachJoin,
            CanonicalCommandType.JoinApproachStraightIn => PhraseologyRules.StraightInApproachJoin,
            CanonicalCommandType.ExpectApproach => PhraseologyRules.ExpectedApproach,
            _ => null,
        };
        return set is null ? null : (set, ApproachCaptures(approach, runway));
    }

    /// <summary>The captures of an approach readback's variant rows: <c>{app}</c> and <c>{rwy}</c>, spoken.</summary>
    private static Dictionary<string, string> ApproachCaptures(string approach, string runway) =>
        new(StringComparer.Ordinal) { ["app"] = approach, ["rwy"] = runway };

    /// <summary>The variant set of a CAPP whose id names a runway and no approach type ("cleared approach"), which has no captures.</summary>
    public static (BuilderVariantSet Set, IReadOnlyDictionary<string, string> Captures) NamingNoTypeVariants { get; } =
        (PhraseologyRules.ApproachClearanceNamingNoType, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// The readback of a <c>CAPP</c> (7110.65 4-8-1, AIM 4-4-7.b): the route and crossing restriction it carries first, then the
    /// clearance, "cleared direct SUNOL, cross SUNOL at or above three thousand four hundred, cleared ILS runway two eight right
    /// approach", or "at SUNOL, cleared ..."; a crossing restriction at the AT fix names that fix itself, so it drops the "at"
    /// lead (7110.65 4-8-1.a.4). "cleared approach" when <paramref name="approach"/> is null (nothing names it).
    /// </summary>
    /// <param name="cmd">The approach clearance.</param>
    /// <param name="approach">The approach's readback name, or null when nothing names it.</param>
    /// <returns>The terminal and spoken readback.</returns>
    public static PilotSpeechText ClearedApproachReadback(ClearedApproachCommand cmd, PilotSpeechText? approach)
    {
        PilotSpeechText? crossing = CrossingClause(cmd);
        var clauses = new List<PilotSpeechText>();
        if (cmd.DctFix is { } dctFix)
        {
            clauses.Add(FixClause("cleared direct", dctFix));
        }
        else if ((cmd.AtFix is { } atFix) && (crossing is null))
        {
            clauses.Add(FixClause("at", atFix));
        }

        if (crossing is not null)
        {
            clauses.Add(crossing);
        }

        clauses.Add(ClearanceClause(approach));
        return JoinClauses(clauses);
    }

    /// <summary>"{lead} SUNOL" / "{lead} sunol": a fix the clearance routes by, in the terminal and spoken forms.</summary>
    private static PilotSpeechText FixClause(string lead, string fix) =>
        new($"{lead} {PhraseologyVerbalizer.FixDisplayText(fix)}", $"{lead} {PhraseologyVerbalizer.SpellFix(fix)}");

    /// <summary>
    /// The crossing restriction a <c>CAPP</c> carries at its DCT (else AT) fix, in the cross-fix readback's canonical words
    /// ("cross SUNOL at or above 3400"); null when it carries none.
    /// </summary>
    private static PilotSpeechText? CrossingClause(ClearedApproachCommand cmd)
    {
        if ((cmd.CrossFixAltitude is not { } altitude) || (altitude <= 0) || (CrossingFix(cmd) is not { } fix))
        {
            return null;
        }

        CrossFixAltitudeType altType = cmd.CrossFixAltType ?? CrossFixAltitudeType.At;
        return PhraseologyVerbalizer.CrossFixReadback(new CrossFixCommand(fix.Fix, fix.Lat, fix.Lon, altitude, altType, Speed: null));
    }

    /// <summary>The fix a <c>CAPP</c>'s crossing restriction is at, with its position: the DCT fix, else the AT fix; null with neither.</summary>
    private static (string Fix, double Lat, double Lon)? CrossingFix(ClearedApproachCommand cmd)
    {
        if (cmd.DctFix is { } dctFix)
        {
            return (dctFix, cmd.DctFixLat ?? 0, cmd.DctFixLon ?? 0);
        }

        return cmd.AtFix is { } atFix ? (atFix, cmd.AtFixLat ?? 0, cmd.AtFixLon ?? 0) : null;
    }

    /// <summary>
    /// "cleared ILS runway 28R approach" / "cleared ILS runway two eight right approach"; "cleared approach" when nothing
    /// names it.
    /// </summary>
    private static PilotSpeechText ClearanceClause(PilotSpeechText? approach) =>
        approach is null
            ? new PilotSpeechText("cleared approach", "cleared approach")
            : new PilotSpeechText($"cleared {approach.Terminal} approach", $"cleared {approach.Tts} approach");

    private static PilotSpeechText JoinClauses(IReadOnlyCollection<PilotSpeechText> clauses) =>
        new(string.Join(", ", clauses.Select(clause => clause.Terminal)), string.Join(", ", clauses.Select(clause => clause.Tts)));

    /// <summary>
    /// The readback of a straight-in clearance or a join (7110.65 4-8-1, 5-9-4): "cleared straight in ILS runway two eight
    /// right approach", "join the ILS runway two eight right approach", "join the straight in ..."; the terminal writes
    /// "straight-in". Null for any other command type.
    /// </summary>
    /// <param name="readbackType">The command's canonical type (a forced join reads back as the join).</param>
    /// <param name="approach">The approach's readback name.</param>
    /// <returns>The terminal and spoken readback, or null.</returns>
    public static PilotSpeechText? JoinApproachReadback(CanonicalCommandType readbackType, PilotSpeechText approach)
    {
        (string Terminal, string Spoken)? lead = readbackType switch
        {
            CanonicalCommandType.ClearedApproachStraightIn => ("cleared straight-in", "cleared straight in"),
            CanonicalCommandType.JoinApproach or CanonicalCommandType.JoinApproachForce => ("join the", "join the"),
            CanonicalCommandType.JoinApproachStraightIn => ("join the straight-in", "join the straight in"),
            _ => null,
        };
        return lead is { } words
            ? new PilotSpeechText($"{words.Terminal} {approach.Terminal} approach", $"{words.Spoken} {approach.Tts} approach")
            : null;
    }

    /// <summary>
    /// The readback of a PTAC (7110.65 5-6-2.a.1, 5-9-4.b, 5-9-4.c.2): "turn left heading two four zero" when the heading was given
    /// with a turn direction, else "fly heading two four zero" ("fly present heading" when <paramref name="heading"/> is
    /// null), then the altitude when one is read back, then the clearance ("cleared approach" when
    /// <paramref name="approach"/> is null); a varied pilot picks among it and the rows of the set <see cref="PtacVariants"/>
    /// pairs it with (the six <c>PhraseologyRules.Ptac*</c> sets).
    /// </summary>
    /// <param name="heading">The heading to fly, or null for the present heading.</param>
    /// <param name="direction">The turn direction the heading was given with, or null when none was.</param>
    /// <param name="altitude">The altitude to maintain and how it is said, or null when none is read back.</param>
    /// <param name="approach">The approach's readback name, or null when nothing names it.</param>
    /// <returns>The terminal and spoken readback.</returns>
    public static PilotSpeechText PtacReadback(MagneticHeading? heading, TurnDirection? direction, PtacAltitude? altitude, PilotSpeechText? approach)
    {
        var clauses = new List<PilotSpeechText> { PtacHeadingClause(heading, direction) };
        if (altitude is { } maintain)
        {
            string verb = maintain.Verb();
            clauses.Add(
                new PilotSpeechText(
                    $"{verb} {PhraseologyVerbalizer.CompactAltitude(maintain.Altitude)}",
                    $"{verb} {PhraseologyVerbalizer.AltitudeWords(maintain.Altitude)}"
                )
            );
        }

        clauses.Add(ClearanceClause(approach));
        return JoinClauses(clauses);
    }

    /// <summary>
    /// The variant set a varied pilot picks a PTAC readback from, by its heading kind (a turn direction or none) and its
    /// vertical verb, and its captures: <c>{dir}</c> "left"/"right", <c>{hdg}</c> and <c>{alt}</c> spoken, <c>{app}</c> and
    /// <c>{rwy}</c> the spoken approach and runway. Null, so the readback stays canonical in every band, for the present heading,
    /// no altitude, an approach no readback names, or one with no runway.
    /// </summary>
    /// <param name="direction">The turn direction the heading was given with, or null when none was.</param>
    /// <param name="heading">The heading to fly, or null for the present heading.</param>
    /// <param name="altitude">The altitude read back, or null when none is.</param>
    /// <param name="parts">The spoken approach words and runway, or null when nothing names them.</param>
    /// <returns>The set and its captures, or null.</returns>
    public static (BuilderVariantSet Set, IReadOnlyDictionary<string, string> Captures)? PtacVariants(
        TurnDirection? direction,
        MagneticHeading? heading,
        PtacAltitude? altitude,
        (string Approach, string? Runway)? parts
    )
    {
        if ((heading is not { } flyHeading) || (altitude is not { } maintain) || (parts is not { Runway: { } runway } named))
        {
            return null;
        }

        PtacVertical vertical = maintain.Vertical();
        BuilderVariantSet set = (direction is not null, vertical) switch
        {
            (true, PtacVertical.Climb) => PhraseologyRules.PtacTurnClimb,
            (true, PtacVertical.Descend) => PhraseologyRules.PtacTurnDescend,
            (true, PtacVertical.Maintain) => PhraseologyRules.PtacTurnMaintain,
            (false, PtacVertical.Climb) => PhraseologyRules.PtacFlyHeadingClimb,
            (false, PtacVertical.Descend) => PhraseologyRules.PtacFlyHeadingDescend,
            (false, PtacVertical.Maintain) => PhraseologyRules.PtacFlyHeadingMaintain,
            _ => throw new UnreachableException($"No PTAC variant set for {vertical}"),
        };
        Dictionary<string, string> captures = ApproachCaptures(named.Approach, runway);
        captures["hdg"] = PhraseologyVerbalizer.HeadingDigits(flyHeading);
        captures["alt"] = PhraseologyVerbalizer.AltitudeWords(maintain.Altitude);
        if (direction is { } turn)
        {
            captures["dir"] = TurnWord(turn);
        }

        return (set, captures);
    }

    private static PilotSpeechText PtacHeadingClause(MagneticHeading? heading, TurnDirection? direction)
    {
        if (heading is not { } flyHeading)
        {
            return new PilotSpeechText("fly present heading", "fly present heading");
        }

        string lead = direction is { } turn ? $"turn {TurnWord(turn)}" : "fly";
        return new PilotSpeechText(
            $"{lead} heading {PhraseologyVerbalizer.HeadingNumber(flyHeading)}",
            $"{lead} heading {PhraseologyVerbalizer.HeadingDigits(flyHeading)}"
        );
    }

    /// <summary>
    /// The altitude a PTAC readback says and what it is said against: climb or descend from <paramref name="PresentAltitude"/>
    /// (within <see cref="PtacLevelToleranceFt"/>, "maintain"); a null <paramref name="PresentAltitude"/> is an altitude the
    /// aircraft keeps (<c>PA</c>), always "maintain" whatever its rounding.
    /// </summary>
    /// <param name="Altitude">The altitude to maintain.</param>
    /// <param name="PresentAltitude">The aircraft's present altitude, or null when the altitude is the one it keeps.</param>
    public readonly record struct PtacAltitude(int Altitude, double? PresentAltitude)
    {
        /// <summary>Whether the aircraft climbs to, descends to or keeps <see cref="Altitude"/>.</summary>
        public PtacVertical Vertical() =>
            (Altitude - PresentAltitude) switch
            {
                > PtacLevelToleranceFt => PtacVertical.Climb,
                < -PtacLevelToleranceFt => PtacVertical.Descend,
                _ => PtacVertical.Maintain,
            };

        /// <summary>"climb and maintain", "descend and maintain" or "maintain".</summary>
        public string Verb() =>
            Vertical() switch
            {
                PtacVertical.Climb => "climb and maintain",
                PtacVertical.Descend => "descend and maintain",
                PtacVertical.Maintain => "maintain",
                _ => throw new UnreachableException($"No PTAC verb for {Vertical()}"),
            };
    }

    /// <summary>The vertical part of a PTAC: a climb, a descent, or the altitude the aircraft is at.</summary>
    public enum PtacVertical
    {
        Climb,
        Descend,
        Maintain,
    }

    /// <summary>Instructor advisory when the cleared/joined approach's coded data did not come from the
    /// current FAA CIFP — recovered from a cached prior cycle, or supplied by an ARTCC CIFP fragment.</summary>
    private static CommandResult AttachProcedureSourceAdvisory(CommandResult result, ResolvedApproach resolved)
    {
        return result.Success && resolved.Source is { } source && resolved.Procedure is { } proc
            ? result with
            {
                Advisory = CommandDispatcher.ProcedureSourceAdvisory("approach", proc.ApproachId, source),
            }
            : result;
    }

    private static CommandResult TryClearedApproachCore(ClearedApproachCommand cmd, AircraftState aircraft, ResolvedApproach resolved)
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

        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        (CifpApproachProcedure? procedure, RunwayInfo? approachRunway, string? airport) = resolved;

        // Cancel existing speed restrictions per 7110.65 §5-7-1.d
        CancelSpeedsForApproachClearance(aircraft);
        RestateStandingInTrailReduction(aircraft);

        FinalApproachCourseResult facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Build approach fix sequence, selecting best transition if available
        CifpTransition? transition = SelectBestTransition(procedure, aircraft);
        List<ApproachFix> approachFixes = transition is not null
            ? BuildApproachFixesWithTransition(transition, procedure)
            : BuildApproachFixes(procedure);

        // Check conditions for deferred vs immediate approach activation
        bool hasDctFix = cmd.DctFix is not null;

        // The vector the aircraft is on is the angle an implied-PTAC intercept is judged against, so
        // it is captured here — with the flag derived from the same read — and not after
        // ClearExistingPhases, which can drop the assigned heading before the clearance does.
        MagneticHeading? interceptHeading = aircraft.Targets.AssignedMagneticHeading;
        bool isOnAssignedHeading = interceptHeading is not null;

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
            List<ApproachFix> trimmedFixes = TrimToNavRouteConnection(approachFixes, aircraft);
            string connectingFix = trimmedFixes.Count > 0 ? trimmedFixes[0].Name : "";

            // AT fix must match the connecting fix (or no AT fix at all) for deferred path
            bool atFixMatchesConnection = cmd.AtFix is null || connectingFix.Equals(cmd.AtFix, StringComparison.OrdinalIgnoreCase);

            if (trimmedFixes.Count > 0 && atFixMatchesConnection && NavRouteContainsFix(aircraft, connectingFix))
            {
                ApproachClearance clearance = BuildClearance(procedure, transition, airport, facResult, approachRunway, cmd.Force);
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

        ApproachClearance immClearance = BuildClearance(procedure, transition, airport, facResult, approachRunway, cmd.Force);

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

            // The intercept's threshold is the landing threshold: its distance is scored against the
            // approach gate, which the P/CG measures from there.
            LatLon interceptThreshold = LandingThreshold.Resolve(approachRunway, aircraft.Ground.Layout);

            if (cmd.CrossFixAltitude is { } interceptCxAlt && interceptCxAlt > 0)
            {
                aircraft.Targets.TargetAltitude = interceptCxAlt;
                aircraft.Targets.AssignedAltitude = interceptCxAlt;
            }

            aircraft.Phases.Add(
                new InterceptCoursePhase
                {
                    FinalApproachCourse = finalCourse,
                    ThresholdLat = interceptThreshold.Lat,
                    ThresholdLon = interceptThreshold.Lon,
                    ApproachId = immClearance.ApproachId,
                    AssignedInterceptHeading = interceptHeading,
                    ForcedIntercept = cmd.Force,
                }
            );
            aircraft.Phases.Add(new FinalApproachPhase());
            bool isHeliIntercept = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
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

        // Procedure turn: insert PT phase BEFORE approach navigation. The PT flies the aircraft to its fix and
        // back inbound, so approach navigation then flies the common route (each fix with its own step-down and
        // role), from the first fix still ahead of the aircraft on the inbound course.
        ProcedureTurnPhase? cappProcedureTurn = null;
        if (needsProcedureTurn && procedure.ProcedureTurnLeg is { } cappPiLeg)
        {
            cappProcedureTurn = BuildProcedureTurnPhase(cappPiLeg, procedure, approachRunway.AirportId, finalCourse);
            if (cappProcedureTurn is not null)
            {
                approachFixes = BuildApproachFixes(procedure);
                aircraft.Phases.Add(cappProcedureTurn);
            }
        }

        // Build phase sequence
        if (approachFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = approachFixes, PostTurnJoin = cappProcedureTurn?.InboundJoin });
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
            string? holdNavaid = HoldInLieuNavaid(procedure, cappHoldLeg);
            HoldingPatternPhase? holdPhase = BuildHoldInLieuPhase(cappHoldLeg, holdNavaid, approachFixes, approachRunway.AirportId, finalCourse);
            if (holdPhase is not null)
            {
                aircraft.Phases.Add(holdPhase);
            }
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        bool isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string cappPrefix = cmd.Force ? "Force: cleared" : "Cleared";
        return new CommandResult(
            true,
            $"{cappPrefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    /// <summary>
    /// <c>JAPP</c>/<c>JAPPF</c>, <c>CAPPSI</c> or <c>JAPPSI</c>, read back as that command's canonical type
    /// (<see cref="CommandDescriber.ToCanonicalType"/>).
    /// </summary>
    public static CommandResult TryJoinApproach(ParsedCommand cmd, AircraftState aircraft)
    {
        (string approachId, string? airportCode, bool force, bool straightIn) = cmd switch
        {
            JoinApproachCommand japp => (japp.ApproachId, japp.AirportCode, japp.Force, false),
            ClearedApproachStraightInCommand cappsi => (cappsi.ApproachId, cappsi.AirportCode, false, true),
            JoinApproachStraightInCommand jappsi => (jappsi.ApproachId, jappsi.AirportCode, false, true),
            _ => throw new ArgumentException($"Not a join or straight-in approach clearance: {cmd}", nameof(cmd)),
        };
        CanonicalCommandType readbackType = CommandDescriber.ToCanonicalType(cmd);
        ResolvedApproach resolved = ResolveApproach(approachId, airportCode, aircraft);
        CommandResult joined = WithApproachReadback(
            TryJoinApproachCore(force, straightIn, aircraft, resolved),
            readbackType,
            resolved,
            approach => JoinApproachReadback(readbackType, approach)
        );
        return AttachProcedureSourceAdvisory(joined, resolved);
    }

    private static CommandResult TryJoinApproachCore(bool force, bool straightIn, AircraftState aircraft, ResolvedApproach resolved)
    {
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        (CifpApproachProcedure? procedure, RunwayInfo? approachRunway, string? airport) = resolved;

        // Cancel existing speed restrictions per 7110.65 §5-7-1.d
        CancelSpeedsForApproachClearance(aircraft);
        RestateStandingInTrailReduction(aircraft);

        FinalApproachCourseResult facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Build approach fix sequence, selecting best transition if available
        CifpTransition? transition = SelectBestTransition(procedure, aircraft);
        List<ApproachFix> approachFixes = transition is not null
            ? BuildApproachFixesWithTransition(transition, procedure)
            : BuildApproachFixes(procedure);

        // For JAPP: find nearest IAF/IF ahead of aircraft
        List<ApproachFix> trimmedFixes = TrimToNearestEntry(approachFixes, aircraft);

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
                $"Unable straight-in {procedure.ApproachId}: intercept angle {japInterceptAngle:F0}° exceeds 90° and a course reversal is published. "
                    + "Request vectors to final or clear for full approach."
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
            TransitionName = transition?.Name,
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

        // Insert procedure turn (PI) if needed — approach navigation then flies the common route from the first
        // fix still ahead on the inbound course (as for CAPP above).
        ProcedureTurnPhase? jappProcedureTurn = null;
        if (jappNeedsProcedureTurn && procedure.ProcedureTurnLeg is { } japPiLeg)
        {
            jappProcedureTurn = BuildProcedureTurnPhase(japPiLeg, procedure, approachRunway.AirportId, finalCourse);
            if (jappProcedureTurn is not null)
            {
                trimmedFixes = BuildApproachFixes(procedure);
                aircraft.Phases.Add(jappProcedureTurn);
            }
        }

        // Insert hold-in-lieu if needed (skipped when a PT is already engaged)
        if (jappProcedureTurn is null && needsHold && procedure.HoldInLieuLeg is { } holdLeg)
        {
            string? holdNavaid = HoldInLieuNavaid(procedure, holdLeg);
            HoldingPatternPhase? holdPhase = BuildHoldInLieuPhase(holdLeg, holdNavaid, trimmedFixes, approachRunway.AirportId, finalCourse);
            if (holdPhase is not null)
            {
                aircraft.Phases.Add(holdPhase);
            }
        }

        if (trimmedFixes.Count > 0)
        {
            aircraft.Phases.Add(new ApproachNavigationPhase { Fixes = trimmedFixes, PostTurnJoin = jappProcedureTurn?.InboundJoin });
        }

        aircraft.Phases.Add(new FinalApproachPhase());
        bool isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string baseVerb = straightIn ? "Cleared straight-in" : "Join";
        string prefix = force ? $"Force: {baseVerb.ToLowerInvariant()}" : baseVerb;
        return new CommandResult(
            true,
            $"{prefix} {procedure.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    /// <summary>
    /// The pilot's readback of a PTAC the handler clears, with the variant set and captures of its shape when it has one; null
    /// when the resolved approach is of a type no readback names, which leaves the readback to the verbalizer.
    /// </summary>
    private static HandlerReadback? PtacHandlerReadback(
        PositionTurnAltitudeClearanceCommand cmd,
        CifpApproachProcedure procedure,
        PtacHeading resolvedHeading,
        PtacAltitude readbackAltitude
    )
    {
        if (ApproachReadbackName.ForProcedure(procedure) is not { } approach)
        {
            return null;
        }

        MagneticHeading? heading = resolvedHeading.FromPh ? null : new MagneticHeading(resolvedHeading.Heading);
        var readback = new HandlerReadback(
            CommandDescriber.ToCanonicalType(cmd),
            PtacReadback(heading, resolvedHeading.Direction, readbackAltitude, approach)
        );
        return PtacVariants(resolvedHeading.Direction, heading, readbackAltitude, ApproachReadbackName.SpokenParts(procedure)) is { } variants
            ? readback with
            {
                Variants = variants.Set,
                Captures = variants.Captures,
            }
            : readback;
    }

    public static CommandResult TryPtac(PositionTurnAltitudeClearanceCommand cmd, AircraftState aircraft)
    {
        ResolvedApproach resolved = ResolveApproach(cmd.ApproachId, null, aircraft);
        if (!resolved.Success)
        {
            return new CommandResult(false, resolved.Error);
        }

        (CifpApproachProcedure? procedure, RunwayInfo? approachRunway, string? airport) = resolved;

        FinalApproachCourseResult facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, NavigationDatabase.Instance);
        TrueHeading finalCourse = facResult.Course;

        // Resolve heading: explicit, the assigned heading PH keeps in force, or present (7110.65 §5-9-4)
        PtacHeading resolvedHeading = ResolvePtacHeading(cmd, aircraft);
        int heading = resolvedHeading.Heading;
        // Resolve altitude before anything clears the via state it reads
        int altitude = ResolvePtacAltitude(cmd, aircraft);
        // Read back against the altitude the aircraft is at before the clearance changes its targets; a PA altitude is the
        // one it keeps, so it is always "maintain"
        HandlerReadback? readback = PtacHandlerReadback(
            cmd,
            procedure,
            resolvedHeading,
            new PtacAltitude(altitude, cmd.Altitude is null ? null : aircraft.Altitude)
        );

        ApplyPtacTargets(aircraft, resolvedHeading, altitude);

        // Clear existing phases
        ClearExistingPhases(aircraft);

        var clearance = new ApproachClearance
        {
            TransitionName = null,
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

        // The intercept's threshold is the landing threshold: its distance is scored against the
        // approach gate, which the P/CG measures from there.
        LatLon interceptThreshold = LandingThreshold.Resolve(approachRunway, aircraft.Ground.Layout);

        aircraft.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = finalCourse,
                ThresholdLat = interceptThreshold.Lat,
                ThresholdLon = interceptThreshold.Lon,
                ApproachId = clearance.ApproachId,
                AssignedInterceptHeading = new MagneticHeading(heading),
                ForcedIntercept = cmd.Forced,
            }
        );
        aircraft.Phases.Add(new FinalApproachPhase());
        bool isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeliApch ? new HelicopterLandingPhase() : new LandingPhase());

        StartPhases(aircraft);

        string ptacVerb = (cmd.Forced, resolvedHeading.FromPh) switch
        {
            (true, true) => "Force: fly",
            (true, false) => "Force: turn",
            (false, true) => "Fly",
            (false, false) => "Turn",
        };
        string ptacPrefix = resolvedHeading.Direction is { } direction ? $"{ptacVerb} {TurnWord(direction)}" : ptacVerb;
        string ptacRunway = RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "");
        return new CommandResult(
            true,
            $"{ptacPrefix} heading {heading:000}, maintain {altitude}, cleared {procedure.ApproachId} approach, runway {ptacRunway}"
        )
        {
            PilotReadback = readback,
        };
    }

    /// <summary>
    /// A PTAC's heading, altitude and speed targets: the speed restriction cancelled (7110.65 §5-7-1.d), the heading set at
    /// once and turned the way the controller said when a direction was given, and the altitude made the new assigned
    /// altitude, cancelling the earlier vertical clearance as CM/DM do.
    /// </summary>
    private static void ApplyPtacTargets(AircraftState aircraft, PtacHeading resolvedHeading, int altitude)
    {
        CancelSpeedsForApproachClearance(aircraft);
        RestateStandingInTrailReduction(aircraft);

        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = new MagneticHeading(resolvedHeading.Heading).ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(resolvedHeading.Heading);
        if (resolvedHeading.Direction is { } direction)
        {
            aircraft.Targets.PreferredTurnDirection = direction;
        }
        else if (!resolvedHeading.KeepTurnDirection)
        {
            aircraft.Targets.PreferredTurnDirection = null;
        }

        aircraft.Targets.TargetAltitude = altitude;
        aircraft.Targets.AssignedAltitude = altitude;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;
        FlightCommandHandler.CancelViaClearances(aircraft);
        aircraft.Procedure.IsExpediting = false;
    }

    /// <summary>
    /// The heading a PTAC intercepts on (7110.65 §5-9-4). An explicit heading argument is used as
    /// given. <c>PH</c> means "no new vector": while an assigned heading is in force and the aircraft
    /// is still steering to it — on no navigation route, and its heading target is that vector rather
    /// than a hold, procedure or pattern leg — the assigned heading stays in force, still turning to
    /// it or already on it, and its turn direction stands so a long-way turn is not reversed.
    /// Otherwise (direct, route, procedure, hold, or no assigned heading at all) PH takes the present
    /// heading.
    /// </summary>
    private static PtacHeading ResolvePtacHeading(PositionTurnAltitudeClearanceCommand cmd, AircraftState aircraft)
    {
        if (cmd.MagneticHeading is { } explicitHeading)
        {
            return new PtacHeading((int)Math.Round(explicitHeading.Degrees), cmd.Direction, FromPh: false, KeepTurnDirection: false);
        }

        if (SteeringAssignedHeading(aircraft) is { } steeringHeading)
        {
            return new PtacHeading((int)Math.Round(steeringHeading.Degrees), Direction: null, FromPh: true, KeepTurnDirection: true);
        }

        return new PtacHeading((int)Math.Round(aircraft.MagneticHeading.Degrees), Direction: null, FromPh: true, KeepTurnDirection: false);
    }

    /// <summary>
    /// The altitude a PTAC clears the aircraft to maintain. An explicit altitude argument is used as
    /// given. <c>PA</c> keeps the altitude still assigned (7110.65 §4-5-7.h.11 NOTE 1, §5-6-2.d NOTE):
    /// the last assigned altitude, unless a descend via or climb via was issued after it or it is the
    /// bound of a VFR at-or-above/at-or-below restriction. Otherwise
    /// PA is the present altitude rounded to 100 ft toward the direction of travel, so the aircraft
    /// never turns back through an altitude it has left: down while descending, up while climbing
    /// (<see cref="PtacVerticalDirection"/>), to the nearest 100 ft when level. A procedure target is never the assigned
    /// altitude.
    /// </summary>
    private static int ResolvePtacAltitude(PositionTurnAltitudeClearanceCommand cmd, AircraftState aircraft)
    {
        if (cmd.Altitude is { } explicitAltitude)
        {
            return explicitAltitude;
        }

        bool viaInForce = aircraft.Procedure.IsViaClearanceInForce;
        // A VFR "at or above" / "at or below" restriction records its bound as AssignedAltitude; a bound is not an altitude to maintain
        bool vfrBoundInForce = (aircraft.Targets.AltitudeFloor is not null) || (aircraft.Targets.AltitudeCeiling is not null);
        if (!viaInForce && !vfrBoundInForce && (aircraft.Targets.AssignedAltitude is { } assigned))
        {
            return (int)Math.Round(assigned);
        }

        double hundreds = aircraft.Altitude / 100.0;
        double rounded = PtacVerticalDirection(aircraft) switch
        {
            < 0 => Math.Floor(hundreds),
            > 0 => Math.Ceiling(hundreds),
            _ => Math.Round(hundreds, MidpointRounding.AwayFromZero),
        };
        return (int)(rounded * 100);
    }

    /// <summary>
    /// Which way the aircraft is travelling vertically for <see cref="ResolvePtacAltitude"/>: toward its
    /// altitude target when one is set at least <see cref="PtacTargetDirectionMinFt"/> away, else by its
    /// vertical speed when that is at least <see cref="PtacVerticalSpeedDirectionMinFpm"/> either way,
    /// else level (0). Negative is descending, positive climbing.
    /// </summary>
    private static int PtacVerticalDirection(AircraftState aircraft)
    {
        double present = aircraft.Altitude;
        if (aircraft.Targets.TargetAltitude is { } target && (Math.Abs(target - present) >= PtacTargetDirectionMinFt))
        {
            return Math.Sign(target - present);
        }

        if (Math.Abs(aircraft.VerticalSpeed) >= PtacVerticalSpeedDirectionMinFpm)
        {
            return Math.Sign(aircraft.VerticalSpeed);
        }

        return 0;
    }

    /// <summary>
    /// The assigned heading while the aircraft is still steering to it, else null: a vector is in
    /// force only off any navigation route and while the heading target is that vector (within
    /// <see cref="PtacAssignedHeadingToleranceDeg"/>), since a hold, procedure or pattern leg can own
    /// the heading with the route empty and the last vector still on the books.
    /// </summary>
    private static MagneticHeading? SteeringAssignedHeading(AircraftState aircraft)
    {
        if (aircraft.Targets.AssignedMagneticHeading is not { } assignedHeading || aircraft.Targets.NavigationRoute.Count != 0)
        {
            return null;
        }

        if (aircraft.Targets.TargetTrueHeading is not { } targetHeading)
        {
            return null;
        }

        MagneticHeading targetMagnetic = targetHeading.ToMagnetic(aircraft.Declination);
        return targetMagnetic.IsCloseTo(assignedHeading, PtacAssignedHeadingToleranceDeg) ? assignedHeading : null;
    }

    /// <summary>
    /// <see cref="TryPtac"/>'s heading decision. <paramref name="FromPh"/> is true whenever the
    /// heading came from PH rather than an explicit argument, which drives the "Fly heading" wording. <paramref name="Direction"/>
    /// is the turn direction an explicit heading was given with, or null to fly it the shorter way.
    /// </summary>
    private readonly record struct PtacHeading(int Heading, TurnDirection? Direction, bool FromPh, bool KeepTurnDirection);

    /// <summary>The word a turn direction is said and written with: "left" or "right".</summary>
    private static string TurnWord(TurnDirection direction) => direction == TurnDirection.Left ? "left" : "right";

    public static CommandResult TryClearedVisualApproach(ClearedVisualApproachCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // CVA is for IFR arrivals being vectored to a visual approach (7110.65 §7-4-3).
        // VFR pattern entry uses a different command set (TPAT etc.) and a different
        // pattern geometry (low altitude, deconflicted). Reject VFR aircraft up front
        // so they don't end up on an IFR-shaped pattern.
        if (aircraft.FlightPlan.IsVfr)
        {
            return new CommandResult(false, "CVA requires an IFR flight plan");
        }

        if ((cmd.RunwayId is null) && (ExpectedVisualAtToweredField(cmd, aircraft, ctx) is { } expectedRunway))
        {
            cmd = cmd with { RunwayId = expectedRunway };
        }

        if ((cmd.RunwayId is null) && (RefuseAirportLevelVisual(cmd, aircraft, ctx) is { } airportLevelRefusal))
        {
            return airportLevelRefusal;
        }

        // Forced CVA (CVAF): the RPO folds the pilot's in-sight report into the visual approach
        // clearance so it need not be reported first. RPO-only, like RFISF/RTISF — a solo
        // student must have the pilot make the report. Force whichever report the acquisition
        // gate below requires: traffic-in-sight when following (§7-4-3.c.2), else field-in-sight.
        if (cmd.Force)
        {
            if (ctx.RefusesRpoOnly)
            {
                return CommandDispatcher.OperatorGuidanceRefusal("CVAF is RPO-only; use RFIS/RTIS in solo training");
            }
            if (cmd.FollowCallsign is not null)
            {
                aircraft.Approach.HasReportedTrafficInSight = true;
                aircraft.Approach.LastReportedTrafficCallsign = cmd.FollowCallsign.ToUpperInvariant();
            }
            else
            {
                aircraft.Approach.HasReportedFieldInSight = true;
                aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
            }
        }

        // Visual-approach acquisition gate (7110.65 §7-4-3.c; AIM §5-4-23.a): the pilot must
        // have EITHER the preceding traffic in sight (when following) OR the airport in sight
        // (otherwise). Per §7-4-3.c.2 NOTE, a following aircraft need not report the field.
        // RPO can force either report with RTISF/RFISF (or CVAF).
        if (cmd.FollowCallsign is not null)
        {
            if (!aircraft.Approach.HasReportedTrafficInSight)
            {
                return new CommandResult(false, "Traffic not in sight — issue RTIS first");
            }

            // §7-4-3.c.2: the report must be about THE PRECEDING AIRCRAFT. A still-standing
            // report about different traffic (e.g. a lead that landed, which keeps the report —
            // the pilot still sees it) does not authorize following someone else. Mirrors the
            // bare-FOLLOW identity gate; a null identity (restored state) cannot be verified
            // and is accepted, matching that gate's bare form.
            if (
                aircraft.Approach.LastReportedTrafficCallsign is { } reportedCs
                && !reportedCs.Equals(cmd.FollowCallsign, StringComparison.OrdinalIgnoreCase)
            )
            {
                return new CommandResult(false, $"Traffic in sight was reported for {reportedCs} — issue RTIS {cmd.FollowCallsign} first");
            }

            // Visual separation — and therefore CVA FOLLOW — is not authorized behind a super
            // (7110.65 §7-4-3.d NOTE, §7-2-1; AIM §5-5-11.2.5). Runs after the CVAF forced
            // block so forcing traffic-in-sight cannot bypass the prohibition.
            if (
                ctx.FindAircraft?.Invoke(cmd.FollowCallsign) is { } lead
                && WakeTurbulenceData.WakeClassForType(lead.AircraftType, AircraftCategorization.Categorize(lead.AircraftType))
                    == WakeTurbulenceData.WakeClass.Super
            )
            {
                return new CommandResult(false, $"Unable, visual separation not authorized behind super {cmd.FollowCallsign}");
            }
        }
        else if (!aircraft.Approach.HasReportedFieldInSight)
        {
            return new CommandResult(false, "Field not in sight — issue RFIS first");
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (!TryResolveApproachAirport(cmd.AirportCode, CommandDispatcher.ResolveAirport(aircraft), out string airport))
        {
            return new CommandResult(false, $"Unknown airport {cmd.AirportCode?.Trim().ToUpperInvariant()}");
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for visual approach");
        }

        if ((cmd.RunwayId ?? PilotChosenVisualRunway(aircraft, airport, ctx)?.Designator) is not { } runwayId)
        {
            return new CommandResult(false, $"Unable, no runway at {airport} long enough to land");
        }

        RunwayInfo? runway = navDb.GetRunway(airport, runwayId);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} at {airport}");
        }

        RunwayInfo approachRunway = runway.IsActiveEnd(runwayId) ? runway : runway.ForApproach(runwayId);

        // Weather gates. 7110.65 §7-4-3.b / AIM §5-5-11.b.1: no visual approach clearance
        // unless the reported weather is at/above basic-VFR minimums (1000 ft ceiling /
        // 3 SM). GetWeatherForAirport falls back to area weather interpolated from nearby
        // stations, so a field with no station of its own is judged on that (the §7-4-2
        // "reasonable assurance" proxy); only a scenario with no weather at all skips the
        // gate. The wide-angle geometry further requires the deck to clear the 2000 ft AGL
        // IFR downwind it would build, plus a below-bases margin — a sim margin stricter
        // than the regulation (a visual needs only "clear of clouds"), because otherwise
        // the pattern is authored inside the cloud and the AIM §5-5-11.a.3 lost-reference
        // consequence breaks it off immediately. CVAF bypasses both gates (instructor
        // override, consistent with its forced reports).
        if (!cmd.Force && ctx.Weather?.GetWeatherForAirport(airport) is { } metar)
        {
            int? ceilingFt = metar.CeilingFeetAgl;
            double? visSm = metar.VisibilityStatuteMiles;
            if ((ceilingFt is { } ceiling && ceiling < BasicVfrCeilingFt) || (visSm is { } vis && vis < BasicVfrVisibilitySm))
            {
                string ceilingText = ceilingFt is { } c ? $"ceiling {c} ft" : "ceiling not reported";
                string visText = visSm is { } v ? $"{v:0.#} SM" : "visibility not reported";
                return new CommandResult(false, $"Unable, {airport} weather below visual approach minimums ({ceilingText}, {visText})");
            }

            double joinAngleOff = aircraft.TrueHeading.AbsAngleTo(approachRunway.TrueHeading);
            if (
                (joinAngleOff > 90.0)
                && ceilingFt is { } patternCeiling
                && (patternCeiling < (IfrVisualDownwindAltAglFt + PatternEntryCeilingMarginFt))
            )
            {
                return new CommandResult(false, $"Unable, ceiling {patternCeiling} ft too low for a visual pattern entry — vector for a closer join");
            }
        }

        // Cancel speed restrictions per 7110.65 §5-7-1.d
        CancelSpeedsForApproachClearance(aircraft);
        RestateStandingInTrailReduction(aircraft);

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;

        // The clearance is predicated on these reports (the acquisition gate above) —
        // ClearExistingPhases wipes them as generic approach state, so restore the basis the
        // gate consumed. Without this every CVA opens with both flags false: the pilot
        // re-reports a field they already reported, and the AIM §5-5-11.a.3 lost-reference
        // consequence would read a freshly-cleared visual as "nothing in sight". The traffic
        // report only carries when this clearance actually follows someone.
        bool hadFieldReport = aircraft.Approach.HasReportedFieldInSight;
        bool hadTrafficReport = aircraft.Approach.HasReportedTrafficInSight;

        // Clear existing phases
        ClearExistingPhases(aircraft);
        aircraft.Approach.HasReportedFieldInSight = hadFieldReport;
        aircraft.Approach.HasReportedTrafficInSight = (cmd.FollowCallsign is not null) && hadTrafficReport;

        var clearance = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = ApproachIds.Visual(runwayId),
            AirportCode = airport,
            RunwayId = runwayId,
            FinalApproachCourse = approachRunway.TrueHeading,
            Procedure = null,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;
        aircraft.Approach.Expected = ApproachIds.Visual(approachRunway.Designator);

        // Determine execution path based on aircraft position
        TrueHeading finalCourse = approachRunway.TrueHeading;
        double angleOff = aircraft.TrueHeading.AbsAngleTo(finalCourse);

        if (cmd.FollowCallsign is not null)
        {
            aircraft.Approach.FollowingCallsign = cmd.FollowCallsign;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
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
            (double Lat, double Lon) interceptPoint = ComputeInterceptPoint(approachRunway, interceptDistNm);

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
            PatternDirection direction = cmd.TrafficDirection ?? DeterminePatternDirection(aircraft, approachRunway);

            double ifrPatternAltMsl = approachRunway.AirportElevationFt + IfrVisualDownwindAltAglFt;
            // The authored size/altitude are deliberately bypassed above, but the threshold displacement
            // is not an override — it is where the runway's landing surface starts, so the ground runway
            // is still supplied for it.
            GroundRunway? authoredRunway = (ctx.GroundLayout ?? aircraft.Ground.Layout)?.FindRunway(approachRunway.Designator);
            PatternWaypoints waypoints = PatternGeometry.Compute(
                approachRunway,
                category,
                aircraft.AircraftType,
                aircraft.WindSpeedKts,
                direction,
                sizeOverrideNm: null,
                ifrPatternAltMsl,
                airportRunways: null,
                authoredRunway
            );

            List<Phase> circuitPhases = PatternBuilder.BuildCircuit(
                approachRunway,
                category,
                aircraft.AircraftType,
                aircraft.WindSpeedKts,
                direction,
                PatternEntryLeg.Downwind,
                false,
                null,
                patternSizeNm: null,
                ifrPatternAltMsl,
                airportRunways: null,
                authoredRunway
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
                    (double Lat, double Lon) leadIn = GeoMath.ProjectPoint(
                        waypoints.DownwindAbeamLat,
                        waypoints.DownwindAbeamLon,
                        reverseDownwind,
                        1.5
                    );

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

            foreach (Phase phase in circuitPhases)
            {
                aircraft.Phases.Add(phase);
            }
        }

        StartPhases(aircraft);

        return cmd.RunwayId is null ? AirportLevelVisualCleared(cmd, airport, approachRunway.Designator) : RunwayVisualCleared(cmd, runwayId);
    }

    /// <summary>
    /// The runway of the visual a runway-less <c>CVA</c> to a towered field clears: the expected <c>VIS{rwy}</c>'s, which
    /// already named the runway 7110.65 7-4-3.c.4 wants in a towered clearance, so it clears that visual as bare <c>CAPP</c>
    /// does. Null at an untowered field, with no visual expected, or when the airport is not the destination.
    /// </summary>
    private static string? ExpectedVisualAtToweredField(ClearedVisualApproachCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        string destination = CommandDispatcher.ResolveAirport(aircraft);
        if (
            !TryResolveApproachAirport(cmd.AirportCode, destination, out string airport)
            || (airport.Length == 0)
            || !ctx.ToweredAirports.IsTowered(airport)
        )
        {
            return null;
        }

        return
            NavigationDatabase.AirportIdsMatch(airport, destination) && ApproachIds.TryParseVisual(aircraft.Approach.Expected, out string? runwayId)
            ? runwayId
            : null;
    }

    /// <summary>
    /// The result of a visual clearance to a runway: the message, and the pilot's readback of the runway assignment (AIM
    /// 4-4-7.b) in 7110.65 7-4-3.c.4's words, "cleared visual approach runway two eight right", then the pattern side and the
    /// traffic to follow. The student's text says "follow the traffic"; the RPO's names its callsign.
    /// </summary>
    private static CommandResult RunwayVisualCleared(ClearedVisualApproachCommand cmd, string runwayId)
    {
        string message = $"Cleared visual approach runway {RunwayIdentifier.ToDisplayDesignator(runwayId)}";
        if (cmd.FollowCallsign is { } follow)
        {
            message += $", follow {follow}";
        }

        (PilotSpeechText readback, IReadOnlyDictionary<string, string> captures) = RunwayVisualReadback(cmd, runwayId);
        return new CommandResult(true, message)
        {
            PilotReadback = new HandlerReadback(CommandDescriber.ToCanonicalType(cmd), readback)
            {
                Variants = PhraseologyRules.VisualApproachClearance,
                Captures = captures,
            },
        };
    }

    /// <summary>
    /// The pilot's readback of a visual clearance to a runway, "cleared visual approach runway two eight right", then the
    /// pattern side and the traffic to follow (the RPO's text names its callsign), and the captures of
    /// <see cref="PhraseologyRules.VisualApproachClearance"/>' rows: <c>{rwy}</c> the spoken runway, <c>{side}</c> and
    /// <c>{follow}</c> the comma-led tail clauses, empty when the clearance has none.
    /// </summary>
    /// <param name="cmd">The visual clearance.</param>
    /// <param name="runwayId">The runway it clears to.</param>
    /// <returns>The canonical readback and the captures.</returns>
    public static (PilotSpeechText Readback, IReadOnlyDictionary<string, string> Captures) RunwayVisualReadback(
        ClearedVisualApproachCommand cmd,
        string runwayId
    )
    {
        string traffic = TrafficSideClause(cmd);
        string spokenRunway = PhraseologyVerbalizer.SpellRunway(runwayId);
        string terminal = $"cleared visual approach runway {PhraseologyVerbalizer.CompactRunway(runwayId)}{traffic}";
        string tts = $"cleared visual approach runway {spokenRunway}{traffic}";
        PilotSpeechText readback = new(terminal, tts);
        string followClause = "";
        if (cmd.FollowCallsign is { } follow)
        {
            followClause = ", follow the traffic";
            readback = new PilotSpeechText($"{terminal}{followClause}", $"{tts}{followClause}") { RpoTerminal = $"{terminal}, follow {follow}" };
        }

        var captures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rwy"] = spokenRunway,
            ["side"] = traffic,
            ["follow"] = followClause,
        };
        return (readback, captures);
    }

    private static string TrafficSideClause(ClearedVisualApproachCommand cmd) =>
        cmd.TrafficDirection switch
        {
            PatternDirection.Left => ", left traffic",
            PatternDirection.Right => ", right traffic",
            _ => "",
        };

    /// <summary>
    /// The airport-level <c>CVA</c> refusals, checked before any report is forced: a towered field must be given the runway
    /// (7110.65 7-4-3.c.4 allows "cleared visual approach to (airport)" only where no control tower operates), and following
    /// traffic (7-4-3.c.2, c.3, both towered-only) needs a runway. Null when the clearance may proceed, or when the airport
    /// does not resolve (the clearance's own airport checks report that).
    /// </summary>
    private static CommandResult? RefuseAirportLevelVisual(ClearedVisualApproachCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        if (!TryResolveApproachAirport(cmd.AirportCode, CommandDispatcher.ResolveAirport(aircraft), out string airport) || (airport.Length == 0))
        {
            return null;
        }

        if (ctx.ToweredAirports.IsTowered(airport))
        {
            return new CommandResult(false, $"Unable, say the runway for a visual approach to {airport}");
        }

        return cmd.FollowCallsign is null ? null : new CommandResult(false, $"Unable, FOLLOW needs a runway for a visual approach to {airport}");
    }

    /// <summary>
    /// The runway end the pilot takes for "cleared visual approach to (airport)" at an untowered field, first that applies:
    /// the runway the controller already named (<see cref="ControllerNamedVisualRunway"/>); the room's active arrival ends at
    /// the airport (a departure-only one skipped), as on CTAF (AC 90-66B 9.4, 11.2); every end of the airport, by the wind
    /// (AC 90-66B 11.6). Each set is ranked by <see cref="RankLandingEnds"/>, and judged per end: only an end the type can
    /// land on is a candidate. When the wind leaves no end of the airport inside the tailwind limit, the straight-in end
    /// stands. Null when the type can land on no end.
    /// </summary>
    private static RunwayInfo? PilotChosenVisualRunway(AircraftState aircraft, string airport, DispatchContext ctx)
    {
        string destination = CommandDispatcher.ResolveAirport(aircraft);
        string? expected = aircraft.Approach.Expected;
        if (ControllerNamedVisualRunway(airport, destination, expected, aircraft.Procedure.DestinationRunway, aircraft.AircraftType) is { } named)
        {
            return named;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        IEnumerable<RunwayInfo> activeEnds = ctx
            .ActiveRunways.For(airport)
            .Where(r => r.Use != ActiveRunwayUse.Departure)
            .Select(r => navDb.GetRunway(airport, r.Designator))
            .OfType<RunwayInfo>()
            .Where(end => IsLandable(end, aircraft.AircraftType));
        if (RankLandingEnds(activeEnds, aircraft, ctx).FirstOrDefault() is { } active)
        {
            return active;
        }

        List<RunwayInfo> landableEnds =
        [
            .. navDb
                .GetRunways(airport)
                .SelectMany(p => new[] { p.ForApproach(p.Id.End1), p.ForApproach(p.Id.End2) })
                .Where(end => IsLandable(end, aircraft.AircraftType)),
        ];
        return RankLandingEnds(landableEnds, aircraft, ctx).FirstOrDefault()
            ?? landableEnds.OrderBy(end => aircraft.TrueTrack.AbsAngleTo(end.TrueHeading)).FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="ends"/> as the pilot would choose among them, best first. An end whose worst-case tailwind is past
    /// <see cref="RunwayUsabilityGate"/>'s limit (10 kt dry, 5 kt wet) is dropped. In a steady wind of
    /// <see cref="RunwayInUseResolver.CalmWindBelowKt"/> or more, the end with the most headwind (to the knot) comes first,
    /// most nearly into the wind (AC 90-66B 11.6), and the aircraft's track only breaks ties; in calm or variable wind, or
    /// with no wind reported, the end most nearly aligned with the track comes first (a straight-in, AC 90-66B 9.5), then
    /// the longest.
    /// </summary>
    private static List<RunwayInfo> RankLandingEnds(IEnumerable<RunwayInfo> ends, AircraftState aircraft, DispatchContext ctx)
    {
        SurfaceWind? wind = RunwayInUseResolver.SampleWind(ctx.Weather);
        DateTime modelDate = ctx.SessionStartUtc.Date;
        double limit = FacilityRunwayKnowledge.IsWet(ctx.Weather) ? RunwayUsabilityGate.MaxTailwindKtWet : RunwayUsabilityGate.MaxTailwindKtDry;
        SurfaceWind? steadyWind = (wind is { Variable: false } && (wind.EffectiveSpeedKt >= RunwayInUseResolver.CalmWindBelowKt)) ? wind : null;
        return
        [
            .. ends.Where(end => (wind is null) || (wind.WorstTailwindOn(MagneticHeading(end, modelDate)) <= limit))
                .OrderByDescending(end => steadyWind is null ? 0 : Math.Round(steadyWind.HeadwindOn(MagneticHeading(end, modelDate))))
                .ThenBy(end => aircraft.TrueTrack.AbsAngleTo(end.TrueHeading))
                .ThenByDescending(end => end.PavementLengthFt),
        ];
    }

    private static double MagneticHeading(RunwayInfo end, DateTime modelDateUtc) =>
        MagneticDeclination.TrueToMagnetic(end.TrueHeading.Degrees, end.ThresholdLatitude, end.ThresholdLongitude, modelDateUtc);

    /// <summary>
    /// The runway the controller already named for a visual to <paramref name="airport"/>: the expected visual's
    /// (<c>VIS{rwy}</c>), else the destination runway, when <paramref name="airport"/> is the destination and the type can
    /// land on it; else null.
    /// </summary>
    internal static RunwayInfo? ControllerNamedVisualRunway(
        string airport,
        string destinationAirport,
        string? expectedApproach,
        string? destinationRunway,
        string aircraftType
    )
    {
        if (!NavigationDatabase.AirportIdsMatch(airport, destinationAirport))
        {
            return null;
        }

        string? named = ApproachIds.TryParseVisual(expectedApproach, out string? visualRunway) ? visualRunway : destinationRunway;
        return (named is not null) && (NavigationDatabase.Instance.GetRunway(airport, named) is { } runway) && IsLandable(runway, aircraftType)
            ? runway
            : null;
    }

    private static bool IsLandable(RunwayInfo end, string aircraftType) =>
        RunwayLandability.IsLandable(RunwayLandability.LandingDistanceAvailableFt(NavigationDatabase.Instance, end), aircraftType);

    /// <summary>
    /// The result of an airport-level visual clearance ("cleared visual approach to (airport)", 7110.65 7-4-3.c.4): the
    /// message and the pilot's readback name the runway the pilot picked as its own intention ("landing runway two nine"),
    /// never as a runway ATC assigned, and a pattern side likewise; the readback speaks the field's name without a trailing
    /// "Airport", as pilots say it on the radio.
    /// </summary>
    private static CommandResult AirportLevelVisualCleared(ClearedVisualApproachCommand cmd, string airport, string runway)
    {
        string traffic = TrafficSideClause(cmd);
        string spokenAirport = SpokenFieldName(airport);
        string spokenRunway = PhraseologyVerbalizer.SpellRunway(runway);
        var readback = new PilotSpeechText(
            $"cleared visual approach to {airport}, landing runway {PhraseologyVerbalizer.CompactRunway(runway)}{traffic}",
            $"cleared visual approach to {spokenAirport}, landing runway {spokenRunway}{traffic}"
        );
        var captures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apt"] = spokenAirport,
            ["rwy"] = spokenRunway,
            ["side"] = traffic,
        };
        return new CommandResult(true, $"Cleared visual approach to {airport}, landing runway {RunwayIdentifier.ToDisplayDesignator(runway)}")
        {
            PilotReadback = new HandlerReadback(CommandDescriber.ToCanonicalType(cmd), readback)
            {
                Variants = PhraseologyRules.AirportVisualApproachClearance,
                Captures = captures,
            },
        };
    }

    private static string SpokenFieldName(string airport)
    {
        const string AirportSuffix = " Airport";
        string name = PhraseologyVerbalizer.SpellAirportName(airport);
        return name.EndsWith(AirportSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^AirportSuffix.Length] : name;
    }

    /// <summary>
    /// <c>EAPP VIS{rwy}</c> (7110.65 4-7-10.d "expect visual approach runway"): resolves the runway from the navigation data,
    /// not CIFP, so a runway with no published approach can be expected too, and writes what the procedure form writes: the
    /// expected approach (<c>VIS</c> + the navdata designator), the destination runway, and the active STAR's runway
    /// transition.
    /// </summary>
    public static CommandResult TryExpectVisualApproach(string runwayId, string? airportCode, AircraftState aircraft)
    {
        if (!TryResolveApproachAirport(airportCode, CommandDispatcher.ResolveAirport(aircraft), out string airport))
        {
            return new CommandResult(false, $"Unknown airport {airportCode?.Trim().ToUpperInvariant()}");
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        if (NavigationDatabase.Instance.GetRunway(airport, runwayId) is not { } runway)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} at {airport}");
        }

        string designator = runway.Designator;
        aircraft.Approach.Expected = ApproachIds.Visual(designator);
        aircraft.Procedure.DestinationRunway = designator;
        NavigationCommandHandler.ExtendActiveStarWithRunwayTransition(aircraft, designator);
        return new CommandResult(true, ApproachIds.ExpectingText(aircraft.Approach.Expected));
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

    /// <summary>
    /// The parts of an aircraft's arrival state an approach command resolves against: the airport it falls back to with no
    /// airport code (<see cref="CommandDispatcher.ResolveAirport(AircraftState)"/>), and the expected approach and
    /// destination runway a bare clearance takes. A reader that only asks (a scenario load reading its presets in order)
    /// passes its own, so it never writes the aircraft.
    /// </summary>
    internal readonly record struct ApproachResolutionContext(string FallbackAirport, string? ExpectedApproach, string? DestinationRunway)
    {
        public static ApproachResolutionContext Of(AircraftState aircraft) =>
            new(CommandDispatcher.ResolveAirport(aircraft), aircraft.Approach.Expected, aircraft.Procedure.DestinationRunway);
    }

    /// <summary>
    /// The airport an approach command is for: <paramref name="airportCode"/> when it names one (false when the navigation
    /// data does not know it), else <paramref name="fallbackAirport"/> (the aircraft's destination, else its assigned
    /// runway's airport); empty when none applies.
    /// </summary>
    internal static bool TryResolveApproachAirport(string? airportCode, string fallbackAirport, out string airport)
    {
        if (airportCode is null)
        {
            airport = fallbackAirport;
            return true;
        }

        if (!NavigationDatabase.Instance.TryResolveAirport(airportCode, out string canonical))
        {
            airport = "";
            return false;
        }

        airport = canonical;
        return true;
    }

    internal static ResolvedApproach ResolveApproach(string? approachId, string? airportCode, AircraftState aircraft) =>
        ResolveApproach(approachId, airportCode, aircraft, ApproachResolutionContext.Of(aircraft));

    /// <summary>
    /// <see cref="ResolveApproach(string?, string?, AircraftState)"/> against <paramref name="context"/> in place of the
    /// aircraft's own arrival state; the aircraft still supplies its route and position for disambiguation.
    /// </summary>
    internal static ResolvedApproach ResolveApproach(
        string? approachId,
        string? airportCode,
        AircraftState aircraft,
        ApproachResolutionContext context
    )
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (!TryResolveApproachAirport(airportCode, context.FallbackAirport, out string airport))
        {
            return ResolvedApproach.Fail($"Unknown airport {airportCode?.Trim().ToUpperInvariant()}");
        }

        if (string.IsNullOrEmpty(airport))
        {
            return ResolvedApproach.Fail("Cannot determine airport for approach");
        }

        if (approachId is null)
        {
            return AutoResolveApproach(navDb, airport, aircraft, context);
        }

        List<string> candidates = navDb.ResolveApproachCandidates(airport, approachId);
        if (candidates.Count == 0)
        {
            return ResolvedApproach.Fail($"Unknown approach: {approachId} at {airport}");
        }

        if (candidates.Count == 1)
        {
            return BuildResolved(navDb, airport, candidates[0]);
        }

        return DisambiguateCandidates(navDb, airport, candidates, aircraft, context.ExpectedApproach);
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
    private static ResolvedApproach AutoResolveApproach(
        NavigationDatabase navDb,
        string airport,
        AircraftState aircraft,
        ApproachResolutionContext context
    )
    {
        HashSet<string> knownFixes = BuildKnownFixes(aircraft);
        bool onNavRoute = aircraft.Targets.NavigationRoute.Count > 0;

        // Tier 1: try hint sources in priority order
        string?[] sources = [context.ExpectedApproach, context.DestinationRunway];

        foreach (string? source in sources)
        {
            // A visual expectation names no procedure: the destination runway it set is the hint that follows.
            if ((source is null) || ApproachIds.TryParseVisual(source, out _))
            {
                continue;
            }

            List<string> candidates = navDb.ResolveApproachCandidates(airport, source);
            if (candidates.Count == 0)
            {
                continue;
            }

            if (onNavRoute)
            {
                // Verify route connectivity — don't blindly use a disconnected approach
                string? connected = FindConnectedCandidate(navDb, airport, candidates, knownFixes);
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

            return DisambiguateCandidates(navDb, airport, candidates, aircraft, context.ExpectedApproach);
        }

        // Tier 2: auto-discover any approach at the airport that connects to the route
        if (onNavRoute)
        {
            IReadOnlyList<CifpApproachProcedure> allApproaches = navDb.GetApproaches(airport);
            foreach (CifpApproachProcedure proc in allApproaches)
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
            CifpApproachProcedure? proc = navDb.GetApproach(airport, candidateId);
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
        AircraftState aircraft,
        string? expectedApproach
    )
    {
        HashSet<string> knownFixes = BuildKnownFixes(aircraft);

        // Priority 1: ExpectedApproach, if it matches one of the candidates and connects
        if (expectedApproach is not null)
        {
            string? expMatch = candidates.FirstOrDefault(c => c.Equals(expectedApproach, StringComparison.OrdinalIgnoreCase));
            if (expMatch is not null)
            {
                CifpApproachProcedure? expProc = navDb.GetApproach(airport, expMatch);
                if (expProc is not null && HasRouteConnectivity(expProc, knownFixes))
                {
                    return BuildResolved(navDb, airport, expMatch);
                }
            }
        }

        // Priority 2: first candidate whose fixes overlap with the aircraft's route
        foreach (string candidateId in candidates)
        {
            CifpApproachProcedure? proc = navDb.GetApproach(airport, candidateId);
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
        CifpApproachProcedure? procedure = navDb.GetApproach(airport, approachId, out ProcedureSource? procedureSource);
        if (procedure?.Runway is null)
        {
            return ResolvedApproach.Fail($"No runway for approach {approachId}");
        }

        RunwayInfo? runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return ResolvedApproach.Fail($"Unknown runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")} at {airport}");
        }

        RunwayInfo approachRunway = runway.IsActiveEnd(procedure.Runway) ? runway : runway.ForApproach(procedure.Runway);

        return new ResolvedApproach(procedure, approachRunway, airport, procedureSource);
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
            foreach (string token in aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int dotIdx = token.IndexOf('.');
                string fixName = dotIdx >= 0 ? token[..dotIdx] : token;
                if (!string.IsNullOrEmpty(fixName))
                {
                    fixes.Add(fixName);
                }
            }
        }

        foreach (NavigationTarget navTarget in aircraft.Targets.NavigationRoute)
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
        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            foreach (CifpLeg leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && knownFixes.Contains(leg.FixIdentifier))
                {
                    return true;
                }
            }
        }

        foreach (CifpLeg leg in procedure.CommonLegs)
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
        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            foreach (CifpLeg leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier))
                {
                    names.Add(leg.FixIdentifier);
                }
            }
        }

        // Collect common segment fixes (stop before MAP)
        foreach (CifpLeg leg in procedure.CommonLegs)
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
        foreach (CifpLeg leg in procedure.CommonLegs)
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
        NavigationDatabase navDb = NavigationDatabase.Instance;
        foreach (CifpLeg leg in procedure.CommonLegs)
        {
            if (leg.FixRole != CifpFixRole.MAP || string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
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
        NavigationDatabase navDb = NavigationDatabase.Instance;
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (CifpLeg leg in procedure.MissedApproachLegs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
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
                    leg.TurnDirection
                );
            }

            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is not null
                && previousFixPos is not null
            )
            {
                (double Lat, double Lon)? navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection
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
        ApproachClearance? clearance = aircraft.Phases?.ActiveApproach;
        if (clearance?.Procedure is null || clearance.MissedApproachFixes.Count == 0)
        {
            return [];
        }

        // Visual approaches have no MAP
        if (clearance.ApproachId.StartsWith("VIS", StringComparison.Ordinal))
        {
            return [];
        }

        var phases = new List<Phase>
        {
            new ApproachNavigationPhase { Fixes = clearance.MissedApproachFixes, IsMissedApproach = true },
        };

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
        foreach (ApproachFix fix in mapFixes)
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
        NavigationDatabase navDb = NavigationDatabase.Instance;
        for (int i = procedure.MissedApproachLegs.Count - 1; i >= 0; i--)
        {
            CifpLeg leg = procedure.MissedApproachLegs[i];
            if (leg.PathTerminator is not (CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM))
            {
                continue;
            }

            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
            if (pos is null)
            {
                continue;
            }

            // The hold leg's course field is the published inbound holding course (magnetic), not an outbound one.
            int inboundCourse = 0;
            if (leg.OutboundCourse is { } holdMagDeg)
            {
                string? navaid = HoldLegNavaid(procedure.MissedApproachLegs, i);
                double declination = navDb.GetPublishedCourseDeclination(navaid, procedure.Airport, new LatLon(pos.Value.Lat, pos.Value.Lon));
                inboundCourse = RoundToWholeDegree(new MagneticHeading(holdMagDeg).ToTrue(declination));
            }

            double legLength = leg.LegDistanceNm ?? 1.0;
            bool isMinuteBased = leg.LegDistanceNm is null;
            TurnDirection direction = leg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right;

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
            foreach (string token in aircraft.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int dotIdx = token.IndexOf('.');
                string fixName = dotIdx >= 0 ? token[..dotIdx] : token;
                if (!string.IsNullOrEmpty(fixName))
                {
                    knownFixes.Add(fixName);
                }
            }
        }

        foreach (NavigationTarget navTarget in aircraft.Targets.NavigationRoute)
        {
            knownFixes.Add(navTarget.Name);
        }

        // Build set of all fix names in the approach (transitions + common legs).
        var approachFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CifpLeg leg in procedure.CommonLegs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier))
            {
                approachFixes.Add(leg.FixIdentifier);
            }
        }

        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            foreach (CifpLeg leg in transition.Legs)
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
        foreach (NavigationTarget navTarget in aircraft.Targets.NavigationRoute)
        {
            if (!approachFixes.Contains(navTarget.Name))
            {
                continue;
            }

            // If the matched fix anchors a course-reversal leg (PI/HM/HF/HA) in any transition,
            // prefer that transition even if the fix also lives in CommonLegs. The controller
            // said "DCT <fix>" implying entry at <fix> with the published course reversal.
            foreach (CifpTransition transition in procedure.Transitions.Values)
            {
                foreach (CifpLeg leg in transition.Legs)
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
            foreach (CifpTransition transition in procedure.Transitions.Values)
            {
                foreach (CifpLeg leg in transition.Legs)
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
            foreach (CifpTransition transition in procedure.Transitions.Values)
            {
                foreach (CifpLeg leg in transition.Legs)
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
        NavigationDatabase navDb = NavigationDatabase.Instance;
        CifpTransition? bestTransition = null;
        bool bestIsCommonLeg = false;
        double bestDist = double.MaxValue;

        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            CifpLeg? firstLeg = null;
            foreach (CifpLeg leg in transition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier))
                {
                    firstLeg = leg;
                    break;
                }
            }

            if (firstLeg is null)
            {
                continue;
            }

            (double Lat, double Lon)? pos = firstLeg.ResolveFixPosition(navDb);
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

        foreach (CifpLeg leg in procedure.CommonLegs)
        {
            if (leg.FixRole is not (CifpFixRole.IAF or CifpFixRole.IF))
            {
                continue;
            }
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
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
    private static HoldingPatternPhase? BuildHoldInLieuPhase(
        CifpLeg holdLeg,
        string? holdNavaidId,
        IReadOnlyList<ApproachFix> approachFixes,
        string airportId,
        TrueHeading finalCourse
    )
    {
        ApproachFix? holdFix = approachFixes.FirstOrDefault(f => f.Name.Equals(holdLeg.FixIdentifier, StringComparison.OrdinalIgnoreCase));
        if (holdFix is null)
        {
            return null;
        }

        // A hold leg's (HA/HF/HM) course field is the published inbound holding course, in magnetic degrees
        // referenced to the hold's navaid (or the airport's variation of record for an RNAV hold).
        var holdFixPosition = new LatLon(holdFix.Latitude, holdFix.Longitude);
        TrueHeading inboundTrue = holdLeg.OutboundCourse is { } holdMagDeg
            ? new MagneticHeading(holdMagDeg).ToTrue(
                NavigationDatabase.Instance.GetPublishedCourseDeclination(holdNavaidId, airportId, holdFixPosition)
            )
            : finalCourse;
        int inboundCourse = RoundToWholeDegree(inboundTrue);

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
            IsHoldInLieu = true,
        };
    }

    /// <summary>
    /// Build a <see cref="ProcedureTurnPhase"/> from the procedure's PI leg. The PT anchor
    /// fix must already be loadable from the navdata (CCR for KCCR S19R). The inbound course is the
    /// course of the leg into the PT fix (for KCCR S19R the CF CCR FAF leg, not the final approach
    /// course past it). Both it and the PT heading are published magnetic and convert to true with the
    /// declination of their own reference (<see cref="NavigationDatabase.GetPublishedCourseDeclination"/>).
    /// A PI leg without a course flies the standard 45° heading off the outbound course, on the side
    /// the 180° turn back is made away from.
    /// </summary>
    internal static ProcedureTurnPhase? BuildProcedureTurnPhase(
        CifpLeg piLeg,
        CifpApproachProcedure procedure,
        string airportId,
        TrueHeading finalCourse
    )
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        (double Lat, double Lon)? pos = piLeg.ResolveFixPosition(navDb);
        if (pos is null)
        {
            return null;
        }

        var fixPosition = new LatLon(pos.Value.Lat, pos.Value.Lon);
        TurnDirection turnBack = piLeg.TurnDirection == 'L' ? TurnDirection.Left : TurnDirection.Right;
        IReadOnlyList<CifpLeg> legsIntoFix = ProcedureTurnInboundCandidates(procedure, piLeg);
        TrueHeading inboundTrue = ResolveProcedureTurnInboundCourse(piLeg, legsIntoFix, airportId, fixPosition, finalCourse);
        TrueHeading ptOutboundTrue;
        if (piLeg.OutboundCourse is { } ptMagDeg)
        {
            ptOutboundTrue = new MagneticHeading(ptMagDeg).ToTrue(
                navDb.GetPublishedCourseDeclination(piLeg.RecommendedNavaidId, airportId, fixPosition)
            );
        }
        else
        {
            // A left turn back means the 45° leg was flown to the right of the outbound course, and the reverse.
            ptOutboundTrue = new TrueHeading(inboundTrue.Degrees + 180.0 + (turnBack == TurnDirection.Left ? 45.0 : -45.0));
            Log.LogWarning(
                "[ProcedureTurn] {ApproachId}: PI leg at {Fix} carries no course, flying {Hdg:000}T (45° off the outbound course)",
                procedure.ApproachId,
                piLeg.FixIdentifier,
                ptOutboundTrue.Degrees
            );
        }

        int minAlt = piLeg.Altitude is { } restriction ? restriction.Altitude1Ft : 0;

        return new ProcedureTurnPhase
        {
            FixName = piLeg.FixIdentifier,
            FixLat = pos.Value.Lat,
            FixLon = pos.Value.Lon,
            InboundCourseDeg = inboundTrue.Degrees,
            PtOutboundCourseDeg = ptOutboundTrue.Degrees,
            MaxOutboundDistanceNm = piLeg.LegDistanceNm ?? 10.0,
            OneEightyTurnDirection = turnBack,
            MinAltitudeFt = minAlt,
        };
    }

    /// <summary>
    /// The legs that can publish a procedure turn's inbound course, best first: the legs after the PI leg in its
    /// own transition that end at the PT fix with a course (KACK S24: PI ACK then CF ACK 239.6), then the common
    /// legs that do (KCCR S19R: CF CCR 190.6, the FAF leg), the FAF leg first.
    /// </summary>
    private static List<CifpLeg> ProcedureTurnInboundCandidates(CifpApproachProcedure procedure, CifpLeg piLeg)
    {
        bool EndsAtFixWithCourse(CifpLeg leg) =>
            leg.FixIdentifier.Equals(piLeg.FixIdentifier, StringComparison.OrdinalIgnoreCase) && (leg.OutboundCourse is not null);

        var candidates = new List<CifpLeg>();
        foreach (CifpTransition transition in procedure.Transitions.Values)
        {
            int piIndex = transition.Legs.ToList().FindIndex(l => (l.PathTerminator == CifpPathTerminator.PI) && EndsAtFixName(l, piLeg));
            if (piIndex >= 0)
            {
                candidates.AddRange(transition.Legs.Skip(piIndex + 1).Where(EndsAtFixWithCourse));
                break;
            }
        }

        List<CifpLeg> commonIntoFix = [.. procedure.CommonLegs.Where(EndsAtFixWithCourse)];
        candidates.AddRange(commonIntoFix.Where(l => l.FixRole == CifpFixRole.FAF));
        candidates.AddRange(commonIntoFix.Where(l => l.FixRole != CifpFixRole.FAF));
        return candidates;
    }

    private static bool EndsAtFixName(CifpLeg leg, CifpLeg other) =>
        leg.FixIdentifier.Equals(other.FixIdentifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The true inbound course of a procedure turn: the published course of the first of
    /// <paramref name="legsIntoFix"/>, converted with its reference's declination. Falls back to the final
    /// approach course when there is none.
    /// </summary>
    private static TrueHeading ResolveProcedureTurnInboundCourse(
        CifpLeg piLeg,
        IReadOnlyList<CifpLeg> legsIntoFix,
        string airportId,
        LatLon fixPosition,
        TrueHeading finalCourse
    )
    {
        CifpLeg? inboundLeg = legsIntoFix.Count > 0 ? legsIntoFix[0] : null;
        if (inboundLeg?.OutboundCourse is not { } inboundMagDeg)
        {
            Log.LogDebug("[ProcedureTurn] no leg into {Fix} carries a course, using the final approach course", piLeg.FixIdentifier);
            return finalCourse;
        }

        double declination = NavigationDatabase.Instance.GetPublishedCourseDeclination(inboundLeg.RecommendedNavaidId, airportId, fixPosition);
        return new MagneticHeading(inboundMagDeg).ToTrue(declination);
    }

    /// <summary>
    /// The navaid a hold leg's course is referenced to: its own recommended navaid, else that of the nearest
    /// preceding leg ending at the same fix (KCCR S19R missed approach: HM REJOY names none, the CF CCR leg into
    /// REJOY names CCR). Null for an RNAV hold, which then converts with the airport's variation of record.
    /// </summary>
    private static string? HoldLegNavaid(IReadOnlyList<CifpLeg> legs, int holdIndex)
    {
        CifpLeg holdLeg = legs[holdIndex];
        if (!string.IsNullOrWhiteSpace(holdLeg.RecommendedNavaidId))
        {
            return holdLeg.RecommendedNavaidId;
        }

        for (int i = holdIndex - 1; i >= 0; i--)
        {
            if (EndsAtFixName(legs[i], holdLeg))
            {
                return string.IsNullOrWhiteSpace(legs[i].RecommendedNavaidId) ? null : legs[i].RecommendedNavaidId;
            }
        }

        return null;
    }

    /// <summary>The hold-in-lieu leg's navaid (<see cref="HoldLegNavaid"/>), from whichever leg list carries the leg.</summary>
    private static string? HoldInLieuNavaid(CifpApproachProcedure procedure, CifpLeg holdLeg)
    {
        foreach (IReadOnlyList<CifpLeg> legs in procedure.Transitions.Values.Select(t => t.Legs).Append(procedure.CommonLegs))
        {
            int holdIndex = legs.ToList().IndexOf(holdLeg);
            if (holdIndex >= 0)
            {
                return HoldLegNavaid(legs, holdIndex);
            }
        }

        return string.IsNullOrWhiteSpace(holdLeg.RecommendedNavaidId) ? null : holdLeg.RecommendedNavaidId;
    }

    private static int RoundToWholeDegree(TrueHeading heading) => (int)Math.Round(heading.Degrees) % 360;

    private static List<ApproachFix> BuildApproachFixesWithTransition(CifpTransition transition, CifpApproachProcedure procedure)
    {
        List<ApproachFix> transitionFixes = BuildFixesFromLegs(transition.Legs, stopAtMahp: false);
        List<ApproachFix> commonFixes = BuildFixesFromLegs(procedure.CommonLegs, stopAtMahp: true);

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

    private static List<ApproachFix> BuildApproachFixes(CifpApproachProcedure procedure) =>
        BuildFixesFromLegs(procedure.CommonLegs, stopAtMahp: true);

    private static List<ApproachFix> BuildFixesFromLegs(IReadOnlyList<CifpLeg> legs, bool stopAtMahp)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        var result = new List<ApproachFix>();
        (double Lat, double Lon)? previousFixPos = null;

        foreach (CifpLeg leg in legs)
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

            (double Lat, double Lon)? pos = leg.ResolveFixPosition(navDb);
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
                    leg.TurnDirection
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
                (double Lat, double Lon)? navaidPos = navDb.GetFixPosition(leg.RecommendedNavaidId);
                if (navaidPos is not null)
                {
                    ExpandApproachArcFixes(
                        result,
                        navaidPos.Value.Lat,
                        navaidPos.Value.Lon,
                        leg.Rho.Value,
                        previousFixPos.Value,
                        pos.Value,
                        leg.TurnDirection
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
        char? turnDirection
    )
    {
        double startBearing = GeoMath.BearingTo(centerLat, centerLon, previousFix.Lat, previousFix.Lon);
        double endBearing = GeoMath.BearingTo(centerLat, centerLon, terminatorFix.Lat, terminatorFix.Lon);
        bool turnRight = GeoMath.ResolveArcTurnRight(turnDirection, startBearing, endBearing);

        List<(double Lat, double Lon)> arcPoints = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight);

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
        CifpTransition? transition,
        string airport,
        FinalApproachCourseResult fac,
        RunwayInfo approachRunway,
        bool force
    )
    {
        return new ApproachClearance
        {
            TransitionName = transition?.Name,
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
        foreach (NavigationTarget target in aircraft.Targets.NavigationRoute)
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
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;

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
            ApproachFix fix = approachFixes[i];
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
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;

        aircraft.Phases = new PhaseList { AssignedRunway = pending.AssignedRunway, ActiveApproach = pending.Clearance };
        aircraft.Phases.Add(new FinalApproachPhase());
        bool isHeli = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        aircraft.Phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());

        // No route to the room's active runways: this handler is not given the dispatch context.
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
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
            ApproachFix fix = fixes[i];
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
    internal static void ClearPendingApproach(AircraftState aircraft) => aircraft.Approach.PendingClearance = null;

    /// <summary>
    /// Clears arrival procedure state when the destination airport is superseded (APT to another airport).
    ///
    /// Two gates decide what happens. The first is intent: only an aircraft that is actually arriving
    /// somewhere — an approach cleared or pending, a VFR circuit, a go-around, a landing clearance — has an
    /// arrival to lose. A departure never does, airborne or not: <see cref="PhaseList.AssignedRunway"/> is
    /// the DEPARTURE runway while <see cref="InitialClimbPhase"/> owns the climb-out, so an airport test
    /// alone would destroy the climb chain of a jet that had just left the field. The second gate is the
    /// airport: when the arrival is flown to <paramref name="newDestination"/> already, the edit is the RPO
    /// correcting a mis-filed plan toward the airport the aircraft is being worked into, and nothing is
    /// cleared at all — cancelling the approach and wiping the route it is flying would be the opposite of
    /// what was asked. Otherwise the arrival belongs to the airport being left: the whole phase chain goes
    /// through <see cref="CommandDispatcher.ClearPhaseChain"/> (runway assignment and landing clearance with
    /// it, the RPO warned that <paramref name="cancelledBy"/> cancelled it), so a following pattern entry
    /// resolves its runway at the new airport, and the route, STAR, expected approach and destination runway
    /// are dropped. Departure-only fields such as <see cref="AircraftProcedure.DepartureRunway"/> are never
    /// touched.
    /// </summary>
    internal static void ClearArrivalProcedureState(AircraftState aircraft, string newDestination, string cancelledBy)
    {
        Phase? current = aircraft.Phases?.CurrentPhase;
        bool hasArrivalIntent =
            aircraft.Approach.PendingClearance is not null
            || aircraft.Phases?.ActiveApproach is not null
            || IsArrivalApproachPhase(current)
            || (current is not null && PhaseClearSummary.IsPatternFamily(current))
            || current is GoAroundPhase
            || aircraft.Phases?.LandingClearance is not null;

        string? arrivalAirport =
            aircraft.Phases?.AssignedRunway?.AirportId
            ?? aircraft.Phases?.ActiveApproach?.AirportCode
            ?? aircraft.Approach.PendingClearance?.Clearance.AirportCode;

        if (hasArrivalIntent && arrivalAirport is not null && NavigationDatabase.Instance.AirportIdsMatchResolved(arrivalAirport, newDestination))
        {
            return;
        }

        if (hasArrivalIntent && aircraft.Phases is not null)
        {
            CommandDispatcher.ClearPhaseChain(aircraft, cancelledBy);
        }
        else
        {
            // ClearPhaseChain drops the FOLLOW state itself; without a chain to tear down, a FOLLOW aimed
            // at traffic into the old airport still has to go with the rest of the arrival state below.
            Phases.AirborneFollowHelper.ClearFollowState(aircraft);
        }

        ClearPendingApproach(aircraft);
        aircraft.Approach.Expected = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Procedure.DestinationRunway = null;
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.StarViaFloor = null;
        aircraft.Approach.HasReportedFieldInSight = false;
        aircraft.Approach.HasReportedTrafficInSight = false;
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
    }

    /// <summary>
    /// True when the aircraft's phase puts it inbound on an arrival approach
    /// (approach-navigation, intercept, final, hold, or procedure turn).
    /// </summary>
    internal static bool IsArrivalApproachPhase(Phase? phase) =>
        phase is ApproachNavigationPhase or InterceptCoursePhase or FinalApproachPhase or HoldingPatternPhase or ProcedureTurnPhase;

    /// <summary>
    /// Widest angle between the aircraft's track and the landing course that still counts as being on
    /// final. A pattern downwind tracks the reciprocal and a base leg tracks across it, so both fall
    /// well outside. Shared with <see cref="RunwayOccupancy"/> so "on final" means one thing.
    /// </summary>
    private const double OnFinalTrackToleranceDeg = RunwayOccupancy.FinalTrackToleranceDeg;

    /// <summary>
    /// True when the aircraft is on final for <paramref name="runway"/>, the position §5-7-1.b.4
    /// describes: "inside the final approach fix <em>on final</em> or a point 5 miles from the runway,
    /// whichever is closer to the runway." Both clauses describe a position on final — distance alone is
    /// not enough. A traffic-pattern downwind sits roughly a mile abeam the runway, so testing distance
    /// by itself gated the entire circuit and made a tower speed instruction to pattern traffic
    /// impossible, even though §3-8-1 lists speed among the tower's sequencing tools.
    ///
    /// Established on an approach counts regardless of track — that also keeps the over-the-runway
    /// segment of a low approach gated. Otherwise the aircraft must both be tracking the landing course
    /// and still be flying toward the threshold, so the upwind leg (same heading, opposite side, flying
    /// away) is not mistaken for final.
    /// </summary>
    /// <param name="aircraft">The aircraft being tested.</param>
    /// <param name="runway">The landing runway end.</param>
    /// <param name="layout">
    /// The ground layout the runway's threshold displacement is read from
    /// (<see cref="LandingThreshold.Resolve(RunwayInfo, AirportGroundLayout?)"/>): the threshold being flown toward is
    /// the landing one, so an aircraft over the departures-only pavement behind a displaced threshold is still on final
    /// rather than past it. Null — no ground map for the field — falls back to the pavement threshold.
    /// </param>
    internal static bool IsOnFinal(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? layout)
    {
        if (aircraft.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase or LowApproachPhase)
        {
            return true;
        }

        if (aircraft.TrueTrack.AbsAngleTo(runway.TrueHeading) > OnFinalTrackToleranceDeg)
        {
            return false;
        }

        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        double bearingToThreshold = GeoMath.BearingTo(aircraft.Position, threshold);
        return aircraft.TrueTrack.AbsAngleTo(new TrueHeading(bearingToThreshold)) <= 90.0;
    }

    /// <summary>
    /// True when the aircraft is inbound to land — being vectored or established on an
    /// approach, or cleared to land (including in the pattern). This is the trigger for
    /// the §5-7-1.b.4 5nm-final speed gate (reject new assignments, auto-cancel an
    /// explicit ATC speed). A departure — which carries an <c>AssignedRunway</c> for its
    /// departure runway but has no landing/approach clearance — and a go-around (climbing
    /// out) are deliberately excluded so neither is blocked from a speed assignment.
    /// </summary>
    internal static bool IsInboundToLand(AircraftState aircraft)
    {
        PhaseList? phases = aircraft.Phases;
        if (phases is null || phases.CurrentPhase is GoAroundPhase)
        {
            return false;
        }

        return phases.LandingClearance is not null || phases.ActiveApproach is not null || IsArrivalApproachPhase(phases.CurrentPhase);
    }

    /// <summary>
    /// An approach clearance cancels any previously assigned speed (7110.65 5-7-1.d; AIM 4-4-12.g) and ends the STAR's
    /// last-crossed published speed with the STAR (7110.65 5-7-1.e and NOTE). The dispatcher puts back a speed restated
    /// in the clearance's transmission (<see cref="DispatchContext.SpeedRestatedInTransmission"/>; 7110.65 5-7-1.c). The
    /// same-runway in-trail reduction's ceiling is not a speed the clearance cancels: it belongs to that pass and is
    /// restated with the clearance (<see cref="RestateStandingInTrailReduction"/>).
    /// </summary>
    private static void CancelSpeedsForApproachClearance(AircraftState aircraft)
    {
        double? ceiling = aircraft.Targets.SpeedCeiling;
        FlightCommandHandler.CancelAssignedSpeed(aircraft);
        if ((aircraft.Approach.SameRunwayProtectionCeilingKts is { } standingReductionKts) && (ceiling == standingReductionKts))
        {
            aircraft.Targets.SpeedCeiling = ceiling;
        }

        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
    }

    /// <summary>
    /// Says out loud a same-runway in-trail reduction that outlives the approach clearance. The clearance cancels the
    /// speed restrictions above (§5-7-1), but the reduction the simulated TRACON is holding this arrival to is not one
    /// of them — it is re-stamped every tick and the aircraft keeps flying it — and §5-7-1.c requires a previously
    /// assigned speed to be restated at the time the approach clearance is issued if it is to stay in force, because
    /// AIM 4-4-12.g otherwise has the pilot making their own speed adjustments once cleared. Nothing is written to the
    /// aircraft: <see cref="ControlTargets.SpeedCeiling"/> belongs to that pass, and this is only the controller
    /// saying it.
    ///
    /// <para>A latched simulated-tower instruction is not restated. What that arrival was told is "reduce to final
    /// approach speed" — a speed below the §5-7-3.c minima under §5-7-3.f, and never a number — so the approach
    /// controller issuing the clearance has neither the instruction nor the figure to restate.</para>
    /// </summary>
    private static void RestateStandingInTrailReduction(AircraftState aircraft)
    {
        if (aircraft.Approach.SameRunwayProtectionFasInstructed)
        {
            return;
        }

        if (aircraft.Approach.SameRunwayProtectionCeilingKts is not { } standingReductionKts)
        {
            return;
        }

        aircraft.PendingNotifications.Add(
            SameRunwayArrivalProtection.RestatementLine(
                aircraft.Track.Owner?.Callsign ?? SameRunwayArrivalProtection.SimulatedApproachControllerLabel,
                aircraft.Callsign,
                standingReductionKts
            )
        );
    }

    private static void ClearExistingPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is not null)
        {
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
            aircraft.Phases.Clear(ctx);
        }

        // Clear STAR state so stale descent logic doesn't conflict with approach phases
        aircraft.Procedure.ActiveStarId = null;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
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
            // No route to the room's active runways: this handler is not given the dispatch context.
            PhaseContext startCtx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
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

        /// <summary>Non-current source that supplied the procedure; null when it came from the current CIFP cycle.</summary>
        public ProcedureSource? Source { get; }

        public ResolvedApproach(CifpApproachProcedure procedure, RunwayInfo runway, string airport, ProcedureSource? source)
        {
            Success = true;
            Procedure = procedure;
            Runway = runway;
            Airport = airport;
            Source = source;
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
