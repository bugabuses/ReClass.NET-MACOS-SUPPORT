#!/usr/bin/env python3
"""Throwaway smoke client for the ReClass.NET MCP plugin's JSON-RPC server.

Reads ~/.reclass-mcp.json, authenticates and exercises every RPC implemented
by chunk 1. Requires a running ReClass.NET with the plugin loaded and a
`sleep 300 &` process to attach to. Prints PASS/FAIL per check.

    python3 ReClass.NET_McpPlugin/test/rpc_smoke.py
"""

import base64
import json
import os
import socket
import struct
import sys

ENDPOINT = os.path.join(os.path.expanduser("~"), ".reclass-mcp.json")

PASSED = 0
FAILED = 0


def check(name, ok, detail=""):
    global PASSED, FAILED
    if ok:
        PASSED += 1
        print("PASS  %-34s %s" % (name, detail))
    else:
        FAILED += 1
        print("FAIL  %-34s %s" % (name, detail))
    return ok


class Client:
    def __init__(self, port, token):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.fp = self.sock.makefile("rwb")
        self.next_id = 1
        self.auth_result = self.send({"jsonrpc": "2.0", "id": 0,
                                      "method": "auth",
                                      "params": {"token": token}})

    def send(self, message):
        self.fp.write((json.dumps(message) + "\n").encode("utf-8"))
        self.fp.flush()
        line = self.fp.readline()
        if not line:
            return None
        return json.loads(line.decode("utf-8"))

    def call(self, method, **params):
        i = self.next_id
        self.next_id += 1
        return self.send({"jsonrpc": "2.0", "id": i,
                          "method": method, "params": params})

    def result(self, method, **params):
        response = self.call(method, **params)
        if response is None or "error" in response:
            raise RuntimeError("%s -> %s" % (method, response))
        return response["result"]

    def batch(self, requests):
        self.fp.write((json.dumps(requests) + "\n").encode("utf-8"))
        self.fp.flush()
        return json.loads(self.fp.readline().decode("utf-8"))

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


def error_code(response):
    if response is None:
        return None
    return response.get("error", {}).get("code")


