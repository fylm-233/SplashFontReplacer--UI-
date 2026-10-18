using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MultiReplacer.Core
{
    /// <summary>容器文件里命中的一张图：偏移 + 长度 + 尺寸。</summary>
    public sealed class EmbeddedImageHit
    {
        public long Offset;
        public int Length;
        public int Width;
        public int Height;
        public SplashImageFormat Format;

        public string OffsetHex
        {
            get { return "0x" + Offset.ToString("X8", CultureInfo.InvariantCulture); }
        }
    }

    public sealed class EmbeddedPatchItem
    {
        public EmbeddedImageHit Hit;
        public byte[] Data;
    }

    /// <summary>
    /// 容器文件（Photoshop 的 PSIcons*Res.dat、InDesign/InCopy 的 .idrc、各种 .res/.bin）
    /// 里内嵌图片的定位与原地替换。
    ///
    /// 为什么用"等长原地覆盖"而不是重排容器：
    /// 这些容器的索引表（IconResources.idx 等）记录的是绝对偏移，
    /// 一旦新图长度变化，所有后续偏移都要重算，等于重写私有格式；
    /// 而等长覆盖不改动任何偏移，索引天然保持有效，风险低得多。
    /// 长度用 PngCodec.FitToExactLength 精确补齐（BMP 同尺寸同位深天然等长）。
    /// </summary>
    public static class EmbeddedImages
    {
        /// <summary>只在这些扩展名的文件里找内嵌图，避免去扫 .exe/.dll（那是 PE 路线的事）。</summary>
        public static readonly string[] ContainerExtensions =
        {
            ".dat", ".idx", ".idrc", ".res", ".bin", ".pak", ".assets", ".rcc", ".img", ".resource", ".rsrc"
        };

        public static bool IsContainerExtension(string path)
        {
            string ext = Path.GetExtension(path);
            if (String.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < ContainerExtensions.Length; i++)
            {
                if (String.Equals(ext, ContainerExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool LooksLikeContainer(string path)
        {
            if (!IsContainerExtension(path)) return false;
            try
            {
                FileInfo info = new FileInfo(path);
                return info.Length >= 64 * 1024 && info.Length <= 320L * 1024 * 1024;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在内存里扫描所有能完整解析出宽高的图片。
        /// 只认头部合法且长度可判定的，避免把随机字节当成图片。
        /// </summary>
        public static List<EmbeddedImageHit> Scan(byte[] data, int minWidth, int minHeight, int maxHits)
        {
            List<EmbeddedImageHit> hits = new List<EmbeddedImageHit>();
            if (data == null || data.Length < 64) return hits;

            int pos = 0;
            while (pos < data.Length - 32)
            {
                int next = FindNextCandidate(data, pos);
                if (next < 0) break;

                SplashImageFormat format;
                int width, height, length;
                if (ImageProbe.TryRead(data, next, data.Length - next, out format, out width, out height, out length)
                    && width >= minWidth && height >= minHeight && length > 0)
                {
                    EmbeddedImageHit hit = new EmbeddedImageHit();
                    hit.Offset = next;
                    hit.Length = length;
                    hit.Width = width;
                    hit.Height = height;
                    hit.Format = format;
                    hits.Add(hit);
                    pos = next + length;
                    if (hits.Count >= maxHits) break;
                    continue;
                }
                pos = next + 1;
            }
            return hits;
        }

        /// <summary>找下一个可能是图片头的字节位置（PNG 签名 / BM / JPEG SOI）。</summary>
        private static int FindNextCandidate(byte[] data, int from)
        {
            for (int i = from; i < data.Length - 2; i++)
            {
                byte b = data[i];
                if (b == 0x89)
                {
                    if (i + 8 <= data.Length && data[i + 1] == 0x50 && data[i + 2] == 0x4E && data[i + 3] == 0x47
                        && data[i + 4] == 0x0D && data[i + 5] == 0x0A && data[i + 6] == 0x1A && data[i + 7] == 0x0A)
                        return i;
                }
                else if (b == (byte)'B')
                {
                    if (i + 2 <= data.Length && data[i + 1] == (byte)'M') return i;
                }
                else if (b == 0xFF)
                {
                    if (i + 3 <= data.Length && data[i + 1] == 0xD8 && data[i + 2] == 0xFF) return i;
                }
            }
            return -1;
        }

        public static List<EmbeddedImageHit> ScanFile(string path, int minWidth, int minHeight, int maxHits, out string error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                return Scan(data, minWidth, minHeight, maxHits);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        public static byte[] ReadRegion(string path, EmbeddedImageHit hit, out string error)
        {
            error = null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (hit.Offset + hit.Length > fs.Length)
                    {
                        error = "区域超出文件末尾。";
                        return null;
                    }
                    fs.Position = hit.Offset;
                    byte[] buffer = new byte[hit.Length];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = fs.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) { error = "读取不完整。"; return null; }
                        read += n;
                    }
                    return buffer;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 把补丁写到副本上（sourcePath 不动）。每一项都必须与命中区域等长，
        /// 写前再核对一次原位置的签名，防止扫描结果过期后误伤其它数据。
        /// </summary>
        public static bool PatchInPlace(string sourcePath, string outputPath, List<EmbeddedPatchItem> items,
                                        out string error, out string detail)
        {
            error = null;
            detail = null;
            if (items == null || items.Count == 0)
            {
                error = "没有需要写入的图片区域。";
                return false;
            }

            try
            {
                File.Copy(sourcePath, outputPath, true);
                File.SetAttributes(outputPath, File.GetAttributes(outputPath) & ~FileAttributes.ReadOnly);

                using (FileStream fs = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        EmbeddedPatchItem item = items[i];
                        if (item.Data == null || item.Data.Length != item.Hit.Length)
                        {
                            error = String.Format(CultureInfo.InvariantCulture,
                                "替换图长度 {0} 与目标区域 {1} 不一致（容器路线要求等长覆盖）。",
                                item.Data == null ? 0 : item.Data.Length, item.Hit.Length);
                            return false;
                        }
                        if (item.Hit.Offset + item.Hit.Length > fs.Length)
                        {
                            error = "目标区域超出文件末尾：" + item.Hit.OffsetHex;
                            return false;
                        }

                        // 复核原位置仍是同一种图片，避免用到过期的偏移
                        byte[] head = new byte[4];
                        fs.Position = item.Hit.Offset;
                        if (fs.Read(head, 0, 4) != 4)
                        {
                            error = "复核目标区域失败：" + item.Hit.OffsetHex;
                            return false;
                        }
                        if (!LooksLikeHead(head, item.Hit.Format))
                        {
                            error = "目标区域已变化（" + item.Hit.OffsetHex + " 不再是 "
                                    + ImageProbe.FormatText(item.Hit.Format) + "），请重新扫描。";
                            return false;
                        }

                        fs.Position = item.Hit.Offset;
                        fs.Write(item.Data, 0, item.Data.Length);
                    }
                    fs.Flush(true);
                }

                // 写后自检：逐项回读比对
                using (FileStream fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        EmbeddedPatchItem item = items[i];
                        byte[] check = new byte[item.Data.Length];
                        fs.Position = item.Hit.Offset;
                        int read = 0;
                        while (read < check.Length)
                        {
                            int n = fs.Read(check, read, check.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read != check.Length)
                        {
                            error = "写后回读不完整：" + item.Hit.OffsetHex;
                            return false;
                        }
                        for (int k = 0; k < check.Length; k++)
                        {
                            if (check[k] != item.Data[k])
                            {
                                error = "写后比对不一致：" + item.Hit.OffsetHex + " 偏移 " + k;
                                return false;
                            }
                        }
                    }
                }

                detail = String.Format(CultureInfo.InvariantCulture,
                    "{0} 个内嵌图片区域等长覆盖完成，容器索引未改动。", items.Count);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool LooksLikeHead(byte[] head, SplashImageFormat format)
        {
            switch (format)
            {
                case SplashImageFormat.Png:
                    return head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
                case SplashImageFormat.Bmp:
                    return head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M';
                case SplashImageFormat.Jpeg:
                    return head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
                default:
                    return false;
            }
        }
    }
}
