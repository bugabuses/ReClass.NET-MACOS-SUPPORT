"""Async TCP/NDJSON JSON-RPC 2.0 client for the ReClass.NET MCP plugin.

Dependency-free: uses only asyncio + json + stdlib. Reads connection info
(port, token, pid) from the endpoint file written by the C# plugin
(``~/.reclass-mcp.json`` by default, overridable via ``RECLASS_MCP_ENDPOINT``).
"""

from __future__ import annotations

import asyncio
import itertools
import json
import os
from pathlib import Path
from typing import Any

DEFAULT_TIMEOUT = 30.0

# Calls which legitimately take much longer than a plain query: project IO,
# a deep class.get, a 16 MiB memory.read, disassembling a whole function.
SLOW_CALL_TIMEOUT = 120.0

# Plugin-side per-call transfer cap is 16 MiB; give StreamReader headroom above
# that for JSON/base64 framing overhead so a large single line (e.g. a big
# process.list or memory.read_batch response) does not exceed the default
# 64 KiB asyncio.StreamReader limit and raise a LimitOverrunError.
STREAM_LIMIT = 16 * 1024 * 1024 + 1024


def _endpoint_path() -> Path:
    override = os.environ.get("RECLASS_MCP_ENDPOINT")
    if override:
        return Path(override).expanduser()
    return Path.home() / ".reclass-mcp.json"


def _timeout() -> float:
    raw = os.environ.get("RECLASS_MCP_TIMEOUT")
    if raw:
        try:
            return float(raw)
        except ValueError:
            pass
    return DEFAULT_TIMEOUT


class RpcError(Exception):
    """Raised when the plugin returns a JSON-RPC error object."""

    def __init__(self, code: int, message: str, data: Any = None):
        self.code = code
        self.message = message
        self.data = data
        super().__init__(f"{code}: {message}")


