using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarmOsWin.Core;

/// <summary>
/// 剪贴板收件箱：设备 → PC 方向。
///
/// 为什么不用 HWP / hdc：
///   - 鸿蒙禁止应用静默读剪贴板，设备端只能靠系统 PasteButton 安全控件点一次拿到一次性读权限，
///     所以这条数据必然由「平板侧的普通应用」主动发出，不是投屏通道的一部分；
///   - 实测（2026-11-11）纯 hdc 读剪贴板在鸿蒙 7 商用版上不存在（shell uid 2000 无权限、
///     PasteboardService 无 dump、剪贴板库不可读、无 su），因此只能走局域网。
///
/// 传输格式（UTF-8 文本，便于抓包排查）：
///   设备 → PC：<c>HCLIP/1 &lt;id&gt; &lt;seq&gt; &lt;total&gt; &lt;base64分片&gt;</c>
///   PC → 设备：<c>HCLIP/1-OK &lt;id&gt;</c>（收到并写进剪贴板后回执，平板据此显示「已发送」）
///
/// 分片是各自独立 base64 的原文片段，重组时按 seq 顺序逐片解码再拼接。
/// PC 端零交互：收到即写 Windows 剪贴板。
/// </summary>
public sealed class ClipInbox : IDisposable
{
    /// <summary>监听端口，必须与设备端 ClipPage.ets 的 CLIP_PORT 一致。</summary>
    public const int Port = 18766;

    private const string Magic = "HCLIP/1 ";
    private const string AckPrefix = "HCLIP/1-OK ";
    private const string ReadyMagic = "HCLIP/1-READY";
    /// <summary>图片通知（2026-11-12）：HCLIP/1-IMG &lt;id&gt; &lt;bytes&gt;。图片本体走 TCP，见 ClipImagePump。</summary>
    private const string ImageMagic = "HCLIP/1-IMG ";
    /// <summary>
    /// 「已受理」信号（2026-11-12）：HCLIP/1-BUSY &lt;id&gt;。
    ///
    /// 图片要先编码 PNG（几 MB 的截图 200~800ms）才发得出去，而 PullClipboardAsync 的
    /// 「点空了」判定窗口只有 600ms —— 不补这一条，PC 会以为没点中而补点一次，
    /// 那时浮窗已经关了，那一击就落到用户平板底层的 App 上。收到本信号即视为点击已命中。
    /// </summary>
    private const string BusyMagic = "HCLIP/1-BUSY";
    private const int PendingTtlMs = 30_000;
    private const int MaxTextBytes = 4 * 1024 * 1024;

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    // 图片通知去重与串行化：同一张图只拉一次，且同一时刻只跑一条 TCP 传输。
    private readonly object _imageLock = new();
    private string _lastImageId = string.Empty;
    private int _imageBusy;

    /// <summary>成功写入剪贴板后触发：(文本, 来源 IP)。</summary>
    public event Action<string, string>? TextReceived;

    /// <summary>
    /// 平板剪贴板页广播的就绪信号：(来源 IP, 粘贴按钮中心 X, Y, 位置是否已确定)。
    ///
    /// 用途有两个：① PC 侧重「取剪贴板」后先唤起 App，等这个信号到了再注入点击，
    /// 避免页面还没切过去就把点击打到首页上；② 坐标由设备自己量（getRectangleById）后带回，
    /// 布局改了（比如换成极简浮层）PC 端不用跟着改常量。
    ///
    /// 第四项 settled 是 2026-11-12 加的：浮窗刚起来时容器还没被系统摆位置，
    /// 那时量到的是**窗口内**偏移（实测 143,143 = 285/2），拿它点必空
    /// —— 就是用户反馈的「第一次点不动、第二次才成功」。只有 settled=true 的坐标才是屏幕绝对值。
    /// </summary>
    public event Action<string, int, int, bool>? ReadyReceived;

    /// <summary>
    /// 收到一张剪贴板图片（PNG 原始字节, 来源 IP）。2026-11-12 新增。
    /// 本类只负责把字节收齐，写 Windows 剪贴板交给上层 ——
    /// WPF 的 Clipboard.SetImage 必须在 STA 线程上调用，放进 Core 会绑死 UI 框架。
    /// </summary>
    public event Action<byte[], string>? ImageReceived;

