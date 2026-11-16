// GATE-L0 网络探针页：启动 HWP v1 UDP 服务，等待 PC 端 scripts/l0_probe.py 驱动测量。
// 与 GATE-01 探针完全隔离，避免破坏已验证的采集功能。
// 视觉与 SessionPage / Index 统一：头部状态胶囊 → 主控卡 → 指标卡 2×2 → 信息标签 → 日志卡。

import { BusinessError } from '@kit.BasicServicesKit';
import { UdpTransport, TransportStats } from '../transport/UdpTransport';
import { LOCAL_PORT, PEER_HOST, PEER_PORT } from '../transport/HwpPacket';
import {
  C_BG_TOP, C_BG_BOTTOM, C_CARD, C_TITLE, C_BODY, C_SUB, C_PRIMARY,
  C_DANGER, C_WARN, C_OK, C_NEUTRAL, C_FILL_CHIP, C_FILL_SOFT, C_FILL_BLUE, C_FILL_DARK, C_FILL_GREEN,
  SHADOW_CARD, SHADOW_CHIP, logColor, timePrefix
} from '../common/Theme';

const MAX_LOG_LINES: number = 60;

@Styles
function probeCard() {
  .width('100%')
  .padding(16)
  .backgroundColor(C_CARD)
  .borderRadius(20)
  .shadow(SHADOW_CARD)
}

interface ProbeStatCard {
  cardLabel: string;
  cardValue: string;
  cardUnit: string;
  cardTone: string;
}

@Entry
@Component
struct NetworkProbePage {
  @State running: boolean = false;
  @State statusText: string = '未启动';
  @State logs: string[] = [];
  @State showLogs: boolean = true;
  // 指标
  @State sentValue: string = '0';
  @State recvValue: string = '0';
  @State videoValue: string = '0';
  @State errValue: string = '0';
  // 信息标签
  @State modeText: string = 'IDLE';
  @State hbText: string = '0';
  @State ackText: string = '0';
  @State upText: string = '0s';
  @State peerText: string = '未握手';

  private transport: UdpTransport = new UdpTransport();
  private statTimer: number = -1;
  private logScroller: Scroller = new Scroller();

  aboutToAppear(): void {
    this.transport.setOnLog((msg: string): void => {
      this.pushLog(msg);
    });
  }

  aboutToDisappear(): void {
    if (this.statTimer >= 0) {
      clearInterval(this.statTimer);
      this.statTimer = -1;
    }
    this.transport.stop().catch((): void => {});
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
    setTimeout((): void => {
      try {
        this.logScroller.scrollEdge(Edge.Bottom);
      } catch (e) {
        // 列表尚未挂载（日志区收起）时忽略
      }
    }, 60);
  }

