# HarmOs-Win

鸿蒙 7 手机 / 平板实时投屏到 Windows，支持反向输入、双向剪贴板、三键导航。目标是交互接近原生：1080p / 60fps / 端到端 <100ms。

---

## 这是什么

把华为鸿蒙设备（MatePad MLR-AL00、Pura X Max）的画面实时镜像到 Windows 桌面，并能在电脑上操作设备。剪贴板的文本和图片可以双向传递。

**关键约束一（2026-11-08 用户批准修订）**：投屏期间屏幕必须**肉眼呈黑态**，同时电脑端画面**实时刷新**——即「视觉息屏」：压低物理屏背光 + 系统级覆盖息屏时间 + 锁屏时 PC 端显示本地锁屏页。**不要求设备进入物理休眠**。原来的「物理息屏/锁屏后仍实时刷新」在三方应用层不可达，A1/A2/A3/B 四条路已逐条实测排除；降级为用户明确批准，见 `AGENTS.json` INV-05。

**关键约束二（2026-11-08 02:58 用户明确批准的需求降级）**：反控能力收窄为**三键导航 + 低频点击**，放弃任意坐标高频鼠标/键盘注入。三条注入路径（hdc、原生 `inputEventClient`、ACL 提权）已真机判死；这是用户明确选择的取舍，**非静默降级**，见 `AGENTS.json` INV-13 / DEC-08。不得静默恢复高频注入。

---

## 目录

```
HarmOs-Win/
├─ AGENTS.json      工程契约：invariant、决策、验证门（改代码前必读）
├─ INDEX.json       关键词 → 文件/符号 映射
├─ PROGRESS.json    会话恢复状态
├─ KZJ.md           三段式对话模板
├─ SESSIONS/        每轮总结，TEMPLATE.json 是模板
├─ docs/            架构-HarmOs-Win.md（架构事实源）、传输层协议-HWP-v1.md（双端协议基准）
├─ scripts/         hdcw.sh（无线调试）、l0_probe.py、gate03_probe.py
├─ tablet/          平板/手机端 HarmonyOS 工程（ArkTS + C++ NAPI）
│  └─ ets/transport/  HwpPacket.ets / UdpTransport.ets（HWP v1.1 源端）
│  └─ ets/core/       SessionTypes.ets / SessionService.ets / InputInjector.ets
│  └─ ets/capture/    VideoSource.ets（采集层抽象）
│  └─ ets/accessibility/  LowFreqBridge.ets / AccessibilityExtAbility.ets（NEXT-07 低频输入）
│  └─ ets/pages/      Index / NetworkProbePage / SessionPage
└─ pc/              Windows 客户端（C# / .NET 8 + WPF，4 个项目）
   ├─ HarmOsWin.Transport/  协议、三通道、RtoEstimator、VideoAssembler
   ├─ HarmOsWin.Core/       输入/剪贴板/会话/解码接口
   ├─ HarmOsWin.Probe/      GATE-L0、gate02、gate05、gate06 四个测量控制台
   └─ HarmOsWin.App/        WPF 单窗口多标签 + 三键 + 锁屏页
```

---

## 快速开始

### 调试设备（默认必须走无线）

```bash
./scripts/hdcw.sh status                              # 看连接状态与当前模式
./scripts/hdcw.sh shell "param get const.product.model"
./scripts/hdcw.sh port 40001                          # 端口变了就切
./scripts/hdcw.sh recv /data/local/tmp/s.jpeg s.jpeg  # 拉文件到 tmp/
./scripts/hdcw.sh keepon                              # 覆盖息屏时间（默认 30min）
./scripts/hdcw.sh screenoff                           # 查电源态与 ScreenOffTime
./scripts/hdcw.sh keepoff                             # 退出前必做：恢复系统设置
./scripts/hdcw.sh brightness                          # 查当前亮度/自动亮度
./scripts/hdcw.sh dim                                 # 压到最低亮度（视觉息屏 S1）
./scripts/hdcw.sh undim                               # 按记录次数恢复亮度
```

**不要手敲 hdc。** 无线调试有四个坑（跨进程掉会话、并发互踢、路径转换、绝对路径拼接错误），`hdcw.sh` 已经全部处理。

**USB 临时例外**：仅 P2P 验证阶段可用 `./scripts/hdcw.sh mode usb` 显式切换（跳过自动 tconn）；验证完必须 `mode wireless` 并补测无线 hdc。GATE-L0 达标后 P2P 已放弃，该例外作废。

`keepon` 的三个实测约束：SLEEP 后覆盖会被压到 10000ms、解锁后清除，因此需 ≤10s 心跳重发；只防自动超时，挡不住手动电源键；异常退出若残留 10000ms 会让设备 10 秒就息屏，所以退出前务必 `keepoff`。

### 设备

| 设备 | 无线地址 | 备注 |
|---|---|---|
| 平板 MLR-AL00 | 192.168.1.100:38803 | 1600x2560 竖屏，当前验证机 |
| 手机 HOP-AL10（Pura X Max 典藏） | 192.168.1.102:40663 | 折叠屏，默认展开态；已启用 ref 采集，与平板双路并行投屏 |

**手机 IP 以用户 2026-11-16 18:18 最终裁定为准：`192.168.1.102:40663`。**
此前把 `192.168.1.103` 当手机是误判，勿再使用（历史日志中出现过，不代表手机地址）。
端口是动态的，设备重启或开关无线调试后会变，以设备屏幕显示为准。

### PC 窗口显示（默认纯画面 / 零黑框）

2026-11-10 起，启动后窗口里**只有画面**：顶部工具栏、底部状态条、侧栏，以及画面区四周的边距 / 边框 / 圆角全部去掉；画面用 `Stretch.Fill` 填满客户区，上下左右不再露出深色底（详见 `docs/架构-HarmOs-Win.md` 文末 2026-11-10 两条补记）。

控制条是**浮在画面上的浮层**，不占布局行，呼出 / 收起都不改变画面区尺寸（实测 F11 前后画面区垂直 / 水平位移均为 0）。侧栏默认折叠，F11 呼出控制条也不会弹出左侧深色面板，要用侧栏点工具栏 `›`。**单击、双击、拖动画面一律只注入设备，不会呼出控制条、不会改变界面**（2026-11-10 21:17 修复：此前双击画面会弹出上下深色条并强制展开侧栏，就是「一点屏幕黑框就出现」）。

