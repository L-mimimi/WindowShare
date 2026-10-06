# WindowShare 测试指南

## 1. 单元测试（无需显示器）

```powershell
dotnet test
```

覆盖范围（按测试文件）：

| 测试文件 | 覆盖内容 |
|----------|----------|
| `FrameProtocolTests` | 帧头编解码往返、魔数校验、超长负载拒绝、未知类型拒绝、负载帧解析 |
| `SecurityTests` | 房间号/密码字符集与长度、随机性、PBKDF2 确定性与盐相关性、常量时间比较、HMAC 确定性 |
| `SecurityHardeningTests` | 认证限流（失败计数/冷却/成功清零）、连接上限判定、能力位版本协商边界 |
| `StatsCollectorTests` | 码率/帧率计算、延迟 EWMA 收敛、无样本时 NaN |
| `CongestionControllerTests` | 健康时维持、高 RTT 降码率、连续降档后降分辨率、最低档保护、高丢帧率触发降档、最小评估间隔、恢复不超初始值 |
| `AppPathsTests` | 便携/安装模式数据目录决策、只读位置回退 `%APPDATA%`、环境变量优先级、设备 ID 稳定性 |
| `JsonSettingsStoreTests` | 设置读写往返、损坏/缺字段的容错、并发写安全 |
| `VideoFormatPlannerTests` | 帧率档位、4K 上限、等比缩放取偶且不上采样、码率推算边界与单调性、`SuggestH264Level` 各档位、Level 展示名 |
| `GopCacheTests` | GOP 缓存：以 IDR 开头才可补发、新 IDR 开启新一轮、帧数/字节越界后等到下一个 IDR 再积累、分辨率切换作废缓存、快照与后续追加互不干扰 |
| `AnnexBTests` | H.264/HEVC NAL 头解析、IRAP 关键帧判定、VPS/SPS/PPS 识别、Annex-B 遍历边界 |
| `AudioTests` | ADTS 封装/解析与非法头拒绝、float32↔int16 转换与削波、重混/重采样、采集时间线（`SampleTimeline`）、音画同步时钟（`AvSyncClock`）、音频流常量自洽 |
| `Yuv420pToBgraTests` | YUV420P→BGRA 转换、BT.709 系数、边界尺寸 |
| `LanDiscoveryTests` | 组播 announce 编解码、魔数/版本校验、过期判据、名称与端口回退 |
| `VideoEncoderFactoryTests` | 编码器候选链选择（厂商硬编 → MF 现链） |
| `VideoDecoderFactoryTests` | 解码器候选链选择（MF → FFmpeg 兜底）、能力位联动 |

合计：**207 项**（`[Fact]` 140 + `[Theory]` 的 `[InlineData]` 67）。

## 2. 冒烟测试（真实捕获本机屏幕）

```powershell
dotnet run --project tools/WindowShare.SmokeTest
```

会依次执行 **11 项**，全部 `PASS` 时退出码为 0：

