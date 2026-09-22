param([string]$Port = 'COM21', [int]$DurationSec = 60, [string]$Tag = 'a')
$ErrorActionPreference = 'Continue'
$dev = New-Object System.IO.Ports.SerialPort $Port,115200
$dev.WriteTimeout = 3000
try { $dev.Open() } catch { Write-Host "OPEN-FAIL $Port : $($_.Exception.Message)"; exit 1 }
# 预构建 500 行批次（约 9KB），批量写以获得高吞吐
$sb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 500; $i++) {
    [void]$sb.Append('{soak' + $Tag + '}' + ($i % 97) + ',' + (($i * 13) % 89) + ',' + ([math]::Sin($i * 0.1).ToString('F3')) + [char]10)
}
$batch = $sb.ToString()
$sent = 0L
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
    try { $dev.Write($batch); $sent += 500 } catch { break }
    Start-Sleep -Milliseconds 15
}
$sw.Stop()
try { $dev.Close() } catch {}
Write-Host ("SENT {0} lines in {1:F1}s = {2:F0} lines/s" -f $sent, $sw.Elapsed.TotalSeconds, ($sent / $sw.Elapsed.TotalSeconds))
