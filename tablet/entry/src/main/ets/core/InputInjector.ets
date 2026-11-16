// GATE-05：设备端输入注入能力探针（DEC-08 重新选型的实测依据）。
//
// 依据：SDK API 26 公开模块 @ohos.multimodalInput.inputEventClient（经 @kit.InputKit 导出，
// @since 26.0.0，非 @systemapi）。它提供 createTouchController / createKeyboardController /
// createMouseController，可注入任意坐标触摸、鼠标移动与按键，正是需求 2 所需能力。
// syscap SystemCapability.MultimodalInput.Input.InputSimulator 在 tablet.json / default.json 均已声明。
//
// 变数：上述 API 要求 ohos.permission.CONTROL_DEVICE
//   (system_basic / manual_settings / provisionEnable=true / deviceTypes=["2in1"] / since 26.0.0)。
// 本轮不声明该权限（避免重演 READ_PASTEBOARD 的 9568289 装机失败），先测无权限时的
// 真实错误码与 syscap 落地情况，再决定是否走 ACL 或无障碍手势注入。
//
// 本类只做「一次完整 touchDown → touchUp 序列」，不产生事件洪流，不改动 HWP 传输链路。

import { inputEventClient } from '@kit.InputKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { InjectReport } from './SessionTypes';

export const SYSCAP_INPUT_SIMULATOR: string =
  'SystemCapability.MultimodalInput.Input.InputSimulator';

export class InputInjector {
  private syscap: number = 0;
  private attempts: number = 0;
  private success: number = 0;
  private errCode: number = 0;
  private stage: number = 0;
  private elapsedUs: number = 0;

  constructor() {
    this.syscap = canIUse(SYSCAP_INPUT_SIMULATOR) ? 1 : 0;
  }

  // stage: 0 完成 / 1 createTouchController / 2 touchDown / 3 touchUp
  async inject(x: number, y: number): Promise<InjectReport> {
    this.attempts++;
    this.errCode = 0;
    this.stage = 1;
    const t0: number = Date.now();
    try {
      const ctrl = await inputEventClient.createTouchController();
      this.stage = 2;
      await ctrl.touchDown({ id: 0, displayId: 0, displayX: x, displayY: y });
      this.stage = 3;
      await ctrl.touchUp({ id: 0, displayId: 0, displayX: x, displayY: y });
      this.success++;
      this.stage = 0;
    } catch (e) {
      const err = e as BusinessError;
      this.errCode = typeof err.code === 'number' ? err.code : -1;
    }
    this.elapsedUs = (Date.now() - t0) * 1000;
    return {
      syscap: this.syscap,
      ok: this.stage === 0 && this.errCode === 0 ? 1 : 0,
      errCode: this.errCode,
      stage: this.stage,
      elapsedUs: this.elapsedUs,
      attempts: this.attempts,
      success: this.success
    };
  }
}
