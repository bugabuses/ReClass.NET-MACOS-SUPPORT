# macOS Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ReClass.NET builds and runs on Apple Silicon macOS under Mono/XQuartz with a new Mach-based native core, so a user can attach to a process (as root), list modules, and read/write memory.

**Architecture:** New `NativeCore/MacOS/` C++17 library exporting the same C ABI as `NativeCore/Unix/` (`NativeCore.hpp`), built with clang into `NativeCore.dylib`. Handle = pid; a side map holds the `task_for_pid` port. C# side gains `IsMacOS()` and loads `NativeCore.dylib`. Debugger/input exports are stubs. Build via `xbuild` + Makefile; run via `run-macos.sh` under `sudo`.

**Tech Stack:** C++17 (clang, Mach VM APIs, libproc, dyld image infos), distorm (vendored), C# / .NET Framework 4.7.2 under Mono 6.14, WinForms X11 backend (XQuartz), GNU make, xbuild.

**Spec:** `docs/superpowers/specs/2026-09-01-macos-port-design.md`

## Global Constraints

- Only arm64 host build. Define `RECLASSNET64=1`. Pointer size 8.
- Native output file name: `NativeCore.dylib`. Export names/signatures exactly as in `NativeCore/Unix/NativeCore.hpp`.
- Handle returned by `OpenRemoteProcess` is the **pid** cast to `RC_Pointer`, same as Linux.
- All structs are `#pragma pack(1)` (from `NativeCore/ReClassNET_Plugin.hpp`). Never redefine them.
- Debugger exports return `false` / no-op. Input exports are stubs. Do not implement these.
- No arm64 disassembly gating in C# (ABI unchanged).
- Root repo for git commands: the repository root. Branch: `master`. Commit after every task.
- Tests needing `task_for_pid` must be run with `sudo`. Plan steps say so explicitly.
- Every commit message ends with:
  ```
  Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019Ji19vNcqX3M9TSERhAg4s
  ```

---

## File map

| File | Responsibility |
|---|---|
| `NativeCore/ReClassNET_Plugin.hpp` (modify line 9-15) | Add `__APPLE__` to `RC_CallConv` selection. |
| `NativeCore/MacOS/NativeCore.hpp` | Same content as Unix header (export decls + `is_number`/`parse_type`). |
| `NativeCore/MacOS/TaskPorts.hpp/.cpp` | pid → `mach_port_t` registry: `Register`, `Lookup`, `Remove`. |
| `NativeCore/MacOS/EnumerateProcesses.cpp` | `proc_listpids` + `proc_pidpath`. |
| `NativeCore/MacOS/OpenRemoteProcess.cpp` | `task_for_pid`, register port, return pid. |
| `NativeCore/MacOS/CloseRemoteProcess.cpp` | Deallocate port, remove entry. |
| `NativeCore/MacOS/IsProcessValid.cpp` | `kill(pid, 0)`. |
| `NativeCore/MacOS/ReadRemoteMemory.cpp` | `mach_vm_read_overwrite`. |
| `NativeCore/MacOS/WriteRemoteMemory.cpp` | `mach_vm_write` with protect/retry/restore. |
| `NativeCore/MacOS/EnumerateRemoteSectionsAndModules.cpp` | dyld image walk + `mach_vm_region_recurse`. |
| `NativeCore/MacOS/ControlRemoteProcess.cpp` | `task_suspend`/`task_resume`/`SIGKILL`. |
| `NativeCore/MacOS/Debugger.cpp` | Stubs. |
| `NativeCore/MacOS/DisassembleCode.cpp` | Delegates to `../Shared/DistormHelper`. |
| `NativeCore/MacOS/Input.cpp` | Stubs. |
| `NativeCore/MacOS/Makefile` | `debug`, `release`, `test`, `clean`. |
| `NativeCore/MacOS/test/harness.cpp` | Throwaway integration harness (dlopen + fork `sleep`). |
| `ReClass.NET/Native/NativeMethods.cs` | Add `IsMacOS()`. |
| `ReClass.NET/Core/InternalCoreFunctions.cs` | Load `NativeCore.dylib` on macOS. |
| `ReClass.NET/Native/NativeMethods.Unix.cs` | Only if `__Internal` dlopen fails on Mono/macOS. |
| `Makefile` (root) | `macos_*` targets. |
| `run-macos.sh` | Launch script. |
| `README.md` | macOS section. |

---

### Task 1: Native scaffold — header, RC_CallConv, Makefile, stub exports

Deliverable: `NativeCore.dylib` builds and exports all symbols (stubs for now).

**Files:**
- Modify: `NativeCore/ReClassNET_Plugin.hpp:9-15`
- Create: `NativeCore/MacOS/NativeCore.hpp`, `NativeCore/MacOS/Makefile`, `NativeCore/MacOS/Debugger.cpp`, `NativeCore/MacOS/Input.cpp`, `NativeCore/MacOS/DisassembleCode.cpp`, `NativeCore/MacOS/IsProcessValid.cpp`, `NativeCore/MacOS/ControlRemoteProcess.cpp`
- Create (temporary stubs, replaced in later tasks): `EnumerateProcesses.cpp`, `OpenRemoteProcess.cpp`, `CloseRemoteProcess.cpp`, `ReadRemoteMemory.cpp`, `WriteRemoteMemory.cpp`, `EnumerateRemoteSectionsAndModules.cpp`

**Interfaces:**
- Produces: `NativeCore/MacOS/build/{debug,release}/NativeCore.dylib` exporting every function declared in `NativeCore.hpp`.

- [ ] **Step 1: Patch `RC_CallConv` for Apple**

Edit `NativeCore/ReClassNET_Plugin.hpp` lines 9–15 to:

```cpp
#if defined(__linux__) || defined(__APPLE__)
	#define RC_CallConv
#elif _WIN32
	#define RC_CallConv __stdcall
#else
	static_assert(false, "Missing RC_CallConv specification");
#endif
```

- [ ] **Step 2: Create `NativeCore/MacOS/NativeCore.hpp`**

Copy `NativeCore/Unix/NativeCore.hpp` verbatim:

```bash
cp NativeCore/Unix/NativeCore.hpp NativeCore/MacOS/NativeCore.hpp
```

- [ ] **Step 3: Create stub/simple sources**

`NativeCore/MacOS/Debugger.cpp`:
```cpp
#include "NativeCore.hpp"

// Hardware-breakpoint debugging is not supported on macOS in this port.
// macOS ptrace exposes no debug-register access; a Mach exception-port
// implementation is a later phase.

extern "C" bool RC_CallConv AttachDebuggerToProcess(RC_Pointer id)
{
	return false;
}

extern "C" void RC_CallConv DetachDebuggerFromProcess(RC_Pointer id)
{
}

extern "C" bool RC_CallConv AwaitDebugEvent(DebugEvent* evt, int timeoutInMilliseconds)
{
	return false;
}

extern "C" void RC_CallConv HandleDebugEvent(DebugEvent* evt)
{
}

extern "C" bool RC_CallConv SetHardwareBreakpoint(RC_Pointer id, RC_Pointer address, HardwareBreakpointRegister reg, HardwareBreakpointTrigger type, HardwareBreakpointSize size, bool set)
{
	return false;
}
```

