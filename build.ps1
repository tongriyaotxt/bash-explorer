# build.ps1 — 一键重建 xs.exe / xg.exe
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1 [-Test]
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }

function Ensure-Bom([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return $false }
    $t = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($path, $t, (New-Object System.Text.UTF8Encoding($true)))
    return $true
}

Write-Host "== 源码编码检查 ==" -ForegroundColor Cyan
foreach ($f in @('xs\xs.cs', 'xs\xg.cs')) {
    $full = Join-Path $root $f
    if (-not (Test-Path $full)) { throw "缺源码：$full" }
    if (Ensure-Bom $full) { Write-Host "  补 BOM: $f" -ForegroundColor Yellow }
    else { Write-Host "  OK: $f" -ForegroundColor DarkGray }
}

function Build([string[]]$src, [string]$out, [string[]]$refs, [string]$target = 'winexe', [string[]]$extra = @()) {
    if (Test-Path $out) {
        try { Remove-Item $out -Force -ErrorAction Stop }
        catch { throw "输出被占用，先关掉正在运行的 $out" }
    }
    $args = @('/nologo', "/target:$target", "/out:$out") + ($refs | ForEach-Object { "/r:$_" }) + $extra + @($src)
    & $csc @args
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$($src -join ', ')" }
    $kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Host ("  {0}  {1} KB" -f (Split-Path $out -Leaf), $kb) -ForegroundColor Green
}

Write-Host "== 编译 ==" -ForegroundColor Cyan
$base = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
          'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll')
Build -src @(Join-Path $root 'xs\xs.cs') -out (Join-Path $root 'xs\xs.exe') -refs $base -target 'exe'   # 控制台程序

# xg 必须带版本资源 + 清单 + 图标：裸 csc 产物（无 CompanyName/无 manifest/无 icon）
# 会被 Defender 的 ML 启发式当成未签名木马（2026-11-10 命中 Trojan:Win32/Bearfoos.A!ml）。
$xgSrc   = @((Join-Path $root 'xs\xg.cs'), (Join-Path $root 'xs\xg.assembly.cs'))
$xgExtra = @('/optimize+', '/debug-', '/platform:anycpu')
$ico     = Join-Path $root 'xs\xg.ico'
$man     = Join-Path $root 'xs\xg.manifest'
if (Test-Path $ico) { $xgExtra += "/win32icon:$ico" } else { Write-Host "  缺图标 xs\xg.ico（跳过）" -ForegroundColor Yellow }
if (Test-Path $man) { $xgExtra += "/win32manifest:$man" } else { Write-Host "  缺清单 xs\xg.manifest（跳过）" -ForegroundColor Yellow }
Build -src $xgSrc -out (Join-Path $root 'xs\xg.exe') -refs $base -extra $xgExtra

# 产物自检：MpCmdRun 若在，立刻扫一遍，误报要在构建阶段发现
$mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path $mp) {
    $scan = & $mp -Scan -ScanType 3 -File (Join-Path $root 'xs\xg.exe') 2>&1 | Out-String
    if ($scan -match 'found no threats') { Write-Host "  Defender 扫描：干净" -ForegroundColor Green }
    else { Write-Host "  Defender 扫描：命中，检查是否又触发误报" -ForegroundColor Red; Write-Host $scan -ForegroundColor DarkGray }
}

if ($Test) {
    Write-Host "== 冒烟测试 ==" -ForegroundColor Cyan
    Push-Location $root
    $out = & (Join-Path $root 'xs\xs.exe') -c 'pwd'; $code = $LASTEXITCODE; Pop-Location
    if ($code -ne 0) { throw "xs.exe 冒烟失败" }
    Write-Host "  xs: $out" -ForegroundColor DarkGray
    Push-Location $root
    $out2 = & (Join-Path $root 'xs\xs.exe') -c 'ls'; Pop-Location
    Write-Host ("  xs ls: {0} 行" -f (@($out2).Count)) -ForegroundColor DarkGray

    # xg 自检：窗口自己开自己关，结果落到 %TEMP%\xg-selftest.txt
    $st = Join-Path $env:TEMP 'xg-selftest.txt'
    $crash = Join-Path $env:TEMP 'xg-crash.log'
    $histFile = Join-Path $env:TEMP 'xg-selftest-history.tsv'
    Remove-Item $st, $crash, $histFile -Force -ErrorAction SilentlyContinue
    $env:XG_HISTORY = $histFile          # 自检别污染真实历史
    & (Join-Path $root 'xs\xg.exe') --selftest $root
    $deadline = (Get-Date).AddSeconds(25)
    while (-not (Test-Path $st) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (-not (Test-Path $st)) { throw "xg --selftest 没有产出结果" }
    $lines = @(Get-Content $st -Encoding UTF8)
    Write-Host "  xg selftest:" -ForegroundColor DarkGray
    foreach ($l in $lines) { Write-Host "    $l" -ForegroundColor DarkGray }
    if ($lines -match '^EXCEPTION') { throw "xg 自检里出现异常（见上，或 %TEMP%\xg-crash.log）" }
    if ($lines -match '^FAIL') { throw "xg 自检有断言失败（见上面的 FAIL 行）" }
    if (Test-Path $crash) { throw "xg 产生了崩溃日志：$crash" }
}

Write-Host ""
Write-Host "完成。启动：xs\xs.exe  或  xs\xg.exe" -ForegroundColor Cyan
