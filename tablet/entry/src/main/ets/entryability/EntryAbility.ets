import { AbilityConstant, UIAbility, Want } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { window } from '@kit.ArkUI';
import { BusinessError } from '@kit.BasicServicesKit';
import { abilityAccessCtrl, common, Permissions, PermissionRequestResult } from '@kit.AbilityKit';
import { applyClipPopupMode, showClipCardFlow, ensureFloatPermission, isClipCardAlive } from '../common/ClipWindow';

const DOMAIN: number = 0x0000;
const TAG: string = 'HarmLink';

// 「取剪贴板」指令的存储键。
// PC 端跑 `aa start -a EntryAbility -b com.harmos.mirror --ps clip 1` 时，
// --ps 的键值会落进 want.parameters；本机把它取出来交给 Index 页做一次跳转。
// 用 AppStorage 而不是直接 loadContent：Ability 里拿不到 router，
// 且热启动（onNewWant）时页面栈已经存在，只能靠页面自己监听。
const KEY_CLIP_REQ: string = 'clipReq';
// 让已打开的剪贴板卡片补发一次 READY（ClipPage 的 @StorageLink('clipPing') 监听它）。
const KEY_CLIP_PING: string = 'clipPing';
const KEY_PROBE_REQ: string = 'probeReq';
// `--ps stream 1`：跳会话页并自动起采（手机走自有 HAP 采集时由 PC 拉起）。
// 同时支持三个可选的端点覆盖参数，用于双路并行时把这一路挪到 9011/9021：
//   --ps peer <PC IP>  --ps peerport 9011  --ps localport 9021
const KEY_STREAM_REQ: string = 'streamReq';
// `--ps extload 1`：在自有 HAP 里 dlopen 扩展库并调 UiTestExtension_OnInit。
// 用途：手机自带 uitest 不触发扩展初始化（补记 73），改由应用进程自己驱动同一份 so。
// 只跳到调试页，不碰采集链路。
const KEY_EXTLOAD_REQ: string = 'extloadReq';
const KEY_CUR_PAGE: string = 'harmlink.page';
// 本次启动是「PC 请求取剪贴板」拉起的一次性标记。
// 用途：冷启动时 onWindowStageCreate 直接 loadContent 到 ClipPage，跳过首页 ——
// 否则用户会先看到首页闪一下再被推入浮层，平白多出一整段动画（2026-11-11 用户反馈「动画时间太长」）。
const KEY_CLIP_AUTO: string = 'clipAuto';
// 子窗口建不出来时的降级标记：ClipPage 据此知道自己是「全屏页」而不是「子窗口内容」，
// 退出时要走 replaceUrl 回首页，否则主窗口会停在透明空页上。
const KEY_CLIP_FALLBACK: string = 'clipFallback';

// 屏幕采集相关的开放权限（normal / user_grant，平板与手机均支持）。
// 目标是让系统记住一次授权：如果 ROM 把 AVScreenCapture 的隐私弹窗挂在这条权限上，
// 授权后就不再弹；若 ROM 不认，至少不影响业务（下面全部 try/catch 吞掉）。
// 注意：CUSTOM_SCREEN_RECORDING 是 system_basic 且 deviceTypes 仅 2in1/tv，平板拿不到，
// 不要再往那个方向试（见 AGENTS facts 里的权限硬结论）。
const SCREEN_PERMISSIONS: Permissions[] = [
  'ohos.permission.CUSTOM_SCREEN_CAPTURE'
];

