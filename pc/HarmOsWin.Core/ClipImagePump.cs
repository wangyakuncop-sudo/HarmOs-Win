using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarmOsWin.Core;

/// <summary>
/// 剪贴板图片的 TCP 旁路（2026-11-12 新增）。与设备端 tablet/entry/src/main/ets/common/ImageLink.ets 配对。
///
/// 为什么图片不走现有的 HCLIP/1 UDP 分片：
///   那套协议是给文本设计的 —— 单片原文 600B、无重传、无顺序保证、靠广播。
///   一张 1600x2560 的截图 PNG 通常 1~3MB，要切成 2000~5000 片发出去，
///   丢任何一片整张就报废。所以图片改走 TCP 点对点。
///
/// 协议（首行 ASCII + '\n'，其后原始字节）：
///   拉取：PC 发 <c>IMG/1 GET &lt;id&gt;\n</c> → 设备回 <c>IMG/1 PNG &lt;bytes&gt;\n</c> + PNG
///   推送：PC 发 <c>IMG/1 PUT &lt;bytes&gt;\n</c> + PNG → 设备回 <c>IMG/1 OK\n</c>
///
/// 设备 IP 从哪来：拉取方向用 UDP 包的来源地址；推送方向用 hdc target 里的 IP。
/// </summary>
public static class ClipImagePump
{
    /// <summary>与设备端 ImageLink.IMG_PORT 必须一致。</summary>
    public const int Port = 18768;

    private const int ConnectRetry = 20;
    private const int ConnectRetryGapMs = 150;
    private const int MaxImageBytes = 24 * 1024 * 1024;

    /// <summary>
    /// 从设备取一张图片。
    /// 设备侧是「先 listen 后广播」，正常情况第一次就能连上；重试只覆盖
    /// 「UDP 通知先到、listen 还没生效」这一小段窗口。
    /// </summary>
    public static async Task<byte[]?> FetchAsync(string deviceIp, string id, int expectedBytes,
        int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        TcpClient? tcp = null;
        try
        {
            tcp = await ConnectAsync(deviceIp, cts.Token).ConfigureAwait(false);
            if (tcp is null)
            {
                return null;
            }
            var ns = tcp.GetStream();

            byte[] req = Encoding.ASCII.GetBytes($"IMG/1 GET {id}\n");
            await ns.WriteAsync(req, cts.Token).ConfigureAwait(false);
            await ns.FlushAsync(cts.Token).ConfigureAwait(false);

            string? header = await ReadLineAsync(ns, cts.Token).ConfigureAwait(false);
            if (header is null || !header.StartsWith("IMG/1 PNG ", StringComparison.Ordinal))
            {
                return null;
            }
            if (!int.TryParse(header.AsSpan(10), out int len) || len <= 0 || len > MaxImageBytes)
            {
                return null;
            }
            // 头部声明的长度与 UDP 通知里的一致才继续：不一致说明设备侧状态错位了
            if (expectedBytes > 0 && len != expectedBytes)
            {
                return null;
            }

            byte[] buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await ns.ReadAsync(buf.AsMemory(got), cts.Token).ConfigureAwait(false);
                if (n <= 0)
                {
                    return null;
                }
                got += n;
            }
            return buf;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            tcp?.Dispose();
        }
    }

    /// <summary>
    /// 把一张 PNG 推给设备。设备侧由 ClipSetAbility 拉起后监听固定端口，
    /// 所以这里必须容忍「能力还没起来」的那几百毫秒，靠重试连上。
    /// </summary>
    public static async Task<bool> PushAsync(string deviceIp, byte[] png, int timeoutMs = 15000)
    {
        if (png.Length == 0 || png.Length > MaxImageBytes)
        {
            return false;
        }
        using var cts = new CancellationTokenSource(timeoutMs);
        TcpClient? tcp = null;
        try
        {
            tcp = await ConnectAsync(deviceIp, cts.Token).ConfigureAwait(false);
            if (tcp is null)
            {
                return false;
            }
            var ns = tcp.GetStream();

            byte[] head = Encoding.ASCII.GetBytes($"IMG/1 PUT {png.Length}\n");
            await ns.WriteAsync(head, cts.Token).ConfigureAwait(false);
            await ns.WriteAsync(png, cts.Token).ConfigureAwait(false);
            await ns.FlushAsync(cts.Token).ConfigureAwait(false);

            string? ack = await ReadLineAsync(ns, cts.Token).ConfigureAwait(false);
            return ack is not null && ack.StartsWith("IMG/1 OK", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            tcp?.Dispose();
        }
    }

    private static async Task<TcpClient?> ConnectAsync(string ip, CancellationToken ct)
    {
        for (int i = 0; i < ConnectRetry; i++)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }
            var tcp = new TcpClient();
            try
            {
                using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                one.CancelAfter(1200);
                await tcp.ConnectAsync(ip, Port, one.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                return tcp;
            }
            catch
            {
                tcp.Dispose();
                try
                {
                    await Task.Delay(ConnectRetryGapMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>读一行 ASCII（到 '\n' 为止）。返回 null 表示连接已断。</summary>
    private static async Task<string?> ReadLineAsync(NetworkStream ns, CancellationToken ct)
    {
        var sb = new StringBuilder(32);
        byte[] one = new byte[1];
        while (sb.Length < 64)
        {
            int n = await ns.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                return null;
            }
            if (one[0] == (byte)'\n')
            {
                return sb.ToString();
            }
            if (one[0] != (byte)'\r')
            {
                sb.Append((char)one[0]);
            }
        }
        return null;
    }
}