| 操作 | 效果 |
|---|---|
| `F11` / `Esc`（键盘） | 呼出或收起控制条（工具栏 + 状态条浮层）；呼出后点工具栏 `›` 可展开侧栏 |
| `--chrome on` | 启动就显示控制条，还原旧界面 |
| `--touchscale <倍数>` | 触控坐标换算倍数，默认等于 `--devscale`（如 2）。注入用设备**物理屏幕** 1600×2560，而 `s.Width×s.Height` 是采集缩放后的 800×1280；若设备端改报物理尺寸，传 `1` |
| 工具栏「▣ 等比」 | 切到 `Stretch.Uniform` 等比完整画面（比例不符时会留边）；再点「▣ 铺满」回到零黑边 |
| 工具栏「⤢ 贴合画面」 | 按设备宽高比重新贴合窗口 |

**注意**：铺满是非等比拉伸。窗口比例由 `WM_SIZING` 锁在设备比例附近，正常状态形变 <1%；手动把窗口拉成横屏时画面会被拉扁，点「⤢ 贴合画面」即可恢复。点击 / 拖动走 Fill 的线性映射，末尾再乘 `touchscale` 换成设备物理坐标 —— 少了这一步点击会整体落在屏幕左上 1/4（已修）。用 `--tap-test 0.8,0.5` 可自测：应输出 `dev≈(1279,1280)`。

**上边框栏与底部三键导航（2026-11-11 00:07 已编译交付，根目录 exe md5 `19beccae91e66e956e121a6c19181be4`）**：22:57 版是画面**上沿常驻一枚悬浮胶囊** = 「☰ 菜单」按钮 + **只显示帧率**（`N fps` / 静止 / `—`）；**23:14 按用户截图要求，菜单与帧率改放到窗口系统标题栏上**（`WindowChrome` 自绘浅色标题栏，跨两列、右侧留出系统三大按钮），画面区不再有顶部胶囊。点菜单可开控制条、贴合画面、等比 / 铺满、开始 / 停止推流、重新连接、展开侧栏、退出。窗口**底部常驻三键导航胶囊**：「□ 多任务 / ○ 主页 / ◁ 返回」（顺序同设备设置），与侧栏按钮同源。原底部 5 项 HUD（帧率 / 延迟 / 帧数 / 丢包 / 解码错误）已移除，延迟等 4 项不再显示；诊断数值仍在底栏 `_detail`，`F11` 呼出可见。三键胶囊仍是**局部半透明胶囊、不占布局行**，不是横贯窗口的深色条带 —— 那会重新变成你之前删掉的上下黑框；标题栏固定 30 DIP 已从客户区扣回，画面区尺寸与点击坐标映射不变。`--menu off` 只关上边框栏的菜单 / 帧率，三键导航保留。历史版本备份在 `releases/HarmOsWin_标题栏菜单_20261110.exe`（新版）与 `releases/HarmOsWin_上边框菜单_三键导航_20261110.exe`（上一版）。**23:01 已对 22:57 版做本机窗口回归**（目视确认上沿菜单 + 帧率、底部三键、HUD 四项消失、无上下黑框，截图 `tmp/topnav_default.png` / `tmp/topnav_detail.png`）；F11 叠放、菜单点击、点画面不弹控制条、三键落点等交互项因非交互会话无法注入键鼠，仍需人工按键 / 连真机验。23:31 的标题栏迁移**已编译落位、尚未启动**，首次启动需目视确认系统按钮、拖动、菜单点击与画面尺寸。详见 `docs/架构-HarmOs-Win.md` 文末补记④⑤⑥。**23:48 又做了一轮纯视觉打磨（未编译）**：两枚胶囊从深色块改成**浅色胶囊**（浅底深字 `TitleChip`，与 `#F3F3F3` 标题栏融合）、菜单去掉 `☰`（11.5 号下会被渲染成「三」）、帧率去掉「帧率」标签只留数值、高度 26 → 22 留出上下呼吸位；改前 / 改后预览见 `tmp/titlebar_ui_compare.png`。标题栏配色见 `Ui/Theme.cs` 的 `TitleBar / TitleChip / TitleText` 一组常量。**23:52 底部三键改自绘图标（未编译）**：原来 `□ 多任务 / ○ 主页 / ◁ 返回` 的**三键图标**是直接写的字符，字号 / 线宽 / 基线各不相同（方形小得像个点）；现改为 `NavKeyButton` 控件用同一 13×13 画布、统一 1.4 线宽的**自绘图标**（圆角方框 / 圆环 / 左三角），图标 + 文字整组居中、三键等宽，`SendNav` 注入链路未动。改前 / 改后预览见 `tmp/nav_ui_compare.png`。

**三键功能修复（2026-11-11 00:07 已编译交付）**：旧实现把同一次点击**同时**丢给 HWP CONTROL 和注入通道，两路都成功时设备执行两次（按一次「返回」退两层）；两条 `Task<bool>` 都丢弃返回值，设备回的 `InjectResult`（`ok` / `errCode`）`MainWindow` 从未订阅，所以「点了没反应」时界面零反馈。现改为：HWP 主路 → 等设备回执（700ms，实测 P50 ≈ 30ms）→ `ok=1` 结束，`ok=0` 或超时未回执才降级注入通道。同时：设备回执 `errCode=-2` 翻译成「无障碍服务未开启：设置 → 辅助功能 → 扩展服务 →『HarmLink 低频操作』」；失败原因写进底部三键胶囊内的红字提示（4s 自动隐藏，纯画面下也可见）；三键增加 `Enabled` 态（未连接时压暗不可点）与 `Flash` 点按反馈（成功绿 / 失败红）。**本轮只编译装机、未启动未连真机**，待验：按一次「返回」是否只退一层、无障碍未开时是否闪红并给出提示、未连接时三键是否压暗。详见 `docs/架构-HarmOs-Win.md` 补记⑧。

