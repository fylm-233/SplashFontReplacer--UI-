using System;
using System.IO;
using System.Text;

namespace MultiReplacer.Core
{
    /// <summary>
    /// 轻量日志：写 %LOCALAPPDATA%\MultiReplacer\logs\yyyy-MM-dd.log，
    /// 单文件发布下也能定位问题。
    /// </summary>
    public static class AppLog
    {
        private static readonly object SyncRoot = new object();

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex)
        {
            StringBuilder sb = new StringBuilder(message);
            if (ex != null)
            {
                Exception current = ex;
                int depth = 0;
                while (current != null && depth < 6)
                {
                    sb.Append(depth == 0 ? " | " : " | <- ");
                    sb.Append(current.GetType().FullName)
                      .Append("(0x").Append(current.HResult.ToString("X8")).Append("): ")
                      .Append(current.Message);
                    if (!String.IsNullOrEmpty(current.StackTrace) && depth == 0)
                    {
                        sb.AppendLine().Append(current.StackTrace);
                    }
                    current = current.InnerException;
                    depth++;
                }
            }
            Write("ERROR", sb.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    string file = Path.Combine(AppPaths.LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }
}
