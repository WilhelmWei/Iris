using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>规则的默认结论：结论、置信度、建议操作（null 表示 none）、给用户的说明（{object} 换成对象名）。</summary>
public sealed record RuleDefault(string Verdict, double Confidence, string? Action, string Text);

/// <summary>
/// 纯规则模式（对应 judge/offline.py）：不联网、不用 Key，也能出一份基础报告。
/// 生成和模型输出同样格式的结论，之后照样经过知识库合并、校验、代码算分和渲染。
/// 结论来源依次是：用户确认过的说明 → 知识库提示 → 按规则写好的默认结论。
/// </summary>
public static class Offline
{
    static RuleDefault R(string v, double c, string? a, string t) => new(v, c, a, t);
    static RuleDefault Unsure(string t) => new("uncertain", 0.5, "ask_user", t);

    /// <summary>RULES：规则 -> 默认结论。</summary>
    public static readonly IReadOnlyDictionary<string, RuleDefault> Rules = new Dictionary<string, RuleDefault>
    {
        // 资源
        ["cpu-busy-idle"] = R("problem", 0.7, "none", "采集时 CPU 一直很忙。可以打开任务管理器按 CPU 排序，看看是谁在占用"),
        ["gpu-busy-idle"] = R("problem", 0.6, "none", "没开游戏和视频时显卡也很忙，值得看看是哪个程序（挖矿程序也会这样，但更常见的是浏览器或壁纸软件）"),
        ["defender-cpu-heavy"] = R("problem", 0.7, "none", "Defender 开机以来一直占用大量 CPU，可能有扫描卡住了。可以在「Windows 安全中心 → 病毒和威胁防护」看看是否有扫描在进行"),
        ["long-uptime"] = R("problem", 0.8, "power.disable_fast_startup", "很久没有真正重启过。开着快速启动时，点\"关机\"再开机并不等于重启，建议定期\"重启\"一次"),
        ["memory-compression-busy"] = R("problem", 0.7, "none", "内存压缩一直在忙，说明内存不够用了。卡顿、发热时先考虑内存不足，而不是中毒"),
        ["memory-high"] = R("problem", 0.7, "none", "内存占用很高。关掉不用的程序和浏览器标签页，比任何\"内存加速球\"都有效"),
        // 进程
        ["heavy-memory"] = R("normal", 0.6, "none", "{object} 占用内存较多，如果正在使用属于正常"),
        ["runs-from-user-folder"] = Unsure("{object} 从用户目录运行。很多正规软件也这样安装，需要你确认是不是自己装的"),
        ["unsigned"] = Unsure("{object} 没有有效的数字签名，需要你确认是不是自己装的、来源可靠的软件"),
        ["system-name-outside-windows"] = R("suspicious", 0.7, "defender.scan", "{object} 用了系统程序的名字，却不在 Windows 目录里，这是恶意程序常用的伪装手法，建议交给 Defender 扫描"),
        // 自启动 / 计划任务 / 服务
        ["unsigned-user-folder"] = Unsure("{object} 开机自启动、在用户目录里且没有有效签名，需要你确认是不是自己装的"),
        ["target-missing"] = R("leftover", 0.85, null, "{object} 指向的程序已经不存在，是软件卸载后留下的残留"),
        ["frequent"] = R("normal", 0.6, "none", "{object} 频繁定时运行，多数是软件的自动更新"),
        ["script-engine"] = Unsure("{object} 通过脚本程序运行，正规软件少见这种做法，需要确认来源"),
        ["seed-bundle"] = Unsure("{object} 属于常见的捆绑 / 全家桶软件，需要你确认是不是主动安装、还在用"),
        ["seed-anticheat-accel"] = Unsure("{object} 像是游戏反作弊、加速器或下载器的组件，需要你确认还在用对应的软件"),
        ["seed-bank-security"] = Unsure("{object} 像是网银安全控件，需要你确认还在用这家银行的网银"),
        ["binary-missing"] = R("leftover", 0.75, "service.delete_orphan", "{object} 的程序文件已经不存在，只剩下登记信息，是卸载或手动清理后的残留"),
        ["runs-from-user-folder-service"] = Unsure("{object} 是从用户目录运行的服务，需要确认来源"),
        ["server-service-running"] = R("problem", 0.6, "service.set_manual", "{object} 是服务器类服务，个人电脑一般用不到，可能是装软件时被顺带开启的"),
        ["svchost-third-party-dll"] = Unsure("{object} 是寄宿在系统 svchost 进程里的第三方组件，需要确认是哪个软件的"),
        // 驱动与系统设置
        ["boot-start-third-party"] = R("normal", 0.6, "none", "{object} 是在系统启动早期加载的第三方驱动，仅供参考；这类驱动不能直接删除"),
        ["duplicate-versions"] = R("leftover", 0.8, "driver.delete_old_version", "驱动仓库里保留了没有设备在用的旧版本，可以删除（系统会自动保护在用的版本）"),
        ["memory-integrity-off"] = R("problem", 0.7, "none", "内存完整性没有开启，常见原因是装过不兼容的旧驱动"),
        ["third-party-class-filter"] = Unsure("{object} 挂在设备的过滤器上，处理不当会让键盘、USB 等设备失灵，需要确认它属于哪个软件"),
        ["newer-driver-not-used"] = Unsure("{object} 驱动仓库里有更新的版本没有被使用，换驱动有风险，需要确认"),
        ["battery-worn"] = R("problem", 0.8, "none", "电池已经明显老化，续航会变短"),
        ["fast-startup-on"] = R("problem", 0.6, "power.disable_fast_startup", "开着快速启动：点\"关机\"其实是半休眠，长期不重启会积累内存占用和小毛病"),
        ["slow-charging"] = R("problem", 0.6, "none", "充电功率偏低，可以检查充电器和数据线"),
        ["bsod"] = R("problem", 0.8, "none", "最近出现过蓝屏"),
        ["bsod-repeated"] = R("problem", 0.9, "none", "最近反复蓝屏，需要尽快排查"),
        // 软件
        ["duplicate-category"] = R("normal", 0.6, "none", "同类软件装了好几个（{object}），不常用的可以卸载"),
        ["large-on-c"] = R("problem", 0.6, "none", "{object} 装在 C 盘且占用很大，C 盘紧张时可以考虑卸载或改装到其他盘"),
        // 存储
        ["system-drive-low"] = R("problem", 0.9, "cache.clean", "C 盘空间不足，会影响系统更新和运行速度"),
        ["chat-cache-large"] = R("problem", 0.8, "none", "{object} 的聊天文件很大。图片、视频、文件属于你的数据，可以在软件设置里清理，或者把存储位置改到其他盘"),
        ["junk-cleanable"] = R("problem", 0.9, "cache.clean", "程序留下的缓存可以直接清理，它们都会在需要时自动重新生成"),
        ["temp-large"] = R("problem", 0.8, "cache.clean", "临时文件占用较大，可以清理"),
        ["downloads-large"] = R("problem", 0.6, "none", "下载文件夹很大，里面多是用过的安装包和文件，可以自己整理一下"),
        ["conda-cache"] = R("problem", 0.7, "none", "conda 的软件包缓存可以用 conda clean -a 清理（不影响已建好的环境）"),
        ["dev-cache"] = R("problem", 0.6, "cache.clean", "{object} 是开发工具的缓存，可以清理，之后需要时会重新下载"),
        ["games-on-c"] = R("problem", 0.6, "none", "{object} 游戏库在 C 盘，C 盘紧张时可以在启动器里把游戏移到其他盘"),
        ["system-leftover"] = R("leftover", 0.8, "none", "{object} 是系统升级或更新留下的文件，确认系统正常后，可以用 Windows 自带的「磁盘清理 → 清理系统文件」删除"),
        // 安全
        ["active-threat"] = R("suspicious", 0.9, "defender.scan", "Defender 报告了尚未处理的威胁：{object}"),
        ["detection-not-remediated"] = R("suspicious", 0.7, "defender.scan", "Defender 检测到的威胁没有处理成功：{object}"),
        ["defender-allowed-threat"] = Unsure("{object} 曾被 Defender 检测到，后来被手动设为\"允许\"，之后不会再报。需要确认是不是你自己放行的"),
        ["defender-exclusion"] = R("uncertain", 0.5, "defender.review", "Defender 有排除项（不扫描的位置或程序），需要确认都是你自己加的"),
        ["defender-exclusion-orphan"] = R("leftover", 0.85, "defender.review", "排除的目录已经不存在，软件卸载后排除项没清掉，等于留了一个不被扫描的位置"),
        ["defender-rtp-off"] = R("problem", 0.9, "none", "Defender 实时保护是关着的。本工具不修改安全设置，建议你在 Windows 安全中心里打开"),
        ["defender-signature-old"] = R("problem", 0.8, "none", "Defender 的病毒库很久没更新了，可以在 Windows 安全中心里检查更新"),
        ["firewall-off"] = R("problem", 0.9, "none", "防火墙是关着的。本工具不修改安全设置，建议你在 Windows 安全中心里打开"),
        ["uac-off"] = R("problem", 0.9, "none", "用户账户控制（UAC）被关闭了，程序可以不经确认获得管理员权限。建议在控制面板里打开"),
        // 浏览器与网络
        ["edge-preload"] = R("bloat", 0.7, "browser.disable_background", "Edge 开机就在后台预加载，即使不用也占内存"),
        ["edge-idle-background"] = R("bloat", 0.8, "browser.disable_background", "你平时不用 Edge，它却开机就留在后台、占着内存。关掉启动增强和后台运行，不影响你偶尔打开它"),
        ["chrome-idle-background"] = R("bloat", 0.75, "browser.disable_chrome_background", "你平时不用 Chrome，它关掉窗口后还在后台运行。关掉后台运行，不影响你偶尔打开它"),
        ["browser-unused"] = R("bloat", 0.7, "app.uninstall", "{object} 很久没用过了，却还在开机自启动或后台运行。不用的话可以卸载；这类浏览器常是装别的软件时被捆绑装上的"),
        ["hosts-custom"] = Unsure("hosts 文件里有自定义条目，可能是软件或你自己加的，也可能被篡改，需要确认"),
        ["proxy-remote"] = Unsure("系统设置了代理服务器。如果你没有开 VPN 或代理软件，需要警惕"),
        ["proxy-pac"] = Unsure("系统设置了自动代理脚本（PAC）。如果你没有开 VPN 或代理软件，需要警惕"),
    };

