# 进度存档 / 下次从这里接着 debug

> 存档时间：2026-10-19 23:25 · 仓库：`tongriyaotxt/explorer-bridge`（private，main）
> 代码全在 `D:\项目\闲扯\explorer-bash`，已提交并推送。

---

## 一句话现状

四个工具都能跑，`xg`（图形界面）是最后在做的一个，**界面/预览/键位已成型，还剩一批打磨项**。

| 工具 | 形态 | 状态 | 入口 |
|---|---|---|---|
| **xf** | 终端 | ✅ 可用 | `xf` / `xf sh "…@*"` |
| **xc** | 驱动真 Explorer | ✅ 可用 | `xc`（锁定一个窗口） |
| **xs** | 终端 TUI | ✅ 可用 | `xs` / `xsw`（WT 里） |
| **xg** | WinForms 图形界面 | 🟡 可用，待打磨 | `xg` |

---

## xg 已经做完的

- 布局：路径栏 / 命令行（启动即聚焦）/ 明细列表 / **右侧预览窗格** / 状态栏
- 图标：系统图标列表（`SHGetFileInfo` + `LVM_SETIMAGELIST`），和资源管理器同一套
- 预览：`IShellItemImageFactory` 真缩略图；文本类显示正文前 120 行；文件夹显示子项数
- 键位（对齐 Explorer）：
  `Enter` 打开 · `Backspace` 后退 · `Alt+←/→` 前进后退 · `Alt+↑` 上一级 ·
  `Delete` 移到回收站（带确认）· `F2` 改名 · `F5` 刷新 · `F4` 切视图 ·
  `Ctrl+A` 全选 · `Ctrl+Shift+C` 复制路径 · 直接敲字母跳到命令行
- 命令行：`ls ll ic cd pwd up back mkdir open info roots help exit`，`Tab` 补全、`↑↓` 历史

---

## 下次接着做的事（按优先级）

1. **排序**：点列头排序（现在固定"目录在前 + 名称"）
2. **右键菜单**：打开 / 在终端中打开 / 复制路径 / 属性
3. **工具条**：后退、前进、上一级、刷新按钮（现在只有快捷键）
4. **面包屑**：路径栏拆成可点片段
5. **图标视图的缩略图**：`ic` 模式现在还是 32px 系统图标，应该换 96/256 缩略图
6. **性能**：>2000 项的目录（现在同步枚举 + 逐个取图标索引），考虑后台加载
7. **内存**：预览的 `Image` 没 Dispose，连续切换大图会涨
8. **高 DPI**：只验证过 150%，175%/200% 与实际 100% 都没验
9. **删除**：目前只能进回收站，加 Shift+Delete 永久删除
10. **xs**：TUI 里的选中项还没有"批量操作"（只有 cd/open）

---

## 复现 / 构建

```powershell
# 装入口（xf/xc/xs/xsw/xg 五个 .cmd 到 WindowsApps，纯用户目录，可逆）
powershell -ExecutionPolicy Bypass -File setup.ps1

# 重建 exe（会先检查并补 UTF-8 BOM）
powershell -ExecutionPolicy Bypass -File build.ps1 -Test

# 直接跑
xs\xg.exe "D:\某目录"
xs\xs.exe -c "ls"
```

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

---

## 代码坑位索引（改之前先看这里）

| 位置 | 坑 |
|---|---|
| `xg.cs` 构造 | `AutoScaleMode.None` + 手动 `DpiX/96` 缩放；用 Dpi 会自动二次缩放 |
| `xg.cs` `FitColumns` | 必须在 `Shown` / `SizeChanged` / `BeginInvoke` 里各算一次 |
| `xg.cs` 路径栏/命令行 | 用自绘 `Panel` 托管，裸 `TextBox` 的 `BackColor` 会被主题刷白 |
| `xg.cs` `RenderPreview` | 文本类必须先于缩略图判断，否则文本文件显示成通用图标 |
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
