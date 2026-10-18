using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiReplacer.Core
{
    public sealed class AppSettings
    {
        public string ResolveExePath { get; set; } = "";
        public string QWindowsPath { get; set; } = "";
        public string ResolveFontName { get; set; } = QtFontTable.DefaultFont;

        public string AeSupportFiles { get; set; } = "";
        public string AeFontName { get; set; } = "Microsoft YaHei UI";
        public int AeLargeSize { get; set; } = 16;
        public int AeMediumSize { get; set; } = 14;
        public int AeSmallSize { get; set; } = 9;

        // Adobe 系列（AE / PR / ME / AU 等）通用字体替换
        public string AdobeLocaleDir { get; set; } = "";
        public string AdobeFontName { get; set; } = "Microsoft YaHei UI";
        public int AdobeLargeSize { get; set; } = 16;
        public int AdobeMediumSize { get; set; } = 14;
        public int AdobeSmallSize { get; set; } = 9;
        public bool AdobeReplacePluginFonts { get; set; } = true;

        public bool IncludeLogoAssets { get; set; }
        public bool PatchIdenticalCopies { get; set; } = true;
        public bool ClearAuthenticode { get; set; }
        public int MinSide { get; set; } = 300;

        // ---- Adobe 启动图 ----
        public string AdobeSplashTarget { get; set; } = "";
        public string AdobeSplashAppId { get; set; } = "";
        public bool AdobeSplashVariants { get; set; } = true;
        public bool AdobeSplashShowAll { get; set; }
        public int AdobeSplashFitMode { get; set; }
        public string Theme { get; set; } = "Dark";
        public string LastPage { get; set; } = "splash";

        [JsonIgnore]
        public string LoadWarning { get; set; }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppSettings Load()
        {
            try
            {
                string path = AppPaths.SettingsFile;
                if (!File.Exists(path)) return new AppSettings();
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (String.IsNullOrWhiteSpace(json)) return new AppSettings();
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                return settings ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppSettings fallback = new AppSettings();
                fallback.LoadWarning = "读取配置失败，已使用默认值：" + ex.Message;
                return fallback;
            }
        }

        public bool Save(out string error)
        {
            error = null;
            try
            {
                string json = JsonSerializer.Serialize(this, Options);
                string temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                File.Move(temp, AppPaths.SettingsFile);
                return true;
            }
            catch (Exception ex)
            {
                error = "保存配置失败：" + ex.Message;
                return false;
            }
        }
    }
}
