using System.Text.Json.Nodes;
using Pcdoctor.Core;
using Xunit;

namespace Pcdoctor.Core.Tests;

/// <summary>界面用的派生信息：发现流摘要、历史变化、交叉校验、反馈包去隐私、版本比较。</summary>
public class InsightsTests
{
    static JsonNode Report() => T.J("""

    {
      "tool": {"sectionSeconds": {"resources": 3.1, "processes": 11.9, "storage": 12.4}},
      "flags": [
        {"id": "F001", "section": "processes", "rule": "unsigned", "severity": "medium", "object": "a.exe"},
        {"id": "F002", "section": "processes", "rule": "unsigned", "severity": "medium", "object": "b.exe"},
        {"id": "F003", "section": "storage", "rule": "system-drive-low", "severity": "high", "object": "C:"}
      ],
      "sections": {
        "resources": {"uptimeDays": 0.1, "memory": {"usedPct": 64}},
        "processes": {"total": 96, "items": []},
        "drivers": {"kernelDrivers": {"thirdPartyCount": 12, "items": [{}, {}]}},
        "storage": {"volumes": [{"drive": "C:", "freeGB": 27.2, "freePct": 14}, {"drive": "D:", "freeGB": 300, "freePct": 60}]},
        "power": {"fastStartup": 1, "battery": {"healthPct": 88.9}},
        "security": {"defender": {"realTimeProtection": true}, "allowedThreats": [{}, {}]},
        "stability": {"bugChecks30d": []},
        "browserNetwork": {"proxy": {"enabled": 1, "server": "10.0.0.8:3128", "pac": "http://corp/pac"}, "dns": [{"interface": "WLAN", "servers": ["192.168.1.1"]}],
                           "hostsEntries": ["1.2.3.4 intranet.corp"], "extensions": [{}, {}, {}]}
      }
    }
    
""");

    [Fact]
    public void SectionSummariesDescribeEachSectionInCollectorOrder()
    {
        var rows = Insights.SectionSummaries(Report());
        var titles = rows.Select(r => r!["title"]!.ToString()).ToList();
        Assert.Equal(["资源占用", "进程", "驱动", "存储", "电源与电池", "安全设置", "浏览器与网络", "稳定性"], titles);
        string D(string t) => rows.First(r => r!["title"]!.ToString() == t)!["detail"]!.ToString();
        Assert.Equal("开机 2.4 小时，内存占用 64%", D("资源占用"));
        Assert.Equal("96 个进程，2 处值得看", D("进程"));
        Assert.Equal("12 个第三方驱动", D("驱动"));
        Assert.Equal("C 盘剩余 27 GB（14%），1 处值得看", D("存储"));
        Assert.Equal("快速启动开着，电池健康度 89%", D("电源与电池"));
        Assert.Equal("Defender 实时保护开着，2 条被放行的威胁", D("安全设置"));
        Assert.Equal("30 天内没有蓝屏", D("稳定性"));
        Assert.Equal(11.9, rows[1]!["sec"]!.GetValue<double>());
        Assert.Null(rows.First(r => r!["title"]!.ToString() == "驱动")!["sec"]);
    }

    static JsonNode Judge(params (string obj, string verdict)[] fs) => new JsonObject
    {
        ["judgement"] = new JsonObject { ["findings"] = new JsonArray(fs.Select(f => (JsonNode?)new JsonObject
            { ["object"] = f.obj, ["verdict"] = f.verdict, ["flagIds"] = new JsonArray("F001"), ["userExplanation"] = "说明" }).ToArray()) },
    };

    [Fact]
    public void ChangesListNewAndGoneIssuesByObjectName()
    {
        var before = Judge(("Thunder", "bloat"), ("ramboplay-uac", "leftover"), ("Defender", "normal"));
        var now = Judge(("Thunder", "bloat"), ("TeamViewer", "uncertain"), ("Defender", "normal"), ("Edge", "normal"));
        var c = Insights.Changes(now, before);
        Assert.Equal(2, c.Count);
        Assert.Equal("add", c[0]!["kind"]!.ToString());
        Assert.Equal("新出现：TeamViewer（拿不准）", c[0]!["text"]!.ToString());
        Assert.Equal("已不再出现：ramboplay-uac", c[1]!["text"]!.ToString());
    }

