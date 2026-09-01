# /// script
# requires-python = ">=3.13"
# dependencies = ["pdfplumber>=0.11", "brotli"]
# ///
"""Build YAAT's committed military training route data from the DoD AP/1B publication.

AP/1B (Area Planning, Military Training Routes, North and South America) is published by NGA
every 8 weeks and is the authoritative source for IFR (IR), VFR (VR), and Slow Speed Low
Altitude (SR) training routes. Each route is a one-way ordered sequence of lettered points,
every point carrying a lat/long, usually a Fac/Rad/Dist, and the altitude block flown on the
segment *terminating at* that point.

The route description is a four-column table (`Altitude Data | Pt | Fac/Rad/Dist | Lat/Long`)
that text-order extraction gets wrong -- `pdftotext -layout` interleaves the Lat/Long column
out of row sync on 245 of 648 routes. This tool reads word bounding boxes instead and buckets
them into per-page column bands, which associates every cell with its own row.

Correctness is checked two ways, both reported by --report:
  * the FRD oracle -- where a point publishes a Fac/Rad/Dist, the great-circle distance from
    that navaid to the printed lat/long must match the published distance. Distance is
    independent of magnetic declination, so this needs no WMM model, and a row mis-association
    throws a point tens to hundreds of NM off.
  * the FAA cross-check -- the AIS `MTRSegment` layer carries independent geometry and
    altitude blocks for 348 of the 648 routes (no SRs, few 4-digit VRs).

AP/1B is US Government work with no copyright claimed under Title 17 U.S.C. The publication is
not fetched automatically: daip.jcs.mil serves a certificate chain that fails default
verification, and silently disabling verification in a committed build tool is the wrong
default. Download it yourself, then point --input at it.

Usage:
    curl -o .tmp/ap1b.pdf https://www.daip.jcs.mil/pdf/ap1b.pdf
    uv run tools/build-mtr-data.py --input .tmp/ap1b.pdf
    uv run tools/build-mtr-data.py --input .tmp/ap1b.pdf --report .tmp/mtr-report.json
    uv run tools/build-mtr-data.py --input .tmp/ap1b.pdf --no-cross-check   # offline
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
import sys
import urllib.parse
import urllib.request
from collections import Counter
from dataclasses import asdict, dataclass, field
from datetime import UTC, datetime
from itertools import pairwise
from pathlib import Path

import pdfplumber

DEFAULT_OUTPUT = Path("src/Yaat.Sim/Data/MilitaryRoutes/ap1b-mtr.json.br")
DEFAULT_AR_OUTPUT = Path("src/Yaat.Sim/Data/MilitaryRoutes/ap1b-ar.json.br")
SOURCE_URL = "https://www.daip.jcs.mil/pdf/ap1b.pdf"
NAVAID_URL = "https://services6.arcgis.com/ssFJjBXIUyZDrSYZ/arcgis/rest/services/NAVAIDSystem/FeatureServer/0/query"
MTR_SEGMENT_URL = "https://services6.arcgis.com/ssFJjBXIUyZDrSYZ/arcgis/rest/services/MTRSegment/FeatureServer/0/query"
ARCGIS_PAGE = 1000

ROUTE_HEADER_RE = re.compile(r"^(IR|VR|SR)-(\d{3,4}[A-Z]?)$")
POINT_LABEL_RE = re.compile(r"^[A-Z]{1,2}\d?$")
FRD_RE = re.compile(r"^(\d{3})/(\d{1,3})$")
# The degree glyph varies with font embedding; accept any single non-digit separator.
LATITUDE_RE = re.compile(r"^([NS])\s?(\d{2,3})\D(\d{2}\.\d{1,2})'?$")
LONGITUDE_RE = re.compile(r"^([EW])\s?(\d{2,3})\D(\d{2}\.\d{1,2})'?$")

# Chapter 5. Track and anchor designators share one number space with no overlap: tracks run
# AR1-AR5xx and anchors start at AR600.
AR_DESIGNATOR_RE = re.compile(r"^AR\d+[A-Z]*$")
AR_DIRECTION_RE = re.compile(r"^\((North|South|East|West)\)$")
# "ENI VORTAC", "MLD VOR-DME" -- the ident is what the FRD oracle needs, the class is decoration.
AR_FACILITY_RE = re.compile(r"^([A-Z]{2,4})\s+(VORTAC|VOR-DME|VOR/DME|VORDME|VOR|TACAN|NDB|DME)$")
AR_TRAILER_MARKERS = ("REMARKS", "ATC")
# Chapter 5 publishes altitudes as a bare floor/ceiling pair rather than chapters 2-4's "B" block:
# "FL240/FL310", "16000/FL260", "1000 AGL/8500". Each side is anchored on a word boundary and
# limited to altitude-shaped tokens so a stray phone number ("DSN 331-3536/3560") cannot match.
AR_ALTITUDE_LEVEL = r"(SFC|FL\s?\d{3}|\d{1,3},\d{3}|\d{3,5})"
AR_ALTITUDE_RE = re.compile(rf"\b{AR_ALTITUDE_LEVEL}\s*(AGL|MSL)?\s*/\s*{AR_ALTITUDE_LEVEL}\s*(AGL|MSL)?", re.IGNORECASE)
# Refueling happens between the surface and the top of the published flight levels; anything
# outside this is a mis-read cell, not an altitude.
AR_ALTITUDE_RANGE_FT = (0, 60000)

ALTITUDE_LEVEL = r"(SFC|FL\d{3}|\d+(?:\.\d+)?)"
ALTITUDE_BLOCK_RE = re.compile(rf"{ALTITUDE_LEVEL}\s*(AGL|MSL)?\s+B\s+{ALTITUDE_LEVEL}\s*(AGL|MSL)", re.IGNORECASE)
ALTITUDE_SINGLE_RE = re.compile(rf"{ALTITUDE_LEVEL}\s*(AGL|MSL)", re.IGNORECASE)
AT_OR_BELOW_RE = re.compile(r"at or below", re.IGNORECASE)
AS_ASSIGNED_RE = re.compile(r"as assigned", re.IGNORECASE)

WIDTH_SYMMETRIC_RE = re.compile(r"(\d+(?:\.\d+)?)\s*NM either side of (?:the )?centerline", re.IGNORECASE)
WIDTH_ASYMMETRIC_RE = re.compile(r"(\d+(?:\.\d+)?)\s*NM left and (\d+(?:\.\d+)?)\s*NM right", re.IGNORECASE)
WIDTH_SPAN_RE = re.compile(r"from\s+([A-Z]{1,2}\d?)\s+to\s+([A-Z]{1,2}\d?)", re.IGNORECASE)
PRIMARY_ENTRY_RE = re.compile(r"Primary Entry Point:?\s*\(?([A-Z]{1,2}\d?)\)?", re.IGNORECASE)
PRIMARY_EXIT_RE = re.compile(r"Primary Exit Point:?\s*\(?([A-Z]{1,2}\d?)\)?", re.IGNORECASE)
ACTIVITY_RE = re.compile(r"{label} ACTIVITY:\s*(.+?)(?=\s*(?:SCHEDULING ACTIVITY|HOURS OF OPERATION|ROUTE DESCRIPTION)|$)", re.DOTALL)
HOURS_RE = re.compile(r"HOURS OF OPERATION:\s*(.+?)(?=\s*ROUTE DESCRIPTION|$)", re.DOTALL)

# Only the section headings that actually follow the route table. "NOTE:" and "CAUTION:" are
# tempting but wrong: VR-1001 carries "NOTE: FOLLOWING SEGMENTS USE LIMITED TO DESIGNATED
# SPECIAL EXERCISES ONLY." as a mid-table annotation in the Altitude Data column, and treating
# it as an ender truncates the route at point K -- swallowing K's longitude, which sits on that
# very row.
TABLE_END_MARKERS = ("TERRAIN", "ROUTE", "Special", "Remarks:", "FSS")
ROW_TOLERANCE = 2.0
# Baseline gap that separates two words inside a rotated (chapter 5) text run, and the nominal
# height of one printed line in the transposed frame.
ROTATED_WORD_GAP = 2.5
ROTATED_LINE_HEIGHT = 9.0
HUNDREDS_OF_FEET = 100

# Validation bands. AP/1B covers North and South America; the widest published route half-width
# is 20 NM, and the longest single published leg is comfortably under 250 NM.
LAT_RANGE = (-60.0, 75.0)
LON_RANGE = (-180.0, -30.0)
MAX_LEG_NM = 250.0
MIN_POINTS = 2
REPEAT_LABEL_TOLERANCE_NM = 0.5

# Expected route counts for the current publication era. A drift of more than a few percent
# means the header scan changed behaviour, not that the DoD reorganised the book.
EXPECTED_COUNTS = {"IR": 213, "VR": 304, "SR": 131}
COUNT_TOLERANCE = 0.05

# Chapter 5 column headers, named left to right. Bands are derived from the header words' own x
# positions rather than fixed offsets, because chapter 5 shares chapters 2-4's mirrored facing-page
# margins. The two shapes are told apart by PATTERN, which only the anchor table publishes.
AR_TRACK_COLUMNS = ("number", "arip", "arcp", "checkPoints", "exit", "crPlan", "altitudes", "unit", "artcc")
AR_ANCHOR_COLUMNS = (
    "number",
    "entryPoints",
    "arip",
    "anchorPoint",
    "anchorPattern",
    "exitPoints",
    "radar",
    "altitudes",
    "unit",
    "artcc",
    "hours",
)
# (column, point role, synthetic label prefix) in the order the aircraft flies them.
AR_TRACK_SEQUENCE = (("arip", "arip", "ARIP"), ("arcp", "arcp", "ARCP"), ("checkPoints", "checkPoint", "CP"), ("exit", "exit", "EXIT"))
AR_ANCHOR_SEQUENCE = (
    ("entryPoints", "entry", "ENTRY"),
    ("arip", "arip", "ARIP"),
    ("anchorPoint", "anchorPoint", "ANCHOR"),
    ("exitPoints", "exit", "EXIT"),
)
AR_EXPECTED_COUNTS = {"track": 156, "anchor": 91}
AR_MIN_POINTS = 2
# A published directional variant is the same track flown the other way, so its points must be the
# primary's in reverse. Anything further apart than this means two variants were conflated.
AR_REVERSAL_TOLERANCE_NM = 1.0

FRD_ORACLE_TOLERANCE_NM = 8.0
FRD_ORACLE_GATE = 0.95
CROSS_CHECK_P95_NM = 2.0
CROSS_CHECK_FATAL_NM = 25.0
MAX_DIVERGING_FRACTION = 0.05


@dataclass
class AltitudeBlock:
    raw: str
    parsed: bool = False
    kind: str = "none"
    floor_ft: int | None = None
    floor_ref: str | None = None
    ceiling_ft: int | None = None
    ceiling_ref: str | None = None


@dataclass
class RoutePoint:
    label: str
    lat: float | None = None
    lon: float | None = None
    frd_fix: str | None = None
    frd_radial: int | None = None
    frd_distance: int | None = None
    role: str = "point"
    altitude: AltitudeBlock | None = None
    # The Altitude Data cell as printed on this point's own row, kept apart from the wrapped
    # continuation rows below it. The altitude expression is always complete on the point row;
    # continuations carry qualifiers ("or as assigned", "descend to cross", "Alternate Entry").
    # Parsing the concatenation instead lets the block regex span two logically separate
    # expressions -- IR-492 point NJ reads "FL200 to" plus "FL200 B 50 MSL descend direct to
    # cross", which yields a nonsensical 20000 ft floor under a 5000 ft ceiling.
    altitude_primary: str = ""


@dataclass
class WidthSpan:
    from_point: str | None
    to_point: str | None
    left_nm: float
    right_nm: float


@dataclass
class MilitaryRoute:
    designator: str
    printed: str
    kind: str
    page: int
    points: list[RoutePoint] = field(default_factory=list)
    widths: list[WidthSpan] = field(default_factory=list)
    entry_points: list[str] = field(default_factory=list)
    exit_points: list[str] = field(default_factory=list)
    originating_activity: str = ""
    scheduling_activity: str = ""
    hours: str = ""
    terrain_following: bool = False
    text: str = field(default="", repr=False)
    warnings: list[str] = field(default_factory=list)


@dataclass
class ArPoint:
    """One cell of a chapter 5 table: an optional navaid fix/radial/distance plus a lat/long."""

    role: str
    label: str = ""
    lat: float | None = None
    lon: float | None = None
    facility: str | None = None
    radial: int | None = None
    distance: int | None = None

    @property
    def complete(self) -> bool:
        return self.lat is not None and self.lon is not None


@dataclass
class ArVariant:
    """One published direction of a track or anchor, with its own geometry.

    The two directions of a track are *not* generally the same line flown backwards: AR4A's
    southbound ARIP sits 50 NM from its northbound exit, because opposing refueling tracks are
    laterally offset so the traffic is separated. Each direction therefore carries its own points.
    """

    direction: str | None
    points: list[ArPoint] = field(default_factory=list)
    pattern: list[ArPoint] = field(default_factory=list)


@dataclass
class ArRoute:
    """An AP/1B chapter 5 aerial refueling track or anchor."""

    designator: str
    kind: str
    page: int
    variants: list[ArVariant] = field(default_factory=list)
    altitude_raw: str = ""
    altitude: AltitudeBlock | None = None
    airspace: list[tuple[float, float]] = field(default_factory=list)
    scheduling_activity: str = ""
    artcc: str = ""
    hours: str = ""
    remarks: str = ""
    warnings: list[str] = field(default_factory=list)

    @property
    def all_points(self) -> list[ArPoint]:
        return [p for v in self.variants for p in v.points + v.pattern]


@dataclass
class FrdProbe:
    """One published Fac/Rad/Dist paired with the coordinate printed on the same row."""

    route: str
    point: str
    fix: str
    radial: int
    distance: int
    lat: float
    lon: float


def haversine_nm(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    radius_nm = 3440.065
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dphi = phi2 - phi1
    dlambda = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(dlambda / 2) ** 2
    return 2 * radius_nm * math.asin(math.sqrt(a))


def parse_coordinate(text: str, pattern: re.Pattern[str], negative_hemisphere: str) -> float | None:
    match = pattern.match(text)
    if match is None:
        return None
    hemisphere, degrees, minutes = match.groups()
    value = int(degrees) + float(minutes) / 60.0
    return -value if hemisphere == negative_hemisphere else value


def altitude_level_to_feet(token: str) -> int:
    """AP/1B prints altitudes in hundreds of feet; SFC is the surface and FLxxx a flight level."""
    upper = token.upper()
    if upper == "SFC":
        return 0
    if upper.startswith("FL"):
        return int(upper[2:]) * HUNDREDS_OF_FEET
    return round(float(token) * HUNDREDS_OF_FEET)


def parse_altitude(raw: str) -> AltitudeBlock:
    """Parse one Altitude Data cell.

    The measured grammar across chapters 2-4 is dominated by eight forms (`NN AGL B NN MSL to`
    at 944 occurrences down to `NN.N AGL B NN MSL to` at 9), with a tail of roughly forty
    irregular entries. Unrecognised text is preserved verbatim rather than guessed at, and the
    report counts it -- geometry is this tool's payload, and a dropped point would be worse
    than an unparsed altitude.
    """
    text = " ".join(raw.split())
    block = AltitudeBlock(raw=text)
    if not text:
        return block
    block.kind = "unparsed"

    match = ALTITUDE_BLOCK_RE.search(text)
    if match is not None:
        floor, floor_ref, ceiling, ceiling_ref = match.groups()
        block.parsed = True
        block.kind = "block"
        block.floor_ft = altitude_level_to_feet(floor)
        block.floor_ref = (floor_ref or ceiling_ref or "MSL").upper()
        block.ceiling_ft = altitude_level_to_feet(ceiling)
        block.ceiling_ref = (ceiling_ref or "MSL").upper()
        if floor.upper() == "SFC":
            block.floor_ref = "AGL"
        return block

    match = ALTITUDE_SINGLE_RE.search(text)
    if match is not None:
        level, reference = match.groups()
        block.parsed = True
        block.kind = "atOrBelow" if AT_OR_BELOW_RE.search(text) else "single"
        block.ceiling_ft = altitude_level_to_feet(level)
        block.ceiling_ref = reference.upper()
        block.floor_ft = None if block.kind == "atOrBelow" else block.ceiling_ft
        block.floor_ref = None if block.kind == "atOrBelow" else block.ceiling_ref
        return block

    if AS_ASSIGNED_RE.search(text):
        block.parsed = True
        block.kind = "asAssigned"
    return block


def classify_role(altitude_text: str, index: int, total: int) -> str:
    lowered = altitude_text.lower()
    if "alternate entry" in lowered:
        return "alternateEntry"
    if "alternate exit" in lowered:
        return "alternateExit"
    if index == 0:
        return "entry"
    if index == total - 1:
        return "exit"
    return "point"


def page_words(page: pdfplumber.page.Page) -> list[dict]:
    """Words on a page, with chapter 5's sideways tables rotated back upright.

    The aerial-refueling tables are printed in landscape on a portrait page: their glyphs carry a
    90-degree text matrix while the running header stays upright, so pdfplumber -- which groups by
    page x -- assembles them backwards ("thgilF" for "Flight"). Rebuilding words from the rotated
    chars in a transposed frame makes chapter 5 look structurally identical to chapters 2-4, so the
    same header-detection and column-banding logic reads both.
    """
    upright = page.extract_words(use_text_flow=False, keep_blank_chars=False)
    rotated_chars = [c for c in page.chars if not c.get("upright")]
    if len(rotated_chars) < len(page.chars) / 2:
        return upright

    # A printed line shares an x on the unrotated page; reading order runs along *decreasing* top.
    lines: dict[float, list[dict]] = {}
    for char in rotated_chars:
        lines.setdefault(round(char["x0"], 0), []).append(char)

    words: list[dict] = []
    for line_x, chars in lines.items():
        chars.sort(key=lambda c: -c["top"])
        buffer: list[dict] = []
        for char in chars:
            if buffer and buffer[-1]["top"] - char["bottom"] > ROTATED_WORD_GAP:
                words.append(rotated_word(buffer, line_x))
                buffer = []
            buffer.append(char)
        if buffer:
            words.append(rotated_word(buffer, line_x))

    return words


def rotated_word(chars: list[dict], line_x: float) -> dict:
    """One word from a rotated run, expressed in the transposed frame.

    `top` becomes the printed line number (the unrotated x) and `x0` the position along the printed
    line, measured from the page bottom so it increases left-to-right as printed.
    """
    text = "".join(c["text"] for c in chars)
    start = min(c["bottom"] for c in chars)
    end = max(c["top"] for c in chars)
    return {
        "text": text,
        "top": line_x,
        "bottom": line_x + ROTATED_LINE_HEIGHT,
        "x0": -end,
        "x1": -start,
    }


def cluster_rows(words: list[dict]) -> list[list[dict]]:
    """Group words into visual rows by `top`, tolerant of sub-pixel baseline drift."""
    rows: list[list[dict]] = []
    for word in sorted(words, key=lambda w: (w["top"], w["x0"])):
        if rows and abs(word["top"] - rows[-1][0]["top"]) <= ROW_TOLERANCE:
            rows[-1].append(word)
        else:
            rows.append([word])
    return [sorted(row, key=lambda w: w["x0"]) for row in rows]


def find_column_bands(rows: list[list[dict]]) -> tuple[dict[str, tuple[float, float]], int] | None:
    """Locate the `Altitude Data | Pt | Fac/Rad/Dist | Lat/Long` header and derive x-bands.

    Bands are derived per page: facing pages are mirrored, so `Pt` sits at x~105 on one and
    x~155 on the other, and document-global bands mis-bin every other page.
    """
    for index, row in enumerate(rows):
        anchors = {word["text"]: word for word in row}
        if "Pt" not in anchors or "Lat/Long" not in anchors:
            continue
        if not any(text.startswith("Altitude") for text in anchors):
            continue

        point_x = anchors["Pt"]["x0"]
        latlon_x = anchors["Lat/Long"]["x0"]
        frd_x = anchors["Fac/Rad/Dist"]["x0"] if "Fac/Rad/Dist" in anchors else (point_x + latlon_x) / 2

        # The Lat/Long band is a tight window, not open to the right edge. Coordinate cells sit
        # a consistent 6-13 pt right of the header word across every margin variant, while
        # Special Operating Procedures cite tower and hazard coordinates inline in prose at
        # arbitrary x. An open-ended band turns those into route points.
        return {
            "altitude": (-1e9, point_x - 4),
            "point": (point_x - 4, frd_x - 6),
            "frd": (frd_x - 6, latlon_x + 2),
            "latlon": (latlon_x + 2, latlon_x + 26),
        }, index
    return None


def band_of(word: dict, bands: dict[str, tuple[float, float]]) -> str:
    x = word["x0"]
    for name, (low, high) in bands.items():
        if low <= x < high:
            return name
    return "altitude"


def is_table_end(row: list[dict], bands: dict[str, tuple[float, float]]) -> bool:
    first = row[0]
    return band_of(first, bands) == "altitude" and first["text"] in TABLE_END_MARKERS


def read_frd(frd_words: list[dict]) -> tuple[str, int, int] | None:
    for index, word in enumerate(frd_words):
        match = FRD_RE.match(word["text"])
        if match is not None and index > 0:
            return frd_words[index - 1]["text"], int(match.group(1)), int(match.group(2))
    return None


def extract_page_points(
    page: pdfplumber.page.Page,
    band_cache: dict[int, dict[str, tuple[float, float]]],
    points: list[RoutePoint],
) -> list[str]:
    """Append this page's route-description rows to the route-level `points`; return warnings.

    Long routes run onto continuation pages that repeat neither the column header nor the
    originating page's margins, so bands are cached by page parity and reused. Inheriting the
    previous page's bands instead bins every continuation page against the wrong margin.
    """
    rows = cluster_rows(page_words(page))
    parity = page.page_number % 2
    located = find_column_bands(rows)

    if located is not None:
        bands, start_index = located
        band_cache[parity] = bands
    else:
        bands = band_cache.get(parity)
        if bands is None:
            return []
        start_index = max((i for i, row in enumerate(rows) if row[0]["top"] < 30), default=-1)

    warnings: list[str] = []
    for row in rows[start_index + 1 :]:
        if is_table_end(row, bands):
            break

        grouped: dict[str, list[dict]] = {}
        for word in row:
            grouped.setdefault(band_of(word, bands), []).append(word)

        altitude_text = " ".join(w["text"] for w in grouped.get("altitude", []))
        labels = [w for w in grouped.get("point", []) if POINT_LABEL_RE.match(w["text"])]
        latlon_words = grouped.get("latlon", [])
        latitude = next(
            (parse_coordinate(w["text"], LATITUDE_RE, "S") for w in latlon_words if LATITUDE_RE.match(w["text"])),
            None,
        )

        # A genuine point row always carries its own latitude. Requiring both rejects the
        # Special Operating Procedures prose that continues onto a route's later pages, where
        # short words ("NM", "SA") drift into the Pt column band.
        if labels and latitude is not None:
            point = RoutePoint(
                label=labels[0]["text"],
                lat=latitude,
                altitude=AltitudeBlock(raw=altitude_text),
                altitude_primary=altitude_text,
            )
            frd = read_frd(grouped.get("frd", []))
            if frd is not None:
                point.frd_fix, point.frd_radial, point.frd_distance = frd
            points.append(point)
        elif points and altitude_text:
            assert points[-1].altitude is not None
            points[-1].altitude.raw = f"{points[-1].altitude.raw} {altitude_text}".strip()

        # The longitude sits on the row below its latitude. `points` is route-level, not
        # page-level, so a point whose latitude ends a page and whose longitude opens the next
        # still pairs correctly.
        for word in latlon_words:
            longitude = parse_coordinate(word["text"], LONGITUDE_RE, "W")
            if longitude is None:
                continue
            target = next((p for p in points if p.lon is None), None)
            if target is None:
                warnings.append(f"orphan longitude {word['text']}")
            else:
                target.lon = longitude

    return warnings


def find_route_header(words: list[dict]) -> dict | None:
    """The route header is the only word on its row, high on the page.

    Matching the designator pattern anywhere in the top band instead picks up route
    cross-references in wrapped Special Operating Procedures prose on continuation pages
    ("IR-022 is normally flown on..."), which fabricates dozens of phantom empty routes.
    """
    for row in cluster_rows(words):
        if row[0]["top"] > 60:
            return None
        if len(row) == 1 and ROUTE_HEADER_RE.match(row[0]["text"]):
            return row[0]
    return None


def extract_routes(pdf: pdfplumber.PDF) -> list[MilitaryRoute]:
    routes: list[MilitaryRoute] = []
    current: MilitaryRoute | None = None
    band_cache: dict[int, dict[str, tuple[float, float]]] = {}

    for index, page in enumerate(pdf.pages):
        words = page_words(page)
        header = find_route_header(words)

        if header is not None:
            match = ROUTE_HEADER_RE.match(header["text"])
            assert match is not None
            kind, number = match.groups()
            current = MilitaryRoute(
                designator=f"{kind}{number}",
                printed=header["text"],
                kind=kind,
                page=index + 1,
            )
            routes.append(current)

        if current is None:
            continue

        current.text += "\n" + (page.extract_text() or "")
        current.warnings.extend(extract_page_points(page, band_cache, current.points))

    return routes


def parse_widths(text: str) -> list[WidthSpan]:
    """Parse the free-text ROUTE WIDTH clause into per-span left/right half-widths."""
    marker = text.find("ROUTE WIDTH")
    if marker < 0:
        return []
    clause = text[marker : marker + 900].split("Special Operating", maxsplit=1)[0].split("Remarks:", maxsplit=1)[0]

    spans: list[WidthSpan] = []
    for segment in clause.split(";"):
        for part in segment.split(","):
            asymmetric = WIDTH_ASYMMETRIC_RE.search(part)
            symmetric = WIDTH_SYMMETRIC_RE.search(part)
            if asymmetric is None and symmetric is None:
                continue
            if asymmetric is not None:
                left, right = float(asymmetric.group(1)), float(asymmetric.group(2))
            else:
                assert symmetric is not None
                left = right = float(symmetric.group(1))
            span = WIDTH_SPAN_RE.search(part) or WIDTH_SPAN_RE.search(segment)
            spans.append(
                WidthSpan(
                    from_point=span.group(1).upper() if span else None,
                    to_point=span.group(2).upper() if span else None,
                    left_nm=left,
                    right_nm=right,
                )
            )
    return spans


def enrich_route(route: MilitaryRoute) -> None:
    """Attach parsed altitudes, point roles, widths, entry/exit points, and metadata."""
    total = len(route.points)
    for index, point in enumerate(route.points):
        raw = point.altitude.raw if point.altitude is not None else ""
        point.altitude = parse_altitude(point.altitude_primary)
        point.altitude.raw = raw
        point.role = classify_role(raw, index, total)

    route.widths = parse_widths(route.text)
    route.terrain_following = "TERRAIN FOLLOWING" in route.text

    labels = [p.label for p in route.points]
    primary_entry = PRIMARY_ENTRY_RE.search(route.text)
    primary_exit = PRIMARY_EXIT_RE.search(route.text)
    entry = primary_entry.group(1).upper() if primary_entry else (labels[0] if labels else None)
    exit_point = primary_exit.group(1).upper() if primary_exit else (labels[-1] if labels else None)

    route.entry_points = [p for p in [entry] if p] + [p.label for p in route.points if p.role == "alternateEntry"]
    route.exit_points = [p for p in [exit_point] if p] + [p.label for p in route.points if p.role == "alternateExit"]

    for label, pattern in (("ORIGINATING", "originating_activity"), ("SCHEDULING", "scheduling_activity")):
        match = re.search(ACTIVITY_RE.pattern.format(label=label), route.text, re.DOTALL)
        if match is not None:
            setattr(route, pattern, " ".join(match.group(1).split())[:400])
    hours = HOURS_RE.search(route.text)
    if hours is not None:
        route.hours = " ".join(hours.group(1).split())[:200]


def validate_route(route: MilitaryRoute) -> list[str]:
    """Per-route invariants. A failing route is dropped and reported, not fatal to the batch."""
    problems: list[str] = []
    complete = [p for p in route.points if p.lat is not None and p.lon is not None]
    dropped = len(route.points) - len(complete)
    if dropped:
        problems.append(f"{dropped} point(s) missing a coordinate")
        route.points = complete

    if len(route.points) < MIN_POINTS:
        problems.append(f"only {len(route.points)} point(s)")
        return problems

    for point in route.points:
        assert point.lat is not None and point.lon is not None
        if not (LAT_RANGE[0] <= point.lat <= LAT_RANGE[1] and LON_RANGE[0] <= point.lon <= LON_RANGE[1]):
            problems.append(f"point {point.label} at ({point.lat:.3f},{point.lon:.3f}) outside the Americas")

    # Alternate entry/exit points are digit-suffixed (D1, P2, AE3) and are printed inline among
    # the mainline points without being sequential legs -- an alternate entry can sit hundreds
    # of NM from the point it substitutes for. Walking them as consecutive legs invents long-leg
    # warnings (IR-266 Y-B1 at 328 NM, SR-104 EA-AC1 at 302 NM) that describe nothing real.
    mainline = [p for p in route.points if not p.label[-1].isdigit()]
    for before, after in pairwise(mainline):
        assert before.lat is not None and before.lon is not None
        assert after.lat is not None and after.lon is not None
        leg = haversine_nm(before.lat, before.lon, after.lat, after.lon)
        if leg > MAX_LEG_NM:
            problems.append(f"leg {before.label}-{after.label} is {leg:.0f} NM (> {MAX_LEG_NM:.0f})")

    for point in route.points:
        block = point.altitude
        if block is None or not block.parsed:
            continue
        if block.floor_ft is not None and block.ceiling_ft is not None and block.floor_ft > block.ceiling_ft:
            problems.append(f"point {point.label} floor {block.floor_ft} above ceiling {block.ceiling_ft}")

    # AP/1B restates a mainline point when an alternate branch leaves from it, so 44 routes
    # legitimately repeat a label -- IR-033 reads A B C D E F1 G E F2 with both Es at the same
    # coordinates. That is fine, and the repeat keeps the synthetic-name-to-position mapping
    # unambiguous. A repeat that *disagrees* on position means two table rows were conflated.
    by_label: dict[str, list[RoutePoint]] = {}
    for point in route.points:
        by_label.setdefault(point.label, []).append(point)
    for label, repeats in by_label.items():
        if len(repeats) < 2:
            continue
        first = repeats[0]
        assert first.lat is not None and first.lon is not None
        for other in repeats[1:]:
            assert other.lat is not None and other.lon is not None
            drift = haversine_nm(first.lat, first.lon, other.lat, other.lon)
            if drift > REPEAT_LABEL_TOLERANCE_NM:
                problems.append(f"point {label} is repeated {drift:.1f} NM apart")

    return problems


# ---------------------------------------------------------------------------
# Chapter 5 -- aerial refueling tracks and anchors
# ---------------------------------------------------------------------------


def bands_from_headers(names: tuple[str, ...], xs: list[float]) -> dict[str, tuple[float, float]]:
    """Half-open x bands split at the midpoint between adjacent column headers.

    Chapter 5's columns sit 45-70 pt apart while a cell drifts at most 5 pt from its own header,
    so midpoints separate them with room to spare and need no per-column offset table.
    """
    edges = [-1e9] + [(low + high) / 2 for low, high in pairwise(xs)] + [1e9]
    return {name: (edges[i], edges[i + 1]) for i, name in enumerate(names)}


def find_ar_bands(rows: list[list[dict]]) -> tuple[dict[str, tuple[float, float]], str, int] | None:
    """Locate a chapter 5 column header and derive its x-bands, or None on a non-table page."""
    for index, row in enumerate(rows):
        texts = [word["text"].strip() for word in row]
        if "NUMBER" not in texts or "ARIP" not in texts:
            continue
        kind = "anchor" if "PATTERN" in texts else "track"
        names = AR_ANCHOR_COLUMNS if kind == "anchor" else AR_TRACK_COLUMNS
        # An exact width match is the guard that the header was read cleanly; a short or long row
        # means a glyph merged or split, and silently mis-binding every cell below is far worse
        # than skipping the page and failing the count invariant.
        if len(row) != len(names):
            continue
        return bands_from_headers(names, sorted(word["x0"] for word in row)), kind, index
    return None


def iter_ar_rows(pdf: pdfplumber.PDF) -> list[tuple[list[dict], dict[str, tuple[float, float]], str, int]]:
    """Every chapter 5 table row in document order, tagged with its page's bands and table kind."""
    tagged: list[tuple[list[dict], dict[str, tuple[float, float]], str, int]] = []
    for index, page in enumerate(pdf.pages):
        located = find_ar_bands(cluster_rows(page_words(page)))
        if located is None:
            continue
        bands, kind, start = located
        for row in cluster_rows(page_words(page))[start + 1 :]:
            tagged.append((row, bands, kind, index + 1))
    return tagged


