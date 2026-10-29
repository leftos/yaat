# Pilot phraseology variation by frequency load

Design seed for the Linear issue "Vary solo pilot readback phraseology by how busy the frequency is". Owner request, with the owner's own reference examples (written for an earlier virtual-ATC mod) below.

## The idea

Pilots know whether the frequency is busy. On a busy frequency they read back laconically (shorter forms, grouped frequency digits, dropped words); on a quiet one they are more relaxed (fuller forms, an occasional "Alright, " prefix or ", thanks" suffix). The TTS speech rate can follow the same load, a little faster when busy.

## Current state

Part of the machinery already exists:

- `FrequencyActivityMeter` keeps the transmissions on the active frequency over a rolling 60 s and judges a Quiet / Normal / Busy `FrequencyLoadBand` by the dead-air share of the window ([solo-training-pilot-speech.md](../solo-training-pilot-speech.md)). It counts the controller's airtime as well as the pilots'.
- `PhraseologyVerbalizer.ShouldUseShortcut` makes a `PilotPersonality.Varied` pilot use a rule's `PilotShortcuts` (the shortest) at Busy.
- `PilotResponder.ApplyQuietFlavor` adds an "alright, " prefix or a ", thanks" suffix to the spoken body, drawn from a hash of the callsign and body (deterministic, but not yet seeded per pilot).

What is not built: a per-pilot terse-to-chatty trait, a per-transmission seeded pick among variants, variants beyond the busy shortcuts, the callsign abbreviation and the grouped frequency form. The decomposition below plans them.

## Settled answers

Owner rulings:

- Busy is how little of the trailing minute is dead air on the frequency, controller and pilot airtime together, banded quiet / normal / busy.
- Variation is both per pilot and per transmission: each pilot has a seeded personality (terse to chatty) that shifts the band, and each readback picks a seeded variant within it, so replay stays deterministic.
- Only the spoken form varies; the Terminal keeps the canonical readback text.
- Every readback gets variants, each variant ruled by `aviation-sim-expert`.
- The Settings › Speech Speed slider sets the base speech rate; pilots speak 1.1 times that rate when the frequency is busy and at that rate otherwise, never slower.
- In scope: the FAA N-number shortening after initial contact and the grouped frequency form. The ICAO "first letter plus last two" shortening is dropped, since it conflicts with 7110.65 2-4-9.b.3 in US airspace. The 7110.65 2-4-20 rule that a pilot abbreviates only after the controller has is deliberately not modelled.
- Grouped frequency without the leading 1 ("twenty-five thirty-five" for 125.35): mostly when busy, rarely when normal, never when quiet; one-decimal frequencies keep "point"; no grouping when the first decimal is 0.
- "Climbing up to" / "descending down to" shortcuts apply to flight levels only; below FL180 the altitude form keeps no "to" before the number.

Orchestrator decisions (open to the owner):

- The meter judges three bands by the dead-air share of the last 60 s: Quiet at 70% or more, Normal from 35% to under 70%, Busy under 35%. Each transmission lasts 0.25 s per word (1 s minimum) of its canonical text, pilot and controller alike; overlaps count once.
- The controller side is counted once per successful dispatch, as the interval ending at the command's sim time. Durations come from the canonical text, so shorter busy readbacks cannot feed back into the load.
- The meter's intervals are snapshotted, so a rewind or restore resumes in the same band.
- Personality is derived, never stored: `Fnv1a(callsign) ^ RngSeed` gives each pilot a terse-to-chatty trait, and `PilotPersonality` stays as the variation on/off switch. The trait is terse (25%), normal (50%) or chatty (25%); terse speaks one load band busier, chatty one band quieter, clamped to quiet..busy (owner ruling).
- The per-transmission pick hashes the callsign, the canonical body and the sim second, so replay is deterministic and two identical readbacks a minute apart can differ.
- The Terminal never varies: busy shortcuts reach only the spoken form, and `VerbalizeTerminal` and the RPO terminal get the canonical values.
- The load nudge is computed by the server and sent on the pilot-transmission broadcast; the client multiplies the Speech Speed slider value by it (1.1 when busy, 1.0 otherwise) and clamps the product to the engine's 0.75 to 1.5.
- Variation covers every readback (rule-path readbacks, handler readbacks, direct-render clauses and the contact readback) and the calls the pilot starts itself (check-ins, position reports, ready-for-departure calls, requests). Unable replies keep their wording, so a refusal stays unmistakable (owner ruling).
- Callsign abbreviation waits for `HasControllerAcknowledgedInitialContact`, per controller, and never applies to airline, military or MEDEVAC call signs or when a similar callsign (same last two characters) is on the frequency (AIM 4-2-4.a.1 to a.3, 7110.65 2-4-9).
- The existing busy shortcuts the aviation consult rejects are fixed in the same programme: `"{deg} left"` keeps "degrees", and the route-less TAXI shortcut keeps "taxi".

