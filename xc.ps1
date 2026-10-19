<#
.SYNOPSIS
  xc —— 用 ls / cd / mkdir 这类命令，直接指挥资源管理器窗口。

.DESCRIPTION
  资源管理器负责"看得见"，控制台负责"敲得快"。
  xc 把命令翻译成对**指定窗口**的导航与选中动作：

      cd 路径     → 窗口跳过去（同一窗口，不是新开）
      ls          → 列出当前视图内容
      mkdir 名字  → 建目录，窗口刷新
      sel *.md    → 在窗口里把这些文件真正选中（看得见的高亮）
      up / back   → 上一级 / 退回

  安全约定：启动时锁定一个窗口（打印 HWND 和路径），之后只动这一个窗口。
  窗口被关掉或锁丢了就报错停下，绝不猜、绝不换窗口。

.EXAMPLE
  xc                       # 锁定当前前台资源管理器窗口
  xc -Hwnd 123456          # 锁定指定窗口
  xc -List                 # 列出所有资源管理器窗口
#>
[CmdletBinding()]
param(
    [int64]$Hwnd = 0,
    [switch]$List,
    [switch]$Help,
    [Parameter(Position = 0)][string]$Cmd = '',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

$ErrorActionPreference = 'Stop'
try { [Console]::InputEncoding  = New-Object System.Text.UTF8Encoding($false) } catch {}
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}

$script:Shell    = $null
$script:Win      = $null      # 锁定的窗口对象
$script:Hwnd     = $Hwnd
$script:Path     = ''         # 跟踪的路径
$script:Back     = New-Object System.Collections.Stack
$script:Fwd      = New-Object System.Collections.Stack
$script:Activate = $false
$script:Root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:StateDir = Join-Path $script:Root 'state'
if (-not (Test-Path $script:StateDir)) { New-Item -ItemType Directory -Force $script:StateDir | Out-Null }

function Get-HistFile {
    param([string]$Kind)
    Join-Path $script:StateDir ("xc-$Kind-$($script:Hwnd).txt")
}

