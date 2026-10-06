# WindowShare 路线图

> 记录已确认的改进方向、诊断结论与设计草案。发布节奏：小改进随 1.3.x，质变项单独立版。
> 最后更新：v1.5.2 试运行审计（见 [AUDIT-v1.5.2.md](AUDIT-v1.5.2.md)）——实锤 6 项运行时缺陷 +
> 10 项安全/鲁棒性发现，已按 P0/P1/P2 编入本文件末尾的「当前优先级队列」。

## 相关文档

| 文档 | 内容 |
|------|------|
| [HANDOFF.md](HANDOFF.md) | **交接说明**（未提交改动 / 未解决问题 / 踩过的坑 / 建议顺序）——接手先看这份 |
| [AUDIT-v1.5.2.md](AUDIT-v1.5.2.md) | **v1.5.2 试运行审计与修复排期**（实测数据 / A1–A6 缺陷 / S1–S10 安全发现 / 竞品对比 / 复现步骤） |
| [DEVLOG.md](DEVLOG.md) | **开发工作日志**（每次会话做了什么、验证到什么、哪些判断被推翻） |
| [PROPOSAL-v1.5.0.md](PROPOSAL-v1.5.0.md) | v1.5.0 提案（编码专项 + 快赢清单，已落地） |
| [PROTOCOL.md](PROTOCOL.md) | 协议规范 |
| [TESTING.md](TESTING.md) | 测试指南 |
| [DEPLOY.md](DEPLOY.md) | 部署指南 |

## 背景与已确认事实

### v1.3.2 已落地的清晰度改进
- 码率规划 bpp 0.085 → 0.14（1080p30 ≈ 8.7 Mbps，4K60 ≈ 49 Mbps，上限 120 Mbps）
- H.264 编码优先启用 High Profile（CABAC），编码器拒绝时自动回退
- 实测局域网观看码率仍只有 ~1.6 Mbps → 见下面的诊断

### 诊断记录（2026-10-04 修正，基于真实会话日志复勘）
- ~~2026-10-03 初判：双端 Wi-Fi 有效吞吐 ~2 Mbps，码率被降档钉在 20% 最低档~~
  **该结论不成立**，已推翻。
- **真实会话证据（v1.3.2，1920x1080@144fps、目标 30500 kbps、High Profile 生效）**：
  三次观看（45s / 108s / 32s）全程**零 [Congestion] 降档、零丢帧、稳定 55–56 fps 发送**——
  网络一路顺畅，降档机制从未触发；Viewer 实测码率 ~1.6 Mbps，仅为目标的 5%。
- **纯编码环节旁证（无网络参与，loopback / 直写文件）**：
  - 冒烟 Part2：合成运动图像 1280x720@30，目标 3000 kbps，实编 230 kbps（8%）
  - 冒烟 Part4：loopback 端到端，目标 2500 kbps，实编 297 kbps
- **根因链**：本机唯一硬件编码器 "Microsoft AVC DX12 Encoder" 拒绝所有 CodecAPI 调参
  （CBR / 码率 / 低延迟 / GOP / ForceKeyFrame 全部 E_NOTIMPL）→ `MF_MT_AVG_BITRATE`
  只是被媒体类型接受、不构成硬约束 → 默认速率控制对桌面内容严重欠产出 → 糊。
- **闭环（2026-10-06，v1.5.0）**：编码专项落地后根因消除——h264_nvenc 真实 CBR 下 AutoTest 实测码率 93%
  （对比同测试 DX12 收件箱编码器的 64% 与历史 GUI 会话 5–24%），欠产出问题闭环。
- **次因**：帧率错配——配置 144 fps 但 WGC 实际捕获仅 ~56 fps（内容脏区更新率决定），
  每帧预算被摊薄。
- **待验证**：① ~~CodecAPI 攻坚结果~~ **已验证（2026-10-04）**：下发时机矩阵（类型配置后/
  流启动后）全部 E_NOTIMPL（0x80004001），IsAvailable 同样未实现——速率控制不可调，
  已按决策转入 HEVC 1.4.0；② 用户侧：共享时播放高动态视频/快速滚动，看码率是否上抬——
  区分「内容简单所以欠产出」与「编码器封顶」。