| 部分 | 内容 | 通过标准 |
|------|------|----------|
| Part1 | 探测捕获引擎、捕获源、编码器清单，并逐档验证「分辨率 × 帧率」可配置（日志带推导出的 H.264 Level 与编码器实际接受的 Level） | 至少一种捕获引擎可用；720p30 / 1080p30 / 1080p60 / 1080p144 / 2K30 / 4K30 / 4K60 各档均可用 |
| Part2 | 合成运动图像 → GPU NV12 → H.264 → 文件 | ≥45 帧、≥30KB、含 SPS/PPS |
| Part2b | 4K（3840×2160@30）与高帧率（1280×720@120）编码 | 4K 输出分辨率正确且码流能解回 3840×2160（解码帧数 ≥ 编码帧数的 80%）；120fps 档实际编码帧率不超过目标的 135%（帧率节流生效） |
| Part2c | HEVC 编解码往返（合成内容 → HEVC 编码 → 解码回读） | 本机无可用 HEVC 编码器时软性通过；有编码器时解码回读帧数与编码帧数一致（MF 探针失败走 FFmpeg 软解兜底，见 [AUDIT-v1.5.2.md](AUDIT-v1.5.2.md)） |
| Part2d | FFmpeg 厂商硬编（`h264_nvenc`/`amf`/`qsv`）**真实 CBR** 断言 | 工厂选中厂商硬编时，实测码率/目标码率 **≥50%**，并用 FFmpeg 软解回读全部编码帧；本机无厂商 GPU 时软性通过 |
| Part3 | 真实捕获主显示器（WGC + GDI 双引擎） | 至少一个引擎出帧并编码成功 |
| Part4 | 回环端到端：ShareSession + LAN 服务器 → 客户端 + 解码器（GDI 定速捕获） | 连接成功、加密启用、收帧 ≥40、解码帧数 ≥ 首个 IDR 后可解码帧数的 90%、**收到的第一帧就是 IDR**（GOP 补发生效） |
| Part4b | HEVC 会话协商（负例 + 正例） | 负例：不支持 HEVC 解码的观看端必须被拒且**原因可读**（含"HEVC"字样）；正例：能力并集（MF 探针 ∥ FFmpeg 兜底）成立时正常接入并解码 |
| Part5 | 信令服务器回环（含错误密码负向用例） | 错误密码被拒、审批通过、取到 LAN 端点。⚠️ **定位信令 dll 时按序尝试源码树 → `dist\publish\signaling\` → `AppContext.BaseDirectory\signaling\`**；对纯 `dist` 部署产物运行时，源码树路径不存在，缺兜底会误报 FAIL |
| Part6 | WebRTC 回环（DTLS-SRTP + H.264 RTP，GDI 定速捕获） | 连接成功、收帧 ≥30、解码帧数 ≥ 投喂帧数的 90% |
| Part7 | 系统声音：**7a** AAC 编解码往返（合成双声道正弦波 → AAC → 解回 PCM）、**7b** LAN 音频端到端（ShareSession → LanShareServer → LanShareClient → 解码）、**7c** WASAPI loopback 探测 | 7a：ADTS 头全部自描述且与帧长一致、时间戳严格递增、解回帧数 ≥ 输入的 85%、格式不符 0 块、RMS ∈ (0.05, 0.9)、左右声道过零比 ∈ (0.35, 0.70)（期望 ≈0.5，可抓住声道交换/被下混/重采样系数写错）；7b：加密开启、会话音频参数与约定一致、收帧 ≥50、ADTS 头非法 0、时间戳乱序 0、解码异常 0、解码块数 ≥ 收帧数的 80%；7c 为软性探测，不计入 Part7 通过条件 |
| Part6（音频） | WebRTC 音频回环：合成音频走 AAC 动态 PT 97 → RTP/DTLS-SRTP → 裸 AAC 包回 ADTS → MF 解码 | 音频帧 ≥50、解码帧数 ≥ 收帧数的 90%（与视频断言合并为 Part6 通过条件；本机无 AAC 编码器时软性跳过） |
| Part8 | 局域网组播发现回环：DiscoveryBeacon → 组播（本机走回环接口）→ DiscoveryListener | 8 秒内收到 announce 且设备名/端口与广播一致；本机组播被禁时按软性处理（协议层由单测覆盖） |

产物：`%APPDATA%\WindowShare\recordings\smoke-*.h264`，可用 ffprobe 验证：

```powershell
ffprobe -f h264 "$env:APPDATA\WindowShare\recordings\smoke-synthetic.h264"
```

> 注意：Part3 的 WGC 在桌面完全静止时可能不产生新帧（屏幕内容无变化时不推送），
> 这是 Windows Graphics Capture 的正常行为；测试会用 GDI 引擎兜底验证编码链路。

> Part4 / Part6 显式指定 `CaptureEnginePreference.Gdi`（定速轮询，出帧节奏与屏幕内容无关），
> 避免静态桌面下 WGC 只出约 8fps 导致比例断言抖动；断言均为比例式而非绝对帧数。

> Part7c 只探测 WASAPI loopback 能否采集：没有播放设备的机器（声卡被禁用、远程会话、无声卡 CI）
> 上它只记告警并返回 true——「本机没声卡」不是产品的 bug，不该让冒烟测试变红。
> Part7b 在 loopback 起不来时自动改用合成正弦波手工注入，验证的仍是同一条编码、网络与解码链路。
> Part7 实测参考（本机）：7a 输入 96000 帧 → 91 个 ADTS 帧 → 解回 90 块 / 92160 帧（96.0%）、
> RMS 0.211、过零比 0.500；7b 6 秒收到 280 帧 / 95 KB → 解出 279 块 / 285696 样本（5952 ms）、解码异常 0 次。

> Part4 连接前会等 `LanShareServer.CachedGopFrames > 0`（最多 10 秒），确保服务器已攒出
> 一个以 IDR 开头的 GOP。本机 `Microsoft AVC DX12 Encoder` 对 `CODECAPI_AVEncVideoForceKeyFrame`
> 返回 `E_NOTIMPL`，观看者能秒开完全依赖服务器补发缓存 GOP，「首帧即 IDR」就是它的回归网。

## 3. Host 自动验证（无 UI）

```powershell
dotnet run --project src\WindowShare.Host -- --autotest
```

验证 ShareSession 编排：房间号/密码生成（6 位 / 8 位）、预览帧、编码分发、
录制文件写入、一键停止回调。退出码 0 = 通过。

## 4. 手工功能测试清单

### 4.1 捕获与预览

- [ ] 整屏共享：预览正常、无花屏、鼠标指针可见
- [ ] 指定窗口共享：仅该窗口内容进入预览（最小化其他窗口、移动共享窗口均验证）
- [ ] 窗口被关闭时：悬浮条提示「共享目标已关闭」并自动停止

### 4.2 编码验证

- [ ] 勾选「编码验证(写H.264)」共享 10 秒，`%APPDATA%\WindowShare\recordings\` 生成文件
- [ ] `ffprobe` 能识别为 H.264 且分辨率与设置一致
- [ ] 日志中 `编码器=...` 标注硬件/零拷贝状态

### 4.3 局域网观看

- [ ] Viewer 直连 IP + 正确密码 → 状态栏显示「已连接 ✓」「LAN TCP 直连」「AES-256-GCM ✓」
- [ ] 状态栏四项统计（码率/帧率/延迟/分辨率）持续刷新
- [ ] 错误密码 → 提示「密码错误」，不进入观看界面
- [ ] 首次连接弹出设备审批框；选择「拒绝」后被拒绝接入
- [ ] 选择「允许并记住」后，第二次连接不再弹窗（白名单生效）
- [ ] Host 端「观看者：N」计数正确
- [ ] 拔掉网线 / 断开网络 → Viewer 显示「重连中…」；恢复后自动重连并请求关键帧
- [ ] Host 点击悬浮条「⏹ 停止」→ Viewer 立即显示「Host 已停止共享」

### 4.4 共享提示与一键停止

- [ ] 共享期间屏幕顶部常驻红色指示条，显示源名称与时长，可拖动
- [ ] 共享指定窗口时窗口四周有红框，且窗口移动/缩放时红框跟随
- [ ] 指示条与红框不遮挡鼠标操作（点击穿透）
- [ ] 三种停止方式（悬浮条按钮 / 主界面按钮 / 关闭 Host 窗口）均立即断流

### 4.5 信令与房间号

- [ ] 启动信令服务器，Host 勾选信令并填入地址 → 显示「已连接 ✓」
- [ ] Viewer 输入房间号 + 密码 → 先尝试 LAN 直连，成功后显示直连状态
- [ ] 错误房间号 → 提示「房间不存在或已过期」
- [ ] 错误密码 → 提示「密码错误」
- [ ] Host 撤销共享后房间失效，Viewer 再连接提示房间不存在

### 4.6 WebRTC 广域网

- [ ] 两台机器位于不同网段（如一台走手机热点）→ Viewer 自动回退 WebRTC
- [ ] 状态栏显示「WebRTC 直连(P2P)」或「WebRTC 中继(TURN)」
- [ ] 画面正常、无绿色块（DTLS-SRTP 协商成功）
- [ ] 配置 TURN 后强制走中继（可在 coturn 日志中确认中继流量）

### 4.7 性能与自适应

- [ ] 4K 屏幕共享时日志显示硬件编码，Host CPU 占用合理
- [ ] 观看端人为限速（如用网络限速工具）→ 数秒内码率自适应下调
- [ ] 网络恢复后约 15 秒，码率逐步回升（不超初始值）
- [ ] 严重拥塞时分辨率下调，画面仍保持连贯

### 4.8 安全验证

- [ ] 抓包（如 Wireshark 抓 TCP 48750）确认视频负载为密文（AES-GCM）
- [ ] 篡改加密帧的任意字节 → 连接被主动断开（GCM 认证失败）
- [ ] 未在 `whitelist.json` 的设备且拒绝审批 → 无法接入
- [ ] 共享期间检查：无任何输入注入相关进程/调用（本软件不含该功能）

### 4.9 系统声音（1.2.0）

- [ ] Host 勾选「共享系统声音」后开始共享，界面出现声音状态与实时电平；日志有 `系统声音采集已启动: 设备 <采样率>Hz/<声道>ch/float32 → 输出 48000Hz/2ch/int16`
- [ ] Viewer 端播放音乐/视频 → Host 电平跳动，Viewer 能听到同样的声音
- [ ] 音画同步：播一段对口型视频，画面与声音不脱节；Viewer 状态栏显示「声音：播放中 XXms」
- [ ] Viewer 取消勾选「播放系统声音」→ 立即静音、状态栏变「声音：已静音」，且画面延迟变低（不再等待音频时钟）
- [ ] Host 未勾选「共享系统声音」→ Viewer 状态栏显示「声音：Host 未共享」，画面一切正常
- [ ] 抓包（TCP 48750）确认 `AudioFrame` 负载为密文（与 VideoFrame 同一把会话密钥）
- [ ] 禁用播放设备 / 无声卡环境下开始共享 → 自动降级为纯视频，画面不受影响，日志记录降级原因
- [ ] 共享过程中切换默认播放设备 → 声音可能中断（已知限制），停止后重新开始即恢复
- [ ] 确认不采集麦克风：对着麦克风说话，Viewer 端听不到任何声音
- [ ] 拥塞降档触发分辨率变化时画面不花屏（编码器已按新尺寸重建）

## 5. 安装包验证

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package
```

- [ ] `installer\output\WindowShare-Setup-<版本>.exe` 生成（如 `WindowShare-Setup-1.5.2.exe`）
- [ ] 双击安装（无需管理员权限），开始菜单出现 Host / Viewer 快捷方式
- [ ] 从开始菜单启动 Host，功能与开发构建一致
- [ ] 卸载后 `%APPDATA%\WindowShare`（白名单/设置）保留，程序目录被清理

## UIA 双端连接回归（v1.5.0 起入库）

`scripts\uia-regression.ps1`：全自动「Host 开共享 → Viewer CLI 直连（--connect/--password）→ 设备审批代批 →
断言已连接/观看者 1 → 优雅断开」。README 历次声称的「双次连接 UIA 回归」自 v1.5.0 起以此脚本为准（此前为
未入库的临时手工运行）。需要 dist\publish（build.ps1 或手动 publish）；退出码 0 = PASS。
