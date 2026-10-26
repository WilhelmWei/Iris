<#
.SYNOPSIS
    AI 电脑体检 · 一键运行（带进度条和分步计时）

.DESCRIPTION
    依次执行：拉取最新代码 → 采集 → 生成证据包 → 调用模型研判。
    每一步显示进度条，最后列出每一步和采集里最慢几节的用时，方便找出慢在哪。

    采集、研判都在隐藏的子进程里跑，输出写进日志文件：就算这个窗口被选中文字暂停，
    后台的采集也不会被卡住。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -DryRun    # 只采集和生成证据包，不调用模型
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Offline   # 纯规则模式：不联网、不用 Key，出一份基础报告
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -SkipPull  # 不拉取代码（网络不好时）
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -SkipPull -SkipCollect  # 只用最新一份报告重跑研判
    powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Scan      # 先用 Windows 自带的 Defender 快速扫描

    查病毒交给 Windows 自带的 Microsoft Defender：本工具只发起扫描、读取结果，不修改任何安全设置。
    用好已有的强大工具，比重复造一个杀毒引擎更可靠。
#>
[CmdletBinding()]
param(
    [switch]$SkipPull,
    [switch]$SkipCollect,          # 不重新采集，用输出目录里最新的一份报告
    [switch]$DryRun,
    [switch]$Offline,              # 纯规则模式：不调用模型，按固定规则和知识库出报告
    [switch]$Quick,
    [switch]$Scan,                 # 采集前先用 Defender 快速扫描（约 1–5 分钟）
    [switch]$Yes,                  # 不再确认，直接把证据包发给模型
    [switch]$KeepKey,              # 窗口里已有 Key 时直接沿用，不再询问
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    [string]$Model = 'deepseek-chat',
    [string]$BaseUrl = 'https://api.deepseek.com'
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = $HOME }
$Root  = $PSScriptRoot
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Self  = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName   # 子进程用同一个 PowerShell
$Total = [Diagnostics.Stopwatch]::StartNew()

$SectionKeys   = @('resources', 'processes', 'startup', 'services', 'scheduledTasks', 'drivers', 'software', 'storage',
                   'power', 'security', 'browserNetwork', 'versions', 'stability')
$SectionTitles = @('资源占用', '进程', '开机自启动', '服务', '计划任务', '驱动', '已装软件', '存储',
                   '电源与电池', '安全设置', '浏览器与网络', '驱动与系统版本', '稳定性')

# 各步骤在总进度条里的占比
$Weights = [ordered]@{ pull = 5; scan = 30; collect = 45; judge = 20 }
if (-not $Scan) { $Weights.Remove('scan') }
if ($SkipPull) { $Weights.Remove('pull') }
if ($SkipCollect) { $Weights.Remove('collect') }
if ($DryRun -or $Offline) { $Weights['judge'] = 5 }
$WeightSum = 0; foreach ($w in $Weights.Values) { $WeightSum += $w }
$script:DoneWeight = 0
$Timings = New-Object System.Collections.ArrayList

function Show-Progress([string]$Step, [string]$Status, [double]$Fraction) {
    $f = [math]::Max(0, [math]::Min(1, $Fraction))
    $pct = [int](($script:DoneWeight + $Weights[$Step] * $f) / $WeightSum * 100)
    Write-Progress -Activity 'AI 电脑体检' -Status $Status -PercentComplete $pct
}

function Complete-Step([string]$Step, [string]$Name, [double]$Seconds, [string]$Note) {
    $script:DoneWeight += $Weights[$Step]
    [void]$Timings.Add([pscustomobject]@{ name = $Name; sec = $Seconds; note = $Note })
    $mark = '完成'; if ($Note) { $mark = $Note }
    Write-Host ('  [{0,6:N1} 秒] {1}：{2}' -f $Seconds, $Name, $mark) -ForegroundColor DarkGray
}

# 日志统一放进子文件夹，不把桌面弄乱
$LogDir = [IO.Path]::Combine($OutDir, 'pcdoctor-logs')
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
function Join-Out([string]$Name) { return [IO.Path]::Combine($LogDir, $Name) }

# 在隐藏的子进程里运行，输出写进日志；返回进程对象
function Start-Child([string]$File, [string]$Arguments, [string]$Log) {
    $opts = @{ FilePath = $File; ArgumentList = $Arguments; PassThru = $true
               RedirectStandardOutput = $Log; RedirectStandardError = "$Log.err" }
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) { $opts.WindowStyle = 'Hidden' }
    $p = Start-Process @opts
    $null = $p.Handle   # 不先取句柄，进程结束后可能读不到 ExitCode
    return $p
}

