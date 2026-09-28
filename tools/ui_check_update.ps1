$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
try {
  $win = $null
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -like 'FreeCom *') { $win = $w; break }
  }
  if (-not $win) { Write-Output 'NO-MAIN-WIN'; exit 1 }
  Write-Output ('main: ' + $win.Current.Name)
  $mic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
  $help = $null
  foreach ($mi in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $mic)) {
    if ($mi.Current.Name -like '帮助*') { $help = $mi; break }
  }
  if (-not $help) { Write-Output 'NO-HELP-MENU'; exit 1 }
  $help.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 600
  $target = $null
  foreach ($w2 in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    foreach ($mi in $w2.FindAll([System.Windows.Automation.TreeScope]::Subtree, $mic)) {
      if ($mi.Current.Name -like '*检查更新*') { $target = $mi; break }
    }
    if ($target) { break }
  }
  if (-not $target) { Write-Output 'NO-UPDATE-MENU-ITEM'; exit 1 }
  Write-Output ('item: ' + $target.Current.Name)
  $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output 'invoked'
  $dlg = $null
  for ($i = 0; $i -lt 10 -and -not $dlg; $i++) {
    Start-Sleep -Seconds 2
    foreach ($w2 in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
      if ($w2.Current.Name -eq 'FreeCom 更新提醒') { $dlg = $w2; break }
    }
    if (-not $dlg) {
      $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
      foreach ($w2 in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $wc)) {
        if ($w2.Current.Name -eq 'FreeCom 更新提醒') { $dlg = $w2; break }
      }
    }
  }
  if (-not $dlg) { Write-Output 'NO-DIALOG'; exit 1 }
  Write-Output 'DIALOG-FOUND'
  $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  foreach ($t in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) { Write-Output ('TEXT: ' + $t.Current.Name) }
  $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  foreach ($b in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
    if ($b.Current.Name -like '*确定*') { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'CLOSED'; break }
  }
} catch {
  Write-Output ('EX: ' + $_.Exception.Message)
}
