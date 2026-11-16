/**
 * 剪贴板浮层的窗口形态。
 *
 * 目标（2026-11-11 21:56 用户反馈「过渡动画太长」/「过度的界面太多」）：
 * 让这一下「取剪贴板」看起来像**系统弹了个小框**，而不是「切到另一个应用」。
 *
 * 走过的死路（记录下来，别再试）：
 *   · `win.resize()` —— 文档写死「只在 FLOATING 窗口模式下生效，否则 1300002」，
 *     而平板上应用**无法**把自己的主窗口切到浮动模式（没有 setWindowMode 这类 API，
 *     那要用户手势或 2in1 设备形态）。实测两次都是 1300002，窗口保持全屏。
 *   · `setWindowTransitionAnimation()` —— 只有 DESTROY（窗口销毁）场景，管不到窗口出现。
 *   · `aa start -W --wl <left> --wt <top> --ww <width> --wh <height>` ——
 *     2026-11-11 22:14 真机实测：窗口依然全屏，参数完全不生效（usage 里 `-s` 已注明
 *     only for FA mode，Stage 模型不吃这套）。想靠命令行指定窗口尺寸是死路。
 *   · 改系统动画缩放 —— 设备上没有 `settings` 命令，`persist.sys.window_animation`
 *     参数不存在（errNum 1002），只能靠用户在开发者选项里手动改。
 *
 * 所以分两层：
 *   ① **默认全屏透明**（本文件）：窗口仍是全屏，但背景与页面根节点都透明，
 *      底下透出用户原来的应用，屏幕上多出来的只有中间那张小卡片。
 *   ② **可拖成小浮窗**（module.json5 的 supportWindowMode 声明 floating）：
 *      系统「应用切换」的左右滑是应用侧关不掉的，唯一治本是让窗口本身变小 ——
 *      用户把它拖成角落小浮窗后，`aa start` 只是把小窗提到前台，滑动范围就是小窗那么大。
 */
import { window, display, floatView } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { common, abilityAccessCtrl, PermissionRequestResult } from '@kit.AbilityKit';

const DOMAIN: number = 0x0000;
const TAG: string = 'HarmLinkWin';

// 子窗口句柄。同一 HAP 内是 ES 模块单例，EntryAbility / Index / ClipPage 拿到的是同一份。
let clipSubWindow: window.Window | null = null;
// 浮窗（floatView）控制器句柄，同样进程内单例。
let clipFloatCtrl: floatView.FloatViewController | null = null;

/**
 * 卡片当前跑在哪种容器里，供 ClipPage 决定「铺满」还是「页内卡片」：
 *   'float' = floatView 浮窗（系统管的辅助窗口，能停在别的应用之上，用户 22:38 要的形态）
 *   'sub'   = 自建子窗口（floatView 拿不到权限时的降级）
 *   'full'  = 整页兜底
 */
function markSurface(kind: string): void {
  AppStorage.setOrCreate<string>('clipSurface', kind);
}

/**
 * 告诉卡片页「容器位置已经定了，现在量出来的坐标是屏幕绝对值，可以放心上报」。
 *
 * 2026-11-12 01:28 新增。浮窗是唯一一个位置由系统异步摆放的容器：刚 STARTED 时
 * getRectangleById 的 screenOffset 只返回窗口内偏移（实测 143,143 = 285/2），
 * PC 拿它点必空 —— 用户报的「第一次点不动、第二次才成功」。
 * 这里按「主窗口退后台 + 浮窗落定」之后才放行；子窗口/主窗口小窗形态本来就同步，
 * 延时一点点让页面完成首帧即可。
 */
function bumpSettled(delayMs: number): void {
  setTimeout((): void => {
    const n: number = (AppStorage.get<number>('clipSettled') ?? 0) + 1;
    AppStorage.setOrCreate<number>('clipSettled', n);
    hilog.info(DOMAIN, TAG, 'clip card settled, coords are screen-absolute, seq=%{public}d', n);
  }, delayMs);
}

