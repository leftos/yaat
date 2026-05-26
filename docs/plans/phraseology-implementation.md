# Phraseology backlog implementation — handoff

## Context

The systematic FAA-coverage audit ran in two prior sessions and produced [`phraseology-coverage-backlog.md`](./phraseology-coverage-backlog.md) — 799 phrasings classified as **Covered / MissingRule / MissingCanonical / OutOfScope** across 7110.65 Chapters 2/3/4/5/6/7/9 and AIM Chapters 4/5/10.

**Your job:** turn the **219 MissingRule entries** into shipped rules. MissingCanonical entries are deferred to product review; you do not touch those (except to surface them at the end). OutOfScope entries are noise — ignore them.

Each MissingRule entry names a canonical command that already exists in `Yaat.Sim.Commands.CanonicalCommandType` but has no rule in `src/Yaat.Sim/Speech/PhraseologyRules.cs` that produces it from the FAA-cited phrasing. The fix is mechanical: add the literal-token pattern + canonical-output template to `PhraseologyRules.cs`, write a failing test, confirm it passes, run the verbalizer regression check, ship.

## Read these first (in this order)

1. `docs/plans/phraseology-coverage-backlog.md` — the backlog. Every entry has the FAA citation, the canonical, and a one-line note. **Source of truth for what to ship.**
2. `docs/plans/archive/phraseology-coverage-audit.md` — the original audit method spec. Useful background on why entries are bucketed the way they are.
3. `src/Yaat.Sim/Speech/PhraseologyRules.cs` — destination file. Read the header doc-comment carefully — it explains pattern syntax (`literal`, `literal?`, `{capture}`), longest-match precedence, the normalized-transcript assumptions, and which canonicals are intentionally out-of-pilot-scope.
4. `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — how rules get matched. Read enough to understand longest-match + tie-breaking by capture count.
5. `src/Yaat.Sim/Pilot/PhraseologyVerbalizer.cs` — the inverse pipeline. Pilot AI uses this to render canonical commands back into spoken English. Critical: adding a new STT rule MAY accidentally change which rule the verbalizer picks for pilot readback. Always run the verbalizer tests after adding a rule.
6. `src/Yaat.Sim/Commands/CanonicalCommandType.cs` — enum reference (209 members). When a backlog entry names a canonical, grep this file to confirm it exists.
7. `docs/speech-recognition-pipeline.md` — overall STT architecture. Skim before your first PR.
8. `.claude/skills/stt-pipeline-debugging/SKILL.md` — auto-fires on STT investigation. The handoff workflow is "test → rule → verbalizer regression → full suite" — match that pattern.

## Per-stage workflow (TDD, mandatory)

For every stage you ship, follow this loop **in order**. The CLAUDE.md project rule says TDD for sim changes; this is non-negotiable.

1. **Pick a stage** (see "Stages" below). Read every backlog entry in that stage. Note the FAA citations.
2. **Validate FAA citations**. For each phrasing, open the cited section in `.claude/reference/faa/7110.65/` or `.claude/reference/faa/aim/` and confirm the exact wording. If a phrasing in the backlog disagrees with the FAA text, **trust the FAA text** — the audit agents made occasional small wording errors.
3. **Write the failing test FIRST**. New tests go in `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` (or a sibling file). Use existing test patterns — `MapText("…").Should().Be("CANONICAL …")`. Use `TestVnasData.EnsureInitialized()` in the constructor if the test class touches navdata-dependent canonicals (CrossFix needs fix resolution, etc.).
4. **Run the test, confirm it fails for the right reason.** Don't skip this. `timeout 30 dotnet test --filter "FullyQualifiedName~Phraseology" 2>&1 | tee .tmp/test.log`
5. **Add the rule(s)** to the matching `*Rules()` method in `PhraseologyRules.cs`. Pick the category that aligns with `CommandRegistry` (TowerRules, AltitudeSpeedRules, ApproachRules, GroundRules, PatternRules, etc.). Match existing style: literal tokens lowercased, captures in `{name}`, optional words with `?`.
6. **Run the test again. Confirm it passes.**
7. **Run the verbalizer regression suite.** `timeout 30 dotnet test --filter "FullyQualifiedName~Verbalizer" 2>&1 | tee .tmp/test.log`. If you added a rule that introduces a shorter pattern for an already-verbalized canonical, the verbalizer's `PickPreferredRule` may swing and break a pilot-readback test. If something breaks, **do NOT change the test to match the new output** — instead, reorder your new rule below the previously-preferred one (file order matters as a tiebreaker — see `PhraseologyRules.cs:35-51`) or use `SttOnly: true` so the verbalizer skips it.
8. **Run the full suite cross-repo.** `pwsh tools/test-all.ps1` — required when you've touched anything in `Yaat.Sim`. Yaat-server has its own tests that link the same Sim assembly.
9. **Update the backlog.** Move each shipped entry from MissingRule to Covered. Set `Notes: PhraseologyRules.cs:<line>`. Decrement the chapter's MissingRule count and increment Covered in the chapter's `**Ch X totals:**` line. Update the audit-wide table at the bottom.
10. **Update `CHANGELOG.md`.** One bullet per stage under `## Unreleased`, user-visible language. Example: `add: STT recognizes "cross (fix) at (altitude)" / "at or above" / "at or below" phraseology (FAA 7110.65 §4-5)`. The shipped backlog entries are the substantiation; don't paraphrase dev-speak.
11. **Commit.** One commit per stage. Use `feat:` for new canonicals (rare here), `add:` for new rule coverage, `fix:` for rule corrections. Imperative, ≤72 chars. Include the FAA citation in the body, e.g. `Closes 7110.65 §4-5, §4-7 CrossFix coverage gap.`
12. **Pause and ask the user before starting the next stage.** Don't batch stages. The user wants one shippable cluster per session so the diff stays reviewable.

