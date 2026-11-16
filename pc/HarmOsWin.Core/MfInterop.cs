// Media Foundation COM interop（GATE-02 硬解）。
// 只声明实际会用到的接口/方法，但保留完整 vtable 顺序 —— 顺序错一个位置就会调用到别的方法，
// 这里按 Windows SDK mfidl.h / mftransform.h 的声明顺序逐条对照。
using System.Runtime.InteropServices;

namespace HarmOsWin.Core;

internal static class MfGuids
{
    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_IYUV = new("56555949-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");

    // 色彩信息属性。GUID 取自 Wine 8.0 mfapi.h 与 Chromium 的 mingw mfapi.h，
    // 并与微软「Uncompressed Video Media Types」文档的枚举值交叉核对（不猜）。
    // 解码器会在输出类型上报告它实际的色彩矩阵与名义范围；调用方按此选系数，
    // 而不是无条件套 BT.601 limited（那会在硬解已展开成 full range 时压死暗部）。
    public static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("C21B8EE5-B956-4071-8DAF-325EDF5CAB11");
    public static readonly Guid MF_MT_YUV_MATRIX = new("3E23D450-2C75-4D25-A00E-B91670D12327");
    public static readonly Guid MF_MT_VIDEO_PRIMARIES = new("DBFBE4D7-0740-4EE0-8192-850AB0E21935");
    public static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5FB0FCE9-BE5C-4935-A811-EC838F8EED93");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");

    // MF_LOW_LATENCY 与 CODECAPI_AVLowLatencyMode 是同一个 GUID（微软文档明确说明）。
    // GUID 取自 FFmpeg mf_utils.h / VLC mft.c / FreeRDP h264_mf.c，三处定义一致；
    // FreeRDP 用的正是本工程同一个 CLSID_CMSH264DecoderMFT。
    // 不设此属性时 H.264 MFT 会先填满内部输出缓冲才返回帧（本工程实测恒定
    // 37 帧差），实时投屏表现为高延迟、静止后恢复必须等设备 IDR。
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
}

internal static class MfConstants
{
    public const int MF_VERSION = 0x00020070;
    public const int MFSTARTUP_NOSOCKET = 0x1;

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D73);

    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x00000000;
    public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;

    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const int MFT_OUTPUT_STREAM_WHOLE_SAMPLES = 0x00000001;

    public const int MFVideoInterlace_Progressive = 2;
    public const int CLSCTX_INPROC_SERVER = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public int dwFlags;
    public int cbSize;
    public int cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO
{
    public long hnsMaxLatency;
    public int dwFlags;
    public int cbSize;
    public int cbMaxLookahead;
    public int cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public int dwStreamID;
    public IntPtr pSample;
    public int dwStatus;
    public IntPtr pEvents;
}

[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
    [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, IntPtr pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
    [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig] new int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig] new int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
    [PreserveSig] new int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, IntPtr pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid guidKey);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig] new int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(ref Guid guidRepresentation, out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation(ref Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5AE634DFB8B1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
    [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
    [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig] new int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
    [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(ref Guid guidKey, out uint pcchLength);
    [PreserveSig] new int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
    [PreserveSig] new int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, IntPtr pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid guidKey);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig] new int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig] new int SetDouble(ref Guid guidKey, double fValue);
    [PreserveSig] new int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

    [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(int dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out int pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(int dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out int pdwInputMinimum, out int pdwOutputMinimum, out int pdwInputMaximum, out int pdwOutputMaximum);
    [PreserveSig] int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
    [PreserveSig] int GetStreamIDs(int dwInputIDArraySize, IntPtr pdwInputIDs, int dwOutputIDArraySize, IntPtr pdwOutputIDs);
    [PreserveSig] int GetInputStreamInfo(int dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
    [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
    [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
    [PreserveSig] int GetInputStreamAttributes(int dwInputStreamID, out IMFAttributes pAttributes);
    [PreserveSig] int GetOutputStreamAttributes(int dwOutputStreamID, out IMFAttributes pAttributes);
    [PreserveSig] int DeleteInputStream(int wStreamID);
    [PreserveSig] int AddInputStreams(int cStreams, IntPtr adwStreamIDs);
    [PreserveSig] int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    [PreserveSig] int GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    [PreserveSig] int SetInputType(int dwInputStreamID, [MarshalAs(UnmanagedType.Interface)] IMFMediaType? pType, int dwFlags);
    [PreserveSig] int SetOutputType(int dwOutputStreamID, [MarshalAs(UnmanagedType.Interface)] IMFMediaType? pType, int dwFlags);
    [PreserveSig] int GetInputCurrentType(int dwInputStreamID, out IMFMediaType ppType);
    [PreserveSig] int GetOutputCurrentType(int dwOutputStreamID, out IMFMediaType ppType);
    [PreserveSig] int GetInputStatus(int dwInputStreamID, out int pdwFlags);
    [PreserveSig] int GetOutputStatus(out int pdwFlags);
    [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
    [PreserveSig] int ProcessEvent(int dwInputStreamID, IntPtr pEvent);
    [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
    [PreserveSig] int ProcessInput(int dwInputStreamID, IntPtr pSample, int dwFlags);
    [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount, [In, Out, MarshalAs(UnmanagedType.LPArray)] MFT_OUTPUT_DATA_BUFFER[] pOutputSamples, out int pdwStatus);
}

internal static class MfApi
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

    public static ulong Pack2(uint high, uint low) => ((ulong)high << 32) | low;

    public static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"{what} 失败 hr=0x{hr:X8}", hr);
        }
    }
}
