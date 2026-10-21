# 进度存档 / 下次从这里接着 debug

> 存档时间：2026-10-21 13:30 · 仓库：`tongriyaotxt/bash-explorer`（private，main）
> 代码全在 `D:\项目\闲扯\explorer-bash`。本轮只改 `xs/xg.cs`、`xs/XG.md`、`build.ps1`（未提交）。

---

## 一句话现状

四个工具都能跑；`xg` 这轮把「待打磨」里最挡手的几件事补完了：**排序 / 右键菜单 / 工具条 /
面包屑 / 图标视图真缩略图 / 预览内存释放 / Shift+Delete**，并且有了不依赖鼠标的 `--selftest`。

| 工具 | 形态 | 状态 | 入口 |
|---|---|---|---|
| **xf** | 终端 | ✅ 可用 | `xf` / `xf sh "…@*"` |
| **xc** | 驱动真 Explorer | ✅ 可用 | `xc`（锁定一个窗口） |
| **xs** | 终端 TUI | ✅ 可用 | `xs` / `xsw`（WT 里） |
| **xg** | WinForms 图形界面 | ✅ 可用 | `xg` / `xg --run "ic;sort size"` |

---

## 这轮给 xg 补的

- **列头排序**：点列头排序，再点反向；表头 `▲ / ▼`；目录恒在前，同组内反向。
  命令行 `sort name|date|type|size [asc|desc]`。排序不再重新枚举目录（只重排内存里的 items）。
- **右键菜单**（深色）：打开 / 在此处打开终端（`wt -d`，回退 `cmd`）/ 在资源管理器中显示（`/select`）
  / 复制路径 / 重命名 / 移到回收站 / 永久删除 / 属性（`ShellExecuteEx` + `SEE_MASK_INVOKEIDLIST`）。
