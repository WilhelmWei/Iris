using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>移植自 tests/test_judge.py：证据包、算分、校验、端到端、用户确认。</summary>
public class EvidenceTests
{
    [Fact]
    public void OwnerByCompanyAndLocation()
    {
        var idx = new Evidence.SoftwareIndex((JsonArray)Fixtures.JudgeReport()["sections"]!["software"]!["items"]!);
        Assert.Equal("WPS Office", idx.Owner("C:\\x\\wps.exe", "Zhuhai Kingsoft Office Software Co.,Ltd"));
        Assert.Equal("迅雷", idx.Owner("D:\\迅雷\\Thunder\\Program\\Thunder.exe"));
        Assert.Equal("迅雷", idx.Owner(null, "深圳市迅雷网络技术有限公司"));
        Assert.Null(idx.Owner(null, "Microsoft Corporation")); // 微软太宽泛，不按厂商对
    }

    [Fact]
    public void SharedContextAndItems()
    {
        var report = Fixtures.JudgeReport();
        var ev = Evidence.Build(report);
        var items = Py.Iter(ev["items"]).OfType<JsonObject>().ToList();
        Assert.Equal(((JsonArray)report["flags"]!).Count, items.Count);
        var excl = items.Where(it => Py.S(it["rule"]) == "defender-exclusion").ToList();
        Assert.All(excl, it => Assert.Equal(Py.S(excl[0]["contextRef"]), Py.S(it["contextRef"])));
        // 空字段会被去掉以节省字符
        T.SameJson(T.J("""{"paths": ["D:\\quickvpn"], "processes": ["Update.exe", "uninst.exe"]}"""),
                   ev["sharedContext"]![Py.S(excl[0]["contextRef"])!]!["exclusions"]);
        var wps = items.First(it => Py.S(it["object"]) == "wps.exe");
        Assert.Equal("WPS Office", Py.S(wps["ownerSoftware"]));
        var peckp = items.Where(it => Py.S(it["object"]) == "PECKP").ToList();
        Assert.Single(peckp.Select(it => Py.S(it["contextRef"])).Distinct()); // 两条 PECKP 共用一份上下文
        var ctx = ev["sharedContext"]![Py.S(peckp[0]["contextRef"])!]!;
        T.SameJson(T.J("""[{"className": "Keyboard", "kind": "UpperFilters"}]"""), ctx["classFilters"]);
    }

    [Fact]
    public void LongStringsCutByCodePoint()
    {
        var s = new string('长', 299) + "😀" + "尾巴";
        var cut = Py.S(Evidence.Short(JsonValue.Create(s)))!;
        Assert.Equal(new string('长', 299) + "😀…", cut); // 表情符号算一个字符，不能被切成半个
    }
}

public class ScoringTests
{
    [Fact]
    public void SameRuleMergedAndCapped()
    {
        var s = Scoring.HealthScore((JsonArray)Fixtures.JudgeReport()["flags"]!);
        var sec = s["categories"]!["安全"]!;
        // 30 + 10/3 + 10/3 = 36.7，上限 45
        Assert.Equal(36.7, Py.Num(sec["deductions"]![0]!["points"]));
        Assert.Equal(63.3, Py.Num(sec["score"]));
        Assert.Single((JsonArray)sec["deductions"]!);
    }

    [Fact]
    public void ConditionRulesNotClearable()
    {
        var flags = new JsonArray(T.Flag(1, "storage", "system-drive-low", "medium", "C:"));
        var s = Scoring.HealthScore(flags, ["F001"]);
        Assert.Equal(90, Py.Num(s["categories"]!["存储"]!["score"])); // 磁盘不足是事实，判为正常也照样扣分
    }

    [Fact]
    public void ClearedFlagsNotDeducted()
    {
        var s = Scoring.HealthScore((JsonArray)Fixtures.JudgeReport()["flags"]!, ["F004"]);
        var perf = s["categories"]!["性能与后台"]!;
        Assert.Equal(100, Py.Num(perf["score"]));
        Assert.Equal("研判为正常，不扣分", Py.S(perf["deductions"]![0]!["note"]));
    }

