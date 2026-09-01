#include <mach/mach.h>
#include <mach/mach_vm.h>

#include "TaskPorts.hpp"

extern "C" bool RC_CallConv ReadRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size)
{
	if (size <= 0)
	{
		return size == 0;
	}

	const auto task = TaskPorts::Get(HandleToPid(handle));
	if (task == MACH_PORT_NULL)
	{
		return false;
	}

	mach_vm_size_t outSize = 0;
	const auto kr = mach_vm_read_overwrite(
		task,
		reinterpret_cast<mach_vm_address_t>(address),
		static_cast<mach_vm_size_t>(size),
		reinterpret_cast<mach_vm_address_t>(static_cast<uint8_t*>(buffer) + offset),
		&outSize);

	return kr == KERN_SUCCESS && outSize == static_cast<mach_vm_size_t>(size);
}
