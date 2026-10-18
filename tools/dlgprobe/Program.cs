using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MultiReplacer.Core;

namespace DlgProbe
{
    /// <summary>
    /// 缺陷 1 的验证探针：直接调用修复后的 NativeFileDialog，
    /// 确认提权/普通模式下 Win32 公共对话框真的会弹出来（而不是"点了没反应"）。
    ///
    /// 用法：dlgprobe open|save|folder [保持秒数]
    /// 输出：ELEVATED=... / FILTER=... / HWND=... / TITLE=... / RESULT=... / ERROR=...
    /// </summary>
    internal static class Program
    {
        private const int WM_CLOSE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc proc, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

        private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// 抢前台。直接 SetForegroundWindow 会被前台锁拒绝，
        /// 需要先把自己挂到当前前台线程的输入队列上。
        /// </summary>
        private static bool ForceForeground(IntPtr hWnd)
        {
            if (SetForegroundWindow(hWnd)) return true;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            if (fgThread == me) return SetForegroundWindow(hWnd);
            bool ok = false;
            if (AttachThreadInput(me, fgThread, true))
            {
                ok = SetForegroundWindow(hWnd);
                AttachThreadInput(me, fgThread, false);
            }
            return ok;
        }

        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>把整段文本当成 Unicode 按键打进去（不依赖输入法）。</summary>
        private static void TypeText(string text)
        {
            foreach (char ch in text)
            {
                INPUT down = new INPUT { type = 1 };
                down.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE };
                INPUT up = new INPUT { type = 1 };
                up.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
                SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
                SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        private static void PressEnter()
        {
            INPUT down = new INPUT { type = 1 };
            down.ki = new KEYBDINPUT { wVk = 0x0D };
            INPUT up = new INPUT { type = 1 };
            up.ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP };
            SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
            SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static IntPtr dialog;
        private static string dialogTitle;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string mode = args.Length > 0 ? args[0] : "open";
            int hold = args.Length > 1 ? Int32.Parse(args[1]) : 5;

            Console.WriteLine("ELEVATED=" + (ElevationService.IsElevated ? "1" : "0"));
            string[] exts = { ".png", ".bmp", ".jpg", ".jpeg" };
            Console.WriteLine("FILTER=" + NativeFileDialog.BuildFilter("图片", exts).Replace("\0", "|"));

            string result = null;
            string error = null;

            Thread worker = new Thread(() =>
            {
                try
                {
                    if (mode == "folder")
                        result = NativeFileDialog.PickFolder(IntPtr.Zero, "选择软件安装目录", out error);
                    else if (mode == "save")
                        result = NativeFileDialog.SaveFile(IntPtr.Zero, "导出原启动图", "图片", exts,
                                                           Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                                           "splash.png", out error);
                    else
                        result = NativeFileDialog.OpenFile(IntPtr.Zero, "选择新的启动图", "图片", exts,
                                                           Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                                           out error);
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
            });
            worker.IsBackground = true;
            worker.Start();

            // 等对话框窗口出现，最多 8 秒
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && dialog == IntPtr.Zero)
            {
                EnumWindows((h, p) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid != (uint)Environment.ProcessId) return true;
                    StringBuilder cls = new StringBuilder(64);
                    GetClassNameW(h, cls, cls.Capacity);
                    if (cls.ToString() != "#32770") return true;
                    StringBuilder text = new StringBuilder(256);
                    GetWindowTextW(h, text, text.Capacity);
                    if (text.Length == 0) return true;
                    dialog = h;
                    dialogTitle = text.ToString();
                    return false;
                }, IntPtr.Zero);
                if (dialog != IntPtr.Zero) break;
                Thread.Sleep(120);
            }

            if (dialog == IntPtr.Zero)
            {
                Console.WriteLine("HWND=0");
                Console.WriteLine("VERDICT=NO_DIALOG");
            }
            else
            {
                Console.WriteLine("HWND=" + dialog.ToInt64());
                Console.WriteLine("TITLE=" + dialogTitle);
                Console.WriteLine("VERDICT=DIALOG_SHOWN");

                // 第三个参数给出路径时：等对话框稳定后自动填入并回车，用于验证返回值链路
                string autofill = args.Length > 2 ? args[2] : null;
                if (!String.IsNullOrEmpty(autofill))
                {
                    Thread.Sleep(900);
                    Console.WriteLine("FG=" + ForceForeground(dialog));
                    Thread.Sleep(300);
                    TypeText(autofill);
                    Thread.Sleep(300);
                    PressEnter();
                    Thread.Sleep(1500);
                }

                Thread.Sleep(Math.Max(1, hold) * 1000);
                PostMessageW(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            worker.Join(6000);
            Console.WriteLine("RESULT=" + (result ?? "(null)"));
            Console.WriteLine("ERROR=" + (error ?? "(none)"));
            return dialog == IntPtr.Zero ? 1 : 0;
        }
    }
}
