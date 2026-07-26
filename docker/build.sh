#!/usr/bin/env bash
# Reproducible build script for PalladiumWallet using Docker.
# Run from anywhere; paths are resolved relative to this script.
set -euo pipefail

# ── Paths ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DIST_DIR="${PROJECT_ROOT}/dist"

# ── Docker image names ────────────────────────────────────────────────────────
IMAGE_DESKTOP="plm-build-desktop"
IMAGE_ANDROID="plm-build-android"

# Named volume for NuGet package cache (shared across all builds, survives reboots).
NUGET_VOLUME="plm-nuget-cache"

# ── Helpers ───────────────────────────────────────────────────────────────────
bold()  { printf '\033[1m%s\033[0m\n' "$*"; }
info()  { printf '\033[1;34m>>>\033[0m %s\n' "$*"; }
ok()    { printf '\033[1;32m✓\033[0m  %s\n' "$*"; }
err()   { printf '\033[1;31m✗\033[0m  %s\n' "$*" >&2; }
die()   { err "$*"; exit 1; }

usage() {
    cat <<EOF
$(bold "Usage:") $(basename "$0") [TARGET] [OPTIONS]

$(bold "Targets:")
  windows     Win x64 single-file executable  →  dist/windows/
  linux       Linux x64 single-file binary    →  dist/linux/
  linux-arm64 Linux ARM64 single-file binary  →  dist/linux-arm64/
  android     Android APK (release-signed)    →  dist/android/
  all         All targets above

$(bold "Options:")
  --rebuild   Force rebuild of Docker images (e.g. after Dockerfile change)
  -h, --help  Show this help

$(bold "Examples:")
  $(basename "$0")               # interactive menu
  $(basename "$0") all           # build everything
  $(basename "$0") windows       # Windows only
  $(basename "$0") android --rebuild
EOF
}

# ── Argument parsing ──────────────────────────────────────────────────────────
REBUILD=false
TARGET=""

for arg in "$@"; do
    case "$arg" in
        windows|linux|linux-arm64|android|all) TARGET="$arg" ;;
        --rebuild)                             REBUILD=true ;;
        -h|--help)                              usage; exit 0 ;;
        *) err "Unknown argument: $arg"; usage; exit 1 ;;
    esac
done

# ── Interactive menu if no target given ───────────────────────────────────────
if [[ -z "$TARGET" ]]; then
    bold "PalladiumWallet — reproducible build"
    echo ""
    PS3="Select target: "
    options=("windows" "linux" "linux-arm64" "android" "all" "quit")
    select opt in "${options[@]}"; do
        case "$opt" in
            windows|linux|linux-arm64|android|all) TARGET="$opt"; break ;;
            quit) echo "Aborted."; exit 0 ;;
            *) echo "Invalid choice, try again." ;;
        esac
    done
    echo ""
fi

# ── Preflight checks ──────────────────────────────────────────────────────────
command -v docker &>/dev/null || die "Docker is not installed or not in PATH."
docker info &>/dev/null       || die "Docker daemon is not running."

# ── Read project version from csproj ─────────────────────────────────────────
CSPROJ="${PROJECT_ROOT}/src/App/PalladiumWallet.App.csproj"
[[ -f "$CSPROJ" ]] || die "Cannot find $CSPROJ — run this script from the repo."
VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ") || die "Cannot extract <Version> from $CSPROJ."
info "Version: ${VERSION}"

# ── Ensure NuGet volume exists ────────────────────────────────────────────────
docker volume inspect "$NUGET_VOLUME" &>/dev/null || \
    docker volume create "$NUGET_VOLUME" > /dev/null

# ── Image builders ────────────────────────────────────────────────────────────
ensure_desktop_image() {
    if [[ "$REBUILD" == true ]] || ! docker image inspect "$IMAGE_DESKTOP" &>/dev/null; then
        info "Building Docker image: ${IMAGE_DESKTOP}"
        docker build \
            --tag "$IMAGE_DESKTOP" \
            --file "${SCRIPT_DIR}/Dockerfile.desktop" \
            "${SCRIPT_DIR}"
    fi
}

ensure_android_image() {
    if [[ "$REBUILD" == true ]] || ! docker image inspect "$IMAGE_ANDROID" &>/dev/null; then
        info "Building Docker image: ${IMAGE_ANDROID}"
        docker build \
            --tag "$IMAGE_ANDROID" \
            --file "${SCRIPT_DIR}/Dockerfile.android" \
            "${SCRIPT_DIR}"
    fi
}

# ── Common docker run wrapper ─────────────────────────────────────────────────
# $1 = image name, $2 = inline bash commands to execute inside the container.
# Source is copied to /tmp/build inside the container so bin/obj never pollute
# the repo. Output is written to /output (→ host DIST_DIR sub-folder).
# Optional extra `docker run` args (e.g. keystore mount, signing passwords via
# -e) can be set by the caller in the EXTRA_DOCKER_ARGS array beforehand.
EXTRA_DOCKER_ARGS=()

