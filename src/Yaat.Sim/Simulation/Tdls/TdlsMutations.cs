namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// Stateless mutation helpers for <see cref="TdlsState"/>. All callers hold the state's
/// <see cref="TdlsState.Gate"/> lock externally. Helpers that allocate new ids advance
/// <see cref="TdlsState.NextItemId"/>; helpers that change status return the updated record
/// (or null if the item didn't exist) and record it in <see cref="TdlsState.Changes"/>, which is
/// what the host broadcasts from.
/// </summary>
public static class TdlsMutations
{
    /// <summary>Default item lifetime per upstream vTDLS docs: two hours from creation.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);

    /// <summary>Allocates a new item id of the form <c>TDLS_{n}</c> and advances <see cref="TdlsState.NextItemId"/>.</summary>
    public static string NewItemId(TdlsState state)
    {
        var id = $"TDLS_{state.NextItemId}";
        state.NextItemId++;
        return id;
    }

    /// <summary>
    /// Resolves the TDLS facility serving the given departure airport id. The vNAS convention
    /// is that facility ids match bare airport identifiers (OAK serves KOAK, SFO serves KSFO,
    /// etc.). Both leading-K and bare forms are accepted on the input. Returns null if no
    /// configured TDLS facility matches.
    /// </summary>
    public static string? ResolveFacilityForAirport(TdlsState state, string airportId)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return null;
        }

        var bare = airportId.StartsWith('K') && airportId.Length == 4 ? airportId[1..] : airportId;

        foreach (var facilityId in state.Configs.Keys)
        {
            if (
                string.Equals(facilityId, bare, StringComparison.OrdinalIgnoreCase)
                || string.Equals(facilityId, airportId, StringComparison.OrdinalIgnoreCase)
            )
            {
                return facilityId;
            }
        }

        return null;
    }

    /// <summary>Finds the active TDLS item for the given (facility, callsign) pair. There is at most one per facility-callsign at any time.</summary>
    public static TdlsItemRecord? FindActiveItem(TdlsState state, string facilityId, string callsign)
    {
        foreach (var item in state.Items.Values)
        {
            if (
                string.Equals(item.FacilityId, facilityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AircraftId, callsign, StringComparison.OrdinalIgnoreCase)
            )
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks every item held for the call sign changed, whatever facility holds it. A record carries no flight-plan
    /// fields of its own — the host resolves them from the world when it builds the item's DTO — so this mark is the
    /// whole of what a flight-plan write owes vTDLS. Caller holds <see cref="TdlsState.Gate"/>.
    /// </summary>
    public static void MarkAircraftChanged(TdlsState state, string callsign)
    {
        foreach (var item in state.Items.Values)
        {
            if (string.Equals(item.AircraftId, callsign, StringComparison.OrdinalIgnoreCase))
            {
                state.Changes.MarkChanged(item.Id);
            }
        }
    }

    /// <summary>
    /// Queues a new Pending TDLS item. Returns the existing record if one is already Pending
    /// for the same (facility, callsign) (idempotent), null if (facility, callsign) is in the
    /// Dumped lockout (auto-gen must not re-create), or the freshly-created Pending record.
    /// Caller holds <see cref="TdlsState.Gate"/>.
    /// </summary>
    public static TdlsItemRecord? QueuePending(TdlsState state, string facilityId, string aircraftId, string? cid, DateTime nowUtc)
    {
        if (state.Dumped.Contains(new DumpedKey(facilityId, aircraftId)))
        {
            return null;
        }

        var existing = FindActiveItem(state, facilityId, aircraftId);
        if (existing is not null)
        {
            return existing;
        }

        var record = new TdlsItemRecord(
            Id: NewItemId(state),
            AircraftId: aircraftId,
            Cid: cid,
            FacilityId: facilityId,
            Status: TdlsItemStatus.Pending,
            Sequence: state.NextItemId,
            CreatedUtc: nowUtc,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: nowUtc + DefaultTtl,
            SentPayload: null
        );

        state.Items[record.Id] = record;
        state.Changes.MarkChanged(record.Id);
        return record;
    }

    /// <summary>Marks a Pending item as Sent and stores the issued clearance payload. Returns the updated record, or null if the item didn't exist or wasn't Pending.</summary>
    public static TdlsItemRecord? MarkSent(TdlsState state, string itemId, TdlsClearance payload, DateTime nowUtc)
    {
        if (!state.Items.TryGetValue(itemId, out var existing))
        {
            return null;
        }

        if (existing.Status != TdlsItemStatus.Pending)
        {
            return null;
        }

        var updated = existing with { Status = TdlsItemStatus.Sent, SentUtc = nowUtc, SentPayload = payload };
        state.Items[itemId] = updated;
        state.Changes.MarkChanged(itemId);
        return updated;
    }

    /// <summary>Marks a Sent item as Wilco. Returns the updated record, or null if the item didn't exist or wasn't Sent.</summary>
    public static TdlsItemRecord? MarkWilco(TdlsState state, string itemId, DateTime nowUtc)
    {
        if (!state.Items.TryGetValue(itemId, out var existing))
        {
            return null;
        }

        if (existing.Status != TdlsItemStatus.Sent)
        {
            return null;
        }

        var updated = existing with { Status = TdlsItemStatus.Wilco, WilcoUtc = nowUtc };
        state.Items[itemId] = updated;
        state.Changes.MarkChanged(itemId);
        return updated;
    }

    /// <summary>
    /// Removes the item from <see cref="TdlsState.Items"/> and adds (facility, callsign) to the
    /// Dumped lockout. Returns the removed record (or null if no such item existed). Lockout
    /// persists for the session — auto-gen cannot re-create a dumped (facility, callsign).
    /// </summary>
    public static TdlsItemRecord? Dump(TdlsState state, string itemId)
    {
        if (!state.Items.TryRemove(itemId, out var existing))
        {
            return null;
        }

        state.Dumped.Add(new DumpedKey(existing.FacilityId, existing.AircraftId));
        state.Changes.MarkRemoved(new TdlsRemoval(existing.Id, existing.FacilityId, existing.AircraftId, Dumped: true));
        return existing;
    }

    /// <summary>Removes a TTL-expired item without recording a Dumped lockout (the item just timed out).</summary>
    public static TdlsItemRecord? Expire(TdlsState state, string itemId)
    {
        if (!state.Items.TryRemove(itemId, out var existing))
        {
            return null;
        }

        state.Changes.MarkRemoved(new TdlsRemoval(existing.Id, existing.FacilityId, existing.AircraftId, Dumped: false));
        return existing;
    }

    /// <summary>
    /// Builds a <see cref="TdlsClearance"/> from the canonical TDLSS payload (nine '|'-separated
    /// positional fields produced by <c>CommandDescriber.DescribeCommand</c>). Empty fields
    /// become null. The caller has validated that the fields list has length 9.
    /// <para>
    /// The field order is the command's, not the record's: field 7 is the departure frequency and field 8 the
    /// local information, which the record declares the other way round.
    /// </para>
    /// </summary>
    public static TdlsClearance ClearancePayloadFromFields(IReadOnlyList<string> fields)
    {
        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        return new TdlsClearance
        {
            Expect = NullIfEmpty(fields[0]),
            Sid = NullIfEmpty(fields[1]),
            Transition = NullIfEmpty(fields[2]),
            Climbout = NullIfEmpty(fields[3]),
            Climbvia = NullIfEmpty(fields[4]),
            InitialAlt = NullIfEmpty(fields[5]),
            ContactInfo = NullIfEmpty(fields[6]),
            DepFreq = NullIfEmpty(fields[7]),
            LocalInfo = NullIfEmpty(fields[8]),
        };
    }
}