/** 设备/系统是否支持 floatView（API 26 起，需 SystemCapability.Window.SessionManager）。 */
export function isFloatViewCapable(): boolean {
  try {
    return floatView.isFloatViewEnabled();
  } catch (e) {
    hilog.warn(DOMAIN, TAG, 'isFloatViewEnabled threw: %{public}s', String(e));
    return false;
  }
}

/**
 * 开一张真正的「悬浮在当前界面之上」的卡片。
 *
 * 这是用户 2026-11-11 22:38 明确要求的形态：**不要换界面，就在当前应用上面弹**。
 * floatView 是系统提供的辅助窗口，官方文档写明「主窗口与 ability 退到后台后仍保持前景显示」，
 * 场景举例就是「看股票时不用来回切应用」——正好是这里的诉求。
 *
 * 与子窗口的本质区别：
 *   · 子窗口依附主窗口，主窗口一旦后台，子窗口跟着走 → 只能靠「主窗口别退」来撑，
 *     结果就是 HarmLink 一直占着前台，用户看到的是「切了个应用」。
 *   · floatView 由系统托管，主窗口可以立刻退后台，卡片继续浮在用户原应用上。
 *
 * 权限：start() 需要 ohos.permission.FLOAT_VIEW（user_grant，system_basic 等级）。
 * 拿不到会抛 201，调用方据此降级到子窗口。
 */
