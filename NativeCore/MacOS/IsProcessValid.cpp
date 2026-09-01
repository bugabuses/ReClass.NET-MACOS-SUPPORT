#include <sys/types.h>
#include <signal.h>

#include "NativeCore.hpp"
#include "TaskPorts.hpp"

extern "C" bool RC_CallConv IsProcessValid(RC_Pointer handle)
{
	try
	{
		const auto pid = static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));
		return kill(pid, 0) == 0 && TaskPorts::IsSameProcess(pid);
	}
	catch (...)
	{
		return false;
	}
}
