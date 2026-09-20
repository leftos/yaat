using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Spine;

namespace Yaat.Sim.Simulation;

// The ASDE-X and SAAB SAID half of the engine: the CRC-sourced mutations a controller's surface display sends, and the
// room-wide alert-inhibit sweep ASDXALERTS is. Everything they write is per-aircraft AircraftStarsState — snapshotted,
// so a replay, a rewind and a session restore all carry it — through the same TrackEngine bodies the typed ASDX verbs
// use. The one thing that leaves the simulation is a terminate: the live room turns the consumer call into its
// one-shot delete marker, which the broadcaster drains into DeleteAsdexTracks / DeleteSaabSaidTracks.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies one recorded CRC ASDE-X mutation. <c>EditDbFields</c> writes every display-field override the record
    /// carries (a null field is one the record does not touch); the per-aircraft verbs move the tag / terminate /
    /// suspend / alert-inhibit bits; <c>EnableAllAlerts</c> is the room-wide sweep. A mutation naming an aircraft the
    /// world no longer holds applies nothing, silently — the display it came from is one frame behind the world.
    /// <paramref name="host"/> is told about a terminate, and about nothing else.
    /// </summary>
    public void ApplyAsdexMutation(RecordedAsdexMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnAsdexAircraft(mutation, ac => ApplyAsdexEdit(ac, mutation));
                break;
            case "Tag":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Tag));
                break;
            case "Terminate":
                OnAsdexAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Terminate);
                        host.OnAsdexTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Suspend));
                break;
            case "Unsuspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Unsuspend));
                break;
            case "InhibitAlerts":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.InhibitAlerts));
                break;
            case "EnableAllAlerts":
                EnableAllAsdexAlerts();
                break;
        }
    }

    /// <summary>
    /// Applies one recorded CRC SAAB SAID mutation — the same shape as the ASDE-X one on the <c>Said*</c> fields,
    /// with no alerts to inhibit or enable. <paramref name="host"/> is told about a terminate.
    /// </summary>
    public void ApplySaidMutation(RecordedSaidMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnSaidAircraft(mutation, ac => ApplySaidEdit(ac, mutation));
                break;
            case "Tag":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Tag));
                break;
            case "Terminate":
                OnSaidAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleSaidVerb(ac, SaidVerb.Terminate);
                        host.OnSaidTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Suspend));
                break;
            case "Unsuspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Unsuspend));
                break;
        }
    }

    /// <summary>
    /// Applies one recorded CRC ASDE-X safety-logic configuration push: the facility's runway footprints, active
    /// runway configuration and inhibited arrival-alert positions become the scenario's configuration, which the
    /// surface-alert detector reads. Snapshotted scenario state, so a rewind onto a snapshot that predates the push
    /// rebuilds the configuration from the log rather than keeping a stale one. With no scenario loaded there is
    /// nothing to write to and the push is dropped.
    /// </summary>
    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change)
    {
        if (Scenario is not { } scenario)
        {
            _logger.LogDebug(
                "ASDE-X safety-logic push for facility {FacilityId} at t={Seconds} dropped: no scenario is loaded",
                change.FacilityId,
                change.ElapsedSeconds
            );
            return;
        }

        scenario.AsdexSafetyLogicConfig = change.Config;
    }

    /// <summary>Airports farther than this from a configured runway footprint are not its owner.</summary>
    private const double AsdexRunwayAirportSearchNm = 5;

    /// <summary>
    /// The post-physics ASDE-X Safety Logic pass: the configured runway footprints plus the ground layout's taxiways
    /// go to the stateless detector, and what it finds is diffed against the alerts the scenario is already holding.
    /// Only the difference leaves the engine — the alerts that appeared and the ids that went away — because CRC's
    /// alert topic is additive with an explicit delete. Runs on every run kind: the standing set is snapshotted
    /// scenario state, so a replay, a rewind and a session restore all reach the alert picture the live room had.
    /// </summary>
    public void TickAsdexAlerts(IHostConsumers host)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        AsdexSafetyLogicConfig? config = scenario.AsdexSafetyLogicConfig;

        // No active safety-logic config (no CRC ASDE-X driving it) — clear any stale alerts.
        if ((config is null) || (config.Runways.Count == 0))
        {
            ImmutableSortedDictionary<string, AsdexSafetyAlert> standing = scenario.ActiveAsdexAlerts;
            if (standing.Count > 0)
            {
                var stale = standing.Keys.ToList();
                scenario.ActiveAsdexAlerts = standing.Clear();
                host.OnAsdexAlertsChanged([], stale);
            }

            return;
        }

        if (NavigationDatabase.InstanceOrNull is not { } navDb)
        {
            return;
        }

        List<AircraftState> aircraft = World.GetSnapshot();
        double fieldElevationFt = aircraft.Count > 0 ? FieldElevationResolver.Resolve(aircraft[0], navDb) : 0;
        List<AsdexRunwaySurface> runways = BuildAsdexRunwaySurfaces(config.Runways, navDb, fieldElevationFt);
        List<AsdexTaxiwaySegment> taxiways = BuildAsdexTaxiwaySegments(World.GroundLayout);

        ApplyAsdexDetection(scenario, AsdexSafetyLogicDetector.Detect(runways, taxiways, aircraft, fieldElevationFt), host);
    }

    /// <summary>
    /// Folds one tick's detector findings into the scenario's standing set and tells the host what moved. An alert
    /// already standing is left as it is (its id is the identity the display keyed on); one the detector no longer
    /// reports is cleared by id. A tick that moves nothing leaves the standing set's reference alone, so a reader
    /// holding it keeps reading the same instance.
    /// </summary>
    private void ApplyAsdexDetection(SimScenarioState scenario, IReadOnlyList<AsdexSafetyAlert> detected, IHostConsumers host)
    {
        ImmutableSortedDictionary<string, AsdexSafetyAlert> active = scenario.ActiveAsdexAlerts;
        var detectedById = detected.ToDictionary(alert => alert.Id);

        var newAlerts = detected.Where(alert => !active.ContainsKey(alert.Id)).ToList();
        var clearedIds = active.Keys.Where(id => !detectedById.ContainsKey(id)).ToList();

        if ((newAlerts.Count == 0) && (clearedIds.Count == 0))
        {
            return;
        }

        var next = active.ToBuilder();
        foreach (AsdexSafetyAlert alert in newAlerts)
        {
            next[alert.Id] = alert;
            _logger.LogWarning("ASDE-X Safety Logic alert: {Kind} {Lines}", alert.Kind, string.Join(" / ", alert.MessageLines));
        }

        foreach (string id in clearedIds)
        {
            next.Remove(id);
        }

        scenario.ActiveAsdexAlerts = next.ToImmutable();
        host.OnAsdexAlertsChanged(newAlerts, clearedIds);
    }

    private static List<AsdexRunwaySurface> BuildAsdexRunwaySurfaces(
        IReadOnlyList<AsdexRunwayConfig> runways,
        NavigationDatabase navDb,
        double fieldElevationFt
    )
    {
        var surfaces = new List<AsdexRunwaySurface>(runways.Count);
        foreach (AsdexRunwayConfig runway in runways)
        {
            if (runway.AreaPoints.Count == 0)
            {
                continue;
            }

            var area = runway.AreaPoints.ToList();
            var centroid = new LatLon(area.Average(point => point.Lat), area.Average(point => point.Lon));
            double variation = MagneticDeclination.GetDeclination(centroid.Lat, centroid.Lon);
            double elevation = ResolveAsdexRunwayElevation(runway.Id, centroid, navDb) ?? fieldElevationFt;
            surfaces.Add(new AsdexRunwaySurface(runway.Id, area, runway.IsClosed, variation, elevation));
        }

        return surfaces;
    }

    /// <summary>The CRC safety-logic config names runways without an airport, so the owning airport is the
    /// nearest one to the footprint; the runway end's threshold elevation is the AGL datum for arrivals over it.</summary>
    private static double? ResolveAsdexRunwayElevation(string runwayId, LatLon centroid, NavigationDatabase navDb)
    {
        (string Id, double Lat, double Lon)? airport = navDb.FindNearestSizeableAirport(
            centroid,
            minRunwayLengthFt: 0,
            maxRangeNm: AsdexRunwayAirportSearchNm
        );
        return airport is null ? null : navDb.GetRunway(airport.Value.Id, runwayId)?.ElevationFt;
    }

    private static List<AsdexTaxiwaySegment> BuildAsdexTaxiwaySegments(AirportGroundLayout? layout)
    {
        if (layout is null)
        {
            return [];
        }

        var segments = new List<AsdexTaxiwaySegment>();
        foreach (GroundEdge edge in layout.Edges)
        {
            // Only true taxiways: not runway centerlines, runway-crossing links, or ramps.
            if (edge.IsRunwayCenterline || edge.IsRunwayCrossingLink || edge.IsRamp || (edge.Nodes.Length < 2))
            {
                continue;
            }

            LatLon start = edge.Nodes[0].Position;
            LatLon end = edge.Nodes[^1].Position;
            segments.Add(new AsdexTaxiwaySegment(edge.TaxiwayName, start, end));
        }

        return segments;
    }

    /// <summary>
    /// <c>ASDXALERTS</c> and a recorded <c>EnableAllAlerts</c>: clears every aircraft's ASDE-X alert inhibit. A pure
    /// per-aircraft sweep — there is no room-wide "alerts enabled" bit to hold.
    /// </summary>
    public CommandResult EnableAllAsdexAlerts()
    {
        foreach (AircraftState ac in World.GetSnapshot())
        {
            ac.Stars.AsdexAlertsInhibited = false;
        }

        return new CommandResult(true, "ASDX alerts enabled");
    }

    /// <summary>
    /// Resolves the mutation's aircraft by callsign alone. A surface mutation names a track, and a surface track id is
    /// the callsign: <c>AsdexTrackDto.Id</c> is <c>"CALLSIGN{callsign}"</c>, which the CRC handler strips before it
    /// builds the record (<c>CrcClientState.Asdex.cs</c>, <c>ReadTrackIdArg</c> / <c>ReadAircraftIdArg</c>), and an
    /// edit prefers the DTO's bare callsign field. Nothing on that wire carries a CID or a beacon code, so the
    /// room's FLID-aware resolver has nothing to add here.
    /// </summary>
    private void OnAsdexAircraft(RecordedAsdexMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private void OnSaidAircraft(RecordedSaidMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private static void ApplyAsdexEdit(AircraftState ac, RecordedAsdexMutation mutation)
    {
        Write(ac, AsdexEditField.Callsign, mutation.Callsign);
        Write(ac, AsdexEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, AsdexEditField.Category, mutation.Category);
        Write(ac, AsdexEditField.AircraftType, mutation.AircraftType);
        Write(ac, AsdexEditField.Fix, mutation.Fix);
        Write(ac, AsdexEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, AsdexEditField.Scratchpad2, mutation.Scratchpad2);
    }

    private static void ApplySaidEdit(AircraftState ac, RecordedSaidMutation mutation)
    {
        Write(ac, SaidEditField.Callsign, mutation.Callsign);
        Write(ac, SaidEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, SaidEditField.Category, mutation.Category);
        Write(ac, SaidEditField.AircraftType, mutation.AircraftType);
        Write(ac, SaidEditField.Fix, mutation.Fix);
        Write(ac, SaidEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, SaidEditField.Scratchpad2, mutation.Scratchpad2);
    }

    /// <summary>A field the record does not carry is left alone; one it carries is written as it stands, empty included.</summary>
    private static void Write(AircraftState ac, AsdexEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetAsdexField(ac, field, value);
        }
    }

    private static void Write(AircraftState ac, SaidEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetSaidField(ac, field, value);
        }
    }
}
