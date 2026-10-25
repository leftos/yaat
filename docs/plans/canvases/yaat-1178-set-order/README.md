# YAAT-1178: order of the favorite sets on the bar

GitHub #1351 (WIZRAD). The favorites bar composes its sets in a fixed order (`FavoriteStore.ComposeDisplay`: Global, the active airport's set, the active scenario's set, then loaded named sets in `UserPreferences.LoadedFavoriteSetIds` order). WIZRAD wants Global's favorites after named set "S1", for any number of loaded sets.

Question: how does the user order the sets? `index.html` shows each option as a lane: the control the user touches and the resulting bar, with Global placed after S1.

- **A**: Set Up / Set Down buttons in the Favorites Editor; the left list leads with the sets on the bar in bar order, Global a movable row.
- **B**: drag to reorder in the bar's Sets flyout, Global a draggable row.
- **C**: Move up / Move down (and Move to start / end) in a right-click menu on each row of the Sets flyout.
- **D**: ▲ / ▼ buttons on each row of the Sets flyout.

In every option the airport and scenario sets are one row each, a slot for whichever airport or scenario set is active, ordered in the same list as the named sets and Global.

Review copy (media embedded): `uv run --no-project python ~/.claude/tools/mock-embed/embed_media.py docs/plans/canvases/yaat-1178-set-order/index.html .tmp/mocks/yaat-1178-set-order`.
