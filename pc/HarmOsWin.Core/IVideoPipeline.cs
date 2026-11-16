namespace HarmOsWin.Core;

public readonly record struct DecodedFrame(uint FrameId, int Width, int Height, byte[] Pixels, long TimestampMs);

/// <summary>
/// 解码/上屏抽象。GATE-02 目标实现是 Media Foundation 硬解 + D3DImage 上屏（与官方 HwMirror.exe 同栈），
/// 当前提供 PassthroughDecoder 用于验证「真实 H.264 码流 → UDP → 重组」链路，不做像素解码。
/// </summary>
public interface IVideoDecoder
{
    string Name { get; }
    event Action<DecodedFrame>? FrameDecoded;
    void Feed(uint frameId, byte[] annexB);
    void Reset();
}

public interface IRenderSurface
{
    string Name { get; }
    void Present(DecodedFrame frame);
}

/// <summary>验证用：把重组后的码流按帧交付，统计字节数，不解码像素。</summary>
public sealed class PassthroughDecoder : IVideoDecoder
{
    public string Name => "passthrough (验证链路，非硬解)";
    public long Frames { get; private set; }
    public long Bytes { get; private set; }
    public event Action<DecodedFrame>? FrameDecoded;

    public void Feed(uint frameId, byte[] annexB)
    {
        Frames++;
        Bytes += annexB.Length;
        FrameDecoded?.Invoke(new DecodedFrame(frameId, 0, 0, annexB, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void Reset()
    {
        Frames = 0;
        Bytes = 0;
    }
}

public sealed class NullRenderSurface : IRenderSurface
{
    public string Name => "null";
    public long Presented { get; private set; }
    public void Present(DecodedFrame frame) => Presented++;
}
