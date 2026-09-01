#include "NativeCore.hpp"
#include "../Shared/Keys.hpp"

extern "C" RC_Pointer RC_CallConv InitializeInput()
{
	return nullptr;
}

extern "C" bool RC_CallConv GetPressedKeys(RC_Pointer handle, Keys* state[], int* count)
{
	return false;
}

extern "C" void RC_CallConv ReleaseInput(RC_Pointer handle)
{
}
