using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

/// <summary>执行处理方案里的步骤，每一步的结果（含改之前的备份）写进操作记录。</summary>
public static class Executor
{
    public static JournalEntry RunStep(PlanStep step, OpContext ctx, string? reportId = null, string? batchId = null)
    {
        var entry = new JournalEntry
        {
            Id = ctx.Sys.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6],
            TimeUtc = ctx.Sys.UtcNow, Op = step.Op, OpName = step.OpName, Title = step.Title, Risk = step.Risk,
            Reversible = step.Reversible, NeedsAdmin = step.NeedsAdmin, Target = (JsonObject)step.Target.DeepClone(), ReportId = reportId, BatchId = batchId,
            FindingKey = string.IsNullOrEmpty(step.FindingKey) ? step.Key.Split('#')[0] : step.FindingKey,
            UndoHint = step.Reversible ? null : step.Undo,
        };
        var op = Operations.Get(step.Op);
        Outcome o;
        if (op == null) o = Outcome.Fail("不认识的操作，没有执行");
        else
        {
            try { o = op.Apply(step, ctx, entry.Backup); }
            catch (Exception ex) { o = Outcome.Fail("执行前检查时出错：" + ex.Message); }
        }
        entry.Status = step.Op == "defender.scan" && o.Status == Status.Done ? Status.Info : o.Status;
        entry.Message = o.Message;
        if (o.Status != Status.Done) entry.Reversible = false;  // 没改动或已回滚的，不需要撤销
        return entry;
    }

    public static Outcome Undo(JournalEntry entry, OpContext ctx)
    {
        if (entry.Status != Status.Done || !entry.Reversible) return Outcome.Fail("这一条不能撤销");
        var op = Operations.Get(entry.Op);
        if (op == null) return Outcome.Fail("不认识的操作");
        Outcome o;
        try { o = op.Undo(entry, ctx); }
        catch (Exception ex) { o = Outcome.Fail("撤销时出错：" + ex.Message); }
        if (o.Status == Status.Done)
        {
            entry.Status = Status.Undone;
            entry.UndoneUtc = ctx.Sys.UtcNow;
        }
        entry.UndoMessage = o.Message;
        return o;
    }
}

/// <summary>操作记录：每条一个 JSON 文件，放在 %LOCALAPPDATA%\pcdoctor\journal。</summary>
public sealed class JournalStore(string dir)
{
    public string Dir { get; } = dir;

    public void Save(JournalEntry e)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, e.Id + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(e, OpsJson.Default.JournalEntry));
        File.Move(tmp, path, overwrite: true);
    }

    public JournalEntry? Get(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
        var path = Path.Combine(Dir, id + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), OpsJson.Default.JournalEntry) : null;
    }

    public List<JournalEntry> All()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<JournalEntry>();
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try { if (JsonSerializer.Deserialize(File.ReadAllText(f), OpsJson.Default.JournalEntry) is { } e) list.Add(e); }
            catch { /* 损坏的记录跳过 */ }
        }
        return list.OrderByDescending(e => e.TimeUtc).ToList();
    }
}

/// <summary>
/// 清理旧的操作备份（设置 → 存储空间）。清理后这一步就不能再一键撤销，所以只清：
///   · 已经撤销的、没生效已恢复的、没执行的（备份已经用不上）；
///   · 早于 cutoff 的已完成步骤（会标成"备份已清理，不能再撤销"）。
/// 最近一批（最后一次执行）永远保留。驱动包导出的整个文件夹也一起删除。
/// </summary>
public static class BackupPruner
{
    public sealed record Result(int Entries, long Bytes);

    /// <summary>没生效 / 已撤销的步骤，或早于截止时间的已生效步骤。搬到其他盘的记录只有几百字节，清掉就再也搬不回来了，一直保留。</summary>
    public static bool Eligible(JournalEntry e, DateTime cutoffUtc) =>
        e.Status is Status.Undone or Status.RolledBack or Status.Failed or Status.Info
        || (e.Status == Status.Done && e.TimeUtc < cutoffUtc && e.Op != "folder.move");

