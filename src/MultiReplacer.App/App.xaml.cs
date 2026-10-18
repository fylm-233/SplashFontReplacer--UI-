using System;
using Microsoft.UI.Xaml;
using MultiReplacer.Core;

namespace MultiReplacer.App
{
    public partial class App : Application
    {
        public static AppSettings Settings { get; private set; }
        public static MainWindow Window { get; private set; }

        public App()
        {
            InitializeComponent();
            Settings = AppSettings.Load();
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Window = new MainWindow();
            Window.Activate();
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                AppLog.Error("未处理异常", e.Exception);
            }
            catch
            {
            }
        }
    }
}
