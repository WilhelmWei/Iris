using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

/// <summary>文件夹里的一项（来自目录枚举，不额外打开文件）。</summary>
/// <param name="Placeholder">云盘"仅在线"的文件：显示有大小，其实不占本地空间。</param>
/// <param name="Compressed">压缩或稀疏文件：实际占用要另外查。</param>
public sealed record SpaceEntry(string Name, bool IsDir, long Size, bool IsReparse, bool Placeholder = false, bool Compressed = false);

/// <summary>深度空间扫描读文件系统的接口。桌面程序里是真实实现，测试里是模拟实现。</summary>
public interface ISpaceSource
{
    IEnumerable<SpaceEntry> List(string dir);
    /// <summary>压缩 / 稀疏文件的实际占用。</summary>
    long Allocated(string path, long logical);
    /// <summary>文件的唯一编号和硬链接数（只在 Windows 目录里查：那里大量文件是硬链接，直接累加会多算一倍）。</summary>
    (ulong id, int links)? LinkInfo(string path);
}

public sealed class SpaceNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public SpaceNode? Parent;
    public Dictionary<string, SpaceNode>? Kids;
}

/// <summary>
/// 「C 盘空间去哪了」：遍历整个系统盘，按文件夹汇总实际占用。不进入联接点和符号链接；
/// 云盘仅在线的文件按 0 算；压缩文件按压缩后的大小算；Windows 目录里的硬链接只算一次。
/// 随时可以取快照（扫描中途也能给出"已经看过的部分"）。
/// </summary>
public sealed class SpaceScan(ISpaceSource src, string root, long usedBytes, string? dedupeUnder = null)
{
    public SpaceNode Root { get; } = new() { Name = root, Path = root };
    public long ScannedBytes { get; private set; }
    public long ScannedFiles { get; private set; }
    public bool Finished { get; private set; }
    public bool Stopped { get; private set; }
    public int Unreadable { get; private set; }
    readonly HashSet<ulong> seenLinks = [];
    readonly List<(string path, long bytes)> bigFiles = [];
    const int BigFileCount = 30;

    public void Run(Func<bool> cancelled, Action? tick = null)
    {
        var stack = new Stack<SpaceNode>();
        stack.Push(Root);
        var sinceTick = 0;
        while (stack.Count > 0)
        {
            if (cancelled()) { Stopped = true; return; }
            var node = stack.Pop();
            IEnumerable<SpaceEntry> entries;
            try { entries = src.List(node.Path).ToList(); }
            catch { Unreadable++; continue; }
            var dedupe = dedupeUnder != null && IsUnder(node.Path, dedupeUnder);
            foreach (var e in entries)
            {
                if (e.IsReparse && e.IsDir) continue;  // 联接点、符号链接文件夹不进入；OneDrive 的文件本身也带这个标记，照常计算
                var path = Join(node.Path, e.Name);
                if (e.IsDir)
                {
                    var kid = new SpaceNode { Name = e.Name, Path = path, Parent = node };
                    (node.Kids ??= new(StringComparer.OrdinalIgnoreCase))[e.Name] = kid;
                    stack.Push(kid);
                    continue;
                }
                long bytes = e.Placeholder ? 0 : e.Compressed ? src.Allocated(path, e.Size) : e.Size;
                if (dedupe && bytes > 0 && src.LinkInfo(path) is { links: > 1 } li && !seenLinks.Add(li.id)) bytes = 0;
                for (var n = node; n != null; n = n.Parent) { n.Bytes += bytes; n.Files++; }
                ScannedBytes += bytes; ScannedFiles++;
                if (bytes > 0) AddBigFile(path, bytes);
                if (++sinceTick >= 2000) { sinceTick = 0; tick?.Invoke(); }
            }
        }
        Finished = true;
        tick?.Invoke();
    }

    void AddBigFile(string path, long bytes)
    {
        if (bigFiles.Count >= BigFileCount && bytes <= bigFiles[^1].bytes) return;
        var i = bigFiles.FindIndex(x => x.bytes < bytes);
        bigFiles.Insert(i < 0 ? bigFiles.Count : i, (path, bytes));
        if (bigFiles.Count > BigFileCount) bigFiles.RemoveAt(bigFiles.Count - 1);
    }

