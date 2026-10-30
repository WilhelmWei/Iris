using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

public class PlanBuilderTests
{
    static JsonNode F(string id, string section, string rule, string obj) =>
        new JsonObject { ["id"] = id, ["section"] = section, ["rule"] = rule, ["severity"] = "low", ["object"] = obj, ["evidence"] = "e" };

    static JsonNode Finding(string op, string verdict, string obj, params string[] ids) => new JsonObject
    {
        ["flagIds"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()), ["object"] = obj, ["verdict"] = verdict,
        ["confidence"] = 0.9, ["proposedAction"] = op,
    };

    static readonly JsonNode Report = JsonNode.Parse("""
    {"flags":[], "sections":{
      "startup":{"items":[
        {"source":"registry","location":"HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run","name":"KWpsBox","command":"x"},
        {"source":"registry","location":"HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run","name":"Thunder","command":"x"},
        {"source":"startup-folder","location":"C:\\Users\\u\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup","name":"QQ.lnk","command":"x"}]},
      "scheduledTasks":{"items":[{"path":"\\","name":"ramboplay-uac","actions":[{"execute":"D:/Rambo/client/ramboplay.ra2.exe"}]},
                                 {"path":"\\Vendor\\","name":"Updater","actions":[{"execute":"C:\\v\\up.exe"}]}]},
      "services":{"items":[{"name":"XLServicePlatform","displayName":"迅雷基础服务","company":"迅雷","serviceDll":"C:\\Program Files (x86)\\Thunder\\XLSP.dll"},
                           {"name":"MsSvc","displayName":"微软服务","company":"Microsoft Corporation","signer":"Microsoft Windows"}]},
      "drivers":{"packages":{"items":[
        {"published":"oem57.inf","inf":"cui_dch.inf","provider":"Intel","version":"30.0","usedBy":null},
        {"published":"oem58.inf","inf":"cui_dch.inf","provider":"Intel","version":"31.0","usedBy":"Intel Graphics"},
        {"published":"oem3.inf","inf":"msdup.inf","provider":"Microsoft","version":"1.0","usedBy":null}]}},
      "software":{"items":[{"name":"迅雷"}]}
    }}
    """)!;

    static (JsonNode report, JsonNode judge) Make(params (JsonNode flag, JsonNode finding)[] items)
    {
        var r = Report.DeepClone();
        var flags = r["flags"]!.AsArray();
        var fs = new JsonArray();
        foreach (var (fl, fi) in items) { if (fl != null) flags.Add(fl.DeepClone()); fs.Add(fi.DeepClone()); }
        return (r, new JsonObject { ["judgement"] = new JsonObject { ["findings"] = fs } });
    }

    [Fact]
    public void Startup_targets_and_admin_needs()
    {
        var (r, j) = Make(
            (F("F001", "startup", "seed-bundle", "KWpsBox"), Finding("startup.disable", "bloat", "KWpsBox", "F001")),
            (F("F002", "startup", "seed-bundle", "Thunder"), Finding("startup.disable", "bloat", "Thunder", "F002")),
            (F("F003", "startup", "unsigned-user-folder", "QQ.lnk"), Finding("startup.disable", "bloat", "QQ", "F003")));
        var p = PlanBuilder.Build(r, j, ["F001", "F002", "F003"], @"C:\Windows");
        Assert.Equal(3, p.Steps.Count);
        var wps = p.Steps[0];
        Assert.False(wps.NeedsAdmin);  // 当前用户的启动项，在普通权限里改
        Assert.Equal("HKCU", wps.Target["approvedHive"]!.ToString());
        Assert.EndsWith(@"StartupApproved\Run", wps.Target["approvedKey"]!.ToString());
        var thunder = p.Steps[1];
        Assert.True(thunder.NeedsAdmin);
        Assert.EndsWith(@"StartupApproved\Run32", thunder.Target["approvedKey"]!.ToString());
        Assert.EndsWith(@"StartupApproved\StartupFolder", p.Steps[2].Target["approvedKey"]!.ToString());
        Assert.EndsWith(@"Startup\QQ.lnk", p.Steps[2].Target["path"]!.ToString());
    }

