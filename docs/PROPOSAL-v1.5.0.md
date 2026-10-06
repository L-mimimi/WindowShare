# v1.5.0 提案：试运行评估 + 竞品对比 + 编码专项综合方案

> 基于 2026-10-06 对 v1.4.5 的实机试运行、全量代码审查与同类开源方案调研。
> 结论先行：**to1.5.0.md 的 FFmpeg 厂商硬件编码专项方向正确，作为 v1.5.0 主体执行**；同版捎带一组低风险快赢修复（含试运行中实锤的两个缺陷）；UDP/FEC 传输等大项排入 1.6+。

## 一、试运行报告（v1.4.5 便携版，本机实装）

环境：Windows 11 24H2 + RTX 5060，便携版 1.4.5（dist\portable），UIA 自动化驱动真实 GUI。

### 1.1 通过项（管线健康）

| 验证 | 结果 |
|------|------|
| `WindowShare.Host.exe --autotest` 无头自检 | PASS（5.35s：WGC 捕获 219 帧/4s、DX12 硬编零拷贝 104 帧、录制文件有效） |
| 全新进程直连（127.0.0.1:48750） | **4 秒内全链路就绪**：TCP→认证→AES-256-GCM→GOP 补发→MF 解码→视频上屏+音频播放 |
| 会话稳定性 | 3 分钟连续会话，断开时 Host 记录「发送 3244 帧，**丢帧 0**」 |
| 实时指标 | 解码/上屏 80–91 fps，网络单向延迟 ≈0 ms，GPU 22%，Host ~37% 一核、Viewer ~45% 一核 |
| 欠产出透明化 | Viewer 状态栏如实显示「1784 / 7300 kbps（编码器欠产出）」，数值随内容静止衰减至 ~1000 kbps |
| HEVC 探针崩溃隔离 | `--probe-hevc` 子进程按预期 AV 崩溃（0xC0000005），被隔离、缓存结果，FFmpeg 兜底可用（1.4.2 设计实机生效） |
| UIA 可自动化 | Host/Viewer 全部关键控件带 AutomationId，可脚本化驱动（本报告的连接流程即全程 UIA 驱动） |

### 1.2 实锤缺陷（按严重度）

**【D1·高】WASAPI loopback 设备失效无自愈，日志刷屏拖垮 Host 全进程**
- 复现：共享中音频设备失效（`GetNextPacketSize` 返回 `0x88890004 / AUDCLNT_E_DEVICE_INVALIDATED`）后，采集循环既不重建（对照 DXGI 路径的 ACCESS_LOST 重建）也不退避，以 **~300 行/秒** 持续刷 Warn。
- 实测后果：累计 **75,570 行**（日志膨胀至 43.9 万行）后，Host 入站处理全面失灵——房间号模式的审批请求无响应（观看端 60s 超时「加入房间失败」）、直连模式的认证质询无响应（「Host 未响应认证质询」），两者均无日志、无弹窗；最终 **AppHangB1**（UI 线程挂起）被系统终止。
- 因果佐证：杀掉进程全新启动后，同样流程一次连通、日志干净（40 行）。触发源（设备失效，如插拔耳机/默认设备切换）在日常使用中常见，该 bug 值得 P0 处理。

**【D2·中】Host 入站路径缺乏可观测性**
- D1 期间的两种入站失败（审批不达、质询不应）在 Host 日志中零痕迹；未认证连接上限（4）触达时疑似静默丢。建议：accept/auth 各环节加 Debug 日志 + 上限触达时明确拒绝并回包。

**【D3·低】次要观察**
- Host 工作集 ~600 MB（self-contained + WPF + 24MB GOP 缓存），Signaling ~60 MB——自包含分发换来体积与内存偏大，可考虑后续评估 trimming。
- 并发写日志仍有行交错（1.3.3 修复了共享打开，未修行原子性）。
- Viewer 每帧 CPU `WritePixels` 上屏，CPU 占用（45%）反超编码侧（37%）——d3d11va 硬解 + D3D 呈现的空间得到实测支持。

## 二、代码审查发现（静态，与试运行互相印证）

