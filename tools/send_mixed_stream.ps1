$ErrorActionPreference = 'Stop'
$dev = New-Object System.IO.Ports.SerialPort 'COM23',115200
$dev.WriteTimeout = 2000
try {
    $dev.Open()
    for ($i = 0; $i -lt 40; $i++) {
        $dev.Write('[sys] boot seq ' + $i + [char]10)                                    # 纯日志行（非协议）
        $dev.Write('{mix}' + $i + ',' + ($i * 2) + [char]10)                              # 绘图帧
        if ($i % 10 -eq 5) { $dev.Write('{bad}nonsense,text' + [char]10) }                # 坏协议行（以{开头格式错）
        if ($i % 10 -eq 7) { $dev.Write('random log line ' + $i + [char]10) }             # 纯日志行
    }
    Write-Host 'mixed stream sent'
} catch {
    Write-Host ('send error: ' + $_.Exception.Message)
} finally {
    if ($dev.IsOpen) { $dev.Close() }
}
