using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Pilot;

/// <summary>How the pilot words a Class B clearance: entering to land inside it, or crossing it.</summary>
public enum BravoClearanceWording
{
    Into,
    Through,
}

/// <summary>
/// The Class B a VFR aircraft is waiting to be cleared into: the one named by its open airspace-entry request, or the
/// one it is holding outside of. Captured before a command is dispatched, because the dispatch can replace the hold.
/// <see cref="Wording"/> is how the pilot asked: "through" only when its open request said so.
/// </summary>
public sealed record BravoClearanceWait(string Ident, BravoClearanceWording Wording);

/// <summary>
/// The controller's "remain outside Bravo airspace" (<c>RMOB</c>) the pilot is under (<see cref="AircraftState.RemainOutsideBravo"/>).
/// </summary>
/// <param name="Ident">The Class B's area name (<see cref="AirspaceDatabase.ClassBAreaName"/>).</param>
/// <param name="Minutes">The minutes the pilot was told to expect clearance in; null when none were given.</param>
/// <param name="AskDueSeconds">When the pilot asks "any estimate on the bravo?", in scenario-elapsed seconds; null for no ask.</param>
/// <param name="AskVoiced">Whether the pilot has voiced that ask: it asks once only.</param>
public sealed record RemainOutsideBravoState(string Ident, int? Minutes, double? AskDueSeconds, bool AskVoiced);

/// <summary>What a <c>RMOB</c> found: the aircraft outside a Class B, inside one, or near none.</summary>
public enum RemainOutsideOutcome
{
    /// <summary>Outside the Class B: the pilot remains outside it.</summary>
    Outside,

    /// <summary>Already inside the Class B (in 3D): the pilot asks for intentions and keeps its clearance.</summary>
    Inside,

    /// <summary>No Class B in reach: the pilot asks the controller to say again.</summary>
    NoBravo,
}

/// <summary>
/// The Class B a <c>RMOB</c> names (its area name; null with <see cref="RemainOutsideOutcome.NoBravo"/>) and where the
/// aircraft is against it.
/// </summary>
public sealed record RemainOutsideResolution(RemainOutsideOutcome Outcome, string? Ident);

/// <summary>
/// Solo training: a controller instruction that can only be flown by entering Class B is taken as the Class B
/// clearance for a VFR aircraft that does not hold one yet. The student playing controller vectors, clears direct, or
/// clears for an approach or pattern entry at an airport inside the Bravo; without this the pilot flies the
/// instruction to the boundary, orbits outside it, and turns away again every time the instruction is repeated. The
/// pilot reads the grant back ("cleared into the bravo"), so the student hears what was taken as a clearance.
/// </summary>
public static class ImplicitBravoClearance
{
    private static readonly ILogger Log = SimLog.CreateLogger("ImplicitBravoClearance");

    /// <summary>How far ahead a vector or direct is projected when deciding whether it enters Class B.</summary>
    public const double EntryLookaheadSeconds = 120.0;

    /// <summary>
    /// The longer projection for a pilot already waiting on a Class B clearance: a vector toward the Bravo it asked
    /// for (or is holding outside) answers the request even when the boundary is a few minutes away.
    /// </summary>
    public const double WaitingEntryLookaheadSeconds = 300.0;

    /// <summary>
    /// How long an aircraft that has entered its cleared Class B may be outside it, with no re-entry projected, before the
    /// clearance ends: a brief excursion past a boundary does not cancel it.
    /// </summary>
    public const double ExitGraceSeconds = 120.0;

    /// <summary>
    /// The instructions that can stand as a Class B clearance: headings and turns, directs, pattern entries and
    /// traffic-pattern direction, and approach clearances. A frequency change is not one. The pending-request tracker
    /// counts these as the answer to a pilot's airspace-entry request.
    /// </summary>
    public static bool IsImpliedClearanceCommand(ParsedCommand command) =>
        IsVectorOrDirect(command)
        || command
            is EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftCrosswindCommand
                or EnterRightCrosswindCommand
                or EnterLeftBaseCommand
                or EnterRightBaseCommand
                or EnterFinalCommand
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand
                or ClearedApproachCommand
                or ClearedApproachStraightInCommand
                or ClearedVisualApproachCommand
                or PositionTurnAltitudeClearanceCommand;

