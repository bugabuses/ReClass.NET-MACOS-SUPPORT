# ReClass.NET macOS Port — Design

Date: 2026-09-01

## Goal

Make ReClass.NET run on macOS (Apple Silicon, arm64) with minimal change to the
existing codebase. The class-reconstruction workflow (process list, attach,
read/write memory, module/section enumeration, class view, code generation)
must work. UI stays WinForms under Mono with the X11 backend (XQuartz).

## Decisions (agreed)

- Path A: minimum-change port. No UI rewrite.
- Privilege model: run as root (`sudo mono ReClass.NET.exe`). No codesigning /
  entitlements in this phase.
- Target processes: both native arm64 and x86_64 (Rosetta). Pointer size is
  always 8. Disassembly (distorm, x86 only) works for x86_64 targets; arm64
  targets get "no disassembly".
- Build: single 64-bit configuration on macOS. No x86 build.

## Out of scope (later phases)

- Debugger (hardware breakpoints / "find what writes"). Needs Mach exception
  ports + arm64 watchpoint registers. Stubbed.
- arm64 disassembly (Capstone).
- Codesign + `com.apple.security.cs.debugger` entitlement.
- Native-looking UI (Avalonia).
- Input key polling (`GetPressedKeys`). Stubbed, same as Linux.

## Constraints from macOS

- `task_for_pid` needs root (or entitlement). Even as root, SIP blocks
  Apple-signed / hardened-runtime system processes. Third-party apps and games
  are attachable.
- macOS `ptrace` has no `PEEKUSER`/debug-register access.
- Mono WinForms on macOS: only the X11 driver works on arm64. Requires XQuartz
  running and `DISPLAY` set.

## Architecture

```
ReClass.NET (C#, WinForms, Mono)
   |  dlopen("NativeCore.dylib")  (via NativeMethodsUnix)
   v
NativeCore/MacOS  (C++17, clang, exports from NativeCore.hpp)
   |  mach / libproc / dyld APIs
   v
target process
```

### 1. Native core — `NativeCore/MacOS/`

Mirrors `NativeCore/Unix/`. One `.cpp` per export, same export names and
signatures as `NativeCore/Unix/NativeCore.hpp` (which includes
`../ReClassNET_Plugin.hpp`). `RC_CallConv` is empty on non-Windows.

Handle convention: `OpenRemoteProcess` returns the **pid** cast to
`RC_Pointer` (same as Linux). A process-local map `pid -> mach_port_t task`
(guarded by a mutex) holds the task port obtained from `task_for_pid`.
Helper `mach_port_t GetTaskPort(RC_Pointer handle)` looks up (and lazily
re-acquires if missing). `CloseRemoteProcess` deallocates the port and
removes the entry. Keeping the handle == pid keeps `IsProcessValid` and
`ControlRemoteProcess` trivial and matches what the C# side already assumes
for Unix.

