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

## Refresh policy

If upstream changes, refresh by re-fetching `vtdls.md` and the `img/` files with the same paths (any new PNGs in upstream's markdown should be added to `img/` here). Update **Fetched** above. Image-path rewrite is `src="/docs/img/` → `src="img/`.
