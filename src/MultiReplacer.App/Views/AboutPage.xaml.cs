using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MultiReplacer.Core;

namespace MultiReplacer.App.Views
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            TxtVersion.Text = "版本 1.0.0 · " + AppPaths.ProductName;
            TxtSettingsPath.Text = AppPaths.SettingsFile;
            TxtBackupPath.Text = AppPaths.BackupRoot;
            TxtLogPath.Text = AppPaths.LogDirectory;
        }

        private void OnOpenRoaming(object sender, RoutedEventArgs e)
        {
            Open(AppPaths.RoamingRoot);
        }

        private void OnOpenBackup(object sender, RoutedEventArgs e)
        {
            Open(AppPaths.BackupRoot);
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            Open(AppPaths.LogDirectory);
        }

        private void Open(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + path + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MainWindow.Instance.Status("打开目录失败：" + ex.Message);
            }
        }
    }
}