    /// <summary>SECTION_RULES：同名规则在不同节里含义不同的，用 "节:规则" 覆盖。</summary>
    public static readonly IReadOnlyDictionary<string, RuleDefault> SectionRules = new Dictionary<string, RuleDefault>
    {
        ["services:runs-from-user-folder"] = Rules["runs-from-user-folder-service"],
        ["startup:target-missing"] = R("leftover", 0.85, "startup.disable", "{object} 开机自启动指向的程序已经不存在，是卸载残留"),
        ["scheduledTasks:target-missing"] = R("leftover", 0.85, "task.delete_leftover", "{object} 指向的程序已经不存在，是软件卸载后留下的残留"),
    };

    /// <summary>QUESTIONS：拿不准时问用户的具体问题（没有的用通用问题）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Questions = new Dictionary<string, string>
    {
        ["defender-allowed-threat"] = "这是你自己在 Windows 安全中心点\"允许\"放行的吗？对应的软件现在还在用吗？",
        ["defender-exclusion"] = "这些排除项都是你自己加的吗？对应的软件还在用吗？",
        ["seed-bank-security"] = "你还在用需要这个安全控件的网银吗？",
        ["seed-anticheat-accel"] = "你还在用对应的游戏、加速器或下载软件吗？",
        ["seed-bundle"] = "这是你主动安装、现在还在用的软件吗？",
        ["hosts-custom"] = "hosts 文件里的条目是你或你信任的软件加的吗？",
        ["proxy-remote"] = "你现在开着 VPN 或代理软件吗？",
        ["proxy-pac"] = "你现在开着 VPN 或代理软件吗？",
    };

