$ErrorActionPreference = 'Stop'
$dev = New-Object System.IO.Ports.SerialPort 'COM23',115200
$dev.WriteTimeout = 2000
try {
    $dev.Open()
    for ($i = 0; $i -lt 60; $i++) {
        $v = '{voltage}' + (3.0 + ([math]::Sin($i * 0.2) * 0.3)).ToString('F3') + ',' + (5.0 + ([math]::Cos($i * 0.1) * 0.2)).ToString('F3')
        $c = '{current}' + (0.5 + ([math]::Sin($i * 0.3) * 0.4)).ToString('F3')
        $t = '{temp}25.' + ($i % 50)
        $dev.Write($v + [char]10)
        $dev.Write($c + [char]10)
        $dev.Write($t + [char]10)
        Start-Sleep -Milliseconds 50
    }
    Write-Host 'multi-window frames sent'
} catch {
    Write-Host ('send error: ' + $_.Exception.Message)
} finally {
    if ($dev.IsOpen) { $dev.Close() }
}
