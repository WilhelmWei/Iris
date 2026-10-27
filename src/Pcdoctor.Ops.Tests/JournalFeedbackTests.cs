using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>内测反馈里的「处理」记录：只有操作类型和结果，不带目标路径、备份和账户；文字经过打码。</summary>
public class JournalFeedbackTests
{
    [Fact]
    public void ViewLeavesOutTargetBackupAndOwner()
    {
        var e = new JournalEntry
        {
            Id = "20261025091000001-abc001", TimeUtc = new DateTime(2026, 10, 25, 9, 10, 0, DateTimeKind.Utc), Op = "task.delete", OpName = "删除计划任务",
            Title = @"删除 C:\Users\Alice\AppData\QuickVPN\update.exe 的计划任务", Risk = "green", Status = Status.Undone, Message = "已删除",
            Reversible = true, Target = new JsonObject { ["path"] = @"C:\Users\Alice\AppData\QuickVPN" }, Backup = new JsonObject { ["xml"] = "<Task/>" },
            Owner = "S-1-5-21-1", ReportId = "r1", FindingKey = "F002", BatchId = "b1",
            UndoneUtc = new DateTime(2026, 10, 25, 9, 20, 0, DateTimeKind.Utc), UndoMessage = "已恢复 Alice 的任务",
        };
        var v = JournalFeedback.View(e, s => s.Replace("Alice", "[用户]"));
        var text = v.ToJsonString();
        Assert.DoesNotContain("Alice", text);
        Assert.DoesNotContain("S-1-5-21", text);
        Assert.DoesNotContain("<Task/>", text);
        Assert.Null(v["target"]);
        Assert.Null(v["backup"]);
        Assert.Equal("2026-10-25T09:20:00Z", v["undoneTime"]!.ToString());
        Assert.Equal(("task.delete", "undone", "F002", "b1"), (v["op"]!.ToString(), v["status"]!.ToString(), v["findingKey"]!.ToString(), v["batchId"]!.ToString()));
        Assert.Contains("[用户]", v["title"]!.ToString());
    }

    [Fact]
    public void LongTextIsCutAndMissingUndoIsNull()
    {
        var v = JournalFeedback.View(new JournalEntry { Id = "x", Title = new string('a', 1000), Message = null! }, s => s);
        Assert.Equal(300, v["title"]!.ToString().Length);
        Assert.Equal("", v["message"]!.ToString());
        Assert.Null(v["undoMessage"]);
        Assert.Null(v["undoneTime"]);
    }
}
