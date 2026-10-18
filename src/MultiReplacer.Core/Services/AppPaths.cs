using System;
using System.IO;

namespace MultiReplacer.Core
{
    /// <summary>
    /// 单文件发布后程序目录并不稳定（可能被解压到临时目录），
    /// 因此配置与备份统一放在用户漫游/本地目录下。
    /// </summary>
    public static class AppPaths
    {
        public const string ProductName = "多合一启动图字体UI替换器";
        public const string ShortName = "MultiReplacer";

        public static string RoamingRoot
        {
            get
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ShortName);
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public static string LocalRoot
        {
            get
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ShortName);
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(RoamingRoot, "settings.json"); }
        }

        public static string LogDirectory
        {
            get
            {
                string dir = Path.Combine(LocalRoot, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string BackupRoot
        {
            get
            {
                string dir = Path.Combine(LocalRoot, "backup");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ResolveBackupDir
        {
            get
            {
                string dir = Path.Combine(BackupRoot, "DaVinciResolve");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}
