using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

public class FolderMoveTests
{
    const string Docs = "FDD39AD0-238F-46AF-ADB4-6C85480369C7";
    const string Downloads = "374DE290-123F-4565-9164-39C4925E467B";
    const string Profile = @"C:\Users\Alice";
    const long GB = 1L << 30;
    readonly FakeWinSystem sys = new();
    OpContext Ctx => new(sys, Path.GetTempPath());

    public FolderMoveTests()
    {
        sys.EnvVars["USERPROFILE"] = Profile;
        sys.EnvVars["SystemDrive"] = "C:";
        sys.KnownFolders[Docs] = Profile + @"\Documents";
        sys.KnownFolders[Downloads] = Profile + @"\Downloads";
        sys.DriveList.Add(new DriveFact("C:", "", "Fixed", "NTFS", "NVMe", 5 * GB, 256 * GB));
        sys.DriveList.Add(new DriveFact("D:", "数据", "Fixed", "NTFS", "SATA", 400 * GB, 1000 * GB));
        sys.DriveList.Add(new DriveFact("E:", "移动硬盘", "Fixed", "NTFS", "USB", 900 * GB, 1000 * GB));
        sys.DriveList.Add(new DriveFact("F:", "U盘", "Removable", "FAT32", "USB", 30 * GB, 32 * GB));
        sys.AddFile(Profile + @"\Documents\desktop.ini", 1, 400);
        sys.AddFile(Profile + @"\Documents\论文\初稿.docx", 3_000_000, 10);
        sys.AddFile(Profile + @"\Documents\论文\图\1.png", 2_000_000, 10);
        sys.AddFile(Profile + @"\Documents\WeChat Files\wxid_x\Msg\a.db", 900_000_000, 1);
        sys.Dirs.Add(Profile + @"\Documents\空文件夹");
        sys.AddFile(Profile + @"\Downloads\setup.exe", 50_000_000, 30);
    }

    static PlanStep Step(string folder, string drive = "D:") => new()
    {
        Key = "move:" + folder + "#0", FindingKey = "move:" + folder, Op = "folder.move", OpName = "搬到其他盘", Title = folder,
        NeedsAdmin = false, Target = new JsonObject { ["folder"] = folder, ["drive"] = drive },
    };

    [Fact]
    public void Only_internal_ntfs_non_system_drives_are_targets()
    {
        Assert.Equal(["D:"], FolderMove.Targets(sys).Select(d => d.Root));
        Assert.Contains("U 盘", FolderMove.DriveProblem(sys, sys.DriveList[2]));
        Assert.Equal("这是系统盘", FolderMove.DriveProblem(sys, sys.DriveList[0]));
    }

    [Fact]
    public void Candidates_measure_and_keep_chat_folders_out()
    {
        var c = FolderMove.Candidates(sys);
        var docs = c.Single(x => x.Folder.Id == "documents");
        Assert.Equal(5_000_001, docs.Bytes);
        Assert.Equal(3, docs.Files);
        Assert.Equal(("WeChat Files", 900_000_000L), docs.Kept.Single());
        Assert.Null(docs.Problem);
        Assert.Null(docs.MovedTo);
        Assert.DoesNotContain(c, x => x.Folder.Id == "pictures");  // 系统里读不到位置的不列
    }

    [Fact]
    public void Move_copies_switches_then_deletes_originals()
    {
        var e = Executor.RunStep(Step("documents"), Ctx, "r1", "b1");
        Assert.Equal(Status.Done, e.Status);
        Assert.True(e.Reversible);
        Assert.Equal(@"D:\Alice\Documents", sys.KnownFolders[Docs]);
        Assert.True(sys.FileExists(@"D:\Alice\Documents\论文\初稿.docx"));
        Assert.True(sys.FileExists(@"D:\Alice\Documents\desktop.ini"));
        Assert.Contains(@"D:\Alice\Documents\空文件夹", sys.Dirs);
        Assert.False(sys.FileExists(Profile + @"\Documents\论文\初稿.docx"));
        // 聊天文件留在原处，不复制
        Assert.True(sys.FileExists(Profile + @"\Documents\WeChat Files\wxid_x\Msg\a.db"));
        Assert.False(sys.FileExists(@"D:\Alice\Documents\WeChat Files\wxid_x\Msg\a.db"));
        Assert.Contains("搬到 D 盘", e.Message);
        Assert.Contains("微信", e.Message);
        Assert.DoesNotContain("Alice", e.Message);  // 消息会进反馈包：不带用户名
        Assert.Equal("move:documents", e.FindingKey);
        Assert.False(BackupPruner.Eligible(e, sys.UtcNow.AddDays(400)));  // 清理旧备份时不清：清掉就搬不回来了
    }

