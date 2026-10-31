using System.Text.Json.Nodes;

namespace Pcdoctor.Ops;

/// <summary>可以搬到其他盘的个人文件夹（Windows 的"已知文件夹"）。Guid 不带大括号。
/// 桌面不搬：很多软件和快捷方式按桌面路径工作，桌面上的东西通常也不大。</summary>
public sealed record UserFolder(string Id, string Name, string Guid, string Leaf);

/// <summary>体检后「处理」页里列出的一个可搬文件夹。</summary>
public sealed record MoveCandidate(UserFolder Folder, string Path, long Bytes, int Files, bool Partial,
                                   string? Problem, string? MovedTo, IReadOnlyList<(string Name, long Bytes)> Kept);

/// <summary>
/// 把文档、下载、图片、视频、音乐搬到其他盘，和文件夹属性里「位置 → 移动」做的是同一件事：
/// 先把文件复制到新位置并逐个核对大小，再把系统里这个文件夹的位置改过去，最后删掉 C 盘上已经复制好、而且没被改过的原件。
/// 任何一步出问题都搬回原样；撤销就是反过来再搬一次。不永久删除用户文件（红线 6）：删掉的只是已经核对过的原件。
/// </summary>
public static class FolderMove
{
    public static readonly UserFolder[] Folders =
    [
        new("documents", "文档", "FDD39AD0-238F-46AF-ADB4-6C85480369C7", "Documents"),
        new("downloads", "下载", "374DE290-123F-4565-9164-39C4925E467B", "Downloads"),
        new("pictures", "图片", "33E28130-4E1E-4676-835A-98395C3BC3BB", "Pictures"),
        new("videos", "视频", "18989B1D-99B5-455B-841C-AB7C74E4DDFC", "Videos"),
        new("music", "音乐", "4BD8D571-6D19-48D3-BE97-422220080E43", "Music"),
    ];

    /// <summary>"D:" → "D 盘"。</summary>
    public static string DriveName(string root) => root.TrimEnd('\\', ':').ToUpperInvariant() + " 盘";

    public static UserFolder? Get(string id) => Folders.FirstOrDefault(f => f.Id == id);

    /// <summary>留在原处的子文件夹：这些软件记着完整路径，跟着搬会找不到（聊天记录、邮件数据文件）。
    /// 聊天文件请用软件自带的设置改存储位置。</summary>
    public static readonly string[] KeepInPlace = ["WeChat Files", "xwechat_files", "Tencent Files", "WXWork", "Outlook Files", "Outlook 文件"];

    /// <summary>留在原处的子文件夹在界面上的叫法。</summary>
    public static string KeptLabel(string folder) => folder switch
    {
        "WeChat Files" or "xwechat_files" => "微信", "Tencent Files" => "QQ", "WXWork" => "企业微信", _ => "Outlook",
    };

    /// <summary>拔掉或卸载后文件夹就打不开的盘：U 盘、移动硬盘、存储卡、挂载的虚拟磁盘。</summary>
    static readonly HashSet<string> BadBus = new(StringComparer.OrdinalIgnoreCase) { "USB", "SD", "MMC", "1394", "Virtual", "FileBackedVirtual" };

    /// <summary>空间要留出的余量：至少 2 GB，大文件夹再多留 5%。</summary>
    public static long Margin(long bytes) => Math.Max(2L << 30, bytes / 20);

    public static string SystemDrive(IWinSystem sys) => (sys.Env("SystemDrive") ?? sys.WindowsDir[..2]).TrimEnd('\\').ToUpperInvariant();

    /// <summary>能不能搬到这个盘：本机内置硬盘（不是 U 盘、移动硬盘、存储卡、网络盘），NTFS 或 ReFS，不是系统盘。返回 null 表示可以。</summary>
    public static string? DriveProblem(IWinSystem sys, DriveFact d)
    {
        if (d.Root.Equals(SystemDrive(sys), StringComparison.OrdinalIgnoreCase)) return "这是系统盘";
        if (d.Kind != "Fixed") return "不是这台电脑的内置硬盘";
        if (BadBus.Contains(d.Bus)) return "这是 U 盘、移动硬盘或存储卡，拔掉后文件夹就打不开了";
        if (d.Format is not ("NTFS" or "ReFS")) return $"这个盘是 {d.Format} 格式，不适合放个人文件夹";
        return null;
    }

