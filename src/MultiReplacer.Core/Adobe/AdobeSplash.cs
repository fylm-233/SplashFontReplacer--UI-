using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MultiReplacer.Core
{
    /// <summary>启动图在目标文件里的存储形态，决定走哪条写回通道。</summary>
    public enum SplashStorageKind
    {
        /// <summary>PE 资源树里的命名/ID 资源（Adobe AIR 系、VS 等），UpdateResource 可变长写回。</summary>
        PeResource = 0,
        /// <summary>磁盘上的独立图片文件（Unreal / Maya 的 BMP 等），整文件覆盖。</summary>
        LooseImage = 1,
        /// <summary>私有容器文件里内嵌的图片（Photoshop .dat、InDesign .idrc 等），等长原地覆盖。</summary>
        Embedded = 2,
        /// <summary>jar / zip 归档里的图片条目（JetBrains、Live2D），重写条目。</summary>
        ZipEntry = 3
    }

    /// <summary>一个可替换的启动图目标（PE 命名资源，或磁盘上的独立 PNG 文件）。</summary>
    public sealed class SplashCandidate
    {
        public string FilePath;
        public string RelativeFile;
        public bool IsLooseFile;
        public PeResourceEntry Resource;

        public string TypeName;
        public string NameName;
        public int LanguageId;

        public int Width;
        public int Height;
        public int Size;

        /// <summary>存储形态，决定写回通道。</summary>
        public SplashStorageKind Kind;

        /// <summary>图片实际格式（PNG / BMP / JPEG）。</summary>
        public SplashImageFormat Format;

        /// <summary>容器内嵌路线的绝对偏移。</summary>
        public long DataOffset;

        /// <summary>归档路线的条目名。</summary>
        public string ZipEntry;

        /// <summary>名称或尺寸命中启动图特征。</summary>
        public bool LikelySplash;

        /// <summary>排序权重，越大越可能真的是启动图。</summary>
        public int Score;

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case SplashStorageKind.PeResource: return "资源";
                    case SplashStorageKind.LooseImage: return "文件";
                    case SplashStorageKind.Embedded: return "容器";
                    case SplashStorageKind.ZipEntry: return "归档";
                    default: return "?";
                }
            }
        }

        /// <summary>写回时的定位说明，用于确认对话框与日志。</summary>
        public string Location
        {
            get
            {
                switch (Kind)
                {
                    case SplashStorageKind.PeResource:
                        return "PE 资源 " + TypeName + "\\" + NameName;
                    case SplashStorageKind.LooseImage:
                        return "独立文件 " + RelativeFile;
                    case SplashStorageKind.Embedded:
                        return String.Format(CultureInfo.InvariantCulture, "容器 {0} 偏移 0x{1:X8}",
                            RelativeFile, DataOffset);
                    case SplashStorageKind.ZipEntry:
                        return "归档 " + RelativeFile + " 条目 " + ZipEntry;
                    default:
                        return RelativeFile;
                }
            }
        }

        public string Display
        {
            get
            {
                return String.Format(CultureInfo.InvariantCulture,
                    "{0}  ·  {1} {2}  ·  {3}×{4}  ·  {5:N0} 字节  ·  {6}",
                    RelativeFile, KindText, NameName, Width, Height, Size, LikelySplash ? "疑似启动图" : "");
            }
        }

        /// <summary>去掉 DPI 后缀后的主干名，用于把 1x / 1.5x / 2x 归为一组。</summary>
        public string Stem
        {
            get
            {
                string name = NameName ?? String.Empty;
                string[] suffixes = { "_AT_3TO2X", "_AT_1TO2X", "_AT_2X", "_AT_3X", "_AT_4X", "@3to2x", "@1.5x", "@2x", "@3x", "-2x", "_2X" };
                for (int i = 0; i < suffixes.Length; i++)
                {
                    if (name.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                        return name.Substring(0, name.Length - suffixes[i].Length);
                }
                return name;
            }
        }

        /// <summary>
        /// 所在文件名去掉分辨率后缀后的主干：
        /// PSIconsHighRes.dat / PSIconsLowRes.dat → PSIcons。
        /// 用于把"同一套资源的高/低分辨率容器"归为一组一起替换。
        /// </summary>
        public string FileStem
        {
            get
            {
                string file = RelativeFile ?? String.Empty;
                try { file = System.IO.Path.GetFileName(file); }
                catch { }
                string name = file;
                int dot = name.LastIndexOf('.');
                if (dot > 0) name = name.Substring(0, dot);

                string[] suffixes = { "XHighRes", "XLowRes", "HighRes", "LowRes", "HiRes", "LoRes", "_2x", "@2x", "2x" };
                for (int i = 0; i < suffixes.Length; i++)
                {
                    if (name.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                        return name.Substring(0, name.Length - suffixes[i].Length);
                }
                return name;
            }
        }
    }

    public sealed class SplashApplyResult
    {
        public string HostFile;
        public string BackupFile;
        public int ReplacedCount;
        public long OriginalSize;
        public long PatchedSize;
        public string Detail;
        public List<string> Replaced = new List<string>();

        /// <summary>涉及的主机文件（容器高/低分辨率成对替换时会多于一个）。</summary>
        public List<string> HostFiles = new List<string>();

        /// <summary>与 HostFiles 一一对应的临时补丁文件，调用方负责部署后删除。</summary>
        public List<string> TempFiles = new List<string>();
    }

    /// <summary>
    /// 启动图替换的统一引擎。最初只为 Adobe 而写，现在按"存储形态"抽象出四条写回通道，
    /// 同一套候选模型 / 备份 / 部署逻辑覆盖 Adobe 与 Unreal、Maya、JetBrains、VS、Live2D、
    /// 3ds Max 等非 Adobe 软件：
    ///
    ///   PeResource —— PE 资源树里的 PNG/BMP 资源，UpdateResource 写回，长度可变
    ///   LooseImage —— 磁盘上的独立图片文件，整文件覆盖
    ///   Embedded   —— 私有容器里内嵌的图片，等长原地覆盖（不动容器索引）
    ///   ZipEntry   —— jar/zip 归档里的图片条目，重写条目
    ///
    /// 与 Selorify 的差异（刻意改进，均在 UI 上有对应说明）：
    /// 1. 不依赖内置的「软件 × 版本 → 资源名」表，改为扫描目标文件里所有图片资源，
    ///    因此新版软件、非 Adobe 软件、以及表里没收录的版本同样可用；
    /// 2. 写入前把用户图缩放到原资源尺寸，避免 Selorify 已知的「尺寸差异过大导致目标崩溃」；
    /// 3. 同一张图自动生成 1x / 1.5x / 2x 全组变体，避免高分屏下只有一半生效。
    /// </summary>
    public static partial class AdobeSplashService
    {
        private static readonly string[] SplashHints =
        {
            "SPLASH", "splash", "Splash", "启动", "LAUNCH", "Launch", "Brand", "brand"
        };

        // ---------------- 目标定位 ----------------

        /// <summary>把 .lnk 解析成真实目标；不是快捷方式则原样返回。</summary>
        public static string ResolveShortcut(string path, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(path)) return path;
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    error = "系统未注册 WScript.Shell，无法解析快捷方式。";
                    return null;
                }
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { path });
                string target = (string)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty,
                    null, shortcut, null);
                if (String.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    error = "快捷方式指向的文件不存在：" + target;
                    return null;
                }
                return target;
            }
            catch (Exception ex)
            {
                error = "解析快捷方式失败：" + ex.Message;
                return null;
            }
        }

        // ---------------- 扫描 ----------------

        public static List<SplashCandidate> Discover(string targetPath, bool includeAll, Action<string> log, out string error)
        {
            error = null;

            string exePath = ResolveShortcut(targetPath, out error);
            if (exePath == null) return null;
            if (!File.Exists(exePath))
            {
                error = "目标文件不存在：" + exePath;
                return null;
            }

            string root = Path.GetDirectoryName(exePath);
            return DiscoverRoot(root, exePath, includeAll, log, out error);
        }

        private static void CollectResourceCandidates(string file, string root, bool includeAll, List<SplashCandidate> sink)
        {
            string error;
            PeResourceFile pe = PeResourceFile.Load(file, out error);
            if (pe == null || !pe.ResourceSectionPresent) return;

            for (int i = 0; i < pe.Entries.Count; i++)
            {
                PeResourceEntry entry = pe.Entries[i];
                if (entry.Size < 128) continue;

                // 具名类型（PNG / BMP / IMAGE …）默认参与候选；
                // 数字 ID 类型（#2 位图、#3 图标）数量极大且绝大多数是图标，
                // 只有用户主动勾选"显示全部"时才纳入，避免列表被图标淹没。
                bool namedType = entry.TypeIsNamed;
                if (!namedType && !includeAll) continue;

                byte[] head = ReadHead(file, entry.DataOffset, 40);
                SplashImageFormat format;
                int w, h;
                if (!ImageProbe.TryReadSize(head, out format, out w, out h)) continue;

                // 数字类型里只放行真正像启动图的大图
                if (!namedType && (w < 400 || h < 300)) continue;

                SplashCandidate c = new SplashCandidate();
                c.FilePath = file;
                c.RelativeFile = MakeRelative(root, file);
                c.IsLooseFile = false;
                c.Kind = SplashStorageKind.PeResource;
                c.Format = format;
                c.Resource = entry;
                c.TypeName = entry.TypeName;
                c.NameName = entry.NameName;
                c.LanguageId = entry.LanguageId;
                c.Width = w;
                c.Height = h;
                c.Size = entry.Size;
                c.LikelySplash = LooksLikeSplash(entry.NameName, w, h);
                c.Score = ComputeScore(entry.NameName, w, h) + ImageProbe.ScoreByGeometry(w, h, entry.Size);

                if (!includeAll && !c.LikelySplash) continue;
                sink.Add(c);
            }
        }

        private static void CollectLooseCandidates(string folder, string root, List<SplashCandidate> sink)
        {
            List<string> files = new List<string>();
            string[] patterns = { "*.png", "*.bmp", "*.jpg", "*.jpeg" };
            for (int p = 0; p < patterns.Length; p++)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(folder, patterns[p], SearchOption.TopDirectoryOnly));
                }
                catch
                {
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                string name = Path.GetFileNameWithoutExtension(file);

                byte[] head = ReadHead(file, 0, 64);
                SplashImageFormat format;
                int w, h;
                if (!ImageProbe.TryReadSize(head, out format, out w, out h)) continue;

                // 独立文件路线没有容器约束，按"名字像启动图"或"尺寸像启动图"判定
                bool named = LooksLikeSplash(name, 0, 0);
                if (!named && !ImageProbe.LooksLikeSplashBySize(w, h)) continue;

                SplashCandidate c = new SplashCandidate();
                c.FilePath = file;
                c.RelativeFile = MakeRelative(root, file);
                c.IsLooseFile = true;
                c.Kind = SplashStorageKind.LooseImage;
                c.Format = format;
                c.NameName = Path.GetFileName(file);
                c.TypeName = "FILE";
                c.LanguageId = 0;
                c.Width = w;
                c.Height = h;
                c.Size = (int)Math.Min(new FileInfo(file).Length, Int32.MaxValue);
                c.LikelySplash = true;
                c.Score = (named ? 100 : 0) + ImageProbe.ScoreByGeometry(w, h, c.Size);
                sink.Add(c);
            }
        }

        private static bool LooksLikeSplash(string name, int width, int height)
        {
            if (!String.IsNullOrEmpty(name))
            {
                for (int i = 0; i < SplashHints.Length; i++)
                {
                    if (name.IndexOf(SplashHints[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            // 启动图通常是横向大图；这条兜底用于名字不含 splash 的版本
            if (width >= 500 && height >= 400 && width > height) return true;
            return false;
        }

        private static int ComputeScore(string name, int width, int height)
        {
            int score = 0;
            if (!String.IsNullOrEmpty(name))
            {
                if (name.IndexOf("SPLASH", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
                if (name.IndexOf("BETA", StringComparison.OrdinalIgnoreCase) >= 0) score -= 60;
                if (name.IndexOf("ABOUT", StringComparison.OrdinalIgnoreCase) >= 0) score -= 40;
                if (name.IndexOf("THUMB", StringComparison.OrdinalIgnoreCase) >= 0) score -= 30;
                if (name.IndexOf("BANNER", StringComparison.OrdinalIgnoreCase) >= 0) score -= 10;
            }
            if (width >= 800) score += 15;
            if (width > height) score += 5;
            return score;
        }

        private static string MakeRelative(string root, string file)
        {
            try
            {
                if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = file.Substring(root.Length).TrimStart('\\', '/');
                    return rel;
                }
            }
            catch { }
            return Path.GetFileName(file);
        }

        private static byte[] ReadHead(string path, long offset, int count)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset + count > fs.Length) return null;
                    fs.Position = offset;
                    byte[] buffer = new byte[count];
                    int read = 0;
                    while (read < count)
                    {
                        int n = fs.Read(buffer, read, count - read);
                        if (n <= 0) return null;
                        read += n;
                    }
                    return buffer;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把选中项扩成同一 DPI 组（AE_SPLASH / AE_SPLASH_AT_2X / _AT_3TO2X …）。</summary>
        public static List<SplashCandidate> ExpandGroup(List<SplashCandidate> all, SplashCandidate selected, bool includeVariants)
        {
            List<SplashCandidate> group = new List<SplashCandidate>();
            group.Add(selected);
            if (!includeVariants) return group;

            // 独立文件 / 归档都是单个目标，没有变体概念
            if (selected.Kind == SplashStorageKind.LooseImage || selected.Kind == SplashStorageKind.ZipEntry) return group;

            // 容器路线：同主干的高/低分辨率容器各取"最像启动图"的那一张一起换，
            // 否则用户换完高分辨率容器，低分辨率下看到的还是旧图。
            if (selected.Kind == SplashStorageKind.Embedded)
            {
                string fileStem = selected.FileStem;
                if (String.IsNullOrEmpty(fileStem)) return group;

                Dictionary<string, SplashCandidate> best = new Dictionary<string, SplashCandidate>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < all.Count; i++)
                {
                    SplashCandidate c = all[i];
                    if (c == selected) continue;
                    if (c.Kind != SplashStorageKind.Embedded) continue;
                    // 同一个容器文件里的其它图片不是"同一张图的不同分辨率"，不能一起换
                    if (String.Equals(c.FilePath, selected.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!String.Equals(c.FileStem, fileStem, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!c.LikelySplash) continue;

                    SplashCandidate existing;
                    if (!best.TryGetValue(c.FilePath, out existing) || c.Score > existing.Score) best[c.FilePath] = c;
                }
                foreach (KeyValuePair<string, SplashCandidate> pair in best) group.Add(pair.Value);
                return group;
            }

            string stem = selected.Stem;
            if (String.IsNullOrEmpty(stem)) return group;

            for (int i = 0; i < all.Count; i++)
            {
                SplashCandidate c = all[i];
                if (c == selected) continue;
                if (c.Kind != SplashStorageKind.PeResource) continue;
                if (!String.Equals(c.FilePath, selected.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!String.Equals(c.TypeName, selected.TypeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (String.Equals(c.Stem, stem, StringComparison.OrdinalIgnoreCase)) group.Add(c);
            }
            return group;
        }

        // ---------------- 读取 / 写入 ----------------

        public static byte[] Extract(SplashCandidate candidate, out string error)
        {
            error = null;
            if (candidate == null) { error = "未选择启动图。"; return null; }

            switch (candidate.Kind)
            {
                case SplashStorageKind.LooseImage:
                    try { return File.ReadAllBytes(candidate.FilePath); }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }

                case SplashStorageKind.Embedded:
                    EmbeddedImageHit hit = new EmbeddedImageHit();
                    hit.Offset = candidate.DataOffset;
                    hit.Length = candidate.Size;
                    hit.Width = candidate.Width;
                    hit.Height = candidate.Height;
                    hit.Format = candidate.Format;
                    return EmbeddedImages.ReadRegion(candidate.FilePath, hit, out error);

                case SplashStorageKind.ZipEntry:
                    return ZipImages.Extract(candidate.FilePath, candidate.ZipEntry, out error);

                default:
                    PeResourceFile pe = PeResourceFile.Load(candidate.FilePath, out error);
                    if (pe == null) return null;
                    return pe.ReadData(candidate.Resource, out error);
            }
        }

        /// <summary>
        /// 备份并生成打好补丁的副本（不写回目标）。
        /// render(width, height) 由调用方负责把用户图缩放到目标尺寸并编码成 PNG。
        /// 调用方拿到 tempFile 后自行部署，用完负责删除。
        /// </summary>
        public static SplashApplyResult ApplyToTemp(List<SplashCandidate> group, Func<int, int, byte[]> render,
                                                    out string tempFile, out string error)
        {
            tempFile = null;
            error = null;
            if (group == null || group.Count == 0) { error = "没有可写入的目标。"; return null; }

            SplashCandidate first = group[0];
            string host = first.FilePath;

            List<ResourceWriteItem> items = new List<ResourceWriteItem>();
            for (int i = 0; i < group.Count; i++)
            {
                SplashCandidate c = group[i];
                byte[] png = render(c.Width, c.Height);
                if (png == null)
                {
                    error = String.Format(CultureInfo.InvariantCulture, "生成 {0}×{1} 的替换图失败。", c.Width, c.Height);
                    return null;
                }
                ResourceWriteItem item = new ResourceWriteItem();
                item.Entry = c.Resource;
                item.Data = png;
                items.Add(item);
            }

            SplashApplyResult result = new SplashApplyResult();
            result.HostFile = host;
            result.ReplacedCount = items.Count;
            for (int i = 0; i < group.Count; i++) result.Replaced.Add(group[i].NameName);

            string temp = Path.Combine(Path.GetTempPath(),
                "MultiReplacer_" + Guid.NewGuid().ToString("N") + Path.GetExtension(host));
            try
            {
                result.OriginalSize = new FileInfo(host).Length;

                string backup;
                if (!Backup(host, out backup, out error)) return null;
                result.BackupFile = backup;

                if (!PeResourceWriter.ReplaceBatch(host, temp, items, out error)) return null;

                string detail;
                if (!PeResourceWriter.Verify(temp, items, out error, out detail)) return null;
                result.Detail = detail;

                // 写回后重算 PE 校验和，避免部分工具对校验和不一致报警
                try
                {
                    PeInfo info = PeTools.Parse(temp, out string _);
                    if (info != null)
                    {
                        using (System.IO.MemoryMappedFiles.MemoryMappedFile mm =
                            System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(temp, FileMode.Open, null, 0,
                                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite))
                        using (System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view =
                            mm.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite))
                        {
                            bool verified;
                            string csError;
                            PeTools.WriteChecksumAndVerify(view, info.FileSize, info.ChecksumOffset, out verified, out csError);
                        }
                    }
                }
                catch { }

                result.PatchedSize = new FileInfo(temp).Length;
                tempFile = temp;
                return result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return null;
            }
        }

        /// <summary>生成补丁副本并直接部署回目标。</summary>
        public static SplashApplyResult Apply(List<SplashCandidate> group, Func<int, int, byte[]> render, out string error)
        {
            string temp;
            SplashApplyResult result = ApplyToTemp(group, render, out temp, out error);
            if (result == null) return null;
            try
            {
                if (!Deploy(temp, result.HostFile, out error)) return null;
                return result;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <summary>覆盖目标文件；失败时由上层转入提权通道。</summary>
        public static bool Deploy(string sourceFile, string destination, out string error)
        {
            error = null;
            try
            {
                string staged = destination + ".deploy.tmp";
                File.Copy(sourceFile, staged, true);
                File.Move(staged, destination, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ---------------- 备份 ----------------

        public static string BackupDirectory
        {
            get
            {
                string dir = Path.Combine(AppPaths.BackupRoot, "AdobeSplash");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string ManifestPath
        {
            get { return Path.Combine(BackupDirectory, "manifest.txt"); }
        }

        private static string Key(string path)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
                StringBuilder sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static List<string> ReadManifest()
        {
            List<string> lines = new List<string>();
            try
            {
                if (!File.Exists(ManifestPath)) return lines;
                string[] raw = File.ReadAllLines(ManifestPath, Encoding.UTF8);
                for (int i = 0; i < raw.Length; i++)
                {
                    if (!String.IsNullOrWhiteSpace(raw[i])) lines.Add(raw[i]);
                }
            }
            catch { }
            return lines;
        }

        private static void WriteManifest(List<string> lines)
        {
            try { File.WriteAllLines(ManifestPath, lines.ToArray(), Encoding.UTF8); }
            catch { }
        }

        /// <summary>已备份过则保留最早那份原始文件，绝不覆盖（否则会把改过的文件当原件存下来）。</summary>
        public static bool Backup(string path, out string backupFile, out string error)
        {
            backupFile = null;
            error = null;
            try
            {
                string key = Key(path);
                List<string> lines = ReadManifest();
                for (int i = 0; i < lines.Count; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length >= 2 && parts[0] == key)
                    {
                        string existing = Path.Combine(BackupDirectory, parts[1]);
                        if (File.Exists(existing))
                        {
                            backupFile = existing;
                            return true;
                        }
                        lines.RemoveAt(i);
                        i--;
                    }
                }

                string name = key + "_" + Path.GetFileName(path) + ".bak";
                string target = Path.Combine(BackupDirectory, name);
                File.Copy(path, target, true);
                lines.Add(key + "\t" + name + "\t" + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture)
                          + "\t" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + path);
                WriteManifest(lines);
                backupFile = target;
                return true;
            }
            catch (Exception ex)
            {
                error = "备份失败：" + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public static string FindBackup(string path)
        {
            string key = Key(path);
            List<string> lines = ReadManifest();
            for (int i = 0; i < lines.Count; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length >= 2 && parts[0] == key)
                {
                    string file = Path.Combine(BackupDirectory, parts[1]);
                    if (File.Exists(file)) return file;
                }
            }
            return null;
        }

        public static List<string> ListBackups(out List<string> originalPaths)
        {
            originalPaths = new List<string>();
            List<string> files = new List<string>();
            List<string> lines = ReadManifest();
            for (int i = 0; i < lines.Count; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 5) continue;
                string file = Path.Combine(BackupDirectory, parts[1]);
                if (!File.Exists(file)) continue;
                files.Add(file);
                originalPaths.Add(parts[4]);
            }
            return files;
        }

        /// <summary>还原指定目标；找不到备份则返回 false。</summary>
        public static bool Restore(string path, out string error)
        {
            error = null;
            string backup = FindBackup(path);
            if (backup == null)
            {
                error = "没有找到该目标的原始备份。";
                return false;
            }
            if (!Deploy(backup, path, out error)) return false;
            return true;
        }
    }
}
