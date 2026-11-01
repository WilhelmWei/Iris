# Code signing policy / 代码签名政策

> **状态：申请中。** Iris 正在申请 SignPath Foundation 的免费开源代码签名。获批之前发布的版本没有签名；获批后，下面的政策适用于所有发布的 Windows 程序。
>
> **Status: applying.** Iris has applied for free open-source code signing from SignPath Foundation. Releases published before approval are unsigned; once approved, this policy applies to every released Windows binary.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).（获批后生效 / effective once approved）

## What gets signed / 签什么

- `Iris.exe` (the desktop app, also used in elevated `--helper` mode) and the installer `Iris-Setup-<version>.exe`
- Only binaries built from this repository by GitHub Actions ([`.github/workflows/release.yml`](../.github/workflows/release.yml)) with the in-repo build script ([`tools/build_app.py`](../tools/build_app.py)). Nothing built on a personal computer is ever signed.
- Third-party files are not re-signed. The Microsoft WebView2 SDK files are already signed by Microsoft (see [DEPENDENCIES.md](DEPENDENCIES.md)).

只签由本仓库源码、经 GitHub Actions 和仓库里的构建脚本构建出来的 `Iris.exe` 和安装程序；个人电脑上构建的文件一律不签；第三方文件不重新签名。

## Team roles / 团队角色

| Role | Members |
| --- | --- |
| Committers and reviewers | [Wilhelm](https://github.com/WilhelmWei) |
| Approvers | [Wilhelm](https://github.com/WilhelmWei) |

All team members use multi-factor authentication for GitHub and SignPath. Every signing request is approved manually by an approver after checking that the build comes from a tagged commit in this repository.

所有成员的 GitHub 和 SignPath 账户都开启两步验证；每次签名都由批准人确认构建来自本仓库带 tag 的提交后，手动批准。

## What the program does to your computer / 程序会对电脑做什么

- The check-up is **read-only**.
- Iris only changes the system when the user selects an item, reads what will change, and confirms in a native Windows dialog. Every change is recorded and, where technically possible, can be undone with one click; results are verified by re-checking.
- Hard-coded safety rules: it never disables Microsoft Defender, the firewall or UAC, never empties the Recycle Bin, never permanently deletes user files, and never stops a running kernel driver.
- Iris has no resident process or service; elevated operations run in a short-lived helper process that exits when done.
- Iris does not scan for vulnerabilities and is not a security or penetration-testing tool. Malware scanning is left to Microsoft Defender.

体检只读；只有用户勾选、看过会改什么、并在 Windows 原生确认框里确认后才修改系统；每一步都有记录，能撤销的都能一键撤销；不关安全防护、不清空回收站、不永久删除用户文件、不强停正在运行的驱动；不常驻；不做漏洞扫描。

## Privacy policy / 隐私政策

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. When the user chooses AI analysis, a de-identified evidence package is sent to the model provider the user selected; beta feedback is sent only with the user's explicit consent; the program checks a version file for updates. Full policy: [PRIVACY.md](../PRIVACY.md).

本程序不会向其他联网系统传输任何信息，除非用户或安装、操作它的人明确要求。用户选择 AI 研判时，会把去掉个人信息的精简证据包发给用户选定的模型服务；内测反馈只在用户明确同意后发送；程序会读取一个版本信息文件检查更新。详见 [PRIVACY.md](../PRIVACY.md)。

## Uninstall / 卸载

See [README → 卸载](../README.md#卸载). The installer version can be removed from Windows Settings → Apps; the portable version by deleting its folder.
