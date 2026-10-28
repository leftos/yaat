# Ground lessons from the SFO Ground familiarization video

The ground-controller technique taught in ZOA's "SFO GND Fam Video Draft 1" (YouTube `NZ4jTZwN5gs`, 74 min, posted 2025-03-30 by the ZOA EC), written as rules the controller AI's Ground brain can adopt. Companion to [`ground-lessons.md`](ground-lessons.md), which holds the rules taught live in coaching sessions.

The same video was studied earlier for sim test coverage in [`../../sfo-ground-technique-tests.md`](../../sfo-ground-technique-tests.md); rows here cite that doc's ids as "tests A4" and do not repeat its route probes or command forms.

## Sources and how each lesson was checked

- **Video**: paraphrased, with the timestamp where the point is made (`h:mm:ss`, link with `&t=<seconds>`). The author says it is one controller's technique and does not override a trainer.
- **Transcript**: YouTube's automatic captions, which mishear taxiway letters, runway numbers and fix names (see [Caption corrections](#caption-corrections)). The timestamped transcript, the caption file and both SOPs as PDF and text are kept out of the repo in `G:\My Drive\Private Config\yaat\sfo-gnd-video\`.
- **SFO ATCT SOP v1.11** and **SFO Ramp Tower SOP v1.0** (04SEP2025, published after the video), ZOA reference library ("For Simulation Use Only"), fetched through `zoa-reference-cli`'s procedure list. Cited as "SOP 3-5.c" and "Ramp SOP 2-4.a". The SOP attachments (super taxi diagrams, gate map, ramp diagram) are images and were not checked.
- **CIFP** (`tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`) for SID and transition names; **`sfo.geojson`** layout facts come from the tests doc.
- **7110.65 / AIM** (`.claude/reference/faa/`), cited by paragraph.
- **Review**: every row was checked by `aviation-sim-expert` against all of the above (timestamp, SOP citation, verdict, 7110.65/AIM), and its findings are folded in.

**Precedence**: the SOP outranks the video. Where they differ, the lesson states the SOP form and the Check column says so.

**Check** is one of:
- **SOP** — the SOP states the rule.
- **SOP differs** — the SOP says otherwise; the lesson is written to the SOP.
- **Video** — technique the SOP does not address and 7110.65 does not forbid.
- **7110** — a generic rule 7110.65 or the AIM states.

**Target** is one of: a GroundBrain rule in [`../04-ground-brain.md`](../04-ground-brain.md); an entry in a future `src/Yaat.Sim/Data/FacilityOps/KSFO.json` ([`../10-facility-knowledge.md`](../10-facility-knowledge.md)); the GC–LC coordination bus ([`../02-positions-and-handoffs.md`](../02-positions-and-handoffs.md)); or scenario data.

## Generic Ground rules

Rules a Ground brain applies at any airport; SFO is where the video shows them.

