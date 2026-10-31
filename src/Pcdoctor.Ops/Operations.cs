using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Pcdoctor.Ops;

/// <summary>执行一步时的环境：系统接口 + 存放备份文件（导出的驱动包等）的目录。</summary>
public sealed record OpContext(IWinSystem Sys, string WorkDir)
{
    /// <summary>耗时长的步骤（搬文件夹）报告进度，界面上显示"正在复制 3.2 / 12 GB"。</summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>一个操作 = 前置检查 → 备份 → 执行 → 重新检测验证 → 没生效就回滚。
/// 结果一律以重新检测为准，不看命令有没有报错（CLAUDE.md"执行结果一律以重新检测为准"）。</summary>
public abstract class Operation
{
    public abstract string Id { get; }

    public Outcome Apply(PlanStep step, OpContext ctx, JsonObject backup)
    {
        var invalid = Operations.ValidateTarget(Id, step.Target);
        if (invalid != null) return Outcome.Fail(invalid);
        var pre = Precheck(step, ctx);
        if (pre != null) return Outcome.Fail(pre);
        Backup(step, ctx, backup);
        try
        {
            Change(step, ctx, backup);
            var bad = Verify(step, ctx, backup);
            if (bad == null) return Outcome.Ok(DoneMessage(step, ctx, backup));
            if (!ChangesOnlyOnSuccess) return Outcome.Fail(bad);
            Rollback(step, ctx, backup);
            return Outcome.Rolled($"重新检测发现没有生效（{bad}），已恢复原状");
        }
        catch (Exception ex)
        {
            try { Rollback(step, ctx, backup); } catch { /* 回滚失败的情况在消息里说明 */ }
            return Outcome.Rolled($"执行时出错（{ex.Message}），已尝试恢复原状");
        }
    }

    /// <summary>撤销：用备份恢复，并重新检测确认。</summary>
    public virtual Outcome Undo(JournalEntry e, OpContext ctx)
    {
        var step = new PlanStep { Op = e.Op, Target = e.Target };
        var invalid = Operations.ValidateTarget(Id, e.Target);
        if (invalid != null) return Outcome.Fail(invalid);
        Rollback(step, ctx, e.Backup);
        var bad = VerifyRestored(step, ctx, e.Backup);
        return bad == null ? Outcome.Ok("已恢复，重新检测确认回到了原来的状态") : Outcome.Fail($"恢复后重新检测不一致：{bad}");
    }

    /// <summary>返回 null 表示可以执行，否则是不执行的原因（不做任何改动）。</summary>
    protected abstract string? Precheck(PlanStep s, OpContext c);
    protected abstract void Backup(PlanStep s, OpContext c, JsonObject b);
    protected abstract void Change(PlanStep s, OpContext c, JsonObject b);
    /// <summary>返回 null 表示生效，否则是没生效的说明。</summary>
    protected abstract string? Verify(PlanStep s, OpContext c, JsonObject b);
    protected abstract void Rollback(PlanStep s, OpContext c, JsonObject b);
    protected virtual string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) => null;
    /// <summary>没生效就等于没改动的操作（卸载被取消、系统拒绝删除驱动包）：不需要回滚，直接说明原因。</summary>
    protected virtual bool ChangesOnlyOnSuccess => true;
    protected virtual string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已完成，重新检测确认生效";

    protected static string T(PlanStep s, string k) => s.Target[k]?.ToString() ?? "";
}

public static class Operations
{
    public static readonly IReadOnlyDictionary<string, Operation> All = new Operation[]
    {
        new StartupDisable(), new TaskDisable(), new TaskDeleteLeftover(), new ServiceSetManual(), new ServiceDeleteOrphan(),
        new BrowserDisableBackground(), new ChromeDisableBackground(), new PowerDisableFastStartup(), new CacheClean(), new DefenderScan(), new AppUninstall(),
        new DriverDeleteOldVersion(), new FolderMoveOp(), new SystemCleanupOp(), new InstallersRecycleOp(), new LeftoverDirOp(),
    }.ToDictionary(o => o.Id);