    [Fact]
    public void IntAndFloatLikePython()
    {
        // 单条扣分是整数（70 分），多条合并是小数（63.3），封顶后是 45.0
        var one = Scoring.HealthScore(new JsonArray(T.Flag(1, "security", "uac-off", "high", "UAC")));
        Assert.Equal("70", Py.ToStr(one["categories"]!["安全"]!["score"]));
        Assert.Equal("30", Py.ToStr(one["categories"]!["安全"]!["deductions"]![0]!["points"]));
        var many = Scoring.HealthScore(Py.List(Enumerable.Range(1, 5).Select(i => (JsonNode?)T.Flag(i, "security", "uac-off", "high", "UAC"))));
        Assert.Equal("45.0", Py.ToStr(many["categories"]!["安全"]!["deductions"]![0]!["points"]));
        Assert.Equal("55.0", Py.ToStr(many["categories"]!["安全"]!["score"]));
    }
}

public class ValidateTests
{
    readonly JsonObject _ev = Evidence.Build(Fixtures.JudgeReport());

    (JsonObject? f, List<string> issues) RunOne(JsonObject finding)
    {
        var (clean, issues) = Validation.Validate(new JsonObject { ["findings"] = new JsonArray(finding) }, _ev);
        var fs = (JsonArray)clean["findings"]!;
        return (fs.Count > 0 ? (JsonObject)fs[0]! : null, issues);
    }

    [Fact]
    public void UnknownOperation()
    {
        var (f, _) = RunOne(T.Finding(["F001"], "registry.delete"));
        Assert.Equal("ask_user", Py.S(f!["proposedAction"]));
        Assert.True(Py.Truthy(f["needsUser"]) && Py.Truthy(f["questionForUser"]));
    }

    [Fact]
    public void OperationNotForSection()
    {
        var (f, _) = RunOne(T.Finding(["F004"], "driver.remove_after_reboot"));
        Assert.Equal("ask_user", Py.S(f!["proposedAction"]));
    }

    [Fact]
    public void OperationLimitedToRules()
    {
        // 驱动登记残留不能用"删除旧版驱动包"
        var (f, issues) = RunOne(T.Finding(["F005"], "driver.delete_old_version"));
        Assert.Equal("ask_user", Py.S(f!["proposedAction"]));
        Assert.Contains(issues, x => x.Contains("只能用于"));
    }

    [Fact]
    public void ProblemVerdict()
    {
        var (f, issues) = RunOne(T.Finding(["F004"], "none", verdict: "problem"));
        Assert.Equal("problem", Py.S(f!["verdict"]));
        Assert.DoesNotContain(issues, x => x.Contains("不合法"));
    }

    [Fact]
    public void RedLineMicrosoftDriver()
    {
        var (f, issues) = RunOne(T.Finding(["F007"], "driver.remove_after_reboot"));
        Assert.Equal("ask_user", Py.S(f!["proposedAction"]));
        Assert.Contains(issues, x => x.Contains("红线 3"));
    }

    [Fact]
    public void RedLineClassFilter()
    {
        // 挂在键盘过滤器上的驱动，文件丢了也不能直接删登记
        var (f, issues) = RunOne(T.Finding(["F006", "F009"], "service.delete_orphan"));
        Assert.Equal("ask_user", Py.S(f!["proposedAction"]));
        Assert.Contains(issues, x => x.Contains("红线 2"));
        (f, _) = RunOne(T.Finding(["F006"], "driver.remove_after_reboot"));
        Assert.Equal("driver.remove_after_reboot", Py.S(f!["proposedAction"]));
        Assert.Equal("red", Py.S(f["risk"])); // 风险由操作库决定
    }

    [Fact]
    public void UncertainGetsQuestion()
    {
        var (f, issues) = RunOne(T.Finding(["F005"], "none", verdict: "uncertain", confidence: 0.4, obj: "XLWFP"));
        Assert.True(Py.Truthy(f!["needsUser"]));
        Assert.Contains("XLWFP", Py.S(f["questionForUser"]));
        Assert.Contains(issues, x => x.Contains("补默认问题"));
    }