| # | Lesson | Video | Check | Target | Relation |
|---|---|---|---|---|---|
| G1 | Runway exits come first: an arrival clear of the runway is moved before any other taxi work. One stopped just clear often has its nose over the parallel taxiway, and other pilots will not taxi in front of it. | 0:13:36, 0:33:58 | SOP 3-3.a | GroundBrain: new top-priority rule | tests B6 |
| G2 | Watch Local's crossings: when Local crosses a batch of arrivals, stop other transmissions and taxi them all clear at once; give crossing arrivals priority over taxiers on the parallel they join. | 0:32:24, 0:33:26 | Video | GroundBrain priority; coordination bus (Ground needs Local's crossing intent) | |
| G3 | Keep aircraft moving: a longer route with no stops beats a shorter one with give-ways and hold-shorts; loop an arrival around the departure flow rather than through it. | 0:30:21–0:31:52 | Video | GroundBrain route choice: cost stops and flow conflicts above distance | OAK L9 |
| G4 | One-way flows: set a direction on each parallel taxiway, keep arrivals and departures on separate lanes, weave arrivals into the flow at intersections, and avoid routing against the flow, except to keep a blocked arrival near its gate (G10). | 0:14:07, 0:15:11 | Video | GroundBrain route choice; KSFO flow directions (S7) | OAK L8, L9 |
| G5 | Protect push capacity: every aircraft taxiing on a taxiway that gates push onto costs push opportunities; route arrivals off it when another route serves. | 0:14:37 | Video | GroundBrain route cost | |
| G6 | Push phraseology follows the surface: onto a movement-area taxiway "push back onto taxiway A approved"; when it must wait, "push back your discretion, hold short of taxiway A"; onto a non-movement area "push back your discretion" with the spot or taxilane. | 0:05:43 | SOP 3-5.a–d (facility forms); 7110.65 has no pushback phraseology, and "your discretion" is consistent with §3-7-2 NOTE 2 and SOP 3-5.f | GroundBrain push rule; pilot readback (YAAT-1240) | OAK L3; tests A6 |
| G7 | Simultaneous tail-to-tail pushes on adjacent lines are allowed where the facility permits them; each aircraft is told about the other. | 0:07:17 | SOP 3-5.c.i; Ramp SOP 1-6.e, 2-3.a | GroundBrain push rule | tests A3 |
| G8 | A push next to an arrival's gate: rather than stop the arrival, park it on the other line of the alley until the push clears, push the departure long across the alley to the far spot, or push it deeper along the taxiway clear of the arrival's turn-in. | 0:27:13–0:29:48 | Video | GroundBrain push/arrival conflict rule | tests A4, A5, A7 |
| G9 | Gate hold: an aircraft at the gate costs nothing; do not push an aircraft that cannot then taxi. Release the one that can push now, in the order the runway wants. Keep about 5–10 at the runway, release one per departure, and never into a congested ramp. | 0:55:15, 1:05:40, 1:07:44–1:08:48 | SOP 6-1.j–l | GroundBrain push gating | OAK L11; tests G4 |
| G10 | A blocked arrival waits near its gate, where departures can be routed around it, rather than being stashed far away and routed back against the flow later. The video's example holds it on a movement-area taxiway. | 1:09:20–1:11:24 | Video (cf. Ramp SOP 1-6.f, 3-4.e for ramp holding spots) | GroundBrain taxi-in rule | tests I2 |
| G11 | Sequence departures for Local: alternate aircraft with an in-trail or turn restriction with unrestricted ones. Prefer two lines merging into one; once an aircraft is held for sequence, its holding spot becomes the line for its category. | 0:35:30–0:41:15 | SOP 3-4.a for in-trail restrictions; Video for the turn alternation, whose benefit is diverging courses (7110 §5-8-3) | GroundBrain sequencing (a v1 non-goal) | tests C1 |
| G12 | Sequence only while there is demand: if sequencing has emptied the runway queue, stop and send aircraft in call order. | 0:42:17 | Video | GroundBrain sequencing guard | tests C4 |
| G13 | A heavy between two departures on the same route resets the alternation, since its wake interval already covers the next one; group heavies together so Local can apply wake categories efficiently. The heavy still needs normal spacing from the departure ahead of it. Under CWT, grouping helps C or D behind C, not anything behind B. Both 28s count as one runway for wake. | 0:42:48–0:43:51 | Video; 7110 §3-9-6.f, §5-8-3 | GroundBrain sequencing | tests C4 |
| G14 | Verify every hold-short readback names the runway, above all at parallel departure runways. | 0:16:14 | 7110 §3-7-2.h; AIM 4-3-18.a.9–10 | GroundBrain readback check | tests I1 |
| G15 | Where the facility gives Local the taxiways between active runways, hand a departure to Local at the hold-short of the first one ("hold short of 1L", then monitor tower); Local issues the crossing and the rest of the route. | 0:15:42–0:16:44, 0:18:50 | 7110 §3-1-3.a allows either crossing pattern; SOP 3-1.a.ii, 3-2.d make this SFO's | GroundBrain transfer rule for departures whose route continues on Local's surface; rule 2 unchanged elsewhere | OAK L7 |
| G16 | Crossing requests: pick the crossing nearest the threshold (it crosses sooner); coordinate in the words used on frequency ("at K, crossing 10R, crossing 10L"). A request conditional on a named departure Local has lined up is allowed between controllers, but the pilot gets an unconditional crossing only after that departure has passed the crossing point. | 0:52:39–0:54:13 | Video; SOP 3-2.a, 3-3.b; 7110 §3-1-3.a–b, §3-7-1.a, §3-7-2.g.1 | Coordination bus | tests D5, D6 |
| G17 | Ground owns inactive runways and uses or crosses them without coordinating with Local, though the pilot still gets an explicit crossing clearance for each runway. When Local asks for one, hold every aircraft short of it at every intersection first. A runway given "for one departure" returns once that departure is clear. | 0:34:29, 0:58:51 | SOP 3-1.a.i, 3-2.b; 7110 §3-1-3.e, §3-7-2.c; AIM 4-3-18.a.5; Video for "one departure" | Coordination bus; GroundBrain | |
| G18 | Report a crossed runway clear to Local when done, and the west end too. | 0:22:27 | 7110 §3-1-3.c for runways; SOP 3-2.a for the west end, where the video says no report is needed (SOP differs) | Coordination bus | |
| G19 | Answer every caller, even with "standby"; give an honest delay estimate; answer in one or two transmissions at a calm pace. | 1:04:04–1:06:11, 1:11:24 | Video | GroundBrain transmission rules | OAK L11 |
| G20 | Stash departures facing a long delay on a taxiway out of the flow, not in the line. | 0:51:37, 1:08:48 | Video | GroundBrain holding areas; KSFO (S14, S16) | tests D4 |
| G21 | A departure with a release time reaches the runway at least 5 minutes ahead and waits where it blocks no one; a wait over about 15 minutes goes to a holding spot off the departure flow. A call-for-release window is 2 minutes before to 1 minute after; an EDCT window is ±5 minutes. | 1:01:26–1:03:33 | Video; 7110 §4-3-4.d–e; SOP 6-1.g | GroundBrain flow (a v1 non-goal) | tests G1, G2 |
| G22 | Expect the gate in a departure's first call, and ask for it when missing. | 0:02:37 | AIM 4-3-18.a.1; SOP 6-1.h | GroundBrain first-call handling | |

