using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pcdoctor.Ops;

/// <summary>处理方案里的一步：一个操作作用在一个具体对象上。</summary>
public sealed class PlanStep
{
    public string Key { get; set; } = "";          // 报告里结论的编号（flagIds 用 + 连接）加 #序号
    public string FindingKey { get; set; } = "";
    public string Op { get; set; } = "";
    public string OpName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Risk { get; set; }
    public string Change { get; set; } = "";      // 会改什么
    public string Cost { get; set; } = "";        // 代价
    public string Undo { get; set; } = "";        // 怎么撤销
    public bool Reversible { get; set; } = true;
    public bool NeedsAdmin { get; set; } = true;
    public JsonObject Target { get; set; } = new();
    public CacheOptions? CacheOptions { get; set; }
    public CacheChoice? CacheChoice { get; set; }
}

public sealed class CacheOptions
{
    public List<string> Categories { get; set; } = [];
    public List<int> Ages { get; set; } = [7, 30, 90, 180, 365];
}

public sealed class CacheChoice
{
    public List<string> Categories { get; set; } = [];
    public int? Age { get; set; }
}

public sealed class Plan
{
    public string PlanId { get; set; } = "";
    /// <summary>这批步骤来自哪份报告（写进操作记录，报告里据此显示"已处理"）。</summary>
    public string? ReportId { get; set; }
    public List<PlanStep> Steps { get; set; } = [];
    /// <summary>选了但不能自动处理的项，以及原因（例如触犯红线、属于专业模式）。</summary>
    public List<SkippedItem> Skipped { get; set; } = [];
    public bool NeedsAdmin => Steps.Any(s => s.NeedsAdmin);
}

public sealed record SkippedItem(string Key, string Title, string Reason);

public static class Status
{
    public const string Done = "done";              // 已完成，重新检测确认生效
    public const string RolledBack = "rolledBack";  // 重新检测发现没生效，已恢复原状
    public const string Failed = "failed";          // 没能执行（前置检查不通过、命令失败），没有改动
    public const string Undone = "undone";          // 用户点了撤销
    public const string Info = "info";              // 不改动系统的步骤（如发起 Defender 扫描）
}

/// <summary>执行或撤销的结果。</summary>
public sealed record Outcome(string Status, string Message)
{
    public static Outcome Ok(string m) => new(Ops.Status.Done, m);
    public static Outcome Fail(string m) => new(Ops.Status.Failed, m);
    public static Outcome Rolled(string m) => new(Ops.Status.RolledBack, m);
}

/// <summary>操作记录里的一条：包括改之前的样子（Backup），撤销时用。</summary>
public sealed class JournalEntry
{
    public string Id { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public string Op { get; set; } = "";
    public string OpName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Risk { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Reversible { get; set; }
    public bool NeedsAdmin { get; set; }
    public string? UndoHint { get; set; }
    public JsonObject Target { get; set; } = new();
    public JsonObject Backup { get; set; } = new();
    public DateTime? UndoneUtc { get; set; }
    public string? UndoMessage { get; set; }
    public string? ReportId { get; set; }
    /// <summary>对应报告里的哪条结论（flagIds 用 + 连接）。</summary>
    public string? FindingKey { get; set; }
    /// <summary>执行这一步的 Windows 账户（SID），只给本人看和撤销。</summary>
    public string? Owner { get; set; }
    /// <summary>同一次"执行"的所有步骤属于同一批，可以一键撤销整批。</summary>
    public string? BatchId { get; set; }
}

[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(JournalEntry))]
[JsonSerializable(typeof(List<JournalEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class OpsJson : JsonSerializerContext { }