    [Fact]
    public void Undo_moves_everything_back_including_new_files()
    {
        var e = Executor.RunStep(Step("downloads"), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.False(sys.DirectoryExists(Profile + @"\Downloads"));
        sys.AddFile(@"D:\Alice\Downloads\new.zip", 10, 0);  // 搬过去之后新下载的
        sys.DriveList[0] = sys.DriveList[0] with { FreeBytes = 50 * GB };
        var u = Executor.Undo(e, Ctx);
        Assert.Equal(Status.Done, u.Status);
        Assert.Equal(Profile + @"\Downloads", sys.KnownFolders[Downloads]);
        Assert.True(sys.FileExists(Profile + @"\Downloads\setup.exe"));
        Assert.True(sys.FileExists(Profile + @"\Downloads\new.zip"));
        Assert.False(sys.DirectoryExists(@"D:\Alice\Downloads"));
        Assert.Equal(Status.Undone, e.Status);
    }

    [Fact]
    public void Undo_refuses_when_c_is_too_full()
    {
        var e = Executor.RunStep(Step("downloads"), Ctx);
        sys.DriveList[0] = sys.DriveList[0] with { FreeBytes = 1 * GB };
        var u = Executor.Undo(e, Ctx);
        Assert.Equal(Status.Failed, u.Status);
        Assert.Contains("C 盘空间不够", u.Message);
        Assert.Equal(@"D:\Alice\Downloads", sys.KnownFolders[Downloads]);  // 没动
    }

    [Fact]
    public void Locked_and_modified_files_stay_in_place_with_one_copy()
    {
        var busy = Profile + @"\Documents\论文\初稿.docx";
        var edited = Profile + @"\Documents\论文\图\1.png";
        sys.Locked.Add(busy);  // 能读不能删（例如开着的文档）
        sys.OnCopied = p => { if (p == edited) sys.Files[edited] = sys.Files[edited] with { LastWriteUtc = sys.UtcNow }; };
        var e = Executor.RunStep(Step("documents"), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.True(sys.FileExists(busy));
        Assert.False(sys.FileExists(@"D:\Alice\Documents\论文\初稿.docx"));
        Assert.True(sys.FileExists(edited));
        Assert.False(sys.FileExists(@"D:\Alice\Documents\论文\图\1.png"));
        Assert.True(sys.FileExists(@"D:\Alice\Documents\desktop.ini"));
        Assert.Contains("2 个文件", e.Message);
    }

    [Fact]
    public void Unreadable_files_are_left_behind_but_too_many_failures_roll_back()
    {
        sys.Unreadable.Add(Profile + @"\Documents\论文\图\1.png");
        var e = Executor.RunStep(Step("documents"), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.True(sys.FileExists(Profile + @"\Documents\论文\图\1.png"));

        var s2 = new FakeWinSystem();
        s2.EnvVars["USERPROFILE"] = Profile; s2.EnvVars["SystemDrive"] = "C:";
        s2.KnownFolders[Downloads] = Profile + @"\Downloads";
        s2.DriveList.AddRange(sys.DriveList);
        for (var i = 0; i < 30; i++) { s2.AddFile(Profile + $@"\Downloads\f{i}.bin", 100, 1); s2.Unreadable.Add(Profile + $@"\Downloads\f{i}.bin"); }
        s2.AddFile(Profile + @"\Downloads\ok.bin", 100, 1);
        var e2 = Executor.RunStep(Step("downloads"), new OpContext(s2, Path.GetTempPath()));
        Assert.Equal(Status.RolledBack, e2.Status);
        Assert.Equal(Profile + @"\Downloads", s2.KnownFolders[Downloads]);
        Assert.True(s2.FileExists(Profile + @"\Downloads\ok.bin"));
        Assert.False(s2.FileExists(@"D:\Alice\Downloads\ok.bin"));
    }

    [Fact]
    public void System_refusing_new_location_rolls_back_without_touching_originals()
    {
        sys.RefuseKnownFolder = true;
        var e = Executor.RunStep(Step("documents"), Ctx);
        Assert.Equal(Status.RolledBack, e.Status);
        Assert.Equal(Profile + @"\Documents", sys.KnownFolders[Docs]);
        Assert.True(sys.FileExists(Profile + @"\Documents\论文\初稿.docx"));
        Assert.False(sys.DirectoryExists(@"D:\Alice\Documents"));
        Assert.DoesNotContain(sys.Deleted, p => p.StartsWith(Profile));
    }

    [Theory]
    [InlineData("E:", "U 盘、移动硬盘")]
    [InlineData("F:", "内置硬盘")]
    [InlineData("C:", "目标盘不合法")]
    [InlineData("Z:", "找不到")]
    public void Bad_targets_are_refused(string drive, string reason)
    {
        var e = Executor.RunStep(Step("documents", drive), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains(reason, e.Message);
        Assert.Equal(0, sys.CopyCount);
    }

    [Fact]
    public void Refuses_when_target_has_files_not_enough_space_or_already_moved()
    {
        sys.AddFile(@"D:\Alice\Documents\old.txt", 1, 100);
        Assert.Contains("不是空的", Executor.RunStep(Step("documents"), Ctx).Message);

        sys.DriveList[1] = sys.DriveList[1] with { FreeBytes = 1 * GB };
        Assert.Contains("空间不够", Executor.RunStep(Step("downloads"), Ctx).Message);

        sys.KnownFolders[Downloads] = @"C:\Users\Alice\OneDrive\Downloads";
        Assert.Contains("不在默认位置", Executor.RunStep(Step("downloads"), Ctx).Message);
        Assert.Equal(0, sys.CopyCount);
        Assert.Equal(@"C:\Users\Alice\OneDrive\Downloads", FolderMove.Candidates(sys).Single(c => c.Folder.Id == "downloads").MovedTo);
    }

    [Fact]
    public void Target_validation_rejects_unknown_folders()
    {
        Assert.NotNull(Operations.ValidateTarget("folder.move", new JsonObject { ["folder"] = "desktop", ["drive"] = "D:" }));
        Assert.NotNull(Operations.ValidateTarget("folder.move", new JsonObject { ["folder"] = "documents", ["drive"] = @"D:\..\C:" }));
        Assert.Null(Operations.ValidateTarget("folder.move", new JsonObject { ["folder"] = "documents", ["drive"] = "d:" }));
    }
}
