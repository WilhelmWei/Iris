# pcdoctor 外部调研 · 第二轮（A–H 八个方向）

调研日期：2026-10-25　范围：第一轮"没覆盖、值得去查"的方向 + 用户新增的 A–H

## 先说明：这份调研怎么做的、哪里不可靠

- 共约 40 次网页搜索 + 2 个可以离线核对的开源数据集。大部分网页正文读不到（代理拦截或 DNS 失败），只拿到搜索引擎摘要，标为［仅搜索摘要］。
- **两份数据是直接下载原文核对的，可信度最高**：
  - **LOLDrivers**（magicsword-io，"已知可被滥用的驱动"库）：从 GitHub raw 下载了 `drivers.json`（706 条：581 条"有漏洞驱动"、125 条"恶意驱动"），本文里的驱动名、厂商、CVE 都是从这份文件里直接查出来的。
  - **LOLRMM**（同一组织，"可被滥用的远程管理工具"库）：下载了 AnyDesk、TeamViewer、RustDesk、ToDesk、向日葵（SunLogin）五个条目的 YAML 原文。
  - 下载的文件在 `/tmp/claude-0/-home-user-pcdoctor/9cc85bf2-1ad4-52b6-8a56-a7200183e41c/scratchpad/loldrv/` 和 `/tmp/claude-0/-home-user-pcdoctor/9cc85bf2-1ad4-52b6-8a56-a7200183e41c/scratchpad/lolrmm/`，可以直接复查。
- 标注：［事实］有原文或多个一致来源；［仅搜索摘要］只看到摘要；［推测］我的推断；［未找到］查了没找到；［已有知识］没在本轮检索、凭训练知识写的，使用前要核实。
- **没有编造服务名**：凡是本轮没核实到的服务名，都写成"待在 Windows 上实测"。

---

## A. 同类工具"把系统搞坏"的真实事故

### A1. 删除系统自带应用（AppX）是最难回滚的一类操作
- **发现**［事实］Win11Debloat 官方 wiki"撤销更改"页原文：几乎所有改动都能通过再运行脚本、取消勾选来撤销，但**"注册表备份不会恢复被卸载的应用"**；**"唯一不能（轻松）重装的是 Microsoft Store 本身和 XboxSpeechToTextOverlay"**；删了 Xbox 服务要用微软的 Gaming Services 修复工具；它可能锁定了 Edge 的某些设置；注册表备份功能 2026.05.10 版才有，更早运行过的机器没有备份可恢复；手工恢复只对当前用户生效。
- **发现**［仅搜索摘要］多个论坛用户反映：激进删除 AppX、禁用服务的后果往往**几个月后才出现**——功能更新时找不到被删的依赖，导致更新失败；Level1Techs 有用户跑脚本后商店和 Xbox 连接坏掉，SFC、DISM、还原都没修好。
- **对 pcdoctor 的建议**
  1. **新增红线 10：不卸载系统自带的 AppX 包（尤其 Microsoft Store、Xbox 相关、WebView2）**。"去臃肿"只做到"关闭开机自启 / 隐藏入口"，不做"删除包"。理由写给用户：删掉以后可能几个月后更新失败，而且商店本身装不回来。
  2. 采集侧可加只读检查"**系统组件缺失**"：Microsoft Store 包（`Microsoft.WindowsStore`）不存在、WebView2 运行时不存在时提示"可能被优化工具删过"，这是"历史遗留"的一种，也属于 360 不会告诉你的信息差。
  3. 设计文档里的"可回滚"要区分"**可回滚**"和"**只能重装**"两类，后者在操作库里直接禁用。
- **来源**：https://github.com/Raphire/Win11Debloat/wiki/Reverting-Changes 、https://forum.level1techs.com/t/i-broke-windows-10-by-running-a-powershell-script/151100 、https://windowsforum.com/news/debloating-windows-11-why-a-cleaner-pc-can-break-updates.383570/［仅搜索摘要，原文 DNS 失败］

### A2. WebView2 不能删
- **发现**［仅搜索摘要］微软 Q&A 版主：没有受支持的办法单独永久禁用 WebView2，删除或禁用会让依赖它的应用和功能停止工作或不稳定；卸载 Edge 不影响 WebView2。社区用户称媒体播放器、照片等自带应用和 365 系列都嵌了 WebView2。一个开源 Edge 卸载工具默认保留 WebView2，并把删除它标为"RISKY"。
- **建议**：把 `Microsoft Edge WebView2 Runtime` 加进知识库"系统依赖，不可卸载"；如果以后界面走 WebView2 路线（阶段 2.3），我们自己也依赖它——多一条理由不能让任何操作碰它。
- **来源**：https://learn.microsoft.com/en-us/answers/a/6613830 、https://github.com/eunjunseo47/edge-remover

### A3. 禁用系统服务：蓝牙、Wi-Fi、微软账户登录、打印
- **发现**［仅搜索摘要］
  - elevenforum：用 .bat 禁用"多余"服务后，Win11 快速设置里的蓝牙箭头点了就卡死关闭，重装驱动无效。
  - wilderssecurity：禁用 **CNG Key Isolation** 后登录微软账户失败，改回"手动"才恢复；笔记本用户精简服务后出现"有时开机连不上网"。
  - 一个 GitHub 上的 Win11 服务优化脚本的禁用清单里同时有 **Windows Search** 和 **Bluetooth Support Service**；一些"可安全禁用"清单里有 Print Spooler、Windows Update。
  - How-To Geek 的观点：随意禁用微软内置服务"十有八九会出问题"，应只处理第三方服务。
- **建议**
  1. **新增红线 11：不改微软内置服务的启动类型**（签名者为 Microsoft Windows 的服务，与驱动红线 3 对称）。阶段 4.1"服务改手动"只对第三方服务开放。
  2. 采集侧新增只读规则"**系统服务被改过**"：对照一份"默认启动类型"表（BTHSERV、WSearch、Spooler、wuauserv、BITS、KeyIso、WlanSvc 等，表要在干净的 Win10 / Win11 虚拟机上导出，**不要凭记忆写**），发现被设成"禁用"的，提示"可能是优化软件改的，以下功能可能因此失灵"。这正好是"反 360"招牌——帮用户发现上一个优化工具留下的坑。
- **来源**：https://www.elevenforum.com/t/bluetooth-additional-setting-arror-in-quick-access-menu.45394/latest 、https://www.wilderssecurity.com/goto/post?id=2575436 、https://www.howtogeek.com/139028/which-windows-services-can-you-safely-disable/

### A4. 注册表清理：微软官方"不支持"
- **发现**［仅搜索摘要］微软 KB 2563254：微软不支持使用注册表清理工具；用这类工具错误修改注册表可能导致严重问题，可能需要重装系统；微软不对此负责。另有二手来源称清理器会删掉仍被使用的共享 COM 注册，导致应用打不开。（第一轮已引用"注册表清理=伪优化"，这里补的是**微软官方立场的出处**，可直接用在报告"小知识"里。）
- **建议**：操作库永不包含"注册表清理"；"小知识"卡片引用 KB 2563254。
- **来源**：https://support.microsoft.com/kb/2563254

### A5. BitLocker：执行前必须检查的一项（重要）
- **发现**
  - ［事实，仅搜索摘要但多源一致］BIOS 更新、启动顺序改变、关闭安全启动、TPM 状态变化、插拔外接硬盘都可能触发 BitLocker 恢复界面；戴尔知识库：通过 Windows 更新升级 BIOS 时 BitLocker 不会自动暂停，导致要求输入恢复密钥。
  - ［仅搜索摘要］Guru3D 报道：2025 年 10 月的累积更新 KB5066835（Win11 24H2 / 25H2）让不少 Intel 机器在没有硬件变化时也弹出恢复界面，微软确认过。
  - ［未找到］Win11 24H2 起"设备加密默认开启"的微软官方原文本轮没找到（只有 Q&A 志愿者的说法）。