export async function openFloatClipCard(ctx: common.Context): Promise<boolean> {
  if (clipFloatCtrl !== null) {
    // 卡片已经开着又收到一次请求：让页面补发一次 READY，否则 PC 只能拿旧坐标点。
    AppStorage.setOrCreate<number>('clipPing', (AppStorage.get<number>('clipPing') ?? 0) + 1);
    // 此时浮窗位置早就定了，补一条 settled 让 PC 立刻拿到可用坐标（不必等 700ms 兜底）
    bumpSettled(60);
    return true;
  }
  try {
    // 浮窗位置由系统摆放，回调可能晚于页面出现 —— 先置 false，等 onRectChange 再放行坐标上报。
    AppStorage.setOrCreate<boolean>('clipRectReady', false);
    const cfg: floatView.FloatViewConfiguration = {
      context: ctx,
      templateType: floatView.FloatViewTemplateType.ROUNDED_RECTANGLE,
      isConfirmOnClose: false
    };
    const ctrl: floatView.FloatViewController = await floatView.create(cfg);
    // 浮窗位置由系统摆放，应用只能被动接收。PC 注入点击用的是屏幕绝对坐标，
    // 所以每次位置变化都写回 AppStorage，页面算坐标时再加上。
    ctrl.onRectChange((info: floatView.FloatViewRectChangeInfo): void => {
      AppStorage.setOrCreate<number>('clipSubLeft', info.windowRect.left);
      AppStorage.setOrCreate<number>('clipSubTop', info.windowRect.top);
      // 页面据此知道「位置已经确定了」，可以放心上报屏幕绝对坐标。
      AppStorage.setOrCreate<boolean>('clipRectReady', true);
    });
    let settle: ((v: boolean) => void) | null = null;
    const started: Promise<boolean> = new Promise<boolean>((resolve: (v: boolean) => void): void => {
      settle = resolve;
    });
    ctrl.onStateChange((info: floatView.FloatViewStateChangeInfo): void => {
      hilog.info(DOMAIN, TAG, 'float view state=%{public}d stopReason=%{public}s',
        info.state, info.stopReason);
      if (info.state === floatView.FloatViewState.STOPPED) {
        // 用户点了浮窗自带的关闭键，或系统回收了它：句柄必须清掉，
        // 否则下一次取剪贴板会误判「卡片还开着」，只补发 PING 而不再建窗，PC 直接点空。
        if (clipFloatCtrl === ctrl) {
          clipFloatCtrl = null;
          markSurface('none');
          // 用户点浮窗自带的 ×、或系统回收：页面标记一并归位，否则下一次请求会被误判「已开着」。
          AppStorage.setOrCreate<string>('harmlink.page', 'index');
        }
      }
      if (settle === null) {
        return;
      }
      if (info.state === floatView.FloatViewState.STARTED) {
        settle(true);
        settle = null;
      } else if (info.state === floatView.FloatViewState.STOPPED) {
        settle(false);
        settle = null;
      }
    });
    // 2026-11-12 01:20 修「点下载没复制、反而弹出整屏窗口」：
    //   ① `clipRectReady` 是**跨窗口共享**的 AppStorage 标记，上一个浮窗置过 true 后没人清。
    //      于是新浮窗里页面 onPageShow 一上来就读到 true，门闩形同虚设，立刻用「浮窗尚未定位」
    //      的坐标广播 READY。PC 拿这个偏掉的坐标去点，正好落在还没退后台的主窗口上 ——
    //      既没点到 PasteButton（没取到内容），还把整屏 HarmLink 带到前台（用户看到的"新窗口"）。
    //      必须在加载页面前把它压回 false，等真正的 onRectChange 再放开。
    //   ② `markSurface` 原来排在 setUIContext 之后，页面 aboutToAppear 读到的还是上一次的
    //      clipSurface，图标/文字的判定会漂移。一并提前。
    AppStorage.setOrCreate<boolean>('clipRectReady', false);
    markSurface('float');
    await ctrl.setUIContext('pages/ClipPage');
    // 尺寸：2026-11-12 00:36 用户要求「尽可能小」，于是不再按 vp 设计值开窗，
    // 直接取系统给的**下限** `FloatViewLimits.minSize` —— 那是这个模板允许的最小外框，
    // 比它更小会被 setWindowSize 拒掉或回弹。取不到下限（各 ROM 可能全 0）才退回经验值。
    // 注意外框含系统标题栏（应用关不掉，`isConfirmOnClose` 只管关闭确认框），
    // 所以内容可用高度 ≈ 外框高 − 标题栏，卡片内容本身是「一行说明 + 粘贴键 + 一行状态」。
    const d: display.Display = display.getDefaultDisplaySync();
    const density: number = d.densityPixels > 0 ? d.densityPixels : 1;
    const lim: floatView.FloatViewLimits =
      floatView.getFloatViewLimits(floatView.FloatViewTemplateType.ROUNDED_RECTANGLE);
    let w: number = Math.round(120 * density);
    let h: number = Math.round(120 * density);
    // 系统下限实测 160x90px（≈68x38vp），那个尺寸连「粘贴」键都放不下 —— 卡片会被裁成一条。
    // 所以取 max(系统下限, 内容需要的最小值)：既要「尽可能小」，又要能看清按钮。
    if (lim.minSize.width > w) {
      w = lim.minSize.width;
    }
    if (lim.minSize.height > h) {
      h = lim.minSize.height;
    }
    if (lim.maxSize.width > 0 && w > lim.maxSize.width) {
      w = lim.maxSize.width;
    }
    if (lim.maxSize.height > 0 && h > lim.maxSize.height) {
      h = lim.maxSize.height;
    }
    hilog.info(DOMAIN, TAG,
      'float limits min=%{public}dx%{public}d max=%{public}dx%{public}d ratios=%{public}d -> use %{public}dx%{public}d',
      lim.minSize.width, lim.minSize.height, lim.maxSize.width, lim.maxSize.height,
      lim.ratioLimits.length, w, h);
    await ctrl.setWindowSize({ width: w, height: h });
    markSurface('float');
    await ctrl.start();
    // start() 返回不代表已经起来，要等 STARTED 回调；给 2.5s 上限，避免卡死整条链路。
    const ok: boolean = await new Promise<boolean>((resolve: (v: boolean) => void): void => {
      let done: boolean = false;
      started.then((v: boolean): void => {
        if (!done) {
          done = true;
          resolve(v);
        }
      }).catch((): void => {});
      setTimeout((): void => {
        if (!done) {
          done = true;
          resolve(false);
        }
      }, 2500);
    });
    if (!ok) {
      hilog.warn(DOMAIN, TAG, 'float view did not reach STARTED');
      markSurface('none');
      try {
        await ctrl.stop();
      } catch (e) {
        // 没起来就停不掉，忽略
      }
      return false;
    }
    clipFloatCtrl = ctrl;
    // 2026-11-12 01:28 用户报「每次要点两次，第一次不行」：这里原先立刻把 clipRectReady 置 true，
    // 而此刻浮窗**还没被系统摆位置**，页面 getRectangleById 的 screenOffset 只返回窗口内偏移
    // （实测 143,143 = 285/2），PC 拿它去点必然落空。改成延时兜底：先等 onRectChange，
    // 600ms 还没到才放行（此时位置一定已经定下来，量出来的是真屏幕坐标）。
    setTimeout((): void => {
      if (AppStorage.get<boolean>('clipRectReady') !== true) {
        AppStorage.setOrCreate<boolean>('clipRectReady', true);
        hilog.info(DOMAIN, TAG, 'rect fallback release after 600ms');
      }
    }, 600);
    // 2026-11-12 00:23 真机踩到双层卡片：并发两条流程时，抢不到权限的那条先降级建了子窗口，
    // 拿到权限的这条随后建浮窗，两张卡就叠在一起（截图实测：浮窗 800x594 压着子窗口 808x594）。
    // 浮窗已经是最终形态，这里把残留子窗口收掉。
    const leftover: window.Window | null = clipSubWindow;
    clipSubWindow = null;
    if (leftover !== null) {
      leftover.destroyWindow().then((): void => {
        hilog.info(DOMAIN, TAG, 'stale sub window destroyed after float view started');
      }).catch((e: BusinessError): void => {
        hilog.warn(DOMAIN, TAG, 'destroy stale sub window failed: %{public}s', JSON.stringify(e));
      });
    }
    hilog.info(DOMAIN, TAG, 'float clip card ready %{public}dx%{public}d', w, h);
    return true;
  } catch (e) {
    const err = e as BusinessError;
    hilog.warn(DOMAIN, TAG, 'float view failed code=%{public}d msg=%{public}s',
      err.code, err.message);
    markSurface('none');
    return false;
  }
}

