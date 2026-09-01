using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// Which AI-staffed position is responsible for an aircraft. Tower-cab jurisdiction follows the phase family
/// (7110.65 §3-1-3: local control owns the active runway and every use of it other than a crossing; ground control
/// owns the movement area short of it), radar jurisdiction follows track ownership, and a phase-less aircraft near a
/// staffed cab is classified by its use of a runway (<see cref="RunwayOccupancy.ClassifyBest"/>). Null means nobody
/// the AI plays is responsible: a shadow, an aircraft assigned to a human connection, a human-owned track, a phase
/// family no staffed position covers, or another airport.
/// </summary>
public static class PositionJurisdiction
{
    public static AiPositionConfig? Resolve(
        AircraftState aircraft,
        IReadOnlyList<AiPositionConfig> staffed,
        Func<AircraftState, AirportGroundLayout?> layoutFor,
        Func<string?, IReadOnlyList<RunwayInfo>> runwaysFor,
        Func<TrackOwner, bool> isHumanHeld,
        Func<string, bool> isAssignedToHuman
    )
    {
        if (aircraft.IsShadow || (staffed.Count == 0) || isAssignedToHuman(aircraft.Callsign))
        {
            return null;
        }

        var runwayAirport = (aircraft.Phases?.DepartureRunway ?? aircraft.Phases?.AssignedRunway)?.AirportId;
        var airport = runwayAirport ?? PilotContactRoster.SurfaceAirportOf(aircraft);
        var phase = aircraft.Phases?.CurrentPhase;
        var phaseRole = phase is null ? null : RoleForPhase(phase);
        if (phaseRole is { } cabRole)
        {
            // §3-1-3.a.4: taxiing or holding ON or ALONG a runway (anything but crossing it) is local control's,
            // whatever ground phase is driving the aircraft — a back-taxi, a CLRWY pull-forward, an on-runway hold.
            if ((cabRole == ControlRole.Ground) && RunwayOccupancy.IsAlongRunway(aircraft, runwaysFor(airport)))
            {
                cabRole = ControlRole.Local;
            }

            if (CabPosition(staffed, cabRole, airport) is { } cab)
            {
                return cab;
            }
        }

        // No staffed cab position for the phase family (partial staffing) or no cab family at all: the position that
        // owns the track is responsible — an AI approach keeps its arrivals through final and its departures through
        // the initial climb when nobody plays the tower.
        if (aircraft.Track.Owner is { } owner)
        {
            if (isHumanHeld(owner))
            {
                return null;
            }

            var radar = staffed.FirstOrDefault(p => (p.Role is ControlRole.Approach or ControlRole.Center) && p.Identity.MatchesPosition(owner));
            if (radar is not null)
            {
                return radar;
            }
        }

        if (phaseRole is not null)
        {
            return null;
        }

        foreach (var candidate in CandidateAirports(aircraft, airport))
        {
            var runways = runwaysFor(candidate);
            if (runways.Count == 0)
            {
                continue;
            }

            var use = RunwayOccupancy.ClassifyBest(aircraft, runways, layoutFor(aircraft));
            if (use is null)
            {
                continue;
            }

            return CabPosition(staffed, use.Kind == RunwayUseKind.Crossing ? ControlRole.Ground : ControlRole.Local, candidate);
        }

        return null;
    }

