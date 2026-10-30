<#
.SYNOPSIS
    AI 电脑体检 · 采集原型（MVP，只读）

.DESCRIPTION
    按《AI 电脑体检 · 核心设计文档》第 3 章的 13 个维度采集信息，
    并用第 4 章的规则做预筛，输出一个 JSON 文件，供 LLM 研判。

    本脚本 **只读**：不修改注册表、服务、驱动、计划任务或任何文件，
    唯一的写入是输出的 JSON 报告。

.PARAMETER OutDir
    报告输出目录，默认是桌面。

.PARAMETER NoRedact
    不做隐私打码（默认会把用户名、计算机名、邮箱替换掉）。

.PARAMETER Quick
    跳过耗时的文件夹体积统计和数字签名校验，约 15 秒完成。

.PARAMETER CheckUpdates
    额外查询待安装的 Windows 更新（可能需要 1–3 分钟）。

.PARAMETER JunkCatalog
    清理目录表 junk.json 的位置（按它测量各类缓存的大小）。默认找脚本旁边的 junk.json 或仓库里的 kb\junk.json。

.EXAMPLE
    # 推荐：管理员 PowerShell 中运行（不改变系统执行策略，只对本次进程生效）
    powershell -ExecutionPolicy Bypass -File .\pcdoctor-collect.ps1

.NOTES
    兼容 Windows PowerShell 5.1 与 PowerShell 7。
    文件必须以 UTF-8 with BOM 保存，否则 5.1 会把中文读成乱码。
#>
[CmdletBinding()]
param(
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    [switch]$NoRedact,
    [switch]$Quick,
    [switch]$CheckUpdates,
    [string]$JunkCatalog = ''
)

$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference    = 'SilentlyContinue'
$ScriptVersion = '0.2.9'
$Clock = [Diagnostics.Stopwatch]::StartNew()

# 清理目录表（kb/junk.json）：桌面版把它写在脚本旁边并用 -JunkCatalog 传进来；远程体检包里和脚本放在一起
if (-not $JunkCatalog -and $PSScriptRoot) {
    foreach ($c in @("$PSScriptRoot\junk.json", "$PSScriptRoot\..\kb\junk.json")) { if (Test-Path -LiteralPath $c) { $JunkCatalog = $c; break } }
}
$Junk = $null
if ($JunkCatalog -and (Test-Path -LiteralPath $JunkCatalog)) { try { $Junk = Get-Content -LiteralPath $JunkCatalog -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $Junk = $null } }

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# ---------------------------------------------------------------------------
# 通用工具
# ---------------------------------------------------------------------------

$Flags   = New-Object System.Collections.ArrayList
$Timings = [ordered]@{}
$CollectErrors = New-Object System.Collections.ArrayList
$Perf    = [ordered]@{}

# 细分计时：按调用类别累计次数和秒数，写进 tool.perf，用来定位一节里到底慢在哪
function Add-Perf([string]$Name, [double]$Seconds) {
    if (-not $Perf.Contains($Name)) { $Perf[$Name] = [ordered]@{ calls = 0; sec = 0.0 } }
    $Perf[$Name].calls += 1
    $Perf[$Name].sec += $Seconds
}

# 单个可选采集项失败时只丢这一项，不拖垮整节；失败原因写进报告，告诉 LLM 哪些数据不完整
function Safe([string]$What, [scriptblock]$Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { & $Body } catch { [void]$CollectErrors.Add(('{0}: {1}' -f $What, $_.Exception.Message)); $null }
    finally { if ($sw.Elapsed.TotalSeconds -ge 0.5) { Add-Perf $What $sw.Elapsed.TotalSeconds } }
}

function Add-Flag {
    param([string]$Section, [string]$Rule, [ValidateSet('high','medium','low','info')][string]$Severity,
          [string]$Object, [string]$Evidence)
    [void]$Flags.Add([ordered]@{
        id       = ('F{0:D3}' -f ($Flags.Count + 1))
        section  = $Section
        rule     = $Rule
        severity = $Severity
        object   = $Object
        evidence = $Evidence
    })
}

function Write-Step([string]$text) {
    Write-Host ('  [{0,5:N1}s] {1}' -f $Clock.Elapsed.TotalSeconds, $text) -ForegroundColor DarkGray
}

