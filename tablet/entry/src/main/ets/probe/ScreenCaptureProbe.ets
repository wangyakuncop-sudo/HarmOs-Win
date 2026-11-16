import { media } from '@kit.MediaKit';
import { fileIo } from '@kit.CoreFileKit';
import { display } from '@kit.ArkUI';
import { common } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { power } from '@kit.BasicServicesKit';

const DOMAIN: number = 0x0001;
const TAG: string = 'GATE01';
const SAMPLE_INTERVAL_MS: number = 500;
// 采集管线建立 + 系统隐私确认存在数秒延迟，宽限期内不做停流判定
const GRACE_MS: number = 15000;
// AVScreenCaptureStateCode 数值，取自 SDK @ohos.multimedia.media.d.ts:7928。
// 直接引用枚举成员会触发 ArkTS「not supported on all devices」告警（deviceTypes 含 phone），
// 改用等价数值常量，语义不变。
const STATE_CANCELED: number = 1;
const STATE_STOPPED_BY_USER: number = 2;
const STATE_INTERRUPTED_BY_OTHER: number = 3;
const STATE_STOPPED_BY_CALL: number = 4;
const SYSCAP_AVSCREENCAPTURE: string = 'SystemCapability.Multimedia.Media.AVScreenCapture';

/**
 * GATE-01 探针：验证息屏后 AVScreenCapture 是否仍持续产出新帧。
 *
 * 判定依据（不依赖肉眼）：
 *  1. stateChange 回调是否上报停止类状态码
 *  2. 输出文件大小在息屏后是否仍在增长（每 500ms 采样一次）
 *  3. 出现连续 N 次 delta=0 即判定为「停流」
 */
export class ScreenCaptureProbe {
  private capture: media.AVScreenCaptureRecorder | undefined = undefined;
  private file: fileIo.File | undefined = undefined;
  private filePath: string = '';
  private timerId: number = -1;
  private watchdogId: number = -1;
  private startTs: number = 0;
  private lastBytes: number = 0;
  private frozenTicks: number = 0;
  private running: boolean = false;
  private tickListener: ((msg: string) => void) | undefined = undefined;
  private stopListener: ((msg: string) => void) | undefined = undefined;

  onTick(cb: (msg: string) => void): void {
    this.tickListener = cb;
  }

  onStopped(cb: (msg: string) => void): void {
    this.stopListener = cb;
  }

  isRunning(): boolean {
    return this.running;
  }

  getFilePath(): string {
    return this.filePath;
  }

  async start(ctx: common.UIAbilityContext, durationMs: number): Promise<void> {
    if (this.running) {
      return;
    }
    let info: display.Display;
    try {
      info = display.getDefaultDisplaySync();
    } catch (e) {
      throw new Error(`获取屏幕信息失败: ${String(e)}`);
    }
    this.filePath = ctx.filesDir + '/gate01.mp4';
    try {
      this.file = fileIo.openSync(
        this.filePath,
        fileIo.OpenMode.READ_WRITE | fileIo.OpenMode.CREATE | fileIo.OpenMode.TRUNC
      );
    } catch (e) {
      throw new Error(`打开录像文件失败: ${String(e)}`);
    }

    let recorder: media.AVScreenCaptureRecorder;
    if (canIUse(SYSCAP_AVSCREENCAPTURE)) {
      try {
        recorder = await media.createAVScreenCaptureRecorder();
      } catch (e) {
        throw new Error(`创建采集器失败: ${String(e)}`);
      }
    } else {
      throw new Error('设备不支持 AVScreenCapture 系统能力');
    }
    this.capture = recorder;

    this.capture.on('stateChange', (code: media.AVScreenCaptureStateCode) => {
      const elapsed = Date.now() - this.startTs;
      hilog.info(DOMAIN, TAG, 'STATE code=%{public}d t=%{public}d', code, elapsed);
      if (this.tickListener !== undefined) {
        this.tickListener(`[STATE] code=${code} t=${elapsed}ms`);
      }
      // 停止类状态码：CANCELED / STOPPED_BY_USER / INTERRUPTED_BY_OTHER / STOPPED_BY_CALL
      const state: number = Number(code);
      if (state === STATE_CANCELED ||
        state === STATE_STOPPED_BY_USER ||
        state === STATE_INTERRUPTED_BY_OTHER ||
        state === STATE_STOPPED_BY_CALL) {
        hilog.warn(DOMAIN, TAG, 'CAPTURE_STOPPED_BY_SYSTEM code=%{public}d', code);
        this.stopInternal(`系统回调停止 code=${code}`);
      }
    });

    this.capture.on('error', (err: BusinessError) => {
      hilog.error(DOMAIN, TAG, 'ERR code=%{public}d msg=%{public}s', err.code, err.message);
      if (this.tickListener !== undefined) {
        this.tickListener(`[ERR] ${err.code} ${err.message}`);
      }
    });

    const cfg: media.AVScreenCaptureRecordConfig = {
      frameWidth: info.width,
      frameHeight: info.height,
      fd: this.file.fd,
      videoBitrate: 10000000,
      displayId: 0
    };

    try {
      await this.capture.init(cfg);
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'INIT_FAIL %{public}s', String(e));
      throw new Error(`AVScreenCapture init 失败: ${String(e)}`);
    }
    this.startTs = Date.now();
    this.lastBytes = 0;
    this.frozenTicks = 0;
    try {
      await this.capture.startRecording();
    } catch (e) {
      hilog.error(DOMAIN, TAG, 'START_FAIL %{public}s', String(e));
      throw new Error(`AVScreenCapture startRecording 失败: ${String(e)}`);
    }
    this.running = true;

