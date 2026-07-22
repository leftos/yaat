# vTDLS reference docs (cached)

Authoritative reference for designing YAAT's vTDLS emulation. Mirrored from the upstream vNAS docs so we can read them offline and pin against a known-good snapshot.

## Source

- **Upstream**: <https://docs.virtualnas.net/vtdls/>
- **Fetched**: 2026-07-22
- **Platform**: Material for MkDocs (rendered HTML only — no raw-markdown endpoint). vTDLS
  previously had its own Docsify site at `tdls.virtualnas.net/docs/` serving `vtdls.md`
  directly, which is why this mirror used to be a verbatim copy; upstream has since folded it
  into the same site as the CRC and Data Admin manuals.
- **Pages**: still just one — the whole controller manual is a single page.

## Refresh tool

```bash
uv run tools/refresh-crc-docs.py --section vtdls
```

Since the platform migration this mirror is **generated**, not copied, by the shared
[`tools/refresh-crc-docs.py`](../../tools/refresh-crc-docs.py) that already produces
`docs/crc/` and `docs/vnas-data-admin/`. It fetches the page, converts the article body back
to Markdown, downloads the images into `img/`, and rewrites links. Re-run it and bump
**Fetched** above when upstream changes.

## Files

| Path | Upstream page | Notes |
| --- | --- | --- |
| [`vtdls.md`](vtdls.md) | `/vtdls/` | The complete controller manual. |
| [`img/`](img/) | `/vtdls/img/**` | 10 PNGs referenced inline by the manual. |

## What's NOT mirrored

- The MkDocs shell (theme CSS/JS, search index) — not content.
- CPDLC — explicitly out of scope on the upstream too ("not currently simulated as VATSIM does not officially support CPDLC").

The TDLS *Facility Engineer* configuration docs are no longer a separate site: they now live
at `/data-admin/facilities/` and **are** mirrored, under
[`../vnas-data-admin/facilities.md`](../vnas-data-admin/facilities.md) (see its "TDLS" and
"Operational Configurations" sections).

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

**Sampled coverage** (ZOA, Oakland ARTCC — five TDLS-configured Bay Area + Sacramento + Reno ATCTs under NCT TRACON):

| Facility | Type        | SIDs | climbouts | climbvias | initialAlts | depFreqs | expects | contactInfos | localInfos | Mandatory fields |
|----------|-------------|------|-----------|-----------|-------------|----------|---------|--------------|------------|------------------|
| SFO      | Atct        | 12   | 4         | 5         | 5           | 2        | 3       | 9            | 9          | expect, depFreq |
| OAK      | Atct        | 12   | 6         | 8         | 5           | 3        | 3       | 5            | 4          | expect, depFreq |
| SJC      | Atct        | 7    | 2         | 2         | 6           | 4        | 1       | 2            | 0          | expect, depFreq |
| SMF      | Atct        | 5    | 7         | 3         | 5           | 4        | 3       | 6            | 2          | expect, depFreq |
| RNO      | Atct        | 6    | 0         | 2         | 2           | 2        | 2       | 0            | 0          | sid, expect, depFreq |

