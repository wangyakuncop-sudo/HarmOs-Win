// 设备端首页：GATE-01 息屏采集探针 + 各功能页入口。
// 视觉与 SessionPage 保持同一套卡片语言（头部状态胶囊 → 主控卡 → 入口卡 → 日志卡）。

import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { router, window } from '@kit.ArkUI';
import { restoreFullWindow, showClipCardFlow, ensureFloatPermission } from '../common/ClipWindow';
import { BusinessError } from '@kit.BasicServicesKit';
import { ScreenCaptureProbe } from '../probe/ScreenCaptureProbe';
import { KeepAlive } from '../common/KeepAlive';
import {
  C_BG_TOP, C_BG_BOTTOM, C_CARD, C_TITLE, C_BODY, C_SUB, C_PRIMARY,
  C_DANGER, C_WARN, C_OK, C_NEUTRAL, C_FILL_CHIP, C_FILL_SOFT, C_FILL_BLUE, C_FILL_DARK,
  SHADOW_CARD, SHADOW_CHIP, logColor, timePrefix
} from '../common/Theme';

const MAX_LOG_LINES: number = 60;
const PROBE_DURATION_MS: number = 180000;

@Styles
function indexCard() {
  .width('100%')
  .padding(16)
  .backgroundColor(C_CARD)
  .borderRadius(20)
  .shadow(SHADOW_CARD)
}

@Entry
@Component
struct Index {
  @State statusText: string = '未开始';
  @State running: boolean = false;
  @State logs: string[] = [];
  @State filePath: string = '';
  @State showLogs: boolean = true;
  private probe: ScreenCaptureProbe = new ScreenCaptureProbe();
  private logScroller: Scroller = new Scroller();

  // PC 端「取剪贴板」指令（--ps clip 1 → EntryAbility 写 AppStorage）最终在这里落地：
  // Ability 里没有 router，热启动时页面栈也已经在别处，只能由页面监听后自己跳。
  @StorageLink('clipReq') @Watch('onClipRequest') clipReq: number = 0;
  private clipNavPending: boolean = false;

  // 用 @StorageLink 而不是一次性读取：取完剪贴板后 ClipPage 会把它清回 false，
  // 首页要立刻重新长回来（主窗口全程都是这个页面，没有页面转场可依赖）。
  @StorageLink('clipAuto') clipAuto: boolean = false;

  // 输入注入自测页的拉起点（PC 端 `--ps probe 1`），机制同上但互不干扰。
  @StorageLink('probeReq') @Watch('onProbeRequest') probeReq: number = 0;
  private probeNavPending: boolean = false;

  // PC 端 `--ps stream 1`：直接跳到会话页并自动开始采集。
  // 手机走自有 HAP 采集时（参考采集在它身上不通），PC 启动时用它把手机会话拉起来，
  // 用户只需要在手机上点一次系统「允许录屏」。
  @StorageLink('streamReq') @Watch('onStreamRequest') streamReq: number = 0;
  private streamNavPending: boolean = false;

  // `--ps extload 1`：跳输入注入自测页，验证应用进程能否 dlopen 扩展库（补记 73 的后续探索）。
  // 复用同一张调试页，只在里面加一块「扩展库加载」卡片。
  @StorageLink('extloadReq') @Watch('onExtLoadRequest') extloadReq: number = 0;
  private extLoadNavPending: boolean = false;

