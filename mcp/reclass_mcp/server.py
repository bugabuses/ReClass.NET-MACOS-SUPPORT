"""MCP server exposing ReClass.NET's RPC surface as MCP tools.

One ``@mcp.tool()`` per RPC method defined in
``docs/superpowers/specs/2026-09-01-mcp-server-design.md`` (the plugin-side
RPC table), with dotted names converted to snake_case
(``memory.read_batch`` -> ``memory_read_batch``). Connects lazily to the
ReClass.NET MCP plugin over TCP via :class:`reclass_mcp.client.RcClient`.

Address parameters accept a hex string (``"0x1F00A0"``), a decimal string, or
an int. Node selectors are ``{"class": <name-or-uuid>, "path": [i, j, ...]}``
(indices into ``Nodes`` at each container level) or
``{"class": <name-or-uuid>, "offset": n}`` (direct child at that byte
offset).
"""

from __future__ import annotations

import json
from typing import Any, Optional, Union

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.exceptions import ToolError

from .client import RcClient, RpcError

mcp = FastMCP("reclass")

Address = Union[str, int]
NodeSelector = dict

_client: Optional[RcClient] = None

# JSON-RPC error codes -> names, from the plugin's error table.
ERROR_NAMES = {
    -32001: "no_process",
    -32002: "bad_argument",
    -32003: "not_found",
    -32004: "internal",
    -32005: "referenced",
    -32006: "busy",
    -32007: "unauthorized",
    -32700: "parse_error",
    -32600: "invalid_request",
    -32601: "method_not_found",
}


def _get_client() -> RcClient:
    global _client
    if _client is None:
        _client = RcClient()
    return _client


def _dump(result: Any) -> str:
    return json.dumps(result, separators=(",", ":"))


async def _invoke(method: str, **params: Any) -> Any:
    client = _get_client()
    try:
        return await client.call(method, **params)
    except RpcError as exc:
        name = ERROR_NAMES.get(exc.code, "error")
        raise ToolError(f"{exc.code} {name}: {exc.message}") from exc
    except TimeoutError as exc:
        # -1 codes are bridge-local (not part of the plugin's JSON-RPC error
        # table): the RPC call never reached/returned from the plugin at all.
        raise ToolError(f"-1 timeout: {exc}") from exc
    except ConnectionError as exc:
        # -1 connection: could not reach or stayed connected to the plugin
        # (endpoint file missing/stale, plugin not running, socket dropped).
        raise ToolError(f"-1 connection: {exc}") from exc


# --------------------------------------------------------------------------
# system
# --------------------------------------------------------------------------


@mcp.tool()
async def system_info() -> str:
    """Get ReClass.NET version/platform/attach status. No params.
    Returns JSON: {reclass_version, platform, process_attached, project_path, class_count}."""
    return _dump(await _invoke("system.info"))


# --------------------------------------------------------------------------
# process
# --------------------------------------------------------------------------


@mcp.tool()
async def process_list(filter: Optional[str] = None) -> str:
    """List running processes, optionally filtered by a substring of their name.
    Returns JSON list: [{id, name, path}]."""
    params: dict[str, Any] = {}
    if filter is not None:
        params["filter"] = filter
    return _dump(await _invoke("process.list", **params))


@mcp.tool()
async def process_attach(id: Optional[int] = None, name: Optional[str] = None) -> str:
    """Attach ReClass.NET to a process by numeric id or by name (exactly one required).
    Returns JSON: {id, name, path}."""
    params: dict[str, Any] = {}
    if id is not None:
        params["id"] = id
    if name is not None:
        params["name"] = name
    return _dump(await _invoke("process.attach", **params))


@mcp.tool()
async def process_detach() -> str:
    """Detach from the currently attached process. No params. Returns JSON: {ok}."""
    return _dump(await _invoke("process.detach"))


@mcp.tool()
async def process_status() -> str:
    """Get current attach status. No params.
    Returns JSON: {attached, id, name, path, is_valid}."""
    return _dump(await _invoke("process.status"))


@mcp.tool()
async def process_control(action: str) -> str:
    """Control the attached process. `action` is one of: suspend, resume, terminate.
    Returns JSON: {ok}."""
    return _dump(await _invoke("process.control", action=action))


# --------------------------------------------------------------------------
# memory
# --------------------------------------------------------------------------