### When something off-script comes up

- **A backlog entry's canonical doesn't exist after all.** The audit agents made errors. Confirm by grepping `CanonicalCommandType.cs`. If genuinely missing, reclassify as MissingCanonical in the backlog and skip it — that goes to product.
- **A phrasing overlaps a recent fix.** The backlog "Notes" field flags many of these as "also at §X-Y-Z". When you ship one rule, scan the backlog for `also at` cross-references and close them all in the same commit.
- **The FAA citation is ambiguous or contradicts the audit's wording.** Don't guess. Run the `aviation-sim-expert` agent with the local FAA reference paths (per CLAUDE.md "Aviation Realism — MANDATORY"). Cite which way the agent resolved it in the commit body.
- **Adding a rule breaks an unrelated test.** Revert the rule. Investigate. Don't iterate on a broken approach more than twice (CLAUDE.md "Revert broken fixes immediately").
- **A stage has more than ~10 entries.** Split it. Smaller commits review faster.

## Stages — ordered by leverage

Each stage corresponds to a canonical (or tightly-related canonical family) that already exists in the enum and is missing one or more rules. They are roughly ordered by how many backlog entries each closes.

### Stage 1 — `CrossFix` (≈15+ entries closed)

The single highest-leverage stage. `CrossFix` appears as MissingRule in 7110.65 §4-3, §4-5, §4-7, §4-8, §5-6, §5-7, §5-9, AIM §4-4, §5-3, §5-4.

**Add to `NavigationRules()` or a new `CrossFixRules()` section:**
- `cross {fix} at {alt}` → `CF {fix} {alt}` (or whatever the existing `ParsedCrossFix` shape expects — verify against `ParsedCommand.cs` and `CrossFixCommandHandler`)
- `cross {fix} at or above {alt}`
- `cross {fix} at or below {alt}`
- `cross {fix} at and maintain {alt}` (compound altitude form from AIM §5-4)
- `cross {fix} at flight level {fl}` (FL variant — `AltitudeResolver` may already normalize)

