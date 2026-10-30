# CLAUDE.md — Iris · AI 电脑体检（GitHub 仓库 WilhelmWei/Iris，公开）

## 项目一句话

面向中文 Windows 用户的 LLM 电脑体检工具。固定代码采集 → 规则预筛 → 知识库匹配 → LLM 研判 → 报告 → 用户确认 → 只从操作库执行 → 重新检测验证 → 失败自动回滚。

完整设计在 `docs/core-design.md`，动手前先读第 1、5、6 章和附录 A；进度表在 `docs/roadmap.md`。

## 和用户沟通

- 用户是作者 Wilhelm，用中文交流；面向用户的文案、报告、界面一律中文，人名或产品名需要时用英文
- 用户会 Python、Next.js，常用 AI 辅助写代码；C# / Windows 内核细节需要解释清楚
- 重要的新想法先讲清楚、等用户点头再动手
- 收费方向：免费自带 Key 版 + 托管服务 + 打赏；不卖授权（AGPL-3.0 纯开源）

## 提交约定（用户要求）

- **提交作者是 Wilhelm**（`Wilhelm <113165252+WilhelmWei@users.noreply.github.com>`），Claude 只作为共同作者，在提交说明末尾写 `Co-Authored-By`；开始工作前先确认 `git config user.name / user.email`
- 提交说明、代码、文档里不写模型名称
- **仓库是公开的**：不提交密钥、真实体检报告、作者或朋友电脑的具体信息（用户名、电脑名、装了什么软件、网络配置）；测试数据一律用虚构的（Alice、LAPTOP-TEST01、QuickVPN）
- 安装包不进仓库（`release/` 已忽略），只放下载服务器和 GitHub Releases

## 内部记录

开发过程的完整记录（每一轮实测和研判复核的细节、内测审批、服务器和运维信息）在**私有仓库 `WilhelmWei/iris-cloud` 的 `docs/internal/`**（`pcdoctor-CLAUDE-full.md`、`pcdoctor-roadmap-full.md`、开通服务的提示词等）。会话里能访问 iris-cloud 时，处理内测、服务器、反馈相关的事先读那里。

## 当前状态（2026-10-30）

- 命令行原型（`collector/`、`judge/`、`kb/`、`prompts/`、`run.ps1`、`remote/`）：采集 13 个方面、规则预筛、知识库、AI / 纯规则研判、误判复核表、远程体检包，已在真实电脑上多轮验证
- 桌面版 1.0 内测 **1.0.0-beta.5**：.NET 10 WinForms + WebView2；快速设定、体检（实时发现流、可先 Defender 扫描）、报告（证据、交叉校验、"判断不对"、和上次比）、「处理」页（待处理 → 执行 → 刚刚完成 → 已处理，可撤销）、存储空间清理、三套主题和动效、内测反馈（同意后去隐私发送，含「处理」页的操作结果和 Defender 扫描是否正常结束）
- 清理第一期（1.0.0-beta.6，已打包未发布）：清理目录表 `kb/junk.json`（A 类 Iris 直接删、B 类交给 Windows、不清理的伪优化）、报告里的「空间」卡片、「C 盘空间去哪了」（报告后继续扫描）、主力浏览器判断和不用的浏览器减负；第二期「搬到其他盘」、第三期 B 类交给 Windows 自带清理
- 服务端和反馈控制台在私有仓库 iris-cloud（阿里云中国香港）
- [ ] beta.4 / beta.5 / beta.6 还没在真实 Windows 上完整跑过（"提权后窗口回到前台"、处理记录发送、C 盘空间扫描的速度和硬链接去重未验证）
- [ ] MVP 放行：10 台不同机器、规则和 AI 结论误判率的 95% 上限 < 10%

## 桌面版工程要点

