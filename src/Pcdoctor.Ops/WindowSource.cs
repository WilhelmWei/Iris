using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

/// <summary>窗口瞄准器点中的窗口属于哪个程序（只在用户点了「瞄准」时读一次）。</summary>
public sealed record WindowFacts(string Process, string? Path, string? Company, string? Product, string? Signer, string? ParentProcess);

/// <summary>
/// 窗口瞄准器：用户点中一个弹窗后，说清它是谁弹的、怎么启动的、怎么让它不再弹。
/// 来源只看最近一次本机体检报告（开机自启动、计划任务、服务、已装软件、预筛结果），不另外扫描。
/// </summary>
public static class WindowSource
{
    static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "ShellHost", "explorer", "StartMenuExperienceHost", "SearchHost", "SearchApp",
    };
    static readonly HashSet<string> NotLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "services", "taskhostw", "taskeng", "wininit", "winlogon", "userinit", "sihost", "Iris",
    };

    static string Leaf(string p) => p.Split('\\', '/')[^1];
    static string NoExe(string p) => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a.Trim('"'), b?.Trim('"'), StringComparison.OrdinalIgnoreCase);

    /// <summary>计划任务的程序是不是这个窗口的程序：写了完整路径的比完整路径（展开 %ProgramFiles% 这类变量），
    /// 只写了文件名的才按文件名比（不同厂商都可能叫 updater.exe）。</summary>
    static bool TaskRuns(string execute, string path, string leaf)
    {
        var x = Environment.ExpandEnvironmentVariables(execute.Trim().Trim('"'));
        return x.Contains('\\') || x.Contains('/') ? Same(x, path) : x.Equals(leaf, StringComparison.OrdinalIgnoreCase) || (x + ".exe").Equals(leaf, StringComparison.OrdinalIgnoreCase);
    }

    public static string KindOf(WindowFacts w) =>
        Shell.Contains(w.Process) ? "system" : Hijack.IsBrowser(w.Path ?? w.Process + ".exe") ? "browser" : "app";

    public static JsonObject Explain(WindowFacts w, JsonNode? report)
    {
        var kind = KindOf(w);
        var launched = new JsonArray();
        var flagIds = new List<string>();
        string? software = null, publisher = null;
        var S = report?["sections"];
        if (kind == "app" && S != null && !string.IsNullOrEmpty(w.Path))
        {
            var leaf = Leaf(w.Path);
            var names = new List<(string section, string name)>();
            foreach (var it in (S["startup"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
                if (Same(it["exe"]?.ToString(), w.Path))
                {
                    launched.Add(new JsonObject { ["kind"] = "startup", ["name"] = it["name"]?.ToString(), ["enabled"] = it["enabled"]?.DeepClone() });
                    names.Add(("startup", it["name"]?.ToString() ?? ""));
                }
            foreach (var it in (S["scheduledTasks"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
                if ((it["actions"] as JsonArray ?? []).Any(a => a?["execute"]?.ToString() is { } x && TaskRuns(x, w.Path, leaf)))
                {
                    launched.Add(new JsonObject { ["kind"] = "task", ["name"] = it["name"]?.ToString(), ["state"] = it["state"]?.DeepClone() });
                    names.Add(("scheduledTasks", it["name"]?.ToString() ?? ""));
                }
            foreach (var it in (S["services"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
                if (Same(it["exe"]?.ToString(), w.Path) || Same(it["serviceDll"]?.ToString(), w.Path))
                {
                    launched.Add(new JsonObject { ["kind"] = "service", ["name"] = (it["displayName"] ?? it["name"])?.ToString() });
                    names.Add(("services", it["name"]?.ToString() ?? ""));
                }
            var sw = (S["software"]?["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(x => (x, loc: x["location"]?.ToString()?.TrimEnd('\\') ?? ""))
                .Where(t => t.loc.Length > 3 && w.Path.StartsWith(t.loc + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.loc.Length).Select(t => t.x).FirstOrDefault();
            if (sw != null)
            {
                software = sw["name"]?.ToString();
                publisher = sw["publisher"]?.ToString();
                names.Add(("software", software ?? ""));
            }
            names.Add(("processes", leaf));
            names.Add(("processes", NoExe(leaf)));
            foreach (var f in (report!["flags"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var sec = f["section"]?.ToString();
                var obj = f["object"]?.ToString() ?? "";
                if (names.Any(n => n.section == sec && n.name.Length > 0 && n.name.Equals(obj, StringComparison.OrdinalIgnoreCase))
                    || (f["evidence"]?.ToString() ?? "").Contains(w.Path, StringComparison.OrdinalIgnoreCase))
                    flagIds.Add(f["id"]!.ToString());
            }
        }
        if (kind == "app" && w.ParentProcess is { Length: > 0 } parent && !NotLaunchers.Contains(NoExe(parent)) && !Same(NoExe(parent), w.Process))
            launched.Add(new JsonObject { ["kind"] = "parent", ["name"] = parent });

        var who = software ?? w.Product ?? w.Company ?? NoExe(w.Process);
        string advice = kind switch
        {
            "system" => "这是 Windows 的系统通知或系统窗口。通知最上面一行写着是哪个程序发的：写着浏览器名字的多半是网页通知，可以在浏览器设置里移除那个网站；其他程序可以在「设置 → 系统 → 通知」里单独关掉。",
            "browser" => "这个窗口是浏览器开的：可能是某个网页弹的窗口，也可能是网页通知。关掉那个网页就好；如果是右下角弹出的通知，在浏览器设置里移除允许发通知的网站。",
            _ when launched.Count > 0 => $"它是「{who}」的程序，由{Describe(launched)}启动。" + (flagIds.Count > 0
                ? "体检报告里也提到了它，可以在报告里直接处理。"
                : "关掉它的自启动或计划任务，通常就不会再弹；不再需要这个软件的话，卸载最干净。"),
            _ => $"它是「{who}」的程序。如果是你在用的软件弹的（例如更新提醒、资讯），在软件自己的设置里关掉提醒；不认识的话，让 Iris 记下来，下次体检会重点看它。",
        };
        return new JsonObject
        {
            ["kind"] = kind, ["process"] = w.Process, ["path"] = w.Path, ["company"] = w.Company, ["product"] = w.Product, ["signer"] = w.Signer,
            ["software"] = software, ["publisher"] = publisher, ["launchedBy"] = launched,
            ["flagIds"] = new JsonArray(flagIds.Distinct().Select(x => (JsonNode?)x).ToArray()), ["advice"] = advice,
        };
    }

    static string Describe(JsonArray launched) => string.Join("、", launched.OfType<JsonObject>().Take(3).Select(l => l["kind"]?.ToString() switch
    {
        "startup" => $"开机自启动「{l["name"]}」",
        "task" => $"计划任务「{l["name"]}」",
        "service" => $"服务「{l["name"]}」",
        _ => $"程序「{l["name"]}」",
    }));

    /// <summary>"记下来"：写进「Iris 记得的事」的一句话。不写完整路径和窗口标题（会进证据包发给模型）。</summary>
    public static (string question, string answer) Memory(JsonObject explained)
    {
        var proc = explained["process"]?.ToString() ?? "";
        var who = explained["software"]?.ToString() ?? explained["product"]?.ToString() ?? explained["company"]?.ToString();
        var via = Describe(explained["launchedBy"] as JsonArray ?? []);
        var a = $"我用窗口瞄准器指认 {proc}.exe{(who != null ? $"（{who}）" : "")}{(via.Length > 0 ? $"，由{via}启动" : "")}，它弹出了我不想要的窗口，希望处理掉";
        return ("这个弹窗是谁弹的？（窗口瞄准器）", a.Length > 200 ? a[..200] : a);
    }
}
