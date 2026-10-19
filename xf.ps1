<#
.SYNOPSIS
  xf - 把 Git Bash 和资源管理器粘在一起。

.DESCRIPTION
  资源管理器负责"看得见"，bash 负责"做得快"。xf 只做一件事：
  把 Explorer 的当前目录和选中项取出来，喂给一个**常驻**的 bash 会话，
  再把 bash 的产出送回去（终端输出 / explorer /select 高亮）。

  零安装、零注册表、零后台服务：整个东西就是这个目录，删掉即彻底回滚。
  兼容 PowerShell 5.1 与 7+。

.EXAMPLE
  xf here
  xf sel
  xf sh "wc -l @*"
  xf reveal @1
  xf                      # 交互 REPL
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Cmd = '',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

$ErrorActionPreference = 'Stop'

# 这台机器的 Console.InputEncoding 是"带 BOM 的 UTF-8"，.NET 创建子进程 stdin 时会先写 BOM，
# 让 bash 把 \xEF\xBB\xBFexec 当成命令名。换成无 BOM 编码即可。
try { [Console]::InputEncoding  = New-Object System.Text.UTF8Encoding($false) } catch {}
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
$script:Root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:StateDir = Join-Path $script:Root 'state'
$script:SelTsv   = Join-Path $script:StateDir '_sel.tsv'
$script:History  = Join-Path $script:StateDir '_hist.txt'
$script:Session  = $null
$script:Shell    = $null
$script:ExpandRows = @()

if (-not (Test-Path $script:StateDir)) { New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null }

# ---------------------------------------------------------------- bash 定位
$script:BashExe = if ($env:XF_BASH) { $env:XF_BASH } else { 'C:\Program Files\Git\bin\bash.exe' }
if (-not (Test-Path $script:BashExe)) {
    $cand = @('C:\Program Files\Git\usr\bin\bash.exe', 'C:\Program Files (x86)\Git\bin\bash.exe') |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $cand) { throw "找不到 bash.exe，可用环境变量 XF_BASH 指定" }
    $script:BashExe = $cand
}

# ---------------------------------------------------------------- 路径
function Get-ErrText {
    param($Err)
    $x = $Err.Exception
    while ($x.InnerException) { $x = $x.InnerException }
    return $x.Message
}
function ConvertTo-UnixPaths {
    param([string[]]$Win)
    $Win = @($Win | Where-Object { $_ })
    if ($Win.Count -eq 0) { return @() }
    $out = foreach ($p in $Win) {
        $x = [string]$p
        if ($x -match '^([A-Za-z]):(.*)$') {
            $d = $Matches[1].ToLower(); $t = $Matches[2] -replace '\\', '/'
            "/$d$t"
        }
        elseif ($x -match '^\\\\') { $x -replace '\\', '/' }
        else { $x -replace '\\', '/' }
    }
    return @($out)
}

function ConvertTo-UnixPath {
    # 单路径版本：避免单元素数组被 PowerShell 展开成标量后 [0] 取到首字符
    param([string]$Win)
    $r = @(ConvertTo-UnixPaths @($Win))
    if ($r.Count -eq 0) { return '' }
    return [string]$r[0]
}

