"""AIRAC cycle arithmetic shared by the pin-status check and the refresh tools.

The grid is the one `src/Yaat.Sim/Data/Vnas/AiracCycle.cs` uses: cycles of 28 days from
2025-01-23, thirteen per year, identified as `YYNN` (two-digit year plus the cycle's number within
that year). Ordering is always by `cycle_index`, never by comparing the ids: `"2613"` precedes
`"2701"`, and a string compare gets that right only by luck.

Nothing here reads the clock. A caller that wants today's cycle passes the date it chose, so a
refresh can stamp the cycle its data belongs to instead of the day it ran.

Usage:
    import airac

    airac.cycle_for_date(date(2026, 11, 26))  # "2612"
    airac.serial_cycle(2026102902)            # "2611"
"""

from __future__ import annotations

from datetime import date, timedelta

AIRAC_EPOCH = date(2025, 1, 23)
CYCLE_DAYS = 28
CYCLES_PER_YEAR = 13
CYCLE_ID_LENGTH = 4
EPOCH_YEAR = 2025
CENTURY = 2000
FIRST_CYCLE = "2501"
SERIAL_DATE_DIGITS = 8


def _cycle_parts(cycle_id: str) -> tuple[int, int]:
    """Split a `YYNN` cycle id into its year suffix and its one-based cycle number.

    Args:
        cycle_id: The id to split, e.g. `"2611"`.

    Returns:
        The two-digit year and the cycle number within that year.

    Raises:
        ValueError: If `cycle_id` is not exactly four ASCII digits.
    """
    if len(cycle_id) != CYCLE_ID_LENGTH or not (cycle_id.isascii() and cycle_id.isdigit()):
        raise ValueError(f"malformed AIRAC cycle id {cycle_id!r}; expected YYNN")
    return int(cycle_id[:2]), int(cycle_id[2:])


def _id_for_index(index: int) -> str:
    """Return the cycle id of a linear cycle index (the first 2025 cycle is 0)."""
    year = EPOCH_YEAR + index // CYCLES_PER_YEAR
    cycle_in_year = index % CYCLES_PER_YEAR + 1
    return f"{year % 100:02d}{cycle_in_year:02d}"


def cycle_for_date(d: date) -> str:
    """Return the cycle effective on `d`.

    Args:
        d: The date to place on the cycle grid.

    Returns:
        The cycle id, `"2501"` for any date before the epoch.
    """
    total_days = (d - AIRAC_EPOCH).days
    if total_days < 0:
        return FIRST_CYCLE
    return _id_for_index(total_days // CYCLE_DAYS)


def cycle_index(cycle_id: str) -> int:
    """Return how many cycles since the epoch the id names (the first 2025 cycle is 0).

    Args:
        cycle_id: The cycle id, e.g. `"2611"`.

    Returns:
        The linear index; only differences of two indices are meaningful.

    Raises:
        ValueError: If `cycle_id` is not exactly four ASCII digits.
    """
    year_suffix, cycle_number = _cycle_parts(cycle_id)
    return (CENTURY + year_suffix - EPOCH_YEAR) * CYCLES_PER_YEAR + (cycle_number - 1)


def cycle_date(cycle_id: str) -> date:
    """Return the date the given cycle takes effect.

    Args:
        cycle_id: The cycle id, e.g. `"2611"`.

    Returns:
        The effective date of that cycle.

    Raises:
        ValueError: If `cycle_id` is not exactly four ASCII digits.
    """
    return AIRAC_EPOCH + timedelta(days=cycle_index(cycle_id) * CYCLE_DAYS)


def next_cycle(cycle_id: str, n: int) -> str:
    """Return the cycle `n` cycles after `cycle_id`.

    Args:
        cycle_id: The cycle id to count from.
        n: How many cycles to move, negative to move back.

    Returns:
        The resulting cycle id.

    Raises:
        ValueError: If `cycle_id` is not exactly four ASCII digits.
    """
    return _id_for_index(cycle_index(cycle_id) + n)


def nearest_cycle(d: date) -> str:
    """Return the cycle whose effective date is nearest to `d`.

    A date exactly halfway between two effective dates (14 days either side) takes the later
    cycle, so data published just before a switch-over lands on the cycle it is built for.

    Args:
        d: The date to place on the cycle grid.

    Returns:
        The nearest cycle id.
    """
    effective = cycle_for_date(d)
    distance_to_previous = (d - cycle_date(effective)).days
    distance_to_next = CYCLE_DAYS - distance_to_previous
    if distance_to_next <= distance_to_previous:
        return next_cycle(effective, 1)
    return effective


def serial_cycle(serial: int) -> str:
    """Return the cycle nearest to the date a NavData serial carries.

    A vNAS NavData serial is `YYYYMMDDNN`: the date the data was built for plus a revision. Its
    first eight digits are that date, and the nearest cycle to it is the one the data serves.

    Args:
        serial: The serial, e.g. `2026102902`.

    Returns:
        The cycle id the serial belongs to.

    Raises:
        ValueError: If the serial's first eight digits are not a date.
    """
    text = str(serial)
    if len(text) < SERIAL_DATE_DIGITS or not (text.isascii() and text.isdigit()):
        raise ValueError(f"malformed NavData serial {serial!r}; expected YYYYMMDDNN")
    try:
        published = date(int(text[0:4]), int(text[4:6]), int(text[6:8]))
    except ValueError as exc:
        raise ValueError(f"malformed NavData serial {serial!r}: {exc}") from exc
    return nearest_cycle(published)
