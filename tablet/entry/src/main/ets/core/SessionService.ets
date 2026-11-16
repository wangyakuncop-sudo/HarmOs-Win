// 设备端会话编排（NEXT-02~06 统一入口）。
// 只做编排与派发，不含 UI；传输细节在 UdpTransport，采集细节在 capture/VideoSource，
// 低频输入落地在 accessibility/LowFreqBridge（NEXT-07 / INV-13）。

import { BusinessError, pasteboard } from '@kit.BasicServicesKit';
import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { UdpTransport } from '../transport/UdpTransport';
import {
  CMD_INPUT, CMD_CLIP_TEXT, CMD_CLIP_IMAGE, CMD_NAV, CMD_ROLE,
  CMD_RESOLUTION, CMD_STREAM, CMD_LOCK_STATE, CMD_INJECT_RESULT,
  CMD_IDLE, CMD_LIGHT, CMD_FULL, CMD_STOP
} from '../transport/HwpPacket';
import {
  WireCodec, InputEvent, ClipText, ResolutionInfo, DeviceProfile, InjectReport,
  ROLE_SOURCE, utf8Encode, fnv1a
} from './SessionTypes';
import { InputInjector } from './InputInjector';
import { LowFreqBridge, STAGE_CLICK } from '../accessibility/LowFreqBridge';
import { KeepAlive } from '../common/KeepAlive';
import { IVideoSource, FileVideoSource, NativeVideoSource, DisplayProfile, VideoFrame,
  DIRECT_SEND_OK, DIRECT_SEND_UNSUPPORTED } from '../capture/VideoSource';
import { CapStats } from 'libharmoscap.so';

const STREAM_INTERVAL_MS: number = 33;
// GATE-02 实时源参数。采集侧出帧率由 native 的 OH_AVScreenCapture_SetMaxVideoFrameRate 强制封顶，
// OH_MD_KEY_FRAME_RATE 只影响编码器元数据；画面静止时采集仍会降到 1~5fps（正常行为）。
// 60fps + CBR 20Mbps：滑动跟手性的关键在帧率，码率用 CBR 压住突发（VBR 实测冲到 23Mbps 会丢包）。
const STREAM_FPS: number = 60;
const STREAM_BITRATE_KBPS: number = 20000;
// 0xFE：GATE-05 原生 inputEventClient 探针回归专用，不属于 INV-13 的交付能力。
// 正常 CMD_INPUT 一律走无障碍低频点击。
const EV_PROBE_NATIVE: number = 0xFE;

// UI 用的结构化采集指标：避免页面去解析 getCaptureStats() 的拼接字符串
export interface CaptureMetrics {
  present: boolean;   // 当前源是否为 native 实时源
  frames: number;
  bytes: number;
  keyframes: number;
  state: number;
  packets: number;
  sendErrors: number;
  directSend: number;
  idrReplays: number;
  idrCachedKb: number;
}

export interface SessionStats {
  streamFrames: number;
  streamBytes: number;
  inputEvents: number;
  navEvents: number;
  clipToDevice: number;
  clipFromDevice: number;
  imageParts: number;
  role: number;
  locked: boolean;
  streaming: boolean;
  resolution: ResolutionInfo;
  injectOk: number;
  injectErr: number;
  injectUs: number;
  injectSyscap: number;
}

export class SessionService {
  // 进程内单例。采集会话一旦起过就必须跨页面存活：
  // OH_AVScreenCapture 的隐私弹窗只在「重新 Start」时出现，页面销毁就 stop() 会让
  // 每次返回主页再进来都重弹一次「允许 HarmLink 使用你的屏幕」。单例把重弹压缩到
  // 「进程被杀之后的下一次启动」，这是三方应用在平板上能做到的最小值。
  private static singleton: SessionService | null = null;

  static shared(): SessionService {
    if (SessionService.singleton === null) {
      SessionService.singleton = new SessionService();
    }
    return SessionService.singleton;
  }

