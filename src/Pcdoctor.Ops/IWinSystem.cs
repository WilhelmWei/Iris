using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

public enum Hive { CurrentUser, LocalMachine }

/// <summary>注册表值。Kind：DWord / QWord / String / ExpandString / MultiString / Binary；
/// Data 按种类存成 JSON（数字、字符串、字符串数组、Base64），方便写进操作记录。</summary>
public sealed record RegValue(string Kind, JsonNode? Data)
{
    public static RegValue DWord(int v) => new("DWord", JsonValue.Create(v));
    public static RegValue Binary(byte[] v) => new("Binary", JsonValue.Create(Convert.ToBase64String(v)));
    public int? AsInt() => Kind is "DWord" or "QWord" ? (int?)Operations.Int(Data) : null;
    public byte[]? AsBytes() => Kind == "Binary" && Data is JsonValue j && j.TryGetValue<string>(out var s) ? Convert.FromBase64String(s) : null;
    public string[] AsStrings() => Data switch
    {
        JsonArray a => a.Select(x => x?.ToString() ?? "").ToArray(),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };
    public JsonObject ToJson() => new() { ["kind"] = Kind, ["data"] = Data?.DeepClone() };
    public static RegValue? FromJson(JsonNode? n) => n is JsonObject o ? new RegValue(o["kind"]!.ToString(), o["data"]?.DeepClone()) : null;
}

/// <summary>一棵注册表子树（删除服务登记前整棵备份，撤销时原样写回）。</summary>
public sealed class RegTree
{
    public Dictionary<string, RegValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegTree> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject ToJson()
    {
        var vals = new JsonObject();
        foreach (var (k, v) in Values) vals[k] = v.ToJson();
        var keys = new JsonObject();
        foreach (var (k, t) in Keys) keys[k] = t.ToJson();
        return new JsonObject { ["values"] = vals, ["keys"] = keys };
    }

    public static RegTree FromJson(JsonNode node)
    {
        var t = new RegTree();
        foreach (var (k, v) in node["values"]!.AsObject()) t.Values[k] = RegValue.FromJson(v)!;
        foreach (var (k, sub) in node["keys"]!.AsObject()) t.Keys[k] = FromJson(sub!);
        return t;
    }
}

public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc);

/// <summary>操作库对 Windows 的全部读写。桌面程序里是真实实现，测试里是模拟实现。
/// 原则：只用不随系统语言变化的接口（注册表、退出码、XML），不解析命令行工具的文字输出。</summary>
public interface IWinSystem
{
    DateTime UtcNow { get; }
    string WindowsDir { get; }
    string? Env(string name);
    /// <summary>展开 %ProgramFiles% 这类环境变量（注册表里的 REG_EXPAND_SZ 路径）。</summary>
    string Expand(string text);
    void Sleep(int milliseconds);

    // 注册表（key 不带 HKCU:\ 前缀，例如 Software\Microsoft\Windows\CurrentVersion\Run）
    RegValue? ReadValue(Hive hive, string key, string name);
    void WriteValue(Hive hive, string key, string name, RegValue value);
    void DeleteValue(Hive hive, string key, string name);
    bool KeyExists(Hive hive, string key);
    IReadOnlyList<string> SubKeys(Hive hive, string key);
    RegTree? ExportTree(Hive hive, string key);
    void ImportTree(Hive hive, string key, RegTree tree);

    /// <summary>运行程序并等待，返回退出码（超时返回 -1）。不读取输出：输出会随系统语言变化。</summary>
    int Run(string exe, string arguments, int timeoutSec = 120);

    /// <summary>服务或驱动的运行状态：Running / Stopped / …；不存在返回 null。</summary>
    string? ServiceState(string name);

    // 计划任务（fullName 形如 \Folder\Name）
    string? ExportTaskXml(string fullName);
    bool SetTaskEnabled(string fullName, bool enabled);
    bool DeleteTask(string fullName);
    bool RegisterTaskXml(string fullName, string xml);

    // 文件
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<FileEntry> EnumerateFiles(string root);
    /// <summary>批量移到回收站（红线 6：不永久删除用户文件），返回确实移走了的文件。
    /// 回收站关闭或文件太大放不进回收站时，系统会先提示，不会悄悄永久删除。</summary>
    IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths);
}
