// HWP v1 源端 UDP 双通道：视频不可靠 / 控制可靠 / 心跳保活。
// 规格见 docs/传输层协议-HWP-v1.md；本文件不含任何 UI 依赖，便于 GATE-02 复用。

import { socket } from '@kit.NetworkKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import {
  HwpPacket,
  HwpHeader,
  hwpNow,
  hwpElapsed,
  CH_VIDEO,
  CH_CONTROL,
  CH_HEARTBEAT,
  CH_ACK,
  CH_STREAM,
  FLAG_NEED_ACK,
  FLAG_IS_ACK,
  FLAG_PROBE,
  FLAG_KEYFRAME,
  FLAG_LAST,
  STREAM_META_SIZE,
  STREAM_DATA_MAX,
  CMD_IDLE,
  CMD_LIGHT,
  CMD_FULL,
  CMD_STOP,
  HEARTBEAT_INTERVAL_MS,
  VIDEO_FRAME_INTERVAL_MS,
  VIDEO_PACKETS_LIGHT,
  VIDEO_PACKETS_FULL,
  LOCAL_PORT,
  PEER_HOST,
  PEER_PORT
} from './HwpPacket';
import { WireCodec } from '../core/SessionTypes';

const LOG_DOMAIN: number = 0x0001;
const LOG_TAG: string = 'HwpL0';
const REPORT_INTERVAL_MS: number = 2000;

export interface TransportStats {
  sent: number;
  received: number;
  ackSent: number;
  videoPackets: number;
  heartbeats: number;
  sendErrors: number;
  mode: number;
  uptimeSec: number;
}

export class UdpTransport {
  private udp: socket.UDPSocket | null = null;
  private peer: socket.NetAddress | null = null;
  private timerHeartbeat: number = -1;
  private timerVideo: number = -1;
  private timerReport: number = -1;
  private seqVideo: number = 0;
  private seqHeartbeat: number = 0;
  private seqControl: number = 0;
  private controlHandler: ((cmd: number, body: Uint8Array) => boolean) | null = null;
  private mode: number = CMD_IDLE;
  private startTime: number = 0;
  private videoPayload: Uint8Array;
  private stats: TransportStats;
  private onLog: ((msg: string) => void) | null = null;
  // 运行时端点（缺省取 HwpPacket 的常量，可被 EntryAbility 的 --ps 参数覆盖）
  private peerHost: string = PEER_HOST;
  private peerPortNo: number = PEER_PORT;
  private localPortNo: number = LOCAL_PORT;
  private msgHandler: (info: socket.SocketMessageInfo) => void;

  constructor() {
    this.videoPayload = HwpPacket.makeVideoPayload();
    this.stats = {
      sent: 0,
      received: 0,
      ackSent: 0,
      videoPackets: 0,
      heartbeats: 0,
      sendErrors: 0,
      mode: CMD_IDLE,
      uptimeSec: 0
    };
    this.msgHandler = (info: socket.SocketMessageInfo): void => {
      this.handleMessage(info);
    };
  }

  setOnLog(cb: (msg: string) => void): void {
    this.onLog = cb;
  }

  // v1.1：业务层注册 CONTROL 派发器。返回 true 表示已消费，不再走 L0 的模式切换逻辑。
  setControlHandler(cb: (cmd: number, body: Uint8Array) => boolean): void {
    this.controlHandler = cb;
  }

  // 当前对端地址：实时码流的 native 直发通道需要它（源端口与本地 9020 不同，PC 端不跟随 remote）
  getPeerHost(): string {
    if (this.peer === null) {
      return this.peerHost;
    }
    return this.peer.address;
  }

  getPeerPort(): number {
    if (this.peer === null || this.peer.port === undefined) {
      return this.peerPortNo;
    }
    return this.peer.port;
  }

  // v1.1：设备端主动下发 CONTROL（剪贴板/角色/分辨率/锁屏状态）。
  sendControl(cmd: number, body: Uint8Array | null, needAck: boolean): void {    if (this.peer === null) {
      return;
    }
    const len: number = body === null ? 1 : 1 + body.length;
    const payload: Uint8Array = new Uint8Array(len);
    payload[0] = cmd & 0xff;
    if (body !== null && body.length > 0) {
      payload.set(body, 1);
    }
    this.seqControl++;
    const flags: number = needAck ? FLAG_NEED_ACK : 0;
    const buf: ArrayBuffer = HwpPacket.build(CH_CONTROL, flags, this.seqControl, hwpNow(), payload);
    this.emit(buf, this.peer);
  }