`NativeCore/MacOS/Input.cpp`:
```cpp
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
```

`NativeCore/MacOS/DisassembleCode.cpp`:
```cpp
#include "../Shared/DistormHelper.hpp"

extern "C" bool RC_CallConv DisassembleCode(RC_Pointer address, RC_Size length, RC_Pointer virtualAddress, bool determineStaticInstructionBytes, EnumerateInstructionCallback callback)
{
	return DisassembleInstructionsImpl(address, length, virtualAddress, determineStaticInstructionBytes, callback);
}
```

`NativeCore/MacOS/IsProcessValid.cpp`:
```cpp
#include <sys/types.h>
#include <signal.h>

#include "NativeCore.hpp"

extern "C" bool RC_CallConv IsProcessValid(RC_Pointer handle)
{
	return kill(static_cast<pid_t>(reinterpret_cast<intptr_t>(handle)), 0) == 0;
}
```

`NativeCore/MacOS/ControlRemoteProcess.cpp` (final version; uses TaskPorts from Task 2 — until then, keep the `kill`-only body below and upgrade in Task 2):
```cpp
#include <csignal>

#include "NativeCore.hpp"

extern "C" void RC_CallConv ControlRemoteProcess(RC_Pointer handle, ControlRemoteProcessAction action)
{
	const auto pid = static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));

	int signal = SIGKILL;
	if (action == ControlRemoteProcessAction::Suspend)
	{
		signal = SIGSTOP;
	}
	else if (action == ControlRemoteProcessAction::Resume)
	{
		signal = SIGCONT;
	}

	kill(pid, signal);
}
```

Temporary stubs (each its own file; bodies replaced in Tasks 3–6):

`EnumerateProcesses.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" void RC_CallConv EnumerateProcesses(EnumerateProcessCallback callbackProcess) {}
```
`OpenRemoteProcess.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" RC_Pointer RC_CallConv OpenRemoteProcess(RC_Pointer id, ProcessAccess desiredAccess) { return nullptr; }
```
`CloseRemoteProcess.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" void RC_CallConv CloseRemoteProcess(RC_Pointer handle) {}
```
`ReadRemoteMemory.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" bool RC_CallConv ReadRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size) { return false; }
```
`WriteRemoteMemory.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" bool RC_CallConv WriteRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size) { return false; }
```
`EnumerateRemoteSectionsAndModules.cpp`:
```cpp
#include "NativeCore.hpp"
extern "C" void RC_CallConv EnumerateRemoteSectionsAndModules(RC_Pointer handle, EnumerateRemoteSectionsCallback callbackSection, EnumerateRemoteModulesCallback callbackModule) {}
```

- [ ] **Step 4: Create `NativeCore/MacOS/Makefile`**

```make
CXX = clang++
CC = clang
ARCH = -arch arm64
STD = -std=c++17
INC = -I../Dependencies/distorm/include
DEFS = -DRECLASSNET64=1
WARN = -Wall -Wno-deprecated-declarations
CXXFLAGS_COMMON = $(ARCH) $(STD) -fPIC $(WARN) $(DEFS) $(INC)
CFLAGS_COMMON = $(ARCH) -fPIC -Wall $(DEFS) $(INC)
LDFLAGS = $(ARCH) -dynamiclib -shared -Wl,-undefined,error

SRCS_CPP = WriteRemoteMemory.cpp ReadRemoteMemory.cpp OpenRemoteProcess.cpp IsProcessValid.cpp Input.cpp \
           EnumerateRemoteSectionsAndModules.cpp EnumerateProcesses.cpp DisassembleCode.cpp Debugger.cpp \
           ControlRemoteProcess.cpp CloseRemoteProcess.cpp TaskPorts.cpp
SHARED_CPP = ../Shared/DistormHelper.cpp
DISTORM_C = decoder.c distorm.c instructions.c insts.c mnemonics.c operands.c prefix.c textdefs.c

define BUILD_RULES
OBJDIR_$(1) = obj/$(1)
OUTDIR_$(1) = build/$(1)
OUT_$(1) = $$(OUTDIR_$(1))/NativeCore.dylib
OBJS_$(1) = $$(addprefix $$(OBJDIR_$(1))/,$$(SRCS_CPP:.cpp=.o)) \
            $$(OBJDIR_$(1))/DistormHelper.o \
            $$(addprefix $$(OBJDIR_$(1))/,$$(DISTORM_C:.c=.o))

$(1): $$(OUT_$(1))

$$(OUT_$(1)): $$(OBJS_$(1))
	@mkdir -p $$(OUTDIR_$(1))
	$$(CXX) $$(LDFLAGS) -o $$@ $$^

$$(OBJDIR_$(1))/%.o: %.cpp NativeCore.hpp
	@mkdir -p $$(OBJDIR_$(1))
	$$(CXX) $$(CXXFLAGS_COMMON) $(2) -c $$< -o $$@

$$(OBJDIR_$(1))/DistormHelper.o: ../Shared/DistormHelper.cpp
	@mkdir -p $$(OBJDIR_$(1))
	$$(CXX) $$(CXXFLAGS_COMMON) $(2) -c $$< -o $$@

$$(OBJDIR_$(1))/%.o: ../Dependencies/distorm/src/%.c
	@mkdir -p $$(OBJDIR_$(1))
	$$(CC) $$(CFLAGS_COMMON) $(2) -c $$< -o $$@

clean_$(1):
	rm -rf $$(OBJDIR_$(1)) $$(OUTDIR_$(1))
endef

$(eval $(call BUILD_RULES,debug,-g -O0))
$(eval $(call BUILD_RULES,release,-O2))

all: debug release

clean: clean_debug clean_release
	rm -rf obj build test/harness

test: debug test/harness

test/harness: test/harness.cpp
	$(CXX) $(ARCH) $(STD) -Wall $(DEFS) -o $@ $<

.PHONY: all clean debug release clean_debug clean_release test
```

Note: `TaskPorts.cpp` is referenced but created in Task 2. For this task to build, create an empty placeholder:

```bash
printf '// populated in Task 2\n' > NativeCore/MacOS/TaskPorts.cpp
```

- [ ] **Step 5: Build**

Run: `cd NativeCore/MacOS && make release`
Expected: `build/release/NativeCore.dylib` produced, no errors. If distorm `.c` files warn, fine; errors are not.

- [ ] **Step 6: Verify exports**

Run: `nm -gU NativeCore/MacOS/build/release/NativeCore.dylib | grep -c ' T _'`
Expected: at least 19 (all 19 exports listed in `NativeCore.hpp` plus `DisassembleCode`).

