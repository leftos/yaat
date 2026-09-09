using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// Stateless helpers shared between <see cref="StripCommandHandler"/>, the engine's auto-print hooks and the server's
/// CRC→canonical translation layer. Every method takes the per-run
/// <see cref="FlightStripState.Gate"/> lock so that multi-slice updates (Items + Bays +
/// printer queues) stay atomic even when multiple clients issue commands concurrently, and records what it touched in
/// <see cref="FlightStripState.Changes"/> — the id for a per-item push, a full-state flag for anything that reordered a
/// rack or a printer queue — which the host drains and turns into broadcasts.
/// </summary>
public static class StripMutations
{
    public const int DepartureStripType = 0;
    public const int ArrivalStripType = 1;

    /// <summary>The id prefix an arrival-format strip is keyed by; one per callsign, so an arrival print is idempotent.</summary>
    public const string ArrivalStripIdPrefix = "ARRIVAL_";

    /// <summary>The id prefix a departure-format strip is keyed by, ahead of the call sign and any duplicate-copy suffix.</summary>
    public const string DepartureStripIdPrefix = "STRIP_";
    public const int HalfStripLeft = 6;
    public const int HalfStripRight = 7;
    public const int BlankStripType = 8;

    // Departure-strip field layout, matching docs/crc/vstrips.md Departure Strip Fields.
    // FieldValues[0..9] are the derived "printed" fields (aircraft id, rev, equipment,
    // cid, beacon, prop dep, alt, dep, 8A, route+remarks). Controller annotation boxes
    // map past those via TryResolveAnnotationFieldIndex: boxes 1..9 → FieldValues[10..18],
    // box 8a → [19], box 8b → [20]. The array is built DepartureFieldCount long, so the
    // top boxes (18/19/20) are grown on demand by SetAnnotationBox.
    public const int DepartureFieldCount = 18;
    public const int FieldIdxAircraftId = 0;
    public const int FieldIdxRevision = 1;
    public const int FieldIdxEquipment = 2;
    public const int FieldIdxCid = 3;
    public const int FieldIdxBeacon = 4;
    public const int FieldIdxPropDep = 5;
    public const int FieldIdxReqAlt = 6;
    public const int FieldIdxDeparture = 7;
    public const int FieldIdx8A = 8;
    public const int FieldIdxRouteRemarks = 9;

    // Arrival-strip field layout, matching docs/crc/vstrips.md Arrival Strip Fields.
    // Shared indices [0..4] (Aircraft id, revision, equipment, CID, beacon). Then at
    // [5..9]: previous fix / coordination fix / ETA / flight rules / dest+remarks.
    // Annotation boxes live at the same FieldValues[10..18] (+ [19]/[20] for 8a/8b)
    // as departure strips, via TryResolveAnnotationFieldIndex.
    public const int FieldIdxPrevFix = 5;
    public const int FieldIdxCoordFix = 6;
    public const int FieldIdxEta = 7;
    public const int FieldIdxFlightRules = 8;
    public const int FieldIdxDestRemarks = 9;

    /// <summary>
    /// Arrivals auto-print when the aircraft is within this many minutes of its
    /// destination, matching the real vStrips behavior (see docs/crc/vstrips.md
    /// Arrival Flight Strips section).
    /// </summary>
    public const double ArrivalAutoPrintMinutes = 20.0;

    // ── Create / update / delete primitives ──────────────────────

    /// <summary>
    /// Appends an item to the given bay/rack. Wire order is oldest-first
    /// (index 0 = first-placed strip). The view renders index 0 at the visual
    /// bottom of the rack via a bottom-docking panel, so later arrivals stack
    /// upward — matching CRC bottom-up FIFO: strip #1 at bottom, strip #2
    /// above, strip #3 above that. Removes prior placement first so calling
    /// this on an already-placed strip relocates it to the tail (top).
    /// </summary>
    public static void AppendStripToBay(FlightStripState state, string bayId, int rack, string stripId)
    {
        lock (state.Gate)
        {
            RemoveFromAllBaysLocked(state, stripId);
            EnsureRack(state, bayId, rack).Add(stripId);
            state.Changes.MarkChanged(stripId);
            state.Changes.MarkFullState();
        }
    }

    /// <summary>
    /// Relocates an existing strip to the specified bay/rack/index, shifting other rows. Marks the moved id and then
    /// the full state, the same order <see cref="AppendStripToBay"/> uses: the id so every viewer that has not seen
    /// the record — the receiving facility of a push or a scan — is sent it, and the full state for the rack layout no
    /// per-item message describes. Every relocating verb goes through here, so no caller marks the move itself.
    /// </summary>
    public static bool MoveStripToBayRack(FlightStripState state, string stripId, string destBayId, int destRack, int destIndex)
    {
        lock (state.Gate)
        {
            if (!state.Items.ContainsKey(stripId))
            {
                return false;
            }

            RemoveFromAllBaysLocked(state, stripId);
            RemoveFromPrinterQueuesLocked(state, stripId);

            var row = EnsureRack(state, destBayId, destRack);
            var clamped = destIndex < 0 ? 0 : (destIndex > row.Count ? row.Count : destIndex);
            row.Insert(clamped, stripId);
            state.Changes.MarkChanged(stripId);
            state.Changes.MarkFullState();
            return true;
        }
    }

