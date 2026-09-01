// Throwaway integration harness for NativeCore.dylib. Run with sudo.
// Usage: sudo ./test/harness ../build/debug/NativeCore.dylib
#include <dlfcn.h>
#include <unistd.h>
#include <sys/wait.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "../../ReClassNET_Plugin.hpp"

static int failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { std::fprintf(stderr, "FAIL: %s\n", msg); ++failures; } else { std::printf("ok: %s\n", msg); } } while (0)

using EnumerateProcessesFn = void(*)(EnumerateProcessCallback);
using OpenRemoteProcessFn = RC_Pointer(*)(RC_Pointer, ProcessAccess);
using CloseRemoteProcessFn = void(*)(RC_Pointer);
using IsProcessValidFn = bool(*)(RC_Pointer);
using ReadRemoteMemoryFn = bool(*)(RC_Pointer, RC_Pointer, RC_Pointer, int, int);
using WriteRemoteMemoryFn = bool(*)(RC_Pointer, RC_Pointer, RC_Pointer, int, int);
using EnumerateRemoteSectionsAndModulesFn = void(*)(RC_Pointer, EnumerateRemoteSectionsCallback, EnumerateRemoteModulesCallback);
using ControlRemoteProcessFn = void(*)(RC_Pointer, ControlRemoteProcessAction);

static std::vector<EnumerateProcessData> g_procs;
static void OnProcess(EnumerateProcessData* d) { g_procs.push_back(*d); }

static std::vector<EnumerateRemoteModuleData> g_modules;
static void OnModule(EnumerateRemoteModuleData* d) { g_modules.push_back(*d); }
static std::vector<EnumerateRemoteSectionData> g_sections;
static void OnSection(EnumerateRemoteSectionData* d) { g_sections.push_back(*d); }

static std::string U16ToAscii(const RC_UnicodeChar* s)
{
	std::string out;
	for (; *s; ++s) out.push_back(static_cast<char>(*s & 0x7f));
	return out;
}

template<typename T>
static T Sym(void* lib, const char* name)
{
	auto p = reinterpret_cast<T>(dlsym(lib, name));
	if (!p) { std::fprintf(stderr, "missing export %s\n", name); std::exit(2); }
	return p;
}

