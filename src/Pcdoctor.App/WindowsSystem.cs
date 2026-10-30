using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Pcdoctor.Ops;

namespace Pcdoctor.App;

/// <summary>IWinSystem 的真实实现。只用不随系统语言变化的接口：注册表、退出码、任务计划程序 COM 返回的 XML。</summary>
public sealed class WindowsSystem(string tempDir) : IWinSystem
{
    public DateTime UtcNow => DateTime.UtcNow;
    public string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string? Env(string name) => Environment.GetEnvironmentVariable(name);
    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);
    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

    // ------------------------------------------------------------------ 注册表
    static RegistryKey Base(Hive h) => RegistryKey.OpenBaseKey(h == Hive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    static RegValue? ToValue(RegistryKey k, string name)
    {
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v == null) return null;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegValue("DWord", JsonValue.Create((int)v)),
            RegistryValueKind.QWord => new RegValue("QWord", JsonValue.Create((long)v)),
            RegistryValueKind.String => new RegValue("String", JsonValue.Create((string)v)),
            RegistryValueKind.ExpandString => new RegValue("ExpandString", JsonValue.Create((string)v)),
            RegistryValueKind.MultiString => new RegValue("MultiString", new JsonArray(((string[])v).Select(x => (JsonNode?)x).ToArray())),
            RegistryValueKind.Binary => RegValue.Binary((byte[])v),
            _ => new RegValue("Binary", JsonValue.Create(Convert.ToBase64String(v as byte[] ?? []))),
        };
    }

    public RegValue? ReadValue(Hive hive, string key, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        return k == null ? null : ToValue(k, name);
    }

    public void WriteValue(Hive hive, string key, string name, RegValue value)
    {
        using var b = Base(hive);
        using var k = b.CreateSubKey(key, writable: true);
        switch (value.Kind)
        {
            case "DWord": k.SetValue(name, (int)Operations.Int(value.Data)!.Value, RegistryValueKind.DWord); break;
            case "QWord": k.SetValue(name, Operations.Int(value.Data)!.Value, RegistryValueKind.QWord); break;
            case "String": k.SetValue(name, value.AsStrings().FirstOrDefault() ?? "", RegistryValueKind.String); break;
            case "ExpandString": k.SetValue(name, value.AsStrings().FirstOrDefault() ?? "", RegistryValueKind.ExpandString); break;
            case "MultiString": k.SetValue(name, value.AsStrings(), RegistryValueKind.MultiString); break;
            default: k.SetValue(name, value.AsBytes() ?? [], RegistryValueKind.Binary); break;
        }
    }

    public void DeleteValue(Hive hive, string key, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool KeyExists(Hive hive, string key)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        return k != null;
    }

    public IReadOnlyList<string> SubKeys(Hive hive, string key)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        try { return k?.GetSubKeyNames() ?? []; } catch { return []; }
    }

    public RegTree? ExportTree(Hive hive, string key)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        return k == null ? null : Export(k);
    }

    static RegTree Export(RegistryKey k)
    {
        var t = new RegTree();
        foreach (var n in k.GetValueNames()) if (ToValue(k, n) is { } v) t.Values[n] = v;
        foreach (var s in k.GetSubKeyNames())
        {
            try { using var sk = k.OpenSubKey(s); if (sk != null) t.Keys[s] = Export(sk); }
            catch { /* 没有权限的子项（如 Security）跳过 */ }
        }
        return t;
    }

    public void ImportTree(Hive hive, string key, RegTree tree)
    {
        foreach (var (n, v) in tree.Values) WriteValue(hive, key, n, v);
        if (tree.Values.Count == 0) { using var b = Base(hive); b.CreateSubKey(key)?.Dispose(); }
        foreach (var (s, sub) in tree.Keys) ImportTree(hive, key + "\\" + s, sub);
    }

    // ------------------------------------------------------------------ 进程
    public int Run(string exe, string arguments, int timeoutSec = 120)
    {
        // 不带路径的系统程序一律用 System32 里的：Windows 找程序时会先看 Iris.exe 所在目录（常常是"下载"文件夹），
        // 那里放一个同名的 sc.exe 就会被以管理员身份运行
        if (!Path.IsPathRooted(exe))
            exe = exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ? Helper.PowerShellExe : Path.Combine(Environment.SystemDirectory, exe);
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // 需要管理员的卸载程序：交给系统弹出管理员确认
            p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true });
        }
        if (p == null) return -1;
        using (p)
        {
            if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return -1; }
            return p.ExitCode;
        }
    }

    public string? ServiceState(string name)
    {
        try { using var sc = new ServiceController(name); return sc.Status.ToString(); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 计划任务（任务计划程序 COM：XML 不随系统语言变化）
    static dynamic Scheduler()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("任务计划程序不可用");
        dynamic s = Activator.CreateInstance(t)!;
        s.Connect();
        return s;
    }

    static (string folder, string name) Split(string fullName)
    {
        var i = fullName.LastIndexOf('\\');
        return (i <= 0 ? "\\" : fullName[..i], fullName[(i + 1)..]);
    }

    static dynamic? GetTask(string fullName)
    {
        var (f, n) = Split(fullName);
        try { return Scheduler().GetFolder(f).GetTask(n); } catch { return null; }
    }

    public string? ExportTaskXml(string fullName) { var t = GetTask(fullName); return t == null ? null : (string)t.Xml; }

    public bool SetTaskEnabled(string fullName, bool enabled)
    {
        var t = GetTask(fullName);
        if (t == null) return false;
        t.Enabled = enabled;
        return true;
    }

    public bool DeleteTask(string fullName)
    {
        var (f, n) = Split(fullName);
        try { Scheduler().GetFolder(f).DeleteTask(n, 0); return true; } catch { return false; }
    }

    public bool RegisterTaskXml(string fullName, string xml)
    {
        // 用 schtasks 按原样的 XML 注册（含原来的运行账户设置），只看退出码
        var tmp = Path.Combine(tempDir, "pcdoctor-task-" + Guid.NewGuid().ToString("N") + ".xml");  // 管理员时是受保护目录
        try
        {
            File.WriteAllText(tmp, xml, new System.Text.UnicodeEncoding(false, true));
            return Run("schtasks.exe", $"/Create /TN \"{fullName}\" /XML \"{tmp}\" /F") == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ------------------------------------------------------------------ 文件
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<FileEntry> EnumerateFiles(string root, bool recurse = true)
    {
        // 不跟随联接点和符号链接：防止从缓存目录"跳"到别的地方
        var opt = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opt); } catch { yield break; }
        foreach (var f in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(f); } catch { continue; }
            yield return new FileEntry(f, fi.Length, fi.LastWriteTimeUtc);
        }
    }

    public IReadOnlyList<string> ListDirectories(string dir)
    {
        try
        {
            var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateDirectories(dir, "*", opt).ToList();
        }
        catch { return []; }
    }

    public bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }  // 读不到属性的，当作不能进入
    }

    public IReadOnlyList<string> DeleteFiles(IReadOnlyList<string> paths)
    {
        var done = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                done.Add(p);
            }
            catch { /* 正在使用或没有权限：跳过 */ }
        }
        return done;
    }

    public bool IsProcessRunning(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400,
                 FOF_NO_CONNECTED_ELEMENTS = 0x2000, FOF_WANTNUKEWARNING = 0x4000;

    public IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths)
    {
        var list = paths.Where(File.Exists).ToList();
        if (list.Count == 0) return [];
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = string.Join("\0", list) + "\0\0",
            // ALLOWUNDO = 移到回收站；WANTNUKEWARNING = 回收站关闭或文件太大放不进去时先警告，不悄悄永久删除（红线 6）；
            // NO_CONNECTED_ELEMENTS = 不连带删除"网页 + _files 文件夹"这类关联文件
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NO_CONNECTED_ELEMENTS | FOF_WANTNUKEWARNING,
        };
        SHFileOperation(ref op);
        return list.Where(p => !File.Exists(p)).ToList();
    }
}
