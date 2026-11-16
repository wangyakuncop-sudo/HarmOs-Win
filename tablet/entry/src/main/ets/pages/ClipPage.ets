// 剪贴板同步页：平板上点一次系统「粘贴」安全控件 → 内容直接进 Windows 剪贴板，PC 端零操作。
//
// 为什么必须是 PasteButton（系统安全控件），不是普通按钮：
//   鸿蒙禁止应用静默读取剪贴板。普通 getDataSync() 需要 ohos.permission.READ_PASTEBOARD，
//   那是 system_basic 级，调试签名的 apl=normal 拿不到（与 CONTROL_DEVICE 同一条硬结论）。
//   PasteButton 是系统提供的安全控件：用户点它的瞬间，系统授予「当前剪贴板内容」的一次性
//   读权限（paste_button.d.ts:150 / 164 / 208），这是非系统应用唯一被允许的读法。
//
// 传输：UDP 广播到 255.255.255.255:18766（并补发子网定向广播），
//   PC 端 HarmOsWin 常驻监听该端口，收到即写 Windows 剪贴板。
//   不经过投屏通道、不依赖 hdc、不需要用户填 IP、PC 端不需要任何点击。
//
// 2026-11-11 新增「PC 替你点」：本页一进前台就广播 HCLIP/1-READY <x> <y>，
//   PC 端「取剪贴板」键收到 READY 后注入 uitest uiInput click 点这个 PasteButton。
//   READY 是必需的握手 —— 否则 PC 只能靠固定 sleep 猜页面有没有切过去，容易点到首页上；
//   坐标也由本页自己量（getRectangleById）后带回，PC 端不必维护硬编码常量。
//   注意此时设备必须开着无线调试（hdc 走的就是这条），这是方案的既有前提，不是新增依赖。
//
// 2026-11-11 02:20 按用户要求新增 auto 模式（从 --ps clip 1 进来时）：
//   不再显示「剪贴板同步」整页，只显示一个居中的小浮层卡片 + 粘贴按钮，
//   发完自动 moveAbilityToBackground 退回用户原来的界面，不停地占着前台。

import { pasteboard, BusinessError } from '@kit.BasicServicesKit';
import { socket, connection } from '@kit.NetworkKit';
import { common } from '@kit.AbilityKit';
import { util } from '@kit.ArkTS';
import { router } from '@kit.ArkUI';
import { image } from '@kit.ImageKit';
import { closeClipCard } from '../common/ClipWindow';
import { serveImageOnce } from '../common/ImageLink';
import { hilog } from '@kit.PerformanceAnalysisKit';

const DOMAIN: number = 0x0000;
const TAG: string = 'HarmLinkClip';

const CLIP_PORT: number = 18766;
const CHUNK_BYTES: number = 600;      // 单片原文字节数，base64 后约 800B，远小于 MTU
const ACK_WAIT_MS: number = 2000;
const BTN_ID: string = 'clipPasteBtn';
/** 图片走 TCP 旁路（见 common/ImageLink.ets）。上限 20MB：再大 UDP 通知与内存都开始不划算。 */
const MAX_IMAGE_BYTES: number = 20 * 1024 * 1024;
const IMG_SERVE_TIMEOUT_MS: number = 12000;

const C_BG_TOP: string = '#F7F8FA';
const C_BG_BOTTOM: string = '#EEF1F5';
const C_CARD: string = '#FFFFFF';
const C_TITLE: string = '#1F2329';
const C_BODY: string = '#3D444E';
const C_SUB: string = '#7A828E';
const C_OK: string = '#0E9F5F';
const C_DANGER: string = '#C4302B';
const C_PRIMARY: string = '#2B6CD4';

@Entry
@Component
struct ClipPage {
  @State statusText: string = '先在平板上复制文字或图片，再点下面的「粘贴」';
  @State statusOk: boolean = false;
  @State statusErr: boolean = false;
  @State busy: boolean = false;
  @State lastInfo: string = '';
  // true = 由 PC 的「取剪贴板」唤起：只显示极简浮层，做完自动退回后台
  @State autoMode: boolean = false;
  // true = 本页跑在「卡片容器」里（floatView 浮窗 / 自建子 window），窗口面积就等于卡片面积，
  // 这时卡片要铺满窗口，否则页内卡片 + 窗口底色会叠成双框。
  // false = 整页兜底形态（两条容器路都不可用时的老方案）。
  private fillCard: boolean = false;
  // true = 跑在系统浮窗里（floatView）。用户 2026-11-12 00:42 要求「不用显示文字，只显示个图标」，
  // 浮窗形态下只留一个纯图标按钮，说明文字与状态文字都不画 —— 浮窗越小越不打扰。
  private iconOnly: boolean = false;
  // 最近一次量出的粘贴按钮屏幕中心，用来判断「量到了没有」（0 表示布局还没完成）。
  private ptX: number = 0;
  private ptY: number = 0;