/**
 * 主窗口若已处于浮动小窗（用户拖过一次，或系统记住了上次形态），
 * 就把它调整成合适的卡片尺寸并返回 true —— 此时**主窗口本身就是那张卡片**，
 * 不需要子窗口、不需要浮窗权限，屏幕上也只有这一小块。
 *
 * 为什么这里能 resize 而全屏时不能：文档写死 `resize()` 只在 FLOATING 模式生效，否则 1300002。
 * 判据用窗口矩形宽度 < 屏幕宽度的 90% —— 全屏窗口不可能比屏窄。
 *
 * 2026-11-11 22:55 修：这段原先只在 EntryAbility 的冷启动路径里跑，热启动
 * （`aa start --ps clip 1` 命中已在运行的进程 → onNewWant → Index.openClipPage）完全绕过它，
 * 于是又去建子窗口，而子窗口会被窄小的主窗口裁掉（真机实测只剩一条竖条）。
 * 现在沉到 ClipWindow，两条路径共用。
 */
export async function prepareSmallWindow(stage: window.WindowStage): Promise<boolean> {
  let win: window.Window;
  try {
    win = stage.getMainWindowSync();
  } catch (e) {
    return false;
  }
  try {
    const d: display.Display = display.getDefaultDisplaySync();
    const p: window.WindowProperties = win.getWindowProperties();
    const r: window.Rect = p.windowRect;
    hilog.info(DOMAIN, TAG, 'main window rect=%{public}d,%{public}d %{public}dx%{public}d screen=%{public}dx%{public}d',
      r.left, r.top, r.width, r.height, d.width, d.height);
    if (r.width <= 0 || r.width >= d.width * 0.9) {
      return false;
    }
    // 2026-11-11 23:01 真机实测：`resize()` / `moveWindowTo()` 在这台平板的「轻量级浮窗」上
    // **静默无效** —— 两个 await 都正常 resolve、不抛 1300002，窗口矩形却一点没变
    // （日志 `rect=1299,121 815x1448` → `resized to 815x1448`）。
    // SDK 文档写明 resize「仅在自由窗口（freeform）状态下生效」，轻量级浮窗不属于 freeform。
    // 所以不再尝试改尺寸：窗口多大就多大，页面铺满即可 —— 用户拖成合适大小后系统会记住。
    hilog.info(DOMAIN, TAG,
      'small window detected %{public}dx%{public}d (target 320x200vp, resize unsupported by ROM)',
      r.width, r.height);
    // 底色透明：卡片的白底与圆角由页面自己画，窗口只提供系统阴影。
    try {
      win.setWindowBackgroundColor('#00000000');
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'small window bg transparent failed: %{public}s', JSON.stringify(e));
    }
    // 页面据此知道窗口已就位（坐标本身由 ClipPage 用 screenOffset 现取，不吃缓存）。
    AppStorage.setOrCreate<boolean>('clipRectReady', true);
    return true;
  } catch (e) {
    const err = e as BusinessError;
    hilog.warn(DOMAIN, TAG, 'prepareSmallWindow failed code=%{public}d msg=%{public}s',
      err.code, err.message);
    return false;
  }
}

