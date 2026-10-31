using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>清理第三期：系统遗留交给磁盘清理、旧安装包移到回收站、卸载残留文件夹挪到备份区。</summary>
public class Phase3Tests
{
    const long GB = 1L << 30;
    const string VC = SystemCleanup.VolumeCaches;
    readonly FakeWinSystem sys = new();
    OpContext Ctx => new(sys, @"C:\Program Files\pcdoctor-data\backups");

    public Phase3Tests()
    {
        sys.EnvVars["SystemDrive"] = "C:";
        sys.EnvVars["USERPROFILE"] = @"C:\Users\Alice";
        sys.DriveList.Add(new DriveFact("C:", "", "Fixed", "NTFS", "NVMe", 10 * GB, 256 * GB));
    }

    static PlanStep Step(string op, JsonObject? target = null, List<string>? choice = null) => new()
    {
        Key = "F001#0", Op = op, OpName = Catalog.Ops[op].Name, Title = op, NeedsAdmin = Catalog.Ops[op].NeedsAdmin,
        Reversible = Catalog.Ops[op].Reversible, Target = target ?? new JsonObject(),
        CacheChoice = choice == null ? null : new CacheChoice { Categories = choice },
    };

    // ------------------------------------------------------------------ 磁盘清理
    void SetUpCleanmgr()
    {
        sys.AddFile(@"C:\Windows\System32\cleanmgr.exe", 1, 100);
        foreach (var h in new[] { "Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files", "Delivery Optimization Files", "Update Cleanup", "Recycle Bin" })
            sys.CreateKey(Hive.LocalMachine, VC + "\\" + h);
        sys.Set(Hive.LocalMachine, VC + @"\Recycle Bin", SystemCleanup.Flag, RegValue.DWord(2));  // 别人留下的同名标记：不能顺带清空回收站
        sys.AddFile(@"C:\Windows.old\Windows\x.dll", 5 * GB, 30);
    }

