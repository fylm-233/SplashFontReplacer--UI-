using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MultiReplacer.Core
{
    /// <summary>
    /// 统一扫描与写回的分发部分。
    ///
    /// 扫描策略刻意做成"内容驱动"而不是"查表"：
    /// 目录里有什么就报什么，四条通道（PE 资源 / 独立文件 / 容器内嵌 / 归档条目）
    /// 各扫一遍再合并排序。这样新增软件、软件换版本、资源改名都不需要改代码，
    /// 代价是要给扫描范围设上限，避免把整个安装目录翻一遍。
    /// </summary>
    public static partial class AdobeSplashService
    {
        private const int MaxFolders = 48;
        private const int MaxPeFiles = 900;
        private const int MaxContainerFiles = 24;
        private const int MaxArchiveFiles = 40;
        private const int MaxEmbeddedHitsPerFile = 60;

        /// <summary>容器/归档路线的最小尺寸，比 PE 路线高，用于过滤图标级噪声。</summary>
        private const int ContainerMinWidth = 400;
        private const int ContainerMinHeight = 300;

        /// <summary>按已识别软件扫描；会额外加入该家族的已知子目录。</summary>
        public static List<SplashCandidate> DiscoverForApp(SplashAppEntry app, bool includeAll, Action<string> log, out string error)
        {
            error = null;
            if (app == null || String.IsNullOrEmpty(app.Root) || !Directory.Exists(app.Root))
            {
                error = "软件目录不存在。";
                return null;
            }

            List<string> folders = BuildFolders(app);
            if (log != null) log("扫描目录（" + folders.Count + " 个）：" + String.Join(" | ", folders.ToArray()));
            return ScanFolders(folders, app.Root, includeAll, log, out error);
        }

        /// <summary>按目录扫描（不依赖软件识别结果，供拖入任意目录时使用）。</summary>
        public static List<SplashCandidate> DiscoverRoot(string root, string exeHint, bool includeAll, Action<string> log, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                error = "目录不存在：" + root;
                return null;
            }

            List<string> folders = new List<string>();
            AddFolder(folders, root);

            // 主程序所在目录优先，其次是一级子目录
            if (!String.IsNullOrEmpty(exeHint))
            {
                string exeDir = Path.GetDirectoryName(exeHint);
                AddFolder(folders, exeDir);
            }
            CollectChildFolders(root, folders, MaxFolders);

            string[] known = { "Support Files", "Resources", "Required", "Contents", "PNG", "lib", "bin" };
            for (int i = 0; i < known.Length; i++) AddFolder(folders, Path.Combine(root, known[i]));

            if (log != null) log("扫描目录（" + folders.Count + " 个）：" + String.Join(" | ", folders.ToArray()));
            return ScanFolders(folders, root, includeAll, log, out error);
        }

        private static List<string> BuildFolders(SplashAppEntry app)
        {
            List<string> folders = new List<string>();
            AddFolder(folders, app.Root);
            CollectChildFolders(app.Root, folders, MaxFolders);

            string[] hints = FamilyHints(app.Family);
            for (int i = 0; i < hints.Length; i++) AddFolder(folders, Path.Combine(app.Root, hints[i]));
            return folders;
        }

        /// <summary>各家族的已知资源目录（相对安装根）。找不到就跳过，不报错。</summary>
        private static string[] FamilyHints(string family)
        {
            switch (family)
            {
                case SplashFamilies.Unreal:
                    return new string[] { @"Engine\Content\Splash", @"Engine\Content\Slate", @"Engine\Content\Editor\Slate" };
                case SplashFamilies.Maya:
                    return new string[] { "splash", "icons", @"icons\splash", "bin" };
                case SplashFamilies.ThreeDsMax:
                    return new string[] { "UI_ln", "icons", "plugcfg" };
                case SplashFamilies.JetBrains:
                    return new string[] { "lib", @"lib\app.jar", "plugins" };
                case SplashFamilies.VisualStudio:
                    return new string[] { @"Common7\IDE", @"Common7\IDE\CommonExtensions\Microsoft" };
                case SplashFamilies.Live2D:
                    return new string[] { "lib", "resources" };
                case SplashFamilies.Adobe:
                    return new string[] { "Support Files", "Resources", "Required", "Contents", "PNG", "Presets" };
                default:
                    return new string[] { "Resources", "resource", "assets", "data" };
            }
        }

        private static void AddFolder(List<string> folders, string dir)
        {
            if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            for (int i = 0; i < folders.Count; i++)
            {
                if (String.Equals(folders[i], dir, StringComparison.OrdinalIgnoreCase)) return;
            }
            folders.Add(dir);
        }

        private static void CollectChildFolders(string root, List<string> folders, int max)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (folders.Count >= max) return;
                    AddFolder(folders, dir);
                }
            }
            catch
            {
            }
        }

        private static List<SplashCandidate> ScanFolders(List<string> folders, string root, bool includeAll,
                                                         Action<string> log, out string error)
        {
            error = null;
            List<SplashCandidate> result = new List<SplashCandidate>();

            // ---- 1) PE 资源 ----
            int peCount = 0;
            for (int f = 0; f < folders.Count && peCount < MaxPeFiles; f++)
            {
                List<string> files = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(folders[f], "*.dll", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(folders[f], "*.exe", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(folders[f], "*.ocx", SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex)
                {
                    if (log != null) log("跳过目录 " + folders[f] + "：" + ex.Message);
                    continue;
                }

                for (int i = 0; i < files.Count && peCount < MaxPeFiles; i++)
                {
                    peCount++;
                    CollectResourceCandidates(files[i], root, includeAll, result);
                }
            }

            // ---- 2) 独立图片文件 ----
            for (int f = 0; f < folders.Count; f++) CollectLooseCandidates(folders[f], root, result);

            // ---- 3) 容器内嵌图片 ----
            int containerCount = 0;
            for (int f = 0; f < folders.Count && containerCount < MaxContainerFiles; f++)
            {
                string[] files;
                try { files = Directory.GetFiles(folders[f], "*", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                for (int i = 0; i < files.Length && containerCount < MaxContainerFiles; i++)
                {
                    if (!EmbeddedImages.LooksLikeContainer(files[i])) continue;
                    containerCount++;
                    CollectEmbeddedCandidates(files[i], root, includeAll, result, log);
                }
            }

            // ---- 4) 归档条目 ----
            int archiveCount = 0;
            for (int f = 0; f < folders.Count && archiveCount < MaxArchiveFiles; f++)
            {
                List<string> files = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(folders[f], "*.jar", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(folders[f], "*.zip", SearchOption.TopDirectoryOnly));
                }
                catch { continue; }

                for (int i = 0; i < files.Count && archiveCount < MaxArchiveFiles; i++)
                {
                    if (!ZipImages.LooksLikeArchive(files[i])) continue;
                    archiveCount++;
                    CollectZipCandidates(files[i], root, includeAll, result, log);
                }
            }

            if (log != null)
            {
                log(String.Format(CultureInfo.InvariantCulture,
                    "扫描完成：PE {0} 个文件、容器 {1} 个、归档 {2} 个。", peCount, containerCount, archiveCount));
            }

            SortCandidates(result);
            if (log != null) log("候选启动图 " + result.Count + " 项。");
            return result;
        }

        private static void CollectEmbeddedCandidates(string file, string root, bool includeAll,
                                                      List<SplashCandidate> sink, Action<string> log)
        {
            string error;
            List<EmbeddedImageHit> hits = EmbeddedImages.ScanFile(file, ContainerMinWidth, ContainerMinHeight,
                                                                 MaxEmbeddedHitsPerFile, out error);
            if (hits == null)
            {
                if (log != null) log("跳过容器 " + Path.GetFileName(file) + "：" + error);
                return;
            }
            if (hits.Count == 0) return;

            // 同一容器里同尺寸重复出现的多半是状态图标，降权处理
            Dictionary<string, int> sizeFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < hits.Count; i++)
            {
                string key = hits[i].Width + "x" + hits[i].Height;
                int n;
                sizeFrequency.TryGetValue(key, out n);
                sizeFrequency[key] = n + 1;
            }

            string relative = MakeRelative(root, file);
            string stem = Path.GetFileNameWithoutExtension(file);

            for (int i = 0; i < hits.Count; i++)
            {
                EmbeddedImageHit hit = hits[i];
                SplashCandidate c = new SplashCandidate();
                c.FilePath = file;
                c.RelativeFile = relative;
                c.IsLooseFile = false;
                c.Kind = SplashStorageKind.Embedded;
                c.Format = hit.Format;
                c.TypeName = "EMBEDDED";
                c.NameName = hit.OffsetHex;
                c.LanguageId = 0;
                c.Width = hit.Width;
                c.Height = hit.Height;
                c.Size = hit.Length;
                c.DataOffset = hit.Offset;
                c.LikelySplash = ImageProbe.LooksLikeSplashBySize(hit.Width, hit.Height);
                c.Score = ImageProbe.ScoreByGeometry(hit.Width, hit.Height, hit.Length)
                          + ScoreContainerName(stem);

                int frequency;
                if (sizeFrequency.TryGetValue(hit.Width + "x" + hit.Height, out frequency) && frequency > 2)
                {
                    c.Score -= 25;
                }

                if (!includeAll && !c.LikelySplash) continue;
                sink.Add(c);
            }
        }

        private static void CollectZipCandidates(string file, string root, bool includeAll,
                                                 List<SplashCandidate> sink, Action<string> log)
        {
            string error;
            List<ZipImageEntry> entries = ZipImages.List(file, 200, 150, out error);
            if (entries == null)
            {
                if (log != null) log("跳过归档 " + Path.GetFileName(file) + "：" + error);
                return;
            }

            string relative = MakeRelative(root, file);
            for (int i = 0; i < entries.Count; i++)
            {
                ZipImageEntry entry = entries[i];
                SplashCandidate c = new SplashCandidate();
                c.FilePath = file;
                c.RelativeFile = relative;
                c.IsLooseFile = false;
                c.Kind = SplashStorageKind.ZipEntry;
                c.Format = entry.Format;
                c.TypeName = "ZIP";
                c.NameName = entry.EntryName;
                c.ZipEntry = entry.EntryName;
                c.LanguageId = 0;
                c.Width = entry.Width;
                c.Height = entry.Height;
                c.Size = (int)Math.Min(entry.UncompressedLength, Int32.MaxValue);
                c.LikelySplash = LooksLikeSplash(entry.EntryName, entry.Width, entry.Height);
                c.Score = ComputeScore(entry.EntryName, entry.Width, entry.Height)
                          + ImageProbe.ScoreByGeometry(entry.Width, entry.Height, c.Size);

                if (!includeAll && !c.LikelySplash) continue;
                sink.Add(c);
            }
        }

        /// <summary>容器文件名里带这些字样的，其中的图更可能是启动图。</summary>
        private static int ScoreContainerName(string stem)
        {
            if (String.IsNullOrEmpty(stem)) return 0;
            string s = stem.ToLowerInvariant();
            int score = 0;
            if (s.IndexOf("splash", StringComparison.Ordinal) >= 0) score += 60;
            if (s.IndexOf("icon", StringComparison.Ordinal) >= 0) score += 20;   // PSIcons*Res.dat 就是这类
            if (s.IndexOf("brand", StringComparison.Ordinal) >= 0) score += 20;
            if (s.IndexOf("logo", StringComparison.Ordinal) >= 0) score += 10;
            return score;
        }

        private static void SortCandidates(List<SplashCandidate> result)
        {
            result.Sort(delegate(SplashCandidate a, SplashCandidate b)
            {
                int x = b.Score.CompareTo(a.Score);
                if (x != 0)
                {
                    // 同一文件里同一组（1x / 2x / 1.5x）不按尺寸加分排序，
                    // 否则 2x 会因为宽高更大而排在标准尺寸前面。
                    bool sameGroup = String.Equals(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase)
                                     && String.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                    if (!sameGroup) return x;
                }

                // 同一组里让不带 DPI 后缀的那张排在最前，用户先看到的就是「标准尺寸」
                bool aBase = String.Equals(a.NameName, a.Stem, StringComparison.OrdinalIgnoreCase);
                bool bBase = String.Equals(b.NameName, b.Stem, StringComparison.OrdinalIgnoreCase);
                if (aBase != bBase) return aBase ? -1 : 1;

                int y = b.Width.CompareTo(a.Width);
                if (y != 0) return y;
                return String.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });
        }

        // ==================== 写回分发 ====================

        /// <summary>
        /// 生成打好补丁的副本（不写回目标），按候选的存储形态自动选择通道。
        /// render(candidate) 由调用方负责把用户图缩放成目标尺寸并编码成"目标原生格式"
        /// （PNG 目标给 PNG，BMP 目标给 BMP），容器路线还会在此之上做等长补齐。
        /// </summary>
        public static SplashApplyResult ApplyToTempAuto(List<SplashCandidate> group, Func<SplashCandidate, byte[]> render,
                                                        out string tempFile, out string error)
        {
            tempFile = null;
            error = null;
            if (group == null || group.Count == 0) { error = "没有可写入的目标。"; return null; }
            if (render == null) { error = "没有提供图片渲染回调。"; return null; }

            SplashCandidate first = group[0];
            SplashStorageKind kind = first.Kind;
            for (int i = 1; i < group.Count; i++)
            {
                if (group[i].Kind != kind)
                {
                    error = "一次只能写入同一种存储形态的目标。";
                    return null;
                }
            }

            if (kind == SplashStorageKind.PeResource)
            {
                // PE 资源允许长度变化，交给原有通道，这里只做尺寸适配
                SplashApplyResult peResult = ApplyToTemp(group, delegate(int w, int h)
                {
                    for (int i = 0; i < group.Count; i++)
                    {
                        if (group[i].Width == w && group[i].Height == h) return render(group[i]);
                    }
                    return render(first);
                }, out tempFile, out error);

                if (peResult != null)
                {
                    peResult.HostFiles.Add(peResult.HostFile);
                    if (tempFile != null) peResult.TempFiles.Add(tempFile);
                }
                return peResult;
            }

            // 非 PE 路线：按主机文件分组，每个主机生成一个补丁副本
            List<string> hosts = new List<string>();
            Dictionary<string, List<SplashCandidate>> byHost = new Dictionary<string, List<SplashCandidate>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < group.Count; i++)
            {
                SplashCandidate c = group[i];
                List<SplashCandidate> list;
                if (!byHost.TryGetValue(c.FilePath, out list))
                {
                    list = new List<SplashCandidate>();
                    byHost[c.FilePath] = list;
                    hosts.Add(c.FilePath);
                }
                list.Add(c);
            }

            SplashApplyResult result = new SplashApplyResult();
            result.HostFile = first.FilePath;
            result.ReplacedCount = group.Count;
            for (int i = 0; i < group.Count; i++) result.Replaced.Add(group[i].NameName);

            List<string> details = new List<string>();
            try
            {
                for (int h = 0; h < hosts.Count; h++)
                {
                    string host = hosts[h];
                    List<SplashCandidate> items = byHost[host];

                    long originalSize = new FileInfo(host).Length;
                    result.OriginalSize += originalSize;

                    string backup;
                    if (!Backup(host, out backup, out error)) return null;
                    if (String.IsNullOrEmpty(result.BackupFile)) result.BackupFile = backup;

                    string temp = Path.Combine(Path.GetTempPath(),
                        "MultiReplacer_" + Guid.NewGuid().ToString("N") + Path.GetExtension(host));

                    string detail;
                    if (!PatchOneHost(kind, host, temp, items, render, out detail, out error)) return null;

                    result.HostFiles.Add(host);
                    result.TempFiles.Add(temp);
                    result.PatchedSize += new FileInfo(temp).Length;
                    details.Add(Path.GetFileName(host) + "：" + detail);
                }

                result.Detail = String.Join("  ", details.ToArray());
                tempFile = result.TempFiles.Count > 0 ? result.TempFiles[0] : null;
                return result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                for (int i = 0; i < result.TempFiles.Count; i++)
                {
                    try { if (File.Exists(result.TempFiles[i])) File.Delete(result.TempFiles[i]); } catch { }
                }
                return null;
            }
        }

        /// <summary>单个主机文件的补丁生成，按存储形态选择通道。</summary>
        private static bool PatchOneHost(SplashStorageKind kind, string host, string temp, List<SplashCandidate> items,
                                         Func<SplashCandidate, byte[]> render, out string detail, out string error)
        {
            detail = null;
            error = null;

            switch (kind)
            {
                case SplashStorageKind.LooseImage:
                {
                    byte[] data = render(items[0]);
                    if (data == null || data.Length == 0) { error = "生成替换图失败。"; return false; }
                    File.WriteAllBytes(temp, data);
                    byte[] check = File.ReadAllBytes(temp);
                    if (check.Length != data.Length) { error = "写后长度不符。"; return false; }
                    detail = String.Format(CultureInfo.InvariantCulture,
                        "独立文件覆盖完成，{0:N0} → {1:N0} 字节。", new FileInfo(host).Length, data.Length);
                    return true;
                }

                case SplashStorageKind.Embedded:
                {
                    List<EmbeddedPatchItem> patch = new List<EmbeddedPatchItem>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        SplashCandidate c = items[i];
                        byte[] data = render(c);
                        if (data == null) { error = "生成替换图失败：" + c.NameName; return false; }

                        // 容器路线必须等长：PNG 用 chunk 补齐，BMP 要求同尺寸同位深
                        if (data.Length != c.Size)
                        {
                            if (c.Format == SplashImageFormat.Png)
                            {
                                string fitError;
                                byte[] fitted = PngCodec.FitToExactLength(data, c.Size, out fitError);
                                if (fitted == null)
                                {
                                    error = "无法把替换图补齐到 " + c.Size.ToString("N0", CultureInfo.InvariantCulture)
                                            + " 字节（容器路线要求等长）：" + fitError;
                                    return false;
                                }
                                data = fitted;
                            }
                            else
                            {
                                error = String.Format(CultureInfo.InvariantCulture,
                                    "替换图 {0:N0} 字节与原图 {1:N0} 字节不一致，{2} 容器路线无法自动补齐。请使用同尺寸同色深的图片。",
                                    data.Length, c.Size, ImageProbe.FormatText(c.Format));
                                return false;
                            }
                        }

                        EmbeddedPatchItem item = new EmbeddedPatchItem();
                        EmbeddedImageHit hit = new EmbeddedImageHit();
                        hit.Offset = c.DataOffset;
                        hit.Length = c.Size;
                        hit.Width = c.Width;
                        hit.Height = c.Height;
                        hit.Format = c.Format;
                        item.Hit = hit;
                        item.Data = data;
                        patch.Add(item);
                    }

                    return EmbeddedImages.PatchInPlace(host, temp, patch, out error, out detail);
                }

                case SplashStorageKind.ZipEntry:
                {
                    byte[] data = render(items[0]);
                    if (data == null) { error = "生成替换图失败。"; return false; }
                    if (!ZipImages.Replace(host, temp, items[0].ZipEntry, data, out error, out detail)) return false;
                    if (ZipImages.HasSignature(temp))
                    {
                        detail += " 注意：该归档带 JAR 签名，替换后签名失效，软件可能拒绝加载。";
                    }
                    return true;
                }

                default:
                    error = "未知的存储形态。";
                    return false;
            }
        }

        /// <summary>生成补丁副本并直接部署回目标（不走提权，权限不足会抛给上层）。</summary>
        public static SplashApplyResult ApplyAuto(List<SplashCandidate> group, Func<SplashCandidate, byte[]> render, out string error)
        {
            string temp;
            SplashApplyResult result = ApplyToTempAuto(group, render, out temp, out error);
            if (result == null) return null;
            try
            {
                for (int i = 0; i < result.TempFiles.Count && i < result.HostFiles.Count; i++)
                {
                    if (!Deploy(result.TempFiles[i], result.HostFiles[i], out error)) return null;
                }
                return result;
            }
            finally
            {
                for (int i = 0; i < result.TempFiles.Count; i++)
                {
                    try { if (File.Exists(result.TempFiles[i])) File.Delete(result.TempFiles[i]); } catch { }
                }
            }
        }
    }
}
