using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

/// <summary>每个操作给用户看的说明：简称、风险、会改什么、代价、怎么撤销。</summary>
public sealed record OpInfo(string Name, string Risk, string Change, string Cost, string Undo, bool Reversible, bool NeedsAdmin);

public static class Catalog
{
    public static readonly IReadOnlyDictionary<string, OpInfo> Ops = new Dictionary<string, OpInfo>
    {
        ["startup.disable"] = new("关闭开机自启动", "green", "在 Windows 的「启动应用」里把它标为禁用（和任务管理器里点「禁用」一样），不删除软件",
            "开机后它不会自己运行，需要时手动打开", "在操作记录里点撤销，或在任务管理器的「启动应用」里重新启用", true, false),
        ["task.disable"] = new("禁用计划任务", "green", "把这个计划任务设为禁用，任务本身保留",
            "它不会再定时运行（多为自动更新或推广）", "在操作记录里点撤销", true, true),
        ["task.delete_leftover"] = new("删除残留计划任务", "green", "删除这个指向已不存在程序的计划任务（删除前完整备份）",
            "没有代价：它指向的程序已经不存在了", "在操作记录里点撤销，会按备份原样恢复", true, true),
        ["browser.disable_background"] = new("关闭 Edge 后台", "green", "用策略关闭 Edge 的启动增强和后台运行",
            "Edge 第一次打开会慢一点；Edge 设置页会显示「你的浏览器由你的组织管理」，这是策略方式的正常提示", "在操作记录里点撤销", true, true),
        ["cache.clean"] = new("清理缓存", "green", "把你勾选的类别里、超过所选天数的文件移到回收站",
            "没有代价：这些是程序会自己重新生成的缓存；正在使用的文件会自动跳过", "文件都在回收站里，需要时可以还原", false, true),
        ["defender.scan"] = new("Defender 快速扫描", "green", "让 Windows 自带的 Microsoft Defender 做一次快速扫描，不修改任何安全设置",
            "扫描时电脑会稍慢 1–5 分钟", "不需要撤销", false, true),
        ["service.set_manual"] = new("服务改为手动", "yellow", "把这个第三方服务的启动方式从「自动」改为「手动」",
            "开机不再自动运行；打开对应软件时一般会自己启动。个别软件的部分功能（如自动同步）可能要打开软件后才工作", "在操作记录里点撤销", true, true),
        ["service.delete_orphan"] = new("删除残留登记", "yellow", "删除这个程序文件已不存在的服务 / 驱动登记（删除前完整备份）",
            "没有代价：它的程序文件已经不在了，登记只是残留", "在操作记录里点撤销，重启后生效", true, true),
        ["app.uninstall"] = new("卸载软件", "yellow", "运行这个软件自己的卸载程序（你会看到它的界面，按提示操作）",
            "软件会被删除，里面的设置可能一起删除", "卸载无法自动撤销，需要时请重新安装", false, false),
        ["driver.delete_old_version"] = new("删除旧版驱动包", "yellow", "从驱动仓库删除这个没有设备在用的旧版本（不强制删除，系统会保护在用的版本）",
            "没有代价：没有设备在用它；删除前会导出备份", "在操作记录里点撤销，会把导出的驱动包加回去", true, true),
        ["power.disable_fast_startup"] = new("关闭快速启动", "yellow", "关闭 Windows 的快速启动，休眠照常保留",
            "开机可能慢几秒；换来的是每次「关机」都是真正的关机，不再积累内存占用和小毛病", "在操作记录里点撤销", true, true),
    };

    /// <summary>界面上可以直接引导、但不改动系统的建议（例如打开 Windows 安全中心）。</summary>
    public static readonly string[] GuideOps = ["defender.review"];
}

/// <summary>把用户在报告里勾选的结论，变成作用在具体对象上的处理步骤。
/// 这里再查一遍红线和适用条件：报告里的建议来自模型或规则，执行前代码说了算。</summary>
public static class PlanBuilder
{
    /// <param name="report">本机原始采集报告（未打码，路径是真实的）</param>
    /// <param name="judge">研判结果（judge.json）</param>
    /// <param name="keys">勾选的结论编号（flagIds 用 + 连接）</param>
    /// <summary>用户回答"不认识"之后可以选择的可撤销操作：按对象所在的报告节决定。</summary>
    public static readonly IReadOnlyDictionary<string, string> AltOps = new Dictionary<string, string>
    {
        ["startup"] = "startup.disable", ["scheduledTasks"] = "task.disable", ["services"] = "service.set_manual",
    };

