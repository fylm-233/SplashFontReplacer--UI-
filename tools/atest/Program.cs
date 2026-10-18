using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MultiReplacer.Core;

namespace ATeset
{
    internal static class Program
    {
        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            string realDir = @"C:\Program Files\Adobe\Adobe After Effects 2026\Support Files";
            string realDll = Path.Combine(realDir, "AfterFXLib.dll");
            string realExe = Path.Combine(realDir, "AfterFX.exe");
            if (!File.Exists(realDll) || !File.Exists(realExe))
            {
                Console.WriteLine("找不到 AE 2026，跳过实测。");
                return;
            }

            string sandbox = Path.Combine(Path.GetTempPath(), "mr_atest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Console.WriteLine("沙箱: " + sandbox);

            string dll = Path.Combine(sandbox, "AfterFXLib.dll");
            string exe = Path.Combine(sandbox, "AfterFX.exe");
            File.Copy(realDll, dll, true);
            File.Copy(realExe, exe, true);

            string originalHash = HashTools.Sha256File(dll);
            long originalLength = new FileInfo(dll).Length;
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "原始 DLL: {0:N0} 字节  sha256={1}", originalLength, originalHash.Substring(0, 16)));

            // ---- 1. 扫描 ----
            string error;
            List<SplashCandidate> all = AdobeSplashService.Discover(exe, false, delegate(string s) { Console.WriteLine("  [log] " + s); }, out error);
            if (all == null)
            {
                Console.WriteLine("Discover 失败: " + error);
                return;
            }

            Console.WriteLine("\n候选列表 (" + all.Count + "):");
            for (int i = 0; i < all.Count; i++)
            {
                SplashCandidate c = all[i];
                Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                    "  [{0}] {1}  stem={2}", i, c.Display, c.Stem));
            }

            SplashCandidate target = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (String.Equals(all[i].NameName, "AE_SPLASH", StringComparison.Ordinal)) { target = all[i]; break; }
            }
            if (target == null)
            {
                Console.WriteLine("没找到 AE_SPLASH，退出。");
                return;
            }

            // ---- 2. 取原图 ----
            byte[] originalPng = AdobeSplashService.Extract(target, out error);
            if (originalPng == null) { Console.WriteLine("Extract 失败: " + error); return; }
            int ow, oh;
            PeResourceFile.TryReadPngSize(originalPng, out ow, out oh);
            Console.WriteLine("\n原图: " + ow + "x" + oh + "  " + originalPng.Length + " 字节");
            File.WriteAllBytes(Path.Combine(sandbox, "original.png"), originalPng);

            // ---- 3. 分组 ----
            List<SplashCandidate> group = AdobeSplashService.ExpandGroup(all, target, true);
            Console.WriteLine("分组命中 " + group.Count + " 项:");
            for (int i = 0; i < group.Count; i++)
            {
                Console.WriteLine("    " + group[i].NameName + "  " + group[i].Width + "x" + group[i].Height);
            }

            // ---- 4. 生成替换图并写入 ----
            SplashApplyResult result = AdobeSplashService.Apply(group, delegate(int w, int h)
            {
                return MakeSolidPng(w, h, 0x1E, 0x6B, 0xC8);
            }, out error);

            if (result == null)
            {
                Console.WriteLine("Apply 失败: " + error);
                return;
            }

            Console.WriteLine("\n写入结果:");
            Console.WriteLine("  文件      : " + result.HostFile);
            Console.WriteLine("  备份      : " + result.BackupFile);
            Console.WriteLine("  替换项    : " + String.Join(", ", result.Replaced.ToArray()));
            Console.WriteLine("  原始大小  : " + result.OriginalSize.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("  写入后大小: " + result.PatchedSize.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("  自检      : " + result.Detail);

            // ---- 5. 复核资源 ----
            PeResourceFile after = PeResourceFile.Load(dll, out error);
            if (after == null) { Console.WriteLine("复核失败: " + error); return; }
            Console.WriteLine("\n写入后的 PNG 资源:");
            for (int i = 0; i < after.Entries.Count; i++)
            {
                PeResourceEntry e = after.Entries[i];
                if (!String.Equals(e.TypeName, "PNG", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] head = ReadHead(dll, e.DataOffset, 33);
                int w, h;
                PeResourceFile.TryReadPngSize(head, out w, out h);
                Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                    "  {0,-26} {1,5}x{2,-5} {3,9:N0} 字节", e.NameName, w, h, e.Size));
            }

            // ---- 6. 还原 ----
            if (!AdobeSplashService.Restore(dll, out error))
            {
                Console.WriteLine("\n还原失败: " + error);
                return;
            }
            string restoredHash = HashTools.Sha256File(dll);
            Console.WriteLine("\n还原后 sha256=" + restoredHash.Substring(0, 16)
                              + "  与原件一致=" + (restoredHash == originalHash));
            Console.WriteLine("还原后大小=" + new FileInfo(dll).Length.ToString("N0", CultureInfo.InvariantCulture));

            try { Directory.Delete(sandbox, true); } catch { }
        }

        private static byte[] ReadHead(string path, long offset, int count)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset + count > fs.Length) return null;
                fs.Position = offset;
                byte[] b = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = fs.Read(b, read, count - read);
                    if (n <= 0) return null;
                    read += n;
                }
                return b;
            }
        }

        /// <summary>生成纯色 RGBA PNG，用于验证任意尺寸写回。</summary>
        private static byte[] MakeSolidPng(int width, int height, byte r, byte g, byte b)
        {
            byte[] raw = new byte[height * (1 + width * 4)];
            int p = 0;
            for (int y = 0; y < height; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < width; x++)
                {
                    raw[p++] = r;
                    raw[p++] = g;
                    raw[p++] = b;
                    raw[p++] = 255;
                }
            }

            byte[] compressed;
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZLibStream z = new ZLibStream(ms, CompressionLevel.Optimal, true))
                {
                    z.Write(raw, 0, raw.Length);
                }
                compressed = ms.ToArray();
            }

            List<byte> png = new List<byte>();
            png.AddRange(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // RGBA
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            png.AddRange(PngCodec.BuildChunk("IHDR", ihdr));
            png.AddRange(PngCodec.BuildChunk("IDAT", compressed));
            png.AddRange(PngCodec.BuildChunk("IEND", new byte[0]));
            return png.ToArray();
        }

        private static void WriteBE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
