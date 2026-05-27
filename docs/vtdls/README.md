# vTDLS reference docs (cached)

Authoritative reference for designing YAAT's vTDLS emulation. Mirrored from the upstream vNAS docs so we can read them offline and pin against a known-good snapshot.

## Source

- **Upstream**: <https://tdls.virtualnas.net/docs/#/>
- **Fetched**: 2026-05-26
- **Pages on the upstream site**: just one — `vtdls.md`. The site is a docsify SPA whose `homepage` is configured to `vtdls.md`; there is no `_sidebar.md` and no other content pages. All controller-facing documentation lives in the single file.

## Files

| Path | Source URL | Notes |
| --- | --- | --- |
| [`vtdls.md`](vtdls.md) | `https://tdls.virtualnas.net/docs/vtdls.md` | The complete controller manual. Verbatim, except image `src` attributes rewritten from `/docs/img/X.png` to `img/X.png` so the doc renders against the local cache. |
| [`img/`](img/) | `https://tdls.virtualnas.net/docs/img/*.png` | 11 PNGs referenced inline by the manual. |

## What's NOT mirrored

- Docsify shell (`index.html`, theme CSS, `docs-common/` JS plugins) — not content.
- The TDLS *Facility Engineer* configuration docs live in a different site at <https://data-admin.virtualnas.net/docs/#/facilities?id=tdls-configuration> and are out of scope for the controller-side emulation.
- CPDLC — explicitly out of scope on the upstream too ("not currently simulated as VATSIM does not officially support CPDLC").

## How this maps to the YAAT plan

The architecture/scope decisions in [`docs/plans/vtdls-emulation.md`](../plans/vtdls-emulation.md) trace back to specific sections of `vtdls.md`. The points the upstream confirms (some of which the plan originally left provisional):

- **Bay model** — upstream uses **per-facility *lists*** (DCL / PDC / CPDLC), not the rack-based bay layout vStrips uses. The DCL holds filed-but-not-yet-sent items; PDC holds sent-and-pending; CPDLC is empty for us. There is no "move between racks" or "offset" concept. **Action**: drop racks from `TdlsState`; model as two flat per-facility lists keyed off Status (Pending/Sent).
- **Lifecycle** — Pending entries leave the DCL list when (a) a PDC is sent, (b) the flight plan is dumped (controller-initiated), (c) the flight plan activates on departure, or (d) two-hour timeout. Sent (PDC) entries leave when the flight plan activates on departure, or after a two-hour timeout. **Action**: add a 2-hour TTL + activate-on-departure removal to the handler.
- **Multi-facility consolidation** — a parent facility (e.g. ZBW) can be selected to view *unstaffed* child TDLS facilities' lists in one consolidated page; this is non-realistic per upstream and is treated as a convenience. **Action**: keep the per-facility tab model, but plan a "consolidated" virtual facility selector that aggregates unstaffed children.
- **PDC fields** — Expect / SID / Transition / Climb out / Climb via / Maintain / Contact info / Departure frequency / Local info. **Nine fields**, populated from Facility-Engineer-defined options keyed off SID+transition. Mandatory subset enforced server-side; status footer shows MANDATORY FIELD NOT SET or CLEARANCE TYPE: PDC. **Action**: rename `ClearanceDto.InitialAlt` → semantic match for upstream's "Maintain"; the existing 9-field shape already lines up.
- **Cancellation** — upstream says "A PDC cannot be amended once it has been sent. Any amendments must be done over voice." **Action**: `TDLSC` (cancel) only applies to Pending. Once Sent, the only way out is dump-via-radar-client.
- **Re-send on reconnect** — "If a pilot disconnects and then reconnects before departure, a new copy of their PDC is automatically sent to them." **Action**: server stores the last-sent PDC payload per aircraft and re-broadcasts on reconnect.
- **Pre-departure timing** — "PDCs can be sent before the aircraft connects to the network. In this situation, the pilot receives the PDC as soon as they connect." **Action**: queue PDCs against a CID, not an active aircraft id. Holds until the aircraft connects.
- **Auto-WILCO timing** — upstream doesn't quote a delay. Real ACARS PDC is auto-ack'd by the FMS in seconds. The plan's default of ~8s is fine pending observed real behavior.
- **Facility selector / key commands** — upstream key map (F4 dump, F10 cancel, F12 send, `Ctrl+Alt+→/←` cycle facilities). **Action**: mirror in the YAAT vTDLS view.
- **CRC ↔ server topic name + DTO field order** — upstream doesn't expose the wire protocol. **Action**: capture a real frame from CRC ↔ vTDLS before locking; ship a single `TdlsBroadcaster` constant for the topic name.

## Data-api integration (Phase 1.0.1 findings)

**Endpoint**: TDLS configuration is **embedded inside the existing ARTCC config response** — there is **no separate `/api/tdls/...` endpoint**. The same `GET https://data-api.vnas.vatsim.net/api/artccs/{ARTCC_ID}` call YAAT already uses for STARS / Tower Cab / ASDE-X / Flight Strips config also carries TDLS config on every facility node that has it. The loader is a one-field enrichment to `FacilityConfig` in `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs`, not a new downloader.

> ⚠️ ARTCC IDs are **uppercase** on this endpoint (`ZBW` works, `zbw` 404s). The existing `tools/refresh-artcc-snapshot.py` already does this.