@mcp.tool()
async def memory_read(address: Address, size: int) -> str:
    """Read `size` bytes from the attached process at `address` (hex string, decimal string, or int).
    Returns JSON: {address, size, data_b64} (base64-encoded bytes)."""
    return _dump(await _invoke("memory.read", address=address, size=size))


@mcp.tool()
async def memory_read_batch(reads: list) -> str:
    """Read multiple memory regions in one round-trip.
    `reads` is a list of {address, size} objects.
    Returns JSON list: [{address, size, data_b64|null}] (null on a failed read)."""
    return _dump(await _invoke("memory.read_batch", reads=reads))


@mcp.tool()
async def memory_write(address: Address, data_b64: str) -> str:
    """Write base64-encoded bytes to `address` in the attached process.
    Returns JSON: {ok}."""
    return _dump(await _invoke("memory.write", address=address, data_b64=data_b64))


@mcp.tool()
async def memory_read_typed(
    address: Address,
    type: str,
    count: int = 1,
    length: Optional[int] = None,
) -> str:
    """Read one or more typed values from memory without client-side decoding.
    `type` is one of: int8, uint8, int16, uint16, int32, uint32, int64, uint64, float, double, bool, ptr, utf8, utf16, utf32.
    `count` repeats the read; `length` bounds text reads.
    Returns JSON: {values: [...]}."""
    params: dict[str, Any] = {"address": address, "type": type, "count": count}
    if length is not None:
        params["length"] = length
    return _dump(await _invoke("memory.read_typed", **params))


@mcp.tool()
async def memory_write_typed(address: Address, type: str, value: Any) -> str:
    """Write one typed value to memory.
    `type` is one of: int8, uint8, int16, uint16, int32, uint32, int64, uint64, float, double, bool, ptr, utf8, utf16, utf32.
    Returns JSON: {ok}."""
    return _dump(await _invoke("memory.write_typed", address=address, type=type, value=value))


@mcp.tool()
async def memory_read_string(address: Address, encoding: str = "utf8", max_length: int = 256) -> str:
    """Read a string from memory. `encoding` is one of: utf8, utf16, utf32.
    Returns JSON: {value}."""
    return _dump(
        await _invoke("memory.read_string", address=address, encoding=encoding, max_length=max_length)
    )


@mcp.tool()
async def memory_eval_address(formula: str) -> str:
    """Evaluate an address formula, e.g. `<Game.exe>+0x10`.
    Returns JSON: {address} (hex string)."""
    return _dump(await _invoke("memory.eval_address", formula=formula))


# --------------------------------------------------------------------------
# modules / sections
# --------------------------------------------------------------------------


@mcp.tool()
async def modules_list(refresh: bool = False) -> str:
    """List loaded modules of the attached process. Set `refresh` to force a re-scan.
    Returns JSON list: [{name, path, start, end, size}]."""
    return _dump(await _invoke("modules.list", refresh=refresh))


@mcp.tool()
async def sections_list(module: Optional[str] = None) -> str:
    """List memory sections/regions, optionally filtered to one module by name.
    Returns JSON list: [{name, start, end, size, category, protection, type, module_name}]."""
    params: dict[str, Any] = {}
    if module is not None:
        params["module"] = module
    return _dump(await _invoke("sections.list", **params))


# --------------------------------------------------------------------------
# project
# --------------------------------------------------------------------------


@mcp.tool()
async def project_new() -> str:
    """Discard the current project and start a new, empty one. No params.
    Returns JSON: {ok}."""
    return _dump(await _invoke("project.new"))


@mcp.tool()
async def project_load(path: str) -> str:
    """Load a .rcnet project file from `path`.
    Returns JSON: {path, classes}."""
    return _dump(await _invoke("project.load", path=path))


@mcp.tool()
async def project_save(path: Optional[str] = None) -> str:
    """Save the current project. `path` defaults to the project's existing path.
    Returns JSON: {path}."""
    params: dict[str, Any] = {}
    if path is not None:
        params["path"] = path
    return _dump(await _invoke("project.save", **params))


@mcp.tool()
async def project_info() -> str:
    """Get the current project's path, classes, and enum names. No params.
    Returns JSON: {path, classes:[{name,uuid,address_formula,size}], enums:[names]}."""
    return _dump(await _invoke("project.info"))


# --------------------------------------------------------------------------
# class
# --------------------------------------------------------------------------


