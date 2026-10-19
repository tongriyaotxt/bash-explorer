# Explorer × Command Line

把 Windows 的 **Shell 命名空间**用命令行操作：不是"打开文件夹再点鼠标"，也不是"纯终端看不见东西"。

四个工具，同一套底座（`Shell.Application` 的 ShellItem），四种界面形态：

| 工具 | 界面 | 方向 | 一句话 |
|---|---|---|---|
| **[xf](README.md#xf)** | 终端 | 选区 → bash | 资源管理器里选中，`@*` 一把交给 bash 处理 |
| **[xc](XC.md)** | 资源管理器 | 命令 → 窗口 | 敲 `cd` / `ls` / `sel`，真资源管理器窗口跟着动 |
| **[xs](XS.md)** | 终端 TUI | 命令 → ShellItem | 全屏字符界面，两行命令行 + 实时列表 |
| **[xg](XG.md)** | 图形界面 | 命令 + 鼠标 | 图标列表 + 右侧预览窗格，像 Explorer 但能敲命令 |

> 共同点：操作对象是 **ShellItem**，所以「此电脑」「回收站」「控制面板」和盘符、普通文件夹是同一类东西。

---

## 环境要求

- Windows 10/11
- .NET Framework 4.x（系统自带）
- Git for Windows（提供 `bash.exe`，`xf` 用）
- Windows Terminal（可选，`xs` 在里面跑最好看）

**不写注册表、不起服务、不改 PATH**。所有入口都是用户目录下的 `.cmd`，卸载就是删文件。

---

## 快速开始

```powershell
# 一次性安装入口（xf/xc/xs/xg 都能直接敲）
powershell -ExecutionPolicy Bypass -File setup.ps1

# 重新编译（改过 .cs 之后）
powershell -ExecutionPolicy Bypass -File build.ps1 -Test
```

```powershell
xg                 # 图形界面，当前目录
xs                 # 终端 TUI
xc                 # 驱动当前资源管理器窗口
xf sel             # 把选中项写进清单
xf sh "wc -l @*"   # 在 bash 里用掉这批选中项
```

---

## xf

资源管理器里选中 → 终端里用 `@*` 处理。核心是一条"选区清单"：选一次，命令跑 N 次。

```powershell
xf                 # 交互 REPL，bash 常驻（cd / 变量都保留）
xf sel             # 取当前选中项 → state\_sel.tsv
xf sh "ls -la @*"  # 在资源管理器当前目录里执行
xf reveal @2       # 在资源管理器里高亮第 2 项
xf find "*.md"     # 搜索结果写进清单
xf paste           # 从剪贴板取文件
```

占位符：`@*` 全部 · `@1` 第 N 项 · `@+md` 按扩展名 · `@d` 所在目录 · `@cwd` 当前目录。
路径自动转 `/d/项目/...` 并加单引号，中文/空格/`&` 都不用转义。

---

## xc

驱动**真实的**资源管理器窗口（锁定一个 HWND，只动它）。

```
xc · 已锁定 HWND=328464
[D:\项目] cd 闲扯
[D:\项目\闲扯] mkdir 新目录
[D:\项目\闲扯] sel *.md
```

`cd` `ls` `ll` `mkdir` `sel` `desel` `open` `up` `back` `pwd` `win` `use` `!<bash>`

详见 [XC.md](XC.md)。

---

## xs

全屏 TUI，两行命令行 + 实时渲染列表。跑在 Windows Terminal 里，字体/配色/分屏白拿。

```
D:\项目\闲扯                    5 items [detail] · XS.md
❯ ll
2026-10-19 22:58:33   116 B   Windows 命令脚本   xs.cmd
2026-10-19 23:06:48  33.2 KB  C# 源文件          xs.cs
```

`Tab` 补全 · `↑↓` 移动/历史 · `Enter` 打开/执行 · `Ctrl+Q` 退出。
详见 [XS.md](XS.md)。

---

## xg

WinForms 图形界面：顶部两行命令行（启动即聚焦），中间是资源管理器风格的图标/明细列表，右侧是预览窗格。

- 图标用**系统图标列表**（`SHGetFileInfo` + `LVM_SETIMAGELIST`），和 Explorer 画的是同一套
- 预览用 **`IShellItemImageFactory`**：图片/视频/PDF/Office 出真缩略图；文本直接显示正文
- 键位照资源管理器：`Backspace` 后退、`Alt+←/→` 前进后退、`Alt+↑` 上一级、`Delete` 移到回收站、`F2` 改名

详见 [XG.md](XG.md)。

---

## 构建

```powershell
csc /target:winexe /out:xs\xs.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll xs\xs.cs
csc /target:winexe /out:xs\xg.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll xs\xg.cs
```

或直接 `powershell -File build.ps1 -Test`。

> 源码必须存成 **UTF-8 with BOM**：`csc` 没有 BOM 时会按系统代码页解析，中文字面量会变乱码。
> `.cmd` 文件必须**纯 ASCII**：cmd.exe 按 OEM 代码页读批处理，中文注释会被当命令执行。

---

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File setup.ps1 -Remove
```

或直接删 `%LOCALAPPDATA%\Microsoft\WindowsApps\{xf,xc,xs,xsw,xg}.cmd` 与 `%LOCALAPPDATA%\xf`。
程序本体删掉 `explorer-bash` 目录即可。

---

## 目录

```
explorer-bash/
├── xf.ps1 / xf.cmd         # 选区 → bash
├── xc.ps1 / xc.cmd         # 命令 → 资源管理器窗口
├── setup.ps1               # 安装/卸载入口（ASCII 联结 + WindowsApps 垫片）
├── build.ps1               # 一键重建 xs.exe / xg.exe
├── xs/
│   ├── xs.cs / xs.exe      # 终端 TUI
│   ├── xg.cs / xg.exe      # 图形界面
│   └── *.md
├── XC.md / XS.md / XG.md   # 各工具说明
└── DESIGN.md               # 交互设计与成本分析
```

---

## 几个踩过的坑（写下来免得重复）

1. **这台机器的 `bash` 工具其实落到 pwsh**：`ls -la` / `which` / `head` 全报错，真 bash 在 `C:\Program Files\Git\bin\bash.exe`。
2. **.NET Framework 没有 `ProcessStartInfo.StandardInputEncoding`**，而 `Console.InputEncoding` 自带 BOM，
   子进程 stdin 开头会多出 `EF BB BF`，bash 报 `$'\357\273\277exec': command not found`。
3. **WT 窗口不能 `SetParent`**：D3D swap chain 绑顶层 HWND，重挂后窗口在、内容不画。
   conhost 跨进程重挂则可能死锁。所以"嵌终端"这条路在 Windows 上走不通，只能"活在终端里"。
4. **DPI**：exe 没有 manifest 会被系统按 150% 拉伸，`GetWindowRect` 返回虚拟坐标，列宽全错。
   解法是启动时 `SetProcessDpiAwareness(2)` 并自己按 `DpiX/96` 算尺寸。
5. **虚拟命名空间里 `ParseName("OS (C:)")` 认不出盘符显示名**，要回退到按显示名遍历匹配。

---

## License

MIT
