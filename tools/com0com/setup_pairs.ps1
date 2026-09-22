$ErrorActionPreference = 'Continue'
Set-Location 'C:\Program Files (x86)\com0com'
$result = @()
$result += '=== BEFORE: setupc list ==='
$result += (& .\setupc.exe list 2>&1 | Out-String)

foreach ($pair in @(@('COM20','COM21'), @('COM22','COM23'), @('COM24','COM25'))) {
    $a, $b = $pair
    $result += ">>> install PortName=$a PortName=$b"
    $result += (& .\setupc.exe install "PortName=$a" "PortName=$b" 2>&1 | Out-String)
}

$result += '=== AFTER: setupc list ==='
$result += (& .\setupc.exe list 2>&1 | Out-String)
$result += '=== GetPortNames ==='
$result += ([System.IO.Ports.SerialPort]::GetPortNames() -join ', ')
$result += '=== DONE ==='
$result | Out-File -FilePath 'C:\Users\admin\Desktop\串口上位机\freecom\tools\com0com\pairs_result.txt' -Encoding UTF8
