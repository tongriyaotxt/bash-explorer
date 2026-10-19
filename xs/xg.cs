// xg - a graphical shell browser for the Windows Shell namespace.
//
//   line 1 : path bar (current namespace location)
//   line 2 : command line (cd / ls / ll / mkdir / ... with Tab + history)
//   below  : a real GUI listing that uses the *system* image list, so the
//            icons are exactly the ones Explorer draws (drives, special
//            folders, file types, shortcuts).
//
// build: csc /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll
//            /r:Microsoft.CSharp.dll xg.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Xg
{
    public static dynamic shell;
    static dynamic cur;
    static string curPath = "";
    static string curName = "";

    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }
        catch { MessageBox.Show("无法创建 Shell.Application"); return; }

        string start = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
        Application.Run(new Frm(start));
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

    public const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0,
                      SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10,
                      FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint LVM_SETIMAGELIST = 0x1003;
    public const int LVSIL_NORMAL = 0, LVSIL_SMALL = 1;

    public static IntPtr SystemImageList(uint which)
    {
        SHFILEINFO info = new SHFILEINFO();
        return SHGetFileInfo("", 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                             SHGFI_SYSICONINDEX | (uint)which);
    }

    public static int IconIndex(string path, bool isFolder, uint which)
    {
        SHFILEINFO info = new SHFILEINFO();
        uint flags = SHGFI_SYSICONINDEX | (uint)which;
        if (!string.IsNullOrEmpty(path) && File.Exists(path) || Directory.Exists(path))
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
        else if (isFolder)
            SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags | SHGFI_USEFILEATTRIBUTES);
        else
            SHGetFileInfo("file.txt", FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags | SHGFI_USEFILEATTRIBUTES);
        return info.iIcon;
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
    public class Item
    {
        public string Name, Path, Type;
        public bool IsFolder;
        public long Size;
        public DateTime Modified;
        public dynamic Com;
        public int IconSmall, IconLarge;
    }

    public static dynamic ItemsOf(dynamic f)
    {
        return f.Items();
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
            o.IconLarge = IconIndex(o.Path, o.IsFolder, SHGFI_LARGEICON);
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
        return (n / 1073741824.0).ToString("0.0") + " GB";
    }
}

class Frm : Form
{
    readonly Panel pnlPath = new Panel();
    readonly Label lblPath = new Label();
    readonly Panel pnlCmd = new Panel();
    readonly TextBox txtCmd = new TextBox();
    readonly ListView lv = new ListView();
    readonly Label lblStatus = new Label();
    readonly SplitContainer split = new SplitContainer();
    readonly PictureBox pic = new PictureBox();
    readonly Label lblPreview = new Label();
    readonly List<string> forward = new List<string>();
    readonly ToolTip tip = new ToolTip();

    dynamic cur;
    string curPath = "";
    string curName = "";
    readonly List<string> back = new List<string>();
    bool suppressHistory;
    readonly List<string> hist = new List<string>();
    int hIdx;
    bool details = true;
    float scale = 1f;

    Frm() { }

    public Frm(string start)
    {
        Text = "xg · Shell namespace";
        float sc = 1f;
        try { sc = CreateGraphics().DpiX / 96f; } catch { }
        if (sc < 1f) sc = 1f;
        scale = sc;
        Width = (int)(1000 * sc); Height = (int)(640 * sc);
        MinimumSize = new Size(640, 400);
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f * sc);
        BackColor = Color.FromArgb(30, 30, 30);

        // ---- line 1: path bar (own drawn strip, so the theme cannot repaint it)
        pnlPath.Dock = DockStyle.Top;
        pnlPath.Height = (int)(30 * sc);
        pnlPath.BackColor = Color.FromArgb(24, 24, 24);
        pnlPath.Padding = new Padding(10, 0, 10, 0);
        lblPath.Dock = DockStyle.Fill;
        lblPath.TextAlign = ContentAlignment.MiddleLeft;
        lblPath.AutoEllipsis = true;
        lblPath.ForeColor = Color.FromArgb(120, 220, 255);
        lblPath.Font = new Font("Consolas", 10.5f * sc, FontStyle.Bold);
        lblPath.BackColor = Color.Transparent;
        pnlPath.Controls.Add(lblPath);