    /// <summary>
    /// The implied-clearance instructions that end in a landing at the clearance's airport (a pattern entry, traffic
    /// direction or approach clearance): the ones <see cref="GrantForLanding"/> judges, as opposed to a vector or direct.
    /// </summary>
    public static bool IsLandingClearanceCommand(ParsedCommand command) => IsImpliedClearanceCommand(command) && !IsVectorOrDirect(command);

    /// <summary>
    /// The Class B the aircraft is waiting on right now, or null: an open Class B airspace-entry request, else an active
    /// boundary hold outside a Class B volume.
    /// </summary>
    public static BravoClearanceWait? CaptureWait(AircraftState aircraft)
    {
        if (
            aircraft.PendingPilotRequest is
            {
                IsOpen: true,
                Kind: PilotPendingRequestKind.AirspaceEntry,
                AirspaceClass: nameof(AirspaceClass.Bravo),
                AirspaceIdent: { Length: > 0 } requested
            } request
        )
        {
            return new BravoClearanceWait(requested, request.BravoWording ?? BravoClearanceWording.Into);
        }

        return aircraft.Phases?.CurrentPhase is AirspaceBoundaryHoldPhase { AirspaceClass: AirspaceClass.Bravo, Ident: { Length: > 0 } held }
            ? new BravoClearanceWait(held, BravoClearanceWording.Into)
            : null;
    }

    /// <summary>
    /// The area name a Class B clearance is held by: the volume's Ident, else its ICAO id. Every shelf volume of one Class B
    /// shares it, so one clearance covers them all.
    /// </summary>
    public static string AreaName(AirspaceVolume volume) => AirspaceDatabase.ClassBAreaName(volume);

    /// <summary>Whether <paramref name="position"/> is laterally inside any volume of the Class B named <paramref name="ident"/>.</summary>
    public static bool IsLaterallyInside(LatLon position, string ident, AirspaceDatabase airspace) =>
        ContainsLateral(VolumesOf(ident, airspace), position);

    /// <summary>
    /// Every volume (shelf) of the Class B named <paramref name="ident"/>, matched case-insensitively on its area name, from
    /// the database's cached lookup; empty for an unknown name.
    /// </summary>
    public static IReadOnlyList<AirspaceVolume> VolumesOf(string ident, AirspaceDatabase airspace) => airspace.ClassBArea(ident);

