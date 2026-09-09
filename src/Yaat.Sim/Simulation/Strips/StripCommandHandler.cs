using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// What a strip verb answers: the verdict, and — for a verb that creates an item under a minted id (<c>SEP</c>,
/// <c>HSC</c>, <c>SCAN</c>, <c>BLANK</c>) — the id it minted or reused. Null for every other strip verb and for a refusal; the
/// router bakes a non-null one onto the record so replay creates the item under the same id.
/// </summary>
internal sealed record StripApplyResult(CommandResult Result, string? StripId);

/// <summary>
/// Single dispatch point for every canonical strip command — STRIP, AN, STRIPD, STRIPO,
/// HSC, HSA, HSD, HSM, HSO, HSS, SEP, SEPD, BLANK, BLANKD. Handlers mutate the engine's
/// <see cref="SimulationEngine.Strips"/> through <see cref="StripMutations"/>, which records what they touched in
/// <see cref="FlightStripState.Changes"/>; the host drains that and broadcasts. The bay and position lookups are the
/// <see cref="ArtccConfigResolver"/> extensions over <see cref="SimScenarioState.ArtccConfig"/>, so the verbs decide
/// from engine state alone and every run kind reaches the same verdict.
/// </summary>
internal static class StripCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("StripCommandHandler");

    private const int HalfStripMaxLines = 6;

    /// <summary>
    /// Applies one strip verb. <paramref name="bakedStripId"/> is the id a creating verb minted at the live run,
    /// replayed from the record so the item is created under it; null on a fresh action, where the verb mints its own
    /// and reports it back for the router to bake.
    /// </summary>
    internal static StripApplyResult Handle(SimulationEngine engine, ParsedCommand parsed, string callsign, string? bakedStripId)
    {
        try
        {
            return parsed switch
            {
                StripMoveCommand cmd => Verdict(HandleStripMove(engine, callsign, cmd)),
                StripScanCommand cmd => HandleStripScan(engine, callsign, cmd, bakedStripId),
                StripAnnotateCommand cmd => Verdict(HandleStripAnnotate(engine, callsign, cmd)),
                StripDeleteCommand cmd => Verdict(HandleStripDelete(engine, callsign, cmd)),
                StripOffsetCommand cmd => Verdict(HandleStripOffset(engine, callsign, cmd)),
                HalfStripCreateCommand cmd => HandleHalfStripCreate(engine, callsign, cmd, bakedStripId),
                HalfStripAmendCommand cmd => Verdict(HandleHalfStripAmend(engine, callsign, cmd)),
                HalfStripDeleteCommand cmd => Verdict(HandleHalfStripDelete(engine, callsign, cmd)),
                HalfStripMoveCommand cmd => Verdict(HandleHalfStripMove(engine, callsign, cmd)),
                HalfStripOffsetCommand cmd => Verdict(HandleHalfStripOffset(engine, callsign, cmd)),
                HalfStripSlideCommand cmd => Verdict(HandleHalfStripSlide(engine, callsign, cmd)),
                SeparatorCreateCommand cmd => HandleSeparatorCreate(engine, cmd, bakedStripId),
                SeparatorDeleteCommand cmd => Verdict(HandleSeparatorDelete(engine, cmd)),
                SeparatorEditCommand cmd => Verdict(HandleSeparatorEdit(engine, cmd)),
                SeparatorMoveCommand cmd => Verdict(HandleSeparatorMove(engine, cmd)),
                BlankCreateCommand cmd => HandleBlankCreate(engine, cmd, bakedStripId),
                BlankDeleteCommand cmd => Verdict(HandleBlankDelete(engine, cmd)),
                _ => Verdict(new CommandResult(false, "Unknown strip command")),
            };
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Strip command handler threw for {Type} callsign={Callsign}", parsed.GetType().Name, callsign);
            return Verdict(new CommandResult(false, $"Strip command error: {ex.Message}"));
        }
    }

    /// <summary>A verb that mints no id: the verdict alone.</summary>
    private static StripApplyResult Verdict(CommandResult result) => new(result, null);

    // ── Full strips ───────────────────────────────────────────────

    private static CommandResult HandleStripMove(SimulationEngine engine, string callsign, StripMoveCommand cmd)
    {
        // Optional leading STRIP_<id> token: strips-tab and translator paths
        // emit it so a scanned copy <c>STRIP_{callsign}_{shortGuid}</c> can be
        // moved without colliding with the original. Terminal users keep
        // typing <c>STRIP bay/rack/index</c> and resolve via callsign.
        string? explicitId = null;
        IReadOnlyList<string> dstTokens = cmd.Tokens;
        if (cmd.Tokens.Count > 0 && IsFullStripId(cmd.Tokens[0]))
        {
            explicitId = cmd.Tokens[0];
            dstTokens = cmd.Tokens.Skip(1).ToList();
        }

        if (explicitId is null && string.IsNullOrEmpty(callsign))
        {
            return new CommandResult(false, "STRIP requires an aircraft selection");
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return new CommandResult(false, "No accessible strip bays for current position");
        }

        var resolved = StripMutations.ResolveStripDest(dstTokens, accessible);
        if (resolved is null)
        {
            return new CommandResult(false, $"Unknown strip bay or invalid rack/index: {string.Join(' ', dstTokens)}");
        }

        var (bay, facilityId, rack, indexOrNull, _) = resolved.Value;
        if (rack < 0 || rack >= bay.NumberOfRacks)
        {
            return new CommandResult(false, $"Rack {rack + 1} out of range (bay {bay.Name} has {bay.NumberOfRacks} racks)");
        }

        // STRIP moves an existing flight strip; it never fabricates one. Both the
        // id-form and the bare-callsign form require the target to already exist.
        // A bare-callsign STRIP for an aircraft with no strip — e.g. an arrival,
        // whose strip is keyed ARRIVAL_{callsign} rather than STRIP_{callsign} —
        // previously synthesized an empty phantom DepartureStrip and reported
        // success, which was the source of the issue #278 "Move All to Bay" spam.
        var stripId = explicitId ?? $"STRIP_{callsign}";
        if (!engine.Strips.Items.ContainsKey(stripId))
        {
            return new CommandResult(false, explicitId is not null ? $"No flight strip {stripId}" : $"No flight strip for {callsign}");
        }
        var state = engine.Strips;

        // Index omitted → append to the tail of the rack (CRC bottom-up FIFO:
        // the new strip lands at the first available bottom slot). Resolved
        // under the state gate so concurrent STRIP requests don't race.
        int index;
        lock (state.Gate)
        {
            if (indexOrNull is int explicitIndex)
            {
                index = explicitIndex;
            }
            else
            {
                // Compute the append index (tail of the rack). Read directly to
                // avoid EnsureRack's side effect — a rack that doesn't exist yet
                // means index 0. rackRows is List<string>[] so check Length, not
                // Count (Count on an array only resolves to the LINQ extension).
                var currentCount = 0;
                if (
                    state.Bays.TryGetValue(bay.Id, out var racks)
                    && racks.TryGetValue(rack.ToString(System.Globalization.CultureInfo.InvariantCulture), out var rackRows)
                    && rackRows.Length > 0
                )
                {
                    currentCount = rackRows[0].Count;
                }
                // If the strip is already in this rack, moving "to the end" is
                // relative to the other N-1 strips, not N (the source slot
                // will vacate). RemoveFromAllBaysLocked runs before the insert
                // inside MoveStripToBayRack, so subtract 1 when already present.
                var alreadyInThisRack =
                    state.Items.TryGetValue(stripId, out var existingRecord)
                    && string.Equals(existingRecord.BayId, bay.Id, StringComparison.Ordinal)
                    && existingRecord.Rack == rack;
                index = alreadyInThisRack && currentCount > 0 ? currentCount - 1 : currentCount;
            }

            // Existence is guaranteed by the guard above; update the record's
            // position in place (never synthesize). The TryGetValue guards only
            // against a concurrent removal between the guard and this lock.
            if (state.Items.TryGetValue(stripId, out var existing))
            {
                state.Items[stripId] = existing with { FacilityId = facilityId, BayId = bay.Id, Rack = rack, Index = index };
            }
        }

        StripMutations.MoveStripToBayRack(state, stripId, bay.Id, rack, index);

        // Task #19 — push-warning for external bays. If the destination bay is
        // owned by another facility (IsExternal=true) and no connected CRC
        // controller is staffed at a position in that facility, the pushed
        // strip has no receiver. Surface this via the command-result message
        // so the pushing client sees it in the status bar / terminal log.
        var dest = FormatResolvedSlot(bay.Name, rack, index, indexOrNull is null);
        var accessibleEntry = accessible.FirstOrDefault(a => a.Bay.Id == bay.Id);
        if (accessibleEntry is { IsExternal: true } && !AnyControllerStaffsFacility(engine, accessibleEntry.Owner.Id))
        {
            return new CommandResult(true, $"Strip moved to {dest} — WARNING: no controller connected at {accessibleEntry.Owner.Id}");
        }
        return new CommandResult(true, $"Strip moved to {dest}");
    }

    /// <summary>
    /// SCAN — copies the aircraft's full strip into an external facility's bay
    /// while leaving the original strip in place. Differs from STRIP (which
    /// relocates) so the originating controller keeps the working copy on
    /// their rack while the receiving facility gets a coordination preview.
    ///
    /// The destination must be marked <c>IsExternal</c> on the resolved
    /// <see cref="AccessibleBay"/>; in-facility moves should use STRIP. The
    /// copy gets a fresh id <c>STRIP_{callsign}_{guid}</c> (keeps the STRIP_
    /// prefix so vStrips renders it as a regular full strip; suffix lets
    /// multiple SCANs to the same bay stack as distinct copies). Each scan
    /// is independent — no cascade on STRIPD, no annotation sync, no
    /// canonical-command path to delete a copy directly (the receiving CRC
    /// vStrips can drop it via its own DeleteStripItem).
    /// </summary>
    private static StripApplyResult HandleStripScan(SimulationEngine engine, string callsign, StripScanCommand cmd, string? bakedStripId)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return Verdict(new CommandResult(false, "SCAN requires an aircraft selection"));
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return Verdict(new CommandResult(false, "No accessible strip bays for current position"));
        }

        var resolved = StripMutations.ResolveStripDest(cmd.Tokens, accessible);
        if (resolved is null)
        {
            return Verdict(new CommandResult(false, $"Unknown strip bay or invalid rack/index: {string.Join(' ', cmd.Tokens)}"));
        }

        var (bay, facilityId, rack, indexOrNull, _) = resolved.Value;
        if (rack < 0 || rack >= bay.NumberOfRacks)
        {
            return Verdict(new CommandResult(false, $"Rack {rack + 1} out of range (bay {bay.Name} has {bay.NumberOfRacks} racks)"));
        }

        var accessibleEntry = accessible.FirstOrDefault(a => a.Bay.Id == bay.Id);
        if (accessibleEntry is not { IsExternal: true })
        {
            return Verdict(new CommandResult(false, $"SCAN destination must be an external bay (use STRIP for in-facility moves)"));
        }

        var sourceStripId = $"STRIP_{callsign}";
        var state = engine.Strips;

        // A run that already holds the recorded copy — a snapshot-based restore the record is re-applied over —
        // must not stack a second one under a fresh id; the print is idempotent, like a recorded strip request's.
        if (bakedStripId is not null && state.Items.ContainsKey(bakedStripId))
        {
            return new StripApplyResult(new CommandResult(true, "Strip already scanned"), bakedStripId);
        }

        string copyId;
        int index;
        StripItemRecord copyRecord;
        lock (state.Gate)
        {
            if (!state.Items.TryGetValue(sourceStripId, out var source))
            {
                return Verdict(new CommandResult(false, $"No flight strip for {callsign} to scan"));
            }

            if (indexOrNull is int explicitIndex)
            {
                index = explicitIndex;
            }
            else
            {
                // Append-to-tail: read current rack count without going through
                // EnsureRack (which mutates). The copy is always fresh, so no
                // "already in this rack" subtraction like HandleStripMoveAsync.
                var currentCount = 0;
                if (
                    state.Bays.TryGetValue(bay.Id, out var racks)
                    && racks.TryGetValue(rack.ToString(System.Globalization.CultureInfo.InvariantCulture), out var rackRows)
                    && rackRows.Length > 0
                )
                {
                    currentCount = rackRows[0].Count;
                }
                index = currentCount;
            }

            // The id the live run drew, or a fresh one keyed on callsign + short guid suffix. Keeping the
            // STRIP_ prefix lets vStrips reuse its full-strip rendering path;
            // the suffix is what differentiates copies and prevents collision
            // with the canonical STRIP_{callsign} record.
            copyId = bakedStripId ?? StripMutations.NewScanStripId(callsign, state);

            // Deep-copy FieldValues — StripItemRecord stores it as string[] (mutable),
            // so a shared reference would let later annotations on either strip leak
            // into the other.
            copyRecord = new StripItemRecord(
                copyId,
                callsign,
                source.Type,
                source.IsOffset,
                source.FieldValues.ToArray(),
                facilityId,
                bay.Id,
                rack,
                index
            );
            state.Items[copyId] = copyRecord;
        }

        StripMutations.MoveStripToBayRack(state, copyId, bay.Id, rack, index);

        var dest = FormatResolvedSlot(bay.Name, rack, index, indexOrNull is null);
        if (!AnyControllerStaffsFacility(engine, accessibleEntry.Owner.Id))
        {
            return new StripApplyResult(
                new CommandResult(true, $"Strip scanned to {dest} — WARNING: no controller connected at {accessibleEntry.Owner.Id}"),
                copyId
            );
        }
        return new StripApplyResult(new CommandResult(true, $"Strip scanned to {dest}"), copyId);
    }

    /// <summary>
    /// True when a leading STRIP token is an explicit full-strip id rather than a
    /// bay name. Full strips are keyed <c>STRIP_{callsign}</c> (departures, scanned
    /// copies) or <c>ARRIVAL_{callsign}</c> (arrival strips); the strips UI and the
    /// CRC→canonical translator always emit the id form so a specific strip — most
    /// importantly an arrival strip or a scanned copy sharing a callsign with its
    /// original — is addressed unambiguously.
    /// </summary>
    private static bool IsFullStripId(string token) =>
        token.StartsWith("STRIP_", StringComparison.OrdinalIgnoreCase) || token.StartsWith("ARRIVAL_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats the final resolved slot as a user-facing destination string —
    /// <c>"Ground 1/2/3"</c>. When <paramref name="appended"/> is true the
    /// wire omitted the index so the server picked the bottom-up
    /// first-available slot; append <c>" (appended)"</c> so the terminal
    /// makes that distinction clear.
    /// </summary>
    private static string FormatResolvedSlot(string bayName, int rack, int resolvedIndex, bool appended)
    {
        var wire = $"{bayName}/{rack + 1}/{resolvedIndex + 1}";
        return appended ? $"{wire} (appended)" : wire;
    }

    /// <summary>
    /// True when at least one attended CRC position sits in <paramref name="facilityId"/>. Used to decide whether a
    /// strip pushed to an external bay has a live receiver; a false return yields a client-visible push warning, and a
    /// session with nobody signed on (a Yaat-only room) always returns false — the trainer alone cannot receive their
    /// own push.
    ///
    /// <para>
    /// Attendance is the recorded derivation of the room's CRC clients, so every run kind answers this the same way;
    /// secondary positions are not in it, so a controller who staffs the receiving facility only as a secondary reads
    /// as absent here.
    /// </para>
    /// </summary>
    private static bool AnyControllerStaffsFacility(SimulationEngine engine, string facilityId)
    {
        if (string.IsNullOrEmpty(facilityId) || engine.Scenario?.ArtccConfig is not { } config)
        {
            return false;
        }

        return engine.Attendance.PositionIds.Any(id => string.Equals(config.FindPosition(id).FacilityId, facilityId, StringComparison.Ordinal));
    }

    private static CommandResult HandleStripAnnotate(SimulationEngine engine, string callsign, StripAnnotateCommand cmd)
    {
        // Id-form: <c>AN STRIP_{id} 3 RV</c> targets a specific strip (e.g.
        // a scanned copy that shares its callsign with the original).
        // Terminal entry uses the bare <c>AN 3 RV</c> form keyed by callsign.
        var state = engine.Strips;
        string stripId;
        var isIdForm = cmd.StripId is not null;
        if (isIdForm)
        {
            stripId = cmd.StripId!;
        }
        else
        {
            if (string.IsNullOrEmpty(callsign))
            {
                return new CommandResult(false, "AN requires an aircraft selection");
            }
            stripId = $"STRIP_{callsign}";
        }

        // A strip must be filed into a bay before it can be annotated — real vStrips edits
        // annotation boxes in a bay; the printer view only offers "Move to Bay". Rejecting here
        // (rather than silently mutating an unfiled strip) surfaces the "still in the printer"
        // state instead of hiding it.
        var guardError = StripMustBeInBayError(state, stripId, callsign, isIdForm);
        if (guardError is not null)
        {
            return new CommandResult(false, guardError);
        }

        // CRC vStrips convention: pressing Shift+/ inserts a checkmark (U+2713)
        // per docs/crc/vstrips.md:130. Replace every '?' in the annotation text
        // with '✓' so both the inline editor UI (which sends AN with the raw
        // character) and terminal input ('AN 3 ?') get the same substitution.
        var text = cmd.Text?.Replace('?', '✓');

        var updated = StripMutations.SetAnnotationBox(state, stripId, cmd.Box, text);
        if (updated is null)
        {
            return new CommandResult(false, $"Invalid annotation box {cmd.Box}");
        }

        var desc = string.IsNullOrEmpty(text) ? $"Box {cmd.Box} cleared" : $"Box {cmd.Box}: {text}";
        return new CommandResult(true, desc);
    }

    private static CommandResult HandleStripDelete(SimulationEngine engine, string callsign, StripDeleteCommand cmd)
    {
        // Id-form (<c>STRIPD STRIP_{id}</c>) targets a specific full strip —
        // the only way to address a scanned copy <c>STRIP_{callsign}_{short}</c>
        // without removing the original. Bare <c>STRIPD</c> resolves via the
        // selected callsign (terminal entry).
        var stripId = cmd.StripId;
        if (stripId is null)
        {
            if (string.IsNullOrEmpty(callsign))
            {
                return new CommandResult(false, "STRIPD requires an aircraft selection");
            }
            stripId = $"STRIP_{callsign}";
        }

        if (!StripMutations.DeleteStrip(engine.Strips, stripId))
        {
            return new CommandResult(false, cmd.StripId is not null ? $"No flight strip {stripId}" : $"No flight strip for {callsign}");
        }

        return new CommandResult(true, $"Flight strip {stripId} deleted");
    }

    private static CommandResult HandleStripOffset(SimulationEngine engine, string callsign, StripOffsetCommand cmd)
    {
        var stripId = cmd.StripId;
        var isIdForm = stripId is not null;
        if (stripId is null)
        {
            if (string.IsNullOrEmpty(callsign))
            {
                return new CommandResult(false, "STRIPO requires an aircraft selection");
            }
            stripId = $"STRIP_{callsign}";
        }

        // Offset slides a strip within its rack — meaningless for a strip that isn't filed into a
        // bay yet, so reject it in the printer for the same reason as annotation (above).
        var guardError = StripMustBeInBayError(engine.Strips, stripId, callsign, isIdForm);
        if (guardError is not null)
        {
            return new CommandResult(false, guardError);
        }

        var result = StripMutations.ToggleOffset(engine.Strips, stripId);
        if (result is null)
        {
            return new CommandResult(false, cmd.StripId is not null ? $"No flight strip {stripId}" : $"No flight strip for {callsign}");
        }

        return new CommandResult(true, result.Value ? "Offset on" : "Offset off");
    }

    /// <summary>
    /// Guard for full-strip operations that only make sense once a strip is filed into a bay
    /// (annotation, offset). Returns an error message when the strip is missing or still in the
    /// printer (<see cref="StripItemRecord.BayId"/> empty), or null when it's in a bay. The label
    /// tracks the id-form vs callsign-form entry so the message matches the user's input.
    /// </summary>
    private static string? StripMustBeInBayError(FlightStripState state, string stripId, string callsign, bool isIdForm)
    {
        if (!state.Items.TryGetValue(stripId, out var record))
        {
            return isIdForm ? $"No flight strip {stripId}" : $"No flight strip for {callsign}";
        }

        if (string.IsNullOrEmpty(record.BayId))
        {
            return isIdForm
                ? $"Flight strip {stripId} is still in the printer — move it to a bay first"
                : $"Flight strip for {callsign} is still in the printer — move it to a bay first";
        }

        return null;
    }

    // ── Half-strip create / amend / delete ────────────────────────

    private static StripApplyResult HandleHalfStripCreate(SimulationEngine engine, string callsign, HalfStripCreateCommand cmd, string? bakedStripId)
    {
        var resolved = ResolveBayByName(engine, cmd.FacilityId, cmd.BayName);
        if (resolved.Error is not null)
        {
            return Verdict(new CommandResult(false, resolved.Error));
        }

        var bay = resolved.Bay!;
        var ownerFacilityId = resolved.OwnerFacilityId!;
        var rack = cmd.Rack ?? 0;
        if (rack < 0 || rack >= bay.NumberOfRacks)
        {
            return Verdict(new CommandResult(false, $"Rack {rack + 1} out of range (bay {bay.Name} has {bay.NumberOfRacks} racks)"));
        }

        var userLines = cmd.Lines ?? [];
        var totalLines = string.IsNullOrEmpty(callsign) ? userLines.Count : userLines.Count + 1;
        if (totalLines > HalfStripMaxLines)
        {
            return Verdict(new CommandResult(false, $"HSC supports at most {HalfStripMaxLines} lines (got {totalLines})"));
        }

        // A run that already holds the recorded half strip — a snapshot-based restore the record is re-applied
        // over — keeps the one it has rather than creating a second under a fresh id.
        if (bakedStripId is not null && engine.Strips.Items.ContainsKey(bakedStripId))
        {
            return new StripApplyResult(new CommandResult(true, $"Half-strip already at {bay.Name}/{rack + 1}"), bakedStripId);
        }

        // An empty half-strip is permitted — context-menu "Add half-strip"
        // creates one with no callsign and no lines so the controller can
        // fill the 3×2 inline cell grid afterwards. The fields array always
        // has at least one (empty) slot so the LookupKey resolution path
        // doesn't trip on a zero-length array.
        var fields = totalLines == 0 ? new[] { string.Empty } : BuildHalfStripLines(callsign, userLines, totalLines);
        var stripId = bakedStripId ?? StripMutations.NewHalfStripId(engine.Strips);
        var record = new StripItemRecord(
            stripId,
            string.IsNullOrEmpty(callsign) ? null : callsign,
            StripMutations.HalfStripLeft,
            false,
            fields,
            ownerFacilityId,
            bay.Id,
            rack,
            0
        );

        var strips = engine.Strips;
        lock (strips.Gate)
        {
            strips.Items[stripId] = record;
        }
        StripMutations.AppendStripToBay(strips, bay.Id, rack, stripId);

        return new StripApplyResult(new CommandResult(true, $"Half-strip created at {bay.Name}/{rack + 1}"), stripId);
    }

    private static string[] BuildHalfStripLines(string callsign, IReadOnlyList<string> userLines, int totalLines)
    {
        var fields = new string[totalLines];
        var idx = 0;
        if (!string.IsNullOrEmpty(callsign))
        {
            fields[idx++] = callsign;
        }

        for (var i = 0; i < userLines.Count; i++, idx++)
        {
            fields[idx] = userLines[i];
        }

        return fields;
    }

    private static CommandResult HandleHalfStripAmend(SimulationEngine engine, string callsign, HalfStripAmendCommand cmd)
    {
        var scope = ResolveOptionalBayScope(engine, cmd.FacilityId, cmd.BayName);
        if (scope.Error is not null)
        {
            return new CommandResult(false, scope.Error);
        }

        var tokens = cmd.Tokens ?? [];
        var decisionResult = DecideAmendFields(callsign, tokens);
        if (decisionResult.Error is not null)
        {
            return new CommandResult(false, decisionResult.Error);
        }

        var lookupKey = decisionResult.LookupKey!;
        var newFields = decisionResult.NewFields!;

        if (newFields.Length > HalfStripMaxLines)
        {
            return new CommandResult(false, $"HSA supports at most {HalfStripMaxLines} lines (got {newFields.Length})");
        }

        var matches = FindHalfStripMatches(engine, lookupKey, scope.BayId, cmd.Rack);
        var matchErr = SingleMatchOrError(matches, lookupKey, scope.Suffix);
        if (matchErr is not null)
        {
            return new CommandResult(false, matchErr);
        }

        var updated = StripMutations.UpdateStripFields(engine.Strips, matches[0].Id, newFields);
        if (updated is null)
        {
            return new CommandResult(false, "Half-strip disappeared during amend");
        }

        return new CommandResult(true, $"Half-strip '{lookupKey}' amended");
    }

    /// <summary>
    /// Resolves the HSA lookup key and replacement lines. The <c>HSTRIP_</c> id form
    /// is always a literal replacement (key = id, lines = the remaining tokens) no
    /// matter which aircraft is selected — the strips UI and the CRC translator
    /// send the full FieldValues, callsign line included. The callsign-prepend
    /// convenience applies only to the aircraft-scoped text shorthand.
    /// </summary>
    private static (string? LookupKey, string[]? NewFields, string? Error) DecideAmendFields(string callsign, IReadOnlyList<string> tokens)
    {
        var headIsStripId = tokens.Count > 0 && tokens[0].StartsWith("HSTRIP_", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(callsign) || headIsStripId)
        {
            if (tokens.Count == 0)
            {
                return (null, null, "HSA requires a lookup key (and at least one replacement line)");
            }
            if (tokens.Count == 1)
            {
                return (null, null, "HSA needs at least one replacement line after the key");
            }

            var lookup = tokens[0];
            var fields = new string[tokens.Count - 1];
            for (var i = 1; i < tokens.Count; i++)
            {
                fields[i - 1] = tokens[i];
            }
            return (lookup, fields, null);
        }

        var scoped = new string[tokens.Count + 1];
        scoped[0] = callsign;
        for (var i = 0; i < tokens.Count; i++)
        {
            scoped[i + 1] = tokens[i];
        }
        return (callsign, scoped, null);
    }

    private static CommandResult HandleHalfStripDelete(SimulationEngine engine, string callsign, HalfStripDeleteCommand cmd)
    {
        var scope = ResolveOptionalBayScope(engine, cmd.FacilityId, cmd.BayName);
        if (scope.Error is not null)
        {
            return new CommandResult(false, scope.Error);
        }

        var tokens = cmd.Tokens ?? [];
        string lookupKey;
        if (string.IsNullOrEmpty(callsign))
        {
            if (tokens.Count != 1)
            {
                return new CommandResult(false, "HSD requires exactly one lookup key in global mode");
            }
            lookupKey = tokens[0];
        }
        else
        {
            lookupKey = tokens.Count == 1 ? tokens[0] : callsign;
        }

        var matches = FindHalfStripMatches(engine, lookupKey, scope.BayId, cmd.Rack);
        var matchErr = SingleMatchOrError(matches, lookupKey, scope.Suffix);
        if (matchErr is not null)
        {
            return new CommandResult(false, matchErr);
        }

        StripMutations.DeleteStrip(engine.Strips, matches[0].Id);
        return new CommandResult(true, $"Half-strip '{lookupKey}' deleted");
    }

    // ── Half-strip move / offset / slide ──────────────────────────

    private static CommandResult HandleHalfStripMove(SimulationEngine engine, string callsign, HalfStripMoveCommand cmd)
    {
        if (cmd.Tokens.Count == 0)
        {
            return new CommandResult(false, "HSM requires a destination bay");
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return new CommandResult(false, "No accessible strip bays for current position");
        }

        // Walk backward from the end: the destination spec ends with the last
        // token (which contains a slash for rack/index). Try the smallest
        // suffix that resolves as a valid bay/rack/index — this lets multi-word
        // bays like "Local 1" round-trip through whitespace tokenization.
        StripBayConfig? destBay = null;
        var destRack = 0;
        int? destIndex = null;
        var destStartIdx = -1;
        for (var suffixLen = 1; suffixLen <= cmd.Tokens.Count; suffixLen++)
        {
            var candidate = new List<string>(cmd.Tokens.Skip(cmd.Tokens.Count - suffixLen));
            var resolved = StripMutations.ResolveStripDest(candidate, accessible);
            if (resolved is not null && resolved.Value.TokensConsumed == suffixLen)
            {
                destBay = resolved.Value.Bay;
                destRack = resolved.Value.Rack;
                destIndex = resolved.Value.Index;
                destStartIdx = cmd.Tokens.Count - suffixLen;
                break;
            }
        }

        if (destBay is null)
        {
            return new CommandResult(false, $"Unknown strip bay or invalid rack/index: {string.Join(' ', cmd.Tokens)}");
        }
        if (destRack < 0 || destRack >= destBay.NumberOfRacks)
        {
            return new CommandResult(false, $"Rack {destRack + 1} out of range (bay {destBay.Name} has {destBay.NumberOfRacks} racks)");
        }

        // Leading tokens (everything before the destination) carry the optional
        // source-bay scope and the optional lookup key.
        var leading = cmd.Tokens.Take(destStartIdx).ToList();
        string? srcBayId = null;
        var srcSuffix = "";
        int? srcRack = null;
        string? lookupKey = null;
        if (leading.Count == 1)
        {
            lookupKey = leading[0];
        }
        else if (leading.Count >= 2)
        {
            // Forward-greedy: peel off a source-bay-spec from the front. The
            // smallest match that leaves exactly one trailing token (the
            // lookup key) wins. Source-bay-spec must not include an explicit
            // index (it scopes the lookup, not the destination).
            var matched = false;
            for (var srcLen = 1; srcLen < leading.Count; srcLen++)
            {
                var srcCandidate = leading.Take(srcLen).ToList();
                var srcResolved = StripMutations.ResolveStripDest(srcCandidate, accessible);
                if (
                    srcResolved is not null
                    && srcResolved.Value.TokensConsumed == srcLen
                    && srcResolved.Value.Index is null
                    && leading.Count - srcLen == 1
                )
                {
                    srcBayId = srcResolved.Value.Bay.Id;
                    srcRack = srcResolved.Value.Rack;
                    srcSuffix = $" in {srcResolved.Value.Bay.Name}";
                    lookupKey = leading[^1];
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                return new CommandResult(false, $"Could not resolve HSM source/key tokens: {string.Join(' ', leading)}");
            }
        }

        lookupKey ??= string.IsNullOrEmpty(callsign) ? null : callsign;
        if (string.IsNullOrEmpty(lookupKey))
        {
            return new CommandResult(false, "HSM requires a lookup key or an aircraft selection");
        }

        var matches = FindHalfStripMatches(engine, lookupKey, srcBayId, srcRack);
        var matchErr = SingleMatchOrError(matches, lookupKey, srcSuffix);
        if (matchErr is not null)
        {
            return new CommandResult(false, matchErr);
        }

        var resolvedDestIndex = destIndex ?? 0;
        StripMutations.MoveStripToBayRack(engine.Strips, matches[0].Id, destBay.Id, destRack, resolvedDestIndex);
        var strips = engine.Strips;
        lock (strips.Gate)
        {
            if (strips.Items.TryGetValue(matches[0].Id, out var existing))
            {
                strips.Items[matches[0].Id] = existing with { BayId = destBay.Id, Rack = destRack, Index = resolvedDestIndex };
            }
        }

        var dest = FormatResolvedSlot(destBay.Name, destRack, resolvedDestIndex, appended: destIndex is null);
        return new CommandResult(true, $"Half-strip '{lookupKey}' moved to {dest}");
    }

    private static CommandResult HandleHalfStripOffset(SimulationEngine engine, string callsign, HalfStripOffsetCommand cmd)
    {
        var scope = ResolveOptionalBayScope(engine, cmd.FacilityId, cmd.BayName);
        if (scope.Error is not null)
        {
            return new CommandResult(false, scope.Error);
        }

        var lookupKey = cmd.LookupKey ?? (string.IsNullOrEmpty(callsign) ? null : callsign);
        if (string.IsNullOrEmpty(lookupKey))
        {
            return new CommandResult(false, "HSO requires a lookup key or an aircraft selection");
        }

        var matches = FindHalfStripMatches(engine, lookupKey, scope.BayId, cmd.Rack);
        var matchErr = SingleMatchOrError(matches, lookupKey, scope.Suffix);
        if (matchErr is not null)
        {
            return new CommandResult(false, matchErr);
        }

        var newValue = StripMutations.ToggleOffset(engine.Strips, matches[0].Id);
        return new CommandResult(true, newValue == true ? $"Half-strip '{lookupKey}' offset on" : $"Half-strip '{lookupKey}' offset off");
    }

    private static CommandResult HandleHalfStripSlide(SimulationEngine engine, string callsign, HalfStripSlideCommand cmd)
    {
        var scope = ResolveOptionalBayScope(engine, cmd.FacilityId, cmd.BayName);
        if (scope.Error is not null)
        {
            return new CommandResult(false, scope.Error);
        }

        var lookupKey = cmd.LookupKey ?? (string.IsNullOrEmpty(callsign) ? null : callsign);
        if (string.IsNullOrEmpty(lookupKey))
        {
            return new CommandResult(false, "HSS requires a lookup key or an aircraft selection");
        }

        var matches = FindHalfStripMatches(engine, lookupKey, scope.BayId, cmd.Rack);
        var matchErr = SingleMatchOrError(matches, lookupKey, scope.Suffix);
        if (matchErr is not null)
        {
            return new CommandResult(false, matchErr);
        }

        var existing = matches[0];
        var newType = existing.Type == StripMutations.HalfStripLeft ? StripMutations.HalfStripRight : StripMutations.HalfStripLeft;
        StripMutations.UpdateStripType(engine.Strips, existing.Id, newType);
        return new CommandResult(true, $"Half-strip '{lookupKey}' slid {(newType == StripMutations.HalfStripRight ? "right" : "left")}");
    }

    // ── Separators ────────────────────────────────────────────────

    private static StripApplyResult HandleSeparatorCreate(SimulationEngine engine, SeparatorCreateCommand cmd, string? bakedStripId)
    {
        var tokens = cmd.Tokens ?? [];
        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return Verdict(new CommandResult(false, "No accessible strip bays for current position"));
        }

        // Greedy longest-prefix bay match (like STRIP), then rack + index + optional label.
        var (bay, facilityId, rack, indexOrNull, label, err) = ParseSeparatorPosition(tokens, accessible);
        if (err is not null)
        {
            return Verdict(new CommandResult(false, err));
        }

        if (bay is null || facilityId is null)
        {
            return Verdict(new CommandResult(false, "Separator position resolution failed"));
        }

        if (rack < 0 || rack >= bay.NumberOfRacks)
        {
            return Verdict(new CommandResult(false, $"Rack {rack} out of range (bay {bay.Name} has {bay.NumberOfRacks} racks)"));
        }

        // A run that already holds the recorded separator — a snapshot-based restore the record is re-applied
        // over — keeps the one it has rather than creating a second under a fresh id.
        if (bakedStripId is not null && engine.Strips.Items.ContainsKey(bakedStripId))
        {
            return new StripApplyResult(new CommandResult(true, "Separator already placed"), bakedStripId);
        }

        // Index omitted → append at the rack tail (visual top). Empty-rack
        // add-menu intent: stack a new separator above existing strips.
        var index = indexOrNull ?? CurrentRackCount(engine.Strips, bay.Id, rack);
        var stripId = bakedStripId ?? StripMutations.NewSeparatorId(engine.Strips);
        var record = new StripItemRecord(
            stripId,
            null,
            StripMutations.TypeFor(cmd.Style),
            false,
            label is null ? [] : [label],
            facilityId!,
            bay.Id,
            rack,
            index
        );

        var strips = engine.Strips;
        lock (strips.Gate)
        {
            strips.Items[stripId] = record;
        }
        StripMutations.MoveStripToBayRack(strips, stripId, bay.Id, rack, index);

        var loc = FormatResolvedSlot(bay.Name, rack, index, appended: false);
        return new StripApplyResult(
            new CommandResult(true, label is null ? $"Separator created at {loc}" : $"Separator '{label}' created at {loc}"),
            stripId
        );
    }

    private static (StripBayConfig? Bay, string? FacilityId, int Rack, int? IndexOrNull, string? Label, string? Err) ParseSeparatorPosition(
        IReadOnlyList<string> tokens,
        IReadOnlyList<AccessibleBay> accessible
    )
    {
        var resolved = StripMutations.ResolveStripDest(tokens, accessible);
        if (resolved is null)
        {
            return (null, null, 0, null, null, $"Unknown strip bay or invalid position: {string.Join(' ', tokens)}");
        }
        // Caller (HandleSeparatorCreateAsync) resolves indexOrNull to either
        // the explicit slot or the rack tail (visual top). Pulling the
        // append default into the handler keeps this static helper free of
        // FlightStripState dependencies.
        var (bay, facilityId, rack, indexOrNull, consumed) = resolved.Value;
        var trailing = tokens.Skip(consumed).ToList();
        var label = trailing.Count == 0 ? null : string.Join(' ', trailing);
        return (bay, facilityId, rack, indexOrNull, label, null);
    }

    private static CommandResult HandleSeparatorDelete(SimulationEngine engine, SeparatorDeleteCommand cmd)
    {
        var tokens = cmd.Tokens ?? [];
        if (tokens.Count == 0)
        {
            return new CommandResult(false, "SEPD requires a locator");
        }

        // Id form: a single SEP_<guid> token deletes by stripId. Symmetric
        // with the SEPE id form so the inline-edit / drag-delete UI doesn't
        // need to derive bay/rack/index for a separator it already has the
        // record for.
        if (tokens.Count == 1 && tokens[0].StartsWith("SEP_", StringComparison.Ordinal) && engine.Strips.Items.TryGetValue(tokens[0], out var byId))
        {
            if (!StripMutations.IsSeparator(byId.Type))
            {
                return new CommandResult(false, $"Strip '{tokens[0]}' is not a separator");
            }
            StripMutations.DeleteStrip(engine.Strips, tokens[0]);
            return new CommandResult(true, "Separator deleted");
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return new CommandResult(false, "No accessible strip bays for current position");
        }

        var match = FindSeparatorToDelete(engine, tokens, accessible);
        if (match.Error is not null)
        {
            return new CommandResult(false, match.Error);
        }

        StripMutations.DeleteStrip(engine.Strips, match.StripId!);
        return new CommandResult(true, "Separator deleted");
    }

    private static (string? StripId, string? Error) FindSeparatorToDelete(
        SimulationEngine engine,
        IReadOnlyList<string> tokens,
        IReadOnlyList<AccessibleBay> accessible
    )
    {
        // Wire: SEPD bay[/rack] label-or-1-based-index. Dest-spec covers the
        // leading bay/rack; everything after it is the locator (label-first,
        // 1-based index fallback for numeric-only).
        var resolved = StripMutations.ResolveStripDest(tokens, accessible);
        if (resolved is null)
        {
            return (null, $"Unknown strip bay or invalid position: {string.Join(' ', tokens)}");
        }
        var (bay, _, rack, _, consumed) = resolved.Value;
        var trailing = tokens.Skip(consumed).ToList();
        if (trailing.Count == 0)
        {
            return (null, "SEPD requires a label or 1-based position after bay[/rack]");
        }
        var locator = string.Join(' ', trailing);

        // Label match (case-insensitive) wins over position.
        foreach (var item in engine.Strips.Items.Values)
        {
            if (!StripMutations.IsSeparator(item.Type))
            {
                continue;
            }
            if (!string.Equals(item.BayId, bay.Id, StringComparison.Ordinal) || item.Rack != rack)
            {
                continue;
            }
            if (item.FieldValues.Length == 0)
            {
                continue;
            }
            if (string.Equals(item.FieldValues[0], locator, StringComparison.OrdinalIgnoreCase))
            {
                return (item.Id, null);
            }
        }

        // Position fallback: numeric locator (1-based on the wire).
        if (int.TryParse(locator, out var posWire) && posWire >= 1)
        {
            var pos = posWire - 1;
            foreach (var item in engine.Strips.Items.Values)
            {
                if (!StripMutations.IsSeparator(item.Type))
                {
                    continue;
                }
                if (string.Equals(item.BayId, bay.Id, StringComparison.Ordinal) && item.Rack == rack && item.Index == pos)
                {
                    return (item.Id, null);
                }
            }
        }

        return (null, $"No separator matching {locator}");
    }

    /// <summary>
    /// Returns the current strip count in <c>Bays[bayId][rack][0]</c> — i.e.
    /// the index at which the next "append" insert lands. 0 when the rack
    /// doesn't exist yet. Used by SEP / BLANK / HSC create paths to resolve
    /// "no index given → top of rack" without racing against concurrent
    /// inserts (the value is captured under the state gate via the caller's
    /// MoveStripToBayRack invocation, which clamps out-of-range indices).
    /// </summary>
    private static int CurrentRackCount(FlightStripState state, string bayId, int rack)
    {
        lock (state.Gate)
        {
            if (!state.Bays.TryGetValue(bayId, out var racks))
            {
                return 0;
            }
            var key = rack.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!racks.TryGetValue(key, out var rackRows) || rackRows.Length == 0)
            {
                return 0;
            }
            return rackRows[0].Count;
        }
    }

    /// <summary>
    /// Reads the strip id at <c>Bays[bayId][rack][0][index]</c>, ignoring
    /// the rack-row inner array shape (every existing rack uses a single
    /// row at index 0). Returns null when the bay/rack doesn't exist or the
    /// index is out of range — callers turn that into a user-facing error.
    /// </summary>
    private static string? ResolveStripIdAt(FlightStripState state, string bayId, int rack, int index)
    {
        lock (state.Gate)
        {
            if (!state.Bays.TryGetValue(bayId, out var racks))
            {
                return null;
            }
            var key = rack.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!racks.TryGetValue(key, out var rackRows) || rackRows.Length == 0)
            {
                return null;
            }
            var row = rackRows[0];
            if (index < 0 || index >= row.Count)
            {
                return null;
            }
            return row[index];
        }
    }

    /// <summary>
    /// SEPE bay rack locator newLabel…
    ///   locator = old label (matched case-insensitively against FieldValues[0])
    ///           OR a 1-based slot index fallback.
    /// Replaces the prior client-side delete+create pattern so the edit is a
    /// single atomic mutation under the state gate — no broadcast gap, no
    /// race with concurrent moves.
    /// </summary>
    private static CommandResult HandleSeparatorEdit(SimulationEngine engine, SeparatorEditCommand cmd)
    {
        var tokens = cmd.Tokens ?? [];
        if (tokens.Count == 0)
        {
            return new CommandResult(false, "SEPE requires a locator and a new label");
        }

        // Two locator forms: "SEPE <stripId> <newLabel>" (id-by-prefix; the
        // separator's id always starts with "SEP_") and the original
        // positional "SEPE bay/rack/index <newLabel>". The id form lets the
        // inline-edit TextBox dispatch without re-resolving rack/index — a
        // remote move between keystrokes can't desync the lookup.
        string stripId;
        string newLabel;
        if (tokens[0].StartsWith("SEP_", StringComparison.Ordinal) && engine.Strips.Items.ContainsKey(tokens[0]))
        {
            stripId = tokens[0];
            newLabel = tokens.Count > 1 ? string.Join(' ', tokens.Skip(1)) : "";
        }
        else
        {
            if (tokens.Count < 2)
            {
                return new CommandResult(false, "SEPE requires bay/rack/index and a new label");
            }

            var accessible = ResolveAccessibleBays(engine);
            if (accessible.Count == 0)
            {
                return new CommandResult(false, "No accessible strip bays for current position");
            }

            var resolved = StripMutations.ResolveStripDest(tokens, accessible);
            if (resolved is null)
            {
                return new CommandResult(false, $"Unknown strip bay or invalid position: {string.Join(' ', tokens)}");
            }
            var (bay, _, rack, indexOrNull, consumed) = resolved.Value;
            if (indexOrNull is not int index)
            {
                return new CommandResult(false, "SEPE requires an explicit slot index — use bay/rack/index");
            }
            var newLabelTokens = tokens.Skip(consumed).ToList();
            if (newLabelTokens.Count == 0)
            {
                return new CommandResult(false, "SEPE requires a new label after bay/rack/index");
            }
            newLabel = string.Join(' ', newLabelTokens);

            // Locate the separator at the given position by walking the bay
            // rack array — StripItemRecord.Index is the position the strip was
            // *created* at and goes stale the moment another strip inserts in
            // front of it. The Bays dictionary is the source of truth.
            var resolvedStripId = ResolveStripIdAt(engine.Strips, bay.Id, rack, index);
            if (resolvedStripId is null)
            {
                return new CommandResult(false, $"No separator at {bay.Name} rack {rack + 1} index {index + 1}");
            }
            stripId = resolvedStripId;
        }

        if (!engine.Strips.Items.TryGetValue(stripId, out var atSlot) || !StripMutations.IsSeparator(atSlot.Type))
        {
            return new CommandResult(false, $"No separator with id '{stripId}'");
        }

        var strips = engine.Strips;
        lock (strips.Gate)
        {
            if (!strips.Items.TryGetValue(stripId, out var existing))
            {
                return new CommandResult(false, "Separator disappeared during edit");
            }
            var fields = existing.FieldValues.Length > 0 ? [.. existing.FieldValues] : new string[1];
            fields[0] = newLabel;
            strips.Items[existing.Id] = existing with { FieldValues = fields };
            // Edited here rather than through a mutation, so the edit marks its own id.
            strips.Changes.MarkChanged(existing.Id);
        }

        if (!strips.Items.ContainsKey(stripId))
        {
            return new CommandResult(false, "Separator disappeared during edit");
        }

        return new CommandResult(true, $"Separator relabeled to '{newLabel}'");
    }

    /// <summary>
    /// SEPM: relocate a separator to a new bay/rack/index by stripId.
    /// True move (delete from current row, insert at destination) so the
    /// existing label and style stick — replaces the prior client-side
    /// "drag = delete + recreate" pattern that duplicated the separator.
    /// </summary>
    private static CommandResult HandleSeparatorMove(SimulationEngine engine, SeparatorMoveCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.StripId))
        {
            return new CommandResult(false, "SEPM requires a strip id");
        }
        if (!engine.Strips.Items.TryGetValue(cmd.StripId, out var existing))
        {
            return new CommandResult(false, $"No strip with id '{cmd.StripId}'");
        }
        if (!StripMutations.IsSeparator(existing.Type))
        {
            return new CommandResult(false, $"Strip '{cmd.StripId}' is not a separator");
        }

        var (bay, _, err) = ResolveBayByName(engine, cmd.DestFacilityId, cmd.DestBayName);
        if (err is not null)
        {
            return new CommandResult(false, err);
        }
        var bayCfg = bay!;
        if (cmd.DestRack < 0 || cmd.DestRack >= bayCfg.NumberOfRacks)
        {
            return new CommandResult(false, $"Rack {cmd.DestRack + 1} out of range (bay {bayCfg.Name} has {bayCfg.NumberOfRacks} racks)");
        }

        StripMutations.MoveStripToBayRack(engine.Strips, cmd.StripId, bayCfg.Id, cmd.DestRack, cmd.DestIndex);
        // Keep the StripItemRecord's BayId in sync so other lookups don't
        // see a stale facility mismatch. Rack/Index on the record stay
        // stale-by-design — the bay rack array remains the source of truth.
        var strips = engine.Strips;
        lock (strips.Gate)
        {
            if (strips.Items.TryGetValue(cmd.StripId, out var rec))
            {
                strips.Items[cmd.StripId] = rec with { BayId = bayCfg.Id, Rack = cmd.DestRack, Index = cmd.DestIndex };
            }
        }

        return new CommandResult(true, $"Separator moved to {FormatResolvedSlot(bayCfg.Name, cmd.DestRack, cmd.DestIndex, appended: false)}");
    }

    // ── Blanks ────────────────────────────────────────────────────

    private static StripApplyResult HandleBlankCreate(SimulationEngine engine, BlankCreateCommand cmd, string? bakedStripId)
    {
        var tokens = cmd.Tokens ?? [];

        // A run that already holds the recorded blank — a snapshot-based restore the record is re-applied over —
        // keeps the one it has. Without the guard the re-mint would draw the *next* counter value (the counter is
        // snapshotted too) and stack a second blank beside the restored one.
        if (bakedStripId is not null && engine.Strips.Items.ContainsKey(bakedStripId))
        {
            return new StripApplyResult(new CommandResult(true, "Blank strip already placed"), bakedStripId);
        }

        var stripId = bakedStripId ?? StripMutations.NewBlankId(engine.Strips);

        if (tokens.Count == 0)
        {
            var record = new StripItemRecord(stripId, null, StripMutations.BlankStripType, false, [], "", "", 0, 0);
            var printerStrips = engine.Strips;
            lock (printerStrips.Gate)
            {
                printerStrips.Items[stripId] = record;
                printerStrips.DeparturePrinterQueue.Add(stripId);
                // Written here rather than through a mutation: the item carries the payload, the full state the
                // printer-queue membership.
                printerStrips.Changes.MarkChanged(stripId);
                printerStrips.Changes.MarkFullState();
            }

            return new StripApplyResult(new CommandResult(true, "Blank strip added to printer queue"), stripId);
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return Verdict(new CommandResult(false, "No accessible strip bays for current position"));
        }

        var resolved = StripMutations.ResolveStripDest(tokens, accessible);
        if (resolved is null)
        {
            return Verdict(new CommandResult(false, $"Unknown strip bay or invalid position: {string.Join(' ', tokens)}"));
        }

        // Index omitted → append at the rack tail (visual top). Empty-rack
        // add-menu intent: stack a new blank above any existing strips.
        var (bay, facilityId, rack, indexOrNull, _) = resolved.Value;
        if (rack < 0 || rack >= bay.NumberOfRacks)
        {
            return Verdict(new CommandResult(false, $"Rack {rack + 1} out of range (bay {bay.Name} has {bay.NumberOfRacks} racks)"));
        }
        var index = indexOrNull ?? CurrentRackCount(engine.Strips, bay.Id, rack);

        var bayRecord = new StripItemRecord(stripId, null, StripMutations.BlankStripType, false, [], facilityId, bay.Id, rack, index);
        var strips = engine.Strips;
        lock (strips.Gate)
        {
            strips.Items[stripId] = bayRecord;
            // Written here rather than through a mutation, so it marks its own id; the move below marks the full state.
            strips.Changes.MarkChanged(stripId);
        }
        StripMutations.MoveStripToBayRack(strips, stripId, bay.Id, rack, index);

        return new StripApplyResult(
            new CommandResult(true, $"Blank strip created at {FormatResolvedSlot(bay.Name, rack, index, appended: false)}"),
            stripId
        );
    }

    private static CommandResult HandleBlankDelete(SimulationEngine engine, BlankDeleteCommand cmd)
    {
        var tokens = cmd.Tokens ?? [];
        if (tokens.Count == 0)
        {
            return new CommandResult(false, "BLANKD requires a bay name or strip id");
        }

        // Id form: a single BLANK_<n> token deletes that specific blank
        // strip wherever it lives — printer queue or in a bay rack. Symmetric
        // with SEPD/SEPE id forms so the printer-modal Delete button can
        // remove a blank without needing a bay locator (printer-queue blanks
        // have no bay).
        if (tokens.Count == 1 && tokens[0].StartsWith("BLANK_", StringComparison.Ordinal) && engine.Strips.Items.TryGetValue(tokens[0], out var byId))
        {
            if (byId.Type != StripMutations.BlankStripType)
            {
                return new CommandResult(false, $"Strip '{tokens[0]}' is not a blank");
            }
            StripMutations.DeleteStrip(engine.Strips, tokens[0]);
            return new CommandResult(true, "Blank strip deleted");
        }

        var accessible = ResolveAccessibleBays(engine);
        if (accessible.Count == 0)
        {
            return new CommandResult(false, "No accessible strip bays for current position");
        }

        var resolved = StripMutations.ResolveStripDest(tokens, accessible);
        if (resolved is null)
        {
            return new CommandResult(false, $"Unknown strip bay: {string.Join(' ', tokens)}");
        }

        var (bay, _, rack, indexOrNull, _) = resolved.Value;
        // BLANKD bay vs bay/rack: if the caller supplied a rack (indicated by
        // a slash in the dest-spec, which makes rack a non-zero or zero value
        // with an explicit signal), match only in that rack. The resolver
        // returns rack = 0 both for "no slash" and for explicit "/1" (→ 0),
        // so we distinguish via whether the original tokens contain a slash.
        var hasRackArg = false;
        foreach (var tok in tokens)
        {
            if (tok.IndexOf('/') >= 0)
            {
                hasRackArg = true;
                break;
            }
        }
        _ = indexOrNull;

        string? targetStripId = null;
        foreach (var item in engine.Strips.Items.Values)
        {
            if (item.Type != StripMutations.BlankStripType)
            {
                continue;
            }
            if (!string.Equals(item.BayId, bay.Id, StringComparison.Ordinal))
            {
                continue;
            }
            if (hasRackArg && item.Rack != rack)
            {
                continue;
            }

            targetStripId = item.Id;
            break;
        }

        if (targetStripId is null)
        {
            return new CommandResult(false, $"No blank strips in {bay.Name}");
        }

        StripMutations.DeleteStrip(engine.Strips, targetStripId);
        return new CommandResult(true, $"Blank strip deleted from {bay.Name}");
    }

    /// <summary>
    /// BLANKD syntax is <c>BLANKD &lt;bay&gt; [&lt;rack&gt;]</c>. The bay portion can be a
    /// multi-token name (greedy match). A rack arg is present when the token count exceeds
    /// the number of whitespace-separated words in the bay name.
    /// </summary>
    private static bool ResolveAccessibleBaysRackGiven(int tokenCount, string bayName)
    {
        var bayWordCount = bayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return tokenCount > bayWordCount;
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// The bays a command from the student's position may target — command-targetable, not the position's own display
    /// set: a strips tab opened for another accessible facility addresses that facility's bays too. Empty without a
    /// scenario, an ARTCC configuration or a student position, exactly as an unknown ARTCC id was.
    /// </summary>
    private static IReadOnlyList<AccessibleBay> ResolveAccessibleBays(SimulationEngine engine)
    {
        if (engine.Scenario is not { ArtccConfig: { } config } scenario)
        {
            return [];
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        return string.IsNullOrEmpty(positionCallsign) ? [] : config.GetAllCommandTargetableStripBays(positionCallsign);
    }

    private static (StripBayConfig? Bay, string? OwnerFacilityId, string? Error) ResolveBayByName(
        SimulationEngine engine,
        string facilityId,
        string bayName
    )
    {
        if (engine.Scenario is not { } scenario)
        {
            return (null, null, "No active scenario");
        }

        var positionCallsign = scenario.StudentPosition?.Callsign ?? "";
        if (scenario.ArtccConfig is not { } config || string.IsNullOrEmpty(positionCallsign))
        {
            return (null, null, "No active position");
        }

        var resolved = config.GetAccessibleStripBay(positionCallsign, facilityId, bayName);
        if (resolved is null)
        {
            return (null, null, $"Unknown strip bay '{facilityId}/{bayName}'");
        }

        return (resolved.Bay, resolved.Owner.Id, null);
    }

    private static (string? BayId, string Suffix, string? Error) ResolveOptionalBayScope(SimulationEngine engine, string? facilityId, string? bayName)
    {
        if (facilityId is null || bayName is null)
        {
            return (null, "", null);
        }

        var resolved = ResolveBayByName(engine, facilityId, bayName);
        if (resolved.Error is not null)
        {
            return (null, "", resolved.Error);
        }

        return (resolved.Bay!.Id, $" in {resolved.OwnerFacilityId} {resolved.Bay.Name}", null);
    }

    private static List<StripItemRecord> FindHalfStripMatches(SimulationEngine engine, string lookupKey, string? scopedBayId, int? scopedRack)
    {
        // Strip-id form: empty half-strips have no first-line text, so the
        // embedded vStrips UI emits the strip's id as the lookup key. Match
        // by Id directly, mirroring SEPD/BLANKD's id-prefix handling.
        var lookupIsStripId = lookupKey.StartsWith("HSTRIP_", StringComparison.Ordinal);

        var matches = new List<StripItemRecord>();
        foreach (var item in engine.Strips.Items.Values)
        {
            if (item.Type != StripMutations.HalfStripLeft && item.Type != StripMutations.HalfStripRight)
            {
                continue;
            }
            if (lookupIsStripId)
            {
                if (!string.Equals(item.Id, lookupKey, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else
            {
                if (item.FieldValues.Length == 0)
                {
                    continue;
                }
                if (!string.Equals(item.FieldValues[0], lookupKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            if (scopedBayId is not null && !string.Equals(item.BayId, scopedBayId, StringComparison.Ordinal))
            {
                continue;
            }
            if (scopedRack is int r && item.Rack != r)
            {
                continue;
            }

            matches.Add(item);
        }

        return matches;
    }

    private static string? SingleMatchOrError(List<StripItemRecord> matches, string lookupKey, string scopeSuffix)
    {
        if (matches.Count == 0)
        {
            return $"No half-strip matching '{lookupKey}'{scopeSuffix}";
        }
        if (matches.Count > 1)
        {
            var locations = string.Join(", ", matches.Select(m => $"{m.BayId}/{m.Rack}"));
            return $"Multiple half-strips match '{lookupKey}' — specify bay: {locations}";
        }
        return null;
    }
}
