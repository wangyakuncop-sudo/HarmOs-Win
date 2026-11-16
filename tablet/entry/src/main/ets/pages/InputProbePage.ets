// 输入注入自测页（调试用，不参与业务）。
//
// 存在的唯一原因：验证 `uitest uiInput inputText <x> <y> '<text>'` 这条命令
// 到底能不能把文本敲进真实输入框，以及中文 / 单引号 / 换行 / emoji / 超长文本
// 各自的转义与分片是否成立。
//
// 为什么不借第三方 App 当试验场：2026-11-21 曾在平板备忘录里做同类验证，
// 结果往用户的私人应用里建了测试笔记 —— 用户明确要求「不要动我的备忘录和微信」。
// 自测一律用自己的页面，不碰用户数据。
//
// 拉起方式（PC 端）：
//   aa start -a EntryAbility -b com.harmos.mirror --ps probe 1
//
// 页面刻意用固定布局：输入框的纵向位置不随内容变化，方便用固定坐标注入。

import { C_BG_TOP, C_BG_BOTTOM, C_CARD, C_TITLE, C_SUB, C_PRIMARY, C_FILL_SOFT } from '../common/Theme';
import { sendTextProbe } from '../accessibility/LowFreqBridge';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { loadExtLibrary, callExtInit } from 'libharmoscap.so';

@Entry
@Component
struct InputProbePage {
  @State inputText: string = '';
  @State echo: string = '（尚未收到输入）';
  @State charCount: number = 0;
  @State multi: string = '';
  @State multiCount: number = 0;
  @State extResult: string = '（未测试）';
  @State extBusy: boolean = false;

