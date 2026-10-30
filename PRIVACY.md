# 隐私政策 / Privacy Policy

**Iris · AI 电脑体检 · 版本 2026-10-30.1**

[中文](#中文) · [English](#english)

---

## 中文

### 一句话

体检数据保存在你的电脑上。只有两种情况会发出数据：你选择 **AI 研判** 时，把**去掉了个人信息的精简证据包**发给**你选定的模型服务**；**内测期间经你同意**，把**去隐私的反馈包**发给 Iris 作者的服务器（阿里云中国香港）。纯规则模式、不同意内测反馈时，除了检查新版本，程序不联系任何服务器。没有广告，也不收集与体检无关的数据。

### 1. 程序在你的电脑上读取什么

体检只读，不修改任何东西。读取的内容：

- 系统和硬件概况：Windows 版本、电脑型号、CPU、内存、硬盘型号和类型（SSD / 机械硬盘）、各磁盘空间、开机时长、电池状态
- 正在运行的程序、开机自启动、服务、计划任务、驱动的名称、路径、厂商和数字签名状态
- 已装软件的名称和厂商
- 安全设置的状态：Microsoft Defender（含排除项、被允许的威胁、近期检测记录）、防火墙是否开启、UAC、安全启动、内存完整性
- 网络设置：系统代理、hosts 文件中的自定义条目、DNS 服务器地址；浏览器扩展的名称和权限
- 浏览器：默认浏览器是哪个，每个浏览器最近一次浏览网页是多少天前（**只看历史记录文件的修改时间，不读浏览记录**），后台进程数和占用的内存，有没有开机自启动
- 几个常见文件夹和各类缓存（临时文件、浏览器缓存、错误报告等）的大小和按文件年龄的统计（只统计大小，**不读取文件内容**）
- 「C 盘空间去哪了」：报告出来后在后台继续统计系统盘上各文件夹的大小和最大的几个文件的名字（只统计大小，**不读取文件内容**，结果只保存在本机）
- 最近的蓝屏和程序崩溃记录

**不读取**：任何文档、照片、聊天记录、浏览记录、密码、Cookie；不采集本机 IP 地址、MAC 地址、Wi-Fi 名称。

### 2. 什么会被发送、发往哪里

| 研判方式 | 发送什么 | 发往哪里 |
| --- | --- | --- |
| **纯规则（默认）** | 不发送任何东西 | — |
| **AI 研判 · 自己的 API Key** | 精简证据包（见下） | 你在"设置"里选择的模型服务商（如 DeepSeek、Kimi、通义千问，或你填写的任何 OpenAI 兼容接口），使用你自己的 Key |
| **AI 研判 · Iris 托管服务**（尚未开放） | 精简证据包（见下） | Iris 托管服务器，由它转发给模型服务商。**默认关闭**，需要你在"设置"里主动打开"允许发送到托管服务"；关闭时程序只会使用你自己的 Key 或只做本地检查 |
| **内测反馈**（只在你同意之后） | 去隐私的反馈包（见下） | Iris 作者的服务器（阿里云函数计算和对象存储，**中国香港**）。第一次打开时会问你，同意或不同意都可以；不同意照常使用全部功能。随时可以在"设置 → 内测反馈"里关掉 |

**精简证据包**只包含需要判断的项目：被规则标记的程序、服务、驱动、计划任务等的名称、路径、厂商、签名状态，已装软件的名称和厂商，以及第 1 条里的系统概况和安全设置状态。发送前会把**用户名、计算机名、邮箱地址**替换成 `[用户]`、`[计算机名]`、`[邮箱]` 这样的占位符。

每次发出去的完整内容都保存在本机（数据文件夹里对应报告的 `evidence.json`），你可以随时打开查看。

模型服务商如何处理收到的数据（例如是否用于训练、保存多久），以服务商自己的隐私政策为准。不同服务商的政策不同，请在选择前查看。

**内测反馈包**用来找出 Iris 判断错的地方、认识更多常见软件。内容：

- 去隐私的体检结果：用户名、计算机名、邮箱换成占位符；再去掉系统代理地址、DNS 服务器地址、hosts 条目（只留数量）。其中包含软件、进程、服务、驱动的名称、版本、厂商和**安装位置**。如果你把文件夹起名为自己的名字，路径里会出现这个名字
- 研判结论和健康分；你对问题的回答、"判断不对 / 漏掉了"的标记，**包括你写的说明文字**
- 每一步的用时、程序版本、主题和研判方式，最近的错误记录（同样打码）；这次体检前有没有做 Defender 快速扫描、扫了多久、是否正常结束
- 「处理」页的记录：每一步做了哪种操作（例如"关闭开机自启动"）、对应报告里的哪条结论、结果（成功 / 没生效已恢复 / 没能执行 / 已撤销）和结果说明（同样打码）。**不含**操作对象的完整路径、注册表位置和改之前的备份。除了随体检反馈一起发送，在「处理」页执行或撤销完成后也会单独发送一次，用来了解哪些操作容易失败
- C 盘空间的分布：只有系统已知位置（例如 Windows、Program Files、下载文件夹、微信聊天文件）的大小，以及总共用了多少、看了多少个文件。**不含**你自己建的文件夹名和任何文件名
- 一个随机生成的安装编号：只用来把同一台电脑的反馈归到一起、防止被刷，和你的身份无关

**不包含**：API Key、文档、照片、聊天记录、浏览记录、密码、Cookie。服务器**不记录你的 IP 地址**；不需要注册。反馈只有作者能看，只用于改进 Iris，不出售、不用于广告。**内测结束后 90 天内删除**。

发送前会先存进本机的待发送文件夹（见第 4 条），网络不通时下次再发；在"设置 → 内测反馈 → 看看会发什么"里可以看到每一部分。

### 3. 其他网络访问

- **检查新版本**：程序启动时读取 Iris 下载服务器上的一个版本信息文件（`latest.json`，阿里云对象存储，中国香港），不发送任何体检数据。有新版本时只在概览页提示，不会自动下载或安装。
- 除此之外，程序没有统计、遥测、广告；错误记录只保存在本机，只有你同意内测反馈时才会随反馈包发送。
- 校验程序的数字签名时，Windows 自己可能会联网检查证书吊销列表，这是系统行为。
- 命令行版采集脚本有一个可选参数 `-CheckUpdates`，会让 Windows 更新服务查询待安装的更新（由 Windows 联系微软服务器）。桌面版不使用这个参数。

### 4. 数据保存在哪里、怎么删除

- `%LOCALAPPDATA%\pcdoctor`：体检报告（含未打码的原始采集数据，只在本机）、操作记录、设置、错误记录（`error.log`）、待发送的内测反馈包（`outbox`，发送成功即删除）。API Key 用 Windows 自带的 DPAPI 加密，只有你的 Windows 账户能解开。
- `C:\Program Files\pcdoctor-data`：需要管理员权限的操作的记录和备份（只有管理员能修改，防止被篡改）。只有你执行过这类操作时才会创建。
- 删除方法：在"设置"里点"清除所有数据"，或者卸载程序时选择同时删除数据，也可以直接删除上面两个文件夹。
- 已经发到服务器的内测反馈：在 GitHub 仓库提 Issue 或直接联系作者，告诉作者你的安装编号（在设置文件 `settings.json` 的 `installId` 里），作者会删除这台电脑的全部反馈。

### 5. 远程体检（给朋友用）

远程体检包只在朋友自己的电脑上生成一份已打码的报告文件，不联网、不自动发送。报告发不发、发给谁，由朋友自己决定。

### 6. 联系与变更

本政策如有变更，会在仓库中更新并在程序首次运行时再次展示。问题和建议请在 GitHub 仓库提 Issue：https://github.com/WilhelmWei/Iris

---

## English

### In short

Your check-up data stays on your computer. Data leaves it in only two cases: when you choose **AI analysis**, a **trimmed, de-identified evidence package** goes to **the model provider you selected**; and **during the beta, only with your consent**, a **de-identified feedback package** goes to the Iris author's server (Alibaba Cloud, **Hong Kong**). In rule-only mode and without beta consent, the app contacts no server except to check for a new version. No advertising, and nothing unrelated to the check-up is collected.

### 1. What the app reads on your computer

The check-up is read-only and changes nothing. It reads:

- System and hardware overview: Windows version, device model, CPU, memory, disk model and type (SSD / HDD), disk space, uptime, battery status
- Names, paths, vendors and digital-signature status of running programs, startup entries, services, scheduled tasks and drivers
- Names and publishers of installed software
- Status of security settings: Microsoft Defender (including exclusions, allowed threats and recent detections), whether the firewall is on, UAC, Secure Boot, Memory Integrity
- Network settings: system proxy, custom entries in the hosts file, DNS server addresses; names and permissions of browser extensions
- Browsers: which one is the default, how many days ago each browser was last used to browse (**only the modification time of the history file is checked; browsing history is never read**), background processes and memory, whether it starts with Windows
- Sizes of a few common folders and of caches (temporary files, browser caches, error reports, etc.) and statistics by file age (sizes only — **file contents are never read**)
- "Where did the C: drive space go": after the report is ready, the app keeps adding up folder sizes on the system drive in the background and notes the names of the largest files (sizes only — **file contents are never read**; the result stays on this computer)
- Recent blue-screen and application-crash records

**Never read**: documents, photos, chat history, browsing history, passwords or cookies. Local IP addresses, MAC addresses and Wi-Fi names are not collected.

### 2. What is sent and where

| Analysis mode | What is sent | Where it goes |
| --- | --- | --- |
| **Rule-only (default)** | Nothing | — |
| **AI · your own API key** | The trimmed evidence package (see below) | The model provider you choose in Settings (e.g. DeepSeek, Kimi, Qwen, or any OpenAI-compatible endpoint you enter), using your own key |
| **AI · Iris hosted service** (not yet available) | The trimmed evidence package (see below) | The Iris hosted server, which forwards it to a model provider. **Off by default.** You must turn on "Allow sending to the hosted service" in Settings; while it is off, the app only uses your own key or runs local checks only |
| **Beta feedback** (only with your consent) | The de-identified feedback package (see below) | The Iris author's server (Alibaba Cloud Function Compute and Object Storage, **Hong Kong**). You are asked on first launch; you may agree or decline, and declining does not limit any feature. You can turn it off any time in Settings → Beta feedback |

The **evidence package** contains only the items that need judgement: names, paths, vendors and signature status of the programs, services, drivers and scheduled tasks flagged by the rules; names and publishers of installed software; and the system overview and security-setting status from section 1. Before sending, **user name, computer name and e-mail addresses** are replaced with placeholders such as `[用户]` (user), `[计算机名]` (computer) and `[邮箱]` (e-mail).

The full content of every request is saved locally (`evidence.json` next to the report in the data folder) so you can inspect it at any time.

How a model provider handles the data it receives (for example whether it is used for training, and how long it is kept) is governed by that provider's own privacy policy. Policies differ between providers; please check before choosing one.

The **beta feedback package** is used to find where Iris judges wrongly and to recognise more common software. It contains:

- The de-identified check-up result: user name, computer name and e-mail replaced with placeholders; system proxy, DNS servers and hosts entries removed (only their counts are kept). It includes names, versions, vendors and **install locations** of software, processes, services and drivers. If you named a folder after yourself, that name will appear in paths
- Verdicts and the health score; your answers and "wrong / missed" marks, **including the notes you typed**
- Timings, app version, theme and analysis mode, recent error records (redacted the same way); whether a Defender quick scan ran before this check-up, how long it took and whether it finished normally
- The "处理" (fix) page log: which kind of operation each step was (e.g. "disable startup entry"), which finding in the report it belongs to, the result (done / not effective and restored / could not run / undone) and the result message (redacted the same way). It does **not** include the full path or registry location of the target, nor the backup taken before the change. Besides travelling with the check-up feedback, it is also sent on its own after you run or undo steps on that page, to learn which operations tend to fail
- How the C: drive space is used: only the sizes of well-known locations (e.g. Windows, Program Files, the Downloads folder, WeChat chat files) plus totals. It does **not** include the names of folders you created or any file names
- A randomly generated install ID, used only to group feedback from the same computer and prevent abuse; it is not linked to your identity

**Not included**: API key, documents, photos, chat history, browsing history, passwords, cookies. The server **does not log your IP address**; no account is needed. Only the author can read the feedback; it is used only to improve Iris and is never sold or used for advertising. **It is deleted within 90 days after the beta ends.**

Packages are first written to a local outbox (see section 4) and retried later if the network is down. Settings → Beta feedback → "What will be sent" shows every part.

### 3. Other network access

- **Update check**: on start-up the app reads a small version file (`latest.json`) from the Iris download server (Alibaba Cloud Object Storage, Hong Kong). No check-up data is sent. If a new version exists, a notice appears on the overview page; nothing is downloaded or installed automatically.
- Apart from that there is no analytics, telemetry or advertising; error records stay on this computer and are only sent inside a feedback package if you consented to beta feedback.
- When verifying digital signatures, Windows itself may go online to check certificate revocation lists; this is operating-system behaviour.
- The command-line collector has an optional `-CheckUpdates` switch that asks the Windows Update service to look for pending updates (Windows contacts Microsoft's servers). The desktop app does not use this switch.

### 4. Where data is stored and how to delete it

- `%LOCALAPPDATA%\pcdoctor`: check-up reports (including the raw, non-redacted collection data, kept on this computer only), the operation log, settings, error records (`error.log`) and beta feedback packages waiting to be sent (`outbox`, deleted once sent). The API key is encrypted with Windows DPAPI and can only be decrypted by your Windows account.
- `C:\Program Files\pcdoctor-data`: log entries and backups for operations that required administrator rights (only administrators can modify it, to prevent tampering). It is created only if you run such operations.
- To delete: click "Clear all data" in Settings, choose to remove data when uninstalling, or delete the two folders above.
- Beta feedback already on the server: open an issue on GitHub or contact the author with your install ID (`installId` in `settings.json`); the author will delete all feedback from that computer.

### 5. Remote check-up (for friends)

The remote check-up package only creates a redacted report file on your friend's own computer. It does not connect to the network or send anything; your friend decides whether and to whom to send the report.

### 6. Contact and changes

If this policy changes, it will be updated in the repository and shown again on the app's next first run. Questions and suggestions: please open an issue at https://github.com/WilhelmWei/Iris