    /// <summary>设备端已受理一次取用（当前是图片编码阶段），(来源 IP, 提示语)。</summary>
    public event Action<string, string>? BusyReceived;

    /// <summary>启动失败原因（端口被占用 / 防火墙拦截等），为空表示监听正常。</summary>
    public string? LastError { get; private set; }

    public long ReceivedCount { get; private set; }

    /// <summary>最近一次来源 IP，供界面显示。</summary>
    public string? LastSource { get; private set; }

    private sealed class Pending
    {
        public int Total;
        public long Stamp;
        public IPEndPoint? From;
        public readonly Dictionary<int, byte[]> Parts = new();
    }

    public ClipInbox()
    {
        // 绑定 Any 才能收到设备发往 255.255.255.255 与子网广播地址的包。
        // 显式关掉 ExclusiveAddressUse，便于程序重启时快速重绑（避免 TIME_WAIT/占用冲突）。
        var ep = new IPEndPoint(IPAddress.Any, Port);
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _udp.Client.Bind(ep);
        }
        catch (SocketException e)
        {
            LastError = $"绑定 UDP {Port} 失败：{e.Message}";
        }
    }

    public void Start() => _ = Task.Run(ReceiveLoopAsync);

    private async Task ReceiveLoopAsync()
    {
        if (LastError is not null)
        {
            return;
        }
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 瞬时错误不终止监听，稍后继续（无线网络抖动很常见）。
                // 注意不要把这类错误写到 LastError —— 那是「监听是否建立成功」的唯一判据。
                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                continue;
            }

            try
            {
                Handle(r.Buffer, r.RemoteEndPoint);
            }
            catch
            {
                // 单包异常不影响整体监听
            }

            SweepExpired();
        }
    }

    private void Handle(byte[] buf, IPEndPoint from)
    {
        string text = Encoding.UTF8.GetString(buf);

        // 就绪信号不走分片协议，先单独拦掉：HCLIP/1-READY [x y [settled]]（无 id / seq 字段）
        if (text.StartsWith(ReadyMagic, StringComparison.Ordinal))
        {
            string[] f = text.TrimEnd('\0', '\r', '\n').Split(' ');
            int rx = 0;
            int ry = 0;
            if (f.Length >= 3)
            {
                int.TryParse(f[1], out rx);
                int.TryParse(f[2], out ry);
            }
            // 第 4 字段缺省（老版本设备端）时按「已确定」处理，保持向后兼容
            bool settled = f.Length < 4 || f[3] != "0";
            ReadyReceived?.Invoke(from.Address.ToString(), rx, ry, settled);
            return;
        }

        // 已受理：设备端点了粘贴、正在编码，先告诉上层「这一击命中了」
        if (text.StartsWith(BusyMagic, StringComparison.Ordinal))
        {
            string[] fb = text.TrimEnd('\0', '\r', '\n').Split(' ');
            BusyReceived?.Invoke(from.Address.ToString(), fb.Length >= 2 ? fb[1] : string.Empty);
            return;
        }

        // 图片通知：HCLIP/1-IMG <id> <bytes>（无 seq/total，本体走 TCP）
        // 设备端为了抗 UDP 丢包会连发 3 遍，这里按 id 去重，只拉一次。
        if (text.StartsWith(ImageMagic, StringComparison.Ordinal))
        {
            string[] f = text.TrimEnd('\0', '\r', '\n').Split(' ');
            if (f.Length < 3 || !int.TryParse(f[2], out int nbytes) || nbytes <= 0)
            {
                return;
            }
            string imgId = f[1];
            lock (_imageLock)
            {
                if (imgId == _lastImageId)
                {
                    return;
                }
                _lastImageId = imgId;
            }
            _ = Task.Run(() => FetchImageAsync(imgId, nbytes, from));
            return;
        }

        // 到这里才是分片文本协议：HCLIP/1 <id> <seq> <total> <b64>
        if (!text.StartsWith(Magic, StringComparison.Ordinal))
        {
            return;
        }

        // HCLIP/1 <id> <seq> <total> <b64>
        string[] parts = text.Split(' ', 5);
        if (parts.Length < 5)
        {
            return;
        }
        if (!int.TryParse(parts[2], out int seq) || !int.TryParse(parts[3], out int total))
        {
            return;
        }
        if (total <= 0 || seq <= 0 || seq > total)
        {
            return;
        }

        string id = parts[1];
        string b64 = parts[4];

        if (!_pending.TryGetValue(id, out Pending? p))
        {
            p = new Pending { Total = total, Stamp = Environment.TickCount64, From = from };
            _pending[id] = p;
        }
        p.Stamp = Environment.TickCount64;
        p.From = from;

        if (!p.Parts.ContainsKey(seq))
        {
            byte[] chunk;
            try
            {
                chunk = Convert.FromBase64String(b64);
            }
            catch
            {
                return;
            }
            p.Parts[seq] = chunk;
        }

        if (p.Parts.Count < p.Total)
        {
            return;
        }

        // 全部到齐：按 seq 顺序拼接
        using var ms = new MemoryStream();
        for (int i = 1; i <= p.Total; i++)
        {
            if (!p.Parts.TryGetValue(i, out byte[]? chunk))
            {
                return;
            }
            ms.Write(chunk, 0, chunk.Length);
        }
        _pending.Remove(id);

        byte[] all = ms.ToArray();
        if (all.Length > MaxTextBytes)
        {
            return;
        }

        string body = Encoding.UTF8.GetString(all);
        if (body.Length == 0)
        {
            return;
        }

        Win32Clipboard.SetText(body);
        ReceivedCount++;
        LastSource = from.Address.ToString();

        // 回执必须写成功之后再发，平板据此显示「已发送到电脑」
        try
        {
            byte[] ack = Encoding.UTF8.GetBytes(AckPrefix + id);
            _udp.Send(ack, ack.Length, from);
        }
        catch
        {
            // 回执丢了不影响 PC 端已写入的事实
        }

        TextReceived?.Invoke(body, from.Address.ToString());
    }

    /// <summary>
    /// 收到图片通知后，去设备侧把 PNG 拉回来。
    ///
    /// 设备端 18768 是「收完就关」的一次性服务，所以这里不做连接复用；
    /// 成功与否都由 ClipImagePump 内部兜住，不让异常冒到 UDP 收包循环。
    /// </summary>
    private async Task FetchImageAsync(string id, int nbytes, IPEndPoint from)
    {
        if (Interlocked.CompareExchange(ref _imageBusy, 1, 0) != 0)
        {
            // 上一张还在传：这多半是同一条通知的重播，直接丢弃
            return;
        }
        try
        {
            byte[]? png = await ClipImagePump.FetchAsync(from.Address.ToString(), id, nbytes)
                .ConfigureAwait(false);
            if (png is null || png.Length == 0)
            {
                return;
            }
            ReceivedCount++;
            LastSource = from.Address.ToString();

            // 回执：设备端据此收起卡片（与文本路径共用同一条 ACK）
            try
            {
                byte[] ack = Encoding.UTF8.GetBytes(AckPrefix + id);
                _udp.Send(ack, ack.Length, from);
            }
            catch
            {
                // 回执丢了不影响 PC 端已收到图片的事实
            }

            ImageReceived?.Invoke(png, from.Address.ToString());
        }
        catch
        {
            // 网络抖动：静默失败，设备端会因超时给出红字提示
        }
        finally
        {
            Interlocked.Exchange(ref _imageBusy, 0);
        }
    }

    private void SweepExpired()    {
        if (_pending.Count == 0)
        {
            return;
        }
        long now = Environment.TickCount64;
        List<string>? dead = null;
        foreach (var kv in _pending)
        {
            if (now - kv.Value.Stamp > PendingTtlMs)
            {
                (dead ??= new List<string>()).Add(kv.Key);
            }
        }
        if (dead is null)
        {
            return;
        }
        foreach (var k in dead)
        {
            _pending.Remove(k);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }
        try
        {
            _udp.Dispose();
        }
        catch
        {
        }
        _cts.Dispose();
    }
}
