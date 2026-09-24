# FreeCom MCP 第一轮：发布版 App 直调 Control API 全 27 端点扫描（正常路径 + 错误路径）
# 前置：FreeCom.App 运行中且已启用 MCP 服务（17340）
$ErrorActionPreference = 'Continue'
$Token = 'freecom-a33b2719d2604832af35697966dd40fd'
$Base = 'http://127.0.0.1:17340'
$H = @{ Authorization = "Bearer $Token" }
$results = New-Object System.Collections.ArrayList
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail }) | Out-Null
  if ($ok) { Write-Host ("PASS  " + $name) -ForegroundColor Green } else { Write-Host ("FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}
function Req([string]$method, [string]$path, $body = $null, $auth = $true, $timeout = 15) {
  $headers = @{}
  if ($auth) { $headers.Authorization = "Bearer $Token" }
  $params = @{ Uri = "$Base$path"; Method = $method; Headers = $headers; TimeoutSec = $timeout; UseBasicParsing = $true; ErrorAction = 'SilentlyContinue' }
  if ($null -ne $body) { $params.Body = $body; $params.ContentType = 'application/json' }
  try { return Invoke-WebRequest @params } catch {
    if ($_.Exception.Response) { return $_.Exception.Response }
    return $null
  }
}
function StatusOf($resp) { if ($resp -is [System.Net.HttpWebResponse]) { return [int]$resp.StatusCode } elseif ($null -ne $resp) { return [int]$resp.StatusCode } else { return 0 } }
function BodyOf($resp) {
  try {
    if ($resp -is [System.Net.HttpWebResponse]) { $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); return $sr.ReadToEnd() }
    return $resp.Content
  } catch { return '' }
}
function JsonData($resp) { $j = BodyOf $resp | ConvertFrom-Json; return $j.data }

# ---- 鉴权 ----
$r0 = Req GET '/v1/health' $null $false
Check '鉴权: 无 token 401' ((StatusOf $r0) -eq 401)
$r0b = Req GET '/v1/health' $null $true
Check '鉴权: 正确 token 200' ((StatusOf $r0b) -eq 200)

# ---- 目录与信息 ----
$r1 = Req GET '/v1/capabilities'
$cap = (BodyOf $r1) | ConvertFrom-Json
Check 'capabilities: 28 端点' ($cap.data.endpoints.Count -eq 28)
Check 'capabilities: 26 工具' ($cap.data.tools.Count -eq 26)
Check 'capabilities: 5 协议' ($cap.data.protocols.Count -eq 5)
$r2 = Req GET '/v1/app/info'
$info = JsonData $r2
Check 'app/info: 版本与计数' ($info.version -eq '0.1.0' -and ($null -ne $info.rxBytes))

# ---- 串口 ----
$r3 = Req GET '/v1/serial/ports?transport=serial'
$ports = @(JsonData $r3 | ForEach-Object { $_.name })
Check 'serial/ports 含 COM20~25' (($ports -contains 'COM24') -and ($ports -contains 'COM25'))
$r4 = Req POST '/v1/serial/open' (@{ transport = 'serial'; params = @{ port = 'COM24'; baud = '115200' } } | ConvertTo-Json -Depth 4)
Check 'serial/open 打开 COM24' ((JsonData $r4).state -eq 'Open')
$r5 = Req GET '/v1/serial/status'
Check 'serial/status Open' ((JsonData $r5).state -eq 'Open')
$rBad = Req POST '/v1/serial/open' (@{ transport = 'bluetooth' } | ConvertTo-Json)
Check '错误路径: 未知 transport 400' ((StatusOf $rBad) -eq 400)
$rBad2 = Req POST '/v1/serial/open' (@{ transport = 'serial'; params = @{ baud = '115200' } } | ConvertTo-Json -Depth 4)
Check '错误路径: 缺 port 报错' ((StatusOf $rBad2) -eq 400 -or (StatusOf $rBad2) -eq 409 -or (StatusOf $rBad2) -eq 500)
$rBadJson = Req POST '/v1/serial/open' '{ broken'
Check '错误路径: 坏 JSON 400' ((StatusOf $rBadJson) -eq 400)

# ---- 模拟器（TEXT 阶段：wait/收发验证） ----
$r6 = Req POST '/v1/simulator/start' (@{ port = 'COM25'; protocol = 'TEXT'; intervalMs = 50 } | ConvertTo-Json)
$sim = JsonData $r6
Check 'simulator/start running' ($sim.running -eq $true -and $sim.port -eq 'COM25')
$r6b = Req POST '/v1/simulator/start' (@{ port = 'COM25'; protocol = 'NOPE' } | ConvertTo-Json)
Check '错误路径: 未知协议 400' ((StatusOf $r6b) -eq 400)
$framesOk = $false
for ($i = 0; $i -lt 15; $i++) {
  $st2 = JsonData (Req GET '/v1/simulator/status')
  if ($st2.sentFrames -gt 3) { $framesOk = $true; break }
  Start-Sleep -Milliseconds 300
}
Check 'simulator/status 计数增长' $framesOk ("lastErr=" + $st2.lastError)

# ---- 收发与等待 ----
$r8 = Req POST '/v1/device/wait' (@{ contains = '{demo}'; direction = 'rx'; timeoutMs = 3000 } | ConvertTo-Json)
$w = JsonData $r8
Check 'device/wait 匹配 {demo}' ($w.matched -eq $true -and $w.items.Count -gt 0)
$r8b = Req POST '/v1/device/wait' (@{ contains = '__nope__'; timeoutMs = 400 } | ConvertTo-Json)
Check 'device/wait 超时 unmatched' ((JsonData $r8b).matched -eq $false)
$r8c = Req POST '/v1/device/wait' '{}'
$w8c = JsonData $r8c
Check 'device/wait 空 body=等任意数据（200+matched）' ((StatusOf $r8c) -eq 200 -and $w8c.matched -eq $true)
$r9 = Req POST '/v1/device/send' (@{ data = 'MCP-ROUND1-TX'; format = 'text' } | ConvertTo-Json)
Check 'device/send 文本 200' ((StatusOf $r9) -eq 200)
$r9b = Req POST '/v1/device/send' (@{ data = 'AA 55 01'; format = 'hex' } | ConvertTo-Json)
Check 'device/send HEX 200' ((StatusOf $r9b) -eq 200)
$r9c = Req POST '/v1/device/send' (@{ data = 'x'; format = 'morse' } | ConvertTo-Json)
Check '错误路径: 坏格式 400' ((StatusOf $r9c) -eq 400)
$r9d = Req POST '/v1/device/send' '{}'
Check '错误路径: 缺 data 400' ((StatusOf $r9d) -eq 400)
$r10 = Req POST '/v1/device/expect' (@{ send = @{ data = 'PING-77' }; contains = 'PING-77'; direction = 'rx'; timeoutMs = 1000 } | ConvertTo-Json -Depth 4)
$ex = JsonData $r10
Check 'device/expect 结构完整 sentBytes>0' ($ex.sentBytes -gt 0 -and ($null -ne $ex.wait))
$r11 = Req GET '/v1/device/receive?since=0&limit=50&format=text'
Check 'device/receive 分页返回条目' ((JsonData $r11).items.Count -gt 0)
# send-file（对端=模拟器不读，验证守卫与审计而非传输完成）
$sfPath = Join-Path $env:TEMP 'r1-sendfile.bin'
[System.IO.File]::WriteAllBytes($sfPath, (New-Object byte[] 100))
$rSf = Req POST '/v1/device/send-file' (@{ path = $sfPath } | ConvertTo-Json) $true 12
$sfStatus = StatusOf $rSf
Check 'device/send-file 端点可达（200 或写超时守卫 500）' ($sfStatus -eq 200 -or $sfStatus -eq 500) ("status=" + $sfStatus)
$r12 = Req GET '/v1/device/send-history?limit=10'
$hist = JsonData $r12
$histDetail = ($hist | ForEach-Object { $_.hex }) -join '|'
$histOk = $false
foreach ($e in $hist) { if ($e.hex -replace ' ', '' -match 'AA55') { $histOk = $true; break } }
Check 'device/send-history 记录新→旧（hex 含 AA55）' $histOk ("hexes=[" + $histDetail + "]")

# ---- 协议 ----
$r13 = Req PUT '/v1/protocol' (@{ name = 'CSV'; options = @{ window = 'round1' } } | ConvertTo-Json -Depth 3)
Check 'protocol/set CSV' ((JsonData $r13).name -eq 'CSV')
$r13b = Req PUT '/v1/protocol' (@{ name = 'NOPE' } | ConvertTo-Json)
Check '错误路径: 未知协议 400' ((StatusOf $r13b) -eq 400)
$r14 = Req GET '/v1/protocol'
Check 'protocol/get CSV' ((JsonData $r14).name -eq 'CSV')
$r15 = Req GET '/v1/protocol/help'
Check 'protocol/help 5 协议' ((JsonData $r15).Count -eq 5)
$r15b = Req GET '/v1/protocol/help?name=MODBUSRTU'
Check 'protocol/help 单协议' (((JsonData $r15b)[0].name) -eq 'MODBUSRTU')
$r15c = Req GET '/v1/protocol/help?name=bad'
Check '错误路径: 未知协议名 400' ((StatusOf $r15c) -eq 400)

# ---- 绘图（CSV 阶段：模拟器重发 CSV 帧 → round1 窗口） ----
$null = Req POST '/v1/simulator/stop' '{}'
$r16a = Req POST '/v1/simulator/start' (@{ port = 'COM25'; protocol = 'CSV'; intervalMs = 40 } | ConvertTo-Json)
Check 'simulator 切 CSV 重发' ((JsonData $r16a).running -eq $true)
$winOk = $false
for ($i = 0; $i -lt 15; $i++) {
  $wins = @(JsonData (Req GET '/v1/plot/windows'))
  foreach ($w in $wins) { if ($w.title -eq 'round1') { $winOk = $true; break } }
  if ($winOk) { break }
  Start-Sleep -Milliseconds 300
}
$infoDiag = JsonData (Req GET '/v1/app/info')
Check 'plot/windows 含 round1 窗口' $winOk ("titles=[" + (($wins | ForEach-Object { $_.title }) -join ',') + "] rx=" + $infoDiag.rxBytes + " frames=" + $infoDiag.framesParsed)
$winId = $null
foreach ($w in $wins) { if ($w.title -eq 'round1') { $winId = $w.id; break } }
$r17 = Req GET "/v1/plot/windows/$winId/data?maxPoints=100"
Check 'plot/windows/data 曲线数据' ((JsonData $r17).curves.Count -gt 0)
$r18 = Req GET "/v1/plot/windows/$winId/stats"
$stats = JsonData $r18
Check 'plot/windows/stats 统计（count>0）' ($stats.curves[0].count -gt 0)
$r18b = Req GET '/v1/plot/windows/nope/stats'
Check '错误路径: 未知窗口 404' ((StatusOf $r18b) -eq 404)
$r17b = Req GET '/v1/plot/windows/nope/data'
Check '错误路径: 未知窗口 data 404' ((StatusOf $r17b) -eq 404)

# ---- 导出 ----
$expDir = Join-Path $env:TEMP 'FreeCom-mcp-round1'
New-Item -ItemType Directory -Force -Path $expDir | Out-Null
$pRaw = Join-Path $expDir 'r1-raw.dat'; Remove-Item $pRaw -ErrorAction SilentlyContinue
$r19 = Req POST '/v1/export/raw' (@{ path = $pRaw } | ConvertTo-Json)
Check 'export/raw 生成文件' ((Test-Path $pRaw) -and ((Get-Item $pRaw).Length -gt 0))
$pDisp = Join-Path $expDir 'r1-disp.txt'; Remove-Item $pDisp -ErrorAction SilentlyContinue
$r20 = Req POST '/v1/export/display' (@{ path = $pDisp; timestamp = $true } | ConvertTo-Json)
Check 'export/display 生成文件' ((Test-Path $pDisp) -and ((Get-Item $pDisp).Length -gt 0))
# export/curves 不支持自定义 path（返回默认路径），按响应 path 验证
$r21 = Req POST '/v1/export/curves' '{}'
$cur = JsonData $r21
$curOk = $false
if ($cur.path) { $curOk = (Test-Path $cur.path) -and ((Get-Item $cur.path).Length -gt 0) }
Check 'export/curves 生成文件（返回路径）' $curOk ("path=" + $cur.path)

# ---- 虚拟串口 ----
$r22 = Req GET '/v1/vcom/pairs'
$pairs = @(JsonData $r22)
Check 'vcom/pairs 3 对（免提权）' ($pairs.Count -eq 3)
# vcom create/remove（合法参数）需要管理员提权（UAC），无人值守测试不调用；非法编号的 400 校验路径免提权，见下
$r22c = Req DELETE '/v1/vcom/pairs/abc' $null $true
$body = BodyOf $r22c
Check '错误路径: vcom remove 非法编号 400+JSON' ((StatusOf $r22c) -eq 400 -and ($body -match 'invalid_param'))

# ---- 收尾 ----
$r23 = Req POST '/v1/simulator/stop' '{}'
Check 'simulator/stop' ((JsonData $r23).running -eq $false)
$r24 = Req POST '/v1/serial/close' '{}'
Check 'serial/close Closed' ((JsonData $r24).state -eq 'Closed')

# ---- 汇总 ----
$pass = @($results | Where-Object { $_.Ok }).Count
$fail = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ''
Write-Host ("==== MCP 第一轮（API）结果：PASS {0} / FAIL {1} / 共 {2} ====" -f $pass, $fail, $results.Count)
foreach ($r in $results | Where-Object { -not $_.Ok }) { Write-Host ("  FAIL: " + $r.Name + "  " + $r.Detail) -ForegroundColor Red }
if ($fail -gt 0) { exit 1 } else { exit 0 }