# 读取子进程正在写的日志（共享读，不打断对方写入）
function Read-Log([string]$Path, [Text.Encoding]$Encoding) {
    try {
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try { return (New-Object IO.StreamReader($fs, $Encoding)).ReadToEnd() } finally { $fs.Close() }
    } catch { return '' }
}

# 提问前清空输入缓冲区：一次粘贴多行命令时，后面几行会被当成这里的回答（实测 Key 被吃成了一个 \）
function Clear-TypeAhead {
    try { $Host.UI.RawUI.FlushInputBuffer() } catch {}
}

# 看起来像不像 API Key：太短、带空白或反斜杠的，多半是误粘进来的命令片段
function Test-ApiKeyShape([string]$Key) {
    return ($Key.Length -ge 20 -and $Key -notmatch '[\s\\"]')
}

function Find-Python {
    foreach ($name in @('python', 'py', 'python3')) {
        $c = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        # 跳过 Windows 商店的 python 占位程序（运行它只会打开商店）
        if ($c -and $c.Source -notmatch '\\WindowsApps\\') { return $c.Source }
    }
    return $null
}

Write-Host ''
Write-Host '  AI 电脑体检 · 一键运行' -ForegroundColor Cyan
Write-Host "  输出目录：$OutDir" -ForegroundColor DarkGray
Write-Host ''

# 0. 先把需要人操作的事做完，之后可以离开电脑 ------------------------------------
$py = Find-Python
if (-not $py) { throw '没有找到 Python。请先安装 Python 3（python.org），安装时勾选 "Add python.exe to PATH"。' }
if (-not $DryRun -and -not $Offline -and -not ($KeepKey -and $env:DEEPSEEK_API_KEY)) {
    # 每次都问，免得用了窗口里以前设置的、自己已经忘了的 Key
    $prompt = '  请输入 DeepSeek API Key（输入时不显示）'
    $old = $env:DEEPSEEK_API_KEY
    if ($old -and (Test-ApiKeyShape $old)) { $prompt = "  这个窗口里已有一个 Key（末尾 $($old.Substring($old.Length - 4))）。直接回车沿用，或粘贴新的 Key" }
    else { $old = $null }
    $got = $null
    for ($try = 1; $try -le 3 -and -not $got; $try++) {
        Clear-TypeAhead
        $k = Read-Host $prompt -AsSecureString
        $plain = ([Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($k))).Trim()
        Remove-Variable k
        if (-not $plain -and $old) { $got = $old }
        elseif (Test-ApiKeyShape $plain) { $got = $plain }
        elseif ($plain) { Write-Host '  输入的内容不像 API Key（可能是粘贴命令时被当成了输入），请重新粘贴。' -ForegroundColor Yellow }
        Remove-Variable plain
    }
    if (-not $got) { throw '没有拿到有效的 API Key。只想采集的话，加 -DryRun 运行。' }
    $env:DEEPSEEK_API_KEY = $got
    Remove-Variable got
}
# 发不发送也在开头问完：之后整个流程不再停下来等人，输完就可以离开电脑
$AutoSend = $true
if (-not $DryRun -and -not $Offline -and -not $Yes) {
    Clear-TypeAhead
    $ans = Read-Host '  采集完成后直接把证据包发给模型？回车 = 直接发送（之后不用守着电脑），输入 n = 发送前先让我看'
    if ($ans -match '^(?i)n') { $AutoSend = $false }
}
$env:PYTHONIOENCODING = 'utf-8'   # 研判脚本的输出按 UTF-8 写进日志