/**
 * 查/申请 ohos.permission.FLOAT_VIEW（floatView 闪控窗的唯一前提）。
 *
 * 2026-11-12 00:17 起这条权限真的可用了：DevEco「ACL 权限配置」已把它写进签名 profile
 * 的顶层 acls（`"acls":{"allowed-acls":["ohos.permission.FLOAT_VIEW"]}`）。
 * 该权限 grantMode=user_grant，所以**必须运行时申请**：首次会弹系统授权框，
 * 用户点「允许」后 checkAccessTokenSync 才会返回 GRANTED。
 *
 * 失败路径要留意：ACL 没生效时 request 会抛 201，此时静默返回 false 降级到子窗口，
 * 绝不能反复弹框。
 */
export async function ensureFloatPermission(ctx: common.UIAbilityContext): Promise<boolean> {
  try {
    const mgr: abilityAccessCtrl.AtManager = abilityAccessCtrl.createAtManager();
    const tokenId: number = ctx.applicationInfo.accessTokenId;
    const status: abilityAccessCtrl.GrantStatus =
      mgr.checkAccessTokenSync(tokenId, 'ohos.permission.FLOAT_VIEW');
    if (status === abilityAccessCtrl.GrantStatus.PERMISSION_GRANTED) {
      hilog.info(DOMAIN, TAG, 'FLOAT_VIEW already granted');
      return true;
    }
    const res: PermissionRequestResult =
      await mgr.requestPermissionsFromUser(ctx, ['ohos.permission.FLOAT_VIEW']);
    const codes: number[] = res.authResults;
    const ok: boolean = codes.length > 0 && codes[0] === 0;
    hilog.info(DOMAIN, TAG, 'FLOAT_VIEW request result=%{public}s ok=%{public}s',
      JSON.stringify(codes), `${ok}`);
    return ok;
  } catch (e) {
    const err = e as BusinessError;
    hilog.warn(DOMAIN, TAG, 'FLOAT_VIEW request failed code=%{public}d msg=%{public}s',
      err.code, err.message);
    return false;
  }
}


export function clipCardOrigin(): Array<number> {
  return [
    AppStorage.get<number>('clipSubLeft') ?? 0,
    AppStorage.get<number>('clipSubTop') ?? 0
  ];
}

/**
 * 开一张「真弹窗」：应用自己创建的、带尺寸的子窗口。
 *
 * 为什么不是把主窗口缩小：
 *   `AbilityConstant.WindowMode` 只有 FULLSCREEN(1) / SPLIT_PRIMARY(100) / SPLIT_SECONDARY(101)
 *   / SPLIT(105)，**没有 FLOATING**；Stage 模型也没有 `setWindowMode` 这类 API；
 *   `resize()` 又只在已处于浮动模式的窗口上生效。所以「主窗口自己变成小浮窗」是平台做不到的，
 *   靠用户手动拖才行 —— 用户 2026-11-11 22:22 明确否掉了「让用户手动拖」。
 *
 * 子窗口是另一回事：`createSubWindowWithOptions({ windowRect })` 由应用自己创建、尺寸位置自定，
 * 不需要任何用户手势。设备侧 syscap 已确认支持 `SystemCapability.Window.SessionManager`。
 * 主窗口保持透明全屏（见 applyClipPopupMode），屏幕上真正可见的只有这张子窗口。
 *
 * @returns true = 子窗口已起来；false = 设备不支持，调用方应退回「透明全屏 + 页内卡片」。
 */
