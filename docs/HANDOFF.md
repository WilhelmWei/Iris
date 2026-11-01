# 暂停开发交接说明（2026-11）

作者从 2026 年 11 月起专心学业，Iris 暂停开发。这份文档写给以后回来继续的人（作者自己，或帮作者写代码的 AI）：现在做到哪了、怎么跑起来、下一步从哪里开始。

## 1. 现在的状态

- **最新版本：1.0.0-beta.12**。免安装 zip 由 `tools/build_app.py` 构建，放在下载服务器上（地址见 README「下载」）。
- **已在真实电脑上多轮验证的**：命令行原型（采集、规则、知识库、AI / 纯规则研判），以及桌面版 beta.3 之前的主流程。
- **写完了、测试通过、但还没在真实 Windows 上完整跑过的**（beta.4 起）。每一项都有单元测试或截图核对，但下面这些行为只有真机才看得出来：
  - 提权后窗口回到前台
  - C 盘空间扫描的速度
  - 搬文件夹的速度、移动硬盘识别
  - 磁盘清理 `cleanmgr /sagerun` 在 Windows 11 上的表现
  - 卸载残留的误报率
  - 一键更新、回到上一版
  - 系统还原点
  - 开机记录、硬盘磨损的读取
  - 快捷方式修改和撤销
  - 窗口瞄准器（多显示器、高分屏、系统通知会不会压在遮罩上面）
  - 驱动指纹比对和可靠性查询的耗时
- **进度表**：`docs/roadmap.md`。「下一阶段」1–7 和「候选版」1–8 都已完成；「下一阶段」8「家人的电脑」只有设计稿。

## 2. 回来时先做的事（按顺序）

1. **真机跑一遍 beta.12**：用上面的清单，Windows 11 和 Windows 10 各一台最好。发现的问题优先修。
2. **看反馈**：iris-cloud 的反馈控制台（`console.bat`）。误判的改规则 / 知识库 / 提示词，漏报的补采集规则。
3. **作者要拍板的事**（AI 不能替作者决定）：
   - 代码签名申请：材料在 `docs/signing-application.md`，提交前要给 GitHub 开两步验证
   - 「家人的电脑」设计稿：`docs/design/family.md` 第 11 节有 5 个问题，点头后才写代码
   - 健康分封顶的上限：现在恶意痕迹 49、UAC 关着 69（`judge/scoring.py` 的 `CAPS`，C# 在 `src/Pcdoctor.Core/Scoring.cs`，两边要一起改）
   - 已知限制：采集在提权的辅助进程里运行，提权时换了别的管理员账户，读到的 HKCU 设置和桌面快捷方式是那个账户的。要把"跟用户有关的部分"挪到界面程序里采，改动较大
4. **关卡 1**：10 台不同电脑，规则和 AI 结论误判率的 95% 上限 < 10%（`judge/review.py stats`）。

## 3. 怎么跑起来

开发在 Linux 上也行：.NET 10 SDK、Python 3、PowerShell 7（只用来跑采集脚本的单元测试）。

```bash
dotnet test pcdoctor.sln                    # C# 研判、操作库（含 Python 一致性测试）
python3 -m unittest discover -s tests       # Python 研判
pwsh -NoProfile -File tests/unit.ps1        # 采集脚本里的纯函数
python3 tools/make_golden.py                # 改了 judge/*.py、kb/seed.json 或提示词后先跑这个
python3 tools/build_app.py --version 1.0.0-beta.13 --feedback-url … --latest-url …   # 打包
python3 tools/update_loldrivers.py          # 更新驱动指纹名单（LOLDrivers）
```

- 界面在 `src/Pcdoctor.App/web/`，浏览器直接打开 `index.html` 会用 `bridge.js` 的模拟数据，适合改界面、截图。
- 采集脚本 `collector/pcdoctor-collect.ps1` 必须兼容 Windows PowerShell 5.1，保存为 UTF-8 with BOM + CRLF（见 CLAUDE.md「编码约定」）。
- 两个服务器地址（反馈、版本信息）不在公开仓库里，在私有仓库 iris-cloud 的内部文档里。

## 4. 怎么发布一个版本

1. 改 `src/Pcdoctor.App/Pcdoctor.App.csproj` 的 `InformationalVersion`，打包。
2. 把 zip 放进私有仓库 iris-cloud 的 `releases/`（替换旧的 zip），加一份 `notes-<版本>.txt`（第一行是一句话标题，会显示在一键更新里），推送。
3. iris-cloud 的 Publish 工作流会上传安装包、更新 `latest.json` 和下载页；装了旧版的人打开 Iris 会看到更新提示。
4. 更新本仓库 README 里的下载链接和 SHA-256。

## 5. 服务器

反馈接收函数、下载存储都在阿里云（中国香港），代码和运维说明在私有仓库 iris-cloud。暂停期间不用动：没人发反馈时，函数不产生费用；存储里只有几个安装包，一个月几分钱。要彻底停掉，按 iris-cloud 的说明删除函数和存储空间。停掉以后，已经装了 Iris 的人检查更新、发送反馈会失败，但体检和处理照常能用。

## 6. 设计原则（回来时别忘了）

见 CLAUDE.md：不常驻、不弹窗、不捆绑、不吓唬；每个结论有证据，每一步可撤销；九条红线写在操作库的代码里；查病毒交给 Defender；采集只读、不联网；隐私政策改动要更新版本号，程序会重新展示。
