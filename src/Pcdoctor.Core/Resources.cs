using System.Reflection;

namespace Pcdoctor.Core;

/// <summary>读取嵌入的资源（kb/seed.json、prompts/judge-prompt.md、collector/pcdoctor-collect.ps1，见 csproj）。</summary>
public static class Resources
{
    public const string SeedJson = "seed.json";
    public const string JudgePrompt = "judge-prompt.md";
    public const string CollectorScript = "pcdoctor-collect.ps1";

    public static string ReadText(string logicalName)
    {
        using var s = typeof(Resources).Assembly.GetManifestResourceStream(logicalName)
                      ?? throw new FileNotFoundException($"找不到嵌入资源 {logicalName}");
        using var r = new StreamReader(s, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /// <summary>采集脚本原文（桌面程序可以写到临时目录后运行）。</summary>
    public static string CollectorScriptText() => ReadText(CollectorScript);

    /// <summary>采集脚本原始字节（保留 UTF-8 BOM：Windows PowerShell 5.1 靠它识别编码）。</summary>
    public static byte[] CollectorScriptBytes()
    {
        using var s = typeof(Resources).Assembly.GetManifestResourceStream(CollectorScript)
                      ?? throw new FileNotFoundException($"找不到嵌入资源 {CollectorScript}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
