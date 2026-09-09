using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// Single dispatch point for canonical vTDLS commands — TDLSQ, TDLSS, TDLSW, TDLSDUMP. Each
/// handler resolves the TDLS facility from the aircraft's filed departure airport and mutates
/// the engine's <see cref="SimulationEngine.Tdls"/> via <see cref="TdlsMutations"/>, which records
/// what changed in <see cref="TdlsState.Changes"/> for the host to broadcast. WILCO handling
/// (auto + manual) lives here too.
/// </summary>
public static class TdlsCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("TdlsCommandHandler");

    /// <summary>
    /// Delay between TDLSS landing and the auto-WILCO, in sim seconds against the session clock. Real FMS
    /// auto-ack is near-instant; three sim seconds give the controller a visible Sent state at 1× (the window
    /// shrinks with the sim rate).
    /// </summary>
    public static readonly TimeSpan DefaultWilcoDelay = TimeSpan.FromSeconds(3);

    public static CommandResult Handle(SimulationEngine engine, ParsedCommand parsed, string callsign)
    {
        try
        {
            return parsed switch
            {
                TdlsQueueCommand => HandleQueue(engine, callsign),
                TdlsSendCommand cmd => HandleSend(engine, callsign, cmd),
                TdlsWilcoCommand => HandleWilco(engine, callsign),
                TdlsDumpCommand => HandleDump(engine, callsign),
                _ => new CommandResult(false, "Unknown TDLS command"),
            };
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "TDLS command handler threw for {Type} callsign={Callsign}", parsed.GetType().Name, callsign);
            return new CommandResult(false, $"TDLS command error: {ex.Message}");
        }
    }

    // ── TDLSQ — queue a Pending item ──────────────────────────────

    private static CommandResult HandleQueue(SimulationEngine engine, string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "TDLSQ requires an aircraft selection");
        }

        var facility = ResolveFacility(engine, callsign, out var failureReason);
        if (facility is null)
        {
            return new CommandResult(false, failureReason ?? "Unable to resolve TDLS facility");
        }

        // CID is null for manual-TDLSQ entries; the auto-queue can populate it when
        // it has the filing CID in hand. The CID is only used for re-broadcast-on-reconnect
        // visibility — null here is benign because the AircraftId still identifies the item.
        string? cid = null;

        var nowUtc = engine.Scenario!.SimTimeUtc;

        TdlsItemRecord? created;
        bool wasIdempotent;
        lock (engine.Tdls.Gate)
        {
            if (engine.Tdls.Dumped.Contains(new DumpedKey(facility, callsign)))
            {
                return new CommandResult(false, $"TDLS for {callsign} at {facility} was dumped this session — clearance must be issued by voice");
            }

            var existing = TdlsMutations.FindActiveItem(engine.Tdls, facility, callsign);
            wasIdempotent = existing is not null;
            created = wasIdempotent ? existing : TdlsMutations.QueuePending(engine.Tdls, facility, callsign, cid, nowUtc);
        }

        if (created is null)
        {
            return new CommandResult(false, "Failed to queue PDC");
        }

        return new CommandResult(
            true,
            wasIdempotent ? $"PDC already queued for {callsign} at {facility}" : $"PDC queued for {callsign} at {facility}"
        );
    }

    // ── TDLSS — send the Pending PDC ──────────────────────────────

    private static CommandResult HandleSend(SimulationEngine engine, string callsign, TdlsSendCommand cmd)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "TDLSS requires an aircraft selection");
        }

        if (cmd.Fields.Count != 9)
        {
            return new CommandResult(false, $"TDLSS requires nine fields, got {cmd.Fields.Count}");
        }

        var facility = ResolveFacility(engine, callsign, out var failureReason);
        if (facility is null)
        {
            return new CommandResult(false, failureReason ?? "Unable to resolve TDLS facility");
        }

        var payload = TdlsMutations.ClearancePayloadFromFields(cmd.Fields);
        if (!ValidateMandatoryFields(engine.Tdls, facility, payload, out var mandatoryError))
        {
            return new CommandResult(false, mandatoryError);
        }

        var nowUtc = engine.Scenario!.SimTimeUtc;

        TdlsItemRecord? sent;
        lock (engine.Tdls.Gate)
        {
            var existing = TdlsMutations.FindActiveItem(engine.Tdls, facility, callsign);
            if (existing is null)
            {
                return new CommandResult(false, $"No queued PDC for {callsign} at {facility} (use TDLSQ first)");
            }

            if (existing.Status != TdlsItemStatus.Pending)
            {
                return new CommandResult(false, $"PDC for {callsign} at {facility} already in status {existing.Status} — cannot re-send");
            }

            sent = TdlsMutations.MarkSent(engine.Tdls, existing.Id, payload, nowUtc);
            if (sent is not null)
            {
                engine.Tdls.ScheduledWilcoAt[sent.Id] = nowUtc + DefaultWilcoDelay;
            }
        }

        if (sent is null)
        {
            return new CommandResult(false, "Failed to mark PDC as Sent");
        }

        EmitSendTerminal(engine, callsign, facility, payload);

        return new CommandResult(true, $"PDC sent to {callsign} at {facility}");
    }

    // ── TDLSW — mark Sent → Wilco ─────────────────────────────────

    /// <summary>
    /// Manual or auto-WILCO. Idempotent: if the item isn't Sent, returns success with a benign
    /// message (the scheduler may double-fire after a controller-issued TDLSW; that's fine).
    /// </summary>
    private static CommandResult HandleWilco(SimulationEngine engine, string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "TDLSW requires an aircraft selection");
        }

        var facility = ResolveFacility(engine, callsign, out var failureReason);
        if (facility is null)
        {
            return new CommandResult(false, failureReason ?? "Unable to resolve TDLS facility");
        }

        var nowUtc = engine.Scenario!.SimTimeUtc;

        lock (engine.Tdls.Gate)
        {
            var existing = TdlsMutations.FindActiveItem(engine.Tdls, facility, callsign);
            if (existing is null)
            {
                return new CommandResult(true, $"No active PDC for {callsign} (already removed)");
            }

            if (existing.Status != TdlsItemStatus.Sent)
            {
                return new CommandResult(true, $"PDC for {callsign} already at {existing.Status}");
            }

            var updated = TdlsMutations.MarkWilco(engine.Tdls, existing.Id, nowUtc);
            if (updated is not null)
            {
                engine.Tdls.ScheduledWilcoAt.Remove(updated.Id);
            }
        }

        return new CommandResult(true, $"PDC for {callsign} acknowledged (WILCO)");
    }

    // ── TDLSDUMP — remove + lockout ──────────────────────────────

    private static CommandResult HandleDump(SimulationEngine engine, string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "TDLSDUMP requires an aircraft selection");
        }

        var facility = ResolveFacility(engine, callsign, out var failureReason);
        if (facility is null)
        {
            return new CommandResult(false, failureReason ?? "Unable to resolve TDLS facility");
        }

        TdlsItemRecord? dumped;
        lock (engine.Tdls.Gate)
        {
            var existing = TdlsMutations.FindActiveItem(engine.Tdls, facility, callsign);
            if (existing is null)
            {
                return new CommandResult(false, $"No PDC for {callsign} at {facility} to dump");
            }

            dumped = TdlsMutations.Dump(engine.Tdls, existing.Id);
        }

        if (dumped is null)
        {
            return new CommandResult(false, "Failed to dump PDC");
        }

        return new CommandResult(true, $"PDC for {callsign} dumped — issue clearance by voice");
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Looks up the TDLS facility that serves the aircraft's filed departure airport. Returns
    /// null with a populated <paramref name="failureReason"/> if the aircraft isn't found, has
    /// no filed departure, or the departure airport isn't served by any TDLS-configured facility.
    /// </summary>
    private static string? ResolveFacility(SimulationEngine engine, string callsign, out string? failureReason)
    {
        var aircraft = engine.FindAircraft(callsign);
        if (aircraft is null)
        {
            failureReason = $"Aircraft {callsign} not found";
            return null;
        }

        var dep = aircraft.FlightPlan?.Departure;
        if (string.IsNullOrEmpty(dep))
        {
            failureReason = $"Aircraft {callsign} has no filed departure airport";
            return null;
        }

        var facility = TdlsMutations.ResolveFacilityForAirport(engine.Tdls, dep);
        if (facility is null)
        {
            failureReason = $"No TDLS facility configured for {dep}";
            return null;
        }

        failureReason = null;
        return facility;
    }

    private static bool ValidateMandatoryFields(TdlsState state, string facilityId, TdlsClearance payload, out string? error)
    {
        if (!state.Configs.TryGetValue(facilityId, out var config))
        {
            error = $"No TDLS config loaded for facility {facilityId}";
            return false;
        }

        if (config.MandatorySid && string.IsNullOrEmpty(payload.Sid))
        {
            error = "MANDATORY FIELD NOT SET: SID";
            return false;
        }
        if (config.MandatoryExpect && string.IsNullOrEmpty(payload.Expect))
        {
            error = "MANDATORY FIELD NOT SET: Expect";
            return false;
        }
        if (config.MandatoryClimbout && string.IsNullOrEmpty(payload.Climbout))
        {
            error = "MANDATORY FIELD NOT SET: Climbout";
            return false;
        }
        if (config.MandatoryClimbvia && string.IsNullOrEmpty(payload.Climbvia))
        {
            error = "MANDATORY FIELD NOT SET: Climb via";
            return false;
        }
        if (config.MandatoryInitialAlt && string.IsNullOrEmpty(payload.InitialAlt))
        {
            error = "MANDATORY FIELD NOT SET: Maintain";
            return false;
        }
        if (config.MandatoryDepFreq && string.IsNullOrEmpty(payload.DepFreq))
        {
            error = "MANDATORY FIELD NOT SET: Dep Freq";
            return false;
        }
        if (config.MandatoryContactInfo && string.IsNullOrEmpty(payload.ContactInfo))
        {
            error = "MANDATORY FIELD NOT SET: Contact Info";
            return false;
        }
        if (config.MandatoryLocalInfo && string.IsNullOrEmpty(payload.LocalInfo))
        {
            error = "MANDATORY FIELD NOT SET: Local Info";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Emits a terminal entry mirroring the ACARS PDC message format the pilot's client
    /// receives (see docs/vtdls/img/pilot-pdc.png). Instructors see exactly what the student
    /// sent — same wire formatting, no editor-internal field names. Kind is <c>"Tdls"</c> so
    /// the entry routes to the dedicated vTDLS terminal channel (defaults to amber).
    /// </summary>
    private static void EmitSendTerminal(SimulationEngine engine, string callsign, string facility, TdlsClearance payload)
    {
        var ac = engine.World.GetSnapshot().FirstOrDefault(a => string.Equals(a.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        engine.Tdls.Configs.TryGetValue(facility, out var cfg);

        var message = FormatPilotPdcMessage(callsign, facility, ac, cfg, payload);
        engine.EmitTerminal("Tdls", callsign, message);
    }

    /// <summary>
    /// Builds the ACARS-style PDC summary for the terminal entry. Mirrors the pilot's
    /// receive format:
    /// <c>ACARS: PDC | CALLSIGN: ... | EQUIPMENT: ... | DEPARTURE: ... | DESTINATION: ... |
    /// ROUTE: ... | ALTITUDE: ... | SQUAWK: ... | REMARKS: CLEARED &lt;SID&gt; DEPARTURE
    /// &lt;ClimbVia&gt; EXP &lt;Alt&gt; &lt;Expect&gt;,DPFRQ &lt;Freq&gt; &lt;Contact&gt; &lt;Local&gt;</c>.
    /// SID/Transition IDs from the canonical command are resolved to names via the facility's
    /// TDLS config; the other field values are already in display form on the wire.
    /// </summary>
    public static string FormatPilotPdcMessage(string callsign, string facility, AircraftState? ac, TdlsConfig? cfg, TdlsClearance payload)
    {
        var fp = ac?.FlightPlan;
        // The bare designator, not the filed string: a plan filed as "H/B763/L" already carries the suffix, and the
        // suffix is appended below, so composing from the filed form would print "H/B763/L/L".
        var aircraftType = !string.IsNullOrEmpty(fp?.AircraftType) ? fp!.BaseAircraftType : (ac?.AircraftType ?? "");
        var equipment = string.IsNullOrEmpty(fp?.EquipmentSuffix) ? aircraftType : $"{aircraftType}/{fp.EquipmentSuffix}";
        var dep = string.IsNullOrEmpty(fp?.Departure) ? "????" : fp.Departure;
        var dest = string.IsNullOrEmpty(fp?.Destination) ? "????" : fp.Destination;
        var route = fp?.Route ?? "";
        var cruiseFeet = fp?.Altitude.CruiseFeet ?? 0;
        var altHundreds = cruiseFeet > 0 ? (cruiseFeet / 100).ToString() : "";
        var squawk = ac is not null && ac.Transponder.AssignedCode > 0 ? ac.Transponder.AssignedCode.ToString("D4") : "0000";

        var remarks = FormatPdcRemarks(cfg, payload, altHundreds);

        return $"ACARS: PDC | CALLSIGN: {callsign} | EQUIPMENT: {equipment} | DEPARTURE: {dep} | DESTINATION: {dest} | ROUTE: {route} | ALTITUDE: {altHundreds} | SQUAWK: {squawk} | REMARKS: {remarks}";
    }

    /// <summary>
    /// Builds the REMARKS clause of the ACARS PDC text. Resolves SID and Transition IDs to
    /// FE-defined names via the facility's TDLS config so the line reads naturally
    /// (<c>CLEARED OAK6 DEPARTURE CLIMB VIA SID EXP 220 10 MIN AFT DP,DPFRQ 133.0 CTC 121.65 TO PUSH</c>),
    /// not as opaque ULIDs. The "no transition" placeholder (name = "- - - -") is omitted.
    /// </summary>
    private static string FormatPdcRemarks(TdlsConfig? cfg, TdlsClearance p, string altHundreds)
    {
        var sb = new StringBuilder("CLEARED");

        var sid = ResolveSid(cfg, p.Sid);
        if (sid is not null && !IsPlaceholderName(sid.Name))
        {
            sb.Append(' ').Append(sid.Name);
        }
        sb.Append(" DEPARTURE");

        var transition = ResolveTransition(sid, p.Transition);
        if (transition is not null && !IsPlaceholderName(transition.Name))
        {
            sb.Append(' ').Append(transition.Name);
        }

        if (!string.IsNullOrEmpty(p.Climbout))
        {
            sb.Append(' ').Append(p.Climbout);
        }
        if (!string.IsNullOrEmpty(p.Climbvia))
        {
            sb.Append(' ').Append(p.Climbvia);
        }

        if (!string.IsNullOrEmpty(p.Expect))
        {
            sb.Append(" EXP");
            if (!string.IsNullOrEmpty(altHundreds))
            {
                sb.Append(' ').Append(altHundreds);
            }
            sb.Append(' ').Append(p.Expect);
        }

        if (!string.IsNullOrEmpty(p.InitialAlt))
        {
            sb.Append(" MAINT ").Append(p.InitialAlt);
        }

        if (!string.IsNullOrEmpty(p.DepFreq))
        {
            sb.Append(",DPFRQ ").Append(p.DepFreq);
        }

        // Contact info values already include their own prefix (e.g. "CTC 121.65 TO PUSH"),
        // so append verbatim.
        if (!string.IsNullOrEmpty(p.ContactInfo))
        {
            sb.Append(' ').Append(p.ContactInfo);
        }

        if (!string.IsNullOrEmpty(p.LocalInfo))
        {
            sb.Append(' ').Append(p.LocalInfo);
        }

        return sb.ToString();
    }

    private static TdlsSidConfig? ResolveSid(TdlsConfig? cfg, string? sidId)
    {
        if (cfg is null || string.IsNullOrEmpty(sidId))
        {
            return null;
        }
        return cfg.Sids.FirstOrDefault(s => string.Equals(s.Id, sidId, StringComparison.Ordinal));
    }

    private static TdlsSidTransitionConfig? ResolveTransition(TdlsSidConfig? sid, string? transitionId)
    {
        if (sid is null || string.IsNullOrEmpty(transitionId))
        {
            return null;
        }
        return sid.Transitions.FirstOrDefault(t => string.Equals(t.Id, transitionId, StringComparison.Ordinal));
    }

    /// <summary>Detects the FE's "no value" placeholder names so they don't leak into the PDC text as literal dashes.</summary>
    private static bool IsPlaceholderName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }
        var trimmed = name.Replace(" ", "").Replace("-", "");
        return string.IsNullOrEmpty(trimmed);
    }
}
