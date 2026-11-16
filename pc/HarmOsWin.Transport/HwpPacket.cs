using System.Buffers.Binary;

namespace HarmOsWin.Transport;

/// <summary>HWP v1 常量。规格见 docs/传输层协议-HWP-v1.md，双端必须逐字节对齐。</summary>
public static class Hwp
{
    public const ushort Magic = 0x4857;
    public const byte Version = 1;
    public const int HeaderSize = 16;
    public const int Mtu = 1200;
    public const int PayloadMax = Mtu - HeaderSize;

    public const byte ChVideo = 1;
    public const byte ChControl = 2;
    public const byte ChHeartbeat = 3;
    public const byte ChAck = 4;

    public const byte FlagNeedAck = 0x01;
    public const byte FlagIsAck = 0x02;
    public const byte FlagProbe = 0x04;

    public const byte CmdIdle = 0x00;
    public const byte CmdLight = 0x01;
    public const byte CmdFull = 0x02;
    public const byte CmdStop = 0xFF;

    // v1.1 扩展（与 tablet/.../HwpPacket.ets 逐值对齐）
    public const byte ChStream = 5;
    public const byte FlagKeyframe = 0x08;
    public const byte FlagLast = 0x10;
    public const byte CmdInput = 0x10;
    public const byte CmdClipText = 0x20;
    public const byte CmdClipImage = 0x21;
    public const byte CmdNav = 0x30;
    public const byte CmdRole = 0x40;
    public const byte CmdResolution = 0x50;
    public const byte CmdStream = 0x60;
    public const byte CmdLockState = 0x70;
    public const byte CmdInjectResult = 0x80; // GATE-05 探针回传
    public const int StreamMetaSize = 8;

    public const int HeartbeatIntervalMs = 30;
    /// <summary>有 RTT 样本时的 RTO 下限（局域网短 RTT 场景）。</summary>
    public const int ControlRtoMs = 20;

    /// <summary>
    /// 无 RTT 样本时的 RTO 兜底。不能取 ControlRtoMs：平板在 IDLE 态不发心跳，
    /// PC 侧拿不到任何样本，此时用 20ms 会让 4 次重传（ControlMaxRetry=3）总共只有 80ms，
    /// 短于本机 Wi-Fi 实测平均 RTT 82ms／峰值 137ms —— 首个 CMD_STREAM 必然判失败，
    /// PC 就一直停在「等待设备画面」（2026-11-08 实测）。等到第一个 ACK 后自动收敛。
    /// </summary>
    public const int ControlRtoInitMs = 200;

    public const int ControlMaxRetry = 3;

    public const int LocalPort = 9010;
    public const int DevicePort = 9020;
    public const string DefaultDeviceHost = "192.168.1.100";

    /// <summary>
    /// epoch 毫秒低 32 位。必须与设备端同源（ArkTS 用 Date.now()，C++ 直发用 system_clock epoch ms），
    /// 否则视频帧的端到端延迟无法计算（历史上用 TickCount64 导致延迟算出 1.17e9 ms 的假数字）。
    /// RTT 仍可用：两端各自取差值，不受绝对值影响。
    /// </summary>
    public static uint Now() => (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);

    /// <summary>无符号回绕差，兼容 uint32 时间戳回绕。</summary>
    public static uint Elapsed(uint now, uint before) => unchecked(now - before);
}

public readonly struct HwpHeader
{
    public byte Channel { get; }
    public byte Flags { get; }
    public uint Seq { get; }
    public uint TimestampMs { get; }
    public ushort PayloadLen { get; }

    public HwpHeader(byte channel, byte flags, uint seq, uint timestampMs, ushort payloadLen)
    {
        Channel = channel;
        Flags = flags;
        Seq = seq;
        TimestampMs = timestampMs;
        PayloadLen = payloadLen;
    }

    public bool NeedAck => (Flags & Hwp.FlagNeedAck) != 0;
    public bool IsAck => (Flags & Hwp.FlagIsAck) != 0;
    public bool IsProbe => (Flags & Hwp.FlagProbe) != 0;
}

public static class HwpPacket
{
    public static byte[] Build(byte channel, byte flags, uint seq, uint timestampMs,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > Hwp.PayloadMax)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"payload {payload.Length}B exceeds MTU budget {Hwp.PayloadMax}B");
        }

        byte[] buffer = new byte[Hwp.HeaderSize + payload.Length];
        Span<byte> span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Hwp.Magic);
        span[2] = Hwp.Version;
        span[3] = channel;
        span[4] = flags;
        span[5] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(10, 4), timestampMs);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), (ushort)payload.Length);
        payload.CopyTo(span.Slice(Hwp.HeaderSize));
        return buffer;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out HwpHeader header)
    {
        header = default;
        if (data.Length < Hwp.HeaderSize)
        {
            return false;
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != Hwp.Magic || data[2] != Hwp.Version)
        {
            return false;
        }
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        if (len > Hwp.PayloadMax || Hwp.HeaderSize + len > data.Length)
        {
            return false;
        }
        header = new HwpHeader(
            data[3],
            data[4],
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(6, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10, 4)),
            len);
        return true;
    }

    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> data, HwpHeader header) =>
        data.Slice(Hwp.HeaderSize, header.PayloadLen);
}