@mcp.tool()
async def class_list() -> str:
    """List all classes in the current project. No params.
    Returns JSON list: [{name,uuid,address_formula,size}]."""
    return _dump(await _invoke("class.list"))


@mcp.tool()
async def class_get(class_name: str, depth: int = 1, with_values: bool = True) -> str:
    """Get a class's node tree, optionally with live memory values.
    `class_name` is a class name or UUID. `depth` limits how far nested class instances expand.
    Returns JSON: a node DTO tree (see module docstring for node selector shape)."""
    return _dump(await _invoke("class.get", **{"class": class_name, "depth": depth, "with_values": with_values}))


@mcp.tool()
async def class_create(name: Optional[str] = None, address_formula: Optional[str] = None, size: int = 64) -> str:
    """Create a new class.
    Returns JSON: {name, uuid}."""
    params: dict[str, Any] = {"size": size}
    if name is not None:
        params["name"] = name
    if address_formula is not None:
        params["address_formula"] = address_formula
    return _dump(await _invoke("class.create", **params))


@mcp.tool()
async def class_rename(class_name: str, name: str) -> str:
    """Rename a class. `class_name` is a class name or UUID.
    Returns JSON: {ok}."""
    return _dump(await _invoke("class.rename", **{"class": class_name, "name": name}))


@mcp.tool()
async def class_delete(class_name: str, force: bool = False) -> str:
    """Delete a class. `class_name` is a class name or UUID.
    Fails with `referenced` (data.references) unless `force` is true.
    Returns JSON: {ok}."""
    return _dump(await _invoke("class.delete", **{"class": class_name, "force": force}))


@mcp.tool()
async def class_set_address(class_name: str, address_formula: str) -> str:
    """Set a class's address formula. `class_name` is a class name or UUID.
    Returns JSON: {ok, resolved}."""
    return _dump(await _invoke("class.set_address", **{"class": class_name, "address_formula": address_formula}))


@mcp.tool()
async def class_select(class_name: str) -> str:
    """Make a class the current selected class in the ReClass.NET UI.
    Returns JSON: {ok}."""
    return _dump(await _invoke("class.select", **{"class": class_name}))


@mcp.tool()
async def class_add_bytes(class_name: str, size: int) -> str:
    """Append `size` bytes of padding to a class. `class_name` is a class name or UUID.
    Returns JSON: {ok}."""
    return _dump(await _invoke("class.add_bytes", **{"class": class_name, "size": size}))


@mcp.tool()
async def class_insert_bytes(node: NodeSelector, size: int) -> str:
    """Insert `size` bytes of padding at a node's position.
    `node` selector: {"class": name/uuid, "path": [i,...]} or {"class": name/uuid, "offset": n}.
    Returns JSON: {ok}."""
    return _dump(await _invoke("class.insert_bytes", node=node, size=size))


# --------------------------------------------------------------------------
# node
# --------------------------------------------------------------------------


@mcp.tool()
async def node_get(node: NodeSelector, with_values: bool = True) -> str:
    """Get one node's DTO by selector.
    `node`: {"class": name/uuid, "path": [i,...]} or {"class": name/uuid, "offset": n}.
    Returns JSON: node DTO."""
    return _dump(await _invoke("node.get", node=node, with_values=with_values))


@mcp.tool()
async def node_change_type(
    node: NodeSelector,
    type: str,
    inner_type: Optional[str] = None,
    class_ref: Optional[str] = None,
) -> str:
    """Change a node's type (e.g. "Hex32", "Pointer", "ClassInstance", "UTF8Text").
    `inner_type` is used for wrapper nodes; `class_ref` names the target class for ClassInstance/pointer-to-class.
    Returns JSON: the updated node DTO."""
    params: dict[str, Any] = {"node": node, "type": type}
    if inner_type is not None:
        params["inner_type"] = inner_type
    if class_ref is not None:
        params["class_ref"] = class_ref
    return _dump(await _invoke("node.change_type", **params))


@mcp.tool()
async def node_rename(node: NodeSelector, name: str) -> str:
    """Rename a node. Returns JSON: {ok}."""
    return _dump(await _invoke("node.rename", node=node, name=name))


@mcp.tool()
async def node_comment(node: NodeSelector, comment: str) -> str:
    """Set a node's comment. Returns JSON: {ok}."""
    return _dump(await _invoke("node.comment", node=node, comment=comment))


@mcp.tool()
async def node_remove(node: NodeSelector) -> str:
    """Remove a node from its containing class. Returns JSON: {ok}."""
    return _dump(await _invoke("node.remove", node=node))


