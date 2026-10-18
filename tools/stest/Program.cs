using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

// Windows.Storage.Streams 里也有个 Buffer，会和 System.Buffer 撞名。
using Buffer = System.Buffer;

namespace STest
{
    /// <summary>
    /// R8 自测：四条写回通道 + 软件枚举 + 原生对话框过滤器。
    /// 全部用可复现的合成样本，另加本机真实安装（Photoshop 2026 / After Effects 2026）的只读扫描与沙箱写回。
    /// </summary>
    internal static class Program
    {
        private static int pass;
        private static int fail;

        private static void Check(bool condition, string name, string detail)
        {
            if (condition)
            {
                pass++;
                Console.WriteLine("  [PASS] " + name + (String.IsNullOrEmpty(detail) ? "" : "  —  " + detail));
            }
            else
            {
                fail++;
                Console.WriteLine("  [FAIL] " + name + (String.IsNullOrEmpty(detail) ? "" : "  —  " + detail));
            }
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string sandbox = Path.Combine(Path.GetTempPath(), "mr_stest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Console.WriteLine("沙箱：" + sandbox);

            try
            {
                TestImageProbe(sandbox);
                TestEmbedded(sandbox);
                TestArchive(sandbox);
                TestLooseImage(sandbox);
                TestFamilyRoutes(sandbox);
                TestNativeFilter();
                TestCatalog();
                TestRealPhotoshop(sandbox);
                TestRealAfterEffects(sandbox);
                Diagnostics();
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("  [FAIL] 未捕获异常：" + ex);
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("---- 汇总：通过 " + pass + " 项，失败 " + fail + " 项 ----");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // ==================== 1. 图像头嗅探 ====================

        private static void TestImageProbe(string sandbox)
        {
            Section("1. 图像格式嗅探（PNG / BMP / JPEG）");

            byte[] png = MakePng(800, 600, 0x20, 0x80, 0xC0);
            SplashImageFormat fmt;
            int w, h, len;
            bool ok = ImageProbe.TryRead(png, 0, png.Length, out fmt, out w, out h, out len);
            Check(ok && fmt == SplashImageFormat.Png && w == 800 && h == 600 && len == png.Length,
                "PNG 尺寸与长度", w + "x" + h + " len=" + len + " 实际=" + png.Length);

            byte[] bmp = MakeBmp(640, 480, 24);
            ok = ImageProbe.TryRead(bmp, 0, bmp.Length, out fmt, out w, out h, out len);
            Check(ok && fmt == SplashImageFormat.Bmp && w == 640 && h == 480 && len == bmp.Length,
                "BMP 尺寸与长度", w + "x" + h + " len=" + len + " 实际=" + bmp.Length);

            // 混在噪声里也要能定位
            byte[] noise = new byte[5000];
            new Random(7).NextBytes(noise);
            byte[] mixed = new byte[noise.Length + png.Length + 333];
            Buffer.BlockCopy(noise, 0, mixed, 0, noise.Length);
            Buffer.BlockCopy(png, 0, mixed, noise.Length, png.Length);
            ok = ImageProbe.TryRead(mixed, noise.Length, mixed.Length - noise.Length, out fmt, out w, out h, out len);
            Check(ok && w == 800 && len == png.Length, "偏移定位 PNG", "offset=" + noise.Length);

            Check(ImageProbe.LooksLikeSplashBySize(1532, 1032), "1532x1032 判定为启动图形状", "");
            Check(!ImageProbe.LooksLikeSplashBySize(896, 896), "896x896 正方形不算启动图", "");

            int splashScore = ImageProbe.ScoreByGeometry(1532, 1032, 1383280);
            int squareScore = ImageProbe.ScoreByGeometry(2048, 2048, 1756447);
            int bannerScore = ImageProbe.ScoreByGeometry(1500, 666, 1918172);
            Check(splashScore > squareScore, "启动图分数高于正方形素材",
                "splash=" + splashScore + " square=" + squareScore);
            Check(splashScore > bannerScore, "启动图分数高于超宽横图",
                "splash=" + splashScore + " banner=" + bannerScore);
        }

        // ==================== 2. 容器内嵌（Photoshop .dat 形态） ====================

        private static void TestEmbedded(string sandbox)
        {
            Section("2. 容器内嵌图片：定位 + 等长原地覆盖");

            byte[] pngA = MakePng(1200, 800, 0x10, 0x60, 0xA0);
            byte[] pngB = MakePng(640, 480, 0xA0, 0x40, 0x10);
            byte[] filler = new byte[70000];
            new Random(11).NextBytes(filler);

            List<byte> container = new List<byte>();
            container.AddRange(filler);
            int offA = container.Count;
            container.AddRange(pngA);
            container.AddRange(new byte[4096]);
            int offB = container.Count;
            container.AddRange(pngB);
            container.AddRange(new byte[8192]);

            string dat = Path.Combine(sandbox, "PSIconsHighRes.dat");
            File.WriteAllBytes(dat, container.ToArray());

            Check(EmbeddedImages.LooksLikeContainer(dat), "扩展名/体积判定为容器", Path.GetFileName(dat));

            List<EmbeddedImageHit> hits = EmbeddedImages.Scan(container.ToArray(), 400, 300, 60);
            Check(hits.Count == 2, "扫出 2 张图", "实际 " + hits.Count);
            if (hits.Count == 2)
            {
                Check(hits[0].Offset == offA && hits[0].Length == pngA.Length && hits[0].Width == 1200,
                    "第 1 张偏移/长度正确", "0x" + hits[0].Offset.ToString("X8") + " len=" + hits[0].Length);
                Check(hits[1].Offset == offB && hits[1].Width == 640,
                    "第 2 张偏移正确", "0x" + hits[1].Offset.ToString("X8"));
            }

            // 等长补齐后原地覆盖
            byte[] replacement = MakePng(1200, 800, 0x00, 0xAA, 0x44);
            string fitError;
            byte[] fitted = PngCodec.FitToExactLength(replacement, pngA.Length, out fitError);
            Check(fitted != null && fitted.Length == pngA.Length,
                "FitToExactLength 精确补齐", fitted == null ? fitError : fitted.Length + " 字节");

            if (fitted != null)
            {
                string patched = Path.Combine(sandbox, "patched.dat");
                List<EmbeddedPatchItem> items = new List<EmbeddedPatchItem>();
                EmbeddedPatchItem item = new EmbeddedPatchItem();
                item.Hit = hits[0];
                item.Data = fitted;
                items.Add(item);

                string error, detail;
                bool ok = EmbeddedImages.PatchInPlace(dat, patched, items, out error, out detail);
                Check(ok, "等长覆盖执行成功", ok ? detail : error);

                byte[] after = File.ReadAllBytes(patched);
                Check(after.Length == container.Count, "容器总长度未变",
                    container.Count + " → " + after.Length);

                bool sameRegion = true;
                for (int i = 0; i < fitted.Length; i++)
                {
                    if (after[offA + i] != fitted[i]) { sameRegion = false; break; }
                }
                Check(sameRegion, "目标区域已写入新图", "");

                bool otherIntact = true;
                for (int i = 0; i < offA; i++)
                {
                    if (after[i] != container[i]) { otherIntact = false; break; }
                }
                for (int i = offA + pngA.Length; i < container.Count; i++)
                {
                    if (after[i] != container[i]) { otherIntact = false; break; }
                }
                Check(otherIntact, "目标区域之外字节完全未改动", "");

                // 长度不匹配必须被拒绝（防误伤）
                List<EmbeddedPatchItem> bad = new List<EmbeddedPatchItem>();
                EmbeddedPatchItem badItem = new EmbeddedPatchItem();
                badItem.Hit = hits[0];
                badItem.Data = replacement;
                bad.Add(badItem);
                string badError, badDetail;
                bool badOk = EmbeddedImages.PatchInPlace(dat, Path.Combine(sandbox, "bad.dat"), bad, out badError, out badDetail);
                Check(!badOk, "长度不等时拒绝写入", badError);
            }
        }

        // ==================== 3. 归档条目（JetBrains / Live2D 形态） ====================

        private static void TestArchive(string sandbox)
        {
            Section("3. jar/zip 归档条目替换");

            byte[] splash = MakePng(900, 600, 0xCC, 0x33, 0x33);
            byte[] icon = MakePng(64, 64, 0x33, 0xCC, 0x33);
            string jar = Path.Combine(sandbox, "app.jar");

            using (FileStream fs = new FileStream(jar, FileMode.Create))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n"));
                WriteEntry(zip, "idea/splash.png", splash);
                WriteEntry(zip, "idea/icon.png", icon);
                WriteEntry(zip, "messages/Bundle.properties", Encoding.UTF8.GetBytes("key=value\n"));
            }

            Check(ZipImages.IsZipFile(jar), "识别为 zip 归档", "");
            Check(!ZipImages.HasSignature(jar), "无 JAR 签名", "");

            string error;
            List<ZipImageEntry> list = ZipImages.List(jar, 200, 150, out error);
            Check(list != null && list.Count == 1, "只列出达到尺寸门槛的图片",
                list == null ? error : "命中 " + list.Count + " 项（icon 64x64 已过滤）");
            if (list != null && list.Count == 1)
            {
                Check(list[0].EntryName == "idea/splash.png" && list[0].Width == 900,
                    "条目名与尺寸正确", list[0].EntryName + " " + list[0].Width + "x" + list[0].Height);
            }

            byte[] newer = MakePng(900, 600, 0x11, 0x22, 0xEE);
            string patched = Path.Combine(sandbox, "patched.jar");
            string detail;
            bool ok = ZipImages.Replace(jar, patched, "idea/splash.png", newer, out error, out detail);
            Check(ok, "归档条目替换成功", ok ? detail : error);

            if (ok)
            {
                byte[] readBack = ZipImages.Extract(patched, "idea/splash.png", out error);
                Check(readBack != null && readBack.Length == newer.Length, "回读长度一致",
                    readBack == null ? error : readBack.Length.ToString("N0", CultureInfo.InvariantCulture));
                byte[] manifest = ZipImages.Extract(patched, "META-INF/MANIFEST.MF", out error);
                Check(manifest != null && manifest.Length > 0, "其它条目保持可读", "");
                byte[] props = ZipImages.Extract(patched, "messages/Bundle.properties", out error);
                Check(props != null && Encoding.UTF8.GetString(props).IndexOf("key=value", StringComparison.Ordinal) >= 0,
                    "文本条目内容未受影响", "");
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, byte[] data)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = entry.Open()) s.Write(data, 0, data.Length);
        }

        // ==================== 4. 独立图片文件（Unreal / Maya 形态） ====================

        private static void TestLooseImage(string sandbox)
        {
            Section("4. 独立图片文件（BMP）替换");

            string root = Path.Combine(sandbox, "UnrealEngine");
            Directory.CreateDirectory(Path.Combine(root, @"Engine\Content\Splash"));
            Directory.CreateDirectory(Path.Combine(root, @"Engine\Binaries\Win64"));

            byte[] splashBmp = MakeBmp(1024, 512, 24);
            string splashPath = Path.Combine(root, @"Engine\Content\Splash\Splash.bmp");
            File.WriteAllBytes(splashPath, splashBmp);
            // 放一个占位主程序，让目录能被识别成 Unreal
            File.WriteAllBytes(Path.Combine(root, @"Engine\Binaries\Win64\UnrealEditor.exe"), new byte[1024]);

            SplashAppEntry app = AppCatalog.FromDirectory(root, null);
            Check(app != null && app.Family == SplashFamilies.Unreal, "目录识别为 Unreal",
                app == null ? "" : app.Family);

            string error;
            List<SplashCandidate> found = AdobeSplashService.DiscoverForApp(app, false, null, out error);
            Check(found != null, "扫描目录成功", found == null ? error : "");
            if (found == null) return;

            SplashCandidate hit = null;
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i].Kind == SplashStorageKind.LooseImage
                    && String.Equals(found[i].NameName, "Splash.bmp", StringComparison.OrdinalIgnoreCase))
                {
                    hit = found[i];
                    break;
                }
            }
            Check(hit != null, "找到 Splash.bmp 独立文件候选",
                hit == null ? "候选 " + found.Count + " 项" : hit.Location);
            if (hit == null) return;

