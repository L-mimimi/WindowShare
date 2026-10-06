# 交接说明（2026-10-06 收工）

> 面向"明天接手的人"。目标：**不读聊天记录也能安全继续**。
> 详细问题清单见 [AUDIT-v1.5.2.md](AUDIT-v1.5.2.md)；过程记录见 [DEVLOG.md](DEVLOG.md)。

---

## 0. 先看这三件事

1. **改动已提交（4 个提交，工作区干净）。** 见 §4 的提交清单。**尚未推送**到 `origin`，
   是否推送／是否发版由你决定。
2. **P0 批次已闭环并验证**：单测 215/215、冒烟 11/11、4 项实机复测通过。**唯一例外是 A6**（见 §3.1）。
3. **P1 安全批次一行没动**（S1–S9）。其中 S1–S3 只在**信令服务器暴露到公网**时是 CRITICAL/HIGH；
   纯局域网自用风险低。见 §3.4。

---

## 1. 环境与基线

| 项 | 值 |
|---|---|
| 分支 / HEAD | `main` @ `fdcd054`（工作区干净；4 个提交未推送） |
| .NET SDK | **8.0.425**（`C:\Program Files\dotnet\sdk`），满足 `global.json` 的 `8.0.100 / latestMinor` |
| 平台 | Windows、RTX 5060、1920×1080@165Hz |
| 显示相关运行时 | `Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App` |
| 诊断工具 | **无** `dotnet-dump` / `dotnet-counters` / `dotnet-gcdump` / `dotnet-trace`（安装需网络） |

**当前基线（改动后，已实测）**

```powershell
dotnet build -c Release -m:1 --no-incremental   # 0 错误；4 条警告，与 HEAD 逐条一致（非本次引入）
dotnet test  -c Release                          # 215/215 通过
dotnet run --project tools/WindowShare.SmokeTest -c Release   # 11/11 PASS
```

4 条既有警告（**不要误当成新引入的**，用 `git stash` 对照 HEAD 验证过）：
`MfVideoDecoder.cs:228`、`LanShareServer.cs:626`、`Viewer/MainWindow.xaml.cs:605`、`同文件:615`。

---

## 2. 已完成并验证的工作（P0 批次）

| ID | 问题 | 修复要点 | 验证方式 |
|----|------|----------|----------|
| **A1** | WGC 不做帧率节流：捕 105fps 而编码只需 24fps，78% 的帧走完全套复制/转换后被丢弃 ⇒ 托管分配 8.3MB/s、堆推到 161MB、工作集 ~466MB | `GraphicsCaptureEngine` 实现 `IFrameRateLimited`（**取帧后判断**：仍必须 `TryGetNextFrame`+`Dispose` 释放池缓冲，只跳过后续昂贵工作）；`ShareSession` 在引擎自己节流时关掉管线那层，**节流权威唯一** | 探针实测：累计分配 2,500→**272MB**、Gen2 1→**18** 次、堆峰值 161→**52MB**、工作集走平；上屏 fps 无回退 |
| **A2** | 托盘 `NOTIFYICONDATA.hWnd = 0` ⇒ 菜单/单击永不可达（默认「关闭隐藏到托盘」下窗口唤不回、共享停不掉） | 写入 `nid.hWnd = _hwnd`；4 处 `Shell_NotifyIcon` 检查返回值 | `PostMessage(WM_TRAY,WM_RBUTTONUP)` → 观察到 `#32768` 菜单窗口；`WM_CLOSE` 后进程存活 |
| **A3** | 拥塞降档是**单向棘轮**：降到 `_bitrateStep==0 && _resolutionStep>0` 后永不恢复；15s 回升节流失效（振荡） | 分辨率回升独立成支；每档回升刷新 `_lastGoodSince`；`SetDynamicResolution` 夹取基准改为**会话初始尺寸** | 新增注入时钟单测（含恢复顺序的确定性断言） |
| **A4** | `SetBitrate` 在主路径（FFmpeg 厂商硬编）恒返回 false，却仍改写 `Settings.BitrateBps` 并上报"已降档"目标值 | 仅编码器接受时才改写 | 代码审阅 + 全量测试 |
| **A5** | Viewer 把 H.264 的 **1088** 行宏块填充当画面显示 | **初版方案无效**（`MF_MT_FRAME_SIZE` 在 `SetOutputType` 前后各报一次，无法区分真实/对齐尺寸）→ 改为 `StatsInfo` 增加 `w`/`h` 上报**编码输出真实尺寸**，Viewer 据此裁剪 | 实机 Viewer 状态栏显示 `1920×1080` |
| **A6** | 抖动缓冲深度计数过减 ⇒ 假欠载 + 自适应缓冲无谓加深 | `_queuedFrames -= dropped.Frames - dropped.Offset`；补 `Debug` 日志；暴露 `EffectiveTargetMs`/`AdaptiveExtraMs` | ⚠️ **仅单元/构建级验证，长会话复测未做** → §3.1 |
| **S6** | `TcpFrameConnection.Dispose` 在"对端断开"路径下变空操作 ⇒ socket/密钥/信号量只等 GC | `_disposed` 与 `_closed` 分离，幂等且无条件释放 | 新增 3 例连接生命周期单测 |
| **N10** | **启动瞬间用控件默认值覆盖已保存的用户设置**（"关闭隐藏到托盘/局域网发现/分辨率"勾了下次又没了）——审计时未识别，实机测试中发现 | 构造函数开头置 `_restoringSettings = true` 护住整段初始化 | 写配置 → 启动 → 回读未被改写 |
| — | 冒烟 Part5 只认源码树 Debug 路径 ⇒ 对 `dist` 产物必然 FAIL | 新增 `FindSignalingDll()` 按序兜底（含 `dist\publish\signaling`），并优先 Release | 冒烟 11/11 PASS |

