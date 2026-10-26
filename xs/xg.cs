// xg - a graphical shell browser for the Windows Shell namespace.
//
//   line 1 : breadcrumb path bar (click a segment to go there)
//   line 2 : toolbar (back / forward / up / refresh / view)
//   line 3 : command line (cd / ls / ll / mkdir / sort / ... with Tab + history)
//   below  : a real GUI listing that uses the *system* image list, so the
//            icons are exactly the ones Explorer draws (drives, special
//            folders, file types, shortcuts). Icon view uses real shell
//            thumbnails.
//
// build: csc /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll
//            /r:Microsoft.CSharp.dll xg.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Xg
{
    public static dynamic shell;

    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { LogCrash("ThreadException", e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { LogCrash("AppDomain", e.ExceptionObject as Exception); };
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }
        catch { MessageBox.Show("无法创建 Shell.Application"); return; }

        string start = Environment.CurrentDirectory;
        bool selfTest = false;
        string runScript = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--selftest") { selfTest = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { start = args[i + 1]; i++; } }
            else if (a == "--run") { if (i + 1 < args.Length) { runScript = args[i + 1]; i++; } }
            else if (!a.StartsWith("--")) start = a;
        }
        var frm = new Frm(start);
        Application.AddMessageFilter(frm);       // 滚轮转发到列表

        if (selfTest)
        {
            frm.Shown += delegate
            {
                var sb = new StringBuilder();
                try { frm.SelfTest(sb); }
                catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); LogCrash("selftest", ex); }
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "xg-selftest.txt"), sb.ToString(), Encoding.UTF8); }
                catch { }
                frm.Close();
            };
        }
        else if (runScript != null)
        {
            frm.Shown += delegate
            {
                foreach (string cmd in runScript.Split(';'))
                {
                    string c = cmd.Trim();
                    if (c.Length == 0) continue;
                    try { frm.Run(c); } catch (Exception ex) { LogCrash("run:" + c, ex); }
                }
            };
        }

        Application.Run(frm);
    }

    public static void LogCrash(string where, Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "xg-crash.log"),
                "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + "\r\n" + ex + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    // ---------------------------------------------------------------- win32
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0,
                      SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10,
                      FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint LVM_SETIMAGELIST = 0x1003;
    public const uint LVM_SETICONSPACING = 0x1035;
    public const int LVSIL_NORMAL = 0, LVSIL_SMALL = 1;

    public static IntPtr SystemImageList(uint which)
    {
        SHFILEINFO info = new SHFILEINFO();
        return SHGetFileInfo("", 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                             SHGFI_SYSICONINDEX | (uint)which);
    }

    // 图标索引缓存：同一扩展名的文件共享一个图标，2000 项目录不必做 2000 次 shell 调用
    static readonly Dictionary<string, int> iconCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static int IconIndex(string path, bool isFolder, uint which)
    {
        string key;
        if (isFolder) key = "D|" + (path == null ? "" : path);
        else
        {
            string ext = "";
            try { if (path != null) ext = Path.GetExtension(path); } catch { }
            key = "F|" + ext.ToLowerInvariant();
        }
        int cached;
        if (iconCache.TryGetValue(key, out cached)) return cached;

        SHFILEINFO info = new SHFILEINFO();
        uint flags = SHGFI_SYSICONINDEX | which;
        bool real = !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        if (real)
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
        else if (isFolder)
            SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags | SHGFI_USEFILEATTRIBUTES);
        else
            SHGetFileInfo("file.txt", FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags | SHGFI_USEFILEATTRIBUTES);
        iconCache[key] = info.iIcon;
        return info.iIcon;
    }

    // 大图标（无缩略图时的兜底）：把系统 HICON 画进 px*px 位图
    public static Image IconBitmap(string path, bool isFolder, int px)
    {
        SHFILEINFO info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        uint size = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
        bool real = !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        if (real) SHGetFileInfo(path, 0, ref info, size, flags);
        else if (isFolder) SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, size, flags | SHGFI_USEFILEATTRIBUTES);
        else SHGetFileInfo("file.txt", FILE_ATTRIBUTE_NORMAL, ref info, size, flags | SHGFI_USEFILEATTRIBUTES);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int side = (int)(px * 0.72);
                using (var ic = Icon.FromHandle(info.hIcon))
                    g.DrawIcon(ic, new Rectangle((px - side) / 2, (px - side) / 2, side, side));
            }
            return bmp;
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    // ---------------------------------------------------------------- shell
    // ---------------- shell thumbnail (IShellItemImageFactory) ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

    public static Image Thumbnail(string path, int px)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            object o;
            SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out o);
            var f = (IShellItemImageFactory)o;
            f.GetImage(new SIZE(px, px), 0x0, out hbmp);   // SIIGBF_RESIZETOFIT
            if (hbmp == IntPtr.Zero) return null;
            return Image.FromHbitmap(hbmp);
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    // 把任意比例的图塞进正方形（透明底），ImageList 只收同尺寸位图
    public static Image FitSquare(Image src, int px)
    {
        var bmp = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Math.Min((float)px / src.Width, (float)px / src.Height);
            int w = Math.Max(1, (int)(src.Width * s));
            int h = Math.Max(1, (int)(src.Height * s));
            g.DrawImage(src, new Rectangle((px - w) / 2, (px - h) / 2, w, h));
        }
        return bmp;
    }

    // ---------------- properties dialog ----------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    public class Item
    {
        public string Name, Path, Type;
        public bool IsFolder;
        public long Size;
        public DateTime Modified;
        public dynamic Com;
        public int IconSmall;
    }

    public static List<Item> ReadFolder(dynamic f)
    {
        var list = new List<Item>();
        dynamic items = f.Items();
        int n = (int)items.Count;
        for (int i = 0; i < n; i++)
        {
            dynamic it = items.Item(i);
            var o = new Item();
            o.Com = it;
            try { o.Name = (string)it.Name; } catch { o.Name = "?"; }
            try { o.Path = (string)it.Path; } catch { o.Path = null; }
            try { o.IsFolder = (bool)it.IsFolder; } catch { }
            try { o.Type = (string)it.Type; } catch { o.Type = ""; }
            if (!o.IsFolder) { try { o.Size = Convert.ToInt64(it.Size); } catch { } }
            try { o.Modified = (DateTime)it.ModifyDate; } catch { o.Modified = DateTime.MinValue; }
            o.IconSmall = IconIndex(o.Path, o.IsFolder, SHGFI_SMALLICON);
            list.Add(o);
        }
        return list;
    }

    public static bool IsFs(string p)
    {
        return p != null && p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
    }

    public static string Human(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1048576) return (n / 1024.0).ToString("0.0") + " KB";
        if (n < 1073741824L) return (n / 1048576.0).ToString("0.0") + " MB";
        return (n / 1073741824L).ToString("0.0") + " GB";
    }

    // ---------------------------------------------------------------- sorting
    public class ItemComparer : IComparer<Item>
    {
        readonly int col;
        readonly bool asc;

        public ItemComparer(int column, bool ascending) { col = column; asc = ascending; }

        public int Compare(Item a, Item b)
        {
            // 目录永远在前（和资源管理器一致），再按列比较
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;

            int c = 0;
            switch (col)
            {
                case 1:
                    c = a.Modified.CompareTo(b.Modified);
                    break;
                case 2:
                    c = string.Compare(a.Type, b.Type, StringComparison.CurrentCultureIgnoreCase);
                    break;
                case 3:
                    c = a.Size.CompareTo(b.Size);
                    break;
                default:
                    c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    break;
            }
            if (c == 0 && col != 0)
                c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return asc ? c : -c;
        }
    }
}

// 文件夹历史：%LOCALAPPDATA%\xg\history.tsv（count \t ticks \t path）
// 环境变量 XG_HISTORY 可覆盖路径（自检用，别污染真实历史）
static class History
{
    public class Entry
    {
        public string Path;
        public int Count;
        public DateTime Last;
    }

    static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.CurrentCultureIgnoreCase);
    static bool loaded;

    public static string FilePath
    {
        get
        {
            string over = Environment.GetEnvironmentVariable("XG_HISTORY");
            if (!string.IsNullOrEmpty(over)) return over;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xg", "history.tsv");
        }
    }

    static void Ensure()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var parts = line.Split(new[] { '\t' }, 3);
                if (parts.Length < 3) continue;
                int c;
                long ticks;
                if (!int.TryParse(parts[0], out c)) continue;
                if (!long.TryParse(parts[1], out ticks)) continue;
                if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) continue;
                var e = new Entry();
                e.Path = parts[2];
                e.Count = c;
                e.Last = new DateTime(ticks);
                if (e.Path.Length > 0) map[e.Path] = e;
            }
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            string fp = FilePath;
            string dir = Path.GetDirectoryName(fp);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string>();
            foreach (var e in map.Values.OrderByDescending(x => x.Last).Take(500))
                lines.Add(e.Count + "\t" + e.Last.Ticks + "\t" + e.Path);
            File.WriteAllLines(fp, lines.ToArray(), Encoding.UTF8);
        }
        catch { }
    }

    public static void Touch(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Ensure();
        Entry e;
        if (!map.TryGetValue(path, out e))
        {
            e = new Entry();
            e.Path = path;
            map[path] = e;
        }
        e.Count++;
        e.Last = DateTime.Now;
        Save();
    }

    // 频率 + 新鲜度：最近去得多排前面（frecency），不是简单按次数
    static double Score(Entry e)
    {
        double days = Math.Max(0, (DateTime.Now - e.Last).TotalDays);
        return e.Count / (1 + days / 14.0);
    }

    public static List<Entry> Snapshot(string filter, string sort)
    {
        Ensure();
        IEnumerable<Entry> src = map.Values;
        if (!string.IsNullOrEmpty(filter))
            src = src.Where(e => e.Path.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0);
        if (sort == "count") src = src.OrderByDescending(e => e.Count).ThenByDescending(e => e.Last);
        else if (sort == "time") src = src.OrderByDescending(e => e.Last);
        else src = src.OrderByDescending(e => Score(e));

        var res = new List<Entry>();
        foreach (var e in src.Take(400)) res.Add(e);
        return res;
    }

    public static int Count { get { Ensure(); return map.Count; } }

    public static void Clear()
    {
        map.Clear();
        Save();
    }
}

class Frm : Form, IMessageFilter
{
    static readonly string[] ColNames = { "名称", "修改日期", "类型", "大小" };

    // ---- strips
    readonly Panel pnlPath = new Panel();
    readonly FlowLayoutPanel crumb = new FlowLayoutPanel();
    readonly Panel pnlTool = new Panel();
    readonly FlowLayoutPanel tools = new FlowLayoutPanel();
    readonly Panel pnlCmd = new Panel();
    readonly CmdBox txtCmd = new CmdBox();
    readonly Label lblStatus = new Label();

    // ---- listing + preview
    readonly IconList lv = new IconList();
    readonly SplitContainer split = new SplitContainer();
    readonly PictureBox pic = new PictureBox();
    readonly Label lblPreview = new Label();
    ImageList ilBig = new ImageList();
    readonly List<Image> bigImages = new List<Image>();

    // ---- chrome
    readonly ToolTip tip = new ToolTip();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    Button btnBack, btnFwd, btnUp, btnRefresh, btnView, btnSize;
    ToolStripMenuItem miOpen, miOpenWith, miTerm, miExplorer, miCopy, miRename, miDelete, miPerma, miProps;

    dynamic cur;
    string curPath = "";
    string curName = "";
    readonly List<string> back = new List<string>();
    readonly List<string> forward = new List<string>();
    bool suppressHistory;
    readonly List<string> hist = new List<string>();
    int hIdx;
    bool details = true;
    float scale = 1f;
    Font crumbFont;

    List<Xg.Item> items = new List<Xg.Item>();
    int sortCol = 0;
    bool sortAsc = true;
    bool rootsView;

    // 图标视图尺寸档位（逻辑像素）；Ctrl+滚轮 / icon 命令 / 工具条按钮切换
    static readonly int[] IconSteps = { 48, 96, 144, 256 };
    int iconPx = 96;

    public Frm(string start)
    {
        Text = "xg · Shell namespace";
        float sc = 1f;
        try { sc = CreateGraphics().DpiX / 96f; } catch { }
        if (sc < 1f) sc = 1f;
        scale = sc;
        Width = (int)(1060 * sc); Height = (int)(680 * sc);
        MinimumSize = new Size(720, 440);
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f * sc);
        BackColor = Color.FromArgb(30, 30, 30);
        crumbFont = new Font("Consolas", 10f * sc, FontStyle.Bold);

        BuildPathBar();
        BuildToolbar();
        BuildCommandLine();
        BuildListing();
        BuildPreview();
        BuildMenu();

        lblStatus.Dock = DockStyle.Bottom;
        lblStatus.Height = (int)(22 * sc);
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.BackColor = Color.FromArgb(40, 40, 40);
        lblStatus.ForeColor = Color.FromArgb(190, 190, 190);
        lblStatus.Padding = new Padding(8, 0, 0, 0);

        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.FixedPanel = FixedPanel.Panel2;
        split.BackColor = Color.FromArgb(60, 60, 60);
        split.SplitterWidth = 1;
        split.Panel1.Controls.Add(lv);
        split.Panel2.Controls.Add(lblPreview);
        split.Panel2.Controls.Add(pic);
        split.Panel2.BackColor = Color.FromArgb(26, 26, 26);
        lv.Zoom = ZoomIcons;
        split.Panel2.Resize += delegate { FitPreviewBox(); };
        split.MouseUp += delegate { if (pic.Visible) RenderPreview(); };   // 拖完分隔条重新取大图

