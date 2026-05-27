# Phraseology coverage backlog

Produced by the systematic audit described in [`archive/phraseology-coverage-audit.md`](./archive/phraseology-coverage-audit.md). One section per audited FAA chapter; one entry per distinct controller or pilot phrasing.

## Entry format

Every entry uses these four fields in this order. No prose. Keep entries scannable.

```markdown
### §X-Y-Z (or AIM Y-Z-W) — Brief topic

- **Status:** Covered | MissingRule | MissingCanonical | OutOfScope
- **Phrasing:** "EXACT FAA WORDING WITH (variables) IN PARENS"
- **Canonical:** existing or proposed canonical command (`CTO`, `CM 5000`, `EF 28R`, etc.). Use `??` if MissingCanonical and no obvious shape yet.
- **Notes:** one line — for Covered, point at the rule line (e.g. `PhraseologyRules.cs:142`); for MissingRule, name the verb to extend; for MissingCanonical, sketch the new `ParsedCommand` shape; for OutOfScope, why.
```

### Conventions

- **Multiple variants of one phrasing** (e.g. "cleared for takeoff" vs "clear for takeoff") get ONE entry unless variants reveal a gap.
- **Word-order variants** (suffix vs prefix runway) only get a separate entry when one is covered and the other isn't.
- **Phrasings spanning multiple sections** get one entry under the primary section, with `Notes: also at §A-B-C`.
- **Compound clearances** (e.g. "cleared to land, hold short of") may produce one entry if a rule handles the compound, or two entries if each clause needs separate handling.

## Status definitions

| Status | Meaning | Effort to ship |
|---|---|---|
| **Covered** | Rule exists, matches this phrasing (with allowance for `{rwy}` / `{alt}` / etc. captures). | None — already done. |
| **MissingRule** | Canonical command exists in `CommandRegistry`; just no rule produces it from this phrasing. | One PR: rule + test + verbalizer regression check. |
| **MissingCanonical** | No existing canonical command supports this intent. New `ParsedCommand` record + dispatcher + verbalizer + rule needed. | Medium-large PR. Often defer. |
| **OutOfScope** | Section is about workflow, equipment, charting, etc. — not phraseology. | N/A. |

---

## 7110.65 — Controller-side

### Chapter 3 — Airport Traffic Control

#### §3-1 — General

##### OutOfScope
- **Phrasing:** "CROSS (runway) AT (point/intersection)."
  **Canonical:** —
  **Notes:** §3-1-3 inter-controller coordination (ground↔local), not pilot-controller phraseology.
- **Phrasing:** "PROCEED AS REQUESTED; (additional instructions or information)."
  **Canonical:** —
  **Notes:** §3-1-5 directed at vehicles/equipment/personnel in RSA, not aircraft.
- **Phrasing:** "Mower left of runway 27." / "Trucks crossing approach end of runway 25." / "Workman on taxiway Bravo." / "Aircraft left of runway 18."
  **Canonical:** —
  **Notes:** §3-1-6 traffic-advisory broadcast — controller-originated narration, not a pilot-to-controller utterance.
- **Phrasing:** "LOW LEVEL WIND SHEAR (or MICROBURST) ADVISORIES IN EFFECT."
  **Canonical:** —
  **Notes:** §3-1-8 controller broadcast/ATIS.
- **Phrasing:** "WIND SHEAR ALERT. AIRPORT WIND (direction) AT (velocity). (Location) BOUNDARY WIND (direction) AT (velocity)."
  **Canonical:** —
  **Notes:** §3-1-8 LLWAS controller broadcast.
- **Phrasing:** "WIND SHEAR ALERTS TWO/SEVERAL/ALL QUADRANTS. AIRPORT WIND (direction) AT (velocity). (Location) BOUNDARY WIND (direction) AT (velocity)."
  **Canonical:** —
  **Notes:** §3-1-8 LLWAS controller broadcast.
- **Phrasing:** "(Runway) (arrival/departure) WIND SHEAR/MICROBURST ALERT, (windspeed) KNOT GAIN/LOSS, (location)."
  **Canonical:** —
  **Notes:** §3-1-8 TDWR/WSP ribbon-display controller broadcast.
- **Phrasing:** "(Runway) DEPARTURE/THRESHOLD WIND (direction) AT (velocity)."
  **Canonical:** —
  **Notes:** §3-1-8 wind-info broadcast on pilot request, not a routed pilot intent.
- **Phrasing:** "(Appropriate wind or alert information) POSSIBLE WIND SHEAR OUTSIDE THE NETWORK."
  **Canonical:** —
  **Notes:** §3-1-8 controller broadcast.
- **Phrasing:** "MULTIPLE WIND SHEAR/MICROBURST ALERTS (specific alert or wind information)."
  **Canonical:** —
  **Notes:** §3-1-8 controller broadcast.
- **Phrasing:** "(Identification), PROCEED (direction)-BOUND, (other instructions or information)." / "(identification), SUGGESTED HEADING (degrees), (other instructions)."
  **Canonical:** —
  **Notes:** §3-1-9 advisory headings via uncertified tower radar — emergency/lost-VFR aid bordering on lost-comm; out of scope per audit plan.
- **Phrasing:** "(Item) APPEAR/S (observed condition)." ("Landing gear appears down and in place." etc.)
  **Canonical:** —
  **Notes:** §3-1-10 controller broadcast describing observed abnormality.
- **Phrasing:** "(A/c call sign) REMAIN OUTSIDE DELTA AIRSPACE AND STANDBY."
  **Canonical:** —
  **Notes:** §3-1-13 Class D entry hold-off; no canonical for "remain outside surface area"; sim does not model Class D entry boundary.
- **Phrasing:** "(Identification) TAXI TO (ramp, gate, or alternate deplaning area) VIA (route)."
  **Canonical:** —
  **Notes:** §3-1-15 Three/Four-Hour Tarmac Rule — taxi-to-gate workflow; no canonical for taxi-to-gate.
- **Phrasing:** "(Identification) EXPECT A (number) MINUTE DELAY DUE TO (ground and/or landing and/or departing) TRAFFIC."
  **Canonical:** —
  **Notes:** §3-1-15 Tarmac Rule delay advisory — controller workflow.
- **Phrasing:** "(Identification) UNABLE DUE TO OPERATIONAL DISRUPTION."
  **Canonical:** —
  **Notes:** §3-1-15 Tarmac Rule unable response — controller-originated unable.

#### §3-2 — Visual Signals

##### OutOfScope
- **Phrasing:** Light-gun signals (steady/flashing green/red/white, alternating red/green) per TBL 3-2-1.
  **Canonical:** —
  **Notes:** §3-2-1 through §3-2-3 cover non-radio visual signals for NORDO/receiver-only aircraft — out of scope (lost-comm) per audit plan.

#### §3-3 — Airport Conditions

##### OutOfScope
- **Phrasing:** "DISABLED AIRCRAFT ON RUNWAY."
  **Canonical:** —
  **Notes:** §3-3-1 NOTAM text, not a pilot-controller utterance.
- **Phrasing:** "Runway 27, condition codes 2, 2, 3 at 1018Z." / "Runway (number) condition codes 2, 3, 1."
  **Canonical:** —
  **Notes:** §3-3-1 RwyCC ATIS / verbal broadcast — controller-originated advisory.
- **Phrasing:** "All runways covered by compacted snow 6 inches deep."
  **Canonical:** —
  **Notes:** §3-3-1 controller-originated surface-condition advisory.
- **Phrasing:** "RUNWAY (runway number) CLOSED/UNSAFE. … UNABLE TO ISSUE DEPARTURE/LANDING/TOUCH-AND-GO CLEARANCE. DEPARTURE/LANDING/TOUCH-AND-GO WILL BE AT YOUR OWN RISK."
  **Canonical:** —
  **Notes:** §3-3-2 controller-originated unable/own-risk for closed runway.
- **Phrasing:** "Braking action medium, reported by a heavy Boeing 767." / "Braking action poor first half of runway, reported by a Boeing 757."
  **Canonical:** —
  **Notes:** §3-3-4 controller-originated braking-action advisory; pilot-originated braking PIREP is informational.
- **Phrasing:** "Ice on runway, RCR 05, patchy."
  **Canonical:** —
  **Notes:** §3-3-4 runway surface condition / RCR advisory.
- **Phrasing:** "NO BRAKING ACTION REPORTS RECEIVED FOR RUNWAY (runway number)."
  **Canonical:** —
  **Notes:** §3-3-5 controller-originated advisory.
- **Phrasing:** "BARRIER - BARRIER - BARRIER" / "CABLE - CABLE - CABLE"
  **Canonical:** —
  **Notes:** §3-3-6 emergency pilot request for arresting-system engagement — out of scope per audit plan.
- **Phrasing:** "YOUR DEPARTURE/LANDING WILL BE TOWARD/OVER A RAISED BARRIER/CABLE ON RUNWAY (number), (location, distance)."
  **Canonical:** —
  **Notes:** §3-3-6 controller advisory about raised arresting gear; military/USAF context not modeled.
- **Phrasing:** "Runway 14 arresting cable 1000 feet from threshold."
  **Canonical:** —
  **Notes:** §3-3-6 controller advisory.
- **Phrasing:** "(Identification), BARRIER/CABLE INDICATES UP/DOWN. CLEARED FOR TAKEOFF/TO LAND."
  **Canonical:** —
  **Notes:** §3-3-6 — CTO/CLAND portion already covered (`PhraseologyRules.cs:140-152`); barrier/cable preamble is military-specific narration.
- **Phrasing:** "CAUTION, MONITOR INDICATES RUNWAY (number) LOCALIZER UNRELIABLE."
  **Canonical:** —
  **Notes:** §3-3-7 FFM controller broadcast — safety advisory, no pilot intent.

#### §3-4 — Airport Lighting

##### OutOfScope
- **Phrasing:** Entire section (3-4-1 through 3-4-19): REIL, VASI, PAPI, ALS, SFL, MALSR/ODALS, ALSF-2/SSALR, runway/taxiway edge lights, HIRL/RCLS/TDZL, MIRL, high-speed turnoff lights, obstruction lights, rotating beacon, RWSL.
  **Canonical:** —
  **Notes:** Equipment-operation rules (when to turn lighting on/off and at what intensity). No PHRASEOLOGY- blocks; no controller-to-pilot utterances to map.

#### §3-5 — Runway Selection

##### OutOfScope
- **Phrasing:** Entire section (3-5-1 SELECTION, 3-5-2 STOL RUNWAYS, 3-5-3 TAILWIND COMPONENTS).
  **Canonical:** —
  **Notes:** Controller workflow rules for designating active/duty runway and reporting tailwind components. Pilot-side runway assignment is conveyed via taxi/takeoff/landing clearances already covered elsewhere.

#### §3-6 — Airport Surface Detection Procedures

##### MissingRule
- **Phrasing:** "TURN (left/right) ON THE TAXIWAY/RUNWAY YOU ARE APPROACHING."
  **Canonical:** `??` (closest existing: `ExitLeft` / `ExitRight`)
  **Notes:** §3-6-3a6 directional taxi guidance from ASDE. `ExitLeft`/`ExitRight` semantics are runway-exit-during-rollout, not "turn at the next taxiway you're approaching while taxiing." Likely needs a new canonical or extension of exit semantics; defer to product.

##### OutOfScope
- **Phrasing:** §3-6-1 EQUIPMENT USAGE, §3-6-2 IDENTIFICATION, §3-6-4 SAFETY LOGIC ALERT RESPONSES.
  **Canonical:** —
  **Notes:** ASDE equipment operation, target identification workflow, and controller decision rules. Go-around issuance on safety alert is covered by existing `GoAround` canonical; the alert-response trigger is controller workflow. Track-drop / false-target handling is out-of-pilot-scope (ASDE-X UI mutations).

#### §3-7 — Taxi and Ground Movement Procedures

