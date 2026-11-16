// NEXT-02~06 统一会话页：设备端一个入口覆盖码流、输入、剪贴板、导航、角色与分辨率。
// 与 GATE-01 / GATE-L0 探针页并列，互不干扰，便于随时回归。
//
// UI 结构（自上而下）：
//   头部（品牌 + 状态胶囊）→ 主控卡（状态 / 主操作 / 次操作）→ 指标卡 2×2
//   → 会话信息标签 → 运行日志（可折叠）
// 指标全部来自 captureMetrics() 的结构化数据，不再解析拼接字符串。

import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { window } from '@kit.ArkUI';
import { BusinessError, pasteboard } from '@kit.BasicServicesKit';
import { SessionService, SessionStats, CaptureMetrics } from '../core/SessionService';
import { LOCAL_PORT, PEER_HOST, PEER_PORT } from '../transport/HwpPacket';
import { probeUitestSocket } from 'libharmoscap.so';
import {
  C_BG_TOP, C_BG_BOTTOM, C_CARD, C_TITLE, C_BODY, C_SUB, C_PRIMARY,
  C_DANGER, C_WARN, C_OK, C_NEUTRAL, C_FILL_CHIP, C_FILL_SOFT, C_FILL_BLUE,
  C_FILL_GREEN, C_FILL_DARK, SHADOW_CARD, SHADOW_CHIP, logColor, timePrefix
} from '../common/Theme';

const MAX_LOG_LINES: number = 60;
// 视觉息屏降级（INV-05）：应用层没有设置系统亮度的 API，
// setWindowBrightness 只在主窗口前台有焦点时生效、移到后台立刻失效，
// 且文档明示不适用于投屏/镜像虚拟屏。因此只能做到「本窗口在前台时压暗物理背光」。
// 采集走 AVScreenCapture 拿渲染内容，不受背光影响（GATE-S1 已验证亮度归零时出帧非黑）。
const DIM_BRIGHTNESS: number = 0.05;

// 配色与阴影统一来自 common/Theme.ets（与 Index / NetworkProbePage 共用）
// 卡片通用外观（@Styles 只允许通用属性）
@Styles
function sessionCard() {
  .width('100%')
  .padding(16)
  .backgroundColor(C_CARD)
  .borderRadius(20)
  .shadow(SHADOW_CARD)
}

// 指标卡：@Builder 单参数对象字面量 + $$ 引用传递，数值变化才会刷新
interface SessionStatCard {
  cardLabel: string;
  cardValue: string;
  cardUnit: string;
  cardTone: string;
}

// 方案 B 探针返回的 JSON 结构（与 cpp/harmoscap.cpp 的 ProbeUitestSocket 一致）
interface ProbeUitestJson {
  connectOk: boolean;
  connectErrno: number;
  peerPid: number;
  peerUid: number;
  sent: boolean;
  sentBytes: number;
  recvBytes: number;
  gotTail: boolean;
  connectMs: number;
  rpcMs: number;
  api: string;
  resp: string;
}

@Entry
@Component
struct SessionPage {
  @State running: boolean = false;
  @State paused: boolean = false;
  @State statusText: string = '未启动';
  // 指标卡
  @State fpsValue: string = '0.0';
  @State frameValue: string = '0';
  @State kbpsValue: string = '0';
  @State errValue: string = '0';
  @State nativeLine: string = '';
  @State directOn: boolean = false;
  // native 录屏状态：<0 表示采集尚未启动（PC 握手下发 CMD_STREAM 后才起）
  @State capState: number = -1;
  // 会话信息
  @State resText: string = '—';
  @State roleText: string = '—';
  @State injectText: string = '—';
  @State clipText: string = '—';
  @State lockText: string = '—';
  // 日志
  @State logs: string[] = [];
  @State showLogs: boolean = true;
  @State dimmed: boolean = false;
  // 窄屏（手机）标记：2026-11-14 起本 App 也要跑在手机上（HOP-AL10，展开 1828×2584），
  // vp 宽度只有约 560，平板那套 16vp 内边距 + 28 号大数字会把卡片挤到贴边。
  // 这里按窗口 vp 宽度切一档紧凑排版，平板（800vp）完全不受影响。
  @State narrow: boolean = false;
  // 方案 B 探针（2026-11-08）：验证应用进程能否直连 agent.so 的 @uitest_socket
  @State probeText: string = '';

