Mock for YAAT-569 (designed quick-command flyouts), brief X2b: the open choices left on the Taxi to runway flyout, drawn on the settled first-batch lane B (one tab per runway end, every variant its own glyph row). Data is UAL455, an A320, at KOAK gate 25 and KSFO gate F12 on the bundled layouts (`oak.geojson`, `sfo.geojson`, plus KATL's ten runway ends from `atl.geojson` for the width question); routes and distances come from a shortest path over the layouts' centrelines measured for the mock, not from the client's pathfinder. Open `quick-command-taxi-to-runway-tabs.html` in a browser; it works offline.

Each question has two or three lanes with how it works and its worst case; the first lane is the recommended one:

1. Keyboard model: A, focus on the first row, Left/Right switch tabs (recommended); B, tablist pattern, focus on the selected tab and Down into the rows; C, focus on the first row, type the runway designator to pick a tab. The three flyouts take real keys.
2. Lazy search states, loading and empty drawn: A, every end a tab, an end searched when its tab is first chosen, an empty end dimmed with "No runway entry found" (recommended); B, search every end on open, tabs only where found; C, every end a tab, the rest searched in the background, an empty tab dropped.
3. Route preview with tabs, on a KOAK scope inset: A, rows preview and tabs stay silent (recommended); B, pointing at a tab previews its first entry.
4. Width of the tab bar (KSFO's eight ends fit, as measured; KATL's ten overflow): A, wrap to a second row (recommended); B, one row with scroll arrows; C, a "More ▾" overflow tab.
   4b. Long variant commands (KSFO 28R, three crossings): A, one line, command trimmed at 180 px; B, two lines, label over the whole command (recommended); C, one line with the via left out of the command.
5. All Commands: A, the same tabbed flyout under All Commands › Taxi to runway (recommended); B, All Commands keeps the nested submenus.
