// GATE-02：Media Foundation H.264 硬解（CLSID_CMSH264DecoderMFT）→ NV12 → BGRA。
// 与官方多屏协同 PC 端同栈（Media Foundation），不使用三方解码库。
// 输出为 DecodedFrame(byte[] BGRA)，上屏由 WPF 侧 WriteableBitmap 完成。
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HarmOsWin.Core;

public sealed class MediaFoundationDecoder : IVideoDecoder, IDisposable
{
    private readonly object _gate = new();

    // 分辨率到达前收到的帧先挂起，等分辨率/SPS 一到位立刻回放。
    // 实测：PC 新进程接入时，重放的关键帧常比 CMD_RESOLUTION 早到 2~6 秒，
    // 旧逻辑会把它们当"解码失败"丢掉，只能干等下一个 GOP（首帧 6.3s / 13.1s）。
    private readonly Queue<byte[]> _pending = new();
    private const int MaxPendingFrames = 8;
    private bool _flushingPending;
    private IMFTransform? _mft;
    private bool _mfStarted;
    // 进程级 MF 平台引用计数（见 Initialize 里的注释：双路各自 MFShutdown 会打断另一路）。
    private static int _mfRefCount;
    private int _width;
    private int _height;
    private int _fps;
    private long _ptsUs;
    private long _decoded;
    private long _errors;
    // H.264 解码器必须先见到 IDR 才能出图；在那之前喂 P 帧既不报错也不出图（实测 dec=0|0 卡 13s）。
    // 所以用一个门闩：没有 IDR 的帧直接跳过，不浪费 ProcessInput，也让问题可见（SkippedNoIdr）。
    private bool _needIdr = true;
    private long _skippedNoIdr;
    // 因 16px 宏块对齐而裁掉填充边的帧数（见 DecodeOneLocked 里的裁剪逻辑）。
    // 正常为 0；devscale 3（533x853→544x864）这类非 16 倍数尺寸才会增长。
    private long _croppedFrames;
    // 最近一组 SPS/PPS（含起始码）。设备把参数集单独成帧发，重放时与 IDR 的先后没有保证；
    // 自己留一份，喂 IDR 时若发现当前帧不带参数集就先补喂一帧，避免「解不出但也不报错」。
    private byte[] _paramSets = Array.Empty<byte>();
    // 收到「裸 IDR」（不带 SPS/PPS）而手上又没参数集时先挂起，等参数集到齐再拼接回放。
    private byte[]? _pendingIdr;
    private long _hungSince;
    private int _dumpCount;
    private readonly string _dumpDir =
        Environment.GetEnvironmentVariable("HARMOS_DUMP_DIR") ?? System.IO.Path.GetTempPath();
    private long _inBytes;
    private int _outWidth;
    private int _outHeight;
    private bool _providesSamples;
    private int _outputBufferSize;
    private byte[] _nv12 = Array.Empty<byte>();
    private byte[] _bgra = Array.Empty<byte>();
    // 性能打点：NV12→BGRA 是纯 CPU 路径（1600x2560 = 4.1M 像素/帧），必须知道它占多少
    private long _convertTicks;
    private long _convertCount;
    private long _feedTicks;
    private long _feedCount;
    // 喂帧明细（前 8 帧）默认关，见 FeedCore 里的注释。排障时设 HARMOS_MF_LOG=1。
    private static readonly bool _verboseFeed =
        Environment.GetEnvironmentVariable("HARMOS_MF_LOG") == "1";
    // vtable 委托缓存。2026-11-13 实测：每帧都调 Marshal.GetDelegateForFunctionPointer
    // 会让进程「提交内存」以约 2.4MB/s 持续上涨（45s 涨 110MB，而工作集不怎么涨，
    // 属于换出到 pagefile 的非托管分配），双路十几分钟后堆被写坏 → 0xc0000374。
    // 同一个 CLSID 的 MFT 家族 vtable 槽位在进程内固定，取一次就够了。
    private LockFn? _fnLock;
    private UnlockFn? _fnUnlock;
    private SetCurrentLengthFn? _fnSetLen;
    private AddBufferFn? _fnAddBuffer;
    private SetSampleTimeFn? _fnSetTime;
    private SetSampleTimeFn? _fnSetDur;
    private ConvertToContiguousBufferFn? _fnConvert;

    public MediaFoundationDecoder(int width = 0, int height = 0, int fps = 30)
    {
        _width = width;
        _height = height;
        _fps = fps <= 0 ? 30 : fps;
    }

    /// <summary>
    /// 本机 H.264 MFT 是否接受 MF_LOW_LATENCY。纯 COM 探测，不触碰设备、
    /// 不需要分辨率，可在窗口构造期调用，用于决定参考采集是否启用补帧兜底。
    /// </summary>
    public static bool LowLatencySupported { get; private set; }

