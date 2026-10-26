using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>测试共用：仓库路径、构造预筛结果、JSON 语义比较。</summary>
internal static class T
{
    public static readonly string RepoRoot = FindRoot();

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "pcdoctor.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录（pcdoctor.sln）");
    }

    public static string RepoPath(params string[] parts) => Path.Combine([RepoRoot, .. parts]);

    public static readonly List<JsonObject> KB = Kb.LoadEmbedded();

    public static JsonObject Flag(int i, string section, string rule, string severity, string obj, string evidence = "证据") => new()
    {
        ["id"] = $"F{i:000}", ["section"] = section, ["rule"] = rule, ["severity"] = severity, ["object"] = obj, ["evidence"] = evidence,
    };

    public static JsonObject Finding(string[] ids, string action, string verdict = "leftover", double confidence = 0.9,
                                     string obj = "x", string? question = null, string? kbId = null)
    {
        var f = new JsonObject
        {
            ["flagIds"] = Py.StrList(ids), ["object"] = obj, ["verdict"] = verdict, ["confidence"] = confidence,
            ["evidence"] = "e", ["proposedAction"] = action, ["userExplanation"] = "说明", ["questionForUser"] = question,
        };
        if (kbId is not null) f["kbId"] = kbId;
        return f;
    }

    public static JsonNode J(string json) => JsonNode.Parse(json)!;

    public static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>语义比较：对象不看键的顺序，数字按值比较；ignore 里的键在任何层级都跳过。返回第一处差异，相同返回 null。</summary>
    public static string? Diff(JsonNode? expected, JsonNode? actual, string path = "$", ISet<string>? ignore = null)
    {
        var ke = Py.KindOf(expected);
        var ka = Py.KindOf(actual);
        bool numE = ke is Py.Kind.Int or Py.Kind.Float, numA = ka is Py.Kind.Int or Py.Kind.Float;
        if (numE && numA)
            return Py.Num(expected) == Py.Num(actual) || (double.IsNaN(Py.Num(expected)) && double.IsNaN(Py.Num(actual)))
                ? null : $"{path}: 期望 {Py.Repr(expected)}，实际 {Py.Repr(actual)}";
        if (ke != ka) return $"{path}: 类型不同，期望 {Py.Repr(expected)}，实际 {Py.Repr(actual)}";
        switch (ke)
        {
            case Py.Kind.Dict:
            {
                var e = (JsonObject)expected!; var a = (JsonObject)actual!;
                foreach (var (k, v) in e)
                {
                    if (ignore?.Contains(k) == true) continue;
                    if (!a.ContainsKey(k)) return $"{path}.{k}: 实际缺少这个键";
                    var d = Diff(v, a[k], $"{path}.{k}", ignore);
                    if (d is not null) return d;
                }
                foreach (var (k, _) in a)
                    if (ignore?.Contains(k) != true && !e.ContainsKey(k)) return $"{path}.{k}: 实际多了这个键";
                return null;
            }
            case Py.Kind.List:
            {
                var e = (JsonArray)expected!; var a = (JsonArray)actual!;
                if (e.Count != a.Count) return $"{path}: 长度不同，期望 {e.Count}，实际 {a.Count}\n期望 {Py.Repr(e)}\n实际 {Py.Repr(a)}";
                for (int i = 0; i < e.Count; i++)
                {
                    var d = Diff(e[i], a[i], $"{path}[{i}]", ignore);
                    if (d is not null) return d;
                }
                return null;
            }
            default:
                return Py.Eq(expected, actual) ? null : $"{path}: 期望 {Py.Repr(expected)}，实际 {Py.Repr(actual)}";
        }
    }

    public static void SameJson(JsonNode? expected, JsonNode? actual, params string[] ignore)
    {
        var d = Diff(expected, actual, "$", ignore.Length > 0 ? new HashSet<string>(ignore) : null);
        Assert.True(d is null, d);
    }

    /// <summary>逐行比较长文本，报告第一处不同的行。</summary>
    public static void SameText(string expected, string actual)
    {
        if (expected == actual) return;
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var x = i < e.Length ? e[i] : "<无>";
            var y = i < a.Length ? a[i] : "<无>";
            if (x != y) Assert.Fail($"第 {i + 1} 行不同：\n期望：{x}\n实际：{y}");
        }
        Assert.Fail("文本不同（行内容相同，可能是结尾）");
    }

    public static JsonObject ById(JsonNode result, string flagId) =>
        (JsonObject)Py.Iter(result["judgement"]!["findings"]).First(f => Py.Iter(f!["flagIds"]).Any(x => Py.S(x) == flagId))!;

    public static List<string> Issues(JsonNode result) => Py.Iter(result["issues"]).Select(Py.ToStr).ToList();
}