##### Covered
- **Phrasing:** "TAXI/CONTINUE TAXIING/PROCEED VIA (route)"
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:444` covers `taxi via {path...}`; "continue/proceed" wording variants in MissingRule below.
- **Phrasing:** "RUNWAY (number), TAXI VIA (route)"
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:446`.
- **Phrasing:** "VIA (route), HOLD SHORT OF (location)"
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:449`.
- **Phrasing:** "RUNWAY (number), TAXI VIA (route), HOLD SHORT OF (location)"
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:452`.
- **Phrasing:** "RUNWAY (number), TAXI VIA (route), CROSS RUNWAY (number), HOLD SHORT OF RUNWAY (number)"
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:460, 469`.
- **Phrasing:** "HOLD POSITION"
  **Canonical:** `HoldPosition`
  **Notes:** `PhraseologyRules.cs:497`.
- **Phrasing:** "CROSS (runway)" / "CROSS RUNWAY (number)"
  **Canonical:** `CrossRunway`
  **Notes:** `PhraseologyRules.cs:500`.
- **Phrasing:** "HOLD SHORT OF (runway)" / "HOLD SHORT OF (taxiway)"
  **Canonical:** `HoldShort`
  **Notes:** `PhraseologyRules.cs:501-502`; also at §3-10, §3-11.
- **Phrasing:** "FOLLOW (traffic)" (on ground)
  **Canonical:** `FollowGround`
  **Notes:** `PhraseologyRules.cs:503` — `follow the? {callsign} on ground`.

##### MissingRule
- **Phrasing:** "CONTINUE TAXIING VIA (route)" / "PROCEED VIA (route)"
  **Canonical:** `Taxi`
  **Notes:** rule line 444 only matches literal "taxi via"; add "continue taxiing via" and "proceed via" alternates per §3-7-2 phraseology block.
- **Phrasing:** "ON (runway number or taxiways)" / "TO (location)" / "(direction)" — sub-forms enumerated in §3-7-2
  **Canonical:** `Taxi`
  **Notes:** variants not currently matched by `taxi via {path...}` (e.g. "taxi on Charlie", "taxi to the hangar", "taxi straight ahead"). Consider extending the Taxi tokenizer.
- **Phrasing:** "ACROSS RUNWAY (number), AT (runway/taxiway)" — taxi-with-cross sub-form
  **Canonical:** `CrossRunway` or `Taxi`
  **Notes:** "across" as alternate to "cross" not in rule line 500.
- **Phrasing:** "CROSS RUNWAY (number) AT (runway/taxiway)" — explicit at-intersection form
  **Canonical:** `CrossRunway`
  **Notes:** rule line 500 is bare `cross runway {rwy}`; add "at {taxiway}" tail. Same shape needed for "CROSS RUNWAY (number) AT (runway/taxiway), HOLD SHORT OF (runway)".
- **Phrasing:** "CROSS RUNWAY (number) AND RUNWAY (number) AT TAXIWAY (designator)" — multiple-runway crossing in single clearance
  **Canonical:** `CrossRunway`
  **Notes:** no multi-runway crossing form supported.
- **Phrasing:** "HOLD FOR (reason)" (e.g. wake turbulence, traffic)
  **Canonical:** `HoldPosition`
  **Notes:** extend rule line 497 to accept "hold for {reason...}" tail. Also at §3-9, §3-11.
- **Phrasing:** "HOLD SHORT OF (runway) APPROACH" / "HOLD SHORT OF (runway) DEPARTURE"
  **Canonical:** `HoldShort`
  **Notes:** approach/departure hold-area suffix not handled by rule line 501.
- **Phrasing:** "HOLD SHORT OF (runway) ILS CRITICAL AREA"
  **Canonical:** `HoldShort`
  **Notes:** same as above — ILS-critical-area suffix.
- **Phrasing:** "TAXI WITHOUT DELAY (traffic if necessary)"
  **Canonical:** `Taxi`
  **Notes:** "without delay" modifier not recognized; could ride along as ignored adverb if rules normalize it out.
- **Phrasing:** "EXIT/PROCEED/CROSS (runway/taxiway) AT (runway/taxiway) WITHOUT DELAY"
  **Canonical:** `ExitTaxiway` / `CrossRunway`
  **Notes:** "without delay" tail on exit/cross forms.
- **Phrasing:** "BEHIND (traffic)" — taxi-behind-traffic form
  **Canonical:** `FollowGround`
  **Notes:** rule line 503 is `follow ... on ground`; FAA allows bare "behind (traffic)" as alternate; add "behind {callsign}" → FollowGround.

##### MissingCanonical
- **Phrasing:** "READ BACK HOLD INSTRUCTIONS"
  **Canonical:** `??`
  **Notes:** no canonical for controller requesting hold-instruction readback; defer to product (rare pilot-side trigger).
- **Phrasing:** "(crossing instructions) LANDING TRAFFIC WILL HOLD SHORT OF THE INTERSECTION" — LAHSO traffic advisory appended to crossing clearance
  **Canonical:** `??`
  **Notes:** modifier/advisory tail on CrossRunway; no canonical for advisory-only utterance.
- **Phrasing:** "(ACID), IN THE EVENT OF MISSED APPROACH (issue traffic) / TAXIING AIRCRAFT/VEHICLE LEFT/RIGHT OF RUNWAY"
  **Canonical:** `??`
  **Notes:** POFZ/OCS missed-approach traffic advisory; advisory-only.
- **Phrasing:** "ILS CRITICAL AREA NOT PROTECTED"
  **Canonical:** `??`
  **Notes:** weather/critical-area advisory to landing aircraft.
- **Phrasing:** "RUNWAY (number) AT (taxiway designator) INTERSECTION DEPARTURE (remaining length) FEET AVAILABLE"
  **Canonical:** `??`
  **Notes:** intersection-departure runway-length advisory; defer to product.
- **Phrasing:** "CAUTION JET BLAST / ROTOR WASH / PROP WASH"
  **Canonical:** `??`
  **Notes:** cautionary advisory; defer to product.

##### OutOfScope
- **Phrasing:** §3-7-1 procedural rules on conditional/unconditional instructions, intersection-departure measurement, term "full length" usage.
  **Canonical:** —
  **Notes:** controller workflow / negative-phraseology rules.
- **Phrasing:** §3-7-3 GROUND OPERATIONS — wake/jet blast cautions embedded in taxi clearances.
  **Canonical:** —
  **Notes:** advisory text appended; underlying Taxi clearance is already covered.
- **Phrasing:** §3-7-5 PRECISION APPROACH CRITICAL AREA — DoD authority, signage/markings responsibilities.
  **Canonical:** —
  **Notes:** equipment/airport-management workflow.
- **Phrasing:** §3-7-6 POFZ/OCS clearance-protection requirements (geometric conditions).
  **Canonical:** —
  **Notes:** controller workflow rule for ensuring areas are clear.

#### §3-8 — Spacing and Sequencing

##### Covered
- **Phrasing:** "CLEARED FOR TAKEOFF"
  **Canonical:** `ClearedForTakeoff`
  **Notes:** `PhraseologyRules.cs:140-144`; runway-prefix variants shipped (recent work).
- **Phrasing:** "EXTEND DOWNWIND"
  **Canonical:** `ExtendPattern`
  **Notes:** `PhraseologyRules.cs:365`.
- **Phrasing:** "MAKE SHORT APPROACH"
  **Canonical:** `MakeShortApproach`
  **Notes:** `PhraseologyRules.cs:366`.
- **Phrasing:** "CIRCLE THE AIRPORT"
  **Canonical:** `CircleAirport`
  **Notes:** `PhraseologyRules.cs:376`.
- **Phrasing:** "MAKE LEFT/RIGHT THREE-SIXTY"
  **Canonical:** `MakeLeft360` / `MakeRight360`
  **Notes:** `PhraseologyRules.cs:369-372`.
- **Phrasing:** "MAKE LEFT/RIGHT TWO SEVENTY"
  **Canonical:** `MakeLeft270` / `MakeRight270`
  **Notes:** `PhraseologyRules.cs:373-374`.
- **Phrasing:** "GO AROUND (additional instructions as necessary)"
  **Canonical:** `GoAround`
  **Notes:** `PhraseologyRules.cs:161-164`.
- **Phrasing:** "CLEARED TO LAND"
  **Canonical:** `ClearedToLand`
  **Notes:** `PhraseologyRules.cs:148-152`.
- **Phrasing:** "CLEARED TOUCH-AND-GO"
  **Canonical:** `TouchAndGo`
  **Notes:** `PhraseologyRules.cs:167-172`.
- **Phrasing:** "CLEARED STOP-AND-GO"
  **Canonical:** `StopAndGo`
  **Notes:** `PhraseologyRules.cs:173-175`.
- **Phrasing:** "CLEARED LOW APPROACH"
  **Canonical:** `LowApproach`
  **Notes:** `PhraseologyRules.cs:176-177`.
- **Phrasing:** "CLEARED FOR THE OPTION"
  **Canonical:** `ClearedForOption`
  **Notes:** `PhraseologyRules.cs:178-179`.

##### MissingRule
- **Phrasing:** "CLEARED FOR TAKEOFF OR HOLD SHORT / HOLD IN POSITION / TAXI OFF THE RUNWAY (traffic)" — compound conditional takeoff clearance
  **Canonical:** `ClearedForTakeoff` (compound with `HoldShort`/`LineUpAndWait`)
  **Notes:** no compound-conditional form exists; the "or" branch may be too unusual to model directly.
- **Phrasing:** "OPTION APPROVED"
  **Canonical:** `ClearedForOption`
  **Notes:** rule lines 178-179 require literal "cleared for the option"/"cleared for option"; add "option approved" alternate.

##### MissingCanonical
- **Phrasing:** "NUMBER (landing sequence number)" — sequence call (e.g. "number two, follow Cessna…")
  **Canonical:** `??`
  **Notes:** no `LandingSequence` canonical; often coupled with Follow.
- **Phrasing:** "TRAFFIC (description and location) LANDING RUNWAY (number)" / "LANDING THE PARALLEL RUNWAY"
  **Canonical:** `??`
  **Notes:** traffic advisory referencing other runway; no canonical for free traffic call.
- **Phrasing:** "UNABLE OPTION, (alternate instructions)" / "UNABLE (type of option), OTHER OPTIONS APPROVED"
  **Canonical:** `??`
  **Notes:** negative-clearance / partial-option-denial; no canonical for "unable X". Defer.
- **Phrasing:** "TRAFFIC (description) ARRIVING/DEPARTING/LOW APPROACH, OPPOSITE DIRECTION ON PARALLEL RUNWAY/LANDING STRIP"
  **Canonical:** `??`
  **Notes:** traffic advisory.

##### OutOfScope
- **Phrasing:** §3-8-2 status definitions (touch-and-go/stop-and-go/low approach considered arrival until X, then departure).
  **Canonical:** —
  **Notes:** controller workflow / classification rule.
- **Phrasing:** §3-8-3 / §3-8-4 simultaneous same/opposite direction parallel-runway distance minima (TBL 3-8-1, TBL 3-8-2).
  **Canonical:** —
  **Notes:** separation criteria / authorization rules.

#### §3-9 — Departure Procedures and Separation

##### Covered
- **Phrasing:** "RUNWAY (number), LINE UP AND WAIT" (§3-9-4)
  **Canonical:** `LineUpAndWait`
  **Notes:** `PhraseologyRules.cs:130-134`; recent work shipped runway-prefix form.
- **Phrasing:** "RUNWAY (number) AT (taxiway designator), LINE UP AND WAIT" (§3-9-4 sub 13)
  **Canonical:** `LineUpAndWait`
  **Notes:** intersection-LUAW runway-prefix form — covered per shipped work.
- **Phrasing:** "DEPARTURE FREQUENCY (frequency), SQUAWK (code)" — squawk component
  **Canonical:** `Squawk`
  **Notes:** `PhraseologyRules.cs:406`.
- **Phrasing:** "RUNWAY (number), CLEARED FOR TAKEOFF" (§3-9-10)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** `PhraseologyRules.cs:140-144` — covered per shipped runway-prefix work.
- **Phrasing:** "RUNWAY (number) AT (taxiway designator) CLEARED FOR TAKEOFF" (§3-9-10 sub 2)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** intersection takeoff — covered per shipped CTO runway-prefix work.
- **Phrasing:** "CANCEL TAKEOFF CLEARANCE" (§3-9-11)
  **Canonical:** `CancelTakeoffClearance`
  **Notes:** `PhraseologyRules.cs:145`.
- **Phrasing:** "RUNWAY (number) SHORTENED, LINE UP AND WAIT" (§3-9-4 sub 16)
  **Canonical:** `LineUpAndWait`
  **Notes:** "shortened" wedge silently consumed by greedy matcher's no-match advance; bare LUAW fires. Sim doesn't model runway distance available. Test: `TowerModifierWedges_Rules`.
- **Phrasing:** "RUNWAY (number) SHORTENED, CLEARED FOR TAKEOFF" (§3-9-10 sub 7)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** "shortened" wedge silent skip; bare CTO fires.
- **Phrasing:** "RUNWAY (number) AT (taxiway designator) INTERSECTION DEPARTURE SHORTENED, CLEARED FOR TAKEOFF" (§3-9-10 sub 7)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** intersection + shortened wedges silent skip; bare CTO fires. Test: `TowerModifierWedges_Rules`.
- **Phrasing:** "RUNWAY (number), FULL-LENGTH, LINE UP AND WAIT" (§3-9-4 sub 14)
  **Canonical:** `LineUpAndWait`
  **Notes:** "full length" wedge silent skip; bare LUAW fires.
- **Phrasing:** "RUNWAY (number), FULL LENGTH, CLEARED FOR TAKEOFF" (§3-9-10 sub 3)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** "full length" wedge silent skip; bare CTO fires.
- **Phrasing:** "RUNWAY (number), WIND (direction velocity), CLEARED FOR TAKEOFF" (§3-9-10 sub 9, USA/USN/USAF)
  **Canonical:** `ClearedForTakeoff`
  **Notes:** wind-advisory wedge silent skip; bare CTO fires.

##### MissingRule
- **Phrasing:** "CROSS RUNWAY (number), RUNWAY (number) CLEARED FOR TAKEOFF" (§3-9-10 sub 5)
  **Canonical:** `CrossRunway` + `ClearedForTakeoff` (compound)
  **Notes:** compound cross-then-takeoff in single utterance.
- **Phrasing:** "RUNWAY (number), CONTINUE, TRAFFIC HOLDING IN POSITION" (§3-9-4 sub 3a)
  **Canonical:** `??` / possibly `Resume`
  **Notes:** "continue" as a pattern-continue instruction with traffic advisory; see "CONTINUE" entry under §3-10 MissingCanonical.
- **Phrasing:** "CONTINUE HOLDING" (§3-9-4 sub 9 / sub 12)
  **Canonical:** `HoldPosition`
  **Notes:** explicit hold-continue not matched by rule line 497; add `continue holding` alternate.
- **Phrasing:** "TAXI OFF THE RUNWAY" (§3-9-4 sub 9 / sub 12)
  **Canonical:** `??`
  **Notes:** no canonical for "vacate runway from LUAW"; closest is `ExitTaxiway` but no taxiway named. Defer.
- **Phrasing:** "HOLD FOR WAKE TURBULENCE" (§3-9-6 sub l / §3-9-7 sub a4)
  **Canonical:** `HoldPosition`
  **Notes:** "hold for" variant; also at §3-7 and §3-11.
- **Phrasing:** "RUNWAY (NUMBER) SHORTENED" — standalone advisory (§3-9-1 sub 10)
  **Canonical:** `??`
  **Notes:** standalone advisory unaccompanied by clearance.

##### MissingCanonical
- **Phrasing:** "CHANGE TO DEPARTURE" (§3-9-3 sub a2)
  **Canonical:** `??`
  **Notes:** USAF/military frequency hand-off; partially overlaps with `Contact` but lacks frequency. Defer to product.
- **Phrasing:** "GATE HOLD PROCEDURES ARE IN EFFECT. ALL AIRCRAFT CONTACT (position) ON (frequency) FOR ENGINE START TIME. EXPECT ENGINE START/TAXI (time)" (§3-9-2 sub 1)
  **Canonical:** `??`
  **Notes:** broadcast/IROPS — gate-hold info.
- **Phrasing:** "START ENGINES, ADVISE WHEN READY TO TAXI" / "ADVISE WHEN READY TO TAXI" (§3-9-2 sub 2)
  **Canonical:** `??`
  **Notes:** gate-hold sequencing.
- **Phrasing:** "GATE HOLD PROCEDURES NO LONGER IN EFFECT" (§3-9-2 sub 4)
  **Canonical:** `??`
  **Notes:** broadcast/IROPS termination.
- **Phrasing:** Traffic-information forms in §3-9-4 sub 4/10 — "TRAFFIC A BOEING 737, SIX MILE FINAL" / "TRAFFIC HOLDING RUNWAY (number)" / "TRAFFIC LANDING RUNWAY (number)" / "TRAFFIC HOLDING IN POSITION RUNWAY (number)" / "TRAFFIC DEPARTING RUNWAY (number)"
  **Canonical:** `??`
  **Notes:** free-form traffic advisory appended to LUAW/CTO/CTL; no canonical for traffic call.

##### OutOfScope
- **Phrasing:** §3-9-1 sub 1-9 — ATIS / current settings / braking action / runway condition codes / surface wind issuance / density-altitude advisory dissemination workflow.
  **Canonical:** —
  **Notes:** information-dissemination workflow.
- **Phrasing:** §3-9-3 sub b — "When the aircraft is about 1/2 mile beyond the runway end, instruct civil aircraft to contact departure".
  **Canonical:** —
  **Notes:** procedural rule. Contact-departure utterance is covered by `Contact` (out of pilot scope per index).
- **Phrasing:** §3-9-4 procedural restrictions (subs 1, 3-12, 15) — visibility/intersection/safety-logic rules, holding-position management, USA/USN/USAF qualifiers.
  **Canonical:** —
  **Notes:** controller workflow.
- **Phrasing:** §3-9-5 ANTICIPATING SEPARATION rule.
  **Canonical:** —
  **Notes:** controller judgment rule, no phraseology block.
- **Phrasing:** §3-9-6 / §3-9-7 / §3-9-8 / §3-9-9 — same-runway / wake-turbulence / intersecting / converging runway separation criteria.
  **Canonical:** —
  **Notes:** separation-criteria workflow rules. Only embedded phraseology is "HOLD FOR WAKE TURBULENCE" (flagged) and traffic advisories (flagged).
- **Phrasing:** §3-9-10 sub 4 / sub 6-8 — runway-crossing pre-takeoff rule, "full length" usage restriction, USAF traffic-info-on-takeoff.
  **Canonical:** —
  **Notes:** controller workflow / qualifier rules.

#### §3-10 — Arrival Procedures and Separation

##### Covered
- **Phrasing:** "ENTER LEFT/RIGHT BASE"
  **Canonical:** `EnterLeftBase` / `EnterRightBase`
  **Notes:** `PhraseologyRules.cs:331-336`.
- **Phrasing:** "MAKE STRAIGHT-IN" / "STRAIGHT-IN"
  **Canonical:** `EnterFinal`
  **Notes:** `PhraseologyRules.cs:343-352`; "Recent work" EF coverage.
- **Phrasing:** "MAKE RIGHT/LEFT TRAFFIC"
  **Canonical:** `MakeLeftTraffic` / `MakeRightTraffic`
  **Notes:** `PhraseologyRules.cs:353-358`.
- **Phrasing:** "RUNWAY (number) CLEARED TO LAND" (incl. prefix and suffix word order)
  **Canonical:** `ClearedToLand`
  **Notes:** `PhraseologyRules.cs:148-152`; "Recent work" CLAND runway-prefix.
- **Phrasing:** "RUNWAY (number) CLEARED TO LAND, HOLD SHORT OF RUNWAY (number)" (LAHSO)
  **Canonical:** `LandAndHoldShort`
  **Notes:** `PhraseologyRules.cs:157-159`; "Recent work" LAHSO runway-prefix.
- **Phrasing:** "GO-AROUND" (with optional wrong-runway/wrong-surface context)
  **Canonical:** `GoAround`
  **Notes:** `PhraseologyRules.cs:161-164`.
- **Phrasing:** "TURN LEFT/RIGHT (taxiway/runway)" (runway-exiting instruction)
  **Canonical:** `ExitLeft` / `ExitRight`
  **Notes:** `PhraseologyRules.cs:505-508`.
- **Phrasing:** "RUNWAY (number) SHORTENED, CLEARED TO LAND"
  **Canonical:** `ClearedToLand`
  **Notes:** "shortened" wedge silent skip; bare CLAND fires. Test: `TowerModifierWedges_Rules`. ("RUNWAY (number) SHORTENED, CONTINUE" form still uncovered — see CONTINUE MissingCanonical.)
- **Phrasing:** "CHANGE TO RUNWAY (number), RUNWAY (number) CLEARED TO LAND" (landing-runway change)
  **Canonical:** `ClearedToLand`
  **Notes:** "change to runway X" preamble silent skip; runway-prefix CLAND rule fires on the second "runway X cleared to land". Test: `TowerModifierWedges_Rules`.
- **Phrasing:** "NOT IN SIGHT, RUNWAY (number) CLEARED TO LAND"
  **Canonical:** `ClearedToLand`
  **Notes:** "not in sight" preamble silent skip; runway-prefix CLAND rule fires.
- **Phrasing:** "RUNWAY (number), WIND (dir/vel), CLEARED TO LAND" (USA/USN/USAF mandated wind+CTL)
  **Canonical:** `ClearedToLand`
  **Notes:** wind wedge silent skip; bare CLAND fires. Test: `TowerModifierWedges_Rules`.

##### MissingRule
- **Phrasing:** "STRAIGHT-IN APPROVED" / "RIGHT TRAFFIC APPROVED" (pattern-entry approval shorthand)
  **Canonical:** `EnterFinal` / `MakeRightTraffic`
  **Notes:** existing rules accept "make/enter straight-in" and "make right traffic" but not the "(direction) APPROVED" form from §3-10-1.
- **Phrasing:** "TURN LEFT/RIGHT (taxiway), CROSS (runway), CONTACT GROUND (freq)" (compound exit+cross+handoff)
  **Canonical:** `ExitLeft`/`ExitRight` + `CrossRunway` + `Contact`
  **Notes:** ExitLeft/Right rules don't compose with the trailing "cross runway X" / "contact ground" segments in one utterance.
- **Phrasing:** "IF ABLE, TURN LEFT/RIGHT (taxiway/runway)" (conditional exit)
  **Canonical:** `ExitLeft` / `ExitRight`
  **Notes:** existing rules don't tolerate the "if able" preamble (§3-10-9).
- **Phrasing:** "CLEARED LOW APPROACH AT OR ABOVE (altitude)" (altitude-restricted low approach §3-10-10)
  **Canonical:** `LowApproach`
  **Notes:** existing LowApproach rules don't carry an altitude restriction; would need an {alt?} slot.
- **Phrasing:** "LEFT/RIGHT CLOSED TRAFFIC APPROVED" (§3-10-11)
  **Canonical:** `ClearedForOption` (closest functional analog) or `??`
  **Notes:** "Closed traffic" = successive operations in pattern; no rule recognizes "closed traffic approved". Defer to product whether it should map to ClearedForOption / MakeLeftTraffic+MakeRightTraffic / a new canonical.
- **Phrasing:** "UNABLE CLOSED TRAFFIC" (denial)
  **Canonical:** `??`
  **Notes:** denial phrasing; no canonical for ATC negative responses.

##### MissingCanonical
- **Phrasing:** "CONTINUE" (standalone withheld-landing-clearance instruction; distinct from "cleared to land")
  **Canonical:** `??`
  **Notes:** §3-10-1 / §3-10-5f / §3-10-9 use "continue" to mean "proceed inbound, expect clearance later." Important controller verb — defer to product. Also referenced in §3-9 MissingRule.
- **Phrasing:** "REPORT (distance) MILE FINAL" / "REPORT ONE MILE FINAL" (succeeding-aircraft position report request)
  **Canonical:** `??`
  **Notes:** YAAT has `SayPosition` but no canonical for "report N-mile final"; consider `ReportFinal{dist}` or generalized `ReportAtPoint`.
- **Phrasing:** "EXPECT LANDING CLEARANCE (distance) MILE FINAL"
  **Canonical:** `??`
  **Notes:** §3-10-1 NOTE: anticipate-clearance phrasing; no canonical for forward-looking landing-clearance heads-up.
- **Phrasing:** "PATTERN ALTITUDE (altitude). RIGHT TURNS." (overhead maneuver §3-10-12)
  **Canonical:** `??`
  **Notes:** military overhead-maneuver phrasing; combines pattern altitude + pattern direction.
- **Phrasing:** "REPORT INITIAL" / "REPORT BREAK" / "BREAK AT (point)" (overhead maneuver §3-10-12)
  **Canonical:** `??`
  **Notes:** overhead-maneuver-specific position reports.
- **Phrasing:** "REPORT (HIGH/LOW) KEY" / "REPORT LOW KEY" / "REPORT (distance) MILE SIMULATED FLAMEOUT FINAL" (§3-10-13)
  **Canonical:** `??`
  **Notes:** military SFO/ELP-specific; likely out of YAAT scope.
- **Phrasing:** "VERIFY YOU ARE ALIGNED WITH RUNWAY (number)" (wrong-surface verification §3-10-5d)
  **Canonical:** `??`
  **Notes:** verification query to pilot.

##### OutOfScope
- **Phrasing:** Wake turbulence advisories ("CAUTION WAKE TURBULENCE, heavy Boeing 747...")
  **Canonical:** —
  **Notes:** advisory text appended to other clearances; YAAT does not currently model controller-issued wake advisories as standalone canonical.
- **Phrasing:** Traffic information ("TRAFFIC, (type) (action)" appended to clearances)
  **Canonical:** —
  **Notes:** advisory text; not a standalone phrasing.
- **Phrasing:** "Number two, follow Boeing 757 on 2-mile final"
  **Canonical:** `Follow`
  **Notes:** sequencing portion covered by `PhraseologyRules.cs:220-221`; "number two" preamble is informational.
- **Phrasing:** "READ BACK HOLD SHORT INSTRUCTIONS"
  **Canonical:** —
  **Notes:** controller-to-pilot meta instruction (request readback); not a sim-controllable action.

#### §3-11 — Helicopter Operations

##### Covered
- **Phrasing:** "AIR-TAXI VIA (route) TO (location)" / "CLEARED FOR AIR TAXI"
  **Canonical:** `AirTaxi`
  **Notes:** `PhraseologyRules.cs:395-397`.
- **Phrasing:** "MAKE APPROACH STRAIGHT-IN" (helicopter landing clearance §3-11-6)
  **Canonical:** `EnterFinal`
  **Notes:** `PhraseologyRules.cs:344, 350` ("make straight in approach") covers the form.
- **Phrasing:** "HOLD SHORT OF (active runway/extended runway centerline/other)"
  **Canonical:** `HoldShort`
  **Notes:** `PhraseologyRules.cs:501-502`.
- **Phrasing:** "CLEARED TO LAND" (helicopter, §3-11-6)
  **Canonical:** `ClearedToLand`
  **Notes:** `PhraseologyRules.cs:148-152`.
- **Phrasing:** "CLEARED FOR TAKEOFF" / "CLEARED FOR TAKEOFF PRESENT POSITION" (helicopter, §3-11-2)
  **Canonical:** `ClearedForTakeoff` / `ClearedTakeoffPresent`
  **Notes:** `PhraseologyRules.cs:140-144, 398-399`.

##### MissingRule
- **Phrasing:** "HOVER-TAXI (supplemented per 3-7-2)" (§3-11-1.b)
  **Canonical:** `AirTaxi` (closest) or `Taxi`
  **Notes:** "hover-taxi" is functionally distinct from "air-taxi" per §3-11-1 (below 20 kt ground-effect vs. expedited >20 kt). Could route to `AirTaxi` via alias or warrant its own canonical.
- **Phrasing:** "MAKE APPROACH CIRCLING LEFT/RIGHT TURN TO (location)" (§3-11-6)
  **Canonical:** `MakeLeftTraffic` / `MakeRightTraffic` (closest)
  **Notes:** "circling left/right turn to" form not matched by existing pattern rules.
- **Phrasing:** "(Present position/taxiway/helipad/numbers) MAKE RIGHT/LEFT TURN FOR (direction) DEPARTURE" (§3-11-2.a)
  **Canonical:** `ClearedTakeoffPresent` (closest) + departure-turn instruction
  **Notes:** existing rules cover "cleared for takeoff present position" but not the compound "(position) make (turn) for (direction) departure".
- **Phrasing:** "REMAIN (direction/distance) OF/FROM (runway/centerline/other)" (helicopter separation §3-11-6)
  **Canonical:** `??`
  **Notes:** spatial-restriction instruction.
- **Phrasing:** "REMAIN (direction) OF (runways/parking/terminals)" (§3-11-2.a)
  **Canonical:** `??`
  **Notes:** same as above; defer.
- **Phrasing:** "REMAIN AT OR BELOW (altitude)" (helicopter air-taxi cap, §3-11-1.c)
  **Canonical:** `??` (closest: `TemporaryAltitude` is out-of-pilot-scope)
  **Notes:** altitude-cap restriction; YAAT has `ClimbMaintain`/`DescendMaintain` but no "remain at or below" ceiling clause.

##### MissingCanonical
- **Phrasing:** "AVOID (aircraft/vehicles/personnel)" (helicopter air-taxi caution §3-11-1.c, takeoff §3-11-2.a)
  **Canonical:** `??`
  **Notes:** explicit avoidance instruction.
- **Phrasing:** "CAUTION (dust/blowing snow/loose debris/power lines/unlighted obstructions/trees/wake turbulence)"
  **Canonical:** `??`
  **Notes:** generic caution prefix; YAAT does not model controller cautions as canonical.
- **Phrasing:** "LAND AND CONTACT TOWER" (helicopter air-taxi terminus §3-11-1.c)
  **Canonical:** `??`
  **Notes:** compound "land then handoff".
- **Phrasing:** "HOLD FOR (reason — takeoff clearance, release, landing/taxiing aircraft)"
  **Canonical:** `HoldPosition`?
  **Notes:** rule line 497 is "hold position" only; "hold for (reason)" extends with a justification clause. Also at §3-7, §3-9.
- **Phrasing:** "DEPARTURE FROM (location) WILL BE AT YOUR OWN RISK" (§3-11-2.b)
  **Canonical:** `??`
  **Notes:** off-airport/non-movement-area authorization.
- **Phrasing:** "LANDING AT (location) WILL BE AT YOUR OWN RISK" (§3-11-6.b)
  **Canonical:** `??`
  **Notes:** same as above for landing.

##### OutOfScope
- **Phrasing:** Helicopter same/cross departure separation rules (§3-11-3, §3-11-4, §3-11-5).
  **Canonical:** —
  **Notes:** separation procedures, not phraseology.

#### §3-12 — Sea Lane Operations

##### OutOfScope
- **Phrasing:** All of §3-12-1 / §3-12-2 / §3-12-3 (sea lane application + departure/arrival separation).
  **Canonical:** —
  **Notes:** entirely separation procedures (Category I/II/III distance minima for float planes in sea lanes); no PHRASEOLOGY- blocks. Sea-lane operations not in YAAT scope.

**Ch 3 totals:** Covered 49 · MissingRule 32 · MissingCanonical 28 · OutOfScope 50 · Phrasings 159

### Chapter 4 — IFR (TRACON / approach control)

#### §4-1 — NAVAID Use Limitations

##### OutOfScope
- **Phrasing:** NAVAID altitude/distance service-volume tables (VOR/VORTAC/TACAN, L/MF RBN, ILS usable height/distance).
  **Canonical:** —
  **Notes:** controller-internal planning constraint, no spoken phraseology.
- **Phrasing:** Routing exceptions (radar-monitored random routes, MTR requests, frequency-management approval).
  **Canonical:** —
  **Notes:** controller workflow rule.
- **Phrasing:** Crossing-altitude / VFR-on-top route selection / fix-use rules (published vs unpublished fixes, divergence angles, TACAN-only restrictions).
  **Canonical:** —
  **Notes:** controller workflow rule.

#### §4-2 — Clearances

##### MissingRule
- **Phrasing:** "CLEARED DIRECT (destination) AIRPORT"
  **Canonical:** `DirectTo`
  **Notes:** existing DirectTo rules (`PhraseologyRules.cs:114-116`) target a fix capture; airport-as-destination form not covered.
- **Phrasing:** "(Call sign) IFR CANCELLATION RECEIVED"
  **Canonical:** `CancelIfr`
  **Notes:** `CancelIfr` is in the enum but listed as out-of-pilot-scope (data ops) in index. This is the controller-spoken acknowledgement of a pilot's cancel request; defer to product on whether STT should accept it.

##### MissingCanonical
- **Phrasing:** "CLEARED TO (destination) AIRPORT" / "CLEARED TO (destination) AIRPORT AS FILED" / "CLEARED TO (NAVAID name and type)" / "CLEARED TO (intersection or waypoint name and type)"
  **Canonical:** `??`
  **Notes:** full IFR route clearance to a clearance limit; controller-issued pre-departure clearance not currently modeled. YAAT scenarios spawn aircraft pre-cleared. Defer (may be permanent OutOfScope).
- **Phrasing:** "CHANGE (portion of route) TO READ (new portion of route)" / "(Amendment to route), REST OF ROUTE UNCHANGED"
  **Canonical:** `??`
  **Notes:** no canonical for partial route amendment; `ChangeDestination` exists but only handles destination swap, not mid-route segment edits.
- **Phrasing:** "CLEARED THROUGH (airport) TO (fix)"
  **Canonical:** `??`
  **Notes:** through-clearance via intermediate airport stops.
- **Phrasing:** "VIA APPROVED ALTITUDE RESERVATION (mission name) FLIGHT PLAN"
  **Canonical:** `??`
  **Notes:** ALTRV clearance phrasing; military/special-ops, low priority.
- **Phrasing:** "(Aircraft call sign), ARE YOU ABLE TO MAINTAIN YOUR OWN TERRAIN AND OBSTRUCTION CLEARANCE UNTIL REACHING (altitude)"
  **Canonical:** `??`
  **Notes:** pop-up IFR terrain-clearance query.
- **Phrasing:** "(Call sign) REPORT CANCELLATION OF IFR ON (frequency)" / "REPORT CANCELLATION OF IFR THIS FREQUENCY OR WITH (FSS / facility)"
  **Canonical:** `??`
  **Notes:** Report* family exists (ReportFieldInSight/ReportTrafficInSight) but not for IFR cancellation reporting.

##### OutOfScope
- **Phrasing:** "A-T-C clears / A-T-C advises / A-T-C requests …" (relay prefix via non-ATC facility).
  **Canonical:** —
  **Notes:** FSS/ARTCC FDU relay convention.
- **Phrasing:** Clearance-item ordering rules (4-2-1 list: ID → limit → SID/vectors → route → altitude → mach → holding → special info → freq/beacon).
  **Canonical:** —
  **Notes:** controller workflow / clearance-construction rule.
- **Phrasing:** "Amend altitude. Cross (fix) at or above (alt); cross (fix) at or above (alt); maintain (alt)".
  **Canonical:** —
  **Notes:** composite restatement of altitude restrictions; `CrossFix` covers per-fix constraints individually — composite is controller workflow.
- **Phrasing:** "Climb via SID except maintain (Flight Level)".
  **Canonical:** —
  **Notes:** `ClimbVia` canonical exists but "except maintain" top-altitude override is a SID restriction-management workflow not currently modeled. Also at §4-3 MissingCanonical.
- **Phrasing:** Delivery-instruction / verbatim-relay procedural rules (4-2-3, 4-2-4).
  **Canonical:** —
  **Notes:** controller workflow.
- **Phrasing:** Airfile / pop-up IFR clearance processing (4-2-9).
  **Canonical:** —
  **Notes:** controller workflow rule for processing airborne flight-plan filings.

#### §4-3 — Departure Procedures

##### Covered
- **Phrasing:** "FLY HEADING (degrees)"
  **Canonical:** `FlyHeading`
  **Notes:** `PhraseologyRules.cs:62-63`.
- **Phrasing:** "TURN LEFT" / "TURN RIGHT" (with heading)
  **Canonical:** `TurnLeft` / `TurnRight`
  **Notes:** `PhraseologyRules.cs:57-58`.
- **Phrasing:** "CLIMB AND MAINTAIN (altitude)"
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:73-74`.
- **Phrasing:** "MAINTAIN (altitude)"
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:90`.
- **Phrasing:** "DIRECT (NAVAID/waypoint/fix/airport)" (random impromptu routing, §4-3-2.d via §4-4 cross-ref)
  **Canonical:** `DirectTo`
  **Notes:** `PhraseologyRules.cs:114-116`.
- **Phrasing:** "CLIMB VIA SID"
  **Canonical:** `ClimbVia`
  **Notes:** `PhraseologyRules.cs:113`. Also at §4-5, AIM §4-4, AIM §5-2, AIM §5-5.
- **Phrasing:** "CLIMB VIA SID EXCEPT MAINTAIN (altitude)"
  **Canonical:** `ClimbVia`
  **Notes:** `PhraseologyRules.cs:114`. `ClimbViaCommand.Altitude` is the "except maintain" ceiling — handler emits "Climb via SID, except maintain {N}".

##### MissingRule
- **Phrasing:** "FLY RUNWAY HEADING"
  **Canonical:** `FlyPresentHeading` (or new `FlyRunwayHeading`)
  **Notes:** existing `FlyPresentHeading` covers "fly/maintain present heading" only. Runway heading is the published runway magnetic course at takeoff; could extend `FlyPresentHeading` to accept "fly runway heading" as near-synonym.
- **Phrasing:** "DEPART (direction or runway)" (e.g. "depart westbound")
  **Canonical:** `FlyHeading` (?)
  **Notes:** cardinal-direction departure; no cardinal-direction → heading mapping. Defer.
- **Phrasing:** "CLIMB AND MAINTAIN (altitude). EXPECT (altitude) AT (time or fix)"
  **Canonical:** `ClimbMaintain` (+ ??)
  **Notes:** primary clearance covered; expect-altitude portion not parsed — no `ExpectAltitudeAt` canonical.
- **Phrasing:** "(altitude) IS NOT AVAILABLE"
  **Canonical:** —
  **Notes:** advisory addendum to expect-altitude clearance; no canonical and no rule.

##### MissingCanonical
- **Phrasing:** "CLEARED TO (destination) AIRPORT" / "CLEARED TO (NAVAID name and type)" / "CLEARED TO (intersection or waypoint name and type)"
  **Canonical:** `??`
  **Notes:** full IFR clearance-limit issuance. Also at §4-2 MissingCanonical — same root issue.
- **Phrasing:** "DESTINATION AS FILED"
  **Canonical:** `??`
  **Notes:** AF1 phraseology.
- **Phrasing:** "WHEN ENTERING CONTROLLED AIRSPACE (instruction), FLY HEADING (degrees) UNTIL REACHING (altitude, point, or fix) BEFORE PROCEEDING ON COURSE"
  **Canonical:** `??`
  **Notes:** conditional clearance with multi-trigger ("when entering airspace…until reaching…"). YAAT compound parser supports `AT` triggers but not these.
- **Phrasing:** "FLY A (degree) BEARING/AZIMUTH FROM/TO (fix) UNTIL (time) / UNTIL REACHING (fix or altitude) / BEFORE PROCEEDING ON COURSE"
  **Canonical:** `??`
  **Notes:** bearing/azimuth-from-fix with conditional terminator.
- **Phrasing:** "(SID name and number) DEPARTURE" / "(SID name and number) DEPARTURE, (transition name) TRANSITION"
  **Canonical:** `??`
  **Notes:** SID assignment as part of clearance; YAAT scenarios pre-assign departures via flight plan.
- **Phrasing:** "(SID name and number) DEPARTURE, EXCEPT CROSS (fix) AT (revised altitude)"
  **Canonical:** `??` (partly `CrossFix`)
  **Notes:** SID amendment phrasing. `CrossFix` canonical exists but no rule (see §4-5).
- **Phrasing:** "(SID name and number) DEPARTURE. CROSS (fix) AT (altitude)"
  **Canonical:** `CrossFix` (no rule) wrapped in SID assignment
  **Notes:** bare "cross (fix) at (alt)" is MissingRule for `CrossFix` (also at §4-5, §4-7, §4-8); the encompassing SID-assignment phrasing has no canonical.
- **Phrasing:** "EXPECT FURTHER CLEARANCE VIA (airways, routes, or fixes)"
  **Canonical:** `??`
  **Notes:** EFC routing advisory. Also at §4-6 MissingCanonical.
- **Phrasing:** "CLEARED TO (destination) AIRPORT; (SID) DEPARTURE, (transition) TRANSITION; THEN AS FILED. MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** full abbreviated IFR clearance.
- **Phrasing:** "CLEARED TO (destination) AIRPORT AS FILED. MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** same without SID.
- **Phrasing:** "EXCEPT CHANGE ROUTE TO READ (amended route portion)"
  **Canonical:** `??`
  **Notes:** route amendment phrase.

##### OutOfScope
- **Phrasing:** "(aircraft identification) RELEASED" / "RELEASED FOR DEPARTURE" / "RELEASED FOR DEPARTURE AT (time)" / "RELEASED FOR DEPARTURE IN (number) MINUTES"
  **Canonical:** —
  **Notes:** inter-controller / controller-to-FSS / uncontrolled-field release coordination.
- **Phrasing:** "ADVISE (aircraft identification) RELEASED FOR DEPARTURE"
  **Canonical:** —
  **Notes:** inter-facility coordination.
- **Phrasing:** "(aircraft identification) HOLD FOR RELEASE, EXPECT (time) DEPARTURE DELAY"
  **Canonical:** —
  **Notes:** uncontrolled-field IFR release procedure.
- **Phrasing:** "IF NOT OFF BY (time), ADVISE (facility) NOT LATER THAN (time) OF INTENTIONS"
  **Canonical:** —
  **Notes:** uncontrolled-field clearance follow-on.
- **Phrasing:** "TIME (time in hours, minutes, and nearest quarter minute)"
  **Canonical:** —
  **Notes:** time check; controller-only utility.
- **Phrasing:** "CLEARANCE VOID IF NOT OFF BY (clearance void time)" / "CLEARANCE VOID IF NOT OFF IN (number) MINUTES"
  **Canonical:** —
  **Notes:** uncontrolled-field clearance void time.
- **Phrasing:** "VFR DEPARTURE AUTHORIZED. CONTACT (facility) ON (frequency) AT (location or time) FOR CLEARANCE"
  **Canonical:** —
  **Notes:** §4-3-9 inter-facility VFR-release coordination + a `Contact` handoff; broader wrapper is coordination.

#### §4-4 — Route Assignment

##### Covered
- **Phrasing:** "DIRECT (name of NAVAID/waypoint/fix/airport)" (impromptu random routing)
  **Canonical:** `DirectTo`
  **Notes:** `PhraseologyRules.cs:114-116`.
- **Phrasing:** "AFTER (fix) PROCEED DIRECT (fix)" (point-to-point routing)
  **Canonical:** `AppendDirectTo`
  **Notes:** `PhraseologyRules.cs:120` ("after {current} direct to? {fix}") — matches FAA point-to-point phraseology directly.
- **Phrasing:** "DIRECT (fix/waypoint)" (§4-4-1.j, RNAV fixes by name)
  **Canonical:** `DirectTo`
  **Notes:** `PhraseologyRules.cs:114-116`.

##### MissingRule
- **Phrasing:** "VIA VICTOR (airway number)" / "VIA J (route number)" / "VIA Q (route number)" / "VIA TANGO (route number)" / "VIA IR (route number)" (designated ATS routes)
  **Canonical:** `JoinAirway`
  **Notes:** `JoinAirway` canonical exists; no rule. Add "via (airway-id)" and "join (airway-id)" forms.
- **Phrasing:** "CROSS/JOIN VICTOR (airway number), (number) MILES (direction) OF (fix)"
  **Canonical:** `JoinAirway` (?)
  **Notes:** offset-join onto an airway at distance/direction from a fix. May need extended argument shape (offset miles + direction + reference fix).
- **Phrasing:** "VIA (name of NAVAID) RADIAL/COURSE/AZIMUTH"
  **Canonical:** `JoinRadialOutbound` / `JoinRadialInbound`
  **Notes:** both canonicals exist; no rule produces either.

##### MissingCanonical
- **Phrasing:** "SUBSTITUTE (ATS route) FROM (fix) TO (fix)"
  **Canonical:** `??`
  **Notes:** §4-4-1.a / §4-4-4 NAVAID-outage alternate routing.
- **Phrasing:** "(fix) AND (fix)" / "RADIALS OF (ATS route) AND (ATS route)" (radials/courses defining a route)
  **Canonical:** `??`
  **Notes:** §4-4-1.b multi-radial route definition; closest are `JoinRadialInbound`/`Outbound` but single-radial only.
- **Phrasing:** "CLEARED TO FLY (general direction) OF (NAVAID) BETWEEN (specified) COURSES TO / BEARINGS FROM / RADIALS (NAVAID) WITHIN (number) MILE RADIUS"
  **Canonical:** `??`
  **Notes:** quadrant/sector clearance for military ops; out-of-pilot-scope for civilian sim.
- **Phrasing:** "CLEARED TO FLY (specified) QUADRANT OF (NAVAID) WITHIN (number) MILE RADIUS"
  **Canonical:** `??`
  **Notes:** same as above.
- **Phrasing:** "DIRECT TO THE (facility) (radial) (distance) FIX"
  **Canonical:** `DirectTo` (+ ??)
  **Notes:** degree-distance fix as `DirectTo` target. No rule synthesizes a fix from "(facility) (radial) (distance)" tokens.
- **Phrasing:** "DIRECT (number) DEGREES, (number) MINUTES (north/south), (number) DEGREES, (number) MINUTES (east/west)" (lat/long)
  **Canonical:** `DirectTo` (+ ??)
  **Notes:** lat/lon coordinate target for `DirectTo`. No rule handles spoken coordinate-pair form.
- **Phrasing:** "OFFSET (distance) RIGHT/LEFT OF (route)"
  **Canonical:** `??`
  **Notes:** parallel-offset clearance off a published route.

##### OutOfScope
- **Phrasing:** §4-4-2 "Vector aircraft to or from radials, courses, or azimuths".
  **Canonical:** —
  **Notes:** procedural guidance; primitives already exist.
- **Phrasing:** §4-4-3 degree-distance fixes for military RBS/celestial/MTR ops.
  **Canonical:** —
  **Notes:** special military ops; not in YAAT scope.
- **Phrasing:** §4-4-5 Class G airspace routing only when pilot-requested.
  **Canonical:** —
  **Notes:** procedural rule, no phraseology block.
- **Phrasing:** §4-4-6 coordination-with-TMU rule.
  **Canonical:** —
  **Notes:** controller workflow.

#### §4-5 — Altitude Assignment and Verification

##### Covered
- **Phrasing:** "MAINTAIN (altitude)"
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:90`.
- **Phrasing:** "CLIMB AND MAINTAIN (altitude)" / "DESCEND AND MAINTAIN (altitude)"
  **Canonical:** `ClimbMaintain` / `DescendMaintain`
  **Notes:** `PhraseologyRules.cs:73, 78`.
