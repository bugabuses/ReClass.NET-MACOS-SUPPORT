#include "TaskPorts.hpp"

extern "C" RC_Pointer RC_CallConv OpenRemoteProcess(RC_Pointer id, ProcessAccess desiredAccess)
{
	try
	{
		const auto pid = HandleToPid(id);
		if (!TaskPorts::Acquire(pid))
		{
			return nullptr;
		}
		return id;
	}
	catch (...)
	{
		return nullptr;
	}
}