    public static Operation? Get(string id) => All.TryGetValue(id, out var o) ? o : null;

    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    static readonly HashSet<string> ApprovedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HKCU|" + Approved + "Run", "HKCU|" + Approved + "StartupFolder",
        "HKLM|" + Approved + "Run", "HKLM|" + Approved + "Run32", "HKLM|" + Approved + "StartupFolder",
    };
    static readonly HashSet<string> RunKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
    };
    static readonly System.Text.RegularExpressions.Regex SafeName = new(@"^[^\\/:*?""<>|\x00-\x1f]{1,256}$");

    /// <summary>白名单校验操作目标：处理方案和操作记录放在普通用户可写的位置，管理员执行前必须确认目标只落在该落的地方。
    /// 返回 null 表示合法。</summary>
    public static string? ValidateTarget(string op, JsonObject t)
    {
        string V(string k) => t[k]?.ToString() ?? "";
        switch (op)
        {
            case "startup.disable":
                if (!ApprovedKeys.Contains(V("approvedHive") + "|" + V("approvedKey"))) return "目标不是启动项的位置，拒绝执行";
                if (!SafeName.IsMatch(V("valueName"))) return "启动项名称不合法";
                if (t["runKey"] != null && !RunKeys.Contains(V("runKey"))) return "启动项来源位置不合法";
                return null;
            case "task.disable":
            case "task.delete_leftover":
                return V("fullName").StartsWith('\\') && !V("fullName").Contains("..") ? null : "计划任务名称不合法";
            case "service.set_manual":
            case "service.delete_orphan":
                return SafeName.IsMatch(V("name")) ? null : "服务名称不合法";
            case "leftover.remove_dir":
                return LeftoverDirs.PathProblem(V("path"));
            case "folder.move":
                if (FolderMove.Get(V("folder")) == null) return "不支持搬这个文件夹";
                return System.Text.RegularExpressions.Regex.IsMatch(V("drive"), "^[D-Zd-z]:$") ? null : "目标盘不合法";
            case "driver.delete_old_version":
                return System.Text.RegularExpressions.Regex.IsMatch(V("publishedName"), @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? null : "只处理第三方驱动包（oem 开头）";
            default:
                return null;
        }
    }

    internal static (Hive hive, string key) ParseRegPath(string p)
    {
        // "HKCU:\Software\..." / "HKLM\SOFTWARE\..." / "HKEY_LOCAL_MACHINE\..."
        var s = p.Replace('/', '\\');
        var i = s.IndexOf('\\');
        var head = (i < 0 ? s : s[..i]).TrimEnd(':').ToUpperInvariant();
        var rest = i < 0 ? "" : s[(i + 1)..];
        return head switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (Hive.CurrentUser, rest),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (Hive.LocalMachine, rest),
            _ => throw new ArgumentException($"不支持的注册表位置：{p}"),
        };
    }

    internal static Hive ParseHive(string h) => h == "HKCU" ? Hive.CurrentUser : Hive.LocalMachine;

    /// <summary>把服务的 ImagePath 换成文件路径：去掉引号和参数，展开 \SystemRoot\、system32\、\??\。</summary>
    public static string ResolveImagePath(string imagePath, string windowsDir, Func<string, string>? expand = null)
    {
        var s = (expand ?? (x => x))(imagePath).Trim();
        if (s.StartsWith('"')) { var j = s.IndexOf('"', 1); s = j > 0 ? s[1..j] : s.Trim('"'); }
        else
        {
            var k = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var m = s.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
            var cut = new[] { k, m }.Where(x => x > 0).DefaultIfEmpty(-1).Min();
            if (cut > 0) s = s[..(cut + 4)];
        }
        if (s.StartsWith(@"\??\")) s = s[4..];
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = windowsDir.TrimEnd('\\') + s[11..];
        else if (s.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) s = windowsDir.TrimEnd('\\') + "\\" + s;
        s = s.Replace("%SystemRoot%", windowsDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
             .Replace("%windir%", windowsDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        return s;
    }

    internal static bool UnderWindows(string path, string windowsDir) =>
        path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static JsonNode? ToJson(RegValue? v) => v?.ToJson();

    public static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) ? x : null;

    public static long? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x
        : n is JsonValue w && w.TryGetValue<int>(out var y) ? y : null;

    internal static void Restore(IWinSystem sys, Hive hive, string key, string name, JsonNode? saved)
    {
        var v = RegValue.FromJson(saved);
        if (v == null) { if (sys.ReadValue(hive, key, name) != null) sys.DeleteValue(hive, key, name); }
        else sys.WriteValue(hive, key, name, v);
    }

    internal static bool SameValue(RegValue? a, JsonNode? saved)
    {
        var b = RegValue.FromJson(saved);
        if (a == null || b == null) return a == null && b == null;
        return a.Kind == b.Kind && JsonNode.DeepEquals(a.Data, b.Data);
    }
}

// ---------------------------------------------------------------------------------------------- 自启动
/// <summary>关闭开机自启动：和任务管理器"禁用"一样，在 StartupApproved 里写"已禁用"标记，不删除原来的启动项。
/// Target：approvedHive、approvedKey、valueName，以及用于前置检查的 runKey（注册表启动项）或 path（启动文件夹）。</summary>
public sealed class StartupDisable : Operation
{
    public override string Id => "startup.disable";

    (Hive, string, string) Approved(PlanStep s) => (Operations.ParseHive(T(s, "approvedHive")), T(s, "approvedKey"), T(s, "valueName"));

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        if (s.Target["runKey"] is { } rk)
        {
            var (h, k) = Operations.ParseRegPath(rk.ToString());
            if (c.Sys.ReadValue(h, k, T(s, "valueName")) == null) return "这个自启动项已经不在了（可能已被卸载或手动删除）";
        }
        else if (s.Target["path"] is { } p && !c.Sys.FileExists(p.ToString())) return "启动文件夹里的这个快捷方式已经不在了";
        return null;
    }

    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k, n) = Approved(s);
        b["approved"] = Operations.ToJson(c.Sys.ReadValue(h, k, n));
    }

    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k, n) = Approved(s);
        // 12 字节：前 4 字节是状态（03 = 禁用），后 8 字节是禁用时间（FILETIME），和任务管理器写法相同
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.GetBytes(c.Sys.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        c.Sys.WriteValue(h, k, n, RegValue.Binary(data));
    }

    static bool Disabled(RegValue? v) => v?.AsBytes() is { Length: > 0 } d && (d[0] & 1) == 1;

    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k, n) = Approved(s);
        return Disabled(c.Sys.ReadValue(h, k, n)) ? null : "禁用标记没有写进去";
    }

    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k, n) = Approved(s);
        Operations.Restore(c.Sys, h, k, n, b["approved"]);
    }

    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k, n) = Approved(s);
        return Operations.SameValue(c.Sys.ReadValue(h, k, n), b["approved"]) ? null : "启动项状态和原来不一样";
    }

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已关闭开机自启动，重新检测确认生效；软件本身没有被删除，需要时照常打开";
}

