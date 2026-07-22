# /// script
# requires-python = ">=3.13"
# dependencies = ["beautifulsoup4>=4.12", "markdownify>=0.13"]
# ///
"""Mirror the vNAS documentation site (CRC controller manual + vStrips + vTDLS + Data Admin).

The upstream docs moved to a Material for MkDocs site at https://docs.virtualnas.net/.
That site serves rendered HTML only -- there is no raw-markdown endpoint and the source
repo is private -- so this tool fetches each page, extracts the article body, converts it
back to clean Markdown, downloads every referenced image, and rewrites both image and
cross-page links to local relative paths.

Run with uv (no manual pip install needed):

    uv run tools/refresh-crc-docs.py                 # refresh everything
    uv run tools/refresh-crc-docs.py --section crc   # just the CRC manual
    uv run tools/refresh-crc-docs.py --list          # print the page plan and exit

See docs/crc/README.md for the source/refresh policy this tool implements.
"""

from __future__ import annotations

import argparse
import os.path
import re
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urljoin, urlsplit

from bs4 import BeautifulSoup, NavigableString, Tag
from markdownify import markdownify

SITE = "https://docs.virtualnas.net/"
USER_AGENT = "yaat-refresh-crc-docs/1.0 (+https://github.com/leftos/yaat)"
REPO_ROOT = Path(__file__).resolve().parent.parent

# Emoji prefixes give each Material admonition type a visible cue once flattened to a
# GitHub blockquote (GitHub does not render MkDocs `!!!` admonition syntax).
ADMONITION_EMOJI = {
    "info": "ℹ️",
    "note": "📝",
    "abstract": "📄",
    "tip": "💡",
    "success": "✅",
    "question": "❓",
    "warning": "⚠️",
    "failure": "❌",
    "danger": "🛑",
    "bug": "🐞",
    "example": "📌",
    "quote": "💬",
}
DEFAULT_ADMONITION_LABELS = {t.capitalize() for t in ADMONITION_EMOJI} | {"Info", "Warning", "Note"}


@dataclass
class Section:
    """A contiguous block of the upstream site mirrored into one local directory."""

    name: str
    base: str  # absolute URL prefix, e.g. "https://docs.virtualnas.net/crc/"
    out_dir: Path  # repo-relative output directory for this section's markdown + img/
    pages: list[tuple[str, str]]  # (url slug, local markdown stem)

    @property
    def base_path(self) -> str:
        return urlsplit(self.base).path  # e.g. "/crc/"


SECTIONS: list[Section] = [
    Section(
        name="crc",
        base=urljoin(SITE, "crc/"),
        out_dir=REPO_ROOT / "docs" / "crc",
        pages=[
            ("overview", "overview"),
            ("tower-cab", "tower-cab"),
            ("asdex", "asdex"),
            ("said/saab", "said-saab"),
            ("stars", "stars"),
            ("eram", "eram"),
            ("troubleshooting", "troubleshooting"),
        ],
    ),
    # vStrips is its own top-level section upstream, but YAAT keeps the controller manual
    # at docs/crc/vstrips.md (referenced by COMMANDS.md + docs/flight-strips.md), and its
    # images share the docs/crc/img/ namespace exactly as the old mirror did.
    Section(
        name="vstrips",
        base=urljoin(SITE, "vstrips/"),
        out_dir=REPO_ROOT / "docs" / "crc",
        pages=[("", "vstrips")],
    ),
    # vTDLS used to live on its own Docsify site (tdls.virtualnas.net/docs/) and was mirrored
    # by hand. Upstream moved it onto this Material site, so it is now an ordinary one-page
    # section like vstrips, and docs/vtdls/ is generated rather than copied verbatim.
    Section(
        name="vtdls",
        base=urljoin(SITE, "vtdls/"),
        out_dir=REPO_ROOT / "docs" / "vtdls",
        pages=[("", "vtdls")],
    ),
    Section(
        name="data-admin",
        base=urljoin(SITE, "data-admin/"),
        out_dir=REPO_ROOT / "docs" / "vnas-data-admin",
        pages=[
            ("overview", "overview"),
            ("aliases", "aliases"),
            ("auto-atc", "auto-atc"),
            ("common-urls", "common-urls"),
            ("controller-feed", "controller-feed"),
            ("facilities", "facilities"),
            ("foreign-facilities", "foreign-facilities"),
            ("restrictions", "restrictions"),
            ("training", "training"),
            ("transceivers", "transceivers"),
            ("user-management", "user-management"),
            ("video-maps", "video-maps"),
        ],
    ),
]


