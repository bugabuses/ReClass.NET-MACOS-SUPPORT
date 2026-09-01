/*
 * Interpose shim to work around a SIGSEGV in Mono's WinForms X11 backend.
 *
 * System.Windows.Forms.X11Keyboard.CreateXic() calls the Xlib variadic
 * function XGetIMValues() to query supported input styles. Mono's P/Invoke
 * marshalling of variadic Xlib calls on arm64 passes arguments in registers
 * where libX11's va_list handling (_XIMCountVaList) expects them on the
 * stack, corrupting memory and crashing the process with SIGSEGV.
 *
 * Setting XMODIFIERS=@im=none is not sufficient to prevent this: Xlib's
 * XOpenIM() still succeeds (falling back to a built-in/none input method
 * implementation) and Mono still proceeds to call the crashing
 * XGetIMValues(). The reliable fix is to make XOpenIM() itself return NULL,
 * which makes Mono's CreateXic() bail out before ever reaching
 * XGetIMValues(). This costs X11 input-method support (dead/compose key
 * input), but core keyboard input via XKeyEvent still works normally.
 *
 * Built as a dylib and loaded via DYLD_INSERT_LIBRARIES so it interposes
 * XOpenIM for the mono process only.
 */
#include <stddef.h>

/* Opaque; we never need the real XIM type here. */
typedef void *XIM;
typedef void *Display;

static XIM my_XOpenIM(Display *dpy, void *db, char *res_name, char *res_class)
{
	(void)dpy;
	(void)db;
	(void)res_name;
	(void)res_class;
	return NULL;
}

extern XIM XOpenIM(Display *dpy, void *db, char *res_name, char *res_class);

__attribute__((used)) static const struct {
	const void *replacement;
	const void *original;
} interposers[] __attribute__((section("__DATA,__interpose"))) = {
	{ (const void *)my_XOpenIM, (const void *)XOpenIM },
};
