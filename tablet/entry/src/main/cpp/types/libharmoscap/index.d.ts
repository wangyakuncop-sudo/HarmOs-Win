// libharmoscap.so 的类型声明（与 cpp/harmoscap.cpp 的导出函数一一对应）
// 返回码 0 = 成功；1~12 为采集/编码失败阶段，见 harmoscap.cpp 的 ERR_* 枚举。

export interface CapStats {
  frames: number;
  bytes: number;
  keyframes: number;
  width: number;
  height: number;
  /** OH_AVScreenCaptureStateCode：0=STARTED 1=CANCELED 2=STOPPED_BY_USER -1=未启动 */
  state: number;
  /** 直发模式下已发送的 UDP 分片包数 */
  packets: number;
  sendErrors: number;
  /** 1 = 编码器回调线程直接 sendto（不经过 JS 线程），0 = TSFN 回传由 ArkTS 分片 */
  directSend: number;
  /** 缓存 IDR 被重放的次数：恢复推流后 > 0 说明走了「立刻出画面」路径 */
  idrReplays: number;
  /** 当前缓存的 IDR 大小（KB），0 表示还没缓存到关键帧 */
  idrCachedKb: number;
}

export type FrameCallback = (data: Uint8Array, keyframe: boolean, ptsUs: number) => void;

export const startCapture: (
  width: number,
  height: number,
  fps: number,
  bitrateKbps: number,
  onFrame: FrameCallback
) => number;

export const stopCapture: () => number;

export const getStats: () => CapStats;

/** 0 成功 / -1 参数无效 / -2 socket 失败 / -3 地址解析失败 */
export const setStreamPeer: (host: string, port: number) => number;

export const setDirectSend: (enabled: boolean) => number;

/**
 * 只切换「发不发」：false 时采集与编码继续运行，避免重新弹录屏授权（每次约 9.9s）。
 * true 时先把缓存的 IDR 立刻重放一遍（静止画面下不必等新帧），再请求一个新 IDR。
 * 重放沿用原始时间戳，不会伪装成低延迟新帧。
 */
export const setStreamActive: (enabled: boolean) => number;

/**
 * 方案 B 可行性探针（2026-11-08）：在应用进程内直连 agent.so 的抽象 unix socket
 * `@uitest_socket`，可选发送一条 Gestures 请求。
 *
 * 验证两件事：
 *   1) connectOk —— 应用域（normal_hap）能否通过 SELinux 的 unix_stream_socket connectto；
 *   2) gotTail / resp —— agent.so 是否对 Peer UID 做白名单校验（校验则会被拒或空回）。
 *
 * @param api  Gestures 子命令，如 "touchDown" / "touchUp" / "pressBack"；传 "" 只建链不发请求
 * @param x    触点 x（默认 800）
 * @param y    触点 y（默认 1900）
 * @returns 一行 JSON，见 ProbeUitestResult
 */
export const probeUitestSocket: (api: string, x: number, y: number) => string;

export interface ProbeUitestResult {
  connectOk: boolean;
  /** connect 失败时的 errno：13=EACCES（多为 SELinux 拒绝）2=ENOENT 111=ECONNREFUSED */
  connectErrno: number;
  /** SO_PEERCRED 拿到的对端 pid/uid，确认连到的确实是 agent.so */
  peerPid: number;
  peerUid: number;
  sent: boolean;
  sentBytes: number;
  recvBytes: number;
  /** 收到结束标记 `_uitestkit_rpc_message_tail_`，说明 agent 正常回了完整包 */
  gotTail: boolean;
  connectMs: number;
  rpcMs: number;
  api: string;
  /** 响应原文前 400 字节 */
  resp: string;
}

/** 在应用进程内 dlopen 扩展库并取 UiTestExtension_OnInit（不执行采集）。 */
export const loadExtLibrary: (path?: string) => string;

/** 调用 UiTestExtension_OnInit（阻塞，内部起 grpc server）。 */
export const callExtInit: (path?: string) => string;