    /// <summary>
    /// 用临时 MFT 探测低延迟属性是否可写可读。GUID 与真正解码时用的是同一个，
    /// 因此结果可直接作为「37 帧内部输出缓冲能否消除」的先验判据。
    /// 参考项目 hos_scrcpy/gui/app.py:798 用 codec.thread_type = "NONE" 达到同一目的。
    /// </summary>
    public static bool ProbeLowLatency()
    {
        Guid clsid = MfGuids.CLSID_CMSH264DecoderMFT;
        Guid iid = MfGuids.IID_IMFTransform;
        try
        {
            int hr = MfApi.CoCreateInstance(ref clsid, IntPtr.Zero,
                MfConstants.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ptr);
            if (hr < 0)
            {
                Console.Error.WriteLine($"[MF] 低延迟探测 CoCreateInstance hr=0x{hr:X8}");
                return LowLatencySupported = false;
            }
            try
            {
                var mft = (IMFTransform)Marshal.GetObjectForIUnknown(ptr);
                try
                {
                    return LowLatencySupported = TrySetLowLatency(mft, "探测");
                }
                finally
                {
                    Marshal.ReleaseComObject(mft);
                }
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MF] 低延迟探测异常：{ex.Message}");
            return LowLatencySupported = false;
        }
    }

    /// <summary>调用方持有 _gate；在真正解码用的 MFT 上启用低延迟（须在 BEGIN_STREAMING 前）。</summary>
    private void ApplyLowLatencyLocked()
    {
        if (_mft is not null)
        {
            LowLatencySupported = TrySetLowLatency(_mft, "解码器");
        }
    }

    private static bool TrySetLowLatency(IMFTransform mft, string tag)
    {
        try
        {
            int hrAttr = mft.GetAttributes(out IMFAttributes attrs);
            if (hrAttr < 0)
            {
                Console.Error.WriteLine($"[MF] {tag} GetAttributes hr=0x{hrAttr:X8}，低延迟未启用");
                return false;
            }
            Guid low = MfGuids.MF_LOW_LATENCY;
            int hrSet = attrs.SetUINT32(ref low, 1);
            int hrGet = attrs.GetUINT32(ref low, out uint value);
            bool ok = hrSet >= 0 && hrGet >= 0 && value != 0;
            Console.Error.WriteLine(
                $"[MF] {tag} 低延迟 Set=0x{hrSet:X8} Get=0x{hrGet:X8} value={value} enabled={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MF] {tag} 低延迟设置异常：{ex.Message}");
            return false;
        }
    }

    public string Name => _mft is null ? "Media Foundation H.264（未初始化）" : "Media Foundation H.264（硬解）";

    public long DecodedFrames => Interlocked.Read(ref _decoded);
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>因「尚未见到 IDR」而被跳过的帧数。持续增长且 DecodedFrames 为 0 说明设备端没给关键帧。</summary>
    public long SkippedNoIdr => Interlocked.Read(ref _skippedNoIdr);
    /// <summary>因 16px 宏块对齐填充而被裁边的帧数（devscale 3 时才会增长）。</summary>
    public long CroppedFrames => Interlocked.Read(ref _croppedFrames);
    public long InputBytes => Interlocked.Read(ref _inBytes);
    public int OutWidth => _outWidth;
    public int OutHeight => _outHeight;

    public event Action<DecodedFrame>? FrameDecoded;