    public const string DefaultQuestion = "这是你自己安装或设置的吗？现在还在用吗？";

    public static readonly IReadOnlySet<string> MalwareRules = new HashSet<string> { "active-threat", "detection-not-remediated", "system-name-outside-windows" };

    /// <summary>TIPS：信息差小知识，和这台电脑命中的规则相关才给。</summary>
    public static readonly IReadOnlyList<(IReadOnlySet<string> rules, string title, string body)> Tips =
    [
        (new HashSet<string> { "defender-exclusion", "defender-exclusion-orphan" }, "卸载软件后，Defender 排除项不会自动删除",
            "有些软件安装时会让 Defender 不扫描自己的目录或进程名，卸载后这些排除项还在。按进程名的排除（比如 Update.exe）对任何同名程序都生效，等于留了一个口子。"),
        (new HashSet<string> { "long-uptime", "fast-startup-on" }, "点\"关机\"不等于重启",
            "Windows 默认开着快速启动，关机时把系统内核存进休眠文件，下次开机直接恢复。所以长期\"关机\"也会积累内存占用和小毛病，真正清空要点\"重启\"。"),
        (new HashSet<string> { "chat-cache-large" }, "微信、QQ 的聊天文件默认存在 C 盘",
            "聊天里收到的图片、视频、文件默认存在 C 盘\"文档\"里，几年下来几十 GB 很常见。可以在微信 / QQ 的设置里把文件位置改到其他盘。"),
        (new HashSet<string> { "seed-bundle", "target-missing" }, "装软件时留意捆绑勾选",
            "国产软件安装界面常默认勾选\"顺便安装\"其他软件；\"全家桶\"卸载后也常留下计划任务、服务和驱动。安装时选\"自定义安装\"，把不需要的勾去掉。"),
        (new HashSet<string> { "memory-high", "memory-compression-busy" }, "\"内存加速球\"是伪优化",
            "清理内存只是把数据挤到硬盘上，过一会儿又会读回来，反而更慢。真正有用的是关掉不用的程序和浏览器标签页，或者加内存。"),
        (new HashSet<string> { "junk-cleanable", "system-drive-low" }, "清缓存腾出的空间，过一阵会慢慢回来",
            "浏览器缓存、临时文件清掉后，程序用到时会重新生成，所以\"一键清理\"腾出的空间会慢慢回来。C 盘真正的大头通常是聊天文件、下载文件夹和游戏，把它们挪到其他盘效果更持久。"),
        (new HashSet<string> { "edge-idle-background", "chrome-idle-background", "browser-unused" }, "不用的浏览器也在后台占内存",
            "很多浏览器为了\"秒开\"，开机就在后台预加载，即使你从来不点它。只关掉不用的那几个的后台运行就好，常用的浏览器不用动。"),
        (new HashSet<string> { "defender-allowed-threat" }, "\"允许\"一个威胁之后，Defender 就不会再提醒",
            "在 Windows 安全中心点了\"允许\"的项目会一直被放行，即使文件后来换成了别的东西。不再需要时，可以在「保护历史记录 → 允许的威胁」里撤销。"),
    ];

