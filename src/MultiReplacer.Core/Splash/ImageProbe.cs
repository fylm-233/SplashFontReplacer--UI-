using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiReplacer.Core
{
    /// <summary>启动图在磁盘上的实际存储格式。</summary>
    public enum SplashImageFormat
    {
        Unknown = 0,
        Png = 1,
        Bmp = 2,
        Jpeg = 3
    }

    /// <summary>
    /// 图像头嗅探。
    ///
    /// 各软件存启动图的格式并不统一：Adobe AIR 系用 PNG，Unreal / Maya 用 BMP，
    /// 个别老版本用 JPEG。这里只读文件头，不解码像素，因此可以安全地用在
    /// "扫一遍几万个资源" 的场景里。
    /// </summary>
    public static class ImageProbe
    {
        /// <summary>小于这个尺寸的多半是图标/按钮，不作为启动图候选。</summary>
        public const int DefaultMinWidth = 200;
        public const int DefaultMinHeight = 150;

        private static readonly byte[] PngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static string FormatText(SplashImageFormat format)
        {
            switch (format)
            {
                case SplashImageFormat.Png: return "PNG";
                case SplashImageFormat.Bmp: return "BMP";
                case SplashImageFormat.Jpeg: return "JPEG";
                default: return "未知";
            }
        }

        /// <summary>
        /// 从任意偏移嗅探一个完整图像：格式、宽高、以及它占用的字节数。
        /// 长度判定对 PNG 走 chunk 链，对 BMP 走 bfSize（必要时按行宽推算），
        /// 对 JPEG 走到 EOI 标记，全部带边界检查，不会越过 available。
        /// </summary>
        public static bool TryRead(byte[] data, int offset, int available, out SplashImageFormat format,
                                   out int width, out int height, out int length)
        {
            format = SplashImageFormat.Unknown;
            width = 0;
            height = 0;
            length = 0;
            if (data == null || offset < 0 || available <= 0 || offset + available > data.Length) return false;

            if (available >= 33 && Match(data, offset, PngSignature))
            {
                if (!TryReadPng(data, offset, available, out width, out height, out length)) return false;
                format = SplashImageFormat.Png;
                return true;
            }

            if (available >= 26 && data[offset] == (byte)'B' && data[offset + 1] == (byte)'M')
            {
                if (!TryReadBmp(data, offset, available, out width, out height, out length)) return false;
                format = SplashImageFormat.Bmp;
                return true;
            }

            if (available >= 4 && data[offset] == 0xFF && data[offset + 1] == 0xD8 && data[offset + 2] == 0xFF)
            {
                if (!TryReadJpeg(data, offset, available, out width, out height, out length)) return false;
                format = SplashImageFormat.Jpeg;
                return true;
            }

            return false;
        }

        /// <summary>只需要宽高时用这个，不做完整长度校验（PE 资源已经有精确长度）。</summary>
        public static bool TryReadSize(byte[] head, out SplashImageFormat format, out int width, out int height)
        {
            format = SplashImageFormat.Unknown;
            width = 0;
            height = 0;
            if (head == null || head.Length < 26) return false;

            if (head.Length >= 24 && Match(head, 0, PngSignature))
            {
                if (head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return false;
                width = (int)ByteTools.ReadUInt32BE(head, 16);
                height = (int)ByteTools.ReadUInt32BE(head, 20);
                if (width <= 0 || height <= 0) return false;
                format = SplashImageFormat.Png;
                return true;
            }

            int w, h, len;
            if (head[0] == (byte)'B' && head[1] == (byte)'M' && head.Length >= 26)
            {
                int biSize = BitConverter.ToInt32(head, 14);
                if (!IsSaneBmpHeaderSize(biSize)) return false;
                w = BitConverter.ToInt32(head, 18);
                h = Math.Abs(BitConverter.ToInt32(head, 22));
                if (w <= 0 || h <= 0) return false;
                format = SplashImageFormat.Bmp;
                width = w;
                height = h;
                return true;
            }

            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            {
                // 头部缓冲区通常已经覆盖 SOF，直接扫一遍即可
                if (TryReadJpeg(head, 0, head.Length, out w, out h, out len))
                {
                    format = SplashImageFormat.Jpeg;
                    width = w;
                    height = h;
                    return true;
                }
            }
            return false;
        }

        // ---------------- PNG ----------------

        private static bool TryReadPng(byte[] data, int offset, int available, out int width, out int height, out int length)
        {
            width = 0;
            height = 0;
            length = 0;
            int end = offset + available;
            if (offset + 33 > end) return false;
            if (data[offset + 12] != 'I' || data[offset + 13] != 'H' || data[offset + 14] != 'D' || data[offset + 15] != 'R') return false;

            width = (int)ByteTools.ReadUInt32BE(data, offset + 16);
            height = (int)ByteTools.ReadUInt32BE(data, offset + 20);
            if (width <= 0 || height <= 0) return false;

            int pos = offset + 8;
            for (int guard = 0; guard < 100000; guard++)
            {
                if (pos + 12 > end) return false;
                uint chunkLength = ByteTools.ReadUInt32BE(data, pos);
                if (chunkLength > Int32.MaxValue) return false;
                int intLength = (int)chunkLength;
                if (pos + 12 + intLength > end) return false;

                bool isIend = data[pos + 4] == 'I' && data[pos + 5] == 'E' && data[pos + 6] == 'N' && data[pos + 7] == 'D';
                pos += 12 + intLength;
                if (isIend)
                {
                    length = pos - offset;
                    return length > 0;
                }
            }
            return false;
        }

        // ---------------- BMP ----------------

        private static bool IsSaneBmpHeaderSize(int biSize)
        {
            return biSize == 12 || biSize == 16 || biSize == 40 || biSize == 52 || biSize == 56
                   || biSize == 64 || biSize == 108 || biSize == 124;
        }

        private static bool TryReadBmp(byte[] data, int offset, int available, out int width, out int height, out int length)
        {
            width = 0;
            height = 0;
            length = 0;
            int end = offset + available;
            if (offset + 26 > end) return false;

            uint fileSize = BitConverter.ToUInt32(data, offset + 2);
            uint offBits = BitConverter.ToUInt32(data, offset + 10);
            int biSize = BitConverter.ToInt32(data, offset + 14);
            if (!IsSaneBmpHeaderSize(biSize)) return false;
            if (offset + 14 + biSize > end) return false;

            width = BitConverter.ToInt32(data, offset + 18);
            height = Math.Abs(BitConverter.ToInt32(data, offset + 22));
            if (width <= 0 || height <= 0) return false;

            int bitCount = BitConverter.ToUInt16(data, offset + 28);
            int compression = biSize >= 40 ? BitConverter.ToInt32(data, offset + 30) : 0;
            if (bitCount != 1 && bitCount != 4 && bitCount != 8 && bitCount != 16 && bitCount != 24 && bitCount != 32) return false;

            long computed;
            if (compression == 0 || compression == 3 || compression == 6)
            {
                long rowSize = ((long)width * bitCount + 31) / 32 * 4;
                computed = 14 + biSize + rowSize * height;
            }
            else
            {
                computed = 0;
            }

            long declared = fileSize;
            long best = 0;
            if (declared >= 26 && declared <= available) best = declared;
            if (best == 0 && computed > 0 && computed <= available) best = computed;
            if (best == 0) return false;
            if (offBits < 14 + biSize || offBits >= best) return false;

            length = (int)best;
            return true;
        }

        // ---------------- JPEG ----------------

        private static bool TryReadJpeg(byte[] data, int offset, int available, out int width, out int height, out int length)
        {
            width = 0;
            height = 0;
            length = 0;
            int end = offset + available;
            int pos = offset + 2;

            for (int guard = 0; guard < 4096; guard++)
            {
                if (pos + 2 > end) return false;
                if (data[pos] != 0xFF) return false;
                int marker = data[pos + 1];
                pos += 2;

                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (marker == 0xD9)
                {
                    length = pos - offset;
                    return width > 0 && height > 0;
                }
                if (pos + 2 > end) return false;
                int segment = (data[pos] << 8) | data[pos + 1];
                if (segment < 2 || pos + segment > end) return false;

                bool isSof = (marker >= 0xC0 && marker <= 0xCF) && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof && segment >= 7)
                {
                    height = (data[pos + 3] << 8) | data[pos + 4];
                    width = (data[pos + 5] << 8) | data[pos + 6];
                }
                pos += segment;
            }
            return false;
        }

        private static bool Match(byte[] data, int offset, byte[] expected)
        {
            if (offset + expected.Length > data.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (data[offset + i] != expected[i]) return false;
            }
            return true;
        }

        /// <summary>按尺寸判断是否像启动图：横向、非正方形、面积够大。</summary>
        public static bool LooksLikeSplashBySize(int width, int height)
        {
            if (width < DefaultMinWidth || height < DefaultMinHeight) return false;
            if (width == height) return false;
            double ratio = (double)width / height;
            return ratio >= 1.05 && ratio <= 4.0;
        }

        /// <summary>
        /// 纯几何打分。名字不含 splash 时（容器内嵌资源、独立 BMP 等）只能靠它排序。
        /// 经验值来自 Photoshop / After Effects / Unreal 的实际启动图尺寸分布。
        /// </summary>
        public static int ScoreByGeometry(int width, int height, int byteSize)
        {
            if (width <= 0 || height <= 0) return 0;
            double ratio = (double)width / height;
            int score = 0;

            if (ratio >= 1.2 && ratio <= 1.95) score += 40;      // 启动图最常见的长宽比
            else if (ratio > 1.0 && ratio <= 2.6) score += 12;
            else if (ratio > 2.6 && ratio <= 3.6) score += 4;
            else if (width == height) score -= 50;               // 正方形基本是图标/素材
            else if (ratio < 1.0) score -= 35;                   // 竖图不是启动图

            if (width >= 1000 && width <= 2400 && height >= 600 && height <= 1500) score += 20;
            if (width < 500 || height < 400) score -= 25;

            if (byteSize > 0)
            {
                int sizeBonus = Math.Min(30, byteSize / 100000);
                score += sizeBonus;
            }
            return score;
        }

        public static string Describe(int width, int height)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0}×{1}", width, height);
        }
    }
}
