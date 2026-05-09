"""Parse Airfleets World Fleet Listing PDFs into airline_icao -> {ICAO_type -> count} maps.

Airfleets PDFs are layout-driven multi-column tables. We use pdfplumber to extract
words with positions; pdfplumber collapses each airline's header row into a single
"line" of the form  "<NAME> <URL> Code : <codes...> Date : <year> Callsign : <call>".
Within each airline section we look for family headers and aircraft variant strings
(e.g. "737-8MAX", "320-271N") which we map to ICAO Doc 8643 type designators.

Run: uv run tools/parse_airfleets.py <pdf_path...> -o output.json
"""

# /// script
# requires-python = ">=3.13"
# dependencies = ["pdfplumber>=0.11"]
# ///

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Iterable

import pdfplumber


FAMILY_HEADERS = {
    "Airbus A220",
    "Airbus A300",
    "Airbus A310",
    "Airbus A318",
    "Airbus A319",
    "Airbus A320",
    "Airbus A321",
    "Airbus A330",
    "Airbus A340",
    "Airbus A350",
    "Airbus A380",
    "ATR 42/72",
    "BAe 146 / Avro RJ",
    "Beech 1900D",
    "Boeing 717",
    "Boeing 737",
    "Boeing 737 NG / Max",
    "Boeing 747",
    "Boeing 757",
    "Boeing 767",
    "Boeing 777",
    "Boeing 787",
    "Canadair Regional Jet",
    "Comac ARJ21",
    "Comac C919",
    "Concorde",
    "Dash 8",
    "Embraer 120 Brasilia",
    "Embraer 135/145",
    "Embraer 170/175",
    "Embraer 190/195",
    "Fokker 50",
    "Fokker 70/100",
    "Iliouchine Il-96",
    "Irkut MC-21",
    "Lockheed L-1011 TriStar",
    "McDonnell Douglas DC-10",
    "McDonnell Douglas MD-11",
    "McDonnell Douglas MD-80/90",
    "Mitsubishi SpaceJet",
    "Saab 2000",
    "Saab 340",
    "Sukhoi SuperJet 100",
}


# Recognize aircraft model-variant strings used in the Type column.
TYPE_PATTERNS = [
    re.compile(r"^7\d{2}-[\dA-Z]+$"),  # Boeing: 737-8MAX, 767-375ERBDSF, 777-333ER, 787-9
    re.compile(r"^[2-3]\d{2}-\d+[A-Z]*$"),  # Airbus: 320-214, 321-271NX, 330-343X, 220-371
    re.compile(r"^CRJ-?\d+[A-Z]*$"),  # CRJ-200LR
    re.compile(r"^17[05][A-Z]{2}$"),  # 170LR, 175LL, 175LR
    re.compile(r"^19[05][A-Z\d]*$"),  # 190E2, 195LR, 190AR
    re.compile(r"^MD-?\d{2}[A-Z]*$"),  # MD-80/88/90/11
    re.compile(r"^DC-?\d{1,2}[A-Z]*$"),  # DC-9, DC-10
    re.compile(r"^DHC-?[\dA-Z-]+$"),  # DHC-8 variants
    re.compile(r"^Q\d{3}$"),  # Q200/Q300/Q400
    re.compile(r"^RJ\d{2,3}$"),  # Avro RJ85/RJ100
    re.compile(r"^F-?\d{2,3}$"),  # Fokker 50/70/100
    re.compile(r"^L-1011[A-Z\d-]*$"),
    re.compile(r"^IL-?96[A-Z\d-]*$"),
    re.compile(r"^ARJ-?21\d*$"),
    re.compile(r"^C919\d*$"),
    re.compile(r"^MC-?21\d*$"),
    re.compile(r"^1900[CD]?$"),
    re.compile(r"^EMB-?\d{3}$"),
    re.compile(r"^ERJ-?\d{3}$"),
    re.compile(r"^SSJ-?\d{2,3}$"),
    re.compile(r"^SAAB-?\d+$", re.IGNORECASE),
    re.compile(r"^SF-?340$"),
    re.compile(r"^Concorde$", re.IGNORECASE),
]


@dataclass
class AirlineRecord:
    name: str
    country: str | None
    iata: str | None
    icao: str | None
    callsign: str | None
    raw_header: str
    families: set[str] = field(default_factory=set)
    types_raw: Counter = field(default_factory=Counter)


# ----- Code line parsing ----- #

