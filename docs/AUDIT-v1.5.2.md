# v1.5.2 试运行审计与修复排期

> 基于对 shipped v1.5.2（`Directory.Build.props` `<Version>1.5.2`，tag `v1.5.2`）的实机试运行、
> 全量静态代码审查与同类开源方案调研。
>
> **结论先行**：v1.5.2 的核心目标（上屏率不再被音频管线延迟钉死）**实测达成**——24fps 目标下实测
> 20.6–24.6fps 上屏、**跳帧 0、队列积压 0、最大帧间隔 57–61ms**。但试运行同时实锤了 **5 项运行时缺陷**，
> 其中 **Host 共享期间内存持续增长**与**托盘图标完全不可用**两项必须在下一个补丁版处理。
> 安全审查另发现信令服务器在公网暴露下的越权面（S1–S3），内网自用风险较低，排入 1.6.0。
>
> 本报告与 [PROPOSAL-v1.5.0.md](PROPOSAL-v1.5.0.md) 同构。每条结论标注复核方式：
> **[实测]** = 本机跑出数据；**[代码]** = 可由 `file:line` 直接复核；**[待定位]** = 已确认现象、根因未定位到行。
>
> **修复状态（2026-10-06）**：P0 批次已落地**并已验证**——`dotnet build` 0 错误、
> `dotnet test` **215/215 通过**、冒烟 **11/11 PASS**、A2/A5/N10 已实机复测通过。
> 详见 [DEVLOG.md S-2026-10-06-b](DEVLOG.md#s-2026-10-06-b) 与本报告 [§七](#七修复排期) 的「状态」列。
>
> ⚠️ **两处初版判断在验证中被推翻**（阅读正文时请以「修复」段落为准）：
> ① [A5](#a5中-viewer-把-h264-的-1088-行填充当画面显示且属越界读实测代码) 原判"越界读"**不成立**
> ——`ProcessOutput` 的输出缓冲由 MFT 分配，采样长度本就是完整 NV12，严重度下调为"显示不准确"；
> ② A5 初版修复（改 `MfVideoDecoder` 尺寸来源）**无效**——`MF_MT_FRAME_SIZE` 在 `SetOutputType`
> 前后各报一次（1080 → 1088），无法区分真实尺寸与对齐尺寸，最终改为由**编码侧上报**。
> 正文各条的 `file:line` 保留**修复前**位置以便对照；已修复项额外给出「修复」段落。

### 本轮**新发现**的缺陷（审计时未识别）

**【N10·中】Host 启动瞬间用控件默认值覆盖已保存的用户设置**

- **现象**：每次启动 Host 都会静默丢失「关闭时隐藏到托盘」「局域网发现」「分辨率档位」等设置，
  表现为"勾了下次又没了"。
- **证据（确定性复现）**：写入 `{ResolutionIndex:2, MinimizeToTray:true, Discoverable:true}` →
  启动 Host → **8 秒后（未共享、未关窗）**回读配置文件，三项均已被改写为控件默认值
  （`0 / false / false`）。
- **根因**：`Host/MainWindow.xaml.cs` 构造函数中 `InitializeSources()`（`:77`）与
  `CboResolution.ItemsSource = ...`（`:78-79`）设置 `SelectedIndex` → 触发
  `Source_SelectionChanged` / `Quality_SelectionChanged` → `SaveSettings()`；这两个处理器只挡
  `_restoringSettings`，而此刻它仍是 `false`（`ApplySettings()` 内部的置位来得太晚），
  于是把尚未初始化的控件默认值写回磁盘。
- **与 1.3.1 的关系**：README 记录的 1.3.1 修复是"XAML 解析期触发 `SaveSettings` → 后声明控件为
  null → 崩溃"，当时**只修了崩溃，没修数据覆盖**；本次是同一根因的另一半。
- **修复（已落地并验证）**：构造函数开头即置 `_restoringSettings = true` 护住整段初始化，
  末尾统一放行。复测：配置不再被改写，且随后 `WM_CLOSE` 进程存活（成功隐藏到托盘）。
- 详见 [DEVLOG.md §3](DEVLOG.md#3-本轮新发现的缺陷审计时未识别)。

## 目录

- [一、环境、手段与局限](#一环境手段与局限)
- [二、通过项（实测数据）](#二通过项实测数据)
- [三、实锤缺陷（A1–A6）](#三实锤缺陷a1a6)
- [四、安全与鲁棒性审查（S1–S10）](#四安全与鲁棒性审查s1s10)
- [五、实测观察与体验判断](#五实测观察与体验判断)
- [六、同类开源方案对比](#六同类开源方案对比)
- [七、修复排期](#七修复排期)
- [八、附录：原始数据](#八附录原始数据)

---

## 一、环境、手段与局限

### 1.1 环境事实（重要：先读这段）

| 项 | 值 |
|---|---|
| 被测版本 | 审计当时为 `dist\publish` 的 self-contained 1.5.2 产物；修复验证用本地 `dotnet publish` 产物 |
| 平台 | Windows、RTX 5060、1920×1080@165Hz |
| **.NET SDK** | **审计当时不存在**（`dotnet --list-sdks` 为空、无 `packs`、全机无 `csc.dll`）→ 随后补装 **8.0.425** |

**审计当时的硬约束**（下述章节的历史结论在此前提下成立；**修复阶段已解除**）：

1. **无法执行单元测试**。`tests\WindowShare.Core.Tests` 输出为 framework-dependent，缺
   `Microsoft.NETCore.App 8.0.0` 目标框架与 SDK，`testhost` 以 `hostpolicy.dll ... not found` 失败。
2. **无法编译任何自定义探针**。Roslyn 编译器与 reference pack 都不存在，`dotnet build` 报
   `No .NET SDKs were found`。因此审计阶段的 A1 根因未能定位（见 [A1](#a1-高-host-共享期间内存持续增长实测待定位)）。
3. **无法运行 `Host --autotest`**（同样需要 SDK 构建）。

**装好 SDK 后（8.0.425）的验证结果**：`dotnet build` 0 错误；`dotnet test` **215/215 通过**；
冒烟 **11/11 PASS**；A2 / A5 / N10 实机复测通过。完整记录见
[DEVLOG.md](DEVLOG.md#4-验证结果sdk-装好后)。

单测计数：静态核实 140 `[Fact]` + 67 `[InlineData]` = **207**（与 README 一致 ✅）；
本批新增 8 个用例后实测为 **215/215 PASS**。

### 1.2 试运行手段

- **端到端双端实跑**：以 `dist\publish\WindowShare.Host.exe` + `WindowShare.Viewer.exe` 实启，
  用 `UIAutomationClient`/`UIAutomationTypes` 驱动 Host 的「开始共享」与「⏹ 一键停止」，
  用 Viewer CLI（`--connect 127.0.0.1 --password <pwd>`）接入，全程读日志与状态栏断言。
- **冒烟测试**：`tools\WindowShare.SmokeTest` 的预构建产物是 framework-dependent，本机跑不起来；
  把 `dist\publish` 的自包含运行时叠加到其输出目录后，用 `dotnet WindowShare.SmokeTest.dll` 运行
  （**未修改仓库内任何文件**，全部在临时目录进行）。
- **内存隔离实验**：先关掉「共享系统声音」与「编码验证(写H.264)」再开始共享，**不接入任何观看者**，
  每 10–15 秒采样 `WorkingSet64` / `PrivateMemorySize64`，用于把变量收敛到「捕获+编码」单一路径。

> 试运行期间创建的所有临时文件、探针与截图均已删除；`git status` 干净。

---

## 二、通过项（实测数据）

### 2.1 全链路连通 [实测]

首次连接 4 秒内完成全链路，日志序（节选）：

```
[Whitelist] 已加载 3 个已批准设备
[Session]   启动共享: [屏幕] 显示器 1 (1920×1080), 3840p@24fps, 7300kbps
[Session]   源 1920x1080 小于目标宽度 3840，按源尺寸输出 1920x1080（不做上采样）
[Encoder]   h264_nvenc 就绪: 1920x1080@24, 目标 7300 kbps（真实 CBR）, GOP 48 帧, 硬件=True
[Audio]     系统声音共享已启动: Microsoft AAC Audio Encoder MFT, 48000Hz/2ch, 128kbps
[Session]   共享已开始: 房间号=DSST92, H.264 1920x1080@24fps 7300kbps, 编码器=h264_nvenc, 硬件=True
[LanServer] 新连接: 127.0.0.1
[LanServer] 会话加密已启用 (AES-256-GCM+AAD): L
[LanServer] 已为 L 补发缓存 GOP 42 帧（接入即出画面，无需等待下一个关键帧）
[LanServer] 观看者接入成功: L (127.0.0.1) 加密=True 系统声音=48000Hz/2ch aac-adts
```

### 2.2 v1.5.2 主目标达成：自由上屏 [实测]

Viewer「上屏节奏」周期诊断（每 10 秒一行）：

| 窗口 | 上屏帧数 | 均值 | 最大间隔 | 跳帧 | 队列积压 | 解码 |
|---|---|---|---|---|---|---|
| 1 | 246 | 24.6fps | 59ms | 0 | 0 | 246 |
| 2 | 207 | 20.7fps | 59ms | 0 | 0 | 453 |
| 3 | 211 | 21.1fps | 57ms | 0 | 0 | 664 |
| 4 | 206 | 20.6fps | 61ms | 0 | 0 | 870 |
| 5 | 208 | 20.8fps | 58ms | 0 | 0 | 1078 |
| 6 | 214 | 21.4fps | 59ms | 0 | 0 | 1292 |

**判定**：`上屏帧数 ≈ 解码帧数`（1:1），跳帧恒为 0，队列积压恒为 0 —— 即 v1.5.2 要解决的
「上屏率被音频管线延迟钉死」（v1.5.1 实测 56fps 解码只剩 8fps 上屏）已闭环。

**遗留观察**：目标 24fps 实际到手 20.6–21.4fps（约 86%），最大间隔 57–61ms（理想帧间隔 41.7ms）。
均值与解码 1:1 说明**丢帧发生在 Host 侧或更上游**，不在上屏环节；该缺口不属 v1.5.2 回归，
但值得后续用 [ROADMAP](ROADMAP.md) 已记录的「实际捕获 fps」口径持续跟踪。

### 2.3 其它通过项

| 项 | 结果 |
|---|---|
| 编码器选择 | `h264_nvenc`，硬件=True（1.5.0 厂商硬编链在生产路径生效） |
| 系统声音 | WASAPI loopback 采集 → AAC-LC 128kbps → 同一条 TCP + 同一把会话密钥；Viewer「声音：播放中 176ms 卡顿2」 |
| 加密 | AES-256-GCM + 帧头 AAD 绑定，Viewer 状态栏「加密：AES-256-GCM ✓」 |
| 接入即出画面 | GOP 补发 42 帧，首帧即 IDR，无需等下一个关键帧 |
| 自适应抖动缓冲 | 起播 42ms → 欠载 2 次 → 80ms → 120ms（行为与 v1.5.1 设计一致，但见 [A6](#a6-中-音频抖动缓冲深度计数过减假欠载代码)） |
| Viewer 全屏/退出、分辨率/码率/延迟状态栏 | 均正常刷新 |

---

## 三、实锤缺陷（A1–A6）

### A1【中】Host 共享期间托管堆被"白做的帧"推到 ~460MB 工作集 [实测][已定位][已修复并验证]

> **严重度修订**：原判"高·持续增长 1–6MB/s，一小时 3.6–21GB"**不成立**。定位后发现工作集在 5 分钟内
> **趋于平台（~466MB）**而非无界增长，且那 1–6MB/s 是**未被回收的托管垃圾**的瞬时斜率，不是泄漏速率。
> 真正的缺陷是"**为将被丢弃的帧做了全套工作**"导致的分配放大。原来的"越界读"推测（见 [A5](#a5中-viewer-把-h264-的-1088-行填充当画面显示-已修复并验证)）
> 同样已在验证中被推翻。

**原始现象**（隔离实验：无观看者、无音频、无录制，1080p24 + `h264_nvenc`）：

```
+10s   WS= 443.1MB  Private= 586.6MB
+40s   WS= 472.3MB  Private= 616.5MB
+70s   WS= 518.1MB  Private= 661.4MB      ← 早期采样的瞬时斜率
```

**定位结果（用自建探针实测，非推测）**：探针同时打印 `GC.GetTotalMemory`、Gen2 次数、
`GC.GetTotalAllocatedBytes` 与工作集，并按引擎做 A/B。

| 观测 | WGC（修复前） | GDI（对照） |
|------|---------------|-------------|
| 捕获帧率 | **~105 fps** | ~42 fps |
| 编码需要 | 24 fps | 24 fps |
| **被丢弃的捕获帧** | **~78%** | ~43% |
| 300 秒内 Gen2 次数 | **1** | **457** |
| 托管堆 | 37 → **161MB** 单调 | 22→110MB **锯齿**（回收掉） |
| 工作集 | 313 → 466MB，然后平台 | 325→400MB 锯齿，停后 198MB |
| 停止 + 强制 GC 后托管堆 | **24.3MB** | **0.6MB** |

**根因**：`GraphicsCaptureEngine` **不做帧率节流**（只有 GDI 实现了 `IFrameRateLimited`），
动画内容下 WGC 按合成节奏推送约 105fps，而编码只取 24fps。被丢弃的 78% 帧此前会走完
`TryGetNextFrame → WgcInterop.GetTextureFromSurface`（COM RCW）→ `TakePooledTexture` →
`CopyResource` → `GpuVideoProcessor.ConvertBgraToNv12`（每帧 2 个 `ID3D11VideoProcessor*View` RCW）
→ `EncoderPipeline.Submit` 的节流判断**在最后**。实测托管分配率约 **8.3 MB/s**，
而 Gen2 预算很大（Server GC），于是 300 秒只回收 1 次，堆被推到 161MB，工作集跟着涨到 466MB。

**为什么"不是真泄漏"**：停止共享 + 强制 GC 后托管堆落到 **24.3MB**（GDI 对照 0.6MB），
工作集 `EmptyWorkingSet` 后 11MB —— 全部是可回收垃圾，没有对象被长期持有。
24.3MB 这个底数是进程级 `D3D11DevicePool` 静态单例 + 池化缓冲等常驻部分，属预期。

**修复（已落地并验证）**——把节流上移到捕获侧，并保证"谁节流"只有一处权威：

1. `GraphicsCaptureEngine` 实现 `IFrameRateLimited`：在 `OnFrameArrived` 里**先取帧再判断**
   —— 必须调用 `TryGetNextFrame` 并 `Dispose` 释放 FramePool 缓冲（不看帧也必须释放，否则池不再
   推送新帧、画面会停），但在"不要这帧"时**跳过** `GetTextureFromSurface` / `CopyResource` / 整条编码路径。
2. **避免两层节流互相抢帧**：捕获侧与编码管线各自带相位与容差，串联后实测把 24fps 目标压到
   **19.5fps**。现在由 `ShareSession` 在"捕获引擎自身实现 `IFrameRateLimited`"时置
   `EncoderPipeline.FrameThrottleEnabled = false`，只有一处节流。
   编码管线那层**保留**，因为存在**直接喂管线**的调用方（冒烟 Part2b 的定速投喂）——
   冒烟实测仍正常节流（提交 2410 帧 → 编码 266 帧，丢弃 2144 帧）。
3. 顺带修正 `ICaptureEngine.IFrameRateLimited` 的注释（原文写"WGC/DXGI 由编码管线统一节流"）。

**修复后实测（同条件 5 分钟）**：

| 指标 | 修复前 | 修复后 |
|------|--------|--------|
| 捕获帧率 | ~105 fps | **~42 fps**（接受 24fps） |
| 捕获总帧数（300s） | 15,861 | **6,240** |
| **托管分配总量（300s）** | **~2,500 MB** | **272 MB**（↓ 89%） |
| 托管分配率 | 8.3 MB/s | **0.68 MB/s** |
| Gen2 次数（300s） | 1 | **18**（约每 10–15s 一次） |
| 托管堆峰值 | 161 MB 单调 | **52 MB**（稳定锯齿） |
| 工作集 t=100s → t=300s | 404 → 466 MB | **336 → 356 MB（基本走平）** |
| 停止后强制 GC | 24.3 MB | 24.3 MB（底数不变） |

**回归**：单测 215/215、冒烟 11/11 仍全部通过；实机双端上屏 **20.8–21.0fps、最大间隔 56–57ms**、
跳帧 0、队列积压 0 —— 与修复前基线（20.6–21.4fps / 57–61ms）**一致，无回退**。
带观看者时 Host 工作集 503MB（原有 GopCache/每观看者队列等开销所致，与本次无关）。

**遗留**：编码 24fps 目标实测约 21fps（≈87%）的缺口**不是本次修复引入**（修复前同样是 20.6–21.4fps），
根因在捕获之外的环节，留作后续观察项。

<details>
<summary>原始的候选方向（已被上述定位取代，保留以便追溯）</summary>

- `WgcInterop.GetTextureFromSurface` 的 COM 引用链；
- `EncoderPipeline.Submit` 的 `UpdateSubresource` 上限路径；
- `CaptureFrame.Dispose` 的 `TextureRelease` / `Texture` 二选一释放语义。

这三条经实测**都不是**主因：真实原因是帧在到达这些代码之前就已经"白做"了 78%。

</details>

---

### A2【高】托盘图标永远收不到鼠标消息（`hWnd = 0`）[代码]

**位置**：`src\WindowShare.Host\TrayIcon.cs:72-77`

```csharp
private static NOTIFYICONDATA NewNotifyIcon() => new()
{
    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
    hWnd = 0, uID = 0x5753, uCallbackMessage = WM_TRAY,
    szTip = "", szInfo = "", szInfoTitle = "",
};
```

构造函数已取得有效句柄 `_hwnd = new WindowInteropHelper(owner).EnsureHandle();`（`:35`）并挂了
窗口钩子（`:36-37`），但**从未把 `_hwnd` 写入 `NOTIFYICONDATA`**——全仓库 grep 无任何
`nid.hWnd =` 赋值；`NewNotifyIcon()` 是 `static`，也拿不到实例字段。
`Shell_NotifyIcon(NIM_ADD, ref nid)`（`:44`）用的就是 `hWnd = 0`。

**后果**：Shell 需要有效窗口句柄才能投递 `uCallbackMessage`（`WM_TRAY = WM_APP + 0x51`），
因此 `WndProc`（`:79-97`）的 `if (msg != WM_TRAY) return IntPtr.Zero;` **永不成立**，托盘菜单
「打开主窗口 / 停止共享 / 退出」全部不可达（README「功能一览」第 11 项与 1.3.0 更新日志均声称可用）。
`Shell_NotifyIcon` 的返回值（`bool`）在 `NIM_ADD`/`NIM_MODIFY`/`NIM_DELETE` 三处**全部被丢弃**，
失败时无任何日志。

**放大风险**：`HostSettings.MinimizeToTray` 默认 `true`（`src\WindowShare.Core\Utils\HostSettings.cs:22`），
且共享中关窗会 `e.Cancel = true; Hide();`（`src\WindowShare.Host\MainWindow.xaml.cs:1054-1068`）——
一旦用户按默认行为关窗，**窗口隐藏、托盘菜单不可用、无法唤回也无法停止共享**，只能结束进程。
本次试运行的 `host-settings.json` 恰好为 `MinimizeToTray: false`，所以未在实跑中暴露。

**最小修复**：`NewNotifyIcon` 改为实例方法（或接收 `IntPtr hwnd` 参数），写入 `nid.hWnd = _hwnd;`；
三处 `Shell_NotifyIcon` 检查返回值并在失败时记 `Warn`（带 `Marshal.GetLastWin32Error()`）。

**修复（已落地，未验证）**：`NewNotifyIcon()` 改为实例方法并写入 `nid.hWnd = _hwnd`（现 `TrayIcon.cs:88`）；
`NIM_ADD`/`NIM_MODIFY`（两处）/`NIM_DELETE` 全部检查返回值，失败记 `Warn` 并带 Win32 错误码与 `hwnd`；
`NIM_ADD` 失败时不再置 `_added`（避免后续对不存在的图标做 MODIFY）。

---

### A3【中】拥塞降档是单向棘轮：降了分辨率就再也回不去 [代码]

**位置**：`src\WindowShare.Core\Network\CongestionController.cs:92-162`

```csharp
if (healthy && _bitrateStep > 0)          // :136  ← 回升分支的入口条件
{
    if ((now - _lastGoodSince).TotalSeconds < 15)
        return new ControlDecision(null, null, null, "观察恢复中");
    _consecutiveDegrades = 0;
    _bitrateStep--;
    if (_bitrateStep == 0 && _resolutionStep > 0)   // :143  ← 分辨率回升嵌在这里面
    {
        _resolutionStep--;
        ...
    }
}
```

**两个独立缺陷**：

1. **分辨率永远回不去**：`_resolutionStep--` 只存在于 `_bitrateStep > 0` 的分支内。
   一旦到达 `_bitrateStep == 0 && _resolutionStep > 0`（这是降档过程的自然中间态：
   每次拥塞先降码率，连续两次后才降分辨率，见 `:121-128`），`healthy` 时 `:136` 的条件为假，
   整个分支被跳过 ⇒ `_resolutionStep` 永久停在 1 或 2，输出分辨率**在本会话内永久钉在 66%/45%**。
   而此时 `IsDowngraded`（`:86-89`）恒为 `true`，UI 只能一直显示"已降档"。
   这与类注释 `:11-12`「恢复：RTT 良好且无丢帧持续 15 秒 → 逐级回升，永不超初始值」不符。

2. **15s 节流失效（振荡）**：回升分支内**从不刷新 `_lastGoodSince`**——该字段只在拥塞分支
   `:112` 与 `healthy && _bitrateStep == 0` 分支 `:159` 被赋值。因此"持续良好 15 秒才回升"只对
   **第一档**成立：15s 过后，之后每 2 秒（`:97` 的评估间隔）都会再降一档，从最低档（20%）
   爬回 100% 只需约 5 档 × 2s ≈ **10 秒**，随即再次拥塞 → 形成降档/回升振荡，而非文档承诺的
   逐级平滑恢复。现有单测 `CongestionControllerTests.cs` 只断言取值范围，覆盖不到该节流。

**最小修复**：① 新增独立的分辨率回升分支，条件为 `healthy && _resolutionStep > 0 && _bitrateStep == 0`，
同样受 15s 节流；② 在**任何**成功回升的动作后刷新 `_lastGoodSince`；③ 补两条注入时钟的单测
（"降档后分辨率回到初始"、"回升每 15s 才一档"）。

**修复（已落地，未验证）**——三处联动，只改控制器还不够：

1. `CongestionController.Evaluate` 的回升分支重写（现 `:142`）：入口条件改为
   `healthy && (_bitrateStep > 0 || _resolutionStep > 0)`；**每成功回升一档都刷新 `_lastGoodSince`**
   （`:151`、`:163`），恢复真正的 15s/档节流；分辨率回升独立成支（`:162`），不再受 `_bitrateStep > 0` 约束。
   15s 常量提为 `internal const int RecoverHoldSeconds`（`:25`）供测试引用。
2. `EncoderPipeline.SetDynamicResolution` 的夹取基准从 `Settings.Width` 改为**新增的会话初始尺寸**
   `_configWidth/_configHeight`（现 `:214`）：`Settings.Width` 会被 `UpdateOutputSize` 改写成降档值，
   用它夹取会把"恢复到原始分辨率"的请求夹回当前值并因相等而提前 return——这是分辨率回不去的第二重成因。
3. 新增单测 `Recovery_RestoresResolution_FromMidStepState` 与 `Recovery_PacesOneStepPerHoldWindow`
   （`tests\WindowShare.Core.Tests\CongestionControllerTests.cs`）。

> ⚠️ 原计划的"RTT 上界裁剪"**未纳入本批**：它同时属于 [S5](#s5medium加密启用后仍接受明文控制帧) 的
> 输入校验范畴（`RttMs` 由对端提供），留到 P1 与 S5 一起做，避免在 P0 批次里夹带安全语义变更。

---

### A4【中】`SetBitrate` 在 1.5.2 的主编码路径上是空操作，但仍上报"已生效" [代码]

**位置**：`src\WindowShare.Core\Encoding\FfmpegVideoEncoder.cs:250-255`

```csharp
/// <summary>v1 记日志不生效（NVENC 动态重配不在 v1 范围，to1.5.0.md）；Host 已有 GOP 补发与码率梯子兜底</summary>
public bool SetBitrate(int bitrateBps)
{
    Logging.Logger.Info("Encoder", $"{EncoderName} 动态码率 v1 暂不生效（请求 {bitrateBps / 1000} kbps，保持初始值）");
    return false;
}
```

**为什么这是 1.5.2 的现实问题**：`VideoEncoderFactory` 在 1.5.0 后**优先选 FFmpeg 厂商硬编**，
本机实测生产路径就是 `h264_nvenc`（[2.1](#21-全链路连通-实测) 日志）。而
`EncoderPipeline.SetBitrate`（`:154-160`）在调用前就改写了 `Settings.BitrateBps`，并打印
「动态码率 → N kbps (设置失败)」；`LanShareServer` 仍按 `_session.Options?.BitrateBps` 上报
`AuthResult.TargetBitrateBps`（`:516`）与周期 `StatsInfo`（`:626`），Viewer 状态栏据此显示
「实测 / 目标」。**结果是 UI 显示一个编码器并不遵守的目标值。**

**与 A3 叠加的后果**：拥塞时唯一真正生效的动作是**不可逆的降分辨率**（A3-1），
既没有降码率也没有降帧率。这解释了「弱网之后画质再也回不来」这一类现象。

**最小修复**：`EncoderPipeline.SetBitrate` 在编码器返回 `false` 时**不得**改写 `Settings.BitrateBps`，
且不上报"已降档"目标值（`CurrentBitrateBps`/`StatsInfo` 走实际生效值）；Host/Viewer 文案体现
"本机编码器不支持动态码率"。FFmpeg 路径真正支持重配（ROADMAP 既定项 `av_opt_set`）留待后续。

**修复（已落地，未验证）**：`EncoderPipeline.SetBitrate`（现 `:165-186`）改为**先调用编码器**，
仅在返回 `true` 时才改写 `Settings.BitrateBps` 并记「已生效」；返回 `false` 时记 `Warn` 说明
"保持 N kbps（本机编码器不支持动态重配；拥塞时仍会降分辨率）"。这样
`Settings.BitrateBps`（`AuthResult.TargetBitrateBps` 与周期 `StatsInfo` 的唯一出处）始终等于
编码器实际使用的码率，也保证分辨率切换重建编码器时恢复正确的档位。
`FfmpegVideoEncoder.SetBitrate`（`:251-255`）保持恒 `false` 的诚实语义不变。

---

### A5【中】Viewer 把 H.264 的 1088 行填充当画面显示 [实测][代码][已修复并验证]

**实测**：Viewer 状态栏显示 `分辨率：1920×1088`，而 Host 的编码输出是 1920×1080
（Host 日志 `编码管线就绪: 1920x1080 @ 24fps`）。

**代码链**：

1. `src\WindowShare.Core\Decoding\MfVideoDecoder.cs` 的 `TryReadOutputSize()` 从 MFT
   **当前输出类型**读 `MF_MT_FRAME_SIZE` 作为 `OutputWidth/OutputHeight`。
2. `Emit()` 用同一个 `OutputHeight` 做三件事：判断缓冲是否够长（`needed = w * h * 3 / 2`）、
   整帧 `Marshal.Copy`、以及 `Nv12ToBgra.Convert(nv12, w, h, bgra)` 的转换与输出尺寸。
3. `src\WindowShare.Viewer\MainWindow.xaml.cs` 的 `RenderFrame` 用 `frame.Height` 建
   `WriteableBitmap` 并整幅 `WritePixels`；XAML 是 `<Image x:Name="VideoImage" Stretch="Uniform"/>`
   （`MainWindow.xaml:95`）。

**后果**：底部 8 行宏块填充被当作真实画面显示，并参与等比缩放（画面高度/宽高比有 ~0.7% 偏差）；
状态栏把分辨率报成 1920×1088。

> ⚠️ **推翻初版判断**：本篇初稿曾断言这同时是"越过有效数据读取"（越界读）。
> 验证阶段确认**不成立**——`MfVideoDecoder` 调 `ProcessOutput` 时传 `NULL` 输出样本，
> **输出缓冲由 MFT 自行分配**，实测该缓冲本就是完整 1088 行 × NV12，`Marshal.Copy` 长度与之匹配。
> 因此严重度从"越界读"下调为"显示不准确"，[A1](#a1-高-host-共享期间内存持续增长实测待定位)
> 的内存增长也因此**不能**用这条解释。

**为什么"解码器自报尺寸"无法解决**：实测日志（同一次连接内）
`解码输出分辨率: 1920x1080` 紧接着 `解码输出分辨率: 1920x1088` —— `MF_MT_FRAME_SIZE` 在
`SetOutputType` **前后各报一次**（先是真实帧尺寸，后是对齐后的缓冲尺寸），**同一属性无法区分二者**。
唯一可靠的来源是**编码侧**：`EncoderPipeline` 的 `_outWidth/_outHeight`（即 Host 日志里的 1920×1080）。

**修复（已落地并验证）**：

1. `Core/Network/AuthPayloads.cs`：`StatsInfoPayload` 增加 `w` / `h`（编码输出真实尺寸）。
2. `Core/Network/LanShareServer.cs`：周期 `StatsInfo` 填 `_session.OutputSize`。
   *（未采用"改 `AuthResult.Width/Height`"：该字段当前语义是源几何尺寸，改动牵动老版本互通；
   `StatsInfo` 是 1.3.3 起就在扩展的既有通道，且能覆盖会话中分辨率变化。）*
3. `Viewer/MainWindow.xaml.cs`：`StatsUpdated` 在 UI 线程记下上报尺寸；`RenderFrame` 经
   `VisibleSize()` 决定渲染行数，仅当「上报尺寸宽度与解码宽度一致、高度更小、差值 ≤32 行（宏块对齐量）、
   且为偶数」时才裁剪——避免把真实的分辨率变化误裁。裁剪只改行数（BGRA 行距不变），无需重排像素。
   另有校验失败（老版本 Host / 尺寸异常）时按解码尺寸整幅渲染，行为与旧版一致。
   会话拆除时清零上报尺寸，避免用上一会话的尺寸裁新画面。
4. **顺带修掉一个黑屏风险**：`Emit()` 原先在"缓冲小于输出类型声明尺寸"时**整帧丢弃**；
   现改为按缓冲反推可渲染行数（`length * 2 / 3 / w`）继续渲染。
5. `Core/Decoding/MfVideoDecoder.cs` + `Core/Utils/Nv12ToBgra.cs` 仍保留"缓冲高度/可见高度分离"
   与 `visibleRows` 参数（本机未用上，但为将来能拿到真实尺寸的解码器留好接口）；
   新增 3 例单测覆盖**UV 平面偏移不随可见行数漂移**这一最容易踩的坑。

**实机验证结果**：Viewer 状态栏 `分辨率：1920×1080`（Host 编码输出 1920×1080，解码缓冲仍为 1088），
帧率 21fps、跳帧 0、队列积压 0，无回退。

---

### A6【中】音频抖动缓冲深度计数过减 → 假欠载 [代码]

**位置**：`src\WindowShare.Core\Audio\AudioRenderer.cs`（修复前 `:180`；修复后 `:184-193`）

- 入队按整块记账：`_queuedFrames += frames;`（`:179`）
- 写设备按实际写出扣减：`_queuedFrames -= written;`（`:427`），而队首块可能是**部分消费**
  （`:420` `head.Offset += take;`）
- **丢最旧时却按整块扣减**：

```csharp
while (_queuedFrames > _maxBufferFrames && _queue.Count > 1)   // :184
{
    var dropped = _queue.Dequeue();
    _queuedFrames -= dropped.Frames;      // :187（修复前）← 应为 dropped.Frames - dropped.Offset
}
```

**后果**：渲染循环每次只写设备腾出的空间（约 10ms ≈ 480 帧），而解码块是 1024 帧
（AAC 一帧固定 1024 采样），因此队首块**几乎总是 `Offset > 0`**。一次突发积压触发丢弃后，
计数就被**永久少算**约 `Offset`（数百帧）且不会自我纠正，`_queuedFrames` 可被推到负值。
此后 `:344` 的 `if (queuedNow <= 0)` 会在队列**仍有真实数据**时判定"缓冲耗尽"：

```
[Warn] [Audio] 音频缓冲耗尽（第 1 次），重新蓄水 80ms（自适应缓冲 +40ms）
[Warn] [Audio] 音频缓冲耗尽（第 2 次），重新蓄水 120ms（自适应缓冲 +80ms）
```

即触发无谓的重灌、把自适应缓冲往 +200ms 上限推，同时 `BufferedMs`（`:77`，UI 状态栏「声音：播放中 XXms」）显示失真值。

**与实测的关系**：本次实跑在第 1、2、3 次欠载的时序与步进（+40ms/次）上与自适应加深逻辑
（修复前 `:342-350`，修复后 `:352-360`）完全一致；
**该计数缺陷是这批"欠载"的高度可疑共同成因，但未被证明为唯一成因**——需先落地一行修复再复测确认
（这也是把它列为 P0 的原因：修复成本一行，可立刻证伪/证实）。

**最小修复**：丢弃处改为 `_queuedFrames -= dropped.Frames - dropped.Offset;`，并在丢弃处补一条
带 `Frames`/`Offset` 的 `Debug` 日志以便复测对账。

**修复（已落地，未验证）**：丢弃处改为 `var remaining = dropped.Frames - dropped.Offset;`
再按 `remaining` 扣减（现 `AudioRenderer.cs:186-193`），并补 `Logger.Debug` 记录
`remaining`/`Offset`/扣减后的帧数。另按可观测性建议暴露 `EffectiveTargetMs` / `AdaptiveExtraMs`
（现 `:83-86`），Viewer 的音频 tooltip 改用实际值而非硬编码的 `DefaultTargetLatencyMs`；
健康回落（`extra` 递减）补一条 `Info` 日志——此前只有加深打 `Warn`，
"目标是否被遵守"在现网无法验证。

**预期行为变化（实跑复测时按此判断）**：
- 正常播放时**不应**再出现 `自适应额外 > 0`（除非真的发生欠载）；
- `声音：播放中 XXms` 的 `XX` 应为正且与队列实际深度相符；
- 若仍出现欠载，新增的 `Debug` 日志（需 `WINDOWSHARE_LOG_LEVEL=Debug`）会打印被丢块的
  `Offset` 与扣减后的帧数，可直接判断是"计数仍错"还是"真的缓冲耗空"。

> **未加单测**：`AudioRenderer.Enqueue` 在 `!_running` 时直接返回，而 `_running` 只能由
> `Start()`（真实 WASAPI 渲染设备）置位，因此该计数逻辑在单测里**没有可用的接缝**。
> 为不引入虚假的测试覆盖，本次未改结构来迁就测试；改为在实跑复测中用新增的 `Debug` 日志对账。

---

## 四、安全与鲁棒性审查（S1–S10）

> 说明：S1–S3 的严重度**以信令服务器暴露到公网为前提**（本项目本意是局域网工具，但
> `DEPLOY.md` 提供公网/WSS 部署路径，故必须按公网可达评估）。内网自用场景下风险显著降低，
> 但 S4/S5/S6 等 LAN 会话内的问题**与是否公网无关**。

### S1【CRITICAL（公网）】`RelayToHost` 零认证：仅知 6 位房间号即可劫持媒体协商

`src\WindowShare.Signaling\Hubs\SignalingHub.cs:169-175`：

```csharp
public Task RelayToHost(string roomCode, string type, string payload)
{
    var room = _rooms.Find(Normalize(roomCode));   // 只校验房间号存在
    if (room == null) return Task.CompletedTask;
    return Clients.Client(room.HostConnectionId).SendAsync("RelayFromViewer",
        FindViewerIdByConnection(), type, payload);   // viewerId 可为 null
}
```

不校验密码、不校验调用者是否该房间成员、不校验是否已批准。payload 直达 Host 的
`src\WindowShare.Host\MainWindow.xaml.cs:792-850` `OnRelayFromViewer`：

- `"webrtc-request"`（`:798-825`）：释放并重建 `_webRtcSender`、`_session.AddSink(_webRtcSink)`。
  sender 是**单例共享**的，因此一条伪造请求就能拆掉正在服务合法观看者的 WebRTC 流（DoS）。
- `"answer"`（`:827-839`）→ `_webRtcSender?.SetAnswer(payload)`；`"ice"`（`:841-843`）→
  `AddIceCandidate(payload)`。SDP/ICE 无签名、不校验来源已批准 ⇒ 攻击者在协商窗口内先发
  `webrtc-request` 逼出 Host offer，再发自己构造的 `answer`，可把媒体协商到攻击者端点。

**前提**：不需要密码，只需房间号（6 位、31 种字符 ≈ 30 bit，服务端**无限速**，可批量枚举）。
**最小修复**：服务端校验 `Context.ConnectionId ∈ room.Viewers` 且已批准，并要求 `viewerId != null`；
Host 侧 `OnRelayFromViewer` 先比对 `viewerId` 是否在已批准集合内，未批准直接丢弃。

### S2【HIGH（公网）】Host 专用方法无角色隔离（viewer 即 host）

`SignalingHub.cs:187-198` 的 `FindRoomByConnection()` 在"调用者是 host"之外，
**只要调用者是该房间的 viewer 也返回房间**。用它鉴权的三个方法因此对 viewer 开放：

| 方法 | 位置 | viewer 可做的事 |
|---|---|---|
| `ApproveViewer` | `:71-102` | 自批自己的 `viewerId` → 服务端立即下发 `HostInfo`（含 `lanEndpoints`，`:84-89`），**绕过 Host 审批** |
| `StopSharing` | `:105-112` | `UnregisterByCode` + 广播 `HostStopped` → 终止整个房间、踢掉其他观看者 |
| `RelayToViewer` | `:115-122` | 冒充 Host 向同房间其他 viewer 注入伪造 SDP/ICE |

**前提**：需房间密码（与 LAN 密码同一个 8 位口令）。但 viewer 恰恰是"不该持有 host 权限"的角色
—— 典型 broken access control（viewer → host 权限提升）。
**最小修复**：拆出 `FindHostRoomByConnection()`（只匹配 `HostConnectionId`）供 Host 方法使用；
服务端维护 viewer 的 `Approved` 标志，不依赖 Host 客户端自觉。

### S3【HIGH（公网）】传输与加固缺失：ROADMAP 的三条加固项均未落地

`src\WindowShare.Signaling\Program.cs`（全文 32 行）：

- `:9-10` CORS：`AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true)`
  —— 任意来源 + 允许凭据。
- 全文**没有** `UseHttpsRedirection()` / `UseHsts()`、没有 `AddAuthentication`/`[Authorize]`、
  没有 IP 限速或连接数上限；`:17` 的 `/stats` 向匿名者暴露房间总数。
- `HashPassword`（`SignalingHub.cs:29-34`）= `SHA256(PBKDF2(password, "wsh1-signaling", 50k, 32))`，
  `JoinRoom`（`:129-144`）直接比较 —— **固定盐、无 nonce/挑战** ⇒ 明文信道被动嗅探到的哈希
  可直接重放加入房间；固定盐还意味着 31^8 的离线穷举**一张表通杀所有部署**。

逐条对照 [ROADMAP.md](ROADMAP.md) 的「信令加固：WSS 默认、CORS 收紧、密码哈希服务端限速」：
**三条全部未落地**。`DEPLOY.md:29-34` 仍把 `dotnet run --urls http://0.0.0.0:5000` 当常规用法。

**最小修复**：① 非 loopback 且非 https 时拒绝启动（或启用 `UseHttpsRedirection().UseHsts()`）；
② CORS 改配置化 allowlist；③ `JoinRoom`/`RegisterHost` 加每 IP 滑动窗口限速与连接数上限；
④ 认证改为"服务端随机 nonce 挑战 → 客户端回 `HMAC(passwordKey, nonce‖roomCode)`"，使线路值不可重放。

### S4【MEDIUM】AAD 绑定与防重放可被未认证方静默降级（与 PROTOCOL 承诺不符）

能力位来源是**明文、未认证、且不在 HMAC transcript 内**的字段：

- `src\WindowShare.Core\Network\AuthPayloads.cs:18` `AppVersion`（AuthRequest 明文）
- `src\WindowShare.Core\Network\AuthRateLimiter.cs:94-100` `PeerCapability.SupportsAadBinding(peerAppVersion)`
- `src\WindowShare.Core\Network\LanShareServer.cs:394` → `:490 conn.UseAadBinding` → `:512 AadBindingEnabled`
- 证明输入 `AuthPayloads.cs:132-143` `ComputeProof(key, salt, deviceId, hostPub, clientPub)`
  —— **不含版本/能力位**（已逐字核对，确认无 transcript hash）
- Viewer 侧 `LanShareClient.cs:287 conn.UseAadBinding = result.AadBindingEnabled`（AuthResult 同样明文无 MAC）

⇒ 在线中间人（同网段 ARP 欺骗等）只需**删掉 AuthRequest 的 `ver`**，或把明文 AuthResult 的 `aad`
置 false，两端就各自关闭帧头 AAD 绑定与序号校验（`TcpFrameConnection.cs:212-221` 整段在
`UseAadBinding` 条件下），**全程无任何报错**。此后可篡改帧头并重放密文帧。
而 `PROTOCOL.md:97` 声明"不知道密码无法伪造/替换公钥"、`:107` 把 AAD+防重放描述成协商后的
安全增强 —— **协商本身不受认证保护**。

（注：真正的"明文回退"不存在 —— `LanShareServer.cs:474-504` 在 `hostPub` 提供时缺 `clientPub`
一律拒绝。这一条是**能力降级**，不是加密降级。）

**最小修复**：对当前 ≥1.3 的对端**恒定启用** AAD（`ver` 只用于"拒绝过旧对端"），
或把能力字节/协议版本并入 `ComputeProof` 的 HMAC 输入，使降级立即导致证明校验失败。

### S5【MEDIUM】加密启用后仍接受明文控制帧

`src\WindowShare.Core\Network\TcpFrameConnection.cs:149-152`：

```csharp
private static bool ShouldEncrypt(MessageType type) => type is MessageType.VideoFrame
    or MessageType.AudioFrame or MessageType.RawFrame or MessageType.StatsInfo;
```

接收侧 `:203-232` 只在 `(hdr.Flags & Encrypted) != 0` 时解密/校验序号，否则**原样把明文 payload
交给上层**。即加密会话中 `Ping`/`Pong`/`Bye`/`KeyframeRequest`/`ShareStopped` 既不发密文、也接受明文。
在线中间人可注入任意 TCP 段从而：① 伪造 `StatsInfo{RttMs=1e9}` →
`LanShareServer.cs:557-566` 无上界直接喂 `_controller.OnRttSample`，把**所有**观看者打到最低档
（叠加 A3 就是永久降分辨率）；② 伪造 `KeyframeRequest` → 逼 Host 重发整段 GOP（`:259` 去重仅 1s）；
③ 伪造 `Bye` 直接断会话。

**最小修复**：`_encryption != null` 时非握手类型必须带 `Encrypted` 标志，否则按协议违例断开；
控制帧也纳入加密或强制其 payload 固定长度。另对 `RttMs` 加上界校验（如 0..5000）。

### S6【MEDIUM】`Dispose` 在正常断连后变成空操作 → socket/AES 密钥/信号量泄漏

`TcpFrameConnection.cs:276-285`：

```csharp
public void Dispose()
{
    if (_closed) return;          // ← 读循环已把 _closed 置 true
    _closed = true;
    _readCts?.Cancel();
    _sendLock.Dispose();
    try { _stream.Dispose(); } catch { }
    try { _client.Dispose(); } catch { }
    (_encryption as IDisposable)?.Dispose();
}
```

而读循环的 `finally`（`:242-249`）在**任何**退出原因（对端关闭、协议违例、解密失败、取消）下
都执行 `_closed = true;`。因此最常见的「对端断开」路径下，`ClientSession.Close()`（`LanShareServer.cs:749-754`）
调用的 `Connection.Dispose()` **直接 return**，`NetworkStream`/`TcpClient`/socket 句柄、
`SemaphoreSlim`、`_readCts`、`AesGcmSession`（含 CNG 句柄与 `_key` 副本）全部只等 GC 终结器。
配合"观看端每 15s 重连"这类循环，句柄会持续累积。

**最小修复**：把"协议关闭标志"与"已释放标志"拆成两个字段；`Dispose` 用 `Interlocked.Exchange`
幂等，且**无条件**执行资源释放。

**修复（已落地，未验证）**：新增 `private int _disposed`（`TcpFrameConnection.cs:27`）与协议关闭标志
`_closed` 分离；`Dispose`（现 `:280-299`）用 `Interlocked.Exchange(ref _disposed, 1) != 0` 做幂等门闩，
无条件 `Cancel()`/`Dispose()` `_readCts`、`_sendLock`、`_stream`、`_client`、`_encryption`，
每个释放各自 `try/catch`（释放路径不应因单个资源失败而中断）。`Send`/`SendAsync` 增加
`Volatile.Read(ref _disposed) != 0` 早退，避免释放后仍在已释放的 `SemaphoreSlim` 上等待抛
`ObjectDisposedException`。新增 `TcpFrameConnectionTests`（3 例：对端断开后 Dispose 仍关闭连接、
Dispose 幂等且发送安全、Dispose 后 `IsConnected` 为假）。

### S7【MEDIUM】15s 握手总超时 < 60s 审批弹窗：Q7 修复实际无效

顺序上 Host 在**发出 `AuthChallenge` 之前**就进入审批（`LanShareServer.cs:411-426`，
`ApproveRequired?.Invoke(info).GetAwaiter().GetResult()` 在 `:418`），而整个握手由
`:536` 的 `Task.Delay(TimeSpan.FromSeconds(15))` 限时。而审批弹窗的自动拒绝时限是
**60 秒**（`src\WindowShare.Host\ApprovalDialog.xaml.cs:22`）。

⇒ 首次连接（未白名单设备）**必然**走这条路径：15 秒到点 → 服务端判定"认证握手失败/超时"
（`:539`）并返回 false，连接被关闭；弹窗仍在等待，用户在 t=40s 点「允许」时
`:443` 的 `conn.Send(AuthChallenge, ...)` 已在已释放流上抛 `ObjectDisposedException`，
被 `HandleClientAsync` 的 catch 记为"观看者处理异常"。同时 Viewer 侧此时是
`Failed` → `return`（`LanShareClient.cs:149`）+ `break`（`:208`），**并不会自动重连**，
与提示语「首次连接请在 Host 端批准本设备后自动重连」不符。

[ROADMAP.md](ROADMAP.md) 把 Q7 标为「落地」，但该路径仍然"审批不可能成功、弹窗必然重来"。

**最小修复**：给审批等待**独立的、短于整体预算的**时限（如批准 20s），或把 15s 计时移到批准之后
（分段：读 AuthRequest 5s / 批准 20s / 证明 10s）；并修正 Viewer 的提示语与自动重连行为。

### S8【MEDIUM】未认证并发上限是软上限 + 认证前无读超时 + 弹窗洪水

- **TOCTOU**：`LanShareServer.cs:292-304` 在 accept 循环里读 `_clients.Count` 判上限，而
  `_clients.Add(clientSession)` 发生在 worker 线程 `:316`。检查与登记之间有窗口，一波同时到达的
  连接可以都看到过期计数而全部放行 —— 突破 `MaxConcurrentUnauthenticated = 4`（`:31`），
  与 `PROTOCOL.md:105` 承诺的硬上限"≤4"不符。
- **可长期占位**：`TcpFrameConnection.cs:48` 设 `_stream.ReadTimeout = Timeout.Infinite;`，
  握手唯一时限是上面那个 15s。攻击者开 4 条静默连接、每 ~15s 重连一次，即可长期占满 4 个
  未认证槽位，合法观看者一律被拒（`:299-302`）。
- **弹窗洪水且不计限流**：`AuthRateLimiter.RecordFailure` 只在口令证明不匹配时调用（`:464`），
  静默连接与"白名单未批准被拒"（`:421`）都**不计入**。因此在槽位上限内可无限次重复触发
  60s 审批弹窗（`ApprovalDialog.xaml.cs:22`），而 `ApproveRequired` 是 UI 线程
  `ShowDialog()`（`MainWindow.xaml.cs:433,936-937`）—— 多连接会**嵌套模态弹窗**。

**最小修复**：把 `_clients.Add` 移进 accept 循环的同一把锁内；给认证前读加短超时；
白名单拒绝与握手超时也计入限流；审批弹窗按 `DeviceId` 限频或在口令校验通过后才弹。

### S9【MEDIUM】`Room.Viewers` 非线程安全（注释与代码矛盾）

`src\WindowShare.Signaling\RoomStore.cs:25`：

```csharp
public Dictionary<string, RoomViewer> Viewers { get; } = new();
```

而 `:28` 的类注释写"房间存储（线程安全 + 过期清理）"。写（`SignalingHub.cs:147-154`）、
删（`:98`）、遍历（`:119,193-195,202-206`）分布在不同连接的不同线程；两个 viewer 同时入房
即可触发 `InvalidOperationException: Collection was modified` 或 Dictionary 结构损坏
（Hub 内未捕获 → 客户端收到框架错误、房间状态不一致）。注意 `_rooms` 本身是 `ConcurrentDictionary`，
**只有内层字典不安全**。

**最小修复**：改 `ConcurrentDictionary<string, RoomViewer>`；遍历前 `ToArray()` 快照。

### S10【LOW】其余确证项（各一行，均有明确位置与最小修复）

| 项 | 位置 | 问题 | 最小修复 |
|---|---|---|---|
| ADTS 头校验漏 bit | `src\WindowShare.Core\Audio\Adts.cs:130` | `(data[1] & 0xF6) != 0xF0` 未校验注释所称的 `protection_absent`（bit0 被屏蔽）⇒ CRC 帧（9 字节头）会按 7 字节头解析，整包错位 2 字节 | 掩码改 `0xF7` 比较 `0xF1`，或让 `TryReadHeader` 返回头长度 |
| accept 前置语句在 try 外 | `LanShareServer.cs:310-318` | `RemoteEndPoint`/`new TcpFrameConnection(client)` 抛异常会逃出 `HandleClientAsync`，而调用处是 `_ = Task.Run(...)`（`:306`）⇒ 客户端不 Dispose、异常不入日志 | 整个方法包 `try/finally` 确保释放；或 accept 循环内 `try/catch` 记录 |
| 接收任务可能永久挂起 | `LanShareServer.cs:546-581` + `TcpFrameConnection.cs:244` | 发送循环先失败退出 → `Close()`→`Dispose()` 把 `_closed` 置 true → 读循环真正退出时 `if (!_closed)` 为假 ⇒ `Disconnected` 不再触发，接收任务阻塞到会话结束（每次泄漏一个线程池线程） | 给 `TcpFrameConnection` 加"关闭即回调"语义，或让接收循环额外等待一个 `Close()` 必然取消的每连接 CTS |
| 对端可控字符串无约束 | `LanShareServer.cs:417` 等 + `src\WindowShare.Core\Logging\Logger.cs:88-89,107` | `req.DeviceName`（单帧上限 8MB）直接进日志与 200 条环形缓存，无截断/转义 ⇒ 日志放大、内存驻留、可用 `\n` 伪造日志行 | 反序列化后截断/白名单化（DeviceId ≤128、DeviceName ≤64、过滤控制字符），或在 `Logger.Write` 统一按 1KB 截断并转义换行 |
| AAC 编码器 drain 无上限 | `src\WindowShare.Core\Audio\MfAacEncoder.cs:363-409` | `while (true)` + `MF_E_TRANSFORM_STREAM_CHANGE` 直接 `continue`；编码器持续报流变化则采集线程自旋不返回（对比 `MfAacDecoder.cs:27,431` 有 `MaxDrainRounds = 4096`） | 照解码器加轮数上限 + 超限记 `Warn` 并 return |
| 正常退出不释放 WASAPI 链 | `src\WindowShare.Core\Audio\LoopbackAudioCapture.cs:242`（`return`）vs `:259`（唯一 `DisposeChain` 调用点） | `DisposeChain`（`client.Stop()` + `Marshal.ReleaseComObject`）只挂在异常重建分支；正常取消退出不调用 ⇒ RCW 只等 GC，期间 loopback 流仍处 started，"停止后立刻再开始"会叠加两个活跃流 | `CaptureLoop` 主体包 `try/finally { if (client != null) DisposeChain(client, capture); }` |
| `MFStartup` 引用计数失衡 | `MfAacEncoder.cs:134` `ProbeEncoders()` + `src\WindowShare.Core\Audio\MfRuntime.cs:14` | 调 `MfRuntime.Startup()` 但无配对 `Shutdown()` ⇒ `_refCount` 永久失衡，`MFShutdown` 在该进程内不再发生 | `try/finally` 配对；编解码器构造函数内枚举/激活抛异常处同样配对 |
| `Stop()`→`Start()` 潜在泄漏 | `AudioRenderer.cs:252-284` | `Stop()` 既不释放也不置空 `_mixPtr`/`_client`/`_render`；`Dispose()` 只 `FreeCoTaskMem(_mixPtr)` ⇒ 若走 `Stop()` 再 `Start()` 会覆盖 `_mixPtr` 泄漏 CoTaskMem（当前 Viewer 调用方走 `StopAudio`→`Dispose`，故**仅潜在**）；另 `Reset()`（`:238-250`）全仓库无调用者 | `Stop` 内 `Marshal.ReleaseComObject` + 先释放 `_mixPtr`；Join 超时未退出则标记 faulted 禁止重启；删除死代码 `Reset` 或接入调用方 |

---

## 五、实测观察与体验判断

### 5.1 状态栏「（编码器欠产出）」在静态内容下必然误报

Viewer 实际显示 `码率：1597 / 7300 kbps（编码器欠产出）`（判定条件见
`MainWindow.xaml.cs:923-929`：未降档且实测 < 目标的 70%）。但本机内容以静态桌面与窗口为主，
**NVENC 的 CBR 不填充空闲码率**是正常行为——[ROADMAP.md](ROADMAP.md) 自己也记录了
「hevc_nvenc 对合成低复杂度内容码率仅 5%（CBR 不填充）」。

问题在于这个提示与 ROADMAP 里**当作缺陷指标**使用的「编码器欠产出」口径混用了：
1.5.0 之前它是"编码器坏了"的证据（DX12 收件箱编码器只产出 5–8%），而 1.5.0 之后
h264_nvenc 在同一台机器上对同一内容产出 71%（冒烟 Part2d 实测），此时低码率**主要是内容简单**。
**建议**：把该提示改为可区分"低复杂度内容"与"编码器封顶"的口径（例如结合内容变化率、
或仅在持续低于阈值且内容明显变化时才提示），避免把正常行为渲染成故障。

### 5.2 「编码验证(写H.264)」是持久化设置且会大量落盘

`HostSettings.RecordForValidation` 是**持久化**字段（`src\WindowShare.Core\Utils\HostSettings.cs:17`），
本次试运行开始时读取到的值就是 `true`（上一次会话留下的）。开启期间每帧裸流写入
`%APPDATA%\WindowShare\recordings\share-<时间戳>.h264`，**无大小上限、无自动轮转/清理**。
试运行前该目录已累计 **582.2 MB / 13 个文件**（单个最大 142.7 MB）。

**建议**：改为"仅本次会话"语义，或在界面标注预计落盘量与剩余空间；至少在设置里加体积上限。

### 5.3 冒烟测试 Part5 对部署产物必然失败

`tools\WindowShare.SmokeTest\Program.cs` 定位信令服务器 dll 时使用源码树相对路径
`..\..\..\src\WindowShare.Signaling\bin\Debug\net8.0\WindowShare.Signaling.dll`，因此
**对 `dist` 部署产物运行冒烟时 Part5 必然 FAIL**（本次实测：其余 10 项 PASS，Part5 报
「信令服务器未构建: C:\Users\src\WindowShare.Signaling\bin\Debug\net8.0\WindowShare.Signaling.dll」）。

按 [TESTING.md](TESTING.md) 的说法「需要 dist\publish」的冒烟流程应当 11/11 通过。
**建议**：按序尝试 源码树路径 → `dist\publish\signaling\` → `AppContext.BaseDirectory\signaling\`，
全找不到时给出可操作的失败原因。

**修复（已落地，未验证）**：新增 `FindSignalingDll()`（`tools\WindowShare.SmokeTest\Program.cs`），
按序尝试：从输出目录向上找到含 `WindowShare.sln` 的仓库根 → 源码树 `bin\Release` 与 `bin\Debug` →
`<根>\dist\publish\signaling\` → `AppContext.BaseDirectory\signaling\`；全部不存在时列出所有候选路径
（`Debug` 级日志）并给出汇总错误。**顺带把 Release 排在 Debug 之前**——原先只认 Debug，
运行 Release 构建的开发者会遇到"已构建却报未构建"的误导。

### 5.4 单测计数核实

静态统计（**审计当时**）：140 `[Fact]` + 67 `[InlineData]` = **207**，与 README「单测 207 项」一致 ✅。

> 本批 P0 修复新增 8 个用例（拥塞恢复 2、NV12 裁剪 3、连接生命周期 3，后者为新测试文件
> `TcpFrameConnectionTests.cs`）。补装 SDK 后**实测 215/215 通过**，与预期一致。
> 另注意：`TESTING.md` 原先引用的 `AdtsTests` / `PcmConvertTests` / `SampleTimelineTests` /
> `AvSyncClockTests` / `AudioStreamInfoTests` 等类名**在仓库中并不存在**（实际都在 `AudioTests.cs` 里），
> 本次已一并修正。

---

## 六、同类开源方案对比

沿用 [PROPOSAL-v1.5.0.md](PROPOSAL-v1.5.0.md) 的表格骨架并更新到 1.5.2 现状。

| 维度 | WindowShare 1.5.2 | [Sunshine](https://github.com/LizardByte/Sunshine) + Moonlight | RustDesk | [Deskreen](https://github.com/pavlobu/deskreen) / [screego](https://github.com/screego/server) |
|---|---|---|---|---|
| 捕获 | WGC → DXGI → GDI | DDA / WGC 双引擎 | WGC / DDA | 浏览器 / Electron |
| 编码 | FFmpeg 厂商硬编（nvenc/amf/qsv，真实 CBR）→ MF 现链 | 厂商硬编，CBR + 低延迟档 | 硬件 H264/H265 + 软编 | 浏览器端编码 |
| 解码 | MF + FFmpeg **软解**兜底（**无硬解**） | 全平台硬解（D3D11VA/NVDEC） | 硬解优先 | 浏览器解码 |
| 传输 | **LAN TCP**（队头阻塞） | **UDP + RS FEC + NACK**（[UDP 数据面](https://deepwiki.com/LizardByte/Sunshine/4.4-udp-streaming-and-data-plane)） | TCP + UDP + 中继 | libwebrtc 全套（GCC/NACK/FEC） |
| 弱网自适应 | 降码率（**当前为空操作**）+ 降分辨率（**不可逆**） | 分片 + 纠错，几乎不降 | 中继 + 自适应 | 自适应 |
| 音频 | AAC-LC 128k（WASAPI loopback，**不采麦**） | Opus | Opus | Opus / 共享标签页 |
| 免安装观看 | ❌ 需装自家 Viewer | ❌ | ❌ | ✅ **浏览器打开即看** |
| 输入控制 | **设计上不存在**（协议无输入消息） | ✅ 完整 | ✅ 完整 | ❌ / 部分只读 |
| 安全模型 | 临时密码 + 设备审批 + AES-256-GCM + 帧头 AAD/防重放 | 配对 PIN | 自建 ID / 中继 | 房间链接 |
| 信令 | ⚠️ 公网暴露下有多处越权面（S1–S3） | — | 自建服务器 | 内置 |

### 三大结构性差距（按投入产出排序）

1. **UDP 媒体面 + FEC/NACK**（ROADMAP「1.6 首选大项」）—— LAN 工具唯一的结构性差距，
   也是"Wi-Fi 弱网卡帧"的正解。⚠️ **依赖关系**：应先修 [A3](#a3-中-拥塞降档是单向棘轮降了分辨率就再也回不去-代码)/[A4](#a4-中-setbitrate-在-152-的主编码路径上是空操作但仍上报已生效-代码)，
   否则换了 UDP 也只是从"降了回不来"变成"完全不降"。
2. **d3d11va 硬解 + D3D 呈现** —— ROADMAP 自述 Viewer CPU（~45% 一核）反超编码侧（~37%）；
   `IVideoDecoder` 契约已就绪，属**纯收益**改动。
3. **浏览器免安装观看**（对标 Deskreen/screego 的核心卖点）—— 工作量最大，但决定了
   "发个链接就能看"与"让对方装 150MB 客户端"的体验代差。

### 只有本项目具备的三个差异点（建议写进 README 作为卖点）

- **协议层不存在输入注入消息**（`MessageType.cs` 枚举可审计），"只读"不是配置项而是结构事实。
- **系统声音取自 WASAPI loopback、不采集麦克风**，且与画面走同一条 TCP + 同一把会话密钥。
- **GOP 补发让新观看者首帧即 IDR**（本次实测「补发缓存 GOP 42 帧，接入即出画面」），
  绕开了 `CODECAPI_AVEncVideoForceKeyFrame` 在多数编码器上返回 `E_NOTIMPL` 的现实。

---

## 七、修复排期

> **状态图例**：✅ 代码已落地（**未编译/未测**，本机无 SDK）｜⬜ 未开始。
> 「位置」列给的是**修复前**的行号，便于对照问题现场；修复后的位置见各条正文的「修复」段落。

### P0（目标 1.5.3）

| ID | 优先级 | 目标版本 | 位置 | 修复要点 | 验收方式 | 状态 |
|---|---|---|---|---|---|---|
| A2 | **P0** | 1.5.3 | `Host/TrayIcon.cs:72-77` | 写入 `nid.hWnd = _hwnd`；检查 `Shell_NotifyIcon` 返回值并记日志 | 托盘菜单三项动作手动可达；`MinimizeToTray` 开时关窗→可唤回可停止 | ✅ |
| A6 | **P0** | 1.5.3 | `Core/Audio/AudioRenderer.cs:180` | `_queuedFrames -= dropped.Frames - dropped.Offset;` + Debug 日志 | 实跑不再出现假欠载/负 `BufferedMs`（无可测接缝，见该条备注） | ✅ |
| A3 | **P0** | 1.5.3 | `CongestionController.cs:92-171` + `EncoderPipeline.cs:198-212` | 独立的分辨率回升分支；每档回升刷新 `_lastGoodSince`；夹取基准改为会话初始尺寸 | 新增注入时钟单测：降档→分辨率回初始；回升每 15s 一档 | ✅ |
| A4 | **P0** | 1.5.3 | `Core/Encoding/EncoderPipeline.cs:154-160` | `SetBitrate` 返回 false 时不改写 `Settings.BitrateBps`、不上报假目标值 | 实跑日志与实际目标一致 | ✅ |
| A5 | **P0** | 1.5.3 | `MfVideoDecoder.cs:493-566` + `Nv12ToBgra.cs` | ~~分离缓冲/可见高度~~ → 实测无效；改为 **`StatsInfo` 上报编码真实尺寸 + 观看端裁剪** | Viewer 状态栏显示 1920×1080 | ✅ 已实机验证 |
| S6 | **P0** | 1.5.3 | `Core/Network/TcpFrameConnection.cs:276-285` | 拆分 `_closed`/`_disposed`，`Dispose` 幂等且无条件释放 | 新增 3 例连接生命周期单测 | ✅ |
| 冒烟 Part5 | **P0** | 1.5.3 | `tools/WindowShare.SmokeTest/Program.cs` | 信令 dll 路径按序兜底（含 `dist\publish\signaling\`） | 冒烟 **11/11 PASS** | ✅ 已验证 |
| **N10** | **P0** | 1.5.3 | `Host/MainWindow.xaml.cs:74-94` | 构造期置 `_restoringSettings` 护住初始化，禁用未初始化控件默认值落盘 | 写配置→启动→回读不被改写 | ✅ 已实机验证 |
| **A1** | **P0** | 1.5.3 | `Capture/GraphicsCaptureEngine.cs` + `EncoderPipeline.cs` + `Session/ShareSession.cs` | WGC 实现 `IFrameRateLimited` 并在取帧前节流；节流权威唯一化（引擎节流时关闭管线节流） | 分配量 ↓89%、托管堆峰值 161→52MB、工作集走平；215/215 + 11/11 + 上屏 fps 无回退 | ✅ 已实测验证 |

### P1（目标 1.6.0）—— 需要独立设计的加固项

| ID | 优先级 | 目标版本 | 位置 | 修复要点 | 验收方式 | 状态 |
|---|---|---|---|---|---|---|
| A1 | ~~**P1**~~ | ~~1.5.4 / 1.6.0~~ | — | **已定位并修复，见 P0 表** | — | ✅ |
| S1 | **P1** | 1.6.0 | `Signaling/Hubs/SignalingHub.cs:169-175` + `Host/MainWindow.xaml.cs:792-843` | 服务端校验成员与批准状态；Host 侧校验 `viewerId` 已批准 | 非成员连接发 `webrtc-request` 无效 | ⬜ |
| S2 | **P1** | 1.6.0 | `SignalingHub.cs:71-122,187-198` | 拆 `FindHostRoomByConnection`；服务端维护 `Approved` 标志 | viewer 连接调 `StopSharing`/`ApproveViewer` 无效 | ⬜ |
| S3 | **P1** | 1.6.0 | `Signaling/Program.cs:9-10` | HTTPS 强制 + CORS allowlist + 每 IP 限速 + nonce 挑战认证 | 非 loopback http 拒绝启动；跨源请求被拒 | ⬜ |
| S4 | **P1** | 1.6.0 | `Core/Network/AuthPayloads.cs:132-143` + `LanShareServer.cs:394,490,512` | 对 ≥1.3 对端恒定启用 AAD，或把能力位并入 HMAC transcript | 篡改/删除 `ver` 导致认证失败而非静默降级 | ⬜ |
| S5 | **P1** | 1.6.0 | `Core/Network/TcpFrameConnection.cs:149-152,203-232` | 加密会话中非握手类型必须带 `Encrypted`，否则断开；`RttMs` 加上界 | 注入明文 `StatsInfo` 被断开 | ⬜ |
| S7 | **P1** | 1.6.0 | `Core/Network/LanShareServer.cs:536` + `Host/ApprovalDialog.xaml.cs:22` | 握时分段（读 5s / 批准 20s / 证明 10s）；修正 Viewer 提示与重连行为 | 首次连接在弹窗内批准可成功 | ⬜ |
| S8 | **P1** | 1.6.0 | `LanShareServer.cs:292-316`；`TcpFrameConnection.cs:48` | `_clients.Add` 进临界区；认证前读超时；拒绝/超时计入限流；弹窗限频 | 突发连接不超上限；槽位不可被静默占满 | ⬜ |
| S9 | **P1** | 1.6.0 | `Signaling/RoomStore.cs:25` | 改 `ConcurrentDictionary` + 遍历快照 | 并发入房无异常 | ⬜ |

### P2（候选）—— 低风险清理与体验口径

| ID | 优先级 | 目标版本 | 位置 | 修复要点 | 验收方式 | 状态 |
|---|---|---|---|---|---|---|
| S10 | **P2** | 候选 | 见 [S10 表](#s10low其余确证项各一行均有明确位置与最小修复) | 逐项低风险修复 | 按项单测/实跑 | ⬜ |
| A7 | **P2** | 候选 | `Viewer/MainWindow.xaml.cs:923-929` | 「编码器欠产出」口径区分"内容简单"与"编码器封顶" | 静态桌面不再误报 | ⬜ |
| A8 | **P2** | 候选 | `Core/Utils/HostSettings.cs:17`、`Host/MainWindow.xaml.cs:414` | 「编码验证」改"仅本次会话"或加体积上限 | 默认不再持续落盘 | ⬜ |
| — | **P2** | 候选 | `LanShareServer.cs:514-515` | `AuthResult` 的 `Width/Height` 上报编码输出尺寸而非源几何尺寸（本次 A5 修复不需要它） | 与 A5 备注对照 | ⬜ |

**顺带**：A4/A5 已同步处理 `docs/ROADMAP.md` 的 `SetBitrate` 动态重配与信令加固两条既定项标注
（改标「未落地/空操作」并指向本报告），避免读者把"已列入路线图"误读成"已实现"。

### P0 验证记录

本批 P0 最初是在**没有 SDK** 的环境下写的（当时无法编译，只能静态复核）。补装 SDK 8.0.425 后已完成验证：

| 验证项 | 命令 / 方法 | 结果 |
|--------|-------------|------|
| 构建 | `dotnet build -c Release -m:1` | ✅ 0 错误；4 个警告**全部为改动前既有**（已逐条核对非本次引入） |
| 单元测试 | `dotnet test -c Release` | ✅ **215/215 通过**（审计当时 207 + 新增 8） |
| 冒烟测试 | `dotnet run --project tools/WindowShare.SmokeTest -c Release` | ✅ **11/11 PASS**（Part5 由 FAIL 转 PASS） |
| A2 托盘 | `PostMessage(WM_TRAY, WM_RBUTTONUP)` → 枚举 `#32768` | ✅ Host 弹出 `TrackPopupMenu` 菜单窗口 |
| A2 端到端 | 开始共享 → `WM_CLOSE` | ✅ 进程存活、共享继续（隐藏到托盘） |
| A5 分辨率 | 实启双端读 Viewer 状态栏 | ✅ `分辨率：1920×1080` |
| N10 设置 | 写配置 → 启动 → 回读 | ✅ 三项设置均未被改写 |
| **A1 内存** | 自建探针（进程内 GC API + 工作集），按引擎 A/B，各 5 分钟 | ✅ 累计托管分配 2,500→**272MB**、Gen2 1→**18** 次、托管堆峰值 161→**52MB**、工作集走平 |
| 回归 | Viewer 帧率/跳帧/积压 | ✅ 20.8–21.0fps、跳帧 0、积压 0（与修复前一致） |

仍**未验证**的一项：
- **A6 假欠载**：本次只捕获 2 次欠载，样本不足。建议 `WINDOWSHARE_LOG_LEVEL=Debug` 跑 ≥10 分钟，
  用新增的 `Debug` 日志对账 `Offset` / `remaining`。

完整过程与"被推翻的判断"记录见 [DEVLOG.md](DEVLOG.md#s-2026-10-06-c)。

```powershell
# 复现本轮验证（需要 .NET 8 SDK）
dotnet build -c Release -m:1
dotnet test  -c Release            # 215/215
dotnet run --project tools/WindowShare.SmokeTest -c Release   # 11/11
```

---

## 八、附录：原始数据

### 8.1 内存采样（隔离实验：无观看者 / 无音频 / 无录制）

```
idle(after launch)                WS= 125.8MB  Private=  92.4MB
before share                      WS= 129.0MB  Private=  93.7MB
+5s   sharing(no viewer,no audio) WS= 436.2MB  Private= 581.5MB
+10s                              WS= 443.1MB  Private= 586.6MB
+15s                              WS= 437.2MB  Private= 580.8MB
+20s                              WS= 444.7MB  Private= 588.9MB
+25s                              WS= 451.8MB  Private= 596.1MB
+30s                              WS= 458.8MB  Private= 603.1MB
+35s                              WS= 468.1MB  Private= 611.5MB
+40s                              WS= 472.3MB  Private= 616.5MB
+45s                              WS= 479.5MB  Private= 623.8MB
+50s                              WS= 487.2MB  Private= 631.3MB
+55s                              WS= 494.5MB  Private= 639.2MB
+60s                              WS= 501.8MB  Private= 646.5MB
+65s                              WS= 510.7MB  Private= 654.3MB
+70s                              WS= 518.1MB  Private= 661.4MB
```

### 8.2 内存采样（含音频 + 1 个观看者，采样窗口较长）

```
idle                              WS= 132.4MB  Private=  97.7MB
session(含音频) t≈36s             WS= 489.7MB  Private= 628.4MB
t≈51s                             WS= 534.5MB  Private= 672.2MB
t≈66s                             WS= 561.3MB  Private= 699.6MB
t≈81s                             WS= 581.0MB  Private= 718.2MB
t≈96s                             WS= 580.9MB  Private= 718.1MB
t≈111s                            WS= 581.7MB  Private= 718.9MB
t≈141s                            WS= 583.8MB  Private= 720.1MB
t≈171s                            WS= 591.0MB  Private= 726.9MB
```

### 8.3 隔离实验复现步骤

1. 启动 `dist\publish\WindowShare.Host.exe`，等待主窗口出现（约 6–8 秒）。
2. 确认「共享系统声音」与「编码验证(写H.264)」复选框均为**未勾选**
   （通过 UIA 的 `TogglePattern` 读 `Current.ToggleState`）。这两项在会话启动时固定，
   开始共享后再改不生效。
3. 通过 UIA `InvokePattern` 点击「开始共享」。
4. 每 10–15 秒采样一次 `(Get-Process WindowShare.Host).WorkingSet64` 与 `PrivateMemorySize64`
   （采样前需调用 `Refresh()`）。
5. **不要**接入任何观看者 —— 只观察捕获+编码单路径。
6. 判据：正常应在前 ~10 秒预热后趋于平台期；本次实测为持续单调增长（见 8.1）。

### 8.4 冒烟测试结果（对 `dist` 部署产物）

```
===== 结果: 合成编码=PASS, 4K/高帧率=PASS, HEVC往返=PASS, 厂商硬编=PASS,
真实捕获=PASS, 回环端到端=PASS, HEVC协商=PASS, 信令=FAIL, WebRTC=PASS,
系统声音=PASS, 局域网发现=PASS =====
```

Part5 失败原因（`[Error] [Part5] 信令服务器未构建: C:\Users\src\WindowShare.Signaling\bin\Debug\net8.0\WindowShare.Signaling.dll`）
为路径硬编码，见 [5.3](#53-冒烟测试-part5-对部署产物必然失败)。

关键断言明细：

| Part | 关键数据 |
|---|---|
| Part2 | `h264_nvenc`，编码 64 帧，实测/目标 = **71%**（目标 3.0 Mbps） |
| Part2b | 4K：3840×2160，32/32 帧，解码回读通过；720p@120：提交 2358 → 编码 266（实际 133fps），节流丢弃 2092，符合 ≤135% 目标 |
| Part2c | HEVC 编解码往返 106/106 帧（FFmpeg 软解，MF 探针不可用/崩溃隔离）；实测/目标 = 4%（低复杂度合成内容） |
| Part2d | 工厂选 `h264_nvenc`，实测/目标 = **71%**，FFmpeg 软解回读 107/107 |
| Part3 | WGC 捕获 157 帧 / 编码 81 帧 |
| Part4 | 连接=True 加密=True 收 140 帧 解码 140 帧 首帧即 IDR=True |
| Part4b | HEVC 负例被拒（原因完整）；正例 FFmpeg 解码 67 帧 |
| Part6 | WebRTC 收/解码 135 帧（含音频 180 帧解码 180） |
| Part7a | 96000 帧 → 91 ADTS → 解回 90 块（96.0%），RMS 0.211，过零比 0.500 |
| Part7b | 收 279 帧 / 94KB，解码 278 块，解码异常 0 |
| Part8 | 发现 `SmokeTest-Host @ 127.0.0.1:48750` |

### 8.5 单测计数（静态核实，未执行）

| 测试文件 | `[Fact]` | `[Theory]` | `[InlineData]` |
|---|---|---|---|
| AnnexBTests | 10 | 2 | 7 |
| AppPathsTests | 8 | 0 | 0 |
| AudioTests | 38 | 2 | 8 |
| CongestionControllerTests | 6 | 0 | 0 |
| FrameProtocolTests | 5 | 0 | 0 |
| GopCacheTests | 10 | 0 | 0 |
| JsonSettingsStoreTests | 8 | 0 | 0 |
| LanDiscoveryTests | 12 | 1 | 2 |
| SecurityHardeningTests | 9 | 1 | 8 |
| SecurityTests | 6 | 0 | 0 |
| StatsCollectorTests | 3 | 0 | 0 |
| VideoDecoderFactoryTests | 4 | 0 | 0 |
| VideoEncoderFactoryTests | 4 | 0 | 0 |
| VideoFormatPlannerTests | 15 | 8 | 39 |
| Yuv420pToBgraTests | 2 | 1 | 3 |
| **合计** | **140** | **15** | **67** |

# 用例总数 = 140 + 67 = **207**（审计当时）。

> 本批新增：`CongestionControllerTests` +2、`Yuv420pToBgraTests` +3、
> `TcpFrameConnectionTests` 新文件 +3 ⇒ 修复后**期望 215**。

### 8.6 复现命令备忘

```powershell
# 单元测试（需先安装 .NET 8 SDK）
dotnet test -c Release

# 冒烟测试（需 SDK）
dotnet run --project tools\WindowShare.SmokeTest -c Release

# 对 dist 部署产物跑冒烟（无 SDK 时的替代路径，需先把 dist\publish 的运行时叠加到冒烟输出目录）
dotnet <smoke-output>\WindowShare.SmokeTest.dll

# Viewer CLI 直连（无人值守）
dist\publish\WindowShare.Viewer.exe --connect 127.0.0.1 --password <临时密码>
```
