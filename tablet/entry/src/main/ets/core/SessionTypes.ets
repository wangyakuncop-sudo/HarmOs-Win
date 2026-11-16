// NEXT-02~06 跨端共享模型与线格式编解码。
// 命令字与 pc/HarmOsWin.Core/HwpCommands.cs 逐字节对齐；禁止在业务代码里另写魔数。

export const ROLE_SOURCE: number = 1;
export const ROLE_HOST: number = 2;
export const ROLE_BOTH: number = 3;

export const NAV_BACK: number = 1;
export const NAV_HOME: number = 2;
export const NAV_RECENTS: number = 3;

export const EV_TOUCH: number = 1;
export const EV_KEY: number = 2;
export const EV_MOVE: number = 3;

export const ACT_DOWN: number = 1;
export const ACT_UP: number = 2;
export const ACT_MOVE: number = 3;

// INV-08：角色由连接协商决定，不在任何一端写死。
export interface DeviceProfile {
  deviceId: number;
  role: number;
  width: number;
  height: number;
  folded: number;
}

export interface ResolutionInfo {
  width: number;
  height: number;
  folded: number;
}

export interface InputEvent {
  type: number;
  action: number;
  x: number;
  y: number;
  key: number;
  ts: number;
}

export interface ClipText {
  hash: number;
  text: string;
}

// FNV-1a 32 位：剪贴板防回环用，双端实现必须一致。
export function fnv1a(data: Uint8Array): number {
  let h: number = 0x811c9dc5;
  for (let i = 0; i < data.length; i++) {
    h = h ^ data[i];
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

export function utf8Encode(s: string): Uint8Array {
  const out: number[] = [];
  for (let i = 0; i < s.length; i++) {
    const c: number = s.charCodeAt(i);
    if (c < 0x80) {
      out.push(c);
    } else if (c < 0x800) {
      out.push(0xc0 | (c >> 6), 0x80 | (c & 0x3f));
    } else if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
      const c2: number = s.charCodeAt(i + 1);
      const cp: number = 0x10000 + ((c - 0xd800) << 10) + (c2 - 0xdc00);
      out.push(0xf0 | (cp >> 18), 0x80 | ((cp >> 12) & 0x3f),
        0x80 | ((cp >> 6) & 0x3f), 0x80 | (cp & 0x3f));
      i++;
    } else {
      out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 0x3f), 0x80 | (c & 0x3f));
    }
  }
  return new Uint8Array(out);
}

export function utf8Decode(data: Uint8Array): string {
  let s: string = '';
  let i: number = 0;
  while (i < data.length) {
    const b: number = data[i];
    if (b < 0x80) {
      s += String.fromCharCode(b);
      i++;
    } else if (b < 0xe0 && i + 1 < data.length) {
      s += String.fromCharCode(((b & 0x1f) << 6) | (data[i + 1] & 0x3f));
      i += 2;
    } else if (b < 0xf0 && i + 2 < data.length) {
      s += String.fromCharCode(((b & 0x0f) << 12) | ((data[i + 1] & 0x3f) << 6) |
        (data[i + 2] & 0x3f));
      i += 3;
    } else if (i + 3 < data.length) {
      const cp: number = ((b & 0x07) << 18) | ((data[i + 1] & 0x3f) << 12) |
        ((data[i + 2] & 0x3f) << 6) | (data[i + 3] & 0x3f);
      const v: number = cp - 0x10000;
      s += String.fromCharCode(0xd800 + (v >> 10), 0xdc00 + (v & 0x3ff));
      i += 4;
    } else {
      i++;
    }
  }
  return s;
}

function putU16(p: Uint8Array, off: number, v: number): void {
  p[off] = v & 0xff;
  p[off + 1] = (v >> 8) & 0xff;
}

function putU32(p: Uint8Array, off: number, v: number): void {
  p[off] = v & 0xff;
  p[off + 1] = (v >> 8) & 0xff;
  p[off + 2] = (v >> 16) & 0xff;
  p[off + 3] = (v >> 24) & 0xff;
}

function getU16(p: Uint8Array, off: number): number {
  return (p[off] | (p[off + 1] << 8)) & 0xffff;
}

function getU32(p: Uint8Array, off: number): number {
  return (p[off] | (p[off + 1] << 8) | (p[off + 2] << 16) | (p[off + 3] << 24)) >>> 0;
}