function Hist-Push {
    param([string]$Path)
    $f = Get-HistFile 'back'
    $lines = @()
    if (Test-Path $f) { $lines = @(Get-Content $f -Encoding UTF8 | Where-Object { $_ }) }
    if ($lines.Count -gt 0 -and $lines[-1] -eq $Path) { return }
    $lines += $Path
    if ($lines.Count -gt 200) { $lines = $lines[($lines.Count - 200)..($lines.Count - 1)] }
    [IO.File]::WriteAllLines($f, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Set-Content (Get-HistFile 'fwd') @() -Encoding UTF8
}

function Hist-Pop {
    param([string]$Kind)
    $f = Get-HistFile $Kind
    if (-not (Test-Path $f)) { return $null }
    $lines = @(Get-Content $f -Encoding UTF8 | Where-Object { $_ })
    if ($lines.Count -eq 0) { return $null }
    $top = $lines[-1]
    $rest = @(); if ($lines.Count -gt 1) { $rest = @($lines[0..($lines.Count - 2)]) }
    [IO.File]::WriteAllLines($f, [string[]]@($rest), (New-Object System.Text.UTF8Encoding($false)))
    return $top
}

function Hist-Clear {
    foreach ($k in @('back', 'fwd')) {
        $f = Get-HistFile $k
        if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }
}
function Get-ErrText {
    param($Err)
    $x = $Err.Exception
    while ($x.InnerException) { $x = $x.InnerException }
    return $x.Message
}

# ------------------------------------------------------------------ 窗口
function Get-Shell {
    if (-not $script:Shell) { $script:Shell = New-Object -ComObject Shell.Application }
    $script:Shell
}

function Get-ForegroundHwnd {
    if (-not ('XcWin32' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public class XcWin32 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
    }
    return [int64][XcWin32]::GetForegroundWindow()
}

function Test-FsPath {
    param([string]$p)
    if (-not $p) { return $false }
    if ($p -match '^::') { return $false }
    if ($p -match '^[A-Za-z]:\\') { return $true }
    if ($p -match '^\\\\[^\\]+\\') { return $true }
    return $false
}

function Get-ExplorerWindows {
    @((Get-Shell).Windows() | Where-Object { $_.Document -and $_.Document.Folder } |
        ForEach-Object {
            $p = ''
            try { $p = [string]$_.Document.Folder.Self.Path } catch {}
            [pscustomobject]@{ Hwnd = [int64]$_.HWND; Path = $p; IsFs = (Test-FsPath $p); Com = $_ }
        })
}

function Lock-Window {
    param([int64]$WantHwnd)

    $wins = Get-ExplorerWindows
    if (@($wins).Count -eq 0) {
        throw "没有打开的资源管理器窗口。先 Win+E 打开一个文件夹，再运行 xc。"
    }

    if ($WantHwnd -eq 0) { $WantHwnd = Get-ForegroundHwnd }

    $cand = @($wins | Where-Object { $_.Hwnd -eq $WantHwnd -and $_.IsFs })
    if ($cand.Count -eq 0) {
        $fgWin = Get-ForegroundHwnd
        $any = @($wins | Where-Object { $_.Hwnd -eq $fgWin -and $_.IsFs })
        if ($any.Count -gt 0) { $cand = $any }
    }
    if ($cand.Count -eq 0) {
        $fs = @($wins | Where-Object { $_.IsFs })
        if ($fs.Count -eq 0) { throw "打开的窗口里没有文件系统路径（都是此电脑/搜索这类虚拟文件夹）。先打开一个真实文件夹再运行 xc。" }
        $cand = @($fs[0])
        Write-Host "[提示] 前台不是资源管理器，已锁定第一个可用窗口：" -ForegroundColor DarkYellow
        Write-Host "       HWND=$($cand[0].Hwnd)  $($cand[0].Path)" -ForegroundColor DarkYellow
        Write-Host "       锁错了敲 win 看列表，再 use <编号> 切换" -ForegroundColor DarkYellow
        Write-Host ""
    }

    # 同一 HWND 可能有多个标签页：优先取第一个文件系统路径
    $pick = $cand[0]
    $script:Hwnd = $pick.Hwnd
    $script:Win  = $pick.Com
    $script:Path = $pick.Path
    # 保留历史（跨进程持久化），启动不清
    return $pick
}

function Sync-Window {
    <# 每次命令前重新解析，窗口没了就报错，绝不换窗口 #>
    $wins = @(Get-ExplorerWindows | Where-Object { $_.Hwnd -eq $script:Hwnd })
    if ($wins.Count -eq 0) { throw "锁定的窗口（HWND=$script:Hwnd）已经关闭。重新运行 xc 锁定新窗口。" }
    $hit = @($wins | Where-Object { $_.Path -eq $script:Path }) | Select-Object -First 1
    $pick = if ($hit) { $hit } else { $wins[0] }
    $script:Win  = $pick.Com
    $script:Path = $pick.Path
    return $pick
}

function Show-Windows {
    $wins = @(Get-ExplorerWindows)
    $i = 0
    foreach ($w in $wins) {
        $i++
        $mark = if ($w.Hwnd -eq $script:Hwnd -and $w.Path -eq $script:Path) { ' <<< 已锁定' } else { '' }
        $tag  = if ($w.IsFs) { '  ' } else { '虚' }
        "{0,3}  {1}  HWND={2,-9} {3}{4}" -f $i, $tag, $w.Hwnd, $w.Path, $mark | Write-Host
    }
}

function Update-Window {
    <# 刷新窗口显示 #>
    try { $script:Win.Refresh() } catch {}
    Start-Sleep -Milliseconds 150
}

function Bring-Front {
    if (-not $script:Activate) { return }
    try { [void][XcWin32]::SetForegroundWindow([IntPtr]$script:Hwnd) } catch {}
}

# ------------------------------------------------------------------ 路径
function Resolve-Target {
    param([string]$Arg)
    $cur = $script:Path
    if (-not $Arg) { return $null }
    $a = $Arg.Trim().Trim('"')

    if ($a -eq '~') { return $env:USERPROFILE }
    if ($a -eq '-') {
        if ($script:Back.Count -eq 0) { throw "没有上一级历史" }
        return [string]$script:Back.Peek()
    }
    if ($a -eq '..') { return (Split-Path -Parent $cur) }
    if ($a -match '^\.\.(\\|/)+') {
        $rest = $a -replace '^\.\.(\\|/)+', ''
        return (Join-Path (Split-Path -Parent $cur) $rest)
    }
    if ($a -match '^[A-Za-z]:$') { return ("$a\") }
    if ([IO.Path]::IsPathRooted($a)) { return $a }
    return (Join-Path $cur $a)
}

function Format-Size {
    param([long]$n)
    if ($n -lt 1024) { return "$n B" }
    if ($n -lt 1048576) { return ('{0:0.0} KB' -f ($n / 1024)) }
    if ($n -lt 1073741824) { return ('{0:0.0} MB' -f ($n / 1048576)) }
    return ('{0:0.0} GB' -f ($n / 1073741824))
}

# ------------------------------------------------------------------ 命令
function Cmd-Cd {
    param([string]$Arg)
    if (-not $Arg) {
        $home_ = $env:USERPROFILE
        Hist-Push $script:Path
        $script:Win.Navigate2($home_)
        Start-Sleep -Milliseconds 350
        Sync-Window | Out-Null
        Write-Host "$($script:Path)" -ForegroundColor Cyan
        Bring-Front
        return
    }
    $full = Resolve-Target $Arg
    if (-not (Test-Path -LiteralPath $full)) { throw "不存在：$full" }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "不是目录：$full" }
    $full = (Resolve-Path -LiteralPath $full).Path

    Hist-Push $script:Path

    $script:Win.Navigate2($full)
    Start-Sleep -Milliseconds 350
    Sync-Window | Out-Null
    Write-Host "$($script:Path)" -ForegroundColor Cyan
    Bring-Front
}

function Cmd-Up {
    param([int]$N = 1)
    $p = $script:Path
    for ($i = 0; $i -lt [Math]::Max(1, $N); $i++) {
        $parent = Split-Path -Parent $p
        if (-not $parent) { break }
        $p = $parent
    }
    if ($p -eq $script:Path) { Write-Host "已经在根目录" -ForegroundColor DarkGray; return }
    Hist-Push $script:Path
    $script:Win.Navigate2($p)
    Start-Sleep -Milliseconds 350
    Sync-Window | Out-Null
    Write-Host "$($script:Path)" -ForegroundColor Cyan
    Bring-Front
}

function Cmd-Back {
    $to = Hist-Pop 'back'
    if (-not $to) { Write-Host "没有历史（首次使用时正常）" -ForegroundColor DarkGray; return }
    # 退回不写 back 栈，避免自循环
    $script:Win.Navigate2($to)
    Start-Sleep -Milliseconds 350
    Sync-Window | Out-Null
    Write-Host "$($script:Path)" -ForegroundColor Cyan
    Bring-Front
}

function Cmd-Ls {
    param([string]$Pattern = '*', [switch]$Long)
    $folder = $script:Win.Document.Folder
    $items = @($folder.Items() | Where-Object {
            if ($Pattern -eq '*') { $true } else { $_.Name -like $Pattern }
        })
    if ($items.Count -eq 0) { Write-Host "(空)" -ForegroundColor DarkGray; return }

    $rows = foreach ($i in $items) {
        $isDir = $false; try { $isDir = [bool]$i.IsFolder } catch {}
        $size = 0L;    try { if (-not $isDir) { $size = [long]$i.Size } } catch {}
        $mt = '';      try { $mt = ([datetime]$i.ModifyDate).ToString('MM-dd HH:mm') } catch {}
        [pscustomobject]@{ Dir = $isDir; Name = [string]$i.Name; Size = $size; MT = $mt; Item = $i }
    }
    $rows = @($rows | Sort-Object @{ Expression = { -not $_.Dir } }, Name)

    foreach ($r in $rows) {
        if ($Long) {
            $t = if ($r.Dir) { '<DIR>' } else { Format-Size $r.Size }
            Write-Host ("{0,-10} {1,12}  {2}" -f $r.MT, $t, $r.Name)
        }
        else {
            if ($r.Dir) { Write-Host ("  [D] " + $r.Name) -ForegroundColor Yellow }
            else { Write-Host ("      " + $r.Name) }
        }
    }
    Write-Host ("  共 $($rows.Count) 项（目录 " + @($rows | Where-Object { $_.Dir }).Count + "）") -ForegroundColor DarkGray
}

function Cmd-Mkdir {
    param([string]$Name)
    if (-not $Name) { throw "用法: mkdir <目录名>" }
    $full = if ([IO.Path]::IsPathRooted($Name)) { $Name } else { Join-Path $script:Path $Name }
    if (Test-Path -LiteralPath $full) { throw "已存在：$full" }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
    Update-Window
    # 建完顺手选中它，看得见
    try {
        $leaf = Split-Path $full -Leaf
        $item = $script:Win.Document.Folder.ParseName($leaf)
        if ($item) { $script:Win.Document.SelectItem($item, 1) }
    } catch {}
    Write-Host "已创建 $full" -ForegroundColor Green
    Bring-Front
}

function Cmd-Sel {
    param([string]$Pattern, [switch]$Add)
    if (-not $Pattern) { throw "用法: sel <通配符>   例如 sel *.md" }
    $folder = $script:Win.Document.Folder
    $items = @($folder.Items())
    $hit = @($items | Where-Object { $_.Name -like $Pattern })
    if ($hit.Count -eq 0) { Write-Host "没有匹配 $Pattern 的项" -ForegroundColor Yellow; return }
    if (-not $Add) {
        foreach ($s in @($script:Win.Document.SelectedItems())) { try { $script:Win.Document.SelectItem($s, 0) } catch {} }
        Start-Sleep -Milliseconds 60
    }
    foreach ($i in $hit) { try { $script:Win.Document.SelectItem($i, 1) } catch {} }
    Start-Sleep -Milliseconds 150
    $n = @($script:Win.Document.SelectedItems()).Count
    Write-Host "已选中 $($hit.Count) 项（窗口内当前共 $n 项高亮）" -ForegroundColor Cyan
    Bring-Front
}

function Cmd-Desel {
    $sel = @($script:Win.Document.SelectedItems())
    foreach ($i in $sel) { try { $script:Win.Document.SelectItem($i, 0) } catch {} }
    Start-Sleep -Milliseconds 150
    Write-Host "已清空选中" -ForegroundColor DarkGray
}

function Cmd-Open {
    param([string]$Name)
    if (-not $Name) { throw "用法: open <名称>" }
    $folder = $script:Win.Document.Folder
    $item = $folder.ParseName($Name)
    if (-not $item) {
        $hit = @($folder.Items() | Where-Object { $_.Name -like $Name })
        if ($hit.Count -eq 0) { throw "当前目录里没有：$Name" }
        if ($hit.Count -gt 1) { throw "匹配到多项，请写完整名称：$(($hit | ForEach-Object { $_.Name }) -join ', ')" }
        $item = $hit[0]
    }
    $isDir = $false; try { $isDir = [bool]$item.IsFolder } catch {}
    if ($isDir) { Cmd-Cd $item.Path; return }
    Start-Process -FilePath $item.Path
    Write-Host "已打开 $($item.Path)" -ForegroundColor Green
}

function Cmd-Bang {
    param([string]$Rest)
    if (-not $Rest) { throw "用法: !<bash 命令>" }
    $bash = if ($env:XF_BASH) { $env:XF_BASH } else { "C:\Program Files\Git\bin\bash.exe" }
    if (-not (Test-Path $bash)) { throw "找不到 bash.exe" }
    if ($script:Path -match "^([A-Za-z]):(.*)$") {
        $u = "/" + $Matches[1].ToLower() + ($Matches[2] -replace "\\", "/")
    } else { $u = $script:Path -replace "\\", "/" }
    $enc = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Rest))
    $out = & $bash -lc "cd '$u' && echo $enc | base64 -d | bash" 2>&1
    if ($out) { $out | ForEach-Object { Write-Host $_ } }
    Update-Window
}
function Show-Help {
    @'
xc —— 用命令指挥资源管理器

导航
  cd <路径|..|~|->   窗口跳到该目录（同一窗口）
  up [n]             上一级（可指定层数）
  back               退回上一个目录
  open <名称>        进目录 / 用默认程序打开文件
  pwd                当前路径

查看 / 操作
  ls [通配符]        列出当前视图（目录在前）
  ll [通配符]        带时间和大小的详细列表
  mkdir <名称>       建目录并选中它
  sel <通配符>       在窗口里选中匹配项（真正的可视化高亮）
  desel              清空选中
  !<bash 命令>       在当前目录跑一条 bash，跑完刷新窗口

窗口
  win                列出所有资源管理器窗口
  use <编号>         改锁到另一个窗口

其它
  :a on|off          命令执行后是否把资源管理器带到前台（默认 off）
  :p                 手动把窗口带到前台看一眼
  help / q

安全
  启动时锁定一个窗口，之后只动它。窗口被关掉会直接报错停下。
'@
}

# ------------------------------------------------------------------ 分发
function Invoke-Line {
    param([string]$Line)
    $Line = $Line -replace "^\uFEFF", ''
    $Line = $Line.Trim()
    if (-not $Line) { return }
    if ($Line -in @('q', 'quit', 'exit', ':q')) { return 'QUIT' }

    $tok = $Line -split '\s+', 2
    $verb = $tok[0].ToLower()
    $arg = if ($tok.Count -gt 1) { $tok[1].Trim() } else { '' }

    Sync-Window | Out-Null
    if ($Line.StartsWith('!')) { Cmd-Bang ($Line.Substring(1).Trim()); return }

    switch -Regex ($verb) {
        '^(cd)$' { Cmd-Cd $arg; return }
        '^(up)$' { Cmd-Up $(if ($arg) { [int]$arg } else { 1 }); return }
        '^(back)$' { Cmd-Back; return }
        '^(pwd)$' { Write-Host $script:Path -ForegroundColor Cyan; return }
        '^(ls)$' { Cmd-Ls -Pattern $(if ($arg) { $arg } else { '*' }); return }
        '^(ll)$' { Cmd-Ls -Pattern $(if ($arg) { $arg } else { '*' }) -Long; return }
        '^(mkdir|md)$' { Cmd-Mkdir $arg; return }
        '^(sel|sel[+])$' { Cmd-Sel -Pattern $arg -Add:($verb -eq 'sel+'); return }
        '^(desel)$' { Cmd-Desel; return }
        '^(open|enter)$' { Cmd-Open $arg; return }
        '^(win)$' { Show-Windows; return }
        '^use$' {
            $ws = @(Get-ExplorerWindows)
            $n = [int]$arg
            if ($n -lt 1 -or $n -gt $ws.Count) { throw "编号超出范围（1..$($ws.Count)）" }
            $script:Hwnd = $ws[$n - 1].Hwnd
            $script:Path = $ws[$n - 1].Path
            Hist-Clear
            Sync-Window | Out-Null
            Write-Host "已改锁到 HWND=$($script:Hwnd)  $($script:Path)" -ForegroundColor Cyan
            return
        }
        '^:a$' {
            $script:Activate = ($arg -eq 'on')
            Write-Host ("自动带到前台：" + $(if ($script:Activate) { '开' } else { '关' })) -ForegroundColor DarkGray
            return
        }
        '^:p$' {
            try { [void][XcWin32]::SetForegroundWindow([IntPtr]$script:Hwnd) } catch {}
            return
        }
        '^(help|\?)$' { Show-Help; return }
        '^!$' { Cmd-Bang $Line.Substring(1).Trim(); return }
        default { Write-Host "不认识的命令：$verb（help 看列表）" -ForegroundColor Yellow; return }
    }
}

# ------------------------------------------------------------------ 入口
if ($Help) { Show-Help; exit 0 }

if ($List) {
    $h = if ($Hwnd) { $Hwnd } else { 0 }
    $wins = @(Get-ExplorerWindows)
    $i = 0
    foreach ($w in $wins) {
        $i++
        "{0,3}  HWND={1,-9} {2}" -f $i, $w.Hwnd, $w.Path | Write-Host
    }
    exit 0
}

if ($Cmd) {
    # 单条命令模式：xc ls / xc cd .. / xc sel *.md
    Lock-Window -WantHwnd $Hwnd | Out-Null
    $line = (@($Cmd) + $Rest) -join ' '
    try { Invoke-Line -Line $line | Out-Host }
    catch { Write-Host (Get-ErrText $_) -ForegroundColor Yellow; exit 1 }
    exit 0
}

Lock-Window -WantHwnd $Hwnd | Out-Null
Write-Host "xc · 已锁定 HWND=$($script:Hwnd)" -ForegroundColor Cyan
Write-Host "     $($script:Path)" -ForegroundColor Cyan
Write-Host "     help 看命令，q 退出" -ForegroundColor DarkGray
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
        Write-Host ("[" + $script:Path + "] ") -NoNewline -ForegroundColor DarkCyan
        $line = [Console]::In.ReadLine()
    }
    if ($null -eq $line) { break }
    try { $res = Invoke-Line -Line $line }
    catch { Write-Host (Get-ErrText $_) -ForegroundColor Yellow; $res = $null }
    if ($res -eq 'QUIT') { break }
}
Write-Host "bye" -ForegroundColor DarkGray
