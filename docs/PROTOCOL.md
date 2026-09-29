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
12    8    TimestampUtc（发送时刻，DateTime.UtcNow.Ticks，100ns）
20    4    PayloadLength（上限 8MB）
```

### 消息类型

| 值 | 名称 | 方向 | 负载 |
|----|------|------|------|
| 1 | AuthRequest | V→H | JSON：deviceId/deviceName/proto/roomCode |
| 2 | AuthChallenge | H→V | JSON：salt(B64)/enc(是否支持加密)/hostPub(ECDH P-256 SPKI, B64) |
| 3 | AuthProof | V→H | JSON：proof(HMAC)/clientPub(B64) |
| 4 | AuthResult | H→V | JSON：ok/reason/enc/encoder/width/height |
| 10 | Ping | V→H | 8 字节时间戳 |
| 11 | Pong | H→V | 原样返回（V 计算 RTT） |
| 12 | KeyframeRequest | V→H | 空（Host 请求下一帧为 IDR；部分编码器不认该 CODECAPI，此时由 GOP 补发兜底，见下） |
| 13 | Bye | 双向 | 空（优雅断开） |
| 14 | ShareStopped | H→V | JSON：reason |
| 15 | StatsInfo | 双向 | JSON：H→V encoder/hw/source；V→H rttMs（拥塞反馈） |
| 20 | VideoFrame | H→V | H.264 Annex-B 访问单元（1 帧） |
| 21 | RawFrame | H→V | 未编码 BGRA（仅测试通路用） |

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
  │ （后续 VideoFrame/StatsInfo 负载加密）  │
```

- **防中间人**：认证证明 HMAC 绑定双方 ECDH 公钥与盐，不知道密码无法伪造/替换公钥。
- **防重放**：盐每次会话随机生成（16 字节）。
- **防爆破**：密码为 8 位随机去混淆字符集（31^8），PBKDF2 10 万次迭代拉高单次尝试成本。
- **临时密码**：随共享会话生成，会话结束即失效，不落盘。

## 3. 会话加密（LAN 可选）

- 算法：AES-256-GCM（tag 16B，nonce 12B 随机）。
- 报文：`[12B nonce][密文][16B tag]`，帧头 Flags.Encrypted=1。
- 覆盖范围：VideoFrame、StatsInfo 负载；握手消息明文（仅公钥/盐，无敏感数据）。

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

- 拓扑：Host sendonly ↔ Viewer recvonly，单视频轨。
- 编码：H.264（packetization-mode=1，profile-level-id 42e01f），时钟率 90kHz。
- 加密：DTLS-SRTP（强制，WebRTC 标准）。
- ICE：STUN（默认 stun:stun.l.google.com:19302）→ 失败走 TURN（环境变量 WINDOWSHARE_TURN_URL/USER/CRED 或自建 coturn）。
- UI 状态：根据候选类型（host/srflx=直连，relay=中继）显示。