// 所有 CONTROL payload 的第 0 字节是命令字；下面函数只处理命令字之后的部分，
// 由 SessionService 负责拼上首字节。
export class WireCodec {
  static encodeInput(e: InputEvent): Uint8Array {
    const p: Uint8Array = new Uint8Array(12);
    p[0] = e.type & 0xff;
    p[1] = e.action & 0xff;
    putU16(p, 2, e.x);
    putU16(p, 4, e.y);
    putU16(p, 6, e.key);
    putU32(p, 8, e.ts);
    return p;
  }

  static decodeInput(p: Uint8Array): InputEvent {
    return {
      type: p.length > 0 ? p[0] : 0,
      action: p.length > 1 ? p[1] : 0,
      x: p.length >= 4 ? getU16(p, 2) : 0,
      y: p.length >= 6 ? getU16(p, 4) : 0,
      key: p.length >= 8 ? getU16(p, 6) : 0,
      ts: p.length >= 12 ? getU32(p, 8) : 0
    };
  }

  static encodeResolution(r: ResolutionInfo): Uint8Array {
    const p: Uint8Array = new Uint8Array(5);
    putU16(p, 0, r.width);
    putU16(p, 2, r.height);
    p[4] = r.folded & 0xff;
    return p;
  }

  static decodeResolution(p: Uint8Array): ResolutionInfo {
    return {
      width: p.length >= 2 ? getU16(p, 0) : 0,
      height: p.length >= 4 ? getU16(p, 2) : 0,
      folded: p.length >= 5 ? p[4] : 0
    };
  }

  static encodeRole(profile: DeviceProfile): Uint8Array {
    return new Uint8Array([profile.role & 0xff, profile.deviceId & 0xff]);
  }

  static encodeNav(key: number): Uint8Array {
    return new Uint8Array([key & 0xff]);
  }

  static encodeStreamCtl(action: number): Uint8Array {
    return new Uint8Array([action & 0xff]);
  }

  static encodeClipText(hash: number, text: string): Uint8Array {
    const body: Uint8Array = utf8Encode(text);
    const p: Uint8Array = new Uint8Array(4 + body.length);
    putU32(p, 0, hash);
    p.set(body, 4);
    return p;
  }

  static decodeClipText(p: Uint8Array): ClipText {
    return {
      hash: p.length >= 4 ? getU32(p, 0) : 0,
      text: p.length > 4 ? utf8Decode(p.subarray(4)) : ''
    };
  }

  // CH_STREAM 分片元数据：frameId u32 + fragment u16 + fragCount u16
  static encodeStreamMeta(frameId: number, fragment: number, fragCount: number): Uint8Array {
    const p: Uint8Array = new Uint8Array(8);
    putU32(p, 0, frameId);
    putU16(p, 4, fragment);
    putU16(p, 6, fragCount);
    return p;
  }

  static decodeStreamMeta(p: Uint8Array): number[] {
    return [getU32(p, 0), getU16(p, 4), getU16(p, 6)];
  }

  // GATE-05 注入结果回传，19B 定长：
  // syscap u8 + ok u8 + errCode u32 + stage u8 + elapsedUs u32 + attempts u32 + success u32
  static encodeInjectReport(r: InjectReport): Uint8Array {
    const p: Uint8Array = new Uint8Array(19);
    p[0] = r.syscap & 0xff;
    p[1] = r.ok & 0xff;
    putU32(p, 2, r.errCode);
    p[6] = r.stage & 0xff;
    putU32(p, 7, r.elapsedUs);
    putU32(p, 11, r.attempts);
    putU32(p, 15, r.success);
    return p;
  }

  static decodeInjectReport(p: Uint8Array): InjectReport {
    return {
      syscap: p.length > 0 ? p[0] : 0,
      ok: p.length > 1 ? p[1] : 0,
      errCode: p.length >= 6 ? (getU32(p, 2) | 0) : 0, // 还原为有符号码（如 -2 服务未响应）
      stage: p.length > 6 ? p[6] : 0,
      elapsedUs: p.length >= 11 ? getU32(p, 7) : 0,
      attempts: p.length >= 15 ? getU32(p, 11) : 0,
      success: p.length >= 19 ? getU32(p, 15) : 0
    };
  }
}

// GATE-05 设备端注入探针回传模型（与 pc/HarmOsWin.Core/HwpCommands.cs 逐字节对齐）
export interface InjectReport {
  syscap: number;    // 1 支持 / 0 不支持
  ok: number;        // 1 成功 / 0 失败
  errCode: number;   // BusinessError.code；0 成功 / 201 权限 / 801 能力不支持
  stage: number;     // 0 完成 / 1 create / 2 touchDown / 3 touchUp
  elapsedUs: number; // 本地注入耗时（微秒）
  attempts: number;  // 累计尝试次数，兼作回传序号
  success: number;   // 累计成功次数
}
