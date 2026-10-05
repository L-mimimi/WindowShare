# WindowShare 路线图

> 记录已确认的改进方向、诊断结论与设计草案。发布节奏：小改进随 1.3.x，质变项单独立版。
> 最后更新：2026-10-05（1.4.2 解码兜底专项落地 + MFT 攻坚结论固化）

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

**后续**：d3d11va 硬件加速（同一套 IVideoDecoder 契约，低功耗设备需要时再启用）；
H.264 的 FFmpeg 兜底位已预留（收件箱 H.264 解码器可靠，暂不启用）。

## 远期 / 大专项

- [ ] **浏览器免安装观看**（对标 screego/Deskreen 核心卖点）：
      手机/平板/任何设备打开网页扫码即看。方案：Host 内嵌 HTTP 服务 +
      WebCodecs（新浏览器）/ MSE 回退；信令复用现有 SignalR 或改 WebSocket；
      音频 AAC 走 MSE 原生可播。工程量大，需单独立项设计。
- [ ] **虚拟显示器/虚拟声卡**支持（无显示器环境、仅推声音场景）

## 明确不做

- **任何输入注入 / 远程控制**：核心安全卖点，永不妥协（协议层无输入消息）
- **剪贴板同步 / 文件传输**：会侵蚀"只读共享、零上行通道"的安全定位，收益不抵代价