  private transport: UdpTransport;
  private displayProfile: DisplayProfile;
  private injector: InputInjector = new InputInjector();
  private bridge: LowFreqBridge = new LowFreqBridge();
  private source: IVideoSource | null = null;
  private timerStream: number = -1;
  private timerReport: number = -1;
  // native 直发模式下没有逐帧回调，帧/字节统计改由轮询 native getStats 增量补齐
  private timerStats: number = -1;
  // 分辨率重报：设备只在会话启动时发一次 CMD_RESOLUTION，PC 中途接入（重开窗口/断线重连）
  // 就永远拿不到，解码器会每帧抛「尚未获得分辨率」。推流期间每 2s 补发一次，代价可忽略。
  private timerResolution: number = -1;
  private lastNativeFrames: number = 0;
  private lastNativeBytes: number = 0;
  private streaming: boolean = false;
  private lastClipHash: number = 0;
  // 剪贴板自动上行实验（2026-11-11，用户要求「平板复制，电脑直接粘贴」）。
  // 事实依据（本机 SDK 原文，非推测）：
  //   · on('update') 无 @permission（@ohos.pasteboard.d.ts:1107）→ 任何应用都能收到「变了」通知；
  //   · getDataSync() 标 @permission ohos.permission.READ_PASTEBOARD（:1283），
  //     该权限 availableLevel = system_basic（PermissionDefinitions.json）→ 调试签名拿不到。
  // 所以这条回调只有两种结局：读到内容（可全自动同步）或抛 201 权限拒绝（此路不通）。
  // 两种都写 hilog（tag CLIP）留证据，不靠猜。
  private clipWatchOn: boolean = false;
  private clipProbeCount: number = 0;
  // 长时任务只停自己启的那次（KeepAlive 是静态单例，探针页也用）
  private keepAliveStarted: boolean = false;
  // 会话级「已建链」标记：true 时再调 start() 只恢复推流，不重建 transport / 采集源，
  // 避免重复 OH_AVScreenCapture 起采（那一步必然弹授权框）
  private started: boolean = false;
  private imageParts: Map<number, Uint8Array> = new Map<number, Uint8Array>();
  private imageTotal: number = 0;
  private stats: SessionStats;
  private onLog: ((msg: string) => void) | null = null;

  constructor() {
    this.transport = new UdpTransport();
    this.displayProfile = new DisplayProfile();
    const r: ResolutionInfo = this.displayProfile.current();
    this.stats = {
      streamFrames: 0,
      streamBytes: 0,
      inputEvents: 0,
      navEvents: 0,
      clipToDevice: 0,
      clipFromDevice: 0,
      imageParts: 0,
      role: ROLE_SOURCE,
      locked: false,
      streaming: false,
      resolution: r,
      injectOk: 0,
      injectErr: 0,
      injectUs: 0,
      injectSyscap: 0
    };
  }

  setOnLog(cb: (msg: string) => void): void {
    this.onLog = cb;
    this.transport.setOnLog(cb);
  }

  getStats(): SessionStats {
    return {
      streamFrames: this.stats.streamFrames,
      streamBytes: this.stats.streamBytes,
      inputEvents: this.stats.inputEvents,
      navEvents: this.stats.navEvents,
      clipToDevice: this.stats.clipToDevice,
      clipFromDevice: this.stats.clipFromDevice,
      imageParts: this.stats.imageParts,
      role: this.stats.role,
      locked: this.stats.locked,
      streaming: this.stats.streaming,
      resolution: this.stats.resolution,
      injectOk: this.stats.injectOk,
      injectErr: this.stats.injectErr,
      injectUs: this.stats.injectUs,
      injectSyscap: this.stats.injectSyscap
    };
  }

  getTransport(): UdpTransport {
    return this.transport;
  }