## 码率空间参考（H.264，bpp → 1080p30 码率）

| bpp | 1080p30 | 评价 |
|-----|---------|------|
| 0.085（≤1.3.1） | 5.3 Mbps | 文本边缘明显偏软 |
| 0.14（1.3.2） | 8.7 Mbps | 规划合理，但受编码器欠产出限制实际达不到 |
| 0.25（候选） | 15.6 Mbps | 文本锐利，接近 H.264 性价比上限 |
| 0.35 | 21.9 Mbps | 边际递减明显 |

对照：Sunshine/Moonlight 局域网用户常跑 50–100 Mbps（1080p60）。有线千兆无压力。
**注意**：在编码器学会"花掉预算"之前，提高规划 bpp 无意义（见诊断记录）。

## v1.3.3（2026-10-04）：画质根因包

- [x] **CodecAPI 攻坚**：HRESULT 逐项日志 + 下发时机矩阵（类型配置后/流启动后）+
  IsAvailable 预检 + CBR→质量模式降级尝试。**结论：DX12 收件箱编码器全数 E_NOTIMPL，
  速率控制不可调**（诊断能力保留，HEVC MFT 接入时复用）
- [x] **并发日志修复**：多进程写同一日志文件被默认共享模式拒绝、静默丢失——
  合并入口同机并行 Host+Viewer 时 Viewer 日志全部丢失，实启 UI 验证时抓到
- [x] **帧率错配修正**：实际捕获 fps 统计，显著低于配置档（<70%）时告警一次并显示
- [x] **码率透明化**：AuthResult/StatsInfo 增目标码率/fps/降档状态；Viewer 状态栏
  「实测/目标 kbps」+（网络降档）/（编码器欠产出）提示；Host 状态栏与悬浮条显示
  观看者数 · 实时 Mbps · 发送 fps
- [x] 日志噪音清理（停止时 OCE 降为 Debug、信令就绪日志去重 + null 接管不再丢进程句柄）
- [ ] ~~若 CodecAPI 全数 E_NOTIMPL → 直接立项 HEVC 1.4.0~~ → **已立项，见下方中期节**

## v1.5.0（立项，见 docs/PROPOSAL-v1.5.0.md 与 to1.5.0.md）

编码专项（FFmpeg 厂商硬编 NVENC/AMF/QSV，对齐 to1.5.0.md Step 0–4）为主体，同版捎带快赢清单。

**Step 0 已通过（2026-10-06，scratch 探针实测，工程留存 `../scratch-probe/EncoderProbe`）**：avcodec-61.dll
（Sdcb 7.1.0）含全部 nvenc/amf/qsv 注册名；h264_nvenc 码率贴合 100%（open2 169ms）；hevc_mf send_frame 即败
（MF HEVC 编码死亡再确认）；amf/qsv 无硬件优雅失败。许可检查点：Sdcb 构建含 libx264 字符串（GPL），
正式分发前核实包许可变体。

**Step 1–4 已完成（2026-10-06），编码专项闭环**：`IVideoEncoder` 抽象（MF 纯提取）→ `FfmpegVideoEncoder`
（nvenc→amf→qsv，tune=ull/p4/cbr/delay=0/1帧 VBV/无 B 帧）→ `VideoEncoderFactory`（FFmpeg→MF；Host HEVC 探测
同走工厂，本机 HEVC 会话从扩展软编 ~19fps 变为 hevc_nvenc 硬件全帧率）→ 验证：单测 207/207（工厂链 4 项）+
冒烟 11/11（新 Part2d：h264_nvenc 实测/目标 71%、FFmpeg 软解回读 107/107）+ AutoTest 生产路径 h264_nvenc
实测 93%（≥80% 达标）+ 双端 UIA 实启（Viewer 84fps/≈0ms）。快赢 Q2（NoDelay）同日落地。
另：D2「认证静默」复勘定性修正——是设备审批模态弹窗等人工确认、Viewer 超时提示语误导（详见 PROPOSAL 验收节），
Q7 改进方向：提示语纠正 + 弹窗限时自动拒绝（当前 `GetAwaiter().GetResult()` 与弹窗无超时不匹配）。
遗留观察：hevc_nvenc 对合成低复杂度内容码率仅 5%（CBR 不填充），真实桌面内容的码率贴合需在 1.5.0 发布前实测确认。

