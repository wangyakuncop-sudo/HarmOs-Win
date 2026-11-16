using System.Diagnostics;
using HarmOsWin.Core;

namespace HarmOsWin.Probe;

/// <summary>
/// 参考采集全链路探针（2026-11-15 新增）。
///
/// 目的：**在纯控制台进程里**跑通「拉扩展 daemon → fport → gRPC 裸 H.264 → 切帧」，
/// 把首帧落成文件，用来独立于 WPF 验证整条链路是否成立。
///
/// 为什么需要它：WPF 里 hdc 调用会随机卡死（已用桥进程规避，但排查期间
/// 需要一个「只跑采集、不碰界面」的最小可复现环境）。只要能出帧，就说明
/// 设备侧 daemon、fport 转发、gRPC 协议、H.264 流四段都没问题，
/// 剩下的问题就只在界面层。
///
/// 用法：
///   HarmOsWin.Probe refcap &lt;hdcTarget&gt; &lt;outDir&gt; [timeoutSec]
/// 产物：
///   &lt;outDir&gt;\frame-0001.h264  首个 IDR 起的裸流片段
///   &lt;outDir&gt;\refcap.log       全过程日志
/// </summary>
internal static class RefCaptureProbe
{
    public static async Task RunAsync(string[] args)
    {
        string target = args.Length > 1 ? args[1] : "192.168.1.102:40663";
        string outDir = args.Length > 2 ? args[2] : ".";
        int timeoutSec = args.Length > 3 && int.TryParse(args[3], out var t) ? t : 30;

        Directory.CreateDirectory(outDir);
        string logPath = Path.Combine(outDir, "refcap.log");
        var logLock = new object();
        void Log(string m)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {m}";
            Console.WriteLine(line);
            lock (logLock)
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            }
        }

        string hdc = ResolveHdc();
        string agent = ResolveAgent();
        Log($"target={target}");
        Log($"hdc={hdc} 存在={File.Exists(hdc)}");
        Log($"agent={agent} 存在={File.Exists(agent)}");
        if (!File.Exists(hdc) || !File.Exists(agent))
        {
            Log("前置文件缺失，退出");
            return;
        }

        // 先把 hdcd 起来（桥进程内部同样会做，这里显式一次让后续更稳）
        HdcRunner.EnsureServer(hdc, Log);

        // 关键：让 ReferenceCapture 把收到的裸 H.264 顺序录盘。
        // 只要 dump 文件里有数据，就证明「设备 daemon → fport → gRPC → 裸流」四段都通。
        string dumpPath = Path.Combine(outDir, "stream.h264");
        try { if (File.Exists(dumpPath)) { File.Delete(dumpPath); } } catch { }
        Environment.SetEnvironmentVariable("HARMOSWIN_REF_DUMP", dumpPath);

        await using var cap = new ReferenceCapture(hdc, target, agent, Log);
        var sw = Stopwatch.StartNew();
        bool ok;
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            ok = await cap.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log($"StartAsync 抛异常：{ex.GetType().Name}: {ex.Message}");
            return;
        }
        Log($"StartAsync = {ok}，耗时 {sw.ElapsedMilliseconds}ms");
        if (!ok)
        {
            Log("采集启动失败，退出");
            HdcRunner.Shutdown();
            return;
        }

        // 轮询 dump 文件大小，涨了就说明真的收到码流
        var wait = Stopwatch.StartNew();
        long lastSize = 0;
        bool gotStream = false;
        while (wait.Elapsed < TimeSpan.FromSeconds(timeoutSec))
        {
            await Task.Delay(500);
            long size = 0;
            try { size = File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0; } catch { }
            if (size != lastSize)
            {
                Log($"t={wait.ElapsedMilliseconds}ms 码流 {size} 字节（+{size - lastSize}）");
                lastSize = size;
            }
            if (size > 4096)
            {
                gotStream = true;
                Log($"✅ 收到码流 {size} 字节，链路成立");
                break;
            }
        }

        if (!gotStream)
        {
            Log($"最终：未收到有效码流（{lastSize} 字节）");
        }

        try
        {
            await cap.StopAsync();
            Log("采集已停止");
        }
        catch (Exception ex)
        {
            Log($"StopAsync 异常：{ex.Message}");
        }
        HdcRunner.Shutdown();
        Log($"产物：{dumpPath}（{(File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0)} 字节）");
    }

    private static string ResolveHdc()
    {
        string? env = Environment.GetEnvironmentVariable("HARMOS_HDC");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }
        foreach (string c in new[]
                 {
                     @"E:\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe",
                     @"C:\Program Files\Huawei\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe",
                 })
        {
            if (File.Exists(c))
            {
                return c;
            }
        }
        return "hdc.exe";
    }

    /// <summary>
    /// 采集要的是 **screen_casting.so**，不是 agent.so。
    /// agent.so 是触控通道的 uitest agent（600KB），screen_casting.so 才是
    /// 屏幕采集扩展（248KB）。推错文件时 daemon 能起来但**绑不上
    /// @scrcpy_grpc_socket**，表现为「gRPC 连上但 0 字节」——2026-11-15 实测踩过。
    /// </summary>
    private static string ResolveAgent()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string rel in new[]
                 {
                     "screen_casting.so",
                     Path.Combine("assets", "screen_casting.so"),
                     Path.Combine("..", "..", "..", "..", "HarmOsWin.App", "assets", "screen_casting.so"),
                 })
        {
            string p = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(p))
            {
                return p;
            }
        }
        return Path.Combine(baseDir, "screen_casting.so");
    }
}