@dataclass
class PagePlan:
    section: Section
    slug: str
    stem: str
    url: str
    out_md: Path


def build_plans(sections: list[Section]) -> list[PagePlan]:
    plans: list[PagePlan] = []
    for section in sections:
        for slug, stem in section.pages:
            url = section.base if slug == "" else urljoin(section.base, f"{slug}/")
            plans.append(
                PagePlan(
                    section=section,
                    slug=slug,
                    stem=stem,
                    url=url,
                    out_md=section.out_dir / f"{stem}.md",
                )
            )
    return plans


def page_index(plans: list[PagePlan]) -> dict[str, Path]:
    """Map normalized upstream URL path -> local markdown path, for cross-link rewriting."""
    return {urlsplit(p.url).path.rstrip("/"): p.out_md for p in plans}


def fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:  # noqa: S310 (trusted host)
        return response.read()


def transform_admonitions(article: Tag, soup: BeautifulSoup) -> None:
    """Flatten Material admonitions to emoji-prefixed blockquotes (GitHub-renderable)."""
    for div in article.select("div.admonition"):
        classes = [c for c in (div.get("class") or []) if c != "admonition"]
        adm_type = classes[0] if classes else "note"
        emoji = ADMONITION_EMOJI.get(adm_type, "ℹ️")

        title_el = div.find("p", class_="admonition-title")
        if title_el is not None:
            title_text = title_el.get_text(strip=True)
            title_el.insert(0, NavigableString(f"{emoji} "))
            if title_text in DEFAULT_ADMONITION_LABELS:
                # Generic "Info"/"Warning"/etc. header -> bold it so the type still reads.
                strong = soup.new_tag("strong")
                for child in list(title_el.children):
                    strong.append(child.extract())
                title_el.append(strong)
            del title_el["class"]

        blockquote = soup.new_tag("blockquote")
        for child in list(div.children):
            blockquote.append(child.extract())
        div.replace_with(blockquote)


def transform_figures(article: Tag, soup: BeautifulSoup) -> None:
    """Replace <figure><img><figcaption> with the image followed by an italic caption."""
    for figure in article.find_all("figure"):
        img = figure.find("img")
        caption_el = figure.find("figcaption")
        replacement: list[Tag] = []
        if img is not None:
            new_p = soup.new_tag("p")
            new_p.append(img.extract())
            replacement.append(new_p)
        if caption_el is not None:
            caption = re.sub(r"^\s*Fig\.\s*\d+\s*-\s*", "", caption_el.get_text(" ", strip=True))
            caption = re.sub(r"\s+", " ", caption).strip()
            if caption:
                cap_p = soup.new_tag("p")
                em = soup.new_tag("em")
                em.string = caption
                cap_p.append(em)
                replacement.append(cap_p)
        if replacement:
            figure.replace_with(*replacement)
        else:
            figure.decompose()


@dataclass
class ImageJob:
    abs_url: str
    local_path: Path


def collect_images(article: Tag, plan: PagePlan) -> list[ImageJob]:
    """Rewrite each in-article <img src> to a local relative path and return download jobs."""
    jobs: list[ImageJob] = []
    base_path = plan.section.base_path
    for img in article.find_all("img"):
        src = img.get("src")
        if not src:
            continue
        abs_url = urljoin(plan.url, src)
        path = urlsplit(abs_url).path
        if not path.startswith(base_path):
            # Off-section asset (logo, etc.) -- drop the class but leave the reference.
            img.attrs.pop("class", None)
            continue
        rel = path[len(base_path) :].lstrip("/")  # e.g. "img/overview/profiles.png"
        img["src"] = rel
        img.attrs.pop("class", None)
        img.attrs.pop("loading", None)
        jobs.append(ImageJob(abs_url=abs_url, local_path=plan.section.out_dir / rel))
    return jobs