- **Phrasing:** "CROSS (fix) AT (altitude)" / "CROSS (fix) AT OR ABOVE/BELOW (altitude)"
  **Canonical:** `CrossFix`
  **Notes:** `PhraseologyRules.cs:128-131`. Also covers §4-3, §4-7, §4-8, AIM §4-4, AIM §5-3.

##### MissingRule
- **Phrasing:** "CRUISE (altitude)"
  **Canonical:** `Cruise`
  **Notes:** canonical exists in enum but no rule. Note: `Cruise` is listed under track-ops out-of-pilot-scope in the index — but the phrasing here is a controller-issued cruise altitude assignment; defer to product whether to extend.
- **Phrasing:** "DESCEND VIA (STAR name and number)" / "DESCEND VIA (STAR), (runway transition)" / "DESCEND VIA (STAR), landing (direction)"
  **Canonical:** `DescendVia`
  **Notes:** canonical exists; no rule. Also at §4-7 MissingRule.
- **Phrasing:** "CLIMB VIA (SID name and number)" / "CLIMB VIA (SID), (en route transition)" / bare "CLIMB VIA SID"
  **Canonical:** `ClimbVia`
  **Notes:** Bare form shipped at `PhraseologyRules.cs:113-114` (covers bare + "EXCEPT MAINTAIN {alt}"). Named-SID variants still need both a SID-name normalizer in the speech pipeline and a canonical that carries a SID-name argument (current `ClimbViaCommand` has no name field — bare CVIA uses the aircraft's already-filed SID, named forms are unmodeled SID amendment).

##### MissingCanonical
- **Phrasing:** "MAINTAIN (altitude) UNTIL (time/fix/waypoint)" / "MAINTAIN (altitude) UNTIL (N) MILES/MINUTES PAST (fix)"
  **Canonical:** `??`
  **Notes:** conditional/expiring altitude restriction.
- **Phrasing:** "INTERCEPT (route) AT OR ABOVE (altitude), CRUISE (altitude)"
  **Canonical:** `??`
  **Notes:** intercept-route-with-altitude not modeled.
- **Phrasing:** "CLIMB/DESCEND TO REACH (altitude) AT (time)" / "AT (fix/waypoint)" / "WITHIN (N) MINUTES" / "IN (N) MINUTES OR LESS"
  **Canonical:** `??`
  **Notes:** time/position-bounded altitude attainment.
- **Phrasing:** "CLIMB/DESCEND TO LEAVE (altitude) WITHIN (N) MINUTES, MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** time-bounded altitude departure.
- **Phrasing:** "CLIMB/DESCEND AND MAINTAIN (altitude) WHEN ESTABLISHED AT LEAST (N) MILES/MINUTES PAST (fix) ON THE (NAVAID) RADIAL"
  **Canonical:** `??`
  **Notes:** conditional altitude tied to radial/DME crossing.
- **Phrasing:** "CROSS (N) MILES (direction) OF (fix) AT (altitude)" / "AT OR ABOVE/BELOW (altitude)"
  **Canonical:** `??`
  **Notes:** distance/bearing-from-fix crossing variant of CROSS not modeled; CrossFix may need a radial/distance form.
- **Phrasing:** "CLIMB/DESCEND AT PILOT'S DISCRETION" / "DESCEND AT PILOT'S DISCRETION, MAINTAIN (altitude)" / "CLIMB/DESCEND NOW TO (alt), THEN CLIMB/DESCEND AT PILOT'S DISCRETION MAINTAIN (alt)"
  **Canonical:** `??`
  **Notes:** pilot's-discretion modifier on climb/descend.
- **Phrasing:** "AMEND ALTITUDE, DESCEND AND MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** explicit amendment marker cancels prior PD authorization.
- **Phrasing:** "MAINTAIN BLOCK (altitude) THROUGH (altitude)"
  **Canonical:** `??`
  **Notes:** block altitude assignment.
- **Phrasing:** "PROCEED DIRECT (fix), CROSS (fix) AT (altitude), THEN DESCEND VIA (STAR)" / "...THEN CLIMB VIA SID"
  **Canonical:** `??`
  **Notes:** compound DCT+CROSS+VNAV; even with individual canonicals, no composite parser.
- **Phrasing:** "DEVIATION (restrictions) APPROVED, MAINTAIN (altitude), EXPECT TO RESUME STAR/SID AT (fix)"
  **Canonical:** `??`
  **Notes:** deviation-with-resume-fix.
- **Phrasing:** "CLIMB/DESCEND VIA (proc) EXCEPT CROSS (fix) (revised altitude)" / "EXCEPT MAINTAIN (altitude)" / "EXCEPT AFTER (fix) MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** override-clause variants of Climb/Descend Via; EXCEPT modifier is unmodeled.
- **Phrasing:** "EXPECT HIGHER/LOWER IN (N) MILES/MINUTES" / "AT (fix)"
  **Canonical:** `??`
  **Notes:** advisory of anticipated altitude change (§4-5-8).
- **Phrasing:** "VERIFY AT (altitude/flight level)" / "VERIFY ASSIGNED ALTITUDE (altitude)" / "VERIFY ASSIGNED FLIGHT LEVEL (FL)"
  **Canonical:** `??`
  **Notes:** altitude-confirmation queries; SayAltitude exists but is a different intent (state current).
- **Phrasing:** "AFFIRMATIVE (altitude)" (readback confirmation) / "NEGATIVE. CLIMB/DESCEND AND MAINTAIN (altitude)" / "NEGATIVE. MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** readback-correction phraseology (§4-5-9).

##### OutOfScope
- **Phrasing:** "REQUEST ALTITUDE/FLIGHT LEVEL CHANGE FROM (facility)"
  **Canonical:** —
  **Notes:** inter-facility coordination.
- **Phrasing:** "(Airport) ARRIVAL DELAYS (time)"
  **Canonical:** —
  **Notes:** ATIS-style broadcast advisory (also referenced in §4-6-3).

#### §4-6 — Holding Aircraft

##### Covered
- **Phrasing:** "HOLD AT (fix)" (with implicit right turns) / "HOLD AT (fix) LEFT TURNS"
  **Canonical:** `HoldAtFixHover` / `HoldAtFixLeft`
  **Notes:** `PhraseologyRules.cs:386-388`.

##### MissingRule
- **Phrasing:** "HOLD (direction) OF (fix) ON (radial/course/bearing/airway/route), (N) MILE/MINUTE LEG, LEFT/RIGHT TURNS"
  **Canonical:** `HoldingPattern`
  **Notes:** `HoldingPattern` canonical exists; current rules only cover bare HOLD AT — no rule parses full holding instructions (direction-of, radial/course, leg length).
- **Phrasing:** "CLEARED TO (fix), HOLD (direction), AS PUBLISHED" / "CLEARED TO (fix), NO DELAY EXPECTED"
  **Canonical:** `HoldingPattern`
  **Notes:** clearance-limit + published-hold form; no rule.

##### MissingCanonical
- **Phrasing:** "EXPECT FURTHER CLEARANCE VIA (routing)" / "EXPECT FURTHER CLEARANCE (time)"
  **Canonical:** `??`
  **Notes:** EFC time/route advisory. Also at §4-3 MissingCanonical.
- **Phrasing:** "ANTICIPATE ADDITIONAL (time) MINUTE/HOUR DELAY AT (fix)" / "EN ROUTE DELAY" / "TERMINAL DELAY"
  **Canonical:** `??`
  **Notes:** delay advisory.
- **Phrasing:** "DELAY INDEFINITE, (reason), EXPECT FURTHER CLEARANCE (time)"
  **Canonical:** `??`
  **Notes:** indefinite-delay advisory.
- **Phrasing:** "VIA LAST ROUTING CLEARED"
  **Canonical:** `??`
  **Notes:** beyond-clearance-limit routing shorthand.
- **Phrasing:** "MAXIMUM HOLDING AIRSPEED IS (speed) KNOTS"
  **Canonical:** `??`
  **Notes:** max-holding-speed advisory; `Speed` canonical doesn't capture "advisory cap".
- **Phrasing:** "HOLD AT (location) UNTIL (time or other condition)"
  **Canonical:** `??`
  **Notes:** visual holding with explicit "UNTIL" condition (§4-6-5); `HoldAtFixHover` exists for bare hold, "UNTIL" condition is unmodeled.

##### OutOfScope
- **Phrasing:** "(Airport) ARRIVAL DELAYS (time)".
  **Canonical:** —
  **Notes:** ATIS-style broadcast (§4-6-3); also at §4-5.

#### §4-7 — Arrival Procedures

##### Covered
- **Phrasing:** "CLEARED (ILS/RNAV/VISUAL/LDA/Localizer/etc.) APPROACH" / "CLEARED (type) RUNWAY (number) APPROACH"
  **Canonical:** `ClearedApproach` / `ClearedVisualApproach`
  **Notes:** `PhraseologyRules.cs:197-209` covers ILS/RNAV runway-suffix variants and bare "cleared approach"; visual at :205-206. LDA/Localizer/VOR variants missing — see §4-8 MissingRule.
- **Phrasing:** "EXPECT (ILS/RNAV/VISUAL) APPROACH RUNWAY (number)" / "EXPECT (type) APPROACH TO RUNWAY (number)"
  **Canonical:** `ExpectApproach`
  **Notes:** `PhraseologyRules.cs:214-218`.
- **Phrasing:** "DESCEND AND MAINTAIN (altitude)" / "MAINTAIN (altitude)"
  **Canonical:** `DescendMaintain` / `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:78-90`.
- **Phrasing:** "CROSS (fix) AT (altitude)" / "CROSS (fix) AT OR ABOVE (altitude)" / "CROSS (fix) AT FLIGHT LEVEL (level)"
  **Canonical:** `CrossFix`
  **Notes:** `PhraseologyRules.cs:128-131`. FL form normalizes to a single token via `AtcNumberParser` and matches the bare "{alt}" capture.

##### MissingRule
- **Phrasing:** "(STAR name and number) ARRIVAL" / "(STAR name and number) ARRIVAL, (transition name) TRANSITION"
  **Canonical:** `JoinStar`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "DESCEND VIA THE (STAR) ARRIVAL" / "DESCEND VIA THE (STAR) ARRIVAL, RUNWAY (number)"
  **Canonical:** `DescendVia`
  **Notes:** canonical exists; no rule. Also at §4-5 MissingRule.

##### MissingCanonical
- **Phrasing:** "CHANGE/AMEND TRANSITION TO (runway number)" / "CHANGE TRANSITION TO (runway) TURN LEFT/RIGHT HEADING (heading) FOR VECTOR TO FINAL APPROACH COURSE"
  **Canonical:** `??`
  **Notes:** amending STAR-transition runway assignment.
- **Phrasing:** "(Airport) AWOS/ASOS WEATHER AVAILABLE ON (frequency)"
  **Canonical:** `??`
  **Notes:** controller-issued weather-source advisory.
- **Phrasing:** "(Airport) RUNWAY (number), FIELD CONDITION, (RwyCC values), (contaminant description). OBSERVED AT (zulu time)"
  **Canonical:** `??`
  **Notes:** RwyCC / FICON NOTAM issuance.
- **Phrasing:** "EXPECT (V-O-R/ILS/PAR/ASR/SURVEILLANCE/PRECISION) APPROACH TO RUNWAY (number); RADAR VECTORS TO FINAL APPROACH COURSE / LOCALIZER COURSE"
  **Canonical:** `??`
  **Notes:** §4-7-5 — variants beyond ILS/RNAV/visual (VOR, PAR, ASR, surveillance/precision); rule set is type-specific. Could be MissingRule if `ExpectApproach` handles arbitrary type tokens.

##### OutOfScope
- **Phrasing:** "CLEARED TO (destination) AIRPORT" / "CLEARED TO (NAVAID/intersection/waypoint name and type)".
  **Canonical:** —
  **Notes:** initial IFR clearance limit issuance; controller workflow (clearance delivery).
- **Phrasing:** "(Identification), (type of aircraft), ESTIMATED/OVER (clearance limit), (time), (altitude), EFC (time). YOUR CONTROL [AT (time, fix or altitude)]".
  **Canonical:** —
  **Notes:** inter-facility coordination message (§4-7-6).
- **Phrasing:** "EXPECT VISUAL APPROACH RUNWAY (number), RUNWAY (number) I-L-S NOT OPERATIONAL".
  **Canonical:** —
  **Notes:** §4-7-10 ILS-status advisory appended to expect-approach; advisory clause is controller-info workflow.

#### §4-8 — Approach Clearance Procedures

##### Covered
- **Phrasing:** "CLEARED (type) APPROACH" / "CLEARED APPROACH" / "CLEARED (specific procedure) APPROACH" (e.g., "Cleared I-L-S Runway Three-Six Approach", "Cleared L-D-A Runway Three-Six Approach", "Cleared RNAV Z Runway Two-Two Approach")
  **Canonical:** `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:197-209` (ILS / RNAV / bare). LDA/Localizer/VOR/BC/GLS variants extend the same canonical — see MissingRule below.
- **Phrasing:** "CLEARED STRAIGHT-IN (type) APPROACH"
  **Canonical:** `ClearedApproachStraightIn`
  **Notes:** `PhraseologyRules.cs:201-202`.
- **Phrasing:** "CANCEL APPROACH CLEARANCE"
  **Canonical:** `CancelLandingClearance`
  **Notes:** closest existing canonical is tower's `CancelLandingClearance` (`PhraseologyRules.cs:153`); see MissingRule below for "cancel approach clearance" wording.
- **Phrasing:** "CROSS (fix) AT OR ABOVE (altitude), CLEARED (type) APPROACH"
  **Canonical:** `CrossFix` + `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:128-131` + `:197-209`. The greedy multi-clause matcher chains the two as `CFIX … , CAPP …` automatically.
- **Phrasing:** "CLEARED LOCALIZER APPROACH" / "CLEARED LOCALIZER BACK COURSE RUNWAY (number) APPROACH" / "CLEARED V-O-R RUNWAY (number) APPROACH" / "CLEARED L-D-A RUNWAY (number) APPROACH"
  **Canonical:** `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:223-230`. Canonical encodes the type as a prefix (LOC, B for back-course, VOR, LDA); resolved by `NavigationDatabase.ResolveApproachId`. GLS variant of this same FAA listing is *not* covered yet — `TryStripTypePrefix` lacks "GLS"→'J', tracked in MissingRule below.

##### MissingRule
- **Phrasing:** "AT (fix), CLEARED (type) APPROACH" (e.g., "At RDFSH, Cleared ILS Runway 27 Approach")
  **Canonical:** `ClearedApproach`
  **Notes:** rules don't accept "at {fix}" prefix carrying a connection-fix instruction.
- **Phrasing:** "CLEARED G-L-S APPROACH" (gap from the otherwise-Covered LOC/VOR/LDA list above)
  **Canonical:** `ClearedApproach`
  **Notes:** `NavigationDatabase.ResolveApproachId.TryStripTypePrefix` doesn't include "GLS" in its prefix table (only ILS/LOC/RNAV/GPS/VOR/NDB/LDA/TACAN/SDF). Shipping the STT rule requires adding "GLS" → 'J' to that list first.
- **Phrasing:** "CLEARED (ILS/LDA) APPROACH, GLIDESLOPE UNUSABLE"
  **Canonical:** `ClearedApproach`
  **Notes:** "glideslope unusable" advisory suffix not produced.
- **Phrasing:** "CLEARED (BRANCH ONE) ARRIVAL AND (ILS/RNAV) RUNWAY (number) APPROACH"
  **Canonical:** `JoinStar` + `ClearedApproach`
  **Notes:** combined STAR + approach; no compound rule.
- **Phrasing:** "CLEARED (type) APPROACH TO (airport name)" / "CLEARED APPROACH TO (airport name)"
  **Canonical:** `ClearedApproach`
  **Notes:** §4-8-2 uncontrolled-airport variant; canonical exists, no rule accepts trailing "to (airport)".
- **Phrasing:** "CIRCLE TO RUNWAY (number)" / "CIRCLE (cardinal direction) OF THE AIRPORT/RUNWAY FOR A LEFT/RIGHT BASE/DOWNWIND TO RUNWAY (number)"
  **Canonical:** `CircleAirport`
  **Notes:** canonical exists (`PhraseologyRules.cs:376` maps "circle the airport"); §4-8-6 directional circling-to-runway form has no rule.
- **Phrasing:** "CANCEL APPROACH CLEARANCE"
  **Canonical:** `CancelLandingClearance`
  **Notes:** §4-8-1 lists this distinct from tower "cancel landing clearance"; rule line 153 only matches "cancel landing clearance".

##### MissingCanonical
- **Phrasing:** "SIDE-STEP TO RUNWAY (number)" (appended to approach clearance)
  **Canonical:** `??`
  **Notes:** §4-8-7 side-step maneuver.
- **Phrasing:** "CHANGE TO ADVISORY FREQUENCY APPROVED"
  **Canonical:** `??` (possibly `FrequencyChangeApproved`)
  **Notes:** §4-8-8 communications release at uncontrolled fields; closest is `FrequencyChangeApproved`. Defer to product whether to reuse.
- **Phrasing:** "INITIAL APPROACH AT (altitude), PROCEDURE TURN AT (altitude), (number) MINUTES/MILES (direction), FINAL APPROACH ON (NAVAID) (specified) COURSE/RADIAL/AZIMUTH AT (altitude)"
  **Canonical:** `??`
  **Notes:** §4-8-10 full approach briefing for unfamiliar pilots.
- **Phrasing:** "MAINTAIN VFR, PRACTICE APPROACH APPROVED, NO SEPARATION SERVICES PROVIDED"
  **Canonical:** `??`
  **Notes:** §4-8-11 practice approach approval.
- **Phrasing:** "AFTER COMPLETING LOW APPROACH, CLIMB AND MAINTAIN (altitude). TURN RIGHT/LEFT, HEADING (heading)" / "MAINTAIN VFR, CONTACT TOWER" (post-low-approach climb-out)
  **Canonical:** `??`
  **Notes:** §4-8-12 sequenced post-low-approach departure instructions; underlying altitude/heading canonicals exist but no compound with "after completing low approach" trigger.
- **Phrasing:** "(missed approach alternate procedure description)" — controller-issued alternate missed approach
  **Canonical:** `??`
  **Notes:** §4-8-9.

##### OutOfScope
- **Phrasing:** "(weather report transmission)" — relayed approach clearance with weather (§4-8-3).
  **Canonical:** —
  **Notes:** inter-station/relay workflow.
- **Phrasing:** "(coordination with facility using ILS fixes)" — switching ILS runways (§4-7-13).
  **Canonical:** —
  **Notes:** controller-to-controller coordination.
- **Phrasing:** "(advisory: GPS may not be available; request intentions)" (§4-8-1.11/12).
  **Canonical:** —
  **Notes:** advisory/info exchange.

**Ch 4 totals:** Covered 23 · MissingRule 23 · MissingCanonical 55 · OutOfScope 29 · Phrasings 130

### Chapter 5 — Radar

#### §5-1 — General (Radar)

##### Covered
- **Phrasing:** "(number of miles) MILES FROM (fix)" / "(number of miles) MILES (direction) OF (fix, airway, or location)" / "OVER/PASSING (fix)" / "CROSSING/JOINING/DEPARTING (airway or route)" / "INTERCEPTING/CROSSING (NAVAID) (specified) RADIAL"
  **Canonical:** `SayPosition`
  **Notes:** controller-side position broadcast; pilot-input ask covered at `PhraseologyRules.cs:523-524`.

##### OutOfScope
- **Phrasing:** "PRIMARY RADAR UNAVAILABLE (location). RADAR SERVICES AVAILABLE ON TRANSPONDER OR ADS-B EQUIPPED AIRCRAFT ONLY."
  **Canonical:** —
  **Notes:** controller-to-pilot equipment advisory.
- **Phrasing:** "BIG PHOTO (identification) (facility) CENTER/TOWER/APPROACH CONTROL. STOP STREAM/BURST IN AREA … RESUME STREAM/BURST / RESUME BUZZER ON (band)."
  **Canonical:** —
  **Notes:** Electronic Attack (EA) coordination with military aircraft; out-of-scope.
- **Phrasing:** "RADAR SERVICE TERMINATED (nonradar routing if required)"
  **Canonical:** —
  **Notes:** controller-side outbound advisory.
- **Phrasing:** Merging-target traffic advisory ("Traffic twelve o'clock, seven miles, eastbound …")
  **Canonical:** —
  **Notes:** controller-side traffic broadcast; pilot response covered by `ReportTrafficInSight`.

#### §5-2 — Beacon/ADS-B Systems

##### Covered
- **Phrasing:** "SQUAWK THREE/ALFA (code)" / "SQUAWK (code)"
  **Canonical:** `Squawk`
  **Notes:** `PhraseologyRules.cs:406`.
- **Phrasing:** "SQUAWK VFR" / "SQUAWK 1200"
  **Canonical:** `SquawkVfr`
  **Notes:** `PhraseologyRules.cs:407`.
- **Phrasing:** "SQUAWK STANDBY" / "SQUAWK NORMAL"
  **Canonical:** `SquawkStandby` / `SquawkNormal`
  **Notes:** `PhraseologyRules.cs:408-409`.
- **Phrasing:** "SAY ALTITUDE" (validation context)
  **Canonical:** `SayAltitude`
  **Notes:** `PhraseologyRules.cs:519-520`. "Say flight level" variant uncovered — see MissingRule.

##### MissingRule
- **Phrasing:** "SQUAWK MAYDAY ON 7700"
  **Canonical:** `Squawk`
  **Notes:** emergency-flavored — likely skip per IRROPS exclusion; squawk action maps to `Squawk` with code 7700.
- **Phrasing:** "IF FEASIBLE, SQUAWK (code)"
  **Canonical:** `Squawk`
  **Notes:** "if feasible" prefix variant.
- **Phrasing:** "(Identification) RESET TRANSPONDER, SQUAWK (appropriate code)"
  **Canonical:** `Squawk`
  **Notes:** "reset transponder" prefix variant.
- **Phrasing:** "(Identification) YOUR TRANSPONDER APPEARS INOPERATIVE/MALFUNCTIONING, RESET, SQUAWK (code)"
  **Canonical:** `Squawk`
  **Notes:** "reset" filler before squawk; transponder malfunction advisory itself is out-of-scope.
- **Phrasing:** "SAY FLIGHT LEVEL"
  **Canonical:** `SayAltitude`
  **Notes:** only "say altitude" tokens covered; FL synonym missing.
- **Phrasing:** "STOP SQUAWK" / "STOP SQUAWK (mode in use)"
  **Canonical:** `SquawkStandby`
  **Notes:** §5-2-20 beacon-termination phrasing; arguably `SquawkStandby` semantically.
- **Phrasing:** "SQUAWK (code) AND IDENT"
  **Canonical:** `Squawk` + `Ident`
  **Notes:** composite phrasing; individual canonicals covered.

##### MissingCanonical
- **Phrasing:** "SQUAWK ALTITUDE" / "STOP ALTITUDE SQUAWK"
  **Canonical:** `??`
  **Notes:** Mode-C reporting on/off; distinct from `SquawkNormal`/`SquawkStandby`.
- **Phrasing:** "STOP ADS-B TRANSMISSIONS, AND IF ABLE, SQUAWK THREE/ALFA (code)"
  **Canonical:** `??`
  **Notes:** no `StopAdsB` canonical.
- **Phrasing:** "VERIFY AT (altitude/flight level)" / "VERIFY ASSIGNED ALTITUDE (altitude)" / "VERIFY ASSIGNED FLIGHT LEVEL (flight level)" / "VERIFY ALTITUDE" / "VERIFY FLIGHT LEVEL"
  **Canonical:** `??`
  **Notes:** Mode-C altitude verification. `SayAltitude` semantics differ. Also at §4-5.
- **Phrasing:** "VERIFY USING TWO NINER NINER TWO AS YOUR ALTIMETER SETTING"
  **Canonical:** `??`
  **Notes:** altimeter setting verification; no altimeter canonical.
- **Phrasing:** "(Location) ALTIMETER (appropriate altimeter), VERIFY ALTITUDE"
  **Canonical:** `??`
  **Notes:** issuing altimeter setting + altitude verification.
- **Phrasing:** "AFFIRMATIVE (altitude)" / "NEGATIVE. CLIMB/DESCEND AND MAINTAIN (altitude)" / "NEGATIVE. MAINTAIN (altitude)"
  **Canonical:** `??`
  **Notes:** readback reconfirmation. Also at §4-5.

##### OutOfScope
- **Phrasing:** "STOP ALTITUDE SQUAWK. ALTITUDE DIFFERS BY (number of feet) FEET."
  **Canonical:** —
  **Notes:** Mode-C malfunction advisory.
- **Phrasing:** "(Name of facility) BEACON INTERROGATOR INOPERATIVE/MALFUNCTIONING"
  **Canonical:** —
  **Notes:** equipment status broadcast.
- **Phrasing:** "YOUR ADS-B TRANSMITTER APPEARS TO BE INOPERATIVE / MALFUNCTIONING"
  **Canonical:** —
  **Notes:** equipment advisory.
- **Phrasing:** "YOUR ADS-B FLIGHT ID DOES NOT MATCH YOUR FLIGHT PLAN AIRCRAFT IDENTIFICATION"
  **Canonical:** —
  **Notes:** CSMM advisory.

#### §5-3 — Radar Identification

##### Covered
- **Phrasing:** "IDENT"
  **Canonical:** `Ident`
  **Notes:** `PhraseologyRules.cs:411`.
- **Phrasing:** "SQUAWK (code) AND IDENT"
  **Canonical:** `Squawk` + `Ident`
  **Notes:** sequential chain via `;`.
- **Phrasing:** "SQUAWK STANDBY, then SQUAWK NORMAL"
  **Canonical:** `SquawkStandby` + `SquawkNormal`
  **Notes:** sequential.
- **Phrasing:** "SQUAWK (4 digit discrete code)"
  **Canonical:** `Squawk`
  **Notes:** `PhraseologyRules.cs:406`.

##### OutOfScope
- **Phrasing:** "RADAR CONTACT (position if required)"
  **Canonical:** —
  **Notes:** controller-side identification status broadcast.
- **Phrasing:** "RADAR CONTACT LOST (alternative instructions when required)"
  **Canonical:** —
  **Notes:** controller-side identification status broadcast.

#### §5-4 — Transfer of Radar Identification

##### OutOfScope
- **Phrasing:** "HANDOFF (position), (aircraft ID), (altitude, restrictions)"
  **Canonical:** —
  **Notes:** controller-to-controller landline coordination.
- **Phrasing:** "POINT OUT (position), (aircraft ID), (altitude)"
  **Canonical:** —
  **Notes:** controller-to-controller coordination — out-of-pilot-scope (track ops).
- **Phrasing:** "TRAFFIC (position), (aircraft ID), (restrictions)"
  **Canonical:** —
  **Notes:** inter-controller traffic coordination.
- **Phrasing:** "(Aircraft ID) RADAR CONTACT" (handoff acceptance response)
  **Canonical:** —
  **Notes:** inter-controller acceptance phrase.
- **Phrasing:** "(Aircraft ID) POINT OUT APPROVED"
  **Canonical:** —
  **Notes:** inter-controller point-out approval.
- **Phrasing:** "TRAFFIC OBSERVED"
  **Canonical:** —
  **Notes:** inter-controller acknowledgement.
- **Phrasing:** "UNABLE (appropriate information)"
  **Canonical:** —
  **Notes:** inter-controller rejection.
- **Phrasing:** §5-4-10 fourth-line data block formats (H080, S210, M.80, D90L, RQ170, CELNAV)
  **Canonical:** —
  **Notes:** en-route fourth-line data-block coordination codes; not spoken.

#### §5-5 — Radar Separation

##### OutOfScope
- **Phrasing:** Entire section (5-5-1 through 5-5-12).
  **Canonical:** —
  **Notes:** internal controller separation standards (target separation, wake turbulence minima, vertical application, passing/diverging, formation flights, obstruction/edge-of-scope/adjacent-airspace buffers).
- **Phrasing:** "Traffic, twelve o'clock, Boeing 727, opposite direction. Do you have it in sight?"
  **Canonical:** —
  **Notes:** opposite-course visual passing advisory; pilot side handles `ReportTrafficInSight` for the response.
- **Phrasing:** "Report passing the traffic."
  **Canonical:** —
  **Notes:** §5-5-7 follow-up after pilot has traffic in sight; distinct from `ReportTrafficInSight` (report when passed).

#### §5-6 — Vectoring

##### Covered
- **Phrasing:** "TURN LEFT/RIGHT HEADING (degrees)"
  **Canonical:** `TurnLeft` / `TurnRight`
  **Notes:** `PhraseologyRules.cs:57-58`.
- **Phrasing:** "FLY HEADING (degrees)"
  **Canonical:** `FlyHeading`
  **Notes:** `PhraseologyRules.cs:62-63`.
- **Phrasing:** "FLY PRESENT HEADING"
  **Canonical:** `FlyPresentHeading`
  **Notes:** `PhraseologyRules.cs:64-65`.
- **Phrasing:** "TURN (number of degrees) DEGREES LEFT/RIGHT"
  **Canonical:** `RelativeLeft` / `RelativeRight`
  **Notes:** `PhraseologyRules.cs:60-61`.
- **Phrasing:** "FLY HEADING (degrees). WHEN ABLE, PROCEED DIRECT (fix)"
  **Canonical:** `FlyHeading` + `AppendDirectTo`
  **Notes:** `PhraseologyRules.cs:62-63` + `:119`.

##### MissingRule
- **Phrasing:** "DEPART (fix) HEADING (degrees)"
  **Canonical:** `DepartFix`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "THIS WILL BE A NO-GYRO VECTOR" / "STOP TURN"
  **Canonical:** `??`
  **Notes:** no-gyro vectoring uses imperative TURN LEFT / TURN RIGHT / STOP TURN without a heading. Existing turn rules require a heading.
- **Phrasing:** "VECTOR TO (fix or airway)"
  **Canonical:** `??`
  **Notes:** purpose-of-vector advisory.
- **Phrasing:** "VECTOR TO INTERCEPT (NAVAID) (specified) RADIAL"
  **Canonical:** `JoinRadialInbound` / `JoinRadialOutbound`
  **Notes:** canonicals exist; no rules. Also at §4-4.
- **Phrasing:** "VECTOR FOR SPACING"
  **Canonical:** `??`
  **Notes:** vector-purpose advisory.
- **Phrasing:** "EXPECT DIRECT (NAVAID, waypoint, fix)"
  **Canonical:** `??`
  **Notes:** future-DCT advisory; compare to existing `ExpectApproach`.
- **Phrasing:** "VECTOR TO FINAL APPROACH COURSE" / "VECTOR TO (approach name) FINAL APPROACH COURSE"
  **Canonical:** `JoinFinalApproachCourse`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "(if necessary, MAINTAIN (speed)), EXPECT TO RESUME (SID, STAR, etc.)"
  **Canonical:** `??`
  **Notes:** advisory of future procedure resumption.
- **Phrasing:** "DEVIATION (restrictions if necessary) APPROVED, MAINTAIN (altitude), ... EXPECT TO RESUME (SID, STAR, etc.) AT (fix)"
  **Canonical:** `??`
  **Notes:** weather/route deviation approval. Also at §4-5.
- **Phrasing:** "(Position with respect to course/fix along route), RESUME OWN NAVIGATION"
  **Canonical:** `??`
  **Notes:** terminates radar vectoring.
- **Phrasing:** "RESUME (SID/STAR/transition/procedure)"
  **Canonical:** `??`
  **Notes:** re-engages a previously assigned SID/STAR after vectoring off.
- **Phrasing:** "CLEARED DIRECT (fix) CROSS (fix) AT/AT OR ABOVE/AT OR BELOW (altitude), then CLIMB VIA/DESCEND VIA (SID/STAR)"
  **Canonical:** `DirectTo` + `CrossFix` + `ClimbVia`/`DescendVia`
  **Notes:** all canonicals exist; `CrossFix`/`ClimbVia`/`DescendVia` have no rules. Also at §4-3, §4-5, §4-7, §4-8.
- **Phrasing:** "EXPECT VECTOR ACROSS (NAVAID radial) (airway/route/course) FOR (purpose)"
  **Canonical:** `??`
  **Notes:** pre-notification advisory.

#### §5-7 — Speed Adjustment

##### Covered
- **Phrasing:** "Say airspeed."
  **Canonical:** `SaySpeed`
  **Notes:** `PhraseologyRules.cs:516-517`.
- **Phrasing:** "Say Mach number."
  **Canonical:** `SayMach`
  **Notes:** `PhraseologyRules.cs:518`.
- **Phrasing:** "Maintain (speed) knots."
  **Canonical:** `Speed`
  **Notes:** `PhraseologyRules.cs:92, 95`.
- **Phrasing:** "Increase/Reduce speed to (specified speed)."
  **Canonical:** `Speed`
  **Notes:** `PhraseologyRules.cs:93-94`.
- **Phrasing:** "Increase speed to Mach point seven two." (Mach assignment)
  **Canonical:** `Mach`
  **Notes:** `PhraseologyRules.cs:106-107`.
- **Phrasing:** "Resume normal speed."
  **Canonical:** `ResumeNormalSpeed`
  **Notes:** `PhraseologyRules.cs:98`.
- **Phrasing:** "Delete speed restrictions."
  **Canonical:** `DeleteSpeedRestrictions`
  **Notes:** `PhraseologyRules.cs:99-100`.
- **Phrasing:** "Cross (fix) at and maintain (altitude) at (speed) knots." (combined fix+alt+speed crossing)
  **Canonical:** `CrossFix`
  **Notes:** `PhraseologyRules.cs:135`. Verbalized as "cross … at and maintain … at … knots" with `SpeedWords` colloquial form.

##### MissingRule
- **Phrasing:** "Maintain present speed."
  **Canonical:** `??` (analogous to `FlyPresentHeading`)
  **Notes:** no rule; might warrant new `MaintainPresentSpeed` canonical.
- **Phrasing:** "Reduce speed twenty knots." (relative delta)
  **Canonical:** `Speed`
  **Notes:** existing `Speed` rules expect absolute target; no relative-delta rule.
- **Phrasing:** "Maintain (speed) until (fix), then (additional instructions)."
  **Canonical:** `??`
  **Notes:** conditional speed-until-fix.
- **Phrasing:** "Resume published speed."
  **Canonical:** `??`
  **Notes:** distinct from "resume normal speed" — semantics differ.
- **Phrasing:** "Comply with speed restrictions."
  **Canonical:** `??`
  **Notes:** reverse of `DeleteSpeedRestrictions`.

##### MissingCanonical
- **Phrasing:** "Cross (fix) at (speed)." (fix-crossing speed only, no altitude)
  **Canonical:** `??` (CrossFix needs speed-only variant)
  **Notes:** reclassified from MissingRule: `CrossFixCommand.Altitude` is non-nullable in `ParsedCommand.cs:733`, so the speed-only form needs a canonical extension (new field or new command). Also at AIM §5-3 UM55/56/57.
- **Phrasing:** "Maintain (speed) knots or greater." / "Maintain (speed) knots or less."
  **Canonical:** `??`
  **Notes:** one-sided speed bound; current `Speed` is absolute.
- **Phrasing:** "Do not exceed (speed) knots."
  **Canonical:** `??`
  **Notes:** upper-bound speed restriction.
- **Phrasing:** "Maintain maximum forward speed."
  **Canonical:** `??`
  **Notes:** symbolic speed.
- **Phrasing:** "Maintain slowest practical speed."
  **Canonical:** `??`
  **Notes:** symbolic speed.
- **Phrasing:** "(speed adjustment), if unable advise." (FL390+ concurrence)
  **Canonical:** `??`
  **Notes:** trailing "if unable advise" tag.
- **Phrasing:** "Reduce speed to (spd), then descend and maintain (alt)." (sequenced speed-then-descend)
  **Canonical:** `??`
  **Notes:** composite — could map to existing `Speed` + `DescendMaintain` via `;` but no STT rule.
- **Phrasing:** "Descend and maintain (alt), then reduce speed to (spd)."
  **Canonical:** `??`
  **Notes:** same as above, reversed.

##### OutOfScope
- **Phrasing:** "Climb via/Descend via (SID/STAR name and number) (transition)" — referenced under speed-restriction cancellation.
  **Canonical:** —
  **Notes:** ClimbVia/DescendVia primary scope is §4; §5-7 only references as cancellation context.

#### §5-8 — Radar Departures

##### Covered
- **Phrasing:** "Turn left/right, heading (degrees)."
  **Canonical:** `TurnLeft` / `TurnRight`
  **Notes:** `PhraseologyRules.cs:57-58`.
- **Phrasing:** "RNAV to (fix/waypoint), runway (number), cleared for takeoff."
  **Canonical:** `ClearedForTakeoff`
  **Notes:** tail covered at `PhraseologyRules.cs:140-144`; RNAV-to-fix prefix is advisory.

##### MissingRule
- **Phrasing:** "Fly runway heading."
  **Canonical:** `FlyPresentHeading` (or new `FlyRunwayHeading`)
  **Notes:** existing rules cover "fly/maintain present heading"; "fly runway heading" is a literal-wording miss. Also at §4-3, §5-10.

##### OutOfScope
- **Phrasing:** §5-8-3, §5-8-4, §5-8-5 — successive/simultaneous departures, departure/arrival separation, parallel-runway departure separation.
  **Canonical:** —
  **Notes:** separation procedures, no phraseology.

#### §5-9 — Radar Arrivals

##### Covered
- **Phrasing:** "Turn (left/right) heading (degrees). Maintain (altitude) until established on the localizer. Cleared ILS runway (rwy) approach."
  **Canonical:** `PositionTurnAltitudeClearance`
  **Notes:** `PhraseologyRules.cs:250-280` PTAC composite.
- **Phrasing:** "Cleared ILS runway (rwy) approach." / "Cleared RNAV runway (rwy) approach."
  **Canonical:** `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:197-200, 208-209`.
- **Phrasing:** "(distance) miles from CENTR, cleared RNAV runway one eight approach" (TAA arrival variant)
  **Canonical:** `ClearedApproach`
  **Notes:** distance-prefix advisory; clearance tail covered.
- **Phrasing:** "Contact tower (frequency)."
  **Canonical:** `Contact`
  **Notes:** `Contact` canonical exists; out-of-pilot-scope per index — see MissingRule for "monitor" variants.
- **Phrasing:** "Cross (fix) at or above (altitude). Cleared ILS runway (rwy) approach." (cross-at-or-above + approach)
  **Canonical:** `CrossFix` + `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:128-131` + `:197-209`. The greedy multi-clause matcher chains them as `CFIX … , CAPP …` automatically. Also at §4-8.

##### MissingRule
- **Phrasing:** "(Ident) (distance) miles from the airport, (distance) miles right/left of course, say intentions."
  **Canonical:** `SayPosition` (partial)
  **Notes:** "say intentions" tail has no canonical/rule.
- **Phrasing:** "Turn (left/right) heading (degrees), maintain (altitude) until established on the localizer." (standalone vector + crossing-altitude, no approach clearance)
  **Canonical:** `TurnLeft` + `ClimbMaintain`/`DescendMaintain`
  **Notes:** PTAC requires trailing "cleared ... approach"; "until established on the localizer" trigger uncovered.
- **Phrasing:** "Cleared direct (fix), cross (fix) at or above (altitude), cleared RNAV runway (rwy) approach."
  **Canonical:** `DirectTo` + `CrossFix` + `ClearedApproach`
  **Notes:** composite; pieces could chain via `;` but no STT rule chains them.
- **Phrasing:** "Expect vectors across final for (purpose)." / "Expect vectors across final for spacing."
  **Canonical:** `??`
  **Notes:** informational advisory.

##### MissingCanonical
- **Phrasing:** "Say intentions."
  **Canonical:** `??`
  **Notes:** pilot prompt for intent after off-course deviation.
- **Phrasing:** "I will advise when over the fix."
  **Canonical:** `??`
  **Notes:** controller commitment; no canonical.
- **Phrasing:** "Over final approach fix."
  **Canonical:** `??`
  **Notes:** controller advisory of FAF crossing.
- **Phrasing:** "Monitor (tower) (frequency)." / "Monitor local control frequency, reporting to the tower when over the approach fix."
  **Canonical:** `??`
  **Notes:** "Monitor" distinct from "contact" (listen-only); no `MonitorFrequency` canonical.
- **Phrasing:** "I show you (left/right) of the final approach course."
  **Canonical:** `??`
  **Notes:** PRM monitor controller advisory.
- **Phrasing:** "You have crossed the final approach course. Turn (left/right) immediately and return to the final approach course."
  **Canonical:** `??`
  **Notes:** bare directional turn tied to course recapture; `TurnLeft`/`TurnRight` need a heading.
- **Phrasing:** "Traffic alert, (call sign), turn (left/right) immediately heading (degrees), climb/descend and maintain (altitude)." (PRM/SOIA breakout)
  **Canonical:** `SafetyAlert` (+composite)
  **Notes:** `SafetyAlert` canonical exists with no rule; "traffic alert" prefix + "immediately" modifier uncovered.

##### OutOfScope
- **Phrasing:** §5-9-1, §5-9-5, §5-9-6 through §5-9-11 (vector-to-FAC criteria, separation responsibility, simultaneous dependent/independent/PRM/SOIA configuration, transitional go-around).
  **Canonical:** —
  **Notes:** pure ATC workflow.

#### §5-10 — Radar Approaches (Terminal)

##### MissingRule
- **Phrasing:** "Turn left/right. Stop turn." (no-gyro approach steering)
  **Canonical:** `TurnLeft` / `TurnRight` (bare) + `??`
  **Notes:** existing TL/TR rules require {hdg} capture; no bare-form rule. "Stop turn" has no canonical.
- **Phrasing:** "Execute missed approach"
  **Canonical:** `GoAround`
  **Notes:** no rule; existing GoAround rules require "go around" literal.
- **Phrasing:** "Contact (facility) final controller on (frequency)"
  **Canonical:** `Contact`
  **Notes:** `Contact` in enum, out-of-pilot-scope per index; no rule.

##### MissingCanonical
- **Phrasing:** "This will be a P-A-R/surveillance approach to runway (rwy)" / "...to (airport) airport, runway (rwy)"
  **Canonical:** `??`
  **Notes:** radar-approach type announcement.
- **Phrasing:** "Missed approach point is (distance) miles from runway/airport/heliport"
  **Canonical:** `??`
  **Notes:** ASR MAP location advisory.
- **Phrasing:** "No traffic or landing runway information available for the airport"
  **Canonical:** `??`
  **Notes:** non-towered advisory.
- **Phrasing:** "This will be a no-gyro surveillance/P-A-R approach"
  **Canonical:** `??`
  **Notes:** no-gyro announcement.
- **Phrasing:** "Make half-standard rate turns"
  **Canonical:** `??`
  **Notes:** `SetTurnRate` exists but is sim-control / out-of-pilot-scope.
- **Phrasing:** "If no transmissions are received for (interval) ... attempt contact on (frequency) ... proceed VFR ... proceed with (nonradar approach), maintain (altitude) until established ..."
  **Canonical:** `??`
  **Notes:** lost-comms script.
- **Phrasing:** "Perform landing check"
  **Canonical:** `??`
  **Notes:** USA/USN landing-check directive.
- **Phrasing:** "(Number) miles (direction) of (airport name) airport [on downwind/base leg]"
  **Canonical:** `??`
  **Notes:** position advisory to pilot.
- **Phrasing:** "(Aircraft call sign), (facility) final controller. How do you hear me?"
  **Canonical:** `??`
  **Notes:** comms check.
- **Phrasing:** "Do not acknowledge further transmissions"
  **Canonical:** `??`
  **Notes:** PAR/ASR final-approach silent-readback directive.
- **Phrasing:** "Your missed approach procedure is (missed approach procedure)"
  **Canonical:** `??`
  **Notes:** MAP procedure assignment.
- **Phrasing:** "After completing low approach/touch and go: climb and maintain (alt), turn (L/R) heading (deg) / fly runway heading, or maintain VFR contact tower"
  **Canonical:** `ClimbMaintain` + `TurnLeft`/`TurnRight` + `Contact` chained
  **Notes:** components covered individually; no "after completing low approach/touch and go" conditional trigger. Also at §4-8.
- **Phrasing:** "Fly runway heading"
  **Canonical:** `??`
  **Notes:** distinct from `FlyPresentHeading`; no canonical. Also at §4-3, §5-8.
- **Phrasing:** "Tower clearance canceled/not received (alternative instructions)"
  **Canonical:** `??`
  **Notes:** announces cancellation.
- **Phrasing:** "(Reason) if runway/approach lights/runway lights not in sight, execute missed approach/(alternative instructions)"
  **Canonical:** `GoAround` (+conditional)
  **Notes:** conditional trigger ("if not in sight") not modeled.

##### OutOfScope
- **Phrasing:** Altimeter/ceiling/visibility/AWOS/ASOS/airport-condition issuance (5-10-2 a.1-4).
  **Canonical:** —
  **Notes:** weather/ATIS issuance.
- **Phrasing:** §5-10-15 Military single-frequency approach procedures.
  **Canonical:** —
  **Notes:** controller workflow / coordination.

#### §5-11 — Surveillance Approaches (Terminal)

##### Covered
- **Phrasing:** "Heading (heading)" (bare)
  **Canonical:** `FlyHeading`
  **Notes:** `PhraseologyRules.cs:63`.

##### MissingCanonical
- **Phrasing:** "Recommended altitudes will be provided for each mile on final to minimum descent altitude/circling minimum descent altitude"
  **Canonical:** `??`
  **Notes:** ASR altitude-info advisory.
- **Phrasing:** "Report (runway, approach/runway lights, or airport) in sight"
  **Canonical:** `ReportFieldInSight`
  **Notes:** existing rules cover "report airport/field in sight"; "report runway in sight" and "report approach/runway lights in sight" not indexed — MissingRule.
- **Phrasing:** "Report when able to proceed visually to airport/heliport/vertiport"
  **Canonical:** `??`
  **Notes:** point-in-space visual proceed report.
- **Phrasing:** "Prepare to descend in (number) miles"
  **Canonical:** `??`
  **Notes:** descent notification.
- **Phrasing:** "Minimum descent altitude (altitude)" / "Published circling minimum descent altitude (altitude)"
  **Canonical:** `??`
  **Notes:** MDA issuance.
- **Phrasing:** "Request your aircraft approach category"
  **Canonical:** `??`
  **Notes:** approach-category solicitation.
- **Phrasing:** "(Number) miles from runway/airport/heliport. Descend to your minimum descent altitude"
  **Canonical:** `DescendMaintain` (?)
  **Notes:** MDA is not a numeric altitude.
- **Phrasing:** "(Number) miles from runway/airport/heliport. Descend and maintain (restriction altitude)"
  **Canonical:** `DescendMaintain`
  **Notes:** distance-prefix variant.
- **Phrasing:** "Heading (heading)" / "On course" / "Slightly/well left/right of course"
  **Canonical:** `??`
  **Notes:** final-approach guidance trend info. "Heading (hdg)" matches `FlyHeading` line 63 (covered above).
- **Phrasing:** "Going left/right of course" / "Left/right of course and holding/correcting"
  **Canonical:** `??`
  **Notes:** trend info.
- **Phrasing:** "(Number) mile(s) from runway/airport/heliport or missed approach point"
  **Canonical:** `??`
  **Notes:** range advisory.
- **Phrasing:** "Altitude should be (altitude)"
  **Canonical:** `??`
  **Notes:** recommended altitude advisory.
- **Phrasing:** "(Distance) mile(s) from runway/airport/heliport, [or] over missed approach point. Proceed visually (additional instructions)"
  **Canonical:** `??`
  **Notes:** "proceed visually" termination.
- **Phrasing:** "If runway, or approach/runway lights not in sight, execute missed approach/(missed approach instructions)"
  **Canonical:** `GoAround`
  **Notes:** "execute missed approach" + conditional. Same gap as §5-10.

#### §5-12 — PAR Approaches (Terminal)

##### MissingCanonical
- **Phrasing:** "Approaching glidepath"
  **Canonical:** `??`
  **Notes:** PAR glidepath notification.
- **Phrasing:** "Decision height (number of feet)"
  **Canonical:** `??`
  **Notes:** DH issuance.
- **Phrasing:** "Begin descent"
  **Canonical:** `??`
  **Notes:** PAR descent-start; distinct from `DescendMaintain` (no altitude).
- **Phrasing:** "On glidepath" / "Slightly/well above/below glidepath" / "Going above/below glidepath" / "Above/below glidepath and coming down/up" / "Above/below glidepath and holding"
  **Canonical:** `??`
  **Notes:** PAR glidepath trend info.
- **Phrasing:** "(Number of miles) miles from touchdown"
  **Canonical:** `??`
  **Notes:** PAR distance-from-touchdown.
- **Phrasing:** "At decision height"
  **Canonical:** `??`
  **Notes:** DH-reached notification.
- **Phrasing:** "Over approach lights"
  **Canonical:** `??`
  **Notes:** PAR position advisory.
- **Phrasing:** "Over landing threshold, (position with respect to course)"
  **Canonical:** `??`
  **Notes:** PAR threshold advisory.
- **Phrasing:** "(Distance) mile(s) from touchdown, proceed visually (additional instructions/clearance)"
  **Canonical:** `??`
  **Notes:** PAR termination — same "proceed visually" gap as §5-11.
- **Phrasing:** "Contact (terminal control function) (frequency) after landing"
  **Canonical:** `Contact`
  **Notes:** "after landing" trigger not modeled; out-of-pilot-scope.
- **Phrasing:** "No glidepath information available. If runway, approach/runway lights, not in sight, execute missed approach/(alternative instructions)"
  **Canonical:** `GoAround`
  **Notes:** PAR elevation-failure + missed-approach conditional.
- **Phrasing:** "This will be a surveillance approach to runway (rwy). Mileages will be from touchdown" / "...using P-A-R azimuth. Mileages will be from touchdown"
  **Canonical:** `??`
  **Notes:** approach-type announcement after PAR elevation failure.

#### §5-13 — Automation En Route

##### OutOfScope
- **Phrasing:** §5-13-1 CA/MCI alert handling and suppression (CO, SG functions).
  **Canonical:** —
  **Notes:** controller-display automation.
- **Phrasing:** §5-13-2 E-MSAW alert handling and suppression.
  **Canonical:** —
  **Notes:** controller automation alert.
- **Phrasing:** §5-13-3 Computer entry of flight plan info (altitude, route data, LIA, interim altitude).
  **Canonical:** —
  **Notes:** ERAM data entry workflow.
- **Phrasing:** §5-13-4 Entry of reported altitude (Mode C unavailable).
  **Canonical:** —
  **Notes:** data-entry workflow.
- **Phrasing:** §5-13-5 Selected altitude limits (display filters).
  **Canonical:** —
  **Notes:** display configuration.
- **Phrasing:** §5-13-6 Sector eligibility (OK function override).
  **Canonical:** —
  **Notes:** controller automation.
- **Phrasing:** §5-13-7 Coast tracks usage.
  **Canonical:** —
  **Notes:** automation policy.
- **Phrasing:** §5-13-8 Controller-initiated coast tracks (FLAT mode).
  **Canonical:** —
  **Notes:** automation workflow.
- **Phrasing:** §5-13-9 ERAM computer entry of hold information.
  **Canonical:** —
  **Notes:** ERAM data entry.
- **Phrasing:** §5-13-10 ERAM SAA visual indicator.
  **Canonical:** —
  **Notes:** display maintenance.

#### §5-14 — STARS Terminal

##### OutOfScope
- **Phrasing:** §5-14-1 through §5-14-9 — STARS application, responsibility, functional use list, system requirements, displayed info, CA/MCI/MSAW alert handling, track suspend, ARV alert handling.
  **Canonical:** —
  **Notes:** entire chapter is equipment/automation/display/track-ops workflow — out-of-pilot-scope per index header.

**Ch 5 totals:** Covered 30 · MissingRule 33 · MissingCanonical 62 · OutOfScope 37 · Phrasings 162

### Chapter 7 — Visual

#### §7-1 — General (Class A restrictions, VFR conditions, VFR holding)

##### MissingRule
- **Phrasing:** "Maintain VFR conditions"
  **Canonical:** `??`
  **Notes:** no `MaintainVfrConditions` canonical.
- **Phrasing:** "Maintain VFR conditions until (time or fix)"
  **Canonical:** `??`
  **Notes:** trigger-conditioned variant.
- **Phrasing:** "Maintain VFR conditions above/below (altitude)"
  **Canonical:** `??`
  **Notes:** altitude-bounded VFR conditions.
- **Phrasing:** "Climb/descend VFR" / "Climb VFR between (altitude) and (altitude)" / "Descend VFR above/below (altitude)"
  **Canonical:** `??`
  **Notes:** VFR climb/descent variant; not covered by `ClimbMaintain`/`DescendMaintain`.
- **Phrasing:** "If unable, (alternative procedure), and advise"
  **Canonical:** `??`
  **Notes:** conditional fallback clearance — recurring template across §7.

##### MissingCanonical
- **Phrasing:** "Hold at (location) until (time or other condition)" (visual holding at non-fix geographical point)
  **Canonical:** `??`
  **Notes:** `HoldAtFixHover/Left/Right` expect a fix name; visual holding at VFR checkpoints / landmarks has no canonical.
- **Phrasing:** "Traffic (description) holding at (fix, altitude if known)" / "proceeding to (fix) from (direction or fix)"
  **Canonical:** `??`
  **Notes:** no traffic-advisory canonical.

##### OutOfScope
- **Phrasing:** §7-1-1 Class A airspace restrictions.
  **Canonical:** —
  **Notes:** procedural/charting.
- **Phrasing:** §7-1-3 approach control service for VFR arrivals — workflow describing wind/runway/altimeter delivery and traffic information.
  **Canonical:** —
  **Notes:** ATIS-equivalent landing-info delivery.

#### §7-2 — Visual Separation

##### Covered
- **Phrasing:** "(ACID), report traffic in sight"
  **Canonical:** `ReportTrafficInSight`
  **Notes:** `PhraseologyRules.cs:226`.

##### MissingRule
- **Phrasing:** "(ACID), traffic, (clock position and distance), (direction) bound, (type of aircraft), (intentions and other relevant information)" — traffic advisory issuance
  **Canonical:** `??`
  **Notes:** no `IssueTraffic`/`TrafficAdvisory` canonical. Recurring gap (also §3-9, §3-10, §5-9).
- **Phrasing:** "Do you have it in sight?"
  **Canonical:** `ReportTrafficInSight` (?)
  **Notes:** interrogative form of "report traffic in sight"; no rule for "do you have ... in sight".
- **Phrasing:** "(ACID), maintain visual separation"
  **Canonical:** `??`
  **Notes:** no `MaintainVisualSeparation` canonical.
- **Phrasing:** "(ACID), approved" (short-form approval of pilot-initiated visual separation)
  **Canonical:** `??`
  **Notes:** bare "approved"; could conflict with pushback "approved".
- **Phrasing:** "(ACID), traffic, (clock/dist), (dir) bound, (type), has you in sight and will maintain visual separation"
  **Canonical:** `??`
  **Notes:** converging-course advisory.
- **Phrasing:** "Targets appear likely to merge" / "Radar targets appear likely to merge"
  **Canonical:** `??`
  **Notes:** merging-target advisory.

##### OutOfScope
- **Phrasing:** §7-2-1 general application/conditions (wake turbulence, lead-aircraft super restrictions).
  **Canonical:** —
  **Notes:** procedural rules.
- **Phrasing:** "Visual separation approved" / "(ACIDs) released" between adjacent towers / nonapproach tower and radar controller.
  **Canonical:** —
  **Notes:** controller-to-controller coordination.

#### §7-3 — VFR-On-Top

##### MissingRule
- **Phrasing:** "Maintain VFR-on-top"
  **Canonical:** `??`
  **Notes:** no `MaintainVfrOnTop` canonical.
- **Phrasing:** "Climb to and report reaching VFR-on-top"
  **Canonical:** `??`
  **Notes:** composite climb + report.
- **Phrasing:** "Tops reported (altitude)"
  **Canonical:** `??`
  **Notes:** informational advisory.
- **Phrasing:** "No tops reports" / "No tops report available"
  **Canonical:** `??`
  **Notes:** informational advisory.
- **Phrasing:** "If not on top at (altitude), maintain (altitude), and advise"
  **Canonical:** `??`
  **Notes:** conditional fallback with report-back.
- **Phrasing:** "Maintain VFR-on-top at or above/below/between (altitudes)"
  **Canonical:** `??`
  **Notes:** altitude-bounded VFR-on-top variant.
- **Phrasing:** "If unable, (alternative procedure), and advise"
  **Canonical:** `??`
  **Notes:** also at §7-1.
- **Phrasing:** "VFR-on-top cruising levels for your direction of flight are: odd/even altitudes/flight levels plus five hundred feet"
  **Canonical:** `??`
  **Notes:** non-compliance advisory per §7-3-2.

#### §7-4 — Visual Approaches

##### Covered
- **Phrasing:** "FLY HEADING (degrees)" / "TURN LEFT/RIGHT HEADING (degrees)" vectoring (visual-approach intent)
  **Canonical:** `FlyHeading` / `TurnLeft` / `TurnRight`
  **Notes:** `PhraseologyRules.cs:57-63`; "VECTOR FOR VISUAL APPROACH" tail is descriptive.
- **Phrasing:** "(Call sign) CLEARED VISUAL APPROACH RUNWAY (number)"
  **Canonical:** `ClearedVisualApproach`
  **Notes:** `PhraseologyRules.cs:205-206`.
- **Phrasing:** "(Call sign) follow (preceding traffic)"
  **Canonical:** `Follow`
  **Notes:** `PhraseologyRules.cs:220-221`.

##### MissingRule
- **Phrasing:** "Report runway in sight" (vs the covered "report airport/field in sight")
  **Canonical:** `ReportFieldInSight`
  **Notes:** existing rules cover field/airport-in-sight (`:224-225`); runway-specific variant not distinct in current canonical set.
- **Phrasing:** "CLEARED VISUAL APPROACH TO (airport name)" (uncontrolled-airport form, no runway)
  **Canonical:** `ClearedVisualApproach`
  **Notes:** every rule requires `runway {rwy}` — airport-name form has no rule.

##### MissingCanonical
- **Phrasing:** "WEATHER NOT AVAILABLE" / "VERIFY THAT YOU HAVE THE (airport) WEATHER"
  **Canonical:** `??`
  **Notes:** advisory appended to visual-approach clearance/vector.
- **Phrasing:** "(traffic position) 12 o'clock, (n) miles" with optional wake-turbulence type ("following a heavy Boeing 747")
  **Canonical:** `??`
  **Notes:** positional traffic call distinct from `Follow {callsign}`.
- **Phrasing:** "(Ident) CLEARED (name of CVFP) APPROACH" — Charted Visual Flight Procedure
  **Canonical:** `??`
  **Notes:** `ClearedVisualApproach` takes a runway not a named procedure; could fold into `ClearedApproach` or new `ClearedCharted Visual`.
- **Phrasing:** "(Ident) CLEARED RNAV VISUAL RUNWAY (number) APPROACH" — RNAV Visual Flight Procedure
  **Canonical:** `??`
  **Notes:** distinct from both `ClearedVisualApproach` and RNAV `ClearedApproach`.
- **Phrasing:** "CLEARED CONTACT APPROACH" with optional "AT OR BELOW (altitude) (routing)" / "IF NOT POSSIBLE, (alternative procedures), AND ADVISE"
  **Canonical:** `??`
  **Notes:** pilot-requested contact approach; distinct from visual and instrument.

##### OutOfScope
- **Phrasing:** §7-4-1 go-around handling narrative (pattern entry / climb to MIA after visual miss).
  **Canonical:** —
  **Notes:** GoAround already covered; remaining content is workflow.
- **Phrasing:** §7-4-4 simultaneous-approach 30° intercept geometry, parallel-runway sighting reports, wake-turbulence overtake prohibition.
  **Canonical:** —
  **Notes:** separation policy.

#### §7-5 — Special VFR

##### MissingCanonical
- **Phrasing:** "CLEARED TO ENTER/OUT OF/THROUGH (name) SURFACE AREA … MAINTAIN SPECIAL V-F-R CONDITIONS [AT OR BELOW (altitude)]"
  **Canonical:** `??`
  **Notes:** SVFR clearance into/out of/through Class B/C/D/E. Existing `ClearedBravoAirspace` family covers Class-B transit only. Needs new `ClearedSvfr` (or `SpecialVfrCleared`) canonical with surface-area + direction + optional altitude.
- **Phrasing:** "CLEARED FOR (coded arrival or departure procedure) ARRIVAL/DEPARTURE" appended to SVFR
  **Canonical:** `??`
  **Notes:** coded SVFR routing; likely subsumed by new SVFR canonical.
- **Phrasing:** "EXPECT (number) MINUTES DELAY"
  **Canonical:** `??`
  **Notes:** SVFR delay advisory. Also at §4-6.
- **Phrasing:** "MAINTAIN SPECIAL V-F-R CONDITIONS AT OR BELOW (altitude)"
  **Canonical:** `??`
  **Notes:** SVFR vertical-separation altitude; ClimbMaintain/DescendMaintain set fixed alts.
- **Phrasing:** "LOCAL SPECIAL V-F-R OPERATIONS IN THE IMMEDIATE VICINITY OF (name) AIRPORT ARE AUTHORIZED UNTIL (time). MAINTAIN SPECIAL V-F-R CONDITIONS."
  **Canonical:** `??`
  **Notes:** series authorization, rarely simulated.
- **Phrasing:** "CLIMB TO V-F-R WITHIN (name) SURFACE AREA / WITHIN (n) MILES FROM (airport) AIRPORT, MAINTAIN SPECIAL V-F-R CONDITIONS UNTIL REACHING V-F-R"
  **Canonical:** `??`
  **Notes:** climb-to-VFR authorization.
- **Phrasing:** "(Name of airport) VISIBILITY LESS THAN 1 MILE. ADVISE INTENTIONS."
  **Canonical:** `??`
  **Notes:** pilot-intentions query.

##### OutOfScope
- **Phrasing:** §7-5-1, §7-5-3, §7-5-7, §7-5-8 separation minima, weather-reporting requirements, IFR-priority rules, helicopter alternate-separation LOAs.
  **Canonical:** —
  **Notes:** policy/separation framework.
- **Phrasing:** §7-5-2 narrative on IFR-priority deferral of SVFR.
  **Canonical:** —
  **Notes:** workflow.
- **Phrasing:** §7-5-7a/b, §7-5-8a/b "clearance cannot be issued" administrative refusals.
  **Canonical:** —
  **Notes:** out-of-pilot-scope (refusal of service).

#### §7-6 — Basic Radar Service to VFR Aircraft (Terminal)

##### Covered
- **Phrasing:** "FOLLOW (description) (position, if necessary)"
  **Canonical:** `Follow`
  **Notes:** `PhraseologyRules.cs:220-221`.
- **Phrasing:** "SQUAWK ONE TWO ZERO ZERO" / "SQUAWK VFR"
  **Canonical:** `Squawk` / `SquawkVfr`
  **Notes:** `PhraseologyRules.cs:406-407`.

##### MissingRule
- **Phrasing:** "RADAR SERVICE TERMINATED"
  **Canonical:** `??`
  **Notes:** advisory-service termination; needs new canonical. Recurring across §7-6, §7-7, §7-9.
- **Phrasing:** "CHANGE TO ADVISORY FREQUENCY APPROVED"
  **Canonical:** `??` (closest: `FrequencyChangeApproved`)
  **Notes:** CTAF/advisory freq switch. Also at §4-8, §7-8.
- **Phrasing:** "FREQUENCY CHANGE APPROVED"
  **Canonical:** `FrequencyChangeApproved`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "CONTACT (frequency identification)"
  **Canonical:** `Contact`
  **Notes:** out-of-pilot-scope per index but used pervasively in §7.

##### OutOfScope
- **Phrasing:** §7-6-1 through §7-6-10 / §7-6-12 — workflow/policy (service application, coordination with tower, sequencing rules, identification before sequencing, tower-inop services).
  **Canonical:** —
  **Notes:** no pilot-facing phraseology.

#### §7-7 — Terminal Radar Service Area (TRSA)

##### Covered
- **Phrasing:** "SQUAWK ONE TWO ZERO ZERO"
  **Canonical:** `Squawk`
  **Notes:** `PhraseologyRules.cs:406`.

##### MissingRule
- **Phrasing:** "RESUME APPROPRIATE VFR ALTITUDES"
  **Canonical:** `??`
  **Notes:** cancels VFR altitude restriction; needs new canonical. Recurring across §7-7, §7-8, §7-9.
- **Phrasing:** "LEAVING THE (name) TRSA"
  **Canonical:** `??`
  **Notes:** airspace-exit announcement.
- **Phrasing:** "RESUME OWN NAVIGATION"
  **Canonical:** `??`
  **Notes:** also at §5-6.
- **Phrasing:** "REMAIN THIS FREQUENCY FOR TRAFFIC ADVISORIES"
  **Canonical:** `??`
  **Notes:** no canonical for "stay on this freq".
- **Phrasing:** "RADAR SERVICE TERMINATED"
  **Canonical:** `??`
  **Notes:** also at §7-6, §7-9.

##### OutOfScope
- **Phrasing:** §7-7-1, §7-7-2, §7-7-3, §7-7-4, §7-7-6 — policy/workflow (TRSA service application, EFC issuance, separation minima, helicopter exceptions, approach interval).
  **Canonical:** —
  **Notes:** no new phraseology.

#### §7-8 — Class C Service (Terminal)

##### MissingRule
- **Phrasing:** "REMAIN OUTSIDE CHARLIE AIRSPACE AND STANDBY"
  **Canonical:** `??`
  **Notes:** Class C entry-denial (inverse of `ClearedBravoAirspace` family); `AcknowledgePilotContact` covers "standby" half (line 188) but not "remain outside".
- **Phrasing:** "RESUME APPROPRIATE VFR ALTITUDES"
  **Canonical:** `??`
  **Notes:** also at §7-7, §7-9.
- **Phrasing:** "CHANGE TO ADVISORY FREQUENCY APPROVED"
  **Canonical:** `??`
  **Notes:** also at §7-6.
- **Phrasing:** "CONTACT (facility identification)"
  **Canonical:** `Contact`
  **Notes:** out-of-pilot-scope per index.

##### OutOfScope
- **Phrasing:** §7-8-1, §7-8-2, §7-8-3, §7-8-6, §7-8-7 — policy/workflow (Class C application, services list, separation minima, helicopter/balloon exceptions, adjacent airport ops).
  **Canonical:** —
  **Notes:** no phraseology.

#### §7-9 — Class B Service Area (Terminal)

##### Covered
- **Phrasing:** "CLEARED THROUGH BRAVO AIRSPACE"
  **Canonical:** `ClearedBravoAirspace`
  **Notes:** `PhraseologyRules.cs:181`.
- **Phrasing:** "CLEARED TO ENTER BRAVO AIRSPACE" / "CLEARED INTO BRAVO AIRSPACE" / "CLEARED OUT OF BRAVO AIRSPACE"
  **Canonical:** `ClearedBravoAirspace`
  **Notes:** `PhraseologyRules.cs:182-184`.
- **Phrasing:** "MAINTAIN (altitude) WHILE IN BRAVO AIRSPACE"
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:90`; "while in bravo airspace" is scope hint.

##### MissingRule
- **Phrasing:** "CLEARED THROUGH BRAVO AIRSPACE VIA (route)"
  **Canonical:** `ClearedBravoAirspace`
  **Notes:** rules don't capture optional `via {route}` argument.
- **Phrasing:** "CLEARED AS REQUESTED"
  **Canonical:** `??`
  **Notes:** generic affirmative response.
- **Phrasing:** "REMAIN OUTSIDE BRAVO AIRSPACE"
  **Canonical:** `??`
  **Notes:** Bravo entry denial (parallel to Charlie at §7-8).
- **Phrasing:** "LEAVING (name) BRAVO AIRSPACE"
  **Canonical:** `??`
  **Notes:** also at §7-7.
- **Phrasing:** "RESUME OWN NAVIGATION" / "REMAIN THIS FREQUENCY FOR TRAFFIC ADVISORIES" / "RADAR SERVICE TERMINATED" / "RESUME APPROPRIATE VFR ALTITUDES"
  **Canonical:** `??`
  **Notes:** same gaps as §7-6, §7-7, §7-8 — rolled up here.

##### OutOfScope
- **Phrasing:** §7-9-1, §7-9-3, §7-9-4, §7-9-5, §7-9-6, §7-9-8 — policy/workflow.
  **Canonical:** —
  **Notes:** no new phraseology.

**Ch 7 totals:** Covered 10 · MissingRule 39 · MissingCanonical 14 · OutOfScope 13 · Phrasings 76

### Chapter 2 — General Control

#### §2-1 — General

##### Covered
- **Phrasing:** "Standby" (operational-requests / acknowledge-and-respond-later)
  **Canonical:** `AcknowledgePilotContact`
  **Notes:** `PhraseologyRules.cs:188`.
- **Phrasing:** "Roger"
  **Canonical:** `AcknowledgePilotContact`
  **Notes:** `PhraseologyRules.cs:189`.

##### MissingRule
- **Phrasing:** "LOW ALTITUDE ALERT (call sign), CHECK YOUR ALTITUDE IMMEDIATELY" (+ optional MEA/MVA/MOCA/MIA appendix)
  **Canonical:** `SafetyAlert`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "TRAFFIC ALERT (call sign) (position) ADVISE YOU TURN LEFT/RIGHT (heading) [and/or] CLIMB/DESCEND (alt) IMMEDIATELY"
  **Canonical:** `SafetyAlert`
  **Notes:** canonical exists; no rule. Same gap as §5-9 PRM/SOIA breakout.
- **Phrasing:** "CONTACT (facility name or location name and terminal function), (frequency) [AT (time/fix/altitude)]"
  **Canonical:** `Contact`
  **Notes:** canonical exists; no rule. Out-of-pilot-scope per index but used pervasively. Also at §3-9, §4-7, §5-9, §5-10, §7-6, §7-8.
- **Phrasing:** "(Identification) CHANGE TO MY FREQUENCY (state frequency)"
  **Canonical:** `FrequencyChangeApproved`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "CAUTION WAKE TURBULENCE (traffic information)"
  **Canonical:** `WakeAdvisory`
  **Notes:** canonical exists; no rule.
- **Phrasing:** "TRAFFIC, (number) O'CLOCK, (number) MILES, (direction)-BOUND, (relative movement), (type/altitude)" / "(type) (number) FEET ABOVE/BELOW YOU" / "ALTITUDE UNKNOWN"
  **Canonical:** `SafetyAlert` (or possibly a dedicated `TrafficAdvisory`)
  **Notes:** controller-issued traffic call. Recurring across §3-8, §3-9, §3-10, §5-9, §7-2.
- **Phrasing:** "TRAFFIC NO FACTOR / NO LONGER OBSERVED"
  **Canonical:** `??`
  **Notes:** traffic-advisory clearing.
- **Phrasing:** "CHECK WHEELS DOWN" / "WHEELS SHOULD BE DOWN"
  **Canonical:** `??`
  **Notes:** USA/USN only; needs `CheckWheelsDown` if military in scope.

##### MissingCanonical
- **Phrasing:** "EXPEDITE" as a standalone modifier on a non-climb/descend instruction (e.g. "turn right heading 120 expedite")
  **Canonical:** `??`
  **Notes:** pure modifier; `Expedite` canonical exists but only as a climb/descend verb.
- **Phrasing:** "ATTENTION ALL AIRCRAFT, GPS REPORTED UNRELIABLE (or WAAS UNAVAILABLE) IN VICINITY/AREA (position)"
  **Canonical:** `??`
  **Notes:** bulk advisory broadcast.
- **Phrasing:** Formation flight construct — formation join/breakup tracking
  **Canonical:** `??`
  **Notes:** existing canonicals handle the controller verbs; formation as a single-track construct is unmodeled.
- **Phrasing:** "MONITOR TOWER" / "MONITOR GROUND" / "MONITOR GROUND POINT SEVEN"
  **Canonical:** `??`
  **Notes:** listen-only frequency transfer; distinct from `Contact`. Also at §5-9.
- **Phrasing:** "REMAIN THIS FREQUENCY"
  **Canonical:** `??`
  **Notes:** explicit instruction not to change frequency; pairs with `FrequencyChangeApproved` as its negation. Also at §7-7, §7-9.
- **Phrasing:** "(Requested operation) APPROVED" / "APPROVED AS REQUESTED"
  **Canonical:** `??`
  **Notes:** generic operational-request approval.
- **Phrasing:** "UNABLE (requested operation) [reason]"
  **Canonical:** `??`
  **Notes:** controller-issued denial.
- **Phrasing:** "U-A-S ACTIVITY, (clock), (miles), (direction)-bound, (type), (altitude)"
  **Canonical:** `??`
  **Notes:** UAS advisory broadcast.
- **Phrasing:** "FLOCK OF GEESE/BIRDS, (clock), (miles), (direction)-bound, (altitude)"
  **Canonical:** `??`
  **Notes:** bird-activity advisory.
- **Phrasing:** "(Identification) POSSIBLE PILOT DEVIATION ADVISE YOU CONTACT (facility) AT (telephone number)"
  **Canonical:** `??`
  **Notes:** Brasher notification; unlikely in sim.
- **Phrasing:** TCAS RA acknowledgement and "clear of conflict, returning to assigned altitude"
  **Canonical:** `??`
  **Notes:** TCAS handling not modeled.
- **Phrasing:** "UNABLE CLEARANCE INTO RVSM AIRSPACE" / "REPORT ABLE TO RESUME RVSM"
  **Canonical:** `??`
  **Notes:** RVSM phraseology; unlikely in sim scope.

##### OutOfScope
- **Phrasing:** §2-1-1, §2-1-2, §2-1-3, §2-1-4 ATC service framing, duty priority, procedural preference, operational priority categorization.
  **Canonical:** —
  **Notes:** policy.
- **Phrasing:** §2-1-7 INFLIGHT EQUIPMENT MALFUNCTIONS, §2-1-8 MINIMUM FUEL.
  **Canonical:** —
  **Notes:** pilot-initiated reports.
- **Phrasing:** §2-1-9, §2-1-10a, §2-1-11, §2-1-12 reporting policy, NAVAID malfunctions, MARSA, military procedures.
  **Canonical:** —
  **Notes:** ground-to-ground / annotation policy.
- **Phrasing:** §2-1-13 a-c, e FORMATION FLIGHTS dialog (uses existing canonicals).
  **Canonical:** —
  **Notes:** see MissingCanonical entry for formation construct.
- **Phrasing:** §2-1-14, §2-1-15, §2-1-16 coordinate use of airspace, control transfer, surface areas.
  **Canonical:** —
  **Notes:** ground-to-ground coordination.
- **Phrasing:** §2-1-19 WAKE TURBULENCE separation-application policy.
  **Canonical:** —
  **Notes:** policy; phraseology in §2-1-20.
- **Phrasing:** §2-1-24, §2-1-26 TRANSFER OF POSITION RESPONSIBILITY, SUPERVISORY NOTIFICATION.
  **Canonical:** —
  **Notes:** internal SOP.
- **Phrasing:** §2-1-29 a-d, f, g RVSM operations (equipment-suffix/coordination policy).
  **Canonical:** —
  **Notes:** policy.
- **Phrasing:** §2-1-30 TAWS ALERTS, §2-1-31 "BLUE LIGHTNING" EVENTS.
  **Canonical:** —
  **Notes:** emergency/IRROPS framing.

#### §2-2 — Flight Plans and Control Information

##### MissingCanonical
- **Phrasing:** "(Identification), REVISED (revised information)" (§2-2-11 amendment phraseology)
  **Canonical:** `??`
  **Notes:** inter-facility coordination on amended flight-plan data, not pilot-bound. Listed because it's the only PHRASEOLOGY block in §2-2.

##### OutOfScope
- **Phrasing:** §2-2-1 through §2-2-15 (excluding §2-2-11) — flight-plan recording, forwarding, VFR/DVFR data, military DVFR, IFR/VFR flight-plan change, IFR flight progress data, computer messages, ALTRV, NADIN, teletype format, NRP.
  **Canonical:** —
  **Notes:** controller-to-controller data forwarding / automation.

#### §2-3 — Flight Progress Strips

##### OutOfScope
- **Phrasing:** §2-3-1 through §2-3-10 — strip handling, en-route/oceanic/terminal data entries, aircraft identity, aircraft type, USAF/USN undergraduate pilots, equipment suffix, clearance status, control symbology.
  **Canonical:** —
  **Notes:** flight-strip recording workflow / data format. TL/TR/RH/PT/SI/MA/CT/VA tokens appear as spoken phraseology elsewhere and are audited there.

#### §2-4 — Radio and Interphone Communications

##### Covered
- **Phrasing:** §2-4-22 — "cleared to enter Bravo airspace"
  **Canonical:** `ClearedBravoAirspace`
  **Notes:** `PhraseologyRules.cs:181-185`.

##### OutOfScope
- **Phrasing:** §2-4-1 through §2-4-21 — radio comms policy, monitoring, pilot read-back, authorized interruptions/transmissions/relays, message format, abbreviated transmissions, interphone priorities, words and phrases (super/heavy), emphasis for clarity, ICAO phonetics, numbers usage (niner), number clarification, facility identification, aircraft identification (callsign construction), description of aircraft types.
  **Canonical:** —
  **Notes:** mostly meta-phraseology (how to format/pronounce) — relevant to STT callsign resolution and number parsing, not to phraseology rules. "Niner" vs "nine" is a known rule (see project memory).

#### §2-5 — Route and NAVAID Description

##### OutOfScope
- **Phrasing:** Victor/J/Q/Tango airway descriptions, colored/L/MF airways, named routes, ATS phonetic-letter+number routes, Military Training Routes (IR/VR), NAVAID radial/arc/bearing/azimuth/quadrant descriptions, unnamed NAVAID fix descriptions.
  **Canonical:** —
  **Notes:** clearance-limit/route construction phraseology; airway/route handling is covered by individual `JoinAirway`/`JoinRadial*` canonicals in §4-4, not separate route-description phrasings.

#### §2-6 — Weather Information

##### MissingRule
- **Phrasing:** "Deviation [restrictions] approved, maintain (altitude)"
  **Canonical:** `??` + `ClimbMaintain`/`DescendMaintain`
  **Notes:** weather deviation approval; deviation-approval semantic missing. Also at §4-5, §5-6.
- **Phrasing:** "Deviation approved, when able, proceed direct (fix)"
  **Canonical:** `AppendDirectTo` (direct piece covered at `:119`) + `??`
  **Notes:** deviation-approval framing missing.
- **Phrasing:** "Deviation approved, when able, fly heading (degrees), vector to join (airway)"
  **Canonical:** `JoinAirway` + `??`
  **Notes:** `JoinAirway` no rule (see §4-4); deviation-approval framing missing.
- **Phrasing:** "Deviation approved, advise clear of weather"
  **Canonical:** `??`
  **Notes:** post-deviation report-back.
- **Phrasing:** "Unable requested deviation, fly heading (heading), advise clear of weather"
  **Canonical:** `FlyHeading` + `??`
  **Notes:** deviation-unable + advisory.
- **Phrasing:** "Unable requested deviation, turn (deg) degrees (left/right) vector for traffic"
  **Canonical:** `RelativeLeft`/`RelativeRight` + `??`
  **Notes:** deviation-unable + advisory.

##### OutOfScope
- **Phrasing:** "Weather/chaff area between X o'clock and Y o'clock, Z miles" / "Area of (intensity) precipitation between X and Y, moving (dir) at Z knots, tops (alt)" / "Attention all aircraft. Hazardous weather information…" / PIREP solicitation forms / wind shear PIREP relay.
  **Canonical:** —
  **Notes:** broadcast advisories / informational relays.

#### §2-7 — Altimeter Settings

##### OutOfScope
- **Phrasing:** "The (facility) altimeter (setting)" / "Altimeter (setting) high barometric pressure restrictions in effect, set 3100 until reaching the final approach fix" / variants.
  **Canonical:** —
  **Notes:** weather data issuance, not a command verb.

#### §2-8 — Runway Visibility Reporting

##### OutOfScope
- **Phrasing:** "Runway (rwy) RVR (value)" and all variants (rollout, more/less than, variable range).
  **Canonical:** —
  **Notes:** visibility advisory.

#### §2-9 — Automatic Terminal Information Service Procedures

##### OutOfScope
- **Phrasing:** "Verify you have information (letter)" / "Information (letter) current. Advise when you have (letter)" / "Attention all aircraft, information (letter) current" / full ATIS broadcast content / runway-shortened warnings / RwyCC ATIS content / unauthorized laser illumination / MANPADS alerts.
  **Canonical:** —
  **Notes:** ATIS playback / ATIS-change broadcast.

#### §2-10 — Team Position Responsibilities

##### OutOfScope
- **Phrasing:** All §2-10-1/§2-10-2/§2-10-3 content (R/RA/RC/FD/NR/LC/GC/CD position duty descriptions).
  **Canonical:** —
  **Notes:** facility/staffing doctrine — no pilot-spoken phraseology.

**Ch 2 totals:** Covered 3 · MissingRule 14 · MissingCanonical 13 · OutOfScope 18 · Phrasings 48

### Chapter 6 — Nonradar

#### §6-1 — General (Distance, Position Reports, Adjacent Airport Operation, Arrival Minima)

##### OutOfScope
- **Phrasing:** Mileage-based (DME/ATD) procedure applicability (§6-1-1).
  **Canonical:** —
  **Notes:** workflow / separation rule.
- **Phrasing:** Non-receipt of position report 5-minute action (§6-1-2) / Duplicate position reports prohibition (§6-1-3).
  **Canonical:** —
  **Notes:** workflow.
- **Phrasing:** Adjacent airport wake-turbulence separation (§6-1-4) / Arrival wake-turbulence minima (§6-1-5).
  **Canonical:** —
  **Notes:** separation rules.

#### §6-2 — Initial Separation of Successive Departing Aircraft

##### OutOfScope
- **Phrasing:** Diverging-course minima after takeoff (§6-2-1) / Same-course departure climb-through minima (§6-2-2).
  **Canonical:** —
  **Notes:** separation rules, no phraseology.

#### §6-3 — Initial Separation of Departing and Arriving Aircraft

##### OutOfScope
- **Phrasing:** Departure vs IFR-arrival separation minima (§6-3-1).
  **Canonical:** —
  **Notes:** separation rule.

#### §6-4 — Longitudinal Separation

##### MissingCanonical
- **Phrasing:** "CROSS (fix) AT OR BEFORE (time)" / "CROSS (fix) AT OR AFTER (time)" (§6-4-1.b)
  **Canonical:** `??`
  **Notes:** time-of-arrival crossing; existing `CrossFix` is altitude-bound. Would need new `CrossFixAtTime` etc.
- **Phrasing:** "Hold at (fix) until (time)" implied by §6-4-1.c
  **Canonical:** `??`
  **Notes:** existing `HoldAtFix*` don't accept EFC/release time argument. Also at §4-6.
- **Phrasing:** "Depart (fix) at (time)" implied by §6-4-1.a
  **Canonical:** `??`
  **Notes:** `DepartFix` exists but not time-parameterised.
- **Phrasing:** "Change altitude at (time)" / "Change altitude at (fix)" implied by §6-4-1.d
  **Canonical:** `??`
  **Notes:** fix form may be parser-level covered via `AT` trigger; time form is the gap.
- **Phrasing:** "MAINTAIN AT LEAST ONE ZERO MINUTES SEPARATION FROM (ident)" / "MAINTAIN AT LEAST TWO ZERO MILES SEPARATION FROM (ident)" (§6-4-4)
  **Canonical:** `??`
  **Notes:** pilot-applied longitudinal separation; rarely modelled in trainers.
- **Phrasing:** "USE DME DISTANCES" (§6-4-5)
  **Canonical:** `??`
  **Notes:** equipment-mode advisory; likely permanent OutOfScope but listed per FAA PHRASEOLOGY block.

##### OutOfScope
- **Phrasing:** Same/converging/crossing course speed-and-distance minima tables (§6-4-2) / Opposite-course altitude-then-pass minima (§6-4-3).
  **Canonical:** —
  **Notes:** separation rules.

#### §6-5 — Lateral Separation

##### MissingCanonical
- **Phrasing:** "Via (n) mile arc (direction) of (NAVAID)"
  **Canonical:** `??` (DmeArc)
  **Notes:** DME-arc / arc-intercept clearance; no canonical.

##### OutOfScope
- **Phrasing:** §6-5-1, §6-5-2, §6-5-4, §6-5-5 — separation methods, diverging-radial minima, off-airway minima, RNAV diverging/crossing minima.
  **Canonical:** —
  **Notes:** separation rules.

#### §6-6 — Vertical Separation

##### Covered
- **Phrasing:** "Say altitude"
  **Canonical:** `SayAltitude`
  **Notes:** `PhraseologyRules.cs:519-520`.

##### MissingRule
- **Phrasing:** "Say altitude or flight level" / "Say flight level"
  **Canonical:** `SayAltitude`
  **Notes:** "flight level" variant not in rules. Also at §5-2.

##### MissingCanonical
- **Phrasing:** "Report leaving (altitude)" / "Report reaching (altitude)"
  **Canonical:** `??`
  **Notes:** no `ReportLeavingAltitude` / `ReportReachingAltitude` canonical.
- **Phrasing:** "Report leaving odd/even altitudes/flight levels"
  **Canonical:** `??`
  **Notes:** niche; no canonical.

##### OutOfScope
- **Phrasing:** §6-6-1 application, §6-6-2 exceptions (incl. "Cleared to CRUISE (altitude)" — `Cruise` canonical addressed in §4-5), §6-6-3 separation by pilots.
  **Canonical:** —
  **Notes:** separation rules / procedural.

#### §6-7 — Timed Approaches

##### MissingCanonical
- **Phrasing:** "Leave (fix) inbound at (time)" / "depart (fix) at (time)"
  **Canonical:** `??` (DepartFixAtTime)
  **Notes:** timed-approach release; `DepartFix` exists but time argument unclear. Also at §6-4.
- **Phrasing:** "Cross (FAF/OM) at (time)"
  **Canonical:** `??` (CrossFixAtTime)
  **Notes:** time-based crossing; also at §6-4.
- **Phrasing:** "Time check, (time)"
  **Canonical:** `??` (TimeCheck)
  **Notes:** pre-timed-approach time sync.

##### OutOfScope
- **Phrasing:** §6-7-1 through §6-7-7 — eligibility, sequence, interruption, level-flight restriction, interval minima, time check procedure, missed approaches.
  **Canonical:** —
  **Notes:** procedural / separation rules.

**Ch 6 totals:** Covered 1 · MissingRule 1 · MissingCanonical 12 · OutOfScope 9 · Phrasings 23

### Chapter 9 — Special Flights

#### §9-1 — General (Flight Inspection / Flight Check)

##### MissingCanonical
- **Phrasing:** "Flight Check (callsign)" / "recorded run" / "FLIGHT VAL" callsign semantics
  **Canonical:** `??`
  **Notes:** military/edge — defer to product (special handling of FLC aircraft; callsign-only, no controller phrasing).

##### OutOfScope
- **Phrasing:** §9-1-1 General assistance / §9-1-2 Special handling / §9-1-3 Flight Check Aircraft restrictions.
  **Canonical:** —
  **Notes:** workflow / track-data ops.

#### §9-2 — Special Operations

##### MissingCanonical
- **Phrasing:** §9-2-6 "CLEARED INTO IR (designator)"
  **Canonical:** `??`
  **Notes:** military/edge — defer to product.
- **Phrasing:** §9-2-6 "MAINTAIN IR (designator) ALTITUDE(S)" / "MAINTAIN AT OR BELOW (altitude)" / "CRUISE (altitude)" (MTR-context)
  **Canonical:** `??`
  **Notes:** military/edge — bare `MAINTAIN`/`CRUISE` covered elsewhere; MTR-context wrapper defer to product.
- **Phrasing:** §9-2-6 "CROSS (fix) AT OR LATER THAN (time)"
  **Canonical:** `??`
  **Notes:** military/edge — same time-restricted crossing gap as §6-4.
- **Phrasing:** §9-2-6 "CLEARED TO (destination/clearance limit) FROM IR (designator/exit fix) VIA (route)"
  **Canonical:** `??`
  **Notes:** military/edge — defer to product.
- **Phrasing:** §9-2-6 "(Call sign) VERIFY YOUR EXIT FIX ESTIMATE AND REQUESTED ALTITUDE AFTER EXIT"
  **Canonical:** `??`
  **Notes:** military/edge.
- **Phrasing:** §9-2-10 "(ACID) TRANSPONDER OBSERVED PROCEED ON COURSE/AS REQUESTED; REMAIN OUTSIDE (class) AIRSPACE"
  **Canonical:** `??`
  **Notes:** DC SFRA security tracking.
- **Phrasing:** §9-2-10 "(Call sign) REPORT LANDING OR LEAVING THE SFRA"
  **Canonical:** `??`
  **Notes:** SFRA exit report request.
- **Phrasing:** §9-2-10 "(A/C call sign) REMAIN OUTSIDE OF THE (location) AND STANDBY. EXPECT (time) MINUTES DELAY"
  **Canonical:** `??`
  **Notes:** SFRA hold-outside.
- **Phrasing:** §9-2-10 "(ACID) REMAIN ON YOUR ASSIGNED TRANSPONDER CODE UNTIL YOU LAND, FREQUENCY CHANGE APPROVED"
  **Canonical:** `??`
  **Notes:** SFRA-landing transponder retention.
- **Phrasing:** §9-2-13 "CLEARED TO CONDUCT REFUELING ALONG (number) TRACK / FROM (fix) TO (fix), MAINTAIN BLOCK (altitude) THROUGH (altitude)"
  **Canonical:** `??`
  **Notes:** military/edge — aerial refueling clearance + block altitude. Also at §4-5 MAINTAIN BLOCK.
- **Phrasing:** §9-2-13 "REPORT A-R-I-P / A-R-C-P / EGRESS FIX"
  **Canonical:** `??`
  **Notes:** military/edge — refueling track fix reports.
- **Phrasing:** §9-2-14 "CLEARED AS FILED VIA ROUTE AND FLIGHT LEVELS"
  **Canonical:** `??`
  **Notes:** FL 600+ supersonic clearance.
- **Phrasing:** §9-2-20 "CLEARED TO CONDUCT EVASIVE ACTION MANEUVER FROM (fix) TO (fix), (number) MILES EITHER SIDE OF CENTERLINE..."
  **Canonical:** `??`
  **Notes:** military/edge — bomber zigzag maneuver.

##### OutOfScope
- **Phrasing:** §9-2-1 through §9-2-21 (excluding entries above) — dangerous-materials handling, celestial nav training, experimental ops, R&D, FLYNET pilot reports, interceptor ops, special-interest sites, SATR/SFRA basics, SECNOT, law enforcement, military special frequencies, nuclear radiation avoidance, SAMP, AWACS/NORAD, weather recon, nonstandard formation/cell.
  **Canonical:** —
  **Notes:** workflow / coordination / handling guidance.

#### §9-3 — Special Use, ATC-Assigned Airspace, and Stationary ALTRVs

##### MissingCanonical
- **Phrasing:** "MAINTAIN VFR-ON-TOP AT LEAST 500 FEET ABOVE/BELOW (limit) ACROSS (airspace) BETWEEN (fix) AND (fix)"
  **Canonical:** `??`
  **Notes:** VFR-on-top routing around SUA/ATCAA.
- **Phrasing:** "(name of ATCAA) IS ATC ASSIGNED AIRSPACE"
  **Canonical:** `??`
  **Notes:** SUA/ATCAA advisory.

##### OutOfScope
- **Phrasing:** §9-3-1, §9-3-2, §9-3-4 — application criteria, separation minima, transit coordination.
  **Canonical:** —
  **Notes:** workflow / separation.

#### §9-4 — Fuel Dumping

##### MissingCanonical
- **Phrasing:** "ATTENTION ALL AIRCRAFT. FUEL DUMPING IN PROGRESS OVER (location) AT (altitude) BY (type) (direction)"
  **Canonical:** `??`
  **Notes:** broadcast advisory.
- **Phrasing:** "ATTENTION ALL AIRCRAFT. FUEL DUMPING OVER (location) TERMINATED"
  **Canonical:** `??`
  **Notes:** broadcast advisory termination.

##### OutOfScope
- **Phrasing:** §9-4-1 through §9-4-4 — info gathering, routing, altitude, separation minima.
  **Canonical:** —
  **Notes:** workflow.

#### §9-5 — Jettisoning of External Stores

##### OutOfScope
- **Phrasing:** §9-5-1 vectoring service eligibility / written-agreement criteria.
  **Canonical:** —
  **Notes:** no PHRASEOLOGY block.

#### §9-6 — Unmanned Free Balloons

##### MissingCanonical
- **Phrasing:** "UNMANNED FREE BALLOON OVER/ESTIMATED OVER (location), MOVING (direction). LAST REPORTED ALTITUDE AT (altitude) / ALTITUDE UNKNOWN"
  **Canonical:** `??`
  **Notes:** traffic advisory for free balloon.
- **Phrasing:** "ADVISORY TO ALL AIRCRAFT. DERELICT BALLOON REPORTED/ESTIMATED/RADAR REPORTED IN VICINITY OF/OVER (location). LAST REPORTED ALTITUDE/FLIGHT LEVEL AT (altitude) / ALTITUDE/FLIGHT LEVEL UNKNOWN"
  **Canonical:** `??`
  **Notes:** derelict balloon broadcast advisory.

##### OutOfScope
- **Phrasing:** §9-6-1 strip posting, radar flight-following, transfer-of-responsibility / §9-6-2 derelict handling workflow.
  **Canonical:** —
  **Notes:** workflow.

#### §9-7 — Parachute Operations

##### OutOfScope
- **Phrasing:** §9-7-1 through §9-7-4 — Class A/B/C/D/E coordination, authorization, and advisory issuance.
  **Canonical:** —
  **Notes:** workflow / authorization procedures; section prescribes "issue advisory" / "issue traffic advisory" but no specific phraseology templates. Any actual wording would map to traffic-advisory canonicals — defer to product.

#### §9-8 — Unidentified Anomalous Phenomena (UAP) Reports

##### OutOfScope
- **Phrasing:** §9-8-1 internal reporting to operations supervisor/CIC.
  **Canonical:** —
  **Notes:** no controller→pilot phraseology.

**Ch 9 totals:** Covered 0 · MissingRule 0 · MissingCanonical 20 · OutOfScope 8 · Phrasings 28

---

## AIM — Pilot-side

### Chapter 4 — Air Traffic Control

#### AIM §4-1 — Services Available to Pilots

##### Covered
- **Phrasing:** "(Facility), (callsign), (position/altitude), landing (airport). I have the automated weather, request airport advisory." (§4-1-9 LAA inbound)
  **Canonical:** — (pilot self-broadcast)
  **Notes:** pilot-side self-broadcast on CTAF; instructor doesn't speak this to the sim.
- **Phrasing:** "Information (code) received." / "(Code) received." (§4-1-13, §4-1-14 ATIS/AFIS acknowledgment)
  **Canonical:** — (pilot readback)
  **Notes:** pilot-side; YAAT's solo-pilot speech already produces ATIS code per `docs/solo-training-pilot-speech.md`.

##### MissingRule
- **Phrasing:** "have numbers" (§4-1-8, §4-1-13, §4-1-18) — pilot declaration of wind/runway/altimeter received but not full ATIS
  **Canonical:** `??`
  **Notes:** pilot-side.
- **Phrasing:** "negative radar service" (§4-1-18-a-1) / "negative TRSA service" (§4-1-18-b-2)
  **Canonical:** `??`
  **Notes:** pilot opt-out of basic services.
- **Phrasing:** "request radar traffic information" / "request traffic information"
  **Canonical:** `??`
  **Notes:** pilot request for traffic advisories.
- **Phrasing:** "request MSAW monitoring" (§4-1-16-a-2)
  **Canonical:** `??`
  **Notes:** VFR pilot request for MSAW.
- **Phrasing:** "request airport advisory" / "request airport information" / "request wind and runway information"
  **Canonical:** `??`
  **Notes:** pilot CTAF/FSS/UNICOM requests.
- **Phrasing:** "low altitude alert (callsign), check your altitude immediately" (§4-1-16-a-1)
  **Canonical:** `SafetyAlert`
  **Notes:** canonical exists; no STT rule. Also at 7110.65 §2-1, §5-9.
- **Phrasing:** "(callsign), traffic alert, advise you turn (left/right) heading (degrees) and/or climb/descend to (altitude) immediately" (§4-1-16-b-1)
  **Canonical:** `SafetyAlert`
  **Notes:** controller composite. Also at §5-9 PRM/SOIA.
- **Phrasing:** "traffic (clock-position), (distance), (direction), (type/altitude)" / "traffic (distance) (cardinal) of (fix), (direction), (type)"
  **Canonical:** `??`
  **Notes:** controller traffic advisory. Recurring across 7110.65 §2-1, §3-8, §3-9, §3-10, §5-9, §7-2.
- **Phrasing:** "resume appropriate VFR altitudes" (§4-1-18-b-7)
  **Canonical:** `??`
  **Notes:** TRSA altitude release. Also at 7110.65 §7-7, §7-8, §7-9.
- **Phrasing:** "stop altitude squawk" / "stop altitude squawk, altitude differs by (number) feet"
  **Canonical:** `??`
  **Notes:** Mode-C control. Also at 7110.65 §5-2.
- **Phrasing:** "stop squawk (mode)"
  **Canonical:** `SquawkStandby`
  **Notes:** full-stop variant covered by `squawk standby` (line 409).
- **Phrasing:** "squawk altitude"
  **Canonical:** `??` (Mode-C-on variant of SquawkNormal)
  **Notes:** would need distinct canonical only if Mode-C-on modelled separately.
- **Phrasing:** "squawk (code) and ident"
  **Canonical:** `Squawk` + `Ident`
  **Notes:** composite expressible via `,` parser separator.

##### OutOfScope
- **Phrasing:** §4-1-1 through §4-1-23 (excluding above) — facility-type descriptions, ATIS/AFIS broadcasts, UNICOM/MULTICOM examples, IFR/ground-vehicle ops, TRSA service descriptions, TEC, ADS-B/transponder identity affixes, airport reservation, waivers, WSP.
  **Canonical:** —
  **Notes:** mostly procedural/equipment background.

#### AIM §4-2 — Radio Communications Phraseology and Techniques

##### Covered
- **Phrasing:** §4-2-3 "Roger" / "Wilco" / "Affirmative"
  **Canonical:** `AcknowledgePilotContact` (for "roger")
  **Notes:** `PhraseologyRules.cs:189`; Wilco/Affirmative fold into same readback semantics.
- **Phrasing:** §4-2-7 Phonetic alphabet
  **Canonical:** —
  **Notes:** consumed by `NatoNearMissResolver` (recent work).
- **Phrasing:** §4-2-9 Altitudes / flight levels speech form
  **Canonical:** —
  **Notes:** consumed by `AltitudeResolver`.
- **Phrasing:** §4-2-10 "heading {hdg}" / "fly heading {hdg}"
  **Canonical:** `FlyHeading`
  **Notes:** `PhraseologyRules.cs:62-63`.
- **Phrasing:** §4-2-11 Speeds "two five zero knots" / "Mach point seven"
  **Canonical:** `Speed` / `Mach`
  **Notes:** `PhraseologyRules.cs:92-107`.

##### MissingRule
- **Phrasing:** §4-2-3.4 Pilot frequency-change readback ("United Two Twenty-Two on one three four point five")
  **Canonical:** `??`
  **Notes:** no `FrequencyReadback` canonical; `Contact` exists for controller side.

##### MissingCanonical
- **Phrasing:** §4-2-3.1 Pilot initial-contact callup with position/intent ("New York Radio, Mooney 311E, request VFR traffic advisories")
  **Canonical:** `??`
  **Notes:** free-form pilot check-in including "with information Charlie" / "ready to taxi IFR to {dest}".
- **Phrasing:** §4-2-3 "ATIS Information {code} received" / "with {code}"
  **Canonical:** `??`
  **Notes:** pilot-reporting ATIS.
- **Phrasing:** §4-2-4.1 "Verify clearance for (call sign)"
  **Canonical:** `??`
  **Notes:** pilot disambiguating own clearance.
- **Phrasing:** §4-2-4.3 "student pilot" appended to callsign
  **Canonical:** `??`
  **Notes:** pilot social flag.
- **Phrasing:** §4-2-14 Pilot position report on initial VFR contact ("over Springfield VOR, over")
  **Canonical:** `??`
  **Notes:** overlaps `SayPosition` (controller-prompted) but distinct as pilot-initiated.

##### OutOfScope
- **Phrasing:** §4-2-1, §4-2-2, §4-2-4.2/.5/.6, §4-2-5, §4-2-6, §4-2-8, §4-2-12, §4-2-13 — philosophy, mic technique, MEDEVAC prefix, carrier/military callsign formats, leased aircraft, ground station call signs, figures, time, inop tx/rx light signals.
  **Canonical:** —
  **Notes:** procedural / STT/numeric / emergency.

#### AIM §4-3 — Airport Operations

##### Covered
- **Phrasing:** §4-3-2.3 Traffic-pattern terminology (departure/upwind/crosswind/downwind/base/final)
  **Canonical:** Pattern entries and turn-to-leg verbs
  **Notes:** `PhraseologyRules.cs:325-362`.
- **Phrasing:** §4-3-2.4 "Squawk ident" for tower radar ID
  **Canonical:** `Ident`
  **Notes:** `PhraseologyRules.cs:410-411`.
- **Phrasing:** §4-3-2.4 "Proceed southwestbound, enter a right downwind runway 30" / suggested heading
  **Canonical:** `EnterRightDownwind` + `FlyHeading`
  **Notes:** `PhraseologyRules.cs:62, 326-330`.
- **Phrasing:** §4-3-3 Straight-in approaches ("make straight in [approach] runway X")
  **Canonical:** `EnterFinal`
  **Notes:** `PhraseologyRules.cs:344-352`; recent work.
- **Phrasing:** §4-3-5 360-degree turn for spacing ("make left/right 360")
  **Canonical:** `MakeLeft360` / `MakeRight360`
  **Notes:** `PhraseologyRules.cs:369-372`.
- **Phrasing:** §4-3-11 LAHSO read-back ("cleared to land runway 6R, hold short of taxiway B")
  **Canonical:** `LandAndHoldShort`
  **Notes:** `PhraseologyRules.cs:157-159`; recent work.
- **Phrasing:** §4-3-11 "Cross runway 6R at taxiway B, landing aircraft will hold short"
  **Canonical:** `CrossRunway` + `HoldShort`
  **Notes:** `PhraseologyRules.cs:500-502`.
- **Phrasing:** §4-3-12 Low approach ("cleared for low approach")
  **Canonical:** `LowApproach`
  **Notes:** `PhraseologyRules.cs:176-177`.
- **Phrasing:** §4-3-17 Helicopter air taxi
  **Canonical:** `AirTaxi`
  **Notes:** `PhraseologyRules.cs:395-397`.
- **Phrasing:** §4-3-17 Helicopter "Cleared for takeoff from (taxiway/helipad/runway), make right/left turn for departure"
  **Canonical:** `ClearedTakeoffPresent` + `MakeLeftTraffic`/`MakeRightTraffic`
  **Notes:** `PhraseologyRules.cs:398-399, 353-358`.
- **Phrasing:** §4-3-18 Taxi clearance read-back
  **Canonical:** `Taxi`
  **Notes:** `PhraseologyRules.cs:444-469`.
- **Phrasing:** §4-3-21 Exit runway at first/instructed taxiway
  **Canonical:** `ExitLeft` / `ExitRight` / `ExitTaxiway`
  **Notes:** `PhraseologyRules.cs:505-509`.
- **Phrasing:** §4-3-23 "Cleared for the option"
  **Canonical:** `ClearedForOption`
  **Notes:** `PhraseologyRules.cs:178-179`.

##### MissingRule
- **Phrasing:** §4-3-2.1 Pilot tower-arrival check-in (~15 miles out)
  **Canonical:** `??`
  **Notes:** "Tower, {callsign} {pos} miles {direction} with information {code}".
- **Phrasing:** §4-3-10 Intersection takeoff request ("at the intersection of taxiway O and runway 23R, ready for departure")
  **Canonical:** `??`
  **Notes:** no `ReadyForDeparture` / `ReadyAtIntersection`.
- **Phrasing:** §4-3-10 "Request waiver to 3-minute interval" (wake-turbulence waiver request)
  **Canonical:** `??`
  **Notes:** pilot wake-waiver request.
- **Phrasing:** §4-3-10 Pilot readback of "Hold for wake turbulence"
  **Canonical:** `??`
  **Notes:** overlaps `HoldShort` but trigger is wake; also at 7110.65 §3-7, §3-9.
- **Phrasing:** §4-3-11 "Unable to LAHSO" / pilot declining LAHSO
  **Canonical:** `??`
  **Notes:** no rule for "unable"/"negative" rejecting LAHSO.
- **Phrasing:** §4-3-18.4 "Ready to taxi, IFR to {destination}"
  **Canonical:** `??`
  **Notes:** pilot first-call requesting taxi clearance.
- **Phrasing:** §4-3-18.4 "Clearing runway 1R on taxiway E3, request clearance to {ramp}"
  **Canonical:** `??`
  **Notes:** pilot post-landing request for taxi to parking.
- **Phrasing:** §4-3-22 "Request practice {approach}" / "practice ILS Runway 14 approach"
  **Canonical:** `??`
  **Notes:** pilot request for practice instrument approach.
- **Phrasing:** §4-3-27 "I have the {airport} one-minute weather, request an ILS Runway 14 approach"
  **Canonical:** `??`
  **Notes:** uncontrolled-field weather-acknowledged request.

##### MissingCanonical
- **Phrasing:** §4-3-2.4 "Suggested heading two two zero, for radar identification"
  **Canonical:** `??`
  **Notes:** non-binding advisory heading; `FlyHeading` treats heading as clearance.
- **Phrasing:** §4-3-3 Departing the pattern with 45° turn ("depart pattern 45 [left|right]")
  **Canonical:** `??`
  **Notes:** pilot exit declaration.
- **Phrasing:** §4-3-6.3 Pilot runway-preference request ("request runway {N}")
  **Canonical:** `??`
  **Notes:** pilot runway request.
- **Phrasing:** §4-3-8 / §4-3-9 Pilot-reported braking action ("braking action poor the first/last half of the runway")
  **Canonical:** `??`
  **Notes:** PIREP-style post-landing report.
- **Phrasing:** §4-3-10 Intersection-departure distance request
  **Canonical:** `??`
  **Notes:** "request distance from intersection to runway end".
- **Phrasing:** §4-3-14 Frequency-omission readback ("contact ground point seven")
  **Canonical:** `??`
  **Notes:** controller drops "121." prefix; pilot reply.
- **Phrasing:** §4-3-20 STR (Standard Taxi Route) request ("request STR {name}")
  **Canonical:** `??`
  **Notes:** STR program.
- **Phrasing:** §4-3-22 Pilot pre-declaring approach termination intent ("touch and go" / "low approach" / "full stop" / "missed")
  **Canonical:** `??`
  **Notes:** pilot intent declaration before being cleared.

##### OutOfScope
- **Phrasing:** §4-3-1, §4-3-2.1-.2/.5, §4-3-2.4 surveillance practices, §4-3-3.1-.5, §4-3-4, §4-3-5 commentary, §4-3-6, §4-3-7, §4-3-8/§4-3-9 advisories, §4-3-13, §4-3-14 frequency-management background, §4-3-15-19, §4-3-20 description, §4-3-24-26, §4-3-27 ASOS/AWOS coverage.
  **Canonical:** —
  **Notes:** procedural/equipment background.

#### AIM §4-4 — ATC Clearances and Aircraft Separation

##### Covered
- **Phrasing:** §4-4-7-b "cleared to land runway 9L" — readback
  **Canonical:** `ClearedToLand`
  **Notes:** `PhraseologyRules.cs:148-152`.
- **Phrasing:** §4-4-10-a-5 "descend and maintain six thousand"
  **Canonical:** `DescendMaintain`
  **Notes:** `PhraseologyRules.cs:78, 90`.
- **Phrasing:** §4-4-12-a-5 "Descend and maintain (altitude); then, reduce speed to (speed)"
  **Canonical:** `DescendMaintain` + `Speed`
  **Notes:** `PhraseologyRules.cs:78, 93`; compound via `;` separator.
- **Phrasing:** §4-4-12-f-1 "resume normal speed"
  **Canonical:** `ResumeNormalSpeed`
  **Notes:** `PhraseologyRules.cs:98`.
- **Phrasing:** §4-4-12-f-4 "delete speed restrictions"
  **Canonical:** `DeleteSpeedRestrictions`
  **Notes:** `PhraseologyRules.cs:100`.
- **Phrasing:** §4-4-13 "EXTEND DOWNWIND"
  **Canonical:** `ExtendPattern`
  **Notes:** `PhraseologyRules.cs:365`.
- **Phrasing:** §4-4-13 "CLEARED FOR IMMEDIATE TAKEOFF"
  **Canonical:** `ClearedForTakeoff`
  **Notes:** "immediate" is adverbial; bare CTO covered at `:140-144`.
- **Phrasing:** §4-4-10-a-5 "cross Lakeview VOR at six thousand" / "cross {fix} at {alt}"
  **Canonical:** `CrossFix`
  **Notes:** `PhraseologyRules.cs:128-131`. Recurring everywhere.
- **Phrasing:** §4-4-12-f-5 "climb via SID"
  **Canonical:** `ClimbVia`
  **Notes:** `PhraseologyRules.cs:113-114`. Bare + "except maintain {alt}" forms. Also at 7110.65 §4-3, §4-5.

##### MissingRule
- **Phrasing:** §4-4-3-d-3 "cruise (altitude)"
  **Canonical:** `Cruise`
  **Notes:** canonical exists in enum; no rule. Also at 7110.65 §4-5, §6-6.
- **Phrasing:** §4-4-12-f-5 "descend via the TYLER One arrival"
  **Canonical:** `DescendVia`
  **Notes:** canonical exists; no rule. Also at 7110.65 §4-5, §4-7.
- **Phrasing:** §4-4-14-b Pilot acceptance of visual separation: "{callsign} in sight, will maintain visual separation"
  **Canonical:** `ReportTrafficInSight` (?)
  **Notes:** pilot-side readback wording diverges from controller-side `report traffic in sight`.

##### MissingCanonical
- **Phrasing:** §4-4-12-f-2 "comply with speed restrictions"
  **Canonical:** `??`
  **Notes:** also at 7110.65 §5-7.
- **Phrasing:** §4-4-12-f-3 "resume published speed"
  **Canonical:** `??`
  **Notes:** distinct from `ResumeNormalSpeed`. Also at 7110.65 §5-7.
- **Phrasing:** §4-4-12-a "maintain (mach) until (fix)" / "maintain (speed) until (fix) then resume normal/published speed"
  **Canonical:** `Speed`/`Mach` + `??`
  **Notes:** "until {fix}" trigger not parsed as phraseology suffix.
- **Phrasing:** §4-4-8 "VFR-on-top" / "maintain VFR-on-top" / "climb to VFR-on-top" / "maintain VFR conditions"
  **Canonical:** `??`
  **Notes:** no canonical. Also at 7110.65 §7-1, §7-3.
- **Phrasing:** §4-4-6 "special VFR clearance" / "cleared into the Class D special VFR"
  **Canonical:** `??`
  **Notes:** also at 7110.65 §7-5.

##### OutOfScope
- **Phrasing:** §4-4-1, §4-4-2, §4-4-3 a-c/e, §4-4-4, §4-4-5, §4-4-7-a, §4-4-9, §4-4-11, §4-4-12 a-d/g-l, §4-4-13 narrative, §4-4-14 a/c-d, §4-4-15, §4-4-16, §4-4-17 — policy/equipment/scanning background.
  **Canonical:** —
  **Notes:** procedural.
- **Phrasing:** Pilot requests for amended clearance ("request lower/higher/direct/descent/climb")
  **Canonical:** —
  **Notes:** instructor-as-pilot doesn't typically request from themselves; defer.

#### AIM §4-5 — Surveillance Systems

##### Covered
- **Phrasing:** §4-5-7-d-4-b "climb and maintain one zero thousand"
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:73`.
- **Phrasing:** §4-5-7-d-4-b "turn fifteen degrees left" / "turn twenty degrees right"
  **Canonical:** `RelativeLeft` / `RelativeRight`
  **Notes:** `PhraseologyRules.cs:60-61`.
- **Phrasing:** §4-5-7-d-4-b "proceed direct Bradford when able" / "direct Bradford when able"
  **Canonical:** `AppendDirectTo`
  **Notes:** `PhraseologyRules.cs:119`.
- **Phrasing:** §4-5-7-d-4-b "cancel climb clearance, maintain eight thousand"
  **Canonical:** `ClimbMaintain` (for tail)
  **Notes:** new maintain replaces prior climb; `:90`.
- **Phrasing:** §4-5-7-d-4-b "roger"
  **Canonical:** `AcknowledgePilotContact`
  **Notes:** `PhraseologyRules.cs:189`.

##### MissingRule
- **Phrasing:** §4-5-7-d-4-b "rejoin Victor Ten" / "rejoin {airway}"
  **Canonical:** `JoinAirway`
  **Notes:** canonical exists; no rule. Also at 7110.65 §4-4.

##### MissingCanonical
- **Phrasing:** §4-5-7-d-3-a "stop ADS-B transmissions" / equipment-failure directive
  **Canonical:** `??`
  **Notes:** also at 7110.65 §5-2.
- **Phrasing:** §4-5-7-d-4-b "rest of route unchanged" — modifier phrase
  **Canonical:** `??`
  **Notes:** should be absorbed/ignored by readback handler.
- **Phrasing:** §4-5-7-d-4-b "negative ADS-B equipment"
  **Canonical:** `??`
  **Notes:** pilot equipment advisory; no sim model.

##### OutOfScope
- **Phrasing:** §4-5-1 through §4-5-10 (excluding above) — radar/ATCRBS/ASR/ARSR/PAR equipment, ASDE-X/ASSC, TIS background, ADS-B intro/cert/cap/lim/reporting, TIS-B, FIS-B, ADS-R.
  **Canonical:** —
  **Notes:** equipment narrative.

#### AIM §4-6 — RVSM Operations

##### OutOfScope
- **Phrasing:** "Confirm RVSM approved" / "Affirm RVSM" / "Negative RVSM" / "Unable RVSM due equipment / turbulence / mountain wave" / "Unable issue clearance into RVSM airspace" / "Confirm able to resume RVSM" / "Ready to resume RVSM".
  **Canonical:** —
  **Notes:** oceanic/high-altitude equipment-status reporting; outside YAAT terminal/approach scope. Emergency contingencies skipped per audit rules. Also at 7110.65 §2-1.
- **Phrasing:** §4-6-2 through §4-6-11 — FL orientation, approvals, equipment-suffix flight planning, operating practices, MWA/turbulence guidance, wake guidance, contingency table, non-RVSM accommodation.
  **Canonical:** —
  **Notes:** workflow/policy/equipment background.

#### AIM §4-7 — Gulf of America 50 NM Lateral Separation

##### OutOfScope
- **Phrasing:** "Negative RNP 10" (initial contact in each Gulf CTA/FIR).
  **Canonical:** —
  **Notes:** oceanic RNP equipment-status; YAAT scope is domestic terminal/approach.
- **Phrasing:** §4-7-1 through §4-7-6 — policy, accommodation, RNP authorization, single-LRNS, flight-plan annotations, oceanic contingency.
  **Canonical:** —
  **Notes:** workflow/equipment/flight-planning background.

**AIM Ch 4 totals:** Covered 34 · MissingRule 27 · MissingCanonical 21 · OutOfScope 10 · Phrasings 92

### Chapter 5 — Air Traffic Procedures

#### AIM §5-1 — Preflight

##### MissingCanonical
- **Phrasing:** "Cancel my IFR flight plan" (§5-1-15) — pilot-initiated IFR cancellation on frequency
  **Canonical:** `CancelIfr`
  **Notes:** canonical exists in enum but listed as out-of-pilot-scope (data ops). AIM defines this as an on-frequency pilot transmission — defer to product whether pilot-side STT should accept it.

##### OutOfScope
- **Phrasing:** §5-1-1 through §5-1-17 (excluding §5-1-15 above) — preflight preparation, IFR procedures even when VFR, NOTAM system, OIS, VFR/IFR/DVFR/military/composite flight plans, high-altitude alternates, foreign airspace, plan changes, closing VFR/DVFR plans, RNAV/RNP, cold temperature operations.
  **Canonical:** —
  **Notes:** filing workflow / preflight planning / charting; not on-frequency phraseology.

#### AIM §5-2 — Departure Procedures

##### Covered
- **Phrasing:** §5-2-5 "Line up and wait" + intersection variant ("Runway 24L at November 4, line up and wait")
  **Canonical:** `LineUpAndWait`
  **Notes:** `PhraseologyRules.cs:130-134`.
- **Phrasing:** §5-2-8 "Delta 345 RNAV to MPASS, Runway 26L, cleared for takeoff"
  **Canonical:** `ClearedForTakeoff`
  **Notes:** `PhraseologyRules.cs:140-144`; runway-prefix shipped (recent work).
- **Phrasing:** §5-2-9 "Cleared Loop Six departure, climb and maintain four thousand" (altitude leg)
  **Canonical:** `ClimbMaintain`
  **Notes:** `PhraseologyRules.cs:73-74`.

##### MissingRule
- **Phrasing:** §5-2-9 "Climb via SID" / "Climb via the Suzan Two departure" / "Climb via SID except maintain FL180" / "Climb via SID except cross Mkala at or above 7000"
  **Canonical:** `ClimbVia`
  **Notes:** Bare "Climb via SID" + "except maintain {alt}" shipped at `PhraseologyRules.cs:113-114`. Named-SID variant ("Suzan Two departure") needs SID-name normalizer; "except cross {fix} at or above {alt}" composite needs Climb Via + Cross Fix chaining (CrossFix shipped). Recurring across §4-3, §4-5, AIM §4-4, AIM §5-5.
- **Phrasing:** §5-2-9 "Cleared (DP name) departure" / "Cleared Loop Six departure" (lateral SID clearance)
  **Canonical:** `??`
  **Notes:** no `ClearedSid`/`ClearedDeparture` canonical.
- **Phrasing:** §5-2-9 "Resume the Solar One departure" / "Proceed direct CIROS, resume the Solar One departure"
  **Canonical:** `??`
  **Notes:** no canonical for "resume {sid}". Also at §5-6 RESUME OWN NAVIGATION family.
- **Phrasing:** §5-2-9 "Expect to resume SID"
  **Canonical:** `??`
  **Notes:** advisory.
- **Phrasing:** §5-2-7 "Cleared to (destination) airport as filed, maintain (altitude), hold for release, expect (time) departure delay"
  **Canonical:** `??`
  **Notes:** IFR departure-release phrasing; also at 7110.65 §4-3.
- **Phrasing:** §5-2-7 "Released for departure at (time)"
  **Canonical:** `??`
  **Notes:** release time issuance.

##### MissingCanonical
- **Phrasing:** §5-2-6 Full route clearance ("Cleared to Miami Intercontinental One departure, Lake Charles transition then as filed, maintain FL270")
  **Canonical:** `??`
  **Notes:** full IFR clearance issuance; also at 7110.65 §4-2, §4-3.
- **Phrasing:** §5-2-2 PDC / CPDLC-DCL data-link clearance acknowledgements
  **Canonical:** `??`
  **Notes:** data-link, not voice.
- **Phrasing:** §5-2-9 VCOA ("Climb in visual conditions to cross … at or above 3500' before proceeding on course")
  **Canonical:** `??`
  **Notes:** visual climb-over-airport procedure.

##### OutOfScope
- **Phrasing:** §5-2-1 through §5-2-9 (excluding above) — pre-taxi clearance program, TDLS/PDC/CPDLC-DCL system descriptions, uncontrolled-field IFR pickup, taxi-clearance frequency guidance, abbreviated-clearance conditions, departure-control sector concepts, ODP/SID/DVA design, climb-gradient TPP notation, PBN NavSpec boxes.
  **Canonical:** —
  **Notes:** workflow/equipment/charting.

#### AIM §5-3 — En Route Procedures

##### Covered
- **Phrasing:** §5-3-8 §10 Holding entries (bare "hold at {fix}", left/right turns)
  **Canonical:** `HoldAtFixHover` / `HoldAtFixLeft` / `HoldAtFixRight`
  **Notes:** `PhraseologyRules.cs:386-388`. Full charted-hold form in MissingRule.
- **Phrasing:** §5-3-1 §1 UM116 "Resume normal speed"
  **Canonical:** `ResumeNormalSpeed`
  **Notes:** `PhraseologyRules.cs:98`.
- **Phrasing:** §5-3-1 §1 UM46/UM49 "Cross (position) at (altitude)" / "at and maintain (altitude)"
  **Canonical:** `CrossFix`
  **Notes:** `PhraseologyRules.cs:128-131`. Recurring.

##### MissingRule
- **Phrasing:** §5-3-1 §2 "Contact (facility) (frequency)" (with optional "at (time/fix/altitude)" trigger)
  **Canonical:** `Contact`
  **Notes:** canonical exists; no rule. Recurring.
- **Phrasing:** §5-3-1 §2 "Verify at (altitude)" / "Verify assigned altitude as (altitude)"
  **Canonical:** `??` (related to `SayAltitude` semantics)
  **Notes:** verification query.
- **Phrasing:** §5-3-1 §1 UM117/120 "Monitor (facility) (frequency)" CPDLC analog
  **Canonical:** `??` (`MonitorFrequency`)
  **Notes:** distinct from `Contact`. Also at 7110.65 §2-1, §5-9.
- **Phrasing:** §5-3-1 §1 UM107 "Maintain present speed"
  **Canonical:** `??` (`MaintainPresentSpeed`)
  **Notes:** no "present speed" canonical; analogous to `FlyPresentHeading`. Also at 7110.65 §5-7.
- **Phrasing:** §5-3-1 §1 UM82 "Cleared to deviate up to (distance) (direction) of route"
  **Canonical:** `??`
  **Notes:** offset/deviation; also at 7110.65 §4-4 offset clauses.
- **Phrasing:** §5-3-1 §1 UM127 "Report back on route"
  **Canonical:** `??`
  **Notes:** post-deviation report.
- **Phrasing:** §5-3-1 §1 UM154 "Radar services terminated" / "Surveillance service terminated"
  **Canonical:** `??`
  **Notes:** recurring (§7-6, §7-7, §7-9).
- **Phrasing:** §5-3-3 §1(a) "Radar contact" advisory (controller-issued)
  **Canonical:** `??`
  **Notes:** informational; also at 7110.65 §5-3.
- **Phrasing:** §5-3-1 §1 UM135 "Confirm assigned altitude" / UM134 "Confirm speed"
  **Canonical:** `SayAltitude` / `SaySpeed` (near-synonyms)
  **Notes:** rule aliases.
- **Phrasing:** §5-3-1 §1 UM51/52/53 "Cross (position) at (time)" / "at or before/after (time)"
  **Canonical:** `??`
  **Notes:** time-based crossing; also at 7110.65 §6-4.
- **Phrasing:** §5-3-8 §9 Holding clearance components (direction from fix, radial/course, leg length, EFC time)
  **Canonical:** `HoldingPattern`
  **Notes:** canonical exists; rules cover only "hold at {fix}" + turns. Also at 7110.65 §4-6.
- **Phrasing:** §5-3-8 §2 "Hold east as published"
  **Canonical:** `HoldingPattern`
  **Notes:** "as published" form.
- **Phrasing:** §5-3-8 §10(b) "Maximum holding airspeed is (speed)"
  **Canonical:** `??`
  **Notes:** advisory cap. Also at 7110.65 §4-6.

##### MissingCanonical
- **Phrasing:** §5-3-1 §1 UM55/56/57 "Cross (position) at (speed)" / "at or less/greater than (speed)"
  **Canonical:** `??` (CrossFix speed-only extension)
  **Notes:** `CrossFixCommand.Altitude` is non-nullable (`ParsedCommand.cs:733`); speed-only crossing needs a canonical extension. Also at 7110.65 §5-7.
- **Phrasing:** §5-3-1 §1 CPDLC altimeter-setting uplinks, IC validation, TOC, ABRR
  **Canonical:** `??`
  **Notes:** data-link, not voice.
- **Phrasing:** §5-3-2 Pilot position-report transmissions / §5-3-3 §1(a)(1) vacating altitude / §5-3-3 §1(a)(4) missed approach / §5-3-3 §1(a)(8) equipment failure / §5-3-3 §1(b)(1) leaving FAF inbound
  **Canonical:** `??`
  **Notes:** pilot transmissions; no controller-side canonical to ask for these reports (closest: `SayPosition`).

##### OutOfScope
- **Phrasing:** §5-3-1 §1(a)/(c), §3-5, §3-4 description — RCAG/sector frequencies, CPDLC tables (WILCO/UNABLE/STANDBY/ROGER/AFFIRM/NEGATIVE attributes, free-text uplinks, system mgmt), ARTCC RF outage procedures, Oakland/NY Oceanic FIR equipment requirements, pilot position-reporting mechanics, PIREP weather reports, airways/route system descriptions, course-change technique, COPs, MTA charting, holding procedure mechanics, time-of-departure-from-fix mechanics, radar surveillance.
  **Canonical:** —
  **Notes:** procedure mechanics / data-link / charting.

#### AIM §5-4 — Arrival Procedures

##### Covered
- **Phrasing:** "Cleared ILS Runway 27 approach" / "Cleared RNAV Runway 34 approach"
  **Canonical:** `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:197-200`.
- **Phrasing:** "turn right heading 330, maintain 2000 until established on the localizer, cleared ILS runway 36 approach"
  **Canonical:** `PositionTurnAltitudeClearance`
  **Notes:** `PhraseologyRules.cs:250-312`.
- **Phrasing:** "Cross Redding VOR at or above 5000, cleared VOR runway 34 approach"
  **Canonical:** `CrossFix` + `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:128-131` + `:197-209`. Greedy multi-clause matcher chains the two automatically as `CFIX … , CAPP …`. VOR/LDA/Localizer approach-type variants still MissingRule.
- **Phrasing:** "Cleared approach" (generic)
  **Canonical:** `ClearedApproach`
  **Notes:** `PhraseologyRules.cs:208-209`.
- **Phrasing:** "Cleared straight-in (type) runway X approach"
  **Canonical:** `ClearedApproachStraightIn`
  **Notes:** `PhraseologyRules.cs:201-202`.
- **Phrasing:** "Cleared visual approach runway X"
  **Canonical:** `ClearedVisualApproach`
  **Notes:** `PhraseologyRules.cs:205-206`.
- **Phrasing:** "Turn (left/right) immediately heading (deg), climb/descend and maintain (alt)" (PRM breakout)
  **Canonical:** `TurnLeft`/`TurnRight` + `ClimbMaintain`/`DescendMaintain`
  **Notes:** components covered; "immediately" + traffic-alert prefix in MissingRule (§5-9, AIM §4-1).
- **Phrasing:** "Pattern altitude (altitude). Right turns" overhead-maneuver preamble
  **Canonical:** `MakeRightTraffic` + `ClimbMaintain`/`DescendMaintain`
  **Notes:** `PhraseologyRules.cs:354-358`.

##### MissingRule
- **Phrasing:** "Descend via the Eagul Five arrival" / "Descend via (procedure name)"
  **Canonical:** `DescendVia`
  **Notes:** canonical exists; no rule. Recurring.
- **Phrasing:** "Cleared Tyler One arrival, descend and maintain FL240" / STAR + descent
  **Canonical:** `JoinStar` + `DescendMaintain`
  **Notes:** `JoinStar` canonical exists; no rule. Also at 7110.65 §4-7.
- **Phrasing:** "Descend via the Eagul Five arrival, except cross Vnnom at or above 12000" / "...except after Geeno, maintain 10000"
  **Canonical:** `DescendVia` + `CrossFix`
  **Notes:** EXCEPT modifier; also at 7110.65 §4-5.
- **Phrasing:** "Cancel approach clearance"
  **Canonical:** `??`
  **Notes:** no `CancelApproachClearance` in enum; closest `CancelLandingClearance`/`CancelTakeoffClearance`. Also at 7110.65 §4-8.
- **Phrasing:** "(Callsign) you have crossed the final approach course. Turn (left/right) immediately and return to the final approach course" (PRM correction)
  **Canonical:** `TurnLeft`/`TurnRight` + `JoinFinalApproachCourse`
  **Notes:** `JoinFinalApproachCourse` canonical exists; no rule.
- **Phrasing:** "Traffic alert (callsign) turn (left/right) immediately heading (degrees), climb/descend and maintain (altitude)"
  **Canonical:** `SafetyAlert`
  **Notes:** canonical exists; "traffic alert" preamble + "immediately" modifier missing. Also at §5-9, AIM §4-1.
- **Phrasing:** "Cleared ILS runway 7L approach, side-step to runway 7R"
  **Canonical:** `ClearedApproach` (+ side-step)
  **Notes:** side-step suffix MissingRule; side-step semantics MissingCanonical. Also at 7110.65 §4-8.

##### MissingCanonical
- **Phrasing:** "CancelApproachClearance" enum equivalent
  **Canonical:** `??`
  **Notes:** new canonical proposal.
- **Phrasing:** "Side-step to runway X" semantics
  **Canonical:** `??`
  **Notes:** distinct from landing-clearance amendment.
- **Phrasing:** Overhead maneuver "REPORT INITIAL" / "BREAK AT (point)" / "REPORT BREAK"
  **Canonical:** `??`
  **Notes:** overhead pattern reports. Also at 7110.65 §3-10.
- **Phrasing:** Pilot-requested "Request contact approach" / controller "Cleared contact approach (and if required) at or below (altitude)"
  **Canonical:** `??`
  **Notes:** contact approach; also at 7110.65 §7-4.
- **Phrasing:** Pilot STAR check-in: "leaving (altitude), descending via (STAR) (transition)"
  **Canonical:** `??`
  **Notes:** pilot-side check-in.
- **Phrasing:** "RNP AR" approach title
  **Canonical:** `??`
  **Notes:** distinct from RNAV; current rules only cover ILS/RNAV/visual.
- **Phrasing:** PAR/ASR/No-Gyro radar approaches as named approach types
  **Canonical:** `??`
  **Notes:** highly specialized; defer.

##### OutOfScope
- **Phrasing:** §5-4-1b-e, §5-4-2, §5-4-3, §5-4-4, §5-4-5, §5-4-6a-d/f-i, §5-4-7, §5-4-8, §5-4-9, §5-4-10, §5-4-11, §5-4-12, §5-4-13-17, §5-4-18, §5-4-20, §5-4-21, §5-4-22, §5-4-23 commentary, §5-4-24, §5-4-26.
  **Canonical:** —
  **Notes:** STAR policy, local flow, approach-control architecture, ATIS, IAP charts (TERPS/MSA/TAA/MVA/VDP/VDA/LNAV/LPV/LP/GLS), approach-clearance workflow rules, special IAPs (FDC NOTAM), procedure turn / hold-in-lieu mechanics, timed approaches mechanics, radar monitoring, simultaneous parallel/PRM/SOIA/converging system descriptions, RNP AR equipage, landing minimums, missed approach mechanics, EFVS, charted visual flight procedure, landing priority.

#### AIM §5-5 — Pilot/Controller Roles and Responsibilities

##### Covered
- **Phrasing:** §5-5-2 ATC clearance readback of hold-short instructions
  **Canonical:** `HoldShort`
  **Notes:** `PhraseologyRules.cs:501`.
- **Phrasing:** §5-5-4 Instrument Approach clearances
  **Canonical:** `ClearedApproach` family + `PositionTurnAltitudeClearance`
  **Notes:** `PhraseologyRules.cs:197-312`.
- **Phrasing:** §5-5-5 Missed Approach
  **Canonical:** `GoAround`
  **Notes:** `PhraseologyRules.cs:161-164`.
- **Phrasing:** §5-5-6 Vectors (heading + altitude)
  **Canonical:** `TurnLeft`/`TurnRight`/`FlyHeading` + `ClimbMaintain`/`DescendMaintain`
  **Notes:** `PhraseologyRules.cs:57-90`.
- **Phrasing:** §5-5-7 Safety Alert
  **Canonical:** `SafetyAlert`
  **Notes:** controller-emitted; canonical exists, no rule. See §2-1, §5-9.
- **Phrasing:** §5-5-9 Speed Adjustments
  **Canonical:** `Speed` / `ResumeNormalSpeed` / `DeleteSpeedRestrictions` / `ReduceToFinalApproachSpeed` / `Mach` / `NormalRate`
  **Notes:** `PhraseologyRules.cs:92-107`.
- **Phrasing:** §5-5-10 Traffic Advisories
  **Canonical:** `ReportTrafficInSight` / `Follow`
  **Notes:** `PhraseologyRules.cs:220-226`.
- **Phrasing:** §5-5-11 Visual Approach
  **Canonical:** `ClearedVisualApproach` / `ExpectApproach` / `ReportFieldInSight` / `Follow`
  **Notes:** `PhraseologyRules.cs:205-226`.
- **Phrasing:** §5-5-12 Visual Separation
  **Canonical:** `Follow`
  **Notes:** `PhraseologyRules.cs:220-221`.

##### MissingRule
- **Phrasing:** §5-5-9.C.5.b "comply with speed restrictions"
  **Canonical:** `??`
  **Notes:** also at 7110.65 §5-7, AIM §4-4.
- **Phrasing:** §5-5-9.C.5.c "resume published speed"
  **Canonical:** `??`
  **Notes:** distinct from `ResumeNormalSpeed`. Also at 7110.65 §5-7.
- **Phrasing:** §5-5-14 "Fly runway heading" (initial heading after takeoff)
  **Canonical:** `??` (or `FlyPresentHeading`)
  **Notes:** distinct from `FlyPresentHeading`. Also at 7110.65 §4-3, §5-8, §5-10.
- **Phrasing:** §5-5-14 "Climb via SID" / "Descend via STAR"
  **Canonical:** `ClimbVia` / `DescendVia`
  **Notes:** ClimbVia bare form shipped at `PhraseologyRules.cs:113-114`. DescendVia STAR phrasing ("Descend via the EAGUL5 arrival") needs STAR-name normalizer before shipping. Recurring.

##### OutOfScope
- **Phrasing:** §5-5-1, §5-5-2 narrative, §5-5-3, §5-5-5 pilot duties, §5-5-8, §5-5-13, §5-5-15, §5-5-16.
  **Canonical:** —
  **Notes:** narrative responsibilities, contact approach workflow, see-and-avoid, VFR-on-top, minimum fuel (emergency-adjacent), RNAV/RNP equipment.

#### AIM §5-6 — National Security and Interception Procedures

##### OutOfScope
- **Phrasing:** §5-6-1 through §5-6-16 — national security, ADIZ, civil/foreign aircraft authorization, FAA/TSA waivers, ESCAT, interception procedures, ICAO intercept signals, VWS.
  **Canonical:** —
  **Notes:** regulatory/operational rules and visual-signal communication; emergency/IRROPS-adjacent per audit rules.

**AIM Ch 5 totals:** Covered 23 · MissingRule 30 · MissingCanonical 14 · OutOfScope 6 · Phrasings 73

### Chapter 10 — Helicopter Operations

#### AIM §10-1 — Helicopter IFR Operations

##### OutOfScope
- **Phrasing:** §10-1-1 Helicopter Flight Control Systems (AFCS/SAS/ATT/AP/FD certification, RFM limitations).
  **Canonical:** —
  **Notes:** equipment/airworthiness reference.
- **Phrasing:** §10-1-2 Helicopter Instrument Approaches (Cat A visibility reduction, Copter IAP speed limits, 90/70 KIAS rules).
  **Canonical:** —
  **Notes:** procedural rules/charting; standard approach clearances already covered.
- **Phrasing:** §10-1-3 Helicopter Approach Procedures to VFR Heliports (Proceed Visually / Proceed VFR / PinS, cancel-IFR advisory at MAP).
  **Canonical:** —
  **Notes:** CancelIfr is out-of-pilot-scope per index.
- **Phrasing:** §10-1-4 Gulf of America Grid System (ADS-B equipage, LOA, OSAP/HEDA/ARA, waypoint naming).
  **Canonical:** —
  **Notes:** flight-planning/equipage reference.
- **Phrasing:** §10-1-5 Departure Procedures (PinS SID with visual segment, HCH, "VFR Climb to" chart instructions).
  **Canonical:** —
  **Notes:** charting/procedural reference; no pilot speech introduced.

#### AIM §10-2 — Helicopter Operations (Special Operations)

##### OutOfScope
- **Phrasing:** §10-2-1.b through §10-2-1.m — passenger management, crane-helicopter ops, helicopter/tanker ops, helideck hazard warnings, offshore VFR altitudes, offshore landing comms, two-helicopter offshore deck ops, rapid refueling procedures.
  **Canonical:** —
  **Notes:** ground safety / industrial / marine / NOTAM / SOP — no ATC phraseology.
- **Phrasing:** §10-2-2 Helicopter Night VFR Operations.
  **Canonical:** —
  **Notes:** operational theory / planning data.
- **Phrasing:** §10-2-3 Landing Zone Safety (HEMS) — clock-reference scene direction, wind in compass-from terms, hand signals, ground guide procedures.
  **Canonical:** —
  **Notes:** ground-responder-to-HEMS-crew comms; not pilot-to-ATC.
- **Phrasing:** §10-2-4 EMS Multiple Helicopter Operations — air-to-air coordination on 123.025.
  **Canonical:** —
  **Notes:** inter-helicopter coordination, not ATC.

**AIM Ch 10 totals:** Covered 0 · MissingRule 0 · MissingCanonical 0 · OutOfScope 9 · Phrasings 9

---

## Summary

All 10 chapters audited.

| Bucket | Count |
|---|---|
| Covered | 173 |
| MissingRule | 199 |
| MissingCanonical | 239 |
| OutOfScope | 189 |
| **Total phrasings audited** | **800** |

### Per-chapter totals

| Chapter | Covered | MissingRule | MissingCanonical | OutOfScope | Phrasings |
|---|---:|---:|---:|---:|---:|
| 7110.65 Ch 3 — Tower | 49 | 32 | 28 | 50 | 159 |
| 7110.65 Ch 4 — IFR/TRACON | 23 | 23 | 55 | 29 | 130 |
| 7110.65 Ch 5 — Radar | 30 | 33 | 62 | 37 | 162 |
| 7110.65 Ch 7 — Visual | 10 | 39 | 14 | 13 | 76 |
| 7110.65 Ch 2 — General Control | 3 | 14 | 13 | 18 | 48 |
| 7110.65 Ch 6 — Nonradar | 1 | 1 | 12 | 9 | 23 |
| 7110.65 Ch 9 — Special Flights | 0 | 0 | 20 | 8 | 28 |
| AIM Ch 4 — ATC | 34 | 27 | 21 | 10 | 92 |
| AIM Ch 5 — ATC Procedures | 23 | 30 | 14 | 6 | 73 |
| AIM Ch 10 — Helicopter Ops | 0 | 0 | 0 | 9 | 9 |
| **Total** | **173** | **199** | **239** | **189** | **800** |

### High-leverage MissingRule clusters (canonicals already exist; just need rule tokens)

Implementation sessions should pull these first — one rule addition per cluster closes many backlog entries:

- ~~**`CrossFix`**~~ — Stage 1 shipped (PhraseologyRules.cs:128-135). Closes 7110.65 §4-5, §4-7, §4-8, §5-7 (alt+speed), §5-9 (compound w/ ClearedApproach), AIM §4-4, AIM §5-3, AIM §5-4. Composite forms with DirectTo/ClimbVia/DescendVia await Stages 2-3. Speed-only "cross {fix} at {speed}" reclassified MissingCanonical (CrossFixCommand.Altitude is non-nullable).
- ~~**`ClimbVia`**~~ — Stage 2 bare forms shipped (PhraseologyRules.cs:113-114): "climb via SID" and "climb via SID except maintain {alt}". Closes §4-3 + AIM §4-4 fully; §4-5 / AIM §5-2 / AIM §5-5 partially (named-SID variants and "except cross" composites still need a SID-name normalizer and named amendment canonical). **`DescendVia`** still pending — "descend via the {star} arrival" needs STAR-name normalization. 7110.65 §4-5, §4-7, §5-7, AIM §5-4, §5-5.
- **`JoinStar`** — 7110.65 §4-7, AIM §5-4. Add `cleared (star) arrival` / `(star) arrival, (transition) transition`.
- **`JoinAirway`** / **`JoinRadialInbound`** / **`JoinRadialOutbound`** — 7110.65 §4-4, §5-6, AIM §4-5. Add `via (airway)` / `join (airway)` / `via (NAVAID) radial` etc.
- **`HoldingPattern`** — 7110.65 §4-6, AIM §5-3. Extend beyond bare `hold at {fix}` to full charted-hold form.
- ~~**`ClearedApproach`** Localizer/VOR/LDA + LOC BC variants~~ — Stage 4 shipped (PhraseologyRules.cs:223-230). GLS variant of §4-8 remains MissingRule pending a `TryStripTypePrefix` "GLS"→'J' addition.
- **`Contact`** / **`FrequencyChangeApproved`** — referenced everywhere; canonicals exist, no rules. Out-of-pilot-scope per current index but ubiquitous in FAA docs — may want product decision.
- **`SafetyAlert`** — 7110.65 §2-1, §5-9, AIM §4-1, §5-4. Add `low altitude alert` / `traffic alert advise you turn...`.
- **`WakeAdvisory`** — 7110.65 §2-1-20 `caution wake turbulence ...`. Canonical exists; no rule.

### High-leverage MissingCanonical proposals (need product review)

- **`MaintainPresentSpeed`** — analog to `FlyPresentHeading`. Multiple sections.
- **`MonitorFrequency`** — listen-only frequency transfer, distinct from `Contact`. Multiple sections.
- **`RemainOutsideAirspace`** — Class B/C entry-denial; `ClearedBravoAirspace` is the affirmative, no negative analog. AIM §7-8 (Charlie), §7-9 (Bravo).
- **`RadarServiceTerminated`** — recurring across §7-6, §7-7, §7-9, AIM §5-3.
- **`ResumeOwnNavigation`** / **`ResumeAppropriateVfrAltitudes`** / **`ResumePublishedSpeed`** — vector/altitude/speed release family. Multiple sections.
- **`CancelApproachClearance`** — enum has cancel-takeoff and cancel-landing but not approach. 7110.65 §4-8, AIM §5-4.
- **`SideStep`** — side-step to parallel runway. §4-8, AIM §5-4.
- **`SpecialVfrCleared`** / **`MaintainVfrConditions`** / **`MaintainVfrOnTop`** — VFR/SVFR cleared & maintain family. §7-1, §7-3, §7-5, AIM §4-4.
- **`CrossFixAtTime`** / **`DepartFixAtTime`** / **`HoldAtFixUntilTime`** — time-based crossing/holding. §6-4, §6-7.
- **Traffic-advisory canonical** (clock-position + distance + direction + type/altitude) — recurring across §2-1, §3-8, §3-9, §3-10, §5-9, §7-2, AIM §4-1. Could be `TrafficAdvisory` or absorbed into `SafetyAlert`.
- **`HoldForReason`** — extends `HoldPosition` with reason clause ("hold for wake turbulence", "hold for traffic"). §3-7, §3-9, §3-11.

Implementation sessions pull from MissingRule first (cheapest, deterministic). MissingCanonical entries are deferred to product / milestone planning.