## SFO facility knowledge (`KSFO.json` candidates)

| # | Lesson | Video | Check | Relation |
|---|---|---|---|---|
| S1 | Ground issues the push only for aircraft pushing onto A or into the GC-controlled non-movement areas (between boarding areas C/D and D/E, and taxilane Y). Ramp Tower pushes the A, G and United ramp gates, and those aircraft call Ground ready to taxi at their spot. Other non-movement areas (Plot 41, cargo) get "push back your discretion" from Ground. | 0:04:12 | SOP 2-1.f, 3-5.c–d; Ramp SOP 1-5 | |
| S2 | Push lines: in the 5 and 6 alleys, 5A/5B and 6A/6B take aircraft smaller than a B757 (simultaneously allowed); a B757 or larger uses spot 5 or 6. Yankee-area gates (about B23–C8) push onto Y when smaller than a B757, else onto A. Gates C10–C11, D6–D11, E12–E13 and F10, F11, F13, F15, F20 push onto A; F12, F14 and F16–F19 are G-ramp gates that Ramp Tower pushes onto the North line. | 0:05:43–0:08:19, 0:09:23–0:10:59 | SOP 3-5.c.i (5/6 alleys); Video for the Yankee size rule; SOP differs (Ramp SOP 1-5.b, 3-3.a) for the F gates, which the video sends onto A | tests A1, A2, A8 |
| S3 | Swing gates D5, D12 and E11 push into the alley or onto A depending on the aircraft; the wing may cross A. The video says to ask the pilot, which the sim would decide by wingspan. | 0:09:23–0:10:26 | Video | |
| S4 | United ramp (E1–E3, E6, E8, E10, F5–F8) belongs to Ramp Tower: spot 7A and 7B lines, closed while spot 7 is in use, limited to ADG-III or ADG-IV up to 145 ft wingspan. An ADG-V at E10V is pushed onto the spot 7 line tail north and calls Ground, which approves the rest of the push onto A. | 0:07:47 | SOP differs (Ramp SOP 4-2, 4-3): the video says "smaller than a B757"; SOP 3-5.a for the E10V continuation | |
| S5 | Ramp entry and exit: A ramp in at spot 1, out at spot 2; G ramp out at spot 10, in at spot 11 (spots 8 and 9 enter via 11 and B5). ADG-IV and ADG-V to B27 enter via spot 2. | 0:04:43, 0:08:51 | SOP differs for the G ramp (SOP 3-5.h; Ramp SOP 2-4.a, 3-4.a–b): the video uses whichever spot is closest | tests A9 |
| S6 | ADG-VI (A388, B748): A ramp via spot 1 on M1/M4, in and out; G ramp via the spot 10 line, in and out. | 0:05:13, 0:26:36 | SOP 3-5.h.i; Ramp SOP 2-2.c, 3-2.a | tests A9 |
| S7 | 28/01 flows: from exit E, B runs counterclockwise and A clockwise; arrivals to Yankee and the A ramp go B, H, A; C-gate arrivals join A further east so they never turn against the departure flow. Inbounds are told "taxi to the ramp", with a route only when needed. | 0:13:36–0:15:42 | Video; SOP 3-5.e for the phrase | tests B7 |
| S8 | 28/01 departures: right turns to 1R, left turns to 1L. 1R via A, A1 (spots 8–10 join A), but no 4-engine or Super aircraft on A1; 1L split off via A, F1, B, and no A380, DC10, MD11 or L1011 departs 1L. Aircraft west of E going to 28L taxi A, F and hold short of 1L; east of E, the nearest intersection. The A, L, F route holds short of A1 so nothing is on L behind a full-length 1R takeoff. | 0:12:33–0:17:48 | SOP 3-4.b.i, 4-8.a–c; the video picks 1L "if requesting" | tests B1–B3 |
| S9 | North field (Plot 41, Signature, cargo): name the turn onto C, and to 28R at E hold short of E (hot spot); to the 1s go around the west end via C and Z, hold short of the west end, then Z and hold short of B1, then join the flow (usually B1, A). | 0:17:48–0:21:25 | SOP 3-2.d–e, 3-4.c | tests B4, B5 |
| S10 | West end (Z from C to Z2) belongs to Local when landing the 10s, departing the 28s, or when given, and is a mandatory briefing item. Request it as "once (twice) around the west end", or conditionally behind a named departure; transfer it with "west end to ground / west end to local". In the west plan it protects 28 departures only, not 28 arrivals; when landing the 10s it is Local's for the arrivals. | 0:19:21–0:22:59 | SOP 3-1.a.vi, 4-2.a.vi; report clear per G18 | tests B4 |
| S11 | Supers and B748: runway 28R by default. From the A ramp go A or B to H and cross at H (never A north of H); from the G ramp go A to Q/Q1, B, F and hold short of 1L. Coordinate with Local before a super taxis on B between Q1 and E, which makes 28L unusable. A super arriving to the A ramp goes Q, B, hold short of F1, then B, H, A (or B, M1 when 1L is idle); Local builds that gap in, so coordination there is optional. A B748 or A380 holding between runways (for example at H after crossing 28L) closes the runway behind it. | 0:22:59–0:26:36 | Video for the routes and the B/28L closure; SOP 4-8.a (no 1L departure), 4-8.c (no A1), 4-8.d.i (holding between runways) | tests H1–H4 |
| S12 | 28/28 RT: Local departs off 28L, so 28R arrivals stack between the runways and cross 28L in batches (G2). A B748 or A380 (any taxiway), A346 or B77W (except on H, T, Q) or A35K (on E or F1) holding between the runways closes the runway behind it, so expect Local to cross those first. Ground owns the 1s and stages departures on them; spot 2 departures go A, L, F; at H only one runway is crossed per instruction. Local may exit arrivals onto a 1 and switch them to Ground. | 0:32:24–0:36:03, 0:41:15 | SOP 1-7, 3-3.b.iii, 4-8.d | tests C1–C3, C6 |
| S13 | Sequencing categories off 28L: TRUKN2 (right turn) alternates with everything else. A run of TRUKN2s can be broken up by amending those to DEDHD or GRTFL to SNTNA2 (straight out) with its climb, early enough for the pilot to brief it. SNTNA2 carries all six TRUKN2 transitions, but straight-outs over ORRCA, MOGEE, TIPRE and SYRAH are avoided. In 28/01 the SOP uses SNTNA2 instead of TRUKN2 off the 28s. | 0:36:03–0:44:54 | Video; CIFP; SOP 2-2.a footnote, 3-4.c.v, 1-7 | tests C5 |
| S14 | 19/10: arrivals usually use 19L only (the ILS runway); exits cross the idle 19R at once and must be cleared as fast as the 28s. Departures from spot 2 go A, E, B, Z, S. CIITY3 goes to 10L at S1, all others to 10R at S3 (the video also sends the first few to 10R at Z1 when quiet), and the north field to 10L at C3 via C, C3. Hot spots are the arrival/departure merge at E and B1: join B early (K, Q1, D, T) and, when in doubt, hold everyone short of B1. Stash delays on Z, C, C3 (about 7–8 aircraft). To Signature, cross the 10s at K or D. | 0:45:26–0:55:15 | SOP 3-4.b.iii, 3-3.b.i; CIFP; Video for the hot spots and Z1 | tests D1–D7 |
| S15 | Keep each gate's taxi-in route the same in every configuration (F7 via B, K, A; F20 via B, B3; C11 via B, G, A), since pilots and controllers expect it. | 0:48:30–0:49:32 | Video | tests B7 |
| S16 | Yankee is a stash for aircraft smaller than a B757: occupied gates, release times, sequencing. | 1:08:48 | Video | |
| S17 | 19/19: departures to 19R via A, E; a line past C turns onto C and rejoins via D. 19L, 10L or 28R on request: hold short of the first active runway, and tell Local about a departure taxied to a non-advertised runway. 28R gets GAPP7 at 3,000 and a long-delay advisory (opposite-direction release). Ground owns the 10s (G17). | 0:55:45–0:59:54 | SOP 3-4.b.v, 4-1.b.i; 7110 §3-1-4.a; Video for GAPP7 at 3,000 | tests E1–E4 |
| S18 | 10/10: Ground owns the 19s. Departures south of 10R go to 10R at Z1, north of 10L to 10L. When 10R takes arrivals, send 10L departures across 10R at S3 or S1 (coordinated with Local), never around behind the 10R threshold. Arrivals cross the 1s past L via P, N or F2. | 0:59:54–1:00:55 | SOP 3-4.b.iv; 7110 §3-1-3.a | tests F1, F2 |
| S19 | Release times in 28/01: park the aircraft on M1 while the others take 1L at M; for waits over about 15 minutes, hold on L short of M2 facing south, which closes the A, L, F route. | 1:01:58–1:03:33 | Video; the layout has no L/M2 junction (tests F-3) | tests G1 |
| S20 | Split ground only in 28/01, and in 28/01 open GC2 rather than a separate gate hold. GC1 has movement areas north of gate D8 (not D8 or F1) and keeps aircraft on standard routes; GC2 has D8 south including F1, and does the F1 split, the runway sequencing and release times. Local sends arrivals exiting the 28s west of D that park in GC2's area to GC1. The video describes the same split with the position numbers reversed. | 1:06:42–1:07:44 | SOP 2-1.i, 4-4.h, 6-1.c, 7-1.b–d, 7-2 | |
| S21 | Gate hold is considered when the departure line passes a marker, as a guideline: past G on B (1L) or M2 on A (1R) in 28/01 and 01/01; past L on F/C in 28/28; past Z1/C3 on Z in 19/10; past B1 on B or Z/R on C in 10/10; past C on E in 19/19. | — | SOP 6-1.d.ii; AIM 4-3-15.a (gate hold for delays over 15 minutes) | tests G3 |
| S22 | Airlines by gate area, for scenario realism: Southwest, SkyWest, Alaska and American in the Yankee area; Delta at C; United and Southwest at D; United and United Express from E to G; international carriers (Hawaiian, Aeromexico, Avianca) at A; United heavies, Lufthansa and ANA at G. | 0:10:59–0:12:33 | Video (gate moves were in progress at recording) | scenario data, not the brain |
| S23 | G ramp inbounds: when the spot 8 and spot 9 lines are both occupied, the gates behind them are unreachable; hold arrivals short of B4, then bring them in via B4 or B5 once the lines clear. | 0:08:51–0:09:23 | Video; Ramp SOP 3-4.b, 3-4.e | |

