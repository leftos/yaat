# Airport → Airline Crosswalk

`src/Yaat.Sim/Data/airport-airlines.json.br` maps each generator airport to the airlines that plausibly fly there, with arrival
counts, so the arrival generator can pick a real carrier for a generated callsign at that airport. It is built by
`tools/refresh-airport-airlines.ps1` from **BTS T-100 segment statistics**.

This is a different file from [`airline-fleets.md`](airline-fleets.md)'s `airline-fleets.json` — that one maps an airline ICAO to
the aircraft types it flies; this one maps an airport to the airlines that serve it.

## How it's built

For each generator airport, `refresh-airport-airlines.ps1` maps a BTS carrier code to an ICAO via the OpenFlights `airlines.dat`
IATA→ICAO crosswalk, keeping the airline only if that ICAO exists in `airline-fleets.json`. Source BTS zips cache under
`.tmp/airport-airlines/source/`. Regenerate offline with:

```
tools/refresh-airport-airlines.ps1 -SkipDownload -Idents <meta.target_airports> -BtsSegmentFiles <the two zips>
```

Consumed at runtime by `AirportAirlines.cs` (`src/Yaat.Sim/Data/`), which loads the Brotli fixture lazily and normalizes
K/P-prefixed U.S. ICAOs to local IDs; `airport-airlines.meta` carries the provenance sidecar (source ZIP row counts, target
airports, unmapped BTS carriers). OpenFlights route data backfills only airports with no BTS carrier hits.

## The defunct-code-collision bug (fixed)

OpenFlights reuses 2-letter IATA codes across defunct and currently-operating airlines, and the original build logic took
**last-wins with no validation** — so real US operators got relabeled as unrelated defunct foreign carriers whose ICAO happened to
collide with a *different* `airline-fleets.json` entry:

- `8C` → Shanxi (inactive) → `CXI` → shown as Corendon (8443 arrivals)
- `N3` → Omskavia → `OMS` → shown as **SalamAir** (216 @ OAK — the reported symptom)
- `E7` → European Aviation → `EAF` → shown as Electra

**Fix — two guards in `Read-OpenFlightsAirlines`:**

1. Trust an *inactive* OpenFlights airline's code only when its name matches the fleet airline that would actually be displayed for
   that ICAO.
2. Prefer an ICAO that IS a curated fleet airline over one that isn't.

This drops the mislabels and recovers the correct carrier (`8C`→`ATN`/Air Transport International, `E7`→`ESF`/Estafeta) plus real
internationals (Asiana, Singapore, Nippon Cargo). `N3` has no valid match and is left unmapped.

**Do not add an active-vs-inactive preference** — an *active* foreign airline can legitimately share a US carrier's code
(`EM`→Empire, `NC`→Northern Air Cargo, `CV`→Cargolux); that regressed when tried.

The pick-time `PickCompatibleAirportAirline` weights candidates **linearly** by arrival count (previously `sqrt`, which
over-represented the legitimate long tail by ~100x), so the generated mix matches real traffic share.

## Regression coverage

`AirportAirlinesTests` asserts `CXI`/`OMS`/`EAF` never reappear and `8C`→`ATN`.
