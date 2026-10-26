using System.Text.Json.Nodes;
using Pcdoctor.Core;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>桌面程序的完整链路（除了界面和真实 Windows）：原始报告 → 打码 → 纯规则研判 → 勾选 → 处理方案 → 执行 → 撤销。</summary>
public class EndToEndTests : IDisposable
{
    readonly string work = Path.Combine(Path.GetTempPath(), "pcd-e2e-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(work)) Directory.Delete(work, true); }

    static JsonNode Flag(string id, string section, string rule, string sev, string obj, string ev) =>
        new JsonObject { ["id"] = id, ["section"] = section, ["rule"] = rule, ["severity"] = sev, ["object"] = obj, ["evidence"] = ev };

    // 本机原始报告（未打码，用户名 Alice）
    static JsonNode RawReport() => new JsonObject
    {
        ["schema"] = "pcdoctor.collect/0.1", ["generatedAt"] = "2026-10-25 09:00", ["tool"] = new JsonObject { ["version"] = "0.2.7" },
        ["machine"] = new JsonObject { ["manufacturer"] = "HUAWEI", ["model"] = "TEST" },
        ["flags"] = new JsonArray(
            Flag("F001", "scheduledTasks", "target-missing", "medium", "ramboplay-uac", "计划任务指向的程序已不存在（卸载残留）：D:/Rambo/client/ramboplay.ra2.exe"),
            Flag("F002", "startup", "unsigned-user-folder", "medium", "Helper", "从用户目录自启动且无签名：C:\\Users\\Alice\\AppData\\Roaming\\x\\helper.exe"),
            Flag("F003", "drivers", "duplicate-versions", "low", "cui_dch.inf", "Intel 的驱动仓库保留了 2 个版本，其中 1 个没有设备在用：oem57.inf 30.0"),
            Flag("F004", "power", "fast-startup-on", "low", "快速启动", "快速启动已开启")),
        ["sections"] = new JsonObject
        {
            ["scheduledTasks"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["path"] = "\\", ["name"] = "ramboplay-uac",
                ["actions"] = new JsonArray(new JsonObject { ["execute"] = "D:/Rambo/client/ramboplay.ra2.exe" }), ["exists"] = false }) },
            ["startup"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["source"] = "registry",
                ["location"] = "HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", ["name"] = "Helper",
                ["command"] = "C:\\Users\\Alice\\AppData\\Roaming\\x\\helper.exe", ["exe"] = "C:\\Users\\Alice\\AppData\\Roaming\\x\\helper.exe" }) },
            ["drivers"] = new JsonObject { ["packages"] = new JsonObject { ["items"] = new JsonArray(
                new JsonObject { ["published"] = "oem57.inf", ["inf"] = "cui_dch.inf", ["provider"] = "Intel", ["version"] = "30.0" },
                new JsonObject { ["published"] = "oem58.inf", ["inf"] = "cui_dch.inf", ["provider"] = "Intel", ["version"] = "31.0", ["usedBy"] = "Intel Graphics" }) } },
            ["power"] = new JsonObject { ["fastStartup"] = 1 },
            ["software"] = new JsonObject { ["items"] = new JsonArray() },
        },
    };

    [Fact]
    public void Collect_judge_plan_execute_undo()
    {
        var raw = RawReport();
        var redacted = Redactor.RedactReport(raw.DeepClone(), "Alice", "BOX");
        Assert.DoesNotContain("Alice", redacted.ToJsonString());

        var result = Judge.RunOffline(redacted, [], Kb.LoadEmbedded());
        var findings = result["judgement"]!["findings"]!.AsArray();
        string KeyOf(string flag) => string.Join("+", findings.First(f => f!["flagIds"]!.AsArray().Any(x => x!.ToString() == flag))!["flagIds"]!.AsArray().Select(x => x!.ToString()));

        // 研判结果按编号对回原始报告（未打码），路径是真实的
        var plan = PlanBuilder.Build(raw, result, [KeyOf("F001"), KeyOf("F003"), KeyOf("F004")], @"C:\Windows");
        Assert.Equal(["driver.delete_old_version", "power.disable_fast_startup", "task.delete_leftover"], plan.Steps.Select(s => s.Op).OrderBy(x => x));

        var sys = new FakeWinSystem();
        sys.Tasks[@"\ramboplay-uac"] = FakeWinSystem.TaskXmlOf(true);
        sys.AddFile(@"C:\Windows\INF\oem57.inf", 1, 100);
        sys.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", RegValue.DWord(1));
        var ctx = new OpContext(sys, work);
        var store = new JournalStore(Path.Combine(work, "journal"));
        foreach (var s in plan.Steps) store.Save(Executor.RunStep(s, ctx, "r1"));

        var entries = store.All();
        Assert.All(entries, e => Assert.Equal(Status.Done, e.Status));
        Assert.False(sys.Tasks.ContainsKey(@"\ramboplay-uac"));
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled")!.AsInt());

        foreach (var e in entries) { Assert.Equal(Status.Done, Executor.Undo(e, ctx).Status); store.Save(e); }
        Assert.True(sys.Tasks.ContainsKey(@"\ramboplay-uac"));
        Assert.Equal(1, sys.ReadValue(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled")!.AsInt());
        Assert.All(store.All(), e => Assert.Equal(Status.Undone, e.Status));
    }

    [Fact]
    public void Startup_item_from_offline_judgement_is_question_not_auto_selected()
    {
        // 无签名的用户目录自启动在纯规则模式下是"拿不准、先问用户"，不会出现可执行的操作
        var redacted = Redactor.RedactReport(RawReport(), "Alice", "BOX");
        var result = Judge.RunOffline(redacted, [], Kb.LoadEmbedded());
        var f = result["judgement"]!["findings"]!.AsArray().First(x => x!["flagIds"]!.AsArray().Any(i => i!.ToString() == "F002"))!;
        Assert.Equal("ask_user", f["proposedAction"]!.ToString());
        var plan = PlanBuilder.Build(RawReport(), result, ["F002"], @"C:\Windows");
        Assert.Empty(plan.Steps);
        Assert.Single(plan.Skipped);
    }
}
