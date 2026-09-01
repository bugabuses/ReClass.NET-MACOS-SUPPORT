#include <algorithm>
#include <cstdint>
#include <libproc.h>
#include <sys/proc_info.h>
#include <unistd.h>
#include <vector>
#include <string>
#include <filesystem>

#include "NativeCore.hpp"

namespace fs = std::filesystem;

extern "C" void RC_CallConv EnumerateProcesses(EnumerateProcessCallback callbackProcess)
{
	try
	{
		if (callbackProcess == nullptr)
		{
			return;
		}

		// proc_listpids does not report how many pids exist ahead of time and
		// returning exactly buffer-size bytes may mean the result was
		// truncated. Grow the buffer and retry until it fits (or we give up).
		std::vector<pid_t> pids(1024);
		int bytes = 0;
		for (int attempt = 0; attempt < 8; ++attempt)
		{
			bytes = proc_listpids(PROC_ALL_PIDS, 0, pids.data(), static_cast<int>(pids.size() * sizeof(pid_t)));
			if (bytes <= 0)
			{
				return;
			}
			if (static_cast<size_t>(bytes) < pids.size() * sizeof(pid_t))
			{
				break;
			}
			pids.resize(pids.size() * 2);
		}
		pids.resize(static_cast<size_t>(bytes) / sizeof(pid_t));

		for (const auto pid : pids)
		{
			if (pid <= 0)
			{
				continue;
			}

			char pathBuffer[PROC_PIDPATHINFO_MAXSIZE] = {};
			if (proc_pidpath(pid, pathBuffer, sizeof(pathBuffer)) <= 0)
			{
				continue;
			}

			EnumerateProcessData data = {};
			data.Id = static_cast<RC_Size>(pid);
			try
			{
				MultiByteToUnicode(pathBuffer, data.Path, PATH_MAXIMUM_LENGTH - 1);
			}
			catch (...)
			{
				// Invalid UTF-8 in the path: skip this entry rather than
				// aborting the whole enumeration.
				continue;
			}

			try
			{
				const auto name = fs::path(pathBuffer).filename().u16string();
				str16cpy(data.Name, name.c_str(), std::min<size_t>(name.length(), PATH_MAXIMUM_LENGTH - 1));
			}
			catch (...)
			{
				continue;
			}

			callbackProcess(&data);
		}
	}
	catch (...)
	{
	}
}
