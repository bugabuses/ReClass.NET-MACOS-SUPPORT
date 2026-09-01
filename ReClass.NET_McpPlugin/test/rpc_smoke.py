#!/usr/bin/env python3
"""Throwaway smoke client for the ReClass.NET MCP plugin's JSON-RPC server.

Reads ~/.reclass-mcp.json, authenticates and exercises every RPC implemented
by chunks 1-3. Requires a running ReClass.NET with the plugin loaded and a
`sleep 300 &` process to attach to. Prints PASS/FAIL per check.

    python3 ReClass.NET_McpPlugin/test/rpc_smoke.py
"""

import base64
import json
import os
import socket
import struct
import sys
import time

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

    # ------------------------------------------------------------------
    # Chunk 2: project / class / node / enum / codegen
    # ------------------------------------------------------------------

    scratch = os.environ.get(
        "SCRATCH",
        "/private/tmp/claude-501/-Users-ops-Desktop-Reclass-Mac/"
        "4daaa40c-5f25-412a-ba6e-07bf4fe08cf7/scratchpad")
    project_path = os.path.join(scratch, "mcp-test.rcnet")

    check("project.new", c.result("project.new").get("ok") is True, "")

    created = c.result("class.create", name="McpTest", size=64)
    check("class.create", created["name"] == "McpTest" and created["size"] == 64,
          "uuid=%s size=%s" % (created["uuid"], created["size"]))

    classes = c.result("class.list")
    check("class.list contains McpTest",
          any(cl["name"] == "McpTest" for cl in classes),
          "%d classes: %s" % (len(classes), [cl["name"] for cl in classes]))

    addr = c.result("class.set_address", **{"class": "McpTest",
                                            "address_formula": "<sleep>"})
    check("class.set_address",
          addr["ok"] is True and int(addr["resolved"], 16) == base,
          "resolved=%s" % addr["resolved"])

    got = c.result("class.get", **{"class": "McpTest", "with_values": True})
    kids = got["children"]
    kinds = sorted(set(k["type"] for k in kids))
    check("class.get default layout",
          len(kids) > 0 and kinds == ["Hex64"] and sum(k["size"] for k in kids) == 64,
          "%d children, types=%s, total=%d" % (len(kids), kinds,
                                               sum(k["size"] for k in kids)))
    check("class.get first value = Mach-O magic",
          isinstance(kids[0]["value"], str)
          and kids[0]["value"].upper().endswith("FEEDFACF"),
          kids[0]["value"])

    n0 = {"class": "McpTest", "path": [0]}
    changed = c.result("node.change_type", node=n0, type="UInt32")
    check("node.change_type -> UInt32", changed["type"] == "UInt32",
          "size=%s" % changed["size"])

    node0 = c.result("node.get", node=n0)
    check("node.get value == 0xfeedfacf", node0["value"] == 4277009103,
          str(node0["value"]))

    c.result("node.rename", node=n0, name="magic")
    c.result("node.comment", node=n0, comment="mach header")
    node0 = c.result("node.get", node=n0)
    check("node.rename / node.comment",
          node0["name"] == "magic" and node0["comment"] == "mach header",
          "%s // %s" % (node0["name"], node0["comment"]))

    n1 = {"class": "McpTest", "path": [1]}
    ptr = c.result("node.change_type", node=n1, type="Pointer",
                   inner_type="UInt8")
    check("node.change_type -> Pointer<UInt8>",
          ptr["type"] == "Pointer" and ptr["inner"]["type"] == "UInt8",
          "inner=%s" % ptr["inner"]["type"])

    other = c.result("class.create", name="McpOther", size=16)
    check("class.create McpOther", other["name"] == "McpOther", "")
    c.result("class.select", **{"class": "McpTest"})

    n2 = {"class": "McpTest", "path": [2]}
    inst = c.result("node.change_type", node=n2, type="ClassInstance",
                    class_ref="McpOther")
    check("node.change_type -> ClassInstance",
          inst["type"] == "ClassInstance" and inst["class_ref"] == "McpOther",
          "class_ref=%s size=%s" % (inst["class_ref"], inst["size"]))

    # Find a trailing Hex64 to turn into an array.
    tree = c.result("class.get", **{"class": "McpTest"})
    hex_index = next(i for i, k in enumerate(tree["children"])
                     if k["type"] == "Hex64")
    na = {"class": "McpTest", "path": [hex_index]}
    arr = c.result("node.change_type", node=na, type="Array")
    check("node.change_type -> Array", arr["type"] == "Array",
          "count=%s inner=%s" % (arr["count"], arr["inner"]["type"]))
    c.result("node.set_array", node=na, count=4)
    arr = c.result("node.get", node=na)
    check("node.set_array count=4", arr["count"] == 4,
          "size=%s" % arr["size"])

    c.result("node.set_hidden", node=na, hidden=True)
    check("node.set_hidden", c.result("node.get", node=na)["hidden"] is True, "")
    c.result("node.set_hidden", node=na, hidden=False)

    types = c.result("node.types")
    check("node.types", isinstance(types, list)
          and any(t["name"] == "Hex64" for t in types)
          and any(t["name"] == "Pointer" and t["is_wrapper"] for t in types),
          "%d types" % len(types))

    es = c.result("enum.set", name="McpEnum", size=4, flags=False,
                  values={"Zero": 0, "One": 1, "Two": 2})
    check("enum.set", es["ok"] is True, "created=%s" % es["created"])

    tree = c.result("class.get", **{"class": "McpTest"})
    hex_index = next(i for i, k in enumerate(tree["children"])
                     if k["type"] == "Hex64")
    ne = {"class": "McpTest", "path": [hex_index]}
    c.result("node.change_type", node=ne, type="Enum")
    c.result("node.set_enum", node=ne, **{"enum": "McpEnum"})
    enum_node = c.result("node.get", node=ne)
    check("node.set_enum", enum_node["class_ref"] == "McpEnum",
          "value=%s" % (enum_node["value"],))

    enums = c.result("enum.list")
    mcp_enum = next((e for e in enums if e["name"] == "McpEnum"), None)
    check("enum.list", mcp_enum is not None and mcp_enum["size"] == 4
          and mcp_enum["values"].get("One") == "1",
          json.dumps(mcp_enum))

    before = len(c.result("class.get", **{"class": "McpTest"})["children"])
    c.result("node.remove", node={"class": "McpTest", "path": [before - 1]})
    after_count = len(c.result("class.get", **{"class": "McpTest"})["children"])
    check("node.remove", after_count == before - 1,
          "%d -> %d" % (before, after_count))

    size_before = c.result("class.get", **{"class": "McpTest"})["size"]
    c.result("class.add_bytes", **{"class": "McpTest", "size": 16})
    size_after = c.result("class.get", **{"class": "McpTest"})["size"]
    check("class.add_bytes", size_after == size_before + 16,
          "%d -> %d" % (size_before, size_after))

    cpp = c.result("codegen.generate", language="cpp")["code"]
    check("codegen.generate cpp", "class McpTest" in cpp,
          "%d chars" % len(cpp))

    cs = c.result("codegen.generate", language="csharp")["code"]
    check("codegen.generate csharp",
          "McpTest" in cs and "struct" in cs or "class McpTest" in cs,
          "%d chars" % len(cs))

    saved = c.result("project.save", path=project_path)
    check("project.save", os.path.exists(saved["path"]),
          "%s (%d bytes)" % (saved["path"], os.path.getsize(saved["path"])))

    c.result("project.new")
    check("project.new clears classes",
          not any(cl["name"] == "McpTest" for cl in c.result("class.list")), "")

    loaded = c.result("project.load", path=project_path)
    check("project.load",
          any(cl["name"] == "McpTest" for cl in loaded["classes"]),
          "%d classes" % len(loaded["classes"]))
    check("class.list after load",
          any(cl["name"] == "McpTest" for cl in c.result("class.list")), "")

    info = c.result("project.info")
    check("project.info", info["path"] == project_path
          and "McpEnum" in info["enums"], json.dumps(info["enums"]))

    referenced = c.call("class.delete", **{"class": "McpOther"})
    refs = referenced.get("error", {}).get("data", {}).get("references")
    check("class.delete referenced -> -32005",
          error_code(referenced) == -32005 and refs == ["McpTest"],
          "references=%s" % (refs,))

    forced = c.result("class.delete", **{"class": "McpOther", "force": True})
    check("class.delete force", forced.get("ok") is True,
          "remaining=%s" % [cl["name"] for cl in c.result("class.list")])

    check("node.get bad path -> -32003",
          error_code(c.call("node.get",
                            node={"class": "McpTest", "path": [999]})) == -32003,
          str(c.call("node.get",
                     node={"class": "McpTest", "path": [999]}).get("error")))

    check("unknown class -> -32003",
          error_code(c.call("class.get", **{"class": "NoSuchClass"})) == -32003, "")

    check("unknown node type -> -32002",
          error_code(c.call("node.change_type",
                            node={"class": "McpTest", "path": [0]},
                            type="NotAType")) == -32002,
          str(c.call("node.change_type",
                     node={"class": "McpTest", "path": [0]},
                     type="NotAType").get("error")))

    check("codegen bad language -> -32002",
          error_code(c.call("codegen.generate", language="rust")) == -32002, "")

    # ------------------------------------------------------------------
    # Chunk 3: scanner / analysis
    # ------------------------------------------------------------------

    def wait_for_scan(label, timeout=60.0):
        deadline = time.time() + timeout
        st = c.result("scan.status")
        while st["running"] and time.time() < deadline:
            time.sleep(0.05)
            st = c.result("scan.status")
        check("scan.status %s finished" % label, st["running"] is False,
              "progress=%s total=%s" % (st["progress"], st["total"]))
        return st

    module_size = int(sleep_module["size"])
    module_range = {"start": sleep_module["start"],
                    "stop": hex(base + module_size),
                    "alignment": 4,
                    "fast": True,
                    # The Mach-O header lives in a read-only image section, so
                    # neither the writable nor the executable filter may exclude
                    # it. `image` keeps the scan inside the mapped binaries.
                    "writable": "indeterminate",
                    "executable": "indeterminate",
                    "cow": "indeterminate",
                    "private": True,
                    "image": True,
                    "mapped": False}

    # 0xFEEDFACF is the Mach-O 64 bit magic. IntegerMemoryComparer compares
    # signed 32 bit values; the RPC accepts the unsigned decimal form and casts
    # it (unchecked) to int, so 4277009103 and -17958193 are equivalent here.
    started = c.result("scan.first", value_type="integer", compare="equal",
                       value=4277009103, settings=module_range)
    check("scan.first", "job" in started, json.dumps(started))

    wait_for_scan("first")

    results = c.result("scan.results", offset=0, limit=1000)
    addresses = set(int(r["address"], 16) for r in results["results"])
    check("scan.results contains module base",
          base in addresses and results["total"] >= 1,
          "total=%d, %d returned" % (results["total"], len(results["results"])))
    check("scan.results value typed",
          all(r["value"] in (-17958193, 0xFEEDFACF) for r in results["results"]),
          str(results["results"][0]["value"]) if results["results"] else "-")

    c.result("scan.next", compare="equal", value=4277009103)
    wait_for_scan("next equal")
    again = c.result("scan.results", offset=0, limit=1000)
    check("scan.next equal keeps module base",
          base in set(int(r["address"], 16) for r in again["results"]),
          "total=%d" % again["total"])

    c.result("scan.next", compare="changed")
    wait_for_scan("next changed")
    changed = c.result("scan.results", offset=0, limit=1000)
    check("scan.next changed narrows results",
          changed["total"] <= again["total"],
          "%d -> %d" % (again["total"], changed["total"]))

    undone = c.result("scan.undo")
    check("scan.undo restores previous count",
          undone.get("ok") is True and undone["total"] == again["total"],
          "total=%d (expected %d)" % (undone["total"], again["total"]))

    # A wide, unaligned, non-fast scan over every mapped region takes long
    # enough that the immediately following scan.first must be rejected.
    c.result("scan.first", value_type="integer", compare="equal", value=0,
             settings={"alignment": 1, "fast": False,
                       "writable": "indeterminate",
                       "executable": "indeterminate",
                       "cow": "indeterminate",
                       "private": True, "image": True, "mapped": True})
    busy = c.call("scan.first", value_type="integer", compare="equal", value=0)
    check("scan.first while running -> -32006", error_code(busy) == -32006,
          str(busy.get("error")))

    check("scan.cancel", c.result("scan.cancel").get("ok") is True, "")
    wait_for_scan("cancel", timeout=90.0)

    check("scan.reset", c.result("scan.reset").get("ok") is True, "")
    check("scan.results after reset -> -32003",
          error_code(c.call("scan.results")) == -32003, "")
    check("scan.first bad value type -> -32002",
          error_code(c.call("scan.first", value_type="nope", compare="equal",
                            value=1)) == -32002, "")

    # --- analysis -----------------------------------------------------

    named = c.result("analysis.named_address", address=base)
    check("analysis.named_address(sleepBase)",
          named["name"] is not None and "sleep" in named["name"],
          repr(named["name"]))

    rtti = c.result("analysis.rtti", address=base)
    check("analysis.rtti returns", "rtti" in rtti, repr(rtti["rtti"]))

    preview = c.result("analysis.pointer_preview", address=base, size=64)
    check("analysis.pointer_preview module",
          preview["module"] is not None and preview["module"]["name"] == "sleep",
          json.dumps(preview["module"]))
    check("analysis.pointer_preview data_b64",
          base64.b64decode(preview["data_b64"])[:4] == b"\xcf\xfa\xed\xfe",
          base64.b64decode(preview["data_b64"])[:4].hex())
    check("analysis.pointer_preview guessed",
          isinstance(preview["guessed"], list) and len(preview["guessed"]) > 0
          and all("offset" in g and "type" in g for g in preview["guessed"]),
          "%d entries, first=%s" % (len(preview["guessed"]),
                                    preview["guessed"][0] if preview["guessed"] else "-"))
    check("analysis.pointer_preview section",
          preview["section"] is None or "name" in preview["section"],
          json.dumps(preview["section"])[:60])

    data_sections = [s for s in c.result("sections.list", module="sleep")
                     if s["category"] in ("DATA", "HEAP") and int(s["size"]) >= 16]
    guess_address = int(data_sections[0]["start"], 16) if data_sections else base
    guessed_node = c.result("analysis.guess", address=guess_address)
    check("analysis.guess",
          "type" in guessed_node and "reason" in guessed_node,
          "type=%s reason=%s" % (guessed_node["type"], guessed_node["reason"]))

    # The attached `sleep` is arm64 on Apple Silicon; the bundled disassembler
    # is x86 only, so only assert the call succeeds and returns a list.
    code = c.result("analysis.disassemble", address=base, length=64)
    check("analysis.disassemble",
          isinstance(code, list)
          and all(set(("address", "length", "bytes_hex", "text")) <= set(i)
                  for i in code),
          "%d instructions, first=%s" % (len(code), code[0]["text"] if code else "-"))

    func = c.result("analysis.disassemble", address=base, length=64,
                    function=True)
    check("analysis.disassemble function=True", isinstance(func, list),
          "%d instructions" % len(func))

    check("analysis.disassemble bad length -> -32002",
          error_code(c.call("analysis.disassemble", address=base,
                            length=0)) == -32002, "")

    dissected = c.result("analysis.dissect", **{"class": "McpTest"})
    check("analysis.dissect McpTest",
          isinstance(dissected.get("changed"), list),
          "%d changed: %s" % (len(dissected["changed"]),
                              [n["type"] for n in dissected["changed"]]))

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
