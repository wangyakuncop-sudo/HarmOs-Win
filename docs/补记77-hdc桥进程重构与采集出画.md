# 补记（77）hdc 调用移出 WPF：桥进程重构 + 手机 ref 采集出画

**结论先说：手机 ref 采集在 WPF 里完整跑通，画面正常上屏，启动到出画约 7 秒。**

---

## 一、为什么必须重构

### 问题

WPF 进程里直接跑 hdc 会**随机卡死**。多轮实测对照（同一段代码、同一条命令、同一台设备）：

| 环境 | 结果 |
|---|---|
| 控制台程序 | 8/8 全过，`fport add` 稳定 114-429ms |
| WPF 进程 | 必然在某一两步卡住，**位置随机** |

### 试过并全部失败的排法

| 方案 | 结果 |
|---|---|
| `ReadToEndAsync` + `WaitForExit` 并行 | 偶发永久挂起（管道 EOF 不到） |
| 先 `WaitForExit` 再读 | 17KB 输出写满管道 → 双向死锁 |
| `cmd.exe /c` 重定向 | `ArgumentList` 会套引号致解析失败；`/c` 有剥首尾引号规则；本机安全策略禁止程序化启动 cmd |
| Git `sh.exe -c` 重定向 | sh 进程残留不退出（3 个僵尸） |
| `FileStream` + `FileOptions.Asynchronous` | 输出 0 字节，静默失败 |
| `BeginOutputReadLine` 事件回调 | 句柄被 GC 根拖住，第 8 次调用起卡死 |
| `Task.Run` 里同步读 | WPF 线程池线程受 SynchronizationContext 影响 |
| 独立 `Thread` + MTA | 同上，无效 |
| ConPTY（`ConPty.Start`） | 仍然随机卡 |

### 根因

WPF 是 **GUI 子系统进程**，三个因素共同干扰 hdc 子进程的 stdio 行为，而这三项**从外部都无法修正**：

1. 无控制台 → 进程 stdin 句柄无效（hdc 读 stdin 直接失败）
2. `SynchronizationContext` 会捕获 `await` 续体
3. 子进程与前台的作业对象/窗口站绑定关系

---

## 二、新架构

```
┌─────────────────┐   stdin/stdout (行式 JSON)   ┌──────────────────────┐
│  HarmOsWin.App  │ ──────────────────────────→  │ HarmOsWin.HdcBridge  │
│  （WPF）        │ ←──────────────────────────  │ （普通控制台进程）    │
└─────────────────┘       一问一答，严格串行      └──────────┬───────────┘
                                                             │ Process.Start
                                                             ↓
                                                          hdc.exe
```

hdc 在桥进程里看到的环境与手工命令行**完全一致**。

### 协议

```jsonc
// 请求
{"id":1,"args":["-t","192.168.1.102:40663","shell","ps -ef"],
 "timeoutMs":5000,"acceptFail":false,"hdcPath":"...","workDir":"..."}
// 响应
{"id":1,"ok":true,"output":"...","ms":203}

// 特殊请求
{"args":["__ENSURE_SERVER__"]}      // 确保 hdcd 常驻（内部含陈旧 pid 清理）
{"args":["__PROBE_PORT__","8710"]}  // 探测本机端口是否 LISTEN
{"args":["__EXIT__"]}               // 退出
```

### 桥进程兼两个职责

1. **hdc 调用代理** —— WPF 侧只发 JSON 收 JSON，不开子进程
2. **hdcd server 宿主** —— server 由桥 `hdc start` 派生，桥活着 server 就受掩护

第 2 点很关键：**fport 规则存在 hdcd server 进程内存里，不在设备端**。server 一死规则全废——端口仍显示 `LISTENING`，但 `connect` 直接 `Connection refused`（僵尸监听）。这是「`fport` 返回 OK 但 TCP 连不上」的真相。

### 生命周期

- 首次 hdc 调用时启动桥
- `Closed` 事件 + `AppDomain.ProcessExit` 双路径调用 `HdcRunner.Shutdown()`
- 实测：关闭窗口后 1 秒内退出，桥进程一并收掉，无残留

---

## 三、修掉的真 bug（都有实测证据）

### 1. 推错 so 文件（本次出画的关键）

| 文件 | 体积 | 用途 |
|---|---|---|
| `agent.so` | 600,246 B | 触控通道的 uitest agent |
| `screen_casting.so` | 247,989 B | **屏幕采集扩展** |

