# Iris · AI 电脑体检

> 电脑用久了，又热、又卡、又慢？试试 Iris。定位，动手，解决。

面向中文 Windows 用户的电脑体检工具。开源协议 [AGPL-3.0](LICENSE)。

固定代码只读地采集信息 → 规则预筛 → 知识库匹配 → AI（或纯规则）研判，讲清楚每个问题是什么、为什么这么判断 → 要不要处理由你决定 → 只从测试过的操作库里执行 → 重新检测确认生效，没生效自动恢复 → 每一步都能撤销。

**当前阶段：桌面版 1.0 内测（1.0.0-beta.5）**

## 和"电脑管家"有什么不一样

学大厂电脑管家的完整度和一站式体验，做法反过来：

- **不常驻**：做完就退出，没有托盘图标、后台服务和开机自启
- **不弹窗、不捆绑、不吓唬**：健康分由代码按规则计算，同一个原因只扣一次分，不制造焦虑
- **每个结论都有证据**：看到了什么、为什么这么判断、把握多大、可能错在哪，都写清楚
- **每一步都能撤销**：改之前备份，改之后重新检测；所有改动都记在「处理」页，可以一键还原
- **查病毒交给 Windows 自带的 Microsoft Defender**：Iris 可以发起 Defender 扫描、读取它的结果，但不修改任何安全设置
- **专找"历史遗留"**：卸载残留的计划任务和驱动登记、失效的 Defender 排除项、被放行过的威胁、长期不重启……这些是普通电脑管家不会告诉你的

## 功能

| | |
| --- | --- |
| 体检 | 13 个方面（资源、进程、自启动、服务、计划任务、驱动、软件、存储、电源与电池、安全设置、浏览器与网络、系统版本、稳定性），约 2 分钟；可以先让 Defender 快速扫描；实时显示查到了什么 |
| 研判 | AI 研判（DeepSeek、Kimi、通义千问或任意 OpenAI 兼容接口，用你自己的 Key）或纯规则研判（完全不联网）；AI 和规则交叉校验；知识库直接识别常见组件 |
| 报告 | 健康分和分项、最该先做的 3 件事、有没有中毒（分清有没有经过 Defender 扫描）、逐项结论（证据、把握、建议、风险、能否撤销）、小知识、和上次相比的变化 |
| 空间 | C 盘还剩多少、能直接清理多少（按内置的清理目录表测量：临时文件、各浏览器缓存、错误报告、崩溃转储、开发工具缓存等）、更大的空间在哪（聊天文件、下载、系统遗留）；「C 盘空间去哪了」在报告出来后继续在后台分析，跑完原地更新；伪优化（预取、注册表清理、内存加速）讲清楚为什么不做 |
| 处理 | 「一键加入建议的项」（默认只加低风险的，包括清理缓存；缓存直接删除，浏览器开着时自动跳过）→「处理」页逐项写清会改什么、代价、怎么撤销 → Windows 原生确认框 → 执行并重新检测 → 结果一目了然，报告里标出已处理的结论；处理清单自动保存，下次体检带过去 |
| 设置 | 三套主题（余烬 / 深空 / 米白）、减少动效、演示模式（遮住用户名和电脑名，方便截图）、存储空间清理、教程卡片、Ctrl+K 命令面板 |
| 内测反馈 | 经你同意，体检后自动发送去隐私的反馈包，不需要注册；「这条判断不对」「Iris 漏掉了什么」一键直达；「处理」页的操作结果（不含路径和备份）帮助找出容易失败的操作；随时可以关掉 |

## 下载（内测版）

- 下载：<https://iris-download-4rxytw.oss-cn-hongkong.aliyuncs.com/releases/Iris-1.0.0-beta.5-win-x64.zip>
- SHA-256：`0E7AFBD228772168BBBF98E2AB723B3D4E60496780074288FDED909C1125DE27`
- 解压到任意文件夹，双击 `Iris.exe`。需要 Windows 10 / 11（64 位）
- 给内测用户的说明：[docs/beta/内测说明.txt](docs/beta/内测说明.txt)（也在压缩包里）；可以发朋友圈的海报：[docs/beta/iris-beta4-poster.png](docs/beta/iris-beta4-poster.png)

正式版会通过 GitHub Releases 发布（免安装 zip + 安装程序，由 GitHub Actions 从源码构建）。

