using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Ops;

/// <summary>浏览器被改：快捷方式后面被加了网址、主页 / 启动页 / 搜索引擎被策略锁定。</summary>
public static class Hijack
{
    static readonly Regex BrowserExe = new(@"^(msedge|msedge_proxy|chrome|chrome_proxy|firefox|iexplore|brave|360se|360chrome|360chromex|qqbrowser|sogouexplorer|2345explorer|opera|vivaldi)\.exe$",
        RegexOptions.IgnoreCase);
    /// <summary>单独作为一个参数的网址（--proxy-server=http://… 这类参数里的网址不算）。</summary>
    static readonly Regex Url = new(@"(?<=^|\s)""?(https?://|www\.)[^\s""]+""?(?=\s|$)", RegexOptions.IgnoreCase);
    static readonly Regex App = new(@"--app(-id)?=", RegexOptions.IgnoreCase);

    public static bool IsBrowser(string? target) => !string.IsNullOrEmpty(target) && BrowserExe.IsMatch(target.Split('\\', '/')[^1]);  // 不用 Path.GetFileName：测试在 Linux 上跑

    /// <summary>和采集脚本的 Get-ShortcutIssue 一致：浏览器快捷方式带了网址（网页应用的快捷方式除外）。</summary>
    public static bool HasUrl(string? target, string? args) => IsBrowser(target) && !App.IsMatch(args ?? "") && Url.IsMatch(args ?? "");

    /// <summary>去掉参数里的网址，其他参数（例如 --profile-directory）原样保留。</summary>
    public static string StripUrls(string args) => Regex.Replace(Url.Replace(args, " "), @"\s{2,}", " ").Trim();

    static readonly string[] Places = [@"\Desktop\", @"\桌面\", @"\Start Menu\Programs\", @"\Quick Launch\User Pinned\TaskBar\"];

    /// <summary>快捷方式只能在桌面、开始菜单、任务栏里，而且是 .lnk 文件。</summary>
    public static string? PathProblem(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.Contains("..") || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:\\")) return "快捷方式路径不合法";
        return Places.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase)) ? null : "只处理桌面、开始菜单和任务栏上的快捷方式";
    }

    /// <summary>公用桌面、所有用户的开始菜单要管理员权限才能改。</summary>
    public static bool NeedsAdmin(string path) =>
        path.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\ProgramData\", StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, string> PolicyKeys = new Dictionary<string, string>
    {
        ["chrome"] = @"SOFTWARE\Policies\Google\Chrome", ["edge"] = @"SOFTWARE\Policies\Microsoft\Edge",
    };

    /// <summary>锁定主页、启动页、新标签页、搜索引擎的策略值（Iris 自己写的后台运行策略不在里面）。</summary>
    public static readonly string[] StartPolicyValues =
    [
        "HomepageLocation", "HomepageIsNewTabPage", "RestoreOnStartup", "NewTabPageLocation",
        "DefaultSearchProviderEnabled", "DefaultSearchProviderName", "DefaultSearchProviderKeyword", "DefaultSearchProviderSearchURL",
        "DefaultSearchProviderSuggestURL", "DefaultSearchProviderIconURL", "DefaultSearchProviderEncodings",
    ];
}

/// <summary>去掉浏览器快捷方式里被加上的网址。改之前把整个 .lnk 文件备份进操作记录，撤销时原样写回。</summary>
public sealed class ShortcutFixOp : Operation
{
    public override string Id => "shortcut.fix";

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var lnk = c.Sys.ReadShortcut(T(s, "path"));
        if (lnk == null) return "快捷方式已经不在了，什么也没做";
        if (!Hijack.HasUrl(lnk.Value.Target, lnk.Value.Arguments)) return "快捷方式里已经没有网址了，不需要处理";
        return null;
    }
    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var bytes = c.Sys.ReadAllBytes(T(s, "path")) ?? throw new IOException("读不到快捷方式文件");
        var lnk = c.Sys.ReadShortcut(T(s, "path"))!.Value;
        b["lnk"] = Convert.ToBase64String(bytes);
        b["target"] = lnk.Target;
        b["arguments"] = lnk.Arguments;
    }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        if (!c.Sys.SetShortcutArguments(T(s, "path"), Hijack.StripUrls(b["arguments"]!.ToString()))) throw new IOException("系统不允许修改这个快捷方式");
    }
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        var lnk = c.Sys.ReadShortcut(T(s, "path"));
        if (lnk == null) return "改完后读不到快捷方式";
        if (!string.Equals(lnk.Value.Target, b["target"]!.ToString(), StringComparison.OrdinalIgnoreCase)) return "快捷方式指向的程序变了";
        return Hijack.HasUrl(lnk.Value.Target, lnk.Value.Arguments) ? "网址还在" : null;
    }
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        if (b["lnk"] is JsonValue v) c.Sys.WriteAllBytes(T(s, "path"), Convert.FromBase64String(v.ToString()));
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b) =>
        c.Sys.ReadShortcut(T(s, "path"))?.Arguments == b["arguments"]?.ToString() ? null : "快捷方式和原来不一样";
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已去掉快捷方式里的网址，现在点它会直接打开浏览器";
}

