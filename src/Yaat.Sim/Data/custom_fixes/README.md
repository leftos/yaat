# Custom Fixes

This directory contains custom fix/landmark definitions that supplement the standard NavData from VNAS. Use these for facility-specific reference points, training waypoints, or local landmarks.

## Directory Organization

Organize files by ARTCC or facility:

```
custom_fixes/
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