# Format: "<name?> <url?> Code : <tokens...> Date : <year>? Callsign : <call>?"
# Tokens are space-separated; ICAO = 3 alpha; IATA = 2-3 alphanumeric (often with digit).

CODE_LINE_SPLIT = re.compile(r"\bCode\s*:\s*(.+)$", re.IGNORECASE)


def parse_header_line(line: str) -> dict:
    """Pull (name, country?, iata, icao, callsign, date) from a 'Code :' header line."""
    out: dict[str, str | None] = {
        "name": None,
        "country": None,
        "iata": None,
        "icao": None,
        "callsign": None,
        "date": None,
    }
    pre, _, after_code = line.partition("Code :")
    if not after_code:
        return out
    # Capture chunks separated by Date :, Callsign :
    parts: dict[str, str] = {"codes": "", "date": "", "callsign": ""}
    remaining = after_code.strip()
    if "Callsign :" in remaining:
        remaining, _, callsign = remaining.partition("Callsign :")
        parts["callsign"] = callsign.strip()
    if "Date :" in remaining:
        remaining, _, date = remaining.partition("Date :")
        parts["date"] = date.strip()
    parts["codes"] = remaining.strip()
    out["callsign"] = parts["callsign"] or None
    out["date"] = parts["date"] or None

    # Pull IATA + ICAO from token list. Strategy:
    #   - Any 3-char alpha = candidate ICAO (last one wins)
    #   - 2-char or 2-3 alphanumeric (with digit) = IATA candidate (first one wins)
    code_tokens = [t for t in re.split(r"\s+", parts["codes"]) if t]
    icaos = [t for t in code_tokens if len(t) == 3 and t.isalpha()]
    iata_candidates = [
        t for t in code_tokens
        if t not in icaos and re.match(r"^[A-Z0-9]{2,3}$", t)
    ]
    if icaos:
        out["icao"] = icaos[-1]
    if iata_candidates:
        out["iata"] = iata_candidates[0]

    # Name + country live in `pre` (everything before "Code :"). pre often looks like:
    #   "<airline name> <url>?"  e.g. "Air Canada http://www.aircanada.ca"
    #   or sometimes the country is also inside, e.g. "Canada Code :" (no name in line).
    name_part = pre.strip()
    # Strip trailing URL
    name_part = re.sub(r"\s*https?://\S+\s*$", "", name_part)
    out["name"] = name_part or None
    return out


# ----- Type detection ----- #


def is_type_variant(text: str) -> bool:
    text = text.strip().rstrip(",;")
    if not text or len(text) < 3 or len(text) > 30:
        return False
    if text in {"Reg.", "MSN", "Type", "Man.", "Year", "Date", "Code", "Callsign", "Ex"}:
        return False
    if text.isdigit():
        return False
    if not any(c.isdigit() for c in text):
        return False
    return any(p.match(text) for p in TYPE_PATTERNS)


# ----- Family header detection ----- #


def find_family_in_line(line_text: str) -> str | None:
    """A family header may appear inside a longer line because pdfplumber merges columns.
    Return the first family name found in the line, else None."""
    for fam in FAMILY_HEADERS:
        if fam in line_text:
            return fam
    return None


# ----- Word grouping by Y position ----- #


def group_words_into_lines(words: list[dict], y_tol: float = 3.0) -> list[list[dict]]:
    if not words:
        return []
    sorted_w = sorted(words, key=lambda w: (round(w["top"]), w["x0"]))
    lines: list[list[dict]] = []
    current: list[dict] = []
    current_top: float | None = None
    for w in sorted_w:
        if current_top is None or abs(w["top"] - current_top) <= y_tol:
            current.append(w)
            if current_top is None:
                current_top = w["top"]
        else:
            lines.append(sorted(current, key=lambda x: x["x0"]))
            current = [w]
            current_top = w["top"]
    if current:
        lines.append(sorted(current, key=lambda x: x["x0"]))
    return lines


# ----- Main parse ----- #


_KEYWORD_PREFIXES = ("Reg.", "MSN", "Type", "Man.", "Date", "Callsign", "Ex ", "Ex.", "http", "Plane e-List")

