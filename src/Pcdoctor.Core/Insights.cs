using System.Globalization;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 给界面用的派生信息：每个方面查到了什么（实时发现流）、和上次体检比有什么变化、AI 和规则是否一致、
/// 内测反馈包的额外去隐私、版本比较。都是纯函数，桌面程序和测试共用。
/// </summary>
public static class Insights
{
    /// <summary>采集脚本的节名 → 界面上的名字（顺序和采集顺序一致）。</summary>
    public static readonly (string Key, string Title)[] Sections =
    [
        ("resources", "资源占用"), ("processes", "进程"), ("startup", "开机自启动"), ("services", "服务"), ("scheduledTasks", "计划任务"),
        ("drivers", "驱动"), ("software", "已装软件"), ("storage", "存储"), ("power", "电源与电池"), ("security", "安全设置"),
        ("browserNetwork", "浏览器与网络"), ("versions", "驱动与系统版本"), ("stability", "稳定性"),
    ];

    static int? Count(JsonNode? n) => n switch
    {
        JsonArray a => a.Count,
        JsonObject o when o["items"] is JsonArray items => items.Count,
        JsonObject o when o["total"] is JsonValue t && t.TryGetValue<int>(out var x) => x,
        JsonValue v when v.TryGetValue<int>(out var x) => x,
        _ => null,
    };
    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;
    static string F(double x) => x.ToString(x >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture);

    /// <summary>每个方面一行：查了多少、有几处值得看、用时。字段缺失时只写能确定的部分。</summary>
    public static JsonArray SectionSummaries(JsonNode report)
    {
        var s = report["sections"];
        var flags = report["flags"] as JsonArray ?? [];
        var secs = report["tool"]?["sectionSeconds"];
        var rows = new JsonArray();
        foreach (var (key, title) in Sections)
        {
            var x = s?[key];
            if (x == null) continue;
            var parts = new List<string>();
            switch (key)
            {
                case "resources":
                    if (Num(x["uptimeDays"]) is { } up) parts.Add(up < 1 ? $"开机 {F(up * 24)} 小时" : $"开机 {F(up)} 天");
                    if (Num(x["memory"]?["usedPct"]) is { } mem) parts.Add($"内存占用 {F(mem)}%");
                    break;
                case "processes": if (Count(x["total"]) is { } p) parts.Add($"{p} 个进程"); break;
                case "startup": if (Count(x["total"]) is { } st) parts.Add($"{st} 项自启动"); break;
                case "services": if (Count(x["thirdPartyCount"]) is { } sv) parts.Add($"{sv} 个第三方服务"); break;
                case "scheduledTasks": if (Count(x["nonMicrosoftCount"]) is { } t) parts.Add($"{t} 个非微软任务"); break;
                case "drivers": if (Count(x["kernelDrivers"]?["thirdPartyCount"]) is { } d) parts.Add($"{d} 个第三方驱动"); break;
                case "software": if (Count(x["total"]) is { } sw) parts.Add($"{sw} 个软件"); break;
                case "storage":
                    var c = (x["volumes"] as JsonArray)?.FirstOrDefault(v => v?["drive"]?.ToString() == "C:");
                    if (Num(c?["freeGB"]) is { } free && Num(c?["freePct"]) is { } pct) parts.Add($"C 盘剩余 {F(free)} GB（{F(pct)}%）");
                    break;
                case "power":
                    if (Num(x["fastStartup"]) is { } fs) parts.Add(fs == 1 ? "快速启动开着" : "快速启动已关闭");
                    if (Num(x["battery"]?["healthPct"]) is { } hp) parts.Add($"电池健康度 {F(hp)}%");
                    break;
                case "security":
                    if (x["defender"]?["realTimeProtection"] is JsonValue rt && rt.TryGetValue<bool>(out var on)) parts.Add(on ? "Defender 实时保护开着" : "Defender 实时保护关着");
                    if (Count(x["allowedThreats"]) is > 0 and var at) parts.Add($"{at} 条被放行的威胁");
                    break;
                case "browserNetwork": if (Count(x["extensions"]) is { } ex) parts.Add($"{ex} 个浏览器扩展"); break;
                case "versions": if (Count(x["displayAdapters"]) is { } da) parts.Add($"{da} 个显卡"); break;
                case "stability":
                    var bsod = Count(x["bugChecks30d"]) ?? 0;
                    parts.Add(bsod > 0 ? $"30 天内 {bsod} 次蓝屏" : "30 天内没有蓝屏");
                    break;
            }
            var n = flags.Count(f => f?["section"]?.ToString() == key);
            rows.Add(new JsonObject
            {
                ["key"] = key, ["title"] = title, ["detail"] = string.Join("，", parts) + (n > 0 ? $"，{n} 处值得看" : ""),
                ["flags"] = n, ["sec"] = Num(secs?[key]),
            });
        }
        return rows;
    }

