$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process FreeCom.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

# 先切回"接收区"选项卡（第 0 个），否则其内容不在可视化树中
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $tabCond)
if ($tabs.Count -gt 0) {
    $sp = $null
    if ($tabs[0].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() }
    Start-Sleep -Milliseconds 700
}

function Find-ById([string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond)
}
function Get-DocText {
    $doc = Find-ById 'TbReceive'
    $tp = $null
    if (-not $doc.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)) { return '<no TextPattern>' }
    return $tp.DocumentRange.GetText(-1)
}

Write-Host '=== 1) 发送两条 TX（UIA 模拟输入并点击发送）==='
$send = Find-ById 'TbSend'
$vp = $null
$null = $send.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)
$vp.SetValue('AT+RST') | Out-Null
$btn = Find-ById 'BtnSend'
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1500

$text = Get-DocText
$rxLines = ([regex]::Matches($text, '<<')).Count
$txLines = ([regex]::Matches($text, '>>')).Count
Write-Host ("混合显示: RX行标记=$rxLines  TX行标记=$txLines（期望两者都 > 0）")

Write-Host '=== 2) 筛选=仅发送 ==='
function Select-ComboItemByName([string]$name) {
    $combo = Find-ById 'CbFilterDir'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($li in $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $liCond)) {
        if ($li.Current.Name -eq $name) {
            $sel = $null
            if ($li.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) {
                $sel.Select(); return $true
            }
        }
    }
    return $false
}
$ok2 = Select-ComboItemByName '仅发送'
Start-Sleep -Milliseconds 900
$text = Get-DocText
$rx = ([regex]::Matches($text, '<<')).Count
$tx = ([regex]::Matches($text, '>>')).Count
Write-Host ("仅发送(selected=$ok2): RX标记=$rx（期望0）  TX标记=$tx（期望>0）")

Write-Host '=== 3) 筛选=全部 + 包含关键字 {mix ==='
$ok3 = Select-ComboItemByName '全部'
Start-Sleep -Milliseconds 600
$ft = Find-ById 'TbFilterText'
$null = $ft.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)
$vp.SetValue('{mix') | Out-Null
Start-Sleep -Milliseconds 900
$text = Get-DocText
$lines = $text -split "`n" | Where-Object { $_.Trim().Length -gt 0 }
$bad = ($lines | Where-Object { $_ -notmatch '\{mix' }).Count
$good = ($lines | Where-Object { $_ -match '\{mix' }).Count
Write-Host ("包含{{mix: 命中行=$good  非命中行=$bad（期望 bad=0, good>0）")

Write-Host '=== 4) 还原筛选 ==='
$null = $ft.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)
$vp.SetValue('') | Out-Null
Start-Sleep -Milliseconds 300
Write-Host done
