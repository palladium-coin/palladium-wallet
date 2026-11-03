#!/usr/bin/env python3
"""Fail if Cobertura line/branch coverage of an assembly drops below a threshold.

usage: check-coverage.py COVERAGE.xml PACKAGE:MIN_LINE[:MIN_BRANCH] [...]
example: check-coverage.py coverage.cobertura.xml PalladiumWallet.Core:90:80
Prints a per-package summary either way. Stdlib only (runs on any CI runner)."""
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    root = ET.parse(sys.argv[1]).getroot()
    packages = {p.get("name"): p for p in root.iter("package")}
    failed = False
    for spec in sys.argv[2:]:
        name, *limits = spec.split(":")
        min_line = float(limits[0]) if limits else 0.0
        min_branch = float(limits[1]) if len(limits) > 1 else 0.0
        pkg = packages.get(name)
        if pkg is None:
            print(f"FAIL {name}: not present in the report")
            failed = True
            continue
        line = float(pkg.get("line-rate")) * 100
        branch = float(pkg.get("branch-rate")) * 100
        ok = line >= min_line and branch >= min_branch
        failed |= not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name}: lines {line:.1f}% (min {min_line:g}), "
              f"branches {branch:.1f}% (min {min_branch:g})")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