    /// <summary>可以搬过去的盘，剩余空间大的在前。</summary>
    public static List<DriveFact> Targets(IWinSystem sys) =>
        sys.Drives().Where(d => DriveProblem(sys, d) == null).OrderByDescending(d => d.FreeBytes).ToList();

    public static string Profile(IWinSystem sys) => (sys.Env("USERPROFILE") ?? "").TrimEnd('\\');
    public static string DefaultPath(IWinSystem sys, UserFolder f) => Profile(sys) + "\\" + f.Leaf;

    /// <summary>搬到的位置：D:\用户文件夹名\Documents。</summary>
    public static string TargetPath(IWinSystem sys, UserFolder f, string drive)
    {
        var p = Profile(sys);
        return drive.TrimEnd('\\') + "\\" + p[(p.LastIndexOf('\\') + 1)..] + "\\" + f.Leaf;
    }

    public static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool Kept(string root, string path) =>
        KeepInPlace.Any(k => path.StartsWith(root + "\\" + k + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>要搬的文件：含隐藏和系统文件（desktop.ini），不含联接点、符号链接和留在原处的子文件夹。</summary>
    public static List<FileEntry> FilesToMove(IWinSystem sys, string root) =>
        sys.EnumerateFiles(root, true, includeSystem: true).Where(f => !Kept(root, f.Path)).ToList();

    /// <summary>要在新位置重建的文件夹（包括空文件夹）。</summary>
    public static List<string> DirectoriesToMove(IWinSystem sys, string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>(sys.ListDirectories(root).Where(d => !Kept(root, d + "\\")));
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            list.Add(d);
            foreach (var sub in sys.ListDirectories(d)) stack.Push(sub);
        }
        return list;
    }

    /// <summary>清单里"搬到其他盘"的一步（处理清单里的编号是 move:文件夹）。没有可去的盘、不能搬时返回 null。</summary>
    public static PlanStep? Step(IWinSystem sys, MoveCandidate c, IReadOnlyList<DriveFact> targets)
    {
        if (c.MovedTo != null || c.Problem != null || targets.Count == 0) return null;
        var info = Catalog.Ops["folder.move"];
        var drive = targets[0];
        return new PlanStep
        {
            Key = $"move:{c.Folder.Id}#0", FindingKey = $"move:{c.Folder.Id}", Op = "folder.move", OpName = info.Name, Risk = info.Risk,
            Title = c.Folder.Name, Change = info.Change + (c.Folder.Id == "documents" ? "。微信、QQ、Outlook 的文件夹留在原处" : ""),
            Cost = info.Cost, Undo = info.Undo, Reversible = true, NeedsAdmin = false, SizeMB = Math.Round(c.Bytes / 1048576.0, 1),
            Target = new JsonObject { ["folder"] = c.Folder.Id, ["drive"] = drive.Root },
            Move = new MoveChoice
            {
                Drive = drive.Root,
                Drives = targets.Select(d => new DriveOption(d.Root, DriveName(d.Root), d.Label, Math.Round(d.FreeBytes / (double)(1L << 30), 1))).ToList(),
            },
        };
    }

    /// <summary>列出可以搬的文件夹和大小（每个文件夹最多量 capSeconds 秒，量不完标 Partial）。</summary>
    public static List<MoveCandidate> Candidates(IWinSystem sys, double capSeconds = 15)
    {
        var list = new List<MoveCandidate>();
        var profile = Profile(sys);
        foreach (var f in Folders)
        {
            var cur = sys.GetKnownFolder(f.Guid);
            if (cur == null) continue;
            var def = DefaultPath(sys, f);
            if (!Same(cur, def))
            {
                // 已经不在默认位置：搬过了，或者在 OneDrive 里，或者用户自己改过。只显示在哪，不再动它
                list.Add(new MoveCandidate(f, cur, 0, 0, false, null, cur, []));
                continue;
            }
            if (!sys.DirectoryExists(cur)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytes = 0; int files = 0; bool partial = false;
            var kept = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in sys.EnumerateFiles(cur, true, includeSystem: true))
            {
                var k = KeepInPlace.FirstOrDefault(k => e.Path.StartsWith(cur + "\\" + k + "\\", StringComparison.OrdinalIgnoreCase));
                if (k != null) kept[k] = kept.GetValueOrDefault(k) + e.Size;
                else { bytes += e.Size; files++; }
                if (sw.Elapsed.TotalSeconds > capSeconds) { partial = true; break; }
            }
            var problem = profile.Length < 3 || !cur.StartsWith(SystemDrive(sys) + "\\", StringComparison.OrdinalIgnoreCase)
                ? "它不在系统盘上" : null;
            list.Add(new MoveCandidate(f, cur, bytes, files, partial, problem, null, kept.Select(kv => (kv.Key, kv.Value)).ToList()));
        }
        return list;
    }
}

/// <summary>搬个人文件夹到其他盘（见 <see cref="FolderMove"/>）。在用户自己的权限里执行：改的是这个用户的文件夹位置。</summary>
public sealed class FolderMoveOp : Operation
{
    public override string Id => "folder.move";
    /// <summary>复制失败的文件超过这么多（或 1%）就停下，全部搬回：多半是盘出了问题，不是个别文件被占用。</summary>
    const int MaxFailures = 20;