# Countries that appear as section headers in Airfleets PDFs. Comprehensive list
# so no country gets misidentified as an airline name. Order doesn't matter.
_COUNTRY_HINTS: frozenset[str] = frozenset({
    "Afghanistan", "Albania", "Algeria", "Andorra", "Angola", "Antigua and Barbuda",
    "Argentina", "Armenia", "Aruba", "Australia", "Austria", "Azerbaijan",
    "Bahamas", "Bahrain", "Bangladesh", "Barbados", "Belarus", "Belgium",
    "Belize", "Benin", "Bermuda", "Bhutan", "Bolivia", "Bosnia and Herzegovina",
    "Bosnia Herzegovina", "Botswana", "Brazil", "Brunei", "Bulgaria",
    "Burkina Faso", "Burma", "Burundi", "Cambodia", "Cameroon", "Canada",
    "Cape Verde", "Cayman Islands", "Central African Republic", "Chad", "Chile",
    "China", "Colombia", "Comoros", "Congo", "Congo (Brazzaville)",
    "Congo (Kinshasa)", "Congo, Democratic Republic of", "Cook Islands",
    "Costa Rica", "Croatia", "Cuba", "Curacao", "Cyprus", "Czech Republic",
    "Czechia", "Denmark", "Djibouti", "Dominica", "Dominican Republic",
    "East Timor", "Ecuador", "Egypt", "El Salvador", "Equatorial Guinea",
    "Eritrea", "Estonia", "Eswatini", "Ethiopia", "Faroe Islands", "Fiji",
    "Finland", "France", "French Polynesia", "Gabon", "Gambia", "Georgia",
    "Germany", "Ghana", "Gibraltar", "Greece", "Greenland", "Grenada",
    "Guatemala", "Guinea", "Guinea-Bissau", "Guyana", "Haiti", "Honduras",
    "Hong Kong", "Hungary", "Iceland", "India", "Indonesia", "Iran", "Iraq",
    "Ireland", "Isle of Man", "Israel", "Italy", "Ivory Coast", "Jamaica",
    "Japan", "Jordan", "Kazakhstan", "Kenya", "Kiribati", "Korea", "Kosovo",
    "Kuwait", "Kyrgyzstan", "Laos", "Latvia", "Lebanon", "Lesotho", "Liberia",
    "Libya", "Liechtenstein", "Lithuania", "Luxembourg", "Macao", "Macedonia",
    "Madagascar", "Malawi", "Malaysia", "Maldives", "Mali", "Malta",
    "Marshall Islands", "Mauritania", "Mauritius", "Mexico", "Micronesia",
    "Moldova", "Monaco", "Mongolia", "Montenegro", "Morocco", "Mozambique",
    "Myanmar", "Namibia", "Nauru", "Nepal", "Netherlands", "New Caledonia",
    "New Zealand", "Nicaragua", "Niger", "Nigeria", "North Korea",
    "North Macedonia", "Norway", "Oman", "Pakistan", "Palau", "Palestine",
    "Panama", "Papua New Guinea", "Paraguay", "Peru", "Philippines", "Poland",
    "Portugal", "Qatar", "Romania", "Russia", "Rwanda", "Saint Kitts and Nevis",
    "Saint Lucia", "Saint Vincent and the Grenadines", "Samoa", "San Marino",
    "Saudi Arabia", "Senegal", "Serbia", "Seychelles", "Sierra Leone",
    "Singapore", "Slovakia", "Slovenia", "Solomon Islands", "Somalia",
    "South Africa", "South Korea", "South Sudan", "Spain", "Sri Lanka",
    "Sudan", "Suriname", "Sweden", "Switzerland", "Syria", "Taiwan",
    "Tajikistan", "Tanzania", "Thailand", "Timor-Leste", "Togo", "Tonga",
    "Trinidad and Tobago", "Trinidad Tobago", "Tunisia", "Turkey",
    "Turkmenistan", "Tuvalu", "Uganda", "Ukraine", "United Arab Emirates",
    "United Kingdom", "United States", "Uruguay", "USA", "Uzbekistan",
    "Vanuatu", "Vatican City", "Venezuela", "Vietnam", "Yemen", "Zambia",
    "Zimbabwe",
})


def _looks_like_table_data(text: str) -> bool:
    """Detect lines that are clearly table content (registrations, MSNs, year sequences)."""
    # Sequence of N-numbers / C-regs / etc.
    if re.match(r"^[A-Z][A-Z0-9-]*(\s+[A-Z][A-Z0-9-]*)+$", text):
        return True
    # Pure year list
    if re.match(r"^(19|20)\d{2}(\s+(19|20)\d{2})*$", text):
        return True
    # Mostly digits / parens
    if re.match(r"^[\d\s\(\)]+$", text):
        return True
    return False


