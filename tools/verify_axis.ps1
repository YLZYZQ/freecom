$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$p = Get-Process FreeCom.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class AX {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@

# ---- 1. 选中绘图页（标题含"绘图"的 TabItem），点击其页签头 ----
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $tabCond)
$plotTab = $null
foreach ($t in $tabs) { if ($t.Current.Name -like '*绘图*') { $plotTab = $t; break } }
if (-not $plotTab) { $plotTab = $tabs[$tabs.Count - 1] }
$hr = $plotTab.Current.BoundingRectangle
[AX]::SetCursorPos([int]($hr.X + $hr.Width / 2), [int]($hr.Y + $hr.Height / 2)) | Out-Null
[AX]::mouse_event(6,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(10,0,0,0,[UIntPtr]::Zero)  # LEFTDOWN/UP 选中
Start-Sleep -Milliseconds 600

# ---- 2. 绘图区中心右键（ScottPlot 菜单是独立顶层弹窗）----
$plotRect = $plotTab.Current.BoundingRectangle
$winRect = $root.Current.BoundingRectangle
# TabItem 的 BoundingRectangle 可能只是页签头：用主窗口几何推算绘图区（顶部菜单+页签条约 110px）
$cx = [int]($winRect.X + $winRect.Width / 2)
$cy = [int]($winRect.Y + 110 + ($winRect.Height - 160) * 0.6)
Write-Host ("right-click at $cx,$cy  (tabRect=" + $plotRect.X + "," + $plotRect.Y + " " + $plotRect.Width + "x" + $plotRect.Height + ")")
[AX]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 300
[AX]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [AX]::mouse_event(16,0,0,0,[UIntPtr]::Zero)  # RIGHTDOWN/UP
Start-Sleep -Milliseconds 900

# ---- 3. 找菜单项：弹窗可能是根的直接子窗口，也可能挂在主窗口子树下 ----
function Find-MenuItem([string]$pattern) {
    $tops = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $miCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($w in $tops) {
        foreach ($mi in $w.FindAll([System.Windows.Automation.TreeScope]::Subtree, $miCond)) {
            if ($mi.Current.Name -like $pattern) { return $mi }
        }
    }
    foreach ($mi in $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $miCond)) {
        if ($mi.Current.Name -like $pattern) { return $mi }
    }
    return $null
}
$mi = Find-MenuItem '*设置坐标轴窗口*'
if (-not $mi) { Write-Host 'MENU-ITEM-NOT-FOUND'; exit 1 }
$inv = $null
if ($mi.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) { $inv.Invoke(); Write-Host 'menu item invoked' }
else { Write-Host 'invoke pattern missing'; exit 1 }
Start-Sleep -Milliseconds 900

# ---- 4. 对话框填写：X 滚动窗口 500 点 + Y 固定窗口 40~60 ----
# 有 Owner 的 WPF 窗口挂在主窗口 UIA 子树下，不在桌面根的直接子节点里
$dlg = $null
$winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $winCond)) {
    if ($w.Current.Name -like '*设置坐标轴显示窗口*') { $dlg = $w; break }
}
if (-not $dlg) {
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.Name -like '*设置坐标轴显示窗口*') { $dlg = $w; break }
    }
}
if (-not $dlg) { Write-Host 'DIALOG-NOT-FOUND'; exit 1 }
function SetVal($dlg, [string]$id, [string]$val) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
    $vp = $null
    if ($el -and $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($val) | Out-Null; Write-Host "$id = $val" }
    else { Write-Host "$id NOT FOUND" }
}
function SelRadio($dlg, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
    if ($el) {
        $sp = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() | Out-Null; Write-Host "$id selected" }
        else { Write-Host "$id select pattern missing" }
    } else { Write-Host "$id NOT FOUND" }
}
SelRadio $dlg 'RbXWindow'
SetVal $dlg 'TbXPoints' '500'
SelRadio $dlg 'RbYFixed'
SetVal $dlg 'TbYMin' '40'
SetVal $dlg 'TbYMax' '60'
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
foreach ($b in $dlg.FindAll([System.Windows.Automation.TreeScope]::Subtree, $btnCond)) {
    if ($b.Current.Name -like '*应用*') { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Host 'apply clicked'; break }
}
Start-Sleep -Milliseconds 1500

# ---- 5. 校验页签标题出现窗口模式标记 ----
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $tabCond)
$header = $tabs[$tabs.Count - 1].Current.Name
Write-Host ("tab header: " + $header)
if ($header -like '*滚动500点*' -and $header -like '*Y固定*') { Write-Host 'AXIS-MODE-OK'; exit 0 }
else { Write-Host 'AXIS-MODE-FAIL'; exit 1 }