@mcp.tool()
async def node_set_hidden(node: NodeSelector, hidden: bool) -> str:
    """Show or hide a node in the ReClass.NET UI. Returns JSON: {ok}."""
    return _dump(await _invoke("node.set_hidden", node=node, hidden=hidden))


@mcp.tool()
async def node_set_array(node: NodeSelector, count: int) -> str:
    """Set the element count of an Array/ClassInstanceArray node. Returns JSON: {ok}."""
    return _dump(await _invoke("node.set_array", node=node, count=count))


@mcp.tool()
async def node_set_bits(node: NodeSelector, bits: int) -> str:
    """Set the bit width of a BitField node. Returns JSON: {ok}."""
    return _dump(await _invoke("node.set_bits", node=node, bits=bits))


@mcp.tool()
async def node_types() -> str:
    """List all available node types. No params.
    Returns JSON list: [{name, size, is_container, is_wrapper}]."""
    return _dump(await _invoke("node.types"))


@mcp.tool()
async def node_set_enum(node: NodeSelector, enum: str) -> str:
    """Attach an enum (by name) to a node. Returns JSON: {ok}."""
    return _dump(await _invoke("node.set_enum", node=node, enum=enum))


# --------------------------------------------------------------------------
# enum
# --------------------------------------------------------------------------


@mcp.tool()
async def enum_list() -> str:
    """List all enums in the current project. No params.
    Returns JSON list: [{name, size, flags, values:{k:v}}]."""
    return _dump(await _invoke("enum.list"))


@mcp.tool()
async def enum_set(name: str, values: dict, size: int = 4, flags: bool = False) -> str:
    """Create or update an enum. `values` maps member name -> integer value.
    Returns JSON: {ok}."""
    return _dump(await _invoke("enum.set", name=name, size=size, flags=flags, values=values))


@mcp.tool()
async def enum_delete(name: str) -> str:
    """Delete an enum by name. Fails with `referenced` if still in use.
    Returns JSON: {ok}."""
    return _dump(await _invoke("enum.delete", name=name))


# --------------------------------------------------------------------------
# codegen
# --------------------------------------------------------------------------


@mcp.tool()
async def codegen_generate(language: str, classes: Optional[list] = None) -> str:
    """Generate source code for classes. `language` is "cpp" or "csharp".
    `classes` limits output to these class names (default: all classes).
    Returns JSON: {code}."""
    params: dict[str, Any] = {"language": language}
    if classes is not None:
        params["classes"] = classes
    return _dump(await _invoke("codegen.generate", **params))


# --------------------------------------------------------------------------
# scan
# --------------------------------------------------------------------------


@mcp.tool()
async def scan_first(
    value_type: str,
    compare: str,
    value: Any,
    value2: Optional[Any] = None,
    settings: Optional[dict] = None,
) -> str:
    """Start a new memory scan.
    `value_type` (case-insensitive): byte, short, integer (or int), long, float,
    double, bytes (or array_of_bytes), string, regex.
    `compare` (case-insensitive, underscores optional): equal, not_equal, changed,
    not_changed, greater_than, greater_than_or_equal, increased, increased_or_equal,
    less_than, less_than_or_equal, decreased, decreased_or_equal, between,
    between_or_equal, unknown.
    `value2` is required for between/between_or_equal (as the upper bound).
    `settings` may include
    {start, stop, alignment, fast, writable, executable, cow, private, image, mapped}.
    Fails with `busy` if a scan is already running. The scan runs asynchronously:
    poll scan_status until `running` is false before calling scan_results or scan_next.
    Returns JSON: {job, total?}."""
    params: dict[str, Any] = {"value_type": value_type, "compare": compare, "value": value}
    if value2 is not None:
        params["value2"] = value2
    if settings is not None:
        params["settings"] = settings
    return _dump(await _invoke("scan.first", **params))


@mcp.tool()
async def scan_next(compare: str, value: Optional[Any] = None, value2: Optional[Any] = None) -> str:
    """Refine the previous scan's results with a new comparison.
    `compare` (case-insensitive, underscores optional): equal, not_equal, changed,
    not_changed, greater_than, greater_than_or_equal, increased, increased_or_equal,
    less_than, less_than_or_equal, decreased, decreased_or_equal, between,
    between_or_equal, unknown.
    `value2` is required for between/between_or_equal (as the upper bound).
    Poll scan_status until `running` is false (this scan and any prior one) before
    calling scan_next or scan_results.
    Returns JSON: {job}."""
    params: dict[str, Any] = {"compare": compare}
    if value is not None:
        params["value"] = value
    if value2 is not None:
        params["value2"] = value2
    return _dump(await _invoke("scan.next", **params))


