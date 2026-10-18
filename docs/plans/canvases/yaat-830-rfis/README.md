Mock for YAAT-830 (Add a Report field in sight quick command and send the field-in-sight state to the menu): the glyph the Report field in sight (`RFIS`) quick command shows, picked by the owner before the build. Open `index.html` in a browser; it works offline, loads no web fonts and has no image files: every surface is drawn in HTML and inline SVG.

`RFIS` is already in the catalog (`MenuCatalog.cs`, `MenuIds.ApproachReportFieldInSight`) and fourth in the Approach situation's default list (`QuickCommandDefaults.cs`), with a glyph in `QuickCommandGlyphs.cs`. Lane A keeps that glyph; lanes B to E are four alternatives. No lane is marked as recommended.

Every lane draws its candidate as `QuickCommandStrip` draws a glyph: the 24 px path, stroke 1.9, round caps and joins, in the Tower family's amber (`#FFC062`), the colour the other Report commands (traffic in sight, N-mile final, at fix) use. Each lane shows:

- AAL204, a B738 on approach to KOAK runway 30, with the Approach strip the `approach-ifr` menu golden gives (Cleared approach, Maintain altitude, Assign speed, Report field in sight, Cleared to land), 32 px in 40 px buttons, the pointer on the RFIS button so the state line reads "Report field in sight — RFIS".
- The candidate beside Cleared visual's eye and Report traffic in sight's crosshair, and the one existing glyph it is nearest to.
- The 22 px inline form, as a menu or flyout row shows it.
- An enlarged view on the 24 px grid.

The app runs Avalonia Fluent dark only (`App.axaml`, `RequestedThemeVariant="Dark"`), so the app surfaces are drawn dark; the page around them follows the viewer's light or dark theme. No candidate uses an eye (Cleared visual's) or a crosshair (Report traffic in sight's).

## Lanes and path data

Each path is drawn on a 24 × 24 view box, as `QuickCommandGlyphs.Tower("…")` takes it, with no rotation or mirror.

- **A, keep: runway with sight ticks (today's glyph).** The runway seen down the approach, a dashed centreline, three short ticks above and beside it. Worst case: its runway is At N-mile final's runway in the same amber, the two differing only in ticks against arcs above it, and the side ticks echo Report traffic in sight's crosshair ticks.

  `M12 2v3 M2 13h3 M19 13h3 M7 21l3-12h4l3 12 M12 12v2 M12 17v2`
- **B, binoculars over the runway line.** Binoculars above the flat runway line Cleared to land and Cleared for takeoff stand on. Worst case: binoculars say "look for" but not what, so they fit Report traffic in sight as well as the field; at 22 px the lenses and bridge can read as eyeglasses.

  `M6.5 9a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7 M17.5 9a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7 M3.2 11.5L5 4h3l1.8 7.5 M14.2 11.5L16 4h3l1.8 7.5 M10 11.5h4 M2 21h20`
- **C, aircraft with a sight line to the runway.** A top-view aircraft at the upper left, a dashed line from it down to a runway in perspective at the lower right. Worst case: a diagonal running down onto a runway is Cleared to land's picture, and Cleared to land is the next button in the strip; the busiest candidate, its aircraft blurs at 22 px.

  `M2 7.5h9 M7.5 7.5l-3-5h-1l1.5 5 M7.5 7.5l-3 5h-1l1.5-5 M3 7.5l-1-2.5 M12.5 10.5l1.3 1.3 M15.3 13.3l1.3 1.3 M13 22l3-5h3l3 5`
- **D, runway framed in viewfinder corners.** A runway in perspective inside four viewfinder corners. Worst case: the corners are Track's and Assume and track's frame (grey, not amber), so on a radar menu that offers both it reads as a track action; it can also read as "take a screenshot".

  `M3 8V3h5 M16 3h5v5 M21 16v5h-5 M8 21H3v-5 M8.5 18l2.3-11h2.4l2.3 11 M12 10.5v2 M12 15v1.5`
- **E, speech bubble holding a runway.** A speech bubble with a runway in it: the pilot calls the field. Worst case: no other Report glyph uses a bubble, so it starts a pattern the other Report commands do not follow; the runway inside is small and at 22 px reads as a letter A in a chat bubble.

  `M5 3h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-8l-4 4v-4H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 14l2-8h2l2 8`
