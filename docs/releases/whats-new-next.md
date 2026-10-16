# What's new

## Taxi to runway

A ground aircraft's Taxi to runway, in the quick-command strip or under All Commands, opens one flyout with a tab for every runway end of the airport: the aircraft's assigned runway ("30 assigned"), the room's departure runways ("28L departure"), then the rest by runway number. The assigned and departure runways are searched as the flyout opens; any other runway is searched the first time you choose its tab, and its tabs show a spinner until the entries appear.

A runway end with no entry is dimmed with "none".

Each tab lists the runway end's entries: the full-length entry, then each intersection long enough for the aircraft's type, with the runway left, the taxi distance and the route, and under each its For departure and Hold short of runway variants, the whole command shown rather than cut. Preset taxi routes that end at the runway sit beside them. Pointing at a row, or moving to it with the arrow keys, previews its route; Left and Right change tab.

![Taxi to runway for a B738 at an Oakland stand, with runway 30's tab](img/whats-new-taxi-to-runway-tabs.png)

## GFS winds aloft

Load Live Weather downloads a NOAA GFS wind grid for your ARTCC, so winds aloft vary across the sector instead of being one set of layers, and the METAR window says where they came from. A second line under the weather name reads, for example, "Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast": the forecast run and the forecast hour the grid was fetched for.

![The METAR window header naming the GFS grid a saved weather file flies on](img/whats-new-winds-aloft-source.png)

Everyone in the room sees the same line, including a client that joins later and a replay of a recording ("· recorded"). When the grid cannot be had, the line says the weather flies the FAA FD forecast and, on the screen that loaded it, why. Weather saved with Save Weather As carries its grid, so loading the file brings the same winds back ("· saved file"). The status bar's load message names the source too.
