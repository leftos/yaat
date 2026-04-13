# Custom Fixes

This directory contains custom fix/landmark definitions that supplement the standard NavData from VNAS. Use these for facility-specific reference points, training waypoints, or local landmarks.

## Directory Organization

Organize files by ARTCC or facility:

```
CustomFixes/
  ZOA/
    oak-landmarks.json
    sfo-training.json
  ZLA/
    lax-landmarks.json
```

## JSON Schema

Each file is a JSON array of fix definitions. Position is specified via either `lat`/`lon` or `frd` (not both).

### Lat/Lon format

```json
[
  {
    "name": "San Mateo Bridge Toll Plaza",
    "aliases": ["VP915", "TOLLPLAZA"],
    "lat": 37.61814825135482,
    "lon": -122.15262493420477
  }
]
```

### FRD (Fix-Radial-Distance) format

```json
[
  {
    "name": "10nm East of OAK",
    "aliases": ["OAK10E"],
    "frd": "OAK090010"
  }
]
```

FRD strings follow the format `{FIX}{radial:3}{distance:3}` — e.g., `OAK090010` means the OAK VOR, 090 radial, 10nm. The fix name is resolved against NavData at load time.

## Field Reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Display name (informational) |
| `aliases` | string[] | Yes (min 1) | Identifiers for fix lookup. First alias is primary. |
| `lat` | number | If no `frd` | Latitude in decimal degrees (WGS84) |
| `lon` | number | If no `frd` | Longitude in decimal degrees (WGS84) |
| `frd` | string | If no `lat`/`lon` | Fix-Radial-Distance reference |
| `spokenPatterns` | string[] | No | Natural-language phrases for speech recognition |

## Speech recognition patterns

Custom fixes often have verbose multi-word names that controllers say naturally on the radio — "direct to the runway 30 numbers", "proceed to the toll plaza". The speech recognition pipeline can't pick these up through the normal `{fix}` rule capture because that only matches a single token. Adding a `spokenPatterns` array lets the pipeline collapse the multi-word phrase to the canonical alias before rule matching runs.

```json
{
  "name": "Oakland Runway 30 Numbers",
  "aliases": ["OAK30NUM"],
  "spokenPatterns": [
    "runway 30 numbers",
    "the runway 30 numbers",
    "oakland runway 30 numbers",
    "30 numbers"
  ],
  "lat": 37.70208081559119,
  "lon": -122.21521095379472
}
```

### Pattern guidelines

- **Write numbers as digits.** The phraseology normalizer converts spoken numbers ("three zero") to digit form ("30") before custom-fix matching runs. Patterns must match the post-normalization tokens.
- **Include natural prefixed variants** like "the ..." and airport-prefixed forms like "oakland ...". Each variant is matched independently — longest match wins when multiple patterns overlap.
- **Keep patterns distinctive.** A pattern like "the approach" or "final" would collide with existing phraseology and swallow tokens that belong to real rules. Prefer compound phrases that are unambiguous to the specific fix.
- **Case is ignored.** Patterns are lowercased at load time and matched case-insensitively against normalized tokens.
- **One alias per pattern.** The first alias in the `aliases` array is used as the canonical form. If you need multiple aliases to have speech patterns, add the patterns to the entry with the primary alias.

When matched, the spoken phrase is replaced with the canonical alias as a single token. Downstream `{fix}` rule captures (e.g. `direct to {fix}`) see `OAK30NUM` and produce `DCT OAK30NUM` in the command input.
