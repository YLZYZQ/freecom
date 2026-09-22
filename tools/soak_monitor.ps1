param([int]$DurationSec = 60, [string]$OutCsv = 'C:\Users\admin\AppData\Local\Temp\freecom_soak_monitor.csv')
Remove-Item $OutCsv -ErrorAction SilentlyContinue
'time,pid,wsMB,cpuSec,handles' | Out-File $OutCsv -Encoding utf8
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
    foreach ($p in Get-Process FreeCom.App -ErrorAction SilentlyContinue) {
        $p.Refresh()
        '{0:HH\:mm\:ss},{1},{2:F1},{3:F1},{4}' -f (Get-Date), $p.Id, ($p.WorkingSet64 / 1MB), $p.CPU, $p.HandleCount | Out-File $OutCsv -Append -Encoding utf8
    }
    Start-Sleep -Seconds 5
}
Write-Host "monitor done -> $OutCsv"
