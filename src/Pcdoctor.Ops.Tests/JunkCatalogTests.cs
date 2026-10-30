using Pcdoctor.Ops;
using Xunit;

namespace Pcdoctor.Ops.Tests;

/// <summary>清理目录表的安全检查：每条路径都只能落在缓存里，不能指向用户自己的文件夹。改 kb/junk.json 时这些测试兜底。</summary>
public class JunkCatalogTests
{
    static readonly Dictionary<string, string> Roots = new()
    {
        ["TEMP"] = @"C:\Users\u\AppData\Local\Temp", ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local", ["APPDATA"] = @"C:\Users\u\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\u", ["PROGRAMDATA"] = @"C:\ProgramData", ["WINDIR"] = @"C:\Windows", ["SYSTEMDRIVE"] = "C:",
        ["STEAM"] = @"C:\Program Files (x86)\Steam",
    };
    /// <summary>这些位置本身不能整个清（只允许不递归、按确切文件名清，例如 C:\Windows\MEMORY.DMP）。</summary>
    static readonly string[] WholeRootsForbidden = ["LOCALAPPDATA", "APPDATA", "USERPROFILE", "PROGRAMDATA", "WINDIR", "SYSTEMDRIVE", "STEAM"];
    static readonly string[] UserFolders = ["Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music", "OneDrive", "WeChat Files", "Tencent Files", "xwechat_files"];

    static IEnumerable<(JunkCategory cat, string path)> AllPaths() => Junk.Catalog.Categories.SelectMany(c => c.Paths.Select(p => (c, p)));

    [Fact]
    public void Catalog_is_well_formed()
    {
        var cats = Junk.Catalog.Categories;
        Assert.Equal("iris.junk/1", Junk.Catalog.Schema);
        Assert.True(cats.Count >= 20);
        Assert.Equal(cats.Count, cats.Select(c => c.Id).Distinct().Count());
        Assert.All(cats, c =>
        {
            Assert.Matches("^[a-z0-9-]+$", c.Id);
            Assert.Contains(c.Tier, new[] { "A", "B" });
            Assert.False(string.IsNullOrWhiteSpace(c.Name + c.Group));
            Assert.False(string.IsNullOrWhiteSpace(c.Why), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Cost), c.Id);
            Assert.NotEmpty(c.Paths);
        });
        Assert.All(Junk.Catalog.NotCleaned, n => Assert.False(string.IsNullOrWhiteSpace(n.Why)));
        Assert.Contains(Junk.Catalog.NotCleaned, n => n.Id == "recycle-bin");  // 红线 6：不清空回收站
    }

    [Fact]
    public void Every_path_starts_with_an_allowed_variable_and_wildcards_are_whole_segments()
    {
        foreach (var (c, p) in AllPaths())
        {
            var m = System.Text.RegularExpressions.Regex.Match(p, @"^%(\w+)%(\\.*)?$");
            Assert.True(m.Success, $"{c.Id}: {p}");
            Assert.Contains(m.Groups[1].Value, Junk.Vars);
            foreach (var seg in p.Split('\\').Skip(1))
            {
                Assert.NotEqual("..", seg);
                Assert.True(seg == "*" || !seg.Contains('*') && !seg.Contains('?'), $"{c.Id}: {p}");
            }
        }
    }

    [Fact]
    public void No_path_is_a_whole_profile_or_system_root_or_a_user_folder()
    {
        foreach (var (c, p) in AllPaths())
        {
            var m = System.Text.RegularExpressions.Regex.Match(p, @"^%(\w+)%(.*)$");
            var rest = m.Groups[2].Value.Trim('\\');
            if (rest.Length == 0 && WholeRootsForbidden.Contains(m.Groups[1].Value))
            {
                // 整个系统目录只能不递归、按确切文件名清
                Assert.False(c.Recurse, $"{c.Id}: {p}");
                Assert.NotNull(c.Include);
                Assert.All(c.Include!, i => Assert.DoesNotContain('*', i));
            }
            var full = Roots[m.Groups[1].Value] + (rest.Length > 0 ? "\\" + rest : "");
            foreach (var u in UserFolders)
                Assert.DoesNotContain("\\" + u, full, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\System32", full, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\Installer", full, StringComparison.OrdinalIgnoreCase);  // 安装程序缓存直接删会让软件卸载不掉
            Assert.DoesNotContain("Recycle", full, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Browser_categories_only_touch_cache_folders_and_wait_for_the_browser_to_close()
    {
        foreach (var c in Junk.Catalog.Categories.Where(c => c.Id.StartsWith("browser-")))
        {
            Assert.Equal("A", c.Tier);
            Assert.NotEmpty(c.CloseFirst!);
            Assert.All(c.Paths, p => Assert.Contains("cache", p.Split('\\').Last(), StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Admin_flag_matches_location()
    {
        // Windows 目录和 ProgramData 下的要管理员；用户目录下的不要（只清自己账户的缓存时不弹管理员确认）
        foreach (var c in Junk.Catalog.Categories.Where(c => c.Tier == "A"))
        {
            var system = c.Paths.Any(p => p.StartsWith("%WINDIR%") || p.StartsWith("%PROGRAMDATA%") || p.StartsWith("%SYSTEMDRIVE%"));
            Assert.Equal(system, c.Admin);
        }
    }

    [Fact]
    public void Expand_and_name_matching()
    {
        var sys = new FakeWinSystem();
        sys.AddFile(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\Default\Cache\a", 1, 1);
        sys.AddFile(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\Profile 2\Cache\b", 1, 1);
        sys.AddFile(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\J\Cache\c", 1, 1);
        sys.ReparseDirs.Add(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\J");
        var got = Junk.Expand(sys, @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache").OrderBy(x => x).ToList();
        Assert.Equal([@"C:\Users\u\AppData\Local\Google\Chrome\User Data\Default\Cache", @"C:\Users\u\AppData\Local\Google\Chrome\User Data\Profile 2\Cache"], got);
        Assert.Empty(Junk.Expand(sys, @"%NOPE%\x"));
        Assert.Empty(Junk.Expand(sys, @"%LOCALAPPDATA%\..\Roaming"));
        Assert.True(Junk.NameMatches("thumbcache_256.db", ["thumbcache_*.db"]));
        Assert.False(Junk.NameMatches("iconcache_256.db", ["thumbcache_*.db"]));
        Assert.True(Junk.NameMatches("MEMORY.DMP", ["memory.dmp"]));
        Assert.False(Junk.NameMatches("MEMORY.DMP.bak", ["MEMORY.DMP"]));
    }
}