def ar_row_head(row: list[dict], bands: dict[str, tuple[float, float]]) -> str:
    words = [w for w in row if band_of(w, bands) == "number"]
    return words[0]["text"].strip() if words else ""


def split_ar_entries(
    tagged: list[tuple[list[dict], dict[str, tuple[float, float]], str, int]],
) -> list[tuple[str, str, int, dict, list[list[list[dict]]], list[list[dict]]]]:
    """Group rows into (designator, kind, page, bands, directional variants, trailer rows).

    A variant is one direction of the track. The first direction's label belongs to the variant
    already open -- it is printed below the designator, and not always on the very next row, since
    a lone footnote glyph can sit between them (AR112L). Every direction row after the first is a
    reverse variant and opens a new one.
    """
    entries: list[tuple[str, str, int, dict, list[list[list[dict]]], list[list[dict]]]] = []
    variants: list[list[list[dict]]] = []
    trailer: list[list[dict]] = []
    seen_direction = False
    in_trailer = False

    for row, bands, kind, page in tagged:
        head = ar_row_head(row, bands)
        if AR_DESIGNATOR_RE.match(head):
            variants, trailer, seen_direction, in_trailer = [[]], [], False, False
            entries.append((head, kind, page, bands, variants, trailer))
        elif not entries:
            continue

        if in_trailer or head.startswith(AR_TRAILER_MARKERS):
            in_trailer = True
            trailer.append(row)
            continue

        if AR_DIRECTION_RE.match(head):
            if seen_direction:
                variants.append([])
            seen_direction = True
        variants[-1].append(row)

    return entries


