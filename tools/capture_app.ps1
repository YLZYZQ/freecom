Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
$p = Get-Process FreeCom.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$sig = @'
using System;
using System.Runtime.InteropServices;
public class WC {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public struct R { public int L, T, Rt, B; }
}
'@
Add-Type -TypeDefinition $sig
[WC]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 900
$r = New-Object WC+R
[WC]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Rt - $r.L; $h = $r.B - $r.T
if ($w -le 0 -or $h -le 0) { throw "bad rect" }
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$out = 'C:\Users\admin\AppData\Local\Temp\freecom_state.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host $out
