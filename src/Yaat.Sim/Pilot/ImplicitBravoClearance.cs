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
            BravoClearanceWording asked = request.LastPilotLine.Contains(PilotResponder.BravoThroughRequest, StringComparison.Ordinal)
                ? BravoClearanceWording.Through
                : BravoClearanceWording.Into;
            return new BravoClearanceWait(requested, asked);
        }

        return aircraft.Phases?.CurrentPhase is AirspaceBoundaryHoldPhase { AirspaceClass: AirspaceClass.Bravo, Ident: { Length: > 0 } held }
            ? new BravoClearanceWait(held, BravoClearanceWording.Into)
            : null;
    }

    /// <summary>
    /// Sets <see cref="AircraftState.IsClearedIntoBravo"/> when an instruction <paramref name="compound"/> applies at
    /// issue time can stand as the clearance. While the pilot waits on a Class B (<paramref name="wait"/>), a pattern
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

            BravoClearanceWording? wording = IsVectorOrDirect(command)
                ? GrantForNewTrack(aircraft, command, wait, scenario, airspace)
                : GrantForLanding(aircraft, command, wait, airspace, airportLookup);
            if (wording is not null)
            {
                aircraft.IsClearedIntoBravo = true;
                return wording;
            }
        }

        return null;
    }

    /// <summary>Solo training, an airborne VFR aircraft, and no Class B clearance held yet.</summary>
    private static bool IsEligibleForImplicitGrant(AircraftState aircraft, SimScenarioState scenario) =>
        scenario.SoloTrainingMode && !aircraft.IsClearedIntoBravo && !aircraft.IsOnGround && aircraft.FlightPlan.IsVfr;

    /// <summary>
    /// True when <paramref name="airportId"/> sits inside a Class B surface area — narrowed to the Class B named
    /// <paramref name="identFilter"/> when it is given — so an aircraft landing there must enter that Class B. An
    /// unknown airport is inside none.
    /// </summary>
    public static bool IsAirportInsideClassB(string? airportId, AirspaceDatabase airspace, Func<string, LatLon?> airportLookup, string? identFilter)
    {
        if (string.IsNullOrWhiteSpace(airportId) || airportLookup(airportId) is not { } position)
        {
            return false;
        }

        double elevation = Data.NavigationDatabase.Instance?.GetAirportElevation(airportId) ?? 0;
        return airspace
            .FindClassBContaining(position, elevation)
            .Any(volume => identFilter is null || string.Equals(volume.Ident, identFilter, StringComparison.OrdinalIgnoreCase));
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

    private static BravoClearanceWording? GrantForNewTrack(
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
        return entered is null ? null : wait?.Wording ?? BravoClearanceWording.Into;
    }

    /// <summary>
    /// A pattern entry, traffic direction or approach clearance ends in a landing at the clearance's airport: it grants
    /// while the pilot waits on a Class B, or when that airport lies inside one.
    /// </summary>
    private static BravoClearanceWording? GrantForLanding(
        AircraftState aircraft,
        ParsedCommand command,
        BravoClearanceWait? wait,
        AirspaceDatabase airspace,
        Func<string, LatLon?> airportLookup
    )
    {
        if (IsAirportInsideClassB(ClearanceAirport(aircraft, command), airspace, airportLookup, wait?.Ident))
        {
            return BravoClearanceWording.Into;
        }

        return wait?.Wording;
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
