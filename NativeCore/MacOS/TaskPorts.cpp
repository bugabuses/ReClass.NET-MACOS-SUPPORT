#include <cstdio>
#include <mutex>
#include <unordered_map>
#include <mach/mach_error.h>

#include "TaskPorts.hpp"

namespace
{
	std::mutex g_mutex;
	std::unordered_map<pid_t, mach_port_t> g_ports;
}

namespace TaskPorts
{
	bool Acquire(pid_t pid)
	{
		mach_port_t task = MACH_PORT_NULL;
		const auto kr = task_for_pid(mach_task_self(), pid, &task);
		if (kr != KERN_SUCCESS)
		{
			std::fprintf(stderr, "ReClass.NET NativeCore: task_for_pid(%d) failed: %s (run as root; SIP-protected processes cannot be attached)\n", pid, mach_error_string(kr));
			return false;
		}

		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			mach_port_deallocate(mach_task_self(), it->second);
			it->second = task;
		}
		else
		{
			g_ports.emplace(pid, task);
		}
		return true;
	}

	mach_port_t Get(pid_t pid)
	{
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_ports.find(pid);
			if (it != g_ports.end())
			{
				return it->second;
			}
		}

		if (!Acquire(pid))
		{
			return MACH_PORT_NULL;
		}

		std::lock_guard<std::mutex> lock(g_mutex);
		return g_ports[pid];
	}

	void Release(pid_t pid)
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			mach_port_deallocate(mach_task_self(), it->second);
			g_ports.erase(it);
		}
	}
}
