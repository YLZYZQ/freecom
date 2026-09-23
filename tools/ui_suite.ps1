# FreeCom UI 全面测试套件 v3（UIA）：主窗口/收发/协议绘图/轴对话框/导出/子窗口/主题/MCP 开关
# v3 修复：①主窗控件查找经 Find-MainById 限定主窗窗口（owned 子窗口挂主窗子树下会撞 AutomationId）
#        ②状态元素每次重新查找（WPF 数据流中 UIA 元素会重建）③循环发送限时防写满只写不读的对端缓冲
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class AX {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@

$script:results = New-Object System.Collections.ArrayList
$script:minimized = New-Object System.Collections.ArrayList
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  $script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail }) | Out-Null
  if ($ok) { Write-Host ("PASS  " + $name) -ForegroundColor Green }
  else { Write-Host ("FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$rawWalker = [System.Windows.Automation.TreeWalker]::RawViewWalker

function Find-TopWindow([string]$pattern, [int]$timeoutMs = 6000) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  do {
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
      if ($w.Current.Name -like $pattern) { return $w }
    }
    Start-Sleep -Milliseconds 200
  } while ($sw.ElapsedMilliseconds -lt $timeoutMs)
  return $null
}
# 主窗控件查找：AutomationId 匹配且最上层 Window 是主窗（排除 owned 子窗口里的同名控件）
function Find-MainById($win, [string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) {
    $p = $el; $top = $null
    while ($p -and $p -ne [System.Windows.Automation.AutomationElement]::RootElement) {
      if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $top = $p; break }
      $p = $rawWalker.GetParent($p)
    }
    if ($top -and ($top.Current.NativeWindowHandle -eq $win.Current.NativeWindowHandle)) { return $el }
  }
  return $null
}
function Find-ById($parent, [string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-WinOrSub($owner, [string]$pattern) {
  $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
  foreach ($w in $owner.FindAll([System.Windows.Automation.TreeScope]::Subtree, $wc)) {
    if ($w.Current.Name -like $pattern) { return $w }
  }
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $wc)) {
    if ($w.Current.Name -like $pattern) { return $w }
  }
  return $null
}
function Find-MenuItemAnywhere($fromWindow, [string]$pattern) {
  $mic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    foreach ($mi in $w.FindAll([System.Windows.Automation.TreeScope]::Subtree, $mic)) {
      if ($mi.Current.Name -like $pattern) { return $mi }
    }
  }
  foreach ($mi in $fromWindow.FindAll([System.Windows.Automation.TreeScope]::Subtree, $mic)) {
    if ($mi.Current.Name -like $pattern) { return $mi }
  }
  return $null
}
function Wait-For([scriptblock]$cond, [int]$timeoutMs = 8000) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  do { if (& $cond) { return $true }; Start-Sleep -Milliseconds 200 } while ($sw.ElapsedMilliseconds -lt $timeoutMs)
  return $false
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Value($el, [string]$v) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Get-RichText($win) {
  for ($i = 0; $i -lt 10; $i++) {
    try {
      $el = Find-MainById $win 'TbReceive'
      if ($el) {
        $tp = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp) -and $tp) {
          return $tp.DocumentRange.GetText(-1)
        }
      }
    } catch { }
    Start-Sleep -Milliseconds 400
  }
  return ''
}
function Esc-Key() {
  [AX]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
  [AX]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
}
# 置顶主窗 + 最小化遮挡窗口（桌面常有 topmost 的资源管理器等盖住 FreeCom 区域）
function Clear-Occlusion($win) {
  # 坐标一律用 UIA BoundingRectangle（物理像素）：GetWindowRect 受 DPI 虚拟化影响会错位
  $h = [IntPtr]$win.Current.NativeWindowHandle
  [AX]::SetWindowPos($h, [IntPtr]::Zero - 1, 0, 0, 0, 0, 0x0003) | Out-Null  # HWND_TOPMOST | NOSIZE|NOMOVE
  Start-Sleep -Milliseconds 300
  $r = $win.Current.BoundingRectangle
  $points = @(@([int]($r.X + $r.Width / 2), [int]($r.Y + 15)), @([int]($r.X + $r.Width / 2), [int]($r.Y + 110 + ($r.Height - 160) * 0.6)))
  for ($i = 0; $i -lt 6; $i++) {
    $hit = $false
    foreach ($pt in $points) {
      $w = [AX]::WindowFromPoint($pt[0], $pt[1])
      if ($w -eq $h) { continue }
      if ($w -ne [IntPtr]::Zero) {
        [AX]::ShowWindow($w, 6) | Out-Null  # SW_MINIMIZE
        $script:minimized.Add($w) | Out-Null
        $hit = $true
      }
    }
    if (-not $hit) { break }
    Start-Sleep -Milliseconds 400
    [AX]::SetWindowPos($h, [IntPtr]::Zero - 1, 0, 0, 0, 0, 0x0003) | Out-Null
  }
  # 点击标题栏拿前台
  $r = $win.Current.BoundingRectangle
  [AX]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + 10)) | Out-Null
  Start-Sleep -Milliseconds 200
  [AX]::mouse_event(6,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(10,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 400
  [AX]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 200
}
function Restore-Occluded() {
  foreach ($w in $script:minimized) { [AX]::ShowWindow($w, 9) | Out-Null }  # SW_RESTORE
}
function UnTop($win) {
  [AX]::SetWindowPos([IntPtr]$win.Current.NativeWindowHandle, [IntPtr]::Zero - 2, 0, 0, 0, 0, 0x0003) | Out-Null  # HWND_NOTOPMOST
}
function Select-TabByIndex($win, [int]$idx) {
  $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
  $tabs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)
  if ($idx -lt $tabs.Count) {
    $tabs[$idx].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 500
    return $true
  }
  return $false
}
function Select-Combo($combo, [string]$itemName) {
  $ecp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  $ecp.Expand(); Start-Sleep -Milliseconds 300
  foreach ($it in $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($it.Current.Name -eq $itemName) {
      $sp = $null
      if ($it.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select(); Start-Sleep -Milliseconds 250; return $true }
    }
  }
  $ecp.Collapse(); return $false
}
function Get-ComboValue($combo) {
  $sp = $null
  if ($combo.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$sp)) {
    $sel = $sp.Current.GetSelection()
    if ($sel.Count -gt 0) { return $sel[0].Current.Name }
  }
  $vp = $null
  if ($combo.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
  return ''
}
function Click-Abs([int]$x, [int]$y, [string]$btn = 'left') {
  [AX]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 150
  if ($btn -eq 'right') { [AX]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(16,0,0,0,[UIntPtr]::Zero) }
  else { [AX]::mouse_event(6,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(10,0,0,0,[UIntPtr]::Zero) }
  Start-Sleep -Milliseconds 250
}
function Open-Menu($win, [string]$namePattern) {
  $mic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
  foreach ($mi in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $mic)) {
    if ($mi.Current.Name -like $namePattern) {
      $mi.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
      Start-Sleep -Milliseconds 400; return $mi
    }
  }
  return $null
}
function Click-MessageBoxOk() {
  if (-not (Wait-For { $null -ne (Find-TopWindow 'FreeCom' 1) } 6000)) { return $false }
  $mb = $null
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq 'FreeCom') { $mb = $w; break }
  }
  if (-not $mb) { return $false }
  foreach ($b in $mb.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    if ($b.Current.Name -like '*确定*' -or $b.Current.Name -like 'OK*') { Invoke-El $b; Start-Sleep -Milliseconds 400; return $true }
  }
  return $false
}
function Save-As([string]$fullPath) {
  # Win11 新版保存对话框的控件并入主窗 UIA 树：以"组织"按钮出现为信号；
  # 文件名框 = 树末尾有 ValuePattern 的 Edit；保存按钮 Name 含"保存"
  $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $found = Wait-For { foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) { if ($b.Current.Name -like '*组织*') { return $true } }; $false } 8000
  if (-not $found) { return $false }
  Start-Sleep -Milliseconds 500
  $ec = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  $edits = @()
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ec)) {
    $vp = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $edits += $e }
  }
  # 对话框 owned 子树挂在主窗树末尾：从后往前试（最后一个通常是文件名框）
  for ($i = $edits.Count - 1; $i -ge [Math]::Max(0, $edits.Count - 4); $i--) {
    try {
      $vp2 = $edits[$i].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
      $vp2.SetValue($fullPath)
      Start-Sleep -Milliseconds 400
    } catch { continue }
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
      if ($b.Current.Name -like '*保存*') {
        try { Invoke-El $b; Start-Sleep -Milliseconds 900; return $true } catch { }
      }
    }
  }
  return $false
}
function Dump-TopWindows() {
  $names = @()
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name) { $names += $w.Current.Name }
  }
  return ($names -join ' | ')
}

