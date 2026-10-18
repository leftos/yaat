Mock for YAAT-803 (Offer an Air taxi quick command with a destination flyout for helicopters): the three open choices the issue leaves to the design, drawn in the style of the YAAT-569 quick-command mocks (`../quick-command-flyouts/`, `../quick-command-taxi-to-runway-tabs/`). Open `air-taxi-mock.html` in a browser; it works offline and embeds no media.

Data is N812HL, an EC35, at parking HELI1 on KOAK's north field, from the bundled `tests/Yaat.Sim.Tests/TestData/oak.geojson`. KOAK's helicopter positions are three `parking` features, HELI1, HELI2 and HELI (no bundled layout has a `helipad` feature; KOAK and KMIA are the only ones with HELI-named positions, and `CMH.geojson` does not parse as JSON).

The flyout rows use HELI2 and HELI, the nearest of the 165 other parking positions (JSX1, GA1, GA2, JSX2, GA4, JSX3, GA3, GA5 …) and all 16 spots (I8R, A8RN, A8RS, 9, K0LS, K0LN, 2, W, C, 3, E, 7, 1, 4, 5, I30).

The runway rows are the holding positions LayoutInspector builds (`--runway 28L,28R,33,10R,10L,15,30,12 --airport-code KOAK`), named by taxiway: 28R at B, E, G, H, P, J; 28L at B, G, H, P; 33 at C, L, K; 10L at C1, J, P, H, G, E; 10R at J, P, H, G; 15 at D, F, K, L; 30 at W1 to W6; 12 at W7 to W2. A bar within 300 ft of the far threshold is left out, as Taxi to runway does. Distances are straight-line from HELI1, rounded to 50 ft.

Each question has two or three lanes, each with how it works and its worst case; no lane is marked as recommended:

1. Which situations show Air taxi, and whether Push back and Push back to… are hidden for helicopters. Each lane draws N812HL's strip at HELI1 and the four situations' lists: A, all four situations (At parking, Holding on ground, Holding short, Rollout / exit) with the push rows hidden; B, all four with nothing hidden; C, At parking and Holding on ground only, push rows hidden.
2. The destination flyout's grouping, with real names and the command each row sends (`ATXI @HELI2`, `ATXI $I8R`, `ATXI 28R`, `ATXI 28R@E`): A, a tab per group (Pads · parking, Spots, Runways) as `FlyoutTabStrip` draws them; B, one scrolling list with sticky group titles. In both, a runway end is one row for its full-length bar with its other bars as chips. `TaxiRouteFlyout` itself is one flat list with no group titles; lane B's sticky titles come from the first-batch mock's scrolling lists.
3. Whether an "Air taxi to" box is offered and what it sends: A, no box, every parking position listed in a scrolling tab; B, a box sent as typed (`ATXI` and the box, the marker the instructor's to type); C, a filter box over every group, Enter sending the top match with its marker.

The Air taxi glyph is a placeholder drawn for the mock.

## Land and NODEL

`land-nodel-mock.html` asks how the Land flyout (an airborne helicopter's Air taxi flyout: filter box on top, Pads · parking and Spots tabs, no Runways tab, live distances; spots send `LAND $NAME`, pads and parking `LAND @NAME`) offers LAND's `NODEL` suffix, which exempts the aircraft from auto-delete after it lands. Data is N812HL, an EC35, airborne at a point 1.2 nm due south of HELI1, with straight-line distances from that point, rounded to 50 ft. Each lane has how it works and its worst case; no lane is marked as recommended:

- A: a "Keep after landing (NODEL)" check box row at the flyout foot, off each time the flyout opens; while ticked every row and Enter send the command with ` NODEL` (drawn ticked).
- B: a `NODEL` chip on every row that sends the row's command with NODEL; the row itself sends plain LAND.
- C: a "Keep NODEL" check box beside the filter box, with A's effect (drawn ticked).

The Land glyph is a placeholder drawn for the mock.