  // 会话是进程内单例：采集授权与编码器跨页面常驻，
  // 离开本页只暂停发送，回来直接续上，不再弹「允许 HarmLink 使用你的屏幕」。
  private session: SessionService = SessionService.shared();
  private statTimer: number = -1;
  private logScroller: Scroller = new Scroller();
  private lastFrames: number = 0;
  private lastBytes: number = 0;
  private lastSampleMs: number = 0;

  aboutToAppear(): void {
    hilog.info(0x0000, 'HarmLink', 'SessionPage aboutToAppear, alive=%{public}s',
      `${this.session.isAlive()}`);
    this.measureScreen();
    this.session.setOnLog((msg: string): void => {
      this.pushLog(msg);
    });
    // 本次是不是 PC 用 `--ps stream 1` 拉起来的？先取，因为它要压过「会话已存活」分支。
    // 2026-11-14 修：原来先判 isAlive() 就 return，手机上 PC 重新拉起时
    // 会话常常还活着（只是暂停发送），自动起采被这个早退直接吃掉 → 看着「拉起了但没画面」。
    const p = this.getUIContext().getRouter().getParams() as Record<string, Object> | undefined;
    const autoStart: boolean = p !== undefined && `${p.autoStart}` === 'true';
    // 会话还在（此前离开过本页）：恢复「已暂停」态与统计轮询。
    // 但如果是 PC 自动拉起，不能停在这里 —— 要继续走到下面的自动起采，把发送恢复起来。
    if (this.session.isAlive()) {
      this.running = true;
      this.paused = true;
      this.statusText = '会话常驻中（采集授权仍有效），点「继续投屏」立即恢复';
      this.statTimer = setInterval((): void => {
        this.refreshStats();
      }, 1000);
      if (!autoStart) {
        return;
      }
      this.pushLog('[AUTO] 会话仍在，直接恢复推流（不重复申请录屏授权）');
      hilog.info(0x0000, 'HarmLink', 'SessionPage AUTO: resume existing session');
      this.startWith('').catch((e: BusinessError): void => {
        hilog.error(0x0000, 'HarmLink', 'startWith resume failed code=%{public}d', e.code);
      });
      return;
    }
    // 2026-11-14：PC 端 `aa start ... --ps stream 1` 拉起的一次性自动起采。
    // 只认路由参数 autoStart，用户手动点进本页时行为完全不变。
    // 系统录屏授权框仍然要用户本人点「允许」，这一步无法绕过。
    if (autoStart) {
      this.pushLog('[AUTO] 来自 PC 的自动起采请求，正在开始采集…');
      hilog.info(0x0000, 'HarmLink', 'SessionPage AUTO: start fresh capture');
      this.startWith('').catch((e: BusinessError): void => {
        hilog.error(0x0000, 'HarmLink', 'startWith fresh failed code=%{public}d msg=%{public}s',
          e.code, e.message);
      });
    }
  }

  aboutToDisappear(): void {
    if (this.statTimer >= 0) {
      clearInterval(this.statTimer);
      this.statTimer = -1;
    }
    // 页面销毁时务必恢复亮度，否则会停留在调暗状态
    if (this.dimmed) {
      this.applyBrightness(-1).catch((): void => {});
      this.dimmed = false;
    }
    // 只暂停发送，不拆会话：采集实例与录屏授权保留，
    // 再次进入本页不会重新弹系统授权框（真正的全释放在「结束投屏」按钮里）。
    if (this.session.isAlive()) {
      this.session.pauseStream();
      this.paused = true;
    }
  }

