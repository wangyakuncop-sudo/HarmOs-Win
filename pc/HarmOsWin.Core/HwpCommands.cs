using System.Buffers.Binary;
using System.Text;
using HarmOsWin.Transport;

namespace HarmOsWin.Core;

/// <summary>HWP v1.1 命令常量与线格式。与 tablet/.../HwpPacket.ets、SessionTypes.ets 逐值对齐。</summary>
public static class Cmd
{
    public const byte Input = 0x10;
    public const byte ClipText = 0x20;
    public const byte ClipImage = 0x21;
    public const byte Nav = 0x30;
    public const byte Role = 0x40;
    public const byte Resolution = 0x50;
    public const byte Stream = 0x60;
    public const byte LockState = 0x70;
    public const byte InjectResult = 0x80; // GATE-05 探针回传
}

/// <summary>GATE-05 设备端注入探针回传，19 字节定长。</summary>
public sealed record InjectReport(
    bool Syscap, bool Ok, int ErrCode, byte Stage,
    uint ElapsedUs, uint Attempts, uint Success);

public static class NavKey
{
    public const byte Back = 1;
    public const byte Home = 2;
    public const byte Recents = 3;
}

public static class DeviceRole
{
    public const byte Source = 1;
    public const byte Host = 2;
    public const byte Both = 3;
}

public static class EventKind
{
    public const byte Touch = 1;
    public const byte Key = 2;
    public const byte Move = 3;
    // 仅 GATE-05 回归用：触发设备端原生 inputEventClient 探针，不参与 INV-13 交付能力
    public const byte ProbeNative = 0xFE;
}

public static class EventAction
{
    public const byte Down = 1;
    public const byte Up = 2;
    public const byte Move = 3;
}

public static class Wire
{
    public static uint Fnv1a(ReadOnlySpan<byte> data)
    {
        uint h = 0x811c9dc5;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 0x01000193;
        }
        return h;
    }

    public static byte[] Control(byte cmd, byte[]? body = null)
    {
        int len = 1 + (body?.Length ?? 0);
        byte[] p = new byte[len];
        p[0] = cmd;
        if (body is not null && body.Length > 0)
        {
            body.CopyTo(p, 1);
        }
        return p;
    }

    public static byte[] EncodeInput(InputEvent e)
    {
        byte[] p = new byte[12];
        p[0] = e.Type;
        p[1] = e.Action;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2, 2), e.X);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4, 2), e.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6, 2), e.Key);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8, 4), e.TimestampMs);
        return p;
    }

    public static InputEvent DecodeInput(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12)
        {
            return default;
        }
        return new InputEvent
        {
            Type = p[0],
            Action = p[1],
            X = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(2, 2)),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(4, 2)),
            Key = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(6, 2)),
            TimestampMs = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(8, 4))
        };
    }

    public static byte[] EncodeClipText(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] p = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0, 4), Fnv1a(body));
        body.CopyTo(p, 4);
        return p;
    }

    public static (uint Hash, string Text) DecodeClipText(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4)
        {
            return (0, string.Empty);
        }
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(p[..4]);
        return (hash, Encoding.UTF8.GetString(p[4..]));
    }

    /// <summary>PNG 分片：hash u32 + id u16 + index u16 + count u16 + 数据。</summary>
    public static List<byte[]> EncodeClipImage(byte[] png, ushort id = 1)
    {
        const int maxData = Hwp.PayloadMax - 1 - 10;
        int count = Math.Max(1, (png.Length + maxData - 1) / maxData);
        uint hash = Fnv1a(png);
        var result = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int from = i * maxData;
            int len = Math.Min(maxData, png.Length - from);
            byte[] body = new byte[10 + len];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), hash);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), id);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8, 2), (ushort)count);
            png.AsSpan(from, len).CopyTo(body.AsSpan(10));
            result.Add(Control(Cmd.ClipImage, body));
        }
        return result;
    }

    public static byte[] EncodeResolution(ushort w, ushort h, byte folded) =>
        Control(Cmd.Resolution, new byte[]
        {
            (byte)(w & 0xff), (byte)(w >> 8), (byte)(h & 0xff), (byte)(h >> 8), folded
        });

    public static byte[] EncodeRole(byte role, byte deviceId) =>
        Control(Cmd.Role, new[] { role, deviceId });

    public static byte[] EncodeNav(byte key) => Control(Cmd.Nav, new[] { key });

    public static byte[] EncodeStreamCtl(bool start) =>
        Control(Cmd.Stream, new[] { start ? (byte)1 : (byte)0 });

    public static byte[] EncodeLockState(bool locked) =>
        Control(Cmd.LockState, new[] { locked ? (byte)1 : (byte)0 });

    /// <summary>GATE-05：19B 定长，syscap u8 + ok u8 + errCode i32 + stage u8 + elapsedUs u32 + attempts u32 + success u32。</summary>
    public static InjectReport DecodeInjectResult(ReadOnlySpan<byte> p)
    {
        if (p.Length < 19)
        {
            return new InjectReport(false, false, -1, 0, 0, 0, 0);
        }
        return new InjectReport(
            p[0] != 0,
            p[1] != 0,
            BinaryPrimitives.ReadInt32LittleEndian(p.Slice(2, 4)),
            p[6],
            BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(7, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(11, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(15, 4)));
    }
}
