param([string]$SelectName, [string]$OutPng)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process FreeCom.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, $cond)
Write-Host ("tabs: " + ($tabs.Count))
for ($i = 0; $i -lt $tabs.Count; $i++) {
    $name = $tabs[$i].Current.Name
    Write-Host ("  [$i] " + $name)
    if ($SelectName -ne '' -and $name -like "$SelectName*") {
        $sp = $null
        if ($tabs[$i].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) {
            $sp.Select()
            Write-Host ("selected: " + $name)
        }
    }
}
Start-Sleep -Milliseconds 900
$sig = @'
using System;
using System.Runtime.InteropServices;
public class WT {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public struct R { public int L, T, Rt, B; }
}
'@
Add-Type -TypeDefinition $sig
[WT]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object WT+R
[WT]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap(($r.Rt - $r.L), ($r.B - $r.T))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("saved: " + $OutPng)
