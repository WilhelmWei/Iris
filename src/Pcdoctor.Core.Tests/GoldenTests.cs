using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>
/// 一致性测试：tests/golden/*.json 由 tools/make_golden.py 用 Python 版研判生成（只用合成数据）。
/// C# 读同一份输入，证据包、纯规则输出、研判结果、Markdown、复核表都要和 Python 一致。
/// </summary>
public class GoldenTests
{
    static readonly DateTime Fixed = new(2026, 1, 1, 0, 0, 0);

    public static IEnumerable<object[]> Cases() =>
        Directory.GetFiles(T.RepoPath("tests", "golden"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !n!.StartsWith('_'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n! });

    static (JsonObject input, JsonObject expected) Load(string name)
    {
        var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(T.RepoPath("tests", "golden", name + ".json")))!;
        return ((JsonObject)doc["input"]!, (JsonObject)doc["expected"]!);
    }

    static (JsonNode report, List<JsonObject> known, List<JsonObject> kb) Inputs(JsonObject input) =>
        (input["report"]!, Py.Iter(input["known"]).OfType<JsonObject>().ToList(), Py.Truthy(input["useKb"]) ? T.KB : []);

    [Fact]
    public void GoldenFilesExist() => Assert.True(Cases().Count() >= 30, "先运行 python tools/make_golden.py");

    [Theory]
    [MemberData(nameof(Cases))]
    public void EvidenceMatchesPython(string name)
    {
        var (input, exp) = Load(name);
        var (report, known, kb) = Inputs(input);
        var p = Judge.Prepare(report, known, kb);
        T.SameJson(exp["evidence"], p.Evidence);
        T.SameJson(exp["modelEvidence"], p.ModelEvidence);
        T.SameJson(exp["kbFindings"], p.KbFindings);
        // 发给模型的原文逐字相同（键的顺序、空格、转义都和 Python 的 json.dumps 一样）
        Assert.Equal(Py.S(exp["modelEvidenceText"]), PyJson.Dumps(p.ModelEvidence));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OfflineMatchesPython(string name)
    {
        var (input, exp) = Load(name);
        var (report, known, kb) = Inputs(input);
        var p = Judge.Prepare(report, known, kb);
        T.SameJson(exp["offlineRaw"], Offline.Output(p.ModelEvidence));
        var result = Judge.RunOffline(report, known, kb, Fixed);
        T.SameJson(exp["offlineResult"], result, "elapsedSec");
        T.SameText(Py.S(exp["offlineMarkdown"])!, MarkdownRender.Render(result));
        T.SameText(Py.S(exp["offlineReviewCsv"])!, Review.ToCsv(Review.RowsFromJudge(result)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModelOutputMatchesPython(string name)
    {
        var (input, exp) = Load(name);
        if (!exp.ContainsKey("modelResult")) return;
        var (report, known, kb) = Inputs(input);
        var result = Judge.Run(report, input["raw"], "test-model", known, kb, Fixed);
        T.SameJson(exp["modelResult"], result);
        if (exp.ContainsKey("modelMarkdown"))
        {
            T.SameText(Py.S(exp["modelMarkdown"])!, MarkdownRender.Render(result));
            T.SameText(Py.S(exp["modelReviewCsv"])!, Review.ToCsv(Review.RowsFromJudge(result)));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void KnownFileMatchesPython(string name)
    {
        var (input, exp) = Load(name);
        if (!exp.ContainsKey("knownFileText")) return;
        var dir = Directory.CreateTempSubdirectory("pcdoctor-known-");
        try
        {
            var path = Path.Combine(dir.FullName, KnownStore.FileName);
            KnownStore.Save(path, Py.Iter(input["known"]).OfType<JsonObject>());
            Assert.Equal(Py.S(exp["knownFileText"]), File.ReadAllText(path));
            T.SameJson(input["known"], Py.List(KnownStore.Load(path)));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>和报告无关的部分：提示词、操作表、注入检测、parse_json、Python 的数字格式。</summary>
public class MiscGoldenTests
{
    static readonly JsonObject Misc = (JsonObject)JsonNode.Parse(File.ReadAllText(T.RepoPath("tests", "golden", "_misc.json")))!;

    [Fact]
    public void SystemPromptMatchesPython()
    {
        Assert.Equal(Py.S(Misc["operationsTable"]), Oplib.OperationsTable());
        Assert.Equal(Py.S(Misc["systemPrompt"]), Prompt.LoadSystemPrompt());
    }

    [Fact]
    public void InjectionDetectorMatchesPython()
    {
        foreach (var (s, v) in (JsonObject)Misc["injection"]!)
            Assert.True(v!.GetValue<bool>() == Evidence.LooksLikeInjection(s), s);
    }

    [Fact]
    public void ParseJsonMatchesPython()
    {
        foreach (var (s, v) in (JsonObject)Misc["parseJson"]!)
            T.SameJson(v, LlmClient.ParseJson(s));
    }

    [Fact]
    public void FloatFormattingMatchesPython()
    {
        foreach (var c in (JsonArray)Misc["floats"]!)
        {
            var x = Py.Num(c!["x"]);
            Assert.Equal(Py.S(c["repr"]), Py.FloatRepr(x));
            Assert.Equal(Py.S(c["fixed2"]), Py.Fixed(x, 2));
            Assert.Equal(Py.Num(c["round1"]), Py.Round(x, 1));
            if (Math.Abs(x) < 9e15) Assert.Equal(Py.Num(c["round0"]), Py.RoundInt(x));
        }
    }

    [Fact]
    public void SumMatchesPython()
    {
        foreach (var c in (JsonArray)Misc["sums"]!)
        {
            var xs = Py.Iter(c!["xs"]).Select(Py.Num).ToList();
            Assert.Equal(Py.Num(c["sum"]), Scoring.PyFloatSum(xs));
        }
    }
}
