using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MultiReplacer.Core
{
    public sealed class PngAsset
    {
        public int Index;
        public long Offset;
        public int Length;
        public int Width;
        public int Height;
        public int BitDepth;
        public int ColorType;
        public int Interlace;
        public long Area { get { return (long)Width * (long)Height; } }
        public string Dimensions { get { return Width.ToString(CultureInfo.InvariantCulture) + " x " + Height.ToString(CultureInfo.InvariantCulture); } }
        public string OffsetHex { get { return "0x" + Offset.ToString("X", CultureInfo.InvariantCulture); } }
        public string ColorTypeText
        {
            get
            {
                switch (ColorType)
                {
                    case 0: return "Gray";
                    case 2: return "RGB";
                    case 3: return "Indexed";
                    case 4: return "Gray+A";
                    case 6: return "RGBA";
                    default: return "Type " + ColorType.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        public string Sha256;
    }

    public sealed class PeInfo
    {
        public long FileSize;
        public uint Machine;
        public ushort OptionalMagic;
        public int SectionCount;
        public uint StoredChecksum;
        public long ChecksumOffset;
        public bool HasSecurityDirectory;
        public uint SecurityDirectoryOffset;
        public uint SecurityDirectorySize;
        public long SecurityDirectoryEntryOffset;
        public long PeHeaderOffset;
        public long OptionalHeaderOffset;
    }

    public sealed class PatchItem
    {
        public PngAsset Original;
        public byte[] Replacement;
        public string ReplacementPath;
        public string OriginalSha256;
        public string ReplacementSha256;
    }

    public sealed class PatchResult
    {
        public string SourcePath;
        public string OutputPath;
        public string ManifestPath;
        public long OriginalSize;
        public long OutputSize;
        public string OriginalSha256;
        public string OutputSha256;
        public uint OriginalPeChecksum;
        public uint PatchedPeChecksum;
        public bool ChecksumVerified;
        public bool LengthPreserved;
        public bool SignatureDirectoryFound;
        public bool SignatureDirectoryCleared;
        public int ReplacementCount;
        public List<string> VerificationMessages = new List<string>();
    }

    public static class NativeMethods
    {
        [DllImport("imagehlp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint CheckSumMappedFile(IntPtr baseAddress, uint fileLength, out uint headerSum, out uint checkSum);
    }

    public static class HashTools
    {
        public static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(fs));
        }

        public static string Sha256Bytes(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        public static string ToHex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    public static class ByteTools
    {
        public static ushort ReadUInt16BE(byte[] b, int offset)
        {
            return (ushort)((b[offset] << 8) | b[offset + 1]);
        }

        public static uint ReadUInt32BE(byte[] b, int offset)
        {
            return ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];
        }

        public static uint ReadUInt32BE(MemoryMappedViewAccessor view, long offset)
        {
            byte[] b = new byte[4];
            view.ReadArray(offset, b, 0, 4);
            return ReadUInt32BE(b, 0);
        }

        public static bool IsPngSignature(byte[] b, int offset)
        {
            return b[offset] == 0x89 && b[offset + 1] == 0x50 && b[offset + 2] == 0x4E && b[offset + 3] == 0x47
                && b[offset + 4] == 0x0D && b[offset + 5] == 0x0A && b[offset + 6] == 0x1A && b[offset + 7] == 0x0A;
        }

        public static bool IsAsciiLetter(byte b)
        {
            return (b >= 65 && b <= 90) || (b >= 97 && b <= 122);
        }
    }

    public sealed class PngChunk
    {
        public string Type;
        public byte[] Data;
        public uint StoredCrc;
        public long Offset;
    }

    public sealed class PngPacket
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ColorType;
        public int CompressionMethod;
        public int FilterMethod;
        public int InterlaceMethod;
        public List<PngChunk> Chunks = new List<PngChunk>();
        public byte[] RawBytes;
    }

    public static class PngCodec
    {
        private static uint[] crcTable;
        private static readonly object CrcLock = new object();
        private static readonly byte[] PngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static uint[] GetCrcTable()
        {
            lock (CrcLock)
            {
                if (crcTable != null) return crcTable;
                uint[] table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                    {
                        if ((c & 1) != 0) c = 0xEDB88320u ^ (c >> 1);
                        else c = c >> 1;
                    }
                    table[n] = c;
                }
                crcTable = table;
                return table;
            }
        }

        public static uint Crc32(byte[] data, int offset, int count)
        {
            uint[] table = GetCrcTable();
            uint c = 0xFFFFFFFFu;
            int end = offset + count;
            for (int i = offset; i < end; i++) c = table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Crc32Two(byte[] a, byte[] b)
        {
            uint[] table = GetCrcTable();
            uint c = 0xFFFFFFFFu;
            if (a != null)
            {
                for (int i = 0; i < a.Length; i++) c = table[(c ^ a[i]) & 0xFF] ^ (c >> 8);
            }
            if (b != null)
            {
                for (int i = 0; i < b.Length; i++) c = table[(c ^ b[i]) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }

        public static PngPacket Parse(byte[] data, bool verifyCrc, out string error)
        {
            error = null;
            if (data == null || data.Length < 33)
            {
                error = "PNG 文件过小。";
                return null;
            }
            if (!ByteTools.IsPngSignature(data, 0))
            {
                error = "文件头不是 PNG 签名。";
                return null;
            }

            PngPacket packet = new PngPacket();
            packet.RawBytes = data;
            int pos = 8;
            bool sawIhdr = false;
            bool sawIdat = false;
            bool idatEnded = false;
            int chunkCount = 0;

            while (true)
            {
                chunkCount++;
                if (chunkCount > 1000000)
                {
                    error = "PNG chunk 数量异常。";
                    return null;
                }
                if (pos + 12 > data.Length)
                {
                    error = "PNG chunk 头越界。";
                    return null;
                }

                uint length = ByteTools.ReadUInt32BE(data, pos);
                if (length > Int32.MaxValue)
                {
                    error = "PNG chunk 长度过大。";
                    return null;
                }
                int intLength = (int)length;
                if (pos + 12 + intLength > data.Length)
                {
                    error = "PNG chunk 数据越界。";
                    return null;
                }

                byte[] typeBytes = new byte[4];
                Buffer.BlockCopy(data, pos + 4, typeBytes, 0, 4);
                for (int i = 0; i < 4; i++)
                {
                    if (!ByteTools.IsAsciiLetter(typeBytes[i]))
                    {
                        error = "PNG chunk 类型含非法字符。";
                        return null;
                    }
                }
                string type = Encoding.ASCII.GetString(typeBytes);
                byte[] chunkData = new byte[intLength];
                if (intLength > 0) Buffer.BlockCopy(data, pos + 8, chunkData, 0, intLength);
                uint storedCrc = ByteTools.ReadUInt32BE(data, pos + 8 + intLength);

                if (verifyCrc)
                {
                    uint calculated = Crc32Two(typeBytes, chunkData);
                    if (calculated != storedCrc)
                    {
                        error = "PNG chunk " + type + " CRC 校验失败。";
                        return null;
                    }
                }

                PngChunk chunk = new PngChunk();
                chunk.Type = type;
                chunk.Data = chunkData;
                chunk.StoredCrc = storedCrc;
                chunk.Offset = pos;
                packet.Chunks.Add(chunk);

                if (!sawIhdr)
                {
                    if (type != "IHDR" || intLength != 13)
                    {
                        error = "PNG 的首个 chunk 必须是长度为 13 的 IHDR。";
                        return null;
                    }
                    packet.Width = (int)ByteTools.ReadUInt32BE(chunkData, 0);
                    packet.Height = (int)ByteTools.ReadUInt32BE(chunkData, 4);
                    packet.BitDepth = chunkData[8];
                    packet.ColorType = chunkData[9];
                    packet.CompressionMethod = chunkData[10];
                    packet.FilterMethod = chunkData[11];
                    packet.InterlaceMethod = chunkData[12];
                    if (packet.Width <= 0 || packet.Height <= 0)
                    {
                        error = "PNG 尺寸无效。";
                        return null;
                    }
                    if (!IsSupportedBitDepth(packet.BitDepth, packet.ColorType))
                    {
                        error = "PNG 位深/颜色类型组合不受支持。";
                        return null;
                    }
                    if (packet.CompressionMethod != 0 || packet.FilterMethod != 0 || packet.InterlaceMethod > 1)
                    {
                        error = "PNG 使用了不支持的方法字段。";
                        return null;
                    }
                    sawIhdr = true;
                }
                else if (type == "IHDR")
                {
                    error = "PNG 中存在多个 IHDR。";
                    return null;
                }

                if (type == "IDAT")
                {
                    if (idatEnded)
                    {
                        error = "PNG 的 IDAT chunk 不连续。";
                        return null;
                    }
                    sawIdat = true;
                }
                else if (sawIdat && type != "IEND")
                {
                    idatEnded = true;
                }

                pos += 12 + intLength;
                if (type == "IEND")
                {
                    if (intLength != 0)
                    {
                        error = "IEND chunk 长度必须为 0。";
                        return null;
                    }
                    if (pos != data.Length)
                    {
                        error = "PNG 的 IEND 后仍有多余数据。";
                        return null;
                    }
                    break;
                }
            }

            if (!sawIhdr || !sawIdat)
            {
                error = "PNG 缺少 IHDR 或 IDAT。";
                return null;
            }
            return packet;
        }

        private static bool IsSupportedBitDepth(int depth, int colorType)
        {
            switch (colorType)
            {
                case 0: return depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16;
                case 2: return depth == 8 || depth == 16;
                case 3: return depth == 1 || depth == 2 || depth == 4 || depth == 8;
                case 4: return depth == 8 || depth == 16;
                case 6: return depth == 8 || depth == 16;
                default: return false;
            }
        }

        public static byte[] GetIdatData(PngPacket packet)
        {
            int total = 0;
            for (int i = 0; i < packet.Chunks.Count; i++)
                if (packet.Chunks[i].Type == "IDAT") total += packet.Chunks[i].Data.Length;
            byte[] result = new byte[total];
            int pos = 0;
            for (int i = 0; i < packet.Chunks.Count; i++)
            {
                PngChunk c = packet.Chunks[i];
                if (c.Type == "IDAT")
                {
                    Buffer.BlockCopy(c.Data, 0, result, pos, c.Data.Length);
                    pos += c.Data.Length;
                }
            }
            return result;
        }

        public static byte[] BuildChunk(string type, byte[] data)
        {
            if (type == null || type.Length != 4) throw new ArgumentException("PNG chunk type must be 4 characters.", "type");
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] chunk = new byte[12 + data.Length];
            chunk[0] = (byte)(data.Length >> 24);
            chunk[1] = (byte)(data.Length >> 16);
            chunk[2] = (byte)(data.Length >> 8);
            chunk[3] = (byte)data.Length;
            Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4);
            if (data.Length > 0) Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
            uint crc = Crc32Two(typeBytes, data);
            chunk[8 + data.Length] = (byte)(crc >> 24);
            chunk[9 + data.Length] = (byte)(crc >> 16);
            chunk[10 + data.Length] = (byte)(crc >> 8);
            chunk[11 + data.Length] = (byte)crc;
            return chunk;
        }

        private static long FixedLengthWithoutIdat(PngPacket packet)
        {
            long total = 8 + 12;
            for (int i = 0; i < packet.Chunks.Count; i++)
            {
                PngChunk c = packet.Chunks[i];
                if (c.Type == "IEND" || c.Type == "IDAT") continue;
                total += 12 + c.Data.Length;
            }
            return total;
        }

        private static byte[] BuildWithIdat(PngPacket packet, byte[] compressed, int idatCount, int paddingDataLength)
        {
            MemoryStream ms = new MemoryStream();
            ms.Write(PngSignature, 0, PngSignature.Length);
            bool wroteIdat = false;
            for (int i = 0; i < packet.Chunks.Count; i++)
            {
                PngChunk c = packet.Chunks[i];
                if (c.Type == "IEND") continue;
                if (c.Type == "IDAT")
                {
                    if (!wroteIdat)
                    {
                        WriteSplitIdat(ms, compressed, idatCount);
                        wroteIdat = true;
                    }
                    continue;
                }
                byte[] chunk = BuildChunk(c.Type, c.Data);
                ms.Write(chunk, 0, chunk.Length);
            }
            if (!wroteIdat) WriteSplitIdat(ms, compressed, idatCount);
            if (paddingDataLength > 0)
            {
                byte[] padChunk = BuildChunk("raNd", new byte[paddingDataLength]);
                ms.Write(padChunk, 0, padChunk.Length);
            }
            byte[] iend = BuildChunk("IEND", new byte[0]);
            ms.Write(iend, 0, iend.Length);
            return ms.ToArray();
        }

        private static void WriteSplitIdat(Stream stream, byte[] compressed, int count)
        {
            if (count < 1) count = 1;
            if (count > compressed.Length) count = Math.Max(1, compressed.Length);
            int baseSize = compressed.Length / count;
            int remainder = compressed.Length % count;
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                int size = baseSize + (i < remainder ? 1 : 0);
                byte[] part = new byte[size];
                if (size > 0) Buffer.BlockCopy(compressed, pos, part, 0, size);
                byte[] chunk = BuildChunk("IDAT", part);
                stream.Write(chunk, 0, chunk.Length);
                pos += size;
            }
        }

        private static bool TryReadInflated(PngPacket packet, out byte[] inflated, out string error)
        {
            inflated = null;
            error = null;
            byte[] zlib = GetIdatData(packet);
            if (zlib.Length < 7)
            {
                error = "IDAT 压缩数据过短。";
                return false;
            }
            int cmf = zlib[0];
            int flg = zlib[1];
            int method = cmf & 15;
            int window = cmf >> 4;
            bool hasPresetDictionary = (flg & 0x20) != 0;
            if (method != 8 || window > 7 || hasPresetDictionary || ((cmf << 8) + flg) % 31 != 0)
            {
                error = "IDAT 不是支持的 zlib 流。";
                return false;
            }
            byte[] rawDeflate = new byte[zlib.Length - 6];
            Buffer.BlockCopy(zlib, 2, rawDeflate, 0, rawDeflate.Length);
            uint expectedAdler = ByteTools.ReadUInt32BE(zlib, zlib.Length - 4);
            try
            {
                using (MemoryStream input = new MemoryStream(rawDeflate))
                using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
                    inflated = output.ToArray();
                }
                if (Adler32(inflated) != expectedAdler)
                {
                    error = "IDAT 的 Adler-32 校验失败。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "IDAT 解压失败: " + ex.Message;
                return false;
            }
        }

        private static byte[] CompressInflatedZlib(byte[] inflated, CompressionLevel level)
        {
            using (MemoryStream output = new MemoryStream())
            {
                // FLG 的 FLEVEL 只是提示位，但写对更规范：(CMF<<8|FLG) 必须能被 31 整除
                byte flg;
                if (level == CompressionLevel.SmallestSize) flg = 0xDA;      // 级别 7~9
                else if (level == CompressionLevel.Optimal) flg = 0x9C;      // 级别 6
                else if (level == CompressionLevel.NoCompression) flg = 0x01;
                else flg = 0x01;                                            // Fastest
                output.WriteByte(0x78);
                output.WriteByte(flg);
                using (DeflateStream deflate = new DeflateStream(output, level, true))
                {
                    deflate.Write(inflated, 0, inflated.Length);
                }
                uint adler = Adler32(inflated);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return output.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint modAdler = 65521;
            uint a = 1;
            uint b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % modAdler;
                b = (b + a) % modAdler;
            }
            return (b << 16) | a;
        }

        public static byte[] FitToExactLength(byte[] replacement, int targetLength, out string error)
        {
            error = null;
            PngPacket packet = Parse(replacement, true, out error);
            if (packet == null) return null;
            if (targetLength <= 0)
            {
                error = "目标长度无效。";
                return null;
            }

            List<KeyValuePair<string, byte[]>> streams = new List<KeyValuePair<string, byte[]>>();
            streams.Add(new KeyValuePair<string, byte[]>("原压缩流", GetIdatData(packet)));

            byte[] inflated;
            string deflateError;
            if (TryReadInflated(packet, out inflated, out deflateError))
            {
                // 顺序很关键：目标长度一般就是"原图长度"，只有最高压缩档才压得进去。
                // 早期版本只试到 Optimal（级别 6），碰上系统编码器重新编码过、比原图大 5%~10% 的 PNG
                // 就会直接失败——这正是"原图导出去再导回来放不进去"的原因。
                streams.Add(new KeyValuePair<string, byte[]>("zlib/smallest", CompressInflatedZlib(inflated, CompressionLevel.SmallestSize)));
                streams.Add(new KeyValuePair<string, byte[]>("zlib/optimal", CompressInflatedZlib(inflated, CompressionLevel.Optimal)));
                streams.Add(new KeyValuePair<string, byte[]>("zlib/fast", CompressInflatedZlib(inflated, CompressionLevel.Fastest)));
                streams.Add(new KeyValuePair<string, byte[]>("zlib/store", CompressInflatedZlib(inflated, CompressionLevel.NoCompression)));
            }
            else
            {
                inflated = null;
            }

            long fixedLength = FixedLengthWithoutIdat(packet);
            long bestBaseLength = Int64.MaxValue;

            byte[] fitted = TryBuildAtLength(packet, streams, fixedLength, targetLength, ref bestBaseLength, out error);
            if (fitted != null) return fitted;

            // 无损也塞不进去时，走"重打包"阶梯：先把扫描线还原成像素重挑过滤器，
            // 再逐级降色深（8→6→4 位），最后才丢 alpha。
            // 每级都按 zlib 最高档压一遍，命中即停，所以正常情况只跑第一级。
            if (inflated != null)
            {
                bool[] normalize = { false, true, true, true, true, true, true, true };
                int[] bits = { 0, 0, 6, 5, 4, 6, 5, 4 };
                bool[] dropAlpha = { false, false, false, false, false, true, true, true };
                string[] names = { "refilter", "normalized", "quant6", "quant5", "quant4",
                                   "quant6/opaque", "quant5/opaque", "quant4/opaque" };

                for (int step = 0; step < names.Length; step++)
                {
                    PngPacket usePacket;
                    byte[] rows = TryRepackScanlines(inflated, packet, normalize[step], bits[step],
                                                     dropAlpha[step], out usePacket);
                    if (rows == null) continue;

                    List<KeyValuePair<string, byte[]>> one = new List<KeyValuePair<string, byte[]>>();
                    one.Add(new KeyValuePair<string, byte[]>(names[step],
                        CompressInflatedZlib(rows, CompressionLevel.SmallestSize)));
                    fitted = TryBuildAtLength(usePacket, one, fixedLength, targetLength, ref bestBaseLength, out error);
                    if (fitted != null) return fitted;
                }
            }

            if (bestBaseLength == Int64.MaxValue)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "无法把新 PNG 压缩到目标长度 {0:N0} 字节；连无损重压都没能产出可用结果，这张图可能不是标准 PNG。",
                    targetLength);
            }
            else if (bestBaseLength > targetLength)
            {
                // 记录的是"所有尝试里最省的那一版"。把超出比例报出来，
                // 用户才知道该换多大的图，而不是只看到一句"仍过大"。
                double over = (double)(bestBaseLength - targetLength) / targetLength * 100.0;
                error = string.Format(CultureInfo.InvariantCulture,
                    "无法把新 PNG 压缩到目标长度 {0:N0} 字节：最省的一版仍有 {1:N0} 字节，超出 {2:0.0}%。"
                    + "这张图细节太多，请换一张更简单或尺寸更小的图。",
                    targetLength, bestBaseLength, over);
            }
            else
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "无法精确补齐到 {0:N0} 字节；最接近的合法 PNG 基础长度为 {1:N0} 字节。请换用尺寸相同、压缩大小略有差别的 PNG。",
                    targetLength, bestBaseLength);
            }
            return null;
        }

        /// <summary>在一组压缩流里找"能塞进目标长度"的那个，塞进去后用私有 ancillary 块补齐到精确长度。</summary>
        private static byte[] TryBuildAtLength(PngPacket packet, List<KeyValuePair<string, byte[]>> streams,
                                               long fixedLength, int targetLength, ref long bestBaseLength,
                                               out string error)
        {
            error = null;
            for (int s = 0; s < streams.Count; s++)
            {
                byte[] compressed = streams[s].Value;
                if (compressed == null) continue;
                int[] counts = CandidateIdatCounts(compressed.Length);
                for (int ci = 0; ci < counts.Length; ci++)
                {
                    int count = counts[ci];
                    long baseLength = fixedLength + compressed.Length + 12L * count;
                    // 先记最省长度（不论是否超标），失败时才能报出"超出多少"。
                    if (baseLength < bestBaseLength) bestBaseLength = baseLength;
                    if (baseLength > targetLength) continue;
                    long gap = targetLength - baseLength;
                    if (gap >= 12 || gap == 0)
                    {
                        int paddingLength = gap == 0 ? 0 : (int)(gap - 12);
                        byte[] result = BuildWithIdat(packet, compressed, count, paddingLength);
                        if (result.Length != targetLength)
                        {
                            error = "内部错误：重建 PNG 后长度不匹配。";
                            return null;
                        }
                        PngPacket verify = Parse(result, true, out error);
                        if (verify == null) return null;
                        if (verify.Width != packet.Width || verify.Height != packet.Height)
                        {
                            error = "内部错误：重建 PNG 尺寸变化。";
                            return null;
                        }
                        return result;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 把"已过滤的扫描线"还原成像素，再按最小绝对值和重新挑过滤器。
        /// 返回新的扫描线（含每行的过滤器字节）；不支持的形态返回 null。
        /// </summary>
        /// <summary>
        /// 把"已过滤的扫描线"还原成像素，按需要做透明归一化 / 降色深 / 丢 alpha，
        /// 再按最小绝对值和重新挑过滤器。返回新的扫描线（含每行的过滤器字节）。
        ///
        /// quantBits：0 表示不量化；6 表示每通道只保留高 6 位（色深从 8 降到 6）。
        /// dropAlpha：RGBA 图丢掉 alpha 通道，改写成 RGB（IHDR 的 colorType 6 → 2）。
        /// 这两个都是有损操作，只在无损压缩塞不进固定长度时才启用。
        /// </summary>
        private static byte[] TryRepackScanlines(byte[] inflated, PngPacket packet, bool normalizeTransparent,
                                                 int quantBits, bool dropAlpha, out PngPacket outPacket)
        {
            outPacket = packet;
            if (packet.InterlaceMethod != 0) return null;
            if (packet.BitDepth != 8 && packet.BitDepth != 16) return null;

            int channels;
            switch (packet.ColorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 3: channels = 1; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: return null;
            }

            bool canDropAlpha = dropAlpha && packet.ColorType == 6 && packet.BitDepth == 8;
            bool canQuantize = quantBits > 0 && quantBits < 8 && packet.BitDepth == 8
                               && (packet.ColorType == 2 || packet.ColorType == 6);

            int bpp = channels * (packet.BitDepth / 8);
            int outChannels = canDropAlpha ? 3 : channels;
            if (packet.Width <= 0 || packet.Height <= 0 || bpp <= 0) return null;

            int inStride = packet.Width * bpp;
            int outStride = packet.Width * outChannels;
            long expected = (long)(inStride + 1) * packet.Height;
            if (inflated.Length < expected) return null;

            byte[] result = new byte[(long)(outStride + 1) * packet.Height];
            byte[] prevIn = new byte[inStride];
            byte[] cur = new byte[inStride];
            byte[] outRow = new byte[outStride];
            byte[] prevOut = new byte[outStride];
            byte[] best = new byte[outStride];
            byte[] scratch = new byte[outStride];

            int src = 0;
            int dst = 0;
            int normalizedCount = 0;
            for (int y = 0; y < packet.Height; y++)
            {
                int filter = inflated[src++];
                Buffer.BlockCopy(inflated, src, cur, 0, inStride);
                src += inStride;

                UnfilterRow(filter, cur, prevIn, bpp);

                // 必须先把"还原后的原始行"存成下一行的参考，再做透明归一化。
                // 归一化会就地改 cur，若晚于这里复制，后续行的反过滤就会用被改过的参考行，
                // 误差会逐行累积成斜向条纹。
                Buffer.BlockCopy(cur, 0, prevIn, 0, inStride);

                if (normalizeTransparent)
                    normalizedCount += NormalizeTransparentRow(cur, packet.ColorType, packet.BitDepth);

                // 生成输出行：丢 alpha / 降色深都在这里完成
                if (outChannels != channels || canQuantize)
                {
                    for (int x = 0; x < packet.Width; x++)
                    {
                        int si = x * bpp;
                        int di = x * outChannels;
                        for (int c = 0; c < outChannels; c++)
                        {
                            int v = cur[si + c];
                            if (canQuantize && c < 3) v = (v >> (8 - quantBits)) << (8 - quantBits);
                            outRow[di + c] = (byte)v;
                        }
                    }
                }
                else
                {
                    Buffer.BlockCopy(cur, 0, outRow, 0, outStride);
                }

                int bestFilter = 0;
                long bestScore = Int64.MaxValue;
                for (int f = 0; f < 5; f++)
                {
                    FilterRow(f, outRow, prevOut, outChannels, scratch);
                    long score = 0;
                    for (int i = 0; i < outStride; i++)
                    {
                        int v = scratch[i];
                        score += v < 128 ? v : 256 - v;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFilter = f;
                        Buffer.BlockCopy(scratch, 0, best, 0, outStride);
                    }
                }

                result[dst++] = (byte)bestFilter;
                Buffer.BlockCopy(best, 0, result, dst, outStride);
                dst += outStride;

                Buffer.BlockCopy(outRow, 0, prevOut, 0, outStride);
            }

            // 归一化没改动任何像素，说明这一级"只为归一化而来"，交回去让上层跳过。
            // 注意别把降色深/丢 alpha 的档也一起挡掉：整张图没有全透明像素是很常见的情况
            // （不透明的照片、截图都是），那些档照样有价值。早期版本这里判断得太宽，
            // 导致"没有透明像素的图"永远走不到量化，超体积时只能报失败。
            if (normalizeTransparent && normalizedCount == 0 && !canQuantize && !canDropAlpha) return null;

            if (canDropAlpha) outPacket = WithColorType(packet, 2);
            return result;
        }

        /// <summary>克隆一份包描述，只把 IHDR 里的 colorType 改掉（其余块原样保留）。</summary>
        private static PngPacket WithColorType(PngPacket packet, int colorType)
        {
            PngPacket clone = new PngPacket();
            clone.Width = packet.Width;
            clone.Height = packet.Height;
            clone.BitDepth = packet.BitDepth;
            clone.ColorType = colorType;
            clone.CompressionMethod = packet.CompressionMethod;
            clone.FilterMethod = packet.FilterMethod;
            clone.InterlaceMethod = packet.InterlaceMethod;
            clone.RawBytes = packet.RawBytes;

            for (int i = 0; i < packet.Chunks.Count; i++)
            {
                PngChunk c = packet.Chunks[i];
                PngChunk copy = new PngChunk();
                copy.Type = c.Type;
                copy.Data = c.Data;
                copy.StoredCrc = c.StoredCrc;
                copy.Offset = c.Offset;
                if (c.Type == "IHDR" && c.Data != null && c.Data.Length >= 13)
                {
                    byte[] data = new byte[c.Data.Length];
                    Buffer.BlockCopy(c.Data, 0, data, 0, c.Data.Length);
                    data[9] = (byte)colorType;
                    copy.Data = data;
                }
                clone.Chunks.Add(copy);
            }
            return clone;
        }

        /// <summary>
        /// 把 alpha=0 的像素颜色压成 0（不可见区域），返回被改动的像素数。
        /// 只支持 8 位深度；16 位或调色板图直接跳过。
        /// </summary>
        private static int NormalizeTransparentRow(byte[] row, int colorType, int bitDepth)
        {
            if (bitDepth != 8) return 0;
            int changed = 0;
            if (colorType == 6)
            {
                for (int i = 0; i + 3 < row.Length; i += 4)
                {
                    if (row[i + 3] != 0) continue;
                    if (row[i] == 0 && row[i + 1] == 0 && row[i + 2] == 0) continue;
                    row[i] = 0; row[i + 1] = 0; row[i + 2] = 0;
                    changed++;
                }
            }
            else if (colorType == 4)
            {
                for (int i = 0; i + 1 < row.Length; i += 2)
                {
                    if (row[i + 1] != 0 || row[i] == 0) continue;
                    row[i] = 0;
                    changed++;
                }
            }
            return changed;
        }

        /// <summary>把一行按 PNG 规范还原（就地写回 row）。</summary>
        private static void UnfilterRow(int filter, byte[] row, byte[] prev, int bpp)
        {
            int n = row.Length;
            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < n; i++) row[i] = (byte)(row[i] + row[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < n; i++) row[i] = (byte)(row[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        row[i] = (byte)(row[i] + ((a + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        row[i] = (byte)(row[i] + Paeth(a, b, c));
                    }
                    break;
                default:
                    break;
            }
        }

        /// <summary>按指定过滤器把原始行编码进 dst。</summary>
        private static void FilterRow(int filter, byte[] row, byte[] prev, int bpp, byte[] dst)
        {
            int n = row.Length;
            switch (filter)
            {
                case 0:
                    Buffer.BlockCopy(row, 0, dst, 0, n);
                    break;
                case 1:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        dst[i] = (byte)(row[i] - a);
                    }
                    break;
                case 2:
                    for (int i = 0; i < n; i++) dst[i] = (byte)(row[i] - prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        dst[i] = (byte)(row[i] - ((a + prev[i]) >> 1));
                    }
                    break;
                default:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        dst[i] = (byte)(row[i] - Paeth(a, b, c));
                    }
                    break;
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = p > a ? p - a : a - p;
            int pb = p > b ? p - b : b - p;
            int pc = p > c ? p - c : c - p;
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static int[] CandidateIdatCounts(int compressedLength)
        {
            List<int> values = new List<int>();
            AddCount(values, 1);
            int[] chunkSizes = new int[] { 1048576, 262144, 65536, 32768, 16384, 8192, 4096, 1024 };
            for (int i = 0; i < chunkSizes.Length; i++)
            {
                int count = (compressedLength + chunkSizes[i] - 1) / chunkSizes[i];
                AddCount(values, count);
            }
            for (int c = 2; c <= 64 && c <= compressedLength; c++) AddCount(values, c);
            return values.ToArray();
        }

        private static void AddCount(List<int> values, int count)
        {
            if (count < 1) count = 1;
            if (!values.Contains(count)) values.Add(count);
        }

        public static byte[] ExtractAsset(string path, PngAsset asset)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] data = new byte[asset.Length];
                fs.Position = asset.Offset;
                int total = 0;
                while (total < data.Length)
                {
                    int read = fs.Read(data, total, data.Length - total);
                    if (read <= 0) throw new EndOfStreamException("读取 PNG 资源失败。");
                    total += read;
                }
                return data;
            }
        }
    }

    public static class PeTools
    {
        public static PeInfo Parse(string path, out string error)
        {
            error = null;
            PeInfo info = new PeInfo();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                info.FileSize = fs.Length;
                if (fs.Length < 512 || fs.Length > UInt32.MaxValue)
                {
                    error = "不是可处理的 PE/EXE 文件（大小必须在 512B 到 4GB 之间）。";
                    return null;
                }
                BinaryReader br = new BinaryReader(fs, Encoding.ASCII);
                if (br.ReadUInt16() != 0x5A4D)
                {
                    error = "缺少 MZ 头。";
                    return null;
                }
                fs.Position = 0x3C;
                uint peOffset = br.ReadUInt32();
                if (peOffset + 24 > fs.Length)
                {
                    error = "PE 头偏移越界。";
                    return null;
                }
                info.PeHeaderOffset = peOffset;
                fs.Position = peOffset;
                if (br.ReadUInt32() != 0x00004550)
                {
                    error = "缺少 PE 签名。";
                    return null;
                }
                info.Machine = br.ReadUInt16();
                info.SectionCount = br.ReadUInt16();
                fs.Position = peOffset + 20;
                ushort optionalSize = br.ReadUInt16();
                fs.Position = peOffset + 24;
                info.OptionalHeaderOffset = peOffset + 24;
                info.OptionalMagic = br.ReadUInt16();
                if (info.OptionalMagic != 0x10B && info.OptionalMagic != 0x20B)
                {
                    error = "不支持的 PE 可选头格式。";
                    return null;
                }
                info.ChecksumOffset = peOffset + 24 + 64;
                fs.Position = info.ChecksumOffset;
                info.StoredChecksum = br.ReadUInt32();

                int securityDirectoryRelative = info.OptionalMagic == 0x20B ? 112 : 96;
                securityDirectoryRelative += 4 * 8;
                info.SecurityDirectoryEntryOffset = peOffset + 24 + securityDirectoryRelative;
                if (optionalSize >= securityDirectoryRelative + 8 && info.SecurityDirectoryEntryOffset + 8 <= fs.Length)
                {
                    fs.Position = info.SecurityDirectoryEntryOffset;
                    info.SecurityDirectoryOffset = br.ReadUInt32();
                    info.SecurityDirectorySize = br.ReadUInt32();
                    info.HasSecurityDirectory = info.SecurityDirectoryOffset != 0 && info.SecurityDirectorySize != 0;
                }
                return info;
            }
        }

        public static uint ComputeChecksum(string path, out uint headerChecksum, out string error)
        {
            headerChecksum = 0;
            error = null;
            PeInfo pe = Parse(path, out error);
            if (pe == null) return 0;
            using (MemoryMappedFile mm = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (MemoryMappedViewAccessor view = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                return ComputeChecksum(view, pe.FileSize, out headerChecksum, out error);
            }
        }

        public static unsafe uint ComputeChecksum(MemoryMappedViewAccessor view, long fileLength, out uint headerChecksum, out string error)
        {
            headerChecksum = 0;
            error = null;
            if (fileLength > UInt32.MaxValue)
            {
                error = "文件超过 4GB，PE 校验和 API 不支持。";
                return 0;
            }
            byte* pointer = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                uint check;
                uint result = NativeMethods.CheckSumMappedFile((IntPtr)pointer, (uint)fileLength, out headerChecksum, out check);
                if (check != 0) return check;
                return result;
            }
            catch (Exception ex)
            {
                error = "PE 校验和计算失败: " + ex.Message;
                return 0;
            }
            finally
            {
                if (pointer != null) view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        public static unsafe uint WriteChecksumAndVerify(MemoryMappedViewAccessor view, long fileLength, long checksumOffset, out bool verified, out string error)
        {
            verified = false;
            error = null;
            uint header;
            uint checksum = ComputeChecksum(view, fileLength, out header, out error);
            if (!string.IsNullOrEmpty(error)) return 0;
            view.Write(checksumOffset, checksum);
            view.Flush();
            uint verifyHeader;
            uint verifyChecksum = ComputeChecksum(view, fileLength, out verifyHeader, out error);
            if (!string.IsNullOrEmpty(error)) return checksum;
            uint stored = view.ReadUInt32(checksumOffset);
            verified = stored == verifyChecksum;
            if (!verified) error = "写入后的 PE 校验和复核失败。";
            return checksum;
        }
    }

    public static class PngScanner
    {
        private static readonly byte[] Signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static List<PngAsset> Scan(string path, int minDimension, out PeInfo pe, out string error)
        {
            pe = null;
            error = null;
            pe = PeTools.Parse(path, out error);
            if (pe == null) return null;

            List<PngAsset> result = new List<PngAsset>();
            using (MemoryMappedFile mm = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (MemoryMappedViewAccessor view = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                const int BufferSize = 1024 * 1024;
                byte[] buffer = new byte[BufferSize + Signature.Length - 1];
                long pos = 0;
                int carry = 0;
                while (pos < pe.FileSize)
                {
                    int want = (int)Math.Min((long)BufferSize, pe.FileSize - pos);
                    view.ReadArray(pos, buffer, carry, want);
                    int total = carry + want;
                    int limit = total - Signature.Length;
                    for (int i = 0; i <= limit; i++)
                    {
                        if (!ByteTools.IsPngSignature(buffer, i)) continue;
                        long absolute = pos - carry + i;
                        int pngLength;
                        PngAsset asset;
                        if (TryParseAt(view, pe.FileSize, absolute, out pngLength, out asset))
                        {
                            if (Math.Max(asset.Width, asset.Height) >= minDimension)
                            {
                                asset.Index = result.Count;
                                result.Add(asset);
                            }
                            int consumed = (int)Math.Min((long)total - i, (long)pngLength);
                            i += Math.Max(0, consumed - 1);
                        }
                    }
                    carry = Math.Min(Signature.Length - 1, total);
                    if (carry > 0) Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                    pos += want;
                }
            }
            return result;
        }

        private static bool TryParseAt(MemoryMappedViewAccessor view, long fileLength, long offset, out int pngLength, out PngAsset asset)
        {
            pngLength = 0;
            asset = null;
            if (offset + 33 > fileLength) return false;

            byte[] head = new byte[33];
            view.ReadArray(offset, head, 0, head.Length);
            if (!ByteTools.IsPngSignature(head, 0)) return false;
            if (ByteTools.ReadUInt32BE(head, 8) != 13 || Encoding.ASCII.GetString(head, 12, 4) != "IHDR") return false;

            int width = (int)ByteTools.ReadUInt32BE(head, 16);
            int height = (int)ByteTools.ReadUInt32BE(head, 20);
            if (width <= 0 || height <= 0) return false;

            long pos = offset + 8;
            int chunks = 0;
            while (pos + 12 <= fileLength)
            {
                chunks++;
                if (chunks > 1000000) return false;
                uint length = ByteTools.ReadUInt32BE(view, pos);
                if (length > Int32.MaxValue || pos + 12 + length > fileLength) return false;
                byte[] typeBytes = new byte[4];
                view.ReadArray(pos + 4, typeBytes, 0, 4);
                for (int i = 0; i < 4; i++) if (!ByteTools.IsAsciiLetter(typeBytes[i])) return false;
                string type = Encoding.ASCII.GetString(typeBytes);
                pos += 12 + length;
                if (type == "IEND")
                {
                    if (length != 0 || pos - offset > Int32.MaxValue) return false;
                    PngAsset a = new PngAsset();
                    a.Offset = offset;
                    a.Length = (int)(pos - offset);
                    a.Width = width;
                    a.Height = height;
                    a.BitDepth = head[24];
                    a.ColorType = head[25];
                    a.Interlace = head[28];
                    asset = a;
                    pngLength = a.Length;
                    return true;
                }
            }
            return false;
        }
    }

    public static class ResolveLocator
    {
        public static string Find()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blackmagic Design", "DaVinci Resolve", "Resolve.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Blackmagic Design", "DaVinci Resolve", "Resolve.exe")
            };
            for (int i = 0; i < candidates.Length; i++) if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i])) return candidates[i];
            return null;
        }

        public static string GetQWindowsPath(string resolveExePath)
        {
            if (String.IsNullOrEmpty(resolveExePath)) return null;
            string dir = Path.GetDirectoryName(resolveExePath.Trim().Trim('"'));
            if (String.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, "platforms", "qwindows.dll");
        }
    }

    public static class Patcher
    {
        public static bool IsProcessRunning(string processName)
        {
            try
            {
                System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcessesByName(processName);
                bool running = processes.Length > 0;
                for (int i = 0; i < processes.Length; i++) processes[i].Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }

        public static PatchResult CreatePatchedCopy(string sourcePath, string outputPath, List<PatchItem> items, bool clearSignature, out string error)
        {
            error = null;
            PatchResult result = new PatchResult();
            result.SourcePath = Path.GetFullPath(sourcePath);
            result.OutputPath = Path.GetFullPath(outputPath);
            if (items == null || items.Count == 0)
            {
                error = "没有待替换资源。";
                return null;
            }
            if (string.Equals(result.SourcePath, result.OutputPath, StringComparison.OrdinalIgnoreCase))
            {
                error = "输出文件不能与源文件相同。";
                return null;
            }

            PeInfo pe = PeTools.Parse(sourcePath, out error);
            if (pe == null) return null;
            result.OriginalSize = pe.FileSize;
            result.OriginalPeChecksum = pe.StoredChecksum;
            result.SignatureDirectoryFound = pe.HasSecurityDirectory;
            result.SignatureDirectoryCleared = clearSignature && pe.HasSecurityDirectory;

            List<PatchItem> sorted = new List<PatchItem>(items);
            sorted.Sort(delegate(PatchItem a, PatchItem b) { return a.Original.Offset.CompareTo(b.Original.Offset); });
            long previousEnd = -1;
            for (int i = 0; i < sorted.Count; i++)
            {
                PatchItem item = sorted[i];
                if (item.Replacement == null || item.Replacement.Length != item.Original.Length)
                {
                    error = "替换数据长度与原资源不一致。";
                    return null;
                }
                if (item.Original.Offset < 0 || item.Original.Offset + item.Original.Length > pe.FileSize)
                {
                    error = "资源偏移越界。";
                    return null;
                }
                if (item.Original.Offset < previousEnd)
                {
                    error = "替换区域重叠。";
                    return null;
                }
                previousEnd = item.Original.Offset + item.Original.Length;
            }

            string directory = Path.GetDirectoryName(result.OutputPath);
            if (string.IsNullOrEmpty(directory)) directory = Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
            if (File.Exists(result.OutputPath)) File.Delete(result.OutputPath);
            File.Copy(sourcePath, result.OutputPath, true);

            try
            {
                using (MemoryMappedFile mm = MemoryMappedFile.CreateFromFile(result.OutputPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
                using (MemoryMappedViewAccessor view = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        PatchItem item = sorted[i];
                        view.WriteArray(item.Original.Offset, item.Replacement, 0, item.Replacement.Length);
                    }

                    if (clearSignature && pe.HasSecurityDirectory)
                    {
                        view.Write(pe.SecurityDirectoryEntryOffset, 0u);
                        view.Write(pe.SecurityDirectoryEntryOffset + 4, 0u);
                    }

                    bool checksumVerified;
                    result.PatchedPeChecksum = PeTools.WriteChecksumAndVerify(view, pe.FileSize, pe.ChecksumOffset, out checksumVerified, out error);
                    if (!string.IsNullOrEmpty(error)) return null;
                    result.ChecksumVerified = checksumVerified;

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        PatchItem item = sorted[i];
                        byte[] verify = new byte[item.Replacement.Length];
                        view.ReadArray(item.Original.Offset, verify, 0, verify.Length);
                        string hash = HashTools.Sha256Bytes(verify);
                        if (!string.Equals(hash, item.ReplacementSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            error = "写回后的资源校验失败: " + item.Original.OffsetHex;
                            return null;
                        }
                        PngPacket packet = PngCodec.Parse(verify, true, out error);
                        if (packet == null) return null;
                        if (packet.Width != item.Original.Width || packet.Height != item.Original.Height)
                        {
                            error = "写回后的 PNG 尺寸不一致: " + item.Original.OffsetHex;
                            return null;
                        }
                    }
                    view.Flush();
                }

                result.OutputSize = new FileInfo(result.OutputPath).Length;
                result.LengthPreserved = result.OriginalSize == result.OutputSize;
                if (!result.LengthPreserved)
                {
                    error = "输出文件长度发生变化。";
                    return null;
                }
                result.OriginalSha256 = HashTools.Sha256File(sourcePath);
                result.OutputSha256 = HashTools.Sha256File(result.OutputPath);
                result.ReplacementCount = sorted.Count;
                result.VerificationMessages.Add("文件长度保持不变。");
                result.VerificationMessages.Add("每个替换 PNG 均已重新解析并验证 chunk CRC。");
                result.VerificationMessages.Add(result.ChecksumVerified ? "PE Checksum 已重新计算并复核通过。" : "PE Checksum 复核未通过。");
                if (clearSignature && pe.HasSecurityDirectory) result.VerificationMessages.Add("已清空 Authenticode 安全目录入口。");
                else if (pe.HasSecurityDirectory) result.VerificationMessages.Add("已保留原 Authenticode 证书区；内容变化后厂商签名不再有效。");
                else result.VerificationMessages.Add("源文件没有 Authenticode 安全目录。");
                result.ManifestPath = WriteManifest(result, sorted, clearSignature);
                return result;
            }
            catch (Exception ex)
            {
                error = "写入补丁副本失败: " + ex.Message;
                return null;
            }
        }

        private static string WriteManifest(PatchResult result, List<PatchItem> items, bool clearSignature)
        {
            string manifestPath = result.OutputPath + ".manifest.json";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"tool\": \"Resolve Splash Studio 1.0\",\r\n");
            sb.Append("  \"source\": ").Append(JsonString(result.SourcePath)).Append(",\r\n");
            sb.Append("  \"output\": ").Append(JsonString(result.OutputPath)).Append(",\r\n");
            sb.Append("  \"original_size\": ").Append(result.OriginalSize.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("  \"output_size\": ").Append(result.OutputSize.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("  \"length_preserved\": ").Append(result.LengthPreserved ? "true" : "false").Append(",\r\n");
            sb.Append("  \"original_sha256\": ").Append(JsonString(result.OriginalSha256)).Append(",\r\n");
            sb.Append("  \"output_sha256\": ").Append(JsonString(result.OutputSha256)).Append(",\r\n");
            sb.Append("  \"original_pe_checksum\": ").Append(JsonString("0x" + result.OriginalPeChecksum.ToString("X8"))).Append(",\r\n");
            sb.Append("  \"patched_pe_checksum\": ").Append(JsonString("0x" + result.PatchedPeChecksum.ToString("X8"))).Append(",\r\n");
            sb.Append("  \"pe_checksum_verified\": ").Append(result.ChecksumVerified ? "true" : "false").Append(",\r\n");
            sb.Append("  \"authenticode_directory_found\": ").Append(result.SignatureDirectoryFound ? "true" : "false").Append(",\r\n");
            sb.Append("  \"authenticode_directory_cleared\": ").Append(clearSignature ? "true" : "false").Append(",\r\n");
            sb.Append("  \"replacements\": [\r\n");
            for (int i = 0; i < items.Count; i++)
            {
                PatchItem item = items[i];
                if (i > 0) sb.Append(",\r\n");
                sb.Append("    {\"index\": ").Append(item.Original.Index.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"offset\": ").Append(item.Original.Offset.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"offset_hex\": ").Append(JsonString(item.Original.OffsetHex));
                sb.Append(", \"length\": ").Append(item.Original.Length.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"width\": ").Append(item.Original.Width.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"height\": ").Append(item.Original.Height.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"original_sha256_before\": ").Append(JsonString(item.OriginalSha256));
                sb.Append(", \"replacement_sha256_after\": ").Append(JsonString(item.ReplacementSha256));
                sb.Append(", \"replacement_file\": ").Append(JsonString(item.ReplacementPath));
                sb.Append("}");
            }
            sb.Append("\r\n  ]\r\n}\r\n");
            File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(false));
            return manifestPath;
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