    /// <summary>有没有需要清理的（不含最近一批）。用来决定要不要为了清理去请求管理员权限。</summary>
    public static bool AnyToPrune(IReadOnlyList<JournalEntry> newestFirst, DateTime cutoffUtc)
    {
        var keep = newestFirst.FirstOrDefault()?.BatchId;
        return newestFirst.Any(e => (e.BatchId == null || e.BatchId != keep) && e.Backup.Count > 0 && Eligible(e, cutoffUtc));
    }

    public static Result Prune(JournalStore store, string workDir, DateTime cutoffUtc, DateTime nowUtc, Func<JournalEntry, bool>? only = null)
    {
        var all = store.All().Where(e => only == null || only(e)).ToList();
        var keepBatch = all.FirstOrDefault()?.BatchId;
        int n = 0; long bytes = 0;
        foreach (var e in all)
        {
            if (e.BatchId != null && e.BatchId == keepBatch) continue;
            if (!Eligible(e, cutoffUtc)) continue;
            var export = DriverExportDir(workDir, e);
            if (e.Backup.Count == 0 && export == null) continue;
            bytes += System.Text.Encoding.UTF8.GetByteCount(e.Backup.ToJsonString());
            if (export != null) { bytes += DirSize(export); Directory.Delete(export, true); }
            e.Backup = new JsonObject();
            if (e.Status == Status.Done)
            {
                e.Reversible = false;
                e.UndoHint = $"备份已于 {nowUtc.ToLocalTime():yyyy-MM-dd} 清理，不能再一键撤销";
            }
            store.Save(e);
            n++;
        }
        return new(n, bytes);
    }

    /// <summary>删除旧版驱动包时导出的备份文件夹（WorkDir\driver-&lt;oemNN.inf&gt;）。名字来自记录，只认 oem数字.inf，防止路径穿越。</summary>
    static string? DriverExportDir(string workDir, JournalEntry e)
    {
        if (e.Op != "driver.delete_old_version") return null;
        var name = e.Target["publishedName"]?.ToString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^oem\d{1,5}\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
        var dir = Path.Combine(workDir, "driver-" + name);
        return Directory.Exists(dir) ? dir : null;
    }

    public static long DirSize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } }) : 0;
}

/// <summary>报告里每条结论的处理状态，来自操作记录。同一条处理过多次时看最近一批：
/// 那一批里有生效的 → done；都撤销了 → undone；否则（没生效已恢复、没执行）→ failed。</summary>
public static class HandledStatus
{
    public sealed record Item(string Status, JournalEntry Latest);

    public static Dictionary<string, Item> Of(IEnumerable<JournalEntry> entries, string reportId)
    {
        var o = new Dictionary<string, Item>();
        foreach (var g in entries.Where(e => e.ReportId == reportId && !string.IsNullOrEmpty(e.FindingKey)).GroupBy(e => e.FindingKey!))
        {
            var latest = g.OrderByDescending(e => e.TimeUtc).First();
            var batch = g.Where(e => e.BatchId == latest.BatchId).ToList();
            var status = batch.Any(e => e.Status is Status.Done or Status.Info) ? "done"
                : batch.Any(e => e.Status == Status.Undone) ? "undone" : "failed";
            o[g.Key] = new Item(status, latest);
        }
        return o;
    }
}

/// <summary>
/// 「处理」记录发给作者（内测反馈）的部分：做了哪种操作、对应哪条结论、结果怎样。
/// 文字由调用方打码（hide）；不带操作目标（路径、注册表位置）、改之前的备份和执行者账户。
/// </summary>
public static class JournalFeedback
{
    public static JsonObject View(JournalEntry e, Func<string, string> hide) => new()
    {
        ["id"] = e.Id, ["time"] = e.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["op"] = e.Op, ["opName"] = e.OpName,
        ["title"] = hide(Cut(e.Title, 300)), ["risk"] = e.Risk, ["status"] = e.Status, ["message"] = hide(Cut(e.Message, 600)),
        ["reversible"] = e.Reversible, ["needsAdmin"] = e.NeedsAdmin, ["undoneTime"] = e.UndoneUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["undoMessage"] = e.UndoMessage == null ? null : hide(Cut(e.UndoMessage, 600)),
        ["reportId"] = e.ReportId, ["findingKey"] = e.FindingKey, ["batchId"] = e.BatchId,
    };

    static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];
}