export async function openClipCard(stage: window.WindowStage, ctx?: common.Context): Promise<boolean> {
  if (clipFloatCtrl !== null) {
    // 浮窗已经开着（上一轮没点、或连点两次）：同样要让页面补发一次 READY。
    AppStorage.setOrCreate<number>('clipPing', (AppStorage.get<number>('clipPing') ?? 0) + 1);
    bumpSettled(60);
    return true;
  }
  if (clipSubWindow !== null) {
    // 卡片已经开着（上一轮用户没点、或者连点两次）：不能直接返回 true 就算完 ——
    // ClipPage 的 onPageShow 不会再跑，PC 就收不到 READY，只能拿旧坐标去点。
    // 用一个计数器让页面自己补发一次 READY。
    AppStorage.setOrCreate<number>('clipPing', (AppStorage.get<number>('clipPing') ?? 0) + 1);
    return true;
  }
  // ① 首选：floatView —— 卡片浮在用户当前应用之上，主窗口可以立刻退后台（用户 22:38 的要求）。
  //    拿不到 FLOAT_VIEW 权限（system_basic，apl=normal 有风险）时往下走到子窗口。
  if (ctx !== undefined && isFloatViewCapable()) {
    const floated: boolean = await openFloatClipCard(ctx);
    if (floated) {
      return true;
    }
    hilog.warn(DOMAIN, TAG, 'float view unavailable, fall back to sub window');
  }
  try {
    const d: display.Display = display.getDefaultDisplaySync();
    const density: number = d.densityPixels > 0 ? d.densityPixels : 1;
    // 目标尺寸用 vp 思考、换算成 px 交给窗口系统（windowRect 单位是 px）
    // 2026-11-12 00:36 用户要求封窗尽可能小，子窗口这条降级路也跟着收窄；
    // 子窗口没有系统标题栏，内容只需要「一行说明 + 粘贴键 + 一行状态」。
    let w: number = Math.round(240 * density);
    const wMax: number = Math.round(d.width * 0.92);
    if (w > wMax) {
      w = wMax;
    }
    const h: number = Math.round(170 * density);
    const left: number = Math.round((d.width - w) / 2);
    // 略偏上：手里拿平板时中上部最容易看，也不挡输入法候选区
    let top: number = Math.round(d.height * 0.36);
    if (top + h > d.height) {
      top = Math.max(0, d.height - h - Math.round(24 * density));
    }

    const sub: window.Window = await stage.createSubWindowWithOptions('clipCard', {
      title: 'HarmLink',
      decorEnabled: false,
      isModal: false,
      windowRect: { left: left, top: top, width: w, height: h }
    });
    // 子窗口也要透明：卡片自己的圆角与投影靠页面画，窗口底色若有就会露出方角
    try {
      sub.setWindowBackgroundColor('#00000000');
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'sub window bg transparent failed: %{public}s', JSON.stringify(e));
    }
    AppStorage.setOrCreate<number>('clipSubLeft', left);
    AppStorage.setOrCreate<number>('clipSubTop', top);
    AppStorage.setOrCreate<boolean>('clipRectReady', true);
    // 必须在 loadContent 之前标记：ClipPage 的 aboutToAppear 要据此决定铺满还是页内卡片。
    markSurface('sub');
    await sub.loadContent('pages/ClipPage');
    await sub.showWindow();
    clipSubWindow = sub;
    hilog.info(DOMAIN, TAG, 'clip card sub window ready %{public}dx%{public}d at %{public}d,%{public}d',
      w, h, left, top);
    return true;
  } catch (e) {
    hilog.warn(DOMAIN, TAG, 'createSubWindowWithOptions failed: %{public}s', JSON.stringify(e));
    markSurface('none');
    return false;
  }
}