def parse_ar_cell(words: list[dict], role: str) -> list[ArPoint]:
    """Read one column's vertical stack of cells into points.

    A cell is a 2-line (lat, long) or 4-line (facility, radial/distance, lat, long) run, and the
    check-point and anchor-pattern columns stack several of them below their own row. The longitude
    is what closes a cell, which is what lets one loop read both shapes and any number of them.
    """
    points: list[ArPoint] = []
    current = ArPoint(role=role)
    for word in words:
        text = " ".join(word["text"].split())
        if not text or text.startswith("(cid:"):
            continue

        latitude = parse_coordinate(text, LATITUDE_RE, "S")
        if latitude is not None:
            current.lat = latitude
            continue

        longitude = parse_coordinate(text, LONGITUDE_RE, "W")
        if longitude is not None:
            current.lon = longitude
            points.append(current)
            current = ArPoint(role=role)
            continue

        radial = FRD_RE.match(text)
        if radial is not None:
            current.radial, current.distance = int(radial.group(1)), int(radial.group(2))
            continue

        facility = AR_FACILITY_RE.match(text)
        if facility is not None and current.facility is None:
            current.facility = facility.group(1)

    return points


def column_words(rows: list[list[dict]], bands: dict[str, tuple[float, float]], column: str) -> list[dict]:
    """One column's words in printed reading order: down the rows, then left to right within one."""
    return [word for row in rows for word in row if band_of(word, bands) == column]


