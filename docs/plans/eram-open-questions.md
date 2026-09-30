# ERAM questions for experienced controllers

YAAT (a VATSIM ATC trainer) emulates the ERAM display that CRC draws. Where the ERAM EDSM SRS appendices, the CRC manual and other emulators did not settle a behaviour, we made a best guess and built it. Each question below states the guess. If you have worked ERAM, a one-line answer per question ("right", or what the real system does) is all we need; send it to whoever shared this page.

## Data block

1. **Handoff retract, Field E.** When you retract a handoff you initiated, what does your own data block show, and for how long? *Our guess:* `O` followed by your own sector number, the same way an accepted handoff shows `O` plus the receiving sector (CRC can only print the owning sector there).
2. **Vertical conformance while Mode C is lost.** If an aircraft had reached its assigned altitude and then its Mode C drops out (standby), does the "reached" state survive until Mode C returns, or is it re-evaluated from scratch? *Our guess:* it is kept unchanged while Mode C is absent.
3. **Vertical conformance on a coasting or frozen track.** Same question for a track in coast or a frozen (QH) track. *Our guess:* kept unchanged until the track is normal again.

## Conflict alert

4. **IFR against a Mode C intruder already inside minima.** If a Mode C intruder is first detected already inside 5 NM / 1,000 ft of an IFR track (a pop-up), does ERAM alert? *Our guess:* yes, as an ordinary current alert; the SRS note that immediate alerts are not reported for IFR/MCI pairs applies only to the immediate kind.
5. **MCI floor and 1200.** With no floor adapted, is 12,500 ft the floor for both the MCI symbol and MCI alerts, and is a 1200 VFR inside that band an MCI that alerts against IFR traffic? *Our guess:* yes to both; below the floor a 1200 draws as a VFR target and anything else uncorrelated as an Uncorrelated Beacon.

## Departure message (`DM`)

The full design is [docs/eram/dm-design.md](../eram/dm-design.md).

6. **Relative time.** In `DM AAL123 XX05`, is the time five minutes from now or five minutes ago? *Our guess:* from now.
7. **DM on an active flight.** If a flight is already active (auto-departed, or airborne), does DM take it and replace the departure time and fix, or reject it, and with what message? *Our guess:* accepted, and it overwrites.
8. **Departure point.** Does `DM N123AB SAC` only record SAC as the coordination fix on readouts and strips, or also rebuild the converted route from that point? *Our guess:* coordination fix only.
9. **Who may DM.** Which positions can DM a proposed departure without `/OK`, and what error shows without it? *Our guess:* anyone while no sector controls the flight; otherwise only the controlling sector, answered NOT YOUR CONTROL.
10. **Time window.** Does ERAM refuse a DM time in the future, or too far in the past? *Our guess:* no window check.

## Readouts

11. **`LA` time line.** Is flying time rounded to the nearest minute, and does a whole hour print `1 HR` or `1 HR 0 MIN`? *Our guess:* nearest minute, `1 HR`.
12. **`LA` to a radar site.** The SRS example adds a line like `2116 ACP` after the range and bearing. What is that number? *Our guess:* unknown, so we omit the line.
13. **`LB` direction.** Is the `LB` bearing measured from the track to the fix? *Our guess:* yes, as `FROM TB TO FIX` reads.

## Commands YAAT is adding (`RK`, `UR`, `SM`, `CA`, `FP`, `RM`)

Each command's behaviour, with its checks and the reasons for its `na` rows, is in its YAML under [docs/eram/commands/](../eram/commands/).

14. **`RK` readout.** What does the Response Area show, word for word, after `RK`, `RK 55 56` or `RK INT`? *Our guess:* `CA FUNCTION ON`, then `CA DISPLAY OFF 55 56`; with named sectors one line each (`55 CA ON`); `MCI` in place of `CA` for `RK INT`.
15. **`UR` readout.** Which altitudes does `UR` print when you give none, are temperatures shown, and what is the layout? *Our guess:* a header with the location, then one line per FD level (030 to 390) as `altitude direction/speed`, true direction to 10°, no temperature.
16. **Sector messages.** An `SM` message shows in the Time View until acknowledged. Is `SM DE` how you acknowledge or clear it, and does plain `SM` put the text in the Response Area? *Our guess:* yes to both.
17. **`CA 55 OFF`.** Does it only stop sector 55's own display showing conflict alerts, or also stop alerts on tracks sector 55 owns from showing elsewhere? *Our guess:* only sector 55's own display.
18. **`FP` for an existing flight.** What does ERAM answer to an FP for an aircraft ID that already has an active plan in your centre? *Our guess:* refused as a duplicate; you amend with AM instead.
19. **`RM` feedback.** After `RM <ACID>` on a route that converts cleanly, do you see anything beyond ACCEPT? *Our guess:* ACCEPT only.
