#!/usr/bin/env bash
# Build self-contained release zips. Users need no .NET SDK and (on macOS) no Homebrew libusb
# when we successfully bundle libusb into the package.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="${VERSION:-$(grep -E '<Version>' hakchi-cli/hakchi-cli.csproj | sed -E 's/.*<Version>([^<]+)<\/Version>.*/\1/')}"
OUT="${OUT:-$ROOT/artifacts/release}"
CONFIG="${CONFIG:-Release}"

# RIDs to build. On macOS we can cross-build other macOS RIDs; Linux RIDs need a Linux host
# (or the GitHub Actions matrix). Override: RIDS="osx-arm64 osx-x64" ./scripts/publish-release.sh
if [[ -z "${RIDS:-}" ]]; then
  case "$(uname -s)" in
    Darwin) RIDS="osx-arm64 osx-x64" ;;
    Linux)  RIDS="linux-x64 linux-arm64" ;;
    *)      RIDS="osx-arm64 osx-x64 linux-x64 linux-arm64" ;;
  esac
fi

export PATH="/opt/homebrew/bin:${PATH:-}"
if [[ -z "${DOTNET_ROOT:-}" ]] && command -v brew >/dev/null 2>&1; then
  export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"
fi

mkdir -p "$OUT"
echo "Version: $VERSION"
echo "Output:  $OUT"
echo "RIDs:    $RIDS"

find_libusb_dylib() {
  local candidates=(
    "${HOMEBREW_PREFIX:-/opt/homebrew}/lib/libusb-1.0.dylib"
    "/usr/local/lib/libusb-1.0.dylib"
    "/opt/local/lib/libusb-1.0.dylib"
  )
  local p
  for p in "${candidates[@]}"; do
    if [[ -f "$p" ]]; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

find_libusb_so() {
  local candidates=(
    "/usr/lib/x86_64-linux-gnu/libusb-1.0.so.0"
    "/usr/lib/aarch64-linux-gnu/libusb-1.0.so.0"
    "/usr/lib/libusb-1.0.so.0"
    "/lib/x86_64-linux-gnu/libusb-1.0.so.0"
    "/lib/aarch64-linux-gnu/libusb-1.0.so.0"
  )
  local p
  for p in "${candidates[@]}"; do
    if [[ -f "$p" ]]; then
      echo "$p"
      return 0
    fi
  done
  # ldconfig if available
  if command -v ldconfig >/dev/null 2>&1; then
    p="$(ldconfig -p 2>/dev/null | awk '/libusb-1.0.so/{print $NF; exit}')"
    if [[ -n "${p:-}" && -f "$p" ]]; then
      echo "$p"
      return 0
    fi
  fi
  return 1
}

bundle_native() {
  local rid="$1"
  local dir="$2"
  case "$rid" in
    osx-*)
      local dylib
      if dylib="$(find_libusb_dylib)"; then
        cp -f "$dylib" "$dir/libusb-1.0.dylib"
        # Prefer relative lookup next to the binary
        if command -v install_name_tool >/dev/null 2>&1; then
          install_name_tool -id "@loader_path/libusb-1.0.dylib" "$dir/libusb-1.0.dylib" 2>/dev/null || true
        fi
        echo "  bundled libusb: $dylib"
      else
        echo "  WARNING: libusb-1.0.dylib not found on build machine — users will need brew install libusb" >&2
      fi
      ;;
    linux-*)
      local so
      if so="$(find_libusb_so)"; then
        # Ship as both names LibUsbDotNet / our resolver accept
        cp -fL "$so" "$dir/libusb-1.0.so.0"
        ln -sfn libusb-1.0.so.0 "$dir/libusb-1.0.so"
        echo "  bundled libusb: $so"
      else
        echo "  WARNING: libusb not found — users need libusb-1.0 (e.g. apt install libusb-1.0-0)" >&2
      fi
      ;;
  esac
}

for rid in $RIDS; do
  name="hakchi-cli-${VERSION}-${rid}"
  dest="$OUT/$name"
  rm -rf "$dest"
  mkdir -p "$dest"

  echo ""
  echo "==> Publishing $rid …"
  # Directory publish (not single-file): keeps assets/ + native dylibs beside the binary.
  # Self-contained: no .NET install required on the user's machine.
  dotnet publish "$ROOT/hakchi-cli/hakchi-cli.csproj" \
    -c "$CONFIG" \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -o "$dest"

  # Top-level memboot / art assets (already partly under dest/assets from csproj)
  if [[ -d "$ROOT/assets" ]]; then
    mkdir -p "$dest/assets"
    # Copy blanks always; optional large boot images if present
    cp -f "$ROOT/assets"/blank_*.png "$dest/assets/" 2>/dev/null || true
    for f in boot.img boot-clovershell.img uboot.bin; do
      [[ -f "$ROOT/assets/$f" ]] && cp -f "$ROOT/assets/$f" "$dest/assets/"
    done
  fi

  bundle_native "$rid" "$dest"

  # Ad-hoc sign (no Apple Developer ID). Downloads still get Gatekeeper quarantine —
  # users: sudo xattr -dr com.apple.quarantine .
  if [[ "$rid" == osx-* ]] && command -v codesign >/dev/null 2>&1; then
    codesign --force --sign - "$dest/libusb-1.0.dylib" 2>/dev/null || true
    codesign --force --sign - "$dest/hakchi-cli" 2>/dev/null || true
    echo "  ad-hoc codesign applied"
  fi

  # Friendly launcher that chdirs to package root (assets + libusb)
  cat > "$dest/hakchi" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"
exec "$ROOT/hakchi-cli" "$@"
EOF
  chmod +x "$dest/hakchi" "$dest/hakchi-cli" 2>/dev/null || chmod +x "$dest/hakchi"

  # README snippet inside the zip
  cat > "$dest/README.txt" <<EOF
hakchi-cli ${VERSION} (${rid})
================================

Self-contained build — no .NET SDK required.

Quick start
-----------
  ./hakchi status
  ./hakchi usb
  ./hakchi games
  ./hakchi add-game /path/to/game.zip

On macOS the first USB access may prompt for permission.
Use a data USB cable; power the Classic to the hakchi game menu (RNDIS 04E8:6863).

If status says libusb is missing:
  macOS:  brew install libusb
  Debian: sudo apt install libusb-1.0-0

macOS Gatekeeper ("Apple could not verify…"):
  sudo xattr -dr com.apple.quarantine .

License: GPL-3.0 — see LICENSE / NOTICE in the source repository.
EOF

  # Include license files
  cp -f "$ROOT/LICENSE" "$dest/" 2>/dev/null || true
  cp -f "$ROOT/NOTICE" "$dest/" 2>/dev/null || true

  zip_path="$OUT/${name}.zip"
  rm -f "$zip_path"
  (
    cd "$OUT"
    # zip contents as folder name/
    if command -v zip >/dev/null 2>&1; then
      zip -qry "${name}.zip" "$name"
    else
      ditto -c -k --sequesterRsrc --keepParent "$name" "${name}.zip"
    fi
  )
  echo "  -> $zip_path ($(du -h "$zip_path" | awk '{print $1}'))"
done

echo ""
echo "Done. Artifacts in $OUT"
ls -la "$OUT"/*.zip 2>/dev/null || true