def column_text(rows: list[list[dict]], bands: dict[str, tuple[float, float]], column: str) -> str:
    """A column's words as one whitespace-normalised string, minus unmapped-glyph placeholders.

    pdfplumber renders AP/1B's footnote daggers as "(cid:2)"-style placeholders. They land in
    whichever column the mark was printed over and would otherwise defeat every cell regex.
    """
    parts = [" ".join(word["text"].split()) for word in column_words(rows, bands, column)]
    return " ".join(part for part in parts if part and not part.startswith("(cid:"))


def label_ar_points(points: list[ArPoint], prefix: str) -> None:
    """Number a column's points only when it published more than one, so most labels stay bare."""
    for index, point in enumerate(points):
        point.label = prefix if len(points) == 1 else f"{prefix}{index + 1}"


def parse_ar_altitude(raw: str) -> AltitudeBlock:
    """Parse chapter 5's floor/ceiling pair, e.g. "FL240/FL310", "16000/FL260", "1000 AGL/8500".

    The cell routinely carries trailing prose ("or as assigned by ATC", "3000' required") and some
    tracks publish several named blocks in one cell ("(Low Block) ... (Mid Block) ... (High
    Block)"). The *first* published pair is taken as the assigned default rather than the envelope
    across all of them: the envelope would authorise the gaps between blocks, which is airspace
    nobody published, whereas the first pair is always a real assignable block. The full cell text
    is preserved so the remaining blocks stay visible.
    """
    text = " ".join(raw.split())
    block = AltitudeBlock(raw=text)
    if not text:
        return block
    block.kind = "unparsed"

    match = AR_ALTITUDE_RE.search(text)
    if match is None:
        return block
    floor, floor_ref, ceiling, ceiling_ref = match.groups()
    low, high = ar_altitude_feet(floor), ar_altitude_feet(ceiling)
    if not all(AR_ALTITUDE_RANGE_FT[0] <= ft <= AR_ALTITUDE_RANGE_FT[1] for ft in (low, high)):
        return block

    block.parsed = True
    block.kind = "block"
    block.floor_ft, block.ceiling_ft = low, high
    # Chapter 5 prints whole feet, not chapters 2-4's hundreds, and only marks the reference when
    # it is AGL -- an unmarked refueling altitude is MSL.
    block.floor_ref = (floor_ref or "MSL").upper()
    block.ceiling_ref = (ceiling_ref or "MSL").upper()
    return block