> 🔴 **2026-11-16 21:48 更正（本节的历史描述不再成立）**：上面那句「`errCode=-2` 翻译成『无障碍服务未开启：设置 → 辅助功能 → 扩展服务 →『HarmLink 低频操作』』」**指向一个不存在的菜单** —— 鸿蒙 7 系统没有「无障碍扩展服务」这个能力（用户 21:28 最终确认「永久记住」；取证 `hidumper -s AccessibilityManagerService -a '-c'` → `client num: 0`）。**PC 端 `MainWindow.cs` 里那句提示文案仍是旧的，属待修项**（改文案需重新编译，须用户当次授权），正确方向是改成「注入通道未就绪（检查无线调试与持久会话）」之类，不要再指向系统设置。详见 `docs/项目梳理-20261116.md` 第八节。

**三键键码纠错（2026-11-11 00:25 已编译装机，根目录 exe md5 `683f3aac519c8b3cae480a651730477e`）**：「多任务」之前不生效是**键码本身错了**，不是设备不响应。取值全部改为本机 SDK 原文（`E:/DevEco Studio/sdk/default/openharmony/ets/api/@ohos.multimodalInput.keyCode.d.ts`）：`KEYCODE_HOME = 1`（47 行）、`KEYCODE_BACK = 2`（54 行）、`KEYCODE_VIRTUAL_MULTITASK = 2210`（1040 行）、`KEYCODE_TASKMANAGER = 2717`（1955 行）。**旧值 `KeyHome = 3` 在鸿蒙是 `KEYCODE_CALL`（拨号）**，**旧值 `KeyRecents = 187` 是 Android 的 `KEYCODE_APP_SWITCH`，鸿蒙键码全表没有 187** —— 两者都是照搬 Android 导致的错误。同时多任务不再走 HWP 主路：设备侧实现是 `AccessibilityElement.performAction('recentTask')`，被标 `@deprecated since 12` 的是 **performAction 这三个重载**（`AccessibilityExtensionContext.d.ts:351/368/384`），`'recentTask'` 字面量本身其实是 **since 12 新增**（此前记成「recentTask 废弃」是错的，已更正）。现改为多任务直接下发系统键码，不再空等 700ms 注定超时的回执；Home / Back 逻辑不变。若 2210 在平板上无反应，用环境变量 `HARMOS_RECENTS_KEY=2717` 重启即可切到 `KEYCODE_TASKMANAGER` 复测，**不用改代码重编**。详见 `docs/架构-HarmOs-Win.md` 补记⑨。

**单实例接管（2026-11-11 00:56 已编译装机，根目录 exe md5 `79fe9e129dc404ef1b25fc710ced0db2`）**：**打开新的会自动关闭旧的**。`Program.Main` 在创建主窗口之前调用 `InstanceGuard.TakeOver()`，用命名 Mutex 串行化竞态，先 `CloseMainWindow()` 让旧进程跑完同步清理（`fport rm` + 杀设备端扩展 daemon），超时才 `Kill(entireProcessTree:true)` 连 hdc 子进程一起收。**为什么必须有**：hdc 严格串行，两进程同打 hdc 会互踢会话（表现为「点了没反应」）；参考采集独占设备端 daemon 与 fport；更关键的是**覆盖 exe 不会让已运行进程换成新代码**（Windows 映像映射）——本轮就是踩到这个坑：00:39 启动的进程跑的是 00:32 版，00:47 覆盖文件后它仍是旧映像，用户看着新窗口的点不到新功能。详见 `docs/架构-HarmOs-Win.md` 补记⑫。

**启动期状态（2026-11-10 22:03 已编译交付）**：纯画面模式隐藏了底部状态条，**面向用户的进度提示**会同步显示在画面中央，逐条显示「正在连接设备（无线调试）→ 正在准备输入通道 → 正在启动设备采集服务 → 采集已就绪 → 正在等待设备首帧」，出帧后自动消失。此前这十几秒只有一行固定的「等待设备画面…」，容易被判成「连接不上 / 卡住」。技术日志（tconn / gRPC h2c / 注入通道就绪等）经 `IsStageMessage()` 白名单隔离，仍写底栏、需 `F11` 呼出查看，**不会盖掉画面中央的进度**。同时 gRPC 握手完成即主动请求一次 IDR，看门狗在等首帧阶段由 3s 轮询改为 500ms（出帧后恢复 3s，运行期开销不变），缩短首帧等待。**⛔ 不要为此加 `power-shell wakeup`**（真机实测出帧 8 → 0）。

---

## 当前阶段

**阶段 6 — NEXT-07 低频输入已落地装机，GATE-06 基线证明探针链路通（2026-11-08 03:16），架构事实源见 `docs/架构-HarmOs-Win.md`。** 链路仍为家庭 LAN + HWP v1.1 UDP 双通道 + 30ms 心跳，**不上 P2P**（INV-12 结案）。

| 项 | 结果 |
|---|---|
| GATE-02 传输/重组 | ✅ 真机 PASS：真实 H.264 码流 235 帧 / 1105440B / 940 包，丢帧 0、视频丢包 0；握手、10/10 输入、3/3 导航、文本与 PNG 剪贴板全部 ACK。设备端 UI 统计与 PC 报告完全一致 |
| **GATE-02 实时采集/上屏** | ✅ **PASS**（2026-11-08 19:14 复核 + 19:26 WPF 人工验收）：`OH_AVScreenCapture`(WithSurface) 零拷贝接 `OH_VideoEncoder`(AVC) 真采真编，native 在编码器回调线程直接按 HWP 分片 sendto；PC 端 MF 硬解 + `WriteableBitmap` 上屏。探针 peakFps=62 / activeFps=46.5 / p99=52ms / 解码 0 错误 / 延迟 11.4ms；暂停 2.5s 恢复首帧 102ms；WPF 窗口 presented=100、写位图 3.0ms、端到端 51ms。**剩余：PC 换新进程接入首帧不稳定（2.4~11s，重放分片突发组装失败）；D3DImage 零拷贝未做；多设备并发未测** |
| GATE-03 输入注入 | ❌ FAIL（**已结案，转为排除依据**）：hdc 每条命令需 2~3s 重连，uitest p50=2552.6ms、uinput 0/4 |
| GATE-05 设备端注入 | ❌ FAIL（**已结案，转为排除依据**）：两轮各 10 次，`syscap=1` 但 `createTouchController()` 20/20 返回 **801**；本地 0~1ms，端到端 p50=29.18ms |
| DEC-08 | ✅ **已结案**：用户明确接受「只保留三键导航 + 低频点击」，记为 INV-13，非静默降级 |
| **NEXT-07 / GATE-06** | ✅ **能力已达成，无障碍通道判「平台不支持」**：无障碍（辅助功能）路径 2026-11-09 系统级取证为不可用 —— `AccessibilityManagerService` 返回 `client num: 0`（零客户端注册），本机 ROM 无「扩展服务」入口，应用侧 `bm dump` 登记正常（`type:4`/`visible:true`）。能力改由 uitest 持久 socket RPC 承担并已达标：**单点 20 次 p50 74.97ms / p95 90.42ms / 0 失败**。无障碍代码保留作跨 ROM 保底 |
| GATE-04 剪贴板 | ✅ **文本双向已打通**：PC→设备 自动同步（文本/PNG 无损）；设备→PC 走官方安全控件 **PasteButton**（点即授权、免 `READ_PASTEBOARD`）——真机 38 字符文本端到端完全一致。上行图片（PNG）尚未做 |
| NEXT-05 / 06 | WPF 多标签+三键+锁屏页编译通过；分辨率/折叠通知与角色协商已落地，手机真机按用户要求暂缓 |

