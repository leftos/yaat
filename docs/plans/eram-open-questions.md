# ERAM questions for experienced controllers

YAAT (a VATSIM ATC trainer) emulates the ERAM display that CRC draws. Where the ERAM EDSM SRS appendices, the CRC manual and other emulators did not settle a behaviour, we made a best guess and built it. Each question below states the guess. If you have worked ERAM, a one-line answer per question ("right", or what the real system does) is all we need; send it to whoever shared this page.

## Data block

1. **Handoff retract, Field E.** When you retract a handoff you initiated, what does your own data block show, and for how long? *Our guess:* `O` followed by your own sector number, the same way an accepted handoff shows `O` plus the receiving sector (CRC can only print the owning sector there).
2. **Vertical conformance while Mode C is lost.** If an aircraft had reached its assigned altitude and then its Mode C drops out (standby), does the "reached" state survive until Mode C returns, or is it re-evaluated from scratch? *Our guess:* it is kept unchanged while Mode C is absent.
3. **Vertical conformance on a coasting or frozen track.** Same question for a track in coast or a frozen (QH) track. *Our guess:* kept unchanged until the track is normal again.

## Conflict alert

4. **IFR against a Mode C intruder already inside minima.** The SRS says immediate alerts are not reported for IFR/MCI pairs. If a Mode C intruder is first detected already inside 5 NM / 1,000 ft of an IFR track, does it ever alert while it stays inside? *Our guess:* no; it alerts only if the pair separates and then closes again.
5. **MCI floor with no adapted value.** With no conflict-alert floor adapted, is 12,500 ft the floor for both the MCI symbol and MCI alerts? *Our guess:* yes, both.

## Departure message (`DM`)

The full design is [eram-dm-design.md](./eram-dm-design.md).

6. **Relative time.** In `DM AAL123 XX05`, is the time five minutes from now or five minutes ago? *Our guess:* from now.
7. **DM on an active flight.** If a flight is already active (auto-departed, or airborne), does DM take it and replace the departure time and fix, or reject it, and with what message? *Our guess:* accepted, and it overwrites.
8. **Departure point.** Does `DM N123AB SAC` only record SAC as the coordination fix on readouts and strips, or also rebuild the converted route from that point? *Our guess:* coordination fix only.
9. **Who may DM.** Which positions can DM a proposed departure without `/OK`, and what error shows without it? *Our guess:* anyone while no sector controls the flight; otherwise only the controlling sector, answered NOT YOUR CONTROL.
10. **Time window.** Does ERAM refuse a DM time in the future, or too far in the past? *Our guess:* no window check.

## Readouts

11. **`LA` time line.** Is flying time rounded to the nearest minute, and does a whole hour print `1 HR` or `1 HR 0 MIN`? *Our guess:* nearest minute, `1 HR`.
12. **`LA` to a radar site.** The SRS example adds a line like `2116 ACP` after the range and bearing. What is that number? *Our guess:* unknown, so we omit the line.
13. **`LB` direction.** Is the `LB` bearing measured from the track to the fix? *Our guess:* yes, as `FROM TB TO FIX` reads.
