#include "TaskPorts.hpp"

extern "C" void RC_CallConv CloseRemoteProcess(RC_Pointer handle)
{
	TaskPorts::Release(HandleToPid(handle));
}