  // 量一次窗口宽度，决定走平板排版还是手机紧凑排版。
  // 用 vp 而不是 px：手机 1828px 在 3.25 密度下只有约 562vp，和平板 800vp 差很多。
  private measureScreen(): void {
    const ctx = this.getUIContext().getHostContext();
    if (ctx === undefined) {
      return;
    }
    window.getLastWindow(ctx as common.UIAbilityContext).then((win: window.Window): void => {
      const px: number = win.getWindowProperties().windowRect.width;
      const vp: number = this.getUIContext().px2vp(px);
      this.narrow = vp < 620;
    }).catch((): void => {
      // 量不到就按平板排版，不能因此让页面起不来
    });
  }

  private async applyBrightness(value: number): Promise<void> {    const host = this.getUIContext().getHostContext();
    if (host === undefined) {
      return;
    }
    try {
      const ctx = host as common.UIAbilityContext;
      const win: window.Window = await window.getLastWindow(ctx);
      await win.setWindowBrightness(value);
    } catch (e) {
      const err = e as BusinessError;
      this.pushLog(`[WARN] 设置窗口亮度失败 ${err.code}：${err.message}`);
    }
  }

  // 视觉息屏降级的开关：只压暗本窗口前台时的物理背光
  private async toggleDim(): Promise<void> {
    if (this.dimmed) {
      await this.applyBrightness(-1);
      this.dimmed = false;
      this.pushLog('已恢复系统亮度');
    } else {
      await this.applyBrightness(DIM_BRIGHTNESS);
      this.dimmed = true;
      this.pushLog(`屏幕已调暗（亮度 ${DIM_BRIGHTNESS}）：采集不受影响，PC 画面正常。` +
        '切到其他应用会自动恢复系统亮度（窗口亮度仅前台有效）');
    }
  }

  private pushLog(msg: string): void {
    const line: string = `${timePrefix()} ${msg}`;
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
    // 列表布局在状态更新后才生效，延后一帧再滚到底
    setTimeout((): void => {
      try {
        this.logScroller.scrollEdge(Edge.Bottom);
      } catch (e) {
        // 列表尚未挂载（如日志区被折叠）时忽略
      }
    }, 60);
  }

  // 三态：未启动 → 运行中 ←→ 已暂停。
  // 「停止投屏」只暂停发送，会话与采集授权保留，继续时不重新弹录屏授权。
  private async toggle(): Promise<void> {
    if (this.running && !this.paused) {
      this.session.pauseStream();
      this.paused = true;
      this.statusText = '已暂停发送，采集仍在继续，点「继续投屏」免授权恢复';
      return;
    }
    if (this.running && this.paused) {
      this.statusText = '恢复投屏中…';
      try {
        await this.session.resumeStream();
        this.paused = false;
        this.statusText = '会话运行中，等待 PC 握手（先 CONTROL 后收流）';
      } catch (e) {
        const err = e as BusinessError;
        this.statusText = `恢复失败 ${err.code}`;
        this.pushLog(`[ERR] ${err.code} ${err.message}`);
      }
      return;
    }
    await this.startWith('');
  }

  // 唯一的全量释放：停采集、拆 transport、退长时任务。
  // 之后系统会认为录屏授权已失效，下次「启动实时投屏」必然重新弹授权框。
  private async finishSession(): Promise<void> {
    if (this.statTimer >= 0) {
      clearInterval(this.statTimer);
      this.statTimer = -1;
    }
    try {
      await this.session.stop();
    } catch (e) {
      const err = e as BusinessError;
      this.pushLog(`[WARN] 释放会话异常 ${err.code}：${err.message}`);
    }
    this.running = false;
    this.paused = false;
    this.statusText = '会话已结束，采集已释放';
  }