@mcp.tool()
async def scan_status() -> str:
    """Get the running scan's progress. No params.
    Poll this until `running` is false before calling scan_results, scan_next, or
    scan_undo (they fail with `busy` while a scan is in progress). `success` is set
    once the scan finishes; `error` holds a message if it failed, else null.
    Returns JSON: {running, progress, total, success, error}."""
    return _dump(await _invoke("scan.status"))


@mcp.tool()
async def scan_results(offset: int = 0, limit: int = 1000) -> str:
    """Page through the current scan's results.
    Fails with `busy` while a scan is running: poll scan_status until `running`
    is false before calling this.
    Returns JSON: {total, results:[{address, value}]}."""
    return _dump(await _invoke("scan.results", offset=offset, limit=limit))


@mcp.tool()
async def scan_undo() -> str:
    """Undo the last scan step, restoring the previous result set. No params.
    Fails with `busy` while a scan is running: poll scan_status until `running`
    is false before calling this.
    Returns JSON: {ok, total}."""
    return _dump(await _invoke("scan.undo"))


@mcp.tool()
async def scan_cancel() -> str:
    """Cancel the currently running scan. No params.
    Returns JSON: {ok}."""
    return _dump(await _invoke("scan.cancel"))


@mcp.tool()
async def scan_reset() -> str:
    """Dispose the active scanner and its results. No params.
    Returns JSON: {ok}."""
    return _dump(await _invoke("scan.reset"))


# --------------------------------------------------------------------------
# analysis
# --------------------------------------------------------------------------


@mcp.tool()
async def analysis_dissect(class_name: Optional[str] = None, node: Optional[NodeSelector] = None) -> str:
    """Dissect hex nodes under a class or a specific node into guessed types.
    Provide exactly one of `class_name` (name/uuid) or `node` (selector).
    Returns JSON: {changed:[node dtos]}."""
    params: dict[str, Any] = {}
    if class_name is not None:
        params["class"] = class_name
    if node is not None:
        params["node"] = node
    return _dump(await _invoke("analysis.dissect", **params))


@mcp.tool()
async def analysis_guess(address: Address) -> str:
    """Guess the data type at an address by inspecting its bytes.
    Returns JSON: {type, reason}."""
    return _dump(await _invoke("analysis.guess", address=address))


@mcp.tool()
async def analysis_pointer_preview(address: Address, size: int = 64) -> str:
    """Preview memory at an address plus guessed interpretations, useful for pointer chasing.
    Returns JSON: {address, section, module, data_b64, guessed:[...]}."""
    return _dump(await _invoke("analysis.pointer_preview", address=address, size=size))


@mcp.tool()
async def analysis_disassemble(
    address: Address,
    length: int = 64,
    max_instructions: Optional[int] = None,
    function: bool = False,
) -> str:
    """Disassemble code at an address.
    `length` is the number of bytes to fetch and disassemble (also used, as the
    starting point, when `function` is set). `max_instructions` additionally caps
    the instruction count when `function` is false (ignored when `function` is
    true). Set `function` to disassemble the whole containing function instead of
    a fixed byte range.
    Returns JSON list: [{address, length, bytes_hex, text}]."""
    params: dict[str, Any] = {"address": address, "length": length, "function": function}
    if max_instructions is not None:
        params["max_instructions"] = max_instructions
    return _dump(await _invoke("analysis.disassemble", **params))


@mcp.tool()
async def analysis_rtti(address: Address) -> str:
    """Read RTTI (runtime type information) for a C++ object at an address.
    Returns JSON: {rtti}."""
    return _dump(await _invoke("analysis.rtti", address=address))


@mcp.tool()
async def analysis_named_address(address: Address) -> str:
    """Resolve an address to a human-readable symbolic name (module+offset, export, etc).
    Returns JSON: {name}."""
    return _dump(await _invoke("analysis.named_address", address=address))


def main() -> None:
    """Entry point for the `reclass-mcp` console script: runs the stdio MCP server."""
    mcp.run()


if __name__ == "__main__":
    main()
