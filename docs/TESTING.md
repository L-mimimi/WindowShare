# WindowShare 测试指南

## 1. 单元测试（无需显示器）

```powershell
dotnet test
```

覆盖范围：

| 测试类 | 覆盖内容 |
|--------|----------|
| `FrameProtocolTests` | 帧头编解码往返、魔数校验、超长负载拒绝、未知类型拒绝、负载帧解析 |
| `SecurityTests` | 房间号/密码字符集与长度、随机性、PBKDF2 确定性与盐相关性、常量时间比较、HMAC 确定性 |
| `StatsCollectorTests` | 码率/帧率计算、延迟 EWMA 收敛、无样本时 NaN |
| `CongestionControllerTests` | 健康时维持、高 RTT 降码率、连续降档后降分辨率、最低档保护、高丢帧率触发降档、最小评估间隔、恢复不超初始值 |

期望结果：**20/20 通过**。

## 2. 冒烟测试（真实捕获本机屏幕）

```powershell
dotnet run --project tools/WindowShare.SmokeTest
```

会依次执行 6 个部分，全部 `PASS` 时退出码为 0：

| 部分 | 内容 | 通过标准 |
|------|------|----------|
| Part1 | 探测捕获引擎、捕获源、编码器清单 | 至少一种捕获引擎可用；列出显示器与窗口 |
| Part2 | 合成运动图像 → GPU NV12 → H.264 → 文件 | ≥45 帧、≥30KB、含 SPS/PPS |
| Part3 | 真实捕获主显示器（WGC + GDI 双引擎） | 至少一个引擎出帧并编码成功 |
| Part4 | 回环端到端：ShareSession + LAN 服务器 → 客户端 + 解码器 | 连接成功、加密启用、收帧 >60、解码 >30 |
| Part5 | 信令服务器回环（含错误密码负向用例） | 错误密码被拒、审批通过、取到 LAN 端点 |
| Part6 | WebRTC 回环（DTLS-SRTP + H.264 RTP） | 连接成功、收帧 >30、解码 >20 |

产物：`%APPDATA%\WindowShare\recordings\smoke-*.h264`，可用 ffprobe 验证：

```powershell
ffprobe -f h264 "$env:APPDATA\WindowShare\recordings\smoke-synthetic.h264"
```

> 注意：Part3 的 WGC 在桌面完全静止时可能不产生新帧（屏幕内容无变化时不推送），
> 这是 Windows Graphics Capture 的正常行为；测试会用 GDI 引擎兜底验证编码链路。

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

## 5. 安装包验证

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package
```

- [ ] `installer\output\WindowShare-Setup-1.0.0.exe` 生成
- [ ] 双击安装（无需管理员权限），开始菜单出现 Host / Viewer 快捷方式
- [ ] 从开始菜单启动 Host，功能与开发构建一致
- [ ] 卸载后 `%APPDATA%\WindowShare`（白名单/设置）保留，程序目录被清理