int main(int argc, char** argv)
{
	if (argc < 2) { std::fprintf(stderr, "usage: harness <path to NativeCore.dylib>\n"); return 2; }
	void* lib = dlopen(argv[1], RTLD_NOW);
	if (!lib) { std::fprintf(stderr, "dlopen: %s\n", dlerror()); return 2; }

	auto EnumerateProcesses = Sym<EnumerateProcessesFn>(lib, "EnumerateProcesses");
	auto OpenRemoteProcess = Sym<OpenRemoteProcessFn>(lib, "OpenRemoteProcess");
	auto CloseRemoteProcess = Sym<CloseRemoteProcessFn>(lib, "CloseRemoteProcess");
	auto IsProcessValid = Sym<IsProcessValidFn>(lib, "IsProcessValid");
	auto ReadRemoteMemory = Sym<ReadRemoteMemoryFn>(lib, "ReadRemoteMemory");
	auto WriteRemoteMemory = Sym<WriteRemoteMemoryFn>(lib, "WriteRemoteMemory");
	auto EnumerateRemoteSectionsAndModules = Sym<EnumerateRemoteSectionsAndModulesFn>(lib, "EnumerateRemoteSectionsAndModules");
	auto ControlRemoteProcess = Sym<ControlRemoteProcessFn>(lib, "ControlRemoteProcess");

	// Spawn a child to inspect.
	pid_t child = fork();
	if (child == 0) { execl("/bin/sleep", "sleep", "60", nullptr); _exit(127); }
	usleep(200 * 1000);
	auto handleId = reinterpret_cast<RC_Pointer>(static_cast<intptr_t>(child));

	// --- Task 2 checks ---
	auto handle = OpenRemoteProcess(handleId, ProcessAccess::Full);
	CHECK(handle == handleId, "OpenRemoteProcess returns pid as handle");
	CHECK(IsProcessValid(handle), "IsProcessValid true for live child");

	ControlRemoteProcess(handle, ControlRemoteProcessAction::Suspend);
	ControlRemoteProcess(handle, ControlRemoteProcessAction::Resume);
	CHECK(IsProcessValid(handle), "child still valid after suspend/resume");

	// --- Task 3 checks ---
	EnumerateProcesses(OnProcess);
	bool found = false;
	for (auto& p : g_procs) if (p.Id == static_cast<RC_Size>(child)) { found = true; CHECK(U16ToAscii(p.Name) == "sleep", "child name is sleep"); CHECK(U16ToAscii(p.Path) == "/bin/sleep", "child path is /bin/sleep"); }
	CHECK(found, "EnumerateProcesses lists child");

	// --- Task 5 checks ---
	EnumerateRemoteSectionsAndModules(handle, OnSection, OnModule);
	CHECK(!g_modules.empty(), "modules enumerated");
	CHECK(!g_sections.empty(), "sections enumerated");
	RC_Pointer sleepBase = nullptr;
	bool sawLibSystem = false;
	for (auto& m : g_modules)
	{
		auto path = U16ToAscii(m.Path);
		if (path == "/bin/sleep") sleepBase = m.BaseAddress;
		if (path.find("libSystem.B.dylib") != std::string::npos) sawLibSystem = true;
		CHECK(m.Size > 0, "module size > 0");
	}
	CHECK(sleepBase != nullptr, "main executable module found");
	CHECK(sawLibSystem, "libSystem module found");
	bool anyOverlap = false;
	for (size_t i = 0; i < g_modules.size() && !anyOverlap; ++i)
	{
		auto aStart = reinterpret_cast<uint64_t>(g_modules[i].BaseAddress);
		auto aEnd = aStart + static_cast<uint64_t>(g_modules[i].Size);
		for (size_t j = i + 1; j < g_modules.size(); ++j)
		{
			auto bStart = reinterpret_cast<uint64_t>(g_modules[j].BaseAddress);
			auto bEnd = bStart + static_cast<uint64_t>(g_modules[j].Size);
			if (aStart < bEnd && bStart < aEnd) { anyOverlap = true; break; }
		}
	}
	CHECK(!anyOverlap, "module ranges do not overlap");
	bool sawCode = false, sawImageSection = false;
	for (auto& s : g_sections) { if (s.Category == SectionCategory::CODE) sawCode = true; if (s.Type == SectionType::Image) sawImageSection = true; }
	CHECK(sawCode, "at least one CODE section");
	CHECK(sawImageSection, "at least one Image section");

	// --- Task 4 checks ---
	uint32_t magic = 0;
	CHECK(ReadRemoteMemory(handle, sleepBase, &magic, 0, sizeof(magic)), "ReadRemoteMemory at image base");
	CHECK(magic == 0xfeedfacf, "Mach-O 64 magic read");
	uint8_t junk[4];
	CHECK(!ReadRemoteMemory(handle, reinterpret_cast<RC_Pointer>(0x10), junk, 0, 4), "read of unmapped address fails");

	// Find a writable DATA section inside the main image and round-trip a value.
	RC_Pointer dataAddr = nullptr;
	for (auto& s : g_sections)
	{
		if (s.Category == SectionCategory::DATA && U16ToAscii(s.ModulePath) == "/bin/sleep") { dataAddr = s.BaseAddress; break; }
	}
	CHECK(dataAddr != nullptr, "found writable DATA section in main image");
	if (dataAddr)
	{
		uint64_t original = 0, written = 0x4142434445464748ULL, back = 0;
		CHECK(ReadRemoteMemory(handle, dataAddr, &original, 0, 8), "read original data");
		CHECK(WriteRemoteMemory(handle, dataAddr, &written, 0, 8), "WriteRemoteMemory succeeds");
		CHECK(ReadRemoteMemory(handle, dataAddr, &back, 0, 8) && back == written, "write round-trips");
		WriteRemoteMemory(handle, dataAddr, &original, 0, 8);
	}
	// Write into a read-only __TEXT page must also succeed via protect/retry.
	{
		uint32_t before = 0, after = 0;
		ReadRemoteMemory(handle, sleepBase, &before, 0, 4);
		CHECK(WriteRemoteMemory(handle, sleepBase, &before, 0, 4), "write to read-only page succeeds (protect+retry)");
		ReadRemoteMemory(handle, sleepBase, &after, 0, 4);
		CHECK(before == after, "read-only page write preserved value");
	}

	ControlRemoteProcess(handleId, ControlRemoteProcessAction::Terminate);
	int status = 0; waitpid(child, &status, 0);
	CHECK(!IsProcessValid(handleId), "child invalid after terminate");

	// pid recycling guard: even though CloseRemoteProcess has not been called
	// yet, ReadRemoteMemory must not silently keep serving the stale cached
	// task port for a pid that no longer refers to the same process.
	uint8_t deadRead[4] = {};
	CHECK(!ReadRemoteMemory(handle, sleepBase, deadRead, 0, sizeof(deadRead)), "ReadRemoteMemory fails for terminated/reaped pid");

	CloseRemoteProcess(handle);

	std::printf("%s (%d failures)\n", failures ? "FAILED" : "PASSED", failures);
	return failures ? 1 : 0;
}
