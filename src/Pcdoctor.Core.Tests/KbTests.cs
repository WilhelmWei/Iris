using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>移植自 tests/test_kb.py：种子表格式、防冒名、按已装软件区分、直接结论不发给模型。</summary>
public class KbTests
{
    [Fact]
    public void EntriesWellFormed()
    {
        var ids = T.KB.Select(e => Py.S(e["id"])).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // 编号不重复
        foreach (var e in T.KB)
        {
            var id = Py.S(e["id"]);
            Assert.True(Validation.Verdicts.Contains(Py.S(e["verdict"])!), id);
            Assert.True(Oplib.Operations.ContainsKey(Py.S(e["proposedAction"])!), id);
            Assert.Contains(Py.S(e["certainty"]), new[] { "confirmed", "likely", "guess" });
            var c = Py.Num(e["confidence"]);
            Assert.True(c > 0 && c <= 1, id);
            Assert.True(Py.Truthy(e["explanation"]) && Py.Truthy(e["source"]) && Py.Truthy(e["product"]), id);
            var m = (JsonObject)e["match"]!;
            foreach (var k in new[] { "object", "path", "company", "evidence" })
                foreach (var p in m[k] is JsonArray a ? a : new JsonArray(Py.Clone(m[k])))
                    if (Py.Truthy(p)) _ = new Regex(Py.S(p)!); // 正则能编译（.NET 语法也要兼容）
            // 操作要适用于条目限定的报告节
            var allowed = Oplib.Operations[Py.S(e["proposedAction"])!].Sections;
            if (!allowed.Contains("*"))
            {
                Assert.True(Py.Truthy(m["section"]), $"{id}：有操作的条目必须限定报告节");
                foreach (var s in m["section"] is JsonArray sa ? sa.Select(Py.S) : [Py.S(m["section"])])
                    Assert.Contains(s!, allowed);
            }
        }
    }

    [Fact]
    public void DirectEntriesCannotBeImpersonated()
    {
        // 直接下结论的条目，不能只靠名字识别：必须同时要求签名有效，以及路径或厂商
        foreach (var e in T.KB.Where(Kb.IsDirect))
        {
            var m = e["match"]!;
            Assert.True(Py.Truthy(m["signed"]), Py.S(e["id"]));
            Assert.True(Py.Truthy(m["path"]) || Py.Truthy(m["company"]), Py.S(e["id"]));
            Assert.DoesNotContain(Py.S(e["verdict"]), new[] { "suspicious", "uncertain" });
        }
    }

    [Fact]
    public void WpsNeedsPathAndSignature()
    {
        var r = Fixtures.KbReport();
        var f = r["flags"]![0]!;
        var ctx = (JsonObject)r["sections"]!["processes"]!["items"]![0]!;
        var sw = (JsonArray)r["sections"]!["software"]!["items"]!;
        Assert.Equal("wps-user-folder", Py.S(Kb.Lookup(f, ctx, Fixtures.Wps, sw, T.KB)!["id"]));
        var fake = "C:\\Users\\[用户]\\AppData\\Local\\Temp\\wps.exe";
        var fakeCtx = (JsonObject)ctx.DeepClone();
        fakeCtx["path"] = fake;
        Assert.Null(Kb.Lookup(f, fakeCtx, fake, sw, T.KB)); // 同名但不在 WPS 目录
        var unsigned = (JsonObject)f.DeepClone();
        unsigned["evidence"] = "从用户可写目录运行（签名：NotSigned）";
        var unsignedCtx = (JsonObject)ctx.DeepClone();
        unsignedCtx["signature"] = "NotSigned";
        Assert.Null(Kb.Lookup(unsigned, unsignedCtx, Fixtures.Wps, sw, T.KB)); // 没有有效签名
    }

    [Fact]
    public void ThunderDependsOnInstalled()
    {
        var r = Fixtures.KbReport();
        var f = r["flags"]![1]!;
        var ctx = r["sections"]!["drivers"]!["kernelDrivers"]!["items"]![0]!;
        Assert.Equal("thunder-drivers", Py.S(Kb.Lookup(f, ctx, ctx["path"], (JsonArray)r["sections"]!["software"]!["items"]!, T.KB)!["id"]));
        var hit = Kb.Lookup(f, ctx, ctx["path"], new JsonArray(new JsonObject { ["name"] = "WPS Office" }), T.KB)!;
        Assert.Equal("thunder-drivers-leftover", Py.S(hit["id"])); // 迅雷已卸载：残留，交给模型并问用户
        Assert.False(Kb.IsDirect(hit));
    }

