#include <cstdio>
#include <mutex>
#include <unordered_map>
#include <mach/mach_error.h>
#include <sys/sysctl.h>

#include "TaskPorts.hpp"

namespace
{
	struct Entry
	{
		mach_port_t Port;
		struct timeval StartTime;
	};

	std::mutex g_mutex;
	std::unordered_map<pid_t, Entry> g_ports;

	// Fetches the process start time for pid via sysctl. Returns false if the
	// pid is unknown to the kernel or the query otherwise fails.
	bool QueryStartTime(pid_t pid, struct timeval& out)
	{
		int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PID, pid };
		struct kinfo_proc info = {};
		size_t size = sizeof(info);
		if (sysctl(mib, 4, &info, &size, nullptr, 0) != 0 || size == 0)
		{
			return false;
		}
		out = info.kp_proc.p_starttime;
		return true;
	}

	bool SameStartTime(const struct timeval& a, const struct timeval& b)
	{
		return a.tv_sec == b.tv_sec && a.tv_usec == b.tv_usec;
	}
}

namespace TaskPorts
{
	bool Acquire(pid_t pid)
	{
		struct timeval startTime = {};
		if (!QueryStartTime(pid, startTime))
		{
			std::fprintf(stderr, "ReClass.NET NativeCore: could not query start time for pid %d\n", pid);
			return false;
		}

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
			mach_port_deallocate(mach_task_self(), it->second.Port);
			it->second.Port = task;
			it->second.StartTime = startTime;
		}
		else
		{
			g_ports.emplace(pid, Entry{ task, startTime });
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
				struct timeval startTime = {};
				if (QueryStartTime(pid, startTime) && SameStartTime(startTime, it->second.StartTime))
				{
					return it->second.Port;
				}

				// pid was recycled (or is no longer queryable): the cached port
				// refers to a dead/different process. Release it and refuse to
				// lazily re-acquire, so callers observe failure rather than
				// silently attaching to the wrong process.
				mach_port_deallocate(mach_task_self(), it->second.Port);
				g_ports.erase(it);
				return MACH_PORT_NULL;
			}
		}

		if (!Acquire(pid))
		{
			return MACH_PORT_NULL;
		}

		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			return it->second.Port;
		}
		return MACH_PORT_NULL;
	}

	void Release(pid_t pid)
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			mach_port_deallocate(mach_task_self(), it->second.Port);
			g_ports.erase(it);
		}
	}

	bool IsSameProcess(pid_t pid)
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it == g_ports.end())
		{
			// Unknown to TaskPorts: no cached identity to contradict the pid.
			return true;
		}

		struct timeval startTime = {};
		if (QueryStartTime(pid, startTime) && SameStartTime(startTime, it->second.StartTime))
		{
			return true;
		}

		mach_port_deallocate(mach_task_self(), it->second.Port);
		g_ports.erase(it);
		return false;
	}
}