def ar_altitude_feet(token: str) -> int:
    upper = token.replace(",", "").replace(" ", "").upper()
    if upper == "SFC":
        return 0
    return int(upper[2:]) * HUNDREDS_OF_FEET if upper.startswith("FL") else int(upper)


def build_ar_route(
    designator: str,
    kind: str,
    page: int,
    bands: dict[str, tuple[float, float]],
    variants: list[list[list[dict]]],
    trailer: list[list[dict]],
) -> ArRoute:
    """Assemble one chapter 5 entry: the primary direction's points plus shared metadata."""
    route = ArRoute(designator=designator, kind=kind, page=page)
    primary = variants[0]
    for rows in variants:
        route.variants.append(build_ar_variant(rows, bands, kind))
    dedupe_variant_labels(route)

    route.altitude_raw = column_text(primary, bands, "altitudes")
    route.altitude = parse_ar_altitude(route.altitude_raw)
    route.scheduling_activity = column_text(primary, bands, "unit")[:200]
    route.artcc = column_text(primary, bands, "artcc")[:200]
    if kind == "anchor":
        route.hours = column_text(primary, bands, "hours")[:200]

    trailer_text = " ".join(" ".join(w["text"].split()) for row in trailer for w in row)
    route.remarks = trailer_text[:600]
    route.airspace = parse_airspace_polygon(trailer_text)
    return route


def build_ar_variant(rows: list[list[dict]], bands: dict[str, tuple[float, float]], kind: str) -> ArVariant:
    """Read one direction's columns into an ordered point list, plus an anchor's orbit corners."""
    heads = [ar_row_head(row, bands) for row in rows]
    direction = next((m.group(1) for h in heads if (m := AR_DIRECTION_RE.match(h)) is not None), None)
    variant = ArVariant(direction=direction)

    for column, role, prefix in AR_ANCHOR_SEQUENCE if kind == "anchor" else AR_TRACK_SEQUENCE:
        points = [p for p in parse_ar_cell(column_words(rows, bands, column), role) if p.complete]
        label_ar_points(points, prefix)
        variant.points.extend(points)

    if kind == "anchor":
        variant.pattern = [p for p in parse_ar_cell(column_words(rows, bands, "anchorPattern"), "patternCorner") if p.complete]
        label_ar_points(variant.pattern, "PC")
    return variant


def dedupe_variant_labels(route: ArRoute) -> None:
    """Suffix each direction's labels so the two directions' synthetic fix names stay distinct.

    A track's directions are separate geometries sharing one designator, so leaving both sides
    named "AR4AARIP" would map one name to two positions 50 NM apart. The single-direction case
    keeps the bare label, which is the common one and the one a controller would recognise.
    """
    if len(route.variants) < 2:
        return
    for index, variant in enumerate(route.variants):
        suffix = (variant.direction or f"V{index + 1}")[0].upper()
        for point in variant.points + variant.pattern:
            point.label = f"{point.label}{suffix}"