Run: `nm -gU NativeCore/MacOS/build/release/NativeCore.dylib | grep -E '_(EnumerateProcesses|OpenRemoteProcess|ReadRemoteMemory|WriteRemoteMemory|EnumerateRemoteSectionsAndModules|DisassembleCode|SetHardwareBreakpoint|GetPressedKeys)$' | wc -l`
Expected: `8`

- [ ] **Step 7: Add `.gitignore` entries and commit**

Append to `.gitignore`:
```
NativeCore/MacOS/obj/
NativeCore/MacOS/build/
NativeCore/MacOS/test/harness
```

```bash
git add .gitignore NativeCore/ReClassNET_Plugin.hpp NativeCore/MacOS
git commit -m "NativeCore: add macOS scaffold with stub exports and clang Makefile"
```

---

### Task 2: TaskPorts registry + OpenRemoteProcess / CloseRemoteProcess / ControlRemoteProcess

Deliverable: `task_for_pid` wired; handle == pid; suspend/resume via task calls.

**Files:**
- Create: `NativeCore/MacOS/TaskPorts.hpp`, `NativeCore/MacOS/TaskPorts.cpp`
- Modify: `NativeCore/MacOS/OpenRemoteProcess.cpp`, `NativeCore/MacOS/CloseRemoteProcess.cpp`, `NativeCore/MacOS/ControlRemoteProcess.cpp`
- Create: `NativeCore/MacOS/test/harness.cpp` (first version; grows in later tasks)

**Interfaces:**
- Produces:
  ```cpp
  namespace TaskPorts {
      // Returns MACH_PORT_NULL if pid unknown and acquisition fails.
      mach_port_t Get(pid_t pid);          // lookup; lazily task_for_pid if absent
      bool Acquire(pid_t pid);             // task_for_pid + store; false on failure
      void Release(pid_t pid);             // deallocate + erase
  }
  inline pid_t HandleToPid(RC_Pointer h) { return static_cast<pid_t>(reinterpret_cast<intptr_t>(h)); }
  ```

- [ ] **Step 1: Write harness (failing)**

`NativeCore/MacOS/test/harness.cpp`:

```cpp
// Throwaway integration harness for NativeCore.dylib. Run with sudo.
// Usage: sudo ./test/harness ../build/debug/NativeCore.dylib
#include <dlfcn.h>
#include <unistd.h>
#include <signal.h>
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

	CloseRemoteProcess(handle);
	ControlRemoteProcess(handleId, ControlRemoteProcessAction::Terminate);
	int status = 0; waitpid(child, &status, 0);
	CHECK(!IsProcessValid(handleId), "child invalid after terminate");

	std::printf("%s (%d failures)\n", failures ? "FAILED" : "PASSED", failures);
	return failures ? 1 : 0;
}
```

- [ ] **Step 2: Build harness and run — expect failures**

Run:
```bash
cd NativeCore/MacOS && make test && sudo ./test/harness build/debug/NativeCore.dylib
```
Expected: `FAIL: OpenRemoteProcess returns pid as handle` and many other FAILs (stubs). Exit code 1.

- [ ] **Step 3: Implement TaskPorts**

`NativeCore/MacOS/TaskPorts.hpp`:
```cpp
#pragma once

#include <mach/mach.h>
#include <sys/types.h>

#include "NativeCore.hpp"

namespace TaskPorts
{
	// Returns the cached task port for pid, acquiring it via task_for_pid if not cached.
	// Returns MACH_PORT_NULL on failure.
	mach_port_t Get(pid_t pid);

	// task_for_pid + cache. Logs to stderr on failure.
	bool Acquire(pid_t pid);

	// Deallocates and forgets the port for pid. No-op if unknown.
	void Release(pid_t pid);
}

inline pid_t HandleToPid(RC_Pointer handle)
{
	return static_cast<pid_t>(reinterpret_cast<intptr_t>(handle));
}
```

`NativeCore/MacOS/TaskPorts.cpp`:
```cpp
#include <cstdio>
#include <mutex>
#include <unordered_map>
#include <mach/mach_error.h>

#include "TaskPorts.hpp"

namespace
{
	std::mutex g_mutex;
	std::unordered_map<pid_t, mach_port_t> g_ports;
}

namespace TaskPorts
{
	bool Acquire(pid_t pid)
	{
		mach_port_t task = MACH_PORT_NULL;
		const auto kr = task_for_pid(mach_task_self(), pid, &task);
		if (kr != KERN_SUCCESS)
		{
			std::fprintf(stderr, "ReClass.NET NativeCore: task_for_pid(%d) failed: %s (run as root; SIP-protected processes cannot be attached)\n", pid, mach_error_string(kr));
			return false;
		}

		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			mach_port_deallocate(mach_task_self(), it->second);
			it->second = task;
		}
		else
		{
			g_ports.emplace(pid, task);
		}
		return true;
	}

	mach_port_t Get(pid_t pid)
	{
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_ports.find(pid);
			if (it != g_ports.end())
			{
				return it->second;
			}
		}

		if (!Acquire(pid))
		{
			return MACH_PORT_NULL;
		}

		std::lock_guard<std::mutex> lock(g_mutex);
		return g_ports[pid];
	}

	void Release(pid_t pid)
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		auto it = g_ports.find(pid);
		if (it != g_ports.end())
		{
			mach_port_deallocate(mach_task_self(), it->second);
			g_ports.erase(it);
		}
	}
}
```

- [ ] **Step 4: Implement Open/Close/Control**

`NativeCore/MacOS/OpenRemoteProcess.cpp`:
```cpp
#include "TaskPorts.hpp"

extern "C" RC_Pointer RC_CallConv OpenRemoteProcess(RC_Pointer id, ProcessAccess desiredAccess)
{
	const auto pid = HandleToPid(id);
	if (!TaskPorts::Acquire(pid))
	{
		return nullptr;
	}
	return id;
}
```

`NativeCore/MacOS/CloseRemoteProcess.cpp`:
```cpp
#include "TaskPorts.hpp"

extern "C" void RC_CallConv CloseRemoteProcess(RC_Pointer handle)
{
	TaskPorts::Release(HandleToPid(handle));
}
```

`NativeCore/MacOS/ControlRemoteProcess.cpp` (replace body):
```cpp
#include <csignal>

#include "TaskPorts.hpp"

extern "C" void RC_CallConv ControlRemoteProcess(RC_Pointer handle, ControlRemoteProcessAction action)
{
	const auto pid = HandleToPid(handle);

	if (action == ControlRemoteProcessAction::Terminate)
	{
		kill(pid, SIGKILL);
		return;
	}

	const auto task = TaskPorts::Get(pid);
	if (task == MACH_PORT_NULL)
	{
		// Fall back to signals if we have no task port.
		kill(pid, action == ControlRemoteProcessAction::Suspend ? SIGSTOP : SIGCONT);
		return;
	}

	if (action == ControlRemoteProcessAction::Suspend)
	{
		task_suspend(task);
	}
	else if (action == ControlRemoteProcessAction::Resume)
	{
		task_resume(task);
	}
}
```

