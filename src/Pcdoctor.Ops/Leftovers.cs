using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Ops;

/// <summary>
/// 系统升级和更新留下的文件交给 Windows 自带的磁盘清理（cleanmgr）删除：Windows.old 这类目录的权限很特殊，
/// 自己删容易删一半；磁盘清理知道哪些能删、怎么删。做法是给选中的清理项写上 StateFlags7421，再运行 cleanmgr /sagerun:7421，
/// 结束后把 StateFlags 恢复原样。清理项的名字（注册表键名）不随系统语言变化。
/// </summary>
public static class SystemCleanup
{
    public const int SageSet = 7421;
    public const string Flag = "StateFlags7421";
    public const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public sealed record Item(string Id, string Name, string[] Handlers, string Why, string Cost, bool ByDefault);

    /// <summary>可以交给磁盘清理的项目。Id 和清理目录表（kb/junk.json）里 B 类的编号一致，体检时量到的大小按 Id 对上。</summary>
    public static readonly Item[] Items =
    [
        new("windows-old", "旧版 Windows（Windows.old）", ["Previous Installations"],
            "升级 Windows 大版本后留下的旧系统，用来回退。", "删除后就不能退回升级前的 Windows 版本。", true),
        new("windows-upgrade-temp", "升级临时文件和日志", ["Temporary Setup Files", "Windows Upgrade Log Files"],
            "Windows 升级时下载和展开的安装文件，升级完成后就没用了。", "没有代价；如果升级还没完成，Windows 会重新下载。", true),
        new("delivery-optimization", "传递优化缓存", ["Delivery Optimization Files"],
            "Windows 更新时为了给局域网里其他电脑分享而留的副本。", "没有代价。", true),
        new("update-cleanup", "被替换掉的旧系统更新", ["Update Cleanup"],
            "装了新的系统更新以后，被替换掉的旧组件还留在系统里。体检时量不出能腾出多少。", "清理后不能再卸载这些已经被替换的更新；可能要十几分钟。", false),
    ];

    public static Item? Get(string id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>处理方案里这一步的选项：体检时量到 100 MB 以上的，加上量不出大小的"旧系统更新"（默认不勾）。</summary>
    public static CacheOptions Options(JsonNode report)
    {
        var measured = (report["sections"]?["storage"]?["junk"]?["items"] as JsonArray)?.OfType<JsonObject>()
            .Where(x => x["id"] != null).ToDictionary(x => x["id"]!.ToString(), x => x) ?? [];
        var items = new List<CacheItem>();
        foreach (var it in Items)
        {
            double? mb = measured.TryGetValue(it.Id, out var m) ? Operations.Num(m["sizeMB"]) ?? Operations.Num(m["cleanableMB"]) : null;
            if (it.Id != "update-cleanup" && !(mb >= 100)) continue;
            items.Add(new CacheItem { Id = it.Id, Name = it.Name, Group = "Windows 自带的磁盘清理", SizeMB = mb, Why = it.Why, Cost = it.Cost });
        }
        return new CacheOptions
        {
            Items = items, Ages = [],
            Categories = items.Where(i => Get(i.Id)!.ByDefault).Select(i => i.Id).ToList(),
        };
    }
}

public sealed class SystemCleanupOp : Operation
{
    public override string Id => "system.cleanup";

    static string Exe(IWinSystem sys) => sys.WindowsDir + @"\System32\cleanmgr.exe";

    static long? Free(IWinSystem sys) =>
        sys.Drives().FirstOrDefault(d => d.Root.Equals(FolderMove.SystemDrive(sys), StringComparison.OrdinalIgnoreCase))?.FreeBytes;

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var ch = s.CacheChoice;
        if (ch == null || ch.Categories.Count == 0) return "没有选择要清理的项目，什么也没做";
        if (ch.Categories.Any(id => SystemCleanup.Get(id) == null)) return "选择了不支持的项目";
        if (!c.Sys.FileExists(Exe(c.Sys))) return "这台电脑上没有 Windows 自带的磁盘清理（cleanmgr.exe）";
        var handlers = ch.Categories.SelectMany(id => SystemCleanup.Get(id)!.Handlers)
            .Where(h => c.Sys.KeyExists(Hive.LocalMachine, SystemCleanup.VolumeCaches + "\\" + h)).ToList();
        return handlers.Count == 0 ? "这台电脑上的磁盘清理没有这些项目（可能已经被 Windows 清理过了）" : null;
    }

