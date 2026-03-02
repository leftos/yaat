"""Scrape ATCTrainer + VICE command docs and build structured JSON.

Run from repo root:
    python3 docs/command-aliases/build.py

Fetches both source pages, parses HTML tables, writes:
    atctrainer-commands.json  — ATCTrainer canonical commands
    vice-commands.json        — VICE keyboard ATC commands

Requires: pip install beautifulsoup4 requests
"""

import json
import re
import sys
from pathlib import Path

try:
    import requests
    from bs4 import BeautifulSoup
except ImportError:
    print("Install dependencies: pip install beautifulsoup4 requests", file=sys.stderr)
    sys.exit(1)

OUT_DIR = Path(__file__).parent

ATCTRAINER_URL = "https://atctrainer.collinkoldoff.dev/docs/commands"
VICE_URL = "https://pharr.org/vice/"


# ---------------------------------------------------------------------------
# Scraping
# ---------------------------------------------------------------------------

def fetch_tables(url: str) -> list[tuple[str, list[list[str]]]]:
    """Fetch a page and return (heading, rows) for each HTML table."""
    resp = requests.get(url, timeout=30)
    resp.raise_for_status()
    soup = BeautifulSoup(resp.text, "html.parser")

    results: list[tuple[str, list[list[str]]]] = []
    for table in soup.find_all("table"):
        # Walk backward to find nearest heading
        heading = ""
        for sib in table.previous_elements:
            if sib.name in ("h2", "h3"):
                heading = sib.get_text(strip=True)
                break

        rows: list[list[str]] = []
        for tr in table.find_all("tr"):
            cells = [td.get_text(" ", strip=True) for td in tr.find_all(["td", "th"])]
            if cells:
                rows.append(cells)
        if rows:
            results.append((heading, rows))

    return results


# ---------------------------------------------------------------------------
# ATCTrainer normalization
# ---------------------------------------------------------------------------

def build_atctrainer() -> list[dict]:
    """Scrape and normalize ATCTrainer commands."""
    tables = fetch_tables(ATCTRAINER_URL)

    # Each ATCTrainer table has rows grouped by command syntax.
    # Row types: Description, Parameters, Examples, Aliases, Delayable
    raw: list[dict] = []
    for heading, rows in tables:
        if len(rows) < 2:
            continue
        for row in rows:
            if len(row) < 2:
                continue
            raw.append({"category": heading, "col0": row[0], "col1": row[1]})

    # Group into commands: a Description row starts a new command, subsequent
    # rows (Parameters, Examples, Aliases, Delayable) belong to it.
    commands: list[dict] = []
    i = 0
    while i < len(raw):
        entry = raw[i]
        if entry["col0"] != "Description:":
            i += 1
            continue

        # The previous row should be "Syntax:" with the command syntax
        syntax = ""
        if i > 0 and raw[i - 1]["col0"] == "Syntax:":
            syntax = raw[i - 1]["col1"]

        cmd: dict = {
            "category": entry["category"],
            "syntax": syntax.upper(),
            "description": entry["col1"].strip(),
            "aliases": [],
            "parameters": "",
            "examples": [],
            "delayable": False,
        }

        j = i + 1
        while j < len(raw) and raw[j]["col0"] not in ("Syntax:", "Description:"):
            row_type = raw[j]["col0"]
            val = raw[j]["col1"].strip()
            if "Parameter" in row_type:
                cmd["parameters"] = val
            elif "Example" in row_type:
                cmd["examples"] = [x.strip() for x in val.split("\n") if x.strip()]
            elif "Alias" in row_type:
                cmd["aliases"] = [
                    a.strip().upper()
                    for a in re.split(r'[,\s]+', val)
                    if a.strip()
                ]
            elif "Delay" in row_type:
                cmd["delayable"] = "yes" in val.lower()
            j += 1

        primary = re.split(r'[\s{(\[]+', cmd["syntax"])[0].strip()
        cmd["primary"] = primary if primary else cmd["syntax"].split()[0] if cmd["syntax"] else "?"

        commands.append(cmd)
        i = j

    # Annotate variants
    seen: dict[str, int] = {}
    for c in commands:
        p = c["primary"]
        if p in seen:
            seen[p] += 1
            c["variant"] = seen[p]
        else:
            seen[p] = 1

    return commands


# ---------------------------------------------------------------------------
# VICE normalization
# ---------------------------------------------------------------------------

def build_vice() -> list[dict]:
    """Scrape and normalize VICE keyboard ATC commands."""
    tables = fetch_tables(VICE_URL)
    commands: list[dict] = []

    for heading, rows in tables:
        if heading != "ATC Instructions (Keyboard)":
            continue
        if len(rows) < 2:
            continue
        for row in rows[1:]:  # skip header row
            if len(row) < 3:
                continue
            cmd_text = row[0].strip()
            func = row[1].strip()
            example = row[2].strip() if len(row) > 2 else ""

            if not cmd_text or cmd_text.startswith("Left Shift"):
                continue

            primary = ""
            args_hint = ""
            if cmd_text.startswith("/"):
                primary = "/"
                args_hint = cmd_text[1:]
            else:
                parts = cmd_text.split("/")
                m = re.match(r'^([A-Z]+)(.*)', parts[0])
                if m:
                    primary = m.group(1)
                    args_hint = m.group(2)
                    if len(parts) > 1:
                        args_hint += "/" + "/".join(parts[1:])

            commands.append({
                "primary": primary,
                "raw_syntax": cmd_text,
                "args_hint": args_hint,
                "description": func,
                "example": example,
            })

    return commands


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    print("Fetching ATCTrainer...")
    atc = build_atctrainer()
    print(f"  {len(atc)} commands")

    print("Fetching VICE...")
    vice = build_vice()
    print(f"  {len(vice)} commands")

    with open(OUT_DIR / "atctrainer-commands.json", "w", newline="\n") as f:
        json.dump(atc, f, indent=2)
    with open(OUT_DIR / "vice-commands.json", "w", newline="\n") as f:
        json.dump(vice, f, indent=2)

    print(f"\nWrote {OUT_DIR / 'atctrainer-commands.json'}")
    print(f"Wrote {OUT_DIR / 'vice-commands.json'}")

    print(f"\n=== ATCTrainer ({len(atc)} commands) ===")
    cur_cat = ""
    for c in atc:
        if c["category"] != cur_cat:
            cur_cat = c["category"]
            print(f"\n  [{cur_cat}]")
        aliases = ", ".join(c["aliases"]) if c["aliases"] else ""
        variant = f" (variant {c['variant']})" if "variant" in c else ""
        alias_str = f"  aliases: {aliases}" if aliases else ""
        print(f"    {c['primary']}{variant}{alias_str}")

    print(f"\n=== VICE ({len(vice)} keyboard commands) ===")
    for c in vice:
        print(f"    {c['primary']:8s} {c['raw_syntax']:30s}  ex: {c['example']}")


if __name__ == "__main__":
    main()