        // docking：后加的 Top 更靠上 → 顺序为 列表/状态/命令行/工具条/路径
        Controls.Add(split);
        Controls.Add(lblStatus);
        Controls.Add(pnlCmd);
        Controls.Add(pnlTool);
        Controls.Add(pnlPath);

        txtCmd.KeyDown += CmdKeyDown;
        // Tab 的官方姿势：PreviewKeyDown 里认成输入键，否则会被当成对话框导航
        txtCmd.PreviewKeyDown += delegate(object s, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Tab) e.IsInputKey = true;
        };
        // 用户自己改了文本 → 候选循环失效
        txtCmd.TextChanged += delegate
        {
            if (compBusy) return;
            compCands.Clear(); compIdx = -1; compHead = null;
        };
        lv.DoubleClick += delegate { OpenSelected(); };
        lv.KeyDown += LvKeyDown;
        lv.SelectedIndexChanged += delegate { UpdateStatus(); RenderPreview(); };
        lv.Resize += delegate { FitColumns(); };
        lv.SizeChanged += delegate { FitColumns(); };
        lv.ColumnClick += ColumnClick;
        lv.MouseUp += LvMouseUp;
        lv.ContextMenuStrip = menu;

        Shown += delegate
        {
            try { split.SplitterDistance = Math.Max(320, split.Width - (int)(380 * sc)); } catch { }
            FitColumns();
            txtCmd.Focus();
            ActiveControl = txtCmd;
            try
            {
                string d = "form=" + ClientSize.Width + "x" + ClientSize.Height +
                           "  lv=" + lv.ClientSize.Width + "x" + lv.ClientSize.Height +
                           "  dpi=" + CreateGraphics().DpiX +
                           "  bounds=" + Bounds.Width + "x" + Bounds.Height +
                           "  cols=" + lv.Columns[0].Width + "," + lv.Columns[1].Width + "," + lv.Columns[2].Width + "," + lv.Columns[3].Width;
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "xg-diag.txt"), d);
            }
            catch { }
        };
        Activated += delegate { if (!txtCmd.Focused && lv.Focused != true) txtCmd.Focus(); };
        FormClosed += delegate { DisposePreviewImage(); ClearBigImages(); };

        LoadFolder(start);
        lv.HandleCreated += delegate { AttachImageLists(); };
        if (lv.IsHandleCreated) AttachImageLists();
    }

    // ---------------------------------------------------------------- chrome
    void BuildPathBar()
    {
        pnlPath.Dock = DockStyle.Top;
        pnlPath.Height = (int)(32 * scale);
        pnlPath.BackColor = Color.FromArgb(24, 24, 24);
        pnlPath.Padding = new Padding(10, 0, 10, 0);

        crumb.Dock = DockStyle.Fill;
        crumb.FlowDirection = FlowDirection.LeftToRight;
        crumb.WrapContents = false;
        crumb.AutoScroll = false;
        crumb.BackColor = Color.FromArgb(24, 24, 24);
        crumb.Margin = new Padding(0);
        crumb.Padding = new Padding(0);
        pnlPath.Controls.Add(crumb);
    }

    void BuildToolbar()
    {
        pnlTool.Dock = DockStyle.Top;
        pnlTool.Height = (int)(36 * scale);
        pnlTool.BackColor = Color.FromArgb(20, 20, 20);
        pnlTool.Padding = new Padding(8, 0, 0, 0);

        tools.Dock = DockStyle.Fill;
        tools.FlowDirection = FlowDirection.LeftToRight;
        tools.WrapContents = false;
        tools.BackColor = Color.FromArgb(20, 20, 20);
        tools.Padding = new Padding(0);
        pnlTool.Controls.Add(tools);

        btnBack = NavButton("←", "后退  (Backspace / Alt+←)", delegate { GoBack(); FocusList(); });
        btnFwd = NavButton("→", "前进  (Alt+→)", delegate { GoForward(); FocusList(); });
        btnUp = NavButton("↑", "上一级  (Alt+↑)", delegate { GoUp(); FocusList(); });
        btnRefresh = NavButton("⟳", "刷新  (F5)", delegate { Populate(); FocusList(); });
        btnView = NavButton("明细", "切换视图  (F4)", delegate { ToggleView(); FocusList(); });
        btnView.Width = (int)(74 * scale);
        btnView.Font = new Font("Microsoft YaHei UI", 9f * scale);

        btnSize = NavButton(IconLabel(), "图标大小 48 / 96 / 144 / 256  (Ctrl+滚轮 / icon 命令)",
                            delegate { CycleIconSize(); FocusList(); });
        btnSize.Width = (int)(78 * scale);
        btnSize.Font = new Font("Microsoft YaHei UI", 9f * scale);

        tools.Controls.Add(btnBack);
        tools.Controls.Add(btnFwd);
        tools.Controls.Add(btnUp);
        tools.Controls.Add(Sep());
        tools.Controls.Add(btnRefresh);
        tools.Controls.Add(btnView);
        tools.Controls.Add(btnSize);
    }

    string IconLabel() { return iconPx + "px"; }

    Button NavButton(string text, string tipText, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.AutoSize = false;
        b.Width = (int)(40 * scale);
        b.Height = (int)(28 * scale);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 46, 46);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(62, 62, 62);
        b.BackColor = Color.FromArgb(20, 20, 20);
        b.ForeColor = Color.FromArgb(225, 225, 225);
        b.Font = new Font("Segoe UI Symbol", 11f * scale);
        b.Margin = new Padding(0, (int)(4 * scale), (int)(2 * scale), 0);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        b.Click += onClick;
        tip.SetToolTip(b, tipText);
        return b;
    }

    Panel Sep()
    {
        var p = new Panel();
        p.Width = 1;
        p.Height = (int)(18 * scale);
        p.BackColor = Color.FromArgb(62, 62, 62);
        p.Margin = new Padding((int)(8 * scale), (int)(9 * scale), (int)(8 * scale), 0);
        return p;
    }

    void BuildCommandLine()
    {
        pnlCmd.Dock = DockStyle.Top;
        pnlCmd.Height = (int)(34 * scale);
        pnlCmd.BackColor = Color.FromArgb(18, 18, 18);
        pnlCmd.Padding = new Padding(8, 5, 8, 5);

        txtCmd.Dock = DockStyle.Fill;
        txtCmd.BorderStyle = BorderStyle.None;
        txtCmd.BackColor = Color.FromArgb(18, 18, 18);
        txtCmd.ForeColor = Color.FromArgb(235, 235, 235);
        txtCmd.Font = new Font("Consolas", 11f * scale);

        var lblPrompt = new Label();
        lblPrompt.Text = "❯";
        lblPrompt.Dock = DockStyle.Left;
        lblPrompt.Width = (int)(26 * scale);
        lblPrompt.TextAlign = ContentAlignment.MiddleLeft;
        lblPrompt.ForeColor = Color.FromArgb(90, 200, 240);
        lblPrompt.Font = new Font("Consolas", 12f * scale, FontStyle.Bold);
        lblPrompt.BackColor = Color.FromArgb(18, 18, 18);

        pnlCmd.Controls.Add(txtCmd);
        pnlCmd.Controls.Add(lblPrompt);
        txtCmd.BringToFront();
    }

    void BuildListing()
    {
        lv.Dock = DockStyle.Fill;
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.HideSelection = false;
        lv.MultiSelect = true;
        lv.BackColor = Color.FromArgb(30, 30, 30);
        lv.ForeColor = Color.FromArgb(235, 235, 235);
        lv.BorderStyle = BorderStyle.None;
        lv.Columns.Add(ColNames[0], 420);
        lv.Columns.Add(ColNames[1], 150);
        lv.Columns.Add(ColNames[2], 160);
        lv.Columns.Add(ColNames[3], 100, HorizontalAlignment.Right);
        lv.ShowItemToolTips = true;
        lv.OwnerDraw = false;
        lv.View = View.Details;
        UpdateHeaders();
    }

    void BuildPreview()
    {
        pic.Dock = DockStyle.Top;
        pic.Height = PreviewBoxHeight();
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.BackColor = Color.FromArgb(22, 22, 22);
        pic.BorderStyle = BorderStyle.None;

        lblPreview.Dock = DockStyle.Fill;
        lblPreview.BackColor = Color.FromArgb(26, 26, 26);
        lblPreview.ForeColor = Color.FromArgb(210, 210, 210);
        lblPreview.Font = new Font("Microsoft YaHei UI", 9.5f * scale);
        lblPreview.Padding = new Padding(10, 10, 10, 10);
        lblPreview.TextAlign = ContentAlignment.TopLeft;
        lblPreview.AutoEllipsis = true;
    }

    // 预览图区：吃满右栏高度（下方留出信息文本的位置），窗口/分隔条变大它就变大
    int PreviewBoxHeight()
    {
        int panelH = split.Panel2.ClientSize.Height;
        if (panelH < 80) panelH = (int)(420 * scale);
        int infoH = (int)(120 * scale);
        int h = panelH - infoH;
        int min = (int)(160 * scale);
        return h < min ? min : h;
    }

    void BuildMenu()
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuTable());
        menu.ShowImageMargin = false;
        menu.BackColor = Color.FromArgb(32, 32, 32);
        menu.ForeColor = Color.FromArgb(235, 235, 235);

        miOpen = MenuItem("打开", "Enter", delegate { OpenSelected(); });
        miOpenWith = MenuItem("用系统程序打开", null, delegate { OpenWithSystem(); });
        miTerm = MenuItem("在此处打开终端", null, delegate { OpenTerminal(); });
        miExplorer = MenuItem("在资源管理器中显示", null, delegate { RevealInExplorer(); });
        miCopy = MenuItem("复制路径", "Ctrl+Shift+C", delegate { CopyPaths(); });
        miRename = MenuItem("重命名", "F2", delegate { RenameSelected(); });
        miDelete = MenuItem("移到回收站", "Delete", delegate { DeleteSelected(false); });
        miPerma = MenuItem("永久删除", "Shift+Delete", delegate { DeleteSelected(true); });
        miProps = MenuItem("属性", "Alt+Enter", delegate { ShowProperties(); });

        menu.Items.Add(miOpen);
        menu.Items.Add(miOpenWith);
        menu.Items.Add(miTerm);
        menu.Items.Add(miExplorer);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miCopy);
        menu.Items.Add(miRename);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miDelete);
        menu.Items.Add(miPerma);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miProps);

        menu.Opening += delegate
        {
            int n = lv.SelectedItems.Count;
            var it = SelectedOne();
            miOpen.Enabled = n > 0;
            miOpen.Text = n > 1 ? "打开 (" + n + " 项)" : "打开";
            miOpenWith.Enabled = n > 0;
            miTerm.Enabled = n <= 1;
            miTerm.Text = it != null && it.IsFolder ? "在此处打开终端" : "在所在目录打开终端";
            miExplorer.Enabled = it != null && Xg.IsFs(it.Path);
            miCopy.Enabled = n > 0;
            miRename.Enabled = it != null && Xg.IsFs(it.Path);
            miDelete.Enabled = n > 0;
            miPerma.Enabled = n > 0;
            miProps.Enabled = it != null;
        };
    }

    ToolStripMenuItem MenuItem(string text, string keys, EventHandler h)
    {
        var m = new ToolStripMenuItem(text);
        m.ForeColor = Color.FromArgb(235, 235, 235);
        m.BackColor = Color.FromArgb(32, 32, 32);
        if (keys != null) m.ShortcutKeyDisplayString = keys;
        m.Click += h;
        return m;
    }

    void FocusList()
    {
        if (lv.Items.Count > 0) lv.Focus();
        else txtCmd.Focus();
    }

    // 焦点通常在命令行，滚轮本该滚列表，所以要自己转发（WM_MOUSEWHEEL 只发给焦点窗口）
    const int WM_MOUSEWHEEL = 0x020A;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        if (!lv.IsHandleCreated || m.HWnd == lv.Handle) return false;
        Rectangle r;
        try { r = lv.RectangleToScreen(lv.ClientRectangle); } catch { return false; }
        if (!r.Contains(Cursor.Position)) return false;
        try { Xg.SendMessage(lv.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam); }
        catch { return false; }
        return true;
    }

    // ---------------------------------------------------------------- path bar
    void UpdatePathBar()
    {
        crumb.SuspendLayout();
        while (crumb.Controls.Count > 0)
        {
            Control c = crumb.Controls[0];
            crumb.Controls.RemoveAt(0);
            c.Dispose();
        }

        if (rootsView)
        {
            crumb.Controls.Add(Crumb("命名空间根", null, true));
        }
        else if (curPath.Length == 0 || curPath.StartsWith("::") || curPath.StartsWith("shell:"))
        {
            var l = new Label();
            l.Text = curPath.Length == 0 ? "(未定位)" : curName + "    " + curPath;
            l.AutoSize = false;
            l.Height = crumb.Height > 10 ? crumb.Height : (int)(26 * scale);
            l.Width = Math.Max(200, crumb.Width - 4);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = Color.FromArgb(120, 220, 255);
            l.Font = crumbFont;
            l.BackColor = Color.Transparent;
            crumb.Controls.Add(l);
        }
        else
        {
            var segs = SplitPath(curPath);
            for (int i = 0; i < segs.Count; i++)
            {
                if (i > 0) crumb.Controls.Add(CrumbSeparator());
                crumb.Controls.Add(Crumb(segs[i].Key, segs[i].Value, i == segs.Count - 1));
            }
        }
        crumb.ResumeLayout();
    }

    static List<KeyValuePair<string, string>> SplitPath(string p)
    {
        var res = new List<KeyValuePair<string, string>>();
        if (p.StartsWith("\\\\"))
        {
            var parts = p.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { res.Add(new KeyValuePair<string, string>(p, p)); return res; }
            string acc = "\\\\" + parts[0];
            res.Add(new KeyValuePair<string, string>("\\\\" + parts[0], acc));
            if (parts.Length > 1)
            {
                acc += "\\" + parts[1];
                res.Add(new KeyValuePair<string, string>(parts[1], acc));
                for (int i = 2; i < parts.Length; i++)
                {
                    acc += "\\" + parts[i];
                    res.Add(new KeyValuePair<string, string>(parts[i], acc));
                }
            }
            return res;
        }
        var ps = p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (ps.Length == 0) { res.Add(new KeyValuePair<string, string>(p, p)); return res; }
        string curTarget = ps[0] + "\\";
        res.Add(new KeyValuePair<string, string>(ps[0] + "\\", curTarget));
        for (int i = 1; i < ps.Length; i++)
        {
            curTarget = curTarget.TrimEnd('\\') + "\\" + ps[i];
            res.Add(new KeyValuePair<string, string>(ps[i], curTarget));
        }
        return res;
    }

    LinkLabel Crumb(string text, string target, bool last)
    {
        var l = new LinkLabel();
        l.Text = text;
        l.AutoSize = false;
        int w = TextRenderer.MeasureText(text, crumbFont).Width + (int)(10 * scale);
        int h = (int)(24 * scale);
        l.Size = new Size(w, h);
        l.Margin = new Padding(0, Math.Max(0, ((int)(32 * scale) - h) / 2 - 1), 0, 0);
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.LinkBehavior = LinkBehavior.HoverUnderline;
        l.Font = crumbFont;
        l.BackColor = Color.Transparent;
        l.LinkColor = Color.FromArgb(120, 220, 255);
        l.ActiveLinkColor = Color.White;
        l.VisitedLinkColor = Color.FromArgb(120, 220, 255);
        if (last || target == null)
        {
            l.Enabled = false;
            l.DisabledLinkColor = Color.FromArgb(238, 238, 238);
        }
        else
        {
            l.LinkClicked += delegate { LoadFolder(target); };
        }
        return l;
    }

    Label CrumbSeparator()
    {
        var l = new Label();
        l.Text = "›";
        l.AutoSize = false;
        l.Size = new Size((int)(14 * scale), (int)(24 * scale));
        l.Margin = new Padding(0, Math.Max(0, ((int)(32 * scale) - l.Height) / 2 - 1), 0, 0);
        l.TextAlign = ContentAlignment.MiddleCenter;
        l.ForeColor = Color.FromArgb(120, 120, 120);
        l.BackColor = Color.Transparent;
        l.Font = crumbFont;
        return l;
    }

    // ---------------------------------------------------------------- listing
    void AttachImageLists()
    {
        if (!lv.IsHandleCreated) return;
        if (!details) return;
        IntPtr large = Xg.SystemImageList(Xg.SHGFI_LARGEICON);
        IntPtr small = Xg.SystemImageList(Xg.SHGFI_SMALLICON);
        Xg.SendMessage(lv.Handle, Xg.LVM_SETIMAGELIST, (IntPtr)Xg.LVSIL_NORMAL, large);
        Xg.SendMessage(lv.Handle, Xg.LVM_SETIMAGELIST, (IntPtr)Xg.LVSIL_SMALL, small);
    }

    void FitColumns()
    {
        if (!details) return;
        int w = lv.ClientSize.Width;
        if (w < 260) return;
        int dateW = (int)(185 * scale), typeW = (int)(190 * scale), sizeW = (int)(90 * scale);
        int nameW = w - dateW - typeW - sizeW - 24;
        if (nameW < 180) nameW = 180;
        lv.Columns[0].Width = nameW;
        lv.Columns[1].Width = dateW;
        lv.Columns[2].Width = typeW;
        lv.Columns[3].Width = sizeW;
    }

    void UpdateHeaders()
    {
        for (int i = 0; i < lv.Columns.Count && i < ColNames.Length; i++)
            lv.Columns[i].Text = ColNames[i] + (i == sortCol ? (sortAsc ? "  ▲" : "  ▼") : "");
    }

    void ApplySort(List<Xg.Item> list)
    {
        list.Sort(new Xg.ItemComparer(sortCol, sortAsc));
    }

    void ColumnClick(object sender, ColumnClickEventArgs e)
    {
        if (!details) return;
        if (e.Column == sortCol) sortAsc = !sortAsc;
        else { sortCol = e.Column; sortAsc = true; }
        UpdateHeaders();
        ResortRows();
        Msg("排序：" + ColNames[sortCol] + (sortAsc ? " 升序" : " 降序"));
    }

    void ResortRows()
    {
        ApplySort(items);
        RenderRows();
    }

    void LvMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = lv.GetItemAt(e.X, e.Y);
        if (hit != null && !hit.Selected)
        {
            lv.SelectedItems.Clear();
            hit.Selected = true;
            hit.Focused = true;
        }
    }

    // ---------------------------------------------------------------- folder
    void LoadFolder(string path)
    {
        dynamic f = null;
        try
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (path == "..") { try { f = cur.ParentFolder; } catch { f = null; } }
            else if (path == "-")
            {
                if (back.Count == 0) { Msg("没有历史"); return; }
                string p = back[back.Count - 1]; back.RemoveAt(back.Count - 1);
                f = Xg.shell.NameSpace(p);
            }
            else
            {
                bool abs = path.StartsWith("::") || path.Contains(":\\") || path.StartsWith("\\\\") ||
                           path.StartsWith("shell:") || (path.Length == 2 && path[1] == ':');
                if (abs) f = Xg.shell.NameSpace(path);
                if (f == null && Path.IsPathRooted(path)) f = Xg.shell.NameSpace(path);
                if (f == null)
                {
                    try
                    {
                        dynamic it = cur.ParseName(path);
                        if (it != null && (bool)it.IsFolder) f = Xg.shell.NameSpace((string)it.Path);
                    }
                    catch { }
                }
                if (f == null)
                {
                    try
                    {
                        foreach (var c in ReadAll())
                        {
                            if (!c.IsFolder) continue;
                            if (string.Equals(c.Name, path, StringComparison.CurrentCultureIgnoreCase))
                            { f = Xg.shell.NameSpace((string)c.Path); break; }
                        }
                    }
                    catch { }
                }
            }
            if (f == null) { Msg("没有这个位置: " + path); return; }

            string np = (string)f.Self.Path;
            if (curPath != "" && np != curPath)
            {
                if (!suppressHistory) { back.Add(curPath); forward.Clear(); }
            }
            string prevPath = curPath;
            cur = f;
            curPath = np;
            try { curName = (string)f.Self.Name; } catch { curName = np; }
            if (np != prevPath) History.Touch(np);   // 只记真正换了地方，F5 不刷次数
            Populate();
        }
        catch (Exception e) { Msg(e.Message); }
    }

    List<Xg.Item> ReadAll()
    {
        try { return Xg.ReadFolder(cur); }
        catch { cur = Xg.shell.NameSpace(curPath); return Xg.ReadFolder(cur); }
    }

    void Populate()
    {
        rootsView = false;
        items = ReadAll();
        ApplySort(items);
        Text = curName + " — xg";
        UpdatePathBar();
        ApplyView();
        if (lv.Items.Count > 0 && lv.SelectedItems.Count == 0) lv.Items[0].Selected = true;
    }

    void ApplyView()
    {
        if (details)
        {
            lv.View = View.Details;
            lv.LargeImageList = null;
            ClearBigImages();          // 明细视图不需要 96px 位图，别占着内存
            AttachImageLists();
        }
        else
        {
            lv.View = View.LargeIcon;
        }
        UpdateHeaders();
        RenderRows();
        FitColumns();
        btnView.Text = details ? "明细" : "图标";
        if (btnSize != null) btnSize.Text = IconLabel();
    }

    void RenderRows()
    {
        var selectedNames = new HashSet<string>();
        foreach (ListViewItem r in lv.SelectedItems) selectedNames.Add(r.Text);

        if (!details) BuildBigImages();

        lv.BeginUpdate();
        lv.Items.Clear();
        int idx = 0;
        foreach (var it in items)
        {
            var row = new ListViewItem(it.Name);
            row.SubItems.Add(it.Modified == DateTime.MinValue || it.Modified.Year < 1900 ? "" : it.Modified.ToString("yyyy-MM-dd HH:mm"));
            row.SubItems.Add(it.Type ?? "");
            row.SubItems.Add(it.IsFolder ? "" : Xg.Human(it.Size));
            row.ImageIndex = details ? it.IconSmall : idx;
            row.Tag = it;
            row.ToolTipText = it.Path ?? it.Name;
            lv.Items.Add(row);
            idx++;
        }
        lv.EndUpdate();

        if (selectedNames.Count > 0)
        {
            lv.BeginUpdate();
            foreach (ListViewItem r in lv.Items)
                if (selectedNames.Contains(r.Text)) r.Selected = true;
            lv.EndUpdate();
        }

        UpdateStatus();
        FitColumns();
        BeginInvoke((Action)delegate { FitColumns(); });
    }

    // ImageList 不复制位图：加进去的 Image 必须一直活着，直到列表被丢弃。
    void ClearBigImages()
    {
        if (lv.LargeImageList == ilBig) lv.LargeImageList = null;
        foreach (var im in bigImages) { try { im.Dispose(); } catch { } }
        bigImages.Clear();
        try { ilBig.Dispose(); } catch { }
        ilBig = new ImageList();
    }

    void BuildBigImages()
    {
        int px = (int)(iconPx * scale);
        if (px < 32) px = 32;
        if (px > 256) px = 256;

        ClearBigImages();
        ilBig.ColorDepth = ColorDepth.Depth32Bit;
        ilBig.ImageSize = new Size(px, px);

        // 超大图标很吃内存，缩略图数量随尺寸收缩
        int thumbCap = px >= 256 ? 120 : px >= 144 ? 300 : 400;

        int thumbs = 0;
        foreach (var it in items)
        {
            Image img = null;
            try
            {
                if (it.Path != null && !it.IsFolder && File.Exists(it.Path) && thumbs < thumbCap)
                {
                    Image raw = Xg.Thumbnail(it.Path, px * 2);
                    if (raw != null)
                    {
                        img = Xg.FitSquare(raw, px);
                        raw.Dispose();
                        thumbs++;
                    }
                }
                if (img == null) img = Xg.IconBitmap(it.Path, it.IsFolder, px);
            }
            catch { }
            if (img == null) img = new Bitmap(px, px);
            bigImages.Add(img);          // 由 bigImages 持有；不要在这里 Dispose
            ilBig.Images.Add(img);
        }

        if (!details)
        {
            lv.LargeImageList = ilBig;
            if (lv.IsHandleCreated)
            {
                // 间距别太肥，否则 256px 时右栏只排得下 2 列
                int sp = px + (int)(34 * scale);
                int packed = (sp & 0xFFFF) | ((sp & 0xFFFF) << 16);
                Xg.SendMessage(lv.Handle, Xg.LVM_SETICONSPACING, IntPtr.Zero, new IntPtr(packed));
            }
        }
    }

    static readonly string[] TextExt = { ".txt", ".md", ".cs", ".json", ".xml", ".yml", ".yaml",
        ".ini", ".log", ".ps1", ".cmd", ".bat", ".py", ".js", ".ts", ".html", ".css", ".csv", ".sql" };

    void DisposePreviewImage()
    {
        Image old = pic.Image;
        pic.Image = null;
        if (old != null) { try { old.Dispose(); } catch { } }
    }

    void RenderPreview()
    {
        DisposePreviewImage();
        pic.Visible = false;
        lblPreview.Text = "";

        if (lv.SelectedItems.Count != 1) return;
        var it = lv.SelectedItems[0].Tag as Xg.Item;
        if (it == null) return;

        string ext = (it.Path == null ? "" : Path.GetExtension(it.Path).ToLowerInvariant());
        var lines = new List<string>();
        lines.Add(it.Name);
        if (it.Path != null) lines.Add(it.Path);
        lines.Add((it.Type ?? "") + (it.IsFolder ? "   目录" : "   " + Xg.Human(it.Size)));
        if (it.Modified != DateTime.MinValue) lines.Add(it.Modified.ToString("yyyy-MM-dd HH:mm:ss"));

        if (it.IsFolder)
        {
            try
            {
                int n = 0;
                bool more = false;
                foreach (var c in Xg.ReadFolder(Xg.shell.NameSpace(it.Path)))
                {
                    n++;
                    if (n >= 2000) { more = true; break; }
                }
                lines.Add(more ? "2000+ 项" : n + " 项");
            }
            catch { }
            lblPreview.Text = string.Join("\n", lines.ToArray());
            return;
        }

        // 1) 图片 / 视频 / PDF：走系统缩略图，尺寸跟着预览栏走（相片想看多大就拉多大）
        bool isText = TextExt.Contains(ext);
        Image img = null;
        if (!isText && it.Path != null && File.Exists(it.Path))
        {
            int h = PreviewBoxHeight();
            int w = split.Panel2.ClientSize.Width - 4;
            int box = Math.Min(w, h);
            if (box < 120) box = 120;
            if (box > 1024) box = 1024;
            pic.Height = h;
            img = Xg.Thumbnail(it.Path, box);
        }
        if (img != null)
        {
            pic.Image = img;
            pic.Visible = true;
        }

        // 2) 文本：读前若干行
        if (img == null && it.Path != null && isText)
        {
            try
            {
                var sb = new StringBuilder();
                using (var sr = new StreamReader(it.Path, Encoding.UTF8, true))
                {
                    for (int i = 0; i < 120 && !sr.EndOfStream; i++)
                    {
                        string line = sr.ReadLine();
                        if (line == null) break;
                        if (line.Length > 400) line = line.Substring(0, 400) + " …";
                        sb.AppendLine(line);
                    }
                }
                lines.Add("");
                lines.Add("────");
                lines.Add(sb.ToString());
            }
            catch { }
        }

        lblPreview.Text = string.Join("\n", lines.ToArray());
    }

    void UpdateStatus()
    {
        int n = lv.Items.Count;
        int sel = lv.SelectedItems.Count;
        string s = n + " 项   ·   " + ColNames[sortCol] + (sortAsc ? " ↑" : " ↓");
        if (sel == 1)
        {
            var it = lv.SelectedItems[0].Tag as Xg.Item;
            if (it != null) s += "   ·   " + it.Name + (it.IsFolder ? "  <目录>" : "   " + Xg.Human(it.Size));
        }
        else if (sel > 1) s += "   ·   已选 " + sel + " 项";
        lblStatus.Text = s;
    }

    void Msg(string s) { lblStatus.Text = "  " + s; }

    // ---------------------------------------------------------------- selection helpers
    List<Xg.Item> Selected()
    {
        var list = new List<Xg.Item>();
        foreach (ListViewItem row in lv.SelectedItems)
        {
            var it = row.Tag as Xg.Item;
            if (it != null) list.Add(it);
        }
        return list;
    }

    Xg.Item SelectedOne()
    {
        if (lv.SelectedItems.Count != 1) return null;
        return lv.SelectedItems[0].Tag as Xg.Item;
    }

    // ---------------------------------------------------------------- actions
    void DeleteSelected(bool permanent)
    {
        var sel = Selected();
        if (sel.Count == 0) { Msg("没有选中任何项"); return; }

        var names = new List<string>();
        foreach (var it in sel.Take(4)) names.Add(it.Name);
        string head = sel.Count == 1
            ? (permanent ? "永久删除这一项？" : "删除这一项？")
            : (permanent ? "永久删除选中的 " + sel.Count + " 项？" : "删除选中的 " + sel.Count + " 项？");
        if (permanent) head += "\n\n不会进回收站，删掉就没了。";
        if (MessageBox.Show(head + "\n\n" + string.Join("\n", names.ToArray()) + (sel.Count > 4 ? "\n…" : ""),
                permanent ? "永久删除" : "移到回收站", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                permanent ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1)
            != DialogResult.Yes) return;

        int ok = 0, fail = 0;
        var errs = new List<string>();
        foreach (var it in sel)
        {
            try
            {
                if (Xg.IsFs(it.Path))
                {
                    if (permanent)
                    {
                        if (it.IsFolder)
                        {
                            if (Directory.Exists(it.Path)) Directory.Delete(it.Path, true);
                        }
                        else if (File.Exists(it.Path)) File.Delete(it.Path);
                    }
                    else
                    {
                        if (it.IsFolder)
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(it.Path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                else { it.Com.InvokeVerb("delete"); }
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                if (errs.Count < 4) errs.Add(it.Name + " —— " + ex.Message);
            }
        }
        Populate();
        Msg((permanent ? "已永久删除: " : "已移到回收站: ") + ok + (fail > 0 ? "，失败 " + fail : ""));

        // 失败要说清楚原因，不要静默
        if (fail > 0)
        {
            MessageBox.Show("有 " + fail + " 项没能删除：\n\n" + string.Join("\n", errs.ToArray()) +
                            (fail > errs.Count ? "\n…" : ""),
                            "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OpenSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Count == 1) { Open(sel[0]); return; }

        foreach (var it in sel)
            if (!it.IsFolder && it.Path != null) { try { Process.Start(it.Path); } catch { } }
        var f = sel.FirstOrDefault(i => i.IsFolder);
        if (f != null) LoadFolder(f.Path);
    }

    void Open(Xg.Item it)
    {
        if (it.IsFolder) { LoadFolder(it.Path); return; }
        if (Viewer.IsImage(it.Path)) { OpenViewer(it); return; }
        try { Process.Start(it.Path); Msg("已打开 " + it.Name); }
        catch (Exception e) { Msg(e.Message); }
    }

    // 图片用内置看图窗口打开：←/→ 能在同目录的图片之间切换（系统"照片"单开一张时切不了）
    void OpenViewer(Xg.Item it)
    {
        var paths = new List<string>();
        int at = -1;
        foreach (var x in items)
        {
            if (x == null || x.IsFolder || x.Path == null) continue;
            if (!Viewer.IsImage(x.Path)) continue;
            if (string.Equals(x.Path, it.Path, StringComparison.CurrentCultureIgnoreCase)) at = paths.Count;
            paths.Add(x.Path);
        }
        if (paths.Count == 0) { Process.Start(it.Path); return; }
        if (at < 0) { paths.Insert(0, it.Path); at = 0; }
        try
        {
            using (var v = new Viewer(paths, at))
                v.ShowDialog(this);
        }
        catch (Exception ex) { Msg(ex.Message); }
        txtCmd.Focus();
    }

    // 绕过内置看图，交给系统默认程序（比如"照片"）
    void OpenWithSystem()
    {
        var sel = Selected();
        if (sel.Count == 0) { Msg("没有选中"); return; }
        int n = 0;
        foreach (var it in sel)
        {
            if (it.IsFolder || it.Path == null) continue;
            try { Process.Start(it.Path); n++; } catch { }
        }
        Msg(n > 0 ? "已交给系统程序打开 " + n + " 项" : "没有可打开的文件");
    }

    void OpenTerminal()
    {
        var it = SelectedOne();
        string dir = curPath;
        if (it != null)
        {
            if (it.IsFolder) dir = it.Path;
            else if (it.Path != null) dir = Path.GetDirectoryName(it.Path);
        }
        LaunchTerminal(dir, null);
    }

    // wt [目录|文件] [在里面跑的命令…]
    //   不给参数 → 就在 xg 的当前目录开一个 Windows Terminal
    //   给目录   → 在那个目录开；给文件 → 在文件所在目录开；给显示名 → 先按当前列表里的项找
    //   后面还能跟命令：wt . bash / wt D:\项目 pwsh / wt .. cmd
    void OpenWt(string a1, List<string> rest)
    {
        string dir = curPath;

        if (!string.IsNullOrEmpty(a1))
        {
            string p = FsPath(a1);
            if (Directory.Exists(p)) dir = p;
            else if (File.Exists(p)) dir = Path.GetDirectoryName(p);
            else
            {
                var it = FindItem(a1);
                if (it != null && Xg.IsFs(it.Path))
                    dir = it.IsFolder ? it.Path : Path.GetDirectoryName(it.Path);
                else { Msg("没有这个目录: " + a1); return; }
            }
        }

        LaunchTerminal(dir, rest);
    }

    // 在 Windows Terminal 里打开 dir；没有 wt.exe 就回退 cmd /k cd /d（和右键菜单同一套）
    void LaunchTerminal(string dir, List<string> run)
    {
        if (!Xg.IsFs(dir)) { Msg("当前不是文件系统位置"); return; }

        var line = new StringBuilder("-d ");
        line.Append(QArg(dir));
        bool withCmd = run != null && run.Count > 0;
        if (withCmd) foreach (var r in run) line.Append(' ').Append(QArg(r));

        // 自检/无窗口验证：XG_WT_DRY 非空就只记一行、真不启终端（和 XG_HISTORY 一个路数）
        if (Environment.GetEnvironmentVariable("XG_WT_DRY") != null)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "xg-wt.txt"),
                    line.ToString() + "   @ " + dir + "\r\n", Encoding.UTF8);
            }
            catch { }
            Msg("wt(dry) · " + dir);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("wt.exe", line.ToString());
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            Msg("wt · " + dir + (withCmd ? " · " + string.Join(" ", run.ToArray()) : ""));
        }
        catch
        {
            try { Process.Start("cmd.exe", "/k cd /d " + QArg(dir)); }
            catch (Exception e) { Msg(e.Message); }
        }
    }

    // 拼命令行用：路径里有空格/中文/& 都不散架
    static string QArg(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

    // ex [目录|文件] [-s]   —— 和 wt 同一套目标解析，只不过开出来的是资源管理器
    //   不给参数 → xg 的当前目录
    //   给目录   → 在那个目录开
    //   给文件   → 在文件所在目录开；加 -s 就直接选中那个文件
    //   给显示名 → 先按当前列表里的项找（"OS (C:)" 这种也认）
    void OpenExplorerAt(string a1, bool select)
    {
        string dir = curPath;
        string pick = null;

        if (!string.IsNullOrEmpty(a1))
        {
            string p = FsPath(a1);
            if (Directory.Exists(p)) dir = p;
            else if (File.Exists(p)) { dir = Path.GetDirectoryName(p); pick = p; }
            else
            {
                var it = FindItem(a1);
                if (it != null && Xg.IsFs(it.Path))
                {
                    if (it.IsFolder) dir = it.Path;
                    else { dir = Path.GetDirectoryName(it.Path); pick = it.Path; }
                }
                else { Msg("没有这个目录: " + a1); return; }
            }
        }

        LaunchExplorer(dir, select ? pick : null);
    }

    // 在资源管理器里打开 dir；select 非空就 /select, 定位到它（文件、文件夹都行）
    void LaunchExplorer(string dir, string select)
    {
        if (dir == null) dir = curPath;
        if (!Xg.IsFs(dir)) { Msg("当前不是文件系统位置"); return; }

        // 光给目录：explorer.exe 直接开那个目录；给 /select,：它自己会开父目录并选中
        var line = new StringBuilder();
        if (select != null) line.Append("/select,").Append(QArg(select));
        else line.Append(QArg(dir));

        // 自检/无窗口验证：XG_EX_DRY 非空就只记一行、真不启资源管理器（和 XG_WT_DRY 一个路数）
        if (Environment.GetEnvironmentVariable("XG_EX_DRY") != null)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "xg-ex.txt"),
                    line.ToString() + "   @ " + dir + "\r\n", Encoding.UTF8);
            }
            catch { }
            Msg("ex(dry) · " + dir);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("explorer.exe", line.ToString());
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            Msg("ex · " + dir + (select != null ? " · 选中 " + Path.GetFileName(select) : ""));
        }
        catch (Exception e) { Msg(e.Message); }
    }

    void RevealInExplorer()
    {
        var it = SelectedOne();
        if (it == null || !Xg.IsFs(it.Path)) { Msg("只有文件系统对象能在资源管理器里显示"); return; }
        // 和 ex 共用一套启动：定位到该项本身；盘根没有父目录，就拿它自己当工作目录
        string parent = Path.GetDirectoryName(it.Path);
        LaunchExplorer(parent == null ? it.Path : parent, it.Path);
    }

    void ShowProperties()
    {
        var it = SelectedOne();
        if (it == null) return;
        if (!Xg.IsFs(it.Path))
        {
            try { it.Com.InvokeVerb("properties"); } catch (Exception e) { Msg(e.Message); }
            return;
        }
        var sei = new Xg.SHELLEXECUTEINFO();
        sei.cbSize = Marshal.SizeOf(typeof(Xg.SHELLEXECUTEINFO));
        sei.fMask = Xg.SEE_MASK_INVOKEIDLIST;
        sei.hwnd = Handle;
        sei.lpVerb = "properties";
        sei.lpFile = it.Path;
        sei.nShow = 1;
        if (!Xg.ShellExecuteEx(ref sei)) Msg("无法打开属性对话框");
    }

    void LvKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { DeleteSelected(e.Shift); e.Handled = true; return; }
        if (e.KeyCode == Keys.Enter)
        {
            if (e.Alt) { ShowProperties(); e.Handled = true; return; }
            OpenSelected(); e.Handled = true; return;
        }

        // Explorer 的键位：Backspace / Alt+← 后退，Alt+→ 前进，Alt+↑ 上一级
        if (e.KeyCode == Keys.Back && e.Modifiers == Keys.None) { GoBack(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Left && e.Modifiers == Keys.Alt) { GoBack(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Right && e.Modifiers == Keys.Alt) { GoForward(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Up && e.Modifiers == Keys.Alt) { GoUp(); e.Handled = true; return; }

        if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; return; }
        if (e.KeyCode == Keys.F5) { Populate(); e.Handled = true; return; }
        if (e.KeyCode == Keys.F4) { ToggleView(); e.Handled = true; return; }
        if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem r in lv.Items) r.Selected = true; e.Handled = true; return; }
        if (e.KeyCode == Keys.C && e.Control && e.Shift) { CopyPaths(); e.Handled = true; return; }

        // 在列表里直接敲字：把焦点交给命令行并把字符接上去
        if ((e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z && e.Modifiers == Keys.None) ||
            (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9 && e.Modifiers == Keys.None) ||
            (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9 && e.Modifiers == Keys.None))
        {
            txtCmd.Focus();
            string ch;
            if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) ch = ((char)('a' + (e.KeyCode - Keys.A))).ToString();
            else if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) ch = ((char)('0' + (e.KeyCode - Keys.D0))).ToString();
            else ch = ((char)('0' + (e.KeyCode - Keys.NumPad0))).ToString();
            txtCmd.Text += ch;
            txtCmd.SelectionStart = txtCmd.TextLength;
            e.Handled = true;
        }
    }

    void GoBack()
    {
        if (back.Count == 0) { Msg("没有可后退的历史"); return; }
        string p = back[back.Count - 1];
        back.RemoveAt(back.Count - 1);
        forward.Add(curPath);
        suppressHistory = true;
        LoadFolder(p);
        suppressHistory = false;
    }

    void GoForward()
    {
        if (forward.Count == 0) { Msg("没有可前进的历史"); return; }
        string p = forward[forward.Count - 1];
        forward.RemoveAt(forward.Count - 1);
        back.Add(curPath);
        suppressHistory = true;
        LoadFolder(p);
        suppressHistory = false;
    }

    void GoUp()
    {
        // 文件系统路径自己算父目录（比 Shell 的 ParentFolder 稳，盘根也能正确落到"此电脑"）
        string parent = null;
        try
        {
            if (Xg.IsFs(curPath))
            {
                string t = curPath.TrimEnd('\\');
                if (t.Length > 2) parent = Path.GetDirectoryName(t);
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) { LoadFolder(parent); return; }
        if (rootsView) { Msg("已经是最上层"); return; }
        // 盘根 / 虚拟命名空间 → 此电脑
        LoadFolder("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
    }

    void RenameSelected()
    {
        if (lv.SelectedItems.Count != 1) { Msg("重命名只支持单个对象"); return; }
        var it = SelectedOne();
        if (it == null) return;
        if (!Xg.IsFs(it.Path)) { Msg("虚拟命名空间的项不能重命名"); return; }

        string nn = Microsoft.VisualBasic.Interaction.InputBox("新名称：", "重命名", it.Name);
        if (string.IsNullOrEmpty(nn) || nn == it.Name) return;
        try
        {
            string dir = Path.GetDirectoryName(it.Path);
            string dst = Path.Combine(dir, nn);
            if (it.IsFolder) Directory.Move(it.Path, dst); else File.Move(it.Path, dst);
            Populate();
            Msg("已重命名为 " + nn);
        }
        catch (Exception ex) { Msg(ex.Message); }
    }

    void CopyPaths()
    {
        var sel = Selected();
        if (sel.Count == 0) { Msg("没有选中"); return; }
        var paths = new List<string>();
        foreach (var it in sel) if (it.Path != null) paths.Add(it.Path);
        if (paths.Count == 0) { Msg("选中项没有路径"); return; }
        try { Clipboard.SetText(string.Join("\r\n", paths.ToArray())); Msg("已复制 " + paths.Count + " 条路径"); }
        catch (Exception ex) { Msg(ex.Message); }
    }

    void ToggleView()
    {
        details = !details;
        ApplyView();
        if (lv.Items.Count > 0)
        {
            if (lv.SelectedItems.Count == 0) lv.Items[0].Selected = true;
            lv.Focus();
        }
        Msg(details ? "明细视图（F4 切换）" : "图标视图 · 真缩略图（F4 切换，Ctrl+滚轮 缩放）");
    }

    // ---------------------------------------------------------------- 图标尺寸（看大图 / 相片）
    void CycleIconSize()
    {
        int idx = Array.IndexOf(IconSteps, iconPx);
        if (idx < 0) idx = 1;
        SetIconSize(IconSteps[(idx + 1) % IconSteps.Length]);
    }

    void SetIconSize(int px)
    {
        if (px < 32) px = 32;
        if (px > 256) px = 256;
        bool changed = px != iconPx || details;   // 在明细视图里点尺寸 → 直接切到图标视图
        iconPx = px;
        if (btnSize != null) btnSize.Text = IconLabel();
        if (changed)
        {
            details = false;
            ApplyView();
        }
        Msg("图标大小 " + iconPx + "px   （Ctrl+滚轮 或 icon " + string.Join("|", Array.ConvertAll(IconSteps, i => i.ToString())) + "）");
    }

    bool ZoomIcons(int dir)
    {
        if (details) { Msg("图标视图里才能缩放（F4 切过去）"); return true; }
        int idx = Array.IndexOf(IconSteps, iconPx);
        if (idx < 0) idx = 1;
        int next = Math.Max(0, Math.Min(IconSteps.Length - 1, idx + dir));
        if (next == idx) { Msg("已经是最" + (dir > 0 ? "大" : "小") + "档：" + iconPx + "px"); return true; }
        SetIconSize(IconSteps[next]);
        return true;
    }

    void FitPreviewBox()
    {
        if (!pic.Visible) return;
        int h = PreviewBoxHeight();
        if (Math.Abs(pic.Height - h) > 2) pic.Height = h;
    }

    // ---------------------------------------------------------------- command line
    void CmdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            string line = txtCmd.Text.Trim();
            txtCmd.Clear();
            if (line.Length == 0) { OpenSelected(); e.Handled = true; return; }
            hist.Add(line); hIdx = hist.Count;
            Run(line);
            txtCmd.Focus();
            e.Handled = true; e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Tab) { Complete(e.Shift); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Escape) { txtCmd.Clear(); e.Handled = true; return; }

        // Alt 组合：导航键在命令行里也要能用（焦点默认就在命令行）
        if (e.Alt && e.KeyCode == Keys.Up) { GoUp(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Alt && e.KeyCode == Keys.Left) { GoBack(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Alt && e.KeyCode == Keys.Right) { GoForward(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Alt && e.KeyCode == Keys.Enter) { ShowProperties(); e.Handled = true; e.SuppressKeyPress = true; return; }

        // 命令行空着的时候，Delete / F2 / F4 / F5 直接作用在列表选中项上
        if (e.KeyCode == Keys.Delete && txtCmd.TextLength == 0) { DeleteSelected(e.Shift); e.Handled = true; return; }
        if (e.KeyCode == Keys.F2 && txtCmd.TextLength == 0) { RenameSelected(); e.Handled = true; return; }
        if (e.KeyCode == Keys.F4) { ToggleView(); e.Handled = true; return; }
        if (e.KeyCode == Keys.F5) { Populate(); e.Handled = true; return; }

        // 翻页键也转给列表（焦点默认在命令行，否则 35 项只能看到头几行）
        if ((e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp) && txtCmd.TextLength == 0)
        {
            Xg.SendMessage(lv.Handle, 0x0115 /*WM_VSCROLL*/,
                           (IntPtr)(e.KeyCode == Keys.PageDown ? 3 : 2), IntPtr.Zero);
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            if (txtCmd.TextLength == 0 && lv.Items.Count > 0)
            {
                int i = lv.FocusedItem == null ? 0 : lv.FocusedItem.Index;
                SelectOnly(i > 0 ? i - 1 : 0);
            }
            else if (hIdx > 0) { hIdx--; txtCmd.Text = hist[hIdx]; txtCmd.SelectionStart = txtCmd.TextLength; }
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Down)
        {
            if (txtCmd.TextLength == 0 && lv.Items.Count > 0)
            {
                int i = lv.FocusedItem == null ? -1 : lv.FocusedItem.Index;
                SelectOnly(i < lv.Items.Count - 1 ? i + 1 : lv.Items.Count - 1);
            }
            else if (hIdx < hist.Count - 1) { hIdx++; txtCmd.Text = hist[hIdx]; txtCmd.SelectionStart = txtCmd.TextLength; }
            e.Handled = true;
            return;
        }
    }

    // 上下键在列表里挪动：必须单选。MultiSelect=true 时 .Selected=true 是"加上"，
    // 不先清空的话按几次就变成全选了（Enter 会打开一堆、Delete 会删一堆）。
    void SelectOnly(int index)
    {
        if (index < 0 || index >= lv.Items.Count) return;
        lv.SelectedItems.Clear();
        lv.Items[index].Selected = true;
        lv.Items[index].Focused = true;
        lv.EnsureVisible(index);
    }

    void Complete(bool backwards)
    {
        string text = txtCmd.Text;
        int caret = txtCmd.SelectionStart;
        if (caret > text.Length) caret = text.Length;
        int start = TokenStart(text, caret);
        string head = text.Substring(0, start);
        string token = text.Substring(start, caret - start);
        string tail = text.Substring(caret);
        bool firstWord = head.Trim().Length == 0;

        // 还在上一轮的候选里？token 以 \ 结尾说明要往下一层走，重新算
        if (compCands.Count > 0 && compHead == head && !token.EndsWith("\\"))
        {
            string cur = Unquote(token);
            int at = compCands.FindIndex(delegate(Cand c)
            {
                return string.Equals(c.Text, cur, StringComparison.CurrentCultureIgnoreCase);
            });
            if (at >= 0)
            {
                int next = backwards ? (at - 1 + compCands.Count) % compCands.Count : (at + 1) % compCands.Count;
                ShowCandidate(head, tail, next);
                return;
            }
        }

        compQuote = token.StartsWith("\"");
        var cands = Candidates(Unquote(token), firstWord);
        if (cands.Count == 0)
        {
            compCands.Clear(); compIdx = -1; compHead = null;
            Msg("没有可补全的: " + token);
            return;
        }

        compCands.Clear();
        compCands.AddRange(cands);
        compHead = head;
        compIdx = -1;

        if (cands.Count == 1)
        {
            var c = cands[0];
            string ins = c.Text;
            if (c.IsCommand) ins += " ";
            else if (c.IsDir) ins += "\\";
            // 引号只看名字本身：尾随的空格 / 反斜杠不该触发引号
            bool q = compQuote || NeedsQuote(c.Text);
            ReplaceToken(head, q ? "\"" + ins + "\"" : ins, tail);
            compCands.Clear(); compIdx = -1; compHead = null;
            Msg("补全: " + ins);
            return;
        }

        ShowCandidate(head, tail, backwards ? cands.Count - 1 : 0);
    }

    // ------------------------------------------------ Tab 补全（向 cmd / PowerShell 对齐）
    // 唯一匹配 → 直接插入：命令补空格、目录补 \、文件不补
    // 多个匹配 → 插入当前候选；再按 Tab 循环，Shift+Tab 反向
    // 支持：首词补命令名、路径补子目录、绝对路径、当前列表显示名、带空格的名字加引号
    class Cand
    {
        public string Text;
        public bool IsDir;
        public bool IsCommand;
    }

    static readonly string[] CmdNames = { "ls", "ll", "ic", "icons", "cd", "pwd", "up", "back",
                                          "mkdir", "md", "rm", "del", "rmdir", "mv", "cp", "cat",
                                          "touch", "trash", "hist", "sort", "sel", "icon", "big", "open",
                                          "wt", "term", "terminal", "ex", "explorer",
                                          "info", "roots", "clear", "help", "exit" };
    readonly List<Cand> compCands = new List<Cand>();
    int compIdx = -1;
    string compHead = null;
    bool compBusy;
    bool compQuote;

    static bool StartsWith(string s, string prefix)
    {
        return s.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase);
    }

    static int TokenStart(string text, int caret)
    {
        bool q = false;
        int last = -1;
        for (int i = 0; i < caret && i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { q = !q; continue; }
            if (!q && (c == ' ' || c == '\t')) last = i;
        }
        return last + 1;
    }

    static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
        if (s.Length >= 1 && s[0] == '"') return s.Substring(1);
        if (s.Length >= 1 && s[s.Length - 1] == '"') return s.Substring(0, s.Length - 1);
        return s;
    }

    static bool NeedsQuote(string s) { return s.IndexOf(' ') >= 0 || s.IndexOf('\t') >= 0; }

    static string Quote(string s) { return NeedsQuote(s) ? "\"" + s + "\"" : s; }

    List<Cand> Candidates(string token, bool firstWord)
    {
        var res = new List<Cand>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        Action<Cand> add = delegate(Cand c)
        {
            if (c.Text == null || c.Text.Length == 0) return;
            if (seen.Add(c.Text)) res.Add(c);
        };

        if (firstWord)
            foreach (var c in CmdNames)
                if (StartsWith(c, token)) add(new Cand { Text = c, IsCommand = true });

        string dirPart = "";
        string leaf = token;
        int sep = token.LastIndexOfAny(new[] { '\\', '/' });
        if (sep >= 0) { dirPart = token.Substring(0, sep + 1); leaf = token.Substring(sep + 1); }

        // 1) 文件系统：支持相对子目录与绝对路径
        string baseDir = null;
        try
        {
            if (sep >= 0 && Path.IsPathRooted(dirPart)) baseDir = dirPart;
            else if (Xg.IsFs(curPath)) baseDir = dirPart.Length == 0 ? curPath : Path.Combine(curPath, dirPart);
        }
        catch { baseDir = null; }

        if (baseDir != null)
        {
            try
            {
                if (Directory.Exists(baseDir))
                {
                    foreach (var e in Directory.GetFileSystemEntries(baseDir))
                    {
                        string n = Path.GetFileName(e);
                        if (n.Length == 0) continue;
                        if (leaf.Length > 0 && !StartsWith(n, leaf)) continue;
                        add(new Cand { Text = dirPart + n, IsDir = Directory.Exists(e) });
                    }
                }
            }
            catch { }
        }

        // 2) 当前列表里的显示名：虚拟命名空间 / 驱动器显示名（"OS (C:)"）之类
        if (dirPart.Length == 0 && !(firstWord && token.Length == 0))
        {
            foreach (var it in items)
            {
                if (leaf.Length > 0 && !StartsWith(it.Name, leaf)) continue;
                add(new Cand { Text = it.Name, IsDir = it.IsFolder });
            }
        }

        // 命令在前，目录其次，文件最后；同组按名字
        res.Sort(delegate(Cand a, Cand b)
        {
            int ka = a.IsCommand ? 0 : a.IsDir ? 1 : 2;
            int kb = b.IsCommand ? 0 : b.IsDir ? 1 : 2;
            if (ka != kb) return ka - kb;
            return string.Compare(a.Text, b.Text, StringComparison.CurrentCultureIgnoreCase);
        });
        return res;
    }

    void ReplaceToken(string head, string ins, string tail)
    {
        compBusy = true;
        txtCmd.Text = head + ins + tail;
        txtCmd.SelectionStart = (head + ins).Length;
        compBusy = false;
    }

    string ShowText(Cand c)
    {
        return compQuote ? "\"" + c.Text + "\"" : Quote(c.Text);
    }

    void ShowCandidate(string head, string tail, int idx)
    {
        compIdx = idx;
        var c = compCands[idx];
        ReplaceToken(head, ShowText(c), tail);
        compHead = head;

        var sb = new StringBuilder();
        sb.Append("补全 " + (idx + 1) + "/" + compCands.Count + "   ");
        int from = Math.Max(0, idx - 3);
        int to = Math.Min(compCands.Count, from + 9);
        for (int i = from; i < to; i++)
        {
            string name = compCands[i].Text + (compCands[i].IsDir ? "\\" : "");
            sb.Append(i == idx ? "[" + name + "]  " : name + "  ");
        }
        if (to < compCands.Count) sb.Append("…");
        Msg(sb.ToString());
    }

    List<string> Tok(string s)
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

    internal void Run(string line)
    {
        var t = Tok(line);
        string v = t[0].ToLowerInvariant();

        // Linux 风格：-l -t -S -r -f -p 按字符拆；单个 "-" 仍是参数（cd -）
        var flags = new HashSet<char>();
        var args = new List<string>();
        for (int i = 1; i < t.Count; i++)
        {
            string a = t[i];
            if (a.Length > 1 && a[0] == '-')
                for (int k = 1; k < a.Length; k++) flags.Add(a[k]);
            else
                args.Add(a);
        }
        string a1 = args.Count > 0 ? args[0] : null;

        switch (v)
        {
            case "ls": case "dir": case "ll": LsCmd(flags, args, v == "ll"); break;
            case "ic": case "icons": details = false; ApplyView(); Msg("图标视图 · 真缩略图（Ctrl+滚轮 缩放）"); break;
            case "cd": CdCmd(a1); break;
            case "pwd": Msg(curPath); break;
            case "up": GoUp(); break;
            case "back": GoBack(); break;
            case "mkdir": case "md": MkDirCmd(flags, args); break;
            case "rm": case "del": case "erase": RmCmd(flags, args); break;
            case "rmdir": case "rd": RmDirCmd(args); break;
            case "mv": case "ren": case "move": MvCmd(args); break;
            case "cp": case "copy": CpCmd(flags, args); break;
            case "cat": case "type": CatCmd(a1); break;
            case "touch": TouchCmd(a1); break;
            case "trash": TrashCmd(args); break;
            case "sort": SortCmd(t); break;
            case "sel": SelCmd(a1); break;
            case "icon": case "size": IconCmd(args); break;
            case "big": CycleIconSize(); break;
            case "open": OpenNamed(a1); break;
            case "wt": case "term": case "terminal":
                OpenWt(a1, args.Count > 1 ? args.GetRange(1, args.Count - 1) : null);
                break;
            case "ex": case "explorer": case "exp":
                OpenExplorerAt(a1, flags.Contains('s'));
                break;
            case "info": Info(a1); break;
            case "roots": Roots(); break;
            case "hist": case "history": HistCmd(flags, args); break;
            case "clear": case "cls": hist.Clear(); hIdx = 0; lblStatus.Text = ""; break;
            case "help": case "?":
                Msg("ls [-l|-t|-S|-r] [目录] · cd [-|~|目录] · pwd · mkdir [-p] · rm [-r] [-f] · rmdir · mv · cp [-r] · cat · touch · trash · hist [-t|-c|-x] [关键字] · sel · sort · icon · big · open · wt [目录|文件] [命令…] · ex [-s] [目录|文件] · info · roots · clear · exit");
                break;
            case "exit": case "q": Close(); break;
            default: Msg("未知命令: " + v + "（help 看列表）"); break;
        }
    }

    // ---------------------------------------------------------------- Linux 风格命令
    static string HomeDir() { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }

    // 用户输入 → 文件系统绝对路径：~ 展开、/ 当 \ 用、相对路径挂到当前目录
    string FsPath(string p)
    {
        if (p == null) return null;
        if (p == "~") return HomeDir();
        if (p.StartsWith("~/") || p.StartsWith("~\\")) p = Path.Combine(HomeDir(), p.Substring(2));
        string q = p.Replace('/', '\\');
        try
        {
            if (Path.IsPathRooted(q)) return Path.GetFullPath(q);
            if (Xg.IsFs(curPath)) return Path.GetFullPath(Path.Combine(curPath, q));
        }
        catch { }
        return q;
    }

    // 不认识的裸名字（"OS (C:)"）要继续走显示名匹配，别急着当路径
    static bool LooksLikePath(string p)
    {
        if (p == null) return false;
        if (p == "." || p == ".." || p == "~") return true;
        if (p.IndexOf('\\') >= 0 || p.IndexOf('/') >= 0) return true;
        if (p.Length >= 2 && p[1] == ':') return true;
        return p.StartsWith("~");
    }

    Xg.Item FindItem(string name)
    {
        if (name == null) return null;
        foreach (var it in items)
        {
            if (it == null) continue;
            if (string.Equals(it.Name, name, StringComparison.CurrentCultureIgnoreCase)) return it;
            if (it.Path != null && string.Equals(it.Path, name, StringComparison.CurrentCultureIgnoreCase)) return it;
        }
        return null;
    }

    // hist：无参弹查询窗；带关键字直接跳到最匹配的一个；-t 最近 / -c 次数 / -x 清空
    void HistCmd(HashSet<char> f, List<string> args)
    {
        string sort = f.Contains('c') ? "count" : f.Contains('t') ? "time" : "score";

        if (f.Contains('x'))
        {
            if (MessageBox.Show("清空文件夹历史？（共 " + History.Count + " 条）", "hist -x",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            History.Clear();
            Msg("历史已清空");
            return;
        }

        if (args.Count > 0)
        {
            string q = args[0];
            var hit = History.Snapshot(q, sort);
            if (hit.Count == 0) { Msg("历史里没有匹配: " + q); return; }
            LoadFolder(hit[0].Path);
            Msg("hist: " + hit[0].Path + "   （去过 " + hit[0].Count + " 次）");
            return;
        }

        using (var dlg = new HistForm(sort))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.Chosen))
                LoadFolder(dlg.Chosen);
        }
        txtCmd.Focus();
    }

    void CdCmd(string target)
    {
        if (target == null) { LoadFolder(HomeDir()); return; }       // cd → ~
        if (target == "-") { GoBack(); return; }                      // cd - → 上一处
        if (target == "~") { LoadFolder(HomeDir()); return; }
        if (!LooksLikePath(target)) { LoadFolder(target); return; }   // 显示名，例如 "OS (C:)"
        LoadFolder(FsPath(target));
    }

    void LsCmd(HashSet<char> f, List<string> args, bool longForm)
    {
        if (args.Count > 0) CdCmd(args[0]);
        details = true;
        if (f.Contains('t')) { sortCol = 1; sortAsc = true; }
        else if (f.Contains('S')) { sortCol = 3; sortAsc = true; }
        else if (f.Contains('r')) sortAsc = !sortAsc;
        if ((f.Contains('t') || f.Contains('S')) && f.Contains('r')) sortAsc = false;
        UpdateHeaders();
        ApplySort(items);
        ApplyView();
        Msg("ls  " + lv.Items.Count + " 项 · " + ColNames[sortCol] + (sortAsc ? " ↑" : " ↓"));
    }

    void MkDirCmd(HashSet<char> f, List<string> args)
    {
        if (args.Count == 0) { Msg("用法: mkdir [-p] 目录"); return; }
        if (!Xg.IsFs(curPath)) { Msg("当前是虚拟命名空间"); return; }
        string full = FsPath(args[0]);
        if (string.IsNullOrEmpty(full)) { Msg("mkdir: 目标无效"); return; }
        if (Directory.Exists(full)) { Msg("mkdir: 已存在 " + full); return; }
        if (!f.Contains('p'))
        {
            string parent = null;
            try { parent = Path.GetDirectoryName(full); } catch { }
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            { Msg("mkdir: 上级目录不存在（建多级用 mkdir -p）"); return; }
        }
        try
        {
            Directory.CreateDirectory(full);
            Populate();
            lv.SelectedItems.Clear();          // 只留新建的这一项，否则 Delete 会把旧选中项一起带走
            foreach (ListViewItem row in lv.Items)
            {
                var it = row.Tag as Xg.Item;
                if (it != null && it.Path != null && string.Equals(it.Path, full, StringComparison.CurrentCultureIgnoreCase))
                { row.Selected = true; row.Focused = true; lv.EnsureVisible(row.Index); }
            }
            Msg("mkdir: 已创建 " + full + "（已选中，Delete 可删）");
        }
        catch (Exception ex) { Msg("mkdir: " + ex.Message); }
    }

    // rm 在 Linux 里是永久删除：目录要 -r，-f 不再确认
    void RmCmd(HashSet<char> f, List<string> args)
    {
        if (args.Count == 0) { Msg("用法: rm [-r] [-f] 目标…（进回收站用 trash）"); return; }
        var targets = new List<Xg.Item>();
        foreach (var a in args)
        {
            var it = FindItem(a);
            if (it != null) { targets.Add(it); continue; }
            string p = FsPath(a);
            if (p != null && (File.Exists(p) || Directory.Exists(p)))
            {
                var o = new Xg.Item();
                o.Name = Path.GetFileName(p);
                o.Path = p;
                o.IsFolder = Directory.Exists(p);
                targets.Add(o);
            }
        }
        if (targets.Count == 0) { Msg("rm: 没有匹配的目标"); return; }

        foreach (var it in targets)
            if (it.IsFolder && !f.Contains('r')) { Msg("rm: " + it.Name + " 是目录，用 rm -r"); return; }

        if (!f.Contains('f'))
        {
            var names = new List<string>();
            foreach (var it in targets.Take(5)) names.Add(it.Name);
            if (MessageBox.Show("永久删除 " + targets.Count + " 项？\n\n" + string.Join("\n", names.ToArray()) +
                                "\n\n不会进回收站。",
                    "rm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        int ok = 0;
        var errs = new List<string>();
        foreach (var it in targets)
        {
            try
            {
                if (!Xg.IsFs(it.Path)) { it.Com.InvokeVerb("delete"); ok++; continue; }
                if (it.IsFolder) Directory.Delete(it.Path, true);
                else File.Delete(it.Path);
                ok++;
            }
            catch (Exception ex) { errs.Add(it.Name + " —— " + ex.Message); }
        }
        Populate();
        Msg("rm: 删除 " + ok + (errs.Count > 0 ? "，失败 " + errs.Count : ""));
        if (errs.Count > 0)
            MessageBox.Show(string.Join("\n", errs.Take(4).ToArray()), "rm 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void RmDirCmd(List<string> args)
    {
        if (args.Count == 0) { Msg("用法: rmdir 目录"); return; }
        foreach (var a in args)
        {
            var it = FindItem(a);
            string p = it != null ? it.Path : FsPath(a);
            if (p == null || !Directory.Exists(p)) { Msg("rmdir: 没有这个目录: " + a); return; }
            try { Directory.Delete(p, false); }
            catch (Exception ex) { Msg("rmdir: " + ex.Message); return; }
        }
        Populate();
        Msg("rmdir: 完成");
    }

    void MvCmd(List<string> args)
    {
        if (args.Count != 2) { Msg("用法: mv 源 目标"); return; }
        var src = FindItem(args[0]);
        string sp = src != null ? src.Path : FsPath(args[0]);
        if (sp == null || (!File.Exists(sp) && !Directory.Exists(sp))) { Msg("mv: 没有这个文件: " + args[0]); return; }
        bool isDir = Directory.Exists(sp);
        string dp = FsPath(args[1]);
        if (string.IsNullOrEmpty(dp)) { Msg("mv: 目标无效"); return; }
        try
        {
            if (Directory.Exists(dp)) dp = Path.Combine(dp, Path.GetFileName(sp));   // 移进目录
            if (isDir) Directory.Move(sp, dp); else File.Move(sp, dp);
            Populate();
            Msg("mv: " + Path.GetFileName(sp) + " → " + dp);
        }
        catch (Exception ex) { Msg("mv: " + ex.Message); }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    void CpCmd(HashSet<char> f, List<string> args)
    {
        if (args.Count != 2) { Msg("用法: cp [-r] 源 目标"); return; }
        var src = FindItem(args[0]);
        string sp = src != null ? src.Path : FsPath(args[0]);
        if (sp == null || (!File.Exists(sp) && !Directory.Exists(sp))) { Msg("cp: 没有这个文件: " + args[0]); return; }
        bool isDir = Directory.Exists(sp);
        if (isDir && !f.Contains('r')) { Msg("cp: " + args[0] + " 是目录，用 cp -r"); return; }
        string dp = FsPath(args[1]);
        if (string.IsNullOrEmpty(dp)) { Msg("cp: 目标无效"); return; }
        try
        {
            if (Directory.Exists(dp)) dp = Path.Combine(dp, Path.GetFileName(sp));
            if (isDir) CopyDir(sp, dp); else File.Copy(sp, dp, true);
            Populate();
            Msg("cp: " + Path.GetFileName(sp) + " → " + dp);
        }
        catch (Exception ex) { Msg("cp: " + ex.Message); }
    }

    // cat：把文件选中，正文用右侧预览显示（前 120 行）
    void CatCmd(string name)
    {
        if (name == null) { Msg("用法: cat 文件"); return; }
        for (int i = 0; i < lv.Items.Count; i++)
        {
            var it = lv.Items[i].Tag as Xg.Item;
            if (it == null) continue;
            bool hit = string.Equals(it.Name, name, StringComparison.CurrentCultureIgnoreCase) ||
                       (it.Path != null && string.Equals(it.Path, name, StringComparison.CurrentCultureIgnoreCase));
            if (!hit) continue;
            if (it.IsFolder) { Msg("cat: " + name + " 是目录"); return; }
            SelectOnly(i);
            RenderPreview();
            Msg("cat: " + it.Name + "（正文在右侧预览，前 120 行）");
            return;
        }
        Msg("cat: 没有这个文件: " + name);
    }

    void TouchCmd(string name)
    {
        if (name == null) { Msg("用法: touch 文件名"); return; }
        if (!Xg.IsFs(curPath)) { Msg("当前是虚拟命名空间"); return; }
        string p = FsPath(name);
        try
        {
            if (File.Exists(p)) File.SetLastWriteTime(p, DateTime.Now);
            else File.WriteAllText(p, "");
            Populate();
            for (int i = 0; i < lv.Items.Count; i++)
            {
                var it = lv.Items[i].Tag as Xg.Item;
                if (it != null && it.Path != null && string.Equals(it.Path, p, StringComparison.CurrentCultureIgnoreCase))
                { SelectOnly(i); break; }
            }
            Msg("touch: " + p);
        }
        catch (Exception ex) { Msg("touch: " + ex.Message); }
    }

    // trash：走回收站（GUI 的 Delete 同款）
    void TrashCmd(List<string> args)
    {
        if (args.Count == 0) { Msg("用法: trash 目标…"); return; }
        lv.SelectedItems.Clear();
        int n = 0;
        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it == null) continue;
            foreach (var a in args)
                if (string.Equals(it.Name, a, StringComparison.CurrentCultureIgnoreCase)) { row.Selected = true; n++; break; }
        }
        if (n == 0) { Msg("trash: 没有匹配的项"); return; }
        DeleteSelected(false);
    }

    static bool WildMatch(string name, string pat)
    {
        var sb = new StringBuilder("^");
        foreach (char c in pat)
        {
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append("$");
        return Regex.IsMatch(name, sb.ToString(), RegexOptions.IgnoreCase);
    }

    // sel *.md / sel pic.jpg：按名字选（支持 * ?），用来配合预览和批量操作
    void SelCmd(string pat)
    {
        if (pat == null) { Msg("用法: sel 名字或 *.md"); return; }
        var hits = new List<ListViewItem>();
        foreach (ListViewItem r in lv.Items)
        {
            var it = r.Tag as Xg.Item;
            if (it == null) continue;
            if (WildMatch(it.Name, pat)) hits.Add(r);
        }
        lv.SelectedItems.Clear();
        foreach (var r in hits) r.Selected = true;
        if (hits.Count > 0)
        {
            hits[0].Focused = true;
            lv.EnsureVisible(hits[0].Index);
        }
        Msg(hits.Count == 0 ? "没有匹配: " + pat : "选中 " + hits.Count + " 项");
    }

    void IconCmd(List<string> args)
    {
        if (args.Count < 1) { Msg("图标大小 " + iconPx + "px；用法: icon 48|96|144|256"); return; }
        string a = args[0].ToLowerInvariant();
        if (a.EndsWith("px")) a = a.Substring(0, a.Length - 2);
        int px;
        if (!int.TryParse(a, out px) || Array.IndexOf(IconSteps, px) < 0)
        {
            Msg("用法: icon 48|96|144|256（big 依次循环）");
            return;
        }
        SetIconSize(px);
    }

    void SortCmd(List<string> t)    {
        string key = t.Count > 1 ? t[1].ToLowerInvariant() : "";
        int col;
        if (key == "name") col = 0;
        else if (key == "date" || key == "time") col = 1;
        else if (key == "type") col = 2;
        else if (key == "size") col = 3;
        else if (key.Length == 0) { col = sortCol; }
        else { Msg("用法: sort name|date|type|size [asc|desc]"); return; }

        bool asc = sortAsc;
        if (key.Length > 0) { sortCol = col; asc = true; }
        if (t.Count > 2)
        {
            string d = t[2].ToLowerInvariant();
            if (d == "desc" || d == "d" || d == "down") asc = false;
            else if (d == "asc" || d == "a" || d == "up") asc = true;
        }
        sortAsc = asc;
        UpdateHeaders();
        ResortRows();
        Msg("排序：" + ColNames[sortCol] + (sortAsc ? " 升序" : " 降序"));
    }

    void OpenNamed(string name)
    {
        if (name == null) { Msg("用法: open 名字"); return; }
        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it == null) continue;
            if (string.Equals(it.Name, name, StringComparison.CurrentCultureIgnoreCase))
            {
                Open(it);        // 图片会进内置看图（可左右切）
                return;
            }
        }
        Msg("没找到: " + name);
    }

    void Info(string name)
    {
        if (name == null) { Msg("用法: info 名字"); return; }
        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it == null) continue;
            if (string.Equals(it.Name, name, StringComparison.CurrentCultureIgnoreCase))
            {
                Msg(it.Name + "   " + (it.Path ?? "-") + "   " + (it.Type ?? "") + "   " +
                    (it.IsFolder ? "目录" : Xg.Human(it.Size)) +
                    (it.Modified != DateTime.MinValue ? "   " + it.Modified.ToString("yyyy-MM-dd HH:mm") : "") +
                    "   " + (Xg.IsFs(it.Path) ? "filesystem" : "virtual"));
                return;
            }
        }
        Msg("没找到: " + name);
    }

    string Head(int n)
    {
        var names = new List<string>();
        for (int i = 0; i < lv.Items.Count && i < n; i++) names.Add(lv.Items[i].Text);
        return string.Join(", ", names.ToArray());
    }

    // 读某个干跑日志的最后一行（自检看 XG_WT_DRY / XG_EX_DRY 里拼出来的命令行）
    static string LastLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return "(none)";
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            return lines.Length > 0 ? lines[lines.Length - 1] : "(empty)";
        }
        catch (Exception e) { return "ERR " + e.Message; }
    }

    // --selftest：不依赖鼠标，按脚本跑一遍视图 / 排序 / 面包屑 / 右键菜单 / 预览
    string CompText(string line)
    {
        txtCmd.Text = line;
        txtCmd.SelectionStart = txtCmd.TextLength;
        Complete(false);
        return txtCmd.Text;
    }

    string CompTextTimes(string line, int n)
    {
        txtCmd.Text = line;
        txtCmd.SelectionStart = txtCmd.TextLength;
        for (int i = 0; i < n; i++) Complete(false);
        return txtCmd.Text;
    }

    string CompTextBackward(string line)
    {
        txtCmd.Text = line;
        txtCmd.SelectionStart = txtCmd.TextLength;
        Complete(false);
        Complete(true);
        return txtCmd.Text;
    }

    internal void SelfTest(StringBuilder sb)
    {
        sb.AppendLine("path=" + curPath);
        sb.AppendLine("start: view=" + lv.View + " details=" + details + " btn=" + btnView.Text + " rows=" + lv.Items.Count);

        Run("ic");
        sb.AppendLine("after ic: view=" + lv.View + " details=" + details + " btn=" + btnView.Text +
                      " big=" + ilBig.Images.Count + " size=" + ilBig.ImageSize.Width +
                      " rows=" + lv.Items.Count);
        if (ilBig.Images.Count > 0)
            sb.AppendLine("big[0]=" + ilBig.Images[0].Width + "x" + ilBig.Images[0].Height +
                          " attached=" + (lv.LargeImageList == ilBig));

        Run("ls");
        sb.AppendLine("after ls: view=" + lv.View + " details=" + details + " btn=" + btnView.Text);

        // ex：和 wt 一套目标解析，开的是资源管理器。XG_EX_DRY 只记一行、不真弹窗口
        try
        {
            string dry = Path.Combine(Path.GetTempPath(), "xg-ex.txt");
            string probe = Path.Combine(curPath, "__xg_ex_probe");
            string keep = Environment.GetEnvironmentVariable("XG_EX_DRY");
            try { File.Delete(dry); } catch { }
            Environment.SetEnvironmentVariable("XG_EX_DRY", "1");
            try
            {
                Directory.CreateDirectory(probe);
                File.WriteAllText(Path.Combine(probe, "pick.txt"), "ex probe\r\n");
                Run("ex");
                sb.AppendLine("ex          -> " + LastLine(dry));
                Run("ex __xg_ex_probe");
                sb.AppendLine("ex dir      -> " + LastLine(dry));
                Run("ex __xg_ex_probe\\pick.txt");
                sb.AppendLine("ex file     -> " + LastLine(dry));
                Run("ex -s __xg_ex_probe\\pick.txt");
                sb.AppendLine("ex -s file  -> " + LastLine(dry));
                Run("ex __xg_no_such_dir__");
                sb.AppendLine("ex bogus    -> msg=" + lblStatus.Text.Trim() + "  (dry 没多记一行: " +
                              LastLine(dry).StartsWith("/select,\"", StringComparison.Ordinal) + ")");
            }
            finally
            {
                Environment.SetEnvironmentVariable("XG_EX_DRY", keep);
                try { if (Directory.Exists(probe)) Directory.Delete(probe, true); } catch { }
                try { Populate(); } catch { }
            }
        }
        catch (Exception ex) { sb.AppendLine("ex EXCEPTION " + ex.Message); }

        Run("sort size desc");
        sb.AppendLine("sort size desc: " + Head(4) + "   header=" + lv.Columns[3].Text);
        Run("sort name asc");
        sb.AppendLine("sort name asc: " + Head(4) + "   header=" + lv.Columns[0].Text);
        Run("sort date desc");
        sb.AppendLine("sort date desc: " + Head(2) + "   header=" + lv.Columns[1].Text);

        var crumbs = new List<string>();
        foreach (Control c in crumb.Controls) crumbs.Add(c.Text);
        sb.AppendLine("crumbs=" + string.Join(" | ", crumbs.ToArray()));

        // Tab 补全：直接调 Complete，不依赖键盘（键盘路径见 CmdBox.IsInputKey）
        sb.AppendLine("tab is input key: " + txtCmd.TabIsInputKey);
        txtCmd.Text = "mk";
        txtCmd.SelectionStart = txtCmd.TextLength;
        CmdKeyDown(txtCmd, new KeyEventArgs(Keys.Tab));
        sb.AppendLine("tab via KeyDown   -> '" + txtCmd.Text + "'");
        sb.AppendLine("compl 'mk'          -> '" + CompText("mk") + "'");
        sb.AppendLine("compl 'wt'          -> '" + CompText("wt") + "'");
        sb.AppendLine("compl 'ex'          -> '" + CompText("ex") + "'");
        sb.AppendLine("compl 'cd ex'       -> '" + CompText("cd ex") + "'");
        sb.AppendLine("compl 'cd xs\\xg.e'  -> '" + CompText("cd xs\\xg.e") + "'");
        sb.AppendLine("compl 'cd 有'       -> '" + CompText("cd 有") + "'");
        sb.AppendLine("compl 'cd ..\\f'    -> '" + CompText("cd ..\\f") + "'");
        sb.AppendLine("compl cycle 'cd 'x3 -> '" + CompTextTimes("cd ", 3) + "'");
        sb.AppendLine("compl shift-tab     -> '" + CompTextBackward("cd ") + "'");
        sb.AppendLine("compl none 'zzz'    -> '" + CompText("cd zzz") + "'");

        // 上下键挪选中：必须单选，不能累加（MultiSelect 下 .Selected=true 是"加上"）
        txtCmd.Clear();
        lv.SelectedItems.Clear();
        lv.Items[0].Selected = true;
        lv.Items[0].Focused = true;
        CmdKeyDown(txtCmd, new KeyEventArgs(Keys.Down));
        CmdKeyDown(txtCmd, new KeyEventArgs(Keys.Down));
        sb.AppendLine("arrow down x2: sel=" + lv.SelectedItems.Count +
                      " focus=" + (lv.FocusedItem == null ? -1 : lv.FocusedItem.Index));
        CmdKeyDown(txtCmd, new KeyEventArgs(Keys.Up));
        sb.AppendLine("arrow up:      sel=" + lv.SelectedItems.Count +
                      " focus=" + (lv.FocusedItem == null ? -1 : lv.FocusedItem.Index));

        try
        {
            menu.Show(lv, new Point(20, 20));
            sb.AppendLine("menu: open=" + miOpen.Enabled + " | " + miTerm.Text + " | props=" + miProps.Enabled);
            menu.Close();
        }
        catch (Exception ex) { sb.AppendLine("menu EXCEPTION " + ex.Message); }

        if (lv.Items.Count > 0)
        {
            lv.SelectedItems.Clear();
            lv.Items[0].Selected = true;
            RenderPreview();
            sb.AppendLine("preview: image=" + (pic.Image != null) + " textLen=" + lblPreview.Text.Length +
                          " first=" + (lblPreview.Text.Length > 0 ? lblPreview.Text.Split('\n')[0] : ""));
        }

        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it == null || it.Path == null) continue;
            if (!it.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) continue;
            lv.SelectedItems.Clear();
            row.Selected = true;
            RenderPreview();
            sb.AppendLine("image preview: image=" + (pic.Image != null) +
                          " size=" + (pic.Image != null ? pic.Image.Width + "x" + pic.Image.Height : "-") +
                          " visible=" + pic.Visible);
            break;
        }

        Run("ic");
        sb.AppendLine("ic again: view=" + lv.View + " btn=" + btnView.Text + " rows=" + lv.Items.Count);

        // 图标尺寸档位（看大图 / 相片）
        Run("icon 256");
        sb.AppendLine("icon 256: px=" + iconPx + " imageList=" + ilBig.ImageSize.Width +
                      " btn=" + btnSize.Text + " view=" + lv.View);
        Run("icon 96");
        sb.AppendLine("icon 96:  px=" + iconPx + " imageList=" + ilBig.ImageSize.Width);

        // 上一级：进子目录再 up，应该回到原处
        string before = curPath;
        Run("cd xs");
        string inside = curPath;
        GoUp();
        sb.AppendLine("cd xs -> " + (inside != before) + " ; up -> " + (curPath == before ? "ok" : curPath));

        // Alt+↑ 走真实 KeyDown 路径（焦点在命令行时也该导航）
        Run("cd xs");
        CmdKeyDown(txtCmd, new KeyEventArgs(Keys.Up | Keys.Alt));
        sb.AppendLine("alt+up via KeyDown -> " + (curPath == before ? "ok" : curPath));

        // sel + 预览尺寸
        Run("sel *.md");
        sb.AppendLine("sel *.md -> selected=" + lv.SelectedItems.Count +
                      " previewLen=" + lblPreview.Text.Length);

        // mkdir 之后必须只剩新建的一项被选中（否则 Delete 会连带删掉原来的选中项）
        if (!rootsView)
        {
            try
            {
                Run("mkdir __xg_selftest_tmp");
                sb.AppendLine("mkdir: selected=" + lv.SelectedItems.Count +
                              " first='" + (lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Text : "-") + "'");
                string tmp = Path.Combine(curPath, "__xg_selftest_tmp");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Populate();
            }
            catch (Exception ex) { sb.AppendLine("mkdir test EXCEPTION " + ex.Message); }
        }

        // Linux 风格命令：在临时目录里跑一遍，最后清干净
        if (!rootsView)
        {
            string baseDir = curPath;
            string lx = Path.Combine(baseDir, "__xg_lx");
            string lx3 = Path.Combine(baseDir, "__xg_lx3");
            try
            {
                Run("mkdir -p __xg_lx/deep/a");
                sb.AppendLine("mkdir -p: " + Directory.Exists(Path.Combine(lx, "deep\\a")));
                Run("touch __xg_lx/deep/a/x.txt");
                bool made = File.Exists(Path.Combine(lx, "deep\\a\\x.txt"));
                if (made) File.WriteAllText(Path.Combine(lx, "deep\\a\\x.txt"), "hello linux\r\n");
                sb.AppendLine("touch: " + made);

                Run("cd __xg_lx/deep/a");
                Run("cat x.txt");
                sb.AppendLine("cd/cat: in=" + curPath.EndsWith("a") + " previewLen=" + lblPreview.Text.Length);
                Run("cd -");
                sb.AppendLine("cd -: back=" + (curPath == baseDir));

                Run("ls -t");
                sb.AppendLine("ls -t: header=" + lv.Columns[1].Text);

                Run("cp -r __xg_lx __xg_lx2");
                bool copied = Directory.Exists(Path.Combine(baseDir, "__xg_lx2"));
                Run("mv __xg_lx2 __xg_lx3");
                sb.AppendLine("cp -r/mv: copied=" + copied + " moved=" + Directory.Exists(lx3));

                Run("rm -rf __xg_lx __xg_lx3");
                sb.AppendLine("rm -rf: gone=" + (!Directory.Exists(lx) && !Directory.Exists(lx3)));
            }
            catch (Exception ex) { sb.AppendLine("linux cmds EXCEPTION " + ex.Message); }
            finally
            {
                try { if (Directory.Exists(lx)) Directory.Delete(lx, true); } catch { }
                try { if (Directory.Exists(lx3)) Directory.Delete(lx3, true); } catch { }
                try { if (Directory.Exists(Path.Combine(baseDir, "__xg_lx2"))) Directory.Delete(Path.Combine(baseDir, "__xg_lx2"), true); } catch { }
                try { Populate(); } catch { }
            }
        }

        // 历史：进去一次、回来、再按关键字跳回去
        if (!rootsView)
        {
            string hp = Path.Combine(curPath, "__xg_hist_probe");
            try
            {
                Directory.CreateDirectory(hp);
                Run("cd __xg_hist_probe");
                Run("cd ..");
                Run("hist __xg_hist_probe");
                sb.AppendLine("hist: entries=" + History.Count +
                              " jumped=" + curPath.EndsWith("__xg_hist_probe", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { sb.AppendLine("hist EXCEPTION " + ex.Message); }
            finally
            {
                Run("cd ..");
                try { if (Directory.Exists(hp)) Directory.Delete(hp, true); } catch { }
            }
        }

        Run("roots");
        sb.AppendLine("roots: rows=" + lv.Items.Count + " view=" + lv.View + " crumbs=" + crumb.Controls.Count);
    }

    void Roots()
    {
        var roots = new List<Xg.Item>();
        Action<string, string> add = delegate(string n, string p)
        {
            var o = new Xg.Item();
            o.Name = n; o.Path = p; o.IsFolder = true; o.Type = "命名空间";
            o.IconSmall = Xg.IconIndex(p, true, Xg.SHGFI_SMALLICON);
            roots.Add(o);
        };
        add("此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        add("回收站", "::{645FF040-5081-101B-9F08-00AA002F954E}");
        add("控制面板", "::{26EE0668-A00A-44D7-9371-BEB064C98683}");
        add("网络", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
        add("用户目录", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady) add("盘 " + d.Name, d.Name);

        rootsView = true;
        items = roots;
        ApplySort(items);
        Text = "命名空间根 — xg";
        UpdatePathBar();
        ApplyView();
        Msg("回车进入选中的命名空间");
    }
}

// 深色右键菜单
// 图标视图里 Ctrl+滚轮 缩放（和资源管理器一致），普通滚轮照常滚动
// 全屏看图：←/→ 在同目录图片间切换（系统"照片"单开一张时切不了，这里自己实现）
class Viewer : Form
{
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
                                          ".tif", ".tiff", ".ico", ".avif" };

    public static bool IsImage(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string e = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ImageExt, e) >= 0;
    }

    readonly List<string> paths;
    readonly PictureBox box = new PictureBox();
    readonly Label caption = new Label();
    int idx;
    Image current;
    MemoryStream buf;

    public Viewer(List<string> list, int start)
    {
        paths = list;
        idx = Math.Max(0, Math.Min(list.Count - 1, start));

        Text = "xg 看图";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        try { Bounds = Screen.FromPoint(Cursor.Position).Bounds; } catch { }
        KeyPreview = true;

        box.Dock = DockStyle.Fill;
        box.SizeMode = PictureBoxSizeMode.Zoom;
        box.BackColor = Color.Black;

        caption.Dock = DockStyle.Bottom;
        caption.Height = 34;
        caption.TextAlign = ContentAlignment.MiddleCenter;
        caption.ForeColor = Color.FromArgb(205, 205, 205);
        caption.BackColor = Color.FromArgb(18, 18, 18);
        caption.Font = new Font("Microsoft YaHei UI", 10f);

        Controls.Add(box);
        Controls.Add(caption);

        KeyDown += ViewerKeyDown;
        MouseWheel += delegate(object s, MouseEventArgs e) { Step(e.Delta > 0 ? -1 : 1); };
        box.DoubleClick += delegate { Close(); };
        Shown += delegate { ShowCurrent(); };
        FormClosed += delegate { ReleaseImage(); };
    }

    void ViewerKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Up: case Keys.PageUp: Step(-1); break;
            case Keys.Right: case Keys.Down: case Keys.PageDown: case Keys.Space: Step(1); break;
            case Keys.Home: idx = 0; ShowCurrent(); break;
            case Keys.End: idx = paths.Count - 1; ShowCurrent(); break;
            case Keys.Escape: case Keys.Q: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Step(int d)
    {
        if (paths.Count == 0) return;
        idx = (idx + d + paths.Count) % paths.Count;   // 到头绕回，和相册一样
        ShowCurrent();
    }

    void ReleaseImage()
    {
        box.Image = null;
        if (current != null) { try { current.Dispose(); } catch { } current = null; }
        if (buf != null) { try { buf.Dispose(); } catch { } buf = null; }
    }

    void ShowCurrent()
    {
        if (idx < 0 || idx >= paths.Count) return;
        string p = paths[idx];
        ReleaseImage();

        Image img = null;
        try
        {
            if (Path.GetExtension(p).ToLowerInvariant() == ".gif")
            {
                buf = new MemoryStream(File.ReadAllBytes(p));   // 走内存流，保留动画
                img = Image.FromStream(buf);
            }
            else
            {
                int side = Math.Max(box.ClientSize.Width, box.ClientSize.Height);
                if (side < 800) side = 1200;
                img = Xg.Thumbnail(p, Math.Min(side, 2048));
                if (img == null)
                {
                    buf = new MemoryStream(File.ReadAllBytes(p));
                    img = Image.FromStream(buf);
                }
            }
        }
        catch { }

        current = img;
        box.Image = img;
        Text = Path.GetFileName(p) + " — xg 看图";
        caption.Text = (idx + 1) + " / " + paths.Count + "    " + Path.GetFileName(p) +
                       "    ← → 切换 · Home/End 首尾 · Esc 关闭" +
                       (img != null ? "    " + img.Width + "×" + img.Height : "    （预览失败）");
    }
}

// 历史查询窗：打字过滤，↑↓ 选，Enter 跳过去
class HistForm : Form
{
    readonly TextBox filter = new TextBox();
    readonly ListBox list = new ListBox();
    readonly Label hint = new Label();
    readonly string sort;
    readonly float sc = 1f;
    List<History.Entry> rows = new List<History.Entry>();

    public string Chosen;

    public HistForm(string sort)
    {
        this.sort = sort;
        float s = 1f;
        try { s = CreateGraphics().DpiX / 96f; } catch { }
        if (s < 1f) s = 1f;
        sc = s;

        Text = "历史文件夹 — xg";
        Size = new Size((int)(820 * s), (int)(560 * s));
        MinimumSize = new Size((int)(520 * s), (int)(320 * s));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 30);
        ShowInTaskbar = false;
        KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9f * s);

        var pad = new Panel();
        pad.Dock = DockStyle.Top;
        pad.Height = (int)(42 * s);
        pad.Padding = new Padding((int)(10 * s), (int)(8 * s), (int)(10 * s), (int)(6 * s));
        pad.BackColor = Color.FromArgb(24, 24, 24);

        filter.Dock = DockStyle.Fill;
        filter.BorderStyle = BorderStyle.FixedSingle;
        filter.BackColor = Color.FromArgb(16, 16, 16);
        filter.ForeColor = Color.FromArgb(235, 235, 235);
        filter.Font = new Font("Consolas", 11f * s);
        pad.Controls.Add(filter);

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Color.FromArgb(26, 26, 26);
        list.ForeColor = Color.FromArgb(225, 225, 225);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = (int)(27 * s);
        list.IntegralHeight = false;

        hint.Dock = DockStyle.Bottom;
        hint.Height = (int)(26 * s);
        hint.TextAlign = ContentAlignment.MiddleLeft;
        hint.Padding = new Padding((int)(10 * s), 0, 0, 0);
        hint.ForeColor = Color.FromArgb(150, 150, 150);
        hint.BackColor = Color.FromArgb(20, 20, 20);

        Controls.Add(list);
        Controls.Add(hint);
        Controls.Add(pad);

        list.DrawItem += ListDraw;
        list.DoubleClick += delegate { Choose(); };
        list.KeyDown += ListKey;
        filter.TextChanged += delegate { Reload(); };
        filter.KeyDown += FilterKey;
        Shown += delegate { Reload(); filter.Focus(); };
        KeyDown += delegate(object s2, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
    }

    string SortName()
    {
        return sort == "count" ? "按次数" : sort == "time" ? "按最近" : "按常用";
    }

    void Reload()
    {
        string q = filter.Text.Trim();
        rows = History.Snapshot(q, sort);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var e in rows) list.Items.Add(e.Path);
        list.EndUpdate();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        hint.Text = "  " + rows.Count + " 条" + (q.Length > 0 ? "（过滤：" + q + "）" : "") +
                    "   ·   ↑↓ 选择 · Enter 跳转 · Esc 关闭   ·   " + SortName() +
                    "   ·   " + History.FilePath;
    }

    void ListDraw(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= rows.Count) return;
        var en = rows[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? Color.FromArgb(56, 56, 56) : Color.FromArgb(26, 26, 26)))
            e.Graphics.FillRectangle(bg, e.Bounds);

        string meta = en.Count + "×   " + en.Last.ToString("MM-dd HH:mm");
        var rf = new Rectangle(e.Bounds.Right - (int)(160 * sc), e.Bounds.Top, (int)(150 * sc), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, meta, Font, rf, Color.FromArgb(140, 140, 140),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var rp = new Rectangle(e.Bounds.Left + (int)(8 * sc), e.Bounds.Top,
                               e.Bounds.Width - (int)(178 * sc), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, en.Path, Font, rp, sel ? Color.White : Color.FromArgb(215, 215, 215),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
    }

    void FilterKey(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down && list.Items.Count > 0)
        {
            list.Focus();
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter) { Choose(); e.Handled = true; }
    }

    void ListKey(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Choose(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
    }

    void Choose()
    {
        if (rows.Count == 0) return;
        int i = list.SelectedIndex;
        if (i < 0) i = 0;
        if (i >= rows.Count) return;
        Chosen = rows[i].Path;
        DialogResult = DialogResult.OK;
        Close();
    }
}

class IconList : ListView
{
    public Func<int, bool> Zoom;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control && Zoom != null)
        {
            Zoom(e.Delta > 0 ? 1 : -1);
            return;
        }
        base.OnMouseWheel(e);
    }
}

// Tab 必须当普通输入键，否则被当成对话框导航键、焦点先跑掉，KeyDown 里补全也救不回来
class CmdBox : TextBox
{
    public bool TabIsInputKey { get { return IsInputKey(Keys.Tab) && IsInputKey(Keys.Shift | Keys.Tab); } }

    protected override bool IsInputKey(Keys keyData)
    {
        if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) return true;
        return base.IsInputKey(keyData);
    }
}

class DarkMenuTable : ProfessionalColorTable{
    static readonly Color Bg = Color.FromArgb(32, 32, 32);
    static readonly Color Sel = Color.FromArgb(56, 56, 56);

    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Color.FromArgb(72, 72, 72); } }
    public override Color MenuItemBorder { get { return Sel; } }
    public override Color MenuItemSelected { get { return Sel; } }
    public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
    public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
    public override Color SeparatorDark { get { return Color.FromArgb(64, 64, 64); } }
    public override Color SeparatorLight { get { return Color.FromArgb(44, 44, 44); } }
}