    /// <summary>
    /// Whether a strip type is one of the four separator styles (handwritten, white, red, green — see
    /// <c>StripItemType</c>). Separators carry no aircraft and are the controller's own workspace lines,
    /// so several paths (SEPD/SEPE/SEPM targeting, the restart carry-over) need to tell them apart.
    /// </summary>
    public static bool IsSeparator(int type) => type is 2 or 3 or 4 or 5;

    /// <summary>The strip type a <c>SEP</c> style creates.</summary>
    public static int TypeFor(SeparatorStyle style) =>
        (int)(
            style switch
            {
                SeparatorStyle.Handwritten => StripItemType.HandwrittenSeparator,
                SeparatorStyle.White => StripItemType.WhiteSeparator,
                SeparatorStyle.Red => StripItemType.RedSeparator,
                SeparatorStyle.Green => StripItemType.GreenSeparator,
                _ => StripItemType.HandwrittenSeparator,
            }
        );

    /// <summary>
    /// The inverse: the <c>SEP</c> style behind a separator's strip type, for a caller rebuilding the command that
    /// would create it. Throws on any other type — a caller that has not filtered on <see cref="IsSeparator"/> is
    /// asking about a strip that no <c>SEP</c> ever made, and answering "handwritten" would silently turn it into one.
    /// </summary>
    public static SeparatorStyle StyleOf(int type) =>
        (StripItemType)type switch
        {
            StripItemType.HandwrittenSeparator => SeparatorStyle.Handwritten,
            StripItemType.WhiteSeparator => SeparatorStyle.White,
            StripItemType.RedSeparator => SeparatorStyle.Red,
            StripItemType.GreenSeparator => SeparatorStyle.Green,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a separator strip type"),
        };

    /// <summary>Removes a strip from Items, every bay rack, and both printer queues.</summary>
    public static bool DeleteStrip(FlightStripState state, string stripId)
    {
        lock (state.Gate)
        {
            if (!state.Items.TryRemove(stripId, out _))
            {
                return false;
            }

            RemoveFromAllBaysLocked(state, stripId);
            RemoveFromPrinterQueuesLocked(state, stripId);
            state.Changes.MarkFullState();
            return true;
        }
    }

    /// <summary>Toggles the offset flag on an existing strip; returns the new value or null if missing.</summary>
    public static bool? ToggleOffset(FlightStripState state, string stripId)
    {
        lock (state.Gate)
        {
            if (!state.Items.TryGetValue(stripId, out var existing))
            {
                return null;
            }

            var updated = existing with { IsOffset = !existing.IsOffset };
            state.Items[stripId] = updated;
            state.Changes.MarkChanged(stripId);
            return updated.IsOffset;
        }
    }

    /// <summary>Replaces the type (used by HSS slide to toggle HalfStripLeft ↔ HalfStripRight).</summary>
    public static StripItemRecord? UpdateStripType(FlightStripState state, string stripId, int newType)
    {
        lock (state.Gate)
        {
            if (!state.Items.TryGetValue(stripId, out var existing))
            {
                return null;
            }

            var updated = existing with { Type = newType };
            state.Items[stripId] = updated;
            state.Changes.MarkChanged(stripId);
            return updated;
        }
    }

    /// <summary>Replaces <see cref="StripItemRecord.FieldValues"/> wholesale.</summary>
    public static StripItemRecord? UpdateStripFields(FlightStripState state, string stripId, string[] newFields)
    {
        lock (state.Gate)
        {
            if (!state.Items.TryGetValue(stripId, out var existing))
            {
                return null;
            }

            var updated = existing with { FieldValues = newFields };
            state.Items[stripId] = updated;
            state.Changes.MarkChanged(stripId);
            return updated;
        }
    }

    /// <summary>
    /// Sets a single annotation slot, growing the FieldValues array as needed.
    /// <paramref name="box"/> is the canonical slot id produced by
    /// <c>CommandParser.ParseStripAnnotate</c>:
    /// <list type="bullet">
    /// <item><c>"1"</c>..<c>"9"</c> → FieldValues[10..18] (3×3 grid).</item>
    /// <item><c>"8a"</c> → FieldValues[19], <c>"8b"</c> → FieldValues[20]
    ///   (col-3 freeform slots below field 8).</item>
    /// </list>
    /// Returns <c>null</c> for unknown box ids or missing strip ids.
    /// </summary>
    public static StripItemRecord? SetAnnotationBox(FlightStripState state, string stripId, string box, string? text)
    {
        if (!TryResolveAnnotationFieldIndex(box, out var fieldIndex))
        {
            return null;
        }

        lock (state.Gate)
        {
            if (!state.Items.TryGetValue(stripId, out var existing))
            {
                return null;
            }

            var fields = existing.FieldValues;
            if (fields.Length <= fieldIndex)
            {
                var grown = new string[fieldIndex + 1];
                Array.Copy(fields, grown, fields.Length);
                for (var i = fields.Length; i < grown.Length; i++)
                {
                    grown[i] = "";
                }
                fields = grown;
            }
            else
            {
                fields = (string[])fields.Clone();
            }

            fields[fieldIndex] = text ?? "";
            var updated = existing with { FieldValues = fields };
            state.Items[stripId] = updated;
            state.Changes.MarkChanged(stripId);
            return updated;
        }
    }