  // v1.1：真实 H.264 码流分片。丢包不重传，乱序由对端直接丢整帧。
  sendStreamFrame(frameId: number, keyframe: boolean, data: Uint8Array): void {
    if (this.peer === null || data.length === 0) {
      return;
    }
    const count: number = Math.ceil(data.length / STREAM_DATA_MAX);
    const ts: number = hwpNow();
    for (let i = 0; i < count; i++) {
      const from: number = i * STREAM_DATA_MAX;
      const to: number = Math.min(data.length, from + STREAM_DATA_MAX);
      const chunk: Uint8Array = data.subarray(from, to);
      const meta: Uint8Array = WireCodec.encodeStreamMeta(frameId, i, count);
      const payload: Uint8Array = new Uint8Array(STREAM_META_SIZE + chunk.length);
      payload.set(meta, 0);
      payload.set(chunk, STREAM_META_SIZE);
      let flags: number = 0;
      if (keyframe && i === 0) {
        flags = flags | FLAG_KEYFRAME;
      }
      if (i === count - 1) {
        flags = flags | FLAG_LAST;
      }
      this.seqVideo++;
      const buf: ArrayBuffer = HwpPacket.build(CH_STREAM, flags, this.seqVideo, ts, payload);
      this.emit(buf, this.peer);
      this.stats.videoPackets++;
    }
  }

  getStats(): TransportStats {
    const s: TransportStats = {
      sent: this.stats.sent,
      received: this.stats.received,
      ackSent: this.stats.ackSent,
      videoPackets: this.stats.videoPackets,
      heartbeats: this.stats.heartbeats,
      sendErrors: this.stats.sendErrors,
      mode: this.stats.mode,
      uptimeSec: this.stats.uptimeSec
    };
    return s;
  }

  async start(): Promise<void> {
    if (this.udp !== null) {
      return;
    }
    // 2026-11-14：端口/对端改为可被 --ps 参数覆盖（AppStorage 由 EntryAbility 写入）。
    // 起因：双路并行里手机走自有 HAP 采集，必须和平板用不同端口（9011/9021），
    // 否则两台的视频包会打到同一个 PC 端口上串台。缺省仍用常量，行为与原来一致。
    this.peerHost = AppStorage.get<string>('streamPeerHost') ?? PEER_HOST;
    this.peerPortNo = AppStorage.get<number>('streamPeerPort') ?? PEER_PORT;
    this.localPortNo = AppStorage.get<number>('streamLocalPort') ?? LOCAL_PORT;
    this.udp = socket.constructUDPSocketInstance();
    const local: socket.NetAddress = { address: '0.0.0.0', port: this.localPortNo, family: 1 };
    const peerAddr: socket.NetAddress = { address: this.peerHost, port: this.peerPortNo, family: 1 };
    this.peer = peerAddr;
    this.udp.on('message', this.msgHandler);
    try {
      await this.udp.bind(local);
    } catch (e) {
      this.udp.off('message', this.msgHandler);
      this.udp = null;
      throw new Error(`UDP bind 失败: ${String(e)}`);
    }
    this.startTime = hwpNow();
    this.timerReport = setInterval((): void => {
      this.report();
    }, REPORT_INTERVAL_MS);
    this.log(`L0 服务已启动 0.0.0.0:${this.localPortNo} → ${this.peerHost}:${this.peerPortNo}`);
  }

  async stop(): Promise<void> {
    this.stopTimers();
    if (this.timerReport >= 0) {
      clearInterval(this.timerReport);
      this.timerReport = -1;
    }
    if (this.udp !== null) {
      this.udp.off('message', this.msgHandler);
      try {
        await this.udp.close();
      } catch (e) {
        this.log(`[WARN] close: ${String(e)}`);
      }
      this.udp = null;
    }
    this.mode = CMD_IDLE;
    this.stats.mode = CMD_IDLE;
    this.log('L0 服务已停止');
  }

  private handleMessage(info: socket.SocketMessageInfo): void {
    this.stats.received++;
    const header: HwpHeader | null = HwpPacket.parse(info.message);
    if (header === null) {
      return;
    }
    const remote: socket.NetAddress = {
      address: info.remoteInfo.address,
      port: info.remoteInfo.port,
      family: 1
    };
    this.peer = remote;

    if (header.channel === CH_CONTROL && header.payloadLen > 0) {
      const payload: Uint8Array = HwpPacket.payloadOf(info.message, header);
      const cmd: number = payload[0];
      let consumed: boolean = false;
      if (this.controlHandler !== null) {
        consumed = this.controlHandler(cmd, payload.subarray(1));
      }
      if (!consumed) {
        // v1.0 L0 回归路径保持原样
        this.applyMode(cmd);
      }
      if ((header.flags & FLAG_NEED_ACK) !== 0) {
        this.sendAck(header, remote);
      }
      return;
    }
    if (header.channel === CH_ACK) {
      // 设备端主动 CONTROL 的 ACK 到达；可靠重传由 PC 端 RtoEstimator 负责
      return;
    }
    if ((header.flags & FLAG_PROBE) !== 0) {
      this.sendAck(header, remote);
    }
  }