UI 修复（2026-10-06）：Host「共享系统声音」「编码验证(写H.264)」复选框在第 1 行因左右泊总宽超窗（~1080 > 980 逻辑像素）
被 DockPanel 裁剪，用户不可见——移至第 2 行右侧，UIA 边界矩形验证完整可见可点。

2026-10-06 实机试运行（UIA 驱动全 GUI 流程）新实锤两个缺陷，纳入 Q1/Q2 优先处理：

- **【实锤】WASAPI loopback 设备失效（0x88890004）无自愈**：采集循环 3ms 死循环刷 Warn（实测 75,570 行），
  期间 Host 入站处理全面失灵（房间号审批、直连认证质询均无响应且无日志），最终 AppHangB1 挂起被系统终止；
  全新进程一次连通。修复方向：失效即重建管线（指数退避 ≤3 次）+ Warn 限速 1/s。
- **【实锤】Host 入站路径零可观测性**：上述失灵期间 accept/auth 环节无任何日志，未认证连接上限（4）触达疑似静默丢。
- 欠产出再实测：GUI 会话 1784→1000 kbps（目标 7300，14–24%），AutoTest 64%——编码专项的必要性再次确认。

## 近期候选（1.3.x，小改动）

- [ ] **GPU 缩放滤波质量**：捕获→NV12 的 VideoProcessor 换高质量滤波（默认近似双线性，
      源/输出分辨率不一致时是一层"柔化"来源）
- [ ] **关键帧质量保护**：尽力下发 MinQP / 质量参数（CBR 下 IDR 可能被压得偏惨，
      屏幕内容画质基线由 IDR 决定）；不支持的编码器自动跳过
- [ ] **全屏鼠标指针闲置自动隐藏**（3 秒不动消失，动则恢复）
- [ ] **全屏悬浮信息条**：码率/延迟/缓冲深度，鼠标移近顶部才浮现
- [ ] **Viewer 截图**：保存当前帧 PNG（含快捷键）
- [ ] **画面缩放模式**：均匀 / 拉伸 / 1:1 像素（1:1 对看代码场景有用）
- [ ] **自动更新提示**：启动时查 GitHub latest release，有新版弹提示（不自动下载）
- [ ] 若 CodecAPI 攻坚成功且实测码率回到规划量级：规划 bpp 0.14 → 0.25
**v1.5.0 快赢清单（Q1–Q7，2026-10-06 全部落地，单测 207/207 + 冒烟 11/11 + UIA 回归 PASS）**：
- [x] Q1 音频设备失效自愈：连续 25 次读取失败即重建 WASAPI 链（0.5s→5s 指数退避、跟随新默认设备、
      Warn 限速），退避期间持续补静音块保持时间线连续——根治 0x88890004 刷屏 7.5 万行 → AppHangB1 缺陷链
- [x] Q2 `TcpClient.NoDelay=true`（TcpFrameConnection 构造，两端共用）
- [x] Q3 解码热路径缓冲复用：MF 解码器 nv12 中间缓冲 + 双解码器 BGRA 输出走 ArrayPool，
      `DecodedVideoFrame.ReturnBuffer` 由上屏线程渲染后归还（跳帧/挤掉/退出清队列三处同步归还）
- [x] Q4 Viewer CLI：`--connect host[:port] / --room / --password / --port / --signaling`，带连接目标时自动连接
- [x] Q5 UIA 回归入库 `scripts\uia-regression.ps1`（Host 开共享 → Viewer CLI 自动连接 → 审批代批 →
      断言已连接/观看者 1 → 优雅断开），TESTING.md 已载