- `TcpClient.NoDelay` **全仓库从未设置**（`TcpFrameConnection` 两端）——Nagle 攒包对小帧（20ms 音频、控制帧）造成偶发延迟毛刺，一行修复。
- 热路径每帧分配：编码器输出（`MfVideoEncoder.cs:784`）、TCP 接收 header+payload（`TcpFrameConnection.cs:197,222`）、解码 BGRA 输出（`Nv12ToBgra`/`FfmpegVideoDecoder`）——高帧率下 GC 压力，可用 ArrayPool/复用缓冲收敛。
- **README 反复声称的「双次连接 UIA 回归」不在仓库**（全仓无 UIA/FlaUI 代码）——发版纪律实际依赖未入库的临时脚本，本次试运行脚本可作为其雏形。
- Viewer 无任何连接类 CLI 参数（Host 有 `--autotest`）——无人值守/自动化/测试均需 UI 驱动。
- 音频抖动缓冲目标 120ms（`AudioRenderer.cs:21`）为延迟地板；LAN 下可提供低延迟档（如 40ms）设置项。
- 安全注记（内网可接受，DEPLOY 文档应强调）：信令服务器持有 PBKDF2 密码哈希可离线穷举（31^8）；CORS 全开；信令默认明文 HTTP。
- WGC 静止桌面不出帧（TESTING.md 已载）、GDI 路径每帧分配全幅 byte[]、DXGI 路径忽略指针形状信息。

## 三、竞品对比与差距

| 维度 | WindowShare 1.4.5 | Sunshine+Moonlight | RustDesk | screego/Deskreen |
|------|-------------------|--------------------|----------|------------------|
| 捕获 | WGC→DXGI→GDI | DDA/WGC（Sunshine 双引擎） | WGC/DDA（Rust） | 浏览器/Electron |
| 编码 | MF/DX12（无码率控制） | NVENC/AMF/QSV，CBR+低延迟档 | 硬件 H264/H265+软编 | 浏览器端编码 |
| 解码 | MF+FFmpeg 软解 | **全平台硬解**（D3D11VA/NVDEC） | 硬解优先 | 浏览器解码 |
| 传输 | LAN TCP + WebRTC(SIPSorcery) | **UDP + RS FEC(GF(256)) + NACK(ENet)** | TCP+UDP 扩展+中继 | libwebrtc 全套（GCC/NACK/FEC） |
| 音频 | AAC-LC 128k 固定 | Opus 低延迟 | Opus | Opus（浏览器强制） |
| 发现 | 组播发现（AP 隔离即失效） | mDNS + 手动 IP 兜底 | ID/中继服务器 | 房间码 URL / 二维码 |
| 观看端 | 仅 Windows | 全平台客户端 | 全平台 + Web | **任意浏览器** |
| 延迟 | ~120ms+（抖动缓冲地板） | 局域网 5–20ms | 桌面控制级 | 抖动缓冲 bound |

**三大结构性差距**（依影响排序）：
1. **传输层**：TCP 队头阻塞——WiFi 丢包时帧卡顿而 Moonlight 依靠 UDP+前向纠错+NACK 掩盖（单帧分片+校验片，接收端 GF(256) 恢复）。这是 LAN-first 工具最大的架构差距，建议 1.6 专项（ENet 用 MIT 上游，勿引 Moonlight 的 GPL fork）。
2. **解码侧**：d3d11va 硬解未启用（IVideoDecoder 契约已就绪）；Viewer CPU 占用实测反超编码侧。
3. **观看端覆盖**：AAC 私有 PT 决定浏览器路不通。两条出路：**WebCodecs**（Edge/Chrome 于 Windows 可硬解 HEVC，WebSocket/WebTransport + 自定义查看页，保留现有 AAC/H264/HEVC 栈）或 **AAC→Opus 转码**走标准 WebRTC（SIPSorcery 自带 Opus）。前者与现有栈更兼容，建议为浏览器观看专项的首选路线。

**许可要点**：nvenc/amf/qsv 经 libavcodec dlopen 驱动库为 LGPL 兼容（现有 Sdcb LGPL 构建可直接用，零新增分发物）；**严禁引入 libx264/x265（GPL 传染）**；Sunshine/Moonlight/RustDesk 代码 GPL 系，只可借鉴协议与设计，不可抄代码。

## 四、v1.5.0 方案（主体 + 快赢）

### 主体：编码专项（= to1.5.0.md，工程量 2–3 天）

