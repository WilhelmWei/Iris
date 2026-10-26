[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace Pcdoctor.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // 管理员辅助进程：不显示窗口，做完就退出
        if (args.Length > 0 && args[0] == "--helper") return Helper.Run(args);

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);
        Application.Run(new MainForm());
        return 0;
    }

    static void ShowError(Exception? ex)
    {
        try
        {
            File.AppendAllText(AppData.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Iris {AppData.Version} {ex}\n\n");
            MessageBox.Show("出现了一个意外错误，已经记在 " + AppData.ErrorLog + "。\n\n如果你参加了内测，下次发送反馈时会自动带上这段记录（已打码）；" +
                            "也可以在「设置 → 内测反馈」里导出反馈包发给作者。\n\n" + ex?.Message,
                            "Iris", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