  // videoPath 为空 → GATE-02 实时源（C++ NAPI 采集编码）；非空 → 文件码流回归
  private async startWith(videoPath: string): Promise<void> {
    // 只在「正在采集且未暂停」时才早退。
    // 原来只看 this.running 就 return，但会话存活时 running 也是 true，而实际可能处于暂停
    // （paused=true, mode=IDLE）→ PC 自动拉起会静默什么都不做，日志里只剩 HwpL0 mode=IDLE。
    // 2026-11-14 修：改成以「session 是否真在推流」为准。
    if (this.running && !this.paused && this.session.isStreaming()) {
      return;
    }
    this.logs = [];
    this.resetMetrics();
    this.statusText = videoPath.length === 0 ? '启动实时采集中…' : '启动文件码流中…';
    try {
      const host = this.getUIContext().getHostContext();
      let ctx: common.UIAbilityContext | null = null;
      if (host !== undefined) {
        ctx = host as common.UIAbilityContext;
      }
      await this.session.start(videoPath, ctx);
      this.running = true;
      this.paused = false;
      this.statusText = '会话运行中，等待 PC 握手（先 CONTROL 后收流）';
      this.statTimer = setInterval((): void => {
        this.refreshStats();
      }, 1000);
    } catch (e) {
      const err = e as BusinessError;
      this.running = false;
      this.statusText = `启动失败 ${err.code}`;
      this.pushLog(`[ERR] ${err.code} ${err.message}`);
    }
  }

  private async startFile(): Promise<void> {
    const ctx: common.UIAbilityContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
    await this.startWith(ctx.filesDir + '/gate01.mp4');
  }

  /**
   * 方案 B 可行性探针：在应用进程（normal_hap 域）内直连 agent.so 的 @uitest_socket。
   * 两步：① 只建链，验证 SELinux 是否放行 unix_stream_socket connectto；
   *       ② 建链成功后发一对 touchDown/touchUp（800,1900 无副作用位置），
   *          验证 agent 是否对 Peer UID 做白名单校验（禁止则无回包或错误回包）。
   * 前置条件：设备上 agent.so 的 daemon 已运行（uitest start-daemon singleness）。
   */
  private runProbe(): void {
    this.probeText = '探测中…';
    try {
      const link = probeUitestSocket('', 0, 0);
      this.pushLog(`[PROBE] 建链 ${link}`);
      const parsed = JSON.parse(link) as ProbeUitestJson;
      if (!parsed.connectOk) {
        this.probeText = `建链失败 errno=${parsed.connectErrno}`;
        return;
      }
      const down = probeUitestSocket('touchDown', 800, 1900);
      const up = probeUitestSocket('touchUp', 800, 1900);
      this.pushLog(`[PROBE] touchDown ${down}`);
      this.pushLog(`[PROBE] touchUp ${up}`);
      const d = JSON.parse(down) as ProbeUitestJson;
      const u = JSON.parse(up) as ProbeUitestJson;
      const verdict: string = (d.gotTail && u.gotTail)
        ? '✅ 放行：应用进程可直连，方案 B 可行'
        : '⚠️ 建链成功但无完整回包，疑似 UID 校验';
      this.probeText = `peerUid=${parsed.peerUid} down=${d.gotTail ? 'ok' : 'no-tail'}(${d.rpcMs.toFixed(2)}ms) ` +
        `up=${u.gotTail ? 'ok' : 'no-tail'}(${u.rpcMs.toFixed(2)}ms) ${verdict}`;
      this.pushLog(`[PROBE] ${verdict}`);
    } catch (e) {
      const err = e as BusinessError;
      this.probeText = `异常 ${err.code} ${err.message}`;
      this.pushLog(`[PROBE][ERR] ${err.code} ${err.message}`);
    }
  }

  /**
   * 读取系统剪贴板文本并上行给 PC。
   *
   * 必须在 PasteButton 的 onClick 回调内**同步发起**读取：安全控件的 SECURE_PASTE 授权
   * 只在这次点击的回调生命周期内有效，回调一结束权限即被回收（文档明示），
   * 所以不能 setTimeout / 排队到别的任务里再读。
   *
   * 这里用 getDataSync()：同步返回，天然贴合「回调内有效」的约束，也省掉一次 await 边界。
   */
  private readClipToPc(): void {
    try {
      const data: pasteboard.PasteData = pasteboard.getSystemPasteboard().getDataSync();
      const text: string = data.getPrimaryText();
      // 是否真发出去由 pushClipText 判定（内容与已同步内容一致时会被防回环拦下），
      // 这里不再无条件报"已上行"，避免把"读到"误报成"发成功"。
      this.session.pushClipText(text);
    } catch (e) {
      const err = e as BusinessError;
      this.pushLog(`[剪贴板上行][ERR] ${err.code} ${err.message}`);
    }
  }

