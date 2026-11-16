// NEXT-07 / GATE-06：主 HAP ↔ 无障碍 ExtensionAbility 的本地 UDP 桥。
// loopback 两个端口：9021 = 扩展服务监听动作，9022 = 主 HAP 收 19B 结果。
// 服务未开启时 9021 无人监听 → 250ms 超时，回传 errCode=-2「服务未响应」。
//
// 能力边界由 INV-13 固定：只做三键导航 + 低频点击，不做任意坐标高频 mouse move。

import { socket } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { WireCodec } from '../core/SessionTypes';

export const BRIDGE_ACTION_PORT: number = 9021;
export const BRIDGE_RESULT_PORT: number = 9022;
export const BRIDGE_TIMEOUT_MS: number = 250;

// 与 pc/HarmOsWin.Core/HwpCommands.cs 的 NavKey 对齐：1 back / 2 home / 3 recents；4 = 单点点击
export const STAGE_BACK: number = 1;
export const STAGE_HOME: number = 2;
export const STAGE_RECENTS: number = 3;
export const STAGE_CLICK: number = 4;
// 5 = 设置焦点输入框文本（键盘转发用）。载荷变长：stage u8 + len u16 + utf8。
// 单包上限 900 字节（约 300 汉字），超长由调用方分批（当前只用于可行性验证与单段输入）。
export const STAGE_TEXT: number = 5;
export const TEXT_MAX_BYTES: number = 900;

const LOOPBACK: string = '127.0.0.1';
const ACTION_LEN: number = 12;

/// 独立发一条 STAGE_TEXT 到无障碍服务（不经 SessionService，供探针页自测用）。
/// 返回 false 表示编码超限或发送失败。
export async function sendTextProbe(text: string): Promise<boolean> {
  const body: Uint8Array = new util.TextEncoder().encodeInto(text);
  if (body.length === 0 || body.length > TEXT_MAX_BYTES) {
    return false;
  }
  const buf: ArrayBuffer = new ArrayBuffer(4 + body.length);
  const p: Uint8Array = new Uint8Array(buf);
  p[0] = STAGE_TEXT;
  p[1] = body.length & 0xff;
  p[2] = (body.length >> 8) & 0xff;
  p[3] = 0;
  p.set(body, 4);
  const s: socket.UDPSocket = socket.constructUDPSocketInstance();
  try {
    await s.bind({ address: LOOPBACK, port: 9025, family: 1 });
    await s.send({
      data: buf,
      address: { address: LOOPBACK, port: BRIDGE_ACTION_PORT, family: 1 }
    });
  } catch (e) {
    return false;
  }
  try {
    await s.close();
  } catch (e) {
    // 关闭失败不影响本次发送结果
  }
  return true;
}

export class LowFreqBridge {
  private udp: socket.UDPSocket | null = null;
  private attempts: number = 0;
  private onResult: ((body: Uint8Array) => void) | null = null;
  private msgHandler: (info: socket.SocketMessageInfo) => void;
  private timer: number = -1;
  private lastError: string = '';

  constructor() {
    this.msgHandler = (info: socket.SocketMessageInfo): void => {
      this.handleMessage(info);
    };
  }

  setOnResult(cb: (body: Uint8Array) => void): void {
    this.onResult = cb;
  }

  getLastError(): string {
    return this.lastError;
  }

  async start(): Promise<void> {
    if (this.udp !== null) {
      return;
    }
    const s: socket.UDPSocket = socket.constructUDPSocketInstance();
    s.on('message', this.msgHandler);
    try {
      await s.bind({ address: LOOPBACK, port: BRIDGE_RESULT_PORT, family: 1 });
    } catch (e) {
      s.off('message', this.msgHandler);
      this.lastError = String(e);
      throw new Error(`本地桥 bind 失败: ${this.lastError}`);
    }
    this.udp = s;
  }

  async stop(): Promise<void> {
    if (this.timer >= 0) {
      clearTimeout(this.timer);
      this.timer = -1;
    }
    const s: socket.UDPSocket | null = this.udp;
    this.udp = null;
    if (s !== null) {
      s.off('message', this.msgHandler);
      try {
        await s.close();
      } catch (e) {
        this.lastError = String(e);
      }
    }
  }

  // 动作包 12B：stage u8 + x u16 + y u16 + seq u32 + 3B 保留
  send(stage: number, x: number, y: number): void {
    if (this.udp === null) {
      return;
    }
    this.attempts++;
    const buf: ArrayBuffer = new ArrayBuffer(ACTION_LEN);
    const p: Uint8Array = new Uint8Array(buf);
    p[0] = stage & 0xff;
    p[1] = x & 0xff;
    p[2] = (x >> 8) & 0xff;
    p[3] = y & 0xff;
    p[4] = (y >> 8) & 0xff;
    p[5] = this.attempts & 0xff;
    p[6] = (this.attempts >> 8) & 0xff;
    p[7] = (this.attempts >> 16) & 0xff;
    p[8] = (this.attempts >> 24) & 0xff;
    const opts: socket.UDPSendOptions = {
      data: buf,
      address: { address: LOOPBACK, port: BRIDGE_ACTION_PORT, family: 1 }
    };
    this.udp.send(opts).then((): void => {}).catch((): void => {});
    if (this.timer >= 0) {
      clearTimeout(this.timer);
    }
    this.timer = setTimeout((): void => {
      this.timer = -1;
      this.emit(WireCodec.encodeInjectReport({
        syscap: 0,
        ok: 0,
        errCode: -2, // 服务未响应（多半是没在系统设置里开启无障碍服务）
        stage: stage,
        elapsedUs: 0,
        attempts: this.attempts,
        success: 0
      }));
    }, BRIDGE_TIMEOUT_MS);
  }

  private handleMessage(info: socket.SocketMessageInfo): void {
    const body: Uint8Array = new Uint8Array(info.message);
    if (body.length < 19) {
      return;
    }
    if (this.timer >= 0) {
      clearTimeout(this.timer);
      this.timer = -1;
    }
    this.emit(body);
  }

  private emit(body: Uint8Array): void {
    if (this.onResult !== null) {
      this.onResult(body);
    }
  }
}
