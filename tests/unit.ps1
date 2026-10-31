# 单元测试：路径解析、用户可写目录规则、隐私打码
# 运行：pwsh -NoProfile -File tests/unit.ps1（Windows 或 Linux 均可）
$src = Join-Path $PSScriptRoot '..\collector\pcdoctor-collect.ps1'
if (-not (Test-Path $src)) { $src = Join-Path $PSScriptRoot '../collector/pcdoctor-collect.ps1' }
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src,[ref]$null,[ref]$null)
$fns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Resolve-ExePath','Test-UserWritablePath','Hide-Private','Get-ExclusionRisk','Get-CacheCategory','Measure-Folder','Get-Cim','Safe','Add-Perf','Format-Size','Expand-JunkPath','Measure-JunkCategory','Measure-Installers','Find-LeftoverDirs' }, $true)
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