    /// <summary>每条结论在用户回答"不认识"后可用的操作（只给所有预筛结果都在同一节的结论）。</summary>
    public static Dictionary<string, string> AltOpsFor(JsonNode report, JsonNode judge)
    {
        var sec = report["flags"]!.AsArray().ToDictionary(f => f!["id"]!.ToString(), f => f!["section"]?.ToString() ?? "");
        var map = new Dictionary<string, string>();
        foreach (var f in judge["judgement"]!["findings"]!.AsArray())
        {
            var ids = f!["flagIds"]!.AsArray().Select(i => i!.ToString()).ToList();
            var secs = ids.Select(i => sec.GetValueOrDefault(i, "")).Distinct().ToList();
            if (secs.Count == 1 && AltOps.TryGetValue(secs[0], out var op) && !Catalog.Ops.ContainsKey(f["proposedAction"]?.ToString() ?? ""))
                map[string.Join("+", ids)] = op;
        }
        return map;
    }

    public static Plan Build(JsonNode report, JsonNode judge, IEnumerable<string> keys, string windowsDir,
                             IReadOnlyDictionary<string, string>? overrides = null)
    {
        var plan = new Plan { PlanId = "plan-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") };
        var findings = judge["judgement"]!["findings"]!.AsArray();
        var flags = report["flags"]!.AsArray().ToDictionary(f => f!["id"]!.ToString(), f => f!);
        var S = report["sections"]!;
        bool cacheAdded = false, scanAdded = false, browserAdded = false, powerAdded = false;

        foreach (var key in keys.Distinct())
        {
            var f = findings.FirstOrDefault(x => string.Join("+", x!["flagIds"]!.AsArray().Select(i => i!.ToString())) == key);
            if (f == null) { plan.Skipped.Add(new(key, key, "报告里找不到这一项")); continue; }
            var op = overrides?.GetValueOrDefault(key) ?? f["proposedAction"]?.ToString() ?? "none";
            var title = f["object"]?.ToString() ?? key;
            if (!Catalog.Ops.TryGetValue(op, out var info))
            {
                plan.Skipped.Add(new(key, title, op switch
                {
                    "driver.remove_after_reboot" or "driver.switch_to_newer" => "这是驱动类的红色操作，属于专业模式（v2），这一版只给建议",
                    "defender.review" => "这一项需要你在 Windows 安全中心里自己复核（我们不修改安全设置）",
                    _ => "这一项没有可以自动执行的操作",
                }));
                continue;
            }
            if (f["verdict"]?.ToString() == "normal" && overrides?.ContainsKey(key) != true) { plan.Skipped.Add(new(key, title, "研判为正常，不需要处理")); continue; }

            var its = f["flagIds"]!.AsArray().Select(i => flags.GetValueOrDefault(i!.ToString())).Where(x => x != null).Select(x => x!).ToList();
            var targets = new List<(string title, JsonObject target, bool? admin)>();
            string? reason = null;

            switch (op)
            {
                case "startup.disable":
                    foreach (var fl in its.Where(x => Sec(x) == "startup"))
                    {
                        var item = Find(S["startup"]?["items"], "name", fl["object"]?.ToString());
                        var t = item == null ? null : StartupTarget(item);
                        if (t != null) targets.Add((item!["name"]!.ToString(), t, t["approvedHive"]!.ToString() == "HKLM"));
                    }
                    break;
                case "task.disable":
                case "task.delete_leftover":
                    foreach (var fl in its.Where(x => Sec(x) == "scheduledTasks"))
                    {
                        if (op == "task.delete_leftover" && fl["rule"]?.ToString() != "target-missing") continue;
                        var item = Find(S["scheduledTasks"]?["items"], "name", fl["object"]?.ToString());
                        if (item == null) continue;
                        var folder = item["path"]?.ToString() ?? "\\";
                        var full = (folder.EndsWith('\\') ? folder : folder + "\\") + item["name"];
                        var exe = item["actions"]?.AsArray().FirstOrDefault()?["execute"]?.ToString()?.Trim('"') ?? "";
                        targets.Add((item["name"]!.ToString(), new JsonObject { ["fullName"] = full, ["executable"] = exe }, null));
                    }
                    break;
                case "service.set_manual":
                    foreach (var fl in its.Where(x => Sec(x) == "services"))
                    {
                        var item = Find(S["services"]?["items"], "name", fl["object"]?.ToString());
                        if (item == null) continue;
                        if (IsMicrosoft(item)) { reason = "这是 Windows 或微软自带的服务，按规则不修改它的启动方式"; continue; }
                        targets.Add((item["displayName"]?.ToString() ?? item["name"]!.ToString(), new JsonObject { ["name"] = item["name"]!.ToString() }, null));
                    }
                    break;
                case "service.delete_orphan":
                    foreach (var fl in its.Where(x => x["rule"]?.ToString() == "binary-missing" && Sec(x) is "services" or "drivers"))
                        targets.Add((fl["object"]!.ToString(), new JsonObject { ["name"] = fl["object"]!.ToString() }, null));
                    break;
                case "app.uninstall":
                    foreach (var fl in its.Where(x => Sec(x) == "software"))
                        if (Find(S["software"]?["items"], "name", fl["object"]?.ToString()) is { } app)
                            targets.Add((app["name"]!.ToString(), new JsonObject { ["displayName"] = app["name"]!.ToString() }, null));
                    if (targets.Count == 0) reason = "请在「设置 → 应用 → 安装的应用」里卸载对应的软件";
                    break;
                case "driver.delete_old_version":
                    foreach (var fl in its.Where(x => x["rule"]?.ToString() == "duplicate-versions"))
                        foreach (var p in S["drivers"]?["packages"]?["items"]?.AsArray() ?? [])
                            if (p?["inf"]?.ToString() == fl["object"]?.ToString() && string.IsNullOrEmpty(p?["usedBy"]?.ToString())
                                && !IsMicrosoftProvider(p!["provider"]?.ToString()))
                                targets.Add(($"{p["inf"]}（{p["published"]}，{p["version"]}）",
                                    new JsonObject { ["publishedName"] = p["published"]!.ToString(), ["inf"] = p["inf"]!.ToString() }, null));
                    break;
                case "cache.clean":
                    if (!cacheAdded) { cacheAdded = true; targets.Add(("临时文件和缓存", new JsonObject(), null)); }
                    else reason = "已合并到上面的清理缓存";
                    break;
                case "defender.scan":
                    if (!scanAdded) { scanAdded = true; targets.Add(("这台电脑", new JsonObject(), null)); } else reason = "已合并到上面的扫描";
                    break;
                case "browser.disable_background":
                    if (!browserAdded) { browserAdded = true; targets.Add(("Microsoft Edge", new JsonObject(), null)); } else reason = "已合并";
                    break;
                case "power.disable_fast_startup":
                    if (!powerAdded) { powerAdded = true; targets.Add(("快速启动", new JsonObject(), null)); } else reason = "已合并";
                    break;
            }

            if (targets.Count == 0) { plan.Skipped.Add(new(key, title, reason ?? "在报告里没找到可以处理的具体对象")); continue; }
            var n = 0;
            foreach (var (t, target, admin) in targets)
                plan.Steps.Add(new PlanStep
                {
                    Key = $"{key}#{n++}", FindingKey = key, Op = op, OpName = info.Name, Title = t, Risk = info.Risk,
                    Change = info.Change, Cost = info.Cost, Undo = info.Undo, Reversible = info.Reversible,
                    NeedsAdmin = admin ?? info.NeedsAdmin, Target = target,
                    CacheOptions = op == "cache.clean" ? new CacheOptions { Categories = [.. CacheClean.Categories] } : null,
                });
        }
        return plan;

