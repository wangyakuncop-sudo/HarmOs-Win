# HWP v1 — HarmOs-Win 传输层协议规格

> 状态：GATE-L0 实现基线（2026-11-08）。双端（ArkTS 源端 / C# 宿主端）必须按本文件逐字节对齐。
> 依据：`AGENTS.json` DEC-06、INV-06、INV-07、INV-12；编码参数参考华为 `HwExScreenConfig.xml`。

## 1. 设计目标

| 目标 | 手段 |
|---|---|
| 端到端 <100ms | 视频不重传、不排队旧帧；控制通道独立 |
| 消除空闲首包 124ms | 20–50ms 双向心跳压住 Wi-Fi 省电唤醒 |
| 60fps / 16Mbps | MTU 1200B 分片，序号 + 时间戳 |
| 不复制 FillP | 只保留 RTT 估计 + 简单码率自适应 |

## 2. 包头（固定 16 字节，全部小端）

| 偏移 | 长度 | 字段 | 说明 |
|---|---|---|---|
| 0 | 2 | `magic` | `0x4857`（'H','W'） |
| 2 | 1 | `version` | `1` |
| 3 | 1 | `channel` | 见 §3 |
| 4 | 1 | `flags` | bit0 `NEED_ACK`、bit1 `IS_ACK`、bit2 `PROBE` |
| 5 | 1 | `reserved` | 0 |
| 6 | 4 | `seq` | uint32，每通道独立单调递增 |
| 10 | 4 | `timestampMs` | uint32，**发送方单调时钟低 32 位**（回绕自动取模） |
| 14 | 2 | `payloadLen` | uint16，≤ 1184 |

`MTU = 1200`，`PAYLOAD_MAX = 1184`（避免 IP 分片）。

**时钟约定**：`timestampMs` 是发送方本地单调时钟，接收方**不做跨设备时钟对齐**。
测 RTT 时接收方原样回显该字段，发送方用自身单调时钟相减，无需 NTP。

## 3. 通道语义

| channel | 名称 | 可靠性 | 规则 |
|---|---|---|---|
| 1 | `VIDEO` | 不可靠 | 序号 + 时间戳；**丢包不重传、乱序直接丢帧**；jitter buffer 1–2 帧 |
| 2 | `CONTROL` | 可靠有序 | `NEED_ACK`；滑动窗口 = 1；RTO ≈ 20ms；最多重传 3 次；承载输入事件、剪贴板、命令 |
| 3 | `HEARTBEAT` | 不可靠 | 双向 30ms 空包；兼做 RTT 采样与 Wi-Fi 保活 |
| 4 | `ACK` | — | `IS_ACK`；回显请求方的 `seq` 与 `timestampMs` |

`PROBE` 置位时，接收方必须**立即**回一个 `ACK`（不排队、不入业务逻辑），用于延迟测量。

## 4. 控制命令（`CONTROL` payload 首字节）

| 值 | 含义 | GATE-L0 用途 |
|---|---|---|
| `0x00` | idle | 关闭心跳与负载，测空闲基线 |
| `0x01` | light | 30ms 心跳 + 60fps × 4 包（≈2.3Mbps） |
| `0x02` | full | 30ms 心跳 + 60fps × 28 包（≈16Mbps，官方基线） |
| `0xFF` | stop | 停止探针 |

## 5. 默认端口

| 端 | 地址 | 端口 |
|---|---|---|
| 平板 MLR-AL00 | `0.0.0.0` | `9020`（bind） |
| Windows PC | `192.168.1.102` | `9010`（bind） |

端口与码率均为配置常量，不得散落在业务代码里（INV-06）。

## 6. 双端实现位置

| 端 | 文件 |
|---|---|
| 协议常量 / 编解码（ArkTS） | `tablet/entry/src/main/ets/transport/HwpPacket.ets` |
| UDP 双通道 + 心跳（ArkTS） | `tablet/entry/src/main/ets/transport/UdpTransport.ets` |
| L0 探针页（ArkTS） | `tablet/entry/src/main/ets/pages/NetworkProbePage.ets` |
| 协议常量 / 编解码（C#） | `pc/HarmOsWin.Transport/HwpPacket.cs` |
| 视频 / 控制 / 心跳通道（C#） | `pc/HarmOsWin.Transport/Channels.cs` |
| L0 测量控制台（C#） | `pc/HarmOsWin.Probe/Program.cs` |
| 立即可跑的对端测量工具 | `scripts/l0_probe.py` |

## 7. GATE-L0 判定

1. 空闲基线出现 ≥100ms 的 RTT 尖峰（复现 Wi-Fi 省电唤醒）；
2. 心跳开启后 p95 RTT 显著下降、max <100ms；
3. 16Mbps 负载下 p95 RTT 仍 <100ms、丢包率可接受；
4. 控制通道在丢包时靠 ACK/RTO 补齐，且不被视频洪流饿死。

**达标即不上 P2P**（INV-12）；不达标才进入 GATE-L1。

---

## 8. v1.1 扩展（NEXT-02~06，向后兼容）

包头、端口、心跳、v1.0 全部命令语义**保持不变**，`scripts/l0_probe.py` 可随时回归。架构说明见 `docs/架构-HarmOs-Win.md`。