export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(DOMAIN, TAG, 'Ability onCreate');
    this.ensureScreenPermissions();
    this.markClipAuto(want);
    this.handleClipWant(want);
    this.handleProbeWant(want);
    this.handleStreamWant(want);
    this.handleExtLoadWant(want);
  }

  // 热启动（应用已在运行）不会走 onCreate，只走 onNewWant。
  onNewWant(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(DOMAIN, TAG, 'Ability onNewWant');
    this.markClipAuto(want);
    this.handleClipWant(want);
    this.handleProbeWant(want);
    this.handleStreamWant(want);
    this.handleExtLoadWant(want);
  }

  // 解析 --ps extload 1，跳输入注入自测页做扩展库加载实验。
  // 传 --ps so <绝对路径> 可覆盖默认的 so 路径（调试用）。
  private handleExtLoadWant(want: Want): void {
    if (!EntryAbility.wantFlag(want, 'extload')) {
      return;
    }
    const soPath: string | null = EntryAbility.wantString(want, 'so');
    if (soPath !== null && soPath.length > 0) {
      AppStorage.setOrCreate<string>('extLoadSoPath', soPath);
    }
    const n: number = (AppStorage.get<number>(KEY_EXTLOAD_REQ) ?? 0) + 1;
    AppStorage.setOrCreate<number>(KEY_EXTLOAD_REQ, n);
    hilog.info(DOMAIN, TAG, 'extload request accepted, seq=%{public}d so=%{public}s',
      n, soPath ?? '-');
  }

  // 冷启动时先把「这次是来取剪贴板的」记下来，供 onWindowStageCreate 决定首屏。
  // onWindowStageCreate 拿不到当次 want，只能靠 AppStorage 传递。
  private markClipAuto(want: Want): void {
    if (EntryAbility.wantClip(want)) {
      AppStorage.setOrCreate<boolean>(KEY_CLIP_AUTO, true);
    }
  }

  // 解析 --ps probe 1，跳到输入注入自测页（调试用，见 pages/InputProbePage.ets）。
  // 单独一个键，不和 clip 混：两者的目标页与副作用完全不同。
  private handleProbeWant(want: Want): void {
    if (!EntryAbility.wantFlag(want, 'probe')) {
      return;
    }
    const cur: string = AppStorage.get<string>(KEY_CUR_PAGE) ?? '';
    if (cur === 'inputprobe') {
      hilog.info(DOMAIN, TAG, 'probe request: already on probe page');
      return;
    }
    const n: number = (AppStorage.get<number>(KEY_PROBE_REQ) ?? 0) + 1;
    AppStorage.setOrCreate<number>(KEY_PROBE_REQ, n);
    hilog.info(DOMAIN, TAG, 'probe request accepted, seq=%{public}d', n);
  }

  // 解析 --ps clip 1，要求页面跳一次剪贴板页。
  // 已经停在剪贴板页时不重复跳 —— 那会往路由栈里堆一个重复实例，
  // 用户返回一次还停在剪贴板页，看着像没反应。
  private handleClipWant(want: Want): void {
    if (!EntryAbility.wantClip(want)) {
      return;
    }
    const cur: string = AppStorage.get<string>(KEY_CUR_PAGE) ?? '';
    if (cur === 'clip') {
      // 2026-11-12 00:41 修「点一次之后第二次失效」：光看页面标记不够 ——
      // 浮窗被 stop 时页面可能没跑 aboutToDisappear，标记停在 'clip'，
      // 而这里只补发 PING，卡片早已不存在，请求静默丢失。
      // 所以再加一道实体确认：真有一张卡片存活才走 PING，否则当成全新请求重建。
      if (!isClipCardAlive()) {
        hilog.info(DOMAIN, TAG, 'clip request: page marked clip but no card alive, rebuild');
        AppStorage.setOrCreate<string>(KEY_CUR_PAGE, '');
      } else {
        // 卡片已经开着（上一轮没点、或者连点两次）。这里**不能直接 return**：
        // 2026-11-11 23:02 真机实测，小窗形态下卡片就是主窗口的页面，早退导致
        // ClipPage 不再广播 READY（PC 日志 `READY -1ms`），PC 只能拿旧坐标点空。
        // 用一个计数器让页面自己补发一次 READY。
        const n: number = (AppStorage.get<number>(KEY_CLIP_PING) ?? 0) + 1;
        AppStorage.setOrCreate<number>(KEY_CLIP_PING, n);
        hilog.info(DOMAIN, TAG, 'clip request: card alive, ping page to re-announce, seq=%{public}d', n);
        return;
      }
    }
    const n: number = (AppStorage.get<number>(KEY_CLIP_REQ) ?? 0) + 1;
    AppStorage.setOrCreate<number>(KEY_CLIP_REQ, n);
    hilog.info(DOMAIN, TAG, 'clip request accepted, seq=%{public}d', n);
  }

  // 解析 --ps stream 1：跳到会话页并自动开始采集。
  // 附带的可选端点参数写进 AppStorage，由 UdpTransport.start() 读取（缺省用常量 9020→9010）。
  private handleStreamWant(want: Want): void {
    if (!EntryAbility.wantFlag(want, 'stream')) {
      return;
    }
    const peer: string | null = EntryAbility.wantString(want, 'peer');
    if (peer !== null && peer.length > 0) {
      AppStorage.setOrCreate<string>('streamPeerHost', peer);
    }
    const pp: number = EntryAbility.wantNumber(want, 'peerport');
    if (pp > 0) {
      AppStorage.setOrCreate<number>('streamPeerPort', pp);
    }
    const lp: number = EntryAbility.wantNumber(want, 'localport');
    if (lp > 0) {
      AppStorage.setOrCreate<number>('streamLocalPort', lp);
    }
    // 采集像素预算（--ps budget <像素数>）：默认 200 万，调小换帧率、调大换画质。
    // 做成参数是为了现场试不同档位不必重编译。
    const budget: number = EntryAbility.wantNumber(want, 'budget');
    if (budget > 0) {
      AppStorage.setOrCreate<number>('capPixelBudget', budget);
    }
    const n: number = (AppStorage.get<number>(KEY_STREAM_REQ) ?? 0) + 1;
    AppStorage.setOrCreate<number>(KEY_STREAM_REQ, n);
    hilog.info(DOMAIN, TAG, 'stream request accepted, seq=%{public}d peer=%{public}s pp=%{public}d lp=%{public}d',
      n, peer ?? '-', pp, lp);
  }

  private static wantString(want: Want, key: string): string | null {
    const p: Record<string, Object> | undefined = want.parameters;
    if (p === undefined || p === null) {
      return null;
    }
    const v: Object | undefined = p[key];
    if (v === undefined || v === null) {
      return null;
    }
    return `${v}`;
  }

  private static wantNumber(want: Want, key: string): number {
    const s: string | null = EntryAbility.wantString(want, key);
    if (s === null) {
      return 0;
    }
    const n: number = Number(s);
    return Number.isFinite(n) ? n : 0;
  }

  // --ps 传进来的值类型不稳定（有的 ROM 给 string '1'，有的给 number 1），统一转字符串比较。
  private static wantClip(want: Want): boolean {
    return EntryAbility.wantFlag(want, 'clip');
  }

  // --ps <key> 1 的通用判定，clip / probe 共用（值可能是 '1' / 'true' / 1 / true）。
  private static wantFlag(want: Want, key: string): boolean {
    const p: Record<string, Object> | undefined = want.parameters;
    if (p === undefined || p === null) {
      return false;
    }
    const v: Object | undefined = p[key];
    if (v === undefined || v === null) {
      return false;
    }
    const s: string = `${v}`;
    return s === '1' || s === 'true';
  }

  // 首次启动静默申请；已授权则直接返回（不会重复弹窗）
  private ensureScreenPermissions(): void {
    const ctx = this.context as common.UIAbilityContext;
    try {
      const mgr = abilityAccessCtrl.createAtManager();
      mgr.requestPermissionsFromUser(ctx, SCREEN_PERMISSIONS)
        .then((result: PermissionRequestResult) => {
          const codes: number[] = result.authResults;
          hilog.info(DOMAIN, TAG, 'screen permission authResults=%{public}s', JSON.stringify(codes));
        })
        .catch((e: BusinessError) => {
          hilog.warn(DOMAIN, TAG, 'screen permission request failed code=%{public}d msg=%{public}s',
            e.code, e.message);
        });
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'ensureScreenPermissions threw %{public}s', String(e));
    }
  }

  onDestroy(): void {
    hilog.info(DOMAIN, TAG, 'Ability onDestroy');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    // 冷启动：主窗口永远加载首页。PC 来取剪贴板的那次不再把 ClipPage 当首屏，
    // 而是让首页把自己「隐掉」（见 Index 里对 clipAuto 的处理），再把卡片放进子窗口。
    // 这样主窗口始终只有一份页面，取完不用 replaceUrl 回退，也就没有页面转场。
    const auto: boolean = AppStorage.get<boolean>(KEY_CLIP_AUTO) ?? false;
    windowStage.loadContent('pages/Index', (err: BusinessError) => {
      if (err.code) {
        hilog.error(DOMAIN, TAG, 'loadContent failed: %{public}s', JSON.stringify(err));
        return;
      }
      hilog.info(DOMAIN, TAG, 'loadContent succeeded, auto=%{public}s', `${auto}`);
      if (!auto) {
        return;
      }
      try {
        const win: window.Window = windowStage.getMainWindowSync();
        // 主窗口只当一块透明玻璃；屏幕上真正可见的是浮窗/子窗口里的那张卡片。
        applyClipPopupMode(win).catch((): void => {});
      } catch (e) {
        hilog.warn(DOMAIN, TAG, 'apply clip popup mode failed: %{public}s', String(e));
      }
      this.startClipCard(windowStage);
    });
  }

  /**
   * 冷启动起卡片。形态判定全部下沉到 ClipWindow.showClipCardFlow：
   *   ⓪ 主窗口已是浮动小窗 → 主窗口即卡片（'win'）
   *   ① floatView 浮窗（需 FLOAT_VIEW 权限，apl=normal 拿不到）
   *   ② 自建子窗口（降级）
   *   ③ 都不可用 → 透明全屏 + 页内卡片
   *
   * 2026-11-11 22:55：原先这里自己带一份小窗判定，热启动路径（Index.openClipPage）
   * 绕过它，导致又去建子窗口、被窄主窗口裁成一条竖条。现已统一。
   */
  private startClipCard(windowStage: window.WindowStage): void {
    const ctx = this.context as common.UIAbilityContext;
    ensureFloatPermission(ctx).then((granted: boolean): void => {
      this.dispatchClipCard(windowStage, ctx, granted);
    }).catch((): void => {
      this.dispatchClipCard(windowStage, ctx, false);
    });
  }

  private dispatchClipCard(windowStage: window.WindowStage, ctx: common.UIAbilityContext,
    granted: boolean): void {
    showClipCardFlow(windowStage, granted ? ctx : undefined).then((ok: boolean): void => {
      if (!ok) {
        // 两条容器路都不可用：退回老的「透明全屏 + 页内卡片」，功能不能因此断掉
        hilog.warn(DOMAIN, TAG, 'both float view and sub window unavailable, fallback to page overlay');
        AppStorage.setOrCreate<boolean>(KEY_CLIP_FALLBACK, true);
        windowStage.loadContent('pages/ClipPage').catch((): void => {});
        return;
      }
      if (AppStorage.get<string>('clipSurface') === 'win') {
        // 主窗口本身就是那张小卡片：把页面装进来铺满整窗。
        // 冷启动时这里是根页面，退出靠 ClipPage 的 replaceUrl 回首页。
        AppStorage.setOrCreate<boolean>(KEY_CLIP_FALLBACK, false);
        AppStorage.setOrCreate<boolean>('clipPushed', false);
        windowStage.loadContent('pages/ClipPage').catch((): void => {});
      }
    }).catch((): void => {
      AppStorage.setOrCreate<boolean>(KEY_CLIP_FALLBACK, true);
      windowStage.loadContent('pages/ClipPage').catch((): void => {});
    });
  }

  onWindowStageDestroy(): void {
    hilog.info(DOMAIN, TAG, 'Ability onWindowStageDestroy');
  }

  onForeground(): void {
    hilog.info(DOMAIN, TAG, 'Ability onForeground');
  }

  onBackground(): void {
    hilog.info(DOMAIN, TAG, 'Ability onBackground');
  }
}
