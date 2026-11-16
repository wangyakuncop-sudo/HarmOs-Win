using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace HarmOsWin.Core;

/// <summary>
/// 进程内单例日志文件写入器（2026-11-15 新增）。
///
/// 为什么不用 <c>File.AppendAllText</c>：它每次调用都会打开/关闭文件，
/// 多线程并发写会撞上 FileShare 冲突 —— 要么抛 IOException，要么在系统层等待，
/// 表现为「日志写到某处突然停住，后续代码也不再推进」，非常像死锁但其实是 IO 争用。
///
/// 这里用「一个共享的文件句柄 + 一把锁 + 同步刷盘」，并发安全且开销低。
/// 写失败一律静默丢弃：日志绝不能反过来影响功能。
/// </summary>
internal static class LogFile
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.OrdinalIgnoreCase);

    public static void WriteLine(string path, string line)
    {
        lock (Gate)
        {
            try
            {
                if (!Writers.TryGetValue(path, out StreamWriter? w))
                {
                    // FileShare.ReadWrite：允许别的进程（如 tail）同时读，避免我们互相锁死
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite, 4096, FileOptions.None);
                    w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    Writers[path] = w;
                }
                w.WriteLine(line);
            }
            catch
            {
                // 写失败绝不影响功能。若句柄已失效，下次会重建。
                try
                {
                    if (Writers.TryGetValue(path, out StreamWriter? bad))
                    {
                        try { bad.Dispose(); } catch { }
                        Writers.Remove(path);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}