    /// <summary>
    /// Maps a canonical annotation box id to its FieldValues index. Accepts
    /// the parser's canonical outputs: <c>"1"</c>..<c>"9"</c>, <c>"8a"</c>,
    /// <c>"8b"</c>. Returns <c>false</c> for any other token.
    /// </summary>
    public static bool TryResolveAnnotationFieldIndex(string box, out int fieldIndex)
    {
        switch (box)
        {
            case "8a":
                fieldIndex = 19;
                return true;
            case "8b":
                fieldIndex = 20;
                return true;
        }
        if (int.TryParse(box, out var n) && n is >= 1 and <= 9)
        {
            fieldIndex = n + 9;
            return true;
        }
        fieldIndex = 0;
        return false;
    }

    // ── Printer queue operations ─────────────────────────────────

    public static void EnqueueArrivalPrinter(FlightStripState state, string stripId)
    {
        lock (state.Gate)
        {
            RemoveFromPrinterQueuesLocked(state, stripId);
            state.ArrivalPrinterQueue.Add(stripId);
            state.Changes.MarkFullState();
        }
    }

    // ── Auto-departure strip synthesis ───────────────────────────

    /// <summary>
    /// Builds the <see cref="StripItemRecord.FieldValues"/> array for a departure strip per
    /// docs/crc/vstrips.md Departure Strip Fields. Annotation boxes 10-18 are left empty for
    /// subsequent <c>AN</c> updates.
    /// </summary>
    public static string[] BuildDepartureStripFields(AircraftState ac, SimScenarioState scenario, bool displayDestinationAirportIds = false)
    {
        var fields = new string[DepartureFieldCount];
        for (var i = 0; i < DepartureFieldCount; i++)
        {
            fields[i] = "";
        }

        fields[FieldIdxAircraftId] = ac.Callsign ?? "";
        fields[FieldIdxRevision] =
            ac.FlightPlan.RevisionNumber > 0 ? ac.FlightPlan.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        fields[FieldIdxEquipment] = FormatEquipment(ac);
        fields[FieldIdxCid] = ac.Cid ?? "";
        fields[FieldIdxBeacon] = FormatBeacon(ac.Transponder.AssignedCode);
        fields[FieldIdxPropDep] = FormatProposedDepartureTime(scenario);
        fields[FieldIdxReqAlt] = FormatRequestedAltitude(ac.FlightPlan.Altitude);
        fields[FieldIdxDeparture] = ac.FlightPlan.Departure ?? "";
        // Field 8 (col 3 row 1) shows the departure airport, optionally with
        // the destination appended ("KOAK KSTS") when the owning facility sets
        // displayDestinationAirportIds=true in its flightStripConfiguration.
        // Tower ATCTs typically enable this; Centers leave it off.
        var dep = ac.FlightPlan.Departure ?? "";
        var dest = ac.FlightPlan.Destination ?? "";
        fields[FieldIdx8A] = !displayDestinationAirportIds || string.IsNullOrEmpty(dest) ? dep : (string.IsNullOrEmpty(dep) ? dest : $"{dep} {dest}");
        fields[FieldIdxRouteRemarks] = FormatRouteField(ac);
        return fields;
    }

    /// <summary>
    /// Aircraft-scoped departure-strip creator. Idempotent: if a departure strip already
    /// exists for this callsign, returns the existing record without printing again.
    /// Otherwise creates a new strip, adds it to <see cref="FlightStripState.Items"/>, and
    /// enqueues it on the departure printer queue.
    /// </summary>
    public static StripItemRecord RequestDepartureStripForAircraft(
        FlightStripState state,
        AircraftState ac,
        SimScenarioState scenario,
        string facilityId,
        bool displayDestinationAirportIds = false
    )
    {
        lock (state.Gate)
        {
            var stripId = $"{DepartureStripIdPrefix}{ac.Callsign}";
            if (state.Items.TryGetValue(stripId, out var existing))
            {
                MarkPrinted(state, stripId);
                return existing;
            }

            var fields = BuildDepartureStripFields(ac, scenario, displayDestinationAirportIds);
            var record = new StripItemRecord(stripId, ac.Callsign, DepartureStripType, false, fields, facilityId, "", 0, 0);

            state.Items[stripId] = record;
            state.DeparturePrinterQueue.Add(stripId);
            MarkPrinted(state, stripId);
            return record;
        }
    }

    /// <summary>
    /// User-initiated (re)print of a departure strip under an id minted by
    /// <see cref="MintStripId"/>. Unlike the idempotent
    /// <see cref="RequestDepartureStripForAircraft"/> (used by the automatic print
    /// triggers), this ALWAYS prints a fresh strip, so multiple strips for one aircraft
    /// stack in the printer. Matches real vStrips, where clicking "Request Strip" stacks
    /// another copy (docs/crc/vstrips.md). Returns null for an empty callsign.
    /// </summary>
    public static StripItemRecord? PrintDepartureStripForAircraft(
        FlightStripState state,
        AircraftState ac,
        SimScenarioState scenario,
        string facilityId,
        bool displayDestinationAirportIds,
        string stripId
    )
    {
        if (string.IsNullOrEmpty(ac.Callsign))
        {
            return null;
        }

        lock (state.Gate)
        {
            var fields = BuildDepartureStripFields(ac, scenario, displayDestinationAirportIds);
            var record = new StripItemRecord(stripId, ac.Callsign, DepartureStripType, false, fields, facilityId, "", 0, 0);

            state.Items[stripId] = record;
            state.DeparturePrinterQueue.Add(stripId);
            MarkPrinted(state, stripId);
            return record;
        }
    }

