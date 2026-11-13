#!/usr/bin/env bash
# Wraps a published osx-* folder into RunnerWatch.app (menu-bar app: no Dock icon).
# Usage: make-app.sh <publish-dir> <version> <output.app>
set -euo pipefail
SRC="$1"; VERSION="$2"; APP="$3"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$SRC"/. "$APP/Contents/MacOS/"

# Icon: build an .icns from the 512 px PNG with the stock macOS tools.
ICONSET="$(mktemp -d)/runnerwatch.iconset"
mkdir -p "$ICONSET"
for s in 16 32 64 128 256 512; do
  sips -z $s $s "$ROOT/src/RunnerWatch/Assets/icon-512.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
done
cp "$ICONSET/icon_32x32.png" "$ICONSET/icon_16x16@2x.png"
cp "$ICONSET/icon_64x64.png" "$ICONSET/icon_32x32@2x.png"
cp "$ICONSET/icon_256x256.png" "$ICONSET/icon_128x128@2x.png"
cp "$ICONSET/icon_512x512.png" "$ICONSET/icon_256x256@2x.png"
rm "$ICONSET/icon_64x64.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/runnerwatch.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>RunnerWatch</string>
  <key>CFBundleDisplayName</key><string>RunnerWatch</string>
  <key>CFBundleIdentifier</key><string>io.github.gustavodaniell.runnerwatch</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>RunnerWatch</string>
  <key>CFBundleIconFile</key><string>runnerwatch</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Ad-hoc signature: required for arm64 binaries to launch at all. Not notarized (see README).
codesign --force --deep --sign - "$APP"
echo "Built $APP"
