using System.Text;
using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>隐私打码（Hide-Private 的移植）。</summary>
public class RedactorTests
{
    [Fact]
    public void HidesUserComputerEmail()
    {
        var s = Redactor.HidePrivate("C:\\Users\\Wilhelm\\AppData 主机 DESKTOP-7Q2X 邮件 a.b+c@example.com wilhelmx", "wilhelm", "desktop-7q2x");
        Assert.Equal("C:\\Users\\[用户]\\AppData 主机 [计算机名] 邮件 [邮箱] wilhelmx", s);
    }

    [Fact]
    public void UserInsideComputerNameIsAlsoHidden()
    {
        // 采集脚本先替换用户名：计算机名里含用户名（按"-"分隔）时会先被替换掉一部分，行为与 Hide-Private 相同
        Assert.Equal("[用户]-PC", Redactor.HidePrivate("bob-PC", "bob", "bob-PC"));
    }

    [Fact]
    public void EmptyNamesSkipped()
    {
        Assert.Equal("abc", Redactor.HidePrivate("abc", "", null));
        Assert.Equal("abc", Redactor.HidePrivate("abc", "  ", ""));
        Assert.Equal("", Redactor.HidePrivate("", "u", "c"));
    }

    [Fact]
    public void RegexCharactersEscaped() => Assert.Equal("x [用户] y", Redactor.HidePrivate("x a.b(c) y", "a.b(c)", null));