class RcClient:
    """Persistent connection to the ReClass.NET MCP plugin's TCP server.

    One connection is shared for all calls; requests are multiplexed over it
    by JSON-RPC ``id`` with a reader task dispatching responses to per-call
    futures. Reconnects (re-reading the endpoint file) once on
    ``ConnectionError``/EOF before giving up.
    """

    def __init__(self, endpoint_path: Path | None = None, timeout: float | None = None):
        self._endpoint_path = endpoint_path or _endpoint_path()
        self._timeout = timeout if timeout is not None else _timeout()
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._reader_task: asyncio.Task | None = None
        self._pending: dict[Any, asyncio.Future] = {}
        self._id_counter = itertools.count(1)
        self._lock = asyncio.Lock()
        self._connected = False

    def _read_endpoint_sync(self) -> dict:
        try:
            raw = self._endpoint_path.read_text()
        except FileNotFoundError as exc:
            raise ConnectionError(
                f"ReClass.NET MCP endpoint file not found at {self._endpoint_path}. "
                "Is ReClass.NET running with the MCP plugin loaded?"
            ) from exc
        try:
            data = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise ConnectionError(f"malformed endpoint file {self._endpoint_path}: {exc}") from exc

        pid = data.get("pid")
        if pid is not None:
            try:
                os.kill(pid, 0)
            except ProcessLookupError as exc:
                raise ConnectionError(
                    f"ReClass.NET (pid {pid}) is not running; stale endpoint file {self._endpoint_path}"
                ) from exc
            except PermissionError:
                pass  # process exists but is owned by someone else (e.g. root) -> alive
        return data

    async def _read_endpoint(self) -> dict:
        # File IO + os.kill are cheap but blocking; keep the event loop free.
        return await asyncio.to_thread(self._read_endpoint_sync)

    async def connect(self) -> None:
        """Connect, authenticate, and start the background reader task."""
        async with self._lock:
            await self._connect_locked()

    async def _connect_locked(self) -> None:
        await self._close_locked()
        endpoint = await self._read_endpoint()
        port = endpoint["port"]
        token = endpoint["token"]

        reader, writer = await asyncio.open_connection("127.0.0.1", port, limit=STREAM_LIMIT)
        self._reader = reader
        self._writer = writer

        auth_id = next(self._id_counter)
        auth_request = {"jsonrpc": "2.0", "id": auth_id, "method": "auth", "params": {"token": token}}
        writer.write((json.dumps(auth_request) + "\n").encode("utf-8"))
        await writer.drain()

        line = await asyncio.wait_for(reader.readline(), timeout=self._timeout)
        if not line:
            await self._close_locked()
            raise ConnectionError("connection closed during auth handshake")
        response = json.loads(line.decode("utf-8"))
        if response.get("error"):
            err = response["error"]
            await self._close_locked()
            raise RpcError(err.get("code", -32000), err.get("message", "auth failed"), err.get("data"))

        self._reader_task = asyncio.create_task(self._read_loop())
        self._connected = True

    async def _close_locked(self) -> None:
        self._connected = False
        if self._reader_task is not None:
            self._reader_task.cancel()
            self._reader_task = None
        if self._writer is not None:
            try:
                self._writer.close()
            except Exception:
                pass
            self._writer = None
        self._reader = None
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("connection closed"))
        self._pending.clear()

    async def close(self) -> None:
        async with self._lock:
            await self._close_locked()

    async def _read_loop(self) -> None:
        assert self._reader is not None
        reader = self._reader
        try:
            while True:
                line = await reader.readline()
                if not line:
                    raise ConnectionError("connection closed by server")
                line = line.strip()
                if not line:
                    continue
                message = json.loads(line.decode("utf-8"))
                messages = message if isinstance(message, list) else [message]
                for msg in messages:
                    self._dispatch(msg)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            for fut in self._pending.values():
                if not fut.done():
                    fut.set_exception(exc if isinstance(exc, Exception) else ConnectionError(str(exc)))
            self._pending.clear()
            self._connected = False

    def _dispatch(self, msg: dict) -> None:
        msg_id = msg.get("id")
        fut = self._pending.pop(msg_id, None)
        if fut is None or fut.done():
            return
        if "error" in msg and msg["error"] is not None:
            err = msg["error"]
            fut.set_exception(RpcError(err.get("code", -32000), err.get("message", ""), err.get("data")))
        else:
            fut.set_result(msg.get("result"))

    async def _ensure_connected(self) -> None:
        if not self._connected or self._writer is None:
            await self._connect_locked()

    async def call(self, method: str, _timeout: float | None = None, **params: Any) -> Any:
        """Issue a single JSON-RPC call and return its ``result``.

        ``_timeout`` overrides this client's default timeout for this one call
        (used by the slow tools: project load/save, deep class.get, large
        memory.read, analysis.disassemble). Raises ``RpcError`` for JSON-RPC
        error responses. Auto-reconnects (re-reading the endpoint file) once on
        ``ConnectionError``/EOF.
        """
        timeout = self._timeout if _timeout is None else _timeout
        for attempt in range(2):
            try:
                async with self._lock:
                    await self._ensure_connected()
                    req_id = next(self._id_counter)
                    fut = asyncio.get_event_loop().create_future()
                    self._pending[req_id] = fut
                    request = {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params}
                    assert self._writer is not None
                    self._writer.write((json.dumps(request) + "\n").encode("utf-8"))
                    await self._writer.drain()
                try:
                    return await asyncio.wait_for(fut, timeout=timeout)
                except asyncio.TimeoutError:
                    self._pending.pop(req_id, None)
                    raise TimeoutError(f"reclass-mcp call '{method}' timed out after {timeout}s")
            except (ConnectionError, EOFError) as exc:
                async with self._lock:
                    await self._close_locked()
                if attempt == 0:
                    continue
                raise ConnectionError(f"reclass-mcp connection lost: {exc}") from exc

    async def batch(self, calls: list[tuple[str, dict]]) -> list[Any]:
        """Issue a JSON-RPC batch (a JSON array of requests).

        ``calls`` is a list of ``(method, params)`` pairs. Returns a list of
        results in the same order (raises the first ``RpcError`` encountered,
        if any request in the batch errored).
        """
        for attempt in range(2):
            try:
                async with self._lock:
                    await self._ensure_connected()
                    ids = [next(self._id_counter) for _ in calls]
                    futs = {}
                    requests = []
                    for req_id, (method, params) in zip(ids, calls):
                        fut = asyncio.get_event_loop().create_future()
                        futs[req_id] = fut
                        self._pending[req_id] = fut
                        requests.append({"jsonrpc": "2.0", "id": req_id, "method": method, "params": params})
                    assert self._writer is not None
                    self._writer.write((json.dumps(requests) + "\n").encode("utf-8"))
                    await self._writer.drain()

                async def _wait_all():
                    return [await futs[i] for i in ids]

                try:
                    return await asyncio.wait_for(_wait_all(), timeout=self._timeout)
                except asyncio.TimeoutError:
                    raise TimeoutError(f"reclass-mcp batch call timed out after {self._timeout}s")
                finally:
                    # _wait_all awaits the futures in order, so a failure (or a
                    # timeout) leaves the later ones unconsumed. Drop them from
                    # the pending table and retrieve any exception already set,
                    # otherwise asyncio logs "Future exception was never
                    # retrieved" when they are collected.
                    for i in ids:
                        self._pending.pop(i, None)
                        fut = futs[i]
                        if fut.done():
                            if not fut.cancelled():
                                fut.exception()
                        else:
                            fut.cancel()
            except (ConnectionError, EOFError) as exc:
                async with self._lock:
                    await self._close_locked()
                if attempt == 0:
                    continue
                raise ConnectionError(f"reclass-mcp connection lost: {exc}") from exc