- **工具条**：`← → ↑ ⟳` + 视图按钮（文字跟随当前视图）。
- **面包屑**：路径按 `\` 拆成可点段；`::` / `shell:` 虚拟位置整体显示。
- **图标视图真缩略图**：默认 96 逻辑像素（150% → 144px），档位见下；`IShellItemImageFactory` →
  失败回退 `SHGetFileInfo(SHGFI_ICON)`；`LVM_SETICONSPACING` 调格距。
- **内存**：预览 `Image` 换选中项时 Dispose；图标位图由 `List<Image>` 统一持有、重建时释放。
- **Shift+Delete** 永久删除（二次确认）；`Alt+Enter` 属性。
- **自检**：`xg --selftest <目录>` 跑一遍视图/排序/面包屑/菜单/预览/roots，写
  `%TEMP%\xg-selftest.txt` 后自己关窗；`build.ps1 -Test` 已接入。
- **崩溃日志**：`Application.ThreadException` + `AppDomain.UnhandledException` → `%TEMP%\xg-crash.log`。
- **Tab 补全重做**（向 cmd / PowerShell 对齐）：命令名 / 路径 / 子目录 / `..\` / 绝对路径；
  唯一匹配补尾随（命令空格、目录 `\`），多候选 Tab 循环、`Shift+Tab` 反向，状态栏显示
  `补全 2/5 [b.txt]`；带空格的名字自动加引号。`CmdBox.IsInputKey` 让 Tab 不再被当成
  对话框导航键（原来 Tab 会先把焦点切到列表）。
- **图标尺寸四档**：48 / 96 / 144 / 256 逻辑像素；`Ctrl+滚轮`（`IconList.OnMouseWheel`）、
  工具条尺寸按钮、`icon 144` / `big` 命令都能切；256px 时缩略图数量降到 120 张防爆内存。
- **相片预览放大**：右栏图片吃满整栏高度（`PreviewBoxHeight`），拖分隔条后按新尺寸重新取图，
  上限 1024px。
- **历史文件夹**：`%LOCALAPPDATA%\xg\history.tsv`（`次数\t时间\t路径`，≤500 条，`XG_HISTORY` 可覆盖）；
  `hist` 弹查询窗（过滤 + ↑↓ + Enter 跳转），`hist 关键字` 直接跳，`-t/-c` 换排序，`-x` 清空；
  默认 frecency 排序。只在真正换目录时记一笔，F5 不刷次数。
- **内置看图**：图片按 Enter 进全屏 `Viewer`，`←/→`（或滚轮）在同目录所有图片间切换、到头绕回，
  `Home/End` 首尾，`Esc` 关；GIF 走内存流保动画，大图按屏幕尺寸取缩略图（≤2048）并在切图时释放。
  右键「用系统程序打开」仍可交给系统"照片"（它单开一张时切不了左右，所以才自己实现）。
- **滚轮/翻页直达列表**：焦点默认在命令行，而 `WM_MOUSEWHEEL` 只发给焦点窗口 →
  用 `IMessageFilter`（`PreFilterMessage`）按鼠标位置把滚轮转发给 ListView；`PgUp/PgDn`
  走 `WM_VSCROLL`。之前"列表看着不全、后半截 jpg 出不来"就是滚不到底。
- **图标间距**：`LVM_SETICONSPACING` 从 `px + 46*scale` 收到 `px + 34*scale`，
  256px 档在 1000px 宽的列表里从 2 列变 3 列。
- **删除**：命令行空着时 `Delete` / `Shift+Delete` / `F2` / `F4` / `F5` 直接作用在列表选中项上
  （`mkdir a` 之后不用先点列表）；失败弹窗写原因，不再静默 `catch`。
- **导航**：`Alt+↑ / Alt+← / Alt+→` 在命令行里也生效；`up` 改走 `GoUp()`——文件系统按路径算
  父目录（`ParentFolder` 在盘根/虚拟位置不可靠），盘根再往上落到「此电脑」，并正确写入历史。
- **命令语法对齐 Linux**：`ls [-l|-t|-S|-r]`、`cd [目录|-|~]`、`mkdir [-p]`、`rm [-r] [-f]`（永久）、
  `rmdir`、`mv`、`cp [-r]`、`cat`、`touch`、`trash`（回收站）、`clear`；路径支持 `/`、`..`、`~`；
  保留 Windows 别名（`dir md del rd ren move copy type cls`）和 GUI 命令（`ic icon big sel sort open info roots up back`）。
- **`sel` 命令**：`sel *.md` / `sel pic.jpg` 按名字（`*` `?`）选中，配合预览与批量操作。
- **图标索引缓存**：同一扩展名不再逐个调 shell（>2000 项目录开销明显下降）。

---

## 下次接着做的事（按优先级）

1. **后台加载**：>2000 项目录仍然是同步枚举 + 同步取图标，窗口会僵一下。
   思路：STA 线程 + 独立 `Shell.Application` 读成 POCO，`BeginInvoke` 回填；或用 `System.IO`
   枚举文件系统目录（快）+ 类型/图标用缓存。注意跨 apartment 传 `ShellItem` 不可靠。
2. **缩略图缓存**：现在图标视图每次渲染（排序、F5）都重取缩略图，400 张会明显卡。
   按 `path|px` 缓存，缓存持有 Image，`ImageList` 只引用。
3. **高 DPI**：只验过 150%；175% / 200% 与实际 100% 都没验（`scale` 全部乘过，理论上没问题）。
4. **列头主题**：明细视图列头还是系统亮色，和深色主题不搭；要自绘 `NM_CUSTOMDRAW`。
5. **xs**：TUI 里的选中项还没有"批量操作"（只有 cd/open）。
6. 可选：图标视图的标签换行/双行省略；拖放（`IDropTarget`）；`Ctrl+C/Ctrl+V` 走 shell 剪贴板。

---

## 复现 / 构建 / 自检

```powershell
# 装入口（xf/xc/xs/xsw/xg 五个 .cmd 到 WindowsApps，纯用户目录，可逆）
powershell -ExecutionPolicy Bypass -File setup.ps1

# 重建 exe（先检查并补 UTF-8 BOM，然后 xs 冒烟 + xg 自检）
powershell -ExecutionPolicy Bypass -File build.ps1 -Test

