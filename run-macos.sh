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
export DYLD_LIBRARY_PATH="/opt/homebrew/lib"
# Force Mono's WinForms X11 backend; otherwise it defaults to the
# unsupported/broken Carbon driver on macOS and throws
# EntryPointNotFoundException (HIViewPlaceInSuperviewAt).
export MONO_MWF_MAC_FORCE_X11=1
cd "$APP"
exec sudo -E env DISPLAY="$DISPLAY" DYLD_LIBRARY_PATH=/opt/homebrew/lib MONO_MWF_MAC_FORCE_X11=1 "$(command -v mono)" ReClass.NET.exe "$@"