- [ ] **Step 5: Build and run harness**

Run: `cd NativeCore/MacOS && make test && sudo ./test/harness build/debug/NativeCore.dylib`
Expected: `ok:` for "OpenRemoteProcess returns pid as handle", "IsProcessValid true for live child", "child still valid after suspend/resume", "child invalid after terminate". Task 3/4/5 checks still FAIL.

If `task_for_pid` fails even under sudo: confirm `csrutil status` and that the child is `/bin/sleep` (Apple-signed binaries **can** be attached by root unless hardened runtime forbids; `sleep` is not hardened). If it still fails, switch the harness child to a self-compiled binary: add `test/child.c` containing `int main(){for(;;)sleep(1);}` built by the `test` Makefile target, and exec that instead. Document which one was needed in the commit message.

- [ ] **Step 6: Commit**

```bash
git add NativeCore/MacOS
git commit -m "NativeCore/MacOS: task_for_pid registry, open/close/control process, test harness"
```

---

### Task 3: EnumerateProcesses

**Files:**
- Modify: `NativeCore/MacOS/EnumerateProcesses.cpp`

**Interfaces:**
- Consumes: `EnumerateProcessData`, `MultiByteToUnicode`, `str16cpy` from `ReClassNET_Plugin.hpp`.

- [ ] **Step 1: Run harness — confirm "EnumerateProcesses lists child" FAILs**

Run: `cd NativeCore/MacOS && sudo ./test/harness build/debug/NativeCore.dylib | grep EnumerateProcesses`
Expected: `FAIL: EnumerateProcesses lists child`

- [ ] **Step 2: Implement**

```cpp
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
```

Note: `MultiByteToUnicode` uses `std::codecvt_utf8_utf16`, deprecated in C++17 — the Makefile passes `-Wno-deprecated-declarations`. If clang errors (not warns), add `-D_LIBCPP_DISABLE_DEPRECATION_WARNINGS` to `DEFS` in the Makefile.

- [ ] **Step 3: Build and run**

Run: `cd NativeCore/MacOS && make debug && sudo ./test/harness build/debug/NativeCore.dylib | grep -E 'EnumerateProcesses|child name|child path'`
Expected: three `ok:` lines.

- [ ] **Step 4: Commit**

```bash
git add NativeCore/MacOS/EnumerateProcesses.cpp NativeCore/MacOS/Makefile
git commit -m "NativeCore/MacOS: enumerate processes via libproc"
```

---

### Task 4: ReadRemoteMemory / WriteRemoteMemory

**Files:**
- Modify: `NativeCore/MacOS/ReadRemoteMemory.cpp`, `NativeCore/MacOS/WriteRemoteMemory.cpp`

**Interfaces:**
- Consumes: `TaskPorts::Get`, `HandleToPid`.

- [ ] **Step 1: Confirm harness read/write checks FAIL**