**无障碍路径已判定不可达（2026-11-08 实机核查）**：本机设置只有「关怀和无障碍」，无「扩展服务 / 已安装的服务」；搜索「扩展服务」无结果；`/system/bin` 无 `accessibility` 命令；`hilog` 显示系统确实解析了我们的服务（`HandlePackageChanged` → `GetJsonStringFromString`），说明**是 ROM 不提供三方无障碍开关，不是声明写错**。加上 hdc（2.5s/次）、`inputEventClient`（801）、ACL 提权（权限 -1），四条注入路线全部关闭。

> > **hdc 明确排除在低频实现之外**：单次 p50=2552.6ms 是 2.5 秒级延迟，不能拿「低频」当理由放行。GATE-06 的 <200ms 是能力收窄后重定的判据，不是放宽验收来掩盖 GATE-03/05 的失败。

### GATE-06 复测（服务开启后）

```bash
cd tmp
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate06 192.168.1.100 5 gate06_result.json
```

判据：三键（back/home/recents）+ 单点点击在设备端可达，且单次端到端 **<200ms**。
链路：`PC → CMD_NAV/CMD_INPUT → LowFreqBridge(127.0.0.1:9021/9022) → AccessibilityExtAbility → CMD_INJECT_RESULT(0x80) → PC`。
`errCode=-2` = 服务未连；`9300003` = 无无障碍权限；`9300005` = Action 不支持（相关 API 已 deprecated since 12）。

### GATE-02 / GATE-05 回归

```bash
# 平板：HarmLink →「进入统一会话（NEXT-02~06）」→「启动统一会话」
cd tmp
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate02 192.168.1.100 8 gate02_result.json
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0/HarmOsWin.Probe.dll \
  gate05 192.168.1.100 10 gate05_result.json   # type=0xFE，只触发原生注入探针回归

python scripts/gate03_probe.py --count 4 --out tmp/gate03_result.json
```

握手顺序不可颠倒：**PC 必须先发 CONTROL，设备才开始 CH_STREAM 推流**，否则 Windows 防火墙会丢弃入站流。

**GATE-S1 已通过（2026-11-08 真机）**：黑遮罩位于合成层会被一起采走，`setWindowBrightness` 又要求应用前台有焦点，因此改用 **hdc 侧 `uitest uiInput keyEvent 41/40` 直接改系统亮度**——与应用前台状态无关、不进合成层。亮度压到 `Min=1~3` 时，AVScreenCapture 录像 8.7MB / 85s，抽出的帧 141~188KB、逐帧不同、内容清晰非黑（历史熄屏黑帧恒为 2348B）。证据在 `tmp/s1.mp4` 与 `tmp/frames_s1/`。

**GATE-L0 已通过（2026-11-08 真机，PASS）**：协议见 `docs/传输层协议-HWP-v1.md`（16B 包头 / MTU 1200B / 视频不重传 / 控制可靠有序 / 30ms 心跳）。实测 30ms 心跳把 Wi-Fi 省电唤醒造成的尖峰彻底压掉：

| 阶段 | 负载 | n | RTT p50 | RTT p95 | max | 视频包 | 丢包 |
|---|---|---|---|---|---|---|---|
| IDLE | 无心跳 | 12 | 94ms | 140ms | 140ms | 0 | — |
| LIGHT | 2.3Mbps | 16 | 15ms | **16ms** | 16ms | 4232 | 0 |
| FULL | **16Mbps** | 16 | 15ms | **31ms** | 31ms | 28476 | 0 / 乱序 0 |

probe 44/44 零丢失、控制命令 3/3 ACK、超时 0。RTT 为往返，单向约 RTT/2，**端到端 <100ms 有充足余量**，因此按 INV-12 放弃 P2P（仅省 1~5ms 却可能踢掉无线 hdc）。权威报告 `tmp/l0_result.json`。

### GATE-02 实时画面（native 采集编码 + MF 硬解）

```bash
# 平板：HarmLink →「进入统一会话」→「启动实时投屏（native 采集 + 编码）」
cd tmp
# 跑起来后平板会弹「允许"HarmLink"使用你的屏幕」，必须点「允许」，否则 choice=false 零帧
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0-windows/HarmOsWin.Probe.dll \
  gate02live 192.168.1.100 25 gate02live.json gate02_frame.bmp

# 离线调解码器（不吃真机）：把落盘的 .h264 直接喂给 MF
"/c/Users/W/.dotnet/dotnet.exe" ../pc/HarmOsWin.Probe/bin/Release/net8.0-windows/HarmOsWin.Probe.dll \
  mfde gate02live.h264 0 mfde_frame.bmp
```