run_build() {
    local image="$1"
    local commands="$2"
    local out_dir="$3"

    mkdir -p "$out_dir"

    docker run --rm \
        --volume "${PROJECT_ROOT}:/src:ro" \
        --volume "${out_dir}:/output" \
        --volume "${NUGET_VOLUME}:/root/.nuget/packages" \
        "${EXTRA_DOCKER_ARGS[@]}" \
        "$image" \
        bash -euo pipefail -c "
            cp -r /src/. /tmp/build
            cd /tmp/build
            ${commands}
            chown -R $(id -u):$(id -g) /output
        "
}

# ── Per-target build functions ────────────────────────────────────────────────
build_windows() {
    ensure_desktop_image
    info "Building Windows x64 …"
    run_build "$IMAGE_DESKTOP" \
        "dotnet publish src/App.Desktop \
            -r win-x64 \
            -c Release \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            --self-contained \
            -o /tmp/win-out
         cp /tmp/win-out/PalladiumWallet.exe \
            \"/output/PalladiumWallet-${VERSION}-win-x64.exe\"" \
        "${DIST_DIR}/windows"
    ok "Windows → dist/windows/PalladiumWallet-${VERSION}-win-x64.exe"
}

build_linux() {
    ensure_desktop_image
    info "Building Linux x64 …"
    run_build "$IMAGE_DESKTOP" \
        "dotnet publish src/App.Desktop \
            -r linux-x64 \
            -c Release \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            --self-contained \
            -o /tmp/linux-out
         install -m 755 /tmp/linux-out/PalladiumWallet \
             \"/output/PalladiumWallet-${VERSION}-linux-x64\"" \
        "${DIST_DIR}/linux"
    ok "Linux → dist/linux/PalladiumWallet-${VERSION}-linux-x64"
}

build_linux_arm64() {
    ensure_desktop_image
    info "Building Linux ARM64 …"
    run_build "$IMAGE_DESKTOP" \
        "dotnet publish src/App.Desktop \
            -r linux-arm64 \
            -c Release \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            --self-contained \
            -o /tmp/linux-arm64-out
         install -m 755 /tmp/linux-arm64-out/PalladiumWallet \
             \"/output/PalladiumWallet-${VERSION}-linux-arm64\"" \
        "${DIST_DIR}/linux-arm64"
    ok "Linux ARM64 → dist/linux-arm64/PalladiumWallet-${VERSION}-linux-arm64"
}

build_android() {
    ensure_android_image

    local keystore="${SCRIPT_DIR}/keystore/release.keystore"
    [[ -f "$keystore" ]] || die "No release keystore found at docker/keystore/release.keystore. Run ./docker/keystore/generate-keystore.sh once first (see docker/keystore/README.md)."

    info "Building Android APK …"
    read -r -s -p "Keystore password: " ANDROID_KS_PASS
    echo
    read -r -s -p "Key password (press Enter to reuse the keystore password): " ANDROID_KEY_PASS
    echo
    ANDROID_KEY_PASS="${ANDROID_KEY_PASS:-$ANDROID_KS_PASS}"

    # versionCode derived from <Version> (MAJOR.MINOR.PATCH, pre-release suffix
    # stripped) so it always increases with releases — required for some
    # installers to accept an in-place update even when the signature matches.
    local semver="${VERSION%%-*}"
    IFS='.' read -r VMAJOR VMINOR VPATCH <<< "$semver"
    local VERSION_CODE=$(( ${VMAJOR:-0} * 10000 + ${VMINOR:-0} * 100 + ${VPATCH:-0} ))
    info "versionCode: ${VERSION_CODE}"

    EXTRA_DOCKER_ARGS=(
        --volume "${keystore}:/keystore/release.keystore:ro"
        --env "ANDROID_KS_PASS=${ANDROID_KS_PASS}"
        --env "ANDROID_KEY_PASS=${ANDROID_KEY_PASS}"
    )
    run_build "$IMAGE_ANDROID" \
        "JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64 \
         dotnet build src/App.Android \
            -c Release \
            -t:SignAndroidPackage \
            -p:AndroidSdkDirectory=\${ANDROID_HOME} \
            -p:ApplicationVersion=${VERSION_CODE} \
            -p:AndroidKeyStore=true \
            -p:AndroidSigningKeyStore=/keystore/release.keystore \
            -p:AndroidSigningKeyAlias=palladiumwallet \
            -p:AndroidSigningStorePass=\${ANDROID_KS_PASS} \
            -p:AndroidSigningKeyPass=\${ANDROID_KEY_PASS}
         cp src/App.Android/bin/Release/net10.0-android/*-Signed.apk \
            \"/output/PalladiumWallet-${VERSION}.apk\"" \
        "${DIST_DIR}/android"
    EXTRA_DOCKER_ARGS=()
    ok "Android → dist/android/PalladiumWallet-${VERSION}.apk"
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
START=$(date +%s)

case "$TARGET" in
    windows)     build_windows     ;;
    linux)       build_linux       ;;
    linux-arm64) build_linux_arm64 ;;
    android)     build_android     ;;
    all)
        build_windows
        build_linux
        build_linux_arm64
        build_android
        ;;
esac

ELAPSED=$(( $(date +%s) - START ))
bold ""
bold "Done in ${ELAPSED}s.  Artifacts in: ${DIST_DIR}/"