    [Fact]
    public void Tasks_services_drivers_resolved()
    {
        var (r, j) = Make(
            (F("F010", "scheduledTasks", "target-missing", "ramboplay-uac"), Finding("task.delete_leftover", "leftover", "ramboplay-uac", "F010")),
            (F("F011", "scheduledTasks", "frequent", "Updater"), Finding("task.disable", "bloat", "Updater", "F011")),
            (F("F012", "services", "svchost-third-party-dll", "XLServicePlatform"), Finding("service.set_manual", "bloat", "迅雷服务", "F012")),
            (F("F013", "drivers", "duplicate-versions", "cui_dch.inf"), Finding("driver.delete_old_version", "leftover", "cui_dch.inf", "F013")));
        var p = PlanBuilder.Build(r, j, ["F010", "F011", "F012", "F013"], @"C:\Windows");
        Assert.Equal(@"\ramboplay-uac", p.Steps.Single(s => s.Op == "task.delete_leftover").Target["fullName"]!.ToString());
        Assert.Equal(@"\Vendor\Updater", p.Steps.Single(s => s.Op == "task.disable").Target["fullName"]!.ToString());
        Assert.Equal("XLServicePlatform", p.Steps.Single(s => s.Op == "service.set_manual").Target["name"]!.ToString());
        var drv = p.Steps.Where(s => s.Op == "driver.delete_old_version").ToList();
        Assert.Single(drv);  // 只删没有设备在用的那个版本
        Assert.Equal("oem57.inf", drv[0].Target["publishedName"]!.ToString());
        Assert.Empty(p.Skipped);
    }

    [Fact]
    public void Red_lines_and_non_executable_are_skipped_with_reasons()
    {
        var (r, j) = Make(
            (F("F020", "services", "seed-bundle", "MsSvc"), Finding("service.set_manual", "bloat", "微软服务", "F020")),
            (F("F021", "drivers", "third-party-class-filter", "PECKP"), Finding("driver.remove_after_reboot", "leftover", "PECKP", "F021")),
            (F("F022", "processes", "runs-from-user-folder", "wps.exe"), Finding("none", "normal", "wps.exe", "F022")),
            (F("F023", "security", "defender-exclusion", "排除项"), Finding("defender.review", "problem", "排除项", "F023")),
            (F("F024", "processes", "heavy-memory", "x.exe"), Finding("startup.disable", "normal", "x", "F024")));
        var p = PlanBuilder.Build(r, j, ["F020", "F021", "F022", "F023", "F024", "F999"], @"C:\Windows");
        Assert.Empty(p.Steps);
        var why = p.Skipped.ToDictionary(s => s.Key, s => s.Reason);
        Assert.Contains("微软自带", why["F020"]);
        Assert.Contains("专业模式", why["F021"]);
        Assert.Contains("安全中心", why["F023"]);
        Assert.Contains("正常", why["F024"]);
        Assert.Contains("找不到", why["F999"]);
    }

    [Fact]
    public void Cache_clean_merged_split_by_admin_and_ordered_by_measured_size()
    {
        var (r, j) = Make(
            (F("F030", "storage", "junk-cleanable", "可清理的缓存"), Finding("cache.clean", "problem", "可清理的缓存", "F030")),
            (F("F031", "storage", "system-drive-low", "C:"), Finding("cache.clean", "problem", "C:", "F031")));
        r["sections"]!["storage"] = JsonNode.Parse("""
        {"junk":{"items":[
          {"id":"temp-user","tier":"A","cleanableMB":300},
          {"id":"browser-chrome","tier":"A","cleanableMB":900,"running":"chrome"},
          {"id":"dev-pip","tier":"A","cleanableMB":0.2},
          {"id":"temp-windows","tier":"A","cleanableMB":50},
          {"id":"windows-old","tier":"B","cleanableMB":20000}]}}
        """);
        var p = PlanBuilder.Build(r, j, ["F030", "F031"], @"C:\Windows");
        Assert.Equal(2, p.Steps.Count);
        var user = p.Steps.Single(s => !s.NeedsAdmin);
        Assert.Equal(["browser-chrome", "temp-user"], user.CacheOptions!.Items.Select(i => i.Id));  // 大的在前，不到 1 MB 的不列
        Assert.Equal(user.CacheOptions.Items.Select(i => i.Id), user.CacheOptions.Categories);    // 一键加入时默认全选
        Assert.Equal("chrome", user.CacheOptions.Items[0].Running);
        var admin = p.Steps.Single(s => s.NeedsAdmin);
        Assert.Equal(["temp-windows"], admin.CacheOptions!.Items.Select(i => i.Id));                // B 类（Windows.old）不列
        Assert.Null(user.CacheChoice);
        Assert.Contains("合并", p.Skipped.Single().Reason);
    }