    /// <summary>
    /// The id a user-initiated "Request Strip" prints under, drawn once at the live request so the
    /// <see cref="Yaat.Sim.Simulation.RecordedStripRequest"/> can bake it and a reconstruction reuses it instead of
    /// minting a second copy. An arrival is the fixed <c>ARRIVAL_{callsign}</c> (its print is idempotent); a departure
    /// is the canonical <c>STRIP_{callsign}</c> when free, otherwise a distinct duplicate id.
    /// </summary>
    public static string MintStripId(FlightStripState state, string callsign, bool isArrival)
    {
        if (isArrival)
        {
            return $"{ArrivalStripIdPrefix}{callsign}";
        }

        lock (state.Gate)
        {
            return NewDepartureStripId(state, callsign);
        }
    }

    /// <summary>
    /// Returns the canonical <c>STRIP_{callsign}</c> id when it's free, otherwise a
    /// distinct <c>STRIP_{callsign}_{hex8}</c> duplicate id (re-rolling on the rare
    /// collision, falling back to a full GUID). Keeping the <c>STRIP_</c> prefix lets
    /// vStrips render the copy as a normal full strip and lets the id-forms of
    /// <c>STRIPD</c>/<c>STRIPO</c>/<c>AN</c> address it. Callers already hold the gate.
    /// </summary>
    private static string NewDepartureStripId(FlightStripState state, string callsign)
    {
        var canonical = $"{DepartureStripIdPrefix}{callsign}";
        if (!state.Items.ContainsKey(canonical))
        {
            return canonical;
        }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = $"{DepartureStripIdPrefix}{callsign}_{Guid.NewGuid().ToString("N")[..8]}";
            if (!state.Items.ContainsKey(candidate))
            {
                return candidate;
            }
        }
        return $"{DepartureStripIdPrefix}{callsign}_{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Removes the aircraft's outdated departure strips that are still sitting in the
    /// PRINTER queue (both the id and the record), leaving copies already moved into a
    /// bay rack untouched. Called on a flight-plan amendment before printing the new
    /// revised strip — matching vStrips (docs/crc/vstrips.md:73-75): a revised strip
    /// clears prior printer copies, but bay copies must be removed by a controller.
    /// </summary>
    public static void RemoveOutdatedDeparturePrinterStrips(FlightStripState state, string callsign)
    {
        lock (state.Gate)
        {
            var stale = state
                .DeparturePrinterQueue.Where(id =>
                    state.Items.TryGetValue(id, out var r)
                    && r.Type == DepartureStripType
                    && string.Equals(r.AircraftId, callsign, StringComparison.Ordinal)
                )
                .ToList();
            foreach (var id in stale)
            {
                DeleteStrip(state, id);
            }
        }
    }

    /// <summary>
    /// Aircraft-scoped departure-strip creator that places the strip directly into a bay
    /// rack (rather than the printer queue). Idempotent: returns the existing record if
    /// one already exists for this callsign. Used by auto-routing flows (tower student →
    /// Ground bay; approach student → matching facility bay on takeoff roll) to skip
    /// the printer entirely.
    /// </summary>
    public static StripItemRecord? RequestDepartureStripForAircraftIntoBay(
        FlightStripState state,
        AircraftState ac,
        SimScenarioState scenario,
        string facilityId,
        string bayId,
        int rack,
        bool displayDestinationAirportIds = false
    )
    {
        if (string.IsNullOrEmpty(ac.Callsign))
        {
            return null;
        }

        lock (state.Gate)
        {
            var stripId = $"{DepartureStripIdPrefix}{ac.Callsign}";
            if (state.Items.TryGetValue(stripId, out var existing))
            {
                MarkPrinted(state, stripId);
                return existing;
            }

            var fields = BuildDepartureStripFields(ac, scenario, displayDestinationAirportIds);
            var row = EnsureRack(state, bayId, rack);
            var index = row.Count;
            var record = new StripItemRecord(stripId, ac.Callsign, DepartureStripType, false, fields, facilityId, bayId, rack, index);

            state.Items[stripId] = record;
            row.Add(stripId);
            MarkPrinted(state, stripId);
            return record;
        }
    }