**Path**: `root.facility` is a recursive `FacilityConfig` tree (root is the ARTCC, then `childFacilities` for TRACONs/ATCTs/etc.). Any node MAY carry a non-null `tdlsConfiguration` object; the ARTCC root typically doesn't, child ATCTs/ATCT-TRACONs do.

**Wire shape** matches the vNAS source DTOs at `..\vatsim-vnas\data\Facilities\Tdls*.cs` exactly:

```jsonc
"tdlsConfiguration": {
  "mandatorySid": true,
  "mandatoryClimbout": false,
  "mandatoryClimbvia": false,
  "mandatoryInitialAlt": true,
  "mandatoryDepFreq": true,
  "mandatoryExpect": true,
  "mandatoryContactInfo": false,
  "mandatoryLocalInfo": false,
  "sids": [
    {
      "name": "BLZZR6",
      "id": "01G61DSM54F852VA15M7RFBJZ9",
      "transitions": [
        {
          "name": "- - - -",                // literal placeholder = "no transition"
          "id": "01G61DSM542W6VJWGPMHBZZ2BP",
          "firstRoutePoint": "BLZZR",         // matches first filed waypoint after the SID
          "defaultExpect": "10 MIN AFT DP",
          "defaultClimbvia": "CLIMB VIA SID",
          "defaultDepFreq": "133.0",
          "defaultContactInfo": "CTC $FREQ TO PUSH",
          "defaultLocalInfo": "ADV ATIS AND LOCATION"
          // defaultClimbout, defaultInitialAlt omitted (null) for this transition
        }
      ]
    }
  ],
  "climbouts": [],                            // [{ id, value }] — empty here
  "climbvias": [ { "id": "...", "value": "CLIMB VIA SID" } ],
  "initialAlts": [ { "id": "...", "value": "..." } ],
  "depFreqs": [ ... ],
  "expects": [ ... ],
  "contactInfos": [ ... ],
  "localInfos": [ ... ],
  "defaultSidId": "01G61DTSACEE8WM6P3QFZQ0E8Y",
  "defaultTransitionId": "01G61DTSAC93B1THQ1S49Q8V1Q"
}
```

**Sampled coverage** (ZBW, ARTCC ID for Boston Center, parent of the four TDLS facilities upstream `vtdls.md` names):

| Facility | Type        | SIDs | climbouts | climbvias | initialAlts | depFreqs | expects | contactInfos | localInfos | Mandatory fields |
|----------|-------------|------|-----------|-----------|-------------|----------|---------|--------------|------------|------------------|
| BDL      | Atct        | 2    | 0         | 0         | 1           | 2        | 1       | 0            | 0          | sid, expect, initialAlt, depFreq |
| PVD      | AtctTracon  | 1    | 0         | 0         | 1           | 1        | 1       | 0            | 1          | sid, expect, initialAlt, depFreq |
| ALB      | AtctTracon  | 1    | 0         | 0         | 1           | 2        | 1       | 0            | 0          | sid, expect, initialAlt, depFreq |
| BOS      | Atct        | 9    | 0         | 3         | 2           | 1        | 1       | 2            | 1          | sid, expect, depFreq, contactInfo, localInfo |

**Implications for the plan** (mostly confirming, one adjustment):
- ✅ Phase 1.0.2 reduces to: extend `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` with a `TdlsConfig` record class + `[JsonPropertyName("tdlsConfiguration")] TdlsConfig? TdlsConfiguration { get; set; }` on `FacilityConfig`. Mirror the four nested record types from `vatsim-vnas/data/Facilities/Tdls*.cs` (`TdlsConfig`, `TdlsSidConfig`, `TdlsSidTransitionConfig`, `TdlsClearanceValueConfig`).
- ✅ No new download path / no new on-disk cache file. The existing `ArtccConfigResolver`/`ArtccConfigService` already handles caching of the parent ARTCC response, and the TDLS subtree rides along.
- 🔁 **Plan adjustment**: there's **no global "which facilities have TDLS" registry** at the ARTCC root — it's strictly per-facility-node presence of `tdlsConfiguration != null`. The plan section that talks about `TdlsState.Configs` should derive the TDLS facility set by walking the facility tree, not by looking up a top-level list.
- 🔁 **Plan adjustment**: the "Maintain" semantic name from upstream `vtdls.md` maps to the wire field `initialAlts` / `mandatoryInitialAlt` / `defaultInitialAlt`. Keep the wire naming verbatim in the DTO; the UI label can read "Maintain" per the upstream manual.
- ❓ **Still unknown**: whether CRC's vTDLS-related WebSocket topic is named the same way as the data-api JSON keys (`tdls...`). Phase 2.3 still needs a live CRC capture or a vNAS messaging-master review.

**Sample fixture** (Phase 1.0.2 will commit this to `tests/Yaat.Sim.Tests/TestData/` for offline parser tests):
- Source: `https://data-api.vnas.vatsim.net/api/artccs/ZBW` (582 KB minified, ~4.5 MB pretty)
- Captured by: `python tools/refresh-artcc-snapshot.py --artcc ZBW --out tests/Yaat.Sim.Tests/TestData/artcc-zbw-snapshot.json`

## Refresh policy

If upstream changes, refresh by re-fetching `vtdls.md` and the `img/` files with the same paths (any new PNGs in upstream's markdown should be added to `img/` here). Update **Fetched** above. Image-path rewrite is `src="/docs/img/` → `src="img/`.
