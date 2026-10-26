using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>移植自 tests/test_offline.py：纯规则模式。</summary>
public class OfflineTests
{
    /// <summary>采集脚本里所有的 (节, 规则)：读嵌入的 pcdoctor-collect.ps1，找 Add-Flag '节' '规则'。</summary>
    static HashSet<(string section, string rule)> CollectorRules() =>
        Regex.Matches(Resources.CollectorScriptText(), @"Add-Flag '(\w+)' '([\w-]+)'")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToHashSet();

    [Fact]
    public void EveryCollectorRuleHasDefault()
    {
        // 采集脚本新增规则时，纯规则模式也要补上默认结论
        var pairs = CollectorRules();
        Assert.True(pairs.Count > 50);
        var missing = pairs.Where(p => !Offline.SectionRules.ContainsKey($"{p.section}:{p.rule}") && !Offline.Rules.ContainsKey(p.rule))
            .Select(p => $"{p.section}:{p.rule}").Order().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EmbeddedCollectorKeepsBom()
    {
        var b = Resources.CollectorScriptBytes();
        Assert.True(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF); // 5.1 在中文系统上靠 BOM 识别 UTF-8
    }

    [Fact]
    public void DefaultsValidForTheirSections()
    {
        foreach (var (s, r) in CollectorRules())
        {
            var d = Offline.SectionRules.GetValueOrDefault($"{s}:{r}") ?? Offline.Rules[r];
            Assert.True(Validation.Verdicts.Contains(d.Verdict), $"{s}:{r}");
            Assert.False(string.IsNullOrEmpty(d.Text));
            if (d.Action is null) continue;
            Assert.True(Oplib.Operations.ContainsKey(d.Action), $"{s}:{r}");
            var op = Oplib.Operations[d.Action];
            Assert.True(op.Sections.Contains("*") || op.Sections.Contains(s), $"{d.Action} 不适用于 {s}");
            Assert.True(op.Rules is null || op.Rules.Contains(r), $"{d.Action} 只用于 {string.Join(",", op.Rules ?? new HashSet<string>())}");
        }
    }

    [Fact]
    public void JudgeOfflineReport()
    {
        var report = Fixtures.JudgeReport();
        var result = Judge.RunOffline(report, kb: T.KB);
        var j = result["judgement"]!;
        Assert.Empty((JsonArray)j["unjudgedFlagIds"]!);
        Assert.False(Py.Truthy(j["malwareSuspected"]));
        Assert.True(Py.Truthy(result["offline"]));
        Assert.Equal("纯规则模式", Py.S(result["model"]));
        // PECKP：知识库提示"先问还用不用这家网银"
        var peckp = T.ById(result, "F009");
        Assert.Equal("ask_user", Py.S(peckp["proposedAction"]));
        Assert.Contains("网银", Py.S(peckp["questionForUser"]));
        // 同一规则、同一对象的 3 条排除项合并成一条
        var excl = T.ById(result, "F001");
        Assert.Equal(new[] { "F001", "F002", "F003" }, Py.Iter(excl["flagIds"]).Select(Py.ToStr).Order());
        Assert.True(Py.Truthy(excl["needsUser"]));
        var md = MarkdownRender.Render(result);
        Assert.Contains("纯规则模式", md);
        Assert.Contains("卸载软件后，Defender 排除项不会自动删除", md); // 小知识和命中的规则相关
    }

    [Fact]
    public void RedLineWithoutKb()
    {
        // 没有知识库时，PECKP 的登记残留按规则默认建议 service.delete_orphan；它挂在键盘过滤器上，被红线 2 拦下
        var result = Judge.RunOffline(Fixtures.JudgeReport());
        Assert.Equal("ask_user", Py.S(T.ById(result, "F009")["proposedAction"]));
        Assert.Contains(T.Issues(result), x => x.Contains("红线 2"));
    }

    [Fact]
    public void MalwareRules()
    {
        var rep = Fixtures.JudgeReport();
        ((JsonArray)rep["flags"]!).Add(T.Flag(20, "security", "active-threat", "high", "Trojan:Win32/Test"));
        var raw = Offline.Output(Judge.Prepare(rep).ModelEvidence);
        Assert.True(Py.Truthy(raw["malwareSuspected"]));
        Assert.Contains("Trojan:Win32/Test", Py.S(raw["malwareReasoning"]));
        Assert.Contains("Trojan:Win32/Test", Py.S(raw["top3"]![0])); // 可疑项排在最前面
    }

    [Fact]
    public void UnknownRuleAsksUser()
    {
        var rep = Fixtures.MakeReport([T.Flag(1, "mystery", "brand-new-rule", "low", "新东西")]);
        var f = T.ById(Judge.RunOffline(rep), "F001");
        Assert.Equal("uncertain", Py.S(f["verdict"]));
        Assert.Equal(Offline.DefaultQuestion, Py.S(f["questionForUser"]));
    }
}