    private static bool ContainsLateral(IReadOnlyList<AirspaceVolume> volumes, LatLon position)
    {
        for (int i = 0; i < volumes.Count; i++)
        {
            if (volumes[i].ContainsLateral(position))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The area name of the Class B laterally around <paramref name="position"/>, or null when it is inside none.</summary>
    private static string? ClassBLaterallyAround(LatLon position, AirspaceDatabase airspace)
    {
        foreach ((string area, IReadOnlyList<AirspaceVolume> volumes) in airspace.ClassBAreas)
        {
            if (ContainsLateral(volumes, position))
            {
                return area;
            }
        }

        return null;
    }

    /// <summary>
    /// The Class B a bare <c>CLBRV</c> clears the aircraft into: the one it is waiting on (<see cref="CaptureWait"/>), else
    /// one it is laterally inside, else the first it is projected to enter within <see cref="EntryLookaheadSeconds"/>; null
    /// when there is none.
    /// </summary>
    public static string? ResolveCleared(AircraftState aircraft, AirspaceDatabase airspace)
    {
        if (CaptureWait(aircraft) is { } wait)
        {
            return wait.Ident;
        }

        if (ClassBLaterallyAround(aircraft.Position, airspace) is { } inside)
        {
            return inside;
        }

        AirspaceBoundaryCrossing? crossing = airspace.FindFirstProjectedEntry(aircraft, EntryLookaheadSeconds, AirspaceClass.Bravo);
        return crossing is null ? null : AreaName(crossing.Volume);
    }

    /// <summary>
    /// The controller's explicit Class B clearance (<c>CLBRV</c>): grants the Class B <see cref="ResolveCleared"/> names,
    /// and changes nothing when it names none.
    /// </summary>
    public static void GrantExplicit(AircraftState aircraft, AirspaceDatabase airspace)
    {
        // A pilot told to remain outside a Class B it is neither waiting at nor heading for is cleared into that one.
        if ((ResolveCleared(aircraft, airspace) ?? aircraft.RemainOutsideBravo?.Ident) is { } ident)
        {
            if (aircraft.IsRemainOutsideBravo(ident) && Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("{Callsign}: CLBRV into {Ident} ends its RMOB", aircraft.Callsign, ident);
            }

            aircraft.GrantBravoClearance(ident);
        }
    }

    /// <summary>
    /// How far a Class B may be from the aircraft for a <c>RMOB</c> that names no other to mean it: the Mode C veil radius
    /// (AIM 3-2-3.b.6).
    /// </summary>
    public const double RemainOutsideReachNm = 30.0;

    /// <summary>When the pilot under <c>RMOB</c> asks "any estimate on the bravo?" with no minutes given, in seconds after it.</summary>
    public const double RemainOutsideAskSeconds = 300.0;

    /// <summary>How long past the minutes a <c>RMOB {minutes}</c> gave the pilot waits before it asks, in seconds.</summary>
    public const double RemainOutsideAskAfterExpectSeconds = 60.0;

    /// <summary>
    /// The Class B a <c>RMOB</c> names and where the aircraft is against it. The Class B is the one the pilot waits on
    /// (<see cref="CaptureWait"/>), else the one it already remains outside, else the one <see cref="ResolveCleared"/>
    /// finds (laterally around it, else projected ahead), else the nearest within <see cref="RemainOutsideReachNm"/>.
    /// Inside means inside a volume of that Class B in 3D, so a pilot levelled under a shelf is outside (AIM 3-2-3.a,
    /// 3-2-3.d.2(a)).
    /// </summary>
    public static RemainOutsideResolution ResolveRemainOutside(AircraftState aircraft, AirspaceDatabase airspace)
    {
        if (RemainOutsideTarget(aircraft, airspace) is not { } ident)
        {
            return new RemainOutsideResolution(RemainOutsideOutcome.NoBravo, null);
        }

        bool inside = airspace
            .FindClassBContaining(aircraft.Position, aircraft.Altitude)
            .Any(volume => string.Equals(AreaName(volume), ident, StringComparison.OrdinalIgnoreCase));
        return new RemainOutsideResolution(inside ? RemainOutsideOutcome.Inside : RemainOutsideOutcome.Outside, ident);
    }

    /// <summary>
    /// The controller's <c>RMOB [minutes]</c>: outside the Class B <see cref="ResolveRemainOutside"/> names, the pilot is
    /// told to remain outside it (its clearance into it ends) and, when it was waiting on that Class B, schedules its one
    /// ask at <see cref="RemainOutsideAskSeconds"/>, or at the minutes plus <see cref="RemainOutsideAskAfterExpectSeconds"/>.
    /// Inside it, or near none, nothing changes.
    /// </summary>
    /// <returns>The dispatch message.</returns>
    public static string ApplyRemainOutside(AircraftState aircraft, int? minutes, AirspaceDatabase airspace, double nowSeconds)
    {
        RemainOutsideResolution resolution = ResolveRemainOutside(aircraft, airspace);
        if (resolution is not { Outcome: RemainOutsideOutcome.Outside, Ident: { } ident })
        {
            return resolution.Outcome == RemainOutsideOutcome.Inside
                ? $"Inside the {resolution.Ident} Class B: the pilot asks for intentions"
                : $"No Class B within {RemainOutsideReachNm:F0} nm: the pilot asks to say again";
        }

        bool waiting = string.Equals(CaptureWait(aircraft)?.Ident, ident, StringComparison.OrdinalIgnoreCase);
        double askAfter = minutes is { } expect ? (expect * 60.0) + RemainOutsideAskAfterExpectSeconds : RemainOutsideAskSeconds;
        aircraft.StartRemainOutsideBravo(ident, minutes, waiting ? nowSeconds + askAfter : null);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "{Callsign}: RMOB for {Ident}, minutes {Minutes}, ask due {AskDue}",
                aircraft.Callsign,
                ident,
                minutes,
                aircraft.RemainOutsideBravo?.AskDueSeconds
            );
        }

        return $"Remain outside Bravo airspace ({ident})";
    }

