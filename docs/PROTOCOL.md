# WindowShare（窗享）协议规范 v1

所有传输（LAN TCP、WebRTC DataChannel 之外的自定义帧）共用同一套帧格式与认证流程。

> **安全声明**：协议为只读共享设计，不存在任何输入注入/远程控制类消息。
> 媒体加密：LAN 路径可选 AES-256-GCM 会话加密；WebRTC 路径强制 DTLS-SRTP。

## 1. 帧格式（TCP，24 字节帧头 + 负载，小端序）

```
偏移  大小  字段
0     4    Magic 'WSH1' (0x31485357)
4     2    HeaderSize = 24
6     1    Type
7     1    Flags (bit0=关键帧, bit1=负载已加密)
8     4    Sequence（发送序号）
12    8    TimestampUtc（DateTime.UtcNow.Ticks，100ns；控制类消息为发送时刻，媒体帧为**采集时刻**，音画同步据此对齐）
20    4    PayloadLength（上限 8MB）
```

### 消息类型

| 值 | 名称 | 方向 | 负载 |
|----|------|------|------|
| 1 | AuthRequest | V→H | JSON：deviceId/deviceName/proto/roomCode；1.4.0 起追加 hevc（观看端实测支持 HEVC 解码，老版本不带 = false） |
| 2 | AuthChallenge | H→V | JSON：salt(B64)/enc(是否支持加密)/hostPub(ECDH P-256 SPKI, B64) |
| 3 | AuthProof | V→H | JSON：proof(HMAC)/clientPub(B64) |
| 4 | AuthResult | H→V | JSON：ok/reason/enc/encoder/width/height；1.2.0 起追加 audio/audioRate/audioCh/audioCodec/audioEnc（老版本 Host 不带这些字段，Viewer 按「无音频」处理）；1.4.0 起追加 bitrate/fps/vcodec（会话编码 "h264"/"hevc"，老版本 Viewer 按 H.264 处理） |
| 10 | Ping | V→H | 8 字节时间戳 |
| 11 | Pong | H→V | 原样返回（V 计算 RTT） |
| 12 | KeyframeRequest | V→H | 空（Host 请求下一帧为 IDR；部分编码器不认该 CODECAPI，此时由 GOP 补发兜底，见下） |
| 13 | Bye | 双向 | 空（优雅断开） |
| 14 | ShareStopped | H→V | JSON：reason |
| 15 | StatsInfo | 双向 | JSON：H→V encoder/hw/source；V→H rttMs（拥塞反馈） |
| 20 | VideoFrame | H→V | 视频编码 Annex-B 访问单元（1 帧）；编码由 AuthResult 的 `vcodec` 协商（h264/hevc），NAL 头语义随编码不同（H.264 1 字节头，HEVC 2 字节头） |
| 21 | RawFrame | H→V | 未编码 BGRA（仅测试通路用） |
| 22 | AudioFrame | H→V | 系统声音：ADTS 封装的 AAC-LC（48 kHz / 立体声 / 128 kbps）；帧头 TimestampUtc 是该帧第一个采样点的**采集时刻** |

### 关键帧与「接入即出画面」

Host 侧 `LanShareServer` 维护一份 GOP 缓存（`Core/Encoding/GopCache.cs`）：保存自上一个 IDR
以来的全部编码帧。观看者认证通过的瞬间，Host 先把这段缓存整帧补发过去，再接着发实时帧——
因此观看者收到的第一帧必定是 IDR，画面当场可解。

这么做的原因：`CODECAPI_AVEncVideoForceKeyFrame` 并非所有编码器都实现（本机实测
`Microsoft AVC DX12 Encoder` 与软件 `H264 Encoder MFT` 均返回 `E_NOTIMPL`），
此时 IDR 只按编码器内部 GOP 周期出现；静态桌面下实际帧率很低（WGC 约 8fps），
一个 GOP 可能横跨数秒，光靠 `KeyframeRequest` 会让新观看者黑屏干等。

补发与实时分发共用同一把锁，保证「补发的最后一帧」与「随后直发的第一帧」严格有序、不重不漏。
缓存超过 24 MB 或 600 帧即整段作废，等下一个 IDR 重新积累；分辨率切换同样作废。

### 系统声音（AudioFrame，1.2.0 起）

音频与视频共用同一条 TCP 连接、同一套帧头与同一把会话密钥，只是消息类型为 `AudioFrame = 22`。

| 项 | 取值 |
|----|------|
| 采集 | WASAPI loopback（默认播放设备**正在播放**的内容，不采集麦克风），事件模式 |
| 采集后处理 | 重混到立体声、重采样到 48 kHz、float32 → int16；设备静默不产包时按 20 ms 补静音块，保持流连续 |
| 编码 | Media Foundation AAC-LC，ADTS 封装（码流自描述，Viewer 不需要任何带外信息），目标 128 kbps |
| 帧长 | 一个 AAC 帧固定 1024 采样 ≈ 21.33 ms；一个网络包里可能粘多帧，Viewer 按 ADTS 头逐帧拆分 |
| 时间戳 | 帧头 `TimestampUtc` = 该帧第一个采样点的**采集时刻**，与视频帧同一时钟 |
| 方向 | 只有 Host → Viewer，协议中不存在任何音频回传消息 |