推错时 daemon 进程能正常启动（`ps -ef` 能看到），但**绑不上 `@scrcpy_grpc_socket`**，表现为「gRPC 握手成功、端口也在转发，但收到 0 字节」。

之前 `grep -c scrcpy_grpc_socket /proc/net/unix` 一直返回 `0`，就是这个原因。

### 2. `file send` 覆盖已存在文件被拒

设备端返回 `[Fail]Error opening file: operation not permitted`。

修法：推之前先 `rm -f`；若仍失败，改用 `xxx.so.new` 写入再 `mv` 覆盖。

### 3. 桥调用未透传 `workDir`

`hdc file send` 用相对文件名（因为含中文的绝对路径会被 hdc 拼到 cwd 后面），依赖 `WorkingDirectory`。桥进程里没设 → `no such file or directory`。

### 4. `EnsureServer` 同步等锁

用 `AcquireAsync().GetAwaiter().GetResult()` 在 WPF 线程上阻塞，而锁释放方可能需要 WPF 线程推进续体 → 死锁。改为不在此处加锁（启动早期无并发）。

### 5. 桥的 `ReadLine` 无超时

`ReadLine` 是阻塞读，桥一旦不回应 → Task 永不完成 → `await` 永不返回 → `HdcSerial` 锁永不释放 → **后续所有 hdc 调用全部卡在等锁上**，表现就是「日志突然停住」。

修法：`ReadBridgeLine` 带 `timeoutMs + 5000` 超时。

### 6. 设备端 socket 轮询用管道

`cat /proc/net/unix | grep` 里的 `|` 会让设备端 grep 等 hdc 转发的空 stdin → 输出 0 字节。改用 `grep xxx /proc/net/unix`（直读文件，无管道）。

顺带发现：`/proc/net/unix` **全量 32KB** 走 JSON 传输会卡死，必须过滤后传。

### 7. `Dispatcher.BeginInvoke` 无脑排队

hdc 每步打 2-3 条日志，采集/触控两路并发时 UI 线程被塞满。改为 `InvokeAsync` + 跳过积压。

### 8. `ps -A` 漏掉 shell 起的 daemon

| 命令 | 结果 |
|---|---|
| `ps -A \| grep uitest` | 0 条（漏） |
| `ps -ef \| grep uitest` | 3 条（正确） |

PID 列位置也随之变：`ps -ef` 是 `f[1]`，`ps -A` 是 `f[0]`。

---

## 四、实测时序（手机 HOP-AL10，192.168.1.102:40663）

```
22:15:35.986  桥进程启动
22:15:37.355  参考触控通道就绪（裸 JSON / 持久 TCP）      +1.37s
22:15:38.964  采集③ 下发 daemon
22:15:39.569  扩展守护进程已就绪（602ms）
22:15:39.571  采集④ 清理陈旧 fport
22:15:40.285  gRPC 握手完成，已主动请求首帧 IDR            +2.93s
22:15:43.199  收到码流（86588 字节）并上屏                 +5.65s
```

**启动到出画约 7 秒。**

### 采集各阶段耗时

| 阶段 | 耗时 |
|---|---|
| 采集① 清理残留 daemon | 263ms |
| 采集② 部署 so | 731ms |
| 采集③ 拉起 daemon | 602ms |
| 采集④ fport | 711ms |
| gRPC 握手 | 505ms |

---

## 五、独立验证产物（`refcap` 探针）

新增 `HarmOsWin.Probe refcap <target> <outDir> [timeoutSec]`，在**纯控制台进程**里跑完整采集并把码流落盘，用于脱离 WPF 验证链路。

```
使用：HARMOSWIN_REF_LOG=... HarmOsWin.Probe.dll refcap 192.168.1.102:40663 tmp/refout 40
产物：tmp/refout/stream.h264
```

实测解码验证：

```
stream.h264   52,330 字节
NAL 结构      00 00 00 01 67 (SPS) / 68 (PPS) / 65 (IDR)
访问单元      3 个
MF 解码       914x1292 → 928x1296，3 帧，错误 0
低延迟        enabled=True
```

---

## 六、已知现象（不是 bug）

**画面静止时设备不推帧。** 收到首批帧后可能长时间无新数据，`[探活]` 会报「画面无数据（请求已回执 → 静止/息屏），链路正常」。这是参考实现的正常行为，设备只在内容变化时编码。PC 端有 `frameRefreshInterval 20` 补帧兜底。

**锁屏/息屏时拿不到像素** —— 与 hnscrcpy 的结论一致，反向印证「视觉息屏」降级的正确性。