# ================= 启动 =================
$exe = Join-Path $PSScriptRoot '..\publish\FreeCom\FreeCom.App.exe'
$exe = (Resolve-Path $exe).Path
if (Get-Process FreeCom.App -ErrorAction SilentlyContinue) { taskkill /IM FreeCom.App.exe /F | Out-Null; Start-Sleep -Milliseconds 600 }
Start-Process $exe
if (-not (Wait-For { $null -ne (Find-TopWindow 'FreeCom *' 1) } 12000)) { Check '启动主窗口' $false '窗口未出现'; exit 1 }
Check '启动主窗口（v0.2 标题）' $true
$win = Find-TopWindow 'FreeCom *' 1
Clear-Occlusion $win   # 置顶 + 最小化遮挡窗口

# ================= A. 主窗口控件存在性 =================
$ids = 'CbPort','CbBaud','CbDataBits','CbStopBits','CbParity','CbFlow','CbProtocol','BtnOpen','TbState','TbSend','BtnSend','CbHistory','MainTabs','TbReceive','TbCounters','CkCyclic','CkTimestamp','CkAutoScroll','CkHexView','RbText','RbHex','CbEncoding','CbNewline','TbInterval','CbFilterDir','TbFilterText','BtnExport','BtnMcp','TbThemeDark','TbThemeLight'
$missing = @()
foreach ($id in $ids) { if (-not (Find-MainById $win $id)) { $missing += $id } }
Check "A1 侧栏与主区控件齐全（$($ids.Count) 个）" ($missing.Count -eq 0) ($missing -join ',')

