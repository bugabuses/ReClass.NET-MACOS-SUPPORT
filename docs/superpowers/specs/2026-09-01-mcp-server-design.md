# ReClass.NET MCP Server — Design

Date: 2026-09-01

## Goal

Expose every ReClass.NET feature to MCP clients (Claude Code and others), fast.
Shape: an in-process C# plugin hosts a local JSON-RPC TCP server with full
access to the running ReClass.NET instance; a thin Python MCP server (official
`mcp` SDK, stdio transport) bridges MCP tools to that socket 1:1.

## Decisions (agreed)

- In-process plugin + Python bridge (not headless, not hand-rolled C# MCP).
- Python bridge, official `mcp` SDK (`FastMCP`), stdio transport.
- Full feature scope in one plan: process, memory, modules/sections, project,
  class/node CRUD, enums, code generation, scanner, analysis.
- Plugin↔bridge transport: TCP on `127.0.0.1`, random port, shared secret token.
  Works on macOS/Mono and Windows without per-OS socket code.
- Wire format: newline-delimited JSON-RPC 2.0. Binary payloads base64.
- JSON in C#: `System.Web.Extensions` `JavaScriptSerializer` (ships with .NET
  Framework 4.7.2 and Mono; zero NuGet deps). DTOs are `Dictionary<string,object>`
  / `List<object>` built by small helper functions.

## Out of scope

- Streaming memory watches / push notifications.
- Non-localhost access.
- GUI automation beyond project/class/node model (no menu clicking).
- Debugger (not available on macOS; on Windows a later phase).

## Constraints from the codebase

- Plugins are `.dll` in `<app>/Plugins/`, loaded by `PluginManager` (product
  name check), class deriving `ReClassNET.Plugins.Plugin`. Host gives
  `IPluginHost { MainWindow, Process, Logger, Settings, Resources }`.
- `RemoteProcess` read/write is safe off the UI thread (GUI already refreshes
  on a timer thread). Node tree, project, and forms are UI-thread only.
- Statics: `Program.RemoteProcess`, `Program.CoreFunctions`, `Program.MainForm`,
  `Program.Logger`. `MainForm.CurrentProject`, `MainForm.CurrentClassNode`,
  `MainForm.AttachToProcess(ProcessInfo)`, `MainForm.SetProject`,
  `MainForm.LoadProjectFromPath`, `MainForm.ReplaceSelectedNodesWithType`.
- Project file IO: `ReClassNetFile(project).Load/Save(path, logger)`.
- Codegen: `CppCodeGenerator` / `CSharpCodeGenerator`
  `.GenerateCode(classes, enums, logger)`.
- Scanner: `new Scanner(process, ScanSettings)`, `Search(IScanComparer, IProgress<int>, CancellationToken)`,
  `GetResults()`, `TotalResultCount`, `UndoLastScan`. Comparers per `ScanValueType`
  in `MemoryScanner/Comparer/*` with `ScanCompareType`.
- Analysis: `NodeDissector.DissectNodes/GuessNode`, `Disassembler(coreFunctions)`,
  `RemoteProcess.ReadRemoteRuntimeTypeInformation`, `RemoteProcess.ParseAddress(formula)`.
- Node types: all `ReClassNET.Nodes.*Node` concrete classes. Public API names
  used by the RPC = class name minus `Node` suffix (`Hex32`, `Pointer`,
  `ClassInstance`, `UTF8Text`, `VirtualMethodTable`, …).

## Architecture

```
MCP client (Claude Code)
   | stdio, MCP
   v
reclass_mcp (Python, FastMCP)        mcp/reclass_mcp/server.py
   | TCP 127.0.0.1:<port>, NDJSON JSON-RPC 2.0, token auth
   v
ReClass.NET_McpPlugin.dll (C#)       loaded into ReClass.NET process
   | direct calls; UI-thread marshalling where required
   v
ReClass.NET (RemoteProcess, project, nodes, scanner, codegen, ...)
```

## Component 1: C# plugin `ReClass.NET_McpPlugin`

