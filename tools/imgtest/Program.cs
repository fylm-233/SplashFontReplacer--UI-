using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ImgTest
{
    /// <summary>
    /// 复现"选了图却导不进去"的那条链路：
    ///   解码 → 缩放/裁切 → 重新编码 → 等长补齐（PngCodec.FitToExactLength）。
    /// 渲染直接编译 src/MultiReplacer.App/Services/ImagePrep.cs，
    /// 保证这里测的就是产品实际跑的那份代码。
    /// </summary>
    internal static class Program
    {
        private static Task<byte[]> RenderAsync(StorageFile file, int targetWidth, int targetHeight,
                                                bool cover, bool forceOpaque)
        {
            return ImagePrep.RenderAsync(file, targetWidth, targetHeight,
                cover ? FitMode.Cover : FitMode.Stretch, OutputImageFormat.Png, forceOpaque);
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            if (args.Length > 0 && args[0] == "patch")
                return PatchMode(args).GetAwaiter().GetResult();

            string path = args[0];
            int width = args.Length > 1 ? Int32.Parse(args[1], CultureInfo.InvariantCulture) : 0;
            int height = args.Length > 2 ? Int32.Parse(args[2], CultureInfo.InvariantCulture) : 0;
            int targetSize = args.Length > 3 ? Int32.Parse(args[3], CultureInfo.InvariantCulture) : 0;
            bool cover = args.Length > 4 ? args[4] == "cover" : true;

            return Run(path, width, height, targetSize, cover).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 端到端：把选中的图片渲染成目标尺寸 → 补齐到目标长度 → 覆盖到容器副本 → 复核。
        /// 对应界面上"选择新的 png… → 应用替换"的完整链路。
        /// </summary>
        private static async Task<int> PatchMode(string[] args)
        {
            // patch <png> <容器源> <容器输出> <offset> <length> <w> <h>
            string pngPath = args[1];
            string src = Path.GetFullPath(args[2]).Replace('/', '\\');
            string dst = Path.GetFullPath(args[3]).Replace('/', '\\');
            long offset = Int64.Parse(args[4], CultureInfo.InvariantCulture);
            int length = Int32.Parse(args[5], CultureInfo.InvariantCulture);
            int width = Int32.Parse(args[6], CultureInfo.InvariantCulture);
            int height = Int32.Parse(args[7], CultureInfo.InvariantCulture);

            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pngPath).Replace('/', '\\'));
            byte[] png = await RenderAsync(file, width, height, true, false);
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "RENDER={0:N0} 字节", png.Length));

            string error;
            byte[] fitted = PngCodec.FitToExactLength(png, length, out error);
            if (fitted == null)
            {
                Console.WriteLine("FIT_FAILED=" + error);
                return 2;
            }
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "FIT_OK={0:N0} 字节", fitted.Length));

            EmbeddedPatchItem item = new EmbeddedPatchItem();
            EmbeddedImageHit hit = new EmbeddedImageHit();
            hit.Offset = offset;
            hit.Length = length;
            hit.Width = width;
            hit.Height = height;
            hit.Format = SplashImageFormat.Png;
            item.Hit = hit;
            item.Data = fitted;

            string detail;
            if (!EmbeddedImages.PatchInPlace(src, dst, new System.Collections.Generic.List<EmbeddedPatchItem> { item },
                                             out error, out detail))
            {
                Console.WriteLine("PATCH_FAILED=" + error);
                return 3;
            }
            Console.WriteLine("PATCH_OK=" + detail);

            long before = new FileInfo(src).Length;
            long after = new FileInfo(dst).Length;
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "SIZE {0:N0} → {1:N0}  {2}", before, after,
                before == after ? "长度一致" : "长度不一致！"));

            // 复核：重新扫一遍输出文件，看该偏移上还是不是一张 1532×1032 的 PNG
            string scanError;
            var hits = EmbeddedImages.ScanFile(dst, width, height, 80, out scanError);
            bool found = false;
            if (hits != null)
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    if (hits[i].Offset == offset && hits[i].Width == width && hits[i].Height == height) { found = true; break; }
                }
            }
            Console.WriteLine("RESCAN=" + (found ? "同偏移仍是同尺寸 PNG" : "未找到（结构可能被破坏）"));
            return found ? 0 : 4;
        }

        private static async Task<int> Run(string path, int width, int height, int targetSize, bool cover)
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path).Replace('/', '\\'));
            Console.WriteLine("FILE=" + Path.GetFileName(path) + "  " + new FileInfo(path).Length.ToString("N0") + " 字节");

            try
            {
                using (IRandomAccessStream s = await file.OpenAsync(FileAccessMode.Read))
                {
                    BitmapDecoder d = await BitmapDecoder.CreateAsync(s);
                    Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                        "PROBE={0}×{1}  fmt={2}  dpi={3:0.##}×{4:0.##}",
                        d.PixelWidth, d.PixelHeight, d.DecoderInformation.CodecId, d.DpiX, d.DpiY));
                    if (width == 0) { width = (int)d.PixelWidth; height = (int)d.PixelHeight; }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE_FAILED=" + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            byte[] png = await RenderAsync(file, width, height, cover, false);
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                "RENDER_PNG={0:N0} 字节（{1}×{2}）", png.Length, width, height));

            string dump = Environment.GetEnvironmentVariable("IMGTEST_DUMP");
            if (!String.IsNullOrEmpty(dump)) File.WriteAllBytes(dump, png);

            byte[] opaque = await RenderAsync(file, width, height, cover, true);
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "RENDER_OPAQUE={0:N0} 字节", opaque.Length));

            if (targetSize <= 0)
            {
                Console.WriteLine("（未给目标长度，跳过补齐）");
                return 0;
            }

            Console.WriteLine("TARGET=" + targetSize.ToString("N0") + " 字节");
            string error;
            byte[] fit = PngCodec.FitToExactLength(png, targetSize, out error);
            if (fit == null)
            {
                Console.WriteLine("FIT_FAILED=" + error);
                byte[] fit2 = PngCodec.FitToExactLength(opaque, targetSize, out error);
                Console.WriteLine(fit2 == null ? "FIT_OPAQUE_FAILED=" + error
                                               : "FIT_OPAQUE_OK=" + fit2.Length.ToString("N0"));
                return 2;
            }

            Console.WriteLine("FIT_OK=" + fit.Length.ToString("N0"));
            string fitDump = Environment.GetEnvironmentVariable("IMGTEST_FIT");
            if (!String.IsNullOrEmpty(fitDump)) File.WriteAllBytes(fitDump, fit);
            return 0;
        }
    }
}