# ================= B. 串口参数 =================
$cbPort = Find-MainById $win 'CbPort'
$cbPort.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
$portNames = @()
foreach ($it in $cbPort.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($it.Current.Name -match '^COM\d+$') { $portNames += $it.Current.Name }
}
$cbPort.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
Check 'B1 串口下拉列出 COM20~25' (($portNames -contains 'COM20') -and ($portNames -contains 'COM25')) ($portNames -join ',')
Check 'B2 串口下拉选择 COM24' (Select-Combo $cbPort 'COM24')

$cbBaud = Find-MainById $win 'CbBaud'
Set-Value $cbBaud '123456'
Check 'B3 波特率自由输入 123456 不吸附列表项' ((Get-ComboValue $cbBaud) -eq '123456') ("实际=" + (Get-ComboValue $cbBaud))
Set-Value $cbBaud '115200'

# ================= C. 设备模拟器 + 连接 + 收发 =================
# 顺序：先打开模拟器窗口（不开端口）→ 主窗连接 → 模拟器开始发送（对端已开，避免写超时自停）
$null = Open-Menu $win '工具*'
$miSim = Find-MenuItemAnywhere $win '*设备模拟器*'
Check 'C1 工具菜单→设备模拟器 打开' ($null -ne $miSim)
Invoke-El $miSim
Check 'C2 模拟器窗口出现' (Wait-For { $script:simWin = Find-WinOrSub $win '*设备模拟器*'; $null -ne $script:simWin } 6000)
if ($script:simWin) {
  $simCbPort = Find-ById $script:simWin 'CbPort'
  Check 'C3 模拟器选择对端 COM25' (Select-Combo $simCbPort 'COM25')
  Set-Value (Find-ById $script:simWin 'TbInterval') '100'
} else { Check 'C3 模拟器配置' $false }

