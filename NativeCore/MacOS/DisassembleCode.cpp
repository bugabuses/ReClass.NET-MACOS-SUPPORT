#include "../Shared/DistormHelper.hpp"

extern "C" bool RC_CallConv DisassembleCode(RC_Pointer address, RC_Size length, RC_Pointer virtualAddress, bool determineStaticInstructionBytes, EnumerateInstructionCallback callback)
{
	try
	{
		return DisassembleInstructionsImpl(address, length, virtualAddress, determineStaticInstructionBytes, callback);
	}
	catch (...)
	{
		return false;
	}
}
