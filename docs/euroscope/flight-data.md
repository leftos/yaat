# Flight Data Conventions

## Scratch Pad Strings

One of the most significantly used element is the scratch pad message. In ASRC and VRC this area is just a place for some short comments, but nothing more. In EuroScope some special formatted scratch pad strings are used to communicate additional information:

- VOR, NDB, FIX name - When a point name like VOR, NDB or FIX is entered to the scratch pad it is compiled as a direct to point assignment. The next route of the plane is updated accordingly. If you set a direct point using the popup menu in the COPX tag item, the name of the point is also published via the scratch pad.
- HXXX - Scratch pad string formatted as H* followed by numbers is interpreted as if heading were assigned to the plane. When you assign the heading using the popup menu then the appropriate HXXX format scratch pad message is published. If you need a heading assignment that is not available via the popup menu, you can enter it manually to the scratch pad (eg. H022). To avoid the real scratch pad data to be deleted, the original content is sent just after the heading data.
- RXXXX - An R followed by numbers are interpreted as assigned climb/descend rating.
- SXXX - An S followed by numbers are interpreted as assigned speed in knots.
- MXXX - An M followed by numbers are interpreted as assigned speed in Mach number. Actually the value is used as Mach number multiplied by 100. M75 is used for Mach .75.
- CLEA - Special scratch pad content to indicate clearance received flag.
- NOTC - Special scratch pad content to indicate clearance not received flag.
- ST-UP - Special scratch pad content to indicate statup approved ground status.
- PUSH - Special scratch pad content to indicate push back approved ground status.
- TAXI - Special scratch pad content to indicate taxiing ground status.
- DEPA - Special scratch pad content to indicate departure (take off) clearance.
- HOLD - Part of the Holding List Plugin; Special scratch pad content to be issued after a DCT to indicate a hold over the DCT fix.

## Temporary Altitude

Special values in the temporary altitude assignment:

- 1 - If the temporary altitude is set to 1, EuroScope indicates that the plane is cleared for an ILS approach.
- 2 - If the temporary altitude is set to 2, EuroScope indicates that the plane is cleared for a visual approach.
