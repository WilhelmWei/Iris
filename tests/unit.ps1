# 单元测试：路径解析、用户可写目录规则、隐私打码
# 运行：pwsh -NoProfile -File tests/unit.ps1（Windows 或 Linux 均可）
$src = Join-Path $PSScriptRoot '..\collector\pcdoctor-collect.ps1'
if (-not (Test-Path $src)) { $src = Join-Path $PSScriptRoot '../collector/pcdoctor-collect.ps1' }
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src,[ref]$null,[ref]$null)
$fns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Resolve-ExePath','Test-UserWritablePath','Hide-Private','Get-ExclusionRisk','Get-CacheCategory','Measure-Folder','Get-Cim','Safe','Add-Perf','Format-Size','Expand-JunkPath','Measure-JunkCategory','Measure-Installers','Find-LeftoverDirs','Get-BootSummary','Get-MemoryAdvice','Get-NotifySites','Test-NavUrl','Get-ShortcutIssue','Get-ReliabilitySummary' }, $true)
foreach ($f in $fns) { . ([scriptblock]::Create($f.Extent.Text)) }
$env:windir = 'C:\Windows'
$cases = @(
 @('"C:\Program Files\Huawei\PCManager\HwMdcUI.exe" /start', 'C:\Program Files\Huawei\PCManager\HwMdcUI.exe'),
 @('C:\Windows\system32\svchost.exe -k netsvcs -p', 'C:\Windows\system32\svchost.exe'),
 @('\SystemRoot\System32\drivers\PECKP_x64.SYS', 'C:\Windows\System32\drivers\PECKP_x64.SYS'),
 @('system32\drivers\UniFairy_x64.sys', 'C:\Windows\system32\drivers\UniFairy_x64.sys'),
 @('\??\C:\Program Files\Huawei\PCManager\dokan2a.sys', 'C:\Program Files\Huawei\PCManager\dokan2a.sys'),
 @('"C:\Users\Alice\AppData\Local\Temp\bwp19e9081b\UnInstDaemon.exe" --daily-app-recovery', 'C:\Users\Alice\AppData\Local\Temp\bwp19e9081b\UnInstDaemon.exe'),
 @('"C:\Program Files (x86)\EasyAntiCheat_EOS\EasyAntiCheat_EOS.exe"', 'C:\Program Files (x86)\EasyAntiCheat_EOS\EasyAntiCheat_EOS.exe'),
 @('"C:\broken path\no closing quote.exe', 'C:\broken path\no closing quote.exe')
)
$pass=0; foreach ($c in $cases) { $r = Resolve-ExePath $c[0]; if ($r -eq $c[1]) { $pass++ } else { "FAIL resolve: [$($c[0])] -> [$r] expected [$($c[1])]" } }
"resolve: $pass/$($cases.Count) passed"
$uw = @(@('C:\Users\Alice\AppData\Local\Temp\x.exe',$true), @('C:\ProgramData\evil.exe',$true), @('C:\ProgramData\Microsoft\Windows Defender\MsMpEng.exe',$false), @('C:\Program Files\Google\Chrome\chrome.exe',$false), @('C:\Users\Public\a.exe',$true))
$p2=0; foreach ($c in $uw) { if ((Test-UserWritablePath $c[0]) -eq $c[1]) { $p2++ } else { "FAIL userpath: $($c[0])" } }
"userpath: $p2/$($uw.Count) passed"
# 打码（测试脚本里真实的 Hide-Private）
$red = @(
 @('C:\Users\Alice\AppData\x', 'Alice', 'LAPTOP-TEST01', 'C:\Users\[用户]\AppData\x'),
 @('Author LAPTOP-TEST01\Alice', 'Alice', 'LAPTOP-TEST01', 'Author [计算机名]\[用户]'),
 @('Alicery app', 'Alice', 'LAPTOP-TEST01', 'Alicery app'),
 @('mail bob@test.de', 'Alice', 'LAPTOP-TEST01', 'mail [邮箱]'),
 @('31.0.101.5522', 'Alice', 'LAPTOP-TEST01', '31.0.101.5522'),
 @('{"a": "内存完整性"}', '', '', '{"a": "内存完整性"}'),
 @('C:\Users\Alice\x', 'Alice', $null, 'C:\Users\[用户]\x')
)
$p3=0; foreach ($c in $red) { $r = Hide-Private $c[0] $c[1] $c[2]; if ($r -eq $c[3]) { $p3++ } else { "FAIL redact: [$($c[0])] -> [$r] expected [$($c[3])]" } }
"redact: $p3/$($red.Count) passed"

