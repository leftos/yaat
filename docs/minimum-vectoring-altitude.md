# Minimum Vectoring Altitude (MVA)

> Read this before touching `src/Yaat.Sim/Data/Mva/`, `tools/build-mva-data.py`, or the radar MVA
> display surfaces (`TargetRenderer` altitude tint, the `OnMapRightClicked` MVA item, the Ctrl+hover
> tooltip in `RadarCanvas`/`RadarRenderer`).

YAAT knows the **Minimum Vectoring Altitude** — the lowest MSL altitude at which ATC may radar-vector
an IFR aircraft (7110.65 §5-6-1.a.3) — for any airborne aircraft's position, and surfaces it on the
radar so an instructor can see whether a student is vectoring at, above, or below it.

## Data source — FAA AIXM, not the vNAS videomap

The vNAS MVA videomap (`MVAC.geojson`) is **pure linework**: sector boundaries plus the altitude
numbers drawn as vector glyph strokes, with no machine-readable altitudes and no label↔sector link.
It is unusable as a data source.

Instead YAAT uses the **FAA's authoritative AIXM 5.1 MVA charts** (AJV-A certified):
`https://aeronav.faa.gov/MVA_Charts/aixm/{FACILITY}_MVA_{FUS3|FUS5}.xml`. Each `Airspace` member
carries one MSL `minimumLimit` (the floor) and a CRS84 (lon,lat) polygon with an exterior ring plus
optional interior holes (a higher-floor obstacle island cut out of the surrounding sector). FUS3 = the
standard 3 NM obstacle-clearance buffer; it is the variant YAAT ships. The FAA FUS3 coverage matches
the vNAS MVAC linework exactly (verified by bbox), so grading against FAA FUS3 = grading against the
scope the controller sees.

## Pipeline

1. **`tools/build-mva-data.py`** (offline, `uv run`) parses FAA AIXM XML into a committed GeoJSON
   FeatureCollection — one Polygon feature per sector, `properties.mvaFloorFt` (MSL),
   `properties.facility`. Validates floors, closed rings, US coordinate ranges. `--all` scrapes the FAA
   listing and downloads **every** FUS3 facility (148 of 154 published; 6 are dead 404 links), merging
   them into one Brotli-compressed file. `--facility NCT --overlay …` renders an HTML map (FAA polygons
   + optional vNAS videomap linework via `--videomap-url`) for a per-facility visual cross-check.
   Re-run to refresh: `uv run tools/build-mva-data.py --all`.
2. **Committed fixture:** `src/Yaat.Sim/Data/Mva/FAA_MVA_FUS3.geojson.br` — all 148 facilities, 3,268
   sectors, ~1.1 MB Brotli (content-copied into client and server build output via the Yaat.Sim csproj
   `Data\Mva\*.geojson*` include; the loader decompresses `.br` transparently).
3. **Runtime lookup** (`src/Yaat.Sim/Data/Mva/`): `MvaDatabase.Default` is a lazy process-wide
   singleton mirroring `AirspaceDatabase` (3-tier fixture search, Brotli support, empty-DB no-op,
   `SetInstance` for tests). `MvaSector` does **exterior-minus-holes** containment (inside the
   exterior ring and outside every hole) — unlike `AirspaceVolume`'s union-of-rings model. Queries:
   - `FindSector(LatLon)` → controlling `MvaSector?` (highest floor wins on any residual overlap).
   - `GetFloorFtMsl(LatLon)` → `int?` floor shortcut.
   - `Classify(LatLon, altFt, atBandFt)` → `(MvaRelation, MvaSector?)` — Below/At/Above/NoData.

`GeoMath.PointInRing(LatLon, ring)` and the shared `LatLonBounds` pre-filter back both `MvaSector` and
`AirspaceVolume` (factored out of `AirspaceVolume`; `AirspacePoint` was deleted in favor of `LatLon`).

## Client display surfaces (instructor radar)

All client-side, reading `MvaDatabase.Default` + the aircraft snapshot — no server change:

- **Datablock altitude tint** (`TargetRenderer`): the altitude field is drawn red when the aircraft is
  below the sector floor, amber within ±100 ft ("at"), normal otherwise. Both the STARS datablock and
  the EuroScope tag. Color only — no geometry change, so the draw-vs-hit-test parity is untouched.
  Gated on airborne + IFR (VFR is MSAW-inhibited by default, 7110.65 §5-14-7) + **not established on an
  approach** + in coverage. The approach inhibit is the one server-computed input: `AircraftStateDto`
  carries `IsEstablishedOnApproach` (from `PhaseList.IsEstablishedOnApproach()` in Yaat.Sim), true once
  the aircraft is on final or cleared for a full approach and flying the procedure — the published
  procedure then owns obstacle clearance below the MVA (AIM 4-1-16.a.1). A lateral-only localizer join
  (JFAC/JLOC, holding its assigned altitude) and a go-around still warn. The live
  toggle is `RadarViewModel.ShowMvaHints` (bound to `RadarCanvas.ShowMvaAltitudeTint`), flipped by the
  **MVA** button on the radar DCB. It is **session state, not persisted**: each scenario load re-seeds it
  from `UserPreferences.GetMvaHintDefault(studentPositionType)` — the four per-type defaults (Approach/
  Center on, Ground/Tower off) configured in Settings → Display → Overlays. The reset is wired alongside
  the auto-cleared-to-land seed in `ApplyScenarioResult` / `OnScenarioLoaded` / the timeline path, **and**
  in `ApplyRoomState` (join). The MVA hint is **user-local**, so a joining RPO seeds it from *their own*
  per-type default — unlike room-shared auto-cleared-to-land, which the joiner inherits from the room. The
  join path needs the student position type, so `RoomStateDto` carries `StudentPositionType` (server +
  client).
- **Right-click MVA-at-point** (`RadarView.ContextMenus.OnMapRightClicked`): an informational line in
  the empty-map menu showing the floor + sector at the clicked geo point.
- **Ctrl+hover tooltip** (`RadarCanvas` → `RadarRenderer.DrawMvaHoverLabel`): holding Ctrl while moving
  the cursor draws a label with the MVA floor + sector under the cursor. Threaded through the render
  snapshot (`MvaHover`) like `HoveredFixName`.

## Not yet wired: automated evaluation

A `SoloTrainingEvaluator` finding for illegal below-MVA vectoring is **deliberately not implemented** —
per-facility vectoring-below-MVA rules (SID/DVA/radar-departure exceptions, established-on-approach,
DER+10 NM climb windows, §5-6-3) make a fair automated verdict premature. The display surfaces let a
human judge instead. When that work happens, the MSAW-style thresholds (no finding ≥ floor−200 ft,
advisory −200…−300, bust < −300 with §5-6-3 suppression) and the aviation breakdown are the starting
point.

## Footguns

- **GeoJSON / CRS84 axis order is `[lon, lat]`.** The AIXM posList, the committed GeoJSON, and the
  loader all use lon-first; the loader swaps to `LatLon(lat, lon)`. Swap them and containment flips.
- **Holes are subtractive, not additive.** `MvaSector` rings are exterior + holes; a point in a hole
  belongs to the separately-charted inner sector. Do not reuse `AirspaceVolume`'s OR-of-rings logic.
- **Coverage has gaps.** Between charted sectors (and outside the FAA's ~148-facility footprint) every
  query returns null — callers must treat "no MVA here" as a non-event, never an error.
- **Overlaps resolve to the highest floor.** Adjacent facilities' charts overlap at boundaries; with all
  facilities loaded, `FindSector` returns the highest floor among containing sectors (the conservative,
  safest MVA). At a boundary this can surface a neighbor facility's floor rather than the nominal
  controlling one — acceptable for a display hint, but not a substitute for the facility's own chart.
- **Refresh drift.** The FAA charts update on no fixed cycle; re-run `--all` and commit the regenerated
  Brotli file to refresh. The `source`/`generatedAt`/`facilityCount` metadata records provenance.