---

## 3. 仍未解决的问题

### 3.1 A6：假欠载**未做长会话复测**（唯一的"改了但没验完"）

- **现状**：修复是一行（`Frames - Offset`），单测与构建通过，但**实跑只捕获到 2 次欠载**，样本不足以判定"假欠载是否消失"。
- **怎么验**：`set WINDOWSHARE_LOG_LEVEL=Debug` → 共享 ≥10 分钟（带声音）→ 看新增日志
  `抖动缓冲超上限，丢弃最旧块（N 帧，Offset=M）`，并把 `声音：播放中 XXms` 与
  `音频缓冲耗尽（第 N 次）` 对账。
- **判据**：正常播放时 `自适应额外` 应保持 0（除非真的欠载）；`BufferedMs` 不应为负或明显偏小。

### 3.2 24fps 目标实测只有 ~21fps（≈87%）——**未解释，且非本次引入**

- 修复前是 20.6–21.4fps，修复后是 20.8–21.0fps，**一致**，所以不是回归。
- 上屏＝解码 1:1、跳帧 0、积压 0，说明丢帧发生在**编码之前或链路更上游**。
- 已知线索：捕获侧现在按 24fps 放行，但 Host 侧"实际捕获 fps"与"编码 fps"都没单独量过。
- **下一步**：在 `RefreshOutputInfo`（Host 已有 `实际捕获 N fps` 提示）与
  `EncoderPipeline.GetCounters()` 上做一次并排采样，定位是捕获没到 24 还是编码没吃满。

### 3.3 P1 安全批次：**一行未动**（S1–S9，目标 1.6.0）

| ID | 严重度（前提） | 一句话 |
|----|----------------|--------|
| S1 | **CRITICAL（公网）** | `RelayToHost` 零认证/零成员校验：**不需密码，只要 6 位房间号**即可打断/劫持 WebRTC 协商（Host 侧会重建 sender 并 `SetAnswer`） |
| S2 | HIGH（公网） | `FindRoomByConnection` 对 viewer 连接也返回房间 ⇒ viewer 可自批审批、可 `StopSharing` 停掉整个房间、可冒充 Host 中继 |
| S3 | HIGH（公网） | 信令无 WSS 强制、CORS 任意来源+凭据、无认证/限速；凭据是**固定盐**可重放哈希。**ROADMAP 承诺的三条加固项均未落地** |
| S4 | MEDIUM | AAD/防重放能力位来自**未认证且不在 HMAC 内**的 `ver` ⇒ 在线中间人可静默降级（与 PROTOCOL 安全承诺不符） |
| S5 | MEDIUM | 加密会话中仍**接受明文控制帧** ⇒ 可注入 `StatsInfo.RttMs=1e9` 把全体码率钉到底（叠 A3 即永久降分辨率） |
| S7 | MEDIUM | 15s 握手总超时 < 60s 审批弹窗 ⇒ 首次连接必然超时，弹窗点了也没用（ROADMAP 的 Q7 实际无效） |
| S8 | MEDIUM | 未认证并发上限是软上限（TOCTOU）+ 认证前无读超时 + 审批弹窗洪水（不计限流） |
| S9 | MEDIUM | 信令 `Room.Viewers` 是普通 `Dictionary`，注释却写"线程安全" |

**建议顺序**：S1+S2（改动小、收益最大）→ S3 的 HTTPS 强制 + CORS allowlist → S4+S5（密码学承诺与代码对齐）→ S7+S8 → S9。

### 3.4 P2 候选（体验/清理）

