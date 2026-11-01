using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>浏览器被改：快捷方式加了网址、策略锁定主页。</summary>
public class HijackTests
{
    const string Lnk = @"C:\Users\Alice\Desktop\Google Chrome.lnk";
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string Pol = @"SOFTWARE\Policies\Google\Chrome";
    readonly FakeWinSystem sys = new();
    OpContext Ctx => new(sys, @"C:\Program Files\pcdoctor-data\backups");

    static PlanStep Step(string op, JsonObject target) => new()
    {
        Key = "F001#0", Op = op, OpName = Catalog.Ops[op].Name, Title = op, NeedsAdmin = Catalog.Ops[op].NeedsAdmin,
        Reversible = Catalog.Ops[op].Reversible, Target = target,
    };

    [Theory]
    [InlineData("http://www.nav.example.com/?k=1", "")]
    [InlineData("--profile-directory=Default \"https://nav.example.com/\"", "--profile-directory=Default")]
    [InlineData("www.nav.example.com --profile-directory=\"Profile 1\"", "--profile-directory=\"Profile 1\"")]
    public void Strip_urls_keeps_other_arguments(string args, string expected) => Assert.Equal(expected, Hijack.StripUrls(args));

    [Fact]
    public void Url_detection_matches_the_collector()
    {
        Assert.True(Hijack.HasUrl(Chrome, "http://nav.example.com"));
        Assert.False(Hijack.HasUrl(Chrome, "--profile-directory=Default"));
        Assert.False(Hijack.HasUrl(@"C:\x\msedge_proxy.exe", "--app-id=abc --app-url=https://mail.example.com/"));
        Assert.False(Hijack.HasUrl(@"C:\Tools\notes.exe", "https://x.test"));
    }