判据（GATE-02）：1080p@60fps、端到端 <100ms。**2026-11-08 19:14 复核 PASS**：探针 peakFps=62 / activeFps=46.5 / 帧间隔 p50=16 p95=28 p99=52ms / 解码 0 错误 / 延迟 11.4ms；暂停 2.5s 后恢复首帧 **102ms**（缓存 IDR 重放，此前 797/924ms）。**2026-11-08 19:26 WPF 窗口首次人工验收上屏**：presented=100、写位图 3.0ms、端到端 51ms、1600x2560 竖屏居中。首帧仍受两件事影响：录屏授权需人工点允许、以及「采集只在画面变化时出帧」。**2026-11-08 22:30 已闭环**：编码器开启 `REPEAT_PREVIOUS_FRAME_AFTER=33`（无新帧时重复编码上一帧），PC 重连首帧 3 次实测稳定 **2.0s**（此前 2.4~13s），静止画面也从 0fps 变为 30~33fps；代价是静止时编码码率约 10~11Mbps。

两个必须知道的口径坑：

1. **`OH_AVScreenCapture` 只在画面变化时出帧** —— 屏幕静止时编码器掉到 1~5fps，这是正常行为。测帧率必须让画面动起来（设备端连续 `uitest uiInput swipe`），并用 `peakFps`（最大 1 秒窗口）/`activeFps`（首帧到末帧）看，别用全程平均。
2. **别把授权等待算进分母** —— 首帧延迟要拆成「等人工授权」与「链路耗时」，否则会把 0.87s 的链路误读成 10s。

### 复测 GATE-L0

```bash
# 平板：HarmLink →「进入 GATE-L0 网络探针」→「启动 L0 服务」
python scripts/l0_probe.py                       # 默认 12×2s + 16×1s + 16×1s
python scripts/l0_probe.py --idle 3 --active 3   # 快速连通性检查

# 编译（本机系统 dotnet 无 SDK，8.0.427 装在 C:/Users/W/.dotnet）
"/c/Users/W/.dotnet/dotnet.exe" build pc/HarmOsWin.sln -c Release --nologo
```

HAP 构建注意：**工程内没有 `hvigorw` 脚本**，必须用 DevEco 自带的 `node.exe hvigorw.js`：

```bash
cd tablet
export JAVA_HOME="/e/DevEco Studio/jbr" NODE_HOME="/e/DevEco Studio/tools/node" \
       DEVECO_SDK_HOME="/e/DevEco Studio/sdk"
export PATH="$JAVA_HOME/bin:$NODE_HOME/bin:$PATH"
"E:/DevEco Studio/tools/node/node.exe" "E:/DevEco Studio/tools/hvigor/bin/hvigorw.js" \
  --mode module -p module=entry@default assembleHap -p buildMode=debug --no-daemon
```

`PATH` 必须含 `$JAVA_HOME/bin`，否则 `PackageHap` 报 `spawn java ENOENT`；增量缓存偶发 `es2abc` 读不到中间 `.ts`，用 `clean` 后全量重建。当前 HAP 有 **7 条 ArkTS WARN**：1 条既有 `ScreenCaptureProbe` syscap 提示 + 3 条 `InputInjector.ets` 的 `CONTROL_DEVICE` 权限告警 + 3 条无障碍 API deprecated（`getWindowRootElement` / `performAction` / `injectGestureSync`）。均属探针与 deprecated API 固有，ArkTS 严格模式禁用 `@ts-ignore`，已如实记录，不掩盖。

进度详情见 `PROGRESS.json`。

---

## 关键词索引