/**
 * 对外唯一入口：开卡片 + 按容器形态决定要不要把 HarmLink 主窗口退到后台。
 *
 * 退后台这一步只在 floatView 路径做：卡片由系统托管，主窗口退后台后它仍留在用户应用之上。
 * 子窗口依附主窗口，主窗口一退后台卡片就跟着消失，所以那条路径必须保持前台。
 */
/**
 * 同一时刻只允许一条建卡流程在跑。
 *
 * 2026-11-12 00:23 真机实测的成因：冷启动的 `startClipCard` 和 `onNewWant` 触发的
 * `Index.openClipPage` 会**并发**进入这里，各自 await `ensureFloatPermission`。
 * 系统不允许并发申请同一条 user_grant 权限 —— 后到那条拿到拒绝，于是降级建子窗口；
 * 先到那条拿到授权，再建浮窗。结果是屏幕上叠了两张卡。
 * 用一条共享 Promise 兜住：后来者直接复用正在进行的结果。
 */
let flowBusy: Promise<boolean> | null = null;

export async function showClipCardFlow(stage: window.WindowStage, ctx?: common.Context): Promise<boolean> {
  const running: Promise<boolean> | null = flowBusy;
  if (running !== null) {
    hilog.info(DOMAIN, TAG, 'clip card flow already running, join it');
    return running;
  }
  const p: Promise<boolean> = runClipCardFlow(stage, ctx);
  flowBusy = p;
  p.then((): void => { flowBusy = null; }).catch((): void => { flowBusy = null; });
  return p;
}

async function runClipCardFlow(stage: window.WindowStage, ctx?: common.Context): Promise<boolean> {
  // ⓪ 已有 FLOAT_VIEW 授权上下文时，直接走 openClipCard —— 它会先试真正的跨应用浮窗，
  //    那才是「在当前界面弹窗」。浮窗不成再降级子窗口。
  //    没权限上下文时才看主窗口是不是浮动小窗（它本身就是卡片，省一次建窗）。
  if (ctx === undefined && await prepareSmallWindow(stage)) {
    markSurface('win');
    // 主窗口本身就是小窗，位置是同步确定的，让页面缓一拍量好尺寸再上报
    bumpSettled(250);
    return true;
  }
  const ok: boolean = await openClipCard(stage, ctx);
  if (!ok) {
    return false;
  }
  if (AppStorage.get<string>('clipSurface') === 'float' && ctx !== undefined) {
    // 给系统一点时间把浮窗真正合成到屏幕上，再退后台 —— 否则可能出现
    // 「主窗口先退、浮窗还没上屏」的空窗一瞬。
    setTimeout((): void => {
      const abilityCtx = ctx as common.UIAbilityContext;
      abilityCtx.moveAbilityToBackground()
        .then((): void => {
          hilog.info(DOMAIN, TAG, 'main ability moved to background, float card stays');
          // 2026-11-12 01:28 主窗口已退后台、浮窗位置也定了，此刻量出来的是屏幕绝对坐标。
          // 让页面发一条带 settled 标记的 READY —— PC 端只认这一条去点击。
          bumpSettled(0);
          // 2026-11-12 00:53 用户报「多任务里有两个 HarmLink」：一个是主窗口、一个是浮窗。
          // `excludeFromMissions` 官方标注 "For system application only"，普通应用改不了清单，
          // 唯一能试的办法是把主窗口彻底销毁 —— 任务里没有窗口，多任务面板里就不该再有第二张卡。
          // 浮窗是否跟着一起没，取决于系统实现，这里只做一次尝试并留日志，失败不影响主流程。
          try {
            stage.getMainWindowSync().destroyWindow()
              .then((): void => {
                hilog.info(DOMAIN, TAG, 'main window destroyed, recents should keep only float card');
              })
              .catch((e: BusinessError): void => {
                hilog.warn(DOMAIN, TAG, 'destroy main window failed code=%{public}d', e.code);
              });
          } catch (e) {
            hilog.warn(DOMAIN, TAG, 'getMainWindowSync for destroy failed: %{public}s', String(e));
          }
        })
        .catch((e: BusinessError): void => {
          hilog.warn(DOMAIN, TAG, 'moveAbilityToBackground failed code=%{public}d', e.code);
          // 退后台失败也要放行，否则页面永远发不出 settled，PC 只能等到超时
          bumpSettled(0);
        });
    }, 120);
  } else {
    // 子窗口 / 整页兜底：容器位置是同步给的，让页面缓一拍量完首帧再放行
    bumpSettled(250);
  }
  return true;
}

