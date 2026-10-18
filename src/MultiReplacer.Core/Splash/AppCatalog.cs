using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace MultiReplacer.Core
{
    /// <summary>软件家族，决定扫描深度、候选文件类型与提示文案。</summary>
    public static class SplashFamilies
    {
        public const string Adobe = "Adobe";
        public const string Unreal = "Unreal";
        public const string Maya = "Maya";
        public const string ThreeDsMax = "3ds Max";
        public const string JetBrains = "JetBrains";
        public const string VisualStudio = "Visual Studio";
        public const string Live2D = "Live2D";
        public const string Other = "其他";

        public static readonly string[] Order = { Adobe, Unreal, Maya, ThreeDsMax, JetBrains, VisualStudio, Live2D, Other };
    }

    /// <summary>一个可替换启动图的已安装软件。</summary>
    public sealed class SplashAppEntry
    {
        public string Family;
        public string Product;
        public string Version;
        public string DisplayName;
        public string Root;
        public string Executable;
        public string Note;
        public bool AutoDetected;

        public bool IsAdobe
        {
            get { return String.Equals(Family, SplashFamilies.Adobe, StringComparison.Ordinal); }
        }

        public string Id
        {
            get { return Family + "|" + (Root ?? "").ToLowerInvariant(); }
        }

        /// <summary>列表里显示的文字，带上未安装/手动标记。</summary>
        public string Display
        {
            get
            {
                string suffix = AutoDetected ? "" : "（手动）";
                return DisplayName + suffix;
            }
        }

        public override string ToString()
        {
            return Display;
        }
    }

    /// <summary>
    /// 已安装软件枚举。
    ///
    /// 只做"找得到就列出来"这件事：默认路径 + 注册表，两种都失败就返回空列表，
    /// 由界面提示用户手动指定目录。这里不判断某个软件到底有没有启动图，
    /// 那是扫描阶段（AdobeSplashService）的事，避免把两件事耦合在一起。
    /// </summary>
    public static class AppCatalog
    {
        /// <summary>Adobe 默认安装根目录（用户显式要求优先检测这里）。</summary>
        public static string DefaultAdobeRoot
        {
            get { return Path.Combine(ProgramFiles, "Adobe"); }
        }

        private static string ProgramFiles
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); }
        }

        private static string ProgramFilesX86
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); }
        }

        private static string LocalAppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        }

        /// <summary>Adobe 产品 → 主程序相对路径候选。多个候选按顺序取第一个存在的。</summary>
        private static readonly string[][] AdobeExeMap =
        {
            new string[] { "After Effects", @"Support Files\AfterFX.exe", @"AfterFX.exe" },
            new string[] { "Premiere Pro", @"Adobe Premiere Pro.exe" },
            new string[] { "Media Encoder", @"Adobe Media Encoder.exe" },
            new string[] { "Audition", @"Audition.exe" },
            new string[] { "Prelude", @"Prelude.exe" },
            new string[] { "Photoshop", @"Photoshop.exe" },
            new string[] { "Illustrator", @"Support Files\Contents\Windows\Illustrator.exe", @"Illustrator.exe" },
            new string[] { "InDesign", @"InDesign.exe" },
            new string[] { "InCopy", @"InCopy.exe" },
            new string[] { "Animate", @"Animate.exe" },
            new string[] { "Character Animator", @"Character Animator.exe" },
            new string[] { "Dreamweaver", @"Dreamweaver.exe" },
            new string[] { "Lightroom Classic", @"Lightroom.exe" },
            new string[] { "Lightroom", @"lightroom.exe" }
        };

        /// <summary>各产品的启动图存储形态提示（仅作说明，实际以扫描结果为准）。</summary>
        private static string AdobeNote(string product, string version)
        {
            string p = (product ?? String.Empty).ToLowerInvariant();
            string era = VersionEra(version);
            if (p.IndexOf("photoshop", StringComparison.Ordinal) >= 0)
                return era + "：启动图内嵌在 Resources\\PSIcons*Res.dat 容器里，走等长覆盖路线。";
            if (p.IndexOf("indesign", StringComparison.Ordinal) >= 0 || p.IndexOf("incopy", StringComparison.Ordinal) >= 0)
                return era + "：启动图在 Resources 下的 .idrc 容器里，走等长覆盖路线。";
            if (p.IndexOf("illustrator", StringComparison.Ordinal) >= 0)
                return era + "：启动图多为 PE 命名资源，部分版本另有独立 PNG，两条路线都会扫。";
            if (p.IndexOf("after effects", StringComparison.Ordinal) >= 0
                || p.IndexOf("premiere", StringComparison.Ordinal) >= 0
                || p.IndexOf("media encoder", StringComparison.Ordinal) >= 0
                || p.IndexOf("audition", StringComparison.Ordinal) >= 0
                || p.IndexOf("prelude", StringComparison.Ordinal) >= 0
                || p.IndexOf("character animator", StringComparison.Ordinal) >= 0
                || p.IndexOf("animate", StringComparison.Ordinal) >= 0)
                return era + "：AIR 系，启动图是 PE 命名资源（如 AE_SPLASH），按资源写回，长度可变。";
            return era + "：先按 PE 命名资源找，再按容器与独立文件兜底。";
        }

        private static string VersionEra(string version)
        {
            string v = version ?? String.Empty;
            if (v.StartsWith("CS", StringComparison.OrdinalIgnoreCase)) return "CS 时代（" + v + "）";
            if (v.StartsWith("CC", StringComparison.OrdinalIgnoreCase)) return "CC 时代（" + v + "）";
            if (v.Length == 4) return "年度版（" + v + "）";
            return "版本未知";
        }

        // ==================== 入口 ====================

        /// <summary>枚举本机所有支持的软件。找不到任何一个时返回空列表（不抛异常）。</summary>
        public static List<SplashAppEntry> Discover(Action<string> log)
        {
            List<SplashAppEntry> result = new List<SplashAppEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> adobeRoots = new List<string>();
            List<string> adobeAppDirs = new List<string>();
            CollectAdobeRoots(adobeRoots, adobeAppDirs);
            for (int i = 0; i < adobeRoots.Count; i++)
            {
                AddAdobeRoot(adobeRoots[i], result, seen, log);
            }
            // 注册表里登记的是"产品目录"，本身就是应用，不能再往下枚举子目录
            // （否则 ...\Support Files 会被误当成一个软件）
            for (int i = 0; i < adobeAppDirs.Count; i++)
            {
                SplashAppEntry entry = BuildAdobe(adobeAppDirs[i], log);
                if (entry == null) continue;
                if (!seen.Add(entry.Id)) continue;
                result.Add(entry);
            }
            if (log != null)
                log("Adobe 目录：" + (adobeRoots.Count == 0 ? "未找到" : String.Join(" | ", adobeRoots.ToArray()))
                    + "，识别出 " + CountAdobe(result) + " 个应用。");

            AddUnreal(result, seen, log);
            AddAutodesk(result, seen, log);
            AddJetBrains(result, seen, log);
            AddVisualStudio(result, seen, log);
            AddLive2D(result, seen, log);

            result.Sort(CompareApps);
            return result;
        }

        private static int CountAdobe(List<SplashAppEntry> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].IsAdobe) n++;
            return n;
        }

        private static int CompareApps(SplashAppEntry a, SplashAppEntry b)
        {
            int fa = Array.IndexOf(SplashFamilies.Order, a.Family);
            int fb = Array.IndexOf(SplashFamilies.Order, b.Family);
            if (fa < 0) fa = SplashFamilies.Order.Length;
            if (fb < 0) fb = SplashFamilies.Order.Length;
            if (fa != fb) return fa.CompareTo(fb);

            int byProduct = String.Compare(a.Product, b.Product, StringComparison.OrdinalIgnoreCase);
            if (byProduct != 0) return byProduct;
            return String.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase);
        }

        // ==================== Adobe ====================

        /// <summary>按用户要求优先检测 C:\Program Files\Adobe，再补 x86 与注册表里登记的路径。</summary>
        private static void CollectAdobeRoots(List<string> containers, List<string> appDirs)
        {
            AddDirectoryIfExists(containers, DefaultAdobeRoot);
            AddDirectoryIfExists(containers, Path.Combine(ProgramFilesX86, "Adobe"));

            try
            {
                using (RegistryKey adobe = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                                       .OpenSubKey(@"SOFTWARE\Adobe"))
                {
                    if (adobe != null)
                    {
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
                                            string path = versionKey == null ? null : versionKey.GetValue("InstallPath") as string;
                                            if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                                            // InstallPath 常指向 Support Files，往上找到产品目录
                                            string root = WalkUpProductRoot(path);
                                            AddDirectoryIfExists(appDirs, root);
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
            }
            catch
            {
            }
        }

        private static string WalkUpProductRoot(string path)
        {
            string current = path;
            for (int guard = 0; guard < 4; guard++)
            {
                string name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar));
                if (String.IsNullOrEmpty(name)) break;
                if (!String.Equals(name, "Support Files", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(name, "Dictionaries", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(name, "Contents", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(name, "Locales", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                DirectoryInfo parent = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == null) break;
                current = parent.FullName;
            }
            return current;
        }

        private static void AddDirectoryIfExists(List<string> list, string dir)
        {
            if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (String.Equals(list[i], dir, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(dir);
        }

        private static void AddAdobeRoot(string root, List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    SplashAppEntry entry = BuildAdobe(dir, log);
                    if (entry == null) continue;
                    if (!seen.Add(entry.Id)) continue;
                    result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("读取目录失败 " + root + "：" + ex.Message);
            }
        }

        private static SplashAppEntry BuildAdobe(string dir, Action<string> log)
        {
            string folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (String.IsNullOrEmpty(folder)) return null;
            if (IsNonProductFolder(folder)) return null;

            string product;
            string version;
            AdobeDictionary.SplitProductFolder(folder, out product, out version);

            SplashAppEntry entry = new SplashAppEntry();
            entry.Family = SplashFamilies.Adobe;
            entry.Product = product;
            entry.Version = version;
            entry.DisplayName = String.Equals(version, "未知", StringComparison.Ordinal)
                ? "Adobe " + product
                : "Adobe " + product + " " + version;
            entry.Root = dir;
            entry.Executable = FindAdobeExecutable(dir, product);
            entry.Note = AdobeNote(product, version);
            entry.AutoDetected = true;
            return entry;
        }

        /// <summary>Adobe 目录下这些不是"有启动图的桌面产品"，列出来只会干扰选择。</summary>
        private static readonly string[] NonProductFolders =
        {
            "Common", "Sync", "Creative Cloud Experience", "AdobeGCClient",
            "ARM", "Plugins", "Presets", "Support Files", "Dictionaries"
        };

        private static bool IsNonProductFolder(string folder)
        {
            string name = folder ?? String.Empty;
            if (name.StartsWith("Adobe ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
            for (int i = 0; i < NonProductFolders.Length; i++)
            {
                if (String.Equals(name, NonProductFolders[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FindAdobeExecutable(string root, string product)
        {
            string p = product ?? String.Empty;
            for (int i = 0; i < AdobeExeMap.Length; i++)
            {
                if (p.IndexOf(AdobeExeMap[i][0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                for (int k = 1; k < AdobeExeMap[i].Length; k++)
                {
                    string candidate = Path.Combine(root, AdobeExeMap[i][k]);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return FindLargestExecutable(root, 2);
        }

        /// <summary>兜底：在 depth 层内找体积最大的 exe（主程序通常最大）。</summary>
        private static string FindLargestExecutable(string root, int depth)
        {
            string best = null;
            long bestSize = 0;
            try
            {
                CollectExecutables(root, depth, ref best, ref bestSize);
            }
            catch
            {
            }
            return best;
        }

        private static void CollectExecutables(string dir, int depth, ref string best, ref long bestSize)
        {
            if (depth < 0) return;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*.exe"))
                {
                    long size;
                    try { size = new FileInfo(file).Length; } catch { continue; }
                    if (size > bestSize) { bestSize = size; best = file; }
                }
                if (depth == 0) return;
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (String.Equals(name, "Plug-ins", StringComparison.OrdinalIgnoreCase)
                        || String.Equals(name, "Presets", StringComparison.OrdinalIgnoreCase)) continue;
                    CollectExecutables(sub, depth - 1, ref best, ref bestSize);
                }
            }
            catch
            {
            }
        }

        // ==================== 非 Adobe ====================

        private static void AddUnreal(List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            string epic = Path.Combine(ProgramFiles, "Epic Games");
            if (!Directory.Exists(epic)) return;
            try
            {
                foreach (string dir in Directory.GetDirectories(epic))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue;
                    string editor = Path.Combine(dir, @"Engine\Binaries\Win64\UnrealEditor.exe");
                    AddEntry(result, seen, SplashFamilies.Unreal, "Unreal Engine", name.Substring(3),
                             dir, editor,
                             "Unreal 的启动图是 Engine\\Content\\Splash\\Splash.bmp（独立 BMP），走独立文件路线。");
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("读取 Epic Games 目录失败：" + ex.Message);
            }
        }

        private static void AddAutodesk(List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            string autodesk = Path.Combine(ProgramFiles, "Autodesk");
            if (!Directory.Exists(autodesk)) return;
            try
            {
                foreach (string dir in Directory.GetDirectories(autodesk))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("Maya", StringComparison.OrdinalIgnoreCase))
                    {
                        AddEntry(result, seen, SplashFamilies.Maya, "Maya", ExtractTrailingVersion(name),
                                 dir, Path.Combine(dir, @"bin\maya.exe"),
                                 "Maya 启动图是磁盘上的 BMP（splash 目录或 icons 目录），走独立文件路线。");
                    }
                    else if (name.StartsWith("3ds Max", StringComparison.OrdinalIgnoreCase))
                    {
                        AddEntry(result, seen, SplashFamilies.ThreeDsMax, "3ds Max", ExtractTrailingVersion(name),
                                 dir, Path.Combine(dir, "3dsmax.exe"),
                                 "3ds Max 启动图多为独立 BMP/PNG，个别版本在 exe 的位图资源里，两条路线都会扫。");
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("读取 Autodesk 目录失败：" + ex.Message);
            }
        }

        private static void AddEntry(List<SplashAppEntry> result, HashSet<string> seen, string family, string product,
                                     string version, string root, string exe, string note)
        {
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            if (!String.IsNullOrEmpty(exe) && !File.Exists(exe)) exe = FindLargestExecutable(root, 2);

            SplashAppEntry entry = new SplashAppEntry();
            entry.Family = family;
            entry.Product = product;
            entry.Version = version ?? "";
            entry.DisplayName = String.IsNullOrEmpty(entry.Version) ? product : product + " " + entry.Version;
            entry.Root = root;
            entry.Executable = exe;
            entry.Note = note;
            entry.AutoDetected = true;
            if (!seen.Add(entry.Id)) return;
            result.Add(entry);
        }

        private static void AddJetBrains(List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            List<string> roots = new List<string>();
            AddJetBrainsRoot(roots, Path.Combine(ProgramFiles, "JetBrains"));
            AddJetBrainsRoot(roots, Path.Combine(ProgramFilesX86, "JetBrains"));

            // Toolbox 安装位置：%LOCALAPPDATA%\JetBrains\Toolbox\apps\<产品>\<渠道>
            string toolbox = Path.Combine(LocalAppData, @"JetBrains\Toolbox\apps");
            if (Directory.Exists(toolbox))
            {
                try
                {
                    foreach (string product in Directory.GetDirectories(toolbox))
                    {
                        foreach (string channel in Directory.GetDirectories(product))
                        {
                            if (Directory.Exists(Path.Combine(channel, "bin"))) AddDirectoryIfExists(roots, channel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("读取 Toolbox 目录失败：" + ex.Message);
                }
            }

            // 少数发行版直接装到 %LOCALAPPDATA%\Programs\<产品>
            string programs = Path.Combine(LocalAppData, "Programs");
            if (Directory.Exists(programs))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(programs))
                    {
                        if (IsJetBrainsProductName(Path.GetFileName(dir))) AddDirectoryIfExists(roots, dir);
                    }
                }
                catch
                {
                }
            }

            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
                string version;
                string product = SplitJetBrainsName(name, out version);
                AddEntry(result, seen, SplashFamilies.JetBrains, product, version, root,
                         Path.Combine(root, @"bin\" + Path.GetFileNameWithoutExtension(name) + ".exe"),
                         "JetBrains IDE 的启动图在 lib 目录下的 jar 里，走归档条目替换；替换后该 jar 的签名（若有）会失效。");
            }
        }

        private static void AddJetBrainsRoot(List<string> roots, string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (Directory.Exists(Path.Combine(sub, "bin"))) AddDirectoryIfExists(roots, sub);
                }
            }
            catch
            {
            }
        }

        private static readonly string[] JetBrainsProducts =
        {
            "IntelliJ IDEA", "PyCharm", "WebStorm", "PhpStorm", "GoLand", "Rider", "CLion",
            "DataGrip", "RubyMine", "RustRover", "Aqua", "DataSpell", "Android Studio",
            "AppCode", "MPS", "Fleet"
        };

        private static bool IsJetBrainsProductName(string name)
        {
            for (int i = 0; i < JetBrainsProducts.Length; i++)
            {
                if (name.StartsWith(JetBrainsProducts[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>"IntelliJ IDEA 2024.1" → ("IntelliJ IDEA", "2024.1")。</summary>
        private static string SplitJetBrainsName(string name, out string version)
        {
            version = "";
            for (int i = 0; i < JetBrainsProducts.Length; i++)
            {
                if (!name.StartsWith(JetBrainsProducts[i], StringComparison.OrdinalIgnoreCase)) continue;
                version = name.Substring(JetBrainsProducts[i].Length).Trim();
                return JetBrainsProducts[i];
            }
            return name;
        }

        private static void AddVisualStudio(List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            string baseDir = Path.Combine(ProgramFiles, @"Microsoft Visual Studio\2022");
            if (!Directory.Exists(baseDir)) return;
            try
            {
                foreach (string edition in Directory.GetDirectories(baseDir))
                {
                    string devenv = Path.Combine(edition, @"Common7\IDE\devenv.exe");
                    string name = Path.GetFileName(edition);
                    AddEntry(result, seen, SplashFamilies.VisualStudio, "Visual Studio 2022 " + name, "2022",
                             edition, devenv,
                             "VS 2022 的启动图是 MsEnvMui 系 DLL 里的命名资源，按资源写回，长度可变。");
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("读取 Visual Studio 目录失败：" + ex.Message);
            }
        }

        private static void AddLive2D(List<SplashAppEntry> result, HashSet<string> seen, Action<string> log)
        {
            string[] patterns = { "Live2D Cubism *", "Live2D Cubism*" };
            for (int i = 0; i < patterns.Length; i++)
            {
                try
                {
                    string[] dirs = Directory.GetDirectories(ProgramFiles, patterns[i]);
                    for (int k = 0; k < dirs.Length; k++)
                    {
                        string root = dirs[k];
                        string name = Path.GetFileName(root);
                        AddEntry(result, seen, SplashFamilies.Live2D, "Cubism Editor",
                                 ExtractTrailingVersion(name), root,
                                 Path.Combine(root, "CubismEditor.exe"),
                                 "Cubism Editor 是 Java 程序，启动图在 jar 里，走归档条目替换。");
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>从目录名尾部取版本号（"Maya2024" / "UE_5.4"）。</summary>
        private static string ExtractTrailingVersion(string name)
        {
            string text = name ?? String.Empty;
            int end = text.Length;
            int start = end;
            while (start > 0)
            {
                char c = text[start - 1];
                if (Char.IsDigit(c) || c == '.' || c == '_') { start--; continue; }
                break;
            }
            string tail = text.Substring(start).Trim('_', '.');
            return tail.Length > 0 ? tail : "";
        }

        // ==================== 手动指定目录 ====================

        /// <summary>用户手动选目录时，按目录特征判断属于哪个家族。</summary>
        public static SplashAppEntry FromDirectory(string dir, Action<string> log)
        {
            if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            SplashAppEntry entry = new SplashAppEntry();
            entry.Root = dir;
            entry.AutoDetected = false;
            entry.Executable = FindLargestExecutable(dir, 2);

            string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (String.IsNullOrEmpty(name)) name = dir;

            if (File.Exists(Path.Combine(dir, "CubismEditor.exe")))
            {
                entry.Family = SplashFamilies.Live2D;
                entry.Product = "Cubism Editor";
                entry.Version = ExtractTrailingVersion(name);
            }
            else if (File.Exists(Path.Combine(dir, @"Common7\IDE\devenv.exe")))
            {
                entry.Family = SplashFamilies.VisualStudio;
                entry.Product = "Visual Studio";
                entry.Version = "2022";
            }
            else if (File.Exists(Path.Combine(dir, @"Engine\Binaries\Win64\UnrealEditor.exe")))
            {
                entry.Family = SplashFamilies.Unreal;
                entry.Product = "Unreal Engine";
                entry.Version = ExtractTrailingVersion(name);
            }
            else if (File.Exists(Path.Combine(dir, @"bin\maya.exe")))
            {
                entry.Family = SplashFamilies.Maya;
                entry.Product = "Maya";
                entry.Version = ExtractTrailingVersion(name);
            }
            else if (File.Exists(Path.Combine(dir, "3dsmax.exe")))
            {
                entry.Family = SplashFamilies.ThreeDsMax;
                entry.Product = "3ds Max";
                entry.Version = ExtractTrailingVersion(name);
            }
            else if (name.StartsWith("Adobe ", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(dir, "Dictionaries")))
            {
                string product;
                string version;
                AdobeDictionary.SplitProductFolder(name, out product, out version);
                entry.Family = SplashFamilies.Adobe;
                entry.Product = product;
                entry.Version = version;
                entry.Note = AdobeNote(product, version);
            }
            else if (Directory.Exists(Path.Combine(dir, "lib")) && IsJetBrainsProductName(name))
            {
                string version;
                string product = SplitJetBrainsName(name, out version);
                entry.Family = SplashFamilies.JetBrains;
                entry.Product = product;
                entry.Version = version;
            }
            else
            {
                entry.Family = SplashFamilies.Other;
                entry.Product = name;
                entry.Version = "";
                entry.Note = "未识别的软件，按通用规则扫描（PE 资源 / 独立图片 / 容器 / 归档）。";
            }

            entry.DisplayName = String.IsNullOrEmpty(entry.Version)
                ? entry.Product
                : entry.Product + " " + entry.Version;
            if (String.IsNullOrEmpty(entry.Note)) entry.Note = AdobeNote(entry.Product, entry.Version);
            if (log != null) log("手动目录识别为 " + entry.Family + "：" + entry.DisplayName);
            return entry;
        }
    }
}
