# Precompute cache

One file per airport, `<FAA>.json.br`: a Brotli-compressed UTF-8 JSON document holding the airport's precomputed layout payload and push targets, as `PrecomputeStore` reads and writes it.

`tools/Yaat.PrecomputeCache` writes these files; nothing here is edited by hand.

## airports.txt

The airport list: a `#` header line, then one FAA id per line, sorted, LF. It names the airports the cache covers, which are the airports any vNAS training scenario names as its `primaryAirportId` or on an aircraft's `airportId` (a flight plan's departure and destination do not count) that have a vNAS ground map. The tool computes and checks the airports it lists. `--refresh-airports` rebuilds it from every ARTCC's scenarios.

## Tool

Run from the repo; every command takes `--airport <id>` (repeatable) to work on named airports instead of the list.

- `dotnet run -c Release --project tools/Yaat.PrecomputeCache` computes each listed airport's entry from the map and NavData vNAS serves now, skipping a current entry (`--force` recomputes it). `--parallel <n>` caps the stands planned at once in total, `--airports-in-flight <n>` (default 4) how many airports are loaded at once.
- `-- --check` reports, offline, each missing or stale entry as a `::warning::` line (an unreadable one is an `::error::` line and exit 1); `--online` also compares the map MD5 and NavData serial with vNAS. CI runs it and a warning does not fail the build.
- `-- --refresh-airports` rewrites `airports.txt`.
- `-- --refresh-envelopes` rewrites `design-group-envelopes.json` and the pinned test copy of the FAA data.

An entry's key carries the airport's sidecar hash, which stales the push targets alone, and its layout sidecar hash (the key field `LayoutSidecarHash`, `AirportSidecarHash.ForLayoutSidecar`), which covers the sidecar `standLeadIns` and `spotEdges` sections and stales the layout too, since the layout build connects those stands and adds those chains. An edit to either section, notes aside, stales the layout and push halves alike; an airport with no `spotEdges` hashes as its lead-ins alone.

An entry stored under the former key field `standLeadInHash` reads as layout-stale until the cache is regenerated.

`--check` names it "the layout sidecar hash differs"; an entry stored without the field reads as layout-stale.

Its tower-cab videomap hash (`VideoMapHash`, `TowerCabVideoMap.Hash`; "" = the airport has no map) is part of the layout match only. A parse with a videomap seats bars on its paint and calibrates each runway's S from it; the shipped entries are built with `VideoMap: null` and written with "", so an entry with a non-empty `VideoMapHash` reads as layout-stale, offline and online, naming "the videomap hash differs".

Its standoff-input hash (`StandoffInputHash`, `HoldShortStandoff.InputHash`) covers the runway inputs the hold-short standoff rule reads for every navdata runway of the airport: width, length, highest ILS category and higher threshold elevation. It is part of the layout match, so a navdata or CIFP change that moves a runway's S stales the entry.

`--check` names it "the standoff-input hash differs". It is "" when no navdata runway is known, and offline it is the committed one, as the map MD5 and the NavData serial are. An entry stored without the field reads as layout-stale.

Regenerate at an AIRAC cycle and before a release, and commit the changed entries. The design is in [`docs/ground/pushback.md`](../../../../docs/ground/pushback.md) ("The push-target cache").
