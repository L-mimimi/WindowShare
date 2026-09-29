# WindowShare（窗享）

Windows 只读屏幕/窗口共享软件：**Host 端**捕获整个屏幕或指定窗口，H.264 编码后经局域网 TCP 或 WebRTC 发送；**Viewer 端**接收、解码、显示；**信令服务器**负责房间号、临时密码、设备上线与 SDP/ICE 交换。

> **本项目为只读共享**：不包含任何远程控制功能，不生成任何输入注入代码（无 SendInput / keybd_event / mouse_event / 反向输入通道），不包含 Android 端，不涉及权限提升。

## 功能一览

| # | 能力 | 实现位置 |
|---|------|----------|
| 1 | 整屏 / 指定窗口捕获 + 本机预览 | `Core/Capture`、`Host/MainWindow` |
| 2 | H.264 编码（硬件优先）+ 写本地文件验证 | `Core/Encoding`、Host 界面「编码验证」勾选 |
| 3 | 局域网 TCP 传输，Viewer 解码显示，实时状态/码率/帧率/延迟 | `Core/Network`、`Viewer` 状态栏 |
| 4 | SignalR 信令：房间号 + 临时密码 + 设备上线 | `Signaling`、`Core/Signaling` |
| 5 | WebRTC 广域网，STUN 穿透失败走 TURN，显示直连/中继 | `Core/WebRtc` |
| 6 | 安全与隐私：临时密码、设备白名单审批、AES-256-GCM 会话加密、DTLS-SRTP、共享悬浮提示、一键停止、仅共享指定窗口 | `Core/Security`、`Host/OverlayWindow`、`Host/ApprovalDialog` |
| 7 | 性能：硬件编码、GPU 零拷贝、动态码率、动态分辨率、断线重连 | `Core/Network/CongestionController`、`Core/Encoding/MfH264Encoder` |
| 8 | 打包为 Windows 安装程序 | `installer/WindowShare.iss`、`scripts/build.ps1` |

## 项目结构

```
WindowShare/
├─ src/
│  ├─ WindowShare.Core/       公共库：捕获 / 编码 / 解码 / 网络 / 协议 / 安全 / 会话
│  ├─ WindowShare.Host/       共享端 WPF（源选择、预览、悬浮提示、一键停止、LAN 服务、WebRTC 发送）
│  ├─ WindowShare.Viewer/     观看端 WPF（连接、解码显示、状态栏统计、WebRTC 回退）
│  └─ WindowShare.Signaling/  ASP.NET Core + SignalR 信令服务器
├─ tools/WindowShare.SmokeTest/  端到端冒烟测试（捕获/编码/传输/解码/信令/WebRTC）
├─ tests/WindowShare.Core.Tests/ 单元测试（协议/密码学/统计/拥塞控制）
├─ installer/WindowShare.iss     Inno Setup 安装脚本
├─ scripts/build.ps1             一键构建脚本
└─ docs/                         PROTOCOL.md（协议）/ DEPLOY.md（部署）/ TESTING.md（测试）
```

## 技术栈

- **.NET 8** + **WPF**（`net8.0-windows10.0.19041.0`）
- 捕获：**Windows Graphics Capture**（首选）→ **DXGI Desktop Duplication**（整屏回退）→ **GDI**（兜底）
- 编码：**Media Foundation H.264**（自动选择：注册硬件 MFT → Microsoft AVC DX12 Encoder → 软件 MFT），支持 D3D11 纹理零拷贝
- 解码：Media Foundation H.264 Decoder MFT
- 传输：局域网 **TCP 自定义二进制协议**；广域网 **WebRTC**（SIPSorcery，DTLS-SRTP）
- 信令：**ASP.NET Core + SignalR**
- NAT 穿透：**STUN** + **coturn TURN**
- 打包：**Inno Setup**

## 快速开始

### 0. 环境要求

- Windows 10 1903（10.0.18362）或更高（WGC 最低要求；推荐 Windows 11）
- .NET 8 SDK（构建）或 .NET 8 Desktop Runtime（运行已发布包）

```powershell
dotnet --list-sdks    # 需要 8.0.x
```

### 1. 构建与测试

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

脚本依次执行：Release 构建 → 单元测试 → 冒烟测试（会真实捕获本机屏幕约 30 秒）→ 发布 self-contained 产物到 `dist\publish`。

### 2. 局域网使用（无需服务器）

1. 在**被共享的机器**上运行 `WindowShare.Host.exe`
2. 选择共享源（显示器 / 指定窗口）→ 选画质 → 「开始共享」
3. 屏幕上出现红色悬浮提示条（可拖动、含「⏹ 停止」按钮）；界面显示**房间号**与**临时密码**
4. 在**观看的机器**上运行 `WindowShare.Viewer.exe`，选择「直连 IP」，填入 Host 的 IP 与密码 → 「连接」
5. Host 首次会弹出设备审批框（允许并记住 / 仅本次 / 拒绝），批准后即可观看

### 3. 跨网段使用（信令 + WebRTC）

```powershell
# 在一台服务器（或内网某台机器）上启动信令
dotnet run --project src\WindowShare.Signaling --urls http://0.0.0.0:5000
```

