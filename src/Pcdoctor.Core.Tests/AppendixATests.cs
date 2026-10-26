using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>
/// 移植自 tests/test_appendix_a.py：附录 A（2026-10-23 实战体检记录）在研判层的回归测试。
/// 测不了的行写在 NotCovered 里，测试会检查它和附录 A 的行数对得上。
/// </summary>
public class AppendixATests
{
    const int Covered = 12;
    static readonly string[] NotCovered =
    [
        "24 个服务批量改手动，第一版命令把失败也打印成成功",
        "设置窗口被管理员窗口遮挡，点击失败",
        "华为电脑管家累计 1400 多秒 CPU",
    ];

    static JsonObject F(int i, string section, string rule, string severity, string obj) => T.Flag(i, section, rule, severity, obj);

    static (JsonObject result, Dictionary<string, JsonObject> byFlag) RunOffline(JsonObject report, bool kb = true)
    {
        var result = Judge.RunOffline(report, kb: kb ? T.KB : []);
        var by = new Dictionary<string, JsonObject>();
        foreach (var f in Py.Iter(result["judgement"]!["findings"]).OfType<JsonObject>())
            foreach (var x in Py.Iter(f["flagIds"])) by[Py.S(x)!] = f;
        return (result, by);
    }

    static JsonObject ModelFinding(string[] ids, string op) => T.Finding(ids, op, obj: "x");

    [Fact]
    public void Bookkeeping()
    {
        var text = File.ReadAllText(T.RepoPath("docs", "core-design.md"));
        var table = text[text.IndexOf("## 附录 A", StringComparison.Ordinal)..text.IndexOf("## 待定问题", StringComparison.Ordinal)];
        var rows = table.Split('\n').Where(l => l.StartsWith("| ") && !l.StartsWith("| 发现") && !l.Contains("---")).ToList();
        Assert.Equal(Covered + NotCovered.Length, rows.Count);
        foreach (var k in NotCovered) Assert.Contains(rows, r => r.Contains(k));
    }

    // 1. 开机即发热、内存 80%、Memory Compression 累计 CPU 第一 → 先诊断内存，不是病毒
    [Fact]
    public void T01_MemoryBeforeMalware()
    {
        var (result, f) = RunOffline(Fixtures.MakeReport([F(1, "resources", "memory-high", "medium", "内存"),
                                                          F(2, "resources", "memory-compression-busy", "medium", "Memory Compression")]));
        Assert.False(Py.Truthy(result["judgement"]!["malwareSuspected"]));
        Assert.Contains("内存", Py.S(f["F002"]["userExplanation"]));
        Assert.Contains("不是中毒", Py.S(f["F002"]["userExplanation"]));
        Assert.Contains("内存诊断优先", Prompt.LoadSystemPrompt());
    }

    // 2. 怀疑挖矿：GPU 忙不等于中毒
    [Fact]
    public void T02_GpuBusyIsNotMalwareByItself()
    {
        var (result, f) = RunOffline(Fixtures.MakeReport([F(1, "resources", "gpu-busy-idle", "medium", "GPU")]));
        Assert.False(Py.Truthy(result["judgement"]!["malwareSuspected"]));
        Assert.Contains("没有发现中毒迹象", Py.S(result["judgement"]!["malwareReasoning"]));
        Assert.NotEqual("suspicious", Py.S(f["F001"]["verdict"]));
    }