| Export | Implementation |
|---|---|
| `EnumerateProcesses` | `proc_listpids(PROC_ALL_PIDS)`, `proc_pidpath` for path. Skip pid 0 and pids with empty path. Arch via `sysctl(CTL_KERN, KERN_PROC, KERN_PROC_PID)` → `kp_proc.p_flag & P_TRANSLATED` means x86_64 under Rosetta, else arm64. Report all 64-bit processes (all of them on arm64 macOS). Name = filename of path. |
| `OpenRemoteProcess` | `task_for_pid(mach_task_self(), pid, &task)`. On failure log `mach_error_string` to stderr and return `nullptr`. Store in map. Record target arch (from the same sysctl) in a second map `pid -> bool isRosetta`. |
| `IsProcessValid` | `kill(pid, 0) == 0`. |
| `CloseRemoteProcess` | `mach_port_deallocate`, erase map entries. |
| `ReadRemoteMemory` | `mach_vm_read_overwrite(task, address, size, buffer+offset, &outSize)`; success iff `KERN_SUCCESS && outSize == size`. |
| `WriteRemoteMemory` | `mach_vm_write`. If it fails with `KERN_PROTECTION_FAILURE`/`KERN_INVALID_ADDRESS`, `mach_vm_protect(task, page-aligned range, FALSE, VM_PROT_READ\|VM_PROT_WRITE\|VM_PROT_COPY)`, retry, restore original protection (read from `mach_vm_region`). |
| `EnumerateRemoteSectionsAndModules` | Sections: loop `mach_vm_region_recurse` (`VM_REGION_SUBMAP_INFO_COUNT_64`, depth 0..N, descend into submaps). Map `protection` bits to `SectionProtection`. Type = `Image` if the region falls inside a known module range (computed first), else `Mapped`/`Private`. Category = `CODE` for R+X in image, `DATA` for R+W in image, `HEAP` for non-image R/W. Modules: `task_info(task, TASK_DYLD_INFO)` → read remote `dyld_all_image_infos` → `infoArray` of `dyld_image_info` (64-bit layout) → for each: read `mach_header_64` + load commands, sum `LC_SEGMENT_64` `vmsize` (skip `__PAGEZERO`) for module size, read path string. Emit `EnumerateRemoteModuleData{BaseAddress, Size, Path}`. Section `ModulePath` set from the enclosing module. Also emit `EnumerateRemoteSectionData.Name` from segment name (`__TEXT`, `__DATA`, …) when a region maps exactly onto a segment. |
| `ControlRemoteProcess` | Suspend → `task_suspend`; Resume → `task_resume`; Terminate → `kill(pid, SIGKILL)`. |
| `AttachDebuggerToProcess` | return `false`. |
| `DetachDebuggerFromProcess` | no-op. |
| `AwaitDebugEvent` | return `false`. |
| `HandleDebugEvent` | no-op. |
| `SetHardwareBreakpoint` | return `false`. |
| `DisassembleCode` | Delegates to `DistormHelper` (shared), unchanged. A global `g_lastOpenedIsRosetta` is **not** used: the C# side passes raw bytes with no process context, so we cannot gate here. Gating happens in C# (see below). |
| `InitializeInput` / `GetPressedKeys` / `ReleaseInput` | Stubs, identical to Unix. |

Build: `NativeCore/MacOS/Makefile`. `clang++ -std=c++17 -arch arm64 -fPIC
-Wall -O2 -DRECLASSNET64=1 -I../Dependencies/distorm/include`, link
`-shared -dynamiclib -o build/<cfg>/NativeCore.dylib`, plus distorm `.c` files
compiled with `clang -arch arm64`. Targets `debug`, `release`, `clean`. Use
`<filesystem>` (not experimental).

### 2. C# changes

- `Native/NativeMethods.cs`: add `IsMacOS()`. Detect via `Environment.OSVersion.Platform == PlatformID.MacOSX` OR (`PlatformID.Unix` and `uname()` returns `"Darwin"`). `uname` via `[DllImport("libc")] uname(IntPtr buf)`; read first 256 bytes as sysname. Cache result. `IsUnix()` stays true on macOS.
- `Core/InternalCoreFunctions.cs`: `CoreFunctionsModuleMacOS = "NativeCore.dylib"`; choose by `IsMacOS()` before `IsUnix()`.
- `Native/NativeMethods.Unix.cs`: `dlopen`/`dlsym`/`dlclose` via `[DllImport("__Internal")]` — verify under Mono on macOS. If it fails, switch to `DllImport("libdl")` (macOS resolves `libdl.dylib` via `libSystem`). Decision made at implementation time by running it.
- Disassembly for arm64 targets: no gating in phase 1. The native ABI has no per-process arch field and `DisassembleCode` receives raw bytes only, so adding gating would mean an ABI change. distorm output for arm64 code is meaningless; README documents this. Revisit in the Capstone phase.
- Windows-only managed deps:
  - `Symbols/SymbolReader.cs`, `Symbols/SymbolStore.cs` use `Dia2Lib` (COM). Confirm they are only instantiated on Windows; if any code path constructs them unconditionally, guard with `NativeMethods.IsUnix()`.
  - `Program.cs` / `Logger/BaseLogger.cs` use `Microsoft.ExceptionMessageBox`. Pure managed WinForms dialog; expected to load under Mono. If it throws at runtime, replace with `MessageBox.Show(ex.ToString())` on Unix.
