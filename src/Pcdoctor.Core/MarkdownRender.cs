using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>把研判结果渲染成给人看的中文 Markdown 报告（对应 judge/render.py）。</summary>
public static class MarkdownRender
{
    /// <summary>VERDICT_NAMES</summary>
    public static readonly IReadOnlyDictionary<string, string> VerdictNames = new Dictionary<string, string>
    {
        ["normal"] = "正常", ["problem"] = "需处理", ["leftover"] = "卸载残留", ["bloat"] = "捆绑 / 流氓",
        ["suspicious"] = "可疑", ["uncertain"] = "拿不准",
    };

    /// <summary>VERDICT_NAMES.get(v, v)</summary>
    public static string VerdictName(JsonNode? v) => Py.S(v) is { } s && VerdictNames.TryGetValue(s, out var n) ? n : Py.ToStr(v);

    static string Cell(JsonNode? s) => Py.StrOr(s).Replace("|", "\\|").Replace("\n", " ");

    static string Join(JsonNode? xs) => string.Join("、", Py.Iter(xs).Select(Py.ToStr));

    static readonly Dictionary<string, int> Order = new()
        { ["suspicious"] = 0, ["problem"] = 1, ["leftover"] = 2, ["bloat"] = 3, ["uncertain"] = 4, ["normal"] = 5 };