// ---------------------------------------------------------------------------------------------- 计划任务
static class TaskXml
{
    static XElement? Find(string? xml, string local, string? parent = null)
    {
        if (xml == null) return null;
        try
        {
            return XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == local && (parent == null || e.Parent?.Name.LocalName == parent));
        }
        catch { return null; }
    }

    /// <summary>任务第一个"启动程序"动作的程序路径（去掉引号）。</summary>
    public static string? Command(string? xml) => Find(xml, "Command", "Exec")?.Value.Trim().Trim('"');

    public static string? LogonType(string? xml) => Find(xml, "LogonType")?.Value.Trim();

    public static bool? Enabled(string? xml)
    {
        if (xml == null) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var en = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Enabled" && e.Parent?.Name.LocalName == "Settings");
            return en == null || !string.Equals(en.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }
}

public sealed class TaskDisable : Operation
{
    public override string Id => "task.disable";
    protected override string? Precheck(PlanStep s, OpContext c) =>
        c.Sys.ExportTaskXml(T(s, "fullName")) == null ? "这个计划任务已经不在了" : null;
    protected override void Backup(PlanStep s, OpContext c, JsonObject b) => b["xml"] = c.Sys.ExportTaskXml(T(s, "fullName"));
    protected override void Change(PlanStep s, OpContext c, JsonObject b) => c.Sys.SetTaskEnabled(T(s, "fullName"), false);
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        TaskXml.Enabled(c.Sys.ExportTaskXml(T(s, "fullName"))) == false ? null : "任务仍然是启用状态";
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        if (TaskXml.Enabled(b["xml"]?.ToString()) != false) c.Sys.SetTaskEnabled(T(s, "fullName"), true);
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        TaskXml.Enabled(c.Sys.ExportTaskXml(T(s, "fullName"))) == TaskXml.Enabled(b["xml"]?.ToString()) ? null : "任务状态和原来不一样";
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已禁用计划任务，重新检测确认生效（任务还在，随时可以恢复）";
}

