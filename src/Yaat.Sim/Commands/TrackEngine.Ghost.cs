using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

// The STARS display-object bodies: ghost tracks (GHOST) and the track-reposition forms that park and re-associate an
// unsupported datablock (RPOSLOC / RPOSMOVE). They create or re-bind display state rather than mutate an owned track.
public static partial class TrackEngine
{
    /// <summary>Ghost stagger offset per runway. Starts at 0.1nm and increments by 0.1nm per ghost.</summary>
    private const double GhostStaggerIncrementNm = 0.1;

    private const double GhostStaggerStartNm = 0.1;

    /// <summary>
    /// Wire id prefix for a parked (unsupported) datablock STARS track, distinct from the <c>CALLSIGN</c> prefix used for
    /// surveillance tracks so the two never collide on the wire.
    /// </summary>
    public const string ParkedDataBlockIdPrefix = "RPOS";

    public static string ParkedDataBlockId(string callsign) => $"{ParkedDataBlockIdPrefix}{callsign}";

    /// <summary>
    /// <c>GHOST</c>: an unsupported track at a fixed point — either exactly where a CRC session put it (lat/lon) or
    /// staggered off a runway's threshold along the reciprocal (each further ghost off the same runway 0.1 nm more).
    /// Overlaid on an existing aircraft when one carries the callsign (never stealing another position's track), else a
    /// new phantom aircraft owned by <paramref name="identity"/>, returned as <see cref="GhostTrackOutcome.Created"/>.
    /// </summary>
    public static GhostTrackOutcome CreateGhostTrack(GhostTrackCommand ghost, SimulationWorld world, SimScenarioState scenario, TrackOwner identity)
    {
        var callsign = ghost.Callsign;
        double lat;
        double lon;
        string? ghostAirportId = ghost.AirportCode;
        string? ghostRunwayId = ghost.RunwayId;

        if ((ghost.Latitude is not null) && (ghost.Longitude is not null))
        {
            lat = ghost.Latitude.Value;
            lon = ghost.Longitude.Value;
        }
        else if (ghostRunwayId is not null)
        {
            var airportCode = ghostAirportId ?? scenario.PrimaryAirportId;
            if (string.IsNullOrWhiteSpace(airportCode))
            {
                return GhostTrackOutcome.Refused("No airport specified and no primary airport in scenario");
            }

            ghostAirportId = airportCode;
            var runway = NavigationDatabase.Instance.GetRunway(airportCode, ghostRunwayId);
            if (runway is null)
            {
                return GhostTrackOutcome.Refused($"Runway {ghostRunwayId} not found at {airportCode}");
            }

            int ghostCount = world
                .GetSnapshot()
                .Count(ac =>
                    ac.Ghost.IsUnsupported
                    && (ac.Ghost.RunwayId == ghostRunwayId)
                    && string.Equals(ac.Ghost.AirportId, airportCode, StringComparison.OrdinalIgnoreCase)
                );
            double offsetNm = GhostStaggerStartNm + (ghostCount * GhostStaggerIncrementNm);
            (lat, lon) = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading.ToReciprocal(), offsetNm);
        }
        else
        {
            return GhostTrackOutcome.Refused("GHOST requires runway or lat/lon");
        }

        var existing = world.FindAircraft(callsign);
        if (existing is not null)
        {
            // A ghost overlay is a display action; it must not steal a track owned by another position. Reject when
            // owned by someone else; the auto-claim below only happens when the track is unowned or already this position's.
            if ((existing.Track.Owner is not null) && !existing.Track.Owner.MatchesPosition(identity))
            {
                return new GhostTrackOutcome(NotOwnedError(existing, identity), null);
            }

            // IsOverlay distinguishes this from a pure phantom data block so the operator-facing Aircraft List keeps the row visible.
            existing.Ghost.IsUnsupported = true;
            existing.Ghost.IsOverlay = true;
            existing.Ghost.Latitude = lat;
            existing.Ghost.Longitude = lon;
            existing.Track.Owner = identity;
            existing.Ghost.AirportId = ghostAirportId;
            existing.Ghost.RunwayId = ghostRunwayId;
            return new GhostTrackOutcome(new CommandResult(true, $"Ghost overlay on {callsign}"), null);
        }