    /// <summary>记下所有清理项上原来的 StateFlags7421（一般都没有），结束后原样恢复。</summary>
    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var flags = new JsonObject();
        foreach (var h in c.Sys.SubKeys(Hive.LocalMachine, SystemCleanup.VolumeCaches))
            if (c.Sys.ReadValue(Hive.LocalMachine, SystemCleanup.VolumeCaches + "\\" + h, SystemCleanup.Flag) is { } v) flags[h] = v.ToJson();
        b["flags"] = flags;
        b["freeBefore"] = Free(c.Sys);
    }

    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var sys = c.Sys;
        var chosen = s.CacheChoice!.Categories.SelectMany(id => SystemCleanup.Get(id)!.Handlers).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 只让选中的项目带上这组标记：别的项目上同名的标记先去掉，免得多清
            foreach (var h in sys.SubKeys(Hive.LocalMachine, SystemCleanup.VolumeCaches))
            {
                var key = SystemCleanup.VolumeCaches + "\\" + h;
                if (chosen.Contains(h)) sys.WriteValue(Hive.LocalMachine, key, SystemCleanup.Flag, RegValue.DWord(2));
                else sys.DeleteValue(Hive.LocalMachine, key, SystemCleanup.Flag);
            }
            c.Progress?.Invoke("Windows 磁盘清理正在运行，会弹出它自己的进度窗口…");
            b["exit"] = sys.Run(Exe(sys), $"/sagerun:{SystemCleanup.SageSet}", 3600);
        }
        finally { RestoreFlags(sys, b); }
    }

    static void RestoreFlags(IWinSystem sys, JsonObject b)
    {
        var saved = b["flags"] as JsonObject ?? [];
        foreach (var h in sys.SubKeys(Hive.LocalMachine, SystemCleanup.VolumeCaches))
        {
            var key = SystemCleanup.VolumeCaches + "\\" + h;
            if (RegValue.FromJson(saved[h]) is { } v) sys.WriteValue(Hive.LocalMachine, key, SystemCleanup.Flag, v);
            else sys.DeleteValue(Hive.LocalMachine, key, SystemCleanup.Flag);
        }
    }

    /// <summary>结果以 C 盘多出来的空间为准；选了 Windows.old 的，再看它还在不在。只用于告诉用户，不算失败。</summary>
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        var before = Operations.Int(b["freeBefore"]);
        var after = Free(c.Sys);
        if (before != null && after != null) b["freed"] = Math.Max(0, after.Value - before.Value);
        if (s.CacheChoice!.Categories.Contains("windows-old"))
            b["oldLeft"] = c.Sys.DirectoryExists(FolderMove.SystemDrive(c.Sys) + @"\Windows.old");
        return null;
    }

    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) => RestoreFlags(c.Sys, b);

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b)
    {
        var msg = b["freed"] is { } f ? $"Windows 磁盘清理已完成，C 盘多出了 {CacheClean.Size(Operations.Int(f) ?? 0)}" : "Windows 磁盘清理已完成";
        if (b["oldLeft"]?.GetValue<bool>() == true) msg += "。Windows.old 还在：可能有文件正在使用，重启后可以再清一次";
        return msg;
    }

    public override Outcome Undo(JournalEntry e, OpContext ctx) => Outcome.Fail("这些文件已经由 Windows 磁盘清理删除，不能撤销");
}

/// <summary>下载文件夹里很久以前的安装包：只认安装程序的扩展名，移到回收站（红线 6：不永久删除用户文件）。</summary>
public static class OldInstallers
{
    public static readonly string[] Extensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"];
    public const int MinAgeDays = 30;
    public const int MaxItems = 200;
    const string DownloadsGuid = "374DE290-123F-4565-9164-39C4925E467B";

    public static string? Root(IWinSystem sys) => sys.GetKnownFolder(DownloadsGuid);

