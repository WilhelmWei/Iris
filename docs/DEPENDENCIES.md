# 依赖与协议清单

本项目以 **AGPL-3.0** 发布（见根目录 `LICENSE`）。下面列出所有第三方依赖及其协议，检查日期 2026-10-25。

## 打进 Windows 安装包的组件

| 组件 | 版本 | 协议 | 源码公开 | 与 AGPL-3.0 | 说明 |
| --- | --- | --- | --- | --- | --- |
| .NET 运行时 + Windows Forms（自包含发布时打包） | 10.0 | MIT | 是（dotnet/runtime、dotnet/winforms） | 兼容 | |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | 是（dotnet/runtime） | 兼容 | 用 DPAPI 加密保存 API Key |
| System.ServiceProcess.ServiceController | 10.0.0 | MIT | 是（dotnet/runtime） | 兼容 | 读取服务状态 |
| 字体 Inter（拉丁字母 400 / 500 / 600，界面正文）、Barlow Semi Condensed（500 / 600，大号数字） | Inter 4.x、Barlow 1.4（取自 npm 包 @fontsource 5.1.0） | SIL Open Font License 1.1（`src/Pcdoctor.App/web/fonts/OFL-*.txt`） | 是（rsms/inter、jpt/barlow） | 兼容（OFL 允许随软件再分发，只要求字体不单独出售、保留协议） | 中文用系统自带的微软雅黑，不打包 |
| Microsoft.Web.WebView2（SDK：`Microsoft.Web.WebView2.Core.dll`、`.WinForms.dll`、原生 `WebView2Loader.dll`） | 1.0.3485.44 | BSD-3-Clause（包内 `LICENSE.txt`） | **否**：只发布二进制，微软没有公开这几个文件的源码 | 协议兼容；**是否算"闭源组件"不明确**，见下文 | 界面的承载控件 |

不打包、由系统提供：**Microsoft Edge WebView2 运行时**（Windows 11 自带，属于操作系统组件，AGPL-3.0 第 1 条"系统库"例外适用）、Windows PowerShell 5.1、任务计划程序等系统组件。

## 只在开发和测试时使用（不进安装包）

| 组件 | 协议 | 用途 |
| --- | --- | --- |
| xunit 2.9.3、xunit.runner.visualstudio | Apache-2.0 | C# 单元测试 |
| Microsoft.NET.Test.Sdk | MIT | C# 单元测试 |
| Python 3 标准库 | PSF | 研判脚本（`judge/`）和构建脚本，不依赖任何第三方包 |
| PowerShell 7（Linux 上跑 `tests/unit.ps1`） | MIT | 测试 |
| Inno Setup 6（GitHub Actions 里生成安装程序） | Inno Setup License（开源，允许商业和非商业使用） | 构建工具；生成的安装程序里包含 Inno Setup 的安装引导代码，其源码公开在 github.com/jrsoftware/issrc |
| GitHub Actions：actions/checkout、actions/setup-dotnet、actions/setup-python | MIT | CI |

## 数据和文档

| 内容 | 协议 |
| --- | --- |
| `kb/seed.json`（知识库种子） | AGPL-3.0（随仓库）。条目来源是作者实测和公开资料，没有复制第三方数据库 |
| 调研里提到的 LOLDrivers、LOLRMM | **没有**打包进仓库或安装包；如果以后要用，需先确认它们的协议 |

## 需要决定的事项

1. **WebView2 SDK 的二进制组件**：协议是 BSD-3-Clause（与 AGPL-3.0 兼容，允许再分发），但 `WebView2Loader.dll` 和两个托管封装 DLL 没有公开源码。
   - 它们是微软官方 SDK 的一部分，很多开源桌面程序也这样分发；运行时本体是系统组件。
   - 如果 SignPath 的审核要求"安装包里每个组件都必须有公开源码"，可能会被问到。
   - 可选做法：A. 保持现状，在 SignPath 申请里说明；B. 改用 WinForms 自带的 WebBrowser 控件（基于 IE，已淘汰，不建议）；C. 以后换成完全开源的界面方案（例如 Avalonia，界面需要重写）。
   - **已定（2026-10-25）：A**，保持现状，申请 SignPath 时主动说明。
