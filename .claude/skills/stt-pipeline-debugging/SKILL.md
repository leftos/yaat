---
name: stt-pipeline-debugging
description: Investigate and iterate on YAAT's speech-to-text pipeline. Auto-fires when a *.yaat-speech-sample.zip path appears in the conversation OR when the user provides a raw / paraphrased ATC transcript with an intent phrase like "why doesn't this match", "should this work", "what canonical would this produce", or "how do I make X recognize this". Walks the pipeline (callsign extract → rule mapper → LLM fallback), identifies the gap, and closes the loop with a TDD-driven fix (test → rule → verbalizer regression check → full suite).
---

# STT Pipeline Debugging

When the user hands you a `.yaat-speech-sample.zip` bundle or a raw transcript and asks why something didn't match (or whether it should), this skill is the runbook.

## The pipeline at a glance

Push-to-talk audio flows through five stages. Each one can drop the ball; the trace shows where.

```
                    biasing prompt
                          │
        ┌─────────────────▼──────────────────────────────┐
   ①    │ WhisperSttEngine.TranscribeAsync               │  raw transcript
        └──────────────────┬─────────────────────────────┘
                           │
        ┌──────────────────▼─────────────────────────────┐
   ②    │ AtcNumberParser.NormalizeDigits +              │  callsign-stripped
        │ ExtractAndStripCallsign + CallsignParser       │  + ICAO callsign
        └──────────────────┬─────────────────────────────┘
                           │
        ┌──────────────────▼─────────────────────────────┐
   ③    │ PhraseologyMapper.MapWithTrace                 │  canonical (or null)
        │   – NatoNearMissResolver                       │
        │   – Custom fix collapse                        │
        │   – NatoLetterNormalizer                       │
        │   – Two-pass greedy rule match                 │
        │   – Runway / cardinal post-validation          │
        └──────────────────┬─────────────────────────────┘
                           │ null on miss
        ┌──────────────────▼─────────────────────────────┐
   ④    │ LocalLlmCommandMapper.MapWithTraceAsync        │  canonical (or null)
        │   – GBNF-constrained generation                │
        │   – Verb + charset post-validation             │
        └──────────────────┬─────────────────────────────┘
                           │
        ┌──────────────────▼─────────────────────────────┐
   ⑤    │ LocalLlmCallsignResolver (callsign fallback)   │  final canonical +
        │   + partial-result surfacing                   │  callsign
        └────────────────────────────────────────────────┘
```

Where each stage lives:

| Stage | Source | Trace field |
|---|---|---|
| ① Whisper biasing + transcript | `src/Yaat.Client/Services/WhisperSttEngine.cs`, `src/Yaat.Sim/Speech/WhisperBiasingPrompt.cs` | `trace.whisperBiasingPrompt`, `trace.rawTranscript` |
| ② Callsign extract | `src/Yaat.Client/Services/SpeechRecognitionService.cs:ExtractAndStripCallsign`, `src/Yaat.Sim/Speech/CallsignParser.cs`, `src/Yaat.Sim/Speech/NatoNearMissResolver.cs` | `trace.callsignStrippedTranscript`, `trace.callsignExtracted` |
| ③ Rule mapper | `src/Yaat.Sim/Speech/PhraseologyMapper.cs`, `src/Yaat.Sim/Speech/PhraseologyRules.cs` | `trace.rule.normalizedTokens`, `trace.rule.matchedRulePatterns`, `trace.rule.outputCanonical`, `trace.rule.failureReason` |
| ④ LLM fallback | `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` | `trace.llm.{systemPrompt, userPrompt, rawOutput, normalizedOutput, failureReason}` (null when rule succeeded) |
| ⑤ Orchestrator | `src/Yaat.Client/Services/SpeechRecognitionService.cs:MapTranscriptWithTraceAsync` | `session.canonicalCommand`, `session.usedLlmFallback` |

## Workflow

### Step 0 — figure out what you have

**Bundle path mentioned** (`*.yaat-speech-sample.zip`):
```bash
mkdir -p .tmp/stt-bundle && unzip -o "<bundle.zip>" -d .tmp/stt-bundle 2>&1 | tail -5
cat .tmp/stt-bundle/manifest.json
cat .tmp/stt-bundle/samples/*/session.json | head -200
```
The session JSON has the full trace — read it before doing anything else. Don't re-derive what's already captured.

**Raw or paraphrased transcript** (no bundle): the user typed it directly, e.g. `"N123AB, make straight-in runway 28R, runway 28R cleared to land"`. You need:
- The transcript verbatim (preserve case and punctuation in case it matters)
- Active callsigns the pipeline would see (ask the user OR assume just the callsign in the transcript)
- The scenario airport (ask if it matters for runway validation, otherwise default to KOAK)

### Step 1 — surface the trace cleanly

If you have a bundle, the trace is already in `session.json`. Pull out:
- `rawTranscript`
- `callsignExtracted` and `callsignStrippedTranscript`
- `rule.{normalizedTokens, matchedRulePatterns, outputCanonical, failureReason}`
- `llm` (null = rule succeeded; non-null = LLM ran, look at its prompts + output)
- `activeCallsigns`, `availableRunwaysByAirport`, `taxiwayNames` — context that constrains the parsers