    [Fact]
    public void ChangesReportSystemDriveSpace()
    {
        var a = T.J("""{"sections": {"storage": {"volumes": [{"drive": "C:", "freeGB": 8.2}]}}}""");
        var b = T.J("""{"sections": {"storage": {"volumes": [{"drive": "C:", "freeGB": 19.5}]}}}""");
        var c = Insights.Changes(Judge(), Judge(), a, b);
        Assert.Equal("C 盘可用空间减少 11 GB", c.Single()!["text"]!.ToString());
        Assert.Empty(Insights.Changes(Judge(), Judge(), b, b));  // 不到 1 GB 的变化不提
    }

    [Fact]
    public void CrossCheckComparesAiWithRulesPerFinding()
    {
        var ai = T.J("""
{"judgement": {"findings": [
            {"flagIds": ["F001"], "verdict": "uncertain"},
            {"flagIds": ["F002", "F003"], "verdict": "leftover"},
            {"flagIds": ["F004"], "verdict": "bloat", "kbId": "xunlei"}]}}
""");
        var rules = T.J("""
{"judgement": {"findings": [
            {"flagIds": ["F001"], "verdict": "problem"},
            {"flagIds": ["F002"], "verdict": "leftover"}, {"flagIds": ["F003"], "verdict": "leftover"},
            {"flagIds": ["F004"], "verdict": "bloat"}]}}
""");
        var c = Insights.CrossCheck(ai, rules);
        Assert.Equal("AI 判为「拿不准」，固定规则判为「需处理」，值得多想一下", c["F001"]!.ToString());
        Assert.Equal("规则和 AI 的结论一致", c["F002+F003"]!.ToString());
        Assert.False(c.ContainsKey("F004"));  // 知识库结论两边一样，不列
    }

    [Fact]
    public void UnknownObjectsCountsOnlyProgramSectionsWithoutKb()
    {
        var report = T.J("""
{"flags": [
            {"id": "F001", "section": "processes"}, {"id": "F002", "section": "drivers"}, {"id": "F003", "section": "storage"}, {"id": "F004", "section": "services"}]}
""");
        var judge = T.J("""
{"judgement": {"findings": [
            {"flagIds": ["F001"], "verdict": "uncertain", "object": "a.exe"},
            {"flagIds": ["F002"], "verdict": "leftover", "object": "x.sys"},
            {"flagIds": ["F003"], "verdict": "problem", "object": "C:"},
            {"flagIds": ["F004"], "verdict": "bloat", "object": "XL", "kbId": "xunlei"},
            {"flagIds": ["F001"], "verdict": "normal", "object": "b.exe"}]}}
""");
        Assert.Equal(2, Insights.UnknownObjects(report, judge));
    }

