# WindowShare（窗享）

Windows 只读屏幕/窗口共享软件：**Host 端**捕获整个屏幕或指定窗口，H.264 编码后经局域网 TCP 或 WebRTC 发送，并可选同时共享**系统声音**（AAC-LC，与画面走同一条加密通道）；**Viewer 端**接收、解码、显示，并以音频播放时钟为主对齐音画；**信令服务器**负责房间号、临时密码、设备上线与 SDP/ICE 交换。

> **本项目为只读共享**：不包含任何远程控制功能，不生成任何输入注入代码（无 SendInput / keybd_event / mouse_event / 反向输入通道），不包含 Android 端，不涉及权限提升。共享的「系统声音」取自 WASAPI loopback（本机**正在播放**的内容），**不采集麦克风**，同样只有 Host → Viewer 一个方向。

## 下载（Windows x64）

最新版本 **v1.3.0** ｜ [全部发布版本](https://github.com/L-mimimi/WindowShare/releases)

| 类型 | 文件 | 大小 | 说明 |
|------|------|------|------|
| 安装版 | [WindowShare-Setup-1.3.0.exe](https://github.com/L-mimimi/WindowShare/releases/download/v1.3.0/WindowShare-Setup-1.3.0.exe) | 88.0 MB | Inno Setup per-user 安装，**无需管理员权限**；数据写入 `%APPDATA%\WindowShare` |
| 便携版 | [WindowShare-Portable-1.3.0.zip](https://github.com/L-mimimi/WindowShare/releases/download/v1.3.0/WindowShare-Portable-1.3.0.zip) | 122.3 MB | 解压即用，可放 U 盘；数据全部存于程序目录 `data\` |

两者均为 self-contained（win-x64），目标机器**无需预装 .NET 运行时**。系统要求：Windows 10 1903（10.0.18362）或更高。

校验（SHA256）：

```
62b8f13f49e5a998953aa4da6918f66efa96c6410bbc96a1acd7804b53355e8c  WindowShare-Setup-1.3.0.exe
567bd70ef6161e22ec79d7f91607d5c3981587fd85d6f8fdd89174e7fe8bb953  WindowShare-Portable-1.3.0.zip
```

## 1.3.0 更新

- **局域网自动发现（对标同类产品的设备发现）**：Host 开始共享后每 2 秒向组播组（`239.255.87.83:48751`，TTL=1）广播本机名称与共享端口；Viewer 打开即被动监听，界面自动列出「发现的共享端」，双击填入直连地址。announce 只含设备名与端口（与端口扫描等价的信息），**不含密码/房间号/设备 ID**，接入仍走完整认证握手；组播被网络策略禁掉时自动失效，手输 IP 直连不受影响。可在 Host 界面用「局域网发现」勾选框关闭。
- **WebRTC 通路补齐系统声音**：广域网/跨网段观看不再"只有画面没声音"。AAC-LC 复用 LAN 通路同一编码器输出，走自定义动态 PT 97（48kHz/立体声）随 DTLS-SRTP 加密传输；旧版观看端（v1.2）协商失败时 Host 自动降级为纯视频 offer 重试，双向兼容。注意：该音频是私有 PT，浏览器无法接收（本就只支持自家 Viewer）。
- **托盘与最小化到托盘**：新增应用图标（蓝底屏幕 + 投射波纹）；共享进行中点关闭按钮默认**隐藏到通知栏继续共享**（气泡提示一次），托盘菜单可打开主窗口 / 停止共享 / 退出；不希望该行为可在界面取消「关闭时隐藏到托盘」。
- **崩溃不再静默**：新增全局异常兜底——UI 线程异常记录后尝试继续运行并提示日志位置，后台线程致命异常与未观察任务异常全部落盘后再退出。
- **UI 设置持久化**：分辨率 / 帧率 / 共享声音 / 编码验证 / 信令地址 / 上次共享源（Host），连接模式 / IP / 端口 / 房间号 / 信令地址 / 声音开关（Viewer）重启后自动恢复；**密码仍为会话级临时凭据，绝不落盘**。
- **发送路径零分配 + GPU 资源复用**：每连接复用发送缓冲、AES-GCM 就地加密，消除每秒上百次的帧级大数组分配；WGC 捕获纹理环形池化、软件编码 staging/NV12 缓冲复用、预览三槽轮换；Host/Viewer 启用服务器 GC。整体降低编码/分发链路的 GC 停顿毛刺。
- **工程化**：新增 GitHub Actions（push/PR 跑构建 + 单测，推 `v*` 标签自动发布安装包 + 便携版 + SHA256）；版本号单源化到 `Directory.Build.props`；补 LICENSE（MIT）与 `.editorconfig`；编译警告清零；安装器中文语言文件随仓库分发（runner 预装的 Inno Setup 不自带）。
- **测试**：单测 158/158（新增设置持久化 8 项、发现协议 14 项）；冒烟测试 Part6 断言 WebRTC 音频回环、新增 Part8 局域网发现回环，八个部分全部 PASS。

## 1.2.0 更新


- **系统声音共享（Host → Viewer，单向）**：Host 端勾上「共享系统声音」后，用 WASAPI loopback（事件模式）采集**默认播放设备正在播的内容**（游戏 / 视频 / 音乐 / 系统提示音，**不是麦克风**），重混重采样为 48 kHz 立体声 int16，经 `Microsoft AAC Audio Encoder MFT` 编成 AAC-LC（ADTS 封装、128 kbps），与画面走**同一条 TCP 通道、同一把 AES-256-GCM 会话密钥**（新增消息类型 `AudioFrame = 22`）。Viewer 端由 `Microsoft AAC Audio Decoder MFT` 解回 PCM，经 WASAPI 共享模式渲染。没有播放设备 / 没有 AAC 编解码 MFT / 启动异常时一律静默降级为纯视频共享，不弹窗打断观看。
- **音画同步以音频时钟为主**：音频帧头带的是**采集时刻**时间戳（不是发送时刻，中间隔着编码与队列等待），Viewer 用它推进同步时钟；解码后的视频帧只入队，由独立上屏线程「等到点再画」。没有声音时（Host 未共享 / Viewer 取消勾选 / 本机无播放设备）自动退回「解码完立即上屏」，延迟更低。
- **Viewer 可随时静音**：顶部「播放系统声音」勾选框即时生效，取消勾选同时解除音画同步等待；状态栏显示「声音：播放中 XXms」的抖动缓冲深度。
- **分辨率切换不再花屏**：输出尺寸变化（源尺寸变化 / 拥塞降档）时重建 H.264 编码器，而不是只改 GPU 侧的目标尺寸——此前编码器的输入媒体类型仍声明旧宽高，喂进去的 NV12 与声明不符会花屏错位，观看端按旧尺寸建解码器同样解不出来。旧编码器改到后台释放，避开与 `ShareSession` 之间的锁环死锁。
- **测试**：新增 `AudioTests`（5 个测试类、44 项，合计 **136/136**）；冒烟测试新增 **Part7 系统声音**（编解码往返 / LAN 端到端 / loopback 探测），七个部分全部 PASS。实测 2 秒双声道正弦波（左 440 Hz、右 880 Hz）编出 91 个 ADTS 帧、解回 92160 帧（占输入 96.0%）、RMS 0.211、左右过零比 0.500；LAN 端到端 6 秒收到 280 帧 / 95 KB，解出 279 块 / 285696 样本（5952 ms），解码异常 0 次。

## 1.1.1 更新

- **观看者接入即出画面（不再黑屏等关键帧）**：Host 侧新增 GOP 缓存（`Core/Encoding/GopCache.cs`），保存自上一个 IDR 以来的全部编码帧；观看者认证通过的瞬间整段补发，因此它收到的第一帧必定是关键帧，画面当场可解。此前 `Microsoft AVC DX12 Encoder` 与软件 `H264 Encoder MFT` 对 `CODECAPI_AVEncVideoForceKeyFrame` 一律返回 `E_NOTIMPL`，「请求关键帧」根本不被认账，IDR 只按编码器内部 GOP 周期出现——静态桌面下实际帧率仅约 8fps，新观看者要黑屏干等好几秒。局域网直连与 WebRTC（房间号模式）两条通路都已覆盖，编码器与压缩率均不受影响。
- **信令连不上时的日志不再误导**：日志与状态栏统一改用「可照着排查」的文案，例如 `无法连接 http://localhost:5000（ConnectionRefused）：请确认信令服务器已启动、地址与端口正确｜共享照常进行，仅房间号模式不可用；启动信令服务器后点「重试」即可`。此前日志只有 `由于目标计算机积极拒绝，无法连接。`，看着像共享失败，实际共享一直在跑。
- **便携版说明补充**：写明信令服务器只负责房间号牵线、不承载画面数据，没启动也不影响局域网直连共享；本机试用填 `http://localhost:5000`。
- **测试**：新增 `GopCacheTests`（10 项，合计 **92/92**）；冒烟测试 Part4 增加「收到的第一帧就是 IDR」的确定性断言（连接前先等 `LanShareServer.CachedGopFrames > 0`）。

## 1.1.0 更新

- **信令服务器连不上不再拖垮共享**：注册信令房间移到「共享已启动」之后，服务器不可达 / 地址写错时只降级为「仅局域网直连」，状态栏给出可照着排查的原因（如 `ConnectionRefused`）并提供「重试」按钮；房间号被占用时自动换号重试，共享不中断，界面同步刷新新房间号与密码。
- **Viewer 房间号模式真正可用**：连接面板拆成「直连 IP / 房间号」两个互斥输入区，未选中的一侧整体置灰（此前左侧密码框是死 UI，填了也不生效）；房间号模式现在真正走信令接入（LAN 直连优先，失败自动回退 WebRTC）。
- **分辨率档位提升到 4K**：新增 4K(3840) / 2K(2560)，等比缩放、**不做上采样**（1080p 显示器选 4K 仍输出 1920×1080），主界面每秒显示真实输出尺寸。
- **帧率分档 24 / 30 / 60 / 90 / 120 / 144**：码率按「分辨率 × 帧率」自动推算并显示；捕获快于目标帧率时按帧间隔丢帧，避免观看端累积延迟。
- **4K / 高帧率的根因修复**：`Microsoft AVC DX12 Encoder` 默认锁在 H.264 Level 5.0，超出即 `E_INVALIDARG`；现按分辨率×帧率推导并在输出媒体类型上显式下发 `MF_MT_MPEG2_LEVEL`（4K30→5.1、4K60/1080p144→5.2），编码器不接受时回退到不带 level 的配置，硬件编码器全部失败时回退软件 `H264 Encoder MFT`。
- **观看者接入不再黑屏等待**：关键帧改为按时间触发（超过 2 秒没有 IDR 就强制一个，静态桌面下尤为关键）；解码器开启低延迟模式（`MF_LOW_LATENCY`），不再先攒 28 帧（约 1 秒）才吐出第一帧。

## 功能一览

| # | 能力 | 实现位置 |
|---|------|----------|
| 1 | 整屏 / 指定窗口捕获 + 本机预览 | `Core/Capture`、`Host/MainWindow` |
| 2 | H.264 编码（硬件优先）+ 写本地文件验证 | `Core/Encoding`、Host 界面「编码验证」勾选 |
| 3 | 局域网 TCP 传输，Viewer 解码显示，实时状态/码率/帧率/延迟 | `Core/Network`、`Viewer` 状态栏 |
| 4 | SignalR 信令：房间号 + 临时密码 + 设备上线 | `Signaling`、`Core/Signaling` |
| 5 | WebRTC 广域网（含系统声音，AAC 走动态 PT 97），STUN 穿透失败走 TURN，显示直连/中继 | `Core/WebRtc` |
| 6 | 安全与隐私：临时密码、设备白名单审批、AES-256-GCM 会话加密、DTLS-SRTP、共享悬浮提示、一键停止、仅共享指定窗口 | `Core/Security`、`Host/OverlayWindow`、`Host/ApprovalDialog` |
| 7 | 性能：硬件编码、GPU 零拷贝、动态码率、动态分辨率、断线重连 | `Core/Network/CongestionController`、`Core/Encoding/MfH264Encoder` |
| 8 | 系统声音共享：WASAPI loopback 采集 → AAC-LC(ADTS) → 同一条加密通道 → Viewer 解码播放，音画以音频时钟对齐 | `Core/Audio`、Host「共享系统声音」、Viewer「播放系统声音」 |
| 9 | 打包为 Windows 安装程序 | `installer/WindowShare.iss`、`scripts/build.ps1` |
| 10 | 局域网自动发现：Host 组播信标，Viewer 自动列出共享端双击直连（可关） | `Core/Network/LanDiscovery` |
| 11 | 托盘：共享中关闭窗口隐藏到通知栏继续共享，托盘菜单停止/退出 | `Host/TrayIcon` |
| 12 | UI 设置持久化与全局异常兜底、CI 自动发布 | `Core/Utils/JsonSettingsStore`、`Core/Logging/CrashReporter`、`.github/workflows` |

## 项目结构

```
WindowShare/
├─ src/
│  ├─ WindowShare.Core/       公共库：捕获 / 编码 / 解码 / 音频 / 网络 / 协议 / 安全 / 会话
│  ├─ WindowShare.Host/       共享端 WPF（源选择、预览、悬浮提示、一键停止、LAN 服务、WebRTC 发送）
│  ├─ WindowShare.Viewer/     观看端 WPF（连接、解码显示、状态栏统计、WebRTC 回退）
│  └─ WindowShare.Signaling/  ASP.NET Core + SignalR 信令服务器
├─ tools/WindowShare.SmokeTest/  端到端冒烟测试（捕获/编码/传输/解码/信令/WebRTC/系统声音）
├─ tests/WindowShare.Core.Tests/ 单元测试（协议/密码学/统计/拥塞控制/音频）
├─ installer/WindowShare.iss     Inno Setup 安装脚本
├─ scripts/build.ps1             一键构建脚本
└─ docs/                         PROTOCOL.md（协议）/ DEPLOY.md（部署）/ TESTING.md（测试）
```

## 技术栈

- **.NET 8** + **WPF**（`net8.0-windows10.0.19041.0`）
- 捕获：**Windows Graphics Capture**（首选）→ **DXGI Desktop Duplication**（整屏回退）→ **GDI**（兜底）
- 编码：**Media Foundation H.264**（自动选择：注册硬件 MFT → Microsoft AVC DX12 Encoder → 软件 MFT），支持 D3D11 纹理零拷贝
- 解码：Media Foundation H.264 Decoder MFT
- 音频：WASAPI loopback 采集（事件模式）→ 重混/重采样到 48 kHz 立体声 int16 → **Media Foundation AAC-LC 编解码**（ADTS 封装、128 kbps）→ WASAPI 共享模式渲染
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
2. 选择共享源（显示器 / 指定窗口）→ 选分辨率（最高 4K）与帧率（24–144）→ 要带声音就勾上「共享系统声音」→ 「开始共享」（该勾选**开始共享后改不生效**，需停止后重新开始）
3. 屏幕上出现红色悬浮提示条（可拖动、含「⏹ 停止」按钮）；界面显示**房间号**与**临时密码**
4. 在**观看的机器**上运行 `WindowShare.Viewer.exe`，选择「直连 IP」，填入 Host 的 IP 与密码 → 「连接」
5. Host 首次会弹出设备审批框（允许并记住 / 仅本次 / 拒绝），批准后即可观看
6. Viewer 顶部「播放系统声音」默认勾选，可随时取消（取消即静音，且画面不再等待音频时钟，延迟更低）；状态栏「声音：播放中 XXms」显示抖动缓冲深度

### 3. 跨网段使用（信令 + WebRTC）

```powershell
# 在一台服务器（或内网某台机器）上启动信令
dotnet run --project src\WindowShare.Signaling --urls http://0.0.0.0:5000

# 也可以直接跑发布产物 / 便携版脚本
#   dist\publish\signaling\WindowShare.Signaling.exe --urls http://0.0.0.0:5000
#   便携版：双击「启动-信令服务器.bat」
```

- 信令服务器**只服务「房间号模式」**，不承载画面数据；局域网直连不需要它，没启动也不影响已经开始的共享（Host 状态栏会给出原因与「重试」按钮）
- Host：「信令服务器（房间号模式）」勾选，地址填 `http://<服务器>:5000`，开始共享
- Viewer：选「房间号」，填入房间号 + 密码 + 信令地址 → 连接（自动 LAN 直连 → 失败回退 WebRTC）
- 若 P2P 打洞失败，配置 TURN 环境变量（见 [docs/DEPLOY.md](docs/DEPLOY.md#4-turn-中继coturn)）

### 4. 安装包

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package
# 产出：installer\output\WindowShare-Setup-1.2.0.exe
```

需要 Inno Setup 6/7（`winget install JRSoftware.InnoSetup`）；未安装时脚本会自动通过
NuGet 包 `Tools.InnoSetup` 获取编译器，无需手工安装。安装包为 per-user 安装，无需管理员权限。

### 5. 便携版（解压即用，可放 U 盘）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Portable
# 产出：dist\portable\WindowShare-Portable-1.2.0.zip（含目录版）
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
WindowShare-Portable-1.2.0/
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
- **会话加密**：LAN 路径 ECDH P-256 → HKDF → AES-256-GCM，覆盖 VideoFrame / AudioFrame / RawFrame / StatsInfo 负载；WebRTC 路径强制 DTLS-SRTP
- **强制加密**：Host 在质询中提供加密能力后，观看端必须完成加密协商，否则直接拒绝接入——认证通过的连接绝不回退明文
- **帧头绑定与防重放**（1.3+，自动协商）：24 字节帧头作为 AAD 绑定进 GCM 认证，篡改类型/序号/长度任一字节即断连；加密帧序号必须严格递增，重放即断开。对 1.2 老观看端自动维持旧加密格式
- **认证限流与连接上限**：同一 IP 认证失败 5 次（60 秒内）进入 5 分钟冷却期，冷却期内直接拒绝且不触发白名单弹窗；未认证并发连接 ≤4、已认证观看者 ≤16，超出直接断开
- **声音同样只读单向**：只采集 WASAPI loopback（本机正在播放的内容），不打开麦克风，也不存在 Viewer → Host 的音频回传通道
- **共享提示**：共享期间屏幕顶部常驻红色指示条；共享指定窗口时窗口周围显示红框
- **一键停止**：悬浮条按钮 / 主界面按钮 / 关闭窗口，三种方式立即断流并通知所有观看者
- **仅共享指定窗口**：窗口捕获只取该窗口内容，窗口之外的桌面不会被编码进视频流
- **信令**：服务器只中继 SDP/ICE，不接触媒体；密码哈希比较使用常量时间算法
- **监听范围可配置**：默认监听所有网卡；可在 `host-settings.json` 的 `bindAddress` 指定本机某个 IPv4，只在对应网卡监听
- **发现信标不含任何密钥**：组播 announce 只暴露「正在共享 + TCP 端口」，接入仍须通过完整的三步握手认证

## 已知限制

- 仅支持 Windows（Host 与 Viewer 均为 Windows）
- 局域网传输为 TCP（不做 UDP，优先保证可靠性与可运行性）
- 信令房间表为内存态，进程重启后房间失效（临时房间语义）
- DXGI Desktop Duplication 回退仅支持整屏（窗口捕获使用 WGC 或 GDI）
- WebRTC 音频走自定义动态 PT（AAC），仅 WindowShare 对 WindowShare 互通，浏览器无法接收（本就只支持自家 Viewer）
- 局域网发现依赖组播：AP 隔离 / 禁组播的网络下自动失效（手输 IP 直连不受影响）；首次监听时 Windows 防火墙可能弹窗，需允许（per-user 安装不预置防火墙规则）
- 系统声音依赖本机存在可用的播放设备与 AAC 编/解码 MFT；任一缺失即自动降级为纯视频共享（只记日志，不弹窗）
- 系统声音固定 48 kHz / 立体声 / AAC-LC 128 kbps，采集对象跟随「默认播放设备」，共享过程中切换默认设备需重新开始共享

## 文档

- [协议规范](docs/PROTOCOL.md)
- [部署指南（含 WSS / coturn）](docs/DEPLOY.md)
- [测试指南](docs/TESTING.md)
