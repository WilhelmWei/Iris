# SignPath Foundation 申请材料

这份文件是给作者提交申请用的：上半部分是提交清单，下半部分是可以直接粘贴的英文材料。

> 2026-11-01 准备。SignPath 的条款可能更新：提交前请在 signpath.org 上看一眼最新的申请页和条款（从这次准备材料的环境访问不到 signpath.org，以下按公开资料和其他开源项目的做法整理）。

## 提交清单（作者做）

1. **GitHub 开两步验证**：GitHub → Settings → Password and authentication → Two-factor authentication。SignPath 要求团队所有成员在代码仓库和 SignPath 上都开启。
2. **打开 signpath.org，找到开源项目申请（Apply）**，按下面"可以粘贴的材料"填写。表单里没有对应栏目的内容可以跳过；有"其他说明"一栏时，把 *Notes for the reviewers* 整段贴进去。
3. 提交后等审核邮件。审核可能会问问题，常见的是第三方组件和"程序会做什么"，答案都在下面。
4. **获批后**（到这一步把收到的信息发给 Claude，后面的配置由 Claude 完成）：
   - 在 SignPath 里按邮件指引建好组织和项目（项目名建议 `iris`），并开启 SignPath 账户的两步验证
   - 在 GitHub 仓库 Settings → Secrets and variables → Actions：Secrets 里加 `SIGNPATH_API_TOKEN`，Variables 里加 `SIGNPATH_ORGANIZATION_ID`
   - Claude 会：配置两个 artifact configuration（`app-zip`：签 zip 里的 `Iris.exe`；`installer`：签 `Iris-Setup-*.exe`）、启用 `release.yml` 里的两段签名步骤、打开 README 和 `docs/CODE_SIGNING.md` 里的"已获批"说明、在一键更新里加上"只替换带 Iris 签名的文件"

## 可以粘贴的材料（英文）

**Project name**: Iris (repository: pcdoctor)

**Repository**: https://github.com/WilhelmWei/Iris

**Homepage**: https://github.com/WilhelmWei/Iris

**License**: AGPL-3.0-only (OSI approved)

**Code signing policy**: https://github.com/WilhelmWei/Iris/blob/main/docs/CODE_SIGNING.md

**Privacy policy**: https://github.com/WilhelmWei/Iris/blob/main/PRIVACY.md

**Short description**:
Iris is an open-source PC check-up tool for Chinese-speaking Windows users. It reads the system (startup items, services, drivers, scheduled tasks, storage, security settings) in a read-only way, explains every finding with evidence in plain language (optionally with an LLM the user configures), and only changes the system after the user selects an item and confirms it in a native Windows dialog. Every change is recorded, verified by re-checking, and can be undone where technically possible.

**Why we need code signing**:
Iris asks for administrator rights to read drivers and system logs and to apply fixes the user confirmed. Unsigned, it triggers SmartScreen warnings and false positives from antivirus products, which makes it hard for non-technical users to trust a tool whose whole point is to be trustworthy. A signature tied to this repository's CI builds lets users verify that the binary they run was built from the public source code.

**Build process**:
All release binaries are built on GitHub-hosted Windows runners by `.github/workflows/release.yml`, using only the in-repository build script `tools/build_app.py` (self-contained .NET 10 single-file publish plus an Inno Setup installer). Unit and consistency tests run before every build. Signing will be requested from that workflow only, for tagged commits, and approved manually.

**Notes for the reviewers**:
- *Third-party components*: everything shipped is open source (.NET runtime and WinForms under MIT, fonts under SIL OFL). The one exception to point out is the Microsoft WebView2 SDK (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll`): it is distributed by Microsoft under the BSD-3-Clause license (included in the NuGet package) and its binaries are signed by Microsoft, but Microsoft does not publish their source. The WebView2 runtime itself is a Windows system component. We do not re-sign these files. Full list: https://github.com/WilhelmWei/Iris/blob/main/docs/DEPENDENCIES.md
- *What the program does to the system*: the check-up is read-only; changes require explicit selection and a native confirmation dialog; hard-coded rules forbid disabling Defender, the firewall or UAC, emptying the Recycle Bin, permanently deleting user files and stopping running kernel drivers. There is no resident process, service or driver; elevated work runs in a short-lived helper process.
- *Not a security tool*: Iris does not scan for vulnerabilities or exploit anything; malware scanning is delegated to Microsoft Defender.
- *Network*: no data leaves the computer unless the user asks for it (AI analysis with the user's own API key, or beta feedback with explicit consent); the app reads a version file to check for updates.
- *Team*: a single maintainer (Wilhelm, GitHub @WilhelmWei) who owns the repository, writes and reviews the code and approves releases.

## 已知可能被问到的问题

| 问题 | 回答 |
| --- | --- |
| 安装包里有没有闭源组件？ | 只有 WebView2 SDK 的三个文件：BSD-3-Clause 协议、微软签名、源码未公开。运行时本体是系统组件。见上面 *Third-party components* |
| 为什么要管理员权限？ | 读取驱动、系统日志，以及执行用户确认过的修改；只在需要时由一个短暂运行的辅助进程使用，做完就退出 |
| 会不会被当成"系统优化"类的潜在不需要软件（PUP）？ | 不捆绑、不常驻、不弹窗、不吓唬，修改前必须确认、能撤销；伪优化（注册表清理、内存加速）明确不做 |
| 项目还在维护吗？ | 是，提交记录和 Releases 可以看到持续更新 |
