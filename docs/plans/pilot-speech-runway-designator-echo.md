# Plan: Pilot-speech runway phonetics + concise terminal echo

## Context

Spun out of issue #193 triage (user-directed). Two separate defects in how the
simulated pilot's transmissions are spoken (TTS) and shown (terminal `[Say]` echo),
both visible in the #193 bundle's client log:

```
[Say] ENY3516: cleared for takeoff runway zero eight right, envoy thirty five sixteen.
```

1. **TTS must not zero-prefix single-digit runways.** A runway should be read
   **"runway eight right"**, not "runway zero **eight** right". The phonetic verbalizer
   is reading the zero-padded canonical designator (`08R`) digit-by-digit; it should
   drop the leading zero for speech (read `8R` as "eight right", `9` as "nine").
2. **The terminal `[Say]` echo must be concise, not the verbatim TTS string.** Today the
   terminal shows exactly what is fed to TTS. It should instead show a compact controller-
   style form. Example: for what is spoken as *"november two two five right, runway eight
   right, taxi via bravo charlie delta"* the terminal should read **`N225R, runway 8R taxi
   via B C D`**. Terminal and TTS are independent outputs built from the canonical command
   — the terminal form is not a regex-strip of the TTS string.

## Why both / how to apply

This follows the established principle that pilot-speech builders emit **terminal and TTS
separately** from the canonical command (do not compact one into the other). The fix is to
make the phonetic (TTS) path drop leading zeros on runway designators, and to ensure the
terminal path renders the concise abbreviated form (callsign as written, `8R`/`9` runway,
single-letter taxiways) rather than echoing the spoken phonetics.

## Likely files

- `src/Yaat.Sim/Pilot/PhraseologyVerbalizer.cs` — phonetic runway/number verbalization.
- `src/Yaat.Sim/Pilot/PilotResponder.cs` — builds pilot read-backs.
- `PilotVoiceService` / client terminal `[Say]` rendering — terminal echo path.
- Reference: `docs/solo-training-pilot-speech.md`.

## Deliverable

1. TDD: verbalizer test that `8R` → "eight right" (and `9` → "nine", `8L` → "eight left"),
   not "zero eight right".
2. TDD: terminal-echo test that the `[Say]` terminal string is the concise form
   (`N225R, runway 8R taxi via B C D`) while the TTS string stays fully phonetic — built
   independently from the canonical command.
3. Verbalizer regression check across existing phraseology tests (per the STT/phraseology
   workflow) to ensure no other readbacks regress.
