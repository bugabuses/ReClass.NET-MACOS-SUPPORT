#pragma once

#include <mach/mach.h>
#include <sys/types.h>

#include "../ReClassNET_Plugin.hpp"

namespace TaskPorts
{
	// Returns the cached task port for pid, acquiring it via task_for_pid if not cached.
	// Returns MACH_PORT_NULL on failure, and also if the cached pid was recycled
	// (its process start time no longer matches what was recorded at Acquire).
	mach_port_t Get(pid_t pid);

	// task_for_pid + cache. Records the process start time so a later pid reuse
	// can be detected. Logs to stderr on failure.
	bool Acquire(pid_t pid);

	// Deallocates and forgets the port for pid. No-op if unknown.
	void Release(pid_t pid);

	// True if pid is known to TaskPorts and its recorded start time still matches
	// the live process (i.e. the pid was not recycled since Acquire/Get cached it).
	// False (and the stale entry is released) if pid is known but the start time
	// no longer matches, or the start time cannot be queried.
	bool IsSameProcess(pid_t pid);
}

inline pid_t HandleToPid(RC_Pointer handle)
{
	return static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));
}
