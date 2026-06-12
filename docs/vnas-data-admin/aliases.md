# Aliases

Aliases provide a way to create shortcuts to save controllers typing lengthy commands to text pilots. An Alias is a short dot command that gets expanded into a longer string of text before it is sent to pilots.

Here's an example of a common Alias:

`.dm descend and maintain $1`

If this Alias is uploaded and a controller types `.dm 6000`, the following text would be sent:

`descend and maintain 6000`

Notice the `$1` was replaced with the first parameter typed after the Alias name. Up to nine parameters (`$1` through `$9`) can be used in Alias definitions.
Here's an example of an Alias which uses more than one parameter:

`.trd turn right heading $1, proceed direct $2 when able`

To use this Alias, controllers might type `.trd 350 PUT`. The resulting text sent on the frequency would be:

`turn right heading 350, proceed direct PUT when able`

## Alias Variables

In addition to substituting parameters using `$1`, `$2`, etc., there are also variables and functions available for creating more complex Aliases. A variable is a special word, preceded with a dollar sign, that is replaced with an appropriate value before the text is sent on the frequency. As an example, the variable `$squawk`, if present in an Alias, will be replaced with the pilot's currently assigned squawk code before the text of the Alias is sent to the pilot. Consider the following Alias:

`.sq reset transponder, squawk $squawk and ident`

If a controller typed `.sq`, and the selected aircraft was assigned 3405 as its squawk code, the following text would be sent:

`reset transponder, squawk 3405 and ident`

The following table lists all of the variables available when constructing Aliases:

Table 1 - Alias variables

| Variable | Description |
| --- | --- |
| `$squawk` | Inserts the assigned squawk code for the selected aircraft. Inserts the aircraft's current squawk code if none assigned. |
| `$route` | Inserts the aircraft's route not including the departure and destination airfields. |
| `$fullroute` | Inserts the aircraft's full route. |
| `$arr` | Inserts the aircraft's destination airport. |
| `$dep` | Inserts the aircraft's departure airport. |
| `$sid` | Inserts the aircraft's filed departure procedure, if any. |
| `$star` | Inserts the aircraft's filed arrival procedure, if any. |
| `$cruise` | Inserts the aircraft's filed cruise altitude. |
| `$calt` | Inserts the aircraft's current altitude. |
| `$callsign` | Inserts the controller's callsign. |
| `$aircraft` | Inserts the aircraft's callsign. |
| `$userid` | Inserts the aircraft's user ID. (The pilot's CID.) |
| `$com1` | Inserts the controller's primary frequency. |
| `$myrealname` | Inserts the controller's full name. |
| `$winds` | Inserts the winds at the aircraft's departure field if the aircraft is not airborne, otherwise inserts the wind at the destination airport. |
| `$time` | Inserts the current Zulu time. |
| `$alt` | Inserts the aircraft's assigned temporary altitude, if assigned, otherwise the aircraft's filed cruise altitude. |
| `$temp` | Inserts the aircraft's assigned temporary altitude. |

## Alias Functions

vNAS also provides several functions for inserting information into transmitted text. A function differs from a variable in that a function can accept a parameter to alter the text that is inserted in place of the function. The following table lists all of the functions available when constructing an Alias:

Table 2 - Alias functions

| Function | Description |
| --- | --- |
| `$context(aircraft)` | Uses the specified aircraft ID for all variable and function substitution for the remainder of the alias. The $context() function must be at the beginning of the alias, separated with at least one space from the rest of the alias. |
| `$metar(airport)` | Inserts the last METAR for the specified airport. |
| `$altim(airport)` | Inserts the current altimeter setting for the specified airport. |
| `$wind(airport)` | Inserts the current winds for the specified airport. |
| `$type(callsign)` | Inserts the ICAO aircraft type for the specified aircraft. |
| `$radioname(SectorID)` | Inserts the radio name for the specified Controller List entry. If the controller doesn't specify a Sector ID, the controller's Radio Name will be inserted. |
| `$freq(SectorID)` | Inserts the primary frequency for the specified Controller List entry. If the controller doesn't specify a Sector ID, the controller's primary frequency will be inserted. |
| `$atccallsign(SectorID)` | Inserts the callsign for the specified Controller List entry. |
| `$dist(fix)` | Inserts the aircraft's distance to the specified fix in nautical miles. A fix can be an intersection, VOR, NDB or Airport. |
| `$bear(fix)` | Inserts the aircraft's bearing to the specified fix, expressed as a cardinal compass direction. A fix can be an intersection, VOR, NDB or Airport. |
| `$oclock(fix)` | Inserts the clock direction relative to the aircraft to the specified fix. A fix can be an intersection, VOR, NDB or Airport. |
| `$ftime(offset)` | Adds the specified offset (in minutes) to the current Zulu time and inserts the results. If the controller doesn't specify an offset, the current Zulu time will be inserted. |
| `$uc(text)` | Converts the specified text to upper case. |
| `$lc(text)` | Converts the specified text to lower case. |
| `$urlescape(text)` | Converts special characters in the specified text into the URL-safe equivalent. |

Here's an example of a function used in an Alias:

`.initvec fly heading $1, .dm $2, vectors ILS runway $3 approach, $4 altimeter $altim($4)`

To use this example Alias, a controller might type `.initvec 070 6000 33L KBOS`

This would result in the following text being sent on the frequency:

`fly heading 070, descend and maintain 6000, vectors ILS runway 33L approach, KBOS altimeter 2975`

Notice that this is a nested Alias. A nested Alias is one which uses additional Aliases in its text. In the above example, the `.dm` Alias is nested within the `.initvec` Alias. You can nest as many Aliases as you like within a single Alias.

## Uploading and Downloading

![Alias upload and download](img/aliases/edit.png)

*Alias upload and download*

Aliases are typed, one per line, in a `.txt` file. To upload an Alias file, click the **Upload** button on the Alias information box on the ARTCC's homepage. This file can later be retrieved by clicking the **Download** button.
