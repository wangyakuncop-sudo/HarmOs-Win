// NEXT-07 / GATE-06：无障碍扩展服务（AccessibilityExtensionAbility）。
// 用户在系统设置开启服务后由 AAMS 拉起；onConnect 在 loopback 9021 监听动作，
// 执行三键(home/back/recentTask) 或单点手势，把 19B InjectReport 发回 9022。
//
// 能力边界由 INV-13 固定：只做三键导航 + 低频点击，不做任意坐标高频 mouse move。
// 注意：performAction / injectGestureSync 自 API 12 起标记 deprecated，但 API 26 的 d.ts 仍在导出，
// 是否真正可用必须靠 GATE-06 真机判定，不得预先写成可用。
// 区分两件事，别混：
//   ① Action 字面量 'recentTask' 是 since 12 **新增**（@ohos.accessibility.d.ts:75），不是废弃；
//   ② 废弃的是 AccessibilityElement.performAction 这三个重载本身
//      （api/application/AccessibilityExtensionContext.d.ts:351/368/384，均 @deprecated since 12）。
// 事实：'recentTask' 在本机 API 26 上未取得可用证据，PC 端「多任务」键已改为
// 直接下发系统键码 KEYCODE_VIRTUAL_MULTITASK = 2210，不再依赖本文件的 recentTask 分支。
// 本文件仅改注释，行为未变，故不随本次交付重编 HAP。

import { AccessibilityExtensionAbility, AccessibilityElement, GesturePath, GesturePoint } from '@kit.AccessibilityKit';
import { socket } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { BusinessError } from '@kit.BasicServicesKit';
import { WireCodec } from '../core/SessionTypes';
import {
  BRIDGE_ACTION_PORT, BRIDGE_RESULT_PORT,
  STAGE_HOME, STAGE_BACK, STAGE_RECENTS, STAGE_CLICK, STAGE_TEXT,
  TEXT_MAX_BYTES
} from './LowFreqBridge';

const LOOPBACK: string = '127.0.0.1';
const ACTION_LEN: number = 12;
const GESTURE_DURATION_MS: number = 50;

export default class LowFreqAccessibility extends AccessibilityExtensionAbility {
  private udp: socket.UDPSocket | null = null;
  private attempts: number = 0;
  private success: number = 0;
  private msgHandler: ((info: socket.SocketMessageInfo) => void) | null = null;
  private lastError: string = '';

  onConnect(): void {
    const cb = (info: socket.SocketMessageInfo): void => {
      this.handleMessage(info);
    };
    this.msgHandler = cb;
    const s: socket.UDPSocket = socket.constructUDPSocketInstance();
    s.on('message', cb);
    s.bind({ address: LOOPBACK, port: BRIDGE_ACTION_PORT, family: 1 }).then((): void => {
      this.udp = s;
    }).catch((): void => {
      s.off('message', cb);
      this.lastError = 'bind 9021 失败';
    });
  }

  onDisconnect(): void {
    const s: socket.UDPSocket | null = this.udp;
    this.udp = null;
    if (s !== null && this.msgHandler !== null) {
      s.off('message', this.msgHandler);
    }
    if (s !== null) {
      s.close().then((): void => {}).catch((): void => {});
    }
    this.msgHandler = null;
  }

