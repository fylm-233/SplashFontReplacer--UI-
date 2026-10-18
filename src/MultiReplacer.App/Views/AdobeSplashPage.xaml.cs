using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MultiReplacer.App.Models;
using MultiReplacer.App.Services;
using MultiReplacer.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MultiReplacer.App.Views
{
    public sealed partial class AdobeSplashPage : Page
    {
        private readonly ObservableCollection<SplashRow> rows = new ObservableCollection<SplashRow>();
        private readonly List<SplashAppEntry> apps = new List<SplashAppEntry>();
        private List<SplashCandidate> allCandidates = new List<SplashCandidate>();
        private SplashCandidate selected;
        private SplashAppEntry currentApp;
        private StorageFile replacementFile;
        private byte[] originalBytes;
        private byte[] previewBytes;
        private bool busy;
        private bool ready;
        private bool loadingApps;
        /// <summary>
        /// 选中项的世代号。异步读图/渲染期间用户可能又换了选中项，
        /// 回到 UI 线程时若世代号已变就丢弃这次结果，避免"图是 A、文案是 B"。
        /// </summary>
        private int selectionGeneration;

        private static readonly string[] RunningProcesses =
        {
            "AfterFX", "AfterFX.com", "Adobe Premiere Pro", "Adobe Media Encoder", "Audition",
            "Animate", "Character Animator", "Lightroom", "Photoshop", "Illustrator",
            "InDesign", "InCopy", "Adobe Premiere Rush",
            "UnrealEditor", "maya", "3dsmax", "devenv", "CubismEditor",
            "idea64", "pycharm64", "webstorm64", "phpstorm64", "goland64", "rider64", "clion64",
            "datagrip64", "rubymine64", "rustrover64", "studio64"
        };

        public AdobeSplashPage()
        {
            InitializeComponent();
            LvCandidates.ItemsSource = rows;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppSettings s = App.Settings;
            TxtTarget.Text = s.AdobeSplashTarget;
            ChkVariants.IsChecked = s.AdobeSplashVariants;
            ChkShowAll.IsOn = s.AdobeSplashShowAll;
            CboFit.SelectedIndex = s.AdobeSplashFitMode >= 0 && s.AdobeSplashFitMode <= 1 ? s.AdobeSplashFitMode : 0;

            // XAML 解析阶段属性赋值会触发 Toggled / SelectionChanged，此时其它控件还没构造好
            ready = true;
            UpdateButtons();
            Log("就绪。选择上方软件列表，或点[浏览目录…]手动指定安装目录。");

            await RefreshAppsAsync(s.AdobeSplashAppId);
        }

        private string TargetPath
        {
            get { return (TxtTarget.Text ?? String.Empty).Trim().Trim('"'); }
        }

        // ---------------- 软件列表 ----------------

        private async Task RefreshAppsAsync(string preferId)
        {
            if (loadingApps) return;
            loadingApps = true;
            BtnRefresh.IsEnabled = false;
            try
            {
                List<SplashAppEntry> found = null;
                List<string> logs = new List<string>();
                await Task.Run(delegate
                {
                    found = AppCatalog.Discover(delegate(string text) { lock (logs) logs.Add(text); });
                });
                lock (logs)
                {
                    for (int i = 0; i < logs.Count; i++) Log(logs[i]);
                }

                apps.Clear();
                if (found != null) apps.AddRange(found);

                CboApp.ItemsSource = null;
                CboApp.ItemsSource = apps;

                if (apps.Count == 0)
                {
                    // 默认路径没扫到任何软件：把入口明确交给用户
                    HeaderInfo.Severity = InfoBarSeverity.Warning;
                    HeaderInfo.Title = "未在默认路径下检测到已安装的软件";
                    HeaderInfo.Message = "已检查 " + AppCatalog.DefaultAdobeRoot
                                         + " 等常见位置。请点[浏览目录…]手动选择安装目录，或[浏览 exe…]直接选主程序。";
                    HeaderInfo.IsOpen = true;
                    Log("未检测到软件。可手动指定目录。");
                    UpdateButtons();
                    return;
                }

                int index = 0;
                if (!String.IsNullOrEmpty(preferId))
                {
                    for (int i = 0; i < apps.Count; i++)
                    {
                        if (String.Equals(apps[i].Id, preferId, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                    }
                }
                CboApp.SelectedIndex = index;
            }
            finally
            {
                loadingApps = false;
                BtnRefresh.IsEnabled = true;
                UpdateButtons();
            }
        }

        private async void OnRefreshApps(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            await RefreshAppsAsync(currentApp == null ? App.Settings.AdobeSplashAppId : currentApp.Id);
        }

        private async void OnAppChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ready) return;
            SplashAppEntry app = CboApp.SelectedItem as SplashAppEntry;
            if (app == null) return;

            currentApp = app;
            TxtTarget.Text = app.Root;
            App.Settings.AdobeSplashTarget = app.Root;
            App.Settings.AdobeSplashAppId = app.Id;
            App.Settings.Save(out string _);

            HeaderInfo.Severity = InfoBarSeverity.Informational;
            HeaderInfo.Title = app.DisplayName;
            HeaderInfo.Message = String.IsNullOrEmpty(app.Note) ? app.Root : app.Note;
            HeaderInfo.IsOpen = true;
            Log("已选择：" + app.DisplayName + "  →  " + app.Root);

            UpdateButtons();
            await ScanAsync();
        }

        private async void OnPickFolder(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            string dir = await PickerHelper.PickFolderAsync("选择软件安装目录");
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (String.IsNullOrEmpty(dir)) return;

            SplashAppEntry manual = AppCatalog.FromDirectory(dir, delegate(string text) { Log(text); });
            if (manual == null)
            {
                await ShowMessage("目录不可用", "无法读取该目录：" + dir);
                return;
            }

            // 手动目录插到列表末尾，避免打乱自动检测结果的顺序
            for (int i = apps.Count - 1; i >= 0; i--)
            {
                if (String.Equals(apps[i].Root, manual.Root, StringComparison.OrdinalIgnoreCase)) apps.RemoveAt(i);
            }
            apps.Add(manual);
            CboApp.ItemsSource = null;
            CboApp.ItemsSource = apps;
            CboApp.SelectedItem = manual;
        }

        // ---------------- 目标选择 ----------------

        private async void OnBrowse(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            string path = null;
            StorageFile file = await PickerHelper.PickOpenFileAsync("选择主程序",
                "程序或快捷方式", new string[] { ".exe", ".lnk" }, TargetPath);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file != null) path = file.Path;
            if (String.IsNullOrEmpty(path)) return;

            currentApp = null;
            TxtTarget.Text = path;
            App.Settings.AdobeSplashTarget = path;
            App.Settings.Save(out string _);
            await ScanAsync();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "松开以识别目标程序";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            for (int i = 0; i < items.Count; i++)
            {
                string path = null;
                StorageFile file = items[i] as StorageFile;
                if (file != null) path = file.Path;
                else
                {
                    StorageFolder folder = items[i] as StorageFolder;
                    if (folder != null) path = folder.Path;
                }
                if (String.IsNullOrEmpty(path)) continue;

                if (Directory.Exists(path))
                {
                    SplashAppEntry manual = AppCatalog.FromDirectory(path, delegate(string text) { Log(text); });
                    if (manual == null) continue;
                    for (int k = apps.Count - 1; k >= 0; k--)
                    {
                        if (String.Equals(apps[k].Root, manual.Root, StringComparison.OrdinalIgnoreCase)) apps.RemoveAt(k);
                    }
                    apps.Add(manual);
                    CboApp.ItemsSource = null;
                    CboApp.ItemsSource = apps;
                    CboApp.SelectedItem = manual;
                    return;
                }

                string ext = Path.GetExtension(path);
                if (!String.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)) continue;

                currentApp = null;
                TxtTarget.Text = path;
                App.Settings.AdobeSplashTarget = path;
                App.Settings.Save(out string _);
                await ScanAsync();
                return;
            }
            Log("拖入的内容里没有可识别的 exe、快捷方式或目录。");
        }

        // ---------------- 扫描 ----------------

        private async void OnScan(object sender, RoutedEventArgs e)
        {
            await ScanAsync();
        }

        private async Task ScanAsync()
        {
            if (busy) return;
            string path = TargetPath;
            if (String.IsNullOrEmpty(path))
            {
                await ShowMessage("还没有目标", "请先从上方选择一个软件，或手动指定安装目录 / 主程序。");
                return;
            }

            SetBusy(true, "正在扫描启动图资源…");
            Log("开始扫描：" + path);
            try
            {
                bool showAll = ChkShowAll.IsOn;
                SplashAppEntry app = currentApp;
                List<SplashCandidate> found = null;
                string error = null;
                List<string> logs = new List<string>();
                await Task.Run(delegate
                {
                    if (Directory.Exists(path))
                    {
                        // 目录：优先用软件识别结果（会带上该家族的已知子目录）
                        SplashAppEntry target = app;
                        if (target == null || !String.Equals(target.Root, path, StringComparison.OrdinalIgnoreCase))
                        {
                            target = AppCatalog.FromDirectory(path, null);
                        }
                        if (target != null)
                            found = AdobeSplashService.DiscoverForApp(target, showAll, delegate(string s) { lock (logs) logs.Add(s); }, out error);
                        else
                            found = AdobeSplashService.DiscoverRoot(path, null, showAll, delegate(string s) { lock (logs) logs.Add(s); }, out error);
                    }
                    else
                    {
                        found = AdobeSplashService.Discover(path, showAll, delegate(string s) { lock (logs) logs.Add(s); }, out error);
                    }
                });
                lock (logs)
                {
                    for (int i = 0; i < logs.Count; i++) Log(logs[i]);
                }

                if (found == null)
                {
                    HeaderInfo.Severity = InfoBarSeverity.Error;
                    HeaderInfo.Title = "扫描失败";
                    HeaderInfo.Message = error ?? "未知错误";
                    HeaderInfo.IsOpen = true;
                    Log("扫描失败：" + error);
                    return;
                }

                allCandidates = found;
                selected = null;
                originalBytes = null;
                ImgOriginal.Source = null;
                TxtOriginalInfo.Text = "未选择资源";
                ApplyFilter();

                if (found.Count == 0)
                {
                    HeaderInfo.Severity = InfoBarSeverity.Warning;
                    HeaderInfo.Title = "没有找到启动图候选";
                    HeaderInfo.Message = "可以勾选[显示全部]再试（会列出所有图片资源，包括图标），或确认该软件确实把启动图放在此目录下。";
                    HeaderInfo.IsOpen = true;
                }
                else
                {
                    HeaderInfo.Severity = InfoBarSeverity.Success;
                    HeaderInfo.Title = String.Format(CultureInfo.InvariantCulture,
                        "找到 {0} 个候选 · 首选 {1} · {2}×{3}",
                        found.Count, found[0].NameName, found[0].Width, found[0].Height);
                    HeaderInfo.Message = found[0].Location;
                    HeaderInfo.IsOpen = true;
                    // 按对象而不是索引选中：列表可能还没实现完，索引会落空或落到相邻行
                    SplashCandidate top = found[0];
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        for (int i = 0; i < rows.Count; i++)
                        {
                            if (rows[i].Candidate == top)
                            {
                                LvCandidates.SelectedItem = rows[i];
                                LvCandidates.ScrollIntoView(rows[i]);
                                break;
                            }
                        }
                    });
                }
                UpdateButtons();
            }
            catch (Exception ex)
            {
                HeaderInfo.Severity = InfoBarSeverity.Error;
                HeaderInfo.Title = "扫描失败";
                HeaderInfo.Message = ex.Message;
                HeaderInfo.IsOpen = true;
                Log("扫描失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
            }
        }

        private void OnShowAllToggled(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            App.Settings.AdobeSplashShowAll = ChkShowAll.IsOn;
            App.Settings.Save(out string _);
            if (allCandidates.Count > 0) _ = ScanAsync();
        }

        // ---------------- 列表 ----------------

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!ready) return;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (!ready) return;
            string key = (TxtFilter.Text ?? String.Empty).Trim();
            SplashCandidate keep = selected;

            rows.Clear();
            for (int i = 0; i < allCandidates.Count; i++)
            {
                SplashCandidate c = allCandidates[i];
                if (key.Length > 0)
                {
                    bool hit = c.NameName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                               || c.RelativeFile.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                rows.Add(SplashRow.From(c));
            }

            if (keep != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Candidate == keep) { LvCandidates.SelectedIndex = i; break; }
                }
            }
        }

        private async void OnCandidateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ready) return;
            int token = ++selectionGeneration;
            SplashRow row = LvCandidates.SelectedItem as SplashRow;
            selected = row == null ? null : row.Candidate;
            originalBytes = null;
            ImgOriginal.Source = null;
            TxtOriginalInfo.Text = "未选择资源";

            if (selected != null)
            {
                string error = null;
                byte[] data = null;
                SplashCandidate local = selected;
                await Task.Run(delegate { data = AdobeSplashService.Extract(local, out error); });
                if (token != selectionGeneration) return;   // 期间又换了选中项，丢弃
                if (data != null)
                {
                    originalBytes = data;
                    ImgOriginal.Source = await ToBitmapAsync(data);
                    if (token != selectionGeneration) return;
                    TxtOriginalInfo.Text = String.Format(CultureInfo.InvariantCulture,
                        "{0}  ·  {1}  ·  {2}×{3}  ·  {4:N0} 字节  ·  {5}",
                        local.NameName, ImageProbe.FormatText(local.Format),
                        local.Width, local.Height, local.Size, local.Location);
                }
                else
                {
                    TxtOriginalInfo.Text = "读取失败：" + error;
                }
                UpdateBackupHint();
            }
            await UpdatePreviewAsync(token);
            UpdateButtons();
        }

        private async Task UpdatePreviewAsync(int token)
        {
            previewBytes = null;
            ImgNew.Source = null;
            if (replacementFile == null || selected == null)
            {
                TxtNewInfo.Text = replacementFile == null ? "尚未选择替换图" : "未选择资源";
                return;
            }

            try
            {
                SplashCandidate local = selected;
                byte[] rendered = await RenderForAsync(replacementFile, local, CurrentFitMode);
                if (token != selectionGeneration) return;
                previewBytes = rendered;
                ImgNew.Source = await ToBitmapAsync(rendered);
                if (token != selectionGeneration) return;

                string text = String.Format(CultureInfo.InvariantCulture,
                    "{0}  ·  输出 {1}×{2} {3}  ·  {4:N0} 字节",
                    replacementFile.Name, local.Width, local.Height,
                    ImageProbe.FormatText(local.Format), rendered.Length);

                if (local.Kind == SplashStorageKind.Embedded)
                {
                    if (rendered.Length > local.Size)
                        text += "  ·  超出原图 " + local.Size.ToString("N0", CultureInfo.InvariantCulture) + " 字节，将自动压缩补齐";
                    else
                        text += "  ·  目标 " + local.Size.ToString("N0", CultureInfo.InvariantCulture) + " 字节，将等长补齐";
                }
                TxtNewInfo.Text = text;
            }
            catch (Exception ex)
            {
                TxtNewInfo.Text = "生成预览失败：" + ex.Message;
            }
        }

        private FitMode CurrentFitMode
        {
            get { return CboFit.SelectedIndex == 1 ? FitMode.Stretch : FitMode.Cover; }
        }

        private async void OnFitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ready) return;
            App.Settings.AdobeSplashFitMode = CboFit.SelectedIndex;
            App.Settings.Save(out string _);
            await UpdatePreviewAsync(selectionGeneration);
        }

        private async void OnChooseImage(object sender, RoutedEventArgs e)
        {
            StorageFile file = await PickerHelper.PickOpenFileAsync("选择新的启动图",
                "图片", new string[] { ".png", ".jpg", ".jpeg", ".bmp" }, null);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            try
            {
                ImagePrep.Info info = await ImagePrep.ProbeAsync(file);
                replacementFile = file;
                Log(String.Format(CultureInfo.InvariantCulture, "已选择替换图 {0}（{1}×{2}）。", file.Name, info.Width, info.Height));
            }
            catch (Exception ex)
            {
                Log("图片无法解码：" + ex.Message);
                await ShowMessage("图片无法解码", ex.Message);
                return;
            }

            await UpdatePreviewAsync(selectionGeneration);
            UpdateButtons();
        }

        private async void OnExportOriginal(object sender, RoutedEventArgs e)
        {
            if (originalBytes == null || selected == null) return;

            string suggested = SafeFileName(selected.NameName) + "_original";
            StorageFile file = await PickerHelper.PickSaveFileAsync("导出原启动图", "PNG 图片",
                new string[] { ".png" }, suggested);
            if (!String.IsNullOrEmpty(PickerHelper.LastError)) Log(PickerHelper.LastError);
            if (file == null) return;

            try
            {
                await FileIO.WriteBytesAsync(file, originalBytes);
                Log("已导出原图：" + file.Path);
            }
            catch (Exception ex)
            {
                await ShowMessage("导出失败", ex.Message);
            }
        }

        private static string SafeFileName(string name)
        {
            if (String.IsNullOrEmpty(name)) return "splash";
            char[] buffer = name.ToCharArray();
            for (int i = 0; i < buffer.Length; i++)
            {
                char c = buffer[i];
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    buffer[i] = '_';
            }
            return new string(buffer);
        }

        // ---------------- 应用 / 还原 ----------------

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (busy || selected == null || replacementFile == null) return;

            string running = FindRunningProcess();
            if (running != null)
            {
                await ShowMessage("程序正在运行", "检测到 " + running + " 正在运行。\r\n\r\n请先完全退出该程序，否则文件被占用会写入失败。");
                return;
            }

            List<SplashCandidate> group = AdobeSplashService.ExpandGroup(allCandidates, selected, ChkVariants.IsChecked == true);
            string message = String.Format(CultureInfo.InvariantCulture,
                "将修改：\r\n{0}\r\n\r\n写入 {1} 个目标（{2}）：\r\n{3}\r\n\r\n原文件会自动备份；修改后该文件的数字签名将不再有效。\r\n\r\n继续吗？",
                selected.FilePath, group.Count, selected.KindText, DescribeGroup(group));
            if (!await Confirm("确认替换启动图", message)) return;

            SetBusy(true, "正在写入…");
            StorageFile image = replacementFile;
            FitMode mode = CurrentFitMode;
            try
            {
                // 先按每个目标尺寸生成图片（WinRT 渲染必须在 UI 线程上下文中等待），再离线写入
                Dictionary<SplashCandidate, byte[]> rendered = new Dictionary<SplashCandidate, byte[]>();
                for (int i = 0; i < group.Count; i++)
                {
                    Log(String.Format(CultureInfo.InvariantCulture, "渲染 {0}×{1} {2}…",
                        group[i].Width, group[i].Height, ImageProbe.FormatText(group[i].Format)));
                    byte[] bytes = await RenderForAsync(image, group[i], mode);
                    if (bytes == null) throw new Exception("渲染 " + group[i].Width + "×" + group[i].Height + " 失败。");
                    rendered[group[i]] = bytes;
                }

                string temp = null;
                string error = null;
                SplashApplyResult result = null;
                await Task.Run(delegate
                {
                    result = AdobeSplashService.ApplyToTempAuto(group, delegate(SplashCandidate c)
                    {
                        byte[] bytes;
                        return rendered.TryGetValue(c, out bytes) ? bytes : null;
                    }, out temp, out error);
                });

                if (result == null) throw new Exception(error ?? "生成补丁失败。");

                Log("补丁已生成，原文件 " + result.OriginalSize.ToString("N0", CultureInfo.InvariantCulture)
                    + " 字节 → " + result.PatchedSize.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
                Log("备份：" + result.BackupFile);

                try
                {
                    // 容器高/低分辨率成对替换时会有多个主机文件，逐个部署
                    for (int i = 0; i < result.TempFiles.Count && i < result.HostFiles.Count; i++)
                    {
                        await DeployAsync(result.TempFiles[i], result.HostFiles[i]);
                        Log("已写入：" + result.HostFiles[i]);
                    }
                }
                finally
                {
                    for (int i = 0; i < result.TempFiles.Count; i++)
                    {
                        try { if (File.Exists(result.TempFiles[i])) File.Delete(result.TempFiles[i]); } catch { }
                    }
                }

                Log("写入完成：" + String.Join(", ", result.Replaced.ToArray()));
                HeaderInfo.Severity = InfoBarSeverity.Success;
                HeaderInfo.Title = "替换完成";
                HeaderInfo.Message = result.Detail + " 备份位于：" + result.BackupFile;
                HeaderInfo.IsOpen = true;

                await ShowMessage("替换完成",
                    "已写入 " + result.ReplacedCount + " 个目标。\r\n\r\n" +
                    "重新启动目标程序即可看到新启动图。\r\n若想恢复，点[还原备份]。");
                await ScanAsync();
            }
            catch (Exception ex)
            {
                Log("替换失败：" + ex.Message);
                HeaderInfo.Severity = InfoBarSeverity.Error;
                HeaderInfo.Title = "替换失败";
                HeaderInfo.Message = ex.Message;
                HeaderInfo.IsOpen = true;
                await ShowMessage("替换失败", ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
            }
        }

        /// <summary>
        /// 按目标的原始格式渲染。容器路线还多做一步：图片比原图大时去掉 alpha 重编码，
        /// 因为等长覆盖不允许变大。
        /// </summary>
        private static async Task<byte[]> RenderForAsync(StorageFile image, SplashCandidate candidate, FitMode mode)
        {
            OutputImageFormat format = OutputImageFormat.Png;
            if (candidate.Format == SplashImageFormat.Bmp) format = OutputImageFormat.Bmp;
            else if (candidate.Format == SplashImageFormat.Jpeg) format = OutputImageFormat.Jpeg;

            byte[] bytes = await ImagePrep.RenderAsync(image, candidate.Width, candidate.Height, mode, format);
            if (bytes == null) return null;

            if (candidate.Kind == SplashStorageKind.Embedded
                && candidate.Format == SplashImageFormat.Png
                && bytes.Length > candidate.Size)
            {
                byte[] opaque = await ImagePrep.RenderAsync(image, candidate.Width, candidate.Height, mode, format, true);
                if (opaque != null && opaque.Length < bytes.Length) bytes = opaque;
            }
            return bytes;
        }

        private static string DescribeGroup(List<SplashCandidate> group)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < group.Count; i++)
            {
                parts.Add(group[i].NameName + "（" + group[i].Width + "×" + group[i].Height + "）");
            }
            return String.Join("\r\n", parts.ToArray());
        }

        private async void OnRestore(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (selected == null)
            {
                await ShowMessage("未选择目标", "请先在列表里选中一个目标，或先扫描目标程序。");
                return;
            }

            string host = selected.FilePath;
            string backup = AdobeSplashService.FindBackup(host);
            if (backup == null)
            {
                await ShowMessage("没有备份", "该文件没有找到原始备份，无法还原。");
                return;
            }

            if (!await Confirm("确认还原", "将用原始备份覆盖：\r\n" + host + "\r\n\r\n继续吗？")) return;

            SetBusy(true, "正在还原…");
            try
            {
                await DeployAsync(backup, host);
                Log("已还原：" + host);
                await ShowMessage("还原完成", "已恢复原始文件。");
                await ScanAsync();
            }
            catch (Exception ex)
            {
                Log("还原失败：" + ex.Message);
                await ShowMessage("还原失败", ex.Message);
            }
            finally
            {
                SetBusy(false, "就绪");
            }
        }

        private void OnOpenBackup(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + AdobeSplashService.BackupDirectory + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("打开目录失败：" + ex.Message);
            }
        }

        /// <summary>覆盖目标文件；权限不足时自动走提权通道。</summary>
        private async Task DeployAsync(string sourceFile, string destination)
        {
            try
            {
                string staged = destination + ".deploy.tmp";
                File.Copy(sourceFile, staged, true);
                File.Move(staged, destination, true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                Log("需要管理员权限，正在请求提权…");
            }
            catch (IOException)
            {
                Log("目标文件被占用或不可写，尝试提权…");
            }

            string elevateError = null;
            bool ok = await Task.Run(delegate
            {
                string e;
                bool r = ElevationService.TryElevate("copyFile", sourceFile, destination, out e);
                elevateError = e;
                return r;
            });

            if (!ok)
            {
                throw new Exception(String.IsNullOrEmpty(elevateError)
                    ? "写入失败。目标位于受保护目录，请以管理员身份运行本工具后重试。"
                    : elevateError);
            }
        }

        private string FindRunningProcess()
        {
            for (int i = 0; i < RunningProcesses.Length; i++)
            {
                if (Patcher.IsProcessRunning(RunningProcesses[i])) return RunningProcesses[i];
            }
            return null;
        }

        // ---------------- 辅助 ----------------

        private void UpdateBackupHint()
        {
            if (selected == null)
            {
                TxtBackup.Text = "";
                return;
            }
            string backup = AdobeSplashService.FindBackup(selected.FilePath);
            TxtBackup.Text = backup == null
                ? "该文件还没有备份，首次替换时会自动创建。"
                : "已有备份：" + backup;
        }

        private void SetBusy(bool value, string status)
        {
            busy = value;
            Busy.IsActive = value;
            if (MainWindow.Instance != null) MainWindow.Instance.Status(status);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (!ready) return;
            bool hasTarget = !String.IsNullOrEmpty(TargetPath);
            bool hasCandidate = selected != null;
            bool hasImage = replacementFile != null;

            BtnScan.IsEnabled = !busy && hasTarget;
            BtnBrowse.IsEnabled = !busy;
            BtnPickFolder.IsEnabled = !busy;
            BtnRefresh.IsEnabled = !busy && !loadingApps;
            BtnChooseImage.IsEnabled = !busy && hasCandidate;
            BtnExportOriginal.IsEnabled = !busy && originalBytes != null;
            BtnApply.IsEnabled = !busy && hasCandidate && hasImage;
            BtnRestore.IsEnabled = !busy && hasCandidate && AdobeSplashService.FindBackup(selected.FilePath) != null;
            BtnOpenBackup.IsEnabled = !busy;
            ChkShowAll.IsEnabled = !busy;
            ChkVariants.IsEnabled = !busy;

            if (hasCandidate)
            {
                List<SplashCandidate> group = AdobeSplashService.ExpandGroup(allCandidates, selected, ChkVariants.IsChecked == true);
                TxtSelection.Text = "将写入 " + group.Count + " 个目标 · 选择器：" + PickerHelper.ModeText;
            }
            else
            {
                TxtSelection.Text = "";
            }
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + "\r\n";
            TxtLog.Text = (TxtLog.Text ?? String.Empty) + line;
        }

        private static async Task<BitmapImage> ToBitmapAsync(byte[] bytes)
        {
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                BitmapImage image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
        }

        private async Task<bool> Confirm(string title, string message)
        {
            ContentDialog dialog = new ContentDialog();
            dialog.XamlRoot = XamlRoot;
            dialog.Title = title;
            dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            dialog.PrimaryButtonText = "继续";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Close;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task ShowMessage(string title, string message)
        {
            ContentDialog dialog = new ContentDialog();
            dialog.XamlRoot = XamlRoot;
            dialog.Title = title;
            dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            dialog.CloseButtonText = "确定";
            await dialog.ShowAsync();
        }
    }
}