| ID | 一句话 |
|----|--------|
| A7 | Viewer「（编码器欠产出）」在**静态桌面内容下必然误报**（低码率是 NVENC CBR 的正常行为，不是故障） |
| A8 | 「编码验证(写H.264)」是**持久化**设置且无大小上限 —— 现网 `recordings\` 已累计 **582MB / 13 个文件**（单文件最大 142MB） |
| S10 | 8 项低危：ADTS 头校验漏 bit、accept 前置语句在 try 外、接收任务可能挂起、对端可控 `DeviceName` 无长度/字符约束、AAC drain 无上限、`LoopbackAudioCapture` 正常退出不释放 WASAPI、`MFStartup` 引用计数失衡、`AudioRenderer.Stop` 潜在 CoTaskMem 泄漏 |

### 3.5 已知的文档欠账（不影响功能）

- 审计文档 `AUDIT-v1.5.2.md` 里 AI 与 S 各条的 `file:line` 是**修复前**的坐标，修复后多处已偏移（文中已声明"保留修复前位置以便对照"，但对不上号时会略费时间）。
- `docs/TESTING.md` 的测试类清单已按实际 15 个文件重列，但**逐类覆盖内容**是概写，未逐条核对到方法级。

---

## 4. 已提交内容（工作区干净）

已按批次拆成 **4 个提交**（未推送；提交信息里带完整动机与实测数据）：

| 提交 | 主题 | 规模 |
|------|------|------|
| `c196927` | `docs: v1.5.2 试运行审计 + 交接/工作日志 + 文档一致性修正` | 7 文件 +1443/−20 |
| `88b620a` | `fix: v1.5.2 P0 批次——托盘/拥塞恢复/码率上报/1088 填充/连接释放/启动设置覆盖` | 13 文件 +302/−72 |
| `cd469ef` | `perf: WGC 捕获侧帧率节流——消除 78% 白做的帧（累计分配 ↓89%）` | 3 文件 +56/−9 |
| `fdcd054` | `test: 新增连接生命周期、NV12 裁剪与拥塞恢复回归（207 → 215）` | 3 文件 +258 |

提交后复核：`dotnet build` 0 错误、`dotnet test` **215/215**、`git status` 干净。

**版本号**：`Directory.Build.props` 仍是 `1.5.2`（未动）。按原计划 P0 全通过才升 **1.5.3** ——
现在 P0 已闭环（除 A6 复测），可以现在升，或等 A6 验完再升。**这是需要你决定的**。

**推送**：4 个提交都还在本地。`git push` 前建议确认是否要一并升版本号，避免推送后再补。

---

## 5. 踩过的坑与注意事项（避免重复走弯路）

1. **两层帧率节流会互相抢帧。** 捕获侧与 `EncoderPipeline` 各自带相位与容差，同时开启会把 24fps 压到 **19.5fps**（我第一版就踩了）。现已由 `ShareSession` 保证**只有一处节流**。
   → **若将来给别的引擎加 `IFrameRateLimited`，必须确认 `FrameThrottleEnabled` 被关掉。**
2. **`OnFrameArrived` 里"不要这帧"也必须 `TryGetNextFrame` + `Dispose`。** 跳过它会让 FramePool 不再推帧、画面直接停。只能跳过后续的 `GetTextureFromSurface`/`CopyResource`/编码路径。
3. **`MF_MT_FRAME_SIZE` 不能用来分辨"真实帧尺寸"与"宏块对齐尺寸"** —— 它在 `SetOutputType` 前后各报一次（1080 → 1088）。真实尺寸只能由编码侧提供（现走 `StatsInfo.w/h`）。
4. **A5 的裁剪依赖 `StatsInfo.w/h`**：老版本 Host 不上报时 Viewer 按解码尺寸整幅渲染（回退到旧行为），这是有意设计。
5. **实机测试会改 `%APPDATA%\WindowShare\config\host-settings.json`**（我用它预置 1080p/关录制）。测试后记得还原——本次已还原。
6. **N10 修复后，构造函数期间的控件事件不再写设置。** 如果以后往构造函数里加"需要立刻持久化"的逻辑，注意 `_restoringSettings` 在构造结束前是 `true`。
7. **本机没有 dump 工具也不是死路。** 定位 A1 靠的是进程内探针：`GC.GetTotalMemory`（当前堆）+ `GC.GetTotalAllocatedBytes`（累计分配）+ 每 30s 强制 GC 后复测 ——
   **"能回收"= 分配放大 + 回收滞后；"回收不掉"= 真被持有**。这套组合足够区分，不必等 dump 工具。
   临时探针工程在 `%TEMP%\wsleak`（已删除），做法记录在 [DEVLOG.md S-c §1](DEVLOG.md#s-2026-10-06-c)。
8. **`docs/` 里中文在部分 PowerShell 读法下会显示成乱码**（`Get-Content` 未指定 `-Encoding UTF8` 时）。文件本身没问题，用 `-Encoding UTF8` 或 `read` 工具读。

---

## 6. 建议的明天顺序

1. ~~审阅并拆提交~~ **已完成**（§4，4 个提交，工作区干净）→ 改为：**决定是否 `git push` 与是否升版本号**。
2. 跑一遍 §1 的三条命令确认基线（5 分钟）。
3. **A6 长会话复测**（10 分钟，唯一"改了没验完"的）。
4. **查 §3.2 的 24fps→21fps 缺口**（Host 实际捕获 fps vs 编码 fps 并排采样）。
5. 开 P1 批次：先 S1+S2（信令鉴权），单独立 `docs/PROPOSAL-v1.6.0.md` 更合适——它们需要独立设计，不适合塞进补丁版。