  private resetMetrics(): void {
    this.lastFrames = 0;
    this.lastBytes = 0;
    this.lastSampleMs = 0;
    this.fpsValue = '0.0';
    this.frameValue = '0';
    this.kbpsValue = '0';
    this.errValue = '0';
    this.nativeLine = '';
    this.directOn = false;
    this.capState = -1;
    this.resText = '—';
    this.roleText = '—';
    this.injectText = '—';
    this.clipText = '—';
    this.lockText = '—';
  }

  private roleName(role: number): string {
    if (role === 1) {
      return '投屏端';
    }
    if (role === 2) {
      return '被控端';
    }
    if (role === 3) {
      return '双向';
    }
    return '未协商';
  }

  // 每秒采样：帧率/码率取两次采样的增量，静止画面掉到 1~5fps 属正常
  private refreshStats(): void {
    const now: number = Date.now();
    const m: CaptureMetrics = this.session.captureMetrics();
    if (m.present) {
      const dt: number = this.lastSampleMs > 0 ? (now - this.lastSampleMs) / 1000 : 0;
      if (dt > 0.2) {
        const dFrames: number = m.frames - this.lastFrames;
        const dBytes: number = m.bytes - this.lastBytes;
        this.fpsValue = (dFrames > 0 ? dFrames / dt : 0).toFixed(1);
        this.kbpsValue = `${Math.max(0, Math.round(dBytes * 8 / dt / 1000))}`;
      }
      this.lastFrames = m.frames;
      this.lastBytes = m.bytes;
      this.lastSampleMs = now;
      this.frameValue = `${m.frames}`;
      this.errValue = `${m.sendErrors}`;
      this.directOn = m.directSend === 1;
      this.capState = m.state;
      this.nativeLine = m.state < 0
        ? '采集尚未启动：PC 握手后由 CMD_STREAM 触发（当前只在等待）'
        : `关键帧 ${m.keyframes} · 已发 ${m.packets} 包 · ` +
          `IDR 缓存 ${m.idrCachedKb}KB / 重放 ${m.idrReplays} 次 · 状态 ${m.state}`;
    }

    const s: SessionStats = this.session.getStats();
    this.resText = s.resolution.width > 0
      ? `${s.resolution.width}×${s.resolution.height}${s.resolution.folded === 1 ? ' 折叠' : ''}`
      : '—';
    this.roleText = this.roleName(s.role);
    this.injectText = s.injectOk + s.injectErr === 0
      ? '无事件'
      : `OK ${s.injectOk} / 失败 ${s.injectErr} / ${(s.injectUs / 1000).toFixed(1)}ms`;
    this.clipText = `下行 ${s.clipToDevice} · 上行 ${s.clipFromDevice}`;
    this.lockText = s.locked ? '已锁屏' : '未锁屏';
  }

  private statusWord(): string {
    if (!this.running) {
      return '未启动';
    }
    if (this.paused) {
      return '已暂停';
    }
    // 会话起了但采集还没起：等 PC 下发 CMD_STREAM，别显示成「投屏中」
    return this.capState >= 0 ? '投屏中' : '等待 PC';
  }

  private statusTone(): string {
    if (!this.running) {
      return C_NEUTRAL;
    }
    if (this.paused) {
      return C_WARN;
    }
    return this.capState >= 0 ? C_DANGER : C_WARN;
  }

