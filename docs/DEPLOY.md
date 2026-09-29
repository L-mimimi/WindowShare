# WindowShare 部署指南

## 1. 组件与端口

| 组件 | 位置 | 端口 |
|------|------|------|
| WindowShare.Host | 观看者要连接的机器 | TCP 48750（LAN 共享，可防火墙放行） |
| WindowShare.Viewer | 观看者机器 | 出站连接 |
| WindowShare.Signaling | 可选，公网/内网服务器 | HTTP 5000（默认），生产建议 443 + WSS |
| coturn（TURN） | 可选，公网服务器 | 3478（UDP/TCP），49152-65535 中继端口段 |

## 2. 局域网直连（最小部署，无需信令）

1. Host 启动 → 选择屏幕/窗口 → 「开始共享」→ 界面显示房间号与临时密码。
2. Viewer 选择「直连 IP」→ 输入 Host 的 IP 与密码 → 连接。
3. 首次连接 Host 会弹出设备审批框，选择「允许并记住」后该设备进入白名单。

防火墙放行（管理员 PowerShell）：

```powershell
New-NetFirewallRule -DisplayName "WindowShare LAN" -Direction Inbound `
  -Protocol TCP -LocalPort 48750 -Action Allow
```

## 3. 房间号模式（信令服务器）

### 3.1 开发/内网运行

```powershell
cd src\WindowShare.Signaling
dotnet run --urls http://0.0.0.0:5000
```

Host 与 Viewer 的「信令地址」均填 `http://<服务器IP>:5000`。

### 3.2 生产部署（Linux + systemd + WSS）

```bash
# 发布
dotnet publish src/WindowShare.Signaling -c Release -o /opt/windowshare-signaling

# systemd 单元 /etc/systemd/system/windowshare-signaling.service
[Unit]
Description=WindowShare Signaling Server
After=network.target

[Service]
WorkingDirectory=/opt/windowshare-signaling
ExecStart=/usr/bin/dotnet WindowShare.Signaling.dll --urls https://0.0.0.0:443
Restart=always
Environment=ASPNETCORE_Kestrel__Certificates__Default__Path=/etc/ssl/windowshare.pem
Environment=ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/etc/ssl/windowshare.key

[Install]
WantedBy=multi-user.target
```

> **必须使用 HTTPS/WSS**：信令承载房间号与密码哈希，明文 HTTP 仅限隔离内网调试。
> 也可以前置 Nginx/Caddy 做 TLS 终结。

## 4. TURN 中继（coturn）

适用：Host 与 Viewer 跨对称 NAT / 严格防火墙，P2P 打洞失败。

安装（Ubuntu）：

```bash
apt install coturn
# /etc/turnserver.conf
listening-port=3478
fingerprint
lt-cred-mech
user=windowshare:CHANGE_ME_STRONG_PASSWORD
realm=windowshare.example.com
min-port=49152
max-port=65535
# 外网 IP（NAT 环境）
external-ip=<公网IP>/<内网IP>
```

```bash
systemctl enable --now coturn
```

客户端配置（Host 与 Viewer 启动前设置环境变量，或系统级设置）：

```powershell
setx WINDOWSHARE_TURN_URL "turn:turn.example.com:3478"
setx WINDOWSHARE_TURN_USER "windowshare"
setx WINDOWSHARE_TURN_CRED "CHANGE_ME_STRONG_PASSWORD"
```

配置后 Viewer 状态栏会显示「传输：WebRTC 中继(TURN)」或「WebRTC 直连(P2P)」。

## 5. 安全清单

- [ ] 信令使用 WSS（TLS），证书可信
- [ ] coturn 使用 long-term 凭据并定期轮换
- [ ] 首次设备审批开启（默认开启），白名单定期审查（`%APPDATA%\WindowShare\whitelist.json`）
- [ ] 临时密码仅通过可信渠道（面对面/加密聊天）传递
- [ ] 共享期间保持悬浮提示条可见，结束时点击「一键停止」
- [ ] 只共享指定窗口时使用 WGC 窗口捕获，窗口之外内容不会进入视频流

## 6. 性能调优

| 现象 | 处置 |
|------|------|
| 观看端延迟高 | 优先 LAN 直连；降低画质档位（流畅 540p） |
| 画面马赛克后恢复 | 发送队列拥塞丢帧，拥塞控制会在数秒内自适应降码率 |
| CPU 占用高 | 确认日志出现「硬件=True」（NVENC/QSV/AMF/DX12）；否则画质降档 |
| 远程桌面内使用 | WGC/DXGI 不可用时自动回退 GDI，帧率上限 30fps |
