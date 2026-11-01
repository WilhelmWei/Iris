using System.IO.Compression;
using System.Text;
using Pcdoctor.Core;
using Xunit;

namespace Pcdoctor.Core.Tests;

public class UpdaterTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "iris-update-" + Guid.NewGuid().ToString("N"));
    public UpdaterTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static byte[] FakeExe(string tag) => Encoding.ASCII.GetBytes("MZ" + tag);

    /// <summary>和 tools/build_app.py 生成的免安装版一样的结构：Iris-版本/ 下面是 Iris.exe、说明文件和 SHA256SUMS.txt。</summary>
    static byte[] Zip(string version, byte[] exe, string? sumsFor = null, bool withPrivacy = true)
    {
        var files = new Dictionary<string, byte[]> { ["Iris.exe"] = exe };
        if (withPrivacy) files["PRIVACY.md"] = Encoding.UTF8.GetBytes("privacy " + version);
        var sums = string.Join("", files.Select(kv => $"{Updater.Sha256(sumsFor == null ? kv.Value : Encoding.ASCII.GetBytes(sumsFor))}  {kv.Key}\r\n"));
        files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums);
        var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, d) in files)
            {
                using var s = za.CreateEntry($"Iris-{version}/{n}").Open();
                s.Write(d);
            }
        return ms.ToArray();
    }

    [Fact]
    public void Install_keeps_old_version_and_rollback_swaps_back()
    {
        File.WriteAllBytes(Path.Combine(dir, "Iris.exe"), FakeExe("v1"));
        File.WriteAllText(Path.Combine(dir, "PRIVACY.md"), "privacy old");
        var zip = Zip("1.0.0-beta.10", FakeExe("v2"));
        var p = Updater.Open(zip, Updater.Sha256(zip), "1.0.0-beta.10");
        Updater.Install(dir, p);
        Assert.Equal(FakeExe("v2"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe")));
        Assert.Equal(FakeExe("v1"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe.old")));
        Assert.Equal("privacy 1.0.0-beta.10", File.ReadAllText(Path.Combine(dir, "PRIVACY.md")));
        Assert.False(File.Exists(Path.Combine(dir, "Iris.exe.new")));

        Assert.True(Updater.CanRollback(dir));
        Updater.Rollback(dir);
        Assert.Equal(FakeExe("v1"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe")));
        Assert.Equal(FakeExe("v2"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe.old")));  // 还能再换回新版
    }

    [Fact]
    public void Extra_files_are_only_updated_when_already_there()
    {
        File.WriteAllBytes(Path.Combine(dir, "Iris.exe"), FakeExe("v1"));
        var zip = Zip("2.0.0", FakeExe("v2"));
        Updater.Install(dir, Updater.Open(zip, Updater.Sha256(zip), "2.0.0"));
        Assert.False(File.Exists(Path.Combine(dir, "PRIVACY.md")));  // 安装版的文件夹里没有说明文件，不往里加
    }

    [Fact]
    public void Wrong_checksums_are_rejected_and_nothing_changes()
    {
        File.WriteAllBytes(Path.Combine(dir, "Iris.exe"), FakeExe("v1"));
        var zip = Zip("2.0.0", FakeExe("v2"));
        Assert.Throws<InvalidDataException>(() => Updater.Open(zip, "00", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Updater.Open(zip, "", "2.0.0"));
        var bad = Zip("2.0.0", FakeExe("v2"), sumsFor: "something else");
        Assert.Throws<InvalidDataException>(() => Updater.Open(bad, Updater.Sha256(bad), "2.0.0"));
        var notExe = Zip("2.0.0", Encoding.ASCII.GetBytes("hello"));
        Assert.Throws<InvalidDataException>(() => Updater.Open(notExe, Updater.Sha256(notExe), "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Updater.Open(zip, Updater.Sha256(zip), "3.0.0"));  // 版本对不上，找不到文件
        Assert.Equal(FakeExe("v1"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe")));
        Assert.False(Updater.CanRollback(dir));
    }

    [Fact]
    public void Second_update_replaces_the_previous_old_copy()
    {
        File.WriteAllBytes(Path.Combine(dir, "Iris.exe"), FakeExe("v1"));
        foreach (var (v, tag) in new[] { ("2.0.0", "v2"), ("3.0.0", "v3") })
        {
            var zip = Zip(v, FakeExe(tag));
            Updater.Install(dir, Updater.Open(zip, Updater.Sha256(zip), v));
        }
        Assert.Equal(FakeExe("v3"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe")));
        Assert.Equal(FakeExe("v2"), File.ReadAllBytes(Path.Combine(dir, "Iris.exe.old")));
    }
}