- `src/Pcdoctor.Core`：研判流水线的 C# 移植，`tests/golden/` 保证与 Python 逐字节一致。**改了 judge/*.py、kb/seed.json 或提示词后，先跑 `python3 tools/make_golden.py` 再跑 `dotnet test`**
- 清理目录表 `kb/junk.json`：采集脚本按它测量（`-JunkCatalog`），操作库按它清理（嵌入资源）；要删的目录只能来自这张表。改它之后跑 `dotnet test`（`JunkCatalogTests` 检查每条路径不会指向用户文件夹）和 `pwsh tests/unit.ps1`
- `src/Pcdoctor.Ops`：操作库。所有对 Windows 的读写走 `IWinSystem`，测试用 `FakeWinSystem`；红线写进代码；操作记录带 `FindingKey` 和 `ReportId`，`HandledStatus` 据此给报告标"已处理"
- `src/Pcdoctor.App`：本机采集用 `-NoRedact`，原始报告只存在 `%LOCALAPPDATA%\pcdoctor`；研判一律对打码副本进行；朋友的报告只能看不能执行。管理员操作由同一个 exe 的 `--helper` 模式按需提权执行（方案按 SHA-256 校验，记录存在受保护目录），做完退出
- 界面 `src/Pcdoctor.App/web/`：无框架、无构建；浏览器直接打开时用 `bridge.js` 的模拟实现（数据 `web/demo/demo.json`）。改界面后用 Playwright 截图检查
- 打包：`python3 tools/build_app.py --version 版本 [--feedback-url … --latest-url …]` → `dist/Iris-版本-win-x64.zip`；没写服务器地址的构建完全不联网

## 夜班 / 无人值守工作规则（用户要求）

- 用户休息时可以自主推进进度表，每完成一项：测试 → 提交 → 推送 → 更新本文件和 `docs/roadmap.md`
- 主动查额度：`mcp__claude-code-remote__list_events`（本会话，`kinds: ["rate_limit_event"]`）里 `unifiedWindows.five_hour / seven_day` 的 `utilization`。5 小时 ≥ 80% 就停下、用 `send_later` 定在 `resetsAt` 之后 3 分钟继续；7 天 ≥ 75% 整个夜班停止
- 不改动用户第二天要跑的流程的行为；没把握的只记录不硬做；最后写夜班报告

## 产品方向（2026-10-24 确定）

- **定位**：学大厂电脑管家的**完整度和一站式体验**，做法反过来——不常驻、不弹窗、不捆绑、不吓唬，每个结论有证据，每一步可撤销。核心价值：**AI 把专业人士的判断过程讲给普通人听**；招牌是普通电脑管家不会告诉你的"历史遗留和信息差"（卸载残留、失效的 Defender 排除项、被放行的威胁、只剩登记的驱动）
- **真正的对手**是主打"干净可信"的同类工具，而不是"全家桶"式的电脑管家；对外文案不点名任何产品
- **副业节奏、纯开源**（AGPL-3.0）
- **第一批用户：作者自己和身边朋友**，即"家里和朋友的电脑问题都找我"的那类人；纯小白放到托管服务之后
- **界面**：学美国科技公司官网的**内容表达和气质**（克制、清楚、自信的文案、讲清楚"为什么"），但形态仍然是**软件**，不做成网页式布局。参考 Linear、Raycast、Arc 这类有科技感又好用的软件。同时要有**可靠感**
- **界面技术已定：WebView2**（2026-10-25）。审美参考（只学气质和交互，不做网页端）：网页 apple.com/iphone、spacex.com、vercel.com、tesla.com；软件 Claude、ChatGPT / Codex、Revolut、Trade Republic
- **查病毒交给 Defender，不重复造轮子**：只用我们的软件时，不修改用户的任何安全设置；可以发起 Defender 扫描、读取它的检测记录。报告里要说明"我们鼓励用好已有的强大工具"
- **问答不能卡住流程**：需要人操作的事（输入 Key、是否自动发送）全部在一开始问完；模型提的问题写进报告，用户有空再回答（`--confirm`），下次研判时生效。用户离开电脑，流程也能跑完

项目进度表和后续规划：`docs/roadmap.md`

## 产品理念传播（用户要求记住）

想办法把我们的理念传给用户：在报告和界面里穿插小教程和"信息差"，让用户知道为什么这么判断、自己以后怎么避坑。例如：
- 快速启动下点"关机"不等于重启，长期不重启会积累内存占用
- 微信 / QQ 的聊天文件默认存在 C 盘"文档"里，可以在设置里改到其他盘
- 装软件时取消勾选捆绑项；国产"全家桶"卸载后常留下计划任务和驱动
- 所谓"内存加速球""注册表清理"属于伪优化
- 正在运行的内核驱动强停会蓝屏（附录 A 的真实教训）

