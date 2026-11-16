import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { common, wantAgent } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

const DOMAIN: number = 0x0001;
const TAG: string = 'GATE01';

/**
 * 长时任务封装（多设备互联类型）。
 *
 * 息屏后系统默认会挂起甚至回收普通应用进程，采集与网络都会中断。
 * 声明 backgroundModes = ["multiDeviceConnection"] +
 * 申请 ohos.permission.KEEP_BACKGROUND_RUNNING 后，
 * 调用 startBackgroundRunning 可在熄屏/后台继续运行。
 */
export class KeepAlive {
  private static running: boolean = false;
  private static ctx: common.UIAbilityContext | undefined = undefined;

  static isRunning(): boolean {
    return KeepAlive.running;
  }

  static async start(ctx: common.UIAbilityContext): Promise<boolean> {
    if (KeepAlive.running) {
      return true;
    }
    try {
      const info: wantAgent.WantAgentInfo = {
        wants: [
          {
            bundleName: ctx.abilityInfo.bundleName,
            abilityName: ctx.abilityInfo.name
          }
        ],
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 0,
        actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
      };
      const agent = await wantAgent.getWantAgent(info);
      // AVScreenCaptureRecorder 的 preset 强制带 AAC 音轨（SDK 只提供 H264_AAC / H265_AAC 两种），
      // 息屏后若只申请 multiDeviceConnection，系统会以 IllegalAudioCapturerBySuspend 杀进程。
      // 因此同时申请 audioRecording，让音轨在后台合法。
      const modes: string[] = ['multiDeviceConnection', 'audioRecording'];
      await backgroundTaskManager.startBackgroundRunning(ctx, modes, agent);
      KeepAlive.running = true;
      KeepAlive.ctx = ctx;
      hilog.info(DOMAIN, TAG, 'KEEPALIVE started mode=multiDeviceConnection');
      return true;
    } catch (e) {
      const err = e as BusinessError;
      hilog.error(DOMAIN, TAG, 'KEEPALIVE start failed code=%{public}d msg=%{public}s', err.code, err.message);
      return false;
    }
  }

  static async stop(): Promise<void> {
    if (!KeepAlive.running) {
      return;
    }
    const ctx = KeepAlive.ctx;
    if (ctx === undefined) {
      KeepAlive.running = false;
      return;
    }
    try {
      await backgroundTaskManager.stopBackgroundRunning(ctx);
      hilog.info(DOMAIN, TAG, 'KEEPALIVE stopped');
    } catch (e) {
      const err = e as BusinessError;
      hilog.error(DOMAIN, TAG, 'KEEPALIVE stop failed code=%{public}d msg=%{public}s', err.code, err.message);
    }
    KeepAlive.running = false;
    KeepAlive.ctx = undefined;
  }
}
