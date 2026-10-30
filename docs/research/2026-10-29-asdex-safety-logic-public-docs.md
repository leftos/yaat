# YAAT-1069 — Public ASDE-X / ASSC safety-logic documentation

Research note, 2026-10-29. Read-only research; nothing in the repo changed. "Secondhand" = a source quoting or paraphrasing another; "Inferred" = my reading, not a source's statement.

Headline: the FAA publishes **what** safety logic alerts on and **how controllers respond**, never the numeric alert parameters. The only public numbers come from NTSB factual reports (departure-state thresholds, alert timing in individual events) and from RWSL documentation (RWSL is a separate system fed by ASDE-X, not the safety logic itself). The FAA describes the parameters as site-adapted.

## Sources

| # | Title | Issuer | Date | URL / path | Section |
|---|-------|--------|------|-----------|---------|
| S1 | JO 7110.65 (local copy) | FAA | current edition in repo | `X:/dev/yaat/.claude/reference/faa/7110.65/chap03_sec06.md` | 3-6-1, 3-6-2, 3-6-3, 3-6-4 |
| S2 | JO 7110.65 (local copy) | FAA | same | `.../7110.65/chap03_sec09.md` L121-149; `chap03_sec10.md` L308-309 | 3-9-4 (LUAW), 3-10-5 (landing clearance w/ LUAW) |
| S3 | P/CG (local copy) | FAA | same | `.../7110.65/pcg.md` L526, L5692-5696 | ASDE; SAFETY LOGIC SYSTEM (local copy of the ALERTS sub-entries is empty — text taken from S4) |
| S4 | P/CG, "S" section | FAA | current web edition | https://www.faa.gov/air_traffic/publications/atpubs/pcg_html/chap1_section_20.html | SAFETY LOGIC SYSTEM ALERTS |
| S5 | JO 7210.3 Ch.12 Sec.7 "Safety Logic Systems" | FAA | current web edition (page returned 403 to fetch; text via search extract) | https://www.faa.gov/air_traffic/publications/atpubs/foa_html/chap12_section_7.html | 12-7-1 … 12-7-5 |
| S6 | JO 7210.3Y Ch.11 Sec.9 | FAA | eff. 2014-04-03 (superseded; kept for text the current edition may still carry) | http://www.faa.gov/air_traffic/publications/atpubs/FAC/1109.html | 11-9-1 … 11-9-6 |
| S7 | AIM (local copy) | FAA | current in repo | `.../aim/chap04_sec05.md` L78-82; `.../aim/chap02_sec01.md` L81-106 | 4-5-5 ASDE-X/ASSC; 2-1-6 RWSL |
| S8 | Runway Status Lights page | FAA | undated web | https://www.faa.gov/air_traffic/technology/rwsl | Operating characteristics; FAQ |
| S9 | RWSL brief to ACF (NACO) | FAA (D. Bryan) | undated (c. 2008, inferred from filename "08-01") | https://www.faa.gov/air_traffic/flight_info/aeronav/acf/media/Presentations/08-01-Runway_Status_Lights_Brief_to_ACF.pdf | "REL Characteristics" |
| S10 | ATC Group Chairman's Factual Report OPS15IA011AB (ORD, ENY3084/GJS3710) | NTSB | 2016-05-04 | https://data.ntsb.gov/Docket/Document/docBLOB?FileExtension=.PDF&FileName=Air+Traffic+Control+Factual+Report-Master.PDF&ID=40441438 | E.2 History of Flight |
| S11 | ATC Group Chairman's Factual Report, FLL 2016-01-29 (NKS371/Truck54) | NTSB | n.d. in extract | https://data.ntsb.gov/Docket/Document/docBLOB?FileExtension=.PDF&FileName=Air+Traffic+Control+Factual+Report-Master.PDF&ID=40482345 | 2.0 Radar and Runway Data |
| S12 | ATC Group Chairman's Factual Report OPS15IA017 (MIA, UPS357/N900QS) | NTSB | 2015-11-23 | https://data.ntsb.gov/Docket/Document/docBLOB?FileExtension=.PDF&FileName=Air+Traffic+Control+Factual+Report-Master.PDF&ID=40482334 | 1.2 History of Flight |
| S13 | ATC Group Chairman's Factual Report DCA17IA148 (SFO, ACA759 taxiway C) | NTSB | 2018-04-24 | https://data.ntsb.gov/Docket/Document/docBLOB?FileExtension=.PDF&FileName=Air+Traffic+Control+Factual+Report-Master.PDF&ID=40462883 | 4.0 ASSC (p.9); F. Previous Safety Recs (pp.14-15) |
| S14 | NTSB press release NR20240620 (JFK Swiss A330, 2024-04-17) | NTSB | 2024-06-20 | https://www.ntsb.gov/news/press-releases/Pages/NR20240620.aspx | body |
| S15 | NTSB press release NR20240604a (JFK 2023-01-13) | NTSB | 2024-06-04 | https://www.ntsb.gov/news/press-releases/Pages/NR20240604a.aspx | body |
| S16 | AV-2008-004 "FAA Needs To Improve ASDE-X Management Controls…" | DOT OIG | 2007-10-31 | https://www.oig.dot.gov/sites/default/files/WEB_ASDE-X_10-31-07.pdf | Findings |
| S17 | GAO-08-29 "Aviation Runway and Ramp Safety" | GAO | 2007-11 | https://www.govinfo.gov/content/pkg/GAOREPORTS-GAO-08-29/html/GAOREPORTS-GAO-08-29.htm | runway safety technology; fn 50 |
| S18 | N JO 7210.860 "Converging Runway Operations" | FAA | 2014 rollout | https://www.faa.gov/documentLibrary/media/Notice/N_JO7210.860_Converging_Runway_Operations.pdf | 10-3-14 |
| S19 | LaGuardia 2026-03 collision coverage (NTSB chair quoted) | NBC News / AP | 2026-03-25 / 2026-04-23 | https://www.nbcnews.com/news/us-news/investigators-search-answers-deadly-laguardia-crash-rcna265118 ; https://apnews.com/article/laguardia-plane-crash-air-canada-report-2fa0bdfb1ccf87b8dc753ab2fe2d6b7a | **secondhand** |
| S20 | Reason Foundation, "What went wrong at LaGuardia" | R. Poole | 2026-04-14 | https://reason.org/aviation-policy-news/what-went-wrong-at-laguardia-airport-last-month/ | **secondhand, anonymous informant** |
| S21 | vNAS CRC ASDE-X docs | vNAS (not FAA) | live | https://docs.virtualnas.net/crc/asdex/ | Safety Logic — for comparison only |

