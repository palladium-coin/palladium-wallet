#!/usr/bin/env bash
# Coverage-guided fuzzing with afl++ + SharpFuzz.
#
# One-time setup:
#   apt install afl++                                (or build from source)
#   dotnet tool install --global SharpFuzz.CommandLine
#
# Usage:
#   ./fuzz.sh <target> [afl-fuzz args...]
#   ./fuzz.sh header
#   ./fuzz.sh peers -V 3600          # time-limited campaign (seconds)
#
# Targets: header merkle slip132 bip39 address coinamount walletdoc encfile peers headerrange cosignerkey
#
# Findings land in findings/<target>/crashes/: replay one with
#   dotnet run -- <target> findings/<target>/crashes/<file>
# then add it to the seed corpus in Program.cs as a regression input.
set -euo pipefail
cd "$(dirname "$0")"

TARGET="${1:?usage: ./fuzz.sh <target> [afl-fuzz args...]}"
shift || true

command -v afl-fuzz >/dev/null || { echo "afl-fuzz not found: apt install afl++"; exit 1; }
command -v sharpfuzz >/dev/null || { echo "sharpfuzz not found: dotnet tool install --global SharpFuzz.CommandLine"; exit 1; }

dotnet build -c Release
BIN="bin/Release/net10.0"

# Instrument the assemblies under test (idempotent: SharpFuzz skips already-
# instrumented DLLs). Core is the real target; NBitcoin gives the fuzzer
# visibility into the library our parsers delegate to.
sharpfuzz "$BIN/PalladiumWallet.Core.dll"
sharpfuzz "$BIN/NBitcoin.dll" || true

mkdir -p "findings/$TARGET"
afl-fuzz -i "Corpus/$TARGET" -o "findings/$TARGET" -t 5000 "$@" \
    -- dotnet "$BIN/PalladiumWallet.Fuzz.dll" "$TARGET" --afl