def parse_airspace_polygon(text: str) -> list[tuple[float, float]]:
    """The anchor tables' ATC ASSIGNED AIRSPACE clause: a lat/long chain closing "to beginning"."""
    marker = text.find("ATC ASSIGNED AIRSPACE")
    if marker < 0:
        return []
    clause = text[marker:].split("REMARKS", maxsplit=1)[0]
    vertices: list[tuple[float, float]] = []
    for latitude, longitude in re.findall(r"([NS]\s?\d{2,3}\D\d{2}\.\d{1,2}')\s*([EW]\s?\d{2,3}\D\d{2}\.\d{1,2}')", clause):
        lat = parse_coordinate(latitude, LATITUDE_RE, "S")
        lon = parse_coordinate(longitude, LONGITUDE_RE, "W")
        if lat is not None and lon is not None:
            vertices.append((lat, lon))
    return vertices


def is_reversed_pair(first: ArVariant, second: ArVariant) -> bool:
    """Whether two directions are the same line flown backwards rather than offset parallels.

    Reported for provenance only. Some tracks (AR5H, AR6) publish exact reversals while others
    (AR4A) offset the opposing direction laterally so the traffic is separated, and the fixture
    has to carry both geometries either way.
    """
    if len(first.points) != len(second.points) or not first.points:
        return False
    for near, far in zip(first.points, reversed(second.points), strict=True):
        assert near.lat is not None and near.lon is not None and far.lat is not None and far.lon is not None
        if haversine_nm(near.lat, near.lon, far.lat, far.lon) > AR_REVERSAL_TOLERANCE_NM:
            return False
    return True


def extract_ar_routes(pdf: pdfplumber.PDF) -> list[ArRoute]:
    entries = split_ar_entries(iter_ar_rows(pdf))
    return [build_ar_route(*entry) for entry in entries]


def validate_ar_route(route: ArRoute) -> list[str]:
    """Per-entry invariants. A failing entry is dropped and reported, not fatal to the batch."""
    problems: list[str] = []
    for index, variant in enumerate(route.variants):
        label = variant.direction or f"variant {index + 1}"
        if len(variant.points) < AR_MIN_POINTS:
            problems.append(f"{label} has only {len(variant.points)} point(s)")
        if route.kind == "anchor" and not variant.pattern:
            problems.append(f"{label} publishes no anchor pattern")

    if not any(len(v.points) >= AR_MIN_POINTS for v in route.variants):
        return problems

    for point in route.all_points:
        assert point.lat is not None and point.lon is not None
        if not (LAT_RANGE[0] <= point.lat <= LAT_RANGE[1] and LON_RANGE[0] <= point.lon <= LON_RANGE[1]):
            problems.append(f"point {point.label} at ({point.lat:.3f},{point.lon:.3f}) outside the Americas")

    block = route.altitude
    if block is not None and block.parsed and block.floor_ft is not None and block.ceiling_ft is not None and block.floor_ft > block.ceiling_ft:
        problems.append(f"altitude floor {block.floor_ft} above ceiling {block.ceiling_ft}")

    problems.extend(route.warnings)
    return problems


def iter_frd_probes(routes: list[MilitaryRoute]) -> list[FrdProbe]:
    probes: list[FrdProbe] = []
    for route in routes:
        for point in route.points:
            if point.frd_fix is None or point.lat is None or point.lon is None or point.frd_distance is None:
                continue
            assert point.frd_radial is not None
            probes.append(FrdProbe(route.designator, point.label, point.frd_fix, point.frd_radial, point.frd_distance, point.lat, point.lon))
    return probes


def iter_ar_frd_probes(routes: list[ArRoute]) -> list[FrdProbe]:
    probes: list[FrdProbe] = []
    for route in routes:
        for point in route.all_points:
            if point.facility is None or point.distance is None or point.radial is None or not point.complete:
                continue
            assert point.lat is not None and point.lon is not None
            probes.append(FrdProbe(route.designator, point.label, point.facility, point.radial, point.distance, point.lat, point.lon))
    return probes


def fetch_arcgis(url: str, fields: str, geometry: bool, where: str = "1=1") -> list[dict]:
    features: list[dict] = []
    offset = 0
    while True:
        query = urllib.parse.urlencode(
            {
                "where": where,
                "outFields": fields,
                "returnGeometry": "true" if geometry else "false",
                "outSR": "4326",
                "resultOffset": offset,
                "resultRecordCount": ARCGIS_PAGE,
                "f": "json",
            }
        )
        with urllib.request.urlopen(f"{url}?{query}", timeout=180) as response:
            page = json.loads(response.read()).get("features", [])
        if not page:
            return features
        features.extend(page)
        offset += ARCGIS_PAGE


def load_navaids(cache: Path) -> dict[str, list[tuple[float, float]]]:
    if cache.exists():
        raw = json.loads(cache.read_text(encoding="utf-8"))
        return {k: [(e["lat"], e["lon"]) for e in v] for k, v in raw.items()}

    navaids: dict[str, list[dict]] = {}
    for feature in fetch_arcgis(NAVAID_URL, "IDENT", geometry=True):
        ident = (feature["attributes"].get("IDENT") or "").strip().upper()
        geometry = feature.get("geometry") or {}
        if ident and "x" in geometry:
            navaids.setdefault(ident, []).append({"lat": geometry["y"], "lon": geometry["x"]})
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text(json.dumps(navaids), encoding="utf-8")
    return {k: [(e["lat"], e["lon"]) for e in v] for k, v in navaids.items()}


def run_frd_oracle(probes: list[FrdProbe], navaids: dict[str, list[tuple[float, float]]]) -> dict:
    """Prove row association: navaid-to-point distance must match the published Fac/Rad/Dist."""
    errors: list[float] = []
    disagreements: list[dict] = []
    unknown = 0

    for probe in probes:
        entries = navaids.get(probe.fix.upper())
        if not entries:
            unknown += 1
            continue
        best = min(abs(haversine_nm(lat, lon, probe.lat, probe.lon) - probe.distance) for lat, lon in entries)
        errors.append(best)
        if best > FRD_ORACLE_TOLERANCE_NM:
            disagreements.append(
                {
                    "route": probe.route,
                    "point": probe.point,
                    "frd": f"{probe.fix} {probe.radial:03d}/{probe.distance}",
                    "errorNm": round(best, 1),
                }
            )

    checked = len(errors)
    agreed = checked - len(disagreements)
    return {
        "checked": checked,
        "agreed": agreed,
        "rate": round(agreed / checked, 6) if checked else 0.0,
        "unknownNavaid": unknown,
        "medianErrorNm": round(statistics.median(errors), 3) if errors else None,
        "p95ErrorNm": round(statistics.quantiles(errors, n=20)[18], 3) if len(errors) > 20 else None,
        "disagreements": sorted(disagreements, key=lambda d: -d["errorNm"]),
    }


def load_faa_segments() -> dict[str, list[tuple[float, float]]]:
    """FAA AIS MTRSegment vertices keyed by designator (IR/VR only; the layer carries no SRs)."""
    kinds = {0: "IR", 1: "VR"}
    vertices: dict[str, list[tuple[float, float]]] = {}
    for feature in fetch_arcgis(MTR_SEGMENT_URL, "IDENT,MTR_TYPE", geometry=True):
        attributes = feature["attributes"]
        kind = kinds.get(attributes.get("MTR_TYPE"))
        ident = (attributes.get("IDENT") or "").strip()
        if kind is None or not ident:
            continue
        for path in (feature.get("geometry") or {}).get("paths", []):
            vertices.setdefault(f"{kind}{ident}", []).extend((lat, lon) for lon, lat in path)
    return vertices


def run_cross_check(routes: list[MilitaryRoute], faa: dict[str, list[tuple[float, float]]]) -> dict:
    """Compare parsed geometry against the FAA layer for the routes it also carries.

    The measured direction is FAA-vertex to nearest-parsed-point: the FAA layer is a subset of
    AP/1B, not a mirror of it, and it is sometimes badly truncated -- VR1351 is a complete
    14-point route across Washington in AP/1B but only two vertices in the layer. Measuring the
    other way makes every truncated route look like a 160 NM parse error, when in fact each of
    our points passed the FRD oracle. The reverse direction is still reported, informationally.
    """
    compared: list[dict] = []
    for route in routes:
        vertices = faa.get(route.designator)
        points = [(p.lat, p.lon) for p in route.points if p.lat is not None and p.lon is not None]
        if not vertices or not points:
            continue
        forward = [min(haversine_nm(lat, lon, plat, plon) for plat, plon in points) for lat, lon in vertices]
        reverse = [min(haversine_nm(plat, plon, lat, lon) for lat, lon in vertices) for plat, plon in points]
        compared.append(
            {
                "route": route.designator,
                "faaVertices": len(vertices),
                "parsedPoints": len(points),
                "p95Nm": round(statistics.quantiles(forward, n=20)[18] if len(forward) > 20 else max(forward), 2),
                "maxNm": round(max(forward), 2),
                "reverseMaxNm": round(max(reverse), 2),
            }
        )

    diverging = sorted((c for c in compared if c["maxNm"] > CROSS_CHECK_FATAL_NM), key=lambda c: -c["maxNm"])
    return {
        "compared": len(compared),
        "faaRoutes": len(faa),
        "missingFromParse": sorted(set(faa) - {r.designator for r in routes}),
        "overP95Tolerance": [c for c in compared if c["p95Nm"] > CROSS_CHECK_P95_NM],
        "diverging": diverging,
        "divergingFraction": round(len(diverging) / len(compared), 4) if compared else 0.0,
    }