## Aviation floor

Every variant, in every band, obeys these invariants; a variant that fails one is discarded and the canonical text is spoken. For rule rows, `AviationFloor.HoldsTemplate` enforces F3 to F9 and F12 at test time over every row: the variant carries the canonical pattern's captures in the same order and its ordered keyword classes. Those are the keywords, left/right, the qualifiers, the safety verbs and the meaning words; climb/descend may be dropped (busy only) but never swapped, and "runway" may be dropped (busy only) on a runway clearance alone. Clause builders that only produce strings keep the text check, `AviationFloor.Holds`, which compares ordered anchors (digit-leading tokens, keywords, direction classes, hold short, cross) and altitude unit words, else the canonical clause is spoken.

| # | Invariant | Cite |
|---|---|---|
| F1 | Callsign present in every readback, at the start or the end (full, or a legal abbreviated form). Busier frequency makes this more important, never less. | AIM 4-4-7.b.1; AIM 4-2-3.c; 7110.65 2-4-3.c |
| F2 | Wake suffix "heavy" / "super" kept whenever the canonical has it (house rule; the 2-4-14 NOTE could license dropping it later). | AIM 4-2-4.a.5; 7110.65 2-4-14.b/c |
| F3 | Every assigned altitude or restriction is spoken with its keyword ("thousand" / "hundred" or "flight level") and its qualifier ("at", "at or above", "at or below", "until established", "except maintain"). No decimal shorthand, no bare FL digits. | AIM 4-4-7.b, 4-4-7.b.2, 4-2-9.a/b; 7110.65 2-4-17.b |
| F4 | Every heading is spoken with the word "heading" and its three digits; the turn direction is kept whenever the controller gave one; relative turns keep "degrees". | AIM 4-4-7.b, 4-2-10; 7110.65 2-4-17.h |
| F5 | Every runway assignment keeps L/R/C: taxi, line-up, takeoff, landing, option / T&G / S&G / low approach, approach clearance, pattern entry with a runway. | AIM 4-4-7.b.4, 4-3-18.a.9.a; 7110.65 2-4-3 NOTE 1.c |
| F6 | Every hold-short is spoken "hold short (of) {target}", with "runway" kept for a runway target and any locative. Never "short runway 33". | 7110.65 2-4-3.b, 3-7-2.h; AIM 4-3-18.a.9.c, 4-3-18.b.1 |
| F7 | Every runway crossing or entry is spoken "cross runway {rwy}" (plus "at {twy}" if given); "line up and wait" keeps its runway. | AIM 4-3-18.a.9.b, 4-3-18.b.1 |
| F8 | LAHSO: "cleared to land runway {rwy}, hold short of {target}" in full, every band; no courtesy, no shortening. | AIM 4-3-11.b.7, 4-3-11.c.2; 7110.65 3-10-5.b |
| F9 | Speeds and Mach keep a keyword ("knots", "speed", "Mach"); squawk codes keep "squawk" and all four digits; frequencies keep enough digits to be unambiguous. | 7110.65 2-4-3 NOTE 1.a, 2-4-17.i/k/l |
| F10 | Variants never reorder clauses: they drop or swap words inside a clause, prepend or append the acknowledgement or courtesy, and move the callsign between start and end. The one exception: TAXI may say "taxi via {twy} to runway {rwy}", the route itself staying in order. | AIM 4-4-7.b.2 |
| F11 | Taxi route: every cleared taxiway, in order, plus the destination (house rule; the FAA mandates only runway, entries and hold-shorts). | AIM 4-3-18.a.9; `docs/pilot-phraseology.md` |
| F12 | Safety-critical verbs are never reduced to "wilco" / "roger": go around, cancel takeoff or landing clearance, hold position, line up and wait, cleared for takeoff / to land, cross runway, hold short, follow / behind, cleared into or through the bravo. | AIM 4-3-18.b.1; 7110.65 2-4-3 |
| F13 | No charted altitude added that the controller did not state. | AIM 4-4-7.b.3; 7110.65 2-4-3 NOTE 1.b |
| F14 | No jargon or CB slang ("warp speed ahead", "on the brakes", "see ya"). | AIM 4-2-1.c |

What may vary: verbs and particles inside a clause, "taxi" / "via" / "contact" / "on", the facility name on a frequency change, the acknowledgement words, courtesy ("alright, " never on runway clearances or an F12 verb, nor when the readback holds "left" or "right"; ", thanks" never on go-around, cancel, hold position or LAHSO), callsign position and form, and frequency form. A readback with no number and no runway, hold or crossing element may collapse to "wilco, {cs}" or "{cs}". The Terminal stays canonical, so the student sees the textbook form even when the voice is laconic.

