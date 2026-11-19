# xg —— 图形化界面 + 命令行，操作 ShellItem

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ D:\ › 项目 › 闲扯 › explorer-bash › xs                       ← 面包屑，可点   │
│ ←  →  ↑ │ ⟳  明细                                            ← 工具条        │
│ ❯ cd ..                                                      ← 命令行（Ctrl+L 聚焦）
├───────────────────────────────────────────────────────────────┬──────────────┤
│ 名称 ▲           │ 修改日期         │ 类型            │ 大小 │  xg.cmd      │
│ 📄 xg.cmd        │ 2026-10-19 23:12 │ Windows 命令脚本 │ 295 B│  D:\…\xg.cmd │
│ 🗔 xg.exe        │ 2026-10-21 13:16 │ 应用程序         │ 60 KB│  Windows …   │
│ 📄 xg.cs         │ 2026-10-21 13:16 │ CS 文件          │ 63 KB│  ────        │
│ ▸ xs.cmd         │ 2026-10-19 22:58 │ Windows 命令脚本 │ 116 B│  @echo off   │
├───────────────────────────────────────────────────────────────┴──────────────┤
│ 12 项   ·   名称 ↑   ·   xg.cmd   295 B                       ← 状态行        │
└──────────────────────────────────────────────────────────────────────────────┘
```

图标来自**系统图标列表**（`SHGetFileInfo` + `LVM_SETIMAGELIST`），
所以文件夹、驱动器、关联程序、快捷方式画的都是资源管理器画的那个图标。
图标视图用的是 **shell 真缩略图**（`IShellItemImageFactory`），文本文件也能出缩略图。

---

## 上手

```
xg                         打开「此电脑」——直接选盘，不落在 C 盘
xg .                       在当前目录打开（就开这儿）
xg D:\项目                  从指定位置打开
xg --here                  等价于 xg .
xg --run "ic;sort size" D:\项目   打开后依次执行命令（分号分隔）
xg --selftest D:\项目       跑一遍自检，结果写 %TEMP%\xg-selftest.txt 后自己关掉
```

**默认起点是「此电脑」**（`::{20D04FE0-3AEA-1069-A2D8-08002B30309D}`，Shell 的 Drives 文件夹）：
列表里就是 C:/D:/E:/F: 和云盘，回车或双击即进；`Backspace` 到顶会停在「此电脑」不再上跳。
要「就在这个目录开」，写 `xg .` —— 入口里的 `xg.cmd` 默认**不再**塞 `%CD%`，只把参数原样转给 exe。

在任意目录敲 `xg` 就行（入口已装到 PATH）。

**打开后焦点在文件列表**：直接打字就是「输入选择」（见下一节）；要打命令按 `Ctrl+L`。

```
❯ ls -t                 明细 + 按时间排序
❯ cd "OS (C:)"          进 C 盘（显示名也认）
❯ cd ..                 上一级；cd - 回上一处；cd ~ 回家
❯ mkdir -p a/b/c        建多级目录（建完自动选中）
❯ cp -r a b             复制目录；mv b c 改名/移动
❯ cat 说明.md           正文进右侧预览
❯ rm -rf c              永久删除（trash 才是回收站）
❯ open readme.txt       用默认程序打开
```

在列表里直接敲字母/数字 = 前缀选择文件，不会再跳到命令行；命令行按 `Ctrl+L` 进。

---

## 键位（按资源管理器的习惯来的）

| 键 | 作用 |
|---|---|
| 字母/数字 | **直接输入**：search 过滤列表 / select 前缀选中（`ta` 切换） |
| `Enter` | 打开 / 进入目录 |
| `Backspace` | 输入选择时删一个前缀字符；没有前缀时后退 |
| `Esc` | 取消输入选择；命令行空着时回列表 |
| `Ctrl+L` | 回命令行（窗口里任何焦点都行：列表 / 工具条 / 面包屑 / 预览） |
| `Ctrl+F` | 回列表，继续输入搜索 |
| `Alt+←` / `Alt+→` | 后退 / 前进 |
| `Alt+↑` | 上一级 |
| `Delete` | **移到回收站**（带确认，只删选中的；绝不兼任"返回"） |
| `Shift+Delete` | **永久删除**（二次确认，不进回收站） |
| `Alt+Enter` | 属性对话框（走 `ShellExecuteEx` + `properties`） |
| `F2` / `F4` / `F5` | 改名 / 切视图 / 刷新（焦点在命令行、且命令行空着时也生效） |
| `Ctrl+A` | 全选 |
| `Ctrl+Shift+C` | 复制选中项路径 |
| `Ctrl+滚轮` | 图标视图缩放（48 → 96 → 144 → 256） |
| 滚轮 / `PgUp` `PgDn` | 列表滚动（焦点在命令行时也生效——滚轮按鼠标位置转发给列表） |
| `Tab` | 命令行里补全（命令名 / 路径 / 子目录）；多候选循环，`Shift+Tab` 反向 |

命令行里：`Backspace` **只删字符**，`Alt+↑` 才是上一级，`Tab` 补全，`↑↓` 翻历史。

---

## 直接输入（学 Nautilus：search / select 两条路）

Nautilus 源码里其实是两套行为，由一个开关 `org.gnome.nautilus.preferences type-ahead-search` 决定：

- **默认（true）= search**：打字 → `nautilus_window_slot_handle_event` 把键转给查询框 →
  `query_editor_changed_callback` 里 `set_search_visible(TRUE)` + `set_search_query`，
  视图切成搜索结果（**列表只剩命中项**）。
- **补丁（false）= type-ahead select**：同一个回调走 else 分支，对当前目录做**大小写不敏感的
  前缀匹配**，按当前排序取第一个，`nautilus_files_view_set_selection(first)`，**只选中、不过滤**；
  1000ms 没有新输入就清空查询（`CLEAR_QUERY_EDITOR_TIMEOUT`）。

xg 两条都实现了，默认跟 Nautilus 一致走 **search**，`ta select` 切到补丁语义：

```
ta           看当前模式
ta search    输入即过滤列表（默认，Nautilus 上游）
ta select    只选中第一个命中，列表不动（Nautilus type-ahead 补丁）
```

共同点：

- **打开窗口就能用**：焦点在列表，直接敲 `al`，不用先点列表。
- 查询显示在**顶部命令行**（提示符 `⌕`）；命令行里已有半条命令时，改用列表左下角的小药丸，绝不覆盖正在写的命令。
- `Ctrl+L` 在**整个窗口**任何焦点下都能回命令行（`ProcessCmdKey` 接的，不只是列表）；进入命令行会**退出搜索、恢复完整列表**，
  避免命令行背后压着一个 0 命中的过滤。空命令行显示灰字「Ctrl+L 输入命令 · Esc 回列表搜索」，聚焦后消失。
- 回搜索：命令行空着按 `Esc`（有字先清字），或任何地方按 `Ctrl+F`；焦点回列表后直接打字就是搜索。
  命令行里 `Tab` 补全、`↑↓` 翻历史。
- **search**：当前目录、名字**包含**查询（子串，不区分大小写），列表只剩命中项，第一个自动选中；
  `Esc` 恢复完整列表，`Backspace` 删字符，`Enter` 打开。**不递归、不落盘**（Nautilus 会递归搜索，见 `NAUTILUS.md`）。
- **select**：前缀匹配，按当前排序取第一个，不重排、不过滤；1 秒没新输入自动清空前缀。
- `Space` 不参与查询（Nautilus 的 `nautilus_query_editor_handle_event` 明确把 Space / Menu / keynav 放回视图）。
- `Backspace` 有前缀时删一个字符；没有前缀时才是「后退」。上游 Nautilus 在 type-ahead 下把 `Backspace` 绑成「上一级」，
  xg 改成编辑前缀，更好用。
- 中文也能匹配：输入法上屏的字符走 `WM_CHAR`，`中文` → `中文条目.txt`。英文前缀建议先 `Shift` 切英文，否则字母会被输入法组词吃掉。
- 连续输入就是延长查询，不循环同前缀项；要换下一个，改查询或等超时（select 模式）。
- 实现：`IconList.WndProc` 截 `WM_CHAR`（原生 ListView 自己也会做前缀查找，必须抢在它前面），
  `TypeAheadChar` / `ApplyTypeAhead` 负责匹配；search 用 `allItems` 备份 + `items` 过滤，`TypeAheadReset` 恢复。

---

## 命令行补全（和 cmd / PowerShell 对齐）

| 输入 | 按 Tab 之后 |
|---|---|
| `mk` | `mkdir `（命令名，补尾随空格） |
| `cd ex` | `cd explorer-bash\`（唯一目录，补反斜杠） |
| `cd xs\xg.e` | `cd xs\xg.exe`（子目录里补） |
| `cd 有` | `cd "有 空格\"`（带空格自动加引号） |
| `cd ..\f` | `cd ..\fixture\`（相对 / 上级 / 绝对路径都行） |
| `cd `（多个候选） | 依次循环，状态栏显示 `补全 2/5  [b.txt] …` |

规则：

- **唯一匹配** → 直接插入：命令补空格、目录补 `\`、文件不补
- **多个匹配** → 先插第一个，再按 `Tab` 往后循环，`Shift+Tab` 往前
- 候选顺序：命令 → 目录 → 文件，同组按名字；状态栏里目录带 `\` 作提示
- 命令名只在**第一段**补（`cd ` 之后补的是路径，不会补出命令）
- 输入框里的文本一改，循环就重置；候选既来自文件系统，也包含当前列表的显示名
  （所以 `cd "OS (C:)"` 这种也能补）
- **中文输入法处于组合状态时，`Tab` / `Enter` 会先被输入法吃掉**（Windows Terminal 一样）：
  先空格/回车上屏，或 `Ctrl+Space` 切英文再敲

---

## 工具条 / 面包屑 / 排序

- **面包屑**：路径按 `\` 拆成可点的段，点哪段去哪段；`::` / `shell:` 这类虚拟位置的
  显示名和路径整体展示（不拆）。
- **工具条**：`←` `→` `↑` `⟳` + 视图切换按钮（按钮文字跟随当前视图：明细 / 图标）。
- **排序**：点列头按该列排，再点一次反向；表头带 `▲ / ▼`。
  **目录永远排在文件前面**（和资源管理器一致），反向只在同组内反向。
  命令行等价写法：`sort name|date|type|size [asc|desc]`。

---

## 右键菜单

`打开` · `在此处打开终端`（`wt -d`，没有 wt 回退 `cmd /k cd`）· `在资源管理器中显示`（`/select`，和 `ex -s` 共用 `LaunchExplorer`）
· `复制路径` · `重命名` · `移到回收站` · `永久删除` · `属性`。
多选时只保留通用项；虚拟命名空间的项不能重命名、不能在资源管理器里定位。

---

## 预览窗格

右侧固定一栏，选中即可预览：

- **图片 / 视频 / PDF / Office**：走系统缩略图接口 `IShellItemImageFactory`
  （跟资源管理器画的是同一张缩略图，不是我自己解码）
- **文本类**（.txt .md .cs .json .ps1 …）：直接显示正文前 120 行
- **文件夹**：显示子项数量（数到 2000 就停，避免扫 `C:\Windows`）
- 下方始终显示名称、完整路径、类型、大小、修改时间
- 换选中项时，上一张缩略图会 `Dispose`，连续翻大图不再涨内存

---

## 图标视图

`ic` / `F4` 进入。缩略图按 **48 / 96 / 144 / 256** 逻辑像素四档（150% DPI → ×1.5，最大 256）：

- 切档位：`Ctrl+滚轮`（列表有焦点时）、工具条上的尺寸按钮、命令 `icon 144` / `big`（依次循环）
- 生成顺序：文件先试 `IShellItemImageFactory`（真缩略图）→ 失败回退 `SHGetFileInfo(SHGFI_ICON)`
- 缩略图数量随尺寸收缩（256px 最多 120 张），防止超大目录一把梭
    
---

## 看相片

选中图片按 `Enter` 打开**内置全屏看图**（不是丢给系统"照片"）：

- `←` `→`（或滚轮、`PgUp/PgDn`）在**当前目录的所有图片**之间切换，到头绕回；`Home` / `End` 跳到首尾
- `Esc` 或双击关闭，回列表后焦点回到命令行
- 支持 `.jpg .jpeg .png .gif .webp .bmp .tif .tiff .ico .avif`；GIF 走内存流，动画还在
- 大图按屏幕尺寸取缩略图（上限 2048），切图时释放上一张
- 想交给系统默认程序（例如"照片"）：右键 →「用系统程序打开」

右栏预览也还在：图片会**吃满整栏高度**，想看更大就把分隔条往左拖（拖完自动按新尺寸重新取图）。

`sel` 可以按名字选：`sel *.md`、`sel pic.jpg`、`sel 2024*`，配合预览和批量操作。

---

## 命令（语法尽量按 Linux 来）

| Linux | 这里 | 说明 |
|---|---|---|
| `ls [-l] [-t] [-S] [-r] [目录]` | ✅ | 切明细并按需排序：`-t` 时间、`-S` 大小、`-r` 反向 |
| `cd [目录]` / `cd -` / `cd ~` | ✅ | 无参回用户目录；`-` 回上一处；`/` 和 `\` 都认 |
| `pwd` | ✅ | 状态栏显示当前路径 |
| `mkdir [-p] 目录` | ✅ | 多级要 `-p`（没有 `-p` 且上级不存在会报错） |
| `rm [-r] [-f] 目标…` | ✅ | **永久删除**；目录要 `-r`，`-f` 不再确认 |
| `rmdir 目录` | ✅ | 只删空目录 |
| `mv 源 目标` | ✅ | 改名 / 移动；目标是已有目录就移进去 |
| `cp [-r] 源 目标` | ✅ | 复制；目录要 `-r` |
| `cat 文件` | ✅ | 正文进右侧预览（前 120 行），并选中该项 |
| `touch 文件` | ✅ | 建空文件 / 刷新修改时间 |
| `trash 目标…` | — | 走回收站（等于 GUI 的 `Delete`） |
| `clear` | ✅ | 清命令历史 |

保留的 GUI 命令：`ic`（图标视图）`icon 48|96|144|256` `big` `ta [search|select]` `sel 名字/*.md` `sort name|size [asc|desc]`
`open` `wt [目录|文件] [命令…]` `ex [-s] [目录|文件]` `info` `roots` `up` `back` `help` `exit`

Windows 别名也认：`dir` `md` `del` `erase` `rd` `ren` `move` `copy` `type` `cls`。

### wt：在当前目录开 Windows Terminal

```
wt                         # xg 的当前目录
wt .                       # 同上，写清楚点
wt ..                      # 上一级
wt D:\项目                 # 指定目录
wt 某文件夹                # 也认当前列表里的显示名
wt C:\Windows\notepad.exe  # 给文件 → 在它所在目录开
wt D:\项目 bash            # 目录 + 想在里面跑的命令（目录后面接什么就传什么）
```

等价于 `wt.exe -d "<目录>"`，进程工作目录也一起设好；机器上没有 `wt.exe` 就回退 `cmd /k cd /d`——
右键菜单「在此处打开终端」和它是同一个函数（`LaunchTerminal`），改一处两边都变。

虚拟命名空间（此电脑 / 回收站 / `shell:`）没有文件系统路径，`wt` 直接拦下报「当前不是文件系统位置」。
`-` 开头的词会被命令行当标志位吃掉，所以 wt 自己的参数（`-w` `-p` 那套）别在 xg 里写。
干跑：设 `XG_WT_DRY=1` 时只把拼好的命令行追加到 `%TEMP%\xg-wt.txt`，不真的启终端（自检用）。

### ex：在当前目录开资源管理器

`wt` 的镜像——同一套目标解析，只是开出来的是资源管理器（`explorer.exe`）。

```
ex                            # xg 的当前目录
ex .                          # 同上，写清楚点
ex ..                         # 上一级
ex D:\项目                    # 指定目录
ex 某文件夹                   # 也认当前列表里的显示名
ex C:\Windows\notepad.exe     # 给文件 → 在它所在目录开
ex -s C:\Windows\notepad.exe  # 给文件 + -s → 进去并选中它
```

- 不带 `-s`：拼 `explorer.exe "<目录>"`；带 `-s`：拼 `/select,"<路径>"`（explorer 自己会开父目录并定位）
- 进程工作目录一起设好；右键菜单「在资源管理器中显示」现在也走同一个 `LaunchExplorer(dir, select)`，
  改一处两边都变（和 `wt` / `LaunchTerminal` 的关系一样）
- 别名 `explorer` / `exp`（`ex` 是 `ex -s` 之外最省事的那一个）
- 虚拟命名空间同样拦下报「当前不是文件系统位置」
- 干跑：`XG_EX_DRY=1` 只把拼好的命令行追加到 `%TEMP%\xg-ex.txt`，不真弹窗口（自检靠它）

`cd` 认四类目标：文件系统路径（含 `/`、`..`、`~`）、`::{GUID}`、**子项显示名**（`cd "OS (C:)"`）、`shell:` 前缀。

`Alt+↑ / Alt+← / Alt+→` 在命令行里同样有效（焦点默认就在命令行）。`up` 命令走同一套逻辑：
文件系统按路径算父目录，盘根再往上落到「此电脑」。

**删除**：`Delete` / `Shift+Delete` 在列表里有焦点、或者命令行空着的时候都生效——
`mkdir a` 之后不用先点列表，直接 `Delete` 就能删。失败会弹窗写清原因，不再静默。
命令行里 `rm` 是永久删除，`trash` 才是回收站——这点和 Linux 一致，别混。

---

## 历史文件夹

每次**真正换目录**都会记一笔（F5 刷新不算）。存在 `%LOCALAPPDATA%\xg\history.tsv`，
格式 `次数 \t 时间 \t 路径`，最多留 500 条；环境变量 `XG_HISTORY` 可改路径（自检用）。

| 命令 | 作用 |
|---|---|
| `hist` | 弹出查询窗：顶部输入过滤，`↑↓` 选，`Enter` 跳过去，`Esc` 关 |
| `hist 关键字` | 直接跳到最匹配的一个（不再弹窗） |
| `hist -t` / `hist -c` | 查询窗按「最近」/「次数」排序 |
| `hist -x` | 清空历史（带确认） |

默认排序是 **frecency**：`次数 / (1 + 距今天数/14)`——最近去得多排前面，不是单纯按次数。
查询窗里每条右边显示 `次数 × 最近时间`，底部一行给出条目数、排序方式、历史文件路径。

---

## 自检

```powershell
xs\xg.exe --selftest D:\某目录
# 结果：%TEMP%\xg-selftest.txt
```

自检不依赖鼠标，按脚本跑一遍：明细 ↔ 图标切视图、三种排序、面包屑段、右键菜单项状态、
文本预览、图片预览、命名空间根，最后自己关窗口。`build.ps1 -Test` 会自动跑它；
结果里出现 `EXCEPTION` 或 `FAIL` 都直接让构建失败。

type-ahead 的自检是真建一个 `__xg_ta` 目录，按 `a → al → alp → alpi`、Backspace、中文、
1 秒超时、`Ctrl+L` / `Esc` 焦点、换目录清前缀逐条断言；另外用 `WM_CHAR` 直接打给
ListView，验的是"消息真的走到 `IconList.WndProc`"这条路径，不是只调内部函数。

`ex` 的自检走干跑（自检里临时设 `XG_EX_DRY=1`），把「无参 / 给目录 / 给文件 / `-s` / 不存在的目标」
五种拼出来的命令行逐条写进结果，不真的弹资源管理器；结果里同时给出 `ex` 的补全和错误提示。

崩溃会写 `%TEMP%\xg-crash.log`（`Application.ThreadException` + `AppDomain.UnhandledException`）。

---

## 这轮踩的坑（都是真问题）

1. **窗口被 DPI 二次缩放**。exe 没有 manifest → 系统按 150% 拉伸，
   而 `GetWindowRect` 返回的是虚拟坐标，列宽全错、截图也被裁。
   修法：启动时 `SetProcessDpiAwareness(2)`，自己按 `DpiX/96` 算尺寸和字号。
2. **路径栏/命令行被系统主题刷成白色**。`TextBox` 的 `BackColor` 在只读/主题下会被覆盖。
   修法：把两者放进自绘 `Panel`，输入框 `BorderStyle=None`，配色由父容器统一。
3. **列宽不跟着窗口**。`FitColumns` 在布局稳定前就算了一次。
   修法：`Shown` + `SizeChanged` + `BeginInvoke` 再算一次，且所有尺寸乘 DPI 系数。
4. **`ImageList` 不复制位图**。`Images.Add(img)` 只是存引用，句柄要到赋值给控件时
   才创建；这之前 `img.Dispose()` 会让 `CreateHandle` 抛 `ArgumentException: 参数无效`。
   表现极具迷惑性：`lv.View` 已经切过去了，但后面几行没执行（按钮文字还是"明细"）。
   修法：用一个 `List<Image>` 持有所有塞进 `ImageList` 的位图，重建时才统一释放。
5. **排序只改表头不改行**。`RenderRows` 只重建行，不会重排；
   排序必须 `items.Sort(comparer)` 之后再渲染。自检里"按大小降序"抓到过这个。
6. **右键菜单和 `SetForegroundWindow` 冲突**：抓图工具抢焦点会把弹出的菜单关掉，
   所以截图工具改成只 `PrintWindow`，不抢前台。
7. **Tab 在 TextBox 里是"对话框导航键"**：默认 `IsInputKey(Tab)` 为 false，
   按键先去切焦点，`KeyDown` 里的补全根本轮不到。修法：`CmdBox : TextBox` 里
   `IsInputKey` 对 Tab 返回 true，并在 `PreviewKeyDown` 里 `e.IsInputKey = true` 双保险。
8. **中文输入法组合状态会吃掉 `Tab` 和 `Enter`**。调试时一度以为"回车没反应"，
   其实是 MS 拼音的候选窗在等上屏（`ImmAssociateContext` 对 TSF 输入法无效）。
   Windows Terminal 同样如此，不是程序 bug——`Ctrl+Space` 切英文即可。
9. **ListView 会自己吞 `WM_CHAR` 做前缀查找，`KeyPress` 根本不触发**。
   现象：状态栏没有任何提示、浮层不出现，但选中项确实跳到了 `alpha.txt`——原生
   ListView 干的。修法：在 `IconList.WndProc` 里先截 `WM_CHAR`，命中就 `return`，
   顺带把原生那套前缀查找关掉，避免两套各选各的。
10. **`SendMessage` 默认走 A 版，中文 `WM_CHAR` 会被按 ANSI 代码页解释而丢掉**。
    自检里发 `0x4E2D`（中）时 `TypeAheadChar` 收不到；`DllImport` 加
    `CharSet = CharSet.Unicode` 走 `SendMessageW` 才对。真实键盘输入同样是 UTF-16。
11. **默认焦点决定了"像不像 Nautilus"**。原来 `Shown` 里 `txtCmd.Focus()`，用户打开
    就打字只会得到 `未知命令: alpha`；改成 `FocusList()`，命令行用 `Ctrl+L` 进，
    `Esc`（命令行空着）回列表，两边能来回。

---

## 构建

```powershell
csc /target:winexe /out:xg.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll xg.cs
```

源码 `xg.cs`（单文件，C# 5 语法，兼容 .NET Framework 4.x 自带编译器）。

---

## 与 xs / xc / xf 的关系

| | 界面 | 操作方式 | 对象 |
|---|---|---|---|
| **xg** | 图形（图标列表） | 命令行 + 鼠标 | ShellItem |
| **xs** | 终端 TUI | 纯键盘 | ShellItem |
| **xc** | 真资源管理器 | 命令驱动它 | 真 Explorer 窗口 |
| **xf** | 终端 | `@*` 占位符 | 选区 → bash |

---

## 回滚

```powershell
Remove-Item "$env:LOCALAPPDATA\Microsoft\WindowsApps\xg.cmd"
```

程序本体在 `D:\项目\闲扯\explorer-bash\xs\`，删目录即可。
**不写注册表、不起服务、不改 PATH。**
