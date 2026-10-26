using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>Judge.Prepare 的结果：完整证据包、发给模型的证据包、知识库直接给出的结论。</summary>
public sealed record Prepared(JsonObject Evidence, JsonObject ModelEvidence, JsonArray KbFindings);

/// <summary>研判流水线（对应 judge/run_judge.py 里的 prepare / merge_kb / judge）。</summary>
public static class Judge
{
    public const string Schema = "pcdoctor.judge/0.1";
    public const string OfflineModelName = "纯规则模式";

    /// <summary>prepare()：返回 (完整证据包, 发给模型的证据包, 知识库直接给出的结论)。</summary>
    public static Prepared Prepare(JsonNode report, IReadOnlyList<JsonObject>? known = null, IReadOnlyList<JsonObject>? kb = null)
    {
        var evidence = Evidence.Build(report, known, kb);
        var (modelEv, kbFindings) = Kb.SplitDirect(evidence, kb ?? []);
        return new Prepared(evidence, modelEv, kbFindings);
    }

    /// <summary>
    /// merge_kb()：把知识库的结论并进模型输出；模型的结论里去掉知识库已经处理过的编号，也不许它自称来自知识库。
    /// 返回新的对象，不修改 rawOutput。
    /// </summary>
    public static (JsonNode? merged, List<string> issues) MergeKb(JsonNode? rawOutput, JsonArray kbFindings)
    {
        if (rawOutput is not JsonObject raw) return (Py.Clone(rawOutput), []);
        var handled = kbFindings.SelectMany(f => Py.Iter(Py.Get(f, "flagIds"))).Select(Py.Key).ToHashSet();
        var issues = new List<string>();
        var findings = new JsonArray();
        foreach (var fNode in Py.Iter(Py.Or(raw["findings"], new JsonArray())))
        {
            if (fNode is JsonObject src)
            {
                var f = new JsonObject();
                foreach (var (k, v) in src)
                    if (k != "kbId") f[k] = Py.Clone(v);
                var dup = Py.Iter(Py.Or(f["flagIds"], new JsonArray())).Where(x => handled.Contains(Py.Key(x))).ToList();
                if (dup.Count > 0)
                {
                    issues.Add($"{Py.ToStr(f["object"])}：{Py.ReprList(dup)} 已由知识库下结论，模型的这部分已忽略");
                    var rest = Py.Iter(f["flagIds"]).Where(x => !handled.Contains(Py.Key(x))).Select(Py.Clone).ToList();
                    f["flagIds"] = Py.List(rest);
                    if (rest.Count == 0) continue;
                }
                findings.Add(f);
            }
            else findings.Add(Py.Clone(fNode));
        }
        foreach (var f in kbFindings) findings.Add(Py.Clone(f));
        var merged = (JsonObject)raw.DeepClone();
        merged["findings"] = findings;
        return (merged, issues);
    }

    /// <summary>
    /// judge()：把模型原始输出变成最终结果（与调用模型分开，便于测试）。
    /// 输出与 Python 同结构：schema、generatedAt、model、collectorVersion、score、judgement、confirmedFlagIds、kbFlagIds、issues。
    /// </summary>
    public static JsonObject Run(JsonNode report, JsonNode? raw, string model, IReadOnlyList<JsonObject>? known = null,
                                 IReadOnlyList<JsonObject>? kb = null, DateTime? now = null)
    {
        known ??= [];
        var p = Prepare(report, known, kb);
        var (merged, kbIssues) = MergeKb(raw, p.KbFindings);
        var (clean, issues) = Validation.Validate(merged, p.Evidence);
        var allIssues = kbIssues.Concat(issues).ToList();
        var flags = Py.Get(report, "flags") as JsonArray ?? [];
        var confirmed = flags.Where(f => f is not null && KnownStore.Match(f, known) is not null).Select(f => Py.Get(f, "id")).ToList();
        return new JsonObject
        {
            ["schema"] = Schema,
            ["generatedAt"] = (now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm"),
            ["model"] = model,
            ["collectorVersion"] = Py.Clone(Py.Get(Py.Get(report, "tool"), "version")),
            ["score"] = Scoring.HealthScore(flags, Validation.ClearedFlagIds(clean), confirmed.Select(Py.ToStr)),
            ["judgement"] = clean,
            ["confirmedFlagIds"] = Py.List(confirmed.Select(Py.Clone)),
            ["kbFlagIds"] = Py.List(p.KbFindings.SelectMany(f => Py.Iter(Py.Get(f, "flagIds"))).Select(Py.Clone)),
            ["issues"] = Py.StrList(allIssues),
        };
    }

    /// <summary>纯规则模式的完整流程（run_judge.py --offline）：结果里带 offline=true 和 elapsedSec。</summary>
    public static JsonObject RunOffline(JsonNode report, IReadOnlyList<JsonObject>? known = null, IReadOnlyList<JsonObject>? kb = null,
                                        DateTime? now = null)
    {
        var sw = Stopwatch.StartNew();
        var p = Prepare(report, known, kb);
        var result = Run(report, Offline.Output(p.ModelEvidence), OfflineModelName, known, kb, now);
        result["offline"] = true;
        result["elapsedSec"] = Py.Round(sw.Elapsed.TotalSeconds, 1);
        return result;
    }
}