完整采纳 to1.5.0.md 的 Step 0–4：
- **Step 0 实弹验证（2026-10-06 已执行，通过）**——scratch 探针（`../scratch-probe/EncoderProbe`，AutoGen 7.1.1 结构体 + avcodec-61 带后缀导入，60 帧 1280x720 NV12 运动图，目标 3000 kbps）：

  | 候选 | open2 | 实测码率 | 结论 |
  |------|-------|---------|------|
  | **h264_nvenc** | 169ms | **3000 kbps（100%）** | **CBR 真实生效，根治「糊」的路径打通** |
  | hevc_nvenc | 110ms | 323 kbps（11%）* | 可用（正常出流 520fps）；*合成内容过于简单，码率贴合需真实桌面内容在 Part2d 复测 |
  | h264_mf（软） | 29ms | 192 kbps（6%） | 再次印证 MF 收件箱欠产出 |
  | hevc_mf（扩展软编） | — | send_frame 失败 0x80004005 | MF HEVC 编码路线死亡，与既有判断一致 |
  | h264/hevc_amf、qsv | — | 优雅失败（无对应硬件） | 工厂候选链按设计跳过 |

  二进制检查：avcodec-61.dll（Sdcb 7.1.0，88MB 全功能构建）含全部 nvenc/amf/qsv 注册名。
  **许可检查点**：该构建含 `libx264` 字符串（GPL 组件）——正式引入编码前核实 Sdcb 包许可变体，
  必要时换纯净 LGPL 构建源（项目自身只用 nvenc/amf/qsv/mf，不调用 x264 不构成衍生争议，但分发物需干净）。
  **Step 0 止损点未触发，Step 1–4 放行。**
- **Step 1** `IVideoEncoder` 抽象（MfVideoEncoder 纯提取，行为零变化）。
- **Step 2** `FfmpegVideoEncoder`：候选链 hevc_nvenc→hevc_amf→hevc_qsv / h264_nvenc→h264_amf→h264_qsv；tune=ull、preset=p4、rc=cbr、delay=0、max_b_frames=0、gop_size=2×fps；CPU NV12 喂入（复用 staging 回读）。
- **Step 3** `VideoEncoderFactory`：FFmpeg 厂商硬编 → MF 现链；Host 三档语义不变，显示实际后端（如 `hevc_nvenc (FFmpeg·硬件)`）。
- **Step 4** 验证闭环：冒烟新增 Part2d（实测/目标码率比 ≥50% 断言，NVENC CBR 预期 ≈100%）+ 工厂回退单测 + UIA 实启验收（H.264 自动档实测 ≥ 目标 80%）。

### 同版快赢清单（每项 ≤ 半天，低风险）

| # | 项 | 动机 | 验收 |
|---|-----|------|------|
| Q1 | **音频设备失效自愈**（D1）：捕获/渲染失效即重建管线（≤3 次退避），Warn 加 1/s 限速 | 试运行实锤：刷屏→入站失灵→AppHang | 模拟失效后 5s 内恢复共享、日志 ≤3 行 |
| Q2 | `TcpClient.NoDelay = true` | Nagle 攒包毛刺 | 冒烟全 PASS，音频帧到达间隔方差下降 |
| Q3 | 热路径缓冲复用（编码输出/接收/解码 BGRA） | GC 压力 | 4K@144 冒烟 Gen0/Gen1 分配速率下降 |
| Q4 | Viewer CLI：`--connect host:port --password xxx [--room xxx]` | 无人值守/自动化/UIA 回归 | CLI 直连成功出画面 |
| Q5 | UIA 回归脚本入库（`scripts/uia-regression.ps1`，本次试运行脚本为雏形） | 发版纪律落库 | 双端启动→直连→断开 全程脚本化 PASS |
| Q6 | 抖动缓冲目标延迟设置项（默认 120ms 不变，新增「低延迟 ~40ms」档） | 延迟地板 | 切档生效、无持续欠载 |
| Q7 | 入站可观测性（D2）：auth 各环节日志 + 未认证上限触达时明确回包 | 排障黑盒 | 人为触发上限有日志有回包 |

### 验收基线

