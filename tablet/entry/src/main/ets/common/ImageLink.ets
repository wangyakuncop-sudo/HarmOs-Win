// 剪贴板图片的 TCP 旁路（2026-11-12 新增）。
//
// 为什么不复用已有的 HCLIP/1 UDP 分片：
//   UDP 分片是给文本设计的 —— 单片原文 600B、无重传、无顺序保证、靠广广播。
//   一张 1600x2560 的截图 PNG 通常 1~3MB，要切成 2000~5000 片发出去，
//   丢任何一片整张就报废，而且没有补片机制。这条路对图片不可用。
//
// 所以图片改走 TCP：设备侧监听固定端口，PC 连上来一次性拿走 / 送进来。
// 端口固定 18768，两个方向共用（不会同时发生）。
//
// 协议：首行 ASCII + '\n'，其后跟原始字节
//   上行（设备→PC）：PC 发 `IMG/1 GET <id>\n`，设备回 `IMG/1 PNG <bytes>\n` + PNG 原始字节
//   下行（PC→设备）：PC 发 `IMG/1 PUT <bytes>\n` + PNG 原始字节，设备回 `IMG/1 OK\n`
//
// 为什么用固定端口而不是每次随机：PC 侧不必知道设备选了哪个端口；
// 端口被上一次会话占住时先 close 再 listen（实测鸿蒙会立刻释放）。
import { socket } from '@kit.NetworkKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

const DOMAIN: number = 0x0000;
const TAG: string = 'HarmLinkImg';

/** 与 PC 端 ClipImagePump.Port 必须一致。 */
export const IMG_PORT: number = 18768;

/** TCP 单次 send 的大小。实测 32KB 一块在家庭 Wi-Fi 上稳定，再大容易阻塞。 */
const SEND_CHUNK: number = 32 * 1024;

/** 首行最大长度（`IMG/1 PUT <digits>\n` 最长也就几十字节，留足余量）。 */
const HEADER_MAX: number = 128;

/** 单张图片上限，与 ClipPage.MAX_IMAGE_BYTES 保持一致（防止被塞一个荒谬的长度头）。 */
const MAX_IMAGE_BYTES: number = 20 * 1024 * 1024;

function toArrayBuffer(u8: Uint8Array): ArrayBuffer {
  const ab: ArrayBuffer = new ArrayBuffer(u8.length);
  new Uint8Array(ab).set(u8);
  return ab;
}

function decodeAscii(buf: ArrayBuffer): string {
  const u8: Uint8Array = new Uint8Array(buf);
  let s: string = '';
  for (let i = 0; i < u8.length; i++) {
    s += String.fromCharCode(u8[i]);
  }
  return s;
}

/**
 * 设备侧监听一次，把 png 交给连上来的 PC。
 * PC 发 GET 后开始传输；发完 resolve true，超时/失败 resolve false。
 */
export function serveImageOnce(png: Uint8Array, timeoutMs: number = 10000): Promise<boolean> {
  return new Promise<boolean>((resolve: (v: boolean) => void) => {
    let finished: boolean = false;
    const server: socket.TCPSocketServer = socket.constructTCPSocketServerInstance();
    let conn: socket.TCPSocketConnection | null = null;
    let timer: number = -1;

    const finish = (ok: boolean): void => {
      if (finished) {
        return;
      }
      finished = true;
      if (timer >= 0) {
        clearTimeout(timer);
        timer = -1;
      }
      const c: socket.TCPSocketConnection | null = conn;
      if (ok && c !== null) {
        // 发送成功时不能立刻 close：最后一块还在内核发送缓冲里，直接关会把尾巴截掉，
        // PC 那边就永远凑不满声明的字节数。等 150ms 再关（实测 3MB 图也够）。
        setTimeout((): void => {
          c.close().catch((): void => {});
        }, 150);
      } else if (c !== null) {
        c.close().catch((): void => {});
      }
      server.close().catch((): void => {});
      resolve(ok);
    };

    timer = setTimeout((): void => {
      timer = -1;
      hilog.warn(DOMAIN, TAG, 'serveImageOnce 超时，没有 PC 来取图');
      finish(false);
    }, timeoutMs);

    const sendAll = async (c: socket.TCPSocketConnection): Promise<void> => {
      const total: number = Math.ceil(png.length / SEND_CHUNK);
      for (let i = 0; i < total; i++) {
        const start: number = i * SEND_CHUNK;
        const end: number = Math.min(png.length, start + SEND_CHUNK);
        const piece: Uint8Array = png.slice(start, end);
        await c.send({ data: toArrayBuffer(piece) });
      }
    };

    server.on('connect', (c: socket.TCPSocketConnection): void => {
      conn = c;
      c.on('message', (info: socket.SocketMessageInfo): void => {
        const txt: string = decodeAscii(info.message);
        if (txt.indexOf('IMG/1 GET') < 0) {
          hilog.warn(DOMAIN, TAG, `收到非 GET 请求：${txt.substring(0, 20)}`);
          finish(false);
          return;
        }
        hilog.info(DOMAIN, TAG, `PC 来取图，开始发送 ${png.length} 字节`);
        // 回一行头，再发原始字节；PC 侧靠这一行确定长度，避免依赖 TCP 分包边界。
        const header: string = `IMG/1 PNG ${png.length}\n`;
        const hb: ArrayBuffer = new ArrayBuffer(header.length);
        const hv: Uint8Array = new Uint8Array(hb);
        for (let i = 0; i < header.length; i++) {
          hv[i] = header.charCodeAt(i);
        }
        c.send({ data: hb })
          .then((): Promise<void> => sendAll(c))
          .then((): void => {
            hilog.info(DOMAIN, TAG, '图片发送完成');
            finish(true);
          })
          .catch((e: Object): void => {
            hilog.error(DOMAIN, TAG, `发送失败：${String(e)}`);
            finish(false);
          });
      });
    });

    server.listen({ address: '0.0.0.0', port: IMG_PORT, family: 1 })
      .catch((e: Object): void => {
        hilog.error(DOMAIN, TAG, `监听 ${IMG_PORT} 失败：${String(e)}`);
        finish(false);
      });
  });
}