# 1. 拉取最新代码 -----------------------------------------------------------------
if (-not $SkipPull) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $git -or -not (Test-Path -LiteralPath ([IO.Path]::Combine($Root, '.git')))) {
        Complete-Step 'pull' '拉取代码' 0 '跳过（不是 git 仓库或没装 git）'
    } else {
        # 这台机器的 DNS 时好时坏，解析失败时隔 2 秒再试，最多 3 次
        for ($try = 1; $try -le 3; $try++) {
            $log = Join-Out "pcdoctor-run-$Stamp-git$try.log"
            $p = Start-Child $git.Source "-C `"$Root`" pull" $log
            while (-not $p.HasExited) {
                Show-Progress 'pull' ('第 1 步：拉取最新代码（第 {0} 次，{1:N0} 秒）' -f $try, $sw.Elapsed.TotalSeconds) ($sw.Elapsed.TotalSeconds / 10)
                if ($sw.Elapsed.TotalSeconds -gt 60) { $p.Kill(); break }
                Start-Sleep -Milliseconds 300
            }
            $note = $null
            if ($p.HasExited -and $p.ExitCode -eq 0) { if ($try -gt 1) { $note = "第 $try 次才成功（DNS 不稳定）" }; break }
            $err = (Read-Log "$log.err" ([Text.Encoding]::UTF8)).Trim()
            $note = '失败，继续用本地代码'
            if ($err -notmatch 'resolve host' -or -not $p.HasExited) { break }
            $note += "（试了 $try 次都解析不到 github.com，是 DNS 问题）"
            Start-Sleep -Seconds 2
        }
        Complete-Step 'pull' '拉取代码' $sw.Elapsed.TotalSeconds $note
    }
}

# 1.5 Defender 快速扫描（可选）--------------------------------------------------------
# 只发起扫描、不改任何设置；扫到的东西按 Defender 自己的默认动作处理。扫描结果会出现在下一步采集的报告里
if ($Scan) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Out "pcdoctor-run-$Stamp-scan.log"
    $p = Start-Child $Self '-NoProfile -Command "Start-MpScan -ScanType QuickScan"' $log
    while (-not $p.HasExited) {
        Show-Progress 'scan' ('Defender 快速扫描（{0:N0} 秒，通常 1–5 分钟）' -f $sw.Elapsed.TotalSeconds) ([math]::Min(0.95, $sw.Elapsed.TotalSeconds / 180))
        Start-Sleep -Milliseconds 500
    }
    $note = $null
    if ($p.ExitCode -ne 0) { $note = '失败：' + (Read-Log "$log.err" ([Console]::OutputEncoding)).Trim() }
    Complete-Step 'scan' 'Defender 快速扫描' $sw.Elapsed.TotalSeconds $note
}

# 2. 采集 -------------------------------------------------------------------------
if ($SkipCollect) {
    $report = Get-ChildItem -LiteralPath $OutDir -Filter 'pcdoctor-report-*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $report) { throw "输出目录里没有 pcdoctor-report-*.json，不能跳过采集：$OutDir" }
    Write-Host "  [   跳过] 采集：用已有报告 $($report.Name)" -ForegroundColor DarkGray
} else {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $started = Get-Date
    $log = Join-Out "pcdoctor-run-$Stamp-collect.log"
    $collector = [IO.Path]::Combine($Root, 'collector', 'pcdoctor-collect.ps1')
    $args2 = "-NoProfile -ExecutionPolicy Bypass -File `"$collector`" -OutDir `"$OutDir`""
    if ($Quick) { $args2 += ' -Quick' }
    $p = Start-Child $Self $args2 $log
    $enc = [Console]::OutputEncoding   # 子进程按控制台编码写日志
    while (-not $p.HasExited) {
        $n = ([regex]::Matches((Read-Log $log $enc), '\[\s*[\d.,]+s\]')).Count   # 每开始一节输出一行 [  9.3s]
        $title = '准备中'; if ($n -ge 1) { $title = $SectionTitles[[math]::Min($n, 13) - 1] }
        Show-Progress 'collect' ('第 2 步：采集 · {0}（{1}/13，{2:N0} 秒）' -f $title, [math]::Max($n, 1), $sw.Elapsed.TotalSeconds) (($n - 1) / 13)
        Start-Sleep -Milliseconds 500
    }
    $report = Get-ChildItem -LiteralPath $OutDir -Filter 'pcdoctor-report-*.json' |
        Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-2) } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $report) {
        Write-Progress -Activity 'AI 电脑体检' -Completed
        Write-Host (Read-Log "$log.err" $enc) -ForegroundColor Red
        throw "采集没有生成报告，详见日志：$log"
    }
    Complete-Step 'collect' '采集' $sw.Elapsed.TotalSeconds $null
}

# 采集里最慢的几节
$slowest = $null; $slowCalls = $null
try {
    $rep = Get-Content -LiteralPath $report.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $secs = $rep.tool.sectionSeconds.PSObject.Properties | Sort-Object { [double]$_.Value } -Descending | Select-Object -First 3
    $slowest = ($secs | ForEach-Object {
        $i = [array]::IndexOf($SectionKeys, $_.Name); $name = $_.Name; if ($i -ge 0) { $name = $SectionTitles[$i] }
        '{0} {1} 秒' -f $name, $_.Value }) -join '，'
    if ($rep.tool.perf) {
        $calls = $rep.tool.perf.PSObject.Properties | Select-Object -First 3
        $slowCalls = ($calls | ForEach-Object { '{0} {1} 秒（{2} 次）' -f $_.Name, $_.Value.sec, $_.Value.calls }) -join '，'
    }
} catch {}

# 3. 证据包 + 研判 ------------------------------------------------------------------
$judgeScript = [IO.Path]::Combine($Root, 'judge', 'run_judge.py')
$sw = [Diagnostics.Stopwatch]::StartNew()
Show-Progress 'judge' '第 3 步：生成证据包' 0
$evLog = Join-Out "pcdoctor-run-$Stamp-evidence.log"
$p = Start-Child $py "`"$judgeScript`" `"$($report.FullName)`" --dry-run" $evLog
$p.WaitForExit()
$evText = (Read-Log $evLog ([Text.Encoding]::UTF8)).Trim()
if ($p.ExitCode -ne 0) { throw "生成证据包失败：$(Read-Log "$evLog.err" ([Text.Encoding]::UTF8))" }
Write-Host ''
Write-Host ($evText -replace '(?m)^', '  ')
Write-Host ''

