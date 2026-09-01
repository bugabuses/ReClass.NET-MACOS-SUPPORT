#include <csignal>

#include "NativeCore.hpp"

extern "C" void RC_CallConv ControlRemoteProcess(RC_Pointer handle, ControlRemoteProcessAction action)
{
	const auto pid = static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));

	int signal = SIGKILL;
	if (action == ControlRemoteProcessAction::Suspend)
	{
		signal = SIGSTOP;
	}
	else if (action == ControlRemoteProcessAction::Resume)
	{
		signal = SIGCONT;
	}

	kill(pid, signal);
}