    static UserFolder F(PlanStep s) => FolderMove.Get(T(s, "folder"))!;  // ValidateTarget 已经检查过

    protected override string? Precheck(PlanStep s, OpContext c)
    {
        var sys = c.Sys;
        var f = F(s);
        if (FolderMove.Profile(sys).Length < 3) return "读不到用户文件夹的位置";
        var cur = sys.GetKnownFolder(f.Guid);
        if (cur == null) return $"读不到「{f.Name}」的位置";
        if (!FolderMove.Same(cur, FolderMove.DefaultPath(sys, f))) return $"「{f.Name}」已经不在默认位置，Iris 不再搬动它";
        if (!sys.DirectoryExists(cur) || sys.IsReparsePoint(cur)) return $"「{f.Name}」文件夹不存在或是一个链接";
        if (!cur.StartsWith(FolderMove.SystemDrive(sys) + "\\", StringComparison.OrdinalIgnoreCase)) return $"「{f.Name}」不在系统盘上，不需要搬";
        var drive = sys.Drives().FirstOrDefault(d => d.Root.Equals(T(s, "drive"), StringComparison.OrdinalIgnoreCase));
        if (drive == null) return $"找不到 {FolderMove.DriveName(T(s, "drive"))}";
        var bad = FolderMove.DriveProblem(sys, drive);
        if (bad != null) return $"不能搬到 {FolderMove.DriveName(drive.Root)}：{bad}";
        var target = FolderMove.TargetPath(sys, f, drive.Root);
        var parent = target[..target.LastIndexOf('\\')];
        if ((sys.DirectoryExists(parent) && sys.IsReparsePoint(parent)) || (sys.DirectoryExists(target) && sys.IsReparsePoint(target)))
            return $"{FolderMove.DriveName(drive.Root)}上的目标位置是一个链接，没有搬";
        if (sys.DirectoryExists(target) && sys.EnumerateFiles(target, true, includeSystem: true).Any())
            return $"{FolderMove.DriveName(drive.Root)}上已经有一个不是空的「{f.Leaf}」文件夹，为了不和里面的文件混在一起，没有搬";
        var bytes = FolderMove.FilesToMove(sys, cur).Sum(x => x.Size);
        if (drive.FreeBytes < bytes + FolderMove.Margin(bytes))
            return $"{FolderMove.DriveName(drive.Root)}空间不够：要搬 {CacheClean.Size(bytes)}，那里只剩 {CacheClean.Size(drive.FreeBytes)}";
        return null;
    }

    protected override void Backup(PlanStep s, OpContext c, JsonObject b)
    {
        var f = F(s);
        b["original"] = c.Sys.GetKnownFolder(f.Guid);
        b["target"] = FolderMove.TargetPath(c.Sys, f, T(s, "drive"));
    }