**编码专项（Step 1–4）已于 2026-10-06 完成，验收达成**：
- Step 1 `IVideoEncoder` 抽象（MfVideoEncoder 纯提取，行为零变化）；
- Step 2 `FfmpegVideoEncoder`（nvenc→amf→qsv 候选链，tune=ull/preset=p4/rc=cbr/delay=0/1 帧 VBV/无 B 帧，CPU NV12 走管线 staging 回读）；
- Step 3 `VideoEncoderFactory`（FFmpeg 厂商硬编 → MF 现链；Host HEVC 探测改走工厂，`hevc_nvenc` 使 HEVC 会话在本机从「扩展软编 ~19fps」变为硬件全帧率）；
- Step 4 验证闭环：单测 207/207（含工厂选择链 4 项）＋冒烟 11/11（新增 Part2d：工厂选 `h264_nvenc`，实测/目标=71%，FFmpeg 软解回读 107/107 帧）＋AutoTest 生产路径（`h264_nvenc`，实测 2.33/2.5 Mbps=**93% ≥ 80% 达标**）＋双端实启 UIA 实测（Host 显示「h264_nvenc (硬件)」，Viewer 直连 84fps、≈0ms）。
- 快赢 Q2（`TcpClient.NoDelay`）同日落地于 `TcpFrameConnection` 构造（两端连接共用）。

**D2 定性修正（2026-10-06 双端实启复勘）**：此前记录的「Host 入站处理失灵/认证静默」实为**设备审批模态弹窗等待人工确认**——
未白名单设备直连时 Host 弹 `ApprovalDialog`，无人批准则 15s 认证超时（Viewer 显示「Host 未响应认证质询」有误导性，
实为「等待设备批准超时」）；自动重连在批准后即成功。与 D1 音频刷屏是两个独立问题。Q7 改进方向不变：
① Viewer 超时提示改为「等待 Host 批准设备」；② Host 在等待批准期间于日志与 UI 提示「有设备等待审批」；
③ `LanShareServer.cs:416` 的 `GetAwaiter().GetResult()` 阻塞式等审批超时（15s）与弹窗无超时不匹配——
弹窗应限时（如 60s）自动拒绝，避免认证线程久挂。

## 五、1.6+ 路线（排队，不进本版）

1. **UDP 媒体传输 + RS FEC + NACK**（对标 Moonlight；控制面可靠、媒体面不可靠+纠错；ENet MIT 上游）
2. **d3d11va 硬解 + D3D 呈现**（消 Viewer CPU 高点；IVideoDecoder 契约不变）
3. **浏览器免安装观看**（首选 WebCodecs：保留 AAC/H264/HEVC 栈，Edge/Chrome 硬解 HEVC；次选 AAC→Opus 转码标准 WebRTC）
4. mDNS 发现 + 手动 IP 兜底（AP 隔离无解，兜底才是主路径）
5. `SetBitrate` 动态重配（FFmpeg 编码器上生效，激活 CongestionController 码率梯子）
6. 信令加固：WSS 默认、CORS 收紧、密码哈希暴力穷举防护（服务端限速）

## 附录：试运行原始数据

- 时间线：15:47 AutoTest PASS → 15:48 Host 启动 → 15:49:35 本机信令就绪并开播（WYSU7A）→ 15:50:15 音频失效刷屏开始 → 15:52 / 15:58 两轮房间号模式接入超时（审批无响应）→ 16:00 直连「Host 未响应认证质询」→ 16:09:49 AppHangB1 进程终止 → 16:12 全新 Host+Viewer → 16:13:36 直连 **4 秒全链路连通**，会话稳定 → 16:16 优雅断开（3244 帧，丢帧 0）。
- 刷屏统计：75,570 行 `GetNextPacketSize 失败 (0x88890004)`，日志膨胀至 43.9 万行；留存于 `dist/portable/WindowShare-Portable-1.4.5/data/logs/flooded-2026-10-06.log`。
- 试运行 UIA 脚本（Q5 雏形）：`dist/portable/uia-*.ps1`（host-start / viewer-connect / direct / status / restart）。
- 编码器欠产出实测（本机，DX12 收件箱编码器）：AutoTest 807,936B/4s ≈ 1.6 Mbps（目标 2500 kbps，64%）；GUI 会话 1784→1000 kbps（目标 7300 kbps，14–24%）。
