using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MultiReplacer.Bootstrapper
{
    /// <summary>
    /// 单文件启动器。
    ///
    /// WinUI 3（Windows App SDK 自包含模式）依赖"可执行文件同目录"的 WinRT 激活清单，
    /// 无法直接用 .NET 单文件打包（IncludeAllContentForSelfExtract 会把运行时解到临时目录，
    /// 进程路径仍指向原始 exe，激活工厂找不到 DLL，启动即报 0x80040111）。
    ///
    /// 因此这里采用"单文件自解压 + 启动"的方式：把完整应用目录压缩后内嵌，
    /// 首次运行解压到 %LOCALAPPDATA%\MultiReplacer\app\&lt;构建标识&gt;\ 并从该目录启动真正的 exe。
    /// 之后每次启动只是校验缓存目录后直接拉起，几乎无额外开销。
    /// </summary>
    internal static class Program
    {
        private const string InnerExeName = "多合一启动图字体UI替换器.exe";
        private const string PayloadName = "payload.zip";
        private const string ReadyMarker = ".ready";
        private const string MutexName = "MultiReplacer.Bootstrapper.Extract";

        [STAThread]
        private static int Main(string[] args)
        {
            Log("启动器开始，参数：" + (args == null ? 0 : args.Length) + " 个");
            try
            {
                string buildId = GetBuildId();
                Log("构建标识：" + buildId);
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiReplacer", "app");
                string target = Path.Combine(root, buildId);
                string innerExe = Path.Combine(target, InnerExeName);

                if (!IsReady(target, innerExe))
                {
                    Log("需要解压到：" + target);
                    ExtractPayload(target, innerExe);
                    Log("解压完成");
                }
                else
                {
                    Log("缓存命中：" + target);
                }

                CleanupOldBuilds(root, buildId);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = innerExe;
                psi.Arguments = JoinArguments(args);
                psi.WorkingDirectory = target;
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log("已启动：" + innerExe);
                return 0;
            }
            catch (Exception ex)
            {
                Log("失败：" + ex);
                ShowError("启动失败：\r\n" + ex.Message + "\r\n\r\n日志：\r\n" + LogPath);
                return 1;
            }
        }

        private static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiReplacer", "logs", "bootstrapper.log");
            }
        }

        private static void Log(string message)
        {
            try
            {
                string path = LogPath;
                string dir = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        /// <summary>
        /// 用程序集 MVID 作为构建标识：同一份构建稳定命中同一缓存目录，重新构建则自动换目录。
        /// </summary>
        private static string GetBuildId()
        {
            Guid id = typeof(Program).Assembly.ManifestModule.ModuleVersionId;
            return id.ToString("N").Substring(0, 12);
        }

        private static bool IsReady(string target, string innerExe)
        {
            return File.Exists(innerExe) && File.Exists(Path.Combine(target, ReadyMarker));
        }

        private static void ExtractPayload(string target, string innerExe)
        {
            using (Mutex mutex = new Mutex(false, MutexName))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(TimeSpan.FromMinutes(5)); }
                    catch (AbandonedMutexException) { owned = true; }

                    // 另一个实例可能刚解压完
                    if (IsReady(target, innerExe)) return;

                    Directory.CreateDirectory(target);

                    Assembly assembly = typeof(Program).Assembly;
                    using (Stream stream = OpenPayload(assembly))
                    {
                        if (stream == null)
                        {
                            throw new InvalidOperationException("安装包内容缺失（未找到 " + PayloadName + "）。");
                        }
                        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                        {
                            ExtractArchive(archive, target);
                        }
                    }

                    if (!File.Exists(innerExe))
                    {
                        throw new InvalidOperationException("解压后未找到主程序：" + innerExe);
                    }
                    File.WriteAllText(Path.Combine(target, ReadyMarker), DateTime.Now.ToString("o"), Encoding.UTF8);
                }
                finally
                {
                    if (owned)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 资源名会被加上根命名空间前缀，这里按后缀匹配，避免改名后失效。
        /// </summary>
        private static Stream OpenPayload(Assembly assembly)
        {
            Stream direct = assembly.GetManifestResourceStream(PayloadName);
            if (direct != null) return direct;

            string[] names = assembly.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(PayloadName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly.GetManifestResourceStream(names[i]);
                }
            }
            return null;
        }

        private static void ExtractArchive(ZipArchive archive, string target)
        {
            string fullTarget = Path.GetFullPath(target);
            if (!fullTarget.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                fullTarget += Path.DirectorySeparatorChar;
            }

            for (int i = 0; i < archive.Entries.Count; i++)
            {
                ZipArchiveEntry entry = archive.Entries[i];
                string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(target, name));

                // 防目录穿越
                if (!destination.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase)) continue;

                if (name.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                string parent = Path.GetDirectoryName(destination);
                if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                entry.ExtractToFile(destination, true);
            }
        }

        /// <summary>
        /// 清理旧构建留下的缓存目录（主程序可能仍在运行，失败就跳过）。
        /// </summary>
        private static void CleanupOldBuilds(string root, string keep)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (String.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(dir, true); }
                    catch { }
                }
            }
            catch
            {
            }
        }

        private static string JoinArguments(string[] args)
        {
            if (args == null || args.Length == 0) return String.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                string arg = args[i] ?? String.Empty;
                if (arg.Length > 0 && arg.IndexOf(' ') < 0 && arg.IndexOf('\t') < 0)
                {
                    sb.Append(arg);
                }
                else
                {
                    sb.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                }
            }
            return sb.ToString();
        }

        private static void ShowError(string message)
        {
            try
            {
                MessageBoxW(IntPtr.Zero, message, "多合一启动图字体UI替换器", 0x00000010);
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
