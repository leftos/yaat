#!/usr/bin/env bash
# Collects everything the open-issue triage needs into .tmp/issue-triage/ so the
# verdicts come from files, not from a dozen exploratory gh/git calls.
#
# Usage: bash .claude/skills/triage-open-issues/scripts/gather.sh   (from the yaat checkout)
set -euo pipefail

repo="leftos/yaat"
root="$(git rev-parse --show-toplevel)"
server="$(dirname "$root")/yaat-server"
out="$root/.tmp/issue-triage"
mkdir -p "$out"

tag="$(git -C "$root" describe --tags --abbrev=0)"
tag_date="$(git -C "$root" log -1 --format=%cs "$tag")"
{
  echo "tag: $tag ($tag_date)"
  echo "yaat commits since tag: $(git -C "$root" rev-list --count "$tag..HEAD")"
  if [ -d "$server/.git" ]; then
    echo "yaat-server commits since $tag_date: $(git -C "$server" rev-list --count --since="$tag_date" HEAD)"
  fi
} >"$out/window.txt"

gh issue list --repo "$repo" --state open --limit 200 \
  --json number,title,createdAt,labels,closedByPullRequestsReferences \
  --jq '.[] | "\(.number)\t\(.createdAt[:10])\t\([.labels[].name] | join(","))\tPR:\([.closedByPullRequestsReferences[].number] | map(tostring) | join(","))\t\(.title)"' \
  >"$out/issues.tsv"

gh pr list --repo "$repo" --state open --json number,title,headRefName,isDraft \
  --jq '.[] | "\(.number)\tdraft=\(.isDraft)\t\(.headRefName)\t\(.title)"' \
  >"$out/open-prs.tsv"

mkdir -p "$out/bodies"
while IFS=$'\t' read -r n _; do
  gh issue view "$n" --repo "$repo" --json body,comments \
    --jq '.body, "", "## Comments", (.comments[] | "**\(.author.login)** (\(.createdAt[:10])):\n\(.body)\n")' \
    >"$out/bodies/$n.md"
done <"$out/issues.tsv"

{
  echo "# issue numbers cited by commits since $tag (yaat)"
  git -C "$root" log "$tag..HEAD" --format='%h %s%n%b' | grep -oE '#[0-9]+' | sort | uniq -c
  echo "# issue numbers cited by yaat-server commits since $tag_date"
  if [ -d "$server/.git" ]; then
    git -C "$server" log --since="$tag_date" --format='%h %s%n%b' | grep -oE 'issues/[0-9]+|#[0-9]+' | sort | uniq -c
  fi
  echo "# issue numbers cited in CHANGELOG.md Unreleased"
  awk '/^## Unreleased/{p=1;next} /^## /{if(p)exit} p' "$root/CHANGELOG.md" | grep -oE '#[0-9]+' | sort | uniq -c
} >"$out/refs.txt" || true

awk '/^## Unreleased/{p=1;next} /^## /{if(p)exit} p' "$root/CHANGELOG.md" >"$out/changelog-unreleased.md"

{
  echo "# plan files citing an open issue number"
  while IFS=$'\t' read -r n _; do
    grep -rnE "#${n}\b|issues/${n}\b" "$root/docs/plans" "$server/docs/plans" 2>/dev/null | sed "s|^|#$n: |" || true
  done <"$out/issues.tsv"
  echo "# open plan checkboxes naming a class the issue body names (rule 1: does a subplan already cover the fix site?)"
  while IFS=$'\t' read -r n _; do
    { grep -oE '[A-Z][A-Za-z0-9.]+\.cs' "$out/bodies/$n.md" || true; } | sed 's/\.cs$//' | sort -u | while read -r cls; do
      grep -rnE "^\s*- \[ \].*(^|[^A-Za-z0-9_])${cls}([^A-Za-z0-9_]|$)" "$root/docs/plans" "$server/docs/plans" 2>/dev/null | cut -c1-200 | sed "s|^|#$n $cls: |" || true
    done
  done <"$out/issues.tsv"
} >"$out/plan-refs.txt"

{
  echo "# files each issue body names, with the commits since the tag that touched them"
  while IFS=$'\t' read -r n _; do
    echo "## #$n"
    { grep -oE '(src|tests|tools)/[A-Za-z0-9_./-]+\.(cs|axaml|py|md)' "$out/bodies/$n.md" || true; } | sort -u | while read -r f; do
      count="$(git -C "$root" rev-list --count "$tag..HEAD" -- "$f" 2>/dev/null || echo 0)"
      echo "$count  $f"
    done
  done <"$out/issues.tsv"
} >"$out/touched-files.txt"

echo "wrote $out:"
ls "$out"
cat "$out/window.txt"