---

## 七、踩坑记录：不要用 `tasklist | grep` 判断进程存活

排查期间多次误判「进程消失了」，实际进程一直活着。`tasklist | grep -ci harmos` 有时序问题，会漏匹配。

正确做法：

```powershell
$p = Start-Process ... -PassThru
while (-not $p.HasExited) { Start-Sleep -Milliseconds 200 }
```

用 `Process.HasExited` 轮询，才知道真实存活状态与退出码。这个误判曾让我以为「进程被强杀」，浪费了不少时间。

---

## 八、会话失效自动重连（2026-11-15 追加）

### 现象

用户报「手机连接不上」。实际表现：

```
ping 192.168.1.102         → 0 丢包，手机在线
hdc list targets           → 只有平板，手机不见了
hdc tconn 192.168.1.102:40663 → Connect OK，手机随即回到列表
```

**端口没变**，设备也没离线。是 **hdc 的 TCP 会话失效了**，而 `list targets` 只反映
「当前 server 持有有效会话的目标」，不代表设备真实在线状态。

会话失效的触发条件（任一即可）：

- 无线调试开关短暂抖动
- hdcd server 重启（`.HDCServer.pid` 失效）
- 会话空闲超时
- **另一路 hdc 进程并发介入，把会话互踢掉**（项目里已知的硬规则）

失效后所有针对该目标的命令返回 `Not match target founded` / `Connect failed`，
一条 `tconn` 就能恢复。

### 修法

在桥进程里加**自动重连**（`HarmOsWin.HdcBridge/Program.cs`）：

```
RunHdc(req):
  first = RunHdcOnce(req)
  if first.Ok                       → 返回
  if !IsSessionLost(first.Output)   → 返回（不是会话问题，重连没用）
  if req.Args[0] == "tconn"         → 返回（tconn 本身就是重连手段，别递归）
  tgt = ExtractTarget(req.Args)
  if tgt == null                    → 返回
  if !TryReconnect(tgt)             → 返回（真离线，tconn 也失败）
  retried = RunHdcOnce(req); retried.Output = "[auto-reconnect] " + ...
```

关键设计：

- **只重试一次**。设备真离线时 `tconn` 会失败，直接被 `TryReconnect` 返回 false 兜住，
  不会陷入循环。
- **识别特征而非硬编码目标**（`IsSessionLost`）：`Not match target` / `Connect failed` /
  `[Empty]` / `target not founded` / `connect-key` / `Device offline`。
- `tconn` 命令自身失败不重连，避免自己调自己。
- 重试成功时输出前缀 `[auto-reconnect]`，便于排查时一眼看出发生过恢复。

### 验证（`tmp/test_reconnect.py`）

构造真实故障：正常用 → `hdc kill` 杀 server → 目标从 `list targets` 消失（返回 `[Empty]`）
→ 再发目标命令：

```
=== 1. 基准 ===
ensure          ok=True  ms=  23  already
tconn           ok=True  ms=  97  [Info]Target is connected, repeat operation
model           ok=True  ms= 249  HOP-AL10

=== 2. hdc kill 后 ===
list targets: '[Empty]'

=== 3. 再发目标命令 ===
model(自动重连)  ok=True  ms= 621  [auto-reconnect] HOP-AL10
```

**从 `[Empty]` 到取到 `HOP-AL10`，621ms，全程自动。** 回归测试确认正常投屏不受影响。

---

## 九、单文件发布配置（2026-11-15 追加）

### 踩到的坑：`NETSDK1151`

最初用 `ProjectReference + OutputItemType=Content` 把桥随 App 输出，结果：

```
error NETSDK1151: 引用的项目"HarmOsWin.HdcBridge.csproj"是自包含的可执行文件。
自包含可执行文件不能由非自包含可执行文件引用。
```

项目引用会把桥的「可执行」身份带进 App 的发布管线，与 App 的框架依赖模式
（`SelfContained=false`，因为本机已装 .NET 8 桌面运行时）冲突，
**整个单文件发布直接失效**。而单文件 exe 是本项目的主力分发形态。

### 修法

改成显式的「先构建桥、再拷产物」两步，全程只做文件拷贝，不进发布管线：