        var created = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "",
            Position = new LatLon(lat, lon),
            Altitude = 0,
            IndicatedAirspeed = 0,
            Transponder = new AircraftTransponder
            {
                Code = 0,
                AssignedCode = 0,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
            Ghost = new AircraftGhostTrack
            {
                IsUnsupported = true,
                AirportId = ghostAirportId,
                RunwayId = ghostRunwayId,
            },
            Track = new AircraftTrack { Owner = identity },
        };

        world.AddAircraft(created);
        return new GhostTrackOutcome(new CommandResult(true, $"Ghost track {callsign} created"), created);
    }

    /// <summary>
    /// STARS Track Reposition Form 2 (<c>&lt;TRK RPOS&gt;&lt;SLEW&gt;&lt;LOCATION&gt;</c>): park an associated track's
    /// datablock at a fixed location. The aircraft's surveillance track becomes a bare unassociated LDB at its real
    /// position; the broadcast layer emits a second STARS track (the unsupported datablock) at the parked location,
    /// owned by the original controller.
    /// </summary>
    public static CommandResult RepositionToLocation(RepositionToLocationCommand cmd, SimulationWorld world, TrackOwner identity)
    {
        var ac = world.FindAircraft(cmd.Callsign);
        if (ac is null)
        {
            return new CommandResult(false, "NO TRK");
        }

        // Source must be associated and still bound to its surveillance (not already parked).
        if ((ac.DataBlock.Binding != DataBlockBinding.Bound) || (ac.Track.Owner is null))
        {
            return new CommandResult(false, "ILL TRK");
        }

        if (!ac.Track.Owner.MatchesPosition(identity))
        {
            return new CommandResult(false, "ILL TRK");
        }

        var owner = ac.Track.Owner;
        ac.DataBlock.Binding = DataBlockBinding.Parked;
        ac.DataBlock.Latitude = cmd.Latitude;
        ac.DataBlock.Longitude = cmd.Longitude;
        ac.DataBlock.DetachedId = ParkedDataBlockId(ac.Callsign);
        ac.DataBlock.CreatedBy = owner;

        // Original surveillance track becomes unassociated — a bare LDB at its real position.
        ac.Track.Owner = null;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffRedirectedBy = null;
        ac.Track.Pointout = null;

        return new CommandResult(true, $"Datablock parked: {ac.Callsign}");
    }

    /// <summary>
    /// STARS Track Reposition Forms 1 &amp; 3 (re-associate a parked unsupported datablock with a surveillance track).
    /// YAAT models one surveillance source per callsign, so a parked datablock can only re-bind to its own track
    /// (matching AID) — i.e. un-park in place, the reverse of Form 2. Re-binding onto a different flight's surveillance
    /// has no analog in the single-track model and is rejected with ILL TRK.
    /// </summary>
    public static CommandResult RepositionMove(RepositionMoveCommand cmd, SimulationWorld world, TrackOwner identity)
    {
        var source = world.FindAircraft(cmd.FromCallsign);
        if (source is null)
        {
            return new CommandResult(false, "NO TRK");
        }

        // Source must be a parked (unsupported) datablock.
        if (source.DataBlock.Binding != DataBlockBinding.Parked)
        {
            return new CommandResult(false, "ILL TRK");
        }

        // Only the controller that created the unsupported datablock may move it.
        if ((source.DataBlock.CreatedBy is not null) && !source.DataBlock.CreatedBy.MatchesPosition(identity))
        {
            return new CommandResult(false, "ILL TRK");
        }

        // The datablock can only re-bind to its own surveillance track (matching AID).
        if (!cmd.ToCallsign.Equals(cmd.FromCallsign, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult(false, "ILL TRK");
        }

        // Un-park: re-associate the datablock with its surveillance track.
        source.Track.Owner = source.DataBlock.CreatedBy ?? identity;
        source.DataBlock.Binding = DataBlockBinding.Bound;
        source.DataBlock.Latitude = null;
        source.DataBlock.Longitude = null;
        source.DataBlock.DetachedId = null;
        source.DataBlock.CreatedBy = null;

        return new CommandResult(true, $"Datablock re-associated: {source.Callsign}");
    }
}

/// <summary>What a <c>GHOST</c> produced: the controller-facing result and the phantom aircraft it created (null for an overlay or a refusal).</summary>
public readonly record struct GhostTrackOutcome(CommandResult Result, AircraftState? Created)
{
    public static GhostTrackOutcome Refused(string message) => new(new CommandResult(false, message), null);
}
