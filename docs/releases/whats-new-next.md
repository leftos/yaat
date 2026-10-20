# What's new

## Taxi to runway

A ground aircraft's Taxi to runway, in the quick-command strip or under All Commands, opens one flyout with a tab for every runway end of the airport: the aircraft's assigned runway ("30 assigned"), the room's departure runways ("28L departure"), then the rest by runway number. The assigned and departure runways are searched as the flyout opens; any other runway is searched the first time you choose its tab, and its tabs show a spinner until the entries appear.

A runway end with no entry is dimmed with "none".

Each tab lists the runway end's entries: the full-length entry, then each intersection long enough for the aircraft's type, with the runway left, the taxi distance and the route, and under each its For departure and Hold short of runway variants, the whole command shown rather than cut. Preset taxi routes that end at the runway sit beside them. Pointing at a row, or moving to it with the arrow keys, previews its route; Left and Right change tab.

![Taxi to runway for a B738 at an Oakland stand, with runway 30's tab](img/whats-new-taxi-to-runway-tabs.png)

## Cleared and Expect approach

Cleared approach and Expect approach, in the quick-command strip or under All Commands › Approach, open one flyout with a tab for every runway of the destination, the room's arrival runways first and a Circling tab last. The default approach (the expected one, else the assigned runway's ILS) is pinned above the tabs with focus on it, so Enter sends it.

Each tab starts with a Visual row, greyed "field not in sight" until the field is reported, and a runway too short for the aircraft's type is dimmed "short". At an untowered airport a "Visual approach to" row clears the aircraft to the airport. The Force toggle in Cleared approach turns every row into its forced form; an Approach box takes an id and sends the top match on Enter; and an IFR aircraft airborne below FL180 gets a "Cancel IFR…" row with an inline confirmation.

## Fly heading

Fly heading in the quick-command strip, and Fly heading, Turn left, Turn right and the two degree turns under All Commands › Heading, open one flyout instead of a list of 72 headings. It has a toggle between Fly heading, Turn left and Turn right (the turn entries open it on their verb), a Heading box that also takes `L180` or `R090`, and a north-up compass rose with its needle on the aircraft's heading, between columns of 5° to 90° chips for a turn left or right by that much.

Click the rose, or drag across it and release, to send that heading; the readout names the heading, the turn it makes and the command (`300 · a right turn of 30° · FH 300`). The arrow keys step the rose by 5°, 30° with Shift, and Enter sends. The needle, the present heading and the chip tooltips follow the aircraft once a second.

![The Fly heading flyout for a B764 heading 077, with the compass rose between the left and right turn chips](img/whats-new-heading-flyout.png)

## Maintain altitude

Maintain altitude, in the quick-command strip and under All Commands › Altitude, opens a flyout instead of a popup. The Altitude box has focus: type `230` or `23000` and the nearest row is outlined and the box shows the command it would send. Below it a list, at most 300 px tall, opens centred on the assigned altitude, else the current one; each row shows its mark (● now, ◆ assigned, ↑ climb, ↓ descend), the altitude and the command, with the MVA line among the rows and the altitudes under it greyed "below MVA" (they still send).

Down from the box goes to the current altitude's row, Up and Down walk the rows, Enter sends and Escape goes back to the strip. The marks, the verbs, the MVA line and the "now" altitude follow the aircraft once a second while the rows stay put. Temporary altitude… and Cruise…, under All Commands › Data Block, open a one-box flyout too, previewing `TEMPALT 120` or `CRUISE 240`, or "Not a valid altitude" when the text cannot be read.

![The Maintain altitude flyout for a B764 at 10,000 ft, with the Altitude box over the list of altitudes](img/whats-new-altitude-flyout.png)

## Assign speed

Assign speed, in the quick-command strip and under All Commands › Speed (with Speed…), opens a flyout instead of a list or an input popup. The Speed box has focus: type `250` and its readout shows the command it would send, `SPD 250`, or "Not a valid speed". Below it are Resume normal speed and Final approach speed (with the type's approach speed), then a Knots grid of chips eight to a row, and at or above FL240 a Mach grid (`M.78` sends `MACH .78`).

The present speed and Mach chips are filled, the assigned speed's chip and the chip the box names are outlined, and a chip's tooltip is its command. Down from the box walks Resume normal speed, Final approach speed and the grids, the arrow keys move within a grid, Enter sends and Escape goes back to the strip. The title and the marks follow the aircraft once a second.

![The Assign speed flyout for a B764 at FL350, with the Speed box over the knots and Mach chips](img/whats-new-speed-flyout.png)

## Direct to

Direct to… and Append direct to…, in the quick-command strip and under All Commands › Navigation, open one flyout instead of a list or an input popup. The title says which verb it is, with "→ {next fix}" while the aircraft navigates to a fix, and then a toggle switches between Direct to (`DCT`) and Append to route (`ADCT`). The Fix box has focus, takes a fix name or an FRD, shows the command Enter would send, and says "Not a known fix" in red when it can send nothing.

Below it, "Remaining route" lists the fixes ahead with a ● on the next one, each with its distance and magnetic bearing from the aircraft (`31 NM · 184°`, the heading it would fly) and its command; a fix the aircraft has passed greys to "passed" for as long as the flyout stays open but still sends. Type a few letters and the route rows narrow and "All fixes · nearest first" lists the 50 nearest other fixes starting with them, however far; an FRD such as `RBL184031` gets its own row. The first row is outlined and Enter sends it. Down moves from the box into the rows, Up walks back to it, and Enter sends the focused row. The rows follow the aircraft once a second and stay put.

![The Direct to flyout for a jet sent direct SUNOL, with SU typed in the Fix box over the route row and the nearest fixes starting with SU](img/whats-new-direct-to-flyout.png)

## GFS winds aloft

Load Live Weather downloads a NOAA GFS wind grid for your ARTCC, so winds aloft vary across the sector instead of being one set of layers, and the METAR window says where they came from. A second line under the weather name reads, for example, "Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast": the forecast run and the forecast hour the grid was fetched for.

![The METAR window header naming the GFS grid a saved weather file flies on](img/whats-new-winds-aloft-source.png)

Everyone in the room sees the same line, including a client that joins later and a replay of a recording ("· recorded"). When the grid cannot be had, the line says the weather flies the FAA FD forecast (or METAR surface winds only, when it has no FD winds aloft) and, on the screen that loaded it, why. Weather saved with Save Weather As carries its grid, so loading the file brings the same winds back ("· saved file"). The status bar's load message names the source too.