## Where the video and the SOPs disagree

- G18: the SOP requires reporting the west end clear; the video says no report is needed for it.
- S2: F12, F14 and F16–F19 are G-ramp gates pushed onto the North line; the video sends them onto A.
- S4: spots 7A/7B take ADG-IV up to 145 ft; the video says smaller than a B757.
- S5: the G ramp has fixed exit and entry spots (10 out, 11 in); the video uses the closest spot.

The Ramp Tower SOP (September 2025) and the ATCT SOP's ramp entry/exit procedures post-date the video, which explains the ramp differences.

## Caption corrections

| Caption | Meaning | Confirmed by |
|---|---|---|
| truckin / trucking | TRUKN2 SID | CIFP, SOP 2-2 |
| Santana | SNTNA2 SID | CIFP, SOP 2-2 |
| Dead Head, Grateful | DEDHD and GRTFL transitions (TRUKN2 and SNTNA2) | CIFP |
| gap 7 | GAPP7 SID | CIFP |
| ones are right / ones are left, one Z right | 10R / 10L ("one zero") | context, SOP 3-3.b.i, 3-4.b.iv |
| five shot, fire shot, flag shot, Fox shot, fire truck | taxiway F, F1 | SOP 3-3.b, layout |
| Fart 10 … five 20, Fox 20 | gates F10–F20 | Ramp SOP 1-5.b |
| rust end | the west end | SOP 3-1.a.vi |
| ASDI, ASD | CRC's ASDE-X surface display (SFO's real system is ASSC) | SOP 1-4 |
| Gulf / golf ramp | G gates ramp | Ramp SOP §3 |
| shadow ramp | the E10–F8 area, the Ramp SOP's United ramp; the word itself is not settled | Ramp SOP 1-5.c |
| ego gates, Echko | E gates, taxiway E | context |
| Norcow | NorCal (NCT) | context |
| Taco | TACA (Avianca group) | context |
| Honapon | ANA (All Nippon Airways) | owner |

## Sim support these lessons need

Most command-surface gaps are already listed in the tests doc (A6 `PUSH … HS A`, A7 push distance, C5 SID amendment, C6 `EXIT <runway>`, D6 conditional crossing). New here:

- G2, G17: Ground needs to see Local's crossing intent and runway-release state through the coordination bus.
- S3, S4: push-line choice by wingspan (ADG) per gate.
- G6: the readback forms for "push back your discretion" and "onto taxiway A approved" (YAAT-1240).
