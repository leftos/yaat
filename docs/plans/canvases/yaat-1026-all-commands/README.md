Mock for YAAT-1026: the aircraft context menu's All Commands reorganized, the situation's quick commands as text rows, the command alias on every row, and the "Show quick command icons" setting (on by default; off removes the icons and puts All Commands' submenus at the root).

Open `index.html` in a browser; it works offline, loads no web fonts and has no image files: every surface is drawn in HTML and inline SVG.

## How to read the page

It opens with the owner's goal and ruling (verbatim), what YAAT-1018 and YAAT-1028 settled (drawn as given, not as lanes), and what was found in the source. Then one artboard per numbered decision, its lanes side by side, the recommended lane first; every lane draws the other decisions at their recommended answer. Under each drawing a measured line gives each menu's height, width and row count; the worst cases quote those numbers. The Open decisions list at the end repeats every option with its worst case.

Two aircraft: N123AB (C172, VFR, left downwind 28L at KOAK, the default Pattern list plus a custom "Say altitude" entry) and UAL123 (B738, IFR, taxiing at KSFO, the default Taxiing list as its predicates would trim it).

## The decisions, numbered as on the page

1. All Commands grouping: A by task in the strip's categories (Runway, Taxi, Vectors, Approach, Pattern, Pilot reports, Track and coordination), thin flight groups merged into Vectors; B by family as today; C by family with Heading/Altitude/Speed/Navigation merged into Flight. Make closed traffic goes to Pattern and Proceed on course to Vectors / Navigation / Flight.
2. Track, Data Block, Squawk and Favorites: A the first three move under All Commands, Favorites stays at the root; B all four stay at the root; C all four move.
3. Text rows beside the strip: A icons on shows only entries without an icon, icons off every entry under the strip's labels; B icons on repeats every entry as text; C icons off in list order, unlabelled.
4. Alias format: A right-aligned dimmed monospace column; B monospace after the label; C in parentheses. 4.1 multi-verb rows (a up to two verbs, b first verb, c none); 4.2 rows with no command of their own (a blank except Warp… WARP and Delete DEL, b blank on every box/window row); 4.3 spelling (a the user's primary verb, b canonical).
5. Icons off, the root: A quick commands first, then the former All Commands children; B the groups first; C keep an All Commands submenu (departs from the ruling).
6. The setting: A Settings › Input › Quick commands; B Settings › General › Appearance. 6.1 Import/Export (a settings bundle, b with the quick-command lists file, c machine-local); 6.2 reach (a every strip including the point menu and the delayed-spawn release strip, b the aircraft menu only).

## Not from real data, or not checked

- Callsigns, positions, runways, the custom entry, the user's verb scheme in 4.3 and the point FRD are illustrative.
- Category labels in text rows, block labels inside submenus, the alias column and the Settings checkbox are drawings of options, not built UI.
- The strip is packed greedily in list-first category order; 1028's own mock settles packing and order.
- Which entries apply to each aircraft is a static read of the predicates; nothing was run in the client.

Source files read: `MenuCatalog.cs`, `SharedMenuGroups.cs`, `AircraftMenuBuilder.cs`, `QuickCommandDefaults.cs`, `QuickCommandGlyphs.cs`, `HeadingFlyout.cs`, `AltitudeFlyout.cs`, `SpeedFlyout.cs`, `HoldFlyout.cs`, `CommandScheme.cs`, `CommandRegistry.cs`, `UserPreferences.Bundle.cs`, `SettingsNavigation.cs`, `QuickCommandsSection.axaml`, `SettingsSearchCatalog.cs`, and the mocks `quick-command-strip-categories/` and `yaat-569-f-pickers/`.