> ⚠️ **The SID counts above are a 2026-05-26 snapshot and no longer hold.** Since vNAS added
> [Operational Configurations](#operational-configurations-2026-07-22) on 2026-06-22, SFO and
> OAK have `dclOpConfigsEnabled: true` and their facility-level `sids` arrays are **empty** —
> every SID now lives inside an ops config (SFO 7 configs, OAK 3). The value lists
> (climbouts/depFreqs/expects/…) and the mandatory flags are unchanged. Re-sample before
> relying on any figure in this table.

**Implications for the plan** (mostly confirming, one adjustment):
- ✅ Phase 1.0.2 reduces to: extend `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` with a `TdlsConfig` record class + `[JsonPropertyName("tdlsConfiguration")] TdlsConfig? TdlsConfiguration { get; set; }` on `FacilityConfig`. Mirror the four nested record types from `vatsim-vnas/data/Facilities/Tdls*.cs` (`TdlsConfig`, `TdlsSidConfig`, `TdlsSidTransitionConfig`, `TdlsClearanceValueConfig`).
- ✅ No new download path / no new on-disk cache file. The existing `ArtccConfigResolver`/`ArtccConfigService` already handles caching of the parent ARTCC response, and the TDLS subtree rides along.
- 🔁 **Plan adjustment**: there's **no global "which facilities have TDLS" registry** at the ARTCC root — it's strictly per-facility-node presence of `tdlsConfiguration != null`. The plan section that talks about `TdlsState.Configs` should derive the TDLS facility set by walking the facility tree, not by looking up a top-level list.
- 🔁 **Plan adjustment**: the "Maintain" semantic name from upstream `vtdls.md` maps to the wire field `initialAlts` / `mandatoryInitialAlt` / `defaultInitialAlt`. Keep the wire naming verbatim in the DTO; the UI label can read "Maintain" per the upstream manual.
- ❓ **Still unknown**: whether CRC's vTDLS-related WebSocket topic is named the same way as the data-api JSON keys (`tdls...`). Phase 2.3 still needs a live CRC capture or a vNAS messaging-master review.

**Sample fixture** (committed to `tests/Yaat.Sim.Tests/TestData/artcc-zoa-snapshot.json` for offline parser tests):
- Source: `https://data-api.vnas.vatsim.net/api/artccs/ZOA` (~838 KB minified)
- Captured by: `python tools/refresh-artcc-snapshot.py --artcc ZOA --out tests/Yaat.Sim.Tests/TestData/artcc-zoa-snapshot.json`
- Parser tests: `tests/Yaat.Sim.Tests/Data/ArtccTdlsConfigParseTests.cs`

## Operational Configurations (2026-07-22)

vNAS shipped Ops Configs on **2026-06-22**. A Facility Engineer attaches a distinct SID list —
and distinct per-transition defaults — to each operational configuration; the controller picks
the active one from an **Ops Config menu in the footer** and clicks **Save**. The menu renders
only when the facility has them enabled.

**Wire shape** — `tdlsConfiguration` gained two fields, confirmed identical in the vNAS `data`
lib (`data/Facilities/Tdls/OpConfig.cs`) and in decompiled **CRC 2.17.4**:

```jsonc
"dclOpConfigsEnabled": true,
"opConfigs": [
  { "id": "01KVS8TH…", "name": "OAKW", "sids": [ /* full TdlsSid list, as facility-level sids */ ] }
  // defaultSidId / defaultTransitionId are omitted when null, and whether they're set is
  // per-facility — see the table below. Consumers must tolerate null on either.
]
```

| Facility | ops configs | per-config `defaultSidId` | per-config `defaultTransitionId` |
|---|---|---|---|
| SFO | 7 | all set | all set |
| BOS | 2 | all set | none set |
| OAK | 3 | none set | none set |

Facility-level `defaultSidId` is only meaningful when ops configs are **disabled** (set at BDL,
PVD, ALB, ADW, SMF; null at IAD, DCA, BWI, RDU, SJC, RNO). No facility sampled sets a
facility-level `defaultTransitionId`.

CRC carries the model but has **no** selection logic — it never renders TDLS. The active
config is purely a vTDLS concern.

**The critical consequence**: when `dclOpConfigsEnabled` is true the facility-level `sids`
array is **empty**. A reader that only knows about `sids` sees a facility with no SIDs at all.

| ARTCC | Facility | enabled | ops configs | facility `sids` |
|---|---|---|---|---|
| ZOA | SFO | true | 7 — `2801`, `2828RT`, `2828SO`, `0101`, `1910`, `1919`, `1010` | 0 |
| ZOA | OAK | true | 3 — `OAKW`, `OAKE`, `SFOE` | 0 |
| ZBW | BOS | true | 2 — `Logan Sid`, `FDT` | 0 |
| ZOA | SJC / SMF / RNO | false | 0 | 7 / 5 / 6 |
| ZDC | IAD / DCA / BWI / ADW / RDU | false | 0 | 10 / 10 / 7 / 5 / 10 |

**Defaults really do differ per config** — this is the feature's payload:

```
SFO  TRUKN2.GRTFL   [2801]      localInfo "EXPECT RWY 1R"
     TRUKN2.GRTFL   [2828RT]    localInfo "EXPECT RWY 28L"
     TRUKN2.GRTFL   [0101]      localInfo "EXPECT RWY 1L"
BOS  BLZZR6.- - - - [Logan Sid] contactInfo "CTC $FREQ TO PUSH"
     BLZZR6.- - - - [FDT]       contactInfo "CTC 121.65 TO PUSH"
```

> ⚠️ **SID ids are not stable across configs.** For the same SID *name*, the id differs per
> config at OAK (12 of 12) and BOS (9 of 9); SFO happens to reuse one id across all 7. A SID id
> is only meaningful relative to the config that was active when it was chosen — which matters
> because the clearance sends the id.

**Facility-Engineer side** (mirrored at [`../vnas-data-admin/facilities.md`](../vnas-data-admin/facilities.md)):
enabling ops configs copies the existing SIDs into a config named "Master"; disabling deletes
every associated SID irreversibly. Each config carries Name, its SIDs/transitions, and an
optional default SID + transition.

**`- - - -` convention**: upstream states dashed lines "indicate no option is selected, or none
are available". Across ZBW/ZDC/ZOA **no** value list contains an empty-string entry, while 9
lists carry an FE-authored literal `- - - -` row and 143 of 511 transitions are *named*
`- - - -` (the no-transition placeholder).

## Refresh policy

See **Refresh tool** above — `uv run tools/refresh-crc-docs.py --section vtdls`, then bump
**Fetched**. The old manual instructions (re-fetch `vtdls.md` verbatim, rewrite
`src="/docs/img/` → `src="img/`) no longer apply; the Docsify site they targeted is gone.
