using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Pcdoctor.App;

/// <summary>启动自己的 --helper 模式。
/// 提权：直接调用 ShellExecuteEx（带主窗口句柄，UAC 弹窗在前台；带 NOZONECHECKS，从网上下载的未签名 exe 不会每次都被安全警告拦住）。
/// 不提权：用 CreateProcess（不做区域检查）。</summary>
public sealed class HelperProcess : IDisposable
{
    readonly IntPtr handle;
    readonly Process? process;

    HelperProcess(IntPtr h) { handle = h; }
    HelperProcess(Process p) { process = p; }

    public bool HasExited => process?.HasExited ?? WaitForSingleObject(handle, 0) == 0;

    public void Dispose()
    {
        process?.Dispose();
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    /// <summary>用户在 UAC 里点了"否"时返回 null。</summary>
    public static HelperProcess? Start(string verb, string dir, bool elevate, IntPtr owner, params string[] extra)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var args = $"--helper {verb} \"{dir}\" --sid {sid} " + string.Join(' ', extra.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var exe = Environment.ProcessPath!;
        if (!elevate || Environment.IsPrivilegedProcess)
            return new HelperProcess(Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!);

        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOZONECHECKS | SEE_MASK_NOASYNC,
            hwnd = owner, lpVerb = "runas", lpFile = exe, lpParameters = args, nShow = 0,
        };
        bool ok = false; int err = 0;
        void Call() { ok = ShellExecuteEx(ref info); err = ok ? 0 : Marshal.GetLastWin32Error(); }
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) Call();
        else { var t = new Thread(Call); t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(); }  // 外壳调用要求 STA 线程
        if (!ok)
        {
            if (err == 1223) return null;  // ERROR_CANCELLED：用户点了"否"
            throw new Win32Exception(err);
        }
        return new HelperProcess(info.hProcess);
    }

    const uint SEE_MASK_NOCLOSEPROCESS = 0x40, SEE_MASK_NOASYNC = 0x100, SEE_MASK_NOZONECHECKS = 0x800000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string? lpVerb; public string lpFile; public string? lpParameters; public string? lpDirectory;
        public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string? lpClass; public IntPtr hkeyClass;
        public uint dwHotKey; public IntPtr hIconOrMonitor; public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