    /// <summary>
    /// Builds the <see cref="StripItemRecord.FieldValues"/> array for an arrival strip per
    /// docs/crc/vstrips.md Arrival Strip Fields. ETA is the session clock at print time plus the
    /// supplied minutes-to-arrival, formatted <c>HHmm</c>.
    /// </summary>
    public static string[] BuildArrivalStripFields(AircraftState ac, SimScenarioState scenario, double etaMinutes)
    {
        var fields = new string[DepartureFieldCount];
        for (var i = 0; i < DepartureFieldCount; i++)
        {
            fields[i] = "";
        }

        fields[FieldIdxAircraftId] = ac.Callsign ?? "";
        fields[FieldIdxRevision] =
            ac.FlightPlan.RevisionNumber > 0 ? ac.FlightPlan.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        fields[FieldIdxEquipment] = FormatEquipment(ac);
        fields[FieldIdxCid] = ac.Cid ?? "";
        fields[FieldIdxBeacon] = FormatBeacon(ac.Transponder.AssignedCode);
        // Previous fix and coordination fix are controller artifacts — left empty for v1.
        fields[FieldIdxEta] = FormatEta(scenario, etaMinutes);
        fields[FieldIdxFlightRules] = ac.FlightPlan.FlightRules ?? "";
        fields[FieldIdxDestRemarks] = FormatDestRemarks(ac);
        return fields;
    }

    /// <summary>
    /// Aircraft-scoped arrival-strip creator. Idempotent — if an arrival strip (or
    /// departure strip, for an aircraft that was originally a departure but is now returning)
    /// already exists for this callsign, returns the existing record. Prints via the arrival
    /// printer queue rather than the departure queue. <paramref name="stripId"/> comes from
    /// <see cref="MintStripId"/> so a recorded request prints under the id the live run used.
    /// </summary>
    public static StripItemRecord? RequestArrivalStripForAircraft(
        FlightStripState state,
        AircraftState ac,
        SimScenarioState scenario,
        double etaMinutes,
        string facilityId,
        string stripId
    )
    {
        if (string.IsNullOrEmpty(ac.Callsign))
        {
            return null;
        }

        lock (state.Gate)
        {
            if (state.Items.TryGetValue(stripId, out var existing))
            {
                MarkPrinted(state, stripId);
                return existing;
            }

            var fields = BuildArrivalStripFields(ac, scenario, etaMinutes);
            var record = new StripItemRecord(stripId, ac.Callsign, ArrivalStripType, false, fields, facilityId, "", 0, 0);

            state.Items[stripId] = record;
            state.ArrivalPrinterQueue.Add(stripId);
            MarkPrinted(state, stripId);
            return record;
        }
    }

    // ── Half-strip + blank id generation ─────────────────────────

    /// <summary>
    /// Mints a fresh half-strip id with an 8-char hex suffix (e.g. <c>HSTRIP_aece26a3</c>).
    /// 8 hex chars = 2^32 distinct values per room — at hundreds of strips per
    /// session the birthday-collision probability is ~1e-6, but the loop below
    /// re-rolls on the rare collision against an existing id so the result is
    /// always unique within the room. Older 32-char ids in legacy recordings
    /// keep working: parser/handler match by prefix + exact id, not length.
    /// </summary>
    public static string NewHalfStripId(FlightStripState? state = null) => MintShortId("HSTRIP_", state);

    /// <summary>
    /// The next blank id off the room's counter. The counter is snapshotted, so a run restored from a snapshot
    /// resumes numbering where the captured one left off — which is why the id a <c>BLANK</c> drew is baked onto its
    /// record like the guid-minted ones: re-minting over a restore would hand out the next number and stack a second
    /// blank beside the restored one.
    /// </summary>
    public static string NewBlankId(FlightStripState state)
    {
        lock (state.Gate)
        {
            var id = state.NextBlankId++;
            return $"BLANK_{id}";
        }
    }

    /// <summary>Same scheme as <see cref="NewHalfStripId"/> but with a <c>SEP_</c> prefix.</summary>
    public static string NewSeparatorId(FlightStripState? state = null) => MintShortId("SEP_", state);

    /// <summary>
    /// Same scheme again for the copy a <c>SCAN</c> puts in an external bay: <c>STRIP_{callsign}_{suffix}</c>. The
    /// <c>STRIP_</c> prefix keeps vStrips' full-strip rendering path; the suffix is what differentiates copies and
    /// keeps them off the canonical <c>STRIP_{callsign}</c> record.
    /// </summary>
    public static string NewScanStripId(string callsign, FlightStripState state) => MintShortId($"STRIP_{callsign}_", state);