| 关键词 | 位置 |
|---|---|
| 契约、规则、invariant、DoD、agents、工程约定 | `AGENTS.json` → invariants / decisions / verification_gates |
| 索引、关键词、定位、index | `INDEX.json` |
| 进度、待办、恢复、progress、resume | `PROGRESS.json` |
| 提示词、模板、开工、收尾、kzj | `KZJ.md` |
| 说明、快速开始、readme、关键词表 | `README.md` |
| 绿边、绿边框、16px 对齐、宏块填充、补零、stride、bufH | `pc/HarmOsWin.Core/MfH264Decoder.cs`（`green-edge-16px-pad`） |
| devscale、传输分辨率、画质、清晰度、档位、超采样、4:2:0 小字 | `pc/HarmOsWin.App/MainWindow.cs`（`scale-tier-calibration`） |
| winscale、窗口倍数、2/3、贴合画面 | `pc/HarmOsWin.App/MainWindow.cs`（`pc-fit-window`） |
| 零黑框、纯画面、--chrome、Stretch.Fill、铺满、等比、F11、呼出控制条、黑边、letterbox、坐标映射 | `pc/HarmOsWin.App/MainWindow.cs`（`borderless-fill`） / `docs/架构-HarmOs-Win.md` 文末补记 |
| 点击弹出黑框、双击劫持、控制条浮层、--touchscale、物理屏幕坐标、点击定位偏左上、滚轮下半屏 | `pc/HarmOsWin.App/MainWindow.cs`（`TryMapToDevice` / `BuildContent`） / `docs/架构-HarmOs-Win.md` 文末 21:17 补记② |
| 启动连不上、启动卡住、等首帧、首帧慢、IDR 催帧、看门狗、启动进度、纯画面看不到状态 | `pc/HarmOsWin.Core/ReferenceCapture.cs`（`StreamLoopAsync` / `WatchdogAsync`）/ `pc/HarmOsWin.App/MainWindow.cs`（`SetStatus`）/ `docs/架构-HarmOs-Win.md` 文末 21:53 补记③ |
| 上边框菜单、菜单按钮、三键导航、底部导航、帧率只显示、删除延迟、HUD 指标、--menu off、悬浮胶囊、三键不生效、按一次退两层、重复注入、无障碍服务未开启、注入回执 | `pc/HarmOsWin.App/MainWindow.cs`（`BuildTopMenu` / `BuildBottomNav` / `BuildRoot` / `SendNav` / `OnInjectResult` / `WindowChrome`）→ 系统标题栏自绘 + 三键单路下发 / `pc/HarmOsWin.App/Ui/Controls.cs`（`NavKeyButton` / `TitleChip`）/ `docs/架构-HarmOs-Win.md` 文末补记④~⑧ |
| 红字偏紫、粉紫、偏色、色偏、App 配色、不准改色彩公式 | `docs/架构-HarmOs-Win.md` v1.24.0 ③（`red-purple-not-pipeline`） |
| hdcw stderr、静默失效、fport 残留、滑动无反应、流停滞假故障、corr 校验 | `scripts/hdcw.sh`（`hdcw-stderr-pitfall`） |
| 参考项目、ohscrcpy、hnscrcpy、HOScrcpy、lanlink、oink、许可证 | `docs/参考项目-能力清单.md` |
| 无线调试、hdc、封装、串行、端口、hdcw、USB 例外、息屏覆盖、keepon | `scripts/hdcw.sh` |
| 设备、平板、手机、MLR-AL00、Pura X、分辨率 | `AGENTS.json` → facts.devices |
| 息屏、锁屏、保活、黑屏、视觉息屏、GATE-01、INV-05 降级 | `AGENTS.json` → invariants.INV-05 / GATE-01 |
| 技术选型、语言、ArkTS、C#、WPF、NAPI | `AGENTS.json` → invariants.INV-11 |
| 平板端、工程、tablet、entry、bundle、HarmLink | `tablet/entry/src/main/ets/pages/Index.ets` |
| 息屏探针、GATE01、采集、AVScreenCapture、ScreenCaptureProbe、帧 | `tablet/entry/src/main/ets/probe/ScreenCaptureProbe.ets` |
| 链路、UDP、P2P、Wi-Fi Direct、心跳、GATE-L0、GATE-L1、INV-12 | `AGENTS.json` → INV-12 / DEC-06 / GATE-L0 |
| 会话、2026-11-08、决策记录、session | `SESSIONS/2026-11-08.json` |
| HWP、包头、MTU、协议规格、通道语义、控制命令 | `docs/传输层协议-HWP-v1.md` |
| ArkTS 编解码、DataView、时间戳回绕、魔数 | `tablet/entry/src/main/ets/transport/HwpPacket.ets` |
| 源端传输、UDPSocket、视频模拟负载、模式切换 | `tablet/entry/src/main/ets/transport/UdpTransport.ets` |
| 网络探针页、L0 服务、统计面板 | `tablet/entry/src/main/ets/pages/NetworkProbePage.ets` |
| C# 传输层、ReliableControlChannel、RttSampler、HwpEndpoint、dotnet | `pc/HarmOsWin.Transport/Channels.cs` |
| 测量控制台、三阶段、判定、结果 JSON | `pc/HarmOsWin.Probe/Program.cs` |
| 一键测量、包装脚本、l0_probe | `scripts/l0_probe.py` |
| PC 说明、编译环境、NET8、运行步骤 | `pc/README.md` |
| L0 实测、延迟报告、PASS、丢包统计 | `tmp/l0_result.json` |
| 架构事实源、模块分层、数据流、架构变更、architecture_doc | `docs/架构-HarmOs-Win.md` |
| CH_STREAM、v1.1 扩展、自适应重传、握手顺序 | `docs/传输层协议-HWP-v1.md` → §8 |
| SessionTypes、WireCodec、防回环哈希、角色枚举 | `tablet/entry/src/main/ets/core/SessionTypes.ets` |
| SessionService、会话编排、剪贴板推送、码流分片 | `tablet/entry/src/main/ets/core/SessionService.ets` |
| IVideoSource、FileVideoSource、DisplayProfile、折叠变更 | `tablet/entry/src/main/ets/capture/VideoSource.ets` |
| 录屏授权、免重弹、永久授权、隐私弹窗、CUSTOM_SCREEN_CAPTURE、CUSTOM_SCREEN_RECORDING、EXEMPT_CAPTURE_SCREEN_AUTHORIZE、system_core、PermissionDefinitions、2in1、会话常驻、单例、结束投屏 | `AGENTS.json` → facts.screen_consent_cannot_be_permanent / session_singleton_survives_pages |
| 采集会话单例、SessionService.shared、start 幂等、pauseStream、finishSession | `tablet/entry/src/main/ets/core/SessionService.ets` / `pages/SessionPage.ets` |
| 输入注入、注入通道、uitest、uinput、fast_touch、MMI SimulateInputEvent、VirtualHid、参考项目做法、无障碍 | `docs/参考项目-能力清单.md` → §2 / `AGENTS.json` → facts.inject_channel_matrix |
| GATE-06、低频输入落地、阻塞、扩展服务、visible、Not match target、8012、注入延迟 | `AGENTS.json` → verification_gates.GATE-06 / `tmp/gate06hdc.json` |
| uitest、agent.so、singleness、@uitest_socket、localabstract、fport、hypiumApiHelper、RPC、pressBack、touchDown | `pc/HarmOsWin.Core/UitestRpcClient.cs` / `AGENTS.json` → facts.uitest_rpc_channel |
| 持久 socket、p95、1.2.3、uitest_agent | 同上 |
| hosScrcpy、uitest_agent、screen-mirroring、agent.so 来源 | `E:/DevEco Studio/plugins/screen-mirroring/lib/hosScrcpy-1.0.20-beta.jar` |
| 统一会话页、SessionPage、NEXT-02 | `tablet/entry/src/main/ets/pages/SessionPage.ets` |
| InputInjector、InputEvent、uitest 通道、uinput 通道 | `pc/HarmOsWin.Core/InputInjector.cs` |
| Win32Clipboard、ClipboardBridge、PNG 无损、FNV-1a | `pc/HarmOsWin.Core/ClipboardBridge.cs` |
| DeviceSession、SessionManager、角色协商、多标签切换 | `pc/HarmOsWin.Core/DeviceSession.cs` |
| IVideoDecoder、IRenderSurface、PassthroughDecoder、硬解占位 | `pc/HarmOsWin.Core/IVideoPipeline.cs` |
| 单窗口多标签、三键导航、锁屏页、MainWindow | `pc/HarmOsWin.App/MainWindow.cs` |
| PC 主窗口布局、左侧设备栏、指标磁贴、设备列表切换 | `pc/HarmOsWin.App/MainWindow.cs` |
| 贴合画面、FitWindowToDevice、画面占比、黑边、铺满、MiniStat | `pc/HarmOsWin.App/MainWindow.cs` |
| 缩放锁比例、WM_SIZING、AspectRatio、拖动缩放、宽高比、_windowAspect、窗口拉不动 | `pc/HarmOsWin.App/MainWindow.cs` → `OnWndProc` |
| 缩放弹回、弹回原大小、CorrectAspectIfNeeded、分辨率上报去重 | `pc/HarmOsWin.App/MainWindow.cs` → `CorrectAspectIfNeeded` |
| 画面静止、静止判定、变化才出帧、SlowScore、断流区分 | `pc/HarmOsWin.App/MainWindow.cs` → `Refresh` |
| 重复上一帧、REPEAT_PREVIOUS_FRAME_AFTER、静止出帧、重连首帧慢、REPEAT_PREVIOUS_MAX_COUNT | `tablet/entry/src/main/cpp/harmoscap.cpp` → `buildFormat` |
| MF 解不出、dec=0、0 帧 0 错误、重复帧不输出、NEED_MORE_INPUT、首帧卡住、IDR 门闩、needIdr、参数集缓存、SPS 自举、裸 IDR 挂起 | `pc/HarmOsWin.Core/MfH264Decoder.cs` → `FeedCore` |
| PC 主题、深色配色、Ui/Theme.cs | `pc/HarmOsWin.App/Ui/Theme.cs` |
| 自绘按钮、FlatButton、StatTile、StatusPill、Ui/Controls.cs | `pc/HarmOsWin.App/Ui/Controls.cs` |
| gate02、端到端控制台、帧重组报告 | `pc/HarmOsWin.Probe/Gate02.cs` |
| gate03_probe、注入延迟对比、tconn 重连 | `scripts/gate03_probe.py` |
| RtoEstimator、VideoAssembler、RTO 估计、码流重组 | `pc/HarmOsWin.Transport/Channels.cs` |
| ControlRtoInitMs、等待设备画面、CMD_STREAM 下发失败、握手失败、streaming 未置位 | `pc/HarmOsWin.Transport/HwpPacket.cs` → `Hwp.ControlRtoInitMs` |
| GATE-02 实测、235 帧、零丢帧 | `tmp/gate02_result.json` |
| GATE-03 实测、DEC-08、注入通道失败 | `tmp/gate03_result.json` |
| 设备端注入探针、inputEventClient、TouchController、801 错误码、InputSimulator | `tablet/entry/src/main/ets/core/InputInjector.ets` |
| gate05、注入探针控制台、端到端注入延迟、握手重试 | `pc/HarmOsWin.Probe/Gate05.cs` |
| CMD_INJECT_RESULT、0x80 命令字、InjectReport 线格式、19B 定长 | `docs/传输层协议-HWP-v1.md` → §8 / §8.5 |
| CONTROL_DEVICE、2in1 设备形态白名单、ACL profile、无障碍手势注入、GATE-05 | `AGENTS.json` → facts.measured / DEC-08 / GATE-05 |
| GATE-05 实测、20/20 注入失败、权限未授予 -1 | `tmp/gate05_r2.json` |
| 注入统计截图、设备端 801 日志 | `tmp/g5_r2_result.jpeg` |
| INV-13、需求 2 降级、三键导航 低频点击、DEC-08 结案 | `AGENTS.json` → invariants.INV-13 / decisions.DEC-08 |
| GATE-06、低频判据 200ms、无障碍落地、AccessibilityExtensionAbility | `AGENTS.json` → verification_gates.GATE-06 |
| PasteButton、剪贴板上行、SECURE_PASTE、getDataSync、点即授权、防回环、pushClipText | `tablet/entry/src/main/ets/pages/SessionPage.ets` |
| 拖动转发、滚轮转发、touchMove、AxisDown、PointerAsync、DragSession、零延迟点击、uitest_pointer_api_full | `pc/HarmOsWin.App/MainWindow.cs` |
| hdc target 必须带端口、fport 残留规则、daemon 脆弱性、Not match target | `AGENTS.json` → facts.hdc_target_must_include_port |
| 单文件发布、PublishSingleFile、EmbeddedResource、ResolveAgentSoPath、harmoswin.json、双击即连、NETSDK1176 | `pc/HarmOsWin.App/HarmOsWin.App.csproj` |
| 重复连接、多会话、device-1 device-2、Dispatch 按 host、Add 去重、FindByHost、--dup-test、只有一张静止图 | `pc/HarmOsWin.Core/DeviceSession.cs` |
| 免授权采集、libscreen_casting、scrcpy_grpc_socket、ScrcpyService onStart、NativeStartScreenCopy、uitest 扩展、HOScrcpy-python、DEC-09 | `docs/参考项目-能力清单.md` |
| frameRefreshInterval、frameRate 无效参数、89fps、14.88Mbps、h264_probe、NAL 解析、so 版本敏感、hdc 会话互踢、动态画面 | `tmp/h264_probe.py` |
| 本地低频桥、LowFreqBridge、loopback 9021、250ms 超时、服务未响应 -2 | `tablet/entry/src/main/ets/accessibility/LowFreqBridge.ets` |
| AccessibilityExtAbility、accessibility_config、无障碍扩展服务、performAction 归属 | `tablet/entry/src/main/ets/accessibility/AccessibilityExtAbility.ets` |
| gate06、低频输入控制台、三键 单点判据、200ms 门限 | `pc/HarmOsWin.Probe/Gate06.cs` |
| GATE-06 基线、8/8 回传、serviceConnected false | `tmp/gate06_baseline.json` |

