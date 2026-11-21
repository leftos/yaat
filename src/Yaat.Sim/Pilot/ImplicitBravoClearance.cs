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
/// Solo training: a controller instruction that can only be flown by entering Class B is taken as the Class B
/// clearance for a VFR aircraft that does not hold one yet. The student playing controller vectors, clears direct, or
/// clears for an approach or pattern entry at an airport inside the Bravo; without this the pilot flies the
/// instruction to the boundary, orbits outside it, and turns away again every time the instruction is repeated. The
/// pilot reads the grant back ("cleared into the bravo"), so the student hears what was taken as a clearance.
/// </summary>
public static class ImplicitBravoClearance
{
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
        if (ResolveCleared(aircraft, airspace) is { } ident)
        {
            aircraft.GrantBravoClearance(ident);
        }
    }

    /// <summary>
    /// Grants the Class B clearance (<see cref="AircraftState.GrantBravoClearance"/>) when an instruction
    /// <paramref name="compound"/> applies at issue time can stand as the clearance into a Class B the aircraft is not
    /// already cleared into. While the pilot waits on a Class B (<paramref name="wait"/>), a pattern
    /// entry or approach clearance grants outright and a vector or direct grants only when it points into that Class B
    /// within <see cref="WaitingEntryLookaheadSeconds"/>; a vector away from it grants nothing. Otherwise a vector or
    /// direct grants when it enters Class B within <see cref="EntryLookaheadSeconds"/>, and a pattern entry or approach
    /// clearance when its airport lies inside Class B. Solo training, airborne VFR aircraft only.
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

            BravoGrant? grant = IsVectorOrDirect(command)
                ? GrantForNewTrack(aircraft, command, wait, scenario, airspace)
                : GrantForLanding(aircraft, command, wait, airspace, airportLookup);
            if ((grant is not null) && !aircraft.IsClearedIntoBravo(grant.Ident))
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
        if (NewTrack(aircraft, command, scenario) is not { } track)
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
    /// the Class B that airport lies inside, else, while the pilot waits on a Class B, that one.
    /// </summary>
    private static BravoGrant? GrantForLanding(
        AircraftState aircraft,
        ParsedCommand command,
        BravoClearanceWait? wait,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (ClassBAtAirport(ClearanceAirport(aircraft, command), airspace, airportLookup, wait?.Ident) is { } volume)
        {
            return new BravoGrant(BravoClearanceWording.Into, AreaName(volume));
        }

        return wait is null ? null : new BravoGrant(wait.Wording, wait.Ident);
    }

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

    /// <summary>The true track the instruction puts the aircraft on: the assigned heading, or the initial track to the first fix.</summary>
    private static TrueHeading? NewTrack(AircraftState aircraft, ParsedCommand command, SimScenarioState scenario)
    {
        double declination = MagneticDeclination.GetDeclination(aircraft.Position, scenario.MagneticModelDateUtc);
        return command switch
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
    }

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
