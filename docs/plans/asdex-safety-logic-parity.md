# ASDE-X safety logic gap analysis (CRC vs YAAT), SFO

## Headline
1. In real CRC the safety logic is NOT in the client. The decompiled CRC only receives `AsdexAlertDto`, `AsdexHoldBarDto` and `AsdexSafetyLogicConfigurationDto` from the vNAS server and displays them. The alert detection logic lives in the vNAS server, which is not in the decompiled tree or the vnas-messaging DTOs. So "what CRC's logic does" can only be taken from the CRC manual (`docs/crc/asdex.md:916-1056`) and the client rendering. Trigger geometry, timing and thresholds are unverified.
2. YAAT's detector (`AsdexSafetyLogicDetector`) is a simplified stand-in that only runs after a CRC display has pushed a safety-logic configuration. Nothing in yaat or yaat-server seeds that configuration from the ARTCC config or the airport. It is probably never active in a normal session. This is the most likely cause of the complaint, and it is a loading/seeding problem, not only missing logic.
3. YAAT never reads SFO's `runwayConfigurations` or `positions` for the ASDE-X surface logic. Only `targetVisibility*` and, for the 7110.65 3-9-4 advisories, whether the facility is live on one of its named runway configurations (`AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive`) are used.
4. Public FAA and NTSB documentation (YAAT-1069) gives what safety logic alerts on and how controllers respond, never its numeric parameters: [research note](../research/2026-10-29-asdex-safety-logic-public-docs.md). 7110.65 3-6-4.c.3 names two-arrival alerts, which the vNAS server did not raise in the SFO probe; the owner ruled YAAT raises them (YAAT-1061).

## 1. CRC ASDE-X safety logic
Source for alert types: manual `docs/crc/asdex.md:922-931`. Wire/render: decompiled CRC.

| Feature | Trigger (manual) | What the controller sees/hears | Config driving it | CRC file:line |
|---|---|---|---|---|
| Landing on closed runway | arrival imminent on closed rwy | alert box, red octagon, aural "Closed" | runway `IsClosed` (pushed by CRC) | asdex.md:924; aural enum `AsdexAuralAlert.Closed` (messaging/Entities/AsdexAuralAlert.cs) |
| Landing on occupied runway | | same, aural "Occupied" | active runway configuration | asdex.md:925 |
| Two aircraft landing on converging runways | | same, aural `Converging` | runway config | asdex.md:926 |
| Landing vs converging-rwy departure | | same | runway config | asdex.md:927 |
| Departing on closed runway | | same | closed flag | asdex.md:928 |
| Departing on occupied runway | | same | | asdex.md:929 |
| Taxi onto occupied runway (arrival or departure) | | same | | asdex.md:930 |
| Taxiway landing | landing on a *configured* taxiway | aural `Taxiway` + phonetic | server-side taxiway config (not in facility JSON; unverified) | asdex.md:931 |
| Opposite-direction alerts | aural tokens `OppDirTaxi`, `OppDirDeparture`, `OppDirLander`, `GoAround`, `Converging`, `Center`, `Left`, `Right` exist | spoken | server logic (unverified) | AsdexAuralAlert.cs |
| Target alert presentation | any alert | target flashes red/normal, red octagon, full datablocks for involved and partial for the rest | n/a | WindowElementTargets.cs:179,221,257,264,315,445,462; asdex.md:933-940 |
| Alert Message Box | up to 5 alerts, 3 lines (runway/twy, callsigns, text) | green text, red border | `AlertMessageLocation` pref | WindowElementAlertMessage.cs:46-63 |
| Alert filter by active tower position | an alert with `RunwayIds` shows only if the runway is in an active local position's `runwayIds` (split on "-") | | `AsdexConfiguration.Positions[].RunwayIds` | AsdexAlertRepository.cs:54,67-74 |
| Aural alert | `PlayAuralAlert` and runway match; F12 silences | sound | | AsdexAlertRepository.cs:54-57; InputManager.cs:272 |
| Closed runway display | white X at each end | | runway `Area` (4 points) | WindowElementSafetyLogicRunways.cs:37-60 |
| Closed-runway toggle | DCB CLOSED RWY sends config with `IsClosed` flipped. Throws "nonexistent runway" if the runway is not in the received list | | | AsdexSafetyLogicConfigurationRepository.cs:90-100; InputManager.cs:2065 |
| Hold bars | lime line strips for `Status != Suppressed`; the server drives Active/Suppressed (manual: shown at intersections of an occupied runway, not at other active runways unless LAHSO) | | `holdShortRunwayPairs` | WindowElementSafetyLogicRunways.cs:62-68; AsdexHoldBarsRepository.cs; asdex.md:982-988 |
| Runway configuration select | DCB RWY CONFIG (up to 60), F2; pushes `RunwayConfigurationId`. Preview Area line 1 "RWY CFG: name". Default id "limited" (LIMITED: only closed-runway alerts) | | `runwayConfigurations[]` (id, name, arrival/departure ids, holdShortRunwayPairs) | AsdexSafetyLogicConfigurationRepository.cs:61-65,102-105; AsdexRunwayConfiguration.cs:7-17; WindowElementPreviewArea.cs:61; asdex.md:990-1006 |
| Tower config positions | local toggle; GC/LC | | `positions`, `defaultPositionId` | asdex.md:1008-1018 |
| Arrival-alert inhibit per position | global; manual says 3 min or until config change (that timer is server-side, unverified) | Preview Area line 4 | `InhibitedArrivalAlertPositionIds` | AsdexSafetyLogicConfigurationRepository.cs:107-119; asdex.md:1024-1030 |
| Per-target alert inhibit / ALL TRACKS ENABLE | square around target; `TRK ALERT INHIB` | | | asdex.md:1038-1054 |
| MIL scratchpad | `MIL` in the scratchpad suppresses alerts between formation aircraft | | | asdex.md:1056 |
| Temporary closed/restricted areas | map areas | | temp data | asdex.md:1066-1074 (out of scope here) |