# 直接跑
xs\xg.exe "D:\某目录"
xs\xg.exe --run "ic;sort size desc" "D:\某目录"
xs\xg.exe --selftest "D:\某目录"     # 结果在 %TEMP%\xg-selftest.txt
xs\xs.exe -c "ls"
```

> **改完 `.ps1` 一定要确认还有 UTF-8 BOM**。编辑工具默认写成无 BOM，
> Windows PowerShell 5.1 会按 GBK 解析，中文全变乱码并报 `Unexpected token`。

---

## 环境事实（这台机器特有，换机器要重新确认）

| 事实 | 影响 |
|---|---|
| 屏幕 **150% DPI**，exe 无 manifest | 必须 `SetProcessDpiAwareness(2)`，否则被系统拉伸、坐标全错 |
| `github.com:443` **直连不通** | 远程用 `ssh://git@ssh.github.com:443/...`，https 会超时 |
| `api.github.com` 通 | `gh` 正常 |
| harness 的 `bash` 工具实际落到 **pwsh** | `ls -la` / `which` / `head` 全报错；真 bash 在 `C:\Program Files\Git\bin\bash.exe` |
| 项目路径含中文 `D:\项目\闲扯` | `.cmd` 里不能写中文路径（会 ASCII 化），靠 `%LOCALAPPDATA%\xf\app` 这个 ASCII 联结绕开 |
| Windows PowerShell 5.1 | `.ps1` 必须 UTF-8 **BOM**，否则按 GBK 解析报错 |
| `git` 不在 PATH | 用 `C:\Program Files\Git\cmd\git.exe` |
| UIA 报的坐标是**物理**像素，`GetWindowRect`（非 DPI 感知进程）是**虚拟**像素 | 写自动化/截图时别混用，差 1.5 倍 |
| 抓图工具 | `D:\项目\闲扯\_shot\shot.exe <hwnd> <out.png>`：DPI 感知 + `PrintWindow`，**不抢焦点**（抢焦点会关掉右键菜单） |

---

## 代码坑位索引（改之前先看这里）

| 位置 | 坑 |
|---|---|
| `xg.cs` 构造 | `AutoScaleMode.None` + 手动 `DpiX/96` 缩放；用 Dpi 会自动二次缩放 |
| `xg.cs` `FitColumns` | 必须在 `Shown` / `SizeChanged` / `BeginInvoke` 里各算一次 |
| `xg.cs` 路径栏/命令行 | 用自绘 `Panel` 托管，裸 `TextBox` 的 `BackColor` 会被主题刷白 |
| `xg.cs` `RenderPreview` | 文本类必须先于缩略图判断，否则文本文件显示成通用图标 |
| `xg.cs` `BuildBigImages` | **`ImageList` 不复制位图**：`Images.Add(img)` 只存引用，句柄要到赋给控件时才建；提前 `Dispose` 会在 `CreateHandle` 抛 `ArgumentException: 参数无效`（表现是视图切了、后面几行没执行） |
| `xg.cs` 排序 | `RenderRows` 只重建行不重排；排序必须 `ApplySort(items)` 后再渲染（`ResortRows`） |
| `xg.cs` `Complete` | Tab 在 TextBox 里是对话框导航键（`IsInputKey` 默认 false），必须 `CmdBox` 覆写 + `PreviewKeyDown` 双保险，否则按键先去切焦点 |
| `xg.cs` 命令行 | 中文输入法组合状态会吃掉 Tab/Enter（WT 一样）；`ImmAssociateContext` 对 TSF 输入法无效，别想着关掉 |
| `xg.cs` `AttachImageLists` | 明细视图用 `LVM_SETIMAGELIST` 塞系统图标列表；图标视图用托管 `LargeImageList`，两者别抢同一个槽 |
| `xf.ps1` bash 会话 | 裸字节写 `BaseStream`；`Console.InputEncoding` 带 BOM 会污染 stdin |
| `xc.ps1` | 只认锁定的 HWND，窗口关闭立刻报错，绝不换窗口 |
| `xs.cs` TUI | `C_OFF` 必须 `ESC[0m`；控制键要走 `KeyChar` 回退分支 |

---

## 已验证的回滚

```powershell
powershell -ExecutionPolicy Bypass -File setup.ps1 -Remove
```

实测：删快捷方式/入口/联结 → 校验三项全 False → 重装 → 又能用。
**注册表零写入**（`HKCU\Software\Classes` 下无本程序键）。
