using System.Text;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>误判复核表（对应 judge/review.py 的 rows_from_judge / write_review）：每条结论人工标"对 / 错 / 不准"。</summary>
public static class Review
{
    public static readonly IReadOnlyList<string> Columns = ["编号", "对象", "模型结论", "来源", "置信度", "建议操作", "给用户的说明", "判定", "应该是", "备注"];

    /// <summary>review_path()：xxx-judge.json → xxx-review.csv。</summary>
    public static string ReviewPath(string judgePath) =>
        judgePath.EndsWith("-judge.json", StringComparison.Ordinal)
            ? judgePath[..^"-judge.json".Length] + "-review.csv"
            : Path.ChangeExtension(judgePath, null) + "-review.csv";

    // csv 模块写 None 时是空字符串，其他值用 str()
    static string Cell(JsonNode? v) => v is null ? "" : Py.ToStr(v);

    /// <summary>rows_from_judge()：每行按 Columns 的顺序。</summary>
    public static List<Dictionary<string, string>> RowsFromJudge(JsonNode result)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (var f in Py.Iter(Py.Get(Py.Get(result, "judgement"), "findings")))
        {
            var kbId = Py.Get(f, "kbId");
            rows.Add(new Dictionary<string, string>
            {
                ["编号"] = string.Join("、", Py.Iter(Py.Get(f, "flagIds")).Select(Py.ToStr)),
                ["对象"] = Cell(Py.Get(f, "object")),
                ["模型结论"] = MarkdownRender.VerdictName(Py.Get(f, "verdict")),
                ["来源"] = Py.Truthy(kbId) ? $"知识库 {Py.ToStr(kbId)}" : "模型",
                ["置信度"] = Py.Fixed(Py.Num(Py.Get(f, "confidence")), 2),
                ["建议操作"] = Cell(Py.Or(Py.Get(f, "actionName"), Py.Get(f, "proposedAction"), JsonValue.Create(""))),
                ["给用户的说明"] = Cell(Py.Or(Py.Get(f, "userExplanation"), JsonValue.Create(""))),
                ["判定"] = "", ["应该是"] = "", ["备注"] = "",
            });
        }
        return rows;
    }

    static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>CSV 文本（Python csv 默认方言：逗号分隔、必要时加引号、\r\n 换行），不含 BOM。</summary>
    public static string ToCsv(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(Quote))).Append("\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", Columns.Select(c => Quote(r.TryGetValue(c, out var v) ? v : "")))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>write_review()：UTF-8 带 BOM（Excel 双击打开时靠 BOM 识别 UTF-8，否则中文乱码）。</summary>
    public static void WriteCsv(string path, IEnumerable<IReadOnlyDictionary<string, string>> rows) =>
        File.WriteAllText(path, ToCsv(rows), new UTF8Encoding(true));

    /// <summary>从研判结果直接写复核表。</summary>
    public static void WriteFromJudge(string path, JsonNode result) => WriteCsv(path, RowsFromJudge(result));
}
