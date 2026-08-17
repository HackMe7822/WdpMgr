#!/usr/bin/env bash
# Mac Display Policy Manager — one-shot installer
# Run on the Mac as:
#   curl -fsSL https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/MacClearDA/install-mac.sh | bash
# Or after cloning the repo:
#   bash MacClearDA/install-mac.sh

set -e

REPO="https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/MacClearDA"
INSTALL_DIR="$HOME/Library/Application Support/MacDisplayPolicy"
DYLIB_DIR="$INSTALL_DIR/maccore"
BIN="$INSTALL_DIR/MacWdpMgr"
DYLIB="$DYLIB_DIR/maccore.dylib"
LOG="/tmp/MacClearDA.log"

echo ""
echo "  ============================================"
echo "    Mac Display Policy Manager - Installer"
echo "  ============================================"
echo ""

# ── [1] Xcode Command Line Tools (clang + swiftc) ────────────────────────────
echo "  [1] Checking Xcode command line tools..."
if ! xcode-select -p &>/dev/null; then
    echo "      Installing Xcode command line tools (follow the popup)..."
    xcode-select --install
    echo "      Re-run this script after the installation completes."
    exit 0
fi
echo "      OK: $(xcode-select -p)"

# ── [2] Create install directory ─────────────────────────────────────────────
echo "  [2] Creating directories..."
mkdir -p "$DYLIB_DIR"
echo "      OK: $INSTALL_DIR"

# ── [3] Download sources ──────────────────────────────────────────────────────
echo "  [3] Downloading sources from GitHub..."
curl -fsSL "$REPO/maccore/maccore.m"        -o "$DYLIB_DIR/maccore.m"
curl -fsSL "$REPO/MacWdpMgr/MacWdpMgr.swift" -o "$INSTALL_DIR/MacWdpMgr.swift"
curl -fsSL "$REPO/inject.sh"                 -o "$INSTALL_DIR/inject.sh"
chmod +x "$INSTALL_DIR/inject.sh"
echo "      OK"

# ── [4] Build maccore.dylib ───────────────────────────────────────────────────
echo "  [4] Building maccore.dylib (fat binary arm64+x86_64)..."
SDK=$(xcrun --show-sdk-path 2>/dev/null || true)
SDK_FLAG=$([ -n "$SDK" ] && echo "-isysroot $SDK" || echo "")
clang -dynamiclib -framework Cocoa \
    -arch arm64 -arch x86_64 \
    -O2 $SDK_FLAG \
    -o "$DYLIB" "$DYLIB_DIR/maccore.m"
codesign --remove-signature "$DYLIB" 2>/dev/null || true
echo "      OK: $DYLIB"

# ── [5] Build MacWdpMgr ───────────────────────────────────────────────────────
echo "  [5] Building MacWdpMgr..."
swiftc "$INSTALL_DIR/MacWdpMgr.swift" \
    -framework AppKit -framework Security \
    -o "$BIN"
echo "      OK: $BIN"

# ── [6] Update inject.sh to point to installed dylib path ────────────────────
# Patch the install dir reference so inject.sh always finds maccore.dylib
sed -i '' "s|SCRIPT_DIR=.*|SCRIPT_DIR=\"$INSTALL_DIR/maccore\"|" "$INSTALL_DIR/inject.sh" 2>/dev/null || true

# ── [7] Symlink to /usr/local/bin ────────────────────────────────────────────
echo "  [6] Symlinking to /usr/local/bin..."
mkdir -p /usr/local/bin
ln -sf "$BIN"                       /usr/local/bin/MacWdpMgr   2>/dev/null || true
ln -sf "$INSTALL_DIR/inject.sh"     /usr/local/bin/mac-inject  2>/dev/null || true
echo "      OK"

echo ""
echo "  ============================================"
echo "    INSTALL COMPLETE"
echo "  ============================================"
echo ""
echo "    Binary : $BIN"
echo "    Dylib  : $DYLIB"
echo "    Log    : $LOG"
echo ""
echo "    Launch GUI:     open '$BIN'"
echo "    Inject an app:  mac-inject /Applications/App.app"
echo ""
echo "    NEXT STEP: Paste your server RSA public key into:"
echo "      $INSTALL_DIR/MacWdpMgr.swift  (line: let RSA_PUBLIC_KEY = ...)"
echo "    Then rebuild:   swiftc MacWdpMgr.swift -framework AppKit -framework Security -o MacWdpMgr"
echo ""
echo "    Key source: WdpMgr Admin Panel -> Settings -> WdpMgr Public Key"
echo ""