        // ---- line 2: command line (inside its own strip so the theme cannot repaint it)
        pnlCmd.Dock = DockStyle.Top;
        pnlCmd.Height = (int)(34 * sc);
        pnlCmd.BackColor = Color.FromArgb(18, 18, 18);
        pnlCmd.Padding = new Padding(8, 5, 8, 5);
        txtCmd.Dock = DockStyle.Fill;
        txtCmd.BorderStyle = BorderStyle.None;
        txtCmd.BackColor = Color.FromArgb(18, 18, 18);
        txtCmd.ForeColor = Color.FromArgb(235, 235, 235);
        txtCmd.Font = new Font("Consolas", 11f * sc);
        var lblPrompt = new Label();
        lblPrompt.Text = "❯";
        lblPrompt.Dock = DockStyle.Left;
        lblPrompt.Width = (int)(26 * sc);
        lblPrompt.TextAlign = ContentAlignment.MiddleLeft;
        lblPrompt.ForeColor = Color.FromArgb(90, 200, 240);
        lblPrompt.Font = new Font("Consolas", 12f * sc, FontStyle.Bold);
        lblPrompt.BackColor = Color.FromArgb(18, 18, 18);
        pnlCmd.Controls.Add(txtCmd);
        pnlCmd.Controls.Add(lblPrompt);
        txtCmd.BringToFront();


        // ---- status
        lblStatus.Dock = DockStyle.Bottom;
        lblStatus.Height = (int)(22 * sc);
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.BackColor = Color.FromArgb(40, 40, 40);
        lblStatus.ForeColor = Color.FromArgb(190, 190, 190);
        lblStatus.Padding = new Padding(8, 0, 0, 0);

        // ---- listing
        lv.Dock = DockStyle.Fill;
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.HideSelection = false;
        lv.MultiSelect = true;
        lv.BackColor = Color.FromArgb(30, 30, 30);
        lv.ForeColor = Color.FromArgb(235, 235, 235);
        lv.BorderStyle = BorderStyle.None;
        lv.Columns.Add("名称", 420);
        lv.Columns.Add("修改日期", 150);
        lv.Columns.Add("类型", 160);
        lv.Columns.Add("大小", 100, HorizontalAlignment.Right);
        lv.ShowItemToolTips = true;

        // ---- preview pane (right)
        pic.Dock = DockStyle.Top;
        pic.Height = (int)(300 * sc);
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.BackColor = Color.FromArgb(22, 22, 22);
        pic.BorderStyle = BorderStyle.None;

        lblPreview.Dock = DockStyle.Fill;
        lblPreview.BackColor = Color.FromArgb(26, 26, 26);
        lblPreview.ForeColor = Color.FromArgb(210, 210, 210);
        lblPreview.Font = new Font("Microsoft YaHei UI", 9.5f * sc);
        lblPreview.Padding = new Padding(10, 10, 10, 10);
        lblPreview.TextAlign = ContentAlignment.TopLeft;
        lblPreview.AutoEllipsis = true;

        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.FixedPanel = FixedPanel.Panel2;
        split.BackColor = Color.FromArgb(60, 60, 60);
        split.SplitterWidth = 1;
        split.Panel1.Controls.Add(lv);
        split.Panel2.Controls.Add(lblPreview);
        split.Panel2.Controls.Add(pic);
        split.Panel2.BackColor = Color.FromArgb(26, 26, 26);

        Controls.Add(split);
        Controls.Add(lblStatus);
        Controls.Add(pnlCmd);
        Controls.Add(pnlPath);