    hilog.info(DOMAIN, TAG, 'START w=%{public}d h=%{public}d path=%{public}s',
      info.width, info.height, this.filePath);

    this.timerId = setInterval(() => {
      this.sample();
    }, SAMPLE_INTERVAL_MS);

    this.watchdogId = setTimeout(() => {
      this.stopInternal('到时自动结束');
    }, durationMs);
  }

  private sample(): void {
    if (!this.running) {
      return;
    }
    let size = 0;
    try {
      size = fileIo.statSync(this.filePath).size;
    } catch (e) {
      const err = e as BusinessError;
      hilog.error(DOMAIN, TAG, 'stat failed %{public}s', err.message);
    }
    const elapsed = Date.now() - this.startTs;
    const delta = size - this.lastBytes;
    this.lastBytes = size;
    // 屏幕电源状态：确认息屏发生的确切时刻（true=亮屏，false=熄屏）
    let active = true;
    try {
      active = power.isActive();
    } catch (e) {
      active = true;
    }
    if (delta <= 0) {
      // 宽限期内（管线建立 / 系统授权确认）不计入停流判定
      if (elapsed > GRACE_MS) {
        this.frozenTicks++;
      }
    } else {
      this.frozenTicks = 0;
    }
    hilog.info(DOMAIN, TAG, 'TICK t=%{public}d size=%{public}d delta=%{public}d frozen=%{public}d active=%{public}s',
      elapsed, size, delta, this.frozenTicks, active ? 'ON' : 'OFF');
    if (this.tickListener !== undefined) {
      const sec = (elapsed / 1000).toFixed(1);
      const kb = (size / 1024).toFixed(1);
      this.tickListener(`t=${sec}s size=${kb}KB Δ=${delta} frozen=${this.frozenTicks}屏=${active ? 'ON' : 'OFF'}`);
    }
    // 连续 10 次（5 秒）无增长，判定停流
    if (this.frozenTicks >= 10) {
      this.stopInternal('STALL 连续 5 秒无新数据');
    }
  }

  async stop(): Promise<void> {
    await this.stopInternal('用户手动停止');
  }

  private async stopInternal(reason: string): Promise<void> {
    if (!this.running) {
      return;
    }
    this.running = false;
    const elapsed = Date.now() - this.startTs;

    if (this.timerId !== -1) {
      clearInterval(this.timerId);
      this.timerId = -1;
    }
    if (this.watchdogId !== -1) {
      clearTimeout(this.watchdogId);
      this.watchdogId = -1;
    }

    const cap = this.capture;
    this.capture = undefined;
    if (cap !== undefined) {
      try {
        await cap.stopRecording();
      } catch (e) {
        const err = e as BusinessError;
        hilog.error(DOMAIN, TAG, 'stopRecording failed %{public}s', err.message);
      }
      try {
        await cap.release();
      } catch (e) {
        const err = e as BusinessError;
        hilog.error(DOMAIN, TAG, 'release failed %{public}s', err.message);
      }
    }

    if (this.file !== undefined) {
      try {
        fileIo.closeSync(this.file);
      } catch (e) {
        const err = e as BusinessError;
        hilog.error(DOMAIN, TAG, 'close failed %{public}s', err.message);
      }
      this.file = undefined;
    }

    let total = 0;
    try {
      total = fileIo.statSync(this.filePath).size;
    } catch (e) {
      const err = e as BusinessError;
      hilog.error(DOMAIN, TAG, 'stat final failed %{public}s', err.message);
    }
    hilog.info(DOMAIN, TAG, 'STOP reason=%{public}s duration=%{public}d total=%{public}d',
      reason, elapsed, total);
    if (this.stopListener !== undefined) {
      this.stopListener(`结束：${reason}｜时长 ${(elapsed / 1000).toFixed(1)}s｜大小 ${(total / 1024).toFixed(1)}KB`);
    }
  }
}
