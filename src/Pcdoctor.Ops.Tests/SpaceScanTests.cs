using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>「C 盘空间去哪了」：汇总、链接、云盘占位、压缩、硬链接去重、快照裁剪、发给作者的摘要不带个人文件夹名。</summary>
public class SpaceScanTests
{
    sealed class FakeSource : ISpaceSource
    {
        public readonly Dictionary<string, List<SpaceEntry>> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (ulong, int)> Links = new(StringComparer.OrdinalIgnoreCase);
        public int LinkCalls;
        public void Add(string dir, SpaceEntry e) { (Dirs.TryGetValue(dir, out var l) ? l : Dirs[dir] = []).Add(e); }
        public IEnumerable<SpaceEntry> List(string dir) => Dirs.TryGetValue(dir, out var l) ? l : throw new UnauthorizedAccessException();
        public long Allocated(string path, long logical) => logical / 4;
        public (ulong id, int links)? LinkInfo(string path) { LinkCalls++; return Links.TryGetValue(path, out var x) ? x : (0UL, 1); }
    }

    const long MB = 1 << 20, GB = 1L << 30;

    static FakeSource Disk()
    {
        var s = new FakeSource();
        s.Add(@"C:\", new("Windows", true, 0, false));
        s.Add(@"C:\", new("Users", true, 0, false));
        s.Add(@"C:\", new("Documents and Settings", true, 0, true));   // 联接点：不进入
        s.Add(@"C:\", new("pagefile.sys", false, 8 * GB, false));
        s.Add(@"C:\", new("System Volume Information", true, 0, false)); // 读不了
        s.Add(@"C:\Windows", new("System32", true, 0, false));
        s.Add(@"C:\Windows", new("WinSxS", true, 0, false));
        s.Add(@"C:\Windows\System32", new("a.dll", false, 2 * GB, false));
        s.Add(@"C:\Windows\WinSxS", new("a.dll", false, 2 * GB, false));   // 和 System32\a.dll 是同一个文件（硬链接）
        s.Add(@"C:\Windows\WinSxS", new("old.dll", false, 1 * GB, false));
        s.Links[@"C:\Windows\System32\a.dll"] = (42, 2);
        s.Links[@"C:\Windows\WinSxS\a.dll"] = (42, 2);
        s.Add(@"C:\Users", new("Alice", true, 0, false));
        s.Add(@"C:\Users\Alice", new("Downloads", true, 0, false));
        s.Add(@"C:\Users\Alice", new("OneDrive", true, 0, false));
        s.Add(@"C:\Users\Alice", new("Alice 的秘密项目", true, 0, false));
        s.Add(@"C:\Users\Alice\Downloads", new("setup.exe", false, 3 * GB, false));
        s.Add(@"C:\Users\Alice\Downloads", new("小.txt", false, 1 * MB, false));
        s.Add(@"C:\Users\Alice\OneDrive", new("cloud.mp4", false, 5 * GB, false, Placeholder: true));
        s.Add(@"C:\Users\Alice\Alice 的秘密项目", new("data.bin", false, 4 * GB, false, Compressed: true));
        return s;
    }

    [Fact]
    public void Sums_skip_links_placeholders_and_count_hard_links_once()
    {
        var src = Disk();
        var scan = new SpaceScan(src, @"C:\", 40 * GB, dedupeUnder: @"C:\Windows");
        scan.Run(() => false);
        Assert.True(scan.Finished);
        Assert.Equal(1, scan.Unreadable);
        var w = scan.Root.Kids!["Windows"];
        Assert.Equal(3 * GB, w.Bytes);                                       // 2 GB 只算一次 + 1 GB
        Assert.Equal(0, scan.Root.Kids["Users"].Kids!["Alice"].Kids!["OneDrive"].Bytes);
        Assert.Equal(1 * GB, scan.Root.Kids["Users"].Kids!["Alice"].Kids!["Alice 的秘密项目"].Bytes);  // 压缩后的实际占用
        Assert.Equal(8 * GB + 3 * GB + 3 * GB + MB + 1 * GB, scan.Root.Bytes);
        Assert.False(scan.Root.Kids.ContainsKey("Documents and Settings"));
        Assert.True(src.LinkCalls <= 3);                                     // 只在 Windows 目录里查硬链接
    }

    [Fact]
    public void Snapshot_prunes_small_folders_and_lists_big_files()
    {
        var scan = new SpaceScan(Disk(), @"C:\", 40 * GB, dedupeUnder: @"C:\Windows");
        scan.Run(() => false);
        var snap = scan.Snapshot(p => SpaceLabels.Json(@"C:\", p));
        var top = snap["tree"]!["children"]!.AsArray();
        Assert.Equal("Users", top[0]!["name"]!.ToString());                 // 大的在前
        Assert.Equal("Windows 系统", top[1]!["label"]!["name"]!.ToString());
        var down = top[0]!["children"]![0]!["children"]!.AsArray().Single(x => x!["name"]!.ToString() == "Downloads")!;
        Assert.Equal("move", down["label"]!["kind"]!.ToString());
        Assert.Equal(@"C:\pagefile.sys", snap["bigFiles"]![0]!["path"]!.ToString());
        Assert.True(snap["finished"]!.GetValue<bool>());
    }

    [Fact]
    public void Cancel_stops_with_partial_result()
    {
        var calls = 0;
        var scan = new SpaceScan(Disk(), @"C:\", 40 * GB);
        scan.Run(() => ++calls > 3);
        Assert.True(scan.Stopped);
        Assert.False(scan.Finished);
        Assert.False(scan.Snapshot()["finished"]!.GetValue<bool>());
    }

    [Fact]
    public void Labels_match_any_user_name_but_only_whole_segments()
    {
        Assert.Equal("微信聊天文件", SpaceLabels.For(@"Users\Bob\Documents\WeChat Files")!.Name);
        Assert.Equal("下载文件夹", SpaceLabels.For(@"\Users\张三\Downloads\")!.Name);
        Assert.Null(SpaceLabels.For(@"Users\Bob\Downloads\old"));
        Assert.Null(SpaceLabels.For(@"Windows2"));
        Assert.Null(SpaceLabels.Json(@"C:\", @"D:\Users\Bob\Downloads"));
    }

    [Fact]
    public void Feedback_summary_has_known_places_only()
    {
        var scan = new SpaceScan(Disk(), @"C:\", 40 * GB, dedupeUnder: @"C:\Windows");
        scan.Run(() => false);
        var snap = scan.Snapshot(p => SpaceLabels.Json(@"C:\", p));
        var sum = SpaceLabels.FeedbackSummary(snap).ToJsonString();
        Assert.DoesNotContain("Alice", sum);
        Assert.DoesNotContain("秘密", sum);
        Assert.DoesNotContain("setup.exe", sum);
        Assert.Contains(@"Users\\*\\Downloads", sum);
        Assert.Equal(5, SpaceLabels.FeedbackSummary(snap)["bigFileCount"]!.GetValue<int>());  // 不小于 1 GB 的文件只给数量；硬链接的另一份不算
    }
}
