// 采集层抽象。INV-04：视频必须来自 AVScreenCapture，禁止 snapshot_display 轮询。
// 当前两种实现：
//   FileVideoSource —— 读取 AVScreenCaptureRecorder 录出的真实 H.264 文件，按 HWP MTU 分片，
//                      用于验证「真实码流 → UDP → PC 重组」链路；不是 60fps 实时源。
//   NativeVideoSource —— C++ NAPI 接入点（OH_AVScreenCapture + OH_VideoEncoder，Surface 零拷贝），
//                       GATE-02 的实时 60fps 能力依赖它，本轮尚未实现，start() 会明确报错。
// 替换实现时只需换 IVideoSource，上层 SessionService 与传输层不动。

import { fileIo } from '@kit.CoreFileKit';
import { display } from '@kit.ArkUI';
import { startCapture, stopCapture, getStats, setStreamPeer, setDirectSend, setStreamActive,
  CapStats } from 'libharmoscap.so';
import { ResolutionInfo } from '../core/SessionTypes';
import { STREAM_DATA_MAX } from '../transport/HwpPacket';

export interface VideoFrame {
  frameId: number;
  keyframe: boolean;
  width: number;
  height: number;
  data: Uint8Array;
}

export interface IVideoSource {
  start(): Promise<void>;
  stop(): Promise<void>;
  nextFrame(): Promise<VideoFrame | null>;
  currentResolution(): ResolutionInfo;
  isRealtime(): boolean;
  // 实时源支持推送：编码器出帧即回调，不依赖 33ms 轮询（轮询会把 60fps 压到 30fps）
  setFrameSink?(cb: (frame: VideoFrame) => void): void;
}

// 直发策略：native 在编码器回调线程内按 HWP 分片 sendto，JS 线程不再处理每个分片。
// 实测对比：ArkTS 逐片发送把 30fps 压到 6.4fps（每帧约 60 个包在单线程排队 + 多次拷贝）。
export const DIRECT_SEND_OK: number = 0;
export const DIRECT_SEND_NO_PEER: number = 1;
export const DIRECT_SEND_UNSUPPORTED: number = 2;

// 向下对齐：编码器要按宏块处理，宽高必须是 32 的倍数。
// 必须**向下**取整 —— 向上取整会让尺寸超出屏幕（1828→1840），编码器白白多算一截。
export function alignDown(v: number, unit: number): number {
  return Math.floor(v / unit) * unit;
}

// 按像素预算等比缩放，保持长宽比。
// 已在不超预算时原样返回，避免无谓重采样。
export function fitToPixelBudget(r: ResolutionInfo, budget: number): ResolutionInfo {
  if (r.width <= 0 || r.height <= 0) {
    return r;
  }
  const px: number = r.width * r.height;
  if (px <= budget) {
    return r;
  }
  const k: number = Math.sqrt(budget / px);
  return {
    width: Math.round(r.width * k),
    height: Math.round(r.height * k),
    folded: r.folded
  };
}

export function readDisplayProfile(): ResolutionInfo {
  try {
    const d: display.Display = display.getDefaultDisplaySync();
    return { width: d.width, height: d.height, folded: 0 };
  } catch (e) {
    // 取不到显示信息时返回「未知」，由 CMD_RESOLUTION 在可用时补齐；不硬编码分辨率（INV-06）
    return { width: 0, height: 0, folded: 0 };
  }
}

// NEXT-06：折叠/展开切换会改变分辨率，编码器需重建。这里统一发出变更通知。
export class DisplayProfile {
  private cb: ((info: ResolutionInfo) => void) | null = null;
  private registered: boolean = false;
  private last: ResolutionInfo = { width: 0, height: 0, folded: 0 };

  onChanged(cb: (info: ResolutionInfo) => void): void {
    this.cb = cb;
    if (this.registered) {
      return;
    }
    this.registered = true;
    const handler = (): void => {
      this.emit();
    };
    try {
      display.on('change', handler);
    } catch (e) {
      // 部分设备形态不支持该事件，忽略即可，仍可通过轮询 current() 获取
    }
    try {
      display.on('foldStatusChange', handler);
    } catch (e) {
      // 同上
    }
  }

  current(): ResolutionInfo {
    return readDisplayProfile();
  }

  private emit(): void {
    const now: ResolutionInfo = readDisplayProfile();
    if (now.width === this.last.width && now.height === this.last.height) {
      return;
    }
    this.last = now;
    if (this.cb !== null) {
      this.cb(now);
    }
  }
}

export class FileVideoSource implements IVideoSource {
  private path: string;
  private file: fileIo.File | null = null;
  private offset: number = 0;
  private frameId: number = 0;
  private resolution: ResolutionInfo;

  constructor(path: string, resolution: ResolutionInfo) {
    this.path = path;
    this.resolution = resolution;
  }

  async start(): Promise<void> {
    try {
      this.file = fileIo.openSync(this.path, fileIo.OpenMode.READ_ONLY);
      this.offset = 0;
      this.frameId = 0;
    } catch (e) {
      this.file = null;
      throw new Error(`打开码流文件失败: ${String(e)}`);
    }
  }

