using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Simulation;

// The flight-strip spine steps — the arrival auto-print sweep, the approach student's takeoff-roll print and the
// deferred strip dispatch — plus the spawn hook's auto-print and the reprint a flight-plan amendment owes. They decide
// from engine state alone (the session clock, the scenario's student position, the ARTCC's bay configuration, the
// world), so every run kind builds the same racks and printer queues; what the mutations touched is drained to the
// host from <see cref="DrainStateChangesInto"/>.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Per-tick arrival strip auto-print check: for every airborne aircraft whose
    /// destination is the scenario's primary airport, computes ETA from great-circle
    /// distance ÷ ground speed and prints an arrival strip when ETA drops below
    /// <see cref="StripMutations.ArrivalAutoPrintMinutes"/>. Idempotent per-aircraft —
    /// <see cref="StripMutations.RequestArrivalStripForAircraft"/> returns early once a
    /// strip exists. Matches the real vStrips behavior described in docs/crc/vstrips.md
    /// Arrival Flight Strips section.
    /// </summary>
    public void TickAutoArrivalStrips()
    {
        if (Scenario is not { ArtccConfig: { } config } scenario || string.IsNullOrEmpty(scenario.PrimaryAirportId))
        {
            return;
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (string.IsNullOrEmpty(positionCallsign))
        {
            return;
        }

        var accessible = config.GetAllAccessibleStripBays(positionCallsign);
        if (accessible.Count == 0)
        {
            return;
        }

        var airportPos = StripRequests.ResolveAirportPosition(scenario.PrimaryAirportId);
        if (airportPos is null)
        {
            return;
        }

        // Auto-print attribution: use the first *own* bay's facility (never an
        // external link). Arrival strips belong to the student's own facility.
        var ownBay = accessible.FirstOrDefault(b => !b.IsExternal);
        if (ownBay is null)
        {
            return;
        }

        // The vNAS config gates arrival strips per facility (e.g. ZOA's OAK ATCT has them off —
        // real vStrips never auto-prints arrivals there). Manual "Request Strip" remains available.
        if (ownBay.Owner.FlightStripsConfiguration?.EnableArrivalStrips != true)
        {
            return;
        }
        var (airportLat, airportLon) = airportPos.Value;
        var facilityId = ownBay.Owner.Id;

        foreach (var ac in World.GetSnapshot())
        {
            if (!StripRequests.IsArrivalCandidate(ac, scenario))
            {
                continue;
            }

            var distanceNm = GeoMath.DistanceNm(ac.Position, new LatLon(airportLat, airportLon));
            var groundSpeed = ac.GroundSpeed;
            if (groundSpeed < 30.0)
            {
                continue;
            }

            var etaMinutes = (distanceNm / groundSpeed) * 60.0;
            if (etaMinutes > StripMutations.ArrivalAutoPrintMinutes)
            {
                continue;
            }

            var stripId = StripMutations.MintStripId(Strips, ac.Callsign, isArrival: true);
            StripMutations.RequestArrivalStripForAircraft(Strips, ac, scenario, etaMinutes, facilityId, stripId);
        }
    }

    /// <summary>
    /// Approach-student departure strip auto-print. The tower's "rolling call"
    /// to approach is the moment a departure begins its takeoff roll: that's
    /// when approach starts the strip scan. We mirror that by creating the
    /// departure strip directly into the approach student's matching facility
    /// bay (e.g. "Friant" bay for FAT_F_APP) when the aircraft enters
    /// <see cref="TakeoffPhase"/> or <see cref="HelicopterTakeoffPhase"/>.
    /// Idempotent per-callsign — RequestDepartureStripForAircraftIntoBay
    /// returns the existing record on subsequent ticks.
    /// </summary>
    public void TickAutoApproachDepartureStrips()
    {
        if (Scenario is not { ArtccConfig: { } config } scenario || scenario.StudentPositionType is not "APP")
        {
            return;
        }

        if (string.IsNullOrEmpty(scenario.PrimaryAirportId))
        {
            return;
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (string.IsNullOrEmpty(positionCallsign))
        {
            return;
        }

        var posConfig = config.FindPositionByCallsign(positionCallsign);
        var posName = posConfig?.Name;
        if (string.IsNullOrEmpty(posName))
        {
            return;
        }

        var bay = config.FindFirstOwnBayWithNamePrefix(positionCallsign, posName);
        if (bay is null)
        {
            return;
        }

        var showDest = bay.Owner.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;

        foreach (var ac in World.GetSnapshot())
        {
            if (!IsApproachDepartureCandidate(ac, scenario))
            {
                continue;
            }

            if (Strips.Items.ContainsKey($"STRIP_{ac.Callsign}"))
            {
                continue;
            }

            var record = StripMutations.RequestDepartureStripForAircraftIntoBay(
                Strips,
                ac,
                scenario,
                bay.Owner.Id,
                bay.Bay.Id,
                rack: 0,
                displayDestinationAirportIds: showDest
            );
            if (record is not null)
            {
                _logger.LogInformation(
                    "Auto-printed approach departure strip for {Callsign} on takeoff roll to bay {Bay}",
                    ac.Callsign,
                    bay.Bay.Name
                );
            }
        }
    }

    private static bool IsApproachDepartureCandidate(AircraftState ac, SimScenarioState scenario)
    {
        if (ac.IsShadow || !NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Departure, scenario.PrimaryAirportId))
        {
            return false;
        }
        var phase = ac.Phases?.CurrentPhase;
        return phase is TakeoffPhase or HelicopterTakeoffPhase;
    }

    /// <summary>
    /// Applies the strip commands the queue put on <c>AircraftState.PendingStripDispatches</c> — preset, deferred or
    /// triggered AN / STRIP / SCAN / … that could not run when they were issued. A failure (e.g. no strip printed yet)
    /// surfaces as a terminal warning so it is not silently lost; success is silent, since the queue already emitted
    /// the <c>[Deferred] … → …</c> / <c>[Preset] …</c> echo when the command fired.
    /// </summary>
    internal void TickStripDispatches()
    {
        foreach (var (callsign, command) in World.DrainAllStripDispatches())
        {
            // A deferred/preset/triggered SEP/HSC/SCAN/BLANK still mints per run kind: the queue carries no record to
            // bake onto. Tracked in docs/plans/MAIN.md.
            var result = StripCommandHandler.Handle(this, command, callsign, bakedStripId: null).Result;
            if (!result.Success)
            {
                EmitTerminal("Warning", callsign, string.IsNullOrEmpty(result.Message) ? "strip command could not be applied" : result.Message);
            }
        }
    }

    /// <summary>
    /// The spawn hook's strip half, for a departure from the scenario's primary airport. Prints according to the
    /// student's position type:
    /// <list type="bullet">
    /// <item>Tower (TWR/LOC): auto-routes to the first own bay whose name starts with
    /// "Ground" so the strip lands where Ground would have placed it. Falls back to the
    /// printer queue if no Ground bay exists.</item>
    /// <item>Ground (GND/DEL): the strip lands on the departure printer queue, since
    /// the student plays Clearance Delivery and physically picks the strip off the printer.</item>
    /// <item>Approach (APP/DEP): no spawn auto-print. The strip is created later by
    /// <see cref="TickAutoApproachDepartureStrips"/> when the aircraft begins its takeoff
    /// roll, mimicking the tower's "rolling call" handoff.</item>
    /// <item>Center / unknown: the printer queue.</item>
    /// </list>
    /// </summary>
    private void PrintSpawnStrip(AircraftState ac, SimScenarioState scenario)
    {
        // Scenario-authored strip placement: the scenario's flightStripConfigurations can
        // pre-assign specific aircraft's strips to a bay/rack (e.g. a Ground scenario drops
        // departures straight into the Ground bay). Honor that before the position-type
        // default routing so an explicit placement wins over the printer-queue / Ground-bay
        // defaults for any student type — but only when the bay is genuinely visible to the
        // student position; otherwise fall through to the default routing below.
        if (TryPlaceConfiguredStrip(ac, scenario))
        {
            return;
        }

        // Approach students get their strip on the takeoff-roll tick, not at spawn.
        if (scenario.StudentPositionType is "APP")
        {
            return;
        }

        // Only auto-print when the position actually has a strip configuration to
        // render them on — otherwise we'd create phantom strips no client can display.
        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (scenario.ArtccConfig is not { } config || string.IsNullOrEmpty(positionCallsign))
        {
            return;
        }

        var accessible = config.GetAllAccessibleStripBays(positionCallsign);
        if (accessible.Count == 0)
        {
            return;
        }

        // Facility id attribution: use the first accessible *own* bay's facility
        // (never an external link). Auto-printed strips belong to the student's
        // own facility; linked external facilities view them only via their own
        // instance. For tower positions this resolves to the ATCT, matching the
        // HSC path.
        var ownBay = accessible.FirstOrDefault(b => !b.IsExternal);
        if (ownBay is null)
        {
            return;
        }
        var showDest = ownBay.Owner.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;

        StripItemRecord? record = null;
        string destinationLabel = "printer queue";

        // Tower student: route to first Ground bay so strips appear where Ground
        // would normally hand them off from. Real ATC flow: Clearance → Ground →
        // Local; with Local being the trainee, Ground's work is implied off-screen.
        if (scenario.StudentPositionType is "TWR")
        {
            var groundBay = config.FindFirstOwnBayWithNamePrefix(positionCallsign, "Ground");
            if (groundBay is not null)
            {
                record = StripMutations.RequestDepartureStripForAircraftIntoBay(
                    Strips,
                    ac,
                    scenario,
                    groundBay.Owner.Id,
                    groundBay.Bay.Id,
                    rack: 0,
                    displayDestinationAirportIds: showDest
                );
                destinationLabel = $"bay {groundBay.Bay.Name}";
            }
        }

        // Fallback (Ground/Center/unknown students, or Tower with no Ground bay):
        // the existing printer-queue path.
        record ??= StripMutations.RequestDepartureStripForAircraft(Strips, ac, scenario, ownBay.Owner.Id, showDest);

        _logger.LogInformation("Auto-printed departure strip {StripId} for {Callsign} to {Destination}", record.Id, ac.Callsign, destinationLabel);
    }

    /// <summary>
    /// Places <paramref name="ac"/>'s departure strip into a scenario-configured bay/rack when the
    /// scenario's <c>flightStripConfigurations</c> names this callsign and the bay is accessible
    /// to the student position. Returns true when the strip was placed (caller should stop), false
    /// when there is no configuration for this aircraft or the bay can't be resolved (caller falls
    /// through to the position-type default routing). Mirrors real vStrips "Move to Bay": a rack
    /// beyond the bay's rack count clamps to the last rack.
    /// </summary>
    private bool TryPlaceConfiguredStrip(AircraftState ac, SimScenarioState scenario)
    {
        if (!scenario.InitialStripBayByCallsign.TryGetValue(ac.Callsign, out var assignment))
        {
            return false;
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        var bay = scenario.ArtccConfig?.GetAccessibleStripBayById(positionCallsign, assignment.BayId);
        if (bay is null)
        {
            return false;
        }

        var rack = Math.Clamp(assignment.Rack, 0, Math.Max(0, bay.Bay.NumberOfRacks - 1));
        var showDest = bay.Owner.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;
        var record = StripMutations.RequestDepartureStripForAircraftIntoBay(Strips, ac, scenario, bay.Owner.Id, bay.Bay.Id, rack, showDest);
        if (record is null)
        {
            return false;
        }

        _logger.LogInformation("Placed departure strip for {Callsign} into scenario-configured bay {Bay}", ac.Callsign, bay.Bay.Name);
        return true;
    }

    /// <summary>
    /// A flight-plan amendment prints a NEW departure strip carrying the bumped revision rather than editing existing
    /// strips in place (docs/crc/vstrips.md:73-75). Outdated copies still in the printer are cleared first; strips
    /// already moved into a bay are left for the controller to remove by hand. The student's own facility's
    /// displayDestinationAirportIds flag keeps the destination a tower ATCT shows next to the departure in field 8.
    /// Runs for a live and a recorded amendment alike; returns the id it printed under, or null when there was nothing
    /// to reprint.
    ///
    /// <para>
    /// <paramref name="bakedStripId"/> is the id the live run printed under, replayed from the record: a run that
    /// already holds it prints nothing (a snapshot restore carried the post-amendment state in, and re-minting would
    /// stack a second copy beside it — the <see cref="Strips.StripRequests.PrintRequestedStrip"/> rule), and a run
    /// that does not prints under it rather than drawing its own. Null on a fresh amendment and on a pre-feature
    /// record, both of which mint. The clear-then-mint order is what lets a live mint reuse the canonical
    /// <c>STRIP_{callsign}</c> when the printer copy was the only holder.
    /// </para>
    /// </summary>
    internal string? ReprintDepartureStripAfterAmendment(string callsign, string? bakedStripId)
    {
        if ((Scenario is not { } scenario) || (FindAircraft(callsign) is not { } ac))
        {
            return null;
        }

        if (bakedStripId is not null && Strips.Items.ContainsKey(bakedStripId))
        {
            return bakedStripId;
        }

        var facilityId = "";
        var showDest = false;
        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (scenario.ArtccConfig is { } config && !string.IsNullOrEmpty(positionCallsign))
        {
            var ownBay = config.GetAllAccessibleStripBays(positionCallsign).FirstOrDefault(b => !b.IsExternal);
            if (ownBay is not null)
            {
                facilityId = ownBay.Owner.Id;
                showDest = ownBay.Owner.FlightStripsConfiguration?.DisplayDestinationAirportIds ?? false;
            }
        }

        StripMutations.RemoveOutdatedDeparturePrinterStrips(Strips, callsign);
        var stripId = bakedStripId ?? StripMutations.MintStripId(Strips, callsign, isArrival: false);
        var printed = StripMutations.PrintDepartureStripForAircraft(Strips, ac, scenario, facilityId, showDest, stripId);
        return printed?.Id;
    }
}
