"""Unit tests for RcClient against a fake NDJSON JSON-RPC server."""

from __future__ import annotations

import asyncio
import json
import os

import pytest

from reclass_mcp.client import RcClient, RpcError

TOKEN = "test-token-abc123"


class FakeServer:
    """A minimal NDJSON JSON-RPC server for exercising RcClient.

    `handler(request) -> response_dict | None` is called per non-auth
    request; return None to suppress a response (used for timeout tests).
    """

    def __init__(self, handler=None, expected_token=TOKEN, close_after_auth_fail=True):
        self.handler = handler
        self.expected_token = expected_token
        self.close_after_auth_fail = close_after_auth_fail
        self.server: asyncio.AbstractServer | None = None
        self.port = 0
        self.connections = 0
        self._writers: list[asyncio.StreamWriter] = []

    async def start(self):
        self.server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        self.port = self.server.sockets[0].getsockname()[1]
        return self

    async def stop(self):
        for w in self._writers:
            try:
                w.close()
            except Exception:
                pass
        if self.server is not None:
            self.server.close()
            await self.server.wait_closed()

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.connections += 1
        self._writers.append(writer)
        try:
            first = await reader.readline()
            if not first:
                return
            req = json.loads(first.decode())
            token = (req.get("params") or {}).get("token")
            if token != self.expected_token:
                err = {
                    "jsonrpc": "2.0",
                    "id": req.get("id"),
                    "error": {"code": -32007, "message": "unauthorized"},
                }
                writer.write((json.dumps(err) + "\n").encode())
                await writer.drain()
                if self.close_after_auth_fail:
                    writer.close()
                return
            ok = {"jsonrpc": "2.0", "id": req.get("id"), "result": {"ok": True, "version": "1.0"}}
            writer.write((json.dumps(ok) + "\n").encode())
            await writer.drain()

            while True:
                line = await reader.readline()
                if not line:
                    return
                line = line.strip()
                if not line:
                    continue
                message = json.loads(line.decode())
                requests = message if isinstance(message, list) else [message]
                for r in requests:
                    if self.handler is None:
                        resp = {"jsonrpc": "2.0", "id": r["id"], "result": {"echo": r.get("params")}}
                    else:
                        resp = self.handler(r)
                    if resp is not None:
                        writer.write((json.dumps(resp) + "\n").encode())
                await writer.drain()
        except (ConnectionResetError, BrokenPipeError):
            pass


def write_endpoint(path, port, token=TOKEN, pid=None):
    # Default to our own pid so the client's liveness check (os.kill(pid, 0))
    # sees a live process rather than a fabricated, always-dead one.
    if pid is None:
        pid = os.getpid()
    path.write_text(json.dumps({"port": port, "token": token, "pid": pid}))


@pytest.mark.asyncio
async def test_auth_handshake_success(tmp_path):
    server = await FakeServer().start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        await client.connect()
        assert client._connected
    finally:
        await client.close()
        await server.stop()


@pytest.mark.asyncio
async def test_auth_wrong_token_closes_connection(tmp_path):
    server = await FakeServer().start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port, token="wrong-token")
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        with pytest.raises(RpcError) as exc_info:
            await client.connect()
        assert exc_info.value.code == -32007
    finally:
        await client.close()
        await server.stop()


@pytest.mark.asyncio
async def test_id_multiplexing_out_of_order_responses(tmp_path):
    # Server collects 3 requests, then replies to them in reverse order.
    # If the client mismatched ids to responses, results would come back scrambled.
    async def handle(reader, writer):
        first = await reader.readline()
        req = json.loads(first.decode())
        ok = {"jsonrpc": "2.0", "id": req.get("id"), "result": {"ok": True}}
        writer.write((json.dumps(ok) + "\n").encode())
        await writer.drain()
        buf = []
        while len(buf) < 3:
            line = await reader.readline()
            if not line:
                return
            buf.append(json.loads(line.decode()))
        for r in reversed(buf):
            resp = {"jsonrpc": "2.0", "id": r["id"], "result": {"value": r["params"]["n"]}}
            writer.write((json.dumps(resp) + "\n").encode())
        await writer.drain()
        writer.close()

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, port)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        await client.connect()
        results = await asyncio.gather(
            client.call("m", n=1),
            client.call("m", n=2),
            client.call("m", n=3),
        )
        assert results == [{"value": 1}, {"value": 2}, {"value": 3}]
    finally:
        await client.close()
        server.close()
        await server.wait_closed()


@pytest.mark.asyncio
async def test_batch(tmp_path):
    def handler(req):
        return {"jsonrpc": "2.0", "id": req["id"], "result": {"doubled": req["params"]["n"] * 2}}

    server = await FakeServer(handler=handler).start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        await client.connect()
        results = await client.batch([("m", {"n": 1}), ("m", {"n": 2}), ("m", {"n": 3})])
        assert results == [{"doubled": 2}, {"doubled": 4}, {"doubled": 6}]
    finally:
        await client.close()
        await server.stop()


@pytest.mark.asyncio
async def test_error_maps_to_rpc_error(tmp_path):
    def handler(req):
        return {
            "jsonrpc": "2.0",
            "id": req["id"],
            "error": {"code": -32001, "message": "no process attached"},
        }

    server = await FakeServer(handler=handler).start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        await client.connect()
        with pytest.raises(RpcError) as exc_info:
            await client.call("memory.read", address="0x1000", size=4)
        assert exc_info.value.code == -32001
        assert "no process" in exc_info.value.message
    finally:
        await client.close()
        await server.stop()


@pytest.mark.asyncio
async def test_call_timeout(tmp_path):
    def handler(req):
        return None  # never respond

    server = await FakeServer(handler=handler).start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port)
    client = RcClient(endpoint_path=endpoint, timeout=0.2)
    try:
        await client.connect()
        with pytest.raises(TimeoutError):
            await client.call("scan.status")
    finally:
        await client.close()
        await server.stop()


@pytest.mark.asyncio
async def test_stale_pid_raises_clear_error(tmp_path):
    # Pick a pid almost certainly not in use.
    dead_pid = 999999
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, port=1, pid=dead_pid)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    with pytest.raises(ConnectionError) as exc_info:
        await client.connect()
    assert str(dead_pid) in str(exc_info.value)
    assert "not running" in str(exc_info.value)


@pytest.mark.asyncio
async def test_reconnect_after_server_drop(tmp_path):
    calls = {"n": 0}

    def handler(req):
        calls["n"] += 1
        return {"jsonrpc": "2.0", "id": req["id"], "result": {"n": calls["n"]}}

    server = await FakeServer(handler=handler).start()
    endpoint = tmp_path / "endpoint.json"
    write_endpoint(endpoint, server.port)
    client = RcClient(endpoint_path=endpoint, timeout=2)
    try:
        await client.connect()
        result = await client.call("m")
        assert result == {"n": 1}

        # Kill the server, start a fresh one on a new port, update the
        # endpoint file: client should reconnect transparently on next call.
        await server.stop()
        server2 = await FakeServer(handler=handler).start()
        write_endpoint(endpoint, server2.port)
        try:
            result2 = await client.call("m")
            assert result2 == {"n": 2}
        finally:
            await server2.stop()
    finally:
        await client.close()
