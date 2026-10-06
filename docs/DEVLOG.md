# 开发工作日志

> 按时间倒序记录每次开发/验证会话的**做了什么、验证到什么、留下了什么**。
> 与 [AUDIT-v1.5.2.md](AUDIT-v1.5.2.md)（问题清单与排期）分工：
> **审计文档记录"问题是什么"**，本日志记录**"这次会话把哪些问题推到了什么状态"**。
> **明天要接手请先看 [HANDOFF.md](HANDOFF.md)**（未提交改动、未解决问题、踩过的坑、建议顺序）。
>
> 约定：每条结论必须给出**可复核的证据**（命令输出、日志行、文件行号）。
> 没做到就写"未验证"，不留含糊表述。

## 索引

| 会话 | 日期 | 主题 | 结果 |
|------|------|------|------|
| [S-2026-10-06-c](#s-2026-10-06-c) | 2026-10-06 | A1 内存增长定位与修复 | **已定位**：WGC 不节流导致 78% 白做的帧；修复后分配 ↓89%、工作集走平 |
| [S-2026-10-06-b](#s-2026-10-06-b) | 2026-10-06 | v1.5.2 试运行审计 → P0 修复 → SDK 装好后验证 | 单测 215/215、冒烟 11/11、实机验证 4 项、新增缺陷 1 项 |

> **收工状态（2026-10-06 晚）**：上述两轮的改动已按 4 个提交入库
> （`c196927` docs → `88b620a` P0 修复 → `cd469ef` WGC 节流 → `fdcd054` 新增测试），
> 工作区干净、**尚未推送**。未解决问题与交接要点见 [HANDOFF.md](HANDOFF.md)。

---

<a id="s-2026-10-06-c"></a>
## S-2026-10-06-c ｜ A1 内存增长：定位与修复

**起点**：S-b 留下的最大欠账——A1「Host 共享期间内存持续增长」只有现象、无根因。
本机**没有** `dotnet-dump`/`dotnet-counters`/`dotnet-gcdump`（安装需网络），因此改用**自建探针**
在进程内取证。

### 1. 方法：自建探针 + A/B 对照

临时工程（`%TEMP%\wsleak`，验证后删除）引用 `WindowShare.Core`，用**生产路径**
`ShareSession` + `ShareOptions` 跑真实捕获+编码，每秒采样并每 10 秒打印：

| 指标 | 作用 |
|------|------|
| `GC.GetTotalMemory(false)` | 托管堆当前值 |
| 每 30s 强制 `GC.Collect(2, Forced, compacting)` 后再取 | **区分"未被回收"与"被持有"** |
| `GC.GetTotalAllocatedBytes` | 累计分配量（判断"分配放大"而非"泄漏"） |
| Gen0/1/2 次数 | 回收是否在推进 |
| 工作集 / 私有提交 | 进程侧观感 |
| `session.CaptureStats.Fps` | 捕获帧率 |

关键设计：**按引擎做 A/B**（`CaptureEnginePreference.Gdi` vs `Auto`=WGC），
因为两条路径共用同一 `EncoderPipeline`，差异只可能在捕获侧。

### 2. 定位结论

| 观测 | WGC（问题路径） | GDI（对照） |
|------|-----------------|-------------|
| 捕获帧率 | **~105 fps** | ~42 fps |
| 编码需要 | 24 fps | 24 fps |
| 被丢弃的捕获帧 | **~78%** | ~43% |
| 300s 内 Gen2 | **1 次** | **457 次** |
| 托管堆 | 37 → **161MB 单调** | 22→110MB **锯齿（回收掉）** |
| 工作集 | 313→466MB 后平台 | 锯齿，停后 198MB |
| 停止 + 强制 GC | **24.3MB** | **0.6MB** |

**根因**：`GraphicsCaptureEngine` **没有实现 `IFrameRateLimited`**（只有 GDI 实现），
动画内容下 WGC 按合成节奏推 ~105fps，而编码只取 24fps；被丢弃的 78% 帧在
`EncoderPipeline.Submit` 的节流判断**之前**已经走完
`TryGetNextFrame → GetTextureFromSurface(COM RCW) → TakePooledTexture → CopyResource →
ConvertBgraToNv12(每帧 2 个 VideoProcessor View RCW)`。托管分配率实测 **8.3MB/s**，
Server GC 的 Gen2 预算大 ⇒ 300 秒只回收 1 次 ⇒ 堆被推到 161MB，工作集到 466MB。

**不是真泄漏**：停止 + 强制 GC 后落到 24.3MB，全部可回收，无对象被长期持有。
24.3MB 是进程级 `D3D11DevicePool` 静态单例等的常驻底数。

### 3. 修复与一次**自造回归**

1. `GraphicsCaptureEngine` 实现 `IFrameRateLimited`。**关键细节**：必须仍然调用
   `TryGetNextFrame` 并 `Dispose` 释放 FramePool 缓冲（不看帧也要释放，否则池不再推帧、画面会停），
   但"不要这帧"时跳过后续全部昂贵工作。
2. **踩到的坑**：两层节流（捕获侧 + 编码管线）各自带相位与容差，串联后互相抢帧 ——
   实机实测上屏从上代基线 21fps **掉到 19.5fps**。这是"优化反而变差"的典型。
   修法：让**节流权威唯一** —— `ShareSession` 在捕获引擎实现 `IFrameRateLimited` 时
   置 `EncoderPipeline.FrameThrottleEnabled = false`。
3. 编码管线那层**必须保留**：冒烟 Part2b 是**直接喂管线**的（不经捕获引擎），
   靠它把 120fps 投喂节到目标帧率。冒烟实测仍正常丢弃 2144 帧，Part2b PASS。

### 4. 修复前后（同条件 1080p24，5 分钟）

| 指标 | 修复前 | 修复后 |
|------|--------|--------|
| 捕获帧率 | ~105 fps | **~42 fps** |
| 捕获总帧数 | 15,861 | **6,240** |
| **累计托管分配** | **~2,500 MB** | **272 MB**（↓89%） |
| Gen2 次数 | 1 | **18**（约 10–15s 一次） |
| 托管堆峰值 | 161 MB 单调 | **52 MB** 稳定锯齿 |
| 工作集 t=100s→300s | 404 → 466 MB | **336 → 356 MB（走平）** |

### 5. 回归验证

- 单测 **215/215**、冒烟 **11/11 PASS**（含 Part2b 直接投喂节流、Part3 WGC 捕获 153 帧）。
- 实机双端：上屏 **20.8–21.0fps、最大间隔 56–57ms、跳帧 0、队列积压 0** ——
  与修复前基线（20.6–21.4fps / 57–61ms）**一致，无回退**。

### 6. 被推翻的判断（再次提醒）

- **"持续增长 1–6MB/s、一小时 3.6–21GB"不成立**：工作集在 5 分钟内趋于平台（~466MB），
  那 1–6MB/s 只是**未被回收的托管垃圾**的瞬时斜率。严重度由"高"修订为"中"。
- 原先列的三个候选方向（`GetTextureFromSurface` 引用链、`UpdateSubresource` 路径、
  `CaptureFrame.Dispose` 语义）**都不是主因** —— 帧在到达那些代码之前就已经白做了 78%。

### 7. 遗留

- 编码 24fps 目标实测 ~21fps（≈87%）的缺口**不是本次引入**（修复前同样 20.6–21.4fps），
  根因在捕获之外，留作后续观察项。
- 带观看者时 Host 工作集 503MB（含 GopCache、每观看者发送队列等），与本次修复无关，
  若后续要压，属独立议题。

### 8. 环境备忘

- 本机**无** `dotnet-dump` / `dotnet-counters` / `dotnet-gcdump` / `dotnet-trace`，安装需网络。
  本次靠**进程内探针**（`GC` API + `Process` 计数器 + `psapi.EmptyWorkingSet`）完成定位，
  经验：**`GC.GetTotalMemory` 与 `GC.GetTotalAllocatedBytes` 一起看**，就能区分
  「内存泄漏」与「分配放大 + 回收滞后」，不需要 dump 工具。

---

<a id="s-2026-10-06-b"></a>
## S-2026-10-06-b ｜ v1.5.2 审计落地 + P0 修复与验证

**起点**：用户要求试运行 1.5.2 提改进意见（不改文件）→ 之后批准"整理审计文档 + 排期" → 装好 SDK 后继续。

### 1. 交付物

| 类型 | 文件 | 说明 |
|------|------|------|
| 新增 | [`docs/AUDIT-v1.5.2.md`](AUDIT-v1.5.2.md) | 试运行审计：实测数据 / A1–A6 缺陷 / S1–S10 安全发现 / 竞品对比 / 修复排期 / 原始数据附录 |
| 新增 | [`tests/WindowShare.Core.Tests/TcpFrameConnectionTests.cs`](../tests/WindowShare.Core.Tests/TcpFrameConnectionTests.cs) | 连接生命周期回归（S6） |
| 修改 | [`docs/ROADMAP.md`](ROADMAP.md) | 相关文档索引 + P0/P1/P2 队列；修正 Q7「已落地」与 `SetBitrate` 两处失实表述 |
| 修改 | [`docs/TESTING.md`](TESTING.md) | 136 → **215** 项；测试类清单按 15 个实际文件重列（原文档引用了不存在的类名）；冒烟 7 部分 → **11 项** |
| 修改 | [`docs/PROTOCOL.md`](PROTOCOL.md) | 音画同步语义改为 v1.5.2「自由上屏」；补 AAD 能力位与控制帧两条已知局限 |
| 修改 | [`README.md`](../README.md) | 3 处硬编码 `1.2.0` → `<版本>`；编码验证落盘提示；2 条已知限制；文档列表加审计链接 |

### 2. 代码修复（11 项）

| ID | 文件 | 改动 |
|----|------|------|
| A2 | `Host/TrayIcon.cs` | `NewNotifyIcon()` 改实例方法并写入 `nid.hWnd = _hwnd`；4 处 `Shell_NotifyIcon` 检查返回值并记 Win32 错误码 |
| A6 | `Core/Audio/AudioRenderer.cs` | 丢弃改按 `Frames - Offset` 扣减；新增 `EffectiveTargetMs`/`AdaptiveExtraMs`；健康回落补日志 |
| A6 | `Core/Audio/AudioPlaybackPipeline.cs` | 补两个透传属性（**首次编译失败的根因**，见 §4） |
| A6 | `Viewer/MainWindow.xaml.cs` | 音频 tooltip 改用实际缓冲目标；文案同步自由上屏语义 |
| A3 | `Core/Network/CongestionController.cs` | 分辨率回升独立成支 + 每档刷新 `_lastGoodSince`；`RecoverHoldSeconds` 提为常量 |
| A3 | `Core/Encoding/EncoderPipeline.cs` | `SetDynamicResolution` 夹取基准改为会话初始尺寸 `_configWidth/_configHeight` |
| A4 | `Core/Encoding/EncoderPipeline.cs` | `SetBitrate` 仅在编码器接受时才改写 `Settings.BitrateBps` |
| A5 | `Core/Network/AuthPayloads.cs` + `LanShareServer.cs` | `StatsInfo` 增 `w`/`h`：上报**编码输出真实尺寸** |
| A5 | `Viewer/MainWindow.xaml.cs` | 按上报尺寸裁剪宏块填充行、如实显示分辨率；会话切换时清零 |
| A5 | `Core/Decoding/MfVideoDecoder.cs` + `Core/Utils/Nv12ToBgra.cs` | 分离"缓冲高度/可见高度"；`Convert` 增 `visibleRows`（UV 偏移仍按缓冲高度）；修掉"声明偏大即整帧丢弃" |
| S6 | `Core/Network/TcpFrameConnection.cs` | `_disposed` 与 `_closed` 分离，`Dispose` 幂等且无条件释放；发送前早退 |
| — | `tools/WindowShare.SmokeTest/Program.cs` | 增 `FindSignalingDll()` 按序兜底（源码树 Release/Debug → `dist\publish\signaling` → 输出目录旁） |
| **N10** | `Host/MainWindow.xaml.cs` | **本轮实测新发现**：构造期写设置导致用户设置被控件默认值覆盖，见 §3 |

### 3. 本轮**新发现**的缺陷（审计时未识别）

**【N10·中】Host 启动瞬间用控件默认值覆盖已保存的用户设置**

- **怎么发现的**：给 A2 做端到端验证时，先写入 `MinimizeToTray=true` 再启动 Host，结果 Host **直接退出**而没有隐藏到托盘。回读配置文件发现 `MinimizeToTray` 已变回 `false`、`Discoverable` 变回 `false`、`ResolutionIndex` 从 2 变回 0。
- **证据（可复现）**：写入 `{ResolutionIndex:2, MinimizeToTray:true, Discoverable:true}` → 启动 Host → **8 秒后（未共享、未关窗）**配置文件即已被改写为控件默认值。
- **根因**：`MainWindow` 构造函数里 `InitializeSources()`（`:77`）与 `CboResolution.ItemsSource = ...`（`:78-79`）设置 `SelectedIndex` → 触发 `Source_SelectionChanged` / `Quality_SelectionChanged` → `SaveSettings()`，而这两个处理器只挡 `_restoringSettings`（此刻为 `false`）与 `_uiReady`（此刻还是 `false`，但 `Setting_Changed`/`Quality_SelectionChanged` 并不检查 `_uiReady`）。`ApplySettings()` 内部的 `_restoringSettings = true` 来得太晚——覆盖在它之前就已经发生。
- **与 1.3.1 的关系**：README 记录的 1.3.1 修复是"XAML 解析期触发 `SaveSettings` → 后声明控件为 null → `NullReferenceException` 崩溃"，当时**只修了崩溃，没修数据覆盖**；本次是同一根因的另一半。
- **影响**：用户每次启动 Host 都会丢掉"关闭时隐藏到托盘""局域网发现"等设置（表现为"勾了下次又没了"），且**静默无日志**。
- **修复**：构造函数开头即 `_restoringSettings = true` 护住整段初始化，末尾统一放行。
- **验证**：同一确定性流程复测 → 配置未被改写，`ResolutionIndex=2 / MinimizeToTray=True / Discoverable=True` 全部保持；随后 `WM_CLOSE` 进程存活（隐藏到托盘）。

### 4. 验证结果（SDK 装好后）

| 项 | 命令/方法 | 结果 |
|----|-----------|------|
| 构建 | `dotnet build -c Release -m:1` | ✅ 成功；0 错误。完整重建（`--no-incremental`）共 **4 条唯一警告，全部为改动前既有**——用 `git stash --include-untracked` 对照 HEAD 重建，同样 4 条（`MfVideoDecoder.cs:226`、`LanShareServer.cs:626`、`MainWindow.xaml.cs:602/612`）逐条一致，本次改动**未新增任何警告** |
| 单元测试 | `dotnet test -c Release` | ✅ **215/215 通过**（审计当时 207 + 新增 8） |
| 冒烟测试 | `dotnet run --project tools/WindowShare.SmokeTest -c Release` | ✅ **11/11 PASS**（Part5 修复后从 FAIL 转 PASS） |
| A2 托盘 | `PostMessage(WM_TRAY, WM_RBUTTONUP)` → 枚举 `#32768` 菜单窗口 | ✅ Host 进程弹出 `TrackPopupMenu` 菜单窗口（`hWnd=0` 时不可能发生） |
| A2 端到端 | 开始共享 → `WM_CLOSE` | ✅ 进程存活、共享继续（隐藏到托盘） |
| A5 分辨率 | 实启双端读 Viewer 状态栏 | ✅ `分辨率：1920×1080`（Host 编码输出 1920×1080；解码缓冲仍是 1088） |
| A6 音频 | 实跑日志 | ✅ 正常播放、无负 `BufferedMs`（本次捕获 2 次欠载，需长会话进一步观察） |
| N10 设置 | 写配置 → 启动 → 回读 | ✅ 未被覆盖 |
| 回归 | Viewer 帧率/跳帧/积压 | ✅ 21fps、跳帧 0、队列积压 0（与修复前一致，无回退） |

### 5. 过程中被推翻的判断（重要，避免后来人重走）

1. **A5 原判"越界读"不成立**。初版推导认为"解码缓冲只有 1080 行却按 1088 行整帧 `Marshal.Copy`"是越界读；实测证明 `ProcessOutput` 传 `NULL` 输出样本时**缓冲由 MFT 自己分配**，采样长度就是 1088 行 × 完整 NV12，不存在越界读。真实缺陷只是"把宏块填充行当画面显示并报成 1088"，严重度从"越界读"下调为"显示不准确"。
2. **A5 初版修复方案（改 `MfVideoDecoder` 的尺寸来源）无效**。以为 `MF_MT_FRAME_SIZE` 能区分"真实帧尺寸"与"对齐缓冲尺寸"，实测日志显示该属性在 `SetOutputType` **前后各报一次**（1080 → 1088），无法区分。最终改为**由编码侧经 `StatsInfo` 上报真实尺寸**、观看端据此裁剪。
3. **A5 未采用"把 `AuthResult.Width/Height` 改成编码输出尺寸"**。该字段当前是源几何尺寸，改它牵动老版本互通语义；`StatsInfo` 是既有扩展点（1.3.3 起就在加字段），且能覆盖会话中分辨率变化，故选它。
4. **A3 修复后恢复确实"更慢"**。审计文档原先只指出"分辨率回不去"，修复过程中量出完整恢复时间：每档 15s、逐档回升，从 35%+66% 的中间态恢复需 3×15s ≈ 45s，从最低档（20%+45%）约 90s。这是"消除振荡"的必然代价，已写入测试注释作为可执行文档。

### 6. 遗留与下一步

- **A1（Host 共享期间内存持续增长）仍未定位**：本轮 SDK 就绪后**尚未**做 ClrMD/dump 定位。审计文档给出的验收门槛是"1080p24、无观看者、无音频、3 分钟后漂移 < 50MB"。**这是下一个应做项**。
- **A6 需长会话复测**：本次只捕获到 2 次欠载，不足以判定"假欠载"是否消失。建议 `WINDOWSHARE_LOG_LEVEL=Debug` 跑 ≥10 分钟，用新增的 `Debug` 日志对账 `Offset`/`remaining`。
- **P1 批次未开始**：信令鉴权（S1/S2）、WSS/CORS（S3）、AAD 能力位（S4）、控制帧加密（S5）、握手超时（S7/S8）、`Room.Viewers`（S9）。
- **版本号未动**：`Directory.Build.props` 仍为 `1.5.2`；P0 全部通过后升 `1.5.3`（按计划，避免半成品被当作可发布版本）。

### 7. 环境备忘

| 项 | 值 |
|----|----|
| .NET SDK | 8.0.425（`C:\Program Files\dotnet\sdk`），满足 `global.json` 的 `8.0.100 / latestMinor` |
| 平台 | Windows、RTX 5060、1920×1080@165Hz |
| 实机验证用的发布方式 | `dotnet publish <项目> -r win-x64 --self-contained true -o <临时目录>`（验证后已删除临时目录） |
| UI 自动化 | `UIAutomationClient`/`UIAutomationTypes`；托盘验证用 `PostMessage` + 枚举 `#32768` |
| 注意 | 实机验证前会改 `%APPDATA%\WindowShare\config\host-settings.json`，**测试后需还原**（本轮已还原） |