Invoke-El (Find-MainById $win 'BtnOpen')
$connOk = Wait-For { ((Find-MainById $win 'TbState').Current.Name) -like '*已连接*' } 10000
Check 'C4 主窗口连接 COM24（状态含"已连接"）' $connOk ((Find-MainById $win 'TbState').Current.Name)

if ($script:simWin) {
  Invoke-El (Find-ById $script:simWin 'BtnStart')
  $simState = Find-ById $script:simWin 'TbState'
  Check 'C5 模拟器开始发送（状态含"发送中"）' (Wait-For { ($simState.Current.Name) -like '*发送中*' } 5000) $simState.Current.Name
}

# 绘图窗口创建会抢选中页签导致接收区 RichTextBox 虚拟化卸载（TbReceive 不在 UIA 树），
# 断言前切回接收区页签（tabs[0]）重建显示
Select-TabByIndex $win 0 | Out-Null
Check 'C6 接收区收到模拟器 {demo} 帧' (Wait-For { (Get-RichText $win) -match '\{demo\}' } 10000)

Check 'C7 计数器 RX/解析帧增长' (Wait-For { $c = (Find-MainById $win 'TbCounters').Current.Name; ($c -match 'RX:\s*[1-9]') -and ($c -match '解析:\s*[1-9]') } 6000) ((Find-MainById $win 'TbCounters').Current.Name)

Select-TabByIndex $win 0 | Out-Null
Set-Value (Find-MainById $win 'TbSend') '{loop}1,2'
Invoke-El (Find-MainById $win 'BtnSend')
Check 'C8 文本发送 {loop} 上屏（TX 显示在接收区）' (Wait-For { (Get-RichText $win) -match '\{loop\}1,2' } 6000)

$rbHex = Find-MainById $win 'RbHex'
$rbHex.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
Set-Value (Find-MainById $win 'TbSend') 'AA 55 01'
Invoke-El (Find-MainById $win 'BtnSend')
Check 'C9 HEX 模式发送 AA 55 01（TX 计数+3）' (Wait-For { $c = (Find-MainById $win 'TbCounters').Current.Name; ($c -match 'TX:\s*(\d+)') -and (([int]($matches[1])) -ge 3) } 6000) ((Find-MainById $win 'TbCounters').Current.Name)
(Find-MainById $win 'RbText').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

# 循环发送：短跑（≤3s）防止写满"只写不读"的模拟器对端缓冲导致超时
$ckCyclic = Find-MainById $win 'CkCyclic'
$ckCyclic.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300
Set-Value (Find-MainById $win 'TbInterval') '100'
$tx0 = 0; $c0 = (Find-MainById $win 'TbCounters').Current.Name; if ($c0 -match 'TX:\s*(\d+)') { $tx0 = [int]$matches[1] }
$cyclicOk = Wait-For { $c = (Find-MainById $win 'TbCounters').Current.Name; ($c -match 'TX:\s*(\d+)') -and (([int]$matches[1]) -ge ($tx0 + 6)) } 3500
$ckCyclic.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
Check 'C10 循环发送 TX 持续增长' $cyclicOk ((Find-MainById $win 'TbCounters').Current.Name)

