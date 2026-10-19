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

function Build([string]$src, [string]$out, [string[]]$refs) {
    if (Test-Path $out) {
        try { Remove-Item $out -Force -ErrorAction Stop }
        catch { throw "输出被占用，先关掉正在运行的 $out" }
    }
    $args = @('/nologo', '/target:winexe', "/out:$out") + ($refs | ForEach-Object { "/r:$_" }) + @($src)
    & $csc @args
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$src" }
    $kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Host ("  {0}  {1} KB" -f (Split-Path $out -Leaf), $kb) -ForegroundColor Green
}

Write-Host "== 编译 ==" -ForegroundColor Cyan
$base = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
          'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll')
Build (Join-Path $root 'xs\xs.cs') (Join-Path $root 'xs\xs.exe') $base
Build (Join-Path $root 'xs\xg.cs') (Join-Path $root 'xs\xg.exe') $base

if ($Test) {
    Write-Host "== 冒烟测试 ==" -ForegroundColor Cyan
    $out = & (Join-Path $root 'xs\xs.exe') -c 'pwd' $root
    if ($LASTEXITCODE -ne 0) { throw "xs.exe 冒烟失败" }
    Write-Host "  xs: $out" -ForegroundColor DarkGray
    $out2 = & (Join-Path $root 'xs\xs.exe') -c 'ls' $root
    Write-Host ("  xs ls: {0} 行" -f (@($out2).Count)) -ForegroundColor DarkGray
    Write-Host "  xg: 需人眼确认（start xs\xg.exe）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "完成。启动：xs\xs.exe  或  xs\xg.exe" -ForegroundColor Cyan
