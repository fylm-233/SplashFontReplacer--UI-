using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MultiReplacer.Core
{
    /// <summary>
    /// 通过注册表枚举已安装字体族名。
    /// 之所以不用 System.Drawing，是为了避免额外依赖，保持单文件发布干净。
    /// </summary>
    public static class FontInventory
    {
        private static readonly object CacheLock = new object();
        private static List<string> cached;
        private static DateTime cachedAt = DateTime.MinValue;

        public static List<string> ListInstalled()
        {
            lock (CacheLock)
            {
                if (cached != null && (DateTime.Now - cachedAt).TotalSeconds < 30) return cached;
                cached = LoadFromRegistry();
                cachedAt = DateTime.Now;
                return cached;
            }
        }

        public static bool IsInstalled(string familyName)
        {
            if (String.IsNullOrEmpty(familyName)) return false;
            List<string> fonts = ListInstalled();
            for (int i = 0; i < fonts.Count; i++)
            {
                if (String.Equals(fonts[i], familyName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static List<string> LoadFromRegistry()
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] paths = new string[]
            {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Fonts"
            };

            RegistryHive[] hives = new RegistryHive[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

            foreach (RegistryHive hive in hives)
            {
                foreach (string path in paths)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                        using (RegistryKey key = baseKey.OpenSubKey(path))
                        {
                            if (key == null) continue;
                            foreach (string valueName in key.GetValueNames())
                            {
                                string family = Normalize(valueName);
                                if (String.IsNullOrEmpty(family)) continue;
                                if (seen.Add(family)) result.Add(family);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string Normalize(string valueName)
        {
            if (String.IsNullOrEmpty(valueName)) return null;
            string name = valueName.Trim();

            // 注册表项形如 "Microsoft YaHei UI (TrueType)" / "微软雅黑 & Microsoft YaHei (TrueType)"
            name = Regex.Replace(name, @"\s*\((TrueType|OpenType|PostScript|All res)\)\s*$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\(.*?\)\s*$", "");

            int amp = name.LastIndexOf('&');
            if (amp >= 0 && amp + 1 < name.Length) name = name.Substring(amp + 1);

            name = name.Trim();
            if (name.Length == 0) return null;
            if (name.IndexOf('.') >= 0) return null;
            return name;
        }
    }
}
