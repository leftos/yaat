using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Commands;

internal static class NavigationCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("NavigationCommandHandler");

    /// <summary>Renders a fix for command responses / SHOWAT descriptions as "NAME (ID)" when it has
    /// an authored display name (e.g. "LAKE CHABOT (VPCBT)"), or the bare identifier otherwise.</summary>
    private static string FixDisplay(string fix) => PhraseologyVerbalizer.FixDisplayTextUpper(fix);

    /// <summary>
    /// Converts a controller-issued magnetic radial from <paramref name="fixName"/> to the true bearing from the fix, with the
    /// station's variation of record (<see cref="RadialConversion.ToTrue"/>).
    /// </summary>
    internal static TrueHeading RadialToTrueBearing(string fixName, double fixLat, double fixLon, int magneticRadial) =>
        RadialConversion.ToTrue(NavigationDatabase.Instance, fixName, new LatLon(fixLat, fixLon), magneticRadial);

    /// <summary>
    /// <see cref="RadialToTrueBearing"/> rounded to whole degrees, which is what a <see cref="BlockTrigger.TrueRadial"/> holds.
    /// </summary>
    internal static int RadialToTrue(string fixName, double fixLat, double fixLon, int magneticRadial) =>
        ToWholeTrueRadial(RadialToTrueBearing(fixName, fixLat, fixLon, magneticRadial));

    /// <summary>A true bearing rounded to whole degrees in 0-359, the form a <see cref="BlockTrigger.TrueRadial"/> holds.</summary>
    internal static int ToWholeTrueRadial(TrueHeading bearing) => (int)Math.Round(bearing.Degrees) % 360;

    internal static CommandResult DispatchJrado(JoinRadialOutboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, fly outbound heading (the radial is magnetic, referenced to the station's variation)
        aircraft.Queue.Blocks.Add(BuildJradoInterceptBlock(cmd));

        return CommandDispatcher.Ok($"Fly present heading, intercept {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial outbound");
    }

    /// <summary>
    /// The block a JRADO leaves queued behind itself: on intercepting the radial, fly it outbound. Built here
    /// rather than inline so <c>CommandDispatcher.RehydrateRestoredBlock</c> rebuilds a restored block from the
    /// same closure; applying the re-parsed instruction instead would set the immediate half and queue a
    /// second block.
    /// </summary>
    internal static CommandBlock BuildJradoInterceptBlock(JoinRadialOutboundCommand cmd)
    {
        TrueHeading trueRadial = RadialToTrueBearing(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial);
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                TrueRadial = RadialToTrue(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial),
                IssuedRadial = cmd.Radial,
            },
            ApplyAction = (ac, _) =>
            {
                var magneticHdg = new MagneticHeading(cmd.Radial);
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetTrueHeading = trueRadial;
                ac.Targets.AssignedMagneticHeading = magneticHdg;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)} R{cmd.Radial:D3}: FH {cmd.Radial:D3}",
            NaturalDescription = $"On {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial: fly heading {cmd.Radial:D3}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        return block;
    }

    internal static CommandResult DispatchJradi(JoinRadialInboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, navigate inbound to fix
        aircraft.Queue.Blocks.Add(BuildJradiInterceptBlock(cmd));

        return CommandDispatcher.Ok($"Fly present heading, intercept {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial inbound");
    }

    /// <summary>
    /// The block a JRADI leaves queued behind itself: on intercepting the radial, proceed inbound to the fix.
    /// Built here for the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildJradiInterceptBlock(JoinRadialInboundCommand cmd)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                TrueRadial = RadialToTrue(cmd.FixName, cmd.FixLat, cmd.FixLon, cmd.Radial),
                IssuedRadial = cmd.Radial,
            },
            ApplyAction = (ac, _) =>
            {
                ac.Targets.AssignedMagneticHeading = null;
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = cmd.FixName, Position = new LatLon(cmd.FixLat, cmd.FixLon) });
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)} R{cmd.Radial:D3}: DCT {FixDisplay(cmd.FixName)}",
            NaturalDescription = $"On {FixDisplay(cmd.FixName)} {cmd.Radial:D3} radial: proceed inbound to {FixDisplay(cmd.FixName)}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        return block;
    }

    internal static CommandResult DispatchDepartFix(DepartFixCommand cmd, AircraftState aircraft)
    {
        // Preserve any crossing restriction already assigned to the depart fix (e.g. a
        // preceding `CFIX CASST 6000 210`). "Depart FIX heading" proceeds direct to the fix
        // and then turns — it must not discard a cross-at altitude/speed the controller set
        // on that same fix, otherwise the aircraft sails over it at default cruise (issue #184).
        NavigationTarget? existing = aircraft.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));

        // Block 0 (immediate): navigate to fix (navigation phase — clear assigned heading)
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = cmd.FixName,
                Position = new LatLon(cmd.FixLat, cmd.FixLon),
                AltitudeRestriction = existing?.AltitudeRestriction,
                SpeedRestriction = existing?.SpeedRestriction,
                RevertAltitude = existing?.RevertAltitude,
                RevertAssignedAltitude = existing?.RevertAssignedAltitude,
                RevertSpeed = existing?.RevertSpeed,
                RevertAssignedSpeed = existing?.RevertAssignedSpeed,
                CrossingTransmissionId = existing?.CrossingTransmissionId,
                SpeedTransmissionId = existing?.SpeedTransmissionId,
            }
        );

        // Block 1: on reaching fix, fly heading (controller heading)
        aircraft.Queue.Blocks.Add(BuildDepartBlock(cmd));

        return CommandDispatcher.Ok($"Proceed direct {FixDisplay(cmd.FixName)}, depart heading {cmd.MagneticHeading.ToDisplayInt():000}");
    }

    /// <summary>
    /// The block a DEPART leaves queued behind itself: on reaching the fix, turn to the depart heading and
    /// publish any crossing-restriction speed stamped on that fix as the aircraft's ceiling. Built here for
    /// the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildDepartBlock(DepartFixCommand cmd)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
            },
            ApplyAction = (ac, _) =>
            {
                // A crossing-restriction speed assigned at this fix is an ATC-assigned speed,
                // not a published one — it persists through the depart vector until an approach
                // or via clearance (7110.65 §5-7-1.e assigns it with the vector; §5-7-1.d — only
                // an approach or climb via/descend via clearance cancels it). Publish it as a
                // ceiling so the aircraft does not accelerate back to default cruise after the turn.
                NavigationTarget? departFix = ac.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));
                if (departFix?.SpeedRestriction is { } crossingSpeed && !ac.Targets.HasExplicitSpeedCommand)
                {
                    ac.Targets.SpeedCeiling = crossingSpeed.SpeedKts;
                }

                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(ac.Declination);
                ac.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {FixDisplay(cmd.FixName)}: FH {cmd.MagneticHeading.ToDisplayInt():000}",
            NaturalDescription = $"At {FixDisplay(cmd.FixName)}: fly heading {cmd.MagneticHeading.ToDisplayInt():000}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        return block;
    }

    /// <summary>
    /// Stamps a crossing restriction on <paramref name="cmd"/>'s fix, with the altitude and speed to revert to once the
    /// fix is passed. After a DVIA or CVIA of this same transmission has applied, the crossing is restated with the via
    /// clearance: it takes no reverts and leaves alone the assigned altitude that clearance cleared. Before the via
    /// clearance applies the CFIX takes its reverts like any other, and the via clearance drops them if and when it applies
    /// (<see cref="SupersedeEarlierCrossingRestrictions"/>), so a via clearance dropped or refused first leaves them.
    /// An altitude revert that would reverse the direction of flight is not captured either: the aircraft keeps the crossing
    /// altitude past the fix (<see cref="AltitudeRevertReversesTravel"/>). Every CFIX takes the transmission's crossing stamp;
    /// only one carrying a speed takes its speed stamp, so an altitude-only CFIX leaves the fix's speed, and the
    /// transmission that set it, as they were. A crossing speed the type can fly neither at its present altitude nor at the
    /// crossing altitude is refused before anything changes, as SPD is (<see cref="FlightCommandHandler.RefuseUnflyableSpeed"/>).
    /// </summary>
    internal static CommandResult DispatchCrossFix(CrossFixCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        if (
            (cmd.Speed is { } crossingSpeed)
            && (FlightCommandHandler.RefuseUnflyableSpeed(aircraft, ctx, crossingSpeed, SpeedModifier.None, cmd.Altitude) is { } unflyable)
        )
        {
            return unflyable;
        }

        int transmissionId = ctx.TransmissionId;

        // Capture current altitude/speed for revert after fix passage
        bool restatedWithVia = ViaAppliedInTransmission(aircraft, transmissionId);
        bool revertsAltitude = !restatedWithVia && !AltitudeRevertReversesTravel(aircraft.Altitude, cmd.Altitude, aircraft.Targets.TargetAltitude);
        double? previousAlt = revertsAltitude ? aircraft.Targets.TargetAltitude : null;
        double? previousAssignedAlt = revertsAltitude ? aircraft.Targets.AssignedAltitude : null;
        // A speed restated while SMAX is in force, or while it waits to be armed again past another crossing, reverts to
        // SMAX past the last crossing (PlaceSpeedMaxRevert), not to a figure it flew.
        bool revertsSpeed = (cmd.Speed is not null) && !restatedWithVia;
        bool previousSpeedMax = revertsSpeed && SpeedMaxPending(aircraft);
        double? previousSpeed = (revertsSpeed && !previousSpeedMax) ? aircraft.Targets.TargetSpeed : null;
        double? previousAssignedSpeed = (revertsSpeed && !previousSpeedMax) ? aircraft.Targets.AssignedSpeed : null;

        CifpAltitudeRestriction altRestriction = FlightCommandHandler.ToAltitudeRestriction(cmd.Altitude, cmd.AltType);
        CifpSpeedRestriction? speedRestriction = cmd.Speed is { } spd ? new CifpSpeedRestriction(spd, CifpSpeedRestrictionType.AtOrBelow) : null;

        // Id 0 is no transmission (a queued block restored from a snapshot written before blocks carried one).
        int? crossingTransmissionId = StampedTransmission(transmissionId);

        // CFIX names a fix that is already on the route. Stamp the restriction on that fix in
        // place, preserving the rest of the route (fixes before and after) and any restriction
        // already on another fix — so multiple CFIX commands are additive instead of clobbering
        // one another. If the named fix is not on the route, append it (with its restriction) to
        // the end of the route rather than wiping the route — so a chain of CFIX on a vectored
        // aircraft builds a crossing profile in issue order. An empty route therefore becomes a
        // single-fix direct route.
        NavigationTarget? existing = aircraft.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.AltitudeRestriction = altRestriction;
            existing.RevertAltitude = previousAlt;
            existing.RevertAssignedAltitude = previousAssignedAlt;
            existing.CrossingTransmissionId = crossingTransmissionId;
            if (speedRestriction is not null)
            {
                existing.SpeedRestriction = speedRestriction;
                existing.SpeedTransmissionId = crossingTransmissionId;
                existing.RevertSpeed = previousSpeed;
                existing.RevertAssignedSpeed = previousAssignedSpeed;
                existing.RevertSpeedMax = previousSpeedMax;
            }
        }
        else
        {
            aircraft.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = cmd.FixName,
                    Position = new LatLon(cmd.FixLat, cmd.FixLon),
                    AltitudeRestriction = altRestriction,
                    SpeedRestriction = speedRestriction,
                    RevertAltitude = previousAlt,
                    RevertAssignedAltitude = previousAssignedAlt,
                    RevertSpeed = previousSpeed,
                    RevertAssignedSpeed = previousAssignedSpeed,
                    RevertSpeedMax = previousSpeedMax,
                    CrossingTransmissionId = crossingTransmissionId,
                    SpeedTransmissionId = (speedRestriction is not null) ? crossingTransmissionId : null,
                }
            );
        }

        if (previousSpeedMax)
        {
            PlaceSpeedMaxRevert(aircraft.Targets.NavigationRoute);
        }

        // Let the planner handle altitude on the next tick — don't set TargetAltitude directly.
        // But do set AssignedAltitude for the datablock display, unless this transmission's DVIA or CVIA already applied
        // and cleared it: both orders of "CFIX, DVIA" and of "CFIX, CVIA" then end with no assigned altitude. A cleared
        // altitude that already meets the crossing's bound stays assigned (CrossingBoundMetByClearance).
        if (!restatedWithVia && !CrossingBoundMetByClearance(aircraft, altRestriction))
        {
            aircraft.Targets.AssignedAltitude = cmd.Altitude;
        }

        if (cmd.Speed is { } spdVal)
        {
            aircraft.Targets.AssignedSpeed = spdVal;
            aircraft.Targets.SpeedLimitsLifted = false;
            FlightCommandHandler.EndSpeedMax(aircraft);

            // For acceleration, set TargetSpeed immediately so the aircraft starts
            // accelerating toward the CFIX speed right away. For deceleration,
            // the look-ahead planner (UpdateSpeedPlanning) handles timing.
            if (spdVal > aircraft.IndicatedAirspeed)
            {
                aircraft.Targets.TargetSpeed = spdVal;
            }

            // Restated with a via clearance that has already applied: apply the first constrained fix again, as that
            // clearance does, so "DVIA, CFIX X 110 230" ends where "CFIX X 110 230, DVIA" does.
            if (restatedWithVia)
            {
                ApplyFirstConstrainedFix(aircraft);
            }
        }

        string altTypeStr = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => "at or above",
            CrossFixAltitudeType.AtOrBelow => "at or below",
            _ => "at",
        };
        string cfixMsg = $"Cross {FixDisplay(cmd.FixName)} {altTypeStr} {cmd.Altitude:N0}";
        if (cmd.Speed is not null)
        {
            cfixMsg += $", speed {cmd.Speed}";
        }
        return CommandDispatcher.Ok(cfixMsg);
    }

    /// <summary>
    /// Whether reverting to <paramref name="previousTarget"/> once past a fix crossed at <paramref name="crossAltitude"/>
    /// would reverse the direction the aircraft flies to make the crossing. Going back to an altitude already left needs
    /// a new clearance (7110.65 4-5-7.h.8 NOTE 2; 14 CFR 91.123), and the crossing overrides the earlier altitude
    /// (AIM 4-4-10.g); a revert that continues the same way stands (AIM 4-4-10.e). The direction of travel is from
    /// <paramref name="currentAltitude"/> toward the crossing altitude; an aircraft already at the crossing altitude (within
    /// the altitude snap) has no direction of travel and keeps its revert.
    /// </summary>
    internal static bool AltitudeRevertReversesTravel(double currentAltitude, double crossAltitude, double? previousTarget)
    {
        if (previousTarget is not { } revert)
        {
            return false;
        }

        if (Math.Abs(crossAltitude - currentAltitude) <= FlightPhysics.AltitudeSnapFt)
        {
            return false;
        }

        int travel = Math.Sign(crossAltitude - currentAltitude);
        int revertDirection = Math.Sign(revert - crossAltitude);
        return (travel != 0) && (revertDirection != 0) && (travel != revertDirection);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s cleared altitude (<see cref="ControlTargets.AssignedAltitude"/>) already lies
    /// beyond <paramref name="restriction"/>'s bound: an at-or-above bound below it, an at-or-below bound above it, or a
    /// window it lies strictly inside. Such a bound does not move the aircraft: it keeps flying to the cleared altitude,
    /// which stays assigned (AIM 4-4-10.a; 7110.65 4-5-7). A cleared altitude equal to the bound (as a CFIX that assigned
    /// its own bound leaves it) is not met, and the planner plans the crossing itself. A plain "at" crossing is never met
    /// this way, nor is any bound with no cleared altitude, or under a descend or climb via, which flies to the depicted
    /// altitudes.
    /// </summary>
    internal static bool CrossingBoundMetByClearance(AircraftState aircraft, CifpAltitudeRestriction restriction)
    {
        if (aircraft.Procedure.StarViaMode || aircraft.Procedure.SidViaMode || (aircraft.Targets.AssignedAltitude is not { } cleared))
        {
            return false;
        }

        return restriction.Type switch
        {
            CifpAltitudeRestrictionType.AtOrAbove => cleared > restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.AtOrBelow => cleared < restriction.Altitude1Ft,
            CifpAltitudeRestrictionType.Between => (restriction.Altitude2Ft is { } lower) && (cleared > lower) && (cleared < restriction.Altitude1Ft),
            _ => false,
        };
    }

    internal static CommandResult DispatchListApproaches(ListApproachesCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string airport;
        if (cmd.AirportCode is not null)
        {
            if (!navDb.TryResolveAirport(cmd.AirportCode, out string? canonical))
            {
                return new CommandResult(false, $"Unknown airport {cmd.AirportCode.Trim().ToUpperInvariant()}");
            }
            airport = canonical;
        }
        else
        {
            airport = aircraft.FlightPlan.Destination;
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "No airport specified and no destination in flight plan");
        }

        IReadOnlyList<CifpApproachProcedure> approaches = navDb.GetApproaches(airport);
        if (approaches.Count == 0)
        {
            return CommandDispatcher.Ok($"No approaches found for {airport.ToUpperInvariant()}");
        }

        IOrderedEnumerable<IGrouping<string, CifpApproachProcedure>> grouped = approaches.GroupBy(a => a.Runway ?? "").OrderBy(g => g.Key);

        IEnumerable<string> parts = grouped.Select(g =>
        {
            string items = string.Join(", ", g.Select(a => FormatApproachDisplay(a)));
            return g.Key.Length > 0 ? $"RWY {RunwayIdentifier.ToDisplayDesignator(g.Key)}: {items}" : items;
        });

        return CommandDispatcher.Ok($"{airport.ToUpperInvariant()} approaches: {string.Join(" | ", parts)}");
    }

    private static string FormatApproachDisplay(CifpApproachProcedure approach)
    {
        string typeName = approach.ApproachTypeName;
        int parenIdx = typeName.IndexOf('(');
        if (parenIdx >= 0)
        {
            typeName = typeName[..parenIdx];
        }

        string rwy = approach.Runway ?? "";

        // Extract variant: anything in ApproachId after type code + runway
        string variant = "";
        if (approach.ApproachId.Length > 1 + rwy.Length)
        {
            variant = approach.ApproachId[(1 + rwy.Length)..];
        }

        return $"{typeName}{rwy}{variant}";
    }

    internal static CommandResult DispatchJarr(JoinStarCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;

        // Resolve a controller-typed STAR id that may omit the version digit (JARR TEJAS → TEJAS5).
        if (aircraft.FlightPlan.Destination is { } destination && !string.IsNullOrEmpty(destination))
        {
            cmd = cmd with { StarId = navDb.ResolveCommandStarId(destination, cmd.StarId) };
        }

        // A runway-transition argument (JARR star 27) also designates the landing runway, so the
        // CIFP runway-transition lookup and downstream approach selection use it. Zero-pad it to the
        // same form as RunwayInfo.Designator so a single-digit designator ("1R") matches the CIFP
        // "RW01R" key rather than silently dropping the transition (issue #273).
        if (cmd.RunwayTransition is { } runwayArg)
        {
            aircraft.Procedure.DestinationRunway = RunwayIdentifier.NormalizeDesignator(runwayArg);
        }

        // Try CIFP STAR first for constrained navigation targets
        List<NavigationTarget>? cifpResult = TryResolveStarFromCifp(cmd, aircraft, out ProcedureSource? starSource);
        if (cifpResult is not null)
        {
            aircraft.Targets.NavigationRoute.Clear();
            aircraft.Targets.AssignedMagneticHeading = null;
            foreach (NavigationTarget target in cifpResult)
            {
                aircraft.Targets.NavigationRoute.Add(target);
            }

            aircraft.Procedure.ActiveStarId = cmd.StarId;
            aircraft.Procedure.StarViaMode = false; // STAR via mode OFF by default
            aircraft.Procedure.StarViaTransmissionId = null;
            aircraft.Procedure.LastCrossedPublishedSpeedKts = null;

            string cifpFixList = string.Join(" ", cifpResult.Select(t => FixDisplay(t.Name)));
            return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {cifpFixList}") with
            {
                Advisory = CommandDispatcher.ProcedureSourceAdvisory("STAR", cmd.StarId, starSource),
            };
        }

        // Fallback to NavData body fixes (lateral path only, no constraints)
        IReadOnlyList<string>? starBody = navDb.GetStarBody(cmd.StarId);
        if (starBody is null || starBody.Count == 0)
        {
            return new CommandResult(false, $"Unknown STAR: {cmd.StarId}");
        }

        List<string> routeFixes;

        if (cmd.Transition is not null)
        {
            IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? transitions = navDb.GetStarTransitions(cmd.StarId);
            (string Name, IReadOnlyList<string> Fixes)? match = transitions?.FirstOrDefault(t =>
                t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null && match.Value.Fixes is not null)
            {
                routeFixes = [.. match.Value.Fixes, .. starBody];
            }
            else
            {
                // No transition matched — try joining at an intermediate fix in the body
                int fixIdx = starBody.ToList().FindIndex(f => f.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (fixIdx >= 0)
                {
                    routeFixes = [.. starBody.Skip(fixIdx)];
                }
                else
                {
                    return new CommandResult(false, $"Unknown transition or fix '{cmd.Transition}' for STAR {cmd.StarId}");
                }
            }
        }
        else
        {
            routeFixes = FindStarFixesAhead(aircraft, starBody);
        }

        if (routeFixes.Count == 0)
        {
            return new CommandResult(false, $"No navigable fixes found for STAR {cmd.StarId}");
        }

        // Deduplicate adjacent identical fix names
        var deduped = new List<string>(routeFixes.Count);
        foreach (string name in routeFixes)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                deduped.Add(name);
            }
        }

        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;
        foreach (string fixName in deduped)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes for STAR {cmd.StarId}");
        }

        // Set STAR state even for NavData fallback (allows DVIA later)
        aircraft.Procedure.ActiveStarId = cmd.StarId;
        aircraft.Procedure.StarViaMode = false;
        aircraft.Procedure.StarViaTransmissionId = null;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;

        string fixListStr = string.Join(" ", deduped.Select(FixDisplay));
        return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {fixListStr}");
    }

    /// <summary>
    /// Attempts to resolve a STAR from CIFP data with altitude/speed constraints.
    /// Builds ordered leg sequence: enroute transition → common → runway transition.
    /// Returns null if CIFP data is unavailable or STAR cannot be resolved.
    /// </summary>
    private static List<NavigationTarget>? TryResolveStarFromCifp(JoinStarCommand cmd, AircraftState aircraft, out ProcedureSource? source)
    {
        source = null;
        if (aircraft.FlightPlan.Destination is null)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        CifpStarProcedure? star = navDb.GetStar(aircraft.FlightPlan.Destination, cmd.StarId, out source);
        if (star is null)
        {
            return null;
        }

        // Build ordered leg sequence: enroute transition → common → runway transition
        var orderedLegs = new List<CifpLeg>();

        // Enroute transition (if specified)
        bool transitionMatched = false;
        if (cmd.Transition is not null && star.EnrouteTransitions.TryGetValue(cmd.Transition, out CifpTransition? enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
            transitionMatched = true;
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition: prefer the live AssignedRunway, fall back to the procedure's
        // DestinationRunway. EAPP sets DestinationRunway without touching AssignedRunway, so
        // a JARR issued after EAPP can still pick up the right transition.
        string? rwDesignator = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Procedure.DestinationRunway;
        if (!string.IsNullOrEmpty(rwDesignator))
        {
            CifpTransition? rwTransition = LookupRunwayTransition(star.RunwayTransitions, rwDesignator);
            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Convert legs to NavigationTargets with constraints
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);

        // If transition was specified but didn't match, try joining at an intermediate fix
        if (cmd.Transition is not null && !transitionMatched)
        {
            int fixIdx = targets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (fixIdx >= 0)
            {
                return targets.GetRange(fixIdx, targets.Count - fixIdx);
            }

            // Check each enroute transition for the fix
            foreach ((string _, CifpTransition? trans) in star.EnrouteTransitions)
            {
                List<NavigationTarget> transTargets = DepartureClearanceHandler.ResolveLegsToTargets(trans.Legs);
                int transFixIdx = transTargets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (transFixIdx >= 0)
                {
                    List<NavigationTarget> result = transTargets.GetRange(transFixIdx, transTargets.Count - transFixIdx);
                    result.AddRange(targets);
                    return result;
                }
            }

            // Fix not found in CIFP data — fall through to NavData
            return null;
        }

        // Filter to fixes ahead of aircraft (same logic as NavData fallback)
        if (cmd.Transition is null && targets.Count > 1)
        {
            targets = FindTargetsAhead(aircraft, targets);
        }

        return targets.Count > 0 ? targets : null;
    }

    /// <summary>
    /// Resolves a CIFP STAR/SID runway-transition entry by runway designator. Handles the
    /// "B" wildcard convention (e.g. <c>RW01B</c> covers both L and R parallels).
    /// </summary>
    internal static CifpTransition? LookupRunwayTransition(IReadOnlyDictionary<string, CifpTransition> transitions, string runwayDesignator)
    {
        // CIFP runway-transition keys are zero-padded ("RW01R"); a controller-typed single-digit
        // designator ("1R") must be padded before the lookup or it silently misses the transition.
        string designator = RunwayIdentifier.NormalizeDesignator(runwayDesignator);
        string rwKey = "RW" + designator;
        if (transitions.TryGetValue(rwKey, out CifpTransition? rwTransition))
        {
            return rwTransition;
        }
        string bothKey = "RW" + designator.TrimEnd('L', 'R', 'C') + "B";
        return transitions.TryGetValue(bothKey, out CifpTransition? bothTransition) ? bothTransition : null;
    }

    /// <summary>
    /// Fix names that appear only on runway transitions other than <paramref name="selectedTransition"/>.
    /// </summary>
    internal static HashSet<string> CollectExclusiveRunwayTransitionFixes(CifpStarProcedure star, CifpTransition selectedTransition)
    {
        var selectedFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CifpLeg leg in selectedTransition.Legs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier))
            {
                selectedFixes.Add(leg.FixIdentifier);
            }
        }

        var exclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CifpTransition otherTransition in star.RunwayTransitions.Values)
        {
            if (ReferenceEquals(otherTransition, selectedTransition))
            {
                continue;
            }

            foreach (CifpLeg leg in otherTransition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && !selectedFixes.Contains(leg.FixIdentifier))
                {
                    exclusive.Add(leg.FixIdentifier);
                }
            }
        }

        return exclusive;
    }

    /// <summary>
    /// Removes nav-route targets that belong exclusively to non-selected STAR runway transitions.
    /// </summary>
    internal static void RemoveStaleStarRunwayTransitionFixes(AircraftState aircraft, CifpStarProcedure star, CifpTransition selectedTransition)
    {
        HashSet<string> stale = CollectExclusiveRunwayTransitionFixes(star, selectedTransition);
        if (stale.Count > 0)
        {
            aircraft.Targets.NavigationRoute.RemoveAll(t => stale.Contains(t.Name));
        }
    }

    /// <summary>
    /// Sets <see cref="AircraftProcedure.DestinationRunway"/> and extends the active STAR route
    /// for that runway (scrubbing fixes from other runway transitions first).
    /// </summary>
    internal static void SyncDestinationRunwayWithActiveStar(AircraftState aircraft, string runwayDesignator)
    {
        aircraft.Procedure.DestinationRunway = runwayDesignator;
        ExtendActiveStarWithRunwayTransition(aircraft, runwayDesignator);
    }

    /// <summary>
    /// Extends an aircraft's active NavigationRoute with the runway-transition fixes for the
    /// given <paramref name="runwayDesignator"/> on its active STAR. No-op when the aircraft
    /// has no active STAR, no destination airport, or the STAR has no transition for the
    /// runway. Skips fixes already present in the route to avoid duplicates when the route
    /// already contains some or all of the transition.
    /// </summary>
    internal static void ExtendActiveStarWithRunwayTransition(AircraftState aircraft, string runwayDesignator)
    {
        if (string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId) || string.IsNullOrEmpty(aircraft.FlightPlan.Destination))
        {
            return;
        }

        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, aircraft.Procedure.ActiveStarId);
        if (star is null)
        {
            return;
        }

        CifpTransition? transition = LookupRunwayTransition(star.RunwayTransitions, runwayDesignator);
        if (transition is null || transition.Legs.Count == 0)
        {
            return;
        }

        RemoveStaleStarRunwayTransitionFixes(aircraft, star, transition);

        var present = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var newLegs = transition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier) && !present.Contains(l.FixIdentifier)).ToList();
        if (newLegs.Count == 0)
        {
            return;
        }

        List<NavigationTarget> newTargets = DepartureClearanceHandler.ResolveLegsToTargets(newLegs);
        foreach (NavigationTarget t in newTargets)
        {
            aircraft.Targets.NavigationRoute.Add(t);
        }
    }

    /// <summary>
    /// Filters NavigationTargets to those ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such target.
    /// </summary>
    private static List<NavigationTarget> FindTargetsAhead(AircraftState aircraft, List<NavigationTarget> targets)
    {
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            double bearing = GeoMath.BearingTo(aircraft.Position, targets[i].Position);
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, targets[i].Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            return targets;
        }

        return targets.GetRange(bestIdx, targets.Count - bestIdx);
    }

    /// <summary>
    /// Find the subset of STAR body fixes ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such fix. Prevents U-turns to fixes behind the aircraft.
    /// </summary>
    private static List<string> FindStarFixesAhead(AircraftState aircraft, IReadOnlyList<string> bodyFixes)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < bodyFixes.Count; i++)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(bodyFixes[i]);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            // No fixes ahead — use first fix as fallback
            return [.. bodyFixes];
        }

        return [.. bodyFixes.Skip(bestIdx)];
    }

    internal static CommandResult DispatchJawy(JoinAirwayCommand cmd, AircraftState aircraft)
    {
        CommandResult planned = PlanAirwayIntercept(aircraft, cmd, out AirwayInterceptPlan plan);
        if (!planned.Success)
        {
            return planned;
        }

        // Block 0 (immediate): fly present heading (to allow intercept)
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on segment intercept, navigate the airway fix sequence
        aircraft.Queue.Blocks.Add(BuildAirwayInterceptBlock(cmd, plan));

        return planned;
    }

    /// <summary>
    /// The bracketing segment a JAWY intercept flies: the trigger's origin and radial, and the airway fix
    /// sequence to navigate once the segment is intercepted. A restored block's plan is rebuilt from the block's
    /// saved trigger, not from the aircraft's restored position.
    /// </summary>
    internal readonly record struct AirwayInterceptPlan(
        string InterceptFixName,
        double InterceptFixLat,
        double InterceptFixLon,
        int SegmentRadial,
        List<NavigationTarget> NavTargets,
        List<string> RemainingFixes
    );

    /// <summary>
    /// The block a JAWY leaves queued behind itself: on intercepting the segment, navigate the remaining fixes.
    /// Built here for the same reason as <see cref="BuildJradoInterceptBlock"/>.
    /// </summary>
    internal static CommandBlock BuildAirwayInterceptBlock(JoinAirwayCommand cmd, AirwayInterceptPlan plan)
    {
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = plan.InterceptFixName,
                FixLat = plan.InterceptFixLat,
                FixLon = plan.InterceptFixLon,
                TrueRadial = plan.SegmentRadial,
            },
            ApplyAction = (ac, _) =>
            {
                ac.Targets.AssignedMagneticHeading = null;
                ac.Targets.NavigationRoute.Clear();
                foreach (NavigationTarget target in plan.NavTargets)
                {
                    ac.Targets.NavigationRoute.Add(target);
                }
                return new CommandResult(true);
            },
            Description = $"intercept {cmd.AirwayId}: DCT {string.Join(" ", plan.RemainingFixes.Select(FixDisplay))}",
            NaturalDescription = $"On {cmd.AirwayId}: proceed via {string.Join(" ", plan.RemainingFixes.Select(FixDisplay))}",
            ParsedCommands = [cmd],
            SourceCommandText = CommandDescriber.DescribeCommand(cmd),
        };
        block.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        return block;
    }

    /// <summary>Plans a JAWY intercept, or returns the refusal the dispatch hands straight back.</summary>
    private static CommandResult PlanAirwayIntercept(AircraftState aircraft, JoinAirwayCommand cmd, out AirwayInterceptPlan plan)
    {
        plan = default;
        NavigationDatabase navDb = NavigationDatabase.Instance;
        IReadOnlyList<string>? airwayFixes = navDb.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            return new CommandResult(false, $"Unknown airway: {cmd.AirwayId}");
        }

        // Find the bracketing segment: the fix behind and fix ahead of the aircraft
        (int behindIdx, int aheadIdx) = AirwayBracketing.Find(navDb, aircraft.Position, aircraft.TrueHeading, airwayFixes);
        if (aheadIdx < 0)
        {
            return new CommandResult(false, $"No navigable segment found on {cmd.AirwayId}");
        }

        // Resolve ahead fix position (guaranteed non-null since AirwayBracketing.Find validated it)
        (double Lat, double Lon) aheadPos = navDb.GetFixPosition(airwayFixes[aheadIdx])!.Value;

        // Determine the segment course to intercept
        double segmentCourse;
        double interceptFixLat;
        double interceptFixLon;
        string interceptFixName;

        if (behindIdx >= 0)
        {
            (double Lat, double Lon) behindPos = navDb.GetFixPosition(airwayFixes[behindIdx])!.Value;
            segmentCourse = GeoMath.BearingTo(behindPos.Lat, behindPos.Lon, aheadPos.Lat, aheadPos.Lon);
            // Use behind fix as the radial origin — aircraft intercepts the radial FROM behind fix TO ahead fix
            interceptFixLat = behindPos.Lat;
            interceptFixLon = behindPos.Lon;
            interceptFixName = airwayFixes[behindIdx];
        }
        else
        {
            // No fix behind — aircraft is before the first fix. Direct to first fix.
            segmentCourse = GeoMath.BearingTo(aircraft.Position, new LatLon(aheadPos.Lat, aheadPos.Lon));
            interceptFixLat = aheadPos.Lat;
            interceptFixLon = aheadPos.Lon;
            interceptFixName = airwayFixes[aheadIdx];
        }

        int segmentRadial = (int)Math.Round(segmentCourse) % 360;

        // Determine direction of travel along the airway (forward or reverse)
        bool reversed = behindIdx >= 0 && behindIdx > aheadIdx;

        // Build remaining fix list from ahead fix onward (in the direction of travel)
        List<string> remainingFixes = FixesFrom(airwayFixes, aheadIdx, forward: !reversed);

        // Build NavigationTargets for the remaining fixes
        List<NavigationTarget> navTargets = ResolveFixTargets(remainingFixes);

        if (navTargets.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes on {cmd.AirwayId}");
        }

        plan = new AirwayInterceptPlan(interceptFixName, interceptFixLat, interceptFixLon, segmentRadial, navTargets, remainingFixes);

        string fixListStr = string.Join(" ", remainingFixes.Select(FixDisplay));
        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.AirwayId}: {fixListStr}");
    }

    /// <summary>
    /// The candidate fix sequences a restored JAWY block could have been planned with, given the trigger it was
    /// saved with. The sequence is rebuilt from that trigger and never re-planned against the aircraft's position:
    /// <see cref="AirwayBracketing.Find"/> can pick a different fix once earlier fixes pass abeam, and finds no
    /// segment at all once the aircraft is past the airway's last fix ahead.
    ///
    /// The trigger names the segment origin, which is either the fix behind the aircraft — the far end is then a
    /// neighbour of it — or, when the live plan found no fix behind, the first fix still to fly, with the whole
    /// forward sequence from it. The trigger alone cannot say which: its radial is a segment course in the first
    /// case and the bearing from the aircraft to the origin in the second, and an aircraft on the airway's extended
    /// centreline makes the two agree. So all three sequences are offered, and the caller keeps the one whose
    /// rebuilt description matches the block's saved one.
    /// </summary>
    internal static IEnumerable<AirwayInterceptPlan> PlanAirwayInterceptsFromTrigger(JoinAirwayCommand cmd, BlockTrigger trigger)
    {
        if (trigger.FixName is not { } originName || trigger.FixLat is not { } originLat || trigger.FixLon is not { } originLon)
        {
            yield break;
        }

        IReadOnlyList<string>? airwayFixes = NavigationDatabase.Instance.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            yield break;
        }

        int originIdx = IndexOfFix(airwayFixes, originName);
        if (originIdx < 0)
        {
            yield break;
        }

        foreach (List<string> remainingFixes in RemainingFixCandidates(airwayFixes, originIdx))
        {
            List<NavigationTarget> navTargets = ResolveFixTargets(remainingFixes);
            if (navTargets.Count == 0)
            {
                continue;
            }

            // The serialized trigger is never re-derived, so the plan carries its own origin and radial only to keep
            // the plan's shape uniform with the live one's.
            yield return new AirwayInterceptPlan(originName, originLat, originLon, trigger.TrueRadial ?? 0, navTargets, remainingFixes);
        }
    }

    /// <summary>
    /// The sequences a trigger's origin can start: forward from the neighbour after it, backward from the neighbour
    /// before it, and forward from the origin itself when it is the first fix still to fly.
    /// </summary>
    private static IEnumerable<List<string>> RemainingFixCandidates(IReadOnlyList<string> airwayFixes, int originIdx)
    {
        if (originIdx + 1 < airwayFixes.Count)
        {
            yield return FixesFrom(airwayFixes, originIdx + 1, forward: true);
        }

        if (originIdx > 0)
        {
            yield return FixesFrom(airwayFixes, originIdx - 1, forward: false);
        }

        yield return FixesFrom(airwayFixes, originIdx, forward: true);
    }

    /// <summary>The airway fix list from <paramref name="startIdx"/> to its end (or to its start, walking back).</summary>
    private static List<string> FixesFrom(IReadOnlyList<string> airwayFixes, int startIdx, bool forward)
    {
        var fixes = new List<string>();
        if (forward)
        {
            for (int i = startIdx; i < airwayFixes.Count; i++)
            {
                fixes.Add(airwayFixes[i]);
            }
        }
        else
        {
            for (int i = startIdx; i >= 0; i--)
            {
                fixes.Add(airwayFixes[i]);
            }
        }

        return fixes;
    }

    private static int IndexOfFix(IReadOnlyList<string> airwayFixes, string fixName)
    {
        for (int i = 0; i < airwayFixes.Count; i++)
        {
            if (string.Equals(airwayFixes[i], fixName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<NavigationTarget> ResolveFixTargets(IReadOnlyList<string> fixNames)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        var targets = new List<NavigationTarget>();
        foreach (string fixName in fixNames)
        {
            (double Lat, double Lon)? pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                targets.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        return targets;
    }

    /// <summary>The altitude at and below which a hold's leg is 1 minute, in feet MSL (AIM 5-3-8(j)(4)(a)).</summary>
    internal const double StandardHoldLegThresholdFt = 14000;

    /// <summary>The leg of an altitude-standard hold at or below <see cref="StandardHoldLegThresholdFt"/>, in minutes.</summary>
    internal const double StandardHoldLegMinutesAtOrBelow = 1.0;

    /// <summary>The leg of an altitude-standard hold above <see cref="StandardHoldLegThresholdFt"/>, in minutes.</summary>
    internal const double StandardHoldLegMinutesAbove = 1.5;

    /// <summary>The leg length an omitted <c>HOLDP</c> leg flies at <paramref name="altitudeFt"/> MSL.</summary>
    internal static double StandardHoldLegMinutes(double altitudeFt) =>
        altitudeFt <= StandardHoldLegThresholdFt ? StandardHoldLegMinutesAtOrBelow : StandardHoldLegMinutesAbove;

    internal static CommandResult DispatchHoldingPattern(HoldingPatternCommand cmd, AircraftState aircraft)
    {
        // An omitted leg is the altitude standard, fixed when the hold is issued: the assigned altitude, else the present one.
        double legLength = cmd.LegLength ?? StandardHoldLegMinutes(aircraft.Targets.AssignedAltitude ?? aircraft.Altitude);
        bool isMinuteBased = (cmd.LegLength is null) || cmd.IsMinuteBased;

        // The controller's holding course is magnetic (7110.65 1-2-1): referenced to the navaid's variation of record when the fix
        // is a navaid, else to the modelled variation at the fix. The phase flies a true course; the readback keeps the magnetic one.
        double declination = NavigationDatabase.Instance.GetPublishedCourseDeclination(cmd.FixName, string.Empty, new LatLon(cmd.FixLat, cmd.FixLon));
        TrueHeading inboundTrue = new MagneticHeading(cmd.InboundCourse).ToTrue(declination);

        var phase = new HoldingPatternPhase
        {
            FixName = cmd.FixName,
            FixLat = cmd.FixLat,
            FixLon = cmd.FixLon,
            InboundCourse = (int)Math.Round(inboundTrue.Degrees) % 360,
            LegLength = legLength,
            IsMinuteBased = isMinuteBased,
            Direction = cmd.Direction,
            Entry = cmd.Entry,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = runway is not null ? new PhaseList { AssignedRunway = runway } : new PhaseList();
        aircraft.Phases.Add(phase);

        PhaseContext startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        string dirStr = cmd.Direction == TurnDirection.Left ? "left" : "right";
        string legStr = $"{legLength.ToString(CultureInfo.InvariantCulture)}{(isMinuteBased ? "min" : "nm")}";
        return CommandDispatcher.Ok($"Hold at {FixDisplay(cmd.FixName)}, {cmd.InboundCourse:D3} inbound, {dirStr} turns, {legStr} legs");
    }

    internal static CommandResult DispatchJfac(JoinFinalApproachCourseCommand cmd, AircraftState aircraft)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string airport = CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        // Auto-resolve: when no approach ID given, try ExpectedApproach first, then assigned runway
        string? approachId = cmd.ApproachId;
        if (approachId is null)
        {
            // A visual approach has no published final approach course to join.
            if (ApproachIds.TryParseVisual(aircraft.Approach.Expected, out _))
            {
                return new CommandResult(false, "Unable, expecting a visual approach; clear it with CVA");
            }

            approachId = aircraft.Approach.Expected ?? aircraft.Procedure.DestinationRunway ?? aircraft.Procedure.DepartureRunway;
            if (approachId is null)
            {
                return new CommandResult(false, "No approach ID and no runway assigned — cannot auto-resolve");
            }
        }

        string? resolvedId = navDb.ResolveApproachId(airport, approachId);
        if (resolvedId is null)
        {
            return new CommandResult(false, $"Unknown approach: {approachId} at {airport}");
        }

        CifpApproachProcedure? procedure = navDb.GetApproach(airport, resolvedId);
        if (procedure?.Runway is null)
        {
            return new CommandResult(false, $"No runway for approach {resolvedId}");
        }

        RunwayInfo? runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")} at {airport}");
        }

        // Ensure the runway designator matches the approach runway
        RunwayInfo approachRunway = runway.IsActiveEnd(procedure.Runway) ? runway : runway.ForApproach(procedure.Runway);

        FinalApproachCourseResult facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, navDb);
        TrueHeading finalCourse = facResult.Course;

        // JFAC/JLOC is a lateral "join the localizer" vector, not an approach clearance: it does
        // NOT cancel a previously assigned speed (7110.65 §5-7-4 / §5-7-1.d only on approach
        // clearance, resume-normal, or descend-via). Keep the assigned speed and any STAR
        // crossing-speed ceiling — the aircraft holds them through the intercept until CAPP.

        // The vector the join was appended to ("FH 220, JLOC") is the angle the intercept is judged
        // against, so it is read before the join drops it.
        MagneticHeading? interceptHeading = aircraft.Targets.AssignedMagneticHeading;

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.NavigationRoute.Clear();

        // Clear existing phases
        if (aircraft.Phases is not null)
        {
            PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        ApproachCommandHandler.ClearPendingApproach(aircraft);

        // Build phase sequence: InterceptCourse → FinalApproach → Landing.
        // RelaxedJoin: a "join the localizer/FAC" instruction (often appended to a vector,
        // e.g. "FH 220, JLOC") arms the join — the aircraft keeps flying its assigned heading
        // and turns onto the localizer when it intercepts, at any cut, never busting through
        // (7110.65 §5-9-2 limits the controller's vector, not this relaxed join). Kept distinct
        // from ForcedIntercept so the glideslope gate stays scoped to PTACF: the lateral-only
        // hold means no descent until CAPP.
        // The intercept's threshold is the landing threshold: its distance is scored against the
        // approach gate, which the P/CG measures from there.
        LatLon interceptThreshold = LandingThreshold.Resolve(approachRunway, aircraft.Ground.Layout);
        var interceptPhase = new InterceptCoursePhase
        {
            FinalApproachCourse = finalCourse,
            ThresholdLat = interceptThreshold.Lat,
            ThresholdLon = interceptThreshold.Lon,
            ApproachId = resolvedId,
            AssignedInterceptHeading = interceptHeading,
            RelaxedJoin = true,
        };

        var finalPhase = new FinalApproachPhase();
        bool isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landingPhase = isHeliApch ? new HelicopterLandingPhase() : new LandingPhase();

        var clearance = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = resolvedId,
            AirportCode = airport,
            RunwayId = procedure.Runway,
            FinalApproachCourse = finalCourse,
            FinalApproachAnchorLat = facResult.AnchorLat,
            FinalApproachAnchorLon = facResult.AnchorLon,
            Procedure = procedure,
            MapAltitudeFt = ApproachCommandHandler.ExtractMapAltitude(procedure),
            MapDistanceNm = ApproachCommandHandler.ExtractMapDistance(procedure, approachRunway),
            // Lateral intercept only: hold altitude on the final approach course until CAPP.
            LateralInterceptOnly = true,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(interceptPhase);
        aircraft.Phases.Add(finalPhase);
        aircraft.Phases.Add(landingPhase);

        PhaseContext startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        return CommandDispatcher.Ok(
            $"Join final approach course, {resolvedId}, runway {RunwayIdentifier.ToDisplayDesignator(procedure.Runway ?? "")}"
        );
    }

    /// <summary>
    /// Whether a DVIA or CVIA of <paramref name="transmissionId"/> has applied and is still in force. Never true for id
    /// 0, which is no transmission.
    /// </summary>
    private static bool ViaAppliedInTransmission(AircraftState aircraft, int transmissionId) =>
        (transmissionId != 0)
        && ((aircraft.Procedure.StarViaTransmissionId == transmissionId) || (aircraft.Procedure.ClimbViaTransmissionId == transmissionId));

    /// <summary>The id to stamp for <paramref name="transmissionId"/>: none for id 0, which is no transmission.</summary>
    private static int? StampedTransmission(int transmissionId) => (transmissionId == 0) ? null : transmissionId;

    internal static CommandResult DispatchClimbVia(ClimbViaCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        // "Climb via the [SID]" only requires the SID to be in the cleared route (AIM 5-2-9). If no SID
        // is active yet — e.g. the aircraft was given runway heading (CTO RH) or vectored off and then
        // re-cleared DCT to a downstream SID fix — activate the one in the filed route and overlay its
        // published crossing restrictions onto the live lateral route.
        if ((aircraft.Procedure.ActiveSidId is null) && !TryActivateFiledSid(aircraft, ctx.TransmissionId))
        {
            return new CommandResult(false, "No active SID — climb via requires a SID in the filed route");
        }

        SupersedeEarlierCrossingRestrictions(aircraft, ctx.TransmissionId, ActiveSidRestrictions);
        CancelEarlierAssignedSpeed(aircraft, ctx.SpeedAssignedInBlock, ctx.TransmissionId);
        (int target, int? exceptMaintain) = ResolveClimbViaTarget(cmd, aircraft, ctx.ArtccConfig);
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.SidViaCeiling = exceptMaintain;
        aircraft.Procedure.ClimbViaTargetFt = target;
        aircraft.Procedure.ClimbViaTransmissionId = StampedTransmission(ctx.TransmissionId);
        // A bare climb via cancels an interim altitude assigned before it and climbs to the SID's top altitude
        // (7110.65 4-5-7.h.10 Example 1, h.11 NOTE 1; AIM 5-2-9.h.8).
        aircraft.Targets.AssignedAltitude = null;
        aircraft.Targets.TargetAltitude = FlightPhysics.ClimbViaAltitude(aircraft);
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Climb via SID, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Climb via SID");
    }

    /// <summary>
    /// The altitude a climb via climbs to, and the ATC-assigned "except maintain" altitude behind it, if any:
    /// <list type="number">
    /// <item><c>CVIA nnn</c>'s altitude, an "except maintain";</item>
    /// <item>else the SID's TDLS top for the departure airport and filed transition
    /// (<see cref="ArtccConfigResolver.GetSidTdlsTopAltitude"/>): the climb-via text's "EXC MAINT" altitude, an "except
    /// maintain" (AIM 5-2-9.h.9), or the transition's initial altitude; a TDLS value is never raised by a published
    /// altitude, floors included;</item>
    /// <item>else the highest altitude the SID publishes on its flown legs, a stand-in for the charted top altitude, which
    /// CIFP does not code;</item>
    /// <item>else present altitude, with a warning.</item>
    /// </list>
    /// Never below present altitude rounded up to 100 ft, since a via clearance never commands a descent (a descent takes
    /// DM). The TDLS initial-altitude menu and the <see cref="AircraftProcedure.SidInitialAltitudeFt"/> CTO stored from it
    /// never take part.
    /// </summary>
    private static (int TargetFt, int? ExceptMaintainFt) ResolveClimbViaTarget(
        ClimbViaCommand cmd,
        AircraftState aircraft,
        ArtccConfigRoot? artccConfig
    )
    {
        int floor = (int)(Math.Ceiling(Math.Round(aircraft.Altitude) / 100.0) * 100);
        if (cmd.Altitude is { } assigned)
        {
            return (Math.Max(assigned, floor), assigned);
        }

        string departure = aircraft.FlightPlan.Departure ?? "";
        if (artccConfig.GetSidTdlsTopAltitude(departure, aircraft.Procedure.ActiveSidId, FiledSidTransition(aircraft)) is { } tdls)
        {
            return (Math.Max(tdls.AltitudeFt, floor), tdls.IsExceptMaintain ? tdls.AltitudeFt : null);
        }

        if (HighestPublishedSidAltitudeFt(aircraft) is { } published)
        {
            return (Math.Max(published, floor), null);
        }

        Log.LogWarning(
            "[ClimbVia] {Callsign}: no top altitude for {Sid} at {Departure} (no TDLS value, no published altitude); holding {Floor}",
            aircraft.Callsign,
            aircraft.Procedure.ActiveSidId,
            departure,
            floor
        );
        return (floor, null);
    }

    /// <summary>The filed route's tokens, split at spaces and at the dots of a "SID.TRANSITION" form.</summary>
    private static string[] FiledRouteTokens(AircraftState aircraft) =>
        (aircraft.FlightPlan.Route ?? "").Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The active SID's filed enroute transition: the filed route's token after the SID's; null when there is none.</summary>
    private static string? FiledSidTransition(AircraftState aircraft)
    {
        string[] tokens = FiledRouteTokens(aircraft);
        int sid = Array.FindIndex(tokens, t => t.Equals(aircraft.Procedure.ActiveSidId, StringComparison.OrdinalIgnoreCase));
        return ((sid >= 0) && (sid + 1 < tokens.Length)) ? tokens[sid + 1] : null;
    }

    /// <summary>
    /// The largest altitude the active SID publishes on the legs <paramref name="aircraft"/> flies: the upper bound of a
    /// between restriction, the value of every other kind. Null when the SID cannot be resolved or publishes none.
    /// </summary>
    private static int? HighestPublishedSidAltitudeFt(AircraftState aircraft)
    {
        if (ResolveActiveSid(aircraft) is not { } sid)
        {
            return null;
        }

        int? highest = null;
        foreach (CifpAltitudeRestriction restriction in FlownSidLegs(sid, aircraft).Select(l => l.Altitude).OfType<CifpAltitudeRestriction>())
        {
            int legHighest =
                restriction.Type == CifpAltitudeRestrictionType.Between
                    ? Math.Max(restriction.Altitude1Ft, restriction.Altitude2Ft ?? 0)
                    : restriction.Altitude1Ft;
            highest = Math.Max(highest ?? 0, legHighest);
        }

        return highest;
    }

    private static CifpSidProcedure? ResolveActiveSid(AircraftState aircraft)
    {
        string? sidId = aircraft.Procedure.ActiveSidId;
        string? departure = aircraft.FlightPlan.Departure;
        return ((sidId is null) || string.IsNullOrEmpty(departure)) ? null : NavigationDatabase.Instance.GetSid(departure, sidId);
    }

    internal static CommandResult DispatchDescendVia(DescendViaCommand cmd, AircraftState aircraft, int transmissionId, bool speedAssignedInBlock)
    {
        // DVIA SPD <speed> <fix> names a fix on the route; refuse one off it before anything changes.
        NavigationTarget? speedFix = null;
        if (cmd.Speed is not null && cmd.SpeedFixName is not null && cmd.SpeedFixLat is not null && cmd.SpeedFixLon is not null)
        {
            speedFix = aircraft.Targets.NavigationRoute.Find(f => string.Equals(f.Name, cmd.SpeedFixName, StringComparison.OrdinalIgnoreCase));
            if (speedFix is null)
            {
                return new CommandResult(false, $"Fix {FixDisplay(cmd.SpeedFixName)} not found in current route");
            }
        }

        // "Descend via the [STAR]" only requires the STAR to be in the cleared route (AIM 5-4-1.a.2);
        // no separate join step is needed. If no STAR is active yet, activate the one in the filed
        // route and overlay its published crossing restrictions onto the live lateral route.
        if ((aircraft.Procedure.ActiveStarId is null) && !TryActivateFiledStar(aircraft, transmissionId))
        {
            return new CommandResult(false, "No active STAR — descend via requires a STAR in the filed route");
        }

        SupersedeEarlierCrossingRestrictions(aircraft, transmissionId, ActiveStarRestrictions);
        CancelEarlierAssignedSpeed(aircraft, speedAssignedInBlock, transmissionId);
        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaTransmissionId = StampedTransmission(transmissionId);
        aircraft.Procedure.StarViaFloor = cmd.Altitude;
        // A descend via ends any climb via still in force, so the STAR's restrictions drive the altitude.
        aircraft.Procedure.ClimbViaTargetFt = null;
        aircraft.Procedure.ClimbViaTransmissionId = null;
        aircraft.Procedure.SidViaCeiling = null;
        // The via clearance supersedes any altitude assigned before it (converse of 7110.65 4-5-7.h.11 NOTE 1).
        aircraft.Targets.AssignedAltitude = null;
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);

        // DVIA SPD <speed> <fix>: set the speed on the fix in place, so everything else on it survives. It is the
        // controller's speed, not a published one, so it takes this transmission's speed stamp as a CFIX speed does: a later
        // via or approach clearance gives the fix its published speed back, and crossing it is not remembered as a published
        // speed. It sets no altitude, so the fix's crossing stamp stays as it is.
        if ((speedFix is not null) && (cmd.Speed is { } speed))
        {
            speedFix.SpeedRestriction = new CifpSpeedRestriction(speed, CifpSpeedRestrictionType.AtOrBelow);
            speedFix.SpeedTransmissionId = StampedTransmission(transmissionId);

            return CommandDispatcher.Ok($"Descend via STAR, {speed} knots at {FixDisplay(speedFix.Name)}");
        }

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Descend via STAR, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Descend via STAR");
    }

    /// <summary>
    /// Applies constraints from the first fix in the nav route that has an altitude
    /// or speed restriction. Called immediately when CVIA/DVIA is issued so the
    /// aircraft starts working toward the first constraint right away, rather than
    /// waiting to sequence through unconstrained fixes.
    /// </summary>
    private static void ApplyFirstConstrainedFix(AircraftState aircraft)
    {
        foreach (NavigationTarget target in aircraft.Targets.NavigationRoute)
        {
            if (target.AltitudeRestriction is not null || target.SpeedRestriction is not null)
            {
                FlightPhysics.ApplyFixConstraints(aircraft, target);
                break;
            }
        }
    }

    /// <summary>
    /// Activates the STAR named in the aircraft's filed route when none is active yet, so a bare
    /// <c>DVIA</c> works without a prior <c>JARR</c> (the STAR being in the cleared route is the only
    /// real-world precondition). Sets <see cref="AircraftProcedure.ActiveStarId"/> and overlays the
    /// published CIFP altitude/speed restrictions onto the existing (already-sequenced) lateral route
    /// by fix name — it does not rebuild the route, so fixes the aircraft has already passed are not
    /// re-added. Returns false when the filed route contains no resolvable STAR.
    /// </summary>
    private static bool TryActivateFiledStar(AircraftState aircraft, int transmissionId)
    {
        if (FiledProcedureLookup.FindStar(NavigationDatabase.Instance, aircraft.FlightPlan.Destination, aircraft.FlightPlan.Route) is not { } filed)
        {
            return false;
        }

        OverlayStarRestrictions(aircraft, filed.Procedure, transmissionId);
        aircraft.Procedure.ActiveStarId = filed.Id;
        return true;
    }

    /// <summary>
    /// Copies each published altitude/speed restriction from <paramref name="star"/> onto the matching
    /// fix (by name) already present in the aircraft's navigation route, reusing the same leg→target
    /// resolution as <c>JARR</c> so the constraint semantics are identical (<see cref="OverlayPublishedRestrictions"/>).
    /// </summary>
    private static void OverlayStarRestrictions(AircraftState aircraft, CifpStarProcedure star, int transmissionId) =>
        OverlayPublishedRestrictions(aircraft, PublishedStarRestrictions(star, aircraft), transmissionId);

    /// <summary>
    /// Copies <paramref name="byName"/>'s published restrictions onto the route fixes of the same name, each axis on its
    /// own. A crossing altitude a CFIX in this same transmission stamped stays, and so does a speed this transmission set:
    /// the controller restated them with the via clearance. The other axis takes the published restriction, so an
    /// altitude-only CFIX still gets the fix's published speed.
    /// </summary>
    private static void OverlayPublishedRestrictions(AircraftState aircraft, Dictionary<string, NavigationTarget> byName, int transmissionId)
    {
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (!byName.TryGetValue(fix.Name, out NavigationTarget? source))
            {
                continue;
            }

            if (!IsStampedBy(fix, transmissionId))
            {
                fix.AltitudeRestriction = source.AltitudeRestriction;
            }

            if (!IsSpeedStampedBy(fix, transmissionId))
            {
                fix.SpeedRestriction = source.SpeedRestriction;
            }
        }
    }

    /// <summary>
    /// A via clearance (descend via or climb via) supersedes a crossing restriction issued before it and not restated
    /// with it (7110.65 4-5-7.h.9, h.11 NOTES 1-2; AIM 5-2-9.h.8 Example 8). Every route fix loses its pending reverts
    /// (altitude, assigned altitude, speed, assigned speed), so passing it can no longer restore an altitude or speed
    /// assigned before the via clearance. Altitude and speed are judged each on its own stamp. A crossing altitude a CFIX or
    /// constrained direct-to of this same transmission stamped (<see cref="NavigationTarget.CrossingTransmissionId"/>)
    /// stays, and every other altitude a controller's crossing set gets the procedure's published restriction back. The
    /// clearance cancels only the speeds ATC assigned before it (7110.65 5-7-1.d), and a pilot keeps a published speed
    /// (AIM 5-4-1 NOTE), so a fix's speed is reset only when an earlier transmission set it
    /// (<see cref="NavigationTarget.SpeedTransmissionId"/>), when it carries a speed revert, or when the via procedure
    /// publishes the fix; one whose speed this same transmission set keeps it, and every other fix keeps its speed, as an
    /// approach's appended fixes, a STAR's fixes on a climb via route, and every fix of a procedure that cannot be resolved
    /// do. A reset fix takes the pending or active approach clearance's published speed for it, else the via procedure's,
    /// else none. The via procedure's restrictions are <paramref name="publishedRestrictions"/>'s (the active STAR's for a
    /// DVIA, the active SID's for a CVIA).
    /// </summary>
    private static void SupersedeEarlierCrossingRestrictions(
        AircraftState aircraft,
        int transmissionId,
        Func<AircraftState, Dictionary<string, NavigationTarget>> publishedRestrictions
    )
    {
        var published = new Lazy<Dictionary<string, NavigationTarget>>(() => publishedRestrictions(aircraft));
        var approachSpeeds = new Lazy<Dictionary<string, CifpSpeedRestriction>>(() => ClearedApproachSpeeds(aircraft));
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (AltitudeSetBefore(fix, transmissionId))
            {
                fix.AltitudeRestriction = published.Value.GetValueOrDefault(fix.Name)?.AltitudeRestriction;
                fix.CrossingTransmissionId = null;
            }

            if (ResetsSpeed(fix, transmissionId, published))
            {
                fix.SpeedRestriction =
                    approachSpeeds.Value.GetValueOrDefault(fix.Name) ?? published.Value.GetValueOrDefault(fix.Name)?.SpeedRestriction;
                fix.SpeedTransmissionId = null;
            }

            fix.RevertAltitude = null;
            fix.RevertAssignedAltitude = null;
            fix.RevertSpeed = null;
            fix.RevertAssignedSpeed = null;
            fix.RevertSpeedMax = false;
        }
    }

    /// <summary>
    /// Whether a controller's crossing set <paramref name="fix"/>'s altitude outside transmission
    /// <paramref name="transmissionId"/>: a crossing stamp of another transmission, or reverts left by a crossing with none.
    /// </summary>
    private static bool AltitudeSetBefore(NavigationTarget fix, int transmissionId) =>
        ((fix.CrossingTransmissionId is not null) || HasPendingRevert(fix)) && !IsStampedBy(fix, transmissionId);

    /// <summary>
    /// Whether a via clearance of transmission <paramref name="transmissionId"/> resets <paramref name="fix"/>'s speed: its
    /// speed is not this transmission's, and an earlier transmission set it, it carries a speed revert, or the via
    /// procedure (<paramref name="published"/>) publishes the fix. Read before the clearance drops the fix's reverts.
    /// </summary>
    private static bool ResetsSpeed(NavigationTarget fix, int transmissionId, Lazy<Dictionary<string, NavigationTarget>> published) =>
        !IsSpeedStampedBy(fix, transmissionId)
        && (
            (fix.SpeedTransmissionId is not null)
            || (fix.RevertSpeed is not null)
            || (fix.RevertAssignedSpeed is not null)
            || fix.RevertSpeedMax
            || published.Value.ContainsKey(fix.Name)
        );

    /// <summary>
    /// A via clearance cancels any speed assigned before it, a superseded crossing speed included, so the procedure's
    /// published speeds and the default schedule take the speed back (7110.65 5-7-1; AIM 4-4-12). A speed assigned in
    /// the via clearance's own block (<paramref name="speedAssignedInBlock"/>) is restated with it (7110.65 5-7-1.c) and
    /// stands. The cancel ends only the ATC assignment: on a STAR the last crossed published speed is the ceiling again
    /// (<see cref="FlightCommandHandler.RestoreCrossedPublishedCeiling"/>), and a crossing speed of this same transmission
    /// stays in force (<see cref="KeepTransmissionCrossingSpeed"/>).
    /// </summary>
    private static void CancelEarlierAssignedSpeed(AircraftState aircraft, bool speedAssignedInBlock, int transmissionId)
    {
        if (!speedAssignedInBlock)
        {
            FlightCommandHandler.CancelAssignedSpeed(aircraft);
            FlightCommandHandler.RestoreCrossedPublishedCeiling(aircraft);
            KeepTransmissionCrossingSpeed(aircraft, transmissionId);
        }
    }

    /// <summary>
    /// An approach clearance cancels every speed assigned before it (7110.65 5-7-1.d), so no crossing restriction on the
    /// route may restore one past its fix: every fix drops its speed reverts, as a via clearance's
    /// <see cref="SupersedeEarlierCrossingRestrictions"/> drops them. The crossing restrictions stay; an earlier
    /// transmission's crossing speed goes back to the published one in <see cref="RestorePublishedSpeedsForApproach"/>.
    /// </summary>
    internal static void DropSpeedReverts(AircraftState aircraft)
    {
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            fix.RevertSpeed = null;
            fix.RevertAssignedSpeed = null;
            fix.RevertSpeedMax = false;
        }
    }

    /// <summary>
    /// A controller speed instruction given after a crossing restriction stands past its fix (7110.65 5-7-1; AIM
    /// 4-4-12.f.1), so SMAX is armed again on no route fix (<see cref="NavigationTarget.RevertSpeedMax"/>). Plain speed
    /// reverts are left as they are.
    /// </summary>
    internal static void DropSpeedMaxReverts(AircraftState aircraft)
    {
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            fix.RevertSpeedMax = false;
        }
    }

    /// <summary>
    /// Whether a crossing speed set now reverts to SMAX past its crossings: SMAX is in force, or a route fix already
    /// arms it again (<see cref="NavigationTarget.RevertSpeedMax"/>).
    /// </summary>
    internal static bool SpeedMaxPending(AircraftState aircraft) =>
        aircraft.Targets.SpeedMaxHeld || aircraft.Targets.NavigationRoute.Exists(fix => fix.RevertSpeedMax);

    /// <summary>
    /// Puts SMAX's re-arm (<see cref="NavigationTarget.RevertSpeedMax"/>) on the route-order-last fix whose speed the
    /// controller set (<see cref="FlightPhysics.IsControllerSetSpeed"/>) and clears it on every other fix, so every
    /// crossing speed before that fix is flown first. The fix that holds it keeps no plain speed revert. Returns whether
    /// a fix holds it.
    /// </summary>
    internal static bool PlaceSpeedMaxRevert(List<NavigationTarget> route)
    {
        int last = route.FindLastIndex(FlightPhysics.IsControllerSetSpeed);
        for (int i = 0; i < route.Count; i++)
        {
            route[i].RevertSpeedMax = i == last;
        }

        if (last >= 0)
        {
            route[last].RevertSpeed = null;
            route[last].RevertAssignedSpeed = null;
        }

        return last >= 0;
    }

    /// <summary>
    /// An approach clearance cancels a crossing speed issued in an earlier transmission, as it cancels every speed assigned
    /// before it (7110.65 5-7-1.d): each route fix whose speed an earlier transmission set
    /// (<see cref="NavigationTarget.SpeedTransmissionId"/>) gets its published speed back, the cleared approach's for that
    /// fix, else the active STAR's, else none, as a via clearance's <see cref="SupersedeEarlierCrossingRestrictions"/> does.
    /// A speed the clearance's own transmission (<paramref name="transmissionId"/>) set stays. The fix's altitude restriction
    /// and altitude reverts stay; its speed reverts go with <see cref="DropSpeedReverts"/>.
    /// </summary>
    internal static void RestorePublishedSpeedsForApproach(AircraftState aircraft, int transmissionId)
    {
        Dictionary<string, CifpSpeedRestriction>? approachSpeeds = null;
        Dictionary<string, NavigationTarget>? starRestrictions = null;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if ((fix.SpeedTransmissionId is null) || IsSpeedStampedBy(fix, transmissionId))
            {
                continue;
            }

            approachSpeeds ??= ClearedApproachSpeeds(aircraft);
            starRestrictions ??= StarRestrictionsForApproach(aircraft);
            fix.SpeedRestriction = approachSpeeds.GetValueOrDefault(fix.Name) ?? starRestrictions.GetValueOrDefault(fix.Name)?.SpeedRestriction;
            fix.SpeedTransmissionId = null;
        }
    }

    /// <summary>
    /// The speeds the approach just cleared publishes, by fix name: the pending clearance's, else the active one's, read
    /// from the transition it cleared (<see cref="ApproachClearance.TransitionName"/>) and then its common legs, the first
    /// leg to give a fix a speed winning; a clearance with no transition, or one its procedure does not name, reads the
    /// common legs only. Each is "at or below", as the approach's fixes enter the route
    /// (<c>ApproachCommandHandler.AppendApproachFixesToNavRoute</c>). Empty for a clearance with no procedure.
    /// </summary>
    internal static Dictionary<string, CifpSpeedRestriction> ClearedApproachSpeeds(AircraftState aircraft)
    {
        var byName = new Dictionary<string, CifpSpeedRestriction>(StringComparer.OrdinalIgnoreCase);
        ApproachClearance? clearance = aircraft.Approach.PendingClearance?.Clearance ?? aircraft.Phases?.ActiveApproach;
        if (clearance?.Procedure is not { } procedure)
        {
            return byName;
        }

        foreach (CifpLeg leg in ClearedTransitionLegs(clearance, procedure).Concat(procedure.CommonLegs))
        {
            if ((leg.Speed is { } speed) && !string.IsNullOrEmpty(leg.FixIdentifier))
            {
                byName.TryAdd(leg.FixIdentifier, new CifpSpeedRestriction(speed.SpeedKts, CifpSpeedRestrictionType.AtOrBelow));
            }
        }

        return byName;
    }

    /// <summary>
    /// The legs of the transition <paramref name="clearance"/> cleared (<see cref="ApproachClearance.TransitionName"/>) on
    /// <paramref name="procedure"/>; none for a clearance with no transition, or one the procedure does not name.
    /// </summary>
    private static IReadOnlyList<CifpLeg> ClearedTransitionLegs(ApproachClearance clearance, CifpApproachProcedure procedure) =>
        ((clearance.TransitionName is { } name) && (procedure.Transitions.TryGetValue(name, out CifpTransition? transition))) ? transition.Legs : [];

    /// <summary>
    /// The active STAR's published restrictions by fix name, for an approach clearance's restore; empty, and without a
    /// warning, when no STAR is active (an aircraft vectored to the approach, or one the clearance took off its STAR).
    /// </summary>
    private static Dictionary<string, NavigationTarget> StarRestrictionsForApproach(AircraftState aircraft)
    {
        if (aircraft.Procedure.ActiveStarId is not { } starId)
        {
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, starId);
        if (star is null)
        {
            Log.LogWarning(
                "{Callsign}: approach clearance could not resolve STAR {Star} at {Dest}; the earlier crossing speeds it cancels are cleared",
                aircraft.Callsign,
                starId,
                aircraft.FlightPlan.Destination
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedStarRestrictions(star, aircraft);
    }

    /// <summary>
    /// After a via or approach clearance has cancelled the speed assigned before it, keeps in force a crossing speed a
    /// CFIX or constrained direct-to of the clearance's own transmission (<paramref name="transmissionId"/>) set, as its
    /// speed stamp records: the crossing was restated with the clearance (7110.65 5-7-1.c), the speed it would revert to
    /// past its fix was not. Each such fix drops its speed reverts, and the first one's speed is the assigned speed again for
    /// the data block, as when the crossing is issued after the clearance. The crossing itself stays on its fix, which the
    /// speed stamp keeps. An altitude-only CFIX restates no speed, so its fix's published speed is never assigned here.
    /// </summary>
    internal static void KeepTransmissionCrossingSpeed(AircraftState aircraft, int transmissionId)
    {
        NavigationTarget? first = null;
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (!IsSpeedStampedBy(fix, transmissionId) || (fix.SpeedRestriction is null))
            {
                continue;
            }

            fix.RevertSpeed = null;
            fix.RevertAssignedSpeed = null;
            fix.RevertSpeedMax = false;
            first ??= fix;
        }

        if (first?.SpeedRestriction is { } crossing)
        {
            aircraft.Targets.AssignedSpeed = crossing.SpeedKts;
        }
    }

    private static bool IsStampedBy(NavigationTarget fix, int transmissionId) =>
        (transmissionId != 0) && (fix.CrossingTransmissionId == transmissionId);

    private static bool IsSpeedStampedBy(NavigationTarget fix, int transmissionId) =>
        (transmissionId != 0) && (fix.SpeedTransmissionId == transmissionId);

    private static bool HasPendingRevert(NavigationTarget fix) =>
        (fix.RevertAltitude is not null)
        || (fix.RevertAssignedAltitude is not null)
        || (fix.RevertSpeed is not null)
        || (fix.RevertAssignedSpeed is not null)
        || fix.RevertSpeedMax;

    /// <summary>The active STAR's published restrictions by fix name; empty when the STAR cannot be resolved.</summary>
    private static Dictionary<string, NavigationTarget> ActiveStarRestrictions(AircraftState aircraft)
    {
        string? starId = aircraft.Procedure.ActiveStarId;
        CifpStarProcedure? star = starId is null ? null : NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, starId);
        if (star is null)
        {
            Log.LogWarning(
                "{Callsign}: DVIA could not resolve STAR {Star} at {Dest}; crossing restrictions it supersedes are cleared, not restored",
                aircraft.Callsign,
                starId,
                aircraft.FlightPlan.Destination
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedStarRestrictions(star, aircraft);
    }

    /// <summary>
    /// The restrictions <paramref name="star"/> publishes on the legs <paramref name="aircraft"/> flies, by fix name,
    /// in the order <c>JARR</c> builds them: the enroute transition(s) its route is on, the common route, then the
    /// runway transition for its runway (the assigned runway, else the procedure's destination runway). Where no
    /// runway transition resolves for it, every runway transition is read, the first to name a fix winning.
    /// </summary>
    private static Dictionary<string, NavigationTarget> PublishedStarRestrictions(CifpStarProcedure star, AircraftState aircraft)
    {
        var commonFixes = new HashSet<string>(star.CommonLegs.Select(l => l.FixIdentifier), StringComparer.OrdinalIgnoreCase);
        var routeFixes = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var orderedLegs = new List<CifpLeg>();
        foreach (CifpTransition transition in star.EnrouteTransitions.Values)
        {
            if (transition.Legs.Any(l => routeFixes.Contains(l.FixIdentifier) && !commonFixes.Contains(l.FixIdentifier)))
            {
                orderedLegs.AddRange(transition.Legs);
            }
        }

        orderedLegs.AddRange(star.CommonLegs);

        string? runway = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Procedure.DestinationRunway;
        CifpTransition? flownRunwayTransition = string.IsNullOrEmpty(runway) ? null : LookupRunwayTransition(star.RunwayTransitions, runway);
        if (flownRunwayTransition is not null)
        {
            orderedLegs.AddRange(flownRunwayTransition.Legs);
        }
        else
        {
            foreach (CifpTransition transition in star.RunwayTransitions.Values)
            {
                orderedLegs.AddRange(transition.Legs);
            }
        }

        var byName = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (NavigationTarget target in DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs))
        {
            if (((target.AltitudeRestriction is not null) || (target.SpeedRestriction is not null)) && !byName.ContainsKey(target.Name))
            {
                byName[target.Name] = target;
            }
        }

        return byName;
    }

    /// <summary>
    /// Activates the SID named in the aircraft's filed route when none is active yet, so a bare
    /// <c>CVIA</c> works after the aircraft was given runway heading (<c>CTO RH</c>) or vectored off
    /// its SID and then re-cleared <c>DCT</c> to a downstream SID fix (the SID being in the cleared
    /// route is the only real-world precondition for "climb via", AIM 5-2-9). Sets
    /// <see cref="AircraftProcedure.ActiveSidId"/> and overlays the published CIFP altitude/speed
    /// restrictions onto the existing (already-sequenced) lateral route by fix name — it does not
    /// rebuild the route, so fixes the aircraft has already passed are not re-added. A fix keeps the crossing altitude
    /// or speed a CFIX in transmission <paramref name="transmissionId"/> set (0 names no transmission). Returns false
    /// when the aircraft is VFR or the filed route contains no resolvable SID.
    /// </summary>
    internal static bool TryActivateFiledSid(AircraftState aircraft, int transmissionId)
    {
        // SIDs are an IFR construct; a VFR aircraft never flies one.
        if (aircraft.FlightPlan.IsVfr)
        {
            return false;
        }

        if (FiledProcedureLookup.FindSid(NavigationDatabase.Instance, aircraft.FlightPlan.Departure, aircraft.FlightPlan.Route) is not { } filed)
        {
            return false;
        }

        OverlaySidRestrictions(aircraft, filed.Procedure, transmissionId);
        aircraft.Procedure.ActiveSidId = filed.Id;
        aircraft.Procedure.LastCrossedPublishedSpeedKts = null;
        return true;
    }

    /// <summary>
    /// Copies each published altitude/speed restriction from <paramref name="sid"/> onto the matching
    /// fix (by name) already present in the aircraft's navigation route, reusing the same leg→target
    /// resolution as the departure clearance so the constraint semantics are identical
    /// (<see cref="OverlayPublishedRestrictions"/>).
    /// </summary>
    private static void OverlaySidRestrictions(AircraftState aircraft, CifpSidProcedure sid, int transmissionId) =>
        OverlayPublishedRestrictions(aircraft, PublishedSidRestrictions(sid, aircraft), transmissionId);

    /// <summary>The active SID's published restrictions by fix name; empty when the SID cannot be resolved.</summary>
    private static Dictionary<string, NavigationTarget> ActiveSidRestrictions(AircraftState aircraft)
    {
        if (ResolveActiveSid(aircraft) is not { } sid)
        {
            Log.LogWarning(
                "{Callsign}: CVIA could not resolve SID {Sid} at {Dep}; crossing restrictions it supersedes are cleared, not restored",
                aircraft.Callsign,
                aircraft.Procedure.ActiveSidId,
                aircraft.FlightPlan.Departure
            );
            return new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        }

        return PublishedSidRestrictions(sid, aircraft);
    }

    /// <summary>
    /// The restrictions <paramref name="sid"/> publishes on the legs <paramref name="aircraft"/> flies, by fix name, first
    /// leg winning.
    /// </summary>
    private static Dictionary<string, NavigationTarget> PublishedSidRestrictions(CifpSidProcedure sid, AircraftState aircraft)
    {
        var byName = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (NavigationTarget target in DepartureClearanceHandler.ResolveLegsToTargets(FlownSidLegs(sid, aircraft)))
        {
            if (((target.AltitudeRestriction is not null) || (target.SpeedRestriction is not null)) && !byName.ContainsKey(target.Name))
            {
                byName[target.Name] = target;
            }
        }

        return byName;
    }

    /// <summary>
    /// The legs of <paramref name="sid"/> that <paramref name="aircraft"/> flies, in order: the runway transition matching
    /// <see cref="AircraftProcedure.DepartureRunway"/> (a persistent field that survives a phase teardown), so the
    /// first-wins dedup prefers the flown runway's values; the common route; then the enroute transition(s) flown — named
    /// in the filed route, or holding a fix of the live route outside the common route. Where no runway transition, or no
    /// enroute transition, can be identified, every one is read: harmless for the by-name overlay, since a rejoin route
    /// only holds downstream fixes.
    /// </summary>
    private static List<CifpLeg> FlownSidLegs(CifpSidProcedure sid, AircraftState aircraft)
    {
        var allLegs = new List<CifpLeg>();

        CifpTransition? assignedRwTransition = null;
        if (aircraft.Procedure.DepartureRunway is { } rwy)
        {
            if (!sid.RunwayTransitions.TryGetValue("RW" + rwy, out assignedRwTransition))
            {
                // CIFP "B" suffix means both L/R share one transition (e.g. "RW28B").
                sid.RunwayTransitions.TryGetValue("RW" + rwy.TrimEnd('L', 'R', 'C') + "B", out assignedRwTransition);
            }
        }

        if (assignedRwTransition is not null)
        {
            allLegs.AddRange(assignedRwTransition.Legs);
        }
        else
        {
            foreach (CifpTransition transition in sid.RunwayTransitions.Values)
            {
                allLegs.AddRange(transition.Legs);
            }
        }

        allLegs.AddRange(sid.CommonLegs);
        allLegs.AddRange(FlownSidEnrouteTransitions(sid, aircraft).SelectMany(t => t.Legs));
        return allLegs;
    }

    private static List<CifpTransition> FlownSidEnrouteTransitions(CifpSidProcedure sid, AircraftState aircraft)
    {
        var filedTokens = new HashSet<string>(FiledRouteTokens(aircraft), StringComparer.OrdinalIgnoreCase);
        var commonFixes = new HashSet<string>(sid.CommonLegs.Select(l => l.FixIdentifier), StringComparer.OrdinalIgnoreCase);
        var routeFixes = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        List<CifpTransition> flown =
        [
            .. sid.EnrouteTransitions.Values.Where(t =>
                filedTokens.Contains(t.Name) || t.Legs.Any(l => routeFixes.Contains(l.FixIdentifier) && (!commonFixes.Contains(l.FixIdentifier)))
            ),
        ];
        return flown.Count > 0 ? flown : [.. sid.EnrouteTransitions.Values];
    }

    internal static CommandResult DispatchReportFieldInSight(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("Use RFIS <clock> <miles> in solo training");
        }

        return DispatchReportFieldInSightCore(aircraft, ctx);
    }

    private static CommandResult DispatchReportFieldInSightCore(AircraftState aircraft, DispatchContext ctx)
    {
        // Fast path: if the tick processor has already confirmed acquisition on a
        // visual approach, just echo the in-sight response.
        if (aircraft.Approach.HasReportedFieldInSight)
        {
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft)
            );
            return CommandDispatcher.Ok("Field in sight");
        }

        string destination = aircraft.FlightPlan.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return new CommandResult(false, "Unable, no arrival airport assigned");
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (navDb.GetAirportPosition(destination) is null || navDb.GetAirportElevation(destination) is null)
        {
            return new CommandResult(false, $"Unable, {destination} not in nav database");
        }

        MetarParser.ParsedMetar? metar = ctx.Weather?.GetWeatherForAirport(destination);
        VisualAcquisitionResult result =
            VisualAcquisition.TryAcquireAirport(aircraft, ctx.Weather)
            ?? throw new InvalidOperationException($"Destination {destination} pre-validated but TryAcquireAirport returned null");

        if (result.Acquired)
        {
            // Setting the flag here unblocks the CVA FOLLOW gate and lets the tick
            // processor take over maintained-contact tracking once visual clearance
            // becomes active. First-check acquisition supersedes any in-flight
            // "looking" state.
            aircraft.Approach.HasReportedFieldInSight = true;
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft)
            );
            aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
            return CommandDispatcher.Ok("Field in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the field yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RFIS replaces the prior observation —
        // the latest request always wins.
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        aircraft.PendingObservations.Add(new FieldAcquisitionObservation());
        if (ctx.SoloTrainingMode)
        {
            Pilot.PilotResponder.QueueSoloPilotReadback(
                aircraft,
                FormatFieldLookingNotification(result, destination),
                Pilot.PilotResponder.SourceSayReadback
            );
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(FormatFieldLookingNotification(result, destination));
        }
        return CommandDispatcher.Ok($"Looking for the field — {FormatFieldFailureHint(result, metar, destination)}");
    }

    internal static CommandResult DispatchReportFieldAdvisory(ReportFieldAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        CommandResult acquisition = DispatchReportFieldInSightCore(aircraft, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(CommandDescriber.FormatFieldAdvisoryPhrase(cmd.Details)) : acquisition;
    }

    internal static CommandResult DispatchReportTrafficInSight(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("Use RTIS <clock> <miles> <direction> <type> [altitude] in solo training");
        }

        string? normalizedCallsign = string.IsNullOrWhiteSpace(targetCallsign) ? null : targetCallsign.Trim().ToUpperInvariant();

        // Fast path: the aircraft already has traffic in sight (tick processor confirmed an active
        // FOLLOW, or a prior RTIS/RTISF/FOLLOWF). A bare RTIS — or one naming the same traffic
        // already reported — re-echoes the in-sight response without re-running visual acquisition
        // (which could now geometrically soft-fail on the followed traffic). A different callsign is
        // a new sighting: fall through to full validate + acquisition so a bogus callsign is
        // rejected and a real one is actually acquired.
        if (
            aircraft.Approach.HasReportedTrafficInSight
            && ((normalizedCallsign is null) || (normalizedCallsign == aircraft.Approach.LastReportedTrafficCallsign))
        )
        {
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, normalizedCallsign)
            );
            return CommandDispatcher.Ok("Traffic in sight");
        }

        if (normalizedCallsign is null)
        {
            return new CommandResult(false, "Unable, no traffic specified");
        }

        AircraftState? target = ctx.FindAircraft?.Invoke(normalizedCallsign);
        if (target is null)
        {
            return new CommandResult(false, $"Negative contact, {normalizedCallsign} not on this frequency");
        }

        return DispatchReportTrafficInSightForTarget(aircraft, normalizedCallsign, target, ctx);
    }

    private static CommandResult DispatchReportTrafficInSightForTarget(
        AircraftState aircraft,
        string targetCallsign,
        AircraftState target,
        DispatchContext ctx
    )
    {
        VisualAcquisitionResult result = VisualAcquisition.TryAcquireTraffic(aircraft, target, ctx.Weather);

        if (result.Acquired)
        {
            aircraft.Approach.HasReportedTrafficInSight = true;
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign)
            );
            // First-check acquisition supersedes any in-flight "looking" state.
            aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
            return CommandDispatcher.Ok("Traffic in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the traffic yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RTIS (same or different callsign) replaces
        // the prior observation — the latest request always wins.
        string targetCallsignUpper = targetCallsign.ToUpperInvariant();
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
        aircraft.PendingObservations.Add(
            new TrafficAcquisitionObservation(targetCallsignUpper, ctx.ScenarioElapsedSeconds, ArmedAirborne: !aircraft.IsOnGround)
        );
        if (ctx.SoloTrainingMode)
        {
            Pilot.PilotResponder.QueueSoloPilotReadback(
                aircraft,
                FormatTrafficLookingNotification(result, target),
                Pilot.PilotResponder.SourceSayReadback
            );
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(FormatTrafficLookingNotification(result, target));
        }
        return CommandDispatcher.Ok($"Looking for traffic — {FormatTrafficFailureHint(result, target)}");
    }

    internal static CommandResult DispatchReportTrafficAdvisory(ReportTrafficAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficAdvisoryPhrase(cmd.Details));
    }

    /// <summary>
    /// Runs the in-sight report against the aircraft an advisory resolved to (looked up by callsign in the snapshot the
    /// candidates were built from) and answers with <paramref name="phrase"/> when the report is accepted.
    /// </summary>
    private static CommandResult AcknowledgeResolvedAdvisory(
        AircraftState aircraft,
        TrafficAdvisoryTargetMatch match,
        IReadOnlyList<AircraftState>? snapshot,
        DispatchContext ctx,
        string phrase
    )
    {
        AircraftState target =
            snapshot?.FirstOrDefault(candidate => candidate.Callsign.Equals(match.Target.Callsign, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Resolved traffic {match.Target.Callsign} is not in the aircraft list it was resolved from");
        CommandResult acquisition = DispatchReportTrafficInSightForTarget(aircraft, target.Callsign, target, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(phrase) : acquisition;
    }

    internal static CommandResult DispatchReportTrafficRelative(ReportTrafficRelativeCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveRelativeTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficRelativePhrase(cmd.Details));
    }

    internal static CommandResult DispatchReportTrafficPattern(ReportTrafficPatternCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficPatternPhrase(cmd.Details));
    }

    internal static CommandResult DispatchReportTrafficLandmark(ReportTrafficLandmarkCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        (double Lat, double Lon)? position = NavigationDatabase.Instance.GetFixPosition(cmd.Details.FixName);
        if (position is null)
        {
            return new CommandResult(false, $"Unable, unknown landmark {cmd.Details.FixName}");
        }

        var landmark = new LatLon(position.Value.Lat, position.Value.Lon);
        IReadOnlyList<AircraftState>? snapshot = ctx.ListAircraft?.Invoke();
        TrafficAdvisoryTargetMatch? match = TrafficAdvisoryMatcher.ResolveLandmarkTrafficTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            landmark,
            cmd.Details.AircraftType,
            TrafficAdvisoryCandidate.FromAll(snapshot),
            out string error
        );
        if (match is null)
        {
            return new CommandResult(false, error);
        }

        return AcknowledgeResolvedAdvisory(aircraft, match, snapshot, ctx, CommandDescriber.FormatTrafficLandmarkPhrase(cmd.Details));
    }

    internal static CommandResult DispatchSafetyAlert(SafetyAlertCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        TrafficAdvisoryCandidate? target = TrafficAdvisoryMatcher.ResolveSafetyAlertTarget(
            TrafficAdvisoryCandidate.From(aircraft),
            cmd.Details,
            TrafficAdvisoryCandidate.FromAll(ctx.ListAircraft?.Invoke()),
            out string error
        );
        if (target is null)
        {
            return new CommandResult(false, error);
        }

        return CommandDispatcher.Ok(CommandDescriber.FormatSafetyAlertPhrase(cmd.Details));
    }

    /// <summary>
    /// Pilot readback when RTIS can't be satisfied on the first check — the
    /// pilot acknowledges the request and commits to keep looking. Reviewed with
    /// aviation-sim-expert: pilot phraseology only ("negative contact",
    /// "looking"), no simulator-internal diagnostics ("outside forward
    /// hemisphere", detection-range numbers). Layer details are paraphrased as
    /// "clouds between us". "Unable" is reserved for refused clearances
    /// (7110.65 2-4-20) and is NOT used here. See AIM 4-4-14 (visual separation)
    /// and AIM 5-5-10 (pilot traffic advisories).
    /// </summary>
    private static string FormatTrafficLookingNotification(VisualAcquisitionResult r, AircraftState target) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.MixedCeiling => $"Negative contact, {target.Callsign}, clouds between us, looking",
            VisualAcquisitionFailure.OccludedByBank => $"Negative contact, {target.Callsign}, in the turn, looking",
            _ => $"Negative contact, {target.Callsign}, looking",
        };

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the target. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="FormatTrafficLookingNotification"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatTrafficFailureHint(VisualAcquisitionResult r, AircraftState target) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.MixedCeiling => $"cloud layer {FormatLayer(r.BindingLayer)} between aircraft",
            VisualAcquisitionFailure.OccludedByBank => "target on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.BehindOwnship => "target behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OutOfRange => $"target {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm ({target.AircraftType})",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Pilot readback when RFIS can't be satisfied on the first check — the
    /// pilot acknowledges the request and commits to keep looking. Mirrors
    /// the RTIS soft-fail readback (see <see cref="FormatTrafficLookingNotification"/>):
    /// pilot phraseology only, no simulator-internal diagnostics. "Unable" is
    /// reserved for refused clearances (7110.65 2-4-20) and is NOT used here.
    /// Reviewed with aviation-sim-expert:
    /// - "On top" only fits <see cref="VisualAcquisitionFailure.AboveCeiling"/>
    ///   (the AIM 4-4-8 / 5-5-3 sense of "above an obscuring layer"). For
    ///   <see cref="VisualAcquisitionFailure.InClassA"/> the issue is altitude,
    ///   not a deck, so the readback collapses to the default — the controller
    ///   already knows why at FL180+.
    /// - "Field's behind us" is a real-world idiom for
    ///   <see cref="VisualAcquisitionFailure.BehindOwnship"/> and is actionable
    ///   for the controller (cue to offer a vector).
    /// - <see cref="VisualAcquisitionFailure.OppositeSideOfRunway"/> stays in
    ///   the default arm; runway side isn't standard pilot phraseology in this
    ///   reply.
    /// See AIM 5-4-23 (visual approach) and AIM 4-1-15 / 5-5-8 ("negative
    /// contact" usage).
    /// </summary>
    private static string FormatFieldLookingNotification(VisualAcquisitionResult r, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.OccludedByBank => $"Negative contact, {airportId}, in the turn, looking",
            VisualAcquisitionFailure.AboveCeiling => $"Negative contact, {airportId}, on top, looking",
            VisualAcquisitionFailure.BehindOwnship => $"Negative contact, {airportId}, field's behind us, looking",
            _ => $"Negative contact, {airportId}, looking",
        };

    // Phraseology borrowed from AIM §4-1-15 and §5-5-8 (traffic advisories use
    // "negative contact" when the pilot cannot visually acquire a target) and
    // 7110.65 §7-2-1 (visual separation boundary at FL180).

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the field. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="FormatFieldLookingNotification"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatFieldFailureHint(VisualAcquisitionResult r, MetarParser.ParsedMetar? metar, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.InClassA => "ownship in Class Alpha",
            VisualAcquisitionFailure.AboveCeiling => $"{airportId} below {FormatLayer(r.BindingLayer)} (cloud deck blocking ground view)",
            VisualAcquisitionFailure.BehindOwnship => $"{airportId} behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OccludedByBank => $"{airportId} on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.OutOfRange =>
                $"{airportId} {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm{VisibilityQualifier(metar)}",
            VisualAcquisitionFailure.OppositeSideOfRunway => $"{airportId} on opposite side of runway",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Formats a cloud layer as the 6-character METAR-style code controllers and
    /// pilots actually read out loud, e.g. BKN070 or OVC200. Falls back to a
    /// generic "cloud layer" if no binding layer was recorded on the result.
    /// </summary>
    private static string FormatLayer(MetarParser.CloudLayer? layer)
    {
        if (layer is null)
        {
            return "cloud layer";
        }
        string prefix = layer.Cover switch
        {
            MetarParser.CloudCover.Few => "FEW",
            MetarParser.CloudCover.Scattered => "SCT",
            MetarParser.CloudCover.Broken => "BKN",
            MetarParser.CloudCover.Overcast => "OVC",
            _ => "CLD",
        };
        int hundreds = layer.BaseFeetAgl / 100;
        return $"{prefix}{hundreds:D3}";
    }

    /// <summary>
    /// Clarifies the max-range qualifier for field acquisition. Reported
    /// visibility only participates in the range cap when below the 10SM
    /// reporting maximum (see <see cref="MetarParser.UnrestrictedVisibilitySm"/>),
    /// so only mention it then — a clear-day "10SM" is not a constraint.
    /// </summary>
    private static string VisibilityQualifier(MetarParser.ParsedMetar? metar)
    {
        if (metar?.VisibilityStatuteMiles is double visSm && visSm < MetarParser.UnrestrictedVisibilitySm)
        {
            return $" — visibility {visSm:F0} SM";
        }
        return string.Empty;
    }

    internal static CommandResult DispatchReportFieldInSightForced(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.RefusesRpoOnly)
        {
            return CommandDispatcher.OperatorGuidanceRefusal("RFISF is RPO-only; use RFIS <clock> <miles> in solo training");
        }

        aircraft.Approach.HasReportedFieldInSight = true;
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildFieldInSight(aircraft)
        );
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        return CommandDispatcher.Ok("Field in sight (forced)");
    }

    internal static CommandResult DispatchReportTrafficInSightForced(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.RefusesRpoOnly)
        {
            return CommandDispatcher.OperatorGuidanceRefusal(
                "RTISF is RPO-only; use RTIS <clock> <miles> <direction> <type> <altitude> in solo training"
            );
        }

        aircraft.Approach.HasReportedTrafficInSight = true;
        if (!string.IsNullOrWhiteSpace(targetCallsign))
        {
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
        }
        else if (aircraft.PendingObservations.OfType<TrafficAcquisitionObservation>().FirstOrDefault() is { } pending)
        {
            // Bare RTISF folds in a still-pending RTIS target (same rationale as bare FOLLOWF):
            // the target callsign lives only in PendingObservations until acquisition succeeds.
            // RTISF supersedes that look-for-traffic, so consume and clear the observation.
            aircraft.Approach.LastReportedTrafficCallsign = pending.TargetCallsign.ToUpperInvariant();
            aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
        }
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign)
        );
        return CommandDispatcher.Ok("Traffic in sight (forced)");
    }

    /// <summary>
    /// Arms (or cancels) a deferred pilot position report. The flags live on
    /// <see cref="AircraftApproachState"/> so pattern-leg reports survive lap rebuilds (re-arm each
    /// circuit); N-mile-final and at-fix arms are one-shot and cleared by the tick poll when they
    /// fire. The pilot wilco acknowledgment is produced by the generic solo readback
    /// (<c>PilotResponder.BuildReadback</c>); this handler only records intent and returns the
    /// controller-facing echo. Dry-run safe — touches no <c>TerminalEmitter</c>.
    /// </summary>
    internal static CommandResult DispatchReport(ReportCommand cmd, AircraftState aircraft)
    {
        AircraftApproachState approach = aircraft.Approach;
        switch (cmd.Trigger)
        {
            case ReportTrigger.Cancel:
                return DispatchReportCancel(cmd, aircraft);

            case ReportTrigger.Crosswind:
            case ReportTrigger.Downwind:
            case ReportTrigger.Base:
            case ReportTrigger.Final:
            case ReportTrigger.Midfield:
                if (!HasPatternCircuit(aircraft))
                {
                    return new CommandResult(false, $"Unable, {aircraft.Callsign} is not in the pattern");
                }
                SetPatternLegReportArmed(approach, cmd.Trigger, true);
                return CommandDispatcher.Ok(PatternReportArmedEcho(cmd.Trigger));

            case ReportTrigger.MileFinal:
                if (aircraft.Phases?.AssignedRunway is null)
                {
                    return new CommandResult(false, "Unable, no runway assigned for final reporting");
                }
                if (cmd.DistanceNm is not > 0)
                {
                    return new CommandResult(false, "Final distance must be a positive whole number of miles");
                }
                approach.ReportFinalMileTarget = cmd.DistanceNm;
                return CommandDispatcher.Ok($"Will report {cmd.DistanceNm}-mile final");

            case ReportTrigger.AtFix:
                return DispatchReportAtFix(cmd, aircraft);

            default:
                return new CommandResult(false, "Unrecognized REPORT request");
        }
    }

    private static CommandResult DispatchReportCancel(ReportCommand cmd, AircraftState aircraft)
    {
        switch (cmd.CancelTarget)
        {
            case ReportTrigger.MileFinal:
                return CancelMileFinalReport(aircraft);

            case ReportTrigger.AtFix:
                return CancelAtFixReport(cmd, aircraft);

            case ReportTrigger.Crosswind:
            case ReportTrigger.Downwind:
            case ReportTrigger.Base:
            case ReportTrigger.Final:
            case ReportTrigger.Midfield:
                return CancelPatternLegReport(aircraft.Approach, cmd.CancelTarget.Value);

            default:
                aircraft.Approach.ClearArmedReports();
                return CommandDispatcher.Ok("Reports cancelled");
        }
    }

    private static CommandResult CancelMileFinalReport(AircraftState aircraft)
    {
        if (aircraft.Approach.ReportFinalMileTarget is not { } miles)
        {
            return new CommandResult(false, $"Unable, {aircraft.Callsign} is not reporting a mile final");
        }

        aircraft.Approach.ReportFinalMileTarget = null;
        return CommandDispatcher.Ok($"Cancelled {miles}-mile final report");
    }

    private static CommandResult CancelAtFixReport(ReportCommand cmd, AircraftState aircraft)
    {
        string? fixName = NormalizeFixName(cmd.FixName);
        if (fixName is null)
        {
            return new CommandResult(false, "REPORT requires a fix name");
        }

        if (!string.Equals(aircraft.Approach.ReportAtFixName, fixName, StringComparison.Ordinal))
        {
            return new CommandResult(false, $"Unable, {aircraft.Callsign} is not reporting passing {fixName}");
        }

        aircraft.Approach.ReportAtFixName = null;
        aircraft.Approach.ReportAtFixLat = null;
        aircraft.Approach.ReportAtFixLon = null;
        return CommandDispatcher.Ok($"Cancelled report passing {fixName}");
    }

    private static CommandResult CancelPatternLegReport(AircraftApproachState approach, ReportTrigger leg)
    {
        SetPatternLegReportArmed(approach, leg, false);
        return CommandDispatcher.Ok(PatternReportCancelledEcho(leg));
    }

    /// <summary>The trimmed, upper-cased fix name, or null when the command carried none.</summary>
    private static string? NormalizeFixName(string? fixName)
    {
        string? normalized = fixName?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static CommandResult DispatchReportAtFix(ReportCommand cmd, AircraftState aircraft)
    {
        string? fixName = NormalizeFixName(cmd.FixName);
        if (fixName is null)
        {
            return new CommandResult(false, "REPORT requires a fix name");
        }

        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(fixName);
        if (pos is null)
        {
            return new CommandResult(false, $"Unable, {fixName} not in nav database");
        }

        aircraft.Approach.ReportAtFixName = fixName;
        aircraft.Approach.ReportAtFixLat = pos.Value.Lat;
        aircraft.Approach.ReportAtFixLon = pos.Value.Lon;
        return CommandDispatcher.Ok($"Will report passing {fixName}");
    }

    private static bool HasPatternCircuit(AircraftState aircraft) =>
        aircraft.Phases?.Phases.Any(p => p is UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase) == true;

    private static void SetPatternLegReportArmed(AircraftApproachState approach, ReportTrigger leg, bool armed)
    {
        switch (leg)
        {
            case ReportTrigger.Crosswind:
                approach.ReportArmedCrosswind = armed;
                break;
            case ReportTrigger.Downwind:
                approach.ReportArmedDownwind = armed;
                break;
            case ReportTrigger.Base:
                approach.ReportArmedBase = armed;
                break;
            case ReportTrigger.Final:
                approach.ReportArmedFinal = armed;
                break;
            case ReportTrigger.Midfield:
                approach.ReportArmedMidfield = armed;
                break;
            default:
                break;
        }
    }

    /// <summary>How a pattern-leg report names itself: "turning crosswind", or "midfield downwind".</summary>
    private static string PatternReportPhrase(ReportTrigger leg) =>
        leg == ReportTrigger.Midfield ? ReportLegWord(leg) : $"turning {ReportLegWord(leg)}";

    /// <summary>The echo for an armed pattern-leg report: "Will report midfield downwind", else "Will report turning {leg}".</summary>
    private static string PatternReportArmedEcho(ReportTrigger leg) => $"Will report {PatternReportPhrase(leg)}";

    /// <summary>The echo for a cancelled pattern-leg report: "Cancelled midfield downwind report", else "Cancelled turning {leg} report".</summary>
    private static string PatternReportCancelledEcho(ReportTrigger leg) => $"Cancelled {PatternReportPhrase(leg)} report";

    private static string ReportLegWord(ReportTrigger leg) =>
        leg switch
        {
            ReportTrigger.Crosswind => "crosswind",
            ReportTrigger.Downwind => "downwind",
            ReportTrigger.Base => "base",
            ReportTrigger.Final => "final",
            ReportTrigger.Midfield => "midfield downwind",
            _ => "final",
        };
}
