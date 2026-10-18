using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace MultiReplacer.Core
{
    /// <summary>zip / jar 里的一张图。</summary>
    public sealed class ZipImageEntry
    {
        public string EntryName;
        public long UncompressedLength;
        public int Width;
        public int Height;
        public SplashImageFormat Format;
        public DateTimeOffset LastWriteTime;
    }

    /// <summary>
    /// jar / zip 归档里的图片替换（JetBrains 全系列、Live2D Cubism 等 Java 系软件）。
    ///
    /// 与容器路线的区别：归档有正式的中央目录，重写条目是合法操作，不需要等长，
    /// 所以这里用 ZipArchive 直接替换条目，尺寸可以自由变化。
    /// 代价是：如果该 jar 带 JAR 签名（META-INF/*.SF + *.RSA），替换后签名失效，
    /// 部分软件会拒绝加载；替换前会检测并在结果里提示。
    /// </summary>
    public static class ZipImages
    {
        private const long MaxProbeBytes = 32L * 1024 * 1024;

        public static bool IsZipFile(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 22) return false;
                    byte[] head = new byte[4];
                    if (fs.Read(head, 0, 4) != 4) return false;
                    return head[0] == 0x50 && head[1] == 0x4B
                           && ((head[2] == 0x03 && head[3] == 0x04) || (head[2] == 0x05 && head[3] == 0x06));
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool LooksLikeArchive(string path)
        {
            string ext = Path.GetExtension(path);
            if (String.IsNullOrEmpty(ext)) return false;
            if (!String.Equals(ext, ".jar", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                return new FileInfo(path).Length <= 512L * 1024 * 1024;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasSignature(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName;
                        if (name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                            && (name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
                                || name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static List<ZipImageEntry> List(string path, int minWidth, int minHeight, out string error)
        {
            error = null;
            List<ZipImageEntry> result = new List<ZipImageEntry>();
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!IsImageName(entry.FullName)) continue;
                        if (entry.Length <= 0 || entry.Length > MaxProbeBytes) continue;

                        byte[] head = new byte[64];
                        int read;
                        using (Stream stream = entry.Open())
                        {
                            read = stream.Read(head, 0, head.Length);
                        }
                        if (read < 26) continue;

                        SplashImageFormat format;
                        int width, height;
                        if (!ImageProbe.TryReadSize(head, out format, out width, out height)) continue;
                        if (width < minWidth || height < minHeight) continue;

                        ZipImageEntry item = new ZipImageEntry();
                        item.EntryName = entry.FullName;
                        item.UncompressedLength = entry.Length;
                        item.Width = width;
                        item.Height = height;
                        item.Format = format;
                        item.LastWriteTime = entry.LastWriteTime;
                        result.Add(item);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static bool IsImageName(string name)
        {
            return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        public static byte[] Extract(string path, string entryName, out string error)
        {
            error = null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry(entryName);
                    if (entry == null) { error = "归档里找不到条目：" + entryName; return null; }
                    using (Stream stream = entry.Open())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>在副本上替换条目；条目名与修改时间保持原样，其余条目不动。</summary>
        public static bool Replace(string sourcePath, string outputPath, string entryName, byte[] data,
                                   out string error, out string detail)
        {
            error = null;
            detail = null;
            if (data == null || data.Length == 0)
            {
                error = "替换数据为空。";
                return false;
            }

            try
            {
                File.Copy(sourcePath, outputPath, true);
                File.SetAttributes(outputPath, File.GetAttributes(outputPath) & ~FileAttributes.ReadOnly);

                int total;
                using (FileStream fs = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Update))
                {
                    ZipArchiveEntry existing = zip.GetEntry(entryName);
                    if (existing == null)
                    {
                        error = "归档里找不到条目：" + entryName;
                        return false;
                    }
                    DateTimeOffset stamp = existing.LastWriteTime;
                    existing.Delete();

                    ZipArchiveEntry created = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    created.LastWriteTime = stamp;
                    using (Stream stream = created.Open())
                    {
                        stream.Write(data, 0, data.Length);
                    }
                    total = zip.Entries.Count;
                }

                // 写后自检
                using (FileStream fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry(entryName);
                    if (entry == null)
                    {
                        error = "写回后条目消失：" + entryName;
                        return false;
                    }
                    if (entry.Length != data.Length)
                    {
                        error = String.Format(CultureInfo.InvariantCulture,
                            "写回长度不符：期望 {0} 实际 {1}。", data.Length, entry.Length);
                        return false;
                    }
                    using (Stream stream = entry.Open())
                    {
                        byte[] check = new byte[data.Length];
                        int read = 0;
                        while (read < check.Length)
                        {
                            int n = stream.Read(check, read, check.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read != check.Length)
                        {
                            error = "写回后条目读取不完整。";
                            return false;
                        }
                        for (int i = 0; i < check.Length; i++)
                        {
                            if (check[i] != data[i]) { error = "写回后内容比对不一致（偏移 " + i + "）。"; return false; }
                        }
                    }
                }

                detail = "归档条目 " + entryName + " 已替换，共 " + total + " 个条目，其余条目未改动。";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