    static bool IsInstaller(string path) => Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>现在下载文件夹里符合条件的安装包，大的在前。</summary>
    public static List<FileEntry> Find(IWinSystem sys)
    {
        var root = Root(sys);
        if (string.IsNullOrEmpty(root) || !sys.DirectoryExists(root)) return [];
        var cutoff = sys.UtcNow.AddDays(-MinAgeDays);
        return sys.EnumerateFiles(root, true).Where(f => IsInstaller(f.Path) && f.LastWriteUtc < cutoff)
            .OrderByDescending(f => f.Size).Take(MaxItems).ToList();
    }

    static string Leaf(string p) => p[(p.LastIndexOf('\\') + 1)..];

    /// <summary>处理方案里列出每个文件（当场列，不来自报告）；默认全选。</summary>
    public static CacheOptions Options(IWinSystem sys)
    {
        var now = sys.UtcNow;
        var items = Find(sys).Select(f => new CacheItem
        {
            Id = f.Path, Name = Leaf(f.Path), Group = "下载文件夹", SizeMB = Math.Round(f.Size / 1048576.0, 1),
            Why = $"{(int)(now - f.LastWriteUtc).TotalDays} 天前下载", Cost = "",
        }).ToList();
        return new CacheOptions { Items = items, Categories = items.Select(i => i.Id).ToList(), Ages = [] };
    }
}

public sealed class InstallersRecycleOp : Operation
{
    public override string Id => "downloads.recycle_installers";

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var ch = s.CacheChoice;
        if (ch == null || ch.Categories.Count == 0) return "没有选择要移走的安装包，什么也没做";
        // 方案里的路径要在"现在的下载文件夹里、符合条件的安装包"之内：不能借这一步移走别的文件
        var allowed = OldInstallers.Find(c.Sys).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ch.Categories.All(allowed.Contains) ? null : "有的文件已经不在下载文件夹里，或者不是安装包，请重新打开「处理」页再试";
    }

    protected override void Backup(PlanStep s, OpContext c, JsonObject b) { }

    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var sizes = OldInstallers.Find(c.Sys).ToDictionary(f => f.Path, f => f.Size, StringComparer.OrdinalIgnoreCase);
        var moved = c.Sys.MoveToRecycleBin(s.CacheChoice!.Categories);
        b["moved"] = moved.Count;
        b["bytes"] = moved.Sum(p => sizes.GetValueOrDefault(p));
        b["left"] = s.CacheChoice.Categories.Count - moved.Count;
    }

    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        (Operations.Int(b["moved"]) ?? 0) > 0 ? null : "一个文件也没有移走（可能回收站关着，或者你在 Windows 的提示里选了「否」）";

    protected override bool ChangesOnlyOnSuccess => false;
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) { }

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b)
    {
        var msg = $"已把 {Operations.Int(b["moved"])} 个旧安装包（{CacheClean.Size(Operations.Int(b["bytes"]) ?? 0)}）移到回收站。"
                  + "确认不需要后清空回收站，才真正腾出空间";
        var left = Operations.Int(b["left"]) ?? 0;
        if (left > 0) msg += $"；{left} 个正在使用，没有移走";
        return msg;
    }

    public override Outcome Undo(JournalEntry e, OpContext ctx) => Outcome.Fail("请打开回收站，选中这些文件后点「还原选定的项目」");
}

/// <summary>
/// 卸载残留的文件夹：Program Files 下面、里面已经没有程序文件、也没有已装软件和服务用到它。
/// 不删除，挪到 Iris 的受保护备份目录（同一个盘，只是改个位置），可以撤销；之后随"清理旧备份"一起释放。
/// </summary>
public static class LeftoverDirs
{
    /// <summary>程序文件：有这些就不算残留（可能是免安装软件或还在用的组件）。</summary>
    public static readonly string[] ProgramExtensions = [".exe", ".dll", ".sys", ".com", ".scr", ".ocx", ".cpl", ".msi", ".drv", ".efi"];

    /// <summary>Windows 和常见运行库自带的目录，不管里面有什么都不动。</summary>
    static readonly Regex Protected = new(@"^(Common Files|Microsoft.*|Windows.*|WindowsApps|ModifiableWindowsApps|Internet Explorer|dotnet|MSBuild|Reference Assemblies|Uninstall Information|WindowsPowerShell|PowerShell|Package Cache|pcdoctor.*|Iris.*|Intel|AMD|NVIDIA.*|Realtek)$",
        RegexOptions.IgnoreCase);

