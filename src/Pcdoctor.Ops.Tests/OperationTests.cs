using System.Text.Json.Nodes;
using Pcdoctor.Ops;
using Xunit;
using static Pcdoctor.Ops.Tests.FakeWinSystem;

namespace Pcdoctor.Ops.Tests;

public class OperationTests : IDisposable
{
    readonly FakeWinSystem sys = new();
    readonly string work = Path.Combine(Path.GetTempPath(), "pcd-ops-" + Guid.NewGuid().ToString("N"));
    OpContext Ctx => new(sys, work);
    public void Dispose() { if (Directory.Exists(work)) Directory.Delete(work, true); }

    static PlanStep Step(string op, JsonObject target, string title = "x") => new()
    {
        Key = "F001#0", Op = op, OpName = Catalog.Ops.GetValueOrDefault(op)?.Name ?? op, Title = title, Target = target,
        Reversible = Catalog.Ops.GetValueOrDefault(op)?.Reversible ?? true,
    };

    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Svc = @"SYSTEM\CurrentControlSet\Services\";

    JsonObject StartupTarget() => new() { ["approvedHive"] = "HKCU", ["approvedKey"] = Approved, ["valueName"] = "KWpsBox", ["runKey"] = "HKCU:\\" + Run };

    // ------------------------------------------------------------------ 自启动
    [Fact]
    public void Startup_disable_and_undo()
    {
        sys.Set(Hive.CurrentUser, Run, "KWpsBox", Str("\"C:\\wps\\ksolaunch.exe\""));
        var e = Executor.RunStep(Step("startup.disable", StartupTarget()), Ctx);
        Assert.Equal(Status.Done, e.Status);
        var v = sys.ReadValue(Hive.CurrentUser, Approved, "KWpsBox")!.AsBytes()!;
        Assert.Equal(12, v.Length);
        Assert.Equal(3, v[0]);
        Assert.NotNull(sys.ReadValue(Hive.CurrentUser, Run, "KWpsBox"));  // 原启动项不删
        Assert.True(e.Reversible);

        var u = Executor.Undo(e, Ctx);
        Assert.Equal(Status.Done, u.Status);
        Assert.Null(sys.ReadValue(Hive.CurrentUser, Approved, "KWpsBox"));  // 原来没有标记，撤销后也没有
        Assert.Equal(Status.Undone, e.Status);
        Assert.Equal(Status.Failed, Executor.Undo(e, Ctx).Status);  // 不能撤销两次
    }

