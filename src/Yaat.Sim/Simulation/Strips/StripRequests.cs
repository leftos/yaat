using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// Where a requested strip prints: the aircraft and scenario it is for, the bay facility that owns it, whether
/// that facility shows the destination in field 8, and whether the request takes the arrival format (and with
/// what ETA) or the departure one.
/// </summary>
public readonly record struct StripRequestPlan(
    AircraftState Aircraft,
    SimScenarioState Scenario,
    string FacilityId,
    bool ShowDestination,
    bool IsArrival,
    double EtaMinutes
);

/// <summary>
/// The manual "Request Strip" body: resolving which bay and which format a request takes, and printing the strip a
/// <see cref="RecordedStripRequest"/> carries. Engine state decides both, so the live mint (the hub RPC and the CRC
/// vStrips handler, which draw the id and record it) and every replay of that record agree on what gets printed.
/// </summary>
public static class StripRequests
{
    /// <summary>
    /// The one body both strip-request entry points resolve through, so the live mint and the recorded print agree on
    /// which bay and which format the request takes. A null <paramref name="facilityId"/> is the training-hub form —
    /// the student's own (non-external) bay, arrival format for an airborne inbound to the primary airport and
    /// departure format otherwise. A facility is the CRC vStrips form: always a departure strip for that facility.
    /// </summary>
    public static CommandResult ResolveStripRequest(SimulationEngine engine, string callsign, string? facilityId, out StripRequestPlan plan)
    {
        plan = default;
        if (string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "Aircraft callsign required");
        }
        var ac = engine.FindAircraft(callsign);
        if (ac is null)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        var config = scenario.ArtccConfig;
        if (facilityId is not null)
        {
            var crcShowDest = config?.FindFacility(facilityId)?.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;
            plan = new StripRequestPlan(ac, scenario, facilityId, crcShowDest, IsArrival: false, EtaMinutes: 0);
            return new CommandResult(true);
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (config is null || string.IsNullOrEmpty(positionCallsign))
        {
            return new CommandResult(false, "No active student position");
        }
        var accessible = config.GetAllAccessibleStripBays(positionCallsign);
        var ownBay = accessible.FirstOrDefault(b => !b.IsExternal);
        if (ownBay is null)
        {
            return new CommandResult(false, "No own strip bay for current position");
        }

        var showDest = ownBay.Owner.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;
        if (!IsArrivalCandidate(ac, scenario) || scenario.PrimaryAirportId is not { } primaryAirportId)
        {
            plan = new StripRequestPlan(ac, scenario, ownBay.Owner.Id, showDest, IsArrival: false, EtaMinutes: 0);
            return new CommandResult(true);
        }

        var airportPos = ResolveAirportPosition(primaryAirportId);
        double etaMinutes = 0.0;
        if (airportPos is not null && ac.GroundSpeed >= 30.0)
        {
            var distanceNm = GeoMath.DistanceNm(ac.Position, new LatLon(airportPos.Value.Lat, airportPos.Value.Lon));
            etaMinutes = (distanceNm / ac.GroundSpeed) * 60.0;
        }
        plan = new StripRequestPlan(ac, scenario, ownBay.Owner.Id, showDest, IsArrival: true, EtaMinutes: etaMinutes);
        return new CommandResult(true);
    }

    /// <summary>
    /// The mutation behind a <see cref="RecordedStripRequest"/>: prints the strip under the id the record carries. The
    /// id decides the format — an <c>ARRIVAL_</c> id prints its one arrival strip and moves it to the printer-queue
    /// tail (so a repeat request resurfaces a lost strip), a <c>STRIP_</c> id prints a fresh departure copy — because
    /// the aircraft's own arrival/departure verdict can flip between the live mint and this apply (a rewind re-runs
    /// the record from a different tick), which would print one format under the other's id. A departure prints
    /// nothing when the engine already holds the recorded id: a restore can carry the strip in (the snapshot covers
    /// it), and the record is re-applied over that state, so the apply has to be idempotent or it would stack a
    /// duplicate. What this printed reaches the host through the change tracker, which is why the caller needs only
    /// the verdict.
    /// </summary>
    public static CommandResult PrintRequestedStrip(SimulationEngine engine, RecordedStripRequest request)
    {
        var resolved = ResolveStripRequest(engine, request.Callsign, request.FacilityId, out var plan);
        if (!resolved.Success)
        {
            return resolved;
        }

        var isArrival = request.StripId.StartsWith(StripMutations.ArrivalStripIdPrefix, StringComparison.Ordinal);
        var printed = $"Flight strip printed for {request.Callsign}";
        if (!isArrival && engine.Strips.Items.ContainsKey(request.StripId))
        {
            return new CommandResult(true, printed);
        }

        StripItemRecord? record;
        if (isArrival)
        {
            // The ETA is the plan's — zero when the plan no longer sees an arrival to measure one for.
            record = StripMutations.RequestArrivalStripForAircraft(
                engine.Strips,
                plan.Aircraft,
                plan.Scenario,
                plan.EtaMinutes,
                plan.FacilityId,
                request.StripId
            );
            if (record is not null)
            {
                StripMutations.EnqueueArrivalPrinter(engine.Strips, record.Id);
            }
        }
        else
        {
            record = StripMutations.PrintDepartureStripForAircraft(
                engine.Strips,
                plan.Aircraft,
                plan.Scenario,
                plan.FacilityId,
                plan.ShowDestination,
                request.StripId
            );
        }

        return record is null ? new CommandResult(false, $"Could not print flight strip for {request.Callsign}") : new CommandResult(true, printed);
    }

    /// <summary>
    /// Whether the aircraft is an arrival for the scenario's primary airport — airborne and filed into it. A rolled-out
    /// landing is on the ground and therefore no longer a candidate, which is what keeps a repeat request for it on
    /// the departure format.
    /// </summary>
    public static bool IsArrivalCandidate(AircraftState ac, SimScenarioState scenario)
    {
        // A live-traffic shadow is real traffic: no auto strip for it (every real arrival would print one).
        if (ac.IsOnGround || ac.IsShadow)
        {
            return false;
        }
        return NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Destination, scenario.PrimaryAirportId);
    }

    /// <summary>The airport reference point an arrival's ETA is measured to, or null when the navdata carries no runway for it.</summary>
    public static (double Lat, double Lon)? ResolveAirportPosition(string airportCode)
    {
        var navDb = NavigationDatabase.Instance;
        var runways = navDb.GetRunways(airportCode);
        if (runways.Count == 0)
        {
            return null;
        }

        // Use the first runway's near-end coords as the airport reference. Good enough
        // for ETA estimation at 20-minute scale where runway-level precision is noise.
        var runway = runways[0];
        return (runway.Lat1, runway.Lon1);
    }
}
