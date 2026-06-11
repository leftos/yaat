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

1. **`tools/build-mva-data.py`** (offline, `uv run`) parses the FAA AIXM XML into a committed GeoJSON
   FeatureCollection — one Polygon feature per sector, `properties.mvaFloorFt` (MSL). Validates 150
   sectors, sane floors, closed rings, NorCal coordinate ranges. `--overlay` renders an HTML map
   (FAA polygons + optional vNAS videomap linework via `--videomap-url`) for visual cross-check.
   Re-run to refresh: `uv run tools/build-mva-data.py --facility NCT --variant FUS3`.
2. **Committed fixture:** `src/Yaat.Sim/Data/Mva/NCT_MVA_FUS3.geojson` (content-copied into client and
   server build output via the Yaat.Sim csproj `Data\Mva\*.geojson*` include).
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
  Gated on airborne + IFR (VFR is MSAW-inhibited by default, 7110.65 §5-14-7) + in coverage. Toggle:
  `UserPreferences.ShowMvaAltitudeTint` (default on), Settings → Display → Overlays.
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
- **Coverage has gaps.** Outside the loaded facility's sectors, every query returns null — callers must
  treat "no MVA here" as a non-event, never an error.
- **One facility only (NorCal `NCT`).** Other facilities / scenario-driven loading are not built yet;
  aircraft outside NorCal get no MVA indicator.
- **Refresh drift.** The FAA chart updates on no fixed cycle; re-run the tool and commit the regenerated
  GeoJSON to refresh. The `source`/`generatedAt` metadata records provenance.