- **建议**
  1. **执行前检查（阶段 3 执行器）**：只要操作涉及驱动、启动配置、安全启动、固件，先检查系统盘 BitLocker 状态（采集里已有 `bitlockerSystemDrive`）；如果是"开启"，必须先提示用户**确认恢复密钥已备份**（微软账户里能查到），没确认就不执行。
  2. 采集侧新增只读规则"**BitLocker 已开启但用户可能不知道恢复密钥在哪**"（信息类，不扣分），附小知识"恢复密钥去 account.microsoft.com/devices/recoverykey 查"。这是普通人最容易"开不了机"的真实场景。［推测：读取保护器类型可以用 `Get-BitLockerVolume` 的 `KeyProtector`，判断是否有 RecoveryPassword；能否知道"已备份到微软账户"——未找到只读办法］
- **来源**：https://www.dell.com/support/kbdoc/en-in/000358493/& 、https://www.guidingtech.com/how-to-fix-bitlocker-keeps-asking-for-recovery-key/ 、https://www.guru3d.com/story/windows-11-25h2-update-causes-unexpected-bitlocker-recovery-prompts/

### A6. 系统还原点：别指望它一定在
- **发现**［仅搜索摘要］Windows 默认 24 小时内只建一个还原点（`SystemRestorePointCreationFrequency`，默认 1440 分钟，该值默认不存在）；还原点依赖"系统保护"已开启。第一轮提到 WinUtil 执行前建还原点、2023 年修过"没建成"的 bug。
- **建议**
  1. 执行器不能把"建还原点"当作回滚手段：24 小时内已有还原点时**新建会被静默跳过**，系统保护可能根本没开。我们的回滚必须是操作库自己的备份（导出注册表键、服务配置），还原点只是"额外保险"。
  2. 采集里加只读项"系统保护是否开启、最近一个还原点的时间"（信息类）。
- **来源**：https://www.elevenforum.com/t/change-system-restore-point-creation-frequency-in-windows-11.3613/latest 、https://www.ninjaone.com/blog/change-system-restore-point-creation-frequency-in-windows/

### A7. 厂商官方教程本身就违反我们的红线
- **发现**［仅搜索摘要］华为官方"内存完整性无法打开"的支持文章 / 以及多篇中文教程教用户 `pnputil /delete-driver ... /force` 删除不兼容驱动。这正是我们红线 5 禁止的做法（附录 A 已有教训）。
- **建议**：报告里遇到"内存完整性被某驱动挡住"时，主动写一句"网上常见教程会加 /force，我们不这样做，因为……"——这就是"信息差"内容。
- **来源**：https://consumer.huawei.com/cn/support/content/zh-cn15915937 、https://gitcode.csdn.net/6aa0dda21a35784c842084cc.html

### A 小结：操作库建议新增的红线和"执行前检查"

| 编号 | 内容 | 依据 |
| --- | --- | --- |
| 红线 10 | 不卸载系统自带 AppX 包（Store、Xbox、WebView2 等），只做"关闭自启 / 隐藏" | A1、A2 |
| 红线 11 | 不改微软内置服务（签名者 Microsoft Windows）的启动类型 | A3 |
| 红线 12 | 不做注册表"清理"；只删除操作库明确列出的、自己能备份的键 | A4 |
| 检查 1 | 涉及驱动 / 启动 / 固件的操作前：BitLocker 开启 → 确认恢复密钥已备份 | A5 |
| 检查 2 | 回滚依赖自己的备份，不依赖还原点；还原点创建失败或被跳过要如实记录 | A6 |
| 检查 3 | 执行前记录"当前状态"，执行后 verify；对"可能延迟出现"的副作用（更新失败）在报告里写明观察期 | A1 |

---

## B. 远程控制软件与电信诈骗