        static string? Sec(JsonNode fl) => fl["section"]?.ToString();
    }

    static JsonNode? Find(JsonNode? items, string field, string? value) =>
        value == null ? null : items?.AsArray().FirstOrDefault(x => x?[field]?.ToString() == value);

    static bool IsMicrosoft(JsonNode item)
    {
        var who = $"{item["company"]} {item["signer"]}";
        return who.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(item["serviceDll"]?.ToString());
    }

    static bool IsMicrosoftProvider(string? p) => p != null && p.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>自启动项对应的 StartupApproved 位置（任务管理器"启动应用"用的同一个地方）。</summary>
    public static JsonObject? StartupTarget(JsonNode item)
    {
        var src = item["source"]?.ToString();
        var loc = item["location"]?.ToString() ?? "";
        var name = item["name"]?.ToString() ?? "";
        const string ap = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        if (src == "registry")
        {
            var (hive, key) = Operations.ParseRegPath(loc);
            var wow = key.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
            if (!key.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase)) return null;  // RunOnce 等不处理
            return new JsonObject
            {
                ["approvedHive"] = hive == Hive.CurrentUser ? "HKCU" : "HKLM",
                ["approvedKey"] = hive == Hive.CurrentUser ? ap + "Run" : ap.Replace("Software", "SOFTWARE") + (wow ? "Run32" : "Run"),
                ["valueName"] = name, ["runKey"] = loc,
            };
        }
        if (src == "startup-folder")
        {
            var common = loc.Contains("ProgramData", StringComparison.OrdinalIgnoreCase);
            return new JsonObject
            {
                ["approvedHive"] = common ? "HKLM" : "HKCU",
                ["approvedKey"] = (common ? ap.Replace("Software", "SOFTWARE") : ap) + "StartupFolder",
                ["valueName"] = name, ["path"] = loc.TrimEnd('\\') + "\\" + name,
            };
        }
        return null;
    }
}