  private async toggle(): Promise<void> {
    if (this.running) {
      await this.transport.stop();
      if (this.statTimer >= 0) {
        clearInterval(this.statTimer);
        this.statTimer = -1;
      }
      this.running = false;
      this.statusText = '已停止';
      return;
    }
    this.logs = [];
    this.statusText = '启动中…';
    try {
      await this.transport.start();
      this.running = true;
      this.statusText = '服务运行中，等待 PC 下发模式命令';
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

  private refreshStats(): void {
    const s: TransportStats = this.transport.getStats();
    this.modeText = s.mode === 2 ? 'FULL 16Mbps' : (s.mode === 1 ? 'LIGHT 2.3Mbps' : 'IDLE');
    this.sentValue = `${s.sent}`;
    this.recvValue = `${s.received}`;
    this.videoValue = `${s.videoPackets}`;
    this.errValue = `${s.sendErrors}`;
    this.hbText = `${s.heartbeats}`;
    this.ackText = `${s.ackSent}`;
    this.upText = `${s.uptimeSec}s`;
    // 收到过对端包即视为已握手（PC 先发 HELLO / 模式命令）
    this.peerText = s.received > 0 ? '已握手' : '未握手';
  }

  private statusWord(): string {
    if (!this.running) {
      return '未启动';
    }
    return this.peerText === '已握手' ? '测量中' : '等待 PC';
  }

  private statusTone(): string {
    if (!this.running) {
      return C_NEUTRAL;
    }
    return this.peerText === '已握手' ? C_DANGER : C_WARN;
  }

  private hintText(): string {
    if (!this.running) {
      return 'PC 端运行 scripts/l0_probe.py，脚本会依次下发 IDLE / LIGHT / FULL 三档并测 RTT';
    }
    if (this.peerText === '已握手') {
      return '已收到对端数据包，探针正在按 PC 指令分档测量';
    }
    return '本机 UDP 服务已就绪，等待 PC 首次握手';
  }

  @Builder
  statCard($$: ProbeStatCard) {
    Column({ space: 8 }) {
      Row({ space: 6 }) {
        Circle({ width: 6, height: 6 })
          .fill($$.cardTone)
        Text($$.cardLabel)
          .fontSize(12)
          .fontColor(C_SUB)
      }

      Row({ space: 5 }) {
        Text($$.cardValue)
          .fontSize(28)
          .fontWeight(FontWeight.Bold)
          .fontColor(C_TITLE)
        Text($$.cardUnit)
          .fontSize(11)
          .fontColor(C_SUB)
          .margin({ bottom: 5 })
      }
      .alignItems(VerticalAlign.Bottom)
    }
    .alignItems(HorizontalAlign.Start)
    .layoutWeight(1)
    .padding({ left: 14, right: 14, top: 14, bottom: 14 })
    .backgroundColor(C_FILL_SOFT)
    .borderRadius(14)
  }

  @Builder
  infoChip($$: ProbeStatCard) {
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
    Column({ space: 14 }) {
      // ── 头部：返回 + 标题 + 状态胶囊 ──────────────────────
      Row({ space: 12 }) {
        Button('‹')
          .width(38)
          .height(38)
          .fontSize(22)
          .fontColor(C_BODY)
          .backgroundColor(C_CARD)
          .borderRadius(19)
          .shadow(SHADOW_CHIP)
          .onClick((): void => {
            this.getUIContext().getRouter().back();
          })

        Column({ space: 3 }) {
          Row({ space: 8 }) {
            Text('网络探针')
              .fontSize(26)
              .fontWeight(FontWeight.Bold)
              .fontColor(C_TITLE)
            Text('GATE-L0')
              .fontSize(10)
              .fontColor(C_PRIMARY)
              .padding({ left: 6, right: 6, top: 3, bottom: 3 })
              .backgroundColor(C_FILL_BLUE)
              .borderRadius(6)
          }

          Text(`本机 :${LOCAL_PORT}  ·  对端 ${PEER_HOST}:${PEER_PORT}`)
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

      // ── 主控卡 ────────────────────────────────────────────
      Column({ space: 12 }) {
        Row({ space: 8 }) {
          Text('UDP 延迟测量')
            .fontSize(16)
            .fontWeight(FontWeight.Medium)
            .fontColor(C_TITLE)
            .layoutWeight(1)

          Text(this.running ? this.modeText : '待启动')
            .fontSize(11)
            .fontColor(this.running && this.modeText !== 'IDLE' ? C_OK : C_BODY)
            .padding({ left: 8, right: 8, top: 4, bottom: 4 })
            .backgroundColor(this.modeText === 'IDLE' ? C_FILL_CHIP : C_FILL_GREEN)
            .borderRadius(6)
        }
        .width('100%')
        .alignItems(VerticalAlign.Center)

        Button(this.running ? '■  停止 L0 服务' : '▶  启动 L0 服务')
          .width('100%')
          .height(56)
          .fontSize(17)
          .fontWeight(FontWeight.Medium)
          .fontColor('#FFFFFF')
          .backgroundColor(this.running ? C_FILL_DARK : C_PRIMARY)
          .borderRadius(16)
          .onClick((): void => {
            this.toggle().catch((): void => {});
          })

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
      .probeCard()

      // ── 指标卡 2×2 + 信息标签 ─────────────────────────────
      Column({ space: 10 }) {
        Row({ space: 10 }) {
          this.statCard({
            cardLabel: '上行包',
            cardValue: this.sentValue,
            cardUnit: '个',
            cardTone: C_PRIMARY
          })
          this.statCard({
            cardLabel: '下行包',
            cardValue: this.recvValue,
            cardUnit: '个',
            cardTone: this.running ? C_OK : C_NEUTRAL
          })
        }
        .width('100%')

        Row({ space: 10 }) {
          this.statCard({
            cardLabel: '视频包',
            cardValue: this.videoValue,
            cardUnit: '个',
            cardTone: C_PRIMARY
          })
          this.statCard({
            cardLabel: '发送错误',
            cardValue: this.errValue,
            cardUnit: '次',
            cardTone: this.errValue === '0' ? C_NEUTRAL : C_DANGER
          })
        }
        .width('100%')

        Row({ space: 8 }) {
          this.infoChip({ cardLabel: '模式', cardValue: this.modeText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: '对端', cardValue: this.peerText, cardUnit: '', cardTone: '' })
        }
        .width('100%')

        Row({ space: 8 }) {
          this.infoChip({ cardLabel: '心跳', cardValue: this.hbText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: 'ACK', cardValue: this.ackText, cardUnit: '', cardTone: '' })
          this.infoChip({ cardLabel: '时长', cardValue: this.upText, cardUnit: '', cardTone: '' })
        }
        .width('100%')
      }
      .probeCard()

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
              Text('📡')
                .fontSize(26)
              Text('暂无日志')
                .fontSize(13)
                .fontColor(C_BODY)
              Text('启动服务后这里会显示握手、分档与 RTT 过程')
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
      .probeCard()
      .layoutWeight(this.showLogs ? 1 : 0)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 18, right: 18, top: 18, bottom: 14 })
    .alignItems(HorizontalAlign.Start)
    .linearGradient({ angle: 180, colors: [[C_BG_TOP, 0.0], [C_BG_BOTTOM, 1.0]] })
  }
}