/** 卡片此刻是否真的存在（浮窗或子窗口任一在）。给 EntryAbility 判断「已开着」用。 */
export function isClipCardAlive(): boolean {
  return clipFloatCtrl !== null || clipSubWindow !== null;
}

/** 收掉卡片。两种容器都要能收：先看 floatView，再退子窗口。 */
export async function closeClipCard(): Promise<void> {
  // 2026-11-12 00:41 修「点一次之后第二次失效」：浮窗 stop 掉时，浮窗里那页的
  // `aboutToDisappear` 不一定跑，`harmlink.page` 就一直卡在 'clip'，
  // 下一次 `--ps clip 1` 于是走进「已开着，只补发 READY」的分支 ——
  // 可卡片根本没监听者，请求静默丢掉。这里开始收卡片就先把标记归位，
  // 再配合 EntryAbility 里 isClipCardAlive() 的二次确认。
  AppStorage.setOrCreate<string>('harmlink.page', 'index');
  const f: floatView.FloatViewController | null = clipFloatCtrl;
  if (f !== null) {
    clipFloatCtrl = null;
    try {
      await f.stop();
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'stop float view failed: %{public}s', JSON.stringify(e));
    }
    markSurface('none');
    return;
  }
  const w: window.Window | null = clipSubWindow;
  clipSubWindow = null;
  markSurface('none');
  if (w === null) {
    return;
  }
  try {
    await w.destroyWindow();
  } catch (e) {
    hilog.warn(DOMAIN, TAG, 'destroy sub window failed: %{public}s', JSON.stringify(e));
  }
}

/**
 * 把窗口调成「透明外壳」，只让页面自己画的那张卡片可见。
 * 每一步独立 try：任何一步不被 ROM 支持都不该拖垮其余步骤 —— 尤其背景透明这一条，
 * 它是整个观感的关键，不能因为前面的圆角/装饰调用失败而一起丢。
 */
export async function applyClipPopupMode(win: window.Window): Promise<void> {
  // ① 窗口底色透明（最关键的一步）
  try {
    win.setWindowBackgroundColor('#00000000');
    hilog.info(DOMAIN, TAG, 'window background set transparent');
  } catch (e) {
    hilog.warn(DOMAIN, TAG, 'setWindowBackgroundColor failed: %{public}s', JSON.stringify(e));
  }
  // ② 全屏布局，让页面铺满，避免顶部留出系统装饰区
  try {
    await win.setWindowLayoutFullScreen(true);
  } catch (e) {
    hilog.warn(DOMAIN, TAG, 'setWindowLayoutFullScreen failed: %{public}s', JSON.stringify(e));
  }
  // ③ 隐藏装饰（标题栏），弹窗不该有窗口边框
  try {
    await win.setWindowDecorVisible(false);
  } catch (e) {
    // 不支持就算了，不影响透明
  }
  // ④ 去掉系统给窗口加的圆角与阴影：全屏透明层上有圆角会露出四角黑边
  try {
    await win.setWindowCornerRadius(0);
  } catch (e) {
    // 同上
  }
  hilog.info(DOMAIN, TAG, 'clip overlay mode ready');
}

/** 恢复成正常窗口（用户手动打开 App 时用）。 */
export async function restoreFullWindow(win: window.Window): Promise<void> {
  try {
    win.setWindowBackgroundColor('#00000000');
  } catch (e) {
    // 首页自己画渐变背景，窗口底色透明是对的
  }
  try {
    await win.setWindowLayoutFullScreen(false);
  } catch (e) {
    // 忽略
  }
  hilog.info(DOMAIN, TAG, 'restore normal window');
}