Viewer 侧音画同步：以音频播放时钟为主时钟。解码后的视频帧不直接上屏，而是进有界队列，
由独立的上屏线程按 `TimestampUtc` 对齐后再画（画面早于声音就等一会儿，晚于声音就立即画）；
队列满时丢最旧的一帧保住实时性。音频不可用时（Host 未共享 / 用户取消勾选 / 本机没有播放
设备或没有 AAC 解码 MFT），同步时钟置为失效，视频退回「解码完立即上屏」。

解码侧的一个实现坑（1.2.0 实测）：`Microsoft AAC Audio Decoder MFT` 的
`MFT_OUTPUT_STREAM_INFO.dwFlags` 报了 `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`，但
`ProcessOutput` 传 `pSample = NULL` 会返回 `E_INVALIDARG`，而且一次失败之后 MFT 永久卡在
`MF_E_NOTACCEPTING`。因此输出样本一律由调用方分配（按 MF 规范，自带样本的 MFT 会忽略传入
样本，两种情况都安全）；另外输入类型是 `MFAudioFormat_ADTS`，喂进去的样本必须带完整的
ADTS 头，剥掉头喂裸 AAC 会表现为「一直要更多输入、一帧 PCM 都出不来」。

## 2. 认证流程

```
Viewer                                   Host
  │ AuthRequest {deviceId, deviceName} ──►│
  │                                       │ 白名单检查：
  │                                       │   未批准 → UI 审批弹窗（允许并记住/仅本次/拒绝）
  │ ◄──AuthChallenge {salt, hostPub}──────│
  │ key = PBKDF2-SHA256(password, salt,   │
  │                     100k 次, 32B)     │
  │ proof = HMAC-SHA256(key,              │
  │        salt‖deviceId‖hostPub‖clientPub)
  │ AuthProof {proof, clientPub} ────────►│ 常量时间比较（CryptographicOperations.FixedTimeEquals）
  │ ◄──AuthResult {ok, enc=true}──────────│
  │ aesKey = HKDF-SHA256(ECDH(client,host),│
  │        salt, info="wsh1-aead")        │ 同左
  │ （后续 VideoFrame/AudioFrame/StatsInfo 负载加密）  │
```

- **防中间人**：认证证明 HMAC 绑定双方 ECDH 公钥与盐，不知道密码无法伪造/替换公钥。
- **防重放**：盐每次会话随机生成（16 字节）。
- **防爆破**：密码为 8 位随机去混淆字符集（31^8），PBKDF2 10 万次迭代拉高单次尝试成本。
- **临时密码**：随共享会话生成，会话结束即失效，不落盘。

### 安全增强（1.3.0 起，自动协商）

- **认证限流**：同一 IP 60 秒内 5 次认证失败 → 5 分钟冷却期，期间 AuthRequest 直接被拒（响应 `ok=false`），也不触发白名单审批弹窗；认证成功即清空该 IP 记录。仅内存态。
- **连接上限**：未认证并发连接 ≤4，已认证观看者 ≤16，超出直接断开（不响应）。
- **强制加密**：AuthChallenge 携带 `hostPub`（Host 提供加密能力）时，观看端 AuthProof 必须携带 `clientPub`，密钥派生失败或缺失公钥一律拒绝接入——不存在「认证通过后回退明文」的路径。
- **帧头 AAD 绑定 + 防重放**：观看端在 AuthRequest 中携带 `ver`（应用版本），Host 对 `ver ≥ 1.3` 的观看端在 AuthResult 置 `aad=true`；此后两端加密均把 24 字节帧头（类型/标志/序号/时间戳/长度）作为 AAD 绑定进 GCM 认证，且接收端要求加密帧序号严格递增（重放即断连）。老版本观看端（不带 `ver`）自动维持旧的仅负载加密格式。

## 3. 会话加密（LAN 可选）

- 算法：AES-256-GCM（tag 16B，nonce 12B 随机）。
- 报文：`[12B nonce][密文][16B tag]`，帧头 Flags.Encrypted=1。
- 覆盖范围：VideoFrame、AudioFrame、RawFrame、StatsInfo 负载。握手消息（AuthRequest / AuthChallenge / AuthProof / AuthResult）必须明文——密钥本身就是在握手过程中协商出来的；Ping / Pong / Bye 不含内容，同样明文。

### 会话编码协商（1.4.0 起）