Key mechanism: CRC builds its runway list (`mRunways`) only from the server's `AsdexSafetyLogicConfiguration` topic (AsdexSafetyLogicConfigurationRepository.cs:49-60). If the server never sends it, the Closed Runway submenu is empty and no runway X is drawn.

## 2. ZOA / SFO configuration
Fetched live to `X:/dev/yaat/.tmp/zoa-artcc.json` (854,494 bytes, HTTP 200). Facility `/ZOA/NCT/SFO` (type Atct), `asdexConfiguration`:

- `videoMapId` = 01GKQDSRS28S2X85BXZF3NAKN0; `defaultRotation` 28; `defaultZoomRange` 100; `targetVisibilityRange` 15; `targetVisibilityCeiling` 1500; `useDestinationIdAsFix` true; 41 `fixRules`; `towerLocation` = {0,0}
- `runwayConfigurations` (6; `holdShortRunwayPairs` empty in all, so no LAHSO):
  - L28D01 (arr/dep 1L,1R,28L,28R)
  - L28D28 (28L,28R)
  - L19D10 (10L,10R,19L,19R)
  - L19D19 (10L,19L,19R,28R)
  - L10D10 (10L,10R)
  - L01D01 (1L,1R,28L,28R)
- `positions`: LC (`runwayIds` 1L-19R, 1R-19L, 28L-10R, 28R-10L) and GC (none). `defaultPositionId` is GC's id, so the default position covers no runways. Only LC activates alerts.
- No taxiway, converging-pair or hold-bar field exists in the facility config.
- There is a "LIMITED" config in addition to these 6 (implicit, CRC default id).
- SFO is the only ZOA facility with runway configurations: OAK, SJC, SMF, RNO and HWD have 0 (from `docs/vnas-artcc-config-examples/zoa.json`, which also has SFO's 6). Other ARTCCs with ASDE-X configs in the cache: PHX 2, DEN 15, LAX 3, SNA 2, SAN 3, LAS 4 (4 LAHSO pairs), SLC 2, MSP 11 (4 LAHSO pairs), PDX 3, SEA 6. SFO is therefore a full safety-logic airport and not a LIMITED-only one, but unlike LAS and MSP it has no LAHSO.
- Hold-bar geometry is not in the facility config or a video map: CRC draws only the points the server sends. YAAT synthesises the bars from the airport ground layout (built, see section 4). Taxiway-landing geometry has no such source yet.

## 3. YAAT today
| CRC feature | State | Evidence |
|---|---|---|
| Closed-runway alert | Implemented; runs on the load-time seeded config, with or without a CRC push. A departure alerts from 50 kt over the footprint (`RWY CLOSED`); an arrival from 1 NM out, whatever its phase (`RWY CLOSED, GO AROUND`); both on the shared runway-user rule | `AsdexRunwayUsers`; `AsdexSafetyLogicDetector.Detect` |
| Occupied-runway alerts (taxi-onto-active folded in, YAAT-1193) | Partial. Texts and per-pair ids match vNAS (YAAT-1192). They are binary: a runway user (`AsdexRunwayUsers`) plus an occupant of the footprint. There is no time-to-collision look-ahead. An airborne arrival counts once it is over the footprint | `AsdexRunwayUsers`; `AsdexSafetyLogicDetector.Detect` |
| Landing on converging runways, landing vs converging departure | Missing (deliberately; the enum notes it) | AsdexSafetyLogicDetector.cs:6-9 |
| Taxiway landing | Partial. Centerline proximity of 0.015 nm to ANY taxiway (not "configured" taxiways), so false positives are likely | AsdexSafetyLogicDetector.cs:136-174, 72 |
| Opposite-direction, go-around and other aural tokens | Missing. Aural sequence is only Warning/Runway/id + Closed or Occupied, or Warning/Taxiway/phonetic | DtoConverter.cs:2221-2239 |
| Active runway configuration gating (which alerts fire per `runwayConfigurations`) | Implemented as the LIMITED gate. While the id is `limited`, empty or names none of the facility's `runwayConfigurations`, only ClosedRunway alerts are raised; under any named configuration every alert kind is raised on every runway, and the arrival/departure runway ids play no part (measured on vNAS Test) | `AsdexSafetyLogicSeed.NamesRunwayConfiguration`; `AsdexSafetyLogicDetector.Detect` (`closedRunwayAlertsOnly`) |
| Position filter (LC/GC `runwayIds`) | Missing on the server (CRC filters client-side from the facility JSON, which YAAT passes through). `AsdexPositionConfig` is parsed, but nothing in yaat-server uses it | ArtccConfig.cs:720-730 |
| Arrival-alert inhibit by position (and 3-min timer) | Stored and echoed only; the detector ignores `InhibitedArrivalAlertPositionIds` | AsdexSafetyLogicConfig.cs:15; detector |
| Per-target inhibit and ALL TRACKS ENABLE | Implemented; the detector excludes inhibited aircraft | TrackEngine.cs:501; SimulationEngine.Asdex.cs:332-339; detector.cs:82 |
| MIL scratchpad suppression | Missing. No "MIL" handling found in src (search for the literal returned none) | |
| Hold bars (`ReceiveAsdexHoldBars`) | Implemented. The bars are synthesised from the ground layout, each Active or Suppressed by the runway users ahead of it, and sent whole as upserts; the runway configuration does not gate them and LAHSO pairs (`JsonElement`, unused) are not read | `AsdexHoldBarBuilder`, `AsdexHoldBarStatusEvaluator`, `SimulationEngine.TickAsdexHoldBars`, `CrcBroadcastService.BroadcastAsdexHoldBarsAsync`; ArtccConfig.cs:746-747 |
| Closed-runway X and DCB runway list | Works from the seed: the server sends each facility's seeded config at subscribe and at scenario load, and returns null only for a facility with no seed (no ARTCC config, no `asdexConfiguration` or no navdata runways) | CrcBroadcastService.cs:1882-1892 |
| Seeding of runway footprints (`Area`) and `IsClosed` | Implemented. `AsdexSafetyLogicSeed.Build` seeds every facility at scenario load with runway footprints, all open, and a named runway configuration picked from the active runways (re-picked on an active-runway change unless a CRC user chose one); `HoldShortRunwayPairs` has no use in yaat-server | `AsdexSafetyLogicSeed.cs`; `SimulationEngine.Asdex.cs` |
| Config echo, recording and replay | Implemented | CrcClientState.Asdex.cs:245-272 |
| Standing-set withdrawal on rewind/unload | Known gap, no Delete | crc-display-state.md:326 |

Facts about the `Detect` input: it is `Yaat.Sim.Simulation.SimulationEngine.TickAsdexAlerts` (SimulationEngine.Asdex.cs:197-231), which clears all alerts when the config is null or has no runways (lines 207-217). It hands `Detect` the facility's runways as navdata resolves them (`AsdexSafetyLogicRunways`, the cache the hold bars read too). Alignment is true track against the runway's true bearing from navdata (+/-35 deg), and height over a runway is measured from the mean of its end elevations.

## 4. Gap table
| Feature | CRC behaviour | YAAT state | Config field | Size | Repo |
|---|---|---|---|---|---|
| Seed `AsdexSafetyLogicConfiguration` (runway polygons, closed flags, default config) at scenario load | vNAS server sends it at subscription so the CRC Closed Runway menu and X markers work | Seeded at scenario load and republished to subscribed displays; built | `runwayConfigurations`, nav-DB runway geometry | multi-file (runway rectangle synthesis from nav DB, seed on subscribe and at scenario load, record as an action for replay) | yaat-server + yaat |
| Honor active runway configuration (incl. LIMITED) | gates alert set | LIMITED gate built (closed-runway alerts only); named configurations raise every kind on every runway; hold bars not gated | `runwayConfigurations[].arrival/departureRunwayIds`, `RunwayConfigurationId` | built | yaat (Sim detector) |
| Position gating and arrival-alert inhibit with 3-min timer | Position filter is client-side; arrival inhibit is server-side | inhibit list stored, ignored; no timer | `positions`, `InhibitedArrivalAlertPositionIds` | multi-file | yaat |
| Converging-runway alerts (2 types) | alert and aural `Converging` | missing | runway geometry plus intersection | multi-file / design (needs aviation review) | yaat |
| Opposite-direction / go-around alerts and aural | exist in aural enum | missing | | multi-file / design | yaat + yaat-server |
| Hold bars with Active/Suppressed status | lime lines at intersections, LAHSO aware | Built: layout-synthesised bars lit by the runway users ahead of them. Not yet: gating by the active runway configuration, LAHSO pairs | `holdShortRunwayPairs` (SFO has none) | built; the configuration and LAHSO gating stay with the "Honor active runway configuration" row | yaat-server + yaat |
| Look-ahead (time-to-collision) instead of binary overlap | real ASDE-X predicts; manual says "imminent" | footprint overlap only | n/a | design | yaat |
| Taxiway landing restricted to configured taxiways | only configured taxiways | any taxiway centerline | not in facility JSON (unverified where CRC gets it) | one-file but data-blocked | yaat |
| MIL scratchpad suppression | formation exemption | missing | scratchpad | one-file | yaat |
| Alert withdrawal after rewind/unload | n/a (CRC re-syncs) | known gap | | one-file | yaat-server |

## 5. Configuration / loading suspicions (ranked)
1. Seeding (resolved): the detector needs a config in the scenario, and the CRC UI cannot make one when `mRunways` is empty (`GetOpenCloseRunwayDto` throws; `GetSetRunwayConfigurationDto` would push an empty runway list, which the detector treats as "no config"). Every ASDE-X facility with navdata runways is now seeded at scenario load, so the CLOSED RWY menu is populated and the detector runs without a push.
2. YAAT's 7110.65 3-9-4 advisories and solo scoring stand down at SFO only while its live Safety Logic configuration is one of its six named runway configurations (`AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive`); on LIMITED, or with no live configuration, they apply (YAAT-1200).
3. If the user means the YAAT instructor client: it has no ASDE-X alert view (docs say server-side emulation only: crc-display-state.md:380). Unverified for alerts specifically.
4. `defaultPositionId` is GC with no runways, so by manual rule alerts show only when the LC position is activated in the DCB. This is a CRC-side setting, not a YAAT defect.

## DOC DRIFT
- `docs/crc-protocol-support.md:185-186` marks `ReceiveAsdexAlerts`/`DeleteAsdexAlerts` `[ ]` "requires detector (deferred)"; code ships them (`CrcBroadcastService.cs:606-634`, and the same file's line 289 is `[x]`). Doc wrong; owner crc-protocol-support.md.
- `docs/crc-protocol-support.md` no longer calls hold-bar status the sole remaining ASDE-X parity gap: it lists runway-config gating, position/arrival-inhibit use and converging alerts as the open parts.
- `AsdexSafetyLogicDetector.cs:48` says "Mirrors CRC's simplified binary alerting"; the real alerting is server-side and unverified, so the claim is unsupported.

## TASK INDEX GAP
Task: "ASDE-X safety logic / alerts / hold bars for CRC at an airport". Files in order: `docs/crc/asdex.md:916-1056`, `docs/crc-display-state.md` (safety-logic section, ~318-334), `docs/crc-protocol-support.md:177-190,285-295`, `src/Yaat.Sim/Asdex/AsdexSafetyLogicDetector.cs`, `AsdexSafetyLogicConfig.cs`, `SimulationEngine.Asdex.cs:96-266`, `yaat-server/.../CrcClientState.Asdex.cs:245`, `CrcBroadcastService.cs` (the `AsdexSafetyLogicConfiguration`, `AsdexAlerts` and `AsdexHoldBars` builders), `DtoConverter.cs` (the ASDE-X converters), `Data/Vnas/ArtccConfig.cs:374-408,720-748`, decompiled `Asdex.SafetyLogic/*`. architecture.md has file-tree rows (1352-1357) but no Task Index row.

## MISSING ENTRY POINT
None. `docs/architecture.md` and `docs/README.md` exist. The terms "LIMITED configuration" and "tower configuration position" are used in a project sense with no CONTEXT.md glossary entry (not checked in CONTEXT.md; unverified).
