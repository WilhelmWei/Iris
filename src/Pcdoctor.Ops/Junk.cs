using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pcdoctor.Ops;

/// <summary>清理目录表 kb/junk.json 里的一类（见文件开头的说明）。</summary>
public sealed class JunkCategory
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>A = 程序会自动重建的缓存，Iris 直接删除；B = 系统组件，只测量，交给 Windows 自带的清理。</summary>
    public string Tier { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    /// <summary>文件名通配（* 和 ?），没有就是全部文件。</summary>
    public List<string>? Include { get; set; }
    public bool Recurse { get; set; } = true;
    public int MinAgeDays { get; set; }
    /// <summary>按修改时间保留最新的几个文件（例如最近一次蓝屏的转储）。</summary>
    public int KeepNewest { get; set; }
    public bool Admin { get; set; }
    /// <summary>这些进程在运行时跳过这一类（浏览器开着时缓存文件被占用，也可能正在写）。</summary>
    public List<string>? CloseFirst { get; set; }
    public string Why { get; set; } = "";
    public string Cost { get; set; } = "";
}

public sealed class JunkNote
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Why { get; set; } = "";
}

public sealed class JunkCatalog
{
    public string Schema { get; set; } = "";
    public List<JunkCategory> Categories { get; set; } = [];
    public List<JunkNote> NotCleaned { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(JunkCatalog))]
public partial class JunkJson : JsonSerializerContext { }

/// <summary>
/// 清理目录表：采集脚本按它测量，清理操作按它删除。表编进程序（嵌入资源），要删的目录只能来自这里，
/// 处理方案（放在普通用户可写的位置）只能说"清哪几类"，说不了"清哪个目录"。
/// </summary>
public static class Junk
{
    public const string ResourceName = "junk.json";

    /// <summary>路径模板里允许的变量。%STEAM% 取自注册表里 Steam 的安装位置。</summary>
    public static readonly string[] Vars = ["TEMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "PROGRAMDATA", "WINDIR", "SYSTEMDRIVE", "STEAM"];

    static JunkCatalog? cached;
    public static JunkCatalog Catalog => cached ??= Parse(CatalogBytes());

    public static byte[] CatalogBytes()
    {
        using var s = typeof(Junk).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new FileNotFoundException($"找不到嵌入资源 {ResourceName}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static JunkCatalog Parse(byte[] json) => JsonSerializer.Deserialize(json, JunkJson.Default.JunkCatalog) ?? new JunkCatalog();

    public static JunkCategory? Get(string id) => Catalog.Categories.FirstOrDefault(c => c.Id == id);

    /// <summary>变量的实际值；取不到时返回 null（这条路径就跳过）。</summary>
    public static string? VarValue(IWinSystem sys, string name) => name.ToUpperInvariant() switch
    {
        "WINDIR" => sys.WindowsDir,
        "SYSTEMDRIVE" => sys.Env("SystemDrive") ?? (sys.WindowsDir.Length >= 2 ? sys.WindowsDir[..2] : null),
        "PROGRAMDATA" => sys.Env("ProgramData"),
        "STEAM" => sys.ReadValue(Hive.CurrentUser, @"Software\Valve\Steam", "SteamPath")?.AsStrings().FirstOrDefault()?.Replace('/', '\\'),
        var n when Vars.Contains(n) => sys.Env(n),
        _ => null,
    };

    /// <summary>
    /// 把一条路径模板展开成实际存在的文件夹。一整段的 * 匹配任意一个子文件夹（不进入联接点和符号链接）；
    /// 其余各段如果是联接点或符号链接也跳过，防止从缓存目录"跳"到别处。
    /// </summary>
    public static List<string> Expand(IWinSystem sys, string template)
    {
        var m = System.Text.RegularExpressions.Regex.Match(template, @"^%(\w+)%(.*)$");
        if (!m.Success) return [];
        var root = VarValue(sys, m.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(root)) return [];
        var current = new List<string> { root.TrimEnd('\\') };
        foreach (var seg in m.Groups[2].Value.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg is "." or "..") return [];
            var next = new List<string>();
            foreach (var c in current)
            {
                if (seg == "*") next.AddRange(sys.ListDirectories(c));
                else
                {
                    var p = c + "\\" + seg;
                    if (sys.DirectoryExists(p) && !sys.IsReparsePoint(p)) next.Add(p);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Where(sys.DirectoryExists).ToList();
    }

    public static IEnumerable<string> Roots(IWinSystem sys, JunkCategory c) =>
        c.Paths.SelectMany(p => Expand(sys, p)).Distinct(StringComparer.OrdinalIgnoreCase);

    static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    public static bool NameMatches(string fileName, IReadOnlyList<string>? include)
    {
        if (include == null || include.Count == 0) return true;
        foreach (var pat in include)
        {
            var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>这一类里可以清理的文件：文件名对得上、超过最短天数，再按修改时间留下最新的几个。</summary>
    public static List<FileEntry> Eligible(IWinSystem sys, JunkCategory c, DateTime nowUtc, int extraMinAgeDays = 0, int maxFiles = int.MaxValue)
    {
        var days = Math.Max(c.MinAgeDays, extraMinAgeDays);
        var cutoff = nowUtc.AddDays(-days);
        var list = new List<FileEntry>();
        foreach (var root in Roots(sys, c))
            foreach (var f in sys.EnumerateFiles(root, c.Recurse))
            {
                if (!NameMatches(FileName(f.Path), c.Include)) continue;
                if (days > 0 && f.LastWriteUtc >= cutoff) continue;
                list.Add(f);
                if (c.KeepNewest == 0 && list.Count >= maxFiles) return list;
            }
        if (c.KeepNewest > 0) list = list.OrderByDescending(f => f.LastWriteUtc).Skip(c.KeepNewest).Take(maxFiles).ToList();
        return list;
    }

    /// <summary>正在运行、导致这一类要跳过的进程（例如开着的浏览器）。</summary>
    public static List<string> Blocking(IWinSystem sys, JunkCategory c) => (c.CloseFirst ?? []).Where(sys.IsProcessRunning).ToList();
}
