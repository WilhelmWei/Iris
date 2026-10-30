using System.Runtime.InteropServices;
using Pcdoctor.Ops;

namespace Pcdoctor.App;

/// <summary>深度空间扫描在 Windows 上的读法：目录枚举本身带回大小和属性，只有压缩文件和 Windows 目录里的文件才额外查一次。</summary>
public sealed class WindowsSpaceSource : ISpaceSource
{
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
    static readonly EnumerationOptions Opt = new() { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };

    public IEnumerable<SpaceEntry> List(string dir)
    {
        foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Opt))
        {
            var a = fsi.Attributes;
            var isDir = (a & FileAttributes.Directory) != 0;
            yield return new SpaceEntry(fsi.Name, isDir, isDir ? 0 : ((FileInfo)fsi).Length, (a & FileAttributes.ReparsePoint) != 0,
                Placeholder: (a & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0,
                Compressed: (a & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0);
        }
    }

    public long Allocated(string path, long logical)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        return low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0 ? logical : ((long)high << 32) | low;
    }

    public (ulong id, int links)? LinkInfo(string path)
    {
        // 只要读属性的权限（不读内容，不影响正在使用这个文件的程序）
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* 共享读写删 */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                  0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, (int)info.NumberOfLinks);
    }

    /// <summary>当前线程进入后台模式：磁盘和 CPU 优先级都降下来，用户边看报告边用电脑时不卡。</summary>
    public static void EnterBackgroundMode() => SetThreadPriority(GetCurrentThread(), 0x00010000 /* THREAD_MODE_BACKGROUND_BEGIN */);

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes; public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string path, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr thread, int priority);
}
