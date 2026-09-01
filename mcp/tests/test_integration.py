"""Integration tests against a live ReClass.NET + MCP plugin.

Skipped entirely unless the endpoint file (``~/.reclass-mcp.json``, or
``$RECLASS_MCP_ENDPOINT``) exists, i.e. ReClass.NET is running with the
``ReClass.NET_McpPlugin`` loaded.

To run:
    1. Build/launch ReClass.NET with the MCP plugin (e.g. ``./run-macos.sh``).
    2. Start a throwaway target process to attach to: ``sleep 300 &``.
    3. ``pytest -m integration`` from ``mcp/``.

These exercise every RPC method group end-to-end through
:class:`reclass_mcp.client.RcClient` (not through the MCP tool wrappers,
which are thin JSON-string pass-throughs over the same calls). They are
written ahead of the C# plugin landing, so some field names/shapes may need
small adjustments once it exists.
"""

from __future__ import annotations

import asyncio
import os
import subprocess
import sys
from pathlib import Path

import pytest

from reclass_mcp.client import RcClient

pytestmark = pytest.mark.integration


def _endpoint_path() -> Path:
    override = os.environ.get("RECLASS_MCP_ENDPOINT")
    if override:
        return Path(override).expanduser()
    return Path.home() / ".reclass-mcp.json"


if not _endpoint_path().exists():
    pytest.skip(
        "no ReClass.NET MCP endpoint file found; start ReClass.NET with the "
        "MCP plugin loaded to run integration tests",
        allow_module_level=True,
    )


@pytest.fixture
async def client():
    c = RcClient()
    await c.connect()
    try:
        yield c
    finally:
        await c.close()


@pytest.fixture
def sleep_child():
    """A throwaway child process for process.attach / memory tests."""
    proc = subprocess.Popen(["sleep", "300"])
    try:
        yield proc
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


@pytest.fixture
async def attached(client: RcClient, sleep_child):
    """Attach to the sleep child process for the duration of a test."""
    await client.call("process.attach", id=sleep_child.pid)
    try:
        yield sleep_child
    finally:
        try:
            await client.call("process.detach")
        except Exception:
            pass


async def test_system_info(client: RcClient):
    info = await client.call("system.info")
    assert "reclass_version" in info
    assert "platform" in info


async def test_process_list(client: RcClient):
    procs = await client.call("process.list")
    assert isinstance(procs, list)
    assert any("id" in p and "name" in p for p in procs)


async def test_process_attach_status_detach(client: RcClient, sleep_child):
    result = await client.call("process.attach", id=sleep_child.pid)
    assert result["id"] == sleep_child.pid

    status = await client.call("process.status")
    assert status["attached"] is True
    assert status["id"] == sleep_child.pid

    detach_result = await client.call("process.detach")
    assert detach_result["ok"] is True

    status_after = await client.call("process.status")
    assert status_after["attached"] is False


async def test_modules_list_and_memory_read_macho_magic(client: RcClient, attached):
    modules = await client.call("modules.list", refresh=True)
    assert isinstance(modules, list) and len(modules) > 0
    base = modules[0]["start"]

    read = await client.call("memory.read", address=base, size=4)
    assert read["size"] == 4
    import base64

    data = base64.b64decode(read["data_b64"])
    # Mach-O 64-bit little-endian magic 0xFEEDFACF -> bytes cf fa ed fe.
    assert data.hex() == "cffaedfe"


async def test_class_create_get_node_change_type_codegen_roundtrip(client: RcClient):
    created = await client.call("class.create", name="IntegrationTestClass", size=64)
    class_name = created["name"]
    try:
        cls = await client.call("class.get", **{"class": class_name, "depth": 1, "with_values": False})
        assert cls["type"] in ("Class", "ClassInstance") or "children" in cls

        node_selector = {"class": class_name, "offset": 0}
        changed = await client.call(
            "node.change_type", node=node_selector, type="Hex32"
        )
        assert changed["type"] == "Hex32"

        code = await client.call("codegen.generate", language="cpp", classes=[class_name])
        assert class_name in code["code"] or "struct" in code["code"].lower()
    finally:
        await client.call("class.delete", **{"class": class_name, "force": True})


async def _wait_for_scan_idle(client: RcClient, timeout: float = 60.0) -> dict:
    """Poll scan.status until the scan is no longer running.

    The plugin rejects scan.results/scan.undo/scan.next with -32006 "busy"
    while a scan is in progress, so every caller must wait for
    `running: false` before issuing any of those.
    """
    deadline = asyncio.get_event_loop().time() + timeout
    status: dict = {}
    while asyncio.get_event_loop().time() < deadline:
        status = await client.call("scan.status")
        if not status.get("running"):
            assert status.get("error") is None
            return status
        await asyncio.sleep(0.1)
    raise AssertionError(f"scan did not finish within {timeout}s: {status}")


async def test_scan_first_status_results(client: RcClient, attached):
    # value_type/compare are lowercase, plugin-defined keys (ScannerApi.cs):
    # value_type in {byte, short, integer/int, long, float, double,
    # bytes/array_of_bytes, string, regex}; compare in {equal, not_equal,
    # changed, not_changed, greater_than, greater_than_or_equal, increased,
    # increased_or_equal, less_than, less_than_or_equal, decreased,
    # decreased_or_equal, between, between_or_equal, unknown}.
    first = await client.call(
        "scan.first",
        value_type="integer",
        compare="equal",
        value=0,
        settings={"start": "0x0", "stop": "0x7fffffffffff", "fast": True},
    )
    assert "job" in first

    # scan.results/scan.undo/scan.next are rejected with -32006 "busy" while
    # the scan is still running, so wait for it to finish first.
    await _wait_for_scan_idle(client)

    results = await client.call("scan.results", offset=0, limit=10)
    assert "total" in results and "results" in results

    await client.call("scan.reset")


async def test_analysis_disassemble(client: RcClient, attached):
    modules = await client.call("modules.list", refresh=True)
    base = modules[0]["start"]
    instructions = await client.call("analysis.disassemble", address=base, length=32)
    assert isinstance(instructions, list)
    assert len(instructions) > 0
    assert "text" in instructions[0]


async def test_project_save_roundtrip(client: RcClient, tmp_path):
    await client.call("project.new")
    await client.call("class.create", name="ProjectRoundtripClass", size=16)
    save_path = str(tmp_path / "integration_test.rcnet")
    result = await client.call("project.save", path=save_path)
    assert result["path"] == save_path
    assert Path(save_path).exists()