def rewrite_links(article: Tag, plan: PagePlan, index: dict[str, Path]) -> None:
    """Point cross-page links at the local mirror; leave anchors and external links alone."""
    for anchor in article.find_all("a"):
        href = anchor.get("href")
        if not href or href.startswith(("#", "mailto:")):
            continue
        split = urlsplit(urljoin(plan.url, href))
        if split.scheme not in ("http", "https") or split.netloc != urlsplit(SITE).netloc:
            continue  # external link -- keep as-is
        target = index.get(split.path.rstrip("/"))
        if target is None:
            continue
        rel_str = Path(os.path.relpath(target, plan.out_md.parent)).as_posix()
        anchor["href"] = rel_str + (f"#{split.fragment}" if split.fragment else "")


def clean_article(article: Tag) -> None:
    for selector in ("a.headerlink", "button", "nav", "script", "style", ".md-clipboard"):
        for tag in article.select(selector):
            tag.decompose()


def html_to_markdown(html: str) -> str:
    markdown = markdownify(
        html,
        heading_style="ATX",
        bullets="-",
        escape_asterisks=False,
        escape_underscores=False,
        # Reference tables use inline icons in cells (data-block fields, target symbols,
        # NEXRAD legend); without this markdownify drops them to alt text only.
        keep_inline_images_in=["td", "th"],
    )
    markdown = re.sub(r"\n{3,}", "\n\n", markdown)
    return markdown.strip() + "\n"


def process_page(plan: PagePlan, index: dict[str, Path], *, dry_run: bool) -> tuple[int, int]:
    html = fetch(plan.url).decode("utf-8")
    soup = BeautifulSoup(html, "html.parser")
    article = soup.select_one("article.md-content__inner")
    if article is None:
        raise RuntimeError(f"no <article class='md-content__inner'> found at {plan.url}")

    clean_article(article)
    transform_admonitions(article, soup)
    transform_figures(article, soup)
    rewrite_links(article, plan, index)
    image_jobs = collect_images(article, plan)

    markdown = html_to_markdown(str(article))

    downloaded = 0
    if not dry_run:
        plan.out_md.parent.mkdir(parents=True, exist_ok=True)
        plan.out_md.write_text(markdown, encoding="utf-8", newline="\n")
        seen: set[Path] = set()
        for job in image_jobs:
            if job.local_path in seen:
                continue
            seen.add(job.local_path)
            job.local_path.parent.mkdir(parents=True, exist_ok=True)
            job.local_path.write_bytes(fetch(job.abs_url))
            downloaded += 1

    return len(markdown.splitlines()), downloaded


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--section", choices=[s.name for s in SECTIONS], help="only refresh one section")
    parser.add_argument("--only", help="only refresh the page with this local stem (e.g. 'asdex')")
    parser.add_argument("--list", action="store_true", help="print the page plan and exit")
    parser.add_argument("--dry-run", action="store_true", help="convert but do not write files or download images")
    args = parser.parse_args(argv)

    sections = [s for s in SECTIONS if args.section is None or s.name == args.section]
    plans = build_plans(sections)
    if args.only:
        plans = [p for p in plans if p.stem == args.only]
    index = page_index(build_plans(SECTIONS))

    if args.list:
        for plan in plans:
            print(f"{plan.url:60s} -> {plan.out_md.relative_to(REPO_ROOT).as_posix()}")
        return 0

    failures = 0
    for plan in plans:
        try:
            lines, images = process_page(plan, index, dry_run=args.dry_run)
            rel = plan.out_md.relative_to(REPO_ROOT).as_posix()
            verb = "would write" if args.dry_run else "wrote"
            print(f"  {verb} {rel:40s} ({lines:4d} lines, {images} images)")
        except Exception as exc:  # noqa: BLE001 -- report per-page and continue
            failures += 1
            print(f"  FAILED {plan.url}: {exc}", file=sys.stderr)

    if failures:
        print(f"\n{failures} page(s) failed", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