    [Fact]
    public void SuspiciousWithoutMalwareFlagged()
    {
        var (_, issues) = Validation.Validate(new JsonObject
        {
            ["malwareSuspected"] = false,
            ["findings"] = new JsonArray(T.Finding(["F001"], "defender.review", verdict: "suspicious")),
        }, _ev);
        Assert.Contains(issues, x => x.Contains("前后不一致"));
        (_, issues) = Validation.Validate(new JsonObject
        {
            ["malwareSuspected"] = true,
            ["findings"] = new JsonArray(T.Finding(["F001"], "defender.review", verdict: "suspicious")),
        }, _ev);
        Assert.DoesNotContain(issues, x => x.Contains("前后不一致"));
    }

    [Fact]
    public void InvalidIdsAndCoverage()
    {
        var (clean, _) = Validation.Validate(new JsonObject { ["findings"] = new JsonArray(T.Finding(["F999"], "none")) }, _ev);
        Assert.Empty((JsonArray)clean["findings"]!);
        Assert.Equal(9, ((JsonArray)clean["unjudgedFlagIds"]!).Count);
    }

    [Fact]
    public void NotAnObject()
    {
        var (clean, issues) = Validation.Validate(new JsonArray(), _ev);
        Assert.Empty((JsonArray)clean["findings"]!);
        Assert.Equal(["模型输出不是 JSON 对象"], issues);
        // Python 版渲染这种结果会 KeyError；C# 版照常出报告（结论全部列为"没有被研判"）
        var md = MarkdownRender.Render(Judge.Run(Fixtures.JudgeReport(), new JsonArray(), "m"));
        Assert.Contains("模型输出不是 JSON 对象", md);
    }
}

public class EndToEndTests
{
    [Fact]
    public void PromptHasOperations()
    {
        var p = Prompt.LoadSystemPrompt();
        Assert.Contains("driver.remove_after_reboot", p);
        Assert.DoesNotContain("{{OPERATIONS}}", p);
    }

    [Fact]
    public void ParseFencedJson() => T.SameJson(T.J("""{"a": 1}"""), LlmClient.ParseJson("好的\n```json\n{\"a\": 1}\n```"));

    [Fact]
    public void ParseJsonInvalidThrows() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => LlmClient.ParseJson("这不是 JSON"));

    public static JsonObject Raw() => new()
    {
        ["headline"] = "整体健康，有一个已卸载 VPN 的残留", ["malwareSuspected"] = false, ["malwareReasoning"] = "无可疑进程",
        ["top3"] = new JsonArray("删除 QuickVPN 留下的排除项"),
        ["findings"] = new JsonArray(
            T.Finding(["F001", "F002", "F003"], "defender.review", obj: "Defender 排除项"),
            T.Finding(["F004"], "none", verdict: "normal", obj: "wps.exe"),
            T.Finding(["F005"], "none", verdict: "normal", confidence: 0.8, obj: "XLWFP"),
            T.Finding(["F006", "F009"], "driver.remove_after_reboot", verdict: "uncertain", confidence: 0.5, obj: "PECKP",
                      question: "你还用这家银行的网银吗？"),
            T.Finding(["F007", "F008"], "none", verdict: "normal", obj: "其他")),
        ["tips"] = new JsonArray(new JsonObject { ["title"] = "卸载软件后排除项不会自动删除", ["body"] = "……", ["relatedFlagIds"] = new JsonArray("F001") }),
        ["notCollected"] = new JsonArray(),
    };

    [Fact]
    public void JudgeAndRender()
    {
        var result = Judge.Run(Fixtures.JudgeReport(), Raw(), "test-model");
        Assert.Empty(T.Issues(result));
        Assert.Empty((JsonArray)result["judgement"]!["unjudgedFlagIds"]!);
        Assert.Equal(100, Py.Num(result["score"]!["categories"]!["性能与后台"]!["score"])); // wps 研判为正常
        foreach (var k in new[] { "schema", "generatedAt", "model", "collectorVersion", "score", "judgement", "confirmedFlagIds", "kbFlagIds", "issues" })
            Assert.True(result.ContainsKey(k), k);
        var md = MarkdownRender.Render(result);
        foreach (var s in new[] { "健康分", "没有发现中毒迹象", "你还用这家银行的网银吗？", "卸载软件后排除项不会自动删除", "卸载残留",
                                  "Microsoft Defender", "不修改任何安全设置" })
            Assert.Contains(s, md);
    }

    [Fact]
    public void RunDoesNotModifyInputs()
    {
        var report = Fixtures.JudgeReport();
        var raw = Raw();
        var before = (report.ToJsonString(), raw.ToJsonString());
        Judge.Run(report, raw, "m", kb: T.KB);
        Judge.RunOffline(report, kb: T.KB);
        Assert.Equal(before, (report.ToJsonString(), raw.ToJsonString()));
    }
}

