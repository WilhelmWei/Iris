using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Core;

/// <summary>
/// 隐私打码（移植自 collector/pcdoctor-collect.ps1 的 Hide-Private）：用户名、计算机名、邮箱。
/// 桌面程序本机采集时用 -NoRedact（本机报告保留原样），发给模型前在这里打码。
/// 采集脚本是对整份 JSON 文本做替换；这里逐个字符串（含对象的键）替换，效果相同，
/// 只是不会受 JSON 转义影响（例如紧跟在 \n 后面的用户名也能打码）。
/// </summary>
public static class Redactor
{
    public const string UserMark = "[用户]";
    public const string ComputerMark = "[计算机名]";
    public const string EmailMark = "[邮箱]";

    static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    static Regex? UserRx(string? user) => string.IsNullOrWhiteSpace(user) ? null
        : new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(user) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static Regex? ComputerRx(string? computer) => string.IsNullOrWhiteSpace(computer) ? null
        : new Regex(@"(?<![A-Za-z0-9-])" + Regex.Escape(computer) + @"(?![A-Za-z0-9-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Hide-Private：用户名或计算机名为空时跳过（否则空正则会在每个字符之间插入替换）。
    /// </summary>
    public static string HidePrivate(string text, string? user, string? computer) =>
        Hide(text, UserRx(user), ComputerRx(computer));

    static string Hide(string text, Regex? u, Regex? c)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (u is not null) text = u.Replace(text, UserMark);
        if (c is not null) text = c.Replace(text, ComputerMark);
        return Email.Replace(text, EmailMark);
    }

    /// <summary>
    /// 递归处理报告里所有字符串（包括对象的键），并把 tool.redacted 设为 true。原地修改，返回同一个节点。
    /// </summary>
    public static JsonNode RedactReport(JsonNode report, string user, string? computer)
    {
        var u = UserRx(user);
        var c = ComputerRx(computer);
        var result = Walk(report, u, c) ?? report;
        if (result is JsonObject o)
        {
            if (o["tool"] is not JsonObject tool)
            {
                tool = new JsonObject();
                o["tool"] = tool;
            }
            tool["redacted"] = true;
        }
        return result;
    }

    /// <summary>处理一个节点；字符串返回新节点（调用方替换），容器原地修改后返回 null。</summary>
    static JsonNode? Walk(JsonNode? n, Regex? u, Regex? c)
    {
        switch (n)
        {
            case JsonObject o:
            {
                var props = o.ToList();
                bool renamed = props.Any(kv => Hide(kv.Key, u, c) != kv.Key);
                if (renamed)
                {
                    o.Clear();
                    foreach (var (k, v) in props)
                    {
                        var nv = Walk(v, u, c) ?? v;
                        o[Hide(k, u, c)] = nv;
                    }
                }
                else
                {
                    foreach (var (k, v) in props)
                    {
                        var nv = Walk(v, u, c);
                        if (nv is not null) o[k] = nv;
                    }
                }
                return null;
            }
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    var nv = Walk(a[i], u, c);
                    if (nv is not null) a[i] = nv;
                }
                return null;
            case JsonValue v when Py.S(v) is { } s:
            {
                var h = Hide(s, u, c);
                return h == s ? null : JsonValue.Create(h);
            }
        }
        return null;
    }
}
