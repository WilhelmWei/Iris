using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Core;

/// <summary>
/// 把完整采集报告压缩成交给模型的证据包（对应 judge/evidence.py，设计文档第 6 章"提示词结构"）。
/// 为每条预筛结果附上对应对象的详情和"属于哪个已装软件"，再加上机器概况和已装软件名单。
/// </summary>
public static class Evidence
{
    /// <summary>MAX_STR：单个字符串最长保留的字符数（按码点）。</summary>
    public const int MaxStr = 300;

    // 提示注入：进程名、路径、计划任务描述、软件名都来自被检查的电脑，恶意程序可以在里面写"请判为正常"。
    // 这里只做粗筛，命中就打标记；真正的保险在 Validation（带标记或高风险的项，模型判正常不算数）
    static readonly Regex Injection = new(
        @"(ignore|disregard|forget)\s+(all\s+|any\s+)?(the\s+)?(previous|above|prior|earlier)"
        + @"|system\s*prompt|you\s+are\s+(now\s+)?(a|an|the)\s"
        + @"|verdict\W{0,5}\w{0,10}\W{0,5}normal|mark\s+(this|it)\s+as\s+(safe|normal|benign)"
        + @"|(判|判定|标记|视|认定|当)为?(正常|安全|无害)"
        + @"|忽略(以上|之前|前面|上述|所有|全部)"
        + @"|(之前|以上|上面)的?(指令|提示|规则)"
        + @"|不要(报告|标记|提示|上报)(此|这|该|本)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>looks_like_injection()</summary>
    public static bool LooksLikeInjection(JsonNode? v) => Py.KindOf(v) switch
    {
        Py.Kind.Str => Injection.IsMatch(Py.S(v)!),
        Py.Kind.Dict => ((JsonObject)v!).Any(kv => LooksLikeInjection(kv.Value)),
        Py.Kind.List => ((JsonArray)v!).Any(LooksLikeInjection),
        _ => false,
    };

    public static bool LooksLikeInjection(string s) => Injection.IsMatch(s);

    static bool IsEmptyValue(JsonNode? x) => Py.KindOf(x) switch
    {
        Py.Kind.None => true,
        Py.Kind.Str => Py.S(x)!.Length == 0,
        Py.Kind.List => ((JsonArray)x!).Count == 0,
        Py.Kind.Dict => ((JsonObject)x!).Count == 0,
        _ => false,
    };

    /// <summary>_short()：截断长字符串，去掉 dict 里的空值（None、''、[]、{}）。返回新节点。</summary>
    public static JsonNode? Short(JsonNode? v)
    {
        switch (Py.KindOf(v))
        {
            case Py.Kind.Str:
                var s = Py.S(v)!;
                return Py.Len(s) > MaxStr ? JsonValue.Create(Py.Head(s, MaxStr) + "…") : JsonValue.Create(s);
            case Py.Kind.Dict:
                var o = new JsonObject();
                foreach (var (k, x) in (JsonObject)v!)
                    if (!IsEmptyValue(x)) o[k] = Short(x);
                return o;
            case Py.Kind.List:
                return Py.List(((JsonArray)v!).Select(Short));
            default:
                return Py.Clone(v);
        }
    }

    internal static string NormPath(JsonNode? p)
    {
        if (!Py.Truthy(p)) return "";
        return Py.ToStr(p).Replace('/', '\\').Trim('"').TrimEnd('\\').ToLowerInvariant();
    }

    static readonly HashSet<string> NoiseTokens = ["co", "ltd", "inc", "llc", "corp", "corporation", "limited", "gmbh", "pbc", "ag", "sa", "plc", "the"];
    static readonly string[] NoiseCn = ["股份有限公司", "有限责任公司", "有限公司", "公司"];
    static readonly Regex CompanySplit = new(@"[^0-9a-z\u4e00-\u9fff]+", RegexOptions.CultureInvariant);

    /// <summary>_norm_company()：'Zhuhai Kingsoft Office Software Co.,Ltd' -> 'zhuhaikingsoftofficesoftware'</summary>
    internal static string NormCompany(JsonNode? c)
    {
        if (!Py.Truthy(c)) return "";
        var s = Py.ToStr(c).ToLowerInvariant();
        foreach (var w in NoiseCn) s = s.Replace(w, "");
        return string.Concat(CompanySplit.Split(s).Where(t => t.Length > 0 && !NoiseTokens.Contains(t)));
    }

    // 微软的产品太多，按厂商对不出具体是哪个软件
    static bool GenericCompany(string c) => c.StartsWith("microsoft", StringComparison.Ordinal);

    // 太宽泛、不能用来判断归属的安装目录
    static readonly HashSet<string> GenericDirs =
        ["c:\\program files", "c:\\program files (x86)", "c:\\windows", "c:\\windows\\system32", "c:\\programdata", "c:", "d:", "c:\\users"];

    /// <summary>按安装目录和厂商，把进程、服务、驱动、计划任务对到已装软件上（SoftwareIndex）。</summary>
    public sealed class SoftwareIndex
    {
        readonly List<(string loc, JsonNode name)> _byLocation = [];
        readonly List<(string pub, JsonNode name)> _byCompany = [];

        public SoftwareIndex(JsonArray? softwareItems)
        {
            foreach (var a in softwareItems ?? [])
            {
                var name = Py.Get(a, "name");
                var loc = NormPath(Py.Get(a, "location"));
                if (Py.Truthy(name) && loc.Length > 0 && !GenericDirs.Contains(loc) && Py.Len(loc) > 3)
                    _byLocation.Add((loc, name!));
                var pub = NormCompany(Py.Get(a, "publisher"));
                if (Py.Truthy(name) && Py.Len(pub) >= 4 && !GenericCompany(pub))
                    _byCompany.Add((pub, name!));
            }
            // 最长前缀优先（稳定排序，同 Python 的 sort）
            var sorted = _byLocation.OrderByDescending(x => Py.Len(x.loc)).ToList();
            _byLocation.Clear();
            _byLocation.AddRange(sorted);
        }

        /// <summary>owner()：返回软件名（可能是"、"连接的多个），对不上返回 null。</summary>
        public JsonNode? Owner(JsonNode? path = null, JsonNode? company = null)
        {
            var p = NormPath(path);
            if (p.Length > 0)
                foreach (var (loc, name) in _byLocation)
                    if (p == loc || p.StartsWith(loc + "\\", StringComparison.Ordinal))
                        return name;
            var c = NormCompany(company);
            if (Py.Len(c) >= 4 && !GenericCompany(c))
            {
                var hits = new Dictionary<string, JsonNode>();
                foreach (var (pub, name) in _byCompany)
                    if (c == pub || pub.Contains(c, StringComparison.Ordinal) || c.Contains(pub, StringComparison.Ordinal))
                        hits.TryAdd(Py.ToStr(name), name);
                if (hits.Count > 0)
                    return JsonValue.Create(string.Join("、", Py.Sorted(hits.Keys).Take(3)));
            }
            return null;
        }

        public string? Owner(string? path, string? company = null) =>
            Py.S(Owner(Py.Str(path), Py.Str(company)));
    }

    static JsonNode? Find(JsonNode? items, string key, JsonNode? value)
    {
        foreach (var it in Py.Iter(items))
            if (Py.Eq(Py.Get(it, key), value)) return it;
        return null;
    }

    /// <summary>_first_path()：对象里最能代表"程序在哪"的路径。</summary>
    public static JsonNode? FirstPath(JsonNode? obj)
    {
        if (obj is not JsonObject o) return null;
        var acts = Py.Or(o["actions"], new JsonArray());
        if (Py.Truthy(acts) && acts is JsonArray a && a[0] is JsonObject a0 && Py.Truthy(a0["execute"]))
            return a0["execute"]; // 计划任务：path 是任务所在的文件夹（如 \），不是程序位置
        foreach (var k in new[] { "serviceDll", "path", "exe" })
            if (Py.Truthy(o[k])) return o[k];
        return null;
    }

    /// <summary>_context_for()：按节和对象名找到报告里对应的原始数据。返回的是原数据的引用或新建的对象，调用方不要修改。</summary>
    public static JsonNode? ContextFor(JsonNode flag, JsonNode? S)
    {
        var sec = Py.S(Py.Get(flag, "section"));
        var obj = Py.Get(flag, "object");
        var rule = Py.S(Py.Get(flag, "rule")) ?? "";
        var objKey = Py.S(obj);
        switch (sec)
        {
            case "processes":
            case "services":
            case "scheduledTasks":
            case "startup":
                return Find(Py.Get(Py.Get(S, sec), "items"), "name", obj);
            case "drivers":
            {
                var d = Py.Get(S, "drivers");
                if (rule == "duplicate-versions")
                    return new JsonObject
                    {
                        ["packages"] = Py.List(Py.Iter(Py.Get(Py.Get(d, "packages"), "items"))
                            .Where(p => Py.Eq(Py.Get(p, "inf"), obj)).Select(Py.Clone)),
                    };
                if (rule == "memory-integrity-off") return Py.Get(d, "virtualizationSecurity");
                var ctx = new JsonObject();
                if (Find(Py.Get(Py.Get(d, "kernelDrivers"), "items"), "name", obj) is JsonObject kd)
                    foreach (var (k, v) in kd) ctx[k] = Py.Clone(v);
                var filters = Py.Iter(Py.Get(d, "classFilters")).Where(f => Py.Eq(Py.Get(f, "driver"), obj)).ToList();
                if (filters.Count > 0)
                    ctx["classFilters"] = Py.List(filters.Select(f => (JsonNode?)new JsonObject
                    {
                        ["className"] = Py.Clone(Py.Get(f, "className")),
                        ["kind"] = Py.Clone(Py.Get(f, "kind")),
                    }));
                return ctx.Count > 0 ? ctx : null;
            }
            case "software":
            {
                var sw = Py.Get(S, "software");
                if (rule == "duplicate-category")
                    return new JsonObject { ["apps"] = Py.Clone(objKey is null ? null : Py.Get(Py.Get(sw, "duplicateCategories"), objKey)) };
                return Find(Py.Get(sw, "items"), "name", obj);
            }
            case "security":
            {
                var sc = Py.Get(S, "security");
                if (rule.StartsWith("defender-exclusion", StringComparison.Ordinal))
                    return new JsonObject { ["exclusions"] = Py.Clone(Py.Get(sc, "defenderExclusions")) };
                if (rule == "defender-allowed-threat")
                    return new JsonObject { ["allowedThreats"] = Py.Clone(Py.Get(sc, "allowedThreats")) };
                if (rule == "detection-not-remediated")
                    return new JsonObject { ["recentDetections"] = Py.Clone(Py.Get(sc, "recentDetections")) };
                return new JsonObject { ["defender"] = Py.Clone(Py.Get(sc, "defender")) };
            }
            case "storage":
            {
                var st = Py.Get(S, "storage");
                var folders = Py.Get(st, "folders");
                var junk = Py.Or(Py.Get(st, "junk"), new JsonObject());
                if (rule == "chat-cache-large")
                    return objKey is null ? null : Py.Get(Py.Get(folders, "chatApps"), objKey);
                if (rule == "junk-cleanable")
                    return new JsonObject
                    {
                        ["cleanableMB"] = Py.Clone(Py.Get(junk, "cleanableMB")),
                        ["items"] = Py.List(Py.Iter(Py.Get(junk, "items"))
                            .Where(x => Py.S(Py.Get(x, "tier")) == "A" && (Py.Truthy(Py.Get(x, "cleanableMB")) ? Py.Num(Py.Get(x, "cleanableMB")) : 0) > 0)
                            .Select(x => (JsonNode?)new JsonObject
                            {
                                ["name"] = Py.Clone(Py.Get(x, "name")), ["group"] = Py.Clone(Py.Get(x, "group")),
                                ["cleanableMB"] = Py.Clone(Py.Get(x, "cleanableMB")), ["running"] = Py.Clone(Py.Get(x, "running")),
                            })),
                    };
                if (rule == "system-leftover")
                    return Find(Py.Get(junk, "items"), "name", obj);
                if (rule == "system-drive-low")
                {
                    var bigOnC = Py.Iter(Py.Get(Py.Get(S, "software"), "items"))
                        .Where(a => Py.StrOr(Py.Get(a, "location")).ToLowerInvariant().StartsWith("c:", StringComparison.Ordinal)
                                    && (Py.Truthy(Py.Get(a, "sizeMB")) ? Py.ToFloat(Py.Get(a, "sizeMB")) : 0) >= 500)
                        .Take(10)
                        .Select(a => (JsonNode?)new JsonObject { ["name"] = Py.Clone(Py.Get(a, "name")), ["sizeMB"] = Py.Clone(Py.Get(a, "sizeMB")) });
                    var fs = new JsonObject();
                    if (folders is JsonObject fo)
                        foreach (var (k, v) in fo)
                            if (v is JsonObject vo && vo.ContainsKey("sizeGB"))
                                fs[k] = new JsonObject { ["sizeGB"] = Py.Clone(vo["sizeGB"]), ["path"] = Py.Clone(vo["path"]) };
                    return new JsonObject
                    {
                        ["volumes"] = Py.Clone(Py.Get(st, "volumes")),
                        ["system"] = Py.Clone(Py.Get(st, "system")),
                        ["folders"] = fs,
                        ["largeSoftwareOnC"] = Py.List(bigOnC),
                        ["steamLibraries"] = Py.Clone(Py.Get(st, "steamLibraries")),
                        ["cleanableCacheMB"] = Py.Clone(Py.Get(junk, "cleanableMB")),
                    };
                }
                return objKey is null ? null : Py.Get(folders, objKey);
            }
            case "stability":
                return Py.Get(S, "stability");
            case "resources":
            {
                var r = Py.Get(S, "resources");
                return new JsonObject
                {
                    ["memory"] = Py.Clone(Py.Get(r, "memory")),
                    ["memoryCompression"] = Py.Clone(Py.Get(r, "memoryCompression")),
                    ["topByMemory"] = Py.List(Py.Iter(Py.Or(Py.Get(r, "topByMemory"), new JsonArray())).Take(8).Select(Py.Clone)),
                    ["topByCpuSec"] = Py.List(Py.Iter(Py.Or(Py.Get(r, "topByCpuSec"), new JsonArray())).Take(8).Select(Py.Clone)),
                };
            }
            case "power":
                return Py.Get(S, "power");
            case "browserNetwork":
            {
                var b = Py.Get(S, "browserNetwork");
                if (rule is "edge-idle-background" or "chrome-idle-background" or "browser-unused")
                    return new JsonObject
                    {
                        ["mainBrowser"] = Py.Clone(Py.Get(b, "mainBrowser")), ["policies"] = Py.Clone(Py.Get(b, "policies")),
                        ["browsers"] = Py.List(Py.Iter(Py.Get(b, "browsers")).Select(x =>
                        {
                            var one = new JsonObject();
                            foreach (var k in new[] { "id", "name", "isDefault", "lastBrowsedDays", "processes", "memoryMB", "autostart" }) one[k] = Py.Clone(Py.Get(x, k));
                            return (JsonNode?)one;
                        })),
                    };
                var o = new JsonObject();
                foreach (var k in new[] { "policies", "edgeProcesses", "proxy", "hostsEntries" }) o[k] = Py.Clone(Py.Get(b, k));
                return o;
            }
            case "versions":
                return Py.Get(S, "versions");
        }
        return null;
    }

    /// <summary>
    /// build_evidence()。known：用户确认过的结论（KnownStore），命中的项会带上 userConfirmed；
    /// kb：知识库条目（Kb），命中的项会带上 knowledge。
    /// </summary>
    public static JsonObject Build(JsonNode report, IReadOnlyList<JsonObject>? known = null, IReadOnlyList<JsonObject>? kb = null)
    {
        known ??= [];
        kb ??= [];
        var S = Py.Get(report, "sections");
        var swItems = Py.Get(Py.Get(S, "software"), "items") as JsonArray ?? [];
        var index = new SoftwareIndex(swItems);

        var items = new List<JsonObject>();
        foreach (var f in Py.Iter(Py.Get(report, "flags")))
        {
            if (f is null) continue;
            var ctx = ContextFor(f, S);
            var item = new JsonObject
            {
                ["flagId"] = Py.Clone(Py.Get(f, "id")),
                ["section"] = Py.Clone(Py.Get(f, "section")),
                ["rule"] = Py.Clone(Py.Get(f, "rule")),
                ["severity"] = Py.Clone(Py.Get(f, "severity")),
                ["object"] = Py.Clone(Py.Get(f, "object")),
                ["evidence"] = Py.Clone(Py.Get(f, "evidence")),
            };
            var k = KnownStore.Match(f, known);
            if (k is not null)
                item["userConfirmed"] = new JsonObject { ["note"] = Py.Clone(k["note"]), ["date"] = Py.Clone(k["confirmedAt"]) };
            if (Py.Truthy(ctx))
            {
                item["context"] = Short(ctx);
                var owner = index.Owner(FirstPath(ctx), Py.Or(Py.Get(ctx, "company"), Py.Get(ctx, "signer"), Py.Get(ctx, "provider")));
                if (Py.Truthy(owner) && !Py.Eq(owner, Py.Get(f, "object")))
                    item["ownerSoftware"] = Py.Clone(owner);
            }
            if (LooksLikeInjection(Py.Get(f, "object")) || LooksLikeInjection(Py.Get(f, "evidence")) || LooksLikeInjection(ctx))
                item["injectionSuspect"] = true;
            var hit = kb.Count > 0 ? Kb.Lookup(f, ctx, FirstPath(ctx), swItems, kb) : null;
            if (hit is not null) item["knowledge"] = Kb.Note(hit);
            items.Add(item);
        }

        // 多条结果共用同一份上下文时（如 7 条排除项）只放一份，用 contextRef 引用
        var shared = new JsonObject();
        var seen = new List<(string key, List<JsonObject> group)>();
        foreach (var item in items)
        {
            if (!item.ContainsKey("context")) continue;
            var key = PyJson.Dumps(item["context"], sortKeys: true);
            var g = seen.FindIndex(x => x.key == key);
            if (g < 0) seen.Add((key, [item]));
            else seen[g].group.Add(item);
        }
        int n = 0;
        foreach (var (_, group) in seen)
        {
            if (group.Count <= 1) continue;
            var reference = $"C{++n}";
            shared[reference] = group[0]["context"]!.DeepClone();
            foreach (var item in group)
            {
                item.Remove("context");
                item["contextRef"] = reference;
            }
        }

        var r = Py.Get(S, "resources");
        var st = Py.Get(S, "storage");
        var sc = Py.Get(S, "security");
        var pw = Py.Get(S, "power");
        var mach = Py.Get(report, "machine");
        var machine = new JsonObject
        {
            ["manufacturer"] = Py.Clone(Py.Get(mach, "manufacturer")),
            ["model"] = Py.Clone(Py.Get(mach, "model")),
            ["os"] = Py.Clone(Py.Get(r, "os")), ["cpu"] = Py.Clone(Py.Get(r, "cpu")),
            ["memory"] = Py.Clone(Py.Get(r, "memory")), ["uptimeDays"] = Py.Clone(Py.Get(r, "uptimeDays")),
            ["volumes"] = Py.Clone(Py.Get(st, "volumes")),
            ["fastStartup"] = Py.Clone(Py.Get(pw, "fastStartup")), ["hibernateEnabled"] = Py.Clone(Py.Get(pw, "hibernateEnabled")),
            ["battery"] = Py.Clone(Py.Get(pw, "battery")),
            ["defender"] = Py.Clone(Py.Get(sc, "defender")), ["defenderRecentDetections"] = Py.Clone(Py.Get(sc, "recentDetections")),
            ["uacEnabled"] = Py.Clone(Py.Get(sc, "uacEnabled")), ["secureBoot"] = Py.Clone(Py.Get(sc, "secureBoot")),
            ["virtualizationSecurity"] = Py.Clone(Py.Get(Py.Get(S, "drivers"), "virtualizationSecurity")),
        };
        var tool = Py.Get(report, "tool");
        return new JsonObject
        {
            ["schema"] = "pcdoctor.evidence/0.1",
            ["collector"] = new JsonObject
            {
                ["reportTime"] = Py.Clone(Py.Get(report, "generatedAt")), // 模型据此判断事件是多久以前
                ["version"] = Py.Clone(Py.Get(tool, "version")),
                ["isAdmin"] = Py.Clone(Py.Get(tool, "isAdmin")),
                ["quick"] = Py.Clone(Py.Get(tool, "quick")),
                ["incomplete"] = Py.List(Py.Iter(Py.Or(Py.Get(tool, "incomplete"), new JsonArray())).Take(10).Select(Py.Clone)),
            },
            ["machine"] = Short(machine),
            ["items"] = Py.List(items),
            ["sharedContext"] = shared,
            ["installedSoftware"] = Py.StrList(swItems.Select(a =>
                $"{Py.ToStr(Py.Get(a, "name"))} | {Py.StrOr(Py.Get(a, "publisher"))}".TrimEnd(' ', '|'))),
        };
    }
}