  /// 传输通道词，只在真正发流时才有意义。与 statusWord 分开，
  /// 避免同一个徽章一会儿说状态、一会儿说通道。
  private channelWord(): string {
    if (!this.running || this.paused || this.capState < 0) {
      return '';
    }
    return this.directOn ? 'Native 直发' : 'TSFN 回传';
  }

  /// 主控卡右上角徽章：状态为主，发流中再挂通道后缀。
  private badgeText(): string {
    const channel: string = this.channelWord();
    return channel.length > 0 ? `${this.statusWord()} · ${channel}` : this.statusWord();
  }

  private primaryLabel(): string {
    if (!this.running) {
      return '启动实时投屏';
    }
    if (this.paused) {
      return '▶  继续投屏';
    }
    return '⏸  暂停投屏';
  }

  private primaryTone(): string {
    if (!this.running) {
      return C_PRIMARY;
    }
    if (this.paused) {
      return C_OK;
    }
    return C_FILL_DARK;
  }

  private hintText(): string {
    if (!this.running) {
      return '启动后本机开始采集编码，PC 端自动握手收流';
    }
    if (this.paused) {
      return '发送已暂停，采集会话保留，继续时不会重新弹出录屏授权';
    }
    return '离开本页只暂停发送，采集授权保留；要彻底释放请点「结束投屏」';
  }

  // 单个指标卡
  @Builder
  statCard($$: SessionStatCard) {
    Column({ space: 8 }) {
      Row({ space: 6 }) {
        Circle({ width: 6, height: 6 })
          .fill($$.cardTone)
        Text($$.cardLabel)
          .fontSize(this.narrow ? 11 : 12)
          .fontColor(C_SUB)
          .maxLines(1)
      }

      Row({ space: 5 }) {
        Text($$.cardValue)
          .fontSize(this.narrow ? 23 : 28)
          .fontWeight(FontWeight.Bold)
          .fontColor(C_TITLE)
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
          .flexShrink(1)
        Text($$.cardUnit)
          .fontSize(11)
          .fontColor(C_SUB)
          .margin({ bottom: 5 })
      }
      .alignItems(VerticalAlign.Bottom)
    }
    .alignItems(HorizontalAlign.Start)
    .layoutWeight(1)
    .padding(this.narrow ? 11 : 14)
    .backgroundColor(C_FILL_SOFT)
    .borderRadius(14)
  }

  // 会话信息小标签：等分宽度，长文本省略而不是撑破卡片
  @Builder
  infoChip($$: SessionStatCard) {
    Row({ space: 6 }) {
      Text($$.cardLabel)
        .fontSize(11)
        .fontColor(C_SUB)
      Text($$.cardValue)
        .fontSize(12)
        .fontColor(C_BODY)
        .fontWeight(FontWeight.Medium)
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .layoutWeight(1)
        .textAlign(TextAlign.End)
    }
    .layoutWeight(1)
    .padding({ left: 10, right: 10, top: 8, bottom: 8 })
    .backgroundColor(C_FILL_CHIP)
    .borderRadius(10)
  }

