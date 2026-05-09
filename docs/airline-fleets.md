# Airline Fleet Map

`src/Yaat.Sim/Data/airline-fleets.json` is the canonical map of which ICAO Doc 8643
aircraft types each airline (by ICAO designator) operates, with airframe counts.
It is generated from the [Airfleets World Fleet Listing](https://www.airfleets.net/),
a paid quarterly publication.

This document covers how to refresh it and how to consume it from code.

## What it provides

Two pre-computed indexes in one file:

- `by_airline["SWA"]` → `{ name: "Southwest Airlines", country: "USA", types: { B38M: 308, B737: 305, B738: 197 } }`
- `by_type["B38M"]` → `{ SWA: 308, RYR: 144, UAL: 123, AAL: 98, ... }` (sorted by count desc)

Roughly **1,050 airlines** and **77 aircraft types** at issue 123.

## Why Airfleets

We evaluated several sources before settling on Airfleets:

| Source | Why we rejected |
|---|---|
| OpenSky Aircraft Database | Has the right schema (`operatoricao` + `typecode` columns) but the public CSV is months stale; retired tails (e.g. SWA B733, retired 2017) still appear with empty `status`. |
| ADS-B Exchange `basic-ac-db.json.gz` | Daily-fresh but lacks an airline ICAO column (only free-text `OWNOP`). Would need a name→ICAO crosswalk. |
| AeroDataBox API | Live, has the right schema, but treats `active` as "still on the registry" rather than "operating." Returns retired MD88/B732 for Delta and B733 for Southwest as `active: true`. |
| ch-aviation / OAG / Cirium / CAPA | Industry-grade, accurate, but enterprise-priced. |

Airfleets is curated for *currently operating* aircraft and is published quarterly
in PDF form — paid but affordable for a one-off refresh. Quality cross-check on
issue 123: no MD-88 in DAL's fleet, no B733 in SWA's, no E190 in JBU's. Clean.

## Refreshing the map

### 1. Buy and download the issue

Airfleets sells issues per region as PDFs. Buy the latest issue from
<https://www.airfleets.net/> and download the per-region PDFs:

- `World_Fleet_listing_issue_NNN_North_America.pdf`
- `World_Fleet_listing_issue_NNN_Europe.pdf`
- `World_Fleet_listing_issue_NNN_Asia.pdf`
- `World_Fleet_listing_issue_NNN_Africa.pdf`
- `World_Fleet_listing_issue_NNN_Eurasia_Middle_East.pdf`
- `World_Fleet_listing_issue_NNN_South_America.pdf`
- `World_Fleet_listing_issue_NNN_Oceania.pdf`

**Do not commit the PDFs.** They are paid content. Save them somewhere outside
the repo (e.g. `~/Downloads/`).

### 2. Run the refresh tool

From the repo root:

```bash
uv run tools/refresh-airline-fleets.py \
    ~/Downloads/World_Fleet_listing_issue_NNN_North_America.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_Europe.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_Asia.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_Africa.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_Eurasia_Middle_East.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_South_America.pdf \
    ~/Downloads/World_Fleet_listing_issue_NNN_Oceania.pdf
```

This rewrites:

- `src/Yaat.Sim/Data/airline-fleets.json` (canonical map; committed)
- `src/Yaat.Sim/Data/airline-fleets.meta` (provenance: per-file SHA-256, row counts; committed)

The tool will print parser stats (airlines found, mapped aircraft, unmapped
variants) for each region. A small unmapped-aircraft count (typically <100
out of ~30,000) is normal — see [Parser caveats](#parser-caveats) below.

### 3. Spot-check the diff

Before committing, eyeball a few key airlines to confirm the data looks current:

```bash
git diff src/Yaat.Sim/Data/airline-fleets.json | head -100
```

Sanity checks:

- Major US airlines (`SWA`, `DAL`, `UAL`, `AAL`, `JBU`) should each gain new types
  for recent deliveries (e.g. `B38M` counts increasing) and lose types for recent
  retirements.
- The `metadata.source_date` field should match the new issue's cover date.
- `metadata.airlines_count` should be in the 1,000–1,200 range.

### 4. Commit

Standard commit style:

```bash
git add src/Yaat.Sim/Data/airline-fleets.json src/Yaat.Sim/Data/airline-fleets.meta
git commit -m "data: refresh airline fleet map to Airfleets issue NNN"
```

## Consuming from code

```csharp
using Yaat.Sim.Data;

// Direct lookup: "what aircraft does Southwest operate?"
if (AirlineFleets.TryGetAirline("SWA", out var info))
{
    Console.WriteLine($"{info.Name} ({info.Country})");
    foreach (var (type, count) in info.Types)
    {
        Console.WriteLine($"  {count}x {type}");
    }
}

// Reverse lookup: "who operates the 737 MAX 8?"
if (AirlineFleets.TryGetAirlinesForType("B38M", out var operators))
{
    foreach (var (airline, count) in operators.Take(10))
    {
        Console.WriteLine($"  {airline}: {count}");
    }
}

// Boolean check: "does this airline operate this type?"
bool swaFliesMax = AirlineFleets.Operates("SWA", "B38M");  // true
bool dalFliesMd88 = AirlineFleets.Operates("DAL", "MD88"); // false (retired 2020)
```

The map is loaded lazily on first access. Lookups are O(1) in either direction.

## JSON schema

```jsonc
{
  "metadata": {
    "source": "Airfleets World Fleet Listing Issue 123",
    "source_date": "2026-05-01",
    "generated_utc": "2026-05-09T17:59:40Z",
    "regions_parsed": ["Africa", "Asia", "Eurasia_Middle_East", "Europe",
                       "North_America", "Oceania", "South_America"],
    "airlines_count": 1053,
    "types_count": 77,
    "tool": "tools/refresh-airline-fleets.py"
  },
  "by_airline": {
    "SWA": {
      "name": "Southwest Airlines",
      "country": "USA",
      "types": { "B38M": 308, "B737": 305, "B738": 197 }
    }
    // ...
  },
  "by_type": {
    "B38M": { "SWA": 308, "RYR": 144, "UAL": 123, "AAL": 98 /* ... */ }
    // ...
  }
}
```

Inner maps within `by_airline.<icao>.types` and `by_type.<icao>` are sorted by
count descending for readable PR diffs.

## Parser caveats

The parser (`tools/parse_airfleets.py`) extracts text via [pdfplumber](https://github.com/jsvine/pdfplumber)
and uses positional grouping to reconstruct columns. A few known edge cases:

- **Stray DHC-8 tokens (~90 unmapped per refresh).** The Airfleets PDFs print
  Dash 8 variants as `"DHC-8" + " " + "NNN"` (two tokens). When the variant
  number lands on a different visual line than `DHC-8`, the parser can't pair
  them. These show as unmapped in `airline-fleets.meta`. Fleet counts still
  reflect the bulk of correctly-paired entries; the remainder are noise.
- **Cross-airline leaks (rare).** When a section boundary aligns badly with a
  page break, a few airframes at the end of one airline can be attributed to
  the next. Observed: 5 stray Dash-8/B38M entries in `DAL` (out of ~1,000) at
  issue 123. Filter with `count >= 5` or `>= 1%-of-total` if your consumer
  needs a noise-free view.
- **Coverage limits.** Airfleets only covers commercial aircraft types listed
  on the cover page (A220–A380, B717–B787, CRJ, EMB, MD-80/90, etc.). Bizjets,
  general aviation, and military types are out of scope. Small carriers that
  fly only excluded types may not appear at all.
- **Source-date drift.** The map is a snapshot from the issue's publication
  date. Fleet *type composition* per airline drifts on a ~yearly timescale;
  exact counts drift faster. Refresh annually or per-issue if cost permits.

Run with `--help` for invocation details. The standalone parser is also
available for diagnostics:

```bash
uv run tools/parse_airfleets.py <pdf_path> -o .tmp/
# writes airfleets-parsed.json, airfleets-by-icao.json, airfleets-unmapped-variants.txt
```

## Related files

- `tools/parse_airfleets.py` — pdfplumber-based parser library (also runnable standalone)
- `tools/refresh-airline-fleets.py` — production refresh entry point
- `src/Yaat.Sim/Data/AirlineFleets.cs` — C# loader
- `src/Yaat.Sim/Data/airline-fleets.json` — generated map (committed)
- `src/Yaat.Sim/Data/airline-fleets.meta` — provenance sidecar (committed)