形式待定：报告里的"小知识"卡片、每条结论旁的"为什么"、短视频素材。以后写报告模板和界面文案时要落实。

## 技术决策（已定）

- **产品名 Iris**（仓库 `WilhelmWei/Iris`；代码里的项目名仍是 pcdoctor / Pcdoctor.*，exe 名 `Iris.exe`；数据目录仍是 `%LOCALAPPDATA%\pcdoctor`、`Program Files\pcdoctor-data`，不改以免丢数据）
- **内测（2026-10-25 用户确认）**：开场文案「电脑用久了，又热、又卡、又慢？试试 Iris。定位，动手，解决。」；内测用户须明确同意才发送去隐私反馈包（一键直达、不需要注册），不同意也能用；AI 收费提示原文「虽然 Iris 开源免费，但 AI 功能会调用第三方 AI 厂商的接口，可能由厂商收取费用（一次体检通常只要几分钱）。」；"阴阳大厂"不点名、只说做法，几句话轮换、不一次全出现；分发用阿里云 OSS 香港区 + 下载页（朋友没有网盘账号）；服务器阿里云香港区；内测说明要写"注意杀毒软件误报"
- **开源协议 AGPL-3.0**，纯开源，不做商业双重授权；收入只来自托管 AI 服务和打赏。以后申请 SignPath Foundation 免费代码签名，代码和仓库要满足其条款：不做主动扫描漏洞的功能；修改系统前必须确认、能撤销；有隐私政策（`PRIVACY.md`，改动时更新版本日期，程序会重新展示）；有卸载方式；构建只能由仓库内脚本完成（`tools/build_app.py`、`.github/workflows/release.yml`）；安装包不含闭源组件（WebView2 SDK 二进制保持现状，申请 SignPath 时主动说明，见 `docs/DEPENDENCIES.md`）
- 产品形态（2026-10-25 用户确认，取代原来的 WinUI 3 + 常驻服务）：**.NET 10 WinForms + WebView2 界面程序（普通权限，负责联网和隐私打码） + 按需 runas 启动的 `--helper` 辅助进程（管理员，做完就退出，不常驻、不装服务）**，通过交换目录里的文件通信，方案按 SHA-256 校验
- 每次修改系统（执行、撤销、清除数据）前由桌面程序弹原生确认框；同一次执行记成一批，可一键还原
- "发送到托管服务"默认关闭（设置里的开关）；托管服务的后端以后放独立仓库，不进客户端仓库
- 发布：单文件自包含 exe（免安装 zip）+ Inno Setup 安装程序（当前用户、自带卸载），GitHub Actions 打 tag 时构建并创建 Release 草稿
- LLM 接入：OpenAI 兼容接口；保留不联网的纯规则模式
- 不做杀毒引擎：扫描和隔离交给 Windows Defender；可疑文件只上传 SHA-256 到 VirusTotal（用户自己的 Key）
- 不选 Python 打包 exe（杀毒误报）；Tauri 是备选
- 不把开发者自己的 API Key 打进客户端；托管走中转服务器
- **缓存清理由用户勾选**：是否清理、清理哪些类别（图片 / 视频 / 文件 / 缓存与日志）、清理多久以前的（7 / 30 / 90 / 180 / 365 天），默认都不勾。采集阶段只统计分桶数字（`Measure-Folder -Breakdown`）。聊天软件里的图片、视频、文件属于用户数据（红线 6），只能移到回收站或引导用软件自带的清理功能，不能永久删除
- PowerShell 采集脚本是原型，数据结构和规则以后迁移到 .NET 采集器，JSON schema 尽量保持兼容

## 红线（操作库硬编码，LLM 不能绕过）