    [Fact]
    public void Startup_restores_previous_enabled_marker()
    {
        sys.Set(Hive.CurrentUser, Run, "KWpsBox", Str("x"));
        var enabled = RegValue.Binary([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        sys.Set(Hive.CurrentUser, Approved, "KWpsBox", enabled);
        var e = Executor.RunStep(Step("startup.disable", StartupTarget()), Ctx);
        Executor.Undo(e, Ctx);
        Assert.Equal(enabled.AsBytes(), sys.ReadValue(Hive.CurrentUser, Approved, "KWpsBox")!.AsBytes());
    }

    [Fact]
    public void Startup_missing_item_is_not_touched()
    {
        var e = Executor.RunStep(Step("startup.disable", StartupTarget()), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Null(sys.ReadValue(Hive.CurrentUser, Approved, "KWpsBox"));
        Assert.False(e.Reversible);
    }

    [Fact]
    public void Write_that_does_not_stick_is_rolled_back()
    {
        sys.Set(Hive.CurrentUser, Run, "KWpsBox", Str("x"));
        sys.ReadOnlyKeys.Add(Approved);  // 系统拒绝写入（红线 7）：写了没生效
        var e = Executor.RunStep(Step("startup.disable", StartupTarget()), Ctx);
        Assert.Equal(Status.RolledBack, e.Status);
        Assert.Contains("已恢复原状", e.Message);
        Assert.False(e.Reversible);
    }

    // ------------------------------------------------------------------ 计划任务
    [Fact]
    public void Task_disable_and_undo()
    {
        sys.Tasks[@"\WpsUpdateTask"] = TaskXmlOf(true);
        var e = Executor.RunStep(Step("task.disable", new() { ["fullName"] = @"\WpsUpdateTask" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Contains("<Enabled>false</Enabled>", sys.Tasks[@"\WpsUpdateTask"]);
        Executor.Undo(e, Ctx);
        Assert.Contains("<Enabled>true</Enabled>", sys.Tasks[@"\WpsUpdateTask"]);
    }

    [Fact]
    public void Task_delete_leftover_backs_up_and_restores()
    {
        sys.Tasks[@"\ramboplay-uac"] = TaskXmlOf(true);
        var t = new JsonObject { ["fullName"] = @"\ramboplay-uac", ["executable"] = @"D:\Rambo\client\ramboplay.ra2.exe" };
        var e = Executor.RunStep(Step("task.delete_leftover", t), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.False(sys.Tasks.ContainsKey(@"\ramboplay-uac"));
        Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
        Assert.Equal(TaskXmlOf(true), sys.Tasks[@"\ramboplay-uac"]);
    }

    [Fact]
    public void Task_delete_refuses_when_program_came_back()
    {
        sys.Tasks[@"\ramboplay-uac"] = TaskXmlOf(true);
        sys.AddFile(@"D:\Rambo\client\ramboplay.ra2.exe", 1, 1);
        var e = Executor.RunStep(Step("task.delete_leftover", new() { ["fullName"] = @"\ramboplay-uac", ["executable"] = @"D:\Rambo\client\ramboplay.ra2.exe" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.True(sys.Tasks.ContainsKey(@"\ramboplay-uac"));
    }

    // ------------------------------------------------------------------ 服务
    void AddService(string name, string imagePath, int start = 2, string? serviceDll = null, bool delayed = false)
    {
        sys.Set(Hive.LocalMachine, Svc + name, "ImagePath", new RegValue("ExpandString", JsonValue.Create(imagePath)));
        sys.Set(Hive.LocalMachine, Svc + name, "Start", RegValue.DWord(start));
        if (delayed) sys.Set(Hive.LocalMachine, Svc + name, "DelayedAutostart", RegValue.DWord(1));
        if (serviceDll != null) sys.Set(Hive.LocalMachine, Svc + name + @"\Parameters", "ServiceDll", new RegValue("ExpandString", JsonValue.Create(serviceDll)));
    }

    [Fact]
    public void Service_set_manual_and_undo_keeps_delayed_start()
    {
        AddService("ThirdSvc", "\"C:\\Program Files\\Vendor\\svc.exe\" -k", delayed: true);
        var e = Executor.RunStep(Step("service.set_manual", new() { ["name"] = "ThirdSvc" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal(3, sys.ReadValue(Hive.LocalMachine, Svc + "ThirdSvc", "Start")!.AsInt());
        Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
        Assert.Equal(2, sys.ReadValue(Hive.LocalMachine, Svc + "ThirdSvc", "Start")!.AsInt());
        Assert.Equal(1, sys.ReadValue(Hive.LocalMachine, Svc + "ThirdSvc", "DelayedAutostart")!.AsInt());
    }

    [Fact]
    public void Service_set_manual_refuses_windows_services()
    {
        AddService("bthserv", @"%SystemRoot%\system32\svchost.exe -k LocalService", serviceDll: @"%SystemRoot%\System32\bthserv.dll");
        var e = Executor.RunStep(Step("service.set_manual", new() { ["name"] = "bthserv" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("Windows 自带", e.Message);
        Assert.Empty(sys.Commands);
    }

    [Fact]
    public void Service_in_svchost_with_third_party_dll_is_allowed()
    {
        AddService("XLServicePlatform", @"%SystemRoot%\System32\svchost.exe -k XLServicePlatform",
            serviceDll: @"C:\Program Files (x86)\Common Files\Thunder Network\ServicePlatform\XLSP.dll");
        Assert.Equal(Status.Done, Executor.RunStep(Step("service.set_manual", new() { ["name"] = "XLServicePlatform" }), Ctx).Status);
    }

    [Fact]
    public void Service_change_that_does_not_stick_is_reported()
    {
        AddService("ThirdSvc", @"C:\Program Files\Vendor\svc.exe");
        sys.ScIgnores = true;
        var e = Executor.RunStep(Step("service.set_manual", new() { ["name"] = "ThirdSvc" }), Ctx);
        Assert.Equal(Status.RolledBack, e.Status);
        Assert.Equal(2, sys.ReadValue(Hive.LocalMachine, Svc + "ThirdSvc", "Start")!.AsInt());
    }

    [Fact]
    public void Orphan_service_deleted_with_full_backup()
    {
        AddService("PassGuard", @"\SystemRoot\system32\drivers\PassGuard_x64.sys", start: 1);
        sys.Set(Hive.LocalMachine, Svc + @"PassGuard\Enum", "Count", RegValue.DWord(0));
        var e = Executor.RunStep(Step("service.delete_orphan", new() { ["name"] = "PassGuard" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.False(sys.KeyExists(Hive.LocalMachine, Svc + "PassGuard"));
        var u = Executor.Undo(e, Ctx);
        Assert.Contains("重启", u.Message);
        Assert.Equal(1, sys.ReadValue(Hive.LocalMachine, Svc + "PassGuard", "Start")!.AsInt());
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, Svc + @"PassGuard\Enum", "Count")!.AsInt());
    }

    [Fact]
    public void Orphan_on_class_filter_is_refused_red_line_2()
    {
        AddService("PECKP", @"\SystemRoot\System32\drivers\PECKP_x64.sys", start: 3);
        sys.Set(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96b-e325-11ce-bfc1-08002be10318}", "UpperFilters", Multi("kbdclass", "PECKP"));
        var e = Executor.RunStep(Step("service.delete_orphan", new() { ["name"] = "PECKP" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("红线 2", e.Message);
        Assert.True(sys.KeyExists(Hive.LocalMachine, Svc + "PECKP"));
    }

    [Fact]
    public void Orphan_still_running_is_refused_red_line_1()
    {
        AddService("TESMON", @"\SystemRoot\system32\drivers\TesMon.sys", start: 1);
        sys.ServiceStates["TESMON"] = "Running";
        var e = Executor.RunStep(Step("service.delete_orphan", new() { ["name"] = "TESMON" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("红线 1", e.Message);
        Assert.DoesNotContain(sys.Commands, c => c.StartsWith("sc.exe"));
    }

    [Fact]
    public void Orphan_whose_file_came_back_is_refused()
    {
        AddService("ew_usbccgpfilter", @"\SystemRoot\System32\drivers\ew_usbccgpfilter.sys", start: 3);
        sys.AddFile(@"C:\Windows\System32\drivers\ew_usbccgpfilter.sys", 1, 1);
        Assert.Equal(Status.Failed, Executor.RunStep(Step("service.delete_orphan", new() { ["name"] = "ew_usbccgpfilter" }), Ctx).Status);
    }

    // ------------------------------------------------------------------ 注册表开关
    [Fact]
    public void Edge_policy_and_undo_removes_values_that_did_not_exist()
    {
        var e = Executor.RunStep(Step("browser.disable_background", new()), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled")!.AsInt());
        Executor.Undo(e, Ctx);
        Assert.Null(sys.ReadValue(Hive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled"));
        Assert.Null(sys.ReadValue(Hive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled"));
    }

    [Fact]
    public void Fast_startup_off_keeps_hibernation_and_undo_restores()
    {
        const string k = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        sys.Set(Hive.LocalMachine, k, "HiberbootEnabled", RegValue.DWord(1));
        var e = Executor.RunStep(Step("power.disable_fast_startup", new()), Ctx);
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, k, "HiberbootEnabled")!.AsInt());
        Assert.DoesNotContain(sys.Commands, c => c.Contains("hibernate", StringComparison.OrdinalIgnoreCase));
        Executor.Undo(e, Ctx);
        Assert.Equal(1, sys.ReadValue(Hive.LocalMachine, k, "HiberbootEnabled")!.AsInt());
    }

    [Fact]
    public void Chrome_background_policy_written_verified_and_undone()
    {
        const string k = @"SOFTWARE\Policies\Google\Chrome";
        var e = Executor.RunStep(Step("browser.disable_chrome_background", new()), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal(0, sys.ReadValue(Hive.LocalMachine, k, "BackgroundModeEnabled")!.AsInt());
        Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
        Assert.Null(sys.ReadValue(Hive.LocalMachine, k, "BackgroundModeEnabled"));  // 原来没有这个策略：撤销后也没有
    }

    // ------------------------------------------------------------------ 清理缓存（按清理目录表）
    const string Local = @"C:\Users\u\AppData\Local";
    const string Chrome = Local + @"\Google\Chrome\User Data";

    PlanStep Clean(bool admin, int? age, params string[] categories)
    {
        var s = Step("cache.clean", new() { ["paths"] = new JsonArray(@"C:\Users\u\Documents") });  // 方案里塞的路径必须被忽略
        s.NeedsAdmin = admin;
        s.CacheChoice = new CacheChoice { Categories = [.. categories], Age = age };
        return s;
    }

    [Fact]
    public void Cache_clean_deletes_only_old_catalog_files_never_user_files()
    {
        sys.AddFile(Local + @"\Temp\old.tmp", 1 << 20, 40);
        sys.AddFile(Local + @"\Temp\sub\old2.tmp", 1 << 20, 10);
        sys.AddFile(Local + @"\Temp\new.tmp", 1 << 20, 2);          // 不到 7 天：可能是正在安装的程序在用
        sys.AddFile(Local + @"\Temp\locked.tmp", 1 << 20, 40);
        sys.Locked.Add(Local + @"\Temp\locked.tmp");
        sys.AddFile(Local + @"\pip\Cache\a.whl", 1 << 20, 400);     // 没选这一类
        sys.AddFile(@"C:\Users\u\Documents\thesis.docx", 1 << 20, 400);
        var e = Executor.RunStep(Clean(false, null, "temp-user"), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal([Local + @"\Temp\old.tmp", Local + @"\Temp\sub\old2.tmp"], sys.Deleted.OrderBy(x => x).ToArray());
        Assert.Empty(sys.RecycleBin);
        // 删掉的每个文件记在备份目录的清单里（界面上「查看全部文件」读它），记录里只存文件名
        var list = OpLists.PathOf(work, e.Backup["list"]?.ToString());
        Assert.NotNull(list);
        var lines = File.ReadAllLines(list!).Select(l => l.Split('\t')).ToList();
        Assert.Equal([Local + @"\Temp\old.tmp", Local + @"\Temp\sub\old2.tmp"], lines.Select(l => l[0]).OrderBy(x => x).ToArray());
        Assert.All(lines, l => Assert.Equal("temp-user", l[2]));
        Assert.Null(OpLists.PathOf(work, @"..\..\x.tsv"));
        Assert.True(sys.FileExists(@"C:\Users\u\Documents\thesis.docx"));
        Assert.True(sys.FileExists(Local + @"\pip\Cache\a.whl"));
        Assert.Contains("腾出了 2 MB", e.Message);
        Assert.Contains("1 个文件正在使用", e.Message);
        Assert.False(e.Reversible);
        Assert.Equal(1L << 20, Operations.Int(e.Backup["leftBytes"]));  // 重新测量：被占用的那个还在
        Assert.Equal(Status.Failed, Executor.Undo(e, Ctx).Status);
    }

    [Fact]
    public void Cache_clean_extra_age_limit_applies_on_top_of_catalog_default()
    {
        sys.AddFile(Local + @"\Temp\a.tmp", 1, 40);
        sys.AddFile(Local + @"\Temp\b.tmp", 1, 10);
        Executor.RunStep(Clean(false, 30, "temp-user"), Ctx);
        Assert.Equal([Local + @"\Temp\a.tmp"], sys.Deleted.ToArray());
    }

    [Fact]
    public void Cache_clean_rejects_missing_unknown_system_or_admin_categories()
    {
        sys.AddFile(Local + @"\Temp\old.tmp", 1, 400);
        Assert.Equal(Status.Failed, Executor.RunStep(Step("cache.clean", new()), Ctx).Status);
        Assert.Equal(Status.Failed, Executor.RunStep(Clean(false, null), Ctx).Status);
        Assert.Equal(Status.Failed, Executor.RunStep(Clean(true, null, "..\\Documents"), Ctx).Status);
        Assert.Equal(Status.Failed, Executor.RunStep(Clean(true, null, "windows-old"), Ctx).Status);  // B 类交给 Windows 自带的清理
        Assert.Equal(Status.Failed, Executor.RunStep(Clean(false, null, "temp-windows"), Ctx).Status); // 需要管理员的类别不在普通权限里清
        Assert.Empty(sys.Deleted);
    }

    [Fact]
    public void Cache_clean_browser_profiles_skip_running_browser_links_and_non_cache_data()
    {
        sys.AddFile(Chrome + @"\Default\Cache\Cache_Data\f_000001", 5 << 20, 0);
        sys.AddFile(Chrome + @"\Profile 1\Code Cache\js\x", 1 << 20, 0);
        sys.AddFile(Chrome + @"\Default\Cookies", 1, 0);
        sys.AddFile(Chrome + @"\Default\History", 1, 0);
        sys.AddFile(Chrome + @"\Default\Login Data", 1, 0);
        sys.AddFile(Chrome + @"\Linked\Cache\y", 1, 0);              // 联接点：可能指向别处，不进入
        sys.ReparseDirs.Add(Chrome + @"\Linked");
        sys.Running.Add("chrome");
        var e = Executor.RunStep(Clean(false, null, "browser-chrome"), Ctx);
        Assert.Empty(sys.Deleted);
        Assert.Contains("chrome 正在运行", e.Message);
        sys.Running.Clear();
        Executor.RunStep(Clean(false, null, "browser-chrome"), Ctx);
        Assert.Equal([Chrome + @"\Default\Cache\Cache_Data\f_000001", Chrome + @"\Profile 1\Code Cache\js\x"], sys.Deleted.OrderBy(x => x).ToArray());
        Assert.True(sys.FileExists(Chrome + @"\Default\Cookies") && sys.FileExists(Chrome + @"\Default\History") && sys.FileExists(Chrome + @"\Default\Login Data"));
        Assert.True(sys.FileExists(Chrome + @"\Linked\Cache\y"));
    }

    [Fact]
    public void Cache_clean_keeps_newest_minidump_and_memory_dmp_only_at_top_level()
    {
        sys.AddFile(@"C:\Windows\Minidump\a.dmp", 1, 30);
        sys.AddFile(@"C:\Windows\Minidump\b.dmp", 1, 20);
        sys.AddFile(@"C:\Windows\Minidump\c.dmp", 1, 3);
        sys.AddFile(@"C:\Windows\MEMORY.DMP", 1 << 30, 30);
        sys.AddFile(@"C:\Windows\System32\config\MEMORY.DMP", 1, 30);
        sys.AddFile(@"C:\Windows\notepad.exe", 1, 3000);
        var e = Executor.RunStep(Clean(true, null, "crash-dumps-system", "crash-dump-full"), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.Equal([@"C:\Windows\MEMORY.DMP", @"C:\Windows\Minidump\a.dmp", @"C:\Windows\Minidump\b.dmp"], sys.Deleted.OrderBy(x => x).ToArray());
        Assert.Contains("1 GB", e.Message);
    }

    // ------------------------------------------------------------------ 驱动包
    [Fact]
    public void Old_driver_package_deleted_without_force_and_undo_adds_back()
    {
        sys.AddFile(@"C:\Windows\INF\oem57.inf", 1, 100);
        var e = Executor.RunStep(Step("driver.delete_old_version", new() { ["publishedName"] = "oem57.inf", ["inf"] = "cui_dch.inf" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.DoesNotContain(sys.Commands, c => c.Contains("/force"));
        Assert.Contains(sys.Commands, c => c.StartsWith("pnputil.exe /export-driver oem57.inf"));
        Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
    }

    [Fact]
    public void Driver_in_use_is_protected_by_windows()
    {
        sys.AddFile(@"C:\Windows\INF\oem12.inf", 1, 100);
        sys.DriversInUse.Add("oem12.inf");
        var e = Executor.RunStep(Step("driver.delete_old_version", new() { ["publishedName"] = "oem12.inf" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("自我保护", e.Message);
    }

    [Fact]
    public void Driver_op_only_for_oem_packages()
    {
        sys.AddFile(@"C:\Windows\INF\usbport.inf", 1, 100);
        Assert.Equal(Status.Failed, Executor.RunStep(Step("driver.delete_old_version", new() { ["publishedName"] = "usbport.inf" }), Ctx).Status);
        Assert.Empty(sys.Commands);
    }

    // ------------------------------------------------------------------ 卸载
    [Fact]
    public void Uninstall_runs_vendor_uninstaller_and_verifies()
    {
        const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Thunder";
        sys.Set(Hive.LocalMachine, k, "DisplayName", Str("迅雷"));
        sys.Set(Hive.LocalMachine, k, "UninstallString", Str("\"C:\\Program Files (x86)\\Thunder\\uninst.exe\" /S"));
        sys.OnRun = (exe, args) => { Assert.Equal(@"C:\Program Files (x86)\Thunder\uninst.exe", exe); Assert.Equal("/S", args); sys.DeleteValue(Hive.LocalMachine, k, "DisplayName"); return 0; };
        var e = Executor.RunStep(Step("app.uninstall", new() { ["displayName"] = "迅雷" }), Ctx);
        Assert.Equal(Status.Done, e.Status);
        Assert.False(e.Reversible);
    }

    [Fact]
    public void Uninstall_cancelled_by_user_is_reported_plainly()
    {
        const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}";
        sys.Set(Hive.LocalMachine, k, "DisplayName", Str("某软件"));
        sys.Set(Hive.LocalMachine, k, "UninstallString", Str("MsiExec.exe /X{GUID}"));
        var e = Executor.RunStep(Step("app.uninstall", new() { ["displayName"] = "某软件" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("取消", e.Message);
        Assert.DoesNotContain("恢复原状", e.Message);
        Assert.Contains(sys.Commands, c => c == "MsiExec.exe /X{GUID}");
    }

    [Fact]
    public void Defender_scan_is_info_only()
    {
        var e = Executor.RunStep(Step("defender.scan", new()), Ctx);
        Assert.Equal(Status.Info, e.Status);
        Assert.Contains(sys.Commands, c => c.Contains("Start-MpScan -ScanType QuickScan"));
        Assert.DoesNotContain(sys.Commands, c => c.Contains("Set-MpPreference"));  // 不修改安全设置
    }

    [Fact]
    public void Unknown_operation_does_nothing()
    {
        var e = Executor.RunStep(Step("driver.remove_after_reboot", new() { ["name"] = "PECKP" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Empty(sys.Commands);
    }

    // ------------------------------------------------------------------ 代码审查发现的问题（2026-10-25）
    [Fact]
    public void Task_delete_uses_command_from_task_not_from_plan()
    {
        // 方案文件被改：把 Defender 的任务说成"残留"、程序路径填空。任务本身指向的程序还在，必须拒绝
        sys.Tasks[@"\Microsoft\Windows\Windows Defender\Scan"] = TaskXmlOf(true, @"C:\ProgramData\Microsoft\Windows Defender\MpCmdRun.exe");
        sys.AddFile(@"C:\ProgramData\Microsoft\Windows Defender\MpCmdRun.exe", 1, 1);
        var e = Executor.RunStep(Step("task.delete_leftover", new() { ["fullName"] = @"\Microsoft\Windows\Windows Defender\Scan", ["executable"] = "" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.True(sys.Tasks.ContainsKey(@"\Microsoft\Windows\Windows Defender\Scan"));
    }

    [Fact]
    public void Task_with_saved_password_is_not_deleted()
    {
        sys.Tasks[@"\Backup"] = TaskXmlOf(true, @"D:\gone\backup.exe", "Password");
        var e = Executor.RunStep(Step("task.delete_leftover", new() { ["fullName"] = @"\Backup" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.Contains("密码", e.Message);
    }

    [Fact]
    public void Tampered_targets_are_rejected_for_apply_and_undo()
    {
        const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe";
        var evil = new JsonObject { ["approvedHive"] = "HKLM", ["approvedKey"] = ifeo, ["valueName"] = "Debugger" };
        Assert.Equal(Status.Failed, Executor.RunStep(Step("startup.disable", evil), Ctx).Status);
        var entry = new JournalEntry { Op = "startup.disable", Status = Status.Done, Reversible = true, Target = evil,
            Backup = new JsonObject { ["approved"] = new RegValue("String", JsonValue.Create(@"C:\evil.exe")).ToJson() } };
        Assert.Equal(Status.Failed, Executor.Undo(entry, Ctx).Status);
        Assert.Null(sys.ReadValue(Hive.LocalMachine, ifeo, "Debugger"));
        Assert.Equal(Status.Failed, Executor.RunStep(Step("service.set_manual", new() { ["name"] = @"..\..\Control" }), Ctx).Status);
    }

    [Fact]
    public void Service_paths_with_environment_variables_are_expanded()
    {
        // %ProgramFiles% 没展开会被当成"文件不存在"，进而误删还在用的服务登记
        AddService("VendorSvc", @"%ProgramFiles%\Vendor\svc.exe", start: 3);
        sys.AddFile(@"C:\Program Files\Vendor\svc.exe", 1, 1);
        var e = Executor.RunStep(Step("service.delete_orphan", new() { ["name"] = "VendorSvc" }), Ctx);
        Assert.Equal(Status.Failed, e.Status);
        Assert.True(sys.KeyExists(Hive.LocalMachine, Svc + "VendorSvc"));
    }

    [Fact]
    public void Nsis_style_uninstaller_that_exits_early_is_waited_for()
    {
        const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo";
        sys.Set(Hive.LocalMachine, k, "DisplayName", Str("Foo"));
        sys.Set(Hive.LocalMachine, k, "UninstallString", new RegValue("ExpandString", JsonValue.Create(@"%ProgramFiles%\Foo\uninst.exe")));
        sys.OnRun = (exe, args) => { Assert.Equal(@"C:\Program Files\Foo\uninst.exe", exe); return 0; };  // 原进程马上退出
        var n = 0;
        sys.OnSleep = () => { if (++n == 3) sys.DeleteValue(Hive.LocalMachine, k, "DisplayName"); };  // 副本几秒后卸载完
        Assert.Equal(Status.Done, Executor.RunStep(Step("app.uninstall", new() { ["displayName"] = "Foo" }), Ctx).Status);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\A\\b.exe\" -k x", @"C:\Program Files\A\b.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\PECKP_x64.sys", @"C:\Windows\System32\drivers\PECKP_x64.sys")]
    [InlineData(@"system32\drivers\UniFairy_x64.sys", @"C:\Windows\system32\drivers\UniFairy_x64.sys")]
    [InlineData(@"\??\C:\Program Files\Huawei\PCManager\dokan2a.sys", @"C:\Program Files\Huawei\PCManager\dokan2a.sys")]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs", @"C:\Windows\System32\svchost.exe")]
    public void Image_path_resolution(string input, string expected) =>
        Assert.Equal(expected, Operations.ResolveImagePath(input, @"C:\Windows"));

    [Fact]
    public void Steps_of_one_execution_share_a_batch_for_one_click_restore()
    {
        sys.Tasks[@"\\A"] = TaskXmlOf(true);
        sys.Tasks[@"\\B"] = TaskXmlOf(true);
        var e1 = Executor.RunStep(Step("task.disable", new() { ["fullName"] = @"\\A" }), Ctx, batchId: "batch-1");
        var e2 = Executor.RunStep(Step("task.disable", new() { ["fullName"] = @"\\B" }), Ctx, batchId: "batch-1");
        Assert.Equal("batch-1", e1.BatchId);
        var store = new JournalStore(Path.Combine(work, "j"));
        store.Save(e1); store.Save(e2);
        var back = store.All().Where(e => e.BatchId == "batch-1").ToList();
        Assert.Equal(2, back.Count);
        foreach (var e in back) Assert.Equal(Status.Done, Executor.Undo(e, Ctx).Status);
        Assert.All(sys.Tasks.Values, x => Assert.Contains("<Enabled>true</Enabled>", x));
    }

    [Fact]
    public void Journal_store_roundtrip()
    {
        sys.Tasks[@"\T"] = TaskXmlOf(true);
        var e = Executor.RunStep(Step("task.disable", new() { ["fullName"] = @"\T" }), Ctx);
        var store = new JournalStore(Path.Combine(work, "journal"));
        store.Save(e);
        var back = store.Get(e.Id)!;
        Assert.Equal(e.Backup.ToJsonString(), back.Backup.ToJsonString());
        Assert.Single(store.All());
        Assert.Null(store.Get("..\\..\\evil"));
        Executor.Undo(back, Ctx);
        store.Save(back);
        Assert.Equal(Status.Undone, store.Get(e.Id)!.Status);
    }
}
