# PC 端（C# / .NET 8）

四个项目，架构分层见 `docs/架构-HarmOs-Win.md`。

| 项目 | TFM | 职责 |
|---|---|---|
| `HarmOsWin.Transport` | net8.0 | HWP v1.1 编解码、UDP 端点、视频/控制/心跳三通道、`RtoEstimator`、`VideoAssembler` |
| `HarmOsWin.Core` | net8.0 | 统一 `InputEvent`、`InputInjector`（hdcw.sh 串行）、Win32 剪贴板桥、`DeviceSession`/`SessionManager`（≤3 台）、解码/上屏接口、`InjectReport` 解码 |
| `HarmOsWin.Probe` | net8.0 | L0 延迟回归 + `gate02` 端到端 + `gate05` 注入探针三个控制台 |
| `HarmOsWin.App` | net8.0-windows (WPF) | 单窗口多标签、三键导航、Q' 本地锁屏页 |

## 环境

本机系统 `dotnet` 只有 6.0 运行时、无 SDK；已补装 **.NET 8.0.427 SDK** 到 `C:\Users\W\.dotnet`（用户目录，不动系统安装）。

```bash
export DOTNET_ROOT="/c/Users/W/.dotnet"
"/c/Users/W/.dotnet/dotnet.exe" build pc/HarmOsWin.sln -c Release --nologo
```

## 运行 GATE-L0 回归

```bash
python scripts/l0_probe.py                       # 12×2s IDLE + 16×1s LIGHT/FULL
python scripts/l0_probe.py --idle 3 --active 3   # 快速连通性检查
```

## 运行 GATE-02 端到端

**前置**：平板打开 HarmLink →「进入统一会话（NEXT-02~06）」→「启动统一会话」。
**握手顺序不可颠倒**：PC 必须先发 CONTROL，Windows 防火墙才会放行设备端入站流。

```bash
cd tmp
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate02 192.168.1.100 8 gate02_result.json
```

依次验证：握手 ACK → 10 个输入事件 → 三键导航 → 文本/PNG 剪贴板 → `CH_STREAM` 真实 H.264 码流重组。
2026-11-08 真机结果：235 帧 / 1105440 B / 940 包，丢帧 0、视频丢包 0，输入 ACK p50=10.88ms。

## 运行 GATE-05 注入探针（DEC-08）

**前置**：同 GATE-02，平板上启动统一会话。

```bash
cd tmp
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate05 192.168.1.100 10 gate05_result.json
```

流程：握手（冷启动 RTO 20ms 超时会自动重试一次）→ 发 N 个 `CMD_INPUT(0x10)` →
设备端 `InputInjector.ets` 在 HAP 内做一次 `createTouchController + touchDown/touchUp` →
经 `CMD_INJECT_RESULT(0x80)` 回传 19B `InjectReport` → 统计成功率、`errCode` 分布、
`stage`、本地注入耗时与端到端延迟。

**2026-11-08 真机结论：FAIL**。两轮各 10 次（未声明权限 / 声明 `CONTROL_DEVICE`）：
`syscap` 20/20 = true，但 `createTouchController()` 20/20 返回 `801 Capability not supported`，
注入成功 0/20，本地耗时 p50=p95=1ms。轮 2 端到端 p50=29.18ms / p95=84.34ms，
**证明 HWP CONTROL 传输层达标、瓶颈只在设备端注入能力**。
`CONTROL_DEVICE` 是 `system_basic` + `manual_settings` + `deviceTypes 仅 2in1`，
平板 `bm dump` 显示 `reqPermissionStates=-1`，签名 profile `acls.allowed-acls=[]`，ACL 提权不适用。
权威报告：`tmp/gate05_r1.json` / `tmp/gate05_r2.json`；设备端截图 `tmp/g5_r2_result.jpeg`。

> **DEC-08 已结案（用户 2026-11-08 02:58 明确批准）**：反控能力收窄为三键导航 + 低频点击（INV-13），
> 高频注入不再属于交付范围。GATE-03/05 的 FAIL 转为排除依据。落地走 `GATE-06 / NEXT-07`：
> 用 `AccessibilityExtensionAbility` 实现三键 + 单点，判据为单次端到端 <200ms；
> hdc 的 2.5s 级延迟明确排除。**该路径尚未实现、尚未真机验证，不得预先写成可用。**

## 运行 GATE-06 低频输入验证（NEXT-07）

**前置**：平板启动统一会话，且已在「设置 → 辅助功能 → 扩展服务 → 已安装的服务」开启 **HarmLink 低频操作**。
本机 ROM 没有 `accessibility` 命令行工具，服务只能手动开启。

```bash
cd tmp
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate06 192.168.1.100 5 gate06_result.json
```

流程：握手 → 5 次 `CMD_INPUT`（单点）→ 3 个 `CMD_NAV`（back/recents/home）→
设备端 `LowFreqBridge`(127.0.0.1:9021/9022) → `AccessibilityExtAbility` 执行 →
`CMD_INJECT_RESULT(0x80)` 回传 → 统计成功率、`errCode` 分布、`stage` 与端到端延迟。
判据：**动作可达且单次端到端 <200ms**（INV-13 收窄后重定）。

**2026-11-08 基线（`tmp/gate06_baseline.json`）**：握手 PASS、8/8 CONTROL ACK、8/8 结果回传、
`stage` 分布正确，但全部 `errCode=-2`、`serviceConnected=false` —— 本地桥 250ms 超时，
即无障碍服务未开启。**探针链路本身已通，动作可达性未验证**，不要写成通过。
服务开启后直接重跑本命令即可得最终判据。

## 运行 GATE-03 注入通道测量

```bash
python scripts/gate03_probe.py --count 4 --out tmp/gate03_result.json
```

严格串行调用 `hdcw.sh`（INV-02）。**结论：FAIL** — uitest p50=2552.6ms（瓶颈是 hdc 会话重建），uinput 参数不被设备接受。
GATE-03 + GATE-05 共同构成 `DEC-08` 的判据：hdc、原生注入 API、ACL 提权三条路径均不可用。

## 解码/上屏状态（GATE-02 未闭项）

- `PassthroughDecoder`：重组后按帧交付，只统计字节，不做像素解码（验证传输链路用）。
- `MediaFoundationDecoder`：接口占位，`Feed()` 只累加计数，明确标注未接入。
- 真实实现需要 COM interop（`IMFTransform` / `IMFSample` / DXVA + D3D11 纹理）与 `D3DImage` 上屏，是独立工作包。

设备端同理：`FileVideoSource` 从已录制 mp4 读真实 H.264 码流；60fps 实时零拷贝采集需 C++ NAPI（`OH_AVScreenCapture` + `OH_VideoEncoder`）。

> 不要把「接口就绪」写成「GATE 通过」。

## GATE-02 实时画面（v1.1.0）

- 解码：`HarmOsWin.Core/MfH264Decoder.cs`，Media Foundation `CMSH264DecoderMFT` 硬解 → NV12 → BGRA。
- 分辨率：`H264Sps.cs` 从关键帧 SPS 自举，不再依赖设备端 `CMD_RESOLUTION`。
- 上屏：`HarmOsWin.App/MainWindow.cs` 用 `WriteableBitmap`（UI 忙时丢帧保实时）。
- 真机量化：`gate02live`；离线调解码器：`mfde <file.h264>`（不吃真机，秒级迭代）。
- 实测（2026-11-08）：161 帧 / 18.3MB / 0 丢包 / 0 解码错误，6.4fps，首帧 14.9s。**画面通了，帧率未达标。**