  build() {
    Column({ space: this.narrow ? 10 : 14 }) {
      // ── 头部：返回 + 品牌 + 状态胶囊 ──────────────────────
      Row({ space: this.narrow ? 8 : 12 }) {
        Button('‹')
          .width(this.narrow ? 34 : 38)
          .height(this.narrow ? 34 : 38)
          .fontSize(22)
          .fontColor(C_BODY)
          .backgroundColor('#FFFFFF')
          .borderRadius(19)
          .shadow(SHADOW_CHIP)
          .onClick((): void => {
            this.getUIContext().getRouter().back();
          })

        Column({ space: 3 }) {
          Row({ space: 8 }) {
            Text('HarmLink')
              .fontSize(this.narrow ? 23 : 26)
              .fontWeight(FontWeight.Bold)
              .fontColor(C_TITLE)
            Text('HWP v1.1')
              .fontSize(10)
              .fontColor(C_PRIMARY)
              .padding({ left: 6, right: 6, top: 3, bottom: 3 })
              .backgroundColor(C_FILL_BLUE)
              .borderRadius(6)
          }

          Text(`${PEER_HOST}:${PEER_PORT}  ·  本机 ${LOCAL_PORT}  ·  ${this.roleText}`)
            .fontSize(12)
            .fontColor(C_SUB)
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })
            .margin({ top: 2 })
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
        .backgroundColor('#FFFFFF')
        .borderRadius(20)
        .shadow(SHADOW_CHIP)
      }
      .width('100%')
      .alignItems(VerticalAlign.Center)

      // ── 主控卡 ────────────────────────────────────────────
      Column({ space: 12 }) {
        Row({ space: 8 }) {
          Text('投屏控制')
            .fontSize(16)
            .fontWeight(FontWeight.Medium)
            .fontColor(C_TITLE)
            .layoutWeight(1)

          Text(this.badgeText())
            .fontSize(11)
            .fontColor(this.directOn && this.capState >= 0 && !this.paused ? C_OK : C_BODY)
            .padding({ left: 8, right: 8, top: 4, bottom: 4 })
            .backgroundColor(this.directOn && this.capState >= 0 && !this.paused ? C_FILL_GREEN : C_FILL_CHIP)
            .borderRadius(6)
        }
        .width('100%')
        .alignItems(VerticalAlign.Center)

        Button(this.primaryLabel())
          .width('100%')
          .height(56)
          .fontSize(17)
          .fontWeight(FontWeight.Medium)
          .fontColor('#FFFFFF')
          .backgroundColor(this.primaryTone())
          .borderRadius(16)
          .onClick((): void => {
            this.toggle().catch((): void => {});
          })

        if (this.running) {
          // 设备 → PC 剪贴板上行。必须是官方安全控件 PasteButton：普通签名拿不到 READ_PASTEBOARD
          // （system_basic），只有「用户点一下控件」才能临时获授 SECURE_PASTE 权限静默读剪贴板。
          // 因此这里是「点即授权」的交互，不是自动同步 —— 平台强制的形态。
          Row({ space: 10 }) {
            PasteButton({ icon: PasteIconStyle.LINES, text: PasteDescription.PASTE, buttonType: ButtonType.Capsule })
              .fontSize(15)
              .fontColor(C_BODY)
              .backgroundColor(C_FILL_CHIP)
              .borderRadius(12)
              .height(44)
              .padding({ left: 16, right: 16 })
              .onClick((event: ClickEvent, result: PasteButtonOnClickResult): void => {
                if (result !== PasteButtonOnClickResult.SUCCESS) {
                  this.pushLog('[剪贴板上行] 安全控件授权失败');
                  return;
                }
                this.readClipToPc();
              })

            Text('把设备剪贴板推到电脑')
              .fontSize(12)
              .fontColor(C_SUB)
              .layoutWeight(1)
          }
          .width('100%')
          .alignItems(VerticalAlign.Center)

          Button(this.dimmed ? '☀  恢复屏幕亮度' : '🌙  调暗屏幕（视觉息屏）')
            .width('100%')
            .height(48)
            .fontSize(15)
            .fontColor(this.dimmed ? '#FFD666' : C_BODY)
            .backgroundColor(this.dimmed ? C_FILL_DARK : C_FILL_CHIP)
            .borderRadius(12)
            .onClick((): void => {
              this.toggleDim().catch((): void => {});
            })

          // 唯一的全量释放入口：会作废录屏授权，下次启动要重新点「允许」
          Button('⏹  结束投屏（释放采集，下次需重新授权）')
            .width('100%')
            .height(44)
            .fontSize(13)
            .fontColor(C_SUB)
            .backgroundColor(C_FILL_CHIP)
            .borderRadius(12)
            .onClick((): void => {
              this.finishSession().catch((): void => {});
            })
        } else {
          Button('启动文件码流（链路回归）')
            .width('100%')
            .height(48)
            .fontSize(15)
            .fontColor(C_BODY)
            .backgroundColor(C_FILL_CHIP)
            .borderRadius(12)
            .onClick((): void => {
              this.startFile().catch((): void => {});
            })

          // 方案 B 验证入口：应用进程直连 @uitest_socket（不依赖 hdc / 无线调试）
          Button('🔌  探针 uitest socket（方案 B）')
            .width('100%')
            .height(44)
            .fontSize(13)
            .fontColor(C_SUB)
            .backgroundColor(C_FILL_CHIP)
            .borderRadius(12)
            .onClick((): void => {
              this.runProbe();
            })

          if (this.probeText.length > 0) {
            Text(this.probeText)
              .fontSize(11)
              .fontColor(C_SUB)
              .width('100%')
              .padding(8)
              .backgroundColor(C_FILL_SOFT)
              .borderRadius(8)
          }
        }

        // 未启动时 hint 已经说明用途，不再重复一行「未启动」
        if (this.running) {
          Text(this.statusText)
            .fontSize(13)
            .fontColor(C_BODY)
            .width('100%')
        }

        Text(this.hintText())
          .fontSize(12)
          .fontColor(C_SUB)
          .width('100%')
      }
      .sessionCard()

      // ── 指标卡 2×2 ────────────────────────────────────────
      Column({ space: 10 }) {
        Row({ space: 10 }) {
          this.statCard({
            cardLabel: '编码帧率',
            cardValue: this.fpsValue,
            cardUnit: 'fps',
            cardTone: this.running && !this.paused && this.capState >= 0 ? C_OK : C_NEUTRAL
          })
          this.statCard({
            cardLabel: '累计帧',
            cardValue: this.frameValue,
            cardUnit: '帧',
            cardTone: C_PRIMARY
          })
        }
        .width('100%')

        Row({ space: 10 }) {
          this.statCard({
            cardLabel: '编码码率',
            cardValue: this.kbpsValue,
            cardUnit: 'kbps',
            cardTone: C_PRIMARY
          })
          this.statCard({
            cardLabel: '发送失败',
            cardValue: this.errValue,
            cardUnit: '次',
            cardTone: this.errValue === '0' ? C_NEUTRAL : C_DANGER
          })
        }
        .width('100%')

        if (this.running) {
          Text(this.nativeLine)
            .fontSize(11)
            .fontColor(C_SUB)
            .width('100%')
        }

        Row({ space: 8 }) {
          this.infoChip({ cardLabel: '分辨率', cardValue: this.resText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: '角色', cardValue: this.roleText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: '锁屏', cardValue: this.lockText, cardUnit: '', cardTone: '' })
        }
        .width('100%')

        Row({ space: 8 }) {
          this.infoChip({ cardLabel: '注入', cardValue: this.injectText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: '剪贴板', cardValue: this.clipText, cardUnit: '', cardTone: '' })
        }
        .width('100%')
      }
      .sessionCard()

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
              Text('📋')
                .fontSize(26)
              Text('暂无日志')
                .fontSize(13)
                .fontColor(C_BODY)
              Text('启动后这里会滚动显示握手、采集与发流过程')
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
      .sessionCard()
      // 收起时按内容高度收窄，避免留一大块空卡片
      .layoutWeight(this.showLogs ? 1 : 0)

      Text('HarmLink · 本机采集编码 → PC 端解码上屏 · 三键导航与低频点击由 PC 端转发')
        .fontSize(10)
        .fontColor(C_SUB)
        .width('100%')
        .textAlign(TextAlign.Center)
        // 手机上系统手势条贴着屏幕底边，留一点空隙免得压字
        .margin({ bottom: this.narrow ? 6 : 0 })
    }
    .width('100%')
    .height('100%')
    .padding({
      left: this.narrow ? 12 : 18,
      right: this.narrow ? 12 : 18,
      top: this.narrow ? 12 : 18,
      bottom: this.narrow ? 10 : 14
    })
    .alignItems(HorizontalAlign.Start)
    .linearGradient({ angle: 180, colors: [[C_BG_TOP, 0.0], [C_BG_BOTTOM, 1.0]] })
  }
}