- [x] Q6 抖动缓冲低延迟档：Viewer「低延迟声音」开关（40ms，默认仍 120ms），下次连接生效
- [x] Q7 审批体验：ApprovalDialog 60 秒倒计时自动拒绝；Host 记录「新设备等待用户审批」；
      Viewer 超时提示改为「Host 无响应（首次连接请在 Host 端批准本设备后自动重连）」
      — ⚠️ **2026-10-06 v1.5.2 审计复勘：部分落地，实际无效**。审批发生在发 `AuthChallenge` 之前
      （`LanShareServer.cs:411-426`），而整个握手由 `:536` 的 **15 秒**总超时兜底，短于弹窗的
      60 秒（`ApprovalDialog.xaml.cs:22`）⇒ 首次连接必然先超时断连，用户再点「允许」已作用在
      已释放的流上。同时 Viewer 此时是 `Failed` → 直接 `return`，**不会自动重连**，与提示语不符。
      修复见 [AUDIT-v1.5.2.md S7](AUDIT-v1.5.2.md#s7medium15s-握手总超时--60s-审批弹窗q7-修复实际无效)

## 中期：HEVC 支持（1.4.0，已于 2026-10-04 落地）

**动机**：同画质 HEVC 比 H.264 省 30–50% 码率。带宽受限场景（Wi-Fi/Wi-Fi）下，H.264 已经没得抠，编解码效率就是清晰度的天花板。

**平台实测结论（2026-10-04，RTX 5060 + Win11 24H2）**：
- 编码：NVIDIA 新驱动不注册 MFT，走 Windows 收件箱的 D3D12 包装器；本机只有 "Microsoft AVC DX12 Encoder"，无 "Microsoft HEVC DX12 Encoder"。可用的是商店扩展 **HEVCVideoExtensionEncoder**（软件路径，720p30 实测约 19fps，跟不上高帧率实时共享；同码率画质仍优于 H.264）。
- 解码：收件箱 HEVC 解码器缺席；商店扩展 "HEVCVideoExtension" 可用但**必须用 MFT 自报的输入类型**（手工拼类型一律被拒），且其 `ProcessMessage` 在部分配置下原生崩溃（AccessViolation，.NET 不可捕获）。
- 工程对策：① Viewer 的 HEVC 解码能力用**子进程探针**实测（自 spawn `--probe-hevc`，崩溃隔离在子进程，结论缓存进设置）；② HEVC 候选解码器用 MFT 自报类型回设；③ 会话级编码协商 + 能力拒接；④ WebRTC 通道 v1.4 不支持 HEVC（H.264 RTP 打包器语义不兼容），HEVC 会话对 `webrtc-request` 回 `webrtc-reject`。

**设计落地**：
1. 编码器链：HEVC 候选（硬件 MFT → HEVC DX12 → 扩展/软件）按名字 + 试配类型筛选；`MfVideoEncoder.ProbeAvailable` 供 Host 开共享前决策，失败自动回退 H.264
2. 协议：AuthRequest `hevc` 能力位（子进程探针实测）、AuthResult `vcodec`（"h264"/"hevc"）；LAN 帧格式不变（Annex-B 裸流，NAL 头语义随编码）
3. Viewer：按协商结果换解码器（MfVideoDecoder(Hevc)），状态栏显示传输通道 + HEVC 标识
4. Host UI：「HEVC 优先」开关（默认关，兼容性最好）；开启且平台支持时 HEVC 会话生效
5. 风险点：各厂商 HEVC MFT 的 CodecAPI 支持度差异（现有"尽力而为"诊断日志已覆盖）

**后续**：硬件 HEVC 机器（有厂商 MFT 或未来 Windows 提供 HEVC DX12 包装器）上 HEVC 优先能同时保住帧率与画质；WebRTC + HEVC 需要自定义 RTP 打包器（RFC 7798），单独立项。

## 解码兜底专项：FFmpeg 软解（1.4.2，2026-10-05 落地）

**动机**：1.4.0 实测证明「扩展 HEVC 解码 MFT」在部分机器上不可用（原生崩溃）且收件箱解码器缺席，
HEVC 会话在这些机器上只能拒接。需要一个**永远不缺位的解码兜底**，让解码能力不再受系统组件状态支配。
对标：RustDesk/OBS 的编解码回退链架构——系统编解码优先，软件库兜底。

**落地结果（v1.4.2）**：解码链 = **MF（子进程探针通过时）→ FFmpeg 软解兜底 → 拒接**；
FFmpeg 为进程内纯软件路径（libavcodec n7.1 动态链接：avcodec-61/avutil-59/swresample-5，
Sdcb LGPL 构建，仅 ~45MB 压缩增量），Viewer 能力位变为 MF 探针 ∥ FFmpeg 可用性（能力并集）。
低延迟配置：thread_count=1 + LOW_DELAY（Moonlight 经验），实测投喂即解出（90 帧进 90 帧出）。
工程细节：函数走自写带版本后缀 P/Invoke（AutoGen 的裸名动态加载器与发行版 DLL 命名不兼容，
仅复用其结构体定义）；YUV420P→BGRA 复用 Nv12ToBgra 的 BT.709 系数；运行期 MF 解码器托管异常
自动换 FFmpeg；LGPL 合规声明见 NOTICES-Ffmpeg.md。

**MFT 最后一轮攻坚（2026-10-05，失败，结论固化）**——四个变体（独立子进程）：
- 基线复现：`ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING)` 必崩（AV），与自报输入类型已设/未设无关；
- 消息顺序对调：`NOTIFY_START_OF_STREAM` 单独存活 → **崩溃点唯一锁定 BeginStreaming**；
  但喂帧前补发 BeginStreaming 照崩 → 无绕过空间；
- 跳过 BeginStreaming 直喂：进程存活、流/类型枚举健全（NV12/IYUV/P010/AYUV 自报类型齐全），
  但**所有 SetOutputType（自报 + 裸占位）一律 MF_E_ATTRIBUTENOTFOUND** → 无法进入解码；
- D3D 管理器先行（Chromium 对 D3D11-aware MFT 的标准姿势）：设备/管理器/ResetDevice 全成功，
  `MFT_MESSAGE_SET_D3D_MANAGER` 被解码器 E_FAIL 干净拒绝。
结论：该机器上的扩展解码 MFT 内部状态已坏（扩展/驱动层问题，非调用方姿势），不再回头；
这类机器由 FFmpeg 路径承接，有厂商硬解 MFT 的机器仍走 MF。

**验收（已达成）**：实测「扩展解码 MFT 崩溃」的机器（本机）上，HEVC 会话可直接观看——
冒烟 Part2c 硬断言（编码 106 帧 → FFmpeg 回读 106 帧）、Part4b 端到端接入解码 66 帧；
实启 UI 双端验证状态栏显示 `HEVC（FFmpeg）· 实测/目标 kbps`。

**v1.4.3 补丁（2026-10-05，用户实测反馈接入黑屏/重连无画面）**：根因是换解码器挂在
Dispatcher 异步执行，而 Host 的 GOP 补发（含 IDR）早于换轨完成 38ms 到达——补发帧被旧的
H.264 解码器静默吞掉（H.264 MFT 收 HEVC 字节不报错），换轨后只能干等编码器下一 IDR
（真实桌面低帧率下几十秒）。修复：换解码器移到**网络线程同步**（`EnsureDecoderFor`，
任何帧解码前就位），实测补发 30 帧 → 3ms 就位 → 37ms 出图。同时新增用户可选解码器
（自动/MF/FFmpeg，能力上报联动）与无画面看门狗提示。

**v1.4.4 补丁（2026-10-05，用户实测反馈首次连接即卡画）**：① 协议里本就有的
`KeyframeRequest`（认证即发）对不认 ForceKeyFrame 的编码器一直是空操作——Host 收到该
请求现在**直接补发缓存 GOP**（1s 去重）；观看端解码器中途重建后也主动请求，画面秒恢复。
② 探针测不出数据路径缺陷：HEVC 会话中 MF 解码器 8 秒零输出（码流在到）自动判定失效，
弃用重建为 FFmpeg（自动模式）+ 请求补发。③ Host 端「HEVC 优先」复选框升级为编码器
选择框（自动/H.264/HEVC 优先，旧设置迁移）。

**后续**：d3d11va 硬件加速（同一套 IVideoDecoder 契约，低功耗设备需要时再启用）；
H.264 的 FFmpeg 兜底位已预留（收件箱 H.264 解码器可靠，暂不启用）。

## 远期 / 大专项

- [ ] **UDP 媒体传输 + 前向纠错 + NACK**（1.6 首选大项，2026-10-06 评估）：
      对标 Moonlight——控制面可靠、媒体面不可靠，单帧分片 + RS GF(256) 校验片接收端恢复，
      残余丢帧 NACK 补发。解决 TCP 队头阻塞（WiFi 弱网卡帧）这一 LAN 工具最大架构差距。
      ENet 用 MIT 上游；Sunshine/Moonlight 代码 GPL，只借鉴协议设计不抄代码。
- [ ] **d3d11va 硬解 + D3D 呈现**：试运行实测 Viewer CPU（~45% 一核）反超编码侧（~37%），
      软解 + CPU WritePixels 是主因；IVideoDecoder 契约已就绪。
- [ ] **浏览器免安装观看**（对标 screego/Deskreen 核心卖点）：
      手机/平板/任何设备打开网页扫码即看。首选路线评估（2026-10-06）：**WebCodecs 优先**
      （保留现有 AAC/H264/HEVC 栈，Edge/Chrome 在 Windows 可硬解 HEVC，WebSocket/WebTransport + 自定义查看页）；
      次选 AAC→Opus 转码走标准 WebRTC（SIPSorcery 自带 Opus）。工程量大，需单独立项设计。
- [ ] **mDNS 发现 + 手动 IP 兜底**：AP 隔离下任何组播发现都失效（竞品同病），手动 IP/房间码兜底才是主路径。
- [ ] **SetBitrate 动态重配**（FFmpeg 厂商编码器上生效，激活 CongestionController 码率梯子）
      — ⚠️ **2026-10-06 v1.5.2 审计实况**：`FfmpegVideoEncoder.SetBitrate` 恒 `return false`
      （`FfmpegVideoEncoder.cs:251-255`），而 1.5.2 的生产路径就是 FFmpeg 厂商硬编 ⇒ **码率梯子
      在主路径上是空操作**，且 `EncoderPipeline` 仍改写 `Settings.BitrateBps` 并向 Viewer 上报
      「已降档」目标值（UI 显示一个编码器不遵守的数字）。与「拥塞降档不可逆」叠加后，
      唯一真正生效的拥塞动作是不可逆降分辨率。详见
      [AUDIT-v1.5.2.md A3/A4](AUDIT-v1.5.2.md#三实锤缺陷a1a6)
- [ ] **信令加固**：WSS 默认、CORS 收紧、密码哈希服务端限速（31^8 可离线穷举）
      — ⚠️ **2026-10-06 v1.5.2 审计逐条核实：三条全部未落地**。`Program.cs:9-10` CORS 允许任意来源
      且带凭据、全文无 HTTPS 强制/认证/限速；凭据是**固定盐**的可重放哈希（嗅探即可重放，且一张
      预计算表通杀所有部署）。另发现两处更严重的越权面：`RelayToHost` 零认证（仅知 6 位房间号即可
      劫持 WebRTC 协商）、Host 专用方法对 viewer 开放（可自批审批 / 停掉整个房间）。详见
      [AUDIT-v1.5.2.md S1–S3](AUDIT-v1.5.2.md#四安全与鲁棒性审查s1s10)
- [ ] **虚拟显示器/虚拟声卡**支持（无显示器环境、仅推声音场景）

## 明确不做

- **任何输入注入 / 远程控制**：核心安全卖点，永不妥协（协议层无输入消息）
- **剪贴板同步 / 文件传输**：会侵蚀"只读共享、零上行通道"的安全定位，收益不抵代价

## 当前优先级队列（2026-10-06，来源：[AUDIT-v1.5.2.md](AUDIT-v1.5.2.md)）

> 每项的完整证据、`file:line` 与验收方式见审计文档对应章节。此处只做索引与排序。

### P0（目标 1.5.3）—— 运行时可感知的缺陷，改动均局部低风险

> **2026-10-06 状态**：本批已落地**并验证**——`dotnet build` 0 错误、`dotnet test` **215/215**、
> 冒烟 **11/11 PASS**、A1/A2/A5/N10 实机复测通过。详见 [DEVLOG.md](DEVLOG.md#s-2026-10-06-b)。

| ID | 一句话 | 位置 | 审计 |
|----|--------|------|------|
| A1 | **WGC 不节流：捕 105fps 而编码只需 24fps**，78% 的帧走完全套复制/转换后被丢弃 ⇒ 托管分配放大到 8.3MB/s，堆被推到 161MB、工作集 ~466MB | `GraphicsCaptureEngine` + `EncoderPipeline` + `ShareSession` | [A1](AUDIT-v1.5.2.md#a1中-host-共享期间托管堆被白做的帧推到-460mb-工作集-实测已定位已修复并验证) |
| A2 | 托盘图标 `hWnd=0` ⇒ 右键菜单/单击完全不可达（默认「关闭隐藏到托盘」下窗口唤不回、共享停不掉） | `TrayIcon.cs:72-77` | [A2](AUDIT-v1.5.2.md#a2高-托盘图标永远收不到鼠标消息hwnd--0-代码) |
| A3 | 拥塞降档是单向棘轮：降了分辨率就再也回不去；15s 回升节流失效（振荡） | `CongestionController.cs:136-156` | [A3](AUDIT-v1.5.2.md#a3中-拥塞降档是单向棘轮降了分辨率就再也回不去-代码) |
| A4 | `SetBitrate` 在主路径上空操作，却仍上报"已降档"目标值 | `FfmpegVideoEncoder.cs:251-255`、`EncoderPipeline.cs:154-160` | [A4](AUDIT-v1.5.2.md#a4中-setbitrate-在-152-的主编码路径上是空操作但仍上报已生效-代码) |
| A5 | Viewer 显示 1920×1088，底部 8 行填充像素进入画面与缩放 | `StatsInfo` + `Viewer/MainWindow.xaml.cs` | [A5](AUDIT-v1.5.2.md#a5中-viewer-把-h264-的-1088-行填充当画面显示-实测代码已修复并验证) |
| A6 | 抖动缓冲深度计数过减 ⇒ 假欠载 + 自适应缓冲无谓加深 | `AudioRenderer.cs:180` | [A6](AUDIT-v1.5.2.md#a6中-音频抖动缓冲深度计数过减--假欠载-代码) |
| S6 | `Dispose` 在"对端断开"路径下变空操作 ⇒ socket/密钥/信号量只等 GC | `TcpFrameConnection.cs:276-285` | [S6](AUDIT-v1.5.2.md#s6mediumdispose-在正常断连后变成空操作--socketaes-密钥信号量泄漏) |
| **N10** | **启动瞬间用控件默认值覆盖已保存的用户设置**（"关闭隐藏到托盘/局域网发现"勾了下次又没了） | `Host/MainWindow.xaml.cs:74-94` | [N10](AUDIT-v1.5.2.md#本轮新发现的缺陷审计时未识别) |
| — | 冒烟 Part5 路径硬编码 ⇒ 对 `dist` 部署产物必然 FAIL | `SmokeTest/Program.cs` | [5.3](AUDIT-v1.5.2.md#53-冒烟测试-part5-对部署产物必然失败) |

### P1（目标 1.6.0）—— 需要独立设计的加固项

| ID | 一句话 | 审计 |
|----|--------|------|
| S1 | `RelayToHost` 零认证：仅知 6 位房间号即可劫持/打断 WebRTC 协商（公网暴露时 CRITICAL） | [S1](AUDIT-v1.5.2.md#s1critical公网relaytohost-零认证仅知-6-位房间号即可劫持媒体协商) |
| S2 | Host 专用方法对 viewer 开放：可自批审批、可停掉整个房间、可冒充 Host 中继 | [S2](AUDIT-v1.5.2.md#s2high公网host-专用方法无角色隔离viewer-即-host) |
| S3 | 信令无 WSS 强制/CORS 任意来源/无限速/固定盐可重放哈希 | [S3](AUDIT-v1.5.2.md#s3high公网传输与加固缺失roadmap-的三条加固项均未落地) |
| S4 | AAD 与防重放能力位由**未认证**字段协商，可被中间人静默降级（与 PROTOCOL 承诺不符） | [S4](AUDIT-v1.5.2.md#s4mediumaad-绑定与防重放可被未认证方静默降级与-protocol-承诺不符) |
| S5 | 加密会话中仍接受明文控制帧（可注入 `StatsInfo.RttMs` 钉死全体码率） | [S5](AUDIT-v1.5.2.md#s5medium加密启用后仍接受明文控制帧) |
| S7 | 15s 握手超时 < 60s 审批弹窗（见上文 Q7 修正） | [S7](AUDIT-v1.5.2.md#s7medium15s-握手总超时--60s-审批弹窗q7-修复实际无效) |
| S8 | 未认证并发上限是软上限（TOCTOU）+ 认证前无读超时 + 审批弹窗洪水 | [S8](AUDIT-v1.5.2.md#s8medium未认证并发上限是软上限--认证前无读超时--弹窗洪水) |
| S9 | 信令 `Room.Viewers` 是普通 `Dictionary`，注释却称"线程安全" | [S9](AUDIT-v1.5.2.md#s9mediumroomviewers-非线程安全注释与代码矛盾) |

### P2（候选）—— 低风险清理与体验口径

| ID | 一句话 | 审计 |
|----|--------|------|
| S10 | 8 项低危项（ADTS 头校验、accept 前置异常、接收任务挂起、日志输入裁剪、AAC drain 上限、WASAPI 释放、`MFStartup` 引用计数、`AudioRenderer.Stop` 泄漏） | [S10](AUDIT-v1.5.2.md#s10low其余确证项各一行均有明确位置与最小修复) |
| A7 | Viewer「编码器欠产出」在静态桌面内容下必然误报，需区分"内容简单"与"编码器封顶" | [5.1](AUDIT-v1.5.2.md#51-状态栏编码器欠产出在静态内容下必然误报) |
| A8 | 「编码验证(写H.264)」是持久化设置且无大小上限（试运行前已累计 582MB/13 文件） | [5.2](AUDIT-v1.5.2.md#52-编码验证写h264是持久化设置且会大量落盘) |

## v1.4.5 补丁（2026-10-05，音画同步时钟冻结 + 能力位竞态）

用户实测「连接成功但画面卡住」复勘定位到**音画同步时钟冻结骗局**：音频欠载时
`GetPlaybackUtcTicks` 返回不再前进的时间戳，而 `AudioPlaybackPipeline` 的时钟定时器
仍在周期调用 `AvSyncClock.Update` 刷新 QPC 基准——「400ms 无更新即失效」的保护被完全
骗过。自动重连进入新会话（或 Host 重启共享）后，新画面时间戳比冻结的音频时钟领先数分钟，
上屏线程以「画面早于声音」为由永久等待 → 冻结。修复三层：① 时钟值持续不前进超 StaleTicks
即失效（AvSyncClock 冻结检测，单测覆盖）；② PresentLoop 单帧等待 500ms 硬上限（任何时钟
病理的兜底）；③ 重连成功时重建音频管线（新时钟 + 新抖动缓冲）。另修复：FFmpeg 可用性改
连接时惰性实测（启动后台探测未完成时能力位误报 false → HEVC 被误拒）；用户断开后的
「Xms 后重连…」误导日志。