    static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
    static bool IsUnder(string path, string dir) =>
        path.Equals(dir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 快照：每层只留够大的文件夹（至少占已用空间的 0.5% 或 200 MB，最多 12 个），最深 6 层；剩下的合成"其他"。
    /// </summary>
    public JsonObject Snapshot(Func<string, JsonObject?>? label = null)
    {
        var min = Math.Max(200L << 20, usedBytes / 200);
        JsonObject Node(SpaceNode n, int depth)
        {
            var o = new JsonObject { ["name"] = n.Name, ["path"] = n.Path, ["bytes"] = n.Bytes, ["files"] = n.Files };
            if (label?.Invoke(n.Path) is { } l) o["label"] = l;
            if (depth < 6 && n.Kids is { Count: > 0 })
            {
                var big = n.Kids.Values.Where(k => k.Bytes >= min).OrderByDescending(k => k.Bytes).Take(12).ToList();
                if (big.Count > 0)
                {
                    var kids = new JsonArray(big.Select(k => (JsonNode)Node(k, depth + 1)).ToArray());
                    var rest = n.Kids.Values.Except(big).Sum(k => k.Bytes);
                    var restCount = n.Kids.Count - big.Count;
                    if (rest > 0) kids.Add(new JsonObject { ["name"] = $"其他 {restCount} 个文件夹", ["other"] = true, ["bytes"] = rest });
                    o["children"] = kids;
                }
            }
            return o;
        }
        return new JsonObject
        {
            ["schema"] = "iris.space/1", ["root"] = Root.Path, ["usedBytes"] = usedBytes, ["scannedBytes"] = ScannedBytes,
            ["scannedFiles"] = ScannedFiles, ["finished"] = Finished, ["stopped"] = Stopped, ["unreadable"] = Unreadable,
            ["tree"] = Node(Root, 0),
            ["bigFiles"] = new JsonArray(bigFiles.Select(f => (JsonNode)new JsonObject { ["path"] = f.path, ["bytes"] = f.bytes }).ToArray()),
        };
    }
}

/// <summary>
/// 系统盘上常见位置的说明：是什么、能不能动、怎么处理。路径相对系统盘根目录，一整段的 * 匹配任意名字（例如用户名）。
/// kind：system 系统（不要动）、tool 交给 Windows 自带的工具、clean Iris 能清、move 可以搬到其他盘、yours 你的文件、apps 软件。
/// </summary>
public static class SpaceLabels
{
    public sealed record Label(string Pattern, string Name, string Kind, string Advice);