    [Fact]
    public void Shortcut_fix_removes_the_url_and_undo_puts_it_back()
    {
        sys.Shortcuts[Lnk] = (Chrome, "--profile-directory=Default http://nav.example.com/?k=1");
        var e = Executor.RunStep(Step("shortcut.fix", new JsonObject { ["path"] = Lnk }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal((Chrome, "--profile-directory=Default"), sys.Shortcuts[Lnk]);

        var u = Executor.Undo(e, Ctx);
        Assert.Equal(Status.Done, u.Status);
        Assert.Equal("--profile-directory=Default http://nav.example.com/?k=1", sys.Shortcuts[Lnk].Arguments);
    }

    [Fact]
    public void Shortcut_fix_refuses_clean_or_foreign_shortcuts()
    {
        sys.Shortcuts[Lnk] = (Chrome, "");
        Assert.Equal(Status.Failed, Executor.RunStep(Step("shortcut.fix", new JsonObject { ["path"] = Lnk }), Ctx).Status);
        // 只处理桌面、开始菜单、任务栏里的 .lnk
        const string other = @"C:\Users\Alice\Documents\Google Chrome.lnk";
        sys.Shortcuts[other] = (Chrome, "http://nav.example.com");
        Assert.Equal(Status.Failed, Executor.RunStep(Step("shortcut.fix", new JsonObject { ["path"] = other }), Ctx).Status);
        Assert.Equal("http://nav.example.com", sys.Shortcuts[other].Arguments);
        Assert.NotNull(Hijack.PathProblem(@"C:\Users\Alice\Desktop\evil.exe"));
        Assert.NotNull(Hijack.PathProblem(@"C:\Users\Alice\Desktop\..\Documents\a.lnk"));
        Assert.True(Hijack.NeedsAdmin(@"C:\Users\Public\Desktop\Google Chrome.lnk"));
        Assert.False(Hijack.NeedsAdmin(Lnk));
    }

    [Fact]
    public void Shortcut_that_cannot_be_written_is_left_alone()
    {
        sys.Shortcuts[Lnk] = (Chrome, "http://nav.example.com/");
        sys.ReadOnlyFiles.Add(Lnk);
        var e = Executor.RunStep(Step("shortcut.fix", new JsonObject { ["path"] = Lnk }), Ctx);
        Assert.NotEqual(Status.Done, e.Status);
        Assert.Equal("http://nav.example.com/", sys.Shortcuts[Lnk].Arguments);
    }

    [Fact]
    public void Start_policy_removal_keeps_other_policies_and_undo_restores()
    {
        sys.Set(Hive.LocalMachine, Pol, "HomepageLocation", FakeWinSystem.Str("http://nav.example.com/"));
        sys.Set(Hive.LocalMachine, Pol, "RestoreOnStartup", RegValue.DWord(4));
        sys.Set(Hive.LocalMachine, Pol, "BackgroundModeEnabled", RegValue.DWord(0));   // Iris 自己写的，不动
        sys.Set(Hive.LocalMachine, Pol + @"\RestoreOnStartupURLs", "1", FakeWinSystem.Str("http://nav.example.com/"));
        var e = Executor.RunStep(Step("browser.remove_start_policy", new JsonObject { ["browser"] = "chrome", ["hive"] = "HKLM" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Null(sys.ReadValue(Hive.LocalMachine, Pol, "HomepageLocation"));
        Assert.Null(sys.ReadValue(Hive.LocalMachine, Pol, "RestoreOnStartup"));
        Assert.Null(sys.ReadValue(Hive.LocalMachine, Pol + @"\RestoreOnStartupURLs", "1"));
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, Pol, "BackgroundModeEnabled")!.AsInt());

        Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
        Assert.Equal("http://nav.example.com/", sys.ReadValue(Hive.LocalMachine, Pol, "HomepageLocation")!.AsStrings()[0]);
        Assert.Equal(4, sys.ReadValue(Hive.LocalMachine, Pol, "RestoreOnStartup")!.AsInt());
        Assert.Equal("http://nav.example.com/", sys.ReadValue(Hive.LocalMachine, Pol + @"\RestoreOnStartupURLs", "1")!.AsStrings()[0]);
    }

    [Fact]
    public void Start_policy_target_is_whitelisted()
    {
        Assert.NotNull(Operations.ValidateTarget("browser.remove_start_policy", new JsonObject { ["browser"] = "firefox", ["hive"] = "HKLM" }));
        Assert.NotNull(Operations.ValidateTarget("browser.remove_start_policy", new JsonObject { ["browser"] = "chrome", ["hive"] = "HKU" }));
        Assert.Equal(Status.Failed, Executor.RunStep(Step("browser.remove_start_policy", new JsonObject { ["browser"] = "edge", ["hive"] = "HKCU" }), Ctx).Status);  // 没有策略
    }

    [Fact]
    public void Plan_builder_finds_shortcuts_and_policies_in_the_report()
    {
        var report = JsonNode.Parse("""
        {"flags":[
          {"id":"F001","section":"browserNetwork","rule":"shortcut-url","severity":"medium","object":"Google Chrome（桌面）"},
          {"id":"F002","section":"browserNetwork","rule":"shortcut-url","severity":"medium","object":"Google Chrome（公用桌面）"},
          {"id":"F003","section":"browserNetwork","rule":"browser-policy-start","severity":"medium","object":"Google Chrome"},
          {"id":"F004","section":"browserNetwork","rule":"web-notify-allowed","severity":"low","object":"Microsoft Edge"}],
         "sections":{"browserNetwork":{
          "shortcuts":[{"label":"Google Chrome（桌面）","path":"C:\\Users\\Alice\\Desktop\\Google Chrome.lnk"},
                       {"label":"Google Chrome（公用桌面）","path":"C:\\Users\\Public\\Desktop\\Google Chrome.lnk"}],
          "policyStart":[{"browser":"chrome","browserName":"Google Chrome","hive":"HKLM"}]}}}
        """)!;
        JsonNode Fi(string op, params string[] ids) => new JsonObject
        {
            ["flagIds"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()), ["object"] = "x", ["verdict"] = "problem", ["confidence"] = 0.9, ["proposedAction"] = op,
        };
        var judge = new JsonObject { ["judgement"] = new JsonObject { ["findings"] = new JsonArray(Fi("shortcut.fix", "F001", "F002"), Fi("browser.remove_start_policy", "F003"), Fi("browser.review", "F004")) } };
        var plan = PlanBuilder.Build(report, judge, ["F001+F002", "F003", "F004"], @"C:\Windows");
        Assert.Equal(3, plan.Steps.Count);
        Assert.False(plan.Steps[0].NeedsAdmin);
        Assert.True(plan.Steps[1].NeedsAdmin);   // 公用桌面
        Assert.Equal("Google Chrome（所有用户）", plan.Steps[2].Title);
        Assert.Equal("chrome", plan.Steps[2].Target["browser"]!.ToString());
        Assert.Single(plan.Skipped);              // 网页通知：引导用户自己在浏览器里看
    }
}
