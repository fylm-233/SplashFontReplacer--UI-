using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MultiReplacer.Core
{
    /// <summary>
    /// 一个可处理的 Adobe 应用（或其自定义语言包目录）。
    ///
    /// Adobe 多数桌面产品（After Effects / Premiere Pro / Media Encoder / Audition /
    /// Prelude / Character Animator 等）的界面字体来自 "AIR/UCF 语言包"：
    ///     &lt;安装目录&gt;\Dictionaries\&lt;locale&gt;\&lt;app&gt;_&lt;locale&gt;.dat
    /// 其中以 $$/.../FontStyle/... 形式记录了字号与字体族名，格式为
    ///     "&lt;size&gt; &lt;style&gt; &lt;字体&gt;"                       （单平台）
    ///     "&lt;size&gt; &lt;style&gt; &lt;Mac字体&gt;{{*MSWindows*}}&lt;size&gt; &lt;style&gt; &lt;Win字体&gt;" （双平台）
    /// 修改 .dat 后还要同步 META-INF\signatures.xml 里的摘要，否则 Adobe 会判定语言包损坏。
    /// </summary>
    public sealed class AdobeAppInfo
    {
        public string Product;              // "After Effects"
        public string DisplayName;          // "Adobe After Effects 2026"
        public string Version;              // "2026" / "CC 2019" / "CS6"
        public string Root;                 // 安装根目录
        public string Locale;               // "zh_CN"
        public string LocaleDir;            // ...\Dictionaries\zh_CN
        public string DatFile;
        public string DatFileName;
        public string SignaturesFile;
        public string MimetypeFile;
        public string KeyPrefix;            // "AE" / "PR" ...
        public bool IsCustom;

        // 以下由 Analyze 填充
        public bool DictionaryFound;
        public bool HasSignature;
        public string DigestAlgorithm;      // "sha1" / "sha256" / "sha512" / ""
        public int StyleKeyCount;
        public int PluginKeyCount;
        public bool SupportsPlatformSeparator;
        public string CurrentFont;
        public int CurrentLarge;
        public int CurrentMedium;
        public int CurrentSmall;
        public string Message;

        public string BackupKey
        {
            get
            {
                string product = String.IsNullOrEmpty(Product) ? "Adobe" : Product;
                string version = String.IsNullOrEmpty(Version) ? "Unknown" : Version;
                return Sanitize(product) + "_" + Sanitize(version) + "_" + (Locale ?? "zh_CN");
            }
        }

        private static string Sanitize(string text)
        {
            char[] buffer = text.ToCharArray();
            for (int i = 0; i < buffer.Length; i++)
            {
                char c = buffer[i];
                if (c == ' ' || c == '\\' || c == '/' || c == ':' || c == '*' ||
                    c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                {
                    buffer[i] = '_';
                }
            }
            return new string(buffer);
        }
    }

    public sealed class AdobeFontOptions
    {
        public string FontName = "Microsoft YaHei UI";
        public int LargeSize = 16;
        public int MediumSize = 14;
        public int SmallSize = 9;
        public bool ReplacePluginFonts = true;
    }

    public sealed class AdobePatchSummary
    {
        public int StyleModified;
        public int PluginModified;
        public int Added;
        public string NewDigest;
        public string DigestAlgorithm;
        public string DatSha256;
        public string StagingDirectory;
    }

    public sealed class AdobeDiffResult
    {
        public int CurrentCount;
        public int BackupCount;
        public int Modified;
        public int Added;
        public int Deleted;
        public bool Identical { get { return Modified == 0 && Added == 0 && Deleted == 0; } }
    }

    public static class AdobeDictionary
    {
        public const string DefaultLocale = "zh_CN";

        private const string SignatureRelative = @"META-INF\signatures.xml";

        /// <summary>
        /// 字体样式键：允许任意前缀（$$$/AE/、$$$/PR/、$$$/MediaCore/... 等），
        /// 只认结尾的 FontStyle/Large|Medium|Small(+Bold|Italic)。
        /// </summary>
        private static readonly Regex StyleKeyRegex = new Regex(
            @"^\$\$\$/[A-Za-z0-9_/]*FontStyle/(Large|Medium|Small)(Bold|Italic)?$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 插件/对话框字体键：值是 "STSong{{*MSWindows*}}SimSun" 这种纯字体名对，
        /// 用来把插件自绘界面里的宋体一起换掉。
        /// </summary>
        private static readonly Regex PluginValueRegex = new Regex(
            @"^(?<mac>[^\s{}]+)\{\{\*MSWindows\*\}\}(?<win>[^\s{}]+)$",
            RegexOptions.CultureInvariant);

        private static readonly string[] CjkDefaultFonts = new string[]
        {
            "SimSun", "宋体", "STSong", "NSimSun", "SimHei", "黑体",
            "Microsoft YaHei", "Microsoft YaHei UI"
        };

        // ==================== 发现 ====================

        /// <summary>
        /// 扫描本机所有可处理的 Adobe 应用（注册表 + 磁盘兜底），按产品名/版本排序。
        /// </summary>
        public static List<AdobeAppInfo> Discover()
        {
            List<AdobeAppInfo> results = new List<AdobeAppInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> roots = new List<string>();

            CollectRegistryRoots(roots);
            CollectDiskRoots(roots);

            for (int i = 0; i < roots.Count; i++)
            {
                AdobeAppInfo app = BuildFromRoot(roots[i], DefaultLocale);
                if (app == null) continue;
                if (!seen.Add(app.DatFile.ToLowerInvariant())) continue;
                Analyze(app);
                results.Add(app);
            }

            results.Sort(delegate(AdobeAppInfo a, AdobeAppInfo b)
            {
                int byProduct = String.Compare(a.Product, b.Product, StringComparison.OrdinalIgnoreCase);
                if (byProduct != 0) return byProduct;
                return String.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }

        private static void CollectRegistryRoots(List<string> roots)
        {
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };
            for (int v = 0; v < views.Length; v++)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, views[v]))
                    using (RegistryKey adobe = baseKey.OpenSubKey(@"SOFTWARE\Adobe"))
                    {
                        if (adobe == null) continue;
                        foreach (string product in adobe.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey productKey = adobe.OpenSubKey(product))
                                {
                                    if (productKey == null) continue;
                                    foreach (string version in productKey.GetSubKeyNames())
                                    {
                                        using (RegistryKey versionKey = productKey.OpenSubKey(version))
                                        {
                                            if (versionKey == null) continue;
                                            object installPath = versionKey.GetValue("InstallPath");
                                            string path = installPath as string;
                                            if (!String.IsNullOrEmpty(path) && Directory.Exists(path))
                                            {
                                                roots.Add(path);
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void CollectDiskRoots(List<string> roots)
        {
            List<string> bases = new List<string>();
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Adobe"));
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Adobe"));

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string adobe = Path.Combine(drive.RootDirectory.FullName, "Program Files", "Adobe");
                    if (Directory.Exists(adobe)) bases.Add(adobe);
                }
            }
            catch
            {
            }

            for (int i = 0; i < bases.Count; i++)
            {
                try
                {
                    if (!Directory.Exists(bases[i])) continue;
                    foreach (string dir in Directory.GetDirectories(bases[i]))
                    {
                        roots.Add(dir);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 从安装根目录构造应用信息；找不到语言包返回 null。
        /// </summary>
        public static AdobeAppInfo BuildFromRoot(string root, string preferredLocale)
        {
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            string locale;
            string localeDir = FindLocaleDirectory(root, preferredLocale, out locale);
            if (localeDir == null) return null;

            string datFile = FindDatFile(localeDir, locale);
            if (datFile == null) return null;

            AdobeAppInfo app = new AdobeAppInfo();
            app.Root = root;
            app.Locale = locale;
            app.LocaleDir = localeDir;
            app.DatFile = datFile;
            app.DatFileName = Path.GetFileName(datFile);
            app.SignaturesFile = Path.Combine(localeDir, SignatureRelative);
            app.MimetypeFile = Path.Combine(localeDir, "mimetype");
            app.HasSignature = File.Exists(app.SignaturesFile);

            string folder = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            if (String.IsNullOrEmpty(folder)) folder = root;
            // 注册表 InstallPath 常指向 "Support Files"，产品名要从上一级取
            folder = ResolveProductFolderName(folder, root);
            ParseProductName(folder, app);
            return app;
        }

        private static string ResolveProductFolderName(string folder, string path)
        {
            string current = folder;
            string walk = path;
            for (int guard = 0; guard < 4; guard++)
            {
                if (!String.Equals(current, "Support Files", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(current, "Dictionaries", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(current, "Contents", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                DirectoryInfo parent = Directory.GetParent(walk.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == null) break;
                walk = parent.FullName;
                current = parent.Name;
            }
            return current;
        }

        /// <summary>
        /// 手动指定语言包目录（用户直接选 Dictionaries\zh_CN 或 Support Files）。
        /// </summary>
        public static AdobeAppInfo FromLocaleDirectory(string path)
        {
            if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;

            string localeDir = path;
            string locale = DefaultLocale;

            // 允许直接选 Dictionaries（此时取其中的 zh_CN）或 Support Files
            string name = Path.GetFileName(localeDir.TrimEnd(Path.DirectorySeparatorChar));
            if (String.Equals(name, "Dictionaries", StringComparison.OrdinalIgnoreCase))
            {
                string child = FindLocaleDirectory(localeDir, DefaultLocale, out locale);
                if (child == null) return null;
                localeDir = child;
            }
            else if (!File.Exists(Path.Combine(localeDir, SignatureRelative)) &&
                     !HasDatFile(localeDir, out locale))
            {
                string child = FindLocaleDirectory(localeDir, DefaultLocale, out locale);
                if (child == null) return null;
                localeDir = child;
            }

            string datFile = FindDatFile(localeDir, locale);
            if (datFile == null) return null;

            AdobeAppInfo app = new AdobeAppInfo();
            app.Locale = locale;
            app.LocaleDir = localeDir;
            app.DatFile = datFile;
            app.DatFileName = Path.GetFileName(datFile);
            app.SignaturesFile = Path.Combine(localeDir, SignatureRelative);
            app.MimetypeFile = Path.Combine(localeDir, "mimetype");
            app.HasSignature = File.Exists(app.SignaturesFile);
            app.IsCustom = true;

            // 尽力还原安装根目录（...\Support Files 或 ...\<App>）
            DirectoryInfo parent = Directory.GetParent(localeDir);
            string root = null;
            if (parent != null)
            {
                if (String.Equals(parent.Name, "Dictionaries", StringComparison.OrdinalIgnoreCase))
                {
                    root = parent.Parent == null ? null : parent.Parent.FullName;
                }
                else
                {
                    root = parent.FullName;
                }
            }
            if (!String.IsNullOrEmpty(root)) app.Root = root;

            string folder = String.IsNullOrEmpty(root) ? null : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            folder = String.IsNullOrEmpty(folder) ? null : ResolveProductFolderName(folder, root);

            // 只有真正的 Adobe 安装目录才按产品名解析，其余统一显示为自定义语言包
            if (String.IsNullOrEmpty(folder) || folder.IndexOf("Adobe", StringComparison.OrdinalIgnoreCase) < 0)
            {
                app.Product = "自定义语言包";
                app.Version = "未知";
                app.DisplayName = "自定义语言包（自定义路径）";
                app.KeyPrefix = "";
            }
            else
            {
                ParseProductName(folder, app);
                app.DisplayName = app.DisplayName + "（自定义路径）";
            }
            return app;
        }

        private static void ParseProductName(string folder, AdobeAppInfo app)
        {
            string product;
            string ver;
            SplitProductFolder(folder, out product, out ver);
            string text = folder ?? String.Empty;
            if (text.StartsWith("Adobe ", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(6);
            }

            app.Product = String.IsNullOrEmpty(product) ? text : product;
            app.Version = ver;
            app.DisplayName = "Adobe " + app.Product + " " + ver;
            if (String.Equals(ver, "未知", StringComparison.Ordinal)) app.DisplayName = "Adobe " + app.Product;

            string prefix = GuessKeyPrefix(app.Product);
            app.KeyPrefix = prefix;
        }

        /// <summary>
        /// 把 "Adobe After Effects 2026" 这类安装目录名拆成产品名与版本号。
        /// 启动图模块的软件清单也用它，避免两处各写一套版本正则。
        /// 版本形式覆盖 CS4 / CS5.5 / CC / CC 2019 / 2026。
        /// </summary>
        public static void SplitProductFolder(string folder, out string product, out string version)
        {
            string text = folder ?? String.Empty;
            if (text.StartsWith("Adobe ", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(6);
            }

            Match match = Regex.Match(text, @"(CS\d(\.\d)?|CC(\s?20\d{2})?|20\d{2})\s*$", RegexOptions.IgnoreCase);
            product = text;
            version = "未知";
            if (match.Success)
            {
                version = match.Value.Trim();
                product = text.Substring(0, match.Index).Trim();
            }
            if (String.IsNullOrEmpty(product)) product = text;
        }

        private static string GuessKeyPrefix(string product)
        {
            string p = (product ?? String.Empty).ToLowerInvariant();
            if (p.IndexOf("after effects", StringComparison.Ordinal) >= 0) return "AE";
            if (p.IndexOf("premiere", StringComparison.Ordinal) >= 0) return "PR";
            if (p.IndexOf("media encoder", StringComparison.Ordinal) >= 0) return "AME";
            if (p.IndexOf("audition", StringComparison.Ordinal) >= 0) return "AU";
            if (p.IndexOf("prelude", StringComparison.Ordinal) >= 0) return "PL";
            if (p.IndexOf("character animator", StringComparison.Ordinal) >= 0) return "CA";
            return "";
        }

        private static string FindLocaleDirectory(string root, string preferredLocale, out string locale)
        {
            locale = null;
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(root, "Dictionaries"));
            candidates.Add(Path.Combine(root, "Support Files", "Dictionaries"));

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    candidates.Add(Path.Combine(dir, "Dictionaries"));
                    string nested = Path.Combine(dir, "Support Files", "Dictionaries");
                    if (Directory.Exists(nested)) candidates.Add(nested);
                }
            }
            catch
            {
            }

            List<string> localeDirs = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (!Directory.Exists(candidates[i])) continue;
                    foreach (string dir in Directory.GetDirectories(candidates[i]))
                    {
                        if (HasDatFile(dir, out locale)) localeDirs.Add(dir);
                    }
                }
                catch
                {
                }
            }
            if (localeDirs.Count == 0) return null;

            // 优先 zh_CN，其次任意可用的中文/日文语言包
            for (int i = 0; i < localeDirs.Count; i++)
            {
                string name = Path.GetFileName(localeDirs[i]);
                if (String.Equals(name, preferredLocale, StringComparison.OrdinalIgnoreCase))
                {
                    locale = name;
                    return localeDirs[i];
                }
            }
            string fallback = Path.GetFileName(localeDirs[0]);
            locale = fallback;
            return localeDirs[0];
        }

        private static bool HasDatFile(string dir, out string locale)
        {
            locale = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            return FindDatFile(dir, locale) != null;
        }

        /// <summary>
        /// 取目录下体积最大的 &lt;app&gt;_&lt;locale&gt;.dat（同目录可能还有别的资源文件）。
        /// </summary>
        private static string FindDatFile(string localeDir, string locale)
        {
            try
            {
                if (!Directory.Exists(localeDir)) return null;
                string[] files = Directory.GetFiles(localeDir, "*.dat");
                string best = null;
                long bestSize = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    long size = new FileInfo(files[i]).Length;
                    if (size < 16 * 1024) continue;
                    bool localeMatch = !String.IsNullOrEmpty(locale) &&
                                       name.EndsWith("_" + locale, StringComparison.OrdinalIgnoreCase);
                    if (localeMatch && size > bestSize)
                    {
                        best = files[i];
                        bestSize = size;
                    }
                }
                if (best != null) return best;

                // 兜底：不按 locale 后缀匹配，取最大
                for (int i = 0; i < files.Length; i++)
                {
                    long size = new FileInfo(files[i]).Length;
                    if (size > bestSize)
                    {
                        best = files[i];
                        bestSize = size;
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        // ==================== 分析 ====================

        /// <summary>
        /// 读取语言包，统计字体键、平台分隔符、摘要算法与当前字体。
        /// 全部按文件实际内容判断，不依赖版本号，因此 CS~2026 通用。
        /// </summary>
        public static void Analyze(AdobeAppInfo app)
        {
            app.DictionaryFound = false;
            app.StyleKeyCount = 0;
            app.PluginKeyCount = 0;
            app.SupportsPlatformSeparator = false;
            app.CurrentFont = null;
            app.CurrentLarge = 0;
            app.CurrentMedium = 0;
            app.CurrentSmall = 0;

            if (app == null || String.IsNullOrEmpty(app.DatFile) || !File.Exists(app.DatFile))
            {
                app.Message = "语言包文件不存在。";
                return;
            }

            try
            {
                string text = ReadText(app.DatFile);
                app.SupportsPlatformSeparator = text.IndexOf("{{*MSWindows*}}", StringComparison.Ordinal) >= 0;

                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    string key;
                    string value;
                    if (!TryParseLine(line, out key, out value)) continue;

                    Match style = StyleKeyRegex.Match(key);
                    if (style.Success)
                    {
                        app.StyleKeyCount++;
                        string suffix = style.Groups[1].Value + style.Groups[2].Value;
                        string winPart = value;
                        int sep = value.IndexOf("{{*MSWindows*}}", StringComparison.Ordinal);
                        if (sep >= 0) winPart = value.Substring(sep + 15);

                        int size;
                        string style2;
                        string font;
                        if (SplitFontSpec(winPart, out size, out style2, out font))
                        {
                            if (String.Equals(suffix, "Large", StringComparison.Ordinal))
                            {
                                app.CurrentLarge = size;
                                if (String.IsNullOrEmpty(app.CurrentFont)) app.CurrentFont = font;
                            }
                            else if (String.Equals(suffix, "Medium", StringComparison.Ordinal))
                            {
                                app.CurrentMedium = size;
                            }
                            else if (String.Equals(suffix, "Small", StringComparison.Ordinal))
                            {
                                app.CurrentSmall = size;
                            }
                        }
                        continue;
                    }

                    Match plugin = PluginValueRegex.Match(value);
                    if (plugin.Success && IsCjkDefault(plugin.Groups["win"].Value))
                    {
                        app.PluginKeyCount++;
                    }
                }

                app.DigestAlgorithm = DetectDigestAlgorithm(app);
                app.DictionaryFound = app.StyleKeyCount > 0;

                if (!app.DictionaryFound)
                {
                    app.Message = "该语言包里没有找到 FontStyle 字体键，可能不是 Adobe 语言包，或该产品不支持字体替换。";
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("字体键 ").Append(app.StyleKeyCount).Append(" 条");
                    if (app.PluginKeyCount > 0) sb.Append("，插件字体键 ").Append(app.PluginKeyCount).Append(" 条");
                    sb.Append(app.SupportsPlatformSeparator ? "，双平台格式" : "，单平台格式");
                    sb.Append(app.HasSignature ? "，含签名（需同步摘要）" : "，无签名");
                    if (!String.IsNullOrEmpty(app.DigestAlgorithm))
                    {
                        sb.Append("，摘要算法 ").Append(app.DigestAlgorithm.ToUpperInvariant());
                    }
                    app.Message = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                app.Message = "解析语言包失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 从 signatures.xml 判断 .dat 使用的摘要算法（CS 时代用 SHA-1，CC 之后多为 SHA-256）。
        /// </summary>
        private static string DetectDigestAlgorithm(AdobeAppInfo app)
        {
            if (!app.HasSignature) return "";
            try
            {
                string xml = ReadText(app.SignaturesFile);
                string target = app.DatFileName;

                int refIndex = xml.IndexOf("URI=\"" + target + "\"", StringComparison.OrdinalIgnoreCase);
                if (refIndex < 0) refIndex = xml.IndexOf(target, StringComparison.OrdinalIgnoreCase);

                if (refIndex >= 0)
                {
                    int end = Math.Min(xml.Length, refIndex + 2000);
                    string segment = xml.Substring(refIndex, end - refIndex);

                    int methodIndex = segment.IndexOf("<DigestMethod", StringComparison.OrdinalIgnoreCase);
                    if (methodIndex >= 0)
                    {
                        int valueIndex = segment.IndexOf("<DigestValue", StringComparison.OrdinalIgnoreCase);
                        string methodPart = valueIndex > methodIndex
                            ? segment.Substring(methodIndex, valueIndex - methodIndex)
                            : segment.Substring(methodIndex);
                        string lowered = methodPart.ToLowerInvariant();
                        if (lowered.IndexOf("sha512", StringComparison.Ordinal) >= 0) return "sha512";
                        if (lowered.IndexOf("sha256", StringComparison.Ordinal) >= 0) return "sha256";
                        if (lowered.IndexOf("sha1", StringComparison.Ordinal) >= 0) return "sha1";
                    }

                    int digestIndex = segment.IndexOf("<DigestValue>", StringComparison.OrdinalIgnoreCase);
                    if (digestIndex >= 0)
                    {
                        int start = digestIndex + "<DigestValue>".Length;
                        int close = segment.IndexOf('<', start);
                        if (close > start)
                        {
                            int length = close - start;
                            if (length == 28) return "sha1";
                            if (length == 44) return "sha256";
                            if (length == 88) return "sha512";
                        }
                    }
                }
            }
            catch
            {
            }
            return "";
        }

        // ==================== 备份 ====================

        public static string GetBackupDir(AdobeAppInfo app, string backupRoot)
        {
            return Path.Combine(backupRoot, "Adobe", app.BackupKey);
        }

        public static bool EnsureBackup(AdobeAppInfo app, string backupRoot, out string backupDir, out string error)
        {
            error = null;
            backupDir = GetBackupDir(app, backupRoot);
            try
            {
                string marker = Path.Combine(backupDir, app.DatFileName);
                if (Directory.Exists(backupDir) && File.Exists(marker)) return true;

                Directory.CreateDirectory(backupDir);
                CopyDirectory(app.LocaleDir, backupDir);
                if (!File.Exists(marker))
                {
                    error = "备份失败：未生成 " + app.DatFileName + "。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "创建备份失败（请尝试以管理员身份运行）：" + ex.Message;
                return false;
            }
        }

        // ==================== 生成补丁 ====================

        /// <summary>
        /// 以首次备份为基线重建语言包，避免在已打过补丁的文件上反复叠加。
        /// </summary>
        public static AdobePatchSummary Prepare(AdobeAppInfo app, string backupRoot, AdobeFontOptions options, out string error)
        {
            error = null;

            string backupDir;
            if (!EnsureBackup(app, backupRoot, out backupDir, out error)) return null;

            string sourceDat = Path.Combine(backupDir, app.DatFileName);
            if (!File.Exists(sourceDat))
            {
                error = "备份中缺少原始语言包文件。";
                return null;
            }

            string staging = Path.Combine(Path.GetTempPath(), "MultiReplacer-adobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            AdobePatchSummary summary = new AdobePatchSummary();
            summary.StagingDirectory = staging;

            try
            {
                string targetDat = Path.Combine(staging, app.DatFileName);
                int pluginCount;
                summary.StyleModified = ApplyPatchToFile(sourceDat, app, options, targetDat, out pluginCount);
                summary.PluginModified = pluginCount;
                summary.Added = 0;

                string mimetypeSource = Path.Combine(backupDir, "mimetype");
                if (File.Exists(mimetypeSource)) File.Copy(mimetypeSource, Path.Combine(staging, "mimetype"), true);

                string signatureSource = Path.Combine(backupDir, SignatureRelative);
                if (File.Exists(signatureSource))
                {
                    string metaDir = Path.Combine(staging, "META-INF");
                    Directory.CreateDirectory(metaDir);
                    string signatureTarget = Path.Combine(metaDir, "signatures.xml");
                    File.Copy(signatureSource, signatureTarget, true);
                    summary.NewDigest = UpdateSignatureDigest(targetDat, signatureTarget, app.DatFileName, app.DigestAlgorithm, out summary.DigestAlgorithm);
                }

                summary.DatSha256 = HashTools.Sha256File(targetDat);
                return summary;
            }
            catch (Exception ex)
            {
                error = "生成补丁失败：" + ex.Message;
                TryDeleteDirectory(staging);
                return null;
            }
        }

        private static int ApplyPatchToFile(string sourcePath, AdobeAppInfo app, AdobeFontOptions options, string outputPath, out int pluginCount)
        {
            pluginCount = 0;
            byte[] raw = File.ReadAllBytes(sourcePath);
            bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
            bool useCrLf = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            string lf = useCrLf ? "\r\n" : "\n";

            string[] lines = text.Split('\n');
            StringBuilder sb = new StringBuilder(text.Length + 4096);
            int styleModified = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimEnd('\r');
                bool isLastEmpty = (i == lines.Length - 1 && trimmed.Length == 0);

                string key;
                string value;
                if (TryParseLine(trimmed.Trim(), out key, out value))
                {
                    Match style = StyleKeyRegex.Match(key);
                    if (style.Success)
                    {
                        string suffix = style.Groups[1].Value + style.Groups[2].Value;
                        int size = ResolveSize(suffix, options);
                        string styleToken = ResolveStyleToken(style.Groups[2].Value);
                        sb.Append('"').Append(key).Append('=').Append(BuildStyleValue(value, size, styleToken, options.FontName)).Append('"');
                        styleModified++;
                        if (!isLastEmpty) sb.Append(lf);
                        continue;
                    }

                    if (options.ReplacePluginFonts)
                    {
                        Match plugin = PluginValueRegex.Match(value);
                        if (plugin.Success && IsCjkDefault(plugin.Groups["win"].Value))
                        {
                            sb.Append('"').Append(key).Append('=')
                              .Append(plugin.Groups["mac"].Value)
                              .Append("{{*MSWindows*}}")
                              .Append(options.FontName).Append('"');
                            pluginCount++;
                            if (!isLastEmpty) sb.Append(lf);
                            continue;
                        }
                    }
                }

                sb.Append(trimmed);
                if (!isLastEmpty) sb.Append(lf);
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(hasBom));
            return styleModified;
        }

        /// <summary>
        /// 保留原值里的 Mac 部分（各版本 Mac 字体不同，不能写死），只重写 Windows 部分。
        /// 单平台格式（早期版本）直接写 "size style font"。
        /// </summary>
        private static string BuildStyleValue(string original, int size, string styleToken, string fontName)
        {
            string winPart = String.Format(CultureInfo.InvariantCulture, "{0:D2} {1} {2}", size, styleToken, fontName);
            int sep = original.IndexOf("{{*MSWindows*}}", StringComparison.Ordinal);
            if (sep >= 0)
            {
                return original.Substring(0, sep) + "{{*MSWindows*}}" + winPart;
            }
            return winPart;
        }

        private static int ResolveSize(string suffix, AdobeFontOptions options)
        {
            if (suffix.StartsWith("Large", StringComparison.Ordinal)) return options.LargeSize;
            if (suffix.StartsWith("Medium", StringComparison.Ordinal)) return options.MediumSize;
            return options.SmallSize;
        }

        private static string ResolveStyleToken(string suffix)
        {
            if (String.IsNullOrEmpty(suffix)) return "plain";
            if (String.Equals(suffix, "Bold", StringComparison.OrdinalIgnoreCase)) return "bold";
            if (String.Equals(suffix, "Italic", StringComparison.OrdinalIgnoreCase)) return "italic";
            if (String.Equals(suffix, "BoldItalic", StringComparison.OrdinalIgnoreCase)) return "bolditalic";
            return "plain";
        }

        private static bool IsCjkDefault(string font)
        {
            if (String.IsNullOrEmpty(font)) return false;
            for (int i = 0; i < CjkDefaultFonts.Length; i++)
            {
                if (String.Equals(CjkDefaultFonts[i], font, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 按签名文件里记录的算法重算摘要；算法未知时回退 SHA-256。
        /// </summary>
        private static string UpdateSignatureDigest(string datPath, string signaturesPath, string datName, string algorithm, out string usedAlgorithm)
        {
            usedAlgorithm = String.IsNullOrEmpty(algorithm) ? "sha256" : algorithm;
            byte[] datBytes = File.ReadAllBytes(datPath);
            string digest;
            using (HashAlgorithm hasher = CreateHasher(usedAlgorithm))
            {
                digest = Convert.ToBase64String(hasher.ComputeHash(datBytes));
            }

            byte[] xmlBytes = File.ReadAllBytes(signaturesPath);
            bool hasBom = xmlBytes.Length >= 3 && xmlBytes[0] == 0xEF && xmlBytes[1] == 0xBB && xmlBytes[2] == 0xBF;
            string xml = new UTF8Encoding(false).GetString(xmlBytes, hasBom ? 3 : 0, xmlBytes.Length - (hasBom ? 3 : 0));

            string pattern = "(<Reference URI=\"" + Regex.Escape(datName) + "\">.*?<DigestValue>)(.*?)(</DigestValue>)";
            Regex regex = new Regex(pattern, RegexOptions.Singleline);
            if (!regex.IsMatch(xml))
            {
                // 某些版本 URI 带 ./ 前缀或大小写不同，放宽匹配
                regex = new Regex("(<Reference URI=\"[^\"]*" + Regex.Escape(datName) + "\">.*?<DigestValue>)(.*?)(</DigestValue>)",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
            }
            string replaced = regex.Replace(xml, new MatchEvaluator(delegate(Match m)
            {
                return m.Groups[1].Value + digest + m.Groups[3].Value;
            }), 1);

            File.WriteAllText(signaturesPath, replaced, new UTF8Encoding(hasBom));
            return digest;
        }

        private static HashAlgorithm CreateHasher(string algorithm)
        {
            switch ((algorithm ?? "").ToLowerInvariant())
            {
                case "sha1": return SHA1.Create();
                case "sha512": return SHA512.Create();
                default: return SHA256.Create();
            }
        }

        // ==================== 部署与还原 ====================

        public static void Deploy(string stagingDir, AdobeAppInfo app)
        {
            CopyDirectory(stagingDir, app.LocaleDir);
        }

        public static void Restore(string backupDir, AdobeAppInfo app)
        {
            CopyDirectory(backupDir, app.LocaleDir);
        }

        public static AdobeDiffResult Diff(AdobeAppInfo app, string backupRoot)
        {
            AdobeDiffResult result = new AdobeDiffResult();
            string backupDat = Path.Combine(GetBackupDir(app, backupRoot), app.DatFileName);
            if (!File.Exists(backupDat) || !File.Exists(app.DatFile)) return result;

            Dictionary<string, string> current = ParseKeys(app.DatFile);
            Dictionary<string, string> backup = ParseKeys(backupDat);
            result.CurrentCount = current.Count;
            result.BackupCount = backup.Count;

            foreach (KeyValuePair<string, string> pair in current)
            {
                string old;
                if (backup.TryGetValue(pair.Key, out old))
                {
                    if (!String.Equals(old, pair.Value, StringComparison.Ordinal)) result.Modified++;
                }
                else
                {
                    result.Added++;
                }
            }
            foreach (string key in backup.Keys)
            {
                if (!current.ContainsKey(key)) result.Deleted++;
            }
            return result;
        }

        // ==================== 通用工具 ====================

        public static string ReadText(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            return new UTF8Encoding(false).GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
        }

        private static Dictionary<string, string> ParseKeys(string path)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            string text = ReadText(path);
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string key;
                string value;
                if (TryParseLine(lines[i].Trim(), out key, out value)) map[key] = value;
            }
            return map;
        }

        /// <summary>
        /// 语言包一行形如 "$$$/AE/FontStyle/Large=12 plain STSong{{*MSWindows*}}10 plain SimSun"
        /// 外层引号在部分版本里可以省略，两种都要认。
        /// </summary>
        private static bool TryParseLine(string line, out string key, out string value)
        {
            key = null;
            value = null;
            if (String.IsNullOrEmpty(line)) return false;

            string inner = line;
            if (inner[0] == '"')
            {
                if (inner.Length < 6 || inner[inner.Length - 1] != '"') return false;
                inner = inner.Substring(1, inner.Length - 2);
            }
            if (!inner.StartsWith("$$$", StringComparison.Ordinal)) return false;

            int eq = inner.IndexOf('=');
            if (eq <= 0) return false;

            key = inner.Substring(0, eq);
            value = inner.Substring(eq + 1);
            return true;
        }

        /// <summary>
        /// 解析 "10 plain SimSun" → size=10, style=plain, font=SimSun。
        /// 字体名可以带空格，取第二个空格之后的全部内容。
        /// </summary>
        private static bool SplitFontSpec(string spec, out int size, out string style, out string font)
        {
            size = 0;
            style = null;
            font = null;
            if (String.IsNullOrEmpty(spec)) return false;

            string trimmed = spec.Trim();
            int first = trimmed.IndexOf(' ');
            if (first <= 0) return false;
            int second = trimmed.IndexOf(' ', first + 1);
            if (second <= first) return false;

            string sizeText = trimmed.Substring(0, first).Trim();
            int parsed;
            if (!Int32.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;

            size = parsed;
            style = trimmed.Substring(first + 1, second - first - 1).Trim();
            font = trimmed.Substring(second + 1).Trim();
            return font.Length > 0;
        }

        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, GetRelative(source, dir)));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, GetRelative(source, file));
                string parent = Path.GetDirectoryName(target);
                if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(file, target, true);
            }
        }

        private static string GetRelative(string root, string path)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return path.Substring(normalizedRoot.Length);
            return Path.GetFileName(path);
        }

        public static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!String.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