New project `ReClass.NET_McpPlugin/ReClass.NET_McpPlugin.csproj` (old-style,
net472, `AnyCPU`, references `ReClass.NET.csproj`, `System.Web.Extensions`),
added to `ReClass.NET.sln`. Output copied into `bin/<cfg>/x64/Plugins/` by a
post-build step (and by the root `Makefile` `macos_dist_*`). `AssemblyProduct`
must equal ReClass.NET's product name (PluginManager checks it).

Files and responsibilities:

| File | Responsibility |
|---|---|
| `McpPlugin.cs` | `Plugin` subclass. `Initialize`: build `RpcDispatcher`, start `TcpJsonRpcServer` on `127.0.0.1:0`, write endpoint file, log. `Terminate`: stop server, delete endpoint file. |
| `Endpoint.cs` | Endpoint file path `~/.reclass-mcp.json` (`%USERPROFILE%` on Windows), content `{"port":N,"token":"hex32","pid":P}`; write atomically (temp + rename), mode 0600 on Unix. |
| `Rpc/TcpJsonRpcServer.cs` | `TcpListener` accept loop on a background thread; one thread per client; reads lines (UTF-8, `\n`), first line must be `{"jsonrpc":"2.0","method":"auth","params":{"token":..},"id":..}` else close. Then dispatch each line (single object or batch array) and write response line. `Stop()` closes listener and clients. |
| `Rpc/RpcDispatcher.cs` | `Register(string method, Func<Dictionary<string,object>, object> handler)`. `Dispatch(request) -> response`. Maps exceptions: `RpcException(code,msg,data)` → error; `NoProcessException` → `-32001`; `ArgumentException`/bad address → `-32002`; `KeyNotFoundException` → `-32003`; any other → `-32004` with `ex.Message`. Unknown method → `-32601`. Malformed → `-32700`/`-32600`. |
| `Rpc/RpcException.cs` | Exception carrying JSON-RPC error `code`, `message`, optional `data`. Static ctors `NoProcess()`, `BadAddress(string)`, `NotFound(string)`. |
| `Rpc/UiThread.cs` | `T Invoke<T>(Func<T>)` / `void Invoke(Action)`: if `MainForm.InvokeRequired` marshal via `Control.Invoke`, rethrow inner exception unwrapped. |
| `Rpc/Json.cs` | `Serialize(object)`, `Deserialize(string) -> object` using `JavaScriptSerializer` with `MaxJsonLength = int.MaxValue`. Helpers `Params.Get<T>(dict, name)`, `Params.GetOptional<T>(dict, name, default)`, `Params.GetAddress(dict, name)` (accepts hex string `"0x…"`/`"…"` or number). Addresses **serialize as hex strings** `"0x1F00A0"`. |
| `Api/ProcessApi.cs` | see RPC table |
| `Api/MemoryApi.cs` | see RPC table |
| `Api/ProjectApi.cs` | see RPC table |
| `Api/NodeApi.cs` | see RPC table |
| `Api/CodeGenApi.cs` | see RPC table |
| `Api/ScannerApi.cs` | see RPC table; owns the single active `Scanner` and a `CancellationTokenSource`; `Search` runs on a task, progress tracked in an `int` |
| `Api/AnalysisApi.cs` | see RPC table |
| `Serialization/NodeDto.cs` | `ToDto(BaseNode, MemoryBuffer? mem, int depth, bool withValues)`; `NodeTypes.Resolve(string apiName) -> Type`, `NodeTypes.ApiName(Type)`; value formatting per node kind (numeric → number, text → string, pointer → hex, vector → list, class instance → nested dto). |
| `Serialization/ValueCodec.cs` | typed read/write helpers: `("int32","uint64","float","double","bool","utf8","utf16","ptr")` ↔ bytes, little-endian. |

### RPC methods (plugin side; MCP tools mirror names with `_`)

Conventions: `address` params accept hex string or integer; results use hex
strings. `class` params accept class name or UUID string (name lookup is
exact, first match). `node` selector = `{ "class": ..., "path": [i, j, ...] }`
(indices into `Nodes` at each container level; wrapper nodes descend into
`InnerNode` with index `0`), OR `{ "class": ..., "offset": n }` for a direct
child at that offset.