function ConvertTo-WinPath {
    param([string]$Unix)
    if ($Unix -match '^/([A-Za-z])(/.*)?$') {
        $d = $Matches[1].ToUpper(); $t = $Matches[2] -replace '/', '\'
        "${d}:$t"
    }
    elseif ($Unix -match '^//') { $Unix -replace '/', '\' }
    else { $Unix }
}

function Quote-Bash {
    param([string]$s)
    "'" + ($s -replace "'", "'\''") + "'"
}

# ---------------------------------------------------------------- Explorer
function Get-Shell {
    if (-not $script:Shell) { $script:Shell = New-Object -ComObject Shell.Application }
    $script:Shell
}

function Get-ForegroundHwnd {
    if (-not ('XfWin32' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public class XfWin32 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
'@
    }
    return [int64][XfWin32]::GetForegroundWindow()
}

function Test-FsPath {
    param([string]$p)
    if (-not $p) { return $false }
    if ($p -match '^::') { return $false }
    if ($p -match '^[A-Za-z]:\\') { return $true }
    if ($p -match '^\\\\[^\\]+\\') { return $true }
    return $false
}

function Get-ExplorerContext {
    $sh = Get-Shell
    $wins = @($sh.Windows() | Where-Object { $_.Document -and $_.Document.Folder })
    if ($wins.Count -eq 0) { return $null }

    $fg = Get-ForegroundHwnd
    $sameHwnd = @($wins | Where-Object { [int64]$_.HWND -eq $fg })
    foreach ($w in $sameHwnd) {
        $p = [string]$w.Document.Folder.Self.Path
        if (Test-FsPath $p) {
            return [pscustomobject]@{ Hwnd = $fg; Path = $p; Item = $w; Source = 'foreground' }
        }
    }
    foreach ($w in $wins) {
        $p = [string]$w.Document.Folder.Self.Path
        if (Test-FsPath $p) {
            return [pscustomobject]@{ Hwnd = [int64]$w.HWND; Path = $p; Item = $w; Source = 'mru' }
        }
    }
    return $null
}

function Get-ExplorerSelection {
    param($Ctx)
    if (-not $Ctx) { return @() }
    try { return @($Ctx.Item.Document.SelectedItems()) } catch { return @() }
}

# ---------------------------------------------------------------- 选区清单
function Save-Selection {
    param([object[]]$Items)
    $rows = New-Object System.Collections.ArrayList
    foreach ($i in @($Items)) {
        if ($null -eq $i) { continue }
        $p = ''
        try { $p = [string]$i.Path } catch { continue }
        if (-not (Test-FsPath $p)) { continue }

        $isDir = $false
        try { $isDir = [bool]$i.IsFolder } catch { $isDir = $false }
        $size = 0
        if (-not $isDir) { try { $size = [long]$i.Size } catch { $size = 0 } }
        $mt = ''
        try { $mt = ([datetime]$i.ModifyDate).ToString('s') } catch { $mt = '' }
        $par = ''
        try { $par = [string]$i.ParentFolder.Self.Path } catch { $par = '' }
        if (-not $par) { try { $par = Split-Path -Parent $p } catch { $par = '' } }
        $nm = ''
        try { $nm = [string]$i.Name } catch { $nm = (Split-Path $p -Leaf) }

        [void]$rows.Add([pscustomobject]@{
            win   = $p
            unix  = (ConvertTo-UnixPath $p)
            name  = $nm
            dir   = $par
            ext   = [IO.Path]::GetExtension($p)
            kind  = $(if ($isDir) { 'dir' } else { 'file' })
            size  = $size
            mtime = $mt
        })
    }

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add("win`tunix`tname`tdir`text`tkind`tsize`tmtime")
    foreach ($r in $rows) {
        [void]$lines.Add(($r.win, $r.unix, $r.name, $r.dir, $r.ext, $r.kind, $r.size, $r.mtime) -join "`t")
    }
    if ((Test-Path $script:SelTsv) -and (Get-Item $script:SelTsv).Length -gt 0) {
        Copy-Item $script:SelTsv "$script:SelTsv.prev" -Force
    }
    [IO.File]::WriteAllLines($script:SelTsv, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    return @($rows)
}

function Read-Selection {
    if (-not (Test-Path $script:SelTsv)) { return @() }
    $lines = @(Get-Content -LiteralPath $script:SelTsv -Encoding UTF8)
    if ($lines.Count -le 1) { return @() }
    $h = $lines[0] -split "`t"
    $out = foreach ($l in $lines[1..($lines.Count - 1)]) {
        if (-not $l) { continue }
        $c = $l -split "`t"
        if ($c.Count -lt $h.Count) { continue }
        $o = [ordered]@{}
        for ($k = 0; $k -lt $h.Count; $k++) { $o[$h[$k]] = $c[$k] }
        [pscustomobject]$o
    }
    return @($out)
}

function Sync-Selection {
    $ctx = Get-ExplorerContext
    if (-not $ctx) { throw "没有可用的资源管理器窗口（虚拟文件夹不算）" }
    $sel = Get-ExplorerSelection $ctx
    if ($sel.Count -eq 0) {
        $folder = (Get-Shell).NameSpace($ctx.Path)
        if ($folder) { $sel = @($folder.Self) }
    }
    $rows = Save-Selection $sel
    return [pscustomobject]@{ Ctx = $ctx; Rows = $rows }
}

function Get-StaleReport {
    param([object[]]$Rows)
    $bad = foreach ($r in @($Rows)) {
        if (-not (Test-Path -LiteralPath $r.win)) { $r.win }
    }
    return @($bad)
}

# ---------------------------------------------------------------- 占位符
function Resolve-Token {
    param([string]$Tok)
    $Rows = @($script:ExpandRows)
    if ($Rows.Count -eq 0) { throw "选区为空：先在资源管理器里选中对象，或执行 xf sel / :paste" }
    $bad = Get-StaleReport $Rows
    if ($bad.Count) {
        throw ("清单已过期，以下对象不存在，请重新选择：`n  " + ($bad -join "`n  "))
    }
    switch -Regex ($Tok) {
        '^\*$' {
            return (($Rows | ForEach-Object { Quote-Bash $_.unix }) -join ' ')
        }
        '^cwd$' {
            $ctx = Get-ExplorerContext
            $p = if ($ctx) { $ctx.Path } else { (Get-Location).Path }
            return (Quote-Bash ((ConvertTo-UnixPath $p)))
        }
        '^d$' {
            $dirs = @($Rows | ForEach-Object { $_.dir } | Where-Object { $_ } | Select-Object -Unique)
            return (($dirs | ForEach-Object { Quote-Bash ((ConvertTo-UnixPath $_)) }) -join ' ')
        }
        '^\+(\w+)$' {
            $e = '.' + $Matches[1].ToLower()
            $pick = @($Rows | Where-Object { ([string]$_.ext).ToLower() -eq $e })
            if ($pick.Count -eq 0) { throw "选区里没有 *$e 文件" }
            return (($pick | ForEach-Object { Quote-Bash $_.unix }) -join ' ')
        }
        '^(\d{1,2})$' {
            $n = [int]$Matches[1]
            if ($n -lt 1 -or $n -gt $Rows.Count) { throw "@$n 超出范围（共 $($Rows.Count) 项）" }
            return (Quote-Bash $Rows[$n - 1].unix)
        }
    }
    return $Tok
}

function Expand-Placeholders {
    param([string]$Text, [object[]]$Rows)
    $script:ExpandRows = @($Rows)
    if ($Text -notmatch '@') { return $Text }
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
        param($m)
        Resolve-Token -Tok $m.Groups[1].Value
    }
    $out = [regex]::Replace($Text, '@(\*|cwd|d|\+[A-Za-z0-9]+|\d{1,2})(?![\w])', $evaluator)
    return $out
}

# ---------------------------------------------------------------- bash 会话
function Send-BashLine {
    # 用裸字节写 stdin：这个框架没有 StandardInputEncoding，StreamWriter 会带 BOM
    param([string]$Line)
    $s = $script:Session
    if (-not $s) { throw "没有活动的 bash 会话" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Line + "`n")
    $s.In.BaseStream.Write($bytes, 0, $bytes.Length)
    $s.In.BaseStream.Flush()
}
function Start-BashSession {
    param([string]$Cwd)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $script:BashExe
    $psi.Arguments              = '--login'
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $true
    try {
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $psi.StandardErrorEncoding  = [Text.Encoding]::UTF8
    } catch {}
    if (-not $Cwd -or -not (Test-Path -LiteralPath $Cwd)) { $Cwd = $env:USERPROFILE }
    $psi.WorkingDirectory = $Cwd
    foreach ($k in @('LANG', 'LC_ALL')) {
        try { $psi.EnvironmentVariables[$k] = 'zh_CN.UTF-8' } catch {}
    }
    try { $psi.EnvironmentVariables['TERM'] = 'dumb' } catch {}

    $p = [Diagnostics.Process]::Start($psi)
    $s = [pscustomobject]@{
        Proc  = $p
        In    = $p.StandardInput
        Out   = $p.StandardOutput
        Pump  = $null
        Token = 0
    }
    $script:Session = $s
    Send-BashLine 'exec 2>&1'
    Send-BashLine ("cd " + (Quote-Bash ((ConvertTo-UnixPath $Cwd))) + " 2>/dev/null")
    Send-BashLine "export PS1=''"
    return $s
}

function Receive-Bash {
    param([string]$Token, [int]$TimeoutSec = 60)
    $s = $script:Session
    if (-not $s) { throw "没有活动的 bash 会话" }
    if ($null -eq $s.Pump) { $s.Pump = $s.Out.ReadLineAsync() }

    $sb = New-Object Text.StringBuilder
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pat = "^\x01$Token (\d+)$"

    while ($true) {
        if ($s.Pump.Wait(150)) {
            $line = $s.Pump.Result
            if ($null -eq $line) { throw "bash 会话已结束" }
            if ($line -match $pat) {
                $s.Pump = $s.Out.ReadLineAsync()
                return [pscustomobject]@{ Output = $sb.ToString(); ExitCode = [int]$Matches[1] }
            }
            [void]$sb.AppendLine($line)
            $s.Pump = $s.Out.ReadLineAsync()
        }
        else {
            if ($s.Proc.HasExited) { throw "bash 进程退出（code=$($s.Proc.ExitCode)）" }
            if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) {
                $s.Pump = $null
                return [pscustomobject]@{ Output = ($sb.ToString() + "`n[xf] 等待超时 ${TimeoutSec}s"); ExitCode = 124 }
            }
        }
    }
}

function Invoke-Bash {
    param([string]$Command, [int]$TimeoutSec = 60)
    $s = $script:Session
    if (-not $s -or $s.Proc.HasExited) {
        Start-BashSession -Cwd (Get-Location).Path | Out-Null
        $s = $script:Session
    }
    $s.Token++
    $tok = "XF$($s.Token)_$([guid]::NewGuid().ToString('N').Substring(0,8))"
    Send-BashLine $Command
    Send-BashLine ("printf '\001$tok %s\n' `$?")

    return Receive-Bash -Token $tok -TimeoutSec $TimeoutSec
}

function Get-BashCwd {
    $r = Invoke-Bash -Command 'pwd' -TimeoutSec 15
    return ($r.Output.Trim())
}

function Stop-BashSession {
    if ($script:Session -and -not $script:Session.Proc.HasExited) {
        try { Send-BashLine 'exit' } catch {}
        Start-Sleep -Milliseconds 150
        try { if (-not $script:Session.Proc.HasExited) { $script:Session.Proc.Kill() } } catch {}
    }
    $script:Session = $null
}

# ---------------------------------------------------------------- 动作
function Show-Help {
    @'
xf —— 资源管理器 × Git Bash

用法
  xf                          交互 REPL（bash 常驻，cd/变量/历史都保留）
  xf here                     打印资源管理器当前目录
  xf sel                      把当前选中项写进清单并显示
  xf list                     显示清单
  xf paste                    从剪贴板（Ctrl+C 复制的文件）取清单
  xf sh "<命令>"              在资源管理器目录里执行，支持 @ 占位符
  xf reveal [@N|<路径>]       在资源管理器里高亮该对象
  xf find <通配符>            在目录下搜索，结果写入清单
  xf help

占位符
  @*        全部选中项          @1 @2 …  第 N 项
  @+md      选区里所有 .md       @d       选中项所在目录（去重）
  @cwd      资源管理器当前目录

REPL 内
  :sel  :list  :paste  :here  :reveal [@N]  :find <glob>  :cd <路径>  :q
  其他任何一行按 bash 命令执行（自动展开占位符）

状态
  state\_sel.tsv   当前选区（TSV，可 diff、可校验）
  state\_sel.tsv.prev  上一版，误覆盖可恢复
  state\_hist.txt  历史

回滚
  删掉整个目录即可：不写注册表、不起服务、不改 PATH。
'@
}

function Show-Selection {
    $rows = Read-Selection
    if ($rows.Count -eq 0) {
        Write-Host "[空] 还没有清单。在资源管理器里选中对象后执行 xf sel" -ForegroundColor DarkGray
        return
    }
    $i = 0
    foreach ($r in $rows) {
        $i++
        $tag = if ($r.kind -eq 'dir') { 'DIR ' } else { 'FILE' }
        "{0,3}  {1}  {2,10}  {3}" -f $i, $tag, $r.size, $r.win | Write-Host
    }
    $bad = @(Get-StaleReport $rows)
    if ($bad.Count) {
        Write-Host "[过期] $($bad.Count) 项已不存在：" -ForegroundColor Yellow
        $bad | ForEach-Object { Write-Host "      $_" }
    }
}

function Do-Here {
    $ctx = Get-ExplorerContext
    if (-not $ctx) { Write-Host "[xf] 没有可用的资源管理器窗口" -ForegroundColor Yellow; return $null }
    Write-Host $ctx.Path
    return $ctx.Path
}

function Do-Sel {
    $sync = Sync-Selection
    $n = @($sync.Rows).Count
    Write-Host "[xf] $($sync.Ctx.Path)  →  选中 $n 项（$($sync.Ctx.Source)）" -ForegroundColor Cyan
    Show-Selection
    return $sync
}

function Do-From {
    param([string[]]$Paths)
    $paths = @($Paths | Where-Object { $_ })
    if ($paths.Count -eq 0) { Write-Host "[xf] 没有收到路径" -ForegroundColor Yellow; return }
    $sh = Get-Shell
    $items = foreach ($p in $paths) {
        $pp = ([string]$p).Trim().Trim('"')
        if (-not $pp) { continue }
        if (Test-Path -LiteralPath $pp -PathType Container) {
            try { $sh.NameSpace($pp).Self } catch {}
        }
        else {
            try { $sh.NameSpace((Split-Path $pp -Parent)).ParseName((Split-Path $pp -Leaf)) } catch {}
        }
    }
    $rows = Save-Selection @($items | Where-Object { $_ })
    Write-Host "[xf] 收到 $(@($rows).Count) 项（已写入清单）" -ForegroundColor Cyan
    Show-Selection
}

function Do-Paste {
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $inner = 'Add-Type -AssemblyName System.Windows.Forms;' +
             '[Console]::OutputEncoding=[Text.Encoding]::UTF8;' +
             '(Get-Clipboard -Format FileDropList) -join [Environment]::NewLine'
    $raw = & $psExe -NoProfile -Sta -Command $inner 2>$null
    $paths = @($raw | Where-Object { $_ -and (Test-FsPath $_) })
    if ($paths.Count -eq 0) {
        Write-Host "[xf] 剪贴板里没有文件（先在资源管理器里 Ctrl+C 复制）" -ForegroundColor Yellow
        return
    }
    $sh = Get-Shell
    $items = foreach ($p in $paths) {
        try { $sh.NameSpace((Split-Path $p -Parent)).ParseName((Split-Path $p -Leaf)) } catch {}
    }
    $rows = Save-Selection @($items | Where-Object { $_ })
    Write-Host "[xf] 从剪贴板取到 $(@($rows).Count) 项" -ForegroundColor Cyan
    Show-Selection
}

function Do-Reveal {
    param([string]$Target)
    if (-not $Target) {
        $rows = Read-Selection
        if ($rows.Count -eq 0) { Write-Host "[xf] 清单为空" -ForegroundColor Yellow; return }
        $Target = $rows[0].win
    }
    elseif ($Target -match '^@(\d+)$') {
        $rows = Read-Selection
        $n = [int]$Matches[1]
        if ($n -lt 1 -or $n -gt $rows.Count) { Write-Host "[xf] @$n 超范围" -ForegroundColor Yellow; return }
        $Target = $rows[$n - 1].win
    }
    if ($Target -match '^/') { $Target = ConvertTo-WinPath $Target }
    if (-not (Test-Path -LiteralPath $Target)) { Write-Host "[xf] 不存在：$Target" -ForegroundColor Yellow; return }
    if (Test-Path -LiteralPath $Target -PathType Container) {
        Start-Process explorer.exe -ArgumentList "`"$Target`""
    }
    else {
        Start-Process explorer.exe -ArgumentList "/select,`"$Target`""
    }
    Write-Host "[xf] 已定位：$Target" -ForegroundColor Cyan
}

function Do-Find {
    param([string]$Pattern)
    if (-not $Pattern) { Write-Host "[xf] 需要通配符，如 xf find '*.md'" -ForegroundColor Yellow; return }
    $ctx = Get-ExplorerContext
    $base = if ($ctx) { $ctx.Path } else { (Get-Location).Path }
    $hadSession = $true
    if ($script:Session) { Stop-BashSession }
    Start-BashSession -Cwd $base | Out-Null
    $u = (ConvertTo-UnixPath $base)
    $cmd = "cd " + (Quote-Bash $u) + " && find . -iname " + (Quote-Bash $Pattern) +
           " -not -path './.git/*' -printf '%p\n' | sed 's|^\./||' | head -500"
    $r = Invoke-Bash -Command $cmd -TimeoutSec 180
    Stop-BashSession
    $names = @($r.Output -split "`r?`n" | Where-Object { $_ })
    if ($names.Count -eq 0) { Write-Host "[xf] 没找到 $Pattern" -ForegroundColor Yellow; return }
    $sh = Get-Shell
    $items = foreach ($n in $names) {
        $full = Join-Path $base ($n -replace '/', '\')
        try { $sh.NameSpace((Split-Path $full -Parent)).ParseName((Split-Path $full -Leaf)) } catch {}
    }
    $rows = Save-Selection @($items | Where-Object { $_ })
    Write-Host "[xf] 命中 $(@($rows).Count) 项（已写入清单）" -ForegroundColor Cyan
    Show-Selection
}

# ---------------------------------------------------------------- 一行命令
function Invoke-Line {
    param([string]$Line)
    $Line = $Line.Trim()
    if (-not $Line) { return }
    if ($Line -in @(':q', ':quit', ':exit', 'quit')) { return 'QUIT' }

    if ($Line -match '^:') {
        $parts = $Line -split '\s+', 2
        $c = $parts[0]
        $a = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        switch -Regex ($c) {
            '^:sel$' { Do-Sel | Out-Null; return }
            '^:list$' { Show-Selection; return }
            '^:paste$' { Do-Paste; return }
            '^:here$' { Do-Here | Out-Null; return }
            '^:reveal$' { Do-Reveal -Target $a; return }
            '^:find$' { Do-Find -Pattern $a; return }
            '^:cd$' {
                if (-not $a) { return }
                $u = if ($a -match '^[A-Za-z]:' -or $a -match '\\') { (ConvertTo-UnixPath $a) } else { $a }
                $r = Invoke-Bash -Command ("cd " + (Quote-Bash $u) + " && pwd")
                Write-Host $r.Output.TrimEnd()
                return
            }
            '^:help$' { Show-Help; return }
            default { Write-Host "[xf] 未知内部命令 $c，:help 看帮助" -ForegroundColor Yellow; return }
        }
    }

    $rows = Read-Selection
    try { $expanded = Expand-Placeholders -Text $Line -Rows $rows }
    catch { Write-Host "[xf] $(Get-ErrText $_)" -ForegroundColor Yellow; return }
    $r = Invoke-Bash -Command $expanded -TimeoutSec 300
    if ($r.Output) { Write-Host $r.Output.TrimEnd() }
    if ($r.ExitCode -ne 0) { Write-Host "[exit $($r.ExitCode)]" -ForegroundColor DarkYellow }
}

# ---------------------------------------------------------------- 分发
switch ($Cmd.ToLower()) {
    '' {
        $ctx = Get-ExplorerContext
        $startDir = if ($ctx) { $ctx.Path } else { (Get-Location).Path }
        Start-BashSession -Cwd $startDir | Out-Null
        Write-Host "xf · bash 已挂在 $startDir" -ForegroundColor Cyan
        Write-Host "     :sel 读选区  :list 看清单  :paste 从剪贴板  :reveal @1 高亮  :help  :q" -ForegroundColor DarkGray
        Write-Host ""

        $redirected = [Console]::IsInputRedirected
        $lines = @()
        if ($redirected) { $lines = @([Console]::In.ReadToEnd() -split "`r?`n") }

        $idx = 0
        while ($true) {
            if ($redirected) {
                if ($idx -ge $lines.Count) { break }
                $line = $lines[$idx]; $idx++
            }
            else {
                $cwdU = Get-BashCwd
                Write-Host ("[" + $cwdU + "] ") -NoNewline -ForegroundColor DarkCyan
                $line = [Console]::In.ReadLine()
            }
            if ($null -eq $line) { break }
            $line = $line -replace "^\uFEFF", ''
            if ($line.Trim()) { Add-Content -LiteralPath $script:History -Value $line -Encoding UTF8 }
            try { $res = Invoke-Line -Line $line }
            catch { Write-Host "[xf] $(Get-ErrText $_)" -ForegroundColor Yellow; $res = $null }
            if ($res -eq 'QUIT') { break }
        }
        Stop-BashSession
        Write-Host "bye" -ForegroundColor DarkGray
    }
    'here' { Do-Here | Out-Null }
    'sel' { Do-Sel | Out-Null }
    'list' { Show-Selection }
    'paste' { Do-Paste }
    'from' { Do-From -Paths $Rest }
    'reveal' { Do-Reveal -Target ($Rest -join ' ') }
    'find' { Do-Find -Pattern ($Rest -join ' ') }
    'help' { Show-Help }
    'sh' {
        $cmd = $Rest -join ' '
        if (-not $cmd) { Write-Host '用法: xf sh "<bash 命令>"' -ForegroundColor Yellow; exit 2 }
        $ctx = Get-ExplorerContext
        $cwd = if ($ctx) { $ctx.Path } else { (Get-Location).Path }
        Start-BashSession -Cwd $cwd | Out-Null
        $rows = Read-Selection
        try { $expanded = Expand-Placeholders -Text $cmd -Rows $rows }
        catch { Write-Host "[xf] $(Get-ErrText $_)" -ForegroundColor Yellow; Stop-BashSession; exit 3 }
        Write-Host "[xf] cwd=$cwd" -ForegroundColor DarkGray
        $r = Invoke-Bash -Command $expanded -TimeoutSec 300
        if ($r.Output) { Write-Host $r.Output.TrimEnd() }
        Stop-BashSession
        exit $r.ExitCode
    }
    default { Write-Host "未知命令：$Cmd（xf help）" -ForegroundColor Yellow; exit 2 }
}