    /// <summary>render_markdown()</summary>
    public static string Render(JsonObject result)
    {
        var j = result["judgement"]!;
        var score = result["score"]!;
        var findings = Py.Iter(j["findings"]).ToList();
        var o = new List<string> { "# 电脑体检报告", "" };
        // headline / malwareReasoning 用 StrOr：模型输出不是对象时 judgement 里没有这两个字段（Python 版这里会 KeyError）
        o.Add($"**健康分 {Py.ToStr(score["total"])} / 100**　{Py.StrOr(j["headline"])}");
        o.Add("");
        if (Py.Truthy(result["offline"]))
            o.Add($"> 生成时间 {Py.ToStr(result["generatedAt"])} · 纯规则模式（没有联网、没有调用 AI） · 采集器 {Py.ToStr(result["collectorVersion"])}"
                  + "　结论来自固定规则和知识库，不结合上下文判断，比 AI 研判粗；拿不准的都列成了问题");
        else
            o.Add($"> 生成时间 {Py.ToStr(result["generatedAt"])} · 模型 {Py.ToStr(result["model"])} · 采集器 {Py.ToStr(result["collectorVersion"])}"
                  + "　健康分由代码按规则计算，模型只负责判断和解释");
        o.AddRange(["", "## 有没有中毒", ""]);
        o.Add((Py.Truthy(j["malwareSuspected"]) ? "**发现可疑迹象，需要进一步核实。** " : "**没有发现中毒迹象。** ") + Py.StrOr(j["malwareReasoning"]));
        o.AddRange(["", "> 查病毒交给 Windows 自带的 Microsoft Defender：本工具只读取它的检测结果（加 `-Scan` 运行时会先发起一次快速扫描），"
                        + "不修改任何安全设置。我们鼓励用好已有的强大工具，而不是重复造一个杀毒引擎。"]);

        var top3 = Py.Iter(j["top3"]).ToList();
        if (top3.Count > 0)
        {
            o.AddRange(["", "## 最该处理的 3 件事", ""]);
            o.AddRange(top3.Select((t, i) => $"{i + 1}. {Py.ToStr(t)}"));
        }

        var questions = findings.Where(f => Py.Truthy(Py.Get(f, "needsUser"))).ToList();
        if (questions.Count > 0)
        {
            o.AddRange(["", "## 需要你确认的问题", ""]);
            o.AddRange(questions.Select(f => $"- **{Cell(Py.Get(f, "object"))}**：{Py.ToStr(Py.Get(f, "questionForUser"))}"));
        }

        var kbIds = Py.Iter(Py.Or(result["kbFlagIds"], new JsonArray())).ToList();
        if (kbIds.Count > 0)
            o.AddRange(["", $"> 有 {kbIds.Count} 项是已经弄清楚的常见软件组件，由知识库直接给出结论（表格里标\"知识库\"），没有交给模型"]);

        var confirmed = Py.Iter(Py.Or(result["confirmedFlagIds"], new JsonArray())).ToList();
        if (confirmed.Count > 0)
            o.AddRange(["", $"> 你以前确认过的 {confirmed.Count} 项这次不再提问（记录在报告旁边的 pcdoctor-known.json）"]);

        var tips = Py.Iter(j["tips"]).ToList();
        if (tips.Count > 0)
        {
            o.AddRange(["", "## 小知识", ""]);
            foreach (var t in tips) o.AddRange([$"**{Py.ToStr(Py.Get(t, "title"))}**", "", Py.ToStr(Py.Get(t, "body")), ""]);
        }

        o.AddRange(["", "## 逐项结论", "", "| 对象 | 结论 | 置信度 | 建议 | 风险 | 说明 |", "| --- | --- | --- | --- | --- | --- |"]);
        foreach (var f in findings.OrderBy(f => Py.S(Py.Get(f, "verdict")) is { } v && Order.TryGetValue(v, out var n) ? n : 9))
        {
            var conf = Py.Fixed(Py.Num(Py.Get(f, "confidence")), 2) + (Py.Truthy(Py.Get(f, "kbId")) ? "（知识库）" : "");
            var risk = Py.Get(f, "risk");
            var riskName = risk is null || Py.S(risk) is "green" or "yellow" or "red" ? Oplib.RiskName(Py.S(risk)) : "—";
            o.Add($"| {Cell(Py.Get(f, "object"))} | {VerdictName(Py.Get(f, "verdict"))} | {conf} "
                  + $"| {Cell(Py.Get(f, "actionName"))} | {riskName} | {Cell(Py.Get(f, "userExplanation"))} |");
        }

        o.AddRange(["", "## 健康分扣分明细", ""]);
        foreach (var (name, c) in (JsonObject)score["categories"]!)
        {
            o.Add($"- **{name}**（权重 {Py.ToStr(Py.Get(c, "weight"))}%）：{Py.ToStr(Py.Get(c, "score"))} 分");
            foreach (var d in Py.Iter(Py.Get(c, "deductions")))
            {
                var note = Py.Truthy(Py.Get(d, "note")) ? $"，{Py.ToStr(Py.Get(d, "note"))}" : "";
                o.Add($"  - −{Py.ToStr(Py.Get(d, "points"))}　{Py.ToStr(Py.Get(d, "rule"))}：{Join(Py.Get(d, "objects"))}（{Join(Py.Get(d, "flagIds"))}{note}）");
            }
        }

        o.AddRange(["", "## 证据（专业模式）", ""]);
        foreach (var f in findings)
            o.Add($"- **{Cell(Py.Get(f, "object"))}**（{Join(Py.Get(f, "flagIds"))}，建议 `{Py.ToStr(Py.Get(f, "proposedAction"))}`）：{Py.ToStr(Py.Get(f, "evidence"))}");

        var notCollected = Py.Iter(j["notCollected"]).ToList();
        if (notCollected.Count > 0)
        {
            o.AddRange(["", "## 没能检查的方面", ""]);
            o.AddRange(notCollected.Select(x => $"- {Py.ToStr(x)}"));
        }

        var issues = Py.Iter(result["issues"]).ToList();
        if (issues.Count > 0)
        {
            o.AddRange(["", "## 校验记录", "", "模型输出中被代码修正的地方（用于复核误判率）：", ""]);
            o.AddRange(issues.Select(x => $"- {Py.ToStr(x)}"));
        }
        return string.Join("\n", o) + "\n";
    }
}