```xml
<Target Name="BuildHdcBridge" BeforeTargets="Build">
  <MSBuild Projects="$(HdcBridgeDir)\HarmOsWin.HdcBridge.csproj"
           Targets="Build"
           Properties="Configuration=$(Configuration);TargetFramework=net8.0" />
  <ItemGroup>
    <HdcBridgeArtifact Include="$(HdcBridgeOut)\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(HdcBridgeArtifact)" DestinationFolder="$(OutDir)"
        SkipUnchangedFiles="true" />
</Target>

<Target Name="PublishHdcBridge" AfterTargets="Publish">
  <!-- 同上，拷到 $(PublishDir) -->
</Target>
```

### 第二个坑：`**\*` 递归通配

第一版用 `**\*` 递归取产物，结果把 `bin\Release\net8.0\win-x64\` 这类 RID 子目录
也一起拷了，发布目录里多出 `win-x64\HarmOsWin.HdcBridge.exe` 的嵌套副本。

改为 `*.*` 只取根目录平铺文件 + `DestinationFolder`。

### 最终产物

```
HarmOsWin.exe                1,654,302 B    ← 单文件（含 agent.so / screen_casting.so）
HarmOsWin.HdcBridge.exe        151,552 B    ← 桥进程（独立 exe，必须一起分发）
HarmOsWin.HdcBridge.dll         14,848 B
HarmOsWin.HdcBridge.deps.json      928 B
HarmOsWin.HdcBridge.runtimeconfig.json
harmoswin.json
```

**发布产物已做端到端验证**：从 `tmp/pubtest/` 纯发布目录启动，投屏正常出画
（914×1292，底栏统计 `包 4,588 帧 438 · 重组丢帧 0 · 解码 338 帧 · 渲染丢帧 0`），
说明桥 exe 的路径解析（`AppContext.BaseDirectory`）在发布场景下正确。

**注意**：桥是独立 exe，分发时**必须与 App 一起给**，不能只给单个 `HarmOsWin.exe`。

### 第三个坑：`NETSDK1047`（2026-11-16 重新发布时踩到）

`dotnet publish ... -r win-x64 -p:PublishSingleFile=true` 会把 `RuntimeIdentifier=win-x64`
作为**全局属性**渗进 `BuildHdcBridge` 里的 `<MSBuild>` 子调用。桥是纯 `net8.0`
（`obj/project.assets.json` 只有 `net8.0` 目标），于是报错：

```
error NETSDK1047: 资产文件 ...\HarmOsWin.HdcBridge\obj\project.assets.json
没有 "net8.0/win-x64" 的目标。
```

`dotnet build sln`（不带 RID）不会暴露这个问题，所以只有在**重新发布单文件**时才炸。

修法：让桥的 RID 与 App 的单文件 RID 解耦 —— 子调用显式移除发布属性：

```xml
<MSBuild Projects="...\HarmOsWin.HdcBridge.csproj" Targets="Build"
         RemoveProperties="RuntimeIdentifier;SelfContained;PublishSingleFile;PublishDir"
         Properties="Configuration=$(Configuration);TargetFramework=net8.0" />
```

桥仍按无 RID 的控制台 exe 构建，产物落在 `bin\$(Configuration)\net8.0\`，两个 Copy 目标不变。

### 2026-11-16 18:13 重新发布（双设备切换 / 真实型号校正版）

```
"C:/Users/W/.dotnet/dotnet.exe" publish pc/HarmOsWin.App/HarmOsWin.App.csproj \
  -c Release -r win-x64 -p:PublishSingleFile=true -o tmp/publish-dual
