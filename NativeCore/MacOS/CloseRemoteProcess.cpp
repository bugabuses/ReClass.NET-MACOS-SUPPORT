#include "TaskPorts.hpp"

extern "C" void RC_CallConv CloseRemoteProcess(RC_Pointer handle)
{
	try
	{
		TaskPorts::Release(HandleToPid(handle));
	}
	catch (...)
	{
	}
}
