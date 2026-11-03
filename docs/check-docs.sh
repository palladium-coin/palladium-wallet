#!/usr/bin/env bash
# Mechanical checks for docs/ (see docs/docs-as-code.md). Run from anywhere.
set -u
cd "$(dirname "$0")/.." || exit 2
fail=0
err() { echo "FAIL: $*"; fail=1; }

adr_dir=docs/adr
prev=0
for f in $(ls $adr_dir/[0-9][0-9][0-9][0-9]-*.md 2>/dev/null | sort); do
  base=$(basename "$f"); num=${base:0:4}
  n=$((10#$num))
  [ "$n" -eq $((prev + 1)) ] || err "$base: numbering gap or duplicate (previous $prev)"
  prev=$n
  head -1 "$f" | grep -q "^# $num\. " || err "$base: first line must be '# $num. <title>'"
  grep -qE '^- \*\*Status:\*\* ' "$f" || err "$base: missing Status line"
  for s in Context Decision Consequences; do
    grep -q "^## $s\$" "$f" || err "$base: missing '## $s' section"
  done
  grep -q "($base)" $adr_dir/README.md || err "$base: not listed in $adr_dir/README.md"
done
[ "$prev" -gt 0 ] || err "no ADRs found"

# Index rows must point to existing files.
grep -oE '\]\([0-9]{4}-[^)]+\.md\)' $adr_dir/README.md | tr -d '])(' | while read -r l; do
  [ -f "$adr_dir/$l" ] || { echo "FAIL: index links to missing $l"; exit 1; }
done || fail=1

# Repo paths cited in backticks must exist.
while IFS= read -r line; do
  file=${line%%:*}; p=${line#*:}
  [ -e "$p" ] || err "$file: cited path does not exist: $p"
done < <(grep -oE '`(src|tests|docker|docs)/[A-Za-z0-9_./-]+`|`(README|USERGUIDE|SECURITY|CHANGELOG|CLAUDE|AGENTS)\.md`|`update-version\.sh`|`\.gitignore`' -r docs --include='*.md' \
         | grep -v 'template.md' | sed -E 's/`//g' | grep -v 'docs/adr/NNNN' | grep -v 'path/to')

# Relative markdown links in docs/ must resolve.
for f in $(find docs -name '*.md'); do
  d=$(dirname "$f")
  for l in $(grep -oE '\]\([^)#:]+\.md\)' "$f" | sed -E "s/^\]\(|\)\$//g" | grep -v NNNN); do
    [ -e "$d/$l" ] || err "$f: broken link $l"
  done
done

# CLAUDE.md and AGENTS.md must match apart from the header block (before '## Role').
if ! diff <(sed -n '/^## Role/,$p' CLAUDE.md) <(sed -n '/^## Role/,$p' AGENTS.md) >/dev/null; then
  err "CLAUDE.md and AGENTS.md differ after the header block"
fi

[ "$fail" -eq 0 ] && echo "docs OK ($prev ADRs)"
exit "$fail"
