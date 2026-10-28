# YAAT-1282: the implicit crossing of a bare TAXI

The settled record of a design mock. The owner's rulings are written in the description of Linear issue YAAT-1282; the behaviour is documented in `COMMANDS.md` (the `TAXI B` row and the implied-crossing paragraphs) and `docs/pilot-phraseology.md`.

## The question

At KOAK an aircraft is holding short of runway 28L at the south hold bar on taxiway B, facing the runway. The controller types a bare `TAXI B`, which names no runway crossing. Did that clear the aircraft across the runway it was holding short of (AIM 4-3-18.a.5, 7110.65 3-7-2)?

## The options

- **Today (as the mock recorded it):** the bare `TAXI B` is a clearance across 28L. The aircraft moves at once, crosses 28L without any crossing clearance and stops at the next bar, short of 28R.
- **Explicit:** a crossing needs its own clearance. `TAXI B HS 28L` holds the aircraft at the bar, and a later `CROSS 28L` (30 seconds later in the mock) lets it cross.

## The answer

The implied crossing stays for a directed `TAXI`, one that names a destination or a further taxiway (`TAXI B C` from the 28L bar clears 28L and the pilot reads the crossing). A bare `TAXI` that names only the taxiway the aircraft is already on, with at most `EXP`/`NODEL`, is refused (`TAXI B rejected: already on B; give a destination or a further taxiway`), the aircraft stays put and the pilot says nothing.

The readback of a `TAXI` stops at the first hold short that stops the aircraft and says nothing past it, so the pilot never reads back a crossing the controller did not clear.

## Where it is recorded

The Linear issue YAAT-1282, in its description.