    protected override void Change(PlanStep s, OpContext c, JsonObject b)
    {
        var sys = c.Sys;
        var f = F(s);
        string src = b["original"]!.ToString(), dst = b["target"]!.ToString();
        var files = FolderMove.FilesToMove(sys, src);
        long total = files.Sum(x => x.Size), done = 0;
        var failed = new List<string>();
        var copied = new List<FileEntry>();  // 复制好的原件（记下复制时的大小和修改时间）
        sys.CreateDirectory(dst);
        foreach (var d in FolderMove.DirectoriesToMove(sys, src)) sys.CreateDirectory(dst + d[src.Length..]);
        var last = DateTime.MinValue;
        foreach (var file in files)
        {
            var to = dst + file.Path[src.Length..];
            if (sys.CopyFile(file.Path, to) && sys.GetFile(to)?.Size == file.Size) copied.Add(file);
            else failed.Add(file.Path);
            if (failed.Count > Math.Max(MaxFailures, files.Count / 100))
                throw new InvalidOperationException($"有 {failed.Count} 个文件复制失败（例如「{Leaf(failed[0])}」），已停止");
            done += file.Size;
            if ((sys.UtcNow - last).TotalSeconds >= 1)
            {
                last = sys.UtcNow;
                c.Progress?.Invoke($"正在复制「{f.Name}」：{CacheClean.Size(done)} / {CacheClean.Size(total)}");
            }
        }

        c.Progress?.Invoke($"正在把「{f.Name}」的位置改到新文件夹…");
        if (!sys.SetKnownFolder(f.Guid, dst) || !FolderMove.Same(sys.GetKnownFolder(f.Guid), dst))
            throw new InvalidOperationException("系统没有接受新的位置");
        b["switched"] = true;

        // 删掉原件：只删复制好、而且复制之后没被改过的。其余的原件留在原处，新位置上对应的副本删掉，每个文件只留一份
        c.Progress?.Invoke("正在删除 C 盘上已经复制好的原件…");
        var toDelete = new List<FileEntry>();
        var stale = new List<string>();
        foreach (var file in copied)
        {
            var now = sys.GetFile(file.Path);
            if (now == null) continue;
            if (now.Size == file.Size && now.LastWriteUtc == file.LastWriteUtc) toDelete.Add(file);
            else stale.Add(file.Path);
        }
        long moved = 0; int movedCount = 0;
        var left = failed.ToList();
        foreach (var chunk in toDelete.Chunk(500))
        {
            var ok = new HashSet<string>(sys.DeleteFiles(chunk.Select(x => x.Path).ToList()), StringComparer.OrdinalIgnoreCase);
            foreach (var x in chunk)
                if (ok.Contains(x.Path)) { moved += x.Size; movedCount++; } else stale.Add(x.Path);
        }
        foreach (var chunk in stale.Chunk(500)) sys.DeleteFiles(chunk.Select(p => dst + p[src.Length..]).ToList());
        left.AddRange(stale);
        sys.DeleteEmptyDirectories(src);
        b["bytes"] = moved; b["files"] = movedCount;
        b["left"] = left.Count;
        // 文件名只记在本机的操作记录里（结果消息会进反馈包，不写文件名）
        b["leftNames"] = new JsonArray(left.Take(5).Select(p => (JsonNode?)Leaf(p)).ToArray());
    }

    static string Leaf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    protected override string? Verify(PlanStep s, OpContext c, JsonObject b) =>
        FolderMove.Same(c.Sys.GetKnownFolder(F(s).Guid), b["target"]?.ToString()) && c.Sys.DirectoryExists(b["target"]!.ToString())
            ? null : "系统里的位置不是新文件夹";