def parse_pdf(pdf_path: Path) -> list[AirlineRecord]:
    airlines: list[AirlineRecord] = []
    current_airline: AirlineRecord | None = None
    last_country: str | None = None
    pending_name: str | None = None

    with pdfplumber.open(pdf_path) as pdf:
        for page in pdf.pages:
            # Reset pending name at page boundary to avoid bleeding through page footers
            words = page.extract_words(use_text_flow=True, keep_blank_chars=False)
            lines = group_words_into_lines(words)
            for line_words in lines:
                tokens = [w["text"] for w in line_words]
                line_text = " ".join(tokens).strip()
                if not line_text or line_text.startswith("Plane e-List"):
                    continue

                # Track country (line is exactly a known country name).
                if line_text in _COUNTRY_HINTS:
                    last_country = line_text
                    continue

                # Code-line: airline boundary.
                if "Code :" in line_text:
                    parsed = parse_header_line(line_text)
                    name = parsed["name"] or pending_name or "(unknown)"
                    current_airline = AirlineRecord(
                        name=name,
                        country=last_country,
                        iata=parsed["iata"],
                        icao=parsed["icao"],
                        callsign=parsed["callsign"],
                        raw_header=line_text,
                    )
                    airlines.append(current_airline)
                    pending_name = None
                    continue

                # Family header — append to current airline.
                fam = find_family_in_line(line_text)
                if fam and current_airline:
                    current_airline.families.add(fam)
                    # Family headers don't reset pending_name (we only need it before Code lines)

                # Type variants on this line — check each token.
                # Special-case: DHC-8 entries split as ("DHC-8", "102"|"103"|"301"|"401"...)
                if current_airline:
                    cleaned_tokens = [t.strip(",;()[]") for t in tokens]
                    i = 0
                    while i < len(cleaned_tokens):
                        tok = cleaned_tokens[i]
                        # Two-token variant: DHC-8 NNN
                        if tok == "DHC-8" and i + 1 < len(cleaned_tokens):
                            nxt = cleaned_tokens[i + 1]
                            if re.match(r"^[1-4]\d{2}$", nxt):
                                current_airline.types_raw[f"DHC-8 {nxt}"] += 1
                                i += 2
                                continue
                        if is_type_variant(tok):
                            current_airline.types_raw[tok] += 1
                        i += 1

                # Candidate airline name: short non-keyword line that isn't table data.
                if (
                    not fam
                    and "Code :" not in line_text
                    and len(line_text) <= 80
                    and not any(line_text.startswith(p) for p in _KEYWORD_PREFIXES)
                    and not _looks_like_table_data(line_text)
                ):
                    # Strip trailing URL if pdfplumber merged it onto the name line
                    cleaned_name = re.sub(r"\s*https?://\S+\s*$", "", line_text).strip()
                    if cleaned_name:
                        pending_name = cleaned_name

    return airlines


# ----- Mapping raw model variants to ICAO Doc 8643 ----- #