    /// <summary>
    /// The first vector or direct <paramref name="compound"/> applies now that takes the aircraft into the Class B it is
    /// told to remain outside within <see cref="WaitingEntryLookaheadSeconds"/>, the lookahead the boundary hold judges
    /// the same vector with, so a vector the hold will stop always draws the advisory; or null. The pilot reads its clause
    /// back as the 7110.65 §7-9-2 NOTE 1 advisory. Solo training, airborne VFR aircraft only.
    /// </summary>
    public static ParsedCommand? FindRemainOutsideConflict(
        AircraftState aircraft,
        CompoundCommand compound,
        SimScenarioState scenario,
        AirspaceDatabase airspace
    )
    {
        if ((aircraft.RemainOutsideBravo is not { } remain) || !IsEligibleForImplicitGrant(aircraft, scenario))
        {
            return null;
        }

        const double lookahead = WaitingEntryLookaheadSeconds;
        ParsedCommand? conflict = SoloTrainingEvaluator
            .EnumerateImmediatelyAppliedCommands(compound)
            .FirstOrDefault(command =>
                IsVectorOrDirect(command)
                // The aircraft's own declination, as the boundary hold converts the same assigned heading, so the advisory
                // and the hold agree on whether the vector enters.
                && (NewTrack(aircraft, command, aircraft.Declination) is { } track)
                && (
                    airspace.FindLevelTrackClassBEntry(aircraft.Position, aircraft.Altitude, track, aircraft.GroundSpeed, lookahead, remain.Ident)
                    is not null
                )
            );
        if ((conflict is not null) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Callsign}: {Command} takes it into {Ident} under RMOB: the advisory", aircraft.Callsign, conflict, remain.Ident);
        }