  private applyMode(cmd: number): void {
    if (cmd === CMD_STOP || cmd === CMD_IDLE) {
      this.stopTimers();
      this.mode = CMD_IDLE;
      this.stats.mode = CMD_IDLE;
      this.log(`模式切换 → ${cmd === CMD_STOP ? 'STOP' : 'IDLE'}（心跳与负载已关闭）`);
      return;
    }
    if (cmd === CMD_LIGHT || cmd === CMD_FULL) {
      this.mode = cmd;
      this.stats.mode = cmd;
      this.startTimers();
      this.log(`模式切换 → ${cmd === CMD_FULL ? 'FULL 16Mbps' : 'LIGHT 2.3Mbps'}（心跳 ${HEARTBEAT_INTERVAL_MS}ms）`);
      return;
    }
    this.log(`[WARN] 未知控制命令 0x${cmd.toString(16)}`);
  }

  private sendAck(header: HwpHeader, remote: socket.NetAddress): void {
    const buf: ArrayBuffer = HwpPacket.build(CH_ACK, FLAG_IS_ACK, header.seq, header.timestampMs, null);
    this.emit(buf, remote);
    this.stats.ackSent++;
  }

  private emit(buf: ArrayBuffer, addr: socket.NetAddress): void {
    if (this.udp === null) {
      return;
    }
    const opts: socket.UDPSendOptions = { data: buf, address: addr };
    this.udp.send(opts).then((): void => {}).catch((): void => {
      this.stats.sendErrors++;
    });
    this.stats.sent++;
  }

  private heartbeatTick(): void {
    if (this.peer === null) {
      return;
    }
    this.seqHeartbeat++;
    const buf: ArrayBuffer = HwpPacket.build(CH_HEARTBEAT, 0, this.seqHeartbeat, hwpNow(), null);
    this.emit(buf, this.peer);
    this.stats.heartbeats++;
  }

  private videoTick(): void {
    if (this.peer === null) {
      return;
    }
    const count: number = this.mode === CMD_FULL ? VIDEO_PACKETS_FULL : VIDEO_PACKETS_LIGHT;
    const ts: number = hwpNow();
    for (let i = 0; i < count; i++) {
      this.seqVideo++;
      const buf: ArrayBuffer = HwpPacket.build(CH_VIDEO, 0, this.seqVideo, ts, this.videoPayload);
      this.emit(buf, this.peer);
      this.stats.videoPackets++;
    }
  }

  private startTimers(): void {
    if (this.timerHeartbeat < 0) {
      this.timerHeartbeat = setInterval((): void => {
        this.heartbeatTick();
      }, HEARTBEAT_INTERVAL_MS);
    }
    if (this.timerVideo < 0) {
      this.timerVideo = setInterval((): void => {
        this.videoTick();
      }, VIDEO_FRAME_INTERVAL_MS);
    }
  }

  private stopTimers(): void {
    if (this.timerHeartbeat >= 0) {
      clearInterval(this.timerHeartbeat);
      this.timerHeartbeat = -1;
    }
    if (this.timerVideo >= 0) {
      clearInterval(this.timerVideo);
      this.timerVideo = -1;
    }
  }

  private report(): void {
    this.stats.uptimeSec = Math.floor(hwpElapsed(hwpNow(), this.startTime) / 1000);
    const s: TransportStats = this.stats;
    const modeName: string = s.mode === CMD_FULL ? 'FULL' : (s.mode === CMD_LIGHT ? 'LIGHT' : 'IDLE');
    this.log(`t=${s.uptimeSec}s mode=${modeName} sent=${s.sent} recv=${s.received} ` +
      `ack=${s.ackSent} video=${s.videoPackets} hb=${s.heartbeats} err=${s.sendErrors}`);
  }

  private log(msg: string): void {
    hilog.info(LOG_DOMAIN, LOG_TAG, '%{public}s', msg);
    if (this.onLog !== null) {
      this.onLog(msg);
    }
  }
}