        txtCmd.KeyDown += CmdKeyDown;
        lv.DoubleClick += delegate { OpenSelected(); };
        lv.KeyDown += LvKeyDown;
        lv.SelectedIndexChanged += delegate { UpdateStatus(); RenderPreview(); };
        lv.Resize += delegate { FitColumns(); };
        lv.SizeChanged += delegate { FitColumns(); };
        Shown += delegate {
            try { split.SplitterDistance = Math.Max(300, split.Width - (int)(360 * sc)); } catch { }
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

        // 1px 高的分隔线
        lv.OwnerDraw = false;

        LoadFolder(start);
        lv.HandleCreated += delegate { AttachImageLists(); };
        if (lv.IsHandleCreated) AttachImageLists();
    }

    void AttachImageLists()
    {
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
            cur = f;
            curPath = np;
            try { curName = (string)f.Self.Name; } catch { curName = np; }
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
        var items = ReadAll();
        items = items.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var it in items)
        {
            var row = new ListViewItem(it.Name);
            row.SubItems.Add(it.Modified == DateTime.MinValue || it.Modified.Year < 1900 ? "" : it.Modified.ToString("yyyy-MM-dd HH:mm"));
            row.SubItems.Add(it.Type ?? "");
            row.SubItems.Add(it.IsFolder ? "" : Xg.Human(it.Size));
            row.ImageIndex = details ? it.IconSmall : it.IconLarge;
            row.Tag = it;
            row.ToolTipText = it.Path ?? it.Name;
            lv.Items.Add(row);
        }
        lv.EndUpdate();

