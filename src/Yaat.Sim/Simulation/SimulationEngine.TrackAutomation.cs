using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Simulation;

// The track-automation spine steps: the delayed-handoff queue in pre-physics, auto-accept, the solo point-out
// approval and the two autotrack passes in post-physics, plus the autotrack conditions a scenario or
// generator aircraft carries. They decide from engine state alone — the scenario's queue and delay, its ATC
// roster and ARTCC config, the recorded CRC attendance and the consolidation hierarchy — so every run kind
// reaches the same verdict.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Files the VFR plan and discrete code an aircraft with no plan needs before it is handed to the student. The
    /// departure is the handing-off facility's airport (the roster entry's facility, else the owner's), blank when
    /// that facility is not an airport. The destination stays blank here: <see cref="DispatchPresetCommands"/>
    /// backfills it to the primary airport when the aircraft spawns.
    /// </summary>
    private void FileHandoffPlan(LoadedAircraft loaded, ResolvedAtcPosition? handingOff, TrackOwner owner)
    {
        AircraftState state = loaded.State;

        // Not through AmendFlightPlan: it finds the aircraft in the world, and a delayed spawn is not there yet.
        state.FlightPlan.HasFlightPlan = true;
        state.FlightPlan.FlightRules = "VFR";
        FiledAircraftType filed = FlightPlanNormalization.SplitTypeAndSuffix(state.AircraftType);
        if (string.IsNullOrEmpty(state.FlightPlan.AircraftType))
        {
            state.FlightPlan.AircraftType = filed.Type;
        }

        state.FlightPlan.EquipmentSuffix = "A";
        state.FlightPlan.ApplyFiledAircraftData(filed);

        string? rosterFacility = handingOff?.Source.FacilityId;
        string facility = string.IsNullOrEmpty(rosterFacility) ? (owner.FacilityId ?? "") : rosterFacility;
        string facilityAirport = NavigationDatabase.NormalizeAirport(facility);
        state.FlightPlan.Departure = NavigationDatabase.Instance.GetAirportPosition(facilityAirport) is null ? "" : facilityAirport;

        uint code = BeaconCodePool.AssignNextCode(true);
        if (code == 0)
        {
            _logger.LogWarning("Beacon code pool exhausted; filing {Callsign} on 1200 without a discrete code", state.Callsign);
            return;
        }

        state.Transponder.AssignCode(code, null, null);
        state.Transponder.Code = state.Transponder.AssignedCode;
    }

    /// <summary>
    /// Applies a scenario or generator aircraft's auto-track conditions: the airport-based owner from the ATC
    /// roster, the scratchpad rules, the configured owner and scratchpad, the delayed handoff to the student and
    /// the ERAM datablock altitudes. The <c>[AutoTrack] …</c> lines go into
    /// <see cref="LoadedAircraft.AutoTrackMessages"/>, which the spawn paths echo to the terminal.
    /// </summary>
    public void ApplyAutoTrackConditions(LoadedAircraft loaded)
    {
        SimScenarioState scenario = Scenario!;
        List<string> messages = loaded.AutoTrackMessages;
        // The owner this function sets, which the pilot of an airborne spawn starts tuned to.
        TrackOwner? spawnOwner = null;

        if (
            loaded.State.Track.Owner is null
            && !FieldElevationResolver.IsBelowDisplayFloor(loaded.State, NavigationDatabase.Instance)
            && !string.IsNullOrEmpty(loaded.State.FlightPlan.Departure)
        )
        {
            foreach (ResolvedAtcPosition atcPos in scenario.AtcPositions)
            {
                if (atcPos.Source.AutoTrackAirportIds.Count == 0)
                {
                    continue;
                }

                string dep = loaded.State.FlightPlan.Departure;
                string? matchedAirportId = null;
                foreach (string airportId in atcPos.Source.AutoTrackAirportIds)
                {
                    if (NavigationDatabase.AirportIdsMatch(dep, airportId))
                    {
                        loaded.State.Track.Owner = atcPos.Owner;
                        spawnOwner = atcPos.Owner;
                        matchedAirportId = airportId;
                        break;
                    }
                }

                if (loaded.State.Track.Owner is not null)
                {
                    messages.Add(
                        $"[AutoTrack] Owned by " + $"{TrackEngine.FormatOwner(atcPos.Owner)} " + $"(autoTrackAirportIds: {matchedAirportId})"
                    );
                    break;
                }
            }
        }

        ScratchpadRuleEngine.Apply(loaded.State, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

        AutoTrackConditions? autoTrack = loaded.AutoTrackConditions;
        if (autoTrack is null)
        {
            TuneToSpawnOwner(loaded.State, spawnOwner, scenario);
            return;
        }

        // Prefer the resolved ATC roster: it is ARTCC-aware, so a position in a *neighboring*
        // ARTCC ("ZLC starts with the track") resolves against its own facility tree. Falling
        // back to the scenario's own ARTCC covers positions not listed in the roster (e.g.
        // generator autoTrackConfiguration in scenarios with an empty atc array).
        TrackOwner? owner =
            scenario.AtcPositions.FirstOrDefault(p => p.Source.PositionId == autoTrack.PositionId)?.Owner
            ?? scenario.ArtccConfig?.ResolvePosition(autoTrack.PositionId);

        if (owner is not null)
        {
            loaded.State.Track.Owner = owner;
            spawnOwner = owner;
            messages.Add($"[AutoTrack] Owned by " + $"{TrackEngine.FormatOwner(owner)} " + "(autoTrackConditions)");
        }
        else
        {
            _logger.LogWarning(
                "[AutoTrack] {Callsign}: could not resolve autoTrackConditions position {PositionId} "
                    + "(not in the scenario ATC roster or {ArtccId}); aircraft spawns untracked",
                loaded.State.Callsign,
                autoTrack.PositionId,
                scenario.ArtccId ?? ""
            );
        }

        if (autoTrack.HandoffDelay is not null && scenario.StudentPosition is not null)
        {
            if ((owner is not null) && !loaded.State.FlightPlan.HasFlightPlan)
            {
                FileHandoffPlan(loaded, scenario.AtcPositions.FirstOrDefault(p => p.Source.PositionId == autoTrack.PositionId), owner);
            }

            string targetLabel = TrackEngine.FormatOwner(scenario.StudentPosition);
            // Always queue autotrack handoffs — don't set HandoffPeer immediately.
            // TickDelayedHandoffs will fire them once the target position is online.
            int spawnAt = loaded.SpawnDelaySeconds;
            int fireAt = autoTrack.HandoffDelay == 0 ? Math.Max((int)scenario.ElapsedSeconds, spawnAt) : spawnAt + autoTrack.HandoffDelay.Value;
            scenario.DelayedHandoffQueue.Add(
                new DelayedHandoff
                {
                    Callsign = loaded.State.Callsign,
                    Target = scenario.StudentPosition,
                    FireAtSeconds = fireAt,
                }
            );
            messages.Add(
                $"[AutoTrack] Handoff to {targetLabel} queued (fireAt={fireAt}s, spawnDelay={spawnAt}s, handoffDelay={autoTrack.HandoffDelay}s)"
            );
        }

        if (!string.IsNullOrEmpty(autoTrack.ScratchPad))
        {
            // ATCTrainer convention: '+' prefix means SP2, bare value means SP1.
            // Supports space-separated values, e.g. "FOO +RGT" → SP1=FOO, SP2=RGT.
            foreach (string token in autoTrack.ScratchPad.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('+'))
                {
                    string sp2Value = token[1..];
                    loaded.State.Stars.Scratchpad2 = sp2Value;
                    messages.Add($"[AutoTrack] SP2 set: {sp2Value}");
                }
                else
                {
                    loaded.State.Stars.Scratchpad1 = token;
                    messages.Add($"[AutoTrack] SP1 set: {token}");
                }
            }
        }

        // Auto-track interim/cleared altitudes are inherited DATABLOCK-DISPLAY state -- they must never
        // change what the aircraft physically flies (no Targets.AssignedAltitude write here). They are
        // wired to the ERAM datablock only: interim -> Eram.InterimAltitude (hundreds of feet), cleared ->
        // FlightPlan.EramAssignedAltitudeFeet, the display-only ERAM assigned altitude in ERAM field B. The
        // cleared altitude is not a controller-entered reported altitude (that would outrank Mode C), and the
        // filed altitude stays as filed because departure logic climbs to it. The STARS datablock is deliberately
        // NOT populated from these fields: whether and how non-ERAM (STARS) scenarios use interim/cleared altitudes
        // is still being confirmed with VATUSA staff, so we leave Stars.TemporaryAltitude untouched until that
        // guidance lands.
        int? interimHundreds = ResolveDatablockAltitudeHundreds(autoTrack.InterimAltitude);
        int? clearedHundreds = ResolveDatablockAltitudeHundreds(autoTrack.ClearedAltitude);
        if (interimHundreds is not null)
        {
            loaded.State.Eram.InterimAltitude = interimHundreds;
            messages.Add($"[AutoTrack] ERAM interim altitude set: {interimHundreds}");
        }
        if ((clearedHundreds is { } cleared) && (cleared > 0))
        {
            loaded.State.FlightPlan.AssignEramAltitude(cleared * 100);
            messages.Add($"[AutoTrack] ERAM assigned altitude set: {cleared}");
        }

        TuneToSpawnOwner(loaded.State, spawnOwner, scenario);
    }

    /// <summary>
    /// The pilot of an airborne spawn starts tuned to the position that owns its track at spawn, when that is not the
    /// student's; an on-ground spawn starts on the student's side (null) even when another position owns its track. With
    /// no student position yet (a replay sets it later) the owner is stamped anyway, and
    /// <see cref="AircraftState.IsTunedAwayFrom"/> compares at read time.
    /// </summary>
    private static void TuneToSpawnOwner(AircraftState state, TrackOwner? owner, SimScenarioState scenario)
    {
        if (!state.IsOnGround && (owner is not null) && !((scenario.StudentPosition is { } student) && owner.IsSamePositionByCallsign(student)))
        {
            state.TunedPosition = owner;
        }
    }

    /// <summary>
    /// Resolves a vNAS auto-track datablock altitude (a STARS hundreds-of-feet string, optionally with a
    /// leading qualifier letter such as "P040") to hundreds of feet -- the unit the ERAM/STARS altitude
    /// fields use. A single leading non-digit qualifier is stripped before parsing. Returns null when the
    /// value is empty or unparseable.
    /// </summary>
    private static int? ResolveDatablockAltitudeHundreds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string token = raw.Trim();
        if (!char.IsDigit(token[0]))
        {
            token = token[1..];
        }

        int? feet = AltitudeResolver.Resolve(token);
        return feet is null ? null : feet.Value / 100;
    }

    /// <summary>
    /// Applies a CRC <c>.AUTOTRACK</c> delta: resolves (or creates) the calling position's roster entry, applies the
    /// delta to the roster, and claims every airborne untracked aircraft whose filed departure now matches one of the
    /// caller's airports. The one body every run kind runs — the live CRC handler records the change through
    /// <see cref="Actions.ActionRouter.IssueDerived(RecordedAction, Actions.IActionHost)"/>, and a rewind or a
    /// from-scratch reconstruction replays it here — so no run can reach a different roster.
    /// </summary>
    internal CommandResult ApplyAutoTrackChange(RecordedAutoTrackChange change)
    {
        if (Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        ResolvedAtcPosition? atcPos = ResolveAutoTrackPosition(scenario, change.PositionId);
        if (atcPos is null)
        {
            return new CommandResult(false, $"Could not resolve position {change.PositionId} for .AUTOTRACK");
        }

        List<(string AirportId, TrackOwner FormerOwner)> stolen = ApplyAutoTrackDelta(scenario, atcPos, change.Entries);
        List<string> airports = atcPos.Source.AutoTrackAirportIds;

        LogAutoTrackChange(change.PositionId, atcPos, airports, stolen);

        if (airports.Count > 0)
        {
            ClaimUntrackedDepartures(scenario, atcPos);
            EmitTerminal("System", "", $"Auto-track airports for {TrackEngine.FormatOwner(atcPos.Owner)}: {string.Join(", ", airports)}");
        }
        else
        {
            EmitTerminal("System", "", $"Auto-track airports cleared for {TrackEngine.FormatOwner(atcPos.Owner)}");
        }

        return new CommandResult(true);
    }

    /// <summary>Logs at Information the auto-track airports <paramref name="atcPos"/> holds and each one it took from another owner.</summary>
    private void LogAutoTrackChange(
        string positionId,
        ResolvedAtcPosition atcPos,
        List<string> airports,
        List<(string AirportId, TrackOwner FormerOwner)> stolen
    )
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Auto-track airports for {PositionId} ({Owner}): [{Airports}]",
            positionId,
            TrackEngine.FormatOwner(atcPos.Owner),
            string.Join(", ", airports)
        );

        foreach ((string? airportId, TrackOwner? formerOwner) in stolen)
        {
            _logger.LogInformation(
                "[AutoTrack] {Owner} took {Airport} from {FormerOwner}",
                TrackEngine.FormatOwner(atcPos.Owner),
                airportId,
                TrackEngine.FormatOwner(formerOwner)
            );
        }
    }

    /// <summary>
    /// The roster entry the delta targets. A CRC position the scenario JSON never listed is resolved through the
    /// room's ARTCC configuration and added; a position that configuration cannot place is refused rather than
    /// invented, so the delta has no target and nothing is written.
    /// </summary>
    private ResolvedAtcPosition? ResolveAutoTrackPosition(SimScenarioState scenario, string positionId)
    {
        if (scenario.AtcPositions.FirstOrDefault(p => p.Source.PositionId == positionId) is { } existing)
        {
            return existing;
        }

        TrackOwner? owner = scenario.ArtccConfig?.ResolvePosition(positionId);
        if (owner is null)
        {
            _logger.LogWarning("[AutoTrack] could not resolve position {PositionId} in {ArtccId}", positionId, scenario.ArtccId ?? "");
            return null;
        }

        var added = new ResolvedAtcPosition
        {
            Source = new ScenarioAtc { PositionId = positionId, ArtccId = scenario.ArtccId ?? "" },
            Owner = owner,
            Tcp = scenario.ArtccConfig?.GetTcpForPosition(positionId),
        };
        scenario.AtcPositions.Add(added);
        return added;
    }

    /// <summary>
    /// Hands the position every airborne untracked aircraft filed out of one of its airports. Airborne rather than
    /// above the display floor: this is the retroactive sweep a controller expects when taking an airport over, and
    /// the deferred pass owns the floor crossing for the ones still on the ground.
    /// </summary>
    private void ClaimUntrackedDepartures(SimScenarioState scenario, ResolvedAtcPosition atcPos)
    {
        StarsConfig? starsConfig = scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? "");

        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.Track.Owner is not null || ac.IsOnGround || string.IsNullOrEmpty(ac.FlightPlan.Departure))
            {
                continue;
            }

            ClaimIfDepartureMatches(ac, atcPos, starsConfig);
        }
    }

    private void ClaimIfDepartureMatches(AircraftState ac, ResolvedAtcPosition atcPos, StarsConfig? starsConfig)
    {
        string dep = ac.FlightPlan.Departure;

        foreach (string airportId in atcPos.Source.AutoTrackAirportIds)
        {
            if (!NavigationDatabase.AirportIdsMatch(dep, airportId))
            {
                continue;
            }

            ac.Track.Owner = atcPos.Owner;
            ScratchpadRuleEngine.Apply(ac, starsConfig);
            RemoveOrDeferCoordination(ac);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "[AutoTrack] {Callsign} auto-owned by {Owner} (departure {Dep})",
                    ac.Callsign,
                    TrackEngine.FormatOwner(atcPos.Owner),
                    dep
                );
            }
            return;
        }
    }

    /// <summary>
    /// Applies a CRC-style autotrack delta to <paramref name="atcPos"/>: each entry is one of
    /// "none" (clear caller's list), "-X" (remove X from every position globally), or a
    /// positive airport ID (add to caller's list AND remove from every other position so each
    /// airport has at most one owner). Returns the airports stolen from other positions paired
    /// with their former owner.
    /// </summary>
    private static List<(string AirportId, TrackOwner FormerOwner)> ApplyAutoTrackDelta(
        SimScenarioState scenario,
        ResolvedAtcPosition atcPos,
        IEnumerable<string> entries
    )
    {
        var stolen = new List<(string AirportId, TrackOwner FormerOwner)>();

        foreach (string raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string entry = raw.Trim();

            if (string.Equals(entry, "none", StringComparison.OrdinalIgnoreCase))
            {
                ClearCallerList(atcPos);
            }
            else if (entry.StartsWith('-'))
            {
                RemoveAirportEverywhere(scenario, entry[1..].Trim());
            }
            else
            {
                TakeAirport(scenario, atcPos, entry, stolen);
            }
        }

        return stolen;
    }

    /// <summary>"none": the caller stops auto-tracking every airport it held, leaving other positions untouched.</summary>
    private static void ClearCallerList(ResolvedAtcPosition atcPos) => atcPos.Source.AutoTrackAirportIds.Clear();

    /// <summary>
    /// "-X": the airport stops being auto-tracked by every position, whoever held it. An entry that is a bare dash
    /// names no airport and is ignored.
    /// </summary>
    private static void RemoveAirportEverywhere(SimScenarioState scenario, string airportId)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return;
        }

        foreach (ResolvedAtcPosition p in scenario.AtcPositions)
        {
            p.Source.AutoTrackAirportIds.RemoveAll(a => string.Equals(a, airportId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A positive entry: the caller takes ownership, stripping the airport from every other position so each airport
    /// has a single owner ("last write wins"). Every position it was taken from is appended to
    /// <paramref name="stolen"/> with the owner that held it.
    /// </summary>
    private static void TakeAirport(
        SimScenarioState scenario,
        ResolvedAtcPosition atcPos,
        string airportId,
        List<(string AirportId, TrackOwner FormerOwner)> stolen
    )
    {
        foreach (ResolvedAtcPosition p in scenario.AtcPositions)
        {
            if (ReferenceEquals(p, atcPos))
            {
                continue;
            }
            if (p.Source.AutoTrackAirportIds.RemoveAll(a => string.Equals(a, airportId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                stolen.Add((airportId, p.Owner));
            }
        }

        if (!atcPos.Source.AutoTrackAirportIds.Any(a => string.Equals(a, airportId, StringComparison.OrdinalIgnoreCase)))
        {
            atcPos.Source.AutoTrackAirportIds.Add(airportId);
        }
    }

    /// <summary>
    /// Auto-track aircraft once they first appear on STARS — i.e. after they climb through the
    /// acquisition floor (field elevation + 100 ft AGL), not the instant their wheels leave the
    /// ground. Owning a track that isn't yet displayed would hand a still-on-the-surface departure
    /// to a radar controller before it exists on the scope.
    /// </summary>
    internal void TickDeferredAutoTrack()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            RemoveCoordinationAtFloorCrossing(ac);
            if (
                ac.Track.Owner is not null
                || FieldElevationResolver.IsBelowDisplayFloor(ac, NavigationDatabase.Instance)
                || string.IsNullOrEmpty(ac.FlightPlan.Departure)
            )
            {
                continue;
            }

            if (FindAutoTrackPosition(scenario, ac.FlightPlan.Departure) is { } match)
            {
                AcquireOnStars(scenario, ac, match.Position, match.AirportId);
            }
        }
    }

    /// <summary>
    /// A departure acquired below the display floor kept its coordination items; once it is above the floor STARS
    /// displays it, so the deferred removal runs and the mark clears, owned or not.
    /// </summary>
    private void RemoveCoordinationAtFloorCrossing(AircraftState ac)
    {
        if ((!ac.Track.RemoveCoordinationAtFloor) || FieldElevationResolver.IsBelowDisplayFloor(ac, NavigationDatabase.Instance))
        {
            return;
        }

        ac.Track.RemoveCoordinationAtFloor = false;
        RemoveCoordinationOnRadarAcquisition(ac.Callsign);
    }

    /// <summary>The first roster position whose auto-track airports take <paramref name="departure"/>, with the airport that matched.</summary>
    private static (ResolvedAtcPosition Position, string AirportId)? FindAutoTrackPosition(SimScenarioState scenario, string departure)
    {
        foreach (ResolvedAtcPosition atcPos in scenario.AtcPositions)
        {
            foreach (string airportId in atcPos.Source.AutoTrackAirportIds)
            {
                if (NavigationDatabase.AirportIdsMatch(departure, airportId))
                {
                    return (atcPos, airportId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The deferred autotrack's acquisition: the position owns the departure that has just appeared on STARS, its
    /// scratchpad rules apply, and the release rundown that got it there is over.
    /// </summary>
    private void AcquireOnStars(SimScenarioState scenario, AircraftState ac, ResolvedAtcPosition atcPos, string matchedAirportId)
    {
        ac.Track.Owner = atcPos.Owner;
        ScratchpadRuleEngine.Apply(ac, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[DeferredAutoTrack] {Callsign} appeared on STARS — owned by {Owner} (departure {Dep}, matched {Airport})",
                ac.Callsign,
                TrackEngine.FormatOwner(atcPos.Owner),
                ac.FlightPlan.Departure,
                matchedAirportId
            );
        }

        EmitTerminal(
            "System",
            ac.Callsign,
            $"[AutoTrack] On STARS — owned by {TrackEngine.FormatOwner(atcPos.Owner)} (autoTrackAirportIds: {matchedAirportId})"
        );
        RemoveCoordinationOnRadarAcquisition(ac.Callsign);
    }

    /// <summary>
    /// Auto-track aircraft to the TCP that created their flight plan via VP/DA, once the pilot
    /// is squawking the assigned beacon code. Mirrors STARS flight-plan-to-track correlation:
    /// when an FP's beacon code matches the aircraft's Mode-3 transponder, the data block flips
    /// to the creating facility. Skips aircraft that already have a track owner (no overwrite).
    /// </summary>
    internal void TickFlightPlanCreatorAutoTrack()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (FlightPlanCreatorToAcquire(ac) is { } creator)
            {
                AcquireForFlightPlanCreator(scenario, ac, creator);
            }
        }
    }

    /// <summary>The creator of an untracked aircraft's flight plan once the pilot squawks the code it assigned, else null.</summary>
    private static TrackOwner? FlightPlanCreatorToAcquire(AircraftState ac)
    {
        if ((ac.Track.Owner is not null) || (ac.FlightPlan.CreatedByOwner is not { } creator))
        {
            return null;
        }

        bool squawkingAssigned = (ac.Transponder.AssignedCode != 0) && (ac.Transponder.Code == ac.Transponder.AssignedCode);
        return squawkingAssigned ? creator : null;
    }

    /// <summary>
    /// The flight-plan-creator acquisition: the creator owns the track and its scratchpad rules apply. The release
    /// rundown ends only once STARS displays the aircraft — above the display floor, as the deferred autotrack's
    /// acquisition — since a departure squawking at the gate is not yet a radar acquisition. One acquired below the
    /// floor is marked so the deferred pass removes its items when it climbs above it.
    /// </summary>
    private void AcquireForFlightPlanCreator(SimScenarioState scenario, AircraftState ac, TrackOwner creator)
    {
        ac.Track.Owner = creator;
        ScratchpadRuleEngine.Apply(ac, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[FlightPlanCreatorAutoTrack] {Callsign} squawking assigned code {Code:D4} — owned by {Owner} (FP creator)",
                ac.Callsign,
                ac.Transponder.Code,
                TrackEngine.FormatOwner(creator)
            );
        }
        EmitTerminal("System", ac.Callsign, $"[AutoTrack] Squawking assigned code — owned by {TrackEngine.FormatOwner(creator)} (FP creator)");
        RemoveOrDeferCoordination(ac);
    }

    /// <summary>
    /// The coordination removal of an acquisition: at once when STARS displays the aircraft, else marked for the
    /// deferred pass to run once it is above the display floor.
    /// </summary>
    private void RemoveOrDeferCoordination(AircraftState ac)
    {
        if (FieldElevationResolver.IsBelowDisplayFloor(ac, NavigationDatabase.Instance))
        {
            ac.Track.RemoveCoordinationAtFloor = true;
            return;
        }

        RemoveCoordinationOnRadarAcquisition(ac.Callsign);
    }

    internal void TickDelayedHandoffs()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        for (int i = scenario.DelayedHandoffQueue.Count - 1; i >= 0; i--)
        {
            DelayedHandoff entry = scenario.DelayedHandoffQueue[i];
            if (scenario.ElapsedSeconds < entry.FireAtSeconds)
            {
                continue;
            }

            // Don't initiate handoffs to positions that are consolidated under
            // another attended TCP. If 3O is consolidated under 3G (because 3O
            // isn't active), 3G already owns those tracks — a handoff to 3O
            // would be a handoff to yourself. Once 3O activates and is no longer
            // consolidated under 3G, the handoff makes sense.
            Tcp? targetTcp = TrackResolver.FindTcpForOwner(entry.Target, scenario);
            if (targetTcp is not null)
            {
                Tcp? consolidationOwner = Attendance.ConsolidationOwnerOf(targetTcp, scenario, ConsolidationState);
                if (consolidationOwner is not null && consolidationOwner.Id != targetTcp.Id)
                {
                    continue;
                }
            }

            scenario.DelayedHandoffQueue.RemoveAt(i);

            List<AircraftState> snapshot = World.GetSnapshot();
            AircraftState? aircraft = snapshot.FirstOrDefault(a => a.Callsign.Equals(entry.Callsign, StringComparison.OrdinalIgnoreCase));

            if (aircraft?.Track.Owner is not { } owner)
            {
                _logger.LogWarning(
                    "[AutoTrack] Delayed handoff for {Callsign} to {Target} dropped at t={T}s: {Reason}",
                    entry.Callsign,
                    TrackEngine.FormatOwner(entry.Target),
                    scenario.ElapsedSeconds,
                    aircraft is null ? "aircraft not found" : "aircraft is untracked (no owner to hand off from)"
                );
            }
            else if (owner.MatchesPosition(entry.Target))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[AutoTrack] Delayed handoff for {Callsign} to {Target} dropped at t={T}s: already owned by the target",
                        entry.Callsign,
                        TrackEngine.FormatOwner(entry.Target),
                        scenario.ElapsedSeconds
                    );
                }
            }
            else
            {
                aircraft.Track.HandoffPeer = entry.Target;
                aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

                EmitTerminal("System", entry.Callsign, "[AutoTrack] Delayed handoff initiated to " + TrackEngine.FormatOwner(entry.Target));
            }
        }
    }

    /// <summary>
    /// Approves a point-out a solo student files to a simulated position after the auto-accept delay, since nobody
    /// else is in the room to answer it. In a non-solo room a point-out to a position nobody is working stays pending
    /// with no timeout, as on a real STARS display: the RPO answers it by hand (<c>OK</c> / <c>UN</c>). A point-out to
    /// an attended CRC position, or in solo mode to the student's own position, is left pending — there is a receiving
    /// controller and they are the one who must act.
    /// </summary>
    internal void TickSoloPointoutApproval()
    {
        if ((Scenario is not { } scenario) || (!scenario.SoloTrainingMode))
        {
            return;
        }

        double effectiveDelaySeconds = scenario.EffectiveAutoAcceptDelaySeconds;

        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (aircraft.Track.Pointout is not { IsPending: true, InitiatedAt: not null } pointout)
            {
                continue;
            }

            bool recipientAttended = Attendance.IsTcpControlledByCrc(pointout.Recipient, scenario, ConsolidationState);
            if (!IsSoloApprovalDue(pointout, scenario, effectiveDelaySeconds, recipientAttended))
            {
                continue;
            }

            CommandResult result = TrackEngine.HandleAcknowledge(aircraft);
            if (result.Success)
            {
                EmitTerminal("System", aircraft.Callsign, $"[AutoAck] Pointout approved by {pointout.Recipient}");
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Auto-approved pointout: {Callsign} to {Recipient} at t={T}s",
                        aircraft.Callsign,
                        pointout.Recipient,
                        scenario.ElapsedSeconds
                    );
                }
            }
        }
    }

    /// <summary>
    /// Whether a pending point-out is one the solo room answers on the simulated position's behalf: the recipient is
    /// neither CRC-attended nor the student's own position, and the auto-accept delay has run since it was filed.
    /// </summary>
    private static bool IsSoloApprovalDue(StarsPointout pointout, SimScenarioState scenario, double delaySeconds, bool recipientAttended)
    {
        return (!recipientAttended)
            && (!IsStudentRecipient(pointout.Recipient, scenario))
            && (scenario.ElapsedSeconds - pointout.InitiatedAt!.Value >= delaySeconds);
    }

    /// <summary>
    /// The tail every successful track dispatch runs beyond the track itself, on every path into
    /// <see cref="TrackEngine.Dispatch"/> (the command arm, an immediate all-track preset, a triggered queue block): a
    /// point-out that has just been filed is announced to the instructor when nobody works its recipient.
    /// </summary>
    internal void AfterTrackDispatch(ParsedCommand command, AircraftState aircraft, SimScenarioState scenario)
    {
        if ((command is PointOutCommand) && (aircraft.Track.Pointout is { IsPending: true } pointout))
        {
            AnnounceUnansweredPointout(aircraft, pointout, scenario);
        }
    }

    /// <summary>
    /// Tells the instructor a non-solo room's point-out to a position nobody is working is waiting for an answer: the
    /// RPO works that position from the client, nothing else announces the point-out, and it now stays pending until
    /// they answer it (<c>OK</c> / <c>UN</c>). A solo room's simulated approval (<see cref="TickSoloPointoutApproval"/>),
    /// an attended CRC recipient and the student as recipient all have a controller who acts, so they stay silent.
    /// </summary>
    private void AnnounceUnansweredPointout(AircraftState aircraft, StarsPointout pointout, SimScenarioState scenario)
    {
        if (
            scenario.SoloTrainingMode
            || Attendance.IsTcpControlledByCrc(pointout.Recipient, scenario, ConsolidationState)
            || IsStudentRecipient(pointout.Recipient, scenario)
        )
        {
            return;
        }

        EmitTerminal(
            "System",
            aircraft.Callsign,
            $"[Pointout] {pointout.Sender} → {pointout.Recipient}: {aircraft.Callsign} awaits OK or UN (nobody works {pointout.Recipient})"
        );
    }

    /// <summary>
    /// Whether <paramref name="recipient"/> addresses the student's own position — either the room's student TCP, or,
    /// when that never resolved, a TCP code that resolves back to <see cref="SimScenarioState.StudentPosition"/> the
    /// way <see cref="TickAutoAccept"/> matches a handoff peer against it.
    /// </summary>
    private static bool IsStudentRecipient(Tcp recipient, SimScenarioState scenario)
    {
        if (
            scenario.StudentTcp is { } studentTcp
            && (recipient.Subset == studentTcp.Subset)
            && string.Equals(recipient.SectorId, studentTcp.SectorId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return scenario.StudentPosition is { } student
            && TrackResolver.ResolveTcpToOwner(scenario, recipient.ToString(), facilityHint: null) is { } recipientOwner
            && recipientOwner.MatchesPosition(student);
    }

    internal void TickAutoAccept()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        bool soloMode = scenario.SoloTrainingMode;
        if (!soloMode && scenario.AutoAcceptDelay <= TimeSpan.Zero)
        {
            LogAutoAcceptDisabled(scenario.AutoAcceptDelay);
            return;
        }

        // Solo mode forces a floored auto-accept for non-student positions even when the operator
        // globally disabled auto-accept; non-solo play uses the operator's configured delay verbatim.
        double effectiveDelaySeconds = scenario.EffectiveAutoAcceptDelaySeconds;

        List<AircraftState> snapshot = World.GetSnapshot();
        var pendingHandoffs = snapshot.Where(a => a.Track.HandoffPeer is not null && a.Track.HandoffInitiatedAt is not null).ToList();
        if (pendingHandoffs.Count > 0 && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TickAutoAccept: {Count} pending handoffs at t={T}s", pendingHandoffs.Count, scenario.ElapsedSeconds);
        }

        foreach (AircraftState aircraft in snapshot)
        {
            if (aircraft.Track.HandoffPeer is null || aircraft.Track.HandoffInitiatedAt is null)
            {
                continue;
            }

            if (aircraft.Track.OwnerFromLiveFeed)
            {
                // A real-world handoff mirrored from the feed: it completes when the feed says so, never by
                // the room's auto-accept (which would move the owner ahead of reality and churn every sample).
                continue;
            }

            if (TrackEngine.IsHandoffPeerControlledByCrc(aircraft.Track.HandoffPeer, scenario, Attendance, ConsolidationState))
            {
                LogAutoAcceptSkippedCrcPeer(aircraft.Callsign);
                continue;
            }

            // Auto-accept never takes a track ERAM may show as CST (7110.65 §5-4-5.e, §5-4-6.f.3 require verbal
            // coordination then). The check reads the ERAM coverage state the step before this one left (coverage-coasting,
            // below coverage on the display's hysteresis side, on the ground, or a QT coast track) and applies to every
            // handoff, STARS included, by design: a low STARS handoff waits until ERAM shows the track again. It also holds
            // after the coverage coast has expired, which only keeps the handoff pending longer.
            if (TrackEngine.IsAutoAcceptWithheld(aircraft, NavigationDatabase.Instance))
            {
                LogAutoAcceptWithheld(aircraft.Callsign);
                continue;
            }

            // Solo mode: never auto-accept a handoff to the student's own position — the student
            // accepts it by hand (in non-solo an RPO does). Every other position auto-accepts below.
            if (soloMode && scenario.StudentPosition is { } student && aircraft.Track.HandoffPeer.MatchesPosition(student))
            {
                LogAutoAcceptLeftForStudent(aircraft.Callsign);
                continue;
            }

            double elapsed = scenario.ElapsedSeconds - aircraft.Track.HandoffInitiatedAt.Value;
            if (elapsed >= effectiveDelaySeconds)
            {
                TrackOwner? previousOwner = aircraft.Track.Owner;
                TrackOwner newOwner = aircraft.Track.HandoffPeer;

                // Keep the previous owner's datablock as a white FDB (CRC WasPreviouslyOwned) after the
                // handoff is accepted, until that controller slews to acknowledge — instead of dropping
                // straight to an unowned green PDB. Shared with the manual ACCEPT / accept-all paths.
                TrackEngine.MarkPreviousOwnerRetained(aircraft, previousOwner, scenario);
                TrackEngine.MarkRecentHandoffAccepted(aircraft, previousOwner, wasForced: false, scenario);

                aircraft.Track.Owner = newOwner;
                aircraft.Track.HandoffPeer = null;
                aircraft.Track.HandoffInitiatedAt = null;
                aircraft.Track.HandoffRedirectedBy = null;
                // Mirror the manual ACCEPT: flag the on-handoff trigger so a queued ONHO condition fires.
                aircraft.Track.HandoffAccepted = true;

                ScratchpadRuleEngine.Apply(aircraft, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

                string fromLabel = previousOwner is not null ? TrackEngine.FormatOwner(previousOwner) : "?";
                string toLabel = TrackEngine.FormatOwner(newOwner);
                EmitTerminal("System", aircraft.Callsign, $"[AutoAccept] Handoff accepted " + $"({fromLabel} \u2192 {toLabel})");

                LogHandoffAutoAccepted(aircraft.Callsign, fromLabel, toLabel, elapsed, scenario.ElapsedSeconds);
            }
        }
    }

    /// <summary>Logs at Trace that auto-accept is disabled by a non-positive <paramref name="delay"/>.</summary>
    private void LogAutoAcceptDisabled(TimeSpan delay)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TickAutoAccept: auto-accept disabled (delay={Delay})", delay);
        }
    }

    /// <summary>Logs at Trace that <paramref name="callsign"/>'s handoff is skipped because its peer is CRC-controlled.</summary>
    private void LogAutoAcceptSkippedCrcPeer(string callsign)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TickAutoAccept: {Callsign} handoff pending to CRC-controlled position, skipping", callsign);
        }
    }

    /// <summary>Logs at Trace that <paramref name="callsign"/>'s handoff is withheld below ERAM coverage or on a QT coast.</summary>
    private void LogAutoAcceptWithheld(string callsign)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TickAutoAccept: {Callsign} handoff withheld below ERAM coverage or on a QT coast, skipping", callsign);
        }
    }

    /// <summary>Logs at Trace that <paramref name="callsign"/>'s handoff to the student position is left for a manual accept.</summary>
    private void LogAutoAcceptLeftForStudent(string callsign)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TickAutoAccept: {Callsign} handoff pending to student position, leaving for manual accept", callsign);
        }
    }

    /// <summary>Logs at Information that <paramref name="callsign"/>'s handoff was auto-accepted after <paramref name="elapsed"/> seconds.</summary>
    private void LogHandoffAutoAccepted(string callsign, string fromLabel, string toLabel, double elapsed, double scenarioSeconds)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Auto-accepted handoff: {Callsign} ({From} -> {To}) after {Elapsed}s at t={T}s",
                callsign,
                fromLabel,
                toLabel,
                elapsed,
                scenarioSeconds
            );
        }
    }
}
