namespace Pcdoctor.Core;

/// <summary>操作库里的一项：名称、风险（green / yellow / red / null 表示不是操作）、允许作用的报告节（"*" 表示任意）、限定的预筛规则（null 表示不限）。</summary>
public sealed record Operation(string Id, string Name, string? Risk, IReadOnlySet<string> Sections, IReadOnlySet<string>? Rules);

/// <summary>
/// 操作库 v0.3（对应 judge/oplib.py）。操作编号的唯一来源：提示词里的操作表由 OperationsTable() 生成，
/// 校验时也只认这里的编号。风险等级由代码决定，模型的输出改不了它。
/// </summary>
public static class Oplib
{
    static Operation Op(string id, string name, string? risk, string[] sections, string[]? rules) =>
        new(id, name, risk, new HashSet<string>(sections), rules is null ? null : new HashSet<string>(rules));

    /// <summary>OPERATIONS（保持 Python 里的顺序，操作表按这个顺序生成）。</summary>
    public static readonly IReadOnlyList<Operation> List =
    [
        Op("none", "不需要处理", null, ["*"], null),
        Op("ask_user", "需要先问用户", null, ["*"], null),
        Op("startup.disable", "关闭开机自启动", "green", ["startup"], null),
        Op("task.disable", "禁用计划任务", "green", ["scheduledTasks"], null),
        Op("task.delete_leftover", "删除指向已不存在文件的计划任务", "green", ["scheduledTasks"], ["target-missing"]),
        Op("browser.disable_background", "关闭 Edge 的启动增强和后台运行", "green", ["browserNetwork", "processes"], null),
        Op("browser.disable_chrome_background", "关闭 Chrome 关掉窗口后的后台运行（Chrome 不是主力浏览器时）", "green", ["browserNetwork"], ["chrome-idle-background"]),
        Op("cache.clean", "清理程序会自动重建的缓存（临时文件、浏览器缓存、错误报告等，按内置的清理目录表）；不碰聊天文件、下载文件夹等用户数据", "green", ["storage"], ["junk-cleanable", "temp-large", "dev-cache", "system-drive-low"]),
        Op("defender.scan", "交给 Defender 扫描或隔离", "green", ["*"], null),
        Op("defender.review", "引导用户在 Windows 安全中心复核并删除不需要的排除项或\"允许的威胁\"", "green", ["security"], null),
        Op("service.set_manual", "第三方服务改为手动启动", "yellow", ["services"], null),
        Op("service.delete_orphan", "删除程序文件已不存在的服务或驱动登记（先确认没有挂在设备过滤器上）", "yellow", ["services", "drivers"], ["binary-missing"]),
        Op("app.uninstall", "卸载软件（用户确认后）", "yellow", ["software", "services", "drivers", "processes", "startup", "scheduledTasks", "storage", "browserNetwork"], null),
        Op("driver.delete_old_version", "删除驱动仓库里没有设备在用的旧版驱动包（不加 /force）", "yellow", ["drivers"], ["duplicate-versions"]),
        Op("power.disable_fast_startup", "关闭快速启动（保留休眠）", "yellow", ["power", "resources"], ["fast-startup-on", "long-uptime"]),
        Op("driver.remove_after_reboot", "删除第三方驱动：先摘除设备过滤器、标记删除、重启后移走文件", "red", ["drivers", "services"], null),
        Op("driver.switch_to_newer", "让设备改用驱动仓库里更新的版本", "red", ["versions", "drivers"], ["newer-driver-not-used"]),
    ];

    public static readonly IReadOnlyDictionary<string, Operation> Operations = List.ToDictionary(o => o.Id);

    /// <summary>RISK_NAMES；null 风险对应"—"。</summary>
    public static string RiskName(string? risk) => risk switch
    {
        "green" => "绿",
        "yellow" => "黄",
        "red" => "红",
        null => "—",
        _ => "—",
    };

    /// <summary>DRIVER_OPS：要过红线 2/3/4 检查的操作（删除登记也算）。</summary>
    public static readonly IReadOnlySet<string> DriverOps = new HashSet<string>
        { "driver.delete_old_version", "driver.remove_after_reboot", "driver.switch_to_newer", "service.delete_orphan" };

    /// <summary>operations_table()：生成提示词里的操作表（Markdown）。</summary>
    public static string OperationsTable()
    {
        var lines = new List<string> { "| 编号 | 含义 | 风险 | 适用的报告节 | 只用于这些规则 |", "| --- | --- | --- | --- | --- |" };
        foreach (var op in List)
        {
            var where = op.Sections.Contains("*") ? "任意" : string.Join("、", Py.Sorted(op.Sections));
            var only = op.Rules is { Count: > 0 } ? string.Join("、", Py.Sorted(op.Rules)) : "不限";
            lines.Add($"| `{op.Id}` | {op.Name} | {RiskName(op.Risk)} | {where} | {only} |");
        }
        return string.Join("\n", lines);
    }
}