    [Fact]
    public void DirectFindingsSkipModel()
    {
        var r = Fixtures.KbReport();
        var p = Judge.Prepare(r, kb: T.KB);
        Assert.Equal(["F003"], Py.Iter(p.ModelEvidence["items"]).Select(it => Py.S(it!["flagId"]))); // 只有磁盘不足交给模型
        Assert.Equal(3, ((JsonArray)p.Evidence["items"]!).Count);
        Assert.Equal(new[] { "thunder-drivers", "wps-user-folder" }, p.KbFindings.Select(f => Py.S(f!["kbId"])).Order());
        Assert.Equal(2, ((JsonArray)p.ModelEvidence["handledByKnowledgeBase"]!).Count);

        var raw = T.O("""
        {"headline": "还行", "malwareSuspected": false, "malwareReasoning": "无", "top3": [],
         "findings": [
           {"flagIds": ["F003"], "object": "C:", "verdict": "problem", "confidence": 0.9, "evidence": "e",
            "proposedAction": "cache.clean", "userExplanation": "清理一下"},
           {"flagIds": ["F001"], "object": "wps.exe", "verdict": "suspicious", "confidence": 0.9, "evidence": "e",
            "proposedAction": "none", "userExplanation": "x", "kbId": "fake"}],
         "tips": [], "notCollected": []}
        """);
        var result = Judge.Run(r, raw, "test", kb: T.KB);
        var j = result["judgement"]!;
        Assert.Empty((JsonArray)j["unjudgedFlagIds"]!);
        Assert.DoesNotContain("suspicious", Py.Iter(j["findings"]).Select(f => Py.S(f!["verdict"]))); // 模型越权的那条被整条去掉
        Assert.Equal(3, ((JsonArray)j["findings"]!).Count);
        Assert.Equal(new[] { "thunder-drivers", "wps-user-folder" },
                     Py.Iter(j["findings"]).Where(f => Py.Truthy(f!["kbId"])).Select(f => Py.S(f!["kbId"])).Order());
        Assert.Contains(T.Issues(result), x => x.Contains("已由知识库下结论"));
        Assert.Equal(new[] { "F001", "F002" }, Py.Iter(result["kbFlagIds"]).Select(Py.ToStr).Order());
        // WPS 研判为正常 → 不扣分；磁盘不足照样扣
        Assert.Equal(100, Py.Num(result["score"]!["categories"]!["性能与后台"]!["score"]));
        Assert.Equal(90, Py.Num(result["score"]!["categories"]!["存储"]!["score"]));
        var md = MarkdownRender.Render(result);
        Assert.Contains("（知识库）", md);
        Assert.Contains("由知识库直接给出结论", md);
    }

    [Fact]
    public void ForgedKbIdRemoved()
    {
        // 模型给自己的结论加上 kbId，想冒充知识库结论：merge_kb 会去掉
        var r = Fixtures.KbReport("WPS Office");
        var raw = new JsonObject { ["findings"] = new JsonArray(T.Finding(["F003"], "cache.clean", "problem", kbId: "wps-user-folder")) };
        var (merged, _) = Judge.MergeKb(raw, Judge.Prepare(r, kb: T.KB).KbFindings);
        var mine = Py.Iter(merged!["findings"]).First(f => Py.Iter(f!["flagIds"]).Any(x => Py.S(x) == "F003"))!;
        Assert.False(((JsonObject)mine).ContainsKey("kbId"));
        Assert.True(raw["findings"]![0]!.AsObject().ContainsKey("kbId")); // 不修改调用方的对象
    }

    [Fact]
    public void UserConfirmationGoesToModel()
    {
        var r = Fixtures.KbReport();
        var known = new List<JsonObject>
        {
            new() { ["rule"] = "runs-from-user-folder", ["object"] = "wps.exe", ["evidence"] = Py.Clone(r["flags"]![0]!["evidence"]), ["note"] = "我自己装的" },
        };
        var p = Judge.Prepare(r, known, T.KB);
        Assert.Contains("F001", Py.Iter(p.ModelEvidence["items"]).Select(it => Py.S(it!["flagId"]))); // 用户回答过的，让模型结合用户的说明
        Assert.Equal(["thunder-drivers"], p.KbFindings.Select(f => Py.S(f!["kbId"])));
    }

    [Fact]
    public void NoKbUnchanged()
    {
        var p = Judge.Prepare(Fixtures.KbReport());
        Assert.Empty(p.KbFindings);
        Assert.Equal(3, ((JsonArray)p.ModelEvidence["items"]!).Count);
        Assert.DoesNotContain("knowledge", p.Evidence.ToJsonString());
    }
}
