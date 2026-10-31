# FileBridge

<img src="FileBridge.Desktop/Assets/logo.png" width="96" alt="FileBridge logo" />

Windows 与 macOS 之间的文件传输工具，支持暂停续传、并行分块和 SHA-256 校验。  
A desktop file transfer app for Windows and macOS, with pause/resume, parallel chunks and SHA-256 verification.

[中文](#中文) · [English](#english) · [所有下载 / All releases](https://github.com/samhu70/FileBridge/releases)

## 下载 / Downloads

| 平台 / Platform | 版本 / Version | 下载 / Download |
| --- | --- | --- |
| Windows x64 | 2.3.7 | [Windows ZIP](https://github.com/samhu70/FileBridge/releases/download/v2.3.7/FileBridge-windows-x64-2.3.7-framework-dependent.zip) |
| macOS Apple Silicon (arm64) | 2.3.7 | [macOS ZIP](https://github.com/samhu70/FileBridge/releases/download/v2.3.7/FileBridge-macOS-arm64-2.3.7-framework-dependent.zip) |
| SHA-256 | 2.3.7 | [SHA256SUMS](https://github.com/samhu70/FileBridge/releases/download/v2.3.7/SHA256SUMS-2.3.7.txt) |

**2.3.7 基于当前优化后的源码重新构建**，包含接收进度重置、停止错误提示保留、后台文件扫描和内部代码整理。发布包免安装、解压运行；`v2.3.7` 标签对应本次构建源码。  
**2.3.7 is rebuilt from the current optimized source**, including receive-progress reset, preserved shutdown errors, background file scanning and internal refactoring. Extract the archives to run the app. The `v2.3.7` tag identifies the source used for these builds.

## 中文

### 软件功能

- **跨平台传输**：在 Windows 与 macOS 之间发送文件或整个文件夹，支持中文文件名和目录。
- **无需应用账号**：FileBridge 本身无需注册；通过局域网、Tailscale 或其他已连通的 VPN 传输，不依赖 FileBridge 中转服务器。
- **连接码配对**：接收端生成连接码，发送端粘贴后即可发送。
- **暂停与断点续传**：暂停、网络中断或接收端重启后，可从已校验的连续位置恢复。接收端重启时需重新分享连接码。
- **并行分块**：发送端可选 1–10 路并发，默认 4 路；1 路为串行，接收端自动协商支持的上限。
- **完整性校验**：每块 4 MiB，分块和最终文件都校验 SHA-256；续传时也检查已有数据。
- **同名文件保护**：不覆盖已有的不同内容文件；内容一致的成品自动跳过。
- **HTTPS 连接**：随机令牌认证，连接码携带证书指纹，发送端验证指纹和证书有效期。
- **双语界面**：中文 / English 即时切换并记住选择，显示收发进度、速度、当前文件剩余时间与并行状态。

### 运行要求

下载包为 **framework-dependent 精简包**，不含 .NET 运行时。请先安装与系统架构匹配的：

- .NET 10 Runtime (`Microsoft.NETCore.App 10.0.x`)；
- ASP.NET Core 10 Runtime (`Microsoft.AspNetCore.App 10.0.x`)。

安装同架构的 .NET 10 SDK 也满足要求。仅安装 .NET Desktop Runtime 不代表已包含 ASP.NET Core。下载入口：[.NET 10 官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。

Windows 解压后打开 `FileBridge.exe`；Mac 解压后打开 `FileBridge.app`。请保留解压后的完整目录，不能只复制一个可执行文件。

### 快速开始

1. 确认双方电脑位于可互通的局域网，或已通过 Tailscale / VPN 连通。
2. 接收方打开「接收文件」，确认接收 IP、端口（默认 `8787`）和保存位置，点击「开始接收」。
3. 接收方点击「复制连接码」，只把连接码交给预期发送方。
4. 发送方选择文件或文件夹，粘贴完整连接码，选择并发路数，点击「开始发送」。
5. 需要中断时点击「暂停」；恢复时点击「继续发送」。以「校验通过」作为完成标准。

接收端重启后，使用原保存位置重新开始接收并分享新连接码；发送方重新选择原文件即可续传。不要删除保存目录下的 `.filebridge-parts`。

### 使用边界

- 自动优先选择 Tailscale 地址，其次为局域网私有 IPv4；只有 `127.0.0.1` 时仅能本机测试。
- 软件不提供 NAT 穿透或公网中转，不配置 VPN、防火墙或 Tailscale 权限；选中的 IP 和端口必须能从对端访问。
- 不传输空目录或符号链接；接收目录及其内部路径不能使用符号链接或目录联接。
- 传输期间不要修改源文件或临时文件。多个文件按顺序发送，并行针对单个文件的分块，不保证在所有网络上提速。
- 当前发布包没有 Apple Developer ID 公证或 Windows Authenticode 签名。

### 从当前 main 源码构建

使用 C# / .NET 10、ASP.NET Core Kestrel 和 Avalonia 12.1.4。

```sh
dotnet build FileBridge.sln -c Release
dotnet run --project FileBridge.Tests -c Release
dotnet run --project FileBridge.Desktop -c Release
```

可选测试：`dotnet run --project FileBridge.Tests -c Release -- --ui` 需要图形会话，会自动打开并关闭测试窗口；`--large` 会创建并传输 2 GiB 文件，验证暂停恢复和 SHA-256。

### 仓库访问

本仓库公开，便于浏览源码和直接下载 Release。公开可读不等于公开可写：访客可以下载、Fork 或提交 Pull Request，但不能直接推送或合并到本仓库；写权限仅授予仓库所有者及其明确授权的协作者。当前未添加协作者。

## English

### Features

- **Cross-platform transfers** between Windows and macOS, including entire folders and Unicode file names.
- **No FileBridge account required.** Transfer over a reachable LAN, Tailscale or another VPN, without a FileBridge relay server.
- **Connection-code pairing:** the receiver shares a code and the sender pastes it into the app.
- **Pause and resume** after a pause, network interruption or receiver restart, starting from the verified contiguous prefix. A receiver restart requires a new connection code.
- **Parallel chunks:** choose 1–10 concurrent requests, with a default of 4. One request disables parallel transfer; the receiver negotiates its supported limit.
- **SHA-256 verification** for each 4 MiB chunk, the resumed prefix and the completed file.
- **Existing-file protection:** different contents are never overwritten; already-completed files with matching contents are skipped.
- **HTTPS transport** with a random authentication token and an exact certificate fingerprint carried in the connection code.
- **Chinese and English UI** with a saved language preference, transfer progress, speed, time remaining for the current file and negotiated parallel mode.

### Requirements

The downloads are **framework-dependent** and do not include .NET. Install both the **.NET 10 Runtime** and **ASP.NET Core 10 Runtime**, matching your system architecture. A matching .NET 10 SDK also provides both. The .NET Desktop Runtime alone is not sufficient.

Download them from the [official .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

On Windows, extract the ZIP and open `FileBridge.exe`. On Mac, extract the ZIP and open `FileBridge.app`. Keep the entire extracted directory together.

### Quick start

1. Connect both computers over a reachable LAN, Tailscale or another VPN.
2. On the receiver, open **Receive files**, check the local IP, port (default `8787`) and destination folder, then select **Start receiving**.
3. Copy the connection code and share it only with the intended sender.
4. On the sender, choose files or a folder, paste the complete code, choose the stream count and select **Start sending**.
5. Select **Pause** when needed and **Continue sending** to resume. Wait for checksum verification before treating a file as complete.

After restarting the receiver, start receiving in the same destination folder and share its new code. Select the original source files again to resume. Keep the `.filebridge-parts` directory.

### Limits

- Address discovery prefers Tailscale, then a private LAN IPv4 address. `127.0.0.1` works only on the same computer.
- FileBridge does not provide NAT traversal or a public relay, and does not configure VPN access or firewalls. The selected IP and port must already be reachable.
- Empty folders and symbolic links are not transferred. Receive paths must not contain symbolic links or directory junctions.
- Do not modify source or partial files during a transfer. Files are sent sequentially; parallelism applies to chunks within a file and does not guarantee faster transfers on every network.
- The current packages are not notarized with Apple Developer ID or signed with Windows Authenticode.

### Build the current main branch

Requires the .NET 10 SDK. The app uses C#, ASP.NET Core Kestrel and Avalonia 12.1.4.

```sh
dotnet build FileBridge.sln -c Release
dotnet run --project FileBridge.Tests -c Release
dotnet run --project FileBridge.Desktop -c Release
```

Optional: add `-- --ui` to the test command for native desktop regression checks in a graphical session, or `-- --large` for an actual 2 GiB transfer with pause/resume and checksum verification.

### Repository layout

```text
FileBridge.Core/       Transfer protocol, validation, sender and receiver
FileBridge.Desktop/    Avalonia UI, localization and application assets
FileBridge.Tests/      Protocol, transfer and desktop regression checks
ThirdPartyNotices/     Third-party library notices
```

The Git repository contains source, required application assets and notices. Local planning documents, design history, build output and release binaries are excluded. Installable downloads are distributed through **GitHub Releases**.

### Repository access

This repository is public for source browsing and release downloads. Visitors may download, fork or submit a pull request, but cannot push or merge directly into this repository. Write access is limited to the owner and explicitly authorized collaborators. No collaborators have been added.

[Third-party notices](ThirdPartyNotices/README.txt)
