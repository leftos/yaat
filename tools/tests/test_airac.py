"""Tests for tools/airac.py, the AIRAC cycle arithmetic the pin tools share.

The module lives beside scripts rather than in an installed package, so it is loaded by path; it
touches nothing but its arguments and the clock is never read.
"""

from __future__ import annotations

import importlib.util
import sys
from datetime import date, timedelta
from pathlib import Path
from typing import Any

import pytest

TOOLS_DIR = Path(__file__).resolve().parents[1]
EPOCH = date(2025, 1, 23)
CYCLE_2611_FIRST_DAY = date(2026, 10, 29)
CYCLE_2612_FIRST_DAY = date(2026, 11, 26)
CYCLE_DAYS = 28
HALF_CYCLE_DAYS = CYCLE_DAYS // 2


def _load_module() -> Any:
    spec = importlib.util.spec_from_file_location("airac", TOOLS_DIR / "airac.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["airac"] = module
    spec.loader.exec_module(module)
    return module


airac = _load_module()


@pytest.mark.parametrize(
    ("day", "expected"),
    [
        (date(2025, 1, 22), "2501"),
        (EPOCH, "2501"),
        (CYCLE_2611_FIRST_DAY, "2611"),
        (date(2026, 11, 25), "2611"),
        (CYCLE_2612_FIRST_DAY, "2612"),
    ],
)
def test_cycle_for_date_matches_the_csharp(day: date, expected: str) -> None:
    assert airac.cycle_for_date(day) == expected


def test_cycle_date_returns_the_effective_date() -> None:
    assert airac.cycle_date("2501") == EPOCH
    assert airac.cycle_date("2612") == CYCLE_2612_FIRST_DAY


def test_next_cycle_rolls_over_into_the_new_year() -> None:
    assert airac.next_cycle("2613", 1) == "2701"
    assert airac.next_cycle("2611", 2) == "2613"
    assert airac.next_cycle("2701", -1) == "2613"


def test_cycle_index_orders_ids_across_a_year_boundary() -> None:
    assert airac.cycle_index("2501") == 0
    assert airac.cycle_index("2701") == airac.cycle_index("2613") + 1


def test_nearest_cycle_takes_the_closer_effective_date() -> None:
    assert airac.nearest_cycle(CYCLE_2612_FIRST_DAY - timedelta(days=3)) == "2612"
    assert airac.nearest_cycle(CYCLE_2612_FIRST_DAY + timedelta(days=3)) == "2612"


def test_nearest_cycle_takes_the_later_cycle_on_a_tie() -> None:
    assert airac.nearest_cycle(CYCLE_2612_FIRST_DAY + timedelta(days=HALF_CYCLE_DAYS)) == "2613"


def test_serial_cycle_uses_the_serials_date() -> None:
    assert airac.serial_cycle(2026102902) == "2611"
    assert airac.serial_cycle(2026102900) == "2611"


def test_a_malformed_cycle_id_names_the_id() -> None:
    with pytest.raises(ValueError) as failure:
        airac.cycle_index("26X2")
    assert "26X2" in str(failure.value)

    with pytest.raises(ValueError) as short:
        airac.cycle_date("261")
    assert "261" in str(short.value)


def test_a_malformed_serial_names_the_serial() -> None:
    with pytest.raises(ValueError) as bad_month:
        airac.serial_cycle(2026132902)
    assert "2026132902" in str(bad_month.value)

    with pytest.raises(ValueError) as too_short:
        airac.serial_cycle(202610)
    assert "202610" in str(too_short.value)