/// <summary>删除指向已不存在程序的计划任务。删除前导出完整的任务定义，撤销时原样注册回去。</summary>
public sealed class TaskDeleteLeftover : Operation
{
    public override string Id => "task.delete_leftover";
    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var xml = c.Sys.ExportTaskXml(T(s, "fullName"));
        if (xml == null) return "这个计划任务已经不在了";
        // 程序路径从任务本身重新读，不用方案里的值（方案文件放在普通用户可写的位置）
        var exe = TaskXml.Command(xml);
        if (string.IsNullOrEmpty(exe)) return "这个任务不是启动程序的任务，不按卸载残留处理";
        exe = c.Sys.Expand(exe).Replace('/', '\\');
        if (c.Sys.FileExists(exe)) return "任务指向的程序还在（可能重新安装了），没有删除";
        if (TaskXml.LogonType(xml) == "Password") return "这个任务用保存的密码运行，删除后无法原样恢复，改为建议禁用";
        return null;
    }
    protected override void Backup(PlanStep s, OpContext c, JsonObject b) => b["xml"] = c.Sys.ExportTaskXml(T(s, "fullName"));
    protected override void Change(PlanStep s, OpContext c, JsonObject b) => c.Sys.DeleteTask(T(s, "fullName"));
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.ExportTaskXml(T(s, "fullName")) == null ? null : "任务还在";
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        if (c.Sys.ExportTaskXml(T(s, "fullName")) == null && b["xml"] is { } x) c.Sys.RegisterTaskXml(T(s, "fullName"), x.ToString());
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.ExportTaskXml(T(s, "fullName")) != null ? null : "任务没能恢复";
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已删除这个卸载残留的计划任务（删除前做了完整备份，可以撤销）";
}

// ---------------------------------------------------------------------------------------------- 服务
static class Svc
{
    public static string Key(string name) => @"SYSTEM\CurrentControlSet\Services\" + name;

    /// <summary>服务真正的程序：svchost 里的服务看 ServiceDll，其他看 ImagePath。</summary>
    public static string Binary(IWinSystem sys, string name)
    {
        var dll = sys.ReadValue(Hive.LocalMachine, Key(name) + @"\Parameters", "ServiceDll")?.AsStrings().FirstOrDefault();
        if (!string.IsNullOrEmpty(dll)) return Operations.ResolveImagePath(dll, sys.WindowsDir, sys.Expand);
        var img = sys.ReadValue(Hive.LocalMachine, Key(name), "ImagePath")?.AsStrings().FirstOrDefault() ?? "";
        return Operations.ResolveImagePath(img, sys.WindowsDir, sys.Expand);
    }

    /// <summary>红线 2：挂在任何设备类的 UpperFilters / LowerFilters 上的，不删除登记。</summary>
    public static string? FilterClass(IWinSystem sys, string name)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Class";
        foreach (var guid in sys.SubKeys(Hive.LocalMachine, root))
            foreach (var kind in new[] { "UpperFilters", "LowerFilters" })
                if (sys.ReadValue(Hive.LocalMachine, root + "\\" + guid, kind)?.AsStrings()
                        .Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) == true)
                    return $"{guid} 的 {kind}";
        return null;
    }

    public static string ScStart(int start, bool delayed) => start switch
    {
        2 => delayed ? "delayed-auto" : "auto",
        3 => "demand",
        4 => "disabled",
        0 => "boot",
        1 => "system",
        _ => "demand",
    };
}

/// <summary>第三方服务改为手动启动。红线：不改微软自带服务（程序在 Windows 目录里的一律不动）。</summary>
public sealed class ServiceSetManual : Operation
{
    public override string Id => "service.set_manual";
    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var n = T(s, "name");
        if (!c.Sys.KeyExists(Hive.LocalMachine, Svc.Key(n))) return "这个服务已经不在了";
        var bin = Svc.Binary(c.Sys, n);
        if (bin == "" || Operations.UnderWindows(bin, c.Sys.WindowsDir))
            return "这是 Windows 自带的服务，按规则不修改它的启动方式（改了可能导致蓝牙、登录、联网等出问题）";
        var start = c.Sys.ReadValue(Hive.LocalMachine, Svc.Key(n), "Start")?.AsInt();
        if (start is 0 or 1) return "这是系统启动早期加载的驱动类服务，不在这里修改";
        return null;
    }
    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var k = Svc.Key(T(s, "name"));
        b["start"] = c.Sys.ReadValue(Hive.LocalMachine, k, "Start")?.AsInt();
        b["delayed"] = c.Sys.ReadValue(Hive.LocalMachine, k, "DelayedAutostart")?.AsInt() == 1;
    }
    protected override void Change(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.Run("sc.exe", $"config \"{T(s, "name")}\" start= demand");
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.ReadValue(Hive.LocalMachine, Svc.Key(T(s, "name")), "Start")?.AsInt() == 3 ? null : "启动方式没有变成手动";
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        var start = Operations.Int(b["start"]) ?? 2;
        var cur = c.Sys.ReadValue(Hive.LocalMachine, Svc.Key(T(s, "name")), "Start")?.AsInt();
        if (cur != start)
            c.Sys.Run("sc.exe", $"config \"{T(s, "name")}\" start= {Svc.ScStart((int)start, b["delayed"] is JsonValue d && d.TryGetValue<bool>(out var dv) && dv)}");
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.ReadValue(Hive.LocalMachine, Svc.Key(T(s, "name")), "Start")?.AsInt() == (Operations.Int(b["start"]) ?? 2) ? null : "启动方式和原来不一样";
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) =>
        "已改为手动启动，重新检测确认生效。下次开机不再自动运行；打开对应软件时一般会自己启动";
}