| Method | Params | Result | Thread |
|---|---|---|---|
| `auth` | `token` | `{ok:true, version}` | — |
| `system.info` | — | `{reclass_version, platform, process_attached, project_path, class_count}` | UI |
| `process.list` | `filter?` (substring) | `[{id, name, path}]` | any (`CoreFunctions.EnumerateProcesses`) |
| `process.attach` | `id` or `name` | `{id, name, path}` | UI (`MainForm.AttachToProcess`) |
| `process.detach` | — | `{ok}` | UI (`RemoteProcess.Close`) |
| `process.status` | — | `{attached, id, name, path, is_valid}` | any |
| `process.control` | `action`: `suspend|resume|terminate` | `{ok}` | any |
| `memory.read` | `address, size` | `{address, size, data_b64}` | any |
| `memory.read_batch` | `reads: [{address,size}]` | `[{address,size,data_b64|null}]` | any |
| `memory.write` | `address, data_b64` | `{ok}` | any |
| `memory.read_typed` | `address, type, count?=1, length?` (text) | `{values: [...]}` | any |
| `memory.write_typed` | `address, type, value` | `{ok}` | any |
| `memory.read_string` | `address, encoding: utf8|utf16|utf32, max_length?=256` | `{value}` | any |
| `memory.eval_address` | `formula` (e.g. `<Game.exe>+0x10`) | `{address}` | any (`ParseAddress`) |
| `modules.list` | `refresh?=false` | `[{name, path, start, end, size}]` | any (`UpdateProcessInformations` then `Modules`) |
| `sections.list` | `module?` | `[{name, start, end, size, category, protection, type, module_name}]` | any |
| `project.new` | — | `{ok}` | UI (`SetProject(new ReClassNetProject())`) |
| `project.load` | `path` | `{path, classes}` | UI (`LoadProjectFromPath`) |
| `project.save` | `path?` (defaults to `project.Path`) | `{path}` | UI (`ReClassNetFile.Save`) |
| `project.info` | — | `{path, classes:[{name,uuid,address_formula,size}], enums:[names]}` | UI |
| `class.list` | — | same as `project.info.classes` | UI |
| `class.get` | `class, depth?=1, with_values?=true` | full node dto tree (see below) | UI (reads memory via `MemoryBuffer.UpdateFrom`) |
| `class.create` | `name?, address_formula?, size?=64` | `{name,uuid}` | UI |
| `class.rename` | `class, name` | `{ok}` | UI |
| `class.delete` | `class, force?=false` | `{ok}` or error `-32005 class_referenced` with `references` | UI |
| `class.set_address` | `class, address_formula` | `{ok, resolved}` | UI |
| `class.select` | `class` | `{ok}` (makes it `CurrentClassNode`) | UI |
| `class.add_bytes` | `class, size` | `{ok}` | UI |
| `class.insert_bytes` | `node, size` | `{ok}` | UI |
| `node.get` | `node, with_values?` | node dto | UI |
| `node.change_type` | `node, type, inner_type?, class_ref?` | node dto | UI (`ReplaceChildNode`; wrapper/class-instance set `InnerNode`/class) |
| `node.rename` | `node, name` | `{ok}` | UI |
| `node.comment` | `node, comment` | `{ok}` | UI |
| `node.remove` | `node` | `{ok}` | UI |
| `node.set_hidden` | `node, hidden` | `{ok}` | UI |
| `node.set_array` | `node, count` | `{ok}` (Array/ClassInstanceArray count) | UI |
| `node.set_bits` | `node, bits` | `{ok}` (BitField) | UI |
| `node.types` | — | `[{name, size, is_container, is_wrapper}]` | any |
| `enum.list` | — | `[{name, size, flags, values:{k:v}}]` | UI |
| `enum.set` | `name, size?=4, flags?=false, values:{k:v}` | `{ok}` (create or update) | UI |
| `enum.delete` | `name` | `{ok}` / `-32005` | UI |
| `node.set_enum` | `node, enum` | `{ok}` | UI |
| `codegen.generate` | `language: cpp|csharp, classes?: [names]` (default all) | `{code}` | UI |
| `scan.first` | `value_type, compare, value, value2?, settings?{start,stop,alignment,fast,writable,executable,cow,private,image,mapped}` | `{job, total?}` | worker |
| `scan.next` | `compare, value?, value2?` | `{job}` | worker |
| `scan.status` | — | `{running, progress, total}` | any |
| `scan.results` | `offset?=0, limit?=1000` | `{total, results:[{address, value}]}` | any |
| `scan.undo` | — | `{ok, total}` | any |
| `scan.cancel` | — | `{ok}` | any |
| `scan.reset` | — | `{ok}` (dispose scanner) | any |
| `analysis.dissect` | `class` or `node` (hex nodes under it) | `{changed:[node dtos]}` | UI |
| `analysis.guess` | `address` | `{type, reason}` (uses a temp `Hex64Node` + `GuessNode`) | UI |
| `analysis.pointer_preview` | `address, size?=64` | `{address, section, module, data_b64, guessed:[...]}` | any |
| `analysis.disassemble` | `address, length?=64 | max_instructions?, function?=false` | `[{address, length, bytes_hex, text}]` | any |
| `analysis.rtti` | `address` | `{rtti}` | any |
| `analysis.named_address` | `address` | `{name}` (`GetNamedAddress`) | any |