    /// <summary>
    /// The cab role a phase places the aircraft under, or null when the phase is not a tower-cab phase (en-route
    /// navigation, holds, procedures — track ownership decides those). Two transfer points are simplified for the
    /// observer milestone and move with the brains that issue the transfers: a runway exit becomes Ground's the moment
    /// the aircraft leaves the centerline (AIM 4-3-21.b/c has the pilot stay with the tower until fully clear of the
    /// runway and told to change), and a hold-short at the departure runway becomes Local's before Ground's <c>CT</c>
    /// (the transfer marker plan 02 gives the AI Ground brain).
    /// </summary>
    public static ControlRole? RoleForPhase(Phase phase) =>
        phase switch
        {
            HoldingShortPhase holdingShort => holdingShort.HoldShort.Reason == HoldShortReason.DestinationRunway
                ? ControlRole.Local
                : ControlRole.Ground,
            RunwayExitPhase exit => exit.IsOnCenterline ? ControlRole.Local : ControlRole.Ground,
            AtParkingPhase
            or PushbackPhase
            or PushbackToSpotPhase
            or HoldingAfterPushbackPhase
            or TaxiingPhase
            or FollowingPhase
            or HoldingAfterExitPhase
            or HoldingInPositionPhase
            or CrossingRunwayPhase
            or ClearRunwayPhase
            or AirTaxiPhase => ControlRole.Ground,
            LineUpPhase
            or LinedUpAndWaitingPhase
            or TakeoffPhase
            or RejectedTakeoffPhase
            or HelicopterTakeoffPhase
            or InitialClimbPhase
            or RunwayHoldingPhase => ControlRole.Local,
            _ when TowerCabPhases.IsArrivalSide(phase) => ControlRole.Local,
            _ => null,
        };

    private static AiPositionConfig? CabPosition(IReadOnlyList<AiPositionConfig> staffed, ControlRole role, string? airport)
    {
        if (string.IsNullOrWhiteSpace(airport))
        {
            return null;
        }

        return staffed.FirstOrDefault(p => (p.Role == role) && p.AirportIds.Any(id => NavigationDatabase.AirportIdsMatch(id, airport)));
    }

    private static IEnumerable<string> CandidateAirports(AircraftState aircraft, string? airport)
    {
        if (!string.IsNullOrWhiteSpace(airport))
        {
            yield return airport;
        }

        if (!aircraft.IsOnGround && !string.IsNullOrWhiteSpace(aircraft.FlightPlan.Destination))
        {
            yield return aircraft.FlightPlan.Destination;
        }
    }
}

/// <summary>
/// One tick's view of the world for the AI brains: the aircraft sorted by callsign (ordinal, so every brain iterates
/// in the same order every run) and each aircraft's responsible AI position.
/// </summary>
public sealed class AiWorldView
{
    private static readonly IReadOnlyList<AircraftState> None = [];

    private readonly Dictionary<string, AiPositionConfig?> _responsible = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AircraftState>> _byPosition = new(StringComparer.Ordinal);

    private AiWorldView(IReadOnlyList<AircraftState> snapshot)
    {
        Snapshot = snapshot;
    }

    public IReadOnlyList<AircraftState> Snapshot { get; }

    public static AiWorldView Build(
        IReadOnlyList<AircraftState> aircraft,
        IReadOnlyList<AiPositionConfig> staffed,
        Func<AircraftState, AirportGroundLayout?> layoutFor,
        Func<string?, IReadOnlyList<RunwayInfo>> runwaysFor,
        Func<TrackOwner, bool> isHumanHeld,
        Func<string, bool> isAssignedToHuman
    )
    {
        var view = new AiWorldView(aircraft.OrderBy(ac => ac.Callsign, StringComparer.Ordinal).ToList());
        foreach (var ac in view.Snapshot)
        {
            var position = PositionJurisdiction.Resolve(ac, staffed, layoutFor, runwaysFor, isHumanHeld, isAssignedToHuman);
            view._responsible[ac.Callsign] = position;
            if (position is not null)
            {
                if (!view._byPosition.TryGetValue(position.PositionId, out var list))
                {
                    list = [];
                    view._byPosition[position.PositionId] = list;
                }

                list.Add(ac);
            }
        }

        return view;
    }

    public AiPositionConfig? ResponsiblePosition(AircraftState aircraft) => _responsible.GetValueOrDefault(aircraft.Callsign);

    public IReadOnlyList<AircraftState> Jurisdiction(AiPositionConfig position) =>
        _byPosition.TryGetValue(position.PositionId, out var list) ? list : None;
}