    [Fact]
    public void ScrubRemovesNetworkDetailsButKeepsCounts()
    {
        var r = Insights.ScrubForFeedback(Report());
        var bn = r["sections"]!["browserNetwork"]!;
        Assert.Equal("[已去掉]", bn["proxy"]!["server"]!.ToString());
        Assert.Equal("[已去掉]", bn["proxy"]!["pac"]!.ToString());
        Assert.Equal(1, bn["dns"]!["interfaces"]!.GetValue<int>());
        Assert.Equal(1, bn["hostsEntries"]!["count"]!.GetValue<int>());
        Assert.DoesNotContain("192.168.1.1", r.ToJsonString());
        Assert.DoesNotContain("intranet.corp", r.ToJsonString());
        Assert.Equal(3, (bn["extensions"] as JsonArray)!.Count);
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.3", 1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.1-beta.1", "1.0.0", 1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta.1", -1)]
    [InlineData("0.9.9", "1.0.0-alpha.1", -1)]
    public void CompareVersions(string a, string b, int expected)
    {
        Assert.Equal(expected, Insights.CompareVersions(a, b));
        Assert.Equal(-expected, Insights.CompareVersions(b, a));
    }

    // 两次体检：预筛编号变了（F001 → F007），规则和对象相同的应该对得上
    static JsonNode OldReport() => T.J("""
        {"flags": [
          {"id": "F001", "rule": "startup-unknown", "object": "Thunder"},
          {"id": "F002", "rule": "task-orphan", "object": "\\XLLiveUD"},
          {"id": "F003", "rule": "edge-background", "object": "Edge"}
        ]}
        """);
    static JsonNode OldJudge() => T.J("""
        {"judgement": {"findings": [
          {"flagIds": ["F001"], "object": "迅雷", "verdict": "bloat"},
          {"flagIds": ["F002"], "object": "XLLiveUD", "verdict": "leftover"},
          {"flagIds": ["F003"], "object": "Edge 后台", "verdict": "bloat"}
        ]}}
        """);
    static JsonNode NewReport() => T.J("""
        {"flags": [
          {"id": "F004", "rule": "edge-background", "object": "Edge"},
          {"id": "F007", "rule": "startup-unknown", "object": "Thunder"},
          {"id": "F008", "rule": "startup-unknown", "object": "Other"}
        ]}
        """);
    static JsonNode NewJudge(string edgeVerdict = "bloat") => T.J("""
        {"judgement": {"findings": [
          {"flagIds": ["F007"], "object": "迅雷（新版）", "verdict": "bloat"},
          {"flagIds": ["F004"], "object": "Edge 后台", "verdict": "EDGE"},
          {"flagIds": ["F008"], "object": "Other", "verdict": "bloat"}
        ]}}
        """.Replace("EDGE", edgeVerdict));

    [Fact]
    public void CarryKeysMatchesByRuleAndObjectNotFlagId()
    {
        var keys = Insights.CarryKeys(OldReport(), OldJudge(), ["F001", "F002"], NewReport(), NewJudge());
        Assert.Equal(["F007"], keys);  // F002 的计划任务这次没有了，不带过去；F008 没选过
    }

    [Fact]
    public void CarryKeysSkipsFindingsNowJudgedNormal()
    {
        Assert.Empty(Insights.CarryKeys(OldReport(), OldJudge(), ["F003"], NewReport(), NewJudge("normal")));
    }

    [Fact]
    public void StillPresentCountsHandledFindingsThatCameBack()
    {
        Assert.Equal(1, Insights.StillPresent(OldReport(), OldJudge(), ["F001", "F002"], NewReport(), NewJudge()));
        Assert.Equal(0, Insights.StillPresent(OldReport(), OldJudge(), ["F002"], NewReport(), NewJudge()));
        Assert.Equal(0, Insights.StillPresent(OldReport(), OldJudge(), [], NewReport(), NewJudge()));
    }

    [Fact]
    public void PrintsFallBackToObjectNameWithoutFlags()
    {
        var p = Insights.Prints(T.J("{}"), T.J("""{"flagIds": ["F009"], "object": "C 盘"}"""));
        Assert.Equal(["object|C 盘"], p);
    }

    [Theory]
    [InlineData("{\"Code\":\"AccessDenied\",\"Message\":\"Current user is in debt.\",\"RequestId\":\"x\"}", "：AccessDenied Current user is in debt.")]
    [InlineData("{\"error\": \"too many\"}", "：too many")]
    [InlineData("<html>\n  Bad   Gateway\n</html>", "：<html> Bad Gateway </html>")]
    [InlineData("{broken", "：{broken")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("{\"RequestId\":\"x\"}", "")]
    public void ServerReasonIsOneShortLine(string? body, string expected) => Assert.Equal(expected, Insights.ServerReason(body));

    [Fact]
    public void ServerReasonIsCut() => Assert.Equal(1 + 200 + 1, Insights.ServerReason(new string('x', 5000)).Length);
}
