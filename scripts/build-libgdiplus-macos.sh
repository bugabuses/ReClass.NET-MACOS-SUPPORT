#!/bin/bash
# Build libgdiplus from source with X11 (cairo-xlib) support, using
# XQuartz's cairo (which has xlib support) instead of Homebrew's cairo
# (which is usually built without it). This is required for Mono
# WinForms to render through XQuartz on macOS (GdipCreateFromXDrawable_linux).
#
# Usage: ./scripts/build-libgdiplus-macos.sh
#
# Requires: brew install autoconf automake libtool pkg-config glib libpng \
#           jpeg giflib libtiff libexif pango
#           XQuartz installed (/opt/X11)
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SCRATCH="${SCRATCH:-$(mktemp -d)}"
SRC="$SCRATCH/libgdiplus"
PREFIX="$REPO/Dependencies/macos/libgdiplus"

# Pinned to the commit that produced the currently-working build (verified
# against the arm64/XQuartz toolchain in this repo's spike). Override with
# LIBGDIPLUS_REF=<sha-or-ref> to build a different revision.
LIBGDIPLUS_REF="${LIBGDIPLUS_REF:-94a49875487e296376f209fe64b921c6020f74c0}"

mkdir -p "$SCRATCH"

echo "==> Cloning libgdiplus (ref=$LIBGDIPLUS_REF) into $SRC"
rm -rf "$SRC"
mkdir -p "$SRC"
git -C "$SRC" init -q
git -C "$SRC" remote add origin https://github.com/mono/libgdiplus.git
git -C "$SRC" fetch --depth 1 origin "$LIBGDIPLUS_REF"
git -C "$SRC" checkout -q FETCH_HEAD

cd "$SRC"

# Poison the "pango" pkg-config module (report a version below
# PANGO_REQUIRED_VERSION) so libgdiplus's configure falls back to its
# built-in cairo/fontconfig text rendering path instead of pango.
# libgdiplus's --with-pango text_v is opt-in, but the *default* path still
# auto-enables pango whenever `pkg-config pango` succeeds -- and there is
# no --without-pango switch to suppress that. The real problem this avoids:
# Homebrew's pango/pangocairo is linked against Homebrew's cairo (Quartz
# font backend), so loading it together with XQuartz's cairo (xlib backend,
# needed for GdipCreateFromXDrawable) fails at dlopen time with
# "Symbol not found: _cairo_quartz_font_face_create_for_cgfont".
PANGO_POISON="$SCRATCH/pango-poison"
mkdir -p "$PANGO_POISON"
cat > "$PANGO_POISON/pango.pc" <<'EOF'
Name: pango
Description: poisoned stub so libgdiplus configure skips pango
Version: 0.0.0
Libs:
Cflags:
EOF
export PKG_CONFIG_PATH="$PANGO_POISON:/opt/X11/lib/pkgconfig:/opt/homebrew/lib/pkgconfig:/opt/homebrew/share/pkgconfig"
export CPPFLAGS="-I/opt/X11/include"
export CFLAGS="${CFLAGS:--Wno-error}"

# XQuartz's freetype2.pc pulls in "-lpng14" (an XQuartz-internal dependency
# of fontconfig/freetype), but XQuartz's libpng14.14.dylib is x86_64/i386
# only (no arm64 slice, no unversioned symlink), so on arm64 the linker
# fails with "library 'png14' not found". libgdiplus never actually calls
# into libpng14 (it uses libpng16 via cairo-png), so an empty arm64 stub
# satisfies the linker without touching anything under /opt/X11.
# The stub's install_name points at its final resting place inside PREFIX
# (copied there below after `make install`), so the dynamic linker can
# actually resolve it at runtime -- not just at link time.
PNGSHIM="$SCRATCH/libpng-shim"
mkdir -p "$PNGSHIM"
if [ ! -f "$PNGSHIM/libpng14.dylib" ]; then
	echo | clang -arch arm64 -dynamiclib -x c - -o "$PNGSHIM/libpng14.dylib" \
		-install_name "$PREFIX/lib/libpng14.dylib"
fi
export LDFLAGS="-L$PNGSHIM -L/opt/X11/lib"

echo "==> pkg-config cairo version/flags:"
pkg-config --modversion cairo || true
pkg-config --variable=prefix cairo || true
pkg-config --list-all | grep -i cairo || true

echo "==> Configuring (prefix=$PREFIX)"
mkdir -p "$PREFIX"
# NOTE: --with-pango is intentionally NOT used. Homebrew's pango is linked
# against Homebrew's cairo (built with a Quartz font backend), but we need
# XQuartz's cairo (built with the xlib backend, for GdipCreateFromXDrawable).
# Loading XQuartz's cairo underneath Homebrew's pangocairo causes:
#   Symbol not found: _cairo_quartz_font_face_create_for_cgfont
# Building without pango falls back to libgdiplus's fontconfig/freetype text
# path, avoiding the conflicting cairo ABI entirely.
# NOTE: deliberately not passing --with-pango (or --without-pango: its
# configure.ac sets text_v=pango whenever the --with-pango option is
# present at all, regardless of value, so --without-pango paradoxically
# still enables it). Leaving the option out uses the default autodetect
# path, which the pango.pc poison above steers to the cairo text backend.
if [ -x ./autogen.sh ]; then
	./autogen.sh --prefix="$PREFIX"
else
	autoreconf -fi
	./configure --prefix="$PREFIX"
fi

echo "==> Checking config.log for xlib support"
grep -i xlib config.log | head -20 || echo "    (no 'xlib' mentions found in config.log)"

echo "==> Building (src only; the 'tests' subdir vendors an old googletest"
echo "    CMakeLists that is incompatible with modern cmake and is not"
echo "    needed to produce/install libgdiplus.dylib)"
make -j"$(sysctl -n hw.ncpu)" -C src

echo "==> Installing to $PREFIX"
make -C src install
# Ship the libpng14 stub alongside libgdiplus so the runtime dylib lookup
# (baked in via its install_name above) actually resolves.
cp "$PNGSHIM/libpng14.dylib" "$PREFIX/lib/libpng14.dylib"
mkdir -p "$PREFIX/lib/pkgconfig"
if [ -f gdiplus.pc ]; then
	cp gdiplus.pc "$PREFIX/lib/pkgconfig/" 2>/dev/null || true
fi

echo "==> Verifying GdipCreateFromXDrawable_linux symbol"
nm -gU "$PREFIX/lib/libgdiplus.dylib" | grep GdipCreateFromXDrawable_linux