    public static readonly IReadOnlyList<Label> All =
    [
        new(@"Windows", "Windows 系统", "system", "系统本身，不要手动删除里面的文件"),
        new(@"Windows\WinSxS", "系统组件仓库", "tool", "Windows 更新留下的旧组件也在这里，只能交给 Windows 自带的清理，手动删会损坏系统"),
        new(@"Windows\Installer", "安装程序缓存", "system", "软件卸载、修复要用到这里的安装包，不能直接删"),
        new(@"Windows\SoftwareDistribution", "Windows 更新文件", "tool", "已下载的更新，交给 Windows 自带的清理"),
        new(@"Windows\Temp", "Windows 临时文件", "clean", "Iris 可以清理 7 天以前的"),
        new(@"Windows.old", "旧版 Windows", "tool", "升级前的旧系统，用 Windows 自带的「磁盘清理」删除；删除后不能退回旧版本"),
        new(@"$Windows.~BT", "Windows 升级临时文件", "tool", "升级完成后就没用了，用 Windows 自带的「磁盘清理」删除"),
        new(@"Program Files", "已安装的软件", "apps", "大的软件可以卸载，或者卸载后重装到其他盘"),
        new(@"Program Files (x86)", "已安装的软件（32 位）", "apps", "大的软件可以卸载，或者卸载后重装到其他盘"),
        new(@"ProgramData", "软件的公共数据", "apps", "软件为所有用户保存的数据，跟着软件走"),
        new(@"Users", "用户文件夹", "yours", "每个 Windows 账户的文件和设置"),
        new(@"Users\*\AppData", "软件的数据和缓存", "apps", "软件的设置、缓存和聊天记录等，跟着软件走；其中的缓存 Iris 可以清理"),
        new(@"Users\*\AppData\Local\Temp", "临时文件", "clean", "Iris 可以清理 7 天以前的"),
        new(@"Users\*\Documents\WeChat Files", "微信聊天文件", "move", "可以在微信的设置里清理，或者把存储位置改到其他盘"),
        new(@"Users\*\Documents\xwechat_files", "微信聊天文件", "move", "可以在微信的设置里清理，或者把存储位置改到其他盘"),
        new(@"Users\*\Documents\Tencent Files", "QQ 聊天文件", "move", "可以在 QQ 的设置里清理，或者把存储位置改到其他盘"),
        new(@"Users\*\Documents\WXWork", "企业微信文件", "move", "可以在企业微信的设置里清理，或者把存储位置改到其他盘"),
        new(@"Users\*\Downloads", "下载文件夹", "move", "多是用过的安装包和文件，可以整理；也可以在「处理」页把整个文件夹搬到其他盘"),
        new(@"Users\*\Desktop", "桌面", "yours", "你的文件"),
        new(@"Users\*\Documents", "文档", "move", "你的文件；可以在「处理」页把整个文件夹搬到其他盘"),
        new(@"Users\*\Pictures", "图片", "move", "你的文件；可以在「处理」页把整个文件夹搬到其他盘"),
        new(@"Users\*\Videos", "视频", "move", "你的文件；可以在「处理」页把整个文件夹搬到其他盘"),
        new(@"Users\*\Music", "音乐", "move", "你的文件；可以在「处理」页把整个文件夹搬到其他盘"),
        new(@"Users\*\OneDrive", "OneDrive", "yours", "同步到云端的文件；可以在 OneDrive 设置里改成「仅在线」来腾出空间"),
        new(@"pagefile.sys", "虚拟内存文件", "system", "Windows 自动管理，不要删；内存不够时它会变大"),
        new(@"swapfile.sys", "虚拟内存文件（应用）", "system", "Windows 自动管理，不要删"),
        new(@"hiberfil.sys", "休眠文件", "system", "关闭休眠后才会消失；笔记本合盖休眠要用到它，关之前想清楚"),
        new(@"$Recycle.Bin", "回收站", "yours", "你删掉的文件；要不要彻底删除由你决定，在桌面上右键回收站清空"),
    ];

    /// <summary>按相对系统盘根目录的路径找说明。</summary>
    public static Label? For(string relative)
    {
        var segs = relative.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in All)
        {
            var ps = l.Pattern.Split('\\');
            if (ps.Length != segs.Length) continue;
            if (ps.Zip(segs).All(p => p.First == "*" || p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase))) return l;
        }
        return null;
    }

    public static JsonObject? Json(string root, string path)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var l = For(path[root.Length..]);
        return l == null ? null : new JsonObject { ["name"] = l.Name, ["kind"] = l.Kind, ["advice"] = l.Advice, ["pattern"] = l.Pattern };
    }

    /// <summary>
    /// 发给作者的摘要（内测反馈）：只有已知位置的说明和大小、整体数字；用户自己建的文件夹名、文件名一律不发。
    /// </summary>
    public static JsonObject FeedbackSummary(JsonNode? space)
    {
        var known = new JsonArray();
        void Walk(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            if (o["label"] is JsonObject l)
                known.Add(new JsonObject { ["where"] = l["pattern"]?.DeepClone(), ["name"] = l["name"]?.DeepClone(), ["bytes"] = o["bytes"]?.DeepClone() });
            foreach (var k in o["children"] as JsonArray ?? []) Walk(k);
        }
        Walk(space?["tree"]);
        return new JsonObject
        {
            ["usedBytes"] = space?["usedBytes"]?.DeepClone(), ["scannedBytes"] = space?["scannedBytes"]?.DeepClone(),
            ["scannedFiles"] = space?["scannedFiles"]?.DeepClone(), ["finished"] = space?["finished"]?.DeepClone(),
            ["seconds"] = space?["seconds"]?.DeepClone(), ["known"] = known,
            ["bigFileCount"] = (space?["bigFiles"] as JsonArray)?.Count(f => (Operations.Int(f?["bytes"]) ?? 0) >= 1L << 30) ?? 0,
        };
    }
}
