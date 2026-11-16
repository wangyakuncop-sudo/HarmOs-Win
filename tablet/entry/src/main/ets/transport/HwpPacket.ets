// HWP v1 包头编解码。规格见 docs/传输层协议-HWP-v1.md
// 双端逐字节对齐，禁止在本文件之外硬编码偏移与魔数。

export const HWP_MAGIC: number = 0x4857;
export const HWP_VERSION: number = 1;
export const HWP_HEADER_SIZE: number = 16;
export const HWP_MTU: number = 1200;
export const HWP_PAYLOAD_MAX: number = HWP_MTU - HWP_HEADER_SIZE;

export const CH_VIDEO: number = 1;
export const CH_CONTROL: number = 2;
export const CH_HEARTBEAT: number = 3;
export const CH_ACK: number = 4;
export const CH_STREAM: number = 5;

export const FLAG_NEED_ACK: number = 0x01;
export const FLAG_IS_ACK: number = 0x02;
export const FLAG_PROBE: number = 0x04;
export const FLAG_KEYFRAME: number = 0x08;
export const FLAG_LAST: number = 0x10;

export const CMD_IDLE: number = 0x00;
export const CMD_LIGHT: number = 0x01;
export const CMD_FULL: number = 0x02;
export const CMD_STOP: number = 0xFF;

// v1.1 命令字（与 pc/HarmOsWin.Core/HwpCommands.cs 逐字节对齐）
export const CMD_INPUT: number = 0x10;
export const CMD_CLIP_TEXT: number = 0x20;
export const CMD_CLIP_IMAGE: number = 0x21;
export const CMD_NAV: number = 0x30;
export const CMD_ROLE: number = 0x40;
export const CMD_RESOLUTION: number = 0x50;
export const CMD_STREAM: number = 0x60;
export const CMD_LOCK_STATE: number = 0x70;

// v1.1 / GATE-05：设备端注入探针结果回传（仅探针使用，不参与正式会话语义）
export const CMD_INJECT_RESULT: number = 0x80;

export const STREAM_META_SIZE: number = 8;
export const STREAM_DATA_MAX: number = HWP_PAYLOAD_MAX - STREAM_META_SIZE;

export const HEARTBEAT_INTERVAL_MS: number = 30;
export const VIDEO_FRAME_INTERVAL_MS: number = 16;
export const VIDEO_PACKETS_LIGHT: number = 4;
export const VIDEO_PACKETS_FULL: number = 28;
export const CONTROL_RTO_MS: number = 20;
export const CONTROL_MAX_RETRY: number = 3;

export const LOCAL_PORT: number = 9020;
export const PEER_PORT: number = 9010;
export const PEER_HOST: string = '192.168.1.102';

export interface HwpHeader {
  channel: number;
  flags: number;
  seq: number;
  timestampMs: number;
  payloadLen: number;
}

export function hwpNow(): number {
  return Date.now() >>> 0;
}

// 无符号回绕差：用于计算 RTT，兼容 uint32 时间戳回绕。
export function hwpElapsed(now: number, before: number): number {
  return (now - before) >>> 0;
}

export class HwpPacket {
  static build(channel: number, flags: number, seq: number, ts: number,
    payload: Uint8Array | null): ArrayBuffer {
    const len: number = payload === null ? 0 : payload.length;
    const buf: ArrayBuffer = new ArrayBuffer(HWP_HEADER_SIZE + len);
    const dv: DataView = new DataView(buf);
    dv.setUint16(0, HWP_MAGIC, true);
    dv.setUint8(2, HWP_VERSION);
    dv.setUint8(3, channel);
    dv.setUint8(4, flags);
    dv.setUint8(5, 0);
    dv.setUint32(6, seq >>> 0, true);
    dv.setUint32(10, ts >>> 0, true);
    dv.setUint16(14, len, true);
    if (payload !== null && len > 0) {
      const dst: Uint8Array = new Uint8Array(buf, HWP_HEADER_SIZE, len);
      dst.set(payload);
    }
    return buf;
  }

  static parse(data: ArrayBuffer): HwpHeader | null {
    if (data.byteLength < HWP_HEADER_SIZE) {
      return null;
    }
    const dv: DataView = new DataView(data);
    if (dv.getUint16(0, true) !== HWP_MAGIC || dv.getUint8(2) !== HWP_VERSION) {
      return null;
    }
    const payloadLen: number = dv.getUint16(14, true);
    if (payloadLen > HWP_PAYLOAD_MAX || HWP_HEADER_SIZE + payloadLen > data.byteLength) {
      return null;
    }
    const header: HwpHeader = {
      channel: dv.getUint8(3),
      flags: dv.getUint8(4),
      seq: dv.getUint32(6, true),
      timestampMs: dv.getUint32(10, true),
      payloadLen: payloadLen
    };
    return header;
  }

  static payloadOf(data: ArrayBuffer, header: HwpHeader): Uint8Array {
    return new Uint8Array(data, HWP_HEADER_SIZE, header.payloadLen);
  }

  static makeVideoPayload(): Uint8Array {
    const p: Uint8Array = new Uint8Array(HWP_PAYLOAD_MAX);
    p.fill(0xA5);
    return p;
  }
}