Node DTO:
```json
{ "type":"Hex32", "name":"", "comment":"", "offset":16, "size":4, "hidden":false,
  "path":[3], "value": 123 | "0x..." | "str" | [..] | null,
  "inner": {dto} | null, "class_ref":"Name" | null, "count": 4 | null,
  "children":[dto...] | null }
```

### Threading rules

- Memory / process / modules / scanner / disassembler methods: run on the RPC
  client thread. `RemoteProcess` throws if no process → `NoProcess`.
- Anything reaching `MainForm`, `ReClassNetProject`, node objects: wrapped in
  `UiThread.Invoke`. Node mutations call `BeginUpdate/EndUpdate` on the
  container and `MainForm.Invalidate()` afterwards (the `MemoryViewControl`
  repaints on its timer).
- Scanner: one job at a time; `scan.first` while running → error `-32006 busy`.

### Errors

| code | name |
|---|---|
| -32001 | no_process |
| -32002 | bad_address / bad_argument |
| -32003 | not_found |
| -32004 | internal (message from exception) |
| -32005 | referenced (class/enum in use; `data.references`) |
| -32006 | busy |
| -32007 | unauthorized |

## Component 2: Python bridge `mcp/`

```
mcp/
  pyproject.toml            name reclass-mcp, deps: mcp>=1.2, python>=3.10; script `reclass-mcp`
  reclass_mcp/__init__.py
  reclass_mcp/client.py     RcClient: reads ~/.reclass-mcp.json, asyncio TCP, auth, request-id
                            multiplexing, `call(method, **params)`, `batch([...])`, auto-reconnect
                            (re-read endpoint file) on ConnectionError, 5s call timeout default
  reclass_mcp/server.py     FastMCP("reclass"); one @mcp.tool per RPC method, snake_case name
                            (`process_list`, `memory_read`, ...). Docstrings = tool descriptions.
                            Returns compact JSON strings. Errors → `ToolError(f"{code} {name}: {message}")`.
  reclass_mcp/__main__.py   `mcp.run()` (stdio)
  tests/test_client.py      fake NDJSON server; auth, multiplexing, batch, reconnect
  tests/test_integration.py @pytest.mark.integration; skipped unless ~/.reclass-mcp.json exists;
                            exercises every tool against a live ReClass with a `sleep` child
```

Performance: single persistent connection; `memory_read_batch` for many small
reads; `memory_read_typed` avoids client-side decoding; typical round-trip
target < 1 ms on localhost. Large `data_b64` capped at 16 MiB per call.

Claude Code registration (documented in README):
```
claude mcp add reclass -- uv run --directory <repo>/mcp reclass-mcp
```
(or `python -m reclass_mcp`).

## Testing strategy

- C#: no unit-test project (test project uses PackageReference; not buildable
  under xbuild). Correctness verified by Python integration tests driving the
  live plugin, plus `system.info` smoke.
- Python unit tests (fake server) run in CI-less local `pytest`.
- Integration: start ReClass (`./run-macos.sh` on macOS), `sleep 300` child,
  `pytest -m integration`. Must pass for every tool group.

## Files touched (summary)

New: `ReClass.NET_McpPlugin/**` (~16 files), `mcp/**` (~7 files).
Modified: `ReClass.NET.sln` (add project), root `Makefile` (build + copy plugin,
`macos_mcp_test` target), `README.md` (MCP section).