    [Fact]
    public void RedactReportRecursive()
    {
        var report = T.O("""
        {"tool": {"version": "1", "redacted": false},
         "flags": [{"evidence": "C:\\Users\\Alice\\x.exe", "n": 3, "b": true}],
         "sections": {"storage": {"folders": {"C:\\Users\\alice\\Docs": {"path": "C:\\Users\\alice\\Docs"}}},
                      "machine": ["DESKTOP-7Q2X", null, "a.li@corp.cn"]}}
        """);
        var same = Redactor.RedactReport(report, "alice", "DESKTOP-7Q2X");
        Assert.Same(report, same);
        Assert.True(report["tool"]!["redacted"]!.GetValue<bool>());
        Assert.Equal("C:\\Users\\[用户]\\x.exe", Py.S(report["flags"]![0]!["evidence"]));
        Assert.Equal(3, Py.Num(report["flags"]![0]!["n"]));
        var folders = (JsonObject)report["sections"]!["storage"]!["folders"]!;
        Assert.True(folders.ContainsKey("C:\\Users\\[用户]\\Docs")); // 键也要打码
        Assert.Equal("C:\\Users\\[用户]\\Docs", Py.S(folders["C:\\Users\\[用户]\\Docs"]!["path"]));
        T.SameJson(T.J("""["[计算机名]", null, "[邮箱]"]"""), report["sections"]!["machine"]);
        Assert.DoesNotContain("alice", report.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactReportAddsTool()
    {
        var r = T.O("""{"flags": []}""");
        Redactor.RedactReport(r, "u", null);
        Assert.True(r["tool"]!["redacted"]!.GetValue<bool>());
    }
}

/// <summary>误判复核表（review.py 的 rows_from_judge / write_review）。</summary>
public class ReviewTests
{
    [Fact]
    public void RowsAndCsv()
    {
        var result = Judge.Run(Fixtures.KbReport(), new JsonObject
        {
            ["findings"] = new JsonArray(T.Finding(["F003"], "cache.clean", "problem", obj: "C:, \"盘\""))
        }, "m", kb: T.KB);
        var rows = Review.RowsFromJudge(result);
        Assert.Equal(3, rows.Count);
        Assert.Equal("模型", rows[0]["来源"]);
        Assert.Equal("0.90", rows[0]["置信度"]);
        Assert.Equal("需处理", rows[0]["模型结论"]);
        Assert.StartsWith("知识库 ", rows[1]["来源"]);
        var csv = Review.ToCsv(rows);
        Assert.StartsWith("编号,对象,模型结论,来源,置信度,建议操作,给用户的说明,判定,应该是,备注\r\n", csv);
        Assert.Contains("\"C:, \"\"盘\"\"\"", csv); // 含逗号和引号的字段加引号、引号加倍

        var dir = Directory.CreateTempSubdirectory("pcdoctor-review-");
        try
        {
            var judgePath = Path.Combine(dir.FullName, "pcdoctor-20261024-000000-judge.json");
            var path = Review.ReviewPath(judgePath);
            Assert.Equal(Path.Combine(dir.FullName, "pcdoctor-20261024-000000-review.csv"), path);
            Review.WriteFromJudge(path, result);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // Excel 靠 BOM 识别 UTF-8
            Assert.Equal(csv, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>Python 行为的小工具。</summary>
public class PyTests
{
    [Theory]
    [InlineData(2.675, 1, 2.7)]
    [InlineData(0.25, 1, 0.2)]
    [InlineData(0.35, 1, 0.3)] // 0.35 实际是 0.34999…
    [InlineData(36.666666666666664, 1, 36.7)]
    [InlineData(-1.25, 1, -1.2)]
    public void RoundLikePython(double x, int n, double expected) => Assert.Equal(expected, Py.Round(x, n));

    [Theory]
    [InlineData(0.125, "0.12")]
    [InlineData(0.375, "0.38")]
    [InlineData(1.0, "1.00")]
    [InlineData(0.6, "0.60")]
    public void FixedLikePython(double x, string expected) => Assert.Equal(expected, Py.Fixed(x, 2));

    [Fact]
    public void ReprAndStr()
    {
        Assert.Equal("['F001', 7, None, True, 1.5, {'a': [1.0]}]", Py.Repr(T.J("""["F001", 7, null, true, 1.5, {"a": [1.0]}]""")));
        Assert.Equal("\"it's\"", Py.ReprStr("it's"));
        Assert.Equal("'a\\nb\\\\'", Py.ReprStr("a\nb\\"));
        Assert.Equal("100.0", Py.ToStr(Py.Float(100)));
        Assert.Equal("1e+16", Py.FloatRepr(1e16));
        Assert.Equal("1e-05", Py.FloatRepr(0.00001));
        Assert.Equal("None", Py.ToStr(null));
    }

    [Fact]
    public void TruthyAndEq()
    {
        Assert.False(Py.Truthy(T.J("0")));
        Assert.False(Py.Truthy(T.J("0.0")));
        Assert.False(Py.Truthy(T.J("\"\"")));
        Assert.False(Py.Truthy(T.J("[]")));
        Assert.False(Py.Truthy(T.J("{}")));
        Assert.True(Py.Truthy(T.J("\"0\"")));
        Assert.True(Py.Eq(T.J("1"), T.J("1.0")));
        Assert.True(Py.Eq(T.J("true"), T.J("1")));
        Assert.False(Py.Eq(T.J("\"1\""), T.J("1")));
        Assert.True(Py.Eq(T.J("""{"a": 1, "b": 2}"""), T.J("""{"b": 2, "a": 1}""")));
    }

    [Fact]
    public void CodePointHelpers()
    {
        Assert.Equal(3, Py.Len("a😀b"));
        Assert.Equal("a😀", Py.Head("a😀b", 2));
        Assert.True(Py.CompareCodePoints("\uFFFF", "😀") < 0); // 按码点 U+FFFF < U+1F600，UTF-16 序则相反
    }

    [Fact]
    public void PyJsonMatchesPythonDumps()
    {
        var n = T.J("""{"a": [1, 2.50, "x\n\"y\"\u0001"], "b": {}, "c": [], "d": null, "e": 1E2}""");
        Assert.Equal("{\"a\": [1, 2.5, \"x\\n\\\"y\\\"\\u0001\"], \"b\": {}, \"c\": [], \"d\": null, \"e\": 100.0}", PyJson.Dumps(n));
        Assert.Equal("{\n \"a\": [\n  1\n ]\n}", PyJson.Dumps(T.J("""{"a": [1]}"""), indent: 1));
    }
}
