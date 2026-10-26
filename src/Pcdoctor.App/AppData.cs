using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pcdoctor.App;

/// <summary>所有数据都在 %LOCALAPPDATA%\pcdoctor 下，只在本机：
///   reports\&lt;编号&gt;\  report.json（原始采集，未打码）、judge.json、evidence.json（发给模型的内容）、meta.json、answers.json
///   journal\         操作记录
///   runs\            和管理员辅助进程交换文件的临时目录（用完删除）
///   outbox\          待发送给 Iris 服务器的内测反馈包（用户同意后才会生成，发送成功即删除）
///   known.json       本机"用户确认过的结论"（格式同 pcdoctor-known.json）
///   error.log        意外错误的记录（内测反馈包会带上最后一段，先打码）
///   settings.json    设置；API Key 用 DPAPI 加密，只有当前 Windows 账户能解开</summary>
public static class AppData
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pcdoctor");
    public static string Reports => Dir("reports");
    public static string Journal => Dir("journal");
    public static string Runs => Dir("runs");
    public static string WebView => Dir("webview");
    public static string Outbox => Dir("outbox");
    public static string ErrorLog => Path.Combine(Root, "error.log");
    public static string KnownFile => Path.Combine(Root, "known.json");
    static string SettingsFile => Path.Combine(Root, "settings.json");

    static string Dir(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Version =>
        typeof(AppData).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    // ------------------------------------------------------------------ 设置
    public sealed class Settings
    {
        public string Provider { get; set; } = "deepseek";
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-chat";
        /// <summary>研判方式：ai / offline。内测版默认纯规则，填了 Key 之后才改成 AI。</summary>
        public string Mode { get; set; } = "offline";
        public string? ApiKeyProtected { get; set; }
        /// <summary>用户同意过的隐私政策版本；和当前版本不同（或从未同意）时，启动后先展示政策。</summary>
        public string? PrivacyAccepted { get; set; }
        /// <summary>是否允许把证据包发给 Iris 托管服务。默认关闭：关闭时只用用户自己的 Key 或只做本地检查。</summary>
        public bool AllowHosted { get; set; }

        // 外观与界面
        /// <summary>dark（余烬）/ space（深空）/ light（米白）/ auto（跟随系统）</summary>
        public string Theme { get; set; } = "dark";
        public bool ReduceMotion { get; set; }
        public bool Pro { get; set; }
        public bool DemoMode { get; set; }
        public bool DefaultScan { get; set; }
        public bool ShowLessons { get; set; } = true;
        public int LessonIndex { get; set; }
        public bool ShowTips { get; set; } = true;
        /// <summary>首次使用的快速设定是否走完（或跳过）。</summary>
        public bool Onboarded { get; set; }

        // 内测
        /// <summary>是否同意把去隐私的反馈包发送到 Iris 服务器：null 还没问，"yes" 同意，"no" 不同意（不同意照样能用）。</summary>
        public string? BetaConsent { get; set; }
        /// <summary>随机安装编号，只用来把同一台电脑的反馈归到一起、防止被刷，和身份无关。</summary>
        public string? InstallId { get; set; }
    }

    public static readonly string[] Themes = ["dark", "space", "light", "auto"];

    public static string InstallId(Settings s)
    {
        if (string.IsNullOrEmpty(s.InstallId)) { s.InstallId = Guid.NewGuid().ToString("N"); SaveSettings(s); }
        return s.InstallId;
    }

    // ------------------------------------------------------------------ 隐私政策（随程序内嵌，版本号取自政策正文）
    public static string PrivacyText { get; } = ReadResource("PRIVACY.md");
    public static string PrivacyVersion { get; } =
        System.Text.RegularExpressions.Regex.Match(PrivacyText, @"版本 (\d{4}-\d{2}-\d{2}(?:\.\d+)?)").Groups[1].Value;

    static string ReadResource(string name)
    {
        using var s = typeof(AppData).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>托管服务的地址。服务还没有上线，所以是 null；上线后由新版本填入。</summary>
    public const string? HostedBaseUrl = null;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("pcdoctor-api-key");

    public static Settings LoadSettings()
    {
        try { if (File.Exists(SettingsFile)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile), Json) ?? new(); }
        catch { /* 设置文件损坏就用默认值 */ }
        return new();
    }

    public static void SaveSettings(Settings s) => WriteAtomic(SettingsFile, JsonSerializer.Serialize(s, Json));

    public static void SetApiKey(Settings s, string key) =>
        s.ApiKeyProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    public static string? GetApiKey(Settings s)
    {
        if (string.IsNullOrEmpty(s.ApiKeyProtected)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ApiKeyProtected), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public static JsonObject SettingsForUi(Settings s)
    {
        var key = GetApiKey(s);
        return new JsonObject
        {
            ["provider"] = s.Provider, ["baseUrl"] = s.BaseUrl, ["model"] = s.Model, ["mode"] = s.Mode,
            ["hasKey"] = key != null, ["keyTail"] = key is { Length: >= 4 } ? key[^4..] : "",
            ["allowHosted"] = s.AllowHosted, ["hostedAvailable"] = HostedBaseUrl != null,
            ["theme"] = s.Theme, ["reduceMotion"] = s.ReduceMotion, ["pro"] = s.Pro, ["demoMode"] = s.DemoMode, ["defaultScan"] = s.DefaultScan,
            ["showLessons"] = s.ShowLessons, ["lessonIndex"] = s.LessonIndex, ["showTips"] = s.ShowTips, ["onboarded"] = s.Onboarded,
            ["betaConsent"] = s.BetaConsent, ["feedbackServer"] = Cloud.FeedbackUrl != null,
        };
    }

    /// <summary>把没有预料到的错误记进 error.log（只在本机；内测反馈包会带上最后一段，先打码）。</summary>
    public static void LogError(string where, Exception ex)
    {
        try { File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Iris {Version} [{where}] {ex}\n\n"); } catch { }
    }

    // ------------------------------------------------------------------ 文件
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static JsonNode? ReadJson(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;

    public static void WriteJson(string path, JsonNode node) =>
        WriteAtomic(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    /// <summary>报告编号只允许数字、字母和短横线，防止路径穿越。</summary>
    public static string? ReportDir(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[A-Za-z0-9-]{1,64}$") ? Path.Combine(Reports, id!) : null;
}
