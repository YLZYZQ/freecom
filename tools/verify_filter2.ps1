$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process FreeCom.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
function FindById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $c)
}
# 切到接收区
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $tabCond)
if ($tabs.Count -gt 0) { $sp = $null; if ($tabs[0].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() }; Start-Sleep -Milliseconds 600 }

# 1) 文本关键字在 HEX 显示模式下也能命中（匹配解码文本）
$ft = FindById 'TbFilterText'
$vp = $null
$null = $ft.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)
$vp.SetValue('{mix') | Out-Null
Start-Sleep -Milliseconds 800
$doc = FindById 'TbReceive'
$tp = $null
$null = $doc.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)
$text = $tp.DocumentRange.GetText(-1)
$lines = @($text -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
$hit = @($lines | Where-Object { $_ -match '7B 6D 69 78 7D' }).Count
$miss = @($lines | Where-Object { $_ -notmatch '7B 6D 69 78 7D' }).Count
Write-Host ("HEX模式+文本关键字'{{mix': 行数=$($lines.Count) 命中=$hit 未命中=$miss（期望 miss=0, hit>0）")

# 2) TX/RX 颜色（TextPattern 前景色属性）
$vp.SetValue('') | Out-Null
Start-Sleep -Milliseconds 800
$null = $doc.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)
$ranges = $tp.DocumentRange.FindText('>>', $false, $false)
if ($ranges.Count -gt 0) {
    $color = $ranges[0].GetAttributeValue([System.Windows.Automation.TextPattern]::ForegroundColorAttribute)
    Write-Host ("TX 行前景色 COLORREF=0x" + ('{0:X6}' -f $color) + "（期望 BGR=0x23A6F5 即 RGB #F5A623 橙）")
}
$rangesRx = $tp.DocumentRange.FindText('<<', $false, $false)
if ($rangesRx.Count -gt 0) {
    $colorRx = $rangesRx[0].GetAttributeValue([System.Windows.Automation.TextPattern]::ForegroundColorAttribute)
    Write-Host ("RX 行前景色 COLORREF=0x" + ('{0:X6}' -f $colorRx) + "（期望 BGR=0xFEDC9C 即 RGB #9CDCFE 蓝）")
}

# 3) 串口参数控件在位
foreach ($id in @('CbDataBits','CbStopBits','CbParity','CbFlow','CkDtr','CkRts')) {
    $el = FindById $id
    $val = if ($el) { $el.Current.Name } else { '<missing>' }
    Write-Host ("$id => [$val]")
}
Write-Host done
