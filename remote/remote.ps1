<#
.SYNOPSIS
    AI 电脑体检 · 远程体检（采集端）

.DESCRIPTION
    给朋友用：双击 start.bat 运行。只读检查——不联网、不安装任何东西、不修改任何设置。
    在桌面生成一份报告，发给把这个工具给你的人；他用 AI 研判后，把中文体检报告发回给你。

    作者 Wilhelm · https://github.com/WilhelmWei/Iris
#>
[CmdletBinding()]
param(
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    [switch]$Elevated,      # 内部用：提权后重新启动时带上
    [switch]$NoElevate,     # 测试用：不请求管理员权限
    [switch]$NoPause        # 测试用：结束时不等回车
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = $HOME }
$Self = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$ProjectUrl = 'https://github.com/WilhelmWei/Iris'
$SectionTitles = @('资源占用', '进程', '开机自启动', '服务', '计划任务', '驱动', '已装软件', '存储',
                   '电源与电池', '安全设置', '浏览器与网络', '驱动与系统版本', '稳定性')

function Test-Admin {
    try {
        return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# 结束前停住，免得窗口一闪而过、朋友看不到结果
function Finish([int]$Code) {
    if (-not $NoPause) { Write-Host ''; [void](Read-Host '  按回车关闭这个窗口') }
    exit $Code
}

# 读取子进程正在写的日志（共享读，不打断对方写入）
function Read-Log([string]$Path, [Text.Encoding]$Encoding) {
    try {
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try { return (New-Object IO.StreamReader($fs, $Encoding)).ReadToEnd() } finally { $fs.Close() }
    } catch { return '' }
}

$OnWindows = ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows)

# 提权后新开的控制台常用不支持中文的点阵字体（实测中文重叠成一团）。
# 只把"这个窗口"临时换成新宋体：不改系统和控制台的默认设置，关掉窗口就恢复
function Set-ChineseConsoleFont {
    if (-not $OnWindows) { return }
    try {
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PcdConsoleFont {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FontInfo {
        public uint cbSize; public uint nFont; public short sizeX; public short sizeY;
        public int family; public int weight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string face;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetCurrentConsoleFontEx(IntPtr h, bool max, ref FontInfo f);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetCurrentConsoleFontEx(IntPtr h, bool max, ref FontInfo f);
    public static string Current() {
        var f = new FontInfo(); f.cbSize = (uint)Marshal.SizeOf(f);
        return GetCurrentConsoleFontEx(GetStdHandle(-11), false, ref f) ? f.face : null;
    }
    public static bool Use(string face, short size) {
        var f = new FontInfo(); f.cbSize = (uint)Marshal.SizeOf(f);
        f.face = face; f.sizeY = size; f.family = 54; f.weight = 400;   // 54 = TrueType 等宽
        return SetCurrentConsoleFontEx(GetStdHandle(-11), false, ref f);
    }
}
'@
        $face = [PcdConsoleFont]::Current()
        if ($face -notmatch '(?i)SimSun|宋体|黑体|YaHei|Hei|Sarasa|Gothic|Ming') { [void][PcdConsoleFont]::Use('NSimSun', 16) }
    } catch {}
}

# 结束时用 Windows 对话框再说一遍：对话框的中文不受控制台字体影响，朋友一定看得懂
function Show-Dialog([string]$Text, [string]$Icon) {
    if (-not $OnWindows -or $NoPause) { return $false }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        [void][System.Windows.Forms.MessageBox]::Show($Text, 'AI 电脑体检 · 远程体检', 'OK', $Icon)
        return $true
    } catch { return $false }
}

# 需要管理员权限才能读全驱动、蓝屏记录等：弹出"是否允许更改"时点"是"
if (-not (Test-Admin) -and -not $Elevated -and -not $NoElevate) {
    try {
        # 把桌面路径传过去：用别的管理员账号提权时，"桌面"会变成那个账号的桌面
        $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated -OutDir `"$OutDir`""
        Start-Process -FilePath $Self -Verb RunAs -ArgumentList $argList
        exit 0
    } catch {
        Write-Host '  没有获得管理员权限，继续以普通权限检查（驱动、蓝屏记录等会不完整）。' -ForegroundColor Yellow
    }
}

Set-ChineseConsoleFont

try {
    Write-Host ''
    Write-Host '  AI 电脑体检 · 远程体检' -ForegroundColor Cyan
    Write-Host '  只读检查：不联网、不安装、不修改任何设置，大约 1–2 分钟。期间可以正常用电脑。'
    Write-Host "  作者 Wilhelm · $ProjectUrl" -ForegroundColor DarkGray
    Write-Host ''

    # 采集脚本：发布包里和本文件放在一起；在项目仓库里运行时在 ..\collector 下
    $collector = [IO.Path]::Combine($PSScriptRoot, 'pcdoctor-collect.ps1')
    if (-not (Test-Path -LiteralPath $collector)) { $collector = [IO.Path]::Combine($PSScriptRoot, '..', 'collector', 'pcdoctor-collect.ps1') }
    if (-not (Test-Path -LiteralPath $collector)) { throw '找不到 pcdoctor-collect.ps1，请把压缩包完整解压后再运行。' }

    # 采集在隐藏的子进程里跑、输出写进临时日志：这个窗口被点住暂停也不会卡住检查
    $started = Get-Date
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $log = [IO.Path]::Combine([IO.Path]::GetTempPath(), "pcdoctor-remote-$PID.log")
    $opts = @{ FilePath = $Self; PassThru = $true; RedirectStandardOutput = $log; RedirectStandardError = "$log.err"
               ArgumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$collector`" -OutDir `"$OutDir`"" }
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) { $opts.WindowStyle = 'Hidden' }
    $p = Start-Process @opts
    $null = $p.Handle
    $enc = [Console]::OutputEncoding
    while (-not $p.HasExited) {
        $n = ([regex]::Matches((Read-Log $log $enc), '\[\s*[\d.,]+s\]')).Count   # 每开始一节输出一行 [  9.3s]
        $title = '准备中'; if ($n -ge 1) { $title = $SectionTitles[[math]::Min($n, 13) - 1] }
        Write-Progress -Activity 'AI 电脑体检 · 远程体检' -Status ('正在检查：{0}（{1}/13，已用 {2:N0} 秒）' -f $title, [math]::Max($n, 1), $sw.Elapsed.TotalSeconds) `
            -PercentComplete ([int]([math]::Max(0, $n - 1) / 13 * 100))
        Start-Sleep -Milliseconds 500
    }
    Write-Progress -Activity 'AI 电脑体检 · 远程体检' -Completed
    $errText = (Read-Log "$log.err" $enc).Trim()
    Remove-Item -LiteralPath $log, "$log.err" -Force -ErrorAction SilentlyContinue

    $report = Get-ChildItem -LiteralPath $OutDir -Filter 'pcdoctor-report-*.json' |
        Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-2) } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $report) { throw "检查没有生成报告。$errText" }

    $r = $null; try { $r = Get-Content -LiteralPath $report.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch {}

    Write-Host ('  完成，用时 {0:N0} 秒。报告已保存：' -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    Write-Host "    $($report.FullName)" -ForegroundColor Green
    if ($r -and $r.flagSummary) {
        $n = [int]$r.flagSummary.high + [int]$r.flagSummary.medium + [int]$r.flagSummary.low
        Write-Host "    初步发现 $n 条值得看一看的项目，是不是问题、要不要处理，等 AI 研判后再说。" -ForegroundColor DarkGray
    }
    if ($r -and -not $r.tool.isAdmin) { Write-Host '    这次没有管理员权限，部分项目不完整。' -ForegroundColor Yellow }
    Write-Host ''
    Write-Host '  下一步：把这个文件发给把工具给你的人（微信、QQ 发文件都可以）。' -ForegroundColor Cyan
    Write-Host '  他会用 AI 研判，再把一份中文体检报告发回给你。' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  报告里有什么：'
    Write-Host '    · 系统版本、电脑型号、CPU、内存、各盘空间'
    Write-Host '    · 已装软件、正在运行的程序、开机自启动、服务、计划任务、驱动的名字和路径'
    Write-Host '    · 安全设置（Defender、防火墙、UAC），最近的蓝屏和程序崩溃记录'
    Write-Host '    · 网络设置（系统代理、hosts、DNS 服务器地址），浏览器扩展的名字'
    Write-Host '    · 几个常见文件夹的大小（只算大小，不看内容）'
    Write-Host '  已经去掉的：你的用户名、计算机名、邮箱地址（换成了 [用户] 这样的占位符）'
    Write-Host '  不会包含：任何文档、照片、聊天记录、浏览记录、密码，本机 IP、MAC 地址、Wi-Fi 名称'
    Write-Host '  请注意：文件夹路径里的名字会保留（比如 D:\项目\毕业设计）。介意的话，可以先用记事本打开报告看一看。' -ForegroundColor DarkGray

    $msg = "检查完成，报告已保存：`n$($report.FullName)`n`n" +
           "下一步：把这个文件发给把工具给你的人（微信、QQ 发文件都可以），他会用 AI 研判后把中文体检报告发回给你。`n`n" +
           "报告里有：系统和硬件信息，已装软件、运行中的程序、开机自启动、服务、计划任务、驱动的名字和路径，安全设置，蓝屏和崩溃记录，网络设置，常见文件夹的大小。`n" +
           "已经去掉：你的用户名、计算机名、邮箱地址。`n" +
           "不会包含：文档、照片、聊天记录、浏览记录、密码，本机 IP、MAC 地址、Wi-Fi 名称。文件夹路径里的名字会保留。`n`n" +
           '点「确定」后会打开报告所在的文件夹。'
    $shown = Show-Dialog $msg 'Information'
    if ($OnWindows) {
        Start-Process explorer.exe -ArgumentList "/select,`"$($report.FullName)`""   # 打开桌面并选中报告，方便直接拖进聊天窗口
    }
    if ($shown) { exit 0 }   # 对话框已经说清楚了，不用再在控制台里等回车
    Finish 0
} catch {
    Write-Progress -Activity 'AI 电脑体检 · 远程体检' -Completed
    Write-Host ''
    Write-Host "  出错了：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '  可以把这个窗口截图发给把工具给你的人。' -ForegroundColor Red
    if (Show-Dialog "检查没有完成：$($_.Exception.Message)`n`n可以截图发给把工具给你的人。" 'Warning') { exit 1 }
    Finish 1
}
