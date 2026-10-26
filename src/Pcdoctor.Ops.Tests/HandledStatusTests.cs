using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;
using static Pcdoctor.Ops.Tests.FakeWinSystem;

namespace Pcdoctor.Ops.Tests;

/// <summary>报告里"已处理 / 没生效 / 已撤销"的来源：操作记录里的结论编号和报告编号。</summary>
public class HandledStatusTests
{
    static JournalEntry E(string key, string status, string batch, int minute, string report = "r1") => new()
    {
        Id = Guid.NewGuid().ToString("N"), FindingKey = key, Status = status, BatchId = batch, ReportId = report,
        TimeUtc = new DateTime(2026, 10, 25, 12, minute, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void RunStepRecordsFindingKeyAndReport()
    {
        var sys = new FakeWinSystem();
        sys.Set(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "KWpsBox", Str("x"));
        var step = new PlanStep
        {
            Key = "F003+F004#0", FindingKey = "F003+F004", Op = "startup.disable", OpName = "关闭开机自启动", Title = "WPS",
            Target = new JsonObject { ["approvedHive"] = "HKCU", ["approvedKey"] = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                                      ["valueName"] = "KWpsBox", ["runKey"] = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" },
        };
        var e = Executor.RunStep(step, new OpContext(sys, Path.GetTempPath()), "r1", "b1");
        Assert.Equal(("F003+F004", "r1", "b1"), (e.FindingKey, e.ReportId, e.BatchId));
        // 旧方案里没有 FindingKey 时，从步骤编号里取
        step.FindingKey = "";
        Assert.Equal("F003+F004", Executor.RunStep(step, new OpContext(sys, Path.GetTempPath()), "r1", "b2").FindingKey);
    }

    [Fact]
    public void StatusFollowsTheLatestBatch()
    {
        var h = HandledStatus.Of([
            E("A", Status.RolledBack, "b1", 1), E("A", Status.Done, "b2", 5),          // 第一次没生效，第二次生效
            E("B", Status.Done, "b1", 1), E("B", Status.Undone, "b2", 6),              // 第二次撤销了
            E("C", Status.Failed, "b1", 2),
            E("D", Status.Done, "b1", 1, report: "r0"),                                 // 别的报告的记录不算
            new JournalEntry { Id = "x", Status = Status.Done, ReportId = "r1" },       // 旧版本没有结论编号的记录不算
        ], "r1");
        Assert.Equal("done", h["A"].Status);
        Assert.Equal("undone", h["B"].Status);
        Assert.Equal("failed", h["C"].Status);
        Assert.False(h.ContainsKey("D"));
        Assert.Equal(3, h.Count);
    }

    [Fact]
    public void AStepUndoneLaterKeepsTheSameBatchButCountsAsUndone()
    {
        // 撤销不会新建记录，而是把原记录改成 undone：同一批里只要还有一步生效，就仍算已处理
        var a1 = E("A", Status.Done, "b1", 1); var a2 = E("A", Status.Done, "b1", 1);
        Assert.Equal("done", HandledStatus.Of([a1, a2], "r1")["A"].Status);
        a1.Status = Status.Undone;
        Assert.Equal("done", HandledStatus.Of([a1, a2], "r1")["A"].Status);
        a2.Status = Status.Undone;
        Assert.Equal("undone", HandledStatus.Of([a1, a2], "r1")["A"].Status);
    }

    [Fact]
    public void DefenderScanCountsAsHandled()
    {
        Assert.Equal("done", HandledStatus.Of([E("S", Status.Info, "b1", 1)], "r1")["S"].Status);
    }
}
