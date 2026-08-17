#!/usr/bin/env bash
# inject.sh — launch a target app with maccore.dylib pre-loaded
# Usage: ./inject.sh /path/to/App.app [args...]
#        ./inject.sh /path/to/binary  [args...]

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DYLIB="$SCRIPT_DIR/maccore/maccore.dylib"

if [[ ! -f "$DYLIB" ]]; then
    echo "ERROR: maccore.dylib not found. cd maccore && make" >&2; exit 1
fi
if [[ -z "$1" ]]; then
    echo "Usage: $0 /path/to/App.app" >&2; exit 1
fi

TARGET="$1"; shift

if [[ "$TARGET" == *.app ]]; then
    NAME=$(basename "$TARGET" .app)
    BIN="$TARGET/Contents/MacOS/$NAME"
    if [[ ! -f "$BIN" ]]; then
        BIN="$TARGET/Contents/MacOS/$(defaults read "$TARGET/Contents/Info.plist" \
            CFBundleExecutable 2>/dev/null || echo "$NAME")"
    fi
else
    BIN="$TARGET"
fi

echo "[inject] launching: $BIN"
echo "[inject] dylib: $DYLIB"
echo "[inject] log: /tmp/MacClearDA.log"

DYLD_INSERT_LIBRARIES="$DYLIB" DYLD_FORCE_FLAT_NAMESPACE=1 exec "$BIN" "$@"
