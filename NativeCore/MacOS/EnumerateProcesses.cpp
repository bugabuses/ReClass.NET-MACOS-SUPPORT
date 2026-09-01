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
	if (callbackProcess == nullptr)
	{
		return;
	}

	int bytes = proc_listpids(PROC_ALL_PIDS, 0, nullptr, 0);
	if (bytes <= 0)
	{
		return;
	}

	std::vector<pid_t> pids(static_cast<size_t>(bytes) / sizeof(pid_t) + 64);
	bytes = proc_listpids(PROC_ALL_PIDS, 0, pids.data(), static_cast<int>(pids.size() * sizeof(pid_t)));
	if (bytes <= 0)
	{
		return;
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
		MultiByteToUnicode(pathBuffer, data.Path, PATH_MAXIMUM_LENGTH);
		const auto name = fs::path(pathBuffer).filename().u16string();
		str16cpy(data.Name, name.c_str(), std::min<size_t>(name.length(), PATH_MAXIMUM_LENGTH - 1));

		callbackProcess(&data);
	}
}
