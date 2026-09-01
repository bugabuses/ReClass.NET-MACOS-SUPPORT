#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach/vm_page_size.h>

#include "TaskPorts.hpp"

namespace
{
	bool TryWrite(mach_port_t task, mach_vm_address_t address, const void* data, int size)
	{
		const auto kr = mach_vm_write(task, address, reinterpret_cast<vm_offset_t>(data), static_cast<mach_msg_type_number_t>(size));
		return kr == KERN_SUCCESS;
	}

	// Queries the protection of the region containing address.
	bool QueryProtection(mach_port_t task, mach_vm_address_t address, vm_prot_t& protection, mach_vm_address_t& regionEnd)
	{
		mach_vm_address_t regionAddress = address;
		mach_vm_size_t regionSize = 0;
		vm_region_basic_info_data_64_t info = {};
		mach_msg_type_number_t count = VM_REGION_BASIC_INFO_COUNT_64;
		mach_port_t objectName = MACH_PORT_NULL;
		const auto kr = mach_vm_region(task, &regionAddress, &regionSize, VM_REGION_BASIC_INFO_64,
			reinterpret_cast<vm_region_info_t>(&info), &count, &objectName);
		if (kr != KERN_SUCCESS || regionAddress > address)
		{
			return false;
		}
		protection = info.protection;
		regionEnd = regionAddress + regionSize;
		return true;
	}
}

extern "C" bool RC_CallConv WriteRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size)
{
	try
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

		const auto target = reinterpret_cast<mach_vm_address_t>(address);
		const auto data = static_cast<const uint8_t*>(buffer) + offset;

		if (TryWrite(task, target, data, size))
		{
			return true;
		}

		// Retry: make the pages writable (copy-on-write), write, restore.
		const mach_vm_address_t pageStart = target & ~static_cast<mach_vm_address_t>(vm_page_size - 1);
		const mach_vm_size_t pageLen = ((target + size + vm_page_size - 1) & ~static_cast<mach_vm_address_t>(vm_page_size - 1)) - pageStart;

		vm_prot_t original = VM_PROT_NONE;
		mach_vm_address_t regionEnd = 0;
		const bool haveOriginal = QueryProtection(task, target, original, regionEnd);

		// Check if write extends past region boundary.
		if (haveOriginal && target + size > regionEnd)
		{
			return false;
		}

		// Clamp protect/restore range to region.
		const mach_vm_address_t protectEnd = haveOriginal ? std::min(pageStart + pageLen, regionEnd) : pageStart + pageLen;
		const mach_vm_size_t protectLen = protectEnd - pageStart;

		if (mach_vm_protect(task, pageStart, protectLen, FALSE, VM_PROT_READ | VM_PROT_WRITE | VM_PROT_COPY) != KERN_SUCCESS)
		{
			std::fprintf(stderr, "ReClass.NET NativeCore: mach_vm_protect (COW) failed for write at %p\n", address);
			return false;
		}

		const bool ok = TryWrite(task, target, data, size);

		if (haveOriginal)
		{
			if (mach_vm_protect(task, pageStart, protectLen, FALSE, original) != KERN_SUCCESS)
			{
				std::fprintf(stderr, "ReClass.NET NativeCore: failed to restore protection after write at %p\n", address);
			}
		}

		return ok;
	}
	catch (...)
	{
		return false;
	}
}