## Alert types and triggers

General definition — S3/S4 P/CG: "A software enhancement to ASDE-3, ASDE-X, and ASSC, that predicts the path of aircraft landing and/or departing, and/or vehicular movements on runways. Visual and aural alarms are activated when the safety logic projects a potential collision." The P/CG ALERT definition (S4) has three classes:
- "a. An actual situation involving two real Safety Logic tracks (aircraft/aircraft, aircraft/vehicle, or aircraft/other tangible object) that the Safety Logic System has predicted will result in an imminent collision, based upon the Safety Logic parameters."
- "b. An actual situation involving a single Safety Logic track arriving to, or departing from, a closed runway."
- "c. An actual situation involving a single Safety Logic track arriving to a taxiway."

S1 3-6-4 distinguishes a "warning alert" (a.) — implying a lower class (caution) exists. **Inferred**: no public FAA text names or defines the caution/warning split for ASDE-X; the real aural text "Warning Runway Two Eight Right Occupied" (S10) confirms the "warning" wording.

### Runway occupied (arrival)
- S1 3-6-4.a: "When an arrival aircraft (still airborne, prior to the landing threshold) activates a warning alert, the controller must issue go-around instructions. (Exception: Alerts involving known formation flights, as they cross the landing threshold, may be disregarded …)". NOTE: "a side-step maneuver or circle to land on another runway satisfies this requirement."
- S12 (MIA 2015): alert fired with the arrival "4039 feet or .66 nm from the runway threshold at 325 feet agl", target holding in position on the same runway. One event, not a parameter.
- S2 3-9-4 / 3-10-5: full-core safety logic is what permits landing clearance with an aircraft in LUAW ("Facilities using safety logic in the full core alert runway configuration may clear an aircraft for a full-stop … on the same runway with an aircraft holding in position or taxiing to LUAW except when reported weather conditions are less than ceiling 800 feet or visibility less than 2 miles"). **Inferred**: so the occupied-arrival alert is expected to cover an aircraft in LUAW.