    /// <summary>_default()</summary>
    static RuleDefault Default(JsonNode item)
    {
        var key = $"{Py.ToStr(item["section"])}:{Py.ToStr(item["rule"])}";
        var rule = Py.S(item["rule"]);
        if (SectionRules.TryGetValue(key, out var d) || (rule is not null && Rules.TryGetValue(rule, out d)))
            return d with { Action = d.Action ?? "none" };
        return Unsure("{object}：规则预筛命中，纯规则模式下无法判断，需要你确认");
    }

    static readonly Dictionary<string, int> SeverityOrder = new() { ["high"] = 0, ["medium"] = 1, ["low"] = 2, ["info"] = 3 };

    /// <summary>offline_output()：对发给模型的那部分证据包，按规则生成和模型同格式的输出。</summary>
    public static JsonObject Output(JsonObject modelEvidence)
    {
        var allItems = Py.Iter(modelEvidence["items"]).OfType<JsonObject>().ToList();
        // 同一规则、同一对象（如 5 条排除项）合并
        var groups = new List<(string key, JsonNode? rule, JsonNode? obj, List<JsonObject> items)>();
        foreach (var it in allItems)
        {
            var key = Py.Key(it["rule"]) + "\u0001" + Py.Key(it["object"]);
            var g = groups.FindIndex(x => x.key == key);
            if (g < 0) groups.Add((key, it["rule"], it["object"], [it]));
            else groups[g].items.Add(it);
        }

        var findings = new List<JsonObject>();
        foreach (var (_, ruleNode, obj, items) in groups)
        {
            var first = items[0];
            var k = first["knowledge"] as JsonObject;
            var confirmed = first["userConfirmed"];
            string verdict, text;
            double conf;
            JsonNode? action;
            if (Py.Truthy(confirmed))
            {
                (verdict, conf, action) = ("normal", 0.8, "none");
                text = $"你以前确认过：{Py.ToStr(Py.Get(confirmed, "note"))}";
            }
            else if (Py.Truthy(k))
            {
                verdict = Py.ToStr(k!["verdict"]);
                conf = Py.S(k["certainty"]) != "guess" ? 0.7 : 0.5;
                action = Py.Truthy(k["proposedAction"]) ? k["proposedAction"]!.DeepClone() : "ask_user";
                text = Py.StrOr(k["explanation"]);
            }
            else
            {
                var d = Default(first);
                (verdict, conf, action) = (d.Verdict, d.Confidence, d.Action);
                text = d.Text.Replace("{object}", Py.ToStr(obj));
            }
            JsonNode? question = null;
            if (verdict == "uncertain" || conf < 0.6 || Py.S(action) == "ask_user")
            {
                var kq = Py.Get(k, "question");
                var rule = Py.S(ruleNode);
                question = Py.Truthy(kq) ? kq!.DeepClone()
                    : rule is not null && Questions.TryGetValue(rule, out var q) ? q : DefaultQuestion;
            }
            findings.Add(new JsonObject
            {
                ["flagIds"] = Py.List(items.Select(it => Py.Clone(it["flagId"]))),
                ["object"] = Py.Clone(obj),
                ["verdict"] = verdict,
                ["confidence"] = conf,
                ["evidence"] = string.Join("；", items.Select(it => $"{Py.ToStr(it["flagId"])} {Py.StrOr(it["evidence"])}")),
                ["proposedAction"] = action,
                ["userExplanation"] = text,
                ["questionForUser"] = question,
            });
        }

        var rules = allItems.Select(it => Py.S(it["rule"])).Where(r => r is not null).Select(r => r!).ToHashSet();
        var bad = allItems.Where(it => Py.S(it["rule"]) is { } r && MalwareRules.Contains(r)).ToList();
        int SevOf(JsonObject f) => allItems
            .Where(it => Py.Iter(f["flagIds"]).Any(x => Py.Eq(x, it["flagId"])))
            .Select(it => Py.S(it["severity"]) is { } s && SeverityOrder.TryGetValue(s, out var o) ? o : 9)
            .Min();
        var todo = findings.Where(f => Py.S(f["verdict"]) is "suspicious" or "problem" or "leftover")
            .OrderBy(SevOf).ToList(); // OrderBy 是稳定排序，同 Python 的 sorted
        var nQ = findings.Count(f => Py.Truthy(f["questionForUser"]));
        var tips = Tips.Where(t => t.rules.Overlaps(rules)).Take(3).Select(t => (JsonNode?)new JsonObject
        {
            ["title"] = t.title,
            ["body"] = t.body,
            ["relatedFlagIds"] = Py.List(allItems.Where(it => Py.S(it["rule"]) is { } r && t.rules.Contains(r)).Select(it => Py.Clone(it["flagId"]))),
        });
        return new JsonObject
        {
            ["headline"] = $"纯规则模式（没有调用 AI）：{todo.Count} 项建议处理，{nQ} 项需要你确认",
            ["malwareSuspected"] = bad.Count > 0,
            ["malwareReasoning"] = bad.Count > 0
                ? "规则发现了需要核实的迹象：" + string.Join("；", bad.Select(it => $"{Py.ToStr(it["object"])}（{Py.ToStr(it["evidence"])}）"))
                : "规则没有发现中毒迹象：没有 Defender 未处理的威胁，没有伪装成系统程序的进程。纯规则模式不结合上下文判断，结论比 AI 研判粗。",
            ["top3"] = Py.StrList(todo.Take(3).Select(f => $"{Py.ToStr(f["object"])}：{Py.ToStr(f["userExplanation"])}")),
            ["findings"] = Py.List(findings),
            ["tips"] = Py.List(tips),
            ["notCollected"] = new JsonArray(),
        };
    }
}
