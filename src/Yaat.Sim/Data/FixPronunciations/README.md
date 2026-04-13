# Fix Pronunciation Hints

This directory contains phonetic pronunciation hints for fixes whose spelling invites mispronunciation. At PTT time, any hint whose fix name matches a programmed fix on the selected aircraft is injected into Whisper's `initial_prompt` alongside the canonical spelling, giving the decoder bias tokens for both forms.

`PhoneticFixMatcher` already normalizes either spelling back to the canonical fix, so downstream code sees the same `MapResult` regardless of which form Whisper produced.

## Directory Organization

Organize files by ARTCC or facility, the same shape as `CustomFixes/`:

```
FixPronunciations/
  ZOA/
    ambiguous.json
  ZLA/
    ambiguous.json
```

## JSON Schema

Each file is a JSON array of pronunciation entries. Use lowercase space-separated phonetic spellings — Whisper's decoder biases on sub-word tokens, so "see rah" is more effective than "SEE-RAH" or "seerah".

```json
[
  {
    "fix": "SYRAH",
    "pronunciations": ["see rah"]
  },
  {
    "fix": "CEPIN",
    "pronunciations": ["seppin"]
  }
]
```

- `fix` — canonical fix name (case-insensitive; stored uppercase internally).
- `pronunciations` — array of phonetic variants. Multiple entries are useful for regional pronunciation differences (e.g., `["see rah", "sih rah"]`).

## When to add a hint

Add a hint only when Whisper is likely to misrecognize the fix name. Add when:

- The spelling is non-obvious (`SYRAH` → "sigh-rah" vs "see-rah").
- The canonical spelling looks like an unrelated common word (`NIKLZ` → "nickels").
- The fix is made-up letters that Whisper tokenizes character-by-character.

Do not add hints for fixes whose spelling already decodes naturally — unnecessary prompt tokens dilute Whisper's bias.