/// <summary>删除程序文件已不存在的服务 / 驱动登记。红线 1：正在运行的不动；红线 2：挂在设备过滤器上的不动。
/// 删除前把整个登记（注册表子树）备份，撤销时原样写回（重启后生效）。</summary>
public sealed class ServiceDeleteOrphan : Operation
{
    public override string Id => "service.delete_orphan";
    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var n = T(s, "name");
        if (!c.Sys.KeyExists(Hive.LocalMachine, Svc.Key(n))) return "这个登记已经不在了";
        var bin = Svc.Binary(c.Sys, n);
        if (bin != "" && c.Sys.FileExists(bin)) return "程序文件又出现了（可能重新安装了），没有删除登记";
        var state = c.Sys.ServiceState(n);
        if (state != null && state != "Stopped") return $"它现在的状态是 {state}，正在运行的驱动或服务不动（红线 1，强停可能蓝屏）";
        var f = Svc.FilterClass(c.Sys, n);
        if (f != null) return $"它挂在设备过滤器上（{f}），删除登记会让对应设备失灵，需要专业模式先摘除过滤器（红线 2）";
        return null;
    }
    protected override void Backup(PlanStep s, OpContext c, JsonObject b) =>
        b["tree"] = c.Sys.ExportTree(Hive.LocalMachine, Svc.Key(T(s, "name")))?.ToJson();
    protected override void Change(PlanStep s, OpContext c, JsonObject b) => c.Sys.Run("sc.exe", $"delete \"{T(s, "name")}\"");
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        var k = Svc.Key(T(s, "name"));
        if (!c.Sys.KeyExists(Hive.LocalMachine, k)) return null;
        return c.Sys.ReadValue(Hive.LocalMachine, k, "DeleteFlag")?.AsInt() == 1 ? null : "登记还在";
    }
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        if (b["tree"] is { } t && !c.Sys.KeyExists(Hive.LocalMachine, Svc.Key(T(s, "name"))))
            c.Sys.ImportTree(Hive.LocalMachine, Svc.Key(T(s, "name")), RegTree.FromJson(t));
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.KeyExists(Hive.LocalMachine, Svc.Key(T(s, "name"))) ? null : "登记没能恢复";
    public override Outcome Undo(JournalEntry e, OpContext ctx)
    {
        var o = base.Undo(e, ctx);
        return o.Status == Status.Done ? Outcome.Ok("已恢复登记，重启电脑后生效") : o;
    }
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已删除这个只剩登记的残留（删除前做了完整备份，可以撤销）";
}

// ---------------------------------------------------------------------------------------------- 注册表开关类
/// <summary>把几个注册表值设成目标值；备份原值，撤销时恢复（原来没有就删掉）。</summary>
public abstract class RegistrySwitch : Operation
{
    protected abstract Hive Hive { get; }
    protected abstract string Key { get; }
    protected abstract (string name, int value)[] Values { get; }

    protected override string? Precheck(PlanStep s, OpContext c) => null;
    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        foreach (var (n, _) in Values) b[n] = Operations.ToJson(c.Sys.ReadValue(Hive, Key, n));
    }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        foreach (var (n, v) in Values) c.Sys.WriteValue(Hive, Key, n, RegValue.DWord(v));
    }
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        foreach (var (n, v) in Values)
            if (c.Sys.ReadValue(Hive, Key, n)?.AsInt() != v) return $"{n} 没有写进去";
        return null;
    }
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        foreach (var (n, _) in Values) Operations.Restore(c.Sys, Hive, Key, n, b[n]);
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b)
    {
        foreach (var (n, _) in Values)
            if (!Operations.SameValue(c.Sys.ReadValue(Hive, Key, n), b[n])) return $"{n} 和原来不一样";
        return null;
    }
}