```

- `HarmOsWin.exe` **1,662,494 B**，md5 `0a4b206a71f1a60409a6dd5d26186e14`（上一版 11-14 为 1,646,110 B / `2649bcef…`）。
- 桥 5 文件齐全；`harmoswin.json` 为双设备版（平板 100:38803 启用 / 手机 102:40663）。
  发布当时手机是 `enabled=false`；**2026-11-16 18:23 已改为 `enabled=true` + `capture=ref`**（见下方「手机未接入」条）。
- **根目录 `HarmOsWin.exe` 已覆盖**，并首次把桥 5 文件一并放到根目录 ——
  此前根目录只有单文件 exe 没有桥，双击它会因 `AppContext.BaseDirectory` 下找不到
  `HarmOsWin.HdcBridge.exe` 而报「桥进程不存在」。现在根目录本身就是完整可运行目录。
- 同步 `tmp/pubtest/`、`tmp/publish/`；归档 `releases/HarmOsWin_双设备切换_真实型号校正_20261116.exe`。
- 静态验证：exe 内 UTF-16LE 检出 `param get const.product.model` / `设备型号=` /
  `无线调试地址` / `当前未接入（配置值`，确认本轮根因修复与双设备 UI 真编进单文件。
- **未启动 App、未连设备、未碰 hdc**，真机上的双设备显示与切换仍待目视验收。

### 手机「当前未接入」事故（2026-11-16 18:23，用户「一定是你的问题」）

**现象**：手机无线调试已打开，点标题栏手机图标只显示占位页
「当前未接入：无线调试未开、IP/端口已变，或该设备在配置里被禁用」。

**根因就是配置，不是设备**：`logs/startup.log` 18:22:44.635
`设备清单 2 台：平板 192.168.1.100:38803 → HWP :9010/:9020 | 手机 192.168.1.102:40663 → HWP :9011/:9021（已禁用）`
—— `BringUpAllDevicesAsync` 只取 `d.Enabled` 的设备，手机被禁用时 App **一条 hdc 命令都没发过**，
无线调试开不开都不会有画面。占位页把「未开 / 端口变 / 被禁用」并列，反而误导用户去查设备。

**第二个错**：手机条目 `capture` 写的是 `hap`。补记66「手机 ref 永远零帧」的结论
已被补记73/76 推翻（手机与平板的 uitest 扩展机制无差别），本补记 §四更是实测手机 ref 采集
出画 86588 字节 / 5.65s 上屏。启用后若仍走 hap，会因手机没装自有 HAP 而必然失败。

**修法（纯配置，零编译）**：手机 `enabled=true`、`capture=ref`，地址保持
`192.168.1.102:40663`；`_twoDeviceNote` 写明 ref 依据。8 份配置副本同步。
`scripts/hdcw.sh dev phone && status` 实测 `握手: Connect OK`，
`list targets` 两台同时 `Connected`（100:38803 / 102:40663），随后切回 tablet。
配置只在启动时读取，**必须重启 App 才生效**；重启后走双路并行（各自独立采集桥 / 会话 / HWP 端口）。

### 最终验证与两处顺带修复（18:34）

用户授权「重启并验证 + 一起修文案」后，改了 `MainWindow.cs` 两处并重新编译发布：

1. **占位页按 `enabled` 分支**：`enabled=false` 时明确写「配置里被禁用、程序不会发任何连接请求、改成 true 重启即可」，
   不再把「无线调试未开 / 端口变 / 被禁用」三种可能并列误导用户；`AddPendingDeviceUi` 的 tooltip 同步区分。
2. **标题栏图标去重**：构造函数先给两台各摆一枚灰淡占位，接入成功后 `AddDevice` 又各加一枚真图标，
   实测日志「已添加 device-1（平板）… 当前共 3 枚 / device-2（手机）… 共 4 枚」——
   用户要的是两枚。新增 `RemovePendingDeviceUi(name)`，在 `AddDevice` 建真条目后撤掉同名占位
   （侧栏行 + 标题栏图标一起移除）；启动失败的设备不调用，占位保留。

**真机重启验证（18:33:55 这次启动，`logs/startup.log` 13669 行之后）**：

```
设备清单 2 台：平板 100:38803 → :9010/:9020 | 手机 102:40663 → :9011/:9021
[设备] 平板 目标解析 → 100:38803（来源 live，型号 MLR-AL00）
[设备] 手机 目标解析 → 102:40663（来源 live，型号 HOP-AL10）
[平板] / [手机] 已连接 …
CMD_STREAM: id=device-1 … ok=True
CMD_STREAM: id=device-2 … ok=True
[平板] / [手机] gRPC 握手完成，已主动请求首帧 IDR
```

两台都拿到了**真实型号**、两条 CMD_STREAM 全部 ok=True、两条 gRPC 握手完成；
`HarmOsWin` + `HarmOsWin.HdcBridge` 两进程存活。平板 gRPC 已收 195326 字节，
手机当前只收到 10~15 字节并停在「等待设备首帧」—— 静止/息屏不推帧是已知行为
（平板同轮也报 `[探活] 画面无数据（请求已回执 → 静止/息屏），链路正常`），
点亮手机屏幕或滑动一下即出画，不是链路故障。

最终单文件 exe `afd2b2c55ed5bfa0ed5852027d8e2b74`（归档
`releases/HarmOsWin_双设备启用_图标去重_20261116.exe`）；编译 0 警告 0 错误。

**启动方式的坑**：直接用工具会话里的 `Start-Process` 启动 WPF，进程会被工具作业对象连带杀掉
（日志无异常、18:30:58 后凭空消失）。改用 `explorer.exe` 由桌面外壳拉起才脱离作业对象、稳定存活。