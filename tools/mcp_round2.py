# -*- coding: utf-8 -*-
# FreeCom MCP 第二轮：freecom-mcp.exe stdio JSON-RPC 全 25 工具 tools/call + 错误路径
# 前置：FreeCom.App 运行中且 MCP 已启用（17340）；com0com 端口对存在（COM24<->25）
import json
import os
import subprocess
import sys
import time

APP = r"C:\Users\admin\Desktop\串口上位机\freecom\publish\FreeCom\FreeCom.Mcp.exe"
TOKEN = "freecom-a33b2719d2604832af35697966dd40fd"
URL = "http://127.0.0.1:17340"

results = []
def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS  " if ok else "FAIL  ") + name + (("  " + detail) if detail and not ok else ""))

env = dict(os.environ, FREECOM_URL=URL, FREECOM_TOKEN=TOKEN)
proc = subprocess.Popen([APP], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        env=env, text=True, encoding="utf-8", bufsize=1)
print("DEBUG: exe started", flush=True)
seq = 0
def call(method, params=None, notify=False):
    global seq
    if notify:
        msg = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        proc.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        proc.stdin.flush()
        return None
    seq += 1
    msg = {"jsonrpc": "2.0", "id": seq, "method": method}
    if params is not None:
        msg["params"] = params
    proc.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
    proc.stdin.flush()
    print("DEBUG: sent", method, flush=True)
    while True:
        line = proc.stdout.readline()
        print("DEBUG: got line:", line[:80] if line else "<EOF>", flush=True)
        if not line:
            return None
        try:
            resp = json.loads(line)
        except ValueError:
            continue
        if resp.get("id") == seq:
            return resp

def call_tool(name, arguments, expect_ok=True):
    resp = call("tools/call", {"name": name, "arguments": arguments or {}})
    if resp is None:
        check(name, False, "无响应")
        return None
    if "error" in resp:
        check(name, False, "JSON-RPC 错误: " + str(resp["error"])[:150])
        return None
    r = resp.get("result", {})
    ok = not r.get("isError", False)
    if ok != expect_ok:
        check(name, False, "isError=" + str(r.get("isError")) + " text=" + str(r.get("content"))[:200])
        return None
    txt = "".join(c.get("text", "") for c in r.get("content", []))
    check(name, True)
    try:
        return json.loads(txt)
    except ValueError:
        return txt

# ---- 握手与目录 ----
r = call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                        "clientInfo": {"name": "round2", "version": "1.0"}})
check("initialize 握手", r is not None and r.get("result", {}).get("serverInfo", {}).get("name") == "freecom-mcp")
call("notifications/initialized", notify=True)
r = call("tools/list")
tools = r["result"]["tools"] if r and "result" in r else []
names = [t["name"] for t in tools]
check("tools/list 26 工具", len(tools) == 26, f"实际 {len(tools)}")
required = ["app_info","diag_connectivity","serial_list","serial_open","serial_close","serial_status",
            "device_send","receive_read","protocol_get","protocol_set","plot_windows","plot_data",
            "curve_export","receive_wait","send_expect","vcom_list","vcom_create","vcom_remove",
            "simulator_start","simulator_stop","curve_stats","protocol_help","send_history","export_raw","export_display","send_file"]
missing = [n for n in required if n not in names]
check("25 工具名齐全", not missing, ",".join(missing))

# ---- 无参/只读工具 ----
call_tool("diag_connectivity", {})
call_tool("app_info", {})
call_tool("serial_list", {"transport": "serial"})
call_tool("protocol_get", {})
call_tool("protocol_help", {})
call_tool("protocol_help", {"name": "MODBUSRTU"})
call_tool("vcom_list", {})
call_tool("serial_status", {})

# ---- 串口 + 模拟器 ----
call_tool("serial_open", {"transport": "serial", "params": {"port": "COM24", "baud": 115200}})
call_tool("serial_status", {})
call_tool("simulator_start", {"port": "COM25", "protocol": "TEXT", "intervalMs": 50})
time.sleep(1.2)

# ---- 收发与等待 ----
call_tool("receive_wait", {"contains": "{demo}", "direction": "rx", "timeoutMs": 3000})
call_tool("device_send", {"data": "R2-TEXT-PROBE", "format": "text"})
call_tool("device_send", {"data": "AA 55 01", "format": "hex"})
call_tool("receive_read", {"since": 0, "limit": 20, "format": "text"})
call_tool("send_history", {"limit": 10})
call_tool("send_expect", {"send": {"data": "PING-9"}, "contains": "PING-9", "timeoutMs": 1000})

# ---- 协议与绘图 ----
call_tool("protocol_set", {"name": "CSV", "options": {"window": "r2win"}})
time.sleep(0.3)
call_tool("simulator_start", {"port": "COM25", "protocol": "CSV", "intervalMs": 40})
time.sleep(1.2)
pw = call_tool("plot_windows", {})
win_id = None
wlist = pw.get("data", []) if isinstance(pw, dict) else (pw if isinstance(pw, list) else [])
for w in wlist:
    if isinstance(w, dict) and w.get("title") == "r2win":
        win_id = w.get("id")
check("plot_windows 含 r2win 窗口", win_id is not None, str(pw)[:150])
if win_id:
    call_tool("plot_data", {"id": win_id, "maxPoints": 100})
    call_tool("curve_stats", {"id": win_id})
    call_tool("curve_export", {"windowId": win_id})
call_tool("export_raw", {"path": os.path.join(os.environ.get("TEMP", "/tmp"), "r2-raw.dat")})
call_tool("export_display", {"path": os.path.join(os.environ.get("TEMP", "/tmp"), "r2-disp.txt"), "timestamp": True})

# ---- 错误路径（工具层） ----
call_tool("protocol_set", {"name": "NOPE"}, expect_ok=False)
call_tool("curve_stats", {"id": "nope"}, expect_ok=False)
call_tool("simulator_start", {"port": "COM25", "protocol": "NOPE"}, expect_ok=False)
call_tool("device_send", {}, expect_ok=False)

r = call("tools/call", {"name": "__no_such_tool__", "arguments": {}})
unknown_ok = (r is not None and ("error" in r or r.get("result", {}).get("isError", False)))
check("未知工具返回错误（JSON-RPC error 或 isError）", unknown_ok, str(r)[:120])
proc.stdin.write("{ broken json\n"); proc.stdin.flush()
r = None
while True:
    line = proc.stdout.readline()
    if not line:
        break
    try:
        resp = json.loads(line)
    except ValueError:
        continue
    if resp.get("id") is None and "error" in resp:
        r = resp
        break
check("坏 JSON 返回 -32700", r is not None and r["error"].get("code") == -32700, str(r)[:120])

# ---- 收尾 ----
call_tool("simulator_stop", {})
call_tool("serial_close", {})
call_tool("serial_status", {})

proc.stdin.close()
proc.wait(timeout=5)
pass_n = sum(1 for _, ok, _ in results if ok)
fail_n = len(results) - pass_n
print("")
print(f"==== MCP 第二轮（stdio 工具层）结果：PASS {pass_n} / FAIL {fail_n} / 共 {len(results)} ====")
for name, ok, detail in results:
    if not ok:
        print(f"  FAIL: {name}  {detail}")
sys.exit(1 if fail_n else 0)
