using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MultiReplacer.App.Views;
using MultiReplacer.Core;
using Windows.Graphics;

namespace MultiReplacer.App
{
    public sealed partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>目标窗口尺寸（DIP）。两栏布局在 1240 宽下最舒展。</summary>
        private const double DesiredWidth = 1240;
        private const double DesiredHeight = 820;

        /// <summary>窗口最小尺寸（DIP），保证左右两栏不会被压到互相遮挡。</summary>
        private const double MinWidth = 940;
        private const double MinHeight = 640;

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            Title = AppPaths.ProductName;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            TryApplyBackdrop();
            ResizeToWorkArea();

            // 构造阶段 XamlRoot 还没准备好，RasterizationScale 取不到真实值，
            // 首次激活后再校正一次，避免高分屏下窗口偏小、两栏被裁。
            Activated += OnFirstActivated;

            ElevationHint.Text = ElevationService.IsElevated ? "管理员模式" : "标准权限";
            VersionText.Text = "v1.0.0";
            if (!String.IsNullOrEmpty(App.Settings.LoadWarning)) Status("配置提示：" + App.Settings.LoadWarning);

            RestoreLastPage();
            if (ContentFrame.Content == null) Navigate("splash");
            AppWindow.Closing += OnClosing;
        }

        /// <summary>启动时回到上次停留的页面（配置里的 LastPage）。</summary>
        private void RestoreLastPage()
        {
            string tag = App.Settings.LastPage;
            if (String.IsNullOrEmpty(tag)) return;
            if (String.Equals(tag, "splash", StringComparison.OrdinalIgnoreCase)) return;

            foreach (object item in Nav.MenuItems)
            {
                NavigationViewItem nvi = item as NavigationViewItem;
                if (nvi != null && String.Equals(nvi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    Nav.SelectedItem = nvi;
                    return;
                }
            }
            foreach (object item in Nav.FooterMenuItems)
            {
                NavigationViewItem nvi = item as NavigationViewItem;
                if (nvi != null && String.Equals(nvi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    Nav.SelectedItem = nvi;
                    return;
                }
            }
        }

        private void TryApplyBackdrop()
        {
            try
            {
                if (MicaController.IsSupported())
                {
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                }
                else if (DesktopAcrylicController.IsSupported())
                {
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                }
            }
            catch
            {
            }
        }

        private bool sizedOnce;

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            if (sizedOnce) return;
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            sizedOnce = true;

            // 构造阶段窗口还没显示，DPI 可能不是最终值；激活后按真实 DPI 校正一次最小尺寸。
            // 这里只调最小尺寸，不再重新 Resize，避免窗口在用户眼前跳一下。
            ApplyMinimumSize(CurrentScale());
        }

        private void ResizeToWorkArea()
        {
            try
            {
                DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

                // 窗口可能落在较小的副屏上，此时按那块屏的可用区域算，两栏会挤到互相遮挡。
                // 小于 1100×700 时改用主显示器。
                if (area.WorkArea.Width < 1100 || area.WorkArea.Height < 700)
                {
                    DisplayArea primary = DisplayArea.Primary;
                    if (primary != null && primary.WorkArea.Width > area.WorkArea.Width) area = primary;
                }

                // AppWindow.Resize 用的是物理像素，而界面按 DIP 布局。
                // 直接问窗口的 DPI（GetDpiForWindow）而不是 XamlRoot.RasterizationScale：
                // 后者在构造阶段和首次布局前还取不到真实值，这正是"启动时窗口偏小、
                // 页面被裁掉一截"的根因。
                double scale = CurrentScale();

                int wantWidth = (int)(DesiredWidth * scale);
                int wantHeight = (int)(DesiredHeight * scale);
                int width = Math.Min(wantWidth, (int)(area.WorkArea.Width * 0.94));
                int height = Math.Min(wantHeight, (int)(area.WorkArea.Height * 0.94));

                // 先设最小尺寸，避免用户在页面布局完成前把窗口拖到装不下两栏
                ApplyMinimumSize(scale);

                AppWindow.Resize(new SizeInt32(width, height));
                int x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
                int y = area.WorkArea.Y + (area.WorkArea.Height - height) / 2;
                AppWindow.Move(new PointInt32(x, y));
            }
            catch
            {
            }
        }

        private double CurrentScale()
        {
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch
            {
            }
            // 兜底：XamlRoot 已就绪时用它的值
            try
            {
                if (Content != null && Content.XamlRoot != null && Content.XamlRoot.RasterizationScale > 0)
                    return Content.XamlRoot.RasterizationScale;
            }
            catch
            {
            }
            return 1.0;
        }

        private void ApplyMinimumSize(double scale)
        {
            minWidthPx = (int)(MinWidth * scale);
            minHeightPx = (int)(MinHeight * scale);

            // WinAppSDK 1.6 的 OverlappedPresenter 没有 PreferredMinimumWidth，
            // 这里改成"尺寸变化后钳回下限"的响应式做法：纯托管代码，不挂 WndProc，
            // 不会因为回调生命周期问题把进程搞崩。
            if (clampHooked) return;
            clampHooked = true;
            AppWindow.Changed += OnAppWindowChanged;
        }

        private int minWidthPx;
        private int minHeightPx;
        private bool clampHooked;
        private bool clamping;

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidSizeChange || clamping) return;
            if (minWidthPx <= 0 || minHeightPx <= 0) return;

            SizeInt32 size = sender.Size;
            if (size.Width >= minWidthPx && size.Height >= minHeightPx) return;

            clamping = true;
            try
            {
                sender.Resize(new SizeInt32(Math.Max(size.Width, minWidthPx), Math.Max(size.Height, minHeightPx)));
            }
            catch
            {
            }
            finally
            {
                clamping = false;
            }
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            NavigationViewItem item = args.SelectedItem as NavigationViewItem;
            if (item == null) return;
            Navigate(item.Tag as string);
        }

        private void Navigate(string tag)
        {
            Type pageType;
            switch (tag)
            {
                case "adobesplash": pageType = typeof(AdobeSplashPage); break;
                case "font": pageType = typeof(FontPage); break;
                case "adobe": pageType = typeof(AdobePage); break;
                case "about": pageType = typeof(AboutPage); break;
                default: pageType = typeof(SplashPage); break;
            }
            AppLog.Info("导航 → " + tag);
            if (ContentFrame.CurrentSourcePageType == pageType) return;
            ContentFrame.Navigate(pageType);

            if (!String.Equals(App.Settings.LastPage, tag, StringComparison.Ordinal))
            {
                App.Settings.LastPage = tag;
                App.Settings.Save(out string _);
            }
        }

        public void Status(string message)
        {
            StatusText.Text = message;
            AppLog.Info(message);
        }

        private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            try
            {
                App.Settings.Save(out string _);
            }
            catch
            {
            }
        }
    }
}