  aboutToAppear(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'inputprobe');
    hilog.info(0x0000, 'HarmLink', 'InputProbePage aboutToAppear');
    const req: number | undefined = AppStorage.get<number>('extloadReq');
    hilog.info(0x0000, 'HarmLink', 'InputProbePage extloadReq=%{public}s', `${req}`);
    if (req !== undefined && req > 0) {
      this.runExtProbe();
    }
    // 无障碍 setText 自动自测：进页面 8 秒后往焦点输入框塞一段文本。
    // 为什么不做成纯按钮：点按钮本身会把焦点从 TextInput 抢走，setText 就落不到
    // 目标框上；定时发送能在「用户已点输入框、焦点还在」的窗口里打一发。
    // 只在 --ps probe 1 拉起的调试页里存在，不影响正常业务。
    setTimeout((): void => {
      sendTextProbe('HARMOS-SETTEXT-OK').then((ok: boolean) => {
        this.echo = ok ? 'setText 已发送（8s 自动）' : 'setText 发送失败';
      }).catch(() => {
        this.echo = 'setText 发送异常';
      });
    }, 8000);
  }

  onPageShow(): void {
    AppStorage.setOrCreate<string>('harmlink.page', 'inputprobe');
  }

  private runExtProbe(): void {
    if (this.extBusy) {
      return;
    }
    this.extBusy = true;
    this.extResult = '加载中…';
    hilog.info(0x0000, 'HarmLink', 'runExtProbe: calling loadExtLibrary');
    try {
      const r: string = loadExtLibrary();
      hilog.info(0x0000, 'HarmLink', 'loadExtLibrary => %{public}s', r);
      this.extResult = r;
    } catch (e) {
      this.extResult = `调用异常：${e}`;
    }
    this.extBusy = false;
  }

  private runExtInit(): void {
    this.extBusy = true;
    this.extResult = '调用 OnInit…（可能阻塞）';
    try {
      this.extResult = callExtInit();
    } catch (e) {
      this.extResult = `异常：${e}`;
    }
    this.extBusy = false;
  }

  build() {
    Column() {
      Text('输入注入自测')
        .fontSize(22)
        .fontWeight(FontWeight.Bold)
        .fontColor(C_TITLE)
        .width('100%')
        .margin({ bottom: 6 })

      Text('仅调试用：不写入任何用户数据')
        .fontSize(13)
        .fontColor(C_SUB)
        .width('100%')
        .margin({ bottom: 18 })

      // ↓ 注入目标。位置固定，PC 侧用固定坐标点它。
      TextInput({ text: this.inputText, placeholder: '点这里，然后从电脑推送文本' })
        .id('probeInput')
        .width('100%')
        .height(64)
        .fontSize(17)
        .backgroundColor(C_CARD)
        .borderRadius(14)
        .padding({ left: 16, right: 16 })
        .onChange((v: string) => {
          this.inputText = v;
          this.charCount = v.length;
          this.echo = v.length === 0 ? '（已清空）' : v;
        })

      Text(`当前 ${this.charCount} 字`)
        .fontSize(12)
        .fontColor(C_SUB)
        .width('100%')
        .margin({ top: 8, bottom: 14 })

      // 回显区改成多行 TextArea：单行 TextInput 不接收 ENTER，测不了「多行文本」，
      // 而复制一段话恰恰是最常见的剪贴板内容。两个控件共存，能分别验证单行/多行行为。
      TextArea({ text: this.multi, placeholder: '多行测试区：在上面输入框之后，这里接 ENTER 与长文本' })
        .id('probeArea')
        .width('100%')
        .height(120)
        .fontSize(15)
        .backgroundColor(C_FILL_SOFT)
        .borderRadius(14)
        .padding(16)
        .onChange((v: string) => {
          this.multi = v;
          this.multiCount = v.length;
        })

      Text(`多行区 ${this.multiCount} 字`)
        .fontSize(12)
        .fontColor(C_SUB)
        .width('100%')
        .margin({ top: 14, bottom: 8 })

      Button('清空')
        .width('100%')
        .height(48)
        .fontSize(16)
        .fontColor(Color.White)
        .backgroundColor(C_PRIMARY)
        .borderRadius(14)
        .margin({ top: 14 })
        .onClick(() => {
          this.inputText = '';
          this.echo = '（已清空）';
          this.charCount = 0;
          this.multi = '';
          this.multiCount = 0;
        })

      // 无障碍 setText 自测：先点上面的输入框拿到焦点，再点这个按钮，
      // 看文字是否被直接写进焦点框。用来验证 API 26 的 performAction('setText')
      // 到底可不可用（键盘转发方案的前提）。
      Button('无障碍 setText 自测')
        .width('100%')
        .height(48)
        .fontSize(16)
        .fontColor(C_PRIMARY)
        .backgroundColor(C_FILL_SOFT)
        .borderRadius(14)
        .margin({ top: 10 })
        .onClick(() => {
          const text: string = `setText 自测 ${new Date().toLocaleTimeString()}`;
          sendTextProbe(text).then((ok: boolean) => {
            this.echo = ok ? '已发送 setText 请求（看输入框是否被填）' : '发送失败（服务未开或编码超限）';
          }).catch(() => {
            this.echo = '发送异常';
          });
        })

      Text('扩展库自加载（手机走参考采集）')
        .fontSize(13)
        .fontColor(C_SUB)
        .width('100%')
        .margin({ top: 14, bottom: 6 })

      Row({ space: 10 }) {
        Button('dlopen 探测')
          .layoutWeight(1)
          .height(44)
          .fontSize(14)
          .fontColor(Color.White)
          .backgroundColor(this.extBusy ? C_FILL_SOFT : C_PRIMARY)
          .borderRadius(12)
          .onClick(() => {
            this.runExtProbe();
          })
        Button('调 OnInit')
          .layoutWeight(1)
          .height(44)
          .fontSize(14)
          .fontColor(C_PRIMARY)
          .backgroundColor(C_FILL_SOFT)
          .borderRadius(12)
          .onClick(() => {
            this.runExtInit();
          })
      }
      .width('100%')

      Text(this.extResult)
        .fontSize(11)
        .fontColor(C_SUB)
        .width('100%')
        .margin({ top: 8, bottom: 6 })
        .copyOption(CopyOptions.LocalDevice)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 20, right: 20, top: 24, bottom: 20 })
    .linearGradient({
      angle: 180,
      colors: [[C_BG_TOP, 0], [C_BG_BOTTOM, 1]]
    })
  }
}