/// <summary>关闭 Edge 的启动增强和后台运行（策略方式，红线 9：Edge 设置页会显示"由你的组织管理"，执行前已告知）。</summary>
public sealed class BrowserDisableBackground : RegistrySwitch
{
    public override string Id => "browser.disable_background";
    protected override Hive Hive => Hive.LocalMachine;
    protected override string Key => @"SOFTWARE\Policies\Microsoft\Edge";
    protected override (string, int)[] Values => [("StartupBoostEnabled", 0), ("BackgroundModeEnabled", 0)];
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已关闭 Edge 的启动增强和后台运行，下次开机生效";
}

/// <summary>关闭 Chrome 关掉窗口后的后台运行（只在 Chrome 不是主力浏览器时建议）。策略方式，红线 9：Chrome 会显示"由你的组织管理"，执行前已告知。</summary>
public sealed class ChromeDisableBackground : RegistrySwitch
{
    public override string Id => "browser.disable_chrome_background";
    protected override Hive Hive => Hive.LocalMachine;
    protected override string Key => @"SOFTWARE\Policies\Google\Chrome";
    protected override (string, int)[] Values => [("BackgroundModeEnabled", 0)];
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已关闭 Chrome 关掉窗口后的后台运行，下次打开 Chrome 时生效";
}

/// <summary>关闭快速启动，保留休眠（红线 8：用户拒绝过关休眠）。</summary>
public sealed class PowerDisableFastStartup : RegistrySwitch
{
    public override string Id => "power.disable_fast_startup";
    protected override Hive Hive => Hive.LocalMachine;
    protected override string Key => @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    protected override (string, int)[] Values => [("HiberbootEnabled", 0)];
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已关闭快速启动，休眠照常可用。以后点「关机」就是真正的关机";
}

// ---------------------------------------------------------------------------------------------- 一次性动作
/// <summary>
/// 清理缓存：只清清理目录表（kb/junk.json，编进程序）里 A 类的缓存，按用户留下的类别，直接删除（不进回收站）。
/// 这些都是程序会自动重新生成的缓存，不是用户文件（红线 6 管的是用户文件）；进回收站等于没腾出空间。
/// 目录不从处理方案里读取：方案文件放在普通用户可写的位置，不能让它指定管理员要删的路径。
/// 浏览器等程序开着时跳过对应的一类；正在使用的文件跳过；删完重新测量。
/// </summary>
public sealed class CacheClean : Operation
{
    public override string Id => "cache.clean";
    public const int MaxFiles = 200000;

