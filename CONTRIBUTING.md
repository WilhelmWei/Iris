# 参与 Iris

谢谢你愿意帮忙！Iris 是业余时间做的开源项目，最有用的贡献按顺序是：

1. **报告判断错误**：Iris 把正常的东西判成问题，或者漏掉了真实的问题。在 Issue 里写清楚是什么软件 / 驱动 / 设置、Iris 怎么说的、实际情况是什么。**不要贴完整的体检报告**，里面有你电脑上的软件清单；只贴相关的那一条就够了。
2. **补充知识库**（`kb/seed.json`）：你确认过的常见软件组件（它是什么、属于哪个软件、要不要处理）。按名字识别的条目必须同时限定安装路径或数字签名，防止恶意程序冒名。
3. **代码和界面**：先开 Issue 说一下想改什么，避免白做。

## 开发

需要 .NET SDK 10 和 Python 3：

```bash
dotnet test pcdoctor.sln
python3 -m unittest discover -s tests
pwsh -NoProfile -File tests/unit.ps1      # 采集脚本（需要 PowerShell 7）
```

- 改了 `judge/*.py`、`kb/seed.json` 或 `prompts/` 之后，先运行 `python3 tools/make_golden.py` 重新生成一致性数据，再跑 `dotnet test`（C# 版的研判和 Python 版必须逐字节一致）
- 界面在 `src/Pcdoctor.App/web/`，用浏览器直接打开 `index.html` 就会用模拟数据，不需要 Windows
- 采集脚本必须兼容 Windows PowerShell 5.1，并保存为 UTF-8 with BOM（细节见 `CLAUDE.md`「编码约定」）

## 几条硬规矩

- **安全红线写在操作库的代码里**（README「安全红线」），任何改动都不能绕过它们；修改系统前必须让用户确认，并且能撤销
- 测试数据一律用虚构的，不要提交真实的体检报告、用户名、电脑名
- 不加主动扫描漏洞的功能；查病毒交给 Windows 自带的 Defender
- 提交即表示你同意以 [AGPL-3.0](LICENSE) 发布你的贡献