/**
 * 设备侧监听一次，从连上来的 PC 收一张 PNG。
 * 收齐返回字节，超时/失败返回 null。
 */
export function receiveImageOnce(timeoutMs: number = 10000): Promise<Uint8Array | null> {
  return new Promise<Uint8Array | null>((resolve: (v: Uint8Array | null) => void) => {
    let finished: boolean = false;
    const server: socket.TCPSocketServer = socket.constructTCPSocketServerInstance();
    let conn: socket.TCPSocketConnection | null = null;
    let timer: number = -1;

    // 累积缓冲：TCP 不保证一次 message 就是完整请求，首行与正文都可能被切开。
    // 正文用定长 Uint8Array 而不是 number[]：一张 3MB 的图用数组要吃掉 24MB 堆，
    // 而且逐元素 push 对 20MB 上限来说太慢。
    const headerBuf: number[] = [];
    let headerDone: boolean = false;
    let expect: number = 0;
    let body: Uint8Array | null = null;
    let got: number = 0;

    const finish = (out: Uint8Array | null): void => {
      if (finished) {
        return;
      }
      finished = true;
      if (timer >= 0) {
        clearTimeout(timer);
        timer = -1;
      }
      const c: socket.TCPSocketConnection | null = conn;
      if (c !== null) {
        // 先把 OK 回执发出去再关，PC 端据此确认写入成功
        if (out !== null) {
          const ack: string = 'IMG/1 OK\n';
          const ab: ArrayBuffer = new ArrayBuffer(ack.length);
          const av: Uint8Array = new Uint8Array(ab);
          for (let i = 0; i < ack.length; i++) {
            av[i] = ack.charCodeAt(i);
          }
          c.send({ data: ab }).catch((): void => {});
        }
        setTimeout((): void => {
          c.close().catch((): void => {});
        }, 120);
      }
      server.close().catch((): void => {});
      resolve(out);
    };

    timer = setTimeout((): void => {
      timer = -1;
      hilog.warn(DOMAIN, TAG, 'receiveImageOnce 超时');
      finish(null);
    }, timeoutMs);

    server.on('connect', (c: socket.TCPSocketConnection): void => {
      conn = c;
      c.on('message', (info: socket.SocketMessageInfo): void => {
        const u8: Uint8Array = new Uint8Array(info.message);
        let i: number = 0;
        while (i < u8.length) {
          if (!headerDone) {
            const b: number = u8[i];
            i++;
            if (b === 0x0A) {
              headerDone = true;
              // ArkTS 不支持展开运算符传参，这里用循环拼首行（最多 HEADER_MAX 字节，开销可忽略）
              let h: string = '';
              for (let k = 0; k < headerBuf.length; k++) {
                h += String.fromCharCode(headerBuf[k]);
              }
              const m: RegExpMatchArray | null = h.match(/^IMG\/1 PUT (\d+)/);
              if (m === null) {
                hilog.error(DOMAIN, TAG, `首行不合法：${h.substring(0, 30)}`);
                finish(null);
                return;
              }
              expect = parseInt(m[1], 10);
              if (expect <= 0 || expect > MAX_IMAGE_BYTES) {
                hilog.error(DOMAIN, TAG, `声明长度不合理：${expect}`);
                finish(null);
                return;
              }
              hilog.info(DOMAIN, TAG, `准备接收 ${expect} 字节`);
              body = new Uint8Array(expect);
              got = 0;
            } else if (headerBuf.length < HEADER_MAX) {
              headerBuf.push(b);
            }
          } else {
            const take: number = Math.min(expect - got, u8.length - i);
            if (body !== null) {
              body.set(u8.subarray(i, i + take), got);
            }
            got += take;
            i += take;
            if (got >= expect) {
              hilog.info(DOMAIN, TAG, '图片接收完成');
              finish(body);
              return;
            }
          }
        }
      });
    });

    server.listen({ address: '0.0.0.0', port: IMG_PORT, family: 1 })
      .catch((e: Object): void => {
        hilog.error(DOMAIN, TAG, `监听 ${IMG_PORT} 失败：${String(e)}`);
        finish(null);
      });
  });
}