        lblPath.Text = curPath.StartsWith("::") ? curName + "   " + curPath : curPath;
        Text = curName + " — xg";
        UpdateStatus();
        FitColumns();
        BeginInvoke((Action)delegate { FitColumns(); });
        if (lv.Items.Count > 0) lv.Items[0].Selected = true;
    }

    static readonly string[] TextExt = { ".txt", ".md", ".cs", ".json", ".xml", ".yml", ".yaml",
        ".ini", ".log", ".ps1", ".cmd", ".bat", ".py", ".js", ".ts", ".html", ".css", ".csv", ".sql" };

    void RenderPreview()
    {
        pic.Image = null;
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
                foreach (var c in Xg.ReadFolder(Xg.shell.NameSpace(it.Path))) n++;
                lines.Add(n + " 项");
            }
            catch { }
            lblPreview.Text = string.Join("\n", lines.ToArray());
            return;
        }

        // 1) 图片 / 视频 / PDF：走系统缩略图
        bool isText = TextExt.Contains(ext);
        Image img = null;
        if (!isText && it.Path != null && File.Exists(it.Path))
        {
            img = Xg.Thumbnail(it.Path, (int)(300 * scale));
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

        if (img == null)
        {
            try
            {
                int idx = it.IconLarge;
                var il = new ImageList();
                il.ImageSize = new Size(48, 48);
                il.ColorDepth = ColorDepth.Depth32Bit;
                // 用系统图标列表里的图标太大不易取，这里直接画一个占位图标
            }
            catch { }
        }

        lblPreview.Text = string.Join("\n", lines.ToArray());
    }
    void UpdateStatus()
    {
        int n = lv.Items.Count;
        int sel = lv.SelectedItems.Count;
        string s = n + " 项";
        if (sel == 1)
        {
            var it = lv.SelectedItems[0].Tag as Xg.Item;
            if (it != null) s += "   ·   " + it.Name + (it.IsFolder ? "  <目录>" : "   " + Xg.Human(it.Size));
        }
        else if (sel > 1) s += "   ·   已选 " + sel + " 项";
        lblStatus.Text = s;
    }

    void Msg(string s) { lblStatus.Text = "  " + s; }

    // ---------------------------------------------------------------- actions
    void DeleteSelected()
    {
        var sel = new List<Xg.Item>();
        foreach (ListViewItem row in lv.SelectedItems)
        {
            var it = row.Tag as Xg.Item;
            if (it != null) sel.Add(it);
        }
        if (sel.Count == 0) { Msg("没有选中任何项（Delete 只删除选中的）"); return; }

        var names = new List<string>();
        foreach (var it in sel.Take(4)) names.Add(it.Name);
        string head = sel.Count == 1 ? "删除这一项？" : "删除选中的 " + sel.Count + " 项？";
        if (MessageBox.Show(head + "\n\n" + string.Join("\n", names.ToArray()) + (sel.Count > 4 ? "\n…" : ""),
                "移到回收站", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes) return;

        int ok = 0, fail = 0;
        foreach (var it in sel)
        {
            try
            {
                if (Xg.IsFs(it.Path))
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
                else { it.Com.InvokeVerb("delete"); }
                ok++;
            }
            catch { fail++; }
        }
        Populate();
        Msg("已移到回收站: " + ok + (fail > 0 ? "，失败 " + fail : ""));
    }

    void OpenSelected()
    {
        if (lv.SelectedItems.Count == 0) return;
        var it = lv.SelectedItems[0].Tag as Xg.Item;
        if (it == null) return;
        if (it.IsFolder) { LoadFolder(it.Path); return; }
        try { Process.Start(it.Path); Msg("已打开 " + it.Name); }
        catch (Exception e) { Msg(e.Message); }
    }

    void LvKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; return; }

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
            string ch = e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z
                ? ((char)('a' + (e.KeyCode - Keys.A))).ToString()
                : ((char)('0' + (e.KeyCode - Keys.D0))).ToString();
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
        suppressHistory = true;
        LoadFolder("..");
        suppressHistory = false;
    }

    void RenameSelected()
    {
        if (lv.SelectedItems.Count != 1) { Msg("重命名只支持单个对象"); return; }
        var it = lv.SelectedItems[0].Tag as Xg.Item;
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
        if (lv.SelectedItems.Count == 0) { Msg("没有选中"); return; }
        var paths = new List<string>();
        foreach (ListViewItem row in lv.SelectedItems)
        {
            var it = row.Tag as Xg.Item;
            if (it != null && it.Path != null) paths.Add(it.Path);
        }
        if (paths.Count == 0) { Msg("选中项没有路径"); return; }
        try { Clipboard.SetText(string.Join("\r\n", paths.ToArray())); Msg("已复制 " + paths.Count + " 条路径"); }
        catch (Exception ex) { Msg(ex.Message); }
    }
    void ToggleView()
    {
        details = !details;
        lv.View = details ? View.Details : View.LargeIcon;
        AttachImageLists();
        Populate();
        Msg(details ? "明细视图" : "图标视图（F4 切换）");
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
        if (e.KeyCode == Keys.Tab) { Complete(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Escape) { txtCmd.Clear(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Up)
        {
            if (txtCmd.TextLength == 0 && lv.Items.Count > 0)
            {
                int i = lv.FocusedItem == null ? 0 : lv.FocusedItem.Index;
                if (i > 0) { lv.Items[i - 1].Selected = true; lv.Items[i - 1].Focused = true; lv.EnsureVisible(i - 1); }
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
                if (i < lv.Items.Count - 1) { lv.Items[i + 1].Selected = true; lv.Items[i + 1].Focused = true; lv.EnsureVisible(i + 1); }
            }
            else if (hIdx < hist.Count - 1) { hIdx++; txtCmd.Text = hist[hIdx]; txtCmd.SelectionStart = txtCmd.TextLength; }
            e.Handled = true;
            return;
        }
        // Backspace 只负责删字符；要上一级用 Alt+↑ / Ctrl+↑ / Backspace(在列表里)
        if (e.KeyCode == Keys.Up && e.Modifiers != Keys.None) { LoadFolder(".."); e.Handled = true; }
    }

    void Complete()
    {
        string text = txtCmd.Text;
        int start = text.LastIndexOf(' ') + 1;
        string token = text.Substring(start).ToLowerInvariant();
        var cands = new List<string>();
        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it == null) continue;
            if (it.Name.ToLowerInvariant().StartsWith(token)) cands.Add(it.IsFolder ? it.Name + "\\" : it.Name);
        }
        if (cands.Count == 0) { Msg("无匹配"); return; }
        cands.Sort(StringComparer.CurrentCultureIgnoreCase);
        if (cands.Count == 1)
        {
            txtCmd.Text = text.Substring(0, start) + cands[0];
            txtCmd.SelectionStart = txtCmd.TextLength;
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
            txtCmd.Text = text.Substring(0, start) + common;
            txtCmd.SelectionStart = txtCmd.TextLength;
        }
        Msg(cands.Count + " 个候选: " + string.Join("  ", cands.Take(8).ToArray()));
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

    void Run(string line)
    {
        var t = Tok(line);
        string v = t[0].ToLowerInvariant();
        string a1 = t.Count > 1 ? t[1] : null;

        switch (v)
        {
            case "ls": details = true; lv.View = View.Details; AttachImageLists(); Populate(); Msg("明细视图"); break;
            case "ll": details = true; lv.View = View.Details; AttachImageLists(); Populate(); Msg("明细视图"); break;
            case "ic": case "icons": details = false; lv.View = View.LargeIcon; AttachImageLists(); Populate(); Msg("图标视图"); break;
            case "cd": if (a1 == null) Msg(curPath); else LoadFolder(a1); break;
            case "pwd": Msg(curPath); break;
            case "up": LoadFolder(".."); break;
            case "back":
                if (back.Count == 0) { Msg("没有历史"); break; }
                { string p = back[back.Count - 1]; back.RemoveAt(back.Count - 1); LoadFolder(p); }
                break;
            case "mkdir": case "md": MkDir(a1); break;
            case "open": OpenNamed(a1); break;
            case "info": Info(a1); break;
            case "roots": Roots(); break;
            case "help": case "?": Msg("ls ll ic cd pwd up back mkdir open info roots exit   |   F4 切换视图  F5 刷新  Enter 打开"); break;
            case "exit": case "q": Close(); break;
            default: Msg("未知命令: " + v); break;
        }
    }

    void MkDir(string name)
    {
        if (name == null) { Msg("用法: mkdir 名字"); return; }
        if (!Xg.IsFs(curPath)) { Msg("当前是虚拟命名空间"); return; }
        string full = Path.Combine(curPath, name);
        if (Directory.Exists(full)) { Msg("已存在"); return; }
        Directory.CreateDirectory(full);
        Populate();
        foreach (ListViewItem row in lv.Items)
        {
            var it = row.Tag as Xg.Item;
            if (it != null && it.Name == name) { row.Selected = true; row.Focused = true; lv.EnsureVisible(row.Index); }
        }
        Msg("已创建 " + full);
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
                if (it.IsFolder) LoadFolder(it.Path);
                else { try { Process.Start(it.Path); Msg("已打开 " + it.Name); } catch (Exception e) { Msg(e.Message); } }
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

    void Roots()
    {
        // 用一个临时视图列出命名空间根：直接在列表里显示，回车进入
        var roots = new List<Xg.Item>();
        Action<string, string> add = delegate(string n, string p)
        {
            var o = new Xg.Item();
            o.Name = n; o.Path = p; o.IsFolder = true; o.Type = "命名空间";
            o.IconSmall = Xg.IconIndex(p, true, Xg.SHGFI_SMALLICON);
            o.IconLarge = Xg.IconIndex(p, true, Xg.SHGFI_LARGEICON);
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

        lv.BeginUpdate(); lv.Items.Clear();
        foreach (var it in roots)
        {
            var row = new ListViewItem(it.Name);
            row.SubItems.Add(""); row.SubItems.Add(it.Type); row.SubItems.Add("");
            row.ImageIndex = details ? it.IconSmall : it.IconLarge;
            row.Tag = it;
            lv.Items.Add(row);
        }
        lv.EndUpdate();
        lblPath.Text = "命名空间根   (命名空间)";
        Msg("回车进入选中的命名空间");
        FitColumns();
    }
}


