using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HarmOsWin.Core;

/// <summary>
/// Windows 伪终端（ConPTY，Win10 1809+）。
///
/// 为什么需要它：`hdc shell` 只有在 stdin 是**终端**时才进入交互模式。
/// 用 ProcessStartInfo 重定向 stdio 给的是 pipe，hdc 会直接回
/// `[W] Not support stdio TTY mode` 并只吐一个提示符 —— 这是 2026-11-14
/// 那次「常驻会话判死」的真实原因（是喂法错了，不是 hdc 不支持）。
///
/// 给 hdc 套一个 ConPTY，它的 isatty(stdin) 就为真，进入交互 shell，
/// 之后一条长连接可以反复喂命令，省掉每条命令一次的进程启动 + 会话重建。
/// </summary>
internal static class ConPty
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    /// <summary>创建进程时不显示控制台窗口（否则每次 ConPTY 建链都会闪一下黑窗）。</summary>
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    /// <summary>当前系统是否支持 ConPTY（Win10 1809 / build 17763 起）。</summary>
    public static bool Available =>
        OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>已启动的伪终端会话。</summary>
    public sealed class Session : IDisposable
    {
        public required FileStream Input { get; init; }   // 我们写入 → 子进程 stdin
        public required FileStream Output { get; init; }  // 子进程 stdout → 我们读取
        public required int Pid { get; init; }
        internal IntPtr PseudoConsole;
        internal IntPtr ProcessHandle;

        public bool HasExited
        {
            get
            {
                try { return Process.GetProcessById(Pid).HasExited; }
                catch { return true; }
            }
        }

        public void Dispose()
        {
            // 顺序：先断输入（让子进程读到 EOF 自行退出），再关伪终端，最后兜底 kill
            try { Input.Dispose(); } catch { }
            if (PseudoConsole != IntPtr.Zero)
            {
                try { ClosePseudoConsole(PseudoConsole); } catch { }
                PseudoConsole = IntPtr.Zero;
            }
            try { Output.Dispose(); } catch { }
            try
            {
                var p = Process.GetProcessById(Pid);
                if (!p.HasExited) { p.Kill(entireProcessTree: true); }
            }
            catch { }
            if (ProcessHandle != IntPtr.Zero)
            {
                try { CloseHandle(ProcessHandle); } catch { }
                ProcessHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 用 ConPTY 启动一个子进程。参数里的 exe 与 args 直接拼成命令行。
    /// 失败抛 <see cref="InvalidOperationException"/>，由调用方降级。
    /// </summary>
    public static Session Start(string exe, string args, short cols = 200, short rows = 40)
    {
        if (!Available)
        {
            throw new InvalidOperationException("当前系统不支持 ConPTY（需 Win10 1809+）");
        }

        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr hPC = IntPtr.Zero;
        IntPtr attrList = IntPtr.Zero;
        IntPtr siPtr = IntPtr.Zero;
        bool ok = false;

        try
        {
            // 两条管道：我们写的一端 → ConPTY 的输入；ConPTY 的输出 → 我们读的一端
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException($"CreatePipe(in) 失败: {Marshal.GetLastWin32Error()}");
            }
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException($"CreatePipe(out) 失败: {Marshal.GetLastWin32Error()}");
            }

            var size = new COORD { X = cols, Y = rows };
            int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out hPC);
            if (hr != 0 || hPC == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreatePseudoConsole 失败: 0x{hr:X8}");
            }

            // ConPTY 已经持有这两端，我们这份必须立刻关掉，否则 EOF 永远不来
            CloseHandle(inRead); inRead = IntPtr.Zero;
            CloseHandle(outWrite); outWrite = IntPtr.Zero;

            // 属性列表：把伪终端句柄挂到子进程上
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attrList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize))
            {
                throw new InvalidOperationException($"InitializeProcThreadAttributeList 失败: {Marshal.GetLastWin32Error()}");
            }
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                           hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException($"UpdateProcThreadAttribute 失败: {Marshal.GetLastWin32Error()}");
            }

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;
            siPtr = Marshal.AllocHGlobal(Marshal.SizeOf<STARTUPINFOEX>());
            Marshal.StructureToPtr(si, siPtr, false);

            string cmdLine = $"\"{exe}\" {args}";
            // 🔴 CREATE_NO_WINDOW（0x08000000）必须带上（2026-11-14 用户反馈「中间总是闪出窗口」）。
            // 不带的话 CreateProcess 会为 hdc.exe 创建一个**可见的控制台窗口**，
            // 而 ConPTY 每次建链（首次连接、空闲 120s 关闭后重连）都会新起一个进程
            // → 用户看到黑色控制台一闪而过。
            // 代价：hdc 出错时的 stderr 不再有窗口可看，但我们的日志已经落盘（AppLog），
            // 且出错信息通过管道读回来，不影响诊断。
            bool created = CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                                          EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                                          IntPtr.Zero, null,
                                          siPtr, out PROCESS_INFORMATION pi);
            if (!created)
            {
                throw new InvalidOperationException($"CreateProcessW 失败: {Marshal.GetLastWin32Error()}");
            }
            CloseHandle(pi.hThread);

            var input = new FileStream(new SafeFileHandle(inWrite, ownsHandle: true),
                                       FileAccess.Write, 1, isAsync: false);
            inWrite = IntPtr.Zero;  // 所有权已交给 FileStream
            var output = new FileStream(new SafeFileHandle(outRead, ownsHandle: true),
                                        FileAccess.Read, 1, isAsync: false);
            outRead = IntPtr.Zero;

            ok = true;
            return new Session
            {
                Input = input,
                Output = output,
                Pid = pi.dwProcessId,
                PseudoConsole = hPC,
                ProcessHandle = pi.hProcess
            };
        }
        finally
        {
            if (!ok)
            {
                if (siPtr != IntPtr.Zero) Marshal.FreeHGlobal(siPtr);
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
                if (inRead != IntPtr.Zero) CloseHandle(inRead);
                if (inWrite != IntPtr.Zero) CloseHandle(inWrite);
                if (outRead != IntPtr.Zero) CloseHandle(outRead);
                if (outWrite != IntPtr.Zero) CloseHandle(outWrite);
            }
            else
            {
                // 成功路径只需释放临时结构，属性列表本身在 CreateProcess 之后就没用了
                if (siPtr != IntPtr.Zero) Marshal.FreeHGlobal(siPtr);
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            }
        }
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
                                          IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput,
                                                  uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
                                                                int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
                                                         IntPtr lpValue, IntPtr cbSize,
                                                         IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
                                              IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
                                              bool bInheritHandles, uint dwCreationFlags,
                                              IntPtr lpEnvironment, string? lpCurrentDirectory,
                                              IntPtr lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