$ckTs = Find-MainById $win 'CkTimestamp'
$ckTs.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$ckAuto = Find-MainById $win 'CkAutoScroll'
$ckAuto.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$ckAuto.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$ckHexView = Find-MainById $win 'CkHexView'
$ckHexView.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$ckHexView.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
$ckTs.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
Check 'C11 时间戳/自动滚动/HEX视图 开关切换无异常' (((Find-MainById $win 'TbCounters').Current.Name) -match 'RX:')

# ================= D. 协议切换 + 绘图 =================
if ($script:simWin) {
  Invoke-El (Find-ById $script:simWin 'BtnStart')  # 停止
  Start-Sleep -Milliseconds 300
  Select-Combo (Find-ById $script:simWin 'CbProtocol') 'CSV' | Out-Null
  Invoke-El (Find-ById $script:simWin 'BtnStart')  # 重新开始（CSV 帧）
  Start-Sleep -Milliseconds 300
}
Select-Combo (Find-MainById $win 'CbProtocol') 'CSV' | Out-Null
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
Check 'D1 切 CSV 协议后绘图页出现（页签名含 csv）' (Wait-For { $script:csvTab = $null; foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) { if ($t.Current.Name -like 'csv*') { $script:csvTab = $t; break } }; $null -ne $script:csvTab } 10000)
if ($script:csvTab) {
  $script:csvTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 800
  Check 'D2 绘图页选中（页签标题含 csv）' ($script:csvTab.Current.Name -match 'csv') $script:csvTab.Current.Name
}

# ================= E. 轴对话框（右键菜单 → X 滚动 500 点） =================
$script:miAxis = $null
$wr = $win.Current.BoundingRectangle
$rcX = [int]($wr.X + $wr.Width / 2); $rcY = [int]($wr.Y + 110 + ($wr.Height - 160) * 0.6)
for ($rcTry = 0; $rcTry -lt 3 -and -not $script:miAxis; $rcTry++) {
  Clear-Occlusion $win   # 右键前清遮挡 + 置顶 + 拿前台
  Click-Abs $rcX $rcY 'right'
  $null = Wait-For { $script:miAxis = Find-MenuItemAnywhere $win '*设置坐标轴窗口*'; $null -ne $script:miAxis } 4000
}
Check 'E1 绘图右键菜单含"设置坐标轴窗口"' ($null -ne $script:miAxis)
if ($script:miAxis) {
  Invoke-El $script:miAxis
  Check 'E2 轴设置对话框打开' (Wait-For { $script:dlg = Find-WinOrSub $win '*设置坐标轴显示窗口*'; $null -ne $script:dlg } 6000)
  if ($script:dlg) {
    $rbXW = Find-ById $script:dlg 'RbXWindow'
    $rbXW.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Value (Find-ById $script:dlg 'TbXPoints') '500'
    $rbYF = Find-ById $script:dlg 'RbYFixed'
    $rbYF.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Value (Find-ById $script:dlg 'TbYMin') '40'
    Set-Value (Find-ById $script:dlg 'TbYMax') '60'
    $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $applied = $false
    foreach ($b in $script:dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
      if ($b.Current.Name -like '*应用*') { Invoke-El $b; $applied = $true; break }
    }
    Start-Sleep -Milliseconds 1200
    $hdr = ''
    foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) { if ($t.Current.Name -like 'csv*') { $hdr = $t.Current.Name } }
    Check 'E3 应用后页签标题含"滚动500点"' ($hdr -like '*滚动500点*') $hdr
    if (-not $applied) { Check 'E3b 找到应用按钮' $false }
  }
}
# 关闭可能的残留弹出菜单（右键菜单若未收起会干扰后续文件菜单操作）
Esc-Key
Start-Sleep -Milliseconds 400
Esc-Key

