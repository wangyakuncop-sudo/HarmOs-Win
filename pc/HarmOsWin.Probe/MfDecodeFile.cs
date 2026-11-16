// 离线解码调试：喂一个 Annex-B .h264 文件给 Media Foundation 解码器，逐帧打印诊断。
// 用途：不依赖真机，快速迭代 COM interop / NV12 转换。
// 用法: HarmOsWin.Probe mfde <file.h264> [maxFrames] [outBmp]
using HarmOsWin.Core;

namespace HarmOsWin.Probe;

public static class MfDecodeFile
{
    public static void Run(string[] args)
    {
        string path = args.Length > 1 ? args[1] : "gate02live.h264";
        int maxFrames = args.Length > 2 ? int.Parse(args[2]) : 0;
        string outBmp = args.Length > 3 ? args[3] : "mfde_frame.bmp";

        if (!File.Exists(path))
        {
            Console.WriteLine($"文件不存在：{path}");
            return;
        }
        byte[] all = File.ReadAllBytes(path);
        Console.WriteLine($"输入 {path} {all.Length} B");

        var units = SplitAccessUnits(all);
        Console.WriteLine($"切出 {units.Count} 个访问单元");

        var decoder = new MediaFoundationDecoder();
        long frames = 0;
        int w = 0;
        int h = 0;
        byte[]? pixels = null;
        decoder.FrameDecoded += f =>
        {
            frames++;
            if (pixels is null)
            {
                w = f.Width;
                h = f.Height;
                pixels = (byte[])f.Pixels.Clone();
                Console.WriteLine($"首帧解码成功 {w}x{h}");
            }
        };

        uint id = 0;
        foreach (byte[] unit in units)
        {
            id++;
            decoder.Feed(id, unit);
            if (maxFrames > 0 && frames >= maxFrames)
            {
                break;
            }
        }

        Console.WriteLine($"解码 {frames} 帧，错误 {decoder.Errors}，输出 {decoder.OutWidth}x{decoder.OutHeight}");
        if (pixels is not null)
        {
            Bmp.Write(outBmp, pixels, w, h);
            Console.WriteLine($"首帧已写入 {outBmp}");
        }
    }

    /// <summary>按 NAL 类型切访问单元：遇到 VCL 起始（类型 1/5）且上一个单元已有 VCL 时断开。</summary>
    internal static List<byte[]> SplitAccessUnits(byte[] data)
    {
        var starts = new List<(int Offset, int HeaderLen, int Type)>();
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }
            if (data[i + 2] == 1)
            {
                if (i + 3 < data.Length)
                {
                    starts.Add((i, 3, data[i + 3] & 0x1F));
                }
                i += 2;
            }
            else if (data[i + 2] == 0 && i + 4 < data.Length && data[i + 3] == 1)
            {
                starts.Add((i, 4, data[i + 4] & 0x1F));
                i += 3;
            }
        }
        var units = new List<byte[]>();
        int begin = -1;
        bool hasVcl = false;
        for (int i = 0; i < starts.Count; i++)
        {
            var (offset, _, type) = starts[i];
            bool isVcl = type == 1 || type == 5;
            if (begin < 0)
            {
                begin = offset;
                hasVcl = isVcl;
                continue;
            }
            if (isVcl && hasVcl)
            {
                var unit = new byte[offset - begin];
                Array.Copy(data, begin, unit, 0, unit.Length);
                units.Add(unit);
                begin = offset;
                hasVcl = true;
            }
            else
            {
                hasVcl |= isVcl;
            }
        }
        if (begin >= 0 && begin < data.Length)
        {
            var unit = new byte[data.Length - begin];
            Array.Copy(data, begin, unit, 0, unit.Length);
            units.Add(unit);
        }
        return units;
    }
}

internal static class Bmp
{
    public static void Write(string path, byte[] bgra, int width, int height)
    {
        int stride = width * 4;
        int imageSize = stride * height;
        int fileSize = 14 + 40 + imageSize;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write(14 + 40);
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);
        for (int y = height - 1; y >= 0; y--)
        {
            bw.Write(bgra, y * stride, stride);
        }
    }
}
