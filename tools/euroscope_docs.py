#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.13"
# dependencies = [
#     "pymupdf>=1.24",
# ]
# ///
"""Extract a curated subset of the EuroScope user manual PDF into docs/euroscope/.

Mirrors docs/crc/ conventions (kebab-case .md files, no frontmatter,
<figure>/<img>/<figcaption> blocks for images, img/<section>/ subfolders).

Usage:
    uv run tools/euroscope_docs.py                        # download + extract with defaults
    uv run tools/euroscope_docs.py --pdf path/to/file.pdf # use a local PDF
    uv run tools/euroscope_docs.py --out custom/path      # override output dir
    uv run tools/euroscope_docs.py --force-download       # re-download even if cached

The PDF is cached at .tmp/euroscope-manual.pdf and reused on subsequent runs.

The curated section list lives in SECTIONS below. Anything outside those page ranges is
intentionally dropped: this is a focused reference for YAAT's EuroScope-style tag mode,
not a faithful 1:1 copy of the manual.

Layout-aware text extraction uses PyMuPDF dict mode and classifies each line by its
bbox.x0 indent (~70 body, ~95 bullet, ~107 bullet-wrap, ~130 sub-bullet, ~143 sub-wrap).
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

import fitz  # pymupdf

DEFAULT_PDF_URL = "https://www.euroscope.hu/documents/EuroScopeUsersManual.3.2.0.20.pdf"
DEFAULT_PDF_NAME = "euroscope-manual.pdf"
PROJECT_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = PROJECT_ROOT / "docs" / "euroscope"
DEFAULT_TMP = PROJECT_ROOT / ".tmp"


@dataclass
class Section:
    slug: str
    out_file: str
    title: str
    ranges: list[tuple[str, str]]
    intro: str = ""
    headings: list[str] = field(default_factory=list)


SECTIONS: list[Section] = [
    Section(
        slug="overview",
        out_file="overview.md",
        title="EuroScope Overview",
        intro=(
            "This is a curated subset of the [EuroScope User's Manual](https://www.euroscope.hu/) "
            "(v3.2.1.20) focused on the tag editor, pseudopilot tag elements, and supporting "
            "display settings. It is the primary-source reference for YAAT's EuroScope-style "
            "interactive tag mode (see `docs/euroscope/pseudopilot.md`).\n\n"
            "Sections outside this curated set were intentionally not extracted. Refer to the "
            "[full manual](https://www.euroscope.hu/) for plug-in API, ESE files, ATIS, and "
            "other EuroScope-specific topics that don't apply to YAAT.\n\n"
            "## Contents\n\n"
            "- [Tags](/tags)\n"
            "- [TAG Editor](/tag-editor)\n"
            "- [Pseudopilot Tag Elements](/pseudopilot)\n"
            "- [Display & Symbology Settings](/display-settings)\n"
            "- [Flight Data Conventions](/flight-data)\n"
        ),
        ranges=[],
        headings=[],
    ),
    Section(
        slug="tags",
        out_file="tags.md",
        title="Tags",
        ranges=[("TAGs in general", "TAG Editor")],
        headings=[
            "TAGs in general",
            "TAG types",
            "Default Matias TAGs",
            "Detailed",
            "Untagged",
            "Moving The TAGS",
        ],
    ),
    Section(
        slug="tag-editor",
        out_file="tag-editor.md",
        title="TAG Editor",
        ranges=[("TAG Editor", "Settings")],
        headings=[
            "TAG Editor",
            "TAG Families",
            "How A TAG Is Built Up",
            "Functions From TAG",
            "Editing The TAGs",
        ],
    ),
    Section(
        slug="pseudopilot",
        out_file="pseudopilot.md",
        title="Pseudopilot Tag Elements",
        intro=(
            "This is the flagship reference for YAAT's EuroScope-style interactive tag mode. "
            "It documents how EuroScope's pseudopilot uses the radar tag itself as the primary "
            "control surface -- clicking a field opens a flyout, and AHDG supports an elastic "
            "vector that draws turn radius live as you drag toward a point on the map."
        ),
        ranges=[("Pseudopilot tag elements", "Scenario editor")],
        headings=[
            "Pseudopilot tag elements",
            "The Simulator Control Ribbon",
            "Information Area",
            "Route Ribbon",
            "Status Ribbon",
            "Approach Ribbon",
            "Ground Simulator Ribbon",
            "Takeoff Ribbon",
            "Emergency Ribbon",
            "Lights Ribbon",
        ],
    ),
    Section(
        slug="display-settings",
        out_file="display-settings.md",
        title="Display & Symbology Settings",
        ranges=[("Symbology Settings", "Plug-Ins")],
        headings=["Symbology Settings"],
    ),
    Section(
        slug="flight-data",
        out_file="flight-data.md",
        title="Flight Data Conventions",
        ranges=[("Scratch Pad Strings", "Flight plan route section")],
        headings=["Scratch Pad Strings", "Temporary Altitude"],
    ),
]


def download_pdf(url: str, dest: Path, *, force: bool) -> None:
    if dest.exists() and not force:
        print(f"[skip] {dest} already exists ({dest.stat().st_size:,} bytes)")
        return
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"[get ] {url}")
    with urllib.request.urlopen(url) as src, dest.open("wb") as out:  # noqa: S310 (trusted PDF URL)
        shutil.copyfileobj(src, out)
    print(f"[ok  ] saved {dest} ({dest.stat().st_size:,} bytes)")


def line_text(line: dict) -> str:
    return "".join(s["text"] for s in line["spans"]).rstrip()


# Punctuation normalization: PyMuPDF preserves the source-PDF punctuation glyphs.
# We map curly quotes / dashes to plain ASCII so the markdown is clean.
_PUNCT_MAP = str.maketrans(
    {
        "‘": "'",
        "’": "'",
        "“": '"',
        "”": '"',
        "–": "-",
        "—": "--",
        " ": " ",
    }
)


def normalize_punct(text: str) -> str:
    return text.translate(_PUNCT_MAP)


def is_page_chrome(text: str) -> bool:
    if re.fullmatch(r"\d+", text.strip()):
        return True
    if re.fullmatch(r"\s*EuroScope User'?s Manual.*", text, re.IGNORECASE):
        return True
    return False


def find_heading_page(doc: fitz.Document, heading: str) -> int | None:
    """Return the 0-based page index where heading first appears as a stand-alone line.

    A stand-alone line is one whose stripped text exactly equals the heading and which
    is not nested inside a longer paragraph. We walk dict mode lines and check exact match.
    """
    target = heading.strip().lower()
    for page_idx in range(doc.page_count):
        page = doc.load_page(page_idx)
        d = page.get_text("dict")
        for block in d["blocks"]:
            if block["type"] != 0:
                continue
            for line in block["lines"]:
                txt = normalize_punct(line_text(line)).strip()
                if txt.lower() == target:
                    return page_idx
    return None


def find_heading_y(doc: fitz.Document, page_idx: int, heading: str) -> float | None:
    target = heading.strip().lower()
    page = doc.load_page(page_idx)
    d = page.get_text("dict")
    for block in d["blocks"]:
        if block["type"] != 0:
            continue
        for line in block["lines"]:
            if normalize_punct(line_text(line)).strip().lower() == target:
                return line["bbox"][1]
    return None


# Indent tiers in points (PyMuPDF dict bbox.x0). Detected empirically.
BODY_X = 90.0
BULLET_X = 105.0
SUBBULLET_X = 125.0
SUBBULLET_WRAP_X = 140.0

_BULLET_GLYPHS = "•·"  # bullet, middle dot, private-use bullet
BULLET_PREFIX = re.compile(rf"^[{_BULLET_GLYPHS}]\s*")
SUBBULLET_PREFIX = re.compile(r"^o\s+")


def classify_line(line: dict) -> tuple[str, str] | None:
    raw = line_text(line)
    if not raw.strip():
        return None
    text = normalize_punct(raw).strip()
    if not text:
        return None
    if is_page_chrome(text):
        return None
    x0 = line["bbox"][0]
    if x0 < BODY_X:
        return ("body", text)
    if x0 < BULLET_X:
        return ("bullet", BULLET_PREFIX.sub("", text))
    if x0 < SUBBULLET_X:
        return ("bullet_wrap", text)
    if x0 < SUBBULLET_WRAP_X:
        return ("subbullet", SUBBULLET_PREFIX.sub("", text))
    return ("subbullet_wrap", text)


@dataclass
class Unit:
    kind: str  # 'heading' | 'bullet' | 'subbullet' | 'para' | 'figure'
    text: str
    page_idx: int


def extract_units(
    doc: fitz.Document,
    page_indices: list[int],
    img_dir: Path,
    headings_set: set[str],
    start_y_first: float | None,
    end_y_last: float | None,
) -> list[Unit]:
    units: list[Unit] = []
    img_dir.mkdir(parents=True, exist_ok=True)
    fig_index = 1

    pending_kind: str | None = None  # 'body' | 'bullet' | 'subbullet'
    pending_lines: list[str] = []
    pending_page = -1

    def flush() -> None:
        nonlocal pending_kind, pending_lines, pending_page
        if not pending_lines:
            pending_kind = None
            return
        joined = re.sub(r"\s+", " ", " ".join(pending_lines)).strip()
        if not joined:
            pending_lines = []
            pending_kind = None
            return
        kind = pending_kind or "para"
        if kind == "body":
            kind = "para"
        if joined in headings_set and kind == "para":
            kind = "heading"
        units.append(Unit(kind=kind, text=joined, page_idx=pending_page))
        pending_lines = []
        pending_kind = None

    for i, page_idx in enumerate(page_indices):
        page = doc.load_page(page_idx)
        d = page.get_text("dict")
        items: list[tuple[float, str, object]] = []

        is_first = i == 0
        is_last = i == len(page_indices) - 1

        for block in d["blocks"]:
            y0 = block["bbox"][1]
            if is_first and start_y_first is not None and block["bbox"][3] < start_y_first - 2:
                continue
            if is_last and end_y_last is not None and y0 >= end_y_last:
                continue
            if block["type"] == 1:
                w, h = block.get("width", 0), block.get("height", 0)
                if w < 80 or h < 60:
                    continue
                items.append((y0, "image", block))
            else:
                for line in block["lines"]:
                    y_line = line["bbox"][1]
                    if is_first and start_y_first is not None and y_line + 2 < start_y_first:
                        continue
                    if is_last and end_y_last is not None and y_line >= end_y_last:
                        continue
                    items.append((y_line, "line", line))

        items.sort(key=lambda t: t[0])

        for _y, kind, payload in items:
            if kind == "image":
                flush()
                ext = payload.get("ext", "png")
                fname = f"fig-{fig_index:02d}.{ext}"
                (img_dir / fname).write_bytes(payload["image"])
                rel = f"img/{img_dir.name}/{fname}"
                units.append(Unit(kind="figure", text=rel, page_idx=page_idx))
                fig_index += 1
                continue

            classified = classify_line(payload)
            if classified is None:
                continue
            cls, txt = classified

            if cls == "body":
                if txt in headings_set:
                    flush()
                    units.append(Unit(kind="heading", text=txt, page_idx=page_idx))
                    continue
                if pending_kind != "body":
                    flush()
                    pending_kind = "body"
                    pending_page = page_idx
                pending_lines.append(txt)
            elif cls == "bullet":
                flush()
                pending_kind = "bullet"
                pending_page = page_idx
                pending_lines.append(txt)
            elif cls == "bullet_wrap":
                if pending_kind in ("bullet", "subbullet"):
                    pending_lines.append(txt)
                else:
                    if pending_kind != "body":
                        flush()
                        pending_kind = "body"
                        pending_page = page_idx
                    pending_lines.append(txt)
            elif cls == "subbullet":
                flush()
                pending_kind = "subbullet"
                pending_page = page_idx
                pending_lines.append(txt)
            elif cls == "subbullet_wrap":
                if pending_kind == "subbullet":
                    pending_lines.append(txt)
                else:
                    if pending_kind != "body":
                        flush()
                        pending_kind = "body"
                        pending_page = page_idx
                    pending_lines.append(txt)

    flush()
    return units


def render_units(units: list[Unit]) -> str:
    lines: list[str] = []
    last_was_bullet = False
    for u in units:
        if u.kind == "heading":
            lines.append("")
            lines.append(f"## {u.text}")
            lines.append("")
            last_was_bullet = False
        elif u.kind == "para":
            if last_was_bullet:
                lines.append("")
            lines.append(u.text)
            lines.append("")
            last_was_bullet = False
        elif u.kind == "bullet":
            lines.append(f"- {u.text}")
            last_was_bullet = True
        elif u.kind == "subbullet":
            lines.append(f"  - {u.text}")
            last_was_bullet = True
        elif u.kind == "figure":
            if last_was_bullet:
                lines.append("")
            lines.append("<figure>")
            lines.append(f'    <img src="{u.text}" style="max-height: 400px;"/>')
            lines.append(f'    <figcaption>Fig. <span class="counter"></span> - p.{u.page_idx + 1}</figcaption>')
            lines.append("</figure>")
            lines.append("")
            last_was_bullet = False
    text = "\n".join(lines)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip() + "\n"


def render_section(doc: fitz.Document, section: Section, out_root: Path) -> None:
    img_dir = out_root / "img" / section.slug
    if img_dir.exists():
        shutil.rmtree(img_dir)
    img_dir.mkdir(parents=True, exist_ok=True)

    parts: list[str] = [f"# {section.title}\n"]
    if section.intro:
        parts.append(section.intro.rstrip())

    headings_set = set(section.headings)

    if not section.ranges:
        out_file = out_root / section.out_file
        out_file.write_text("\n\n".join(p.rstrip() for p in parts) + "\n", encoding="utf-8")
        if not any(img_dir.iterdir()):
            shutil.rmtree(img_dir)
        print(f"[ok  ] {out_file.relative_to(PROJECT_ROOT)}  (intro-only)")
        return

    for start_h, end_h in section.ranges:
        start_page = find_heading_page(doc, start_h)
        end_page = find_heading_page(doc, end_h)
        if start_page is None:
            print(f"[warn] {section.slug}: start heading not found: {start_h!r}")
            continue
        if end_page is None or end_page <= start_page:
            print(f"[warn] {section.slug}: end heading not found or before start: {end_h!r}")
            end_page = min(start_page + 8, doc.page_count - 1)
        page_indices = list(range(start_page, end_page + 1))
        start_y = find_heading_y(doc, start_page, start_h)
        end_y = find_heading_y(doc, end_page, end_h)
        units = extract_units(doc, page_indices, img_dir, headings_set, start_y, end_y)
        # Drop a redundant leading heading that duplicates the section title.
        title_lower = section.title.strip().lower()
        while units and units[0].kind == "heading" and units[0].text.strip().lower() == title_lower:
            units.pop(0)
        # Collapse consecutive duplicate headings (PDF often repeats e.g. "Symbology Settings"
        # both as section header and as dialog subsection header).
        deduped: list[Unit] = []
        for u in units:
            if (
                u.kind == "heading"
                and deduped
                and deduped[-1].kind == "heading"
                and deduped[-1].text.strip().lower() == u.text.strip().lower()
            ):
                continue
            deduped.append(u)
        parts.append(render_units(deduped))

    body = "\n\n".join(p.rstrip() for p in parts if p.strip()) + "\n"
    body = re.sub(r"\n{3,}", "\n\n", body)
    out_file = out_root / section.out_file
    out_file.write_text(body, encoding="utf-8")
    fig_count = body.count("<figure>")
    line_count = body.count("\n")
    if not any(img_dir.iterdir()):
        shutil.rmtree(img_dir)
    print(f"[ok  ] {out_file.relative_to(PROJECT_ROOT)}  ({line_count} lines, {fig_count} figures)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--pdf", default=None, help="Local PDF path (skips download).")
    parser.add_argument("--url", default=DEFAULT_PDF_URL, help="PDF URL if not using --pdf.")
    parser.add_argument("--out", default=str(DEFAULT_OUT), help="Output directory for generated docs.")
    parser.add_argument(
        "--force-download", action="store_true", help="Re-download the PDF even if .tmp/ has a cached copy."
    )
    args = parser.parse_args()

    out_root = Path(args.out).resolve()
    out_root.mkdir(parents=True, exist_ok=True)

    if args.pdf:
        pdf_path = Path(args.pdf).resolve()
        if not pdf_path.exists():
            print(f"[fail] {pdf_path} does not exist", file=sys.stderr)
            return 2
    else:
        pdf_path = DEFAULT_TMP / DEFAULT_PDF_NAME
        download_pdf(args.url, pdf_path, force=args.force_download)

    print(f"[open] {pdf_path}")
    doc = fitz.open(pdf_path)
    print(f"[info] {doc.page_count} pages")

    for section in SECTIONS:
        render_section(doc, section, out_root)

    print(f"[done] wrote {len(SECTIONS)} sections to {out_root}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
