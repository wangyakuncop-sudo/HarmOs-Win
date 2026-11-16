// ClipSetAbility —— PC → 平板 剪贴板的落点（2026-11-11 21:40 新增）。
//
// 为什么要有这个 ability：
//   鸿蒙禁止第三方应用「静默」写系统剪贴板，`pasteboard.setData()` 只能由设备端应用自己执行。
//   而参考采集模式（默认 --capture ref）下我们的 HAP 不参与投屏、平时也不在前台，
//   没有可执行点 —— PC 侧再怎么发 hdc 也只能做到「模拟敲字」（uitest uiInput inputText），
//   敲字必须已有焦点输入框，且传不了图片/富文本。
//   所以由 PC 用 `aa start` 拉起一个**没有窗口**的 ability，它在 onCreate 里把 PC 递过来的
//   base64 文本解码 → setData 写进系统剪贴板 → 立刻 terminateSelf 退出。
//
// 为什么不复用 EntryAbility：
//   EntryAbility 有 windowStage、会加载首页，拉起时平板屏幕会闪一下我们的界面，
//   用户明确要求「不希望平板弹出新的页面」。本 ability 不重写 onWindowStageCreate，
//   没有任何窗口与页面，只为完成一次剪贴板写入。
//
// 参数：`aa start -a ClipSetAbility -b com.harmos.mirror --ps cliptext <base64(UTF-8)>`
//   用 base64 而不是明文，是为了让文本里的空格/引号/换行在 want 参数里保持字面量。
//   长度上限见 PC 侧 ClipTextInjector.SetClipMaxBase64Chars，超长由 PC 自动退回敲字。
import { UIAbility, Want, AbilityConstant } from '@kit.AbilityKit';
import { pasteboard, BusinessError } from '@kit.BasicServicesKit';
import { image } from '@kit.ImageKit';
import { util } from '@kit.ArkTS';
import { receiveImageOnce } from '../common/ImageLink';
import { hilog } from '@kit.PerformanceAnalysisKit';

const DOMAIN: number = 0x0A01;
const TAG: string = 'HarmLinkClipSet';

/** 写入完成后停留一点时间再退出，避免进程被回收时打断未落盘的 setData。 */
const EXIT_DELAY_MS: number = 120;

/** 等 PC 把图片推上来。PC 侧会先 aa start 再连，正常 1s 内就绪，12s 是保险。 */
const IMG_RECV_TIMEOUT_MS: number = 12000;

export default class ClipSetAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(DOMAIN, TAG, 'ClipSetAbility onCreate');
    this.applyAndExit(want);
  }

  // 热启动（进程已在、ability 又被拉起）不会走 onCreate。
  onNewWant(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(DOMAIN, TAG, 'ClipSetAbility onNewWant');
    this.applyAndExit(want);
  }

  private applyAndExit(want: Want): void {
    // 图片模式（2026-11-12 新增）：`--ps clipimg 1`
    // 图片本体不走 want（base64 后几 MB，超命令行长度上限），
    // 本 ability 只负责起来后监听 18768，等 PC 连上来推 PUT。
    if (ClipSetAbility.wantFlag(want, 'clipimg')) {
      this.receiveImage();
      return;
    }

    const text: string = ClipSetAbility.readText(want);
    if (text.length === 0) {
      hilog.warn(DOMAIN, TAG, 'want 里没有可用文本，直接退出');
      this.exitSoon(0);
      return;
    }
    try {
      const data: pasteboard.PasteData = pasteboard.createData('text/plain', text);
      pasteboard.getSystemPasteboard().setData(data)
        .then((): void => {
          hilog.info(DOMAIN, TAG, `写剪贴板成功 ${text.length} 字`);
          this.exitSoon(EXIT_DELAY_MS);
        })
        .catch((e: BusinessError): void => {
          // 27787278 = Replication is prohibited：后台/受限场景会被拒，必须把码打出来，
          // 否则 PC 侧只能看到「指令下发成功」而不知道剪贴板其实没写进去。
          hilog.error(DOMAIN, TAG, `写剪贴板失败 code=${e.code} msg=${e.message}`);
          this.exitSoon(EXIT_DELAY_MS);
        });
    } catch (e) {
      hilog.error(DOMAIN, TAG, `写剪贴板异常: ${String(e)}`);
      this.exitSoon(0);
    }
  }

  /**
   * 图片下行：监听 18768 收一张 PNG → 解成 PixelMap → 写系统剪贴板 → 退出。
   *
   * 为什么用 'pixelMap' 这个 mime：鸿蒙的 PasteData 里图片记录就是它
   * （见 @ohos.pasteboard.d.ts MIMETYPE_PIXELMAP = 'pixelMap'），
   * 写进去之后其它 App 长按输入框能直接粘出图片。
   */
  private receiveImage(): void {
    hilog.info(DOMAIN, TAG, `等待 PC 推送图片（端口 18768，超时 ${IMG_RECV_TIMEOUT_MS}ms）`);
    receiveImageOnce(IMG_RECV_TIMEOUT_MS)
      .then((png: Uint8Array | null): Promise<void> => {
        if (png === null || png.length === 0) {
          hilog.warn(DOMAIN, TAG, '没收到图片，直接退出');
          return Promise.reject(new Error('no-image'));
        }
        const ab: ArrayBuffer = new ArrayBuffer(png.length);
        new Uint8Array(ab).set(png);
        const src: image.ImageSource = image.createImageSource(ab);
        return src.createPixelMap()
          .then((pm: image.PixelMap): Promise<void> => {
            const data: pasteboard.PasteData = pasteboard.createData('pixelMap', pm);
            return pasteboard.getSystemPasteboard().setData(data);
          })
          .then((): void => {
            hilog.info(DOMAIN, TAG, `写图片到剪贴板成功 ${png.length} 字节`);
            src.release();
          })
          .catch((e: BusinessError): void => {
            hilog.error(DOMAIN, TAG, `写图片剪贴板失败 code=${e.code} msg=${e.message}`);
            src.release();
          });
      })
      .catch((e: BusinessError): void => {
        hilog.error(DOMAIN, TAG, `图片接收流程结束：${e.message}`);
      })
      .finally((): void => {
        this.exitSoon(EXIT_DELAY_MS);
      });
  }

  private exitSoon(delayMs: number): void {
    setTimeout((): void => {
      this.context.terminateSelf().catch((): void => {});
    }, delayMs);
  }

  /** want 里的开关参数（`--ps clipimg 1`）。`--ps` 传进来的类型不稳定，统一字符串比较。 */
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

  /**
   * 从 want.parameters.cliptext 取 base64 并解成 UTF-8 文本。
   * --ps 传进来的值类型不稳定（string / number），统一 `${v}` 转字符串再解，
   * 与 EntryAbility.wantClip 的处理方式一致。
   */
  private static readText(want: Want): string {
    const p: Record<string, Object> | undefined = want.parameters;
    if (p === undefined || p === null) {
      return '';
    }
    const v: Object | undefined = p['cliptext'];
    if (v === undefined || v === null) {
      return '';
    }
    const b64: string = `${v}`;
    if (b64.length === 0) {
      return '';
    }
    try {
      const helper: util.Base64Helper = new util.Base64Helper();
      const bytes: Uint8Array = helper.decodeSync(b64);
      const decoder: util.TextDecoder = util.TextDecoder.create('utf-8');
      return decoder.decodeToString(bytes);
    } catch (e) {
      hilog.error(DOMAIN, TAG, `base64 解码失败: ${String(e)}`);
      return '';
    }
  }
}