  aboutToAppear(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'index');
    hilog.info(0x0000, 'HarmLink',
      'Index aboutToAppear: streamReq=%{public}d clipReq=%{public}d extloadReq=%{public}d',
      this.streamReq, this.clipReq, this.extloadReq);
    // 冷启动时 @Watch 不会对初值触发，必须自己判一次
    if (this.clipReq > 0) {
      this.openClipPage();
    }
    if (this.probeReq > 0) {
      this.openProbePage();
    }
    if (this.streamReq > 0) {
      hilog.info(0x0000, 'HarmLink', 'Index → openSessionPage (cold start)');
      this.openSessionPage();
    }
    if (this.extloadReq > 0) {
      hilog.info(0x0000, 'HarmLink', 'Index → openProbePage (extload)');
      this.openProbePage();
    }
  }

  onPageShow(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'index');
    // 本次是 PC 来取剪贴板（clipAuto 由 EntryAbility 置位）→ 不能在这里恢复全屏，
    // 否则刚缩好的小卡片会被撑回整屏，用户就看见「又切了一次界面」。
    if (AppStorage.get<boolean>('clipAuto') === true) {
      return;
    }
    // 首页出现 = 用户手动在用 App。剪贴板浮层会把窗口缩成小卡片并一直留着，
    // 这里必须恢复整屏，否则下次打开 App 会看到一张小卡片。
    this.ensureFullWindow();
  }

  private async ensureFullWindow(): Promise<void> {
    try {
      const ctx = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const win = await window.getLastWindow(ctx);
      await restoreFullWindow(win);
    } catch (e) {
      // 恢复失败不影响使用，下次冷启动会重建窗口
    }
  }

  onClipRequest(): void {
    if (this.clipReq > 0) {
      this.openClipPage();
    }
  }

  onProbeRequest(): void {
    if (this.probeReq > 0) {
      this.openProbePage();
    }
  }

  onStreamRequest(): void {
    if (this.streamReq > 0) {
      this.openSessionPage();
    }
  }

  onExtLoadRequest(): void {
    if (this.extloadReq > 0) {
      this.openProbePage();
    }
  }

  // 会话页 + 自动起采（PC 端 --ps stream 1）。复用同一个 SessionPage，
  // 只多传一个 autoStart 参数；用户手动点进会话页时行为完全不变。
  private openSessionPage(): void {
    if (this.streamNavPending) {
      return;
    }
    this.streamNavPending = true;
    AppStorage.setOrCreate<number>('streamReq', 0);
    const opts: router.RouterOptions = {
      url: 'pages/SessionPage',
      params: { autoStart: true }
    };
    this.getUIContext().getRouter().pushUrl(opts)
      .then((): void => {
        this.streamNavPending = false;
        hilog.info(0x0000, 'HarmLink', 'pushUrl SessionPage ok');
      })
      .catch((e: BusinessError): void => {
        this.streamNavPending = false;
        hilog.error(0x0000, 'HarmLink', 'pushUrl SessionPage FAILED code=%{public}d msg=%{public}s',
          e.code, e.message);
      });
  }

  // 输入注入自测页：仅调试用，不承载业务。
  private openProbePage(): void {
    if (this.probeNavPending) {
      return;
    }
    this.probeNavPending = true;
    AppStorage.setOrCreate<number>('probeReq', 0);
    const opts: router.RouterOptions = { url: 'pages/InputProbePage' };
    this.getUIContext().getRouter().pushUrl(opts)
      .then((): void => {
        this.probeNavPending = false;
      })
      .catch((): void => {
        this.probeNavPending = false;
      });
  }

  // 消费掉指令再弹窗，避免返回到首页时又弹一次。
  // 2026-11-11 22:22 起不再用 router 跳页：改由应用自己建一个带尺寸的**子窗口**当弹窗，
  // 主窗口临时变成透明玻璃（见 build 里对 clipAuto 的分支）。这样屏幕上只冒出一张小卡片，
  // 没有「切到另一个界面」的观感，也不需要用户手动把窗口拖成小窗。
  private openClipPage(): void {
    if (this.clipNavPending) {
      return;
    }
    this.clipNavPending = true;
    AppStorage.setOrCreate<number>('clipReq', 0);
    AppStorage.setOrCreate<boolean>('clipAuto', true);
    let stage: window.WindowStage | null = null;
    let actx: common.UIAbilityContext | null = null;
    try {
      const ctx = this.getUIContext().getHostContext() as common.UIAbilityContext;
      actx = ctx;
      stage = ctx.windowStage;
    } catch (e) {
      stage = null;
    }
    if (stage === null || actx === null) {
      this.clipNavPending = false;
      this.pushClipPageFallback();
      return;
    }
    // 走统一入口。2026-11-12 起再把 ctx 传进去：签名 profile 带上 FLOAT_VIEW 的 acls 后，
    // 这条路径会先申请权限、再弹真正的跨应用浮窗（用户点一次「允许」即可）。
    // acls 没批下来时 request 会抛 201，自动降级到子窗口 / 小窗，行为与之前一致。
    const useCtx: common.UIAbilityContext = actx;
    const useStage: window.WindowStage = stage;
    ensureFloatPermission(useCtx).then((granted: boolean): Promise<boolean> => {
      return showClipCardFlow(useStage, granted ? useCtx : undefined);
    }).then((ok: boolean) => {
      this.clipNavPending = false;
      if (!ok) {
        // 设备不支持子窗口，退回老的整页浮层，功能不能断
        AppStorage.setOrCreate<boolean>('clipFallback', true);
        this.pushClipPageFallback();
        return;
      }
      if (AppStorage.get<string>('clipSurface') === 'win') {
        // 主窗口已经是浮动小窗：主窗口就是那张卡片，只要把 ClipPage 压进来即可。
        // 用 pushUrl 而不是 loadContent —— 首页要留在栈底，取完 back 回来时
        // 投屏日志、会话状态都还在（loadContent 会把它们全丢掉）。
        AppStorage.setOrCreate<boolean>('clipPushed', true);
        this.pushClipPageFallback();
      }
    }).catch((): void => {
      this.clipNavPending = false;
      AppStorage.setOrCreate<boolean>('clipFallback', true);
      this.pushClipPageFallback();
    });
  }

  private pushClipPageFallback(): void {
    const opts: router.RouterOptions = { url: 'pages/ClipPage', params: { auto: true } };
    this.getUIContext().getRouter().pushUrl(opts).catch((): void => {});
  }

  // 注意：这里刻意不做 aboutToDisappear 清理 —— 跳转到会话页 / 网络探针页时
  // 首页只 onHide 不销毁，而探针本身有 180s 上限会自停。若在这里 stop()，
  // 用户跳去别的页面会把还在跑的探针顺手杀掉。

  private pushLog(msg: string): void {
    const line = `${timePrefix()} ${msg}`;
    const next: string[] = this.logs.slice();
    next.push(line);
    if (next.length > MAX_LOG_LINES) {
      next.splice(0, next.length - MAX_LOG_LINES);
    }
    this.logs = next;
    this.scrollLogToBottom();
  }

  private scrollLogToBottom(): void {
    if (!this.showLogs) {
      return;
    }
    setTimeout((): void => {
      try {
        this.logScroller.scrollEdge(Edge.Bottom);
      } catch (e) {
        // 列表尚未挂载（日志区收起）时忽略
      }
    }, 60);
  }

  private async toggleProbe(): Promise<void> {
    if (this.running) {
      await this.probe.stop();
      await KeepAlive.stop();
      this.running = false;
      this.statusText = '已手动停止';
      return;
    }

    this.logs = [];
    this.statusText = '启动中…';
    const host = this.getUIContext().getHostContext();
    if (host === undefined) {
      this.statusText = '启动失败：无法获取上下文';
      this.pushLog('[ERR] getHostContext 返回 undefined');
      return;
    }
    const ctx = host as common.UIAbilityContext;

    this.probe.onTick((msg: string) => {
      this.pushLog(msg);
    });
    this.probe.onStopped((msg: string) => {
      this.running = false;
      this.statusText = '探针结束';
      this.pushLog(msg);
      KeepAlive.stop();
    });

    try {
      this.running = true;
      const kept = await KeepAlive.start(ctx);
      this.pushLog(kept ? '长时任务已启动（multiDevice + audioRecording）' : '[ERR] 长时任务启动失败');
      await this.probe.start(ctx, PROBE_DURATION_MS);
      this.filePath = this.probe.getFilePath();
      this.statusText = '采集中，可以锁屏 / 熄屏测试';
      this.pushLog(`探针启动，输出 ${this.filePath}`);
    } catch (e) {
      const err = e as BusinessError;
      this.running = false;
      this.statusText = `启动失败 ${err.code}`;
      this.pushLog(`[ERR] ${err.code} ${err.message}`);
    }
  }

  private statusWord(): string {
    if (!this.running) {
      return this.statusText === '探针结束' ? '已完成' : '待机';
    }
    return '采集中';
  }

  private statusTone(): string {
    if (!this.running) {
      return this.statusText === '探针结束' ? C_OK : C_NEUTRAL;
    }
    return C_DANGER;
  }

  // 入口行：左标题+说明，右箭头
  @Builder
  entryRow(title: string, desc: string, accent: string, url: string) {
    Row({ space: 12 }) {
      Column({ space: 3 }) {
        Text(title)
          .fontSize(15)
          .fontWeight(FontWeight.Medium)
          .fontColor(C_TITLE)
        Text(desc)
          .fontSize(11)
          .fontColor(C_SUB)
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
      }
      .alignItems(HorizontalAlign.Start)
      .layoutWeight(1)

      Text('›')
        .fontSize(20)
        .fontColor(accent)
    }
    .width('100%')
    .padding({ left: 14, right: 14, top: 14, bottom: 14 })
    .backgroundColor(C_FILL_SOFT)
    .borderRadius(14)
    .onClick((): void => {
      const opts: router.RouterOptions = { url: url };
      this.getUIContext().getRouter().pushUrl(opts).catch((): void => {});
    })
  }

  build() {
    // PC 来取剪贴板的那一次（clipAuto=true）：主窗口退化成一块**全透明玻璃**，
    // 屏幕上只留 ClipWindow.openClipCard 建出来的子窗口卡片。
    // 2026-11-11 22:22 用户否掉了「让用户手动拖成小窗」——必须是自动的弹窗。
    // 主窗口留在这里而不是换页：取完清标记即可恢复，全程没有页面转场。
    if (this.clipAuto) {
      Column()
        .width('100%')
        .height('100%')
        .backgroundColor('#00000000')
    } else {
      this.IndexView()
    }
  }

  @Builder
  IndexView() {
    Column({ space: 14 }) {
      // ── 头部：品牌 + 状态胶囊 ──────────────────────────────
      Row({ space: 12 }) {
        Column({ space: 3 }) {
          Row({ space: 8 }) {
            Text('HarmLink')
              .fontSize(26)
              .fontWeight(FontWeight.Bold)
              .fontColor(C_TITLE)
            Text('device')
              .fontSize(10)
              .fontColor(C_PRIMARY)
              .padding({ left: 6, right: 6, top: 3, bottom: 3 })
              .backgroundColor(C_FILL_BLUE)
              .borderRadius(6)
          }

          Text('手机 / 平板端 · 采集编码与控制台')
            .fontSize(12)
            .fontColor(C_SUB)
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)

        Row({ space: 6 }) {
          Circle({ width: 7, height: 7 })
            .fill(this.statusTone())
          Text(this.statusWord())
            .fontSize(12)
            .fontColor(C_BODY)
            .fontWeight(FontWeight.Medium)
        }
        .padding({ left: 12, right: 12, top: 8, bottom: 8 })
        .backgroundColor(C_CARD)
        .borderRadius(20)
        .shadow(SHADOW_CHIP)
      }
      .width('100%')
      .alignItems(VerticalAlign.Center)

      // ── 主控卡：GATE-01 探针 ──────────────────────────────
      Column({ space: 12 }) {
        Row({ space: 8 }) {
          Text('息屏采集探针')
            .fontSize(16)
            .fontWeight(FontWeight.Medium)
            .fontColor(C_TITLE)
            .layoutWeight(1)

          Text('GATE-01')
            .fontSize(11)
            .fontColor(C_PRIMARY)
            .padding({ left: 8, right: 8, top: 4, bottom: 4 })
            .backgroundColor(C_FILL_BLUE)
            .borderRadius(6)
        }
        .width('100%')
        .alignItems(VerticalAlign.Center)

        Text('验证熄屏 / 锁屏后 AVScreenCapture 是否仍持续出新帧')
          .fontSize(12)
          .fontColor(C_SUB)
          .width('100%')

        Button(this.running ? '■  停止探针' : `开始 ${PROBE_DURATION_MS / 1000} 秒探针`)
          .width('100%')
          .height(56)
          .fontSize(17)
          .fontWeight(FontWeight.Medium)
          .fontColor('#FFFFFF')
          .backgroundColor(this.running ? C_FILL_DARK : C_PRIMARY)
          .borderRadius(16)
          .onClick((): void => {
            this.toggleProbe().catch((): void => {});
          })

        if (this.running || this.logs.length > 0) {
          Text(this.statusText)
            .fontSize(13)
            .fontColor(C_BODY)
            .width('100%')
        }

        Text('开始 → 运行 10 秒 → 按电源键熄屏 → 等 20 秒 → 亮屏看日志')
          .fontSize(12)
          .fontColor(C_SUB)
          .width('100%')
      }
      .indexCard()

      // ── 入口卡 ────────────────────────────────────────────
      Column({ space: 10 }) {
        Text('功能入口')
          .fontSize(14)
          .fontWeight(FontWeight.Medium)
          .fontColor(C_TITLE)
          .width('100%')

        // 剪贴板同步放第一行：这是高频操作，打开 App 就该一眼看到。
        this.entryRow('剪贴板 · 发到电脑', '复制文字后点系统「粘贴」，电脑直接 Ctrl+V', C_PRIMARY,
          'pages/ClipPage')
        this.entryRow('统一会话 · 投屏与控制', '码流 · 注入 · 剪贴板 · 三键导航', C_OK,
          'pages/SessionPage')
        this.entryRow('网络探针 · GATE-L0', 'UDP 延迟与三档码率测量（HWP v1.1）', C_PRIMARY,
          'pages/NetworkProbePage')
      }
      .indexCard()

      // ── 日志卡（可折叠）───────────────────────────────────
      Column({ space: 0 }) {
        Row({ space: 8 }) {
          Text('运行日志')
            .fontSize(14)
            .fontWeight(FontWeight.Medium)
            .fontColor(C_TITLE)
          Text(`${this.logs.length}`)
            .fontSize(10)
            .fontColor(C_SUB)
            .padding({ left: 6, right: 6, top: 2, bottom: 2 })
            .backgroundColor(C_FILL_CHIP)
            .borderRadius(6)

          Blank()

          Text(this.showLogs ? '收起' : '展开')
            .fontSize(12)
            .fontColor(C_PRIMARY)
        }
        .width('100%')
        .height(28)
        .onClick((): void => {
          this.showLogs = !this.showLogs;
          if (this.showLogs) {
            this.scrollLogToBottom();
          }
        })

        if (this.showLogs) {
          if (this.logs.length === 0) {
            Column({ space: 8 }) {
              Text('🖥')
                .fontSize(26)
              Text('暂无日志')
                .fontSize(13)
                .fontColor(C_BODY)
              Text('启动探针后这里会滚动显示采集过程')
                .fontSize(11)
                .fontColor(C_SUB)
            }
            .width('100%')
            .layoutWeight(1)
            .justifyContent(FlexAlign.Center)
          } else {
            List({ space: 3, scroller: this.logScroller }) {
              ForEach(this.logs, (item: string, idx: number): void => {
                ListItem() {
                  Text(item)
                    .fontSize(11)
                    .fontFamily('monospace')
                    .fontColor(logColor(item))
                    .width('100%')
                }
              }, (item: string, idx: number): string => `${idx}_${item}`)
            }
            .width('100%')
            .layoutWeight(1)
            .padding({ top: 8 })
          }
        }
      }
      .indexCard()
      .layoutWeight(this.showLogs ? 1 : 0)

      Text('HarmLink · HWP v1.1 · Wi-Fi LAN 直连')
        .fontSize(10)
        .fontColor(C_SUB)
        .width('100%')
        .textAlign(TextAlign.Center)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 18, right: 18, top: 18, bottom: 14 })
    .alignItems(HorizontalAlign.Start)
    .linearGradient({ angle: 180, colors: [[C_BG_TOP, 0.0], [C_BG_BOTTOM, 1.0]] })
  }
}
