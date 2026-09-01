#!/bin/sh
# Launches ReClass.NET on macOS under Mono using the X11 WinForms backend.
# Requirements: Homebrew mono, XQuartz running. Runs as root because
# task_for_pid needs it.
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
CFG="${RECLASS_CONFIG:-Release}"
APP="$DIR/build/$CFG/x64"

if [ ! -f "$APP/ReClass.NET.exe" ]; then
	echo "ReClass.NET.exe not found in $APP. Run 'make macos' first." >&2
	exit 1
fi
if [ ! -f "$APP/NativeCore.dylib" ]; then
	echo "NativeCore.dylib not found in $APP. Run 'make macos' first." >&2
	exit 1
fi

export DISPLAY="${DISPLAY:-:0}"

# Prefer a from-source libgdiplus built with cairo-xlib support (see
# scripts/build-libgdiplus-macos.sh) over Homebrew's libgdiplus, which is
# usually built without X11 support and is missing
# GdipCreateFromXDrawable_linux, needed by Mono WinForms' X11 backend.
LOCAL_GDIPLUS="$DIR/Dependencies/macos/libgdiplus/lib"
if [ ! -d "$LOCAL_GDIPLUS" ]; then
	echo "local libgdiplus not found; run scripts/build-libgdiplus-macos.sh" >&2
	exit 1
fi
export DYLD_LIBRARY_PATH="$LOCAL_GDIPLUS:/opt/X11/lib:/opt/homebrew/lib"
# Force Mono's WinForms X11 backend; otherwise it defaults to the
# unsupported/broken Carbon driver on macOS and throws
# EntryPointNotFoundException (HIViewPlaceInSuperviewAt).
export MONO_MWF_MAC_FORCE_X11=1

# Work around a SIGSEGV in Mono's X11Keyboard.CreateXic -> XGetIMValues:
# XGetIMValues is a variadic Xlib call, and Mono's P/Invoke marshalling of
# variadic functions on arm64 passes arguments in registers where libX11's
# va_list handling (_XIMCountVaList) expects them on the stack, corrupting
# memory and crashing the process. Setting XMODIFIERS=@im=none alone does
# NOT avoid this: XOpenIM() still succeeds (falling back to a built-in
# input method) and Mono still calls the crashing XGetIMValues(). Instead
# we interpose XOpenIM via DYLD_INSERT_LIBRARIES to always return NULL, so
# Mono's CreateXic() bails out before ever calling XGetIMValues(). This
# disables X11 input-method/compose-key support; ordinary keyboard input
# still works via core XKeyEvents. See scripts/xim-shim.c and
# scripts/build-xim-shim-macos.sh.
XIM_SHIM="$DIR/Dependencies/macos/xim-shim/libximshim.dylib"
if [ ! -f "$XIM_SHIM" ]; then
	echo "libximshim.dylib not found. Run scripts/build-xim-shim-macos.sh first." >&2
	exit 1
fi

cd "$APP"
exec sudo -E env DISPLAY="$DISPLAY" DYLD_LIBRARY_PATH="$DYLD_LIBRARY_PATH" DYLD_INSERT_LIBRARIES="$XIM_SHIM" MONO_MWF_MAC_FORCE_X11=1 "$(command -v mono)" ReClass.NET.exe "$@"
