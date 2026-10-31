using Pcdoctor.Ops;

namespace Pcdoctor.Ops.Tests;

/// <summary>内存里的"Windows"：注册表、计划任务、服务状态、文件。Run() 模拟 sc.exe / pnputil.exe 的效果，
/// 并可以注入故障（命令没生效、文件被占用、驱动在用），用来测试验证失败时的自动回滚。</summary>
public sealed class FakeWinSystem : IWinSystem
{
    readonly Dictionary<(Hive, string), Dictionary<string, RegValue>> reg = new();
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ServiceStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RecycleBin { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DriversInUse { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ReadOnlyKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ScIgnores { get; set; }
    public List<string> Commands { get; } = [];
    public Func<string, string, int>? OnRun { get; set; }

    public DateTime UtcNow { get; set; } = new(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
    public string WindowsDir => @"C:\Windows";
    public Dictionary<string, string> EnvVars { get; } = new() { ["TEMP"] = @"C:\Users\u\AppData\Local\Temp", ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local" };
    public string? Env(string name) => EnvVars.GetValueOrDefault(name);
    public string Expand(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase).Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase);
    public int Slept { get; private set; }
    public Action? OnSleep { get; set; }
    public void Sleep(int milliseconds) { Slept += milliseconds; OnSleep?.Invoke(); }
    public bool RecycleBinDisabled { get; set; }

    static string N(string k) => k.TrimEnd('\\');
    Dictionary<string, RegValue>? Key(Hive h, string k) => reg.GetValueOrDefault((h, N(k).ToLowerInvariant()));

    public void Set(Hive h, string k, string name, RegValue v)
    {
        var key = (h, N(k).ToLowerInvariant());
        if (!reg.TryGetValue(key, out var d)) reg[key] = d = new(StringComparer.OrdinalIgnoreCase);
        d[name] = v;
    }
    public void CreateKey(Hive h, string k) { var key = (h, N(k).ToLowerInvariant()); if (!reg.ContainsKey(key)) reg[key] = new(StringComparer.OrdinalIgnoreCase); }
    public static RegValue Str(string s) => new("String", System.Text.Json.Nodes.JsonValue.Create(s));
    public static RegValue Multi(params string[] s) => new("MultiString", new System.Text.Json.Nodes.JsonArray(s.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()));

    public RegValue? ReadValue(Hive hive, string key, string name) => Key(hive, key)?.GetValueOrDefault(name);
    public void WriteValue(Hive hive, string key, string name, RegValue value)
    {
        if (ReadOnlyKeys.Contains(N(key))) return;  // 模拟"系统拒绝写入"（不报错，但没生效）
        Set(hive, key, name, value);
    }
    public void DeleteValue(Hive hive, string key, string name) => Key(hive, key)?.Remove(name);
    public bool KeyExists(Hive hive, string key) => Key(hive, key) != null;
    public IReadOnlyList<string> SubKeys(Hive hive, string key)
    {
        var prefix = N(key).ToLowerInvariant() + "\\";
        return reg.Keys.Where(k => k.Item1 == hive && k.Item2.StartsWith(prefix))
            .Select(k => k.Item2[prefix.Length..].Split('\\')[0]).Distinct().ToList();
    }
    public RegTree? ExportTree(Hive hive, string key)
    {
        if (!KeyExists(hive, key)) return null;
        var t = new RegTree();
        foreach (var (n, v) in Key(hive, key)!) t.Values[n] = v;
        foreach (var sub in SubKeys(hive, key)) t.Keys[sub] = ExportTree(hive, key + "\\" + sub)!;
        return t;
    }
    public void ImportTree(Hive hive, string key, RegTree tree)
    {
        CreateKey(hive, key);
        foreach (var (n, v) in tree.Values) Set(hive, key, n, v);
        foreach (var (sub, t) in tree.Keys) ImportTree(hive, key + "\\" + sub, t);
    }
    void DeleteTree(Hive hive, string key)
    {
        var k = N(key).ToLowerInvariant();
        foreach (var x in reg.Keys.Where(x => x.Item1 == hive && (x.Item2 == k || x.Item2.StartsWith(k + "\\"))).ToList()) reg.Remove(x);
    }

    public int Run(string exe, string arguments, int timeoutSec = 120)
    {
        Commands.Add($"{exe} {arguments}");
        if (OnRun != null) return OnRun(exe, arguments);
        var a = System.Text.RegularExpressions.Regex.Matches(arguments, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        if (exe == "sc.exe")
        {
            if (ScIgnores) return 0;  // 命令"成功"了，但没生效
            var svc = @"SYSTEM\CurrentControlSet\Services\" + a[1];
            if (a[0] == "config")
            {
                var start = a[3] switch { "auto" => 2, "delayed-auto" => 2, "demand" => 3, "disabled" => 4, _ => 3 };
                Set(Hive.LocalMachine, svc, "Start", RegValue.DWord(start));
                if (a[3] == "delayed-auto") Set(Hive.LocalMachine, svc, "DelayedAutostart", RegValue.DWord(1));
                else DeleteValue(Hive.LocalMachine, svc, "DelayedAutostart");
                return 0;
            }
            if (a[0] == "delete") { DeleteTree(Hive.LocalMachine, svc); return 0; }
        }
        if (exe == "pnputil.exe")
        {
            if (a[0] == "/export-driver") { Directory.CreateDirectory(a[2]); File.WriteAllText(Path.Combine(a[2], "x.inf"), "inf"); return 0; }
            if (a[0] == "/delete-driver")
            {
                if (DriversInUse.Contains(a[1])) return 1;
                Files.Remove(WindowsDir + @"\INF\" + a[1]); return 0;
            }
            if (a[0] == "/add-driver") { Files[WindowsDir + @"\INF\oem99.inf"] = new(WindowsDir + @"\INF\oem99.inf", 1, UtcNow); return 0; }
        }
        return 0;
    }

    public string? ServiceState(string name) => KeyExists(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + name)
        ? ServiceStates.GetValueOrDefault(name, "Stopped") : null;

    public string? ExportTaskXml(string fullName) => Tasks.GetValueOrDefault(fullName);
    public bool SetTaskEnabled(string fullName, bool enabled)
    {
        if (!Tasks.ContainsKey(fullName)) return false;
        Tasks[fullName] = TaskXmlOf(enabled); return true;
    }
    public bool DeleteTask(string fullName) => Tasks.Remove(fullName);
    public bool RegisterTaskXml(string fullName, string xml) { Tasks[fullName] = xml; return true; }
    public static string TaskXmlOf(bool enabled, string command = @"D:\Rambo\client\ramboplay.ra2.exe", string logonType = "InteractiveToken") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal><LogonType>{logonType}</LogonType></Principal></Principals><Settings><Enabled>{(enabled ? "true" : "false")}</Enabled></Settings><Actions><Exec><Command>\"{command}\"</Command></Exec></Actions></Task>";

    public bool FileExists(string path) => Files.ContainsKey(path);
    public bool DirectoryExists(string path) => Files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                                                || Dirs.Contains(path.TrimEnd('\\'));
    /// <summary>空文件夹（有文件的文件夹由文件路径隐含）。</summary>
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>模拟的联接点 / 符号链接文件夹：列文件夹时看不到，递归列文件时不进入。</summary>
    public HashSet<string> ReparseDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
    static bool Under(string path, string dir) => path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public IEnumerable<FileEntry> EnumerateFiles(string root, bool recurse = true, bool includeSystem = false) =>
        Files.Values.Where(f => Under(f.Path, root) && (recurse || !f.Path[(root.TrimEnd('\\').Length + 1)..].Contains('\\'))
                                && !ReparseDirs.Any(r => Under(f.Path, r) && Under(r, root))).ToList();
    public IReadOnlyList<string> ListDirectories(string dir) =>
        Files.Keys.Concat(Dirs).Where(f => Under(f, dir)).Select(f => dir.TrimEnd('\\') + "\\" + f[(dir.TrimEnd('\\').Length + 1)..].Split('\\')[0])
            .Where(d => (Dirs.Contains(d) || Files.Keys.Concat(Dirs).Any(f => Under(f, d))) && !ReparseDirs.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public bool IsReparsePoint(string path) => ReparseDirs.Contains(path.TrimEnd('\\'));
    public IReadOnlyList<string> DeleteFiles(IReadOnlyList<string> paths)
    {
        var done = new List<string>();
        foreach (var p in paths)
            if (!Locked.Contains(p) && Files.Remove(p)) { Deleted.Add(p); done.Add(p); }
        return done;
    }
    public bool IsProcessRunning(string name) => Running.Contains(name);
    public IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths)
    {
        if (RecycleBinDisabled) return [];  // 真实系统会先弹出"将永久删除"的警告；这里模拟用户选了"否"
        var moved = new List<string>();
        foreach (var p in paths)
            if (!Locked.Contains(p) && Files.Remove(p)) { RecycleBin.Add(p); moved.Add(p); }
        return moved;
    }
    public void AddFile(string path, long size, int daysOld) => Files[path] = new(path, size, UtcNow.AddDays(-daysOld));

    public FileEntry? GetFile(string path) => Files.GetValueOrDefault(path);

    /// <summary>读不出来的文件（被别的程序独占打开）：复制会失败。</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>复制完一个文件后调用（模拟"搬的过程中别的程序改了原件"）。</summary>
    public Action<string>? OnCopied { get; set; }
    public int CopyCount { get; private set; }
    public bool CopyFile(string source, string destination)
    {
        if (Unreadable.Contains(source) || !Files.TryGetValue(source, out var f) || Files.ContainsKey(destination)) return false;
        Files[destination] = f with { Path = destination };
        CopyCount++;
        OnCopied?.Invoke(source);
        return true;
    }
    public bool MoveDirectory(string source, string destination)
    {
        var s = source.TrimEnd('\\');
        if (!DirectoryExists(s) || DirectoryExists(destination) || Files.Keys.Any(f => Under(f, s) && Locked.Contains(f))) return false;
        foreach (var f in Files.Keys.Where(f => Under(f, s)).ToList()) { var e = Files[f]; Files.Remove(f); Files[destination + f[s.Length..]] = e with { Path = destination + f[s.Length..] }; }
        foreach (var d in Dirs.Where(d => d.Equals(s, StringComparison.OrdinalIgnoreCase) || Under(d, s)).ToList()) { Dirs.Remove(d); Dirs.Add(destination + d[s.Length..]); }
        return true;
    }
    public void CreateDirectory(string path) { if (!DirectoryExists(path)) Dirs.Add(path.TrimEnd('\\')); }
    public void DeleteEmptyDirectories(string root)
    {
        // 有文件的文件夹由文件路径隐含，删完文件自然就"没有了"；这里只需要处理空文件夹
        var r = root.TrimEnd('\\');
        foreach (var d in Dirs.Where(d => d.Equals(r, StringComparison.OrdinalIgnoreCase) || Under(d, r)).OrderByDescending(d => d.Length).ToList())
            if (!Files.Keys.Any(f => Under(f, d)) && !Dirs.Any(x => Under(x, d))) Dirs.Remove(d);
    }
    public Dictionary<string, string> KnownFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RefuseKnownFolder { get; set; }
    public string? GetKnownFolder(string guid) => KnownFolders.GetValueOrDefault(guid);
    public bool SetKnownFolder(string guid, string path)
    {
        if (RefuseKnownFolder) return false;
        KnownFolders[guid] = path; return true;
    }
    public List<DriveFact> DriveList { get; } = [];
    public IReadOnlyList<DriveFact> Drives() => DriveList;
}