### 8.1 新增通道与标志

| 项 | 值 | 语义 |
|---|---|---|
| `CH_STREAM` | 5 | 真实 H.264 码流分片；不可靠、不重传、乱序丢整帧 |
| `FLAG_KEYFRAME` | 0x08 | 该帧为关键帧起点，用于解码器重建后的首个可解码帧 |
| `FLAG_LAST` | 0x10 | 当前帧的最后一个分片 |

`CH_STREAM` payload 布局：`[0..3] frameId:u32 LE` + `[4..5] fragment:u16 LE` + `[6..7] fragCount:u16 LE` + `[8..] 码流字节`。单包总长 ≤1200B，故码流字节 ≤1176B。

### 8.2 新增 CONTROL 命令（payload 首字节）

| 值 | 名称 | payload 后续 |
|---|---|---|
| `0x10` | `CMD_INPUT` | 统一 `InputEvent`：`type:u8` + `action:u8` + `x:u16` + `y:u16` + `key:u16` + `ts:u32` |
| `0x20` | `CMD_CLIP_TEXT` | `hash:u32` + UTF-8 文本 |
| `0x21` | `CMD_CLIP_IMAGE` | `hash:u32` + `id:u16` + `index:u16` + `count:u16` + PNG 字节 |
| `0x30` | `CMD_NAV` | `key:u8`（1 back / 2 home / 3 recents） |
| `0x40` | `CMD_ROLE` | `role:u8`（1 SOURCE / 2 HOST / 3 BOTH）+ `deviceId:u8` |
| `0x50` | `CMD_RESOLUTION` | `w:u16` + `h:u16` + `fold:u8` |
| `0x60` | `CMD_STREAM` | `action:u8`（1 start / 0 stop） |
| `0x70` | `CMD_LOCK_STATE` | `locked:u8`（Q' 本地锁屏页触发） |
| `0x80` | `CMD_INJECT_RESULT` | **GATE-05 探针专用**：19B `InjectReport`，见 §8.5 |

所有新增 CONTROL 命令仍带 `NEED_ACK`，复用窗口 1 + ACK + 自适应 RTO；`CMD_INJECT_RESULT` 是唯一例外（best-effort 回传，不要求 ACK）。

### 8.3 自适应 RTO

`RTO = clamp(srtt + 4 * rttvar, 20, 200)` ms，`srtt`/`rttvar` 由心跳与控制 ACK 的 RTT 样本滚动估计（α=1/8, β=1/4）。无样本时回退 20ms。理由：GATE-L0 实测固定 20ms 在 Wi-Fi 省电态每阶段触发 1~3 次无谓重传。

### 8.4 握手顺序（不可颠倒）

Windows 防火墙只放行「PC 先发过包」的远端入站，因此必须：
`PC 先发 CONTROL(NEED_ACK) → 设备回 ACK → 设备才开始 CH_STREAM 推流`。
若设备先推流，入站包会被静默丢弃。

### 8.5 注入结果回传（GATE-05 探针专用）

`CMD_INJECT_RESULT = 0x80` 由**设备端主动发往 PC**，payload 在命令字之后固定 19 字节、小端：

| 偏移 | 字段 | 类型 | 说明 |
|---|---|---|---|
| 0 | `syscap` | u8 | 1 = `canIUse(InputSimulator)` 为真 |
| 1 | `ok` | u8 | 1 = 本次注入序列完整成功 |
| 2..5 | `errCode` | i32 | `BusinessError.code`；0 成功 / 201 权限 / 801 能力不支持 |
| 6 | `stage` | u8 | 0 完成 / 1 `createTouchController` / 2 `touchDown` / 3 `touchUp` |
| 7..10 | `elapsedUs` | u32 | 设备端本地注入耗时（微秒） |
| 11..14 | `attempts` | u32 | 累计尝试次数，兼作回传序号 |
| 15..18 | `success` | u32 | 累计成功次数 |

双端实现：`SessionTypes.ets` 的 `WireCodec.encodeInjectReport` / `decodeInjectReport`
与 `pc/HarmOsWin.Core/HwpCommands.cs` 的 `Wire.DecodeInjectResult` + `InjectReport` record 逐字节对齐。
该命令字**只服务探针**，不参与正式会话语义；DEC-08 结案后若移除探针，应一并删除此命令字及两端编解码。

2026-11-08 真机两轮各 10 次：`errCode` 全部 801、`stage` 全部 1、`ok` 全部 0、`syscap` 全部 1，
即 SDK 声明层支持但平板 ROM 未实现 `InputSimulator`。详见 `tmp/gate05_r2.json`。

**NEXT-07 复用**：`CMD_INJECT_RESULT` 同时承载 GATE-05 原生探针结果与 GATE-06 低频输入结果，
不新增命令字。用 `stage` 区分：GATE-05 为 `1 create / 2 touchDown / 3 touchUp`；
GATE-06 为 `1 back / 2 home / 3 recents / 4 单点点击`。
`errCode=-2` 是主 HAP 本地低频桥 250ms 无响应（无障碍服务未开启），属链路状态而非协议错误；
`syscap` 在 GATE-06 语义为「无障碍扩展服务已连接回包」。基线见 `tmp/gate06_baseline.json`。
