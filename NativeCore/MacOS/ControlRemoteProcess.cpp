#include <csignal>

#include "TaskPorts.hpp"

extern "C" void RC_CallConv ControlRemoteProcess(RC_Pointer handle, ControlRemoteProcessAction action)
{
	const auto pid = HandleToPid(handle);

	if (action == ControlRemoteProcessAction::Terminate)
	{
		kill(pid, SIGKILL);
		return;
	}

	const auto task = TaskPorts::Get(pid);
	if (task == MACH_PORT_NULL)
	{
		// Fall back to signals if we have no task port.
		kill(pid, action == ControlRemoteProcessAction::Suspend ? SIGSTOP : SIGCONT);
		return;
	}

	if (action == ControlRemoteProcessAction::Suspend)
	{
		task_suspend(task);
	}
	else if (action == ControlRemoteProcessAction::Resume)
	{
		task_resume(task);
	}
}