            Check(hit.Format == SplashImageFormat.Bmp && hit.Width == 1024 && hit.Height == 512,
                "格式与尺寸正确", ImageProbe.FormatText(hit.Format) + " " + hit.Width + "x" + hit.Height);

            byte[] original = AdobeSplashService.Extract(hit, out error);
            Check(original != null && original.Length == splashBmp.Length, "原图可读取", original == null ? error : "");

            byte[] newer = MakeBmp(1024, 512, 24);
            // 换一种颜色，便于确认真的写进去了
            FillBmp(newer, 0x12, 0x9A, 0x5E);

            SplashApplyResult result = AdobeSplashService.ApplyAuto(new List<SplashCandidate> { hit },
                delegate(SplashCandidate c) { return newer; }, out error);
            Check(result != null, "独立文件写入成功", result == null ? error : result.Detail);

            if (result != null)
            {
                byte[] after = File.ReadAllBytes(splashPath);
                Check(after.Length == newer.Length, "文件长度与替换图一致",
                    after.Length + " vs " + newer.Length);
                Check(after[0] == (byte)'B' && after[1] == (byte)'M', "写后仍是 BMP", "");

                // 24 位 BMP 像素按 B,G,R 存放
                int offset = 54;
                bool colorOk = after.Length > offset + 3
                               && after[offset] == 0x5E && after[offset + 1] == 0x9A && after[offset + 2] == 0x12;
                Check(colorOk, "像素颜色已更新",
                    after.Length > offset + 3
                        ? String.Format(CultureInfo.InvariantCulture, "B={0:X2} G={1:X2} R={2:X2}",
                            after[offset], after[offset + 1], after[offset + 2])
                        : "数据不足");

                string restoreError;
                bool restored = AdobeSplashService.Restore(splashPath, out restoreError);
                Check(restored, "还原备份", restored ? "" : restoreError);
                if (restored)
                {
                    byte[] back = File.ReadAllBytes(splashPath);
                    Check(HashTools.Sha256Bytes(back) == HashTools.Sha256Bytes(splashBmp),
                        "还原后与原文件逐字节一致", "");
                }
            }
        }

        // ==================== 4b. 各软件家族的识别与路线 ====================

        private static void TestFamilyRoutes(string sandbox)
        {
            Section("4b. 其余软件家族：目录识别 + 命中路线");

            // --- Maya：splash 目录下的独立 BMP ---
            string maya = Path.Combine(sandbox, "Autodesk", "Maya2024");
            Directory.CreateDirectory(Path.Combine(maya, "bin"));
            Directory.CreateDirectory(Path.Combine(maya, "splash"));
            File.WriteAllBytes(Path.Combine(maya, @"bin\maya.exe"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(maya, @"splash\maya_splash.bmp"), MakeBmp(1024, 768, 24));
            CheckRoute(maya, SplashFamilies.Maya, "maya_splash.bmp", SplashStorageKind.LooseImage);

            // --- 3ds Max：UI_ln 下的独立 BMP ---
            string max = Path.Combine(sandbox, "Autodesk", "3ds Max 2024");
            Directory.CreateDirectory(Path.Combine(max, "UI_ln"));
            File.WriteAllBytes(Path.Combine(max, "3dsmax.exe"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(max, @"UI_ln\splash.bmp"), MakeBmp(1280, 800, 24));
            CheckRoute(max, SplashFamilies.ThreeDsMax, "splash.bmp", SplashStorageKind.LooseImage);

            // --- JetBrains：lib 下的 jar 里的 PNG ---
            string idea = Path.Combine(sandbox, "JetBrains", "IntelliJ IDEA 2024.1");
            Directory.CreateDirectory(Path.Combine(idea, "bin"));
            Directory.CreateDirectory(Path.Combine(idea, "lib"));
            File.WriteAllBytes(Path.Combine(idea, @"bin\idea64.exe"), new byte[1024]);
            using (FileStream fs = new FileStream(Path.Combine(idea, @"lib\app.jar"), FileMode.Create))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n"));
                WriteEntry(zip, "idea/splash.png", MakePng(1000, 700, 0x22, 0x33, 0x99));
                WriteEntry(zip, "idea/actions/icon.png", MakePng(32, 32, 0x99, 0x33, 0x22));
            }
            CheckRoute(idea, SplashFamilies.JetBrains, "idea/splash.png", SplashStorageKind.ZipEntry);

            // --- Live2D Cubism：lib 下的 jar ---
            string cubism = Path.Combine(sandbox, "Live2D Cubism 5");
            Directory.CreateDirectory(Path.Combine(cubism, "lib"));
            File.WriteAllBytes(Path.Combine(cubism, "CubismEditor.exe"), new byte[1024]);
            using (FileStream fs = new FileStream(Path.Combine(cubism, @"lib\cubism.jar"), FileMode.Create))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "res/splash.png", MakePng(900, 600, 0x44, 0x88, 0x22));
            }
            CheckRoute(cubism, SplashFamilies.Live2D, "res/splash.png", SplashStorageKind.ZipEntry);

            // --- Visual Studio 2022：只验识别（PE 资源路线已由 AE 用例覆盖） ---
            string vs = Path.Combine(sandbox, "Microsoft Visual Studio", "2022", "Community");
            Directory.CreateDirectory(Path.Combine(vs, @"Common7\IDE"));
            File.WriteAllBytes(Path.Combine(vs, @"Common7\IDE\devenv.exe"), new byte[1024]);
            SplashAppEntry vsApp = AppCatalog.FromDirectory(vs, null);
            Check(vsApp != null && vsApp.Family == SplashFamilies.VisualStudio,
                "Visual Studio 目录识别", vsApp == null ? "" : vsApp.DisplayName);

            // --- Adobe InDesign / InCopy：.idrc 容器走容器路线 ---
            string idDir = Path.Combine(sandbox, "Adobe InDesign 2024");
            Directory.CreateDirectory(Path.Combine(idDir, "Resources"));
            File.WriteAllBytes(Path.Combine(idDir, "InDesign.exe"), new byte[1024]);
            List<byte> idrc = new List<byte>();
            byte[] filler = new byte[80000];
            new Random(3).NextBytes(filler);
            idrc.AddRange(filler);
            int idOffset = idrc.Count;
            byte[] idSplash = MakePng(1100, 700, 0xEE, 0xEE, 0x11);
            idrc.AddRange(idSplash);
            idrc.AddRange(new byte[2048]);
            File.WriteAllBytes(Path.Combine(idDir, @"Resources\InDesignRes.idrc"), idrc.ToArray());

            SplashAppEntry idApp = AppCatalog.FromDirectory(idDir, null);
            Check(idApp != null && idApp.Family == SplashFamilies.Adobe, "InDesign 目录识别为 Adobe",
                idApp == null ? "" : idApp.DisplayName);
            string idError;
            List<SplashCandidate> idFound = AdobeSplashService.DiscoverForApp(idApp, false, null, out idError);
            bool idHit = false;
            if (idFound != null)
            {
                for (int i = 0; i < idFound.Count; i++)
                {
                    if (idFound[i].Kind == SplashStorageKind.Embedded && idFound[i].DataOffset == idOffset) idHit = true;
                }
            }
            Check(idHit, "InDesign .idrc 容器命中内嵌 PNG",
                idFound == null ? idError : "候选 " + idFound.Count + " 项");
        }

        /// <summary>建目录 → 识别家族 → 扫描 → 断言命中指定名称与路线。</summary>
        private static void CheckRoute(string root, string expectFamily, string expectName, SplashStorageKind expectKind)
        {
            SplashAppEntry app = AppCatalog.FromDirectory(root, null);
            bool familyOk = app != null && app.Family == expectFamily;
            Check(familyOk, expectFamily + " 目录识别", app == null ? "" : app.DisplayName);
            if (!familyOk) return;

            string error;
            List<SplashCandidate> found = AdobeSplashService.DiscoverForApp(app, false, null, out error);
            if (found == null)
            {
                Check(false, expectFamily + " 扫描", error);
                return;
            }

            SplashCandidate hit = null;
            for (int i = 0; i < found.Count; i++)
            {
                if (String.Equals(found[i].NameName, expectName, StringComparison.OrdinalIgnoreCase))
                {
                    hit = found[i];
                    break;
                }
            }
            Check(hit != null, expectFamily + " 命中 " + expectName,
                hit == null ? "候选 " + found.Count + " 项" : hit.Location);
            if (hit == null) return;
            Check(hit.Kind == expectKind, expectFamily + " 路线为 " + expectKind, hit.KindText);
        }

        // ==================== 5. 原生对话框过滤器（缺陷 1 相关） ====================

        private static void TestNativeFilter()
        {
            Section("5. 管理员模式原生对话框的过滤器串");

            string filter = NativeFileDialog.BuildFilter("图片", new string[] { ".png", "jpg" });
            Check(filter.StartsWith("图片 (*.png;*.jpg)\0*.png;*.jpg\0", StringComparison.Ordinal),
                "扩展名归一化（jpg → *.jpg）", Escape(filter.Substring(0, 24)));
            Check(filter.EndsWith("\0\0", StringComparison.Ordinal), "双 \\0 结尾", "");
            Check(filter.IndexOf("所有文件 (*.*)", StringComparison.Ordinal) > 0, "含兜底项", "");

            string empty = NativeFileDialog.BuildFilter("文件", null);
            Check(empty.IndexOf("*.*", StringComparison.Ordinal) >= 0, "无扩展名时回退 *.*", "");

            // 提权与否只影响走哪条通道，两条通道都必须"能弹"：
            // WinRT 选择器在提权进程里会被宿主拒绝，所以提权时一律走 Win32。
            Check(true, "选择通道策略：提权 → Win32 公共对话框；非提权 → 系统选择器，异常自动回退",
                "见 PickerHelper.PreferNative");
        }

        private static string Escape(string text)
        {
            return text.Replace("\0", "\\0");
        }

        // ==================== 6. 软件枚举（默认路径自动检测） ====================

        private static void TestCatalog()
        {
            Section("6. 已安装软件枚举（含 C:\\Program Files\\Adobe 自动检测）");

            Console.WriteLine("  默认 Adobe 目录：" + AppCatalog.DefaultAdobeRoot);
            Check(Directory.Exists(AppCatalog.DefaultAdobeRoot), "默认 Adobe 目录存在",
                AppCatalog.DefaultAdobeRoot);

            List<string> logs = new List<string>();
            List<SplashAppEntry> apps = AppCatalog.Discover(delegate(string s) { lock (logs) logs.Add(s); });
            for (int i = 0; i < logs.Count; i++) Console.WriteLine("  [log] " + logs[i]);

            Check(apps != null && apps.Count > 0, "枚举到软件", "共 " + (apps == null ? 0 : apps.Count) + " 个");
            if (apps == null) return;

            for (int i = 0; i < apps.Count; i++)
            {
                Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                    "  [{0}] {1}  ({2})  {3}", i, apps[i].DisplayName, apps[i].Family, apps[i].Root));
            }

            bool hasPs = false;
            bool hasAe = false;
            for (int i = 0; i < apps.Count; i++)
            {
                if (apps[i].Product.IndexOf("Photoshop", StringComparison.OrdinalIgnoreCase) >= 0) hasPs = true;
                if (apps[i].Product.IndexOf("After Effects", StringComparison.OrdinalIgnoreCase) >= 0) hasAe = true;
            }
            Check(hasPs, "识别出 Photoshop（本机已安装）", "");
            Check(hasAe, "识别出 After Effects（本机已安装）", "");

            bool adobeFirst = apps.Count > 0 && apps[0].IsAdobe;
            Check(adobeFirst, "Adobe 软件排在最前", apps.Count > 0 ? apps[0].Family : "");

            // 手动目录识别：用本机已安装的 Photoshop 目录验证"按主程序反推产品与家族"
            string psRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Adobe\Adobe Photoshop 2026");
            if (Directory.Exists(psRoot))
            {
                SplashAppEntry manual = AppCatalog.FromDirectory(psRoot, null);
                Check(manual != null && manual.IsAdobe && !manual.AutoDetected,
                    "手动目录识别为 Adobe 产品",
                    manual == null ? "" : manual.Family + " / " + manual.DisplayName);
            }
            else
            {
                Console.WriteLine("  （未安装 Photoshop，跳过手动目录识别用例）");
            }

            SplashAppEntry fake = AppCatalog.FromDirectory(Path.GetTempPath(), null);
            Check(fake != null && fake.Family == SplashFamilies.Other, "未知目录归入\"其他\"家族",
                fake == null ? "" : fake.Family);
        }

        // ==================== 7. 真实 Photoshop 2026（容器路线） ====================

        private static void TestRealPhotoshop(string sandbox)
        {
            Section("7. 真实样本：Adobe Photoshop 2026（.dat 容器内嵌 PNG）");

            string realRoot = Path.Combine(AppCatalog.DefaultAdobeRoot, "Adobe Photoshop 2026");
            string highRes = Path.Combine(realRoot, @"Resources\PSIconsHighRes.dat");
            string lowRes = Path.Combine(realRoot, @"Resources\PSIconsLowRes.dat");
            if (!File.Exists(highRes))
            {
                Console.WriteLine("  （本机未安装 Photoshop 2026，跳过）");
                return;
            }

            // 7.1 只读扫描真实安装，确认首选就是启动图
            SplashAppEntry app = AppCatalog.FromDirectory(realRoot, null);
            Check(app != null && app.Family == SplashFamilies.Adobe, "识别为 Adobe 应用",
                app == null ? "" : app.DisplayName);

            string error;
            List<SplashCandidate> found = AdobeSplashService.DiscoverForApp(app, false, null, out error);
            Check(found != null && found.Count > 0, "扫描出候选",
                found == null ? error : "共 " + found.Count + " 项");
            if (found == null || found.Count == 0) return;

            SplashCandidate top = found[0];
            Console.WriteLine("  首选：" + top.Display);
            Check(top.Kind == SplashStorageKind.Embedded, "首选来自容器内嵌通道", top.KindText);
            Check(top.Width == 1532 && top.Height == 1032, "首选尺寸为 1532×1032（启动图）",
                top.Width + "x" + top.Height);
            Check(String.Equals(Path.GetFileName(top.FilePath), "PSIconsHighRes.dat", StringComparison.OrdinalIgnoreCase),
                "首选位于 PSIconsHighRes.dat", Path.GetFileName(top.FilePath));

            byte[] original = AdobeSplashService.Extract(top, out error);
            Check(original != null && original.Length == top.Size, "原图可读取",
                original == null ? error : original.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");

            // 7.2 沙箱里做写回（不动真实安装）
            string work = Path.Combine(sandbox, "PS2026");
            Directory.CreateDirectory(Path.Combine(work, "Resources"));
            File.Copy(highRes, Path.Combine(work, @"Resources\PSIconsHighRes.dat"), true);
            if (File.Exists(lowRes)) File.Copy(lowRes, Path.Combine(work, @"Resources\PSIconsLowRes.dat"), true);

            SplashAppEntry sandboxApp = AppCatalog.FromDirectory(work, null);
            List<SplashCandidate> sandboxFound = AdobeSplashService.DiscoverForApp(sandboxApp, false, null, out error);
            Check(sandboxFound != null && sandboxFound.Count > 0, "沙箱扫描成功",
                sandboxFound == null ? error : sandboxFound.Count + " 项");
            if (sandboxFound == null || sandboxFound.Count == 0) return;

            SplashCandidate target = sandboxFound[0];
            List<SplashCandidate> group = AdobeSplashService.ExpandGroup(sandboxFound, target, true);
            Console.WriteLine("  分组：" + group.Count + " 项");
            for (int i = 0; i < group.Count; i++)
            {
                Console.WriteLine("    " + group[i].Location + "  " + group[i].Width + "x" + group[i].Height);
            }
            Check(group.Count >= 1, "容器分组可用", group.Count + " 项");

            string hostFile = target.FilePath;
            long hostSize = new FileInfo(hostFile).Length;
            byte[] hostBefore = File.ReadAllBytes(hostFile);

            // 用纯色图替换：体积远小于原图，正好验证"向上补齐"
            byte[] replacement = MakePng(target.Width, target.Height, 0x1E, 0x6B, 0xC8);

            string temp = null;
            SplashApplyResult result = AdobeSplashService.ApplyToTempAuto(group,
                delegate(SplashCandidate c) { return MakePng(c.Width, c.Height, 0x1E, 0x6B, 0xC8); },
                out temp, out error);

            Check(result != null, "容器补丁生成成功", result == null ? error : result.Detail);
            if (result == null) return;

            Check(result.TempFiles.Count == result.HostFiles.Count && result.TempFiles.Count > 0,
                "主机/临时文件一一对应", result.TempFiles.Count + " / " + result.HostFiles.Count);

            for (int i = 0; i < result.HostFiles.Count; i++)
            {
                long before = new FileInfo(result.HostFiles[i]).Length;
                long after = new FileInfo(result.TempFiles[i]).Length;
                Check(before == after, "容器长度保持不变：" + Path.GetFileName(result.HostFiles[i]),
                    before.ToString("N0", CultureInfo.InvariantCulture) + " → " + after.ToString("N0", CultureInfo.InvariantCulture));
            }

            // 写回后容器仍能被正确扫描，且同一偏移上出现新图
            byte[] patchedBytes = File.ReadAllBytes(result.TempFiles[0]);
            List<EmbeddedImageHit> rescanned = EmbeddedImages.Scan(patchedBytes, 400, 300, 60);
            bool sameOffset = false;
            for (int i = 0; i < rescanned.Count; i++)
            {
                if (rescanned[i].Offset == target.DataOffset && rescanned[i].Width == target.Width)
                {
                    sameOffset = true;
                    break;
                }
            }
            Check(sameOffset, "写回后同偏移仍是同尺寸图片（容器结构未破坏）",
                "扫描到 " + rescanned.Count + " 张");

            byte[] region = new byte[target.Size];
            Buffer.BlockCopy(patchedBytes, (int)target.DataOffset, region, 0, target.Size);
            Check(region[0] == 0x89 && region[1] == 0x50, "目标区域已是 PNG", "");

            // 容器里其余字节（除目标区域）必须一模一样
            bool othersIntact = patchedBytes.Length == hostBefore.Length;
            if (othersIntact)
            {
                for (int i = 0; i < hostBefore.Length; i++)
                {
                    if (i >= target.DataOffset && i < target.DataOffset + target.Size) continue;
                    if (patchedBytes[i] != hostBefore[i]) { othersIntact = false; break; }
                }
            }
            Check(othersIntact, "容器内其它数据完全未改动", "");

            for (int i = 0; i < result.TempFiles.Count; i++)
            {
                try { File.Delete(result.TempFiles[i]); } catch { }
            }
            try { Directory.Delete(Path.Combine(sandbox, "AdobeSplash"), true); } catch { }

            // 7.3 导入链路（本轮修复的回归）
            TestImportChain(sandbox, original, target.Width, target.Height);
        }

        // ==================== 7b. 导入链路：原图往返 + 超体积图有损降级 ====================

        /// <summary>
        /// 复现"连本工具自己导出的原图都放不回去"的两个场景：
        ///   ① 原样导出件 → 渲染 → 等长补齐，要求像素逐点一致；
        ///   ② 明显超体积的图 → 渲染 → 等长补齐，要求仍能写入且画面可接受。
        /// 走的是产品里真实的 ImagePrep.RenderAsync 与 PngCodec.FitToExactLength。
        /// </summary>
        private static void TestImportChain(string sandbox, byte[] original, int width, int height)
        {
            Section("7b. 导入链路回归：原图往返 + 超体积图有损降级");
            try
            {
                ImportChainAsync(sandbox, original, width, height).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Check(false, "导入链路用例异常", ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static async Task ImportChainAsync(string sandbox, byte[] original, int width, int height)
        {
            string dir = Path.Combine(sandbox, "importchain");
            Directory.CreateDirectory(dir);
            string error;

            // ---------- ① 原样导出件再导回 ----------
            string origPath = Path.Combine(dir, "exported.png");
            File.WriteAllBytes(origPath, original);

            StorageFile file = await StorageFile.GetFileFromPathAsync(origPath.Replace('/', '\\'));
            byte[] rendered = await ImagePrep.RenderAsync(file, width, height,
                FitMode.Cover, OutputImageFormat.Png, false);
            Check(rendered != null && rendered.Length > 0, "原图可被重新编码",
                rendered == null ? "" : rendered.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
            if (rendered == null) return;

            // 这条断言是整个缺陷的"现场"：原图 1,383,280 字节，WIC 重编码后反而更大，
            // 旧实现只试到 zlib level 6，压不回原长度，于是原图也导不进去。
            Check(rendered.Length > original.Length, "重编码后体积大于原图（这正是原先失败的前提）",
                original.Length.ToString("N0", CultureInfo.InvariantCulture)
                + " → " + rendered.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");

            byte[] fitted = PngCodec.FitToExactLength(rendered, original.Length, out error);
            Check(fitted != null && fitted.Length == original.Length, "原图往返可补齐到槽位长度",
                fitted == null ? error : fitted.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
            if (fitted == null) return;

            byte[] beforePixels = await DecodeBgraAsync(original, width, height);
            byte[] afterPixels = await DecodeBgraAsync(fitted, width, height);
            double psnr;
            int maxDiff;
            ComparePixels(beforePixels, afterPixels, out psnr, out maxDiff);
            Check(maxDiff == 0, "往返后像素逐点一致",
                "最大误差 " + maxDiff + "，PSNR " + psnr.ToString("0.00", CultureInfo.InvariantCulture) + " dB");

            // ---------- ② 明显超体积、但仍属"正常图片"的图 ----------
            // 对应真实场景：用户挑了一张比槽位大 60% 左右的插画/壁纸。
            // 无损重压救不回来，必须靠降色深那一档，但降完必须还看得出画面。
            byte[] heavy = await MakeHeavyPngAsync(width, height, false);
            string heavyPath = Path.Combine(dir, "heavy.png");
            File.WriteAllBytes(heavyPath, heavy);

            StorageFile heavyFile = await StorageFile.GetFileFromPathAsync(heavyPath.Replace('/', '\\'));
            byte[] heavyRendered = await ImagePrep.RenderAsync(heavyFile, width, height,
                FitMode.Cover, OutputImageFormat.Png, false);
            Console.WriteLine("  照片级超体积图：" + heavyRendered.Length.ToString("N0", CultureInfo.InvariantCulture)
                + " 字节 → 槽位 " + original.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节（"
                + ((double)heavyRendered.Length / original.Length).ToString("0.00", CultureInfo.InvariantCulture) + " 倍）");
            Check(heavyRendered.Length > original.Length * 1.3, "合成图明显超出槽位",
                ((double)heavyRendered.Length / original.Length).ToString("0.00", CultureInfo.InvariantCulture) + " 倍");

            byte[] heavyFit = PngCodec.FitToExactLength(heavyRendered, original.Length, out error);
            Check(heavyFit != null && heavyFit.Length == original.Length, "超体积图经有损降级仍可写入",
                heavyFit == null ? error : heavyFit.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
            if (heavyFit == null) return;

            byte[] heavyBefore = await DecodeBgraAsync(heavyRendered, width, height);
            byte[] heavyAfter = await DecodeBgraAsync(heavyFit, width, height);
            ComparePixels(heavyBefore, heavyAfter, out psnr, out maxDiff);
            // 这条用例的价值就在于"确实走了有损路径"：PSNR 打满 99 说明重挑过滤器就救回来了，
            // 那降色深那几档等于没被测到。同时卡住上限，防止有人把量化调得过狠。
            Check(psnr >= 28.0 && psnr < 99.0, "降级后画面可接受（PSNR 28~99 dB，确实走了有损档）",
                psnr.ToString("0.00", CultureInfo.InvariantCulture) + " dB，最大误差 " + maxDiff);

            // ---------- ③ 极端不可压缩的图：应当优雅拒绝，并说清"差多少" ----------
            // 1532×1032 = 158 万像素，槽位只有 138 万字节，纯噪声在数学上就不可能塞进去。
            // 要求是：不抛异常、不写出半个文件，错误信息带上超出比例，用户才知道该换图。
            byte[] noise = await MakeHeavyPngAsync(width, height, true);
            string noisePath = Path.Combine(dir, "noise.png");
            File.WriteAllBytes(noisePath, noise);

            StorageFile noiseFile = await StorageFile.GetFileFromPathAsync(noisePath.Replace('/', '\\'));
            byte[] noiseRendered = await ImagePrep.RenderAsync(noiseFile, width, height,
                FitMode.Cover, OutputImageFormat.Png, false);
            byte[] noiseFit = PngCodec.FitToExactLength(noiseRendered, original.Length, out error);
            Console.WriteLine("  纯噪声图：" + noiseRendered.Length.ToString("N0", CultureInfo.InvariantCulture)
                + " 字节，补齐结果：" + (noiseFit == null ? "拒绝" : noiseFit.Length.ToString("N0", CultureInfo.InvariantCulture)));
            Check(noiseFit == null && !String.IsNullOrEmpty(error), "不可压缩图被明确拒绝而非静默失败", error);
            Check(noiseFit == null && error != null && error.Contains("超出"),
                "拒绝信息包含超出比例（用户可据此换图）", error);
        }

        /// <summary>解码成 BGRA8 直通 alpha，用于像素级比较。</summary>
        private static async Task<byte[]> DecodeBgraAsync(byte[] png, int width, int height)
        {
            using (InMemoryRandomAccessStream input = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(input))
                {
                    writer.WriteBytes(png);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                input.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                using (SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight))
                {
                    byte[] pixels = new byte[width * height * 4];
                    bitmap.CopyToBuffer(pixels.AsBuffer());
                    return pixels;
                }
            }
        }

        /// <summary>
        /// 只统计非全透明像素的 RGB 误差：全透明像素的 RGB 是噪声，
        /// 补齐过程会把它归一化为 0，拿来算 PSNR 没有意义。
        /// </summary>
        private static void ComparePixels(byte[] before, byte[] after, out double psnr, out int maxDiff)
        {
            double sum = 0;
            long count = 0;
            maxDiff = 0;

            int n = Math.Min(before.Length, after.Length);
            for (int i = 0; i + 3 < n; i += 4)
            {
                if (before[i + 3] == 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    int d = before[i + c] - after[i + c];
                    if (d < 0) d = -d;
                    if (d > maxDiff) maxDiff = d;
                    sum += (double)d * d;
                    count++;
                }
            }

            if (count == 0) { psnr = 0; return; }
            double mse = sum / count;
            psnr = mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        /// <summary>
        /// 造一张超体积图，两种模式对应两类真实输入：
        ///   pureNoise=false：渐变底 + 0~7 的逐像素噪声。噪声让无损压缩彻底失效（体积冲到 1.5 倍以上），
        ///                    但它恰好落在低 3 位上，5 位量化就能整体抹掉——这正是"照片/插画"的典型行为。
        ///   pureNoise=true ：0~255 全幅随机噪声。任何量化都去不掉，用来验证"该拒绝时明确拒绝"。
        /// 两者都带一条右侧半透明带，顺带覆盖丢 alpha 的那几档。
        /// </summary>
        private static async Task<byte[]> MakeHeavyPngAsync(int width, int height, bool pureNoise)
        {
            byte[] pixels = new byte[width * height * 4];
            uint seed = 0x12345678u;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    int grain = pureNoise
                        ? ((int)((seed >> 24) & 0xFF) - 128) / 6      // ±21，任何量化都去不掉
                        : (int)((seed >> 24) & 0x0F);                 // 0~15，低 4 位，4 位量化可整体抹掉

                    double u = (double)x / width;
                    double v = (double)y / height;

                    int i = (y * width + x) * 4;
                    pixels[i] = ClampToByte(40 + 170 * u + grain);                      // B
                    pixels[i + 1] = ClampToByte(30 + 150 * v + grain);                  // G
                    pixels[i + 2] = ClampToByte(120 + 90 * (1 - u) * (1 - v) + grain);  // R
                    pixels[i + 3] = (byte)(u > 0.75 ? 160 : 255);                       // 右侧半透明带
                }
            }

            using (InMemoryRandomAccessStream output = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    (uint)width, (uint)height, 96, 96, pixels);
                await encoder.FlushAsync();

                output.Seek(0);
                uint size = (uint)output.Size;
                byte[] bytes = new byte[size];
                using (DataReader reader = new DataReader(output.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync(size);
                    reader.ReadBytes(bytes);
                }
                return bytes;
            }
        }

        private static byte ClampToByte(double value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        // ==================== 8. 回归：After Effects 2026（PE 资源路线） ====================

        private static void TestRealAfterEffects(string sandbox)
        {
            Section("8. 回归：Adobe After Effects 2026（PE 命名资源）");

            string support = Path.Combine(AppCatalog.DefaultAdobeRoot, @"Adobe After Effects 2026\Support Files");
            string realDll = Path.Combine(support, "AfterFXLib.dll");
            string realExe = Path.Combine(support, "AfterFX.exe");
            if (!File.Exists(realDll) || !File.Exists(realExe))
            {
                Console.WriteLine("  （本机未安装 After Effects 2026，跳过）");
                return;
            }

            string work = Path.Combine(sandbox, "AE2026");
            Directory.CreateDirectory(work);
            string dll = Path.Combine(work, "AfterFXLib.dll");
            string exe = Path.Combine(work, "AfterFX.exe");
            File.Copy(realDll, dll, true);
            File.Copy(realExe, exe, true);

            string originalHash = HashTools.Sha256File(dll);
            string error;
            List<SplashCandidate> found = AdobeSplashService.Discover(exe, false, null, out error);
            Check(found != null && found.Count > 0, "PE 资源扫描成功",
                found == null ? error : found.Count + " 项");
            if (found == null || found.Count == 0) return;

            SplashCandidate top = found[0];
            Console.WriteLine("  首选：" + top.Display);
            Check(top.Kind == SplashStorageKind.PeResource, "首选为 PE 资源通道", top.KindText);
            Check(String.Equals(top.NameName, "AE_SPLASH", StringComparison.Ordinal), "首选为 AE_SPLASH", top.NameName);

            List<SplashCandidate> group = AdobeSplashService.ExpandGroup(found, top, true);
            Check(group.Count >= 2, "DPI 变体分组", group.Count + " 项");

            byte[] png = MakePng(top.Width, top.Height, 0x1E, 0x6B, 0xC8);
            SplashApplyResult result = AdobeSplashService.ApplyAuto(group,
                delegate(SplashCandidate c) { return MakePng(c.Width, c.Height, 0x1E, 0x6B, 0xC8); }, out error);
            Check(result != null, "PE 资源写入成功", result == null ? error : result.Detail);

            if (result != null)
            {
                Check(result.PatchedSize > 0, "写回后文件可读",
                    result.OriginalSize.ToString("N0", CultureInfo.InvariantCulture)
                    + " → " + result.PatchedSize.ToString("N0", CultureInfo.InvariantCulture) + " 字节");

                string restoreError;
                bool restored = AdobeSplashService.Restore(dll, out restoreError);
                Check(restored, "还原备份", restored ? "" : restoreError);
                if (restored)
                {
                    Check(HashTools.Sha256File(dll) == originalHash, "还原后 sha256 与原件一致", "");
                }
            }
        }

        // ==================== 9. 各软件扫描诊断（不做断言，仅输出实测结果） ====================

        private static void Diagnostics()
        {
            Section("9. 本机各软件扫描诊断（实测，用于核对支持范围）");

            List<SplashAppEntry> apps = AppCatalog.Discover(null);
            for (int i = 0; i < apps.Count; i++)
            {
                string error;
                List<SplashCandidate> found = AdobeSplashService.DiscoverForApp(apps[i], false, null, out error);
                if (found == null)
                {
                    Console.WriteLine("  " + apps[i].DisplayName + "  →  扫描失败：" + error);
                    continue;
                }

                Console.WriteLine("  " + apps[i].DisplayName + "  →  " + found.Count + " 个候选");
                for (int k = 0; k < found.Count && k < 3; k++)
                {
                    Console.WriteLine("      " + found[k].Display);
                }
                if (found.Count == 0)
                {
                    Console.WriteLine("      （默认门槛下没有命中，可勾选\"显示全部\"或在界面上手动选文件）");
                }
            }
        }

        // ==================== 样本生成 ====================

        private static byte[] MakePng(int width, int height, byte r, byte g, byte b)
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
            ihdr[8] = 8;
            ihdr[9] = 6;
            png.AddRange(PngCodec.BuildChunk("IHDR", ihdr));
            png.AddRange(PngCodec.BuildChunk("IDAT", compressed));
            png.AddRange(PngCodec.BuildChunk("IEND", new byte[0]));
            return png.ToArray();
        }

        private static byte[] MakeBmp(int width, int height, int bitCount)
        {
            int rowSize = ((width * bitCount + 31) / 32) * 4;
            int pixelBytes = rowSize * height;
            int fileSize = 14 + 40 + pixelBytes;
            byte[] data = new byte[fileSize];

            data[0] = (byte)'B';
            data[1] = (byte)'M';
            BitConverter.GetBytes(fileSize).CopyTo(data, 2);
            BitConverter.GetBytes(54).CopyTo(data, 10);
            BitConverter.GetBytes(40).CopyTo(data, 14);
            BitConverter.GetBytes(width).CopyTo(data, 18);
            BitConverter.GetBytes(height).CopyTo(data, 22);
            BitConverter.GetBytes((short)1).CopyTo(data, 26);
            BitConverter.GetBytes((short)bitCount).CopyTo(data, 28);
            BitConverter.GetBytes(pixelBytes).CopyTo(data, 34);

            FillBmp(data, 0x30, 0x30, 0x30);
            return data;
        }

        private static void FillBmp(byte[] data, byte r, byte g, byte b)
        {
            for (int i = 54; i + 2 < data.Length; i += 3)
            {
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
            }
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
