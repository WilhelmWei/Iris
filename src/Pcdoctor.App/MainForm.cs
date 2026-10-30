using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Pcdoctor.App;

/// <summary>主窗口：一个 WebView2 填满窗口，界面文件从程序内嵌资源里读（https://pcdoctor.example/ 只是本地虚拟地址，不联网；
/// .example 是保留后缀，不会被任何真实网站注册）。
/// 界面发来的 {id, method, params} 交给 Services 处理，回 {id, result} / {id, error}；Services 的事件推成 {event, data}。</summary>
public sealed class MainForm : Form
{
    const string Origin = "https://pcdoctor.example/";
    IntPtr hwnd;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly Services services;

    public MainForm()
    {
        Text = "Iris";
        StartPosition = FormStartPosition.Manual;
        BackColor = DarkTheme() ? Color.FromArgb(12, 11, 12) : Color.FromArgb(244, 241, 235);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(web);
        services = new Services(Emit, () => hwnd) { Confirm = ConfirmChanges, Foreground = BringBack };
        Load += async (_, _) => await InitWeb();
    }

    /// <summary>管理员确认之后把窗口拉回前台；最小化了就还原。</summary>
    void BringBack()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (GetForegroundWindow() != Handle) { Activate(); SetForegroundWindow(Handle); }
        });
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// <summary>界面主题：余烬、深空是深色；米白是浅色；跟随系统时看 Windows 的设置。</summary>
    static bool DarkTheme()
    {
        var t = AppData.LoadSettings().Theme;
        return t == "light" ? false : t == "auto" ? SystemDark() : true;
    }

    static bool SystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0; }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>关闭窗口时让还在扫 C 盘的辅助进程停下，已经扫到的部分存进报告（最多等 2 秒）。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { services.StopSpaceScans(); } catch { }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hwnd = Handle;
        // 窗口大小按逻辑像素算（高缩放比例下不会太小），并且不超出屏幕可用区域（1366×768 的小屏也放得下）
        var wa = Screen.FromHandle(Handle).WorkingArea;
        var want = LogicalToDeviceUnits(new Size(1240, 840));
        var min = LogicalToDeviceUnits(new Size(960, 640));
        MinimumSize = new Size(Math.Min(min.Width, wa.Width), Math.Min(min.Height, wa.Height));
        Size = new Size(Math.Min(want.Width, wa.Width), Math.Min(want.Height, wa.Height));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        if (DarkTheme()) { var on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); }  // 深色标题栏
    }

    async Task InitWeb()
    {
        try
        {
            Services.ClearWebViewCacheIfRequested();
            var env = await CoreWebView2Environment.CreateAsync(null, AppData.WebView);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "这台电脑缺少 Microsoft Edge WebView2 运行时（Windows 11 自带，Windows 10 一般随 Edge 安装）。\n\n" +
                "请到 https://developer.microsoft.com/microsoft-edge/webview2/ 下载\"常青版引导程序\"安装后再打开。\n\n" + ex.Message,
                "Iris", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("PCDOCTOR_DEVTOOLS") == "1";
        core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreDevToolsEnabled;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeEmbedded;
        core.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
        core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith(Origin)) { a.Cancel = true; OpenExternal(a.Uri); } };
        core.WebMessageReceived += (_, a) => { if (a.Source.StartsWith(Origin, StringComparison.Ordinal)) _ = HandleMessage(a.WebMessageAsJson); };
        core.Navigate(Origin + "index.html");
        services.StartBackgroundTasks();
    }

    static void OpenExternal(string uri)
    {
        if (uri.StartsWith("https://") || uri.StartsWith("http://")) Services.OpenUri(uri);
    }

    void ServeEmbedded(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        var s = typeof(MainForm).Assembly.GetManifestResourceStream("web/" + path);
        var type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml", ".png" => "image/png", ".woff2" => "font/woff2", ".json" => "application/json", _ => "application/octet-stream",
        };
        e.Response = s == null
            ? web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
            : web.CoreWebView2.Environment.CreateWebResourceResponse(s, 200, "OK",
                $"Content-Type: {type}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'");
    }

    /// <summary>每一次修改系统之前的确认框（Windows 原生对话框）：逐项写清改什么、能不能撤销。默认按钮是"取消"。</summary>
    bool ConfirmChanges(string heading, IReadOnlyList<Services.ChangeItem> items)
    {
        var lines = items.Select((it, i) =>
            $"{i + 1}. {it.Title}\n    会改：{it.Change}\n    " + (it.Reversible ? $"可以撤销：{it.Undo}" : $"不能自动撤销：{it.Undo}"));
        var admin = items.Any(i => i.NeedsAdmin) ? "\n\n点\"确认\"后会再弹出一次 Windows 的管理员确认。" : "";
        var irreversible = items.Count(i => !i.Reversible);
        bool ok = false;
        Invoke(() =>
        {
            var confirm = new TaskDialogButton("确认") { };
            var cancel = TaskDialogButton.Cancel;
            var page = new TaskDialogPage
            {
                Caption = "Iris",
                Heading = heading,
                Text = string.Join("\n\n", lines) + admin,
                Icon = irreversible > 0 ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
                Footnote = irreversible > 0 ? new TaskDialogFootnote($"其中 {irreversible} 项不能自动撤销，请确认后再继续。") : null,
                Buttons = { confirm, cancel },
                DefaultButton = cancel,
                AllowCancel = true,
                SizeToContent = true,
            };
            ok = TaskDialog.ShowDialog(this, page) == confirm;
        });
        return ok;
    }

    static readonly HashSet<string> AllowedHosts =
    [
        "github.com", "platform.deepseek.com", "api-docs.deepseek.com", "platform.moonshot.cn", "bailian.console.aliyun.com",
        "help.aliyun.com", "developer.microsoft.com",
    ];

    /// <summary>标题栏跟着主题变深 / 变浅（Windows 10 1809 起支持；不支持时没有效果，也不报错）。</summary>
    void SetTitleBar(bool dark)
    {
        Invoke(() =>
        {
            var on = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            BackColor = dark ? Color.FromArgb(12, 11, 12) : Color.FromArgb(244, 241, 235);
        });
    }

    void Emit(string evt, JsonNode? data) => Post(new JsonObject { ["event"] = evt, ["data"] = data });

    void Post(JsonObject msg)
    {
        var json = msg.ToJsonString();
        if (IsDisposed) return;
        try { BeginInvoke(() => { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); }); }
        catch (InvalidOperationException) { /* 窗口正在关闭 */ }
    }

    async Task HandleMessage(string json)
    {
        var msg = JsonNode.Parse(json);
        var id = msg?["id"]?.DeepClone();
        var method = msg?["method"]?.ToString() ?? "";
        var p = msg?["params"] ?? new JsonObject();
        try
        {
            var result = await Task.Run(() => Dispatch(method, p));
            Post(new JsonObject { ["id"] = id, ["result"] = result });
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException a ? a.InnerExceptions[0] : ex;
            if (inner is not (InvalidOperationException or ArgumentException)) AppData.LogError(method, inner);  // 预料之中的提示不记
            Post(new JsonObject { ["id"] = id, ["error"] = inner.Message });
        }
    }

    async Task<JsonNode?> Dispatch(string method, JsonNode p)
    {
        string S(string k) => p[k]?.ToString() ?? "";
        switch (method)
        {
            case "getState": return services.GetState();
            case "startCheckup": return services.StartCheckup(S("mode"), p["scan"]?.GetValue<bool>() == true);
            case "cancel": services.Cancel(); return new JsonObject();
            case "getReport": return services.GetReport(S("id"));
            case "rejudge": return await services.Rejudge(S("id"), S("mode"));
            case "answer": return services.Answer(S("reportId"), S("key"), S("answer"), S("note"));
            case "buildPlan": return services.BuildPlan(S("reportId"), p["keys"]!.AsArray().Select(x => x!.ToString()));
            case "executePlan": return services.ExecutePlan(S("planId"), S("reportId"), p["steps"]!.AsArray());
            case "getJournal": return services.GetJournal();
            case "undo": return await services.Undo(p["id"]?.ToString(), p["batchId"]?.ToString());
            case "saveSettings": return services.SaveSettings(p);
            case "getPrivacy": return services.GetPrivacy();
            case "acceptPrivacy": return services.AcceptPrivacy();
            case "declinePrivacy": BeginInvoke(Close); return new JsonObject();
            case "clearAllData": return await services.ClearAllData();
            case "openUrl":
                // 只打开固定的几个网站：项目主页、AI 厂商申请 Key 的页面、WebView2 下载页
                if (Uri.TryCreate(S("url"), UriKind.Absolute, out var u) && u.Scheme == "https" && AllowedHosts.Contains(u.Host)) Services.OpenUri(u.AbsoluteUri);
                return new JsonObject();
            case "openUpdate": services.OpenUpdate(); return new JsonObject();
            case "setWindowTheme": SetTitleBar(p["dark"]?.GetValue<bool>() == true); return new JsonObject();
            case "markFinding": return services.MarkFinding(S("reportId"), S("key"), S("kind"), S("note"));
            case "storageInfo": return services.StorageInfo();
            case "cleanStorage": return await services.CleanStorage(S("kind"), p["days"] is JsonValue dv && dv.TryGetValue<int>(out var dd) ? dd : 90);
            case "setSelected": return services.SetSelected(S("reportId"), p["keys"]!.AsArray().Select(x => x?.ToString() ?? ""));
            case "setDismissed": return services.SetDismissed(S("reportId"), S("key"), p["on"]?.GetValue<bool>() == true);
            case "sendFeedback": return await services.SendFeedback(S("reportId"));
            case "exportFeedback": return services.ExportFeedback(S("reportId"));
            case "previewFeedback": return services.PreviewFeedback(p["reportId"]?.ToString());
            case "testConnection": return await services.TestConnection();
            case "exportReport": return services.ExportReport(S("id"));
            case "openDefender": Services.OpenUri("windowsdefender://threat"); return new JsonObject();
            case "openPath": Services.OpenUri(AppData.Root); return new JsonObject();
            case "openReportFile":
            {
                string? file = null;
                Invoke(() =>
                {
                    using var dlg = new OpenFileDialog
                    {
                        Title = "打开朋友发来的体检报告", Filter = "体检报告 (pcdoctor-report-*.json)|pcdoctor-report-*.json|JSON 文件 (*.json)|*.json",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK) file = dlg.FileName;
                });
                return file == null ? new JsonObject() : await services.ImportReport(file);
            }
            default: throw new InvalidOperationException("未知方法 " + method);
        }
    }
}
