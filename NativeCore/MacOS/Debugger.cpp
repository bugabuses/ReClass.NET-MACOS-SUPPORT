#include "NativeCore.hpp"

// Hardware-breakpoint debugging is not supported on macOS in this port.
// macOS ptrace exposes no debug-register access; a Mach exception-port
// implementation is a later phase.

extern "C" bool RC_CallConv AttachDebuggerToProcess(RC_Pointer id)
{
	return false;
}

extern "C" void RC_CallConv DetachDebuggerFromProcess(RC_Pointer id)
{
}

extern "C" bool RC_CallConv AwaitDebugEvent(DebugEvent* evt, int timeoutInMilliseconds)
{
	return false;
}

extern "C" void RC_CallConv HandleDebugEvent(DebugEvent* evt)
{
}

extern "C" bool RC_CallConv SetHardwareBreakpoint(RC_Pointer id, RC_Pointer address, HardwareBreakpointRegister reg, HardwareBreakpointTrigger type, HardwareBreakpointSize size, bool set)
{
	return false;
}