## Code map

Entry points for the work, by decomposition part (each part is its own issue under YAAT-849, landed on `feat/pilot-phraseology-load`):

- **A, YAAT-1076, load meter.** `Pilot/FrequencyActivityMeter.cs` (`DeadAirShare`, `FrequencyLoadBand`, `Transmissions`, `Restore`), `Pilot/FrequencyState.cs` (the pilot-side `Record` at dequeue), `SimulationWorld.RecordControllerTransmission`, `Simulation/SimulationEngine.Commands.cs` (`ApplyPostDispatch`, the controller-side hook), `Simulation/SimulationEngine.Snapshots.cs` and `StateSnapshotDto.FrequencyTransmissions`.
- **B, YAAT-1077, seed and personality.** A per-pilot trait derived from `DeterministicHash.Fnv1a` and `SimScenarioState.RngSeed`, and a seeded variant pick: `Pilot/PilotTrait.cs` (`PilotTraits.For`, `EffectiveBand`), `Pilot/VariantPick.cs`, `DeterministicHash.Mix`, and `Pilot/PilotSpeechContext.cs`, which replaces the `(PilotPersonality, activity level)` pair through `PilotResponder.BuildReadback*` and takes a `FrequencyLoadBand` in `PhraseologyVerbalizer`; `SimulationEngine.ApplyPostDispatch` builds it. `PilotPersonality` stays as the on/off switch, and nothing reads the trait or the pick until part C.
- **C, YAAT-1078, spoken-only seam.** `Pilot/PilotResponder.cs` (`BuildReadback`, `FrameReadback`, `ApplyQuietFlavor`, `VerbalizeDual`) and `Pilot/PhraseologyVerbalizer.cs` (`Verbalize` vs `VerbalizeTerminal`): the band and personality reach only the spoken form, the Terminal and RPO body stay canonical.
- **D, YAAT-1079, speech-rate nudge (cross-repo).** `PilotTransmission` and the queue methods in `PilotResponder`; yaat-server `Simulation/TickProcessor.cs`, `TrainingBroadcastService` and `PilotTransmissionBroadcastDto`; client `MainViewModel.Aircraft.cs` (`OnPilotTransmissionReceived`), `Services/PilotVoiceService.cs` and `SherpaOnnxPilotVoiceSynthesizer.SpeakAsync`.
- **E, YAAT-1080, callsign abbreviation.** `Pilot/SpokenFormDraws.cs` (`CanShortenCallsign`, `DrawShortCallsign`), `PilotResponder.SpokenReadbackCallsign` and its readback callers, `CallsignParser.ShortenedNNumberSpoken`, `SimulationEngine.PilotSpeechContextFor`, `AircraftState.HasControllerAcknowledgedInitialContact`.
- **F, YAAT-1081, grouped frequency form.** `PhraseologyVerbalizer.FrequencyToWords(double, bool grouped)`, `SpokenFormDraws.DrawGroupedContactFrequency`, `PilotResponder.BuildContactReadback` (takes the speech context), reached through `DispatchContext.SpeechContextFor`.
- **G, YAAT-1082, variants per readback family.** `Speech/PhraseologyRules.cs` (`PilotShortcuts` as tiered forms), the direct renders in `PhraseologyVerbalizer.VerbalizeCore`, the direct clause builders in `PilotResponder`, and the handler readbacks (`CommandResult.PilotReadback`); each family needs an `aviation-sim-expert` ruling first.

Order: A, B and F start together; D and E follow; C needs A and B; G needs C.

## Owner's reference examples