    /// <summary>搬回原处：恢复系统里的位置，把新位置里的文件（包括搬过去之后新放进去的）复制回来、核对后删掉新位置上的。
    /// 原处已经有同名文件时：一模一样的只删新位置上的副本；不一样的两边都留着，不覆盖任何一边。</summary>
    protected override void Rollback(PlanStep s, OpContext c, JsonObject b)
    {
        var sys = c.Sys;
        var f = F(s);
        var src = b["original"]?.ToString();
        var dst = b["target"]?.ToString();
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst)) return;
        if (!FolderMove.Same(sys.GetKnownFolder(f.Guid), src)) sys.SetKnownFolder(f.Guid, src);
        if (!sys.DirectoryExists(dst)) return;
        sys.CreateDirectory(src);
        foreach (var d in FolderMove.DirectoriesToMove(sys, dst)) sys.CreateDirectory(src + d[dst.Length..]);
        var files = sys.EnumerateFiles(dst, true, includeSystem: true).ToList();
        long total = files.Sum(x => x.Size), done = 0;
        var toDelete = new List<string>();
        int left = 0;
        var last = DateTime.MinValue;
        foreach (var file in files)
        {
            var to = src + file.Path[dst.Length..];
            var there = sys.GetFile(to);
            if (there != null)
            {
                if (there.Size == file.Size && there.LastWriteUtc == file.LastWriteUtc) toDelete.Add(file.Path); else left++;
            }
            else if (sys.CopyFile(file.Path, to) && sys.GetFile(to)?.Size == file.Size) toDelete.Add(file.Path);
            else left++;
            done += file.Size;
            if ((sys.UtcNow - last).TotalSeconds >= 1)
            {
                last = sys.UtcNow;
                c.Progress?.Invoke($"正在把「{f.Name}」搬回 C 盘：{CacheClean.Size(done)} / {CacheClean.Size(total)}");
            }
        }
        foreach (var chunk in toDelete.Chunk(500)) left += chunk.Length - sys.DeleteFiles(chunk).Count;
        sys.DeleteEmptyDirectories(dst);
        b["backLeft"] = left;
    }

    public override Outcome Undo(JournalEntry e, OpContext ctx)
    {
        var sys = ctx.Sys;
        var f = FolderMove.Get(e.Target["folder"]?.ToString() ?? "");
        string? src = e.Backup["original"]?.ToString(), dst = e.Backup["target"]?.ToString();
        if (f == null || src == null || dst == null) return Outcome.Fail("记录不完整，不能撤销");
        var cur = sys.GetKnownFolder(f.Guid);
        if (!FolderMove.Same(cur, dst) && !FolderMove.Same(cur, src))
            return Outcome.Fail($"「{f.Name}」的位置之后又被改过，为了不弄乱，Iris 不再自动搬回");
        var bytes = sys.DirectoryExists(dst) ? sys.EnumerateFiles(dst, true, includeSystem: true).Sum(x => x.Size) : 0;
        var sd = sys.Drives().FirstOrDefault(d => d.Root.Equals(FolderMove.SystemDrive(sys), StringComparison.OrdinalIgnoreCase));
        if (sd != null && sd.FreeBytes < bytes + FolderMove.Margin(bytes))
            return Outcome.Fail($"C 盘空间不够搬回来：要搬 {CacheClean.Size(bytes)}，C 盘只剩 {CacheClean.Size(sd.FreeBytes)}");
        return base.Undo(e, ctx);
    }

    protected override string? VerifyRestored(PlanStep s, OpContext c, JsonObject b)
    {
        if (!FolderMove.Same(c.Sys.GetKnownFolder(F(s).Guid), b["original"]?.ToString())) return "系统里的位置没有改回来";
        var left = Operations.Int(b["backLeft"]) ?? 0;
        return left > 0 ? $"位置已经改回来了，但有 {left} 个文件留在了{FolderMove.DriveName(b["target"]!.ToString()[..2])}（正在被使用，或者和原处的同名文件不一样）。关掉相关软件后可以再点一次撤销" : null;
    }

    protected override string DoneMessage(PlanStep s, OpContext c, JsonObject b)
    {
        var f = F(s);
        var msg = $"已把「{f.Name}」搬到 {FolderMove.DriveName(T(s, "drive"))}（{CacheClean.Size(Operations.Int(b["bytes"]) ?? 0)}，{Operations.Int(b["files"]) ?? 0} 个文件），"
                  + $"资源管理器里的「{f.Name}」已经指向新位置";
        var left = Operations.Int(b["left"]) ?? 0;
        if (left > 0)
            msg += $"。{left} 个文件正在被使用或在搬的时候被修改了，还在原来的文件夹里，关掉相关软件后可以自己拖过去";
        if (f.Id == "documents") msg += "。微信、QQ、Outlook 的文件夹留在原处，聊天文件可以在软件设置里改存储位置";
        return msg;
    }
}