### Runway occupied (departure) / runway incursion by a taxiing target
- S10 (ORD 2015): "an ASDE-X alert was issued when ENY3084 met the velocity (47 knots) and acceleration (2.5 knots/sec) parameters that are required by the ASDE-X to be considered a departure"; when the other aircraft entered the runway, "an aural alert of 'Warning Runway Two Eight Right Occupied' was voiced … alert text 'RWY 28R|ENY3084, GJS3710|RWY OCCUPIED'". Note: the taxiing aircraft was ~42 ft short of the edge line at first alert — i.e. predicted entry, not only actual entry.
- S14 (JFK 2024): no alert "because the Swiss Air flight never reached the thresholds of both acceleration and velocity that would indicate to the system that the jetliner was in a 'departure state.'"
- S15 (JFK 2023): ASDE-X "issued aural and visual alerts" as a crossing B777 entered ahead of a departing 737.
- S11 (FLL 2016): alert for a truck on the runway ahead of a departure; alert at 0200:24 after roll start ~0200:00.

### Closed runway (arrival / departure)
- S4 ALERT b (above). S5 12-7-2(5): "When a runway becomes unavailable for aircraft operations for an extended period of time, the runway should be entered as 'closed' in the safety logic system."
- Survives LIMITED configuration on ASDE-X/ASSC (see Configuration).

### Converging / intersecting runways
- S1 3-6-4.c: "When two arrival aircraft, or an arrival aircraft and a departing aircraft activate an alert, the controller will issue go-around instructions or take appropriate action to ensure intersecting runway separation is maintained." References 3-9-8 / 3-10-4.
- S16 (OIG 2007): build 2 adds "alerts for intersecting runways and converging taxiways"; Louisville commissioned July 2007 "with the capability to alert controllers of potential collisions on intersecting runways and converging taxiways". IOT&E found an intersecting-runway case where the alert was "too slow: an aircraft landed on one runway as another aircraft was taxiing on a runway toward the intersection".
- S18: ASDE-X "Virtual Runway Intersection Point (VRIP)" named as a tool for non-intersecting converging runways (1 NM extensions). **Inferred**: a display/procedural aid; no public text says safety logic alerts on VRIPs.
- No public geometry, distance or time parameter for the converging alert.

### Opposite direction
- No FAA or NTSB public source found that describes an opposite-direction alert (OPP DIR TAXI / DEPARTURE / LANDER). Only the vNAS aural tokens (yaat-server probe) show them. **Gap.**

### Go-around
- No public source describes a safety-logic "go-around" alert type. 3-6-4 makes go-around the controller's *response*. vNAS has a "GoAround" aural token (probe). **Gap.**

### Taxiway landing (arrival to a taxiway)
- S4 ALERT c; S1 3-6-4.b: "When an arrival aircraft activates a warning alert to a taxiway, the controller must issue go-around instructions."
- S13 (SFO 2017, ASSC): no alert as ACA759 lined up on taxiway C. FAA's reply to NTSB A-11-012/-013 (quoted pp.14-15): "The ability to accurately predict that an aircraft is arriving to a taxiway is not possible without significant degradation in performance, timeliness, and accuracy of safety logic alerts for the more likely event of an aircraft arriving to a closed or occupied runway. These same performance trade-offs exist today at some airports with close parallel and offset threshold runways." NTSB classified the recs "CLOSED--UNACCEPTABLE". **Inferred**: taxiway-arrival alerting exists only where a taxiway is specifically adapted (matches vNAS "configured taxiway").

### Wrong runway
- No public source describes a wrong-runway alert in ASDE-X safety logic (S16 cites Lexington 2006 as motivation for closed-runway alerting only). **Gap.**