    [Fact]
    public void Unused_browser_uninstall_and_chrome_background_targets()
    {
        var (r, j) = Make(
            (F("F050", "browserNetwork", "browser-unused", "360安全浏览器"), Finding("app.uninstall", "bloat", "360安全浏览器", "F050")),
            (F("F051", "browserNetwork", "chrome-idle-background", "Google Chrome"), Finding("browser.disable_chrome_background", "bloat", "Google Chrome", "F051")));
        r["sections"]!["software"] = JsonNode.Parse("""{"items":[{"name":"360安全浏览器"}]}""");
        var p = PlanBuilder.Build(r, j, ["F050", "F051"], @"C:\Windows");
        Assert.Equal("360安全浏览器", p.Steps.Single(s => s.Op == "app.uninstall").Target["displayName"]!.ToString());
        var chrome = p.Steps.Single(s => s.Op == "browser.disable_chrome_background");
        Assert.True(chrome.NeedsAdmin);
        Assert.Contains("由你的组织管理", chrome.Cost);  // 红线 9：执行前告知
    }

    [Fact]
    public void Cache_clean_on_old_report_lists_all_cleanable_categories()
    {
        var (r, j) = Make((F("F030", "storage", "temp-large", "userTemp"), Finding("cache.clean", "problem", "临时文件", "F030")));
        var p = PlanBuilder.Build(r, j, ["F030"], @"C:\Windows");
        Assert.Equal(CacheClean.Cleanable.Count(), p.Steps.Sum(s => s.CacheOptions!.Items.Count));
        Assert.All(p.Steps.SelectMany(s => s.CacheOptions!.Items), i => Assert.Null(i.SizeMB));
    }

    [Fact]
    public void Answer_not_mine_unlocks_reversible_alternative()
    {
        var (r, j) = Make(
            (F("F040", "startup", "unsigned-user-folder", "KWpsBox"), Finding("ask_user", "uncertain", "KWpsBox", "F040")),
            (F("F041", "security", "defender-allowed-threat", "PUA"), Finding("ask_user", "uncertain", "PUA", "F041")));
        var alt = PlanBuilder.AltOpsFor(r, j);
        Assert.Equal("startup.disable", alt["F040"]);
        Assert.False(alt.ContainsKey("F041"));  // 安全设置类没有替代操作（不修改安全设置）
        Assert.Empty(PlanBuilder.Build(r, j, ["F040"], @"C:\Windows").Steps);  // 没回答"不认识"时不处理
        var p = PlanBuilder.Build(r, j, ["F040"], @"C:\Windows", alt);
        Assert.Equal("startup.disable", Assert.Single(p.Steps).Op);
    }

    [Fact]
    public void Every_catalog_op_is_implemented_and_texts_complete()
    {
        foreach (var (id, info) in Catalog.Ops)
        {
            Assert.NotNull(Operations.Get(id));
            Assert.False(string.IsNullOrWhiteSpace(info.Change + info.Cost + info.Undo));
            Assert.Contains(info.Risk, new[] { "green", "yellow" });  // 1.0 不包含红色操作
        }
        Assert.Equal(Catalog.Ops.Keys.OrderBy(x => x), Operations.All.Keys.OrderBy(x => x));
    }
}