def route_payload(route: MilitaryRoute) -> dict:
    return {
        "designator": route.designator,
        "printed": route.printed,
        "type": route.kind,
        "oneWay": True,
        "page": route.page,
        "entryPoints": route.entry_points,
        "exitPoints": route.exit_points,
        "terrainFollowing": route.terrain_following,
        "originatingActivity": route.originating_activity,
        "schedulingActivity": route.scheduling_activity,
        "hours": route.hours,
        "widths": [asdict(w) for w in route.widths],
        "points": [
            {
                "id": p.label,
                "name": f"{route.designator}{p.label}",
                "lat": round(p.lat, 6),
                "lon": round(p.lon, 6),
                "role": p.role,
                **({"frd": f"{p.frd_fix}{p.frd_radial:03d}{p.frd_distance:03d}"} if p.frd_fix else {}),
                "altitude": {k: v for k, v in asdict(p.altitude).items() if v is not None} if p.altitude else {},
            }
            for p in route.points
            if p.lat is not None and p.lon is not None
        ],
    }


def document_text(routes: list[MilitaryRoute], source_sha: str, edition: str, oracle: dict, cross: dict) -> str:
    """Hand-diffable layout: one compact route per line under a stable header."""
    payloads = [route_payload(r) for r in sorted(routes, key=lambda r: (r.kind, r.designator))]
    metadata = {
        "source": SOURCE_URL,
        "sourceSha256": source_sha,
        "edition": edition,
        "generatedAt": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "routeCount": len(payloads),
        "pointCount": sum(len(p["points"]) for p in payloads),
        "byType": dict(Counter(p["type"] for p in payloads)),
        "frdOracleRate": oracle["rate"],
        "crossCheckCompared": cross.get("compared", 0),
        "note": "DoD AP/1B military training routes. Not for navigation. One-way; reversals prohibited.",
    }
    lines = ["{", '"metadata": ' + json.dumps(metadata) + ",", '"routes": [']
    for index, payload in enumerate(payloads):
        comma = "," if index < len(payloads) - 1 else ""
        lines.append(json.dumps(payload, separators=(",", ":")) + comma)
    lines.extend(["]", "}"])
    return "\n".join(lines) + "\n"


def ar_point_payload(route: ArRoute, point: ArPoint) -> dict:
    assert point.lat is not None and point.lon is not None
    return {
        "id": point.label,
        "name": f"{route.designator}{point.label}",
        "lat": round(point.lat, 6),
        "lon": round(point.lon, 6),
        "role": point.role,
        **({"frd": f"{point.facility}{point.radial:03d}{point.distance:03d}"} if point.facility and point.distance else {}),
    }


def ar_variant_payload(route: ArRoute, variant: ArVariant) -> dict:
    return {
        "direction": variant.direction or "",
        "entryPoints": [p.label for p in variant.points if p.role in ("arip", "entry")],
        "exitPoints": [p.label for p in variant.points if p.role == "exit"],
        "points": [ar_point_payload(route, p) for p in variant.points],
        "pattern": [ar_point_payload(route, p) for p in variant.pattern],
    }


def ar_payload(route: ArRoute) -> dict:
    return {
        "designator": route.designator,
        "printed": route.designator,
        "type": "AR",
        "arKind": route.kind,
        "page": route.page,
        "schedulingActivity": route.scheduling_activity,
        "artcc": route.artcc,
        "hours": route.hours,
        "altitude": {k: v for k, v in asdict(route.altitude).items() if v is not None} if route.altitude else {},
        # One entry per published direction. A track's two directions are separate geometries, so
        # the clearance picks the variant the aircraft is actually positioned to fly.
        "variants": [ar_variant_payload(route, v) for v in route.variants],
        "airspace": [[round(lat, 6), round(lon, 6)] for lat, lon in route.airspace],
        "remarks": route.remarks,
    }


def ar_document_text(routes: list[ArRoute], source_sha: str, edition: str, oracle: dict) -> str:
    payloads = [ar_payload(r) for r in sorted(routes, key=lambda r: (r.kind, r.page, r.designator))]
    metadata = {
        "source": SOURCE_URL,
        "sourceSha256": source_sha,
        "edition": edition,
        "generatedAt": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "routeCount": len(payloads),
        "pointCount": sum(len(v["points"]) for p in payloads for v in p["variants"]),
        "byType": dict(Counter(p["arKind"] for p in payloads)),
        "frdOracleRate": oracle["rate"],
        "note": "DoD AP/1B chapter 5 aerial refueling tracks and anchors. Not for navigation.",
    }
    lines = ["{", '"metadata": ' + json.dumps(metadata) + ",", '"routes": [']
    for index, payload in enumerate(payloads):
        lines.append(json.dumps(payload, separators=(",", ":")) + ("," if index < len(payloads) - 1 else ""))
    lines.extend(["]", "}"])
    return "\n".join(lines) + "\n"


def check_ar_invariants(routes: list[ArRoute], oracle: dict) -> list[str]:
    failures: list[str] = []
    counts = Counter(r.kind for r in routes)
    for kind, expected in AR_EXPECTED_COUNTS.items():
        actual = counts.get(kind, 0)
        if abs(actual - expected) > expected * COUNT_TOLERANCE:
            failures.append(f"AR {kind} count {actual} differs from expected {expected} by more than {COUNT_TOLERANCE:.0%}")

    duplicates = [d for d, n in Counter(r.designator for r in routes).items() if n > 1]
    if duplicates:
        failures.append(f"duplicate AR designators: {', '.join(sorted(duplicates)[:10])}")

    if oracle["checked"] and oracle["rate"] < FRD_ORACLE_GATE:
        failures.append(f"AR FRD oracle agreement {oracle['rate']:.2%} below gate {FRD_ORACLE_GATE:.0%}")

    names: dict[str, tuple[float, float]] = {}
    for route in routes:
        for point in route.all_points:
            name = f"{route.designator}{point.label}"
            if name[-3:].isdigit() or name[-6:].isdigit():
                failures.append(f"synthetic name {name} would be misread as an FRD anchor")
            # Two directions share a designator, so a labelling slip would silently map one
            # synthetic fix name to two positions hundreds of NM apart.
            assert point.lat is not None and point.lon is not None
            previous = names.setdefault(name, (point.lat, point.lon))
            if haversine_nm(previous[0], previous[1], point.lat, point.lon) > AR_REVERSAL_TOLERANCE_NM:
                failures.append(f"synthetic name {name} maps to two different positions")
    return failures


def build_ar_report(routes: list[ArRoute], problems: dict[str, list[str]], oracle: dict) -> dict:
    multi = [r for r in routes if len(r.variants) >= 2]
    return {
        "routes": len(routes),
        "byType": dict(Counter(r.kind for r in routes)),
        "points": sum(len(v.points) for r in routes for v in r.variants),
        "patternCorners": sum(len(v.pattern) for r in routes for v in r.variants),
        "multiDirection": len(multi),
        "exactReversals": sum(1 for r in multi if is_reversed_pair(r.variants[0], r.variants[1])),
        "withAirspace": sum(1 for r in routes if r.airspace),
        "altitudeKinds": dict(Counter(r.altitude.kind for r in routes if r.altitude is not None)),
        "unparsedAltitudes": sorted({r.altitude_raw for r in routes if r.altitude is not None and not r.altitude.parsed})[:40],
        "routeProblems": problems,
        "frdOracle": oracle,
    }