### Converging taxiways
- S16: build 2 "alerts for intersecting runways and converging taxiways"; FAA "did not test the converging taxiway capability". No description of behaviour. vNAS does not model it.

## Parameters

| Parameter | Value | Source | Notes |
|-----------|-------|--------|-------|
| Departure state (ASDE-X safety logic, ORD) | ≥47 kt and ≥2.5 kt/s | S10 | Site event; both conditions. "parameters that are required by the ASDE-X to be considered a departure" |
| Departure state requires both velocity and acceleration | qualitative | S14 | JFK 2024 no-alert |
| Departure state (RWSL, FLL) | 48.5 kt and 3.8 kt/s | S11 | RWSL system, not safety logic; "minimum speed of 48.5 knots with an acceleration rate of 3.8 knots per second" |
| Occupied-arrival alert, observed | 0.66 nm / 325 ft AGL before threshold | S12 | One event; not a configured value |
| Alert lead time (AMASS simulations) | 8–11 s before potential collision | S17 fn 50 | **secondhand** (GAO quoting NTSB), AMASS not ASDE-X |
| False-alert requirement | ≤2 false alerts / 24 h | S17 | GAO: "system requirements not to generate more than two false alerts within 24 hours" |
| Positional accuracy | ±20 ft | S10, S12 | |
| Surveillance coverage height | surface to 200 ft | S7 4-5-5 | primary radar: "the airspace up to 200 feet above the surface" |
| ASSC extended-centerline display | ~2.5 nm | S13 p.9 | display, not logic |
| Parameters site-adapted | qualitative | S4 ("based upon the Safety Logic parameters"); S20 | S20 **secondhand/anonymous**: time/distance "safety cells", tuned per airport to suppress false alarms |
| RWSL REL arrival activation | ~1 NM from threshold, site adjustable | S7 2-1-6.b.2, S8 | RWSL, not safety logic |
| RWSL REL departure activation | ~30 kt, site adaptable | S7, S8, S9 | |
| RWSL REL extinguish before intersection | 3–4 s (local AIM) vs 2–3 s (FAA RWSL page) vs 3 s (S9) | S7, S8, S9 | sources disagree |
| RWSL arrival slow-down | 80 kt → only arrays within 30 s stay; 34 kt → taxi state, all off | S7, S8 | S9 (older) says 65 kt / 50 kt |
| RWSL THL extent | 1,500 ft starting 375 ft from threshold | S7 2-1-6.c | |
| Arrival-alert inhibit duration | none in FAA docs | — | vNAS: 3 min (S21) |

## Configuration and inhibits

- Full core required: S5 12-7-1(2) "The safety logic system must be operated in a full core alert runway configuration. (In ASDE-X/ASSC, when rain configuration is selected, it includes full core alerting capabilities.)"
- Operated continuously: S1 3-6-1.b.1 "ASDE systems with safety logic must be operated continuously."
- Radar-only mode stays full core: S1 3-6-1 NOTE "The system stays in full core alert status under radar-only mode but without automatic data block capability."
- Maintenance mode: S5/S6 "ASDE-X/ASSC safety logic will automatically be disabled when the system is in maintenance mode."
- Rain configuration: S5 12-7-4; for AMASS "all safety logic alerts with the exception of arrivals to a closed runway are inhibited"; for ASDE-X/ASSC rain config keeps full core (12-7-1).
- LIMITED configuration: S5 12-7-5 NOTE b "For ASDE-X/ASSC the limited configuration disables all alerts except arrivals to and departures on a closed runway and is not considered full-core alert status." Use only "to temporarily inhibit persistent false alerts".
- Track drop: S1 3-6-2.c a verified false track "may be temporarily dropped, which will remove the target from the display and safety logic processing" (logged on 7230-4).
- Real-target inhibit: S6 11-9-1.h "The air traffic manager may authorize a real target to be inhibited from safety logic processing when the target will likely generate a nuisance alert."
- Construction: S6 11-9-1.f — NASE "may be able to provide an adaptation to filter the affected areas from safety logic system coverage".
- Nuisance alert examples: S4 "known situation that is not considered an unsafe operation, such as LAHSO", and aircraft "not intending to use a runway/taxiway (helicopter, pipeline patrol, non-Mode C overflight, etc.)". **Inferred**: LAHSO adaptation exists so LAHSO is not alerted.
- Formation flights: S1 3-6-4.a exception (procedural, not a system inhibit). vNAS uses `MIL` scratchpad (S21) — not in FAA docs.
- Watch checklist: S6 11-9-6 operational status, runway configuration, display on all ASDE displays, aural test.
- Position-based arrival-alert inhibit, tower-configuration positions: not in any public FAA doc (vNAS only).

