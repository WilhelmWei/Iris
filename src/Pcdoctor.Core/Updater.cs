using System.IO.Compression;
using System.Security.Cryptography;

namespace Pcdoctor.Core;

/// <summary>
/// 一键更新里和 Windows 无关的部分：核对下载的安装包、换上新版、回到上一版。
/// 换新版的做法：正在运行的 Iris.exe 不能覆盖，但可以改名。所以把它改名成 Iris.exe.old（留作"上一版"），
/// 新文件写成 Iris.exe，再由界面程序启动新版、自己退出。任何一步失败都恢复原样。
/// </summary>
public static class Updater
{
    public const string Exe = "Iris.exe";
    public const string Old = "Iris.exe.old";

    /// <summary>跟着 Iris.exe 一起放在免安装版文件夹里的说明文件：文件夹里原来有才更新。</summary>
    static readonly string[] Extras = ["LICENSE.txt", "PRIVACY.md", "使用说明.txt", "内测说明.txt"];

    public sealed record Package(string Version, byte[] ExeBytes, IReadOnlyDictionary<string, byte[]> ExtraFiles);

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// 核对下载的免安装版 zip：整个文件的 SHA-256 要和版本信息里的一致；zip 里要有 Iris-版本/Iris.exe，
    /// 而且和 zip 里 SHA256SUMS.txt 记的一致。不一致就抛出异常，什么也不改。
    /// </summary>
    public static Package Open(byte[] zip, string expectedSha256, string version)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) throw new InvalidDataException("版本信息里没有校验码，为了安全不自动更新");
        if (!Sha256(zip).Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载的文件和版本信息里的校验码对不上，可能下载不完整或被篡改，已丢弃");
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var prefix = $"Iris-{version}/";
        byte[] Read(string name)
        {
            var e = za.GetEntry(prefix + name) ?? throw new InvalidDataException($"安装包里缺少 {name}");
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        var exe = Read(Exe);
        var sums = System.Text.Encoding.UTF8.GetString(Read("SHA256SUMS.txt"));
        var line = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith("  " + Exe, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("安装包里的校验清单没有 Iris.exe");
        if (!line[..line.IndexOf(' ')].Equals(Sha256(exe), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包里的 Iris.exe 和它自带的校验码对不上，已丢弃");
        if (exe.Length < 2 || exe[0] != 'M' || exe[1] != 'Z') throw new InvalidDataException("安装包里的 Iris.exe 不是 Windows 程序");
        var extras = new Dictionary<string, byte[]>();
        foreach (var n in Extras)
            if (za.GetEntry(prefix + n) != null) extras[n] = Read(n);
        return new Package(version, exe, extras);
    }

    /// <summary>换上新版。dir 是 Iris.exe 所在的文件夹。</summary>
    public static void Install(string dir, Package p)
    {
        string exe = Path.Combine(dir, Exe), old = Path.Combine(dir, Old), tmp = Path.Combine(dir, "Iris.exe.new");
        if (!File.Exists(exe)) throw new FileNotFoundException("找不到正在运行的 Iris.exe");
        File.WriteAllBytes(tmp, p.ExeBytes);
        try
        {
            if (!Sha256(File.ReadAllBytes(tmp)).Equals(Sha256(p.ExeBytes), StringComparison.Ordinal)) throw new IOException("写入新版时出错");
            if (File.Exists(old)) File.Delete(old);  // 只保留一个上一版
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        foreach (var (n, data) in p.ExtraFiles)
        {
            var f = Path.Combine(dir, n);
            try { if (File.Exists(f)) File.WriteAllBytes(f, data); } catch { /* 说明文件写不进去不影响程序 */ }
        }
    }

    public static bool CanRollback(string dir) => File.Exists(Path.Combine(dir, Old)) && File.Exists(Path.Combine(dir, Exe));

    /// <summary>回到上一版：Iris.exe 和 Iris.exe.old 对调（对调后还能再换回来）。</summary>
    public static void Rollback(string dir)
    {
        string exe = Path.Combine(dir, Exe), old = Path.Combine(dir, Old), swap = Path.Combine(dir, "Iris.exe.swap");
        if (!CanRollback(dir)) throw new FileNotFoundException("没有上一版可以回去");
        if (File.Exists(swap)) File.Delete(swap);
        File.Move(exe, swap);
        try { File.Move(old, exe); }
        catch { File.Move(swap, exe); throw; }
        File.Move(swap, old);
    }
}