    /// <summary>可以清理的类别（目录表里的 A 类）。</summary>
    public static IEnumerable<JunkCategory> Cleanable => Junk.Catalog.Categories.Where(c => c.Tier == "A");

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var ch = s.CacheChoice;
        if (ch == null || ch.Categories.Count == 0) return "没有选择要清理的类别，什么也没做";
        foreach (var id in ch.Categories)
        {
            var cat = Junk.Get(id);
            if (cat == null || cat.Tier != "A") return "选择了不支持的类别";
            if (cat.Admin && !s.NeedsAdmin) return "选择了需要管理员权限的类别";
        }
        if (ch.Age is < 0 or > 3650) return "时间范围不对";
        return null;
    }
    /// <summary>处理方案里清理这一步的选项：按体检时量到的大小列出可清理的类别（大的在前），默认全选。
    /// 旧报告里没有测量数字时，列出目录表里全部 A 类。</summary>
    public static CacheOptions Options(JsonNode report, bool admin)
    {
        var measured = (report["sections"]?["storage"]?["junk"]?["items"] as JsonArray)?.OfType<JsonObject>()
            .Where(x => x["id"] != null).ToDictionary(x => x["id"]!.ToString(), x => x);
        var items = new List<CacheItem>();
        foreach (var cat in Cleanable.Where(c => c.Admin == admin))
        {
            JsonObject? m = null;
            if (measured != null && (!measured.TryGetValue(cat.Id, out m) || !((Operations.Num(m["cleanableMB"]) ?? 0) >= 1))) continue;
            items.Add(new CacheItem
            {
                Id = cat.Id, Name = cat.Name, Group = cat.Group, SizeMB = m == null ? null : Operations.Num(m["cleanableMB"]),
                Running = m?["running"]?.ToString() is { Length: > 0 } r ? r : null, Why = cat.Why, Cost = cat.Cost,
            });
        }
        items = items.OrderByDescending(i => i.SizeMB ?? 0).ToList();
        return new CacheOptions { Items = items, Categories = items.Select(i => i.Id).ToList() };
    }

    protected override void Backup(PlanStep s, OpContext c, JsonObject b) { }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var results = new JsonArray();
        long bytes = 0; int deleted = 0, skipped = 0;
        foreach (var cat in s.CacheChoice!.Categories.Distinct().Select(Junk.Get).OfType<JunkCategory>())
        {
            var r = new JsonObject { ["id"] = cat.Id, ["name"] = cat.Name };
            results.Add(r);
            var blocking = Junk.Blocking(c.Sys, cat);
            if (blocking.Count > 0) { r["running"] = string.Join("、", blocking); continue; }
            var files = Junk.Eligible(c.Sys, cat, c.Sys.UtcNow, s.CacheChoice.Age ?? 0, MaxFiles - deleted - skipped);
            long catBytes = 0; int catDeleted = 0;
            foreach (var chunk in files.Chunk(500))
            {
                var done = new HashSet<string>(c.Sys.DeleteFiles(chunk.Select(f => f.Path).ToList()), StringComparer.OrdinalIgnoreCase);
                foreach (var f in chunk) if (done.Contains(f.Path)) { catDeleted++; catBytes += f.Size; } else skipped++;
            }
            r["deleted"] = catDeleted; r["bytes"] = catBytes;
            deleted += catDeleted; bytes += catBytes;
        }
        b["categories"] = results; b["deleted"] = deleted; b["skipped"] = skipped; b["bytes"] = bytes;
    }
    /// <summary>重新测量：清过的类别里还剩多少符合条件的文件（被占用、没删掉的）。只用于告诉用户，不算失败。</summary>
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        long left = 0;
        foreach (var r in (b["categories"] as JsonArray ?? []).OfType<JsonObject>())
            if (r["running"] == null && Junk.Get(r["id"]!.ToString()) is { } cat)
                left += Junk.Eligible(c.Sys, cat, c.Sys.UtcNow, s.CacheChoice!.Age ?? 0, MaxFiles).Sum(f => f.Size);
        b["leftBytes"] = left;
        return null;
    }
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) { }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{Math.Max(bytes, 0) / 1048576.0:0.#} MB";

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b)
    {
        var cats = (b["categories"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var parts = cats.Where(r => (Operations.Int(r["bytes"]) ?? 0) > 0).OrderByDescending(r => Operations.Int(r["bytes"]) ?? 0)
            .Select(r => $"{r["name"]} {Size((Operations.Int(r["bytes"]) ?? 0))}").ToList();
        var msg = $"腾出了 {Size((Operations.Int(b["bytes"]) ?? 0))}" + (parts.Count > 0 ? "：" + string.Join("、", parts) : "");
        foreach (var r in cats.Where(r => r["running"] != null))
            msg += $"。{r["running"]} 正在运行，没有清「{r["name"]}」，关掉后可以再清一次";
        var skipped = Operations.Int(b["skipped"]) ?? 0;
        if (skipped > 0) msg += $"。{skipped} 个文件正在使用，已跳过";
        return msg;
    }
    public override Outcome Undo(JournalEntry e, OpContext ctx) => Outcome.Fail("缓存已经直接删除，不能撤销；它们会在需要时自动重新生成");
}

/// <summary>交给 Defender 快速扫描：只发起扫描，不修改任何安全设置。</summary>
public sealed class DefenderScan : Operation
{
    public override string Id => "defender.scan";
    protected override string? Precheck(PlanStep s, OpContext c) => null;
    protected override void Backup(PlanStep s, OpContext c, JsonObject b) { }
    protected override void Change(PlanStep s, OpContext c, JsonObject b) =>
        b["exit"] = c.Sys.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Start-MpScan -ScanType QuickScan\"", 1800);
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) => null;
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) { }
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => Operations.Int(b["exit"]) == 0
        ? "Defender 快速扫描已完成。发现的威胁会由 Defender 自己处理，可以在 Windows 安全中心的「保护历史记录」里查看"
        : "Defender 扫描没能正常完成，可以在 Windows 安全中心里手动扫描";
    public override Outcome Undo(JournalEntry e, OpContext ctx) => Outcome.Fail("扫描不需要撤销");
}