Run: `cd NativeCore/MacOS && sudo ./test/harness build/debug/NativeCore.dylib | grep -E 'ReadRemoteMemory|WriteRemoteMemory|round-trips|Mach-O'`
Expected: FAIL lines (note: these depend on Task 5's module list for `sleepBase`; if `sleepBase` is null the checks fail for that reason — that's fine, Task 5 fixes it. To test this task in isolation, temporarily read the harness's own `child` base is not available; proceed and rely on Task 5.)

- [ ] **Step 2: Implement read**

`ReadRemoteMemory.cpp`:
```cpp
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
```

- [ ] **Step 3: Implement write with protect/retry/restore**

`WriteRemoteMemory.cpp`:
```cpp
#include <mach/mach.h>
#include <mach/mach_vm.h>

#include "TaskPorts.hpp"

namespace
{
	bool TryWrite(mach_port_t task, mach_vm_address_t address, const void* data, int size)
	{
		const auto kr = mach_vm_write(task, address, reinterpret_cast<vm_offset_t>(data), static_cast<mach_msg_type_number_t>(size));
		return kr == KERN_SUCCESS;
	}

	// Queries the protection of the region containing address.
	bool QueryProtection(mach_port_t task, mach_vm_address_t address, vm_prot_t& protection)
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
		return true;
	}
}

extern "C" bool RC_CallConv WriteRemoteMemory(RC_Pointer handle, RC_Pointer address, RC_Pointer buffer, int offset, int size)
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
	const bool haveOriginal = QueryProtection(task, target, original);

	if (mach_vm_protect(task, pageStart, pageLen, FALSE, VM_PROT_READ | VM_PROT_WRITE | VM_PROT_COPY) != KERN_SUCCESS)
	{
		return false;
	}

	const bool ok = TryWrite(task, target, data, size);

	if (haveOriginal)
	{
		mach_vm_protect(task, pageStart, pageLen, FALSE, original);
	}

	return ok;
}
```

- [ ] **Step 4: Build**

Run: `cd NativeCore/MacOS && make debug`
Expected: no errors.

- [ ] **Step 5: Commit** (harness verification happens after Task 5 supplies module bases)

```bash
git add NativeCore/MacOS/ReadRemoteMemory.cpp NativeCore/MacOS/WriteRemoteMemory.cpp
git commit -m "NativeCore/MacOS: read/write remote memory via mach_vm"
```

---

### Task 5: EnumerateRemoteSectionsAndModules

**Files:**
- Modify: `NativeCore/MacOS/EnumerateRemoteSectionsAndModules.cpp`

**Interfaces:**
- Consumes: `TaskPorts::Get`, `HandleToPid`; emits `EnumerateRemoteSectionData` / `EnumerateRemoteModuleData`.

- [ ] **Step 1: Confirm harness module/section checks FAIL**

Run: `cd NativeCore/MacOS && sudo ./test/harness build/debug/NativeCore.dylib | grep -E 'modules enumerated|sections enumerated|libSystem|main executable'`
Expected: FAIL lines.

- [ ] **Step 2: Implement**

```cpp
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach-o/dyld_images.h>
#include <mach-o/loader.h>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>

#include "TaskPorts.hpp"

namespace
{
	struct Segment
	{
		uint64_t Start;
		uint64_t End;
		char Name[16];
	};

	struct Module
	{
		uint64_t Base = 0;
		uint64_t Size = 0;
		std::string Path;
		std::vector<Segment> Segments;
	};

	bool ReadRemote(mach_port_t task, uint64_t address, void* out, size_t size)
	{
		mach_vm_size_t outSize = 0;
		const auto kr = mach_vm_read_overwrite(task, address, size, reinterpret_cast<mach_vm_address_t>(out), &outSize);
		return kr == KERN_SUCCESS && outSize == size;
	}

	std::string ReadRemoteString(mach_port_t task, uint64_t address, size_t maxLength)
	{
		std::string result;
		char chunk[64];
		while (result.size() < maxLength)
		{
			if (!ReadRemote(task, address + result.size(), chunk, sizeof(chunk)))
			{
				// Fall back to byte-wise read near page boundaries.
				char c = 0;
				if (!ReadRemote(task, address + result.size(), &c, 1) || c == 0) break;
				result.push_back(c);
				continue;
			}
			const auto nul = static_cast<const char*>(std::memchr(chunk, 0, sizeof(chunk)));
			if (nul != nullptr)
			{
				result.append(chunk, nul - chunk);
				break;
			}
			result.append(chunk, sizeof(chunk));
		}
		return result;
	}

	// Parses a Mach-O 64 header at base, filling segments and total size.
	bool ParseImage(mach_port_t task, uint64_t base, Module& module)
	{
		mach_header_64 header = {};
		if (!ReadRemote(task, base, &header, sizeof(header)) || header.magic != MH_MAGIC_64 || header.ncmds > 4096)
		{
			return false;
		}

		std::vector<uint8_t> commands(header.sizeofcmds);
		if (header.sizeofcmds == 0 || header.sizeofcmds > 1024 * 1024 || !ReadRemote(task, base + sizeof(header), commands.data(), commands.size()))
		{
			return false;
		}

		// Slide = actual base - preferred __TEXT vmaddr.
		uint64_t textVmAddr = 0;
		bool haveText = false;
		size_t offset = 0;
		for (uint32_t i = 0; i < header.ncmds && offset + sizeof(load_command) <= commands.size(); ++i)
		{
			const auto* lc = reinterpret_cast<const load_command*>(commands.data() + offset);
			if (lc->cmdsize < sizeof(load_command) || offset + lc->cmdsize > commands.size()) break;
			if (lc->cmd == LC_SEGMENT_64)
			{
				const auto* seg = reinterpret_cast<const segment_command_64*>(lc);
				if (std::strncmp(seg->segname, "__TEXT", 16) == 0) { textVmAddr = seg->vmaddr; haveText = true; }
			}
			offset += lc->cmdsize;
		}
		if (!haveText) return false;
		const uint64_t slide = base - textVmAddr;

		uint64_t lowest = UINT64_MAX, highest = 0;
		offset = 0;
		for (uint32_t i = 0; i < header.ncmds && offset + sizeof(load_command) <= commands.size(); ++i)
		{
			const auto* lc = reinterpret_cast<const load_command*>(commands.data() + offset);
			if (lc->cmdsize < sizeof(load_command) || offset + lc->cmdsize > commands.size()) break;
			if (lc->cmd == LC_SEGMENT_64)
			{
				const auto* seg = reinterpret_cast<const segment_command_64*>(lc);
				if (std::strncmp(seg->segname, "__PAGEZERO", 16) != 0 && seg->vmsize > 0)
				{
					Segment s = {};
					s.Start = seg->vmaddr + slide;
					s.End = s.Start + seg->vmsize;
					std::strncpy(s.Name, seg->segname, 16);
					module.Segments.push_back(s);
					lowest = std::min(lowest, s.Start);
					highest = std::max(highest, s.End);
				}
			}
			offset += lc->cmdsize;
		}

		if (module.Segments.empty()) return false;
		module.Base = base;
		module.Size = highest - base;
		return true;
	}

	std::vector<Module> EnumerateModules(mach_port_t task)
	{
		std::vector<Module> modules;

		task_dyld_info_data_t dyldInfo = {};
		mach_msg_type_number_t count = TASK_DYLD_INFO_COUNT;
		if (task_info(task, TASK_DYLD_INFO, reinterpret_cast<task_info_t>(&dyldInfo), &count) != KERN_SUCCESS || dyldInfo.all_image_info_addr == 0)
		{
			return modules;
		}

		dyld_all_image_infos infos = {};
		// Only the leading fields are needed; read the whole struct size defensively.
		if (!ReadRemote(task, dyldInfo.all_image_info_addr, &infos, std::min<size_t>(sizeof(infos), dyldInfo.all_image_info_size)))
		{
			return modules;
		}
		if (infos.infoArrayCount == 0 || infos.infoArrayCount > 8192 || infos.infoArray == nullptr)
		{
			return modules;
		}

		std::vector<dyld_image_info> images(infos.infoArrayCount);
		if (!ReadRemote(task, reinterpret_cast<uint64_t>(infos.infoArray), images.data(), images.size() * sizeof(dyld_image_info)))
		{
			return modules;
		}

		for (const auto& image : images)
		{
			Module m;
			if (!ParseImage(task, reinterpret_cast<uint64_t>(image.imageLoadAddress), m)) continue;
			m.Path = ReadRemoteString(task, reinterpret_cast<uint64_t>(image.imageFilePath), PATH_MAXIMUM_LENGTH - 1);
			modules.push_back(std::move(m));
		}

		// dyld itself is not in the info array.
		if (infos.dyldImageLoadAddress != nullptr)
		{
			Module m;
			if (ParseImage(task, reinterpret_cast<uint64_t>(infos.dyldImageLoadAddress), m))
			{
				m.Path = infos.dyldPath != nullptr ? ReadRemoteString(task, reinterpret_cast<uint64_t>(infos.dyldPath), PATH_MAXIMUM_LENGTH - 1) : "/usr/lib/dyld";
				modules.push_back(std::move(m));
			}
		}

		return modules;
	}

	SectionProtection ToProtection(vm_prot_t p)
	{
		auto result = SectionProtection::NoAccess;
		if (p & VM_PROT_READ) result |= SectionProtection::Read;
		if (p & VM_PROT_WRITE) result |= SectionProtection::Write;
		if (p & VM_PROT_EXECUTE) result |= SectionProtection::Execute;
		return result;
	}

	const Module* FindModule(const std::vector<Module>& modules, uint64_t address, const Segment** segmentOut)
	{
		for (const auto& m : modules)
		{
			for (const auto& s : m.Segments)
			{
				if (address >= s.Start && address < s.End)
				{
					*segmentOut = &s;
					return &m;
				}
			}
		}
		*segmentOut = nullptr;
		return nullptr;
	}
}

extern "C" void RC_CallConv EnumerateRemoteSectionsAndModules(RC_Pointer handle, EnumerateRemoteSectionsCallback callbackSection, EnumerateRemoteModulesCallback callbackModule)
{
	if (callbackSection == nullptr && callbackModule == nullptr)
	{
		return;
	}

	const auto task = TaskPorts::Get(HandleToPid(handle));
	if (task == MACH_PORT_NULL)
	{
		return;
	}

	const auto modules = EnumerateModules(task);

	if (callbackSection != nullptr)
	{
		mach_vm_address_t address = 0;
		natural_t depth = 0;
		while (true)
		{
			mach_vm_size_t size = 0;
			vm_region_submap_info_data_64_t info = {};
			mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;
			const auto kr = mach_vm_region_recurse(task, &address, &size, &depth, reinterpret_cast<vm_region_recurse_info_t>(&info), &count);
			if (kr != KERN_SUCCESS)
			{
				break;
			}
			if (info.is_submap)
			{
				++depth;
				continue;
			}

			EnumerateRemoteSectionData section = {};
			section.BaseAddress = reinterpret_cast<RC_Pointer>(address);
			section.Size = static_cast<RC_Size>(size);
			section.Protection = ToProtection(info.protection);
			section.Type = SectionType::Unknown;
			section.Category = SectionCategory::Unknown;

			const Segment* segment = nullptr;
			const auto* module = FindModule(modules, address, &segment);
			if (module != nullptr)
			{
				section.Type = SectionType::Image;
				MultiByteToUnicode(module->Path.c_str(), section.ModulePath, PATH_MAXIMUM_LENGTH);
				MultiByteToUnicode(segment->Name, section.Name, 15);
				const bool r = info.protection & VM_PROT_READ;
				const bool w = info.protection & VM_PROT_WRITE;
				const bool x = info.protection & VM_PROT_EXECUTE;
				if (r && x) section.Category = SectionCategory::CODE;
				else if (r && w) section.Category = SectionCategory::DATA;
			}
			else
			{
				section.Type = info.share_mode == SM_PRIVATE ? SectionType::Private : SectionType::Mapped;
				if (info.protection & (VM_PROT_READ | VM_PROT_WRITE))
				{
					section.Category = SectionCategory::HEAP;
				}
			}

			callbackSection(&section);

			address += size;
		}
	}

	if (callbackModule != nullptr)
	{
		for (const auto& m : modules)
		{
			EnumerateRemoteModuleData data = {};
			data.BaseAddress = reinterpret_cast<RC_Pointer>(m.Base);
			data.Size = static_cast<RC_Size>(m.Size);
			MultiByteToUnicode(m.Path.c_str(), data.Path, PATH_MAXIMUM_LENGTH);
			callbackModule(&data);
		}
	}
}
```

Notes for the implementer:
- `dyld_image_info` / `dyld_all_image_infos` in `<mach-o/dyld_images.h>` are pointer-sized for the **host**. Host is arm64 (64-bit) and all targets are 64-bit, so layouts match. Do not use these on a 32-bit host.
- For a Rosetta target the dyld info struct still points at a 64-bit x86_64 layout, identical.
- If `mach_vm_region_recurse` loops forever, ensure `address += size` happens and `depth` is reset to 0 when a non-submap region is returned. The loop above does not reset depth; if the harness hangs, add `depth = 0;` right after the `callbackSection(&section);` line — Apple's `vmmap` keeps depth, but resetting is safe.

- [ ] **Step 3: Build and run full harness**

Run: `cd NativeCore/MacOS && make debug && sudo ./test/harness build/debug/NativeCore.dylib`
Expected: `PASSED (0 failures)`. Every `CHECK` line prints `ok:`.

If "write to read-only page succeeds" fails: check that `mach_vm_protect` with `VM_PROT_COPY` returned `KERN_SUCCESS`; on some macOS versions the `__TEXT` of a shared-cache dylib cannot be COW'd, but `/bin/sleep`'s own `__TEXT` should be. If it still fails, relax the harness check to a DATA-only write and note the limitation in README (Task 8).

- [ ] **Step 4: Also build release**

Run: `cd NativeCore/MacOS && make release && sudo ./test/harness build/release/NativeCore.dylib`
Expected: `PASSED (0 failures)`.

- [ ] **Step 5: Commit**

```bash
git add NativeCore/MacOS
git commit -m "NativeCore/MacOS: enumerate modules via dyld image infos and sections via mach_vm_region_recurse"
```

---

### Task 6: C# — IsMacOS detection and dylib loading

**Files:**
- Modify: `ReClass.NET/Native/NativeMethods.cs`
- Modify: `ReClass.NET/Core/InternalCoreFunctions.cs:15-16,49`
- Possibly modify: `ReClass.NET/Native/NativeMethods.Unix.cs`
- Test: `ReClass.NET_Tests/` — a new test file `NativeMethodsTest.cs` if the test project builds under Mono (see Step 1).

**Interfaces:**
- Produces: `public static bool NativeMethods.IsMacOS()`.

- [ ] **Step 1: Check whether the C# test project builds on Mono**

Run:
```bash
mono Dependencies/nuget.exe restore ReClass.NET.sln 2>&1 | tail -5
xbuild /p:Configuration=Debug /p:Platform=x64 ReClass.NET/ReClass.NET.csproj 2>&1 | tail -15
```
Expected: `ReClass.NET/bin/Debug/x64/ReClass.NET.exe` exists (path from csproj `OutputPath`, relative to solution dir: `bin/Debug/x64/`). If xbuild fails on `PackageReference` in the test project, ignore the test project — it uses `PackageReference` which xbuild does not support. In that case, skip Steps 2 and 4 and record "C# unit test project not buildable under xbuild; verified by manual run" in the commit message.

If the **main** project fails to build, stop and report the first error verbatim; do not proceed to later tasks until it builds.

- [ ] **Step 2: Write failing test (only if test project builds)**

`ReClass.NET_Tests/NativeMethodsTest.cs`:
```csharp
using NFluent;
using ReClassNET.Native;
using Xunit;

namespace ReClass.NET_Tests
{
	public class NativeMethodsTest
	{
		[Fact]
		public void IsMacOSImpliesIsUnix()
		{
			if (NativeMethods.IsMacOS())
			{
				Check.That(NativeMethods.IsUnix()).IsTrue();
			}
		}

		[Fact]
		public void IsMacOSIsStable()
		{
			Check.That(NativeMethods.IsMacOS()).IsEqualTo(NativeMethods.IsMacOS());
		}
	}
}
```
Add `<Compile Include="NativeMethodsTest.cs" />` to `ReClass.NET_Tests/ReClass.NET_Tests.csproj` in the existing `<ItemGroup>` with other `Compile` items.

- [ ] **Step 3: Implement `IsMacOS()`**

In `ReClass.NET/Native/NativeMethods.cs`, add after `IsUnix()`:

```csharp
		private static bool? isMacOS;
		public static bool IsMacOS()
		{
			if (isMacOS.HasValue)
			{
				return isMacOS.Value;
			}

			var p = GetPlatformId();
			if (p == PlatformID.MacOSX)
			{
				isMacOS = true;
			}
			else if (IsUnix())
			{
				// Mono reports PlatformID.Unix on macOS; ask the kernel.
				isMacOS = NativeMethodsUnix.GetKernelName() == "Darwin";
			}
			else
			{
				isMacOS = false;
			}

			return isMacOS.Value;
		}
```

In `ReClass.NET/Native/NativeMethods.Unix.cs`, add inside the `#region Imports` and a helper:

```csharp
		[DllImport("libc")]
		private static extern int uname(IntPtr buf);

		/// <summary>Returns utsname.sysname ("Darwin", "Linux") or empty string on failure.</summary>
		internal static string GetKernelName()
		{
			var buffer = IntPtr.Zero;
			try
			{
				// utsname is 5 (or 6) fields of at most 256 bytes each on Linux/macOS.
				buffer = Marshal.AllocHGlobal(8192);
				if (uname(buffer) != 0)
				{
					return string.Empty;
				}
				return Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(buffer);
				}
			}
		}
```

- [ ] **Step 4: Update `InternalCoreFunctions.Create()`**

Replace lines 15–16 and 49 in `ReClass.NET/Core/InternalCoreFunctions.cs`:

```csharp
		private const string CoreFunctionsModuleWindows = "NativeCore.dll";
		private const string CoreFunctionsModuleUnix = "NativeCore.so";
		private const string CoreFunctionsModuleMacOS = "NativeCore.dylib";
```
```csharp
			var libraryName = NativeMethods.IsMacOS() ? CoreFunctionsModuleMacOS
				: NativeMethods.IsUnix() ? CoreFunctionsModuleUnix
				: CoreFunctionsModuleWindows;
```

- [ ] **Step 5: Build main project**

Run: `xbuild /p:Configuration=Debug /p:Platform=x64 ReClass.NET/ReClass.NET.csproj 2>&1 | grep -E 'error|Build succeeded' | head`
Expected: `Build succeeded.`

- [ ] **Step 6: Run tests (only if test project builds)**

Run: `xbuild /p:Configuration=Debug /p:Platform=x64 ReClass.NET_Tests/ReClass.NET_Tests.csproj && mono packages/xunit.runner.console.*/tools/net472/xunit.console.exe bin/Debug/x64/ReClass.NET_Tests.dll -class ReClass.NET_Tests.NativeMethodsTest`
Expected: 2 passed. (Adjust runner path to whatever `nuget restore` placed under `packages/`; if no console runner package exists, skip and note it.)

- [ ] **Step 7: Smoke-check dlopen under Mono**

Create `NativeCore/MacOS/test/DlopenSmoke.cs` (throwaway, not in csproj):
```csharp
using System;
using System.Runtime.InteropServices;

class DlopenSmoke
{
	[DllImport("__Internal")] static extern IntPtr dlopen(string f, int flags);
	[DllImport("__Internal")] static extern IntPtr dlsym(IntPtr h, string s);

	static void Main(string[] args)
	{
		var h = dlopen(args[0], 2);
		Console.WriteLine("dlopen: " + h);
		Console.WriteLine("EnumerateProcesses: " + dlsym(h, "EnumerateProcesses"));
	}
}
```
Run:
```bash
csc -out:/tmp/DlopenSmoke.exe NativeCore/MacOS/test/DlopenSmoke.cs && mono /tmp/DlopenSmoke.exe "$PWD/NativeCore/MacOS/build/debug/NativeCore.dylib"
```
Expected: two non-zero pointers. If `dlopen` throws `EntryPointNotFoundException` under `__Internal`, change both `DllImport("__Internal")` attributes in `NativeMethods.Unix.cs` to `DllImport("libdl")` (macOS maps it to `libSystem`), rebuild, and rerun the smoke test with the same change applied to `DlopenSmoke.cs`.

- [ ] **Step 8: Commit**

```bash
git add ReClass.NET/Native/NativeMethods.cs ReClass.NET/Native/NativeMethods.Unix.cs ReClass.NET/Core/InternalCoreFunctions.cs ReClass.NET_Tests NativeCore/MacOS/test/DlopenSmoke.cs
git commit -m "ReClass.NET: detect macOS and load NativeCore.dylib"
```

---

### Task 7: Build integration — root Makefile, run script, dist layout

**Files:**
- Modify: `Makefile` (root)
- Create: `run-macos.sh`

- [ ] **Step 1: Add macOS targets to root `Makefile`**

Append:

```make
# ---- macOS (Mono + XQuartz, arm64) ----
MACOS_XBUILD = xbuild /p:Platform=x64 /nologo /verbosity:minimal

macos_update:
	mono Dependencies/nuget.exe restore ReClass.NET.sln

macos_debug:
	$(MACOS_XBUILD) /p:Configuration=Debug ReClass.NET_Launcher/ReClass.NET_Launcher.csproj
	$(MACOS_XBUILD) /p:Configuration=Debug ReClass.NET/ReClass.NET.csproj
	$(MAKE) -C NativeCore/MacOS debug
	$(MAKE) macos_dist_debug

macos_release:
	$(MACOS_XBUILD) /p:Configuration=Release ReClass.NET_Launcher/ReClass.NET_Launcher.csproj
	$(MACOS_XBUILD) /p:Configuration=Release ReClass.NET/ReClass.NET.csproj
	$(MAKE) -C NativeCore/MacOS release
	$(MAKE) macos_dist_release

macos_dist_debug:
	mkdir -p build/Debug/x64/Plugins
	cp -r bin/Debug/x64/* build/Debug/x64/
	cp NativeCore/MacOS/build/debug/NativeCore.dylib build/Debug/x64/
	cp -r Dependencies/x64/* build/Debug/x64/ 2>/dev/null || true

macos_dist_release:
	mkdir -p build/Release/x64/Plugins
	cp -r bin/Release/x64/* build/Release/x64/
	cp NativeCore/MacOS/build/release/NativeCore.dylib build/Release/x64/
	cp -r Dependencies/x64/* build/Release/x64/ 2>/dev/null || true

macos: macos_release

macos_clean:
	rm -rf bin obj build
	$(MAKE) -C NativeCore/MacOS clean

.PHONY: macos macos_update macos_debug macos_release macos_dist_debug macos_dist_release macos_clean
```

Check the launcher's real output path first: `grep -n OutputPath ReClass.NET_Launcher/ReClass.NET_Launcher.csproj` shows `$(SolutionDir)bin\Release\` (AnyCPU, no `x64` subfolder). Adjust the `cp -r bin/Release/x64/*` line to also copy `bin/Release/ReClass.NET_Launcher.exe` if it lands one level up:
```make
	-cp bin/Release/ReClass.NET_Launcher.exe build/Release/x64/ 2>/dev/null
```
(and the Debug equivalent).

- [ ] **Step 2: Create `run-macos.sh`**

```sh
#!/bin/sh
# Launches ReClass.NET on macOS under Mono using the X11 WinForms backend.
# Requirements: Homebrew mono, XQuartz running. Runs as root because
# task_for_pid needs it.
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
CFG="${RECLASS_CONFIG:-Release}"
APP="$DIR/build/$CFG/x64"

if [ ! -f "$APP/ReClass.NET.exe" ]; then
	echo "ReClass.NET.exe not found in $APP. Run 'make macos' first." >&2
	exit 1
fi
if [ ! -f "$APP/NativeCore.dylib" ]; then
	echo "NativeCore.dylib not found in $APP. Run 'make macos' first." >&2
	exit 1
fi

export DISPLAY="${DISPLAY:-:0}"
cd "$APP"
exec sudo -E "$(command -v mono)" ReClass.NET.exe "$@"
```

```bash
chmod +x run-macos.sh
```

- [ ] **Step 3: Full build**

Run: `make macos_update && make macos 2>&1 | tail -20`
Expected: `build/Release/x64/ReClass.NET.exe` and `build/Release/x64/NativeCore.dylib` exist.

Run: `ls build/Release/x64/`
Expected: includes `ReClass.NET.exe`, `NativeCore.dylib`, `ColorCode.dll`, `Microsoft.ExceptionMessageBox.dll`, `Plugins/`.

- [ ] **Step 4: Launch smoke test**

Ensure XQuartz is running (`open -a XQuartz`, wait a few seconds). Then:

Run: `./run-macos.sh` (enter sudo password). Leave it running for 10 seconds, then close the window.
Expected: main window appears in an XQuartz window. No exception dialog on startup. stderr may show Mono WinForms warnings; those are fine.

Failure triage:
- `System.DllNotFoundException: libgdiplus` → `brew install mono-libgdiplus`, then `export DYLD_LIBRARY_PATH=/opt/homebrew/lib` inside `run-macos.sh` before `exec` (add it permanently if needed).
- `Failed to load native core functions!` → the dylib path is wrong or dlopen failed; run the Task 6 Step 7 smoke test against `build/Release/x64/NativeCore.dylib`.
- `TypeInitializationException` involving `Dia2Lib` or `ExceptionMessageBox` → go to Task 8 Step 1 first.
- Window never appears, no error → `DISPLAY` wrong; run `echo $DISPLAY` in a regular terminal and pass it: `DISPLAY=$DISPLAY ./run-macos.sh`.

- [ ] **Step 5: Attach smoke test**

With the app running: File → Attach to process (or the toolbar process button). Confirm the list is populated. Select a non-Apple process (e.g. a `sleep 300` you started in another terminal, or any Homebrew-installed app). Confirm the class view shows memory bytes rather than `??`. Open the module list (Process → Modules / "Class address calculator" dialog) and confirm modules appear.

Expected: attach works, memory shows.

- [ ] **Step 6: Commit**

```bash
git add Makefile run-macos.sh
git commit -m "Build: add macOS make targets and run script"
```

---

### Task 8: Windows-only managed dependency guards (only if Task 7 Step 4/5 surfaced a failure)

**Files:**
- Possibly modify: `ReClass.NET/Program.cs:100-115`, `ReClass.NET/Logger/BaseLogger.cs:15`, `ReClass.NET/Symbols/SymbolStore.cs`, `ReClass.NET/Symbols/SymbolReader.cs`, and the site that constructs `SymbolStore`.

- [ ] **Step 1: Locate constructors of Windows-only types**

Run: `grep -rn "new SymbolStore\|new SymbolReader\|new ExceptionMessageBox\|ExceptionMessageBox\." ReClass.NET --include='*.cs'`

- [ ] **Step 2: Guard `ExceptionMessageBox` (if it threw at runtime)**

In `ReClass.NET/Program.cs`, wrap the `ExceptionMessageBox` usage:

```csharp
			if (NativeMethods.IsUnix())
			{
				MessageBox.Show(ex.ToString(), Constants.ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
```
placed immediately before `var msg = new ExceptionMessageBox(ex)`. In `ReClass.NET/Logger/BaseLogger.cs` line 15 replace `ExceptionMessageBox.GetMessageText(ex)` with `NativeMethods.IsUnix() ? ex.Message : ExceptionMessageBox.GetMessageText(ex)` and add `using ReClassNET.Native;`.

- [ ] **Step 3: Guard `SymbolStore` (if it threw at runtime)**

Find the constructor site from Step 1 (expected in `Program.cs` or `Symbols/…`). Wrap so that on Unix the store is only created lazily and any `COMException` / `TypeLoadException` is caught and logged via `Program.Logger.Log(LogLevel.Warning, "Symbols unavailable on this platform")`.

- [ ] **Step 4: Rebuild, relaunch**

Run: `make macos && ./run-macos.sh`
Expected: startup without exception dialogs.

- [ ] **Step 5: Commit**

```bash
git add ReClass.NET
git commit -m "ReClass.NET: guard Windows-only symbol and message box dependencies on Unix"
```

If nothing failed in Task 7, skip this task entirely and note "Task 8 not needed" in the Task 9 commit message.

---

### Task 9: README macOS section

**Files:**
- Modify: `README.md` (insert after the "## Compiling" section, before "## Videos")

- [ ] **Step 1: Write section**

```markdown
## macOS (experimental)

ReClass.NET runs on Apple Silicon macOS under Mono with the X11 WinForms backend.

**Prerequisites**
```
brew install mono mono-libgdiplus
brew install --cask xquartz   # then log out/in once, and start XQuartz
```

**Build**
```
make macos_update   # NuGet restore
make macos          # builds launcher, app, NativeCore.dylib into build/Release/x64
```

**Run**
```
./run-macos.sh
```
The script runs Mono as root via `sudo` because `task_for_pid` requires it.

**Limitations**
- Reading other processes needs root. Even as root, SIP prevents attaching to
  Apple-signed / hardened-runtime processes. Third-party apps and games work.
- The debugger ("Find out what accesses/writes this address") is not available.
- Disassembly uses distorm (x86 only). It is correct for x86_64 processes
  running under Rosetta and meaningless for native arm64 processes.
- Debug symbols (PDB) and file-type registration are Windows-only.
- The UI is drawn through XQuartz and does not look native.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "README: document macOS build, run and limitations"
```

---

## Self-review

**Spec coverage:**
- Native core exports: Tasks 1–5 ✔ (Debugger/Input/Disassemble stubs Task 1; Open/Close/Control Task 2; EnumerateProcesses Task 3; Read/Write Task 4; Sections/Modules Task 5).
- Handle == pid + TaskPorts map: Task 2 ✔.
- Makefile clang arm64 dylib: Task 1 ✔.
- C# `IsMacOS`, dylib name, `__Internal` verification: Task 6 ✔.
- Windows-only dep guards: Task 8 (conditional) ✔.
- Root Makefile, run script, README: Tasks 7, 9 ✔.
- Native harness tests: Task 2 (file) through Task 5 (full pass) ✔. C# tests: Task 6 best-effort, documented fallback.
- Rosetta arch detection via sysctl: spec listed it for `EnumerateProcesses`, but since no ABI field carries it and no gating is done, it is dropped (YAGNI). Spec's "Report all 64-bit processes" holds.

**Placeholders:** none. Every code step has full code.

**Type consistency:** `TaskPorts::Get/Acquire/Release` and `HandleToPid` used identically in Tasks 2, 4, 5. `RC_*` types from `ReClassNET_Plugin.hpp`. Harness function-pointer typedefs match `NativeCore.hpp` signatures.
