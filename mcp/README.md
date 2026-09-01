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
you so.

## Environment overrides

| Variable | Default | Purpose |
|---|---|---|
| `RECLASS_MCP_ENDPOINT` | `~/.reclass-mcp.json` | Path to the endpoint file (useful for multiple instances or non-standard `$HOME`). |
| `RECLASS_MCP_TIMEOUT` | `5` (seconds) | Per-call RPC timeout. |

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