    [Fact]
    public void System_cleanup_flags_only_chosen_handlers_and_restores_them()
    {
        SetUpCleanmgr();
        Dictionary<string, int?>? during = null;
        sys.OnRun = (exe, args) =>
        {
            Assert.EndsWith("cleanmgr.exe", exe);
            Assert.Equal("/sagerun:7421", args);
            during = sys.SubKeys(Hive.LocalMachine, VC).ToDictionary(h => h, h => sys.ReadValue(Hive.LocalMachine, VC + "\\" + h, SystemCleanup.Flag)?.AsInt());
            sys.Files.Remove(@"C:\Windows.old\Windows\x.dll");
            sys.DriveList[0] = sys.DriveList[0] with { FreeBytes = 15 * GB };
            return 0;
        };
        var e = Executor.RunStep(Step("system.cleanup", choice: ["windows-old", "windows-upgrade-temp"]), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal(2, during!["previous installations"]);
        Assert.Equal(2, during["temporary setup files"]);
        Assert.Null(during["recycle bin"]);         // 运行时只有选中的项目带标记
        Assert.Null(during["update cleanup"]);
        Assert.Equal(2, sys.ReadValue(Hive.LocalMachine, VC + @"\Recycle Bin", SystemCleanup.Flag)!.AsInt());  // 结束后原样恢复
        Assert.Null(sys.ReadValue(Hive.LocalMachine, VC + @"\Previous Installations", SystemCleanup.Flag));
        Assert.Contains("C 盘多出了 5 GB", e.Message);
        Assert.False(e.Reversible);
    }

    [Fact]
    public void System_cleanup_reports_windows_old_left_and_restores_flags_on_crash()
    {
        SetUpCleanmgr();
        var e = Executor.RunStep(Step("system.cleanup", choice: ["windows-old"]), Ctx);
        Assert.Contains("Windows.old 还在", e.Message);

        sys.OnRun = (_, _) => throw new InvalidOperationException("boom");
        Executor.RunStep(Step("system.cleanup", choice: ["windows-old"]), Ctx);
        Assert.Null(sys.ReadValue(Hive.LocalMachine, VC + @"\Previous Installations", SystemCleanup.Flag));
        Assert.Equal(2, sys.ReadValue(Hive.LocalMachine, VC + @"\Recycle Bin", SystemCleanup.Flag)!.AsInt());
    }

    [Fact]
    public void System_cleanup_refuses_unknown_items_and_missing_cleanmgr()
    {
        Assert.Contains("cleanmgr", Executor.RunStep(Step("system.cleanup", choice: ["windows-old"]), Ctx).Message);
        SetUpCleanmgr();
        Assert.Contains("不支持", Executor.RunStep(Step("system.cleanup", choice: ["recycle-bin"]), Ctx).Message);
        Assert.Contains("没有选择", Executor.RunStep(Step("system.cleanup", choice: []), Ctx).Message);
    }

    [Fact]
    public void System_cleanup_options_follow_measurements()
    {
        var report = JsonNode.Parse("""
            {"sections":{"storage":{"junk":{"items":[
            {"id":"windows-old","tier":"B","sizeMB":18000},{"id":"delivery-optimization","tier":"B","sizeMB":50},{"id":"temp-user","tier":"A","sizeMB":900}]}}}}
            """)!;
        var o = SystemCleanup.Options(report);
        Assert.Equal(["windows-old", "update-cleanup"], o.Items.Select(i => i.Id));
        Assert.Equal(["windows-old"], o.Categories);  // 量不出大小的旧更新默认不勾
        Assert.Empty(o.Ages);
    }

    // ------------------------------------------------------------------ 旧安装包
    void SetUpDownloads()
    {
        sys.KnownFolders["374DE290-123F-4565-9164-39C4925E467B"] = @"C:\Users\Alice\Downloads";
        sys.AddFile(@"C:\Users\Alice\Downloads\QuickVPN-setup.exe", 80_000_000, 200);
        sys.AddFile(@"C:\Users\Alice\Downloads\old\tool.msi", 20_000_000, 60);
        sys.AddFile(@"C:\Users\Alice\Downloads\new-setup.exe", 50_000_000, 3);   // 太新
        sys.AddFile(@"C:\Users\Alice\Downloads\论文.docx", 1_000_000, 400);        // 不是安装包
    }

    [Fact]
    public void Installers_are_listed_live_and_moved_to_recycle_bin()
    {
        SetUpDownloads();
        var o = OldInstallers.Options(sys);
        Assert.Equal([@"C:\Users\Alice\Downloads\QuickVPN-setup.exe", @"C:\Users\Alice\Downloads\old\tool.msi"], o.Items.Select(i => i.Id));
        Assert.Equal(o.Items.Select(i => i.Id), o.Categories);
        Assert.Equal("200 天前下载", o.Items[0].Why);

        var e = Executor.RunStep(Step("downloads.recycle_installers", choice: o.Categories), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Contains(@"C:\Users\Alice\Downloads\QuickVPN-setup.exe", sys.RecycleBin);
        Assert.True(sys.FileExists(@"C:\Users\Alice\Downloads\论文.docx"));
        Assert.Contains("2 个旧安装包", e.Message);
        Assert.Contains("回收站", e.Message);
        Assert.DoesNotContain("QuickVPN", e.Message);  // 消息会进反馈包：不写文件名
    }

    [Fact]
    public void Installers_step_cannot_be_pointed_at_other_files()
    {
        SetUpDownloads();
        var e = Executor.RunStep(Step("downloads.recycle_installers", choice: [@"C:\Users\Alice\Downloads\论文.docx"]), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Empty(sys.RecycleBin);
        var e2 = Executor.RunStep(Step("downloads.recycle_installers", choice: [@"C:\Users\Alice\Downloads\new-setup.exe"]), Ctx);
        Assert.Equal(Status.Failed, e2.Status);
    }

    [Fact]
    public void Installers_with_recycle_bin_off_change_nothing()
    {
        SetUpDownloads();
        sys.RecycleBinDisabled = true;
        var e = Executor.RunStep(Step("downloads.recycle_installers", choice: OldInstallers.Options(sys).Categories), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.True(sys.FileExists(@"C:\Users\Alice\Downloads\QuickVPN-setup.exe"));
    }

    // ------------------------------------------------------------------ 卸载残留文件夹
    const string Old = @"C:\Program Files (x86)\OldTool";

    [Fact]
    public void Leftover_dir_moves_to_backup_and_undo_puts_it_back()
    {
        sys.AddFile(Old + @"\logs\run.log", 2_000_000, 400);
        sys.AddFile(Old + @"\config.ini", 1000, 400);
        var e = Executor.RunStep(Step("leftover.remove_dir", new JsonObject { ["path"] = Old }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.False(sys.DirectoryExists(Old));
        var moved = e.Backup["moved"]!.ToString();
        Assert.StartsWith(@"C:\Program Files\pcdoctor-data\backups\leftovers\", moved);
        Assert.True(sys.FileExists(moved + @"\logs\run.log"));
        Assert.True(e.Reversible);

        var u = Executor.Undo(e, Ctx);
        Assert.Equal(Status.Done, u.Status);
        Assert.True(sys.FileExists(Old + @"\logs\run.log"));
        Assert.False(sys.DirectoryExists(moved));
    }

    [Theory]
    [InlineData(@"\bin\tool.exe", "程序文件")]
    [InlineData(@"\x\helper.dll", "程序文件")]
    public void Leftover_dir_with_programs_is_left_alone(string file, string reason)
    {
        sys.AddFile(Old + file, 10, 400);
        var e = Executor.RunStep(Step("leftover.remove_dir", new JsonObject { ["path"] = Old }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains(reason, e.Message);
        Assert.True(sys.DirectoryExists(Old));
    }

    [Fact]
    public void Leftover_dir_still_referenced_is_left_alone()
    {
        sys.AddFile(Old + @"\data.bin", 10, 400);
        sys.Set(Hive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OldTool", "InstallLocation", FakeWinSystem.Str(Old + "\\"));
        Assert.Contains("引用", Executor.RunStep(Step("leftover.remove_dir", new JsonObject { ["path"] = Old }), Ctx).Message);

        var s2 = new FakeWinSystem(); s2.EnvVars["SystemDrive"] = "C:";
        s2.AddFile(Old + @"\data.bin", 10, 400);
        s2.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\OldSvc", "ImagePath", FakeWinSystem.Str("\"" + Old + "\\svc.exe\" -k"));
        Assert.Contains("引用", Executor.RunStep(Step("leftover.remove_dir", new JsonObject { ["path"] = Old }), new OpContext(s2, "x")).Message);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Program Files\Microsoft Office")]
    [InlineData(@"C:\Program Files\WindowsApps")]
    [InlineData(@"C:\Program Files\OldTool\sub")]
    [InlineData(@"C:\Users\Alice\AppData\Local\OldTool")]
    [InlineData(@"C:\Program Files\..\Windows")]
    [InlineData(@"D:\Program Files\OldTool")]
    public void Leftover_dir_only_accepts_plain_program_files_children(string path)
    {
        sys.AddFile(path + @"\a.txt", 10, 400);
        var e = Executor.RunStep(Step("leftover.remove_dir", new JsonObject { ["path"] = path }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.True(sys.FileExists(path + @"\a.txt"));
    }

    [Fact]
    public void Plan_builder_makes_one_cleanup_step_and_lists_installers_live()
    {
        SetUpDownloads();
        var report = JsonNode.Parse("""
            {"flags":[{"id":"F001","section":"storage","rule":"system-leftover","object":"旧版 Windows（Windows.old）"},
                      {"id":"F002","section":"storage","rule":"system-leftover","object":"升级临时文件（$Windows.~BT）"},
                      {"id":"F003","section":"storage","rule":"downloads-installers","object":"下载文件夹里的旧安装包"},
                      {"id":"F004","section":"software","rule":"leftover-dir","object":"C:\\Program Files (x86)\\OldTool"}],
             "sections":{"storage":{"junk":{"items":[{"id":"windows-old","tier":"B","sizeMB":18000},{"id":"windows-upgrade-temp","tier":"B","sizeMB":3000}]}}}}
            """)!;
        var judge = JsonNode.Parse("""
            {"judgement":{"findings":[
              {"flagIds":["F001"],"verdict":"leftover","proposedAction":"system.cleanup","object":"Windows.old"},
              {"flagIds":["F002"],"verdict":"leftover","proposedAction":"system.cleanup","object":"升级临时文件"},
              {"flagIds":["F003"],"verdict":"leftover","proposedAction":"downloads.recycle_installers","object":"旧安装包"},
              {"flagIds":["F004"],"verdict":"leftover","proposedAction":"leftover.remove_dir","object":"OldTool"}]}}
            """)!;
        var plan = PlanBuilder.Build(report, judge, ["F001", "F002", "F003", "F004"], @"C:\Windows", sys: sys);
        Assert.Equal(["system.cleanup", "downloads.recycle_installers", "leftover.remove_dir"], plan.Steps.Select(s => s.Op));
        Assert.Equal(["windows-old", "windows-upgrade-temp"], plan.Steps[0].CacheOptions!.Categories);
        Assert.True(plan.Steps[0].NeedsAdmin);
        Assert.False(plan.Steps[1].NeedsAdmin);
        Assert.Equal(2, plan.Steps[1].CacheOptions!.Items.Count);
        Assert.Equal(@"C:\Program Files (x86)\OldTool", plan.Steps[2].Target["path"]!.ToString());
        Assert.Contains(plan.Skipped, x => x.Reason.Contains("合并"));

        var noSys = PlanBuilder.Build(report, judge, ["F003"], @"C:\Windows");
        Assert.Empty(noSys.Steps);
    }

    [Fact]
    public void Pruning_old_backups_deletes_moved_leftover_dirs()
    {
        var work = Path.Combine(Path.GetTempPath(), "pcd-prune-" + Guid.NewGuid().ToString("N"));
        var journalDir = Path.Combine(work, "journal");
        try
        {
            var moved = work + "\\leftovers\\20260101-abc";
            Directory.CreateDirectory(moved);
            File.WriteAllText(Path.Combine(moved, "a.log"), "x");
            var store = new JournalStore(journalDir);
            store.Save(new JournalEntry
            {
                Id = "1", Op = "leftover.remove_dir", Status = Status.Done, Reversible = true, TimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Target = new JsonObject { ["path"] = Old }, Backup = new JsonObject { ["moved"] = moved }, BatchId = "old",
            });
            store.Save(new JournalEntry { Id = "2", Op = "startup.disable", Status = Status.Done, TimeUtc = DateTime.UtcNow, BatchId = "new" });
            BackupPruner.Prune(store, work, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
            Assert.False(Directory.Exists(moved));
            Assert.False(store.All().Single(e => e.Id == "1").Reversible);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }
}