function Invoke-Section {
    param([string]$Name, [string]$Title, [scriptblock]$Body)
    Write-Step "采集：$Title"
    $t = [Diagnostics.Stopwatch]::StartNew()
    $result = $null
    try   { $result = & $Body }
    catch { $result = [ordered]@{ error = $_.Exception.Message }; [void]$CollectErrors.Add(('section {0}: {1}' -f $Name, $_.Exception.Message)) }
    $Timings[$Name] = [math]::Round($t.Elapsed.TotalSeconds, 1)
    return $result
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

# 从命令行字符串里取出可执行文件路径
function Resolve-ExePath([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $null }
    $c = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    $c = $c -replace '^\\\?\?\\', ''
    $c = $c -replace '^(?i)\\SystemRoot', $env:windir
    if ($c -match '^(?i)system32\\') { $c = "$env:windir\$c" }
    $p = $null
    if ($c.StartsWith('"')) {
        $end = $c.IndexOf('"', 1)
        if ($end -gt 1) { $p = $c.Substring(1, $end - 1) } else { $p = $c.Trim('"') }
    }
    elseif ($c -match '^(.+?\.(exe|sys|dll|com|bat|cmd|ps1|vbs|js|msc|cpl))(\s|,|$)') { $p = $Matches[1] }
    else { $p = ($c -split '\s')[0] }
    if ($p -and $p -notmatch '^[A-Za-z]:\\|^\\\\') {
        $cand = "$env:windir\System32\$p"
        if (Test-Path -LiteralPath $cand) { $p = $cand }
    }
    return $p
}

$FileCache = @{}
# 文件事实：是否存在、厂商、签名（带缓存；-Quick 时跳过签名）
# 签名校验是采集里最慢的一步。Windows 目录下（不含 Temp 等用户可写位置）厂商为微软的文件跳过校验，
# signature 留空：后面判断"是否微软文件"本来就认厂商字段，跳过不会让任何规则漏报
function Get-FileFacts([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $key = $Path.ToLowerInvariant()
    if ($FileCache.ContainsKey($key)) { return $FileCache[$key] }
    $f = [ordered]@{ path = $Path; exists = $false; company = $null; product = $null; signature = $null; signer = $null }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $f.exists = $true
        # 商店应用的执行别名：WindowsApps 下的重解析点，本身读不到版本和签名，标记后跳过
        $fi = Get-Item -LiteralPath $Path -Force
        if ($fi -and ($fi.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $Path -match '(?i)\\WindowsApps\\') {
            $f.signature = 'AppExecutionAlias'
            $FileCache[$key] = $f
            return $f
        }
        # 读版本信息或签名失败（如被占用、无权限）时只丢这个文件的事实，不拖垮整节
        try {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $vi = $fi.VersionInfo
            if ($vi) { $f.company = $vi.CompanyName; $f.product = $vi.ProductName }
            Add-Perf 'file versionInfo' $sw.Elapsed.TotalSeconds
            $needSig = -not $Quick
            if ($needSig -and $Path -like "$env:windir\*" -and -not (Test-UserWritablePath $Path) -and $f.company -match 'Microsoft') { $needSig = $false }
            if ($needSig) {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $sig = Get-AuthenticodeSignature -LiteralPath $Path
                Add-Perf 'file signature' $sw.Elapsed.TotalSeconds
                if ($sig) {
                    $f.signature = [string]$sig.Status
                    if ($sig.SignerCertificate) {
                        $f.signer = ($sig.SignerCertificate.Subject -replace '^CN="?([^,"]+)"?.*$', '$1')
                    }
                }
            }
        } catch { [void]$CollectErrors.Add(('file {0}: {1}' -f $Path, $_.Exception.Message)) }
    }
    $FileCache[$key] = $f
    return $f
}

function Test-MicrosoftFile($Facts) {
    if (-not $Facts) { return $false }
    return (($Facts.signer -match 'Microsoft') -or ($Facts.company -match 'Microsoft'))
}

# Windows 自带（收件箱）驱动：即使厂商是 AMD / LSI 等，签名者也是 "Microsoft Windows"
function Test-InboxFile($Facts) {
    if (-not $Facts) { return $false }
    return ($Facts.signer -match '^Microsoft Windows')
}

function Test-UserWritablePath([string]$Path) {
    if (-not $Path) { return $false }
    return ($Path -match '(?i)\\AppData\\|\\Temp\\|\\Downloads\\|\\Desktop\\|^[A-Z]:\\ProgramData\\[^\\]+$|^[A-Z]:\\Users\\Public\\')
}

# Defender 排除项的风险：整盘、用户目录、可执行类扩展名的排除会让恶意软件整个绕过扫描
function Get-ExclusionRisk([string]$Kind, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '^N/A') { return $null }  # 非管理员读不到，返回 N/A 提示
    if ($Kind -eq 'path') {
        $v = $Value.TrimEnd('\')
        if ($v -match '^(?i)[A-Z]:$|^(?i)[A-Z]:\\(ProgramData|Users|Windows\\Temp)$|^(?i)[A-Z]:\\Users\\[^\\]+$') { return 'high' }
        # 开发工具会主动提示把自己的目录加进排除项（JetBrains IDE、conda 等），属于常见做法
        if ($v -match '(?i)\\(JetBrains|PycharmProjects|IdeaProjects|\.conda|\.anaconda|anaconda3|miniconda3|\.gradle|\.m2|node_modules)(\\|$)') { return 'low' }
        if (Test-UserWritablePath ($v + '\')) { return 'high' }
        return 'low'
    }
    if ($Kind -eq 'extension') {
        if ($Value.TrimStart('.', '*') -match '^(?i)(exe|dll|sys|scr|com|msi|ps1|bat|cmd|vbs|js|hta)$') { return 'high' }
        return 'low'
    }
    if (Test-UserWritablePath $Value) { return 'high' }
    # 按进程名排除时，任何同名程序都不被扫描；Update.exe、uninst.exe 这类通用名字恶意程序也常用
    if ($Value -match '^(?i)(update|updater|uninst|uninstall|unins\d*|setup|install|installer|launcher)\.exe$') { return 'high' }
    return 'medium'
}

# 缓存目录的类别：按目录名判断，子目录继承父目录的类别
function Get-CacheCategory([string]$Name) {
    switch -Regex ($Name) {
        '^(?i)(Image|Images|Img|Pic|Pics|Picture|Pictures|Thumb|Thumbs|Emotion|CustomEmotion|Sticker)$' { return '图片' }
        '^(?i)(Video|Videos)$' { return '视频' }
        '^(?i)(File|Files|FileRecv|MsgAttach)$' { return '文件' }
        '^(?i)(Cache|Caches|Temp|Tmp|Log|Logs|xlog|CrashDump|Crashpad)$' { return '缓存与日志' }
    }
    return $null
}

# 文件夹体积（有时间上限，跳过重解析点，避免卡死）
# -Breakdown：额外按类别和"最后修改时间早于 N 天"分桶统计。清理时由用户自己勾选清哪些类别、
# 清多久以前的，采集阶段先把这些数字准备好（脚本本身只读，不删除任何东西）
function Measure-Folder([string]$Path, [int]$CapSeconds = 20, [switch]$Breakdown) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return $null }
    $t = [Diagnostics.Stopwatch]::StartNew()
    $sum = [int64]0; $count = 0; $partial = $false
    $now = Get-Date
    $ageDays = @(7, 30, 90, 180, 365)
    $byAge = @{}; foreach ($d in $ageDays) { $byAge[$d] = [int64]0 }
    $byCat = @{}
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($Path, '其他'))
    while ($stack.Count -gt 0) {
        if ($t.Elapsed.TotalSeconds -gt $CapSeconds) { $partial = $true; break }
        $entry = $stack.Pop()
        $dir = New-Object IO.DirectoryInfo ($entry[0])
        $cat = $entry[1]
        try {
            foreach ($fi in $dir.GetFiles()) {
                $sum += $fi.Length; $count++
                if ($Breakdown) {
                    $byCat[$cat] = [int64]$byCat[$cat] + $fi.Length
                    $age = ($now - $fi.LastWriteTime).TotalDays
                    foreach ($d in $ageDays) { if ($age -ge $d) { $byAge[$d] += $fi.Length } }
                }
            }
        } catch {}
        try {
            foreach ($sub in $dir.GetDirectories()) {
                if (($sub.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $subCat = $cat
                if ($Breakdown) { $c = Get-CacheCategory $sub.Name; if ($c) { $subCat = $c } }
                $stack.Push(@($sub.FullName, $subCat))
            }
        } catch {}
    }
    $r = [ordered]@{ path = $Path; sizeGB = [math]::Round($sum / 1GB, 2); files = $count; partial = $partial }
    if ($Breakdown) {
        $r.byCategoryGB = [ordered]@{}
        foreach ($k in @($byCat.Keys | Sort-Object { $byCat[$_] } -Descending)) { $r.byCategoryGB[$k] = [math]::Round($byCat[$k] / 1GB, 2) }
        $r.olderThanGB = [ordered]@{}
        foreach ($d in $ageDays) { $r.olderThanGB["${d}d"] = [math]::Round($byAge[$d] / 1GB, 2) }
    }
    return $r
}

function Format-Size([double]$MB) {
    if ($MB -ge 1024) { return ('{0:0.#} GB' -f ($MB / 1024)) }
    return ('{0:0} MB' -f $MB)
}

# 清理目录表里的路径模板 → 实际存在的文件夹。一整段的 * 匹配任意一个子文件夹；不进入联接点和符号链接
function Expand-JunkPath([string]$Template, [hashtable]$Vars) {
    if (-not ($Template -match '^%(\w+)%(.*)$')) { return @() }
    $root = $Vars[$Matches[1].ToUpperInvariant()]
    $rest = $Matches[2]
    if (-not $root) { return @() }
    $current = @(([string]$root).TrimEnd('\', '/'))
    foreach ($seg in @($rest.Split('\') | Where-Object { $_ })) {
        if ($seg -eq '..' -or $seg -eq '.') { return @() }
        $next = @()
        foreach ($c in $current) {
            if ($seg -eq '*') {
                $next += @(Get-ChildItem -LiteralPath $c -Directory -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 } | ForEach-Object { $_.FullName })
            } else {
                $p = [IO.Path]::Combine($c, $seg)
                $it = Get-Item -LiteralPath $p -Force
                if ($it -and $it.PSIsContainer -and (($it.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) { $next += $p }
            }
        }
        $current = $next
        if (-not $current.Count) { return @() }
    }
    return @($current | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
}

# 量一类缓存：总大小，以及按目录表的规则（文件名、最短天数、保留最新几个）可以清理的大小。和桌面版的清理操作用同一套规则
function Measure-JunkCategory($Cat, [hashtable]$Vars, [hashtable]$Running, [int]$CapSeconds = 15) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    $minAge = 0; if ($Cat.minAgeDays) { $minAge = [int]$Cat.minAgeDays }
    $recurse = $true; if ($Cat.PSObject.Properties['recurse'] -and $Cat.recurse -eq $false) { $recurse = $false }
    $include = @(); if ($Cat.include) { $include = @($Cat.include) }
    $keep = 0; if ($Cat.keepNewest) { $keep = [int]$Cat.keepNewest }
    $cutoff = (Get-Date).AddDays(-$minAge)
    $roots = @(); foreach ($p in @($Cat.paths)) { $roots += @(Expand-JunkPath $p $Vars) }
    if (-not $roots.Count) { return $null }
    $total = [int64]0; $files = 0; $partial = $false
    $eligBytes = [int64]0; $eligFiles = 0
    $kept = New-Object System.Collections.ArrayList
    $skipAttr = [IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::System
    foreach ($root in $roots) {
        $stack = New-Object System.Collections.Stack
        $stack.Push($root)
        while ($stack.Count -gt 0) {
            if ($t.Elapsed.TotalSeconds -gt $CapSeconds) { $partial = $true; break }
            $dir = New-Object IO.DirectoryInfo ($stack.Pop())
            try {
                foreach ($fi in $dir.GetFiles()) {
                    if (($fi.Attributes -band $skipAttr) -ne 0) { continue }
                    if ($include.Count) {
                        $ok = $false
                        foreach ($pat in $include) { if ($fi.Name -like $pat) { $ok = $true; break } }
                        if (-not $ok) { continue }
                    }
                    $total += $fi.Length; $files++
                    if ($minAge -gt 0 -and $fi.LastWriteTime -ge $cutoff) { continue }
                    if ($keep -gt 0) { [void]$kept.Add(@($fi.LastWriteTime, $fi.Length)) } else { $eligBytes += $fi.Length; $eligFiles++ }
                }
            } catch {}
            if ($recurse) {
                try { foreach ($sub in $dir.GetDirectories()) { if (($sub.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $stack.Push($sub.FullName) } } } catch {}
            }
        }
        if ($partial) { break }
    }
    if ($keep -gt 0) { foreach ($x in @($kept | Sort-Object { $_[0] } -Descending | Select-Object -Skip $keep)) { $eligBytes += $x[1]; $eligFiles++ } }
    $run = @(); foreach ($n in @($Cat.closeFirst)) { if ($n -and $Running.ContainsKey(([string]$n).ToLowerInvariant())) { $run += $n } }
    [ordered]@{ id = $Cat.id; group = $Cat.group; name = $Cat.name; tier = $Cat.tier
                sizeMB = [math]::Round($total / 1MB, 1); cleanableMB = [math]::Round($eligBytes / 1MB, 1); files = $files; cleanableFiles = $eligFiles
                partial = $partial; running = ($run -join '、'); paths = @($roots) }
}

# 隐私打码：用户名、计算机名、邮箱。用户名或计算机名为空时跳过，否则空正则会在每个字符之间插入替换
function Hide-Private([string]$Text, [string]$User, [string]$Computer) {
    if (-not $Text) { return $Text }
    if (-not [string]::IsNullOrWhiteSpace($User)) {
        $Text = [regex]::Replace($Text, '(?i)(?<![A-Za-z0-9])' + [regex]::Escape($User) + '(?![A-Za-z0-9])', '[用户]')
    }
    if (-not [string]::IsNullOrWhiteSpace($Computer)) {
        $Text = [regex]::Replace($Text, '(?i)(?<![A-Za-z0-9-])' + [regex]::Escape($Computer) + '(?![A-Za-z0-9-])', '[计算机名]')
    }
    return [regex]::Replace($Text, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[邮箱]')
}

# WMI 查询带超时：WMI 提供程序卡住时（性能计数器类首次查询、上次被 Ctrl+C 打断）Get-CimInstance 会无限期等待
function Get-Cim([string]$Class, [string]$Namespace = 'root\cimv2', [string]$Filter, [int]$TimeoutSec = 30) {
    $q = @{ Namespace = $Namespace; ClassName = $Class; OperationTimeoutSec = $TimeoutSec; ErrorAction = 'Stop' }
    if ($Filter) { $q.Filter = $Filter }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { return Get-CimInstance @q }
    catch {
        # 类不存在（如台式机没有电池类）属于正常，只记录耗时异常或超时，方便定位哪个查询慢
        if ($sw.Elapsed.TotalSeconds -ge 5) { [void]$CollectErrors.Add(('cim {0} ({1:N0}s): {2}' -f $Class, $sw.Elapsed.TotalSeconds, $_.Exception.Message)) }
        return $null
    }
    finally { Add-Perf "cim $Class" $sw.Elapsed.TotalSeconds }
}

# 种子名单：仅作为"值得 LLM 关注"的线索，不直接下结论，需人工持续审核
$Seeds = [ordered]@{
    leftoverOrBundle = '(?i)360(safe|se|tray|sd|zip|极速|安全|压缩|卫士)|Qihoo|2345|鲁大师|ludashi|驱动精灵|驱动人生|金山毒霸|kingsoft\s*antivirus|猎豹|快压|hao123|百度卫士|腾讯电脑管家|QQPCMgr|Huorong|火绒'
    antiCheatOrAccel = '(?i)EasyAntiCheat|BattlEye|BEService|UniFairy|unirsdt|ACE-|TesMon|WeGame|LgdCatcher|HeyboxPF|uupacket|xlwfp|XLGuard|vgk|vgc'
    bankSecurity     = '(?i)PECKP|PassGuard|CSII|ICBC|CMB.*Edit|WellsSecure|aliedit'
    serverServices   = '^(?i)(W3SVC|WAS|ftpsvc|MSFTPSVC|RemoteRegistry|TlntSvr|SNMP|sshd|MySQL\d*|MSSQL.*|postgresql.*|Apache\d*|nginx)$'
    scriptEngines    = '(?i)\b(powershell|pwsh|mshta|wscript|cscript)(\.exe)?\b|\.vbs\b|\.js\b|\s-e(nc|ncodedcommand)?\s'
    systemNames      = '^(?i)(svchost|csrss|lsass|winlogon|services|smss|wininit|dwm|conhost|rundll32|taskhostw|spoolsv|explorer|sihost|ctfmon)\.exe$'
}
$Categories = [ordered]@{
    '音乐'     = '(?i)QQ音乐|网易云|CloudMusic|酷狗|酷我|Spotify|Apple Music'
    '办公套件' = '(?i)^WPS|Microsoft Office|Microsoft 365|LibreOffice'
    '视频会议' = '(?i)Zoom|腾讯会议|Microsoft Teams|钉钉|DingTalk|飞书|Lark|Webex'
    'VPN'      = '(?i)VPN|WireGuard|OpenVPN|Forcepoint|Sangfor|EasyConnect|QuickFox'
    '远程控制' = '(?i)ToDesk|向日葵|Sunlogin|TeamViewer|AnyDesk|UU远程|RustDesk'
    '浏览器'   = '(?i)Google Chrome$|Microsoft Edge$|Firefox|360极速浏览器|QQ浏览器|夸克|华为浏览器'
    '网盘'     = '(?i)百度网盘|阿里云盘|夸克网盘|OneDrive|Google Drive|Dropbox|坚果云'
}

# ---------------------------------------------------------------------------
Write-Host ''
Write-Host ('  AI 电脑体检 · 采集原型 v{0}（只读，不会修改任何设置）' -f $ScriptVersion) -ForegroundColor Cyan
if (-not $IsAdmin) {
    Write-Host '  提示：当前不是管理员权限，驱动、BitLocker、蓝屏转储等项目会不完整。' -ForegroundColor Yellow
}
Write-Host '  运行期间请不要点击窗口：在控制台里选中文字会让脚本暂停，直到按 Esc 或回车。' -ForegroundColor DarkGray
Write-Host ''

# Get-MpThreatCatalog 每调用一次都要加载整个威胁目录（实测 34 秒一次）。
# 一开始就放进后台作业，一次查完所有"允许的威胁"里缺名称的编号，和其他采集同时进行，安全节再取结果
# powercfg 电池报告实测要 26 秒：同样一开始就在后台生成，电源节需要时再取；
# /duration 1 只分析最近 1 天的使用历史（我们只要容量数据）。临时文件在脚本结束前删除
$BatteryReportPath = [IO.Path]::Combine([IO.Path]::GetTempPath(), "pcdoctor-battery-$PID.xml")
$BatteryReportProc = $null
if (-not $Quick) {
    $BatteryReportProc = Safe 'Start powercfg batteryreport' {
        Start-Process -FilePath 'powercfg.exe' -ArgumentList "/batteryreport /xml /duration 1 /output `"$BatteryReportPath`"" -WindowStyle Hidden -PassThru
    }
}

$CatalogJob = $null
if (-not $Quick) {
    $CatalogJob = Safe 'Start-Job threat catalog' {
        Start-Job -ScriptBlock {
            $pref = Get-MpPreference -ErrorAction Stop
            $ids = @($pref.ThreatIDDefaultAction_Ids); $acts = @($pref.ThreatIDDefaultAction_Actions)
            $allowedIds = @(for ($i = 0; $i -lt $ids.Count -and $i -lt $acts.Count; $i++) { if ([int]$acts[$i] -eq 6) { [int64]$ids[$i] } })
            $inHistory = @(Get-MpThreat -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.ThreatID })
            $need = @($allowedIds | Where-Object { $inHistory -notcontains [string]$_ })
            if ($need.Count) {
                Get-MpThreatCatalog -ThreatID $need -ErrorAction Stop |
                    ForEach-Object { [pscustomobject]@{ id = [string]$_.ThreatID; name = $_.ThreatName; severityId = $_.SeverityID } }
            }
        }
    }
}

$Sections = [ordered]@{}

# 1. 资源占用 ------------------------------------------------------------------
$Sections['resources'] = Invoke-Section 'resources' '资源占用' {
    $os  = Get-Cim Win32_OperatingSystem
    $cpu = Get-Cim Win32_Processor | Select-Object -First 1
    $uptimeDays = $null; $memTotal = 0; $memUsedPct = $null; $commitPct = $null
    if ($os -and $os.TotalVisibleMemorySize) {
        $uptimeDays = Safe 'uptime' { [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalDays, 1) }
        $memTotal   = [double]$os.TotalVisibleMemorySize
        $memUsedPct = [math]::Round(100 - [double]$os.FreePhysicalMemory / $memTotal * 100)
        $commitPct  = [math]::Round(100 - [double]$os.FreeVirtualMemory / [double]$os.TotalVirtualMemorySize * 100)
    }

    # CPU / GPU 采样：用性能计数器的 WMI 类（类名不随系统语言变化，Get-Counter 的路径会被本地化）
    $cpuSamples = @()
    for ($i = 0; $i -lt 3; $i++) {
        $s = Get-Cim Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
        if ($s) { $cpuSamples += [double]$s.PercentProcessorTime }
        Start-Sleep -Milliseconds 900
    }
    $cpuAvg = $null; if ($cpuSamples.Count) { $cpuAvg = [math]::Round(($cpuSamples | Measure-Object -Average).Average, 1) }

    $gpu = Get-Cim Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine
    $gpu3d = $null
    if ($gpu) { $gpu3d = [math]::Round((($gpu | Where-Object { $_.Name -match 'engtype_3D' } | Measure-Object UtilizationPercentage -Sum).Sum), 1) }

    $procs = Get-Process
    $top = $procs | Group-Object ProcessName | ForEach-Object {
        [ordered]@{
            name      = $_.Name
            processes = $_.Count
            memoryMB  = [int](($_.Group | Measure-Object WorkingSet64 -Sum).Sum / 1MB)
            cpuSec    = [int](($_.Group | Measure-Object CPU -Sum).Sum)
        }
    }
    $topByCpu = @($top | Sort-Object { $_.cpuSec } -Descending | Select-Object -First 15)
    $topByMem = @($top | Sort-Object { $_.memoryMB } -Descending | Select-Object -First 15)

    $mc = $procs | Where-Object { $_.ProcessName -eq 'Memory Compression' } | Select-Object -First 1
    $mcRank = $null
    if ($mc) {
        for ($i = 0; $i -lt $topByCpu.Count; $i++) { if ($topByCpu[$i].name -eq 'Memory Compression') { $mcRank = $i + 1; break } }
    }

    if ($memUsedPct -ne $null -and $memUsedPct -ge 80) { Add-Flag 'resources' 'memory-high' 'medium' '内存' "物理内存占用 $memUsedPct%，可用 $([math]::Round($os.FreePhysicalMemory/1MB,1)) GB" }
    if ($mcRank -and $mcRank -le 3) { Add-Flag 'resources' 'memory-compression-busy' 'medium' 'Memory Compression' "内存压缩累计 CPU 排第 $mcRank（$([int]$mc.CPU) 秒），说明内存长期吃紧" }
    # Defender 自开机以来的平均占用：实测作者机器开机 2.4 小时、MsMpEng 累计 41593 秒，约 5 个核心一直被占满
    $msmp = $procs | Where-Object { $_.ProcessName -eq 'MsMpEng' } | Select-Object -First 1
    if ($msmp -and $msmp.CPU -and $os -and $os.LastBootUpTime) {
        $upSec = ((Get-Date) - $os.LastBootUpTime).TotalSeconds
        if ($upSec -ge 600) {
            $cores = [math]::Round($msmp.CPU / $upSec, 1)
            if ($cores -ge 1) { Add-Flag 'resources' 'defender-cpu-heavy' 'low' 'Microsoft Defender' "开机以来平均一直占用约 $cores 个 CPU 核心（累计 $([int]$msmp.CPU) 秒），可能有扫描正在进行或卡住；持续这样电脑会发热、变慢" }
        }
    }
    if ($uptimeDays -ne $null -and $uptimeDays -ge 7) { Add-Flag 'resources' 'long-uptime' 'low' '系统运行时间' "已 $uptimeDays 天没有真正重启（快速启动下'关机'不算重启）" }
    if ($cpuAvg -ne $null -and $cpuAvg -ge 15) { Add-Flag 'resources' 'cpu-busy-idle' 'medium' 'CPU' "采样期间平均 CPU $cpuAvg%（采集本身也占一点）" }
    if ($gpu3d -ne $null -and $gpu3d -ge 30) { Add-Flag 'resources' 'gpu-busy-idle' 'high' 'GPU' "采样时 3D 引擎占用 $gpu3d%，如果此时没在用图形程序，需要排查挖矿" }

    [ordered]@{
        os           = [ordered]@{ caption = $os.Caption; version = $os.Version; build = $os.BuildNumber
                                   displayVersion = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'DisplayVersion'
                                   ubr = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'UBR' }
        cpu          = [ordered]@{ name = $cpu.Name; cores = $cpu.NumberOfCores; threads = $cpu.NumberOfLogicalProcessors }
        uptimeDays   = $uptimeDays
        memory       = [ordered]@{ totalGB = [math]::Round($memTotal / 1MB, 1); freeGB = [math]::Round([double]$os.FreePhysicalMemory / 1MB, 1); usedPct = $memUsedPct; commitUsedPct = $commitPct }
        cpuSamplePct = $cpuSamples
        gpu3dPct     = $gpu3d
        memoryCompression = if ($mc) { [ordered]@{ cpuSec = [int]$mc.CPU; memoryMB = [int]($mc.WorkingSet64 / 1MB); cpuRank = $mcRank } } else { $null }
        topByCpuSec  = $topByCpu
        topByMemory  = $topByMem
    }
}

# 2. 进程 ----------------------------------------------------------------------
$Sections['processes'] = Invoke-Section 'processes' '进程' {
    $list = New-Object System.Collections.ArrayList
    $seen = @{}
    foreach ($p in (Get-Process)) {
        # Path 是脚本属性，每次读取都重新查询；进程在采集途中退出时会变成 $null，所以只读一次
        $path = $p.Path
        if (-not $path) { continue }
        $k = $path.ToLowerInvariant()
        if ($seen.ContainsKey($k)) { $seen[$k]['instances'] += 1; $seen[$k]['memoryMB'] += [int]($p.WorkingSet64 / 1MB); continue }
        $facts = Get-FileFacts $path
        $item = [ordered]@{
            name = $p.ProcessName + '.exe'; path = $path; instances = 1; memoryMB = [int]($p.WorkingSet64 / 1MB)
            company = $facts.company; signature = $facts.signature; signer = $facts.signer
        }
        $seen[$k] = $item; [void]$list.Add($item)
    }
    foreach ($it in $list) {
        $isMs = Test-MicrosoftFile $it
        if ($it.name -match $Seeds.systemNames -and $it.path -notmatch ('^(?i)' + [regex]::Escape($env:windir) + '\\')) {
            Add-Flag 'processes' 'system-name-outside-windows' 'high' $it.name "系统进程名却不在 Windows 目录：$($it.path)"
        }
        if (Test-UserWritablePath $it.path) {
            # Claude、Zoom 等正规软件常装在 AppData：签名有效降为低，签名无效升为高
            $sev = 'medium'
            if ($it.signature -eq 'Valid') { $sev = 'low' } elseif ($it.signature -and $it.signature -ne 'AppExecutionAlias') { $sev = 'high' }
            Add-Flag 'processes' 'runs-from-user-folder' $sev $it.name "从用户可写目录运行（签名：$($it.signature)）：$($it.path)"
        }
        if (-not $Quick -and -not $isMs -and $it.signature -and $it.signature -notin @('Valid', 'AppExecutionAlias')) {
            Add-Flag 'processes' 'unsigned' 'low' $it.name "没有有效数字签名（$($it.signature)）"
        }
        if ($it.memoryMB -ge 500 -and -not $isMs) {
            Add-Flag 'processes' 'heavy-memory' 'info' $it.name "$($it.instances) 个进程共占 $($it.memoryMB) MB"
        }
    }
    [ordered]@{ total = $list.Count; items = @($list | Sort-Object { $_.memoryMB } -Descending) }
}

# 3. 开机自启动 ----------------------------------------------------------------
$Sections['startup'] = Invoke-Section 'startup' '开机自启动' {
    $items = New-Object System.Collections.ArrayList
    $runKeys = @(
        @{ hive = 'HKCU'; path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';                 approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' }
        @{ hive = 'HKCU'; path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce';             approved = $null }
        @{ hive = 'HKLM'; path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run';                 approved = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' }
        @{ hive = 'HKLM'; path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run';     approved = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32' }
        @{ hive = 'HKLM'; path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce';             approved = $null }
    )
    foreach ($rk in $runKeys) {
        $props = Get-ItemProperty -LiteralPath $rk.path
        if (-not $props) { continue }
        foreach ($pr in $props.PSObject.Properties) {
            if ($pr.Name -like 'PS*') { continue }
            $enabled = $true
            if ($rk.approved) {
                $bin = Get-RegValue $rk.approved $pr.Name
                if ($bin -is [byte[]] -and $bin.Length -gt 0) { $enabled = (($bin[0] % 2) -eq 0) }
            }
            $exe = Resolve-ExePath ([string]$pr.Value)
            $facts = Get-FileFacts $exe
            [void]$items.Add([ordered]@{ source = 'registry'; location = $rk.path; name = $pr.Name; command = [string]$pr.Value
                                         exe = $exe; exists = $facts.exists; company = $facts.company; signature = $facts.signature; enabled = $enabled })
        }
    }
    $shell = Safe 'WScript.Shell' { New-Object -ComObject WScript.Shell }
    foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))) {
        if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
        foreach ($f in (Get-ChildItem -LiteralPath $folder -File -Force | Where-Object { $_.Name -ne 'desktop.ini' })) {
            $target = $f.FullName
            if ($f.Extension -eq '.lnk' -and $shell) {
                $lnk = Safe "shortcut $($f.Name)" { $shell.CreateShortcut($f.FullName).TargetPath }
                if ($lnk) { $target = $lnk }
            }
            $facts = Get-FileFacts $target
            [void]$items.Add([ordered]@{ source = 'startup-folder'; location = $folder; name = $f.Name; command = $target
                                         exe = $target; exists = $facts.exists; company = $facts.company; signature = $facts.signature; enabled = $true })
        }
    }
    foreach ($it in $items) {
        if ($it.exe -and -not $it.exists) { Add-Flag 'startup' 'target-missing' 'medium' $it.name "自启动指向的文件已不存在（软件卸载残留）：$($it.exe)" }
        elseif ($it.enabled -and (Test-UserWritablePath $it.exe) -and $it.signature -and $it.signature -notin @('Valid', 'AppExecutionAlias')) {
            Add-Flag 'startup' 'unsigned-user-folder' 'high' $it.name "从用户目录自启动且无有效签名：$($it.exe)"
        }
        if ("$($it.name) $($it.exe)" -match $Seeds.leftoverOrBundle) { Add-Flag 'startup' 'seed-bundle' 'medium' $it.name '命中常见捆绑/残留软件种子名单' }
    }
    [ordered]@{ total = $items.Count; enabledCount = @($items | Where-Object { $_.enabled }).Count; items = @($items) }
}

# 4. 服务 ----------------------------------------------------------------------
$Sections['services'] = Invoke-Section 'services' '服务' {
    $out = New-Object System.Collections.ArrayList
    foreach ($s in (Get-Cim Win32_Service)) {
        $exe = Resolve-ExePath $s.PathName
        # 寄宿在 svchost 里的服务，真正的代码在 ServiceDll；只看 svchost.exe 会把第三方 DLL 当成微软服务跳过
        $dll = $null
        if ($exe -match '(?i)\\svchost\.exe$') {
            $svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$($s.Name)"
            $dll = Get-RegValue "$svcKey\Parameters" 'ServiceDll'
            if (-not $dll) { $dll = Get-RegValue $svcKey 'ServiceDll' }
            if (-not $dll -and $s.Name -match '^(.+)_[0-9a-f]+$') {
                # 每用户服务（如 CDPUserSvc_1a2b3）的实例键下没有参数，要去模板服务键里找
                $dll = Get-RegValue "HKLM:\SYSTEM\CurrentControlSet\Services\$($Matches[1])\Parameters" 'ServiceDll'
            }
            if ($dll) { $dll = [Environment]::ExpandEnvironmentVariables([string]$dll) }
            # 部分 Windows 自带服务登记的 DLL 已被精简掉，不存在时按 svchost 本身判断，避免当成第三方误报
            if ($dll -and -not (Test-Path -LiteralPath $dll -PathType Leaf)) { $dll = $null }
        }
        $target = $exe; if ($dll) { $target = $dll }
        $facts = Get-FileFacts $target
        $isMs = Test-MicrosoftFile $facts
        if ($s.Name -match $Seeds.serverServices -and $s.State -eq 'Running') {
            Add-Flag 'services' 'server-service-running' 'medium' $s.Name "个人电脑上运行着服务器类服务：$($s.DisplayName)"
        }
        if ($isMs) { continue }
        $item = [ordered]@{ name = $s.Name; displayName = $s.DisplayName; state = $s.State; startMode = $s.StartMode
                            exe = $exe; serviceDll = $dll; exists = $facts.exists; company = $facts.company; signature = $facts.signature; signer = $facts.signer }
        [void]$out.Add($item)
        if ($target -and -not $facts.exists) { Add-Flag 'services' 'binary-missing' 'medium' $s.Name "服务程序文件已不存在：$target" }
        if (Test-UserWritablePath $target) { Add-Flag 'services' 'runs-from-user-folder' 'high' $s.Name "服务从用户可写目录运行：$target" }
        elseif ($dll -and $facts.exists) {
            $sev = 'medium'; if ($facts.signature -and $facts.signature -ne 'Valid') { $sev = 'high' }
            Add-Flag 'services' 'svchost-third-party-dll' $sev $s.Name "第三方 DLL 寄宿在系统 svchost 进程里（厂商：$($facts.company)，签名：$($facts.signature)）：$dll"
        }
        if ("$($s.Name) $($s.DisplayName) $exe" -match $Seeds.leftoverOrBundle) { Add-Flag 'services' 'seed-bundle' 'medium' $s.Name '命中常见捆绑/残留软件种子名单' }
        if ("$($s.Name) $exe" -match $Seeds.antiCheatOrAccel) { Add-Flag 'services' 'seed-anticheat-accel' 'medium' $s.Name '疑似游戏反作弊或加速器组件' }
    }
    $auto = @($out | Where-Object { $_.startMode -eq 'Auto' })
    [ordered]@{ thirdPartyCount = $out.Count; thirdPartyAutoCount = $auto.Count; items = @($out | Sort-Object { $_.startMode }, { $_.name }) }
}

# 5. 计划任务 ------------------------------------------------------------------
$Sections['scheduledTasks'] = Invoke-Section 'scheduledTasks' '计划任务' {
    $out = New-Object System.Collections.ArrayList
    $flaggedMs = 0
    foreach ($t in (Safe 'Get-ScheduledTask' { Get-ScheduledTask })) {
        $isMsPath = $t.TaskPath -like '\Microsoft\*'
        $acts = @()
        foreach ($a in $t.Actions) {
            if ($a.Execute) { $acts += [ordered]@{ execute = $a.Execute; arguments = $a.Arguments } }
            else { $acts += [ordered]@{ comHandler = $a.ClassId } }
        }
        $text = ($acts | ForEach-Object { "$($_.execute) $($_.arguments)" }) -join ' | '
        $exe = $null; if ($acts.Count -and $acts[0].execute) { $exe = Resolve-ExePath ('"' + $acts[0].execute.Trim('"') + '"') }
        $facts = $null
        $needFacts = (-not $isMsPath) -or (Test-UserWritablePath $exe) -or ($text -match $Seeds.scriptEngines)
        if ($needFacts) { $facts = Get-FileFacts $exe }
        $interval = $null
        foreach ($tr in $t.Triggers) { if ($tr.Repetition -and $tr.Repetition.Interval) { $interval = $tr.Repetition.Interval } }

        $disabled = ([string]$t.State -eq 'Disabled')
        $hit = $false
        if ($exe -and $facts -and -not $facts.exists) {
            Add-Flag 'scheduledTasks' 'target-missing' 'medium' $t.TaskName "计划任务指向的程序已不存在（卸载残留）：$exe"; $hit = $true
        }
        if ($text -match $Seeds.scriptEngines) {
            $sev = 'low'; if (-not $isMsPath) { $sev = 'medium' }
            if ((Test-UserWritablePath $text) -or $text -match '(?i)\s-e(nc|ncodedcommand)?\s|-w(indowstyle)?\s+hidden') { $sev = 'high' }
            Add-Flag 'scheduledTasks' 'script-engine' $sev $t.TaskName "调用脚本引擎：$text"; $hit = $true
        }
        if ($exe -and (Test-UserWritablePath $exe)) {
            # WPS、Zoom 等正规软件的更新程序常装在 AppData：签名有效或任务已禁用时降为低
            $sev = 'medium'; if ($disabled -or ($facts -and $facts.signature -eq 'Valid')) { $sev = 'low' }
            Add-Flag 'scheduledTasks' 'runs-from-user-folder' $sev $t.TaskName "从用户可写目录运行（签名：$($facts.signature)，状态：$($t.State)）：$exe"; $hit = $true
        }
        if ("$($t.TaskName) $text" -match $Seeds.leftoverOrBundle) { Add-Flag 'scheduledTasks' 'seed-bundle' 'medium' $t.TaskName '命中常见捆绑/残留软件种子名单'; $hit = $true }
        if (-not $isMsPath -and -not $disabled -and $interval -match '^PT(\d+M|1H)$') { Add-Flag 'scheduledTasks' 'frequent' 'info' $t.TaskName "每 $interval 重复触发" }

        if ($isMsPath -and -not $hit) { continue }
        if ($isMsPath) { $flaggedMs++ }
        [void]$out.Add([ordered]@{ path = $t.TaskPath; name = $t.TaskName; state = [string]$t.State; author = $t.Author
                                   actions = $acts; repeatInterval = $interval
                                   exists = if ($facts) { $facts.exists } else { $null }
                                   company = if ($facts) { $facts.company } else { $null }
                                   signature = if ($facts) { $facts.signature } else { $null } })
    }
    [ordered]@{ nonMicrosoftCount = $out.Count - $flaggedMs; flaggedMicrosoftCount = $flaggedMs; items = @($out) }
}

# 6. 驱动 ----------------------------------------------------------------------
$Sections['drivers'] = Invoke-Section 'drivers' '驱动（较慢）' {
    $result = [ordered]@{}

    # 6a. 驱动仓库中的第三方驱动包（需管理员）
    $pkgs = @(); if ($IsAdmin) { $pkgs = @((Safe 'Get-WindowsDriver' { Get-WindowsDriver -Online }) | Where-Object { $_ }) }
    $inUse = @{}
    foreach ($d in (Get-Cim Win32_PnPSignedDriver)) { if ($d.InfName) { $inUse[$d.InfName.ToLowerInvariant()] = $d.DeviceName } }
    $pkgList = foreach ($p in $pkgs) {
        [ordered]@{ published = $p.Driver; inf = (Split-Path $p.OriginalFileName -Leaf); provider = $p.ProviderName
                    class = $p.ClassName; version = $p.Version; date = if ($p.Date) { $p.Date.ToString('yyyy-MM-dd') } else { $null }
                    usedBy = $inUse[$p.Driver.ToLowerInvariant()] }
    }
    # 按 INF 名 + 提供商分组：oemvista.inf 这类通用名会被不同厂商的 TAP 网卡共用，不是同一驱动的多个版本。
    # 打印机驱动不走 PnP，Win32_PnPSignedDriver 里永远查不到"在用"；微软提供的包按红线 3 不动
    $dups = @($pkgList | Where-Object { $_.class -ne 'Printer' -and $_.provider -notmatch '^Microsoft' } |
        Group-Object { '{0} | {1}' -f $_.inf, $_.provider } | Where-Object { $_.Count -gt 1 })
    foreach ($g in $dups) {
        $unused = @($g.Group | Where-Object { -not $_.usedBy })
        if ($unused.Count) {
            Add-Flag 'drivers' 'duplicate-versions' 'low' $g.Group[0].inf "$($g.Group[0].provider) 的驱动仓库保留了 $($g.Count) 个版本，其中 $($unused.Count) 个没有设备在用：$(($unused | ForEach-Object { $_.published + ' ' + $_.version }) -join '，')"
        }
    }
    $result.packages = [ordered]@{ available = $IsAdmin; total = @($pkgList).Count; duplicateGroups = $dups.Count; items = @($pkgList) }

    # 6b. 内核驱动服务（排除 Windows 自带）
    # 用 ServiceController.GetDevices() 取运行状态、注册表取启动类型和路径：实测比 Win32_SystemDriver 快约 7 秒；
    # 失败时退回 WMI。输出字段与 Win32_SystemDriver 保持一致（Name / State / StartMode / PathName）
    $drvList = Safe 'ServiceController.GetDevices' {
        Add-Type -AssemblyName System.ServiceProcess
        $startNames = @{ 0 = 'Boot'; 1 = 'System'; 2 = 'Auto'; 3 = 'Manual'; 4 = 'Disabled' }
        foreach ($dev in [System.ServiceProcess.ServiceController]::GetDevices()) {
            $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$($dev.ServiceName)")
            $img = $null; $start = $null
            if ($key) { $img = $key.GetValue('ImagePath'); $start = $key.GetValue('Start'); $key.Close() }
            if (-not $img) { $img = "$env:windir\System32\drivers\$($dev.ServiceName).sys" }  # 没写 ImagePath 时系统按这个默认位置加载
            $mode = $null; if ($null -ne $start) { $mode = $startNames[[int]$start] }
            [pscustomobject]@{ Name = $dev.ServiceName; State = [string]$dev.Status; StartMode = $mode; PathName = [string]$img }
        }
    }
    if (-not $drvList) { $drvList = Get-Cim Win32_SystemDriver }
    $kd = New-Object System.Collections.ArrayList
    foreach ($s in @($drvList | Where-Object { $_ })) {
        $exe = Resolve-ExePath $s.PathName
        $facts = Get-FileFacts $exe
        if ((Test-InboxFile $facts) -or (Test-MicrosoftFile $facts)) { continue }
        $item = [ordered]@{ name = $s.Name; state = $s.State; startMode = $s.StartMode; path = $exe; exists = $facts.exists
                            company = $facts.company; signer = $facts.signer; signature = $facts.signature }
        [void]$kd.Add($item)
        $label = "$($s.Name) $exe"
        if ($exe -and -not $facts.exists) {
            # 文件已不在，就谈不上是在运行的网银 / 反作弊驱动：只报登记残留，种子名单命中写进证据，避免同一对象重复扣分
            $kind = ''
            if ($label -match $Seeds.bankSecurity) { $kind = '（像是网银安全控件）' }
            elseif ($label -match $Seeds.antiCheatOrAccel) { $kind = '（像是游戏反作弊或加速器）' }
            elseif ($label -match $Seeds.leftoverOrBundle) { $kind = '（命中捆绑 / 残留软件种子名单）' }
            Add-Flag 'drivers' 'binary-missing' 'low' $s.Name "驱动服务登记还在，但文件已不存在$kind：$exe"
        } else {
            if ($label -match $Seeds.antiCheatOrAccel) { Add-Flag 'drivers' 'seed-anticheat-accel' 'medium' $s.Name "疑似游戏反作弊、加速器或下载器驱动（$($s.State)，签名者：$($facts.signer)）" }
            if ($label -match $Seeds.bankSecurity)     { Add-Flag 'drivers' 'seed-bank-security' 'medium' $s.Name "疑似网银安全控件驱动（$($s.State)，签名者：$($facts.signer)）" }
            if ($label -match $Seeds.leftoverOrBundle) { Add-Flag 'drivers' 'seed-bundle' 'medium' $s.Name '命中常见捆绑/残留软件种子名单' }
        }
        if ($s.State -eq 'Running' -and $s.StartMode -in @('Boot','System')) {
            Add-Flag 'drivers' 'boot-start-third-party' 'info' $s.Name '第三方驱动在系统启动早期加载（红线 4：禁止直接删除）'
        }
    }
    $result.kernelDrivers = [ordered]@{ thirdPartyCount = $kd.Count; items = @($kd) }

    # 6c. 设备类过滤驱动（删除前必须先摘除，红线 2）
    $filters = New-Object System.Collections.ArrayList
    foreach ($cls in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class')) {
        foreach ($v in @('UpperFilters', 'LowerFilters')) {
            $vals = Get-RegValue $cls.PSPath $v
            foreach ($name in @($vals)) {
                if (-not $name) { continue }
                $img = Get-RegValue "HKLM:\SYSTEM\CurrentControlSet\Services\$name" 'ImagePath'
                $exe = Resolve-ExePath $img
                if (-not $exe) { $exe = "$env:windir\System32\drivers\$name.sys" }
                $facts = Get-FileFacts $exe
                $thirdParty = -not ((Test-InboxFile $facts) -or (Test-MicrosoftFile $facts))
                $className = Get-RegValue $cls.PSPath 'Class'
                [void]$filters.Add([ordered]@{ classGuid = $cls.PSChildName; className = $className; kind = $v; driver = $name
                                               company = $facts.company; thirdParty = $thirdParty })
                if ($thirdParty) { Add-Flag 'drivers' 'third-party-class-filter' 'medium' $name "挂在 $className 设备类的 $v 上（删除前必须先摘除，否则设备可能失灵）" }
            }
        }
    }
    $result.classFilters = @($filters)

    # 6d. 幽灵设备
    $pnp = Safe 'Get-PnpDevice' { Get-PnpDevice }
    $ghost = @($pnp | Where-Object { $_ -and -not $_.Present -and $_.InstanceId -notmatch '^(SWD|ROOT)\\' })
    $result.ghostDevices = [ordered]@{ total = $ghost.Count
        items = @($ghost | Select-Object -First 60 | ForEach-Object { [ordered]@{ class = $_.Class; name = $_.FriendlyName } }) }

    # 6e. 基于虚拟化的安全
    $dg = Get-Cim Win32_DeviceGuard 'root\Microsoft\Windows\DeviceGuard'
    $scen = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios'
    $result.virtualizationSecurity = [ordered]@{
        servicesRunning          = @($dg.SecurityServicesRunning)
        memoryIntegrityEnabled   = Get-RegValue "$scen\HypervisorEnforcedCodeIntegrity" 'Enabled'
        kernelStackProtection    = Get-RegValue "$scen\KernelShadowStacks" 'Enabled'
    }
    if ($result.virtualizationSecurity.memoryIntegrityEnabled -ne 1) {
        Add-Flag 'drivers' 'memory-integrity-off' 'medium' '内存完整性' '未开启；常见原因是有不兼容的旧驱动，可在 Windows 安全中心查看列表'
    }
    $result
}

# 7. 已装软件 ------------------------------------------------------------------
$Sections['software'] = Invoke-Section 'software' '已装软件' {
    $keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
              'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
              'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $apps = foreach ($k in $keys) {
        Get-ItemProperty $k | Where-Object { $_.DisplayName -and $_.SystemComponent -ne 1 -and -not $_.ParentKeyName } | ForEach-Object {
            [ordered]@{ name = $_.DisplayName; publisher = $_.Publisher; version = $_.DisplayVersion
                        installDate = $_.InstallDate; sizeMB = if ($_.EstimatedSize) { [int]($_.EstimatedSize / 1024) } else { $null }
                        location = $_.InstallLocation }
        }
    }
    $apps = @($apps | Sort-Object { $_.name } -Unique | Sort-Object { [int]$_.sizeMB } -Descending)
    foreach ($a in $apps) {
        if ("$($a.name) $($a.publisher)" -match $Seeds.leftoverOrBundle) { Add-Flag 'software' 'seed-bundle' 'medium' $a.name '命中常见捆绑软件种子名单，确认是否主动安装' }
        if ($a.sizeMB -ge 5000 -and $a.location -match '^(?i)C:') { Add-Flag 'software' 'large-on-c' 'info' $a.name "约 $([math]::Round($a.sizeMB/1024,1)) GB，装在 C 盘" }
    }
    $dupCats = [ordered]@{}
    foreach ($cat in $Categories.Keys) {
        $hits = @($apps | Where-Object { $_.name -match $Categories[$cat] } | ForEach-Object { $_.name })
        if ($hits.Count -ge 2) { $dupCats[$cat] = $hits; Add-Flag 'software' 'duplicate-category' 'info' $cat "同类软件 $($hits.Count) 个：$($hits -join '、')" }
    }
    $appxAll = Safe 'Get-AppxPackage' { Get-AppxPackage }
    $appx = @($appxAll | Where-Object { $_ -and -not $_.IsFramework -and $_.SignatureKind -ne 'System' })
    [ordered]@{ total = $apps.Count; storeAppCount = $appx.Count; duplicateCategories = $dupCats; items = $apps }
}

# 8. 存储 ----------------------------------------------------------------------
$Sections['storage'] = Invoke-Section 'storage' '存储（较慢）' {
    $vols = foreach ($d in (Get-Cim Win32_LogicalDisk -Filter 'DriveType=3')) {
        $freePct = [math]::Round([double]$d.FreeSpace / [double]$d.Size * 100)
        if ($d.DeviceID -eq $env:SystemDrive -and $freePct -lt 15) {
            Add-Flag 'storage' 'system-drive-low' 'medium' $d.DeviceID "系统盘只剩 $([math]::Round($d.FreeSpace/1GB,1)) GB（$freePct%）"
        }
        [ordered]@{ drive = $d.DeviceID; label = $d.VolumeName; sizeGB = [math]::Round($d.Size / 1GB, 1); freeGB = [math]::Round($d.FreeSpace / 1GB, 1); freePct = $freePct }
    }
    $hib = Get-Item -LiteralPath "$env:SystemDrive\hiberfil.sys" -Force
    $pf  = Get-Cim Win32_PageFileUsage
    $sys = [ordered]@{
        hiberfilGB = if ($hib) { [math]::Round($hib.Length / 1GB, 1) } else { 0 }
        pagefileGB = if ($pf) { [math]::Round((($pf | Measure-Object AllocatedBaseSize -Sum).Sum) / 1024, 1) } else { $null }
    }

    # 物理硬盘：SSD 还是机械硬盘。MediaType / BusType / HealthStatus 在 CIM 里是数字，按文档换成英文名（不随系统语言变化）
    $mediaNames  = @{ 0 = 'Unspecified'; 3 = 'HDD'; 4 = 'SSD'; 5 = 'SCM' }
    $busNames    = @{ 1 = 'SCSI'; 3 = 'ATA'; 7 = 'USB'; 8 = 'RAID'; 10 = 'SAS'; 11 = 'SATA'; 12 = 'SD'; 13 = 'MMC'; 17 = 'NVMe'; 18 = 'SCM'; 19 = 'UFS' }
    $healthNames = @{ 0 = 'Healthy'; 1 = 'Warning'; 2 = 'Unhealthy'; 5 = 'Unknown' }
    $disks = @((Safe 'MSFT_PhysicalDisk' { Get-CimInstance -Namespace 'root\Microsoft\Windows\Storage' -ClassName MSFT_PhysicalDisk -OperationTimeoutSec 20 -ErrorAction Stop }) |
        Where-Object { $_ } | ForEach-Object {
            $m = $mediaNames[[int]$_.MediaType]; if (-not $m) { $m = [string]$_.MediaType }
            $b = $busNames[[int]$_.BusType]; if (-not $b) { $b = [string]$_.BusType }
            $hs = $healthNames[[int]$_.HealthStatus]; if (-not $hs) { $hs = [string]$_.HealthStatus }
            [ordered]@{ name = $_.FriendlyName; mediaType = $m; busType = $b; sizeGB = [math]::Round([double]$_.Size / 1GB, 1); health = $hs }
        })

    $folders = [ordered]@{}
    if (-not $Quick) {
        # 临时文件、各类缓存、Windows.old 等按清理目录表另外测量（见下面的 junk）
        $cands = [ordered]@{
            downloads       = Join-Path $env:USERPROFILE 'Downloads'
            condaPkgs       = @("$env:USERPROFILE\anaconda3\pkgs", "$env:USERPROFILE\miniconda3\pkgs", "$env:USERPROFILE\.conda\pkgs") | Where-Object { Test-Path $_ } | Select-Object -First 1
            recycleBinC     = "$env:SystemDrive\`$Recycle.Bin"
        }
        $withBreakdown = @('downloads')
        foreach ($k in $cands.Keys) {
            if (-not $cands[$k]) { continue }
            $m = Measure-Folder $cands[$k] 15 -Breakdown:($withBreakdown -contains $k)
            if ($m) { $folders[$k] = $m }
        }

        # 中文用户 C 盘被占满的头号原因：聊天软件的文件缓存默认放在"文档"里
        $docs = [Environment]::GetFolderPath('MyDocuments')
        $wechatDir = "$docs\WeChat Files"
        $wechatSave = Get-RegValue 'HKCU:\Software\Tencent\WeChat' 'FileSavePath'
        if ($wechatSave -and $wechatSave -notmatch '^MyDocument') { $wechatDir = "$($wechatSave.TrimEnd('\'))\WeChat Files" }
        $chatCands = [ordered]@{
            '微信'     = $wechatDir
            '微信 4.x' = "$docs\xwechat_files"
            'QQ'       = "$docs\Tencent Files"
            '企业微信' = "$docs\WXWork"
            '钉钉'     = "$env:APPDATA\DingTalk"
        }
        $chat = [ordered]@{}
        foreach ($k in $chatCands.Keys) {
            $m = Measure-Folder $chatCands[$k] 10 -Breakdown
            if (-not $m) { continue }
            $chat[$k] = $m
            if ($m.sizeGB -ge 5 -and $m.path -like "$env:SystemDrive\*") {
                $atLeast = ''; if ($m.partial) { $atLeast = '至少 ' }
                Add-Flag 'storage' 'chat-cache-large' 'low' $k "$k 的聊天文件在系统盘占 $atLeast$($m.sizeGB) GB，可在软件设置里清理，或把存储位置改到其他盘"
            }
        }
        $folders.chatApps = $chat
        if ($folders.condaPkgs -and $folders.condaPkgs.sizeGB -ge 2) { Add-Flag 'storage' 'conda-cache' 'low' 'conda pkgs' "安装包缓存 $($folders.condaPkgs.sizeGB) GB，可用 conda clean -a 清理" }
        if ($folders.downloads -and $folders.downloads.sizeGB -ge 10) { Add-Flag 'storage' 'downloads-large' 'info' 'Downloads' "下载文件夹 $($folders.downloads.sizeGB) GB" }
    }

    # 按清理目录表量各类缓存和系统遗留文件（kb/junk.json；A 类 Iris 可以直接清理，B 类交给 Windows 自带的清理）
    $junk = $null
    if ($Junk -and -not $Quick) {
        $jvars = @{ TEMP = $env:TEMP; LOCALAPPDATA = $env:LOCALAPPDATA; APPDATA = $env:APPDATA; USERPROFILE = $env:USERPROFILE
                    PROGRAMDATA = $env:ProgramData; WINDIR = $env:windir; SYSTEMDRIVE = $env:SystemDrive; STEAM = $null }
        $sp = Get-RegValue 'HKCU:\Software\Valve\Steam' 'SteamPath'
        if ($sp) { $jvars.STEAM = ([string]$sp) -replace '/', '\' }
        $running = @{}
        foreach ($p in @(Get-Process)) { $running[$p.ProcessName.ToLowerInvariant()] = $true }
        $jt = [Diagnostics.Stopwatch]::StartNew()
        $jitems = New-Object System.Collections.ArrayList
        foreach ($cat in @($Junk.categories)) {
            if ($jt.Elapsed.TotalSeconds -gt 90) { [void]$CollectErrors.Add('junk: 90 秒内没有量完，其余类别跳过'); break }
            $m = Measure-JunkCategory $cat $jvars $running 15
            if ($m) { [void]$jitems.Add($m) }
        }
        $cleanMB = 0.0; $groups = [ordered]@{}; $anyPartial = $false
        foreach ($x in $jitems) {
            if ($x.tier -ne 'A' -or $x.cleanableMB -le 0) { continue }
            $cleanMB += $x.cleanableMB
            $groups[$x.group] = [double]$groups[$x.group] + $x.cleanableMB
            if ($x.partial) { $anyPartial = $true }
        }
        $junk = [ordered]@{ catalog = $Junk.schema; items = @($jitems); cleanableMB = [math]::Round($cleanMB, 1) }
        if ($cleanMB -ge 1024) {
            $top = @($groups.Keys | Sort-Object { $groups[$_] } -Descending | Select-Object -First 3 | ForEach-Object { "$_ $(Format-Size $groups[$_])" })
            $sev = 'low'; if ($cleanMB -ge 5120) { $sev = 'medium' }
            $atLeast = ''; if ($anyPartial) { $atLeast = '至少' }
            Add-Flag 'storage' 'junk-cleanable' $sev '可清理的缓存' "$atLeast$(Format-Size $cleanMB) 可以直接清理：$($top -join '、')"
        }
        foreach ($x in $jitems) {
            if ($x.tier -eq 'B' -and $x.sizeMB -ge 2048 -and @('windows-old', 'windows-upgrade-temp', 'delivery-optimization', 'windows-update-download') -contains $x.id) {
                $atLeast = ''; if ($x.partial) { $atLeast = '至少 ' }
                Add-Flag 'storage' 'system-leftover' 'low' $x.name "$($x.name) 占 $atLeast$(Format-Size $x.sizeMB)，可以用 Windows 自带的「磁盘清理 → 清理系统文件」删除"
            }
        }
    }

    # Steam 等启动器可能把游戏库分散在多个盘
    $steamLibs = @()
    $steamPath = Get-RegValue 'HKCU:\Software\Valve\Steam' 'SteamPath'
    if ($steamPath) {
        $vdf = Join-Path ($steamPath -replace '/', '\') 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $common = Join-Path $lib 'steamapps\common'
                $games = @(); if (Test-Path -LiteralPath $common) { $games = @(Get-ChildItem -LiteralPath $common -Directory | ForEach-Object { $_.Name }) }
                $steamLibs += [ordered]@{ path = $lib; games = $games }
                if ($lib -match '^(?i)C:' -and $games.Count) { Add-Flag 'storage' 'games-on-c' 'info' $lib "C 盘上的 Steam 游戏库：$($games -join '、')" }
            }
        }
    }

    $rootStd = '(?i)^(\$.*|Windows|Program Files|Program Files \(x86\)|ProgramData|Users|PerfLogs|Recovery|System Volume Information|Intel|AMD|NVIDIA|Config\.Msi|Documents and Settings|OneDriveTemp|inetpub|Drivers|ESD)$'
    $cRoot = @(Get-ChildItem -LiteralPath "$env:SystemDrive\" -Directory -Force | Where-Object { $_.Name -notmatch $rootStd } | ForEach-Object { $_.Name })

    [ordered]@{ volumes = @($vols); physicalDisks = $disks; system = $sys; folders = $folders; junk = $junk; steamLibraries = $steamLibs; nonStandardFoldersAtSystemRoot = $cRoot }
}

# 9. 电源与电池 ----------------------------------------------------------------
$Sections['power'] = Invoke-Section 'power' '电源与电池' {
    $plan = Get-Cim Win32_PowerPlan 'root\cimv2\power' 'IsActive=True'
    $overlayMap = @{ '961cc777-2547-4f9d-8174-7d86181b8a7a' = '最佳能效'; '00000000-0000-0000-0000-000000000000' = '平衡'; 'ded574b5-45a0-4f42-8737-46345c09c238' = '最佳性能' }
    $ovAc = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' 'ActiveOverlayAcPowerScheme'
    $ovDc = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' 'ActiveOverlayDcPowerScheme'
    $fast = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled'
    $hib  = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
    if ($fast -eq 1) { Add-Flag 'power' 'fast-startup-on' 'low' '快速启动' '开启中：点"关机"不会清空内核和驱动状态，长期不重启会积累内存占用' }

    $battery = $null
    $static = Get-Cim BatteryStaticData 'root\wmi' | Select-Object -First 1
    $full   = Get-Cim BatteryFullChargedCapacity 'root\wmi' | Select-Object -First 1
    $cycle  = Get-Cim BatteryCycleCount 'root\wmi' | Select-Object -First 1
    $status = Get-Cim BatteryStatus 'root\wmi' | Select-Object -First 1
    if ($static -or $full) {
        $designed = $static.DesignedCapacity; $cycles = $cycle.CycleCount; $designSource = 'wmi'
        if (-not $designed -and $BatteryReportProc) {
            # 部分机器的 WMI 读不到设计容量（实测华为这台就是），改用一开始就在后台生成的 powercfg XML 电池报告。
            # XML 元素名不随系统语言变化
            $sw = [Diagnostics.Stopwatch]::StartNew()
            if (-not $BatteryReportProc.WaitForExit(60000)) { [void]$CollectErrors.Add('powercfg batteryreport: 60 秒内没有完成') }
            Add-Perf 'wait powercfg batteryreport' $sw.Elapsed.TotalSeconds
            $xml = Safe 'powercfg batteryreport' {
                $doc = New-Object Xml.XmlDocument
                $doc.Load($BatteryReportPath)   # 按文件声明的编码读取
                $doc
            }
            $node = $null; if ($xml) { $node = $xml.SelectSingleNode("//*[local-name()='Battery']") }
            if ($node) {
                $d = $node.SelectSingleNode("*[local-name()='DesignCapacity']"); $c = $node.SelectSingleNode("*[local-name()='CycleCount']")
                if ($d -and [int64]$d.InnerText -gt 0) { $designed = [int64]$d.InnerText; $designSource = 'powercfg' }
                if (-not $cycles -and $c) { $cycles = [int64]$c.InnerText }
            }
        }
        $health = $null
        if ($designed -and $full.FullChargedCapacity) { $health = [math]::Round($full.FullChargedCapacity / $designed * 100, 1) }
        $remainPct = $null
        if ($status.RemainingCapacity -and $full.FullChargedCapacity) { $remainPct = [math]::Min(100, [math]::Round($status.RemainingCapacity / $full.FullChargedCapacity * 100)) }
        $battery = [ordered]@{ designedmWh = $designed; designSource = $designSource; fullChargedmWh = $full.FullChargedCapacity; healthPct = $health; remainingPct = $remainPct
                               cycles = $cycles; charging = $status.Charging; powerOnline = $status.PowerOnline
                               chargeRatemW = $status.ChargeRate; dischargeRatemW = $status.DischargeRate }
        if ($health -ne $null -and $health -lt 80) { Add-Flag 'power' 'battery-worn' 'medium' '电池' "健康度 $health%，循环 $cycles 次" }
        # 电量 95% 以上时充电功率本来就会降下来（涓流），不报
        if ($status.Charging -and $status.ChargeRate -and $status.ChargeRate -lt 10000 -and -not ($remainPct -ge 95)) {
            Add-Flag 'power' 'slow-charging' 'info' '电池' "当前充电功率 $([math]::Round($status.ChargeRate/1000,1)) W，可能是负载高或充电器功率不足"
        }
    }
    [ordered]@{ activePlan = $plan.ElementName
                overlayAc = if ($ovAc) { $overlayMap[([string]$ovAc).ToLowerInvariant()] } else { $null }
                overlayDc = if ($ovDc) { $overlayMap[([string]$ovDc).ToLowerInvariant()] } else { $null }
                fastStartup = $fast; hibernateEnabled = $hib; battery = $battery }
}

# 10. 安全设置 -----------------------------------------------------------------
$Sections['security'] = Invoke-Section 'security' '安全设置' {
    $mp = Safe 'Get-MpComputerStatus' { Get-MpComputerStatus }
    $def = $null
    if ($mp) {
        $def = [ordered]@{ antivirusEnabled = $mp.AntivirusEnabled; realTimeProtection = $mp.RealTimeProtectionEnabled
                           tamperProtected = $mp.IsTamperProtected; signatureAgeDays = $mp.AntivirusSignatureAge
                           quickScanAgeDays = $mp.QuickScanAge; fullScanAgeDays = $mp.FullScanAge }
        if (-not $mp.RealTimeProtectionEnabled) { Add-Flag 'security' 'defender-rtp-off' 'high' 'Defender' '实时保护已关闭（如装有其他杀毒软件则属正常）' }
        if ($mp.AntivirusSignatureAge -gt 7) { Add-Flag 'security' 'defender-signature-old' 'medium' 'Defender' "病毒库已 $($mp.AntivirusSignatureAge) 天未更新" }
    }
    $threats = @((Safe 'Get-MpThreat' { Get-MpThreat }) | Where-Object { $_ } | ForEach-Object { [ordered]@{ threatId = [string]$_.ThreatID; name = $_.ThreatName; severityId = $_.SeverityID; active = $_.IsActive } })
    # 检测记录带文件路径（Resources 形如 file:_C:\...），用来告诉用户每条威胁对应的是哪个文件
    $detections = @((Safe 'Get-MpThreatDetection' { Get-MpThreatDetection }) | Where-Object { $_ })
    function Get-DetectionFiles([string]$ThreatId) {
        @($detections | Where-Object { [string]$_.ThreatID -eq $ThreatId } | ForEach-Object { $_.Resources } |
            Where-Object { $_ } | ForEach-Object { $_ -replace '^\w+:_', '' } | Select-Object -Unique -First 3)
    }
    $since = (Get-Date).AddDays(-30)
    $recentDetections = @($detections | Where-Object { $_.InitialDetectionTime -ge $since } | Sort-Object InitialDetectionTime -Descending | Select-Object -First 20 | ForEach-Object {
        $tid = [string]$_.ThreatID
        $name = ($threats | Where-Object { $_.threatId -eq $tid } | Select-Object -First 1).name
        [ordered]@{ threatId = $tid; name = $name; time = $_.InitialDetectionTime.ToString('yyyy-MM-dd HH:mm')
                    remediated = [bool]$_.ActionSuccess; files = @($_.Resources | Where-Object { $_ } | ForEach-Object { $_ -replace '^\w+:_', '' } | Select-Object -First 3) } })
    foreach ($d in $recentDetections) {
        if (-not $d.remediated) { Add-Flag 'security' 'detection-not-remediated' 'high' 'Defender' "30 天内的检测没有处理成功（$($d.time)）：$($d.files -join '，')" }
    }
    if (@($threats | Where-Object { $_.active }).Count) { Add-Flag 'security' 'active-threat' 'high' 'Defender' 'Defender 记录中有尚未处理的威胁' }

    $fw = @((Safe 'Get-NetFirewallProfile' { Get-NetFirewallProfile }) | Where-Object { $_ } | ForEach-Object { [ordered]@{ profile = $_.Name; enabled = ([string]$_.Enabled -eq 'True') } })
    foreach ($p in $fw) { if (-not $p.enabled) { Add-Flag 'security' 'firewall-off' 'medium' "防火墙 $($p.profile)" '该网络类型的防火墙已关闭' } }

    $uac = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'EnableLUA'
    if ($uac -eq 0) { Add-Flag 'security' 'uac-off' 'high' 'UAC' '用户账户控制已关闭' }

    $bl = $null
    if ($IsAdmin) { $v = Safe 'Get-BitLockerVolume' { Get-BitLockerVolume -MountPoint $env:SystemDrive }; if ($v) { $bl = [string]$v.ProtectionStatus } }
    $sb = $null
    if ($IsAdmin) { try { $sb = Confirm-SecureBootUEFI -ErrorAction Stop } catch { $sb = $null } }

    # Defender 排除项：恶意软件常把自己的目录或进程加进排除项
    $excl = $null
    $allowed = New-Object System.Collections.ArrayList
    $catalog = @{}
    if ($CatalogJob) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        if (Wait-Job $CatalogJob -Timeout 90) {
            foreach ($c in @(Safe 'Get-MpThreatCatalog' { Receive-Job $CatalogJob -ErrorAction Stop })) { if ($c -and $c.id) { $catalog[$c.id] = $c } }
        } else { [void]$CollectErrors.Add('Get-MpThreatCatalog: 后台查询 90 秒内没有完成') }
        Remove-Job $CatalogJob -Force
        Add-Perf 'wait threat catalog job' $sw.Elapsed.TotalSeconds
    }
    $pref = Safe 'Get-MpPreference' { Get-MpPreference }
    if ($pref) {
        $excl = [ordered]@{ paths = @($pref.ExclusionPath | Where-Object { $_ }); processes = @($pref.ExclusionProcess | Where-Object { $_ })
                            extensions = @($pref.ExclusionExtension | Where-Object { $_ }) }
        $lowCount = 0
        foreach ($pair in @(@('path', $excl.paths, '路径'), @('process', $excl.processes, '进程'), @('extension', $excl.extensions, '扩展名'))) {
            foreach ($v in $pair[1]) {
                $risk = Get-ExclusionRisk $pair[0] ([string]$v)
                if ($pair[0] -eq 'path' -and $risk -and $v -notmatch '[*?%]' -and -not (Test-Path -LiteralPath $v)) {
                    Add-Flag 'security' 'defender-exclusion-orphan' 'medium' 'Defender 排除项' "排除的目录已不存在（软件卸载后排除项没清掉，等于留了一个不被扫描的位置）：$v"
                    continue
                }
                if ($risk -eq 'low') { $lowCount++ }
                elseif ($risk) { Add-Flag 'security' 'defender-exclusion' $risk 'Defender 排除项' "按$($pair[2])排除，不会被扫描：$v" }
            }
        }
        if ($lowCount) { Add-Flag 'security' 'defender-exclusion' 'info' 'Defender 排除项' "另有 $lowCount 条排除项（普通目录或扩展名），请确认都是自己加的" }

        # "允许的威胁"：用户在 Defender 弹窗里点过"允许"的检测，以后不会再报（动作代码 6 = Allow）
        $ids = @($pref.ThreatIDDefaultAction_Ids); $acts = @($pref.ThreatIDDefaultAction_Actions)
        for ($i = 0; $i -lt $ids.Count -and $i -lt $acts.Count; $i++) {
            if ([int]$acts[$i] -ne 6) { continue }
            $known = $threats | Where-Object { $_.threatId -eq [string]$ids[$i] } | Select-Object -First 1
            $item = [ordered]@{ threatId = [string]$ids[$i]; name = $null; severityId = $null }
            if ($known) { $item.name = $known.name; $item.severityId = $known.severityId }
            else {
                # 威胁历史可能已被清理，名称和严重程度从后台查好的威胁目录里取（实测有一条"高"因此被当成了"低"）
                $cat = $catalog[[string]$ids[$i]]
                if ($cat) { $item.name = $cat.name; $item.severityId = $cat.severityId }
            }
            $item.files = @(Get-DetectionFiles $item.threatId)   # 只有一个时 PowerShell 会展开成字符串，统一成数组
            [void]$allowed.Add($item)
            # SeverityID：1 低、2 中、4 高、5 严重
            $sev = 'low'; if ($item.severityId -ge 4) { $sev = 'high' } elseif ($item.severityId -eq 2) { $sev = 'medium' }
            $label = $item.name; if (-not $label) { $label = "威胁编号 $($item.threatId)" }
            $where = '对应文件不详（检测记录可能已被清理）'; if ($item.files.Count) { $where = "对应文件：$($item.files -join '，')" }
            Add-Flag 'security' 'defender-allowed-threat' $sev $label "曾被 Defender 检测到、被手动设为允许，之后不会再报。$where"
        }
    }

    [ordered]@{ defender = $def; defenderExclusions = $excl; allowedThreats = @($allowed); recentDetections = $recentDetections; threatHistory = $threats; firewall = $fw; uacEnabled = $uac; bitlockerSystemDrive = $bl; secureBoot = $sb }
}

# 11. 浏览器与网络 -------------------------------------------------------------
$Sections['browserNetwork'] = Invoke-Section 'browserNetwork' '浏览器与网络' {
    $pol = [ordered]@{
        edgeStartupBoost   = Get-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'StartupBoostEnabled'
        edgeBackgroundMode = Get-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'BackgroundModeEnabled'
        chromeBackground   = Get-RegValue 'HKLM:\SOFTWARE\Policies\Google\Chrome' 'BackgroundModeEnabled'
    }
    $edgeProcs = @(Get-Process -Name msedge).Count

    # 主力浏览器：默认浏览器 + 每个浏览器最近一次浏览网页的时间（只看历史记录文件的修改时间，不读内容）
    $progId = [string](Get-RegValue 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' 'ProgId')
    $bdefs = @(
        @('edge', 'Microsoft Edge', @('msedge'), @("$env:LOCALAPPDATA\Microsoft\Edge\User Data"), '^MSEdge', ''),
        @('chrome', 'Google Chrome', @('chrome'), @("$env:LOCALAPPDATA\Google\Chrome\User Data"), '^ChromeHTML', ''),
        @('firefox', 'Firefox', @('firefox'), @("$env:APPDATA\Mozilla\Firefox\Profiles"), '^FirefoxURL', ''),
        @('brave', 'Brave', @('brave'), @("$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\User Data"), '^BraveHTML', ''),
        @('360se', '360安全浏览器', @('360se'), @("$env:APPDATA\360se6\User Data"), '^360SE', '360安全浏览器'),
        @('360chrome', '360极速浏览器', @('360chrome', '360ChromeX'), @("$env:LOCALAPPDATA\360ChromeX\Chrome\User Data", "$env:LOCALAPPDATA\360Chrome\Chrome\User Data"), '^360Chrome', '360极速浏览器'),
        @('qqbrowser', 'QQ浏览器', @('QQBrowser'), @("$env:LOCALAPPDATA\Tencent\QQBrowser\User Data"), '^QQBrowser', 'QQ浏览器'),
        @('sogou', '搜狗高速浏览器', @('SogouExplorer'), @("$env:APPDATA\SogouExplorer"), '^Sogou', '搜狗高速浏览器'),
        @('2345', '2345加速浏览器', @('2345Explorer'), @("$env:APPDATA\2345Explorer"), '^2345', '2345加速浏览器')
    )
    $startupItems = @()
    if ($Sections['startup'] -and $Sections['startup'].items) { $startupItems = @($Sections['startup'].items | Where-Object { $_ -and $_.enabled -ne $false }) }
    $swItems = @()
    if ($Sections['software'] -and $Sections['software'].items) { $swItems = @($Sections['software'].items) }
    $now = Get-Date
    $browsers = New-Object System.Collections.ArrayList
    foreach ($d in $bdefs) {
        $dirs = @($d[3] | Where-Object { Test-Path -LiteralPath $_ })
        $uninstall = ''
        if ($d[5]) { $hit = $swItems | Where-Object { [string]$_.name -like "*$($d[5])*" } | Select-Object -First 1; if ($hit) { $uninstall = [string]$hit.name } }
        if (-not $dirs.Count -and -not $uninstall) { continue }
        $newest = $null
        foreach ($dir in $dirs) {
            foreach ($f in @(Get-ChildItem -LiteralPath $dir -Recurse -Depth 2 -File -Force -Include 'History', 'places.sqlite')) {
                if (-not $newest -or $f.LastWriteTime -gt $newest) { $newest = $f.LastWriteTime }
            }
        }
        $last = $null; if ($newest) { $last = [math]::Round(($now - $newest).TotalDays, 1) }
        $procs = @(Get-Process -Name $d[2])
        $mem = 0; foreach ($p in $procs) { $mem += $p.WorkingSet64 }
        $auto = @()
        foreach ($s in $startupItems) {
            foreach ($exe in $d[2]) { if ([string]$s.command -match ('\\' + [regex]::Escape($exe) + '\.exe')) { $auto += [string]$s.name; break } }
        }
        [void]$browsers.Add([ordered]@{ id = $d[0]; name = $d[1]; isDefault = ($progId -match $d[4]); lastBrowsedDays = $last
                                         processes = $procs.Count; memoryMB = [math]::Round($mem / 1MB); autostart = @($auto); uninstallName = $uninstall })
    }
    $main = $null
    $def = $browsers | Where-Object { $_.isDefault } | Select-Object -First 1
    if ($def -and $def.lastBrowsedDays -ne $null -and $def.lastBrowsedDays -le 30) { $main = $def }
    else { $main = $browsers | Where-Object { $_.lastBrowsedDays -ne $null -and $_.lastBrowsedDays -le 30 } | Sort-Object { $_.lastBrowsedDays } | Select-Object -First 1 }
    $mainId = $null; if ($main) { $mainId = $main.id }

    if (-not $main) {
        # 判断不出主力浏览器时沿用原来的规则
        if ($pol.edgeStartupBoost -ne 0 -and $edgeProcs -ge 5) { Add-Flag 'browserNetwork' 'edge-preload' 'info' 'Microsoft Edge' "后台有 $edgeProcs 个 Edge 进程，可能是启动增强在预加载" }
    } else {
        foreach ($b in $browsers) {
            if ($b.id -eq $main.id) { continue }
            if ($b.lastBrowsedDays -ne $null -and $b.lastBrowsedDays -lt 14) { continue }   # 最近也在用：不是"不用的浏览器"
            $lastText = '没有找到用它浏览过网页的记录'
            if ($b.lastBrowsedDays -ne $null) { $lastText = "已经 $([math]::Floor($b.lastBrowsedDays)) 天没用它浏览过网页" }
            $bg = @()
            if ($b.autostart.Count) { $bg += "开机自启动（$($b.autostart -join '、')）" }
            if ($b.processes -gt 0) { $bg += "此刻后台有 $($b.processes) 个进程，占约 $($b.memoryMB) MB 内存" }
            $ev = "你主要用 $($main.name)。$($b.name)$lastText"
            if ($bg.Count) { $ev += '，但' + ($bg -join '；') }
            if ($b.id -eq 'edge') {
                if (($b.autostart.Count -or $b.processes -gt 0) -and ($pol.edgeStartupBoost -ne 0 -or $pol.edgeBackgroundMode -ne 0)) { Add-Flag 'browserNetwork' 'edge-idle-background' 'low' 'Microsoft Edge' $ev }
            } elseif ($b.id -eq 'chrome') {
                if (($b.autostart.Count -or $b.processes -gt 0) -and $pol.chromeBackground -ne 0) { Add-Flag 'browserNetwork' 'chrome-idle-background' 'low' 'Google Chrome' $ev }
            } elseif ($b.uninstallName -and ($b.lastBrowsedDays -eq $null -or $b.lastBrowsedDays -ge 90)) {
                Add-Flag 'browserNetwork' 'browser-unused' 'low' $b.uninstallName $ev
            }
        }
    }

    $ext = New-Object System.Collections.ArrayList
    $roots = [ordered]@{ Chrome = "$env:LOCALAPPDATA\Google\Chrome\User Data"; Edge = "$env:LOCALAPPDATA\Microsoft\Edge\User Data" }
    foreach ($b in $roots.Keys) {
        if (-not (Test-Path -LiteralPath $roots[$b])) { continue }
        foreach ($prof in (Get-ChildItem -LiteralPath $roots[$b] -Directory | Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' })) {
            $edir = Join-Path $prof.FullName 'Extensions'
            if (-not (Test-Path -LiteralPath $edir)) { continue }
            foreach ($e in (Get-ChildItem -LiteralPath $edir -Directory)) {
                $man = Get-ChildItem -LiteralPath $e.FullName -Directory | Sort-Object Name -Descending | Select-Object -First 1
                if (-not $man) { continue }
                $mf = Join-Path $man.FullName 'manifest.json'
                $j = $null; try { $j = Get-Content -LiteralPath $mf -Raw -Encoding UTF8 | ConvertFrom-Json } catch {}
                if (-not $j) { continue }
                $name = [string]$j.name
                if ($name -match '^__MSG_(.+)__$') {
                    $msgKey = $Matches[1]
                    foreach ($loc in @('zh_CN', 'en', 'en_US')) {
                        $msgFile = Join-Path $man.FullName "_locales\$loc\messages.json"
                        if (Test-Path -LiteralPath $msgFile) {
                            try {
                                $msgs = Get-Content -LiteralPath $msgFile -Raw -Encoding UTF8 | ConvertFrom-Json
                                $hitProp = $msgs.PSObject.Properties | Where-Object { $_.Name -ieq $msgKey } | Select-Object -First 1
                                if ($hitProp) { $name = $hitProp.Value.message; break }
                            } catch {}
                        }
                    }
                }
                [void]$ext.Add([ordered]@{ browser = $b; profile = $prof.Name; id = $e.Name; name = $name; version = $j.version
                                           permissions = @($j.permissions | Where-Object { $_ -is [string] } | Select-Object -First 12) })
            }
        }
    }

    $inet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $proxy = [ordered]@{ enabled = Get-RegValue $inet 'ProxyEnable'; server = Get-RegValue $inet 'ProxyServer'; pac = Get-RegValue $inet 'AutoConfigURL' }
    if ($proxy.enabled -eq 1 -and $proxy.server -notmatch '^(?i)(127\.0\.0\.1|localhost)') { Add-Flag 'browserNetwork' 'proxy-remote' 'medium' '系统代理' "系统代理指向外部地址：$($proxy.server)" }
    if ($proxy.pac) { Add-Flag 'browserNetwork' 'proxy-pac' 'low' '系统代理' "设置了自动代理脚本：$($proxy.pac)" }

    $hostsPath = "$env:windir\System32\drivers\etc\hosts"
    $hosts = @(Get-Content -LiteralPath $hostsPath | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*#' } | ForEach-Object { $_.Trim() })
    $hostsCustom = @($hosts | Where-Object { $_ -notmatch '^(127\.0\.0\.1|::1)\s+localhost\s*$' })
    if ($hostsCustom.Count) { Add-Flag 'browserNetwork' 'hosts-custom' 'medium' 'hosts' "hosts 文件有 $($hostsCustom.Count) 条自定义记录" }

    $dns = @((Safe 'Get-DnsClientServerAddress' { Get-DnsClientServerAddress -AddressFamily IPv4 }) | Where-Object { $_ -and $_.ServerAddresses } | ForEach-Object { [ordered]@{ interface = $_.InterfaceAlias; servers = $_.ServerAddresses } })

    [ordered]@{ policies = $pol; edgeProcesses = $edgeProcs; browsers = @($browsers); mainBrowser = $mainId; extensions = @($ext); proxy = $proxy; hostsEntries = $hostsCustom; dns = $dns }
}

# 12. 驱动与系统版本 -----------------------------------------------------------
$Sections['versions'] = Invoke-Section 'versions' '驱动与系统版本' {
    $pkgItems = @()
    if ($Sections.drivers -and $Sections.drivers.packages) { $pkgItems = @($Sections.drivers.packages.items) }
    $gpus = foreach ($v in (Get-Cim Win32_VideoController)) {
        $newer = $null
        $infLeaf = $null
        if ($v.InfFilename) {
            $pub = $v.InfFilename.ToLowerInvariant()
            $mine = $pkgItems | Where-Object { $_.published -and $_.published.ToLowerInvariant() -eq $pub } | Select-Object -First 1
            if ($mine) {
                $infLeaf = $mine.inf
                $cur = $null; try { $cur = [version]$mine.version } catch {}
                $best = $pkgItems | Where-Object { $_.inf -eq $mine.inf } | Sort-Object { try { [version]$_.version } catch { [version]'0.0' } } -Descending | Select-Object -First 1
                if ($best -and $cur -and ([version]$best.version -gt $cur)) {
                    $newer = $best.version
                    Add-Flag 'versions' 'newer-driver-not-used' 'medium' $v.Name "正在用 $($mine.version)，驱动仓库里有更新的 $($best.version)（常见原因：OEM 定制驱动匹配更精确）"
                }
            }
        }
        [ordered]@{ name = $v.Name; driverVersion = $v.DriverVersion
                    driverDate = if ($v.DriverDate) { ([datetime]$v.DriverDate).ToString('yyyy-MM-dd') } else { $null }
                    inf = $infLeaf; publishedInf = $v.InfFilename; newerInStore = $newer }
    }
    $pending = $null
    if ($CheckUpdates) {
        try {
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $r = $searcher.Search('IsInstalled=0 and IsHidden=0')
            $pending = @($r.Updates | ForEach-Object { $_.Title })
        } catch { $pending = 'error' }
    }
    [ordered]@{ displayAdapters = @($gpus); pendingWindowsUpdates = $pending }
}

# 13. 稳定性 -------------------------------------------------------------------
$Sections['stability'] = Invoke-Section 'stability' '稳定性' {
    $since = (Get-Date).AddDays(-30)
    $bsod = @((Safe 'Get-WinEvent bugcheck' { Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; Id = 1001; StartTime = $since } }) |
        Where-Object { $_ } | ForEach-Object { [ordered]@{ time = $_.TimeCreated.ToString('yyyy-MM-dd HH:mm'); detail = (($_.Message -split "`r?`n") | Where-Object { $_ -match '0x' } | Select-Object -First 1) } })
    $unexpected = @((Safe 'Get-WinEvent kernel-power' { Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41; StartTime = $since } }) | Where-Object { $_ }).Count
    $dumps = @()
    if ($IsAdmin) { $dumps = @(Get-ChildItem -LiteralPath "$env:windir\Minidump" -Filter *.dmp | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | ForEach-Object { [ordered]@{ file = $_.Name; time = $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm') } }) }
    $crashes = @((Safe 'Get-WinEvent app-crash' { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; StartTime = (Get-Date).AddDays(-7) } }) |
        Where-Object { $_ } | ForEach-Object { if ($_.Properties.Count) { [string]$_.Properties[0].Value } } |
        Group-Object | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object { [ordered]@{ app = $_.Name; crashes = $_.Count } })
    if ($bsod.Count -ge 3) { Add-Flag 'stability' 'bsod-repeated' 'high' '蓝屏' "30 天内蓝屏 $($bsod.Count) 次" }
    elseif ($bsod.Count -ge 1) { Add-Flag 'stability' 'bsod' 'medium' '蓝屏' "30 天内蓝屏 $($bsod.Count) 次，最近一次：$($bsod[0].time)" }
    [ordered]@{ bugChecks30d = $bsod; unexpectedShutdowns30d = $unexpected; minidumps = $dumps; appCrashes7d = $crashes }
}

# ---------------------------------------------------------------------------
# 汇总、打码、写出
# ---------------------------------------------------------------------------
$sevOrder = @{ high = 0; medium = 1; low = 2; info = 3 }
$flagList = @($Flags | Sort-Object { $sevOrder[$_.severity] }, { $_.section })
$summary = [ordered]@{
    high   = @($flagList | Where-Object { $_.severity -eq 'high' }).Count
    medium = @($flagList | Where-Object { $_.severity -eq 'medium' }).Count
    low    = @($flagList | Where-Object { $_.severity -eq 'low' }).Count
    info   = @($flagList | Where-Object { $_.severity -eq 'info' }).Count
}

# 收尾：后台的 powercfg 没用上或还没结束的，结束掉并删除临时文件
if ($BatteryReportProc) {
    if (-not $BatteryReportProc.HasExited) { try { $BatteryReportProc.Kill() } catch {} }
    Start-Sleep -Milliseconds 200
    Remove-Item -LiteralPath $BatteryReportPath -Force -ErrorAction SilentlyContinue
}

$cs = Get-Cim Win32_ComputerSystem
$perfOut = [ordered]@{}
foreach ($k in @($Perf.Keys | Sort-Object { $Perf[$_].sec } -Descending)) {
    if ($Perf[$k].sec -ge 0.2) { $perfOut[$k] = [ordered]@{ calls = $Perf[$k].calls; sec = [math]::Round($Perf[$k].sec, 1) } }
}
$report = [ordered]@{
    schema      = 'pcdoctor.collect/0.1'
    generatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    tool        = [ordered]@{ version = $ScriptVersion; elapsedSec = [math]::Round($Clock.Elapsed.TotalSeconds, 1)
                              isAdmin = $IsAdmin; quick = [bool]$Quick; redacted = -not $NoRedact; sectionSeconds = $Timings
                              incomplete = @($CollectErrors); perf = $perfOut }
    machine     = [ordered]@{ manufacturer = $cs.Manufacturer; model = $cs.Model; language = (Get-Culture).Name }
    flagSummary = $summary
    flags       = $flagList
    sections    = $Sections
}

# 不缩进：缩进占了报告体积的四分之三；人要看时用 VS Code 打开后"格式化文档"
$json = $report | ConvertTo-Json -Depth 10 -Compress

# 路径里的用户名、计算机名、邮箱（JSON 中反斜杠已被转义成 \\，不影响匹配）
if (-not $NoRedact) { $json = Hide-Private $json $env:USERNAME $env:COMPUTERNAME }

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$outFile = Join-Path $OutDir ('pcdoctor-report-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.File]::WriteAllText($outFile, $json, (New-Object Text.UTF8Encoding $false))

# 控制台摘要
$sevName = @{ high = '高'; medium = '中'; low = '低'; info = '提示' }
$sevColor = @{ high = 'Red'; medium = 'Yellow'; low = 'Gray'; info = 'DarkGray' }
Write-Host ''
Write-Host ('  完成，用时 {0:N0} 秒。预筛命中：高 {1} · 中 {2} · 低 {3} · 提示 {4}' -f $Clock.Elapsed.TotalSeconds, $summary.high, $summary.medium, $summary.low, $summary.info) -ForegroundColor Cyan
Write-Host ''
foreach ($f in ($flagList | Where-Object { $_.severity -ne 'info' } | Select-Object -First 20)) {
    $line = '  [{0}] {1} · {2}：{3}' -f $sevName[$f.severity], $f.section, $f.object, $f.evidence
    if (-not $NoRedact) { $line = Hide-Private $line $env:USERNAME $env:COMPUTERNAME }
    if ($line.Length -gt 160) { $line = $line.Substring(0, 157) + '...' }
    Write-Host $line -ForegroundColor $sevColor[$f.severity]
}
Write-Host ''
Write-Host "  报告已保存：$outFile" -ForegroundColor Green
Write-Host '  下一步：把报告和 prompts\judge-prompt.md 一起交给 LLM 研判。' -ForegroundColor Green
Write-Host ''