### B1. LOLRMM 已经整理好了远控工具的只读痕迹（直接可用）
- **发现**［事实，已下载原文］LOLRMM 的 YAML 条目：
  - **AnyDesk**：安装路径 `C:\Program Files (x86)\AnyDesk\`（定制版 `AnyDesk-*`）；服务注册表 `HKLM\SYSTEM\CurrentControlSet\Services\AnyDesk`；服务安装事件 7045 的服务名 `AnyDesk Service`，命令行带 `--service`；配置 `%ProgramData%\AnyDesk\service.conf`（"可设置密码自动接受会话，密码以加盐哈希保存"）、`system.conf`；**`%ProgramData%\AnyDesk\connection_trace.txt` 记录每次传入连接的时间、对方 ID 和批准方式：`User`（有人点了接受）、`Passwd`（用无人值守密码登录）、`Token`（保存的登录令牌）**；启动文件夹 `AnyDesk.lnk`；`file_transfer_trace.txt` 记录文件传输。
  - **TeamViewer**：服务名 `TeamViewer`，`TeamViewer_Service.exe`；`HKLM\SOFTWARE\TeamViewer\ConnectionHistory`；`C:\Program Files*\TeamViewer\connections*.txt`（传入连接记录）。
  - **RustDesk**：服务名 `RustDesk`（`--service`，自动启动）；配置 `C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk2.toml`；启动项 `RustDesk Tray.lnk`。
  - **ToDesk**：进程 `todesk.exe`、`ToDesk_Service.exe`，域名 `*.todesk.com`；**没有注册表和文件痕迹**（条目基本是空的）。
  - **向日葵（SunLogin）**：进程 `OrayRemoteShell.exe`、`OrayRemoteService.exe`、`sunlogin*.exe`；签名者 **上海贝锐信息科技股份有限公司**（英文 Shanghai Best Oray Information Technology Co., Ltd.）；域名 `sunlogin.oray.com`、`client.oray.net`；**没有注册表和文件痕迹**。
- **对 pcdoctor 的建议**
  1. 新增只读规则 **`remote-access-unattended`**：远控软件**以服务方式安装 + 自动启动**（= 开机即可被连，无需有人在电脑前）→ 中；再看最近 30 天"无人值守"传入连接：AnyDesk 的 `connection_trace.txt` 里出现 `Passwd` / `Token`、TeamViewer 有 `connections_incoming.txt` 记录 → 报"最近有人远程登录过这台电脑"，列出**次数和时间**（不输出对方 ID，隐私打码）。
  2. 识别方式优先用**签名者 + 服务的 ImagePath**，不要只靠服务名（国产软件版本变动快，服务名未核实）。签名者白名单：AnyDesk Software GmbH［已有知识，需核实］、TeamViewer Germany GmbH［已有知识，需核实］、上海贝锐［事实，LOLRMM］、ToDesk 签名者［未找到］。
  3. 采集脚本里已有 `'远程控制' = '(?i)ToDesk|向日葵|Sunlogin|TeamViewer|AnyDesk|UU远程|RustDesk'`（重复软件分类），可以复用作已装软件识别；新增的是"服务 + 自动启动 + 连接记录"三项状态。
  4. 只**提示不删除**：很多人是给父母装的、公司 IT 装的。报告问用户"这是你自己 / 家人装的吗？"→ 走已有的 `--confirm` 机制。
- **来源**：https://github.com/magicsword-io/LOLRMM （本地副本 `/tmp/claude-0/-home-user-pcdoctor/9cc85bf2-1ad4-52b6-8a56-a7200183e41c/scratchpad/lolrmm/`）、https://www.intel471.com/blog/understanding-and-threat-hunting-for-rmm-software-misuse

### B2. 向日葵旧版本有被真实利用的远程代码执行漏洞
- **发现**［事实，多源一致：AhnLab ASEC 报告经 The Hacker News 等转述］向日葵个人版 Windows **11.0.0.33 及以下**存在远程代码执行漏洞 **CNVD-2022-10270**、**CNVD-2022-03672**（HTTP 服务的 RPC 接口未授权访问），2023 年被用来投放 Sliver、Gh0st RAT、XMRig 挖矿；部分攻击链用改过的 **mhyprot2 驱动**（《原神》反作弊驱动）关掉杀毒软件（BYOVD，"自带有漏洞的驱动"）。另一批攻击投放 PlugX。
- **建议**：知识库条目"向日葵版本 ≤ 11.0.0.33 → 高，建议升级或卸载"。版本号从已装软件列表读（`DisplayVersion`），只读。
- **来源**：https://thehackernews.com/2023/02/hackers-exploit-vulnerabilities-in.html 、https://www.anvilogic.com/threat-reports/byovd-combos-with-the-sliver-framework-to-exploit-sunlogin-vulnerabilities 、https://www.antiy.net/p/antiy-ptd-effectively-detects-the-exploitation-behavior-of-the-sunflower-remote-code-execution-vulnerability/

### B3. 反诈案例：骗子让装的多是"屏幕共享 / 会议软件"
- **发现**［仅搜索摘要］2025–2026 年公安反诈报道（南京、银川、长春、天津、深圳）的共同模式：冒充平台 / 航空公司客服，以"关闭自动扣费""退款赔偿""资金安全核查"为由，让受害人通过链接下载**屏幕共享软件或会议 App** 并开启屏幕共享 / 远程协助，再套取验证码转账；天津反诈部门称部分 App 带木马可远程控制。**多数报道没点名向日葵、ToDesk**。公安建议：已开启屏幕共享先关闭，无法操作就关机拔卡断网，可拨 96110。
- **建议**
  1. 报告"小知识"：**"任何自称客服、让你下载软件或开屏幕共享的，都是诈骗"**，附 96110。在检测到远控 / 会议软件**最近 7 天新装**时显示这张卡片（比常驻提示更有用，也不吓人）。
  2. 不点名把向日葵、ToDesk 说成"诈骗工具"——它们是正规软件，案例里也多是会议 App。措辞："正规软件，但骗子常利用这类功能"。
- **来源**：https://www.yzwb.net/news/sh/202602/t20260205_319197.html 、https://www.news.cn/legal/20250423/149dfe212a0b49efb6937d582e058bb8/c.html 、https://m.thepaper.cn/newsDetail_forward_32285537 、https://www.sz.gov.cn/hdjl/ywzsk/gaj/xz/content/post_11748910.html

### B4. 二手电脑 / 网吧 / 机房：还原软件和残留远控
- **发现**
  - ［仅搜索摘要］Deep Freeze（冰点还原）的驱动文件是 `DeepFrz.sys`（2014 年 Faronics 支持邮件）；冻结状态可以用它自带的 `DFC.EXE get /ISFROZEN` 查（论坛说法，返回码区分）。服务名 DFServ **［未找到确认］**。
  - ［未找到］二手电脑卖家预装远控的公开报道、国内网吧还原 / 管理软件（如云更新、顺网）的服务名，本轮都没找到可靠来源。
- **建议**
  1. "帮朋友检查刚买的二手电脑"可以做成**一个独立的报告视角**（远程体检包已有，正好适合）：重点列远控软件（B1 规则）、还原软件、不是本人的微软账户 / 本地账户、BitLocker 恢复密钥归属、残留的公司管理（MDM / 组策略"由组织管理"）。前几项现有采集已覆盖一部分。
  2. 还原软件只做"识别 + 告诉用户你的改动重启后会消失"，不做任何操作。驱动名 `DeepFrz` 可先作为知识库"推测"类条目。
- **来源**：https://lists.oplin.org/pipermail/oplintech/2014-October/011344.html 、https://forum.bigfix.com/t/retrieved-property-for-faronics-deepfrozen-workstations/2051

---

## C. OEM 预装软件

### C1. LOLDrivers 里的 OEM 驱动（离线核对，直接可用）
- **发现**［事实，`drivers.json` 原文］以下 OEM 驱动在 LOLDrivers 里被列为"有漏洞驱动"（注意：**列的是特定哈希的样本，同名新版不一定有漏洞**）：
  - **华为**：`HwOs2Ec10x64.sys` / `HwOs2Ec7x64.sys`（产品名 "Huawei MateBook"）、`HWAuidoOs2Ec.sys`（"Huawei Audio Driver"，2026-04 收录）。**作者本机就是华为，值得优先查一下本机有没有这两个文件、哈希是否命中。**
  - **联想**：`LenovoDiagnosticsDriver.sys`（条目里写 CVE-2022-3699）、`LnvMSRIO.sys`（CVE-2025-8061）、`bootrepair.sys`、`srswdrv.sys`。
  - **戴尔**：`dbutil_2_3.sys`、`DBUtilDrv2.sys`（CVE-2021-36276）、`DDDriver.sys`、`dddriver64Dcsa.sys`、`WDTKernel.sys`、`dtr_ec.sys`。
  - **惠普**：`HpPortIox64.sys`、`etdsupp.sys`、`HP_SWTOOLS_DRIVER.sys`、`hp64vision.sys`。
  - **华硕**：`AsIO.sys` / `AsIO2.sys` / `AsIO3.sys`（37 个样本）、`AsusSAIO.sys`（CVE-2024-55408）、`asmmap64.sys`、`IOMap64.sys`、`EIO.sys`、`ATSZIO.sys`、`bsitf.sys`。
  - 其他常见：微星 `NTIOLib*.sys`、技嘉 `gdrv.sys`、`RTCore64.sys`（MSI Afterburner 相关）、`WinRing0x64.sys`（很多硬件监控软件自带）、`throttlestop.sys`（CVE-2025-7771）、AMD `AMDRyzenMasterDriver.sys`、`cpuz*.sys`。
- **建议**：见 D 节末尾的统一方案——**用 SHA-256 / Authentihash 离线比对 LOLDrivers**。
- **来源**：https://github.com/magicsword-io/LOLDrivers （`loldrivers.io/content/api/drivers.json`，本地副本 `/tmp/claude-0/-home-user-pcdoctor/9cc85bf2-1ad4-52b6-8a56-a7200183e41c/scratchpad/loldrv/drivers.json`）

### C2. OEM 工具自身的漏洞历史（CVE）
| 厂商 / 软件 | CVE | 内容 | 可信度 |
| --- | --- | --- | --- |
| 华为电脑管家 PCManager | CVE-2019-5241、CVE-2019-5242 | 本地提权 / 任意代码执行，**微软研究人员通过 Defender ATP 异常行为发现**；修复版 9.0.1.50 | ［仅搜索摘要，SecurityWeek 等］ |
| 华为电脑管家 | CVE-2020-1817（< 10.0.1.36）、CVE-2020-1844（< 10.0.5.51）、CVE-2020-1845（< 10.0.5.53，CVSS 6.7） | 本地提权 | ［仅搜索摘要，CVE 库转述］ |
| 华硕 DriverHub | CVE-2025-3462（8.4）、CVE-2025-3463（9.4） | 来源校验 / 证书校验缺陷，可串联成远程代码执行（网站可让它装东西）；2025-05-09 修复，1.0.6.0 及以后 | ［仅搜索摘要，多源一致］ |
| 华硕 Armoury Crate | CVE-2025-3464（8.8） | AsIO3.sys 导致提权到 SYSTEM；之后还有 CVE-2025-9337 / 9338（AsIO3.sys） | ［仅搜索摘要，单一来源转载，未见华硕公告原文］ |
| 联想 System Interface Foundation（ImControllerService） | CVE-2021-3922、CVE-2021-3969 | 提权，< 1.1.20.3 | ［仅搜索摘要］ |
| 联想 Vantage | CVE-2023-6044 | 物理接触的本地攻击者冒充 Vantage 服务提权（LEN-144736） | ［仅搜索摘要］ |
| 戴尔 SupportAssist | CVE-2022-34387、CVE-2023-44283、CVE-2023-48670、CVE-2023-25535、CVE-2024-38305 | 多为本地提权 / 安装程序缺陷；修复版见 NVD（如 Home PCs 3.14.1 之后；CVE-2024-38305 修于 4.3.1） | ［仅搜索摘要，NVD 页面］ |
| 惠普 | ［未找到］本轮没有找到可靠的 HP Support Assistant CVE 摘要 | | |
| 联想 Vantage 2025 年 Atredis 报告 | ［未找到］ | | |

- **建议**
  1. 新增知识库类别"**OEM 工具版本过旧且有已知提权漏洞**"：读已装软件的 `DisplayVersion`，低于修复版本 → 中，建议"在厂商工具里更新"或"不用的话卸载"。只用上表里有**明确修复版本号**的几条，别泛化。
  2. 报告措辞："这是电脑厂商自带的软件，有用（驱动更新、电池保养），但旧版本有安全漏洞"——不要说成"流氓软件"。

### C3. OEM 常驻服务名（部分核实）
- **联想**：`ImControllerService`，显示名 **System Interface Foundation Service**［仅搜索摘要］；进程 `Lenovo.Modern.ImController.PluginHost*.exe`，有用户报告高 CPU；`LenovoVantageService` 有高 CPU 报告［仅搜索摘要，低质量来源为主］。
- **戴尔**：`SupportAssistAgent`，路径 `C:\Program Files\Dell\SupportAssistAgent\bin\SupportAssistAgent.exe`［仅搜索摘要］；戴尔官方 KB 000129424：Dell Insights / SupportAssist for Business（v2.1.4 及以前）因**性能计数器损坏**导致 `Dell.Genoa.Agent.exe` 高 CPU，官方修法是停服务、用 `lodctr` 重建计数器［仅搜索摘要］；`DDVCollectorSvcApi.exe`（DDV 数据收集）是 SupportAssist 依赖的服务。
- **华硕**：Armoury Crate 发布说明列出 Armoury Crate Service、Armoury Crate Lite Service、AURA Service（Lighting Service）、ROG Live Service、ASUS Framework；论坛提到 `AsusCertService`、`asComSvc`、`AsusUpdateCheck`、`AsusFanControlService`［仅搜索摘要］。**普通卸载会留下残留，官方提供"Armoury Crate Uninstall Tool"**（ASUS 发布说明："升级卸载工具以避免卸载不完整"）。
- **华为**：已有 `dokan2a`（知识库 `huawei-dokan`）；本轮补充 `HwOs2Ec10x64.sys`（见 C1）。华为电脑管家服务名［未找到］。
- **惠普**：［未找到］HP Analytics / HP Insights 相关服务名的可靠来源。
- **建议**
  1. 这些服务名先进知识库的**推测类（knowledge 提示）**，等朋友机器上实测到再升级为 `direct`（和现有 `huawei-dokan` 的做法一致：路径 + 签名 + 厂商）。
  2. 华硕要专门写一条"**卸载 Armoury Crate 请用官方卸载工具**，系统'应用'里卸载会留下服务和驱动"——这是典型的信息差。
  3. 戴尔"性能计数器损坏导致高 CPU"可以作为"单个第三方进程累计 CPU 高"规则（附录 A 缺口②）的知识库示例。
- **来源**：https://appuals.com/services-and-controller-app-random-high-cpu-usage/ 、https://www.file.net/process/lenovo.modern.imcontroller.pluginhost.device.exe.html 、https://www.dell.com/support/kbdoc/ko-kr/000129424/& 、https://www.winhelponline.com/blog/wp-json/wp/v2/posts/18635 、https://rog-forum.asus.com:443/t5/release-notes/release-notes-armoury-crate-v5-6-3-0-aura-creator-3-6-5-0/ba-p/929268 、https://docs.signalrgb.com/troubleshooting/brand-specific/asus/ 、https://securityweek.com/microsoft-finds-privilege-escalation-code-execution-flaws-huawei-tool 、https://thehackernews.com/2025/05/asus-patches-driverhub-rce-flaws.html 、https://nvd.nist.gov/vuln/detail/cve-2023-44283 、https://tenable.com/cve/CVE-2023-6044

---

## D. 游戏玩家场景

### D1. 反作弊驱动：哪些在 LOLDrivers 里
- **发现**［事实，`drivers.json` 原文］
  - **腾讯 ACE**：`ACE-BASE.sys`，公司 ANTICHEATEXPERT.COM，产品 Anti-Cheat Expert，列为有漏洞驱动（`Verified: FALSE`，即 LOLDrivers 自己也没验证哈希）。
  - **BattlEye**：`BEDaisy.sys`，34 个样本，`Verified: TRUE`。
  - **米哈游**：`mhyprot.sys` / `mhyprot2.sys` / `mhyprot3.sys` / `mhyprotect.sys` 等，签名者 miHoYo Co.,Ltd.——就是 B2 里向日葵攻击链用来关杀软的那个驱动。
  - **西山居**：`SeasunProtect.sys`（成都西山居）。
  - 本轮在 LOLDrivers 里**没找到** Vanguard（vgk.sys）、EasyAntiCheat。
- **发现**［仅搜索摘要］ACE 文件在 `C:\Program Files\AntiCheatExpert\`，卸载游戏后控制面板删不掉，文件被内核驱动占用；启动相关游戏又会装回来；进程 `SGuard64.exe`、`SGuardSvc64.exe` 被大量玩家抱怨占 CPU（GitHub 上有专门限制它的工具 ACE-YES）。
- **发现**［事实，Riot 官方支持页，仅搜索摘要］Vanguard 由服务 `vgc` 和驱动 `vgk` 组成；官方手工卸载步骤：管理员命令行 `sc delete vgc`、`sc delete vgk`，重启，再删 Program Files 下的 Riot Vanguard 文件夹；官方承认 Vanguard"有时会顽固残留"；启动依赖它的游戏会自动重装。第三方指南提醒：**不要先手动删 vgk.sys**；在启动项里关掉 `VGTray.exe` 只是隐藏图标，不等于 Vanguard 停了。
- **建议**
  1. 现有知识库 `anticheat-leftovers`（`^(EasyAntiCheat|UniFairy|unirsdt|ACE-)`）可以补：`vgk` / `vgc`（Riot，签名者需实测）、`BEDaisy` / `BEService`（BattlEye）、`mhyprot*`（米哈游）、`SeasunProtect`。
  2. **"游戏已卸载但反作弊还在"**是一个高价值的"历史遗留"规则：反作弊驱动 / 服务存在，但已装软件里找不到对应游戏或启动器（腾讯 WeGame、Riot Client、米哈游启动器）→ 中，"卸载残留"。处理方式只给厂商官方步骤（如 Riot 的 `sc delete` 流程），走阶段 4 的黄色 / 红色操作，严格遵守红线 1（正在运行不 stop）。
  3. `SGuard64` 高 CPU 适合放进"单个第三方进程累计 CPU 高"规则（附录 A 缺口②），知识库给解释："腾讯游戏反作弊，游戏关了它有时还在扫描"。
- **来源**：https://www.loldrivers.io/drivers/ff77b58d-e143-4f61-92de-c0d9bc0af7d5/ 、https://juejin.cn/post/7651818452136329250 、https://github.com/mmm1h/ACE-YES 、https://support.riotgames.com/en-us/riot/performance/uninstall-and-disable-vanguard 、https://blog.gridinsoft.com/vgtray-exe/

### D2. 国产安全软件和工具的驱动也在 LOLDrivers 里
- **发现**［事实，`drivers.json` 原文］**360**：`360FsFlt_win10.sys`、`360netmon_wfp.sys`（2026-06 收录）、`360hvm64.sys`、`DsArk64.sys`（Qihoo360 Kernel Mode Driver）；**火绒**：`sysdiag.sys`（2026-09 收录）、`nxeng.sys`；**金山**：`ksapi.sys`、`kdhacker64_ev.sys`；**百度杀毒**：`BdApiUtil64.sys`；**IObit**：`iobitunlocker.sys`、`IUForceDelete.sys`、`IMFForceDelete`、`Monitor_win10_x64.sys`（Advanced SystemCare）；**Process Explorer** 的 `procexp.sys` 旧版；`kprocesshacker.sys`。
- **建议**：措辞很重要——"有漏洞驱动"指**特定旧版本**可被恶意软件利用来关掉杀软，不等于这个软件是坏的。火绒是我们的"真正对手"（第一轮），报告里如果命中火绒的驱动要格外中立，只说"这个版本的驱动文件在公开的漏洞驱动库里，建议升级"。

### D3. 加速器、内存完整性
- **发现**
  - ［事实，微软 Learn 文档，仅搜索摘要］**微软"易受攻击驱动阻止列表"从 Windows 11 22H2 起所有设备默认开启**，可在 Windows 安全中心开关；在**内存完整性（HVCI）、智能应用控制或 S 模式开启时强制生效**（此时开关是灰的）；列表随大版本更新，**一般一年 1–2 次**，也可作为可选更新获取；微软明确说**不保证拦住所有有漏洞的驱动**，而且拦截可能让设备或软件故障，少数情况蓝屏。
  - ［仅搜索摘要］"内存完整性无法打开"的常见原因是旧厂商软件的驱动（罗技官方文档举例），可在"内核隔离 → 查看不兼容的驱动程序"看到列表。
  - ［未找到］UU、迅游、雷神等加速器具体的驱动名 / LSP / WFP 组件名，本轮没找到可靠来源。
- **建议**
  1. 采集新增只读项：**易受攻击驱动阻止列表是否开启**（注册表 `HKLM\SYSTEM\CurrentControlSet\Control\CI\Config` 的 `VulnerableDriverBlocklistEnable`［已有知识，需在 Windows 上核实键名］）和智能应用控制状态。关闭时 → 中（和现有 `memory-integrity-off` 配合说明）。
  2. 现有规则 `memory-integrity-off` 可以补一个证据：**是哪个驱动挡住了内存完整性**。［推测］不兼容驱动列表没有公开 API，可以退而求其次：列出第三方、非 HVCI 兼容的驱动——这需要读 PE 头，属于 .NET 采集器阶段的工作，先记下。
  3. 加速器的驱动名等朋友机器实测后再进知识库，现在不写。
- **来源**：https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules 、https://support.microsoft.com/en-us/kb/5020779 、https://support.logi.com/hc/zh-cn/articles/19426387012119

### C + D 统一方案：离线比对 LOLDrivers（本轮性价比最高的发现）
- 做法：采集时对**所有第三方内核驱动文件**算 SHA-256（采集脚本已经在读驱动列表和签名），研判侧用打包进来的 LOLDrivers 哈希表比对（706 条，几千个样本哈希；只带哈希、文件名、厂商、分类、CVE 字段，体积很小）。
- 输出两类：
  - `driver-known-vulnerable`（哈希命中"有漏洞驱动"）→ 中 / 高："这个版本的驱动在公开漏洞库里，恶意软件可以借它关掉杀毒软件。建议在厂商工具里更新，或卸载不用的软件"。
  - `driver-known-malicious`（哈希命中"恶意驱动"）→ 高，并建议用 Defender 扫描（符合"查病毒交给 Defender"）。
- **只认哈希，不认文件名**：同名文件的新版本可能已修复；文件名命中只能作为知识库的"提示"，不扣分。
- 好处：完全只读、不联网、可复现，和"每个结论有证据"的品牌一致；能直接覆盖华为 `HwOs2Ec10x64.sys`、ACE、mhyprot、BattlEye、戴尔 dbutil、华硕 AsIO 这些用户群里真实常见的驱动。
- 注意：LOLDrivers 本身也有 `Verified: FALSE` 的条目（如 ACE-BASE.sys），这类命中要降一级可信度；数据集要标注版本和下载日期，跟着发布包更新。许可证需要确认（［未找到］本轮没查 LOLDrivers 的协议）。

---

## E. 体检报告的风险沟通

> 本节检索很少（2 次），多为经验和已有知识，使用前请再核实。

### E1. SSL Labs 的"封顶 + 警告"分级
- **发现**［仅搜索摘要，SSL Labs 官方博客］SSL Labs 用两种机制：**封顶（grade cap）**——某个严重问题直接把最高分限制在 B / C / F（例如 Heartbleed 直接 F、不支持 TLS 1.2 封顶 B）；**警告（warning）**——配置不错但有警告时给 A-，警告指"现在还不影响评分、但以后很可能会影响"的问题。
- **对 pcdoctor 的建议**
  1. 健康分可以引入"**封顶**"：出现"真的有病毒 / 未处理的威胁""系统盘快满"这类严重问题时，无论其他项多好，总分封顶（例如 59 分），并在分数旁写一句"因为 X，分数最高只能到 59"。这比"扣 30 分"更好理解，也防止"88 分但其实中毒了"的误导。
  2. 引入"**提醒**"级：不扣分或只扣一点，专门放"现在没问题、但以后可能有问题"的项（快速启动、长期不重启、BitLocker 恢复密钥）。和现有 `CLEARABLE_RULES` 的思路一致。
- **来源**：https://blog.ivanristic.com/2014/01/ssl-labs-stricter-security-requirements-for-2014.html 、https://blog.qualys.com/product-tech/2016/11/16/announcing-ssl-labs-grading-changes-for-2017
- ［未找到］Mozilla Observatory 的加减分说明。

### E2. 怎么表达"推测""不确定"
- ［已有知识，未本轮检索］IPCC（联合国政府间气候变化专门委员会）用一套**固定的概率词表**：几乎确定（99–100%）、很可能（90–100%）、可能（66–100%）、或许（33–66%）、不太可能（0–33%）……并且要求作者只能用这些词。
- ［已有知识］医学体检报告的通行做法：每项给"参考范围"，异常项用箭头标"偏高 / 偏低"，再分"建议复查 / 建议就医"，而不是只给结论。信用报告（如央行征信）列事实、不打分。［未调研具体规范］
- **建议**
  1. 把模型输出里的 `confidence` 映射成**固定的三到四个中文词**（"可以确定 / 很可能 / 可能 / 只是猜测"），提示词里要求模型只能用这几个词，报告里统一显示，并在报告末尾解释每个词的意思。这样"推测"不会被读成"事实"（第一轮复核里"电池快充满了"就是这类问题）。
  2. 每条结论分"**我们看到了什么**（证据）/ **我们认为是什么**（判断 + 可信度词）/ **你可以怎么做**"三段，学体检报告的"结果 + 参考范围 + 建议"。
  3. 不用红色大字；"高"风险用文字和图标表达，颜色只做辅助（设计规范 2.5 已有这个方向）。

---

## F. 小样本评估：10 台机器够不够

> 计算用 Wilson 区间（Python 实算），统计方法属［已有知识］。

### F1. 数字
| 观测 | 95% Wilson 区间 | 说明 |
| --- | --- | --- |
| 单台 1/20 错 | 0.9% – 23.6% | 一台机器的误判率几乎没有意义 |
| 单台 0/20 错 | 0% – 16.1% | 全对也不能说明 < 10% |
| 10 台共 200 条，错 10（5%） | 2.7% – 9.0% | 上限 < 10%，**勉强**能说过关 |
| 200 条，错 14（7%） | 4.2% – 11.4% | 不能说过关 |
| 200 条，错 16（8%） | 5.0% – 12.6% | 不能说过关 |
| 400 条，错 25（6.25%） | 4.3% – 9.1% | 能说过关 |

"单侧 95% 上限 < 10%"需要的结论条数（假设各条独立）：

| 真实误判率 | 需要的结论数 |
| --- | --- |
| 0%（全对） | ≥ 25 |
| 3% | ≥ 42 |
| 5% | ≥ 86 |
| 6% | ≥ 138 |
| 7% | ≥ 248 |
| 8% | ≥ 568 |

### F2. 两个容易忽略的坑
1. **同一台机器的结论不独立**（同一个人的习惯、同一个品牌的全家桶）。按"整群"算，设计效应 ≈ 1 +（每台条数 − 1）× 组内相关。每台 20 条、组内相关 0.05 时设计效应约 1.95，200 条只相当于约 100 条独立样本。［已有知识］**建议同时报告"按条数"和"按机器"两种误判率**，并用"整台机器为单位的自助法（cluster bootstrap）"算区间——`judge/review.py stats` 已经按文件夹分机器，加这个不难。
2. **知识库直接下结论的条目会稀释误判率**：作者本机 51 条预筛里 16 条不经模型。建议 `review.py` 把"知识库直接结论""纯规则""模型结论"**分开统计**，关卡 1 只看模型结论那一组（这才是要验证的东西），另外两组单独报告。

### F3. 人工复核的做法（建议）
- **盲评**［已有知识］：复核时不看"这条是模型说的还是知识库说的"、不看置信度，只看证据和结论——`-review.csv` 可以加一个"盲评模式"，把来源列隐藏或打乱顺序。
- **一致性**［已有知识］：抽 20–30% 的结论请第二个人（比如一个懂电脑的朋友）独立标注，算 Cohen's kappa（两人一致程度，扣掉碰巧一致的部分）；一般 > 0.6 算"较好"。不一致的条目拿出来讨论，往往能发现"对 / 不准"的标准没写清楚。
- **事先写好判定标准**：什么算"错"、什么算"不准"，写成一页说明并固定下来，避免看了结果再改标准。
- **关卡 1 的建议改写**：从"10 台、误判 < 10%"改成"≥ 10 台、模型结论 ≥ 200 条、误判率的 95% 上限 < 10%（按机器整群计算）"。如果真实误判率在 5% 左右，200 条大概够；如果在 7–8%，需要 20 台以上——**这个数字本身就说明，提示词 / 知识库的改进比多找机器更划算**。

---

## G. Windows 的变化对采集脚本的影响

### G1. WMIC 已经被移除
- **发现**［仅搜索摘要，微软支持文章 + BleepingComputer，多源一致］时间线：2016 年 Server 2012 弃用 → 2021 年 Win10 21H2 弃用 → 2022 年 Win11 22H2 变成"按需功能"（默认装上）→ 2024 年 Win11 23H2 / 24H2 默认禁用 → **升级到 25H2 时移除**；微软支持文章（2026-08-19 更新）说 **WMIC 已从当前所有受支持的 Win11 版本移除，也不能再作为按需功能安装**；BleepingComputer 2026-08-18：已从 24H2 和 25H2 移除。**WMI 本身不受影响**，只是命令行工具没了。
- **建议**：采集脚本已经用 CIM（`Get-CimInstance`），不受影响。但要检查：①`remote/`、`run.ps1`、文档、知识库的"处理方法"里有没有写 `wmic` 命令；②**报告里给用户的手工命令不能再出现 wmic**（很多网上教程还在用）；③可以作为"小知识"：网上老教程里的 wmic 命令在新系统上已经不能用了。
- **来源**：https://support.microsoft.com/help/5067470 、https://bleepingcomputer.com/news/microsoft/microsoft-removes-wmic-lolbin-tool-in-windows-11-beta-builds

### G2. VBScript
- **发现**［仅搜索摘要，多源一致］2024-05 微软宣布三阶段：第一阶段 Win11 24H2 起 VBScript 是"按需功能"、默认开启；**第二阶段约 2027 年默认关闭**；第三阶段从未来版本中移除（无日期）。
- **建议**
  1. 我们自己不用 VBScript（已满足）。
  2. **新增采集规则候选**：计划任务 / 启动项里调用 `wscript.exe` / `cscript.exe` 执行 `.vbs` 的第三方项——2027 年后它们会静默失效，而且 .vbs 自启动本身就是恶意软件常用手法。报告分两种说法：签名软件的 → "以后会失效，建议更新软件"；用户目录下无签名的 .vbs 自启动 → "可疑，建议用 Defender 扫描"。
- **来源**：https://www.theregister.com/2024/05/23/windows_11_24h2_vbscript/ 、https://cyberinsider.com/microsoft-announces-deprecation-of-vbscript-on-windows-by-2027/

### G3. Windows 10 停止支持与 ESU
- **发现**
  - ［事实］Windows 10 于 2025-10-14 停止支持。
  - ［仅搜索摘要，未在微软在线页面核实］个人版 ESU（扩展安全更新）三种免费 / 付费方式：同步电脑设置到微软账户（免费）、1000 微软奖励积分、30 美元一次性购买；欧洲经济区登录微软账户即可免费。**多家媒体（pureinfotech 等）报道微软 2026-06-25 把个人版 ESU 延长到 2027-10-12**；微软的缓存页面还写着 2026-10-13。
  - ［未找到］中国大陆个人用户能否加入 ESU、如何只读判断一台机器是否已加入 ESU。
- **建议**
  1. 新增规则：Win10 且最近一次安全更新超过 60 天 → 高，说明"你的系统已经不再自动获得安全修补"；Win10 但安全更新还在持续 → 信息类"可能已加入 ESU，到 2027-10 为止"。用"最近一次安装的累积更新时间"判断，比判断 ESU 注册状态更可靠也更通用。［推测］
  2. 这直接关系 roadmap 的待定问题"是否支持 Windows 10"：建议**支持采集和研判**（朋友里 Win10 机器多），但操作库只在 Win11 上放开，Win10 的报告重点写"升级 / 换机建议"。
- **来源**：https://pureinfotech.com/windows-10-esu-extended-2027-microsoft-update/ 、https://www.microsoft.com/windows/extended-security-updates 、https://learn.microsoft.com/en-ca/answers/questions/5805066/windows-esu-does-enrollment-end-on-13-oct-2026-or

### G4. PowerShell 5.1 的未来
- ［未找到］本轮没有找到微软关于 Windows PowerShell 5.1 移除计划的声明。［已有知识］目前它仍随 Windows 自带，微软的说法一直是"不再加新功能、只修安全问题"。
- **建议**：维持现在的做法（兼容 5.1、不依赖 pwsh 7）；既然最终要迁移到 .NET 采集器，没必要为此投入。每次 Windows 大版本更新后用远程体检包跑一次即可发现问题。

---

## H. 知识库众包与防投毒

### H1. winget-pkgs
- **发现**［仅搜索摘要］社区提交的清单先过**自动校验**（其中一步是 SmartScreen 检查安装包 URL，信誉差会被打上 `URL-Validation-Error` 标签），再由**志愿者版主人工审核**；被杀软误报的安装包，指南建议提交给 Defender 团队做误报分析。
- **建议**：将来众包时，用户提交的"这是某某软件的组件"必须带**文件哈希 + 签名者**，入库前自动核对：签名有效、签名者和声称的厂商一致、哈希不在 LOLDrivers 恶意表里、VirusTotal 结果（如果有）干净；再人工审核。和 `kb/seed.json` 现有的"direct 条目必须要求签名有效 + 路径或厂商"一致。
- **来源**：https://github.com/microsoft/winget-pkgs/pull/246675 、https://github.com/renovate-bot/winget-pkgs/blob/master/Troubleshoot.md

### H2. 广告过滤列表（EasyList / uBlock）
- **发现**［仅搜索摘要］IMC 2019 论文《Errors, Misunderstandings, and Attacks: Analyzing the Crowdsourcing Process of Ad-blocking Systems》研究 EasyList 的众包过程：误报（错误拦截正常内容）往往很久才被发现，编辑和网站方都有责任；论文标题点明还研究了"攻击"（借众包流程投毒）。uBO 作者的立场：规则造成的问题要报给**列表维护者**，不是报给拦截器本身——即"引擎"和"规则"分开维护、分开问责。
- **建议**
  1. 知识库和程序**分开发版、分开签名**，每条知识库条目带"提交人 / 审核人 / 依据（哪台机器、哪份报告）/ 日期"，出了误判能追溯和撤回。
  2. 众包条目默认只能是**"提示"类**（`knowledge`），不能直接成为 `direct` 结论；升级为 `direct` 需要 ≥ 2 台独立机器复现 + 维护者审核。这正好防住"有人提交'某恶意驱动是正常组件'"的投毒。
  3. 每条知识库条目要能被"一键撤回"，客户端下次更新就失效。
- **来源**：https://www.cse.psu.edu/~sxz16/papers/IMC19.pdf 、https://pure.psu.edu/en/publications/errors-misunderstandings-and-attacks-analyzing-the-crowdsourcing-

### H3. ClamAV
- **发现**［仅搜索摘要，ClamAV 官方 FAQ］误报 / 漏报通过网页表单或 `clamsubmit` 提交，**大部分由自动化处理**，一般至少 48 小时进入官方库；上传的文件只有 Cisco Talos 内部人员能看；第三方签名带 `.UNOFFICIAL` 后缀，误报要找该签名的发布者，而不是 ClamAV。
- **建议**：学 `.UNOFFICIAL` 的做法——知识库条目分"**官方**（我们审核的）"和"**社区 / 本地**"（用户自己 `--confirm` 的、第三方列表），报告里显示来源；用户自己确认的只在本机生效（现在 `pcdoctor-known.json` 已经是这样）。
- **来源**：https://docs.clamav.net/faq/faq-malware-fp-reports.html 、https://sanesecurity.com/support/false-positives/

---

## 可以直接写进知识库的条目

可信度：**高**＝有原文 / 数据集核对；**中**＝多个搜索摘要一致；**低**＝单一摘要或推测，只能作为 `knowledge` 提示。所有"高"条目进入 `direct` 之前，仍需要在真实 Windows 上核对路径和签名者。

| 对象名 | 特征（建议匹配条件） | 属于哪个软件 | 建议结论 | 可信度 | 来源 |
| --- | --- | --- | --- | --- | --- |
| `AnyDesk`（服务） | 服务 ImagePath 含 `AnyDesk.exe" --service`；`%ProgramData%\AnyDesk\` 存在 | AnyDesk 远程控制 | 识别；服务 + 自动启动 → 中"开机即可被远程连接"，问用户是否本人安装 | 高 | LOLRMM anydesk.yaml |
| AnyDesk 无人值守登录记录 | `%ProgramData%\AnyDesk\connection_trace.txt` 中近 30 天出现 `Passwd` / `Token` 行 | AnyDesk | 中："最近 N 次用密码 / 令牌远程登录过"，只给次数和时间 | 高 | LOLRMM anydesk.yaml |
| `TeamViewer`（服务） | 服务名 `TeamViewer`，`TeamViewer_Service.exe`；`connections_incoming.txt` | TeamViewer | 同上 | 高 | LOLRMM teamviewer.yaml |
| `RustDesk`（服务） | 服务名 `RustDesk`，`RustDesk.exe --service`；`RustDesk2.toml` 在 LocalService 配置目录 | RustDesk（开源，可自建服务器） | 同上；自建服务器地址非官方时附加说明 | 高 | LOLRMM rustdesk.yaml |
| `ToDesk_Service.exe` | 进程 / 服务可执行文件名；域名 `*.todesk.com` | ToDesk | 识别；服务 + 自动启动 → 中 | 中（服务名未核实） | LOLRMM todesk.yaml、Elastic 规则 |
| `OrayRemoteService.exe` / `OrayRemoteShell.exe` / `sunlogin*.exe` | 签名者"上海贝锐信息科技股份有限公司" | 向日葵 | 识别；服务 + 自动启动 → 中；版本 ≤ 11.0.0.33 → 高（已知被利用的远程代码执行漏洞） | 高（文件名、签名者）/ 中（版本阈值） | LOLRMM sunlogin.yaml；ASEC / THN |
| `ACE-BASE.sys`、`SGuard64.exe`、`SGuardSvc64.exe` | 路径 `C:\Program Files\AntiCheatExpert\` 或 `drivers\`；公司 ANTICHEATEXPERT.COM | 腾讯 ACE 反作弊 | 识别；无腾讯游戏 / WeGame 时 → "卸载残留"；`SGuard64` 高 CPU 时给解释 | 中（LOLDrivers 标 Verified FALSE） | LOLDrivers、掘金 |
| `vgk`（驱动）/ `vgc`（服务） | 服务名；路径含 `Riot Vanguard` | Riot Vanguard（无畏契约、英雄联盟） | 识别；无 Riot Client 时 → 残留，处理方法用 Riot 官方步骤；**不手删 vgk.sys** | 中 | Riot 官方支持页 |
| `BEDaisy.sys` | 公司 / 签名需实测 | BattlEye 反作弊 | 识别；哈希命中 LOLDrivers → 建议更新游戏 | 高（在 LOLDrivers 中） | LOLDrivers |
| `mhyprot*.sys` | 签名者 miHoYo Co.,Ltd. | 米哈游游戏反作弊 | 识别；**曾被攻击者用来关杀软**；没装米哈游游戏却存在 → 高（可能被恶意软件带进来） | 高 | LOLDrivers；ASEC 向日葵攻击链 |
| `SeasunProtect.sys` | 公司 成都西山居 | 西山居游戏保护 | 识别；哈希命中 → 建议更新 | 高 | LOLDrivers |
| `HwOs2Ec10x64.sys`、`HwOs2Ec7x64.sys` | 产品名 "Huawei MateBook" | 华为 MateBook 驱动 | 识别；**哈希**命中 LOLDrivers → 中"建议在华为电脑管家更新驱动" | 高（在 LOLDrivers 中） | LOLDrivers |
| `HWAuidoOs2Ec.sys` | 产品名 "Huawei Audio Driver" | 华为音频驱动 | 同上 | 高 | LOLDrivers |
| 华为电脑管家（PCManager） | 已装软件版本 < 10.0.5.53 | 华为 | 中：旧版本有本地提权漏洞（CVE-2019-5241、CVE-2020-1817/1844/1845），建议更新 | 中 | SecurityWeek、CVE 库 |
| `LenovoDiagnosticsDriver.sys`、`LnvMSRIO.sys` | 公司 Lenovo | 联想诊断 / 驱动 | 哈希命中 → 中（CVE-2022-3699、CVE-2025-8061） | 高 | LOLDrivers |
| `ImControllerService` | 显示名 System Interface Foundation Service | 联想 Vantage 底层服务 | 识别；版本 < 1.1.20.3 → 中（CVE-2021-3922/3969） | 中 | 搜索摘要 |
| `dbutil_2_3.sys`、`DBUtilDrv2.sys` | 公司 Dell | 戴尔 BIOS 更新工具驱动 | 哈希命中 → 高（CVE-2021-36276 等，著名漏洞驱动） | 高 | LOLDrivers |
| `SupportAssistAgent` | 路径 `C:\Program Files\Dell\SupportAssistAgent\` | 戴尔 SupportAssist | 识别；旧版本有多个提权 CVE，建议更新 | 中 | NVD、搜索摘要 |
| `Dell.Genoa.Agent.exe` 高 CPU | 进程名 | Dell Insights / SupportAssist for Business | 解释：可能是性能计数器损坏，见戴尔 KB 000129424 | 中 | 戴尔 KB |
| `AsIO.sys` / `AsIO2.sys` / `AsIO3.sys`、`AsusSAIO.sys` | 公司 ASUSTeK | 华硕 Armoury Crate / AI Suite | 哈希命中 → 中 / 高（CVE-2025-3464、CVE-2024-55408）；建议更新，或用**官方卸载工具**卸载 | 高（LOLDrivers）/ 中（CVE-2025-3464） | LOLDrivers、搜索摘要 |
| 华硕 DriverHub | 已装软件版本 < 1.0.6.0 | 华硕 | 高：远程代码执行漏洞（CVE-2025-3462/3463），建议更新或卸载 | 中 | THN、阿联酋网络安全委员会通告 |
| `HpPortIox64.sys`、`etdsupp.sys` | 公司 HP | 惠普工具驱动 | 哈希命中 → 中 | 高 | LOLDrivers |
| `RTCore64.sys` | — | MSI Afterburner 等超频工具 | 哈希命中 → 中（勒索软件常用的漏洞驱动） | 高 | LOLDrivers |
| `WinRing0x64.sys` | — | 多种硬件监控 / 风扇 / 灯效软件 | 哈希命中 → 中；说明"很多监控软件自带，建议升级" | 高 | LOLDrivers |
| `360FsFlt_win10.sys`、`360netmon_wfp.sys`、`DsArk64.sys` | 公司 360.cn | 360 安全卫士 | 哈希命中 → 中，中立措辞 | 高 | LOLDrivers |
| `sysdiag.sys`、`nxeng.sys` | 公司 北京火绒 | 火绒 | 哈希命中 → 中，**特别中立**的措辞，只建议升级 | 高 | LOLDrivers |
| `iobitunlocker.sys`、`IUForceDelete.sys`、`Monitor_win10_x64.sys` | 公司 IObit | IObit Unlocker / Uninstaller / Advanced SystemCare | 哈希命中 → 中 | 高 | LOLDrivers |
| `DeepFrz.sys` | 驱动文件名 | Faronics Deep Freeze（冰点还原） | 识别；信息类"重启后改动会被还原" | 低 | 2014 年邮件列表 |
| `Microsoft.WindowsStore` 包缺失 | AppX 包不存在 | Windows 自带 | 中："可能被优化工具删过，商店很难装回" | 中 | Win11Debloat wiki |
| `Microsoft Edge WebView2 Runtime` | 已装软件 | Windows 依赖组件 | 永远"正常，不要卸载"；缺失 → 中 | 中 | 微软 Q&A |

---

## 第二轮最值得做的 10 个点子（按性价比排序）

1. **离线比对 LOLDrivers（S–M）**：采集时给第三方驱动算 SHA-256，研判时比对打包的哈希表，新增 `driver-known-vulnerable` / `driver-known-malicious`。只读、不联网、证据硬，直接覆盖华为、联想、戴尔、华硕、ACE、米哈游、BattlEye、360、火绒等用户群常见驱动。先在作者本机（华为）跑一次看命中情况。**只认哈希，不认文件名。**
2. **远控软件"无人值守"规则（S）**：用 LOLRMM 的痕迹（服务 + 自动启动 + AnyDesk `connection_trace.txt` 的 `Passwd` / `Token` 次数 + TeamViewer 传入连接记录）+ 向日葵 ≤ 11.0.0.33 版本检查；配一张反诈"小知识"卡片（96110）。二手电脑、父母电脑场景价值很高。
3. **补三条红线进操作库和设计文档（S，只改文档）**：不删系统 AppX（Store / Xbox / WebView2）、不改微软内置服务启动类型、不做注册表清理；外加执行前检查"BitLocker 开启 → 确认恢复密钥已备份"。
4. **关卡 1 改成统计上站得住的标准（S）**：`review.py stats` 输出 Wilson 区间 + 按机器整群的区间；"知识库直接结论 / 纯规则 / 模型结论"分开统计；关卡写成"模型结论 ≥ 200 条、95% 上限 < 10%"。顺便加"盲评模式"（隐藏来源列）。
5. **"系统被优化工具改过"规则（M）**：在干净的 Win10 / Win11 虚拟机上导出系统服务默认启动类型表，采集时对照，找出被禁用的系统服务（蓝牙、搜索、打印、更新、CNG Key Isolation），以及缺失的 Store / WebView2。这是"反 360"最有说服力的招牌之一。
6. **"游戏卸了、反作弊还在"规则（S）**：反作弊驱动 / 服务存在但对应游戏或启动器不在 → 卸载残留；补充知识库 `vgk/vgc`、`BEDaisy`、`mhyprot*`、`SeasunProtect`、`SGuard64`。Riot 的官方卸载步骤可以直接写进"怎么处理"。
7. **健康分"封顶 + 提醒"（S）**：学 SSL Labs——严重问题封顶总分并解释原因；"现在没事、以后可能有事"的项单独一档不扣分。同时把置信度映射成固定的四个中文词，提示词里限定用词。
8. **易受攻击驱动阻止列表 / 智能应用控制状态（S）**：采集新增只读项，配合 `memory-integrity-off` 一起说明（键名要先在 Windows 上核实）。
9. **Win10 停止支持规则（S）**：按"最近一次累积更新时间"判断是否还在拿安全补丁（覆盖 ESU 情况），报告里写清楚 ESU 到 2027-10（需在微软官网核实）；顺带决定 roadmap 里"是否支持 Win10"——建议支持采集和研判，操作只在 Win11 放开。
10. **.vbs 自启动规则 + 去掉一切 wmic 文本（S）**：计划任务 / 启动项里 `wscript` / `cscript` 跑 `.vbs` 的第三方项（2027 年默认失效，且是恶意软件常用手法）；检查仓库文档和报告模板里没有给用户 `wmic` 命令。

---

## 本轮未调研 / 没找到、留给下一轮

- 国产加速器（UU、迅游、雷神）的驱动名、LSP、WFP 组件——需要在装了加速器的朋友机器上实测，网上没有可靠来源。
- 向日葵、ToDesk 的 Windows 服务名和配置文件路径（含"无人值守"开关保存在哪）——LOLRMM 里这两个条目是空的，需要实测；可以考虑实测后给 LOLRMM 提 PR（顺便给项目做宣传）。
- 华为电脑管家、惠普工具的服务名；联想 Vantage 2025 年漏洞报告。
- 易受攻击驱动阻止列表的注册表键名（凭记忆写的，需核实）；如何只读判断 BitLocker 恢复密钥是否已备份到微软账户。
- 中国大陆个人用户能否加入 Windows 10 ESU。
- Mozilla Observatory 评分说明、征信报告呈现规范、医学体检报告的国家标准。
- LOLDrivers / LOLRMM 的开源协议（打包进发布版前必须确认）。
- 网吧还原 / 管理软件、二手电脑卖家预装远控的公开案例。