        return conflict;
    }

    /// <summary>The Class B a <c>RMOB</c> names, in the order <see cref="ResolveRemainOutside"/> gives; null for none.</summary>
    private static string? RemainOutsideTarget(AircraftState aircraft, AirspaceDatabase airspace) =>
        CaptureWait(aircraft)?.Ident
        ?? aircraft.RemainOutsideBravo?.Ident
        ?? ResolveCleared(aircraft, airspace)
        ?? NearestClassBWithin(aircraft.Position, RemainOutsideReachNm, airspace);

    /// <summary>The area name of the nearest Class B within <paramref name="reachNm"/> of <paramref name="position"/> laterally, or null.</summary>
    private static string? NearestClassBWithin(LatLon position, double reachNm, AirspaceDatabase airspace)
    {
        string? nearest = null;
        double best = reachNm;
        foreach ((string area, IReadOnlyList<AirspaceVolume> volumes) in airspace.ClassBAreas)
        {
            double distance = LateralDistanceNm(volumes, position);
            if (distance <= best)
            {
                best = distance;
                nearest = area;
            }
        }

        return nearest;
    }

    /// <summary>The lateral distance from <paramref name="position"/> to the nearest of <paramref name="volumes"/>; zero inside one.</summary>
    private static double LateralDistanceNm(IReadOnlyList<AirspaceVolume> volumes, LatLon position)
    {
        if (ContainsLateral(volumes, position))
        {
            return 0;
        }

        double best = double.MaxValue;
        foreach (IReadOnlyList<LatLon> ring in volumes.SelectMany(volume => volume.Rings))
        {
            for (int i = 0; i < ring.Count - 1; i++)
            {
                best = Math.Min(best, GeoMath.DistanceToSegmentFt(position, ring[i], ring[i + 1]) / GeoMath.FeetPerNm);
            }
        }

        return best;
    }

    /// <summary>
    /// Grants the Class B clearance (<see cref="AircraftState.GrantBravoClearance"/>) when an instruction
    /// <paramref name="compound"/> applies at issue time can stand as the clearance into a Class B the aircraft is not
    /// already cleared into. A vector or direct grants when it enters Class B within <see cref="EntryLookaheadSeconds"/>,
    /// or within <see cref="WaitingEntryLookaheadSeconds"/> while the pilot waits on a Class B
    /// (<paramref name="wait"/>) and the track points into that one; a vector away from it grants nothing. A pattern
    /// entry or approach clearance grants only the Class B its airport lies inside, narrowed to the one the pilot waits
    /// on, and nothing for an airport outside every Class B. Solo training, airborne VFR aircraft only.
    /// </summary>
    /// <returns>
    /// How the pilot words the clearance it took ("through" only when its open request asked to go through, "into" for
    /// a landing inside the Class B and otherwise), or null when nothing was granted.
    /// </returns>
    public static BravoClearanceWording? TryGrant(
        AircraftState aircraft,
        CompoundCommand compound,
        BravoClearanceWait? wait,
        SimScenarioState scenario,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (!IsEligibleForImplicitGrant(aircraft, scenario))
        {
            return null;
        }

        foreach (ParsedCommand command in SoloTrainingEvaluator.EnumerateImmediatelyAppliedCommands(compound))
        {
            if (!IsImpliedClearanceCommand(command))
            {
                continue;
            }

            BravoGrant? grant = IsLandingClearanceCommand(command)
                ? GrantForLanding(aircraft, command, wait, airspace, airportLookup)
                : GrantForNewTrack(aircraft, command, wait, scenario, airspace);
            // A Class B the pilot is told to remain outside is entered only on an explicit CLBRV.
            if ((grant is not null) && !aircraft.IsClearedIntoBravo(grant.Ident) && !aircraft.IsRemainOutsideBravo(grant.Ident))
            {
                aircraft.GrantBravoClearance(grant.Ident);
                return grant.Wording;
            }
        }

        return null;
    }

    /// <summary>A Class B clearance an instruction stands as: how the pilot words it, and the Class B (its area name).</summary>
    private sealed record BravoGrant(BravoClearanceWording Wording, string Ident);

    /// <summary>Solo training and an airborne VFR aircraft.</summary>
    private static bool IsEligibleForImplicitGrant(AircraftState aircraft, SimScenarioState scenario) =>
        scenario.SoloTrainingMode && !aircraft.IsOnGround && aircraft.FlightPlan.IsVfr;

    /// <summary>
    /// True when <paramref name="airportId"/> sits inside a Class B surface area — narrowed to the Class B named
    /// <paramref name="identFilter"/> when it is given — so an aircraft landing there must enter that Class B. An
    /// unknown airport is inside none.
    /// </summary>
    public static bool IsAirportInsideClassB(
        string? airportId,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup,
        string? identFilter
    ) => ClassBAtAirport(airportId, airspace, airportLookup, identFilter) is not null;

    /// <summary>
    /// The Class B surface area <paramref name="airportId"/> sits inside (narrowed to <paramref name="identFilter"/> when
    /// given), or null for an unknown airport or one inside none.
    /// </summary>
    private static AirspaceVolume? ClassBAtAirport(
        string? airportId,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup,
        string? identFilter
    )
    {
        if (string.IsNullOrWhiteSpace(airportId) || airportLookup(airportId) is not { } position)
        {
            return null;
        }

        double elevation = Data.NavigationDatabase.Instance?.GetAirportElevation(airportId) ?? 0;
        return airspace
            .FindClassBContaining(position, elevation)
            .FirstOrDefault(volume => (identFilter is null) || string.Equals(AreaName(volume), identFilter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Into" when the flight plan's destination is inside the Class B named <paramref name="ident"/>, else "through".</summary>
    public static BravoClearanceWording WordingFor(
        AircraftState aircraft,
        string ident,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    ) =>
        IsAirportInsideClassB(aircraft.FlightPlan.Destination, airspace, airportLookup, ident)
            ? BravoClearanceWording.Into
            : BravoClearanceWording.Through;

    private static BravoGrant? GrantForNewTrack(
        AircraftState aircraft,
        ParsedCommand command,
        BravoClearanceWait? wait,
        SimScenarioState scenario,
        AirspaceDatabase airspace
    )
    {
        if (NewTrack(aircraft, command, MagneticDeclination.GetDeclination(aircraft.Position, scenario.MagneticModelDateUtc)) is not { } track)
        {
            return null;
        }

        double lookahead = wait is null ? EntryLookaheadSeconds : WaitingEntryLookaheadSeconds;
        AirspaceVolume? entered = airspace.FindLevelTrackClassBEntry(
            aircraft.Position,
            aircraft.Altitude,
            track,
            aircraft.GroundSpeed,
            lookahead,
            wait?.Ident
        );
        return entered is null ? null : new BravoGrant(wait?.Wording ?? BravoClearanceWording.Into, AreaName(entered));
    }

    /// <summary>
    /// A pattern entry, traffic direction or approach clearance ends in a landing at the clearance's airport: it grants
    /// only the Class B that airport lies inside (narrowed to the Class B the pilot waits on), and nothing for an airport
    /// outside every Class B. A satellite field outside the Bravo whose path clips it is no clearance into it.
    /// </summary>
    private static BravoGrant? GrantForLanding(
        AircraftState aircraft,
        ParsedCommand command,
        BravoClearanceWait? wait,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    ) =>
        ClassBAtAirport(ClearanceAirport(aircraft, command), airspace, airportLookup, wait?.Ident) is { } volume
            ? new BravoGrant(BravoClearanceWording.Into, AreaName(volume))
            : null;

    /// <summary>
    /// The airport the clearance lands at, resolved from the command the way its handler resolves it, so a
    /// reaction-delayed dispatch (not yet applied when the grant is decided) and an immediate one agree. An approach
    /// clearance lands at its named airport, else where <see cref="CommandDispatcher.ResolveAirport"/> puts it (the
    /// filed destination first). A pattern entry or traffic direction resolves its runway at
    /// <see cref="PatternCommandHandler.ResolveAirportContext"/> (the assigned runway's airport first): a runway
    /// argument names no airport of its own, and the handler looks it up there.
    /// </summary>
    private static string ClearanceAirport(AircraftState aircraft, ParsedCommand command)
    {
        string? namedAirport = command switch
        {
            ClearedApproachCommand c => c.AirportCode,
            ClearedApproachStraightInCommand c => c.AirportCode,
            ClearedVisualApproachCommand c => c.AirportCode,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(namedAirport))
        {
            return namedAirport;
        }

        return IsApproachClearance(command) ? CommandDispatcher.ResolveAirport(aircraft) : PatternCommandHandler.ResolveAirportContext(aircraft);
    }

    private static bool IsApproachClearance(ParsedCommand command) =>
        command is ClearedApproachCommand or ClearedApproachStraightInCommand or ClearedVisualApproachCommand or PositionTurnAltitudeClearanceCommand;

    private static bool IsVectorOrDirect(ParsedCommand command) =>
        command
            is FlyHeadingCommand
                or TurnLeftCommand
                or TurnRightCommand
                or LeftTurnCommand
                or RightTurnCommand
                or DirectToCommand
                or ForceDirectToCommand
                or TurnLeftDirectToCommand
                or TurnRightDirectToCommand;

    /// <summary>
    /// The true track the instruction puts the aircraft on: the assigned heading (converted with
    /// <paramref name="declination"/>), or the initial track to the first fix.
    /// </summary>
    private static TrueHeading? NewTrack(AircraftState aircraft, ParsedCommand command, double declination) =>
        command switch
        {
            FlyHeadingCommand c => c.MagneticHeading.ToTrue(declination),
            TurnLeftCommand c => c.MagneticHeading.ToTrue(declination),
            TurnRightCommand c => c.MagneticHeading.ToTrue(declination),
            LeftTurnCommand c => aircraft.TrueHeading - c.Degrees,
            RightTurnCommand c => aircraft.TrueHeading + c.Degrees,
            DirectToCommand c => InitialTrackTo(aircraft, c.Fixes),
            ForceDirectToCommand c => InitialTrackTo(aircraft, c.Fixes),
            TurnLeftDirectToCommand c => InitialTrackTo(aircraft, c.Fixes),
            TurnRightDirectToCommand c => InitialTrackTo(aircraft, c.Fixes),
            _ => null,
        };

    private static TrueHeading? InitialTrackTo(AircraftState aircraft, List<ResolvedFix> fixes)
    {
        if (fixes.Count == 0)
        {
            return null;
        }

        ResolvedFix first = fixes[0];
        return new TrueHeading(GeoMath.BearingTo(aircraft.Position, new LatLon(first.Lat, first.Lon)));
    }
}
