#!/bin/bash
# Builds "VerbaFlow Widget.app" from this folder. On a Mac with Xcode or the Command Line Tools:
#   cd widget/mac && ./make-app.sh
# Then move build/VerbaFlow Widget.app into Applications. GitHub runs this too (.github/workflows/mac.yml).
# UNIVERSAL=1 builds for both Apple silicon and Intel Macs (needs the full Xcode).
set -euo pipefail
cd "$(dirname "$0")"
APP="build/VerbaFlow Widget.app"
BUILD_NUMBER="${BUILD_NUMBER:-1}"

if [ "${UNIVERSAL:-0}" = "1" ]; then
  swift build -c release --arch arm64 --arch x86_64
  BIN=".build/apple/Products/Release/VerbaFlowWidget"
else
  swift build -c release
  BIN="$(swift build -c release --show-bin-path)/VerbaFlowWidget"
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/VerbaFlowWidget"
sed "s/BUILD_NUMBER/$BUILD_NUMBER/" Resources/Info.plist > "$APP/Contents/Info.plist"

# The icon, in every size macOS asks for.
ICONSET="build/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s Resources/icon-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z $d $d Resources/icon-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Signed for this Mac only ("ad hoc"): macOS needs a signature to run it. A Developer ID would let it open without
# the "unidentified developer" question (docs/widget-mac.md).
codesign --force --sign - "$APP"
echo "Made: $APP"