## Hold bars

- No public FAA source describes safety-logic "hold bars" on the ASDE-X display. S13 (SFO, ASSC) has the controller saying "the bars on the runway were up [indicating the runway was clear]" — secondhand interview paraphrase, the only FAA-side mention found.
- **Inferred**: the CRC/vNAS hold bars are a display analogue of RWSL REL logic (1 NM arrival trigger, ~30–50 kt departure trigger), which is the only publicly parameterised logic of that shape (S7, S8). The probe's ≈6000 ft (≈1 NM) arrival trigger matches the RWSL REL "approximately 1 mile" figure.
- RWSL (S7 2-1-6): REL "illuminate when … high speed traffic on the runway or there is an aircraft on final approach within the activation area"; THL need "Two aircraft, or a surface vehicle and an aircraft"; both have "anticipated separation". S19 (secondhand, NTSB prelim via AP, LGA 2026): "By design, the lights turn off two or three seconds before a plane reaches a runway intersection".
- RIL (Runway Intersection Lights) and FAROS listed as RWSL components (S9) — no parameters found.

## Disagreements with the vNAS-mirroring rulings

1. **Two arrivals on converging runways — public FAA text expects an alert.**
   - FAA JO 7110.65 3-6-4.c (local `chap03_sec06.md` L72): "When two arrival aircraft, or an arrival aircraft and a departing aircraft activate an alert, the controller will issue go-around instructions or take appropriate action to ensure intersecting runway separation is maintained."
   - vNAS's own public CRC docs (S21) list "Two aircraft landing on converging runways" as an alert event, separately from "An aircraft landing on a runway converging with an aircraft departing from another runway".
   - Ruling: "converging-runway alerts only when a departure is involved, never two arrivals" (matches the probe, yaat-server `docs/plans/asdex-probe-sfo/README.md` L317-323, one test at config L28D01). So the ruling departs from both the FAA procedure text and vNAS's published docs; it agrees only with the vNAS server's observed behaviour. Caveat: 3-6-4.c says how to respond *if* such an alert fires; it does not state the real system's adaptation at any given site, and the alert may be site-configured off.
2. **No distance cap on the landing-vs-departure converging alert — no public source either way.** FAA documents give no converging geometry or distance/time window. The only public hints point to time-based prediction ("predicts the path", "projected a potential collision", S3/S4) and to site-adapted time/distance cells (S20, secondhand). An uncapped distance is not contradicted by any public text, but nor is it supported; flag as unsupported rather than disagreeing.
3. Not a ruling, but related: arrival-alert position inhibit (3 min) and tower positions have no FAA public basis; nothing to compare.

## Gaps (no public source gives)

- Any numeric safety-logic parameter: arrival look-ahead time/distance/altitude gate for occupied/closed/converging alerts; caution vs warning thresholds; occupancy width ("on the runway") definition.
- Converging-runway geometry (projection point, time windows, whether non-intersecting converging runways count).
- Whether arrival–arrival converging alerts are adapted on at real sites.
- Opposite-direction, go-around, and wrong-runway alert types — no public FAA/NTSB description found.
- Hold-bar (display) logic for ASDE-X/ASSC; LAHSO configuration effect on bars or alerts.
- Taxi-onto-runway alert: whether it needs an arrival/departure state on the runway (S10 implies a departure in departure state; arrival case undocumented).
- Arrival-alert inhibit duration and per-position runway assignment.
- The current JO 7210.3 chapter 12-7 page could not be fetched directly (403); text taken from search extracts of the live page and from the 2014 7210.3Y edition.
- Vendor (Sensis/Saab) safety-logic papers: none with parameters found publicly.