$judged = $false
if ($Offline -and -not $DryRun) {
    $jLog = Join-Out "pcdoctor-run-$Stamp-judge.log"
    $p = Start-Child $py "`"$judgeScript`" `"$($report.FullName)`" --offline" $jLog
    $p.WaitForExit()
    $out = (Read-Log $jLog ([Text.Encoding]::UTF8)).Trim()
    $out = (($out -split "`r?`n") | Where-Object { $_ -notmatch '^(报告：|证据包：|将要发送的内容已保存：|知识库直接下结论)' }) -join "`n"
    Write-Host ($out -replace '(?m)^', '  ')
    if ($p.ExitCode -ne 0) {
        Write-Host ((Read-Log "$jLog.err" ([Text.Encoding]::UTF8)).Trim() -replace '(?m)^', '  ') -ForegroundColor Red
        Complete-Step 'judge' '纯规则研判' $sw.Elapsed.TotalSeconds '失败'
    } else {
        $judged = $true
        Complete-Step 'judge' '纯规则研判' $sw.Elapsed.TotalSeconds $null
    }
} elseif (-not $DryRun) {
    $go = $true
    if (-not $AutoSend) {
        Write-Progress -Activity 'AI 电脑体检' -Completed
        Clear-TypeAhead
        $ans = Read-Host '  证据包就是要发给模型的全部内容。按回车发送，输入 n 取消'
        if ($ans -match '^(?i)n') { $go = $false }
    }
    if ($go) {
        $sw2 = [Diagnostics.Stopwatch]::StartNew()
        $jLog = Join-Out "pcdoctor-run-$Stamp-judge.log"
        $p = Start-Child $py "`"$judgeScript`" `"$($report.FullName)`" --model $Model --base-url $BaseUrl" $jLog
        while (-not $p.HasExited) {
            $t = $sw2.Elapsed.TotalSeconds
            Show-Progress 'judge' ('第 3 步：等待 {0} 研判（已 {1:N0} 秒，通常 30–90 秒）' -f $Model, $t) ([math]::Min(0.95, $t / 90))
            Start-Sleep -Milliseconds 500
        }
        Write-Progress -Activity 'AI 电脑体检' -Completed
        $out = (Read-Log $jLog ([Text.Encoding]::UTF8)).Trim()
        $err = (Read-Log "$jLog.err" ([Text.Encoding]::UTF8)).Trim()
        # 证据包那几行上面已经显示过了
        $out = (($out -split "`r?`n") | Where-Object { $_ -notmatch '^(报告：|证据包：|将要发送的内容已保存：)' }) -join "`n"
        Write-Host ($out -replace '(?m)^', '  ')
        if ($p.ExitCode -ne 0) { Write-Host ($err -replace '(?m)^', '  ') -ForegroundColor Red; $note = '失败' } else { $note = $null; $judged = $true }
        Complete-Step 'judge' '模型研判' $sw2.Elapsed.TotalSeconds $note
    } else {
        Complete-Step 'judge' '模型研判' 0 '已取消'
    }
} else {
    Complete-Step 'judge' '生成证据包' $sw.Elapsed.TotalSeconds $null
}
Write-Progress -Activity 'AI 电脑体检' -Completed
# 删掉空的错误日志
Get-ChildItem -LiteralPath $LogDir -Filter "pcdoctor-run-$Stamp-*.err" | Where-Object { $_.Length -eq 0 } | Remove-Item -Force

# 汇总 ------------------------------------------------------------------------------
Write-Host ''
Write-Host ('  全部完成，总用时 {0:N0} 秒。各步骤：' -f $Total.Elapsed.TotalSeconds) -ForegroundColor Cyan
foreach ($s in $Timings) {
    $line = '    {0}　{1,6:N1} 秒' -f $s.name, $s.sec
    if ($s.note) { $line += "　$($s.note)" }
    Write-Host $line
}
if ($slowest) { Write-Host "    采集里最慢的三节：$slowest" -ForegroundColor DarkGray }
if ($slowCalls) { Write-Host "    采集里最慢的调用：$slowCalls" -ForegroundColor DarkGray }
Write-Host "    日志：$(Join-Out "pcdoctor-run-$Stamp-*.log")" -ForegroundColor DarkGray
if ($judged) {
    $md = Get-ChildItem -LiteralPath $OutDir -Filter 'pcdoctor-*-judge.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($md) { Write-Host "  体检报告：$($md.FullName)" -ForegroundColor Green }
}
Write-Host ''