    // 3 附带：华为电脑管家的 dokan 驱动是 OEM 组件，启动早期加载，不能直接删
    [Fact]
    public void T03b_OemManagerDriver()
    {
        var sections = T.O("""
        {"drivers": {"kernelDrivers": {"items": [
          {"name": "dokan2a", "state": "Running", "startMode": "System", "path": "C:\\Program Files\\Huawei\\PCManager\\dokan2a.sys",
           "company": "Dokan Project", "signer": "Huawei Device Co.", "signature": "Valid"}]}}}
        """);
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "drivers", "boot-start-third-party", "info", "dokan2a")], sections, "华为电脑管家"));
        Assert.Equal("huawei-dokan", Py.S(f["F001"]["kbId"]));
        Assert.Equal("none", Py.S(f["F001"]["proposedAction"]));
    }

    // 4. IIS、FTP 服务器在运行 → 改手动（黄色）
    [Fact]
    public void T04_ServerServices()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "services", "server-service-running", "medium", "W3SVC"),
                                                     F(2, "services", "server-service-running", "medium", "FTPSVC")]));
        foreach (var x in new[] { "F001", "F002" })
        {
            Assert.Equal("service.set_manual", Py.S(f[x]["proposedAction"]));
            Assert.Equal("yellow", Py.S(f[x]["risk"]));
        }
    }

    // 6. 360、火绒、爱奇艺的计划任务残留（程序已不存在）→ 删除残留任务（绿色）
    [Fact]
    public void T06_LeftoverTasks()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "scheduledTasks", "target-missing", "medium", "360安全卫士升级任务")]));
        Assert.Equal("leftover", Py.S(f["F001"]["verdict"]));
        Assert.Equal("task.delete_leftover", Py.S(f["F001"]["proposedAction"]));
        Assert.Equal("green", Py.S(f["F001"]["risk"]));
    }

    // 7. 内存完整性打不开：PassGuard、TesMon、华为旧 USB 驱动 → 识别出来但先问用户，不自动删
    [Fact]
    public void T07_MemoryIntegrityBlockers()
    {
        var (result, f) = RunOffline(Fixtures.MakeReport([F(1, "drivers", "memory-integrity-off", "medium", "内存完整性"),
                                                          F(2, "drivers", "binary-missing", "low", "PassGuard"),
                                                          F(3, "drivers", "binary-missing", "low", "TESMON"),
                                                          F(4, "drivers", "binary-missing", "low", "ew_usbccgpfilter")]));
        Assert.Equal("problem", Py.S(f["F001"]["verdict"]));
        foreach (var x in new[] { "F002", "F003", "F004" }) Assert.True(Py.Truthy(f[x]["needsUser"]), x);
        Assert.DoesNotContain("F001", Py.Iter(result["kbFlagIds"]).Select(Py.ToStr)); // 推测类知识不直接下结论
    }

    // 8. 内核堆栈保护打不开：游戏卸载后留下的反作弊 → 先问还玩不玩
    [Fact]
    public void T08_AnticheatLeftovers()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "drivers", "seed-anticheat-accel", "medium", "EasyAntiCheat"),
                                                     F(2, "services", "seed-anticheat-accel", "medium", "UniFairy")]));
        foreach (var x in new[] { "F001", "F002" })
        {
            Assert.Equal("ask_user", Py.S(f[x]["proposedAction"]));
            Assert.Contains("游戏", Py.S(f[x]["questionForUser"]));
        }
    }

    // 9. 停止网银键盘保护 PECKP 蓝屏 0xCE → 没有"停止驱动"操作；挂在过滤器上的只能摘过滤器 + 重启后删
    [Fact]
    public void T09_NeverStopFilterDriver()
    {
        Assert.DoesNotContain(Oplib.List, op => op.Id.Contains("stop"));
        var r = Fixtures.MakeReport([F(1, "drivers", "third-party-class-filter", "medium", "PECKP")], Fixtures.Drivers());
        var ev = Judge.Prepare(r).Evidence;
        foreach (var op in new[] { "service.delete_orphan", "driver.delete_old_version" })
        {
            var (c, _) = Validation.Validate(new JsonObject { ["findings"] = new JsonArray(ModelFinding(["F001"], op)) }, ev);
            Assert.Equal("ask_user", Py.S(c["findings"]![0]!["proposedAction"]));
        }
        var (clean, _) = Validation.Validate(new JsonObject { ["findings"] = new JsonArray(ModelFinding(["F001"], "driver.remove_after_reboot")) }, ev);
        Assert.Equal("driver.remove_after_reboot", Py.S(clean["findings"]![0]!["proposedAction"]));
        Assert.Equal("red", Py.S(clean["findings"]![0]!["risk"]));
        var (_, f) = RunOffline(r);
        Assert.Contains("网银", Py.S(f["F001"]["questionForUser"])); // 规则模式：先问还用不用
    }

    // 9 附带：红线 3、4（微软驱动、启动早期加载且正在运行的第三方驱动）
    [Fact]
    public void T09b_RedLines3And4()
    {
        var r = Fixtures.MakeReport([F(1, "drivers", "boot-start-third-party", "info", "storahci"),
                                     F(2, "drivers", "boot-start-third-party", "info", "vendorboot")], Fixtures.Drivers());
        var ev = Judge.Prepare(r).Evidence;
        var (clean, issues) = Validation.Validate(new JsonObject
        {
            ["findings"] = new JsonArray(ModelFinding(["F001"], "driver.remove_after_reboot"), ModelFinding(["F002"], "driver.remove_after_reboot")),
        }, ev);
        Assert.All(Py.Iter(clean["findings"]), f => Assert.Equal("ask_user", Py.S(f!["proposedAction"])));
        Assert.Contains(issues, x => x.Contains("红线 3"));
        Assert.Contains(issues, x => x.Contains("红线 4"));
    }

    // 10. 驱动仓库旧版重复驱动 → 删除旧版包，不加 /force
    [Fact]
    public void T10_OldDriverPackages()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "drivers", "duplicate-versions", "low", "bth.inf")]));
        Assert.Equal("driver.delete_old_version", Py.S(f["F001"]["proposedAction"]));
        Assert.Contains("不加 /force", Oplib.Operations["driver.delete_old_version"].Name);
    }

    // 11. 华为定制显卡驱动压住 Intel 新版 → 换驱动是红色操作，必须先问
    [Fact]
    public void T11_NewerDriverNotUsed()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "versions", "newer-driver-not-used", "medium", "Intel 显卡")]));
        Assert.True(Py.Truthy(f["F001"]["needsUser"]));
        Assert.Equal("red", Oplib.Operations["driver.switch_to_newer"].Risk);
        Assert.Equal(new HashSet<string> { "newer-driver-not-used" }, Oplib.Operations["driver.switch_to_newer"].Rules);
    }

    // 12. Steam 游戏库在 C 盘 → C 盘不足时把游戏库交给模型
    [Fact]
    public void T12_SteamLibraryOnC()
    {
        var sections = T.O("""{"storage": {"volumes": [{"drive": "C:", "freePct": 8}], "steamLibraries": [{"path": "C:\\SteamLibrary", "sizeGB": 22.4}]}}""");
        var r = Fixtures.MakeReport([F(1, "storage", "games-on-c", "medium", "Steam"), F(2, "storage", "system-drive-low", "medium", "C:")], sections);
        var ev = Judge.Prepare(r).Evidence;
        var c = Py.Iter(ev["items"]).First(it => Py.S(it!["flagId"]) == "F002")!;
        Assert.Contains("steamLibraries", (c["context"] ?? ev["sharedContext"])!.ToJsonString());
        var (_, f) = RunOffline(r);
        Assert.Equal("problem", Py.S(f["F001"]["verdict"]));
    }

    // 13. Edge 预加载 → 关闭浏览器后台（绿色）
    [Fact]
    public void T13_EdgePreload()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "browserNetwork", "edge-preload", "low", "Microsoft Edge")]));
        Assert.Equal("browser.disable_background", Py.S(f["F001"]["proposedAction"]));
        Assert.Equal("green", Py.S(f["F001"]["risk"]));
    }

    // 14. 用户拒绝关休眠 → 只关快速启动、保留休眠；操作库里没有关休眠
    [Fact]
    public void T14_KeepHibernation()
    {
        Assert.Contains("保留休眠", Oplib.Operations["power.disable_fast_startup"].Name);
        Assert.DoesNotContain(Oplib.List, op => op.Id.Contains("hibernat"));
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "power", "fast-startup-on", "low", "快速启动")]));
        Assert.Equal("power.disable_fast_startup", Py.S(f["F001"]["proposedAction"]));
    }

    // 14 附带：长期不重启也走"关快速启动"
    [Fact]
    public void T14b_LongUptime()
    {
        var (_, f) = RunOffline(Fixtures.MakeReport([F(1, "resources", "long-uptime", "low", "开机时长")]));
        Assert.Equal("power.disable_fast_startup", Py.S(f["F001"]["proposedAction"]));
    }
}
