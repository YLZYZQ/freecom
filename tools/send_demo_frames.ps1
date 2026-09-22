$ErrorActionPreference = 'Stop'
$dev = New-Object System.IO.Ports.SerialPort 'COM23',115200
$dev.WriteTimeout = 2000   # 防止对端不收导致无限阻塞
try {
    $dev.Open()
    for ($i = 0; $i -lt 80; $i++) {
        $s = '{demo}' + ($i % 100) + ',' + (($i * 7) % 50) + ',' + ([math]::Sin($i * 0.1).ToString('F3'))
        $dev.Write($s + [char]10)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 800
    Write-Host 'frames sent: 80'
} catch {
    Write-Host ('send error: ' + $_.Exception.Message)
} finally {
    if ($dev.IsOpen) { $dev.Close() }
}