1. 正在运行的内核驱动**不 stop**，只标记删除，重启后再处理文件。实战中 `sc stop` 网银键盘过滤驱动 PECKP_x64.sys 直接蓝屏 0xCE
2. 删除驱动或服务前，先从所有设备类的 UpperFilters / LowerFilters 摘除
3. 不动微软签名驱动和 Windows 自带驱动（签名者为 "Microsoft Windows" 的 AMD / LSI / Mellanox 等存储驱动也属于自带）
4. 不动 Boot / System 启动且正在运行的第三方驱动，除非有专门卸载程序
5. 删除旧版驱动包不加 `/force`，让系统自动保护在用版本
6. 不关 Defender / 防火墙 / UAC；不清空回收站；不永久删除用户文件
7. 系统拒绝写入的位置不硬改
8. 改变用户习惯的操作（关休眠、禁用小组件）先说清代价；用户拒绝过关休眠（笔记本放包里耗电）
9. 用 Policy 改浏览器设置时，提前告知会出现"由你的组织管理"

执行结果一律以重新检测（verify）为准，不能只看命令有没有报错。回滚必须是独立、明确的按钮（实战中用户把"仅失败时使用"的回滚命令当成下一步运行了）。

## 编码约定（PowerShell 采集脚本）

- **兼容 Windows PowerShell 5.1**：不用三元运算符、`??`、`?.`、`&&`、`ForEach-Object -Parallel`
- **.ps1 文件必须保存为 UTF-8 with BOM**，否则 5.1 在中文系统上按 GBK 读取，中文变乱码。改完文件记得检查 BOM 还在
- 不用 `Get-Counter`（计数器路径在中文系统上会被本地化），改用 `Win32_PerfFormattedData_*` WMI 类
- 不解析 `sc`、`pnputil`、`powercfg` 等命令行工具的文字输出（会本地化），优先用 CIM / 注册表 / Cmdlet 对象。例外：`powercfg /batteryreport /xml` 输出 XML，元素名不随语言变化，可以用（写到临时文件，读完立即删除）
- 每个可能缺失或报错的 Cmdlet 用 `Safe '名称' { ... }` 包起来，失败写入 `tool.incomplete`，不拖垮整节
- 对 `Safe` 的结果做管道前先过滤 `$null`：`(Safe ...) | Where-Object { $_ } | ...`，否则 `$null` 会进管道生成空对象
- 有序字典里不要用 `count` / `keys` / `values` 这类和字典自身属性同名的键（已改用 `total`、`instances`）
- 路径拼接用字符串而不是 `Join-Path`（后者依赖当前系统存在该盘符）
- 只读：脚本唯一允许的写入是输出报告

## 常用命令

```bash
# 语法检查（需要 pwsh）
pwsh -NoProfile -c '$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile("collector/pcdoctor-collect.ps1",[ref]$null,[ref]$e); $e'
# 单元测试
pwsh -NoProfile -File tests/unit.ps1
python3 -m unittest discover -s tests
# 研判（只生成证据包，不调用模型）
python3 judge/run_judge.py 报告.json --dry-run
# 误判复核汇总
python3 judge/review.py stats 文件夹
# 桌面版（.NET 10 SDK；Linux 上也能编译和测试）
dotnet test pcdoctor.sln
python3 tools/make_golden.py      # 改了 Python 研判、知识库或提示词后重新生成一致性数据
python3 tools/build_app.py        # 打包桌面版
```

Windows 上运行（推荐一键）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1             # 拉取 → 采集 → 研判，带进度条和分步计时
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -DryRun     # 不调用模型
```

只运行采集：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\collector\pcdoctor-collect.ps1 *> "$env:USERPROFILE\Desktop\pcdoctor-run.log"   # 推荐，约 100 秒
powershell -NoProfile -ExecutionPolicy Bypass -File .\collector\pcdoctor-collect.ps1 -Quick   # 约 15 秒
```

## 下一步

1. 用反馈控制台看内测反馈：误判的改规则 / 知识库 / 提示词，漏报的补采集规则
2. beta.5 在真实 Windows 上完整跑一遍，重点看提权后窗口是否回到前台、「处理」页全流程
3. 候选版：LOLDrivers 哈希比对（先确认协议）、远控"无人值守"规则、"系统被优化工具改过"规则、健康分封顶、代码签名（SignPath）
