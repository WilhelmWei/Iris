using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

public class WindowSourceTests
{
    const string Exe = @"C:\Program Files (x86)\QuickVPN\news\qnews.exe";
    static readonly JsonNode Report = JsonNode.Parse("""
    {"flags":[{"id":"F003","section":"scheduledTasks","rule":"seed-bundle","object":"QuickVPN News","evidence":"x"},
              {"id":"F009","section":"startup","rule":"seed-bundle","object":"Other","evidence":"x"}],
     "sections":{
      "startup":{"items":[{"name":"QNews","exe":"C:\\Program Files (x86)\\QuickVPN\\news\\qnews.exe","enabled":true}]},
      "scheduledTasks":{"items":[{"name":"QuickVPN News","state":"Ready","actions":[{"execute":"\"C:\\Program Files (x86)\\QuickVPN\\news\\qnews.exe\"","arguments":"/tip"}]}]},
      "services":{"items":[]},
      "software":{"items":[{"name":"QuickVPN","publisher":"QuickVPN Ltd","location":"C:\\Program Files (x86)\\QuickVPN\\"},
                           {"name":"Other","location":"C:\\Program Files"}]}}}
    """)!;

    [Fact]
    public void App_popup_is_traced_to_its_startup_item_task_and_software()
    {
        var r = WindowSource.Explain(new WindowFacts("qnews", Exe, "QuickVPN Ltd", "QuickVPN 资讯", null, "explorer.exe"), Report);
        Assert.Equal("app", r["kind"]!.ToString());
        Assert.Equal("QuickVPN", r["software"]!.ToString());   // 最长的安装目录
        var kinds = r["launchedBy"]!.AsArray().Select(x => x!["kind"]!.ToString()).ToList();
        Assert.Equal(["startup", "task"], kinds);              // explorer 不算启动者
        Assert.Equal(["F003"], r["flagIds"]!.AsArray().Select(x => x!.ToString()));
        Assert.Contains("计划任务「QuickVPN News」", r["advice"]!.ToString());

        var (q, a) = WindowSource.Memory(r);
        Assert.Contains("qnews.exe（QuickVPN）", a);
        Assert.DoesNotContain(@"C:\", a);                       // 不写路径
        Assert.Contains("瞄准器", q);
    }

    [Fact]
    public void System_and_browser_windows_get_their_own_advice()
    {
        Assert.Equal("system", WindowSource.Explain(new("ShellExperienceHost", @"C:\Windows\SystemApps\x\ShellExperienceHost.exe", null, null, null, null), Report)["kind"]!.ToString());
        var b = WindowSource.Explain(new("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", null, null, null), Report);
        Assert.Equal("browser", b["kind"]!.ToString());
        Assert.Empty(b["launchedBy"]!.AsArray());
    }

    [Fact]
    public void Unknown_app_with_a_launcher_parent()
    {
        var r = WindowSource.Explain(new("popup", @"C:\Users\Alice\AppData\Local\Temp\popup.exe", null, null, null, "updater.exe"), null);
        Assert.Equal("parent", r["launchedBy"]![0]!["kind"]!.ToString());
        Assert.Contains("「popup」", r["advice"]!.ToString());
    }
}
