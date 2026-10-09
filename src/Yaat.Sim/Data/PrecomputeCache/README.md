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

Regenerate at an AIRAC cycle and before a release, and commit the changed entries. The design is in [`docs/ground/pushback.md`](../../../../docs/ground/pushback.md) ("The push-target cache").
