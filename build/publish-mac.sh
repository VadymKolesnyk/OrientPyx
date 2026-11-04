#!/usr/bin/env bash
# Builds the macOS Velopack packages for OrientPyx and (optionally) uploads them to GitHub Releases.
#
# Must run ON macOS: Velopack can only create a .app bundle / .pkg installer (and sign + notarize it) with
# Apple's tools. Normally this is run by .github/workflows/release-macos.yml on a macOS runner when the
# v<version> tag is pushed; the Windows side is build/publish.ps1. Both upload into the same GitHub Release
# (--merge), each under its own Velopack channel, so an installed app only ever sees its own platform's feed:
#   win        - Windows (publish.ps1)
#   osx-arm64  - Apple Silicon
#   osx-x64    - Intel Macs
#
# Usage:
#   build/publish-mac.sh <version> [--upload] [--runtime osx-arm64|osx-x64|all]
#
# Environment (all optional):
#   GITHUB_TOKEN           token with 'repo' scope - required with --upload, used for deltas otherwise
#   SIGN_APP_IDENTITY      "Developer ID Application: Name" (no team id, per Velopack docs)  -> codesign the .app
#   SIGN_INSTALL_IDENTITY  "Developer ID Installer: Name"             -> sign the .pkg
#   NOTARY_PROFILE         notarytool keychain profile name           -> notarize + staple
#   KEYCHAIN_PATH          keychain holding the identities (CI uses a temporary one)
# Without the signing variables the packages are unsigned: Gatekeeper then needs right-click -> Open
# (or System Settings -> Privacy & Security -> Open Anyway) on first launch.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <version> [--upload] [--runtime osx-arm64|osx-x64|all]" >&2
  exit 1
fi

VERSION="$1"; shift
UPLOAD=false
RUNTIMES=(osx-arm64 osx-x64)
while [[ $# -gt 0 ]]; do
  case "$1" in
    --upload) UPLOAD=true ;;
    --runtime)
      shift
      case "${1:-}" in
        all) RUNTIMES=(osx-arm64 osx-x64) ;;
        osx-arm64|osx-x64) RUNTIMES=("$1") ;;
        *) echo "Unknown runtime '${1:-}'" >&2; exit 1 ;;
      esac ;;
    *) echo "Unknown argument '$1'" >&2; exit 1 ;;
  esac
  shift
done

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script packs a macOS .app/.pkg and must run on macOS." >&2
  exit 1
fi
if ! command -v vpk >/dev/null 2>&1; then
  echo "Velopack CLI (vpk) not found. Install it with:  dotnet tool install -g vpk" >&2
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/OrientPyx.Presentation/OrientPyx.Presentation.csproj"
PLUGINS_SLN="$REPO_ROOT/plugins/OrientPyx.Plugins.sln"
PLUGINS_OUT="$REPO_ROOT/artifacts/plugins"
BUILD_DIR="$REPO_ROOT/build"
PACK_ID="OrientPyx"
PACK_TITLE="OrientPyx"
REPO_URL="https://github.com/VadymKolesnyk/OrientPyx"
ICON_PNG="$REPO_ROOT/src/OrientPyx.Presentation/Assets/orientpyx.png"
ICON_ICNS="$BUILD_DIR/publish-mac/orientpyx.icns"
PLIST_TEMPLATE="$BUILD_DIR/mac/Info.plist"
PLIST="$BUILD_DIR/publish-mac/Info.plist"

# --- 1. Icon: vpk wants an .icns on macOS; build it from the 256px PNG with Apple's tools ---------------
make_icns() {
  local iconset="$BUILD_DIR/publish-mac/orientpyx.iconset"
  rm -rf "$iconset"
  mkdir -p "$iconset"
  local size double
  for size in 16 32 128 256 512; do
    double=$((size * 2))
    sips -z "$size" "$size" "$ICON_PNG" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z "$double" "$double" "$ICON_PNG" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$ICON_ICNS"
  rm -rf "$iconset"
}
mkdir -p "$BUILD_DIR/publish-mac"
echo "Building $ICON_ICNS..."
make_icns