def map_to_icao_type(variant: str, families: set[str]) -> str | None:
    """Map an Airfleets 'Type' string to an ICAO Doc 8643 designator using the
    family-set of the owning airline as a hint when needed.

    Returns None if the variant can't be confidently mapped (caller handles it).
    """
    v = variant.upper()

    # Boeing 737 family
    if v.endswith("MAX") or "MAX" in v:
        if v.startswith("737-7"):
            return "B37M"
        if v.startswith("737-8"):
            return "B38M"
        if v.startswith("737-9"):
            return "B39M"
        if v.startswith("737-10"):
            return "B3XM"
    m = re.match(r"^737-([2-9])(\d?)", v)
    if m:
        return {"2": "B732", "3": "B733", "4": "B734", "5": "B735", "6": "B736", "7": "B737", "8": "B738", "9": "B739"}[m.group(1)]
    # Boeing 747
    if v.startswith("747-SP"):
        return "B74S"
    if v.startswith("747-2"):
        return "B742"
    if v.startswith("747-3"):
        return "B743"
    if v.startswith("747-4"):
        return "B744"
    if v.startswith("747-8"):
        return "B748"
    # Boeing 757
    if v.startswith("757-2"):
        return "B752"
    if v.startswith("757-3"):
        return "B753"
    # Boeing 767
    if v.startswith("767-2"):
        return "B762"
    if v.startswith("767-3"):
        return "B763"
    if v.startswith("767-4"):
        return "B764"
    # Boeing 777 — distinguish 777-200/200ER (B772), 777-200LR/F (B77L), 777-300 (B773), 777-300ER (B77W), 777-9 (B779)
    if v.startswith("777-F"):
        # 777F (freighter, e.g. 777-FS2, 777-FZN, 777-F1B) — ICAO designator B77L (shared with 777-200LR base)
        return "B77L"
    if v.startswith("777-2"):
        if "LR" in v or "F" in v:
            return "B77L"
        return "B772"
    if v.startswith("777-3"):
        if "ER" in v:
            return "B77W"
        return "B773"
    if v.startswith("777-9"):
        return "B779"
    # Boeing 787
    if v.startswith("787-8"):
        return "B788"
    if v.startswith("787-9"):
        return "B789"
    if v.startswith("787-10"):
        return "B78X"
    # Boeing 717
    if v.startswith("717-"):
        return "B712"

    # Airbus A220 (Bombardier CSeries)
    if v.startswith("220-1") or v.startswith("BD-500-1A10") or v.startswith("CS100"):
        return "BCS1"
    if v.startswith("220-3") or v.startswith("BD-500-1A11") or v.startswith("CS300"):
        return "BCS3"
    # Airbus A300/A310
    if v.startswith("300-"):
        return "A306"
    if v.startswith("310-"):
        return "A310"
    # Airbus A318/A319/A320/A321
    if v.startswith("318-"):
        return "A318"
    if v.startswith("319-"):
        return "A19N" if "N" in v else "A319"
    if v.startswith("320-"):
        return "A20N" if "N" in v else "A320"
    if v.startswith("321-"):
        return "A21N" if "N" in v else "A321"
    # Airbus A330
    if v.startswith("330-743L"):
        return "A337"  # A330-700L Beluga XL
    if v.startswith("330-2"):
        return "A332"
    if v.startswith("330-3"):
        return "A333"
    if v.startswith("330-8"):
        return "A338"
    if v.startswith("330-9"):
        return "A339"
    # Airbus A340
    if v.startswith("340-2"):
        return "A342"
    if v.startswith("340-3"):
        return "A343"
    if v.startswith("340-5"):
        return "A345"
    if v.startswith("340-6"):
        return "A346"
    # Airbus A350
    if v.startswith("350-9"):
        return "A359"
    if v.startswith("350-10"):
        return "A35K"
    # Airbus A380
    if v.startswith("380-"):
        return "A388"

    # Embraer 170/175
    if v.startswith("170") and ("Embraer 170/175" in families):
        return "E170"
    if v.startswith("175"):
        if "LR" in v or "LL" in v:
            return "E75L"
        return "E75S"
    # Embraer 190/195
    if v.startswith("190"):
        if "E2" in v:
            return "E290"
        return "E190"
    if v.startswith("195"):
        if "E2" in v:
            return "E295"
        return "E195"
    # ERJ 135/145
    if v.startswith("135") or v.startswith("ERJ-135"):
        return "E135"
    if v.startswith("145") or v.startswith("ERJ-145"):
        return "E145"
    if v.startswith("EMB-120") or v == "EMB120":
        return "E120"

    # CRJ
    crj = re.match(r"^CRJ-?(\d+)", v)
    if crj:
        n = crj.group(1)
        if n.startswith("100"):
            return "CRJ1"
        if n.startswith("200"):
            return "CRJ2"
        if n.startswith("550") or n.startswith("700") or n.startswith("701") or n.startswith("702"):
            return "CRJ7"
        if n.startswith("900"):
            return "CRJ9"
        if n.startswith("1000"):
            return "CRJX"

    # MD-80/90
    if v.startswith("MD-80") or v == "MD80":
        return "MD82"
    if v.startswith("MD-81"):
        return "MD81"
    if v.startswith("MD-82"):
        return "MD82"
    if v.startswith("MD-83"):
        return "MD83"
    if v.startswith("MD-87"):
        return "MD87"
    if v.startswith("MD-88"):
        return "MD88"
    if v.startswith("MD-90"):
        return "MD90"
    if v.startswith("MD-11"):
        return "MD11"

    # DC
    if v.startswith("DC-9") or v.startswith("DC9"):
        return "DC9"
    if v.startswith("DC-10") or v.startswith("DC10"):
        return "DC10"

    # Dash 8 / Q400 — handle "DHC-8 NNN" two-token form (variant captured during parse)
    m = re.match(r"^DHC-8[\s-]*(\d{3})", v)
    if m:
        n = int(m.group(1))
        if 100 <= n < 200:
            return "DH8A"
        if 200 <= n < 300:
            return "DH8B"
        if 300 <= n < 400:
            return "DH8C"
        if 400 <= n < 500:
            return "DH8D"
    if v == "Q400":
        return "DH8D"
    if v == "Q300":
        return "DH8C"
    if v in {"Q200", "Q100"}:
        return "DH8A"

    # Avro RJ / BAe 146
    if v in {"RJ70"}:
        return "RJ70"
    if v in {"RJ85"}:
        return "RJ85"
    if v == "RJ100":
        return "RJ1H"
    if v.startswith("BAE-146-1") or v.startswith("146-1"):
        return "B461"
    if v.startswith("BAE-146-2") or v.startswith("146-2"):
        return "B462"
    if v.startswith("BAE-146-3") or v.startswith("146-3"):
        return "B463"

    # Fokker
    if v.startswith("F-50") or v == "F50" or v == "50":
        return "F50"
    if v.startswith("F-70") or v == "F70":
        return "F70"
    if v.startswith("F-100") or v == "F100":
        return "F100"

    # Saab
    if v.startswith("SAAB-340") or v == "SF340":
        return "SF34"
    if v.startswith("SAAB-2000") or v == "SAAB2000":
        return "SB20"

    # SSJ
    if v.startswith("SSJ"):
        return "SU95"

    # Beech 1900
    if v in {"1900D", "1900C"}:
        return "B190"

    # Concorde
    if v.upper() == "CONCORDE":
        return "CONC"

    # Comac, Irkut, Il-96
    if v.startswith("ARJ-21") or v == "ARJ21":
        return "AJ27"
    if v.startswith("C919"):
        return "C919"
    if v.startswith("MC-21"):
        return "MC21"
    if v.startswith("IL-96") or v.startswith("IL96"):
        return "IL96"

    return None