  private handleMessage(info: socket.SocketMessageInfo): void {
    const p: Uint8Array = new Uint8Array(info.message);
    // STAGE_TEXT 是变长包（stage + len + utf8），不能走下面固定的 12B 分支
    if (p.length >= 4 && p[0] === STAGE_TEXT) {
      const len: number = p[1] | (p[2] << 8);
      if (len === 0 || len > TEXT_MAX_BYTES || p.length < 4 + len) {
        return;
      }
      const bytes: Uint8Array = p.slice(4, 4 + len);
      this.attempts++;
      const t0: number = Date.now();
      this.runText(bytes).then((code: number): void => {
        this.reply(STAGE_TEXT, code, (Date.now() - t0) * 1000);
      }).catch((e: BusinessError): void => {
        const code: number = typeof e.code === 'number' ? e.code : -1;
        this.reply(STAGE_TEXT, code, (Date.now() - t0) * 1000);
      });
      return;
    }
    if (p.length < ACTION_LEN) {
      return;
    }
    const stage: number = p[0];
    const x: number = p[1] | (p[2] << 8);
    const y: number = p[3] | (p[4] << 8);
    this.attempts++;
    const t0: number = Date.now();
    this.runAction(stage, x, y).then((code: number): void => {
      this.reply(stage, code, (Date.now() - t0) * 1000);
    }).catch((e: BusinessError): void => {
      const code: number = typeof e.code === 'number' ? e.code : -1;
      this.reply(stage, code, (Date.now() - t0) * 1000);
    });
  }

  private async runAction(stage: number, x: number, y: number): Promise<number> {
    try {
      if (stage === STAGE_HOME || stage === STAGE_BACK || stage === STAGE_RECENTS) {
        const name: string = stage === STAGE_HOME ? 'home' :
          (stage === STAGE_BACK ? 'back' : 'recentTask');
        // performAction 属于 AccessibilityElement，不是 AccessibilityExtensionContext：
        // 必须先取当前窗口根节点，再在其上执行全局动作（home/back/recentTask）。
        const root: AccessibilityElement = await this.context.getWindowRootElement();
        await root.performAction(name, {});
      } else if (stage === STAGE_CLICK) {
        const path: GesturePath = new GesturePath(GESTURE_DURATION_MS);
        path.points = [new GesturePoint(x, y)];
        this.context.injectGestureSync(path);
      } else {
        return -3; // 未知动作
      }
      return 0;
    } catch (e) {
      const err = e as BusinessError;
      return typeof err.code === 'number' ? err.code : -1;
    }
  }

  /// 把文本直接设进当前焦点输入框（不经过输入法，中文原样写入）。
  /// 能力边界：这是 DEC-08 降级之后新开的一条口子，**可行性尚未真机验证** ——
  /// API 26 是否接受 performAction('setText') 必须先测，测不通就回退到剪贴板粘贴方案。
  private async runText(bytes: Uint8Array): Promise<number> {
    try {
      const text: string = util.TextDecoder.create('utf-8').decodeToString(bytes);
      const root: AccessibilityElement = await this.context.getWindowRootElement();
      // findElement 必须给第二个参数，否则重载无法收窄到单元素（编译期会报
      // 'void & Promise<AccessibilityElement[]> & Promise<AccessibilityElement>'）。
      const focused: AccessibilityElement = await root.findElement('focusType', 'normal');
      if (focused === null || focused === undefined) {
        return -10;   // 没有焦点元素（用户没点到输入框）
      }
      await focused.performAction('setText', { setText: text });
      return 0;
    } catch (e) {
      const err = e as BusinessError;
      return typeof err.code === 'number' ? err.code : -1;
    }
  }

  private reply(stage: number, code: number, elapsedUs: number): void {    if (code === 0) {
      this.success++;
    }
    const body: Uint8Array = WireCodec.encodeInjectReport({
      syscap: 1, // 能回包就说明服务已连接
      ok: code === 0 ? 1 : 0,
      errCode: code,
      stage: stage,
      elapsedUs: elapsedUs,
      attempts: this.attempts,
      success: this.success
    });
    const buf: ArrayBuffer = new ArrayBuffer(body.length);
    new Uint8Array(buf).set(body);
    const s: socket.UDPSocket | null = this.udp;
    if (s === null) {
      return;
    }
    const opts: socket.UDPSendOptions = {
      data: buf,
      address: { address: LOOPBACK, port: BRIDGE_RESULT_PORT, family: 1 }
    };
    s.send(opts).then((): void => {}).catch((): void => {});
  }
}
