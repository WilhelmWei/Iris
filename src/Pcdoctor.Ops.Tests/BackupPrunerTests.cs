using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>设置 → 存储空间 → 清理旧备份：只清用不上的和过期的，最近一批永远保留，清理后不能再撤销。</summary>
public class BackupPrunerTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "pcd-prune-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    JournalEntry Entry(string id, int daysAgo, string status, string batch, string op = "startup.disable", string? oem = null)
    {
        var e = new JournalEntry
        {
            Id = id, TimeUtc = Now.AddDays(-daysAgo), Op = op, Status = status, BatchId = batch, Reversible = status == Status.Done,
            Backup = new JsonObject { ["value"] = "原来的值" }, Target = new JsonObject(),
        };
        if (oem != null)
        {
            e.Target["publishedName"] = oem;
            var d = Directory.CreateDirectory(Path.Combine(dir, "work", "driver-" + oem)).FullName;
            File.WriteAllBytes(Path.Combine(d, "x.inf"), new byte[1000]);
        }
        return e;
    }

    [Fact]
    public void PrunesUnusedAndOldButKeepsRecentAndLatestBatch()
    {
        var store = new JournalStore(Path.Combine(dir, "journal"));
        store.Save(Entry("a-old-done", 200, Status.Done, "b1"));
        store.Save(Entry("b-undone", 10, Status.Undone, "b2"));
        store.Save(Entry("c-recent-done", 10, Status.Done, "b2"));
        store.Save(Entry("d-rolled", 5, Status.RolledBack, "b3"));
        store.Save(Entry("e-latest-old?", 1, Status.Undone, "b4"));  // 最近一批：即使已撤销也保留
        var r = BackupPruner.Prune(store, Path.Combine(dir, "work"), Now.AddDays(-90), Now);
        Assert.Equal(3, r.Entries);
        var by = store.All().ToDictionary(e => e.Id);
        Assert.Empty(by["a-old-done"].Backup);
        Assert.False(by["a-old-done"].Reversible);
        Assert.StartsWith("备份已于", by["a-old-done"].UndoHint);
        Assert.Empty(by["b-undone"].Backup);
        Assert.Empty(by["d-rolled"].Backup);
        Assert.NotEmpty(by["c-recent-done"].Backup);       // 90 天内、还能撤销的保留
        Assert.True(by["c-recent-done"].Reversible);
        Assert.NotEmpty(by["e-latest-old?"].Backup);      // 最近一批保留
    }

    [Fact]
    public void DeletesDriverExportFolderOnlyForSafeNames()
    {
        var store = new JournalStore(Path.Combine(dir, "journal"));
        store.Save(Entry("drv", 200, Status.Done, "b1", "driver.delete_old_version", "oem57.inf"));
        var evil = Entry("evil", 200, Status.Done, "b1", "driver.delete_old_version");
        evil.Target["publishedName"] = "../../journal";
        store.Save(evil);
        store.Save(Entry("latest", 0, Status.Done, "b9"));
        var r = BackupPruner.Prune(store, Path.Combine(dir, "work"), Now.AddDays(-30), Now);
        Assert.False(Directory.Exists(Path.Combine(dir, "work", "driver-oem57.inf")));
        Assert.True(r.Bytes >= 1000);
        Assert.True(Directory.Exists(Path.Combine(dir, "journal")));  // 不合法的名字不会被拼成路径删除
        Assert.Equal(2, r.Entries);
    }

    [Fact]
    public void PrunedEntriesCanNoLongerBeUndone()
    {
        var store = new JournalStore(Path.Combine(dir, "journal"));
        store.Save(Entry("old", 100, Status.Done, "b1"));
        store.Save(Entry("latest", 0, Status.Done, "b2"));
        BackupPruner.Prune(store, Path.Combine(dir, "work"), Now.AddDays(-90), Now);
        var e = store.Get("old")!;
        var o = Executor.Undo(e, new OpContext(new FakeWinSystem(), Path.Combine(dir, "work")));
        Assert.Equal(Status.Failed, o.Status);
    }
}