    static readonly Dictionary<string, string> VerdictName = new()
    {
        ["suspicious"] = "可疑", ["problem"] = "需处理", ["leftover"] = "卸载残留", ["bloat"] = "捆绑 / 常驻", ["uncertain"] = "拿不准", ["normal"] = "正常",
    };

    /// <summary>
    /// 和上一次体检比较：新出现的问题、已经不再出现的问题、C 盘空间变化。按对象名比较（预筛编号每次会变）。
    /// 返回 [{kind: add|fix|info, text, why}]，最多 8 条。
    /// </summary>
    public static JsonArray Changes(JsonNode newJudge, JsonNode oldJudge, JsonNode? newReport = null, JsonNode? oldReport = null)
    {
        static Dictionary<string, JsonNode> Issues(JsonNode j) =>
            (j["judgement"]?["findings"] as JsonArray ?? [])
                .Where(f => f?["verdict"]?.ToString() is { } v && v != "normal")
                .GroupBy(f => f!["object"]?.ToString() ?? "").Where(g => g.Key.Length > 0)
                .ToDictionary(g => g.Key, g => g.First()!);
        var now = Issues(newJudge);
        var before = Issues(oldJudge);
        var rows = new JsonArray();
        foreach (var (obj, f) in now.Where(kv => !before.ContainsKey(kv.Key)).Take(4))
            rows.Add(new JsonObject { ["kind"] = "add", ["text"] = $"新出现：{obj}（{VerdictName.GetValueOrDefault(f["verdict"]!.ToString(), "")}）", ["why"] = Short(f["userExplanation"]?.ToString()) });
        foreach (var obj in before.Keys.Where(k => !now.ContainsKey(k)).Take(3))
            rows.Add(new JsonObject { ["kind"] = "fix", ["text"] = $"已不再出现：{obj}", ["why"] = "上次体检时还在，这次没有了" });
        static double? CFree(JsonNode? r) => Num((r?["sections"]?["storage"]?["volumes"] as JsonArray)?.FirstOrDefault(v => v?["drive"]?.ToString() == "C:")?["freeGB"]);
        if (CFree(newReport) is { } a && CFree(oldReport) is { } b && Math.Abs(a - b) >= 1)
            rows.Add(new JsonObject { ["kind"] = a < b ? "add" : "fix", ["text"] = $"C 盘可用空间{(a < b ? "减少" : "增加")} {F(Math.Abs(a - b))} GB", ["why"] = $"上次 {F(b)} GB，这次 {F(a)} GB" });
        return rows;
    }

    /// <summary>一条结论的"指纹"：它每条预筛结果的 规则|对象。预筛编号每次体检都会变，指纹不变；没有预筛信息时退回用结论的对象名。</summary>
    public static HashSet<string> Prints(JsonNode report, JsonNode finding)
    {
        var byId = (report["flags"] as JsonArray ?? []).Where(f => f?["id"] != null)
            .GroupBy(f => f!["id"]!.ToString()).ToDictionary(g => g.Key, g => g.First()!);
        var set = new HashSet<string>();
        foreach (var id in finding["flagIds"] as JsonArray ?? [])
            if (id != null && byId.TryGetValue(id.ToString(), out var f))
                set.Add($"{f["rule"]}|{f["object"]}");
        if (set.Count == 0 && finding["object"]?.ToString() is { Length: > 0 } o) set.Add("object|" + o);
        return set;
    }

    static string KeyOf(JsonNode f) => string.Join("+", (f["flagIds"] as JsonArray ?? []).Select(x => x?.ToString()));
    static IEnumerable<JsonNode> Findings(JsonNode judge) => (judge["judgement"]?["findings"] as JsonArray ?? []).Where(f => f != null)!;

    /// <summary>把上一份报告里选中（还没处理）的结论，对到新报告里还存在、仍需处理的结论上。返回新报告里的结论编号。</summary>
    public static List<string> CarryKeys(JsonNode oldReport, JsonNode oldJudge, IEnumerable<string> oldKeys, JsonNode newReport, JsonNode newJudge)
    {
        var wanted = oldKeys.ToHashSet();
        var prints = Findings(oldJudge).Where(f => wanted.Contains(KeyOf(f))).SelectMany(f => Prints(oldReport, f)).ToHashSet();
        return Findings(newJudge)
            .Where(f => f["verdict"]?.ToString() != "normal" && Prints(newReport, f).Overlaps(prints))
            .Select(KeyOf).Distinct().ToList();
    }

    /// <summary>上一份报告里处理过的结论，在新报告里还有几条（任一条预筛结果还在就算还在）。</summary>
    public static int StillPresent(JsonNode oldReport, JsonNode oldJudge, IEnumerable<string> oldKeys, JsonNode newReport, JsonNode newJudge)
    {
        var now = Findings(newJudge).SelectMany(f => Prints(newReport, f)).ToHashSet();
        var wanted = oldKeys.ToHashSet();
        return Findings(oldJudge).Where(f => wanted.Contains(KeyOf(f))).Count(f => Prints(oldReport, f).Overlaps(now));
    }