public class KnownTests
{
    static JsonArray Flags()
    {
        var flags = (JsonArray)Fixtures.JudgeReport()["flags"]!.DeepClone();
        flags.Add(T.Flag(10, "storage", "system-drive-low", "medium", "C:"));
        return flags;
    }

    [Fact]
    public void ConfirmAndMatch()
    {
        var flags = Flags();
        var entries = new List<JsonObject>();
        var added = KnownStore.Confirm(flags, ["F001", "F010"], "是我自己加的", entries);
        Assert.Equal(2, added.Count);
        Assert.Empty(KnownStore.Confirm(flags, ["F001"], "再确认一次", entries)); // 不重复记录
        Assert.NotNull(KnownStore.Match(flags[0], entries));
        var changed = (JsonObject)flags[0]!.DeepClone();
        changed["evidence"] = "证据变了";
        Assert.Null(KnownStore.Match(changed, entries)); // 证据变了就重新问
        var e = Assert.Throws<ArgumentException>(() => KnownStore.Confirm(flags, ["F999"], "x", entries));
        Assert.Equal("报告里没有这些编号：['F999']", e.Message);
    }

    [Fact]
    public void ConfirmedScoring()
    {
        var s = Scoring.HealthScore(Flags(), confirmedIds: ["F001", "F002", "F003", "F010"]);
        Assert.Equal(100, Py.Num(s["categories"]!["安全"]!["score"]));  // Defender 排除项：用户确认后不扣
        Assert.Equal(90, Py.Num(s["categories"]!["存储"]!["score"]));   // 磁盘不足：确认了也照样扣
        Assert.Contains("你已确认，不扣分", Py.Iter(s["categories"]!["安全"]!["deductions"]).Select(d => Py.S(d!["note"])));
    }

    [Fact]
    public void EvidenceCarriesConfirmation()
    {
        var report = Fixtures.JudgeReport();
        var entries = new List<JsonObject>();
        KnownStore.Confirm((JsonArray)report["flags"]!, ["F005"], "迅雷是我装的", entries);
        var ev = Evidence.Build(report, entries);
        var xl = Py.Iter(ev["items"]).First(it => Py.S(it!["flagId"]) == "F005")!;
        Assert.Equal("迅雷是我装的", Py.S(xl["userConfirmed"]!["note"]));
    }

    [Fact]
    public void SaveLoadRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("pcdoctor-known-");
        try
        {
            var reportPath = Path.Combine(dir.FullName, "pcdoctor-report-20261024-000000.json");
            var kpath = KnownStore.KnownPath(reportPath);
            Assert.Equal(Path.Combine(dir.FullName, "pcdoctor-known.json"), kpath);
            Assert.Empty(KnownStore.Load(kpath)); // 文件不存在
            var entries = new List<JsonObject>();
            KnownStore.Confirm(Flags(), ["F005"], "迅雷是我装的", entries, new DateTime(2026, 10, 24));
            KnownStore.Save(kpath, entries);
            var loaded = KnownStore.Load(kpath);
            Assert.Single(loaded);
            Assert.Equal("2026-10-24", Py.S(loaded[0]["confirmedAt"]));
            Assert.StartsWith("{\n \"schema\": \"pcdoctor.known/0.1\",\n \"entries\": [\n  {\n   \"rule\": ", File.ReadAllText(kpath));
            var result = Judge.Run(Fixtures.JudgeReport(), EndToEndTests.Raw(), "m", loaded);
            Assert.Equal(["F005"], Py.Iter(result["confirmedFlagIds"]).Select(Py.ToStr));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