- Host：「信令服务器（房间号模式）」勾选，地址填 `http://<服务器>:5000`，开始共享
- Viewer：选「房间号」，填入房间号 + 密码 + 信令地址 → 连接（自动 LAN 直连 → 失败回退 WebRTC）
- 若 P2P 打洞失败，配置 TURN 环境变量（见 [docs/DEPLOY.md](docs/DEPLOY.md#4-turn-中继coturn)）

### 4. 安装包

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package
# 产出：installer\output\WindowShare-Setup-1.0.0.exe
```

需要 Inno Setup 6/7（`winget install JRSoftware.InnoSetup`）；未安装时脚本会自动通过
NuGet 包 `Tools.InnoSetup` 获取编译器，无需手工安装。安装包为 per-user 安装，无需管理员权限。

### 5. 便携版（解压即用，可放 U 盘）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Portable
# 产出：dist\portable\WindowShare-Portable-1.0.0.zip（含目录版）
```

解压后直接双击 `启动-共享端.bat` / `启动-观看端.bat` 即可，**无需安装、无需 .NET 运行时**。

| 特性 | 说明 |
|------|------|
| 数据隔离 | 所有数据存于程序目录 `data\`，不写系统 `%APPDATA%` |
| 随身携带 | 整个文件夹拷到 U 盘，换电脑即插即用，设备 ID 与白名单一起带走 |
| 只读回退 | 程序位置不可写（写保护 U 盘/只读目录）时自动回退 `%APPDATA%`，不会启动失败 |
| 自定义位置 | 可用环境变量显式指定：`set WINDOWSHARE_DATA_DIR=D:\MyData` |
| 界面提示 | Host 界面右上角显示「便携模式/安装模式」，📁 按钮直达数据目录 |

```
WindowShare-Portable-1.0.0/
├── WindowShare.Host.exe        共享端
├── WindowShare.Viewer.exe      观看端
├── data\                       便携数据（logs / config / recordings）
├── signaling\                  信令服务器（跨网段时才需要）
├── portable.marker             便携模式标记（删除即恢复安装模式）
├── 启动-共享端.bat             带使用提示的启动器
├── 启动-观看端.bat
├── 启动-信令服务器.bat
└── 使用说明.txt
```

> 便携模式判定优先级：`WINDOWSHARE_DATA_DIR` 环境变量 > 程序目录存在 `portable.marker` 或 `data\` 且可写 > `%APPDATA%\WindowShare`。

## 编码验证（离线验证编码链路）

Host 界面勾选「编码验证(写H.264)」后开始共享，共享期间生成的裸流文件位于：

```
%APPDATA%\WindowShare\recordings\share-<时间戳>.h264
```

用 ffprobe 或播放器验证：

```powershell
ffprobe -f h264 "%APPDATA%\WindowShare\recordings\share-*.h264"
# 期望输出：Video: h264, yuv420p, 1280x720, 30 fps
```

## 日志与数据位置

数据根目录按运行模式自动选择（Host 界面右上角会标注当前模式）：

| 模式 | 根目录 | 适用场景 |
|------|--------|----------|
| 便携模式 | 程序目录 `data\` | 便携版（存在 `portable.marker` 或 `data\`） |
| 安装模式 | `%APPDATA%\WindowShare` | 安装版或直接运行发布产物 |

| 内容 | 位置（相对于数据根目录） |
|------|--------------------------|
| 日志 | `logs\windowshare-<日期>.log`（保留 7 天） |
| 设备白名单 | `config\whitelist.json` |
| 本机设备 ID | `config\device.json` |
| 录制验证文件 | `recordings\` |

日志级别可用环境变量覆盖：`set WINDOWSHARE_LOG_LEVEL=Debug`
数据目录可用环境变量强制指定：`set WINDOWSHARE_DATA_DIR=D:\MyData`

## 安全设计要点

- **临时密码**：8 位随机（去混淆字符集，排除 0/O/1/I/L），随会话生成、会话结束即失效、不落盘
- **设备白名单**：首次连接必须由 Host 用户在弹窗中明确批准，可勾选「允许并记住」
- **认证**：PBKDF2-SHA256（10 万次迭代）+ HMAC 证明，且 HMAC 绑定双方 ECDH 公钥，防中间人替换密钥
- **会话加密**：LAN 路径 ECDH P-256 → HKDF → AES-256-GCM；WebRTC 路径强制 DTLS-SRTP
- **共享提示**：共享期间屏幕顶部常驻红色指示条；共享指定窗口时窗口周围显示红框
- **一键停止**：悬浮条按钮 / 主界面按钮 / 关闭窗口，三种方式立即断流并通知所有观看者
- **仅共享指定窗口**：窗口捕获只取该窗口内容，窗口之外的桌面不会被编码进视频流
- **信令**：服务器只中继 SDP/ICE，不接触媒体；密码哈希比较使用常量时间算法

## 已知限制

- 仅支持 Windows（Host 与 Viewer 均为 Windows）
- 局域网传输为 TCP（不做 UDP，优先保证可靠性与可运行性）
- 信令房间表为内存态，进程重启后房间失效（临时房间语义）
- DXGI Desktop Duplication 回退仅支持整屏（窗口捕获使用 WGC 或 GDI）
- WebRTC 媒体为单视频轨 H.264，无音频（只读画面共享定位）

## 文档

- [协议规范](docs/PROTOCOL.md)
- [部署指南（含 WSS / coturn）](docs/DEPLOY.md)
- [测试指南](docs/TESTING.md)
