#!/bin/bash
# Build MacOverlay.app — run on a Mac with Xcode command-line tools installed
# Usage: ./build.sh
# Output: MacOverlay.app (in same directory)

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

APP_NAME="MacOverlay"
BUNDLE="$APP_NAME.app"
MACOS_DIR="$BUNDLE/Contents/MacOS"
RESOURCES_DIR="$BUNDLE/Contents/Resources"

echo "==> Cleaning..."
rm -rf "$BUNDLE"

echo "==> Creating bundle structure..."
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

echo "==> Compiling Swift..."
swiftc MacOverlay.swift \
    -o "$MACOS_DIR/$APP_NAME" \
    -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
    -target "x86_64-apple-macosx12.0" \
    -framework Cocoa \
    -framework WebKit \
    -O

# Also build arm64 (Apple Silicon)
swiftc MacOverlay.swift \
    -o "$MACOS_DIR/${APP_NAME}_arm64" \
    -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
    -target "arm64-apple-macosx12.0" \
    -framework Cocoa \
    -framework WebKit \
    -O

echo "==> Creating universal binary..."
lipo -create -output "$MACOS_DIR/${APP_NAME}_universal" \
    "$MACOS_DIR/$APP_NAME" \
    "$MACOS_DIR/${APP_NAME}_arm64"
mv "$MACOS_DIR/${APP_NAME}_universal" "$MACOS_DIR/$APP_NAME"
rm "$MACOS_DIR/${APP_NAME}_arm64"

echo "==> Copying Info.plist..."
cp Info.plist "$BUNDLE/Contents/"

echo "==> Making executable..."
chmod +x "$MACOS_DIR/$APP_NAME"

echo "==> Code signing (ad-hoc for distribution without Apple account)..."
codesign --force --deep --sign - "$BUNDLE" || echo "   (code sign failed — app may not run on newer macOS without notarization)"

echo ""
echo "==> Done: $BUNDLE"
echo "    To install: copy MacOverlay.app to /Applications"
echo "    To license: place a .lic file next to MacOverlay.app or in ~/Library/Application Support/MacOverlay/"
echo ""
echo "    To create a pkg installer:"
echo "    pkgbuild --install-location /Applications --component $BUNDLE MacOverlay.pkg"
