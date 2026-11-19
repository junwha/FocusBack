#!/bin/bash
# Build FocusQueue.app (macOS). No Xcode project needed, only the Swift toolchain (xcode-select --install).
#   ./build.sh          build mac/build/FocusQueue.app
#   ./build.sh --run    build, then restart the running widget (your task data is backed up first, never touched)
set -euo pipefail
cd "$(dirname "$0")"

APP="build/FocusQueue.app"
BIN="$APP/Contents/MacOS/FocusQueue"
BUNDLE_ID="io.github.focusqueue"
MIN_MACOS="12.0"
DATA="$HOME/Library/Application Support/ADHD-PQ"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

archs=()
for a in arm64 x86_64; do
  swiftc -O -swift-version 5 -target "$a-apple-macos$MIN_MACOS" -o "build/FocusQueue-$a" Sources/*.swift
  archs+=("build/FocusQueue-$a")
done
lipo -create -output "$BIN" "${archs[@]}"
rm -f "${archs[@]}"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>FocusQueue</string>
  <key>CFBundleDisplayName</key><string>FocusQueue</string>
  <key>CFBundleExecutable</key><string>FocusQueue</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>$MIN_MACOS</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAccessibilityUsageDescription</key><string>Used to tell which window you are working in.</string>
</dict>
</plist>
PLIST

codesign --force --sign - "$APP" >/dev/null 2>&1 || true
echo "built $APP"

if [[ "${1:-}" == "--run" ]]; then
  # keep a copy of the task queue before restarting (the app never deletes it either)
  if [[ -s "$DATA/items.tsv" ]]; then cp "$DATA/items.tsv" "$DATA/items.bak.tsv"; fi
  # ask the running widget to quit cleanly (it logs a running session and saves)
  osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
  for _ in 1 2 3 4 5 6 7 8 9 10; do pgrep -qx FocusQueue || break; sleep 0.3; done
  open "$APP"
fi