  async stop(): Promise<void> {
    if (this.file !== null) {
      try {
        fileIo.closeSync(this.file);
      } catch (e) {
        // 关闭失败不影响会话退出
      }
      this.file = null;
    }
  }

  async nextFrame(): Promise<VideoFrame | null> {
    if (this.file === null) {
      return null;
    }
    const buf: ArrayBuffer = new ArrayBuffer(STREAM_DATA_MAX * 4);
    let n: number = 0;
    try {
      n = fileIo.readSync(this.file.fd, buf, { offset: this.offset, length: buf.byteLength });
    } catch (e) {
      return null;
    }
    if (n <= 0) {
      return null;
    }
    this.offset += n;
    this.frameId++;
    return {
      frameId: this.frameId,
      keyframe: this.frameId === 1,
      width: this.resolution.width,
      height: this.resolution.height,
      data: new Uint8Array(buf, 0, n)
    };
  }

  currentResolution(): ResolutionInfo {
    return this.resolution;
  }

  isRealtime(): boolean {
    return false;
  }
}

// GATE-02 实时源：C++ NAPI 里做 OH_AVScreenCapture(WithSurface) → OH_VideoEncoder(AVC)。
// 出帧有两条路：直发（默认，C++ 内分片 sendto，JS 线程零负担）与 TSFN 回传（回落）。
// 失败时把 native 返回码原样抛出（1~12 对应 harmoscap.cpp 的 ERR_*），不吞错误。
export class NativeVideoSource implements IVideoSource {
  /**
   * 每帧像素预算（宽×高）。**可用 `--ps budget <像素数>` 现场覆盖**，不必重编译。
   *
   * 实测依据：手机 1828×2584 = 472 万像素时只有 31fps，
   * 反推编码吞吐上限约 1.46 亿像素/秒 → 要跑 60fps，每帧须 ≤ 约 243 万像素。
   * 默认 200 万（手机 → 1184×1664），留一点余量，兼顾画质与帧率。
   */
  static readonly PIXEL_BUDGET_DEFAULT: number = 2000000;

  static pixelBudget(): number {
    const v: number | undefined = AppStorage.get<number>('capPixelBudget');
    if (v !== undefined && v > 0) {
      return v;
    }
    return NativeVideoSource.PIXEL_BUDGET_DEFAULT;
  }

  private resolution: ResolutionInfo;
  /** 构造时的原始物理分辨率，缩放计算以它为基准（避免重复缩放）。 */
  private origin: ResolutionInfo;
  /** 算好的采集尺寸缓存：尺寸只算一次，start() 与上报共用这一份。 */
  private captureSize: ResolutionInfo | null = null;
  private fps: number;
  private bitrateKbps: number;
  private running: boolean = false;
  private frameId: number = 0;
  private sink: ((frame: VideoFrame) => void) | null = null;
  private pending: VideoFrame | null = null;
  private lastCapStats: CapStats | null = null;
  private statsErrShown: boolean = false;
  private direct: boolean = false;

  constructor(resolution: ResolutionInfo, fps: number, bitrateKbps: number) {
    this.resolution = resolution;
    this.origin = resolution;
    this.fps = fps;
    this.bitrateKbps = bitrateKbps;
  }

  static readonly ERR_TEXT: string[] = [
    'OK', '编码器创建失败', '编码器回调注册失败', '编码器配置失败', '编码器 Prepare 失败',
    '编码器 Start 失败', '获取编码器 Surface 失败', '录屏实例创建失败', '录屏 Init 失败',
    '录屏 Start 失败', '已在运行', '未运行', 'TSFN 创建失败'
  ];

  // 开启 native 直发：分片与 sendto 全部在 C++，ArkTS 只保留控制通道。
  // 必须在 start() 之前调用：本开关是 native 全局标志，先打开可保证从首帧起就不走 TSFN。
  // 开启后 setFrameSink 不再被触发，帧/字节统计改由 getStats() 轮询。
  enableDirectSend(host: string, port: number): number {
    try {
      const r: number = setStreamPeer(host, port);
      if (r !== 0) {
        return DIRECT_SEND_NO_PEER;
      }
      if (setDirectSend(true) !== 0) {
        return DIRECT_SEND_UNSUPPORTED;
      }
      this.direct = true;
      return DIRECT_SEND_OK;
    } catch (e) {
      return DIRECT_SEND_UNSUPPORTED;
    }
  }

  disableDirectSend(): void {
    try {
      setDirectSend(false);
    } catch (e) {
      // 模块未就绪时忽略；stop() 里会再次关闭
    }
    this.direct = false;
  }

  isDirectSend(): boolean {
    return this.direct;
  }

  /** 采集是否还在跑（暂停发送时仍为 true）。 */
  isCapturing(): boolean {
    return this.running;
  }

  /** 暂停发送但保留采集会话：再开始时不会重新弹录屏授权。 */
  pauseSend(): void {
    try {
      setStreamActive(false);
    } catch (e) {
      // 模块未就绪时忽略
    }
  }

