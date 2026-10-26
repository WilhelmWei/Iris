using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pcdoctor.Core;
using Pcdoctor.Ops;

namespace Pcdoctor.App;

/// <summary>界面背后的全部业务：体检、研判、回答问题、处理方案、执行、撤销。
/// 事件通过 emit(事件名, 数据) 推给界面。</summary>
public sealed class Services(Action<string, JsonNode?> emit, Func<IntPtr> owner)
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Plan> plans = new();
    readonly JournalStore journal = new(AppData.Journal);
    CancellationTokenSource? checkupCts;
    string? currentRun;

    // ================================================================== 状态
    public JsonObject GetState()
    {
        var s = AppData.LoadSettings();
        var reports = LocalReports();
        var last = reports.Count > 0 ? reports[^1] : null;
        var prev = reports.Count > 1 ? reports[^2] : null;
        JsonObject? lastReport = last == null ? null : new JsonObject
        {
            ["id"] = last.Id, ["score"] = last.Judge["score"]?["total"]?.DeepClone(),
            ["headline"] = last.Judge["judgement"]?["headline"]?.DeepClone(), ["time"] = last.Meta["reportTime"]?.DeepClone(),
            ["delta"] = prev == null ? null : (JsonNode?)(Score(last) - Score(prev)),
        };
        JsonArray changes = [];
        if (last != null && prev != null)
            try { changes = Insights.Changes(last.Judge, prev.Judge, ReadReport(last.Id), ReadReport(prev.Id)); } catch { }
        // 处理过的项：上次体检后处理了几项（提示重新体检）；上上次处理的，这次还剩几项
        JsonObject? handled = null, handledBefore = null;
        try
        {
            if (last != null)
            {
                var h = Handled(last.Id).Where(kv => kv.Value?["status"]?.ToString() == "done").ToList();
                if (h.Count > 0) handled = new JsonObject { ["count"] = h.Count, ["time"] = h.Max(kv => kv.Value!["time"]!.ToString()) };
            }
            if (last != null && prev != null)
            {
                var keys = Handled(prev.Id).Where(kv => kv.Value?["status"]?.ToString() == "done").Select(kv => kv.Key).ToList();
                if (keys.Count > 0)
                    handledBefore = new JsonObject { ["total"] = keys.Count,
                        ["still"] = Insights.StillPresent(ReadReport(prev.Id)!, prev.Judge, keys, ReadReport(last.Id)!, last.Judge) };
            }
        }
        catch (Exception ex) { AppData.LogError("处理统计", ex); }
        return new JsonObject
        {
            ["version"] = AppData.Version,
            ["history"] = new JsonArray(reports.TakeLast(8).Select(r => (JsonNode?)new JsonArray(ShortDate(r.Meta["reportTime"]?.ToString()), Score(r))).ToArray()),
            ["changes"] = changes,
            ["update"] = update?.DeepClone(),
            ["outbox"] = Directory.GetFiles(AppData.Outbox, "*.json.gz").Length,
            ["isAdmin"] = Environment.IsPrivilegedProcess,
            ["settings"] = AppData.SettingsForUi(s),
            ["executableOps"] = new JsonArray(Catalog.Ops.Keys.Select(k => (JsonNode?)k).ToArray()),
            ["guideOps"] = new JsonArray(Catalog.GuideOps.Select(k => (JsonNode?)k).ToArray()),
            ["lastReport"] = lastReport,
            ["handled"] = handled, ["handledBefore"] = handledBefore,
            ["pending"] = last == null ? 0 : SelectedKeys(last.Id).Count,
            ["privacy"] = new JsonObject { ["version"] = AppData.PrivacyVersion, ["accepted"] = s.PrivacyAccepted == AppData.PrivacyVersion },
        };
    }

    public JsonObject GetPrivacy() => new() { ["version"] = AppData.PrivacyVersion, ["text"] = AppData.PrivacyText };

    public JsonObject AcceptPrivacy()
    {
        var s = AppData.LoadSettings();
        s.PrivacyAccepted = AppData.PrivacyVersion;
        AppData.SaveSettings(s);
        return new JsonObject { ["accepted"] = true };
    }

    sealed record LocalReport(string Id, JsonNode Meta, JsonNode Judge);

    /// <summary>本机做过的体检（不含朋友的报告），按时间从早到晚。</summary>
    static List<LocalReport> LocalReports()
    {
        var list = new List<LocalReport>();
        foreach (var d in Directory.GetDirectories(AppData.Reports).OrderBy(x => x))
        {
            try
            {
                var meta = AppData.ReadJson(Path.Combine(d, "meta.json"));
                var judge = AppData.ReadJson(Path.Combine(d, "judge.json"));
                if (meta == null || judge == null || meta["source"]?.ToString() != "local") continue;
                list.Add(new(Path.GetFileName(d), meta, judge));
            }
            catch { /* 损坏的报告跳过 */ }
        }
        return list;
    }

    static int Score(LocalReport r) => r.Judge["score"]?["total"] is JsonValue v && v.TryGetValue<double>(out var x) ? (int)Math.Round(x) : 0;
    static JsonNode? ReadReport(string id) => AppData.ReadJson(Path.Combine(AppData.ReportDir(id)!, "report.json"));

    static string ShortDate(string? time) =>
        DateTime.TryParse(time, out var t) ? $"{t.Month}月{t.Day}日" : time ?? "";

    // ================================================================== 检查更新
    JsonObject? update;

    /// <summary>启动后在后台做的事：检查更新；把上次没发出去的反馈包补发。都不打扰用户，失败就算了。</summary>
    public void StartBackgroundTasks()
    {
        _ = Task.Run(async () =>
        {
            update = await Cloud.CheckUpdate();
            if (update != null) emit("update", update.DeepClone());
            var s = AppData.LoadSettings();
            if (s.BetaConsent == "yes" && Directory.GetFiles(AppData.Outbox, "*.json.gz").Length > 0)
                await Cloud.Flush(AppData.InstallId(s));
        });
    }

    public void OpenUpdate()
    {
        if (update?["url"]?.ToString() is { } url && url.StartsWith("https://", StringComparison.Ordinal)) OpenUri(url);
    }

    // ================================================================== 体检
    public JsonObject StartCheckup(string mode, bool scan)
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref checkupCts, cts, null) != null) throw new InvalidOperationException("体检已经在进行中");
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var id = await Collect(scan, ct);
                try { await JudgeReport(id, mode, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && mode == "ai")
                {
                    // AI 研判失败（断网、Key 不对、余额不足……）：报告已经采好了，改用纯规则研判，不让用户白等
                    AppData.LogError("AI 研判", ex);
                    await JudgeReport(id, "offline", ct);
                    emit("notice", new JsonObject { ["message"] = "AI 研判没有成功（" + ex.Message + "），已改用纯规则研判。可以稍后在报告页点「重新研判」" });
                }
                emit("checkupDone", new JsonObject { ["id"] = id });
                AutoSendFeedback(id);
            }
            catch (OperationCanceledException) { emit("checkupError", new JsonObject { ["message"] = "已取消体检" }); }
            catch (Exception ex) { AppData.LogError("体检", ex); emit("checkupError", new JsonObject { ["message"] = ex.Message }); }
            finally { checkupCts = null; currentRun = null; }
        });
        return new JsonObject { ["started"] = true };
    }

    public void Cancel()
    {
        checkupCts?.Cancel();
        if (currentRun != null) try { File.WriteAllText(Path.Combine(currentRun, "cancel"), ""); } catch { }
    }

    static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    static string NewRunDir()
    {
        var d = Path.Combine(AppData.Runs, DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>启动辅助进程。elevate=true 时弹出管理员确认；用户点"否"返回 null。</summary>
    HelperProcess? StartHelper(string verb, string dir, bool elevate, params string[] extra)
    {
        var p = HelperProcess.Start(verb, dir, elevate, owner(), extra);
        if (p != null && elevate && !Environment.IsPrivilegedProcess) _ = RegainForeground();
        return p;
    }

    /// <summary>把主窗口拉回前台（不在前台、被最小化时）。由主窗口设置。</summary>
    public Action? Foreground { get; set; }

    /// <summary>UAC 确认后前台落在没有窗口的辅助进程上，Windows 会把前台随手交给别的窗口。
    /// 辅助进程一启动就允许别人拿回前台；它启动要一点时间，所以分几次尝试。</summary>
    async Task RegainForeground()
    {
        foreach (var ms in new[] { 300, 700, 1500 }) { await Task.Delay(ms); Foreground?.Invoke(); }
    }

    /// <summary>等辅助进程结束，同时把 progress.jsonl 里的新行交给 onProgress。</summary>
    static async Task<JsonNode?> WaitHelper(HelperProcess p, string dir, Action<JsonObject> onProgress, CancellationToken ct)
    {
        using var _ = p;
        var path = Path.Combine(dir, "progress.jsonl");
        var seen = 0;
        void Pump()
        {
            if (!File.Exists(path)) return;
            string[] lines;
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var r = new StreamReader(fs); lines = r.ReadToEnd().Split('\n'); }
            catch { return; }
            for (; seen < lines.Length - 1; seen++)
                if (JsonNode.Parse(lines[seen]) is JsonObject o) onProgress(o);
        }
        while (!p.HasExited)
        {
            Pump();
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { File.WriteAllText(Path.Combine(dir, "cancel"), ""); }
        }
        Pump();
        return AppData.ReadJson(Path.Combine(dir, "done.json"));
    }

    async Task<string> Collect(bool scan, CancellationToken ct)
    {
        var dir = currentRun = NewRunDir();
        var sw = Stopwatch.StartNew();
        var timings = new JsonArray();
        emit("progress", new JsonObject { ["phase"] = "admin", ["index"] = -1, ["elapsed"] = 0 });
        var extra = scan ? new[] { "--scan" } : [];

        // 实时发现流：每开始采集一个方面，就把上一个方面记为完成（用时）；采集完成后再补上每个方面查到了什么
        string? curTitle = null;
        var curStart = TimeSpan.Zero;
        var collectStart = TimeSpan.Zero;
        void Finish(string? title)
        {
            if (curTitle != null) emit("section", new JsonObject { ["title"] = curTitle, ["detail"] = "", ["flags"] = 0, ["sec"] = Math.Round((sw.Elapsed - curStart).TotalSeconds, 1) });
            curTitle = title; curStart = sw.Elapsed;
        }
        void Progress(JsonObject o)
        {
            o["elapsed"] = sw.Elapsed.TotalSeconds;
            if (o["phase"]?.ToString() == "scan") { curTitle = "Microsoft Defender 快速扫描"; curStart = sw.Elapsed; }
            else if (o["phase"]?.ToString() == "collect" && o["title"] is { } t)
            {
                var title = t.ToString().Replace("（较慢）", "");
                o["title"] = title;
                if (collectStart == TimeSpan.Zero) collectStart = sw.Elapsed;
                if (title != curTitle) Finish(title);
            }
            emit("progress", o);
        }

        // 管理员权限能读到驱动、系统日志等；用户拒绝时用普通权限继续（结果不完整，报告里会注明）
        var p = StartHelper("collect", dir, elevate: true, extra);
        var adminSec = Math.Round(sw.Elapsed.TotalSeconds, 1);
        timings.Add(new JsonArray("等待管理员确认", adminSec));
        emit("section", new JsonObject { ["title"] = "管理员权限", ["kind"] = "admin", ["flags"] = 0, ["sec"] = adminSec,
            ["detail"] = p == null ? "没有同意，改用普通权限检查（驱动和系统日志会少一些）" : "你在 Windows 弹窗里点了“是”" });
        emit("progress", new JsonObject { ["phase"] = scan ? "scan" : "collect", ["index"] = -1, ["elapsed"] = sw.Elapsed.TotalSeconds });
        var done = p == null ? null : await WaitHelper(p, dir, Progress, CancellationToken.None);
        if (p == null || done?["code"]?.ToString() == "other-user")
        {
            // 用户拒绝了管理员权限，或者用另一个管理员账户授权：改用普通权限采集（结果少一些，报告里会注明）
            if (done != null) emit("notice", new JsonObject { ["message"] = done["error"]?.DeepClone() });
            File.Delete(Path.Combine(dir, "done.json"));
            File.Delete(Path.Combine(dir, "progress.jsonl"));
            done = await WaitHelper(StartHelper("collect", dir, elevate: false, extra)!, dir, Progress, CancellationToken.None);
        }
        Finish(null);
        ct.ThrowIfCancellationRequested();
        if (done?["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(done?["error"]?.ToString() ?? "采集失败");

        var id = NewId();
        var rdir = AppData.ReportDir(id)!;
        Directory.CreateDirectory(rdir);
        File.Copy(done!["report"]!.ToString(), Path.Combine(rdir, "report.json"), overwrite: true);
        var report = AppData.ReadJson(Path.Combine(rdir, "report.json"))!;
        AppData.WriteJson(Path.Combine(rdir, "meta.json"), new JsonObject
        {
            ["source"] = "local", ["reportTime"] = report["generatedAt"]?.DeepClone() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["machine"] = MachineLabel(report), ["isAdmin"] = report["tool"]?["isAdmin"]?.DeepClone(),
            ["timings"] = CollectTimings(timings, report, scan, sw.Elapsed - collectStart),
        });
        try { emit("sectionDetails", new JsonObject { ["rows"] = Insights.SectionSummaries(report) }); } catch { }
        try { Directory.Delete(dir, true); } catch { }
        return id;
    }

    static JsonArray CollectTimings(JsonArray timings, JsonNode report, bool scan, TimeSpan collect)
    {
        var total = report["tool"]?["elapsedSec"] is JsonValue v && v.TryGetValue<double>(out var x) ? x : collect.TotalSeconds;
        if (scan) timings.Add(new JsonArray("Defender 快速扫描", Math.Round(Math.Max(0, collect.TotalSeconds - total), 1)));
        timings.Add(new JsonArray("采集 13 个方面", Math.Round(total, 1)));
        return timings;
    }

    static string MachineLabel(JsonNode report)
    {
        var m = report["machine"];
        var os = report["sections"]?["resources"]?["os"]?["caption"]?.ToString()?.Replace("Microsoft ", "");
        return string.Join(" · ", new[] { $"{m?["manufacturer"]} {m?["model"]}".Trim(), os }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ================================================================== 研判
    static string KnownPath(string id, JsonNode meta) =>
        meta["source"]?.ToString() == "local" ? AppData.KnownFile : Path.Combine(AppData.ReportDir(id)!, "known.json");

    /// <summary>研判一律用打码后的报告：用户名、计算机名、邮箱换成占位符。纯规则模式也一样，保证"确认记录"前后一致。</summary>
    static JsonNode Redacted(JsonNode report)
    {
        var r = Redactor.RedactReport(report.DeepClone(), Environment.UserName, Environment.MachineName);
        var profile = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.Equals(profile, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            r = Redactor.RedactReport(r, profile, null);
        return r;
    }

    public async Task JudgeReport(string id, string mode, CancellationToken ct)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var meta = AppData.ReadJson(Path.Combine(dir, "meta.json"))!;
        var report = Redacted(AppData.ReadJson(Path.Combine(dir, "report.json"))!);
        var known = KnownStore.Load(KnownPath(id, meta));
        var kb = Kb.LoadEmbedded();
        var s = AppData.LoadSettings();
        var key = AppData.GetApiKey(s);
        if (s.Provider == "hosted" && (!s.AllowHosted || AppData.HostedBaseUrl == null)) key = null;  // 没有打开"允许发送到托管服务"：不发送
        var sw = Stopwatch.StartNew();
        JsonObject result;
        if (mode != "ai" || key == null)
        {
            emit("progress", new JsonObject { ["phase"] = "judge", ["mode"] = "offline", ["elapsed"] = 0 });
            result = Judge.RunOffline(report, known, kb);
        }
        else
        {
            var p = Judge.Prepare(report, known, kb);
            AppData.WriteJson(Path.Combine(dir, "evidence.json"), p.ModelEvidence);  // 发给模型的全部内容，留档可查
            using var tick = new System.Threading.Timer(_ => emit("progress", new JsonObject { ["phase"] = "judge", ["mode"] = "ai", ["elapsed"] = sw.Elapsed.TotalSeconds }), null, 0, 1000);
            var llm = await LlmClient.CallAsync(Prompt.LoadSystemPrompt(), p.ModelEvidence, s.Model, s.BaseUrl, key, ct: ct);
            result = Judge.Run(report, llm.Output, s.Model, known, kb);
            result["usage"] = new JsonArray(llm.Usage.Select(u => u?.DeepClone()).ToArray());
            result["elapsedSec"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
        }
        AppData.WriteJson(Path.Combine(dir, "judge.json"), result);

        // 交叉校验：AI 研判时同时跑一遍纯规则，结论不一致的地方在界面上标出来
        var cross = Path.Combine(dir, "crosscheck.json");
        if (result["offline"]?.GetValue<bool>() == true) File.Delete(cross);
        else try { AppData.WriteJson(cross, Insights.CrossCheck(result, Judge.RunOffline(report, known, kb))); } catch { }

        // 用时记进 meta.json（重新研判时覆盖上一次的研判用时）
        var label = result["offline"]?.GetValue<bool>() == true ? "规则研判" : "AI 研判";
        var timings = meta["timings"] as JsonArray ?? new JsonArray();
        foreach (var t in timings.Where(t => t?[0]?.ToString() is "AI 研判" or "规则研判").ToList()) timings.Remove(t);
        timings.Add(new JsonArray(label, Math.Round(sw.Elapsed.TotalSeconds, 1)));
        meta["timings"] = timings.DeepClone();
        AppData.WriteJson(Path.Combine(dir, "meta.json"), meta);
        if (meta["source"]?.ToString() == "local") try { CarryOver(id); } catch (Exception ex) { AppData.LogError("带入处理清单", ex); }
    }

    // ================================================================== 处理清单（存在报告旁边的 selected.json）和处理状态
    static string SelectedPath(string id) => Path.Combine(AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对"), "selected.json");

    static List<string> SelectedKeys(string id) =>
        (AppData.ReadJson(SelectedPath(id))?["keys"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();

    public JsonObject SetSelected(string id, IEnumerable<string> keys)
    {
        var path = SelectedPath(id);
        var o = new JsonObject();  // 用户改过清单以后，"从上次带过来几项"的说明就不再显示
        o["keys"] = new JsonArray(keys.Where(k => k.Length is > 0 and <= 200).Distinct().Take(500).Select(k => (JsonNode?)k).ToArray());
        AppData.WriteJson(path, o);
        return new JsonObject { ["saved"] = true };
    }

    /// <summary>新的一次体检：把上一份报告里选了、还没处理的项带过来（按规则和对象对应，预筛编号每次会变）。</summary>
    void CarryOver(string id)
    {
        if (File.Exists(SelectedPath(id))) return;
        var reports = LocalReports();
        if (reports.Count < 2 || reports[^1].Id != id) return;
        var prev = reports[^2];
        var done = Handled(prev.Id).Where(kv => kv.Value?["status"]?.ToString() == "done").Select(kv => kv.Key).ToHashSet();
        var keys = SelectedKeys(prev.Id).Where(k => !done.Contains(k)).ToList();
        if (keys.Count == 0) return;
        var carried = Insights.CarryKeys(ReadReport(prev.Id)!, prev.Judge, keys, ReadReport(id)!, reports[^1].Judge);
        AppData.WriteJson(SelectedPath(id), new JsonObject
        {
            ["keys"] = new JsonArray(carried.Select(k => (JsonNode?)k).ToArray()),
            ["carried"] = carried.Count, ["dropped"] = Math.Max(0, keys.Count - carried.Count),
        });
    }

    /// <summary>这份报告里每条结论的处理状态（来自操作记录）：done 已处理 / failed 没生效 / undone 已撤销。同一条处理过多次时看最近一批。</summary>
    JsonObject Handled(string id)
    {
        var o = new JsonObject();
        foreach (var (key, h) in HandledStatus.Of(AllEntries(), id))
        {
            var latest = h.Latest;
            o[key] = new JsonObject
            {
                ["status"] = h.Status, ["time"] = latest.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm"), ["batchId"] = latest.BatchId,
                ["message"] = latest.Status == Status.Undone ? $"已于 {latest.UndoneUtc?.ToLocalTime():MM-dd HH:mm} 撤销" : latest.Message,
            };
        }
        return o;
    }

    public JsonObject GetReport(string id)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var meta = AppData.ReadJson(Path.Combine(dir, "meta.json")) ?? new JsonObject();
        var judge = AppData.ReadJson(Path.Combine(dir, "judge.json"));
        var alt = new JsonObject();
        if (judge != null && meta["source"]?.ToString() == "local")
            foreach (var (k, op) in PlanBuilder.AltOpsFor(AppData.ReadJson(Path.Combine(dir, "report.json"))!, judge))
                alt[k] = new JsonObject { ["op"] = op, ["name"] = Catalog.Ops[op].Name, ["risk"] = Catalog.Ops[op].Risk };
        var report = AppData.ReadJson(Path.Combine(dir, "report.json"));
        int unknown = 0;
        try { if (judge != null && report != null) unknown = Insights.UnknownObjects(report, judge); } catch { }
        return new JsonObject
        {
            ["id"] = id, ["result"] = judge, ["meta"] = meta, ["altOps"] = alt,
            ["answers"] = AppData.ReadJson(Path.Combine(dir, "answers.json")) ?? new JsonObject(),
            ["marks"] = AppData.ReadJson(Path.Combine(dir, "feedback.json")) ?? new JsonObject(),
            ["dismissed"] = AppData.ReadJson(Path.Combine(dir, "dismissed.json")) ?? new JsonArray(),
            ["crossCheck"] = AppData.ReadJson(Path.Combine(dir, "crosscheck.json")) ?? new JsonObject(),
            ["unknownObjects"] = unknown,
            ["defender"] = DefenderInfo(report, meta),
            ["handled"] = Handled(id),
            ["selected"] = AppData.ReadJson(Path.Combine(dir, "selected.json")) ?? new JsonObject(),
            ["sent"] = meta["feedbackSent"]?.DeepClone(),
            ["canExecute"] = meta["source"]?.ToString() == "local",
        };
    }

    /// <summary>报告里"有没有中毒"那张卡片要用的 Defender 状态：这次有没有扫描、多久没扫了、实时保护开没开。</summary>
    static JsonObject? DefenderInfo(JsonNode? report, JsonNode meta)
    {
        if (report?["sections"]?["security"]?["defender"] is not JsonObject d) return null;
        static JsonNode? N(JsonNode? v) => v is JsonValue x && x.TryGetValue<double>(out var n) && n >= 0 && n < 100000 ? JsonValue.Create(n) : null;
        bool scanned = (meta["timings"] as JsonArray)?.Any(t => t?[0]?.ToString() == "Defender 快速扫描") == true;
        return new JsonObject
        {
            ["scanned"] = scanned,
            ["realTime"] = d["realTimeProtection"] is JsonValue r && r.TryGetValue<bool>(out var rb) ? rb : null,
            ["quickScanAgeDays"] = N(d["quickScanAgeDays"]), ["fullScanAgeDays"] = N(d["fullScanAgeDays"]),
            ["signatureAgeDays"] = N(d["signatureAgeDays"]),
        };
    }

    public async Task<JsonObject> Rejudge(string id, string mode)
    {
        await JudgeReport(id, mode, CancellationToken.None);
        return new JsonObject { ["id"] = id };
    }

    /// <summary>打开朋友发来的报告：复制一份进来研判。别人电脑的报告只能看、不能执行，确认记录也单独存放。</summary>
    public async Task<JsonObject?> ImportReport(string path)
    {
        var report = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidOperationException("文件不是 JSON");
        if (!(report["schema"]?.ToString() ?? "").StartsWith("pcdoctor.collect", StringComparison.Ordinal))
            throw new InvalidOperationException("这不是 AI 电脑体检的采集报告（pcdoctor-report-*.json）");
        var id = NewId() + "-f";
        var dir = AppData.ReportDir(id)!;
        Directory.CreateDirectory(dir);
        AppData.WriteJson(Path.Combine(dir, "report.json"), report);
        AppData.WriteJson(Path.Combine(dir, "meta.json"), new JsonObject
        {
            ["source"] = "file", ["file"] = Path.GetFileName(path),
            ["reportTime"] = report["generatedAt"]?.DeepClone(), ["machine"] = MachineLabel(report),
        });
        var s = AppData.LoadSettings();
        await JudgeReport(id, AppData.GetApiKey(s) != null ? s.Mode : "offline", CancellationToken.None);
        return new JsonObject { ["id"] = id };
    }

    // ================================================================== 回答问题
    public JsonObject Answer(string id, string key, string answer, string note)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var answers = AppData.ReadJson(Path.Combine(dir, "answers.json")) as JsonObject ?? new JsonObject();
        answers[key] = new JsonObject { ["answer"] = answer, ["note"] = note, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        AppData.WriteJson(Path.Combine(dir, "answers.json"), answers);
        if (answer == "yes")
        {
            // "是，我知道"记进确认记录：下次研判不再追问；识别类和用户自己的 Defender 设置不再扣分
            var meta = AppData.ReadJson(Path.Combine(dir, "meta.json"))!;
            var flags = Redacted(AppData.ReadJson(Path.Combine(dir, "report.json"))!)["flags"]!.AsArray();
            var path = KnownPath(id, meta);
            var entries = KnownStore.Load(path);
            KnownStore.Confirm(flags, key.Split('+'), string.IsNullOrWhiteSpace(note) ? "用户确认：知道这一项" : note, entries);
            KnownStore.Save(path, entries);
        }
        return new JsonObject { ["saved"] = true };
    }

    // ================================================================== 处理
    public JsonObject BuildPlan(string id, IEnumerable<string> keys)
    {
        // 用户回答了"不认识"的项，用可撤销的替代操作（关闭自启动、禁用任务、服务改手动）
        var answers = AppData.ReadJson(Path.Combine(AppData.ReportDir(id) ?? "", "answers.json")) as JsonObject ?? new JsonObject();
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var meta = AppData.ReadJson(Path.Combine(dir, "meta.json"))!;
        if (meta["source"]?.ToString() != "local") throw new InvalidOperationException("这份报告来自别的电脑，只能查看，不能在这台电脑上执行");
        var plan = PlanBuilder.Build(AppData.ReadJson(Path.Combine(dir, "report.json"))!, AppData.ReadJson(Path.Combine(dir, "judge.json"))!,
                                     keys, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Overrides(dir, answers));
        plans[plan.PlanId] = plan;
        return JsonSerializer.SerializeToNode(plan, OpsJson.Default.Plan)!.AsObject().Also(o => o["needsAdmin"] = plan.NeedsAdmin);
    }

    static Dictionary<string, string> Overrides(string dir, JsonObject answers)
    {
        var alt = PlanBuilder.AltOpsFor(AppData.ReadJson(Path.Combine(dir, "report.json"))!, AppData.ReadJson(Path.Combine(dir, "judge.json"))!);
        return alt.Where(kv => answers[kv.Key]?["answer"]?.ToString() == "no").ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>确认框里每一项的内容：改什么、能不能撤销。</summary>
    public sealed record ChangeItem(string Title, string Change, bool Reversible, string Undo, bool NeedsAdmin);

    /// <summary>执行前必须经过的确认（由主窗口弹出 Windows 原生对话框，网页界面绕不过去）。返回 true 才执行。</summary>
    public Func<string, IReadOnlyList<ChangeItem>, bool> Confirm { get; set; } = (_, _) => false;

    public JsonObject ExecutePlan(string planId, string reportId, JsonArray uiSteps)
    {
        if (!plans.TryGetValue(planId, out var plan)) throw new InvalidOperationException("处理方案已过期，请回到报告重新选择");
        // 只采用界面传回来的"保留了哪些步骤"和缓存清理的勾选，步骤内容以程序生成的为准
        var keep = uiSteps.Select(s => s?["key"]?.ToString()).ToHashSet();
        var steps = plan.Steps.Where(s => keep.Contains(s.Key)).ToList();
        foreach (var s in steps.Where(s => s.Op == "cache.clean"))
        {
            var ui = uiSteps.FirstOrDefault(x => x?["key"]?.ToString() == s.Key)?["cacheChoice"];
            s.CacheChoice = ui == null ? null : new CacheChoice
            {
                Categories = ui["categories"]?.AsArray().Select(x => x!.ToString()).ToList() ?? [],
                Age = ui["age"] is JsonValue v && v.TryGetValue<int>(out var a) ? a : null,
            };
        }
        if (steps.Count == 0) throw new InvalidOperationException("没有要执行的步骤");
        var items = steps.Select(s => new ChangeItem($"{s.OpName}：{s.Title}",
            s.Op == "cache.clean" && s.CacheChoice != null
                ? $"把「{string.Join("、", s.CacheChoice.Categories)}」里 {s.CacheChoice.Age} 天前的文件移到回收站" : s.Change,
            s.Reversible, s.Undo, s.NeedsAdmin)).ToList();
        if (!Confirm($"确认执行这 {steps.Count} 项修改？", items)) return new JsonObject { ["cancelled"] = true };
        var batch = "batch-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        _ = Task.Run(() => RunSteps(steps, reportId, batch));
        return new JsonObject { ["started"] = true };
    }

    async Task RunSteps(List<PlanStep> steps, string reportId, string batch)
    {
        try
        {
            // 先在普通权限里执行不需要管理员的步骤（当前用户的自启动、卸载软件）
            var ctx = Helper.UserContext();
            foreach (var s in steps.Where(s => !s.NeedsAdmin))
            {
                emit("opProgress", new JsonObject { ["key"] = s.Key, ["status"] = "running", ["message"] = "正在执行…" });
                var e = await Task.Run(() => Executor.RunStep(s, ctx, reportId, batch));
                e.Owner = Me;
                journal.Save(e);
                emit("opProgress", new JsonObject { ["key"] = s.Key, ["status"] = e.Status, ["message"] = e.Message });
            }
            var admin = steps.Where(s => s.NeedsAdmin).ToList();
            if (admin.Count > 0)
            {
                var dir = NewRunDir();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Plan { PlanId = batch, ReportId = reportId, Steps = admin }, OpsJson.Default.Plan);
                File.WriteAllBytes(Path.Combine(dir, "plan.json"), bytes);
                // 方案的哈希放在命令行里（UAC 确认后改不了），辅助进程校验后才执行；记录由辅助进程存进受保护目录
                var p = StartHelper("execute", dir, elevate: true, "--sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                var done = p == null ? null : await WaitHelper(p, dir, o => emit("opProgress", o), CancellationToken.None);
                if (p == null || done?["ok"]?.GetValue<bool>() != true)
                    foreach (var s in admin)
                        emit("opProgress", new JsonObject { ["key"] = s.Key, ["status"] = Status.Failed,
                            ["message"] = p == null ? "没有同意管理员权限，这一步没有执行" : done?["error"]?.ToString() ?? "辅助程序没有完成" });
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch (Exception ex) { AppData.LogError("执行", ex); emit("planError", new JsonObject { ["message"] = "执行时出错：" + ex.Message }); }
        finally { emit("planDone", new JsonObject { ["batchId"] = batch }); }
    }

    // ================================================================== 操作记录
    /// <summary>两处记录：普通权限执行的在 %LOCALAPPDATA%\pcdoctor\journal；管理员执行的在受保护目录（普通用户只能读，防篡改）。</summary>
    static string Me => System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    IEnumerable<JournalEntry> AllEntries()
    {
        var mine = journal.All().Where(e => !e.NeedsAdmin);  // 普通目录里自称"管理员"的记录不认
        var admin = Directory.Exists(Helper.AdminJournal)
            ? new JournalStore(Helper.AdminJournal).All().Where(e => e.Owner == Me) : [];
        return mine.Concat(admin).OrderByDescending(e => e.TimeUtc);
    }

    static bool CanUndo(JournalEntry e) => e.Status == Status.Done && e.Reversible;

    public JsonArray GetJournal() => new(AllEntries().Select(e => (JsonNode?)new JsonObject
    {
        ["id"] = e.Id, ["batchId"] = e.BatchId, ["time"] = e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), ["op"] = e.Op, ["opName"] = e.OpName,
        ["title"] = e.Title, ["risk"] = e.Risk, ["status"] = e.Status, ["findingKey"] = e.FindingKey, ["reportId"] = e.ReportId,
        ["message"] = e.Status == Status.Undone ? $"已于 {e.UndoneUtc?.ToLocalTime():MM-dd HH:mm} 撤销：{e.UndoMessage}" : e.Message,
        ["reversible"] = e.Reversible, ["undoHint"] = e.UndoHint,
    }).ToArray());

    /// <summary>撤销一条，或者一键撤销同一批里所有还能撤销的步骤（后做的先撤）。撤销同样会修改系统，执行前也要确认。</summary>
    public async Task<JsonObject> Undo(string? id, string? batchId)
    {
        var all = AllEntries().ToList();
        var targets = (batchId != null ? all.Where(e => e.BatchId == batchId) : all.Where(e => e.Id == id))
            .Where(CanUndo).OrderByDescending(e => e.TimeUtc).ToList();
        if (targets.Count == 0) throw new InvalidOperationException("没有可以撤销的步骤");
        var items = targets.Select(e => new ChangeItem($"{e.OpName}：{e.Title}", "恢复到这一步执行之前的状态（按当时的备份）", true,
            "撤销后如需再次处理，可以重新体检后再执行", e.NeedsAdmin)).ToList();
        if (!Confirm(targets.Count == 1 ? "确认撤销这一步？" : $"确认撤销这一批的 {targets.Count} 步？", items))
            return new JsonObject { ["cancelled"] = true, ["ok"] = false, ["message"] = "已取消" };

        var messages = new List<string>();
        var ok = true;
        foreach (var e in targets.Where(e => !e.NeedsAdmin))
        {
            var o = Executor.Undo(e, Helper.UserContext());
            journal.Save(e);
            ok &= o.Status == Status.Done;
            messages.Add(o.Message);
        }
        var admin = targets.Where(e => e.NeedsAdmin).ToList();
        if (admin.Count > 0)
        {
            // 管理员执行的步骤：只把编号交给辅助进程，它从受保护目录读取记录和备份；一批只弹一次管理员确认
            var dir = NewRunDir();
            try
            {
                var p = StartHelper("undo", dir, elevate: true, "--ids", string.Join(",", admin.Select(e => e.Id)))
                        ?? throw new InvalidOperationException("没有同意管理员权限，没有撤销");
                var done = await WaitHelper(p, dir, _ => { }, CancellationToken.None);
                ok &= done?["ok"]?.GetValue<bool>() == true;
                messages.Add(done?["message"]?.ToString() ?? done?["error"]?.ToString() ?? "撤销失败");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        return new JsonObject { ["ok"] = ok, ["message"] = targets.Count == 1 ? messages[0]
            : ok ? $"已撤销 {targets.Count} 步，重新检测确认都回到了原来的状态" : "部分步骤没能撤销，详情见操作记录" };
    }

    // ================================================================== 存储空间（设置里可以看、可以清）
    static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : BackupPruner.DirSize(path);
    static string UserBackups => Path.Combine(AppData.Root, "backups");
    static string WebViewFlag => Path.Combine(AppData.Root, "clear-webview-cache");

    public JsonObject StorageInfo()
    {
        var reports = Directory.GetDirectories(AppData.Reports);
        var mine = journal.All().Where(e => !e.NeedsAdmin).ToList();
        var admin = Directory.Exists(Helper.AdminJournal) ? new JournalStore(Helper.AdminJournal).All().Where(e => e.Owner == Me).ToList() : [];
        long backupBytes = mine.Concat(admin).Sum(e => (long)System.Text.Encoding.UTF8.GetByteCount(e.Backup.ToJsonString())) + Size(UserBackups);
        return new JsonObject
        {
            ["reports"] = new JsonObject { ["count"] = reports.Length, ["local"] = LocalReports().Count, ["bytes"] = reports.Sum(Size) },
            ["backups"] = new JsonObject
            {
                ["count"] = mine.Concat(admin).Count(e => e.Backup.Count > 0), ["bytes"] = backupBytes,
                ["admin"] = admin.Count(e => e.Backup.Count > 0),
            },
            ["webview"] = new JsonObject { ["bytes"] = Size(AppData.WebView), ["pending"] = File.Exists(WebViewFlag) },
            ["outbox"] = new JsonObject { ["count"] = Directory.GetFiles(AppData.Outbox, "*.json.gz").Length, ["bytes"] = Size(AppData.Outbox) },
        };
    }

    static string Mb(long b) => b < 1024 * 1024 ? $"{Math.Max(1, b / 1024)} KB" : $"{b / 1048576.0:0.#} MB";

    /// <summary>清理一类数据。每一类都先弹 Windows 确认框，写清删什么、删了之后有什么影响。</summary>
    public async Task<JsonObject> CleanStorage(string kind, int days)
    {
        switch (kind)
        {
            case "reports":
            {
                var local = LocalReports();
                var old = local.Take(Math.Max(0, local.Count - 5)).Select(r => AppData.ReportDir(r.Id)!).ToList();
                var friends = Directory.GetDirectories(AppData.Reports).Where(d => d.EndsWith("-f", StringComparison.Ordinal)
                    && Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-30)).ToList();
                var targets = old.Concat(friends).ToList();
                if (targets.Count == 0) return new JsonObject { ["ok"] = true, ["message"] = "没有需要清理的旧报告（本机保留最近 5 份）" };
                if (!Confirm($"删除 {targets.Count} 份旧体检报告？", [new($"本机较早的 {old.Count} 份报告、30 天前打开的 {friends.Count} 份朋友的报告",
                        $"共 {Mb(targets.Sum(Size))}；保留最近 5 份本机报告", false, "删除后这些报告打不开，健康分走势里也会少这几个点", false)]))
                    return new JsonObject { ["cancelled"] = true };
                foreach (var d in targets) try { Directory.Delete(d, true); } catch { }
                return new JsonObject { ["ok"] = true, ["message"] = $"已删除 {targets.Count} 份旧报告" };
            }
            case "backups":
            {
                if (days is < 1 or > 3650) throw new ArgumentException("天数不对");
                var hasAdmin = Directory.Exists(Helper.AdminJournal)
                    && BackupPruner.AnyToPrune(new JournalStore(Helper.AdminJournal).All().Where(e => e.Owner == Me).ToList(), DateTime.UtcNow.AddDays(-days));
                if (!Confirm("清理旧的操作备份？", [new($"已经撤销、没有生效的步骤的备份，以及 {days} 天以前的备份",
                        "最近一次执行的那一批永远保留", false, $"{days} 天以前、还没撤销的步骤，清理后就不能再一键撤销", hasAdmin)]))
                    return new JsonObject { ["cancelled"] = true };
                var r = BackupPruner.Prune(journal, UserBackups, DateTime.UtcNow.AddDays(-days), DateTime.UtcNow, e => !e.NeedsAdmin);
                int n = r.Entries; long bytes = r.Bytes;
                string extra = "";
                if (hasAdmin)
                {
                    var dir = NewRunDir();
                    try
                    {
                        var p = StartHelper("prune", dir, elevate: true, "--days", days.ToString());
                        var done = p == null ? null : await WaitHelper(p, dir, _ => { }, CancellationToken.None);
                        if (done?["ok"]?.GetValue<bool>() == true) { n += done["entries"]!.GetValue<int>(); bytes += done["bytes"]!.GetValue<long>(); }
                        else extra = "。管理员操作的备份没有清理（" + (done?["error"]?.ToString() ?? "没有同意管理员权限") + "）";
                    }
                    finally { try { Directory.Delete(dir, true); } catch { } }
                }
                return new JsonObject { ["ok"] = true, ["message"] = n == 0 ? "没有需要清理的备份" + extra : $"已清理 {n} 条备份，释放 {Mb(bytes)}" + extra };
            }
            case "webview":
                if (!Confirm("清理界面缓存？", [new($"删除 {AppData.WebView}", "Iris 运行时这个文件夹被占用，下次启动 Iris 时再删除", false, "没有影响，会自动重新生成", false)]))
                    return new JsonObject { ["cancelled"] = true };
                File.WriteAllText(WebViewFlag, "");
                return new JsonObject { ["ok"] = true, ["message"] = "下次启动 Iris 时会清理界面缓存" };
            case "outbox":
                var files = Directory.GetFiles(AppData.Outbox, "*.json.gz");
                if (files.Length == 0) return new JsonObject { ["ok"] = true, ["message"] = "没有待发送的反馈" };
                if (!Confirm($"删除 {files.Length} 个待发送的反馈包？", [new("还没发出去的内测反馈包", Mb(files.Sum(f => new FileInfo(f).Length)), false, "删除后这些反馈不会再发给作者", false)]))
                    return new JsonObject { ["cancelled"] = true };
                foreach (var f in files) try { File.Delete(f); } catch { }
                return new JsonObject { ["ok"] = true, ["message"] = $"已删除 {files.Length} 个待发送的反馈包" };
            default: throw new ArgumentException("不认识的清理类型");
        }
    }

    /// <summary>启动时调用：上次要求清理界面缓存的，在创建 WebView2 之前删掉。</summary>
    public static void ClearWebViewCacheIfRequested()
    {
        if (!File.Exists(WebViewFlag)) return;
        try { Directory.Delete(AppData.WebView, true); } catch { }
        try { File.Delete(WebViewFlag); } catch { }
    }

    /// <summary>"先不管"：记在报告旁边的 dismissed.json，下次打开报告时这些项仍然排在后面。</summary>
    public JsonObject SetDismissed(string id, string key, bool on)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var path = Path.Combine(dir, "dismissed.json");
        var keys = (AppData.ReadJson(path) as JsonArray ?? []).Select(x => x?.ToString()).Where(x => x != null).ToHashSet();
        if (on) keys.Add(Trim(key, 200)); else keys.Remove(key);
        AppData.WriteJson(path, new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()));
        return new JsonObject { ["saved"] = true };
    }

    // ================================================================== 内测反馈
    static readonly string[] MarkKinds = ["wrong", "inaccurate", "missed", "other"];

    /// <summary>"这条判断不对"：记在报告旁边的 feedback.json。同意过内测反馈时，顺手把这一条（不含整份报告）发出去。</summary>
    public JsonObject MarkFinding(string id, string key, string kind, string note)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        if (!MarkKinds.Contains(kind)) throw new ArgumentException("反馈类型不对");
        var path = Path.Combine(dir, "feedback.json");
        var marks = AppData.ReadJson(path) as JsonObject ?? new JsonObject();
        if (kind == "other" && string.IsNullOrWhiteSpace(note)) marks.Remove(key);  // 撤回
        else marks[key] = new JsonObject { ["kind"] = kind, ["note"] = Trim(note, 500), ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        AppData.WriteJson(path, marks);
        var s = AppData.LoadSettings();
        var meta = AppData.ReadJson(Path.Combine(dir, "meta.json"));
        var willSend = s.BetaConsent == "yes" && Cloud.FeedbackUrl != null && meta?["source"]?.ToString() == "local";
        if (willSend)
        {
            var judge = AppData.ReadJson(Path.Combine(dir, "judge.json"));
            var finding = (judge?["judgement"]?["findings"] as JsonArray)?.FirstOrDefault(f => string.Join("+", (f?["flagIds"] as JsonArray ?? []).Select(x => x?.ToString())) == key);
            var payload = Envelope("mark", id, s);
            payload["mark"] = new JsonObject { ["key"] = key, ["kind"] = kind, ["note"] = Trim(note, 500), ["finding"] = finding?.DeepClone() };
            payload["mode"] = judge?["offline"]?.GetValue<bool>() == true ? "offline" : "ai";
            Cloud.Enqueue("mark", payload);
            _ = Cloud.Flush(AppData.InstallId(s));
        }
        return new JsonObject { ["saved"] = true, ["sent"] = willSend };
    }

    static string Trim(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

    static JsonObject Envelope(string kind, string id, AppData.Settings s) => new()
    {
        ["schema"] = "iris.feedback/1", ["kind"] = kind, ["app"] = AppData.Version, ["installId"] = AppData.InstallId(s),
        ["reportId"] = id, ["createdUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>
    /// 完整的反馈包：去隐私的采集报告（用户名、计算机名、邮箱换成占位符，再去掉代理、DNS、hosts）、研判结果、
    /// 你的回答和"判断不对"的标记、用时、交叉校验、最近的错误记录（同样打码）。不含 API Key。
    /// </summary>
    JsonObject FeedbackPackage(string id, AppData.Settings s)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var meta = AppData.ReadJson(Path.Combine(dir, "meta.json")) ?? new JsonObject();
        if (meta["source"]?.ToString() != "local") throw new InvalidOperationException("朋友发来的报告不能作为你的反馈发送");
        var p = Envelope("checkup", id, s);
        p["meta"] = new JsonObject
        {
            ["reportTime"] = meta["reportTime"]?.DeepClone(), ["machine"] = meta["machine"]?.DeepClone(),
            ["isAdmin"] = meta["isAdmin"]?.DeepClone(), ["timings"] = meta["timings"]?.DeepClone(),
        };
        p["settings"] = new JsonObject { ["mode"] = s.Mode, ["provider"] = s.Provider, ["model"] = s.Model, ["theme"] = s.Theme };
        p["report"] = Insights.ScrubForFeedback(Redacted(AppData.ReadJson(Path.Combine(dir, "report.json"))!));
        p["judge"] = AppData.ReadJson(Path.Combine(dir, "judge.json"))?.DeepClone();
        p["answers"] = AppData.ReadJson(Path.Combine(dir, "answers.json"))?.DeepClone() ?? new JsonObject();
        p["marks"] = AppData.ReadJson(Path.Combine(dir, "feedback.json"))?.DeepClone() ?? new JsonObject();
        p["crossCheck"] = AppData.ReadJson(Path.Combine(dir, "crosscheck.json"))?.DeepClone();
        p["errorLog"] = RecentErrors();
        return p;
    }

    /// <summary>error.log 的最后 16 KB，打码后带上，方便定位内测时遇到的错误。</summary>
    static string RecentErrors()
    {
        try
        {
            if (!File.Exists(AppData.ErrorLog)) return "";
            var text = File.ReadAllText(AppData.ErrorLog);
            if (text.Length > 16384) text = text[^16384..];
            var profile = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return Redactor.HidePrivate(Redactor.HidePrivate(text, Environment.UserName, Environment.MachineName), profile, null);
        }
        catch { return ""; }
    }

    /// <summary>同意过内测反馈时，每次体检完自动发送（一键都不用点）；没同意就什么也不做。</summary>
    void AutoSendFeedback(string id)
    {
        var s = AppData.LoadSettings();
        if (s.BetaConsent != "yes" || Cloud.FeedbackUrl == null) return;  // 没有服务器的构建不自动导出文件，界面上有手动按钮
        _ = Task.Run(async () =>
        {
            try { emit("feedbackSent", await SendFeedback(id)); }
            catch (Exception ex) { emit("feedbackSent", new JsonObject { ["ok"] = false, ["message"] = ex.Message }); }
        });
    }

    /// <summary>一键发送这份报告的反馈包。没有配置服务器时改为导出文件。</summary>
    public async Task<JsonObject> SendFeedback(string id)
    {
        var s = AppData.LoadSettings();
        if (Cloud.FeedbackUrl == null) return ExportFeedback(id);
        Cloud.Enqueue("checkup", FeedbackPackage(id, s));
        var r = await Cloud.Flush(AppData.InstallId(s));
        var ok = r.Error == null && r.Pending == 0;
        if (ok)
        {
            var dir = AppData.ReportDir(id)!;
            var meta = AppData.ReadJson(Path.Combine(dir, "meta.json"))!;
            meta["feedbackSent"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            AppData.WriteJson(Path.Combine(dir, "meta.json"), meta);
        }
        return new JsonObject { ["ok"] = ok, ["id"] = id, ["message"] = ok ? "反馈已发送，谢谢你" : r.Error ?? "还有反馈没发出去，稍后会自动重试" };
    }

    /// <summary>导出反馈包到桌面（没有服务器、或者不想自动发送时，手动发给作者）。</summary>
    public JsonObject ExportFeedback(string id)
    {
        var s = AppData.LoadSettings();
        var bytes = System.Text.Encoding.UTF8.GetBytes(FeedbackPackage(id, s).ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, $"Iris-反馈-{id}.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("iris-feedback.json", System.IO.Compression.CompressionLevel.SmallestSize);
            using var w = e.Open();
            w.Write(bytes);
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return new JsonObject { ["ok"] = true, ["exported"] = true, ["path"] = path, ["message"] = "反馈包已保存到桌面，发给作者就行" };
    }

    /// <summary>"看看会发什么"：列出反馈包里的每一部分和大小。</summary>
    public JsonObject PreviewFeedback(string? id)
    {
        var s = AppData.LoadSettings();
        id ??= LocalReports().LastOrDefault()?.Id;
        var items = new JsonArray();
        long size = 0;
        if (id != null)
        {
            var p = FeedbackPackage(id, s);
            size = Cloud.Gzip(p).Length;
            var report = p["report"]!;
            items.Add($"去隐私的体检结果：{(report["flags"] as JsonArray)?.Count ?? 0} 条预筛结果，以及进程、自启动、服务、驱动、已装软件的名称、版本、厂商、安装位置");
            items.Add($"研判结论：{(p["judge"]?["judgement"]?["findings"] as JsonArray)?.Count ?? 0} 条，以及健康分");
            items.Add($"你的回答和“判断不对”的标记：{(p["answers"] as JsonObject)?.Count ?? 0} 条回答、{(p["marks"] as JsonObject)?.Count ?? 0} 条标记（包括你写的说明）");
            items.Add("每一步的用时、程序版本、随机安装编号");
            if (((string?)p["errorLog"])?.Length > 0) items.Add("最近的错误记录（已打码）");
        }
        return new JsonObject
        {
            ["items"] = items, ["sizeKB"] = Math.Max(1, size / 1024),
            ["server"] = Cloud.FeedbackUrl != null ? "Iris 作者的服务器（阿里云中国香港）" : null,
            ["excluded"] = new JsonArray("用户名、电脑名、邮箱（换成占位符）", "代理服务器、DNS、hosts 里的地址", "API Key",
                                         "文档、照片、聊天记录、浏览记录、密码"),
        };
    }

    // ================================================================== 设置与其他
    public JsonObject SaveSettings(JsonNode p)
    {
        var s = AppData.LoadSettings();
        if (p["allowHosted"] is JsonValue ah && ah.TryGetValue<bool>(out var allow)) s.AllowHosted = allow;
        if (p["provider"] is { } pr)
        {
            if (pr.ToString() == "hosted" && (!s.AllowHosted || AppData.HostedBaseUrl == null))
                throw new InvalidOperationException(AppData.HostedBaseUrl == null ? "托管服务还没有开放" : "请先打开「允许发送到托管服务」");
            s.Provider = pr.ToString();
        }
        if (!s.AllowHosted && s.Provider == "hosted") s.Provider = "deepseek";
        if (p["baseUrl"] is { } b && b.ToString().Length > 0) s.BaseUrl = b.ToString();
        if (p["model"] is { } m && m.ToString().Length > 0) s.Model = m.ToString();
        if (p["mode"] is { } md && md.ToString() is "ai" or "offline") s.Mode = md.ToString();
        if (p["theme"]?.ToString() is { } th && AppData.Themes.Contains(th)) s.Theme = th;
        bool? B(string k) => p[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
        if (B("reduceMotion") is { } rm) s.ReduceMotion = rm;
        if (B("pro") is { } pro) s.Pro = pro;
        if (B("demoMode") is { } dm) s.DemoMode = dm;
        if (B("defaultScan") is { } ds) s.DefaultScan = ds;
        if (B("showLessons") is { } sl) s.ShowLessons = sl;
        if (B("showTips") is { } st) s.ShowTips = st;
        if (B("onboarded") is { } ob) s.Onboarded = ob;
        if (p["lessonIndex"] is JsonValue li && li.TryGetValue<int>(out var idx)) s.LessonIndex = Math.Clamp(idx, 0, 10);
        if (p["betaConsent"]?.ToString() is "yes" or "no") s.BetaConsent = p["betaConsent"]!.ToString();
        if (p["apiKey"]?.ToString() is { Length: > 0 } key)
        {
            if (key.Length < 20 || key.Any(char.IsWhiteSpace) || key.Contains('\\')) throw new InvalidOperationException("这看起来不像 API Key，请重新粘贴");
            AppData.SetApiKey(s, key);
            s.Mode = "ai";
        }
        AppData.SaveSettings(s);
        return AppData.SettingsForUi(s);
    }

    public async Task<JsonObject> TestConnection()
    {
        var s = AppData.LoadSettings();
        var key = AppData.GetApiKey(s) ?? throw new InvalidOperationException("还没有填 API Key");
        var sw = Stopwatch.StartNew();
        var probe = new JsonObject { ["test"] = "连接测试：请只回复 {\"ok\": true}" };
        await LlmClient.CallAsync("你是连接测试。只输出 JSON：{\"ok\": true}", probe, s.Model, s.BaseUrl, key, TimeSpan.FromSeconds(60));
        return new JsonObject { ["ok"] = true, ["message"] = $"连接成功（{s.Model}，用时 {sw.Elapsed.TotalSeconds:0.0} 秒）" };
    }

    /// <summary>清除所有数据：报告、操作记录、设置（含 API Key）、备份；管理员目录交给辅助进程删除。删除前确认。</summary>
    public async Task<JsonObject> ClearAllData()
    {
        var hasAdmin = Directory.Exists(Helper.ProtectedRoot);
        var items = new List<ChangeItem>
        {
            new($"删除 {AppData.Root}", "体检报告、操作记录、设置（包括保存的 API Key）、确认记录和备份", false, "删除后无法恢复；操作记录删除后，已经做过的处理也不能再一键撤销", false),
        };
        if (hasAdmin) items.Add(new($"删除 {Helper.ProtectedRoot}", "管理员操作的记录和备份", false, "删除后无法恢复", true));
        if (!Confirm("确认清除 Iris 的所有数据？", items)) return new JsonObject { ["cancelled"] = true };

        foreach (var sub in new[] { "reports", "journal", "runs", "backups" })
            try { Directory.Delete(Path.Combine(AppData.Root, sub), true); } catch { }
        foreach (var f in new[] { "settings.json", "known.json", "error.log" })
            try { File.Delete(Path.Combine(AppData.Root, f)); } catch { }
        var message = "已清除。界面缓存会在关闭程序后由 Windows 释放，可以手动删除 " + AppData.WebView;
        if (hasAdmin)
        {
            var dir = NewRunDir();
            try
            {
                var p = StartHelper("purge", dir, elevate: true);
                var done = p == null ? null : await WaitHelper(p, dir, _ => { }, CancellationToken.None);
                if (done?["ok"]?.GetValue<bool>() != true) message += $"。管理员数据没有删除（{done?["error"] ?? "没有同意管理员权限"}），可以手动删除 {Helper.ProtectedRoot}";
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        return new JsonObject { ["ok"] = true, ["message"] = message };
    }

    public JsonObject ExportReport(string id)
    {
        var dir = AppData.ReportDir(id) ?? throw new ArgumentException("报告编号不对");
        var result = AppData.ReadJson(Path.Combine(dir, "judge.json"))!.AsObject();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, $"pcdoctor-{id}-体检报告.md");
        File.WriteAllText(path, MarkdownRender.Render(result), new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return new JsonObject { ["path"] = path };
    }

    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}

static class JsonExt
{
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}