# --- 1b. Info.plist: Velopack copies a provided plist as-is, so the template carries every standard key
#         plus the .opyx document type (Finder association); only the version is filled in here.
sed "s/__VERSION__/$VERSION/g" "$PLIST_TEMPLATE" > "$PLIST"
plutil -lint "$PLIST"

# --- 2. Standard plugins (once; they are architecture-independent IL) ---------------------------------
echo "Building plugins..."
rm -rf "$PLUGINS_OUT"
dotnet build "$PLUGINS_SLN" -c Release

# --- 3. Publish + pack per runtime -------------------------------------------------------------------
for RID in "${RUNTIMES[@]}"; do
  PUBLISH_DIR="$BUILD_DIR/publish-mac/$RID"
  RELEASE_DIR="$BUILD_DIR/releases-mac/$RID"
  CHANNEL="$RID"

  echo "Publishing $PACK_ID $VERSION ($RID)..."
  rm -rf "$PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -p:Version="$VERSION" -o "$PUBLISH_DIR"

  mkdir -p "$PUBLISH_DIR/plugins"
  cp -R "$PLUGINS_OUT"/. "$PUBLISH_DIR/plugins/"

  # Previous packages of this channel make delta updates possible; a missing release is not an error.
  mkdir -p "$RELEASE_DIR"
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    vpk download github --repoUrl "$REPO_URL" --token "$GITHUB_TOKEN" --channel "$CHANNEL" --outputDir "$RELEASE_DIR" \
      || echo "No previous $CHANNEL release to build a delta against."
  fi

  SIGN_ARGS=()
  if [[ -n "${SIGN_APP_IDENTITY:-}" ]]; then
    SIGN_ARGS+=(--signAppIdentity "$SIGN_APP_IDENTITY")
    if [[ -n "${SIGN_INSTALL_IDENTITY:-}" ]]; then SIGN_ARGS+=(--signInstallIdentity "$SIGN_INSTALL_IDENTITY"); fi
    if [[ -n "${NOTARY_PROFILE:-}" ]]; then SIGN_ARGS+=(--notaryProfile "$NOTARY_PROFILE"); fi
    if [[ -n "${KEYCHAIN_PATH:-}" ]]; then SIGN_ARGS+=(--keychain "$KEYCHAIN_PATH"); fi
  else
    echo "SIGN_APP_IDENTITY not set - packing UNSIGNED $RID packages."
  fi

  echo "Packing Velopack release $VERSION ($CHANNEL)..."
  vpk pack \
    --packId "$PACK_ID" \
    --packVersion "$VERSION" \
    --packDir "$PUBLISH_DIR" \
    --mainExe "$PACK_ID" \
    --packTitle "$PACK_TITLE" \
    --icon "$ICON_ICNS" \
    --plist "$PLIST" \
    --runtime "$RID" \
    --channel "$CHANNEL" \
    --outputDir "$RELEASE_DIR" \
    "${SIGN_ARGS[@]+"${SIGN_ARGS[@]}"}"

  echo "Release ready in: $RELEASE_DIR"

  if [[ "$UPLOAD" == true ]]; then
    if [[ -z "${GITHUB_TOKEN:-}" ]]; then
      echo "Set GITHUB_TOKEN (repo scope) to upload." >&2
      exit 1
    fi
    echo "Uploading $CHANNEL release $VERSION to GitHub..."
    vpk upload github \
      --outputDir "$RELEASE_DIR" \
      --channel "$CHANNEL" \
      --repoUrl "$REPO_URL" \
      --token "$GITHUB_TOKEN" \
      --publish \
      --merge \
      --releaseName "OrientPyx $VERSION" \
      --tag "v$VERSION"
    echo "Uploaded $CHANNEL."
  fi
done