If you only have a transcript, run it through `PhraseologyMapper.MapWithTrace` via a one-shot xunit test. The smallest harness:

```csharp
// drop into tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTraceTests.cs temporarily
[Fact]
public void Probe()
{
    var ctx = MapContext.Empty with
    {
        AvailableRunways = new Dictionary<string, IReadOnlyList<string>> { ["KOAK"] = ["28R", "28L"] },
    };
    var (result, trace) = PhraseologyMapper.MapWithTrace("<transcript>", ctx);
    throw new Xunit.Sdk.XunitException($"canonical={result?.CanonicalCommand} matched=[{string.Join(';', trace.MatchedRulePatterns)}] reason={trace.FailureReason}");
}
```
Run with `timeout 30 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter Probe 2>&1 | tail -15`. Delete the probe after you're done.

For callsign-extract investigation only, use `SpeechRecognitionService.ExtractAndStripCallsign(transcript, activeCallsigns)` from a probe in `tests/Yaat.Client.Tests/SpeechCallsignExtractionTests.cs`.

For end-to-end Whisper + rule + LLM on an actual audio file, use the existing tool:
```bash
dotnet run --project tools/Yaat.SpeechSandbox -- --pipeline .tmp/stt-bundle/samples/*/audio.wav 2>&1 | tee .tmp/sandbox.log
```

### Step 2 — identify which stage dropped the ball

Walk the stages in order and ask:

1. **Whisper transcript** — did Whisper hear the right words? If not, the biasing prompt may be missing a vocabulary word. Check `src/Yaat.Sim/Speech/WhisperBiasingPrompt.cs`. Don't add per-scenario callsigns to the prompt — the static list is the design.

2. **Callsign extract** — did the orchestrator recover the right ICAO? Common misses:
   - **Whisper mishear of a suffix NATO letter** ("gulf" → "golf"). `CallsignParser.TryNatoLetterOrNearMiss` (via `NatoNearMissResolver.TryResolveSingle`) handles distance-1 cases. If a 2-edit mishear leaks through, consider extending the near-miss table or adding the literal to the NATO protection set.
   - **Airline callsign not in `AirlineTelephony`** — verify the telephony is in `src/Yaat.Sim/Speech/Data/airlines.tsv`.
   - **GA callsign truncated** before consuming all NATO suffix letters — see the recent N346G fix; check `CallsignParser.cs` lines 333–351.

3. **Rule mapper normalization** — `trace.rule.normalizedTokens` is what the matcher actually saw. Differences vs `callsignStrippedTranscript`:
   - Filler-strip dropped a word (see `PhraseologyMapper.FillerWords`).
   - NATO near-miss rewrote a token.
   - NATO letter collapse merged a run of phonetic letters into a taxiway token.
   - Custom-fix collapse swapped a multi-word phrase for a fix alias.

4. **Rule mapper match** — `trace.rule.matchedRulePatterns` shows every rule that fired. Missing clauses mean tokens that no rule consumed.
   - **Word-order gap** — the rule exists with the runway as suffix but the transcript has it as prefix (or vice versa). Recently fixed for CTO/CLAND/LUAW/TG/LAHSO/MLT/MRT/ELD/ERD/ELB/ERB. **Compound clauses with leading-runway + trailing-runway** ("runway 28R, cleared to land" twice in one transmission) are the common case to watch for.
   - **`failureReason = "runway not in scenario"`** — the `{rwy}` capture matched a token but the runway isn't in `MapContext.AvailableRunways`. Either the scenario context is wrong (check `MainViewModel.BuildSpeechContext`) or the captured token is a Whisper mishear `TryRecoverRunway` couldn't fix.
   - **`failureReason = "no rule matched"`** — there's no rule for this phraseology. Add one (see Step 3).

5. **LLM fallback** — only runs when rule mapper returned null.
   - `failureReason = "output failed canonical validation"` — model emitted something `NormalizeOutput` rejected (unknown verb, illegal characters, too long). Try the prompt manually in `tools/Yaat.SpeechSandbox --llm-probe "<transcript>"`.
   - `failureReason = "LLM produced empty output"` — the GBNF grammar refused to produce anything matching the constraint. Probably means the canonical verb the user wanted doesn't exist in `CommandRegistry`.

### Step 3 — fix with TDD

Don't add rules in `PhraseologyRules.cs` without a failing test first.

1. **Write the failing test** in `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTraceTests.cs` (rule mapper) or `tests/Yaat.Client.Tests/SpeechCallsignExtractionTests.cs` (callsign extract). Confirm it fails for the right reason (wrong canonical, not a build error).

2. **Aviation realism** — before adding any new rule, cite the FAA reference. Real ATC phraseology lives in:
   - `.claude/reference/faa/7110.65/` — controller-side
   - `.claude/reference/faa/aim/` — pilot-side
   Use `aviation-realism-review` skill for non-trivial additions. For obvious word-order tweaks ("runway 28R cleared for takeoff" is 7110.65 §3-9-9), one-line cite in the rule comment is enough.

