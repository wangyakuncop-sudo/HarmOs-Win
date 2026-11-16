// 从 Annex-B 码流的 SPS(nal_unit_type=7) 里解析出分辨率。
// 目的：解码器不再依赖设备端「会话启动时上报一次 CMD_RESOLUTION」，
// 只要收到关键帧就能自举，重连/中途加入都能工作。
using System.Collections.Generic;

namespace HarmOsWin.Core;

internal static class H264Sps
{
    /// <summary>
    /// 判断一个 Annex-B 访问单元里是否含 IDR 切片（nal_unit_type=5）。
    /// 2026-11-08：PC 重连后 MF 解码器一帧都解不出、也不报错（dec=0|0），
    /// 根因是它拿到的头几十帧全是 P 帧 —— H.264 解码器必须先见到 IDR 才能起解。
    /// 有了这个判断就能「没 IDR 就跳过」，避免白跑 ProcessInput。
    /// </summary>
    public static bool IsKeyFrame(byte[] annexB)
    {
        foreach (byte[] nal in SplitNalUnits(annexB))
        {
            if (nal.Length > 0 && (nal[0] & 0x1F) == 5)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>是否含 slice NAL（1=非 IDR 切片，5=IDR 切片）。用来区分「纯参数集帧」和「图像帧」。</summary>
    public static bool HasSliceNal(byte[] annexB)
    {
        foreach (byte[] nal in SplitNalUnits(annexB))
        {
            if (nal.Length == 0)
            {
                continue;
            }
            int t = nal[0] & 0x1F;
            if (t == 1 || t == 5)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 抽出 SPS(7)/PPS(8)，保留原始起始码，拼成一段独立的 Annex-B。
    /// PC 侧必须自己留一份：设备把参数集单独成帧发，重放期间参数集帧与 IDR 的到达顺序并不保证，
    /// 参数集晚于 IDR 到就只能干等下一个 GOP（实测首帧 12s）。
    /// </summary>
    public static byte[] ExtractParamSets(byte[] data)
    {
        var marks = new List<(int Start, int Header)>();
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }
            if (data[i + 2] == 1)
            {
                marks.Add((i, i + 3));
                i += 2;
            }
            else if (data[i + 2] == 0 && data[i + 3] == 1)
            {
                marks.Add((i, i + 4));
                i += 3;
            }
        }
        if (marks.Count == 0)
        {
            return Array.Empty<byte>();
        }
        var outp = new List<byte>();
        for (int k = 0; k < marks.Count; k++)
        {
            int hdr = marks[k].Header;
            if (hdr >= data.Length)
            {
                break;
            }
            int type = data[hdr] & 0x1F;
            if (type != 7 && type != 8)
            {
                continue;
            }
            int end = k + 1 < marks.Count ? marks[k + 1].Start : data.Length;
            for (int j = marks[k].Start; j < end; j++)
            {
                outp.Add(data[j]);
            }
        }
        return outp.ToArray();
    }

    public static bool TryParse(byte[] annexB, out int width, out int height)
    {
        width = 0;
        height = 0;
        foreach (byte[] nal in SplitNalUnits(annexB))
        {
            if (nal.Length < 4 || (nal[0] & 0x1F) != 7)
            {
                continue;
            }
            byte[] rbsp = RemoveEmulationPrevention(nal, 1, nal.Length - 1);
            try
            {
                var br = new BitReader(rbsp);
                int profileIdc = (int)br.ReadU(8);
                br.ReadU(8); // constraint_set flags
                br.ReadU(8); // level_idc
                br.ReadUe(); // seq_parameter_set_id

                int chromaFormatIdc = 1;
                if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chromaFormatIdc = (int)br.ReadUe();
                    if (chromaFormatIdc == 3)
                    {
                        br.ReadU(1); // separate_colour_plane_flag
                    }
                    br.ReadUe(); // bit_depth_luma_minus8
                    br.ReadUe(); // bit_depth_chroma_minus8
                    br.ReadU(1); // qpprime_y_zero_transform_bypass_flag
                    if (br.ReadU(1) == 1) // seq_scaling_matrix_present_flag
                    {
                        int lists = chromaFormatIdc != 3 ? 8 : 12;
                        for (int i = 0; i < lists; i++)
                        {
                            if (br.ReadU(1) == 1)
                            {
                                SkipScalingList(br, i < 6 ? 16 : 64);
                            }
                        }
                    }
                }

                br.ReadUe(); // log2_max_frame_num_minus4
                int pocType = (int)br.ReadUe();
                if (pocType == 0)
                {
                    br.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
                }
                else if (pocType == 1)
                {
                    br.ReadU(1); // delta_pic_order_always_zero_flag
                    br.ReadSe(); // offset_for_non_ref_pic
                    br.ReadSe(); // offset_for_top_to_bottom_field
                    int cycle = (int)br.ReadUe();
                    for (int i = 0; i < cycle; i++)
                    {
                        br.ReadSe();
                    }
                }

                br.ReadUe(); // max_num_ref_frames
                br.ReadU(1); // gaps_in_frame_num_value_allowed_flag

                int picWidthInMbs = (int)br.ReadUe() + 1;
                int picHeightInMapUnits = (int)br.ReadUe() + 1;
                int frameMbsOnly = (int)br.ReadU(1);
                if (frameMbsOnly == 0)
                {
                    br.ReadU(1); // mb_adaptive_frame_field_flag
                }
                br.ReadU(1); // direct_8x8_inference_flag

                int cropLeft = 0;
                int cropRight = 0;
                int cropTop = 0;
                int cropBottom = 0;
                if (br.ReadU(1) == 1)
                {
                    cropLeft = (int)br.ReadUe();
                    cropRight = (int)br.ReadUe();
                    cropTop = (int)br.ReadUe();
                    cropBottom = (int)br.ReadUe();
                }

                int subWidthC = chromaFormatIdc == 3 ? 1 : 2;
                int subHeightC = chromaFormatIdc == 1 ? 2 : 1;
                int w = picWidthInMbs * 16 - subWidthC * (cropLeft + cropRight);
                int h = (2 - frameMbsOnly) * picHeightInMapUnits * 16 - subHeightC * (2 - frameMbsOnly) * (cropTop + cropBottom);
                if (w <= 0 || h <= 0)
                {
                    return false;
                }
                width = w;
                height = h;
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static void SkipScalingList(BitReader br, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                int delta = br.ReadSe();
                nextScale = (lastScale + delta + 256) % 256;
            }
            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    private static IEnumerable<byte[]> SplitNalUnits(byte[] data)
    {
        int start = -1;
        for (int i = 0; i + 2 < data.Length; i++)
        {
            bool isStart3 = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1;
            bool isStart4 = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && i + 3 < data.Length && data[i + 3] == 1;
            if (!isStart3 && !isStart4)
            {
                continue;
            }
            int headerLen = isStart4 ? 4 : 3;
            if (start >= 0)
            {
                var nal = new byte[i - start];
                Array.Copy(data, start, nal, 0, nal.Length);
                yield return nal;
            }
            start = i + headerLen;
            i += headerLen - 1;
        }
        if (start >= 0 && start < data.Length)
        {
            var tail = new byte[data.Length - start];
            Array.Copy(data, start, tail, 0, tail.Length);
            yield return tail;
        }
    }

    private static byte[] RemoveEmulationPrevention(byte[] src, int offset, int length)
    {
        var outBytes = new List<byte>(length);
        int zeros = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte b = src[i];
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue;
            }
            outBytes.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return outBytes.ToArray();
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;

        public BitReader(byte[] data) => _data = data;

        public int ReadBit()
        {
            int byteIndex = _bit >> 3;
            if (byteIndex >= _data.Length)
            {
                throw new IndexOutOfRangeException();
            }
            int bit = (_data[byteIndex] >> (7 - (_bit & 7))) & 1;
            _bit++;
            return bit;
        }

        public uint ReadU(int count)
        {
            uint v = 0;
            for (int i = 0; i < count; i++)
            {
                v = (v << 1) | (uint)ReadBit();
            }
            return v;
        }

        public uint ReadUe()
        {
            int leadingZeros = 0;
            while (ReadBit() == 0)
            {
                leadingZeros++;
                if (leadingZeros > 31)
                {
                    throw new InvalidOperationException("exp-golomb 过长");
                }
            }
            if (leadingZeros == 0)
            {
                return 0;
            }
            return (uint)((1u << leadingZeros) - 1 + ReadU(leadingZeros));
        }

        public int ReadSe()
        {
            uint ue = ReadUe();
            int k = (int)((ue + 1) / 2);
            return (ue & 1) == 1 ? k : -k;
        }
    }
}