/// <summary>删除锁定浏览器主页、启动页、新标签页、搜索引擎的策略（红线 9 的反向：删掉以后"由你的组织管理"会消失）。
/// 只删 Hijack.StartPolicyValues 里的值和 RestoreOnStartupURLs 里的网址；备份原值，撤销时写回。</summary>
public sealed class BrowserStartPolicyOp : Operation
{
    public override string Id => "browser.remove_start_policy";
    const string Urls = "RestoreOnStartupURLs";

    const string OtherAccountMessage = "以管理员身份运行时用的是另一个账户，改不到你这个账户的设置。请用这个账户的管理员身份运行 Iris，或在浏览器设置里手动改";

    /// <summary>HKCU 的策略：辅助进程的账户和建方案时的账户不同，就不碰（否则改的、恢复的都是别的账户）。</summary>
    static bool OtherAccount(PlanStep s, OpContext c) =>
        T(s, "hive") == "HKCU" && (string.IsNullOrEmpty(T(s, "sid")) || T(s, "sid") != c.Sys.UserSid());

    public override Outcome Undo(JournalEntry e, OpContext ctx) =>
        OtherAccount(new PlanStep { Op = e.Op, Target = e.Target }, ctx) ? Outcome.Fail(OtherAccountMessage) : base.Undo(e, ctx);

    static (Hive hive, string key) Where(PlanStep s) => (Operations.ParseHive(T(s, "hive")), Hijack.PolicyKeys[T(s, "browser")]);

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        if (OtherAccount(s, c)) return OtherAccountMessage;
        var (h, k) = Where(s);
        var any = Hijack.StartPolicyValues.Any(n => c.Sys.ReadValue(h, k, n) != null) || UrlNames(c, h, k).Count > 0;
        return any ? null : "这些策略已经不在了，不需要处理";
    }
    static IReadOnlyList<string> UrlNames(OpContext c, Hive h, string k) =>
        c.Sys.ExportTree(h, k + "\\" + Urls)?.Values.Keys.ToList() ?? (IReadOnlyList<string>)[];

    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k) = Where(s);
        var vals = new JsonObject();
        foreach (var n in Hijack.StartPolicyValues) vals[n] = Operations.ToJson(c.Sys.ReadValue(h, k, n));
        b["values"] = vals;
        var urls = new JsonObject();
        foreach (var n in UrlNames(c, h, k)) urls[n] = Operations.ToJson(c.Sys.ReadValue(h, k + "\\" + Urls, n));
        b["urls"] = urls;
    }
    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k) = Where(s);
        foreach (var n in Hijack.StartPolicyValues) if (c.Sys.ReadValue(h, k, n) != null) c.Sys.DeleteValue(h, k, n);
        foreach (var n in UrlNames(c, h, k)) c.Sys.DeleteValue(h, k + "\\" + Urls, n);
    }
    protected override string? Verify(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k) = Where(s);
        var left = Hijack.StartPolicyValues.FirstOrDefault(n => c.Sys.ReadValue(h, k, n) != null);
        if (left != null) return $"{left} 没有删掉";
        return UrlNames(c, h, k).Count > 0 ? "启动页网址没有删掉" : null;
    }
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k) = Where(s);
        foreach (var (n, v) in b["values"]?.AsObject() ?? []) Operations.Restore(c.Sys, h, k, n, v);
        foreach (var (n, v) in b["urls"]?.AsObject() ?? []) Operations.Restore(c.Sys, h, k + "\\" + Urls, n, v);
    }
    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b)
    {
        var (h, k) = Where(s);
        foreach (var (n, v) in b["values"]?.AsObject() ?? [])
            if (!Operations.SameValue(c.Sys.ReadValue(h, k, n), v)) return $"{n} 和原来不一样";
        foreach (var (n, v) in b["urls"]?.AsObject() ?? [])
            if (!Operations.SameValue(c.Sys.ReadValue(h, k + "\\" + Urls, n), v)) return "启动页网址和原来不一样";
        return null;
    }
    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b) => "已删除锁定主页和启动页的策略，重新打开浏览器后可以在设置里自己改";
}