/// <summary>卸载软件：运行软件自己的卸载程序（你会看到它的界面），结束后检查软件是否还在。
/// 在普通权限的主程序里执行（不经过管理员辅助程序）：卸载命令来自注册表，当前用户的那部分谁都能写，不能用管理员身份运行。</summary>
public sealed class AppUninstall : Operation
{
    public override string Id => "app.uninstall";
    protected override bool ChangesOnlyOnSuccess => false;
    static readonly (Hive, string)[] Roots =
    [
        (Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Hive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    public static (Hive hive, string key, string command)? Find(IWinSystem sys, string displayName)
    {
        foreach (var (h, root) in Roots)
            foreach (var sub in sys.SubKeys(h, root))
            {
                var k = root + "\\" + sub;
                if (!string.Equals(sys.ReadValue(h, k, "DisplayName")?.AsStrings().FirstOrDefault(), displayName, StringComparison.Ordinal)) continue;
                if (sys.ReadValue(h, k, "SystemComponent")?.AsInt() == 1) continue;
                var cmd = sys.ReadValue(h, k, "UninstallString")?.AsStrings().FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(cmd)) return (h, k, cmd);
            }
        return null;
    }

    public static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var j = cmd.IndexOf('"', 1);
            return j > 0 ? (cmd[1..j], cmd[(j + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        var k = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return k > 0 ? (cmd[..(k + 4)], cmd[(k + 4)..].Trim()) : (cmd, "");
    }

    protected override string? Precheck(PlanStep s, OpContext c) =>
        Find(c.Sys, T(s, "displayName")) == null ? "在系统的已装软件列表里没找到它（可能已经卸载了）" : null;
    protected override void Backup(PlanStep s, OpContext c, JsonObject b) { }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var (_, _, cmd) = Find(c.Sys, T(s, "displayName"))!.Value;
        var (exe, args) = SplitCommand(c.Sys.Expand(cmd));
        b["exit"] = c.Sys.Run(exe, args, 3600);
        // 很多国产软件用 NSIS 打包：卸载程序把自己复制到临时目录再运行副本，原进程马上退出。
        // 所以进程结束后再等一会儿，直到软件从已装列表里消失（最多 3 分钟）
        for (var i = 0; i < 90 && Find(c.Sys, T(s, "displayName")) != null; i++) c.Sys.Sleep(2000);
    }
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        Find(c.Sys, T(s, "displayName")) == null ? null : "软件还在：可能在卸载程序里点了取消，或者卸载程序还开着。完成卸载后重新体检一次就能确认";
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) { }
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) =>
        "已卸载。软件卸载后常留下计划任务、服务和驱动，建议过几天再体检一次看看有没有残留";
    public override Outcome Undo(JournalEntry e, OpContext ctx) => Outcome.Fail("卸载不能自动撤销，需要的话请重新安装这个软件");
}

/// <summary>删除驱动仓库里没有设备在用的旧版本（红线 5：不加 /force，让系统自动保护在用的版本）。
/// 删除前用 pnputil 导出整个驱动包，撤销时重新添加。</summary>
public sealed class DriverDeleteOldVersion : Operation
{
    public override string Id => "driver.delete_old_version";
    protected override bool ChangesOnlyOnSuccess => false;
    string Inf(OpContext c, PlanStep s) => c.Sys.WindowsDir.TrimEnd('\\') + @"\INF\" + T(s, "publishedName");
    string ExportDir(OpContext c, PlanStep s) => Path.Combine(c.WorkDir, "driver-" + T(s, "publishedName"));

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var pub = T(s, "publishedName");
        if (!System.Text.RegularExpressions.Regex.IsMatch(pub, @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "只处理第三方驱动包（oem 开头）";
        return c.Sys.FileExists(Inf(c, s)) ? null : "这个驱动包已经不在了";
    }
    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var dir = ExportDir(c, s);
        Directory.CreateDirectory(dir);
        b["exportDir"] = dir;
        b["exportExit"] = c.Sys.Run("pnputil.exe", $"/export-driver {T(s, "publishedName")} \"{dir}\"");
    }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        if (Operations.Int(b["exportExit"]) != 0) throw new InvalidOperationException("备份驱动包失败，没有删除");
        b["exit"] = c.Sys.Run("pnputil.exe", $"/delete-driver {T(s, "publishedName")}");
    }
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.FileExists(Inf(c, s)) ? "系统拒绝删除：可能还有设备在用这个版本（这是系统的自我保护，属于正常）" : null;
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b) { /* 没删掉就不需要恢复 */ }
    public override Outcome Undo(JournalEntry e, OpContext ctx)
    {
        var dir = e.Backup["exportDir"]?.ToString();
        var inf = dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir, "*.inf").FirstOrDefault() : null;
        if (inf == null) return Outcome.Fail("找不到删除前导出的驱动包，无法恢复");
        return ctx.Sys.Run("pnputil.exe", $"/add-driver \"{inf}\"") == 0
            ? Outcome.Ok("已把驱动包重新加回驱动仓库") : Outcome.Fail("重新添加驱动包失败");
    }
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已删除这个没有设备在用的旧版驱动包（删除前已导出备份，可以撤销）";
}
