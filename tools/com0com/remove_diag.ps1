$ErrorActionPreference = 'Continue'
Set-Location 'C:\Program Files (x86)\com0com'
$r = @()
$r += '=== list ==='
$listOutput = (& .\setupc.exe list 2>&1 | Out-String)
$r += $listOutput
$r += '=== EXITCODE(list)=' + $LASTEXITCODE + ' ==='

# 找出 COM20-25（测试预置对）之外的用户自建对，选编号最大的一个做 remove 试验
$pairs = @{}
foreach ($line in ($listOutput -split "`n")) {
    $t = $line.Trim()
    if ($t -match '^(CNCA(\d+))\s+PortName=(\S+)$') { $pairs["A$($Matches[2])] = $Matches[3] }
    if ($t -match '^(CNCB(\d+))\s+PortName=(\S+)$') { $pairs["B$($Matches[2])] = $Matches[3] }
}
$target = $null
foreach ($kv in $pairs.GetEnumerator()) {
    if ($kv.Key -like 'A*' -and $kv.Value -notmatch '^COM(2[0-5])$') {
        $num = $kv.Key.Substring(1)
        if (-not $target -or [int]$num -gt [int]($target.Substring(4))) { $target = "CNCA$num" }
    }
}
$r += "=== chosen target for remove test: $target ==="
if ($target) {
    $r += '--- try: remove by CNCA id ---'
    $r += (& .\setupc.exe remove $target 2>&1 | Out-String)
    $r += '=== EXITCODE(remove)=' + $LASTEXITCODE + ' ==='
    $r += '--- list after ---'
    $r += (& .\setupc.exe list 2>&1 | Out-String)
}
$r | Out-File -FilePath 'C:\Users\admin\Desktop\串口上位机\freecom\tools\com0com\remove_diag.txt' -Encoding UTF8