- Host 开始共享时决定**会话级**编码（勾选「HEVC 优先」且探针实测本机有可用 HEVC 编码器 → HEVC，否则 H.264），编码随 AuthResult 的 `vcodec` 下发。
- Viewer 在 AuthRequest 携带 `hevc` 能力位：由**子进程探针**实测得出（`WindowShare.Viewer.exe --probe-hevc` / 合并入口同参数）——部分平台的商店扩展 HEVC 解码 MFT 在 `ProcessMessage` 阶段原生崩溃（AccessViolation，.NET 不可捕获），子进程隔离后以退出码承载结论（0=支持，其他=不支持），并缓存进 viewer-settings.json。
- HEVC 会话仅放行 `hevc=true` 的观看端；不支持的观看端在认证阶段收到明确的拒绝原因（老版本 Viewer 不带能力位，同样被拒——升级观看端或 Host 取消「HEVC 优先」即可恢复）。
- WebRTC（跨网段/房间号回退通道）暂不支持 HEVC 会话：H.264 RTP 打包器按 1 字节 NAL 头语义分包，载 HEVC 会损坏码流，Host 对 `webrtc-request` 回 `webrtc-reject` 中继消息说明原因。

## 4. 信令服务器协议（SignalR，`/signalr`）

服务器仅中继信号、验证房间与密码；不接触媒体流。

### Host 方法
| 方法 | 参数 | 说明 |
|------|------|------|
| RegisterHost | roomCode, passwordHash, deviceName, lanEndpoints[] | 注册房间，返回 bool |
| Heartbeat | - | 保活（2 分钟无心跳过期） |
| ApproveViewer | viewerId, approved, reason | 审批结果 |
| StopSharing | - | 关闭房间并通知观看者 |
| RelayToViewer | viewerId, type, payload | SDP/ICE 中继（不解析内容） |

### Viewer 方法
| 方法 | 参数 | 说明 |
|------|------|------|
| JoinRoom | roomCode, passwordHash, deviceId, deviceName | 返回 viewerId；触发 ViewerJoinRequest |
| RelayToHost | roomCode, type, payload | SDP/ICE 中继 |

### 客户端回调
- Host：`ViewerJoinRequest(viewerId, deviceName, deviceId)`、`RelayFromViewer(viewerId, type, payload)`
- Viewer：`ViewerApproved(viewerId)`、`JoinFailed(reason)`、`HostInfo{hostDeviceName, lanEndpoints[], shareActive}`、`RelayFromHost(type, payload)`、`HostStopped`

### 密码哈希
双方均发送 `SHA256(PBKDF2-SHA256(password, "wsh1-signaling", 50k))` 的十六进制串，服务器常量时间比较，不接触明文。生产部署必须使用 WSS（TLS）。

### 接入模式选择
1. 服务器比较双方连接来源与 Host 上报的 LAN 端点，观看者优先尝试 **LAN TCP 直连**（低延迟）；
2. 直连全部失败 → **WebRTC 回退**（Viewer 发送 `webrtc-request`，Host 创建 offer 经信令中继）。

## 5. WebRTC 媒体

- 拓扑：Host sendonly ↔ Viewer recvonly，视频 + 可选音频双轨。
- 视频编码：H.264（packetization-mode=1，profile-level-id 42e01f），时钟率 90kHz，动态 PT 96。
- 音频编码：AAC-LC（与 LAN 通路同一编码器输出），动态 **PT 97**、RTP 时钟率 48kHz、双声道。
  两端都是 WindowShare 对 WindowShare，无需浏览器互通；RTP 负载为裸 AAC 帧（剥掉 ADTS 头），
  接收端按协商参数自行包回 ADTS 后解码。旧版观看端（v1.2）对带音频轨的 offer 协商失败时，
  Host 自动降级为纯视频 offer 重试一次。
- 加密：DTLS-SRTP（强制，WebRTC 标准）。
- ICE：STUN（默认 stun:stun.l.google.com:19302）→ 失败走 TURN（环境变量 WINDOWSHARE_TURN_URL/USER/CRED 或自建 coturn）。
- UI 状态：根据候选类型（host/srflx=直连，relay=中继）显示。

## 6. 局域网发现（UDP 组播）

Host 共享期间周期广播，Viewer 被动监听即可发现同网段的共享端。

- 组播组 `239.255.87.83`，端口 `48751`（UDP，组织本地范围），TTL=1 不出网段，开启回环（同机多端可自见）。
- 广播间隔 2 秒；Viewer 侧条目 6 秒未见刷新即判过期。
- 报文格式：`magic(5B) 'WSH1D'` + `version(1B)` + `JSON(UTF-8)`，单包负载上限 512 字节，超限/魔数/版本不符一律丢弃。

```json
{ "name": "办公室-DESKTOP", "port": 48750 }
```

- `name`：Host 机器名（≤64 字符）；`port`：LAN TCP 共享端口（非法值回退 48750）。
- 安全边界：announce 只暴露「该机正在共享、TCP 端口是多少」——与端口扫描等价的信息；
  **不含密码、房间号、设备 ID**，接入仍须通过完整的三步握手认证（第 2 节）。
- 可用性：组播被 AP 隔离/组播路由禁用时发现自动失效，不影响手输 IP 直连；
  首次监听时 Windows 防火墙可能弹窗，需允许（安装器为 per-user 安装，不预置防火墙规则）。