    static readonly Regex Shape = new(@"^[A-Za-z]:\\Program Files( \(x86\))?\\([^\\]+)$");

    /// <summary>路径必须是"某个盘的 Program Files 下面一层"，而且不是受保护的目录。返回 null 表示形状合法。</summary>
    public static string? PathProblem(string path)
    {
        var m = Shape.Match(path);
        if (!m.Success || path.Contains("..")) return "只处理 Program Files 下面一层的文件夹";
        return Protected.IsMatch(m.Groups[2].Value) ? "这是 Windows 或常见运行库自带的文件夹，不动" : null;
    }

    static bool IsProgram(string path) => ProgramExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>已装软件和服务登记里出现过的路径（小写），用来确认没有人在用这个文件夹。</summary>
    static IEnumerable<string> References(IWinSystem sys)
    {
        foreach (var (hive, root) in new[]
                 {
                     (Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Hive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
            foreach (var k in sys.SubKeys(hive, root))
                foreach (var name in new[] { "InstallLocation", "UninstallString", "DisplayIcon", "QuietUninstallString" })
                    if (sys.ReadValue(hive, root + "\\" + k, name)?.AsStrings().FirstOrDefault() is { Length: > 0 } v) yield return sys.Expand(v);
        const string svc = @"SYSTEM\CurrentControlSet\Services";
        foreach (var k in sys.SubKeys(Hive.LocalMachine, svc))
            if (sys.ReadValue(Hive.LocalMachine, svc + "\\" + k, "ImagePath")?.AsStrings().FirstOrDefault() is { Length: > 0 } v) yield return sys.Expand(v);
    }

    /// <summary>执行前现场再查一遍：在、不是链接、没有程序文件、没有被已装软件或服务引用。返回 null 表示是残留。</summary>
    public static string? NotLeftover(IWinSystem sys, string path)
    {
        if (PathProblem(path) is { } p) return p;
        if (!path.StartsWith(FolderMove.SystemDrive(sys) + "\\", StringComparison.OrdinalIgnoreCase)) return "只处理系统盘上的文件夹";
        if (!sys.DirectoryExists(path)) return "这个文件夹已经不在了";
        if (sys.IsReparsePoint(path)) return "这是一个链接，不动";
        if (sys.EnumerateFiles(path, true, includeSystem: true).Any(f => IsProgram(f.Path))) return "里面还有程序文件，可能还在用，不动";
        var needle = path.TrimEnd('\\') + "\\";
        if (References(sys).Any(r => r.Contains(needle, StringComparison.OrdinalIgnoreCase) || r.TrimEnd('\\', '"').Equals(path, StringComparison.OrdinalIgnoreCase)))
            return "已装的软件或服务还引用着这个文件夹，不动";
        return null;
    }
}

public sealed class LeftoverDirOp : Operation
{
    public override string Id => "leftover.remove_dir";

    static string Dest(OpContext c, PlanStep s, JsonObject b) => b["moved"]?.ToString()
        ?? c.WorkDir.TrimEnd('\\', '/') + "\\leftovers\\" + c.Sys.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    protected override string? Precheck(PlanStep s, OpContext c) => LeftoverDirs.NotLeftover(c.Sys, T(s, "path"));

    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        b["moved"] = Dest(c, s, b);
        b["bytes"] = c.Sys.EnumerateFiles(T(s, "path"), true, includeSystem: true).Sum(f => f.Size);
    }

    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        if (!c.Sys.MoveDirectory(T(s, "path"), b["moved"]!.ToString())) throw new InvalidOperationException("文件夹正在使用，没有挪走");
    }

    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        !c.Sys.DirectoryExists(T(s, "path")) && c.Sys.DirectoryExists(b["moved"]!.ToString()) ? null : "文件夹还在原处";

    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        var dest = b["moved"]?.ToString();
        if (dest != null && !c.Sys.DirectoryExists(T(s, "path")) && c.Sys.DirectoryExists(dest)) c.Sys.MoveDirectory(dest, T(s, "path"));
    }

    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.DirectoryExists(T(s, "path")) ? null : "文件夹没有回到原处";

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) =>
        $"已把这个残留文件夹（{CacheClean.Size(Operations.Int(b["bytes"]) ?? 0)}）挪到 Iris 的备份区；需要时可以撤销，清理旧备份时才真正删除";
}
