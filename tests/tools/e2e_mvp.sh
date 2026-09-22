#!/usr/bin/env bash
# FreeCom v0.1.1 端到端验证（真实虚拟串口路径，无需物理串口）：
# 1. 无头宿主在 com0com 端口对上双端扮演（App 侧 SerialTransport + 设备侧流量）
# 2. HTTP 全链路：查端口→打开→切协议→读曲线→导出 CSV
# 3. MCP stdio 桥（真实子进程）：initialize / tools-list / tools-call
# 前置：com0com 已安装且存在端口对 COM22↔COM23（测试预置对）。
set -u
cd "$(dirname "$0")/../.."

DOTNET_ROOT="${DOTNET_ROOT:-C:\Users\admin\dotnet8}"
DOTNET="$(cygpath -u "$DOTNET_ROOT")/dotnet.exe"
HOST_DLL="$(pwd)/src/FreeCom.Host/bin/Release/net8.0/FreeCom.Host.dll"
MCP_EXE="$(pwd)/src/FreeCom.Mcp/bin/Release/net8.0/FreeCom.Mcp.exe"
PORT=17471
TOKEN="e2e-token-$$"
BASE="http://127.0.0.1:$PORT"
APP_PORT="${E2E_APP_PORT:-COM22}"
DEV_PORT="${E2E_DEV_PORT:-COM23}"
PASS=0; FAIL=0

ok()   { echo "  [PASS] $1"; PASS=$((PASS+1)); }
bad()  { echo "  [FAIL] $1"; FAIL=$((FAIL+1)); }
need() { if echo "$2" | grep -q "$1"; then ok "$3"; else bad "$3 (缺少: $1)"; echo "    got: $(echo "$2" | head -c 300)"; fi }

# 前置检查：端口对存在
export DOTNET_ROOT
PORTS=$(powershell.exe -NoProfile -Command "[System.IO.Ports.SerialPort]::GetPortNames() -join ','")
if ! echo "$PORTS" | grep -q "$APP_PORT" || ! echo "$PORTS" | grep -q "$DEV_PORT"; then
  echo "[SKIP] 未发现端口对 $APP_PORT/$DEV_PORT（当前端口: $PORTS）"
  echo "       请先安装 com0com 并创建测试端口对（见 README）。"
  exit 2
fi

"$DOTNET" "$HOST_DLL" --port "$PORT" --token "$TOKEN" --protocol TEXT \
  --app-port "$APP_PORT" --dev-port "$DEV_PORT" --interval 10 &
HOST_PID=$!
trap 'kill $HOST_PID 2>/dev/null' EXIT
sleep 3

echo "== 1) Health / 鉴权 =="
R=$(curl -s "$BASE/v1/health" -H "Authorization: Bearer $TOKEN")
need '"ok":true' "$R" "health 带 Token"
R=$(curl -s "$BASE/v1/health")
need 'unauthorized' "$R" "health 无 Token → 401"

echo "== 2) 端口列表（含虚拟串口对）=="
R=$(curl -s "$BASE/v1/serial/ports?transport=serial" -H "Authorization: Bearer $TOKEN")
need "$APP_PORT" "$R" "端口列表含 $APP_PORT"
need 'serial' "$R" "传输类型为 serial"

echo "== 3) 打开串口（真实路径）+ 切协议 =="
R=$(curl -s -X POST "$BASE/v1/serial/open" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d "{\"transport\":\"serial\",\"params\":{\"port\":\"$APP_PORT\",\"baud\":\"115200\"}}")
need '"state":"Open"' "$R" "打开串口 $APP_PORT"
R=$(curl -s -X PUT "$BASE/v1/protocol" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"name":"TEXT"}')
need '"name":"TEXT"' "$R" "协议切换 TEXT"

echo "== 4) 设备侧流量 → 曲线 =="
for i in $(seq 1 20); do
  W=$(curl -s "$BASE/v1/plot/windows" -H "Authorization: Bearer $TOKEN")
  if echo "$W" | grep -q '"points":[1-9]'; then break; fi
  sleep 1
done
need 'demo' "$W" "绘图窗口已创建（设备侧真实串口流量）"
WID=$(echo "$W" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -1)
D=$(curl -s "$BASE/v1/plot/windows/$WID/data?maxPoints=10" -H "Authorization: Bearer $TOKEN")
need '"xs"' "$D" "曲线数据可读"

echo "== 5) 导出 CSV =="
E=$(curl -s -X POST "$BASE/v1/export/curves" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}')
CSV=$(echo "$E" | sed -n 's/.*"path":"\([^"]*\)".*/\1/p' | head -1)
if [ -n "$CSV" ] && [ -f "$CSV" ] && head -1 "$CSV" | grep -q "window,curve,x,y"; then
  ok "曲线导出 CSV ($(wc -l < "$CSV") 行)"
else
  bad "曲线导出 CSV"
fi

echo "== 6) MCP stdio 桥（真实子进程）=="
export FREECOM_URL="$BASE" FREECOM_TOKEN="$TOKEN"
RPC=$(
  printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"serial_status","_arguments":{}}}'
)
R=$(echo "$RPC" | "$MCP_EXE")
need '"protocolVersion":"2024-11-05"' "$R" "MCP initialize"
need '"serial_list"' "$R" "MCP tools/list"
need '"isError":false' "$R" "MCP tools/call serial_status"

R=$(printf '%s\n' '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"app_info","_arguments":{}}}' | "$MCP_EXE")
need 'rxBytes' "$R" "MCP app_info"

kill $HOST_PID 2>/dev/null
echo
echo "===== E2E v2 结果: PASS=$PASS FAIL=$FAIL （真实串口路径 $APP_PORT ↔ $DEV_PORT）====="
[ "$FAIL" -eq 0 ]
