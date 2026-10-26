using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Core;

/// <summary>
/// 知识库（对应 judge/kb.py）：已经弄清楚的对象直接下结论，其余作为提示交给模型。
/// 每条的 match 条件全部满足才算命中；when 按"某软件装没装"区分情况。
/// direct=true 且 certainty=confirmed：直接生成结论，不发给模型，但照样经过 Validation 的操作库和红线检查。
/// 用户确认过的条目（userConfirmed）不直接下结论，让模型结合用户的说明。
/// </summary>
public static class Kb
{
    static readonly string[] TextKeys = ["object", "path", "company", "evidence"];

    /// <summary>load_kb()：从嵌入的 seed.json 读取条目。</summary>
    public static List<JsonObject> LoadEmbedded() => Parse(Resources.ReadText(Resources.SeedJson));

    /// <summary>load_kb(path)</summary>
    public static List<JsonObject> Load(string path) => Parse(File.ReadAllText(path));

    public static List<JsonObject> Parse(string json) =>
        Py.Arr(JsonNode.Parse(json), "entries").OfType<JsonObject>().Select(e => (JsonObject)e.DeepClone()).ToList();

    static IEnumerable<JsonNode?> AsList(JsonNode? v) => v is JsonArray a ? a : new[] { v };

    static Regex Rx(string pattern) => RegexCache.Get(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>_search：text 为真且任一正则命中。</summary>
    static bool Search(JsonNode? patterns, string? text) =>
        !string.IsNullOrEmpty(text) && AsList(patterns).Any(p => Rx(Py.ToStr(p)).IsMatch(text));

    /// <summary>_installed</summary>
    static bool Installed(JsonNode? pattern, JsonArray software)
    {
        var rx = Rx(Py.ToStr(pattern));
        return software.Any(a => rx.IsMatch($"{Py.StrOr(Py.Get(a, "name"))} {Py.StrOr(Py.Get(a, "publisher"))}"));
    }

    /// <summary>lookup()：返回第一条命中的知识库条目；path 是对象所在位置（证据包里的 FirstPath）。</summary>
    public static JsonObject? Lookup(JsonNode flag, JsonNode? ctx, JsonNode? path, JsonArray software, IEnumerable<JsonObject> entries)
    {
        var c = ctx as JsonObject ?? new JsonObject();
        var texts = new Dictionary<string, string?>
        {
            ["object"] = TextOf(Py.Get(flag, "object")),
            ["path"] = TextOf(path),
            // 厂商可能在上下文的不同字段里，svchost 那类规则只写在证据里
            ["company"] = string.Join(" ", new[] { "company", "signer", "provider", "publisher" }.Select(k => Py.StrOr(Py.Get(c, k))))
                          + " " + Py.StrOr(Py.Get(flag, "evidence")),
            ["evidence"] = TextOf(Py.Get(flag, "evidence")),
        };
        foreach (var e in entries)
        {
            var m = Py.Obj(e, "match");
            if (m.ContainsKey("section") && !AsList(m["section"]).Any(s => Py.Eq(s, Py.Get(flag, "section")))) continue;
            if (m.ContainsKey("rule") && !AsList(m["rule"]).Any(s => Py.Eq(s, Py.Get(flag, "rule")))) continue;
            if (!TextKeys.Where(m.ContainsKey).All(k => Search(m[k], texts[k]))) continue;
            if (Py.Truthy(m["signed"]) && !(Py.S(c["signature"]) == "Valid" || Py.StrOr(Py.Get(flag, "evidence")).Contains("签名：Valid")))
                continue; // 按名字和路径识别时要求签名有效，防止恶意程序冒名
            var w = Py.Obj(e, "when");
            if (w.ContainsKey("installed") && !Installed(w["installed"], software)) continue;
            if (w.ContainsKey("notInstalled") && Installed(w["notInstalled"], software)) continue;
            return e;
        }
        return null;
    }

    // 只有为真的值才拿去匹配（Python 里 bool(text) 为假直接不命中）
    static string? TextOf(JsonNode? n) => Py.Truthy(n) ? Py.ToStr(n) : null;

    /// <summary>is_direct()</summary>
    public static bool IsDirect(JsonNode entry) => Py.Truthy(Py.Get(entry, "direct")) && Py.S(Py.Get(entry, "certainty")) == "confirmed";

    /// <summary>note()：附在证据包条目上的提示。</summary>
    public static JsonObject Note(JsonObject entry)
    {
        var n = new JsonObject
        {
            ["kbId"] = Py.Clone(entry["id"]),
            ["product"] = Py.Clone(entry["product"]),
            ["verdict"] = Py.Clone(entry["verdict"]),
            ["proposedAction"] = Py.Clone(entry["proposedAction"]),
            ["explanation"] = Py.Clone(entry["explanation"]),
            ["certainty"] = Py.Clone(entry["certainty"]),
            ["direct"] = IsDirect(entry),
        };
        if (Py.Truthy(entry["question"])) n["question"] = Py.Clone(entry["question"]);
        return n;
    }

    /// <summary>
    /// split_direct()：把证据包拆成"发给模型的部分"和"知识库直接给出的结论"（与模型输出同格式，之后一起校验）。
    /// </summary>
    public static (JsonObject modelEvidence, JsonArray findings) SplitDirect(JsonObject evidence, IEnumerable<JsonObject> entries)
    {
        var byId = new Dictionary<string, JsonObject>();
        foreach (var e in entries) byId[Py.Key(e["id"])] = e;
        var groups = new List<(JsonNode? id, List<JsonObject> items)>();
        var keep = new List<JsonObject>();
        foreach (var it in Py.Arr(evidence, "items").OfType<JsonObject>())
        {
            var k = it["knowledge"];
            if (Py.Truthy(k) && Py.Truthy(Py.Get(k, "direct")) && !it.ContainsKey("userConfirmed"))
            {
                var id = Py.Get(k, "kbId");
                var g = groups.FindIndex(x => Py.Key(x.id) == Py.Key(id));
                if (g < 0) groups.Add((id, [it]));
                else groups[g].items.Add(it);
            }
            else keep.Add(it);
        }

        var findings = new JsonArray();
        foreach (var (kbId, items) in groups)
        {
            var e = byId[Py.Key(kbId)];
            var objects = new List<string>();
            foreach (var it in items)
            {
                var o = Py.ToStr(it["object"]);
                if (!objects.Contains(o)) objects.Add(o);
            }
            findings.Add(new JsonObject
            {
                ["flagIds"] = Py.List(items.Select(it => Py.Clone(it["flagId"]))),
                ["object"] = $"{Py.ToStr(e["product"])}：{string.Join("、", objects)}",
                ["verdict"] = Py.Clone(e["verdict"]),
                ["confidence"] = Py.Clone(e["confidence"]),
                ["evidence"] = string.Join("；", items.Select(it => $"{Py.ToStr(it["flagId"])} {Py.StrOr(it["evidence"])}"))
                               + $"（知识库 {Py.ToStr(kbId)}：{Py.StrOr(e["source"])}）",
                ["proposedAction"] = Py.Truthy(e["proposedAction"]) ? Py.Clone(e["proposedAction"]) : "none",
                ["userExplanation"] = Py.Clone(e["explanation"]),
                ["questionForUser"] = Py.Clone(e["question"]),
                ["kbId"] = Py.Clone(kbId),
            });
        }

        var modelEv = (JsonObject)evidence.DeepClone();
        modelEv["items"] = Py.List(keep.Select(it => it.DeepClone()));
        var used = keep.Select(it => Py.Key(it["contextRef"])).ToHashSet();
        var shared = new JsonObject();
        foreach (var (k, v) in Py.Obj(evidence, "sharedContext"))
            if (used.Contains("s:" + k)) shared[k] = Py.Clone(v);
        modelEv["sharedContext"] = shared;
        if (findings.Count > 0)
        {
            // 让模型知道这些已经有结论，写总结和"最该处理的 3 件事"时可以参考，但不用再逐条研判
            modelEv["handledByKnowledgeBase"] = Py.List(findings.Select(f => (JsonNode?)new JsonObject
            {
                ["flagIds"] = Py.Clone(f!["flagIds"]),
                ["object"] = Py.Clone(f["object"]),
                ["verdict"] = Py.Clone(f["verdict"]),
                ["proposedAction"] = Py.Clone(f["proposedAction"]),
            }));
        }
        return (modelEv, findings);
    }
}

/// <summary>正则缓存：知识库和注入检测的正则会被反复用到。</summary>
internal static class RegexCache
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, RegexOptions), Regex> Cache = new();

    public static Regex Get(string pattern, RegexOptions options) =>
        Cache.GetOrAdd((pattern, options), k => new Regex(k.Item1, k.Item2));
}
