#pragma once

#include <mach/mach.h>
#include <sys/types.h>

#include "../ReClassNET_Plugin.hpp"

namespace TaskPorts
{
	// Returns the cached task port for pid, acquiring it via task_for_pid if not cached.
	// Returns MACH_PORT_NULL on failure.
	mach_port_t Get(pid_t pid);

	// task_for_pid + cache. Logs to stderr on failure.
	bool Acquire(pid_t pid);

	// Deallocates and forgets the port for pid. No-op if unknown.
	void Release(pid_t pid);
}

inline pid_t HandleToPid(RC_Pointer handle)
{
	return static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));
}