    static string Short(string? s) => s == null ? "" : s.Length <= 48 ? s : s[..46] + "…";

    /// <summary>
    /// 交叉校验：同一批预筛结果，AI 的结论和纯规则的结论是否一致。键是 AI 结论的预筛编号（用 + 连接），值是一句说明。
    /// 知识库直接下的结论两边相同，不列出。
    /// </summary>
    public static JsonObject CrossCheck(JsonNode aiResult, JsonNode offlineResult)
    {
        var rule = new Dictionary<string, string>();
        foreach (var f in offlineResult["judgement"]?["findings"] as JsonArray ?? [])
            foreach (var id in f?["flagIds"] as JsonArray ?? [])
                rule[id!.ToString()] = f!["verdict"]?.ToString() ?? "";
        var o = new JsonObject();
        foreach (var f in aiResult["judgement"]?["findings"] as JsonArray ?? [])
        {
            if (f?["kbId"] != null) continue;
            var ids = (f?["flagIds"] as JsonArray ?? []).Select(x => x!.ToString()).ToList();
            var ruleVerdicts = ids.Where(rule.ContainsKey).Select(i => rule[i]).Distinct().ToList();
            if (ids.Count == 0 || ruleVerdicts.Count == 0) continue;
            var ai = f!["verdict"]?.ToString() ?? "";
            o[string.Join("+", ids)] = ruleVerdicts.Contains(ai)
                ? "规则和 AI 的结论一致"
                : $"AI 判为「{VerdictName.GetValueOrDefault(ai, ai)}」，固定规则判为「{string.Join(" / ", ruleVerdicts.Select(v => VerdictName.GetValueOrDefault(v, v)))}」，值得多想一下";
        }
        return o;
    }

    /// <summary>Iris 还不认识的组件数：落在程序类方面（进程、自启动、服务、计划任务、驱动、软件）、不是正常、也没有知识库条目的对象。</summary>
    public static int UnknownObjects(JsonNode report, JsonNode judge)
    {
        var program = new HashSet<string> { "processes", "startup", "services", "scheduledTasks", "drivers", "software" };
        var section = (report["flags"] as JsonArray ?? []).Where(f => f?["id"] != null)
            .ToDictionary(f => f!["id"]!.ToString(), f => f!["section"]?.ToString() ?? "");
        return (judge["judgement"]?["findings"] as JsonArray ?? [])
            .Where(f => f?["kbId"] == null && f?["verdict"]?.ToString() != "normal")
            .Where(f => (f?["flagIds"] as JsonArray ?? []).Any(id => program.Contains(section.GetValueOrDefault(id!.ToString(), ""))))
            .Select(f => f!["object"]?.ToString()).Distinct().Count();
    }

    /// <summary>
    /// 发给 Iris 服务器之前，在已打码（用户名、计算机名、邮箱）的报告上再去掉网络相关的信息：
    /// 代理服务器地址、DNS 服务器、hosts 条目（可能暴露公司内网或常去的网站），只留数量。原地修改。
    /// </summary>
    public static JsonNode ScrubForFeedback(JsonNode redactedReport)
    {
        if (redactedReport["sections"]?["browserNetwork"] is JsonObject bn)
        {
            if (bn["proxy"] is JsonObject proxy && proxy["server"] != null) proxy["server"] = "[已去掉]";
            if (bn["proxy"] is JsonObject p2 && p2["pac"] is JsonValue) p2["pac"] = "[已去掉]";
            if (bn["dns"] is JsonArray dns) bn["dns"] = new JsonObject { ["interfaces"] = dns.Count, ["removed"] = true };
            if (bn["hostsEntries"] is JsonArray hosts) bn["hostsEntries"] = new JsonObject { ["count"] = hosts.Count, ["removed"] = true };
        }
        return redactedReport;
    }

    /// <summary>语义化版本比较：1.0.0-beta.1 &lt; 1.0.0-beta.2 &lt; 1.0.0-rc.1 &lt; 1.0.0 &lt; 1.0.1。</summary>
    public static int CompareVersions(string a, string b)
    {
        static (int[] core, string[] pre) Parse(string v)
        {
            v = v.Trim().TrimStart('v').Split('+')[0];
            var i = v.IndexOf('-');
            var core = (i < 0 ? v : v[..i]).Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray();
            return (core, i < 0 ? [] : v[(i + 1)..].Split('.'));
        }
        var (ca, pa) = Parse(a);
        var (cb, pb) = Parse(b);
        for (var i = 0; i < 3; i++) if (ca[i] != cb[i]) return ca[i].CompareTo(cb[i]);
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length);  // 正式版比同号的预发布版新
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var x);
            var nb = int.TryParse(pb[i], out var y);
            var c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return Math.Sign(c);
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