### 杀毒软件或 SmartScreen 拦截

Iris 还没有代码签名，又会请求管理员权限读取驱动和系统日志，部分杀毒软件可能拦截或提示风险；Windows 可能显示"Windows 已保护你的电脑"。

- SmartScreen：点「更多信息」→「仍要运行」
- 杀毒软件：选「信任 / 允许」，或者把 Iris 所在文件夹加进信任区
- 不放心的话，先用 `Get-FileHash .\文件名` 核对上面的 SHA-256

以后会申请免费的开源代码签名（SignPath Foundation），签名后这些提示会逐渐消失。

## 安全红线（写在操作库的代码里，AI 绕不过去）

1. 正在运行的内核驱动不停止，只标记删除，重启后再处理（实战中强停网银键盘驱动直接蓝屏）
2. 删除驱动或服务前，先从所有设备过滤器里摘除
3. 不动微软签名的驱动和 Windows 自带驱动
4. 不动开机就加载且正在运行的第三方驱动
5. 删除旧版驱动包不强制，让系统保护在用的版本
6. 不关 Defender / 防火墙 / UAC；不清空回收站；不永久删除用户文件（缓存只移到回收站，由你选类别和天数）
7. 系统拒绝写入的位置不硬改
8. 改变使用习惯的操作（关休眠、禁用小组件）先说清代价
9. 用策略改浏览器设置时，提前告知会出现"由你的组织管理"

执行结果一律以重新检测为准；撤销是独立、明确的按钮。管理员权限只在需要时临时申请：同一个 `Iris.exe` 以 `--helper` 模式运行，做完就退出，不装服务；它只执行哈希校验过的方案，记录和备份存在普通用户改不了的目录。

## 隐私

- 体检报告只保存在你的电脑上（`%LOCALAPPDATA%\pcdoctor`）
- 选择 AI 研判时，把去掉用户名、电脑名、邮箱的精简证据包发给**你选定**的模型服务；纯规则模式完全不联网
- 内测反馈：只有你同意才发送；去掉个人信息，再去掉代理、DNS、hosts；发到作者在阿里云中国香港的服务器；不记录 IP；内测结束后 90 天内删除
- API Key 只保存在这台电脑上，用 Windows 的 DPAPI 加密

详见 [PRIVACY.md](PRIVACY.md)（中英双语，程序首次运行时也会展示）。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `src/Pcdoctor.App` | Windows 桌面程序：.NET 10 WinForms 窗口 + WebView2 界面（`web/`，无框架、无构建）；带 `--helper` 时是按需提权的管理员辅助进程 |
| `src/Pcdoctor.Core` | 研判流水线（从 `judge/*.py` 移植，`tests/golden/` 保证两边结果逐字节一致）、反馈包去隐私、和上次的变化 |
| `src/Pcdoctor.Ops` | 操作库：处理方案、执行（前置检查 → 备份 → 执行 → 重新检测 → 没生效回滚）、撤销、操作记录、处理状态 |
| `collector/pcdoctor-collect.ps1` | 13 个方面的只读采集脚本 + 规则预筛（PowerShell 5.1，桌面程序内嵌同一份） |
| `judge/`、`prompts/`、`kb/` | Python 研判原型、AI 研判的系统提示、知识库种子表 |
| `remote/` | 命令行时代的远程体检包（给没装 Iris 的朋友只读采集） |
| `tools/` | `build_app.py`（打包桌面版）、`make_golden.py`（重新生成一致性数据）、`build_remote.py` 等 |
| `docs/` | [进度表](docs/roadmap.md)、[核心设计](docs/core-design.md)、[依赖和协议](docs/DEPENDENCIES.md)、`beta/`（内测说明、海报） |
| `CLAUDE.md` | 给 Claude Code 的项目说明（决策、红线、约定、进度） |
| `SECURITY.md`、`CONTRIBUTING.md` | 怎么报告安全问题、怎么参与 |

内测反馈的接收服务、下载服务和反馈控制台是作者自己运营的，不在这个仓库里（阿里云中国香港）。安装包不进仓库，只放在下载服务器和 GitHub Releases。

## 开发

需要 .NET SDK 10 和 Python 3（Linux 上也能编译和测试 Windows 程序）：