> **证据文件清理说明（2026-11-16 20:08）**：清理 `tmp/` 非必要文件时，大块原始媒体证据
> `h264_long30s.bin`、`gate02_frame4.bmp`、`s1.mp4`、`gate02live.h264`、`gate02live4.h264`、
> `gate01.mp4`、`harmos.hap`、`harmos-entry.hap`（共 8 文件 / 100.22 MB）已送 Windows 回收站。
> 本表及 `INDEX.json` / `PROGRESS.json` / `AGENTS.json` / `SESSIONS/` 中指向这些文件的路径均为
> **历史记录**，实测结论数字仍然有效；如需原始文件，按 `tmp/media_evidence_delete_20261116.json`
> 中的原路径与 md5 从回收站还原。文本结论、`tmp/refout/` 与验证脚本均保留。

---

| C++ NAPI、OH_AVScreenCapture、OH_VideoEncoder、StartScreenCaptureWithSurface、TSFN、native 直发、实时采集、编码 | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| CMakeLists、externalNativeOptions、libharmoscap、abiFilters | `tablet/entry/src/main/cpp/CMakeLists.txt` |
| NativeVideoSource、setFrameSink、推流、实时源 | `tablet/entry/src/main/ets/capture/VideoSource.ets` |
| Media Foundation、COM interop、IMFTransform、MFT_OUTPUT_DATA_BUFFER、vtable | `pc/HarmOsWin.Core/MfInterop.cs` |
| MF 硬解、H.264 解码、NV12、BGRA、CMSH264DecoderMFT | `pc/HarmOsWin.Core/MfH264Decoder.cs` |
| SPS、分辨率自举、exp-golomb | `pc/HarmOsWin.Core/H264Sps.cs` |
| gate02live、实时验证、首帧延迟、fps | `pc/HarmOsWin.Probe/Gate02Live.cs` |
| mfde、离线解码、访问单元 | `pc/HarmOsWin.Probe/MfDecodeFile.cs` |
| GATE-02 复测、peakFps 62、activeFps 51.14、1507 帧、0 解码错误、23.3Mbps、首帧 10.77s | `tmp/gate02live4.json` |
| 实时画面、1600x2560、人工核对 | `tmp/gate02_frame4.bmp` |
| native 直发、setStreamPeer、setDirectSend、分片 sendto | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| 帧率口径、peakFps、activeFps、Gate02Live | `pc/HarmOsWin.Probe/Gate02Live.cs` |
| 流畅度实测、60fps 上屏、activeFps 59.84、p99 31ms、0 丢包、延迟 8.5ms | `tmp/diag1.json` |
| 暂停推流、暂停发送、setStreamActive、免重新授权、请求 IDR | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| pauseStream、resumeStream、暂停投屏、继续投屏、免授权 | `tablet/entry/src/main/ets/core/SessionService.ets` |
| paused、capText、三态按钮、@State | `tablet/entry/src/main/ets/pages/SessionPage.ets` |
| SendCachedIdrSplit、追加重放、参数集拆帧、缓存关键帧、napi_create_int64、BigInt、double、NAPI 类型、getStats | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| pending、分辨率未到、待初始化帧、挂起队列 | `pc/HarmOsWin.Core/MfH264Decoder.cs` |
| GATE-02 复核、peakFps 62、activeFps 46.49、p99 52ms、0 解码错误、延迟 11.4ms | `tmp/gate02_clean.json` |
| 暂停恢复首帧 102ms（HWP_RESUME_AT=15） | `tmp/gate02_resume2.json`、`tmp/resume_run2.log` |
| WPF 窗口上屏人工验收、presented 100、写位图 3.0ms、延迟 51ms | `tmp/app_ok.png`、`tmp/app_ok.jsonl` |
| 参数集随 IDR 缓存、CODEC_DATA、SPS/PPS | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| IDR缓存、自举、重放前置 | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| pacing、分批重放、usleep、恢复首帧 | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| CMD_RESOLUTION、分辨率重报、去重、ResolutionChanged | `pc/HarmOsWin.Core/DeviceSession.cs` |
| 分辨率重报去重、ResolutionChanged | `pc/HarmOsWin.Core/DeviceSession.cs` |
| 快照时机、--snapshot-delay、--auto 会话日志 | `pc/HarmOsWin.App/MainWindow.cs` |
| 收包解码分离、RCVBUF 8MB、只保留最新帧 | `pc/HarmOsWin.Core/DeviceSession.cs` |
| 分辨率周期重报、startResolutionBeacon | `tablet/entry/src/main/ets/core/SessionService.ets` |
| 恢复首帧 797ms、activeFps 59.96、暂停恢复 | `tmp/rs3.json` |
| 缓存 IDR、重放、ScheduleTrailingReplay、恢复推流 | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| 长时任务、KeepAlive、息屏保活 | `tablet/entry/src/main/ets/core/SessionService.ets` |
| 调暗屏幕、窗口亮度、视觉息屏降级 | `tablet/entry/src/main/ets/pages/SessionPage.ets` |
| 历史 GATE-02、161 帧、6.4fps、首帧 14.9s（口径已作废，见上条） | `tmp/gate02live.json` |
| 会话页 UI、卡片式控制台、返回入口、指标卡、状态胶囊、等待 PC、日志折叠 | `tablet/entry/src/main/ets/pages/SessionPage.ets` |
| 结构化指标、captureMetrics、CapStats、帧率增量、码率增量 | `tablet/entry/src/main/ets/core/SessionService.ets` |
| 历史解码画面、首帧 BMP、人工核对（旧版） | `tmp/gate02_frame_preview.jpg` |
| 方案 B、EACCES、SELinux、debug_hap、uitest 域、connectto、抽象 socket、peerUid、probeUitestSocket | `tablet/entry/src/main/cpp/harmoscap.cpp` |
| 三键注入、InjectNavAsync、EnsureRpcAsync、回退 hdc、nav-test | `pc/HarmOsWin.App/MainWindow.cs` |
| 点击转发、坐标映射、canvas_tap_mapping、落点越界、预热注入 | `docs/架构-HarmOs-Win.md` |
| TryMapToDevice、tap-test、tap-delay、letterbox、双击窗口 | `pc/HarmOsWin.App/MainWindow.cs` |
| agent.so 部署、随包分发、md5 校验、空壳 daemon、部署顺序、EnsureAgentAsync、EnsureDaemonAsync、hdc file send、assets/agent.so | `pc/HarmOsWin.Core/UitestRpcClient.cs` |

## 三条不能碰的红线

1. **默认只走无线调试**，USB 线插上会把 TCP 通道踢掉；唯一例外是 P2P 验证期显式 `mode usb`，且必须恢复并补测。
2. **hdc 必须串行**，两条并行会话立刻死。
3. **构建/装机要逐次授权**，改完代码不等于可以编译。

---

## 许可证注意

参考项目里 `oink` 是 GPL-3.0（衍生必须开源），`hnscrcpy` 内置华为 jar 禁商用，`lanlink` 仓库无 LICENSE。**只能看架构思路，代码一行都不能复制。**
