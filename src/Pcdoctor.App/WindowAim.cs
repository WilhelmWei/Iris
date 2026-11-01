using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pcdoctor.Ops;

namespace Pcdoctor.App;

/// <summary>
/// 窗口瞄准器：用户点「瞄准弹窗」后，Iris 最小化，屏幕盖一层半透明的遮罩，用户点一下弹窗，
/// 遮罩消失，读出那个位置的窗口属于哪个程序。不装鼠标钩子、不常驻，点完（或按 Esc、60 秒没点）就结束。
/// </summary>
static class WindowAim
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    /// <summary>让用户点一下要查的窗口；取消返回 IntPtr.Zero。</summary>
    public static Task<IntPtr> Pick(Form owner)
    {
        var tcs = new TaskCompletionSource<IntPtr>();
        var before = owner.WindowState == FormWindowState.Minimized ? FormWindowState.Normal : owner.WindowState;
        owner.WindowState = FormWindowState.Minimized;
        var vs = SystemInformation.VirtualScreen;
        var ov = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = vs, TopMost = true,
            ShowInTaskbar = false, BackColor = Color.Black, Opacity = 0.35, Cursor = Cursors.Cross, KeyPreview = true, Text = "Iris 窗口瞄准器",
        };
        var ps = Screen.PrimaryScreen?.Bounds ?? vs;
        var tip = new Label
        {
            AutoSize = false, Size = new Size(560, 64), Text = "点一下那个弹窗　·　按 Esc 取消", ForeColor = Color.White,
            BackColor = Color.FromArgb(24, 24, 24), Font = new Font("Microsoft YaHei UI", 16f), TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(ps.Left - vs.Left + (ps.Width - 560) / 2, ps.Top - vs.Top + 48), Cursor = Cursors.Cross,
        };
        ov.Controls.Add(tip);
        var timeout = new System.Windows.Forms.Timer { Interval = 60_000 };
        void Done(IntPtr h)
        {
            if (tcs.Task.IsCompleted) return;
            timeout.Stop(); timeout.Dispose();
            ov.Close(); ov.Dispose();
            owner.WindowState = before;
            owner.Activate();
            tcs.TrySetResult(h);
        }
        void Click(object? s, MouseEventArgs e)
        {
            var pt = Cursor.Position;
            ov.Hide();  // 先藏起遮罩，才能读到它下面的窗口
            Done(WindowFromPoint(new POINT { X = pt.X, Y = pt.Y }));
        }
        ov.MouseDown += Click;
        tip.MouseDown += Click;
        ov.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Done(IntPtr.Zero); };
        timeout.Tick += (_, _) => Done(IntPtr.Zero);
        // 等 Iris 最小化的动画走完再盖遮罩
        var show = new System.Windows.Forms.Timer { Interval = 350 };
        show.Tick += (_, _) => { show.Stop(); show.Dispose(); ov.Show(); ov.Activate(); timeout.Start(); };
        show.Start();
        return tcs.Task;
    }

    /// <summary>读出窗口属于哪个程序；点到 Iris 自己或读不到时返回 null。</summary>
    public static (WindowFacts facts, string title)? Inspect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (root == IntPtr.Zero) root = hwnd;
        GetWindowThreadProcessId(root, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        var sb = new StringBuilder(256);
        GetWindowText(root, sb, sb.Capacity);
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "未知程序"; }
        var path = ImagePath(pid);
        string? company = null, product = null, signer = null;
        if (path != null)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                company = string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
                product = string.IsNullOrWhiteSpace(v.FileDescription) ? (string.IsNullOrWhiteSpace(v.ProductName) ? null : v.ProductName.Trim()) : v.FileDescription.Trim();
            }
            catch { }
            signer = Signer(path);
        }
        return (new WindowFacts(name, path, company, product, signer, ParentName(pid)), sb.ToString());
    }

    static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>数字签名上写的是谁（只读名字，不在这里判断签名是否有效）。</summary>
    static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var c = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var m = System.Text.RegularExpressions.Regex.Match(c.Subject, @"CN=(""[^""]+""|[^,]+)");
            return m.Success ? m.Groups[1].Value.Trim('"') : c.Subject;
        }
        catch { return null; }
    }

    static string? ParentName(uint pid)
    {
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            uint parent = 0;
            var all = new Dictionary<uint, string>();
            if (!Process32FirstW(snap, ref e)) return null;
            do
            {
                all[e.th32ProcessID] = e.szExeFile;
                if (e.th32ProcessID == pid) parent = e.th32ParentProcessID;
            } while (Process32NextW(snap, ref e));
            return parent != 0 && all.TryGetValue(parent, out var n) ? n : null;
        }
        finally { CloseHandle(snap); }
    }
}