  private udp: socket.UDPSocket | null = null;
  private waitId: string = '';
  private acked: boolean = false;
  private ackTimer: number = -1;
  // 容器（浮窗/子窗口）在屏幕上的左上角不再缓存成字段：浮窗位置由系统摆放、还能被拖动，
  // 上报 READY 时现取组件相对屏幕的偏移（见 readyPayload 里的 screenOffset）。
  //
  // 窗口形态只在 Ability 的 onWindowStageCreate 里设一次就够，本页不再碰窗口。
  // 2026-11-11 实测：这里原先也调了一次 applyClipPopupMode，一串 await 排在前头，
  // 让 READY 从 736ms 涨到 974ms，而窗口尺寸根本没变 —— 纯亏，已删。
  //
  // aa start 把已在栈顶的本页重新拉到前台时，aboutToAppear 不会再跑，
  // 但 PC 端仍然在等 READY（否则就得靠猜），所以每次显示都补发一次。
  onPageShow(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'clip');
    this.announceReady();
  }
  // 卡片已经开着时 PC 再次触发取剪贴板：openClipCard 会 +1，这里补发一次 READY。
  // 否则 onPageShow 不重跑，PC 收不到就绪回报，只能用上次的旧坐标点。
  @StorageLink('clipPing') @Watch('onClipPing') clipPing: number = 0;

  // 「位置已确定」信号：ClipWindow 在浮窗 STARTED 且主窗口退后台之后 +1。
  // 2026-11-12 01:28 为什么单开一路：浮窗刚起来时 screenOffset 只有窗口内偏移（实测 143,143），
  // 拿它点必空 —— 用户反馈的「第一次点不动、第二次才成功」就是这么来的。
  // 收到本信号时报出的才是「浮窗屏幕位置 + 按钮中心」的真值。
  @StorageLink('clipSettled') @Watch('onClipSettled') clipSettled: number = 0;

  private onClipSettled(): void {
    if (this.clipSettled > 0) {
      this.announceReady(0, true);
    }
  }

  private onClipPing(): void {
    if (this.clipPing > 0) {
      this.announceReady();
    }
  }

  private msgHandler: (info: socket.SocketMessageInfo) => void =
    (info: socket.SocketMessageInfo): void => {
      this.onMessage(info);
    };

  aboutToAppear(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'clip');
    // 容器形态由 ClipWindow / EntryAbility 在加载本页之前写好：
    //   'float' = floatView 浮窗 · 'sub' = 自建子窗口 · 'win' = 主窗口本身就是小浮窗 · 'full' = 整页兜底
    // 前三种面积都等于卡片面积，页内卡片要铺满。
    const surface: string = AppStorage.get<string>('clipSurface') ?? 'full';
    this.fillCard = (surface === 'float' || surface === 'sub' || surface === 'win');
    this.iconOnly = (surface === 'float');
    // 浮窗位置由系统摆放，且可能晚于本页出现才回调 —— 所以这里不缓存，
    // 真正上报 READY 时再实时读（见 readyPayload）。
    // 自动模式有两个来源，任一成立即可：
    //   ① 热启动：Index 收到 clipReq 后 router.pushUrl 过来，带 params.auto；
    //   ② 冷启动：EntryAbility 直接把本页 loadContent 成首屏（见 onWindowStageCreate），
    //      此时没有 router 参数，靠 AppStorage 的 clipAuto 标记。
    try {
      const p: Record<string, Object> = this.getUIContext().getRouter().getParams() as Record<string, Object>;
      if (p !== undefined && p !== null && `${p['auto']}` === 'true') {
        this.autoMode = true;
      }
    } catch (e) {
      // 作为根页面加载时 getParams 可能抛，忽略
    }
    if (AppStorage.get<boolean>('clipAuto') === true) {
      this.autoMode = true;
    }
    this.openSocket();
  }

  aboutToDisappear(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'index');
    // 消费掉「本次是 PC 来取剪贴板」这个标记：否则 Index 会一直以为自己在 clip 流程里，
    // 永远不恢复全屏，用户手动打开 App 就会看到一张小卡片。
    AppStorage.setOrCreate<boolean>('clipAuto', false);
    this.clearAckTimer();
    const s: socket.UDPSocket | null = this.udp;
    this.udp = null;
    if (s !== null) {
      s.off('message', this.msgHandler);
      s.close().catch((): void => {});
    }
  }

  // 告诉 PC「剪贴板页已经在前台，可以点 PasteButton 了」，并顺手把按钮的真实中心坐标带回去。
  // 没绑好 socket 时静默丢弃：PC 端 READY 超时后仍会点一次，功能不因此中断。
  private announceReady(retry: number = 0, settled: boolean = false): void {
    const s: socket.UDPSocket | null = this.udp;
    if (s === null) {
      return;
    }
    // 已经发过 settled 的那条了，排队中的旧重试链直接作废 ——
    // 否则它会用「位置未定」的坐标再播一次，把 PC 刚拿到的准坐标覆盖掉。
    if (!settled && this.clipSettled > 0) {
      return;
    }
    // 本页不再动窗口，进前台时布局已经是最终形态，可以直接量坐标上报。
    const payload: string = this.readyPayload(settled);
    // 布局还没量出坐标时先别报：PC 拿到 0 会退回它自己的硬编码常量，布局一改就点偏。
    // 浮窗还要多等一步 —— 它的大小位置由系统摆放，onRectChange 可能晚于本页出现，
    // 位置没确定就上报等于给 PC 一个偏掉的坐标。
    // settled 信号本身就说明「容器位置已定、坐标是屏幕绝对值」，此时不必再等 clipRectReady ——
    // 否则要等到 600ms 兜底才发，PC 端白等一秒（实测端到端 2538ms 就是这么来的）。
    const rectReady: boolean = settled || AppStorage.get<boolean>('clipRectReady') !== false;
    // 2026-11-12 01:20 浮窗的 rect 回调比冷启动整条链路晚得多（实测 aa start→STARTED 就 870ms），
    // 原来 6×60ms=360ms 的窗口根本等不到，等于没等。浮窗放宽到 20×100ms=2s，其余形态维持短窗口。
    const maxRetry: number = (AppStorage.get<string>('clipSurface') === 'float') ? 20 : 8;
    const retryGap: number = (AppStorage.get<string>('clipSurface') === 'float') ? 100 : 60;
    if ((this.ptX <= 0 || this.ptY <= 0 || !rectReady) && retry < maxRetry) {
      setTimeout((): void => {
        // 只在真正「位置已定」的调用上标记 settled，重试也要带着它
        this.announceReady(retry + 1, settled);
      }, retryGap);
      return;
    }
    hilog.info(DOMAIN, TAG, 'READY send x=%{public}d y=%{public}d settled=%{public}s retry=%{public}d',
      this.ptX, this.ptY, `${settled}`, retry);
    for (const addr of this.buildTargets()) {
      s.send({
        data: payload,
        address: { address: addr, port: CLIP_PORT, family: 1 }
      }).catch((): void => {});
    }
  }

  // 量出粘贴按钮的屏幕中心（px）。量不到就报 0 让 PC 端回退到它自己的常量。
  // 这样布局怎么改（完整页面 / 极简浮层）PC 端都不用跟着改硬编码坐标。
  private readyPayload(settled: boolean = false): string {
    let x: number = 0;
    let y: number = 0;
    try {
      const info = this.getUIContext().getComponentUtils().getRectangleById(BTN_ID);
      const w: number = info.size.width;
      const h: number = info.size.height;
      if (w > 0 && h > 0) {
        // screenOffset = 组件相对**屏幕**的偏移，容器（浮窗/子窗口/小窗）的位置已经算在里面。
        // 2026-11-11 23:04 改用这个：原先走「windowOffset + 缓存窗口左上角」，
        // 而小窗形态下窗口会被用户随时拖动，缓存值立刻过期 → PC 一定点偏。
        // 顺带绕过 getWindowProperties().windowRect 的坑：真机上报 width/height 被放大了
        // 一倍多（窗口实际 246×435px，接口报 815×1448），只有 left/top 可信。
        //
        // 2026-11-12 01:28 注意：浮窗尚未被系统摆位置时，这里返回的是**窗口内**偏移
        // （实测 143,143 = 285/2），不是屏幕坐标。所以必须靠末尾的 settled 标记区分。
        x = Math.round(info.screenOffset.x + w / 2);
        y = Math.round(info.screenOffset.y + h / 2);
      }
    } catch (e) {
      // 布局还没完成或组件还不存在，交给 PC 端兜底
    }
    this.ptX = x;
    this.ptY = y;
    // 第 4 个字段：1 = 容器位置已确定（坐标是屏幕绝对值）；0 = 位置可能还没定，别拿它点。
    return `HCLIP/1-READY ${x} ${y} ${settled ? 1 : 0}`;
  }

  private openSocket(): void {
    if (this.udp !== null) {
      return;
    }
    const s: socket.UDPSocket = socket.constructUDPSocketInstance();
    s.on('message', this.msgHandler);
    s.bind({ address: '0.0.0.0', port: 0, family: 1 })
      .then((): Promise<void> => s.setExtraOptions({ broadcast: true }))
      .then((): void => {
        this.udp = s;
        // onPageShow 早于 bind 完成，那次广播会被丢弃，这里补一次
        this.announceReady();
      })
      .catch((e: BusinessError): void => {
        s.off('message', this.msgHandler);
        s.close().catch((): void => {});
        this.setFail(`网络未就绪：${e.message}`);
      });
  }

  // PC 回执：HCLIP/1-OK <id>
  private onMessage(info: socket.SocketMessageInfo): void {
    try {
      const dec: util.TextDecoder = util.TextDecoder.create('utf-8');
      const txt: string = dec.decodeToString(new Uint8Array(info.message));
      if (txt.startsWith('HCLIP/1-OK ')) {
        const id: string = txt.substring(11).trim();
        if (id === this.waitId) {
          this.acked = true;
          this.clearAckTimer();
          this.setOk(`已发送到电脑（${this.lastInfo}）`);
          // 自动模式：让用户瞥一眼「已发送」就退回原来那个 App。
          // 2026-11-11 用户反馈「动画时间太长」：700ms → 220ms，够看清绿字，不拖节奏。
          this.exitAuto(220);
        }
      }
    } catch (e) {
      // 非本协议的杂包直接忽略
    }
  }

  // 点系统「粘贴」控件：result=SUCCESS 才拿到一次性读权限
  private handlePaste(result: PasteButtonOnClickResult): void {
    if (result !== PasteButtonOnClickResult.SUCCESS) {
      this.setFail('系统未授予剪贴板读取权限，请再点一次');
      this.exitAuto(2000);
      return;
    }
    let data: pasteboard.PasteData;
    try {
      const pb: pasteboard.SystemPasteboard = pasteboard.getSystemPasteboard();
      data = pb.getDataSync();
    } catch (e) {
      const err = e as BusinessError;
      this.setFail(`读取剪贴板失败 code=${err.code}`);
      this.exitAuto(2000);
      return;
    }

    // 文本优先：绝大多数场景复制的是文字，走原来的 UDP 分片（快、无需额外握手）。
    // ⚠ 2026-11-12 真机踩坑：剪贴板里是图片时 getPrimaryText() **不抛异常、直接返回 undefined**，
    // 原写法 `text = data.getPrimaryText(); if (text.length > 0)` 会抛
    // TypeError: Cannot read property length of undefined，整个页面被销毁、浮窗跟着关闭。
    // 所以这里必须显式判 undefined，不能只靠 try/catch。
    let text: string = '';
    try {
      const t: string | undefined = data.getPrimaryText();
      text = (t === undefined || t === null) ? '' : t;
    } catch (e) {
      // 图片/文件类内容调 getPrimaryText 也可能抛，同样忽略后继续尝试图片
      text = '';
    }
    if (text.length > 0) {
      this.sendText(text);
      return;
    }

    // 图片（2026-11-12 新增）：鸿蒙里图片记录的 mime 是 'pixelMap'。
    // 走不了 UDP 分片（大图几千片、丢一片整张报废），改走 common/ImageLink 的 TCP 旁路。
    // 同样要判 undefined：没有图片记录时这个接口也可能返回 undefined 而非 null。
    let pm: image.PixelMap | null = null;
    try {
      const raw: image.PixelMap | undefined = data.getPrimaryPixelMap();
      pm = (raw === undefined || raw === null) ? null : raw;
    } catch (e) {
      pm = null;
    }
    if (pm !== null) {
      // 立刻播一条「已受理」：编码本身要 200~800ms，等编码完再播就赶不上
      // PC 那个 600ms 的「点空了」判定窗口了。
      this.announceBusy();
      this.sendImage(pm);
      return;
    }

    this.setFail('剪贴板里既没有文字也没有图片');
    this.exitAuto(2000);
  }

  /** 播一条「已受理」给 PC（fire-and-forget，丢一条不影响功能）。 */
  private announceBusy(): void {
    const s: socket.UDPSocket | null = this.udp;
    if (s === null) {
      return;
    }
    const pkt: string = 'HCLIP/1-BUSY img';
    for (const addr of this.buildTargets()) {
      s.send({ data: pkt, address: { address: addr, port: CLIP_PORT, family: 1 } })
        .catch((): void => {});
    }
  }

  /**
   * 图片上行：编码 PNG → 起 TCP 服务 → 广播通知 PC → PC 连上来取走。
   *
   * 为什么先起服务再广播：反过来的话 PC 收到通知立刻连接，而设备侧还没 listen，
   * 第一次必然连不上；虽然 PC 端有重试，但把顺序摆正可以省掉这一轮 RTT。
   */
  private async sendImage(pm: image.PixelMap): Promise<void> {
    const s: socket.UDPSocket | null = this.udp;
    if (s === null) {
      this.setFail('网络未就绪，请退出本页重进');
      return;
    }
    this.busy = true;
    this.statusOk = false;
    this.statusErr = false;
    this.statusText = '正在编码图片…';

    let png: ArrayBuffer;
    try {
      const packer: image.ImagePacker = image.createImagePacker();
      // quality 100 + PNG：PNG 本身无损，quality 只影响 JPEG，这里保持原样即可。
      png = await packer.packing(pm, { format: 'image/png', quality: 100 });
      packer.release();
    } catch (e) {
      const err = e as BusinessError;
      this.setFail(`图片编码失败 code=${err.code}`);
      this.exitAuto(2000);
      return;
    }
    const bytes: Uint8Array = new Uint8Array(png);
    if (bytes.length === 0) {
      this.setFail('图片内容为空');
      this.exitAuto(2000);
      return;
    }
    if (bytes.length > MAX_IMAGE_BYTES) {
      this.setFail(`图片太大（${Math.round(bytes.length / 1048576)}MB），暂不支持`);
      this.exitAuto(2500);
      return;
    }

    const id: string = Date.now().toString(16).slice(-8);
    this.waitId = id;
    this.lastInfo = `${Math.round(bytes.length / 1024)} KB`;
    this.statusText = '正在发送图片…';

    // 先告诉 PC「这一击命中了」：后面还要编码 + 传输，可能超过 PC 的 600ms 判定窗口。
    // 不补这一条，PC 会以为点空了而补点一次，那时浮窗已关，那一击会落到用户底层的 App 上。
    // （handlePaste 里已经发过一条，这里是兜底重播，重复对 PC 无害）
    this.announceBusy();

    // 先挂监听，再广播
    const served: Promise<boolean> = serveImageOnce(bytes, IMG_SERVE_TIMEOUT_MS);
    const pkt: string = `HCLIP/1-IMG ${id} ${bytes.length}`;
    const targets: string[] = this.buildTargets();
    // UDP 广播不保证送达，连发 3 轮（PC 端按 id 去重，重复通知无害）
    for (let round = 0; round < 3; round++) {
      for (const addr of targets) {
        try {
          await s.send({ data: pkt, address: { address: addr, port: CLIP_PORT, family: 1 } });
        } catch (e) {
          // 单个目标失败不影响其它目标
        }
      }
    }

    const ok: boolean = await served;
    this.busy = false;
    if (!ok) {
      this.setFail('图片发送失败 —— 确认电脑上的投屏程序开着、且在同一 Wi-Fi');
      this.exitAuto(2500);
      return;
    }
    this.setOk(`已发送图片到电脑（${this.lastInfo}）`);
    this.exitAuto(700);
  }


  private async sendText(text: string): Promise<void> {
    const s: socket.UDPSocket | null = this.udp;
    if (s === null) {
      this.setFail('网络未就绪，请退出本页重进');
      return;
    }
    this.busy = true;
    this.lastInfo = `${text.length} 字`;
    this.statusOk = false;
    this.statusErr = false;
    this.statusText = '正在发送…';

    try {
      const enc: util.TextEncoder = new util.TextEncoder();
      const bytes: Uint8Array = enc.encodeInto(text);
      const total: number = Math.max(1, Math.ceil(bytes.length / CHUNK_BYTES));
      const b64: util.Base64Helper = new util.Base64Helper();
      const id: string = Date.now().toString(16).slice(-8);
      this.waitId = id;
      this.acked = false;

      const targets: string[] = this.buildTargets();
      for (let i = 0; i < total; i++) {
        const start: number = i * CHUNK_BYTES;
        const end: number = Math.min(bytes.length, start + CHUNK_BYTES);
        const piece: Uint8Array = bytes.slice(start, end);
        const body: string = b64.encodeToStringSync(piece);
        const pkt: string = `HCLIP/1 ${id} ${i + 1} ${total} ${body}`;
        for (const addr of targets) {
          try {
            await s.send({ data: pkt, address: { address: addr, port: CLIP_PORT, family: 1 } });
          } catch (e) {
            // 单个目标失败不影响其它目标
          }
        }
      }

      this.clearAckTimer();
      this.ackTimer = setTimeout((): void => {
        this.ackTimer = -1;
        this.busy = false;
        if (!this.acked) {
          this.setFail('电脑没响应 —— 确认电脑上的投屏程序开着、且在同一 Wi-Fi');
          // 失败也让用户瞥一眼再退，否则自动模式下这行红字根本看不见
          this.exitAuto(2000);
        }
      }, ACK_WAIT_MS);
    } catch (e) {
      this.busy = false;
      const err = e as BusinessError;
      this.setFail(`发送失败：${err.message}`);
      this.exitAuto(2000);
    }
  }

  // 自动模式专用：做完就退回后台，把前台还给用户原来那个 App。
  // 用 moveAbilityToBackground 而不是 terminateSelf —— 后者会结束整个 Ability，
  // 把同在进程里的投屏会话一并断掉（HAP 模式下投屏服务就跑在本应用内）。
  private exitAuto(delayMs: number): void {
    if (!this.autoMode) {
      return;
    }
    setTimeout((): void => {
      // 先收子窗口再退后台：反过来的话子窗口会跟着主窗口一起被压到后台，
      // 再调 destroyWindow 就容易撞上「窗口已不可见」的时序问题。
      closeClipCard().then((): void => {
        // 降级/小窗场景（本页就画在主窗口里）：退后台前必须离开本页，
        // 否则用户下次打开 App 会停在这张卡片上。
        const inWindow: boolean = AppStorage.get<string>('clipSurface') === 'win';
        const needLeave: boolean = AppStorage.get<boolean>('clipFallback') === true || inWindow;
        if (needLeave) {
          AppStorage.setOrCreate<boolean>('clipFallback', false);
          // 热启动是小窗里 pushUrl 进来的 → back 回首页，首页的投屏日志与状态都还在。
          // 冷启动是根页面 → 无处可 back，只能 replaceUrl 换成首页。
          if (AppStorage.get<boolean>('clipPushed') === true) {
            AppStorage.setOrCreate<boolean>('clipPushed', false);
            try {
              this.getUIContext().getRouter().back();
            } catch (e) {
              // router 已失效，忽略
            }
          } else {
            try {
              this.getUIContext().getRouter().replaceUrl({ url: 'pages/Index' })
                .catch((): void => {});
            } catch (e) {
              // router 已失效，忽略
            }
          }
        }
        try {
          const ctx = this.getUIContext().getHostContext() as common.UIAbilityContext;
          ctx.moveAbilityToBackground()
            .then((): void => {
              // 一次性标记用完即清：否则用户下次手动打开 App 还会落进浮层。
              AppStorage.setOrCreate<boolean>('clipAuto', false);
            })
            .catch((): void => {
              AppStorage.setOrCreate<boolean>('clipAuto', false);
            });
        } catch (e) {
          // 已经在后台或被系统拒绝，忽略
        }
      });
    }, delayMs);
  }

  // 全局广播 + 各网卡的子网定向广播（有些网络只转发后者）
  private buildTargets(): string[] {
    const list: string[] = ['255.255.255.255'];
    try {
      const net: connection.NetHandle = connection.getDefaultNetSync();
      const props: connection.ConnectionProperties = connection.getConnectionPropertiesSync(net);
      for (const la of props.linkAddresses) {
        const addr: string = la.address.address;
        const prefix: number = la.prefixLength;
        if (addr.indexOf('.') > 0 && prefix > 0 && prefix < 32) {
          const parts: string[] = addr.split('.');
          let ip: number = ((parseInt(parts[0]) << 24) | (parseInt(parts[1]) << 16) |
            (parseInt(parts[2]) << 8) | parseInt(parts[3])) >>> 0;
          const mask: number = prefix === 0 ? 0 : (0xFFFFFFFF << (32 - prefix)) >>> 0;
          const bc: number = (ip | (~mask >>> 0)) >>> 0;
          const bcast: string = `${(bc >>> 24) & 0xff}.${(bc >>> 16) & 0xff}.` +
            `${(bc >>> 8) & 0xff}.${bc & 0xff}`;
          if (bcast !== '255.255.255.255' && list.indexOf(bcast) < 0) {
            list.push(bcast);
          }
        }
      }
    } catch (e) {
      // 拿不到网卡信息就只发全局广播
    }
    return list;
  }

  private clearAckTimer(): void {
    if (this.ackTimer >= 0) {
      clearTimeout(this.ackTimer);
      this.ackTimer = -1;
    }
  }

  private setOk(msg: string): void {
    this.busy = false;
    this.statusOk = true;
    this.statusErr = false;
    this.statusText = msg;
  }

  private setFail(msg: string): void {
    this.busy = false;
    this.statusOk = false;
    this.statusErr = true;
    this.statusText = msg;
  }

  private tone(): string {
    if (this.statusOk) {
      return C_OK;
    }
    return this.statusErr ? C_DANGER : C_SUB;
  }

  // 2026-11-11 02:26 按用户要求去掉页面切换动画：
  // 这一段是纯呈现层调整 —— 弹层本身已经是「遮罩 + 小卡片」，用户看到的应该是
  // 「唰一下弹出来、做完唰一下消失」，而不是一次页面推入/推回。
  // ArkUI 的 pushUrl / back 默认带约 350ms 的转场（位移 + 淡入淡出），把进、出都设为
  // duration 0 即可彻底关掉，视觉上等价于弹窗。注意这只影响本页，不影响其它页面。
  pageTransition() {
    PageTransitionEnter({ duration: 0 })
    PageTransitionExit({ duration: 0 })
  }

  build() {
    if (this.autoMode) {
      if (this.iconOnly) {
        this.IconCard()
      } else {
        this.AutoOverlay()
      }
    } else {
      this.FullView()
    }
  }

  // 浮窗形态：只有一枚图标按钮，没有标题、没有状态文字（用户 2026-11-12 00:42 要求）。
  // 系统标题栏（HarmLink + ×）由浮窗模板自带，应用侧关不掉，所以这里不再自己加任何文字，
  // 整张卡片就是一块白底圆角 + 居中的「粘贴」图标。
  @Builder
  IconCard() {
    Column() {
      PasteButton({ icon: PasteIconStyle.LINES })
        .id(BTN_ID)
        .onClick((event: ClickEvent, result: PasteButtonOnClickResult): void => {
          this.handlePaste(result);
        })
    }
    .width('100%')
    .height('100%')
    .justifyContent(FlexAlign.Center)
    .alignItems(HorizontalAlign.Center)
    .padding(8)
    .backgroundColor('#FAFFFFFF')
    .borderRadius(24)
  }

  // PC 触发（--ps clip 1）时的形态：一张居中的小卡片，**没有全屏遮罩**。
  // 2026-11-11 21:57 用户反馈「过度的界面太多」：原来是一整屏压 40% 黑 + 卡片，
  // 观感上就是「切了个界面」。现在窗口本身被缩成居中小卡片（见 common/ClipWindow.ets），
  // 背景设成透明，底下直接透出用户原来的 App —— 视觉上等价于系统弹窗，没有整屏切换。
  // 兜底：设备不支持自由窗口时窗口仍是全屏，这时就是「一张悬浮卡片浮在画面上」，
  // 也比整屏压黑要轻。
  @Builder
  AutoOverlay() {
    Column() {
      Column({ space: this.fillCard ? 8 : 14 }) {
        Text('发送剪贴板到电脑')
          .fontSize(13)
          .fontColor(C_SUB)
        PasteButton({ icon: PasteIconStyle.LINES, text: PasteDescription.PASTE })
          .id(BTN_ID)
          .onClick((event: ClickEvent, result: PasteButtonOnClickResult): void => {
            this.handlePaste(result);
          })
        Row({ space: 6 }) {
          if (this.busy) {
            LoadingProgress()
              .width(12)
              .height(12)
              .color(C_PRIMARY)
          }
          Text(this.statusText)
            .fontSize(11.5)
            .fontColor(this.tone())
            .maxLines(2)
            .textAlign(TextAlign.Center)
        }
        .justifyContent(FlexAlign.Center)
      }
      // 跑在子窗口里时铺满整个窗口（窗口本身就是卡片大小），
      // 否则页内再套一张卡会和窗口底色叠成「双框」—— 2026-11-11 22:25 真机截图实测。
      .width(this.fillCard ? '100%' : '90%')
      .height(this.fillCard ? '100%' : undefined)
      .constraintSize({ maxWidth: this.fillCard ? 4000 : 320 })
      .justifyContent(FlexAlign.Center)
      .padding(this.fillCard
        ? { left: 12, right: 12, top: 8, bottom: 8 }
        : { left: 20, right: 20, top: 16, bottom: 16 })
      .backgroundColor('#FAFFFFFF')
      .borderRadius(this.fillCard ? 24 : 20)
      .shadow(this.fillCard
        ? { radius: 0, color: '#00000000' }
        : { radius: 26, color: '#40000000', offsetY: 8 })
    }
    .width('100%')
    .height('100%')
    .justifyContent(FlexAlign.Center)
    .alignItems(HorizontalAlign.Center)
    .backgroundColor('#00000000')
  }

  @Builder
  FullView() {
    Column() {
      // 顶栏
      Row({ space: 10 }) {
        Text('‹')
          .fontSize(24)
          .fontColor(C_TITLE)
          .width(32)
          .textAlign(TextAlign.Center)
          .onClick((): void => {
            router.back();
          })
        Text('剪贴板同步')
          .fontSize(18)
          .fontWeight(FontWeight.Medium)
          .fontColor(C_TITLE)
          .layoutWeight(1)
      }
      .width('100%')
      .padding({ left: 8, right: 16, top: 8, bottom: 8 })

      Scroll() {
        Column({ space: 14 }) {
          // 说明卡
          Column({ space: 8 }) {
            Text('怎么用')
              .fontSize(14)
              .fontWeight(FontWeight.Medium)
              .fontColor(C_TITLE)
              .width('100%')
            Text('1. 在任何 App 里复制文字或图片')
              .fontSize(13)
              .fontColor(C_BODY)
              .width('100%')
            Text('2. 回到这里，点下面的「粘贴」')
              .fontSize(13)
              .fontColor(C_BODY)
              .width('100%')
            Text('3. 电脑上直接 Ctrl+V')
              .fontSize(13)
              .fontColor(C_BODY)
              .width('100%')
            Text('电脑端不需要任何操作，只要投屏程序开着、和这台平板连同一个 Wi-Fi。')
              .fontSize(11.5)
              .fontColor(C_SUB)
              .width('100%')
              .margin({ top: 4 })
          }
          .width('100%')
          .padding(16)
          .backgroundColor(C_CARD)
          .borderRadius(20)

          // 主按钮卡
          Column({ space: 14 }) {
            PasteButton({ icon: PasteIconStyle.LINES, text: PasteDescription.PASTE })
              .id(BTN_ID)
              .onClick((event: ClickEvent, result: PasteButtonOnClickResult): void => {
                this.handlePaste(result);
              })

            Row({ space: 8 }) {
              if (this.busy) {
                LoadingProgress()
                  .width(14)
                  .height(14)
                  .color(C_PRIMARY)
              }
              Text(this.statusText)
                .fontSize(12.5)
                .fontColor(this.tone())
                .layoutWeight(1)
                .textAlign(TextAlign.Center)
              if (this.lastInfo.length > 0) {
                Text(this.lastInfo)
                  .fontSize(11)
                  .fontColor(C_SUB)
              }
            }
            .width('100%')
            .justifyContent(FlexAlign.Center)
          }
          .width('100%')
          .padding({ left: 16, right: 16, top: 22, bottom: 18 })
          .backgroundColor(C_CARD)
          .borderRadius(20)
          .alignItems(HorizontalAlign.Center)

          Text('受鸿蒙隐私策略限制，读剪贴板必须由你亲自点系统「粘贴」按钮，应用无法后台静默读取。')
            .fontSize(11)
            .fontColor(C_SUB)
            .width('100%')
            .padding({ left: 4, right: 4 })
        }
        .width('100%')
        .padding({ left: 16, right: 16, bottom: 24 })
      }
      .layoutWeight(1)
      .align(Alignment.Top)
      .scrollBar(BarState.Off)
    }
    .width('100%')
    .height('100%')
    .linearGradient({
      angle: 180,
      colors: [[C_BG_TOP, 0], [C_BG_BOTTOM, 1]]
    })
  }
}