    private static string MintShortId(string prefix, FlightStripState? state)
    {
        // 6 attempts is more than enough — at the practical strip count per
        // room, even one collision is an outlier; six chained collisions are
        // essentially impossible. If it ever happens we fall back to the
        // full GUID so we never block the caller.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = prefix + Guid.NewGuid().ToString("N")[..8];
            if (state is null || !state.Items.ContainsKey(candidate))
            {
                return candidate;
            }
        }
        return prefix + Guid.NewGuid().ToString("N");
    }

    // ── STRIP tokens → (bay, rack, index) resolver ───────────────

    /// <summary>
    /// Greedy longest-prefix bay match for the extended STRIP verb. Walks the accessible
    /// bay list trying to match the longest concatenation of leading tokens against a bay
    /// name (whitespace-insensitive, case-insensitive). Remaining tokens are parsed as
    /// rack / index (up to 2).
    ///
    /// <para>The canonical wire format uses <b>1-based</b> rack/index tokens — users
    /// think in "rack 1" and "slot 1", not "rack 0" / "slot 0". This method converts
    /// to the 0-based internal representation (rack 1 → 0, index 1 → 0) and rejects
    /// any token &lt; 1. The <c>Index</c> return is <c>null</c> when the caller omitted
    /// the index token entirely — <see cref="HandleStripMoveAsync"/> treats that as
    /// "append to the tail of the rack" (CRC bottom-up first-available semantics),
    /// while other callers coerce null to 0.</para>
    ///
    /// Returns null on ambiguity, no match, or sub-1 rack/index tokens.
    /// </summary>
    public static (StripBayConfig Bay, string FacilityId, int Rack, int? Index)? ResolveStripTokens(
        IReadOnlyList<string> tokens,
        IReadOnlyList<AccessibleBay> accessibleBays
    )
    {
        if (tokens.Count == 0 || accessibleBays.Count == 0)
        {
            return null;
        }

        AccessibleBay? bestMatch = null;
        var bestTokenCount = 0;

        for (var len = Math.Min(tokens.Count, 6); len >= 1; len--)
        {
            var candidate = string.Join(' ', tokens.Take(len));
            var normalized = candidate.Replace(" ", "", StringComparison.Ordinal);

            foreach (var entry in accessibleBays)
            {
                var bayNameNorm = entry.Bay.Name.Replace(" ", "", StringComparison.Ordinal);
                if (bayNameNorm.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (len > bestTokenCount)
                    {
                        bestMatch = entry;
                        bestTokenCount = len;
                    }
                    break;
                }
            }

            if (bestMatch is not null)
            {
                break;
            }
        }

        if (bestMatch is null)
        {
            return null;
        }

        var remaining = tokens.Count - bestTokenCount;
        var rack = 0;
        int? index = null;

        if (remaining >= 1)
        {
            // 1-based rack on the wire → 0-based internal. "Rack 0" is not a
            // thing users can type; reject it so off-by-one bugs surface loudly.
            if (!int.TryParse(tokens[bestTokenCount], out var rackOneBased) || rackOneBased < 1)
            {
                return null;
            }
            rack = rackOneBased - 1;
        }

        if (remaining >= 2)
        {
            // 1-based index on the wire → 0-based internal. Omitting the token
            // entirely keeps Index null (→ HandleStripMoveAsync appends).
            if (!int.TryParse(tokens[bestTokenCount + 1], out var indexOneBased) || indexOneBased < 1)
            {
                return null;
            }
            index = indexOneBased - 1;
        }

        if (remaining > 2)
        {
            return null;
        }

        return (bestMatch.Bay, bestMatch.Owner.Id, rack, index);
    }

    /// <summary>
    /// Resolves a slash-compound vStrips destination spec from a token sequence
    /// against the set of accessible bays. The dest-spec is
    /// <c>bay[/rack[/index]]</c> on the wire with <b>1-based</b> rack and index;
    /// this method returns the 0-based internal values. Bay names may contain
    /// spaces — anything before the first <c>/</c> (across all provided tokens,
    /// joined with spaces) is taken as the bay name; whitespace is stripped
    /// before comparing against accessible bay names case-insensitively.
    ///
    /// <para><c>TokensConsumed</c> is the number of leading input tokens the
    /// dest-spec ate, so callers that have trailing arguments (SEP label,
    /// SEPE new-label, SEPD locator) can slice them off via
    /// <c>tokens.Skip(result.Value.TokensConsumed)</c>.</para>
    ///
    /// Returns <c>null</c> on an unknown bay, missing rack when required,
    /// malformed rack/index, or sub-1 rack/index tokens.
    /// </summary>
    public static (StripBayConfig Bay, string FacilityId, int Rack, int? Index, int TokensConsumed)? ResolveStripDest(
        IReadOnlyList<string> tokens,
        IReadOnlyList<AccessibleBay> accessibleBays
    )
    {
        if (tokens.Count == 0 || accessibleBays.Count == 0)
        {
            return null;
        }

        // Peel the required leading FACILITY/ qualifier off token 0. It has to come
        // off the raw string rather than the token list because a bay name may
        // contain spaces: "O90/BAY Sutro/1/1" tokenizes as ["O90/BAY", "Sutro/1/1"],
        // so the qualifier and the first word of the bay share a token.
        var qualifierSlash = tokens[0].IndexOf('/');
        if (qualifierSlash <= 0)
        {
            return null;
        }
        var facilityId = tokens[0][..qualifierSlash];
        var facilityBays = accessibleBays.Where(b => b.Owner.Id.Equals(facilityId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (facilityBays.Count == 0)
        {
            return null;
        }

        var remainderHead = tokens[0][(qualifierSlash + 1)..];
        var droppedLeadingToken = remainderHead.Length == 0;
        var inner = droppedLeadingToken ? tokens.Skip(1).ToList() : [remainderHead, .. tokens.Skip(1)];
        var resolved = ResolveBayWithinFacility(inner, facilityBays);
        if (resolved is null)
        {
            return null;
        }

        var consumed = resolved.Value.TokensConsumed + (droppedLeadingToken ? 1 : 0);
        return (resolved.Value.Bay.Bay, resolved.Value.Bay.Owner.Id, resolved.Value.Rack, resolved.Value.Index, consumed);
    }

    /// <summary>
    /// The unqualified half of <see cref="ResolveStripDest"/>: matches
    /// <c>bay[/rack[/index]]</c> across <paramref name="tokens"/> against a single
    /// facility's bays. Anything before the first <c>/</c> (across all tokens,
    /// joined with spaces) is the bay name; whitespace is stripped before the
    /// case-insensitive comparison.
    /// </summary>
    private static (AccessibleBay Bay, int Rack, int? Index, int TokensConsumed)? ResolveBayWithinFacility(
        IReadOnlyList<string> tokens,
        IReadOnlyList<AccessibleBay> accessibleBays
    )
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        // Find the first token containing '/'. Tokens before it (plus any part
        // of that token before the slash) form the bay name; the slash tail
        // goes on to parse as rack/index.
        var slashTokIdx = -1;
        var slashCharIdx = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            var idx = tokens[i].IndexOf('/');
            if (idx >= 0)
            {
                slashTokIdx = i;
                slashCharIdx = idx;
                break;
            }
        }

        string bayPart;
        string tail;
        int tokensConsumed;
        if (slashTokIdx < 0)
        {
            bayPart = string.Join(' ', tokens);
            tail = "";
            tokensConsumed = tokens.Count;
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < slashTokIdx; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(tokens[i]);
            }
            if (slashCharIdx > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(tokens[slashTokIdx], 0, slashCharIdx);
            }
            bayPart = sb.ToString();
            tail = tokens[slashTokIdx][slashCharIdx..];
            tokensConsumed = slashTokIdx + 1;
        }

        if (bayPart.Length == 0)
        {
            return null;
        }

        var bayNorm = bayPart.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        AccessibleBay? match = null;
        foreach (var entry in accessibleBays)
        {
            var entryNorm = entry.Bay.Name.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            if (entryNorm == bayNorm)
            {
                match = entry;
                break;
            }
        }
        if (match is null)
        {
            return null;
        }

        var rack = 0;
        int? index = null;
        if (tail.Length > 0)
        {
            var parts = tail.Split('/');
            // tail starts with "/", so parts[0] is empty; parts[1] = rack, parts[2] = index.
            if (parts.Length is < 2 or > 3)
            {
                return null;
            }
            if (!int.TryParse(parts[1], out var rWire) || rWire < 1)
            {
                return null;
            }
            rack = rWire - 1;
            if (parts.Length >= 3)
            {
                if (!int.TryParse(parts[2], out var iWire) || iWire < 1)
                {
                    return null;
                }
                index = iWire - 1;
            }
        }

        return (match, rack, index, tokensConsumed);
    }

    // ── Field formatters (exposed for tests) ─────────────────────

    /// <summary>
    /// Strip-field equipment format: "{CWT}/{baseType}/{suffix}", e.g.
    /// "C/B763/L" for a 767 with CWT C, DME/TCAS suffix. Drops the CWT prefix
    /// when unknown and drops the "/{suffix}" tail when the aircraft has no
    /// equipment suffix. Uses <see cref="AircraftFlightPlan.BaseAircraftType"/>
    /// (the *filed* type) — strips are flight-plan-driven, so blanking the type
    /// in the FP editor blanks the equipment column too.
    /// </summary>
    internal static string FormatEquipment(AircraftState ac)
    {
        var baseType = ac.FlightPlan.BaseAircraftType;
        var suffix = ac.FlightPlan.EquipmentSuffix ?? "";
        var cwt = WakeTurbulenceData.GetCwt(baseType);
        var withSuffix = string.IsNullOrEmpty(suffix) ? baseType : $"{baseType}/{suffix}";
        return string.IsNullOrEmpty(cwt) ? withSuffix : $"{cwt}/{withSuffix}";
    }

    internal static string FormatBeacon(uint beacon)
    {
        if (beacon == 0)
        {
            return "";
        }
        // SimulationWorld.GenerateBeaconCode already stores the code as a decimal
        // integer whose digits are each in 0..7 (e.g. 3447). The strip label is the
        // digit pattern as assigned, zero-padded to 4 chars — NOT a base conversion
        // to octal (which would turn 3447 into 6567 and mislabel every strip).
        return beacon.ToString("D4");
    }

    /// <summary>
    /// Formats the session clock at print time (<see cref="SimScenarioState.SimTimeUtc"/>) as <c>HHmm</c>
    /// for the Proposed Departure Time field. The clock is anchored at the session start and advances with
    /// the scenario, so the same strip reprints the same text on a rewind or a reconstruction.
    /// </summary>
    internal static string FormatProposedDepartureTime(SimScenarioState scenario)
    {
        return scenario.SimTimeUtc.ToString("HHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a filed cruise altitude for the strip's requested-altitude box and any
    /// other CRC field that follows the same convention. Delegates to the shared
    /// <see cref="FlightPlanAltitude.Format"/> so IFR/VFR/OTP all use one rendering.
    /// </summary>
    public static string FormatRequestedAltitude(PlannedAltitude altitude)
    {
        return FlightPlanAltitude.Format(altitude);
    }

    /// <summary>
    /// Formats the arrival's ETA as <c>HHmm</c>: the session clock at print time
    /// (<see cref="SimScenarioState.SimTimeUtc"/>) plus <paramref name="minutesUntilArrival"/>.
    /// </summary>
    internal static string FormatEta(SimScenarioState scenario, double minutesUntilArrival)
    {
        if (minutesUntilArrival < 0)
        {
            minutesUntilArrival = 0;
        }
        var eta = scenario.SimTimeUtc.AddMinutes(minutesUntilArrival);
        return eta.ToString("HHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Packs destination + remarks into arrival-strip field 9 with a <c>\n</c>
    /// separator when both are present. Client splits on the separator so row 3
    /// can be reserved for remarks (per CRC reference docs/crc/img/arrival-strip.png
    /// where the last row shows "KBOS ○NEW PILOT" = destination + remark token).
    /// </summary>
    internal static string FormatDestRemarks(AircraftState ac)
    {
        var head = ac.FlightPlan.Destination ?? "";
        var remarks = ac.FlightPlan.Remarks ?? "";
        if (string.IsNullOrEmpty(remarks))
        {
            return head;
        }
        if (string.IsNullOrEmpty(head))
        {
            return "\n" + remarks;
        }
        return head + "\n" + remarks;
    }

    /// <summary>
    /// Packs departure + route + destination + remarks into departure-strip
    /// field 9 with a <c>\n</c> between the route/dest head and the remarks
    /// tail. The route text starts with the departure airport and ends with
    /// the destination airport per CRC convention (docs/crc/img/departure-strip.png,
    /// e.g. "KBOS CELTK6 CELTK FRILL TUSKY N261A JOOPY***EGLL"). Departure
    /// and destination are only prepended/appended when <see cref="AircraftState.Route"/>
    /// doesn't already start/end with them, so scenarios that ship the route
    /// already wrapped with the airports don't get doubled labels.
    /// The client uses the newline separator to keep remarks on row 3 and wrap
    /// the route across rows 1-2 (or across all 3 rows when no remarks are
    /// present).
    /// </summary>
    internal static string FormatRouteField(AircraftState ac)
    {
        var route = (ac.FlightPlan.Route ?? "").Trim();
        var departure = (ac.FlightPlan.Departure ?? "").Trim();
        var destination = (ac.FlightPlan.Destination ?? "").Trim();

        var head = route;
        if (!string.IsNullOrEmpty(departure) && !StartsWithToken(head, departure))
        {
            head = string.IsNullOrEmpty(head) ? departure : departure + " " + head;
        }
        if (!string.IsNullOrEmpty(destination) && !EndsWithToken(head, destination))
        {
            head = string.IsNullOrEmpty(head) ? destination : head + " " + destination;
        }

        var remarks = ac.FlightPlan.Remarks ?? "";
        if (string.IsNullOrEmpty(remarks))
        {
            return head;
        }
        if (string.IsNullOrEmpty(head))
        {
            return "\n" + remarks;
        }
        return head + "\n" + remarks;
    }

    /// <summary>
    /// True when <paramref name="text"/> begins with <paramref name="token"/>
    /// followed by whitespace or end-of-string — avoids mistaking "KBOSTON" for
    /// a prepended "KBOS" departure airport.
    /// </summary>
    private static bool StartsWithToken(string text, string token)
    {
        if (!text.StartsWith(token, StringComparison.Ordinal))
        {
            return false;
        }
        return text.Length == token.Length || char.IsWhiteSpace(text[token.Length]);
    }

    /// <summary>
    /// True when <paramref name="text"/> ends with <paramref name="token"/>
    /// preceded by whitespace or start-of-string — mirror of
    /// <see cref="StartsWithToken"/> for the destination airport.
    /// </summary>
    private static bool EndsWithToken(string text, string token)
    {
        if (!text.EndsWith(token, StringComparison.Ordinal))
        {
            return false;
        }
        var before = text.Length - token.Length;
        return before == 0 || char.IsWhiteSpace(text[before - 1]);
    }

    // ── Private helpers (callers already hold the gate) ─────────

    /// <summary>
    /// A print marks both: the item, so a receiver has the payload, and the full state, so it learns which printer
    /// queue or rack the id now sits in — the items-then-full-state order every strip broadcast uses. Marked for a
    /// repeat request that found the strip already there too, since that is what the callers push.
    /// </summary>
    private static void MarkPrinted(FlightStripState state, string stripId)
    {
        state.Changes.MarkChanged(stripId);
        state.Changes.MarkFullState();
    }

    private static List<string> EnsureRack(FlightStripState state, string bayId, int rack)
    {
        if (!state.Bays.TryGetValue(bayId, out var racks))
        {
            racks = new Dictionary<string, List<string>[]>();
            state.Bays[bayId] = racks;
        }

        var key = rack.ToString();
        if (!racks.TryGetValue(key, out var rackRows))
        {
            rackRows = [new List<string>()];
            racks[key] = rackRows;
        }

        if (rackRows.Length == 0)
        {
            racks[key] = [new List<string>()];
            rackRows = racks[key];
        }

        return rackRows[0];
    }

    private static void RemoveFromAllBaysLocked(FlightStripState state, string stripId)
    {
        foreach (var racks in state.Bays.Values)
        {
            foreach (var rackRows in racks.Values)
            {
                foreach (var row in rackRows)
                {
                    row.Remove(stripId);
                }
            }
        }
    }

    private static void RemoveFromPrinterQueuesLocked(FlightStripState state, string stripId)
    {
        state.DeparturePrinterQueue.Remove(stripId);
        state.ArrivalPrinterQueue.Remove(stripId);
    }
}