def main():
    if not os.path.exists(ENDPOINT):
        print("FAIL  endpoint file missing: %s" % ENDPOINT)
        return 1

    with open(ENDPOINT) as fp:
        endpoint = json.load(fp)
    check("endpoint file", all(k in endpoint for k in ("port", "token", "pid")),
          "port=%s pid=%s" % (endpoint.get("port"), endpoint.get("pid")))

    mode = os.stat(ENDPOINT).st_mode & 0o777
    check("endpoint file mode 0600", mode == 0o600, oct(mode))

    c = Client(endpoint["port"], endpoint["token"])
    check("auth", c.auth_result.get("result", {}).get("ok") is True,
          str(c.auth_result.get("result")))

    info = c.result("system.info")
    check("system.info", "reclass_version" in info and "platform" in info,
          json.dumps(info))

    processes = c.result("process.list")
    check("process.list", isinstance(processes, list) and len(processes) > 0,
          "%d processes" % len(processes))

    sleeps = [p for p in c.result("process.list", filter="sleep")
              if p["name"] == "sleep"]
    if not check("process.list filter=sleep", len(sleeps) > 0,
                 "%d matches (start `sleep 300 &` first)" % len(sleeps)):
        return 1

    attached = c.result("process.attach", name="sleep")
    check("process.attach", attached["name"] == "sleep",
          "id=%s" % attached["id"])

    status = c.result("process.status")
    check("process.status", status["attached"] and status["is_valid"],
          json.dumps(status))

    modules = c.result("modules.list", refresh=True)
    check("modules.list", isinstance(modules, list) and len(modules) > 0,
          "%d modules, first=%s @ %s" % (len(modules),
                                         modules[0]["name"] if modules else "-",
                                         modules[0]["start"] if modules else "-"))

    sections = c.result("sections.list")
    check("sections.list", isinstance(sections, list) and len(sections) > 0,
          "%d sections" % len(sections))

    sleep_module = next((m for m in modules if m["name"] == "sleep"), modules[0])
    base = int(sleep_module["start"], 16)

    read = c.result("memory.read", address=sleep_module["start"], size=4)
    magic = base64.b64decode(read["data_b64"])
    check("memory.read Mach-O magic", magic == b"\xcf\xfa\xed\xfe",
          magic.hex())

    typed = c.result("memory.read_typed", address=base, type="uint32")
    check("memory.read_typed uint32", typed["values"][0] == 0xFEEDFACF,
          hex(typed["values"][0]))

    typed4 = c.result("memory.read_typed", address=base, type="uint8", count=4)
    check("memory.read_typed count=4", typed4["values"] == [0xCF, 0xFA, 0xED, 0xFE],
          str(typed4["values"]))

    batch_reads = c.result("memory.read_batch", reads=[
        {"address": sleep_module["start"], "size": 4},
        {"address": base + 4, "size": 4},
        {"address": "0x1", "size": 4},
    ])
    check("memory.read_batch",
          len(batch_reads) == 3
          and base64.b64decode(batch_reads[0]["data_b64"]) == b"\xcf\xfa\xed\xfe"
          and batch_reads[2]["data_b64"] is None,
          "third read unreadable -> null: %s" % (batch_reads[2]["data_b64"],))

    # A writable, private DATA section of the attached process.
    writable = [s for s in sections
                if "Write" in s["protection"] and s["category"] in ("DATA", "HEAP")
                and int(s["size"]) >= 16]
    if check("writable section found", len(writable) > 0,
             "%d candidates" % len(writable)):
        wrote = False
        for section in writable:
            addr = int(section["start"], 16)
            try:
                original = c.result("memory.read", address=addr, size=8)
            except RuntimeError:
                continue
            payload = base64.b64encode(b"\xde\xad\xbe\xef\x01\x02\x03\x04").decode()
            try:
                c.result("memory.write", address=addr, data_b64=payload)
            except RuntimeError:
                continue
            back = c.result("memory.read", address=addr, size=8)
            if back["data_b64"] == payload:
                wrote = True
                c.result("memory.write", address=addr,
                         data_b64=original["data_b64"])
                check("memory.write + read-back", True,
                      "%s @ %s" % (section["name"], section["start"]))
                restored = c.result("memory.read", address=addr, size=8)
                check("memory.write restore",
                      restored["data_b64"] == original["data_b64"], "")

                c.result("memory.write_typed", address=addr, type="uint32",
                         value=0x11223344)
                rt = c.result("memory.read_typed", address=addr, type="uint32")
                check("memory.write_typed + read_typed",
                      rt["values"][0] == 0x11223344, hex(rt["values"][0]))
                c.result("memory.write", address=addr,
                         data_b64=original["data_b64"])
                break
        if not wrote:
            check("memory.write + read-back", False,
                  "no writable section accepted a write")

    ev = c.result("memory.eval_address", formula="<%s>+0x10" % sleep_module["name"])
    check("memory.eval_address", int(ev["address"], 16) == base + 0x10,
          "%s (base=%s)" % (ev["address"], sleep_module["start"]))

    string = c.result("memory.read_string", address=base + 0x20,
                      encoding="utf8", max_length=16)
    check("memory.read_string", "value" in string, repr(string["value"])[:40])

    # Error cases.
    check("bad address -> -32002",
          error_code(c.call("memory.read", address="0x1", size=16)) == -32002,
          str(c.call("memory.read", address="0x1", size=16).get("error")))

    check("unknown method -> -32601",
          error_code(c.call("no.such.method")) == -32601, "")

    batch_response = c.batch([
        {"jsonrpc": "2.0", "id": 91, "method": "system.info", "params": {}},
        {"jsonrpc": "2.0", "id": 92, "method": "process.status", "params": {}},
        {"jsonrpc": "2.0", "id": 93, "method": "no.such.method", "params": {}},
    ])
    check("jsonrpc batch",
          isinstance(batch_response, list) and len(batch_response) == 3
          and error_code(batch_response[2]) == -32601,
          "ids=%s" % [r.get("id") for r in batch_response])

    detach = c.result("process.detach")
    check("process.detach", detach.get("ok") is True, "")

    after = c.result("process.status")
    check("process.status after detach", after["attached"] is False,
          json.dumps(after))

    check("no process -> -32001",
          error_code(c.call("memory.read", address="0x1000", size=4)) == -32001,
          "")

    c.close()

    # Wrong token must close the connection.
    bad = Client(endpoint["port"], "0" * 32)
    closed = bad.send({"jsonrpc": "2.0", "id": 1, "method": "system.info"}) is None
    check("wrong token closes connection",
          error_code(bad.auth_result) == -32007 and closed,
          "auth error=%s" % (bad.auth_result.get("error"),))
    bad.close()

    # A non-auth first line must also close the connection.
    unauth = Client.__new__(Client)
    unauth.sock = socket.create_connection(("127.0.0.1", endpoint["port"]), timeout=10)
    unauth.fp = unauth.sock.makefile("rwb")
    unauth.next_id = 1
    first = unauth.send({"jsonrpc": "2.0", "id": 1, "method": "system.info"})
    check("non-auth first line rejected", error_code(first) == -32007,
          str(first.get("error") if first else None))
    unauth.close()

    print("\n%d passed, %d failed" % (PASSED, FAILED))
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