**Verify before shipping:**
- `ParsedCrossFix` (or equivalent) actually accepts the altitude-constraint variants. If it only accepts "at" (no above/below), this is half MissingRule, half blocked-on-canonical-extension. Surface to product.
- Existing tests: `Yaat.Sim.Tests/Commands/CrossFixCommandHandlerTests.cs` (or similar).

### Stage 2 — `ClimbVia` / `DescendVia` (≈8-10 entries closed)

7110.65 §4-3, §4-5, §4-7, §5-7, AIM §4-4, §5-2, §5-4, §5-5. Critical for STAR/SID modeling.

**Add to `AltitudeSpeedRules()`:**
- `climb via sid` → `CV` (bare form)
- `climb via {sid}` → `CV {sid}`
- `climb via the {sid} departure` → `CV {sid}` (AIM-style phrasing)
- `descend via {star}` → `DV {star}`
- `descend via the {star} arrival` → `DV {star}`

**Hold the "EXCEPT" modifier variants** ("climb via SID except maintain FL180", "descend via the X arrival except cross Y at or above Z") for Stage 3 once CrossFix is in place — they're compounds of (Climb/Descend)Via + CrossFix.

### Stage 3 — `ClimbVia` / `DescendVia` EXCEPT modifiers (≈4 entries)

Depends on Stages 1 + 2. Verify `CommandSchemeParser` supports the compound form via `;`/`,` already; if so, the rule is a multi-clause pattern. If the parser needs work, this becomes blocked-on-canonical.

### Stage 4 — `ClearedApproach` type-token alternation (≈5 entries)

7110.65 §4-8, AIM §5-4. Existing rules at `PhraseologyRules.cs:197-209` only enumerate ILS / RNAV. Extend to Localizer, LOC-BC, VOR, GLS, LDA.

**Approach: introduce an internal helper or duplicate-with-type alternation.** Watch out — the canonical output `CAPP ILS{rwy}` encodes the approach type in the canonical. Adding "Localizer" means the canonical needs to be `CAPP LOC{rwy}` and the downstream `CAPP` dispatcher must accept that type tag. Verify in `ApproachCommandHandler` / wherever `CAPP` is parsed back. If it doesn't, surface to product (MissingCanonical extension needed).

### Stage 5 — `JoinStar`, `JoinAirway`, `JoinRadialInbound/Outbound`, `JoinFinalApproachCourse` (≈8 entries)

Five canonicals in the enum, no rules. 7110.65 §4-4, §4-7, §5-6, AIM §4-5, §5-4.

- `(star) arrival` → `JS {star}`
- `(star) arrival, (transition) transition` → `JS {star} {transition}`
- `via {airway}` → `JA {airway}` (where airway is V12, J533, Q145 etc — verify `NatoLetterNormalizer` handles them)
- `via {navaid} {radial} radial` → `JRI {navaid} {radial}` or `JRO ...` (inbound vs outbound disambiguation)
- `vector to final approach course` → `JFAC`
- `vector to {approach} final approach course` → `JFAC {approach}`

### Stage 6 — `HoldingPattern` full form (≈4 entries)

7110.65 §4-6, AIM §5-3. Bare `hold at {fix}` is covered (`PhraseologyRules.cs:386-388`). Extend to:
- `hold {direction} of {fix} on {radial}, {n} mile leg, left turns` → `HOLD {fix} {direction} {radial} {n} L`
- `hold {direction} of {fix}, as published` → `HOLD {fix} AS PUBLISHED`
- `cleared to {fix}, hold {direction}, as published`

Verify `HoldingPattern` canonical's argument shape before deciding the canonical-output template. Likely needs work.

### Stage 7 — Tower modifier wedges (≈8 entries)

7110.65 §3-9, §3-10. The CTO/LUAW/CTL/LAHSO rules don't tolerate adverbial modifiers between the runway and the verb:
- `runway {rwy} shortened, cleared for takeoff` → `CTO`
- `runway {rwy} full length, cleared for takeoff` → `CTO`
- `runway {rwy} shortened, line up and wait` → `LUAW`
- `runway {rwy} full length, line up and wait` → `LUAW`
- `runway {rwy} shortened, cleared to land` → `CLAND`
- `runway {rwy}, wind {dir} at {vel}, cleared for takeoff` → `CTO` (wind tail)
- `runway {rwy}, wind {dir} at {vel}, cleared to land` → `CLAND`

