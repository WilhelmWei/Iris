using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pcdoctor.Ops;

namespace Pcdoctor.App;

/// <summary>管理员辅助进程：Iris.exe --helper &lt;collect|execute|undo&gt; &lt;交换目录&gt; --sid &lt;用户 SID&gt; [--sha256 &lt;哈希&gt;] [--id &lt;记录编号&gt;] [--scan]
/// 由主程序按需提权启动，做完就退出（不常驻、不装服务）。
///
/// 安全设计（2026-10-25 代码审查）：交换目录在普通用户可写的 %LOCALAPPDATA% 下，所以辅助进程
///   · 不执行交换目录里的任何东西：采集脚本由辅助进程自己写进只有管理员能写的目录再运行；
///   · plan.json 按命令行里的 SHA-256 校验（命令行在 UAC 确认后改不了）；
///   · 管理员操作的记录和备份只存在受保护目录（Program Files\pcdoctor-data，普通用户只能读），撤销时只按编号从那里读；
///   · 只接受和主程序同一个 Windows 账户（用另一个管理员账户授权时，当前用户的设置都读不到）。
/// 交流文件：progress.jsonl（逐行进度）、done.json（结果）、cancel（主程序要求取消）。</summary>
public static class Helper
{
    /// <summary>辅助进程绝不执行的操作：卸载命令来自普通用户可写的注册表位置，不能用管理员身份运行。</summary>
    static readonly HashSet<string> NotInHelper = ["app.uninstall", "folder.move"];  // 改的是当前用户自己的东西，只在用户自己的权限里做

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string[] args)
    {
        // 用户在 UAC 里点"是"之后，前台交给了这个没有窗口的进程；把前台还给主程序，主窗口才不会被压到其他窗口下面
        AllowSetForegroundWindow(-1);
        if (args.Length < 3) return 2;
        var verb = args[1];
        var dir = Path.GetFullPath(args[2]);
        // 交换目录必须是某个用户的 ...\pcdoctor\runs\<名字>（账户不同时也要能把"账户不同"写回去）
        var parent = Path.GetDirectoryName(dir) ?? "";
        if (!parent.EndsWith(@"\pcdoctor\runs", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) return 3;
        try
        {
            if (Arg(args, "--sid") != WindowsIdentity.GetCurrent().User?.Value)
            {
                Done(dir, new JsonObject { ["ok"] = false, ["code"] = "other-user",
                    ["error"] = "授权时用的是另一个管理员账户，读不到你自己的设置。请用你自己的账户授权，或者让这台电脑的管理员登录后再用" });
                return 0;
            }
            if (!Path.GetFullPath(parent).Equals(Path.GetFullPath(AppData.Runs), StringComparison.OrdinalIgnoreCase)) return 3;
            return verb switch
            {
                "collect" => Collect(dir, args.Contains("--scan"), args.Contains("--space"), int.TryParse(Arg(args, "--parent"), out var pp) ? pp : 0),
                "execute" => Execute(dir, Arg(args, "--sha256") ?? ""),
                "purge" => Purge(dir),
                "prune" => Prune(dir, int.TryParse(Arg(args, "--days"), out var d) ? d : -1),
                "undo" => Undo(dir, (Arg(args, "--ids") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Done(dir, new JsonObject { ["ok"] = false, ["error"] = ex.Message });
            return 1;
        }
    }

    static void Progress(string dir, JsonObject p)
    {
        lock (typeof(Helper)) File.AppendAllText(Path.Combine(dir, "progress.jsonl"), p.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    static void Done(string dir, JsonObject d) => AppData.WriteAtomic(Path.Combine(dir, "done.json"), d.ToJsonString());
    static bool Cancelled(string dir) => File.Exists(Path.Combine(dir, "cancel"));

    // ------------------------------------------------------------------ 受保护目录
    /// <summary>Program Files 下的目录继承"只有管理员能写、普通用户只能读"的权限，普通用户也没法抢先创建它。</summary>
    public static string ProtectedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "pcdoctor-data");
    public static string AdminJournal => Path.Combine(ProtectedRoot, "journal");
    /// <summary>管理员步骤的备份和清单（普通用户能读，不能写）。</summary>
    public static string AdminBackups => Path.Combine(ProtectedRoot, "backups");
    static string ProtectedDir(string name) => Directory.CreateDirectory(Path.Combine(ProtectedRoot, name)).FullName;

    public static string PowerShellExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    // ------------------------------------------------------------------ 采集
    public static readonly string[] SectionTitles = ["资源占用", "进程", "开机自启动", "服务", "计划任务", "驱动（较慢）", "已装软件", "存储（较慢）",
                                                     "电源与电池", "安全设置", "浏览器与网络", "驱动与系统版本", "稳定性"];

    static int Collect(string dir, bool scan, bool space = false, int parentPid = 0)
    {
        var sw = Stopwatch.StartNew();
        var elevated = Environment.IsPrivilegedProcess;
        // 提权时：脚本和输出都放在只有管理员能写的目录里，普通权限的程序没法在运行前替换脚本
        var work = elevated ? ProtectedDir(Path.Combine("work", Guid.NewGuid().ToString("N"))) : dir;
        try
        {
            JsonObject? scanInfo = null;
            if (scan)
            {
                Progress(dir, new JsonObject { ["phase"] = "scan" });
                var ssw = Stopwatch.StartNew();
                var code = RunPowerShell(dir, "Start-MpScan -ScanType QuickScan", null, 1800);
                // 退出码 0 = 扫描正常结束；-1 = 超时或取消；其他 = Start-MpScan 报错（例如没有管理员权限、Defender 被第三方杀毒软件接管）
                scanInfo = new JsonObject { ["seconds"] = Math.Round(ssw.Elapsed.TotalSeconds, 1), ["exitCode"] = code, ["ok"] = code == 0 };
            }
            if (Cancelled(dir)) { Done(dir, new JsonObject { ["ok"] = false, ["error"] = "已取消" }); return 0; }

            var script = Path.Combine(work, "pcdoctor-collect.ps1");
            File.WriteAllBytes(script, Pcdoctor.Core.Resources.CollectorScriptBytes());
            var junk = Path.Combine(work, "junk.json");
            File.WriteAllBytes(junk, Pcdoctor.Ops.Junk.CatalogBytes());
            // 以文本方式运行脚本块：组策略强制的执行策略管不到它；本机采集不打码（报告只存在本机），发给模型前由主程序打码
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var cmd = $"& ([scriptblock]::Create([IO.File]::ReadAllText({Q(script)}, [Text.Encoding]::UTF8))) -OutDir {Q(work)} -NoRedact -JunkCatalog {Q(junk)}";
            var exit = RunPowerShell(dir, cmd, line =>
            {
                var i = line.IndexOf("采集：", StringComparison.Ordinal);
                if (i < 0) return;
                var title = line[(i + 3)..].Trim();
                Progress(dir, new JsonObject { ["phase"] = "collect", ["index"] = Array.IndexOf(SectionTitles, title), ["title"] = title, ["elapsed"] = sw.Elapsed.TotalSeconds });
            }, 900);

            var report = Directory.GetFiles(work, "pcdoctor-report-*.json").OrderByDescending(f => f).FirstOrDefault();
            if (Cancelled(dir)) Done(dir, new JsonObject { ["ok"] = false, ["error"] = "已取消" });
            else if (report == null)
                Done(dir, new JsonObject { ["ok"] = false, ["error"] = $"采集没有生成报告（退出码 {exit}）。详细信息见 {Path.Combine(dir, "collect.log")}" });
            else
            {
                var dest = Path.Combine(dir, Path.GetFileName(report));
                if (!dest.Equals(report, StringComparison.OrdinalIgnoreCase)) File.Copy(report, dest, true);
                Done(dir, new JsonObject { ["ok"] = true, ["report"] = dest, ["isAdmin"] = elevated, ["elapsed"] = sw.Elapsed.TotalSeconds, ["scan"] = scanInfo,
                                           ["space"] = space });
                // 报告已经交给主程序去研判；同一个进程接着扫 C 盘（不用再弹一次管理员确认），主程序随时取快照
                if (space) Space(dir, elevated, parentPid);
            }
            return 0;
        }
        finally
        {
            if (elevated) try { Directory.Delete(work, true); } catch { }
            else try { File.Delete(Path.Combine(work, "pcdoctor-collect.ps1")); File.Delete(Path.Combine(work, "junk.json")); } catch { }
        }
    }

    /// <summary>隐藏窗口运行 Windows PowerShell 5.1（全路径），输出按 UTF-8 读取并写进 collect.log。取消时结束整个进程树。</summary>
    /// <summary>
    /// 「C 盘空间去哪了」：低优先级遍历整个系统盘，每秒写进度、每 5 秒写快照（space.json），扫完写最终结果。
    /// 主程序关掉（或写了 cancel）时停下，已经扫到的部分照样写出来。
    /// </summary>
    static void Space(string dir, bool elevated, int parentPid)
    {
        WindowsSpaceSource.EnterBackgroundMode();
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drive = new DriveInfo(root);
        var used = drive.TotalSize - drive.TotalFreeSpace;
        var scan = new SpaceScan(new WindowsSpaceSource(), root, used, dedupeUnder: Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var sw = Stopwatch.StartNew();
        var lastSnap = TimeSpan.Zero; var lastCheck = TimeSpan.Zero; var stop = false;
        void Write()
        {
            var snap = scan.Snapshot(p => SpaceLabels.Json(root, p));
            snap["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            snap["isAdmin"] = elevated;
            AppData.WriteAtomic(Path.Combine(dir, "space.json"), snap.ToJsonString());
        }
        bool Stop()
        {
            if (sw.Elapsed - lastCheck < TimeSpan.FromMilliseconds(500)) return stop;
            lastCheck = sw.Elapsed;
            return stop = Cancelled(dir) || !ParentAlive(parentPid);
        }
        try
        {
            scan.Run(Stop, () =>
            {
                AppData.WriteAtomic(Path.Combine(dir, "space-progress.json"), new JsonObject
                {
                    ["scannedBytes"] = scan.ScannedBytes, ["usedBytes"] = used, ["files"] = scan.ScannedFiles,
                    ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                }.ToJsonString());
                if (sw.Elapsed - lastSnap > TimeSpan.FromSeconds(5)) { lastSnap = sw.Elapsed; Write(); }
            });
        }
        finally { Write(); }
    }

    static bool ParentAlive(int pid)
    {
        if (pid <= 0) return true;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static int RunPowerShell(string dir, string command, Action<string>? onLine, int timeoutSec)
    {
        var full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + command;
        var psi = new ProcessStartInfo(PowerShellExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", full }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var log = Path.Combine(dir, "collect.log");
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (typeof(Helper)) File.AppendAllText(log, e.Data + "\n"); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (typeof(Helper)) File.AppendAllText(log, "[错误] " + e.Data + "\n"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        while (!p.WaitForExit(500))
        {
            if (Cancelled(dir) || sw.Elapsed.TotalSeconds > timeoutSec) { try { p.Kill(true); } catch { } return -1; }
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    // ------------------------------------------------------------------ 执行与撤销
    /// <summary>普通权限下的上下文（主程序进程里用）。</summary>
    public static OpContext UserContext() =>
        new(new WindowsSystem(Path.GetTempPath()), Directory.CreateDirectory(Path.Combine(AppData.Root, "backups")).FullName);

    /// <summary>管理员上下文：临时文件和备份（导出的驱动包等）都在受保护目录里。</summary>
    static OpContext AdminContext() => new(new WindowsSystem(ProtectedDir("work")), ProtectedDir("backups"));

    static int Execute(string dir, string sha256)
    {
        // 只读一次文件内容，先校验哈希再解析：防止 UAC 确认期间被普通权限的程序替换
        var bytes = File.ReadAllBytes(Path.Combine(dir, "plan.json"));
        if (sha256.Length != 64 || !SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(sha256)))
        {
            Done(dir, new JsonObject { ["ok"] = false, ["error"] = "处理方案文件在执行前被改动过，已拒绝执行" });
            return 1;
        }
        var plan = JsonSerializer.Deserialize(bytes, OpsJson.Default.Plan);
        var steps = plan?.Steps ?? [];
        var ctx = AdminContext();
        var journal = new JournalStore(AdminJournal);
        foreach (var s in steps)
        {
            if (Cancelled(dir)) break;
            Progress(dir, new JsonObject { ["key"] = s.Key, ["status"] = "running" });
            JournalEntry e;
            if (NotInHelper.Contains(s.Op) || !Catalog.Ops.ContainsKey(s.Op))
                e = new JournalEntry { Id = Guid.NewGuid().ToString("N"), TimeUtc = DateTime.UtcNow, Op = s.Op, OpName = s.OpName, Title = s.Title,
                                       Status = Status.Failed, Message = "这一步不能在管理员辅助程序里执行",
                                       ReportId = plan?.ReportId, BatchId = plan?.PlanId, FindingKey = s.FindingKey };
            else e = Executor.RunStep(s, ctx, plan?.ReportId, plan?.PlanId);
            e.NeedsAdmin = true;
            e.Owner = WindowsIdentity.GetCurrent().User?.Value;
            journal.Save(e);
            Progress(dir, new JsonObject { ["key"] = s.Key, ["status"] = e.Status, ["message"] = e.Message, ["entryId"] = e.Id });
        }
        Done(dir, new JsonObject { ["ok"] = true });
        return 0;
    }

    /// <summary>删除受保护目录（用户在设置里点"清除所有数据"并确认后）。</summary>
    static int Purge(string dir)
    {
        if (Directory.Exists(ProtectedRoot)) Directory.Delete(ProtectedRoot, true);
        Done(dir, new JsonObject { ["ok"] = true });
        return 0;
    }

    /// <summary>清理受保护目录里自己的旧操作备份（只动当前账户的记录）。</summary>
    static int Prune(string dir, int days)
    {
        if (days is < 1 or > 3650) { Done(dir, new JsonObject { ["ok"] = false, ["error"] = "天数不对" }); return 0; }
        var me = WindowsIdentity.GetCurrent().User?.Value;
        var r = Directory.Exists(AdminJournal)
            ? BackupPruner.Prune(new JournalStore(AdminJournal), ProtectedDir("backups"), DateTime.UtcNow.AddDays(-days), DateTime.UtcNow, e => e.Owner == me)
            : new BackupPruner.Result(0, 0);
        Done(dir, new JsonObject { ["ok"] = true, ["entries"] = r.Entries, ["bytes"] = r.Bytes });
        return 0;
    }

    /// <summary>撤销一条或一批（按时间倒序：后做的先撤）。只按编号从受保护目录读记录，不读交换目录里的任何内容。</summary>
    static int Undo(string dir, string[] ids)
    {
        var journal = new JournalStore(AdminJournal);
        var me = WindowsIdentity.GetCurrent().User?.Value;
        var entries = ids.Select(journal.Get).Where(e => e != null && e.Owner == me).Select(e => e!).OrderByDescending(e => e.TimeUtc).ToList();
        if (entries.Count == 0) { Done(dir, new JsonObject { ["ok"] = false, ["error"] = "找不到要撤销的记录" }); return 0; }
        var ctx = AdminContext();
        var results = new JsonArray();
        foreach (var e in entries)
        {
            var o = NotInHelper.Contains(e.Op) ? Outcome.Fail("这一步不能在管理员辅助程序里撤销") : Executor.Undo(e, ctx);
            journal.Save(e);
            results.Add(new JsonObject { ["id"] = e.Id, ["ok"] = o.Status == Status.Done, ["message"] = o.Message });
        }
        var ok = results.All(r => r!["ok"]!.GetValue<bool>());
        Done(dir, new JsonObject { ["ok"] = ok, ["results"] = results,
            ["message"] = entries.Count == 1 ? results[0]!["message"]!.DeepClone() : (ok ? $"已撤销 {entries.Count} 步，重新检测确认都回到了原来的状态" : "部分步骤没能撤销，详情见操作记录") });
        return 0;
    }
}