  /** 恢复发送；native 侧会主动请求一个 IDR，PC 立即能出画面。 */
  resumeSend(): void {
    try {
      setStreamActive(true);
    } catch (e) {
      // 模块未就绪时忽略
    }
  }

  setFrameSink(cb: (frame: VideoFrame) => void): void {
    this.sink = cb;
  }

  async start(): Promise<void> {
    // 采集尺寸：向下对齐到 32 的倍数，并按像素预算缩放（2026-11-14 手机 31fps 修复）
    //
    // 原来的写法有两个问题，手机上叠加成了「只有 31fps」：
    //   1) `Math.floor((w+15)/16)*16` 实际是**向上**取整 —— 1828→1840、2584→2592，
    //      比屏幕还大，编码器要额外处理超出屏幕的部分；平板 1600x2560 天然对齐，所以没暴露。
    //   2) 手机按原始 1828×2584 = 472 万像素编码，目标是 60fps → 每秒 2.8 亿像素，
    //      远超这颗编码器（实测上限约 1.46 亿），只能掉到 31fps。
    //      平板走参考采集时 -scale 2 实际只编 800×1280（102 万像素），所以能跑 57fps。
    //
    // 尺寸在 prepareCaptureSize() 里算好并缓存，这样会话启动阶段（还没收到 CMD_STREAM）
    // 就能把真实尺寸报给 PC，不会出现「先报原始分辨率、后改小」的错配。
    const size: ResolutionInfo = this.prepareCaptureSize();
    const w: number = size.width;
    const h: number = size.height;
    if (w <= 0 || h <= 0) {
      throw new Error('分辨率无效，无法启动实时采集');
    }
    const ret: number = startCapture(w, h, this.fps, this.bitrateKbps,
      (data: Uint8Array, keyframe: boolean, ptsUs: number): void => {
        this.onFrame(data, keyframe, ptsUs, w, h);
      });
    if (ret !== 0) {
      const text: string = ret < NativeVideoSource.ERR_TEXT.length
        ? NativeVideoSource.ERR_TEXT[ret] : `未知错误 ${ret}`;
      throw new Error(`实时采集启动失败(${ret})：${text}`);
    }
    this.running = true;
    this.frameId = 0;
  }

  /**
   * 算出真正要传给编码器的宽高，并写回 this.resolution（供 currentResolution() 上报）。
   * 幂等：多次调用结果相同，不会因为重复计算而越缩越小。
   *
   * 必须与 start() 使用同一份结果，否则 PC 收到的尺寸和实际码流对不上，画面会被拉伸。
   */
  prepareCaptureSize(): ResolutionInfo {
    if (this.captureSize !== null) {
      return this.captureSize;
    }
    const src: ResolutionInfo = this.origin;
    const budget: number = NativeVideoSource.pixelBudget();
    const capped: ResolutionInfo = fitToPixelBudget(src, budget);
    // 向下对齐到 32：只会裁掉边缘几个像素，不引入黑边或拉伸。
    const w: number = alignDown(capped.width, 32);
    const h: number = alignDown(capped.height, 32);
    const size: ResolutionInfo = { width: w, height: h, folded: src.folded };
    this.captureSize = size;
    this.resolution = size;
    return size;
  }

  /** 原始物理分辨率（未缩放），供排查与重算用。 */
  originResolution(): ResolutionInfo {
    return this.origin;
  }

  private onFrame(data: Uint8Array, keyframe: boolean, ptsUs: number, w: number, h: number): void {
    if (!this.running || data.length === 0) {
      return;
    }
    this.frameId++;
    const frame: VideoFrame = {
      frameId: this.frameId,
      keyframe: keyframe,
      width: w,
      height: h,
      data: data
    };
    if (this.sink !== null) {
      this.sink(frame);
    } else {
      // 没有消费者时只保留最后一帧，避免无界队列把内存吃爆
      this.pending = frame;
    }
    if (ptsUs < 0) {
      this.pending = null;
    }
  }

  async stop(): Promise<void> {
    this.running = false;
    this.pending = null;
    this.disableDirectSend();
    try {
      stopCapture();
    } catch (e) {
      // 停止失败不阻塞会话退出
    }
  }

  async nextFrame(): Promise<VideoFrame | null> {
    const f: VideoFrame | null = this.pending;
    this.pending = null;
    return f;
  }

  currentResolution(): ResolutionInfo {
    return this.resolution;
  }

  isRealtime(): boolean {
    return true;
  }

  stats(): CapStats {
    try {
      this.lastCapStats = getStats();
    } catch (e) {
      // 原生模块未就绪时返回空统计；异常只打一次，避免每秒刷屏
      if (!this.statsErrShown) {
        this.statsErrShown = true;
        const err = e as Error;
        console.error(`[cap] getStats 失败：${err !== null && err !== undefined && err.message ? err.message : String(e)}`);
      }
    }
    if (this.lastCapStats === null) {
      return {
        frames: 0, bytes: 0, keyframes: 0, width: 0, height: 0, state: -1,
        packets: 0, sendErrors: 0, directSend: 0, idrReplays: 0, idrCachedKb: 0
      };
    }
    return this.lastCapStats;
  }
}