```bash
dotnet test pcdoctor.sln                      # C# 测试（研判一致性、操作库、处理状态……）
python3 -m unittest discover -s tests         # Python 测试
pwsh -NoProfile -File tests/unit.ps1          # 采集脚本测试
python3 tools/make_golden.py                  # 改了 judge/*.py、kb/seed.json 或提示词之后，先跑这个再跑 dotnet test
python3 tools/build_app.py --version 1.0.0    # 打包 dist/Iris-版本-win-x64.zip（单文件自包含 exe，约 42 MB）
```

- 界面：用浏览器直接打开 `src/Pcdoctor.App/web/index.html`，会自动换成模拟实现（`web/bridge.js`，数据是虚构的一台电脑 `web/demo/demo.json`）。改界面后用 Playwright 截图检查。
- 内测版的服务器地址在构建时写入：`build_app.py --feedback-url … --latest-url …`；没写入的构建完全不联系 Iris 的服务器。
- 同样的源码和 SDK 版本会构建出逐字节相同的 `Iris.exe`。

## 命令行版（原型，仍可用）

桌面版之前的原型，适合想看原始数据或批量处理朋友报告的人。

```powershell
# 一键：拉取 → 采集 → 研判（右键开始菜单 →「终端（管理员）」）
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1          # -DryRun 不调模型；-Offline 纯规则；-Scan 先 Defender 扫描；-SkipCollect 只重跑研判
# 只采集（约 1–2 分钟，报告在桌面；-Quick 约 15 秒）
powershell -NoProfile -ExecutionPolicy Bypass -File .\collector\pcdoctor-collect.ps1 *> "$env:USERPROFILE\Desktop\pcdoctor-run.log"
# 研判（只用标准库；Key 从环境变量 DEEPSEEK_API_KEY 读）
python judge\run_judge.py --dry-run           # 先看会发送什么
python judge\run_judge.py                     # 调用 deepseek-chat；--offline 纯规则；--model deepseek-reasoner
python judge\run_judge.py --confirm F046 --note "自己装的"   # 回答报告里的问题，下次不再问
python judge\review.py stats 文件夹           # 误判复核汇总（关卡：10 台机器、误判率 95% 上限 < 10%）
python tools\build_remote.py                  # 打包给朋友的远程体检包（零安装，只读采集）
```

`-ExecutionPolicy Bypass` 只对这一次运行生效；`-NoProfile` 和把输出写进日志，是为了避开个人配置文件和控制台窗口导致的卡顿。

## 卸载

- 先在 Iris 的「处理」页撤销想恢复的修改（卸载不会自动撤销已经做过的处理）
- 「设置 → 隐私与数据 → 清除所有数据」会删除下面两个文件夹（删除前会确认）：
  - `%LOCALAPPDATA%\pcdoctor`：体检报告、操作记录、设置
  - `C:\Program Files\pcdoctor-data`：需要管理员权限的操作的记录和备份（只有执行过这类操作才会有）
- 免安装版：删除解压出来的文件夹；安装版：「设置 → 应用」里卸载

<!--
## Code signing policy

（SignPath Foundation 批准后启用：删除这一段外面的 HTML 注释符号）

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Wilhelm](https://github.com/WilhelmWei)
- Approvers: [Wilhelm](https://github.com/WilhelmWei)

Every release is built from this repository's source code by GitHub Actions (`.github/workflows/release.yml`) and signed only after manual approval.

**Privacy policy**: This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. When the user chooses AI analysis, a de-identified evidence package is sent to the model provider the user selected; beta feedback is sent only with the user's explicit consent. See [PRIVACY.md](PRIVACY.md) for details.

本程序不会向其他联网系统传输任何信息，除非用户或安装、操作它的人明确要求。用户选择 AI 研判时，会把去掉个人信息的精简证据包发给用户选定的模型服务；内测反馈只在用户明确同意后发送。详见 [PRIVACY.md](PRIVACY.md)。
-->

## 协议 / License

本项目以 **GNU Affero General Public License v3.0（AGPL-3.0）** 发布，全文见 [LICENSE](LICENSE)。纯开源，不提供商业授权；收入只来自可选的托管 AI 服务和自愿打赏，客户端的全部功能免费。第三方依赖及其协议见 [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)。

Licensed under the GNU Affero General Public License v3.0 (AGPL-3.0). See [LICENSE](LICENSE).
