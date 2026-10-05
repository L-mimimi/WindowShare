# 第三方组件声明：FFmpeg（HEVC 软解兜底）

本项目（MIT License）通过**动态链接**方式使用以下 LGPL 库实现 HEVC 软件解码兜底
（`FfmpegVideoDecoder`）。依据 LGPL 要求，随分发附如下声明；各库的原始许可文本与源码
获取方式见下。未做任何静态链接，未使用包含 GPL 组件（x264/x265 等）的 GPL 构建。

## 1. FFmpeg 原生库（avcodec-61.dll / avutil-59.dll / swresample-5.dll）

- 许可：LGPL-2.1-or-later（FFmpeg 项目）
- 版权：Copyright (c) FFmpeg contributors
- 源码：https://ffmpeg.org/download.html （对应版本 tag：n7.1）
- 使用的二进制：[Sdcb.FFmpeg.runtime.windows-x64](https://www.nuget.org/packages/Sdcb.FFmpeg.runtime.windows-x64) 7.1.0（LGPL 共享构建，未启用 GPL 组件）
- 用途：HEVC（H.265）Annex-B 码流的软件解码；仅使用 `libavcodec`/`libavutil` 的约 13 个解码 API
- 替换说明（LGPL §4）：可将上述三个 DLL 替换为自行构建的同版本 FFmpeg 共享库
  （`--disable-*` 裁剪保留 hevc 解码器即可），应用无需重新编译即可继续工作

## 2. FFmpeg.AutoGen（托管结构体绑定）

- 许可：LGPL-3.0
- 版权：Copyright © Ruslan Balanukhin
- 源码：https://github.com/Ruslan-B/FFmpeg.AutoGen （NuGet：FFmpeg.AutoGen 7.1.1）
- 用途：仅引用其 C 结构体托管定义（`AVCodecContext`/`AVFrame`/`AVPacket`）；
  **未使用**其函数加载器（裸名动态加载与本项目的带版本后缀 DLL 不兼容），
  全部函数经 `FfmpegInterop` 自行以 `DllImport("avcodec-61"/"avutil-59")` 导入

## 3. 临时/开发期依赖（不随发布产物分发）

- xUnit、Microsoft.NET.Test.Sdk 等测试框架：仅用于本仓库测试，不在运行时依赖内
