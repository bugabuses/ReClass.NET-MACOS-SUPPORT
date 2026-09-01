# reclass-mcp

A thin Python MCP bridge exposing every ReClass.NET feature (process control,
memory, project/class/node editing, code generation, memory scanning, and
analysis) as MCP tools. It talks to the `ReClass.NET_McpPlugin` C# plugin
over a local TCP/NDJSON JSON-RPC connection; the plugin runs in-process
inside a running ReClass.NET instance.

```
MCP client (Claude Code)  --stdio, MCP-->  reclass-mcp (this package)
                                                   |
                                    TCP 127.0.0.1:<port>, NDJSON JSON-RPC, token auth
                                                   v
                                    ReClass.NET_McpPlugin.dll (in-process)
```

## Install

From the `mcp/` directory:

```sh
uv sync --extra dev      # installs the package + pytest/pytest-asyncio
# or
pip install -e '.[dev]'
```

## Register with Claude Code

```sh
claude mcp add reclass -- uv run --directory <repo>/mcp reclass-mcp
```

or, using a plain Python install instead of `uv`:

```sh
claude mcp add reclass -- python -m reclass_mcp
```

(replace `<repo>` with the path to your ReClass.NET checkout.)

## How it finds ReClass.NET

On `Initialize`, the C# plugin starts a TCP server on `127.0.0.1` (random
port) with a random shared-secret token, and writes both to an endpoint
file:

```
~/.reclass-mcp.json   ->  {"port": <int>, "token": "<hex32>", "pid": <int>}
```

`reclass-mcp` reads this file on first use (and again on reconnect) to find
and authenticate to the running plugin. If ReClass.NET (with the MCP plugin
loaded) isn't running, every tool call fails with a connection error telling
you so. Before connecting, it also checks that the `pid` in the endpoint
file is still alive; a stale file left behind by a closed ReClass.NET
produces a clear "not running" error instead of a raw connection failure.

## Trust boundary

There is no user model here: the endpoint file `~/.reclass-mcp.json` holds the
port and the shared token, and **anyone who can read it can drive ReClass.NET
with ReClass.NET's own privileges**. On macOS that normally means root — the
app is launched with `sudo` so it can read and write other processes' memory.

Through this bridge that grants, to whoever holds the token: arbitrary read
*and write* of any process's memory, attaching to and suspending/terminating
processes, and reading/writing project files anywhere on the filesystem. The
only protections are that the listener binds `127.0.0.1` (never a routable
interface) and that the endpoint file is written `0600`.

So: keep the machine single-user and trusted, do not share the token, and do
not run the plugin on a host where other people (or untrusted code) have a
local account. Stop ReClass.NET when you are done — `Terminate` deletes the
endpoint file.

## Errors

Tool errors from the plugin surface as `<code> <name>: <message>` (see the
plugin's JSON-RPC error table for codes/names). Two extra bridge-local
codes are used when the RPC call never reached or returned from the plugin
at all: `-1 timeout` (the call exceeded `RECLASS_MCP_TIMEOUT`) and
`-1 connection` (couldn't connect, or the connection dropped and could not
be re-established).

## Environment overrides

| Variable | Default | Purpose |
|---|---|---|
| `RECLASS_MCP_ENDPOINT` | `~/.reclass-mcp.json` | Path to the endpoint file (useful for multiple instances or non-standard `$HOME`). |
| `RECLASS_MCP_TIMEOUT` | `30` (seconds) | Per-call RPC timeout. A few slow tools (`project_load`, `project_save`, `class_get`, `memory_read`, `analysis_disassemble`) use a fixed 120 s instead. |

## Development

```sh
uv sync --extra dev
uv run pytest -q tests/test_client.py     # unit tests (fake NDJSON server)
uv run pytest -m integration              # live tests; skipped unless the endpoint file exists
```

`tests/test_integration.py` drives a real ReClass.NET + plugin + a `sleep`
child process end to end (process attach, memory read, class/node CRUD,
scanning, disassembly, project save). It is skipped automatically unless
`~/.reclass-mcp.json` (or `$RECLASS_MCP_ENDPOINT`) exists.