# Defender 排除项风险
$ex = @(
 @('path','C:\','high'), @('path','D:','high'), @('path','C:\Users\Alice\AppData\Local\Temp','high'), @('path','C:\ProgramData','high'),
 @('path','C:\Users\Alice','high'), @('path','D:\Projects\build','low'), @('path','C:\Program Files\Docker','low'),
 @('extension','.exe','high'), @('extension','*.ps1','high'), @('extension','log','low'),
 @('process','C:\Users\Alice\AppData\Local\x\a.exe','high'), @('process','node.exe','medium'),
 @('process','Update.exe','high'), @('process','uninst.exe','high'), @('process','unins000.exe','high'), @('process','LetsPRO.exe','medium'),
 @('path','C:\Users\Alice\AppData\Local\JetBrains\PyCharmCE2023.3','low'), @('path','C:\Users\Alice\.conda','low'),
 @('path','C:\Users\Alice\AppData\Local\Evil','high'),
 @('path','N/A: Must be an administrator to view exclusions',$null)
)
$p4=0; foreach ($c in $ex) { $r = Get-ExclusionRisk $c[0] $c[1]; if ($r -eq $c[2]) { $p4++ } else { "FAIL exclusion: [$($c[0]) $($c[1])] -> [$r] expected [$($c[2])]" } }
"exclusion: $p4/$($ex.Count) passed"

# 文件夹分类与按时间分桶：Image 下一个 100 天前的文件，Cache 下一个新文件，根目录一个 400 天前的文件
$root = Join-Path ([IO.Path]::GetTempPath()) ('pcd-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $root 'wxid_a/FileStorage/Image/2025-01') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $root 'wxid_a/FileStorage/Cache') -Force
function New-TestFile($rel, $bytes, $daysAgo) {
    $f = Join-Path $root $rel
    [IO.File]::WriteAllBytes($f, (New-Object byte[] $bytes))
    (Get-Item -LiteralPath $f).LastWriteTime = (Get-Date).AddDays(-$daysAgo)
}
New-TestFile 'wxid_a/FileStorage/Image/2025-01/a.dat' (32MB) 100
New-TestFile 'wxid_a/FileStorage/Cache/b.tmp' (21MB) 1
New-TestFile 'old.bin' (11MB) 400
$m = Measure-Folder $root 10 -Breakdown
Remove-Item -LiteralPath $root -Recurse -Force
$checks = @(
 @('files', $m.files, 3),
 @('sizeGB', $m.sizeGB, 0.06),
 @('图片', $m.byCategoryGB['图片'], 0.03),
 @('缓存与日志', $m.byCategoryGB['缓存与日志'], 0.02),
 @('其他', $m.byCategoryGB['其他'], 0.01),
 @('older 7d', $m.olderThanGB['7d'], 0.04),
 @('older 90d', $m.olderThanGB['90d'], 0.04),
 @('older 180d', $m.olderThanGB['180d'], 0.01),
 @('older 365d', $m.olderThanGB['365d'], 0.01)
)
$p5=0; foreach ($c in $checks) { if ([string]$c[1] -eq [string]$c[2]) { $p5++ } else { "FAIL folder $($c[0]): [$($c[1])] expected [$($c[2])]" } }
"folder: $p5/$($checks.Count) passed"

# 查询失败必须被吞掉并返回空，不能抛出去拖垮整节（曾因 finally 放在 catch 前导致 catch 失效）
$Perf = [ordered]@{}; $CollectErrors = New-Object System.Collections.ArrayList
$p6=0
try { $r = Get-Cim 'Pcdoctor_NoSuchClass'; if ($null -eq $r) { $p6++ } else { 'FAIL cim: 返回了非空' } } catch { "FAIL cim: 抛出异常 $($_.Exception.Message)" }
try { $r = Safe 'boom' { throw 'x' }; if ($null -eq $r -and $CollectErrors.Count -eq 1) { $p6++ } else { 'FAIL safe: 未记录错误' } } catch { "FAIL safe: 抛出异常" }
if ($Perf.Contains('cim Pcdoctor_NoSuchClass')) { $p6++ } else { 'FAIL perf: 没有记录 cim 耗时' }
"error-handling: $p6/3 passed"

# 清理目录表的测量：通配一整段、不进入链接、文件名过滤、最短天数、保留最新几个、浏览器开着时记下来
$jr = Join-Path ([IO.Path]::GetTempPath()) ("pcdoctor-junk-" + [guid]::NewGuid().ToString('N'))
function New-JunkFile([string]$Rel, [int]$KB, [int]$DaysOld) {
    $f = [IO.Path]::Combine([string[]](@($jr) + $Rel.Split('\')))
    [void](New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($f)))
    [IO.File]::WriteAllBytes($f, (New-Object byte[] ($KB * 1024)))
    (Get-Item -LiteralPath $f).LastWriteTime = (Get-Date).AddDays(-$DaysOld)
}
New-JunkFile 'U\Default\Cache\a' 512 0
New-JunkFile 'U\Profile 1\Cache\b' 512 0
New-JunkFile 'U\Default\Cookies' 512 0
New-JunkFile 'Elsewhere\Cache\secret' 1024 0
$linked = $false
try { [void](New-Item -ItemType SymbolicLink -Path ([IO.Path]::Combine($jr, 'U', 'Linked')) -Target ([IO.Path]::Combine($jr, 'Elsewhere')) -ErrorAction Stop); $linked = $true } catch {}
New-JunkFile 'T\old.tmp' 1024 30
New-JunkFile 'T\new.tmp' 1024 1
New-JunkFile 'D\a.dmp' 1024 30
New-JunkFile 'D\b.dmp' 1024 20
New-JunkFile 'D\c.dmp' 1024 2
New-JunkFile 'W\MEMORY.DMP' 2048 30
New-JunkFile 'W\sub\MEMORY.DMP' 2048 30
$vars = @{ ROOT = $jr }
$cat = { param($json) $json | ConvertFrom-Json }
$jchecks = @()
$b = Measure-JunkCategory (& $cat '{"id":"b","tier":"A","paths":["%ROOT%\\U\\*\\Cache"],"closeFirst":["chrome"]}') $vars @{ chrome = $true }
$jchecks += ,@('browser size', $b.sizeMB, 1)
$jchecks += ,@('browser roots', $b.paths.Count, 2)
$jchecks += ,@('browser running', $b.running, 'chrome')
$tm = Measure-JunkCategory (& $cat '{"id":"t","tier":"A","paths":["%ROOT%\\T"],"minAgeDays":7}') $vars @{}
$jchecks += ,@('temp size', $tm.sizeMB, 2)
$jchecks += ,@('temp cleanable', $tm.cleanableMB, 1)
$dm = Measure-JunkCategory (& $cat '{"id":"d","tier":"A","paths":["%ROOT%\\D"],"include":["*.dmp"],"keepNewest":1}') $vars @{}
$jchecks += ,@('dumps cleanable files', $dm.cleanableFiles, 2)
$wm = Measure-JunkCategory (& $cat '{"id":"w","tier":"A","paths":["%ROOT%\\W"],"include":["MEMORY.DMP"],"recurse":false}') $vars @{}
$jchecks += ,@('memory.dmp top level only', $wm.sizeMB, 2)
$jchecks += ,@('missing var', @(Expand-JunkPath '%NOPE%\x' $vars).Count, 0)
$jchecks += ,@('dot dot', @(Expand-JunkPath '%ROOT%\..\x' $vars).Count, 0)
$jchecks += ,@('size text', (Format-Size 1536), '1.5 GB')
$p7=0; foreach ($c in $jchecks) { if ([string]$c[1] -eq [string]$c[2]) { $p7++ } else { "FAIL junk $($c[0]): [$($c[1])] expected [$($c[2])]" } }
if (-not $linked) { 'note: 这台机器不能创建符号链接，跳过"不进入链接"的检查' }
Remove-Item -LiteralPath $jr -Recurse -Force
"junk: $p7/$($jchecks.Count) passed"

# 第三期：旧安装包统计、卸载残留文件夹
$lr = Join-Path ([IO.Path]::GetTempPath()) ("pcdoctor-left-" + [guid]::NewGuid().ToString('N'))
function New-OldFile([string]$Rel, [int]$KB, [int]$DaysOld) {
    $f = [IO.Path]::Combine([string[]](@($lr) + $Rel.Split('\')))
    [void](New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($f)))
    [IO.File]::WriteAllBytes($f, (New-Object byte[] ($KB * 1024)))
    (Get-Item -LiteralPath $f).LastWriteTime = (Get-Date).AddDays(-$DaysOld)
}
New-OldFile 'DL\setup.exe' 2048 90
New-OldFile 'DL\sub\tool.MSI' 1024 60
New-OldFile 'DL\fresh-setup.exe' 4096 2
New-OldFile 'DL\notes.txt' 4096 400
New-OldFile 'PF\OldTool\logs\a.log' 512 400
New-OldFile 'PF\OldTool\config.ini' 1 400
New-OldFile 'PF\LiveApp\app.exe' 10 400
New-OldFile 'PF\RefApp\data.bin' 10 400
New-OldFile 'PF\Google\Update\x.dat' 10 400
New-OldFile 'PF\Fresh\x.log' 10 3
New-OldFile 'PF\Common Files\x.log' 10 400
New-OldFile 'PF\Lib\x.DLL' 10 400
$lchecks = @()
$mi = Measure-Installers ([IO.Path]::Combine($lr, 'DL')) 30
$lchecks += ,@('installers count', $mi.count, 2)
$lchecks += ,@('installers size', $mi.sizeGB, 0)
$lchecks += ,@('installers missing', (Measure-Installers ([IO.Path]::Combine($lr, 'nope'))), $null)
$pf = [IO.Path]::Combine($lr, 'PF')
$refApp = [IO.Path]::Combine($pf, 'RefApp')
$found = @(Find-LeftoverDirs @($pf) @("`"$([IO.Path]::Combine($refApp, 'uninst.exe'))`" /S") @('Google Chrome', 'Google LLC'))
$lchecks += ,@('leftover only OldTool', (@($found | ForEach-Object { Split-Path $_.name -Leaf }) -join ','), 'OldTool')
$lchecks += ,@('leftover files', $found[0].files, 2)
$lchecks += ,@('leftover size', $found[0].sizeMB, 0.5)
$lchecks += ,@('leftover age', ($found[0].lastWriteDays -ge 399), $true)
$lchecks += ,@('leftover ref by install location', (@(Find-LeftoverDirs @($pf) @($refApp, ([IO.Path]::Combine($pf, 'OldTool'))) @()) | ForEach-Object { Split-Path $_.name -Leaf }) -join ',', 'Google')
$p8=0; foreach ($c in $lchecks) { if ([string]$c[1] -eq [string]$c[2]) { $p8++ } else { "FAIL leftover $($c[0]): [$($c[1])] expected [$($c[2])]" } }
Remove-Item -LiteralPath $lr -Recurse -Force
"leftover: $p8/$($lchecks.Count) passed"

# 开机耗时、内存升级建议
$t0 = Get-Date '2026-10-30 08:00'
$recs = @(
    @{ id = 100; time = $t0; data = @{ BootTime = '95000'; MainPathBootTime = '72000'; BootPostBootTime = '23000' } },
    @{ id = 100; time = $t0.AddDays(-1); data = @{ BootTime = '40000'; MainPathBootTime = '30000'; BootPostBootTime = '10000' } },
    @{ id = 101; time = $t0; data = @{ FriendlyName = 'QuickVPN 助手'; Name = 'qv.exe'; DegradationTime = '6000' } },
    @{ id = 101; time = $t0.AddDays(-1); data = @{ FriendlyName = 'QuickVPN 助手'; Name = 'qv.exe'; DegradationTime = '4000' } },
    @{ id = 102; time = $t0; data = @{ FriendlyName = ''; Name = 'slowdrv.sys'; DegradationTime = '2000' } },
    $null)
$bs = Get-BootSummary $recs
$hchecks = @()
$hchecks += ,@('boot recent count', $bs.recent.Count, 2)
$hchecks += ,@('boot newest first', $bs.recent[0].mainPathSec, 72)
$hchecks += ,@('boot post', $bs.recent[0].postBootSec, 23)
$hchecks += ,@('slow top', $bs.slowItems[0].name, 'QuickVPN 助手')
$hchecks += ,@('slow avg', $bs.slowItems[0].avgSec, 5)
$hchecks += ,@('slow times', $bs.slowItems[0].times, 2)
$hchecks += ,@('slow driver name fallback', $bs.slowItems[1].name, 'slowdrv.sys')
$hchecks += ,@('slow driver kind', $bs.slowItems[1].kind, '驱动')
$hchecks += ,@('boot empty', (Get-BootSummary @()).recent.Count, 0)
$ddr4 = @{ capacityGB = 8; speedMHz = 3200; smbiosType = 26; formFactor = 12 }
$a1 = Get-MemoryAdvice @($ddr4) 2 32 8
$hchecks += ,@('mem free slot', $a1.canAdd, $true)
$hchecks += ,@('mem text', $a1.text, '8 GB（8 GB DDR4-3200）；还空 1 个插槽，可以加一条同规格（DDR4-3200）的内存，主板最多支持 32 GB')
$a2 = Get-MemoryAdvice @($ddr4, $ddr4) 2 32 16
$hchecks += ,@('mem full slots', $a2.canAdd, $true)
$a3 = Get-MemoryAdvice @(@{ capacityGB = 8; speedMHz = 6400; smbiosType = 35 }) 0 $null 8
$hchecks += ,@('mem soldered', $a3.canAdd, $false)
$hchecks += ,@('mem soldered text', ($a3.text -like '*焊在主板上*'), $true)
$hchecks += ,@('mem none', (Get-MemoryAdvice @() 2 32 8), $null)
$p9=0; foreach ($c in $hchecks) { if ([string]$c[1] -eq [string]$c[2]) { $p9++ } else { "FAIL hw $($c[0]): [$($c[1])] expected [$($c[2])]" } }
"hardware: $p9/$($hchecks.Count) passed"

# 弹窗和篡改
$prefs = '{"profile":{"content_settings":{"exceptions":{"notifications":{"https://news.example.com:443,*":{"setting":1},"https://ok.example.org:443,*":{"setting":2},"http://[*.]ads.test,*":{"setting":1}}}}}}' | ConvertFrom-Json
$pc = @()
$ns = @(Get-NotifySites $prefs)
$pc += ,@('notify count', $ns.Count, 2)
$pc += ,@('notify host', $ns[0], 'news.example.com')
$pc += ,@('notify wildcard', $ns[1], 'ads.test')
$pc += ,@('notify none', @(Get-NotifySites ('{"profile":{}}' | ConvertFrom-Json)).Count, 0)
$pc += ,@('nav hao123', (Test-NavUrl 'https://www.hao123.com/?tn=123'), $true)
$pc += ,@('nav 360', (Test-NavUrl 'http://hao.360.com/'), $true)
$pc += ,@('nav normal', (Test-NavUrl 'https://www.bing.com/'), $false)
$pc += ,@('nav lookalike', (Test-NavUrl 'https://nothao123.com.example.net/'), $false)
$pc += ,@('lnk url', (Get-ShortcutIssue 'Google Chrome.lnk' 'C:\Program Files\Google\Chrome\Application\chrome.exe' 'http://www.2345.com/?k123'), 'shortcut-url')
$pc += ,@('lnk www', (Get-ShortcutIssue 'Microsoft Edge.lnk' 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' '--profile-directory=Default www.example.com'), 'shortcut-url')
$pc += ,@('lnk normal', (Get-ShortcutIssue 'Google Chrome.lnk' 'C:\Program Files\Google\Chrome\Application\chrome.exe' ''), $null)
$pc += ,@('lnk pwa', (Get-ShortcutIssue 'Outlook.lnk' 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge_proxy.exe' '--profile-directory=Default --app-id=abc --app-url=https://outlook.example.com/'), $null)
$pc += ,@('lnk fake', (Get-ShortcutIssue 'Internet Explorer.lnk' 'C:\Program Files\FakeBrowser\fb.exe' ''), 'shortcut-fake-browser')
$pc += ,@('lnk other', (Get-ShortcutIssue 'Notes.lnk' 'C:\Tools\notes.exe' 'https://x.test'), $null)
$p10 = 0; foreach ($c in $pc) { if ($c[1] -eq $c[2]) { $p10++ } else { "FAIL popup $($c[0]): [$($c[1])] expected [$($c[2])]" } }
"popup: $p10/$($pc.Count) passed"

# 可靠性时间线
$m = @(); $d0 = Get-Date '2026-10-01 08:00'
foreach ($i in 0..9) { $v = 9.5; if ($i -ge 6) { $v = 5.2 }; $m += [pscustomobject]@{ TimeGenerated = $d0.AddDays($i); SystemStabilityIndex = $v }; $m += [pscustomobject]@{ TimeGenerated = $d0.AddDays($i).AddHours(5); SystemStabilityIndex = $v + 0.3 } }
$r = @(
 [pscustomobject]@{ TimeGenerated = $d0.AddDays(5); SourceName = 'MsiInstaller'; EventIdentifier = 11707; ProductName = 'QuickVPN 3.0' },
 [pscustomobject]@{ TimeGenerated = $d0.AddDays(6); SourceName = 'Microsoft-Windows-WindowsUpdateClient'; EventIdentifier = 19; ProductName = 'KB5099999' },
 [pscustomobject]@{ TimeGenerated = $d0.AddDays(7); SourceName = 'Application Error'; EventIdentifier = 1000; ProductName = 'app.exe' },
 [pscustomobject]@{ TimeGenerated = $d0.AddDays(7).AddHours(1); SourceName = 'Application Error'; EventIdentifier = 1000; ProductName = 'app.exe' })
$rs = Get-ReliabilitySummary $m $r
$rc = @()
$rc += ,@('days', $rs.stabilityByDay.Count, 10)
$rc += ,@('daily min', $rs.stabilityByDay[0][1], 9.5)
$rc += ,@('drop date', $rs.biggestDrop.date, '2026-10-07')
$rc += ,@('drop by', $rs.biggestDrop.by, 4.3)
$rc += ,@('drop installs', ($rs.biggestDrop.installedThatWeek -join ','), 'QuickVPN 3.0,KB5099999')
$rc += ,@('failures', $rs.failuresByDay[0][1], 2)
$rc += ,@('empty', (Get-ReliabilitySummary @() @()).stabilityByDay.Count, 0)
$p11 = 0; foreach ($c in $rc) { if ($c[1] -eq $c[2]) { $p11++ } else { "FAIL reliability $($c[0]): [$($c[1])] expected [$($c[2])]" } }
"reliability: $p11/$($rc.Count) passed"
