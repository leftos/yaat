# Pilot Phraseology Examples — Reference Corpus

Source corpus for populating `PhraseologyRule.PilotShortcuts` over time. Compiled by Leftos
from prior ATC-app work; the items marked `[Complete]` were canonical readbacks in that app.
Used by YAAT's pilot AI (`src/Yaat.Sim/Pilot/PilotResponder.cs`) once the inversion machinery
ships in M10.1 and shortcut population begins in M10.x.

**Convention used below:**

- **BATC Line** — the formal, textbook ATC readback (matches the `PhraseologyRule.Pattern`
  inversion verbatim — what the controller would say and what M10.1's verbalizer emits).
- **Shorter** — common abbreviation / drop-words form. Pilot still legal phraseology.
- **Less strict** — informal but common variant; deviates from textbook order/structure.
- **Verbose** — conversational form. Uses fillers like "alright", "we'll", "thanks".
- **Variation** — alternate phrasing, similar length to the textbook.

The shortcuts depend on capture-name conventions in `PhraseologyRule` patterns. Where a
shortcut needs a different number formatting (e.g. "thirty-five" for 3500 instead of "three
thousand five hundred"), it uses a suffixed capture form like `{alt-tens}` or `{hdg-paired}`
that the verbalizer formatter knows how to render.

---

## Frequency / Contact

```
[Complete]
BATC Line: Ground 121.9 when ready, N696CL.
Shorter: Ground .9 when ready, N969CL.
(FAA phraseology says that 121 can be implied when referring to Airport frequencies.)
Shorter: .9 when ready, N969CL.
Shorter: Ground .9, N969CL.
Verbose: Ground on 121.9 when ready, N696CL.
Verbose: We'll contact ground 121.9 when ready, N696CL, thanks.
```

```
[Complete]
BATC Line: Contact Taipei Control 124.6, N696CL.
Variation: Taipei Control on 124.6, N696CL.
Shorter: Control on 124.6, N696CL.
Shorter: Control 124.6, N696CL.
Shorter: 124.6, N696CL.
```

## Taxi / Ground

```
[Complete]
BATC Line: Runway 28 taxi via S1, E, N696CL.
Shorter: Runway 28 via S1, E, N696CL.
Shorter: Runway 28 taxi S1, E, N696CL.
Less strict: Taxi S1, E to runway 28, N696CL.
Verbose: Alright, we'll taxi to runway 28 via S1, E, N696CL.
```

```
[Complete]
BATC Line: Ramp 27 taxi via A, B. Hold short runway 33, N696CL.
Shorter: Ramp 27 via A, B, short runway 33, N696CL.
```

```
[Complete]
BATC Line: Cross runway 33 at B. Continue taxi via B, C, G, N696CL.
Shorter: Cross 33 at B, then B, C, G, N696CL.
```

```
BATC Line: Exit right at A, N696CL.
Shorter: Right on A, N696CL.
```

## Direct / Navigation

```
[Complete]
BATC Line: Roger. Cleared direct APU, resume own navigation, N696CL.
Shorter: Direct APU, resume own navigation, N696CL.
Shorter: Direct APU, own nav, N696CL.
Verbose: Alright, we'll head direct APU, then resume own navigation, N696CL, thanks.
```

```
[Complete]
BATC Line: Cleared direct to XLN, N696CL.
Shorter: Direct XLN, N696CL.
```

## Climb / Descend

```
BATC Line: Climb to FL190, N696CL.
Less strict: Up to FL190, N696CL.
```

```
BATC Line: [Climb/Descend] maintain FL190
Shorter: [Up/Down] to 190
Shorter: FL190
Verbose: We'll [climb/descend] and maintain FL190
```

```
BATC Line: [Climb/Descend] maintain 4,500
Shorter: Up/down to 4,500
Shorter: Up/down to 4.5 ("four point five")
Shorter: 4,500 (it's clear that this can't be anything except a non-flight level altitude)
```

```
[Complete]
BATC Line: OVG9ZA, Runway 34, descend to 4,900, N696CL.
Variation: OVG9ZA, Runway 34, down to 4,900, N696CL.
Shorter; OVG9ZA, Runway 34, 4,900, N696CL.
Verbose: Alright, we'll descend via OVG9ZA arrival to 4,900 feet, [Airport Name] runway 34, N696CL.
```

```
[Complete]
BATC Line: N696CL, ready for descent.
Verbose: [ATC Callsign], N696CL, we're approaching our top-of-descent and are ready for lower.
Variation: N696CL, we're at top-of-descent.
Variation: N696CL, top-of-descent.
```

```
[Complete]
BATC Line: Bali Center, Supergreen 695, crossing 9,800 for 12,000 direct LEBAH.
Shorter: Bali Center, Supergreen 695, 9.8 for 12, direct LEBAH.
```

## Heading

```
BATC Line: Turn [left/right] heading 200
Shorter: [Left/Right] 200
Shorter: Heading 200
Shorter: 200 ("two-zero-zero")
Verbose: Turning [left/right] to heading 200
```

## Speed

```
BATC Line: Maintain 250 knots
Shorter: 250 knots
Shorter: Speed 250 ("speed two fifty" / "speed two five zero")
Verbose: We'll maintain speed 250 knots
Verbose: [Slowing down / Speeding up] to 250 knots
Verbose / Playful: [Warp speed ahead / on the brakes], 250 knots
```

## Approach / Arrival

```
[Complete]
BATC Line: Zhuhai, N696CL, FL112 with information India.
Shorter: Zhuhai, N696CL, FL112 with India.
Variation: Zhuhai, N696CL, FL112, we have India.
Variation: Zhuhai, N696CL, FL112, we have India on-board.
```

```
[Complete]
BATC Line: QNH 1016 expect the ILS-Z 34 with the GUBLO transition, N696CL.
Shorter: 1016, expect the ILS-Z 34 with the GUBLO transition, N696CL.
Shorter: 1016, expect ILS-Z 34 from GUBLO, N696CL.
Shorter: 1016, expect ILS-Z 34 via GUBLO, N696CL.
```

```
BATC Line: Cleared direct GUBLO, cross GUBLO at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO, cross at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO at or above 4,900, cleared ILS-Z 34, N696CL.
Shorter: Direct GUBLO at or above 4.9, cleared ILS-Z 34, N696CL.
```

---

## Cross-cutting modifiers

Things that wrap around or modify any of the above readbacks. These are NOT simple
`PilotShortcuts` array entries — they need a small modifier layer in the verbalizer that
reads aircraft / context state.

### "Thanks" / "Alright" sprinkles (personality)

When the radio is quiet, a pilot may sprinkle `", thanks"` (before or after the callsign,
sometimes both) as a suffix, or `"Alright, "` as a prefix. Ties into a future
`PilotPersonality` enum and per-tick "radio busyness" measurement.

```
Example: Left heading 200, N696CL, thanks.
Example: Left heading 200, thanks, N696CL.
Example: Alright, we'll turn left heading 200, N696CL, thanks.
```

### Drop leading 1 in frequencies when radio is busy *(2026-01-25)*

When the radio is busy, pilots are much more likely to drop the leading 1 in frequency
readbacks AND switch to grouped-digit speech: `"twenty-two seventy-five"` instead of
`"one two two point seven five"`. Requires per-frequency activity tracking; defer until we
have meaningful frequency state.

### FAA: drop trailing 5 in `.XX5` *(2026-01-25)*

FAA controller training (per 8.33 kHz spacing): the trailing `.XX5`/`.XX0` digit can be
dropped because there's never both `.XX0` and `.XX5` sharing the first 5 digits. Phrased as
"only the first two decimal digits". **Likely don't implement — would confuse users.** Note
here so future readers know the rule exists but is intentionally elided.

### Shortened GA callsigns — FAA *(2026-01-25)*

After initial contact, FAA domestic GA callsigns (e.g. `N835LC`, but **NOT** foreign GA like
`CLDTS`) can be shortened to `N` + last 3 chars: `"N5LC"`. Also `[type] [last-3]`:
`"Cessna 5LC"`, `"Bonanza 5LC"`. Requires:

- `aircraft.HasEstablishedContactWith[facility]` state (to gate "after initial contact").
- `aircraft.IsDomesticGa` classification (to exclude foreign tails).
- Aircraft-type lookup for `[type] 5LC` form.

Foreign GA callsigns are NEVER shortened by FAA controllers in spoken transmissions.

### Shortened GA callsigns — ICAO *(2026-01-25)*

After initial contact, ICAO callsigns shorten to first letter + last two letters:
`"D-IACG"` → `"Delta Charlie Golf"`. Requires the same initial-contact state plus a
locale flag (`PilotLocale.Faa` / `PilotLocale.Icao`).

---

## Pilot-initiated utterances (no controller equivalent)

These don't invert from `PhraseologyRule` because no controller phrase produces them.
They live in `PilotResponder` directly as pilot-only templates, fired by phase transitions
or proactive timers (M10.4):

- **Spawn check-in (M10.1):** `"Oakland Ground, N696CL at the FBO ramp, ready to taxi."`
- **Top-of-descent ready:** `"Center, N696CL, ready for descent."`
- **With you / on frequency:** `"NorCal Approach, N696CL, descending to ten thousand."`
- **Going around volunteered (M10.5):** `"Going around, N696CL."`
- **Unable (M10.5):** `"Unable, N696CL."` / `"Unable, we're at the gate, N696CL."`
- **Pre-handoff sign-off (M10.4):** `"Departure on 125.35, N696CL, good day."`
