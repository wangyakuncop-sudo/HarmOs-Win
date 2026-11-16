using System.Text;
using HarmOsWin.Core;

namespace HarmOsWin.Probe;

/// <summary>
/// PC → 平板 文本注入的端到端探针。
///
/// 走的是**产品代码本身**（<see cref="ClipTextInjector"/> + <see cref="InputInjector"/>），
/// 不是另写一份等价逻辑 —— 否则验的就不是要交付的东西。
///
/// 文本从文件读（UTF-8）而不是命令行参数：Git Bash / cmd 往 exe 传参时按 ANSI 转码，
/// 会把 emoji 变问号，那验的就不是 C# 这一段了。用文件能把「C# → ProcessStartInfo → hdc」
/// 这一环单独隔出来看。
///
/// 用法:
///   HarmOsWin.Probe clippush &lt;hdcTarget&gt; &lt;x&gt; &lt;y&gt; &lt;textFile&gt;
/// 例:
///   HarmOsWin.Probe clippush 192.168.1.100:38803 800 1383 tmp/clip.txt
/// </summary>
public static class ClipPush
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 5)
        {
            Console.WriteLine("用法: HarmOsWin.Probe clippush <hdcTarget> <x> <y> <textFile>");
            return;
        }
        string target = args[1];
        if (!int.TryParse(args[2], out int x) || !int.TryParse(args[3], out int y))
        {
            Console.WriteLine("x / y 必须是整数");
            return;
        }
        string file = args[4];
        if (!File.Exists(file))
        {
            Console.WriteLine($"找不到文件: {file}");
            return;
        }
        string text = await File.ReadAllTextAsync(file, Encoding.UTF8);
        // 文件末尾的换行通常是编辑器自动加的，不属于用户内容
        text = text.TrimEnd('\r', '\n');

        var injector = new InputInjector(Environment.CurrentDirectory, hdcTarget: target);

        // 与产品同一路径：文件优先，命令行直传兜底。
        string local = ClipTextInjector.WriteTempFile(text);
        var up = await injector.SendFileAsync(local, ClipTextInjector.RemotePath);
        Console.WriteLine($"[{(up.Ok ? "OK" : "FAIL")} {up.ElapsedMs,6:F0}ms] file send {text.Length} 字 → {ClipTextInjector.RemotePath}");
        if (up.Ok)
        {
            string cmd = ClipTextInjector.BuildFileCommand(x, y);
            var r = await injector.ShellAsync(cmd);
            Console.WriteLine($"[{(r.Ok ? "OK" : "FAIL")} {r.ElapsedMs,6:F0}ms] {cmd}");
            if (r.Ok)
            {
                Console.WriteLine("完成 1/1（文件通道）");
                return;
            }
            Console.WriteLine($"  输出: {r.Output}");
        }

        Console.WriteLine("—— 退回命令行直传 ——");
        List<string> cmds = ClipTextInjector.BuildCommands(x, y, text);
        Console.WriteLine($"文本 {text.Length} 字 → {cmds.Count} 条命令，锚点 ({x},{y})");
        int ok = 0;
        foreach (string cmd in cmds)
        {
            var r = await injector.ShellAsync(cmd);
            string shown = cmd.Length > 90 ? cmd[..90] + "…" : cmd;
            Console.WriteLine($"[{(r.Ok ? "OK" : "FAIL")} {r.ElapsedMs,6:F0}ms] {shown}");
            if (!r.Ok)
            {
                Console.WriteLine($"  输出: {r.Output}");
                break;
            }
            ok++;
        }
        Console.WriteLine($"完成 {ok}/{cmds.Count}");
    }
}