```
[Complete]
BATC Line: Ground 121.9 when ready, N696CL.
Shorter: Ground .9 when ready, N969CL.
(FAA phraseology says that 121 can be implied when referring to Airport frequencies.)
Shorter: .9 when ready, N969CL.
Shorter: Ground .9, N969CL.
Verbose: Ground on 121.9 when ready, N696CL.
Verbose: We'll contact ground 121.9 when ready, N696CL, thanks.

[Complete]
BATC Line: Runway 28 taxi via S1, E, N696CL.
Shorter: Runway 28 via S1, E, N696CL.
Shorter: Runway 28 taxi S1, E, N696CL.
Less strict: Taxi S1, E to runway 28, N696CL.
Verbose: Alright, we'll taxi to runway 28 via S1, E, N696CL.


BATC Line: Climb to FL190, N696CL.
Less strict: Up to FL190, N696CL.

[Complete]
BATC Line: Roger. Cleared direct APU, resume own navigation, N696CL.
Shorter: Direct APU, resume own navigation, N696CL.
Shorter: Direct APU, own nav, N696CL.
Verbose: Alright, we'll head direct APU, then resume own navigation, N696CL, thanks.

[Complete]
BATC Line: Contact Taipei Control 124.6, N696CL.
Variation: Taipei Control on 124.6, N696CL.
Shorter: Control on 124.6, N696CL.
Shorter: Control 124.6, N696CL.
Shorter: 124.6, N696CL.

[Complete]
BATC Line: Cleared direct to XLN, N696CL.
Shorter: Direct XLN, N696CL.

[Complete]
BATC Line: N696CL, ready for descent.
Verbose: [ATC Callsign], N696CL, we're approaching our top-of-descent and are ready for lower.
Variation: N696CL, we're at top-of-descent.
Variation: N696CL, top-of-descent.

[Complete]
BATC Line: OVG9ZA, Runway 34, descend to 4,900, N696CL.
Variation: OVG9ZA, Runway 34, down to 4,900, N696CL.
Shorter; OVG9ZA, Runway 34, 4,900, N696CL.
Verbose: Alright, we'll descend via OVG9ZA arrival to 4,900 feet, [Airport Name] runway 34, N696CL.

[Complete]
BATC Line: Zhuhai, N696CL, FL112 with information India.
Shorter: Zhuhai, N696CL, FL112 with India.
Variation: Zhuhai, N696CL, FL112, we have India.
Variation: Zhuhai, N696CL, FL112, we have India on-board.

[Complete]
BATC Line: QNH 1016 expect the ILS-Z 34 with the GUBLO transition, N696CL.
Shorter: 1016, expect the ILS-Z 34 with the GUBLO transition, N696CL.
Shorter: 1016, expect ILS-Z 34 from GUBLO, N696CL.
Shorter: 1016, expect ILS-Z 34 via GUBLO, N696CL.


BATC Line: Cleared direct GUBLO, cross GUBLO at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO, cross at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO at or above 4.9, cleared ILS-Z 34, N696CL.


BATC Line: Exit right at A, N696CL.
Shorter: Right on A, N696CL.

[Complete]
BATC Line: Ramp 27 taxi via A, B. Hold short runway 33, N696CL.
Shorter: Ramp 27 via A, B, short runway 33, N696CL.

[Complete]
BATC Line: Cross runway 33 at B. Continue taxi via B, C, G, N696CL.
Shorter: Cross 33 at B, then B, C, G, N696CL.

[Complete]
BATC Line: Bali Center, Supergreen 695, crossing 9,800 for 12,000 direct LEBAH.
Shorter: Bali Center, Supergreen 695, 9.8 for 12, direct LEBAH.


BATC Line: Turn [left/right] heading 200
Shorter: [Left/Right] 200
Shorter: Heading 200
Shorter: 200 ("two-zero-zero")
Verbose: Turning [left/right] to heading 200


BATC Line: Maintain 250 knots
Shorter: 250 knots
Shorter: Speed 250 ("speed two fifty" / "speed two five zero")
Verbose: We'll maintain speed 250 knots
Verbose: [Slowing down / Speeding up] to 250 knots
Verbose / Playful: [Warp speed ahead / on the brakes], 250 knots


BATC Line: [Climb/Descend] maintain FL190
Shorter: [Up/Down] to 190
Shorter: FL190
Verbose: We'll [climb/descend] and maintain FL190


BATC Line: [Climb/Descend] maintain 4,500
Shorter: Up/down to 4,500
Shorter: Up/down to 4.5 ("four point five")
Shorter: 4,500 (it's clear that this can't be anything except a non-flight level altitude)
```

### Other notes (owner)

- **"Thanks" / "Alright":** a ", thanks" suffix (before or after the pilot callsign) or an "Alright, " prefix fits almost any pilot response when things are quiet. Examples: "Left heading 200, N696CL, thanks." / "Left heading 200, thanks, N696CL." / "Alright, we'll turn left heading 200, N696CL, thanks."
- **Drop the leading 1 in frequencies more often when busy:** busy pilots are far more likely to drop the leading 1 and group the digits ("twenty-two seventy-five" for 122.75, not "one two two point seven five").
- **Trailing .XX0 / .XX5:** FAA training tells controllers to say only the first two decimals, since 8.33 kHz spacing never gives both XXX.XX0 and XXX.XX5 with the same first five digits. Possibly confusing to users; maybe not implemented.
- **Shortened GA callsigns, FAA:** after initial contact, a domestic N-number (N835LC, not a foreign one such as CLDTS) can be shortened to N plus the last three characters, or the type plus the last three: "N5LC", "Cessna 5LC", "Bonanza 5LC". Foreign GA callsigns are never shortened by FAA controllers.
- **Shortened GA callsigns, ICAO:** after initial contact, the first letter and the last two: D-IACG becomes "Delta Charlie Golf".
