#Requires -Version 5.1
<#
  setup.ps1 —— xf 的安装 / 卸载（只动用户自己的目录，不碰注册表）

  安装做三件事：
    1) 在 %LOCALAPPDATA%\xf\app 建一个指向本项目目录的 ASCII 联结
       （因为项目路径含中文，写成 .cmd 会被 ASCII 化）
    2) 在 WindowsApps 放一个 xf.cmd，让 `xf` 能在任何目录直接敲
    3) 在 SendTo 放一个 xf 快捷方式，让资源管理器里能「右键 → 发送到 → xf」

  卸载按清单逐个删，删完校验：
    powershell -File setup.ps1 -Remove
#>
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$Root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$LinkHome  = Join-Path $env:LOCALAPPDATA 'xf'
$LinkApp   = Join-Path $LinkHome 'app'
$Shim      = Join-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps') 'xf.cmd'
$ShimXc    = Join-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps') 'xc.cmd'
$SendTo    = [Environment]::GetFolderPath('SendTo')
$Lnk       = Join-Path $SendTo 'xf.lnk'
$Manifest  = Join-Path $Root 'state\install.json'

function Say($m, $c = 'Cyan') { Write-Host "  $m" -ForegroundColor $c }

function New-Junction($path, $target) {
    if (Test-Path $path) { (Get-Item $path).Delete() }
    $null = & cmd /c "mklink /J `"$path`" `"$target`" 2>&1"
    if (-not (Test-Path (Join-Path $path 'xf.ps1'))) { throw "联结创建失败：$path" }
}

function Test-JunctionPointsTo($path, $target) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $i = Get-Item $path
        return ($i.LinkType -eq 'Junction') -and ($i.Target -contains $target)
    } catch { return $false }
}

# ------------------------------------------------------------------ 安装
if (-not $Remove) {
    Say "安装 xf" 'White'
    if (-not (Test-Path (Join-Path $Root 'state'))) { New-Item -ItemType Directory -Force (Join-Path $Root 'state') | Out-Null }

    New-Item -ItemType Directory -Force -Path $LinkHome | Out-Null
    try { New-Junction $LinkApp $Root; Say "联结 OK  $LinkApp -> $Root" 'Green' }
    catch { Say "联结失败：$($_.Exception.Message)" 'Red'; throw }

    $shimBody = @'
@echo off
chcp 65001 >nul
set "_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
"%_PS%" -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\xf\app\xf.ps1" %*
exit /b %ERRORLEVEL%
'@
    $created = $false
    if (Test-Path $Shim) {
        $old = Get-Content $Shim -Raw
        if ($old -notmatch 'LOCALAPPDATA%\\xf\\app') { throw "已存在同名 xf.cmd 且不是本程序，拒绝覆盖：$Shim" }
    } else { $created = $true }
    [IO.File]::WriteAllText($Shim, $shimBody, (New-Object System.Text.ASCIIEncoding))
    Say "入口 OK  $Shim" 'Green'

    $xcBody = @'
@echo off
chcp 65001 >nul
set "_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
"%_PS%" -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\xf\app\xc.ps1" %*
exit /b %ERRORLEVEL%
'@
    if (Test-Path $ShimXc) {
        $oldxc = Get-Content $ShimXc -Raw
        if ($oldxc -notmatch 'LOCALAPPDATA%\\xf\\app\\xc') { throw "已存在同名 xc.cmd 且不是本程序，拒绝覆盖：$ShimXc" }
    }
    [IO.File]::WriteAllText($ShimXc, $xcBody, (New-Object System.Text.ASCIIEncoding))
    Say "入口 OK  $ShimXc" 'Green'

    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($Lnk)
    $sc.TargetPath       = Join-Path $LinkApp 'sendto.cmd'
    $sc.WorkingDirectory = $LinkApp
    $sc.Description      = 'xf - send selection to bash'
    $sc.IconLocation     = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe,0"
    $sc.Save()
    Say "SendTo OK  $Lnk" 'Green'

    [pscustomobject]@{
        installedAt = (Get-Date).ToString('s')
        root        = $Root
        junction    = $LinkApp
        shim        = $Shim
        shortcut    = $Lnk
        shimCreated = $created
    } | ConvertTo-Json | Set-Content -LiteralPath $Manifest -Encoding UTF8
    Say "清单  $Manifest" 'DarkGray'

    Write-Host ""
    Say "装好了。验证：" 'White'
    Say 'xf here' 'Yellow'
    Write-Host ""
    Say "卸载：powershell -ExecutionPolicy Bypass -File `"$Root\setup.ps1`" -Remove" 'DarkGray'
    exit 0
}

# ------------------------------------------------------------------ 卸载
Say "卸载 xf" 'White'
$stopped = 0

if (Test-Path $Lnk) {
    $ws = New-Object -ComObject WScript.Shell
    $target = ''
    try { $target = $ws.CreateShortcut($Lnk).TargetPath } catch {}
    if ($target -like '*xf*' -or $target -like '*sendto.cmd*') {
        Remove-Item $Lnk -Force; $stopped++; Say "已删快捷方式  $Lnk" 'Green'
    } else { Say "跳过（不是本程序的快捷方式）：$target" 'Yellow' }
} else { Say "无快捷方式" 'DarkGray' }

if (Test-Path $Shim) {
    $body = Get-Content $Shim -Raw
    if ($body -match 'LOCALAPPDATA%\\xf\\app') {
        Remove-Item $Shim -Force; $stopped++; Say "已删入口  $Shim" 'Green'
    } else { Say "跳过（不是本程序的入口）：$Shim" 'Yellow' }
} else { Say "无入口" 'DarkGray' }

if (Test-Path $ShimXc) {
    $bodyxc = Get-Content $ShimXc -Raw
    if ($bodyxc -match 'LOCALAPPDATA%\\xf\\app\\xc') {
        Remove-Item $ShimXc -Force; $stopped++; Say "已删入口  $ShimXc" 'Green'
    } else { Say "跳过（不是本程序的入口）：$ShimXc" 'Yellow' }
} else { Say "无 xc 入口" 'DarkGray' }

if (Test-Path $LinkApp) {
    try { (Get-Item $LinkApp).Delete(); $stopped++; Say "已删联结  $LinkApp" 'Green' }
    catch { Say "联结删除失败：$($_.Exception.Message)" 'Red' }
} else { Say "无联结" 'DarkGray' }

if (Test-Path $LinkHome) {
    $rest = @(Get-ChildItem $LinkHome -Force -ErrorAction SilentlyContinue)
    if ($rest.Count -eq 0) { Remove-Item $LinkHome -Force -ErrorAction SilentlyContinue; Say "已删空目录  $LinkHome" 'Green' }
    else { Say "保留（里面还有别的东西）：$LinkHome" 'Yellow' }
}

if (Test-Path $Manifest) { Remove-Item $Manifest -Force }

Write-Host ""
Say "校验：" 'White'
Say ("快捷方式存在 = " + (Test-Path $Lnk))
Say ("入口存在     = " + (Test-Path $Shim))
Say ("联结存在     = " + (Test-Path $LinkApp))
Say "注册表：本程序从未写入，无需清理" 'DarkGray'
Say "项目目录未被删除，如需彻底清掉：Remove-Item -Recurse -Force 'D:\项目\闲扯\explorer-bash'" 'DarkGray'