- `App.config` / csproj: no change to target framework (4.7.2 profile ships with Mono 6.14).

### 3. Build and run

- Top-level `Makefile`: new targets `macos`, `macos_debug`, `macos_release`, `macos_dist`:
  - `xbuild /p:Configuration=<cfg> /p:Platform=x64 ReClass.NET_Launcher/ReClass.NET_Launcher.csproj` and same for `ReClass.NET/ReClass.NET.csproj` (`msbuild` not installed by Homebrew Mono; `xbuild` is). NuGet restore via `mono Dependencies/nuget.exe restore` (existing `update` target).
  - `make -C NativeCore/MacOS <cfg>`.
  - dist: copy `bin/<cfg>/x64/*` to `build/<cfg>/x64/`, copy `NativeCore.dylib`, create `Plugins/`, copy `Dependencies/x64/*`.
- `run-macos.sh` at repo root:
  ```sh
  #!/bin/sh
  # Requires XQuartz running. Launches ReClass.NET as root under Mono/X11.
  export DISPLAY="${DISPLAY:-:0}"
  cd "$(dirname "$0")/build/Release/x64" || exit 1
  exec sudo -E mono ReClass.NET.exe "$@"
  ```
- README: new "macOS" section — prerequisites (Homebrew `mono`, XQuartz), build commands, run script, limitations (root, SIP, no debugger, no arm64 disassembly, X11 look).

### 4. Error handling

- `task_for_pid` failure → `OpenRemoteProcess` returns `nullptr`; existing C# shows attach failure. stderr gets `task_for_pid(<pid>) failed: <mach_error_string>` plus a hint "run as root".
- Read failures return `false`; C# already treats as unreadable memory.
- Module enumeration reads remote memory defensively: bounded load-command count (`ncmds <= 4096`), bounded path length (`PATH_MAXIMUM_LENGTH`), skip image on any read failure.

### 5. Testing

- **Native (throwaway harness)** `NativeCore/MacOS/test/`: C++ program that `dlopen`s `NativeCore.dylib`, forks a child running `sleep 60`, then: `EnumerateProcesses` contains the child; `OpenRemoteProcess` succeeds (run under `sudo`); `ReadRemoteMemory` at the child's main executable base (from module enumeration) returns Mach-O magic `0xfeedfacf`; `EnumerateRemoteSectionsAndModules` yields at least `sleep` and `libSystem.B.dylib`; `WriteRemoteMemory` to a writable data page round-trips; `ControlRemoteProcess` suspend/resume changes task suspend count. Built by `make test` in the MacOS Makefile. Not part of dist.
- **C#**: build `ReClass.NET_Tests` with `xbuild`, run with the NUnit/xUnit runner the project already uses (check `packages.config`) under `mono`. Must pass unchanged.
- **Manual**: `./run-macos.sh`, attach to a self-built test binary (non-SIP), view memory in class view, verify module list, change a value via write.

## Files touched

New: `NativeCore/MacOS/{NativeCore.hpp, EnumerateProcesses.cpp, OpenRemoteProcess.cpp, CloseRemoteProcess.cpp, IsProcessValid.cpp, ReadRemoteMemory.cpp, WriteRemoteMemory.cpp, EnumerateRemoteSectionsAndModules.cpp, ControlRemoteProcess.cpp, Debugger.cpp, DisassembleCode.cpp, Input.cpp, TaskPorts.{hpp,cpp}, Makefile, test/harness.cpp}`, `run-macos.sh`.

Modified: `Makefile` (root), `README.md`, `ReClass.NET/Native/NativeMethods.cs`, `ReClass.NET/Native/NativeMethods.Unix.cs` (maybe), `ReClass.NET/Core/InternalCoreFunctions.cs`, possibly `Program.cs`/`Symbols/*` guards.
