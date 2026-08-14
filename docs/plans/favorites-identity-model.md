# Favorites Identity Model

Rework of the favorite-commands data model (supersedes the base-pool + scope-fields + named-sets model from #354).

## Model

- **A favorite is its own entity** with a stable 8-hex id, stored one json file per favorite. No scope fields on the entity anymore — Label, CommandText, GroundCommandText, Category, colors, sizes, IsSpacer, Id.
- **Every container is a set** referencing favorites by id, all on the same level:
  - `Global` — always visible. Auto-created, cannot be renamed/deleted.
  - `Airport (XXX)` — kind Airport, key = airport id. Visible while that airport is the scenario's primary airport. Created on demand.
  - `Scenario (Name)` — kind Scenario, key = scenario id, display name captured from the scenario. Visible while that scenario is active. Created on demand.
  - Named sets — user-created; visible while loaded (load order preserved; loaded ids live in preferences.json).
- **A favorite can be in any number of sets** (same entity everywhere — one edit updates all appearances). The bar renders each visible container's ordered block in full, so a favorite in two visible containers appears once per container. Container order: Global, Airport(active), Scenario(active), loaded named sets in load order.
- **Orphans survive**: removing a favorite from its last set keeps the entity; the Favorites Editor shows a "Not in any set" view. Only explicit Delete destroys the entity (and clears all memberships).

## Storage

```
%LOCALAPPDATA%/yaat/favorites/
├─ commands/[SanitizedLabel].{8hexid}.json     (one favorite per file)
└─ sets/[SanitizedName].{8hexid}.json          (one set per file: kind, key, name, ordered favoriteIds)
```

- Filenames are cosmetic (sanitized label/name + id suffix so files are human-identifiable); the id inside the json is authoritative. Renaming a label/name renames the file.
- `FavoriteStore` (Yaat.Client.Core) loads everything at startup, saves per-entity on mutation, raises a Changed event the bar/panel/editor follow.
- Loaded named-set ids stay in preferences.json (window profiles capture them, as before, now by id).

## Migration (one-time)

When `favorites/` doesn't exist and preferences.json still has the old fields: base pool partitions by its scope fields into Global / Airport(x) / Scenario(x) sets (relative order preserved; scenario sets fall back to the id as display name), named sets become Named sets, loadedFavoriteSetNames maps to loaded ids, window-profile loaded-set names map to ids. The old preferences fields are then dropped from the schema.

## Export / import

- **Export a set** → `[Name].yaat-favset.zip`: `set.json` + `favorites/[Label].{id}.json` for each referenced favorite, side by side.
- **Export everything** → library zip: `sets/*.json` + `favorites/*.json` (orphans included) + loaded ids.
- **Import** accepts either zip, a single favorite json, or a single set json. Merge by id: same favorite id overwrites the entity; same set id replaces that set's membership list; new ids create entities/sets; a named-set display-name collision with a different id auto-suffixes; referenced-but-missing ids are dropped with a warning. Old `.yaat-favorites.json` formats are removed (unreleased feature — replace, don't deprecate).

## UI

- **Flyouts** (add / edit / blank / batch): the Scope dropdown is gone; a single "In" checkbox list shows every container (Global, active + existing Airport sets, active + existing Scenario sets, named sets). Add defaults to Global; Edit pre-checks current memberships and syncs them on save; Save disabled with zero checked; Delete removes the entity everywhere.
- **Favorites Editor**: left pane lists all containers as peers plus "Not in any set". Loaded checkbox on named sets only. New/Rename/Delete for named sets; Airport/Scenario sets deletable (memberships removed, entities orphaned); Global protected. Right-pane ops: Move Up/Down (within a set), Add to… (membership), Move to…, Remove from set, Delete (entity).

## Status

- [x] FavoriteStore entities + persistence + filename handling
- [x] Legacy migration (preferences + window profiles)
- [x] MainViewModel composition + membership API (display entries carry container id)
- [x] Flyout rework (membership checkboxes)
- [x] Favorites Editor rework (flat container list + orphan view)
- [x] Zip export/import
- [x] Test rewrite (store, compose, membership, import/export, UI updates)
- [x] USER_GUIDE + architecture.md
