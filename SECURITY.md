# 安全问题报告 / Security Policy

Iris 会读取系统信息，并且能在你确认后修改系统设置（有些需要管理员权限），所以安全问题我们会优先处理。

## 怎么报告

**请不要在公开的 Issue 里贴漏洞细节。** 用 GitHub 的私下报告功能：

1. 打开本仓库的 **Security** 标签页
2. 点 **Report a vulnerability**，写清楚：影响哪个版本、怎么复现、可能造成什么后果

我们会在 7 天内回复，确认后尽快修复并发布新版本；修复发布前请不要公开细节。愿意的话，修复说明里会致谢你。

## 特别关心的问题

- 普通权限的程序能让 Iris 的管理员辅助进程（`Iris.exe --helper`）执行它不该执行的东西
- 绕过执行前的 Windows 确认框，或者绕过操作库的安全红线（见 README「安全红线」）
- 体检报告或内测反馈包里漏掉了打码，带出用户名、电脑名、邮箱等个人信息
- API Key 被写进明文文件、日志或反馈包

## 不在范围内

- 需要先拿到这台电脑管理员权限才能做到的事
- 杀毒软件对未签名程序的误报（见 README「杀毒软件或 SmartScreen 拦截」）

---

To report a security issue, please use GitHub's private vulnerability reporting (Security tab → Report a vulnerability). Do not open a public issue with details. We aim to reply within 7 days.
