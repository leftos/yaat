Mock for YAAT-569 part X1 (the designed Exit left / Exit right flyouts): two owner decisions raised by the X1 reviews. Drawn on the built flyout's rows with a B738 (SWA1871) landing KOAK 28R, right exits from the bundled layout (`LayoutInspector --exits 28R`): E ~1,700 ft 45°, G ~2,700 ft 53°, H ~3,100 ft 90°, P ~4,600 ft 53°, J ~4,900 ft 141°, C1 ~5,500 ft 89°. Open `index.html` in a browser.

Each question has two lanes, A and B, with how it works and its worst case under each. Lane A is today's build in both; no lane is marked recommended.

1. What a row says when it leaves the exits-ahead list while the order is frozen. Drawn at two moments: on a 3 NM final after a gust drops G from the forecast, and on the rollout at 80 kt with H behind the aircraft and P ahead but no longer makeable.
   - A (today's build): any dropped row reads "passed", on the rollout and on final.
   - B: "passed" only for an exit behind the aircraft; one still ahead that the aircraft cannot brake for reads "can't make", so on final a dropped row always reads "can't make".
2. Exit right on final when the forecast lists no right exit (a constructed state: the forecast holds only J on the left).
   - A (today's build): Exit right is absent from the strip and All Commands, so its "Exit at" box cannot be reached.
   - B: Exit right stays on final while the runway has right exits; its flyout shows a dimmed "none forecast" line, no Pilot's choice row, and the "Exit at" box listing the runway's right exits as suggestions.
