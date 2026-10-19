// xs - a full screen TUI for the Windows Shell namespace.
//
//   line 1 : current namespace path + counts
//   line 2 : command line (editable, with completion and history)
//   below  : a live rendered listing (Explorer-like: name / size / date)
//   last   : status + message
//
// Commands operate on ShellItems, so "This PC", "Recycle Bin" and zip-like
// shell folders are first class citizens. Runs inside Windows Terminal, so it
// inherits WT's font, colours, panes and scrollback.
//
// build: csc /target:exe /r:Microsoft.CSharp.dll xs.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Xs
{
    // ---------------------------------------------------------------- state
    static dynamic shell;
    static dynamic cur;
    static string curPath = "";
    static string curName = "";
    static readonly List<string> back = new List<string>();
    static readonly List<string> hist = new List<string>();
    static string histFile;

    static bool tui;
    static bool detail;
    static string msg = "";
    static bool quit;

    // view
    static List<ItemInfo> view = new List<ItemInfo>();
    static int cursor, offset;
    static string viewKind = "dir";          // dir | roots | find

    // input
    static StringBuilder input = new StringBuilder();
    static int ipos;
    static int hIdx;
    static string stash = "";

    // ---------------------------------------------------------------- colours
    const string ESC = "\u001b";
    static string C_OFF = ESC + "[0m";
    static string C_DIM = ESC + "[90m";
    static string C_DIR = ESC + "[1;34m";
    static string C_ACC = ESC + "[36m";
    static string C_ERR = ESC + "[31m";
    static string C_OK = ESC + "[32m";
    static string C_REV = ESC + "[7m";
    static string C_BOLD = ESC + "[1m";

    // ---------------------------------------------------------------- win32
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);

    static void EnableVt()
    {
        try
        {
            IntPtr hOut = GetStdHandle(-11);
            uint mo;
            if (GetConsoleMode(hOut, out mo)) SetConsoleMode(hOut, mo | 0x0004);   // VT output
        }
        catch { }
        try
        {
            IntPtr hIn = GetStdHandle(-10);
            uint mi;
            if (GetConsoleMode(hIn, out mi)) SetConsoleMode(hIn, mi & ~0x0200u);   // not VT input
        }
        catch { }
    }

    // ---------------------------------------------------------------- boot
    static int Main(string[] args)
    {
        EnableVt();
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }
        catch { Console.Error.WriteLine("cannot create Shell.Application"); return 2; }

        string dir = Directory.GetCurrentDirectory();
        string oneShot = null, scriptFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-h" || args[i] == "--help") { Usage(); return 0; }
            else if (args[i] == "-d" && i + 1 < args.Length) dir = args[++i];
            else if (args[i] == "-f" && i + 1 < args.Length) scriptFile = args[++i];
            else if (args[i] == "-c") { oneShot = string.Join(" ", args, i + 1, args.Length - i - 1); i = args.Length; }
            else if (args[i] == "-t") { dir = args[++i]; }
            else dir = args[i];
        }

        histFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xs", "history.txt");
        try
        {
            if (File.Exists(histFile))
                foreach (var l in File.ReadAllLines(histFile))
                    if (l.Trim().Length > 0) hist.Add(l);
        }
        catch { }
        hIdx = hist.Count;

        if (!Cd(dir)) { Console.Error.WriteLine("cannot open: " + dir); return 2; }

        bool batch = (oneShot != null || scriptFile != null) || Console.IsOutputRedirected;
        if (batch)
        {
            C_DIM = C_DIR = C_ACC = C_ERR = C_OK = C_REV = C_BOLD = "";
            if (scriptFile != null)
            {
                foreach (var ln in File.ReadAllLines(scriptFile))
                {
                    string c = ln.Trim();
                    if (c.Length == 0 || c.StartsWith("#")) continue;
                    RunLine(c);
                }
                return 0;
            }
            if (oneShot != null)
            {
                foreach (var part in oneShot.Split(new string[] { "&&" }, StringSplitOptions.None))
                {
                    string c = part.Trim();
                    if (c.Length > 0) RunLine(c);
                }
                return 0;
            }
            RunLine("ls");
            return 0;
        }

        tui = true;
        return RunTui();
    }

    static void Usage()
    {
        Console.WriteLine(@"xs - full screen shell for the Windows Shell namespace

  xs                interactive TUI
  xs D:\项目         start in that folder
  xs -c ""ls""       run one command (batch output, no TUI)
  xs -f cmds.txt    run a command file

  keys      Tab complete   ↑↓ history or move   Enter run/open   Backspace up
            Ctrl+A/E/U/K/W line edit   Ctrl+C clear   Ctrl+Q quit
  commands  ls ll cd pwd up back mkdir rm cp mv open info find roots help exit");
    }

    // ---------------------------------------------------------------- tui core
    static int W2 { get { try { return Math.Max(40, Console.WindowWidth); } catch { return 120; } } }
    static int H2 { get { try { return Math.Max(10, Console.WindowHeight); } catch { return 30; } } }

    static int RunTui()
    {
        try { Console.TreatControlCAsInput = true; } catch { }
        Console.Out.Write(ESC + "[?1049h" + ESC + "[?25l" + ESC + "[2J");
        Console.Out.Flush();
        RefreshView();

        int lastW = -1, lastH = -1;
        while (!quit)
        {
            int w = W2, h = H2;
            if (w != lastW || h != lastH) { Console.Out.Write(ESC + "[2J"); lastW = w; lastH = h; }
            Draw(w, h);
            ConsoleKeyInfo k;
            try { k = Console.ReadKey(true); }
            catch { break; }
            Handle(k);
        }

        Console.Out.Write(ESC + "[?25h" + ESC + "[?1049l");
        Console.Out.Flush();
        SaveHist();
        try { Console.TreatControlCAsInput = false; } catch { }
        return 0;
    }

    static string Fit(string s, int width)
    {
        if (width <= 0) return "";
        int acc = 0;
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            int cw = IsWide(c) ? 2 : 1;
            if (acc + cw > width) { sb.Append('…'); return sb.ToString(); }
            sb.Append(c); acc += cw;
        }
        return sb.ToString() + new string(' ', width - acc);
    }

    static void Draw(int w, int h)
    {
        var sb = new StringBuilder();
        sb.Append(ESC + "[H");

        // line 1: path + counts
        string head = (curPath.StartsWith("::") ? curName : curPath);
        string selInfo = (cursor >= 0 && cursor < view.Count) ? view[cursor].Name : "";
        string counts = view.Count + " items" + (detail ? "  [detail]" : "") + (selInfo.Length > 0 ? "  ·  " + selInfo : "");
        int cw2 = Width(counts);
        string left = Fit(head, Math.Max(10, w - cw2 - 2));
        sb.Append(C_ACC + left + C_OFF + C_DIM + counts + C_OFF + ESC + "[K\r\n");

        // line 2: command line
        string promptText = "❯ ";
        sb.Append(C_ACC + promptText + C_OFF);
        string shown = input.ToString();
        sb.Append(Fit(shown, w - 2) + ESC + "[K\r\n");

        // listing
        int rows = h - 3;
        if (rows < 1) rows = 1;
        if (cursor < offset) offset = cursor;
        if (cursor >= offset + rows) offset = cursor - rows + 1;
        if (offset < 0) offset = 0;

        int nameW = Math.Max(20, w - 28);
        for (int i = 0; i < rows; i++)
        {
            int idx = offset + i;
            if (idx >= view.Count) { sb.Append(ESC + "[K\r\n"); continue; }
            var it = view[idx];
            string size = it.IsFolder ? "<DIR>" : Human(it.Size);
            string date = (it.Modified == DateTime.MinValue || it.Modified.Year < 1900) ? "" : it.Modified.ToString(detail ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm");
            string name = it.Name + (it.IsFolder ? "\\" : "");
            string type = it.Type ?? "";
            string line;
            if (detail)
            {
                int tw = 16;
                line = Fit(date, detail ? 20 : 16) + " " + Pad(size, 10) + " " + Fit(type, tw) + " " + Fit(name, Math.Max(10, w - 20 - 10 - tw - 4));
            }
            else
            {
                line = Fit(name, nameW) + " " + Pad(size, 10) + " " + Fit(date, 16);
            }
            if (idx == cursor)
                sb.Append(C_REV + Fit(line, w) + C_OFF + "\r\n");
            else
                sb.Append((it.IsFolder ? C_DIR : C_OFF) + Fit(line, w) + C_OFF + "\r\n");
        }

        // last line: message
        string tail = msg;
        if (tail.Length == 0) tail = "Tab 补全   ↑↓ 移动/历史   Enter 打开/执行   Backspace 上一级   Ctrl+Q 退出";
        sb.Append(C_DIM + Fit(tail, w) + C_OFF + ESC + "[K");

        // cursor back to the command line
        int col = 3 + Width(shown.Substring(0, Math.Min(ipos, shown.Length)));
        if (col > w) col = w;
        sb.Append(ESC + "[" + 2 + ";" + col + "H");
        sb.Append(ESC + "[?25h");

        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
        // (message line already appended above)
    }

    static void DoEnter()
    {
        string line = input.ToString().Trim();
        if (line.Length == 0) { OpenCursor(); return; }
        hist.Add(line); hIdx = hist.Count;
        input.Length = 0; ipos = 0;
        RunLine(line);
        if (viewKind == "dir") RefreshView();
    }

    static void DoBackspace()
    {
        if (input.Length == 0) { if (!Cd("..")) msg = "已经在根"; return; }
        if (ipos > 0) { input.Remove(ipos - 1, 1); ipos--; }
    }

    static void Handle(ConsoleKeyInfo k)
    {
        bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;

        if (ctrl && k.Key == ConsoleKey.Q) { quit = true; return; }
        if (ctrl && k.Key == ConsoleKey.C) { input.Length = 0; ipos = 0; msg = ""; return; }
        if (ctrl && k.Key == ConsoleKey.L) { Console.Out.Write(ESC + "[2J"); return; }

        // some hosts deliver control keys as bare characters
        if ((int)k.Key == 0)
        {
            if (k.KeyChar == '\r' || k.KeyChar == '\n') { DoEnter(); return; }
            if (k.KeyChar == '\b' || k.KeyChar == '\u007f') { DoBackspace(); return; }
            if (k.KeyChar == '\t') { Complete(); return; }
        }

        if (k.Key == ConsoleKey.Enter)
        {
            string line = input.ToString().Trim();
            if (line.Length == 0) { OpenCursor(); }
            else
            {
                hist.Add(line); hIdx = hist.Count;
                input.Length = 0; ipos = 0;
                RunLine(line);
                if (viewKind == "dir") RefreshView();
            }
            return;
        }

        if (k.Key == ConsoleKey.UpArrow)
        {
            if (input.Length == 0) { if (cursor > 0) cursor--; }
            else if (hIdx > 0)
            {
                if (hIdx == hist.Count) stash = input.ToString();
                hIdx--;
                input.Length = 0; input.Append(hist[hIdx]); ipos = input.Length;
            }
            return;
        }
        if (k.Key == ConsoleKey.DownArrow)
        {
            if (input.Length == 0) { if (cursor < view.Count - 1) cursor++; }
            else if (hIdx < hist.Count)
            {
                hIdx++;
                input.Length = 0; input.Append(hIdx == hist.Count ? stash : hist[hIdx]); ipos = input.Length;
            }
            return;
        }
        if (k.Key == ConsoleKey.PageUp) { cursor = Math.Max(0, cursor - (H2 - 3)); return; }
        if (k.Key == ConsoleKey.PageDown) { cursor = Math.Min(Math.Max(0, view.Count - 1), cursor + (H2 - 3)); return; }
        if (k.Key == ConsoleKey.Home && input.Length == 0) { cursor = 0; return; }
        if (k.Key == ConsoleKey.End && input.Length == 0) { cursor = Math.Max(0, view.Count - 1); return; }

        if (k.Key == ConsoleKey.Backspace) { DoBackspace(); return; }
        if (k.Key == ConsoleKey.Delete) { if (ipos < input.Length) input.Remove(ipos, 1); return; }
        if (k.Key == ConsoleKey.LeftArrow) { if (ipos > 0) ipos--; return; }
        if (k.Key == ConsoleKey.RightArrow) { if (ipos < input.Length) ipos++; return; }
        if (k.Key == ConsoleKey.Home) { ipos = 0; return; }
        if (k.Key == ConsoleKey.End) { ipos = input.Length; return; }
        if (ctrl && k.Key == ConsoleKey.A) { ipos = 0; return; }
        if (ctrl && k.Key == ConsoleKey.E) { ipos = input.Length; return; }
        if (ctrl && k.Key == ConsoleKey.U) { input.Remove(0, ipos); ipos = 0; return; }
        if (ctrl && k.Key == ConsoleKey.K) { input.Remove(ipos, input.Length - ipos); return; }
        if (ctrl && k.Key == ConsoleKey.W)
        {
            int i = ipos;
            while (i > 0 && input[i - 1] == ' ') i--;
            while (i > 0 && input[i - 1] != ' ') i--;
            input.Remove(i, ipos - i); ipos = i; return;
        }
        if (k.Key == ConsoleKey.Tab) { Complete(); return; }
        if (k.Key == ConsoleKey.Escape) { input.Length = 0; ipos = 0; msg = ""; return; }

        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
        {
            input.Insert(ipos, k.KeyChar); ipos++;
        }
    }

    static void OpenCursor()
    {
        if (cursor < 0 || cursor >= view.Count) return;
        var it = view[cursor];
        if (viewKind == "roots") { Cd(it.Path); viewKind = "dir"; RefreshView(); return; }
        if (it.IsFolder) { Cd(it.Path); viewKind = "dir"; RefreshView(); return; }
        try { Process.Start(it.Path); msg = "opened " + it.Name; }
        catch (Exception e) { msg = e.Message; }
    }

    static void Complete()
    {
        string text = input.ToString();
        int start = text.LastIndexOf(' ') + 1;
        string token = text.Substring(start).ToLowerInvariant();

        var cands = new List<string>();
        foreach (var it in view)
            if (it.Name.ToLowerInvariant().StartsWith(token))
                cands.Add(it.IsFolder ? it.Name + "\\" : it.Name);
        if (cands.Count == 0) { msg = "无匹配"; return; }
        cands.Sort(StringComparer.CurrentCultureIgnoreCase);

        if (cands.Count == 1)
        {
            input.Length = 0; input.Append(text.Substring(0, start) + cands[0]);
            ipos = input.Length; msg = "";
            return;
        }
        string common = cands[0];
        foreach (var c in cands)
        {
            int i = 0;
            while (i < common.Length && i < c.Length && common[i] == c[i]) i++;
            common = common.Substring(0, i);
        }
        if (common.Length > token.Length)
        {
            input.Length = 0; input.Append(text.Substring(0, start) + common);
            ipos = input.Length;
        }
        msg = cands.Count + " 个候选: " + string.Join("  ", cands.Take(6).ToArray()) + (cands.Count > 6 ? " …" : "");
    }

    static void RefreshView()
    {
        try
        {
            if (viewKind == "dir") view = Sorted(CurrentItems());
            else if (viewKind == "roots") view = Sorted(RootItems());
        }
        catch (Exception e) { msg = e.Message; }
        if (cursor >= view.Count) cursor = Math.Max(0, view.Count - 1);
        if (cursor < 0) cursor = 0;
    }

    static List<ItemInfo> Sorted(List<ItemInfo> src)
    {
        return src.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void SaveHist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(histFile));
            File.WriteAllLines(histFile, hist.Skip(Math.Max(0, hist.Count - 500)).ToArray());
        }
        catch { }
    }

    // ---------------------------------------------------------------- output
    static void L(string s) { if (tui) msg = Strip(s); else Console.WriteLine(s); }
    static void W(string s) { if (!tui) Console.Write(s); }
    static void Err(string s) { if (tui) msg = Strip(s); else Console.WriteLine(C_ERR + s + C_OFF); }
    static void Msg(string s) { msg = s; }

    static string Strip(string s)
    {
        var sb = new StringBuilder();
        bool esc = false;
        foreach (char c in s)
        {
            if (c == '\u001b') { esc = true; continue; }
            if (esc) { if (char.IsLetter(c)) esc = false; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- namespace
    static bool Cd(string target)
    {
        dynamic f = null;
        try
        {
            target = (target ?? "").Trim().Trim('"');
            if (target.Length == 0) target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (target == "..") { try { f = cur.ParentFolder; } catch { f = null; } }
            else if (target == "-")
            {
                if (back.Count == 0) { Err("no history"); return false; }
                target = back[back.Count - 1]; back.RemoveAt(back.Count - 1);
                f = shell.NameSpace(target);
            }
            else
            {
                bool abs = target.StartsWith("::") || target.Contains(":\\") || target.StartsWith("\\\\") ||
                           target.StartsWith("shell:") || (target.Length == 2 && target[1] == ':');
                if (abs) f = shell.NameSpace(target);
                if (f == null && Path.IsPathRooted(target)) f = shell.NameSpace(target);
                if (f == null)
                {
                    try
                    {
                        dynamic it = cur.ParseName(target);
                        if (it != null && (bool)it.IsFolder) f = shell.NameSpace((string)it.Path);
                    }
                    catch { }
                }
                if (f == null)
                {
                    try
                    {
                        foreach (var c in CurrentItems())
                        {
                            if (!c.IsFolder) continue;
                            if (string.Equals(c.Name, target, StringComparison.CurrentCultureIgnoreCase))
                            { f = shell.NameSpace((string)c.Path); break; }
                        }
                    }
                    catch { }
                }
            }

            if (f == null) { Err("没有这个位置: " + target); return false; }

            string np = (string)f.Self.Path;
            if (np != curPath) back.Add(curPath);
            cur = f; curPath = np;
            try { curName = (string)f.Self.Name; } catch { curName = np; }
            return true;
        }
        catch (Exception e) { Err("cd: " + e.Message); return false; }
    }

    class ItemInfo
    {
        public string Name, Path, Type;
        public bool IsFolder, IsLink;
        public long Size;
        public DateTime Modified;
        public dynamic Com;
    }

    static List<ItemInfo> ItemsOf(dynamic f)
    {
        var list = new List<ItemInfo>();
        if (f == null) return list;
        dynamic items = f.Items();
        int n = (int)items.Count;
        for (int i = 0; i < n; i++) list.Add(Wrap(items.Item(i)));
        return list;
    }

    static List<ItemInfo> CurrentItems()
    {
        try { return ItemsOf(cur); }
        catch { cur = shell.NameSpace(curPath); return ItemsOf(cur); }
    }

    static ItemInfo Wrap(dynamic it)
    {
        var o = new ItemInfo();
        o.Com = it;
        try { o.Name = (string)it.Name; } catch { o.Name = "?"; }
        try { o.Path = (string)it.Path; } catch { o.Path = null; }
        try { o.IsFolder = (bool)it.IsFolder; } catch { }
        try { o.IsLink = (bool)it.IsLink; } catch { }
        try { o.Type = (string)it.Type; } catch { }
        if (!o.IsFolder) { try { o.Size = Convert.ToInt64(it.Size); } catch { } }
        try { o.Modified = (DateTime)it.ModifyDate; } catch { o.Modified = DateTime.MinValue; }
        return o;
    }

    static List<ItemInfo> RootItems()
    {
        var list = new List<ItemInfo>();
        Action<string, string> add = delegate(string name, string p)
        {
            var o = new ItemInfo();
            o.Name = name; o.Path = p; o.IsFolder = true; o.Type = "命名空间";
            list.Add(o);
        };
        add("此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        add("回收站", "::{645FF040-5081-101B-9F08-00AA002F954E}");
        add("控制面板", "::{26EE0668-A00A-44D7-9371-BEB064C98683}");
        add("网络", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
        add("用户目录", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady) add("盘 " + d.Name, d.Name);
        return list;
    }

    static bool IsFs(string p)
    {
        return p != null && p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
    }

    static bool Like(string name, string pat)
    {
        var rx = new StringBuilder("^");
        foreach (char c in pat)
        {
            if (c == '*') rx.Append(".*");
            else if (c == '?') rx.Append('.');
            else rx.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        rx.Append("$");
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    static List<ItemInfo> Match(string pattern)
    {
        var all = viewKind == "dir" ? CurrentItems() : view;
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return all;
        var res = new List<ItemInfo>();
        foreach (var it in all) if (Like(it.Name, pattern)) res.Add(it);
        return res;
    }

    // ---------------------------------------------------------------- commands
    static List<string> Tok(string s)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char c in s)
        {
            if (c == '"') { q = !q; continue; }
            if (!q && (c == ' ' || c == '\t')) { if (sb.Length > 0) { res.Add(sb.ToString()); sb.Length = 0; } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    static void RunLine(string line)
    {
        var t = Tok(line);
        if (t.Count == 0) return;
        string v = t[0].ToLowerInvariant();
        string a1 = t.Count > 1 ? t[1] : null;
        try
        {
            switch (v)
            {
                case "ls": if (tui) { detail = false; viewKind = "dir"; RefreshView(); msg = view.Count + " 项"; } else CmdLs(a1, false); break;
                case "ll": if (tui) { detail = true; viewKind = "dir"; RefreshView(); msg = view.Count + " 项"; } else CmdLs(a1, true); break;
                case "cd": if (a1 == null) Msg(curPath); else { if (Cd(a1)) { viewKind = "dir"; if (tui) RefreshView(); else Msg(curPath); } } break;
                case "pwd": Msg(curPath.StartsWith("::") ? curName + "  " + curPath : curPath); if (!tui) L(curPath); break;
                case "up": if (Cd("..")) { viewKind = "dir"; if (tui) RefreshView(); } break;
                case "back":
                    if (back.Count == 0) { Err("没有历史"); break; }
                    { string p = back[back.Count - 1]; back.RemoveAt(back.Count - 1); if (Cd(p)) { viewKind = "dir"; if (tui) RefreshView(); } }
                    break;
                case "mkdir": case "md": CmdMkdir(a1); break;
                case "rm": case "del": CmdRm(t); break;
                case "cp": CmdCopy(t, false); break;
                case "mv": CmdCopy(t, true); break;
                case "open": case "start": CmdOpen(a1); break;
                case "info": CmdInfo(a1); break;
                case "find": CmdFind(a1); break;
                case "roots": if (tui) { viewKind = "roots"; RefreshView(); cursor = 0; } else CmdRoots(); break;
                case "help": case "?": Usage2(); break;
                case "exit": case "q": if (tui) quit = true; break;
                default: Err("未知命令: " + v + "   (help)"); break;
            }
            if (tui && viewKind == "dir" && (v == "mkdir" || v == "rm" || v == "cp" || v == "mv")) RefreshView();
        }
        catch (Exception e) { Err(e.GetType().Name + ": " + e.Message); }
    }

    static void Usage2()
    {
        Msg("cd ls ll pwd up back mkdir rm cp mv open info find roots exit   |   Tab 补全  ↑↓ 移动  Enter 打开");
    }

    static string Human(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1048576) return (n / 1024.0).ToString("0.0") + " KB";
        if (n < 1073741824L) return (n / 1048576.0).ToString("0.0") + " MB";
        return (n / 1073741824.0).ToString("0.0") + " GB";
    }

    static int Width(string s)
    {
        int w = 0;
        foreach (char c in s) w += IsWide(c) ? 2 : 1;
        return w;
    }

    static bool IsWide(char c)
    {
        return (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) ||
               (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
               (c >= 0xFFE0 && c <= 0xFFE6);
    }

    static string Pad(string s, int width)
    {
        int w = Width(s);
        if (w > width)
        {
            var sb = new StringBuilder();
            int acc = 0;
            foreach (char c in s)
            {
                int cw = IsWide(c) ? 2 : 1;
                if (acc + cw > width - 1) break;
                sb.Append(c); acc += cw;
            }
            s = sb.ToString() + "…"; w = Width(s);
        }
        return s + new string(' ', Math.Max(0, width - w));
    }

    static void CmdLs(string pattern, bool longFmt)
    {
        dynamic other = null;
        string pat = pattern;
        if (!string.IsNullOrEmpty(pattern) && (pattern.IndexOf('\\') >= 0 || pattern.IndexOf('/') >= 0 || Path.IsPathRooted(pattern)))
        {
            other = ResolveFolder(pattern);
            if (other != null) pat = "*";
        }
        List<ItemInfo> items;
        if (other != null) items = ItemsOf(other);
        else items = Match(pat);
        items = Sorted(items);
        if (items.Count == 0) { L(C_DIM + "(空)" + C_OFF); return; }
        foreach (var it in items)
        {
            string size = it.IsFolder ? "<DIR>" : Human(it.Size);
            string date = (it.Modified == DateTime.MinValue || it.Modified.Year < 1900) ? "" : it.Modified.ToString("yyyy-MM-dd HH:mm");
            string name = it.Name + (it.IsFolder ? "\\" : "");
            if (longFmt) L(C_DIM + Pad(date, 16) + C_OFF + " " + Pad(size, 10) + " " + (it.IsFolder ? C_DIR : "") + name + C_OFF);
            else L((it.IsFolder ? C_DIR : "") + Pad(name, 46) + C_OFF + C_DIM + Pad(size, 10) + " " + date + C_OFF);
        }
        long files = 0, dirs = 0, bytes = 0;
        foreach (var it in items) { if (it.IsFolder) dirs++; else { files++; bytes += it.Size; } }
        L(C_DIM + "  " + dirs + " dirs, " + files + " files, " + Human(bytes) + C_OFF);
    }

    static void CmdMkdir(string name)
    {
        if (name == null) { Err("用法: mkdir <名字>"); return; }
        if (!IsFs(curPath)) { Err("当前是虚拟命名空间，不能建目录"); return; }
        string full = Path.Combine(curPath, name);
        if (Directory.Exists(full)) { Err("已存在: " + full); return; }
        Directory.CreateDirectory(full);
        Msg("已创建 " + full);
    }

    static bool Confirm(string text)
    {
        if (tui) return true;               // TUI 里不弹交互，改为必带 -f
        W(C_ERR + text + " ? [y/N] " + C_OFF);
        var k = Console.ReadKey(true);
        W(k.KeyChar + "\r\n");
        return k.KeyChar == 'y' || k.KeyChar == 'Y';
    }

    static void CmdRm(List<string> t)
    {
        bool rec = t.Contains("-r") || t.Contains("-rf") || t.Contains("-fr");
        bool force = t.Contains("-f") || t.Contains("-rf") || t.Contains("-fr");
        var pats = t.Skip(1).Where(x => !x.StartsWith("-")).ToList();
        if (pats.Count == 0) { Err("用法: rm [-r] [-f] <模式>"); return; }
        if (tui && !force) { Err("TUI 里请显式加 -f（例如 rm -rf 名字）"); return; }
        foreach (var p in pats)
        {
            foreach (var it in Match(p))
            {
                if (!IsFs(it.Path)) { Err("跳过（非真实路径）: " + it.Name); continue; }
                if (it.IsFolder)
                {
                    if (!rec) { Err("是目录，需要 -r: " + it.Name); continue; }
                    if (!force && !Confirm("删除目录及全部内容: " + it.Path)) continue;
                    Directory.Delete(it.Path, true);
                }
                else
                {
                    if (!force && !Confirm("删除 " + it.Path)) continue;
                    File.Delete(it.Path);
                }
                Msg("已删 " + it.Name);
            }
        }
    }

    static void CmdCopy(List<string> t, bool move)
    {
        if (t.Count < 3) { Err("用法: " + t[0] + " <模式> <目标>"); return; }
        string pat = t[1], dest = t[2];
        dynamic destFolder = ResolveFolder(dest);
        if (destFolder == null) { Err("目标无效: " + dest); return; }
        string destPath = (string)destFolder.Self.Path;

        foreach (var it in Match(pat))
        {
            try
            {
                if (IsFs(it.Path) && IsFs(destPath))
                {
                    string target = Path.Combine(destPath, it.Name);
                    if (move) { if (it.IsFolder) Directory.Move(it.Path, target); else File.Move(it.Path, target); }
                    else { if (it.IsFolder) CopyDir(it.Path, target); else File.Copy(it.Path, target, true); }
                }
                else
                {
                    if (move) destFolder.MoveHere(it.Com, 0x14);
                    else destFolder.CopyHere(it.Com, 0x14);
                }
                Msg((move ? "已移动 " : "已复制 ") + it.Name);
            }
            catch (Exception e) { Err(it.Name + ": " + e.Message); }
        }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static dynamic ResolveFolder(string p)
    {
        try
        {
            if (p == "..") return cur.ParentFolder;
            dynamic f = null;
            if (p.StartsWith("::") || Path.IsPathRooted(p)) f = shell.NameSpace(p);
            if (f == null) { dynamic it = cur.ParseName(p); if (it != null && (bool)it.IsFolder) f = shell.NameSpace((string)it.Path); }
            return f;
        }
        catch { return null; }
    }

    static void CmdOpen(string name)
    {
        if (name == null) { Err("用法: open <名字>"); return; }
        dynamic it = null;
        try { it = cur.ParseName(name); } catch { }
        if (it == null)
        {
            var m = Match(name);
            if (m.Count == 0) { Err("没找到: " + name); return; }
            if (m.Count > 1) { Err("有多项: " + string.Join(", ", m.Select(x => x.Name).ToArray())); return; }
            it = m[0].Com;
        }
        if ((bool)it.IsFolder) { if (Cd((string)it.Path)) { viewKind = "dir"; if (tui) RefreshView(); } return; }
        it.InvokeVerb("open");
        Msg("已打开 " + (string)it.Name);
    }

    static void CmdInfo(string name)
    {
        if (name == null) { Err("用法: info <名字>"); return; }
        dynamic it = null;
        try { it = cur.ParseName(name); } catch { }
        if (it == null) { Err("没找到: " + name); return; }
        var o = Wrap(it);
        string s = o.Name + "  |  " + (o.Path ?? "-") + "  |  " + (o.Type ?? "-") +
                   "  |  " + (o.IsFolder ? "folder" : Human(o.Size)) +
                   (o.Modified != DateTime.MinValue ? "  |  " + o.Modified.ToString("yyyy-MM-dd HH:mm") : "") +
                   "  |  " + (IsFs(o.Path) ? "filesystem" : "virtual");
        Msg(s);
        if (!tui) L(s);
    }

    static void CmdFind(string pat)
    {
        if (pat == null) { Err("用法: find <模式>"); return; }
        if (!tui) { foreach (var it in Match(pat)) L((it.IsFolder ? C_DIR : "") + it.Name + C_OFF); return; }
        var res = new List<ItemInfo>();
        foreach (var it in CurrentItems()) if (Like(it.Name, pat)) res.Add(it);
        view = Sorted(res); viewKind = "find"; cursor = 0; offset = 0;
        Msg("搜索结果: " + res.Count + " 项（cd/up 回到目录视图）");
    }

    static void CmdRoots()
    {
        foreach (var it in RootItems()) L(C_DIR + Pad(it.Name, 12) + C_OFF + C_DIM + it.Path + C_OFF);
    }
}
