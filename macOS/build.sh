#!/bin/bash
# Bouwt PipelineBar.app. Gebruik: ./build.sh            (bouwen en openen)
#                                  ./build.sh --install  (ook naar /Applications of ~/Applications kopiëren)
set -euo pipefail

APP="PipelineBar"
BUNDLE_ID="be.ae.pipelinebar"
VERSION="1.0"
HERE="$(cd "$(dirname "$0")" && pwd)"
BUILD="$(cd "$HERE/.." && pwd)/build"
APPDIR="$BUILD/$APP.app"

if ! xcrun --find swiftc >/dev/null 2>&1; then
  echo "Swift niet gevonden. Installeer Xcode of de Command Line Tools: xcode-select --install"
  exit 1
fi

echo "› Compileren…"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/Contents/MacOS" "$APPDIR/Contents/Resources"
xcrun swiftc -O -parse-as-library \
  -target "$(uname -m)-apple-macos14.0" \
  -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
  "$HERE"/Sources/*.swift \
  -o "$APPDIR/Contents/MacOS/$APP"

echo "› Icoon maken…"
ICONSET="$BUILD/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
if xcrun swift "$HERE/MakeIcon.swift" "$BUILD/icon.png" 2>/dev/null; then
  for s in 16 32 128 256 512; do
    sips -z $s $s "$BUILD/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$BUILD/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APPDIR/Contents/Resources/AppIcon.icns"
else
  echo "  (icoon overgeslagen)"
fi

cat > "$APPDIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleName</key><string>$APP</string>
  <key>CFBundleDisplayName</key><string>$APP</string>
  <key>CFBundleExecutable</key><string>$APP</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleDevelopmentRegion</key><string>nl</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

echo "› Ondertekenen (ad hoc)…"
codesign --force --sign - "$APPDIR" >/dev/null

TARGET="$APPDIR"
if [ "${1:-}" = "--install" ]; then
  # Zonder beheerdersrechten is /Applications niet schrijfbaar; dan in je eigen Programma's.
  DEST="/Applications"
  [ -w "$DEST" ] || DEST="$HOME/Applications"
  mkdir -p "$DEST"
  pkill -x "$APP" 2>/dev/null || true
  rm -rf "$DEST/$APP.app"
  cp -R "$APPDIR" "$DEST/"
  TARGET="$DEST/$APP.app"
  echo "› Geïnstalleerd in $DEST"
fi

open "$TARGET"
echo "✓ $APP draait. Zoek het icoon in je menubalk."