  async start(videoPath: string, ctx: common.UIAbilityContext | null = null): Promise<void> {
    // 幂等：会话还活着就只补报信息、等 PC 的 CMD_STREAM。
    // 重建 NativeVideoSource 会重新 StartScreenCapture，必然再弹一次录屏授权。
    if (this.started) {
      this.transport.sendControl(CMD_ROLE, WireCodec.encodeRole({
        deviceId: 1,
        role: this.stats.role,
        width: this.stats.resolution.width,
        height: this.stats.resolution.height,
        folded: this.stats.resolution.folded
      }), true);
      this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(this.stats.resolution), true);
      this.log('会话常驻中：已重报 ROLE + RESOLUTION，等 PC 发 CMD_STREAM（不重建采集，免录屏授权）');
      return;
    }
    hilog.info(0x0000, 'HarmLink', 'session.start: begin');
    this.transport.setControlHandler((cmd: number, body: Uint8Array): boolean => {
      return this.onControl(cmd, body);
    });
    await this.transport.start();
    hilog.info(0x0000, 'HarmLink', 'session.start: transport started');
    // ⛔ 2026-11-14 用户明确要求：**不再后台自动同步剪贴板**，这里不再挂监听。
    // 原来 sessions 一起来就 `startClipWatch()`，效果是监听系统剪贴板变化并反向推给 PC。
    // 与 PC 侧 `EnableClipboard()`（300ms 轮询 PC 剪贴板 → 写设备剪贴板）合起来是一条
    // 双向静默镜像链路，两边都要停。
    // 现在设备 → PC 只由两条**显式触发**的路径完成，都不经过本监听：
    //   · 剪贴板页的 PasteButton（一次性读权限，readClipToPc）
    //   · 会话页的 PasteButton（同上）
    // `startClipWatch` / `stopClipWatch` 实现保留，将来要恢复把下面这行取消注释即可。
    // this.startClipWatch();
    // 长时任务：息屏/切后台后系统会挂起普通进程，采集与 UDP 一起断。
    // 必须在正式会话里启（此前只有 pages/Index 的 GATE-01 探针页启过，投屏链路没有）。
    if (ctx !== null) {
      try {
        hilog.info(0x0000, 'HarmLink', 'session.start: KeepAlive.begin');
        const kept: boolean = await KeepAlive.start(ctx);
        hilog.info(0x0000, 'HarmLink', 'session.start: KeepAlive.done=%{public}s', `${kept}`);
        this.keepAliveStarted = kept;
        this.log(kept
          ? '长时任务已启动（multiDeviceConnection + audioRecording）'
          : '[WARN] 长时任务启动失败：息屏/后台可能被系统挂起');
      } catch (e) {
        this.log(`[WARN] 长时任务异常: ${String(e)}`);
      }
    }
    try {
      hilog.info(0x0000, 'HarmLink', 'session.start: bridge.begin');
      await this.bridge.start();
      hilog.info(0x0000, 'HarmLink', 'session.start: bridge.done');
    } catch (e) {
      this.log(`[WARN] 本地低频桥启动失败: ${String(e)}`);
    }
    this.bridge.setOnResult((body: Uint8Array): void => {
      this.onLowFreqResult(body);
    });
    // videoPath 为空 → GATE-02 实时源（C++ NAPI 真采真编）；否则读文件验证链路
    this.source = videoPath.length === 0
      ? new NativeVideoSource(this.displayProfile.current(), STREAM_FPS, STREAM_BITRATE_KBPS)
      : new FileVideoSource(videoPath, this.displayProfile.current());
    // 采集实际用的尺寸可能被缩放/对齐过（见 NativeVideoSource.prepareCaptureSize 的像素预算）。
    // **必须在这里就算好**：真机实测发现，如果等 CMD_STREAM 之后 source.start() 里才算，
    // 此时 ROLE/RESOLUTION 早已按物理分辨率发出去了 → PC 按 1828×2584 建画布，
    // 而实际码流是 1184×1664，画面会被拉伸。
    if (this.source instanceof NativeVideoSource) {
      const actual: ResolutionInfo = (this.source as NativeVideoSource).prepareCaptureSize();
      if (actual.width > 0 && actual.height > 0) {
        this.stats.resolution = actual;
      }
    }
    hilog.info(0x0000, 'HarmLink', 'session.start: source ready, res=%{public}dx%{public}d',
      this.stats.resolution.width, this.stats.resolution.height);
    this.displayProfile.onChanged((r: ResolutionInfo): void => {
      this.onResolutionChanged(r);
    });
    const profile: DeviceProfile = {
      deviceId: 1,
      role: this.stats.role,
      width: this.stats.resolution.width,
      height: this.stats.resolution.height,
      folded: this.stats.resolution.folded
    };
    this.transport.sendControl(CMD_ROLE, WireCodec.encodeRole(profile), true);
    this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(this.stats.resolution), true);
    this.timerReport = setInterval((): void => {
      this.log(`会话 t 帧=${this.stats.streamFrames} 字节=${this.stats.streamBytes} 输入=${this.stats.inputEvents} 剪贴板=${this.stats.clipToDevice}/${this.stats.clipFromDevice}`);
    }, 5000);
    this.started = true;
    this.log('会话已启动：ROLE + RESOLUTION 已上报；低频输入桥 127.0.0.1:9021/9022 就绪，等待 PC 握手');
  }

  // 会话是否仍常驻（采集授权仍有效）：页面据此恢复「继续投屏」而不是「启动」
  isAlive(): boolean {
    return this.started;
  }

  /**
   * 是否真的在往外推流（区别于 isAlive：会话可能存活但已暂停发送，mode=IDLE）。
   * 页面自动起采时用它判断「要不要再调 start() 把它推起来」。
   */
  isStreaming(): boolean {
    return this.started && this.streaming;
  }

  async stop(): Promise<void> {
    this.stopStream();
    this.stopClipWatch();
    if (this.timerReport >= 0) {
      clearInterval(this.timerReport);
      this.timerReport = -1;
    }
    if (this.keepAliveStarted) {
      await KeepAlive.stop();
      this.keepAliveStarted = false;
    }
    await this.bridge.stop();
    if (this.source !== null) {
      await this.source.stop();
      this.source = null;
    }
    await this.transport.stop();
    // 全量释放后采集授权作废：下次 start() 会重新申请并弹一次系统授权框
    this.started = false;
    this.streaming = false;
    this.log('会话已停止（采集已释放，下次启动会重新弹录屏授权）');
  }

  // 用户主动「停止投屏」：只停码流，不拆会话。
  // 保留采集会话（NativeVideoSource.pauseSend）、长时任务、transport 与输入桥，
  // 下次继续时走 startStream 的「采集还在跑」分支，不重新申请录屏授权（省掉人工点允许 + 约 9.9s 首帧等待）。
  // 真正的全量释放在 stop()，只在页面销毁 / 应用退出时调用。
  pauseStream(): void {
    if (this.source === null) {
      return;
    }
    this.stopStream();
    this.log('投屏已暂停（会话与采集保留，点「继续投屏」立即恢复，无需重新授权）');
  }

  // 与 pauseStream 配对：复用现有会话恢复发送。
  async resumeStream(): Promise<void> {
    if (this.source === null) {
      this.log('[WARN] 会话已释放，无法继续，请重新启动');
      return;
    }
    await this.startStream();
  }

  isPausedState(): boolean {
    return this.source !== null && !this.streaming;
  }

  // 设备 → PC 剪贴板。
  //
  // 权限背景（2026-11-09 复核 PermissionDefinitions.json）：`ohos.permission.READ_PASTEBOARD` 是
  // `user_grant` + `system_basic`，**deviceTypes 含 tablet**（不是设备形态白名单问题），
  // 但普通签名拿不到 system_basic，2026-11-08 装机实测 code 9568289。
  //
  // 因此上行改走官方免权限通道：**PasteButton 安全控件**（API 10+）。用户点击该控件时，
  // 系统临时授予 SECURE_PASTE 权限（仅 onClick 回调内有效，回调结束即回收），
  // 可直接 getData() 读取系统剪贴板 —— 不弹窗、不申请权限。
  //
  // 所以设备端不再是"自动同步"，而是**用户主动点一下**（点即授权）。这是平台强制的交互形态，
  // 不是实现偷懒：没有权限就读不了剪贴板，安全控件是唯一合规入口。
  //
  // 传入的 text 由页面在 PasteButton 的 onClick 回调内读出后传入，本方法只负责编码与发送。
  // 返回 true 表示确实发了上行报文（页面据此打日志，避免"读到但被防回环拦下"时误报成功）。
  pushClipText(text: string): boolean {
    if (text.length === 0) {
      this.log('剪贴板为空，未上行');
      return false;
    }
    const hash: number = fnv1a(utf8Encode(text));
    if (hash === this.lastClipHash) {
      // 内容与最近一次同步过的完全一致：说明这段文本本来就是 PC 同步下来的，
      // 推回去只会形成回环。这是防回环生效，不是失败。
      this.log('剪贴板内容未变化（与已同步内容一致），未上行');
      return false;
    }
    this.lastClipHash = hash;
    this.transport.sendControl(CMD_CLIP_TEXT, WireCodec.encodeClipText(hash, text), true);
    this.stats.clipFromDevice++;
    this.log(`剪贴板文本 → PC (${text.length} 字符, hash=${hash})`);
    return true;
  }

  /**
   * 注册系统剪贴板变化监听（自动上行实验）。
   *
   * 只在会话里注册一次。回调里**同步**读取，不得 setTimeout / await 推到别的任务 ——
   * 任何临时授权都只在回调生命周期内有效（页面 PasteButton 那条路径同理）。
   */
  private startClipWatch(): void {
    if (this.clipWatchOn) {
      return;
    }
    try {
      const pb: pasteboard.SystemPasteboard = pasteboard.getSystemPasteboard();
      pb.on('update', (): void => {
        this.onClipboardUpdate();
      });
      this.clipWatchOn = true;
      console.info('[CLIP] on(update) 注册成功');
      this.log('[剪贴板] 已监听系统剪贴板变化（自动上行）');
    } catch (e) {
      const err = e as BusinessError;
      console.error(`[CLIP] on(update) 注册失败 code=${err.code} msg=${err.message}`);
      this.log(`[剪贴板][WARN] 监听注册失败 ${err.code} ${err.message}`);
    }
  }

  private stopClipWatch(): void {
    if (!this.clipWatchOn) {
      return;
    }
    try {
      pasteboard.getSystemPasteboard().off('update');
    } catch (e) {
      const err = e as BusinessError;
      console.error(`[CLIP] off(update) 失败 code=${err.code}`);
    }
    this.clipWatchOn = false;
  }

  /**
   * 剪贴板变化回调：能读到就上行，读不到就把确切错误码记下来。
   * 判定标准很硬 —— code=201 即为权限拒绝，说明后台读剪贴板这条路对普通应用是死的。
   */
  private onClipboardUpdate(): void {
    this.clipProbeCount++;
    const seq: number = this.clipProbeCount;
    console.info(`[CLIP] update #${seq} 进入回调`);
    try {
      const data: pasteboard.PasteData = pasteboard.getSystemPasteboard().getDataSync();
      const text: string = data.getPrimaryText();
      let mime: string = '';
      try {
        mime = data.getPrimaryMimeType();
      } catch (e) {
        mime = '?';
      }
      console.info(`[CLIP] update #${seq} 读取成功 mime=${mime} len=${text.length}`);
      this.log(`[剪贴板] update #${seq} 读到 ${text.length} 字符（${mime}）`);
      if (text.length > 0) {
        this.pushClipText(text);
      }
    } catch (e) {
      const err = e as BusinessError;
      console.error(`[CLIP] update #${seq} 读取失败 code=${err.code} msg=${err.message}`);
      this.log(`[剪贴板][ERR] update #${seq} 读取失败 ${err.code} ${err.message}`);
    }
  }

  private onControl(cmd: number, body: Uint8Array): boolean {
    if (cmd === CMD_IDLE || cmd === CMD_LIGHT || cmd === CMD_FULL || cmd === CMD_STOP) {
      return false; // 交回 L0 回归路径
    }
    switch (cmd) {
      case CMD_STREAM:
        if (body.length > 0 && body[0] === 1) {
          this.startStream();
        } else {
          this.stopStream();
        }
        return true;
      case CMD_INPUT: {
        const ev: InputEvent = WireCodec.decodeInput(body);
        this.stats.inputEvents++;
        if (ev.type === EV_PROBE_NATIVE) {
          // GATE-05 回归：原生 inputEventClient 探针（平板实测 801，保留为可复测证据）
          this.onInput(ev).catch((): void => {});
        } else {
          // INV-13：正常输入一律走无障碍低频点击
          this.bridge.send(STAGE_CLICK, ev.x, ev.y);
        }
        return true;
      }
      case CMD_NAV: {
        const key: number = body.length > 0 ? body[0] : 0;
        this.stats.navEvents++;
        // NavKey(1 back / 2 home / 3 recents) 与 LowFreqBridge 的 STAGE_* 逐值对齐
        this.bridge.send(key, 0, 0);
        return true;
      }
      case CMD_CLIP_TEXT: {
        const clip: ClipText = WireCodec.decodeClipText(body);
        this.lastClipHash = clip.hash; // 抑制回环：刚写入的内容不再回传
        this.writeClipText(clip.text);
        this.stats.clipToDevice++;
        this.log(`剪贴板文本 ← PC (${clip.text.length} 字符, hash=${clip.hash})`);
        return true;
      }
      case CMD_CLIP_IMAGE:
        this.onClipImage(body);
        return true;
      case CMD_ROLE:
        if (body.length > 0) {
          this.stats.role = body[0];
          this.log(`角色协商 → ${this.stats.role}`);
        }
        return true;
      case CMD_RESOLUTION:
        this.stats.resolution = WireCodec.decodeResolution(body);
        return true;
      case CMD_LOCK_STATE:
        this.stats.locked = body.length > 0 && body[0] !== 0;
        this.log(`锁屏状态 → ${this.stats.locked}`);
        return true;
      default:
        return false;
    }
  }

  // NEXT-07 / GATE-06：低频桥返回 19B InjectReport，原样回传 PC 用于量化端到端延迟。
  // errCode=-2 表示 250ms 内无响应 —— 通常是没在系统设置里开启无障碍服务。
  private onLowFreqResult(body: Uint8Array): void {
    const r: InjectReport = WireCodec.decodeInjectReport(body);
    this.stats.injectOk = r.success;
    this.stats.injectErr = r.errCode;
    this.stats.injectUs = r.elapsedUs;
    this.stats.injectSyscap = r.syscap;
    this.log(`低频操作 ${r.ok === 1 ? 'OK' : 'FAIL'} code=${r.errCode} stage=${r.stage} ` +
      `${(r.elapsedUs / 1000).toFixed(2)}ms 服务=${r.syscap}`);
    this.transport.sendControl(CMD_INJECT_RESULT, body, false);
  }

  // GATE-05 回归专用：设备端原生注入探针（平板 20/20 返回 801）。
  // 不属于 INV-13 交付能力，只在 CMD_INPUT.type=0xFE 时触发。
  private async onInput(ev: InputEvent): Promise<void> {
    const r: InjectReport = await this.injector.inject(ev.x, ev.y);
    this.stats.injectOk = r.success;
    this.stats.injectErr = r.errCode;
    this.stats.injectUs = r.elapsedUs;
    this.stats.injectSyscap = r.syscap;
    this.log(`GATE-05 回归 ${r.ok === 1 ? 'OK' : 'FAIL'} code=${r.errCode} stage=${r.stage} ` +
      `${(r.elapsedUs / 1000).toFixed(2)}ms syscap=${r.syscap}`);
    this.transport.sendControl(CMD_INJECT_RESULT, WireCodec.encodeInjectReport(r), false);
  }

  private async startStream(): Promise<void> {
    if (this.source === null) {
      return;
    }
    // 已在推流却收到 CMD_STREAM=1：多半是 PC 换了新进程重新接入（设备侧编码器从未停）。
    // 此时绝不能直接早退 —— 新解码器没有 SPS/PPS，只会收到 P 帧，错误计数一直涨但永远解不出画面。
    // 正确做法：补发分辨率 + 重放「参数集 + IDR」（setStreamActive 幂等，可重复调用）。
    if (this.streaming) {
      this.startResolutionBeacon();
      this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(this.stats.resolution), false);
      if (this.source instanceof NativeVideoSource) {
        const natCur: NativeVideoSource = this.source as NativeVideoSource;
        // peer 可能也变了（PC 换了监听端口），先重置再重放
        natCur.enableDirectSend(this.transport.getPeerHost(), this.transport.getPeerPort());
        natCur.resumeSend();
      }
      this.log('PC 重新接入：已补发分辨率 + 重放缓存关键帧（编码器未重启）');
      return;
    }
    this.startResolutionBeacon();
    // 采集还在跑（只是暂停了发送）：直接恢复，不重新申请录屏权限。
    // 重新申请会弹「允许 HarmLink 使用你的屏幕」，人工点一次约 9.9s，是首帧延迟的唯一大头。
    if (this.source instanceof NativeVideoSource && (this.source as NativeVideoSource).isCapturing()) {
      const nat: NativeVideoSource = this.source as NativeVideoSource;
      const hostR: string = this.transport.getPeerHost();
      const portR: number = this.transport.getPeerPort();
      const dr: number = nat.enableDirectSend(hostR, portR);
      this.resetNativeBaseline();
      // 恢复前补发一次分辨率：PC 端可能是刚重连的，等 2s beacon 周期会白白多等一截
      this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(this.stats.resolution), false);
      nat.resumeSend();
      this.streaming = true;
      this.stats.streaming = true;
      if (dr === DIRECT_SEND_OK) {
        this.timerStats = setInterval((): void => {
          this.syncNativeStats();
        }, 1000);
      } else {
        nat.setFrameSink((frame: VideoFrame): void => {
          this.pushFrame(frame);
        });
      }
      const rr: ResolutionInfo = nat.currentResolution();
      this.log(`实时码流恢复（采集未中断，无需重新授权）：${rr.width}x${rr.height} → ${hostR}:${portR}`);
      return;
    }
    // 直发必须在 start() 之前打开：否则首帧起就走 TSFN，编码器一口气推几十帧会把队列打满
    let directRet: number = DIRECT_SEND_UNSUPPORTED;
    let directHost: string = '';
    let directPort: number = 0;
    if (this.source instanceof NativeVideoSource) {
      const src: NativeVideoSource = this.source as NativeVideoSource;
      directHost = this.transport.getPeerHost();
      directPort = this.transport.getPeerPort();
      directRet = src.enableDirectSend(directHost, directPort);
    }
    try {
      await this.source.start();
    } catch (e) {
      this.log(`[ERR] 码流源启动失败: ${String(e)}`);
      if (this.source instanceof NativeVideoSource) {
        (this.source as NativeVideoSource).disableDirectSend();
      }
      return;
    }
    this.streaming = true;
    this.stats.streaming = true;
    // 实时源优先走 native 直发：分片与 sendto 在编码器回调线程完成，JS 线程只同步统计。
    // 逐片在 ArkTS 发送实测只有 6.4fps（30fps 源被单线程分片拖垮），直发是帧率达标的前提。
    if (this.source instanceof NativeVideoSource) {
      const r: ResolutionInfo = this.source.currentResolution();
      if (directRet === DIRECT_SEND_OK) {
        this.syncNativeStats();
        this.timerStats = setInterval((): void => {
          this.syncNativeStats();
        }, 1000);
        this.log(`实时码流开始（native 直发）：${r.width}x${r.height} / ${STREAM_FPS}fps / ` +
          `${STREAM_BITRATE_KBPS}kbps → ${directHost}:${directPort}`);
        return;
      }
      this.log(`[WARN] native 直发不可用(${directRet})，回落到 ArkTS 分片发送`);
    }
    // 实时源：编码器出帧即推，不排队不轮询；文件源仍走定时拉取
    if (this.source.isRealtime() && this.source.setFrameSink !== undefined) {
      this.source.setFrameSink((frame: VideoFrame): void => {
        this.pushFrame(frame);
      });
      const r: ResolutionInfo = this.source.currentResolution();
      this.log(`实时码流开始：${r.width}x${r.height} / ${STREAM_FPS}fps / ${STREAM_BITRATE_KBPS}kbps`);
      return;
    }
    this.timerStream = setInterval((): void => {
      this.streamTick();
    }, STREAM_INTERVAL_MS);
    this.log(`码流开始（realtime=${this.source.isRealtime()}，间隔 ${STREAM_INTERVAL_MS}ms）`);
  }

  private pushFrame(frame: VideoFrame): void {
    if (!this.streaming || frame.data.length === 0) {
      return;
    }
    this.transport.sendStreamFrame(frame.frameId, frame.keyframe, frame.data);
    this.stats.streamFrames++;
    this.stats.streamBytes += frame.data.length;
  }

  // 推流期间周期性重报分辨率：PC 中途接入也能拿到（只靠 SPS 兜底不可行，
  // 编码器未必在每个 IDR 前重复 SPS/PPS）。
  private startResolutionBeacon(): void {
    if (this.timerResolution >= 0) {
      return;
    }
    this.sendResolution();
    this.timerResolution = setInterval((): void => {
      this.sendResolution();
    }, 2000);
  }

  private sendResolution(): void {
    this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(this.stats.resolution), true);
  }

  // 暂停/恢复时把统计基线对齐到当前 native 计数，避免恢复瞬间把历史帧一次性计入
  private resetNativeBaseline(): void {
    if (this.source instanceof NativeVideoSource) {
      const s: CapStats = (this.source as NativeVideoSource).stats();
      this.lastNativeFrames = s.frames;
      this.lastNativeBytes = s.bytes;
    }
  }

  // 直发模式：JS 侧不再逐帧推送，用 native 统计的增量维持 streamFrames/streamBytes
  private syncNativeStats(): void {
    if (!(this.source instanceof NativeVideoSource)) {
      return;
    }
    const s: CapStats = (this.source as NativeVideoSource).stats();
    const df: number = s.frames - this.lastNativeFrames;
    if (df > 0) {
      this.stats.streamFrames += df;
      this.lastNativeFrames = s.frames;
    }
    const db: number = s.bytes - this.lastNativeBytes;
    if (db > 0) {
      this.stats.streamBytes += db;
      this.lastNativeBytes = s.bytes;
    }
    if (s.sendErrors > 0) {
      this.log(`[WARN] native 直发失败 ${s.sendErrors} 次 / 已发 ${s.packets} 包`);
    }
  }

  // 结构化采集指标：UI 指标卡直接用，不再解析拼接字符串
  captureMetrics(): CaptureMetrics {
    const empty: CaptureMetrics = {
      present: false, frames: 0, bytes: 0, keyframes: 0, state: -1,
      packets: 0, sendErrors: 0, directSend: 0, idrReplays: 0, idrCachedKb: 0
    };
    if (!(this.source instanceof NativeVideoSource)) {
      return empty;
    }
    const s: CapStats = (this.source as NativeVideoSource).stats();
    return {
      present: true,
      frames: s.frames,
      bytes: s.bytes,
      keyframes: s.keyframes,
      state: s.state,
      packets: s.packets,
      sendErrors: s.sendErrors,
      directSend: s.directSend,
      idrReplays: s.idrReplays,
      idrCachedKb: s.idrCachedKb
    };
  }

  // 供 UI 读取原生采集统计（编码器帧数/字节/关键帧/录屏状态）
  getCaptureStats(): string {
    if (this.source instanceof NativeVideoSource) {
      const s = (this.source as NativeVideoSource).stats();
      return `native 帧 ${s.frames} ${Math.floor(s.bytes / 1024)}KB 关键帧 ${s.keyframes} 状态 ${s.state}` +
        ` 直发=${s.directSend === 1 ? 'on' : 'off'} 包 ${s.packets} 错 ${s.sendErrors}` +
        ` | IDR缓存 ${s.idrCachedKb}KB 重放 ${s.idrReplays} 次` +
        ` | 保活=${KeepAlive.isRunning() ? 'on' : 'off'}`;
    }
    return '';
  }

  private stopStream(): void {
    if (this.timerStream >= 0) {
      clearInterval(this.timerStream);
      this.timerStream = -1;
    }
    if (this.timerStats >= 0) {
      clearInterval(this.timerStats);
      this.timerStats = -1;
    }
    if (this.timerResolution >= 0) {
      clearInterval(this.timerResolution);
      this.timerResolution = -1;
    }
    if (this.source !== null) {
      if (this.source instanceof NativeVideoSource) {
        // 只暂停发送，保留采集会话 —— 下次开始推流不必重新弹录屏授权
        (this.source as NativeVideoSource).pauseSend();
      } else {
        this.source.stop().catch((): void => {});
      }
    }
    this.resetNativeBaseline();
    this.streaming = false;
    this.stats.streaming = false;
    this.log('码流停止（采集会话保留，可免授权恢复）');
  }

  private streamTick(): void {
    if (this.source === null) {
      return;
    }
    this.source.nextFrame().then((frame: VideoFrame | null): void => {
      if (frame === null || !this.streaming) {
        return;
      }
      this.transport.sendStreamFrame(frame.frameId, frame.keyframe, frame.data);
      this.stats.streamFrames++;
      this.stats.streamBytes += frame.data.length;
    }).catch((): void => {
      this.log('[WARN] 读取码流帧失败');
    });
  }

  private onResolutionChanged(r: ResolutionInfo): void {
    this.stats.resolution = r;
    // NEXT-06：分辨率变化必须重建编码器（NativeVideoSource 接入后在此重建）
    this.transport.sendControl(CMD_RESOLUTION, WireCodec.encodeResolution(r), true);
    this.log(`分辨率变更 → ${r.width}x${r.height} folded=${r.folded}（编码器需重建）`);
  }

  private writeClipText(text: string): void {
    try {
      const data: pasteboard.PasteData = pasteboard.createData('text/plain', text);
      pasteboard.getSystemPasteboard().setData(data).catch((): void => {});
    } catch (e) {
      this.log(`[WARN] 写剪贴板失败: ${String(e)}`);
    }
  }

  private onClipImage(body: Uint8Array): void {
    if (body.length < 10) {
      return;
    }
    const id: number = body[4] | (body[5] << 8);
    const index: number = body[6] | (body[7] << 8);
    const count: number = body[8] | (body[9] << 8);
    this.imageTotal = count;
    this.imageParts.set(index, body.subarray(10));
    this.stats.imageParts = this.imageParts.size;
    if (this.imageParts.size >= count && count > 0) {
      let total: number = 0;
      this.imageParts.forEach((v: Uint8Array): void => {
        total += v.length;
      });
      const merged: Uint8Array = new Uint8Array(total);
      let off: number = 0;
      for (let i = 0; i < count; i++) {
        const part: Uint8Array | undefined = this.imageParts.get(i);
        if (part !== undefined) {
          merged.set(part, off);
          off += part.length;
        }
      }
      this.log(`剪贴板 PNG ← PC 重组完成 id=${id} ${total}B（写入系统图片剪贴板待 C++ 侧实现）`);
      this.stats.clipToDevice++;
      this.imageParts.clear();
      this.stats.imageParts = 0;
    }
  }

  private log(msg: string): void {
    if (this.onLog !== null) {
      this.onLog(msg);
    }
  }
}
