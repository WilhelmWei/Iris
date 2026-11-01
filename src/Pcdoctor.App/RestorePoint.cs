using System.Runtime.InteropServices;

namespace Pcdoctor.App;

/// <summary>
/// 执行前建一个 Windows 系统还原点（用户在设置里打开了开关才做）。这是 Windows 自带的整机后悔药，
/// 在 Iris 自己的撤销之外再多一层。需要管理员权限，所以在辅助进程里做。
/// Windows 默认 24 小时内只建一个还原点：今天已经建过时，Windows 会直接跳过，这里如实说明。
/// </summary>
static class RestorePoint
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW info, out STATEMGRSTATUS status);

    const int BEGIN_SYSTEM_CHANGE = 100, END_SYSTEM_CHANGE = 101, MODIFY_SETTINGS = 12;
    const int ERROR_SERVICE_DISABLED = 1058;

    /// <summary>返回给用户看的一句话。</summary>
    public static string Create(string description)
    {
        try
        {
            var info = new RESTOREPOINTINFOW { dwEventType = BEGIN_SYSTEM_CHANGE, dwRestorePtType = MODIFY_SETTINGS, szDescription = description };
            if (!SRSetRestorePointW(ref info, out var st))
                return st.nStatus == ERROR_SERVICE_DISABLED
                    ? "没有建还原点：这台电脑关着系统还原（可以在「系统属性 → 系统保护」里打开）"
                    : $"没有建还原点：Windows 返回了错误 {st.nStatus}";
            var end = new RESTOREPOINTINFOW { dwEventType = END_SYSTEM_CHANGE, llSequenceNumber = st.llSequenceNumber, szDescription = description };
            SRSetRestorePointW(ref end, out _);
            return "已请 Windows 建好系统还原点（如果今天已经建过一个，Windows 会跳过这一次）";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "没有建还原点：这个 Windows 版本没有系统还原功能";
        }
    }
}
