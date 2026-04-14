# Speech pipeline follow-ups (post LM-Kit milestone)

Two unrelated cleanups left over from the LM-Kit engine swap + speech-pipeline
polish session. Pick either one as a fresh-session task.

## A — Move durable LM-Kit diagnostics out of `Yaat.Scratch` into `Yaat.SpeechSandbox`

`Yaat.Scratch` is meant to be ephemeral per CLAUDE.md ("ad-hoc testing, throwaway console project").
During the LM-Kit swap I accreted several diagnostic modes into `tools/Yaat.Scratch/Program.cs`
that have lasting value and shouldn't disappear when someone next repurposes Scratch:

- [ ] `--lmkit-stt <wav>` — transcribes a WAV through one or more LM-Kit Whisper variants and
      reflects over `LMKit.Speech.SpeechToText` to enumerate its public surface. Useful for
      iterating on Whisper model selection and for confirming `SpeechToText.Prompt` biasing
      behavior on captured PTT clips.
- [ ] `--lmkit-models` — dumps every entry in `LMKit.Model.ModelCard.GetPredefinedModelCards()`
      with capabilities, file size, license, and `IsLocallyAvailable`. Used to decide what to
      expose in `LmKitModelCatalog`. Useful any time LM-Kit publishes a new bundle.
- [ ] `--lmkit-gpus` — enumerates `LMKit.Hardware.Gpu.GpuDeviceInfo.Devices` so we can see what
      LM-Kit detects on the local machine. Useful for diagnosing "model didn't load on GPU"
      reports.
- [ ] `--yaat-catalog` — dumps the FILTERED Whisper + LLM catalogs as they'll appear in the
      Settings picker (after `LmKitModelCatalog.Build*Catalog()` filters and recommended-tier
      annotation). Sanity check before shipping changes to the catalog filter logic.
- [ ] `--llm-probe <transcript>` — runs a single transcript through `LocalLlmCommandMapper`
      (after `AtcNumberParser.NormalizeDigits`) and prints the canonical command. Used for
      "what would the LLM produce here?" investigations during rule-engine triage.

`Yaat.SpeechSandbox` already has the `--pipeline <wav>` console mode and a GUI mode. Adding
these as additional subcommands fits its role as the durable speech-pipeline tool.

### Suggested approach

1. Add each mode as a new top-level branch in `tools/Yaat.SpeechSandbox/Program.cs:Main`
   alongside the existing `--pipeline` dispatch. The argument parser pattern is already there;
   just extend the `args[0]` switch.
2. Lift the inline implementation from `tools/Yaat.Scratch/Program.cs` for each mode. Most of
   them are 20–50 lines so this is mechanical copy-paste.
3. Delete the corresponding modes from `Yaat.Scratch/Program.cs`. Leave the LLM iteration
   harness at the bottom of Scratch (the canned-cases test loop) — that's what Scratch was
   originally built for and it stays useful as a quick prompt-tuning tool.
4. Update `CLAUDE.md`'s **Build & Run** section if it mentions any of the moved modes (it
   currently lists `tools/Yaat.Scratch` as "ad-hoc testing"; consider noting `tools/Yaat.SpeechSandbox`
   alongside it as "speech pipeline diagnostics").

### Verification

For each moved mode, run it once from `Yaat.SpeechSandbox` against a known-good input and
confirm the output matches what `Yaat.Scratch` produced before the move. The existing
`tests/Yaat.Client.Tests/TestData/audio/probe-1.wav` and `probe-2.wav` are reasonable fixtures
for `--lmkit-stt`; the recorded test transcripts ("United 234, climb and maintain 8000, fly
heading 270" and "November 346 Golf, descend maintain 4000, reduce speed to 180") are good
inputs for `--llm-probe`.

---

## B — Fix `AtcNumberParser` mangling "two thirty four" into `"2 34"` (callsign digit drop)

### Symptom

Recorded PTT: `"United two thirty four, climb and maintain eight thousand, fly heading two seven zero"`

Whisper transcribes (with the static biasing prompt) as:

> `"united two thirty four clobin maintain eight thousand to fly heading two seven zero"`

After `AtcNumberParser.NormalizeDigits`:

> `"united 2 34 clobin maintain 8000 to fly heading 270"`

The callsign extractor sees `"united 2"` as the leading callsign tokens and resolves it to
`UAL2` instead of `UAL234`. Verified end-to-end via `tools/Yaat.SpeechSandbox --pipeline`
on `tests/Yaat.Client.Tests/TestData/audio/probe-1.wav`.

### Root cause

`AtcNumberParser.TryReadNumberRun` reads "two" → `2` and "thirty four" → `34` as two separate
number runs. The expected behavior is to combine consecutive number runs that follow the
"hundreds-and-paired" English pattern (`"two thirty four"` → `234`, `"twelve thirty four"` →
`1234`) into a single token.

The existing `FlightNumberToPairedWords` helper does the *inverse* transformation (digits →
pair-spoken words), so the algorithm is half-implemented — we just need a parser that runs the
same logic in reverse during normalization.

### Suggested approach (TDD per CLAUDE.md)

1. **Write the failing tests first** in `tests/Yaat.Sim.Tests/Speech/AtcNumberParserTests.cs`,
   then confirm they fail before fixing:

   ```csharp
   [InlineData("united two thirty four", "united 234")]
   [InlineData("delta twelve thirty four", "delta 1234")]
   [InlineData("november one twenty three", "november 123")]
   [InlineData("two hundred and thirty four", "234")]
   ```

   Note: also add a non-regression case `[InlineData("flight level three five zero", "flight level 35000")]`
   to confirm the FL handling still works after the change.

2. **Update `TryReadNumberRun`** (or add a new pass after it) to detect the
   single-digit-followed-by-pair-cardinal pattern and combine. The `FlightNumberToPairedWords`
   logic is the reference for which token sequences should collapse.

3. **Run the full sim suite** (`timeout 30 dotnet test tests/Yaat.Sim.Tests`) to catch any
   regressions in the existing 2830 cases. Pay special attention to:
   - `NormalizeDigits_PureNumberPhrase` — already covers some of this surface
   - `NormalizeDigits_WithCommandContext` — the comma-stripping and runway-collapse cases
   - `Pattern_Rules` — runway designator captures
4. **Re-run the SpeechSandbox pipeline mode** on `probe-1.wav` and confirm the verdict line
   shows `UAL234` instead of `UAL2`:

   ```
   --pipeline tests/Yaat.Client.Tests/TestData/audio/probe-1.wav
   ```

### Out of scope

The Whisper `clobin` mistranscription of "climb and" in the same probe is a separate issue
(model accuracy, not pipeline correctness). The two-pass filler stripping commit established
the pattern for working around model-side imperfections; a similar approach for `clobin` →
`climb and` would belong in the biasing prompt or in a new `WhisperWordCorrector` post-pass,
neither of which are in scope for this fix.
