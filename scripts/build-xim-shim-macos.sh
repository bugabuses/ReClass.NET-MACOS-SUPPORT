#!/bin/sh
# Builds Dependencies/macos/xim-shim/libximshim.dylib, a DYLD interpose
# shim that makes XOpenIM() return NULL. This works around a SIGSEGV in
# Mono's System.Windows.Forms.X11Keyboard.CreateXic -> XGetIMValues on
# arm64 (variadic Xlib call marshalled incorrectly by Mono's P/Invoke).
# See scripts/xim-shim.c for details.
set -e
DIR="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$DIR/Dependencies/macos/xim-shim"
mkdir -p "$OUT"
clang -dynamiclib -arch arm64 \
	-I/opt/X11/include \
	-L/opt/X11/lib -lX11 \
	-o "$OUT/libximshim.dylib" \
	"$DIR/scripts/xim-shim.c"
echo "Built $OUT/libximshim.dylib"
