# 补记（76）PC 端 hdc 调用层重构：四个真 bug + 一个新发现

**本轮目标**：修 `fport` 阻塞，打通手机 ref 采集。

## 已修复的四个真 bug（都有实测对照）

### 1. `Process.Kill(true)` 自阻塞

递归终止进程树时，如果子树里有不可终止的进程，`Kill(true)` 自己会阻塞，
把 4 秒超时拖成 **70 秒以上**。改为 `Kill(false)`。

### 2. 设备端管道命令必须断开 stdin（关键）

`hdc shell "ps -ef | grep uitest"` 里的 `|` 会让设备端 grep 等待 hdc 转发的空 stdin
→ hdc 不返回、输出 **0 字节**，只在 WPF 里复现（控制台 0.3 秒正常）。

试过加 `< /dev/null`，但本机 ROM 的 SELinux 直接报 `Permission denied`。

**最终解法：设备端不用任何管道**，拿 `ps -ef` 全量（约 17KB），过滤放在 C# 侧。

### 3. `ps -A` 在这台手机上看不到 shell 起的后台 daemon

实测对照：

```
ps -A  | grep uitest  → 0 条   ← 漏
ps -ef | grep uitest  → 3 条   ← 对
```

误用 `ps -A` 会让 `KillDaemonsAsync` 永远认为「没有残留」，
旧 daemon 不被清理，新 daemon 绑不上 socket → 0 帧。
**PID 列位置也跟着变**：`ps -ef` 是 `f[1]`，`ps -A` 是 `f[0]`。

### 4. WPF 进程的 stdin 句柄无效

WPF 是 GUI 子系统进程，没有控制台。若 `RedirectStandardInput = false` 让 hdc 继承它，
hdc 读 stdin 直接失败 → **进程瞬间退出、输出 0 字节、且不抛任何异常**。

必须 `RedirectStandardInput = true` 并保持流打开。

## 试过但无效的方案（都已实测，别再重复）

| 方案 | 结果 |
|---|---|
| `ReadToEndAsync` + `WaitForExit` 并行 | WPF 里偶发永久挂起（管道 EOF 不到） |
| 先 `WaitForExit` 再读 | 17KB 输出写满管道 → 双向死锁 |
| `cmd.exe /c` 重定向到文件 | ①`ArgumentList` 会套引号致解析失败 ②`/c` 有剥首尾引号规则 ③本机安全策略禁止程序化启动 cmd |
| Git `sh.exe -c` 重定向 | sh 进程残留不退出（观察到 3 个僵尸） |
| `FileStream` + `FileOptions.Asynchronous` | 输出 0 字节，静默失败 |
| `BeginOutputReadLine` 事件回调 | 句柄被 GC 根拖住，第 8 次调用起卡死 |
| `Task.Run` 里同步读 | WPF 线程池线程受 SynchronizationContext 影响 |
| 独立 `Thread` + MTA | 同上，无效 |

## 新发现：单设备 vs 双设备的巨大差异

`harmoswin.json` 里**只有手机一台**时，触控通道 ①→⑦ 全部在 1 秒内跑通：

```
① tconn OK
② agent.so md5 一致
③ ps -ef OK（316ms）
④ grep -a OK（309ms）→ localabstract:uitest_socket
⑤ fport rm OK
⑥ fport add OK（165ms）
⑦ TCP 已连上 → 参考触控通道就绪（裸 JSON / 持久 TCP）
```

双设备（加平板）时，同样的代码在**随机步骤**卡住。
原因是启动期 `HdcTargetResolver` 要先解析两台设备，
平板在 `192.168.1.100` 上解析失败（日志里是 `来源 config` 而非 `live`），
留下不确定的 hdcd 会话状态。

**结论：单设备链路已经通了。双设备需要单独处理平板解析失败的问题。**

## 当前状态

- **代码**：`HdcRunner.cs` 已抽出统一 hdc 调用层（同步读、清代理变量、
  `RedirectStandardInput=true`、`Kill(false)`、5s 超时、`HdcSerial` 全局锁）。
  `ReferenceCapture` 与 `ReferenceTouchChannel` 都已切过去。
- **触控通道**：单设备 100% 跑通。
- **采集通道**：能进到 `ps -ef` 并成功，但后续步骤仍会随机卡（fport add / 部署 so）。
  **尚未取得第一帧。**
- **设备侧**：手机 `@scrcpy_grpc_socket` 与 `@uitest_socket` 两个 daemon 可并存。
  `fport` 转发 `localabstract:scrcpy_grpc_socket` 实测返回 `Forwardport result:OK`
  且本机端口处于 LISTENING —— **设备侧 ref 链路成立，卡点全在 PC 端。**