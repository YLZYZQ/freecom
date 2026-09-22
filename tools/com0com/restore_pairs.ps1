$ErrorActionPreference = 'Continue'
Set-Location 'C:\Program Files (x86)\com0com'
$r = @()
foreach ($p in @(@('COM22','COM23'), @('COM24','COM25'))) {
    $r += "install PortName=$($p[0]) PortName=$($p[1])"
    $r += (& .\setupc.exe install "PortName=$($p[0])" "PortName=$($p[1])" 2>&1 | Out-String) + ('EXIT=' + $LASTEXITCODE)
}
$r += '=== final list ==='
$r += (& .\setupc.exe list 2>&1 | Out-String)
$r | Out-File -FilePath 'C:\Users\admin\Desktop\串口上位机\freecom\tools\com0com\restore_pairs.txt' -Encoding UTF8
