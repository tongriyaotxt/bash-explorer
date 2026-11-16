# Nautilus 能给 xg 什么

这份文档回答两件事：

1. Nautilus 的「直接输入选中文件」（type-ahead）到底怎么定义，xg 这次按什么语义实现；
2. Nautilus 还有哪些东西值得搬进 xg，按性价比排个队。

参考的是 Nautilus 社区维护的 type-ahead 补丁，不是凭印象：

- 需求：[Implement "Jump to file" feature (type ahead / type to seek) #1157](https://gitlab.gnome.org/GNOME/nautilus/-/work_items/1157)
- 设计讨论：[Files: type ahead #142](https://gitlab.gnome.org/Teams/Design/whiteboards/-/work_items/142)
- 补丁实现：[albertvaka/nautilus commit 5342cad](https://gitlab.gnome.org/albertvaka/nautilus/-/commit/5342cade7e750db61d71c9179c32d9123d0ec66b)
- 打包/开关说明：[fedora-nautilus-typeahead](https://github.com/nelsonaloysio/fedora-nautilus-typeahead)

---

## 1. Nautilus 的 type-ahead 是什么

Nautilus 现在的默认行为是「打字 = 搜索」：敲一个字符就进搜索模式、异步出结果。对已经知道文件名的人来说，这有两个问题（#1157 里写得很清楚）：

- 搜索有延迟（大目录、网络盘、慢盘），打断连续浏览；
- 视图被换成搜索结果，位置感丢失。

社区补丁把「打字 = 跳转」恢复回来，并在首选项里给一个开关（`type-ahead-search`）。补丁的实际语义（`nautilus-window-slot.c`）：

| 细节 | 补丁怎么做 |
|---|---|
| 缓冲区 | 复用工具栏的 query editor：打的字进输入框，不弹搜索 |
| 匹配 | 对当前目录的显示名做 **casefold 前缀匹配**（`g_utf8_casefold` + `g_utf8_collate_key_for_filename`） |
| 选谁 | 命中项按**当前排序**排序，选第一个 |
| 视图 | **不重排、不切搜索视图**，只改 selection |
| 超时 | `CLEAR_QUERY_EDITOR_TIMEOUT = 1000`，1 秒没新输入就清空缓冲区 |
| 开关 | `org.gnome.nautilus.preferences type-ahead-search`：**true = 搜索（默认）**，false = 只选中（跳转） |

注意：上面这张表是**开关关掉时**的补丁行为。默认 `type-ahead-search=true` 时，
`nautilus_window_slot_handle_event` 里的 `if (retval && type_ahead_search())` 会调用
`set_search_visible(TRUE)`，同一个 `query_editor_changed_callback` 走 `set_search_query` 分支，
**视图切成搜索结果（列表只剩命中项）**——这是上游 Nautilus 的默认效果。

补丁模式（开关关掉）一句话：**一次同步的前缀查找 + 选中，不是搜索。**

---

## 2. xg 这次怎么落地

xg 把两条路都做了，默认跟 Nautilus 一致走 **search**，`ta select` 切补丁语义：

| 能力 | Nautilus | xg `ta search`（默认） | xg `ta select` |
|---|---|---|---|
| 查询显示 | 工具栏 query editor / 搜索框 | 顶部命令行 `⌕`（命令行有内容时用左下角药丸） | 同左 |
| 匹配 | 搜索：名字包含查询，递归、排名 | 名字**包含**查询（子串），当前目录 | **前缀**匹配，大小写不敏感 |
| 视图 | 切成搜索结果列表 | `items` 只留命中项，`RenderRows` 重建 | 不重排、不过滤，只 `SelectOnly` |
| 选谁 | 第一个结果自动选中 | 第一个命中自动选中 | 按当前排序取第一个 |
| 超时 | 搜索不超时 | 不超时，`Esc` 恢复 | 1000ms 清空前缀 |
| 开关 | `type-ahead-search` | `ta search` | `ta select` |
| 焦点 | 文件视图有焦点 | 打开窗口焦点就在列表 | 同左 |

源码里几个容易忽略、xg 已经对齐的细节：

- **Space / Menu / keynav 不算查询字符**：`nautilus_query_editor_handle_event` 里
  `nautilus_gtk_search_entry_is_keynav_event(...) || keyval == GDK_KEY_space || keyval == GDK_KEY_Menu`
  直接 `PROPAGATE`，交给视图。xg 的 `TypeAheadChar` 对空格返回 false。
- **Backspace**：上游 `nautilus_window_slot_handle_event` 在搜索不可见时对 Backspace 直接
  `PROPAGATE`，补丁又把 `slot.up` 绑到 `BackSpace` —— 也就是**type-ahead 下按 Backspace 是上一级**。
  xg 改成「有前缀先删字符，没前缀才后退」，更好用，但这是和上游的**有意差异**。
- **清除图标**：`entry_changed_cb` 按文本是否为空切 `clear_icon`；xg 没有单独的 ×，`Esc` 清空。
- **匹配用 casefold + collate**：`g_utf8_casefold(name, text_len)` +
  `g_utf8_collate_key_for_filename`，是「按字节取前缀再做本地化排序键」；
  xg 用 `StartsWith` / `IndexOf` + `CurrentCultureIgnoreCase`，常规名字等价。
- **排序用视图当前状态**：`nautilus_files_view_get_sort_state` + `nautilus_file_compare_for_sort_by_attribute_q`，
  含 `directories_first` / `reversed`；xg 直接扫已经是当前排序的 `items` / `allItems`。
- **选中后 reveal**：`nautilus_files_view_set_selection(..., TRUE)`；xg 的 `SelectOnly` 里有 `EnsureVisible`。

实现位置（`xg.cs`）：

- `IconList.WndProc`：截 `WM_CHAR`。原生 ListView 自己也会做前缀查找，必须抢在它前面，否则会出现「选中跳了、但状态栏/浮层没反应」的两套逻辑。
- `TypeAheadChar` / `TypeAheadBackspace` / `ApplyTypeAhead` / `TypeAheadReset`：缓冲区、匹配、超时、search 过滤。
- `allItems` / `items` / `filtering`：完整列表与当前可见列表；`TypeAheadReset` 负责恢复。
- `ShowTypeAhead` / `EndTypeAheadMirror`：命令行镜像与退出。
- 自检：`__xg_ta` 目录里断言 `a → al → alp → alpi`、Backspace、中文、超时、`ta select` / `ta search` 的条数差异、
  `Ctrl+L` / `Esc`、换目录清前缀；另有 `WM_CHAR` 直投 ListView 的消息路径测试。

---

## 3. 逐项对比：Nautilus vs xg

| 能力 | Nautilus | xg 现状 | 结论 |
|---|---|---|---|
| 直接输入 | 默认搜索（过滤列表）/ 补丁跳转（只选中） | ✅ 两条都实现，`ta search|select` 切换 | 已对齐 |
| 面包屑 + 位置输入 | 路径栏 + `Ctrl+L` | ✅ 面包屑 + `Ctrl+L` | 已有 |
| 列表 / 图标视图 | ✅ | ✅ `F4` | 已有 |
| 图标缩放 | `Ctrl+滚轮` | ✅ 48/96/144/256 | 已有 |
| 排序（名称/日期/类型/大小，目录优先） | ✅ | ✅ 列头 + `sort` 命令 | 已有 |
| 真缩略图 | ✅ | ✅ `IShellItemImageFactory` | 已有 |
| 预览 | Sushi 空格快速预览 | ✅ 常驻右栏 + 全屏看图 | 形态不同，够用 |
| 隐藏文件开关 | `Ctrl+H` | ❌ | **值得加（便宜）** |
| 批量重命名 | ✅ 查找替换 / 模板 / 冲突预览 | ❌ 只支持单个 | **值得加（P0）** |
| 按模式选择 | `Ctrl+S` Select Items Matching | 只有 `sel *.md` 命令 | 包个对话框就成（P0） |
| Copy to… / Move to… | ✅ | ❌ | **值得加（P1）** |
| 压缩 / 解压 | ✅ | ❌ | 值得加（P1，走 Shell verb） |
| 回收站 / 永久删除 | ✅ | ✅ | 已有 |
| 属性（含权限） | ✅ 多文件属性 | 系统属性对话框（单个） | 够用 |
| Open With 子菜单 | ✅ | 只有「用系统程序打开」 | 可选（P2） |
| 新建文件夹 / 模板 | ✅ | `mkdir` | 够用 |
| 操作队列 + 进度 + 取消 | ✅ | ❌ 同步复制/删除 | **值得加（P1，大目录会卡 UI）** |
| 撤销 | `Ctrl+Z`（部分操作） | ❌ | 可选（P2） |
| 最近 / 收藏 / 星标 | ✅ | roots 里只有命名空间 | 可选（P2） |
| 递归搜索 / 全文搜索 | ✅ tracker/localsearch | ❌ | 可选（P2，别混进 type-ahead） |
| 标签页 | `Ctrl+T` / `Ctrl+W` | ❌ | 大改（P1/P2） |
| 拖放 | ✅ | ❌ | 大改（P2） |

---

## 4. 值得加进 xg 的，按性价比排序

### P0：小改动，马上能用

1. **批量重命名（F2 多选）**
   - 做一个对话框：查找/替换 + 序号模板（`{n}` / `{name}` / `{ext}`）+ 结果预览 + 冲突标红。
   - xg 已有单文件 `RenameSelected()`，批量只是循环 + 预览；UI 可以照 `HistForm` 的写法。
   - Nautilus 的坑：循环改名冲突、大小写不敏感文件系统、扩展名不要重复拼。

2. **Select Items Matching（`Ctrl+S`）**
   - `WildMatch` 已经有了，缺的只是一个输入框；复用 `sel` 的语义（`*.md`、`?`）。
   - 选中后 `EnsureVisible` 第一项，状态栏报数量。

3. **Copy to… / Move to…（右键）**
   - `FolderBrowserDialog` 选目标目录 → 复用 `CopyDir` / `Directory.Move` / `File.Move`。
   - 虚拟命名空间（`::{GUID}` / `shell:`）直接禁用，和 `wt` / `ex` 的判定一致。

4. **隐藏文件开关（`Ctrl+H`）**
   - `Xg.ReadFolder` 出来的项按 `FileAttributes.Hidden` 过滤；`rootsView` 不滤。
   - 系统文件（`System` 属性）建议单独一档，别和普通隐藏混在一起。
   - 开关状态只存内存即可，先别写配置。

### P1：中等改动，体验提升最大

5. **操作队列 + 进度 + 取消**
   - 现在 `cp -r` / `rm -rf` 在 UI 线程跑，大目录直接假死。
   - 抽一个 `FileOp`（源、目标、类型、进度、取消），用 `BackgroundWorker` 或 `Task` + `IProgress`，主线程只更新进度条。
   - Nautilus 的进度浮层可以简化成一个模态进度对话框（带取消）。

6. **标签页**
   - 先把「一个窗口的状态」抽成 `Session`：`cur / curPath / back / forward / items / sortCol / sortAsc / details / iconPx`。
   - 然后 `TabControl` 每个页放一组 `lv + preview`，或者复用同一组控件、切页时换 state（前者简单、内存大；后者省内存、代码绕）。
   - 快捷键照 Nautilus：`Ctrl+T` 新标签、`Ctrl+W` 关、`Ctrl+PageUp/Down` 切。

7. **压缩 / 解压**
   - 右键「压缩为 zip」「解压到此处」，走 Shell verb（`Compress-Archive` 或 `Shell.Application` 的 `CopyHere`），保留进度/取消。
   - 先只支持 zip，7z/rar 交给系统关联。

8. **空格快速预览**
   - 复用 `Viewer`：`Space` 打开当前选中（图片/文本），`Esc` 关；不抢焦点时用非模态。
   - Nautilus 用 Sushi，xg 已有自己的看图器，成本很低。

### P2：大改 / 可选

9. 收藏 / 最近 / 回收站视图（需要持久化 + 命名空间映射）
10. 递归搜索（独立模式，显式触发；不要做成「打字即搜索」）
11. 拖放（OLE `DragDrop`，跨进程）
12. 撤销（记录操作日志；移到回收站的可还原）

---

## 5. 不该照抄的

- **「打字即搜索」**：Nautilus 的默认行为，对键盘浏览是倒退；xg 的 type-ahead 更合适，搜索应该显式触发（以后要做也是独立模式）。
- **GTK / Adwaita 的视觉细节**：xg 是 WinForms + 深色自绘，抄交互不抄皮。
- **Tracker / localsearch 依赖**：xg 是单文件、零依赖，搜索要自己遍历或走 Shell。
- **侧边栏 + 标签页的 GNOME 习惯**：xg 的入口是命令行，标签页/侧边栏要按 xg 的键位体系重新设计。

---

## 6. 参考

- [#1157 Jump to file](https://gitlab.gnome.org/GNOME/nautilus/-/work_items/1157)
- [#142 Files: type ahead（设计讨论）](https://gitlab.gnome.org/Teams/Design/whiteboards/-/work_items/142)
- [type-ahead 补丁（nautilus-window-slot.c）](https://gitlab.gnome.org/albertvaka/nautilus/-/commit/5342cade7e750db61d71c9179c32d9123d0ec66b)
- [fedora-nautilus-typeahead（安装/开关）](https://github.com/nelsonaloysio/fedora-nautilus-typeahead)
- [Nautilus NEWS（批量重命名、压缩、标签页等历史）](https://gitlab.gnome.org/GNOME/nautilus/-/raw/main/NEWS)