**Decision needed:** parse the modifier as a silent skip (rule ignores it), or canonicalize it (rule captures it for the sim to use). Wind is informational — silent skip is fine. "Shortened"/"full length" affects landing distance available — the sim doesn't currently model this. Silent skip per audit plan.

### Stage 8 — Taxi/Ground verb synonyms (≈6 entries)

7110.65 §3-7:
- `continue taxiing via {path...}` → `TAXI {path}`
- `proceed via {path...}` → `TAXI {path}`
- `across runway {rwy}` → `CROSS {rwy}` (alternate to "cross")
- `cross runway {rwy} at {taxiway}` → `CROSS {rwy} AT {taxiway}` (intersection variant; verify `CrossRunwayCommandHandler` accepts an `AT` argument)
- `behind {callsign}` → `FOLLOWG {callsign}` (alternate to "follow ... on ground")
- `hold for wake turbulence` / `hold for traffic` → `HOLD` (extend HoldPosition rule line 497 with optional "for {reason...}" tail)

### Stage 9 — `SafetyAlert` + `WakeAdvisory` (≈4 entries)

Canonicals exist (lines 174-175 in `CanonicalCommandType.cs`), no rules.

- `low altitude alert check your altitude immediately` → `ALERT LOW_ALT`
- `low altitude alert check your altitude immediately {mea_mva_moca_mia} in your area is {alt}` → `ALERT LOW_ALT {alt}`
- `traffic alert advise you turn {left|right} heading {hdg} immediately` → `ALERT TRAFFIC TL/TR {hdg}` (composite — verify how `SafetyAlert` is parameterized)
- `caution wake turbulence` (with optional traffic info tail) → `WAKE` (verify `WakeAdvisory` argument shape)

These are controller-issued. Confirm in code review whether YAAT's STT pipeline should accept controller-issued safety alerts as instructor input — they're not pilot transmissions. If not, reclassify as OutOfScope.

### Stage 10 — Pattern-entry "APPROVED" shorthand (≈2 entries)

7110.65 §3-10. Existing `EnterFinal` / `MakeRightTraffic` rules accept "make/enter ..." but not the "(direction) APPROVED" form:
- `straight in approved` → `EF`
- `right traffic approved` → `MRT`
- `left traffic approved` → `MLT`

### Stage 11+ — Remaining single-canonical clusters

Work through the backlog from the top down. Remaining `MissingRule` entries cluster on:
- `Cruise` canonical (7110.65 §4-5, §6-6, AIM §4-4)
- `DepartFix` (`depart {fix} heading {hdg}`) (7110.65 §5-6)
- `LineUpAndWait` / `ClearedForTakeoff` / `ClearedToLand` modifier variants (already covered by Stage 7 to some extent)
- `CircleAirport` directional form (`circle (cardinal) of the airport/runway for a (left/right) base/downwind to runway X`)
- `ExitLeft`/`ExitRight` conditional ("if able, turn left/right ...")
- `LowApproach` with altitude restriction (`cleared low approach at or above {alt}`)
- `ClearedForOption` "option approved" alternate
- `ExpectApproach` non-ILS/RNAV/visual variants (VOR, PAR, ASR, surveillance, precision) — depends on Stage 4 token-alternation infrastructure
- `Speed`/`Mach` "until {fix}" trigger (may need parser work)

For each, follow the same workflow. Stop after each stage and ask the user.

## After all MissingRule stages

When the MissingRule count hits zero (or only blocked-on-product entries remain), surface the **MissingCanonical proposals** from the backlog. Group by theme:

- "Resume X" family (`ResumeOwnNavigation`, `ResumePublishedSpeed`, `ResumeAppropriateVfrAltitudes`)
- "Remain outside" family (Bravo, Charlie airspace entry denials)
- VFR family (`MaintainVfrConditions`, `MaintainVfrOnTop`, `SpecialVfrCleared`)
- Time-based crossing family (`CrossFixAtTime`, `DepartFixAtTime`, `HoldAtFixUntilTime`)
- Traffic-advisory canonical (recurring 7+ sections)
- Side-step canonical
- "Continue" (withheld-landing-clearance) verb

Each is a product decision: does YAAT model this surface? Write a short proposal per family (1 paragraph + the backlog rows) and let the user decide.

## Critical files

**Read:**
- `src/Yaat.Sim/Speech/PhraseologyRules.cs` — destination
- `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — match engine
- `src/Yaat.Sim/Pilot/PhraseologyVerbalizer.cs` — round-trip dependency
- `src/Yaat.Sim/Commands/CanonicalCommandType.cs` — enum reference
- `src/Yaat.Sim/Commands/ParsedCommand.cs` — canonical argument shapes
- `src/Yaat.Sim/Commands/CommandRegistry.cs` — canonical → handler map

**Write:**
- `src/Yaat.Sim/Speech/PhraseologyRules.cs` — add rules
- `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — add tests
- `docs/plans/phraseology-coverage-backlog.md` — move entries MissingRule → Covered, update chapter and audit-wide totals
- `CHANGELOG.md` — one user-visible bullet per stage

**Never write:**
- New helpers / utilities for "future" rules
- Backwards-compat shims (project is pre-release; replace, don't deprecate)
- Optional parameters on `PhraseologyRule` to avoid updating call sites (project rule "Scrutinize optional arguments")

## Verification

Before each commit:
1. `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` — zero warnings
2. `timeout 30 dotnet test --filter "FullyQualifiedName~Speech" 2>&1 | tee .tmp/test.log` — speech-specific tests pass
3. `pwsh tools/test-all.ps1 2>&1 | tee .tmp/test.log` — cross-repo full suite passes
4. `prek run` — pre-commit hooks pass (will run automatically on `git commit` too)

Then commit. Don't push without the user asking.

## Coordination notes

- **Backlog is a live document.** When you ship a stage, update the backlog in the same commit as the rule additions and tests. The chapter totals (`**Ch X totals:** ...`) and audit-wide summary table must always reflect reality.
- **No half-stages.** If a stage's tests don't pass, don't commit the partial work and "fix it next time." Revert and re-plan.
- **The `stt-pipeline-debugging` skill auto-fires** when you mention transcripts or pipeline gaps in conversation. Let it. It encodes the rule-mapper → LLM fallback → verbalizer regression flow.
- **The `aviation-sim-expert` agent** is required for any phraseology change you're unsure about. CLAUDE.md "Aviation Realism — MANDATORY" applies. Read FAA references locally (`.claude/reference/faa/`), not via web search — the agent is instructed to do the same.
- **N-number / GA tail-number callsign handling** (`NatoNearMissResolver.TryResolveSingle`) is already shipped. Don't re-add coverage for callsigns; the backlog "Recent work to NOT re-flag" list explicitly excludes them.
- **Runway-prefix word order** for CTO/CLAND/LUAW/TG/LAHSO/MLT/MRT/ELD/ERD/ELB/ERB is also already shipped. Same exclusion list.

## When you're done

When the backlog's MissingRule count is zero or only blocked-on-product entries remain:
1. Re-run the audit-wide totals: `awk '...' docs/plans/phraseology-coverage-backlog.md` (the same awk one-liner used in the audit's verification step).
2. Update the audit-wide summary table at the bottom of the backlog.
3. Promote this handoff plan + the backlog itself: decide with the user whether to archive both to `docs/plans/archive/` or keep them as living references. The MissingCanonical entries may justify keeping the backlog visible.
4. Surface the remaining MissingCanonical groupings to the user (the "After all MissingRule stages" section above).

Good luck. The backlog already did the hard part — comparing FAA against rules. Your job is the mechanical part: shipping the rules the backlog says are missing, one cluster at a time, with tests proving the gap closed.
