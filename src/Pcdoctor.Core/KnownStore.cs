using System.Text;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 用户确认过的结论（对应 judge/known.py）：设计文档里"拿不准就问用户"的答案要记住，下次不再问。
/// 存在报告所在目录的 pcdoctor-known.json，只在本机，不上传。
/// 按"规则 + 对象 + 证据"匹配：同一条规则、同一个对象、证据一字不差，才算同一件事；证据变了就重新问。
/// </summary>
public static class KnownStore
{
    public const string FileName = "pcdoctor-known.json";

    /// <summary>known_path()：报告旁边的 pcdoctor-known.json。</summary>
    public static string KnownPath(string reportPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".", FileName);

    /// <summary>load_known()：文件不存在时返回空列表。</summary>
    public static List<JsonObject> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        return Py.Arr(root, "entries").OfType<JsonObject>().Select(e => (JsonObject)e.DeepClone()).ToList();
    }

    /// <summary>save_known()：格式与 Python 的 json.dump(..., ensure_ascii=False, indent=1) 相同。</summary>
    public static void Save(string path, IEnumerable<JsonObject> entries)
    {
        var doc = new JsonObject
        {
            ["schema"] = "pcdoctor.known/0.1",
            ["entries"] = Py.List(entries.Select(e => (JsonNode?)e.DeepClone())),
        };
        File.WriteAllText(path, PyJson.Dumps(doc, indent: 1), new UTF8Encoding(false));
    }

    static bool SameKey(JsonNode? a, JsonNode? b) =>
        Py.Eq(Py.Get(a, "rule"), Py.Get(b, "rule")) && Py.Eq(Py.Get(a, "object"), Py.Get(b, "object"))
        && Py.Eq(Py.Get(a, "evidence"), Py.Get(b, "evidence"));

    /// <summary>match()：返回匹配的确认记录，没有返回 null。</summary>
    public static JsonObject? Match(JsonNode? flag, IEnumerable<JsonObject>? entries) =>
        entries?.FirstOrDefault(e => SameKey(e, flag));

    /// <summary>
    /// confirm()：把报告里指定编号的预筛结果记为"用户已确认"（加进 entries），返回新增的条目。
    /// 编号不存在时抛 ArgumentException（Python 是 ValueError）。
    /// </summary>
    public static List<JsonObject> Confirm(JsonArray flags, IEnumerable<string> ids, string note, List<JsonObject> entries, DateTime? today = null)
    {
        var byId = new Dictionary<string, JsonNode>();
        foreach (var f in flags.OfType<JsonObject>())
            if (Py.S(f["id"]) is { } id) byId[id] = f;
        var idList = ids.ToList();
        var unknown = idList.Where(i => !byId.ContainsKey(i)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"报告里没有这些编号：{Py.ReprList(unknown)}");
        var added = new List<JsonObject>();
        foreach (var i in idList)
        {
            var f = byId[i];
            if (Match(f, entries) is not null) continue;
            var e = new JsonObject
            {
                ["rule"] = Py.Clone(Py.Get(f, "rule")),
                ["object"] = Py.Clone(Py.Get(f, "object")),
                ["evidence"] = Py.Clone(Py.Get(f, "evidence")),
                ["note"] = note,
                ["confirmedAt"] = (today ?? DateTime.Today).ToString("yyyy-MM-dd"),
            };
            entries.Add(e);
            added.Add(e);
        }
        return added;
    }
}
