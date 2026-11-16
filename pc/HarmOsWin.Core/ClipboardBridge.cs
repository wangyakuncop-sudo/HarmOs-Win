using System.Runtime.InteropServices;
using System.Text;

namespace HarmOsWin.Core;

/// <summary>Win32 剪贴板最小封装。图片用自定义格式存 PNG 原始字节，保证无压缩损失。</summary>
public static class Win32Clipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const string PngFormatName = "HarmOsWin/PNG";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, IntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalSize(IntPtr handle);

    private static uint PngFormat => RegisterClipboardFormatW(PngFormatName);

    private static bool PutBytes(uint format, byte[] data)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }
        try
        {
            EmptyClipboard();
            IntPtr h = GlobalAlloc(GmemMoveable | GmemZeroInit, (IntPtr)data.Length);
            if (h == IntPtr.Zero)
            {
                return false;
            }
            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }
            Marshal.Copy(data, 0, ptr, data.Length);
            GlobalUnlock(h);
            // 成功后内存所有权归系统，不得 GlobalFree
            return SetClipboardData(format, h) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? GetBytes(uint format)
    {
        if (!IsClipboardFormatAvailable(format) || !OpenClipboard(IntPtr.Zero))
        {
            return null;
        }
        try
        {
            IntPtr h = GetClipboardData(format);
            if (h == IntPtr.Zero)
            {
                return null;
            }
            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                int size = (int)GlobalSize(h).ToInt64();
                if (size <= 0)
                {
                    return Array.Empty<byte>();
                }
                byte[] data = new byte[size];
                Marshal.Copy(ptr, data, 0, size);
                return data;
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool SetText(string text) =>
        PutBytes(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"));

    public static string? GetText()
    {
        byte[]? data = GetBytes(CfUnicodeText);
        if (data is null || data.Length < 2)
        {
            return null;
        }
        string s = Encoding.Unicode.GetString(data);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    public static bool SetPng(byte[] png) => PutBytes(PngFormat, png);

    public static byte[]? GetPng() => GetBytes(PngFormat);
}

/// <summary>
/// 剪贴板双向桥：内容哈希抑制回环（自己写进去的不再回传）。
/// 只负责取/放，跨端发送由 DeviceSession 注入的回调完成。
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    private readonly Func<byte[], Task<bool>> _sendControl;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private uint _lastTextHash;
    private uint _lastImageHash;
    private int _pollMs;

    public long SentText { get; private set; }
    public long SentImages { get; private set; }
    public long ReceivedText { get; private set; }
    public long ReceivedImages { get; private set; }

    public ClipboardBridge(Func<byte[], Task<bool>> sendControl, int pollMs = 300)
    {
        _sendControl = sendControl;
        _pollMs = pollMs;
    }

    public void Start() => _loop = Task.Run(WatchAsync);

    /// <summary>设备端文本到达：写本地剪贴板并记账，避免触发回环。</summary>
    public void ApplyRemoteText(string text, uint hash)
    {
        _lastTextHash = hash;
        Win32Clipboard.SetText(text);
        ReceivedText++;
    }

    public void ApplyRemoteImage(byte[] png)
    {
        _lastImageHash = Wire.Fnv1a(png);
        Win32Clipboard.SetPng(png);
        ReceivedImages++;
    }

    private async Task WatchAsync()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? text = Win32Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    uint hash = Wire.Fnv1a(Encoding.UTF8.GetBytes(text));
                    if (hash != _lastTextHash)
                    {
                        _lastTextHash = hash;
                        await _sendControl(Wire.Control(Cmd.ClipText, Wire.EncodeClipText(text)));
                        SentText++;
                    }
                }
                byte[]? png = Win32Clipboard.GetPng();
                if (png is { Length: > 0 })
                {
                    uint hash = Wire.Fnv1a(png);
                    if (hash != _lastImageHash)
                    {
                        _lastImageHash = hash;
                        foreach (byte[] part in Wire.EncodeClipImage(png))
                        {
                            await _sendControl(part);
                        }
                        SentImages++;
                    }
                }
            }
            catch (Exception)
            {
                // 剪贴板被其它进程占用是常态，下一轮重试
            }
            try
            {
                await Task.Delay(_pollMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // 忽略
        }
        _cts.Dispose();
    }
}