3. **Add the rule** in `src/Yaat.Sim/Speech/PhraseologyRules.cs`. **Order matters for the verbalizer**: `PhraseologyVerbalizer.PickPreferredRule` picks the first-declared rule on capture-count ties. Put the pilot-readback-canonical form FIRST; informal STT-only variants come after. The rule mapper itself is order-insensitive (it scores by longest match).

4. **Check the verbalizer didn't regress**:
   ```bash
   timeout 60 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~Verbalizer|FullyQualifiedName~PhraseologyMapperTrace" 2>&1 | tail -10
   ```

5. **Full speech suite**:
   ```bash
   timeout 60 dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~Speech|FullyQualifiedName~Callsign|FullyQualifiedName~Verbalizer" 2>&1 | tail -5
   ```

6. **Cross-repo sanity** before committing:
   ```bash
   timeout 300 pwsh tools/test-all.ps1 2>&1 | tail -10
   ```

## Common failure modes (quick lookup)

| Symptom in trace | Likely cause | Fix location |
|---|---|---|
| `callsignExtracted` is shorter than expected (e.g. `N346` instead of `N346G`) | NATO suffix mishear stops the GA tail-number scan | Already handled by `TryNatoLetterOrNearMiss`; extend `NatoNearMissResolver` if a 2-edit mishear is needed |
| `rule.matchedRulePatterns` is empty, transcript looks valid | No rule for this phraseology / word order | Add rule to `PhraseologyRules.cs` |
| `rule.failureReason = "runway not in scenario"` | Captured runway isn't in `availableRunwaysByAirport` | Fix `BuildSpeechContext` runway derivation, or extend `TryRecoverRunway` for the Whisper mishear |
| `rule.matchedRulePatterns` has one match but tokens were skipped | Compound transcript where one clause matched and the rest had no rule | Add the missing-clause rule, OR check if greedy match should have consumed more |
| `llm.failureReason = "output failed canonical validation"` | LLM produced an unknown verb or malformed token | Inspect `llm.rawOutput`; probably the user meant a canonical that doesn't exist yet |
| `activeCallsigns` has 16+ entries but only 3 aircraft are spawned | `BuildSpeechContext` not filtering delayed/unsupported | Already filtered by `!IsDelayed && (!IsUnsupported || IsGhostOverlay)` — verify the snapshot wasn't taken pre-fix |
| `availableRunwaysByAirport` is `{}` despite a loaded scenario | Aircraft `Destination`/`Departure` not yet populated AND no `Ground.DomainLayout` | Ground-layout airport fallback covers this; verify a layout is actually loaded |

## Anti-patterns

- **Don't re-run the bundle to "see" the trace.** It's already in `session.json`.
- **Don't add a rule without a failing test.** TDD is the only way to confirm the rule fires for the right transcript.
- **Don't bump capture count in an existing rule.** Adding `{rwy}` to a rule that didn't have it changes the verbalizer's pick. Add a NEW rule instead.
- **Don't put STT-only variants first in `PhraseologyRules`.** The verbalizer picks first-declared on ties; the pilot AI will start speaking the informal form.
- **Don't seed scenario-specific callsigns into the Whisper biasing prompt.** The static ATC vocabulary list is intentional — see commit history if you're tempted to add per-PTT injection.
- **Don't speculate from the audio.** The trace is the ground truth. If something in `session.json` looks wrong, that's the data point — not your re-listening of the WAV.

## Reference: relevant files and tests

- `src/Yaat.Sim/Speech/PhraseologyRules.cs` — the rule list (order matters for verbalizer)
- `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — matcher (order-insensitive, longest match wins)
- `src/Yaat.Sim/Speech/CallsignParser.cs` — airline / GA / hybrid callsign parsing
- `src/Yaat.Sim/Speech/NatoNearMissResolver.cs` — distance-1 NATO rewrites
- `src/Yaat.Sim/Speech/WhisperBiasingPrompt.cs` — static biasing vocabulary
- `src/Yaat.Sim/Pilot/PhraseologyVerbalizer.cs` — inverse direction; first-declared rule wins on capture ties
- `src/Yaat.Client/Services/SpeechRecognitionService.cs` — orchestrator, `ExtractAndStripCallsign`, `MapTranscriptWithTraceAsync`
- `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` — LLM fallback with GBNF + post-validation
- `src/Yaat.Client/ViewModels/MainViewModel.cs:BuildSpeechContext` — builds `SpeechContext` (active callsigns, runways, taxiways, fixes)
- `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTraceTests.cs` — rule mapper tests with trace assertions
- `tests/Yaat.Sim.Tests/Pilot/PhraseologyVerbalizerTests.cs` — pilot-speech regression tests (run after every rule reorder)
- `tests/Yaat.Client.Tests/SpeechCallsignExtractionTests.cs` — callsign extract tests
- `tools/Yaat.SpeechSandbox` — `--pipeline <wav>` for full pipeline on a WAV; `--llm-probe "<transcript>"` for LLM-only investigation
