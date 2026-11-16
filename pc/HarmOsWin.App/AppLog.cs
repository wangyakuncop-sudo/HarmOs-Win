using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HarmOsWin.App;

/// <summary>
/// 启动日志。**这是「连不上」这件事最后一次靠猜** —— 以前程序跑完什么都不留，
/// 用户看到黑屏或一句状态栏提示，只能靠口头描述来定位，效率极低。
///
/// 写到哪里：exe 同目录的 logs\startup.log（用户最容易找到）。
/// exe 目录不可写时退回 %LOCALAPPDATA%\HarmOsWin\logs\startup.log。
/// 超过 1.5MB 自动滚成 startup.prev.log，只留一份历史，不会无限长大。
///
/// 调用点故意很少：只在启动链路的关键节点（配置 → 目标解析 → 采集 → 建会话 → 推流 → 首帧）
/// 和未捕获异常上打点，不做逐帧日志，避免影响性能。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 1536 * 1024;

    // 重复消息抑制：同一条消息连续出现时只写前几次，其余计数。
    // 20:53 实测教训：扫描端口时被放弃的 TCP 连接每秒抛十几个「未观察异常」，
    // 一条日志里堆了 1.2MB 完全没用的重复行，反而把关键行埋掉了。
    private static string _lastMessage = "";
    private static int _suppressed;

    /// <summary>当前日志文件绝对路径（未初始化时给出提示串，界面上会显示它）。</summary>
    public static string LogPath => _path ?? "(日志未初始化)";

    public static void Init()
    {
        if (_path is not null) return;
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "HarmOsWin", "logs")
        };
        foreach (string dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "startup.log");
                RollIfNeeded(file);
                // 探测可写性：打一行最便宜的内容
                using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    _path = file;
                    break;
                }
            }
            catch (Exception)
            {
                // 换下一个候选目录
            }
        }
        if (_path is null) return;   // 全都不可写：静默降级，不阻断启动

        lock (Gate)
        {
            try
            {
                using var w = new StreamWriter(_path, append: true, Encoding.UTF8);
                w.WriteLine();
                w.WriteLine(new string('=', 78));
                w.WriteLine($"# HarmOsWin 启动  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  " +
                            $"pid={Environment.ProcessId}  base={AppContext.BaseDirectory}");
                w.WriteLine(new string('=', 78));
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>把一个段落（比如目标解析的 trace）整块写进去，前缀统一缩进便于阅读。</summary>
    public static void Block(string title, IEnumerable<string> lines)
    {
        Write("INFO ", title);
        foreach (string line in lines)
        {
            Write("  ·   ", line);
        }
    }

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                if (message == _lastMessage)
                {
                    _suppressed++;
                    if (_suppressed > 3) return;   // 连续第 4 次起不再写
                }
                else if (_suppressed > 0)
                {
                    using (var w0 = new StreamWriter(_path, append: true, Encoding.UTF8))
                    {
                        w0.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [ -- ] 上一条同样的消息又重复 {_suppressed} 次（已省略）");
                    }
                    _suppressed = 0;
                }

                if (message != _lastMessage)
                {
                    _lastMessage = message;
                    _suppressed = 0;
                }

                using var w = new StreamWriter(_path, append: true, Encoding.UTF8);
                w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
            }
            catch (Exception)
            {
                // 日志写不进去绝不能影响主流程
            }
        }
    }

    private static void RollIfNeeded(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            string prev = Path.Combine(fi.DirectoryName ?? ".", "startup.prev.log");
            File.Copy(file, prev, overwrite: true);
            File.WriteAllText(file, string.Empty);
        }
        catch (Exception)
        {
            // 滚动失败就继续往原文件追加
        }
    }
}