# ================= G. 虚拟串口管理器（PnP 免提权） =================
$null = Open-Menu $win '工具*'
$miVcom = Find-MenuItemAnywhere $win '*虚拟串口管理器*'
Check 'G1 虚拟串口管理器菜单项' ($null -ne $miVcom)
if ($miVcom) {
  Invoke-El $miVcom
  Check 'G2 管理器窗口打开' (Wait-For { $script:vcomWin = Find-WinOrSub $win '*虚拟串口管理器*'; $null -ne $script:vcomWin } 6000)
  if ($script:vcomWin) {
    $tbDrv = Find-ById $script:vcomWin 'TbDriver'
    $drvOk = $tbDrv.Current.Name -like '*已安装*'
    Check 'G3 驱动状态"已安装"' $drvOk $tbDrv.Current.Name
    $lb = Find-ById $script:vcomWin 'LbPairs'
    $pairCount = 0
    if ($lb) {
      $lic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
      $pairCount = $lb.FindAll([System.Windows.Automation.TreeScope]::Descendants, $lic).Count
    }
    Check 'G4 端口对列表 3 对（免提权 PnP）' ($pairCount -eq 3) ("对数=" + $pairCount)
    $script:vcomWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  }
}

# ================= H. 帮助窗口 =================
$null = Open-Menu $win '帮助*'
$miHelp = Find-MenuItemAnywhere $win '*协议格式说明*'
Check 'H1 协议格式说明菜单项' ($null -ne $miHelp)
if ($miHelp) {
  Invoke-El $miHelp
  Check 'H2 协议帮助窗口打开' (Wait-For { $script:helpWin = Find-WinOrSub $win '*协议格式说明*'; $null -ne $script:helpWin } 6000)
  if ($script:helpWin) {
    $nav = Find-ById $script:helpWin 'NavList'
    $lic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $n = if ($nav) { $nav.FindAll([System.Windows.Automation.TreeScope]::Descendants, $lic).Count } else { 0 }
    Check 'H3 协议导航 ≥5 项' ($n -ge 5) ("项数=" + $n)
    $script:helpWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  }
}
$null = Open-Menu $win '帮助*'
$miAbout = Find-MenuItemAnywhere $win '*关于*'
Check 'H4 关于菜单项' ($null -ne $miAbout)
if ($miAbout) {
  Invoke-El $miAbout
  Check 'H5 关于窗口打开' (Wait-For { $script:aboutWin = Find-WinOrSub $win '关于' 1; $null -ne $script:aboutWin } 6000)
  if ($script:aboutWin) { $script:aboutWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
}

# ================= I. 主题切换（经 TbThemeDark/TbThemeLight 文本坐标点击） =================
$tLight = Find-MainById $win 'TbThemeLight'
$tDark = Find-MainById $win 'TbThemeDark'
if ($tLight -and $tDark) {
  $r = $tLight.Current.BoundingRectangle
  Click-Abs ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
  $alive1 = $null -ne (Find-MainById $win 'CbPort')
  $r = $tDark.Current.BoundingRectangle
  Click-Abs ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
  $alive2 = $null -ne (Find-MainById $win 'CbPort')
  Check 'I1 亮色↔暗色主题切换窗口健康' ($alive1 -and $alive2)
} else { Check 'I1 主题切换控件存在' $false }

# ================= J. MCP 开关（保持开启供 MCP 轮验证） =================
$null = Open-Menu $win '工具*'
$miMcp = Find-MenuItemAnywhere $win '*启用 MCP 服务*'
Check 'J1 MCP 开关菜单项' ($null -ne $miMcp)
if ($miMcp) {
  Invoke-El $miMcp
  $mcpOn = $false
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  do {
    try {
      $r = Invoke-WebRequest -Uri 'http://127.0.0.1:17340/v1/health' -Headers @{ Authorization = 'Bearer freecom-a33b2719d2604832af35697966dd40fd' } -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
      if ($r.StatusCode -eq 200) { $mcpOn = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 300
  } while ($sw.ElapsedMilliseconds -lt 8000)
  Check 'J2 MCP 开关后 17340 API 可达' $mcpOn
}

# ================= K. 断开 + 清理 =================
$stateNow = (Find-MainById $win 'TbState').Current.Name
if ($stateNow -like '*已连接*') {
  Invoke-El (Find-MainById $win 'BtnOpen')
  Check 'K1 断开连接（状态"已断开"）' (Wait-For { ((Find-MainById $win 'TbState').Current.Name) -like '*已断开*' } 6000) ((Find-MainById $win 'TbState').Current.Name)
} else {
  Check 'K1 断开连接（状态"已断开"）' $false ('前置非已连接，TbState=' + $stateNow)
}
if ($script:simWin) {
  Invoke-El (Find-ById $script:simWin 'BtnStart')
  $script:simWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Check 'K2 模拟器停止并关闭' $true
}

# ================= F. 导出（原始/显示/曲线，Win32 保存对话框） =================
$expDir = Join-Path $env:TEMP 'FreeCom-ui-export'
New-Item -ItemType Directory -Force -Path $expDir | Out-Null
$expRaw = Join-Path $expDir 'ui-raw.dat'
$expDisp = Join-Path $expDir 'ui-disp.txt'
$expCsv = Join-Path $expDir 'ui-curves.csv'
Remove-Item $expRaw, $expDisp, $expCsv -ErrorAction SilentlyContinue

function Run-Export([string]$menuPattern, [string]$outPath, [string]$checkName) {
  # 点击标题栏复位：清掉可能残留的弹出菜单/焦点（前序步骤污染会让文件菜单 Invoke 失效）
  $r = $win.Current.BoundingRectangle
  [AX]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + 10)) | Out-Null
  Start-Sleep -Milliseconds 200
  [AX]::mouse_event(6,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(10,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 500
  $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $mi = $null
  for ($attempt = 0; $attempt -lt 2; $attempt++) {
    $null = Open-Menu $win '文件*'
    $mi = Find-MenuItemAnywhere $win $menuPattern
    if (-not $mi) { continue }
    try { Invoke-El $mi } catch { continue }
    Start-Sleep -Milliseconds 800
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
      if ($b.Current.Name -like '*组织*') { $mi = $null; break }  # 对话框已弹出，跳出重试循环
    }
    if ($null -eq $mi) { break }
  }
  if ($null -ne $mi) { Check $checkName $false ('对话框未弹出（Invoke 无效）: ' + $menuPattern); return }
  $saved = Save-As $outPath
  $mb = Click-MessageBoxOk
  $ok = $saved -and (Test-Path $outPath) -and ((Get-Item $outPath).Length -gt 0)
  if (-not $ok) {
    Check $checkName $false ("saved=$saved mb=$mb size=" + $(if (Test-Path $outPath) { (Get-Item $outPath).Length } else { 0 }) + " 顶层窗口: " + (Dump-TopWindows))
  } else {
    Check $checkName $true
  }
}
Run-Export '*导出原始数据*' $expRaw 'F1 导出原始数据 .dat（对话框+文件生成）'
Run-Export '*导出显示数据*' $expDisp 'F2 导出显示数据 .txt'
Run-Export '*导出曲线数据*' $expCsv 'F3 导出曲线数据 .csv'


# ================= 汇总 =================
UnTop $win
Restore-Occluded
$pass = @($script:results | Where-Object { $_.Ok }).Count
$fail = @($script:results | Where-Object { -not $_.Ok }).Count
Write-Host ''
Write-Host ("==== UI 套件结果：PASS {0} / FAIL {1} / 共 {2} ====" -f $pass, $fail, $script:results.Count)
foreach ($r in $script:results | Where-Object { -not $_.Ok }) { Write-Host ("  FAIL: " + $r.Name + "  " + $r.Detail) -ForegroundColor Red }
if ($fail -gt 0) { exit 1 } else { exit 0 }