# ----- Output ----- #


def airline_to_icao_typemap(record: AirlineRecord) -> dict[str, int]:
    out: Counter = Counter()
    for variant, count in record.types_raw.items():
        mapped = map_to_icao_type(variant, record.families)
        if mapped:
            out[mapped] += count
    return dict(out.most_common())


def write_outputs(airlines: list[AirlineRecord], out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)

    raw_data = []
    for a in airlines:
        raw_data.append({
            "name": a.name,
            "country": a.country,
            "iata": a.iata,
            "icao": a.icao,
            "callsign": a.callsign,
            "raw_header": a.raw_header,
            "families": sorted(a.families),
            "types_raw": dict(a.types_raw.most_common()),
            "types_icao": airline_to_icao_typemap(a),
        })
    (out_dir / "airfleets-parsed.json").write_text(json.dumps(raw_data, indent=2))

    # Compact ICAO -> {type: count} map (for airlines with an ICAO code)
    compact = {}
    for a in airlines:
        if a.icao:
            compact[a.icao] = {
                "name": a.name,
                "callsign": a.callsign,
                "types": airline_to_icao_typemap(a),
            }
    (out_dir / "airfleets-by-icao.json").write_text(json.dumps(compact, indent=2))

    # Unmapped variants (for diagnostics)
    unmapped: Counter = Counter()
    for a in airlines:
        for variant, count in a.types_raw.items():
            if not map_to_icao_type(variant, a.families):
                unmapped[variant] += count
    (out_dir / "airfleets-unmapped-variants.txt").write_text(
        "\n".join(f"{c:>5}  {v}" for v, c in unmapped.most_common(200))
    )


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("pdfs", nargs="+", type=Path)
    ap.add_argument("-o", "--out-dir", type=Path, default=Path(".tmp"))
    args = ap.parse_args()

    all_airlines: list[AirlineRecord] = []
    for pdf in args.pdfs:
        if not pdf.exists():
            print(f"ERROR: {pdf} not found", file=sys.stderr)
            return 1
        print(f"Parsing {pdf} ...", file=sys.stderr)
        airlines = parse_pdf(pdf)
        with_icao = sum(1 for a in airlines if a.icao)
        print(f"  found {len(airlines)} airlines, {with_icao} with ICAO", file=sys.stderr)
        all_airlines.extend(airlines)

    write_outputs(all_airlines, args.out_dir)
    print(f"Wrote {len(all_airlines)} airline records to {args.out_dir}/", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