    /// <summary>单帧 NV12→BGRA 平均耗时（毫秒）。60fps 下预算只有 16.6ms，这里必须远小于它。</summary>
    public double AvgConvertMs
    {
        get
        {
            long n = Interlocked.Read(ref _convertCount);
            return n <= 0 ? 0 : Interlocked.Read(ref _convertTicks) * 1000.0 / n / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>单帧 Feed（含 ProcessInput + 解码 + 转换 + 回调）平均耗时（毫秒）。</summary>
    public double AvgFeedMs
    {
        get
        {
            long n = Interlocked.Read(ref _feedCount);
            return n <= 0 ? 0 : Interlocked.Read(ref _feedTicks) * 1000.0 / n / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>设备上报分辨率后调用；分辨率变化会重建解码器。</summary>
    public void Configure(int width, int height)
    {
        lock (_gate)
        {
            if (width <= 0 || height <= 0 || (width == _width && height == _height))
            {
                return;
            }
            _width = width;
            _height = height;
            ResetLocked();
            // 分辨率到位：立刻放行之前挂起的帧
            FlushPendingLocked();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetLocked();
            _pending.Clear();
            _decoded = 0;
            _errors = 0;
            _inBytes = 0;
            _skippedNoIdr = 0;
            _croppedFrames = 0;
            _pendingIdr = null;
            _hungSince = 0;
            _ptsUs = 0;
        }
    }

    private void ResetLocked()
    {
        if (_mft is not null)
        {
            try
            {
                _mft.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
                Marshal.ReleaseComObject(_mft);
            }
            catch
            {
                // 释放失败不影响重建
            }
            _mft = null;
        }
        _outWidth = 0;
        _outHeight = 0;
        // MFT 重建后换了一批 COM 对象，缓存的 vtable 委托必须作废（否则可能调到旧地址）。
        _fnLock = null;
        _fnUnlock = null;
        _fnSetLen = null;
        _fnAddBuffer = null;
        _fnSetTime = null;
        _fnSetDur = null;
        _fnConvert = null;
        // 重建 MFT 后解码器内部参考帧全丢，必须重新等到 IDR 才能出图
        _needIdr = true;
    }

    private void EnsureInitLocked()
    {
        if (_mft is not null)
        {
            return;
        }
        if (_width <= 0 || _height <= 0)
        {
            throw new InvalidOperationException("解码器尚未获得分辨率（等 CMD_RESOLUTION）");
        }

        if (!_mfStarted)
        {
            // Media Foundation 是**进程级**平台，一个进程里多路解码器必须共享同一份启动状态。
            //
            // 2026-11-13 崩溃根因：双路并行时两个解码器各自 MFStartup/MFShutdown。
            // 任何一路建链失败重连会 Dispose 自己的解码器 → 调 MFShutdown 把**整个进程**
            // 的 MF 平台关掉 → 另一路正在用的 MFT 立刻变成野指针 → 下一次解码
            // 0xc0000374（ntdll 堆损坏）直接崩进程，日志停在「等待设备首帧」。
            // 单路永远只有一份，所以单路跑多久都不崩，只有双路会。
            // 改成进程级引用计数：第一个实例启动，最后一个实例关闭。
            if (Interlocked.Increment(ref _mfRefCount) == 1)
            {
                int hrStart = MfApi.MFStartup(MfConstants.MF_VERSION, 0);
                if (hrStart < 0)
                {
                    Interlocked.Decrement(ref _mfRefCount);
                    MfApi.ThrowIfFailed(hrStart, "MFStartup");
                }
            }
            _mfStarted = true;
        }

        Guid clsid = MfGuids.CLSID_CMSH264DecoderMFT;
        Guid iid = MfGuids.IID_IMFTransform;
        int hr = MfApi.CoCreateInstance(ref clsid, IntPtr.Zero, MfConstants.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ptr);
        MfApi.ThrowIfFailed(hr, "CoCreateInstance(CMSH264DecoderMFT)");
        try
        {
            _mft = (IMFTransform)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }

        // 必须在 SetInputType / BEGIN_STREAMING 之前。不设时 MFT 会先攒满约 37 帧
        // 内部输出缓冲才返回画面：实时投屏延迟到秒级，且静止后真实帧恢复要等
        // 重新填满缓冲，表现为「平板操作后电脑端超过 2s 才更新」。
        ApplyLowLatencyLocked();

        // 输入类型：H.264 elementary stream
        int hrType = MfApi.MFCreateMediaType(out IMFMediaType inType);
        MfApi.ThrowIfFailed(hrType, "MFCreateMediaType(input)");
        Guid majorKey = MfGuids.MF_MT_MAJOR_TYPE;
        Guid majorVal = MfGuids.MFMediaType_Video;
        MfApi.ThrowIfFailed(inType.SetGUID(ref majorKey, ref majorVal), "inType.SetGUID(major)");
        Guid subKey = MfGuids.MF_MT_SUBTYPE;
        Guid subVal = MfGuids.MFVideoFormat_H264;
        MfApi.ThrowIfFailed(inType.SetGUID(ref subKey, ref subVal), "inType.SetGUID(subtype)");

        Guid frameSizeKey = MfGuids.MF_MT_FRAME_SIZE;
        MfApi.ThrowIfFailed(
            inType.SetUINT64(ref frameSizeKey, MfApi.Pack2((uint)_width, (uint)_height)),
            "inType.SetUINT64(frameSize)");
        Guid frameRateKey = MfGuids.MF_MT_FRAME_RATE;
        MfApi.ThrowIfFailed(
            inType.SetUINT64(ref frameRateKey, MfApi.Pack2((uint)_fps, 1)),
            "inType.SetUINT64(frameRate)");
        Guid aspectKey = MfGuids.MF_MT_PIXEL_ASPECT_RATIO;
        MfApi.ThrowIfFailed(
            inType.SetUINT64(ref aspectKey, MfApi.Pack2(1, 1)), "inType.SetUINT64(aspect)");
        Guid interlaceKey = MfGuids.MF_MT_INTERLACE_MODE;
        MfApi.ThrowIfFailed(
            inType.SetUINT32(ref interlaceKey, MfConstants.MFVideoInterlace_Progressive),
            "inType.SetUINT32(interlace)");

        MfApi.ThrowIfFailed(_mft.SetInputType(0, inType, 0), "SetInputType");
        Marshal.ReleaseComObject(inType);

        SelectOutputTypeLocked();
        if (_mft.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO outInfo) >= 0)
        {
            _providesSamples = (outInfo.dwFlags & MfConstants.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputBufferSize = outInfo.cbSize;
        }

        MfApi.ThrowIfFailed(
            _mft.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero),
            "BEGIN_STREAMING");
        MfApi.ThrowIfFailed(
            _mft.ProcessMessage(MfConstants.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero),
            "START_OF_STREAM");
    }

    private void SelectOutputTypeLocked()
    {
        if (_mft is null)
        {
            return;
        }
        for (int i = 0; i < 32; i++)
        {
            int hr = _mft.GetOutputAvailableType(0, i, out IMFMediaType type);
            if (hr < 0)
            {
                break;
            }
            Guid subKey = MfGuids.MF_MT_SUBTYPE;
            Guid sub = Guid.Empty;
            if (type.GetGUID(ref subKey, out sub) < 0)
            {
                Marshal.ReleaseComObject(type);
                continue;
            }
            // 只接受 NV12：Nv12ToBgra() 按「Y 平面 + UV 交织平面」取样。
            // IYUV/I420 是 Y + 独立 U 平面 + 独立 V 平面，布局完全不同；旧代码把 IYUV
            // 也放行，一旦 MFT 选中它就会色度错位、画面颜色全乱（静默错误，不报错）。
            // CMSH264DecoderMFT 在 Windows 上必定提供 NV12 输出类型，不存在兼容损失。
            if (sub != MfGuids.MFVideoFormat_NV12)
            {
                Marshal.ReleaseComObject(type);
                continue;
            }
            Guid sizeKey = MfGuids.MF_MT_FRAME_SIZE;
            ulong size = 0;
            type.GetUINT64(ref sizeKey, out size);
            _outWidth = (int)(size >> 32);
            _outHeight = (int)(size & 0xFFFFFFFF);
            int hrSet = _mft.SetOutputType(0, type, 0);
            Marshal.ReleaseComObject(type);
            if (hrSet >= 0)
            {
                return;
            }
        }
        throw new InvalidOperationException("找不到可用的 NV12 输出类型");
    }

    public void Feed(uint frameId, byte[] annexB)
    {
        if (annexB.Length == 0)
        {
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            FeedCore(frameId, annexB);
        }
        finally
        {
            Interlocked.Add(ref _feedTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _feedCount);
        }
    }

    /// <summary>把参数集拼到图像帧前面，作为同一个 sample 喂给 MFT。</summary>
    private static byte[] Stitch(byte[] paramSets, byte[] frame)
    {
        var outp = new byte[paramSets.Length + frame.Length];
        Buffer.BlockCopy(paramSets, 0, outp, 0, paramSets.Length);
        Buffer.BlockCopy(frame, 0, outp, paramSets.Length, frame.Length);
        return outp;
    }

    private void FeedCore(uint frameId, byte[] annexB)
    {
        lock (_gate)
        {
            // 分辨率自举：优先用 CMD_RESOLUTION，缺失时从关键帧的 SPS 解析（重连/中途加入都能work）
            if (_width <= 0 || _height <= 0)
            {
                if (H264Sps.TryParse(annexB, out int spsW, out int spsH))
                {
                    _width = spsW;
                    _height = spsH;
                }
            }

            // 参数集缓存：任何一帧只要带 SPS/PPS 就留一份（设备会把它单独成帧发）
            byte[] psThis = H264Sps.ExtractParamSets(annexB);
            if (psThis.Length > 0)
            {
                _paramSets = psThis;
            }
            bool hasSlice = H264Sps.HasSliceNal(annexB);

            // 纯参数集帧：只缓存，绝不喂给 MFT。
            // MF 遇到不含 slice 的访问单元会当成无效帧，之后持续 NEED_MORE_INPUT 但 error=0。
            // 但如果此刻手上正挂着一个「裸 IDR」，参数集到齐就是解出首帧的最佳时机：
            // 重建干净的 MFT 再把参数集拼到 IDR 前面一次喂进去。
            if (!hasSlice)
            {
                if (_pendingIdr is not null && _paramSets.Length > 0 &&
                    Interlocked.Read(ref _decoded) == 0)
                {
                    byte[] stitched = Stitch(_paramSets, _pendingIdr);
                    _pendingIdr = null;
                    _hungSince = 0;
                    ResetLocked();
                    _needIdr = false;
                    Console.Error.WriteLine($"[MF] 参数集到位，重置解码器并回放挂起 IDR（{stitched.Length}B）");
                    try
                    {
                        DecodeOneLocked(stitched, 0);
                    }
                    catch (Exception ex)
                    {
                        RecordError($"[MF] 回放挂起 IDR 失败：{ex.Message}");
                    }
                }
                return;
            }

            // IDR 门闩：没有关键帧之前不喂解码器。MF 的 H.264 MFT 遇到 P 帧会静静吃掉
            // （ProcessInput 成功、ProcessOutput 报 need more input、错误计数 0），
            // 于是「看起来在跑其实一帧没解」。重连时设备重放的关键帧若被后续 P 帧挤掉，
            // 就只能干等下一个 GOP —— 实测首帧 2.4~13s 全由此而来。
            bool isKey = H264Sps.IsKeyFrame(annexB);
            long feedNo = Interlocked.Read(ref _feedCount);
            // 前 8 帧的喂帧明细默认不打：双路并行时它会在解码线程里同步写 stderr，
            // 一旦调用方没消费管道（重定向到文件、被 IDE 托管）就会阻塞整条解码链，
            // 表现成「程序卡死 / 闪退」。排障时 `set HARMOS_MF_LOG=1` 再打开。
            if (feedNo < 8 && _verboseFeed)
            {
                Console.Error.WriteLine(
                    $"[MF] feed#{feedNo} len={annexB.Length} key={isKey} " +
                    $"ps={psThis.Length} cachedPs={_paramSets.Length} " +
                    $"needIdr={_needIdr} res={_width}x{_height}");
            }
            if (_needIdr && !isKey)
            {
                Interlocked.Increment(ref _skippedNoIdr);
                return;
            }
            if (isKey)
            {
                _needIdr = false;
                _pending.Clear(); // 关键帧之前的零散 P 帧已无意义
            }

            // 裸 IDR（不带 SPS/PPS）且手上还没有参数集：先挂起等参数集。
            // 直接喂进去会让 MFT 进入「收了一帧但永远 NEED_MORE_INPUT」的死状态，
            // 之后即使补上参数集也解不出（实测重连后首帧 10~13s 就是这么来的）。
            // 兜底：超过 5s 还没等到参数集就放弃挂起，避免永久黑屏。
            if (isKey && psThis.Length == 0 && _paramSets.Length == 0)
            {
                _pendingIdr = annexB;
                if (_hungSince == 0)
                {
                    _hungSince = Environment.TickCount64;
                }
                if (Environment.TickCount64 - _hungSince < 5000)
                {
                    return;
                }
                Console.Error.WriteLine("[MF] 等参数集超时 5s，按原样喂入裸 IDR");
            }
            else if (isKey)
            {
                _pendingIdr = null;
                _hungSince = 0;
            }

            // 排障开关：HARMOS_DUMP=1 时把前几帧 Annex-B 落盘，用 ffprobe 判定码流是否合法。
            // 这比猜「解码器为什么不吐帧」快得多。
            if (_dumpCount < 6 && Environment.GetEnvironmentVariable("HARMOS_DUMP") == "1")
            {
                try
                {
                    System.IO.File.WriteAllBytes(
                        $@"{_dumpDir}\mf_{_dumpCount:D2}_{(isKey ? "k" : "p")}_{annexB.Length}.h264", annexB);
                }
                catch
                {
                    // 落盘失败不影响解码
                }
                _dumpCount++;
            }

            try
            {
                // 当前帧不带参数集、且还没成功解出过任何帧 → 把缓存参数集拼到帧前一起喂。
                // 2026-11-08 实测：拆成「纯参数集 sample + IDR sample」两次 ProcessInput 时，
                // MF 的 CMSH264DecoderMFT 会把不含 slice 的访问单元当无效帧，之后一路 NEED_MORE_INPUT，
                // dec=0|0 卡住（码流本身经 ffprobe 验证合法）。
                byte[] feed = annexB;
                if (psThis.Length == 0 && _paramSets.Length > 0 &&
                    Interlocked.Read(ref _decoded) == 0)
                {
                    feed = new byte[_paramSets.Length + annexB.Length];
                    Buffer.BlockCopy(_paramSets, 0, feed, 0, _paramSets.Length);
                    Buffer.BlockCopy(annexB, 0, feed, _paramSets.Length, annexB.Length);
                }
                DecodeOneLocked(feed, frameId);
            }
            catch (Exception ex) when (ex is InvalidOperationException && (_width <= 0 || _height <= 0))
            {
                // 还没有分辨率：挂起这一帧，等分辨率/SPS 到位后立即回放（不丢、不计错误）
                if (_pending.Count >= MaxPendingFrames)
                {
                    _pending.Dequeue();
                }
                _pending.Enqueue(annexB);
            }
            catch (Exception ex)
            {
                long n = Interlocked.Increment(ref _errors);
                if (n <= 2)
                {
                    Console.Error.WriteLine($"[MF] 解码失败：{ex}");
                }
            }

            // 同一帧内刚拿到的分辨率（SPS 自举）：立即回放暂存帧
            FlushPendingLocked();
        }
    }

    /// <summary>把一帧 Annex-B 送进 MFT 并排空输出。调用方必须持有 _gate。</summary>
    private void DecodeOneLocked(byte[] annexB, uint frameId)
    {
        EnsureInitLocked();
        Interlocked.Add(ref _inBytes, annexB.Length);

        // 全程用原始指针 + vtable 调用：MFCreateSample/MFCreateMemoryBuffer 的 out 参数
        // 用 RCW 接会抛 E_NOINTERFACE（已实测），绕开接口包装最稳。
        int hrBuf = MfApi.MFCreateMemoryBuffer(annexB.Length, out IntPtr buffer);
        MfApi.ThrowIfFailed(hrBuf, "MFCreateMemoryBuffer");
        try
        {
        var lockFn = _fnLock ??= Marshal.GetDelegateForFunctionPointer<LockFn>(VtableSlot(buffer, SlotBufferLock));
        var unlockFn = _fnUnlock ??= Marshal.GetDelegateForFunctionPointer<UnlockFn>(VtableSlot(buffer, SlotBufferUnlock));
        var setLen = _fnSetLen ??= Marshal.GetDelegateForFunctionPointer<SetCurrentLengthFn>(
            VtableSlot(buffer, SlotBufferSetCurrentLength));
        int hrLock = lockFn(buffer, out IntPtr dst, out _, out _);
        MfApi.ThrowIfFailed(hrLock, "buffer.Lock");
        try
        {
            Marshal.Copy(annexB, 0, dst, annexB.Length);
        }
        finally
        {
            unlockFn(buffer);
        }
        setLen(buffer, annexB.Length);

        int hrSample = MfApi.MFCreateSample(out IntPtr sample);
        MfApi.ThrowIfFailed(hrSample, "MFCreateSample");
        try
        {
            var addBuffer = _fnAddBuffer ??= Marshal.GetDelegateForFunctionPointer<AddBufferFn>(VtableSlot(sample, SlotSampleAddBuffer));
            addBuffer(sample, buffer);
            _ptsUs += 1_000_000 / _fps;
            var setTime = _fnSetTime ??= Marshal.GetDelegateForFunctionPointer<SetSampleTimeFn>(
                VtableSlot(sample, SlotSampleSetTime));
            var setDur = _fnSetDur ??= Marshal.GetDelegateForFunctionPointer<SetSampleTimeFn>(
                VtableSlot(sample, SlotSampleSetDuration));
            setTime(sample, _ptsUs * 10);
            setDur(sample, 10_000_000 / _fps);

            int hrIn = _mft!.ProcessInput(0, sample, 0);
            if (hrIn < 0)
            {
                long ne = Interlocked.Increment(ref _errors);
                if (ne <= 3)
                {
                    Console.Error.WriteLine($"[MF] ProcessInput hr=0x{hrIn:X8} {Marshal.GetExceptionForHR(hrIn)?.Message} len={annexB.Length}");
                }
                return;
            }
        }
        finally
        {
            Marshal.Release(sample);
        }
        }
        finally
        {
        Marshal.Release(buffer);
        }

        DrainOutputLocked(frameId);
    }

    private void FlushPendingLocked()
    {
        if (_flushingPending || _pending.Count == 0 || _width <= 0 || _height <= 0)
        {
            return;
        }
        _flushingPending = true;
        try
        {
            while (_pending.Count > 0)
            {
                byte[] frame = _pending.Dequeue();
                FeedCore(0, frame);
            }
        }
        finally
        {
            _flushingPending = false;
        }
    }

    private void DrainOutputLocked(uint frameId)
    {
        if (_mft is null)
        {
            return;
        }
        for (int guard = 0; guard < 4; guard++)
        {
            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            buffers[0].dwStreamID = 0;
            buffers[0].pSample = IntPtr.Zero;
            IntPtr ownSample = IntPtr.Zero;
            if (!_providesSamples)
            {
                int size = _outputBufferSize > 0 ? _outputBufferSize : Math.Max(1, _width * _height * 3 / 2);
                ownSample = CreateOutputSample(size);
                if (ownSample == IntPtr.Zero)
                {
                    RecordError("[MF] 创建输出样本失败");
                    return;
                }
                buffers[0].pSample = ownSample;
            }
            int hr = _mft.ProcessOutput(0, 1, buffers, out _);

            if (hr == MfConstants.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                // 没产出帧时 MFT 不接管我们传入的输出样本，必须自己释放。
                // 静止画面下这条路径几乎每帧都会走：不释放就每帧漏一个
                // IMFSample + IMFMediaBuffer（各带一块非托管缓冲），双路跑十几分钟
                // 累积到几万个 COM 对象，是 0xc0000374 堆损坏的重点嫌疑。
                ReleaseUnconsumed(ownSample, buffers[0].pEvents);
                return;
            }
            // 0xC00D6D61：媒体类型变化，需要重新协商输出类型（与 STREAM_CHANGE 同义）
            if (hr == MfConstants.MF_E_TRANSFORM_STREAM_CHANGE || hr == unchecked((int)0xC00D6D61))
            {
                ReleaseUnconsumed(ownSample, buffers[0].pEvents);
                SelectOutputTypeLocked();
                if (_mft.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO info) >= 0)
                {
                    _providesSamples = (info.dwFlags & MfConstants.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                    _outputBufferSize = info.cbSize;
                }
                continue;
            }
            if (hr < 0)
            {
                ReleaseUnconsumed(ownSample, buffers[0].pEvents);
                long ne = Interlocked.Increment(ref _errors);
                if (ne <= 3)
                {
                    Console.Error.WriteLine($"[MF] ProcessOutput hr=0x{hr:X8} {Marshal.GetExceptionForHR(hr)?.Message}");
                }
                return;
            }
            IntPtr produced = buffers[0].pSample != IntPtr.Zero ? buffers[0].pSample : ownSample;
            if (produced != IntPtr.Zero)
            {
                try
                {
                    EmitSampleLocked(produced, frameId);
                }
                finally
                {
                    // 自己创建的样本由我们释放；MFT 提供的样本由 MFT 侧标记后我们释放一次
                    Marshal.Release(produced);
                    if (ownSample != IntPtr.Zero && produced != ownSample)
                    {
                        Marshal.Release(ownSample);
                    }
                }
            }
            else if (ownSample != IntPtr.Zero)
            {
                Marshal.Release(ownSample);
            }
            if (buffers[0].pEvents != IntPtr.Zero)
            {
                Marshal.Release(buffers[0].pEvents);
            }
        }
    }

    /// <summary>ProcessOutput 未产出帧时，把我们自己创建的输出样本与事件对象还掉。</summary>
    private static void ReleaseUnconsumed(IntPtr ownSample, IntPtr events)
    {
        if (ownSample != IntPtr.Zero)
        {
            Marshal.Release(ownSample);
        }
        if (events != IntPtr.Zero)
        {
            Marshal.Release(events);
        }
    }

    // MFT 回调给回的是原始 COM 指针，用 RCW 强转会莫名 E_NOINTERFACE（即使 QI 成功），
    // 这里直接按 vtable 下标调用，绕开 CLR 的接口包装。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferFn(IntPtr self, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockFn(IntPtr self, out IntPtr pBuffer, out int maxLength, out int currentLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentLengthFn(IntPtr self, int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddBufferFn(IntPtr self, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSampleTimeFn(IntPtr self, long value);

    // IMFMediaBuffer：IUnknown(3) → Lock=3 Unlock=4 GetCurrentLength=5 SetCurrentLength=6
    private const int SlotBufferLock = 3;
    private const int SlotBufferUnlock = 4;
    private const int SlotBufferSetCurrentLength = 6;
    // IMFSample：IUnknown(3) + IMFAttributes(30) → GetSampleFlags=33 … SetSampleTime=36 SetSampleDuration=38
    //            ConvertToContiguousBuffer=41 AddBuffer=42
    private const int SlotSampleSetTime = 36;
    private const int SlotSampleSetDuration = 38;
    private const int SlotConvertToContiguousBuffer = 41;
    private const int SlotSampleAddBuffer = 42;

    private static IntPtr VtableSlot(IntPtr comObj, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObj, 0), index * IntPtr.Size);

    private static readonly Guid IidIMFSample = new("C40A00F2-B93A-4D80-AE8C-5AE634DFB8B1");
    private static readonly Guid IidIMFMediaBuffer = new("045FA593-8799-42B8-BC8D-8968C6453507");

    private IntPtr CreateOutputSample(int size)
    {
        if (MfApi.MFCreateSample(out IntPtr sample) < 0)
        {
            return IntPtr.Zero;
        }
        if (MfApi.MFCreateMemoryBuffer(size, out IntPtr buffer) < 0)
        {
            Marshal.Release(sample);
            return IntPtr.Zero;
        }
        var addBuffer = _fnAddBuffer ??= Marshal.GetDelegateForFunctionPointer<AddBufferFn>(VtableSlot(sample, SlotSampleAddBuffer));
        addBuffer(sample, buffer);
        Marshal.Release(buffer);
        return sample;
    }

    private void EmitSampleLocked(IntPtr samplePtr, uint frameId)
    {
        // 不做 QI：MFCreateSample 返回的对象在 .NET 侧 QI 会误报 E_NOINTERFACE，
        // 但 vtable 布局是正确的，直接按槽位调用即可（已用 GetSampleTime 验证）。
        IntPtr sample = samplePtr;
        if (sample == IntPtr.Zero)
        {
            RecordError("[MF] 样本指针为空");
            return;
        }
        IntPtr buffer = IntPtr.Zero;
        bool locked = false;
        try
        {
            var convert = _fnConvert ??= Marshal.GetDelegateForFunctionPointer<ConvertToContiguousBufferFn>(
                VtableSlot(sample, SlotConvertToContiguousBuffer));
            int hrConv = convert(sample, out buffer);
            if (hrConv < 0 || buffer == IntPtr.Zero)
            {
                RecordError($"[MF] ConvertToContiguousBuffer 失败 hr=0x{hrConv:X8}");
                return;
            }
            var lockFn = _fnLock ??= Marshal.GetDelegateForFunctionPointer<LockFn>(VtableSlot(buffer, SlotBufferLock));
            int hrLock = lockFn(buffer, out IntPtr src, out int maxLen, out int curLen);
            if (hrLock < 0 || curLen <= 0)
            {
                RecordError($"[MF] Lock 失败 hr=0x{hrLock:X8} len={curLen}");
                return;
            }
            // 能安全读的字节数取「已写长度」与「缓冲容量」的较小值。只信 curLen 是不够的：
            // 个别 MFT 会把 curLen 报得比 maxLen 还大，按 curLen 去 Marshal.Copy 就是
            // 从非托管堆越界读 —— 后面某次分配/释放时才以 0xc0000374 报出来。
            int readable = maxLen > 0 && maxLen < curLen ? maxLen : curLen;
            locked = true;
            try
            {
                int bufW = _outWidth > 0 ? _outWidth : _width;
                int bufH = _outHeight > 0 ? _outHeight : _height;
                if (bufW <= 0 || bufH <= 0)
                {
                    return;
                }
                int stride = (int)((long)readable * 2 / (3L * bufH));
                if (stride < bufW)
                {
                    stride = bufW;
                }
                int ySize = stride * bufH;
                // NV12 的色度面是半高，奇数高时向上取整 —— 用 bufH/2 会少算一行，
                // 让最后一行像素读到数组之外。
                int uvSize = stride * ((bufH + 1) / 2);
                if (readable < ySize + uvSize)
                {
                    RecordError($"[MF] 缓冲长度不足 {readable} < {ySize + uvSize}");
                    return;
                }

                // 裁掉编码器的 16px 对齐填充。H.264 宏块是 16x16，内容尺寸不是 16 的
                // 倍数时编码器会补到对齐尺寸，补出来的区域填 YUV=0，经 BT.601 limited
                // 公式解出 RGB≈(0,154,0) —— 就是「画面右/底缘的纯绿边」。
                // 实测：devscale 3 内容 534x854 → 对齐 544x864，右/底各多 10px 绿边；
                // devscale 1/2/4 的结果都能被 16 整除（1600/800/400），故从未出现过。
                // 只裁渲染区域，stride/buffer 计算仍按对齐尺寸，否则取样会错位。
                int w = _width > 0 && _width < bufW ? _width : bufW;
                int h = _height > 0 && _height < bufH ? _height : bufH;
                if (w != bufW || h != bufH)
                {
                    Interlocked.Increment(ref _croppedFrames);
                }

                if (_nv12.Length < ySize + uvSize)
                {
                    _nv12 = new byte[ySize + uvSize];
                }
                if (_bgra.Length != w * h * 4)
                {
                    _bgra = new byte[w * h * 4];
                }
                Marshal.Copy(src, _nv12, 0, ySize + uvSize);
                long tc = System.Diagnostics.Stopwatch.GetTimestamp();
                Nv12ToBgra(_nv12, _bgra, w, h, stride, bufH);
                Interlocked.Add(ref _convertTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tc);
                Interlocked.Increment(ref _convertCount);
                Interlocked.Increment(ref _decoded);
                FrameDecoded?.Invoke(new DecodedFrame(frameId, w, h, _bgra,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            finally
            {
                var unlockFn = _fnUnlock ??= Marshal.GetDelegateForFunctionPointer<UnlockFn>(VtableSlot(buffer, SlotBufferUnlock));
                unlockFn(buffer);
                locked = false;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.Release(buffer);
            }
            _ = locked;
        }
    }

    private void RecordError(string message)
    {
        long n = Interlocked.Increment(ref _errors);
        if (n <= 3)
        {
            Console.Error.WriteLine(message);
        }
    }

    /// <summary>
    /// NV12 → BGRA（BT.601 limited range）。
    /// 色度用**最近邻复制**（每个 2x2 块共用同一 U/V）。
    /// 2026-11-10 曾尝试改成双线性上采样，用 tmp/chroma_ab.py 对构造图做客观
    /// 对比后**回滚**：双线性虽然让 2x2 块边界跳变从 0.868 降到 0.674（更平滑），
    /// 但与真值的平均色差从 1.09 升到 1.63 —— 更平滑却更不准，且两种输出
    /// 平均差异仅 0.66/255，肉眼几乎不可辨。不值得为它引入误差。
    /// </summary>
    /// <param name="w">渲染宽度（已裁掉 16px 对齐填充）。</param>
    /// <param name="h">渲染高度（已裁掉 16px 对齐填充）。</param>
    /// <param name="stride">缓冲行长（字节），来自解码器缓冲实际大小。</param>
    /// <param name="bufH">缓冲高度（对齐后的高度）。UV 平面紧跟在 Y 平面之后，
    /// 偏移是 stride * bufH，**不是** stride * h —— 两者只在有对齐填充时才不同，
    /// 用错会让色度整幅错位（颜色全乱但不报错）。</param>
    private static unsafe void Nv12ToBgra(byte[] src, byte[] dst, int w, int h,
        int stride, int bufH)
    {
        fixed (byte* ps = src)
        fixed (byte* pd = dst)
        {
            byte* yPlane = ps;
            byte* uvPlane = ps + (long)stride * bufH;
            byte* dPlane = pd;
            int ww = w;
            int ss = stride;
            Parallel.For(0, h, y =>
            {
                byte* sy = yPlane + (long)y * ss;
                byte* suv = uvPlane + (long)(y >> 1) * ss;
                byte* d = dPlane + (long)y * ww * 4;
                for (int x = 0; x < ww; x++)
                {
                    int c = sy[x] - 16;
                    if (c < 0)
                    {
                        c = 0;
                    }
                    int u = suv[(x >> 1) << 1] - 128;
                    int v = suv[((x >> 1) << 1) + 1] - 128;
                    int r = (298 * c + 409 * v + 128) >> 8;
                    int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                    int b = (298 * c + 516 * u + 128) >> 8;
                    int o = x * 4;
                    d[o] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                    d[o + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                    d[o + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                    d[o + 3] = 255;
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ResetLocked();
            if (_mfStarted)
            {
                _mfStarted = false;
                // 只有最后一个使用者才真正关平台；否则会打断另一路正在解码的 MFT。
                if (Interlocked.Decrement(ref _mfRefCount) <= 0)
                {
                    Interlocked.Exchange(ref _mfRefCount, 0);
                    MfApi.MFShutdown();
                }
            }
        }
    }
}
