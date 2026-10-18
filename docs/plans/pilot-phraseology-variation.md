# Pilot phraseology variation by frequency load

Design seed for the Linear issue "Vary solo pilot readback phraseology by how busy the frequency is". Owner request, with the owner's own reference examples (written for an earlier virtual-ATC mod) below.

## The idea

Pilots know whether the frequency is busy. On a busy frequency they read back laconically (shorter forms, grouped frequency digits, dropped words); on a quiet one they are more relaxed (fuller forms, an occasional "Alright, " prefix or ", thanks" suffix). The TTS speech rate can follow the same load, a little faster when busy.

Open questions for the design interview (not settled):

- How "busy" is measured (transmissions per minute on the frequency, aircraft on frequency, pending pilot transmissions, the controller's recent transmission density) and over what window.
- Whether variation is per transmission (random within a load band) or per pilot (a pilot personality: terse vs chatty) or both, and how it stays deterministic for replay (seeded).
- Which readbacks get variants first, and where the variants live (`PilotResponder.Build*`, `PhraseologyVerbalizer`, `PilotSpeechText`; `docs/pilot-phraseology.md`, `docs/pilot-phraseology-examples.md`).
- Whether the Terminal text varies with the speech or only the spoken form.
- Speech-rate range and how it combines with Settings › Speech's Speed slider.
- Which forms are FAA-correct (7110.65 / AIM pilot readback guidance) versus merely common; every variant gets an `aviation-sim-expert` ruling.
- The callsign abbreviation notes (FAA N-number after initial contact; ICAO first letter plus last two) and the trailing-digit frequency note: in scope here or separate items.

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