def find_edition(pdf: pdfplumber.PDF) -> str:
    text = pdf.pages[0].extract_text() or ""
    effective = re.search(r"EFFECTIVE\s+\d{4}L\s+(\d{1,2}\s+\w{3}\s+\d{4})", text)
    cycle = re.search(r"CYCLE\s+(\d{4})", text)
    return f"{effective.group(1) if effective else 'unknown'} (cycle {cycle.group(1) if cycle else '?'})"


def write_outputs(text: str, out_path: Path, report: dict) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.suffix == ".br":
        import brotli  # lazy: only the compressed fixture needs it

        out_path.write_bytes(brotli.compress(text.encode("utf-8"), quality=11))
    else:
        out_path.write_text(text, encoding="utf-8", newline="\n")

    # The fixture is Brotli, so its inline metadata is not greppable in the repo; the sidecar
    # carries provenance and QA in plain text for review and for the next refresh.
    sidecar = out_path.with_suffix("").with_suffix(".meta")
    sidecar.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8", newline="\n")


def check_global_invariants(routes: list[MilitaryRoute], oracle: dict, cross: dict) -> list[str]:
    failures: list[str] = []
    counts = Counter(r.kind for r in routes)
    for kind, expected in EXPECTED_COUNTS.items():
        actual = counts.get(kind, 0)
        if abs(actual - expected) > expected * COUNT_TOLERANCE:
            failures.append(f"{kind} count {actual} differs from expected {expected} by more than {COUNT_TOLERANCE:.0%}")

    duplicates = [d for d, n in Counter(r.designator for r in routes).items() if n > 1]
    if duplicates:
        failures.append(f"duplicate designators: {', '.join(sorted(duplicates)[:10])}")

    if oracle["checked"] and oracle["rate"] < FRD_ORACLE_GATE:
        failures.append(f"FRD oracle agreement {oracle['rate']:.2%} below gate {FRD_ORACLE_GATE:.0%}")

    # An individual route disagreeing with the FAA layer is upstream drift, not our bug: the two
    # sources publish on different cycles and diverge in both directions. VR1351 is a complete
    # 14-point route in AP/1B but only two vertices in the layer, IR-177 ends at point Q in
    # AP/1B while the layer still carries a longer earlier version, and IR-983 is in the layer
    # but absent from AP/1B 2607 entirely. Only a *systematic* disagreement means the parser
    # regressed, so the gate is on the fraction, not on any single route.
    if cross.get("compared") and cross["divergingFraction"] > MAX_DIVERGING_FRACTION:
        failures.append(
            f"cross-check: {cross['divergingFraction']:.1%} of shared routes disagree with the FAA layer by more than {CROSS_CHECK_FATAL_NM:.0f} NM"
        )

    # Synthetic fix names are handed to FrdResolver.ParseFrd, which reads a name as an FRD when
    # its last six characters are all digits ({FIX}{radial}{distance}) or its last three are
    # ({FIX}{radial}). A single trailing digit is harmless -- alternate points are labelled D1,
    # P2, AE3, so "IR109D1" ends in "9D1" and parses as a plain fix name. Assert the real rule
    # rather than "must not end in a digit", which would reject every alternate point.
    for route in routes:
        for point in route.points:
            name = f"{route.designator}{point.label}"
            if name[-3:].isdigit() or name[-6:].isdigit():
                failures.append(f"synthetic name {name} would be misread as an FRD anchor")
    return failures


def build_report(routes: list[MilitaryRoute], problems: dict[str, list[str]], oracle: dict, cross: dict) -> dict:
    kinds = Counter(r.kind for r in routes)
    altitude_kinds = Counter(p.altitude.kind for r in routes for p in r.points if p.altitude is not None)
    with_frd = Counter(r.kind for r in routes for p in r.points if p.frd_fix)
    points_by_kind = Counter(r.kind for r in routes for _ in r.points)
    return {
        "routes": len(routes),
        "byType": dict(kinds),
        "points": sum(len(r.points) for r in routes),
        "pointsByType": dict(points_by_kind),
        "altitudeKinds": dict(altitude_kinds),
        "unparsedAltitudes": sorted({p.altitude.raw for r in routes for p in r.points if p.altitude is not None and not p.altitude.parsed})[:80],
        "frdCoverage": {kind: round(with_frd.get(kind, 0) / points_by_kind[kind], 4) for kind in points_by_kind if points_by_kind[kind]},
        "routesWithWidths": sum(1 for r in routes if r.widths),
        "routeProblems": problems,
        "frdOracle": oracle,
        "crossCheck": cross,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, help="path to a locally downloaded ap1b.pdf")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--ar-out", type=Path, default=DEFAULT_AR_OUTPUT, help="chapter 5 refueling fixture")
    parser.add_argument("--report", type=Path, help="write the full QA report here as well")
    parser.add_argument("--navaid-cache", type=Path, default=Path(".tmp/navaids.json"))
    parser.add_argument("--no-cross-check", action="store_true", help="skip the FAA layer comparison (offline)")
    parser.add_argument("--no-oracle", action="store_true", help="skip the FRD oracle (offline)")
    args = parser.parse_args()

    source = Path(args.input)
    source_sha = hashlib.sha256(source.read_bytes()).hexdigest()

    with pdfplumber.open(source) as pdf:
        edition = find_edition(pdf)
        routes = extract_routes(pdf)
        ar_routes = extract_ar_routes(pdf)
    print(f"  parsed {len(routes)} routes and {len(ar_routes)} refueling entries from {source} ({edition})", file=sys.stderr)

    for route in routes:
        enrich_route(route)

    problems: dict[str, list[str]] = {}
    kept: list[MilitaryRoute] = []
    for route in routes:
        issues = validate_route(route)
        if issues:
            problems[route.designator] = issues
        if len(route.points) >= MIN_POINTS:
            kept.append(route)
        else:
            print(f"  SKIP {route.designator}: {'; '.join(issues)}", file=sys.stderr)

    ar_problems: dict[str, list[str]] = {}
    ar_kept: list[ArRoute] = []
    for route in ar_routes:
        issues = validate_ar_route(route)
        if issues:
            ar_problems[route.designator] = issues
        route.variants = [v for v in route.variants if len(v.points) >= AR_MIN_POINTS]
        if route.variants:
            ar_kept.append(route)
        else:
            print(f"  SKIP {route.designator}: {'; '.join(issues)}", file=sys.stderr)

    empty_oracle = {"checked": 0, "agreed": 0, "rate": 0.0, "unknownNavaid": 0, "disagreements": []}
    oracle, ar_oracle = dict(empty_oracle), dict(empty_oracle)
    if not args.no_oracle:
        navaids = load_navaids(args.navaid_cache)
        oracle = run_frd_oracle(iter_frd_probes(kept), navaids)
        ar_oracle = run_frd_oracle(iter_ar_frd_probes(ar_kept), navaids)
        print(f"  FRD oracle: {oracle['agreed']}/{oracle['checked']} ({oracle['rate']:.2%})", file=sys.stderr)
        print(f"  AR FRD oracle: {ar_oracle['agreed']}/{ar_oracle['checked']} ({ar_oracle['rate']:.2%})", file=sys.stderr)

    cross: dict = {}
    if not args.no_cross_check:
        cross = run_cross_check(kept, load_faa_segments())
        print(f"  FAA cross-check: {cross['compared']} of {cross['faaRoutes']} shared routes", file=sys.stderr)

    report = build_report(kept, problems, oracle, cross)
    ar_report = build_ar_report(ar_kept, ar_problems, ar_oracle)
    report["aerialRefueling"] = ar_report
    failures = check_global_invariants(kept, oracle, cross) + check_ar_invariants(ar_kept, ar_oracle)
    if failures:
        for failure in failures:
            print(f"  FAIL {failure}", file=sys.stderr)
        if args.report:
            args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
        raise SystemExit("global invariants failed; fixture not written")

    write_outputs(document_text(kept, source_sha, edition, oracle, cross), args.out, report)
    write_outputs(ar_document_text(ar_kept, source_sha, edition, ar_oracle), args.ar_out, ar_report)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")

    size_kb = args.out.stat().st_size / 1024
    ar_size_kb = args.ar_out.stat().st_size / 1024
    print(
        f"OK: {len(kept)} routes / {report['points']} points -> {args.out} ({size_kb:.0f} KB)",
        file=sys.stderr,
    )
    print(
        f"OK: {len(ar_kept)} refueling entries / {ar_report['points']} points -> {args.ar_out} ({ar_size_kb:.0f} KB)",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
